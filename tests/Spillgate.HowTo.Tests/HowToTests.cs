using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Spillgate.Http;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.HowTo.Tests;

/// <summary>Runs the code of docs/how-to.md and checks that the guide shows exactly that code.</summary>
public sealed partial class HowToTests
{
    private static byte[] Gzip(string text)
    {
        var sink = new MemoryStream();
        using (var z = new GZipStream(sink, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            z.Write(bytes, 0, bytes.Length);
        }

        return sink.ToArray();
    }

    [Fact]
    [Covers("SG-R-API-01")]
    public async Task ReadBodyDecodesAndRefuses()
    {
        Assert.Equal("hello", Encoding.UTF8.GetString(await Snippets.ReadBody(new MemoryStream(Gzip("hello")), "gzip", CancellationToken.None)));
        Assert.Equal("hello", Encoding.UTF8.GetString(await Snippets.ReadBody(new MemoryStream(Encoding.UTF8.GetBytes("hello")), null, CancellationToken.None)));
        SpillgateException unsupported = await Assert.ThrowsAsync<SpillgateException>(() => Snippets.ReadBody(new MemoryStream(), "snappy", CancellationToken.None));
        Assert.Equal(ErrorKind.UnsupportedEncoding, unsupported.Kind);
        SpillgateException bomb = await Assert.ThrowsAsync<SpillgateException>(() => Snippets.ReadBody(new MemoryStream(Snippets.MakeGzipBomb()), "gzip", CancellationToken.None));
        Assert.True(bomb.Kind is ErrorKind.RatioLimit or ErrorKind.OutputLimit, bomb.Kind.ToString());
    }

    [Fact]
    [Covers("SG-R-API-02")]
    public void DecodeAllReturnsTheWholeBodyOrThrows()
    {
        Assert.Equal("data", Encoding.UTF8.GetString(Snippets.ReadSmallBody(new MemoryStream(Gzip("data")), new[] { "gzip" })));
        Assert.Throws<SpillgateException>(() => Snippets.ReadSmallBody(new MemoryStream(Snippets.MakeGzipBomb()), new[] { "gzip" }));
    }

    [Fact]
    [Covers("SG-R-LIM-01")]
    public void TheSharedDecoderHasTheDocumentedLimitsAndCodecs()
    {
        Assert.Contains("br", Snippets.Shared.SupportedEncodings);
        Assert.Contains("zstd", Snippets.Shared.SupportedEncodings);
        Assert.Equal(ErrorKind.LayerLimit, Assert.Throws<SpillgateException>(() => Snippets.Shared.Open(new MemoryStream(), "gzip", "gzip", "gzip", "gzip")).Kind);
    }

    [Fact]
    [Covers("SG-R-API-04")]
    public async Task CancellationWithADeadline()
    {
        Assert.Equal(5, await Snippets.CountWithDeadline(new MemoryStream(Gzip("12345")), "gzip"));
    }

    [Fact]
    [Covers("SG-R-CDC-09", "SG-R-CDC-01")]
    public void ACustomCodecRunsBehindTheLimits()
    {
        Decoder decoder = Snippets.WithCustomCodec();
        Assert.Equal("SHOUT", Encoding.UTF8.GetString(decoder.DecodeAll(new MemoryStream(Encoding.UTF8.GetBytes("shout")), "x-upper")));
    }

    [Fact]
    [Covers("SG-R-API-05")]
    public void StatusMappingClassifiesFailures()
    {
        Assert.Equal((413, "rejected output_limit in layer 1"), Snippets.Classify(RealOutputLimit()));
        Assert.Equal(415, Snippets.Classify(new SpillgateException(ErrorKind.UnsupportedEncoding, "x")).Status);
        Assert.Equal(400, Snippets.Classify(new SpillgateException(ErrorKind.Corrupt, "x")).Status);
        Assert.Equal((500, "ALERT internal"), Snippets.Classify(new SpillgateException(ErrorKind.Internal, "x")));
        Assert.Equal(500, Snippets.Classify(new InvalidOperationException("x")).Status);
        Assert.Equal(413, Snippets.Classify(new HttpRequestException("wrapped", new IOException("io", new SpillgateException(ErrorKind.RatioLimit, "x")))).Status);
    }

    [Fact]
    [Covers("SG-R-API-02")]
    public async Task TheClientAndTheServerOfTheGuideWorkTogether()
    {
        WebApplication app = Snippets.BuildServer();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            string address = app.Urls.First();
            using var raw = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None });

