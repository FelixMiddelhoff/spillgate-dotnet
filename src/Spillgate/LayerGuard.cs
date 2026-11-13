using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Spillgate;

/// <summary>
/// The layer guard: everything the specification demands of one layer. Whatever the codec does, the guard never
/// hands out more than MaxOutput bytes in total (SG-I-01), never turns a limit hit into a clean end (SG-I-02),
/// keeps failing with the same error once one occurred (SG-I-03) and contains the failures of the codec
/// (SG-I-05, SG-R-CDC-08). The codec writes into a span that the guard sizes, so it cannot write past what was
/// allowed. A guard is also the byte source of the next layer's <see cref="InputMeter"/>.
/// </summary>
internal sealed class LayerGuard : IByteSource
{
    /// <summary>
    /// The most a guard asks a codec for in one step (64 KiB). It bounds the memory of one step (SG-R-MEM-02),
    /// whatever the size of the caller's buffer, and it is the tolerance of SG-R-RAT-02: the ratio is judged once per step.
    /// </summary>
    internal const int MaxStep = 64 * 1024;

    /// <summary>SG-R-RDR-03 in the push model: this many codec steps in a row without progress are "no progress".</summary>
    internal const int MaxEmptySteps = 100;

    private readonly InputMeter _meter;
    private readonly int _layer;
    private readonly EffectiveLimits _limits;
    private ICodecDecoder? _codec;
    private long _produced;
    private Phase _phase = Phase.Running;
    private Exception? _error;
    private bool _codecDone;

    internal LayerGuard(ICodecDecoder codec, InputMeter meter, int layer, EffectiveLimits limits)
    {
        _codec = codec;
        _meter = meter;
        _layer = layer;
        _limits = limits;
    }

    private enum Phase : byte
    {
        Running,
        End,
        Failed,
    }

    private enum Attempt : byte
    {
        Produced,
        Ended,
        NeedFill,
        Retry,
    }

    /// <summary>Gets a value indicating whether the layer has a terminal error (a clean end is not a failure).</summary>
    internal bool Failed => _phase == Phase.Failed;

    internal int ReadStep(Span<byte> destination)
    {
        if (!Begin(destination.Length, out int step, out bool probing))
        {
            return 0;
        }

        Span<byte> one = stackalloc byte[1];
        Span<byte> target = probing ? one : destination.Slice(0, step);
        for (int empty = 0; ;)
        {
            Attempt attempt = TryStep(target, ref empty, out int written);
            switch (attempt)
            {
                case Attempt.Produced:
                    return Finish(written, probing);
                case Attempt.Ended:
                    return 0;
                case Attempt.NeedFill:
                    FillOrFail();
                    break;
                default:
                    break;
            }
        }
    }

    internal async ValueTask<int> ReadStepAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (!Begin(destination.Length, out int step, out bool probing))
        {
            return 0;
        }

