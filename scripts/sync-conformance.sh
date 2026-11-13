#!/usr/bin/env bash
# Pin the conformance corpus and the spec from a checkout of the reference repository.
# Usage: scripts/sync-conformance.sh <path to spillgate checkout>
# Copies conformance/ and spec/ and rewrites conformance/CORPUS.sha256.
set -euo pipefail
src="${1:?path to a spillgate checkout}"
root="$(cd "$(dirname "$0")/.." && pwd)"
rm -rf "$root/conformance/vectors"
cp -r "$src/conformance/vectors" "$root/conformance/vectors"
cp "$src/conformance/manifest.json" "$src/conformance/SCHEMA.md" "$src/conformance/manifest.schema.json" "$root/conformance/"
cp "$src/spec/SPEC.md" "$src/spec/generators.md" "$root/spec/"
cd "$root"
{
  find conformance/vectors -type f
  echo conformance/manifest.json
  echo conformance/SCHEMA.md
  echo conformance/manifest.schema.json
  echo spec/SPEC.md
  echo spec/generators.md
} | LC_ALL=C sort | xargs sha256sum > conformance/CORPUS.sha256
echo "pinned $(wc -l < conformance/CORPUS.sha256) files"
