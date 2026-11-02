using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// CheckAnimator's end-to-end contract: summary tokens and verdict through Run.
//
// Run resolves scene handles through SceneHandle.Resolve, whose path and name rungs search the ACTIVE scene,
// which no preview scene can stand in for — so the fixtures live in the active scene and are torn down in place.
public class CheckAnimatorTests
{
    private const string TmpDir = "Assets/AgentCheckAnimatorTmp";
    private const string AssetPath = TmpDir + "/CheckAnimatorTest.controller";
    private const string AvatarName = "CheckAnimatorAvatar";

    private GameObject _avatar;
    private string _logPath;

    [TearDown]
    public void TearDown()
    {
        if (_avatar != null) UnityEngine.Object.DestroyImmediate(_avatar);
        _avatar = null;
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        LogAssert.ignoreFailingMessages = false;
    }

    [Test]
    public void ExplicitBasis_oneResolvingOneBroken_reportsBrokenBinding1_andFails()
    {
        _avatar = new GameObject(AvatarName);
        var child = new GameObject("Resolvable");
        child.transform.SetParent(_avatar.transform);

        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentCheckAnimatorTmp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);

        // SetEditorCurve (not clip.SetCurve) so each path yields EXACTLY one binding — SetCurve on a
        // vector sub-property auto-expands to x/y/z, which would multiply the broken count.
        var clip = new AnimationClip { name = "TestClip" };
        var curve = AnimationCurve.Linear(0, 0, 1, 1);
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Resolvable", typeof(Transform), "m_LocalScale.x"), curve);
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Ghost", typeof(Transform), "m_LocalScale.x"), curve);
        AssetDatabase.AddObjectToAsset(clip, controller);

        var sm = controller.layers[0].stateMachine;
        var state = sm.AddState("S");
        state.motion = clip;
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        // Emit logs the FAIL verdict via Debug.LogError; that is expected output, not a test failure.
        LogAssert.ignoreFailingMessages = true;

