using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Spillgate;

/// <summary>Strict Content-Encoding parsing (SPEC section 4).</summary>
internal static class Header
{
    internal const int MaxLength = 1024;
    internal const int MaxElements = 16;
    internal const int MaxTokenLength = 32;
    internal const string Identity = "identity";
    internal const string Gzip = "gzip";
    internal const string XGzip = "x-gzip";

    internal static bool IsToken(string? name)
    {
        if (name is null || name.Length < 1 || name.Length > MaxTokenLength)
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool ok = i == 0 ? IsAlnum(c) : (IsAlnum(c) || c == '.' || c == '_' || c == '-');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool HasUpperCase(string name)
    {
        foreach (char c in name)
        {
            if (c is >= 'A' and <= 'Z')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Steps (1) to (3) of the evaluation order: length, element grammar, element count.</summary>
    internal static List<string> Parse(IEnumerable<string?>? values)
    {
        var joined = new List<byte[]>();
        if (values is not null)
        {
            long total = 0;
            foreach (string? value in values)
            {
                if (value is null)
                {
                    continue;
                }

                // Bound the work before encoding anything: UTF-8 never needs fewer bytes than chars.
                total += value.Length;
                if (total + joined.Count > MaxLength)
                {
                    throw TooLong();
                }

                byte[] bytes = Encoding.UTF8.GetBytes(value);
                joined.Add(bytes);
            }

            long size = Math.Max(joined.Count - 1, 0);
            foreach (byte[] b in joined)
            {
                size += b.Length;
            }

            if (size > MaxLength)
            {
                throw TooLong();
            }
        }

        List<string> elements = Split(joined);
        if (elements.Count > MaxElements)
        {
            throw new SpillgateException(
                ErrorKind.LayerLimit,
                elements.Count.ToString(CultureInfo.InvariantCulture) + " encodings listed, at most "
                    + MaxElements.ToString(CultureInfo.InvariantCulture) + " are accepted",
                0,
                MaxElements,
                null);
        }

        return elements;
    }

    /// <summary>Steps (4) and (5): alias resolution, support check, layer count. Returns the layers in decoding order.</summary>
    internal static List<string> Resolve(List<string> elements, Func<string, bool> supported, int maxLayers)
    {
        var layers = new List<string>(elements.Count);
        foreach (string element in elements)
        {
            string name = element == XGzip ? Gzip : element;
            if (name == Identity)
            {
                continue;
            }

            if (!supported(name))
            {
                throw new SpillgateException(
                    ErrorKind.UnsupportedEncoding,
                    "encoding " + SpillgateException.Quote(Encoding.UTF8.GetBytes(name)) + " is not supported");
            }

            layers.Add(name);
        }

        if (layers.Count > maxLayers)
        {
            throw new SpillgateException(
                ErrorKind.LayerLimit,
                layers.Count.ToString(CultureInfo.InvariantCulture) + " encoding layers, at most "
                    + maxLayers.ToString(CultureInfo.InvariantCulture) + " are accepted",
                0,
                maxLayers,
                null);
        }

        layers.Reverse();
        return layers;
    }

    private static SpillgateException TooLong() => new(
        ErrorKind.MalformedEncoding,
        "Content-Encoding is longer than " + MaxLength.ToString(CultureInfo.InvariantCulture) + " bytes",
        0,
        MaxLength,
        null);

    private static bool IsAlnum(int b) => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z');

    private static bool IsToken(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1 || bytes.Length > MaxTokenLength || !IsAlnum(bytes[0]))
        {
            return false;
        }

        for (int i = 1; i < bytes.Length; i++)
        {
            int b = bytes[i];
            if (!IsAlnum(b) && b != '.' && b != '_' && b != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> Split(List<byte[]> values)
    {
        var elements = new List<string>();
        foreach (byte[] value in values)
        {
            int start = 0;
            for (int i = 0; i <= value.Length; i++)
            {
                if (i < value.Length && value[i] != (byte)',')
                {
                    continue;
                }

                int from = start;
                int to = i;
                start = i + 1;
                while (from < to && (value[from] == (byte)' ' || value[from] == (byte)'\t'))
                {
                    from++;
                }

                while (to > from && (value[to - 1] == (byte)' ' || value[to - 1] == (byte)'\t'))
                {
                    to--;
                }

                if (from == to)
                {
                    continue; // RFC 9110 section 5.6.1.2: empty list elements are ignored
                }

                var element = new ReadOnlySpan<byte>(value, from, to - from);
                if (!IsToken(element))
                {
                    throw new SpillgateException(
                        ErrorKind.MalformedEncoding,
                        "invalid encoding name " + SpillgateException.Quote(element));
                }

                var folded = new char[element.Length];
                for (int k = 0; k < element.Length; k++)
                {
                    int b = element[k];
                    folded[k] = (char)(b is >= 'A' and <= 'Z' ? b + ('a' - 'A') : b);
                }

                elements.Add(new string(folded));
            }
        }

        return elements;
    }
}
