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

// A type SQUATTING an SDK namespace root from a non-SDK assembly — the one shape that observes
// SdkNamespaceRoots at all. Every other probe reaches its verdict through the assembly check, so without
// this the root list could be deleted with the whole file still green, and the deletion would silently be a
// behavior change rather than dead-code removal.
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
    // disagree: a type under an SDK root compiled into an assembly that is NOT the SDK's. Excluding it is the
    // deliberate squatting bet the production docstring argues for — cheap first filter, and no installed
    // package takes such a name. Asserting it keeps the bet a decision rather than an accident: delete the
    // root list and this returns the namespace instead of null, which is a census behavior change.
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
        // "System" was dropped from the namespace roots so a project's own System.* types stay visible.
        // Only the assembly check can still exclude the real base library, so these isolate that path.
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(int)));
        Assert.IsNull(ReportPackage.NonSdkKey(typeof(List<int>)));
    }

    // The type door, both directions, with NonSdkKey held to the same verdict: whatever IsFromSdkAssembly
    // admits must key to null, and whatever it rejects must survive to the census. Asserting the two
    // together is what keeps them from drifting apart — NonSdkKey consults the namespace roots FIRST, so a
    // pair that disagrees is a real defect, not a redundancy.
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
    // A bare "VRC" prefix classified VRCFury as SDK infrastructure — its runtime assembly is named
    // exactly `VRCFury` — so a VRCFury-composed package censused to zero and togglesCaveat went on to
    // claim nothing compiles toggles at build. VRCFury's components are `internal` in a
    // non-auto-referenced assembly, so no typeof() here can witness it; the string door can.

    [Test]
    public void IsSdkAssemblyName_does_not_swallow_VRCFury()
    {
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury"), "the runtime assembly VRCFury components live in");
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury-Editor-Avatars"));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury-Editor-Common"));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("VRCFury-Tests"));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName("com.vrcfury.api"), "lowercase: no prefix reaches it either");

        // Every other framework the census exists to name, held to the same must-survive rule. VRCFury is
        // only the collision that was actually found; a new prefix that swallows any of these is the same bug.
        foreach (var name in new[]
        {
            "nadena.dev.modular-avatar.core", "nadena.dev.ndmf", "nadena.dev.ndmf.vrchat",
            "d4rkpl4y3r.d4rkavataroptimizer.Editor", "dev.limitex.avatar-compressor",
            "dev.vrlabs.vrcsdkplus", "lyuma.av3emulator", "vrchat.blackstartx.gesture-manager",
            "lilToon.Editor", "Poi.Tools", "ThryAssemblyDefinition",
        })
            Assert.IsFalse(ReportPackage.IsSdkAssemblyName(name), "must survive to the census: " + name);

        // No name at all is not an SDK name: a component with an unresolvable assembly must reach
        // unresolvedScripts, never get quietly excluded as infrastructure.
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName(null));
        Assert.IsFalse(ReportPackage.IsSdkAssemblyName(""));
    }

    [Test]
    public void IsSdkAssemblyName_still_reaches_every_real_sdk_assembly()
    {
        // Enumerated from the installed SDK rather than invented, so narrowing the prefixes cannot
        // quietly drop one: com.vrchat.{avatars,base} asmdefs plus their precompiled DLLs.
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

    // Both sides of the truncation boundary, plus the empty floor. FOUR entries is the case that earns its
    // keep: "+1 more" is where an off-by-one lives (three named, one remaining), and nothing else in this
    // file reaches it. The count always LEADS the parenthesis, so truncation can never hide the total.
    [TestCase(0, "0")]
    [TestCase(3, "3(n1, n2, n3)")]
    [TestCase(4, "4(n1, n2, n3, +1 more)")]
    [TestCase(6, "6(n1, n2, n3, +3 more)")]
    public void NonSdkSummary_leads_with_the_count_and_truncates_past_three(int entries, string expected)
    {
        // Inserted least-present first and named so that rank order is n1, n2, n3 … — the ranking has to
        // reorder them, so a summary that echoed insertion order would fail rather than coincide.
        var census = new Dictionary<string, int>();
        for (int i = entries; i >= 1; i--) census["n" + i] = entries - i + 1;

        Assert.AreEqual(expected, ReportPackage.NonSdkSummary(census));
    }

    // ── Viseme mesh: the lipSync mode gate ────────────────────────────────────────────────────

    // The gate exists because VisemeSkinnedMesh is NOT cleared when the mode changes — the inspector only
    // switches which fields it draws. So a descriptor sitting on JawFlapBone can still hold a live pointer to
    // whatever face mesh was chosen before the switch, and reading it there would report a stale guess under a
    // field that claims to be a fact. Both polarities are pinned: widening this set silently re-admits that
    // stale read, and narrowing it silently demotes every JawFlapBlendShape avatar to the guess route.
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

    // The two routes must not emit two different KEYS — a reader would then have to parse both to learn one
    // thing. The basis rides the value instead, the same shape `toggles=` already uses. The unresolved case
    // stays "?" so it reads as absent rather than as a mesh named "unknown".
    [TestCase("Body",  "descriptor",             "Body(descriptor)")]
    [TestCase("Body",  "guess:most-blendshapes", "Body(guess:most-blendshapes)")]
    [TestCase(null,    "descriptor",             "?")]
    public void VisemeSummary_carries_the_basis_beside_the_value(string mesh, string basis, string expected)
    {
        Assert.AreEqual(expected, ReportPackage.VisemeSummary(mesh, basis));
    }

    // A named mesh with no basis is a bug in the caller, not a silent "trust me": the field still says the
    // basis is unknown rather than presenting the name bare, which would read as the descriptor route.
    [Test]
    public void VisemeSummary_never_presents_a_bare_name_as_if_it_were_a_fact()
    {
        Assert.AreEqual("Body(unknown)", ReportPackage.VisemeSummary("Body", null));
    }

    // ── The face-exclusion key for the body pick ──────────────────────────────────────────────
    //
    // The body pick excludes the face mesh, and WHICH key it excludes on has to follow the route that
    // answered — not whether a descriptor exists. The two come apart in a case the resolver's own docstring
    // calls ordinary: a descriptor declares a face mesh this package's FBX inventory does not contain (an
    // outfit or hair package pointing at a base body elsewhere). The run degrades to the guess route, but the
    // descriptor-derived mesh is non-null — so keying the exclusion on its nullity excludes nothing at all
    // (identity matches no renderer, which is precisely why the join failed), and the top renderer becomes
    // both visemeMesh and bodyGuess. The report then names one mesh twice while the docs promise two.
    [TestCase("descriptor",             true)]
    [TestCase("guess:most-blendshapes", false)]
    public void FaceExclusionKey_followsTheRouteThatAnswered(string basis, bool identityKey)
    {
        Assert.AreEqual(identityKey, ReportPackage.ExcludesFaceByIdentity(basis));
    }

    // The degraded-with-a-declared-mesh case named above, stated as its own obligation: a declared but
    // unjoinable mesh must not switch the exclusion to an identity test that matches nothing.
    [Test]
    public void FaceExclusionKey_degradedRouteExcludesByName_evenWhenADescriptorDeclaredAMesh()
    {
        Assert.IsFalse(ReportPackage.ExcludesFaceByIdentity("guess:most-blendshapes"));
    }
}
