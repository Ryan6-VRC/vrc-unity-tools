using System;
using System.Collections.Generic;
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

// Plumbing shared by the two CheckAvatar fixtures in this file. Deliberately NOT a base class: NUnit runs an
// inherited [SetUp] ahead of each derived one, and the split below exists precisely because the two fixtures
// need different setup.
internal static class CheckAvatarFixture
{
    internal static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    internal static GameObject Avatar(string name)
    {
        var go = new GameObject(name);
        go.AddComponent<VRCAvatarDescriptor>();
        return go;
    }

    internal static void SetSeam(string field, object value) =>
        typeof(CheckAvatar).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

    internal static object GetSeam(string field) =>
        typeof(CheckAvatar).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    // The artifact path off a summary's `| log=` trailer; null when the summary carries none (a bad-input
    // refusal must not point at an artifact — that is itself asserted below).
    internal static string LogPath(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? null : result.Substring(i + marker.Length).Trim();
    }
}

// CheckAvatar proof obligations (spec 2026-07-07-avatarlint-design.md, Acceptance criteria) — the surface that
// needs real assets: saved clips, saved controllers, and the MA/VRCFury frames that carry them.
//
// CheckAvatar.Inspect resolves scene paths against the ACTIVE scene (its local FindByHierarchyPath), so —
// like CheckAnimatorRefactorTests — fixtures live in the active scene and are torn down in place. Nothing is
// saved into TmpDir that must outlive a test: the temp controllers/clips + the emitted RunLog go in TearDown's
// one batched delete, and the real scene file is never written. MA/VRCFury are the REAL installed types
// (reflection AddComponent), the same path the tool detects them on. The internal test seams are flipped via
// reflection (Tests is a separate assembly), which is also how they are exercised live via execute_code.
//
// The merge-conflict grouping core needs NONE of this scaffolding (no clip, no controller, no asset at all) and
// lives in CheckAvatarMergeConflictTests below, on a scene built once.
public class CheckAvatarTests
{
    private const string TmpDir = "Assets/AgentCheckAvatarTmp";
    private const string VendorTmpDir = "Assets/Vendor/AgentCheckAvatarTmp";

