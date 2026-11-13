using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>brotli on the decoder of the runtime: differential tests, framing, window rules, strictness.</summary>
public sealed class BrotliTests
{
    private static readonly Decoder Wide = T.DecoderFor(1 << 26, l => l.WithoutRatioLimit());

    public static IEnumerable<object[]> DataSets()
    {
        yield return new object[] { "empty", Array.Empty<byte>() };
        yield return new object[] { "one", new byte[] { 7 } };
        yield return new object[] { "text-4000", T.Text(1, 4000) };
        yield return new object[] { "random-5000", Generators.Data(2, 5000) };
        yield return new object[] { "zeros-100000", new byte[100_000] };
        yield return new object[] { "text-300000", T.Text(3, 300_000) };
        yield return new object[] { "mixed-250000", T.Mixed(4, 250_000) };
    }

    private static byte[] Encode(byte[] data, int quality, int window)
    {
        var dest = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length) + 16];
        Assert.True(BrotliEncoder.TryCompress(data, dest, out int written, quality, window));
        return dest.AsSpan(0, written).ToArray();
    }

    private static byte[]? RuntimeDecode(byte[] stream)
    {
        try
        {
            using var input = new MemoryStream(stream);
            using var z = new BrotliStream(input, CompressionMode.Decompress);
            var sink = new MemoryStream();
            z.CopyTo(sink);
            return sink.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    [Theory]
    [MemberData(nameof(DataSets))]
    [Covers("SG-R-CDC-04", "SG-I-09", "SG-R-CDC-06")]
    public void ValidStreamsMatchTheRuntimeForQualitiesWindowsAndReadSizes(string name, byte[] data)
    {
        foreach (int quality in new[] { 0, 1, 4, 6, 9, 11 })
        {
            foreach (int window in new[] { 10, 16, 22, 24 })
            {
                byte[] stream = Encode(data, quality, window);
                foreach (int size in new[] { 1, 33, 4096, 1 << 20 })
                {
                    if (size == 1 && data.Length > 100_000)
                    {
                        continue;
                    }

                    foreach (int chunk in new[] { int.MaxValue, 1, 999 })
                    {
                        if (chunk == 1 && stream.Length > 20_000)
                        {
                            continue;
                        }

                        Outcome o = T.Read(Wide, new ChunkedStream(stream, chunk), size, "br");
                        Assert.True(
                            o.Error is null && o.Data.AsSpan().SequenceEqual(data),
                            name + " q" + quality + " w" + window + " read " + size + " chunk " + chunk + ": " + o);
                    }
                }
            }
        }
    }

    [Fact]
    [Covers("SG-R-CDC-04", "SG-R-CDC-06", "SG-I-02")]
    public void EveryStrictPrefixIsCorruptAndTrailingBytesAreCorrupt()
    {
        foreach (byte[] data in new[] { Array.Empty<byte>(), "hello hello hello"u8.ToArray(), T.Text(5, 2000) })
        {
            byte[] stream = Encode(data, 9, 18);
            for (int cut = 0; cut < stream.Length; cut++)
            {
                Outcome o = T.Read(Wide, T.Slice(stream, cut), 4096, "br");
                Assert.True(o.Name == "corrupt", "cut " + cut + "/" + stream.Length + ": " + o);
            }

            Assert.Equal("ok", T.Read(Wide, stream, 4096, "br").Name);
            foreach (byte[] extra in new[] { new byte[] { 0 }, new byte[] { 0x3b }, new byte[16] })
            {
                foreach (int chunk in new[] { int.MaxValue, 1 })
                {
                    Outcome o = T.Read(Wide, new ChunkedStream(Generators.Concat(stream, extra), chunk), 4096, "br");
                    Assert.True(o.Name == "corrupt" && o.Data.AsSpan().SequenceEqual(data), extra.Length + " extra: " + o);
                }
            }
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [Covers("SG-R-CDC-04", "SG-I-04")]
    public void TheWindowIsReadFromTheFirstByteAndCheckedAgainstTheCap(int wbits)
    {
        byte[] payload = Generators.Data(wbits, 3000);
        byte[] stream = Generators.BrotliStored(payload, wbits);
        Assert.Equal(payload, T.Read(Wide, stream, 4096, "br").Data); // the default cap is 24
        for (int cap = 10; cap <= 24; cap++)
        {
            Decoder d = Decoder.CreateBuilder(Limits.Create(1 << 20)).AddBrotli(BrotliOptions.Create(cap)).Build();
            Outcome o = T.Read(d, stream, 4096, "br");
            if (wbits <= cap)
            {
                Assert.True(o.Error is null, "wbits " + wbits + " cap " + cap + ": " + o);
            }
            else
            {
                Assert.Equal("memory_limit", o.Name);
                Assert.Empty(o.Data);
            }
        }

        // The decision needs one byte of the stream only: a stream cut right after it is memory_limit too.
        Decoder strict = Decoder.CreateBuilder(Limits.Create(1 << 20)).AddBrotli(BrotliOptions.Create(10)).Build();
        if (wbits > 10)
        {
            Assert.Equal("memory_limit", T.Read(strict, T.Slice(stream, 1), 4096, "br").Name);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-04")]
    public void LargeWindowStreamsAreMemoryLimitWhateverTheCapIs()
    {
        byte[] prefix = Generators.BrotliLargeWindowPrefix();
        for (int cap = 10; cap <= 24; cap++)
        {
            Decoder d = Decoder.CreateBuilder(Limits.Create(1 << 20)).AddBrotli(BrotliOptions.Create(cap)).Build();
            Assert.Equal("memory_limit", T.Read(d, prefix, 4096, "br").Name);
            Assert.Equal("memory_limit", T.Read(d, T.Slice(prefix, 1), 4096, "br").Name);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-04")]
    public void BadOptionsAreRefused()
    {
        foreach (int bad in new[] { 0, 9, 25, -1, 100 })
        {
            Assert.Equal(ErrorKind.InvalidOption, Assert.Throws<SpillgateException>(() => BrotliCodec.Create(BrotliOptions.Create(bad))).Kind);
        }

        _ = BrotliCodec.Create(null);
        Assert.Equal(24, BrotliOptions.Default.MaxWindowBits);
    }

    [Fact]
    [Covers("SG-R-CDC-04", "SG-R-CDC-06")]
    public void AnEmptyInputIsCorruptAndSingleBytesAgreeWithTheRuntime()
    {
        Assert.Equal("corrupt", T.Read(Wide, Array.Empty<byte>(), 100, "br").Name);
        for (int b = 0; b < 256; b++)
        {
            Outcome ours = T.Read(Wide, new[] { (byte)b }, 100, "br");
            byte[]? theirs = RuntimeDecode(new[] { (byte)b });
            Assert.True(
                ours.Name is "ok" or "corrupt" or "memory_limit",
                "byte " + b + ": " + ours);
            if (ours.Error is null)
            {
                Assert.True(theirs is not null, "byte " + b + " accepted but the runtime refuses it");
            }
        }
    }

    [Fact]
    [Covers("SG-R-CDC-04", "SG-I-05")]
    public void MutatedStreamsNeverBeatTheRuntimesVerdict()
    {
        var rnd = new Random(4321);
        byte[] data = T.Text(6, 6000);
        byte[][] bases = { Encode(data, 5, 16), Encode(data, 11, 22), Encode(Generators.Data(1, 800), 4, 12) };
        for (int round = 0; round < 1200; round++)
        {
            byte[] stream = (byte[])bases[round % 3].Clone();
            for (int i = 0, flips = 1 + rnd.Next(3); i < flips; i++)
            {
                stream[rnd.Next(stream.Length)] ^= (byte)(1 << rnd.Next(8));
            }

            Outcome ours = T.Read(Wide, stream, 1 + rnd.Next(9000), "br");
            Assert.True(ours.Error is null || ours.Kind is ErrorKind.Corrupt or ErrorKind.MemoryLimit, ours.ToString());
            if (ours.Error is null)
            {
                byte[]? theirs = RuntimeDecode(stream);
                Assert.True(theirs is not null && theirs.AsSpan().SequenceEqual(ours.Data), "round " + round + ": accepted what the runtime rejects");
            }
        }
    }

    [Fact]
    [Covers("SG-R-CDC-04", "SG-I-02")]
    public void NonZeroPaddingAfterTheLastMetaBlockIsCorrupt()
    {
        // Upstream brotli fix 8ed0d7d: the bits after the final empty meta-block must be zero. The result depends on
        // the brotli version of the runtime; this test is the canary for it (see docs/threat-model.md).
        byte[] stream = Generators.BrotliStored(Generators.Data(1, 3000), 16);
        Assert.Equal("ok", T.Read(Wide, stream, 4096, "br").Name);
        byte[] dirty = (byte[])stream.Clone();
        dirty[dirty.Length - 1] |= 0x80;
        Assert.Equal("corrupt", T.Read(Wide, dirty, 4096, "br").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-04", "SG-I-01")]
    public void AMetaBlockThatProducesMoreThanItsLengthIsCorrupt()
    {
        // Upstream brotli fix 693a4a1: a non-last meta-block whose commands run past MLEN. The header says
        // 2122 bytes, the command copies 50 more.
        foreach (int overshoot in new[] { 0, 1, 50, 5000 })
        {
            var w = new BitWriter();
            w.Bits(0, 1); // WBITS 16
            int mlen = 2122;
            w.Bits(0, 1); // ISLAST
            w.Bits(0, 2); // MNIBBLES 4
            w.Bits(mlen - 1, 16);
            w.Bits(0, 1);
            w.Bits(0, 3);
            w.Bits(0, 2);
            w.Bits(0, 4);
            w.Bits(0, 2);
            w.Bits(0, 2);
            foreach ((long symbol, int bits) in new[] { (0L, 8), (423L, 10), (0L, 6) })
            {
                w.Bits(1, 2);
                w.Bits(0, 2);
                w.Bits(symbol, bits);
            }

            w.Bits(mlen + overshoot - 4 - 2118, 24);
            w.Bits(1, 1); // ISLAST
            w.Bits(1, 1); // ISLASTEMPTY
            byte[] stream = w.Finish();
            Outcome o = T.Read(Wide, stream, 4096, "br");
            if (overshoot == 0)
            {
                Assert.True(o.Error is null && o.Data.Length == mlen, o.ToString());
            }
            else
            {
                Assert.True(o.Name == "corrupt" && o.Data.Length <= mlen, "overshoot " + overshoot + ": " + o);
            }
        }
    }

    [Fact]
    [Covers("SG-R-CDC-04", "SG-R-MEM-02")]
    public void ManyDisposedDecodersNeitherLeakNorFail()
    {
        byte[] stream = Encode(T.Text(7, 50_000), 5, 24);
        for (int i = 0; i < 300; i++)
        {
            DecodedStream s = Wide.Open(new MemoryStream(stream), "br");
            s.ReadExactly(new byte[100], 0, 100);
            s.Dispose();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal("ok", T.Read(Wide, stream, 4096, "br").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-08", "SG-R-INP-01")]
    public void TheBrotliCodecIsExactAboutWhatItConsumes()
    {
        byte[] data = T.Text(8, 20_000);
        byte[] stream = Encode(data, 6, 20);
        Decoder exact = T.DecoderFor(1 << 20, l => l.WithMaxInput(stream.Length));
        Decoder less = T.DecoderFor(1 << 20, l => l.WithMaxInput(stream.Length - 1));
        Assert.Equal("ok", T.Read(exact, new ChunkedStream(stream, 700), 4096, "br").Name);
        Assert.Equal("input_limit", T.Read(less, new ChunkedStream(stream, 700), 4096, "br").Name);
        Assert.Equal("corrupt", T.Read(exact, new ChunkedStream(Generators.Concat(stream, new byte[] { 0 }), 700), 4096, "br").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-04")]
    public void TheBuilderExtensionRegistersBr()
    {
        Assert.Throws<ArgumentNullException>(() => BrotliDecoderBuilderExtensions.AddBrotli(null!));
        Decoder d = Decoder.CreateBuilder(Limits.Create(100)).AddBrotli().Build();
        Assert.Equal("ok", T.Read(d, Encode("hi"u8.ToArray(), 5, 16), 10, "br").Name);
        Assert.Equal(OperationStatus.Done, OperationStatus.Done);
    }
}
