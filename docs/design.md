# Design

Port-specific decisions of the .NET implementation, and the traps of the platform with the tests that cover them. The normative text is `spec/SPEC.md`.

## Shape

- A `Decoder` is immutable and thread safe. `Open` validates the `Content-Encoding` values (nothing is read from the source before) and returns a forward-only `DecodedStream`. `DecodeAll` grows its buffer with the bytes produced.
- One layer per non-identity token. Each layer is an `InputMeter` (counts what the codec consumes, fetches at most 16 KiB at a time, guards the source) feeding a codec, behind a `LayerGuard` (output budget, ratio, sticky errors, containment of codec failures). The guard of layer *k* is the byte source of layer *k+1*.
- Codecs are push-style state machines: `ICodecDecoder.Decode(input, inputEnded, output, out consumed, out written)`. They never read from a stream, so the same codec code serves the synchronous and the asynchronous path; only the fetch from the source differs.
- The guard asks a codec for at most 64 KiB of output per step; this is also the tolerance of the ratio check.
- Targets: `net8.0` and `net10.0`. No dependency of any package except the runtime; `Spillgate.Http` takes ASP.NET Core as a framework reference only.

## Decisions

- **Own inflate.** `DeflateStream` cannot report how many input bytes it consumed, cannot detect trailing data and reads ahead, so ratio and `MaxInput` could not be exact. `Inflater` is a resumable RFC 1951 decoder that gives back look-ahead at the end of a step. The runtime's streams are the test oracle (`DeflateTests`, `FuzzTests`).
- **Brotli on the runtime's decoder.** `BrotliDecoder` reports exact consumption. Two traps made framing necessary: the window is checked from the first byte before any decoding (large-window streams are `MemoryLimit`), and the native decoder flushes output when it runs out of input and drops what it decoded in a call that fails, so bytes delivered before an error depended on how the source was chunked. The adapter therefore feeds it in steps of exactly 1 KiB of stream offset (plus the end of input and the `MaxInput` boundary), which makes the result independent of the source's chunking.
- **Own zstd decoder**, ported in structure from the Java port; frame header, window and dictionary checks come before any allocation, buffers follow arrived bytes. Not tied to the runtime's zstd (a built-in one is announced for a later .NET).
- **Cancellation** is `OperationCanceledException` (the idiomatic type), reported by the specification's outcome class `canceled`; `SpillgateHttpErrors` and `Outcome` helpers treat it as such. It is sticky once observed.
- **`leaveOpen`.** The source stays open on dispose (SG-R-RDR-06) unless `leaveOpen: false` is passed explicitly.
- **Zero is the end.** `Stream.Read` returns 0 only at the end of a stream, so the "no progress" rule for sources (SG-R-RDR-03) cannot occur; a codec that makes no progress for 100 steps is `Internal`.
- **Fault after output.** A codec that finds corruption in a step in which it also produced bytes returns the bytes and throws on the next call; otherwise the delivered prefix would depend on read sizes (found by the fuzz test).
- **Probe byte.** Before the byte after `MaxInput` is shown, the codec first delivers everything it can produce from the bytes it holds, so the prefix before `input_limit` does not depend on read sizes.

## .NET traps and their tests

| Trap | Handling | Tests |
|------|----------|-------|
| `Stream.CopyTo`, `ReadAllBytes`, `MemoryStream` growth unbounded | only the decoder's own streams are read; `DecodeAll` grows from produced bytes; reads are bounded by the limiter | `ReaderContractTests.DecodeAllGrowsFromWhat...`, `AsyncTests.DisposeAsyncAndCopyToAsyncWork` |
| `Read` returning 0 | 0 only for a zero-length request or the clean end | `ReaderContractTests`, conformance runner |
| Declared sizes (ISIZE, zstd content size, `Content-Length`) | never used as allowance | `GzipTests`, `ZstdTests` |
| Async cancellation semantics, deadlocks | token checked per read and per fetch; no context capture (`ConfigureAwait(false)`) | `CancellationTests`, `AsyncTests` |
| Transport decodes before the library | `SpillgateHandler` refuses `AutomaticDecompression` | `HttpTests` |
| Wrapped exceptions (`HttpRequestException`, `BadHttpRequestException`) | `SpillgateHttpErrors.Find`; the middleware also checks the stream's recorded failure when a framework swallows it | `HttpTests` |
| Dispose closes the source | it does not, unless `leaveOpen: false` | `ReaderContractTests` |
| Dispose from another thread | the read fails with `ObjectDisposedException`, never crashes | `ReaderContractTests` |
| Runtime differences | .NET 10 writes no bytes for an empty gzip/zlib/deflate stream; the decoder rejects empty input as corrupt, tests use fixed empty streams | `Helpers.cs`, `DeflateTests` |
| Native brotli | window cap, 1 KiB steps, patched runtime advice | `BrotliTests`, `docs/threat-model.md` |

## Verification layers

Conformance corpus 0.2.0 (370 vectors, sync and async, all read patterns, nothing skipped); unit tests per rule with `[Covers]` and a trace test over the spec; differential tests against the runtime for gzip, zlib, deflate and brotli and against libzstd-made golden streams; seeded randomized tests; resource tests with a child process under a heap limit; public API approval files in `api/`; compiled how-to snippets.
