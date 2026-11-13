using System;
using System.Collections.Generic;
using System.Text;

namespace Spillgate;

/// <summary>
/// Builds a <see cref="Decoder"/>: optional strict zlib and explicitly registered codecs such as brotli and zstd.
/// There is no global registry and no registration at import time (SG-R-CDC-09). A builder is not thread safe;
/// the decoder it builds is.
/// </summary>
public sealed class DecoderBuilder
{
    private readonly Limits? _limits;
    private readonly List<(string? Name, ICodec? Codec)> _registrations = new();
    private bool _strictZlib;

    internal DecoderBuilder(Limits? limits)
    {
        _limits = limits;
    }

    /// <summary>
    /// Disables the raw deflate fallback of SG-R-CDC-03: without a valid zlib header a deflate stream is
    /// <see cref="ErrorKind.Corrupt"/>.
    /// </summary>
    /// <param name="strict">True to require the zlib wrapper.</param>
    /// <returns>This builder.</returns>
    public DecoderBuilder WithStrictZlib(bool strict = true)
    {
        _strictZlib = strict;
        return this;
    }

    /// <summary>
    /// Registers a codec for a lower-case Content-Encoding token. The built-in names identity, gzip, x-gzip and
    /// deflate cannot be replaced. Problems are reported by <see cref="Build"/>.
    /// </summary>
    /// <param name="name">The token, lower case, matching <c>[a-z0-9][a-z0-9._-]{0,31}</c>.</param>
    /// <param name="codec">The codec.</param>
    /// <returns>This builder.</returns>
    public DecoderBuilder AddCodec(string name, ICodec codec)
    {
        _registrations.Add((name, codec));
        return this;
    }

    /// <summary>Validates the limits and the registrations and builds the immutable decoder.</summary>
    /// <returns>The decoder.</returns>
    /// <exception cref="SpillgateException">Kind <see cref="ErrorKind.InvalidLimits"/> or <see cref="ErrorKind.InvalidOption"/>.</exception>
    public Decoder Build()
    {
        EffectiveLimits effective = EffectiveLimits.From(_limits);
        var codecs = new Dictionary<string, ICodec>(StringComparer.Ordinal)
        {
            [Header.Identity] = new IdentityCodec(),
            [Header.Gzip] = new GzipCodec(),
            ["deflate"] = new DeflateCodec(_strictZlib),
        };
        foreach ((string? name, ICodec? codec) in _registrations)
        {
            if (name is null || !Header.IsToken(name) || Header.HasUpperCase(name))
            {
                throw Invalid("codec name " + Quoted(name) + " must be a lower-case token (SG-R-HDR-04)");
            }

            if (name is Header.Identity or Header.Gzip or Header.XGzip or "deflate")
            {
                throw Invalid("codec name " + Quoted(name) + " is reserved for a built-in encoding");
            }

            if (codec is null)
            {
                throw Invalid("codec " + Quoted(name) + " is null");
            }

            if (!codecs.TryAdd(name, codec))
            {
                throw Invalid("codec " + Quoted(name) + " is registered twice");
            }
        }

        return new Decoder(effective, codecs);
    }

    private static string Quoted(string? name)
    {
        if (name is null)
        {
            return "null";
        }

        string head = name.Length > Header.MaxTokenLength + 1 ? name.Substring(0, Header.MaxTokenLength + 1) : name;
        return SpillgateException.Quote(Encoding.UTF8.GetBytes(head));
    }

    private static SpillgateException Invalid(string message) => new(ErrorKind.InvalidOption, message);
}
