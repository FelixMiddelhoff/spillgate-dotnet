using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Spillgate.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Spillgate.Review.Tests;

public sealed class InputLimitReview
{
    private readonly ITestOutputHelper _out;
    public InputLimitReview(ITestOutputHelper o) { _out = o; }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [Covers("SG-R-INP-01", "SG-I-09")]
    public void InputLimitSemanticsAndInvariance(int form)
    {
        byte[] p = R.Text(8000, 3);
        var (stream, enc) = R.Forms(p)[form];
        string[] e = enc == "identity" ? Array.Empty<string>() : new[] { enc };
        foreach (var (delta, extra) in new[] { (0, 0), (0, 50), (-1, 0), (-1, 50), (1, 0), (-10, 0) })
        {
            long maxIn = stream.Length + delta;
            byte[] src = stream.Concat(new byte[extra]).ToArray();
            var seen = new HashSet<string>();
            foreach (int chunk in new[] { 1, 7, 1024, 100000 })
                foreach (string mode in R.Modes)
                {
                    R.Res r = R.Run(R.Dec(1 << 20, l => l.WithMaxInput(maxIn)), new R.Chunked(src, () => chunk), e, mode);
                    Assert.True(r.Err == null || r.Err is SpillgateException);
                    seen.Add(r.ToString());
                }

            _out.WriteLine($"{enc} len={stream.Length} maxIn={maxIn} extraSource={extra}: {string.Join(" | ", seen)}");
            Assert.Single(seen);
            R.Res one = R.Run(R.Dec(1 << 20, l => l.WithMaxInput(maxIn)), new MemoryStream(src), e, "readbig");
            if (delta >= 0 && extra == 0) Assert.Null(one.Err);
            if (delta < 0)
            {
                var se = Assert.IsType<SpillgateException>(one.Err);
                Assert.True(se.Kind is ErrorKind.InputLimit or ErrorKind.Corrupt, se.Kind.ToString());
                Assert.Equal(ErrorKind.InputLimit, se.Kind);
            }
        }
    }
}
