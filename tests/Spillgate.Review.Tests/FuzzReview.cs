using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spillgate.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Spillgate.Review.Tests;

/// <summary>
/// Independent public-API fuzz: structured mutation of valid streams, random source chunking, sync and async,
/// random read sizes. Asserts: only SpillgateException, at most MaxOutput delivered, outcome class and delivered
/// bytes independent of chunking/read sizes (F1 prefix invariance). Cases per codec: SPILLGATE_REVIEW_CASES (default 4000).
/// </summary>
public sealed class FuzzReview
{
    private readonly ITestOutputHelper _out;
    public FuzzReview(ITestOutputHelper o) { _out = o; }

    public static int KnownBrotliBcl;

    private static int Cases => int.TryParse(Environment.GetEnvironmentVariable("SPILLGATE_REVIEW_CASES"), out int c) ? c : 4000;

    private static List<(byte[] s, string[] enc)> Bases(string codec)
    {
        var l = new List<(byte[], string[])>();
        var rnd = new Random(99);
        var noise = new byte[3000]; rnd.NextBytes(noise);
        byte[][] plains = { Array.Empty<byte>(), R.Text(40, 2), R.Text(900, 3), R.Text(20000, 4), noise, new byte[40000] };
        foreach (byte[] p in plains)
        {
            switch (codec)
            {
                case "gzip":
                    l.Add((R.Gzip(p), new[] { "gzip" }));
                    break;
                case "deflate":
                    l.Add((R.Zlib(p), new[] { "deflate" }));
                    l.Add((R.Deflate(p), new[] { "deflate" }));
                    break;
                case "br":
                    l.Add((R.Br(p), new[] { "br" }));
                    break;
                case "zstd":
                    l.Add((Generators.ZstdStored(p, 17), new[] { "zstd" }));
                    break;
                case "chain":
                    l.Add((R.Br(R.Gzip(p)), new[] { "gzip", "br" }));
                    l.Add((Generators.ZstdStored(R.Zlib(p), 18), new[] { "deflate", "zstd" }));
                    break;
            }
        }

        switch (codec)
        {
            case "gzip": l.Add((Generators.Concat(R.Gzip(R.Text(100, 5)), R.Gzip(new byte[0]), R.Gzip(R.Text(500, 6))), new[] { "gzip" })); l.Add((Generators.GzipStored(R.Text(3000, 8)), new[] { "gzip" })); break;
            case "deflate": l.Add((Generators.ZlibStored(R.Text(3000, 8)), new[] { "deflate" })); l.Add((Generators.StoredDeflate(R.Text(3000, 8)), new[] { "deflate" })); break;
            case "br": l.Add((Generators.BrotliStored(R.Text(5000, 9), 16), new[] { "br" })); l.Add((Generators.BrotliZeros(70000, 22), new[] { "br" })); l.Add((Generators.BrotliZeros(70000, 10), new[] { "br" })); break;
            case "zstd":
                l.Add((Generators.Concat(Generators.ZstdSkippable(30), Generators.ZstdStored(R.Text(700, 3), 17), Generators.ZstdZeros(5000, 20)), new[] { "zstd" }));
                l.Add((Generators.ZstdZeros(300000, 17), new[] { "zstd" }));
                break;
        }

        return l;
    }

    private static byte[] Mutate(Random rnd, byte[] s)
    {
        byte[] m = (byte[])s.Clone();
        int rounds = 1 + rnd.Next(3);
        for (int r = 0; r < rounds; r++)
        {
            if (m.Length == 0) { m = new byte[rnd.Next(1, 40)]; rnd.NextBytes(m); continue; }
            switch (rnd.Next(9))
            {
                case 0: m[rnd.Next(m.Length)] ^= (byte)(1 << rnd.Next(8)); break;
                case 1: m = m.AsSpan(0, rnd.Next(m.Length)).ToArray(); break;
                case 2: { int p = rnd.Next(m.Length); int n = rnd.Next(1, 20); var ins = new byte[n]; rnd.NextBytes(ins); m = m.Take(p).Concat(ins).Concat(m.Skip(p)).ToArray(); break; }
                case 3: { int p = rnd.Next(m.Length); int n = Math.Min(rnd.Next(1, 20), m.Length - p); m = m.Take(p).Concat(m.Skip(p + n)).ToArray(); break; }
                case 4: m[rnd.Next(m.Length)] = (byte)rnd.Next(256); break;
                case 5: { int p = rnd.Next(m.Length); int n = Math.Min(rnd.Next(1, 64), m.Length - p); m = m.Take(p + n).Concat(m.Skip(p)).ToArray(); break; }
                case 6: m = m.Concat(new byte[] { (byte)rnd.Next(256) }).ToArray(); break;
                case 7: { int p = rnd.Next(Math.Min(m.Length, 24)); m[p] = (byte)rnd.Next(256); break; }
                default: { int p = rnd.Next(m.Length); m[p] = rnd.Next(2) == 0 ? (byte)0 : (byte)0xff; break; }
            }
        }

        return m;
    }

