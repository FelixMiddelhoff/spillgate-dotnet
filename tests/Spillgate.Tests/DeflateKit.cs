using System;
using Spillgate.TestSupport;

namespace Spillgate.Tests;

/// <summary>Writes DEFLATE blocks symbol by symbol, so tests can build valid and deliberately broken streams.</summary>
public sealed class DeflateKit
{
    public static readonly int[] FixedLiteralLengths = BuildFixedLiteral();
    public static readonly int[] FixedDistanceLengths = Fill(32, 5);

    private static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    private static readonly int[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    private static readonly int[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    private static readonly int[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };

    private readonly int[] _litCodes;
    private readonly int[] _litLens;
    private readonly int[] _distCodes;
    private readonly int[] _distLens;

    private DeflateKit(int[] litLens, int[] distLens)
    {
        _litLens = litLens;
        _distLens = distLens;
        _litCodes = Canonical(litLens);
        _distCodes = Canonical(distLens);
    }

    public BitWriter W { get; } = new();

    /// <summary>A fixed-Huffman block header.</summary>
    public static DeflateKit Fixed(bool final)
    {
        var k = new DeflateKit(FixedLiteralLengths, FixedDistanceLengths);
        k.W.Bits(final ? 1 : 0, 1);
        k.W.Bits(1, 2);
        return k;
    }

    /// <summary>
    /// A dynamic block header for the given code lengths (0..15 each; litLens at least 257 entries, distLens at
    /// least 1). The code length alphabet is sixteen 4-bit codes, so any lengths can be written without repeats.
    /// </summary>
    public static DeflateKit Dynamic(bool final, int[] litLens, int[] distLens)
    {
        var k = new DeflateKit(litLens, distLens);
        k.W.Bits(final ? 1 : 0, 1);
        k.W.Bits(2, 2);
        k.W.Bits(litLens.Length - 257, 5);
        k.W.Bits(distLens.Length - 1, 5);
        k.W.Bits(15, 4); // HCLEN: all 19 entries
        int[] order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
        foreach (int symbol in order)
        {
            k.W.Bits(symbol <= 15 ? 4 : 0, 3 + 0);
        }

        foreach (int length in litLens)
        {
            k.W.Code(length, 4);
        }

        foreach (int length in distLens)
        {
            k.W.Code(length, 4);
        }

        return k;
    }

    /// <summary>An array of n entries, all with the given value.</summary>
    public static int[] Fill(int n, int value)
    {
        var a = new int[n];
        Array.Fill(a, value);
        return a;
    }

    /// <summary>
    /// Literal/length lengths of a complete code over 286 entries: literals 0..127 at 8 bits, 128..255 at 9 bits and
    /// the symbols 256..271 (end of block and the length codes up to 24) at 6 bits.
    /// </summary>
    public static int[] CompleteLiteralLengths()
    {
        var l = new int[286];
        Array.Fill(l, 8, 0, 128);
        Array.Fill(l, 9, 128, 128);
        Array.Fill(l, 6, 256, 16);
        return l;
    }

    public static byte[] Stored(byte[] payload, bool final)
    {
        var w = new BitWriter();
        w.Bits(final ? 1 : 0, 1);
        w.Bits(0, 2);
        w.Align();
        w.Bits(payload.Length, 16);
        w.Bits(~payload.Length & 0xffff, 16);
        w.Raw(payload, 0, payload.Length);
        return w.Finish();
    }

    public DeflateKit Lit(int symbol)
    {
        W.Code(_litCodes[symbol], _litLens[symbol]);
        return this;
    }

    public DeflateKit Literal(byte b) => Lit(b);

    public DeflateKit Literals(string text)
    {
        foreach (char c in text)
        {
            Lit(c);
        }

        return this;
    }

    public DeflateKit EndOfBlock() => Lit(256);

    /// <summary>A match of the given length (3..258) and distance (1..32768).</summary>
    public DeflateKit Match(int length, int distance)
    {
        int lcode = LengthBase.Length - 1;
        while (LengthBase[lcode] > length)
        {
            lcode--;
        }

        Lit(257 + lcode);
        W.Bits(length - LengthBase[lcode], LengthExtra[lcode]);
        int dcode = DistBase.Length - 1;
        while (DistBase[dcode] > distance)
        {
            dcode--;
        }

        W.Code(_distCodes[dcode], _distLens[dcode]);
        W.Bits(distance - DistBase[dcode], DistExtra[dcode]);
        return this;
    }

    /// <summary>Writes a raw distance symbol (for invalid-symbol tests).</summary>
    public DeflateKit DistSymbol(int symbol)
    {
        W.Code(_distCodes[symbol], _distLens[symbol]);
        return this;
    }

    public byte[] Finish() => W.Finish();

    public static int[] Canonical(int[] lengths)
    {
        var count = new int[16];
        foreach (int l in lengths)
        {
            count[l]++;
        }

        count[0] = 0;
        var next = new int[16];
        int code = 0;
        for (int l = 1; l <= 15; l++)
        {
            code = (code + count[l - 1]) << 1;
            next[l] = code;
        }

        var codes = new int[lengths.Length];
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] != 0)
            {
                codes[s] = next[lengths[s]]++;
            }
        }

        return codes;
    }

    public static byte[] AsGzip(byte[] deflate, byte[] plain) => Generators.GzipMember(deflate, Generators.Crc32(plain), plain.Length);

    public static byte[] AsZlib(byte[] deflate, byte[] plain) => Generators.ZlibWrap(deflate, Generators.Adler32(plain));

    private static int[] BuildFixedLiteral()
    {
        var l = new int[288];
        Array.Fill(l, 8, 0, 144);
        Array.Fill(l, 9, 144, 112);
        Array.Fill(l, 7, 256, 24);
        Array.Fill(l, 8, 280, 8);
        return l;
    }
}
