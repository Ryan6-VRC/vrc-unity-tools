using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// CheckPackage proof obligations, scoped to the empty-vs-dangling verdict its PASS/FAIL rests on:
//   • DescribeTarget(...) — the target-naming wording, pure, so all three shapes are asserted directly
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
        var s = CheckPackage.DescribeTarget(487710, mapped: false, guid: null, fileId: 0, assetPath: null);
        StringAssert.Contains("487710", s);
        StringAssert.Contains("no guid mapping", s);
    }

    [Test]
    public void DescribeTarget_guidWithNoAsset_saysAbsentFromProject()
    {
        var s = CheckPackage.DescribeTarget(1, mapped: true, guid: "deadbeef", fileId: 2100000, assetPath: "");
        StringAssert.Contains("deadbeef", s);
        StringAssert.Contains("absent from this project", s);
        // The remedy differs from the resolves-but-lacks-the-sub-object case, so the two must not blur.
        Assert.IsFalse(s.Contains("holds no object"), "absent-guid wording must not read as a present file: " + s);
    }

    [Test]
    public void DescribeTarget_guidResolves_namesTheFileAndTheMissingFileId()
    {
        var s = CheckPackage.DescribeTarget(1, mapped: true, guid: "deadbeef", fileId: 2100000,
                                            assetPath: "Assets/Vendor/Outfits/X/Models/x.fbx");
        StringAssert.Contains("Assets/Vendor/Outfits/X/Models/x.fbx", s);
        StringAssert.Contains("2100000", s);
        StringAssert.Contains("holds no object", s);
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

    [Test]
    public void RunLog_carriesDistinctTargetCountsOutsideTheMaterialsObject()
    {
        SavePrefab("empty", new Material[1]);

        var logPath = CheckPackage.VerifyFolder(TmpDir).Split(new[] { "log=" }, 2, System.StringSplitOptions.None)[1];

        var json = File.ReadAllText(logPath);
        StringAssert.Contains("\"missing\": 0 }", json); // the materials object closes after missing
        StringAssert.Contains("\"danglingDistinctTargets\": 0", json);
        StringAssert.Contains("\"danglingUnidentifiedTargets\": 0", json);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────

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
