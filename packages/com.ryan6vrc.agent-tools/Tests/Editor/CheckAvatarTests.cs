using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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

    // Seams live on CheckAvatar (its own merge seams), VendorReflect (the shared AOR boxing/pin seams),
    // or MergeSurfaces (the shared surface enumeration's) — one lookup covers every home so a test names
    // the seam, not the type that holds it, and lifting code into shared use is not a test edit.
    private static FieldInfo SeamField(string field) =>
        typeof(CheckAvatar).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)
        ?? typeof(VendorReflect).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)
        ?? typeof(MergeSurfaces).GetField(field, BindingFlags.NonPublic | BindingFlags.Static);

    internal static void SetSeam(string field, object value) => SeamField(field).SetValue(null, value);

    internal static object GetSeam(string field) => SeamField(field).GetValue(null);

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
// CheckAvatar.Run resolves scene paths against the ACTIVE scene (its local FindByHierarchyPath), so —
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
    private object _origMergePairs, _origDynamics, _origVrcfRewrite;

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
        _origResolve = GetSeam("ResolveAorGetOverload");
        _origAnchor = GetSeam("FrameAnchorOverride");
        _origMergePairs = GetSeam("ResolveMergePairs");
        _origDynamics = GetSeam("CollectDynamicsTargets");
        _origVrcfRewrite = GetSeam("ResolveVrcfRewritePath");
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
        SetSeam("ResolveAorGetOverload", _origResolve);
        SetSeam("FrameAnchorOverride", _origAnchor);
        SetSeam("ResolveMergePairs", _origMergePairs);
        SetSeam("CollectDynamicsTargets", _origDynamics);
        SetSeam("ResolveVrcfRewritePath", _origVrcfRewrite);
    }

    private static string Inspect(string root) => CheckAvatar.Run(root);

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
        => AddMaMergeAnimator(go, ctrl, null, null);

    // Relative MergeAnimator. refPath/targetGO set the relativePathRoot's two halves INDEPENDENTLY (a null
    // refPath leaves both untouched) — the frame walk's whole contract is which half wins when, so the
    // fixture must be able to write the inconsistent combinations a real scene can carry.
    private Component AddMaMergeAnimator(GameObject go, AnimatorController ctrl, string refPath, GameObject targetGO)
    {
        var t = Resolve("nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator");
        Assert.IsNotNull(t, "MA MergeAnimator type must resolve");
        var c = go.AddComponent(t);
        var so = new SerializedObject(c);
        so.FindProperty("animator").objectReferenceValue = ctrl;
        so.FindProperty("pathMode").enumValueIndex = 0; // Relative
        if (refPath != null)
        {
            var rel = so.FindProperty("relativePathRoot");
            rel.FindPropertyRelative("referencePath").stringValue = refPath;
            rel.FindPropertyRelative("targetObject").objectReferenceValue = targetGO;
        }
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

    // ── The intentional-empty exemption is BOTH halves, not the path alone ────────────────────────────
    // An empty referencePath carrying a live targetObject is the silent no-op nondestructive.md names: the
    // inspector's resolver checks targetObject first and shows it resolved, while the Get(Component) the
    // build takes returns null on the empty path. Exempting on the path alone made the scan blind to it.

    [Test]
    public void EmptyPath_withLiveTargetObject_isAnOffender()
    {
        var a = NewAvatar("LintEmptyTarget");
        var body = NewChild(a, "Body_Base");
        var outfit = NewChild(a, "Outfit");
        AddMaObjectToggle(outfit, "", body); // targetObject set, path never written

        var r = Inspect("LintEmptyTarget");
        StringAssert.Contains("maSceneRef=1", r, "the targetObject-only ref must be named, not exempted: " + r);
        StringAssert.Contains("unset referencePath", ReadLog(r), "the offender says which half is missing");
    }

    [Test]
    public void EmptyPath_withNoTargetObject_staysExempt()
    {
        var a = NewAvatar("LintEmptyBoth");
        var outfit = NewChild(a, "Outfit");
        AddMaObjectToggle(outfit, "", null); // both halves empty ⇒ unset by design

        var r = Inspect("LintEmptyBoth");
        StringAssert.Contains("maSceneRef=0", r, "a genuinely unset ref is not an offender: " + r);
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

    // A rewrite rule whose `to` starts with `/` emits VRCFury's absolute form: the build
    // (AnimationBindingUtils.ResolveTarget) resolves it from the avatar root with no ancestor walk. The
    // checker must do the same — probing the literal `/`-prefixed path against the walk roots resolves
    // nowhere and manufactures a false clip-binding offender on a build that works.
    [Test]
    public void Vrcf_rewriteBindings_absoluteRule_resolvesFromAvatarRoot()
    {
        var a = NewAvatar("LintVrcfAbs");
        NewChild(NewChild(a, "Armature"), "Bone");             // real location: avatar-root Armature/Bone
        var prop = NewChild(NewChild(a, "Deep"), "Prop");      // VRCF mount, nested away from the bone

        var clip = NewClip(TmpDir, "VrcfAbsClip", "Armature/Bone");
        var c = AddVrcfFullController(prop, NewController("VrcfAbsCtrl", clip), prop);
        SetVrcfRewriteBindings(c, ("Armature", "/Armature", false)); // absolute: resolve from avatar root

        var r = Inspect("LintVrcfAbs");
        StringAssert.Contains("clipBinding=0", r,
            "an absolute (leading-/) rewrite must resolve against the avatar root, not read as a break: " + r);
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

        SetSeam("ResolveAorGetOverload", (Func<Type, MethodInfo>)(_ => null)); // force the Get(Component) overload unreachable
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

    // ── W12/A1: the frame walk resolves relativePathRoot the way the BUILD does ───────────────────────
    //
    // MergeAnimatorProcessor calls relativePathRoot.Get(avatarRootTransform) and falls back to
    // merge.gameObject on null. Since A1, TryMaFrame INVOKES that same Get(Component) rather than mirroring
    // its order (W12's hand-copy was wrong on the day it landed — the drift class invoking closes), so these
    // tests now pin the invoke wiring end-to-end against MA's real resolution: empty path first, then an
    // IN-AVATAR targetObject, then the AVATAR_ROOT sentinel, then the path. Each fails against the pre-W12 walk.

    // The frame root TryMaFrame computes for c, via the same reflective call the B2 test above uses.
    private GameObject MaFrameRoot(Component c, GameObject avatarGO)
    {
        var args = new object[] { c, avatarGO, null, null };
        bool ok = (bool)typeof(CheckAnimator).GetMethod("TryMaFrame", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, args);
        Assert.IsTrue(ok, "the fixture MA MergeAnimator must be recognised as a frame");
        var frame = args[3];
        return (GameObject)frame.GetType().GetField("Root").GetValue(frame);
    }

    // Empty referencePath ⇒ Get(Component) returns null BEFORE reading targetObject ⇒ build mounts at the
    // component's own GameObject. Pre-W12 this returned the targetObject: the wrong-frame report W12 names.
    [Test]
    public void TryMaFrame_emptyPathLiveTarget_usesOwnGameObject()
    {
        var a = NewAvatar("LintMaEmptyPath");
        var outfit = NewChild(a, "Outfit");
        var decoy = NewChild(a, "Decoy");
        var c = AddMaMergeAnimator(outfit, NewController("MaEmptyCtrl", NewClip(TmpDir, "MaEmptyClip", "Bone")),
            "", decoy); // targetObject set, referencePath left empty — the shape W9's scan started flagging

        Assert.AreSame(outfit, MaFrameRoot(c, a),
            "an empty referencePath resolves to null whatever targetObject holds, so the build mounts at the own GameObject");
    }

    // Both MA Get overloads gate targetObject on IsChildOf(avatarRoot) — one pointing outside the avatar
    // resolves to nothing, and the non-empty path decides the frame instead.
    [Test]
    public void TryMaFrame_targetObjectOutsideAvatar_isIgnored()
    {
        var a = NewAvatar("LintMaOutside");
        var outfit = NewChild(a, "Outfit");
        var mount = NewChild(a, "Mount");
        var outsider = new GameObject("OutsideTheAvatar"); // sibling of the avatar root, not under it
        try
        {
            var c = AddMaMergeAnimator(outfit, NewController("MaOutsideCtrl", NewClip(TmpDir, "MaOutsideClip", "Bone")),
                "Mount", outsider);

            Assert.AreSame(mount, MaFrameRoot(c, a),
                "a targetObject outside the avatar root resolves under neither MA overload — the path decides the frame");
        }
        finally { UnityEngine.Object.DestroyImmediate(outsider); }
    }

    // referencePath == AvatarObjectReference.AVATAR_ROOT is a sentinel, not a hierarchy path: Get(Component)
    // returns the avatar root for it. Pre-W12 the walk fed it to Transform.Find, got null, and fell back to
    // the own GameObject — a Relative merge silently reported at the wrong depth.
    [Test]
    public void TryMaFrame_avatarRootSentinel_mountsAtAvatarRoot()
    {
        var a = NewAvatar("LintMaSentinel");
        var outfit = NewChild(a, "Outfit");
        var c = AddMaMergeAnimator(outfit, NewController("MaSentinelCtrl", NewClip(TmpDir, "MaSentinelClip", "Bone")),
            "$$$AVATAR_ROOT$$$", null);

        Assert.AreSame(a, MaFrameRoot(c, a), "the AVATAR_ROOT sentinel resolves to the avatar root, not a child named after it");
    }

    // Get(Component) redirects a path landing on a CHILDLESS "Armature" to the same-named sibling that has
    // children (MA issue #308 — avatars carrying a decoy armature to move the VRChat eye position). Skipping
    // that swap is silent: TryResolveSceneRef invokes MA's real Get, so it sees the swapped object and emits
    // neither a scene-ref offender nor an R-K caveat, while the frame walk mounts at the childless decoy and
    // every binding under it fails. Asserted BOTH ways round, because the offender flood is the visible half.
    [Test]
    public void TryMaFrame_childlessArmatureDecoy_takesThePopulatedSibling()
    {
        var a = NewAvatar("LintMaArmature");
        var body = NewChild(a, "Body");
        NewChild(body, "Armature");                       // the childless decoy — created first, so Find hits it
        var realArmature = NewChild(body, "Armature");    // the true armature
        NewChild(realArmature, "Bone");
        var outfit = NewChild(a, "Outfit");

        // The clip animates the bone relative to the frame, so it resolves against the true armature only.
        var c = AddMaMergeAnimator(outfit, NewController("MaArmatureCtrl", NewClip(TmpDir, "MaArmatureClip", "Bone")),
            "Body/Armature", null);

        Assert.AreSame(realArmature, MaFrameRoot(c, a),
            "a path landing on the childless 'Armature' decoy must redirect to the populated sibling, as Get(Component) does");

        var r = Inspect("LintMaArmature");
        ReadLog(r);
        StringAssert.Contains("clipBinding=0", r,
            "mounting at the decoy would fail EVERY binding under it, with no offender or caveat to say why: " + r);
    }

    // Precedence pair nothing else pins: the sentinel loses to a live in-avatar targetObject, because
    // Get(Component) tests targetObject BEFORE it tests AVATAR_ROOT.
    [Test]
    public void TryMaFrame_sentinelWithLiveTarget_prefersTheTarget()
    {
        var a = NewAvatar("LintMaSentinelTarget");
        var outfit = NewChild(a, "Outfit");
        var mount = NewChild(a, "Mount");
        var c = AddMaMergeAnimator(outfit, NewController("MaSentTgtCtrl", NewClip(TmpDir, "MaSentTgtClip", "Bone")),
            "$$$AVATAR_ROOT$$$", mount);

        Assert.AreSame(mount, MaFrameRoot(c, a), "targetObject is tested before the AVATAR_ROOT sentinel");
    }

    // No avatar root above the merge site: the build's FindAvatarTransformInParents misses, Get(Component)
    // returns null, and the merge lands on its own GameObject — even with a live targetObject. DetectAuto
    // surfaces the missing descriptor separately, so the frame staying honest here is not a silent fallback.
    [Test]
    public void TryMaFrame_noAvatarRoot_usesOwnGameObject()
    {
        var a = NewAvatar("LintMaNoRoot");
        var outfit = NewChild(a, "Outfit");
        var mount = NewChild(a, "Mount");
        var c = AddMaMergeAnimator(outfit, NewController("MaNoRootCtrl", NewClip(TmpDir, "MaNoRootClip", "Bone")),
            "Mount", mount);

        Assert.AreSame(outfit, MaFrameRoot(c, null),
            "with no avatar root to resolve against, nothing resolves and the build mounts at the own GameObject");
    }

    // ── A1: the frame walk INVOKES Get(Component), and the hand-walk survives only as the drift fallback ─

    // Get re-derives the avatar root internally, and FindAvatarTransformInParents walks to the OUTERMOST
    // descriptor — so a nested descriptor above the merge site no longer skews the frame the way the
    // nearest-descriptor avatarGO DetectAuto hands in could. The referencePath is stored relative to the
    // root the BUILD uses (the outermost), so resolving it against the nearest one reported a frame the
    // build never mounts. Fails against the pre-A1 hand-walk, which did Find(path) on avatarGO directly.
    [Test]
    public void TryMaFrame_nestedDescriptor_resolvesAgainstOutermostRoot()
    {
        var outer = NewAvatar("LintMaNested");
        var inner = NewChild(outer, "Inner");
        inner.AddComponent<VRCAvatarDescriptor>(); // nested descriptor: nearest-root ≠ outermost-root
        var mount = NewChild(inner, "Mount");
        var outfit = NewChild(inner, "Outfit");
        var c = AddMaMergeAnimator(outfit, NewController("MaNestedCtrl", NewClip(TmpDir, "MaNestedClip", "Bone")),
            "Inner/Mount", null); // outermost-relative, as the build stores it

        Assert.AreSame(mount, MaFrameRoot(c, inner),
            "Get walks to the outermost root, so the outermost-relative referencePath resolves even when the caller hands in the nearest descriptor");
    }

    // The invoke tier unreachable (MA API drift) ⇒ the hand-walk fallback resolves in Get's order, LOUD
    // both out-of-band (console) and in-band (the MA.Get(Component) anchor — the degrade can diverge from
    // the build under a nested descriptor, so the frame must carry its own caveat) — and still applies the
    // childless-"Armature" decoy swap the pre-W12 copy shed.
    [Test]
    public void TryMaFrame_getUnreachable_fallsBackLoud_withDecoySwap()
    {
        var a = NewAvatar("LintMaFallback");
        var body = NewChild(a, "Body");
        NewChild(body, "Armature");                    // childless decoy, created first so Find hits it
        var realArmature = NewChild(body, "Armature"); // the true armature
        NewChild(realArmature, "Bone");
        var outfit = NewChild(a, "Outfit");
        var c = AddMaMergeAnimator(outfit, NewController("MaFallbackCtrl", NewClip(TmpDir, "MaFallbackClip", "Bone")),
            "Body/Armature", null);

        SetSeam("ResolveAorGetOverload", (Func<Type, MethodInfo>)(_ => null)); // force the invoke tier unreachable
        LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("relativePathRoot resolve degraded"));

        var args = new object[] { c, a, null, null };
        Assert.IsTrue((bool)typeof(CheckAnimator).GetMethod("TryMaFrame", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, args), "the fixture MA MergeAnimator must be recognised as a frame");
        var frame = args[3];
        Assert.AreSame(realArmature, frame.GetType().GetField("Root").GetValue(frame),
            "the degraded self-resolve keeps Get's order including the decoy swap — and says so out loud");
        Assert.AreEqual("MA.Get(Component)", frame.GetType().GetField("UnreflectedAnchor").GetValue(frame),
            "the degraded frame carries an in-band caveat, not just a console line");
    }

    // The one anchor that can ride a frame carrying OUR controller must surface on CheckAnimator's own
    // door: rules present + RewritePath unreachable ⇒ a Notes line, so the (possibly inflated)
    // brokenBinding count is never presented uncaveated.
    [Test]
    public void Lint_rewriteRulesWithUnreachableRewritePath_notesTheInflationRisk()
    {
        var a = NewAvatar("LintVrcfRwNote");
        var prop = NewChild(a, "Prop");
        var ctrl = NewController("RwNoteCtrl", NewClip(TmpDir, "RwNoteClip", "Relocated/X"));
        var c = AddVrcfFullController(prop, ctrl, prop);
        SetVrcfRewriteBindings(c, ("Relocated", "", false));

        SetSeam("ResolveVrcfRewritePath", (Func<MethodInfo>)(() => null)); // force the pin unreachable

        var r = CheckAnimator.Run(ctrl, "auto", mergeSite: "LintVrcfRwNote/Prop");
        var log = ReadLog(r);
        StringAssert.Contains("could not be applied", log,
            "the un-applied rewrite rules must be named beside the count they may inflate: " + log);
    }

    // A1 (C3): rewrite rules present but VRCFury's RewritePath unreachable ⇒ the frame carries the
    // VRCF.RewritePath anchor (R-H rail) and NO rewriter — never a silent identity that would fabricate
    // plausible-but-false binding results. Fails against the pre-A1 replication, which rewrote regardless.
    [Test]
    public void TryVrcfFrame_rewriteRulesWithUnreachableRewritePath_anchorsTheFrame()
    {
        var a = NewAvatar("LintVrcfRwDrift");
        var prop = NewChild(a, "Prop");
        var c = AddVrcfFullController(prop, NewController("RwDriftCtrl", NewClip(TmpDir, "RwDriftClip", "X")), prop);
        SetVrcfRewriteBindings(c, ("From", "To", false));

        SetSeam("ResolveVrcfRewritePath", (Func<MethodInfo>)(() => null)); // force the pin unreachable

        var args = new object[] { c, null, null };
        bool ok = (bool)typeof(CheckAnimator).GetMethod("TryVrcfFrame", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, args);
        Assert.IsTrue(ok, "a present FullController is a frame");
        var frame = args[2];
        Assert.AreEqual("VRCF.RewritePath", frame.GetType().GetField("UnreflectedAnchor").GetValue(frame),
            "rules-present + RewritePath unreachable must anchor the frame, not silently skip the rewrite");
        Assert.IsNull(frame.GetType().GetField("PathRewrite").GetValue(frame),
            "no rewriter is handed out when the vendor method is unreachable — the anchor says why");
    }

    // Rider 1 (R-K symmetry): post-W9 the generic scan emits an MA-scene-ref offender for the
    // targetObject-only shape, but MaFrameUncertaintyNote returned null for it — an offender with no frame
    // line beside it, which reads as a dropped ref rather than a relocated frame. Both must be present.
    [Test]
    public void Inspect_emptyPathLiveTarget_notesTheFrameBesideTheOffender()
    {
        var a = NewAvatar("LintMaFrameNote");
        var outfit = NewChild(a, "Outfit");
        var decoy = NewChild(a, "Decoy");
        AddMaMergeAnimator(outfit, NewController("MaNoteCtrl", NewClip(TmpDir, "MaNoteClip", "Bone")), "", decoy);

        var r = Inspect("LintMaFrameNote");
        var log = ReadLog(r);

        StringAssert.Contains("maSceneRef=1", r, "W9's scan still flags the targetObject-only ref: " + r);
        StringAssert.Contains("frame-certain", log,
            "the offender must carry its frame caption — the frame is the own GameObject, the REF is what is broken: " + log);
    }

    // The other side of rider 1, and the one that keeps the caption honest: a WHOLLY empty relativePathRoot
    // (both halves) is the intentional-empty ScanSceneRefs exempts, so there is no offender — and a caption
    // with no offender beside it is the same asymmetry rider 1 fixes, pointing the other way. The two
    // predicates are coupled across the two methods; move either and this goes red.
    [Test]
    public void Inspect_whollyEmptyRelativePathRoot_getsNeitherOffenderNorCaption()
    {
        var a = NewAvatar("LintMaEmptyBoth");
        var outfit = NewChild(a, "Outfit");
        AddMaMergeAnimator(outfit, NewController("MaEmptyBothCtrl", NewClip(TmpDir, "MaEmptyBothClip", "Bone")));

        var r = Inspect("LintMaEmptyBoth");
        var log = ReadLog(r);

        StringAssert.Contains("maSceneRef=0", r, "both halves empty is the intentional-empty exemption: " + r);
        StringAssert.DoesNotContain("frame-certain", log, "no offender to caption ⇒ no caption: " + log);
    }

    // Rider 2: the degraded self-resolve (MA API drift) accepted ANY live targetObject, skipping the
    // IsChildOf gate both real overloads apply — a false negative on the one path that exists to survive drift.
    [Test]
    public void SelfResolve_targetObjectOutsideAvatar_isNotResolved()
    {
        var a = NewAvatar("LintSelfResolveOutside");
        var outfit = NewChild(a, "Outfit");
        var outsider = new GameObject("OutsideTheAvatar");
        try
        {
            AddMaObjectToggle(outfit, "Stale_wrong_path", outsider);

            SetSeam("ResolveAorGetOverload", (Func<Type, MethodInfo>)(_ => null)); // force the degraded self-resolve
            var r = Inspect("LintSelfResolveOutside");
            ReadLog(r);

            StringAssert.Contains("maSceneRef=1", r,
                "an out-of-avatar targetObject resolves nowhere at bake — the degraded path must flag it, not accept it: " + r);
        }
        finally { UnityEngine.Object.DestroyImmediate(outsider); }
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

    // ── Real dynamics reflection: type/getter canary + null-root extraction ───────────────────────────

    [Test] public void Canary_DynamicsTypesAndGettersResolve()
    {
        foreach (var c in CheckAvatar.DynamicsCategories)
            AssertTypeGetter(c.typeName, c.getter);
        // pin the collider fields ColliderDetail and the same-shape read use on the real collider type (a rename
        // must go red, not silently blank the detail or leave every collider group unsplit)
        var col = VendorReflect.FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider");
        Assert.IsNotNull(col, "collider type unresolved (drift)");
        foreach (var f in new[] { "shapeType", "radius", "height", "insideBounds", "position", "rotation", "rootTransform" })
            Assert.IsNotNull(col.GetField(f), "collider field unresolved (drift): " + f);
    }

    private static void AssertTypeGetter(string typeName, string getter)
    {
        var t = VendorReflect.FindType(typeName);
        Assert.IsNotNull(t, "type unresolved (drift): " + typeName);
        Assert.IsNotNull(t.GetMethod(getter, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null),
            "getter unresolved (drift): " + typeName + "." + getter);
    }

    [Test] public void CollectDynamics_RealPhysbone_NullRoot_UsesOwnTransform()
    {
        var root = NewAvatar("PB");
        var pbType = VendorReflect.FindType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone");
        Assert.IsNotNull(pbType);
        var child = NewChild(root, "Bone");
        child.AddComponent(pbType); // rootTransform defaults null
        var targets = CheckAvatar.CollectDynamicsTargets(root); // real default
        Assert.IsTrue(targets.Exists(x => x.category == "physbone" && x.target == child.transform),
            "physbone with null rootTransform should target its own transform");
    }

    // ── anchor-seam ─────────────────────────────────────────────────────────────────────────────────
    //
    // WHAT THESE PROVE, AND WHAT THEY DO NOT. Every test below exercises the WALK — the carrier and
    // relocator sets, the mount boundary, the declared-parameter match, the degraded rail. None of them
    // proves the underlying claim that a parameter-carrying component an MA relocator moves out of its
    // FullController keeps the bare name at bake: a fixture asserting "the predicate fires on a shape I
    // believe breaks" passes whether or not the belief is true. The oracle for the claim is external: the
    // pre-fix `selective-animation` entry (vrc-patterns @ a62924f^), whose two VRCRaycasts under the
    // BoneProxy'd `Aim` bake still writing `SelectiveAnimation/Ray` and `SelectiveAnimation/Wall` while the
    // built FX reads only the prefixed names, and whose 28 clip bindings through that same `Aim` all
    // survive — which is why a clip binding is no longer this class's. Treat a green run here as "the walk
    // still walks", never as "the break is real".

    private Component AddMaRelocator(GameObject go, string shortName)
    {
        var t = Resolve("nadena.dev.modular_avatar.core.ModularAvatar" + shortName);
        Assert.IsNotNull(t, "MA " + shortName + " type must resolve");
        return go.AddComponent(t);
    }

    // VRCFury ArmatureLink on a node: the sanctioned anchor, which must never be an offender.
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

    // A parameter-carrying VRC component (receiver / physbone / raycast) writing `parameter`.
    private Component AddCarrier(GameObject go, string fullName, string parameter)
    {
        var t = Resolve(fullName);
        Assert.IsNotNull(t, fullName + " must resolve");
        var c = go.AddComponent(t);
        var so = new SerializedObject(c);
        so.FindProperty("parameter").stringValue = parameter;
        so.ApplyModifiedPropertiesWithoutUndo();
        return c;
    }

    private const string Receiver = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";

    // A controller declaring the given float parameters and animating nothing.
    private AnimatorController NewParamController(string name, params string[] parameters)
    {
        var c = AnimatorController.CreateAnimatorControllerAtPath(TmpDir + "/" + name + ".controller");
        foreach (var p in parameters) c.AddParameter(p, AnimatorControllerParameterType.Float);
        return c;
    }

    // Point the FullController's first params entry at a saved asset declaring `parameters`.
    private void SetVrcfParams(Component vrcf, string assetName, params string[] parameters)
    {
        var ep = ScriptableObject.CreateInstance<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters>();
        ep.parameters = parameters.Select(n => new VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.Parameter
        {
            name = n, valueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType.Float, networkSynced = true,
        }).ToArray();
        AssetDatabase.CreateAsset(ep, TmpDir + "/" + assetName + ".asset");
        var so = new SerializedObject(vrcf);
        var prms = so.FindProperty("content").FindPropertyRelative("prms");
        prms.arraySize = 1;
        prms.GetArrayElementAtIndex(0).FindPropertyRelative("parameters").FindPropertyRelative("objRef").objectReferenceValue = ep;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // The rig every test below varies: avatar → Prop (the FullController mount) → Aim → Sensor, plus
    // Payload as a genuine sibling of Aim. It is the pre-fix selective-animation shape, reduced.
    private GameObject NewSeamRig(string name, out GameObject aim, out GameObject sensor, out GameObject payload)
    {
        var a = NewAvatar(name);
        var prop = NewChild(a, "Prop");
        aim = NewChild(prop, "Aim");
        sensor = NewChild(aim, "Sensor");
        payload = NewChild(prop, "Payload");
        return prop;
    }

    // The RunLog body for a fresh Inspect of the fixture avatar.
    private string SeamLog() => ReadLog(Inspect(_avatar.name));

    private static int SeamCount(string log)
    {
        var m = Regex.Match(log, @"anchorSeam=(\d+)");
        Assert.IsTrue(m.Success, "summary must carry an anchorSeam count:\n" + log);
        return int.Parse(m.Groups[1].Value);
    }

    [Test]
    public void AnchorSeam_DeclaredReceiverUnderRelocator_Fires()
    {
        var prop = NewSeamRig("AS1", out var aim, out var sensor, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(sensor, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl1", "Touch"), null);
        var log = SeamLog();
        Assert.AreEqual(1, SeamCount(log), log);
        StringAssert.Contains("parameter=`Touch` moved-by=ModularAvatarBoneProxy @ `AS1/Prop/Aim`", log);
        StringAssert.Contains(CheckAvatar.AnchorSeamNoteLine, log);
    }

    // The sanctioned idiom: the proxied node is a SIBLING of the sensor, so nothing it carries leaves the
    // subtree. The scope note still rides the clean run, the case most easily misread as confirmation.
    [Test]
    public void AnchorSeam_CarrierBesideTheRelocator_CleanWithScopeNote()
    {
        var prop = NewSeamRig("AS2", out _, out _, out var payload);
        AddMaRelocator(NewChild(prop, "StowAnchor"), "BoneProxy");
        AddCarrier(payload, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl2", "Touch"), null);
        var log = SeamLog();
        Assert.AreEqual(0, SeamCount(log), log);
        StringAssert.Contains(CheckAvatar.AnchorSeamScopeLine, log);
    }

    // The builder renames only what the FullController declares, so an undeclared parameter bridges to
    // whatever else declares it and is not this break. AS1 is the same rig with the name declared.
    [Test]
    public void AnchorSeam_UndeclaredParameter_Clean()
    {
        var prop = NewSeamRig("AS3", out var aim, out var sensor, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(sensor, Receiver, "Elsewhere");
        AddVrcfFullController(prop, NewParamController("AsCtrl3", "Touch"), null);
        Assert.AreEqual(0, SeamCount(SeamLog()));
    }

    // A physbone or raycast is renamed on the parameters its runtime writes, never on its bare name.
    [TestCase("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone", "Grab_IsGrabbed")]
    [TestCase("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone", "Grab_Angle")]
    [TestCase("VRC.SDK3.Avatars.Components.VRCRaycast", "Grab_Hit")]
    [TestCase("VRC.SDK3.Avatars.Components.VRCRaycast", "Grab_Ratio")]
    public void AnchorSeam_SuffixedCarrier_FiresOnTheWrittenName(string type, string declared)
    {
        var prop = NewSeamRig("AS4" + declared + type.Length, out var aim, out var sensor, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(sensor, type, "Grab");
        AddVrcfFullController(prop, NewParamController("AsCtrl4" + declared + type.Length, declared), null);
        var log = SeamLog();
        Assert.AreEqual(1, SeamCount(log), log);
        StringAssert.Contains("parameter=`Grab`", log);
    }

    [Test]
    public void AnchorSeam_PhysBoneOnItsBareName_Clean()
    {
        var prop = NewSeamRig("AS5", out var aim, out var sensor, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(sensor, "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone", "Grab");
        AddVrcfFullController(prop, NewParamController("AsCtrl5", "Grab"), null);
        Assert.AreEqual(0, SeamCount(SeamLog()));
    }

    // The builder renames params-asset names too, and a FullController carrying only a params asset mounts
    // no controller — a walk keyed on merged controllers would miss it entirely.
    [Test]
    public void AnchorSeam_ParamsAssetOnlyFullController_Fires()
    {
        var prop = NewSeamRig("AS6", out var aim, out var sensor, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(sensor, Receiver, "Touch");
        var vrcf = AddVrcfFullControllerNoControllers(prop, null);
        SetVrcfParams(vrcf, "AsParams6", "Touch");
        Assert.AreEqual(1, SeamCount(SeamLog()));
    }

    // Inclusive at the carrier end, exclusive at the mount: a relocator on the sensor itself moves it
    // out; one on the mount moves the whole subtree, FullController included, so the builder still finds it.
    [Test]
    public void AnchorSeam_RelocatorOnTheCarrier_Fires()
    {
        var prop = NewSeamRig("AS7", out _, out var sensor, out _);
        AddMaRelocator(sensor, "BoneProxy");
        AddCarrier(sensor, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl7", "Touch"), null);
        Assert.AreEqual(1, SeamCount(SeamLog()));
    }

    [Test]
    public void AnchorSeam_RelocatorOnTheMount_Clean()
    {
        var prop = NewSeamRig("AS8", out _, out var sensor, out _);
        AddMaRelocator(prop, "BoneProxy");
        AddCarrier(sensor, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl8", "Touch"), null);
        var log = SeamLog();
        Assert.AreEqual(0, SeamCount(log), log);
        StringAssert.Contains(CheckAvatar.AnchorSeamScopeLine, log); // the walk saw the relocator
    }

    // The mount is rootObjOverride when set — the builder walks that object, not the component's own.
    [Test]
    public void AnchorSeam_RootObjOverride_IsTheMount()
    {
        var a = NewAvatar("AS9");
        var host = NewChild(a, "Host");
        var mount = NewChild(a, "Mount");
        var aim = NewChild(mount, "Aim");
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(NewChild(aim, "Sensor"), Receiver, "Touch");
        AddVrcfFullController(host, NewParamController("AsCtrl9", "Touch"), mount);
        var log = SeamLog();
        Assert.AreEqual(1, SeamCount(log), log);
        StringAssert.Contains("[VRCFury FullController @ AS9/Host]", log);
    }

    [TestCase("BoneProxy")]
    [TestCase("MergeArmature")]
    [TestCase("WorldFixedObject")]
    [TestCase("ReplaceObject")]
    public void AnchorSeam_EveryTrackedRelocatorType_Fires(string shortName)
    {
        var prop = NewSeamRig("AS10" + shortName, out var aim, out var sensor, out _);
        AddMaRelocator(aim, shortName);
        AddCarrier(sensor, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl10" + shortName, "Touch"), null);
        var log = SeamLog();
        Assert.AreEqual(1, SeamCount(log), log);
        StringAssert.Contains("moved-by=ModularAvatar" + shortName, log);
    }

    // VisibleHeadAccessory reparents only the clones it creates, never its own node, so the sensor stays
    // under the mount and the builder renames it.
    [Test]
    public void AnchorSeam_VisibleHeadAccessory_NotARelocator()
    {
        var prop = NewSeamRig("AS15", out var aim, out var sensor, out _);
        AddMaRelocator(aim, "VisibleHeadAccessory");
        AddCarrier(sensor, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl15", "Touch"), null);
        Assert.AreEqual(0, SeamCount(SeamLog()));
    }

    // A FullController mounted on the avatar root still contains every MA destination, so the builder's
    // walk finds the moved carrier. The same rig with the mount on Prop is AS1.
    [Test]
    public void AnchorSeam_MountOnTheAvatarRoot_Clean()
    {
        var a = NewAvatar("AS16");
        var aim = NewChild(NewChild(a, "Prop"), "Aim");
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(NewChild(aim, "Sensor"), Receiver, "Touch");
        AddVrcfFullController(a, NewParamController("AsCtrl16", "Touch"), null);
        var log = SeamLog();
        Assert.AreEqual(0, SeamCount(log), log);
        StringAssert.Contains(CheckAvatar.AnchorSeamScopeLine, log); // the walk saw the relocator
    }

    // ArmatureLink runs after the FullController has renamed, so it is never the anchor. The control
    // DISCRIMINATES: the link sits nearer the sensor than the tracked BoneProxy, and the walk names only
    // the nearest anchor, so the reported anchor is `Outer` iff the link was skipped.
    [Test]
    public void AnchorSeam_VrcfArmatureLink_NotTheAnchor()
    {
        var a = NewAvatar("AS11");
        var prop = NewChild(a, "Prop");
        var outer = NewChild(prop, "Outer");
        var aim = NewChild(outer, "Aim");
        var sensor = NewChild(aim, "Sensor");
        var link = AddVrcfArmatureLink(aim, aim);
        AddMaRelocator(outer, "BoneProxy");
        AddCarrier(sensor, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl11", "Touch"), null);
        var content = new SerializedObject(link).FindProperty("content");
        Assert.IsNotNull(content.managedReferenceValue, "ArmatureLink content must be live on the fixture");
        var log = SeamLog();
        Assert.AreEqual(1, SeamCount(log), log);
        StringAssert.Contains("moved-by=ModularAvatarBoneProxy @ `AS11/Prop/Outer`", log);
    }

    // The builder is VRCFury's; an MA MergeAnimator declaring the same name renames nothing.
    [Test]
    public void AnchorSeam_MaMergeAnimator_NotFlagged()
    {
        var prop = NewSeamRig("AS12", out var aim, out var sensor, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(sensor, Receiver, "Touch");
        AddMaMergeAnimator(prop, NewParamController("AsCtrl12", "Touch"));
        Assert.AreEqual(0, SeamCount(SeamLog()));
    }

    // The retired shape: a FullController clip binding through a relocated node survives the build, so it
    // must not come back as an offender. Fires if a clip walk is ever reinstated here.
    [Test]
    public void AnchorSeam_ClipBindingThroughRelocator_NotFlagged()
    {
        var prop = NewSeamRig("AS13", out var aim, out _, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddVrcfFullController(prop, NewController("AsCtrl13", NewClip(TmpDir, "AsClip13", "Aim/Sensor")), null);
        var log = SeamLog();
        Assert.AreEqual(0, SeamCount(log), log);
        StringAssert.Contains("clipBinding=0", log); // it resolves — so 0 is the class, not a miss
    }

    // The gate door: no descriptor required, and a null root DEGRADES rather than reporting clean.
    [Test]
    public void ScanAnchorSeams_BareModuleWithoutDescriptor_Reports()
    {
        var prop = NewSeamRig("AS14", out var aim, out var sensor, out _);
        AddMaRelocator(aim, "BoneProxy");
        AddCarrier(sensor, Receiver, "Touch");
        AddVrcfFullController(prop, NewParamController("AsCtrl14", "Touch"), null);
        var lines = CheckAvatar.ScanAnchorSeams(prop); // the MOUNT, not an avatar root
        var offenders = lines.FindAll(l => !l.StartsWith(CheckAvatar.ScopePrefix) && !l.StartsWith(CheckAvatar.DegradedPrefix));
        Assert.AreEqual(1, offenders.Count, string.Join("\n", lines));
        StringAssert.Contains("VRCContactReceiver", offenders[0]);
        StringAssert.Contains("ModularAvatarBoneProxy", offenders[0]);
        Assert.IsTrue(lines.Exists(l => l.StartsWith(CheckAvatar.ScopePrefix)), string.Join("\n", lines));
    }

    [Test]
    public void ScanAnchorSeams_NullRoot_Degrades()
    {
        var lines = CheckAvatar.ScanAnchorSeams(null);
        Assert.AreEqual(1, lines.Count);
        StringAssert.StartsWith(CheckAvatar.DegradedPrefix, lines[0]);
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
        var path = CheckAvatarFixture.LogPath(CheckAvatar.Run(_root.name));
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

    // ── Same-shape colliders ──────────────────────────────────────────────────────────────────────────
    // Real VRCPhysBoneCollider components, so the shape is read the way production reads it. Each collider
    // sits on its own child of a bone (host ≠ root, as vendors ship them) and roots on that bone; the merge
    // pair folds the merge bone onto the base bone. Bones are coincident unless a test moves one.

    private static VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider Capsule(
        Transform bone, string name, float radius, float height, Vector3 position = default, Quaternion? rotation = null)
    {
        var host = NewChild(bone.gameObject, name);
        var c = host.AddComponent<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider>();
        c.shapeType = VRC.Dynamics.VRCPhysBoneColliderBase.ShapeType.Capsule;
        c.rootTransform = bone;
        c.radius = radius;
        c.height = height;
        c.position = position;
        c.rotation = rotation ?? Quaternion.identity;
        return c;
    }

    // An avatar with a base bone and a merge bone the injected merge pair folds onto it.
    private (GameObject root, Transform baseBone, Transform mergeBone) ColliderScene(string name)
    {
        var root = NewAvatar(name);
        var baseBone = NewChild(root, "BaseBone").transform;
        var mergeBone = NewChild(root, "MergeBone").transform;
        CheckAvatar.ResolveMergePairs = Pairs(new List<(Transform, Transform)> { (mergeBone, baseBone) });
        return (root, baseBone, mergeBone);
    }

    private static Func<GameObject, List<(Component, Transform, string, string)>> Colliders(
        params VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider[] cols)
        => _ => cols.Select(c => ((Component)c, c.rootTransform, "collider", "")).ToList();

    // The case that must never go quiet: one shape stacked twice on a bone.
    [Test]
    public void MergeConflict_IdenticalColliders_AreFlagged()
    {
        var (root, b, m) = ColliderScene("MCSame");
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f, new Vector3(0f, 0.065f, 0.01f));
        var mergeCol = Capsule(m, "MergeCol", 0.07f, 0.25f, new Vector3(0f, 0.065f, 0.01f));
        CheckAvatar.CollectDynamicsTargets = Colliders(baseCol, mergeCol);
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains("category=`collider`", log);
        StringAssert.Contains(root.name + "/BaseBone/BaseCol", log);
        StringAssert.Contains(root.name + "/MergeBone/MergeCol", log);
    }

    // Distinct shapes sharing a bone are an authored composite, not a conflict (Shinano_Belliluna's legs).
    [Test]
    public void MergeConflict_DistinctColliderShapes_AreQuiet()
    {
        var (root, b, m) = ColliderScene("MCDistinct");
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f);
        var mergeCol = Capsule(m, "MergeCol", 0.08f, 0.25f);   // 10 mm wider
        CheckAvatar.CollectDynamicsTargets = Colliders(baseCol, mergeCol);
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=0", log);
        StringAssert.Contains("=> PASS", log);
    }

    // The tolerance edge, from both sides, on placement alone.
    [Test]
    public void MergeConflict_ColliderOffsetWithinTolerance_IsFlagged()
    {
        var (_, b, m) = ColliderScene("MCNear");
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f);
        var mergeCol = Capsule(m, "MergeCol", 0.07f, 0.25f, new Vector3(0.001f, 0f, 0f));
        CheckAvatar.CollectDynamicsTargets = Colliders(baseCol, mergeCol);
        StringAssert.Contains("mergeConflict=1", InspectLog());
    }

    [Test]
    public void MergeConflict_ColliderOffsetPastTolerance_IsQuiet()
    {
        var (_, b, m) = ColliderScene("MCFar");
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f);
        var mergeCol = Capsule(m, "MergeCol", 0.07f, 0.25f, new Vector3(0.003f, 0f, 0f));
        CheckAvatar.CollectDynamicsTargets = Colliders(baseCol, mergeCol);
        StringAssert.Contains("mergeConflict=0", InspectLog());
    }

    // Placement is compared in world space from each collider's own root: identical local fields on a
    // rotated merge bone are a different capsule.
    [Test]
    public void MergeConflict_SameLocalFieldsOnRotatedRoot_IsQuiet()
    {
        var (_, b, m) = ColliderScene("MCRotRoot");
        m.localRotation = Quaternion.Euler(0f, 0f, 30f);
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f);
        var mergeCol = Capsule(m, "MergeCol", 0.07f, 0.25f);
        CheckAvatar.CollectDynamicsTargets = Colliders(baseCol, mergeCol);
        StringAssert.Contains("mergeConflict=0", InspectLog());
    }

    // A capsule is symmetric end for end, so a flipped axis is the same shape.
    [Test]
    public void MergeConflict_FlippedCapsuleAxis_IsFlagged()
    {
        var (_, b, m) = ColliderScene("MCFlip");
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f);
        var mergeCol = Capsule(m, "MergeCol", 0.07f, 0.25f, default, Quaternion.Euler(180f, 0f, 0f));
        CheckAvatar.CollectDynamicsTargets = Colliders(baseCol, mergeCol);
        StringAssert.Contains("mergeConflict=1", InspectLog());
    }

    // In a mixed group only the matching pair is an offender; the distinct member is not named.
    [Test]
    public void MergeConflict_MixedColliderGroup_FlagsOnlyTheMatchingPair()
    {
        var (root, b, m) = ColliderScene("MCMixed");
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f);
        var mergeCol = Capsule(m, "MergeCol", 0.07f, 0.25f);
        var mergeFront = Capsule(m, "MergeFront", 0.07f, 0.30f, new Vector3(0f, 0.12f, 0f));
        CheckAvatar.CollectDynamicsTargets = Colliders(baseCol, mergeCol, mergeFront);
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains(root.name + "/MergeBone/MergeCol", log);
        StringAssert.DoesNotContain("MergeFront", log);
    }

    // The mergeable test applies per cluster, not per group: two identical base colliders beside a distinct
    // mergeable one are a base↔base duplicate and stay dropped.
    [Test]
    public void MergeConflict_IdenticalBasePairBesideDistinctMergeable_IsDropped()
    {
        var (_, b, m) = ColliderScene("MCBasePair");
        var baseA = Capsule(b, "BaseA", 0.07f, 0.25f);
        var baseB = Capsule(b, "BaseB", 0.07f, 0.25f);
        var mergeCol = Capsule(m, "MergeCol", 0.09f, 0.25f);
        CheckAvatar.CollectDynamicsTargets = Colliders(baseA, baseB, mergeCol);
        StringAssert.Contains("mergeConflict=0", InspectLog());
    }

    // A shape that cannot be read fails toward flagging: a host with no collider fields keeps the group whole.
    [Test]
    public void MergeConflict_UnreadableColliderShape_KeepsGroupFlagged()
    {
        var (root, b, m) = ColliderScene("MCUnread");
        var baseCol = Capsule(b, "BaseCol", 0.07f, 0.25f);
        var bare = NewChild(m.gameObject, "Bare").transform;
        CheckAvatar.CollectDynamicsTargets = _ => new List<(Component, Transform, string, string)> {
            (baseCol, b, "collider", ""), (bare, m, "collider", "") };
        var log = InspectLog();
        StringAssert.Contains("mergeConflict=1", log);
        StringAssert.Contains(root.name + "/MergeBone/Bare", log);
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
