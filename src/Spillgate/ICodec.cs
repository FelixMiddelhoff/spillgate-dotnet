using System;

namespace Spillgate;

/// <summary>What a codec step reports. See <see cref="ICodecDecoder.Decode"/>.</summary>
public enum CodecStatus
{
    /// <summary>The decoder has taken everything useful from the input and needs more to go on.</summary>
    NeedInput,

    /// <summary>The decoder can go on without more input: the output was full, or it stopped at an internal boundary.</summary>
    Continue,

    /// <summary>The stream is complete and verified. Bytes the decoder did not take are trailing data.</summary>
    Done,
}

/// <summary>
/// A codec for one Content-Encoding token. Codecs are registered on a decoder instance with
/// <see cref="DecoderBuilder.AddCodec"/>; there is no global registry (SG-R-CDC-09). Every codec runs
/// behind the same limiter, ratio meter and input meter (SG-R-CDC-01).
/// </summary>
public interface ICodec
{
    /// <summary>Creates the state for one stream. Called once per layer and read; the codec itself is shared and must be thread safe.</summary>
    /// <returns>A new, independent decoder.</returns>
    ICodecDecoder CreateDecoder();
}

/// <summary>
/// The push-style decoder of one stream. The limiter owns the buffers and calls
/// <see cref="Decode"/> in steps of at most 64 KiB of output.
/// </summary>
public interface ICodecDecoder : IDisposable
{
    /// <summary>
    /// Decodes one step. Bytes counted in <paramref name="consumed"/> are bytes the decoder took from the input;
    /// they are what the ratio meter and the input limit are judged on, so a decoder must only take bytes it uses
    /// (a byte-aligned end of stream gives back what it looked at but did not need).
    /// </summary>
    /// <param name="input">Input bytes that are available now (may be empty).</param>
    /// <param name="inputEnded">True only when <paramref name="input"/> is empty and the source has ended. A decoder
    /// that is not at a clean end of its stream must then throw <see cref="SpillgateException"/> of kind
    /// <see cref="ErrorKind.Corrupt"/> (SG-R-CDC-06).</param>
    /// <param name="output">Space for decoded bytes, at least one byte.</param>
    /// <param name="consumed">Bytes taken from <paramref name="input"/>, 0 to its length.</param>
    /// <param name="written">Bytes written to <paramref name="output"/>, 0 to its length.</param>
    /// <returns>
    /// <see cref="CodecStatus.NeedInput"/> if it needs more input, <see cref="CodecStatus.Continue"/> if it can go on
    /// without, <see cref="CodecStatus.Done"/> at the verified end of the stream. A decoder that supports several
    /// concatenated members or frames returns <see cref="CodecStatus.Done"/> only with <paramref name="inputEnded"/>.
    /// </returns>
    /// <exception cref="SpillgateException">The data is invalid. Any other exception ends the layer with kind Internal.</exception>
    CodecStatus Decode(ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written);
}
