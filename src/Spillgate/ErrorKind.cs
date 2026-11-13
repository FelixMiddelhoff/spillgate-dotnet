namespace Spillgate;

/// <summary>The outcome classes of the specification (SPEC section 2), as a machine-checkable value.</summary>
public enum ErrorKind
{
    /// <summary>The limits are invalid (SG-R-LIM-01 to SG-R-LIM-06).</summary>
    InvalidLimits,

    /// <summary>A builder option is invalid (codec name, codec options).</summary>
    InvalidOption,

    /// <summary>A layer would produce more than MaxOutput bytes.</summary>
    OutputLimit,

    /// <summary>The expansion ratio limit was exceeded.</summary>
    RatioLimit,

    /// <summary>The chain has more layers than allowed.</summary>
    LayerLimit,

    /// <summary>The stream needs more than MaxInput input bytes.</summary>
    InputLimit,

    /// <summary>A decoder would need more memory than its cap (window size, allocation failure).</summary>
    MemoryLimit,

    /// <summary>A well-formed Content-Encoding token that is not supported.</summary>
    UnsupportedEncoding,

    /// <summary>The Content-Encoding value is malformed.</summary>
    MalformedEncoding,

    /// <summary>The compressed data is invalid, truncated, or followed by trailing bytes.</summary>
    Corrupt,

    /// <summary>A codec or source violated its contract, or an unexpected failure occurred.</summary>
    Internal,

    /// <summary>The operation was canceled. Reported as <see cref="System.OperationCanceledException"/>.</summary>
    Canceled,

    /// <summary>The caller's source stream failed. The original exception is the inner exception.</summary>
    SourceError,
}

/// <summary>Names and conventional HTTP statuses of <see cref="ErrorKind"/> values.</summary>
public static class ErrorKindExtensions
{
    /// <summary>The outcome class name of the specification, for example <c>output_limit</c>.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The lower-case specification name.</returns>
    public static string SpecName(this ErrorKind kind) => kind switch
    {
        ErrorKind.InvalidLimits => "invalid_limits",
        ErrorKind.InvalidOption => "invalid_option",
        ErrorKind.OutputLimit => "output_limit",
        ErrorKind.RatioLimit => "ratio_limit",
        ErrorKind.LayerLimit => "layer_limit",
        ErrorKind.InputLimit => "input_limit",
        ErrorKind.MemoryLimit => "memory_limit",
        ErrorKind.UnsupportedEncoding => "unsupported_encoding",
        ErrorKind.MalformedEncoding => "malformed_encoding",
        ErrorKind.Corrupt => "corrupt",
        ErrorKind.Internal => "internal",
        ErrorKind.Canceled => "canceled",
        ErrorKind.SourceError => "source_error",
        _ => "internal",
    };

    /// <summary>
    /// The HTTP status a server should answer for this kind when it decodes a request body:
    /// 413 for limits, 415 for unsupported encodings, 400 for malformed or corrupt data and source
    /// errors, 500 for configuration errors and internal failures, 503 for canceled work.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The status code.</returns>
    public static int HttpStatus(this ErrorKind kind) => kind switch
    {
        ErrorKind.OutputLimit or ErrorKind.RatioLimit or ErrorKind.LayerLimit
            or ErrorKind.InputLimit or ErrorKind.MemoryLimit => 413,
        ErrorKind.UnsupportedEncoding => 415,
        ErrorKind.MalformedEncoding or ErrorKind.Corrupt or ErrorKind.SourceError => 400,
        ErrorKind.Canceled => 503,
        _ => 500,
    };
}
