using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Spillgate.Zstd;

/// <summary>The push-style frame reader: accumulates headers and blocks from the input a step at a time.</summary>
internal sealed class ZstdDecoder : ICodecDecoder, IFailureAccounting
{
    private const uint FrameMagic = 0xfd2fb528;
    private const uint SkippableMagic = 0x184d2a50;
    private const uint SkippableMask = 0xfffffff0;

    private static readonly int[] DictionaryBytes = { 0, 1, 2, 4 };
    private static readonly int[] ContentSizeBytes = { 0, 2, 4, 8 };

    private readonly long _maxWindow;
    private readonly Scratch _scratch = new();
    private readonly byte[] _small = new byte[16];

    private State _state = State.Boundary;
    private int _smallHave;
    private long _frames;
    private Frame? _frame;
    private Xxh64? _hasher;
    private bool _hasDeclared;
    private ulong _declared;
    private int _headerSize;
    private bool _checksum;
    private int _blockKind;
    private int _blockSize;
    private bool _blockLast;
    private int _bodyHave;
    private int _outPos;
    private long _skipLeft;

    internal ZstdDecoder(long maxWindow)
    {
        _maxWindow = maxWindow;
    }

    private enum State : byte
    {
        Boundary,
        Magic,
        SkipSize,
        Skipping,
        Descriptor,
        HeaderRest,
        BlockHeader,
        BlockBody,
        Drain,
        Checksum,
        Finished,
    }

    public int ConsumedBeforeFailure { get; private set; }

    public void Dispose()
    {
        _frame = null;
        _hasher = null;
        _scratch.Release();
    }

