using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// CheckPackage proof obligations, scoped to the empty-vs-dangling verdict its PASS/FAIL rests on:
//   • DescribeTarget(...) — the target-naming wording, pure, so all four shapes are asserted directly
//     rather than by hunting an asset state that produces each one.
//   • The false-alarm trap — a clean-zero slot stays EMPTY and stays PASS.
//   • Distinct-target collapse — N slots dangling at one target report N missing, 1 target. This is the
//     property the offender list exists to make legible; asserting the counts move independently is the
//     only way to catch a regression that re-fuses them.
// A dangling reference is synthesized by deleting an assigned material out from under a saved prefab,
// which is the real-world shape (the vendor case that motivated this was a prefab variant overriding
// material sub-objects its FBX never created). Whether the guid mapping survives that for an unloadable
// instance id is NOT asserted — DescribeTarget reports whichever way it goes, and pinning it here would
// bake in an assumption this suite cannot justify.
[Category("CheckPackage")]
public class CheckPackageTests
{
    private const string TmpDir = "Assets/AgentCheckPackageTmp";
    private static readonly Regex ErrRe = new Regex(@"\[CheckPackage\]");

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentCheckPackageTmp");
    }

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        AssetDatabase.Refresh();
        foreach (var f in Directory.Exists(RunLogFormat.RunLogDir)
                     ? Directory.GetFiles(RunLogFormat.RunLogDir, "check-package_AgentCheckPackageTmp_*")
                     : new string[0])
            File.Delete(f);
    }

    // ── Target naming: pure, every branch ──────────────────────────────────────────────────────────────

    [Test]
    public void DescribeTarget_unmapped_namesTheInstanceIdAndClaimsNoGuid()
    {
        var s = CheckPackage.DescribeTarget(487710, mapped: false, guid: null, fileId: 0, assetPath: null,
                                            assetExists: false);
        StringAssert.Contains("487710", s);
        StringAssert.Contains("no guid mapping", s);
    }

    [Test]
    public void DescribeTarget_guidWithNoAsset_saysAbsentFromProject()
    {
        var s = CheckPackage.DescribeTarget(1, mapped: true, guid: "deadbeef", fileId: 2100000, assetPath: "",
                                            assetExists: false);
        StringAssert.Contains("deadbeef", s);
        StringAssert.Contains("absent from this project", s);
        // The remedy differs from the resolves-but-lacks-the-sub-object case, so the two must not blur.
        Assert.IsFalse(s.Contains("holds no object"), "absent-guid wording must not read as a present file: " + s);
    }

    [Test]
    public void DescribeTarget_guidResolves_namesTheFileAndTheMissingFileId()
    {
        var s = CheckPackage.DescribeTarget(1, mapped: true, guid: "deadbeef", fileId: 2100000,
                                            assetPath: "Assets/Vendor/Outfits/X/Models/x.fbx", assetExists: true);
        StringAssert.Contains("Assets/Vendor/Outfits/X/Models/x.fbx", s);
        StringAssert.Contains("2100000", s);
        StringAssert.Contains("holds no object", s);
    }

    [Test]
    public void DescribeTarget_pathIsStale_saysTheAssetIsGoneNotTheSubObject()
    {
        // A deleted asset's guid keeps resolving to its old path, so the path alone cannot tell these
        // apart — and the remedies are opposite (find the sub-asset vs. restore the asset).
        var s = CheckPackage.DescribeTarget(1, mapped: true, guid: "deadbeef", fileId: 2100000,
                                            assetPath: "Assets/Vendor/Outfits/X/Models/x.fbx", assetExists: false);
        StringAssert.Contains("Assets/Vendor/Outfits/X/Models/x.fbx", s);
        StringAssert.Contains("no longer resolves", s);
        // Not "deleted": a failed import reaches here too, and the GUID cannot tell the two apart.
        Assert.IsFalse(s.Contains("no longer exists"), "must not assert deletion it cannot establish: " + s);
        Assert.IsFalse(s.Contains("holds no object"), "a gone file must not read as a present one: " + s);
    }

    // ── The verdict the counts drive ───────────────────────────────────────────────────────────────────

    [Test]
    public void VerifyFolder_cleanZeroSlot_isEmptyAndPasses()
    {
        SavePrefab("empty", new Material[1]); // one slot, never assigned

        var summary = CheckPackage.VerifyFolder(TmpDir);

        StringAssert.Contains("empty=1", summary);
        StringAssert.Contains("MISSING=0", summary);
        StringAssert.Contains("=> PASS", summary);
        // The false-alarm trap: an unassigned slot must not acquire dangling-reference language.
        Assert.IsFalse(summary.Contains("dangling"), "a clean-zero slot is not a dangling reference: " + summary);
    }

    [Test]
    public void VerifyFolder_twoSlotsOneDeadTarget_reportsTwoRefsAtOneDistinctTarget()
    {
        var mat = SaveMaterial("doomed");
        SavePrefab("dangling", new[] { mat, mat }); // two slots, same target
        AssetDatabase.DeleteAsset(TmpDir + "/doomed.mat");
        AssetDatabase.Refresh();

        LogAssert.Expect(LogType.Error, ErrRe); // FAIL logs at Error
        var summary = CheckPackage.VerifyFolder(TmpDir);

        // If this precondition fails the synthesis is suspect, not the feature: deleting the material is
        // what is meant to leave the prefab's two serialized references non-zero and unresolvable.
        StringAssert.Contains("MISSING=2", summary);
        StringAssert.Contains("dangling: 2 ref(s) at 1 distinct target(s)", summary);
        StringAssert.Contains("=> FAIL", summary);
        // No residue rider: that clause appears only when the count fell back to in-memory handles,
        // which is the one case it can over-count.
        Assert.IsFalse(summary.Contains("instance id only"), "targets should key on guid+fileID here: " + summary);
    }

    [Test]
    public void VerifyFolder_twoDeadTargets_doesNotCollapseUnrelatedTargets()
    {
        var a = SaveMaterial("doomedA");
        var b = SaveMaterial("doomedB");
        SavePrefab("dangling", new[] { a, b });
        AssetDatabase.DeleteAsset(TmpDir + "/doomedA.mat");
        AssetDatabase.DeleteAsset(TmpDir + "/doomedB.mat");
        AssetDatabase.Refresh();

        LogAssert.Expect(LogType.Error, ErrRe);
        var summary = CheckPackage.VerifyFolder(TmpDir);

        StringAssert.Contains("dangling: 2 ref(s) at 2 distinct target(s)", summary);
    }

    [Test]
    public void VerifyFolder_deadMesh_contributesADistinctTargetToo()
    {
        var meshPath = TmpDir + "/doomed.asset";
        AssetDatabase.CreateAsset(new Mesh(), meshPath);
        var go = new GameObject("deadmesh");
        try
        {
            go.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            go.AddComponent<MeshRenderer>().sharedMaterials = new Material[0];
            PrefabUtility.SaveAsPrefabAsset(go, TmpDir + "/deadmesh.prefab");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.Refresh();

        LogAssert.Expect(LogType.Error, ErrRe);
        var summary = CheckPackage.VerifyFolder(TmpDir);

        // The tally spans both dangling classes, so a mesh-only break must still report a target.
        StringAssert.Contains("meshMISSING=1", summary);
        StringAssert.Contains("dangling: 1 ref(s) at 1 distinct target(s)", summary);
    }

    // The dangling tally spans material slots AND meshes, so nesting it inside "materials" would mislabel its
    // scope. That is a STRUCTURAL claim and it is asserted structurally: the previous form pinned the literal
    // `"missing": 0 }`, which proved the parent only via the brace's position in the emitted text — adding any
    // field to the materials block broke it, and nesting danglingDistinctTargets under materials without
    // disturbing that spacing passed it.
    [Test]
    public void RunLog_carriesDistinctTargetCountsOutsideTheMaterialsObject()
    {
        SavePrefab("empty", new Material[1]);

        var logPath = CheckPackage.VerifyFolder(TmpDir).Split(new[] { "log=" }, 2, System.StringSplitOptions.None)[1];

        var json = File.ReadAllText(logPath);
        var materials = MaterialsObject(json);
        StringAssert.Contains("\"missing\"", materials); // the block still carries its own per-slot counts
        StringAssert.Contains("\"danglingDistinctTargets\": 0", json);
        StringAssert.Contains("\"danglingUnidentifiedTargets\": 0", json);
        StringAssert.DoesNotContain("danglingDistinctTargets", materials);
        StringAssert.DoesNotContain("danglingUnidentifiedTargets", materials);
    }

    // ── External-material remap: the two classes, pure ────────────────────────────────────────────────
    // Both classes FAIL, but their remedies run in OPPOSITE order, so the discriminator carries the whole
    // value: telling a caller to force-reimport when the pack was never imported is a guaranteed no-op.
    // Asserted through the pure decision + wording rather than a binary FBX fixture, matching the
    // DescribeTarget precedent above — an importer state that produces each branch cannot be built here.

    [Test]
    public void ClassifyRemap_noMapEntries_isSilent()
    {
        // The predicate that replaced the dead `materialLocation == External` gate. A model with no external
        // map says nothing about its empty slots, and empty slots alone must never become evidence — that is
        // the false-alarm trap this whole tool is built to avoid.
        Assert.AreEqual(CheckPackage.RemapVerdict.None, CheckPackage.ClassifyRemap(0, 0, 12));
    }

    [Test]
    public void ClassifyRemap_mapButNoEmptySlots_isSilent()
    {
        Assert.AreEqual(CheckPackage.RemapVerdict.None, CheckPackage.ClassifyRemap(4, 2, 0));
    }

    [Test]
    public void ClassifyRemap_allEntriesResolveYetSlotsEmpty_isStale()
    {
        Assert.AreEqual(CheckPackage.RemapVerdict.Stale, CheckPackage.ClassifyRemap(4, 0, 3));
    }

    [Test]
    public void ClassifyRemap_anyUnresolvedEntry_isUnresolved()
    {
        Assert.AreEqual(CheckPackage.RemapVerdict.Unresolved, CheckPackage.ClassifyRemap(2, 2, 10));
    }

    [Test]
    public void ClassifyRemap_partiallyResolvedPack_isUnresolvedNotStale()
    {
        // The case that decides which remedy the caller is handed. Force-reimport alone leaves the
        // unresolved half exactly as it was, so a partial miss must not read as merely stale.
        Assert.AreEqual(CheckPackage.RemapVerdict.Unresolved, CheckPackage.ClassifyRemap(5, 1, 2));
    }

    [Test]
    public void DescribeRemap_unresolved_namesTheKeysAndOrdersTheRemedy()
    {
        var s = CheckPackage.DescribeRemap(CheckPackage.RemapVerdict.Unresolved,
            mappedCount: 2, unresolved: new System.Collections.Generic.List<string> { "m_lower", "m_upper" }, emptySlots: 10);
        StringAssert.Contains("m_lower", s);
        StringAssert.Contains("m_upper", s);
        // Order is the payload: the targets must exist BEFORE the reimport, or the reimport achieves nothing.
        int restore = s.IndexOf("restore or import", System.StringComparison.Ordinal);
        int reimport = s.IndexOf("force-reimport", System.StringComparison.Ordinal);
        Assert.That(restore, Is.GreaterThanOrEqualTo(0), "the unresolved remedy must name the missing targets: " + s);
        Assert.That(restore, Is.LessThan(reimport), "restoring the targets must precede force-reimport: " + s);
        // A null remap target proves the target is absent, not WHY — a deleted or re-GUIDed .mat looks identical
        // to a never-imported pack, so the message must not assert the cause it cannot observe.
        StringAssert.DoesNotContain("never imported", s);
    }

    [Test]
    public void DescribeRemap_stale_prescribesReimportAndNotThePack()
    {
        var s = CheckPackage.DescribeRemap(CheckPackage.RemapVerdict.Stale,
            mappedCount: 4, unresolved: new System.Collections.Generic.List<string>(), emptySlots: 3);
        StringAssert.Contains("force-reimport", s);
        // Naming absent targets here would send a caller off to restore something that already resolves.
        StringAssert.DoesNotContain("restore or import", s);
        // Model-level assertion, so a reimport can legitimately not clear it — say so, or the caller loops.
        StringAssert.Contains("not the mapped ones", s);
    }

    [Test]
    public void NameList_capsAndCountsTheRemainder()
    {
        var many = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 9; i++) many.Add("mat" + i);
        var s = CheckPackage.NameList(many);
        StringAssert.Contains("mat0", s);
        StringAssert.Contains("+3 more", s);
        StringAssert.DoesNotContain("mat8", s);
    }

    [Test]
    public void Summary_carriesBothRemapClassesSeparately()
    {
        // Two counters, two names: a single fused remap number would hide which remedy applies.
        SavePrefab("empty", new Material[1]);
        var summary = CheckPackage.VerifyFolder(TmpDir);
        StringAssert.Contains("remapSTALE=0", summary);
        StringAssert.Contains("remapUNRESOLVED=0", summary);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────

    // The "materials" object's own text, brace-balanced from its opening '{' — whitespace-independent, which
    // is the whole point. The block holds only numbers, so no string literal can carry an unbalanced brace.
    private static string MaterialsObject(string json)
    {
        int at = json.IndexOf("\"materials\"", System.StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), "RunLog lost its materials object: " + json);
        int open = json.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < json.Length; i++)
        {
            if (json[i] == '{') depth++;
            else if (json[i] == '}' && --depth == 0) return json.Substring(open, i - open + 1);
        }
        Assert.Fail("the materials object never closes: " + json);
        return null;
    }

    private static Material SaveMaterial(string name)
    {
        var mat = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(mat, TmpDir + "/" + name + ".mat");
        return mat;
    }

    private static void SavePrefab(string name, Material[] slots)
    {
        var go = new GameObject(name);
        try
        {
            go.AddComponent<MeshRenderer>().sharedMaterials = slots;
            PrefabUtility.SaveAsPrefabAsset(go, TmpDir + "/" + name + ".prefab");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        AssetDatabase.Refresh();
    }
}
