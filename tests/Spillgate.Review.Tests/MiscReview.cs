using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Spillgate.Http;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Review.Tests;

public sealed class MiscReview
{
    [Fact]
    [Covers("SG-I-03", "SG-R-LAY-05")]
    public void ErrorsAreStickyOnEveryReadPath()
    {
        byte[] bomb = Generators.ZerosOf("gzip", 2000);
        foreach (string enc in new[] { "gzip", "br", "zstd" })
        {
            byte[] src = enc == "gzip" ? bomb : enc == "br" ? Generators.BrotliZeros(5_000_000, 22) : Generators.ZstdZeros(5_000_000, 20);
            Decoder d = R.Dec(1000, l => l.WithoutRatioLimit());
            DecodedStream s = d.Open(new MemoryStream(src), enc);
            var buf = new byte[4096];
            SpillgateException? first = null;
            try { while (s.Read(buf, 0, buf.Length) > 0) { } } catch (SpillgateException e) { first = e; }
            Assert.NotNull(first);
            Assert.Equal(ErrorKind.OutputLimit, first!.Kind);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(first.Kind, Assert.Throws<SpillgateException>(() => s.Read(buf, 0, 10)).Kind);
                Assert.Equal(first.Kind, Assert.Throws<SpillgateException>(() => s.Read(buf.AsSpan())).Kind);
                Assert.Equal(first.Kind, Assert.Throws<SpillgateException>(() => s.ReadByte()).Kind);
                Assert.Equal(first.Kind, Assert.ThrowsAsync<SpillgateException>(async () => await s.ReadAsync(buf)).GetAwaiter().GetResult().Kind);
                Assert.Equal(first.Kind, Assert.Throws<SpillgateException>(() => s.CopyTo(Stream.Null)).Kind);
            }

            s.Dispose();
            s.Dispose();
            Assert.Throws<ObjectDisposedException>(() => s.Read(buf, 0, 1));
        }
    }

    /// <summary>
    /// F-B (low, availability, not a limit issue): a bodyless response (304, 204) that carries a Content-Encoding header makes
    /// SpillgateHandler fail the read with corrupt (empty input is a truncated gzip) instead of returning an empty body.
    /// </summary>
    [ReviewFact]
    public async Task BodylessResponseWithContentEncodingReadsAsEmpty()
    {
        Decoder d = R.Dec(1000);
        WebApplicationBuilder b = WebApplication.CreateBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = b.Build();
        app.MapGet("/{code:int}", (int code, HttpContext c) => { c.Response.StatusCode = code; c.Response.Headers.ContentEncoding = "gzip"; return Task.CompletedTask; });
        await app.StartAsync();
        try
        {
            using HttpClient http = SpillgateHttpClient.Create(d);
            foreach (int code in new[] { 204, 304 })
            {
                using HttpResponseMessage r = await http.GetAsync(app.Urls.First() + "/" + code);
                Assert.Empty(await r.Content.ReadAsByteArrayAsync());
            }
        }
        finally { await app.StopAsync(); }
    }
}
