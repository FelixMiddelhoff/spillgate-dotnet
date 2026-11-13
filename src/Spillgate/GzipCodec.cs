using System;

namespace Spillgate;

/// <summary>gzip (RFC 1952) with concatenated members sharing one budget (SG-R-CDC-02).</summary>
internal sealed class GzipCodec : ICodec
{
    public ICodecDecoder CreateDecoder() => new GzipDecoder();

    private sealed class GzipDecoder : ICodecDecoder, IFailureAccounting
    {
        private const int FlagHcrc = 1 << 1;
        private const int FlagExtra = 1 << 2;
        private const int FlagName = 1 << 3;
        private const int FlagComment = 1 << 4;
        private const int ReservedFlags = 0xe0;
        private const int MaxString = 512;

        private Inflater? _inflater = new();
        private State _state = State.Boundary;
        private Crc32 _headerCrc = Crc32.Create();
        private Crc32 _crc = Crc32.Create();
        private long _size;
        private int _members;
        private int _flags;
        private int _fixedLeft;
        private int _extraLeft;
        private int _extraLow;
        private int _stringLeft;
        private int _storedHeaderCrc;
        private int _trailerRead;
        private ulong _trailer;

        private enum State : byte
        {
            Boundary,
            Id1,
            Id2,
            Method,
            Flags,
            Fixed,
            ExtraLength1,
            ExtraLength2,
            ExtraData,
            Name,
            Comment,
            HeaderCrc1,
            HeaderCrc2,
            Body,
            Trailer,
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

        private static SpillgateException Truncated(string what) => SpillgateException.Corrupt("truncated gzip " + what);

