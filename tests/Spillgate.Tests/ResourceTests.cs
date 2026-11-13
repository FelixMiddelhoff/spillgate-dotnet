using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Tests;

/// <summary>
/// Memory stays bounded however much a stream expands (SG-I-08). Two angles: allocation counters in this process
/// (the managed heap grows by a constant, not by the output), and a child process with a hard limit on the
/// garbage collected heap that decodes gigabyte-scale virtual bombs and reports its peak working set (which
/// also counts native memory, such as the runtime's brotli decoder).
/// </summary>
public sealed class ResourceTests
{
    private const long Gib = 1L << 30;

    public static IEnumerable<object[]> Forms()
    {
        foreach (string f in new[] { "gzip", "zlib", "raw", "br", "zstd", "identity", "chain" })
        {
            yield return new object[] { f };
        }
    }

    [Theory]
    [MemberData(nameof(Forms))]
    [Covers("SG-I-08", "SG-R-MEM-02", "SG-R-MEM-03", "SG-R-MEM-01")]
    public void AChildProcessWithAHardHeapLimitDecodesAGigabyteBombInConstantMemory(string form)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "canary", "Spillgate.Canary.dll");
        Assert.True(File.Exists(dll), "the canary was not built next to the tests: " + dll);
        long size = form == "chain" ? 256L << 20 : Gib;
        var start = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(form);
        start.ArgumentList.Add(size.ToString(CultureInfo.InvariantCulture));
        foreach (string key in start.Environment.Keys.ToList())
        {
            // The test host's own runtime settings must not leak into the child.
            if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(key);
            }
        }

        start.Environment["DOTNET_GCHeapHardLimit"] = "0x6000000"; // 96 MiB for the managed heap
        start.Environment["DOTNET_gcServer"] = "0";
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "the canary does not finish");
        Assert.True(process.ExitCode == 0, form + " canary failed: " + error + output);

        var fields = output.Trim().Split(' ').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("ok", fields["outcome"]);
        Assert.Equal(fields["expected"], fields["delivered"]);
        long peak = long.Parse(fields["peak_ws"], CultureInfo.InvariantCulture);
        long allocated = long.Parse(fields["allocated"], CultureInfo.InvariantCulture);
        long input = long.Parse(fields["input"], CultureInfo.InvariantCulture);
        // The managed allocations are a constant plus what the bomb itself needed; the peak working set stays far
        // below the gigabyte of output (the runtime itself takes about 50 MB, brotli up to 16 MB of window).
        Assert.True(allocated < (24L << 20) + (input * 3), form + " allocated " + allocated);
        Assert.True(peak < 400L << 20, form + " peak working set " + peak);
    }

    private static string DotnetHost()
    {
        // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet[.exe]
        string runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        string root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        string exe = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(exe) ? exe : "dotnet";
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("zstd")]
    [InlineData("br")]
    [InlineData("identity")]
    [Covers("SG-I-08", "SG-R-MEM-02", "SG-R-MEM-03")]
    public void TheManagedHeapGrowsByAConstantNotByTheOutput(string form)
    {
        // Warm up so type initialization and tables are not counted.
        DecodeBomb(form, 4 << 20);
        long small = DecodeBomb(form, 16 << 20);
        long large = DecodeBomb(form, 512 << 20);
        Assert.True(large < small + (1 << 20), form + ": 16 MiB allocated " + small + ", 512 MiB allocated " + large);
        Assert.True(large < 8 << 20, form + " allocated " + large);
    }

    private static long DecodeBomb(string form, long size)
    {
        byte[] stream;
        string[] enc;
        Stream source;
        switch (form)
        {
            case "gzip":
                stream = Generators.ZerosOf("gzip", size / 258);
                enc = new[] { "gzip" };
                source = new MemoryStream(stream);
                break;
            case "zstd":
                stream = Generators.ZstdZeros(size, 21);
                enc = new[] { "zstd" };
                source = new MemoryStream(stream);
                break;
            case "br":
                stream = Generators.BrotliZeros(size, 24);
                enc = new[] { "br" };
                source = new MemoryStream(stream);
                break;
            default:
                enc = Array.Empty<string>();
                source = new EndlessStream(size);
                break;
        }

        Decoder decoder = T.DecoderFor(long.MaxValue, l => l.WithoutRatioLimit());
        long before = GC.GetAllocatedBytesForCurrentThread();
        using (DecodedStream s = decoder.Open(source, enc))
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
            }

            Assert.True(total >= size - 258, "delivered " + total);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    [Covers("SG-R-MEM-02", "SG-R-CDC-07")]
    public void ALimitOfTwoToTheSixtyThreeNeverPreallocates()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Decoder decoder = Decoder.Create(Limits.Create(long.MaxValue));
        using DecodedStream s = decoder.Open(new MemoryStream(T.Gzip(T.Text(1, 1000))), "gzip");
        T.ReadAll(s, 4096);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }
}
