using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// Exercises the extracted ControllerRules.Run door directly on an IN-MEMORY (unsaved) controller — the shape
// a future compiler hands it, with no CheckAnimator basis resolution and no asset I/O. The saved-asset surface
// (and the byte-identical Emit rendering) is already characterized end-to-end by CheckAnimatorRefactorTests
// through the Lint door; this pins only the raw Run contract, including the roots-empty broken-binding skip
// the compiler relies on.
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
        // Unsaved, in-memory controller (no asset path) — the compiler-door shape.
        _controller = new AnimatorController();
        _controller.AddParameter("Declared", AnimatorControllerParameterType.Bool);
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;

        var a = sm.AddState("A");
        var b = sm.AddState("B");

        // A transition condition references a parameter that is NOT declared → undeclaredParam.
        var tr = a.AddTransition(b);
        tr.AddCondition(AnimatorConditionMode.If, 0f, "UndeclaredParam");

        // Entry ladder: an unconditional entry followed by a second entry — the second is unreachable.
        sm.AddEntryTransition(a); // unconditional (no conditions)
        sm.AddEntryTransition(b); // shadowed by the earlier unconditional entry

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: true, pathRewrite: null);

        Assert.AreEqual(1, r.UndeclaredParam, "the single undeclared condition parameter must be counted");
        Assert.GreaterOrEqual(r.EntryShadow, 1, "the second entry transition is shadowed by the earlier unconditional entry");
        Assert.AreEqual(0, r.BrokenBinding, "roots empty ⇒ the broken-binding rule is skipped (no basis root)");
    }

    [Test]
    public void Run_Flags_NonFloat_BlendParameter_As_Error()
    {
        // The measured trap: a blend tree evaluates only Float parameters — a name-matched Int reads 0
        // silently and the tree never leaves its zero branch. Declared-Int blend param must FAIL; a
        // declared-Float one must not; an UNDECLARED one belongs to undeclaredParam, not this rule.
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
    public void Run_Does_Not_Flag_ExitTime_From_Motionless_State()
    {
        // A motionless state with an exit-time transition is a VALID timer idiom, not a dead transition:
        // an empty state has a default 1s length and its normalizedTime advances in real time, so the
        // transition fires on schedule (proven by manual Animator.Update; shipped VRCFury Action-layer timer
        // states rely on it). The rule must leave it alone — neither error nor advisory.
        _controller = new AnimatorController();
        _controller.AddLayer("Base");
        var sm = _controller.layers[0].stateMachine;
        var a = sm.AddState("A");   // no motion assigned → motionless, but exit-time still advances
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
    // A parameter curve writes only Float parameters: measured, a clip curve on a Bool or Int leaves the
    // parameter at its declared default while still BINDING it — which hands the parameter to the animation
    // system and locks out every other writer (menu, contact, driver, script). See docs/runtime.md.

    /// <summary>A controller whose single state plays a clip carrying one animator-parameter curve on
    /// <paramref name="param"/>, declared as <paramref name="type"/>.</summary>
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
        // The legal AAP idiom, and the reason the parameter-curve surface exists at all. If this fires, the
        // rule is refusing the construct it exists to protect.
        _controller = WithParamCurve("Smoothed", AnimatorControllerParameterType.Float);

        var r = ControllerRules.Run(_controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);

        Assert.AreEqual(0, r.NonFloatParamCurve, "a Float parameter curve is the legal AAP idiom");
        Assert.IsFalse(r.Errors.Any(o => o.Kind == "nonFloatParamCurve"));
    }

    [Test]
    public void NonFloatParamCurve_Ignores_Humanoid_Muscle_And_Tdof_Curves()
    {
        // Muscle and TDOF curves use the SAME path-less Animator binding shape as a parameter curve. The
        // discriminator is that their property names no declared parameter, so a rig full of them stays
        // silent with no muscle allowlist. A Bool parameter is declared alongside to prove the rule was
        // armed and simply found nothing to say.
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
        // Two clips writing one offending parameter is a single defect to fix — the first-site-per-parameter
        // convention Rule 2 already uses.
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
}
