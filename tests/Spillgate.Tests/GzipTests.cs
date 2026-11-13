using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>gzip framing (RFC 1952) beyond what the runtime accepts: every header field, members, trailers.</summary>
public sealed class GzipTests
{
    private static readonly Decoder Wide = T.DecoderFor(1 << 22);
    private static readonly byte[] Plain = "the quick brown fox jumps over the lazy dog"u8.ToArray();

    private static byte[] Member(
        int flags = 0,
        byte[]? extra = null,
        byte[]? name = null,
        byte[]? comment = null,
        bool headerCrc = false,
        byte[]? payload = null,
        int id1 = 0x1f,
        int id2 = 0x8b,
        int method = 8)
    {
        payload ??= Plain;
        var header = new List<byte> { (byte)id1, (byte)id2, (byte)method, (byte)flags, 1, 2, 3, 4, 0, 3 };
        if (extra is not null)
        {
            header.Add((byte)extra.Length);
            header.Add((byte)(extra.Length >> 8));
            header.AddRange(extra);
        }

        if (name is not null)
        {
            header.AddRange(name);
            header.Add(0);
        }

        if (comment is not null)
        {
            header.AddRange(comment);
            header.Add(0);
        }

        if (headerCrc)
        {
            uint crc = Generators.Crc32(header.ToArray());
            header.Add((byte)crc);
            header.Add((byte)(crc >> 8));
        }

        byte[] deflate = DeflateKit.Stored(payload, true);
        uint dataCrc = Generators.Crc32(payload);
        return Generators.Concat(
            header.ToArray(),
            deflate,
            BitConverter.GetBytes(dataCrc),
            BitConverter.GetBytes((uint)payload.Length));
    }

    private static string Decode(byte[] stream, int readSize = 4096) => T.Read(Wide, stream, readSize, "gzip").Name;

    [Fact]
    [Covers("SG-R-CDC-02")]
    public void AllOptionalHeaderFieldsAreAcceptedAndSkipped()
    {
        byte[] stream = Member(0x04 | 0x08 | 0x10 | 0x02, extra: new byte[100], name: "file.txt"u8.ToArray(), comment: "a comment"u8.ToArray(), headerCrc: true);
        foreach (int size in new[] { 1, 3, 4096 })
        {
            Outcome o = T.Read(Wide, new ChunkedStream(stream, 1), size, "gzip");
            Assert.True(o.Error is null && o.Data.SequenceEqual(Plain), o.ToString());
        }
    }

