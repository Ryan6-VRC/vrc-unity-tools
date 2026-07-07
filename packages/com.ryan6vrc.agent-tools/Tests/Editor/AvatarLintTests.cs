using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;
using VRC.SDK3.Avatars.Components;

// AvatarLint proof obligations (spec 2026-07-07-avatarlint-design.md, Acceptance criteria).
//
// AvatarLint.Inspect resolves scene paths against the ACTIVE scene (its local FindByHierarchyPath), so —
// like AnimatorLintRefactorTests — fixtures live in the active scene and are torn down in place. Nothing is
// saved: temp controllers/clips + the emitted RunLog are deleted in TearDown; the real scene file is never
// written. MA/VRCFury are the REAL installed types (reflection AddComponent), the same path the tool detects
// them on. The internal test seams are flipped via reflection (Tests is a separate assembly), which is also
// how they are exercised live via execute_code.
public class AvatarLintTests
{
    private const string TmpDir = "Assets/AgentAvatarLintTmp";
    private const string VendorTmpDir = "Assets/Vendor/AgentAvatarLintTmp";

    private GameObject _avatar;
    private string _logPath;
    private Scene _tmpScene;
    private Scene _prevActive;
    private object _origBoxed, _origResolve, _origAnchor;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // CLASSIFY logs a warning; degrade paths log warnings — expected
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentAvatarLintTmp");
        // B4: build fixtures in a throwaway additive scene, never the real active scene (Plum-Remy is Ryan's
        // real project). Capture the seam delegates so TearDown restores the real behaviour.
        _prevActive = EditorSceneManager.GetActiveScene();
        _tmpScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        EditorSceneManager.SetActiveScene(_tmpScene);
        _origBoxed = GetSeam("GetBoxedValue");
        _origResolve = GetSeam("ResolveGetOverload");
        _origAnchor = GetSeam("FrameAnchorOverride");
    }

    [TearDown]
    public void TearDown()
    {
        _avatar = null; // owned by _tmpScene; CloseScene(remove) tears it down
        ResetSeams();
        // Close the temp scene BEFORE deleting TmpDir (the no-dirty test saves the scene into TmpDir).
        if (_tmpScene.IsValid())
        {
            if (_prevActive.IsValid() && _prevActive.isLoaded) EditorSceneManager.SetActiveScene(_prevActive);
            EditorSceneManager.CloseScene(_tmpScene, true); // remove, UNSAVED — never persists to the real project
        }
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        if (AssetDatabase.IsValidFolder(VendorTmpDir)) AssetDatabase.DeleteAsset(VendorTmpDir);
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Reflection helpers (real MA/VRCF types + internal seams) ────────────────────────────────────

    private static Type Resolve(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void SetSeam(string field, object value) =>
        typeof(AvatarLint).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

    private static object GetSeam(string field) =>
        typeof(AvatarLint).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private void ResetSeams()
    {
        SetSeam("GetBoxedValue", _origBoxed);
        SetSeam("ResolveGetOverload", _origResolve);
        SetSeam("FrameAnchorOverride", _origAnchor);
    }

    private static string Inspect(string root) => AvatarLint.Inspect(root);

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

    // A saved .anim with one float binding per path (SetEditorCurve, not SetCurve, so paths don't expand).
    private AnimationClip NewClip(string dir, string name, params string[] paths)
    {
        var clip = new AnimationClip { name = name };
        var curve = AnimationCurve.Linear(0, 0, 1, 1);
        foreach (var p in paths)
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(p, typeof(Transform), "m_LocalScale.x"), curve);
        AssetDatabase.CreateAsset(clip, dir + "/" + name + ".anim");
        return clip;
    }

    private AnimatorController NewController(string name, AnimationClip clip)
    {
        var c = AnimatorController.CreateAnimatorControllerAtPath(TmpDir + "/" + name + ".controller");
        c.layers[0].stateMachine.AddState("S").motion = clip;
        return c;
    }

    private GameObject NewAvatar(string name)
    {
        _avatar = new GameObject(name);
        _avatar.AddComponent<VRCAvatarDescriptor>();
        return _avatar;
    }

    private void SetBaseLayers(GameObject avatar, params (VRCAvatarDescriptor.AnimLayerType type, AnimatorController ctrl)[] layers)
    {
        var d = avatar.GetComponent<VRCAvatarDescriptor>();
        d.baseAnimationLayers = layers.Select(l => new VRCAvatarDescriptor.CustomAnimLayer
        {
            type = l.type, animatorController = l.ctrl, isDefault = false, isEnabled = true
        }).ToArray();
        d.specialAnimationLayers = Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
    }

    private Component AddMaMergeAnimator(GameObject go, AnimatorController ctrl)
    {
        var t = Resolve("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
        Assert.IsNotNull(t, "MA MergeAnimator type must resolve");
        var c = go.AddComponent(t);
        var so = new SerializedObject(c);
        so.FindProperty("animator").objectReferenceValue = ctrl;
        so.FindProperty("pathMode").enumValueIndex = 0; // Relative
        so.ApplyModifiedPropertiesWithoutUndo();
        return c;
    }

    // ObjectToggle with one entry; refPath is avatar-root-relative; targetGO optional (targetObject-wins).
    private Component AddMaObjectToggle(GameObject go, string refPath, GameObject targetGO)
    {
        var t = Resolve("nadena.dev.modular_avatar.core.ModularAvatarObjectToggle");
        Assert.IsNotNull(t, "MA ObjectToggle type must resolve");
        var c = go.AddComponent(t);
        var so = new SerializedObject(c);
        var arr = so.FindProperty("m_objects");
        arr.arraySize = 1;
        var aor = arr.GetArrayElementAtIndex(0).FindPropertyRelative("Object");
        aor.FindPropertyRelative("referencePath").stringValue = refPath;
        aor.FindPropertyRelative("targetObject").objectReferenceValue = targetGO;
        so.ApplyModifiedPropertiesWithoutUndo();
        return c;
    }

    private Component AddVrcfFullController(GameObject go, AnimatorController ctrl, GameObject rootOverride)
    {
        var vt = Resolve("VF.Model.VRCFury");
        Assert.IsNotNull(vt, "VF.Model.VRCFury must resolve");
        var ft = Resolve("VF.Model.Feature.FullController");
        Assert.IsNotNull(ft, "VF.Model.Feature.FullController must resolve");
        var c = go.AddComponent(vt);
        var so = new SerializedObject(c);
        so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(ft);
        so.ApplyModifiedPropertiesWithoutUndo();

        so = new SerializedObject(c);
        var content = so.FindProperty("content");
        var controllers = content.FindPropertyRelative("controllers");
        controllers.arraySize = 1;
        controllers.GetArrayElementAtIndex(0).FindPropertyRelative("controller").FindPropertyRelative("objRef").objectReferenceValue = ctrl;
        content.FindPropertyRelative("rootObjOverride").objectReferenceValue = rootOverride;
        so.ApplyModifiedPropertiesWithoutUndo();
        return c;
    }

    // A FullController with an empty (present) controllers array — the B1 not-drift boundary.
    private Component AddVrcfFullControllerNoControllers(GameObject go, GameObject rootOverride)
    {
        var vt = Resolve("VF.Model.VRCFury");
        var ft = Resolve("VF.Model.Feature.FullController");
        Assert.IsNotNull(vt); Assert.IsNotNull(ft);
        var c = go.AddComponent(vt);
        var so = new SerializedObject(c);
        so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(ft);
        so.ApplyModifiedPropertiesWithoutUndo();
        so = new SerializedObject(c);
        var content = so.FindProperty("content");
        content.FindPropertyRelative("controllers").arraySize = 0;
        content.FindPropertyRelative("rootObjOverride").objectReferenceValue = rootOverride;
        so.ApplyModifiedPropertiesWithoutUndo();
        return c;
    }

    // ── PASS ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void CleanAvatar_allResolve_isPass()
    {
        var a = NewAvatar("LintClean");
        var bone = NewChild(a, "Body_Base");
        var outfit = NewChild(a, "Outfit");
        NewChild(outfit, "Bone_Present");
        var clip = NewClip(TmpDir, "OkClip", "Body_Base", "Outfit/Bone_Present"); // both exist at avatar root
        SetBaseLayers(a, (VRCAvatarDescriptor.AnimLayerType.FX, NewController("OkCtrl", clip)));
        AddMaObjectToggle(outfit, "Body_Base", null); // resolves

        var r = Inspect("LintClean");
        StringAssert.Contains("maSceneRef=0 clipBinding=0 => PASS", r, r);
    }

    // ── Proof A + Proof B coexist ─────────────────────────────────────────────────────────────────────

    [Test]
    public void ProofA_break_and_ProofB_noFalseAbort_coexist()
    {
        var a = NewAvatar("LintAB");
        NewChild(a, "Body_Base");                          // base renamed away from Body_base
        var outfit = NewChild(a, "Outfit");
        NewChild(outfit, "Bone_Present");                  // to-be-merged bone, present in scene

        // Descriptor FX layer clip animates the renamed base by its OLD name → fails at the avatar-root frame.
        var fxClip = NewClip(TmpDir, "FxBroken", "Body_base");
        SetBaseLayers(a, (VRCAvatarDescriptor.AnimLayerType.FX, NewController("FxCtrl", fxClip)));

        // MA MergeAnimator (Relative, frame = Outfit) clip animates the present bone → resolves (Proof B).
        var outfitClip = NewClip(TmpDir, "OutfitOk", "Bone_Present");
        AddMaMergeAnimator(outfit, NewController("OutfitCtrl", outfitClip));

        // MA reactive ref to the renamed base → MA-scene-ref offender (Proof A). Plus a resolving ref (not counted).
        AddMaObjectToggle(outfit, "Body_base", null);
        AddMaObjectToggle(outfit, "Outfit/Bone_Present", null);

        var r = Inspect("LintAB");
        var log = ReadLog(r);

        StringAssert.Contains("=> CLASSIFY", r, r);
        StringAssert.Contains("maSceneRef=1", r, "exactly the one broken reactive ref: " + r);
        StringAssert.Contains("clipBinding=1", r, "exactly the base-rename binding; the present bone must NOT surface: " + r);
        StringAssert.Contains("path=`Body_base`", log, "the broken binding is surfaced by its class: " + log);
        Assert.IsFalse(log.Contains("path=`Bone_Present`"), "Proof B: the present to-be-merged bone must not be an offender: " + log);
    }

    // ── VRCF ancestor walk (D-A) ──────────────────────────────────────────────────────────────────────

    [Test]
    public void Vrcf_avatarLevelObject_resolvesUpward_renamedBase_failsAllLevels()
    {
        var a = NewAvatar("LintVrcf");
        NewChild(a, "AvatarLevelThing");                    // lives at the avatar root, not under the mount
        var prop = NewChild(a, "Prop");                     // VRCF mount, deep frame

        var clip = NewClip(TmpDir, "VrcfClip", "AvatarLevelThing", "Body_base");
        AddVrcfFullController(prop, NewController("VrcfCtrl", clip), prop);

        var r = Inspect("LintVrcf");
        var log = ReadLog(r);

        StringAssert.Contains("clipBinding=1", r, "only the renamed base fails; the avatar-level obj resolves via upward strip: " + r);
        StringAssert.Contains("path=`Body_base`", log, log);
        Assert.IsFalse(log.Contains("path=`AvatarLevelThing`"), "D-A: an avatar-level object resolves upward and must not surface: " + log);
    }

    // ── clipAssetPath routing (R-E) ───────────────────────────────────────────────────────────────────

    [Test]
    public void ClipAssetPath_distinguishesVendorFromOwned()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Vendor")) AssetDatabase.CreateFolder("Assets", "Vendor");
        if (!AssetDatabase.IsValidFolder(VendorTmpDir)) AssetDatabase.CreateFolder("Assets/Vendor", "AgentAvatarLintTmp");

        var a = NewAvatar("LintRoute");
        var ownedClip = NewClip(TmpDir, "OwnedBroken", "Body_base");
        var vendorClip = NewClip(VendorTmpDir, "VendorBroken", "Body_base");
        SetBaseLayers(a,
            (VRCAvatarDescriptor.AnimLayerType.FX, NewController("OwnedCtrl", ownedClip)),
            (VRCAvatarDescriptor.AnimLayerType.Gesture, NewController("VendorCtrl", vendorClip)));

        var r = Inspect("LintRoute");
        var log = ReadLog(r);

        StringAssert.Contains("clipAssetPath=`" + TmpDir + "/OwnedBroken.anim`", log, "owned clip path present: " + log);
        StringAssert.Contains("clipAssetPath=`" + VendorTmpDir + "/VendorBroken.anim`", log, "vendor clip path present (distinct from scene path): " + log);
    }

    // ── Never throws: .Get(Component) unreachable → self-resolve (targetObject-first) ──────────────────

    [Test]
    public void GetUnreachable_selfResolves_targetObjectFirst_andCompletes()
    {
        var a = NewAvatar("LintSelfResolve");
        var bone = NewChild(NewChild(a, "Outfit"), "Bone_Present");
        var outfit = a.transform.Find("Outfit").gameObject;
        // Stale referencePath but a live targetObject → targetObject-first must resolve it in the fallback.
        AddMaObjectToggle(outfit, "Stale_wrong_path", bone);

        SetSeam("ResolveGetOverload", (Func<Type, MethodInfo>)(_ => null)); // force the Get(Component) overload unreachable
        var r = Inspect("LintSelfResolve");
        ReadLog(r);

        StringAssert.Contains("=> PASS", r, "targetObject-first self-resolve keeps the live ref resolved: " + r);
        StringAssert.Contains("maSceneRef=0", r, r);
    }

    // ── Never throws: boxedValue throws (R-J) ─────────────────────────────────────────────────────────

    [Test]
    public void BoxedValueThrows_isCaught_andCompletes()
    {
        var a = NewAvatar("LintBoxThrow");
        var outfit = NewChild(a, "Outfit");
        AddMaObjectToggle(outfit, "Body_base", null); // unresolvable via path; self-resolve → still null → offender

        SetSeam("GetBoxedValue", (Func<SerializedProperty, object>)(p => throw new Exception("forced boxedValue throw"))); // R-J
        Assert.DoesNotThrow(() =>
        {
            var r = Inspect("LintBoxThrow");
            ReadLog(r);
            StringAssert.Contains("=> CLASSIFY", r, r); // completes with a verdict
        });
    }

    // ── Fail-loud frame reads (R-H) ───────────────────────────────────────────────────────────────────

    [Test]
    public void UnreflectedFrameField_isSurfaced_notDropped()
    {
        var a = NewAvatar("LintRH");
        var outfit = NewChild(a, "Outfit");
        var brokenClip = NewClip(TmpDir, "RhBroken", "Nope_missing");
        AddMaMergeAnimator(outfit, NewController("RhCtrl", brokenClip));

        SetSeam("FrameAnchorOverride", (Func<string, string>)(_ => "MA.pathMode")); // inject the drift anchor onto a real MA frame
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("frame field 'MA.pathMode'.*did not reflect"));
        var r = Inspect("LintRH");
        var log = ReadLog(r);

        StringAssert.Contains("clipBinding=1", r, "R-H: the controller is NOT dropped — its broken binding still surfaces: " + r);
        StringAssert.Contains("fail-loud (R-H)", log, "the unreflected anchor is surfaced in Notes: " + log);
    }

    // R-H symmetric on the VRCF side (B1): a drifted VRCF frame surfaces loud + the controller is not dropped.
    [Test]
    public void UnreflectedFrameField_VRCF_isSurfaced_notDropped()
    {
        var a = NewAvatar("LintRhVrcf");
        var prop = NewChild(a, "Prop");
        var brokenClip = NewClip(TmpDir, "RhVrcfBroken", "Nope_missing");
        AddVrcfFullController(prop, NewController("RhVrcfCtrl", brokenClip), prop);

        SetSeam("FrameAnchorOverride", (Func<string, string>)(_ => "VRCF.content")); // inject the drift anchor onto a real VRCF frame
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("frame field 'VRCF.content'.*did not reflect"));
        var r = Inspect("LintRhVrcf");
        var log = ReadLog(r);

        StringAssert.Contains("clipBinding=1", r, "R-H: the VRCF controller is NOT dropped — its broken binding still surfaces: " + r);
        StringAssert.Contains("fail-loud (R-H)", log, "the unreflected anchor is surfaced in Notes: " + log);
    }

    // B1 boundary: an empty-but-present FullController controllers array is NOT drift (must stay quiet — no anchor).
    [Test]
    public void TryVrcfFrame_emptyControllersList_isNotDrift()
    {
        var a = NewAvatar("LintVrcfEmpty");
        var prop = NewChild(a, "Prop");
        var c = AddVrcfFullControllerNoControllers(prop, prop);

        var args = new object[] { c, null, null };
        bool ok = (bool)typeof(AnimatorLint).GetMethod("TryVrcfFrame", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, args);
        Assert.IsTrue(ok, "a present FullController is a frame");
        var frame = args[2];
        string anchor = (string)frame.GetType().GetField("UnreflectedAnchor").GetValue(frame);
        Assert.IsNull(anchor, "an empty-but-present controllers array must NOT be treated as drift");
    }

    // B2 boundary: a present-but-null MA animator is an intentional empty, not drift — TryMaFrame stays quiet.
    [Test]
    public void TryMaFrame_presentButNullAnimator_staysQuiet()
    {
        var a = NewAvatar("LintMaNull");
        var outfit = NewChild(a, "Outfit");
        var t = Resolve("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
        Assert.IsNotNull(t);
        var c = outfit.AddComponent(t);
        var so = new SerializedObject(c);
        so.FindProperty("pathMode").enumValueIndex = 0;
        so.ApplyModifiedPropertiesWithoutUndo(); // animator left null (present-but-null, not field-absent)

        var args = new object[] { c, a, null, null };
        bool ok = (bool)typeof(AnimatorLint).GetMethod("TryMaFrame", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, args);
        Assert.IsFalse(ok, "a present-but-null animator is an intentional empty, not drift — stays quiet");
    }

    // ── Inspection-class: no scene dirtying, no .anim write ───────────────────────────────────────────

    [Test]
    public void Inspect_doesNotDirtyScene_norTouchAnim()
    {
        var a = NewAvatar("LintNoDirty");
        NewChild(a, "Body_Base");
        var clip = NewClip(TmpDir, "NoDirtyClip", "Body_base");
        SetBaseLayers(a, (VRCAvatarDescriptor.AnimLayerType.FX, NewController("NoDirtyCtrl", clip)));

        // B4: save the temp scene so the baseline is genuinely CLEAN — otherwise the fixture build leaves it
        // dirty and the assertion would only prove Inspect preserves an already-dirty scene.
        string scenePath = TmpDir + "/NoDirtyScene.unity";
        EditorSceneManager.SaveScene(_tmpScene, scenePath);
        Assert.IsFalse(_tmpScene.isDirty, "baseline must be a clean scene");
        long animMtime = File.GetLastWriteTimeUtc(TmpDir + "/NoDirtyClip.anim").Ticks;

        var r = Inspect("LintNoDirty");
        ReadLog(r);

        Assert.IsFalse(EditorSceneManager.GetActiveScene().isDirty, "Inspect must not dirty a clean scene");
        Assert.AreEqual(animMtime, File.GetLastWriteTimeUtc(TmpDir + "/NoDirtyClip.anim").Ticks, "Inspect must not touch the .anim");
    }

    // ── Bad input → bare FAIL, no trailer ─────────────────────────────────────────────────────────────

    [Test]
    public void BadInput_barFail_noTrailer()
    {
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[AvatarLint\] FAIL:"));
        var r = AvatarLint.Inspect("NoSuchRoot_xyz");
        StringAssert.StartsWith("[AvatarLint] FAIL:", r);
        Assert.IsFalse(r.Contains("| log="), "bad input carries no artifact trailer: " + r);
    }
}
