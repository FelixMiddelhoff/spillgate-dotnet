using System;
using System.IO;
using System.Linq;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>MaxInput (probe byte semantics), the ratio rules and chains, from the outside and with a spying source.</summary>
public sealed class InputRatioChainTests
{
    private static readonly byte[] Plain = T.Text(2, 3000);

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("identity")]
    [Covers("SG-R-INP-01", "SG-R-INP-02", "SG-I-02")]
    public void AStreamThatFitsPassesEvenIfTheSourceHoldsMoreAndTheProbeIsNeverTaken(string codec)
    {
        byte[] stream = codec == "identity" ? Plain : T.Wrap(codec, Plain);
        string[] enc = codec == "identity" ? Array.Empty<string>() : new[] { codec };
        int m = stream.Length;

        // Exactly the stream, nothing behind it.
        var spy = new ChunkedStream(stream);
        Assert.Equal("ok", T.Read(T.DecoderFor(1 << 20, l => l.WithMaxInput(m)), spy, 4096, enc).Name);
        Assert.True(spy.Total <= m + 1);

        // One byte more in the source than allowed: codecs report trailing data under their own rules (corrupt);
        // identity takes every byte it passes, so it is input_limit after exactly m bytes.
        var spy2 = new ChunkedStream(Generators.Concat(stream, new byte[] { 0 }));
        Outcome o = T.Read(T.DecoderFor(1 << 20, l => l.WithMaxInput(m)), spy2, 4096, enc);
        Assert.Equal(codec == "identity" ? "input_limit" : "corrupt", o.Name);
        Assert.True(spy2.Total <= m + 1, "took " + spy2.Total + " of a cap of " + m + " + 1");
        if (codec == "identity")
        {
            Assert.Equal(Plain, o.Data);
        }

        // A source that goes on for ever: never more than MaxInput + 1 bytes are requested (SG-R-INP-02).
        var endless = new EndlessStream();
        Outcome e = T.Read(T.DecoderFor(1L << 40, l => l.WithMaxInput(m).WithoutRatioLimit()), endless, 4096, enc);
        Assert.True(e.Kind is ErrorKind.InputLimit or ErrorKind.Corrupt, e.ToString());
        Assert.True(endless.Total <= m + 1, "took " + endless.Total);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("zstd")]
    [Covers("SG-R-INP-01")]
    public void ACodecThatNeedsTheProbeByteTakesItAndTheResultIsInputLimit(string codec)
    {
        byte[] stream = T.Wrap(codec, Plain);
        for (int m = 1; m < stream.Length; m += Math.Max(1, stream.Length / 40))
        {
            Outcome o = T.Read(T.DecoderFor(1 << 20, l => l.WithMaxInput(m)), new ChunkedStream(stream, 997), 4096, codec);
            Assert.True(o.Name == "input_limit", codec + " m=" + m + "/" + stream.Length + ": " + o);
            // Only a source that really ends is a truncated stream.
            Outcome cut = T.Read(T.DecoderFor(1 << 20, l => l.WithMaxInput(m)), new ChunkedStream(T.Slice(stream, m), 997), 4096, codec);
            Assert.True(cut.Name == "corrupt", codec + " cut at m=" + m + ": " + cut);
        }
    }

    [Fact]
    [Covers("SG-R-INP-01")]
    public void TheInputCapAppliesToTheChainsSourceOnly()
    {
        byte[] inner = T.Gzip(T.Text(3, 50_000)); // layer 2 reads this much from layer 1
        byte[] outer = T.Gzip(inner);
        Decoder d = T.DecoderFor(1 << 20, l => l.WithMaxInput(outer.Length));
        Assert.Equal("ok", T.Read(d, outer, 4096, "gzip", "gzip").Name);
        Assert.True(inner.Length > 100);
        Decoder tight = T.DecoderFor(1 << 20, l => l.WithMaxInput(outer.Length - 1));
        Outcome o = T.Read(tight, outer, 4096, "gzip", "gzip");
        Assert.Equal("input_limit", o.Name);
        Assert.Equal(1, ((SpillgateException)o.Error!).Layer);
    }

    [Fact]
    [Covers("SG-R-RAT-01", "SG-R-RAT-02", "SG-R-RAT-05")]
    public void TheRatioIsJudgedOnConsumedBytesWithTheGraceAndNeverOverflows()
    {
        byte[] bomb = Generators.ZerosOf("gzip", 20_000); // 5.2 MB from ~5 KB
        Outcome o = T.Read(T.DecoderFor(100_000_000, l => l.WithMaxRatio(100)), bomb, 65536, "gzip");
        Assert.Equal("ratio_limit", o.Name);
        Assert.True(o.Data.Length > 65536 && o.Data.Length < 2_000_000, "delivered " + o.Data.Length);
        Assert.Equal(ErrorKind.RatioLimit, ((SpillgateException)o.Error!).Kind);
        Assert.Equal(100, ((SpillgateException)o.Error!).Limit);

        // A generous ratio lets the same bomb through; the grace covers small outputs entirely.
        Assert.Equal("ok", T.Read(T.DecoderFor(100_000_000, l => l.WithMaxRatio(2000)), bomb, 65536, "gzip").Name);
        Assert.Equal("ok", T.Read(T.DecoderFor(100_000_000, l => l.WithMaxRatio(1).WithRatioGrace(10_000_000)), bomb, 65536, "gzip").Name);

        // Overflow safety at the extremes: a huge ratio with a huge grace never wraps into a false positive.
        Assert.Equal("ok", T.Read(T.DecoderFor(long.MaxValue, l => l.WithMaxRatio((1L << 31) - 1).WithRatioGrace(long.MaxValue)), bomb, 65536, "gzip").Name);
    }

    [Fact]
    [Covers("SG-R-RAT-03", "SG-R-RAT-01")]
    public void TheRatioIsAlsoJudgedEndToEndOverTheWholeChain()
    {
        byte[] inner = Generators.ZerosOf("gzip", 100_000); // ~25 KB -> 25.8 MB, ratio ~1032
        byte[] outer = T.Gzip(inner); // ~100 bytes
        Decoder d = T.DecoderFor(100_000_000, l => l.WithMaxRatio(2000).WithRatioGrace(0));
        Outcome o = T.Read(d, outer, 65536, "gzip", "gzip");
        var e = Assert.IsType<SpillgateException>(o.Error);
        Assert.Equal(ErrorKind.RatioLimit, e.Kind);
        Assert.Equal(0, e.Layer); // the end-to-end check, not a single layer
        // The opt-out disables it for the whole chain.
        Assert.Equal("ok", T.Read(T.DecoderFor(100_000_000, l => l.WithoutRatioLimit()), outer, 65536, "gzip", "gzip").Name);
    }

    [Fact]
    [Covers("SG-R-RAT-04", "SG-R-LAY-01")]
    public void TheOptOutLeavesOutputAndLayerLimitsInForce()
    {
        byte[] bomb = Generators.ZerosOf("gzip", 100_000);
        Decoder d = T.DecoderFor(1_000_000, l => l.WithoutRatioLimit());
        Outcome o = T.Read(d, bomb, 65536, "gzip");
        Assert.Equal("output_limit", o.Name);
        Assert.Equal(1_000_000, o.Data.Length);
    }

    [Fact]
    [Covers("SG-R-HDR-11", "SG-R-LAY-01", "SG-R-HDR-12")]
    public void DeepChainsDecodeUpToTheLayerLimit()
    {
        byte[] data = T.Text(9, 2000);
        byte[] stream = data;
        for (int i = 0; i < 16; i++)
        {
            stream = T.Gzip(stream);
        }

        string[] sixteen = Enumerable.Repeat("gzip", 16).ToArray();
        Decoder wide = T.DecoderFor(1 << 20, l => l.WithMaxLayers(16).WithoutRatioLimit());
        Outcome o = T.Read(wide, stream, 100, sixteen);
        Assert.True(o.Error is null && o.Data.AsSpan().SequenceEqual(data), o.ToString());
        Assert.Equal(ErrorKind.LayerLimit, Assert.Throws<SpillgateException>(() => T.DecoderFor(1 << 20).Open(new MemoryStream(stream), sixteen)).Kind);

        // Mixed codecs, one byte at a time.
        byte[] mixed = Generators.ZstdStored(T.Brotli(T.ZLib(T.Gzip(data))), 17);
        Outcome m = T.Read(T.DecoderFor(1 << 20), new ChunkedStream(mixed, 1), 1, "gzip", "deflate", "br", "zstd");
        Assert.True(m.Error is null && m.Data.AsSpan().SequenceEqual(data), m.ToString());
    }

    [Fact]
    [Covers("SG-R-LAY-01", "SG-R-RDR-05")]
    public void AnErrorInALowerLayerNamesThatLayer()
    {
        byte[] inner = T.Slice(T.Gzip(T.Text(4, 5000)), 100); // truncated: layer 2 would see a broken inner stream
        byte[] outer = T.Gzip(inner);
        Outcome o = T.Read(T.DecoderFor(1 << 20), outer, 100, "gzip", "gzip");
        var e = Assert.IsType<SpillgateException>(o.Error);
        Assert.Equal((ErrorKind.Corrupt, 2), (e.Kind, e.Layer));

        byte[] badOuter = T.Gzip(T.Gzip(Plain));
        badOuter[badOuter.Length - 5] ^= 0xff;
        Outcome o2 = T.Read(T.DecoderFor(1 << 20), badOuter, 100, "gzip", "gzip");
        var e2 = Assert.IsType<SpillgateException>(o2.Error);
        Assert.Equal(ErrorKind.Corrupt, e2.Kind);
        Assert.Equal(1, e2.Layer);
    }
}
