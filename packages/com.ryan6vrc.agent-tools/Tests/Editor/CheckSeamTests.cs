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
using VRC.SDK3.Avatars.Components;

// CheckSeam proof obligations (spec 2026-07-11-checkseam-design.md).
//
// The test venue (TestEditor) is VRChat-SDK-only — Modular Avatar is ABSENT — so the two hops that need
// MA (seam classification + GetBonesMapping) are exercised through CheckSeam's internal seams, set via
// reflection exactly as CheckAvatarTests sets its seams. Everything the metric actually reasons over —
// base + mergeable bone Transforms and real SkinnedMeshRenderer weights — is built for real in a throwaway
// scene; only the (base, merge) correspondence is injected (its real source, MA GetBonesMapping, is
// validated live on the corpus, not here). Fixtures are torn down in place; nothing is saved.
public class CheckSeamTests
{
    private const string TmpDir = "Assets/AgentCheckSeamTmp";
    private string _logPath;
    private Scene _tmpScene;
    private object _origDetect, _origResolve;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // REVIEW/FAIL log a warning; refusals log an error — expected
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentCheckSeamTmp");
        // Single (not Additive): order-independent — Additive throws "untitled scene unsaved" when this
        // fixture runs first/filtered. TestEditor is a throwaway project, so replacing its scene is safe.
        _tmpScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _origDetect = GetSeam("DetectSeam");
        _origResolve = GetSeam("ResolveMapping");
    }

    [TearDown]
    public void TearDown()
    {
        SetSeam("DetectSeam", _origDetect);
        SetSeam("ResolveMapping", _origResolve);
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Seam reflection (fields are internal; set/get via reflection, like CheckAvatarTests) ──────────

    private static void SetSeam(string field, object value) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);
    private static object GetSeam(string field) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private void InjectMergeArmature(Component seamComp, List<(Transform, Transform)> pairs)
    {
        SetSeam("DetectSeam", (Func<GameObject, CheckSeam.SeamInfo>)(_ =>
            new CheckSeam.SeamInfo { Kind = CheckSeam.SeamKind.MergeArmature, Component = seamComp }));
        SetSeam("ResolveMapping", (Func<Component, List<(Transform, Transform)>>)(_ => pairs));
    }

    private void InjectSeamKind(CheckSeam.SeamKind kind)
    {
        SetSeam("DetectSeam", (Func<GameObject, CheckSeam.SeamInfo>)(_ =>
            new CheckSeam.SeamInfo { Kind = kind, Component = null }));
    }

    private string ReadLog(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, StringComparison.Ordinal);
        _logPath = i < 0 ? null : result.Substring(i + marker.Length).Trim();
        return _logPath != null && File.Exists(_logPath) ? File.ReadAllText(_logPath) : "";
    }

    // ── Fixture builders ────────────────────────────────────────────────────────────────────────────

    private static readonly string[] BoneNames = { "Hips", "Spine", "Chest", "Neck" };
    private static readonly Vector3 LocalStep = new Vector3(0, 0.20f, 0);

    private GameObject NewChild(GameObject parent, string name, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        return go;
    }

    // A nested Hips→Spine→Chest→Neck chain under `armatureRoot`; returns the four bone transforms.
    private Transform[] BuildChain(GameObject armatureRoot)
    {
        var bones = new Transform[BoneNames.Length];
        GameObject parent = armatureRoot;
        for (int i = 0; i < BoneNames.Length; i++)
        {
            var local = i == 0 ? new Vector3(0, 1.0f, 0) : LocalStep;
            parent = NewChild(parent, BoneNames[i], local);
            bones[i] = parent.transform;
        }
        return bones;
    }

    private GameObject NewBaseAvatar(out Transform[] baseBones)
    {
        var avatar = new GameObject("Base");
        avatar.AddComponent<VRCAvatarDescriptor>();
        var arm = NewChild(avatar, "Armature", Vector3.zero);
        baseBones = BuildChain(arm);
        return avatar;
    }

    // A mergeable under `avatar`: Outfit/Armature + a chain + a skinned mesh whose vertices are each fully
    // weighted to one bone (only the indices in `weightedIdx`). Returns the mergeable bones; `seamComp` is
    // the armature root (stands in for the MA component the seam would carry).
    private GameObject NewMergeable(GameObject avatar, int[] weightedIdx, out Transform[] mergeBones, out Component seamComp)
    {
        var outfit = NewChild(avatar, "Outfit", Vector3.zero);
        var arm = NewChild(outfit, "Armature", Vector3.zero);
        seamComp = arm.transform;
        mergeBones = BuildChain(arm);

        var meshGO = NewChild(outfit, "Mesh", Vector3.zero);
        var smr = meshGO.AddComponent<SkinnedMeshRenderer>();
        var mesh = new Mesh { name = "MergeMesh" };
        var verts = new Vector3[weightedIdx.Length];
        var weights = new BoneWeight[weightedIdx.Length];
        for (int k = 0; k < weightedIdx.Length; k++)
        {
            verts[k] = Vector3.zero;
            weights[k] = new BoneWeight { boneIndex0 = weightedIdx[k], weight0 = 1f };
        }
        mesh.vertices = verts;
        mesh.boneWeights = weights;
        mesh.bindposes = mergeBones.Select(_ => Matrix4x4.identity).ToArray();
        smr.bones = mergeBones;
        smr.sharedMesh = mesh;
        smr.rootBone = mergeBones[0];
        return outfit;
    }

    private static List<(Transform, Transform)> Pairs(Transform[] baseBones, Transform[] mergeBones)
    {
        var p = new List<(Transform, Transform)>();
        for (int i = 0; i < baseBones.Length; i++) p.Add((baseBones[i], mergeBones[i]));
        return p;
    }

    private static readonly int[] AllWeighted = { 0, 1, 2, 3 };

    // ── PASS ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AlignedOverlay_isPass()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones, out var seam); // coincide with base
        InjectMergeArmature(seam, Pairs(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("=> PASS", r, r);
    }

    // An offset bone that is UNWEIGHTED must not flag (weighted-restriction).
    [Test]
    public void UnweightedOffsetBone_doesNotFlag_isPass()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, new[] { 0, 1, 2 }, out var mergeBones, out var seam); // Neck (idx3) unweighted
        mergeBones[3].localPosition += new Vector3(0, 0.05f, 0);                    // shove the unweighted Neck 50mm
        InjectMergeArmature(seam, Pairs(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("=> PASS", r, "an unweighted bone's offset must not flag: " + r);
    }

    // ── REVIEW — uniform offset, root aligned (the head-swap class) ────────────────────────────────────

    [Test]
    public void UniformRegionOffset_rootAligned_isReview()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones, out var seam);
        // Shift Chest by +20mm; Neck (its child) rides along → Chest+Neck uniformly +20mm, Hips/Spine coincide.
        mergeBones[2].localPosition += new Vector3(0, 0.02f, 0);
        InjectMergeArmature(seam, Pairs(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        var log = ReadLog(r);
        StringAssert.Contains("=> REVIEW", r, r);
        StringAssert.Contains("uniform", log, "the offending region is a uniform shift: " + log);
    }

    // ── FAIL — differential (a gradient) ──────────────────────────────────────────────────────────────

    [Test]
    public void DifferentialOffset_isFail()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones, out var seam);
        // Chest +20mm, Neck an ADDITIONAL +40mm → Chest world +20, Neck world +60 → offsets clearly disagree.
        mergeBones[2].localPosition += new Vector3(0, 0.02f, 0);
        mergeBones[3].localPosition += new Vector3(0, 0.04f, 0);
        InjectMergeArmature(seam, Pairs(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        var log = ReadLog(r);
        StringAssert.Contains("=> FAIL", r, r);
        StringAssert.Contains("differential", log, "disagreeing offsets are differential: " + log);
    }

    // ── FAIL — uniform offset but the mergeable root was moved off its drop ────────────────────────────

    [Test]
    public void UniformOffset_rootMoved_isFail()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        var outfit = NewMergeable(avatar, AllWeighted, out var mergeBones, out var seam);
        outfit.transform.localPosition = new Vector3(0, 0.02f, 0); // whole mergeable shifted → uniform + root moved
        InjectMergeArmature(seam, Pairs(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        var log = ReadLog(r);
        StringAssert.Contains("=> FAIL", r, r);
        StringAssert.Contains("root-moved", log, "a uniform shift from a moved root is a FAIL: " + log);
    }

    // ── Inspection-class: no scene dirtying ────────────────────────────────────────────────────────────

    [Test]
    public void Inspect_doesNotDirtyScene()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones, out var seam);
        InjectMergeArmature(seam, Pairs(baseBones, mergeBones));
        string scenePath = TmpDir + "/CheckSeamNoDirty.unity";
        EditorSceneManager.SaveScene(_tmpScene, scenePath);
        Assert.IsFalse(_tmpScene.isDirty, "baseline must be a clean scene");
        var r = CheckSeam.Inspect("Base", "Outfit");
        ReadLog(r);
        Assert.IsFalse(EditorSceneManager.GetActiveScene().isDirty, "Inspect must not dirty a clean scene");
        AssetDatabase.DeleteAsset(scenePath);
    }

    // ── Out-of-scope routing (refuse — bare FAIL, no trailer) ──────────────────────────────────────────

    [Test]
    public void NoSeam_refusesToOwnMergeable()
    {
        var avatar = NewBaseAvatar(out _);
        NewChild(avatar, "Outfit", Vector3.zero);
        InjectSeamKind(CheckSeam.SeamKind.None);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] FAIL:"));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.StartsWith("[CheckSeam] FAIL:", r);
        StringAssert.Contains("own-mergeable", r, "a bare prefab routes to own-mergeable: " + r);
        Assert.IsFalse(r.Contains("| log="), "a refusal carries no artifact trailer: " + r);
    }

    [Test]
    public void BoneProxyProp_refusesToAnchorCheck()
    {
        var avatar = NewBaseAvatar(out _);
        NewChild(avatar, "Outfit", Vector3.zero);
        InjectSeamKind(CheckSeam.SeamKind.BoneProxy);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] FAIL:"));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("BoneProxy", r, "a BoneProxy prop routes to proxy-target check: " + r);
    }

    [Test]
    public void VrcfurySeam_refusesWithFastFollowNote()
    {
        var avatar = NewBaseAvatar(out _);
        NewChild(avatar, "Outfit", Vector3.zero);
        InjectSeamKind(CheckSeam.SeamKind.VRCFury);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] FAIL:"));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("VRCFury", r, "a VRCFury seam is v1-out-of-scope: " + r);
    }

    // ── Bad input → bare FAIL, no trailer ──────────────────────────────────────────────────────────────

    [Test]
    public void BadInput_bareFail_noTrailer()
    {
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] FAIL:"));
        var r = CheckSeam.Inspect("NoSuchBase_xyz", "NoSuchMerge_xyz");
        StringAssert.StartsWith("[CheckSeam] FAIL:", r);
        Assert.IsFalse(r.Contains("| log="), "bad input carries no artifact trailer: " + r);
    }

    [Test]
    public void BaseWithoutDescriptor_refuses()
    {
        var notAvatar = new GameObject("Base"); // no VRCAvatarDescriptor
        NewChild(notAvatar, "Armature", Vector3.zero);
        NewChild(notAvatar, "Outfit", Vector3.zero); // distinct mergeable handle, so the descriptor check is reached
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] FAIL:.*VRCAvatarDescriptor"));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("VRCAvatarDescriptor", r, r);
    }
}
