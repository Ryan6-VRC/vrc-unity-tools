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

// CheckAvatar proof obligations (spec 2026-07-07-avatarlint-design.md, Acceptance criteria).
//
// CheckAvatar.Inspect resolves scene paths against the ACTIVE scene (its local FindByHierarchyPath), so —
// like CheckAnimatorRefactorTests — fixtures live in the active scene and are torn down in place. Nothing is
// saved: temp controllers/clips + the emitted RunLog are deleted in TearDown (the no-dirty test saves the
// throwaway scene into TmpDir, which TearDown removes); the real scene file is never written. MA/VRCFury are
// the REAL installed types (reflection AddComponent), the same path the tool detects them on. The internal
// test seams are flipped via reflection (Tests is a separate assembly), which is also how they are exercised
// live via execute_code.
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
        // The no-dirty test saves the throwaway scene into TmpDir; replace it with a fresh Single scene so
        // the file being deleted with TmpDir below is never the loaded active scene.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
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
        typeof(CheckAvatar).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

    private static object GetSeam(string field) =>
        typeof(CheckAvatar).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

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
        StringAssert.Contains("maSceneRef=0 clipBinding=0 mergeConflict=0 => PASS", r, r);
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

    // ── VRCF rewriteBindings (D-A step 1) — the RemyDoll downward-relocation case ──────────────────────

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
        StringAssert.Contains("clipBinding=0 mergeConflict=0 => PASS", r, "a delete-ruled binding is not a break: " + r);
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
        // dirty and the assertion would only prove Inspect preserves an already-dirty scene.
        string scenePath = TmpDir + "/NoDirtyScene.unity";
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene, scenePath);
        Assert.IsFalse(scene.isDirty, "baseline must be a clean scene");
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
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckAvatar\] FAIL:"));
        var r = CheckAvatar.Inspect("NoSuchRoot_xyz");
        StringAssert.StartsWith("[CheckAvatar] FAIL:", r);
        Assert.IsFalse(r.Contains("| log="), "bad input carries no artifact trailer: " + r);
    }

    // ── Merge-conflict grouping core (fakes-injected: no MA/VRCF/VRC-dynamics types needed) ────────────
    // Each test injects fake merge→base pairs + fake dynamics targets via the two seams, so it proves the
    // pure grouping/resolution logic on synthetic transforms. A child GameObject's .transform is a fine
    // stand-in for a dynamics Component host. TearDown restores both seams.

    private static Func<GameObject, (List<(Transform, Transform)>, string)> Pairs(
        List<(Transform, Transform)> pairs, string note = null) => _ => (pairs, note);

    private static Func<GameObject, List<(Component, Transform, string, string)>> Targets(
        params (Component host, Transform target, string category, string detail)[] t)
        => _ => t.Select(x => (x.host, x.target, x.category, x.detail)).ToList();

    [Test]
    public void MergeConflict_PhysboneMergedOntoBase_IsClassified()
    {
        var root = NewAvatar("MC1");
        var baseTail = NewChild(root, "BaseTail").transform;
        var mergeTail = NewChild(root, "MergeTail").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeTail, baseTail) });
        CheckAvatar.CollectDynamicsTargets = Targets(
            (mergeTail, mergeTail, "physbone", ""), (baseTail, baseTail, "physbone", ""));
        var log = ReadLog(Inspect(root.name));
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
        var log = ReadLog(Inspect(root.name));
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
        var log = ReadLog(Inspect(root.name));
        StringAssert.Contains("mergeConflict=0", log);
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
        var log = ReadLog(Inspect(root.name));
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
        var log = ReadLog(Inspect(root.name));
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
        var log = ReadLog(Inspect(root.name));
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
        Assert.DoesNotThrow(() => log = ReadLog(Inspect(root.name)), "cycle-guarded ResolveFinal must terminate");
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
        Assert.DoesNotThrow(() => log = ReadLog(Inspect(root.name)), "null-sided pairs must be skipped, not thrown on");
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
        var log = ReadLog(Inspect(root.name));
        StringAssert.Contains("mergeConflict=1", log); // m→b1 shared with b1's own physbone
        StringAssert.Contains("final=`" + PathOf(root, "B1") + "`", log);
    }

    [Test]
    public void MergeConflict_PartialMapNote_Surfaces()
    {
        var root = NewAvatar("MC9");
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)>(), "merge map partial — seam X did not resolve");
        CheckAvatar.CollectDynamicsTargets = Targets();
        var log = ReadLog(Inspect(root.name));
        StringAssert.Contains("merge map partial — seam X did not resolve", log);
        StringAssert.Contains("mergeConflict=0", log);
    }

    [Test]
    public void MergeConflict_EmptyEverything_IsPass()
    {
        var root = NewAvatar("MC10");
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)>());
        CheckAvatar.CollectDynamicsTargets = Targets();
        var log = ReadLog(Inspect(root.name));
        StringAssert.Contains("mergeConflict=0", log);
        StringAssert.Contains("=> PASS", log);
    }

    // Avatar-root-relative path of a named child, matching CheckAvatar.PathOf output (Root/Child).
    private static string PathOf(GameObject root, string childName) => root.name + "/" + childName;
}
