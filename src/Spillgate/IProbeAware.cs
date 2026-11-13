namespace Spillgate;

/// <summary>
/// Implemented by built-in decoders that hold input back (brotli stages its input in fixed steps so that its
/// output does not depend on how the source happens to be chunked). The layer guard tells them when the window it
/// shows is the probe byte after MaxInput: a byte that may be looked at but must not be taken unless the decoder
/// cannot go on without it (SG-R-INP-01).
/// </summary>
internal interface IProbeAware
{
    /// <summary>Sets a value indicating whether the input window is exactly the probe byte after MaxInput.</summary>
    bool WindowIsProbe { set; }
}
