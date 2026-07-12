using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// Synthetic Component subclasses used to exercise type-name resolution without touching VRC types
// (the test assembly does not reference the VRC SDK). TypeCache picks these up after compile.
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

    [Test]
    public void ResolveTypes_matches_by_simple_name()
    {
        var r = TransplantCore.ResolveTypes(new[] { "TcUniqueProbe" });
        Assert.AreEqual(0, r.unresolved.Count);
        Assert.AreEqual(1, r.resolved.Count);
        Assert.AreEqual(typeof(TransplantCoreTests_NsA.TcUniqueProbe), r.resolved[0]);
    }

    [Test]
    public void ResolveTypes_matches_by_full_name()
    {
        var r = TransplantCore.ResolveTypes(new[] { "TransplantCoreTests_NsA.TcUniqueProbe" });
        Assert.AreEqual(0, r.unresolved.Count);
        Assert.AreEqual(1, r.resolved.Count);
        Assert.AreEqual(typeof(TransplantCoreTests_NsA.TcUniqueProbe), r.resolved[0]);
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
        // Same simple Name in two namespaces, both derive from Component → ambiguous → unresolved.
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

    [Test]
    public void ResolveTypes_base_name_resolves_to_base_type_for_assignability_selection()
    {
        // A base-type name resolves to the base type; scene selection is later done by
        // assignability, so the concrete subclass is caught.
        var r = TransplantCore.ResolveTypes(new[] { "TcBaseProbe" });
        Assert.AreEqual(0, r.unresolved.Count);
        Assert.AreEqual(typeof(TransplantCoreTests_NsA.TcBaseProbe), r.resolved[0]);
        Assert.IsTrue(r.resolved[0].IsAssignableFrom(typeof(TransplantCoreTests_NsA.TcDerivedProbe)));
    }

    // ── VrcComponentTable (resolved through ResolveTypes to avoid a compile-time VRC reference) ──

    [Test]
    public void VrcTable_physbone_has_collider_hard_and_ignore_soft()
    {
        var r = TransplantCore.ResolveTypes(new[] { "VRCPhysBone" });
        Assert.AreEqual(1, r.resolved.Count, "VRCPhysBone should resolve (VRC SDK present)");
        var d = VrcComponentTable.Lookup(r.resolved[0]);
        Assert.IsNotNull(d);
        CollectionAssert.Contains(d.hardDepFieldPaths, "colliders");
        CollectionAssert.Contains(d.softDepFieldPaths, "ignoreTransforms");
        CollectionAssert.Contains(d.anchorFieldPaths, "rootTransform");
        Assert.IsFalse(d.leafRecreateEligible);
    }

    [Test]
    public void VrcTable_lookup_walks_base_chain_to_collider_base()
    {
        // Concrete VRCPhysBoneCollider has no row of its own; lookup walks up to
        // VRCPhysBoneColliderBase (leaf-recreate eligible).
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
        Assert.AreEqual(new Vector3(1, 2, 3), h1.parent.localPosition);   // verbatim vendor local TRS
        Assert.AreEqual(new Vector3(2, 2, 2), h1.parent.localScale);
        Assert.AreEqual(new Vector3(0, 0.5f, 0), h1.localPosition);
        Assert.IsTrue(session.TryGetTransform(host, out var mappedHost) && mappedHost == h1);

        // Re-run is idempotent: reuse-by-path, no duplicate GOs.
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

    // Builds vendor V/Armature/Hips/Chest (Chest = host) with verbatim TRS on Chest.
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

        // Re-run reuses all — never mints a parallel 'Armature'.
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
    public void EnsureHost_null_map_mints_parallel_armature_exactly_as_today()
    {
        var (vroot, chest) = VendorArmatureChest();
        var droot = new GameObject("D").transform;
        new GameObject("Armature.1").transform.SetParent(droot);

        // Null map → the vendor segment 'Armature' has no counterpart, so a parallel 'Armature' is minted
        // (today's behavior), proving the map is the only thing that changes behavior.
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
        // source P/[Other, Armature], map {Other ⇒ Armature}, dest P/[Armature#0, Armature#1]. The mapped key
        // AND the literal sibling both resolve to "Armature"; the occurrence index must be counted in the SAME
        // resolving-to space as the dest lookup so they bind 1:1 (Other→#0, Armature→#1), never collapsing onto
        // #0 and orphaning #1.
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
        // Same source/map but dest has ONE Armature → 2 source siblings resolve to "Armature", dest has 1 →
        // the occurrence index cannot address a unique dest sibling → named FAIL (not a silent mis-bind).
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

    [Test]
    public void EnsureHost_A1_guard_returns_null_and_names_count_mismatch()
    {
        // Source has 1 'Armature'; dest has 2 'Armature.1' → the occurrence index is unambiguous on the
        // source but not on the mapped dest → return null with a named reason, never a wrong-sibling bind.
        var vroot = new GameObject("V").transform;
        var arm   = new GameObject("Armature").transform; arm.SetParent(vroot);
        var hips  = new GameObject("Hips").transform;     hips.SetParent(arm);

        var droot = new GameObject("D").transform;
        new GameObject("Armature.1").transform.SetParent(droot);
        new GameObject("Armature.1").transform.SetParent(droot);   // two → count mismatch
        var map = new Dictionary<string, string> { { "Armature", "Armature.1" } };

        var h = ScaffoldBuilder.EnsureHost(vroot, droot, hips, out var fr, null, "T", map);
        Assert.IsNull(h);
        Assert.IsNotNull(fr);
        StringAssert.Contains("ambiguous rename", fr);

        Object.DestroyImmediate(vroot.gameObject);
        Object.DestroyImmediate(droot.gameObject);
    }

    // ── SessionMap ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void SessionMap_stores_and_returns_transform_mappings()
    {
        var src = new GameObject("s").transform;
        var dst = new GameObject("d").transform;
        var m = new SessionMap();
        m.AddTransform(src, dst);
        Assert.IsTrue(m.TryGetTransform(src, out var got));
        Assert.AreSame(dst, got);
        Assert.IsFalse(m.TryGetTransform(dst, out _));
        Object.DestroyImmediate(src.gameObject);
        Object.DestroyImmediate(dst.gameObject);
    }

    // ── EnsureFailHasOffender (offenders⇔FAIL reverse-leg guard) ──────────────────────────────────

    [Test]
    public void EnsureFailHasOffender_backfills_from_error_on_offenderless_fail()
    {
        var log = new TransplantRunLog("test") { result = "FAIL", error = "NullReferenceException: boom" };
        log.EnsureFailHasOffender();
        Assert.AreEqual(1, log.offenders.Count);
        StringAssert.Contains("NullReferenceException: boom", log.offenders[0]);
    }

    [Test]
    public void EnsureFailHasOffender_does_not_double_add_when_offender_present()
    {
        var log = new TransplantRunLog("test") { result = "FAIL" };
        log.Offender("real named offender");
        log.EnsureFailHasOffender();
        Assert.AreEqual(1, log.offenders.Count);
        Assert.AreEqual("real named offender", log.offenders[0]);
    }

    [Test]
    public void EnsureFailHasOffender_leaves_pass_untouched()
    {
        var log = new TransplantRunLog("test") { result = "PASS" };
        log.EnsureFailHasOffender();
        Assert.AreEqual(0, log.offenders.Count);
    }

    [Test]
    public void EnsureFailHasOffender_uses_fallback_text_when_error_null()
    {
        var log = new TransplantRunLog("test") { result = "FAIL", error = null };
        log.EnsureFailHasOffender();
        Assert.AreEqual(1, log.offenders.Count);
        StringAssert.Contains("no error detail", log.offenders[0]);
    }

    // ── WriteRunLog sections (the envelope's custom-section hook) ─────────────────────────────

    [Test]
    public void WriteRunLog_without_sections_ends_at_warnings()
    {
        var log = new TransplantRunLog("tc-test");
        log.Count("n", 1);
        string path = TransplantCore.WriteRunLog(log, "no-sections");
        try
        {
            string json = System.IO.File.ReadAllText(path);
            StringAssert.Contains("\"warnings\": []\n}", json); // envelope closes right after warnings
        }
        finally { UnityEditor.AssetDatabase.DeleteAsset(path); }
    }

    [Test]
    public void WriteRunLog_emits_sections_verbatim_after_warnings_in_order()
    {
        var log = new TransplantRunLog("tc-test");
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

    sealed class SectionedProbeLog : TransplantRunLog
    {
        public SectionedProbeLog() : base("tc-probe") { }
    }
}
