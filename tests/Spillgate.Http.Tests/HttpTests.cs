using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;
using Xunit;

namespace Spillgate.Http.Tests;

/// <summary>Both adapters against a real Kestrel server on the loopback interface.</summary>
public sealed class HttpTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseAddress = string.Empty;
    private string? _lastAcceptEncoding;

    private static Decoder SmallDecoder() => Decoder.CreateBuilder(Limits.Create(1 << 20).WithMaxInput(1 << 20))
        .AddBrotli()
        .AddZstd()
        .Build();

    private static byte[] Gzip(byte[] data)
    {
        var sink = new MemoryStream();
        using (var z = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data, 0, data.Length);
        }

        return sink.ToArray();
    }

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();

        // The server side of the client tests: it answers with whatever encoding the request asks for in the path.
        app.MapGet("/gzip", (HttpContext c) =>
        {
            _lastAcceptEncoding = c.Request.Headers.AcceptEncoding.ToString();
            c.Response.Headers.ContentEncoding = "gzip";
            c.Response.Headers["X-Test"] = "kept";
            return Results.Bytes(Gzip(Encoding.UTF8.GetBytes("hello decoded world")), "text/plain");
        });
        app.MapGet("/bomb", (HttpContext c) =>
        {
            c.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(Generators.ZerosOf("gzip", 4_000_000), "application/octet-stream"); // ~1 GB decoded
        });
        app.MapGet("/snappy", (HttpContext c) =>
        {
            c.Response.Headers.ContentEncoding = "snappy";
            return Results.Bytes(new byte[10], "application/octet-stream");
        });
        app.MapGet("/identity-big", () => Results.Bytes(new byte[3 << 20], "application/octet-stream"));
        app.MapMethods("/head", new[] { "HEAD" }, () => Results.Text("body"));

        // The server side of the middleware tests.
        app.UseSpillgateRequestDecoding(Decoder.Create(Limits.Create(1 << 20).WithMaxInput(1 << 20)));
        app.MapPost("/echo", async (HttpContext c) =>
        {
            using var sink = new MemoryStream();
            await c.Request.Body.CopyToAsync(sink);
            return Results.Text(
                "length=" + sink.Length + ";encoding=" + (c.Request.Headers.ContentEncoding.Count == 0 ? "none" : "present") + ";text=" + Encoding.UTF8.GetString(sink.ToArray(), 0, (int)Math.Min(sink.Length, 100)));
        });
        app.MapPost("/json", (Payload p) => Results.Text("name=" + p.Name));

        await app.StartAsync();
        _app = app;
        _baseAddress = app.Urls.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // -- HttpClient ----------------------------------------------------------------------------------------

    [Fact]
    [Covers("SG-R-HDR-08", "SG-R-CDC-02", "SG-R-API-02")]
    public async Task AClientDecodesAndKeepsTheOtherHeaders()
    {
        using HttpClient client = SpillgateHttpClient.Create(SmallDecoder());
        using HttpResponseMessage response = await client.GetAsync(_baseAddress + "/gzip", HttpCompletionOption.ResponseHeadersRead);
        Assert.False(response.Content.Headers.Contains("Content-Encoding"));
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal("hello decoded world", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("kept", response.Headers.GetValues("X-Test").Single());
        Assert.Equal("br, deflate, gzip, zstd", _lastAcceptEncoding);
    }

    [Fact]
    [Covers("SG-R-LAY-02", "SG-I-01", "SG-R-RAT-01")]
    public async Task ABombStopsAtTheLimitAndNeverReachesTheApplication()
    {
        using HttpClient client = SpillgateHttpClient.Create(Decoder.Create(Limits.Create(1 << 20).WithoutRatioLimit()));
        using HttpResponseMessage response = await client.GetAsync(_baseAddress + "/bomb", HttpCompletionOption.ResponseHeadersRead);
        await using Stream body = await response.Content.ReadAsStreamAsync();
        long total = 0;
        var buffer = new byte[64 * 1024];
        SpillgateException? failure = null;
        try
        {
            int n;
            while ((n = await body.ReadAsync(buffer)) > 0)
            {
                total += n;
            }
        }
        catch (SpillgateException e)
        {
            failure = e;
        }

        Assert.NotNull(failure);
        Assert.Equal(ErrorKind.OutputLimit, failure.Kind);
        Assert.Equal(1 << 20, total);

        // Buffered reading: the failure comes out of HttpClient, possibly wrapped; Find unwraps it.
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(_baseAddress + "/bomb"));
        SpillgateException? found = SpillgateHttpErrors.Find(thrown);
        Assert.NotNull(found);
        Assert.Equal(413, SpillgateHttpErrors.ToStatusCode(thrown));
    }

    [Fact]
    [Covers("SG-R-HDR-08", "SG-I-06", "SG-R-API-03")]
    public async Task AnUnsupportedEncodingFailsTheRequestWithATypedError()
    {
        using HttpClient client = SpillgateHttpClient.Create(SmallDecoder());
        SpillgateException e = await Assert.ThrowsAsync<SpillgateException>(() => client.GetAsync(_baseAddress + "/snappy"));
        Assert.Equal(ErrorKind.UnsupportedEncoding, e.Kind);
    }

    [Fact]
    [Covers("SG-R-CDC-10", "SG-R-LAY-01")]
    public async Task UnencodedBodiesAreBoundedToo()
    {
        using HttpClient client = SpillgateHttpClient.Create(Decoder.Create(Limits.Create(1 << 20)));
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(_baseAddress + "/identity-big"));
        Assert.Equal(ErrorKind.OutputLimit, SpillgateHttpErrors.Find(thrown)?.Kind);
    }

    [Fact]
    [Covers("SG-R-API-02")]
    public async Task HeadRequestsPassUntouched()
    {
        using HttpClient client = SpillgateHttpClient.Create(SmallDecoder());
        using var request = new HttpRequestMessage(HttpMethod.Head, _baseAddress + "/head");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    [Covers("SG-R-LIM-07", "SG-I-02")]
    public void ATransportThatDecompressesByItselfIsRefused()
    {
        Decoder decoder = SmallDecoder();
        Assert.Throws<ArgumentException>(() => new SpillgateHandler(decoder, new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip }));
        Assert.Throws<ArgumentException>(() => new SpillgateHandler(decoder, new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }));
        Assert.Throws<ArgumentException>(() => new SpillgateHandler(decoder, new Passthrough { InnerHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.Brotli } }));
        using var fine = new SpillgateHandler(decoder, new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None });
        Assert.NotNull(fine);
    }

    [Fact]
    [Covers("SG-R-LIM-07", "SG-I-02")]
    public async Task ADelegatingHandlerOnTopOfADecompressingTransportFailsOnTheFirstRequest()
    {
        var handler = new SpillgateHandler(SmallDecoder()) { InnerHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip } };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync(_baseAddress + "/gzip"));
    }

    [Fact]
    [Covers("SG-R-CDC-02", "SG-R-RDR-07")]
    public async Task TheClientFactoryExtensionWiresTheHandlerAndATransportWithoutDecompression()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("limited").AddSpillgateDecoding(SmallDecoder());
        using ServiceProvider provider = services.BuildServiceProvider();
        HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("limited");
        Assert.Equal("hello decoded world", await client.GetStringAsync(_baseAddress + "/gzip"));
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(_baseAddress + "/bomb"));
        Assert.Equal(ErrorKind.RatioLimit, SpillgateHttpErrors.Find(thrown)?.Kind);
    }

    // -- ASP.NET Core middleware -----------------------------------------------------------------------------

    private async Task<HttpResponseMessage> Post(string path, byte[] body, string? encoding, string contentType = "application/octet-stream")
    {
        using var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None });
        var request = new HttpRequestMessage(HttpMethod.Post, _baseAddress + path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (encoding is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    [Covers("SG-R-API-03", "SG-R-CDC-02")]
    public async Task ARequestBodyIsDecodedBeforeTheApplicationSeesIt()
    {
        byte[] text = Encoding.UTF8.GetBytes("a decoded request body");
        using HttpResponseMessage ok = await Post("/echo", Gzip(text), "gzip");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("length=22;encoding=none;text=a decoded request body", await ok.Content.ReadAsStringAsync());

        using HttpResponseMessage plain = await Post("/echo", text, null);
        Assert.Equal("length=22;encoding=none;text=a decoded request body", await plain.Content.ReadAsStringAsync());

        using HttpResponseMessage chained = await Post("/echo", Gzip(Gzip(text)), "gzip, gzip");
        Assert.Equal("length=22;encoding=none;text=a decoded request body", await chained.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("gzip", 400)]
    [InlineData("snappy", 415)]
    [InlineData("gzip;q=1", 400)]
    [InlineData("gzip,gzip,gzip,gzip,gzip,gzip", 413)]
    [Covers("SG-R-API-03", "SG-R-HDR-08", "SG-R-HDR-04", "SG-R-HDR-11", "SG-R-HDR-13")]
    public async Task HeaderFailuresAnswerWithTheirStatusAndNeverEchoTheRequest(string encoding, int status)
    {
        using HttpResponseMessage response = await Post("/echo", new byte[] { 1, 2, 3 }, encoding);
        Assert.Equal(status, (int)response.StatusCode);
        string text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("snappy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("q=1", text, StringComparison.Ordinal);
    }

    [Fact]
    [Covers("SG-R-LAY-02", "SG-I-01", "SG-R-LAY-04")]
    public async Task ABombInARequestBodyIsA413AndNotATruncatedBody()
    {
        byte[] bomb = Generators.ZerosOf("gzip", 40_000); // 10 MB decoded, limit is 1 MiB
        using HttpResponseMessage response = await Post("/echo", bomb, "gzip");
        // 413 for the output limit or the ratio limit, whichever trips first: both are limit failures.
        Assert.Equal(413, (int)response.StatusCode);
    }

    [Fact]
    [Covers("SG-R-CDC-06", "SG-R-CDC-02")]
    public async Task CorruptRequestBodiesAre400()
    {
        byte[] stream = Gzip(Encoding.UTF8.GetBytes(new string('x', 5000)));
        using HttpResponseMessage truncated = await Post("/echo", stream.AsSpan(0, stream.Length / 2).ToArray(), "gzip");
        Assert.Equal(400, (int)truncated.StatusCode);
        using HttpResponseMessage junk = await Post("/echo", new byte[100], "gzip");
        Assert.Equal(400, (int)junk.StatusCode);
    }

    [Fact]
    [Covers("SG-R-API-05", "SG-I-05")]
    public async Task AFailureInsideModelBindingIsStillMappedToTheRightStatus()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"name\":\"" + new string('a', 3 << 20) + "\"}");
        using HttpResponseMessage bomb = await Post("/json", Gzip(json), "gzip", "application/json");
        Assert.Equal(413, (int)bomb.StatusCode);

        byte[] small = Encoding.UTF8.GetBytes("{\"name\":\"spill\"}");
        using HttpResponseMessage ok = await Post("/json", Gzip(small), "gzip", "application/json");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("name=spill", await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    [Covers("SG-R-RDR-07", "SG-I-09")]
    public async Task ManyRequestsAtOnceShareOneDecoder()
    {
        byte[] text = Encoding.UTF8.GetBytes(new string('z', 20_000));
        byte[] body = Gzip(text);
        IEnumerable<Task<string>> tasks = Enumerable.Range(0, 40).Select(async _ =>
        {
            using HttpResponseMessage r = await Post("/echo", body, "gzip");
            return (await r.Content.ReadAsStringAsync()).Split(';')[0];
        });
        string[] results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal("length=20000", r));
    }

    public sealed record Payload(string Name);

    private sealed class Passthrough : DelegatingHandler
    {
    }
}