    public CodecStatus Decode(
        ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
    {
        int pos = 0;
        written = 0;
        try
        {
            CodecStatus status = Run(input, inputEnded, output, ref pos, ref written);
            consumed = pos;
            return status;
        }
        catch (SpillgateException)
        {
            ConsumedBeforeFailure = pos;
            throw;
        }
    }

    private static SpillgateException Truncated(string what) => Err.Corrupt("the input ends inside the " + what);

    private static uint Le32(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt32LittleEndian(b);

    private ulong Le(int n)
    {
        ulong v = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            v = (v << 8) | _small[i];
        }

        return v;
    }

    /// <summary>Collects bytes into the small buffer until it holds <paramref name="total"/> bytes.</summary>
    private bool Take(ReadOnlySpan<byte> input, ref int pos, int total)
    {
        int n = Math.Min(total - _smallHave, input.Length - pos);
        input.Slice(pos, n).CopyTo(_small.AsSpan(_smallHave));
        pos += n;
        _smallHave += n;
        return _smallHave == total;
    }

    private CodecStatus Run(ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, ref int pos, ref int written)
    {
        for (; ; )
        {
            switch (_state)
            {
                case State.Boundary:
                    if (pos >= input.Length)
                    {
                        if (!inputEnded)
                        {
                            return CodecStatus.NeedInput;
                        }

                        if (_frames > 0)
                        {
                            _state = State.Finished;
                            return CodecStatus.Done;
                        }

                        throw Err.Corrupt("an empty input is not a zstd stream");
                    }

                    if (_frames > 0)
                    {
                        int first = input[pos];
                        if (first != 0x28 && (first & 0xf0) != 0x50)
                        {
                            // Cannot start another frame: trailing data. The byte is only looked at, not taken,
                            // so it never counts against MaxInput (SG-R-INP-01).
                            throw Err.Corrupt("data that is not a zstd frame follows the stream");
                        }
                    }

                    _smallHave = 0;
                    _state = State.Magic;
                    break;

                case State.Magic:
                    {
                        if (!Take(input, ref pos, 4))
                        {
                            return NeedMore(inputEnded, "zstd magic number");
                        }

                        uint magic = Le32(_small);
                        _smallHave = 0;
                        if (magic == FrameMagic)
                        {
                            _state = State.Descriptor;
                        }
                        else if ((magic & SkippableMask) == SkippableMagic)
                        {
                            _state = State.SkipSize;
                        }
                        else
                        {
                            throw Err.Corrupt("data that is not a zstd frame follows the stream");
                        }

                        break;
                    }

                case State.SkipSize:
                    if (!Take(input, ref pos, 4))
                    {
                        return NeedMore(inputEnded, "skippable frame header");
                    }

                    _skipLeft = Le32(_small);
                    _smallHave = 0;
                    _state = State.Skipping;
                    break;

                case State.Skipping:
                    {
                        int n = (int)Math.Min(_skipLeft, input.Length - pos);
                        pos += n;
                        _skipLeft -= n;
                        if (_skipLeft > 0)
                        {
                            return NeedMore(inputEnded, "skippable frame");
                        }

                        _frames++; // a skippable frame is a frame: the input may end after it
                        _state = State.Boundary;
                        break;
                    }

                case State.Descriptor:
                    {
                        if (!Take(input, ref pos, 1))
                        {
                            return NeedMore(inputEnded, "frame header");
                        }

                        int descriptor = _small[0];
                        if ((descriptor & (1 << 3)) != 0)
                        {
                            throw Err.Corrupt("reserved bit set in the frame header");
                        }

                        bool singleSegment = (descriptor & (1 << 5)) != 0;
                        int flag = descriptor >> 6;
                        int fcsLen = flag == 0 && singleSegment ? 1 : ContentSizeBytes[flag];
                        _headerSize = 1 + (singleSegment ? 0 : 1) + DictionaryBytes[descriptor & 3] + fcsLen;
                        _state = State.HeaderRest;
                        break;
                    }

                case State.HeaderRest:
                    if (!Take(input, ref pos, _headerSize))
                    {
                        return NeedMore(inputEnded, "frame header");
                    }

                    StartFrame();
                    _state = State.BlockHeader;
                    _smallHave = 0;
                    break;

                case State.BlockHeader:
                    if (!Take(input, ref pos, 3))
                    {
                        return NeedMore(inputEnded, "block header");
                    }

                    ReadBlockHeader();
                    _smallHave = 0;
                    _bodyHave = 0;
                    _state = State.BlockBody;
                    break;

                case State.BlockBody:
                    if (!ReadBlockBody(input, ref pos))
                    {
                        return NeedMore(inputEnded, "block");
                    }

                    _outPos = 0;
                    _state = State.Drain;
                    break;

                case State.Drain:
                    {
                        int n = Math.Min(_scratch.OutLen - _outPos, output.Length - written);
                        if (n > 0)
                        {
                            _scratch.Out.AsSpan(_outPos, n).CopyTo(output.Slice(written));
                            _outPos += n;
                            written += n;
                        }

                        if (_outPos < _scratch.OutLen)
                        {
                            return CodecStatus.Continue;
                        }

                        // Everything this block produced is delivered; only now may the frame end be judged, so the
                        // bytes delivered before a late failure do not depend on the read size (SG-R-RDR-01).
                        _state = _blockLast ? State.Checksum : State.BlockHeader;
                        if (written > 0)
                        {
                            return CodecStatus.Continue;
                        }

                        break;
                    }

                case State.Checksum:
                    if (_checksum)
                    {
                        if (!Take(input, ref pos, 4))
                        {
                            return NeedMore(inputEnded, "content checksum");
                        }

                        if (Le32(_small) != _hasher!.Digest32())
                        {
                            throw Err.Corrupt("content checksum mismatch");
                        }
                    }

                    // Only an integrity check; the declared size is never an allowance (SG-R-CDC-07).
                    if (_hasDeclared && _declared != (ulong)_frame!.Total)
                    {
                        throw Err.Corrupt("the frame size differs from its declared size");
                    }

                    _frame = null;
                    _hasher = null;
                    _frames++;
                    _smallHave = 0;
                    _state = State.Boundary;
                    break;

                default:
                    return CodecStatus.Done;
            }
        }
    }

    private static CodecStatus NeedMore(bool inputEnded, string what)
    {
        if (inputEnded)
        {
            throw Truncated(what);
        }

        return CodecStatus.NeedInput;
    }

    private void StartFrame()
    {
        int descriptor = _small[0];
        bool singleSegment = (descriptor & (1 << 5)) != 0;
        _checksum = (descriptor & (1 << 2)) != 0;
        int at = 1;

        ulong window = 0;
        if (!singleSegment)
        {
            int wd = _small[at++];
            ulong baseSize = 1UL << (10 + (wd >> 3));
            window = baseSize + ((baseSize / 8) * (ulong)(wd & 7));
        }

        int dictionaryLen = DictionaryBytes[descriptor & 3];
        if (dictionaryLen > 0)
        {
            ulong id = ReadAt(at, dictionaryLen);
            at += dictionaryLen;
            if (id != 0)
            {
                throw Err.Corrupt("the frame needs a dictionary");
            }
        }

        int flag = descriptor >> 6;
        int fcsLen = flag == 0 && singleSegment ? 1 : ContentSizeBytes[flag];
        if (fcsLen > 0)
        {
            // For the 8-byte form the value may exceed 2^63: it is only compared as unsigned bits.
            _declared = ReadAt(at, fcsLen) + (fcsLen == 2 ? 256UL : 0UL);
            _hasDeclared = true;
        }
        else
        {
            _declared = 0;
            _hasDeclared = false;
        }

        if (singleSegment)
        {
            window = _declared;
        }

        // The memory declaration of the frame is checked before anything is allocated (SG-R-CDC-05).
        if (window > (ulong)_maxWindow)
        {
            throw Err.Memory(
                "the frame asks for a window of " + window.ToString(CultureInfo.InvariantCulture)
                    + " bytes (the cap is " + _maxWindow.ToString(CultureInfo.InvariantCulture) + ")",
                _maxWindow);
        }

        _frame = new Frame((int)window);
        _hasher = _checksum ? new Xxh64() : null;
    }

    private ulong ReadAt(int at, int n)
    {
        ulong v = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            v = (v << 8) | _small[at + i];
        }

        return v;
    }

