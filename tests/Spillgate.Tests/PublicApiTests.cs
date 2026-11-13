using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
        string actual = Dump(assembly);
        string path = Repo.Path("api", name + ".txt");
        if (Environment.GetEnvironmentVariable("SPILLGATE_UPDATE_API") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
        }

        Assert.True(File.Exists(path), "api/" + name + ".txt is missing; run with SPILLGATE_UPDATE_API=1");
        string approved = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(approved, actual);
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

    private static string Dump(Assembly assembly)
    {
        var text = new StringBuilder();
        foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            text.Append(KindOf(type)).Append(' ').Append(type.FullName);
            Type[] bases = new[] { type.BaseType }.Concat(type.GetInterfaces()).Where(t => t is not null && t != typeof(object) && t != typeof(ValueType) && t != typeof(Enum)).Cast<Type>().OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray();
            if (bases.Length > 0)
            {
                text.Append(" : ").Append(string.Join(", ", bases.Select(b => b.FullName)));
            }

            text.Append('\n');
            var members = new List<string>();
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MemberInfo m in type.GetMembers(All))
            {
                string? line = Describe(type, m);
                if (line is not null)
                {
                    members.Add(line);
                }
            }

            foreach (string line in members.OrderBy(l => l, StringComparer.Ordinal))
            {
                text.Append("  ").Append(line).Append('\n');
            }
        }

        return text.ToString();
    }

    private static string KindOf(Type t) =>
        t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : t.IsSealed ? "sealed class" : t.IsAbstract ? "abstract class" : "class";

    private static string? Describe(Type owner, MemberInfo m)
    {
        switch (m)
        {
            case MethodInfo method when !method.IsSpecialName && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly):
                return (method.IsStatic ? "static " : string.Empty) + (method.IsFamily || method.IsFamilyOrAssembly ? "protected " : string.Empty)
                    + (method.IsVirtual && !method.IsFinal && !owner.IsSealed ? "virtual " : string.Empty) + method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
            case ConstructorInfo ctor when ctor.IsPublic || ctor.IsFamily:
                return "ctor(" + string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")";
            case PropertyInfo prop when (prop.GetMethod?.IsPublic ?? false) || (prop.SetMethod?.IsPublic ?? false) || (prop.GetMethod?.IsFamily ?? false):
                return (prop.GetMethod?.IsStatic == true ? "static " : string.Empty) + "property " + prop.PropertyType.Name + " " + prop.Name + " { " + (prop.GetMethod is { IsPublic: true } ? "get; " : prop.GetMethod is not null ? "protected get; " : string.Empty) + (prop.SetMethod is { IsPublic: true } ? "set; " : string.Empty) + "}";
            case FieldInfo field when field.IsPublic:
                return (field.IsStatic ? "static " : string.Empty) + (field.IsLiteral ? "const " : string.Empty) + "field " + field.FieldType.Name + " " + field.Name;
            default:
                return null;
        }
    }
}
