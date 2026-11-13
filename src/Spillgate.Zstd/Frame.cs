using System;

namespace Spillgate.Zstd;

/// <summary>Reusable block buffers. Sizes follow the bytes that arrived, never a declared size, and stay under 128 KiB each.</summary>
internal sealed class Scratch
{
    internal const int BlockMax = 128 * 1024;

    internal byte[] Content = Array.Empty<byte>();
    internal int ContentLen;
    internal byte[] Lits = Array.Empty<byte>();
    internal int LitsLen;
    internal byte[] Out = Array.Empty<byte>();
    internal int OutLen;

    internal static byte[] Grow(byte[] a, int needed)
    {
        if (needed < 0 || needed > BlockMax)
        {
            throw Err.Internal("a block buffer of " + needed + " bytes was requested");
        }

        if (a.Length >= needed)
        {
            return a;
        }

        int size = Math.Max(needed, Math.Min(a.Length * 2, BlockMax));
        var b = new byte[size];
        Buffer.BlockCopy(a, 0, b, 0, a.Length);
        return b;
    }

    internal void EnsureContent(int n) => Content = Grow(Content, n);

    internal void EnsureLits(int n) => Lits = Grow(Lits, n);

    internal void EnsureOut(int n) => Out = Grow(Out, n);

    internal void Release()
    {
        Content = Array.Empty<byte>();
        Lits = Array.Empty<byte>();
        Out = Array.Empty<byte>();
        ContentLen = 0;
        LitsLen = 0;
        OutLen = 0;
    }
}

