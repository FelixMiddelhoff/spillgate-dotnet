using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spillgate;

/// <summary>What an <see cref="InputMeter"/> reads from: the caller's stream for layer 1, the layer below otherwise.</summary>
internal interface IByteSource
{
    /// <summary>Reads at least one byte, or returns 0 at the end. Never returns 0 for a non-empty buffer except at the end.</summary>
    int Read(Span<byte> buffer);

    /// <summary>Asynchronous <see cref="Read"/>.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>The caller's stream as the source of layer 1. It is never closed by this class.</summary>
internal sealed class StreamSource : IByteSource
{
    private readonly Stream _stream;

    internal StreamSource(Stream stream)
    {
        _stream = stream;
    }

    public int Read(Span<byte> buffer) => _stream.Read(buffer);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        _stream.ReadAsync(buffer, cancellationToken);
}
