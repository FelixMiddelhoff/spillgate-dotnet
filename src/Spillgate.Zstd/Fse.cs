using System;
using System.Numerics;

namespace Spillgate.Zstd;

/// <summary>Finite State Entropy tables (RFC 8878 section 4.1).</summary>
internal static class Fse
{
    internal static Table Build(int[] normalized, int count, int accuracyLog)
    {
        if (accuracyLog < 0 || accuracyLog > 9)
        {
            throw Err.Corrupt("a table asks for too large an accuracy");
        }

        if (count > 256 || count > normalized.Length)
        {
            throw BadSum();
        }

        var t = new Table(accuracyLog);
        int size = t.Size;
        var next = new int[count];

        // Symbols with probability "less than one" take the highest states.
        int high = size;
        for (int s = 0; s < count; s++)
        {
            int p = normalized[s];
            if (p == -1)
            {
                high--;
                if (high < 0)
                {
                    throw BadSum();
                }

                t.Symbol[high] = (byte)s;
                next[s] = 1;
            }
            else
            {
                if (p < -1 || p > size)
                {
                    throw BadSum();
                }

                next[s] = Math.Max(p, 0);
            }
        }

        // The others are spread over 0..high with a fixed stride.
        int step = (size >> 1) + (size >> 3) + 3;
        int mask = size - 1;
        int position = 0;
        for (int s = 0; s < count; s++)
        {
            int p = Math.Max(normalized[s], 0);
            for (int i = 0; i < p; i++)
            {
                t.Symbol[position] = (byte)s;
                int guard = 0;
                for (; ; )
                {
                    position = (position + step) & mask;
                    if (position < high)
                    {
                        break;
                    }

                    guard++;
                    if (guard > size)
                    {
                        throw BadSum();
                    }
                }
            }
        }

        if (position != 0)
        {
            throw BadSum();
        }

        for (int state = 0; state < size; state++)
        {
            int symbol = t.Symbol[state];
            if (symbol >= count)
            {
                throw BadSum();
            }

            int nextState = next[symbol]++;
            if (nextState <= 0)
            {
                throw BadSum();
            }

            int top = 31 - BitOperations.LeadingZeroCount((uint)nextState);
            int bits = accuracyLog - top;
            if (bits < 0)
            {
                throw BadSum();
            }

            t.NbBits[state] = (byte)bits;
            int shifted = nextState << bits;
            if (shifted < size)
            {
                throw BadSum();
            }

            t.Baseline[state] = shifted - size;
        }

        return t;
    }

    internal static (Table Table, int Used) Read(byte[] a, int from, int to, int maxAccuracyLog, int maxSymbol)
    {
        var bits = new BitStreams.Forward(a, from, to);
        int accuracyLog = bits.Read(4) + 5;
        if (accuracyLog > maxAccuracyLog)
        {
            throw Err.Corrupt("a table asks for too large an accuracy");
        }

        int total = 1 << accuracyLog;
        var normalized = new int[maxSymbol + 1];
        int count = 0;
        int sum = 0;
        while (sum < total)
        {
            int maxValue = total - sum + 1; // the number of values still possible
            int bitCount = 32 - BitOperations.LeadingZeroCount((uint)maxValue);
            int raw = bits.Read(bitCount);
            int lowThreshold = (1 << bitCount) - 1 - maxValue;
            int mask = (1 << (bitCount - 1)) - 1;
            int small = raw & mask;
            int value;
            if (small < lowThreshold)
            {
                bits.Unread(1);
                value = small;
            }
            else if (raw > mask)
            {
                value = raw - lowThreshold;
            }
            else
            {
                value = raw;
            }

            int probability = value - 1;
            sum += probability < 0 ? 1 : probability;
            if (count >= normalized.Length)
            {
                throw Err.Corrupt("a table has too many symbols");
            }

            normalized[count++] = probability;
            if (probability == 0)
            {
                // A run of unused symbols.
                int repeat;
                do
                {
                    repeat = bits.Read(2);
                    if (count + repeat > normalized.Length)
                    {
                        throw Err.Corrupt("a table has too many symbols");
                    }

                    count += repeat; // the entries are already 0
                }
                while (repeat == 3);
            }

            if (bits.Overrun())
            {
                throw Err.Corrupt("a table description ends too early");
            }
        }

        if (sum != total)
        {
            throw BadSum();
        }

        return (Build(normalized, count, accuracyLog), bits.BytesUsed());
    }

    internal static Table Rle(int symbol)
    {
        var t = new Table(0);
        t.Symbol[0] = (byte)symbol;
        return t;
    }

    private static SpillgateException BadSum() => Err.Corrupt("the probabilities of a table do not add up");

    internal sealed class Table
    {
        internal Table(int accuracyLog)
        {
            AccuracyLog = accuracyLog;
            Size = 1 << accuracyLog;
            Symbol = new byte[Size];
            NbBits = new byte[Size];
            Baseline = new int[Size];
        }

        internal int AccuracyLog { get; }

        internal int Size { get; }

        internal byte[] Symbol { get; }

        internal byte[] NbBits { get; }

        internal int[] Baseline { get; }
    }
}
