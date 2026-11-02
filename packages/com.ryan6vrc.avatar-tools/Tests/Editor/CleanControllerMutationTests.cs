using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Ryan6Vrc.AvatarTools.Editor;

// CleanController's mutation path (copy→trim→param-prune→descriptor-wire), on throwaway on-disk assets under a
// scratch path and a scene avatar with a VRCAvatarDescriptor.
public class CleanControllerMutationTests
{
    const string Root = "Assets/Agent/Scratch/CleanTests_NUnit";
    GameObject _avatar;

    [SetUp] public void SetUp() { AnimatorTestHelpers.EnsureFolder(Root); }

    [TearDown]
    public void TearDown()
    {
        if (_avatar != null) Object.DestroyImmediate(_avatar);
        AssetDatabase.DeleteAsset(Root);
    }

    // The AnyState transition gives param "GestureLeft" a referencing path of its own: a layer bearing the name
    // does not keep it (CollectReferencedParameters walks conditions, blend-tree params, state bindings and
    // driver refs).
    AnimatorController BuildSourceFx(string path)
    {
        var c = AnimatorController.CreateAnimatorControllerAtPath(path);
        c.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);
        c.AddParameter("GestureRight", AnimatorControllerParameterType.Int);
        c.AddParameter("BlendParam", AnimatorControllerParameterType.Float);
        c.AddParameter("DriverParam", AnimatorControllerParameterType.Float);
        c.AddParameter("UnusedVendorParam", AnimatorControllerParameterType.Float);

