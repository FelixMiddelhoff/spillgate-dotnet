using System;
using System.Globalization;

namespace Spillgate;

/// <summary>Validated limits with defaults applied (SG-R-LIM-01 to SG-R-LIM-08).</summary>
internal sealed class EffectiveLimits
{
    internal const int DefaultMaxLayers = 5;
    internal const long DefaultMaxRatio = 1000;
    internal const long DefaultRatioGrace = 64 * 1024;
    internal const int HardMaxLayers = 16;
    internal const long HardMaxRatio = (1L << 31) - 1;

    private EffectiveLimits(long maxOutput, int maxLayers, long maxRatio, long ratioGrace, long maxInput)
    {
        MaxOutput = maxOutput;
        MaxLayers = maxLayers;
        MaxRatio = maxRatio;
        RatioGrace = ratioGrace;
        MaxInput = maxInput;
    }

    internal long MaxOutput { get; }

    internal int MaxLayers { get; }

    /// <summary>Gets the ratio; 0 means the ratio check is disabled by the named opt-out.</summary>
    internal long MaxRatio { get; }

    internal long RatioGrace { get; }

    /// <summary>Gets the input cap; 0 means no input cap.</summary>
    internal long MaxInput { get; }

    internal static EffectiveLimits From(Limits? limits)
    {
        if (limits is null)
        {
            throw Invalid("limits are required (maxOutput has no default and no unlimited value)");
        }

        long output = limits.RawMaxOutput;
        if (output < 1)
        {
            throw Invalid("maxOutput must be in 1.." + long.MaxValue.ToString(CultureInfo.InvariantCulture)
                + ", got " + output.ToString(CultureInfo.InvariantCulture));
        }

        int layers = DefaultMaxLayers;
        if (limits.RawMaxLayers is { } l)
        {
            layers = l;
            if (layers < 1 || layers > HardMaxLayers)
            {
                throw Invalid("maxLayers must be in 1.." + HardMaxLayers.ToString(CultureInfo.InvariantCulture)
                    + ", got " + layers.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (limits.RawDisableRatioLimit && limits.RawMaxRatio.HasValue)
        {
            throw Invalid("maxRatio contradicts WithoutRatioLimit; set only one");
        }

        long ratio = DefaultMaxRatio;
        if (limits.RawDisableRatioLimit)
        {
            ratio = 0;
        }
        else if (limits.RawMaxRatio is { } r)
        {
            ratio = r;
            if (ratio < 1 || ratio > HardMaxRatio)
            {
                throw Invalid("maxRatio must be in 1.." + HardMaxRatio.ToString(CultureInfo.InvariantCulture)
                    + ", got " + ratio.ToString(CultureInfo.InvariantCulture));
            }
        }

        long grace = DefaultRatioGrace;
        if (limits.RawRatioGrace is { } g)
        {
            grace = g;
            if (grace < 0)
            {
                throw Invalid("ratioGrace must not be negative, got " + grace.ToString(CultureInfo.InvariantCulture));
            }
        }

        long input = 0;
        if (limits.RawMaxInput is { } m)
        {
            input = m;
            if (input < 1)
            {
                throw Invalid("maxInput must be in 1.." + long.MaxValue.ToString(CultureInfo.InvariantCulture)
                    + ", got " + input.ToString(CultureInfo.InvariantCulture));
            }
        }

        return new EffectiveLimits(output, layers, ratio, grace, input);
    }

    /// <summary>SG-R-RAT-01: produced greater than grace and greater than maxRatio * max(consumed, 1), without overflow.</summary>
    internal bool RatioExceeded(long produced, long consumed)
    {
        if (MaxRatio == 0 || produced <= RatioGrace)
        {
            return false;
        }

        long baseBytes = Math.Max(consumed, 1);
        if (baseBytes > long.MaxValue / MaxRatio)
        {
            return false;
        }

        return produced > MaxRatio * baseBytes;
    }

    private static SpillgateException Invalid(string message) => new(ErrorKind.InvalidLimits, message);
}
