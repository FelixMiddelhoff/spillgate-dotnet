# Contributing

Spillgate is a security library: a limit that can be bypassed is worse than none. The rules are strict.

## Ground rules

- Tests first. Every bug and every rule starts with a failing test or conformance vector.
- A test is never weakened to make code pass. Changing an expected outcome in the conformance manifest needs the maintainer's explicit decision (it is a corpus change in the reference repository).
- When in doubt choose the stricter behavior.
- Every rule of `spec/SPEC.md` is named by a vector or by a test with `[Covers("SG-R-...")]`; `SpecTraceTests` fails otherwise.
- No dependencies in the shipped packages, no `unsafe` code.
- Small functions, intention-revealing names, comments say why.

## Build and test

Needs the .NET SDK pinned in `global.json` (10.0.x) and the .NET 8 runtime for the net8.0 test runs.

```
dotnet build -c Release
dotnet test -c Release -f net8.0
dotnet test -c Release -f net10.0
dotnet format --verify-no-changes
dotnet pack -c Release
```

Warnings are errors. Public API changes need the approval files in `api/` updated: run the tests with
`SPILLGATE_UPDATE_API=1`, review the diff, commit it.

Randomized tests are seeded and time-boxed: `SPILLGATE_FUZZ_SECONDS` (default 4) and `SPILLGATE_FUZZ_SEED`.
A failure message names the seed and the iteration number, which reproduce it.

## Conformance corpus

`conformance/` and `spec/` are pinned copies from the reference repository, with their SHA-256 in
`conformance/CORPUS.sha256`. Update them only with `scripts/sync-conformance.sh <checkout>`. The zstd
golden streams in `tests/Spillgate.Tests/Resources/zstd` are made by `scripts/gen-zstd-goldens.py`
(needs the python package `zstandard`).

## Pull requests

Describe the rule or invariant the change protects, include the test that failed before, keep the
how-to guide in step (its code blocks are compiled by `tests/Spillgate.HowTo.Tests`).
