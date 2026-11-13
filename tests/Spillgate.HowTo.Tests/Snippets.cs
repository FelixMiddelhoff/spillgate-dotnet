using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spillgate.Brotli;
using Spillgate.Http;
using Spillgate.Zstd;

namespace Spillgate.HowTo.Tests;

/// <summary>
/// The code of docs/how-to.md. Every region below appears verbatim in the guide, and the tests of this project
/// run them, so the guide cannot describe code that no longer compiles or behaves differently.
/// </summary>
internal static class Snippets
{
    #region snippet:create-decoder
    // One decoder for the whole process: it is immutable and safe to share between threads.
    public static readonly Decoder Shared = Decoder.CreateBuilder(
            Limits.Create(maxOutput: 10 * 1024 * 1024) // required: no default, no "unlimited"
                .WithMaxInput(2 * 1024 * 1024)         // optional: cap on the compressed bytes taken from the source
                .WithMaxLayers(3))                     // default is 5
        .AddBrotli()                                   // br: Spillgate.Brotli
        .AddZstd()                                     // zstd: Spillgate.Zstd
        .Build();
    #endregion

    #region snippet:read-stream
    public static async Task<byte[]> ReadBody(Stream compressed, string? contentEncoding, CancellationToken ct)
    {
        // Open validates the Content-Encoding first: nothing is read from the source if it is unsupported.
        await using DecodedStream body = Shared.Open(compressed, new[] { contentEncoding }, leaveOpen: true, ct);
        var result = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            // Bytes may arrive before a later error: use them only after the loop ended cleanly.
            result.Write(buffer, 0, n);
        }

        return result.ToArray();
    }
    #endregion

    #region snippet:decode-all
    public static byte[] ReadSmallBody(Stream compressed, IEnumerable<string?> contentEncodingValues)
    {
        // The whole body or an exception, never partial data. The result buffer grows with what was produced.
        return Shared.DecodeAll(compressed, contentEncodingValues, CancellationToken.None);
    }
    #endregion

    #region snippet:http-client
    public static HttpClient CreateClient() => SpillgateHttpClient.Create(Shared);

    public static async Task<string> Download(HttpClient client, string url)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception e) when (SpillgateHttpErrors.Find(e) is { } failure)
        {
            // HttpClient may wrap the failure; Find returns the original SpillgateException.
            throw new InvalidOperationException("The server sent a body that was refused: " + failure.Kind.SpecName(), e);
        }
    }
    #endregion

    #region snippet:http-factory
    public static void ConfigureClients(IServiceCollection services)
    {
        // The primary handler does no automatic decompression; the Spillgate handler decodes with the limits.
        services.AddHttpClient("downloads").AddSpillgateDecoding(Shared);
    }
    #endregion

    #region snippet:aspnet
    public static WebApplication BuildServer()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        WebApplication app = builder.Build();

        // Request bodies with a Content-Encoding are decoded under the limits: 413, 415 or 400 on failure.
        app.UseSpillgateRequestDecoding(Shared);

        app.MapPost("/ingest", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            string text = await reader.ReadToEndAsync(); // decoded bytes; Content-Encoding is gone
            return Results.Text("received " + text.Length + " characters");
        });
        return app;
    }
    #endregion

    #region snippet:status-mapping
    public static (int Status, string Log) Classify(Exception e)
    {
        switch (SpillgateHttpErrors.Find(e))
        {
            case { Kind: ErrorKind.Internal or ErrorKind.MemoryLimit or ErrorKind.InvalidLimits } failure:
                return (500, "ALERT " + failure.Kind.SpecName()); // a bug or a sizing problem: page someone
            case { } failure:
                return (failure.Kind.HttpStatus(), "rejected " + failure.Kind.SpecName() + " in layer " + failure.Layer);
            default:
                return (500, "unexpected " + e.GetType().Name);
        }
    }
    #endregion

    #region snippet:cancellation
    public static async Task<int> CountWithDeadline(Stream compressed, string contentEncoding)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using DecodedStream body = Shared.Open(compressed, new[] { contentEncoding }, leaveOpen: true, deadline.Token);
        var buffer = new byte[16 * 1024];
        int total = 0;
        int n;
        while ((n = await body.ReadAsync(buffer, deadline.Token)) > 0) // OperationCanceledException when the time is up
        {
            total += n;
        }

        return total;
    }
    #endregion

    #region snippet:custom-codec
    // A codec for a token you define: it runs behind the same output, ratio and input limits as the built-in ones.
    public sealed class UpperCaseCodec : ICodec
    {
        public ICodecDecoder CreateDecoder() => new Decoder1();

        private sealed class Decoder1 : ICodecDecoder
        {
            public CodecStatus Decode(ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
            {
                int n = Math.Min(input.Length, output.Length);
                for (int i = 0; i < n; i++)
                {
                    byte b = input[i];
                    output[i] = b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;
                }

                consumed = n;
                written = n;
                if (n > 0)
                {
                    return CodecStatus.Continue;
                }

                return inputEnded ? CodecStatus.Done : CodecStatus.NeedInput;
            }

            public void Dispose()
            {
            }
        }
    }

    public static Decoder WithCustomCodec() =>
        Decoder.CreateBuilder(Limits.Create(1 << 20)).AddCodec("x-upper", new UpperCaseCodec()).Build();
    #endregion

    #region snippet:bomb-test
    // A 200 MB body of zeros compresses to about 200 KB. Your ingress must refuse it, not decode it.
    public static byte[] MakeGzipBomb()
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (int i = 0; i < 200; i++)
            {
                gzip.Write(zeros, 0, zeros.Length);
            }
        }

        return compressed.ToArray();
    }
    #endregion
}
