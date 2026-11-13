using System;

namespace Spillgate.TestSupport;

/// <summary>
/// Names the specification rules (SG-R-*) and invariants (SG-I-*) a test protects. The spectrace test fails
/// when a rule of spec/SPEC.md is named by no vector and no test.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class CoversAttribute : Attribute
{
    public CoversAttribute(params string[] rules)
    {
        Rules = rules;
    }

    public string[] Rules { get; }
}
