using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Spillgate.Http;

/// <summary>Adapters that put Spillgate between the network and the application, for servers and for HttpClient.</summary>
public static class SpillgateExtensions
{
    /// <summary>
    /// Decodes request bodies that carry a <c>Content-Encoding</c> through <paramref name="decoder"/>. The handler
    /// reads <c>Request.Body</c> through the limits; the application sees decoded bytes and no
    /// <c>Content-Encoding</c> and no <c>Content-Length</c> header. Failures answer without echoing request data:
    /// 413 (limits), 415 (unsupported encoding), 400 (malformed or corrupt data), 500 (internal failure). A failure
    /// after the response has started aborts the connection. Requests without <c>Content-Encoding</c>, and requests
    /// without a body (<c>Content-Length: 0</c>), pass untouched (the server's own body size limit applies to them).
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="decoder">The decoder with the limits and codecs to apply.</param>
    /// <returns>The application builder.</returns>
    public static IApplicationBuilder UseSpillgateRequestDecoding(this IApplicationBuilder app, Decoder decoder)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(decoder);
        return app.Use(async (context, next) => await Handle(context, next, decoder).ConfigureAwait(false));
    }

    /// <summary>
    /// Adds Spillgate to a named or typed client of <c>IHttpClientFactory</c>: the primary handler does no automatic
    /// decompression, and a <see cref="SpillgateHandler"/> decodes the responses.
    /// </summary>
    /// <param name="builder">The client builder.</param>
    /// <param name="decoder">The decoder with the limits and codecs to apply.</param>
    /// <returns>The client builder.</returns>
    public static IHttpClientBuilder AddSpillgateDecoding(this IHttpClientBuilder builder, Decoder decoder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(decoder);
        builder.ConfigurePrimaryHttpMessageHandler(SpillgateHandler.CreateTransport);
        return builder.AddHttpMessageHandler(() => new SpillgateHandler(decoder));
    }

    private static async Task Handle(HttpContext context, RequestDelegate next, Decoder decoder)
    {
        HttpRequest request = context.Request;
        if (!request.Headers.TryGetValue("Content-Encoding", out Microsoft.Extensions.Primitives.StringValues values) || values.Count == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (request.ContentLength == 0 || context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false })
        {
            // No body at all: nothing to decode, and an empty body is not a truncated stream.
            await next(context).ConfigureAwait(false);
            return;
        }

        Stream original = request.Body;
        DecodedStream decoded;
        try
        {
            decoded = decoder.Open(original, values.ToArray(), leaveOpen: true, context.RequestAborted);
        }
        catch (SpillgateException e)
        {
            await Reject(context, e).ConfigureAwait(false);
            return;
        }

        request.Body = decoded;
        request.Headers.Remove("Content-Encoding");
        request.Headers.Remove("Content-Length");
        request.ContentLength = null;
        try
        {
            await next(context).ConfigureAwait(false);

            // Frameworks such as the minimal API swallow a failed body read and answer 400 themselves: the stream
            // remembers why it failed, so the answer can still say 413 or 415 as long as nothing was sent yet.
            if (decoded.Failure is SpillgateException swallowed)
            {
                await Reject(context, swallowed).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (SpillgateHttpErrors.Find(e) is { } failure && !SpillgateHttpErrors.IsCancellation(e))
        {
            await Reject(context, failure).ConfigureAwait(false);
        }
        finally
        {
            request.Body = original;
            await decoded.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task Reject(HttpContext context, SpillgateException failure)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = failure.Kind.HttpStatus();
        context.Response.ContentType = "text/plain; charset=utf-8";
        // A fixed text: nothing the client sent is echoed back.
        await context.Response.WriteAsync("The request body could not be decoded (" + failure.Kind.SpecName() + ").").ConfigureAwait(false);
    }
}

/// <summary>Convenience for tests and tools that need a hardened client without a factory.</summary>
public static class SpillgateHttpClient
{
    /// <summary>Creates an <see cref="HttpClient"/> whose responses are decoded through the limits of <paramref name="decoder"/>.</summary>
    /// <param name="decoder">The decoder with the limits and codecs to apply.</param>
    /// <returns>A client that owns its handlers.</returns>
    public static HttpClient Create(Decoder decoder) =>
        new(new SpillgateHandler(decoder, SpillgateHandler.CreateTransport()), disposeHandler: true);
}
