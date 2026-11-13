# How-to guide: decoding untrusted compressed data with Spillgate

For developers who build services with Spillgate. It shows where to put the library, how to size the limits and what to do with the results. Every code block below is compiled and run by `tests/Spillgate.HowTo.Tests`; the test fails if the guide and the code drift apart.

Packages: `Spillgate` (core: gzip, deflate, identity), `Spillgate.Brotli`, `Spillgate.Zstd`, `Spillgate.Http` (HttpClient handler and ASP.NET Core middleware). All target net8.0 and net10.0; the core has no dependencies.

```csharp
// usings
using Spillgate;
using Spillgate.Brotli;
using Spillgate.Http;
using Spillgate.Zstd;
```

## 1. What Spillgate protects you from, and the .NET calls that do not

A hostile sender can ship a few kilobytes that expand to gigabytes (a decompression bomb), chain many encodings, lie about sizes, or hide trailing data. Spillgate decodes only under limits that you must set, counts the bytes it really produces, and fails with a typed error instead of returning a truncated body.

The unbounded calls in the .NET class library, which are the reason this library exists:

- `GZipStream`, `DeflateStream`, `ZLibStream` and `BrotliStream` decode whatever they are given. None has an output limit.
- `Stream.CopyTo`, `CopyToAsync`, `File.WriteAllBytes(stream.ToArray())`, `MemoryStream.ToArray()` and `HttpContent.ReadAsByteArrayAsync()` read to the end of a stream of any size. On a decoding stream this is the bomb.
- `HttpClientHandler.AutomaticDecompression` and `SocketsHttpHandler.AutomaticDecompression` decode inside the transport, before your code sees a byte. `SpillgateHandler` refuses to run on top of a transport that does.
- Declared sizes (`Content-Length`, gzip ISIZE, zstd frame content size) are claims. Spillgate never uses them to allow anything.

