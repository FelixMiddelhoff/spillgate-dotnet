using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Spillgate.Http;

/// <summary>
/// An <see cref="HttpClient"/> handler that decodes response bodies through a Spillgate <see cref="Decoder"/>, so
/// that no body is ever expanded without limits. It sets an explicit <c>Accept-Encoding</c> (the tokens the decoder
/// knows) and takes the body before the transport can decode it: the transport must therefore not do automatic
/// decompression, and the handler refuses to run on top of one that does.
/// <para>
/// A response whose <c>Content-Encoding</c> is malformed, unsupported or too deep surfaces as a
/// <see cref="SpillgateException"/> thrown by <see cref="HttpClient"/>. Failures while the body is read (limits,
/// corrupt data) surface where the body is read; <see cref="HttpClient"/> may wrap them in an
/// <see cref="HttpRequestException"/>, so use <see cref="SpillgateHttpErrors.Find"/> to get the original.
/// The decoded content has no <c>Content-Encoding</c> and no <c>Content-Length</c>: the length is unknown until the
/// body is decoded.
/// </para>
/// </summary>
public sealed class SpillgateHandler : DelegatingHandler
{
    private readonly Decoder _decoder;
    private readonly string _acceptEncoding;
    private int _checked;

    /// <summary>
    /// Creates a handler to be used as a delegating handler (for example with <c>AddHttpMessageHandler</c>); the
    /// inner handler is assigned by the pipeline and checked on the first request.
    /// </summary>
    /// <param name="decoder">The decoder with the limits and codecs to apply.</param>
    public SpillgateHandler(Decoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _decoder = decoder;
        _acceptEncoding = BuildAcceptEncoding(decoder);
    }

    /// <summary>Creates a handler on top of an explicit inner handler, which must not decompress by itself.</summary>
    /// <param name="decoder">The decoder with the limits and codecs to apply.</param>
    /// <param name="innerHandler">The transport, for example a <see cref="SocketsHttpHandler"/> with <c>AutomaticDecompression</c> none.</param>
    /// <exception cref="ArgumentException">The inner handler decodes content before this handler sees it.</exception>
    public SpillgateHandler(Decoder decoder, HttpMessageHandler innerHandler)
        : this(decoder)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
        InnerHandler = innerHandler;
        RequireNoAutomaticDecompression(innerHandler);
        Volatile.Write(ref _checked, 1);
    }

    /// <summary>A transport that leaves compressed bodies alone: a <see cref="SocketsHttpHandler"/> without automatic decompression.</summary>
    /// <returns>A new handler. The caller owns it.</returns>
    public static HttpMessageHandler CreateTransport() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None };

    /// <summary>Throws when a handler in the chain would decode a body before Spillgate sees it.</summary>
    /// <param name="handler">The first handler of the chain.</param>
    /// <exception cref="ArgumentException">A handler in the chain decompresses automatically.</exception>
    public static void RequireNoAutomaticDecompression(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        for (HttpMessageHandler? current = handler; current is not null;)
        {
            if (current is SocketsHttpHandler { AutomaticDecompression: not DecompressionMethods.None }
                || current is HttpClientHandler { AutomaticDecompression: not DecompressionMethods.None })
            {
                throw new ArgumentException(
                    "The transport decompresses responses by itself (AutomaticDecompression is set), so it would decode before the limits apply. Use DecompressionMethods.None.",
                    nameof(handler));
            }

            current = current is DelegatingHandler d ? d.InnerHandler : null;
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.Exchange(ref _checked, 1) == 0 && InnerHandler is { } inner)
        {
            RequireNoAutomaticDecompression(inner);
        }

        if (request.Headers.AcceptEncoding.Count == 0 && _acceptEncoding.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", _acceptEncoding);
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.Method == HttpMethod.Head)
        {
            return response;
        }

        try
        {
            HttpContent original = response.Content;
            IEnumerable<string> encodings = original.Headers.NonValidated.TryGetValues("Content-Encoding", out HeaderStringValues values)
                ? values
                : Array.Empty<string>();
            Stream raw = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            DecodedStream decoded = _decoder.Open(raw, encodings, leaveOpen: false, cancellationToken);
            var content = new StreamContent(decoded);
            foreach (KeyValuePair<string, IEnumerable<string>> header in original.Headers)
            {
                if (!header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                    && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            response.Content = content;
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static string BuildAcceptEncoding(Decoder decoder)
    {
        var tokens = new List<string>();
        foreach (string token in decoder.SupportedEncodings)
        {
            if (token != "identity")
            {
                tokens.Add(token);
            }
        }

        return string.Join(", ", tokens);
    }
}