            using var good = new HttpRequestMessage(HttpMethod.Post, address + "/ingest") { Content = new ByteArrayContent(Gzip("twelve bytes")) };
            good.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            using HttpResponseMessage ok = await raw.SendAsync(good);
            Assert.Equal("received 12 characters", await ok.Content.ReadAsStringAsync());

            using var bad = new HttpRequestMessage(HttpMethod.Post, address + "/ingest") { Content = new ByteArrayContent(Snippets.MakeGzipBomb()) };
            bad.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            using HttpResponseMessage refused = await raw.SendAsync(bad);
            Assert.Equal(413, (int)refused.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static SpillgateException RealOutputLimit()
    {
        Decoder d = Decoder.Create(Limits.Create(1));
        using DecodedStream stream = d.Open(new MemoryStream(new byte[5]));
        return Assert.Throws<SpillgateException>(() => stream.CopyTo(Stream.Null));
    }

    [Fact]
    [Covers("SG-R-API-02")]
    public async Task TheClientOfTheGuideRefusesABombAndReadsAGoodBody()
    {
        using var bombClient = new HttpClient(new SpillgateHandler(Snippets.Shared, new Stub(Snippets.MakeGzipBomb())));
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Snippets.Download(bombClient, "http://stub/"));
        Assert.Contains("refused", refused.Message, StringComparison.Ordinal);
        using var goodClient = new HttpClient(new SpillgateHandler(Snippets.Shared, new Stub(Gzip("fine"))));
        Assert.Equal("fine", await Snippets.Download(goodClient, "http://stub/"));
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly byte[] _body;

        public Stub(byte[] body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip");
            return Task.FromResult(response);
        }
    }

    [Fact]
    [Covers("SG-R-API-02")]
    public void TheFactoryExtensionRegistersANamedClient()
    {
        var services = new ServiceCollection();
        Snippets.ConfigureClients(services);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>().CreateClient("downloads"));
    }

    // -- the guide shows what is compiled ---------------------------------------------------------------------

    [Fact]
    [Covers("SG-R-API-01")]
    public void EveryRegionOfTheSnippetsAppearsVerbatimInTheGuideAndNothingElseIsShownAsCSharp()
    {
        string guide = File.ReadAllText(Repo.Path("docs", "how-to.md"));
        string source = File.ReadAllText(Repo.Path("tests", "Spillgate.HowTo.Tests", "Snippets.cs"));
        var regions = RegionPattern().Matches(source).ToDictionary(m => m.Groups[1].Value, m => Normalize(m.Groups[2].Value));
        Assert.NotEmpty(regions);

        var shown = FencePattern().Matches(guide).Select(m => Normalize(m.Groups[1].Value)).ToList();
        foreach ((string name, string code) in regions)
        {
            Assert.True(shown.Any(block => block == code), "the guide does not show snippet " + name + " verbatim");
        }

        foreach (string block in shown)
        {
            Assert.True(regions.ContainsValue(block) || block.StartsWith("// usings", StringComparison.Ordinal), "the guide shows C# that is not compiled by the tests:\n" + block);
        }
    }

    private static string Normalize(string code)
    {
        string[] lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).ToArray();
        int first = Array.FindIndex(lines, l => l.Length > 0);
        int last = Array.FindLastIndex(lines, l => l.Length > 0);
        if (first < 0)
        {
            return string.Empty;
        }

        lines = lines[first..(last + 1)];
        int indent = lines.Where(l => l.Length > 0).Min(l => l.Length - l.TrimStart().Length);
        return string.Join('\n'.ToString(), lines.Select(l => l.Length == 0 ? l : l.Substring(indent)));
    }

    [GeneratedRegex(@"#region snippet:([a-z-]+)\r?\n(.*?)#endregion", RegexOptions.Singleline)]
    private static partial Regex RegionPattern();

    [GeneratedRegex("```csharp\\r?\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();
}
