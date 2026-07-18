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
        // dirty, so additive is order-dependent — never allowed when a test runs in isolation. Use a Single
        // throwaway scene (CheckAvatarTests uses the same pattern): our fixtures live in it (the active scene,
        // which is exactly what CheckSeam.Resolve searches). Never saved.
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

    // Build one SkinnedMeshRenderer under mergeGO. Each (bone, weight) in `weighted` gets one vertex binding it
    // at influence-0 with that weight; `extraBones` are appended to bones[] (so they count as smrBones for leaf
    // detection) but carry no vertex weight. bones[] ∥ bindposes[]. One SMR is enough — leaf/weight reads sweep
    // every mergeable SMR, so a single one holding the whole skeleton mirrors the multi-SMR union.
    private void AttachSkin(GameObject mergeGO, (Transform bone, float weight)[] weighted, Transform[] extraBones)
    {
        extraBones ??= Array.Empty<Transform>();
        var allBones = weighted.Select(w => w.bone).Concat(extraBones).ToArray();
        int nv = weighted.Length;
        var verts = new Vector3[nv];
        var bw = new BoneWeight[nv];
        for (int i = 0; i < nv; i++)
        {
            verts[i] = Vector3.zero;
            bw[i] = new BoneWeight { boneIndex0 = i, weight0 = weighted[i].weight };
        }
        var bindposes = new Matrix4x4[allBones.Length];
        for (int i = 0; i < allBones.Length; i++) bindposes[i] = Matrix4x4.identity;
        var mesh = new Mesh { vertices = verts };
        mesh.boneWeights = bw;
        mesh.bindposes = bindposes;
        var smr = NewChild(mergeGO, "Skin").AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = allBones;
    }

    // Attach a SkinnedMeshRenderer whose mesh carries EXPLICIT per-vertex influences (via SetBoneWeights),
    // exercising MaxWeights' all-influence read. Weights within a vertex must be sorted descending. Unity's mesh
    // store QUANTIZES the weights (16-bit), so a read value drifts ~1e-5 from what was set — make each vertex sum
    // to 1 (so no per-vertex renormalization compounds it) AND keep any threshold-sensitive weight clear of the
    // 0.1 gate boundary, or the quantization drift can flip the comparison. bones[] indices are what
    // BoneWeight1.boneIndex references; bindposes ∥ bones.
    private void AttachExplicitSkin(GameObject mergeGO, Transform[] bones, byte[] bonesPerVertex, BoneWeight1[] bw)
    {
        var verts = new Vector3[bonesPerVertex.Length];
        var mesh = new Mesh { vertices = verts };
        var bpvNa = new Unity.Collections.NativeArray<byte>(bonesPerVertex, Unity.Collections.Allocator.Temp);
        var bwNa = new Unity.Collections.NativeArray<BoneWeight1>(bw, Unity.Collections.Allocator.Temp);
        mesh.SetBoneWeights(bpvNa, bwNa);
        bpvNa.Dispose(); bwNa.Dispose();
        var bindposes = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++) bindposes[i] = Matrix4x4.identity;
        mesh.bindposes = bindposes;
        var smr = NewChild(mergeGO, "Skin").AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
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
        // no-humanoid is a valid-abstain REFUSE ⇒ warning (RefuseAbstain → Debug.LogWarning).
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] REFUSE:"));
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

    // G26: zero pairs with NO BoneProxy present ⇒ genuinely bare prop ⇒ route to own-mergeable.
    [Test]
    public void NoSeam_refusesAsBareProp()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // no seam ⇒ valid-abstain ⇒ warning
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution(); // empty Pairs, no BoneProxy on Merge
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("no seam component", r);
        StringAssert.Contains("bare prop", r); // names the route (own-mergeable), opposite the proxy route
    }

    // G26: zero pairs BUT a ModularAvatar BoneProxy present ⇒ offset-tolerant anchor attachment, NOT bare.
    // The skill routes oppositely on these two — one string for both was the conflation the finding names.
    [Test]
    public void ZeroPairs_withBoneProxy_refusesAsProxy_notBareProp()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // proxy abstain ⇒ warning
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        var bpType = Resolve("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
        Assert.IsNotNull(bpType, "TestEditor must have Modular Avatar installed (setup-test-editor copies it)");
        mergeGO.AddComponent(bpType); // a BoneProxy maps no humanoid bones ⇒ zero scorable pairs
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution();
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("bone-proxy attachment", r);
        StringAssert.Contains("verify the baked result", r);
        StringAssert.DoesNotContain("bare prop", r); // must NOT be routed out as bare
    }

    [Test]
    public void SeamConflict_refuses()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // seams disagree ⇒ valid-abstain ⇒ warning
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
        // Two same-base-bone merges are disambiguated by hierarchy path, not bare name.
        StringAssert.Contains(Path(m1), r);
        StringAssert.Contains(Path(m2), r);
    }

    [Test]
    public void RootsDontNest_refuses()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // roots don't nest ⇒ valid-abstain ⇒ warning
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

    // ── Reflection failure classification: genuine API drift vs a seam that can't resolve onto THIS base ──
    // DefaultResolveSeam's unwrap/classify branch can't run here (no MA/VRCFury to throw); these prove the
    // door routes the two carrier fields to the right severity. Drift = tool broken vs installed package
    // (misuse → error); Unresolvable = valid asset the gate can't certify against this base (abstain → warning).

    [Test]
    public void ReflectDrift_refusesAtError()
    {
        LogAssert.Expect(LogType.Error, RefuseRe); // genuine API drift ⇒ misuse ⇒ error
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution { ReflectError = "MissingMethodException: X" };
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("MissingMethodException: X", r);
    }

    [Test]
    public void UnresolvableSeam_refusesAtWarning()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // seam present but can't resolve onto this base ⇒ abstain ⇒ warning
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution
        {
            UnresolvableReason = "seam present but does not resolve onto this base: InvalidOperationException: boom"
        };
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("does not resolve onto this base", r);
    }

    // ── Task 7: VRCFury scale-bake REFUSE + severity split ──────────────────────────────────────────────
    // The real scale read (forceOneWorldScale / GetScalingFactor) is proven live (Task 8); here inject a
    // SeamResolution carrying ScaleBakeReason and prove the door REFUSEs on it at WARNING (valid-abstain
    // severity, not misuse), distinguishing it from the root-not-found / reflect-error misuse paths.

    [Test]
    public void ScaleBake_refusesAtWarning()
    {
        SetupBaseAndHumanoid(out var baseGO, out var mergeGO, humanoidBones: 3);
        CheckSeam.ResolveSeam = (_, __) => new CheckSeam.SeamResolution { ScaleBakeReason = "scaled at bake — test" };
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("REFUSE: scaled at bake"));
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("scaled at bake", r);
    }

    // ── Task 4: weighted-humanoid count + ≤1 proxy REFUSE ──────────────────────────────────────────────
    // Join on the merge side: NewSkinnedMergeable's SMR.bones[] reference the SAME Transforms the injected
    // SeamResolution's .Merge carries, so a ≥0.1 skin weight on a mapped humanoid pair makes it count.

    [Test]
    public void OneWeightedHumanoid_refusesAsProxy()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // ≤1 proxy ⇒ valid-abstain ⇒ warning
        // Head-only hair: 1 humanoid weighted bone ⇒ offset-tolerant proxy ⇒ REFUSE.
        BuildSeam(out var baseGO, out var mergeGO, humanoid: new[] { "Head" }, weights: new[] { ("Head", 1.0f) });
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("single humanoid attachment", r);
        StringAssert.Contains("Head", r);
    }

    // Regression: MA and VRCFury can each contribute the SAME (Base,Merge) pair. The conflict guard keeps an
    // identical duplicate (it only rejects a base mapped to two DIFFERENT merges), so a genuine single-bone
    // proxy was counted twice — reaching the ≥2 gate and PASSing an asset that must hit the ≤1 proxy REFUSE.
    // The reference-identity dedupe collapses it to 1. Without the fix this returns PASS; with it, REFUSE.
    [Test]
    public void DuplicatePair_stillCountsOnce_refusesProxy()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // collapses to 1 ⇒ proxy REFUSE (abstain ⇒ warning)
        var baseGO = NewChild(_root, "Base");
        var mergeGO = NewChild(_root, "Merge");
        var bHead = NewChild(baseGO, "Head").transform;
        var map = new CheckSeam.HumanoidMap { SpanMm = 350f };
        map.Bones.Add(bHead);
        CheckSeam.ResolveHumanoid = _ => map;

        var mHead = NewChild(mergeGO, "Head").transform;
        AttachSkin(mergeGO, new[] { (mHead, 1.0f) }, null); // weighted ≥0.1 ⇒ weighted humanoid

        // The SAME (bHead, mHead) pair twice — same Transform instances, as MA∪VRCFury would produce.
        var seam = new CheckSeam.SeamResolution();
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bHead, Merge = mHead });
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bHead, Merge = mHead });
        CheckSeam.ResolveSeam = (_, __) => seam;

        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("single humanoid attachment", r); // count collapses to 1, NOT a ≥2 gate PASS
        StringAssert.Contains("Head", r);
    }

    [Test]
    public void WeightThreshold_flipsTheCount()
    {
        LogAssert.Expect(LogType.Warning, RefuseRe); // 0.09 case ⇒ 1 weighted ⇒ proxy REFUSE (abstain ⇒ warning)
        // Spine shares its vertex with a filler bone at the complementary weight (a realistic normalized skin —
        // GetAllBoneWeights returns the normalized per-vertex view, so a lone sub-1 influence would read as 1.0).
        // 0.09 case: Spine is 9% of its vertex, below the 0.1 threshold ⇒ dropped ⇒ 1 weighted ⇒ proxy REFUSE.
        BuildThresholdFixture(0.09f, out var b1, out var m1);
        var r1 = CheckSeam.Check(Path(b1), Path(m1));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r1);
        StringAssert.Contains("single humanoid attachment", r1);

        // Isolate the second fixture: it shares the "Base"/"Merge" hierarchy path, so Resolve(Path(b2)) would
        // hit b1's base (and REFUSE "different avatar" before the count logic — a vacuous pass) unless b1 is
        // gone. Destroy the first fixture, then 0.11 keeps Spine ⇒ 2 weighted ⇒ gate ⇒ coincident ⇒ PASS. This
        // POSITIVELY exercises "0.11 flips 1→2 and reaches the gate".
        UnityEngine.Object.DestroyImmediate(b1);
        UnityEngine.Object.DestroyImmediate(m1);

        BuildThresholdFixture(0.11f, out var b2, out var m2);
        var r2 = CheckSeam.Check(Path(b2), Path(m2));
        StringAssert.Contains("weightedHumanoid=2", r2);
        StringAssert.Contains("=> PASS", r2);
        ReadLog(r2); // sets _logPath so TearDown cleans the PASS RunLog
    }

    // Chest+Spine humanoid base/merge (coincident at origin); one filler bone. Mesh: v0 = Chest at 1.0; v1 =
    // Spine at spineWeight sharing with the filler at (1−spineWeight) so the normalized Spine share == spineWeight.
    private void BuildThresholdFixture(float spineWeight, out GameObject baseGO, out GameObject mergeGO)
    {
        baseGO = NewChild(_root, "Base");
        mergeGO = NewChild(_root, "Merge");
        var bChest = NewChild(baseGO, "Chest").transform;
        var bSpine = NewChild(baseGO, "Spine").transform;
        var map = new CheckSeam.HumanoidMap { SpanMm = 350f };
        map.Bones.Add(bChest); map.Bones.Add(bSpine);
        CheckSeam.ResolveHumanoid = _ => map;

        var mChest = NewChild(mergeGO, "Chest").transform;
        var mSpine = NewChild(mergeGO, "Spine").transform;
        var mFill = NewChild(mergeGO, "Fill").transform; // non-humanoid, not a seam pair — weight-source only
        var bones = new[] { mChest, mSpine, mFill };      // boneIndex 0,1,2
        // v1 influences sorted descending: filler (1−w) then Spine (w) — requires w ≤ 0.5 (true for 0.09/0.11).
        AttachExplicitSkin(mergeGO, bones,
            new byte[] { 1, 2 },
            new[]
            {
                new BoneWeight1 { boneIndex = 0, weight = 1.0f },              // v0: Chest
                new BoneWeight1 { boneIndex = 2, weight = 1.0f - spineWeight },// v1: filler
                new BoneWeight1 { boneIndex = 1, weight = spineWeight },       // v1: Spine
            });

        var seam = new CheckSeam.SeamResolution();
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bChest, Merge = mChest });
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bSpine, Merge = mSpine });
        CheckSeam.ResolveSeam = (_, __) => seam;
    }

    // Regression: mesh.boneWeights (legacy) exposes only the top-4 influences per vertex, so a humanoid bone at
    // ≥0.1 that never lands in a vertex's top 4 is dropped — flipping the count (here 2→1, a false proxy REFUSE).
    // MaxWeights now reads GetAllBoneWeights()/GetBonesPerVertex(). Fixture: a vertex with 5 influences where the
    // 5th (lowest) is Spine at 0.1; the old top-4 read would drop it, leaving count=1; the all-influence read
    // keeps it ⇒ count=2 ⇒ reaches the gate ⇒ coincident ⇒ PASS.
    [Test]
    public void MaxWeights_countsInfluenceBeyondTop4()
    {
        var baseGO = NewChild(_root, "Base");
        var mergeGO = NewChild(_root, "Merge");
        var bChest = NewChild(baseGO, "Chest").transform;
        var bSpine = NewChild(baseGO, "Spine").transform;
        var map = new CheckSeam.HumanoidMap { SpanMm = 350f };
        map.Bones.Add(bChest); map.Bones.Add(bSpine);
        CheckSeam.ResolveHumanoid = _ => map;

        // Merge bones: Chest, Spine (humanoid) + four fillers to crowd out Spine past the top-4 on one vertex.
        var mChest = NewChild(mergeGO, "Chest").transform;
        var mSpine = NewChild(mergeGO, "Spine").transform;
        var f1 = NewChild(mergeGO, "F1").transform;
        var f2 = NewChild(mergeGO, "F2").transform;
        var f3 = NewChild(mergeGO, "F3").transform;
        var f4 = NewChild(mergeGO, "F4").transform;
        var bones = new[] { mChest, mSpine, f1, f2, f3, f4 }; // boneIndex 0..5

        // Vertex 0: Chest at 1.0 (1 influence). Vertex 1: 5 influences sorted descending, Spine (idx 1) is the
        // 5th/lowest at 0.12 — outside the legacy top-4 window (F1..F4 all outrank it). Spine sits 0.02 ABOVE the
        // 0.1 gate threshold (not on it) so 16-bit weight quantization can't drift the read below 0.1 and flip
        // the count. v1 sums to 1.0 so no per-vertex renormalization compounds the drift.
        AttachExplicitSkin(mergeGO, bones,
            new byte[] { 1, 5 },
            new[]
            {
                new BoneWeight1 { boneIndex = 0, weight = 1.0f },  // v0: Chest
                new BoneWeight1 { boneIndex = 2, weight = 0.40f }, // v1: F1
                new BoneWeight1 { boneIndex = 3, weight = 0.20f }, // v1: F2
                new BoneWeight1 { boneIndex = 4, weight = 0.15f }, // v1: F3
                new BoneWeight1 { boneIndex = 5, weight = 0.13f }, // v1: F4
                new BoneWeight1 { boneIndex = 1, weight = 0.12f }, // v1: Spine — 5th, dropped by a top-4 read
            });

        var seam = new CheckSeam.SeamResolution();
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bChest, Merge = mChest });
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bSpine, Merge = mSpine });
        CheckSeam.ResolveSeam = (_, __) => seam;

        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("weightedHumanoid=2", r); // the 5th influence (Spine@0.12) participates
        StringAssert.Contains("=> PASS", r);
        ReadLog(r);
    }

    // ── Task 5: the ε coincidence gate — PASS / NOT-PASS + offenders ────────────────────────────────────
    // Fixtures put base bones and merge bones both at their root origin (localPosition 0) ⇒ coincident ⇒
    // delta 0; move a merge bone's localPosition to inject a known world offset. SpanMm=350 (BuildSeam) ⇒
    // ε = max(0.5, 0.003·350) = 1.05mm.

    [Test]
    public void Gate_coincident_pass()
    {
        BuildSeam(out var baseGO, out var mergeGO, humanoid: new[] { "Chest", "Spine" },
            weights: new[] { ("Chest", 1.0f), ("Spine", 1.0f) });
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> PASS", r);
        StringAssert.Contains("| log=", r);
        StringAssert.Contains("weightedHumanoid=2", r);
        StringAssert.Contains("offenders=0", r);
        StringAssert.Contains("context=0 dropped=0", r);
        StringAssert.DoesNotContain("maxOffset", r); // G37: PASS carries no offender magnitude
        StringAssert.Contains("maxWithinEps=", r);   // PASS surfaces the sub-ε band on the one-liner
        var body = ReadLog(r);
        StringAssert.Contains("_(all within ε)_", body);
        StringAssert.Contains("maxWithinEps=", body); // and in the RunLog body
    }

    [Test]
    public void Gate_offset_notPass()
    {
        BuildSeam(out var baseGO, out var mergeGO, humanoid: new[] { "Chest", "Spine" },
            weights: new[] { ("Chest", 1.0f), ("Spine", 1.0f) });
        // ε = 1.05mm; offset Chest by 1.7mm (0.0017 world units) — still > ε ⇒ one offender.
        FindBone(mergeGO, "Chest").localPosition = new Vector3(0.0017f, 0f, 0f);
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> NOT-PASS", r);
        StringAssert.Contains("offenders=1", r);
        StringAssert.Contains("maxOffset=1.7mm", r); // G37: worst-offender magnitude on the one-liner
        var body = ReadLog(r);
        StringAssert.Contains("**seam-offset**", body);
        StringAssert.Contains("bone=`Chest`", body);
    }

    [Test]
    public void Gate_boundary()
    {
        // ε = 1.05mm. One fixture, one bone (a second fixture would collide on the "Base"/"Merge" hierarchy
        // path and Resolve would return the first): at 0.6mm (< ε) → PASS; nudged to 1.2mm (> ε) → NOT-PASS.
        BuildSeam(out var baseGO, out var mergeGO, humanoid: new[] { "Chest", "Spine" },
            weights: new[] { ("Chest", 1.0f), ("Spine", 1.0f) });
        var chest = FindBone(mergeGO, "Chest");

        chest.localPosition = new Vector3(0.0006f, 0f, 0f); // 0.6mm < ε
        var r1 = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> PASS", r1);
        ReadLog(r1); // sets _logPath; delete now so both boundary logs are cleaned (TearDown handles r2)
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);

        chest.localPosition = new Vector3(0.0012f, 0f, 0f); // 1.2mm > ε
        var r2 = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> NOT-PASS", r2);
        ReadLog(r2);
    }

    // ── Task 6: non-humanoid handling — leaf drop + ungated context, never touching the verdict ─────────
    // The gate sees two coincident weighted humanoid bones (→ PASS). A weighted non-humanoid NON-leaf bone
    // (a child bone in SMR.bones[] ⇒ non-leaf) offset 50mm becomes ungated CONTEXT; a weighted non-humanoid
    // LEAF bone (no child in SMR.bones[]) offset 30mm is DROPPED (count only). Neither shifts PASS.

    [Test]
    public void NonHumanoid_leafDropped_contextUngated()
    {
        var baseGO = NewChild(_root, "Base");
        var mergeGO = NewChild(_root, "Merge");

        // Base anchors (all at origin): two humanoid (Chest/Spine) + two non-humanoid the deltas measure to.
        var bChest = NewChild(baseGO, "Chest").transform;
        var bSpine = NewChild(baseGO, "Spine").transform;
        var bBreast = NewChild(baseGO, "Breast_L_Root").transform; // non-humanoid base anchor (context)
        var bTail = NewChild(baseGO, "Tail_End").transform;        // non-humanoid base anchor (dropped)

        var map = new CheckSeam.HumanoidMap { SpanMm = 350f };
        map.Bones.Add(bChest); map.Bones.Add(bSpine); // ONLY these two count as humanoid
        CheckSeam.ResolveHumanoid = _ => map;

        // Merge bones. Humanoid pairs coincident (delta 0 ⇒ gate PASS). Breast_L_Root is non-leaf (its child
        // Breast_L_Tip is in SMR.bones[]) offset 50mm ⇒ context. Tail_End is a leaf offset 30mm ⇒ dropped.
        var mChest = NewChild(mergeGO, "Chest").transform;
        var mSpine = NewChild(mergeGO, "Spine").transform;
        var mBreast = NewChild(mergeGO, "Breast_L_Root").transform;
        var mBreastTip = NewChild(mBreast.gameObject, "Breast_L_Tip").transform; // child in smrBones ⇒ non-leaf
        var mTail = NewChild(mergeGO, "Tail_End").transform;
        mBreast.localPosition = new Vector3(0.05f, 0f, 0f); // 50mm ⇒ context delta
        mTail.localPosition = new Vector3(0.03f, 0f, 0f);   // 30mm (irrelevant: leaf drops, no delta reported)

        AttachSkin(mergeGO,
            new[] { (mChest, 1.0f), (mSpine, 1.0f), (mBreast, 1.0f), (mTail, 1.0f) },
            new[] { mBreastTip }); // Breast_L_Tip: in bones[] (⇒ parent non-leaf), no vertex weight of its own

        var seam = new CheckSeam.SeamResolution();
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bChest, Merge = mChest });
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bSpine, Merge = mSpine });
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bBreast, Merge = mBreast });
        seam.Pairs.Add(new CheckSeam.BonePair { Base = bTail, Merge = mTail });
        CheckSeam.ResolveSeam = (_, __) => seam;

        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("weightedHumanoid=2", r);
        StringAssert.Contains("offenders=0", r);
        StringAssert.Contains("context=1 dropped=1", r);
        StringAssert.Contains("=> PASS", r); // a 50mm context delta NEVER flips the verdict

        var body = ReadLog(r);
        StringAssert.Contains("## Context", body);
        StringAssert.Contains("bone=`Breast_L_Root`", body);
        StringAssert.Contains("offset=50.0mm", body);
        StringAssert.Contains("Dropped: 1 non-humanoid end-bones", body);
        Assert.IsFalse(body.Contains("**seam-offset**"), "context/dropped bones are never gate offenders");
        Assert.IsFalse(body.Contains("bone=`Tail_End`"), "a dropped leaf is neither offender nor context");
    }
}