        c.AddLayer("GestureLeft");
        var left = c.layers[c.layers.Length - 1].stateMachine;
        var bt = new BlendTree { name = "Direct", blendType = BlendTreeType.Direct };
        AssetDatabase.AddObjectToAsset(bt, c);
        var leaf = AnimatorTestHelpers.MakeClip(Root + "/leaf.anim");
        bt.AddChild(leaf);
        var kids = bt.children; kids[0].directBlendParameter = "BlendParam"; bt.children = kids;
        var blendState = left.AddState("Blend");
        blendState.motion = bt;
        var driver = blendState.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
        driver.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
        {
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "DriverParam", value = 1f },
        };
        var anyTr = left.AddAnyStateTransition(blendState);
        anyTr.AddCondition(AnimatorConditionMode.Greater, 0, "GestureLeft");

        c.AddLayer("GestureRight");
        var right = c.layers[c.layers.Length - 1].stateMachine;
        var a = right.AddState("Idle"); var b = right.AddState("Fist");
        var t = a.AddTransition(b); t.AddCondition(AnimatorConditionMode.Greater, 0, "GestureRight");

        AnimatorTestHelpers.Save(c, path);
        return c;
    }

    VRCAvatarDescriptor BuildAvatarWithFxSlot()
    {
        _avatar = new GameObject("TestAvatar");
        var d = _avatar.AddComponent<VRCAvatarDescriptor>();
        d.baseAnimationLayers = new[]
        {
            new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.FX, isDefault = true },
        };
        return d;
    }

    static bool FxWiredTo(VRCAvatarDescriptor d, AnimatorController ctrl)
    {
        foreach (var l in d.baseAnimationLayers)
            if (l.type == VRCAvatarDescriptor.AnimLayerType.FX && l.animatorController == ctrl && !l.isDefault && l.isEnabled)
                return true;
        return false;
    }

    static List<string> ParamNames(AnimatorController c)
    {
        var n = new List<string>(); foreach (var p in c.parameters) n.Add(p.name); return n;
    }

    [Test]
    public void Full_run_trims_prunes_creates_empties_and_wires_descriptor()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        var d = BuildAvatarWithFxSlot();
        var probe = new AnimatorTestHelpers.DirtyMaterialProbe(Root, "Unrelated");
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft", "GestureRight" });
        StringAssert.Contains("=> PASS", s);
        var clean = AssetDatabase.LoadAssetAtPath<AnimatorController>(Root + "/Src_Clean.controller");
        Assert.IsNotNull(clean);
        var names = new List<string>(); foreach (var l in clean.layers) names.Add(l.name);
        CollectionAssert.AreEquivalent(new[] { "Base Layer", "GestureLeft", "GestureRight" }, names);
        Assert.IsTrue(FxWiredTo(d, clean));
        Assert.IsTrue(d.customExpressions, "customExpressions must be enabled or VRChat ignores the assets");
        Assert.IsNotNull(d.expressionParameters); Assert.AreEqual(0, d.expressionParameters.parameters.Length);
        Assert.IsNotNull(d.expressionsMenu); Assert.AreEqual(0, d.expressionsMenu.controls.Count);
        probe.AssertWasNotSaved();
    }

    [Test]
    public void Rerun_reuses_same_asset_guid_and_keeps_descriptor_wired()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        var d = BuildAvatarWithFxSlot();
        CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        string guid1 = AssetDatabase.AssetPathToGUID(Root + "/Src_Clean.controller");

        string s2 = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("(reuse)", s2);
        string guid2 = AssetDatabase.AssetPathToGUID(Root + "/Src_Clean.controller");
        Assert.AreEqual(guid1, guid2, "clean controller GUID is stable across re-runs");
        Assert.IsTrue(FxWiredTo(d, AssetDatabase.LoadAssetAtPath<AnimatorController>(Root + "/Src_Clean.controller")));
    }

    [Test]
    public void Prune_keeps_blendtree_and_driver_refs_drops_unreferenced()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        var d = BuildAvatarWithFxSlot();
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> PASS", s);
        var clean = AssetDatabase.LoadAssetAtPath<AnimatorController>(Root + "/Src_Clean.controller");
        var p = ParamNames(clean);
        CollectionAssert.Contains(p, "GestureLeft", "kept layer's own AnyState transition condition ref must be kept");
        CollectionAssert.Contains(p, "BlendParam", "blend-tree directBlendParameter ref must be kept");
        CollectionAssert.Contains(p, "DriverParam", "parameter-driver ref must be kept");
        CollectionAssert.DoesNotContain(p, "GestureRight", "dropped layer's condition param pruned");
        CollectionAssert.DoesNotContain(p, "UnusedVendorParam", "unreferenced param pruned");
    }

    [Test]
    public void Reused_params_asset_with_content_is_cleared_to_empty()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        var d = BuildAvatarWithFxSlot();
        CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        var pa = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(Root + "/VRCExpressionParameters_Empty.asset");
        pa.parameters = new[] { new VRCExpressionParameters.Parameter { name = "Intruder" } };
        EditorUtility.SetDirty(pa); AssetDatabase.SaveAssets();

        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> PASS", s);
        StringAssert.Contains("cleared to empty", s);
        Assert.AreEqual(0, pa.parameters.Length);
    }

    [Test]
    public void WhatIf_previews_without_creating_assets_or_touching_descriptor()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        var d = BuildAvatarWithFxSlot();
        string preview = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" }, whatIf: true);
        StringAssert.Contains("(whatIf)", preview);
        StringAssert.Contains("=> PASS", preview);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<AnimatorController>(Root + "/Src_Clean.controller"), "no asset created");
        Assert.IsTrue(d.baseAnimationLayers[0].isDefault, "descriptor untouched (still default)");
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters>(Root + "/VRCExpressionParameters_Empty.asset"), "whatIf created no params asset");
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu>(Root + "/VRCExpressionsMenu_Empty.asset"), "whatIf created no menu asset");
        Assert.IsFalse(d.customExpressions, "whatIf must not flip customExpressions");
        Assert.IsNull(d.expressionParameters, "whatIf must not wire params");
        Assert.IsNull(d.expressionsMenu, "whatIf must not wire menu");
    }

    [Test]
    public void WhatIf_on_a_reused_clean_controller_fails_the_name_execute_fails()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        BuildAvatarWithFxSlot();
        StringAssert.Contains("=> PASS", CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" }));

        // That run trimmed GestureRight from Src_Clean; the source still carries it.
        LogAssert.Expect(LogType.Error, new Regex("GestureRight"));
        string preview = CleanController.Run(src, _avatar, Root, new[] { "GestureRight" }, whatIf: true);
        LogAssert.Expect(LogType.Error, new Regex("GestureRight"));
        string execute = CleanController.Run(src, _avatar, Root, new[] { "GestureRight" });
        StringAssert.Contains("=> FAIL", preview);
        StringAssert.Contains("=> FAIL", execute);
    }

    [Test]
    public void RunLog_is_the_shared_envelope()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        BuildAvatarWithFxSlot();
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> PASS", s);
        var path = s.Substring(s.IndexOf("log=") + 4);
        var json = System.IO.File.ReadAllText(path);
        StringAssert.Contains("\"kind\": \"clean-controller\"", json);
        StringAssert.Contains("\"source\": \"Src\"", json);
        StringAssert.DoesNotContain("\"sourceFx\"", json, "old bespoke key renamed to the envelope's source");
        StringAssert.Contains("\"ctrlParamsKept\"", json);
        UnityEditor.AssetDatabase.DeleteAsset(path);
    }

    [Test]
    public void Null_sourceFx_fails_through_the_runlog_grammar_not_a_bare_line()
    {
        BuildAvatarWithFxSlot();
        LogAssert.Expect(LogType.Error, new Regex("sourceFx is null"));
        string s = CleanController.Run(null, _avatar, Root, new string[0]);
        StringAssert.Contains("error=sourceFx is null => FAIL | log=", s);
        var path = s.Substring(s.IndexOf("log=") + 4);
        Assert.IsTrue(System.IO.File.Exists(path), "guard FAIL must write a RunLog: " + path);
        UnityEditor.AssetDatabase.DeleteAsset(path);
    }

    [Test]
    public void Fails_when_no_descriptor()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        _avatar = new GameObject("NoDesc");
        LogAssert.Expect(LogType.Error, new Regex("VRCAvatarDescriptor not found"));
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> FAIL", s);
    }

    [Test]
    public void Fails_when_descriptor_has_no_fx_base_layer()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        _avatar = new GameObject("NoFx");
        var d = _avatar.AddComponent<VRCAvatarDescriptor>();
        d.baseAnimationLayers = new[] { new VRCAvatarDescriptor.CustomAnimLayer { type = VRCAvatarDescriptor.AnimLayerType.Gesture, isDefault = true } };
        LogAssert.Expect(LogType.Error, new Regex("FX not wired"));
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> FAIL", s);
    }

    [Test]
    public void Fails_when_params_reuse_path_holds_wrong_typed_asset()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        var d = BuildAvatarWithFxSlot();
        // The decoy goes at the params path: only the params and menu reuse paths FAIL with "is not a"; the
        // controller path's guard says "LoadAssetAtPath returned null".
        AnimatorTestHelpers.MakeClip(Root + "/VRCExpressionParameters_Empty.asset");
        LogAssert.Expect(LogType.Error, new Regex("exists but is not a VRCExpressionParameters"));
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> FAIL", s);
    }
}
