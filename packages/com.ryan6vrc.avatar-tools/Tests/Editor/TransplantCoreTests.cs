using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// Synthetic Component subclasses for type-name resolution: an ambiguous simple name, a base/derived pair and
// a guaranteed-unique name, which no real VRC type supplies and which would be hostage to the SDK's naming.
// Where SDK identity is under test, resolve through the SDK, as
// VrcTable_physbone_field_paths_are_real_serialized_properties does. TypeCache picks these up after compile.
namespace TransplantCoreTests_NsA
{
    public class TcUniqueProbe : MonoBehaviour { }
    public class TcDupProbe : MonoBehaviour { }
    public class TcBaseProbe : MonoBehaviour { }
    public class TcDerivedProbe : TcBaseProbe { }
}
namespace TransplantCoreTests_NsB
{
    public class TcDupProbe : MonoBehaviour { }
}

public class TransplantCoreTests
{
    // ── ResolveTypes ──────────────────────────────────────────────────────────────────────────

    // The base-type case says something only because TcDerivedProbe is in the scan too: a resolver that
    // matched subclasses would find two candidates and report ambiguity.
    [TestCase("TcUniqueProbe", typeof(TransplantCoreTests_NsA.TcUniqueProbe))]
    [TestCase("TransplantCoreTests_NsA.TcUniqueProbe", typeof(TransplantCoreTests_NsA.TcUniqueProbe))]
    [TestCase("TcBaseProbe", typeof(TransplantCoreTests_NsA.TcBaseProbe))]
    public void ResolveTypes_matches_by_simple_or_full_name(string query, System.Type expected)
    {
        var r = TransplantCore.ResolveTypes(new[] { query });
        Assert.AreEqual(0, r.unresolved.Count);
        Assert.AreEqual(1, r.resolved.Count);
        Assert.AreEqual(expected, r.resolved[0]);
    }

    [Test]
    public void ResolveTypes_unknown_name_is_unresolved()
    {
        var r = TransplantCore.ResolveTypes(new[] { "NoSuchComponentTypeXyz" });
        Assert.AreEqual(0, r.resolved.Count);
        Assert.AreEqual(1, r.unresolved.Count);
    }

    [Test]
    public void ResolveTypes_ambiguous_simple_name_fails_loud()
    {
        var r = TransplantCore.ResolveTypes(new[] { "TcDupProbe" });
        Assert.AreEqual(0, r.resolved.Count);
        Assert.AreEqual(1, r.unresolved.Count);
        StringAssert.Contains("TcDupProbe", r.unresolved[0]);
    }

    [Test]
    public void ResolveTypes_full_name_disambiguates_an_otherwise_ambiguous_name()
    {
        var r = TransplantCore.ResolveTypes(new[] { "TransplantCoreTests_NsB.TcDupProbe" });
        Assert.AreEqual(0, r.unresolved.Count);
        Assert.AreEqual(1, r.resolved.Count);
        Assert.AreEqual(typeof(TransplantCoreTests_NsB.TcDupProbe), r.resolved[0]);
    }

    // ── CopyAssetFile ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void CopyAssetFile_Saves_Dirty_Source_But_Not_Unrelated_Dirty_Assets()
    {
        const string root = "Assets/Agent/Scratch/TransplantCoreCopyTests";
        AnimatorTestHelpers.EnsureFolder(root);
        try
        {
            string sourcePath = root + "/Source.mat";
            string destinationPath = root + "/Destination.mat";
            var source = new Material(Shader.Find("Standard"));
            source.SetFloat("_Glossiness", 0.1f);
            AssetDatabase.CreateAsset(source, sourcePath);
            AssetDatabase.SaveAssetIfDirty(source);
            var unrelated = new AnimatorTestHelpers.DirtyMaterialProbe(root, "Unrelated");

            source.SetFloat("_Glossiness", 0.4f);
            EditorUtility.SetDirty(source);
            Assert.IsTrue(TransplantCore.CopyAssetFile(sourcePath, destinationPath));

            var copied = AssetDatabase.LoadAssetAtPath<Material>(destinationPath);
            Assert.IsNotNull(copied);
            Assert.AreEqual(0.4f, copied.GetFloat("_Glossiness"), 1e-6f,
                "the copy reflects the source's dirty in-memory state");
            unrelated.AssertWasNotSaved();
        }
        finally
        {
            AssetDatabase.DeleteAsset(root);
        }
    }

