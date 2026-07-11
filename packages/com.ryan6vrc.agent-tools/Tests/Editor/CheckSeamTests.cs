using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// CheckSeam proof obligations (spec 2026-07-11-checkseam-design.md, plan 2026-07-11-checkseam.md).
//
// CheckSeam.Check resolves scene paths against the ACTIVE scene (its local Resolve/FindByHierarchyPath), so —
// like CheckAvatarTests — fixtures live in a throwaway ADDITIVE scene set active in SetUp and torn down in
// place; nothing is ever saved to the real project. The pure-core logic is exercised by injecting fake seams
// (ResolveSeam / ResolveHumanoid) via reflection (Tests is a separate assembly), the same seams that carry the
// real MA/VRCFury reflection live. The seams are captured in SetUp and restored in TearDown.
[Category("CheckSeam")]
public class CheckSeamTests
{
    private GameObject _root;
    private string _logPath;
    private object _origResolveSeam, _origResolveHumanoid;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // REFUSE paths log at error/warning — expected in negative tests
        // Batchmode boots on an untitled, unsaved scene; NewScene(Additive) throws against that ("Cannot create
        // a new scene additively with an untitled scene unsaved") and a freshly created untitled scene is itself
        // dirty, so additive is never allowed when a test runs in isolation (CheckAvatarTests only survives it
        // because an earlier full-suite fixture leaves a saved scene active). Use a Single throwaway scene: our
        // fixtures live in it (the active scene, which is exactly what CheckSeam.Resolve searches). Never saved.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _root = new GameObject("CheckSeamTestRoot");
        _origResolveSeam = GetSeam("ResolveSeam");
        _origResolveHumanoid = GetSeam("ResolveHumanoid");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
        _root = null;
        ResetSeams();
        // _tmpScene is the only open scene (Single) and so cannot be closed; the next SetUp's Single NewScene
        // replaces it, discarding fixtures. It is never saved — nothing persists to the project.
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Reflection helpers (resolve real types + flip internal seams) ───────────────────────────────

    private static Type Resolve(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void SetSeam(string field, object value) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

    private static object GetSeam(string field) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private void ResetSeams()
    {
        SetSeam("ResolveSeam", _origResolveSeam);
        SetSeam("ResolveHumanoid", _origResolveHumanoid);
    }

    private string ReadLog(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, StringComparison.Ordinal);
        _logPath = i < 0 ? null : result.Substring(i + marker.Length).Trim();
        return _logPath != null && File.Exists(_logPath) ? File.ReadAllText(_logPath) : "";
    }

    // ── Fixture builders ────────────────────────────────────────────────────────────────────────────

