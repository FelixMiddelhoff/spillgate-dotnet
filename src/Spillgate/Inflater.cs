using System;

namespace Spillgate;

/// <summary>
/// A resumable DEFLATE (RFC 1951) decoder written for exact accounting. It takes input bytes only as it needs
/// them and gives back what it looked at but did not use, so the number of consumed bytes is exact at the end of
/// the stream (SG-R-RAT-02, SG-R-INP-01) and the caller can see trailing data. It never allocates from a value in
/// the stream: the window is 32 KiB and the Huffman tables are fixed-size arrays.
/// </summary>
internal sealed class Inflater
{
    private const int WindowSize = 32768;
    private const int WindowMask = WindowSize - 1;
    private const int MaxTableEntries = 1 << 15;

    private static readonly ushort[] LengthBase =
    {
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
    };

    private static readonly byte[] LengthExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    };

    private static readonly ushort[] DistanceBase =
    {
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097,
        6145, 8193, 12289, 16385, 24577,
    };

    private static readonly byte[] DistanceExtra =
    {
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    };

    private static readonly byte[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private const int FixedLiteralBits = 9;
    private const int FixedDistanceBits = 5;

    private static readonly ushort[] FixedLiteral = BuildFixedLiteral();
    private static readonly ushort[] FixedDistance = BuildFixedDistance();

    private readonly byte[] _window = new byte[WindowSize];
    private readonly byte[] _lengths = new byte[286 + 32];
    private readonly byte[] _codeLengthLengths = new byte[19];
    private readonly ushort[] _codeLengthTable = new ushort[1 << 7];

    private ushort[]? _ownLiteral;
    private ushort[]? _ownDistance;
    private ushort[] _literal = FixedLiteral;
    private ushort[] _distance = FixedDistance;
    private int _literalMask;
    private int _distanceMask;
    private int _codeLengthBits;

    private State _state = State.Header;
    private ulong _bitBuffer;
    private int _bitCount;
    private bool _lastBlock;
    private int _storedLeft;
    private int _hlit;
    private int _hdist;
    private int _hclen;
    private int _index;
    private int _matchLength;
    private int _matchDistance;
    private bool _needDistance;
    private int _windowPos;
    private long _total;

    private enum State : byte
    {
        Header,
        StoredHeader,
        StoredCopy,
        DynamicCounts,
        CodeLengthLengths,
        Lengths,
        Block,
        Finished,
    }

    /// <summary>Gets the input position reached when the last step threw (see <see cref="IFailureAccounting"/>).</summary>
    internal int ConsumedBeforeFailure { get; private set; }

    /// <summary>Gets the number of bytes produced since the last <see cref="Reset"/>.</summary>
    internal long TotalProduced => _total;

    internal static bool IsZlibHeader(int cmf, int flg) =>
        (cmf & 0x0f) == 8 && (cmf >> 4) <= 7 && (((cmf << 8) | flg) % 31) == 0;

    /// <summary>Starts a new stream. The window content is not cleared; distances are checked against the stream's own length.</summary>
    internal void Reset()
    {
        _state = State.Header;
        _bitBuffer = 0;
        _bitCount = 0;
        _lastBlock = false;
        _matchLength = 0;
        _needDistance = false;
        _total = 0;
        _windowPos = 0;
    }

    /// <summary>Puts two bytes the caller already took back in front of the stream (raw deflate after the zlib probe).</summary>
    internal void Prime(byte first, byte second)
    {
        _bitBuffer = first | ((ulong)second << 8);
        _bitCount = 16;
    }

    internal CodecStatus Inflate(
        ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
    {
        int pos = 0;
        int w = 0;
        CodecStatus status;

        try
        {
            for (; ; )
            {
                switch (_state)
                {
                    case State.Header:
                        {
                            Refill(input, ref pos);
                            if (_bitCount < 3)
                            {
                                status = CodecStatus.NeedInput;
                                goto Exit;
                            }

                            _lastBlock = (_bitBuffer & 1) != 0;
                            int type = (int)((_bitBuffer >> 1) & 3);
                            Drop(3);
                            switch (type)
                            {
                                case 0:
                                    Drop(_bitCount & 7);
                                    _state = State.StoredHeader;
                                    break;
                                case 1:
                                    _literal = FixedLiteral;
                                    _distance = FixedDistance;
                                    _literalMask = (1 << FixedLiteralBits) - 1;
                                    _distanceMask = (1 << FixedDistanceBits) - 1;
                                    _state = State.Block;
                                    break;
                                case 2:
                                    _state = State.DynamicCounts;
                                    break;
                                default:
                                    throw SpillgateException.Corrupt("invalid deflate block type");
                            }

                            break;
                        }

                    case State.StoredHeader:
                        {
                            Refill(input, ref pos);
                            if (_bitCount < 32)
                            {
                                status = CodecStatus.NeedInput;
                                goto Exit;
                            }

                            int len = (int)(_bitBuffer & 0xffff);
                            int nlen = (int)((_bitBuffer >> 16) & 0xffff);
                            if (len != (~nlen & 0xffff))
                            {
                                throw SpillgateException.Corrupt("invalid stored block lengths");
                            }

                            Drop(32);
                            _storedLeft = len;
                            _state = State.StoredCopy;
                            break;
                        }

                    case State.StoredCopy:
                        {
                            while (_storedLeft > 0 && _bitCount >= 8 && w < output.Length)
                            {
                                output[w++] = (byte)_bitBuffer;
                                Drop(8);
                                _storedLeft--;
                            }

                            if (_storedLeft > 0 && _bitCount < 8)
                            {
                                int n = Math.Min(_storedLeft, Math.Min(input.Length - pos, output.Length - w));
                                input.Slice(pos, n).CopyTo(output.Slice(w));
                                pos += n;
                                w += n;
                                _storedLeft -= n;
                            }

                            if (_storedLeft == 0)
                            {
                                _state = _lastBlock ? State.Finished : State.Header;
                            }
                            else if (w == output.Length)
                            {
                                status = CodecStatus.Continue;
                                goto Exit;
                            }
                            else
                            {
                                status = CodecStatus.NeedInput;
                                goto Exit;
                            }

                            break;
                        }

                    case State.DynamicCounts:
                        {
                            Refill(input, ref pos);
                            if (_bitCount < 14)
                            {
                                status = CodecStatus.NeedInput;
                                goto Exit;
                            }

                            _hlit = (int)(_bitBuffer & 31) + 257;
                            _hdist = (int)((_bitBuffer >> 5) & 31) + 1;
                            _hclen = (int)((_bitBuffer >> 10) & 15) + 4;
                            Drop(14);
                            if (_hlit > 286 || _hdist > 30)
                            {
                                throw SpillgateException.Corrupt("too many length or distance codes");
                            }

                            Array.Clear(_codeLengthLengths);
                            _index = 0;
                            _state = State.CodeLengthLengths;
                            break;
                        }

                    case State.CodeLengthLengths:
                        {
                            while (_index < _hclen)
                            {
                                Refill(input, ref pos);
                                if (_bitCount < 3)
                                {
                                    status = CodecStatus.NeedInput;
                                    goto Exit;
                                }

                                _codeLengthLengths[CodeLengthOrder[_index++]] = (byte)(_bitBuffer & 7);
                                Drop(3);
                            }

                            if (!BuildTable(_codeLengthLengths, _codeLengthTable, out _codeLengthBits, allowSingleCode: false))
                            {
                                throw SpillgateException.Corrupt("invalid code length code");
                            }

                            _index = 0;
                            _state = State.Lengths;
                            break;
                        }

                    case State.Lengths:
                        {
                            int total = _hlit + _hdist;
                            int mask = (1 << _codeLengthBits) - 1;
                            while (_index < total)
                            {
                                Refill(input, ref pos);
                                ushort e = _codeLengthTable[(int)(_bitBuffer & (ulong)mask)];
                                int len = e & 15;
                                if (len == 0)
                                {
                                    throw SpillgateException.Corrupt("invalid code length symbol");
                                }

                                if (len > _bitCount)
                                {
                                    status = CodecStatus.NeedInput;
                                    goto Exit;
                                }

                                int sym = e >> 4;
                                if (sym < 16)
                                {
                                    Drop(len);
                                    _lengths[_index++] = (byte)sym;
                                    continue;
                                }

                                int extraBits = sym == 16 ? 2 : sym == 17 ? 3 : 7;
                                if (len + extraBits > _bitCount)
                                {
                                    status = CodecStatus.NeedInput;
                                    goto Exit;
                                }

                                int extra = (int)((_bitBuffer >> len) & (ulong)((1 << extraBits) - 1));
                                int repeat;
                                byte value;
                                if (sym == 16)
                                {
                                    if (_index == 0)
                                    {
                                        throw SpillgateException.Corrupt("invalid bit length repeat");
                                    }

                                    value = _lengths[_index - 1];
                                    repeat = 3 + extra;
                                }
                                else if (sym == 17)
                                {
                                    value = 0;
                                    repeat = 3 + extra;
                                }
                                else
                                {
                                    value = 0;
                                    repeat = 11 + extra;
                                }

                                if (_index + repeat > total)
                                {
                                    throw SpillgateException.Corrupt("invalid bit length repeat");
                                }

                                Drop(len + extraBits);
                                _lengths.AsSpan(_index, repeat).Fill(value);
                                _index += repeat;
                            }

                            if (_lengths[256] == 0)
                            {
                                throw SpillgateException.Corrupt("missing end-of-block code");
                            }

                            _ownLiteral ??= new ushort[MaxTableEntries];
                            _ownDistance ??= new ushort[MaxTableEntries];
                            int distanceBits = 1;
                            bool valid = BuildTable(_lengths.AsSpan(0, _hlit), _ownLiteral, out int literalBits, allowSingleCode: true);
                            if (valid)
                            {
                                valid = BuildTable(_lengths.AsSpan(_hlit, _hdist), _ownDistance, out distanceBits, allowSingleCode: true);
                            }

                            if (!valid)
                            {
                                throw SpillgateException.Corrupt("invalid literal/length or distance code set");
                            }

                            _literal = _ownLiteral;
                            _distance = _ownDistance;
                            _literalMask = (1 << literalBits) - 1;
                            _distanceMask = (1 << distanceBits) - 1;
                            _state = State.Block;
                            break;
                        }

                    case State.Block:
                        {
                            for (; ; )
                            {
                                Refill(input, ref pos);
                                if (_matchLength == 0)
                                {
                                    if (w == output.Length)
                                    {
                                        status = CodecStatus.Continue;
                                        goto Exit;
                                    }

                                    ushort e = _literal[(int)(_bitBuffer & (ulong)_literalMask)];
                                    int len = e & 15;
                                    if (len == 0)
                                    {
                                        throw SpillgateException.Corrupt("invalid literal/length code");
                                    }

                                    if (len > _bitCount)
                                    {
                                        status = CodecStatus.NeedInput;
                                        goto Exit;
                                    }

                                    int sym = e >> 4;
                                    if (sym < 256)
                                    {
                                        _bitBuffer >>= len;
                                        _bitCount -= len;
                                        output[w++] = (byte)sym;
                                        continue;
                                    }

                                    if (sym == 256)
                                    {
                                        Drop(len);
                                        _state = _lastBlock ? State.Finished : State.Header;
                                        break;
                                    }

                                    int lcode = sym - 257;
                                    if (lcode >= LengthBase.Length)
                                    {
                                        throw SpillgateException.Corrupt("invalid literal/length code");
                                    }

                                    int extraBits = LengthExtra[lcode];
                                    if (len + extraBits > _bitCount)
                                    {
                                        status = CodecStatus.NeedInput;
                                        goto Exit;
                                    }

                                    _matchLength = LengthBase[lcode] + (int)((_bitBuffer >> len) & (ulong)((1 << extraBits) - 1));
                                    Drop(len + extraBits);
                                    _needDistance = true;
                                }

                                if (_needDistance)
                                {
                                    ushort e = _distance[(int)(_bitBuffer & (ulong)_distanceMask)];
                                    int len = e & 15;
                                    if (len == 0)
                                    {
                                        throw SpillgateException.Corrupt("invalid distance code");
                                    }

                                    if (len > _bitCount)
                                    {
                                        status = CodecStatus.NeedInput;
                                        goto Exit;
                                    }

                                    int dcode = e >> 4;
                                    if (dcode >= DistanceBase.Length)
                                    {
                                        throw SpillgateException.Corrupt("invalid distance code");
                                    }

                                    int extraBits = DistanceExtra[dcode];
                                    if (len + extraBits > _bitCount)
                                    {
                                        status = CodecStatus.NeedInput;
                                        goto Exit;
                                    }

                                    int distance = DistanceBase[dcode] + (int)((_bitBuffer >> len) & (ulong)((1 << extraBits) - 1));
                                    if (distance > _total + w)
                                    {
                                        throw SpillgateException.Corrupt("distance too far back");
                                    }

                                    Drop(len + extraBits);
                                    _matchDistance = distance;
                                    _needDistance = false;
                                }

                                int n = Math.Min(_matchLength, output.Length - w);
                                if (n > 0)
                                {
                                    CopyMatch(output, ref w, n, _matchDistance);
                                    _matchLength -= n;
                                }

                                if (_matchLength > 0)
                                {
                                    status = CodecStatus.Continue;
                                    goto Exit;
                                }
                            }

                            break;
                        }

                    default:
                        {
                            // Finished: drop the padding bits and give back whole bytes that were read ahead.
                            Drop(_bitCount & 7);
                            int surplus = _bitCount >> 3;
                            if (surplus > pos)
                            {
                                throw SpillgateException.Internal("deflate read ahead more than it took in this step");
                            }

                            pos -= surplus;
                            _bitBuffer = 0;
                            _bitCount = 0;
                            status = CodecStatus.Done;
                            goto Exit;
                        }
                }
            }

        Exit:
            if (status == CodecStatus.NeedInput && inputEnded)
            {
                throw SpillgateException.Corrupt("the deflate stream is truncated");
            }

            if (status == CodecStatus.Continue)
            {
                // Give back whole bytes of read-ahead that came from this step, so nothing is held between steps.
                int back = Math.Min(_bitCount >> 3, pos);
                if (back > 0)
                {
                    _bitCount -= back * 8;
                    _bitBuffer &= (1UL << _bitCount) - 1;
                    pos -= back;
                }
            }

            RememberOutput(output.Slice(0, w));
            _total += w;
            consumed = pos;
            written = w;
            return status;
        }
        catch (SpillgateException)
        {
            ConsumedBeforeFailure = pos;
            throw;
        }
    }

    private static ushort[] BuildFixedLiteral()
    {
        var lengths = new byte[288];
        lengths.AsSpan(0, 144).Fill(8);
        lengths.AsSpan(144, 112).Fill(9);
        lengths.AsSpan(256, 24).Fill(7);
        lengths.AsSpan(280, 8).Fill(8);
        var table = new ushort[1 << FixedLiteralBits];
        BuildTable(lengths, table, out _, allowSingleCode: false);
        return table;
    }

    private static ushort[] BuildFixedDistance()
    {
        var lengths = new byte[32];
        lengths.AsSpan().Fill(5);
        var table = new ushort[1 << FixedDistanceBits];
        BuildTable(lengths, table, out _, allowSingleCode: false);
        return table;
    }

    /// <summary>
    /// Builds a canonical Huffman decoding table indexed by the next bits, least significant first. Entries are
    /// <c>symbol &lt;&lt; 4 | length</c>; length 0 marks a bit pattern no code uses. Over-subscribed sets are
    /// rejected; incomplete ones only when they hold a single code of length 1 (zlib's rule).
    /// </summary>
    private static bool BuildTable(ReadOnlySpan<byte> lengths, ushort[] table, out int tableBits, bool allowSingleCode)
    {
        Span<int> count = stackalloc int[16];
        int max = 0;
        foreach (byte l in lengths)
        {
            count[l]++;
            if (l > max)
            {
                max = l;
            }
        }

        count[0] = 0;
        if (max == 0)
        {
            tableBits = 1;
            table[0] = 0;
            table[1] = 0;
            return true;
        }

        int left = 1;
        for (int l = 1; l <= 15; l++)
        {
            left = (left << 1) - count[l];
            if (left < 0)
            {
                tableBits = 0;
                return false;
            }
        }

        if (left > 0 && !(allowSingleCode && max == 1))
        {
            tableBits = 0;
            return false;
        }

        Span<int> next = stackalloc int[16];
        int code = 0;
        for (int l = 1; l <= 15; l++)
        {
            code = (code + count[l - 1]) << 1;
            next[l] = code;
        }

        tableBits = max;
        int size = 1 << max;
        table.AsSpan(0, size).Clear();
        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int l = lengths[symbol];
            if (l == 0)
            {
                continue;
            }

            int c = next[l]++;
            int reversed = 0;
            for (int i = 0; i < l; i++)
            {
                reversed = (reversed << 1) | ((c >> i) & 1);
            }

            ushort entry = (ushort)((symbol << 4) | l);
            for (int i = reversed; i < size; i += 1 << l)
            {
                table[i] = entry;
            }
        }

        return true;
    }

    private void Refill(ReadOnlySpan<byte> input, ref int pos)
    {
        while (_bitCount <= 56 && pos < input.Length)
        {
            _bitBuffer |= (ulong)input[pos++] << _bitCount;
            _bitCount += 8;
        }
    }

    private void Drop(int n)
    {
        _bitBuffer >>= n;
        _bitCount -= n;
    }

    private void CopyMatch(Span<byte> output, ref int w, int n, int distance)
    {
        if (distance > w)
        {
            // The first bytes of the match lie before this step, in the window.
            int back = distance - w;
            int fromWindow = Math.Min(n, back);
            int start = (_windowPos - back) & WindowMask;
            int first = Math.Min(fromWindow, WindowSize - start);
            _window.AsSpan(start, first).CopyTo(output.Slice(w));
            if (first < fromWindow)
            {
                _window.AsSpan(0, fromWindow - first).CopyTo(output.Slice(w + first));
            }

            w += fromWindow;
            n -= fromWindow;
            if (n == 0)
            {
                return;
            }
        }

        int source = w - distance;
        if (distance >= n)
        {
            output.Slice(source, n).CopyTo(output.Slice(w));
        }
        else if (distance == 1)
        {
            output.Slice(w, n).Fill(output[source]);
        }
        else
        {
            // Overlapping copy: the pattern repeats with period `distance`, so each pass can copy what exists.
            int done = 0;
            while (done < n)
            {
                int chunk = Math.Min(n - done, w + done - source);
                output.Slice(source, chunk).CopyTo(output.Slice(w + done));
                done += chunk;
            }
        }

        w += n;
    }

    private void RememberOutput(ReadOnlySpan<byte> produced)
    {
        if (produced.Length == 0)
        {
            return;
        }

        if (produced.Length >= WindowSize)
        {
            produced.Slice(produced.Length - WindowSize).CopyTo(_window);
            _windowPos = 0;
            return;
        }

        int first = Math.Min(produced.Length, WindowSize - _windowPos);
        produced.Slice(0, first).CopyTo(_window.AsSpan(_windowPos));
        if (first < produced.Length)
        {
            produced.Slice(first).CopyTo(_window);
        }

        _windowPos = (_windowPos + produced.Length) & WindowMask;
    }
}
