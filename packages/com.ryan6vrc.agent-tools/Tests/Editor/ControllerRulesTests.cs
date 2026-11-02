using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// Pins the raw ControllerRules.Run contract on an in-memory, unsaved controller, the shape CompileController hands
// it, including the roots-empty broken-binding skip. CheckAnimatorTests covers CheckAnimator end to end.
public class ControllerRulesTests
{
    private AnimatorController _controller;

    [SetUp]
    public void SetUp()
    {
        // Building sub-objects on a non-persistent controller can log benign warnings — don't fail on them.
        LogAssert.ignoreFailingMessages = true;
    }

    [TearDown]
    public void TearDown()
    {
        if (_controller != null) Object.DestroyImmediate(_controller);
        _controller = null;
        LogAssert.ignoreFailingMessages = false;
    }

    [Test]
    public void Run_On_InMemory_Controller_Fires_Undeclared_And_Shadow()
    {
        _controller = new AnimatorController();
        _controller.AddParameter("Declared", AnimatorControllerParameterType.Bool);
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;

        var a = sm.AddState("A");
        var b = sm.AddState("B");

        var tr = a.AddTransition(b);
        tr.AddCondition(AnimatorConditionMode.If, 0f, "UndeclaredParam");

        sm.AddEntryTransition(a);
        sm.AddEntryTransition(b);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: true, pathRewrite: null);

