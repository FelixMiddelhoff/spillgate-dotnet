using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Spillgate.TestSupport;

/// <summary>Dumps the public surface of an assembly as text and compares it with the approved file in api/.</summary>
public static class ApiDump
{
    private static readonly string CarriageReturn = new((char)13, 1);
    private static readonly string LineFeed = new((char)10, 1);

    /// <summary>Fails with a message if the public API of the assembly differs from api/&lt;name&gt;.txt.</summary>
    public static string Approve(string name, Assembly assembly)
    {
        string actual = Dump(assembly);
        string path = Repo.Path("api", name + ".txt");
        if (Environment.GetEnvironmentVariable("SPILLGATE_UPDATE_API") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("api/" + name + ".txt is missing; run with SPILLGATE_UPDATE_API=1");
        }

        string approved = File.ReadAllText(path).Replace(CarriageReturn + LineFeed, LineFeed, StringComparison.Ordinal);
        return approved == actual ? string.Empty : "the public API of " + name + " changed:" + LineFeed + "--- approved" + LineFeed + approved + LineFeed + "--- actual" + LineFeed + actual;
    }

    public static string Dump(Assembly assembly)
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
