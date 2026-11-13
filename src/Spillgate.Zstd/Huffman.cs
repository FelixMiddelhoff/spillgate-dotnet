using System;
using System.Numerics;

namespace Spillgate.Zstd;

/// <summary>Huffman literal trees (RFC 8878 section 4.2).</summary>
internal static class Huffman
{
    private const int MaxHuffmanBits = 11;

    internal static (Table Table, int Used) Read(byte[] a, int from, int to)
    {
        if (from < 0 || to > a.Length || to <= from)
        {
            throw Err.Corrupt("an empty Huffman tree description");
        }

        int header = a[from];
        int rest = from + 1;
        var weights = new byte[257];
        int count = 0;
        int used;
        if (header >= 128)
        {
            // Direct representation: header - 127 weights of 4 bits each, two per byte (high nibble first).
            int n = header - 127;
            int payload = (n + 1) / 2;
            used = 1 + payload;
            if (rest + payload > to)
            {
                throw Err.Corrupt("a Huffman tree description ends too early");
            }

            for (int i = 0; i < n; i++)
            {
                int b = a[rest + (i / 2)];
                weights[count++] = (byte)(i % 2 == 0 ? b >> 4 : b & 15);
            }
        }
        else
        {
            // FSE-compressed weights, header bytes.
            used = 1 + header;
            if (header == 0 || rest + header > to)
            {
                throw Err.Corrupt("a Huffman tree description ends too early");
            }

            count = DecodeWeights(a, rest, rest + header, weights);
        }

        if (count > 255)
        {
            throw Err.Corrupt("too many Huffman weights");
        }

        // The last weight is implied: the sum of 2^(w-1) must reach a power of two.
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            int w = weights[i];
            if (w > 0)
            {
                total += 1L << (w - 1);
            }
        }

        if (total == 0)
        {
            throw Err.Corrupt("a Huffman tree has no codes");
        }

        int maxBits = 64 - BitOperations.LeadingZeroCount((ulong)total); // smallest b with 2^b > total
        long power = 1L << maxBits;
        long remainder = power - total;
        if (BitOperations.PopCount((ulong)remainder) != 1)
        {
            throw Err.Corrupt("Huffman weights do not complete a tree");
        }

        weights[count++] = (byte)(BitOperations.TrailingZeroCount((ulong)remainder) + 1); // log2(rest) + 1

        // libzstd refuses a tree with fewer than two weight-1 symbols (the implied one included) or an odd
        // number of them; accepting it would be a parser differential.
        int ones = 0;
        for (int i = 0; i < count; i++)
        {
            if (weights[i] == 1)
            {
                ones++;
            }
        }

        if (ones < 2 || ones % 2 == 1)
        {
            throw Err.Corrupt("Huffman weights do not form a valid tree");
        }

        if (maxBits > MaxHuffmanBits)
        {
            throw Err.Corrupt("a Huffman code is too long");
        }

        return (Build(weights, count, maxBits), used);
    }

    internal static void DecodeStream(Table table, byte[] a, int from, int to, byte[] output, int outOff, int count)
    {
        var bits = new BitStreams.Backward(a, from, to);
        int maxBits = table.MaxBits;
        int[] entries = table.Entries;
        for (int i = 0; i < count; i++)
        {
            int index = (int)bits.Peek(maxBits);
            int entry = entries[index];
            bits.Remaining -= entry & 15;
            output[outOff + i] = (byte)(entry >> 4);
        }

        if (bits.Remaining != 0)
        {
            throw Err.Corrupt("a Huffman stream was not consumed exactly");
        }
    }

    private static int DecodeWeights(byte[] a, int from, int to, byte[] weights)
    {
        (Fse.Table table, int usedBytes) = Fse.Read(a, from, to, 6, 15);
        int start = from + usedBytes;
        if (start > to)
        {
            throw Err.Corrupt("a Huffman tree description ends too early");
        }

        var bits = new BitStreams.Backward(a, start, to);
        long state1 = bits.Read(table.AccuracyLog);
        long state2 = bits.Read(table.AccuracyLog);
        int n = 0;
        for (; ; )
        {
            if (state1 >= table.Size)
            {
                throw Err.Corrupt("a table state is out of range");
            }

            int s1 = (int)state1;
            n = Push(weights, n, table.Symbol[s1]);
            state1 = table.Baseline[s1] + bits.Read(table.NbBits[s1]);
            if (bits.Remaining < 0)
            {
                if (state2 >= table.Size)
                {
                    throw Err.Corrupt("a table state is out of range");
                }

                return Push(weights, n, table.Symbol[(int)state2]);
            }

            if (state2 >= table.Size)
            {
                throw Err.Corrupt("a table state is out of range");
            }

            int s2 = (int)state2;
            n = Push(weights, n, table.Symbol[s2]);
            state2 = table.Baseline[s2] + bits.Read(table.NbBits[s2]);
            if (bits.Remaining < 0)
            {
                if (state1 >= table.Size)
                {
                    throw Err.Corrupt("a table state is out of range");
                }

                return Push(weights, n, table.Symbol[(int)state1]);
            }

            if (n > 255)
            {
                throw Err.Corrupt("too many Huffman weights");
            }
        }
    }

    private static int Push(byte[] weights, int n, byte weight)
    {
        if (n >= 256)
        {
            throw Err.Corrupt("too many Huffman weights");
        }

        weights[n] = weight;
        return n + 1;
    }

    private static Table Build(byte[] weights, int count, int maxBits)
    {
        int size = 1 << maxBits;
        var entries = new int[size];

        // Canonical codes, shortest codes take the highest bit patterns (symbols sorted by weight).
        int next = 0;
        for (int w = 1; w <= maxBits; w++)
        {
            for (int symbol = 0; symbol < count; symbol++)
            {
                if (weights[symbol] != w)
                {
                    continue;
                }

                int length = maxBits + 1 - w;
                int span = 1 << (maxBits - length);
                int value = (symbol << 4) | length;
                int end = next + span;
                if (end > size)
                {
                    throw Err.Corrupt("a Huffman tree is not complete");
                }

                Array.Fill(entries, value, next, span);
                next = end;
            }
        }

        if (next != size)
        {
            throw Err.Corrupt("a Huffman tree is not complete");
        }

        return new Table(maxBits, entries);
    }

    internal sealed class Table
    {
        internal Table(int maxBits, int[] entries)
        {
            MaxBits = maxBits;
            Entries = entries;
        }

        internal int MaxBits { get; }

        internal int[] Entries { get; }
    }
}
