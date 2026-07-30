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

    // ── The assembly authority (a namespace root cannot carry these) ───────────────────────────

    [Test]
    public void NonSdkKey_excludes_base_library_types_the_root_list_no_longer_names()
    {
        // "System" was dropped from the namespace roots so a project's own System.* types stay visible.
        // Only the assembly check can still exclude the real base library, so these isolate that path.
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(int)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(List<int>)));
    }

    [Test]
    public void IsFromSdkAssembly_recognizes_engine_base_library_and_sdk_assemblies()
    {
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(Transform)));
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(int)));
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(VRCAvatarDescriptor)));
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone)));
    }

    [Test]
    public void IsFromSdkAssembly_rejects_our_own_and_test_assemblies()
    {
        // A global-namespace type in a non-SDK assembly must survive to the census, or the assembly check
        // would swallow exactly the optimizers that ship with no namespace.
        Assert.IsFalse(ReportPackage.IsFromSdkAssembly(typeof(ReportPackageTests_GlobalProbe)));
        Assert.IsFalse(ReportPackage.IsFromSdkAssembly(typeof(ReportPackage)));
        Assert.IsFalse(ReportPackage.IsFromSdkAssembly(null));
    }

    // ── ComposeToggleCaveat: named vs unknown vs absent ────────────────────────────────────────

    [Test]
    public void ComposeToggleCaveat_reports_unknown_not_absent_when_only_scripts_are_unresolved()
    {
        var caveat = ReportPackage.ComposeToggleCaveat(0, 5, 0);
        StringAssert.Contains("UNKNOWN, not absent", caveat);
        StringAssert.Contains("5 components have unresolved scripts", caveat);
        Assert.IsFalse(caveat.Contains("No non-SDK framework components were found"),
            "a package with unresolved scripts must never be reported as framework-free");
    }

    [Test]
    public void ComposeToggleCaveat_claims_absence_only_when_the_census_is_empty_and_complete()
    {
        var caveat = ReportPackage.ComposeToggleCaveat(0, 0, 0);
        StringAssert.Contains("No non-SDK framework components were found", caveat);
        StringAssert.Contains("every component's script resolved", caveat);
    }

    [Test]
    public void ComposeToggleCaveat_routes_a_named_framework_to_the_vendor_components()
    {
        var caveat = ReportPackage.ComposeToggleCaveat(3, 0, 0);
        StringAssert.Contains("3 non-SDK namespaces present", caveat);
        StringAssert.Contains("read the vendor's own toggle/menu components", caveat);
        Assert.IsFalse(caveat.Contains("unresolved scripts"), "nothing was unresolved in this case");
    }

    [Test]
    public void ComposeToggleCaveat_flags_incompleteness_alongside_a_named_framework()
    {
        var caveat = ReportPackage.ComposeToggleCaveat(3, 5, 0);
        StringAssert.Contains("3 non-SDK namespaces present", caveat);
        StringAssert.Contains("the namespace list is incomplete", caveat);
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
