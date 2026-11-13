using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;

namespace Spillgate.Tests;

/// <summary>The outcome of reading a stream to its terminal condition.</summary>
public sealed class Outcome
{
    public byte[] Data { get; init; } = Array.Empty<byte>();

    public Exception? Error { get; init; }

    public ErrorKind? Kind => Error switch
    {
        SpillgateException e => e.Kind,
        OperationCanceledException => ErrorKind.Canceled,
        _ => null,
    };

    public string Name => Error is null ? "ok" : Kind?.SpecName() ?? ("other:" + Error.GetType().Name);

    public override string ToString() => Name + " (" + Data.Length + " bytes)" + (Error is null ? string.Empty : ": " + Error.Message);
}

/// <summary>Builders and readers shared by the tests.</summary>
public static class T
{
    public static readonly int[] ReadSizes = { 1, 2, 3, 7, 64, 4096, 65536, 1 << 20 };

    public static Decoder DecoderFor(long maxOutput = 1 << 20, Func<Limits, Limits>? configure = null, bool codecs = true, bool strictZlib = false)
    {
        Limits limits = Limits.Create(maxOutput);
        if (configure is not null)
        {
            limits = configure(limits);
        }

        DecoderBuilder builder = Decoder.CreateBuilder(limits).WithStrictZlib(strictZlib);
        if (codecs)
        {
            builder.AddBrotli().AddZstd();
        }

        return builder.Build();
    }

    public static Outcome Read(Decoder decoder, byte[] source, int readSize = 4096, params string[] encodings) =>
        Read(decoder, new MemoryStream(source), readSize, encodings);

    public static Outcome Read(Decoder decoder, Stream source, int readSize, params string[] encodings)
    {
        DecodedStream stream;
        try
        {
            stream = decoder.Open(source, encodings);
        }
        catch (SpillgateException e)
        {
            return new Outcome { Error = e };
        }

        using (stream)
        {
            return ReadAll(stream, readSize);
        }
    }

    public static Outcome ReadAll(Stream stream, int readSize) => ReadPattern(stream, new[] { readSize });

    public static Outcome ReadPattern(Stream stream, int[] pattern)
    {
        var all = new MemoryStream();
        var buffer = new byte[pattern.Max()];
        try
        {
            for (int i = 0; ; i++)
            {
                int size = pattern[i % pattern.Length];
                int n = stream.Read(buffer, 0, size);
                if (n == 0)
                {
                    break;
                }

                all.Write(buffer, 0, n);
            }
        }
        catch (Exception e)
        {
            return new Outcome { Data = all.ToArray(), Error = e };
        }

        return new Outcome { Data = all.ToArray() };
    }

    public static async Task<Outcome> ReadAllAsync(Stream stream, int readSize, CancellationToken token = default)
    {
        var all = new MemoryStream();
        var buffer = new byte[readSize];
        try
        {
            for (; ; )
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, readSize), token);
                if (n == 0)
                {
                    break;
                }