    [Fact]
    [Covers("SG-R-CDC-02")]
    public void AnExtraFieldOfTheMaximumSizeIsAccepted()
    {
        Assert.Equal("ok", Decode(Member(0x04, extra: new byte[65535])));
        Assert.Equal("ok", Decode(Member(0x04, extra: Array.Empty<byte>())));
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x40)]
    [InlineData(0x80)]
    [InlineData(0xe0)]
    [Covers("SG-R-CDC-02")]
    public void ReservedFlagBitsAreCorrupt(int flags)
    {
        Assert.Equal("corrupt", Decode(Member(flags)));
    }

    [Theory]
    [InlineData(0x00, 0x8b, 8)]
    [InlineData(0x1f, 0x8c, 8)]
    [InlineData(0x1f, 0x8b, 7)]
    [InlineData(0x1f, 0x8b, 9)]
    [InlineData(0x1e, 0x8b, 8)]
    [Covers("SG-R-CDC-02")]
    public void AWrongMagicOrMethodIsCorrupt(int id1, int id2, int method)
    {
        Assert.Equal("corrupt", Decode(Member(id1: id1, id2: id2, method: method)));
    }

    [Fact]
    [Covers("SG-R-CDC-02")]
    public void ANameOrCommentMustEndWithin512BytesIncludingTheTerminator()
    {
        Assert.Equal("ok", Decode(Member(0x08, name: Enumerable.Repeat((byte)'n', 511).ToArray())));
        Assert.Equal("corrupt", Decode(Member(0x08, name: Enumerable.Repeat((byte)'n', 512).ToArray())));
        Assert.Equal("ok", Decode(Member(0x10, comment: Enumerable.Repeat((byte)'c', 511).ToArray())));
        Assert.Equal("corrupt", Decode(Member(0x10, comment: Enumerable.Repeat((byte)'c', 512).ToArray())));

        // An unterminated name that runs for ever never needs more than 512 bytes of work.
        var endless = new byte[] { 0x1f, 0x8b, 8, 0x08, 0, 0, 0, 0, 0, 3 };
        Outcome o = T.Read(Wide, new ChunkedStream(Generators.Concat(endless, Enumerable.Repeat((byte)'x', 100_000).ToArray()), 997), 4096, "gzip");
        Assert.Equal("corrupt", o.Name);
    }

    [Fact]
    [Covers("SG-R-CDC-02")]
    public void AWrongHeaderChecksumIsCorrupt()
    {
        byte[] ok = Member(0x02, headerCrc: true);
        Assert.Equal("ok", Decode(ok));
        byte[] bad = (byte[])ok.Clone();
        bad[10] ^= 0x01; // first byte of the two-byte header CRC
        Assert.Equal("corrupt", Decode(bad));
        byte[] flipped = (byte[])ok.Clone();
        flipped[5] ^= 0x01; // MTIME is covered by it
        Assert.Equal("corrupt", Decode(flipped));
    }

    [Fact]
    [Covers("SG-R-CDC-02", "SG-R-CDC-07")]
    public void ATrailerMismatchOfCrcOrSizeIsCorrupt()
    {
        byte[] ok = Member();
        Assert.Equal("ok", Decode(ok));
        for (int i = 0; i < 8; i++)
        {
            byte[] bad = (byte[])ok.Clone();
            bad[bad.Length - 8 + i] ^= 0x80;
            Outcome o = T.Read(Wide, bad, 4096, "gzip");
            Assert.True(o.Name == "corrupt" && o.Data.SequenceEqual(Plain), "trailer byte " + i + ": " + o);
        }
    }

    [Fact]
    [Covers("SG-R-CDC-02", "SG-R-CDC-07", "SG-I-04")]
    public void TheDeclaredSizeIsComparedModulo2To32AndNeverUsedAsAnAllowance()
    {
        // The data is 2^32 + 43 bytes of output in a real bomb; here only the rule is checked on the formula:
        // ISIZE 0xFFFFFFFF with a 43-byte payload is a mismatch, and a huge declared size does not raise the limit.
        byte[] ok = Member();
        byte[] liar = (byte[])ok.Clone();
        for (int i = 0; i < 4; i++)
        {
            liar[liar.Length - 4 + i] = 0xff;
        }

        Assert.Equal("corrupt", Decode(liar));
        Decoder tiny = T.DecoderFor(10);
        Assert.Equal("output_limit", T.Read(tiny, ok, 4096, "gzip").Name);
    }

    [Fact]
    [Covers("SG-R-CDC-02", "SG-R-CDC-10")]
    public void ConcatenatedMembersDecodeAndShareOneBudget()
    {
        byte[] a = Member(payload: new byte[300]);
        byte[] b = Member(payload: new byte[300], name: "x"u8.ToArray(), flags: 0x08);
        byte[] both = Generators.Concat(a, b, a);
        Assert.Equal(900, T.Read(Wide, new ChunkedStream(both, 5), 17, "gzip").Data.Length);
        Outcome limited = T.Read(T.DecoderFor(899), both, 100, "gzip");
        Assert.Equal("output_limit", limited.Name);
        Assert.Equal(899, limited.Data.Length);
        Assert.Equal("ok", T.Read(T.DecoderFor(900), both, 100, "gzip").Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    [Covers("SG-R-CDC-02", "SG-R-CDC-06")]
    public void ZeroPaddingAndOtherBytesAfterAMemberAreCorrupt(int count)
    {
        byte[] stream = Generators.Concat(Member(), new byte[count]);
        Outcome o = T.Read(Wide, new ChunkedStream(stream, 1), 4096, "gzip");
        Assert.Equal("corrupt", o.Name);
        Assert.Equal(Plain, o.Data);
        byte[] junk = Generators.Concat(Member(), "GARBAGE"u8.ToArray());
        Assert.Equal("corrupt", Decode(junk));
        // A second member that starts like a member but is cut short is corrupt too.
        byte[] cut = Generators.Concat(Member(), T.Slice(Member(), 5));
        Assert.Equal("corrupt", Decode(cut));
    }

    [Fact]
    [Covers("SG-R-CDC-06", "SG-R-CDC-02")]
    public void AnEmptyInputIsNotGzip()
    {
        Outcome o = T.Read(Wide, Array.Empty<byte>(), 100, "gzip");
        Assert.Equal("corrupt", o.Name);
    }

    [Fact]
    [Covers("SG-R-CDC-02", "SG-R-HDR-07")]
    public void XGzipIsGzip()
    {
        Assert.Equal(Plain, T.Read(Wide, Member(), 100, "x-gzip").Data);
    }

    [Fact]
    [Covers("SG-R-CDC-02", "SG-I-09")]
    public void MemberBoundariesInsideASourceChunkAreHandledForEveryChunking()
    {
        byte[] stream = Generators.Concat(Member(0x08, name: Encoding.ASCII.GetBytes("n")), Member(), Member(0x04, extra: new byte[10]));
        foreach (int chunk in new[] { 1, 2, 3, 7, 13, 64, 10_000 })
        {
            foreach (int size in new[] { 1, 11, 4096 })
            {
                Outcome o = T.Read(Wide, new ChunkedStream(stream, chunk), size, "gzip");
                Assert.True(o.Error is null && o.Data.Length == 3 * Plain.Length, "chunk " + chunk + " read " + size + ": " + o);
            }
        }
    }
}
