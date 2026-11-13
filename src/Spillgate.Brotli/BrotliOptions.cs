namespace Spillgate.Brotli;

/// <summary>Options of the brotli codec. Immutable.</summary>
public sealed class BrotliOptions
{
    /// <summary>The format's largest window, 2^24 - 16 bytes (RFC 7932). It is the default cap (SG-R-CDC-04).</summary>
    public const int DefaultMaxWindowBits = 24;

    /// <summary>The smallest cap that can be set.</summary>
    public const int MinWindowBits = 10;

    private BrotliOptions(int maxWindowBits)
    {
        MaxWindowBits = maxWindowBits;
    }

    /// <summary>Gets the default options (window cap 24 bits).</summary>
    public static BrotliOptions Default { get; } = new(DefaultMaxWindowBits);

    /// <summary>Gets the largest window the codec accepts, as window bits (10 to 24). A stream that needs more is <see cref="ErrorKind.MemoryLimit"/>.</summary>
    public int MaxWindowBits { get; }

    /// <summary>Options with a window cap in bits. The window is 2^bits - 16 bytes. The range is checked by <see cref="BrotliCodec.Create"/>.</summary>
    /// <param name="maxWindowBits">10 to 24.</param>
    /// <returns>New options.</returns>
    public static BrotliOptions Create(int maxWindowBits) => new(maxWindowBits);
}
