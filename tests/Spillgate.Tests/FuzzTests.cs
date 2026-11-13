using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;
using Xunit;

namespace Spillgate.Tests;

/// <summary>
/// Seeded, time-boxed randomized tests. Every iteration is reproducible from the seed and its number; the budget
/// comes from SPILLGATE_FUZZ_SECONDS (default 4; the nightly workflow sets it to many minutes) and the seed from
/// SPILLGATE_FUZZ_SEED. Invariants: no exception but SpillgateException, delivered at most MaxOutput, sticky
/// outcomes, outcome and bytes independent of read sizes (except the length of a ratio_limit prefix), and for
/// the deflate family: whatever is accepted, the runtime accepts with the same bytes.
/// </summary>
public sealed class FuzzTests
{
    private static readonly string[] Forms = { "gzip", "deflate-zlib", "deflate-raw", "br", "zstd", "identity", "gzip+gzip", "zstd+br", "br+deflate+gzip" };
    private static readonly Dictionary<string, List<byte[]>> Bases = BuildBases();

    private static double Budget => double.TryParse(Environment.GetEnvironmentVariable("SPILLGATE_FUZZ_SECONDS"), System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 4;

    private static long Seed => long.TryParse(Environment.GetEnvironmentVariable("SPILLGATE_FUZZ_SEED"), out long s) ? s : 20261113;

    private static Dictionary<string, List<byte[]>> BuildBases()
    {
        var plains = new List<byte[]>
        {
            Array.Empty<byte>(),
            T.Text(1, 300),
            T.Text(2, 9000),
            Generators.Data(3, 2500),
            new byte[30_000],
            T.Mixed(4, 12_000),
        };
        var map = new Dictionary<string, List<byte[]>>();
        foreach (string form in Forms)
        {
            map[form] = plains.Select(p => Make(form, p)).ToList();
        }

        return map;
    }

    private static byte[] Make(string form, byte[] p) => form switch
    {
        "gzip" => T.Gzip(p),
        "deflate-zlib" => T.ZLib(p),
        "deflate-raw" => T.RawDeflate(p),
        "br" => T.Brotli(p),
        "zstd" => Generators.ZstdStored(p, 18),
        "identity" => p,
        "gzip+gzip" => T.Gzip(T.Gzip(p)),
        "zstd+br" => Generators.ZstdStored(T.Brotli(p), 17),
        _ => T.Brotli(T.ZLib(T.Gzip(p))),
    };

    private static string[] Encodings(string form) => form switch
    {
        "gzip" => new[] { "gzip" },
        "deflate-zlib" or "deflate-raw" => new[] { "deflate" },
        "br" => new[] { "br" },
        "zstd" => new[] { "zstd" },
        "identity" => Array.Empty<string>(),
        "gzip+gzip" => new[] { "gzip", "gzip" },
        "zstd+br" => new[] { "br", "zstd" },
        _ => new[] { "gzip", "deflate", "br" },
    };

    private static byte[] Mutate(Random rnd, byte[] stream, List<byte[]> others)
    {
        byte[] s = (byte[])stream.Clone();
        switch (rnd.Next(8))
        {
            case 0:
                break; // untouched
            case 1:
            case 2:
                for (int i = 0, n = 1 + rnd.Next(4); i < n && s.Length > 0; i++)
                {
                    s[rnd.Next(s.Length)] ^= (byte)(1 << rnd.Next(8));
                }

                break;
            case 3:
                return s.AsSpan(0, s.Length == 0 ? 0 : rnd.Next(s.Length)).ToArray();
            case 4:
                byte[] other = others[rnd.Next(others.Count)];
                return Generators.Concat(s, other.AsSpan(0, Math.Min(other.Length, rnd.Next(20))).ToArray());
            case 5:
                if (s.Length > 0)
                {
                    int at = rnd.Next(s.Length);
                    return Generators.Concat(s.AsSpan(0, at).ToArray(), new[] { (byte)rnd.Next(256) }, s.AsSpan(at).ToArray());
                }

                break;
            case 6:
                if (s.Length > 1)
                {
                    int at = rnd.Next(s.Length);
                    return Generators.Concat(s.AsSpan(0, at).ToArray(), s.AsSpan(at + 1).ToArray());
                }

                break;
            default:
                for (int i = 0; i < s.Length && i < 8; i++)
                {
                    if (rnd.Next(3) == 0)
                    {
                        s[i] = (byte)rnd.Next(256);
                    }
                }

                break;
        }

        return s;
    }

    [Fact]
    [Covers("SG-I-01", "SG-I-02", "SG-I-03", "SG-I-05", "SG-I-09", "SG-R-LAY-02", "SG-R-CDC-08")]
    public void RandomizedStreamsKeepEveryInvariant()
    {
        var clock = Stopwatch.StartNew();
        long seed = Seed;
        long iteration = 0;
        while (clock.Elapsed.TotalSeconds < Budget)
        {
            iteration++;
            RunIteration(seed, iteration);
        }

        Assert.True(iteration > 50, "too few iterations: " + iteration);
    }

    private static void RunIteration(long seed, long iteration)
    {
        var rnd = new Random(unchecked((int)(seed * 31 + iteration)));
        string form = Forms[rnd.Next(Forms.Length)];
        List<byte[]> pool = Bases[form];
        byte[] stream = Mutate(rnd, pool[rnd.Next(pool.Count)], pool);
        string[] enc = Encodings(form);
        long[] outputs = { 1, 10, 1000, 20_000, 1 << 22 };
        long maxOutput = outputs[rnd.Next(outputs.Length)];
        long maxInput = rnd.Next(4) == 0 ? 1 + rnd.Next(Math.Max(1, stream.Length + 10)) : 0;
        long ratio = rnd.Next(5) == 0 ? 1 + rnd.Next(50) : 0;
        Limits limits = Limits.Create(maxOutput);
        if (maxInput > 0)
        {
            limits = limits.WithMaxInput(maxInput);
        }

        limits = ratio > 0 ? limits.WithMaxRatio(ratio).WithRatioGrace(rnd.Next(3) == 0 ? 0 : 100) : limits.WithoutRatioLimit();
        string label = "seed " + seed + " iteration " + iteration + " form " + form + " out " + maxOutput + " in " + maxInput + " ratio " + ratio;

        Decoder decoder = Decoder.CreateBuilder(limits).AddBrotli().AddZstd().Build();
        int[] pattern = rnd.Next(3) switch
        {
            0 => new[] { 1 },
            1 => new[] { 1 + rnd.Next(100), 1 + rnd.Next(5000) },
            _ => new[] { 1 << 20 },
        };
        int chunk = rnd.Next(3) == 0 ? 1 + rnd.Next(300) : int.MaxValue;
        var timer = Stopwatch.StartNew();
        Outcome o;
        try
        {
            o = T.ReadPattern(decoder.Open(new ChunkedStream(stream, chunk), enc), pattern);
        }
        catch (SpillgateException e)
        {
            o = new Outcome { Error = e };
        }

        Assert.True(timer.Elapsed.TotalSeconds < 10, label + ": took " + timer.Elapsed);
        Assert.True(o.Error is null || o.Error is SpillgateException, label + ": unexpected exception " + o.Error);
        Assert.True(o.Data.Length <= maxOutput, label + ": delivered " + o.Data.Length);

        // Independence of read sizes and chunking. The ratio is judged once per codec step, so a stream whose own
        // ratio is close to MaxRatio may trip the check in one run and not in another (SG-R-RDR-01 allows the
        // length of a ratio_limit prefix to differ, SG-R-RAT-05 keeps vectors away from the threshold); then only
        // the prefix property holds.
        Outcome reference = T.ReadPattern(decoder.Open(new MemoryStream(stream), enc), new[] { 1 << 20 });
        bool ratioInvolved = o.Kind == ErrorKind.RatioLimit || reference.Kind == ErrorKind.RatioLimit;
        if (!ratioInvolved)
        {
            Assert.True(reference.Name == o.Name, label + ": " + o.Name + " vs " + reference.Name);
            Assert.True(o.Data.AsSpan().SequenceEqual(reference.Data), label + ": bytes differ between read patterns (" + o + " with chunk " + chunk + " and reads " + string.Join(",", pattern) + " vs " + reference + ")");
        }
        else
        {
            int common = Math.Min(o.Data.Length, reference.Data.Length);
            Assert.True(o.Data.AsSpan(0, common).SequenceEqual(reference.Data.AsSpan(0, common)), label + ": prefixes of ratio runs differ");
        }

        if (o.Error is null && maxInput == 0 && enc.Length == 1)
        {
            AssertRuntimeAgrees(form, stream, o.Data, label);
        }

        // Sticky: a second pass over the same dead stream gives the same kind.
        DecodedStream again = decoder.Open(new MemoryStream(stream), enc);
        Outcome first = T.ReadAll(again, 777);
        Outcome second = T.ReadAll(again, 5);
        Assert.True(second.Data.Length == 0 && (first.Error is null ? second.Error is null : second.Name == first.Name), label + ": not sticky");
    }

    private static void AssertRuntimeAgrees(string form, byte[] stream, byte[] ours, string label)
    {
        Func<Stream, Stream>? wrap = form switch
        {
            "gzip" => s => new GZipStream(s, CompressionMode.Decompress),
            "deflate-zlib" => s => new ZLibStream(s, CompressionMode.Decompress),
            "deflate-raw" => s => new DeflateStream(s, CompressionMode.Decompress),
            _ => null,
        };
        if (wrap is null)
        {
            return;
        }

        if (form == "deflate-raw" && stream.Length >= 2 && Inflater.IsZlibHeader(stream[0], stream[1]))
        {
            return; // by SG-R-CDC-03 such a stream is read as zlib
        }

        byte[]? theirs;
        try
        {
            using var input = new MemoryStream(stream);
            using Stream z = wrap(input);
            var sink = new MemoryStream();
            z.CopyTo(sink);
            theirs = sink.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            theirs = null;
        }

        Assert.True(theirs is not null && theirs.AsSpan().SequenceEqual(ours), label + ": accepted what the runtime rejects or reads differently");
    }

    [Fact]
    [Covers("SG-I-01", "SG-I-05", "SG-R-HDR-14", "SG-I-07", "SG-I-06")]
    public void RandomHeadersNeverCrashAndAlwaysFailClosed()
    {
        var clock = Stopwatch.StartNew();
        var rnd = new Random(unchecked((int)Seed));
        Decoder decoder = T.DecoderFor(1000, l => l.WithMaxLayers(16));
        string alphabet = "gzip,deflate br zstd identity x-gzip,;\" \t\r\né\u0000-_.ABCabc";
        int n = 0;
        while (clock.Elapsed.TotalSeconds < Budget / 2)
        {
            n++;
            int parts = rnd.Next(4);
            var values = new string[parts];
            for (int i = 0; i < parts; i++)
            {
                int len = rnd.Next(40);
                values[i] = new string(Enumerable.Range(0, len).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
            }

            try
            {
                decoder.Open(new MemoryStream(), values).Dispose();
            }
            catch (SpillgateException e)
            {
                Assert.True(
                    e.Kind is ErrorKind.MalformedEncoding or ErrorKind.UnsupportedEncoding or ErrorKind.LayerLimit,
                    string.Join("|", values) + ": " + e.Kind);
                Assert.True(e.Message.Length < 400 && !e.Message.Any(char.IsControl), "unsafe message");
            }
        }

        Assert.True(n > 100);
    }
}
