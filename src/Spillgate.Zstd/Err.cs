namespace Spillgate.Zstd;

/// <summary>Errors of the zstd decoder, with the prefix that names the codec.</summary>
internal static class Err
{
    internal static SpillgateException Corrupt(string message) => SpillgateException.Corrupt("zstd: " + message);

    internal static SpillgateException Internal(string message) => SpillgateException.Internal("zstd: " + message);

    internal static SpillgateException Memory(string message, long limit) =>
        new(ErrorKind.MemoryLimit, "zstd: " + message, 0, limit, null);
}
