#if MA_PRESENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using nadena.dev.modular_avatar.core;
using Ryan6Vrc.AgentTools.Editor;

// Task 8 (spec 2026-07-11-testeditor-compose-coverage): exercise CheckSeam's REAL reflection collectors
// against live MA/VRCFury components — the paths the injected-seam CheckSeamTests deliberately cannot run.
// Inject ResolveHumanoid (cheap map) but leave ResolveSeam at default so CollectMaPairs / CollectVrcfPairs
// actually run. Fixtures sit under a VRCAvatarDescriptor (MA's mergeTarget.Get needs a registered avatar
// root) with DISTINCT base positions and each merge bone overlaid on its intended partner, so a PASS proves
// the correct pairs were collected, not merely that >=2 were.
[Category("CheckSeamLive")]
public partial class CheckSeamLiveTests
{
    private GameObject _avatar;
    private string _logPath;
    private object _origResolveSeam, _origResolveHumanoid;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // NOT-PASS/REFUSE log at warning/error — expected here
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _avatar = new GameObject("Avatar");
        _avatar.AddComponent<VRCAvatarDescriptor>(); // the registered root MA's AvatarObjectReference needs
        _origResolveSeam = GetSeam("ResolveSeam");
        _origResolveHumanoid = GetSeam("ResolveHumanoid");
    }

    [TearDown]
    public void TearDown()
    {
        if (_avatar != null) UnityEngine.Object.DestroyImmediate(_avatar);
        _avatar = null;
        SetSeam("ResolveSeam", _origResolveSeam);
        SetSeam("ResolveHumanoid", _origResolveHumanoid);
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        LogAssert.ignoreFailingMessages = false;
    }

    private static void SetSeam(string f, object v) =>
        typeof(CheckSeam).GetField(f, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, v);
    private static object GetSeam(string f) =>
        typeof(CheckSeam).GetField(f, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private static GameObject Child(GameObject parent, string name, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        return go;
    }

    private static string Path(GameObject go)
    {
        var t = go.transform; var sb = new System.Text.StringBuilder(t.name);
        while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
        return sb.ToString();
    }

    private string ReadLog(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, StringComparison.Ordinal);
        _logPath = i < 0 ? null : result.Substring(i + marker.Length).Trim();
        return _logPath != null && System.IO.File.Exists(_logPath) ? System.IO.File.ReadAllText(_logPath) : "";
    }

    private void InjectHumanoid(params Transform[] baseBones)
    {
        var map = new CheckSeam.HumanoidMap { SpanMm = 350f };
        foreach (var b in baseBones) map.Bones.Add(b);
        CheckSeam.ResolveHumanoid = _ => map;
    }

    private void AttachSkin(GameObject mergeGO, Transform[] bones)
    {
        int n = bones.Length;
        var verts = new Vector3[n]; var bw = new BoneWeight[n]; var bp = new Matrix4x4[n];
        for (int i = 0; i < n; i++) { verts[i] = Vector3.zero; bw[i] = new BoneWeight { boneIndex0 = i, weight0 = 1f }; bp[i] = Matrix4x4.identity; }
        var mesh = new Mesh { vertices = verts }; mesh.boneWeights = bw; mesh.bindposes = bp;
        var smr = Child(mergeGO, "Skin", Vector3.zero).AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = bones;
    }

    private void BuildMaFixture(out GameObject baseGO, out GameObject mergeGO, out Transform mHips, out Transform mSpine)
    {
        var baseArm = Child(_avatar, "BaseArmature", Vector3.zero);
        var bHips = Child(baseArm, "Hips", new Vector3(0f, 1.0f, 0f)).transform;
        var bSpine = Child(baseArm, "Spine", new Vector3(0f, 1.2f, 0f)).transform;
        baseGO = baseArm;

        mergeGO = Child(_avatar, "Outfit", Vector3.zero);
        var mergeArm = Child(mergeGO, "Armature", Vector3.zero);
        mHips = Child(mergeArm, "Hips", new Vector3(0f, 1.0f, 0f)).transform;
        mSpine = Child(mergeArm, "Spine", new Vector3(0f, 1.2f, 0f)).transform;

        var ma = mergeArm.AddComponent<ModularAvatarMergeArmature>();
        ma.mergeTarget.Set(baseArm);

        AttachSkin(mergeGO, new[] { mHips, mSpine });
        InjectHumanoid(bHips, bSpine);
    }

    [Test]
    public void MaMergeArmature_coincident_pass()
    {
        BuildMaFixture(out var baseGO, out var mergeGO, out _, out _);
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> PASS", r);
        StringAssert.Contains("weightedHumanoid=2", r);
        ReadLog(r);
    }

    [Test]
    public void MaMergeArmature_offset_notPass()
    {
        BuildMaFixture(out var baseGO, out var mergeGO, out var mHips, out _);
        mHips.localPosition += new Vector3(0.01f, 0f, 0f); // 10mm >> eps=0.7mm ⇒ one offender
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> NOT-PASS", r);
        StringAssert.Contains("offenders=1", r);
        var body = ReadLog(r);
        StringAssert.Contains("Hips", body); // bone name is in the RunLog body, not the one-line summary
    }
}

