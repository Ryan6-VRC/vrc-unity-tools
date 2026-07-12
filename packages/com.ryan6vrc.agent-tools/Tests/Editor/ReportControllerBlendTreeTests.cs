using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// Locks the empty-vs-broken split for blend-tree CHILD motions (finding F11). Before the fix,
// AppendBlendTree printed a bare "(empty)" for any null child motion — a dangling child (asset
// deleted) was indistinguishable from an intentionally-empty slot. These two tests pin both poles;
// the dangling case would also silently regress if the "m_Childs" serialized key were wrong.
public class ReportControllerBlendTreeTests
{
    private const string Dir = "Assets/Agent/_bt_test";
    private AnimatorController _ctrl;

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(Dir);
        _ctrl = AnimatorController.CreateAnimatorControllerAtPath(Dir + "/bt.controller");
        _ctrl.AddParameter("blend", AnimatorControllerParameterType.Float);
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(Dir);

    private static string PathFrom(string summary)
    {
        int i = summary.IndexOf("log=");
        return i >= 0 ? summary.Substring(i + 4).Trim() : summary;
    }

    [Test]
    public void EmptyBlendTreeChild_RendersEmpty()
    {
        var sm = _ctrl.layers[0].stateMachine;
        var st = sm.AddState("Blend");
        var bt = new BlendTree { name = "bt", blendType = BlendTreeType.Simple1D, blendParameter = "blend" };
        AssetDatabase.AddObjectToAsset(bt, _ctrl);
        st.motion = bt;
        bt.AddChild((Motion)null, 0f);
        var clip = new AnimationClip { name = "live" };
        AssetDatabase.AddObjectToAsset(clip, _ctrl);
        bt.AddChild(clip, 1f);
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string report = File.ReadAllText(PathFrom(ReportController.Report(_ctrl)));
        StringAssert.Contains("(empty)", report);
        StringAssert.Contains("`live`", report);
    }

    [Test]
    public void DanglingBlendTreeChild_RendersBroken_NotEmpty()
    {
        var sm = _ctrl.layers[0].stateMachine;
        var st = sm.AddState("Blend");
        var bt = new BlendTree { name = "bt", blendType = BlendTreeType.Simple1D, blendParameter = "blend" };
        AssetDatabase.AddObjectToAsset(bt, _ctrl);
        st.motion = bt;
        var clip = new AnimationClip { name = "doomed" };
        AssetDatabase.AddObjectToAsset(clip, _ctrl);
        bt.AddChild(clip, 0f);
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        // Rewrite ONLY the blend-tree child's motion ref (the one pointing at the sub-asset clip) into
        // a dangling external guid, then reimport. Targeting the clip's exact local fileID leaves the
        // state->blendtree link intact — so the tree still expands and the CHILD path is truly
        // exercised (a blanket m_Motion rewrite would also break the state motion and pass vacuously).
        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipLocalId);
        string yaml = File.ReadAllText(path);
        yaml = System.Text.RegularExpressions.Regex.Replace(
            yaml, @"m_Motion: \{fileID: " + clipLocalId + @"\}",
            "m_Motion: {fileID: 7400000, guid: deadbeefdeadbeefdeadbeefdeadbeef, type: 2}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        string report = File.ReadAllText(PathFrom(ReportController.Report(reloaded)));
        StringAssert.Contains("broken", report);
        StringAssert.Contains("deadbeefdeadbeefdeadbeefdeadbeef", report);
    }
}
