using System;

namespace Spillgate;

/// <summary>identity: bytes pass through the limiter like any other codec (SG-R-CDC-10).</summary>
internal sealed class IdentityCodec : ICodec
{
    public ICodecDecoder CreateDecoder() => new IdentityDecoder();

    private sealed class IdentityDecoder : ICodecDecoder
    {
        public void Dispose()
        {
        }

        public CodecStatus Decode(
            ReadOnlySpan<byte> input, bool inputEnded, Span<byte> output, out int consumed, out int written)
        {
            int n = Math.Min(input.Length, output.Length);
            input.Slice(0, n).CopyTo(output);
            consumed = n;
            written = n;
            if (n > 0)
            {
                return CodecStatus.Continue;
            }

            return inputEnded ? CodecStatus.Done : CodecStatus.NeedInput;
        }
    }
}
