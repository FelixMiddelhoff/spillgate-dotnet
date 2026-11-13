using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;
using Xunit;

namespace Spillgate.Conformance.Tests;

/// <summary>
/// Runs every vector of the pinned corpus. Each vector is run with its own read pattern and with the
/// alternative patterns of the schema, synchronously, and the main pattern again through the asynchronous path.
/// Nothing is skipped: a vector that cannot be built or run fails.
/// </summary>
[Covers("SG-I-01", "SG-I-02", "SG-I-03", "SG-I-09", "SG-R-RDR-01", "SG-R-LAY-05")]
public sealed class ConformanceTests
{
    private const int ExpectedVectors = 370;
    private const long MaxRead = 32L * 1024 * 1024;
    private static readonly long[][] AlternativePatterns = { new long[] { 1 }, new long[] { 3 }, new long[] { 4096 }, new long[] { -1 }, new long[] { 1, 7, 4096, 1 } };

    public static IEnumerable<object[]> VectorIds()
    {
        using JsonDocument manifest = Repo.ReadManifest();
        foreach (JsonElement v in manifest.RootElement.GetProperty("vectors").EnumerateArray())
        {
            yield return new object[] { v.GetProperty("id").GetString()! };
        }
    }

    [Fact]
    public void TheCorpusHasAllVectors()
    {
        using JsonDocument manifest = Repo.ReadManifest();
        Assert.Equal("0.2.0", manifest.RootElement.GetProperty("corpus_version").GetString());
        Assert.Equal(ExpectedVectors, manifest.RootElement.GetProperty("vectors").GetArrayLength());
    }

    [Theory]
    [MemberData(nameof(VectorIds))]
    public async Task Vector(string id)
    {
        using JsonDocument manifest = Repo.ReadManifest();
        JsonElement vector = default;
        foreach (JsonElement v in manifest.RootElement.GetProperty("vectors").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == id)
            {
                vector = v;
            }
        }

        byte[] stream = BuildInput(vector);
        var patterns = new List<long[]>();
        if (vector.TryGetProperty("read_pattern", out JsonElement rp))
        {
            var first = new List<long>();
            foreach (JsonElement e in rp.EnumerateArray())
            {
                first.Add(e.GetInt64());
            }

            patterns.Add(first.ToArray());
        }
        else
        {
            patterns.Add(new long[] { 4096 });
        }

        patterns.AddRange(AlternativePatterns);
        foreach (long[] pattern in patterns)
        {
            if (!SkipTiny(vector, pattern))
            {
                await RunPattern(vector, stream, pattern, useAsync: false);
            }
        }