                all.Write(buffer, 0, n);
            }
        }
        catch (Exception e)
        {
            return new Outcome { Data = all.ToArray(), Error = e };
        }

        return new Outcome { Data = all.ToArray() };
    }

    public static ErrorKind? KindOf(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (SpillgateException e)
        {
            return e.Kind;
        }
    }

    // -- compression through the runtime (the oracle for valid streams) --------------------------------------

    // .NET 10 writes nothing at all when an empty input is compressed; the valid empty streams are fixed here.
    public static byte[] Gzip(byte[] data, CompressionLevel level = CompressionLevel.Optimal) =>
        data.Length == 0
            ? new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0xff, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0 }
            : Through(data, s => new GZipStream(s, level, leaveOpen: true));

    public static byte[] ZLib(byte[] data, CompressionLevel level = CompressionLevel.Optimal) =>
        data.Length == 0
            ? new byte[] { 0x78, 0x9c, 3, 0, 0, 0, 0, 1 }
            : Through(data, s => new ZLibStream(s, level, leaveOpen: true));

    public static byte[] RawDeflate(byte[] data, CompressionLevel level = CompressionLevel.Optimal) =>
        data.Length == 0
            ? new byte[] { 3, 0 }
            : Through(data, s => new DeflateStream(s, level, leaveOpen: true));

    public static byte[] Brotli(byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        int quality = level switch
        {
            CompressionLevel.NoCompression => 0,
            CompressionLevel.Fastest => 1,
            CompressionLevel.SmallestSize => 11,
            _ => 5,
        };
        var dest = new byte[BrotliEncoder.GetMaxCompressedLength(data.Length) + 16];
        if (!BrotliEncoder.TryCompress(data, dest, out int written, quality, 22))
        {
            throw new InvalidOperationException("brotli compression failed");
        }

        return dest.AsSpan(0, written).ToArray();
    }

    public static byte[] Through(byte[] data, Func<Stream, Stream> wrap)
    {
        var sink = new MemoryStream();
        using (Stream z = wrap(sink))
        {
            z.Write(data, 0, data.Length);
        }

        return sink.ToArray();
    }

    /// <summary>Compressible but not trivial test data of the given size.</summary>
    public static byte[] Text(int seed, int size)
    {
        string[] words = { "spill", "gate", "stream", "window", "block", "frame", "literal", "match", "offset", "huffman", "the", "of" };
        var rnd = new Random(seed);
        var output = new byte[size];
        int pos = 0;
        while (pos < size)
        {
            string w = words[(int)Math.Min(words.Length - 1, -Math.Log(1 - rnd.NextDouble()) * 4)] + (rnd.Next(10) == 0 ? "\n" : " ");
            for (int i = 0; i < w.Length && pos < size; i++)
            {
                output[pos++] = (byte)w[i];
            }
        }

        return output;
    }

    public static byte[] Mixed(int seed, int size)
    {
        byte[] a = Text(seed, size / 2);
        byte[] b = Generators.Data(seed, size - a.Length);
        return Generators.Concat(a, b);
    }

    public static byte[] Slice(byte[] a, int length) => a.AsSpan(0, length).ToArray();

    public static string Sha(byte[] data) => Generators.Sha256Hex(data);

    public static byte[] Wrap(string codec, byte[] payload) => codec switch
    {
        "gzip" => Gzip(payload),
        "deflate" => ZLib(payload),
        "br" => Brotli(payload),
        "zstd" => Generators.ZstdStored(payload, 20),
        _ => throw new ArgumentException(codec),
    };

    public static IEnumerable<object[]> CodecNames()
    {
        foreach (string c in new[] { "gzip", "deflate", "br", "zstd" })
        {
            yield return new object[] { c };
        }
    }
}

/// <summary>Returns the data in chunks of the given sizes (cyclic), however much is asked for.</summary>
public sealed class ChunkedStream : Stream
{
    private readonly byte[] _data;
    private readonly int[] _chunks;
    private int _pos;
    private int _call;

    public ChunkedStream(byte[] data, params int[] chunks)
    {
        _data = data;
        _chunks = chunks.Length == 0 ? new[] { int.MaxValue } : chunks;
    }

    public int Reads { get; private set; }

    public long Total { get; private set; }

    public bool Disposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        Reads++;
        int limit = _chunks[_call++ % _chunks.Length];
        int n = Math.Min(Math.Min(buffer.Length, limit), _data.Length - _pos);
        _data.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        Total += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return Read(buffer.Span);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>Delivers the data, then throws instead of reporting the end (or throws after a number of bytes).</summary>
public sealed class FailingStream : Stream
{
    private readonly byte[] _data;
    private readonly int _failAfter;
    private readonly Exception _error;
    private readonly int _chunk;
    private int _pos;

    public FailingStream(byte[] data, int failAfter, Exception error, int chunk = int.MaxValue)
    {
        _data = data;
        _failAfter = Math.Min(failAfter, data.Length);
        _error = error;
        _chunk = chunk;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= _failAfter)
        {
            throw _error;
        }

        int n = Math.Min(Math.Min(buffer.Length, _chunk), _failAfter - _pos);
        _data.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return new ValueTask<int>(Read(buffer.Span));
        }
        catch (Exception e)
        {
            return ValueTask.FromException<int>(e);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A source that misreports its byte counts.</summary>
public sealed class LyingStream : Stream
{
    private readonly int _answer;

    public LyingStream(int answer)
    {
        _answer = answer;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _answer;

    public override int Read(Span<byte> buffer) => _answer < 0 ? _answer : buffer.Length + _answer;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>An endless source of zero bytes that counts what it gave (for tests that must not read it all).</summary>
public sealed class EndlessStream : Stream
{
    private readonly long _limit;

    public EndlessStream(long limit = long.MaxValue)
    {
        _limit = limit;
    }

    public long Total { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = (int)Math.Min(buffer.Length, _limit - Total);
        buffer.Slice(0, n).Clear();
        Total += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
