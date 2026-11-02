using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

// NonSdkKey reads only the Type (namespace, name, assembly), so plain probe classes exercise it exactly as
// components do; the real Unity/VRC typeof() references cover the infrastructure side and double as
// rename canaries against the pinned SDK.

namespace ReportPackageTests_Framework.Core.Deep
{
    public class DeeplyNamespacedProbe { }
}

// Prefix-matching the root instead of splitting on '.' would hide every vendor namespace beginning
// "Unity…" or "VRC…".
namespace UnityEngineExtras
{
    public class NearMissRootProbe { }
}

namespace VRC.ReportPackageTests_SquatProbe
{
    public class SquattingRootProbe { }
}

public class ReportPackageTests_GlobalProbe { }

public class ReportPackageTests
{
    // ── NonSdkKey ─────────────────────────────────────────────────────────────────────────────

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

    // The namespace-root check runs BEFORE the assembly check, and this is the only case where the two
    // disagree: a type under an SDK root in an assembly that is NOT the SDK's. Excluding it is deliberate
    // (SdkNamespaceRoots). Every other test stays green with the root list deleted; this one returns the
    // namespace instead of null.
    [Test]
    public void NonSdkKey_excludes_a_squatted_sdk_root_even_from_a_non_sdk_assembly()
    {
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(VRC.ReportPackageTests_SquatProbe.SquattingRootProbe)));
    }

    [Test]
    public void NonSdkKey_keys_global_namespace_types_on_their_type_name()
    {
        // Their namespace carries nothing to report, and the most-deployed avatar optimizers ship this way.
        Assert.AreEqual("ReportPackageTests_GlobalProbe",
            ReportPackage.NonSdkKey(typeof(ReportPackageTests_GlobalProbe)));
    }

    // ── The assembly authority (a namespace root cannot carry these) ───────────────────────────

    [Test]
    public void NonSdkKey_excludes_base_library_types_the_root_list_no_longer_names()
    {
        // "System" is not a namespace root, so a project's own System.* types stay visible, and only the
        // assembly check can exclude the real base library.
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(int)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(List<int>)));
    }

    [Test]
    public void IsFromSdkAssembly_admits_engine_and_sdk_rejects_ours_and_NonSdkKey_agrees()
    {
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(Transform)));
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(int)));
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(VRCAvatarDescriptor)));
        Assert.IsTrue(ReportPackage.IsFromSdkAssembly(typeof(VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone)));

        Assert.IsNull(ReportPackage.NonSdkKey(typeof(Transform)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(SkinnedMeshRenderer)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(UnityEngine.Animations.RotationConstraint)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(VRCAvatarDescriptor)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone)));

        // A global-namespace type in a non-SDK assembly must survive to the census, or the assembly check
        // would swallow exactly the optimizers that ship with no namespace.
        Assert.IsFalse(ReportPackage.IsFromSdkAssembly(typeof(ReportPackageTests_GlobalProbe)));
        Assert.IsFalse(ReportPackage.IsFromSdkAssembly(typeof(ReportPackage)));
        Assert.IsFalse(ReportPackage.IsFromSdkAssembly(null));
    }

    // ── Assembly NAMES: the near-miss the type-level tests above structurally cannot reach ─────
    // VRCFury's runtime assembly is named exactly `VRCFury`, so a bare "VRC" prefix would class it as SDK
    // infrastructure and a VRCFury-composed package would census to zero. Its components are `internal` in a
    // non-auto-referenced assembly, so no typeof() here can witness it; the string door can.

    [Test]
    public void IsSdkAssemblyName_does_not_swallow_VRCFury()
    {
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury"), "the runtime assembly VRCFury components live in");
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury-Editor-Avatars"));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury-Editor-Common"));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury-Tests"));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("com.vrcfury.api"), "lowercase: no prefix reaches it either");

        foreach (var name in new[]
        {
            "nadena.dev.modular-avatar.core", "nadena.dev.ndmf", "nadena.dev.ndmf.vrchat",
            "d4rkpl4y3r.d4rkavataroptimizer.Editor", "dev.limitex.avatar-compressor",
            "dev.vrlabs.vrcsdkplus", "lyuma.av3emulator", "vrchat.blackstartx.gesture-manager",
            "lilToon.Editor", "Poi.Tools", "ThryAssemblyDefinition",
        })
            Assert.IsFalse(ReportPackage.IsSdkAssemblyName(name), "must survive to the census: " + name);

        Assert.IsFalse(ReportPackage.IsSdkAssemblyName(null));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName(""));
    }

    [Test]
    public void IsSdkAssemblyName_still_reaches_every_real_sdk_assembly()
    {
        // The VRC entries are enumerated from the com.vrchat.{avatars,base} asmdefs and precompiled DLLs, so
        // narrowing the prefixes cannot quietly drop one.
        foreach (var name in new[]
        {
            "VRC.SDK3A", "VRC.SDK3A.Editor", "VRC.SDKBase", "VRC.SDKBase.Editor",
            "VRC.SDKBase.Editor.BuildPipeline", "VRC.ExampleCentral.Editor", "VRC.Dynamics",
            "VRC.SDK3.Dynamics.PhysBone", "VRC.SDK3.Dynamics.Contact", "VRC.SDK3.Dynamics.Constraint",
            "VRC.Utility", "VRCSDK3A", "VRCSDK3A-Editor", "VRCSDKBase", "VRCSDKBase-Editor",
            "VRCCore-Standalone", "VRCCore-Editor", "SDKBase-Legacy",
            "UnityEngine", "UnityEngine.CoreModule", "UnityEditor", "Unity.TextMeshPro",
            "mscorlib", "netstandard", "System", "System.Core", "UniTask", "UniTask.Linq", "DOTween",
        })
            Assert.IsTrue(ReportPackage.IsSdkAssemblyName(name), "must still read as SDK: " + name);
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

    [TestCase(0, "0")]
    [TestCase(3, "3(n1, n2, n3)")]
    [TestCase(4, "4(n1, n2, n3, +1 more)")]
    [TestCase(6, "6(n1, n2, n3, +3 more)")]
    public void NonSdkSummary_leads_with_the_count_and_truncates_past_three(int entries, string expected)
    {
        // Inserted least-present first and named so that rank order is n1, n2, n3 …, so a summary that echoed
        // insertion order would fail rather than coincide.
        var census = new Dictionary<string, int>();
        for (int i = entries; i >= 1; i--) census["n" + i] = entries - i + 1;

        Assert.AreEqual(expected, ReportPackage.NonSdkSummary(census));
    }

    // ── Viseme mesh: the lipSync mode gate ────────────────────────────────────────────────────

    [TestCase(VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape,   true)]
    [TestCase(VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape,  true)]
    [TestCase(VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBone,        false)]
    [TestCase(VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeParameterOnly, false)]
    [TestCase(VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.Default,           false)]
    public void DeclaresFaceMesh_admits_only_the_modes_that_draw_a_face_mesh(
        VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle style, bool expected)
    {
        Assert.AreEqual(expected, ReportPackage.DeclaresFaceMesh(style));
    }

    // ── Viseme mesh: one key, basis in parens ─────────────────────────────────────────────────

    // One key for both routes, with the basis in the value. The unresolved case stays "?" so it reads as absent
    // rather than as a mesh named "unknown".
    [TestCase("Body",  "descriptor",             "Body(descriptor)")]
    [TestCase("Body",  "guess:most-blendshapes", "Body(guess:most-blendshapes)")]
    [TestCase(null,    "descriptor",             "?")]
    public void VisemeSummary_carries_the_basis_beside_the_value(string mesh, string basis, string expected)
    {
        Assert.AreEqual(expected, ReportPackage.VisemeSummary(mesh, basis));
    }

    // A bare name would read as the descriptor route.
    [Test]
    public void VisemeSummary_never_presents_a_bare_name_as_if_it_were_a_fact()
    {
        Assert.AreEqual("Body(unknown)", ReportPackage.VisemeSummary("Body", null));
    }

    // ── The face-exclusion key for the body pick ──────────────────────────────────────────────
    //
    // The key follows the route that answered, not whether a descriptor exists. A descriptor can declare a face
    // mesh this package's FBX inventory lacks (an outfit or hair package pointing at a base body elsewhere): the
    // run degrades to the guess route, an identity test matches no renderer, and the top renderer becomes both
    // visemeMesh and bodyGuess.
    [TestCase("descriptor",             true)]
    [TestCase("guess:most-blendshapes", false)]
    public void FaceExclusionKey_followsTheRouteThatAnswered(string basis, bool identityKey)
    {
        Assert.AreEqual(identityKey, ReportPackage.ExcludesFaceByIdentity(basis));
    }

    [Test]
    public void FaceExclusionKey_degradedRouteExcludesByName_evenWhenADescriptorDeclaredAMesh()
    {
        Assert.IsFalse(ReportPackage.ExcludesFaceByIdentity("guess:most-blendshapes"));
    }
}
