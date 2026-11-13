# Changelog

## Unreleased (intended as 0.1.0)

First release of the .NET implementation of spillgate.

- `Spillgate`: identity, gzip (multi-member, strict framing), deflate (zlib with raw fallback, `strict_zlib` option) on an own inflate with exact consumption accounting; mandatory `MaxOutput`, `MaxLayers`, `MaxRatio` with grace, `MaxInput`; strict `Content-Encoding` parsing; typed sticky errors; synchronous and asynchronous reads with cancellation; extension codecs registered per decoder.
- `Spillgate.Brotli`: brotli on the runtime's decoder with the window read from the first byte and capped, large-window streams refused, input handed over in fixed steps and decoder output taken in fixed steps, so that the bytes delivered before an error and the outcome do not depend on source chunking or read size.
- `Spillgate.Zstd`: managed zstd decoder with header, window, dictionary and block checks before allocation, content checksum, skippable and concatenated frames.
- `Spillgate.Http`: `HttpClient` handler (explicit `Accept-Encoding`, refuses transports that decompress), `IHttpClientFactory` extension, ASP.NET Core request decoding middleware with 413/415/400 mapping.
- `Spillgate.Http`: responses to HEAD, 1xx, 204, 304 and with `Content-Length: 0`, and requests with `Content-Length: 0`, are not decoded even when `Content-Encoding` is set.
- Conformance corpus 0.2.0: 370 of 370 vectors on net8.0 and net10.0, none skipped.
