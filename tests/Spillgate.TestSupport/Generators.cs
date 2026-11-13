using System;
using System.Security.Cryptography;
using System.Text.Json;

namespace Spillgate.TestSupport;

/// <summary>
/// The conformance corpus generators (spec/generators.md) and the stream builders the tests use. Nothing here
/// calls a compression library, so the streams are reproducible byte for byte.
/// </summary>
public static class Generators
{
    public const long BrotliMetablockMax = 1L << 24;
    public const long ZstdBlockMax = 128 * 1024;

    private const long MaxMatch = 258;
    private const int StoredBlockLimit = 65535;
    private const long BrotliMinCommandBytes = 4 + 2118;
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>xorshift64 pseudo-random bytes that do not compress.</summary>
    public static byte[] Data(long seed, int size)
    {
        unchecked
        {
            ulong state = ((ulong)seed * 0x9e3779b97f4a7c15UL) + 1;
            var output = new byte[size];
            for (int i = 0; i < size; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                output[i] = (byte)(state >> 24);
            }

            return output;
        }
    }

    public static byte[] Concat(params byte[][] parts)
    {
        long total = 0;
        foreach (byte[] p in parts)
        {
            total += p.Length;
        }

        var output = new byte[checked((int)total)];
        int pos = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, output, pos, p.Length);
            pos += p.Length;
        }

        return output;
    }

    public static byte[] StoredDeflate(byte[] payload)
    {
        int blocks = Math.Max(1, (payload.Length + StoredBlockLimit - 1) / StoredBlockLimit);
        var output = new byte[payload.Length + (5 * blocks)];
        int pos = 0;
        int o = 0;
        for (; ; )
        {
            int n = Math.Min(payload.Length - pos, StoredBlockLimit);
            bool last = pos + n == payload.Length;
            output[o++] = (byte)(last ? 1 : 0);
            output[o] = (byte)n;
            output[o + 1] = (byte)(n >> 8);
            output[o + 2] = (byte)(~n & 0xff);
            output[o + 3] = (byte)((~n >> 8) & 0xff);
            o += 4;
            Buffer.BlockCopy(payload, pos, output, o, n);
            o += n;
            pos += n;
            if (pos >= payload.Length)
            {
                return output;
            }
        }
    }

    /// <summary>One dynamic block that decodes to 1 + 258 * matches zero bytes at about 1032:1.</summary>
    public static byte[] ZerosDeflate(long matches)
    {
        var w = new BitWriter();
        w.Bits(1, 1); // BFINAL
        w.Bits(2, 2); // BTYPE dynamic
        w.Bits(29, 5); // HLIT
        w.Bits(1, 5); // HDIST
        w.Bits(14, 4); // HCLEN
        foreach (int length in new[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2 })
        {
            w.Bits(length, 3);
        }

        w.Code(0b11, 2); // literal 0: length 2
        ZeroRun(w, 138);
        ZeroRun(w, 117);
        w.Code(0b11, 2); // symbol 256
        ZeroRun(w, 28);
        w.Code(0b10, 2); // symbol 285
        w.Code(0b10, 2); // distance symbol 0
        w.Code(0b10, 2); // distance symbol 1
        w.Code(0b10, 2); // literal 0
        w.ZeroBits(checked(2 * matches));
        w.Code(0b11, 2); // end of block
        return w.Finish();
    }

    public static long ZerosDecodedSize(long matches) => 1 + (MaxMatch * matches);

    public static uint Crc32(byte[] payload)
    {
        uint crc = ~0u;
        foreach (byte b in payload)
        {
            crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        }

        return ~crc;
    }

    /// <summary>CRC-32 of n zero bytes without touching them (GF(2) polynomial arithmetic).</summary>
    public static uint Crc32OfZeros(long n)
    {
        var powers = new uint[32];
        uint p = 1u << 30; // x^1
        powers[0] = p;
        for (int i = 1; i < 32; i++)
        {
            p = MultModP(p, p);
            powers[i] = p;
        }

        uint result = 1u << 31; // x^0
        int k = 3;
        long rest = n;
        while (rest != 0)
        {
            if ((rest & 1) != 0)
            {
                result = MultModP(powers[k & 31], result);
            }

            rest >>= 1;
            k++;
        }

        return ~MultModP(result, ~0u);
    }

    public static uint Adler32OfZeros(long n) => (uint)(((n % 65521) << 16) | 1);

    public static uint Adler32(byte[] payload)
    {
        uint a = 1;
        uint b = 0;
        int i = 0;
        while (i < payload.Length)
        {
            int end = Math.Min(payload.Length, i + 5552);
            for (; i < end; i++)
            {
                a += payload[i];
                b += a;
            }

            a %= 65521;
            b %= 65521;
        }

        return (b << 16) | a;
    }

    public static byte[] GzipMember(byte[] deflate, uint crc, long size) =>
        Concat(new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff }, deflate, Le32(crc), Le32((uint)size));

    public static byte[] ZlibWrap(byte[] deflate, uint adler) =>
        Concat(new byte[] { 0x78, 0x01 }, deflate, new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler });

    public static byte[] GzipStored(byte[] payload) => GzipMember(StoredDeflate(payload), Crc32(payload), payload.Length);

    public static byte[] ZlibStored(byte[] payload) => ZlibWrap(StoredDeflate(payload), Adler32(payload));

    // -- brotli (RFC 7932) ---------------------------------------------------------------------------

    public static byte[] BrotliZeros(long size, int wbits)
    {
        var w = new BitWriter();
        BrotliWindowBits(w, wbits);
        long left = size;
        while (left > 0)
        {
            long chunk = Math.Min(left, BrotliMetablockMax);
            long remainder = left - chunk;
            if (remainder > 0 && remainder < BrotliMinCommandBytes)
            {
                chunk -= BrotliMinCommandBytes;
            }

            BrotliZerosMetablock(w, chunk);
            left -= chunk;
        }

        BrotliEnd(w);
        return w.Finish();
    }

    public static byte[] BrotliStored(byte[] payload, int wbits)
    {
        var w = new BitWriter();
        BrotliWindowBits(w, wbits);
        for (long pos = 0; pos < payload.Length; pos += BrotliMetablockMax)
        {
            int n = (int)Math.Min(BrotliMetablockMax, payload.Length - pos);
            BrotliMlen(w, n);
            w.Bits(1, 1); // ISUNCOMPRESSED
            w.Align();
            w.Raw(payload, (int)pos, n);
        }

        BrotliEnd(w);
        return w.Finish();
    }

    public static byte[] BrotliLargeWindowPrefix() => new byte[] { 0x11, 0, 0, 0 };

    // -- zstd (RFC 8878) -----------------------------------------------------------------------------

    public static byte[] ZstdHeader(int windowLog)
    {
        if (windowLog < 10 || windowLog > 41)
        {
            throw new ArgumentOutOfRangeException(nameof(windowLog));
        }

        return new byte[] { 0x28, 0xb5, 0x2f, 0xfd, 0, (byte)((windowLog - 10) << 3) };
    }

    public static long ZstdBlockMaxFor(int windowLog) => windowLog >= 17 ? ZstdBlockMax : 1L << windowLog;

    public static byte[] ZstdRleBlocks(long size, long blockMax, int value, bool last)
    {
        long blocks = Math.Max(1, (size + blockMax - 1) / blockMax);
        var output = new byte[checked((int)(blocks * 4))];
        long left = size;
        int o = 0;
        for (; ; )
        {
            long chunk = Math.Min(left, blockMax);
            left -= chunk;
            byte[] h = ZstdBlockHeader(last && left == 0, 1, chunk);
            Buffer.BlockCopy(h, 0, output, o, 3);
            output[o + 3] = (byte)value;
            o += 4;
            if (left == 0)
            {
                return output;
            }
        }
    }

    public static byte[] ZstdRawBlocks(byte[] payload, long blockMax, bool last)
    {
        var w = new BitWriter();
        int pos = 0;
        for (; ; )
        {
            int end = (int)Math.Min(payload.Length, pos + blockMax);
            byte[] h = ZstdBlockHeader(last && end >= payload.Length, 0, end - pos);
            w.Raw(h, 0, 3);
            w.Raw(payload, pos, end - pos);
            pos = end;
            if (pos >= payload.Length)
            {
                return w.Finish();
            }
        }
    }

    public static byte[] ZstdZeros(long size, int windowLog) =>
        Concat(ZstdHeader(windowLog), ZstdRleBlocks(size, ZstdBlockMaxFor(windowLog), 0, true));

    public static byte[] ZstdStored(byte[] payload, int windowLog) =>
        Concat(ZstdHeader(windowLog), ZstdRawBlocks(payload, ZstdBlockMaxFor(windowLog), true));

    public static byte[] ZstdSkippable(int n) => Concat(new byte[] { 0x50, 0x2a, 0x4d, 0x18 }, Le32((uint)n), new byte[n]);

    // -- generator specs -----------------------------------------------------------------------------

    /// <summary>Builds the compressed input a manifest generator describes.</summary>
    public static byte[] Generate(JsonElement spec)
    {
        string kind = spec.GetProperty("kind").GetString()!;
        JsonElement p = spec.GetProperty("params");
        string codec = p.GetProperty("codec").GetString()!;
        int window = p.TryGetProperty("window", out JsonElement wi) ? wi.GetInt32() : 0;
        switch (kind)
        {
            case "zeros":
                if (codec == "br")
                {
                    return BrotliZeros(p.GetProperty("size").GetInt64(), window);
                }

                if (codec == "zstd")
                {
                    return ZstdZeros(p.GetProperty("size").GetInt64(), window);
                }

                return ZerosStream(codec, p.GetProperty("matches").GetInt64());
            case "data":
                return WrapOnce(codec, Data(p.GetProperty("seed").GetInt64(), checked((int)p.GetProperty("size").GetInt64())), window);
            case "wrap":
                byte[] stream = Generate(p.GetProperty("inner"));
                long times = p.GetProperty("times").GetInt64();
                for (long i = 0; i < times; i++)
                {
                    stream = WrapOnce(codec, stream, window);
                }

                return stream;
            default:
                throw new ArgumentException("unknown generator kind " + kind);
        }
    }

    /// <summary>A gzip, zlib ("zlib") or raw deflate ("raw") zeros bomb of 1 + 258 * matches decoded bytes.</summary>
    public static byte[] ZerosOf(string codec, long matches) => ZerosStream(codec, matches);

    public static string Sha256Hex(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    public static string HexOf(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static byte[] ZerosStream(string codec, long matches)
    {
        long size = ZerosDecodedSize(matches);
        byte[] deflate = ZerosDeflate(matches);
        return codec switch
        {
            "gzip" => GzipMember(deflate, Crc32OfZeros(size), size),
            "zlib" => ZlibWrap(deflate, Adler32OfZeros(size)),
            "raw" => deflate,
            _ => throw new ArgumentException("zeros generator for codec " + codec),
        };
    }

    private static byte[] WrapOnce(string codec, byte[] payload, int window) => codec switch
    {
        "br" => BrotliStored(payload, window),
        "zstd" => ZstdStored(payload, window),
        "gzip" => GzipStored(payload),
        "zlib" => ZlibStored(payload),
        "raw" => StoredDeflate(payload),
        _ => throw new ArgumentException("wrap generator for codec " + codec),
    };

    private static void ZeroRun(BitWriter w, int count)
    {
        w.Code(0, 1);
        w.Bits(count - 11, 7);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    private static uint MultModP(uint a, uint b)
    {
        uint m = 1u << 31;
        uint p = 0;
        uint bb = b;
        for (; ; )
        {
            if ((a & m) != 0)
            {
                p ^= bb;
                if ((a & (m - 1)) == 0)
                {
                    break;
                }
            }

            m >>= 1;
            bb = (bb & 1) != 0 ? (bb >> 1) ^ 0xedb88320u : bb >> 1;
        }

        return p;
    }

    private static byte[] Le32(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    private static void BrotliWindowBits(BitWriter w, int wbits)
    {
        if (wbits == 16)
        {
            w.Bits(0, 1);
        }
        else if (wbits == 17)
        {
            w.Bits(1, 1);
            w.Bits(0, 3);
            w.Bits(0, 3);
        }
        else if (wbits >= 18 && wbits <= 24)
        {
            w.Bits(1, 1);
            w.Bits(wbits - 17, 3);
        }
        else if (wbits >= 10 && wbits <= 15)
        {
            w.Bits(1, 1);
            w.Bits(0, 3);
            w.Bits(wbits - 8, 3);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(wbits), "brotli window bits must be in 10..24");
        }
    }

    private static void BrotliMlen(BitWriter w, long mlen)
    {
        long value = mlen - 1;
        w.Bits(0, 1); // ISLAST
        if (value < 1L << 16)
        {
            w.Bits(0, 2);
            w.Bits(value, 16);
        }
        else if (value < 1L << 20)
        {
            w.Bits(1, 2);
            w.Bits(value, 20);
        }
        else
        {
            w.Bits(2, 2);
            w.Bits(value, 24);
        }
    }

    private static void BrotliEnd(BitWriter w)
    {
        w.Bits(1, 1);
        w.Bits(1, 1);
    }

    private static void BrotliZerosMetablock(BitWriter w, long mlen)
    {
        BrotliMlen(w, mlen);
        w.Bits(0, 1); // ISUNCOMPRESSED
        w.Bits(0, 3); // NBLTYPES*
        w.Bits(0, 2); // NPOSTFIX
        w.Bits(0, 4); // NDIRECT
        w.Bits(0, 2); // context mode
        w.Bits(0, 2); // NTREESL, NTREESD
        foreach ((long symbol, int bits) in new[] { (0L, 8), (423L, 10), (0L, 6) })
        {
            w.Bits(1, 2); // HSKIP
            w.Bits(0, 2); // NSYM - 1
            w.Bits(symbol, bits);
        }

        w.Bits(mlen - 4 - 2118, 24);
    }

    private static byte[] ZstdBlockHeader(bool last, int kind, long size)
    {
        long v = (size << 3) | ((long)kind << 1) | (last ? 1L : 0L);
        return new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16) };
    }
}
