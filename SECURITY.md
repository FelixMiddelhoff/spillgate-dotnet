# Security policy

## Reporting a vulnerability

Use GitHub private vulnerability reporting: the repository's **Security** tab,
**Report a vulnerability**. Please do not open a public issue for a bypass.

A bypass is any input that makes the library deliver more than the configured limits, pass an
unsupported or malformed encoding through, throw anything but a `SpillgateException` (or
`OperationCanceledException` after a cancel), loop without bound, leak native memory of the brotli
decoder, or use memory that grows with the output instead of staying bounded.

What to expect:

- acknowledgment within 3 business days
- coordinated disclosure, up to 90 days from the report
- credit in the advisory if you want it
- no bug bounty

Every fixed bypass becomes a permanent conformance vector in the reference repository
([spillgate](https://github.com/FelixMiddelhoff/spillgate)), so all language implementations get it.

## Supported versions

The latest release only.

## Scope

The limits, header parsing, the own inflate, gzip and zlib framing, the reader contract, the own zstd
decoder (`Spillgate.Zstd`), the window and input handling around the runtime's brotli decoder
(`Spillgate.Brotli`) and the HTTP adapters are in scope. The brotli decoding core is part of the .NET
runtime (Google's code): flaws inside it are reported to Microsoft (MSRC) and Google as well, but report
them to us too if the limiter should have contained them. Bugs in the runtime or other libraries are
reported upstream.

The library has no runtime dependencies and uses no `unsafe` code, no reflection and no file or
network access of its own. A memory-safety problem in it is a bug in the runtime and equally a
reportable bypass.

Out of scope: the compressed size (unless `MaxInput` is set), timeouts and CPU of code after decoding,
archive formats (zip, tar, 7z), and encryption.

Run patched .NET runtimes: brotli CVE-2026-111497 (GHSA-p3jp-9v83-76w9) is fixed in 8.0.33, 9.0.22 and 10.0.14; ASP.NET Core CVE-2026-69304 has the floors 8.0.31, 9.0.20 and 10.0.12 (confirmed by secondary sources only). `docs/threat-model.md` has the details.