    [Test]
    public void CopyAssetFile_Refuses_A_Package_Destination()
    {
        const string root = "Assets/Agent/Scratch/TransplantCoreCopyTests";
        const string destinationPath =
            "Packages/com.ryan6vrc.avatar-tools/Tests/__CopyAssetFileRefusal.mat";
        AnimatorTestHelpers.EnsureFolder(root);
        try
        {
            string sourcePath = root + "/Source.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Standard")), sourcePath);
            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(destinationPath), "refusal fixture starts absent");

            Assert.IsFalse(TransplantCore.CopyAssetFile(sourcePath, destinationPath));

            Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(destinationPath),
                "an immutable package destination remains untouched");
        }
        finally
        {
            AssetDatabase.DeleteAsset(root);
        }
    }

    // ── VrcComponentTable (resolved through ResolveTypes, matching the transplant engine's own lookup) ──

    // The table's field paths are SerializedProperty names on the live SDK type, and the engine reports a
    // renamed one as table drift only when a live copy or move reaches it. So each path is resolved here
    // against a real component's SerializedObject, as the engine consumes it. FindProperty, not
    // Type.GetField, because the serialized name is the contract.
    [Test]
    public void VrcTable_physbone_field_paths_are_real_serialized_properties()
    {
        var r = TransplantCore.ResolveTypes(new[] { "VRCPhysBone" });
        Assert.AreEqual(1, r.resolved.Count, "VRCPhysBone should resolve (VRC SDK present)");
        var d = VrcComponentTable.Lookup(r.resolved[0]);
        Assert.IsNotNull(d);

        // Which bucket each path sits in is the row's own judgment, so that stays asserted by name.
        CollectionAssert.Contains(d.hardDepFieldPaths, "colliders");
        CollectionAssert.Contains(d.softDepFieldPaths, "ignoreTransforms");
        CollectionAssert.Contains(d.anchorFieldPaths, "rootTransform");
        Assert.IsFalse(d.leafRecreateEligible);

        var go = new GameObject("PbFieldPathProbe");
        try
        {
            var so = new UnityEditor.SerializedObject(go.AddComponent(r.resolved[0]));
            foreach (var path in d.anchorFieldPaths)
                Assert.IsNotNull(so.FindProperty(path), "anchor path no longer a serialized property: " + path);
            foreach (var path in d.hardDepFieldPaths)
                Assert.IsNotNull(so.FindProperty(path), "hard-dep path no longer a serialized property: " + path);
            foreach (var path in d.softDepFieldPaths)
                Assert.IsNotNull(so.FindProperty(path), "soft-dep path no longer a serialized property: " + path);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void VrcTable_lookup_walks_base_chain_to_collider_base()
    {
        var r = TransplantCore.ResolveTypes(new[] { "VRCPhysBoneCollider" });
        Assert.AreEqual(1, r.resolved.Count, "VRCPhysBoneCollider should resolve (VRC SDK present)");
        var d = VrcComponentTable.Lookup(r.resolved[0]);
        Assert.IsNotNull(d);
        Assert.IsTrue(d.leafRecreateEligible);
        CollectionAssert.Contains(d.anchorFieldPaths, "rootTransform");
    }

    [Test]
    public void VrcTable_lookup_returns_null_for_non_vrc_type()
    {
        Assert.IsNull(VrcComponentTable.Lookup(typeof(TransplantCoreTests_NsA.TcUniqueProbe)));
    }

    // ── ScaffoldBuilder.EnsureHost ──────────────────────────────────────────────────────────────

    [Test]
    public void EnsureHost_builds_depth_n_chain_with_verbatim_local_trs_and_reuses_by_path()
    {
        var vroot = new GameObject("V").transform;
        var a = new GameObject("A").transform; a.SetParent(vroot);
        var b = new GameObject("B").transform; b.SetParent(a);
        b.localPosition = new Vector3(1, 2, 3);
        b.localRotation = Quaternion.Euler(10, 20, 30);
        b.localScale = new Vector3(2, 2, 2);
        var host = new GameObject("Host").transform; host.SetParent(b);
        host.localPosition = new Vector3(0, 0.5f, 0);

        var droot = new GameObject("D").transform;
        var session = new SessionMap();

        var h1 = ScaffoldBuilder.EnsureHost(vroot, droot, host, session, "T");
        Assert.IsNotNull(h1);
        Assert.AreEqual("Host", h1.name);
        Assert.AreEqual("B", h1.parent.name);
        Assert.AreEqual("A", h1.parent.parent.name);
        Assert.AreSame(droot, h1.parent.parent.parent);
        Assert.AreEqual(new Vector3(1, 2, 3), h1.parent.localPosition);
        Assert.AreEqual(new Vector3(2, 2, 2), h1.parent.localScale);
        Assert.AreEqual(new Vector3(0, 0.5f, 0), h1.localPosition);
        Assert.IsTrue(session.TryGetTransform(host, out var mappedHost) && mappedHost == h1);

        var h2 = ScaffoldBuilder.EnsureHost(vroot, droot, host, session, "T");
        Assert.AreSame(h1, h2);
        int aCount = 0;
        for (int i = 0; i < droot.childCount; i++) if (droot.GetChild(i).name == "A") aCount++;
        Assert.AreEqual(1, aCount);

        Object.DestroyImmediate(vroot.gameObject);
        Object.DestroyImmediate(droot.gameObject);
    }

    [Test]
    public void EnsureHost_returns_dst_root_when_vendor_host_is_vendor_root()
    {
        var vroot = new GameObject("V").transform;
        var droot = new GameObject("D").transform;
        Assert.AreSame(droot, ScaffoldBuilder.EnsureHost(vroot, droot, vroot, null, "T"));
        Object.DestroyImmediate(vroot.gameObject);
        Object.DestroyImmediate(droot.gameObject);
    }

    // ── EnsureHost renameMap (armature-root reconcile) ────────────────────────────────────────────

    static (Transform vroot, Transform chest) VendorArmatureChest()
    {
        var vroot = new GameObject("V").transform;
        var arm   = new GameObject("Armature").transform; arm.SetParent(vroot);
        var hips  = new GameObject("Hips").transform;     hips.SetParent(arm);
        var chest = new GameObject("Chest").transform;    chest.SetParent(hips);
        chest.localPosition = new Vector3(1, 2, 3);
        chest.localScale    = new Vector3(2, 2, 2);
        return (vroot, chest);
    }

    [Test]
    public void EnsureHost_with_rename_reuses_owned_armature_then_mints_children_idempotently()
    {
        var (vroot, chest) = VendorArmatureChest();
        var droot = new GameObject("D").transform;
        new GameObject("Armature.1").transform.SetParent(droot);   // owned armature already present
        var map = new Dictionary<string, string> { { "Armature", "Armature.1" } };

        var h1 = ScaffoldBuilder.EnsureHost(vroot, droot, chest, out var fr, null, "T", map);
        Assert.IsNull(fr);
        Assert.IsNotNull(h1);
        Assert.AreEqual("Chest", h1.name);
        Assert.AreEqual("Hips", h1.parent.name);
        Assert.AreEqual("Armature.1", h1.parent.parent.name, "reused the owned renamed armature");
        Assert.AreSame(droot, h1.parent.parent.parent);
        Assert.AreEqual(new Vector3(1, 2, 3), h1.localPosition, "verbatim vendor local TRS");
        Assert.AreEqual(new Vector3(2, 2, 2), h1.localScale);

        var h2 = ScaffoldBuilder.EnsureHost(vroot, droot, chest, out _, null, "T", map);
        Assert.AreSame(h1, h2);
        int armDot = 0, armPlain = 0;
        for (int i = 0; i < droot.childCount; i++)
        {
            var n = droot.GetChild(i).name;
            if (n == "Armature.1") armDot++;
            if (n == "Armature") armPlain++;
        }
        Assert.AreEqual(1, armDot);
        Assert.AreEqual(0, armPlain, "no parallel 'Armature' minted under a rename");

        Object.DestroyImmediate(vroot.gameObject);
        Object.DestroyImmediate(droot.gameObject);
    }

    [Test]
    public void EnsureHost_null_map_mints_parallel_armature()
    {
        var (vroot, chest) = VendorArmatureChest();
        var droot = new GameObject("D").transform;
        new GameObject("Armature.1").transform.SetParent(droot);

        // With no map the vendor segment 'Armature' has no counterpart, so a parallel 'Armature' is minted:
        // the map is the only thing that changes this.
        var h = ScaffoldBuilder.EnsureHost(vroot, droot, chest, out _, null, "T", null);
        Assert.IsNotNull(h);
        Assert.AreEqual("Armature", h.parent.parent.name, "minted the vendor name under a null map");
        Assert.IsNotNull(droot.Find("Armature.1"), "the pre-existing owned armature is untouched");

        Object.DestroyImmediate(vroot.gameObject);
        Object.DestroyImmediate(droot.gameObject);
    }

    [Test]
    public void EnsureHost_rename_funnel_binds_1to1_across_siblings_no_orphan()
    {
        // The mapped key and the literal sibling both resolve to "Armature", so the occurrence index must
        // be counted in the same resolving-to space as the dest lookup for them to bind 1:1, not collapse
        // onto #0 and orphan #1.
        var vroot = new GameObject("V").transform;
        var vp = new GameObject("P").transform; vp.SetParent(vroot);
        var vOther = new GameObject("Other").transform; vOther.SetParent(vp);
        var vArm = new GameObject("Armature").transform; vArm.SetParent(vp);

        var droot = new GameObject("D").transform;
        var dp = new GameObject("P").transform; dp.SetParent(droot);
        var dA0 = new GameObject("Armature").transform; dA0.SetParent(dp);
        var dA1 = new GameObject("Armature").transform; dA1.SetParent(dp);
        var map = new Dictionary<string, string> { { "Other", "Armature" } };

        var eOther = ScaffoldBuilder.EnsureHost(vroot, droot, vOther, out var f1, null, "T", map);
        var eArm   = ScaffoldBuilder.EnsureHost(vroot, droot, vArm,   out var f2, null, "T", map);
        Assert.IsNull(f1); Assert.IsNull(f2);
        Assert.AreSame(dA0, eOther, "mapped 'Other' → dest Armature#0");
        Assert.AreSame(dA1, eArm,   "literal 'Armature' → dest Armature#1 (1:1, no orphan/mis-bind)");

        Object.DestroyImmediate(vroot.gameObject);
        Object.DestroyImmediate(droot.gameObject);
    }

    [Test]
    public void EnsureHost_rename_funnel_fails_loud_when_dest_count_differs()
    {
        // Two source siblings resolve to "Armature" and the dest has one, so the occurrence index cannot
        // address a unique dest sibling. The mirrored shape (source 1, dest 2) needs no fixture: the guard
        // is a `srcResolving != dstCount` inequality, so swapped operands exercise the identical branch.
        var vroot = new GameObject("V").transform;
        var vp = new GameObject("P").transform; vp.SetParent(vroot);
        var vOther = new GameObject("Other").transform; vOther.SetParent(vp);
        new GameObject("Armature").transform.SetParent(vp);

        var droot = new GameObject("D").transform;
        var dp = new GameObject("P").transform; dp.SetParent(droot);
        new GameObject("Armature").transform.SetParent(dp);
        var map = new Dictionary<string, string> { { "Other", "Armature" } };

        var e = ScaffoldBuilder.EnsureHost(vroot, droot, vOther, out var fr, null, "T", map);
        Assert.IsNull(e);
        Assert.IsNotNull(fr);
        StringAssert.Contains("ambiguous rename", fr);

        Object.DestroyImmediate(vroot.gameObject);
        Object.DestroyImmediate(droot.gameObject);
    }

    // ── EnsureFailHasOffender (offenders⇔FAIL reverse-leg guard) ──────────────────────────────────

    [TestCase("FAIL", "NullReferenceException: boom", null, 1, "NullReferenceException: boom")]
    [TestCase("FAIL", null, null, 1, "no error detail")]
    [TestCase("FAIL", "an error AND an offender", "real named offender", 1, "real named offender")]
    [TestCase("PASS", null, null, 0, null)]
    public void EnsureFailHasOffender_backfills_only_an_offenderless_fail(
        string result, string error, string preNamed, int expectedCount, string expectedFragment)
    {
        var log = new RunLog("test") { result = result, error = error };
        if (preNamed != null) log.Offender(preNamed);

        log.EnsureFailHasOffender();

        Assert.AreEqual(expectedCount, log.offenders.Count);
        if (expectedFragment != null) StringAssert.Contains(expectedFragment, log.offenders[0]);
    }

    // ── WriteRunLog sections (the envelope's custom-section hook) ─────────────────────────────

    [Test]
    public void WriteRunLog_without_sections_ends_at_warnings()
    {
        var log = new RunLog("tc-test");
        log.Count("n", 1);
        string path = TransplantCore.WriteRunLog(log, "no-sections");
        try
        {
            string json = System.IO.File.ReadAllText(path);
            StringAssert.Contains("\"warnings\": []\n}", json);
        }
        finally { UnityEditor.AssetDatabase.DeleteAsset(path); }
    }

    [Test]
    public void WriteRunLog_emits_sections_verbatim_after_warnings_in_order()
    {
        var log = new RunLog("tc-test");
        log.Warning("w1");
        log.Section("rows", "[\n    { \"a\": 1 }\n  ]");
        log.Section("extra", "[]");
        string path = TransplantCore.WriteRunLog(log, "with-sections");
        try
        {
            string json = System.IO.File.ReadAllText(path);
            StringAssert.Contains(",\n  \"rows\": [\n    { \"a\": 1 }\n  ],\n  \"extra\": []\n}", json);
            Assert.Less(json.IndexOf("\"warnings\""), json.IndexOf("\"rows\""),
                "sections must follow the warnings array");
        }
        finally { UnityEditor.AssetDatabase.DeleteAsset(path); }
    }

    [Test]
    public void Subclassed_runlog_flows_through_finish_with_its_section()
    {
        var log = new SectionedProbeLog();
        log.Section("probe", "[]");
        string summary = TransplantCore.Finish(log, "probe-label");
        StringAssert.Contains("[tc-probe] probe-label", summary);
        int i = summary.IndexOf("log=");
        Assert.GreaterOrEqual(i, 0, "summary missing 'log=' trailer: " + summary);
        string path = summary.Substring(i + 4);
        try
        {
            string json = System.IO.File.ReadAllText(path);
            StringAssert.Contains("\"probe\": []", json);
        }
        finally { UnityEditor.AssetDatabase.DeleteAsset(path); }
    }

    sealed class SectionedProbeLog : RunLog
    {
        public SectionedProbeLog() : base("tc-probe") { }
    }
}
