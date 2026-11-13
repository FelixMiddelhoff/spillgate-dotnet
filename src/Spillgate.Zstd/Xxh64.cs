using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Spillgate.Zstd;

/// <summary>XXH64 with seed 0, as the zstd content checksum uses it (low 32 bits).</summary>
internal sealed class Xxh64
{
    private const ulong P1 = 0x9E3779B185EBCA87UL;
    private const ulong P2 = 0xC2B2AE3D27D4EB4FUL;
    private const ulong P3 = 0x165667B19E3779F9UL;
    private const ulong P4 = 0x85EBCA77C2B2AE63UL;
    private const ulong P5 = 0x27D4EB2F165667C5UL;

    private readonly byte[] _tail = new byte[32];
    private ulong _v1 = unchecked(P1 + P2);
    private ulong _v2 = P2;
    private ulong _v3;
    private ulong _v4 = unchecked(0UL - P1);
    private int _tailLen;
    private ulong _total;

    internal void Update(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;
        if (_tailLen > 0)
        {
            int take = Math.Min(32 - _tailLen, data.Length);
            data.Slice(0, take).CopyTo(_tail.AsSpan(_tailLen));
            _tailLen += take;
            data = data.Slice(take);
            if (_tailLen < 32)
            {
                return;
            }

            Stripe(_tail);
            _tailLen = 0;
        }

        while (data.Length >= 32)
        {
            Stripe(data);
            data = data.Slice(32);
        }

        data.CopyTo(_tail);
        _tailLen = data.Length;
    }

    internal uint Digest32() => (uint)Digest();

    internal ulong Digest()
    {
        ulong h;
        if (_total >= 32)
        {
            h = BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7)
                + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18);
            h = Merge(Merge(Merge(Merge(h, _v1), _v2), _v3), _v4);
        }
        else
        {
            h = P5;
        }

        h += _total;
        int i = 0;
        while (_tailLen - i >= 8)
        {
            h = (BitOperations.RotateLeft(h ^ Round(0, BinaryPrimitives.ReadUInt64LittleEndian(_tail.AsSpan(i))), 27) * P1) + P4;
            i += 8;
        }

        if (_tailLen - i >= 4)
        {
            h = (BitOperations.RotateLeft(h ^ (BinaryPrimitives.ReadUInt32LittleEndian(_tail.AsSpan(i)) * P1), 23) * P2) + P3;
            i += 4;
        }

        while (i < _tailLen)
        {
            h = BitOperations.RotateLeft(h ^ (_tail[i] * P5), 11) * P1;
            i++;
        }

        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        return h ^ (h >> 32);
    }

    private static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + (input * P2), 31) * P1;

    private static ulong Merge(ulong acc, ulong value) => ((acc ^ Round(0, value)) * P1) + P4;

    private void Stripe(ReadOnlySpan<byte> b)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(b));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(8)));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(16)));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(24)));
    }
}
