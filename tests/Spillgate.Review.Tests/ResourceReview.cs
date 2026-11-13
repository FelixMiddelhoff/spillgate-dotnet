using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;
using Xunit;
using Xunit.Abstractions;

namespace Spillgate.Review.Tests;

/// <summary>CPU and memory measurements under hostile streams (review items 4 and 5). Numbers go to the test output and to SPILLGATE_REVIEW_OUT.</summary>
public sealed class ResourceReview
{
    private readonly ITestOutputHelper _out;
    public ResourceReview(ITestOutputHelper o) { _out = o; }

    private static readonly string[] CodeLengthOrder = Array.Empty<string>();

    private (double ms, double nsPerByte, long alloc, long delivered, string outcome) Measure(string name, byte[] input, string enc, Decoder d)
    {
        // warm-up on a tiny part is not possible for a hostile stream; run once cold, once warm and report the warm run
        (double, double, long, long, string) one = default;
        for (int round = 0; round < 2; round++)
        {
            GC.Collect();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            long delivered = 0;
            string outcome = "ok";
            try
            {
                using DecodedStream s = d.Open(new MemoryStream(input), enc);
                var buf = new byte[65536];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    delivered += n;
                }
            }
            catch (SpillgateException e) { outcome = e.Kind.ToString(); }

            sw.Stop();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            one = (sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, input.Length), alloc, delivered, outcome);
        }

        string line = $"{name}: input={input.Length}B time={one.Item1:F1}ms {one.Item2:F0}ns/inputByte alloc={one.Item3 / 1024}KiB delivered={one.Item4}B outcome={one.Item5}";
        _out.WriteLine(line);
        string? path = Environment.GetEnvironmentVariable("SPILLGATE_REVIEW_OUT");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + "\n");
        }

        return one;
    }

    private static Decoder Big() => R.Dec(1L << 40, l => l.WithoutRatioLimit());

    // ---- brotli hostile (port of spillgate-js packages/br/tests/hostile.ts) ----

    private static void LiteralTree(BitWriter w, string shape)
    {
        int[] order = { 1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        if (shape == "single") { w.Bits(1, 2); w.Bits(0, 2); w.Bits(0x41, 8); }
        else if (shape == "four") { w.Bits(1, 2); w.Bits(3, 2); foreach (int s in new[] { 0x41, 0x42, 0x43, 0x44 }) { w.Bits(s, 8); } w.Bits(0, 1); }
        else
        {
            w.Bits(0, 2);
            foreach (int symbol in order)
            {
                if (symbol == 8 || symbol == 9)
                {
                    w.Bits(7, 4); if (symbol == 9)
                    {
                        break;
                    }
                }
                else
                {
                    w.Bits(0, 2);
                }
            }

            for (int i = 0; i < 256; i++)
            {
                w.Bits(0, 1);
            }
        }
    }

    public static byte[] HostileBrotli(int count, string shape, bool distanceTrees)
    {
        var w = new BitWriter();
        w.Bits(0, 1);
        for (int i = 0; i < count; i++)
        {
            w.Bits(0, 1); w.Bits(0, 2); w.Bits(0, 16); w.Bits(0, 1); w.Bits(0, 3); w.Bits(0, 2); w.Bits(0, 4); w.Bits(0, 2);
            w.Bits(1, 1); w.Bits(7, 3); w.Bits(127, 7);
            w.Bits(0, 1); w.Bits(1, 2); w.Bits(0, 2); w.Bits(0, 8); w.Bits(0, 1);
            if (distanceTrees)
            {
                w.Bits(1, 1); w.Bits(7, 3); w.Bits(127, 7);
                w.Bits(0, 1); w.Bits(1, 2); w.Bits(0, 2); w.Bits(0, 8); w.Bits(0, 1);
            }
            else
            {
                w.Bits(0, 1);
            }

            for (int t = 0; t < 256; t++)
            {
                LiteralTree(w, shape);
            }

            w.Bits(1, 2); w.Bits(0, 2); w.Bits(8, 10);
            for (int t = 0; t < (distanceTrees ? 256 : 1); t++) { w.Bits(1, 2); w.Bits(0, 2); w.Bits(0, 6); }
            w.Bits(0, shape == "single" ? 0 : shape == "four" ? 2 : 8);
        }

        w.Bits(1, 1); w.Bits(1, 1);
        return w.Finish();
    }

    public static byte[] HostileBlockTypes(int count, bool dist)
    {
        var w = new BitWriter();
        w.Bits(0, 1);
        void Bt() { w.Bits(1, 1); w.Bits(7, 3); w.Bits(127, 7); w.Bits(1, 2); w.Bits(0, 2); w.Bits(0, 9); w.Bits(1, 2); w.Bits(0, 2); w.Bits(0, 5); w.Bits(0, 2); }
        for (int i = 0; i < count; i++)
        {
            w.Bits(0, 1); w.Bits(0, 2); w.Bits(0, 16); w.Bits(0, 1);
            Bt();
            w.Bits(0, 1);
            if (dist)
            {
                Bt();
            }
            else
            {
                w.Bits(0, 1);
            }

            w.Bits(0, 2); w.Bits(0, 4);
            for (int t = 0; t < 256; t++)
            {
                w.Bits(0, 2);
            }

            w.Bits(0, 1); w.Bits(0, 1);
            w.Bits(1, 2); w.Bits(0, 2); w.Bits(0x41, 8);
            w.Bits(1, 2); w.Bits(0, 2); w.Bits(8, 10);
            w.Bits(1, 2); w.Bits(0, 2); w.Bits(0, 6);
        }

        w.Bits(1, 1); w.Bits(1, 1);
        return w.Finish();
    }

    [Theory]
    [InlineData("single", false)]
    [InlineData("four", true)]
    [InlineData("all8", true)]
    public void BrotliHostileMetaBlocks(string shape, bool dist)
    {
        byte[] s = HostileBrotli(4000, shape, dist);
        var r = Measure($"brotli F3 4000 metablocks x256 trees shape={shape} dist={dist}", s, "br", Big());
        Assert.True(r.ms < 30_000, "brotli hostile metablocks took " + r.ms + " ms");
        Assert.Equal("ok", r.outcome);
        Assert.Equal(4000, r.delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrotliHostileBlockTypes(bool dist)
    {
        byte[] s = HostileBlockTypes(4000, dist);
        var r = Measure($"brotli F3b 4000 metablocks x256 block types dist={dist}", s, "br", Big());
        Assert.True(r.ms < 30_000);
    }

    // ---- zstd ----

    [Fact]
    public void ZstdSkippableAndEmptyFrameSpam()
    {
        var ms = new MemoryStream();
        byte[] skip = Generators.ZstdSkippable(0);
        for (int i = 0; i < 1_000_000; i++)
        {
            ms.Write(skip);
        }

        var r = Measure("zstd 1,000,000 empty skippable frames", ms.ToArray(), "zstd", Big());
        Assert.True(r.ms < 30_000);

        ms = new MemoryStream();
        byte[] empty = Generators.ZstdStored(Array.Empty<byte>(), 10);
        _out.WriteLine("empty frame bytes=" + empty.Length);
        for (int i = 0; i < 300_000; i++)
        {
            ms.Write(empty);
        }

        r = Measure("zstd 300,000 empty frames (window 1 KiB)", ms.ToArray(), "zstd", Big());
        Assert.True(r.ms < 30_000);

        byte[] one = Generators.ZstdStored(new byte[] { 1 }, 27);
        ms = new MemoryStream();
        for (int i = 0; i < 100_000; i++)
        {
            ms.Write(one);
        }

        r = Measure("zstd 100,000 one-byte frames declaring a 128 MiB window (windowLog 27, cap 8 MiB)", ms.ToArray(), "zstd", Big());
        Assert.True(r.ms < 30_000);
    }

    [Fact]
    public void ZstdDeclaredWindowIsNotAllocatedUpFront()
    {
        var d = Decoder.CreateBuilder(Limits.Create(1L << 40).WithoutRatioLimit()).AddZstd(ZstdOptions.Create(1L << 30)).Build();
        byte[] s = Generators.ZstdZeros(100, 30);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (DecodedStream st = d.Open(new MemoryStream(s), "zstd"))
        {
            var b = new byte[10];
            Assert.Equal(10, st.Read(b, 0, 10));
        }

        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"zstd windowLog 30 declared, 100 bytes of data: allocated {alloc / 1024} KiB");
        Assert.True(alloc < 8 * 1024 * 1024, "allocated " + alloc);

        // lying frame content size (single segment, FCS = 2^40) on a stream that ends: must fail without allocating the window
        var hdr = new byte[] { 0x28, 0xb5, 0x2f, 0xfd, 0xe0, 0, 0, 0, 0, 0, 1, 0, 0 /* FCS 8 bytes: 2^40 */, 1, 0, 0 };
        before = GC.GetAllocatedBytesForCurrentThread();
        try { using var st = d.Open(new MemoryStream(hdr), "zstd"); st.Read(new byte[10], 0, 10); } catch (SpillgateException e) { _out.WriteLine("lying FCS: " + e.Kind); }
        alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        _out.WriteLine($"lying FCS 2^40 allocated {alloc / 1024} KiB");
        Assert.True(alloc < 8 * 1024 * 1024, "allocated " + alloc);
    }

    // ---- gzip / deflate ----

    [Fact]
    public void GzipMemberSpamAndHugeHeaderFields()
    {
        byte[] member = Generators.GzipStored(Array.Empty<byte>());
        var ms = new MemoryStream();
        for (int i = 0; i < 1_000_000; i++)
        {
            ms.Write(member);
        }

        var r = Measure($"gzip 1,000,000 empty members ({member.Length}B each)", ms.ToArray(), "gzip", Big());
        Assert.True(r.ms < 60_000);

        // FNAME 50 MB without terminator, then end: must not buffer
        var name = new byte[50_000_000];
        Array.Fill(name, (byte)'a');
        byte[] hdr = { 0x1f, 0x8b, 8, 8, 0, 0, 0, 0, 0, 255 };
        r = Measure("gzip FNAME 50 MB unterminated", Generators.Concat(hdr, name), "gzip", Big());
        Assert.True(r.alloc < 10 * 1024 * 1024, "alloc " + r.alloc);
        Assert.True(r.ms < 30_000);
        byte[] hdr2 = { 0x1f, 0x8b, 8, 16, 0, 0, 0, 0, 0, 255 };
        r = Measure("gzip FCOMMENT 50 MB unterminated", Generators.Concat(hdr2, name), "gzip", Big());
        Assert.True(r.alloc < 10 * 1024 * 1024);
        byte[] hdr3 = { 0x1f, 0x8b, 8, 4, 0, 0, 0, 0, 0, 255, 0xff, 0xff };
        r = Measure("gzip FEXTRA 65535 declared, truncated", Generators.Concat(hdr3, new byte[1000]), "gzip", Big());
        Assert.Equal("Corrupt", r.outcome);
    }

    private static byte[] DynamicEmptyBlocks(int count)
    {
        var w = new BitWriter();
        int[] lens = new int[316];
        for (int i = 0; i < 286; i++)
        {
            lens[i] = i < 144 ? 8 : i < 148 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8;
        }

        lens = lens.Take(316).ToArray(); for (int i = 286; i < 316; i++)
        {
            lens[i] = i < 288 ? 4 : 5;
        }

        for (int b = 0; b < count; b++)
        {
            w.Bits(b == count - 1 ? 1 : 0, 1);
            w.Bits(2, 2);
            w.Bits(29, 5);
            w.Bits(29, 5);
            w.Bits(8, 4); // 12 code-length code lengths
            int[] clOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4 };
            foreach (int sym in clOrder)
            {
                w.Bits(sym is 8 or 7 or 5 ? 2 : sym is 9 or 4 ? 3 : 0, 3);
            }

            foreach (int l in lens)
            {
                if (l == 5)
                {
                    w.Code(0, 2);
                }
                else if (l == 7)
                {
                    w.Code(1, 2);
                }
                else if (l == 8)
                {
                    w.Code(2, 2);
                }
                else if (l == 4)
                {
                    w.Code(6, 3);
                }
                else
                {
                    w.Code(7, 3);
                }
            }

            w.Code(0, 7); // EOB: first canonical code of length 7
        }

        return w.Finish();
    }

    [Fact]
    public void DeflateDynamicHeaderSpam()
    {
        byte[] raw = DynamicEmptyBlocks(20000);
        Decoder d = Big();
        // sanity: the runtime accepts the stream too
        using (var ds = new System.IO.Compression.DeflateStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress))
        {
            var sink = new MemoryStream();
            ds.CopyTo(sink);
            Assert.Equal(0, sink.Length);
        }

        var r = Measure("deflate 20,000 dynamic blocks (HLIT 286, HDIST 32), each ~85 B, empty output", raw, "deflate", d);
        Assert.Equal("ok", r.outcome);
        Assert.True(r.ms < 30_000);
        byte[] gz = Generators.GzipMember(raw, 0, 0);
        r = Measure("gzip around the same", gz, "gzip", d);
        Assert.Equal("ok", r.outcome);
    }

    // ---- chain depth and per-layer memory ----

    [Fact]
    public void ChainOfSixteenLayers()
    {
        byte[] p = R.Text(1000);
        byte[] s = p;
        var enc = new List<string>();
        for (int i = 0; i < 16; i++) { s = Generators.GzipStored(s); enc.Add("gzip"); }
        Decoder d = R.Dec(1 << 20, l => l.WithMaxLayers(16).WithoutRatioLimit());
        GC.Collect();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        byte[] got = d.DecodeAll(new MemoryStream(s), enc.ToArray());
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
        _out.WriteLine($"16 gzip layers (stored): {sw.Elapsed.TotalMilliseconds:F1} ms, allocated {alloc / 1024} KiB (~{alloc / 16 / 1024} KiB/layer)");
        Assert.Equal(p, got);
        enc.Add("gzip");
        Assert.Equal(ErrorKind.LayerLimit, Assert.Throws<SpillgateException>(() => d.Open(new MemoryStream(s), enc.ToArray())).Kind);
    }

    [Fact]
    public void BrotliLargeWindowDeclaredAllocation()
    {
        byte[] s = Generators.BrotliStored(R.Text(100), 24);
        var d = R.Dec(1 << 20);
        long ws0 = Process.GetCurrentProcess().PrivateMemorySize64;
        long peak = 0;
        for (int i = 0; i < 20; i++)
        {
            using DecodedStream st = d.Open(new MemoryStream(s), "br");
            Assert.Equal(100, st.Read(new byte[1000], 0, 1000));
            peak = Math.Max(peak, Process.GetCurrentProcess().PrivateMemorySize64);
        }

        _out.WriteLine($"brotli wbits=24, 100 B body: private memory delta while open {(peak - ws0) / 1024} KiB");
        // large-window magic: rejected before any allocation
        long before = GC.GetAllocatedBytesForCurrentThread();
        var e = Assert.Throws<SpillgateException>(() => d.DecodeAll(new MemoryStream(Generators.BrotliLargeWindowPrefix()), "br"));
        _out.WriteLine("large window prefix -> " + e.Kind);
    }
}
