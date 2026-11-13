using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>The reader contract (SPEC section 9) and the layer rules (section 5) from the outside.</summary>
public sealed class ReaderContractTests
{
    private static readonly byte[] Data = T.Mixed(77, 150_000);

    private static (byte[] Stream, string[] Encodings) Build(string kind)
    {
        switch (kind)
        {
            case "gzip": return (T.Gzip(Data), new[] { "gzip" });
            case "zlib": return (T.ZLib(Data), new[] { "deflate" });
            case "br": return (T.Brotli(Data), new[] { "br" });
            case "zstd": return (Generators.ZstdStored(Data, 20), new[] { "zstd" });
            case "identity": return (Data, Array.Empty<string>());
            case "gzip+zlib": return (T.ZLib(T.Gzip(Data)), new[] { "gzip", "deflate" });
            case "br+gzip": return (T.Gzip(T.Brotli(Data)), new[] { "br", "gzip" });
            default: return (Generators.ZstdStored(T.Brotli(T.ZLib(Data)), 18), new[] { "deflate", "br", "zstd" });
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("zlib")]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("identity")]
    [InlineData("gzip+zlib")]
    [InlineData("br+gzip")]
    [InlineData("zstd+br+zlib")]
    [Covers("SG-R-RDR-01", "SG-I-09", "SG-I-01")]
    public void TheDecodedBytesAndTheOutcomeDoNotDependOnReadSizesOrSourceChunks(string kind)
    {
        (byte[] stream, string[] encodings) = Build(kind);
        Decoder decoder = T.DecoderFor(1 << 22);
        var patterns = new[]
        {
            new[] { 1 }, new[] { 2 }, new[] { 3, 5, 7 }, new[] { 64 }, new[] { 4096 }, new[] { 65535 }, new[] { 65537 },
            new[] { 1 << 20 }, new[] { 1, 1 << 20, 1, 4096 },
        };
        var chunkings = new[] { Array.Empty<int>(), new[] { 1 }, new[] { 3 }, new[] { 4095, 1, 17 }, new[] { 70_000 } };
        foreach (int[] pattern in patterns)
        {
            foreach (int[] chunks in chunkings)
            {
                if ((pattern.Max() <= 3 && stream.Length > 100_000 && chunks.Length > 0 && chunks[0] <= 3) || (pattern[0] == 1 && stream.Length > 100_000 && pattern.Length == 1 && chunks.Length > 0))
                {
                    continue; // keep the run time sensible: single-byte reads over a single-byte source
                }

                Outcome o = T.ReadPattern(decoder.Open(new ChunkedStream(stream, chunks), encodings), pattern);
                Assert.True(o.Error is null && o.Data.AsSpan().SequenceEqual(Data), kind + " pattern " + string.Join(",", pattern) + " chunks " + string.Join(",", chunks) + ": " + o);
            }
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("zlib")]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("identity")]
    [InlineData("gzip+zlib")]
    [Covers("SG-R-RDR-01", "SG-I-09", "SG-I-01", "SG-R-LAY-03", "SG-R-LAY-07")]
    public void FailuresDeliverTheSamePrefixForEveryReadSize(string kind)
    {
        (byte[] stream, string[] encodings) = Build(kind);
        // One limit, one truncation and one trailing byte: output_limit, corrupt, corrupt.
        var variants = new List<(string Name, byte[] Stream, long Limit)>
        {
            ("limit", stream, 70_001),
            ("truncated", T.Slice(stream, stream.Length * 2 / 3), 1 << 22),
            ("trailing", Generators.Concat(stream, new byte[] { 0x42 }), 1 << 22),
        };
        foreach ((string name, byte[] bytes, long limit) in variants)
        {
            if (kind == "identity" && name != "limit")
            {
                continue; // unencoded data has no end marker to be cut off or followed
            }

            Decoder decoder = T.DecoderFor(limit);
            Outcome reference = T.ReadPattern(decoder.Open(new MemoryStream(bytes), encodings), new[] { 1 << 20 });
            Assert.NotNull(reference.Error);
            foreach (int[] pattern in new[] { new[] { 1 }, new[] { 7 }, new[] { 4096 }, new[] { 1, 3, 60000 } })
            {
                Outcome o = T.ReadPattern(decoder.Open(new MemoryStream(bytes), encodings), pattern);
                Assert.Equal(reference.Name, o.Name);
                Assert.True(o.Data.AsSpan().SequenceEqual(reference.Data), kind + " " + name + " pattern " + string.Join(",", pattern) + ": " + o.Data.Length + " vs " + reference.Data.Length);
                Assert.True(o.Data.AsSpan().SequenceEqual(Data.AsSpan(0, o.Data.Length)), "delivered bytes must be a prefix of the decoded stream");
            }
        }
    }

