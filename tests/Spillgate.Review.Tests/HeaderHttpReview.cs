using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spillgate.Http;
using Spillgate.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Spillgate.Review.Tests;

public sealed class HeaderHttpReview
{
    private readonly ITestOutputHelper _out;
    public HeaderHttpReview(ITestOutputHelper o) { _out = o; }

    private static ErrorKind? KindOf(Decoder d, params string?[] enc)
    {
        try { using var s = d.Open(new MemoryStream(R.Gzip(R.Text(10))), enc); return null; }
        catch (SpillgateException e) { return e.Kind; }
    }

    [Fact]
    public void HeaderParsingFailsClosedAndBounded()
    {
        Decoder d = R.Dec(1000);
        Assert.Null(KindOf(d, "GZip"));
        Assert.Null(KindOf(d, " \tgzip\t ,identity, "));
        Assert.Null(KindOf(d, "identity", "gzip", ""));
        Assert.Null(KindOf(d, ",,gzip,,"));
        Assert.Null(KindOf(d, new string?[] { null }));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "gzip;q=1"));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "gzip\u0000"));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "gzip\r\nX: y"));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "gzéip"));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "gızip")); // dotless i must not fold to ascii
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "K")); // Kelvin sign
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "gzip  "));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "\"gzip\""));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "gzip gzip"));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, new string('a', 100_000)));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, new string(',', 5000)));
        Assert.Equal(ErrorKind.LayerLimit, KindOf(d, string.Join(",", Enumerable.Repeat("gzip", 17))));
        Assert.Equal(ErrorKind.LayerLimit, KindOf(d, string.Join(",", Enumerable.Repeat("identity", 17))));
        Assert.Equal(ErrorKind.UnsupportedEncoding, KindOf(d, "compress"));
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, "*"));
        Assert.Equal(ErrorKind.UnsupportedEncoding, KindOf(d, "identity;x".Replace(";x", string.Empty) + ",aes128gcm"));
        // many separate values
        Assert.Equal(ErrorKind.MalformedEncoding, KindOf(d, Enumerable.Repeat("gzip", 5000).ToArray()));
        // error text is bounded and does not echo the attacker string
        foreach (string evil in new[] { new string('Z', 10_000), "x" + new string('é', 500), "gzip" + new string('\u0001', 400) })
        {
            try { d.Open(new MemoryStream(), evil.Length > 1024 ? evil.Substring(0, 1000) : evil); }
            catch (SpillgateException e)
            {
                _out.WriteLine(e.Message.Length + ": " + e.Message);
                Assert.True(e.Message.Length < 200, e.Message);
            }
        }
    }

    [Fact]
    public void ConcurrentSharedDecoderAndDisposeRaces()
    {
        byte[] p = R.Text(200_000, 5);
        byte[] gz = R.Gzip(p), br = R.Br(p);
        Decoder d = R.Dec(1 << 20);
        var bad = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Parallel.For(0, 400, i =>
        {
            var rnd = new Random(i);
            DecodedStream s = d.Open(new MemoryStream(i % 2 == 0 ? gz : br), i % 2 == 0 ? "gzip" : "br");
            int mode = i % 4;
            var t = Task.Run(() =>
            {
                try
                {
                    var buf = new byte[1000];
                    long total = 0;
                    while (true)
                    {
                        int n = s.Read(buf, 0, buf.Length);
                        if (n == 0) break;
                        total += n;
                        if (total > (1 << 20)) bad.Enqueue("over limit");
                    }
                }
                catch (ObjectDisposedException) { }
                catch (SpillgateException) { }
                catch (Exception e) { bad.Enqueue(e.GetType().FullName + ": " + e.Message); }
            });
            if (mode != 0) Thread.Sleep(rnd.Next(0, 3));
            if (mode == 1) { s.Dispose(); s.Dispose(); }
            if (mode == 2) { s.DisposeAsync().AsTask().Wait(); s.Dispose(); }
            if (!t.Wait(20000)) bad.Enqueue("hang");
            s.Dispose();
            Assert.Throws<ObjectDisposedException>(() => s.Read(new byte[1], 0, 1));
        });
        Assert.True(bad.IsEmpty, string.Join("\n", bad.Take(10)));
    }

    [Fact]
    public async Task CancellationIsStickyAndTyped()
    {
        byte[] gz = R.Gzip(R.Text(100_000));
        Decoder d = R.Dec(1 << 20);
        using var cts = new CancellationTokenSource();
        await using DecodedStream s = d.Open(new MemoryStream(gz), new[] { "gzip" }, true, cts.Token);
        Assert.True(await s.ReadAsync(new byte[10]) > 0);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await s.ReadAsync(new byte[10]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await s.ReadAsync(new byte[10], CancellationToken.None));
        Assert.ThrowsAny<OperationCanceledException>(() => s.Read(new byte[10], 0, 10));
        // per-call token
        using var cts2 = new CancellationTokenSource();
        await using DecodedStream s2 = d.Open(new MemoryStream(gz), "gzip");
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await s2.ReadAsync(new byte[10], cts2.Token));
        Assert.True(await s2.ReadAsync(new byte[10]) > 0); // per-call cancel before work leaves state untouched
    }

    [Fact]
    public void SourceExceptionsAreContained()
    {
        Decoder d = R.Dec(1 << 20);
        foreach (Exception ex in new Exception[] { new IOException("io"), new InvalidOperationException("x"), new NullReferenceException(), new OutOfMemoryException(), new UnauthorizedAccessException() })
        {
            var r = R.Run(d, new Throwing(ex, R.Gzip(R.Text(5000))), new[] { "gzip" }, "readbig");
            _out.WriteLine(ex.GetType().Name + " -> " + r);
            Assert.IsType<SpillgateException>(r.Err);
        }
    }

    private sealed class Throwing : Stream
    {
        private readonly Exception _e; private readonly byte[] _d; private int _p;
        public Throwing(Exception e, byte[] d) { _e = e; _d = d; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 0; public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_p >= 20) throw _e;
            int n = Math.Min(count, Math.Min(5, _d.Length - _p)); Array.Copy(_d, _p, buffer, offset, n); _p += n; return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    // ---------------- HTTP ----------------

    private static async Task<(WebApplication app, string url)> Serve(Decoder d, Action<WebApplication> map)
    {
        WebApplicationBuilder b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        WebApplication app = b.Build();
        app.UseSpillgateRequestDecoding(d);
        map(app);
        await app.StartAsync();
        return (app, app.Urls.First());
    }

    [Fact]
    public async Task MiddlewareStatusMappingAndHeaderForms()
    {
        Decoder d = R.Dec(10_000);
        var (app, url) = await Serve(d, a =>
        {
            a.MapPost("/echo", async (HttpContext c) => { using var ms = new MemoryStream(); await c.Request.Body.CopyToAsync(ms); return Results.Text(ms.Length.ToString()); });
            a.MapPost("/started", async (HttpContext c) => { await c.Response.WriteAsync("hello"); await c.Response.Body.FlushAsync(); using var ms = new MemoryStream(); await c.Request.Body.CopyToAsync(ms); });
            a.MapPost("/swallow", async (HttpContext c) => { try { await c.Request.Body.CopyToAsync(Stream.Null); } catch { } return Results.Text("swallowed-200"); });
            a.MapPost("/throw", (HttpContext c) => { throw new InvalidOperationException("boom"); });
        });
        try
        {
            using var http = new HttpClient();
            async Task<(int, string)> Post(string path, byte[] body, params string[] ce)
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url + path) { Content = new ByteArrayContent(body) };
                foreach (string v in ce) req.Content.Headers.TryAddWithoutValidation("Content-Encoding", v);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, url + path) { Content = Clone(req.Content) });
                        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
                    }
                    catch (HttpRequestException) when (attempt < 4 && path != "/started" && path != "/swallow") { } // 413 may reset the connection while the client still uploads
                }
            }

            byte[] ok = R.Gzip(R.Text(5000)), bomb = Generators.ZerosOf("gzip", 100_000);
            Assert.Equal((200, "5000"), await Post("/echo", ok, "gzip"));
            Assert.Equal((200, "5000"), await Post("/echo", ok, "GZIP"));
            Assert.Equal((200, "5000"), await Post("/echo", R.Gzip(R.Gzip(R.Text(5000))), "gzip", "gzip")); // two header lines
            Assert.Equal((200, "5000"), await Post("/echo", R.Gzip(R.Gzip(R.Text(5000))), "gzip, gzip"));
            var (status, text) = await Post("/echo", bomb, "gzip");
            _out.WriteLine($"bomb -> {status} {text}");
            Assert.Equal(413, status);
            Assert.Equal(415, (await Post("/echo", ok, "compress")).Item1);
            Assert.Equal(400, (await Post("/echo", ok, "gzip;q=1")).Item1);
            Assert.Equal(400, (await Post("/echo", new byte[] { 1, 2, 3, 4 }, "gzip")).Item1);
            try { _out.WriteLine("swallow -> " + await Post("/swallow", bomb, "gzip")); } catch (HttpRequestException) { _out.WriteLine("swallow -> connection aborted"); }
            // observation: a handler that swallows the failure AND answers (response started) keeps its own 200; the middleware can only abort
            Assert.Equal(500, (await Post("/throw", ok, "gzip")).Item1);
            // identity body without Content-Encoding is outside spillgate (Kestrel limit applies); document only
            Assert.Equal((200, "30000"), await Post("/echo", new byte[30000]));
            // response already started: connection aborted, no 413 appended
            try { var r = await Post("/started", bomb, "gzip"); _out.WriteLine("started -> " + r); } catch (Exception e) { _out.WriteLine("started -> " + e.GetType().Name); }
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task ClientHandlerDecodesOnlyThroughSpillgate()
    {
        Decoder d = R.Dec(10_000);
        byte[] bomb = Generators.ZerosOf("gzip", 100_000);
        var seen = new List<string>();
        var (app, url) = await Serve(d, a => a.MapMethods("/{kind}", new[] { "GET", "HEAD" }, async (string kind, HttpContext c) =>
        {
            seen.Add(c.Request.Headers.AcceptEncoding.ToString());
            if (kind == "bomb") { c.Response.Headers.ContentEncoding = "gzip"; await c.Response.Body.WriteAsync(bomb); }
            else if (kind == "br") { c.Response.Headers.ContentEncoding = "br"; await c.Response.Body.WriteAsync(R.Br(R.Text(100))); }
            else if (kind == "unsolicited-deflate") { c.Response.Headers.ContentEncoding = "deflate"; await c.Response.Body.WriteAsync(R.Zlib(R.Text(100))); }
            else if (kind == "bigidentity") { await c.Response.Body.WriteAsync(new byte[50_000]); }
            else if (kind == "nobody") { c.Response.StatusCode = 304; c.Response.Headers.ContentEncoding = "gzip"; }
            else if (kind == "unknown") { c.Response.Headers.ContentEncoding = "zz"; await c.Response.WriteAsync("x"); }
        }));
        try
        {
            using HttpClient http = SpillgateHttpClient.Create(d);
            using (var r = await http.GetAsync(url + "/bomb", HttpCompletionOption.ResponseHeadersRead))
            {
                await using var s = await r.Content.ReadAsStreamAsync();
                var e = await Assert.ThrowsAnyAsync<IOException>(async () => await s.CopyToAsync(Stream.Null));
                Assert.Equal(ErrorKind.OutputLimit, Assert.IsType<SpillgateException>(e).Kind);
            }

            var ex2 = await Assert.ThrowsAnyAsync<Exception>(async () => await http.GetByteArrayAsync(url + "/bigidentity"));
            _out.WriteLine("bigidentity -> " + ex2.GetType().Name + " / " + ex2.InnerException?.GetType().Name);
            Assert.NotNull(SpillgateHttpErrors.Find(ex2));
            Assert.Equal(100, (await http.GetByteArrayAsync(url + "/br")).Length);
            Assert.Equal(100, (await http.GetByteArrayAsync(url + "/unsolicited-deflate")).Length);
            Assert.NotNull(SpillgateHttpErrors.Find(await Assert.ThrowsAnyAsync<Exception>(async () => await http.GetByteArrayAsync(url + "/unknown"))));
            try { using var r = await http.GetAsync(url + "/nobody"); _out.WriteLine("304 with Content-Encoding: gzip -> " + (int)r.StatusCode + " body " + (await r.Content.ReadAsByteArrayAsync()).Length); }
            catch (Exception e) { _out.WriteLine("304 with Content-Encoding: gzip -> " + e.GetType().Name + " " + e.Message); }
            using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url + "/bomb"))) { Assert.Equal(200, (int)head.StatusCode); }
            _out.WriteLine("Accept-Encoding seen: " + string.Join(" | ", seen.Distinct()));
        }
        finally { await app.StopAsync(); }

        Assert.Throws<ArgumentException>(() => new SpillgateHandler(d, new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }));
        Assert.Throws<ArgumentException>(() => new SpillgateHandler(d, new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip }));
        Assert.Throws<ArgumentException>(() => new SpillgateHandler(d, new DelegatingStub(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.Brotli })));
    }

    private static HttpContent Clone(HttpContent c)
    {
        var n = new ByteArrayContent(c.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        foreach (var h in c.Headers) n.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return n;
    }

    private sealed class DelegatingStub : DelegatingHandler { public DelegatingStub(HttpMessageHandler i) : base(i) { } }
}
