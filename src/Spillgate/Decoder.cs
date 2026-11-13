using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Spillgate;

/// <summary>
/// A configured, immutable decoder: validated limits plus the codecs it knows. It is safe for concurrent use
/// (SG-R-RDR-07); create one at start-up and share it. Every stream it opens is independent.
/// </summary>
public sealed class Decoder
{
    private readonly EffectiveLimits _limits;
    private readonly Dictionary<string, ICodec> _codecs;

    internal Decoder(EffectiveLimits limits, Dictionary<string, ICodec> codecs)
    {
        _limits = limits;
        _codecs = codecs;
    }

    /// <summary>Creates a decoder for gzip, deflate and identity with the given limits.</summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The decoder.</returns>
    /// <exception cref="SpillgateException">Kind <see cref="ErrorKind.InvalidLimits"/>.</exception>
    public static Decoder Create(Limits limits) => new DecoderBuilder(limits).Build();

    /// <summary>Starts a builder for options and extra codecs (brotli, zstd).</summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The builder.</returns>
    public static DecoderBuilder CreateBuilder(Limits limits) => new(limits);

    /// <summary>
    /// Opens a decoding stream over <paramref name="source"/>. The Content-Encoding values are validated now,
    /// before any byte is read (SG-R-API-03). Disposing the stream does not close the source.
    /// </summary>
    /// <param name="source">The compressed (or unencoded) bytes. Owned by the caller.</param>
    /// <param name="contentEncoding">The Content-Encoding field values as received, in order. Empty means identity.</param>
    /// <returns>A forward-only readable stream.</returns>
    /// <exception cref="SpillgateException">The encodings are malformed, unsupported or too many.</exception>
    public DecodedStream Open(Stream source, params string?[] contentEncoding) =>
        Open(source, contentEncoding, leaveOpen: true, CancellationToken.None);

    /// <summary>Opens a decoding stream with cancellation and an explicit source ownership choice.</summary>
    /// <param name="source">The compressed (or unencoded) bytes.</param>
    /// <param name="contentEncoding">The Content-Encoding field values as received, in order.</param>
    /// <param name="leaveOpen">True (the default and the specification's rule) to leave the source open on dispose; false to dispose it with the stream.</param>
    /// <param name="cancellationToken">Observed on every read and every fetch from the source. Cancellation is sticky.</param>
    /// <returns>A forward-only readable stream.</returns>
    /// <exception cref="SpillgateException">The encodings are malformed, unsupported or too many.</exception>
    public DecodedStream Open(
        Stream source, IEnumerable<string?>? contentEncoding, bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        }

