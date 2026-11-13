using System;

namespace Spillgate.Zstd;

/// <summary>
/// The decoder window: a ring of the last <c>windowSize</c> bytes. It grows with the bytes that were really
/// produced (never from the declared size) up to the size the frame header announced and the cap allowed.
/// </summary>
internal sealed class Window
{
    private readonly int _ring;
    private byte[] _buf = Array.Empty<byte>();
    private int _pos;

    internal Window(int windowSize)
    {
        _ring = Math.Max(windowSize, 1);
    }

    internal long Total { get; private set; }

    internal int Capacity => _buf.Length;

    internal void Push(byte[] bytes, int off, int len)
    {
        if (len == 0)
        {
            return;
        }

        Ensure(len);
        int n = _buf.Length;
        if (len >= n && n == _ring)
        {
            // The newest n bytes fill the whole ring.
            Buffer.BlockCopy(bytes, off + len - n, _buf, 0, n);
            _pos = 0;
        }
        else
        {
            int start = _pos;
            if (start > n)
            {
                throw Err.Internal("the window ring is inconsistent");
            }

            int first = Math.Min(len, n - start);
            Buffer.BlockCopy(bytes, off, _buf, start, first);
            int rest = len - first;
            if (rest > n)
            {
                throw Err.Internal("the window ring is inconsistent");
            }

            Buffer.BlockCopy(bytes, off + first, _buf, 0, rest);
            _pos = (int)(((long)start + len) % _ring);
        }

        Total += len;
    }

    /// <summary>Copies n bytes that lie d bytes before the end of the history into output at outPos.</summary>
    internal void CopyHistory(int d, int n, byte[] output, int outPos)
    {
        long idxLong = d <= _pos ? (long)_pos - d : (long)_pos + _ring - d;
        if (idxLong < 0 || idxLong > _buf.Length)
        {
            throw Err.Internal("the window ring is inconsistent");
        }

        int idx = (int)idxLong;
        int first = Math.Min(n, _buf.Length - idx);
        Buffer.BlockCopy(_buf, idx, output, outPos, first);
        int rest = n - first;
        if (rest > 0)
        {
            if (rest > _buf.Length)
            {
                throw Err.Internal("the window ring is inconsistent");
            }

            Buffer.BlockCopy(_buf, 0, output, outPos + first, rest);
        }
    }

    private void Ensure(int extra)
    {
        long wanted = Math.Min(Total + extra, _ring);
        int target = (int)Math.Max(wanted, 1);
        int len = _buf.Length;
        if (len >= target)
        {
            return;
        }

        int grown = (int)Math.Min(Math.Max((long)target, (long)len * 2), _ring);
        try
        {
            var b = new byte[grown];
            Buffer.BlockCopy(_buf, 0, b, 0, len);
            _buf = b;
        }
        catch (OutOfMemoryException)
        {
            _buf = Array.Empty<byte>();
            throw Err.Memory("the window could not be allocated", _ring);
        }
    }
}
