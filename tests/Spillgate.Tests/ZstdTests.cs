using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Spillgate.TestSupport;
using Spillgate.Zstd;
using Xunit;

namespace Spillgate.Tests;

/// <summary>zstd: golden streams made by libzstd, hand-built frames for every header and block rule.</summary>
public sealed class ZstdTests
{
    private static readonly Decoder Wide = T.DecoderFor(1 << 26, l => l.WithoutRatioLimit());
    private static readonly byte[] Magic = { 0x28, 0xb5, 0x2f, 0xfd };

    private static string ResourceDir => Repo.Path("tests", "Spillgate.Tests", "Resources", "zstd");

    public static IEnumerable<object[]> Goldens()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ResourceDir, "manifest.json")));
        foreach (JsonElement e in manifest.RootElement.EnumerateArray())
        {
            yield return new object[] { e.GetProperty("name").GetString()! };
        }
    }

    private static (byte[] Stream, long Size, string Sha) Golden(string name)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ResourceDir, "manifest.json")));
        foreach (JsonElement e in manifest.RootElement.EnumerateArray())
        {
            if (e.GetProperty("name").GetString() == name)
            {
                return (File.ReadAllBytes(Path.Combine(ResourceDir, name + ".zst")), e.GetProperty("size").GetInt64(), e.GetProperty("sha256").GetString()!);
            }
        }

        throw new ArgumentException(name);
    }

    // -- builders ------------------------------------------------------------------------------------------

    private static byte[] Block(bool last, int type, int size, params byte[] body)
    {
        uint h = (uint)((size << 3) | (type << 1) | (last ? 1 : 0));
        return Generators.Concat(new[] { (byte)h, (byte)(h >> 8), (byte)(h >> 16) }, body);
    }

    private static byte[] Raw(bool last, byte[] data) => Block(last, 0, data.Length, data);

    private static byte[] Rle(bool last, int size, byte value) => Block(last, 1, size, value);

    /// <summary>Frame header with an explicit descriptor, window byte (omitted for single segment) and trailing fields.</summary>
    private static byte[] Header(int descriptor, int? windowByte, params byte[] rest) =>
        Generators.Concat(Magic, new[] { (byte)descriptor }, windowByte is { } w ? new[] { (byte)w } : Array.Empty<byte>(), rest);

    private static byte[] Frame(byte[] data, int windowLog = 20, bool checksum = false, int blockSize = 100_000)
    {
        int descriptor = checksum ? 4 : 0;
        var parts = new List<byte[]> { Header(descriptor, (windowLog - 10) << 3) };
        for (int pos = 0; pos < data.Length || pos == 0; pos += blockSize)
        {
            int n = Math.Min(blockSize, data.Length - pos);
            parts.Add(Raw(pos + n >= data.Length, data.AsSpan(pos, n).ToArray()));
            if (n == 0)
            {
                break;
            }
        }

        if (checksum)
        {
            var h = new Xxh64();
            h.Update(data);
            parts.Add(BitConverter.GetBytes(h.Digest32()));
        }

        return Generators.Concat(parts.ToArray());
    }

    private static string Kind(byte[] stream, long maxOutput = 1 << 26, int chunk = int.MaxValue, Func<Limits, Limits>? limits = null, ZstdOptions? options = null)
    {
        Decoder d = Decoder.CreateBuilder((limits ?? (l => l.WithoutRatioLimit()))(Limits.Create(maxOutput))).AddZstd(options).Build();
        return T.Read(d, new ChunkedStream(stream, chunk), 4096, "zstd").Name;
    }

    // -- golden streams ------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Goldens))]
    [Covers("SG-R-CDC-05", "SG-R-CDC-07", "SG-I-09")]
    public void GoldenStreamsDecodeToTheirPlainDataForEveryReadSizeAndChunking(string name)
    {
        (byte[] stream, long size, string sha) = Golden(name);
        foreach (int readSize in new[] { 1, 17, 4096, 1 << 20 })
        {
            if (readSize == 1 && size > 100_000)
            {
                continue;
            }

            foreach (int chunk in new[] { int.MaxValue, 1, 1000 })
            {
                if (chunk == 1 && stream.Length > 100_000)
                {
                    continue;
                }

                Outcome o = T.Read(Wide, new ChunkedStream(stream, chunk), readSize, "zstd");
                Assert.True(o.Error is null, name + ": " + o);
                Assert.Equal(size, o.Data.Length);
                Assert.Equal(sha, T.Sha(o.Data));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    [Covers("SG-R-CDC-06", "SG-R-CDC-05", "SG-I-02")]
    public void CutsAndTrailingBytesOfGoldenStreamsAreCorrupt(string name)
    {
        (byte[] stream, _, _) = Golden(name);
        int step = Math.Max(1, stream.Length / 60);
        for (int cut = 0; cut < stream.Length; cut += cut < 40 ? 1 : step)
        {
            Outcome o = T.Read(Wide, T.Slice(stream, cut), 4096, "zstd");
            Assert.True(o.Name == "corrupt", name + " cut " + cut + "/" + stream.Length + ": " + o);
        }

        foreach (byte[] extra in new[] { new byte[] { 0 }, new byte[] { 0x28 }, new byte[] { 0x50, 0x2a, 0x4d } })
        {
            Outcome o = T.Read(Wide, Generators.Concat(stream, extra), 4096, "zstd");
            Assert.True(o.Name == "corrupt", name + " + " + extra.Length + " extra bytes: " + o);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-05")]
    public void ChecksumsAreVerifiedOnGoldenStreams()
    {
        (byte[] stream, _, _) = Golden("text-l9-ck");
        byte[] bad = (byte[])stream.Clone();
        bad[bad.Length - 1] ^= 1;
        Outcome o = T.Read(Wide, bad, 4096, "zstd");
        Assert.Equal("corrupt", o.Name);
        Assert.NotEmpty(o.Data); // delivered before the checksum could be judged
    }

    [Fact]
    [Covers("SG-R-CDC-05")]
    public void TheXxHash64MatchesItsKnownAnswers()
    {
        static ulong Hash(string text, int chunk)
        {
            var h = new Xxh64();
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
            for (int i = 0; i < bytes.Length; i += chunk)
            {
                h.Update(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
            }

            return h.Digest();
        }

        foreach (int chunk in new[] { 1, 3, 7, 32, 1000 })
        {
            Assert.Equal(0xEF46DB3751D8E999UL, Hash(string.Empty, chunk));
            Assert.Equal(0xD24EC4F1A98C6E5BUL, Hash("a", chunk));
            Assert.Equal(0x44BC2CF5AD770999UL, Hash("abc", chunk));
            Assert.Equal(0xFBCEA83C8A378BF1UL, Hash("Nobody inspects the spammish repetition", chunk));
        }
    }

    // -- frames --------------------------------------------------------------------------------------------

    [Fact]
    [Covers("SG-R-CDC-05")]
    public void RawRleAndMultiBlockFramesDecode()
    {
        byte[] a = Generators.Data(1, 5000);
        var stream = Generators.Concat(Header(0, 7 << 3), Raw(false, a), Rle(false, 1000, 0x61), Raw(true, new byte[] { 1, 2, 3 }));
        Outcome o = T.Read(Wide, stream, 77, "zstd");
        Assert.Equal("ok", o.Name);
        Assert.Equal(Generators.Concat(a, Enumerable.Repeat((byte)0x61, 1000).ToArray(), new byte[] { 1, 2, 3 }), o.Data);
        Assert.Equal("ok", Kind(Frame(a, checksum: true)));
        Assert.Equal("ok", Kind(Frame(Array.Empty<byte>(), checksum: true)));
        Assert.Equal("ok", Kind(Frame(Array.Empty<byte>())));
    }

    [Fact]
    [Covers("SG-R-CDC-05")]
    public void ConcatenatedAndSkippableFramesAreAllowedAndSkippableBytesAreInputOnly()
    {
        byte[] f1 = Frame("one"u8.ToArray());
        byte[] f2 = Frame("two"u8.ToArray(), checksum: true);
        byte[] skip = Generators.ZstdSkippable(100);
        Outcome o = T.Read(Wide, new ChunkedStream(Generators.Concat(skip, f1, skip, f2, skip), 7), 5, "zstd");
        Assert.Equal("ok", o.Name);
        Assert.Equal("onetwo"u8.ToArray(), o.Data);

        // A skippable frame alone is a valid, empty stream; any of the sixteen magic numbers counts.
        Assert.Equal("ok", Kind(skip));
        for (int i = 0; i < 16; i++)
        {
            byte[] s = Generators.ZstdSkippable(3);
            s[0] = (byte)(0x50 + i);
            Assert.Equal("ok", Kind(s));
        }

        // The byte after the last frame must start a frame.
        Assert.Equal("corrupt", Kind(Generators.Concat(f1, new byte[] { 0x60, 0x2a, 0x4d, 0x18, 0, 0, 0, 0 })));
        Assert.Equal("corrupt", Kind(Generators.Concat(f1, new byte[] { 0x28, 0xb5, 0x2f, 0xfe })));
        Assert.Equal("corrupt", Kind(Array.Empty<byte>()));
        Assert.Equal("corrupt", Kind(new byte[] { 0x28, 0xb5 }));
        // A skippable frame whose body is cut short is corrupt, however large it claims to be.
        byte[] huge = { 0x50, 0x2a, 0x4d, 0x18, 0xff, 0xff, 0xff, 0xff, 1, 2, 3 };
        Assert.Equal("corrupt", Kind(huge));
        Assert.Equal("corrupt", Kind(new byte[] { 0x50, 0x2a, 0x4d, 0x18, 1, 0 }));
    }

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(23, 1, false)]
    [InlineData(22, 7, true)]
    [InlineData(24, 0, false)]
    [InlineData(31, 7, false)]
    [Covers("SG-R-CDC-05", "SG-I-04")]
    public void TheWindowIsCheckedAgainstTheCapBeforeAnythingIsAllocated(int windowLog, int mantissa, bool accepted)
    {
        byte[] stream = Generators.Concat(Header(0, ((windowLog - 10) << 3) | mantissa), Raw(true, "hi"u8.ToArray()));
        Assert.Equal(accepted ? "ok" : "memory_limit", Kind(stream));
        // A custom cap moves the line.
        Assert.Equal("memory_limit", Kind(Generators.Concat(Header(0, (20 - 10) << 3), Raw(true, "hi"u8.ToArray())), options: ZstdOptions.Create(1 << 19)));
        Assert.Equal("ok", Kind(Generators.Concat(Header(0, (20 - 10) << 3), Raw(true, "hi"u8.ToArray())), options: ZstdOptions.Create(1 << 20)));
    }

    [Fact]
    [Covers("SG-R-CDC-05", "SG-R-CDC-07")]
    public void ForSingleSegmentFramesTheDeclaredSizeIsTheWindow()
    {
        // single segment (bit 5), FCS flag 3 (8 bytes): 9 MiB declared with a 2 byte body.
        byte[] nine = Header(0xe0, null, BitConverter.GetBytes(9L * 1024 * 1024));
        Assert.Equal("memory_limit", Kind(Generators.Concat(nine, Raw(true, "hi"u8.ToArray()))));
        // The same frame with 8 MiB is accepted by the window rule, then judged by the size it declares.
        byte[] eight = Header(0xe0, null, BitConverter.GetBytes(8L * 1024 * 1024));
        Assert.Equal("corrupt", Kind(Generators.Concat(eight, Raw(true, "hi"u8.ToArray()))));
        // Declared sizes above 2^63 are unsigned: still a memory_limit, never a negative number.
        byte[] huge = Header(0xe0, null, BitConverter.GetBytes(ulong.MaxValue));
        Assert.Equal("memory_limit", Kind(Generators.Concat(huge, Raw(true, "hi"u8.ToArray()))));
        // Single segment with the 1-byte form: the size is the window (here 2 bytes) and must match.
        Assert.Equal("ok", Kind(Generators.Concat(Header(0x20, null, 2), Raw(true, "hi"u8.ToArray()))));
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0x20, null, 3), Raw(true, "hi"u8.ToArray()))));
    }

    [Fact]
    [Covers("SG-R-CDC-05", "SG-R-CDC-07", "SG-I-04")]
    public void ADeclaredContentSizeThatLiesIsCorruptButNeverAnAllowance()
    {
        // Window descriptor present, FCS 4-byte form (flag 2): declares 4 GiB - 1, delivers 2 bytes.
        byte[] liar = Generators.Concat(Header(0x80, 7 << 3, BitConverter.GetBytes(uint.MaxValue)), Raw(true, "hi"u8.ToArray()));
        Outcome o = T.Read(T.DecoderFor(1 << 20), liar, 4096, "zstd");
        Assert.Equal("corrupt", o.Name);
        // Declares 2, delivers 100 MB-less: both directions are caught; the limiter still bounds what is delivered.
        byte[] small = Generators.Concat(Header(0x80, 7 << 3, BitConverter.GetBytes(2u)), Raw(true, new byte[1000]));
        Outcome o2 = T.Read(T.DecoderFor(100), small, 4096, "zstd");
        Assert.Equal("output_limit", o2.Name);
        Assert.Equal(100, o2.Data.Length);
        // 2-byte form stores size - 256.
        byte[] two = Generators.Concat(Header(0x40, 7 << 3, 0, 0), Raw(true, new byte[256]));
        Assert.Equal("ok", Kind(two));
        byte[] twoWrong = Generators.Concat(Header(0x40, 7 << 3, 1, 0), Raw(true, new byte[256]));
        Assert.Equal("corrupt", Kind(twoWrong));
    }

    [Theory]
    [InlineData(0x01, 1)]
    [InlineData(0x02, 2)]
    [InlineData(0x03, 4)]
    [Covers("SG-R-CDC-05")]
    public void AFrameThatNeedsADictionaryIsCorruptButIdZeroIsAccepted(int flag, int idBytes)
    {
        byte[] zero = Generators.Concat(Header(flag, 7 << 3, new byte[idBytes]), Raw(true, "hi"u8.ToArray()));
        Assert.Equal("ok", Kind(zero));
        for (int i = 0; i < idBytes; i++)
        {
            byte[] id = new byte[idBytes];
            id[i] = 1;
            Assert.Equal("corrupt", Kind(Generators.Concat(Header(flag, 7 << 3, id), Raw(true, "hi"u8.ToArray()))));
        }
    }

    [Fact]
    [Covers("SG-R-CDC-05")]
    public void TheReservedBitAndTheReservedBlockTypeAreCorrupt()
    {
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0x08, 7 << 3), Raw(true, "hi"u8.ToArray()))));
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0, 7 << 3), Block(true, 3, 2, 1, 2))));
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0, 7 << 3), Block(true, 2, 0)))); // an empty compressed block
    }

    [Fact]
    [Covers("SG-R-CDC-05", "SG-I-04")]
    public void ABlockLargerThanItsMaximumIsRefusedBeforeItsBodyIsRead()
    {
        // Block max is min(window, 128 KiB). The 3-byte header claims a bigger block; the body is not even there.
        int tooBig = (128 * 1024) + 1;
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0, 12 << 3), Block(true, 0, tooBig))));
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0, 12 << 3), Block(true, 1, tooBig, 0))));
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0, 12 << 3), Block(true, 2, tooBig))));
        // A 1 KiB window (log 10) allows blocks of 1 KiB at most.
        Assert.Equal("ok", Kind(Generators.Concat(Header(0, 0), Raw(true, new byte[1024]))));
        Assert.Equal("corrupt", Kind(Generators.Concat(Header(0, 0), Raw(true, new byte[1025]))));
        // 128 KiB exactly fits a window of 128 KiB or more.
        Assert.Equal("ok", Kind(Generators.Concat(Header(0, 7 << 3), Raw(true, new byte[128 * 1024]))));
    }

    [Fact]
    [Covers("SG-R-CDC-05", "SG-R-MEM-01")]
    public void AllocationsFollowTheBytesThatArriveNotTheDeclaredSizes()
    {
        // A frame declaring an 8 MiB window and a 128 KiB block, then 100 bytes: the allocation stays small.
        byte[] stream = Generators.Concat(Header(0, 13 << 3), Block(true, 0, 128 * 1024, new byte[100]));
        long before = GC.GetAllocatedBytesForCurrentThread();
        string kind = Kind(stream);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("corrupt", kind);
        Assert.True(allocated < 1_500_000, "allocated " + allocated);
    }

    [Fact]
    [Covers("SG-R-CDC-05")]
    public void BadOptionsAreRefused()
    {
        foreach (long bad in new[] { 0L, 1023L, (1L << 30) + 1, -5 })
        {
            Assert.Equal(ErrorKind.InvalidOption, Assert.Throws<SpillgateException>(() => ZstdCodec.Create(ZstdOptions.Create(bad))).Kind);
        }

        _ = ZstdCodec.Create(ZstdOptions.Create(1024));
        _ = ZstdCodec.Create(ZstdOptions.Create(1 << 30));
        Assert.Equal(8L * 1024 * 1024, ZstdOptions.Default.MaxWindowBytes);
    }

    [Fact]
    [Covers("SG-R-CDC-05", "SG-R-CDC-08", "SG-I-05")]
    public void MutatedGoldenStreamsOnlyEverEndInOkOrCorrupt()
    {
        var rnd = new Random(99);
        foreach (string name in new[] { "small-text-l3-ck", "small-text-l1", "tiny", "text-l9-ck" })
        {
            (byte[] stream, _, string sha) = Golden(name);
            for (int round = 0; round < 600; round++)
            {
                byte[] mutated = (byte[])stream.Clone();
                for (int i = 0, flips = 1 + rnd.Next(3); i < flips; i++)
                {
                    mutated[rnd.Next(mutated.Length)] ^= (byte)(1 << rnd.Next(8));
                }

                Outcome o = T.Read(T.DecoderFor(4 << 20, l => l.WithoutRatioLimit()), mutated, 1 + rnd.Next(5000), "zstd");
                Assert.True(
                    o.Error is null || o.Kind is ErrorKind.Corrupt or ErrorKind.MemoryLimit or ErrorKind.OutputLimit,
                    name + " round " + round + ": " + o);
                if (o.Error is null && name.EndsWith("-ck", StringComparison.Ordinal))
                {
                    Assert.Equal(sha, T.Sha(o.Data)); // a content checksum accepts only the right data
                }
            }
        }
    }

    [Fact]
    [Covers("SG-I-01", "SG-R-LAY-03")]
    public void TheLimitIsExactForRleBombs()
    {
        byte[] bomb = Generators.ZstdZeros(10_000_000, 20);
        Outcome exact = T.Read(T.DecoderFor(10_000_000, l => l.WithoutRatioLimit()), bomb, 1 << 20, "zstd");
        Assert.Equal("ok", exact.Name);
        Outcome over = T.Read(T.DecoderFor(9_999_999, l => l.WithoutRatioLimit()), bomb, 1 << 20, "zstd");
        Assert.Equal("output_limit", over.Name);
        Assert.Equal(9_999_999, over.Data.Length);
    }
}
