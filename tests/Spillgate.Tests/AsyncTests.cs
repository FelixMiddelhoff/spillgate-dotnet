using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>The asynchronous path gives the same results as the synchronous one and does not capture contexts.</summary>
public sealed class AsyncTests
{
    private static readonly byte[] Data = T.Mixed(61, 90_000);

    public static IEnumerable<object[]> Forms()
    {
        yield return new object[] { "gzip" };
        yield return new object[] { "deflate" };
        yield return new object[] { "br" };
        yield return new object[] { "zstd" };
        yield return new object[] { "identity" };
        yield return new object[] { "gzip+br" };
    }

    private static (byte[] Stream, string[] Enc) Form(string name) => name switch
    {
        "gzip" => (T.Gzip(Data), new[] { "gzip" }),
        "deflate" => (T.ZLib(Data), new[] { "deflate" }),
        "br" => (T.Brotli(Data), new[] { "br" }),
        "zstd" => (Generators.ZstdStored(Data, 19), new[] { "zstd" }),
        "identity" => (Data, Array.Empty<string>()),
        _ => (T.Brotli(T.Gzip(Data)), new[] { "gzip", "br" }),
    };

    [Theory]
    [MemberData(nameof(Forms))]
    [Covers("SG-R-RDR-01", "SG-I-09", "SG-R-API-04")]
    public async Task AsyncReadsGiveTheBytesOfSyncReadsForAnyChunking(string name)
    {
        (byte[] stream, string[] enc) = Form(name);
        Decoder d = T.DecoderFor(1 << 22);
        foreach (int chunk in new[] { int.MaxValue, 1, 333 })
        {
            foreach (int size in new[] { 1, 99, 1 << 20 })
            {
                if (size == 1 && chunk == 1)
                {
                    continue;
                }

                DecodedStream s = d.Open(new ChunkedStream(stream, chunk), enc);
                Outcome o = await T.ReadAllAsync(s, size);
                Assert.True(o.Error is null && o.Data.AsSpan().SequenceEqual(Data), name + " chunk " + chunk + " read " + size + ": " + o);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Forms))]
    [Covers("SG-R-RDR-01", "SG-I-02")]
    public async Task AsyncFailuresMatchSyncFailures(string name)
    {
        (byte[] stream, string[] enc) = Form(name);
        Decoder d = T.DecoderFor(40_000);
        Outcome sync = T.Read(d, stream, 1 << 20, enc);
        Outcome async = await T.ReadAllAsync(d.Open(new ChunkedStream(stream, 700), enc), 4096);
        Assert.Equal(sync.Name, async.Name);
        Assert.Equal(sync.Data.Length, async.Data.Length);

        byte[] cut = T.Slice(stream, stream.Length / 2);
        if (name != "identity")
        {
            Outcome syncCut = T.Read(T.DecoderFor(1 << 22), cut, 1 << 20, enc);
            Outcome asyncCut = await T.ReadAllAsync(T.DecoderFor(1 << 22).Open(new ChunkedStream(cut, 700), enc), 4096);
            Assert.Equal("corrupt", asyncCut.Name);
            Assert.Equal(syncCut.Data.Length, asyncCut.Data.Length);
        }
    }

    [Fact]
    [Covers("SG-R-API-04")]
    public void TheAsyncPathNeverNeedsTheCallersSynchronizationContext()
    {
        // A context that never runs what is posted to it: any continuation that captured it would hang.
        var context = new DeadContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            byte[] stream = T.Gzip(Data);
            DecodedStream s = T.DecoderFor(1 << 22).Open(new PoolStream(stream), "gzip");
            var sink = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = s.ReadAsync(buffer.AsMemory()).AsTask().GetAwaiter().GetResult()) > 0)
            {
                sink.Write(buffer, 0, n);
            }

            Assert.Equal(Data, sink.ToArray());
            Assert.Equal(0, context.Posts);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    [Covers("SG-R-RDR-06", "SG-I-10")]
    public async Task DisposeAsyncAndCopyToAsyncWork()
    {
        byte[] stream = T.Gzip(Data);
        var sink = new MemoryStream();
        await using (DecodedStream s = T.DecoderFor(1 << 22).Open(new ChunkedStream(stream, 500), "gzip"))
        {
            await s.CopyToAsync(sink);
        }

        Assert.Equal(Data, sink.ToArray());

        // CopyTo (sync) is bounded by the limit like any read.
        var small = new MemoryStream();
        using DecodedStream limited = T.DecoderFor(1000).Open(new MemoryStream(stream), "gzip");
        Assert.Throws<SpillgateException>(() => limited.CopyTo(small));
        Assert.Equal(1000, small.Length);
    }

    private sealed class DeadContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => _posts;

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);
    }

    /// <summary>Completes its reads on the thread pool, as a network stream does.</summary>
    private sealed class PoolStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;

        public PoolStream(byte[] data)
        {
            _data = data;
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

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            int n = Math.Min(Math.Min(buffer.Length, 3000), _data.Length - _pos);
            _data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
