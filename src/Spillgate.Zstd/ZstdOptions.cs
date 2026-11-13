namespace Spillgate.Zstd;

/// <summary>Options of the zstd codec. Immutable.</summary>
public sealed class ZstdOptions
{
    /// <summary>The default window cap, 8 MiB: at least the 8 MB that RFC 9659 requires decoders to support (SG-R-CDC-05).</summary>
    public const long DefaultMaxWindowBytes = 8L * 1024 * 1024;

    private ZstdOptions(long maxWindowBytes)
    {
        MaxWindowBytes = maxWindowBytes;
    }

    /// <summary>Gets the default options (window cap 8 MiB).</summary>
    public static ZstdOptions Default { get; } = new(DefaultMaxWindowBytes);

    /// <summary>Gets the largest window, in bytes, a frame may announce. A frame that needs more is <see cref="ErrorKind.MemoryLimit"/>.</summary>
    public long MaxWindowBytes { get; }

    /// <summary>Options with a window cap. The range (1 KiB to 1 GiB) is checked by <see cref="ZstdCodec.Create"/>.</summary>
    /// <param name="maxWindowBytes">The cap in bytes.</param>
    /// <returns>New options.</returns>
    public static ZstdOptions Create(long maxWindowBytes) => new(maxWindowBytes);
}
