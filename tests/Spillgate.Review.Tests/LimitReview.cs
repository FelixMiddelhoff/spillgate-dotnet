using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Review.Tests;

public sealed class LimitReview
{
    public static IEnumerable<object[]> Matrix()
    {
        int idx = 0;
        foreach (string mode in R.Modes)
        {
            for (int f = 0; f < 6; f++)
            {
                for (int d = -1; d <= 1; d++)
                {
                    yield return new object[] { mode, f, d, idx++ };
                }
            }
        }
    }

    [Theory, MemberData(nameof(Matrix))]
    [Covers("SG-I-01", "SG-I-02", "SG-R-LAY-03")]
    public void NeverMoreThanMaxOutput_AtLimitMinusZeroPlusOne(string mode, int form, int delta, int _)
    {
        byte[] p = R.Text(5000);
        var (stream, enc) = R.Forms(p)[form];
        long limit = p.Length + delta;
        Decoder d = R.Dec(limit);
        R.Res r = R.Run(d, new R.Chunked(stream, () => 1 + (stream.Length % 13 == 0 ? 3 : 17)), new[] { enc }, mode);
        Assert.True(r.Data.Length <= limit, $"delivered {r.Data.Length} > limit {limit} ({mode},{enc})");
        Assert.True(p.AsSpan(0, r.Data.Length).SequenceEqual(r.Data));
        if (delta < 0)
        {
            var e = Assert.IsType<SpillgateException>(r.Err);
            Assert.Equal(ErrorKind.OutputLimit, e.Kind);
        }
        else
        {
            Assert.Null(r.Err);
            Assert.Equal(p.Length, r.Data.Length);
        }
    }

    [Fact]
    public void ExtremeLimitsDoNotOverflow()
    {
        byte[] p = R.Text(3000);
        foreach (var (stream, enc) in R.Forms(p))
        {
            foreach (long max in new[] { long.MaxValue, long.MaxValue - 1, int.MaxValue, (long)int.MaxValue + 1, 1L << 32 })
            {
                Decoder d = R.Dec(max, l => l.WithMaxInput(long.MaxValue).WithRatioGrace(long.MaxValue));
                byte[] got = d.DecodeAll(new MemoryStream(stream), enc);
                Assert.Equal(p, got);
                d = R.Dec(max, l => l.WithMaxRatio((1L << 31) - 1).WithRatioGrace(0));
                Assert.Equal(p, d.DecodeAll(new MemoryStream(stream), enc));
                d = R.Dec(max, l => l.WithoutRatioLimit().WithRatioGrace(long.MaxValue));
                Assert.Equal(p, d.DecodeAll(new MemoryStream(stream), enc));
            }
        }
    }

