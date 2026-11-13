using System.Globalization;

namespace Spillgate.Zstd;

/// <summary>
/// A managed Zstandard decoder (RFC 8878, RFC 9659). It checks the frame header (window against the cap,
/// dictionary id, reserved bits) before it allocates anything, decodes block by block with buffers that follow the
/// bytes that arrived, verifies the content checksum, and treats anything after the last frame as corrupt.
/// It contains no unsafe code and does not depend on the zstd of any runtime.
/// </summary>
public sealed class ZstdCodec : ICodec
{
    private const long MinWindow = 1L << 10;
    private const long MaxWindow = 1L << 30;

    private readonly long _maxWindow;

    private ZstdCodec(long maxWindow)
    {
        _maxWindow = maxWindow;
    }

    /// <summary>Creates the codec.</summary>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The codec, to register as <c>zstd</c>.</returns>
    /// <exception cref="SpillgateException">Kind <see cref="ErrorKind.InvalidOption"/>: the window cap is out of range.</exception>
    public static ZstdCodec Create(ZstdOptions? options = null)
    {
        ZstdOptions o = options ?? ZstdOptions.Default;
        if (o.MaxWindowBytes < MinWindow || o.MaxWindowBytes > MaxWindow)
        {
            throw new SpillgateException(
                ErrorKind.InvalidOption,
                "zstd: MaxWindowBytes must be in " + MinWindow.ToString(CultureInfo.InvariantCulture) + ".."
                    + MaxWindow.ToString(CultureInfo.InvariantCulture));
        }

        return new ZstdCodec(o.MaxWindowBytes);
    }

    /// <inheritdoc />
    public ICodecDecoder CreateDecoder() => new ZstdDecoder(_maxWindow);
}
