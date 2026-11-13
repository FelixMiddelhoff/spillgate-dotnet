using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>deflate in all its forms: the runtime as an oracle for valid streams, hand-built streams for the rest.</summary>
public sealed class DeflateTests
{
    private static readonly Decoder Wide = T.DecoderFor(64 << 20, l => l.WithRatioGrace(long.MaxValue / 4));

    public static IEnumerable<object[]> DataSets()
    {
        yield return new object[] { "empty", Array.Empty<byte>() };
        yield return new object[] { "one", new byte[] { 42 } };
        yield return new object[] { "two", new byte[] { 1, 2 } };
        yield return new object[] { "text-100", T.Text(1, 100) };
        yield return new object[] { "text-5000", T.Text(2, 5000) };
        yield return new object[] { "random-3000", Generators.Data(3, 3000) };
        yield return new object[] { "zeros-70000", new byte[70000] };
        yield return new object[] { "text-300000", T.Text(4, 300000) };
        yield return new object[] { "mixed-200000", T.Mixed(5, 200000) };
        yield return new object[] { "random-100000", Generators.Data(6, 100000) };
    }

    [Theory]
    [MemberData(nameof(DataSets))]
    [Covers("SG-R-CDC-03", "SG-R-CDC-02", "SG-I-09")]
    public void ValidStreamsMatchTheRuntimeForEveryLevelAndReadSize(string name, byte[] data)
    {
        foreach (CompressionLevel level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            var forms = new (string Token, byte[] Stream)[]
            {
                ("gzip", T.Gzip(data, level)),
                ("deflate", T.ZLib(data, level)),
                ("deflate", T.RawDeflate(data, level)),
            };
            foreach ((string token, byte[] stream) in forms)
            {
                if (token == "deflate" && stream.Length >= 2 && Inflater.IsZlibHeader(stream[0], stream[1]) && stream != forms[1].Stream)
                {
                    continue; // a raw stream that looks like zlib is corrupt by SG-R-CDC-03; covered separately
                }

                foreach (int size in new[] { 1, 7, 4096, 1 << 20 })
                {
                    if (size == 1 && data.Length > 70000)
                    {
                        continue;
                    }

                    foreach (int[] chunks in new[] { Array.Empty<int>(), new[] { 1 }, new[] { 3, 1000, 17 } })
                    {
                        if (chunks.Length == 1 && stream.Length > 80000)
                        {
                            continue;
                        }

                        Outcome o = T.Read(Wide, new ChunkedStream(stream, chunks), size, token);
                        Assert.True(
                            o.Error is null && o.Data.AsSpan().SequenceEqual(data),
                            name + " " + level + " " + token + " read " + size + ": " + o);
                    }
                }
            }
        }
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void AFixedBlockWithAMatchDecodes()
    {
        byte[] plain = "abcabcabc"u8.ToArray();
        byte[] deflate = DeflateKit.Fixed(true).Literals("abc").Match(6, 3).EndOfBlock().Finish();
        Assert.Equal(plain, T.Read(Wide, DeflateKit.AsGzip(deflate, plain), 4, "gzip").Data);
        Assert.Equal(plain, T.Read(Wide, DeflateKit.AsZlib(deflate, plain), 1, "deflate").Data);
        Assert.Equal(plain, T.Read(Wide, deflate, 2, "deflate").Data);
    }

    [Fact]
    [Covers("SG-R-CDC-03", "SG-I-09")]
    public void OverlappingMatchesAndHistoryAcrossStepsAreExactForAnyReadSize()
    {
        // 40000 random bytes, a match back to the very start window edge, an overlapping run, and a long copy.
        byte[] head = Generators.Data(9, 40000);
        var expected = new List<byte>(head);
        var stored = DeflateKit.Stored(head, final: false);
        var kit = DeflateKit.Fixed(true);
        kit.Match(258, 32768);
        for (int i = 0; i < 258; i++)
        {
            expected.Add(expected[expected.Count - 32768]);
        }

        kit.Match(258, 1);
        for (int i = 0; i < 258; i++)
        {
            expected.Add(expected[expected.Count - 1]);
        }

        kit.Match(100, 7);
        for (int i = 0; i < 100; i++)
        {
            expected.Add(expected[expected.Count - 7]);
        }

        kit.Match(3, 32768);
        for (int i = 0; i < 3; i++)
        {
            expected.Add(expected[expected.Count - 32768]);
        }

        kit.EndOfBlock();
        byte[] deflate = Generators.Concat(stored, kit.Finish());
        // The stored block is byte aligned; the fixed block follows in the next byte.
        byte[] plain = expected.ToArray();
        foreach (int size in new[] { 1, 5, 258, 1000, 65536 })
        {
            Outcome o = T.Read(Wide, DeflateKit.AsGzip(deflate, plain), size, "gzip");
            Assert.True(o.Error is null && o.Data.AsSpan().SequenceEqual(plain), "read " + size + ": " + o);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-03", "SG-R-CDC-06")]
    public void AMatchBeyondTheProducedBytesIsCorrupt()
    {
        byte[] first = DeflateKit.Fixed(true).Match(3, 1).EndOfBlock().Finish();
        Assert.Equal("corrupt", T.Read(Wide, first, 100, "deflate").Name);

        byte[] ok = DeflateKit.Fixed(true).Literals("0123456789").Match(5, 10).EndOfBlock().Finish();
        Assert.Equal("ok", T.Read(Wide, ok, 100, "deflate").Name);
        byte[] bad = DeflateKit.Fixed(true).Literals("0123456789").Match(5, 11).EndOfBlock().Finish();
        Assert.Equal("corrupt", T.Read(Wide, bad, 100, "deflate").Name);

        // A gzip member does not see the previous member's bytes: the window is per stream.
        byte[] member1 = DeflateKit.AsGzip(DeflateKit.Stored(new byte[100], true), new byte[100]);
        byte[] deflate2 = DeflateKit.Fixed(true).Match(3, 1).EndOfBlock().Finish();
        byte[] member2 = DeflateKit.AsGzip(deflate2, new byte[3]);
        Assert.Equal("corrupt", T.Read(Wide, Generators.Concat(member1, member2), 4096, "gzip").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void InvalidSymbolsAreCorrupt()
    {
        // Length symbols 286 and 287 and distance symbols 30 and 31 exist in the fixed code but never occur.
        byte[] l286 = DeflateKit.Fixed(true).Literals("abc").Lit(286).EndOfBlock().Finish();
        byte[] l287 = DeflateKit.Fixed(true).Literals("abc").Lit(287).EndOfBlock().Finish();
        byte[] d30 = DeflateKit.Fixed(true).Literals("abc").Lit(257).DistSymbol(30).EndOfBlock().Finish();
        byte[] d31 = DeflateKit.Fixed(true).Literals("abc").Lit(257).DistSymbol(31).EndOfBlock().Finish();
        foreach (byte[] stream in new[] { l286, l287, d30, d31 })
        {
            Assert.Equal("corrupt", T.Read(Wide, stream, 100, "deflate").Name);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void BlockTypeThreeAndBrokenStoredHeadersAreCorrupt()
    {
        Assert.Equal("corrupt", T.Read(Wide, new byte[] { 0x07, 0, 0, 0 }, 100, "deflate").Name);

        var w = new BitWriter();
        w.Bits(1, 1);
        w.Bits(0, 2);
        w.Align();
        w.Bits(5, 16);
        w.Bits(5, 16); // NLEN should be the complement
        w.Raw(new byte[5], 0, 5);
        Assert.Equal("corrupt", T.Read(Wide, w.Finish(), 100, "deflate").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void DynamicHeaderViolationsAreCorrupt()
    {
        int[] good = DeflateKit.CompleteLiteralLengths();
        int[] distGood = DeflateKit.Fill(2, 1);
        Assert.Equal(
            "ok",
            T.Read(Wide, DeflateKit.Dynamic(true, good, distGood).Literals("hi").EndOfBlock().Finish(), 100, "deflate").Name);

        // Over-subscribed literal code: three symbols of length 1.
        int[] over = new int[286];
        over[0] = over[1] = over[256] = 1;
        Assert.Equal("corrupt", T.Read(Wide, DeflateKit.Dynamic(true, over, distGood).EndOfBlock().Finish(), 100, "deflate").Name);

        // Incomplete literal code: two symbols of length 2.
        int[] incomplete = new int[286];
        incomplete[0] = incomplete[256] = 2;
        Assert.Equal("corrupt", T.Read(Wide, DeflateKit.Dynamic(true, incomplete, distGood).EndOfBlock().Finish(), 100, "deflate").Name);

        // Missing end-of-block code.
        int[] noEob = new int[286];
        noEob[0] = noEob[1] = 1;
        Assert.Equal("corrupt", T.Read(Wide, DeflateKit.Dynamic(true, noEob, distGood).Finish(), 100, "deflate").Name);

        // Too many length codes (HLIT = 287) or distance codes (HDIST = 31 symbols).
        int[] tooManyLit = new int[287];
        Array.Copy(good, tooManyLit, 286);
        tooManyLit[286] = 8;
        Assert.Equal("corrupt", T.Read(Wide, DeflateKit.Dynamic(true, tooManyLit, distGood).EndOfBlock().Finish(), 100, "deflate").Name);
        Assert.Equal("corrupt", T.Read(Wide, DeflateKit.Dynamic(true, good, DeflateKit.Fill(31, 5)).EndOfBlock().Finish(), 100, "deflate").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void AnIncompleteDistanceCodeIsOnlyAcceptedAsASingleOneBitCode()
    {
        int[] lit = DeflateKit.CompleteLiteralLengths();
        // One distance code of length 1 (zlib accepts it): a match with distance symbol 0 works.
        byte[] one = DeflateKit.Dynamic(true, lit, new[] { 1 }).Literals("ab").Match(4, 1).EndOfBlock().Finish();
        Assert.Equal("abbbbb"u8.ToArray(), T.Read(Wide, one, 100, "deflate").Data);
        // No distance codes at all is valid as long as no match is used.
        byte[] none = DeflateKit.Dynamic(true, lit, new[] { 0 }).Literals("ab").EndOfBlock().Finish();
        Assert.Equal("ab"u8.ToArray(), T.Read(Wide, none, 100, "deflate").Data);
        // Two distance codes of length 2 are incomplete and refused.
        byte[] two = DeflateKit.Dynamic(true, lit, new[] { 2, 2 }).Literals("ab").EndOfBlock().Finish();
        Assert.Equal("corrupt", T.Read(Wide, two, 100, "deflate").Name);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("zlib")]
    [InlineData("raw")]
    [Covers("SG-R-CDC-06", "SG-R-CDC-03", "SG-R-CDC-02", "SG-I-02")]
    public void EveryStrictPrefixOfAValidStreamIsCorrupt(string form)
    {
        foreach (byte[] data in new[] { Array.Empty<byte>(), "hello hello hello"u8.ToArray(), T.Text(11, 3000), Generators.Data(12, 700) })
        {
            byte[] stream = form switch
            {
                "gzip" => T.Gzip(data),
                "zlib" => T.ZLib(data),
                _ => T.RawDeflate(data),
            };
            string token = form == "gzip" ? "gzip" : "deflate";
            for (int cut = 0; cut < stream.Length; cut++)
            {
                Outcome o = T.Read(Wide, T.Slice(stream, cut), 4096, token);
                Assert.True(o.Name == "corrupt", form + " cut at " + cut + "/" + stream.Length + ": " + o);
            }

            Assert.Equal("ok", T.Read(Wide, stream, 4096, token).Name);
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("zlib")]
    [InlineData("raw")]
    [Covers("SG-R-CDC-02", "SG-R-CDC-03", "SG-R-CDC-06")]
    public void BytesAfterTheEndOfTheStreamAreCorrupt(string form)
    {
        byte[] data = T.Text(13, 500);
        byte[] stream = form switch
        {
            "gzip" => T.Gzip(data),
            "zlib" => T.ZLib(data),
            _ => T.RawDeflate(data),
        };
        string token = form == "gzip" ? "gzip" : "deflate";
        foreach (byte[] extra in new[] { new byte[] { 0 }, new byte[] { 0x1f }, new byte[16], new byte[] { 0xff, 0xff } })
        {
            foreach (int chunk in new[] { 1, 100, int.MaxValue })
            {
                Outcome o = T.Read(Wide, new ChunkedStream(Generators.Concat(stream, extra), chunk), 4096, token);
                Assert.True(o.Name == "corrupt" && o.Data.AsSpan().SequenceEqual(data), form + " + " + extra.Length + ": " + o);
            }
        }
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void ARawStreamThatLooksLikeZlibIsCorruptAndNeverMisreadAsData()
    {
        // Bytes 0x78 0x01 are a valid zlib header and also the start of a stored raw block (header bits in 0x78, LEN 0x0001...).
        byte[] raw = { 0x78, 0x01, 0x00, 0xfe, 0xff, 0x41, 0x01, 0x00, 0x00, 0xff, 0xff };
        Outcome o = T.Read(Wide, raw, 100, "deflate");
        Assert.Equal("corrupt", o.Name);
        Assert.Empty(o.Data);
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void StrictZlibRefusesRawDeflateAndPresetDictionaries()
    {
        Decoder strict = T.DecoderFor(1 << 20, strictZlib: true);
        byte[] data = T.Text(14, 300);
        Assert.Equal(data, T.Read(strict, T.ZLib(data), 100, "deflate").Data);
        Assert.Equal("corrupt", T.Read(strict, T.RawDeflate(data), 100, "deflate").Name);
        Assert.Equal(data, T.Read(Wide, T.RawDeflate(data), 100, "deflate").Data);

        // FDICT set: corrupt, and not retried as raw.
        byte[] zlib = T.ZLib(data);
        byte[] withDict = (byte[])zlib.Clone();
        withDict[0] = 0x78;
        int fcheck = (31 - (((0x78 << 8) | 0x20) % 31)) % 31;
        withDict[1] = (byte)(0x20 | fcheck);
        Assert.True(Inflater.IsZlibHeader(withDict[0], withDict[1]));
        Assert.Equal("corrupt", T.Read(Wide, withDict, 100, "deflate").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-03")]
    public void TheAdlerChecksumIsVerified()
    {
        byte[] data = T.Text(15, 400);
        byte[] zlib = T.ZLib(data);
        zlib[zlib.Length - 1] ^= 1;
        Outcome o = T.Read(Wide, zlib, 4096, "deflate");
        Assert.Equal("corrupt", o.Name);
        Assert.Equal(data, o.Data); // the data was delivered before the trailer failed; the error is sticky
    }

    [Fact]
    [Covers("SG-R-CDC-07", "SG-I-04", "SG-R-CDC-02")]
    public void DeflateStoredBlocksOfAnySizeAreBoundedByTheOutputLimit()
    {
        byte[] data = new byte[200_000];
        Decoder small = T.DecoderFor(100_000);
        byte[] big = Generators.GzipStored(data);
        Outcome limited = T.Read(small, big, 4096, "gzip");
        Assert.Equal("output_limit", limited.Name);
        Assert.Equal(100_000, limited.Data.Length);
    }

    [Fact]
    [Covers("SG-R-CDC-03", "SG-I-05")]
    public void ReadSizesAroundTheStepSizeGiveTheSameBytes()
    {
        byte[] data = T.Mixed(21, 400_000);
        byte[] stream = T.ZLib(data);
        foreach (int size in new[] { 65535, 65536, 65537, 131072, 1 << 20 })
        {
            Outcome o = T.Read(Wide, stream, size, "deflate");
            Assert.True(o.Error is null && o.Data.AsSpan().SequenceEqual(data), "read " + size);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-03", "SG-R-RAT-02")]
    public void ConsumedBytesAreExactAtTheEndOfTheStream()
    {
        // With MaxInput equal to the stream length, the stream passes; one less fails: the count is exact
        // (no look-ahead is charged).
        byte[] data = T.Text(31, 20_000);
        foreach (byte[] stream in new[] { T.Gzip(data), T.ZLib(data), T.RawDeflate(data) })
        {
            string token = stream[0] == 0x1f ? "gzip" : "deflate";
            if (token == "deflate" && !Inflater.IsZlibHeader(stream[0], stream[1]))
            {
                continue;
            }

            Decoder exact = T.DecoderFor(1 << 20, l => l.WithMaxInput(stream.Length));
            Decoder less = T.DecoderFor(1 << 20, l => l.WithMaxInput(stream.Length - 1));
            Assert.Equal("ok", T.Read(exact, new ChunkedStream(stream, 1000), 4096, token).Name);
            Assert.Equal("input_limit", T.Read(less, new ChunkedStream(stream, 1000), 4096, token).Name);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-03", "SG-I-05")]
    public void MutatedStreamsNeverDeliverWrongDataThatTheRuntimeRejects()
    {
        // Differential: whatever Spillgate accepts, the runtime accepts with identical bytes.
        var rnd = new Random(1234);
        byte[] data = T.Text(41, 4000);
        byte[][] bases = { T.Gzip(data), T.ZLib(data), T.RawDeflate(data) };
        for (int round = 0; round < 1500; round++)
        {
            byte[] stream = (byte[])bases[round % 3].Clone();
            int flips = 1 + rnd.Next(3);
            for (int i = 0; i < flips; i++)
            {
                stream[rnd.Next(stream.Length)] ^= (byte)(1 << rnd.Next(8));
            }

            string token = round % 3 == 0 ? "gzip" : "deflate";
            Outcome ours = T.Read(Wide, stream, 4096, token);
            Assert.True(ours.Error is null || ours.Kind == ErrorKind.Corrupt, ours.ToString());
            if (ours.Error is null)
            {
                byte[]? theirs = RuntimeDecode(stream, round % 3);
                Assert.True(theirs is not null && theirs.AsSpan().SequenceEqual(ours.Data), "round " + round + ": accepted what the runtime rejects or reads differently");
            }
        }
    }

    private static byte[]? RuntimeDecode(byte[] stream, int form)
    {
        try
        {
            using var input = new MemoryStream(stream);
            using Stream z = form switch
            {
                0 => new GZipStream(input, CompressionMode.Decompress),
                1 => new ZLibStream(input, CompressionMode.Decompress),
                _ => new DeflateStream(input, CompressionMode.Decompress),
            };
            var sink = new MemoryStream();
            z.CopyTo(sink);
            return sink.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
