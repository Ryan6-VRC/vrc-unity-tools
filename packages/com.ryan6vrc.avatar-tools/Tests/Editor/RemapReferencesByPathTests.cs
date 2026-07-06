using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class RemapReferencesByPathTests
{
    // Armature-root rename fixture: root / Armature / Hips / Chain(x2). The only segment whose NAME differs
    // vendor↔owned is the armature root (skinning preserves the bones below); the rename map carries that
    // one correspondence, and every ref crossing the armature resolves under it.
    static GameObject MakeArmatureTree(string rootName, string armatureName)
    {
        var root = new GameObject(rootName);
        var arm  = new GameObject(armatureName); arm.transform.SetParent(root.transform, false);
        var hips = new GameObject("Hips");       hips.transform.SetParent(arm.transform, false);
        var c1 = new GameObject("Chain"); c1.transform.SetParent(hips.transform, false);   // occurrence 0
        var c2 = new GameObject("Chain"); c2.transform.SetParent(hips.transform, false);   // occurrence 1 (dup)
        return root;
    }

    [Test]
    public void RenameMap_rebinds_ref_across_renamed_armature_root_and_disambiguates_dup()
    {
        var src = MakeArmatureTree("SRC", "Armature");
        var dst = MakeArmatureTree("DST", "Armature.1");
        var holder = src.AddComponent<TestRefHolder>();
        holder.refA  = src.transform.Find("Armature/Hips");                   // crosses the renamed root
        holder.refB2 = src.transform.Find("Armature/Hips").GetChild(1);       // the 2nd "Chain" (dup sibling)
        var so = new SerializedObject(holder);

        var map = new Dictionary<string, string> { { "Armature", "Armature.1" } };
        var r = RemapReferencesByPath.Remap(so, src.transform, dst.transform, map);
        so.ApplyModifiedPropertiesWithoutUndo();

        Assert.AreEqual(dst.transform.Find("Armature.1/Hips"), holder.refA, "rebound under the renamed root");
        Assert.AreEqual(dst.transform.Find("Armature.1/Hips").GetChild(1), holder.refB2,
            "deeper dup sibling still disambiguates to the 2nd Chain under the renamed root");
        Assert.AreEqual(2, r.remapped);
        Assert.AreEqual(0, r.nulled);

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }

    [Test]
    public void NullMap_leaves_renamed_root_ref_nulled_exactly_as_today()
    {
        var src = MakeArmatureTree("SRC", "Armature");
        var dst = MakeArmatureTree("DST", "Armature.1");
        var holder = src.AddComponent<TestRefHolder>();
        holder.refA = src.transform.Find("Armature/Hips");
        var so = new SerializedObject(holder);

        // Absent map (default overload) → the ref nulls: "Armature" has no counterpart under DST.
        var r = RemapReferencesByPath.Remap(so, src.transform, dst.transform);
        so.ApplyModifiedPropertiesWithoutUndo();
        Assert.IsNull(holder.refA);
        Assert.AreEqual(1, r.nulled);

        // Counterpart with a null map returns null on the same input (byte-identical to today).
        Assert.IsNull(RemapReferencesByPath.Counterpart(src.transform, dst.transform, src.transform.Find("Armature/Hips")));

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }

    [Test]
    public void FindByIndexedPath_rename_funnel_binds_1to1_with_symmetric_occurrence_index()
    {
        // src P/[Other, Armature], map {Other ⇒ Armature}, dst P/[Armature#0, Armature#1]. Both the mapped
        // key and the literal sibling resolve to "Armature"; the occurrence index is counted in resolving-to
        // space (symmetric with the dest lookup) so they bind 1:1 — Other→#0, Armature→#1 — not both onto #0.
        var src = new GameObject("S");
        var sp = new GameObject("P"); sp.transform.SetParent(src.transform, false);
        var sOther = new GameObject("Other"); sOther.transform.SetParent(sp.transform, false);
        var sArm = new GameObject("Armature"); sArm.transform.SetParent(sp.transform, false);

        var dst = new GameObject("D");
        var dp = new GameObject("P"); dp.transform.SetParent(dst.transform, false);
        var dA0 = new GameObject("Armature"); dA0.transform.SetParent(dp.transform, false);
        var dA1 = new GameObject("Armature"); dA1.transform.SetParent(dp.transform, false);
        var map = new Dictionary<string, string> { { "Other", "Armature" } };

        var hOther = IndexedPath.FindByIndexedPath(src.transform, dst.transform, sOther.transform, map, out var f1);
        var hArm   = IndexedPath.FindByIndexedPath(src.transform, dst.transform, sArm.transform,   map, out var f2);
        Assert.IsNull(f1); Assert.IsNull(f2);
        Assert.AreSame(dA0.transform, hOther, "mapped 'Other' → dest Armature#0");
        Assert.AreSame(dA1.transform, hArm,   "literal 'Armature' → dest Armature#1");
        Assert.AreNotSame(hOther, hArm, "1:1 — no collapse onto one dest occurrence");

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }

    [Test]
    public void A1_count_mismatch_nulls_ref_with_named_rename_warning_not_wrong_bind()
    {
        // src has 1 'Armature'; dst has 2 'Armature.1' → the mapped-name occurrence is ambiguous, so the
        // A1 guard nulls the ref (never binds the wrong sibling) and names it in renameWarnings.
        var src = new GameObject("SRC");
        var sArm = new GameObject("Armature"); sArm.transform.SetParent(src.transform, false);
        var sHips = new GameObject("Hips"); sHips.transform.SetParent(sArm.transform, false);

        var dst = new GameObject("DST");
        for (int i = 0; i < 2; i++)
        {
            var a = new GameObject("Armature.1"); a.transform.SetParent(dst.transform, false);
            new GameObject("Hips").transform.SetParent(a.transform, false);
        }

        var holder = src.AddComponent<TestRefHolder>();
        holder.refA = sHips.transform;
        var so = new SerializedObject(holder);
        var map = new Dictionary<string, string> { { "Armature", "Armature.1" } };
        var r = RemapReferencesByPath.Remap(so, src.transform, dst.transform, map);
        so.ApplyModifiedPropertiesWithoutUndo();

        Assert.IsNull(holder.refA, "ambiguous mapped occurrence → nulled, not wrong-sibling bound");
        Assert.AreEqual(1, r.nulled);
        Assert.AreEqual(1, r.renameWarnings.Count);
        StringAssert.Contains("ambiguous rename", r.renameWarnings[0]);

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }
    static GameObject MakeTree(string name)
    {
        var root = new GameObject(name);
        var a = new GameObject("A"); a.transform.SetParent(root.transform);
        var b1 = new GameObject("B"); b1.transform.SetParent(root.transform);
        var b2 = new GameObject("B"); b2.transform.SetParent(root.transform); // duplicate name
        return root;
    }

    [Test]
    public void Remaps_basic_and_duplicate_siblings_and_leaves_orphan()
    {
        var src = MakeTree("SRC");
        var dst = MakeTree("DST");
        var orphan = new GameObject("ORPHAN").transform;   // NOT under src
        var holder = src.AddComponent<TestRefHolder>();
        holder.refA = src.transform.Find("A");
        holder.refB2 = src.transform.GetChild(2);            // the 2nd "B"
        holder.orphan = orphan;
        var so = new SerializedObject(holder);

        var r = RemapReferencesByPath.Remap(so, src.transform, dst.transform);
        so.ApplyModifiedPropertiesWithoutUndo();

        Assert.AreEqual(dst.transform.Find("A"), holder.refA);          // basic
        Assert.AreEqual(dst.transform.GetChild(2), holder.refB2);       // dup-sibling -> 2nd B, not 1st
        Assert.AreEqual(orphan, holder.orphan);                         // ref NOT under src -> untouched
        Assert.AreEqual(2, r.remapped);
        Assert.AreEqual(0, r.nulled);

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst); Object.DestroyImmediate(orphan.gameObject);
    }

    [Test]
    public void Nulls_when_path_missing_in_dst()
    {
        var src = MakeTree("SRC");
        var dst = new GameObject("DST"); // empty, no A/B
        var holder = src.AddComponent<TestRefHolder>();
        holder.refA = src.transform.Find("A");
        var so = new SerializedObject(holder);
        var r = RemapReferencesByPath.Remap(so, src.transform, dst.transform);
        so.ApplyModifiedPropertiesWithoutUndo();
        Assert.IsNull(holder.refA);
        Assert.AreEqual(1, r.nulled);
        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }
}

public class TestRefHolder : MonoBehaviour
{
    public Transform refA, refB2, orphan;
}
