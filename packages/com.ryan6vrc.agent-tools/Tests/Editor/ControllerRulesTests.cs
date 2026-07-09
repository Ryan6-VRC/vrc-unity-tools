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
}