    [Fact]
    public void InvalidLimitsAreRejectedAtBuild()
    {
        foreach (long bad in new[] { 0L, -1, long.MinValue })
        {
            Assert.Equal(ErrorKind.InvalidLimits, Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(bad))).Kind);
        }

        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxLayers(0)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxLayers(17)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxRatio(0)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxRatio(1L << 31)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxRatio(-5)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithRatioGrace(-1)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxInput(0)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxInput(-1)));
        Assert.Throws<SpillgateException>(() => Decoder.Create(Limits.Create(1).WithMaxRatio(5).WithoutRatioLimit()));
        Assert.Throws<SpillgateException>(() => Decoder.Create(null!));
        Assert.DoesNotContain(typeof(Decoder).GetMethods(), m => m.IsStatic && m.Name == "Create" && m.GetParameters().Length == 0);
        Assert.Empty(typeof(Decoder).GetConstructors());
    }

    [Fact]
    public void BufferArgumentMisuse()
    {
        Decoder d = R.Dec(1000);
        using DecodedStream s = d.Open(new MemoryStream(R.Gzip(R.Text(100))), "gzip");
        var b = new byte[10];
        Assert.Throws<ArgumentNullException>(() => s.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(b, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Read(b, 0, -1));
        Assert.ThrowsAny<ArgumentException>(() => s.Read(b, 5, int.MaxValue));
        Assert.ThrowsAny<ArgumentException>(() => s.Read(b, int.MaxValue, int.MaxValue));
        Assert.ThrowsAny<ArgumentException>(() => s.ReadAsync(b, 5, int.MaxValue, default).GetAwaiter().GetResult());
        Assert.Throws<NotSupportedException>(() => s.Write(b, 0, 1));
        Assert.Throws<NotSupportedException>(() => _ = s.Length);
        Assert.Throws<ArgumentNullException>(() => d.Open(null!, "gzip"));
        Assert.ThrowsAny<ArgumentException>(() => d.Open(new WriteOnly(), "gzip"));
        Assert.Equal(ErrorKind.UnsupportedEncoding, Assert.Throws<SpillgateException>(() => d.Open(new MemoryStream(), "nope")).Kind);
        Assert.Equal(100, s.Read(new byte[1000], 0, 1000));
    }

    private sealed class WriteOnly : MemoryStream { public override bool CanRead => false; }

    [Fact]
    public void HostileCodecCannotBreakTheGuard()
    {
        foreach (string kind in new[] { "endless", "lieProduced", "lieConsumed", "negative", "nullref", "oom", "indexrange", "hugeThenDone", "neverProgress", "throwing-dispose" })
        {
            Decoder d = Decoder.CreateBuilder(Limits.Create(1000)).AddCodec("evil", new Evil(kind)).Build();
            var res = R.Run(d, new MemoryStream(new byte[100]), new[] { "evil" }, "readbig");
            Assert.True(res.Data.Length <= 1000, kind + " delivered " + res.Data.Length);
            Assert.NotNull(res.Err);
            Assert.IsType<SpillgateException>(res.Err);
            res = R.Run(d, new MemoryStream(new byte[100]), new[] { "evil" }, "read1");
            Assert.True(res.Data.Length <= 1000);
            Assert.IsType<SpillgateException>(res.Err);
        }
    }

    private sealed class Evil : ICodec
    {
        private readonly string _k;
        public Evil(string k) { _k = k; }
        public ICodecDecoder CreateDecoder() => new Dec(_k);
        private sealed class Dec : ICodecDecoder
        {
            private readonly string _k;
            public Dec(string k) { _k = k; }
            public void Dispose()
            {
                if (_k.Contains("throwing-dispose"))
                {
                    throw new InvalidOperationException("x");
                }
            }
            public CodecStatus Decode(ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
            {
                consumed = 0; written = 0;
                switch (_k)
                {
                    case "endless": output.Fill(7); written = output.Length; return CodecStatus.Continue;
                    case "lieProduced": written = output.Length + 5; return CodecStatus.Continue;
                    case "lieConsumed": consumed = input.Length + 5; return CodecStatus.Continue;
                    case "negative": written = -1; return CodecStatus.Continue;
                    case "nullref": throw new NullReferenceException();
                    case "oom": throw new OutOfMemoryException();
                    case "indexrange": throw new IndexOutOfRangeException();
                    case "hugeThenDone": output.Fill(1); written = output.Length; return CodecStatus.Done;
                    case "neverProgress": return CodecStatus.Continue;
                    default: output.Fill(1); written = output.Length; return CodecStatus.Continue;
                }
            }
        }
    }

    [Fact]
    public async Task ZeroLengthAndCountGreaterThanRemainingAsync()
    {
        byte[] p = R.Text(500);
        Decoder d = R.Dec(500);
        await using DecodedStream s = d.Open(new MemoryStream(R.Gzip(p)), "gzip");
        Assert.Equal(0, await s.ReadAsync(Memory<byte>.Empty));
        var big = new byte[1 << 20];
        int total = 0, n;
        while ((n = await s.ReadAsync(big.AsMemory(total, 100))) > 0)
        {
            total += n;
        }

        Assert.Equal(500, total);
        Assert.Equal(0, await s.ReadAsync(big, 0, big.Length));
        Assert.Equal(0, s.Read(Array.Empty<byte>(), 0, 0));
    }
}
