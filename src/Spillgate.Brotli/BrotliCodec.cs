using System;
using System.Buffers;
using System.Globalization;
using System.IO.Compression;

namespace Spillgate.Brotli;

/// <summary>
/// The brotli codec (RFC 7932) on the decoder of the .NET runtime (System.IO.Compression.BrotliDecoder), with
/// Spillgate's own framing: the window is read from the first byte and checked against the cap before any
/// decoding (SG-R-CDC-04, large-window streams are <see cref="ErrorKind.MemoryLimit"/>), the input is handed over
/// in exact steps so consumption is counted where the decoder takes bytes, and bytes after the last meta-block
/// are corrupt. The decoder of the runtime is native code; docs/threat-model.md names the patch level it needs.
/// </summary>
public sealed class BrotliCodec : ICodec
{
    private readonly int _maxWindowBits;

    private BrotliCodec(int maxWindowBits)
    {
        _maxWindowBits = maxWindowBits;
    }

    /// <summary>Creates the codec.</summary>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The codec, to register as <c>br</c>.</returns>
    /// <exception cref="SpillgateException">Kind <see cref="ErrorKind.InvalidOption"/>: the window cap is out of range.</exception>
    public static BrotliCodec Create(BrotliOptions? options = null)
    {
        BrotliOptions o = options ?? BrotliOptions.Default;
        if (o.MaxWindowBits < BrotliOptions.MinWindowBits || o.MaxWindowBits > BrotliOptions.DefaultMaxWindowBits)
        {
            throw new SpillgateException(ErrorKind.InvalidOption, "brotli: MaxWindowBits must be in 10..24");
        }

        return new BrotliCodec(o.MaxWindowBits);
    }

    /// <inheritdoc />
    public ICodecDecoder CreateDecoder() => new Adapter(_maxWindowBits);

    private sealed class Adapter : ICodecDecoder, IFailureAccounting
    {
        private readonly int _maxWindowBits;
        private BrotliDecoder _decoder;
        private State _state = State.Start;

        internal Adapter(int maxWindowBits)
        {
            _maxWindowBits = maxWindowBits;
        }

        private enum State : byte
        {
            Start,
            Running,
            Finished,
        }

        public int ConsumedBeforeFailure { get; private set; }

        public void Dispose() => _decoder.Dispose();

        public CodecStatus Decode(
            ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
        {
            consumed = 0;
            written = 0;
            if (_state == State.Finished)
            {
                return CodecStatus.Done;
            }

            if (_state == State.Start)
            {
                if (input.IsEmpty)
                {
                    if (inputEnded)
                    {
                        throw SpillgateException.Corrupt("brotli: the input ends before the stream header");
                    }

                    return CodecStatus.NeedInput;
                }

                CheckWindow(input[0]);
                _state = State.Running;
            }

            OperationStatus status = _decoder.Decompress(input, output, out consumed, out written);
            switch (status)
            {
                case OperationStatus.Done:
                    _state = State.Finished;
                    return CodecStatus.Done;
                case OperationStatus.DestinationTooSmall:
                    return CodecStatus.Continue;
                case OperationStatus.NeedMoreData:
                    if (inputEnded)
                    {
                        throw SpillgateException.Corrupt("brotli: the stream is truncated");
                    }

                    return CodecStatus.NeedInput;
                default:
                    ConsumedBeforeFailure = consumed;
                    throw SpillgateException.Corrupt("brotli: invalid data");
            }
        }

        /// <summary>SG-R-CDC-04: the window is announced by the first bits of the stream.</summary>
        private void CheckWindow(byte first)
        {
            int bits;
            if ((first & 1) == 0)
            {
                bits = 16;
            }
            else
            {
                int n = (first >> 1) & 7;
                if (n != 0)
                {
                    bits = 17 + n;
                }
                else
                {
                    int m = (first >> 4) & 7;
                    if (m == 1)
                    {
                        // The reserved pattern 1000001 announces large-window brotli, which this codec does not support.
                        throw new SpillgateException(
                            ErrorKind.MemoryLimit, "brotli: large-window streams are not supported", 0, 0, null);
                    }

                    bits = m == 0 ? 17 : 8 + m;
                }
            }

            if (bits > _maxWindowBits)
            {
                long window = (1L << bits) - 16;
                throw new SpillgateException(
                    ErrorKind.MemoryLimit,
                    "brotli: the stream needs a window of " + window.ToString(CultureInfo.InvariantCulture) + " bytes",
                    0,
                    (1L << _maxWindowBits) - 16,
                    null);
            }
        }
    }
}