/// <summary>One zstd frame: window, repeat offsets, carried-over tables, and the decoding of compressed blocks.</summary>
internal sealed class Frame
{
    private static readonly int[] LlBase =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512,
        1024, 2048, 4096, 8192, 16384, 32768, 65536,
    };

    private static readonly int[] LlBits =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    };

    private static readonly int[] MlBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        32, 33, 34, 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771,
        65539,
    };

    private static readonly int[] MlBits =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2,
        2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    };

    private static readonly int[] LlDefault =
    {
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1,
    };

    private static readonly int[] MlDefault =
    {
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1,
    };

    private static readonly int[] OfDefault =
    {
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    };

    private static readonly Fse.Table DefaultLl = Fse.Build(LlDefault, LlDefault.Length, 6);
    private static readonly Fse.Table DefaultMl = Fse.Build(MlDefault, MlDefault.Length, 6);
    private static readonly Fse.Table DefaultOf = Fse.Build(OfDefault, OfDefault.Length, 5);

    private readonly int _windowSize;
    private long _rep0 = 1;
    private long _rep1 = 4;
    private long _rep2 = 8;
    private Huffman.Table? _huffman;
    private Fse.Table? _ll;
    private Fse.Table? _of;
    private Fse.Table? _ml;

    internal Frame(int windowSize)
    {
        _windowSize = windowSize;
        Window = new Window(windowSize);
    }

    internal Window Window { get; }

    internal long Total => Window.Total;

    internal int WindowCapacity => Window.Capacity;

    /// <summary>The largest block this frame may contain: the window or 128 KiB, whichever is smaller.</summary>
    internal int BlockLimit => Math.Min(_windowSize, Scratch.BlockMax);

    internal void Push(byte[] block, int len) => Window.Push(block, 0, len);

    internal void Compressed(Scratch s)
    {
        int used = ReadLiterals(s);
        ReadSequences(s, used);
    }

    private static int ByteAt(byte[] c, int n, int i)
    {
        if (i >= n)
        {
            throw Err.Corrupt("a literals section ends too early");
        }

        return c[i];
    }

    private static Fse.Table ReadMode(
        int mode, int maxLog, int maxSymbol, Fse.Table fallback, Fse.Table? previous, byte[] sec, int secEnd, ref int at)
    {
        switch (mode)
        {
            case 0:
                return fallback;
            case 1:
                {
                    if (at >= secEnd)
                    {
                        throw Err.Corrupt("an RLE table ends too early");
                    }

                    int symbol = sec[at];
                    at++;
                    if (symbol > maxSymbol)
                    {
                        throw Err.Corrupt("an RLE table names an impossible symbol");
                    }

                    return Fse.Rle(symbol);
                }

            case 2:
                {
                    (Fse.Table table, int used) = Fse.Read(sec, at, secEnd, maxLog, maxSymbol);
                    at += used;
                    return table;
                }

            default:
                return previous ?? throw Err.Corrupt("a repeated table without an earlier one");
        }
    }

    private int ReadLiterals(Scratch s)
    {
        byte[] c = s.Content;
        int n = s.ContentLen;
        if (n == 0)
        {
            throw Err.Corrupt("an empty compressed block");
        }

        int b0 = c[0];
        int kind = b0 & 3;
        int sizeFormat = (b0 >> 2) & 3;
        int blockMax = BlockLimit;
        s.LitsLen = 0;

        if (kind == 0 || kind == 1)
        {
            int regen;
            int header;
            switch (sizeFormat)
            {
                case 0:
                case 2:
                    regen = b0 >> 3;
                    header = 1;
                    break;
                case 1:
                    regen = (b0 >> 4) + (ByteAt(c, n, 1) << 4);
                    header = 2;
                    break;
                default:
                    regen = (b0 >> 4) + (ByteAt(c, n, 1) << 4) + (ByteAt(c, n, 2) << 12);
                    header = 3;
                    break;
            }

            if (regen > blockMax)
            {
                throw Err.Corrupt("a literals section is larger than a block");
            }

            if (kind == 0)
            {
                int end = header + regen;
                if (end > n)
                {
                    throw Err.Corrupt("a literals section ends too early");
                }

                s.EnsureLits(regen);
                Buffer.BlockCopy(c, header, s.Lits, 0, regen);
                s.LitsLen = regen;
                return end;
            }

            int fill = ByteAt(c, n, header);
            s.EnsureLits(regen);
            Array.Fill(s.Lits, (byte)fill, 0, regen);
            s.LitsLen = regen;
            return header + 1;
        }

        // Compressed (2) or treeless (3): sizes of 10, 10, 14 or 18 bits.
        long regenL;
        long compressedL;
        int headerSize;
        int streams;
        switch (sizeFormat)
        {
            case 0:
            case 1:
                {
                    long v = b0 + ((long)ByteAt(c, n, 1) << 8) + ((long)ByteAt(c, n, 2) << 16);
                    regenL = (v >> 4) & 0x3ff;
                    compressedL = (v >> 14) & 0x3ff;
                    headerSize = 3;
                    streams = sizeFormat == 0 ? 1 : 4;
                    break;
                }

            case 2:
                {
                    long v = b0 + ((long)ByteAt(c, n, 1) << 8) + ((long)ByteAt(c, n, 2) << 16)
                        + ((long)ByteAt(c, n, 3) << 24);
                    regenL = (v >> 4) & 0x3fff;
                    compressedL = v >> 18;
                    headerSize = 4;
                    streams = 4;
                    break;
                }

            default:
                {
                    long v = b0 + ((long)ByteAt(c, n, 1) << 8) + ((long)ByteAt(c, n, 2) << 16)
                        + ((long)ByteAt(c, n, 3) << 24) + ((long)ByteAt(c, n, 4) << 32);
                    regenL = (v >> 4) & 0x3ffff;
                    compressedL = (v >> 22) & 0x3ffff;
                    headerSize = 5;
                    streams = 4;
                    break;
                }
        }

        if (regenL > blockMax)
        {
            throw Err.Corrupt("a literals section is larger than a block");
        }

        int regenSize = (int)regenL;
        long endL = headerSize + compressedL;
        if (endL > n)
        {
            throw Err.Corrupt("a literals section ends too early");
        }

        int endAt = (int)endL;
        int bodyFrom = headerSize;

        if (kind == 2)
        {
            (Huffman.Table parsed, int used) = Huffman.Read(c, bodyFrom, endAt);
            _huffman = parsed;
            bodyFrom += used;
            if (bodyFrom > endAt)
            {
                throw Err.Corrupt("a Huffman tree description ends too early");
            }
        }

        Huffman.Table table = _huffman ?? throw Err.Corrupt("treeless literals without an earlier Huffman table");
        s.EnsureLits(regenSize);
        s.LitsLen = regenSize;
        if (streams == 1)
        {
            Huffman.DecodeStream(table, c, bodyFrom, endAt, s.Lits, 0, regenSize);
        }
        else
        {
            if (endAt - bodyFrom < 6)
            {
                throw Err.Corrupt("a literals jump table ends too early");
            }

            int s1 = c[bodyFrom] | (c[bodyFrom + 1] << 8);
            int s2 = c[bodyFrom + 2] | (c[bodyFrom + 3] << 8);
            int s3 = c[bodyFrom + 4] | (c[bodyFrom + 5] << 8);
            long restL = (long)(endAt - bodyFrom - 6) - s1 - s2 - s3;
            if (restL < 0)
            {
                throw Err.Corrupt("literal stream sizes exceed the section");
            }

            int part = (regenSize + 3) / 4;
            int last = regenSize - (3 * part);
            if (last < 0)
            {
                throw Err.Corrupt("a literals section is too small for four streams");
            }

            int at = bodyFrom + 6;
            int outAt = 0;
            int[] sizes = { s1, s2, s3, (int)restL };
            int[] counts = { part, part, part, last };
            for (int i = 0; i < 4; i++)
            {
                Huffman.DecodeStream(table, c, at, at + sizes[i], s.Lits, outAt, counts[i]);
                at += sizes[i];
                outAt += counts[i];
            }
        }

        return endAt;
    }

    private void ReadSequences(Scratch s, int from)
    {
        byte[] sec = s.Content;
        int end = s.ContentLen;
        s.OutLen = 0;
        if (from >= end)
        {
            throw Err.Corrupt("a sequences section is missing");
        }

        int b0 = sec[from];
        int at = from + 1;
        int count;
        if (b0 == 0)
        {
            // No sequences: the literals are the whole block, and nothing may follow the count.
            if (end - from != 1)
            {
                throw Err.Corrupt("bytes follow a sequences section without sequences");
            }

            s.EnsureOut(s.LitsLen);
            Buffer.BlockCopy(s.Lits, 0, s.Out, 0, s.LitsLen);
            s.OutLen = s.LitsLen;
            return;
        }

        if (b0 < 128)
        {
            count = b0;
        }
        else if (b0 < 255)
        {
            if (at >= end)
            {
                throw Err.Corrupt("a sequences header ends too early");
            }

            count = ((b0 - 128) << 8) + sec[at];
            at += 1;
        }
        else
        {
            if (at + 1 >= end)
            {
                throw Err.Corrupt("a sequences header ends too early");
            }

            count = sec[at] + (sec[at + 1] << 8) + 0x7f00;
            at += 2;
        }

        if (at >= end)
        {
            throw Err.Corrupt("a sequences header ends too early");
        }

        int modes = sec[at];
        at++;
        if ((modes & 3) != 0)
        {
            throw Err.Corrupt("reserved bits are set in the symbol compression modes");
        }

        int cursor = at;
        Fse.Table nl = ReadMode(modes >> 6, 9, 35, DefaultLl, _ll, sec, end, ref cursor);
        Fse.Table no = ReadMode((modes >> 4) & 3, 8, 31, DefaultOf, _of, sec, end, ref cursor);
        Fse.Table nm = ReadMode((modes >> 2) & 3, 9, 52, DefaultMl, _ml, sec, end, ref cursor);
        _ll = nl;
        _of = no;
        _ml = nm;
        Execute(s, cursor, end, count, nl, no, nm);
    }

    private void Execute(Scratch s, int from, int end, int count, Fse.Table llT, Fse.Table ofT, Fse.Table mlT)
    {
        var bits = new BitStreams.Backward(s.Content, from, end);
        long llState = bits.Read(llT.AccuracyLog);
        long ofState = bits.Read(ofT.AccuracyLog);
        long mlState = bits.Read(mlT.AccuracyLog);

        long limit = BlockLimit;
        long windowLimit = _windowSize;
        long history = Window.Total;
        byte[] lits = s.Lits;
        int litsLen = s.LitsLen;
        int litPos = 0;

        for (int i = 0; i < count; i++)
        {
            if (llState >= llT.Size || ofState >= ofT.Size || mlState >= mlT.Size)
            {
                throw Err.Corrupt("a table state is out of range");
            }

            int llI = (int)llState;
            int ofI = (int)ofState;
            int mlI = (int)mlState;
            int ofCode = ofT.Symbol[ofI];
            int mlCode = mlT.Symbol[mlI];
            int llCode = llT.Symbol[llI];
            if (ofCode > 31 || mlCode > 52 || llCode > 35)
            {
                throw Err.Corrupt("a sequence names an impossible code");
            }

            long offsetValue = (1L << ofCode) + bits.Read(ofCode);
            long matchLength = MlBase[mlCode] + bits.Read(MlBits[mlCode]);
            long literalLength = LlBase[llCode] + bits.Read(LlBits[llCode]);

            // Repeat offsets (RFC 8878 section 3.1.1.5).
            long offset;
            if (offsetValue > 3)
            {
                offset = offsetValue - 3;
                _rep2 = _rep1;
                _rep1 = _rep0;
                _rep0 = offset;
            }
            else
            {
                long index = offsetValue;
                if (literalLength == 0)
                {
                    index++;
                }

                if (index == 1)
                {
                    offset = _rep0;
                }
                else if (index == 2)
                {
                    offset = _rep1;
                    _rep1 = _rep0;
                    _rep0 = offset;
                }
                else if (index == 3)
                {
                    offset = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;
                    _rep0 = offset;
                }
                else
                {
                    offset = _rep0 - 1;
                    if (offset == 0)
                    {
                        throw Err.Corrupt("a repeat offset of zero");
                    }

                    _rep2 = _rep1;
                    _rep1 = _rep0;
                    _rep0 = offset;
                }
            }

            if (s.OutLen + literalLength + matchLength > limit)
            {
                throw Err.Corrupt("a block regenerates more than its maximum");
            }

            if (litPos + literalLength > litsLen)
            {
                throw Err.Corrupt("a sequence uses more literals than the block has");
            }

            int need = s.OutLen + (int)literalLength + (int)matchLength;
            s.EnsureOut(need);
            int ln = (int)literalLength;
            Buffer.BlockCopy(lits, litPos, s.Out, s.OutLen, ln);
            s.OutLen += ln;
            litPos += ln;

            if (offset <= 0 || offset > history + s.OutLen || offset > windowLimit)
            {
                throw Err.Corrupt("a match reaches before the start of the window");
            }

            CopyMatch(s, (int)offset, (int)matchLength);

            if (i + 1 < count)
            {
                llState = llT.Baseline[llI] + bits.Read(llT.NbBits[llI]);
                mlState = mlT.Baseline[mlI] + bits.Read(mlT.NbBits[mlI]);
                ofState = ofT.Baseline[ofI] + bits.Read(ofT.NbBits[ofI]);
            }
        }

        if (bits.Remaining != 0)
        {
            throw Err.Corrupt("the sequence bit stream was not consumed exactly");
        }

        // The literals that no sequence used come last.
        int tail = litsLen - litPos;
        if ((long)s.OutLen + tail > limit)
        {
            throw Err.Corrupt("a block regenerates more than its maximum");
        }

        s.EnsureOut(s.OutLen + tail);
        Buffer.BlockCopy(lits, litPos, s.Out, s.OutLen, tail);
        s.OutLen += tail;
    }

    private void CopyMatch(Scratch s, int offset, int len)
    {
        byte[] output = s.Out;
        int remaining = len;
        while (remaining > 0)
        {
            if (offset <= s.OutLen)
            {
                int start = s.OutLen - offset;
                if (offset == 1)
                {
                    Array.Fill(output, output[start], s.OutLen, remaining);
                    s.OutLen += remaining;
                    return;
                }

                // The period is `offset`; each full copy doubles what is available.
                while (remaining > 0)
                {
                    int n = Math.Min(remaining, s.OutLen - start);
                    Buffer.BlockCopy(output, start, output, s.OutLen, n);
                    s.OutLen += n;
                    remaining -= n;
                }
            }
            else
            {
                // d bytes of the match lie in the history before this block.
                int d = offset - s.OutLen;
                int n = Math.Min(remaining, d);
                Window.CopyHistory(d, n, output, s.OutLen);
                s.OutLen += n;
                remaining -= n;
            }
        }
    }
}
