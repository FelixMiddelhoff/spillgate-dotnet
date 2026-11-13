using System;

namespace Spillgate.TestSupport;

/// <summary>LSB-first bit packing as DEFLATE and brotli use it, with a cheap way to append long runs of zero bits.</summary>
public sealed class BitWriter
{
    private byte[] _out = new byte[64];
    private int _len;
    private ulong _acc;
    private int _count;

    public void Bits(long value, int n)
    {
        _acc |= (ulong)value << _count;
        _count += n;
        while (_count >= 8)
        {
            Push((byte)(_acc & 0xff));
            _acc >>= 8;
            _count -= 8;
        }
    }

    /// <summary>A Huffman code: most significant bit first.</summary>
    public void Code(long code, int n)
    {
        long reversed = 0;
        for (int i = 0; i < n; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        Bits(reversed, n);
    }

    public void ZeroBits(long n)
    {
        long head = Math.Min(n, (8 - _count) % 8);
        Bits(0, (int)head);
        long rest = n - head;
        if (_count == 0)
        {
            long bytes = rest / 8;
            long total = checked(_len + bytes);
            if (total > int.MaxValue - 64)
            {
                throw new InvalidOperationException("stream too large");
            }

            if (total > _out.Length)
            {
                Array.Resize(ref _out, (int)Math.Max(total, Math.Min((long)_out.Length * 2, int.MaxValue - 64)));
            }

            // Bytes past _len were never written, so the new region is zero.
            Array.Clear(_out, _len, (int)bytes);
            _len = (int)total;
            Bits(0, (int)(rest % 8));
        }
    }

    public void Align()
    {
        if (_count > 0)
        {
            Push((byte)(_acc & 0xff));
            _acc = 0;
            _count = 0;
        }
    }

    public void Raw(byte[] bytes, int off, int n)
    {
        for (int i = 0; i < n; i++)
        {
            Push(bytes[off + i]);
        }
    }

    public byte[] Finish()
    {
        Align();
        return _out.AsSpan(0, _len).ToArray();
    }

    private void Push(byte b)
    {
        if (_len == _out.Length)
        {
            Array.Resize(ref _out, checked(_out.Length * 2));
        }

        _out[_len++] = b;
    }
}