    private static R.Res RunOne(Decoder d, byte[] s, string[] enc, Random rnd, bool async)
    {
        int mode = rnd.Next(4);
        int srcMax = rnd.Next(3) == 0 ? 1 : 1 + rnd.Next(2000);
        var src = new R.Chunked(s, () => rnd.Next(2) == 0 ? 1 + rnd.Next(srcMax) : srcMax);
        var r = new R.Res();
        var ms = new MemoryStream();
        DecodedStream? st = null;
        try
        {
            st = d.Open(src, enc);
            int rs = rnd.Next(3) == 0 ? 1 : 1 + rnd.Next(70000);
            var buf = new byte[rs];
            while (true)
            {
                int n;
                if (async) n = st.ReadAsync(buf.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                else if (mode == 0) n = st.Read(buf, 0, rs);
                else if (mode == 1) n = st.Read(buf.AsSpan());
                else { int b = st.ReadByte(); n = b < 0 ? 0 : 1; if (n == 1) buf[0] = (byte)b; }
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }
        }
        catch (Exception e) { r.Err = e; }
        finally { st?.Dispose(); }
        r.Data = ms.ToArray();
        return r;
    }

    private void Fuzz(string codec, int seedBase)
    {
        var bases = Bases(codec);
        var failures = new ConcurrentQueue<string>();
        int cases = Cases;
        var kinds = new ConcurrentDictionary<string, int>();
        Parallel.For(0, cases, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var rnd = new Random(seedBase * 1_000_003 + i);
            var (stream, enc) = bases[rnd.Next(bases.Count)];
            byte[] m = rnd.Next(10) == 0 ? stream : Mutate(rnd, stream);
            long max = rnd.Next(3) == 0 ? long.MaxValue / 2 : rnd.Next(1, 50000);
            long? maxIn = rnd.Next(4) == 0 ? rnd.Next(1, Math.Max(2, m.Length + 5)) : null;
            bool ratio = rnd.Next(5) == 0;
            Decoder d = R.Dec(max, l =>
            {
                if (maxIn is { } mi) l = l.WithMaxInput(mi);
                if (ratio) l = l.WithMaxRatio(20).WithRatioGrace(0);
                return l;
            });
            R.Res reference = RunOne(d, m, enc, new Random(7), false);
            string tag = $"{codec} case {i} max={max} in={maxIn} ratio={ratio} len={m.Length} hex={(m.Length <= 100 ? Convert.ToHexString(m) : "-")}";
            Check(failures, tag + " ref", reference, max);
            kinds.AddOrUpdate(reference.Err is SpillgateException se ? se.Kind.ToString() : (reference.Err == null ? "ok" : "other"), 1, (_, v) => v + 1);
            for (int v = 0; v < 3; v++)
            {
                bool async = (v & 1) == 1;
                R.Res other = RunOne(d, m, enc, new Random(i * 31 + v), async);
                Check(failures, tag + $" v{v}", other, max);
                Compare(failures, tag + $" v{v}", reference, other, ratio);
            }
        });
        foreach (var kv in kinds.OrderBy(k => k.Key)) _out.WriteLine($"{codec}: {kv.Key} = {kv.Value}");
        _out.WriteLine("known brotli F-A mismatches seen so far: " + KnownBrotliBcl);
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(12)));
    }

    private static void Check(ConcurrentQueue<string> f, string tag, R.Res r, long max)
    {
        if (r.Data.Length > max) f.Enqueue(tag + " delivered over MaxOutput: " + r.Data.Length);
        if (r.Err != null && r.Err is not SpillgateException && r.Err is not OperationCanceledException)
            f.Enqueue(tag + " foreign exception " + r.Err.GetType().FullName + ": " + r.Err.Message);
        if (r.Err is SpillgateException { Kind: ErrorKind.Internal } ie)
            f.Enqueue(tag + " Internal error: " + ie.Message);
    }

    private static void Compare(ConcurrentQueue<string> f, string tag, R.Res a, R.Res b, bool ratio)
    {
        string ka = a.Err is SpillgateException x ? x.Kind.ToString() : (a.Err == null ? "ok" : a.Err.GetType().Name);
        string kb = b.Err is SpillgateException y ? y.Kind.ToString() : (b.Err == null ? "ok" : b.Err.GetType().Name);
        bool brotliLike = tag.StartsWith("br") || tag.StartsWith("chain");
        bool prefix = a.Data.Length <= b.Data.Length ? b.Data.AsSpan(0, a.Data.Length).SequenceEqual(a.Data) : a.Data.AsSpan(0, b.Data.Length).SequenceEqual(b.Data);
        bool corruptInvolved = ka == "Corrupt" || kb == "Corrupt";
        if (brotliLike && corruptInvolved && prefix && Environment.GetEnvironmentVariable("SPILLGATE_REVIEW") != "1")
        {
            Interlocked.Increment(ref KnownBrotliBcl);
            return; // finding F-A, proven by BrotliReview.DeliveredPrefixDependsOnReadSize
        }

        if (ratio && (ka == "RatioLimit" || kb == "RatioLimit") && prefix) return; // RDR-01: ratio near threshold may flip with step sizes
        if (ka != kb) { f.Enqueue($"{tag} outcome differs: {ka} ({a.Data.Length}B) vs {kb} ({b.Data.Length}B)"); return; }
        if (ka == "RatioLimit")
        {
            int n = Math.Min(a.Data.Length, b.Data.Length);
            if (!a.Data.AsSpan(0, n).SequenceEqual(b.Data.AsSpan(0, n))) f.Enqueue(tag + " ratio prefix bytes differ");
            return;
        }

        if (!a.Data.AsSpan().SequenceEqual(b.Data)) f.Enqueue($"{tag} delivered bytes differ: {ka} {a.Data.Length}B vs {b.Data.Length}B");
    }

    [Fact] public void Gzip() => Fuzz("gzip", 1);
    [Fact] public void Deflate() => Fuzz("deflate", 2);
    [Fact] public void Brotli() => Fuzz("br", 3);
    [Fact] public void Zstd() => Fuzz("zstd", 4);
    [Fact] public void Chain() => Fuzz("chain", 5);
}