        var result = CheckAnimator.Run(controller, "explicit", null, AvatarName, AvatarName);
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("brokenBinding=1", result,
            "exactly one unresolved binding (Ghost) must survive; Resolvable must resolve under the basis root: " + result);
        StringAssert.Contains("=> FAIL", result,
            "explicit basis keeps broken-binding at error-tier, so one broken binding is a FAIL: " + result);
        StringAssert.Contains("missingMotion=0 undeclaredParam=0 nonFloatBlendParam=0 nonFloatParamCurve=0 driverOnAnimatedParam=0 entryShadow=0", result,
            "no other rule should fire: " + result);
    }

    [Test]
    public void ExplicitBasis_nonFloatBlendParam_showsCountInSummary_andFails()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentCheckAnimatorTmp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);
        controller.AddParameter("IntSpeed", AnimatorControllerParameterType.Int);

        // A blend tree keyed on a declared Int reads 0 and cannot respond.
        var tree = new BlendTree { name = "BadTree", blendType = BlendTreeType.Simple1D, blendParameter = "IntSpeed" };
        AssetDatabase.AddObjectToAsset(tree, controller);
        controller.layers[0].stateMachine.AddState("S").motion = tree;
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        LogAssert.ignoreFailingMessages = true;
        // Null roots skip the broken-binding rule (no basis root), isolating this rule.
        var result = CheckAnimator.Run(controller, "explicit", null, null, null);
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("nonFloatBlendParam=1", result,
            "the firing rule's count must be visible in the summary, not only in the offender body: " + result);
        StringAssert.Contains("=> FAIL", result,
            "a non-float blend parameter is an error-tier defect: " + result);
    }

    // CheckAnimator's door is the OTHER half of the error-tier wiring: its verdict and its summary format string
    // each enumerate the counters by name, so a rule can be correct inside ControllerRules and still report PASS
    // here. CompileController's door has its own guard per rule; this is CheckAnimator's.
    [Test]
    public void ExplicitBasis_nonFloatParamCurve_showsCountInSummary_andFails()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentCheckAnimatorTmp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);
        controller.AddParameter("Flag", AnimatorControllerParameterType.Bool);

        // A parameter curve on the declared Bool: emitted, inert, and it binds the parameter.
        var clip = new AnimationClip { name = "WritesFlag" };
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("", typeof(Animator), "Flag"),
            AnimationCurve.Constant(0f, 1f / 60f, 1f));
        AssetDatabase.AddObjectToAsset(clip, controller);
        controller.layers[0].stateMachine.AddState("S").motion = clip;
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        LogAssert.ignoreFailingMessages = true;
        var result = CheckAnimator.Run(controller, "explicit", null, null, null);
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("nonFloatParamCurve=1", result,
            "the firing rule's count must reach the summary, not only the offender body: " + result);
        StringAssert.Contains("=> FAIL", result,
            "a parameter curve on a Bool is an error-tier defect at the CheckAnimator door too: " + result);
    }

    [Test]
    public void ExplicitBasis_driverOnAnimatedParam_showsCountInSummary_andFails()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentCheckAnimatorTmp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);
        controller.AddParameter("Held", AnimatorControllerParameterType.Float);

        var clip = new AnimationClip { name = "HoldsHeld" };
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("", typeof(Animator), "Held"),
            AnimationCurve.Constant(0f, 1f / 60f, 1f));
        AssetDatabase.AddObjectToAsset(clip, controller);
        var sm = controller.layers[0].stateMachine;
        sm.AddState("Holds").motion = clip;

        var drvState = sm.AddState("Drives");
        var drv = drvState.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
        drv.parameters = new System.Collections.Generic.List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
        {
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
            {
                type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set, name = "Held", value = 1f
            }
        };
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        LogAssert.ignoreFailingMessages = true;
        var result = CheckAnimator.Run(controller, "explicit", null, null, null);
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("driverOnAnimatedParam=1", result,
            "the firing rule's count must reach the summary, not only the offender body: " + result);
        StringAssert.Contains("=> FAIL", result,
            "a driver op on a clip-bound parameter is an error-tier defect at the CheckAnimator door too: " + result);
    }

    [Test]
    public void AutoBasis_vrcfRewriteBindings_countIsTruthful()
    {
        _avatar = new GameObject(AvatarName);
        var prop = new GameObject("Prop"); prop.transform.SetParent(_avatar.transform);
        var nested = new GameObject("Nested"); nested.transform.SetParent(prop.transform);
        var armature = new GameObject("Armature"); armature.transform.SetParent(nested.transform);
        var bone = new GameObject("Bone"); bone.transform.SetParent(armature.transform);

        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentCheckAnimatorTmp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);
        var clip = new AnimationClip { name = "RwClip" };
        var curve = AnimationCurve.Linear(0, 0, 1, 1);
        // "Armature/Bone" is base-rooted; the rewrite relocates it to "Nested/Armature/Bone" (resolves at the
        // mount). "Ghost/Missing" is a genuine break the rewrite does not touch.
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Armature/Bone", typeof(Transform), "m_LocalScale.x"), curve);
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Ghost/Missing", typeof(Transform), "m_LocalScale.x"), curve);
        AssetDatabase.AddObjectToAsset(clip, controller);
        controller.layers[0].stateMachine.AddState("S").motion = clip;
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        AddVrcfFullControllerWithRewrite(prop, controller, ("Armature", "Nested/Armature", false));

        LogAssert.ignoreFailingMessages = true;
        var result = CheckAnimator.Run(controller, "auto", AvatarName + "/Prop");
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("brokenBinding=1", result,
            "rewriteBindings must resolve Armature/Bone; only Ghost/Missing may remain — a truthful count: " + result);
        StringAssert.Contains("=> PASS", result, "auto-basis still demotes broken-binding to advisory (PASS): " + result);
    }

    // OrphanTestSmb exercises the SMB arm of the five-type filter. HideInHierarchy matches a controller's own
    // sub-objects, for which AssetDatabase.IsSubAsset returns false, so a reintroduced IsSubAsset gate fails here.
    [Test]
    public void OrphanSubAsset_hiddenSmb_isReported()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentCheckAnimatorTmp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);
        controller.layers[0].stateMachine.AddState("A");

        var orphan = ScriptableObject.CreateInstance<OrphanTestSmb>();
        orphan.name = "ORPHAN_SMB";
        AssetDatabase.AddObjectToAsset(orphan, controller);
        orphan.hideFlags = HideFlags.HideInHierarchy;
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(AssetPath);

        // An orphan is advisory-tier, so the verdict is a PASS logged without an error and needs no LogAssert.
        var result = CheckAnimator.Run(controller, "explicit", null, null, null);
        _logPath = ExtractLogPath(result);

        Assert.Contains("OrphanTestSmb 'ORPHAN_SMB'", OrphanTokens(result),
            "CheckAnimator must name the hidden orphan sub-asset (regression: a reintroduced IsSubAsset gate hides it): " + result);
    }

    // Parse the emitted RunLog markdown for orphanSubAsset "Type 'name'" tokens (advisory line shape:
    // "- **orphanSubAsset** <Where> — <Detail>").
    private static List<string> OrphanTokens(string result)
    {
        var tokens = new List<string>();
        string rel = ExtractLogPath(result);
        if (string.IsNullOrEmpty(rel)) return tokens;
        string proj = Application.dataPath;
        proj = proj.Substring(0, proj.Length - "Assets".Length);
        string abs = proj + rel;
        if (!File.Exists(abs)) return tokens;
        const string mark = "**orphanSubAsset**";
        foreach (var line in File.ReadAllText(abs).Split('\n'))
        {
            int oi = line.IndexOf(mark, StringComparison.Ordinal);
            if (oi < 0) continue;
            string after = line.Substring(oi + mark.Length);
            int dash = after.IndexOf(" — ", StringComparison.Ordinal);
            if (dash >= 0) after = after.Substring(0, dash);
            tokens.Add(after.Trim());
        }
        return tokens;
    }

    private static string ExtractLogPath(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, System.StringComparison.Ordinal);
        return i < 0 ? null : result.Substring(i + marker.Length).Trim();
    }

    private static Type Resolve(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void AddVrcfFullControllerWithRewrite(GameObject go, AnimatorController ctrl, params (string from, string to, bool delete)[] rules)
    {
        var vt = Resolve("VF.Model.VRCFury");
        var ft = Resolve("VF.Model.Feature.FullController");
        Assert.IsNotNull(vt, "VF.Model.VRCFury must resolve"); Assert.IsNotNull(ft, "VF.Model.Feature.FullController must resolve");
        var c = go.AddComponent(vt);
        var so = new SerializedObject(c);
        so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(ft);
        so.ApplyModifiedPropertiesWithoutUndo();

        so = new SerializedObject(c);
        var content = so.FindProperty("content");
        var controllers = content.FindPropertyRelative("controllers");
        controllers.arraySize = 1;
        controllers.GetArrayElementAtIndex(0).FindPropertyRelative("controller").FindPropertyRelative("objRef").objectReferenceValue = ctrl;
        content.FindPropertyRelative("rootObjOverride").objectReferenceValue = go;
        var arr = content.FindPropertyRelative("rewriteBindings");
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
}
