using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Spillgate.Brotli;
using Spillgate.TestSupport;
using Spillgate.Zstd;
using Xunit;

namespace Spillgate.Tests;

/// <summary>
/// The public API is approved by a text dump per package (api/*.txt). Any change to it fails this test until the
/// dump is updated deliberately (run with SPILLGATE_UPDATE_API=1 and review the diff).
/// </summary>
public sealed class PublicApiTests
{
    public static IEnumerable<object[]> Assemblies()
    {
        yield return new object[] { "Spillgate", typeof(Decoder).Assembly };
        yield return new object[] { "Spillgate.Brotli", typeof(BrotliCodec).Assembly };
        yield return new object[] { "Spillgate.Zstd", typeof(ZstdCodec).Assembly };
    }

    [Theory]
    [MemberData(nameof(Assemblies))]
    [Covers("SG-R-API-01", "SG-R-CDC-09")]
    public void ThePublicApiIsAsApproved(string name, Assembly assembly)
    {
        Assert.Equal(string.Empty, ApiDump.Approve(name, assembly));
    }

    [Fact]
    [Covers("SG-R-API-01", "SG-R-LIM-01")]
    public void NoPublicMemberOfTheCoreCanDecodeWithoutLimits()
    {
        // Every public entry that takes a Stream and returns decoded data is an instance member of Decoder.
        foreach (Type type in typeof(Decoder).Assembly.GetExportedTypes())
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                bool takesStream = m.GetParameters().Any(p => typeof(Stream).IsAssignableFrom(p.ParameterType));
                Assert.False(takesStream, type.Name + "." + m.Name + " takes a Stream but is static");
            }
        }

        Assert.Empty(typeof(Decoder).GetConstructors());
    }
}
