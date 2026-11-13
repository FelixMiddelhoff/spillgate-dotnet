# Threat model

## What is defended

An attacker controls the compressed bytes and the `Content-Encoding` value of a body that the application decodes. The goal of the library is that the worst result is a typed error with bounded CPU and memory: no output beyond `MaxOutput` per layer, no unbounded chain, no allocation sized by a value from the stream, no silent truncation, no unknown encoding passed through.

Out of scope: the size of the compressed input unless `MaxInput` or a server limit is set, timeouts of slow sources, archive formats, and the time spent in code after decoding.

## Controls and where they are tested

| Threat | Control | Tests |
|--------|---------|-------|
| Decompression bomb | mandatory `MaxOutput` per layer, ratio limit judged on consumed input, end-to-end ratio | conformance BOMB, RAT, BND vectors; `ReaderContractTests`, `InputRatioChainTests`, `ResourceTests` |
| Limit switched off by an input value | no sentinel values; declared sizes never used (gzip ISIZE, zstd content size) | `LimitsTests`, `GzipTests`, `ZstdTests` |
| Chain of encodings | at most 16 elements, `MaxLayers` (default 5), own budget per layer | `HeaderTests`, `InputRatioChainTests` |
| Header tricks | strict token grammar, 1024 byte cap, quoted and bounded error text | `HeaderTests`, `FuzzTests` |
| Window abuse (brotli, zstd) | window read from the first bytes and checked before decoding; zstd buffers grow with arrived bytes | `BrotliTests`, `ZstdTests`, conformance WIN vectors |
| Trailing data, truncation | clean end is judged by the codec; trailing bytes and cut streams are corrupt | `DeflateTests`, `GzipTests`, `BrotliTests`, `ZstdTests` |
| Misbehaving source or codec | counts checked, no-progress guard, exceptions contained as `Internal` | `ReaderContractTests`, `CodecContractTests` |
| Read-size and chunking dependence | bytes before an error do not depend on read sizes or source chunking | `LateSourceErrorTests`, `FuzzTests` |
| Transport decodes first | `SpillgateHandler` refuses a transport with `AutomaticDecompression` | `HttpTests` |

## Memory per layer (measured)

Measured with `tests/Spillgate.Canary` on Windows, net8.0, a hard limit of 96 MiB on the managed heap, decoding a virtual bomb of 1 GiB (the chain: 256 MiB). Peak working set includes the runtime itself (about 25 MB for an idle process).

| Codec | Time for 1 GiB | Peak working set | Managed allocation |
|-------|----------------|------------------|--------------------|
| identity | 0.05 s | 25 MB | 0.1 MB |
| gzip | 0.9 s | 31 MB | 0.25 MB |
| zlib | 0.6 s | 31 MB | 0.25 MB |
| raw deflate | 0.2 s | 31 MB | 0.25 MB |
| zstd (window 8 MiB) | 0.1 s | 43 MB | 17 MB (window and block buffers) |
| brotli (window 16 MiB) | 0.7 s | 41 MB | 0.1 MB (the window is native memory) |
| gzip over zstd over brotli (256 MiB) | 0.2 s | 28 MB | 0.2 MB |

Bounds: the inflate takes a 32 KiB window, two 64 KiB code tables and a 16 KiB input buffer per layer. zstd takes the window cap (default 8 MiB) plus at most three 128 KiB block buffers; the window is allocated as bytes are produced, never from the declared size. brotli takes up to 16 MiB of native window plus the decoder's tables (a few MiB per meta-block). The whole-body functions grow their buffer with what was produced.

## Residual risks

1. **Brotli depends on the runtime.** `Spillgate.Brotli` uses the brotli decoder that ships in the .NET runtime (native code, Google's library). Spillgate limits and frames it, but a memory-safety flaw inside the decoder is the runtime's. CVE-2026-111497 (brotli out-of-bounds write; reported fixed in the runtime servicing releases of 25 October 2026) is fixed in .NET 8.0.33, 9.0.22 and 10.0.14 (verified against the GitHub advisory GHSA-p3jp-9v83-76w9 on 2026-11-13). Run runtimes at or above these releases, or do not register brotli. The development machine that produced the measurements and the test runs used 8.0.26 and 10.0.12, which are older than these releases: the test suite does not prove anything about the patched decoder, and it contains canaries (`BrotliTests.NonZeroPaddingAfterTheLastMetaBlockIsCorrupt`, `AMetaBlockThatProducesMoreThanItsLengthIsCorrupt`) for two parser strictness fixes that other ports needed; both pass on 8.0.26 and 10.0.12.
2. **Brotli CPU.** A stream of many small meta-blocks with many Huffman trees costs CPU per input byte that the output and ratio limits cannot see (the Java port measured it; the Rust and JS ports reduced it by changing the decoder). The runtime's decoder is native and several times faster than those, but the cost is linear in the input: set `MaxInput` and a timeout where brotli input is untrusted. Measured with the independent review (hostile meta-blocks, 4000 meta-blocks with 256 trees each): 25 to 60 ns per input byte (up to 2.2 s for a 38 MB stream), so set `MaxInput` where brotli input is untrusted.
3. **ASP.NET Core CVE-2026-69304** (denial of service through highly compressed data in ASP.NET Core): with the floors 8.0.31, 9.0.20 and 10.0.12. These numbers are confirmed by secondary sources only (NCSC-NL CSAF, OSV, a vendor advisory database); the Microsoft advisory page itself was not read. It concerns the framework's own request decompression, which Spillgate replaces when you use `UseSpillgateRequestDecoding`; run patched runtimes anyway.
4. **Brotli latency.** Input reaches the brotli decoder in steps of 1 KiB, and the decoder writes into an internal 1 KiB buffer, so that the bytes delivered before an error and the outcome do not depend on source chunking or read size (the runtime decoder loses bytes in a failing call otherwise); the tail of a stream that is shorter than a step waits for the end of the input.
5. **Ratio timing.** The ratio is judged once per codec step (at most 64 KiB of output), so the length of a `RatioLimit` prefix may depend on read sizes, as the specification allows.
6. **Native memory is not counted by `MaxOutput`.** It is bounded by the brotli window cap (default 16 MiB per stream, configurable down to 1 KiB).
7. **Sources that ignore cancellation.** A source whose `ReadAsync` does not honor its token cannot be interrupted while it blocks.
