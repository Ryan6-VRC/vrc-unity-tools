// Pins ReportPrefab's per-level attribution on a chain built here from scratch: a regular prefab, a variant
// of it that removes one child, adds one, removes one component, flips one property and leaves overrides on
// what it removed, and a scene instance
// of the variant with one authored move. The load-bearing claim is that each edit lands at the LEVEL that made
// it and nowhere else — the flattened read every other door gives cannot say that. Fixture assets go under a
// throwaway folder and are deleted in TearDown; the Snapshot artifacts each call writes are recorded and
// deleted at OneTimeTearDown (never globbed — the Snapshots dir is the operator's durable pile).
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

[Category("ReportPrefab")]
public class ReportPrefabTests
{
    private const string TmpDir = "Assets/AgentReportPrefabTmp";
    private const string BasePath = TmpDir + "/Base.prefab";
    private const string VarPath = TmpDir + "/Var.prefab";
    private const string NestedPath = TmpDir + "/Nested.prefab";
    private const string ScenePath = TmpDir + "/Fixture.unity";
    private static readonly List<string> Artifacts = new List<string>();

    [SetUp]
    public void SetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentReportPrefabTmp");
    }

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        AssetDatabase.Refresh();
    }

    [OneTimeTearDown]
    public void DeleteWrittenSnapshots()
    {
        var failed = new List<string>();
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), failed);
        Artifacts.Clear();
        Assert.IsEmpty(failed, "snapshot artifacts left behind in the operator's durable Snapshots dir");
    }

    // ----- fixture ---------------------------------------------------------------------------------------

    /// <summary>Base(Keep[BoxCollider], Drop(Sub)) → Var = Base − Drop − BoxCollider + Add + an instance of Nested,
    /// Keep inactive, plus three overrides left on the removed targets (collider size, Sub's position, Drop's
    /// rotation). Returns the scene instance of Var, whose `Add` is moved to (1,2,3).</summary>
    private static GameObject BuildChain(bool placeInScene)
    {
        var baseGo = new GameObject("Base");
        try
        {
            var keep = new GameObject("Keep"); keep.transform.SetParent(baseGo.transform); keep.AddComponent<BoxCollider>();
            var drop = new GameObject("Drop"); drop.transform.SetParent(baseGo.transform);
            new GameObject("Sub").transform.SetParent(drop.transform);
            PrefabUtility.SaveAsPrefabAsset(baseGo, BasePath);
        }
        finally { Object.DestroyImmediate(baseGo); }

        var nestedGo = new GameObject("Nested");
        try { new GameObject("Leaf").transform.SetParent(nestedGo.transform); PrefabUtility.SaveAsPrefabAsset(nestedGo, NestedPath); }
        finally { Object.DestroyImmediate(nestedGo); }

        var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BasePath);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset);
        try
        {
            Object.DestroyImmediate(inst.transform.Find("Drop").gameObject);          // records a removed GameObject
            Object.DestroyImmediate(inst.transform.Find("Keep").GetComponent<BoxCollider>()); // records a removed component
            // Overrides on targets this level removes. Appended rather than edited in, since the removed objects
            // have no instance left to edit; the shadowed test asserts they survived the save. Before the edits
            // below: SetPropertyModifications writes back a list that does not yet hold a just-made live edit.
            var mods = new List<PropertyModification>(PrefabUtility.GetPropertyModifications(inst));
            var dropSrc = baseAsset.transform.Find("Drop");
            mods.Add(new PropertyModification { target = baseAsset.transform.Find("Keep").GetComponent<BoxCollider>(), propertyPath = "m_Size.x", value = "2" });
            mods.Add(new PropertyModification { target = dropSrc.Find("Sub"), propertyPath = "m_LocalPosition.x", value = "5" });
            var rot = Quaternion.Euler(0f, 90f, 0f);
            foreach (var axis in new[] { "x", "y", "z", "w" })
                mods.Add(new PropertyModification { target = dropSrc, propertyPath = "m_LocalRotation." + axis, value = rot["xyzw".IndexOf(axis)].ToString(System.Globalization.CultureInfo.InvariantCulture) });
            PrefabUtility.SetPropertyModifications(inst, mods.ToArray());
            var add = new GameObject("Add"); add.transform.SetParent(inst.transform);     // records an added GameObject
            inst.transform.Find("Keep").gameObject.SetActive(false);                       // records a property override
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(NestedPath));
            nested.transform.SetParent(inst.transform);                                     // an added nested instance Var's file owns
            PrefabUtility.SaveAsPrefabAsset(inst, VarPath);
        }
        finally { Object.DestroyImmediate(inst); }
        AssetDatabase.Refresh();
        Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(VarPath)), "fixture must be a Variant");

        if (!placeInScene) return null;
        var sceneInst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(VarPath));
        sceneInst.transform.Find("Add").localPosition = new Vector3(1f, 2f, 3f);
        EditorSceneManager.SaveScene(SceneManager_Active(), ScenePath);
        return sceneInst;
    }

    private static UnityEngine.SceneManagement.Scene SceneManager_Active() => UnityEngine.SceneManagement.SceneManager.GetActiveScene();

    private static string Run(string handle, bool all = false)
    {
        string summary = ReportPrefab.Run(handle, all);
        Record(summary);
        return summary;
    }

    private static string Dependents(string path)
    {
        string summary = ReportPrefab.Dependents(path);
        Record(summary);
        return summary;
    }

    private static void Record(string summary)
    {
        int i = summary.IndexOf("log=");
        if (i >= 0) Artifacts.Add(summary.Substring(i + 4).Trim());
    }

    private static string Body(string summary)
    {
        int i = summary.IndexOf("log=");
        Assert.GreaterOrEqual(i, 0, "expected an artifact trailer, got: " + summary);
        return File.ReadAllText(summary.Substring(i + 4).Trim());
    }

    private static string Level(string body, int k)
    {
        int a = body.IndexOf("## L" + k + " ");
        Assert.GreaterOrEqual(a, 0, "no level " + k + " in:\n" + body);
        int b = body.IndexOf("\n## L", a + 1);
        return b < 0 ? body.Substring(a) : body.Substring(a, b - a);
    }

    // ----- Run on a scene instance --------------------------------------------------------------------

    [Test]
    public void Run_sceneInstance_attributesEachEditToItsLevel()
    {
        BuildChain(true);
        string summary = Run("Var");
        StringAssert.Contains("levels=3", summary);
        string body = Body(summary);

        string l0 = Level(body, 0);
        StringAssert.Contains("(scene instance)", l0);
        StringAssert.Contains("**added objects** (0)", l0);
        StringAssert.Contains("**removed objects** (0)", l0);
        StringAssert.Contains("| authored | `Transform(Add)` | `m_LocalPosition.x` |", l0);
        StringAssert.DoesNotContain("| default |", l0);   // root defaults are counted, not rowed, without all:true

        string l1 = Level(body, 1);
        StringAssert.Contains("(Variant)", l1);
        // Section-anchored, so swapping the added and removed lists cannot pass.
        StringAssert.Contains("**added objects** (2)\n- Add under `.`", l1);
        StringAssert.Contains("**removed objects** (1)\n- Drop under `.`", l1);
        StringAssert.Contains("**removed components** (1)\n- BoxCollider on `Keep`", l1);
        StringAssert.Contains("**added components** (0)", l1);
        StringAssert.Contains("| authored | `GameObject(Keep)` | `m_IsActive` | 1 | 0 |", l1);
        StringAssert.DoesNotContain("m_LocalPosition.x", l1);   // the scene's move must not leak down a level
        // The nested instance is owned by Var's file, so it is listed here with its own counts — and only here.
        StringAssert.Contains("**instances owned by this file** (1)\n- `Nested` <- `" + NestedPath + "`", l1);
        StringAssert.Contains("[added here]", l1);
        StringAssert.Contains("**instances owned by this file** (0)", l0);

        string l2 = Level(body, 2);
        StringAssert.Contains("(Regular)", l2);
        StringAssert.Contains("base of the chain", l2);
    }

    [Test]
    public void Run_all_rowsEveryTierAndKeepsCounts()
    {
        BuildChain(true);
        string body = Body(Run("Var", all: true));
        string l0 = Level(body, 0);
        StringAssert.Contains("| default | `Transform(.)` | `m_LocalRotation` |", l0);
        StringAssert.DoesNotContain("| default |", Level(Body(Run("Var")), 0));
    }

    // ----- Run on the asset directly --------------------------------------------------------------------

    [Test]
    public void Run_variantAssetPath_isLevelZeroWithTheSameContent()
    {
        BuildChain(false);
        string body = Body(Run(VarPath));
        string l0 = Level(body, 0);
        StringAssert.Contains("(Variant)", l0);
        StringAssert.Contains("**added objects** (2)\n- Add under `.`", l0);
        StringAssert.Contains("**removed objects** (1)\n- Drop under `.`", l0);
        StringAssert.Contains("**removed components** (1)\n- BoxCollider on `Keep`", l0);
        StringAssert.Contains("`m_IsActive`", l0);
        StringAssert.Contains("(Regular)", Level(body, 1));
    }

    [Test]
    public void Run_overrideOnRemovedTarget_isShadowedUnderItsRemoval()
    {
        BuildChain(false);
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(VarPath);
        int onRemoved = 0;
        foreach (var m in PrefabUtility.GetPropertyModifications(asset))
            if (m.target != null && (m.propertyPath == "m_Size.x" || m.propertyPath == "m_LocalPosition.x" && m.target.name == "Sub" || m.propertyPath.StartsWith("m_LocalRotation.") && m.target.name == "Drop"))
                onRemoved++;
        Assert.AreEqual(6, onRemoved, "fixture: the overrides on removed targets must survive the save with live targets, or this test proves nothing");

        string summary = Run(VarPath, all: true);
        StringAssert.Contains("authored=1 dangling=0 shadowed=3", summary);   // m_IsActive is the level's only authoring
        string l0 = Level(Body(summary), 0);
        StringAssert.Contains("**removed objects** (1)\n- Drop under `.` (2 shadowed overrides)", l0);
        StringAssert.Contains("**removed components** (1)\n- BoxCollider on `Keep` (1 shadowed override)", l0);
        StringAssert.Contains("| shadowed | `BoxCollider(Keep)` | `m_Size.x` |", l0);
        StringAssert.Contains("| shadowed | `Transform(Drop/Sub)` | `m_LocalPosition.x` |", l0);
        StringAssert.Contains("| shadowed | `Transform(Drop)` | `m_LocalRotation` |", l0);
        StringAssert.Contains("under removed Drop |", l0);
        StringAssert.Contains("on removed BoxCollider |", l0);
        StringAssert.DoesNotContain("?/", l0);
        StringAssert.DoesNotContain("| shadowed |", Level(Body(Run(VarPath)), 0));   // rowed only under all:true, like every non-authored tier
    }

    // ----- Refusals --------------------------------------------------------------------------------------

    [Test]
    public void Run_nestedObject_refusesNamingTheOutermostRoot()
    {
        BuildChain(true);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[ReportPrefab\] FAIL"));
        string r = ReportPrefab.Run("Var/Keep");
        StringAssert.Contains("FAIL", r);
        StringAssert.Contains("Run(\"Var\")", r);
        StringAssert.DoesNotContain("log=", r);
    }

    [Test]
    public void Run_plainObject_refuses()
    {
        new GameObject("Loose");
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[ReportPrefab\] FAIL"));
        StringAssert.Contains("not a prefab instance", ReportPrefab.Run("Loose"));
    }

    [Test]
    public void Run_unresolvable_refuses()
    {
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[ReportPrefab\] FAIL"));
        StringAssert.Contains("FAIL: handle:", ReportPrefab.Run("NoSuchThing"));
    }

    // ----- Dependents ------------------------------------------------------------------------------------

    [Test]
    public void Dependents_listsVariantAndTheSceneUnderIt()
    {
        BuildChain(true);
        string summary = Dependents(BasePath);
        StringAssert.Contains("variants=1", summary);
        StringAssert.Contains("scenes=1", summary);
        string body = Body(summary);
        StringAssert.Contains("- variant " + VarPath, body);
        StringAssert.Contains("  - scene " + ScenePath, body);   // indented: the scene contains Var, which contains Base
    }

    [Test]
    public void Dependents_nester_isNestsAndCaseVariantHandleStillResolves()
    {
        BuildChain(true);
        string body = Body(Dependents(NestedPath.Replace("Assets/", "assets/")));   // a non-canonical handle must hit the canonical map
        StringAssert.Contains("- nests " + VarPath, body);
        StringAssert.Contains("  - scene " + ScenePath, body);
    }

    [Test]
    public void Dependents_nonPrefab_refuses()
    {
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[ReportPrefab\] FAIL"));
        StringAssert.Contains("not a prefab", ReportPrefab.Dependents("Assets/NoSuch.prefab"));
    }

    // ----- Pure helpers ----------------------------------------------------------------------------------

    [Test]
    public void PrefabChurn_matchesRootLeafAndIndexedSegments_notOthers()
    {
        Assert.IsTrue(PrefabChurn.IsChurn("_modularAvatarVersionTag.MinimumVersion"));
        Assert.IsTrue(PrefabChurn.IsChurn("animationHashSet.Array.data[3]"));
        Assert.IsTrue(PrefabChurn.IsChurn("m_LocalEulerAnglesHint.x"));
        Assert.IsFalse(PrefabChurn.IsChurn("m_IsActive"));
        Assert.IsFalse(PrefabChurn.IsChurn("m_Bones.Array.data[0]"));   // re-pointing is proven inert, never allowlisted
        Assert.AreEqual("animationHashSet", PrefabChurn.ChurnToken("animationHashSet.Array.size"));
    }

    [Test]
    public void PrefabChurn_sceneStampSetIsTheNarrowOne()
    {
        Assert.IsTrue(PrefabChurn.IsSceneStamp("MinimumVersion"));
        Assert.IsFalse(PrefabChurn.IsSceneStamp("m_RootOrder"));   // a reorder is real work to the scene gate
        Assert.IsTrue(PrefabChurn.OverrideTokens.IsSupersetOf(PrefabChurn.SceneStampKeys));
    }

    [Test]
    public void Rotation_signFlipAndPartialListing_readAsTheSameRotation()
    {
        var src = Quaternion.Euler(30f, 40f, 50f);
        var flipped = new Quaternion(-src.x, -src.y, -src.z, -src.w);
        Assert.IsTrue(ReportPrefab.SameRotation(src, flipped));
        // Only three components listed by the instance: the fourth comes from the source, never zero.
        var partial = ReportPrefab.Reconstruct(src, flipped.x, flipped.y, flipped.z, null);
        Assert.IsFalse(ReportPrefab.SameRotation(src, partial), "three flipped + one unflipped is a genuinely different rotation");
        var same = ReportPrefab.Reconstruct(src, src.x, null, null, null);
        Assert.IsTrue(ReportPrefab.SameRotation(src, same));
        Assert.IsFalse(ReportPrefab.SameRotation(src, Quaternion.Euler(30f, 40f, 51f)));
    }
}
