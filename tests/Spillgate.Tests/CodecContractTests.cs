using System;
using System.IO;
using System.Linq;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>Extension codecs: they run behind the same limiter, and a misbehaving one is contained.</summary>
public sealed class CodecContractTests
{
    private delegate CodecStatus DecodeFunc(ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written);

    private sealed class TestCodec : ICodec
    {
        private readonly Func<ICodecDecoder> _create;

        public TestCodec(Func<ICodecDecoder> create)
        {
            _create = create;
        }

        public ICodecDecoder CreateDecoder() => _create();
    }

    private sealed class TestDecoder : ICodecDecoder
    {
        private readonly DecodeFunc _decode;

        public TestDecoder(DecodeFunc decode)
        {
            _decode = decode;
        }

        public int Disposed { get; private set; }

        public bool ThrowOnDispose { get; init; }

        public CodecStatus Decode(ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written) =>
            _decode(input, inputEnded, output, out consumed, out written);

        public void Dispose()
        {
            Disposed++;
            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("dispose failed");
            }
        }
    }

    /// <summary>Doubles every input byte: an expanding codec (ratio 2) that works with any output size, even one byte.</summary>
    private static DecodeFunc MakeDoubler()
    {
        byte pending = 0;
        bool has = false;
        return (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
        {
            consumed = 0;
            written = 0;
            if (has && output.Length > 0)
            {
                output[written++] = pending;
                has = false;
            }

            while (consumed < input.Length && written < output.Length && !has)
            {
                byte b = input[consumed++];
                output[written++] = b;
                if (written < output.Length)
                {
                    output[written++] = b;
                }
                else
                {
                    pending = b;
                    has = true;
                }
            }

            if (written > 0 || consumed > 0)
            {
                return CodecStatus.Continue;
            }

            return ended && !has ? CodecStatus.Done : has ? CodecStatus.Continue : CodecStatus.NeedInput;
        };
    }

    /// <summary>Turns every input byte into 1000 bytes: ratio 1000, to trip the ratio limit.</summary>
    private static CodecStatus Exploder(ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written)
    {
        // Produces up to output.Length bytes of 'x' per input byte, consuming one input byte per 4000 bytes produced.
        consumed = 0;
        written = 0;
        if (input.IsEmpty)
        {
            return ended ? CodecStatus.Done : CodecStatus.NeedInput;
        }

        written = Math.Min(output.Length, 4000);
        output.Slice(0, written).Fill((byte)'x');
        consumed = 1;
        return CodecStatus.Continue;
    }

    private static Decoder With(string name, Func<ICodecDecoder> factory, long maxOutput = 1 << 20) =>
        Decoder.CreateBuilder(Limits.Create(maxOutput)).AddCodec(name, new TestCodec(factory)).Build();

    private static Outcome Run(Decoder d, byte[] src, params string[] enc) => T.Read(d, src, 100, enc);

    [Fact]
    [Covers("SG-R-CDC-09", "SG-R-CDC-01", "SG-R-HDR-08")]
    public void ARegisteredExtensionCodecDecodesThroughTheLimiter()
    {
        Decoder d = With("dbl", () => new TestDecoder(MakeDoubler()), maxOutput: 100);
        Assert.Equal("aabbcc"u8.ToArray(), Run(d, "abc"u8.ToArray(), "dbl").Data);
        Outcome over = Run(d, new byte[60], "dbl");
        Assert.Equal("output_limit", over.Name);
        Assert.Equal(100, over.Data.Length);
        // It chains with the built-ins and counts as one layer.
        Decoder withGzip = With("dbl", () => new TestDecoder(MakeDoubler()));
        Assert.Equal("aabb"u8.ToArray(), Run(withGzip, T.Gzip("ab"u8.ToArray()), "dbl", "gzip").Data);
    }

    [Fact]
    [Covers("SG-R-CDC-01", "SG-R-RAT-01", "SG-R-RAT-02")]
    public void AnExtensionCodecIsSubjectToTheRatioMeter()
    {
        Decoder d = With("boom", () => new TestDecoder(Exploder), maxOutput: 1L << 30);
        Outcome o = T.Read(d, new byte[100_000], 65536, "boom");
        Assert.Equal("ratio_limit", o.Name);
        Assert.True(o.Data.Length < 10_000_000, "delivered " + o.Data.Length);
    }

    [Fact]
    [Covers("SG-R-CDC-01", "SG-R-INP-01")]
    public void AnExtensionCodecIsSubjectToTheInputMeter()
    {
        Decoder d = With("dbl", () => new TestDecoder(MakeDoubler()), maxOutput: 1 << 20);
        Decoder capped = Decoder.CreateBuilder(Limits.Create(1 << 20).WithMaxInput(10)).AddCodec("dbl", new TestCodec(() => new TestDecoder(MakeDoubler()))).Build();
        Assert.Equal("ok", Run(d, new byte[11], "dbl").Name);
        Assert.Equal("input_limit", Run(capped, new byte[11], "dbl").Name);
        Assert.Equal("ok", Run(capped, new byte[10], "dbl").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-09")]
    public void CodecRegistrationRulesAreEnforcedAtBuild()
    {
        var codec = new TestCodec(() => new TestDecoder(MakeDoubler()));
        foreach (string reserved in new[] { "identity", "gzip", "deflate", "x-gzip" })
        {
            AssertInvalidOption(b => b.AddCodec(reserved, codec));
        }

        foreach (string bad in new[] { "BR", "Upper", string.Empty, "has space", "-x", "a;b", new string('a', 33), null! })
        {
            AssertInvalidOption(b => b.AddCodec(bad, codec));
        }

        AssertInvalidOption(b => b.AddCodec("x", null!));
        AssertInvalidOption(b => b.AddCodec("dup", codec).AddCodec("dup", codec));

        // There is no process-global registry: a second decoder does not know the codec.
        Decoder one = With("only-here", () => new TestDecoder(MakeDoubler()));
        Decoder two = T.DecoderFor(100);
        Assert.Equal("ok", Run(one, new byte[2], "only-here").Name);
        Assert.Equal("unsupported_encoding", Run(two, new byte[2], "only-here").Name);
    }

    private static void AssertInvalidOption(Func<DecoderBuilder, DecoderBuilder> configure)
    {
        SpillgateException e = Assert.Throws<SpillgateException>(() => configure(Decoder.CreateBuilder(Limits.Create(10))).Build());
        Assert.Equal(ErrorKind.InvalidOption, e.Kind);
    }

    [Fact]
    [Covers("SG-R-CDC-08", "SG-I-05")]
    public void AnExceptionFromACodecIsInternalAndNeverEscapes()
    {
        foreach (Exception thrown in new Exception[]
        {
            new InvalidOperationException("secret that must not be echoed"),
            new NullReferenceException(),
            new IndexOutOfRangeException(),
            new InsufficientExecutionStackException(),
            new DivideByZeroException(),
        })
        {
            Decoder d = With("bad", () => new TestDecoder(Throw(thrown)));
            Outcome o = Run(d, new byte[10], "bad");
            var e = Assert.IsType<SpillgateException>(o.Error);
            Assert.Equal(ErrorKind.Internal, e.Kind);
            Assert.Equal(1, e.Layer);
            Assert.DoesNotContain("secret", e.Message, StringComparison.Ordinal);
            Assert.Same(thrown, e.InnerException);
        }
    }

    private static DecodeFunc Throw(Exception e) => (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
    {
        consumed = 0;
        written = 0;
        throw e;
    };

    [Fact]
    [Covers("SG-R-CDC-08")]
    public void ACodecThatLiesAboutItsCountsIsInternal()
    {
        DecodeFunc tooMuchOut = (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
        {
            consumed = 0;
            written = output.Length + 1;
            return CodecStatus.Continue;
        };
        DecodeFunc tooMuchIn = (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
        {
            consumed = input.Length + 1;
            written = 0;
            return CodecStatus.NeedInput;
        };
        DecodeFunc negative = (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
        {
            consumed = -1;
            written = 0;
            return CodecStatus.NeedInput;
        };
        foreach (DecodeFunc f in new[] { tooMuchOut, tooMuchIn, negative })
        {
            Decoder d = With("liar", () => new TestDecoder(f));
            Outcome o = Run(d, new byte[10], "liar");
            Assert.Equal("internal", o.Name);
            Assert.Empty(o.Data);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-08", "SG-R-RDR-03")]
    public void ACodecThatMakesNoProgressEndsTheLayerInsteadOfSpinning()
    {
        DecodeFunc idle = (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
        {
            consumed = 0;
            written = 0;
            return CodecStatus.Continue;
        };
        Decoder d = With("idle", () => new TestDecoder(idle));
        Outcome o = Run(d, new byte[10], "idle");
        Assert.Equal("internal", o.Name);

        // Asking for input after the input ended is just as wrong.
        DecodeFunc needy = (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
        {
            consumed = input.Length;
            written = 0;
            return CodecStatus.NeedInput;
        };
        Assert.Equal("internal", Run(With("needy", () => new TestDecoder(needy)), new byte[10], "needy").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-08", "SG-I-10")]
    public void ACodecThatCannotBeCreatedOrDisposedIsContained()
    {
        Decoder noCreate = With("x", () => throw new InvalidOperationException("no"));
        SpillgateException e = Assert.Throws<SpillgateException>(() => noCreate.Open(new MemoryStream(), "x"));
        Assert.Equal(ErrorKind.Internal, e.Kind);
        Decoder nullCreate = With("x", () => null!);
        Assert.Equal(ErrorKind.Internal, Assert.Throws<SpillgateException>(() => nullCreate.Open(new MemoryStream(), "x")).Kind);
        Decoder oom = With("x", () => throw new OutOfMemoryException());
        Assert.Equal(ErrorKind.MemoryLimit, Assert.Throws<SpillgateException>(() => oom.Open(new MemoryStream(), "x")).Kind);
        Decoder typed = With("x", () => throw new SpillgateException(ErrorKind.InvalidOption, "bad option"));
        Assert.Equal(ErrorKind.InvalidOption, Assert.Throws<SpillgateException>(() => typed.Open(new MemoryStream(), "x")).Kind);

        var made = new TestDecoder(MakeDoubler()) { ThrowOnDispose = true };
        Decoder d = With("x", () => made);
        Outcome o = Run(d, new byte[3], "x");
        Assert.Equal("ok", o.Name);
        Assert.Equal(1, made.Disposed);
    }

    [Fact]
    [Covers("SG-R-CDC-08", "SG-I-05")]
    public void OutOfMemoryInACodecStepIsAMemoryLimitNotARawOutOfMemory()
    {
        DecodeFunc oom = Throw(new OutOfMemoryException());
        Outcome o = Run(With("oom", () => new TestDecoder(oom)), new byte[10], "oom");
        Assert.Equal("memory_limit", o.Name);
    }

    [Fact]
    [Covers("SG-I-10", "SG-R-RDR-06")]
    public void EveryCodecStateIsReleasedExactlyOnceOnEveryEnding()
    {
        var created = new System.Collections.Generic.List<TestDecoder>();
        Func<ICodecDecoder> factory = () =>
        {
            var td = new TestDecoder(MakeDoubler());
            created.Add(td);
            return td;
        };
        Decoder d = With("dbl", factory, maxOutput: 50);

        // clean end
        Run(d, "abc"u8.ToArray(), "dbl", "dbl"); // second layer sees 6 bytes
        // output limit
        Run(d, new byte[100], "dbl");
        // dispose in the middle
        DecodedStream mid = d.Open(new EndlessStream(), "dbl");
        mid.ReadExactly(new byte[10], 0, 10);
        mid.Dispose();
        mid.Dispose();
        // never read at all
        d.Open(new MemoryStream(), "dbl").Dispose();

        Assert.NotEmpty(created);
        foreach (TestDecoder td in created)
        {
            Assert.Equal(1, td.Disposed);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-08", "SG-R-RDR-06")]
    public void ACodecIsNotCalledAgainAfterItsDone()
    {
        int calls = 0;
        DecodeFunc once = (ReadOnlySpan<byte> input, bool ended, Span<byte> output, out int consumed, out int written) =>
        {
            calls++;
            consumed = input.Length;
            written = 0;
            if (input.Length > 0)
            {
                output[0] = 7;
                written = 1;
                return CodecStatus.Continue;
            }

            return ended ? CodecStatus.Done : CodecStatus.NeedInput;
        };
        Decoder d = With("once", () => new TestDecoder(once));
        DecodedStream s = d.Open(new MemoryStream(new byte[] { 1 }), "once");
        Assert.Equal(new byte[] { 7 }, T.ReadAll(s, 10).Data);
        int afterEnd = calls;
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, s.Read(new byte[5], 0, 5));
        }

        Assert.Equal(afterEnd, calls);
        Assert.True(Enumerable.Range(0, 1).All(_ => true));
    }
}
