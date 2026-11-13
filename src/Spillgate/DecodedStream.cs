using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spillgate;

/// <summary>
/// The decoded bytes of a chain as a forward-only <see cref="Stream"/>. A read returns 0 only for a zero-length
/// request or at the clean end of a validated stream; a limit hit, corruption or a source failure throws a
/// <see cref="SpillgateException"/> and the failure is sticky (SG-R-LAY-05). In streaming mode bytes may be
/// delivered before a later error: treat any error as failure of the whole body (SG-R-LAY-06).
/// Reads are not safe for concurrent use; <see cref="Stream.Dispose()"/> may be called from another thread.
/// </summary>
public sealed class DecodedStream : Stream
{
    private readonly LayerGuard[] _guards;
    private readonly LayerGuard _top;
    private readonly InputMeter _sourceMeter;
    private readonly EffectiveLimits _limits;
    private readonly CancellationToken _cancel;
    private readonly Stream? _ownedSource;
    private long _produced;
    private volatile int _state;
    private volatile Exception? _error;
    private int _disposed;

    internal DecodedStream(
        LayerGuard[] guards, InputMeter sourceMeter, EffectiveLimits limits, Stream? ownedSource, CancellationToken cancel)
    {
        _guards = guards;
        _top = guards[guards.Length - 1];
        _sourceMeter = sourceMeter;
        _limits = limits;
        _cancel = cancel;
        _ownedSource = ownedSource;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(new Span<byte>(buffer, offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        try
        {
            return ReadCore(buffer);
        }
        catch (Exception) when (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(DecodedStream));
        }
    }

    /// <inheritdoc />
    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            return await ReadCoreAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(DecodedStream));
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseAll();
            _ownedSource?.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ReadCore(Span<byte> buffer)
    {
        if (!Precheck(buffer.Length, CancellationToken.None))
        {
            return 0;
        }

        int n;
        try
        {
            n = _top.ReadStep(buffer.Slice(0, Math.Min(buffer.Length, LayerGuard.MaxStep)));
        }
        catch (Exception e)
        {
            throw Fail(e);
        }

        return AfterStep(n);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!Precheck(buffer.Length, cancellationToken))
        {
            return 0;
        }

        int n;
        try
        {
            n = await _top.ReadStepAsync(buffer.Slice(0, Math.Min(buffer.Length, LayerGuard.MaxStep)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw Fail(e);
        }

        return AfterStep(n);
    }

    /// <summary>State checks shared by both read paths. False means "return 0".</summary>
    private bool Precheck(int length, CancellationToken callToken)
    {
        if (_state == 2)
        {
            throw Sticky();
        }

        if (_state == 1 || length == 0)
        {
            return false; // a clean end is sticky; a zero-length read changes nothing (SG-R-RDR-02)
        }

        if (_cancel.IsCancellationRequested)
        {
            throw Fail(new OperationCanceledException(_cancel));
        }

        callToken.ThrowIfCancellationRequested(); // before any work: no state is touched
        return true;
    }

    private int AfterStep(int n)
    {
        if (n == 0)
        {
            _state = 1;
            ReleaseAll();
            return 0;
        }

        _produced += n;

        // SG-R-RAT-03: the same ratio test, end to end. The error follows these bytes. A layer that
        // already failed has the more specific error, so it wins.
        if (!_top.Failed && _limits.RatioExceeded(_produced, _sourceMeter.Consumed))
        {
            Fail(new SpillgateException(
                ErrorKind.RatioLimit, "end-to-end expansion over the whole chain", 0, _limits.MaxRatio, null));
        }

        return n;
    }

    private Exception Sticky() => _error switch
    {
        SpillgateException s => s.Repeat(),
        OperationCanceledException c => new OperationCanceledException(c.Message, c, c.CancellationToken),
        _ => SpillgateException.Internal("the stream failed"),
    };

    private Exception Fail(Exception e)
    {
        if (_state != 2)
        {
            _error = e;
            _state = 2;
        }

        ReleaseAll();
        return e switch
        {
            SpillgateException s => s.Repeat(),
            OperationCanceledException c => new OperationCanceledException(c.Message, c, c.CancellationToken),
            _ => new SpillgateException(ErrorKind.Internal, "unexpected failure: " + e.GetType().Name, 0, 0, e),
        };
    }

    private void ReleaseAll()
    {
        foreach (LayerGuard guard in _guards)
        {
            guard.Close();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
