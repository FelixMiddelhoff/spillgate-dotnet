using System;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Spillgate.Review.Tests;

public sealed class Diag
{
    private readonly ITestOutputHelper _out;
    public Diag(ITestOutputHelper o) { _out = o; }

    [Fact]
    public void Dump()
    {
        string path = Environment.GetEnvironmentVariable("SPILLGATE_DIAG");
        if (string.IsNullOrEmpty(path)) return;
        byte[] s = File.ReadAllBytes(path);
        long max = long.Parse(Environment.GetEnvironmentVariable("SPILLGATE_DIAG_MAX") ?? "1000000");
        Decoder d = R.Dec(max);
        foreach (int chunk in new[] { 1, 3, 15, 1000 })
            foreach (int rs in new[] { 1, 100, 4096, 65536, 1 << 20 })
            {
                var r = R.Run(d, new R.Chunked(s, () => chunk), new[] { "br" }, rs == 1 ? "read1" : rs == 100 ? "readbigcount" : "readbig");
                _out.WriteLine($"chunk={chunk} readsize~{rs}: {r}");
            }
    }
}
