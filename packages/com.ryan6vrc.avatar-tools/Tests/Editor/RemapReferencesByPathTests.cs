using System.Collections.Generic;
using NUnit.Framework;
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

    // NOTE: these tests assert the read-only resolver's DECISION (IndexedPath.FindByIndexedPath /
    // RemapReferencesByPath.Counterpart) rather than Remap's SerializedObject write-through. Building +
    // reading + destroying plain GameObject hierarchies is NUnit-safe; mutating a SerializedObject via
    // Remap and then DestroyImmediate-ing its GameObject crashes the Editor (see coverage-gaps.md for what
    // that dropped from coverage and where it's re-covered).

    [Test]
    public void RenameMap_resolves_ref_across_renamed_armature_root_and_disambiguates_dup()
    {
        // Same fixture as the (removed) mutating test: rename map {Armature ⇒ Armature.1} plus a ref
        // crossing the renamed root and a ref to the 2nd of two duplicate-named "Chain" siblings deeper
        // in the tree.
        var src = MakeArmatureTree("SRC", "Armature");
        var dst = MakeArmatureTree("DST", "Armature.1");
        var hips = src.transform.Find("Armature/Hips");
        var chain2 = hips.GetChild(1);   // the 2nd "Chain" (dup sibling)
        var map = new Dictionary<string, string> { { "Armature", "Armature.1" } };

        var hitHips = IndexedPath.FindByIndexedPath(src.transform, dst.transform, hips, map, out var f1);
        var hitChain2 = IndexedPath.FindByIndexedPath(src.transform, dst.transform, chain2, map, out var f2);
        Assert.IsNull(f1); Assert.IsNull(f2);

        Assert.AreEqual(dst.transform.Find("Armature.1/Hips"), hitHips, "resolves under the renamed root");
        Assert.AreEqual(dst.transform.Find("Armature.1/Hips").GetChild(1), hitChain2,
            "deeper dup sibling still disambiguates to the 2nd Chain under the renamed root");

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }

    [Test]
    public void NoMap_leaves_renamed_root_ref_unresolved_exactly_as_today()
    {
        // Absent map: "Armature" (src) has no counterpart under DST (only "Armature.1" exists), so the
        // indexed path is missing and resolution returns null — a plain missing path, not the A1
        // ambiguous-rename guard (which never fires with a null map).
        var src = MakeArmatureTree("SRC", "Armature");
        var dst = MakeArmatureTree("DST", "Armature.1");
        var hips = src.transform.Find("Armature/Hips");

        var hit = IndexedPath.FindByIndexedPath(src.transform, dst.transform, hips, null, out var failReason);
        Assert.IsNull(hit);
        Assert.IsNull(failReason, "a plain missing path is not the A1 ambiguous-rename guard");

        // Counterpart with a null map returns null on the same input (byte-identical to today).
        Assert.IsNull(RemapReferencesByPath.Counterpart(src.transform, dst.transform, hips));

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
    public void A1_count_mismatch_resolves_to_null_with_ambiguous_rename_reason()
    {
        // src has 1 'Armature'; dst has 2 'Armature.1' → mapped-name occurrence is ambiguous, so the A1
        // guard resolves to null (never a wrong sibling) and names the reason. Read-only: no SerializedObject
        // mutation, so building + destroying these hierarchies is NUnit-safe.
        var src = new GameObject("SRC");
        var sArm = new GameObject("Armature"); sArm.transform.SetParent(src.transform, false);
        var sHips = new GameObject("Hips"); sHips.transform.SetParent(sArm.transform, false);

        var dst = new GameObject("DST");
        for (int i = 0; i < 2; i++)
        {
            var a = new GameObject("Armature.1"); a.transform.SetParent(dst.transform, false);
            new GameObject("Hips").transform.SetParent(a.transform, false);
        }
        var map = new Dictionary<string, string> { { "Armature", "Armature.1" } };

        var hit = IndexedPath.FindByIndexedPath(src.transform, dst.transform, sHips.transform, map, out var failReason);
        Assert.IsNull(hit, "ambiguous mapped occurrence resolves to null, not a wrong sibling");
        StringAssert.Contains("ambiguous rename", failReason);

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
    public void Resolves_basic_and_duplicate_siblings_by_indexed_path()
    {
        // Same fixture as the (removed) mutating test: a plain child and the 2nd of two duplicate-named
        // "B" siblings. The orphan (a ref NOT under srcRoot) case moved out — Counterpart's "not under
        // srcRoot ⇒ null" contract is not the same claim as Remap's "leave it untouched", so it can't be
        // re-expressed read-only; see coverage-gaps.md.
        var src = MakeTree("SRC");
        var dst = MakeTree("DST");
        var a = src.transform.Find("A");
        var b2 = src.transform.GetChild(2);   // the 2nd "B"

        var hitA = IndexedPath.FindByIndexedPath(src.transform, dst.transform, a);
        var hitB2 = IndexedPath.FindByIndexedPath(src.transform, dst.transform, b2);

        Assert.AreEqual(dst.transform.Find("A"), hitA, "basic");
        Assert.AreEqual(dst.transform.GetChild(2), hitB2, "dup-sibling -> 2nd B, not 1st");

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }

    [Test]
    public void Resolves_to_null_when_path_missing_in_dst()
    {
        var src = MakeTree("SRC");
        var dst = new GameObject("DST"); // empty, no A/B
        var a = src.transform.Find("A");

        var hit = IndexedPath.FindByIndexedPath(src.transform, dst.transform, a);
        Assert.IsNull(hit);

        Object.DestroyImmediate(src); Object.DestroyImmediate(dst);
    }
}