        byte[]? probeBuffer = probing ? new byte[1] : null;
        for (int empty = 0; ;)
        {
            Attempt attempt;
            int written;
            if (probing)
            {
                attempt = TryStep(probeBuffer.AsSpan(), ref empty, out written);
            }
            else
            {
                attempt = TryStep(destination.Span.Slice(0, step), ref empty, out written);
            }

            switch (attempt)
            {
                case Attempt.Produced:
                    return Finish(written, probing);
                case Attempt.Ended:
                    return 0;
                case Attempt.NeedFill:
                    await FillOrFailAsync(cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }
    }

    /// <inheritdoc />
    int IByteSource.Read(Span<byte> buffer) => ReadStep(buffer);

    /// <inheritdoc />
    ValueTask<int> IByteSource.ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        ReadStepAsync(buffer, cancellationToken);

    /// <summary>Releases the codec. A running layer becomes terminal (it was closed under a reader).</summary>
    internal void Close()
    {
        if (_phase == Phase.Running)
        {
            _error = new SpillgateException(ErrorKind.Internal, "the layer is closed", _layer, 0, null);
            _phase = Phase.Failed;
        }

        Release();
    }

    private SpillgateException FailWith(SpillgateException e)
    {
        SpillgateException named = e.WithLayer(_layer);
        if (_phase == Phase.Running)
        {
            _error = named;
            _phase = Phase.Failed;
        }

        Release();
        return _phase == Phase.Failed && _error is SpillgateException stored ? stored.Repeat() : named;
    }

    private Exception FailWithAny(Exception e)
    {
        if (e is SpillgateException s)
        {
            return FailWith(s);
        }

        if (_phase == Phase.Running)
        {
            _error = e;
            _phase = Phase.Failed;
        }

        Release();
        return e is OperationCanceledException c
            ? new OperationCanceledException(c.Message, c, c.CancellationToken)
            : e;
    }

    private void End()
    {
        if (_phase == Phase.Running)
        {
            _phase = Phase.End;
        }

        Release();
    }

    private void Release()
    {
        ICodecDecoder? codec = _codec;
        _codec = null;
        _meter.Release();
        if (codec is not null)
        {
            try
            {
                codec.Dispose();
            }
            catch (Exception)
            {
                // A Dispose that throws is a bug of the codec; the layer is released anyway.
            }
        }
    }

    /// <summary>Common start of a read: state checks and the size of this step. False means "return 0".</summary>
    private bool Begin(int length, out int step, out bool probing)
    {
        step = 0;
        probing = false;
        if (_phase == Phase.Failed)
        {
            throw Sticky();
        }

        if (length == 0 || _phase == Phase.End)
        {
            return false; // SG-R-RDR-02: a zero-length read changes nothing; a clean end is sticky
        }

        long remaining = _limits.MaxOutput - _produced;
        if (remaining <= 0)
        {
            probing = true; // SG-R-LAY-03: is there one more byte?
            return true;
        }

        step = (int)Math.Min(Math.Min(length, remaining), MaxStep);
        return true;
    }

    private Exception Sticky() => _error switch
    {
        SpillgateException s => s.Repeat(),
        OperationCanceledException c => new OperationCanceledException(c.Message, c, c.CancellationToken),
        _ => SpillgateException.Internal("the layer failed"),
    };

    private int Finish(int written, bool probing)
    {
        if (probing)
        {
            // The budget was used up and the codec yielded one more byte: the limit is exceeded, the byte is dropped.
            throw FailWith(new SpillgateException(
                ErrorKind.OutputLimit,
                "the layer would produce more output than allowed",
                _layer,
                _limits.MaxOutput,
                null));
        }

        _produced += written;

        // The bytes that broke the ratio were produced first, so they are still delivered;
        // the error follows on the next read (SG-R-LAY-07).
        if (_limits.RatioExceeded(_produced, _meter.Consumed))
        {
            FailWith(new SpillgateException(
                ErrorKind.RatioLimit,
                "output of " + _produced.ToString(CultureInfo.InvariantCulture) + " bytes from "
                    + _meter.Consumed.ToString(CultureInfo.InvariantCulture) + " input bytes",
                _layer,
                _limits.MaxRatio,
                null));
        }

        return written;
    }

    /// <summary>
    /// One attempt: give the codec what is buffered and see what it does. Never blocks; asks the caller to
    /// fill the buffer when the codec needs more input than is buffered.
    /// </summary>
    private Attempt TryStep(Span<byte> output, ref int empty, out int written)
    {
        written = 0;
        ICodecDecoder codec = _codec ?? throw Sticky();

        if (_codecDone)
        {
            return TrailingCheck();
        }

        ReadOnlySpan<byte> window = _meter.Window();
        int shown = window.Length;
        bool inputEnded = _meter.IsEnded;
        CodecStatus status = CodecStatus.NeedInput;
        int consumed = 0;
        int produced = 0;
        Exception? problem = null;
        try
        {
            status = codec.Decode(window, inputEnded, output, out consumed, out produced);
        }
        catch (SpillgateException e)
        {
            problem = e;
        }
        catch (OperationCanceledException e)
        {
            problem = e;
        }
        catch (OutOfMemoryException e)
        {
            problem = new SpillgateException(ErrorKind.MemoryLimit, "the codec ran out of memory", _layer, 0, e);
        }
        catch (Exception e)
        {
            // Never touch the message or ToString() of a hostile exception.
            problem = new SpillgateException(
                ErrorKind.Internal, "codec threw " + e.GetType().Name, _layer, 0, e);
        }

        if (problem is not null)
        {
            int taken = codec is IFailureAccounting accounting ? accounting.ConsumedBeforeFailure : 0;
            ConsumeFailed(taken, shown);
        }
        else if (consumed < 0 || consumed > shown || produced < 0 || produced > output.Length)
        {
            problem = new SpillgateException(
                ErrorKind.Internal, "codec reported an invalid byte count", _layer, 0, null);
        }
        else
        {
            _meter.Consume(consumed, shown);
        }

        // End of the codec step: a codec that took the MaxInput+1-th byte violated SG-R-INP-01, whatever it
        // returned in this step (data, the end, or corruption). The probe is only looked at until consumed.
        SpillgateException? limit = _meter.CheckTaken();
        if (limit is not null)
        {
            throw FailWith(limit);
        }

        if (problem is not null)
        {
            // A source failure is reported as such, whatever the codec made of it (SG-R-RDR-05).
            throw FailWithAny(_meter.Failure ?? problem);
        }

        written = produced;
        if (produced > 0)
        {
            return Attempt.Produced;
        }

        if (status == CodecStatus.Done)
        {
            _codecDone = true;
            return TrailingCheck();
        }

        if (status == CodecStatus.NeedInput && consumed == shown && !_meter.HasBuffered)
        {
            if (inputEnded || !_meter.NeedsFill)
            {
                throw FailWith(new SpillgateException(
                    ErrorKind.Internal, "codec asked for input that does not exist", _layer, 0, null));
            }

            empty = 0;
            return Attempt.NeedFill;
        }

        if (consumed > 0)
        {
            empty = 0;
        }
        else if (++empty >= MaxEmptySteps)
        {
            throw FailWith(new SpillgateException(ErrorKind.Internal, "codec made no progress", _layer, 0, null));
        }

        return Attempt.Retry;
    }

    private void ConsumeFailed(int taken, int shown)
    {
        try
        {
            _meter.ConsumeOnFailure(taken, shown);
        }
        catch (SpillgateException)
        {
            // Bookkeeping of a failing step must not mask the failure itself.
        }
    }

    /// <summary>
    /// After the codec's clean end: data behind it is corrupt (looked at, not taken), else the layer ends.
    /// When nothing is buffered the caller is asked to fetch one more chunk.
    /// </summary>
    private Attempt TrailingCheck()
    {
        if (_meter.HasBuffered)
        {
            throw FailWith(SpillgateException.Corrupt("unexpected data after the end of the stream"));
        }

        if (_meter.IsEnded || !_meter.NeedsFill)
        {
            End();
            return Attempt.Ended;
        }

        return Attempt.NeedFill;
    }

    private void FillOrFail()
    {
        try
        {
            _meter.Fill();
        }
        catch (Exception e)
        {
            throw FailWithAny(e);
        }
    }

    private async ValueTask FillOrFailAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _meter.FillAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw FailWithAny(e);
        }
    }
}
