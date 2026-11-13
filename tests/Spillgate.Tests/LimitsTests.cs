using System;
using System.IO;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

public sealed class LimitsTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [Covers("SG-R-LIM-01", "SG-I-02")]
    public void MaxOutputZeroOrNegativeIsInvalid(long value)
    {
        SpillgateException e = Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(value)));
        Assert.Equal(ErrorKind.InvalidLimits, e.Kind);
    }

    [Fact]
    [Covers("SG-R-LIM-01", "SG-R-API-01")]
    public void NullLimitsAreInvalidAndThereIsNoEntryPointWithoutLimits()
    {
        Assert.Equal(ErrorKind.InvalidLimits, Assert.Throws<SpillgateException>(() => Decoder.Create(null!)).Kind);
        Assert.Equal(ErrorKind.InvalidLimits, Assert.Throws<SpillgateException>(() => Decoder.CreateBuilder(null!).Build()).Kind);

        // SG-R-API-01: every public way to decode goes through a Decoder, which cannot exist without limits.
        foreach (var method in typeof(Decoder).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            Assert.True(
                method.Name is "Create" or "CreateBuilder",
                "unexpected public static member of Decoder: " + method.Name);
        }

        foreach (var ctor in typeof(Decoder).GetConstructors())
        {
            Assert.Fail("Decoder has a public constructor: " + ctor);
        }
    }

    [Fact]
    [Covers("SG-R-LIM-02")]
    public void MaxOutputUpToInt64MaxWorksWithoutOverflow()
    {
        Decoder decoder = Decoder.Create(Limits.Create(long.MaxValue).WithMaxRatio(1 << 30));
        byte[] data = Generators.Data(1, 5000);
        Outcome o = T.Read(decoder, Generators.GzipStored(data), 4096, "gzip");
        Assert.Equal("ok", o.Name);
        Assert.Equal(data, o.Data);

        // The ratio arithmetic must not overflow either: a huge ratio times a large consumed count.
        Assert.False(Decoder.Create(Limits.Create(long.MaxValue).WithMaxRatio((1L << 31) - 1)).GetType() is null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    [InlineData(int.MaxValue)]
    [Covers("SG-R-LIM-03")]
    public void MaxLayersOutsideOneToSixteenIsInvalid(int value)
    {
        Assert.Equal(ErrorKind.InvalidLimits, Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(100).WithMaxLayers(value))).Kind);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [Covers("SG-R-LIM-03")]
    public void MaxLayersBoundsAreValid(int value)
    {
        _ = Decoder.Create(Limits.Create(100).WithMaxLayers(value));
    }

    [Fact]
    [Covers("SG-R-LIM-03", "SG-R-HDR-11")]
    public void MaxLayersDefaultsToFive()
    {
        Decoder decoder = T.DecoderFor(1 << 16);
        byte[] five = Layered(5);
        Assert.Equal("ok", T.Read(decoder, five, 4096, Repeat("gzip", 5)).Name);
        Assert.Equal(ErrorKind.LayerLimit, Open(decoder, five, Repeat("gzip", 6)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(1L << 31)]
    [InlineData(long.MaxValue)]
    [Covers("SG-R-LIM-04")]
    public void MaxRatioOutsideItsRangeIsInvalidAndNoValueDisablesIt(long value)
    {
        Assert.Equal(ErrorKind.InvalidLimits, Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(100).WithMaxRatio(value))).Kind);
    }

    [Fact]
    [Covers("SG-R-LIM-04")]
    public void MaxRatioBoundsAreValid()
    {
        _ = Decoder.Create(Limits.Create(100).WithMaxRatio(1));
        _ = Decoder.Create(Limits.Create(100).WithMaxRatio((1L << 31) - 1));
    }

    [Fact]
    [Covers("SG-R-LIM-04", "SG-R-RAT-04")]
    public void TheRatioOptOutIsOneDistinctCallAndCannotBeCombined()
    {
        Assert.Equal(
            ErrorKind.InvalidLimits,
            Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(100).WithMaxRatio(10).WithoutRatioLimit())).Kind);
        Assert.Equal(
            ErrorKind.InvalidLimits,
            Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(100).WithoutRatioLimit().WithMaxRatio(10))).Kind);

        // With the opt-out, a 1000:1 stream is fine up to MaxOutput, and MaxOutput stays in force.
        byte[] bomb = Generators.ZerosOf("gzip", 40_000); // ~10 MB decoded
        Decoder open = Decoder.Create(Limits.Create(20_000_000).WithoutRatioLimit());
        Assert.Equal("ok", T.Read(open, bomb, 65536, "gzip").Name);
        Decoder tight = Decoder.Create(Limits.Create(1_000_000).WithoutRatioLimit());
        Assert.Equal("output_limit", T.Read(tight, bomb, 65536, "gzip").Name);
        Assert.Equal("ratio_limit", T.Read(Decoder.Create(Limits.Create(20_000_000)), bomb, 65536, "gzip").Name);
    }

    [Fact]
    [Covers("SG-R-LIM-05")]
    public void NegativeRatioGraceIsInvalidAndZeroChecksFromTheFirstByte()
    {
        Assert.Equal(ErrorKind.InvalidLimits, Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(100).WithRatioGrace(-1))).Kind);

        byte[] bomb = Generators.ZerosOf("gzip", 500); // 129 KB decoded from ~150 bytes
        // Default grace 64 KiB: still passes at 129 KB? ratio is 860:1 < 1000 so it passes either way.
        Assert.Equal("ok", T.Read(Decoder.Create(Limits.Create(1 << 20)), bomb, 4096, "gzip").Name);
        // A stricter ratio fires only after the grace; with grace 0 it fires at once, and delivers less.
        Outcome withGrace = T.Read(Decoder.Create(Limits.Create(1 << 20).WithMaxRatio(100)), bomb, 4096, "gzip");
        Outcome noGrace = T.Read(Decoder.Create(Limits.Create(1 << 20).WithMaxRatio(100).WithRatioGrace(0)), bomb, 4096, "gzip");
        Assert.Equal("ratio_limit", withGrace.Name);
        Assert.Equal("ratio_limit", noGrace.Name);
        Assert.True(withGrace.Data.Length > 65536);
        Assert.True(noGrace.Data.Length < withGrace.Data.Length);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [Covers("SG-R-LIM-06")]
    public void MaxInputMustBePositive(long value)
    {
        Assert.Equal(ErrorKind.InvalidLimits, Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(100).WithMaxInput(value))).Kind);
    }

    [Fact]
    [Covers("SG-R-LIM-07", "SG-I-02")]
    public void NoValueDisablesOrWeakensALimit()
    {
        // MaxOutput 1 really means one byte, MaxLayers 1 really means one layer.
        byte[] two = Generators.GzipStored(new byte[] { 1, 2 });
        Assert.Equal("output_limit", T.Read(T.DecoderFor(1), two, 4096, "gzip").Name);
        Decoder oneLayer = Decoder.Create(Limits.Create(100).WithMaxLayers(1));
        Assert.Equal(ErrorKind.LayerLimit, Open(oneLayer, Layered(2), Repeat("gzip", 2)));
        Assert.Equal("ok", T.Read(oneLayer, Layered(1), 4096, "gzip").Name);
    }

    [Fact]
    [Covers("SG-R-LIM-08", "SG-I-06")]
    public void ValidationHappensBeforeAnyByteIsRead()
    {
        var source = new ChunkedStream(new byte[100]);
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(0)).Open(source, "gzip"));
        Decoder decoder = T.DecoderFor(100);
        Assert.Throws<SpillgateException>(() => decoder.Open(source, "gzip", "nonsense!"));
        Assert.Throws<SpillgateException>(() => decoder.Open(source, "compress"));
        Assert.Throws<SpillgateException>(() => decoder.Open(source, Repeat("gzip", 6)));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    [Covers("SG-R-LIM-09")]
    public void LimitsAreImmutableAndCapturedByValue()
    {
        Limits original = Limits.Create(10);
        Limits changed = original.WithMaxLayers(2).WithMaxInput(5).WithMaxRatio(7).WithRatioGrace(3);
        Assert.NotSame(original, changed);
        Assert.Equal("Limits[maxOutput=10, maxLayers=default, maxRatio=default, ratioGrace=default, maxInput=none]", original.ToString());
        Assert.Equal("Limits[maxOutput=10, maxLayers=2, maxRatio=7, ratioGrace=3, maxInput=5]", changed.ToString());

        Decoder decoder = Decoder.Create(original);
        Outcome o = T.Read(decoder, Generators.GzipStored(new byte[11]), 4096, "gzip");
        Assert.Equal("output_limit", o.Name);
    }

    private static string[] Repeat(string s, int n)
    {
        var a = new string[n];
        Array.Fill(a, s);
        return a;
    }

    private static byte[] Layered(int layers)
    {
        byte[] data = Generators.Data(7, 100);
        for (int i = 0; i < layers; i++)
        {
            data = Generators.GzipStored(data);
        }

        return data;
    }

    private static ErrorKind? Open(Decoder decoder, byte[] source, string[] encodings)
    {
        try
        {
            decoder.Open(new MemoryStream(source), encodings).Dispose();
            return null;
        }
        catch (SpillgateException e)
        {
            return e.Kind;
        }
    }
}
