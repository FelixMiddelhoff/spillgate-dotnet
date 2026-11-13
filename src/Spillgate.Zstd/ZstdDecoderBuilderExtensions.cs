using System;

namespace Spillgate.Zstd;

/// <summary>Registers zstd on a <see cref="DecoderBuilder"/>.</summary>
public static class ZstdDecoderBuilderExtensions
{
    /// <summary>Registers the <c>zstd</c> token with a zstd codec.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The builder.</returns>
    public static DecoderBuilder AddZstd(this DecoderBuilder builder, ZstdOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCodec("zstd", ZstdCodec.Create(options));
    }
}
