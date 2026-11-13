using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Spillgate.Http.Tests;

/// <summary>Messages that cannot carry a body are not decoded, whatever Content-Encoding says (finding F-B).</summary>
public sealed class BodylessTests
{
    private static Decoder Small() => Decoder.Create(Limits.Create(1 << 20).WithMaxInput(1 << 20));

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _make;
        public Stub(Func<HttpResponseMessage> make) { _make = make; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_make());
    }

    private static HttpResponseMessage Response(int status, byte[] body, string encoding)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentEncoding.Add(encoding);
        return r;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(102)]
    [InlineData(199)]
    [InlineData(204)]
    [InlineData(304)]
    public async Task StatusWithoutBodyReadsAsEmpty(int status)
    {
        using var client = new HttpClient(new SpillgateHandler(Small(), new Stub(() => Response(status, Array.Empty<byte>(), "gzip"))));
        using HttpResponseMessage r = await client.GetAsync("http://x.test/");
        Assert.Empty(await r.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("snappy")]
    public async Task HeadReadsAsEmpty(string encoding)
    {
        using var client = new HttpClient(new SpillgateHandler(Small(), new Stub(() => Response(200, Array.Empty<byte>(), encoding))));
        using var req = new HttpRequestMessage(HttpMethod.Head, "http://x.test/");
        using HttpResponseMessage r = await client.SendAsync(req);
        Assert.Empty(await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ContentLengthZeroReadsAsEmpty()
    {
        using var client = new HttpClient(new SpillgateHandler(Small(), new Stub(() => Response(200, Array.Empty<byte>(), "gzip"))));
        using HttpResponseMessage r = await client.GetAsync("http://x.test/");
        Assert.Empty(await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NonEmptyBodyWithoutValidDataStillFails()
    {
        using var client = new HttpClient(new SpillgateHandler(Small(), new Stub(() => Response(200, Encoding.ASCII.GetBytes("not gzip"), "gzip"))));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using HttpResponseMessage r = await client.GetAsync("http://x.test/");
            await r.Content.ReadAsByteArrayAsync();
        });
    }

    [Fact]
    public async Task NoContentOverKestrelAndRequestWithoutBodyOverMiddleware()
    {
        WebApplicationBuilder b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        WebApplication app = b.Build();
        app.UseSpillgateRequestDecoding(Small());
        app.MapGet("/{code:int}", (int code, HttpContext c) => { c.Response.StatusCode = code; c.Response.Headers.ContentEncoding = "gzip"; return Task.CompletedTask; });
        app.MapPost("/echo", async (HttpContext c) =>
        {
            using var sink = new System.IO.MemoryStream();
            await c.Request.Body.CopyToAsync(sink);
            return Results.Text("length=" + sink.Length);
        });
        await app.StartAsync();
        try
        {
            string url = app.Urls.First();
            using HttpClient http = SpillgateHttpClient.Create(Small());
            foreach (int code in new[] { 204, 304 })
            {
                using HttpResponseMessage r = await http.GetAsync(url + "/" + code);
                Assert.Empty(await r.Content.ReadAsByteArrayAsync());
            }

            using HttpClient plain = new();
            using var content = new ByteArrayContent(Array.Empty<byte>());
            content.Headers.ContentEncoding.Add("gzip");
            using HttpResponseMessage e = await plain.PostAsync(url + "/echo", content);
            Assert.Equal(HttpStatusCode.OK, e.StatusCode);
            Assert.Equal("length=0", await e.Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
