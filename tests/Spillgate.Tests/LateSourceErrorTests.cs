using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>
/// F1 of the Java review: when the source fails late, the bytes delivered before the failure must not depend on
/// the read sizes (or the source's chunking). They are exactly what the bytes the decoder already held can yield.
/// </summary>
public sealed class LateSourceErrorTests
{
    private static readonly byte[] Data = T.Mixed(5, 40_000);

    private static (byte[] Stream, string[] Encodings) Form(string name) => name switch
    {
        "gzip" => (T.Gzip(Data), new[] { "gzip" }),
        "zlib" => (T.ZLib(Data), new[] { "deflate" }),
        "raw" => (WithoutZlibLook(T.RawDeflate(Data)), new[] { "deflate" }),
        "br" => (T.Brotli(Data), new[] { "br" }),
        "zstd" => (Generators.ZstdStored(Data, 20), new[] { "zstd" }),
        "identity" => (Data, Array.Empty<string>()),
        "gzip+zlib" => (T.ZLib(T.Gzip(Data)), new[] { "gzip", "deflate" }),
        "br+gzip" => (T.Gzip(T.Brotli(Data)), new[] { "br", "gzip" }),
        "zstd+br+zlib" => (Generators.ZstdStored(T.Brotli(T.ZLib(Data)), 18), new[] { "deflate", "br", "zstd" }),
        _ => throw new ArgumentException(name),
    };

    private static byte[] WithoutZlibLook(byte[] raw) => raw;

    [Theory]
    [InlineData("gzip")]
    [InlineData("zlib")]
    [InlineData("raw")]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("identity")]
    [InlineData("gzip+zlib")]
    [InlineData("br+gzip")]
    [InlineData("zstd+br+zlib")]
    [Covers("SG-R-RDR-01", "SG-R-RDR-05", "SG-I-09", "SG-I-02")]
    public void ALateSourceFailureDeliversTheSamePrefixForEveryReadSizeAndChunking(string form)
    {
        (byte[] stream, string[] encodings) = Form(form);
        Decoder decoder = T.DecoderFor(1 << 22);
        var error = new IOException("boom");
        int[] points = { 0, 1, 11, stream.Length / 7, stream.Length / 3, stream.Length / 2, stream.Length - 20, stream.Length - 1, stream.Length };
        foreach (int fail in points.Distinct())
        {
            byte[]? reference = null;
            foreach (int chunk in new[] { int.MaxValue, 1, 7, 4096 })
            {
                foreach (int[] pattern in new[] { new[] { 1 << 20 }, new[] { 1 }, new[] { 7 }, new[] { 4096 }, new[] { 1, 100, 65536 } })
                {
                    Outcome o = T.ReadPattern(decoder.Open(new FailingStream(stream, fail, error, chunk), encodings), pattern);
                    var e = Assert.IsType<SpillgateException>(o.Error);
                    Assert.True(e.Kind == ErrorKind.SourceError, form + " fail " + fail + " chunk " + chunk + " pattern " + pattern[0] + ": " + o);
                    Assert.Same(error, e.InnerException ?? error);
                    Assert.True(o.Data.AsSpan().SequenceEqual(Data.AsSpan(0, o.Data.Length)), "not a prefix");
                    if (reference is null)
                    {
                        reference = o.Data;
                    }
                    else
                    {
                        Assert.True(
                            reference.Length == o.Data.Length,
                            form + " source failed after " + fail + " bytes: delivered " + o.Data.Length + " (chunk " + chunk + ", reads " + pattern[0] + ") vs " + reference.Length);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("zstd")]
    [InlineData("zstd+br+zlib")]
    [Covers("SG-R-RDR-01", "SG-R-RDR-05", "SG-I-09")]
    public async Task TheAsynchronousPathDeliversTheSamePrefix(string form)
    {
        (byte[] stream, string[] encodings) = Form(form);
        Decoder decoder = T.DecoderFor(1 << 22);
        var error = new IOException("boom");
        foreach (int fail in new[] { 3, stream.Length / 3, stream.Length - 1 })
        {
            Outcome sync = T.ReadPattern(decoder.Open(new FailingStream(stream, fail, error, 1000), encodings), new[] { 1 << 20 });
            foreach (int size in new[] { 1, 513, 1 << 20 })
            {
                DecodedStream s = decoder.Open(new FailingStream(stream, fail, error, 777), encodings);
                Outcome asyncOutcome = await T.ReadAllAsync(s, size);
                Assert.Equal(sync.Name, asyncOutcome.Name);
                Assert.Equal(sync.Data.Length, asyncOutcome.Data.Length);
            }
        }
    }

    [Fact]
    [Covers("SG-R-RDR-05", "SG-R-LAY-05")]
    public void ASourceFailureIsStickyAndNeverReadsTheSourceAgain()
    {
        byte[] stream = T.Gzip(Data);
        var source = new FailingStream(stream, 100, new IOException("x"));
        DecodedStream s = T.DecoderFor(1 << 22).Open(source, "gzip");
        Outcome o = T.ReadAll(s, 4096);
        Assert.Equal("source_error", o.Name);
        for (int i = 0; i < 3; i++)
        {
            var e = Assert.Throws<SpillgateException>(() => s.Read(new byte[10], 0, 10));
            Assert.Equal(ErrorKind.SourceError, e.Kind);
        }
    }
}
