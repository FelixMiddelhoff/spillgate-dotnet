using System;
using System.Collections.Generic;
using System.IO;
using Spillgate.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Spillgate.Review.Tests;

/// <summary>Review findings on the brotli adapter (BCL BrotliDecoder dependence).</summary>
public sealed class BrotliReview
{
    private readonly ITestOutputHelper _out;
    public BrotliReview(ITestOutputHelper o) { _out = o; }

    // Mutated brotli stream (21 bytes). Valid up to a point, then corrupt; the BCL decoder holds decoded bytes
    // in its ring buffer and does not hand them out when the failing call has a big output buffer.
    private static readonly byte[] Corrupt21 = Convert.FromHexString("21BD4504001000E16A98AF87ED5486893E8804357D");

    private static R.Res Read(long max, int mode, int chunk) =>
        R.Run(R.Dec(max), new R.Chunked(Corrupt21, () => chunk), new[] { "br" }, R.Modes[mode]);

    /// <summary>
    /// F-A (medium, SG-R-RDR-01, SG-I-09): the bytes delivered before a corrupt error depend on the caller's read size
    /// (3071 bytes with 1-byte reads, 3056 with 16-byte reads, 0 with 4096 and larger). The spec allows this only for ratio_limit.
    /// </summary>
    [ReviewFact]
    [Covers("SG-R-RDR-01", "SG-I-09")]
    public void DeliveredPrefixDependsOnReadSize()
    {
        var results = new List<int>();
        foreach (int mode in new[] { 0, 1, 2, 3, 4, 9 })
        {
            R.Res r = Read(1_000_000, mode, 1000);
            _out.WriteLine(R.Modes[mode] + ": " + r);
            results.Add(r.Data.Length);
        }

        Assert.Single(new HashSet<int>(results));
    }

    private static readonly byte[] RatioFlip15 = Convert.FromHexString("21BD4504001000715A0026090143CA");

    /// <summary>
    /// F-A2 (low-medium, SG-R-RDR-01): consequence of F-A. 15 input bytes: with 1-byte reads the outcome is ratio_limit
    /// (65537 bytes), with 4096-byte and larger reads it is corrupt (65536 bytes). RDR-01 allows only the prefix length to vary
    /// for ratio_limit, not the class. (output_limit vs corrupt did not flip in a sweep of every MaxOutput 1..3100.)
    /// </summary>
    [ReviewFact]
    [Covers("SG-R-RDR-01", "SG-R-LAY-07")]
    public void OutcomeClassDependsOnReadSize()
    {
        var kinds = new Dictionary<string, string>();
        foreach (int mode in new[] { 0, 1, 2, 3, 4, 9 })
        {
            R.Res r = R.Run(R.Dec(1 << 20), new R.Chunked(RatioFlip15, () => 15), new[] { "br" }, R.Modes[mode]);
            _out.WriteLine(R.Modes[mode] + ": " + r);
            kinds[R.Modes[mode]] = r.Err is SpillgateException s ? s.Kind.ToString() : "ok";
        }

        Assert.Single(new HashSet<string>(kinds.Values));
    }
}
