using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>Cancellation: observed on every read and every fetch, reported as OperationCanceledException, sticky.</summary>
public sealed class CancellationTests
{
    private static readonly byte[] Data = T.Text(9, 200_000);

    [Fact]
    [Covers("SG-R-API-04")]
    public void ACanceledTokenFailsTheFirstReadAndIsStickyOnBothPaths()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new ChunkedStream(T.Gzip(Data));
        using DecodedStream s = T.DecoderFor(1 << 22).Open(source, new[] { "gzip" }, leaveOpen: true, cts.Token);
        for (int i = 0; i < 3; i++)
        {
            OperationCanceledException e = Assert.Throws<OperationCanceledException>(() => s.Read(new byte[100], 0, 100));
            Assert.Equal(cts.Token, e.CancellationToken);
        }

        Assert.ThrowsAsync<OperationCanceledException>(async () => await s.ReadExactlyAsync(new byte[100])).Wait();
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    [Covers("SG-R-API-04", "SG-I-03")]
    public void CancelingBetweenReadsStopsTheNextReadAndStaysCanceled()
    {
        using var cts = new CancellationTokenSource();
        using DecodedStream s = T.DecoderFor(1 << 22).Open(new ChunkedStream(T.Gzip(Data)), new[] { "gzip" }, leaveOpen: true, cts.Token);
        Assert.Equal(100, s.Read(new byte[100], 0, 100));
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => s.Read(new byte[100], 0, 100));
        Assert.Throws<OperationCanceledException>(() => s.Read(new byte[100], 0, 100));
        Outcome o = T.ReadAll(s, 10);
        Assert.Equal("canceled", o.Name);
        Assert.Equal(ErrorKind.Canceled, o.Kind);
    }

    [Fact]
    [Covers("SG-R-API-04")]
    public void TheCleanEndAndEarlierErrorsAreNotChangedByALaterCancel()
    {
        using var cts = new CancellationTokenSource();
        DecodedStream ok = T.DecoderFor(1 << 22).Open(new MemoryStream(Data), Array.Empty<string>(), leaveOpen: true, cts.Token);
        Assert.Equal(Data.Length, T.ReadAll(ok, 10_000).Data.Length);
        cts.Cancel();
        Assert.Equal(0, ok.Read(new byte[10], 0, 10));

        using var cts2 = new CancellationTokenSource();
        DecodedStream failed = T.DecoderFor(10).Open(new MemoryStream(Data), Array.Empty<string>(), leaveOpen: true, cts2.Token);
        Assert.Equal("output_limit", T.ReadAll(failed, 100).Name);
        cts2.Cancel();
        Assert.Equal("output_limit", T.ReadAll(failed, 100).Name);
    }

    [Fact]
    [Covers("SG-R-API-04")]
    public void CancellationIsObservedWhileTheCodecConsumesInputWithoutOutput()
    {
        // A gzip header with a 64 KiB extra field followed by ever more bytes: the codec takes input and
        // produces nothing, so only the fetches can see the cancel. At most one more fetch happens after it.
        using var cts = new CancellationTokenSource();
        var header = new byte[] { 0x1f, 0x8b, 8, 0x04, 0, 0, 0, 0, 0, 3, 0xff, 0xff };
        var source = new CancelingStream(Generators.Concat(header, new byte[2_000_000]), 2, cts);
        using DecodedStream s = T.DecoderFor(1 << 22).Open(source, new[] { "gzip" }, leaveOpen: true, cts.Token);
        Assert.Throws<OperationCanceledException>(() => s.Read(new byte[100], 0, 100));
        Assert.True(source.ReadsAfterCancel <= 1, "reads after cancel: " + source.ReadsAfterCancel);
    }

    [Fact]
    [Covers("SG-R-API-04")]
    public async Task APerCallTokenCanceledBeforeTheCallDoesNotTouchTheStream()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new ChunkedStream(T.Gzip(Data));
        using DecodedStream s = T.DecoderFor(1 << 22).Open(source, "gzip");
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await s.ReadExactlyAsync(new byte[10], cts.Token));
        Assert.Equal(0, source.Reads);
        Assert.Equal(Data, (await T.ReadAllAsync(s, 5000)).Data); // not sticky: nothing was started
    }

    [Fact]
    [Covers("SG-R-API-04", "SG-R-RDR-05")]
    public async Task CancelingAnInFlightSourceReadIsStickyCanceled()
    {
        using var cts = new CancellationTokenSource();
        var source = new BlockingStream();
        DecodedStream s = T.DecoderFor(1 << 22).Open(source, "gzip");
        Task<int> pending = s.ReadAsync(new byte[100], cts.Token).AsTask();
        await source.Started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await s.ReadExactlyAsync(new byte[10]));
        Assert.Throws<OperationCanceledException>(() => s.Read(new byte[10], 0, 10));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    [Covers("SG-R-API-04", "SG-R-RDR-05")]
    public void ACancellationFromTheSourceIsCanceledNotASourceError()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Outcome o = T.Read(T.DecoderFor(100), new FailingStream(T.Gzip(Data), 10, new OperationCanceledException(cts.Token)), 100, "gzip");
        Assert.Equal(ErrorKind.Canceled, o.Kind);
        Assert.IsAssignableFrom<OperationCanceledException>(o.Error);
    }

    [Fact]
    [Covers("SG-R-API-04", "SG-R-API-02")]
    public async Task DecodeAllObservesTheToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Decoder decoder = T.DecoderFor(1 << 22);
        Assert.Throws<OperationCanceledException>(() => decoder.DecodeAll(new MemoryStream(T.Gzip(Data)), new[] { "gzip" }, cts.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await decoder.DecodeAllAsync(new MemoryStream(T.Gzip(Data)), new[] { "gzip" }, cts.Token));
    }

    [Fact]
    [Covers("SG-R-API-04")]
    public void ACancelInsideAChainIsSeenByEveryLayer()
    {
        using var cts = new CancellationTokenSource();
        byte[] chain = T.Gzip(T.Gzip(T.Gzip(Data)));
        using DecodedStream s = T.DecoderFor(1 << 22).Open(new ChunkedStream(chain, 100), new[] { "gzip", "gzip", "gzip" }, leaveOpen: true, cts.Token);
        s.ReadExactly(new byte[1000], 0, 1000);
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => s.Read(new byte[1000], 0, 1000));
    }

    private sealed class CancelingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _cancelOnRead;
        private readonly CancellationTokenSource _cts;
        private int _pos;
        private int _reads;

        public CancelingStream(byte[] data, int cancelOnRead, CancellationTokenSource cts)
        {
            _data = data;
            _cancelOnRead = cancelOnRead;
            _cts = cts;
        }

        public int ReadsAfterCancel { get; private set; }

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
            _reads++;
            if (_cts.IsCancellationRequested)
            {
                ReadsAfterCancel++;
            }

            if (_reads == _cancelOnRead)
            {
                _cts.Cancel();
            }

            int n = Math.Min(buffer.Length, _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer);
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

    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads { get; private set; }

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
            Reads++;
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
