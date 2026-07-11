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
// The test venue (TestEditor) is VRChat-SDK-only — Modular Avatar and VRCFury are ABSENT — so the seam
// resolution hop (MA GetBonesMapping / VRCFury GetLinks, unioned) is exercised through CheckSeam's one
// internal seam, set via reflection exactly as CheckAvatarTests sets its seams. Everything the metric
// actually reasons over — base + mergeable bone Transforms and real SkinnedMeshRenderer weights — is
// built for real; only the (base, merge, will-snap) correspondence is injected. The real GetBonesMapping /
// GetLinks chains are validated live on the corpus (both recover the true offsets), not here. The bare-
// prefab refuse runs the REAL DefaultResolveSeam (that branch needs no MA/VRCFury types).
public class CheckSeamTests
{
    private const string TmpDir = "Assets/AgentCheckSeamTmp";
    private string _logPath;
    private Scene _tmpScene;
    private object _origResolveSeam;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // REVIEW/FAIL log a warning; refusals log an error — expected
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentCheckSeamTmp");
        // Single (not Additive): order-independent — Additive throws "untitled scene unsaved" when this
        // fixture runs first/filtered. TestEditor is a throwaway project, so replacing its scene is safe.
        _tmpScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _origResolveSeam = GetSeam("ResolveSeam");
    }

    [TearDown]
    public void TearDown()
    {
        SetSeam("ResolveSeam", _origResolveSeam);
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Seam reflection (field is internal; set/get via reflection, like CheckAvatarTests) ────────────

    private static void SetSeam(string field, object value) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);
    private static object GetSeam(string field) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private void InjectSeam(CheckSeam.SeamKind kind, List<CheckSeam.SeamBone> bones, int links = 1)
    {
        SetSeam("ResolveSeam", (Func<GameObject, GameObject, CheckSeam.Seam>)((b, m) =>
            new CheckSeam.Seam { Kind = kind, Bones = bones, LinkCount = links }));
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
        baseBones = BuildChain(NewChild(avatar, "Armature", Vector3.zero));
        return avatar;
    }

    // A mergeable under `parent`: Outfit/Armature + chain + a skinned mesh whose vertices are each fully
    // weighted to one bone (only the indices in `weightedIdx`). No seam component — correspondence is injected.
    private GameObject NewMergeable(GameObject parent, int[] weightedIdx, out Transform[] mergeBones)
    {
        var outfit = NewChild(parent, "Outfit", Vector3.zero);
        var arm = NewChild(outfit, "Armature", Vector3.zero);
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

    private static List<CheckSeam.SeamBone> Bones(Transform[] baseBones, Transform[] mergeBones, bool willSnap = false)
    {
        var l = new List<CheckSeam.SeamBone>();
        for (int i = 0; i < baseBones.Length; i++)
            l.Add(new CheckSeam.SeamBone { Base = baseBones[i], Merge = mergeBones[i], WillSnap = willSnap });
        return l;
    }

    private static readonly int[] AllWeighted = { 0, 1, 2, 3 };

    // ── PASS ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AlignedOverlay_isPass()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones);
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(baseBones, mergeBones));
        StringAssert.Contains("=> PASS", CheckSeam.Inspect("Base", "Outfit"));
    }

    // A VRCFury link with offsets kept (no snap) scores exactly like MA.
    [Test]
    public void VrcfuryOffsetsKept_scoresLikeMA_isPass()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones);
        InjectSeam(CheckSeam.SeamKind.VRCFury, Bones(baseBones, mergeBones, willSnap: false));
        StringAssert.Contains("=> PASS", CheckSeam.Inspect("Base", "Outfit"));
    }

    [Test]
    public void UnweightedOffsetBone_doesNotFlag_isPass()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, new[] { 0, 1, 2 }, out var mergeBones); // Neck (idx3) unweighted
        mergeBones[3].localPosition += new Vector3(0, 0.05f, 0);     // shove the unweighted Neck 50mm
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(baseBones, mergeBones));
        StringAssert.Contains("=> PASS", CheckSeam.Inspect("Base", "Outfit"));
    }

    // ── REVIEW ─────────────────────────────────────────────────────────────────────────────────────────

    // Uniform offset, root aligned (the head-swap class).
    [Test]
    public void UniformRegionOffset_rootAligned_isReview()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones);
        mergeBones[2].localPosition += new Vector3(0, 0.02f, 0); // Chest+Neck ride +20mm; Hips/Spine coincide
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("=> REVIEW", r, r);
        StringAssert.Contains("uniform", ReadLog(r));
    }

    // A VRCFury will-snap bone: the build moves it, so the edit-time delta can't be certified → REVIEW.
    [Test]
    public void VrcfuryWillSnap_isReview()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones);
        mergeBones[3].localPosition += new Vector3(0, 0.05f, 0); // an offset that the build will snap away
        InjectSeam(CheckSeam.SeamKind.VRCFury, Bones(baseBones, mergeBones, willSnap: true));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("=> REVIEW", r, r);
        StringAssert.Contains("vrcfury-snap", ReadLog(r));
    }

    // ── FAIL ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void DifferentialOffset_isFail()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones);
        mergeBones[2].localPosition += new Vector3(0, 0.02f, 0); // Chest world +20
        mergeBones[3].localPosition += new Vector3(0, 0.04f, 0); // Neck world +60 → offsets disagree
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("=> FAIL", r, r);
        StringAssert.Contains("differential", ReadLog(r));
    }

    [Test]
    public void UniformOffset_rootMoved_isFail()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        var outfit = NewMergeable(avatar, AllWeighted, out var mergeBones);
        outfit.transform.localPosition = new Vector3(0, 0.02f, 0); // whole mergeable shifted → uniform + root moved
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("=> FAIL", r, r);
        StringAssert.Contains("root-moved", ReadLog(r));
    }

    // A uniform offset from an intermediate offset CONTAINER (mergeable root at identity-local but world-
    // displaced) is a wrong drop → FAIL. Guards the world-space (not parent-local) root-alignment check.
    [Test]
    public void UniformOffset_intermediateContainer_isFail()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        var container = NewChild(avatar, "Container", new Vector3(0, 0.03f, 0));
        NewMergeable(container, AllWeighted, out var mergeBones); // Outfit under Container, local identity
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(baseBones, mergeBones));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("=> FAIL", r, r);
        StringAssert.Contains("root-moved", ReadLog(r));
    }

    // ── refuse ─────────────────────────────────────────────────────────────────────────────────────────

    // The seam's merge target resolving to ANOTHER avatar's rig must refuse, not silently score B's bones.
    [Test]
    public void SeamTargetsDifferentBase_refuses()
    {
        var avatar = NewBaseAvatar(out _);
        NewMergeable(avatar, AllWeighted, out var mergeBones);
        var other = new GameObject("OtherBase");
        other.AddComponent<VRCAvatarDescriptor>();
        var otherBones = BuildChain(NewChild(other, "Armature", Vector3.zero));
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(otherBones, mergeBones)); // base side belongs to OtherBase
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] FAIL:"));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.Contains("OUTSIDE the passed base", r, r);
    }

    // Bare prefab (no seam component) → the REAL DefaultResolveSeam refuses to own-mergeable (no injection).
    [Test]
    public void NoSeam_refusesToOwnMergeable()
    {
        var avatar = NewBaseAvatar(out _);
        NewChild(avatar, "Outfit", Vector3.zero); // a bare object, no seam component
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] FAIL:"));
        var r = CheckSeam.Inspect("Base", "Outfit");
        StringAssert.StartsWith("[CheckSeam] FAIL:", r);
        StringAssert.Contains("own-mergeable", r, r);
        Assert.IsFalse(r.Contains("| log="), "a refusal carries no artifact trailer: " + r);
    }

    // ── Inspection-class: no scene dirtying ────────────────────────────────────────────────────────────

    [Test]
    public void Inspect_doesNotDirtyScene()
    {
        var avatar = NewBaseAvatar(out var baseBones);
        NewMergeable(avatar, AllWeighted, out var mergeBones);
        InjectSeam(CheckSeam.SeamKind.MergeArmature, Bones(baseBones, mergeBones));
        string scenePath = TmpDir + "/CheckSeamNoDirty.unity";
        EditorSceneManager.SaveScene(_tmpScene, scenePath);
        Assert.IsFalse(_tmpScene.isDirty, "baseline must be a clean scene");
        ReadLog(CheckSeam.Inspect("Base", "Outfit"));
        Assert.IsFalse(EditorSceneManager.GetActiveScene().isDirty, "Inspect must not dirty a clean scene");
        AssetDatabase.DeleteAsset(scenePath);
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