        List<string> elements = Header.Parse(contentEncoding);
        List<string> layers = Header.Resolve(elements, _codecs.ContainsKey, _limits.MaxLayers);
        return BuildChain(source, layers, leaveOpen, cancellationToken);
    }

    /// <summary>Decodes the whole body or fails; never returns partial data (SG-R-API-02).</summary>
    /// <param name="source">The compressed bytes.</param>
    /// <param name="contentEncoding">The Content-Encoding field values as received.</param>
    /// <returns>The complete decoded body.</returns>
    /// <exception cref="SpillgateException">Any limit, format or source failure.</exception>
    public byte[] DecodeAll(Stream source, params string?[] contentEncoding) =>
        DecodeAll(source, (IEnumerable<string?>)contentEncoding, CancellationToken.None);

    /// <summary>Decodes the whole body or fails, with cancellation.</summary>
    /// <param name="source">The compressed bytes.</param>
    /// <param name="contentEncoding">The Content-Encoding field values as received.</param>
    /// <param name="cancellationToken">The token; cancellation throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The complete decoded body.</returns>
    public byte[] DecodeAll(Stream source, IEnumerable<string?>? contentEncoding, CancellationToken cancellationToken)
    {
        using DecodedStream stream = Open(source, contentEncoding, leaveOpen: true, cancellationToken);
        var sink = new GrowingBuffer();
        byte[] chunk = new byte[LayerGuard.MaxStep];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            sink.Append(chunk.AsSpan(0, n));
        }

        return sink.ToArray();
    }

    /// <summary>Asynchronous <see cref="DecodeAll(Stream, IEnumerable{string?}, CancellationToken)"/>.</summary>
    /// <param name="source">The compressed bytes.</param>
    /// <param name="contentEncoding">The Content-Encoding field values as received.</param>
    /// <param name="cancellationToken">The token.</param>
    /// <returns>The complete decoded body.</returns>
    public async ValueTask<byte[]> DecodeAllAsync(
        Stream source, IEnumerable<string?>? contentEncoding, CancellationToken cancellationToken = default)
    {
        DecodedStream stream = Open(source, contentEncoding, leaveOpen: true, cancellationToken);
        await using (stream.ConfigureAwait(false))
        {
            var sink = new GrowingBuffer();
            byte[] chunk = new byte[LayerGuard.MaxStep];
            int n;
            while ((n = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                sink.Append(chunk.AsSpan(0, n));
            }

            return sink.ToArray();
        }
    }

    private DecodedStream BuildChain(Stream source, List<string> layers, bool leaveOpen, CancellationToken token)
    {
        int count = Math.Max(layers.Count, 1);
        var guards = new List<LayerGuard>(count);
        InputMeter? first = null;
        IByteSource current = new StreamSource(source);
        try
        {
            for (int index = 1; index <= count; index++)
            {
                // The input cap applies to the chain's source only (SG-R-INP-01).
                long cap = index == 1 ? _limits.MaxInput : 0;
                var meter = new InputMeter(current, cap, index, token);
                first ??= meter;
                ICodecDecoder decoder = layers.Count == 0
                    ? new IdentityCodec().CreateDecoder()
                    : NewDecoder(layers[index - 1], index);
                var guard = new LayerGuard(decoder, meter, index, _limits);
                guards.Add(guard);
                current = guard;
            }
        }
        catch
        {
            foreach (LayerGuard guard in guards)
            {
                guard.Close();
            }

            throw;
        }

        return new DecodedStream(guards.ToArray(), first!, _limits, leaveOpen ? null : source, token);
    }

    private ICodecDecoder NewDecoder(string name, int layer)
    {
        // A codec that throws while it is constructed is contained like one that throws later.
        ICodec codec = _codecs[name];
        ICodecDecoder? decoder;
        try
        {
            decoder = codec.CreateDecoder();
        }
        catch (SpillgateException e)
        {
            throw e.WithLayer(layer);
        }
        catch (OutOfMemoryException e)
        {
            throw new SpillgateException(ErrorKind.MemoryLimit, "creating the decoder ran out of memory", layer, 0, e);
        }
        catch (Exception e)
        {
            throw new SpillgateException(
                ErrorKind.Internal, "creating a decoder failed: " + e.GetType().Name, layer, 0, e);
        }

        return decoder ?? throw new SpillgateException(ErrorKind.Internal, "the codec returned no decoder", layer, 0, null);
    }

    /// <summary>A byte buffer that grows by doubling from what was actually produced, never from a limit (SG-R-MEM-02).</summary>
    private sealed class GrowingBuffer
    {
        private byte[] _data = new byte[8192];
        private int _size;

        internal void Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > Array.MaxLength - _size)
            {
                throw new SpillgateException(
                    ErrorKind.OutputLimit, "the body does not fit in a .NET array", 0, Array.MaxLength, null);
            }

            int needed = _size + bytes.Length;
            if (needed > _data.Length)
            {
                long doubled = Math.Max((long)_data.Length * 2, needed);
                int grown = (int)Math.Min(doubled, Array.MaxLength);
                try
                {
                    Array.Resize(ref _data, grown);
                }
                catch (OutOfMemoryException e)
                {
                    // Only this growth is converted: the process cannot hold the body, which is a limit of
                    // the process, not a corrupt stream.
                    _data = Array.Empty<byte>();
                    throw new SpillgateException(
                        ErrorKind.MemoryLimit,
                        "the decoded body does not fit in memory (" + grown.ToString(CultureInfo.InvariantCulture) + " bytes)",
                        0,
                        grown,
                        e);
                }
            }

            bytes.CopyTo(_data.AsSpan(_size));
            _size = needed;
        }

        internal byte[] ToArray() => _size == _data.Length ? _data : _data.AsSpan(0, _size).ToArray();
    }
}
