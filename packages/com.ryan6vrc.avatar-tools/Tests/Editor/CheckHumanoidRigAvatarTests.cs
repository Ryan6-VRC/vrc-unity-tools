using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using Ryan6Vrc.AvatarTools.Editor;

// CheckHumanoidRig.InspectAvatar proof obligations: the composed-avatar divergence a PLACED rig can carry
// that Run() (one FBX, bind-vs-geometry) cannot see — a humanoid bone mapped to an unskinned "proxy"
// transform, and a "name-shadow" decoy that makes a plain name lookup silently wrong.
//
// Fixtures build a REAL runtime humanoid Avatar via AvatarBuilder.BuildHumanAvatar — never an asset on
// disk, which is exactly the "generated/standalone Avatar, no ModelImporter" shape InspectAvatar treats as
// normal, not a failure — so Animator.GetBoneTransform resolves for real, the same call the door makes.
// Every fixture object is discarded by the next test's NewScene(Single); nothing here is DestroyImmediate'd
// mid-test and no SerializedObject/SerializedProperty write touches anything that will be (docs/verify.md
// "Test venue — NUnit vs execute_code") — plain C# property sets (`animator.avatar = …`) and constructor
// calls only, so there is nothing for that hazard to reach.
public class CheckHumanoidRigAvatarTests
{
    private string _logPath;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // CLASSIFY/FAIL log at warning/error — expected here
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        LogAssert.ignoreFailingMessages = false;
    }

    private string ReadLog(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, StringComparison.Ordinal);
        _logPath = i < 0 ? null : result.Substring(i + marker.Length).Trim();
        return _logPath != null && System.IO.File.Exists(_logPath) ? System.IO.File.ReadAllText(_logPath) : "";
    }

    private static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    // The 15 humanoid bones Mecanim requires to build a valid Avatar — the fixture standing in for a real
    // model's skeleton, built anatomically so the hierarchy makes sense to AvatarBuilder.
    private static readonly string[] RoleOrder =
    {
        "Hips", "Spine", "Head", "LeftUpperArm", "LeftLowerArm", "LeftHand",
        "RightUpperArm", "RightLowerArm", "RightHand", "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
        "RightUpperLeg", "RightLowerLeg", "RightFoot",
    };

    // Builds avatarRoot (VRCAvatarDescriptor + Animator, humanoid Avatar) -> Hips -> {Spine -> Head/arms,
    // legs}. `rename` overrides one required bone's transform NAME while the humanoid mapping still points
    // at that SAME transform by boneName — the shape a rig authored against a renamed node is (Head mapped
    // to a transform actually named "Head_Proxy"). `extra` lets a test graft additional, unmapped
    // transforms (a name-shadow decoy, a skinned proxy-candidate sibling) onto the built `nodes` map before
    // the skin + Animator + Avatar are built; `extraSkinRoles` names which of those extra `nodes` entries
    // (by key) also get woven into the one SkinnedMeshRenderer, alongside every required bone not listed in
    // `skipSkinningRoles`.
    private GameObject BuildHumanoidAvatar(string avatarName,
        Dictionary<HumanBodyBones, string> rename = null,
        HashSet<string> skipSkinningRoles = null,
        Action<Dictionary<string, GameObject>> extra = null,
        List<string> extraSkinRoles = null)
    {
        rename = rename ?? new Dictionary<HumanBodyBones, string>();
        skipSkinningRoles = skipSkinningRoles ?? new HashSet<string>();
        var avatar = new GameObject(avatarName);
        avatar.AddComponent<VRCAvatarDescriptor>();

        var nodes = new Dictionary<string, GameObject>();
        GameObject Make(string role, GameObject parent)
        {
            string name = rename.TryGetValue((HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), role), out var rn) ? rn : role;
            var go = Child(parent, name);
            nodes[role] = go;
            return go;
        }

        var hips = Make("Hips", avatar);
        var spine = Make("Spine", hips);
        Make("Head", spine);
        var lUpArm = Make("LeftUpperArm", spine);
        var lLoArm = Make("LeftLowerArm", lUpArm);
        Make("LeftHand", lLoArm);
        var rUpArm = Make("RightUpperArm", spine);
        var rLoArm = Make("RightLowerArm", rUpArm);
        Make("RightHand", rLoArm);
        var lUpLeg = Make("LeftUpperLeg", hips);
        var lLoLeg = Make("LeftLowerLeg", lUpLeg);
        Make("LeftFoot", lLoLeg);
        var rUpLeg = Make("RightUpperLeg", hips);
        var rLoLeg = Make("RightLowerLeg", rUpLeg);
        Make("RightFoot", rLoLeg);

        extra?.Invoke(nodes); // may add further entries to `nodes` (decoys, candidates)

        var skinBones = new List<Transform>();
        foreach (var role in RoleOrder)
            if (!skipSkinningRoles.Contains(role)) skinBones.Add(nodes[role].transform);
        if (extraSkinRoles != null)
            foreach (var role in extraSkinRoles) skinBones.Add(nodes[role].transform);
        AttachSkin(avatar, skinBones.ToArray());

        var animator = avatar.AddComponent<Animator>();
        animator.avatar = BuildAvatar(avatar, nodes);
        return avatar;
    }

    // One SkinnedMeshRenderer weighting each given bone at index i, full weight — mirrors
    // CheckSeamLiveTests.AttachSkin, the repo's precedent for a throwaway weighted-skin fixture.
    private static void AttachSkin(GameObject parent, Transform[] bones)
    {
        int n = bones.Length;
        var verts = new Vector3[n];
        var bw = new BoneWeight[n];
        var bp = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            verts[i] = Vector3.zero;
            bw[i] = new BoneWeight { boneIndex0 = i, weight0 = 1f };
            bp[i] = Matrix4x4.identity;
        }
        var mesh = new Mesh { vertices = verts };
        mesh.boneWeights = bw;
        mesh.bindposes = bp;
        var smr = Child(parent, "Skin").AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
    }

    // AvatarBuilder.BuildHumanAvatar over the fixture hierarchy: human[] maps each of the 15 required
    // humanoid names to its ACTUAL current transform name (post-rename); skeleton[] carries every
    // transform's current local pose, root-first — what a real ModelImporter stores, reduced to a fixture.
    private static Avatar BuildAvatar(GameObject root, Dictionary<string, GameObject> nodes)
    {
        var human = new List<HumanBone>();
        foreach (var role in RoleOrder)
            human.Add(new HumanBone { humanName = role, boneName = nodes[role].name, limit = new HumanLimit { useDefaultValues = true } });

        var skeleton = new List<SkeletonBone>();
        void Walk(Transform t)
        {
            skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            foreach (Transform c in t) Walk(c);
        }
        Walk(root.transform);

        var hd = new HumanDescription
        {
            human = human.ToArray(),
            skeleton = skeleton.ToArray(),
            upperArmTwist = 0.5f,
            lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f,
            lowerLegTwist = 0.5f,
            armStretch = 0.05f,
            legStretch = 0.05f,
            feetSpacing = 0f,
            hasTranslationDoF = false,
        };
        var avatar = AvatarBuilder.BuildHumanAvatar(root, hd);
        Assert.IsNotNull(avatar, "AvatarBuilder.BuildHumanAvatar returned null — fixture/API drift, not a test bug");
        Assert.IsTrue(avatar.isValid, "built Avatar is not valid — fixture/API drift, not a test bug");
        Assert.IsTrue(avatar.isHuman, "built Avatar is not humanoid — fixture/API drift, not a test bug");
        return avatar;
    }

    // ── PASS ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void CleanRig_allSkinned_isPass()
    {
        BuildHumanoidAvatar("CleanAvatar");
        var r = CheckHumanoidRig.InspectAvatar("CleanAvatar");
        StringAssert.Contains("bones=15 proxy=0 nameShadow=0", r, r);
        StringAssert.Contains("=> PASS", r, r);
        var log = ReadLog(r);
        StringAssert.Contains("scan scope:", log, "the zero-count scope note must be present even on PASS: " + log);
    }

    // ── proxy: a humanoid-mapped transform no renderer weights ─────────────────────────────────────────

    [Test]
    public void ProxyMapping_unskinnedTransform_isClassifyWithProxyRow()
    {
        var rename = new Dictionary<HumanBodyBones, string> { { HumanBodyBones.Head, "Head_Proxy" } };
        BuildHumanoidAvatar("ProxyAvatar", rename: rename,
            skipSkinningRoles: new HashSet<string> { "Head" }, // Head_Proxy itself stays unskinned
            extra: nodes => nodes["HeadCandidate"] = Child(nodes["Spine"], "Head"), // skinned sibling candidate
            extraSkinRoles: new List<string> { "HeadCandidate" });

        var r = CheckHumanoidRig.InspectAvatar("ProxyAvatar");
        // The canonical proxy IS also a name-shadow, and both rows are wanted: the mapping points at
        // Head_Proxy (unskinned) WHILE a different transform carries the plain label `Head`. Those are the
        // two halves of the same trap — an agent that looks the bone up by name gets the decoy, and one that
        // trusts the mapping gets a transform nothing skins — so the door reports each on its own class
        // rather than collapsing them and leaving the reader to guess which failure they have.
        StringAssert.Contains("proxy=1 nameShadow=1", r, r);
        StringAssert.Contains("=> CLASSIFY", r, r);
        var log = ReadLog(r);
        StringAssert.Contains("\"class\": \"proxy\"", log, log);
        StringAssert.Contains("\"class\": \"name-shadow\"", log, log);
        StringAssert.Contains("\"bone\": \"Head\"", log, log);
        StringAssert.Contains("\"mapped\": \"ProxyAvatar/Hips/Spine/Head_Proxy\"", log, "the mapped transform's path must be named: " + log);
        StringAssert.Contains("\"other\": \"ProxyAvatar/Hips/Spine/Head\"", log, "the sibling candidate must be named, as a candidate: " + log);
    }

    // ── name-shadow: some OTHER transform carries the plain humanoid label ─────────────────────────────

    [Test]
    public void MappingOnThePlainlyNamedBone_isNotAShadow_evenWithASameNamedTwin()
    {
        // The guard that keeps this door off every composed avatar: where the humanoid bone IS the
        // transform named for it, a second same-named transform is the ordinary base/mergeable pair the
        // build is about to zip, not a decoy.
        //
        // The two-same-named-nodes fixture this test would rather build is NOT constructible here:
        // AvatarBuilder.BuildHumanAvatar keys HumanDescription.skeleton by NAME, so a skeleton carrying two
        // `Head` nodes returns an invalid Avatar (measured — the builder refuses before the door is ever
        // called). So the guard is pinned the only way a unit can: a clean rig whose mapped bones carry
        // their plain labels reports no shadow at all. Its live evidence is docs/verify.md's execute_code
        // venue — on a real composed avatar the pre-fix code reported 5 name-shadows and 48 proxies where
        // the truth is zero of each, which is what the guard and the name-matched skin test remove.
        BuildHumanoidAvatar("PlainAvatar");

        var r = CheckHumanoidRig.InspectAvatar("PlainAvatar");
        StringAssert.Contains("nameShadow=0", r, r);
        StringAssert.Contains("=> PASS", r, r);
    }

    // ── bad input: bare FAIL, no trailer (family discipline, matches CheckAvatar) ──────────────────────

    [Test]
    public void UnresolvableHandle_bareFail_noTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[CheckHumanoidRig:avatar\] FAIL:"));
        var r = CheckHumanoidRig.InspectAvatar("NoSuchRoot_xyz");
        StringAssert.StartsWith("[CheckHumanoidRig:avatar] FAIL:", r);
        Assert.IsFalse(r.Contains("| log="), "bad input carries no artifact trailer: " + r);
    }

    [Test]
    public void NoAnimator_bareFail_named()
    {
        var avatar = new GameObject("NoAnimatorAvatar");
        avatar.AddComponent<VRCAvatarDescriptor>();

        LogAssert.Expect(LogType.Error, new Regex(@"\[CheckHumanoidRig:avatar\] FAIL:.*no Animator"));
        var r = CheckHumanoidRig.InspectAvatar("NoAnimatorAvatar");
        StringAssert.StartsWith("[CheckHumanoidRig:avatar] FAIL:", r);
        StringAssert.Contains("no Animator", r);
        Assert.IsFalse(r.Contains("| log="), "bad input carries no artifact trailer: " + r);
    }

    // ── non-humanoid avatar: FAIL, named ────────────────────────────────────────────────────────────────

    [Test]
    public void NonHumanoidAvatar_bareFail_named()
    {
        var avatar = new GameObject("GenericAvatar");
        avatar.AddComponent<VRCAvatarDescriptor>();
        var animator = avatar.AddComponent<Animator>();
        var generic = AvatarBuilder.BuildGenericAvatar(avatar, avatar.name); // root motion node = the root itself
        Assert.IsNotNull(generic, "AvatarBuilder.BuildGenericAvatar returned null — fixture/API drift, not a test bug");
        animator.avatar = generic;

        LogAssert.Expect(LogType.Error, new Regex(@"\[CheckHumanoidRig:avatar\] FAIL:.*not humanoid"));
        var r = CheckHumanoidRig.InspectAvatar("GenericAvatar");
        StringAssert.StartsWith("[CheckHumanoidRig:avatar] FAIL:", r);
        StringAssert.Contains("not humanoid", r);
        Assert.IsFalse(r.Contains("| log="), "bad input carries no artifact trailer: " + r);
    }
}
