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

    private sealed class Adapter : ICodecDecoder, IFailureAccounting, IProbeAware
    {
        /// <summary>
        /// The decoder gets its input in steps of exactly this many bytes (plus the end of the input and the
        /// MaxInput boundary). The native decoder flushes output when it runs out of input and loses what it decoded in
        /// a call that fails, so how the stream is cut into calls decides which bytes are delivered before an error.
        /// Cutting it at fixed offsets makes the result independent of the source's chunking (SG-I-09).
        /// </summary>
        private const int Step = 1024;

        private readonly int _maxWindowBits;
        private readonly byte[] _stage = new byte[Step];
        private BrotliDecoder _decoder;
        private State _state = State.Start;
        private int _staged;
        private bool _feeding;
        private bool _drain;
        private bool _trailing;
        private bool _failed;

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

        public bool WindowIsProbe { private get; set; }

        public int ConsumedBeforeFailure { get; private set; }

        public void Dispose() => _decoder.Dispose();

        public CodecStatus Decode(
            ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
        {
            consumed = 0;
            written = 0;
            if (_failed)
            {
                throw SpillgateException.Corrupt("brotli: invalid data");
            }

            if (_trailing)
            {
                throw SpillgateException.Corrupt("brotli: unexpected data after the end of the stream");
            }

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

            int taken = 0;
            for (; ; )
            {
                if (!_feeding && !_drain)
                {
                    // The probe byte is looked at, not taken, as long as bytes are waiting to be fed: they may be the
                    // whole stream (SG-R-INP-01).
                    if (!(WindowIsProbe && _staged > 0))
                    {
                        int n = Math.Min(Step - _staged, input.Length - taken);
                        input.Slice(taken, n).CopyTo(_stage.AsSpan(_staged));
                        taken += n;
                        _staged += n;
                    }

                    bool flush = _staged == Step || inputEnded || (WindowIsProbe && _staged > 0);
                    if (!flush)
                    {
                        consumed = taken;
                        return CodecStatus.NeedInput;
                    }
                }

                int before = _staged;
                int space = output.Length - written;
                OperationStatus status = _decoder.Decompress(_stage.AsSpan(0, _staged), output.Slice(written), out int used, out int produced);
                written += produced;
                _staged -= used;
                if (_staged > 0 && used > 0)
                {
                    Buffer.BlockCopy(_stage, used, _stage, 0, _staged);
                }

                _feeding = _staged > 0;
                // With the output full the decoder may hold more, and then still reports NeedMoreData once its
                // input is used up: call it again, with no new input, until it leaves space unused.
                _drain = status == OperationStatus.DestinationTooSmall || (produced == space && space > 0);
                switch (status)
                {
                    case OperationStatus.Done:
                        _state = State.Finished;
                        if (_staged > 0)
                        {
                            // Bytes behind the end of the stream that were already staged: trailing data. They
                            // were counted when staged, except what this very call took beyond the stream.
                            ConsumedBeforeFailure = Math.Max(0, taken - _staged);
                            if (written == 0)
                            {
                                throw SpillgateException.Corrupt("brotli: unexpected data after the end of the stream");
                            }

                            _trailing = true;
                            consumed = taken;
                            return CodecStatus.Continue;
                        }

                        consumed = taken;
                        return written > 0 ? CodecStatus.Continue : CodecStatus.Done;
                    case OperationStatus.DestinationTooSmall:
                        consumed = taken;
                        return CodecStatus.Continue;
                    case OperationStatus.NeedMoreData:
                        if (written > 0)
                        {
                            consumed = taken;
                            return CodecStatus.Continue;
                        }

                        if (inputEnded)
                        {
                            consumed = taken;
                            throw SpillgateException.Corrupt("brotli: the stream is truncated");
                        }

                        if (before == 0)
                        {
                            consumed = taken;
                            return CodecStatus.NeedInput;
                        }

                        break; // all staged bytes are in; stage more or ask for more
                    default:
                        if (written > 0)
                        {
                            // What the decoder wrote before it met the fault is delivered first; the fault follows.
                            _failed = true;
                            consumed = taken;
                            return CodecStatus.Continue;
                        }

                        ConsumedBeforeFailure = Math.Max(0, taken - _staged);
                        throw SpillgateException.Corrupt("brotli: invalid data");
                }

                if (taken >= input.Length || WindowIsProbe)
                {
                    consumed = taken;
                    return CodecStatus.NeedInput;
                }
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
