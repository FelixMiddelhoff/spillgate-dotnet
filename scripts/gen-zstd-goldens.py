#!/usr/bin/env python3
"""Regenerates tests/Spillgate.Tests/Resources/zstd/*.zst and manifest.json.

Needs the python package `zstandard` (pip install zstandard). The streams are test oracles produced by libzstd;
the manifest holds length and SHA-256 of the plain data, which is all a test needs. Only run this
when a golden stream has to change; the resulting files are committed.
"""
import hashlib
import json
import os
import random
import struct
import sys

import zstandard

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "Spillgate.Tests", "Resources", "zstd")
WORDS = ["spill", "gate", "stream", "window", "block", "frame", "literal", "match", "offset", "huffman", "entropy",
         "table", "state", "header", "decoder", "limit", "bomb", "ratio", "chain", "layer", "ok", "x", "the", "of"]


def plain(kind, seed, size):
    """Deterministic test data of several shapes (text-like, records, random, zeros, mixed)."""
    rnd = random.Random(seed)
    if kind == "text":
        out = bytearray()
        while len(out) < size:
            out += (WORDS[min(int(rnd.expovariate(0.25)), len(WORDS) - 1)] + (" " if rnd.random() < 0.9 else "\n")).encode()
        return bytes(out[:size])
    if kind == "random":
        return bytes(rnd.getrandbits(8) for _ in range(size))
    if kind == "zeros":
        return bytes(size)
    if kind == "records":
        out = bytearray()
        i = 0
        while len(out) < size:
            out += struct.pack("<IHH", i, rnd.randrange(50), rnd.randrange(3)) + b"record-" + str(i % 97).encode() + b"\n"
            i += 1
        return bytes(out[:size])
    if kind == "mixed":
        a = plain("text", seed, size // 3)
        b = plain("random", seed + 1, size // 3)
        c = plain("records", seed + 2, size - len(a) - len(b))
        return a + b + c
    raise ValueError(kind)


CASES = [
    # name, kind, seed, size, level, checksum, content_size
    ("empty", "text", 1, 0, 3, True, True),
    ("tiny", "text", 2, 1, 3, False, True),
    ("small-text-l1", "text", 3, 3000, 1, False, True),
    ("small-text-l3-ck", "text", 4, 5000, 3, True, True),
    ("text-l1", "text", 5, 400000, 1, True, True),
    ("text-l3", "text", 6, 400000, 3, False, False),
    ("text-l9-ck", "text", 7, 300000, 9, True, True),
    ("text-l19", "text", 8, 300000, 19, True, True),
    ("records-l3", "records", 9, 300000, 3, True, True),
    ("records-l15", "records", 10, 300000, 15, False, True),
    ("random-l3", "random", 11, 200000, 3, True, True),
    ("zeros-l3", "zeros", 12, 600000, 3, True, True),
    ("mixed-l5", "mixed", 13, 450000, 5, True, True),
    ("mixed-l22-ck", "mixed", 14, 450000, 22, True, True),
    ("text-l1-stream", "text", 15, 350000, 1, True, False),
]


def main():
    os.makedirs(OUT, exist_ok=True)
    manifest = []
    for name, kind, seed, size, level, checksum, content_size in CASES:
        data = plain(kind, seed, size)
        c = zstandard.ZstdCompressor(level=level, write_checksum=checksum, write_content_size=content_size)
        blob = c.compress(data)
        with open(os.path.join(OUT, name + ".zst"), "wb") as f:
            f.write(blob)
        manifest.append({"name": name, "kind": kind, "seed": seed, "size": size, "sha256": hashlib.sha256(data).hexdigest(),
                         "compressed": len(blob), "checksum": checksum})
        print(name, len(data), "->", len(blob))
    with open(os.path.join(OUT, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    sys.exit(main())
