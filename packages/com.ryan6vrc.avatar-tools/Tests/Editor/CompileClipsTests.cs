using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

public class CompileClipsTests
{
    const string Root = "Assets/Agent/Scratch/CompileClips_NUnit";
    const string Out  = "Assets/Agent/Scratch/CompileClips_NUnit/clips";
    const string VendorRoot = "Assets/Vendor/CompileClips_NUnit";
    string _yamlPath;
    bool _createdVendor;

    [SetUp] public void SetUp() { AnimatorTestHelpers.EnsureFolder(Root); }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
        AssetDatabase.DeleteAsset(VendorRoot);
        if (_createdVendor && AssetDatabase.IsValidFolder("Assets/Vendor")
            && AssetDatabase.FindAssets("", new[] { "Assets/Vendor" }).Length == 0)
            AssetDatabase.DeleteAsset("Assets/Vendor");
        _createdVendor = false;
        if (_yamlPath != null && File.Exists(_yamlPath)) File.Delete(_yamlPath);
        _yamlPath = null;
    }

    void EnsureVendor()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Vendor")) { AssetDatabase.CreateFolder("Assets", "Vendor"); _createdVendor = true; }
        AnimatorTestHelpers.EnsureFolder(VendorRoot);
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
        var probe = new AnimatorTestHelpers.DirtyMaterialProbe(Root, "Unrelated");
        string s = CompileClips.Run(WriteYaml(TwoPoses), Out);
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(2, AnimatorTestHelpers.Count(s, "emitted"));
        var wave = AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim");
        Assert.IsNotNull(wave);
        Assert.AreEqual(HideFlags.None, wave.hideFlags);
        StringAssert.Contains(Out + "/Wave.anim", s);
        probe.AssertWasNotSaved();
    }

    [Test]
    public void Recompile_is_guid_stable()
    {
        CompileClips.Run(WriteYaml(TwoPoses), Out);
        string g1 = AssetDatabase.AssetPathToGUID(Out + "/Wave.anim");
        CompileClips.Run(WriteYaml(TwoPoses), Out);
        Assert.AreEqual(g1, AssetDatabase.AssetPathToGUID(Out + "/Wave.anim"));
    }

    [Test]
    public void Residual_binding_removed_on_recompile()
    {
        CompileClips.Run(WriteYaml(TwoPoses), Out);
        string mutated = TwoPoses.Replace("blendShape.Wave", "blendShape.Salute");
        CompileClips.Run(WriteYaml(mutated), Out);
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
        CompileClips.Run(WriteYaml(TwoPoses), Out);
        string onlyWave = Head + "clips:\n  Wave: { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n";
        CompileClips.Run(WriteYaml(onlyWave), Out);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Point.anim"));
    }

    [Test]
    public void Layers_present_is_refused()
    {
        string withLayers = TwoPoses + "layers:\n  - name: L\n    states: { Idle: {} }\n";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("CompileController"));
        string s = CompileClips.Run(WriteYaml(withLayers), Out);
        StringAssert.Contains("FAIL", s);
        StringAssert.Contains("CompileController", s);
    }

    [Test]
    public void Batch_atomic_on_a_bad_clip()
    {
        // "Arm/UI.Image.enabled" names component type `UI`, which resolves in no allowlisted namespace, so the
        // build-all phase throws before any write and Good.anim never appears.
        string body = Head + "clips:\n" +
            "  Good: { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n" +
            "  Bad:  { set: { \"Arm/UI.Image.enabled\": 1 } }\n";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("clip build failed"));
        string s = CompileClips.Run(WriteYaml(body), Out);
        StringAssert.Contains("FAIL", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Good.anim"));
    }

    [Test]
    public void Sanitized_filename_collision_is_refused()
    {
        // "Wave Left" and "Wave_Left" both sanitize to "Wave_Left.anim".
        string body = Head + "clips:\n" +
            "  \"Wave Left\": { set: { \"Arm/SkinnedMeshRenderer.blendShape.A\": 100 } }\n" +
            "  \"Wave_Left\": { set: { \"Arm/SkinnedMeshRenderer.blendShape.B\": 100 } }\n";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("collision"));
        string s = CompileClips.Run(WriteYaml(body), Out);
        StringAssert.Contains("FAIL", s);
        StringAssert.Contains("Wave Left", s);
        StringAssert.Contains("Wave_Left", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave_Left.anim"), "nothing written");
    }

    [Test]
    public void Sanitized_filename_collision_is_case_insensitive()
    {
        // "Wave.anim" and "wave.anim" are distinct under Ordinal but one file on case-insensitive NTFS.
        string body = Head + "clips:\n" +
            "  \"Wave\": { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n" +
            "  \"wave\": { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("collision"));
        string s = CompileClips.Run(WriteYaml(body), Out);
        StringAssert.Contains("FAIL", s);
        StringAssert.Contains("Wave", s);
        StringAssert.Contains("wave", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim"), "nothing written");
        Assert.IsFalse(AssetDatabase.IsValidFolder(Out), "collision refused before the out folder is created");
    }

    [Test]
    public void Readonly_outDir_fails_unless_force()
    {
        EnsureVendor();
        string vendorOut = VendorRoot + "/out";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("read-only outDir"));
        string fail = CompileClips.Run(WriteYaml(TwoPoses), vendorOut);
        StringAssert.Contains("FAIL", fail);
        StringAssert.Contains("read-only outDir", fail);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(vendorOut + "/Wave.anim"), "nothing written without force");

        string forced = CompileClips.Run(WriteYaml(TwoPoses), vendorOut, force: true);
        StringAssert.Contains("=> PASS", forced);
        StringAssert.Contains("read-only outDir override (force)", forced);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(vendorOut + "/Wave.anim"), "force writes into vendor outDir");
    }

    [Test]
    public void WhatIf_writes_nothing()
    {
        string s = CompileClips.Run(WriteYaml(TwoPoses), Out, force: false, whatIf: true);
        StringAssert.Contains("=> PASS", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim"));
    }

    [Test]
    public void Content_hash_is_stable_and_edit_sensitive()
    {
        CompileClips.Run(WriteYaml(TwoPoses), Out);
        string h1 = CompileClips.ReadContentStamp(Out + "/Wave.anim");
        Assert.IsFalse(string.IsNullOrEmpty(h1), "stamp written on emit");
        CompileClips.Run(WriteYaml(TwoPoses), Out);                 // identical recompile
        Assert.AreEqual(h1, CompileClips.ReadContentStamp(Out + "/Wave.anim"), "stable for identical content");
        var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim");
        Assert.AreEqual(h1, CompileClips.HashClipContent(c), "hash of the on-disk clip matches its stamp");
        string mutated = TwoPoses.Replace("blendShape.Wave", "blendShape.Salute");
        // Changed YAML over an unedited clip: the on-disk clip still matches its stamp, so it overwrites
        // without force.
        CompileClips.Run(WriteYaml(mutated), Out);
        Assert.AreNotEqual(h1, CompileClips.ReadContentStamp(Out + "/Wave.anim"), "hash changes when content changes");
    }

    [Test]
    public void Weighted_tangent_edit_is_caught()
    {
        // The content hash covers weightedMode/inWeight/outWeight, so a no-force recompile refuses rather than
        // clobbering a weighted-tangent toggle, a common smoothing hand-edit.
        CompileClips.Run(WriteYaml(TwoPoses), Out);                  // emit + stamp Wave (PASS)
        var wave = AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim");
        var binding = AnimationUtility.GetCurveBindings(wave)[0];
        var curve = AnimationUtility.GetEditorCurve(wave, binding);
        var keys = curve.keys;
        keys[0].weightedMode = WeightedMode.Both;                       // the smoothing hand-edit
        curve.keys = keys;
        AnimationUtility.SetEditorCurve(wave, binding, curve);
        AnimatorTestHelpers.Save(wave, Out + "/Wave.anim");            // edit lands on disk (stamp now stale)

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("refusing to clobber"));
        string s = CompileClips.Run(WriteYaml(TwoPoses), Out);      // no-force recompile
        StringAssert.Contains("FAIL", s);
        StringAssert.Contains("Wave", s);

        var after = AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim");
        var afterKeys = AnimationUtility.GetEditorCurve(after, binding).keys;
        Assert.AreEqual(WeightedMode.Both, afterKeys[0].weightedMode, "weighted-tangent edit preserved (refused)");
    }

    [Test]
    public void Refuses_to_clobber_a_hand_edit()
    {
        CompileClips.Run(WriteYaml(TwoPoses), Out);                  // emit + stamp Wave (a PASS)
        var wave = AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Wave.anim");
        AnimatorTestHelpers.AddFloatCurve(wave, "HandEdited", typeof(Transform), "m_LocalPosition.x");
        AnimatorTestHelpers.Save(wave, Out + "/Wave.anim");             // human edit lands on disk (stamp now stale)

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("refusing to clobber"));
        string s = CompileClips.Run(WriteYaml(TwoPoses), Out);
        StringAssert.Contains("FAIL", s);
        StringAssert.Contains("Wave", s);
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(Out + "/Wave.anim", "HandEdited"), "hand-edit preserved (refused, nothing written)");

        string s2 = CompileClips.Run(WriteYaml(TwoPoses), Out, force: true);   // force overrides → overwrite + re-stamp
        StringAssert.Contains("=> PASS", s2);
        Assert.IsFalse(AnimatorTestHelpers.ClipHasBinding(Out + "/Wave.anim", "HandEdited"), "force reverts to YAML content");
    }

    [Test]
    public void Adopting_an_unstamped_anim_refuses_without_force()
    {
        AnimatorTestHelpers.EnsureFolder(Out);
        var pre = AnimatorTestHelpers.MakeClip(Out + "/Wave.anim");
        AnimatorTestHelpers.AddFloatCurve(pre, "Human", typeof(Transform), "m_LocalPosition.x");
        AnimatorTestHelpers.Save(pre, Out + "/Wave.anim");             // pre-existing human .anim, never stamped by us
        // An unstamped clip counts as diverged, so the recompile refuses and writes nothing, Point included.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("refusing to clobber"));
        string s = CompileClips.Run(WriteYaml(TwoPoses), Out);
        StringAssert.Contains("FAIL", s);
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(Out + "/Wave.anim", "Human"), "unstamped human .anim protected (nothing written)");
    }

    [Test]
    public void Controller_resolves_an_external_clip_by_ref_path()
    {
        string one = Head + "clips:\n  Wave: { set: { \"Arm/SkinnedMeshRenderer.blendShape.Wave\": 100 } }\n";
        CompileClips.Run(WriteYaml(one), Out);
        string animPath = Out + "/Wave.anim";
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath), "external clip emitted");

        string ctrlYaml =
            "schema: 1\ncontroller: FxExt\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n" +
            "      S: { motion: { ref: \"" + animPath + "\" } }\n" +
            "    default: S\n";
        string cyPath = Path.Combine(Path.GetTempPath(), "ctrl_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".yaml");
        File.WriteAllText(cyPath, ctrlYaml);
        try
        {
            string res = CompileController.Run(cyPath, Out);
            StringAssert.Contains("=> OK", res);
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(Out + "/FxExt.controller");
            Assert.IsNotNull(ctrl, "controller compiled");
            var motion = ctrl.layers[0].stateMachine.states[0].state.motion as AnimationClip;
            Assert.IsNotNull(motion, "state has a clip motion");
            Assert.AreEqual(animPath, AssetDatabase.GetAssetPath(motion), "state resolves to the EXTERNAL clip by path");
            Assert.IsFalse(AssetDatabase.IsSubAsset(motion), "external clip is a standalone asset, not embedded in the controller");
        }
        finally { File.Delete(cyPath); }
    }

    [Test]
    public void Tangents_UnsupportedToken_RefusedAtParse()
    {
        // One token is enough: both reach one throw. The assertion quotes `got '<token>'` because the refusal
        // text names 'auto' and 'free' verbatim as the unsupported pair, so a bare Contains(token) passes for
        // either input.
        string body = Head + "clips:\n  Bad:\n    curves:\n      Prop/Renderer.enabled:\n" +
            "        tangents: auto\n        keys: [[0, 0], [1, 1]]\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(body, "test"));
        StringAssert.Contains("got 'auto'", ex.Message, "the refusal echoes the offending token");
        StringAssert.Contains("'flat', 'linear', or 'stepped'", ex.Message, "and names the supported set");
    }

    [Test]
    public void No_clips_is_refused()
    {
        // Parses and validates (schema + a declared param) but has no `clips:` block.
        string body = "schema: 1\nbasis: avatar-root\ncontroller: Empty\nparameters:\n  P: { type: float }\n";
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no clips"));
        string s = CompileClips.Run(WriteYaml(body), Out);
        StringAssert.Contains("FAIL", s);
        StringAssert.Contains("no clips", s);
        Assert.IsFalse(AssetDatabase.IsValidFolder(Out), "nothing emitted");
    }
}