public partial class CheckSeamLiveTests
{
    private static Type FindType(string full) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == full);

    private static void SetField(object o, string name, object v)
    {
        var f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        if (f == null) throw new MissingFieldException(o.GetType().Name, name);
        f.SetValue(o, v);
    }

    // VRCFury is internal → reflection. Distinguish "package absent" (Ignore) from "package present but its
    // types didn't resolve" — the latter is real collector DRIFT (e.g. a package-side type rename) and MUST
    // fail, not skip, or the loudest drift this suite exists to catch would pass green. VRCF_PRESENT keys on
    // the package name (stable across type reshapes), unlike the reflected type string.
    private void BuildVrcfOrGate(out GameObject baseGO, out GameObject mergeGO, out Transform mHips, bool forceScale)
    {
        if (BuildVrcfFixture(out baseGO, out mergeGO, out mHips, forceScale)) return;
#if VRCF_PRESENT
        Assert.Fail("VRCFury is installed but VF.Model.VRCFury/ArmatureLink did not resolve — collector drift");
#else
        Assert.Ignore("VRCFury not installed in this editor");
#endif
    }

    // Attach a real VRCFury component carrying an object-mode, recursive ArmatureLink linking merge→base.
    // Object-mode (useObj) skips VRCFury's humanoid-Hips lookup (which would need a real Animator); recursive
    // makes GetLinks walk the >=2 name-matched children. Returns false if VRCFury isn't installed.
    private bool AttachVrcfArmatureLink(GameObject mergeArm, GameObject baseArm, GameObject propRoot, bool forceScale)
    {
        var vrcfType = FindType("VF.Model.VRCFury");
        var alType = FindType("VF.Model.Feature.ArmatureLink");
        if (vrcfType == null || alType == null) return false;

        var al = System.Activator.CreateInstance(alType);
        SetField(al, "propBone", propRoot);
        SetField(al, "recursive", true);
        SetField(al, "forceOneWorldScale", forceScale);

        var linkTo = alType.GetField("linkTo").GetValue(al) as System.Collections.IList;
        var link0 = linkTo[0];
        SetField(link0, "useBone", false);
        SetField(link0, "useObj", true);
        SetField(link0, "obj", baseArm);

        var comp = mergeArm.AddComponent(vrcfType);
        SetField(comp, "content", al);
        return true;
    }

    private bool BuildVrcfFixture(out GameObject baseGO, out GameObject mergeGO, out Transform mHips, bool forceScale)
    {
        var baseArm = Child(_avatar, "BaseArmature", Vector3.zero);
        var bHips = Child(baseArm, "Hips", new Vector3(0f, 1.0f, 0f)).transform;
        var bSpine = Child(baseArm, "Spine", new Vector3(0f, 1.2f, 0f)).transform;
        baseGO = baseArm;

        mergeGO = Child(_avatar, "Outfit", Vector3.zero);
        var mergeArm = Child(mergeGO, "Armature", Vector3.zero);
        mHips = Child(mergeArm, "Hips", new Vector3(0f, 1.0f, 0f)).transform;
        var mSpine = Child(mergeArm, "Spine", new Vector3(0f, 1.2f, 0f)).transform;

        AttachSkin(mergeGO, new[] { mHips, mSpine });
        InjectHumanoid(bHips, bSpine);
        return AttachVrcfArmatureLink(mergeArm, baseArm, mergeArm, forceScale);
    }

    [Test]
    public void VrcfArmatureLink_coincident_pass()
    {
        BuildVrcfOrGate(out var baseGO, out var mergeGO, out _, forceScale: false);
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> PASS", r);
        StringAssert.Contains("weightedHumanoid=2", r);
        ReadLog(r);
    }

    [Test]
    public void VrcfArmatureLink_offset_notPass()
    {
        BuildVrcfOrGate(out var baseGO, out var mergeGO, out var mHips, forceScale: false);
        mHips.localPosition += new Vector3(0.01f, 0f, 0f); // 10mm ⇒ offender
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.Contains("=> NOT-PASS", r);
        StringAssert.Contains("offenders=1", r);   // N2: parity with the MA twin
        var body = ReadLog(r);
        StringAssert.Contains("Hips", body); // bone names appear in the RunLog BODY, not the one-liner
    }

    [Test]
    public void VrcfArmatureLink_forceOneWorldScale_refuses()
    {
        BuildVrcfOrGate(out var baseGO, out var mergeGO, out _, forceScale: true);
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("scaled at bake", r);
    }
}
#endif
