using System;

namespace Spillgate;

/// <summary>deflate in HTTP: zlib (RFC 1950) with a raw deflate fallback (SG-R-CDC-03).</summary>
internal sealed class DeflateCodec : ICodec
{
    private readonly bool _strictZlib;

    internal DeflateCodec(bool strictZlib)
    {
        _strictZlib = strictZlib;
    }

    public ICodecDecoder CreateDecoder() => new DeflateDecoder(_strictZlib);

    private sealed class DeflateDecoder : ICodecDecoder, IFailureAccounting
    {
        private readonly bool _strict;
        private Inflater? _inflater = new();
        private State _state = State.Start;
        private bool _zlib;
        private Adler32 _adler = Adler32.Create();
        private int _have;
        private byte _first;
        private byte _second;
        private int _trailerRead;
        private uint _trailer;

        internal DeflateDecoder(bool strict)
        {
            _strict = strict;
        }

        private enum State : byte
        {
            Start,
            Body,
            Adler,
            Finished,
        }

        public int ConsumedBeforeFailure { get; private set; }

        public void Dispose() => _inflater = null;

        public CodecStatus Decode(
            ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
        {
            int pos = 0;
            written = 0;
            try
            {
                return Run(input, inputEnded, output, ref pos, ref written, out consumed);
            }
            catch (SpillgateException)
            {
                ConsumedBeforeFailure = pos;
                throw;
            }
        }

        private CodecStatus Run(
            ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, ref int pos, ref int written, out int consumed)
        {
            Inflater inflater = _inflater ?? throw SpillgateException.Internal("the decoder was disposed");
            for (; ; )
            {
                switch (_state)
                {
                    case State.Start:
                        while (_have < 2 && pos < input.Length)
                        {
                            if (_have == 0)
                            {
                                _first = input[pos++];
                            }
                            else
                            {
                                _second = input[pos++];
                            }

                            _have++;
                        }

                        if (_have < 2)
                        {
                            consumed = pos;
                            if (inputEnded)
                            {
                                throw SpillgateException.Corrupt("truncated deflate data");
                            }

                            return CodecStatus.NeedInput;
                        }

                        inflater.Reset();
                        if (Inflater.IsZlibHeader(_first, _second))
                        {
                            if ((_second & 0x20) != 0)
                            {
                                throw SpillgateException.Corrupt("preset dictionaries are not supported");
                            }

                            _zlib = true;
                        }
                        else if (_strict)
                        {
                            throw SpillgateException.Corrupt("not a zlib stream");
                        }
                        else
                        {
                            inflater.Prime(_first, _second);
                        }

                        _state = State.Body;
                        break;

                    case State.Body:
                        {
                            CodecStatus status;
                            int c;
                            int w;
                            try
                            {
                                status = inflater.Inflate(input.Slice(pos), inputEnded, output, out c, out w);
                            }
                            catch (SpillgateException)
                            {
                                pos += inflater.ConsumedBeforeFailure;
                                throw;
                            }

                            pos += c;
                            written = w;
                            if (_zlib)
                            {
                                _adler.Update(output.Slice(0, w));
                            }

                            if (status == CodecStatus.Done)
                            {
                                _state = _zlib ? State.Adler : State.Finished;
                                if (w > 0)
                                {
                                    consumed = pos;
                                    return CodecStatus.Continue;
                                }

                                break;
                            }

                            consumed = pos;
                            return status;
                        }

                    case State.Adler:
                        while (_trailerRead < 4 && pos < input.Length)
                        {
                            _trailer = (_trailer << 8) | input[pos++];
                            _trailerRead++;
                        }

                        if (_trailerRead < 4)
                        {
                            consumed = pos;
                            if (inputEnded)
                            {
                                throw SpillgateException.Corrupt("truncated deflate data");
                            }

                            return CodecStatus.NeedInput;
                        }

                        if (_trailer != _adler.Value)
                        {
                            throw SpillgateException.Corrupt("deflate checksum mismatch");
                        }

                        _state = State.Finished;
                        break;

                    default:
                        consumed = pos;
                        return CodecStatus.Done;
                }
            }
        }
    }
}
