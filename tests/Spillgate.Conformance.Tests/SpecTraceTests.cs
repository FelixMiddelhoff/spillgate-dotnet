using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spillgate.TestSupport;
using Xunit;

namespace Spillgate.Conformance.Tests;

/// <summary>
/// Traceability: every rule and invariant of spec/SPEC.md is named by at least one conformance vector or by a
/// test that carries it in a <c>[Covers(...)]</c> attribute, and nothing names a rule the spec does not define.
/// </summary>
public sealed partial class SpecTraceTests
{
    [Fact]
    public void EveryRuleIsCovered()
    {
        SortedSet<string> defined = Repo.DefinedRules();
        Assert.True(defined.Count >= 74, "found only " + defined.Count + " rules in the spec: the extraction is broken");

        var covered = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        using (JsonDocument manifest = Repo.ReadManifest())
        {
            foreach (JsonElement vector in manifest.RootElement.GetProperty("vectors").EnumerateArray())
            {
                foreach (JsonElement r in vector.GetProperty("rules").EnumerateArray())
                {
                    string rule = r.GetString()!;
                    if (!defined.Contains(rule))
                    {
                        unknown.Add(vector.GetProperty("id").GetString() + " names " + rule);
                    }

                    covered.Add(rule);
                }
            }
        }

        foreach (string file in TestSources())
        {
            foreach (string rule in CoveredIn(File.ReadAllText(file)))
            {
                if (!defined.Contains(rule))
                {
                    unknown.Add(Path.GetFileName(file) + " names " + rule);
                }

                covered.Add(rule);
            }
        }

        var missing = new SortedSet<string>(defined, StringComparer.Ordinal);
        missing.ExceptWith(covered);
        Assert.True(unknown.Count == 0, "names a rule the spec does not define: " + string.Join(", ", unknown));
        Assert.True(missing.Count == 0, missing.Count + " rules are not covered: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheManifestIsWellFormed()
    {
        SortedSet<string> rules = Repo.DefinedRules();
        using JsonDocument manifest = Repo.ReadManifest();
        Assert.Equal(1, manifest.RootElement.GetProperty("schema_version").GetInt32());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement vector in manifest.RootElement.GetProperty("vectors").EnumerateArray())
        {
            string id = vector.GetProperty("id").GetString()!;
            Assert.Matches("^SG-V-[A-Z]+-[0-9]{4}$", id);
            Assert.True(seen.Add(id), "duplicate " + id);
            Assert.False(string.IsNullOrEmpty(vector.GetProperty("description").GetString()), id + ": no description");
            Assert.NotEqual(0, vector.GetProperty("rules").GetArrayLength());
            foreach (JsonElement rule in vector.GetProperty("rules").EnumerateArray())
            {
                Assert.True(rules.Contains(rule.GetString()!), id + " names an undefined rule " + rule.GetString());
            }

            Assert.Matches("^[0-9a-f]{64}$", vector.GetProperty("input").GetProperty("sha256").GetString()!);
        }
    }

    [Fact]
    public void ThePinnedCorpusMatchesItsHashFile()
    {
        string[] lines = File.ReadAllLines(Repo.Path("conformance", "CORPUS.sha256"));
        Assert.True(lines.Length > 300);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            int star = line.IndexOf(" *", StringComparison.Ordinal);
            Assert.True(star == 64, "bad line: " + line);
            string hash = line.Substring(0, 64);
            string rel = line.Substring(66);
            listed.Add(rel);
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Repo.Path(rel.Split('/'))))).ToLowerInvariant();
            Assert.True(actual == hash, rel + " differs from the pinned hash");
        }

        // Every file below conformance/vectors and spec/ is pinned: nothing was added by hand.
        foreach (string dir in new[] { "conformance/vectors", "spec" })
        {
            foreach (string file in Directory.EnumerateFiles(Repo.Path(dir.Split('/')), "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(Repo.Root, file).Replace('\\', '/');
                Assert.True(listed.Contains(rel), rel + " is not in CORPUS.sha256");
            }
        }
    }

    private static IEnumerable<string> TestSources()
    {
        foreach (string dir in new[] { "tests" })
        {
            foreach (string file in Directory.EnumerateFiles(Repo.Path(dir), "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (!normalized.Contains("/obj/", StringComparison.Ordinal) && !normalized.Contains("/bin/", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> CoveredIn(string source)
    {
        foreach (Match attribute in CoversPattern().Matches(source))
        {
            foreach (string rule in Repo.RuleIdsIn(attribute.Groups[1].Value))
            {
                yield return rule;
            }
        }
    }

    [GeneratedRegex(@"\[Covers\(([^\]]*)\)\]")]
    private static partial Regex CoversPattern();
}
