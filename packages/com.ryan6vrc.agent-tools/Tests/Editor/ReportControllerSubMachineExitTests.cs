using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// A sub-machine's OUTGOING (on-Exit) edges are the only way out of that sub-machine, and the digest walked
// AnyState, Entry, and each state's ladder while never reading GetStateMachineTransitions. So a layer built
// around sub-machines rendered a machine with no exit and gave the reader no sign control flow was omitted —
// a silent gap, not a loud one, in the door an agent uses instead of opening the Animator window. Measured at
// 3 of 6 controllers in a real vendor prop package, which is also why the schema grew `onExit:`.
//
// These pin both that the edges appear and that their CONDITIONS and TARGET do, since a rendered edge with no
// condition is indistinguishable from an unconditional one — the difference between "always leaves" and
// "leaves when g".
public class ReportControllerSubMachineExitTests
{
    private const string Dir = "Assets/Agent/_smexit_test";
    private AnimatorController _ctrl;

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(Dir);
        _ctrl = AnimatorController.CreateAnimatorControllerAtPath(Dir + "/smexit.controller");
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(Dir);

    private static string ReadReport(AnimatorController c)
    {
        string summary = ReportController.Report(c);
        int i = summary.IndexOf("log=");
        return i >= 0 ? File.ReadAllText(summary.Substring(i + 4).Trim()) : summary;
    }

    [Test]
    public void TargetedOutgoingEdge_IsRenderedWithItsConditionAndDestination()
    {
        _ctrl.AddParameter("g", AnimatorControllerParameterType.Bool);
        var root = _ctrl.layers[0].stateMachine;
        var sub = root.AddStateMachine("Sub");
        sub.AddState("Inner");
        var dst = root.AddState("Dst");
        var t = root.AddStateMachineTransition(sub, dst);
        t.AddCondition(AnimatorConditionMode.If, 0, "g");

        string report = ReadReport(_ctrl);

        StringAssert.Contains("`Sub` (state machine) onExit", report);
        StringAssert.Contains("Dst", report);
        StringAssert.Contains("g", report);
    }

    [Test]
    public void ExitEdge_IsRenderedRatherThanOmitted()
    {
        var root = _ctrl.layers[0].stateMachine;
        var sub = root.AddStateMachine("Sub");
        sub.AddState("Inner");
        root.AddStateMachineExitTransition(sub);   // unconditional, straight up to the parent's Exit

        string report = ReadReport(_ctrl);

        StringAssert.Contains("`Sub` (state machine) onExit", report);
        StringAssert.Contains("Exit", report);
    }

    // A sub-machine with no outgoing edges must not acquire an onExit line — the row is evidence, so a
    // fabricated one would read as an exit path that does not exist.
    [Test]
    public void SubMachineWithNoOutgoingEdges_RendersNoOnExitLine()
    {
        var root = _ctrl.layers[0].stateMachine;
        root.AddStateMachine("Sub").AddState("Inner");

        string report = ReadReport(_ctrl);

        StringAssert.DoesNotContain("onExit", report);
    }
}
