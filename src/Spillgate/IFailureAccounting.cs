namespace Spillgate;

/// <summary>
/// Implemented by the built-in decoders so the layer guard knows how many input bytes a step had taken when
/// it threw. A byte that was taken counts against MaxInput even when the step then reports corruption
/// (SG-R-INP-01); a byte that was only looked at does not. Decoders that do not implement this are assumed
/// to have taken nothing in a failed step.
/// </summary>
internal interface IFailureAccounting
{
    /// <summary>Gets the number of input bytes the last failed step had taken.</summary>
    int ConsumedBeforeFailure { get; }
}
