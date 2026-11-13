using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Spillgate.TestSupport;

/// <summary>Locates the repository, the pinned corpus and the spec.</summary>
public static partial class Repo
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Root => RootLazy.Value;

    public static string Path(params string[] parts) => System.IO.Path.Combine(Root, System.IO.Path.Combine(parts));

    public static JsonDocument ReadManifest() => JsonDocument.Parse(File.ReadAllBytes(Path("conformance", "manifest.json")));

    /// <summary>The IDs defined by bullets that start with a bold rule ID in the spec.</summary>
    public static SortedSet<string> DefinedRules()
    {
        string spec = File.ReadAllText(Path("spec", "SPEC.md"));
        var rules = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in DefinitionPattern().Matches(spec))
        {
            rules.Add(m.Groups[1].Value);
        }

        return rules;
    }

    /// <summary>All rule IDs written in the text.</summary>
    public static IEnumerable<string> RuleIdsIn(string text)
    {
        foreach (Match m in IdPattern().Matches(text))
        {
            yield return m.Value;
        }
    }

    [GeneratedRegex(@"^- \*\*(SG-[RI]-[A-Z]*-?\d+)\*\*", RegexOptions.Multiline)]
    private static partial Regex DefinitionPattern();

    [GeneratedRegex(@"SG-[RI]-(?:[A-Z]+-)?\d+")]
    private static partial Regex IdPattern();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "conformance", "manifest.json"))
                && File.Exists(System.IO.Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repository root not found");
    }
}
