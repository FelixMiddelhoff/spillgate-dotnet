using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;
using Xunit;

namespace Spillgate.Review.Tests;

/// <summary>A test that proves a reported finding. Skipped unless SPILLGATE_REVIEW=1 so the default suite stays green.</summary>
public sealed class ReviewFactAttribute : FactAttribute
{
    public ReviewFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPILLGATE_REVIEW") != "1")
        {
            Skip = "review proof test of a reported finding; run with SPILLGATE_REVIEW=1";
        }
    }
}

public static class R
{
    public static Decoder Dec(long max, Func<Limits, Limits>? cfg = null)
    {
        Limits l = Limits.Create(max);
        if (cfg != null) l = cfg(l);
        return Decoder.CreateBuilder(l).AddBrotli().AddZstd().Build();
    }

    public static byte[] Text(int n, int seed = 1)
    {
        var rnd = new Random(seed);
        string[] w = { "alpha", "beta", "gamma", "delta", "spill", "gate", "limit", "stream", "byte", "zip" };
        var sb = new StringBuilder();
        while (sb.Length < n) sb.Append(w[rnd.Next(w.Length)]).Append(rnd.Next(1000)).Append(' ');
        return Encoding.ASCII.GetBytes(sb.ToString(0, n));
    }

    public static byte[] Gzip(byte[] p) { var m = new MemoryStream(); using (var g = new GZipStream(m, CompressionLevel.Optimal, true)) g.Write(p); return m.ToArray(); }
    public static byte[] Zlib(byte[] p) { var m = new MemoryStream(); using (var g = new ZLibStream(m, CompressionLevel.Optimal, true)) g.Write(p); return m.ToArray(); }
    public static byte[] Deflate(byte[] p) { var m = new MemoryStream(); using (var g = new DeflateStream(m, CompressionLevel.Optimal, true)) g.Write(p); return m.ToArray(); }
    public static byte[] Br(byte[] p) { var o = new byte[BrotliEncoder.GetMaxCompressedLength(p.Length) + 16]; BrotliEncoder.TryCompress(p, o, out int w, 6, 22); return o.AsSpan(0, w).ToArray(); }

    public static (byte[] stream, string enc)[] Forms(byte[] p) => new[]
    {
        (Gzip(p), "gzip"), (Zlib(p), "deflate"), (Deflate(p), "deflate"), (Br(p), "br"),
        (Generators.ZstdStored(p, 18), "zstd"), (p, "identity"),
    };

    public sealed class Chunked : Stream
    {
        private readonly byte[] _d; private int _pos; private readonly Func<int> _next;
        public Chunked(byte[] d, Func<int> next) { _d = d; _next = next; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));
        public override int Read(Span<byte> b)
        {
            int n = Math.Min(Math.Min(_next(), b.Length), _d.Length - _pos);
            _d.AsSpan(_pos, n).CopyTo(b); _pos += n; return n;
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    public sealed class Res { public byte[] Data = Array.Empty<byte>(); public Exception? Err; public override string ToString() => $"{Data.Length}B {(Err == null ? "ok" : Err.GetType().Name + (Err is SpillgateException s ? ":" + s.Kind : ""))}"; }

    public static readonly string[] Modes = { "read1", "readbig", "span7", "readbyte", "copyto", "copytoasync", "readasyncmem", "readasyncarr", "zeromix", "readbigcount" };

    public static Res Run(Decoder d, Stream src, string[] enc, string mode)
    {
        var r = new Res();
        var ms = new MemoryStream();
        DecodedStream? s = null;
        try
        {
            s = d.Open(src, enc);
            switch (mode)
            {
                case "read1": { var b = new byte[1]; int n; while ((n = s.Read(b, 0, 1)) > 0) ms.Write(b, 0, n); break; }
                case "readbig": { var b = new byte[1 << 20]; int n; while ((n = s.Read(b, 0, b.Length)) > 0) ms.Write(b, 0, n); break; }
                case "readbigcount": { var b = new byte[16]; int n; while ((n = s.Read(b, 0, 16)) > 0) ms.Write(b, 0, n); break; }
                case "span7": { var b = new byte[7]; int n; while ((n = s.Read(b.AsSpan())) > 0) ms.Write(b, 0, n); break; }
                case "readbyte": { int c; while ((c = s.ReadByte()) >= 0) ms.WriteByte((byte)c); break; }
                case "copyto": s.CopyTo(ms); break;
                case "copytoasync": s.CopyToAsync(ms, 77).GetAwaiter().GetResult(); break;
                case "readasyncmem": { var b = new byte[100000]; int n; while ((n = s.ReadAsync(b.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult()) > 0) ms.Write(b, 0, n); break; }
                case "readasyncarr": { var b = new byte[333]; int n; while ((n = s.ReadAsync(b, 0, b.Length).GetAwaiter().GetResult()) > 0) ms.Write(b, 0, n); break; }
                case "zeromix": { var b = new byte[50]; int n; while (true) { s.Read(b, 0, 0); n = s.Read(b, 0, 50); if (n == 0) break; ms.Write(b, 0, n); } break; }
            }
        }
        catch (Exception e) { r.Err = e; }
        finally { s?.Dispose(); }
        r.Data = ms.ToArray();
        return r;
    }
}
