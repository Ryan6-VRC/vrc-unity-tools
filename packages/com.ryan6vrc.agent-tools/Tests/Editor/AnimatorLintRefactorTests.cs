using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// Characterization guard for the AnimatorLint extraction refactor (T1). There were no prior AnimatorLint
// tests — this pins the observable end-to-end contract (summary tokens + verdict) through the extracted
// CollectUnresolvedBindings path, so a behavior drift in the shared helper is caught. Explicit basis keeps
// broken-binding at error-tier, so one unresolved binding must FAIL.
//
// AnimatorLint.Lint resolves scene paths against the ACTIVE scene (its internal FindByHierarchyPath), which
// no preview scene can stand in for — so the fixtures live in the active scene and are torn down in place.
// Nothing is saved: Ryan's real scene file is never written, and the temp controller/clip + emitted RunLog
// are deleted in TearDown.
public class AnimatorLintRefactorTests
{
    private const string TmpDir = "Assets/AgentLintRefactorTmp";
    private const string AssetPath = TmpDir + "/RefactorTest.controller";
    private const string AvatarName = "LintRefactorAvatar";

    private GameObject _avatar;
    private string _logPath;

    [TearDown]
    public void TearDown()
    {
        if (_avatar != null) Object.DestroyImmediate(_avatar);
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

        var result = AnimatorLint.Lint(controller, "explicit", null, AvatarName, AvatarName);
        _logPath = ExtractLogPath(result);

        StringAssert.Contains("brokenBinding=1", result,
            "exactly one unresolved binding (Ghost) must survive; Resolvable must resolve under the basis root: " + result);
        StringAssert.Contains("=> FAIL", result,
            "explicit basis keeps broken-binding at error-tier, so one broken binding is a FAIL: " + result);
        StringAssert.Contains("missingMotion=0 undeclaredParam=0 entryShadow=0", result,
            "no other rule should fire: " + result);
    }

    private static string ExtractLogPath(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, System.StringComparison.Ordinal);
        return i < 0 ? null : result.Substring(i + marker.Length).Trim();
    }
}