    [Fact]
    [Covers("SG-R-RDR-02")]
    public void AZeroLengthReadConsumesNothingAndChangesNothing()
    {
        byte[] stream = T.Gzip(Data);
        var source = new ChunkedStream(stream);
        using DecodedStream s = T.DecoderFor(1 << 22).Open(source, "gzip");
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, s.Read(Array.Empty<byte>(), 0, 0));
            Assert.Equal(0, s.Read(Span<byte>.Empty));
        }

        Assert.Equal(0, source.Reads);
        Outcome o = T.ReadAll(s, 4096);
        Assert.Equal(Data, o.Data);

        // After the clean end and after an error, a zero-length read does not alter the state.
        Assert.Equal(0, s.Read(Array.Empty<byte>(), 0, 0));
        Assert.Equal(0, s.Read(new byte[8], 0, 8));

        using DecodedStream failing = T.DecoderFor(10).Open(new MemoryStream(Data), Array.Empty<string>());
        Assert.Throws<SpillgateException>(() => T.ReadAll(failing, 100).Error is { } e ? throw e : 0);
        Assert.Throws<SpillgateException>(() => failing.Read(Array.Empty<byte>(), 0, 0)); // MAY return the terminal condition
    }

    [Fact]
    [Covers("SG-R-RDR-03")]
    public void ASourceThatAnswersZeroIsAtItsEndBecauseStreamReadHasNoNoProgressState()
    {
        // System.IO.Stream.Read returns 0 only at the end of the stream, so the "0 bytes without error 100 times"
        // situation of the specification cannot be expressed. Zero is the end: an empty gzip input is corrupt,
        // an empty identity input is a clean empty body, and the codec is never polled again.
        var zero = new ChunkedStream(Array.Empty<byte>());
        Assert.Equal("corrupt", T.Read(T.DecoderFor(100), zero, 100, "gzip").Name);
        Assert.Equal(1, zero.Reads);
        Assert.Equal("ok", T.Read(T.DecoderFor(100), new ChunkedStream(Array.Empty<byte>()), 100).Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(1000)]
    [Covers("SG-R-RDR-04", "SG-I-05")]
    public void ASourceThatReportsMoreThanRequestedOrANegativeCountIsInternal(int answer)
    {
        foreach (string[] encodings in new[] { new[] { "gzip" }, Array.Empty<string>(), new[] { "br" } })
        {
            Outcome o = T.Read(T.DecoderFor(100), new LyingStream(answer), 100, encodings);
            Assert.Equal("internal", o.Name);
            Assert.Equal(1, ((SpillgateException)o.Error!).Layer);
        }
    }

    [Fact]
    [Covers("SG-R-RDR-05", "SG-I-02")]
    public void ASourceErrorIsReportedAsSuchWithTheOriginalExceptionNotAsCorrupt()
    {
        byte[] stream = T.Gzip(Data);
        var original = new IOException("the network went away");
        foreach (int after in new[] { 0, 5, stream.Length / 2, stream.Length - 1 })
        {
            Outcome o = T.Read(T.DecoderFor(1 << 22), new FailingStream(stream, after, original, chunk: 1000), 4096, "gzip");
            var e = Assert.IsType<SpillgateException>(o.Error);
            Assert.Equal(ErrorKind.SourceError, e.Kind);
            Assert.Same(original, e.InnerException);
        }

        // An early end of the source, by contrast, is corrupt (SG-R-CDC-06).
        Assert.Equal("corrupt", T.Read(T.DecoderFor(1 << 22), T.Slice(stream, 100), 4096, "gzip").Name);

        // Any exception type counts, and a typed exception of a lower layer passes through unchanged.
        Outcome other = T.Read(T.DecoderFor(100), new FailingStream(stream, 3, new InvalidOperationException("x")), 100, "gzip");
        Assert.Equal(ErrorKind.SourceError, ((SpillgateException)other.Error!).Kind);
        Outcome chain = T.Read(T.DecoderFor(100_000), new FailingStream(T.Gzip(T.Gzip(Data)), 40, original), 100, "gzip", "gzip");
        Assert.Equal(ErrorKind.SourceError, ((SpillgateException)chain.Error!).Kind);
        Assert.Equal(1, ((SpillgateException)chain.Error!).Layer);
    }

    [Fact]
    [Covers("SG-R-RDR-06", "SG-I-10")]
    public void DisposeIsIdempotentAndDoesNotCloseTheCallersSource()
    {
        var source = new ChunkedStream(T.Gzip(Data));
        DecodedStream s = T.DecoderFor(1 << 22).Open(source, "gzip");
        s.ReadExactly(new byte[10], 0, 10);
        s.Dispose();
        s.Dispose();
        s.DisposeAsync().AsTask().Wait();
        Assert.False(source.Disposed);
        Assert.Throws<ObjectDisposedException>(() => s.Read(new byte[10], 0, 10));
        Assert.Throws<ObjectDisposedException>(() => s.ReadByte());
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await s.ReadExactlyAsync(new byte[10])).Wait();
        Assert.Equal(1, source.Reads);

        // After the clean end the source is untouched too.
        var source2 = new ChunkedStream(T.Gzip(Data));
        using (DecodedStream s2 = T.DecoderFor(1 << 22).Open(source2, "gzip"))
        {
            T.ReadAll(s2, 8192);
        }

        Assert.False(source2.Disposed);
    }

    [Fact]
    [Covers("SG-R-RDR-06")]
    public void LeaveOpenFalseIsTheOnlyWayTheSourceIsClosed()
    {
        var source = new ChunkedStream(T.Gzip(Data));
        DecodedStream s = T.DecoderFor(1 << 22).Open(source, new[] { "gzip" }, leaveOpen: false);
        T.ReadAll(s, 8192);
        Assert.False(source.Disposed);
        s.Dispose();
        Assert.True(source.Disposed);
    }

    [Fact]
    [Covers("SG-R-RDR-06", "SG-I-10")]
    public void ADisposedStreamReadsNothingMoreFromTheSource()
    {
        var source = new ChunkedStream(Data, 100);
        DecodedStream s = T.DecoderFor(1 << 22).Open(source);
        s.ReadExactly(new byte[10], 0, 10);
        int reads = source.Reads;
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Read(new byte[10], 0, 10));
        Assert.Equal(reads, source.Reads);
    }

    [Fact]
    [Covers("SG-R-RDR-07", "SG-I-01")]
    public void OneDecoderServesManyThreadsAtOnce()
    {
        Decoder decoder = T.DecoderFor(1 << 22);
        byte[][] streams = Enumerable.Range(0, 8).Select(i => T.Gzip(T.Text(100 + i, 40_000 + (i * 1000)))).ToArray();
        byte[][] expected = Enumerable.Range(0, 8).Select(i => T.Text(100 + i, 40_000 + (i * 1000))).ToArray();
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            int k = i % 8;
            Outcome o = T.Read(decoder, new ChunkedStream(streams[k], 1 + (i % 50)), 1 + ((i * 37) % 9000), "gzip");
            Assert.True(o.Error is null && o.Data.AsSpan().SequenceEqual(expected[k]), "thread run " + i + ": " + o);
        });
    }

    [Fact]
    [Covers("SG-R-RDR-07", "SG-I-10")]
    public void DisposingFromAnotherThreadWhileReadingNeverCrashes()
    {
        Decoder decoder = T.DecoderFor(1L << 40, l => l.WithoutRatioLimit());
        for (int round = 0; round < 30; round++)
        {
            DecodedStream s = decoder.Open(new EndlessStream(), Array.Empty<string>());
            var failures = new List<Exception>();
            Task reader = Task.Run(() =>
            {
                var buf = new byte[4096];
                try
                {
                    while (s.Read(buf, 0, buf.Length) > 0)
                    {
                    }
                }
                catch (Exception e) when (e is ObjectDisposedException or SpillgateException)
                {
                    // the only acceptable endings
                }
                catch (Exception e)
                {
                    lock (failures)
                    {
                        failures.Add(e);
                    }
                }
            });
            Task.Delay(round % 7).Wait();
            s.Dispose();
            Assert.True(reader.Wait(10_000), "the reader hangs after dispose");
            Assert.Empty(failures);
        }
    }

    [Fact]
    [Covers("SG-R-LAY-05", "SG-I-03", "SG-R-LAY-04")]
    public async Task ErrorsAndTheCleanEndAreStickyOnBothPaths()
    {
        Decoder decoder = T.DecoderFor(1000);
        foreach (bool useAsync in new[] { false, true })
        {
            DecodedStream bad = decoder.Open(new MemoryStream(Data), Array.Empty<string>());
            var first = await Capture(bad, useAsync);
            Assert.Equal(ErrorKind.OutputLimit, first.Kind);
            for (int i = 0; i < 5; i++)
            {
                var again = await Capture(bad, useAsync);
                Assert.Equal(ErrorKind.OutputLimit, again.Kind);
                Assert.Equal(0, again.Delivered);
            }

            DecodedStream good = decoder.Open(new MemoryStream(new byte[10]), Array.Empty<string>());
            Assert.Equal(10, T.ReadAll(good, 100).Data.Length);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(0, useAsync ? await good.ReadAsync(new byte[5]) : good.Read(new byte[5], 0, 5));
            }
        }
    }

    private static async Task<(ErrorKind? Kind, int Delivered)> Capture(DecodedStream s, bool useAsync)
    {
        int total = 0;
        try
        {
            for (; ; )
            {
                int n = useAsync ? await s.ReadAsync(new byte[300]) : s.Read(new byte[300], 0, 300);
                if (n == 0)
                {
                    return (null, total);
                }

                total += n;
            }
        }
        catch (SpillgateException e)
        {
            return (e.Kind, total);
        }
    }

    [Fact]
    [Covers("SG-R-LAY-04", "SG-I-02")]
    public void ALimitHitIsAnErrorNeverAnEndOfStream()
    {
        Decoder decoder = T.DecoderFor(100);
        byte[] big = T.Gzip(new byte[10_000]);
        DecodedStream s = decoder.Open(new MemoryStream(big), "gzip");
        var buf = new byte[1000];
        int total = 0;
        int n;
        try
        {
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
            }

            Assert.Fail("a limit hit ended the stream cleanly after " + total + " bytes");
        }
        catch (SpillgateException e)
        {
            Assert.Equal(ErrorKind.OutputLimit, e.Kind);
            Assert.Equal(100, total);
        }
    }

    [Fact]
    [Covers("SG-R-LAY-06", "SG-R-API-02")]
    public void StreamingMayDeliverBeforeAnErrorButDecodeAllNeverReturnsPartialData()
    {
        byte[] stream = T.Slice(T.Gzip(Data), 5000);
        Outcome streamed = T.Read(T.DecoderFor(1 << 22), stream, 4096, "gzip");
        Assert.Equal("corrupt", streamed.Name);
        Assert.NotEmpty(streamed.Data);

        Assert.Throws<SpillgateException>(() => T.DecoderFor(1 << 22).DecodeAll(new MemoryStream(stream), "gzip"));
        Assert.Throws<SpillgateException>(() => T.DecoderFor(100).DecodeAll(new MemoryStream(T.Gzip(Data)), "gzip"));
        Assert.Equal(Data, T.DecoderFor(1 << 22).DecodeAll(new MemoryStream(T.Gzip(Data)), "gzip"));
        Assert.Empty(T.DecoderFor(1 << 22).DecodeAll(new MemoryStream(Array.Empty<byte>())));
    }

    [Fact]
    [Covers("SG-R-API-02", "SG-R-MEM-02")]
    public async Task DecodeAllGrowsFromWhatWasProducedNeverFromTheLimit()
    {
        // MaxOutput is 2^62, the body is tiny: a preallocation from the limit would throw or exhaust memory.
        Decoder decoder = T.DecoderFor(1L << 62, l => l.WithoutRatioLimit());
        byte[] data = T.Text(5, 10_000);
        Assert.Equal(data, decoder.DecodeAll(new MemoryStream(T.Gzip(data)), "gzip"));
        Assert.Equal(data, await decoder.DecodeAllAsync(new MemoryStream(T.Gzip(data)), new[] { "gzip" }));
        long before = GC.GetAllocatedBytesForCurrentThread();
        decoder.DecodeAll(new MemoryStream(T.Gzip(data)), "gzip");
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4_000_000);
    }

    [Fact]
    [Covers("SG-R-API-03", "SG-R-API-05", "SG-R-LAY-01")]
    public void FailuresCarryTheirKindTheLayerAndTheLimit()
    {
        Decoder decoder = T.DecoderFor(1000, l => l.WithMaxLayers(3).WithMaxInput(500));
        Outcome output = T.Read(T.DecoderFor(1000), new MemoryStream(Data), 100);
        var e1 = Assert.IsType<SpillgateException>(output.Error);
        Assert.Equal((ErrorKind.OutputLimit, 1, 1000L), (e1.Kind, e1.Layer, e1.Limit));
        Assert.StartsWith("spillgate: output_limit in layer 1 (limit 1000)", e1.Message, StringComparison.Ordinal);

        var layer = Assert.Throws<SpillgateException>(() => decoder.Open(new MemoryStream(), "gzip", "gzip", "gzip", "gzip"));
        Assert.Equal((ErrorKind.LayerLimit, 0, 3L), (layer.Kind, layer.Layer, layer.Limit));

        Outcome input = T.Read(decoder, new MemoryStream(Data), 100);
        var e3 = Assert.IsType<SpillgateException>(input.Error);
        Assert.Equal((ErrorKind.InputLimit, 1, 500L), (e3.Kind, e3.Layer, e3.Limit));

        foreach (ErrorKind kind in Enum.GetValues<ErrorKind>())
        {
            Assert.False(string.IsNullOrEmpty(kind.SpecName()));
            Assert.True(kind.HttpStatus() is >= 400 and <= 503);
        }
    }

    [Fact]
    [Covers("SG-R-LAY-09", "SG-R-RDR-05")]
    public void NothingIsReadFromTheSourceAfterATerminalConditionAndLookAheadIsBounded()
    {
        byte[] stream = T.Gzip(T.Text(3, 1000));
        byte[] padded = Generators.Concat(stream, new byte[5_000_000]);
        var source = new ChunkedStream(padded);
        Outcome o = T.Read(T.DecoderFor(1 << 20), source, 4096, "gzip");
        Assert.Equal("corrupt", o.Name);
        Assert.True(source.Total <= stream.Length + (64 * 1024), "read ahead " + (source.Total - stream.Length));

        // A dead stream does not touch the source again.
        var spy = new ChunkedStream(padded);
        DecodedStream dead = T.DecoderFor(5).Open(spy, Array.Empty<string>());
        Assert.Throws<SpillgateException>(() => T.ReadAll(dead, 100).Error is { } e ? throw e : 0);
        int afterFailure = spy.Reads;
        for (int i = 0; i < 10; i++)
        {
            Assert.Throws<SpillgateException>(() => dead.Read(new byte[10], 0, 10));
        }

        Assert.Equal(afterFailure, spy.Reads);
    }

    [Fact]
    [Covers("SG-R-LAY-01", "SG-R-LAY-02", "SG-R-CDC-10")]
    public void AnIntermediateLayerHasItsOwnBudgetEvenIfALaterLayerShrinksTheData()
    {
        // Layer 2 input is a 3 MiB sequence of tiny gzip members padded by 64 KiB extra fields: it decodes to almost nothing.
        byte[] padded = PaddedMembers(48);
        byte[] outer = Generators.GzipStored(padded); // layer 1 turns this into the 3 MiB blob, layer 2 shrinks it
        Decoder decoder = T.DecoderFor(1 << 20, l => l.WithoutRatioLimit());
        Outcome o = T.Read(decoder, outer, 4096, "gzip", "gzip");
        var e = Assert.IsType<SpillgateException>(o.Error);
        Assert.Equal(ErrorKind.OutputLimit, e.Kind);
        Assert.Equal(1, e.Layer);

        Decoder roomy = T.DecoderFor(8 << 20, l => l.WithoutRatioLimit());
        Outcome ok = T.Read(roomy, outer, 4096, "gzip", "gzip");
        Assert.Equal("ok", ok.Name);
        Assert.Equal(48 * 4, ok.Data.Length);

        // The identity-only chain has a budget as well.
        Assert.Equal("output_limit", T.Read(T.DecoderFor(10), new byte[11], 100).Name);
        Assert.Equal("ok", T.Read(T.DecoderFor(10), new byte[10], 100).Name);
    }

    private static byte[] PaddedMembers(int count)
    {
        var parts = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            var header = new List<byte> { 0x1f, 0x8b, 8, 0x04, 0, 0, 0, 0, 0, 3, 0xff, 0xff };
            header.AddRange(new byte[65535]);
            byte[] payload = { 1, 2, 3, 4 };
            parts.Add(Generators.Concat(header.ToArray(), DeflateKit.Stored(payload, true), BitConverter.GetBytes(Generators.Crc32(payload)), BitConverter.GetBytes(4u)));
        }

        return Generators.Concat(parts.ToArray());
    }

    [Fact]
    [Covers("SG-R-LAY-03", "SG-R-LAY-07", "SG-I-01")]
    public void ExactlyTheLimitIsFineOneMoreIsNotAndTheFirstConditionWins()
    {
        byte[] data = Generators.Data(4, 5000);
        byte[] gz = T.Gzip(data);
        Assert.Equal("ok", T.Read(T.DecoderFor(5000), gz, 77, "gzip").Name);
        Outcome over = T.Read(T.DecoderFor(4999), gz, 77, "gzip");
        Assert.Equal("output_limit", over.Name);
        Assert.Equal(4999, over.Data.Length);

        // A bad trailer after exactly MaxOutput bytes is corrupt; a longer stream with a bad trailer is output_limit.
        byte[] badTrailer = (byte[])gz.Clone();
        badTrailer[badTrailer.Length - 8] ^= 0xff;
        Outcome corrupt = T.Read(T.DecoderFor(5000), badTrailer, 77, "gzip");
        Assert.Equal("corrupt", corrupt.Name);
        Assert.Equal(5000, corrupt.Data.Length);
        Assert.Equal("output_limit", T.Read(T.DecoderFor(4999), badTrailer, 77, "gzip").Name);

        // The same for every read size around the limit.
        foreach (int size in new[] { 1, 4999, 5000, 5001, 1 << 20 })
        {
            Assert.Equal("ok", T.Read(T.DecoderFor(5000), gz, size, "gzip").Name);
            Assert.Equal(4999, T.Read(T.DecoderFor(4999), gz, size, "gzip").Data.Length);
        }
    }
}
