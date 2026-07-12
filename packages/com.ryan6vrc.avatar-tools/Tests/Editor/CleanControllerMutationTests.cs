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

// Behavioral tests for CleanController's mutation path (copy→trim→param-prune→descriptor-wire). Throwaway
// on-disk assets under a scratch path + a scene avatar with a VRCAvatarDescriptor. Headless via
// tools/run-editmode-tests.ps1. (SelectLayersToKeep is covered separately in CleanControllerSelectLayersTests.)
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

    // Source FX: base layer + GestureLeft (an AnyState transition gated on int param "GestureLeft" into
    // a Direct blend tree using directBlendParameter "BlendParam" + a parameter-driver writing
    // "DriverParam") + GestureRight (transition condition on "GestureRight"). Plus "UnusedVendorParam"
    // referenced by nothing. Keeping only GestureLeft must PRUNE GestureRight + UnusedVendorParam and
    // KEEP GestureLeft + BlendParam + DriverParam.
    //
    // ADAPTED from the plan's starting code: the plan's fixture never referenced parameter "GestureLeft"
    // anywhere (only the layer bore that name) — its own prune test then asserted the param survives,
    // which it can't under CollectReferencedParameters' actual rules (transition conditions / blend-tree
    // params / state bindings / driver refs only). Added a realistic AnyState transition gated on
    // "GestureLeft" (the standard VRC gesture-select pattern) so the param has a genuine referencing path
    // distinct from BlendParam (blend-tree) and DriverParam (driver) — this ADDS a third, more thorough
    // prune-survival path rather than removing coverage of the two required in the task brief.
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
        // driver.parameters is a List<Parameter> and is null on a fresh behaviour — must assign a new
        // list, not append to one (confirmed against ControllerEmit.PopulateDriver, which always
        // constructs+assigns a fresh List rather than mutating an existing one).
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
        // whatIf must create NO side-effect assets and leave the descriptor's expression wiring untouched —
        // checking only the controller + isDefault would let a whatIf that wrongly minted the empty assets
        // or flipped customExpressions slip through.
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters>(Root + "/VRCExpressionParameters_Empty.asset"), "whatIf created no params asset");
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu>(Root + "/VRCExpressionsMenu_Empty.asset"), "whatIf created no menu asset");
        Assert.IsFalse(d.customExpressions, "whatIf must not flip customExpressions");
        Assert.IsNull(d.expressionParameters, "whatIf must not wire params");
        Assert.IsNull(d.expressionsMenu, "whatIf must not wire menu");
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
        // FinishEarly (via TransplantCore.Finish) and the Step-6-verify FAIL path (via BuildSummary)
        // both log exactly once — one Expect.
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
        // Non-whatIf execute has no dedicated early-abort for a missing FX base layer — it proceeds
        // through the copy/trim/prune/empty-asset steps and only FAILs at Step 6 verify, with message
        // "descriptor FX not wired to clean controller (isDefault or wrong ref)" (source line ~447).
        // Matches the same "FX not wired" substring the whatIf-path early-abort uses.
        LogAssert.Expect(LogType.Error, new Regex("FX not wired"));
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> FAIL", s);
    }

    [Test]
    public void Fails_when_params_reuse_path_holds_wrong_typed_asset()
    {
        var src = BuildSourceFx(Root + "/Src.controller");
        var d = BuildAvatarWithFxSlot();
        // ADAPTED from the plan: the plan's starting code planted the decoy at the CONTROLLER reuse path
        // (Src_Clean.controller) and expected a "not an AnimatorController"/"not a" message. Reading
        // CleanController.cs shows the controller-path reuse guard's FAIL message is actually
        // "LoadAssetAtPath returned null for <path>" (line 247) — it never says "is not a…". Only the
        // params/menu reuse paths carry that literal wording ("asset at <path> exists but is not a
        // VRCExpressionParameters/VRCExpressionsMenu", lines 293/325). So the decoy goes at the params
        // path instead, which is where the "wrong-typed asset FAILs loud" contract actually has that
        // phrasing to assert against.
        AnimatorTestHelpers.MakeClip(Root + "/VRCExpressionParameters_Empty.asset");
        // FinishEarly logs the FAIL summary exactly once (via TransplantCore.Finish) — one Expect.
        LogAssert.Expect(LogType.Error, new Regex("exists but is not a VRCExpressionParameters"));
        string s = CleanController.Run(src, _avatar, Root, new[] { "GestureLeft" });
        StringAssert.Contains("=> FAIL", s);
    }
}