    private GameObject NewChild(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    // Hierarchy path of a fixture GO (matches CheckSeam.Resolve's FindByHierarchyPath contract).
    private static string Path(GameObject go)
    {
        var t = go.transform;
        var sb = new System.Text.StringBuilder(t.name);
        while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
        return sb.ToString();
    }

    // Build a base GO with N named child bones + an empty merge GO, and inject a non-empty HumanoidMap over the
    // base bones so the door's humanoid guard passes and the seam guards (this task) are the ones under test.
    private void SetupBaseAndHumanoid(out GameObject baseGO, out GameObject mergeGO, int humanoidBones)
    {
        baseGO = NewChild(_root, "Base");
        mergeGO = NewChild(_root, "Merge");
        string[] names = { "Hips", "Spine", "Chest", "Head", "Neck" };
        var map = new CheckSeam.HumanoidMap { SpanMm = 350f };
        for (int i = 0; i < humanoidBones; i++)
            map.Bones.Add(NewChild(baseGO, names[i % names.Length]).transform);
        CheckSeam.ResolveHumanoid = _ => map;
    }

    private static Transform FindBone(GameObject root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    // Create one merge bone child per (name,weight) under mergeGO, plus a SkinnedMeshRenderer whose sharedMesh
    // has one vertex per bone binding that bone at influence-0 with the given weight, and whose bones[] point at
    // exactly those transforms. Returns name→merge Transform so the caller can map base↔merge on the SAME
    // instances (the merge-side join is on the Transform reference, not the name).
    private Dictionary<string, Transform> NewSkinnedMergeable(GameObject mergeGO, (string name, float weight)[] weights)
    {
        int n = weights.Length;
        var bones = new Transform[n];
        var byName = new Dictionary<string, Transform>();
        var verts = new Vector3[n];
        var bw = new BoneWeight[n];
        var bindposes = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            var b = NewChild(mergeGO, weights[i].name).transform;
            bones[i] = b;
            byName[weights[i].name] = b;
            verts[i] = Vector3.zero;
            bw[i] = new BoneWeight { boneIndex0 = i, weight0 = weights[i].weight };
            bindposes[i] = Matrix4x4.identity;
        }
        var mesh = new Mesh { vertices = verts };
        mesh.boneWeights = bw;
        mesh.bindposes = bindposes;
        var smr = NewChild(mergeGO, "Skin").AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
        return byName;
    }

    // Build a base+merge fixture wired for the count branch: for each humanoid name create a base bone (→ injected
    // HumanoidMap) and a merge bone (skinned per `weights` if listed, else a plain child), map them base↔merge via
    // an injected SeamResolution. A weight ≥0.1 makes that pair weighted-humanoid. Names in `weights` must be a
    // subset of `humanoid`.
    private void BuildSeam(out GameObject baseGO, out GameObject mergeGO, string[] humanoid, (string, float)[] weights)
    {
        baseGO = NewChild(_root, "Base");
        mergeGO = NewChild(_root, "Merge");

        var map = new CheckSeam.HumanoidMap { SpanMm = 350f };
        var baseByName = new Dictionary<string, Transform>();
        foreach (var name in humanoid)
        {
            var bt = NewChild(baseGO, name).transform;
            baseByName[name] = bt;
            map.Bones.Add(bt);
        }
        CheckSeam.ResolveHumanoid = _ => map;

        var mergeByName = NewSkinnedMergeable(mergeGO, weights);

        var seam = new CheckSeam.SeamResolution();
        foreach (var name in humanoid)
        {
            if (!mergeByName.TryGetValue(name, out var mt))
                mt = NewChild(mergeGO, name).transform; // mapped humanoid bone with no skin weight
            seam.Pairs.Add(new CheckSeam.BonePair { Base = baseByName[name], Merge = mt });
        }
        CheckSeam.ResolveSeam = (_, __) => seam;
    }

    private static System.Text.RegularExpressions.Regex RefuseRe =>
        new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] REFUSE:");

    // ── Tests ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void BadInput_baseNotFound_bareRefuse_noTrailer()
    {
        // A bare REFUSE logs at Error (Refuse → Debug.LogError); consume it so the test doesn't flag it.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] REFUSE:"));
        var r = CheckSeam.Check("no-such-base", "no-such-merge");
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("no-such-base", r);
        Assert.IsFalse(r.Contains("| log="), "refusal carries no RunLog trailer");
    }

    [Test]
    public void NoHumanoidAvatar_refuses()
    {
        // A base with no humanoid Animator → DefaultResolveHumanoid returns an empty map. Inject the empty map
        // directly so this proves the door's empty-bucket REFUSE path independently of the reflection default.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] REFUSE:"));
        var baseGO = NewChild(_root, "Base");     // plain GO, no humanoid Animator
        var mergeGO = NewChild(_root, "Merge");
        CheckSeam.ResolveHumanoid = _ => new CheckSeam.HumanoidMap(); // empty ⇒ REFUSE upstream
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("no humanoid", r);
    }