        Assert.AreEqual(1, r.UndeclaredParam, "the single undeclared condition parameter must be counted");
        Assert.GreaterOrEqual(r.EntryShadow, 1, "the second entry transition is shadowed by the earlier unconditional entry");
        Assert.AreEqual(0, r.BrokenBinding, "roots empty ⇒ the broken-binding rule is skipped (no basis root)");
    }

    [Test]
    public void Run_Flags_NonFloat_BlendParameter_As_Error()
    {
        // Measured: a blend tree evaluates only Float parameters, so a name-matched Int reads 0 silently and
        // the tree never leaves its zero branch.
        _controller = new AnimatorController();
        _controller.AddParameter("IntSpeed", AnimatorControllerParameterType.Int);
        _controller.AddParameter("FloatSpeed", AnimatorControllerParameterType.Float);
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;

        var bad = new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "IntSpeed" };
        var good = new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "FloatSpeed" };
        var undeclared = new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "NoSuchParam" };
        sm.AddState("Bad").motion = bad;
        sm.AddState("Good").motion = good;
        sm.AddState("Undeclared").motion = undeclared;

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: true, pathRewrite: null);

        Assert.AreEqual(1, r.NonFloatBlendParam, "exactly the declared-Int blend parameter fires the rule");
        Assert.IsTrue(r.Errors.Any(o => o.Kind == "nonFloatBlendParam" && o.Detail.Contains("IntSpeed") && o.Detail.Contains("Int")),
            "the offender names the parameter and its declared type");
        Assert.IsFalse(r.Errors.Any(o => o.Kind == "nonFloatBlendParam" && o.Detail.Contains("FloatSpeed")),
            "a Float blend parameter is the correct shape — never an offender");
        Assert.AreEqual(1, r.UndeclaredParam, "the undeclared blend parameter stays Rule 2's offender, not this rule's");

        Object.DestroyImmediate(bad); Object.DestroyImmediate(good); Object.DestroyImmediate(undeclared);
    }

    [Test]
    public void Run_Flags_NoCondition_NoExit_Transition_As_Dead_Error()
    {
        _controller = new AnimatorController();
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;
        var a = sm.AddState("A");
        var b = sm.AddState("B");

        // No conditions AND no exit time AND not a to-Exit transition — Unity can never activate it.
        var tr = a.AddTransition(b);
        tr.hasExitTime = false;

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: true, pathRewrite: null);

        Assert.AreEqual(1, r.DeadTransition, "the no-condition + no-exit transition is a dead (never-firing) transition");
        Assert.IsTrue(r.Errors.Any(o => o.Kind == "deadTransition" && o.Where.Contains("A") && o.Where.Contains("B")),
            "the dead transition is an error-tier offender named by source -> dest");
    }

    [Test]
    public void Run_Advises_A_Rung_To_Its_Own_State_With_CanTransitionToSelf_Off()
    {
        _controller = new AnimatorController();
        _controller.AddParameter("P", AnimatorControllerParameterType.Bool);
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;
        var a = sm.AddState("A");
        var b = sm.AddState("B");

        var dead = a.AddTransition(a);
        dead.canTransitionToSelf = false;
        dead.AddCondition(AnimatorConditionMode.If, 0, "P");
        var live = b.AddTransition(b);
        live.canTransitionToSelf = true;
        live.AddCondition(AnimatorConditionMode.If, 0, "P");
        var other = a.AddTransition(b);      // the flag decides nothing on a rung to another state
        other.canTransitionToSelf = false;
        other.AddCondition(AnimatorConditionMode.IfNot, 0, "P");
        var c = sm.AddState("C");
        var muted = c.AddTransition(c);      // muted: dead whatever the flag, so the flag is not the cause
        muted.canTransitionToSelf = false;
        muted.mute = true;
        muted.AddCondition(AnimatorConditionMode.If, 0, "P");

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: true, pathRewrite: null);

        var hits = r.Advisories.Where(o => o.Kind == "deadSelfRung").ToList();
        Assert.AreEqual(1, hits.Count, "only A's rung to itself, at flag off, is advised");
        StringAssert.Contains("'A'", hits[0].Where);
        Assert.IsEmpty(r.Errors, "advisory tier: a dead rung to itself does not flip the verdict");
    }

    [Test]
    public void Run_Does_Not_Flag_ExitTime_From_Motionless_State()
    {
        _controller = new AnimatorController();
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;
        var a = sm.AddState("A");
        var b = sm.AddState("B");

        var tr = a.AddTransition(b);
        tr.hasExitTime = true;
        tr.exitTime = 1.0f;

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: true, pathRewrite: null);

        Assert.AreEqual(0, r.DeadTransition, "a motionless exit-time transition fires normally — not a dead transition");
        Assert.IsFalse(r.Advisories.Any(o => o.Kind.StartsWith("deadTransition")),
            "the motionless exit-time timer idiom must not be flagged at all");
    }

    // ----- nonFloatParamCurve -----------------------------------------------------------------------
    // Ground truth: docs/runtime.md §Animator evaluation.

    private static AnimatorController WithParamCurve(string param, AnimatorControllerParameterType type)
    {
        var c = new AnimatorController();
        c.AddParameter(param, type);
        c.AddLayer("Base");
        var st = c.layers[0].stateMachine.AddState("Play");

        var clip = new AnimationClip { name = "writes_" + param };
        // The path-less Animator binding whose property names a declared parameter IS the parameter curve.
        UnityEditor.AnimationUtility.SetEditorCurve(
            clip,
            UnityEditor.EditorCurveBinding.FloatCurve("", typeof(Animator), param),
            AnimationCurve.Constant(0f, 1f / 60f, 1f));
        st.motion = clip;
        return c;
    }

    [Test]
    public void NonFloatParamCurve_Fires_On_A_Bool_Parameter_Curve()
    {
        _controller = WithParamCurve("Flag", AnimatorControllerParameterType.Bool);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(1, r.NonFloatParamCurve, "a clip curve on a Bool parameter is an error-tier defect");
        var o = r.Errors.FirstOrDefault(e => e.Kind == "nonFloatParamCurve");
        Assert.IsNotNull(o, "the offender is reported at error tier");
        StringAssert.Contains("Flag", o.Detail, "the offender names the parameter");
    }

    [Test]
    public void NonFloatParamCurve_Fires_On_An_Int_Parameter_Curve()
    {
        _controller = WithParamCurve("Count", AnimatorControllerParameterType.Int);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(1, r.NonFloatParamCurve, "a clip curve on an Int parameter is the same defect");
    }

    [Test]
    public void NonFloatParamCurve_Ignores_A_Float_Parameter_Curve()
    {
        _controller = WithParamCurve("Smoothed", AnimatorControllerParameterType.Float);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(0, r.NonFloatParamCurve, "a Float parameter curve is the legal AAP idiom");
        Assert.IsFalse(r.Errors.Any(o => o.Kind == "nonFloatParamCurve"));
    }

    [Test]
    public void NonFloatParamCurve_Ignores_Humanoid_Muscle_And_Tdof_Curves()
    {
        // Muscle and TDOF curves use the same path-less Animator binding shape as a parameter curve; only
        // their property naming no declared parameter tells them apart. The declared Bool keeps the rule armed.
        _controller = new AnimatorController();
        _controller.AddParameter("Flag", AnimatorControllerParameterType.Bool);
        _controller.AddLayer("Base");
        var st = _controller.layers[0].stateMachine.AddState("Pose");

        var clip = new AnimationClip { name = "muscle" };
        foreach (var prop in new[] { "LeftHand.Index.1 Stretched", "SpineTDOF.x", "RootT.y" })
            UnityEditor.AnimationUtility.SetEditorCurve(
                clip,
                UnityEditor.EditorCurveBinding.FloatCurve("", typeof(Animator), prop),
                AnimationCurve.Constant(0f, 1f / 60f, 0.5f));
        st.motion = clip;

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(0, r.NonFloatParamCurve, "muscle and TDOF curves name no parameter and are not this defect");
    }

    [Test]
    public void NonFloatParamCurve_Reports_Each_Parameter_Once()
    {
        _controller = WithParamCurve("Flag", AnimatorControllerParameterType.Bool);
        var second = new AnimationClip { name = "writes_Flag_again" };
        UnityEditor.AnimationUtility.SetEditorCurve(
            second,
            UnityEditor.EditorCurveBinding.FloatCurve("", typeof(Animator), "Flag"),
            AnimationCurve.Constant(0f, 1f / 60f, 1f));
        _controller.layers[0].stateMachine.AddState("Play2").motion = second;

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(1, r.NonFloatParamCurve, "one offending parameter ⇒ one offender, however many clips write it");
    }

    // ----- driverOnAnimatedParam ---------------------------------------------------------------------
    // Ground truth: docs/runtime.md §Animator evaluation.

    private static AnimatorController WithCurveAndDriver(string curveParam, string driverParam,
        VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType opType, bool asCopySource)
    {
        var c = new AnimatorController();
        c.AddParameter(curveParam, AnimatorControllerParameterType.Float);
        if (driverParam != curveParam) c.AddParameter(driverParam, AnimatorControllerParameterType.Float);
        c.AddParameter("Other", AnimatorControllerParameterType.Float);
        c.AddLayer("Base");
        var sm = c.layers[0].stateMachine;

        var clip = new AnimationClip { name = "holds_" + curveParam };
        UnityEditor.AnimationUtility.SetEditorCurve(
            clip,
            UnityEditor.EditorCurveBinding.FloatCurve("", typeof(Animator), curveParam),
            AnimationCurve.Constant(0f, 1f / 60f, 1f));
        sm.AddState("Holds").motion = clip;

        var drvState = sm.AddState("Drives");
        // CreateInstance + assign, not AddStateMachineBehaviour<T>(): the latter goes through asset/undo
        // machinery that has no asset to work with on an unsaved controller.
        var drv = ScriptableObject.CreateInstance<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
        drvState.behaviours = new StateMachineBehaviour[] { drv };
        drv.parameters = new System.Collections.Generic.List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
        {
            asCopySource
                ? new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { type = opType, name = "Other", source = driverParam }
                : new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { type = opType, name = driverParam, value = 1f }
        };
        return c;
    }

    [Test]
    public void DriverOnAnimatedParam_Fires_On_A_Driver_Write()
    {
        _controller = WithCurveAndDriver("Held", "Held",
            VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set, asCopySource: false);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(1, r.DriverOnAnimatedParam, "a driver Set on a clip-bound parameter is an error-tier defect");
        var o = r.Errors.FirstOrDefault(e => e.Kind == "driverOnAnimatedParam");
        Assert.IsNotNull(o);
        StringAssert.Contains("writes", o.Detail, "the offender states the direction");
    }

    [Test]
    public void DriverOnAnimatedParam_Fires_On_A_Copy_Source_Read()
    {
        _controller = WithCurveAndDriver("Held", "Held",
            VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy, asCopySource: true);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(1, r.DriverOnAnimatedParam, "a driver Copy whose SOURCE is clip-bound is equally dead");
        var o = r.Errors.FirstOrDefault(e => e.Kind == "driverOnAnimatedParam");
        Assert.IsNotNull(o);
        StringAssert.Contains("reads", o.Detail, "the offender states the direction");
    }

    [Test]
    public void DriverOnAnimatedParam_Ignores_A_Driver_On_A_Param_No_Clip_Binds()
    {
        _controller = WithCurveAndDriver("Held", "Free",
            VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set, asCopySource: false);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(0, r.DriverOnAnimatedParam, "a driver on a parameter no clip animates is the correct idiom");
    }

    [Test]
    public void DriverOnAnimatedParam_Ignores_A_BlendTree_Read_Of_The_Same_Param()
    {
        _controller = new AnimatorController();
        _controller.AddParameter("Held", AnimatorControllerParameterType.Float);
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;

        var clip = new AnimationClip { name = "holds_Held" };
        UnityEditor.AnimationUtility.SetEditorCurve(
            clip,
            UnityEditor.EditorCurveBinding.FloatCurve("", typeof(Animator), "Held"),
            AnimationCurve.Constant(0f, 1f / 60f, 1f));
        sm.AddState("Holds").motion = clip;

        var tree = new BlendTree { name = "ReadsHeld", blendType = BlendTreeType.Simple1D, blendParameter = "Held" };
        tree.children = new[] { new ChildMotion { motion = clip, threshold = 0f, timeScale = 1f } };
        sm.AddState("Reads").motion = tree;

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(0, r.DriverOnAnimatedParam, "a blend tree may read a clip-written float — that is the AAP idiom");
        Assert.AreEqual(0, r.NonFloatBlendParam, "and the parameter is a Float, so the blend-param rule is silent too");
    }

    [Test]
    public void DriverOnAnimatedParam_Fires_On_A_Synced_Layer_Override_Driver()
    {
        // Run() excludes synced layers from the state/machine topology, so a rule walking only that topology
        // cannot see a driver installed as a synced layer's per-state behaviour override.
        _controller = new AnimatorController();
        _controller.AddParameter("Held", AnimatorControllerParameterType.Float);
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;

        var clip = new AnimationClip { name = "holds_Held" };
        UnityEditor.AnimationUtility.SetEditorCurve(
            clip,
            UnityEditor.EditorCurveBinding.FloatCurve("", typeof(Animator), "Held"),
            AnimationCurve.Constant(0f, 1f / 60f, 1f));
        var srcState = sm.AddState("Holds");
        srcState.motion = clip;

        var drv = ScriptableObject.CreateInstance<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
        drv.parameters = new System.Collections.Generic.List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
        {
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
            {
                type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set, name = "Held", value = 1f
            }
        };

        // A second layer synced to layer 0, carrying its own behaviour override on that shared state. Both
        // writes go through the SAME array instance that is assigned back: `controller.layers` hands out fresh
        // wrapper objects each call, so mutating `controller.layers[1]` inline would write to a throwaway.
        _controller.AddLayer("Synced");
        var layers = _controller.layers;
        layers[1].syncedLayerIndex = 0;
        layers[1].SetOverrideBehaviours(srcState, new StateMachineBehaviour[] { drv });
        _controller.layers = layers;

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(1, r.DriverOnAnimatedParam,
            "a driver installed as a synced-layer behaviour override is not exempt");
        var o = r.Errors.FirstOrDefault(e => e.Kind == "driverOnAnimatedParam");
        Assert.IsNotNull(o);
        StringAssert.Contains("synced layer", o.Where, "the offender names the synced layer as its site");
    }

    // ── IsVrcReserved: the built-in exempt set ─────────────────────────────────────────────────────
    //
    // The undeclared-param rule and the compiler's VRCExpressionParameters emitter (ControllerEmit) share this
    // predicate, so a missing name is both a false FAIL and a parameter published onto an avatar that owns it.

    [Test]
    public void IsVrcReserved_IsAnimatorEnabled_IsABuiltIn()
        => Assert.IsTrue(ControllerRules.IsVrcReserved("IsAnimatorEnabled"));

    [Test]
    public void IsVrcReserved_PreviewMode_IsABuiltIn()
        => Assert.IsTrue(ControllerRules.IsVrcReserved("PreviewMode"));

    [Test]
    public void IsVrcReserved_AnAuthoredName_IsNotReserved()
        => Assert.IsFalse(ControllerRules.IsVrcReserved("IsAnimatorEnabledToo"));

    [Test]
    public void IsVrcReserved_IsCaseSensitive()
        => Assert.IsFalse(ControllerRules.IsVrcReserved("isAnimatorEnabled"));

    [Test]
    public void IsVrcReserved_Null_IsNotReserved()
        => Assert.IsFalse(ControllerRules.IsVrcReserved(null));
}