    private void ReadBlockHeader()
    {
        Frame f = _frame ?? throw Err.Internal("no frame");
        uint header = (uint)Le(3);
        _blockLast = (header & 1) != 0;
        _blockKind = (int)((header >> 1) & 3);
        _blockSize = (int)(header >> 3); // below 2^21
        if (_blockKind == 3)
        {
            throw Err.Corrupt("reserved block type");
        }

        if (_blockSize > Scratch.BlockMax || _blockSize > f.BlockLimit)
        {
            throw Err.Corrupt("a block is larger than its maximum of " + f.BlockLimit.ToString(CultureInfo.InvariantCulture) + " bytes");
        }
    }

    /// <summary>Accumulates the block body. Returns true when the block is complete and decoded into the output buffer.</summary>
    private bool ReadBlockBody(ReadOnlySpan<byte> input, ref int pos)
    {
        Frame f = _frame ?? throw Err.Internal("no frame");
        Scratch s = _scratch;
        int need = _blockKind == 1 ? 1 : _blockSize;
        if (_bodyHave < need)
        {
            int n = Math.Min(need - _bodyHave, input.Length - pos);
            if (n > 0)
            {
                // The buffer grows with the bytes that arrived, not with the declared size.
                if (_blockKind == 2)
                {
                    s.EnsureContent(_bodyHave + n);
                    input.Slice(pos, n).CopyTo(s.Content.AsSpan(_bodyHave));
                }
                else if (_blockKind == 0)
                {
                    s.EnsureOut(_bodyHave + n);
                    input.Slice(pos, n).CopyTo(s.Out.AsSpan(_bodyHave));
                }
                else
                {
                    _small[0] = input[pos];
                }

                pos += n;
                _bodyHave += n;
            }

            if (_bodyHave < need)
            {
                return false;
            }
        }

        switch (_blockKind)
        {
            case 1:
                s.EnsureOut(_blockSize);
                Array.Fill(s.Out, _small[0], 0, _blockSize);
                s.OutLen = _blockSize;
                break;
            case 0:
                s.OutLen = _blockSize;
                break;
            default:
                s.ContentLen = _blockSize;
                f.Compressed(s);
                break;
        }

        f.Push(s.Out, s.OutLen);
        _hasher?.Update(s.Out.AsSpan(0, s.OutLen));
        return true;
    }
}
