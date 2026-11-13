using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Spillgate;

/// <summary>
/// The input meter: counts what a codec takes and guards the source.
/// <list type="bullet">
/// <item>It counts the bytes the codec actually consumes, not the bytes fetched, so read-ahead buffers give no
/// free ratio credit (SG-R-RAT-02).</item>
/// <item>It fetches at most <see cref="BufferSize"/> bytes at a time, far below the 64 KiB tolerance.</item>
/// <item>With MaxInput it never requests more than MaxInput + 1 bytes in total (SG-R-INP-01, SG-R-INP-02). The
/// probe byte (number MaxInput + 1) is shown to the codec, but never together with earlier bytes, so a codec
/// that takes its whole window is not charged for a byte it did not need; showing is looking, only consuming
/// counts, and the layer guard checks that after every codec step.</item>
/// <item>It turns a misbehaving source into a typed failure and remembers it: a source that throws is
/// <see cref="ErrorKind.SourceError"/>, a count outside the buffer is <see cref="ErrorKind.Internal"/> (SG-R-RDR-04),
/// a <see cref="SpillgateException"/> from a lower layer passes through, cancellation is sticky.</item>
/// <item>It checks the cancellation token on every fetch (SG-R-API-04) and never reads again after a failure.</item>
/// </list>
/// Not thread safe (SG-R-RDR-07).
/// </summary>
internal sealed class InputMeter
{
    internal const int BufferSize = 16 * 1024;

    private readonly long _maxInput;
    private readonly int _layer;
    private readonly CancellationToken _cancel;
    private IByteSource? _source;
    private byte[]? _buffer;
    private int _pos;
    private int _filled;
    private long _fetched;
    private long _consumed;
    private bool _ended;
    private Exception? _failure;

    /// <summary>Creates a meter; <paramref name="maxInput"/> 0 means no cap.</summary>
    internal InputMeter(IByteSource source, long maxInput, int layer, CancellationToken cancel)
    {
        _source = source;
        _maxInput = maxInput;
        _layer = layer;
        _cancel = cancel;
    }

    /// <summary>Gets the bytes the codec has consumed so far (the C(k) of the specification).</summary>
    internal long Consumed => _consumed;

    /// <summary>Gets the terminal failure this meter raised, if any.</summary>
    internal Exception? Failure => _failure;

    /// <summary>Gets a value indicating whether the buffer is empty and the source has ended (the codec's "input ended").</summary>
    internal bool IsEnded => _ended && _pos >= _filled;

    /// <summary>Gets a value indicating whether bytes are buffered (the probe byte included).</summary>
    internal bool HasBuffered => _pos < _filled;

    /// <summary>Gets a value indicating whether the buffer is empty and the source may still have bytes.</summary>
    internal bool NeedsFill => _pos >= _filled && !_ended && FetchSize() > 0;

    internal void Release()
    {
        _source = null;
        _buffer = null;
        _pos = 0;
        _filled = 0;
    }

    /// <summary>The bytes the codec may look at now.</summary>
    internal ReadOnlySpan<byte> Window()
    {
        if (_buffer is null || _pos >= _filled)
        {
            return default;
        }

        int end = _filled;
        if (_maxInput > 0)
        {
            long allowance = _maxInput - _consumed;
            if (allowance > 0 && allowance < end - _pos)
            {
                end = _pos + (int)allowance;
            }
        }

        return new ReadOnlySpan<byte>(_buffer, _pos, end - _pos);
    }

    internal void Consume(int n, int shown)
    {
        if (n < 0 || n > shown)
        {
            throw SpillgateException.Internal("a codec consumed more input than it was shown");
        }

        _pos += n;
        _consumed += n;
    }

    /// <summary>Counts bytes a failed step had taken (see <see cref="IFailureAccounting"/>), within what it was shown.</summary>
    internal void ConsumeOnFailure(int n, int shown) => Consume(Math.Clamp(n, 0, shown), shown);

