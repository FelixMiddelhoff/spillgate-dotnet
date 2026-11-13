using System.Globalization;

namespace Spillgate;

/// <summary>
/// The limits of a decoder. <see cref="Create"/> takes the one mandatory value, MaxOutput; there is no default
/// and no unlimited value (SG-R-LIM-01). The instance is immutable: every <c>With</c> method returns a new
/// one (SG-R-LIM-09). Values are validated when a <see cref="Decoder"/> is built, not here (SG-R-LIM-08).
/// </summary>
public sealed class Limits
{
    private readonly long _maxOutput;
    private readonly int? _maxLayers;
    private readonly long? _maxRatio;
    private readonly long? _ratioGrace;
    private readonly long? _maxInput;
    private readonly bool _disableRatioLimit;

    private Limits(long maxOutput, int? maxLayers, long? maxRatio, long? ratioGrace, long? maxInput, bool disable)
    {
        _maxOutput = maxOutput;
        _maxLayers = maxLayers;
        _maxRatio = maxRatio;
        _ratioGrace = ratioGrace;
        _maxInput = maxInput;
        _disableRatioLimit = disable;
    }

    internal long RawMaxOutput => _maxOutput;

    internal int? RawMaxLayers => _maxLayers;

    internal long? RawMaxRatio => _maxRatio;

    internal long? RawRatioGrace => _ratioGrace;

    internal long? RawMaxInput => _maxInput;

    internal bool RawDisableRatioLimit => _disableRatioLimit;

    /// <summary>Limits with the mandatory output cap. Any other value is applied with its default.</summary>
    /// <param name="maxOutput">The most bytes any layer may produce, 1 to 2^63-1.</param>
    /// <returns>New limits.</returns>
    public static Limits Create(long maxOutput) => new(maxOutput, null, null, null, null, false);

    /// <summary>Sets the maximum number of non-identity layers (default 5, valid 1 to 16).</summary>
    /// <param name="layers">The maximum.</param>
    /// <returns>New limits.</returns>
    public Limits WithMaxLayers(int layers) =>
        new(_maxOutput, layers, _maxRatio, _ratioGrace, _maxInput, _disableRatioLimit);

    /// <summary>Sets the maximum expansion ratio (output bytes per consumed input byte; default 1000, valid 1 to 2^31-1).</summary>
    /// <param name="ratio">The maximum ratio.</param>
    /// <returns>New limits.</returns>
    public Limits WithMaxRatio(long ratio) =>
        new(_maxOutput, _maxLayers, ratio, _ratioGrace, _maxInput, _disableRatioLimit);

    /// <summary>Sets the number of output bytes below which the ratio is not judged (default 65536, 0 checks from the first byte).</summary>
    /// <param name="bytes">The grace in bytes, not negative.</param>
    /// <returns>New limits.</returns>
    public Limits WithRatioGrace(long bytes) =>
        new(_maxOutput, _maxLayers, _maxRatio, bytes, _maxInput, _disableRatioLimit);

    /// <summary>Caps the compressed input read from the source (absent by default, valid 1 to 2^63-1).</summary>
    /// <param name="bytes">The most input bytes a stream may take.</param>
    /// <returns>New limits.</returns>
    public Limits WithMaxInput(long bytes) =>
        new(_maxOutput, _maxLayers, _maxRatio, _ratioGrace, bytes, _disableRatioLimit);

    /// <summary>The named opt-out of the ratio check (SG-R-LIM-04). Combining it with a MaxRatio is invalid.</summary>
    /// <returns>New limits.</returns>
    public Limits WithoutRatioLimit() =>
        new(_maxOutput, _maxLayers, _maxRatio, _ratioGrace, _maxInput, true);

    /// <inheritdoc />
    public override string ToString()
    {
        static string Show(long? value, string none) =>
            value is { } v ? v.ToString(CultureInfo.InvariantCulture) : none;

        return "Limits[maxOutput=" + _maxOutput.ToString(CultureInfo.InvariantCulture)
            + ", maxLayers=" + Show(_maxLayers, "default")
            + ", maxRatio=" + (_disableRatioLimit ? "disabled" : Show(_maxRatio, "default"))
            + ", ratioGrace=" + Show(_ratioGrace, "default")
            + ", maxInput=" + Show(_maxInput, "none") + "]";
    }
}
