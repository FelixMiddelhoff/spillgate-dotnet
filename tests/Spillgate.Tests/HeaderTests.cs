using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

public sealed class HeaderTests
{
    private static readonly Decoder Wide = T.DecoderFor(1 << 16, l => l.WithMaxLayers(16));

    private static ErrorKind? Check(Decoder decoder, params string?[] values)
    {
        try
        {
            decoder.Open(new MemoryStream(), values).Dispose();
            return null;
        }
        catch (SpillgateException e)
        {
            return e.Kind;
        }
    }

    public static IEnumerable<object?[]> Cases()
    {
        // header values, expected kind (null = accepted)
        yield return new object?[] { new[] { "gzip" }, null };
        yield return new object?[] { new[] { "GZIP" }, null };
        yield return new object?[] { new[] { "x-gzip" }, null };
        yield return new object?[] { new[] { "X-Gzip" }, null };
        yield return new object?[] { new[] { "gzip, br" }, null };
        yield return new object?[] { new[] { " gzip ,\tbr\t" }, null };
        yield return new object?[] { new[] { "gzip,,br" }, null };
        yield return new object?[] { new[] { ",gzip," }, null };
        yield return new object?[] { new[] { string.Empty }, null };
        yield return new object?[] { new[] { "   " }, null };
        yield return new object?[] { new[] { ",,," }, null };
        yield return new object?[] { Array.Empty<string>(), null };
        yield return new object?[] { new[] { "identity" }, null };
        yield return new object?[] { new[] { "identity, identity" }, null };
        yield return new object?[] { new[] { "compress" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "x-compress" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "dcb" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "dcz" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "aes128gcm" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "snappy" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "deflate64" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "gzip, snappy" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "gzip;q=1" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "\"gzip\"" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "gzip;" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "gz ip" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "gzip\u0000" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "gzip\r\n" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "gzipé" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "K" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { "-gzip" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { ".gzip" }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { new string('a', 33) }, ErrorKind.MalformedEncoding };
        yield return new object?[] { new[] { new string('a', 32) }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "a" }, ErrorKind.UnsupportedEncoding };
        yield return new object?[] { new[] { "a.b_c-d" }, ErrorKind.UnsupportedEncoding };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Covers("SG-R-HDR-01", "SG-R-HDR-03", "SG-R-HDR-04", "SG-R-HDR-05", "SG-R-HDR-07", "SG-R-HDR-08", "SG-R-HDR-10")]
    public void TheGrammarAndTheSupportCheck(string[] values, ErrorKind? expected)
    {
        Assert.True(expected == Check(Wide, values), string.Join("|", values));
    }

    [Fact]
    [Covers("SG-R-HDR-02", "SG-I-07")]
    public void ALongListIsMalformedAtExactlyTheCap()
    {
        string atCap = string.Concat(Enumerable.Repeat("gzip,", 204)) + "gzip"; // 204*5+4 = 1024
        Assert.Equal(1024, atCap.Length);
        // 205 elements: fails with layer_limit for the count, not for the length.
        Assert.Equal(ErrorKind.LayerLimit, Check(Wide, atCap));
        Assert.Equal(ErrorKind.MalformedEncoding, Check(Wide, atCap + ","));
        Assert.Null(Check(Wide, new string(',', 1024)));
        Assert.Equal(ErrorKind.MalformedEncoding, Check(Wide, new string(',', 1025)));
        Assert.Equal(ErrorKind.MalformedEncoding, Check(Wide, new string(' ', 1025)));
        // Several field values count with their separators: 512 + 1 + 512 = 1025.
        Assert.Equal(ErrorKind.MalformedEncoding, Check(Wide, new string(',', 512), new string(',', 512)));
        Assert.Null(Check(Wide, new string(',', 512), new string(',', 511)));
        // The length is judged first: a malformed token inside an overlong list is still a length failure.
        Assert.Equal(ErrorKind.MalformedEncoding, Check(Wide, "@" + new string(',', 1030)));
    }

    [Fact]
    [Covers("SG-R-HDR-03", "SG-R-HDR-14", "SG-I-07")]
    public void EmptyElementsAreIgnoredAndDoNotCount()
    {
        string many = string.Concat(Enumerable.Repeat(",", 500)) + "gzip" + string.Concat(Enumerable.Repeat(",", 500));
        Assert.Null(Check(T.DecoderFor(100, l => l.WithMaxLayers(1)), many));
    }

    [Fact]
    [Covers("SG-R-HDR-06", "SG-I-07")]
    public void MoreThanSixteenElementsIsALayerLimitWhateverMaxLayersIs()
    {
        string seventeen = string.Join(",", Enumerable.Repeat("identity", 17));
        string sixteen = string.Join(",", Enumerable.Repeat("identity", 16));
        Assert.Equal(ErrorKind.LayerLimit, Check(Wide, seventeen));
        Assert.Null(Check(Wide, sixteen));
        // identity counts toward the cap of 16 even though it is no layer.
        Assert.Equal(ErrorKind.LayerLimit, Check(Wide, string.Join(",", Enumerable.Repeat("identity", 16)) + ",gzip"));
        Assert.Equal(ErrorKind.LayerLimit, Check(T.DecoderFor(100, l => l.WithMaxLayers(16)), seventeen));
    }

    [Fact]
    [Covers("SG-R-HDR-08", "SG-R-CDC-09")]
    public void BrAndZstdAreSupportedOnlyWhenRegistered()
    {
        Decoder plain = T.DecoderFor(100, codecs: false);
        Assert.Equal(ErrorKind.UnsupportedEncoding, Check(plain, "br"));
        Assert.Equal(ErrorKind.UnsupportedEncoding, Check(plain, "zstd"));
        Assert.Null(Check(Wide, "br"));
        Assert.Null(Check(Wide, "zstd"));
    }

    [Fact]
    [Covers("SG-R-HDR-09", "SG-I-06", "SG-R-API-03")]
    public void AnUnsupportedElementMeansNothingIsDecodedAndNothingIsRead()
    {
        var source = new ChunkedStream(Generators.GzipStored(new byte[10]));
        Assert.Equal(ErrorKind.UnsupportedEncoding, Assert.Throws<SpillgateException>(() => Wide.Open(source, "gzip", "snappy")).Kind);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    [Covers("SG-R-HDR-10", "SG-R-HDR-11")]
    public void IdentityIsNoLayerForMaxLayers()
    {
        Decoder one = T.DecoderFor(100, l => l.WithMaxLayers(1));
        Assert.Null(Check(one, "identity, gzip, identity"));
        Assert.Equal(ErrorKind.LayerLimit, Check(one, "gzip, gzip"));
        Assert.Equal(ErrorKind.LayerLimit, Check(one, "identity, gzip, identity, gzip"));
    }

    [Fact]
    [Covers("SG-R-HDR-12")]
    public void TheLastListedTokenIsDecodedFirst()
    {
        byte[] data = Generators.Data(3, 500);
        byte[] gzipped = Generators.GzipStored(data); // layer 1 (read from the source) must be gzip
        byte[] zlibbed = Generators.ZlibStored(gzipped);
        // header order: "gzip, deflate" means deflate was applied last, so it is undone first.
        Assert.Equal(data, T.Read(Wide, zlibbed, 4096, "gzip", "deflate").Data);
        Assert.Equal("corrupt", T.Read(Wide, zlibbed, 4096, "deflate", "gzip").Name);
        Assert.Equal(data, T.Read(Wide, zlibbed, 4096, "gzip, deflate").Data);
    }

    [Fact]
    [Covers("SG-R-HDR-13")]
    public void ErrorTextQuotesAndBoundsAttackerData()
    {
        string hostile = "a\r\nINJECTED \"line\" \\ \u0000ÿ" + new string('z', 100);
        SpillgateException malformed = Assert.Throws<SpillgateException>(() => Wide.Open(new MemoryStream(), hostile));
        Assert.DoesNotContain('\r', malformed.Message);
        Assert.DoesNotContain('\n', malformed.Message);
        Assert.DoesNotContain('\0', malformed.Message);
        Assert.True(malformed.Message.Length < 200, malformed.Message);
        Assert.Contains("\\x0d\\x0a", malformed.Message, StringComparison.Ordinal);
        Assert.Contains("\\\"line\\\"", malformed.Message, StringComparison.Ordinal);

        SpillgateException unsupported = Assert.Throws<SpillgateException>(() => Wide.Open(new MemoryStream(), new string('q', 32)));
        Assert.True(unsupported.Message.Length < 150);
        Assert.Contains("\"qqqq", unsupported.Message, StringComparison.Ordinal);

        SpillgateException tooLong = Assert.Throws<SpillgateException>(() => Wide.Open(new MemoryStream(), new string('x', 5000)));
        Assert.True(tooLong.Message.Length < 150);
    }

    [Fact]
    [Covers("SG-R-HDR-14", "SG-R-HDR-05")]
    public void EquivalentPresentationsGiveTheSameResult()
    {
        byte[] data = Generators.Data(5, 300);
        byte[] stream = Generators.GzipStored(Generators.ZlibStored(data));
        string[][] forms =
        {
            new[] { "deflate, gzip" },
            new[] { "deflate", "gzip" },
            new[] { "DEFLATE,GZIP" },
            new[] { " deflate ,, gzip, " },
            new[] { string.Empty, "deflate", string.Empty, "x-gzip" },
            new[] { "identity", "deflate,identity", "gzip" },
        };
        foreach (string[] form in forms)
        {
            Outcome o = T.Read(Wide, stream, 100, form);
            Assert.True(o.Error is null && o.Data.SequenceEqual(data), string.Join("|", form) + ": " + o);
        }

        // And the same for failures.
        string[][] bad = { new[] { "gzip, snappy" }, new[] { "gzip", "snappy" }, new[] { " GZIP ,, Snappy " } };
        foreach (string[] form in bad)
        {
            Assert.Equal(ErrorKind.UnsupportedEncoding, Check(Wide, form));
        }
    }

    [Fact]
    [Covers("SG-R-HDR-01")]
    public void NullEntriesAndNullListAreTheEmptyChain()
    {
        Assert.Null(Check(Wide, null, "gzip", null));
        Wide.Open(new MemoryStream(), (IEnumerable<string?>?)null).Dispose();
    }

    [Fact]
    [Covers("SG-R-HDR-02", "SG-R-HDR-04", "SG-R-HDR-06", "SG-R-HDR-08", "SG-R-HDR-11")]
    public void TheEvaluationOrderDecidesWhichFaultWins()
    {
        // (2) grammar before (3) count: a malformed element left of 20 tokens wins over layer_limit.
        string bad = "@@," + string.Join(",", Enumerable.Repeat("gzip", 20));
        Assert.Equal(ErrorKind.MalformedEncoding, Check(Wide, bad));
        // (2) first fault wins, left to right.
        Assert.Equal(ErrorKind.MalformedEncoding, Check(Wide, "snappy, @@"));
        // (3) the element count before (4) the support check.
        string many = string.Join(",", Enumerable.Repeat("snappy", 17));
        Assert.Equal(ErrorKind.LayerLimit, Check(Wide, many));
        // (4) the support check before (5) the layer count.
        Assert.Equal(ErrorKind.UnsupportedEncoding, Check(T.DecoderFor(100, l => l.WithMaxLayers(1)), "gzip, gzip, snappy"));
    }
}
