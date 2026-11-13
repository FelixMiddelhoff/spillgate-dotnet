using System;
using System.Numerics;

namespace Spillgate.Zstd;

/// <summary>Bit readers over a byte range, forward (FSE headers) and backward (FSE, Huffman and sequence streams).</summary>
internal static class BitStreams
{
    /// <summary>Reads n (at most 32) bits at bit position pos of a[from..to); bytes past the end read as zero.</summary>
    internal static long BitsAt(byte[] a, int from, int to, long pos, int n)
    {
        if (n == 0)
        {
            return 0;
        }

        int shift = (int)(pos & 7);
        long first = (pos >> 3) + from;
        int count = ((shift + n - 1) >> 3) + 1; // at most 5 bytes: 40 bits
        long value = 0;
        for (int k = 0; k < count; k++)
        {
            long i = first + k;
            if (i < to)
            {
                value |= (long)a[(int)i] << (8 * k);
            }
        }

        return (value >> shift) & ((1L << n) - 1);
    }

    internal sealed class Forward
    {
        private readonly byte[] _a;
        private readonly int _from;
        private readonly int _to;
        private long _pos;

        internal Forward(byte[] a, int from, int to)
        {
            _a = a;
            _from = from;
            _to = to;
        }

        internal int Read(int n)
        {
            long value = BitsAt(_a, _from, _to, _pos, n);
            _pos += n;
            return (int)value;
        }

        internal void Unread(int n) => _pos = Math.Max(0, _pos - n);

        internal int BytesUsed() => (int)Math.Min(int.MaxValue, (_pos + 7) >> 3);

        internal bool Overrun() => _pos > (long)(_to - _from) * 8;
    }

    internal sealed class Backward
    {
        private readonly byte[] _a;
        private readonly int _from;
        private readonly int _to;

        internal Backward(byte[] a, int from, int to)
        {
            if (from < 0 || to > a.Length || to <= from || a[to - 1] == 0)
            {
                throw Err.Corrupt("a bit stream ends without its end mark");
            }

            _a = a;
            _from = from;
            _to = to;
            int last = a[to - 1];
            int leading = BitOperations.LeadingZeroCount((uint)last) - 24; // within 8 bits
            Remaining = ((long)(to - from - 1) * 8) + (7 - leading);
        }

        /// <summary>Gets or sets the number of bits not yet read. May go negative on a corrupt stream.</summary>
        internal long Remaining { get; set; }

        internal long Read(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            long pos = Remaining - n;
            Remaining = pos;
            return At(pos, n);
        }

        internal long Peek(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            return At(Remaining - n, n);
        }

        private long At(long pos, int n)
        {
            if (pos >= 0)
            {
                return BitsAt(_a, _from, _to, pos, n);
            }

            // Some of the bits lie before the start: they read as zeros at the low end.
            long missing = -pos;
            if (missing >= n)
            {
                return 0;
            }

            return BitsAt(_a, _from, _to, 0, n - (int)missing) << (int)missing;
        }
    }
}
