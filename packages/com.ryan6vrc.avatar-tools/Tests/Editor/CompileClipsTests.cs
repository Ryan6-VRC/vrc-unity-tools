// Behavioral tests for CompileClips (the external-clip writer). Throwaway .anim assets under an owned scratch
// path; the clips-file YAML is written to a temp filesystem path (Parse reads text, not an asset). Assert on
// the returned one-line summary + post-write clip state. Headless via tools/run-editmode-tests.ps1.
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

public class CompileClipsTests
{
    const string Root = "Assets/Agent/Scratch/CompileClips_NUnit";
    const string Out  = "Assets/Agent/Scratch/CompileClips_NUnit/clips";
    string _yamlPath;

    [SetUp] public void SetUp() { AnimatorTestHelpers.EnsureFolder(Root); }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
        if (_yamlPath != null && File.Exists(_yamlPath)) File.Delete(_yamlPath);
        _yamlPath = null;
    }

    string WriteYaml(string body)
    {
        _yamlPath = Path.Combine(Path.GetTempPath(), "clips_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".yaml");
        File.WriteAllText(_yamlPath, body);
        return _yamlPath;
    }

    const string Head = "schema: 1\nbasis: avatar-root\ncontroller: Poses\n";
    const string TwoPoses = Head +
        "clips:\n" +
        "  Wave:  { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n" +
        "  Point: { set: { \"Arm/SkinnedMeshRenderer.blendShape.Point\": 100 } }\n";

    [Test]
    public void Emits_visible_anim_per_clip_with_ref_handles()
    {
        string s = CompileClips.Compile(WriteYaml(TwoPoses), Out);
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(2, AnimatorTestHelpers.Count(s, "emitted"));
        var wave = AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim");
        Assert.IsNotNull(wave);
        Assert.AreEqual(HideFlags.None, wave.hideFlags);
        StringAssert.Contains(Out + "/Wave.anim", s);
    }

    [Test]
    public void Recompile_is_guid_stable()
    {
        CompileClips.Compile(WriteYaml(TwoPoses), Out);
        string g1 = AssetDatabase.AssetPathToGUID(Out + "/Wave.anim");
        CompileClips.Compile(WriteYaml(TwoPoses), Out);
        Assert.AreEqual(g1, AssetDatabase.AssetPathToGUID(Out + "/Wave.anim"));
    }

    [Test]
    public void Residual_binding_removed_on_recompile()
    {
        CompileClips.Compile(WriteYaml(TwoPoses), Out);
        string mutated = TwoPoses.Replace("blendShape.Wave", "blendShape.Salute");
        CompileClips.Compile(WriteYaml(mutated), Out);
        Assert.IsFalse(ClipHasProp(Out + "/Wave.anim", "blendShape.Wave"));
        Assert.IsTrue(ClipHasProp(Out + "/Wave.anim", "blendShape.Salute"));
    }

    static bool ClipHasProp(string path, string prop)
    {
        var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        foreach (var b in AnimationUtility.GetCurveBindings(c)) if (b.propertyName == prop) return true;
        return false;
    }

    [Test]
    public void Absent_clip_is_not_pruned()
    {
        CompileClips.Compile(WriteYaml(TwoPoses), Out);
        string onlyWave = Head + "clips:\n  Wave: { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n";
        CompileClips.Compile(WriteYaml(onlyWave), Out);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Point.anim"));
    }

    [Test]
    public void Layers_present_is_refused()
    {
        string withLayers = TwoPoses + "layers:\n  - name: L\n    states: { Idle: {} }\n";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("CompileController"));
        string s = CompileClips.Compile(WriteYaml(withLayers), Out);
        StringAssert.Contains("FAIL", s);
        StringAssert.Contains("CompileController", s);
    }

    [Test]
    public void Batch_atomic_on_a_bad_clip()
    {
        // "Arm/UI.Image.enabled" → ResolveComponentType("UI") rejects a non-UnityEngine-namespace type →
        // EmitException in the build-all phase, before any write. Good.anim must therefore never appear.
        string body = Head + "clips:\n" +
            "  Good: { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n" +
            "  Bad:  { set: { \"Arm/UI.Image.enabled\": 1 } }\n";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("clip build failed"));
        string s = CompileClips.Compile(WriteYaml(body), Out);
        StringAssert.Contains("FAIL", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Good.anim"));
    }

    [Test]
    public void WhatIf_writes_nothing()
    {
        string s = CompileClips.Compile(WriteYaml(TwoPoses), Out, force: false, whatIf: true);
        StringAssert.Contains("=> PASS", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim"));
    }
}
