using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// Unit tests for ReportPackage's non-SDK component census — the generic replacement for a fixed
// three-framework detection list, which reported one framework on an avatar carrying three.
// NonSdkKey reads only Type.Namespace/Type.Name, so plain probe classes exercise it exactly as
// components do; the real Unity/VRC typeof() references cover the infrastructure side and double as
// rename canaries against the pinned SDK. Headless via tools/run-editmode-tests.ps1.

namespace ReportPackageTests_Framework.Core.Deep
{
    public class DeeplyNamespacedProbe { }
}

// A root that merely starts with an SDK root's text. The predicate must split on '.' rather than
// prefix-match, or every vendor namespace beginning "Unity…" or "VRC…" silently vanishes.
namespace UnityEngineExtras
{
    public class NearMissRootProbe { }
}

public class ReportPackageTests_GlobalProbe { }

public class ReportPackageTests
{
    // ── NonSdkKey ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void NonSdkKey_excludes_unity_builtin_components()
    {
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(Transform)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(SkinnedMeshRenderer)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(UnityEngine.Animations.RotationConstraint)));
    }

    [Test]
    public void NonSdkKey_excludes_vrchat_sdk_components()
    {
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(VRCAvatarDescriptor)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone)));
    }

    [Test]
    public void NonSdkKey_reports_unknown_namespace_verbatim_without_collapsing()
    {
        Assert.AreEqual("ReportPackageTests_Framework.Core.Deep",
            ReportPackage.NonSdkKey(typeof(ReportPackageTests_Framework.Core.Deep.DeeplyNamespacedProbe)));
    }

    [Test]
    public void NonSdkKey_splits_root_on_dot_so_a_near_miss_root_is_not_excluded()
    {
        Assert.AreEqual("UnityEngineExtras",
            ReportPackage.NonSdkKey(typeof(UnityEngineExtras.NearMissRootProbe)));
    }

    [Test]
    public void NonSdkKey_keys_global_namespace_types_on_their_type_name()
    {
        // Their namespace carries nothing to report, and the most-deployed avatar optimizers ship this way.
        Assert.AreEqual("ReportPackageTests_GlobalProbe",
            ReportPackage.NonSdkKey(typeof(ReportPackageTests_GlobalProbe)));
    }

    [Test]
    public void NonSdkKey_returns_null_for_a_null_type()
    {
        Assert.IsNull(ReportPackage.NonSdkKey(null));
    }

    // ── RankNonSdk / NonSdkSummary ────────────────────────────────────────────────────────────

    private static Dictionary<string, int> Census(params KeyValuePair<string, int>[] entries)
    {
        var d = new Dictionary<string, int>();
        foreach (var e in entries) d[e.Key] = e.Value;
        return d;
    }

    private static KeyValuePair<string, int> E(string ns, int n)
    {
        return new KeyValuePair<string, int>(ns, n);
    }

    [Test]
    public void RankNonSdk_orders_by_component_count_then_name()
    {
        var ranked = ReportPackage.RankNonSdk(Census(E("b.two", 2), E("a.five", 5), E("a.two", 2)));
        Assert.AreEqual(new[] { "a.five", "a.two", "b.two" },
            new[] { ranked[0].Key, ranked[1].Key, ranked[2].Key });
    }

    [Test]
    public void NonSdkSummary_is_bare_zero_for_an_empty_census()
    {
        Assert.AreEqual("0", ReportPackage.NonSdkSummary(Census()));
    }

    [Test]
    public void NonSdkSummary_names_every_namespace_up_to_three()
    {
        Assert.AreEqual("3(a, b, c)",
            ReportPackage.NonSdkSummary(Census(E("c", 1), E("b", 2), E("a", 3))));
    }

    [Test]
    public void NonSdkSummary_truncates_past_three_and_counts_the_remainder()
    {
        var census = Census(E("n1", 9), E("n2", 8), E("n3", 7), E("n4", 6), E("n5", 5), E("n6", 4));
        Assert.AreEqual("6(n1, n2, n3, +3 more)", ReportPackage.NonSdkSummary(census));
    }

    [Test]
    public void NonSdkSummary_leads_with_the_count_so_truncation_never_hides_the_total()
    {
        var census = Census(E("only", 1));
        Assert.AreEqual("1(only)", ReportPackage.NonSdkSummary(census));
    }
}
