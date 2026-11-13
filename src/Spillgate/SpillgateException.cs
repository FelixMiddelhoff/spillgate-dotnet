using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Spillgate;

/// <summary>
/// A typed decoding failure. <see cref="Kind"/> carries the outcome class of the specification.
/// Cancellation is not reported with this type: it surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class SpillgateException : IOException
{
    private readonly string? _detail;

    /// <summary>Creates an exception of the given kind. Custom codecs use this to report corrupt data.</summary>
    /// <param name="kind">The outcome class.</param>
    /// <param name="message">A short description. It must not contain unbounded attacker-controlled data.</param>
    public SpillgateException(ErrorKind kind, string message)
        : this(kind, message, 0, 0, null)
    {
    }

    /// <summary>Creates an exception of the given kind with the original failure attached.</summary>
    /// <param name="kind">The outcome class.</param>
    /// <param name="message">A short description.</param>
    /// <param name="innerException">The original failure, or <see langword="null"/>.</param>
    public SpillgateException(ErrorKind kind, string message, Exception? innerException)
        : this(kind, message, 0, 0, innerException)
    {
    }

    internal SpillgateException(ErrorKind kind, string? detail, int layer, long limit, Exception? inner)
        : base(null, inner)
    {
        Kind = kind;
        Layer = layer;
        Limit = limit;
        _detail = detail;
    }

    /// <summary>The outcome class.</summary>
    public ErrorKind Kind { get; }

    /// <summary>The 1-based layer in which the failure happened, or 0 if it is not tied to a layer.</summary>
    public int Layer { get; }

    /// <summary>The limit value that was exceeded, or 0 if the kind has none.</summary>
    public long Limit { get; }

    /// <inheritdoc />
    public override string Message
    {
        get
        {
            var text = new StringBuilder("spillgate: ").Append(Kind.SpecName());
            if (Layer > 0)
            {
                text.Append(" in layer ").Append(Layer.ToString(CultureInfo.InvariantCulture));
            }

            if (Limit > 0)
            {
                text.Append(" (limit ").Append(Limit.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            if (!string.IsNullOrEmpty(_detail))
            {
                text.Append(": ").Append(_detail);
            }

            return text.ToString();
        }
    }

    internal static SpillgateException Corrupt(string detail) => new(ErrorKind.Corrupt, detail, 0, 0, null);

    internal static SpillgateException Internal(string detail) => new(ErrorKind.Internal, detail, 0, 0, null);

    internal SpillgateException WithLayer(int layer)
    {
        if (Layer > 0 || layer <= 0)
        {
            return this;
        }

        return new SpillgateException(Kind, _detail, layer, Limit, InnerException);
    }

    /// <summary>A copy thrown again by sticky readers, so each throw has its own stack trace.</summary>
    internal SpillgateException Repeat() => new(Kind, _detail, Layer, Limit, InnerException);

    /// <summary>Quotes at most 32 bytes of attacker-controlled text so it cannot forge log lines (SG-R-HDR-13).</summary>
    internal static string Quote(ReadOnlySpan<byte> bytes)
    {
        const int Max = 32;
        var text = new StringBuilder("\"");
        for (int i = 0; i < bytes.Length && i < Max; i++)
        {
            int b = bytes[i];
            if (b == '"')
            {
                text.Append("\\\"");
            }
            else if (b == '\\')
            {
                text.Append("\\\\");
            }
            else if (b >= 0x20 && b <= 0x7e)
            {
                text.Append((char)b);
            }
            else
            {
                text.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
        }

        if (bytes.Length > Max)
        {
            text.Append("...");
        }

        return text.Append('"').ToString();
    }
}