        private CodecStatus Run(
            ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, ref int pos, ref int written, out int consumed)
        {
            Inflater inflater = _inflater ?? throw SpillgateException.Internal("the decoder was disposed");
            for (; ; )
            {
                switch (_state)
                {
                    case State.Boundary:
                        if (pos >= input.Length)
                        {
                            consumed = pos;
                            if (!inputEnded)
                            {
                                return CodecStatus.NeedInput;
                            }

                            if (_members == 0)
                            {
                                throw Truncated("header");
                            }

                            _state = State.Finished;
                            return CodecStatus.Done;
                        }

                        if (_members > 0 && input[pos] != 0x1f)
                        {
                            // Looked at, not taken: the byte may be the probe byte after MaxInput (SG-R-INP-01).
                            throw SpillgateException.Corrupt("data that is not a gzip member follows the stream");
                        }

                        _headerCrc.Reset();
                        _state = State.Id1;
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
                            _crc.Update(output.Slice(0, w));
                            _size += w;
                            written = w;
                            if (status == CodecStatus.Done)
                            {
                                _state = State.Trailer;
                                _trailerRead = 0;
                                _trailer = 0;
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

                    case State.Trailer:
                        {
                            while (_trailerRead < 8 && pos < input.Length)
                            {
                                _trailer |= (ulong)input[pos++] << (8 * _trailerRead);
                                _trailerRead++;
                            }

                            if (_trailerRead < 8)
                            {
                                consumed = pos;
                                if (inputEnded)
                                {
                                    throw Truncated("trailer");
                                }

                                return CodecStatus.NeedInput;
                            }

                            if ((uint)_trailer != _crc.Value)
                            {
                                throw SpillgateException.Corrupt("gzip checksum mismatch");
                            }

                            if ((uint)(_trailer >> 32) != (uint)_size)
                            {
                                throw SpillgateException.Corrupt("gzip size mismatch");
                            }

                            _members++;
                            _state = State.Boundary;
                            break;
                        }

                    case State.ExtraData:
                        {
                            int n = Math.Min(_extraLeft, input.Length - pos);
                            _headerCrc.Update(input.Slice(pos, n));
                            pos += n;
                            _extraLeft -= n;
                            if (_extraLeft > 0)
                            {
                                consumed = pos;
                                if (inputEnded)
                                {
                                    throw Truncated("extra field");
                                }

                                return CodecStatus.NeedInput;
                            }

                            _state = AfterExtra();
                            break;
                        }

                    case State.Finished:
                        consumed = pos;
                        return CodecStatus.Done;

                    default:
                        if (pos >= input.Length)
                        {
                            consumed = pos;
                            if (inputEnded)
                            {
                                throw Truncated("header");
                            }

                            return CodecStatus.NeedInput;
                        }

                        HeaderByte(input[pos++]);
                        break;
                }
            }
        }

        private State AfterExtra() => (_flags & FlagName) != 0 ? StartString(State.Name)
            : (_flags & FlagComment) != 0 ? StartString(State.Comment)
            : AfterStrings();

        private State StartString(State state)
        {
            _stringLeft = MaxString;
            return state;
        }

        private State AfterName() => (_flags & FlagComment) != 0 ? StartString(State.Comment) : AfterStrings();

        private State AfterStrings() => (_flags & FlagHcrc) != 0 ? State.HeaderCrc1 : StartBody();

        private State StartBody()
        {
            _inflater!.Reset();
            _crc.Reset();
            _size = 0;
            return State.Body;
        }

        private void HeaderByte(byte b)
        {
            if (_state != State.HeaderCrc1 && _state != State.HeaderCrc2)
            {
                _headerCrc.Update(b);
            }

            switch (_state)
            {
                case State.Id1:
                    _state = b == 0x1f ? State.Id2 : throw SpillgateException.Corrupt("invalid gzip header");
                    break;
                case State.Id2:
                    _state = b == 0x8b ? State.Method : throw SpillgateException.Corrupt("invalid gzip header");
                    break;
                case State.Method:
                    _state = b == 8 ? State.Flags : throw SpillgateException.Corrupt("invalid gzip header");
                    break;
                case State.Flags:
                    if ((b & ReservedFlags) != 0)
                    {
                        throw SpillgateException.Corrupt("reserved gzip header flags are set");
                    }

                    _flags = b;
                    _fixedLeft = 6; // MTIME, XFL, OS
                    _state = State.Fixed;
                    break;
                case State.Fixed:
                    if (--_fixedLeft == 0)
                    {
                        _state = (_flags & FlagExtra) != 0 ? State.ExtraLength1
                            : (_flags & FlagName) != 0 ? StartString(State.Name)
                            : (_flags & FlagComment) != 0 ? StartString(State.Comment)
                            : AfterStrings();
                    }

                    break;
                case State.ExtraLength1:
                    _extraLow = b;
                    _state = State.ExtraLength2;
                    break;
                case State.ExtraLength2:
                    _extraLeft = _extraLow | (b << 8);
                    _state = _extraLeft > 0 ? State.ExtraData : AfterExtra();
                    break;
                case State.Name:
                    if (b == 0)
                    {
                        _state = AfterName();
                    }
                    else if (--_stringLeft == 0)
                    {
                        throw SpillgateException.Corrupt("gzip name is not terminated within 512 bytes");
                    }

                    break;
                case State.Comment:
                    if (b == 0)
                    {
                        _state = AfterStrings();
                    }
                    else if (--_stringLeft == 0)
                    {
                        throw SpillgateException.Corrupt("gzip comment is not terminated within 512 bytes");
                    }

                    break;
                case State.HeaderCrc1:
                    _storedHeaderCrc = b;
                    _state = State.HeaderCrc2;
                    break;
                case State.HeaderCrc2:
                    _storedHeaderCrc |= b << 8;
                    if (_storedHeaderCrc != (int)(_headerCrc.Value & 0xffff))
                    {
                        throw SpillgateException.Corrupt("gzip header checksum mismatch");
                    }

                    _state = StartBody();
                    break;
                default:
                    throw SpillgateException.Internal("gzip header state");
            }
        }
    }
}