    /// <summary>End-of-step check: a codec that took the MaxInput+1-th byte violated SG-R-INP-01, whatever it returned.</summary>
    internal SpillgateException? CheckTaken()
    {
        if (_maxInput > 0 && _consumed > _maxInput && _failure is null)
        {
            var e = new SpillgateException(
                ErrorKind.InputLimit,
                "the stream needs more than " + _maxInput.ToString(CultureInfo.InvariantCulture) + " input bytes",
                _layer,
                _maxInput,
                null);
            _failure = e;
            return e;
        }

        return null;
    }

    internal void ThrowIfFailed()
    {
        if (_failure is not null)
        {
            Rethrow(_failure);
        }
    }

    /// <summary>Fetches the next chunk. The buffer must be empty.</summary>
    internal void Fill()
    {
        BeginFill();
        if (_ended)
        {
            return;
        }

        int want = FetchSize();
        if (want == 0)
        {
            return;
        }

        IByteSource source = _source ?? throw FailInternal("the input was released");
        _buffer ??= new byte[BufferSize];
        int n;
        try
        {
            n = source.Read(_buffer.AsSpan(0, want));
        }
        catch (Exception e)
        {
            throw Fail(e);
        }

        EndFill(n, want);
    }

    /// <summary>Asynchronous <see cref="Fill"/>.</summary>
    internal async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        BeginFill();
        if (_ended)
        {
            return;
        }

        int want = FetchSize();
        if (want == 0)
        {
            return;
        }

        IByteSource source = _source ?? throw FailInternal("the input was released");
        _buffer ??= new byte[BufferSize];
        int n;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            n = await source.ReadAsync(_buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw Fail(e);
        }

        EndFill(n, want);
    }

    /// <summary>Peeks whether any byte follows (fetching one chunk if needed). Looks, never takes.</summary>
    internal bool HasMore()
    {
        if (_pos < _filled)
        {
            return true;
        }

        if (_ended || FetchSize() == 0)
        {
            return false;
        }

        Fill();
        return _pos < _filled;
    }

    /// <summary>Asynchronous <see cref="HasMore"/>.</summary>
    internal async ValueTask<bool> HasMoreAsync(CancellationToken cancellationToken)
    {
        if (_pos < _filled)
        {
            return true;
        }

        if (_ended || FetchSize() == 0)
        {
            return false;
        }

        await FillAsync(cancellationToken).ConfigureAwait(false);
        return _pos < _filled;
    }

    private static void Rethrow(Exception failure)
    {
        if (failure is OperationCanceledException canceled)
        {
            throw new OperationCanceledException(canceled.Message, canceled, canceled.CancellationToken);
        }

        throw ((SpillgateException)failure).Repeat();
    }

    /// <summary>How much to ask the source for: with a cap never beyond MaxInput + 1 bytes in total.</summary>
    private int FetchSize()
    {
        if (_maxInput == 0)
        {
            return BufferSize;
        }

        long room = _maxInput - _fetched;
        if (room < 0)
        {
            return 0;
        }

        return room >= BufferSize ? BufferSize : (int)(room + 1);
    }

    private void BeginFill()
    {
        ThrowIfFailed();
        try
        {
            _cancel.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException e)
        {
            throw Fail(e);
        }

        _pos = 0;
        _filled = 0;
    }

    private void EndFill(int n, int want)
    {
        if (n == 0)
        {
            // Stream contract: zero bytes for a non-empty request is the end. There is no "no progress" state.
            _ended = true;
            return;
        }

        if (n < 0 || n > want)
        {
            throw FailInternal("the source returned an invalid byte count (not in 0.." + want.ToString(CultureInfo.InvariantCulture) + ")");
        }

        _filled = n;
        _fetched += n;
    }

    private Exception FailInternal(string message) =>
        Fail(new SpillgateException(ErrorKind.Internal, message, _layer, 0, null));

    private Exception Fail(Exception e)
    {
        Exception named;
        if (e is OperationCanceledException)
        {
            named = e;
        }
        else if (e is SpillgateException s)
        {
            named = s.WithLayer(_layer);
        }
        else
        {
            named = new SpillgateException(ErrorKind.SourceError, "reading the source failed", _layer, 0, e);
        }

        _failure ??= named;
        return named is SpillgateException sp ? sp.Repeat() : named;
    }
}
