using System;

namespace Spillgate.Brotli;

/// <summary>Registers brotli on a <see cref="DecoderBuilder"/>.</summary>
public static class BrotliDecoderBuilderExtensions
{
    /// <summary>Registers the <c>br</c> token with a brotli codec.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The builder.</returns>
    public static DecoderBuilder AddBrotli(this DecoderBuilder builder, BrotliOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCodec("br", BrotliCodec.Create(options));
    }
}
