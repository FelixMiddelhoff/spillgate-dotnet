# spillgate for .NET

The gate that stops decompression bombs at the door. Spillgate decodes untrusted compressed data (HTTP `Content-Encoding` chains and raw gzip, deflate, brotli and zstd streams) under mandatory limits, so that a hostile input can cause at most a clean, typed error and bounded resource use.

This is the C# implementation of the language-neutral [spillgate specification](spec/SPEC.md). It passes the shared conformance corpus (370 vectors) unchanged.

**New here? Read the [how-to guide](docs/how-to.md)**: where to put the library, how to size the limits, what to do with the results.

| Package | Contents |
|---------|----------|
| `Spillgate` | core: identity, gzip, deflate (zlib and raw), limits, streams, no dependencies |
| `Spillgate.Brotli` | brotli on the decoder of the .NET runtime, with Spillgate's own framing and window cap |
| `Spillgate.Zstd` | a managed zstd decoder (no unsafe code, no native code) |
| `Spillgate.Http` | `HttpClient` handler and ASP.NET Core middleware |

All packages target `net8.0` and `net10.0`.

## Why

Recurring vulnerabilities in decompression handling share a few mistakes: a limit that covers only some codecs, a limit that an input value switches off (a declared size of zero, `max_length=0`), unbounded chains of encodings, streaming decoders that ignore the caller's bound, and transparent decoding with no limit at all. In .NET, `GZipStream`, `DeflateStream`, `ZLibStream` and `BrotliStream` decode whatever they are given, and `AutomaticDecompression` decodes before your code runs.

## Rules

1. The output limit is mandatory. There is no default and no unlimited value.
2. Declared sizes are never trusted. Only bytes actually produced count.
3. All codecs are treated identically: same output, ratio, input and layer limits.
4. Exceeding a limit is a typed error (`SpillgateException`, `Kind`), never silent truncation.
5. Chain length is limited (default 5) and so is the expansion ratio (default 1000:1 after 64 KiB).
6. Unknown encodings fail closed. Trailing bytes after a complete stream are corrupt.
7. The core has no dependencies and no unsafe code.

## Quick start

```csharp
using Spillgate;
using Spillgate.Brotli;
using Spillgate.Zstd;

Decoder decoder = Decoder.CreateBuilder(Limits.Create(maxOutput: 10 * 1024 * 1024))
    .AddBrotli()
    .AddZstd()
    .Build();

await using DecodedStream body = decoder.Open(networkStream, "gzip");
byte[] all = decoder.DecodeAll(networkStream, "br");   // whole body or an exception, never partial data
```

With HTTP:

```csharp
using HttpClient client = SpillgateHttpClient.Create(decoder);          // Spillgate.Http
app.UseSpillgateRequestDecoding(decoder);                               // ASP.NET Core
```

## Documents

- [docs/how-to.md](docs/how-to.md): using it in a service
- [docs/threat-model.md](docs/threat-model.md): what is protected, measured costs, residual risks, runtime patch levels (brotli needs .NET 8.0.33, 9.0.22 or 10.0.14 or later)
- [docs/design.md](docs/design.md): design decisions and the .NET traps with the tests that cover them
- [spec/SPEC.md](spec/SPEC.md) and [conformance/](conformance/SCHEMA.md): the specification and the corpus
- [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), [CHANGELOG.md](CHANGELOG.md), [RELEASING.md](RELEASING.md)

## License

MIT, see [LICENSE](LICENSE).
