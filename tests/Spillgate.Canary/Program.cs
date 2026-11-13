using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;

namespace Spillgate.Canary;

/// <summary>
/// Decodes a virtual bomb of the given size and prints what it cost: the peak working set of this process, the
/// bytes allocated on the managed heap, the outcome and the bytes delivered. The resource tests start it as a
/// child process, with a hard limit on the garbage collected heap, so a decoder that buffers the output fails
/// there and not only in a measurement.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: Spillgate.Canary <gzip|zlib|raw|br|zstd|identity|chain> <output bytes>");
            return 2;
        }

        string form = args[0];
        long size = long.Parse(args[1], CultureInfo.InvariantCulture);
        byte[] stream;
        string[] encodings;
        switch (form)
        {
            case "gzip":
                stream = Generators.ZerosOf("gzip", Math.Max(1, (size - 1) / 258));
                encodings = new[] { "gzip" };
                size = Generators.ZerosDecodedSize(Math.Max(1, (size - 1) / 258));
                break;
            case "zlib":
                stream = Generators.ZerosOf("zlib", Math.Max(1, (size - 1) / 258));
                encodings = new[] { "deflate" };
                size = Generators.ZerosDecodedSize(Math.Max(1, (size - 1) / 258));
                break;
            case "raw":
                stream = Generators.ZerosOf("raw", Math.Max(1, (size - 1) / 258));
                encodings = new[] { "deflate" };
                size = Generators.ZerosDecodedSize(Math.Max(1, (size - 1) / 258));
                break;
            case "br":
                stream = Generators.BrotliZeros(size, 24);
                encodings = new[] { "br" };
                break;
            case "zstd":
                stream = Generators.ZstdZeros(size, 23);
                encodings = new[] { "zstd" };
                break;
            case "identity":
                stream = Array.Empty<byte>();
                encodings = Array.Empty<string>();
                break;
            case "chain":
                // gzip around zstd around brotli: three layers, each a bomb of its own size class.
                stream = Generators.GzipStored(Generators.ZstdStored(Generators.BrotliZeros(size, 22), 20));
                encodings = new[] { "br", "zstd", "gzip" };
                break;
            default:
                Console.Error.WriteLine("unknown form " + form);
                return 2;
        }

        Decoder decoder = Decoder.CreateBuilder(Limits.Create(long.MaxValue).WithoutRatioLimit().WithMaxLayers(5))
            .AddBrotli()
            .AddZstd()
            .Build();
        Stream source = form == "identity" ? new Endless(size) : new MemoryStream(stream);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        long delivered = 0;
        string outcome = "ok";
        try
        {
            using DecodedStream decoded = decoder.Open(source, encodings);
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = decoded.Read(buffer, 0, buffer.Length)) > 0)
            {
                delivered += n;
            }
        }
        catch (SpillgateException e)
        {
            outcome = e.Kind.SpecName();
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"outcome={outcome} delivered={delivered} expected={size} peak_ws={peak} allocated={allocated} input={stream.Length} seconds={clock.Elapsed.TotalSeconds:F2}"));
        return 0;
    }

    private sealed class Endless : Stream
    {
        private readonly long _limit;
        private long _total;

        internal Endless(long limit)
        {
            _limit = limit;
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
            int n = (int)Math.Min(buffer.Length, _limit - _total);
            buffer.Slice(0, n).Clear();
            _total += n;
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