    private GameObject _avatar;
    private string _logPath;
    private object _origBoxed, _origResolve, _origAnchor;
    private object _origMergePairs, _origDynamics;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // CLASSIFY logs a warning; degrade paths log warnings — expected
        // B4: build fixtures in a Single throwaway scene, never a real saved scene (same pattern as
        // CheckSeamTests): NewScene(Additive) throws whenever the active scene is untitled AND dirty — the
        // batchmode boot state once any earlier test has touched it — so additive is order-dependent.
        // Capture the seam delegates so TearDown restores the real behaviour.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentCheckAvatarTmp");
        _origBoxed = GetSeam("GetBoxedValue");
        _origResolve = GetSeam("ResolveGetOverload");
        _origAnchor = GetSeam("FrameAnchorOverride");
        _origMergePairs = GetSeam("ResolveMergePairs");
        _origDynamics = GetSeam("CollectDynamicsTargets");
    }

    [TearDown]
    public void TearDown()
    {
        _avatar = null; // owned by the throwaway scene; the next Single NewScene discards it
        ResetSeams();
        // ONE batched AssetDatabase mutation, not three: this runs per test, and each separate DeleteAsset is
        // an import. There is no second NewScene here either — the only test that saved a scene saves it
        // OUTSIDE TmpDir and cleans up after itself, so nothing deleted below can be the loaded active scene.
        var doomed = new List<string>();
        if (!string.IsNullOrEmpty(_logPath)) doomed.Add(_logPath);
        if (AssetDatabase.IsValidFolder(TmpDir)) doomed.Add(TmpDir);
        if (AssetDatabase.IsValidFolder(VendorTmpDir)) doomed.Add(VendorTmpDir);
        if (doomed.Count > 0) AssetDatabase.DeleteAssets(doomed.ToArray(), new List<string>());
        _logPath = null;
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Reflection helpers (real MA/VRCF types + internal seams) ────────────────────────────────────

    private static Type Resolve(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void SetSeam(string field, object value) => CheckAvatarFixture.SetSeam(field, value);
    private static object GetSeam(string field) => CheckAvatarFixture.GetSeam(field);

    private void ResetSeams()
    {
        SetSeam("GetBoxedValue", _origBoxed);
        SetSeam("ResolveGetOverload", _origResolve);
        SetSeam("FrameAnchorOverride", _origAnchor);
        SetSeam("ResolveMergePairs", _origMergePairs);
        SetSeam("CollectDynamicsTargets", _origDynamics);
    }

    private static string Inspect(string root) => CheckAvatar.Inspect(root);

    private string ReadLog(string result)
    {
        _logPath = CheckAvatarFixture.LogPath(result);
        return _logPath != null && File.Exists(_logPath) ? File.ReadAllText(_logPath) : "";
    }

    // ── Fixture builders ────────────────────────────────────────────────────────────────────────────

    private GameObject NewChild(GameObject parent, string name) => CheckAvatarFixture.Child(parent, name);

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

    private GameObject NewAvatar(string name) => _avatar = CheckAvatarFixture.Avatar(name);

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

    // Set the FullController "Path Rewrite Rules" (content.rewriteBindings) on an existing VRCF component.
    private void SetVrcfRewriteBindings(Component c, params (string from, string to, bool delete)[] rules)
    {
        var so = new SerializedObject(c);
        var arr = so.FindProperty("content").FindPropertyRelative("rewriteBindings");
        arr.arraySize = rules.Length;
        for (int i = 0; i < rules.Length; i++)
        {
            var el = arr.GetArrayElementAtIndex(i);
            el.FindPropertyRelative("from").stringValue = rules[i].from;
            el.FindPropertyRelative("to").stringValue = rules[i].to;
            el.FindPropertyRelative("delete").boolValue = rules[i].delete;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
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
        StringAssert.Contains("maSceneRef=0 clipBinding=0 anchorSeam=0 mergeConflict=0 => PASS", r, r);
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

    // ── VRCF rewriteBindings (D-A step 1) — the carried-doll downward-relocation case ──────────────────

    // A prop's clips address a bone by a base-rooted path (Armature/Bone) but the bone is mounted DOWNWARD
    // (Prop/Nested/Armature/Bone). The upward strip alone can't reach it; the FullController's own
    // rewriteBindings rule (Armature → Nested/Armature) must be applied first, exactly as the build does.
    [Test]
    public void Vrcf_rewriteBindings_resolvesDownwardRelocation()
    {
        var a = NewAvatar("LintVrcfRw");
        var prop = NewChild(a, "Prop");                        // VRCF mount
        var armature = NewChild(NewChild(prop, "Nested"), "Armature");
        NewChild(armature, "Bone");                            // real location: Prop/Nested/Armature/Bone

        var clip = NewClip(TmpDir, "VrcfRwClip", "Armature/Bone", "Ghost/Missing");
        var c = AddVrcfFullController(prop, NewController("VrcfRwCtrl", clip), prop);
        SetVrcfRewriteBindings(c, ("Armature", "Nested/Armature", false));

        var r = Inspect("LintVrcfRw");
        var log = ReadLog(r);

        Assert.IsFalse(log.Contains("path=`Armature/Bone`"),
            "rewriteBindings must relocate Armature/Bone → Nested/Armature/Bone and resolve it: " + log);
        StringAssert.Contains("clipBinding=1", r, "only the genuinely-missing Ghost/Missing survives: " + r);
        StringAssert.Contains("path=`Ghost/Missing`", log, log);
    }

    // A matched delete rule drops the binding at build — it must not surface as a break.
    [Test]
    public void Vrcf_rewriteBindings_deleteRule_dropsBinding()
    {
        var a = NewAvatar("LintVrcfDel");
        var prop = NewChild(a, "Prop");
        var clip = NewClip(TmpDir, "VrcfDelClip", "DeleteMe/Gone");
        var c = AddVrcfFullController(prop, NewController("VrcfDelCtrl", clip), prop);
        SetVrcfRewriteBindings(c, ("DeleteMe", "", true)); // delete: the binding vanishes at build

        var r = Inspect("LintVrcfDel");
        StringAssert.Contains("clipBinding=0 anchorSeam=0 mergeConflict=0 => PASS", r, "a delete-ruled binding is not a break: " + r);
    }

    // ── clipAssetPath routing (R-E) ───────────────────────────────────────────────────────────────────

    [Test]
    public void ClipAssetPath_distinguishesVendorFromOwned()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Vendor")) AssetDatabase.CreateFolder("Assets", "Vendor");
        if (!AssetDatabase.IsValidFolder(VendorTmpDir)) AssetDatabase.CreateFolder("Assets/Vendor", "AgentCheckAvatarTmp");

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
        StringAssert.Contains(CheckAvatar.FailLoudNotePrefix, log, "the unreflected anchor is surfaced in Notes: " + log);
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
        StringAssert.Contains(CheckAvatar.FailLoudNotePrefix, log, "the unreflected anchor is surfaced in Notes: " + log);
    }

    // B1 boundary: an empty-but-present FullController controllers array is NOT drift (must stay quiet — no anchor).
    [Test]
    public void TryVrcfFrame_emptyControllersList_isNotDrift()
    {
        var a = NewAvatar("LintVrcfEmpty");
        var prop = NewChild(a, "Prop");
        var c = AddVrcfFullControllerNoControllers(prop, prop);

        var args = new object[] { c, null, null };
        bool ok = (bool)typeof(CheckAnimator).GetMethod("TryVrcfFrame", BindingFlags.NonPublic | BindingFlags.Static)
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
        bool ok = (bool)typeof(CheckAnimator).GetMethod("TryMaFrame", BindingFlags.NonPublic | BindingFlags.Static)
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
        // dirty and the assertion would only prove Inspect preserves an already-dirty scene. Saved OUTSIDE
        // TmpDir and cleaned up here: when it lived inside TmpDir, TearDown was deleting the LOADED active
        // scene, which is why every one of this fixture's tests used to pay a second NewScene.
        const string scenePath = "Assets/AgentCheckAvatarNoDirtyScene.unity";
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, scenePath);
        Assert.IsFalse(scene.isDirty, "baseline must be a clean scene");
        long animMtime = File.GetLastWriteTimeUtc(TmpDir + "/NoDirtyClip.anim").Ticks;

        var r = Inspect("LintNoDirty");
        ReadLog(r);

        Assert.IsFalse(EditorSceneManager.GetActiveScene().isDirty, "Inspect must not dirty a clean scene");
        Assert.AreEqual(animMtime, File.GetLastWriteTimeUtc(TmpDir + "/NoDirtyClip.anim").Ticks, "Inspect must not touch the .anim");

        // Unload before deleting: the saved scene is the active one, and DeleteAsset on a loaded scene is
        // not a state Unity guarantees.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        AssetDatabase.DeleteAsset(scenePath);
    }

    // ── Bad input → bare FAIL, no trailer ─────────────────────────────────────────────────────────────

    [Test]
    public void BadInput_barFail_noTrailer()
    {
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckAvatar\] FAIL:"));
        var r = CheckAvatar.Inspect("NoSuchRoot_xyz");
        StringAssert.StartsWith("[CheckAvatar] FAIL:", r);
        Assert.IsFalse(r.Contains("| log="), "bad input carries no artifact trailer: " + r);
    }

    // ── Real dynamics reflection: type/getter canary + null-root extraction ───────────────────────────

    [Test] public void Canary_DynamicsTypesAndGettersResolve()
    {
        foreach (var c in CheckAvatar.DynamicsCategories)
            AssertTypeGetter(c.typeName, c.getter);
        // pin ColliderDetail's field names on the real collider type (a rename must go red, not silently blank the detail)
        var col = CheckSeam.FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider");
        Assert.IsNotNull(col, "collider type unresolved (drift)");
        foreach (var f in new[] { "shapeType", "radius", "height" })
            Assert.IsNotNull(col.GetField(f), "collider field unresolved (drift): " + f);
    }

    private static void AssertTypeGetter(string typeName, string getter)
    {
        var t = CheckSeam.FindType(typeName);
        Assert.IsNotNull(t, "type unresolved (drift): " + typeName);
        Assert.IsNotNull(t.GetMethod(getter, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null),
            "getter unresolved (drift): " + typeName + "." + getter);
    }

    [Test] public void CollectDynamics_RealPhysbone_NullRoot_UsesOwnTransform()
    {
        var root = NewAvatar("PB");
        var pbType = CheckSeam.FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
        Assert.IsNotNull(pbType);
        var child = NewChild(root, "Bone");
        child.AddComponent(pbType); // rootTransform defaults null
        var targets = CheckAvatar.CollectDynamicsTargets(root); // real default
        Assert.IsTrue(targets.Exists(x => x.category == "physbone" && x.target == child.transform),
            "physbone with null rootTransform should target its own transform");
    }

    // ── anchor-seam: the MA build-time move that kills a binding the scene still resolves ─────────────
    //
    // The true positive these are modelled on is vrc-patterns/selective-animation before #26: a VRCFury
    // FullController on the module root, an MA BoneProxy on an interior `Aim` node, and clips binding
    // `Aim/Origin/Beam/...`. Every one of those bindings resolved in the placed scene and came out of the
    // merged FX with zero curves. The negatives are the shape five shipped entries actually use — a proxied
    // anchor that is referenced by object and never animated — which is why the root and leaf endpoints are
    // asserted separately from the interior case.
    //
    // Every proxy here is given a RESOLVABLE target (boneReference LastBone + subPath, which MA resolves as
    // avatarTransform.Find(subPath) with no humanoid rig needed). The predicate deliberately does not read
    // the target — an unresolved anchor is a broken module, not a licence — but a fixture whose proxy MA
    // would refuse to move is not the rig this class exists for, and would keep passing if the predicate
    // ever started reading it. AnchorSeam_proxyWithNoTarget_isStillCounted pins that policy on its own.

    private Component AddMaBoneProxy(GameObject go, string subPath)
    {
        var t = Resolve("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
        Assert.IsNotNull(t, "MA BoneProxy type must resolve");
        var c = go.AddComponent(t);
        var so = new SerializedObject(c);
        so.FindProperty("subPath").stringValue = subPath; // boneReference stays LastBone ⇒ avatar-root-relative Find
        so.ApplyModifiedPropertiesWithoutUndo();
        return c;
    }

    private Component AddMaComponent(GameObject go, string typeName)
    {
        var t = Resolve(typeName);
        Assert.IsNotNull(t, typeName + " must resolve");
        return go.AddComponent(t);
    }

    // A VRCFury ArmatureLink whose Link From is propBone.
    private Component AddVrcfArmatureLink(GameObject go, GameObject propBone)
    {
        var vt = Resolve("VF.Model.VRCFury");
        var ft = Resolve("VF.Model.Feature.ArmatureLink");
        Assert.IsNotNull(vt); Assert.IsNotNull(ft, "VF.Model.Feature.ArmatureLink must resolve");
        var c = go.AddComponent(vt);
        var so = new SerializedObject(c);
        so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(ft);
        so.ApplyModifiedPropertiesWithoutUndo();
        so = new SerializedObject(c);
        so.FindProperty("content").FindPropertyRelative("propBone").objectReferenceValue = propBone;
        so.ApplyModifiedPropertiesWithoutUndo();
        return c;
    }

    // avatar → Prop → Aim → Origin → Beam, plus a Payload sibling of Aim under Prop and a Bone the proxies
    // can resolve onto. Returns Prop.
    private GameObject NewSeamRig(string avatarName, out GameObject aim, out GameObject payload)
    {
        var a = NewAvatar(avatarName);
        NewChild(a, "Bone");
        var prop = NewChild(a, "Prop");
        aim = NewChild(prop, "Aim");
        var origin = NewChild(aim, "Origin");
        NewChild(origin, "Beam");
        payload = NewChild(prop, "Payload");
        return prop;
    }

    private static int SeamCount(string summary)
    {
        var m = System.Text.RegularExpressions.Regex.Match(summary, @"anchorSeam=(\d+)");
        Assert.IsTrue(m.Success, "summary carries no anchorSeam count: " + summary);
        return int.Parse(m.Groups[1].Value);
    }

    [Test]
    public void AnchorSeam_vrcfBindingThroughInteriorBoneProxy_isClassified()
    {
        var prop = NewSeamRig("SeamPositive", out var aim, out _);
        AddMaBoneProxy(aim, "Bone");
        AddVrcfFullController(prop, NewController("SeamCtrl", NewClip(TmpDir, "seam_beam", "Aim/Origin/Beam")), prop);

        var res = Inspect("SeamPositive");
        Assert.AreEqual(1, SeamCount(res), res);
        StringAssert.Contains("CLASSIFY", res);
        var log = ReadLog(res);
        StringAssert.Contains("**anchor-seam**", log);
        StringAssert.Contains("moved-by=MA BoneProxy", log);
        StringAssert.Contains("Aim/Origin/Beam", log);
        StringAssert.Contains(CheckAvatar.AnchorSeamNoteLine, log); // the repair is stated, not left to inference
    }

    [Test]
    public void AnchorSeam_proxiedAnchorNeverAnimated_isPass()
    {
        var prop = NewSeamRig("SeamAnchorOnly", out var aim, out _);
        AddMaBoneProxy(aim, "Bone"); // referenced by object, never path-animated — what five shipped entries do
        AddVrcfFullController(prop, NewController("SeamCtrl2", NewClip(TmpDir, "seam_payload", "Payload")), prop);

        var res = Inspect("SeamAnchorOnly");
        Assert.AreEqual(0, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_moduleRootItselfProxied_isPass()
    {
        var prop = NewSeamRig("SeamRootAnchored", out _, out _);
        AddMaBoneProxy(prop, "Bone"); // the frame root moves WHOLESALE — the documented safe case
        AddVrcfFullController(prop, NewController("SeamCtrl3", NewClip(TmpDir, "seam_root", "Aim/Origin/Beam")), prop);

        var res = Inspect("SeamRootAnchored");
        Assert.AreEqual(0, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_mergeArmatureAtTheFrameRoot_isClassified()
    {
        // The root exemption is WHOLESALE-ONLY. MergeArmature reparents each matched bone individually onto a
        // different base bone and renames it, so the subtree scatters and an interior binding dies even though
        // the mover sits at the frame root — the one shape where excluding the root would be a false negative.
        var prop = NewSeamRig("SeamScatterRoot", out _, out _);
        AddMaComponent(prop, "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
        AddVrcfFullController(prop, NewController("SeamCtrl4", NewClip(TmpDir, "seam_scatter", "Aim/Origin/Beam")), prop);

        var res = Inspect("SeamScatterRoot");
        Assert.AreEqual(1, SeamCount(res), res);
        StringAssert.Contains("moved-by=MA MergeArmature", ReadLog(res));
    }

    [Test]
    public void AnchorSeam_mergeArmatureAboveTheMount_isClassified()
    {
        // The mover is an ANCESTOR of the FullController's mount, which a leaf-to-root walk can never reach —
        // so registering only the component's own GameObject reported nothing here. MergeArmature relocates
        // every node beneath it individually and mangleNames (default true) renames each one, so the interior
        // nodes ARE movers and the walk finds them between the frame root and the leaf.
        var a = NewAvatar("SeamScatterAbove");
        var outfit = NewChild(a, "Outfit");
        AddMaComponent(outfit, "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
        var hips = NewChild(outfit, "Hips");
        var mount = NewChild(hips, "Mount");
        NewChild(NewChild(mount, "Aim"), "Origin");
        AddVrcfFullController(mount, NewController("SeamCtrlAbove", NewClip(TmpDir, "seam_above", "Aim/Origin")), mount);

        var res = Inspect("SeamScatterAbove");
        Assert.AreEqual(1, SeamCount(res), res);
        StringAssert.Contains("moved-by=MA MergeArmature", ReadLog(res));
    }

    [Test]
    public void AnchorSeam_proxiedNodeIsTheAnimatedLeaf_isClassified()
    {
        var prop = NewSeamRig("SeamLeaf", out var aim, out _);
        AddMaBoneProxy(aim, "Bone");
        AddVrcfFullController(prop, NewController("SeamCtrl5", NewClip(TmpDir, "seam_leaf", "Aim")), prop);

        // Leaf-INCLUSIVE: the nearest-match walk fails on `Aim` exactly as it fails on `Aim/Origin/Beam`,
        // so excluding the leaf would buy only a false negative.
        var res = Inspect("SeamLeaf");
        Assert.AreEqual(1, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_worldFixedObjectIsAMover()
    {
        // The mover set is an enumerated allowlist, so each member needs its own case: a hole here is silent.
        var prop = NewSeamRig("SeamWorldFixed", out var aim, out _);
        AddMaComponent(aim, "nadena.dev.modular_avatar.core.ModularAvatarWorldFixedObject");
        AddVrcfFullController(prop, NewController("SeamCtrl6", NewClip(TmpDir, "seam_wf", "Aim/Origin")), prop);

        var res = Inspect("SeamWorldFixed");
        Assert.AreEqual(1, SeamCount(res), res);
        StringAssert.Contains("moved-by=MA WorldFixedObject", ReadLog(res));
    }

    [Test]
    public void AnchorSeam_visibleHeadAccessoryIsAMover()
    {
        var prop = NewSeamRig("SeamVisHead", out var aim, out _);
        AddMaComponent(aim, "nadena.dev.modular_avatar.core.ModularAvatarVisibleHeadAccessory");
        AddVrcfFullController(prop, NewController("SeamCtrl7", NewClip(TmpDir, "seam_vh", "Aim/Origin")), prop);

        var res = Inspect("SeamVisHead");
        Assert.AreEqual(1, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_proxyWithNoTarget_isStillCounted()
    {
        // Policy, pinned on its own: MA moves nothing for an unresolved proxy (ProcessProxy guards the
        // SetParent), but an unresolved anchor is a broken module rather than a licence to animate through it.
        var prop = NewSeamRig("SeamNoTarget", out var aim, out _);
        AddMaBoneProxy(aim, ""); // no subPath, boneReference LastBone ⇒ target resolves to null
        AddVrcfFullController(prop, NewController("SeamCtrl8", NewClip(TmpDir, "seam_notarget", "Aim/Origin")), prop);

        var res = Inspect("SeamNoTarget");
        Assert.AreEqual(1, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_maClipThroughVrcfArmatureLink_isPass()
    {
        // The MIRROR direction is NOT a break, and this is the regression that holds the correction. VRCFury
        // relocates through ObjectMoveService, whose ApplyDeferred rewrites every clip in
        // ControllersService.GetAllUsedControllers() — the descriptor's controllers, which by then already
        // carry what MA merged at -11000. FeatureOrder orders FullController before ArmatureLink for exactly
        // this reason. Flagging it would hard-FAIL a working entry in the vrc-patterns gate.
        var prop = NewSeamRig("SeamMirror", out var aim, out _);
        AddVrcfArmatureLink(prop, aim);
        AddMaMergeAnimator(prop, NewController("SeamCtrl9", NewClip(TmpDir, "seam_mirror", "Aim/Origin")));

        var res = Inspect("SeamMirror");
        Assert.AreEqual(0, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_moveAndAnimateInOneFramework_isPass()
    {
        var prop = NewSeamRig("SeamOneFramework", out var aim, out _);
        AddVrcfArmatureLink(prop, aim); // VRCFury moves it AND VRCFury merges the clips — #26's fix shape
        AddVrcfFullController(prop, NewController("SeamCtrl10", NewClip(TmpDir, "seam_one", "Aim/Origin/Beam")), prop);

        var res = Inspect("SeamOneFramework");
        Assert.AreEqual(0, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_maMoveUnderMaMergeAnimator_isPass()
    {
        var prop = NewSeamRig("SeamMaOnly", out var aim, out _);
        AddMaBoneProxy(aim, "Bone"); // MA moves it AND MA merges the clips — NDMF's ObjectPathRemapper repaths
        AddMaMergeAnimator(prop, NewController("SeamCtrl11", NewClip(TmpDir, "seam_maonly", "Aim/Origin")));

        var res = Inspect("SeamMaOnly");
        Assert.AreEqual(0, SeamCount(res), res);
    }

    [Test]
    public void AnchorSeam_animatorTypedBinding_isPass()
    {
        // FullControllerBuilder composes AnimatorBindingsAlwaysTargetRoot after the nearest-match walk, so an
        // Animator-typed binding is retargeted to the avatar's Animator whatever its authored path.
        var prop = NewSeamRig("SeamAnimatorTyped", out var aim, out _);
        AddMaBoneProxy(aim, "Bone");
        // The Animator component is what makes this test bite: without it the binding never RESOLVES, so it
        // would be skipped as unresolved and the test would pass with the type-skip deleted.
        aim.AddComponent<Animator>();
        var clip = new AnimationClip { name = "seam_animtyped" };
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("Aim", typeof(Animator), "m_Enabled"), AnimationCurve.Linear(0, 0, 1, 1));
        AssetDatabase.CreateAsset(clip, TmpDir + "/seam_animtyped.anim");
        AddVrcfFullController(prop, NewController("SeamCtrl12", clip), prop);

        var res = Inspect("SeamAnimatorTyped");
        Assert.AreEqual(0, SeamCount(res), res);
        StringAssert.Contains("clipBinding=0", res); // resolved-and-skipped, not quietly reclassified
    }

    [Test]
    public void AnchorSeam_unresolvedBinding_staysInTheClipBindingClass()
    {
        var prop = NewSeamRig("SeamUnresolved", out var aim, out _);
        AddMaBoneProxy(aim, "Bone");
        AddVrcfFullController(prop, NewController("SeamCtrl13", NewClip(TmpDir, "seam_dead", "Aim/Gone/Beam")), prop);

        var res = Inspect("SeamUnresolved");
        Assert.AreEqual(0, SeamCount(res), res); // an unresolved binding is a break the scene ALREADY shows
        StringAssert.Contains("clipBinding=1", res);
    }

    [Test]
    public void ScanAnchorSeams_bareModuleWithNoDescriptor_findsTheSeam()
    {
        // The vrc-patterns gate's home: an entry prefab instantiated on its own. There is no avatar root and
        // no descriptor, so the mount root is the only frame there is.
        var prop = new GameObject("BareModule");
        _avatar = prop; // TearDown destroys it
        var aim = NewChild(prop, "Aim");
        NewChild(NewChild(aim, "Origin"), "Beam");
        AddMaBoneProxy(aim, "Bone");
        AddVrcfFullController(prop, NewController("SeamCtrl14", NewClip(TmpDir, "seam_bare", "Aim/Origin/Beam")), prop);

        var lines = CheckAvatar.ScanAnchorSeams(prop);
        Assert.AreEqual(1, lines.Count, string.Join(" | ", lines));
        StringAssert.Contains("MA BoneProxy", lines[0]);
    }

    [Test]
    public void ScanAnchorSeams_degradedFrameRead_isReportedNotSwallowed()
    {
        // The gate FAILs on any line this returns, so a frame that did not reflect must produce one. Otherwise
        // an MA/VRCFury field rename turns the gate's whole seam pass into a silent no-op at exactly the moment
        // the drift it guards against occurs.
        var prop = new GameObject("DegradedModule");
        _avatar = prop;
        var aim = NewChild(prop, "Aim");
        NewChild(aim, "Origin");
        AddMaBoneProxy(aim, "Bone");
        AddVrcfFullController(prop, NewController("SeamCtrl15", NewClip(TmpDir, "seam_degraded", "Payload")), prop);
        SetSeam("FrameAnchorOverride", (Func<string, string>)(_ => "VRCF.content")); // force the fail-loud branch

        var lines = CheckAvatar.ScanAnchorSeams(prop);
        Assert.IsNotEmpty(lines, "a degraded frame read must fail the gate, not pass it");
        Assert.IsTrue(lines.Exists(l => l.StartsWith(CheckAvatar.DegradedPrefix)), string.Join(" | ", lines));
    }

    [Test]
    public void ScanAnchorSeams_maOnlyModule_raisesNothing()
    {
        // The door FAILs the gate on any line it returns, so it must not manufacture one for a surface that
        // structurally cannot carry this break. A bare MA-merged module has no descriptor ancestor, so its
        // frame read is uncertain — a note that would hard-FAIL an entry with no FullController at all.
        var prop = new GameObject("MaOnlyModule");
        _avatar = prop;
        var aim = NewChild(prop, "Aim");
        NewChild(aim, "Origin");
        AddMaBoneProxy(aim, "Bone");
        AddMaMergeAnimator(prop, NewController("SeamCtrlMaOnly", NewClip(TmpDir, "seam_maonly_gate", "Aim/Origin")));

        Assert.IsEmpty(CheckAvatar.ScanAnchorSeams(prop));
    }
}

// The merge-conflict grouping core. Each test injects fake merge→base pairs + fake dynamics targets via the
// two seams, so it proves the pure grouping/resolution logic on synthetic transforms — a child GameObject's
// .transform is a fine stand-in for a dynamics Component host.
//
// Its own fixture because it needs NONE of CheckAvatarTests' scaffolding: no clip, no controller, no scratch
// folder, no saved scene. One scene for the whole fixture; per-test cleanup is a DestroyImmediate plus a seam
// restore (both free), and the RunLogs are removed in one batch at the end.
public class CheckAvatarMergeConflictTests
{
    private GameObject _root;
    private object _origMergePairs, _origDynamics;
    private readonly List<string> _logs = new List<string>();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _origMergePairs = CheckAvatarFixture.GetSeam("ResolveMergePairs");
        _origDynamics = CheckAvatarFixture.GetSeam("CollectDynamicsTargets");
    }

    [SetUp]
    public void SetUp() => LogAssert.ignoreFailingMessages = true; // CLASSIFY logs a warning — expected

    [TearDown]
    public void TearDown()
    {
        if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
        _root = null;
        CheckAvatarFixture.SetSeam("ResolveMergePairs", _origMergePairs);
        CheckAvatarFixture.SetSeam("CollectDynamicsTargets", _origDynamics);
        LogAssert.ignoreFailingMessages = false;
    }

    [OneTimeTearDown]
    public void DeleteRunLogs()
    {
        if (_logs.Count > 0) AssetDatabase.DeleteAssets(_logs.ToArray(), new List<string>());
        _logs.Clear();
    }

    private GameObject NewAvatar(string name) => _root = CheckAvatarFixture.Avatar(name);
    private static GameObject NewChild(GameObject parent, string name) => CheckAvatarFixture.Child(parent, name);

    // The RunLog body for a fresh Inspect of the fixture root, with the artifact recorded for teardown.
    private string InspectLog()
    {
        var path = CheckAvatarFixture.LogPath(CheckAvatar.Inspect(_root.name));
        if (path == null) return "";
        _logs.Add(path);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private static Func<GameObject, (List<(Transform, Transform)>, string)> Pairs(
        List<(Transform, Transform)> pairs, string note = null) => _ => (pairs, note);

    private static Func<GameObject, List<(Component, Transform, string, string)>> Targets(
        params (Component host, Transform target, string category, string detail)[] t)
        => _ => t.Select(x => (x.host, x.target, x.category, x.detail)).ToList();

    // Avatar-root-relative path of a named child, matching CheckAvatar.PathOf output (Root/Child).
    private static string PathOf(GameObject root, string childName) => root.name + "/" + childName;

    [Test]
    public void MergeConflict_PhysboneMergedOntoBase_IsClassified()
    {
        var root = NewAvatar("MC1");
        var baseTail = NewChild(root, "BaseTail").transform;
        var mergeTail = NewChild(root, "MergeTail").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeTail, baseTail) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (mergeTail, mergeTail, "physbone", ""), (baseTail, baseTail, "physbone", ""));
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains("=> CLASSIFY", log);
        StringAssert.Contains("[mergeable]", log);
        StringAssert.Contains("[base]", log);
    }

    [Test]
    public void MergeConflict_ColliderDuplicate_CarriesShapeDetail()
    {
        var root = NewAvatar("MCcol");
        var baseCol = NewChild(root, "BaseCol").transform;
        var mergeCol = NewChild(root, "MergeCol").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeCol, baseCol) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (mergeCol, mergeCol, "collider", "shape=Sphere radius=0.1 height=0"),
            (baseCol, baseCol, "collider", "")); // colliders group; detail emitted for the mergeable one
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains("=> CLASSIFY", log);
        StringAssert.Contains("category=`collider`", log);
        StringAssert.Contains("radius=0.1", log); // the ", " + h.Detail emit branch
    }

    [Test]
    public void MergeConflict_BaseToBaseDuplicate_IsDropped()
    {
        var root = NewAvatar("MC2");
        var a = NewChild(root, "A").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)>());
        CheckAvatar.CollectDynamicsTargets = Targets(
            (a, a, "physbone", ""), (a, a, "physbone", ""));
        StringAssert.Contains("mergeConflict=0", InspectLog());
    }

    // A base bone carrying a per-range variant set reads as N components fighting unless each offender says
    // whether it is running: the group a mergeable joins is then 1 real conflict + N intentional variants,
    // and de-conflicting against a not-live member is silent. Both markers are spelled in ONE report here —
    // the strongest form of "an all-live report is distinguishable from one produced before liveness was
    // evaluated at all". The note fires once and only for a mixed-live PHYSBONE group, and none of it touches
    // the ≥2-with-a-mergeable predicate.
    [Test]
    public void MergeConflict_MixedLivePhysboneGroup_IsMarkedAndNoted()
    {
        var root = NewAvatar("MCLive");
        var baseBone = NewChild(root, "BaseBone").transform;
        var variantOff = NewChild(root, "VariantOff");
        variantOff.SetActive(false);
        var mergeBone = NewChild(root, "MergeBone").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeBone, baseBone) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (mergeBone, mergeBone, "physbone", ""),
            (baseBone, baseBone, "physbone", ""),
            (variantOff.transform, baseBone, "physbone", ""));
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains("[not-live] " + root.name + "/VariantOff", log);
        StringAssert.Contains("[live] " + root.name + "/MergeBone", log);
        StringAssert.Contains("[live] " + root.name + "/BaseBone", log);
        // The GATE is the contract, not the wording — assert the canon's own constant so a prose pass over
        // CheckAvatar's Notes cannot red this test (and its three negatives) for no behaviour change.
        StringAssert.Contains(CheckAvatar.VariantSetNoteLine, log);
    }

    // The note speaks about physbone variant sets, so an unrelated inactive collider must not summon it.
    [Test]
    public void MergeConflict_NotLiveCollider_MarkedButNotNoted()
    {
        var root = NewAvatar("MCLiveCol");
        var baseCol = NewChild(root, "BaseCol").transform;
        var mergeColOff = NewChild(root, "MergeColOff");
        mergeColOff.SetActive(false);
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeColOff.transform, baseCol) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (mergeColOff.transform, mergeColOff.transform, "collider", ""),
            (baseCol, baseCol, "collider", ""));
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains("[not-live] " + root.name + "/MergeColOff", log);
        StringAssert.DoesNotContain(CheckAvatar.VariantSetNoteLine, log);
    }

    // A VRC constraint is a Behaviour but carries a SECOND enable flag, `IsActive`. Testing `enabled` alone
    // reports an inert constraint as fighting — the category's enable surface is not the Behaviour's.
    [Test]
    public void MergeConflict_ConstraintIsActiveFalse_IsNotLive()
    {
        var root = NewAvatar("MCLiveCon");
        var baseT = NewChild(root, "BaseT").transform;
        var mergeGo = NewChild(root, "MergeT");
        var con = mergeGo.AddComponent<VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint>();
        con.IsActive = false;   // Behaviour stays enabled, object stays active
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeGo.transform, baseT) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (con, mergeGo.transform, "constraint", ""), (baseT, baseT, "constraint", ""));
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains("[not-live] " + root.name + "/MergeT", log);
    }

    [Test]
    public void MergeConflict_ConstraintIsActiveTrue_IsLive()
    {
        var root = NewAvatar("MCLiveCon2");
        var baseT = NewChild(root, "BaseT").transform;
        var mergeGo = NewChild(root, "MergeT");
        var con = mergeGo.AddComponent<VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint>();
        con.IsActive = true;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeGo.transform, baseT) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (con, mergeGo.transform, "constraint", ""), (baseT, baseT, "constraint", ""));
        var log = InspectLog();
        StringAssert.Contains("[live] " + root.name + "/MergeT", log);
        StringAssert.DoesNotContain("[not-live]", log);
    }

    // Liveness is relative to the avatar root: parking the avatar inactive must not flip every host to
    // not-live and fire the note over a real conflict. This is also the all-live PHYSBONE group's no-note
    // case — the variant-set note's gate is mixed-live, not merely physbone.
    [Test]
    public void MergeConflict_InactiveAvatarRoot_LivenessStaysRelative()
    {
        var root = NewAvatar("MCLiveRoot");
        var baseBone = NewChild(root, "BaseBone").transform;
        var mergeBone = NewChild(root, "MergeBone").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeBone, baseBone) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (mergeBone, mergeBone, "physbone", ""), (baseBone, baseBone, "physbone", ""));
        root.SetActive(false);
        var log = InspectLog();
        StringAssert.DoesNotContain("[not-live]", log);
        StringAssert.DoesNotContain(CheckAvatar.VariantSetNoteLine, log);
        StringAssert.Contains(CheckAvatar.InactiveRootNoteLine, log);
    }

    [Test]
    public void MergeConflict_CategoryIsolation_PhysboneAndColliderNotAConflict()
    {
        var root = NewAvatar("MC3");
        var m = NewChild(root, "M").transform;
        var b = NewChild(root, "B").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (m, b) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (m, m, "physbone", ""), (b, b, "collider", "radius=0.1")); // both resolve to b, different categories
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=0", log); // two groups of one → no conflict
        StringAssert.Contains("=> PASS", log);
    }

    [Test]
    public void MergeConflict_TwoMergeablesOntoOneBase_IsClassified()
    {
        var root = NewAvatar("MC4");
        var m1 = NewChild(root, "M1").transform;
        var m2 = NewChild(root, "M2").transform;
        var b = NewChild(root, "B").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (m1, b), (m2, b) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (m1, m1, "physbone", ""), (m2, m2, "physbone", ""));
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log); // {m1,m2} share final b, both mergeable
        StringAssert.Contains("=> CLASSIFY", log);
    }

    [Test]
    public void MergeConflict_TransitiveChain_ResolvesToRootBase()
    {
        var root = NewAvatar("MC5");
        var a = NewChild(root, "A").transform;
        var b = NewChild(root, "B").transform;
        var c = NewChild(root, "C").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (a, b), (b, c) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (a, a, "physbone", ""), (c, c, "physbone", "")); // a→b→c and c → same final c
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains("final=`" + PathOf(root, "C") + "`", log);
    }

    [Test]
    public void MergeConflict_CycleGuard_Terminates()
    {
        var root = NewAvatar("MC6");
        var a = NewChild(root, "A").transform;
        var b = NewChild(root, "B").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (a, b), (b, a) }); // cycle
        CheckAvatar.CollectDynamicsTargets = Targets((a, a, "physbone", ""));
        string log = null;
        Assert.DoesNotThrow(() => log = InspectLog(), "cycle-guarded ResolveFinal must terminate");
        StringAssert.Contains("mergeConflict=0", log); // single host → no conflict, but run completed
    }

    [Test]
    public void MergeConflict_NullSidedPair_SkippedNoThrow()
    {
        var root = NewAvatar("MC7");
        var m = NewChild(root, "M").transform;
        var b = NewChild(root, "B").transform;
        var b2 = NewChild(root, "B2").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (null, b), (m, null), (m, b2) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (m, m, "physbone", ""), (b2, b2, "physbone", "")); // m→b2 (the only surviving pair) → shared final
        string log = null;
        Assert.DoesNotThrow(() => log = InspectLog(), "null-sided pairs must be skipped, not thrown on");
        StringAssert.Contains("mergeConflict=1", log); // proves map ended with m→b2
    }

    [Test]
    public void MergeConflict_FirstWinsOnDuplicateKey()
    {
        var root = NewAvatar("MC8");
        var m = NewChild(root, "M").transform;
        var b1 = NewChild(root, "B1").transform;
        var b2 = NewChild(root, "B2").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (m, b1), (m, b2) }); // dup key m
        CheckAvatar.CollectDynamicsTargets = Targets(
            (m, m, "physbone", ""), (b1, b1, "physbone", "")); // m resolves to first-won b1
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log); // m→b1 shared with b1's own physbone
        StringAssert.Contains("final=`" + PathOf(root, "B1") + "`", log);
    }

    // The empty fixture: no pairs, no targets. Also the PASS floor — the seam's partial-map note rides on the
    // same run, so one test covers both (an "empty everything is PASS" twin asserted strictly less).
    [Test]
    public void MergeConflict_PartialMapNote_Surfaces()
    {
        var root = NewAvatar("MC9");
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)>(), "merge map partial — seam X did not resolve");
        CheckAvatar.CollectDynamicsTargets = Targets();
        var log = InspectLog();
        StringAssert.Contains("merge map partial — seam X did not resolve", log);
        StringAssert.Contains("mergeConflict=0", log);
        StringAssert.Contains("=> PASS", log);
    }
}