What Spillgate does not cover: the size of the compressed input (set `MaxInput` or your server's body limit), timeouts of a slow source, archive formats (zip, tar, 7z), and defects inside the runtime's own brotli decoder (see `docs/threat-model.md`).

## 2. Choose the limits

| Limit | Meaning | Default |
|-------|---------|---------|
| `maxOutput` | most bytes any layer may produce | required, 1 to 2^63-1, no unlimited value |
| `WithMaxLayers` | most non-identity layers in the chain | 5 (1 to 16) |
| `WithMaxRatio` | output bytes per consumed input byte | 1000 (1 to 2^31-1) |
| `WithRatioGrace` | output below this many bytes is not judged | 65536 (0 judges from the first byte) |
| `WithMaxInput` | most compressed bytes taken from the source | none |
| `WithoutRatioLimit` | the one named opt-out of the ratio check | off |

Sizing by data path:

| Data path | `maxOutput` | `MaxLayers` | Notes |
|-----------|-------------|-------------|-------|
| JSON or form API request | 1 to 10 MiB | 1 or 2 | the biggest legitimate payload plus headroom |
| Webhook receiver | 256 KiB to 1 MiB | 1 | payloads are small and known |
| File upload you store as is | your product limit | 1 | add `MaxInput`; the ratio limit may need `WithoutRatioLimit` for very repetitive files |
| Download from a service you trust only a little | the expected size times 2 | 2 | the response header is not a promise |
| Log or telemetry ingest | 16 to 64 MiB | 1 | text compresses 5:1 to 20:1, far below the default ratio |

Notes. The ratio limit judges the whole chain and each layer; real data rarely exceeds 100:1, long runs of zeros or one repeated record can reach 1000:1 and above, which is what the limit is for. If your product legitimately receives such data, raise `WithMaxRatio`, never remove `maxOutput`. Peak memory per call is `maxOutput` only for the whole-body functions, which grow their buffer with what was produced; streaming reads need a constant (below 1 MiB per layer for gzip and zstd with the default window cap of 8 MiB, up to 16 MiB of window for brotli).

## 3. Wire it in

### One decoder, created at start-up

```csharp
// One decoder for the whole process: it is immutable and safe to share between threads.
public static readonly Decoder Shared = Decoder.CreateBuilder(
        Limits.Create(maxOutput: 10 * 1024 * 1024) // required: no default, no "unlimited"
            .WithMaxInput(2 * 1024 * 1024)         // optional: cap on the compressed bytes taken from the source
            .WithMaxLayers(3))                     // default is 5
    .AddBrotli()                                   // br: Spillgate.Brotli
    .AddZstd()                                     // zstd: Spillgate.Zstd
    .Build();
```

`Build` throws `SpillgateException` with kind `InvalidLimits` or `InvalidOption` if anything is wrong, before any byte is read. The decoder is immutable and safe for concurrent use; every stream it opens is independent.

### HttpClient

```csharp
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
```

`SpillgateHandler` sets `Accept-Encoding` to the tokens the decoder knows, takes the body before the transport can decode it, removes `Content-Encoding` and `Content-Length` from the decoded content and enforces the limits while the body is read. An unsupported or malformed `Content-Encoding` fails the request with `SpillgateException` from `SendAsync`; limit and corruption failures surface where the body is read. `HttpClient` may wrap read failures in `HttpRequestException`, which is why `SpillgateHttpErrors.Find` exists. With `IHttpClientFactory`:

```csharp
public static void ConfigureClients(IServiceCollection services)
{
    // The primary handler does no automatic decompression; the Spillgate handler decodes with the limits.
    services.AddHttpClient("downloads").AddSpillgateDecoding(Shared);
}
```

### ASP.NET Core request bodies

```csharp
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
```

The middleware decodes bodies that carry a `Content-Encoding` and answers 413 (a limit), 415 (unsupported encoding) or 400 (malformed or corrupt data) without echoing anything the client sent. It also catches failures that frameworks swallow while binding a body (the minimal API answers 400 by itself): as long as nothing was sent yet, the status becomes the right one. Requests without `Content-Encoding` pass through; the server's own body size limit applies to them. Put the middleware before routing-dependent code that reads the body.

### Any other source: files, object storage, queues

```csharp
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
```

`Open` never closes the source (pass `leaveOpen: false` to hand ownership to the decoded stream). The stream returns 0 only at the clean end of a validated body; every limit, corruption and source failure throws and keeps throwing the same failure on later reads. Bytes may be delivered before a later error: use them only after the loop ended cleanly. If the whole body is small enough to hold in memory:

```csharp
public static byte[] ReadSmallBody(Stream compressed, IEnumerable<string?> contentEncodingValues)
{
    // The whole body or an exception, never partial data. The result buffer grows with what was produced.
    return Shared.DecodeAll(compressed, contentEncodingValues, CancellationToken.None);
}
```

`DecodeAll` returns the complete body or throws; it never returns partial data.

### Cancellation

```csharp
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
```

The token is observed on every read and every fetch from the source, at least once per 64 KiB. Cancellation surfaces as `OperationCanceledException` and is sticky. It is not a hard CPU bound inside one codec step; use `maxOutput`, `MaxInput` and your own timeouts for that. A source whose `ReadAsync` ignores the token cannot be interrupted while it blocks.

### Custom codecs

```csharp
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
```

A codec is registered on one decoder; there is no global registry. It passes through the same limiter, ratio meter and input meter as gzip. Return decoded bytes before you throw for a fault found in the same step, so what a reader receives before an error does not depend on its read size.

## 4. Handle the results

`SpillgateException.Kind` is the outcome class; `Layer` and `Limit` say where and which limit.

| Kind | Meaning | Server status | Client behavior | Log or metric |
|------|---------|---------------|-----------------|---------------|
| `OutputLimit` | a layer would produce more than `maxOutput` | 413 | do not retry, do not use the body | count, include layer |
| `RatioLimit` | expansion above `MaxRatio` | 413 | do not retry | count; many of these means an attack |
| `LayerLimit` | too many encodings | 413 | do not retry | count |
| `InputLimit` | stream needs more than `MaxInput` bytes | 413 | do not retry | count |
| `MemoryLimit` | codec window above its cap, or allocation failed | 413 | do not retry | alert: the cap or the host is too small |
| `UnsupportedEncoding` | well-formed token that is not supported | 415 | request another encoding or none | count |
| `MalformedEncoding` | bad `Content-Encoding` value | 400 | do not retry | count |
| `Corrupt` | invalid, truncated or trailing data | 400 | retry only if the transfer itself was cut | count |
| `SourceError` | the source stream failed (inner exception is the original) | your usual retry policy | retry with backoff | transport metrics |
| `Internal` | a codec or source broke its contract | 500 | none | alert and keep the input |
| `InvalidLimits`, `InvalidOption` | wrong configuration | 500 | none | fails at start-up |
| cancellation (`OperationCanceledException`) | the caller canceled | 503 or none | none | none |

```csharp
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
```

Rules that apply everywhere. Any error means the whole body is unusable, including bytes already read. Never retry a limit error with the same input. Never echo request data into responses or logs without bounds (the exception messages quote at most 32 bytes of attacker-controlled text, escaped). Alert on `Internal` and `MemoryLimit`. After launch, look at the distribution of `OutputLimit` and `RatioLimit` per endpoint: many legitimate clients near the limit mean it is too tight, a few clients far above it mean abuse.

## 5. Test your integration

Test every ingress with a bomb, a deep chain, an unknown encoding, a truncated body and a body with trailing bytes. A bomb is easy to make:

```csharp
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
```

Send it with `Content-Encoding: gzip` and expect 413, within a small amount of time and memory. Send `Content-Encoding: gzip, gzip, gzip, gzip, gzip, gzip` and expect 413, `snappy` and expect 415, `gzip;q=1` and expect 400, a gzip body cut in half and expect 400. Check the memory of the process while the bomb is decoded: it must not grow with the output.

## 6. Find and fix the places that decompress today

Search your code and your dependencies for: `GZipStream`, `DeflateStream`, `ZLibStream`, `BrotliStream`, `ZipArchive`, `ZipFile`, `AutomaticDecompression`, `UseRequestDecompression`, `UseResponseCompression` (compression of responses is not the issue, but check what else is registered), `CopyTo`, `ReadAsByteArrayAsync`, `ReadAllBytes`. Libraries that decode for you (SDK clients, gRPC, SignalR, model binders) usually use `HttpClient` underneath: give them a client built with `SpillgateHandler` where they allow it, or put `UseSpillgateRequestDecoding` in front of them on the server.

## 7. Pitfalls

- A decoder with a huge `maxOutput` is not protection. Size it to the data, not to the machine.
- `AutomaticDecompression` set on a handler under `SpillgateHandler` is refused at construction or on the first request; do not work around it.
- Do not read the decoded stream with `ReadAsByteArrayAsync` on a stream that has no limit: the limit lives in the decoder, so the whole-body call is safe only on a stream that came from it.
- The compressed input is not limited unless you set `MaxInput` or a server body size limit.
- Brotli inside the runtime is native code. Run patched runtimes (see `docs/threat-model.md`).
- A decoded stream is forward-only and not thread safe for concurrent reads; the `Decoder` is.

## 8. Checklist before you ship

- [ ] One shared `Decoder` with explicit `maxOutput`, `MaxLayers` and, where useful, `MaxInput`.
- [ ] No handler or middleware decompresses before Spillgate does.
- [ ] Every ingress maps failures to 413, 415 or 400 and never echoes request data.
- [ ] Alerts on `Internal` and `MemoryLimit`.
- [ ] Bomb, deep chain, unknown encoding, truncation and trailing-byte tests per ingress.
- [ ] The runtime is on a patched servicing release.
