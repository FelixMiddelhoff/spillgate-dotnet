using System;

namespace Spillgate;

/// <summary>CRC-32 (IEEE, reflected) as used by gzip, with a slicing-by-4 table.</summary>
internal struct Crc32
{
    private static readonly uint[] Table = BuildTable();

    private uint _state;

    internal static Crc32 Create() => new() { _state = 0xffffffff };

    internal readonly uint Value => ~_state;

    internal void Reset() => _state = 0xffffffff;

    internal void Update(byte value) => _state = Table[(_state ^ value) & 0xff] ^ (_state >> 8);

    internal void Update(ReadOnlySpan<byte> data)
    {
        uint crc = _state;
        int i = 0;
        while (data.Length - i >= 4)
        {
            crc ^= data[i] | ((uint)data[i + 1] << 8) | ((uint)data[i + 2] << 16) | ((uint)data[i + 3] << 24);
            crc = Table[768 + (crc & 0xff)] ^ Table[512 + ((crc >> 8) & 0xff)]
                ^ Table[256 + ((crc >> 16) & 0xff)] ^ Table[crc >> 24];
            i += 4;
        }

        for (; i < data.Length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
        }

        _state = crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[1024];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xedb88320u : c >> 1;
            }

            table[i] = c;
        }

        for (int i = 0; i < 256; i++)
        {
            uint c = table[i];
            for (int t = 1; t < 4; t++)
            {
                c = table[c & 0xff] ^ (c >> 8);
                table[(t * 256) + i] = c;
            }
        }

        return table;
    }
}

/// <summary>Adler-32 as used by zlib.</summary>
internal struct Adler32
{
    private const uint Mod = 65521;
    private const int Block = 5552;

    private uint _a;
    private uint _b;

    internal static Adler32 Create() => new() { _a = 1, _b = 0 };

    internal readonly uint Value => (_b << 16) | _a;

    internal void Update(ReadOnlySpan<byte> data)
    {
        uint a = _a;
        uint b = _b;
        int i = 0;
        while (i < data.Length)
        {
            int end = Math.Min(data.Length, i + Block);
            for (; i < end; i++)
            {
                a += data[i];
                b += a;
            }

            a %= Mod;
            b %= Mod;
        }

        _a = a;
        _b = b;
    }
}