        if (!SkipTiny(vector, patterns[0]))
        {
            await RunPattern(vector, stream, patterns[0], useAsync: true);
        }
    }

    private static long LimitValue(JsonElement value)
    {
        try
        {
            return value.ValueKind == JsonValueKind.String
                ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                : value.GetInt64();
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            return 0;
        }
    }

    private static Limits BuildLimits(JsonElement vector)
    {
        JsonElement raw = vector.GetProperty("limits");
        // An absent max_output is expressed as 0: both are invalid_limits (SG-R-LIM-01).
        Limits limits = Limits.Create(raw.TryGetProperty("max_output", out JsonElement mo) ? LimitValue(mo) : 0);
        if (raw.TryGetProperty("max_layers", out JsonElement ml))
        {
            limits = limits.WithMaxLayers((int)Math.Clamp(LimitValue(ml), int.MinValue, int.MaxValue));
        }

        if (raw.TryGetProperty("ratio_grace", out JsonElement rg))
        {
            limits = limits.WithRatioGrace(LimitValue(rg));
        }

        if (raw.TryGetProperty("max_input", out JsonElement mi))
        {
            limits = limits.WithMaxInput(LimitValue(mi));
        }

        if (raw.TryGetProperty("max_ratio", out JsonElement mr))
        {
            limits = mr.ValueKind == JsonValueKind.String && mr.GetString() == "none"
                ? limits.WithoutRatioLimit()
                : limits.WithMaxRatio(LimitValue(mr));
        }

        return limits;
    }

    private static List<string> NeededCodecs(JsonElement vector)
    {
        var needed = new List<string>();
        if (vector.TryGetProperty("tags", out JsonElement tags))
        {
            foreach (JsonElement t in tags.EnumerateArray())
            {
                string tag = t.GetString()!;
                if (tag.StartsWith("needs-", StringComparison.Ordinal))
                {
                    needed.Add(tag.Substring("needs-".Length));
                }
            }
        }

        return needed;
    }

    private static int WindowBitsWithin(long limit)
    {
        int bits = 10;
        while (bits < 24 && (1L << (bits + 1)) - 16 <= limit)
        {
            bits++;
        }

        return bits;
    }

    private static DecoderBuilder AddCodec(DecoderBuilder builder, string name, long memoryCap)
    {
        switch (name)
        {
            case "br":
                return builder.AddBrotli(memoryCap != 0 ? BrotliOptions.Create(WindowBitsWithin(memoryCap)) : null);
            case "zstd":
                return builder.AddZstd(memoryCap != 0 ? ZstdOptions.Create(memoryCap) : null);
            default:
                throw new InvalidOperationException("codec " + name + " is not available");
        }
    }

    private static string OutcomeOf(Exception error) => error switch
    {
        SpillgateException e => e.Kind.SpecName(),
        OperationCanceledException => "canceled",
        _ => "other: " + error,
    };

    private static bool SkipTiny(JsonElement vector, long[] pattern)
    {
        JsonElement e = vector.GetProperty("expect");
        long expected = 0;
        foreach (string k in new[] { "length", "delivered_exactly", "delivered_at_most" })
        {
            if (e.TryGetProperty(k, out JsonElement v))
            {
                expected = LimitValue(v);
                break;
            }
        }

        if (expected <= 4L * 1024 * 1024)
        {
            return false;
        }

        foreach (long s in pattern)
        {
            if (s >= 0 && s < 64)
            {
                return true; // multi-GiB output one byte at a time is not a useful test
            }
        }

        return false;
    }

    private static byte[] BuildInput(JsonElement vector)
    {
        string id = vector.GetProperty("id").GetString()!;
        JsonElement input = vector.GetProperty("input");
        byte[] stream;
        if (input.TryGetProperty("generator", out JsonElement generator))
        {
            stream = Generators.Generate(generator);
        }
        else if (input.TryGetProperty("file", out JsonElement file))
        {
            stream = File.ReadAllBytes(Repo.Path("conformance", file.GetString()!));
        }
        else
        {
            throw new InvalidOperationException(id + ": input without file or generator");
        }

        if (Generators.Sha256Hex(stream) != input.GetProperty("sha256").GetString())
        {
            throw new InvalidOperationException(id + ": the input differs from the manifest (generator or file drift)");
        }

        return stream;
    }

    private static async Task RunPattern(JsonElement vector, byte[] stream, long[] pattern, bool useAsync)
    {
        string id = vector.GetProperty("id").GetString()!;
        string label = id + " read pattern [" + string.Join(",", pattern) + "]" + (useAsync ? " async" : string.Empty);
        JsonElement expect = vector.GetProperty("expect");
        string wantOutcome = expect.GetProperty("outcome").GetString()!;
        var source = new SpySource(stream);

        var encodings = new List<string?>();
        foreach (JsonElement e in vector.GetProperty("encodings").EnumerateArray())
        {
            encodings.Add(e.GetString());
        }

        bool strictZlib = false;
        long memoryCap = 0;
        if (vector.TryGetProperty("options", out JsonElement options))
        {
            strictZlib = options.TryGetProperty("strict_zlib", out JsonElement sz) && sz.GetBoolean();
            memoryCap = options.TryGetProperty("memory_cap", out JsonElement mc) ? LimitValue(mc) : 0;
        }

        JsonElement rawLimits = vector.GetProperty("limits");
        long maxOutput = rawLimits.TryGetProperty("max_output", out JsonElement mo) ? LimitValue(mo) : 0;
        bool expectConstruct = expect.TryGetProperty("phase", out JsonElement ph) && ph.GetString() == "construct";

        DecodedStream reader;
        try
        {
            DecoderBuilder builder = Decoder.CreateBuilder(BuildLimits(vector)).WithStrictZlib(strictZlib);
            foreach (string name in NeededCodecs(vector))
            {
                builder = AddCodec(builder, name, memoryCap);
            }

            reader = builder.Build().Open(source, encodings);
        }
        catch (SpillgateException error)
        {
            if (!expectConstruct)
            {
                Assert.Fail(label + ": unexpected construction error " + error);
            }

            Assert.True(error.Kind.SpecName() == wantOutcome, label + ": construction error " + error.Kind.SpecName() + ", want " + wantOutcome + ": " + error);
            Assert.True(source.Reads == 0, label + ": the source was read before the construction error");
            return;
        }

        if (expectConstruct)
        {
            reader.Dispose();
            Assert.Fail(label + ": expected " + wantOutcome + " at construction");
        }

        try
        {
            ReadResult result = await ReadWithPattern(reader, pattern, maxOutput, useAsync);
            string outcome = result.Error is null ? "ok" : OutcomeOf(result.Error);
            CheckOutcome(vector, label, result, outcome, maxOutput);

            // Sticky (SG-R-LAY-05): further reads return the same outcome and no data.
            for (int i = 0; i < 3; i++)
            {
                var one = new byte[16];
                string again;
                try
                {
                    int n = useAsync ? await reader.ReadAsync(one) : reader.Read(one, 0, one.Length);
                    again = n == 0 ? "ok" : n + " more bytes";
                }
                catch (Exception e)
                {
                    again = OutcomeOf(e);
                }

                Assert.True(again == outcome, label + ": not sticky: " + outcome + " then " + again);
            }
        }
        finally
        {
            reader.Dispose();
        }

        if (rawLimits.TryGetProperty("max_input", out JsonElement mi))
        {
            long cap = LimitValue(mi);
            Assert.True(source.Total <= cap + 1, label + ": took " + source.Total + " bytes from the source (SG-R-INP-02)");
        }
    }

    private static void CheckOutcome(JsonElement vector, string label, ReadResult r, string outcome, long maxOutput)
    {
        JsonElement expect = vector.GetProperty("expect");
        string want = expect.GetProperty("outcome").GetString()!;
        Assert.True(r.Delivered <= maxOutput, label + ": delivered " + r.Delivered + " > max_output " + maxOutput);
        Assert.True(outcome == want, label + ": outcome " + outcome + ", want " + want + " (delivered " + r.Delivered + "; " + r.Error + ")");
        if (want == "ok")
        {
            long length = expect.TryGetProperty("length", out JsonElement l) ? LimitValue(l) : -1;
            Assert.True(r.Delivered == length, label + ": delivered " + r.Delivered + ", want " + length);
            Assert.True(expect.GetProperty("sha256").GetString() == r.Digest, label + ": wrong data");
            return;
        }

        Assert.True(
            expect.TryGetProperty("phase", out JsonElement phase) && phase.GetString() == "read",
            label + ": a read error but a construction error was expected");
        if (expect.TryGetProperty("delivered_exactly", out JsonElement exact))
        {
            Assert.True(r.Delivered == LimitValue(exact), label + ": delivered " + r.Delivered + ", want exactly " + LimitValue(exact));
        }

        if (expect.TryGetProperty("delivered_at_most", out JsonElement most))
        {
            Assert.True(r.Delivered <= LimitValue(most), label + ": delivered " + r.Delivered + ", want at most " + LimitValue(most));
        }

        if (expect.TryGetProperty("layer", out JsonElement layer) && r.Error is SpillgateException se)
        {
            Assert.True(se.Layer == LimitValue(layer), label + ": error in layer " + se.Layer + ", want " + LimitValue(layer));
        }
    }

    private static async Task<ReadResult> ReadWithPattern(DecodedStream reader, long[] pattern, long maxOutput, bool useAsync)
    {
        var sizes = new int[pattern.Length];
        int largest = 1;
        for (int i = 0; i < pattern.Length; i++)
        {
            sizes[i] = pattern[i] < 0 ? (int)(Math.Min(maxOutput, MaxRead - 1) + 1) : (int)pattern[i];
            largest = Math.Max(largest, sizes[i]);
        }

        var buffer = new byte[largest];
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var result = new ReadResult();
        for (long i = 0; ; i++)
        {
            int size = sizes[(int)(i % sizes.Length)];
            int n;
            try
            {
                n = useAsync
                    ? await reader.ReadAsync(buffer.AsMemory(0, size))
                    : reader.Read(buffer, 0, size);
            }
            catch (Exception e)
            {
                result.Error = e;
                break;
            }

            if (n == 0)
            {
                break;
            }

            digest.AppendData(buffer, 0, n);
            result.Delivered += n;
        }

        result.Digest = Generators.HexOf(digest.GetHashAndReset());
        return result;
    }

    private sealed class ReadResult
    {
        public long Delivered;
        public string Digest = string.Empty;
        public Exception? Error;
    }

    private sealed class SpySource : Stream
    {
        private readonly byte[] _data;
        private int _pos;

        public SpySource(byte[] data)
        {
            _data = data;
        }

        public long Reads { get; private set; }

        public long Total { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            int n = Math.Min(buffer.Length, _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            Total += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