    // ── Task 3: seam-mapping structural guards (injected SeamResolution over plain Transforms) ─────────
    // The reflection default (DefaultResolveSeam) is proven by the live corpus (Task 8), not here — a
    // SDK-only TestEditor has no MA/VRCFury. These prove the door's union/guard/conflict branches.

    [Test]
    public void NoSeam_refuses()
    {
        LogAssert.Expect(LogType.Error, RefuseRe);
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution(); // empty Pairs
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("no scorable seam", r);
    }

    [Test]
    public void SeamConflict_refuses()
    {
        LogAssert.Expect(LogType.Error, RefuseRe);
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        var baseBone = FindBone(baseGO, "Hips");
        var m1 = NewChild(mergeGO, "Hips.a");
        var m2 = NewChild(mergeGO, "Hips.b");
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution
        {
            Pairs = {
                new CheckSeam.BonePair { Base = baseBone, Merge = m1.transform },
                new CheckSeam.BonePair { Base = baseBone, Merge = m2.transform } }
        };
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("seams disagree", r);
    }

    [Test]
    public void RootsDontNest_refuses()
    {
        LogAssert.Expect(LogType.Error, RefuseRe);
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        var baseBone = FindBone(baseGO, "Hips");
        var stray = NewChild(_root, "StrayMerge"); // under _root, NOT under mergeGO
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution
        {
            Pairs = { new CheckSeam.BonePair { Base = baseBone, Merge = stray.transform } }
        };
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("different avatar", r);
    }

    [Test]
    public void ReflectError_refuses()
    {
        LogAssert.Expect(LogType.Error, RefuseRe);
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution { ReflectError = "GetLinks threw" };
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("GetLinks threw", r);
    }

    // ── Task 4: weighted-humanoid count + ≤1 proxy REFUSE ──────────────────────────────────────────────
    // Join on the merge side: NewSkinnedMergeable's SMR.bones[] reference the SAME Transforms the injected
    // SeamResolution's .Merge carries, so a ≥0.1 skin weight on a mapped humanoid pair makes it count.

    [Test]
    public void OneWeightedHumanoid_refusesAsProxy()
    {
        LogAssert.Expect(LogType.Error, RefuseRe);
        // Head-only hair: 1 humanoid weighted bone ⇒ offset-tolerant proxy ⇒ REFUSE.
        BuildSeam(out var baseGO, out var mergeGO, humanoid: new[] { "Head" }, weights: new[] { ("Head", 1.0f) });
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("single humanoid attachment", r);
        StringAssert.Contains("Head", r);
    }

    [Test]
    public void WeightThreshold_flipsTheCount()
    {
        LogAssert.Expect(LogType.Error, RefuseRe); // 0.09 case ⇒ 1 weighted ⇒ proxy REFUSE
        LogAssert.Expect(LogType.Error, RefuseRe); // 0.11 case ⇒ 2 weighted ⇒ gate tail ("not yet implemented")
        // 2 mapped humanoid; Spine skinned 0.09 (below threshold, dropped) ⇒ 1 weighted ⇒ REFUSE.
        BuildSeam(out var b1, out var m1, humanoid: new[] { "Chest", "Spine" },
            weights: new[] { ("Chest", 1.0f), ("Spine", 0.09f) });
        var r1 = CheckSeam.Check(Path(b1), Path(m1));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r1);
        StringAssert.Contains("single humanoid attachment", r1);
        // Bump Spine to 0.11 ⇒ kept ⇒ 2 weighted ⇒ reaches the gate (not a proxy refuse). Gate verdict is Task 5.
        BuildSeam(out var b2, out var m2, humanoid: new[] { "Chest", "Spine" },
            weights: new[] { ("Chest", 1.0f), ("Spine", 0.11f) });
        var r2 = CheckSeam.Check(Path(b2), Path(m2));
        Assert.IsFalse(r2.Contains("single humanoid attachment"),
            "0.11 keeps the bone ⇒ 2 weighted humanoid ⇒ not a proxy refuse");
    }
}
