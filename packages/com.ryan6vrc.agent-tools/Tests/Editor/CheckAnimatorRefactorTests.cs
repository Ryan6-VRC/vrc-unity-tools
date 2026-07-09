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

// Characterization guard for the CheckAnimator extraction refactor (T1). There were no prior CheckAnimator
// tests — this pins the observable end-to-end contract (summary tokens + verdict) through the extracted
// CollectUnresolvedBindings path, so a behavior drift in the shared helper is caught. Explicit basis keeps
// broken-binding at error-tier, so one unresolved binding must FAIL.
//
// CheckAnimator.Lint resolves scene paths against the ACTIVE scene (its internal FindByHierarchyPath), which
// no preview scene can stand in for — so the fixtures live in the active scene and are torn down in place.
// Nothing is saved: Ryan's real scene file is never written, and the temp controller/clip + emitted RunLog
// are deleted in TearDown.
public class CheckAnimatorRefactorTests
{
    private const string TmpDir = "Assets/AgentLintRefactorTmp";
    private const string AssetPath = TmpDir + "/RefactorTest.controller";
    private const string AvatarName = "LintRefactorAvatar";

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
        // Scene: an avatar root with a single child the resolving binding will hit.
        _avatar = new GameObject(AvatarName);
        var child = new GameObject("Resolvable");
        child.transform.SetParent(_avatar.transform);

        // Controller with a state whose clip carries two float bindings: "Resolvable" (hits the child) and
        // "Ghost" (hits nothing). Saved to a temp asset so Lint's YAML/asset-path reads have a real path.
        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentLintRefactorTmp");
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

        var result = CheckAnimator.Lint(controller, "explicit", null, AvatarName, AvatarName);
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("brokenBinding=1", result,
            "exactly one unresolved binding (Ghost) must survive; Resolvable must resolve under the basis root: " + result);
        StringAssert.Contains("=> FAIL", result,
            "explicit basis keeps broken-binding at error-tier, so one broken binding is a FAIL: " + result);
        StringAssert.Contains("missingMotion=0 undeclaredParam=0 entryShadow=0", result,
            "no other rule should fire: " + result);
    }

    // basis=auto BUG-FIX (not merely visibility): under a VRCFury FullController with rewriteBindings,
    // CheckAnimator must apply those rules before resolving, so the (demoted) broken-binding COUNT is
    // truthful. Without the fix, a path a declared rule relocates reads as unresolvable and inflates the
    // count with false sample offenders — the RemyDoll_Fx ~66-false-positive case, at unit scale. Verdict
    // stays PASS (auto still demotes — the D1 error-tier decision is CheckAvatar's charter, not imported here).
    [Test]
    public void AutoBasis_vrcfRewriteBindings_countIsTruthful()
    {
        _avatar = new GameObject(AvatarName);
        var prop = new GameObject("Prop"); prop.transform.SetParent(_avatar.transform);
        var nested = new GameObject("Nested"); nested.transform.SetParent(prop.transform);
        var armature = new GameObject("Armature"); armature.transform.SetParent(nested.transform);
        var bone = new GameObject("Bone"); bone.transform.SetParent(armature.transform); // Prop/Nested/Armature/Bone

        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentLintRefactorTmp");
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
        var result = CheckAnimator.Lint(controller, "auto", AvatarName + "/Prop");
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("brokenBinding=1", result,
            "rewriteBindings must resolve Armature/Bone; only Ghost/Missing may remain — a truthful count: " + result);
        StringAssert.Contains("=> PASS", result, "auto-basis still demotes broken-binding to advisory (PASS): " + result);
    }

    // orphanSubAsset detection (migrated from the deleted orphan-sweep tests when the mutating other half of
    // this rule was removed in favor of the Decompile→Compile round-trip). This is the coverage that would
    // otherwise be lost: that CheckAnimator NAMES a controller sub-asset reachable
    // from no layer. SweepTestDummySmb (its own same-named file, relocated here for assembly visibility) is a
    // StateMachineBehaviour, so it exercises the SMB arm of the five-type filter; HideInHierarchy guards the
    // regression a reintroduced IsSubAsset gate would cause (Unity hides a controller's own sub-objects, so
    // such a gate would silently drop the real dead weight this rule exists to name).
    [Test]
    public void OrphanSubAsset_hiddenSmb_isReported()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentLintRefactorTmp");
        var controller = AnimatorController.CreateAnimatorControllerAtPath(AssetPath);
        controller.layers[0].stateMachine.AddState("A"); // reachable content the orphan must not shadow

        // The orphan: an SMB reachable from no state machine. Sub-assets only exist on a saved asset, so it
        // is added after the controller is on disk. SMBs are ScriptableObjects — CreateInstance, not new().
        var orphan = ScriptableObject.CreateInstance<SweepTestDummySmb>();
        orphan.name = "ORPHAN_SMB";
        AssetDatabase.AddObjectToAsset(orphan, controller);
        orphan.hideFlags = HideFlags.HideInHierarchy;
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(AssetPath);

        // Orphan is advisory-tier → verdict PASS → Debug.Log (no error), so no LogAssert is needed.
        var result = CheckAnimator.Lint(controller, "explicit", null, null, null);
        _logPath = ExtractLogPath(result);

        Assert.Contains("SweepTestDummySmb 'ORPHAN_SMB'", OrphanTokens(result),
            "CheckAnimator must name the hidden orphan sub-asset (regression: a reintroduced IsSubAsset gate hides it): " + result);
    }

    // Parse the emitted RunLog markdown for orphanSubAsset "Type 'name'" tokens (advisory line shape:
    // "- **orphanSubAsset** <Where> — <Detail>"). Reads the file at the in-band "| log=" path.
    private static List<string> OrphanTokens(string result)
    {
        var tokens = new List<string>();
        string rel = ExtractLogPath(result);
        if (string.IsNullOrEmpty(rel)) return tokens;
        string proj = Application.dataPath;
        proj = proj.Substring(0, proj.Length - "Assets".Length); // project root (dataPath ends in /Assets)
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

    // Minimal real-VRCFury FullController: controllers=[ctrl], rootObjOverride=go, and the given rewriteBindings.
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
