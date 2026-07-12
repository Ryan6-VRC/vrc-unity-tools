using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using Ryan6Vrc.AgentTools.Editor;

// Widening coverage: Unity IConstraint components must land in ReportGimmick's constraint edge-list with
// their per-type Axis-flag mask and NO false READ-MISS note, and the geometric feedback-loop observation
// must transfer to them — without routing them through the VRC-only reflection helpers. VRC-idiom
// regression (world anchor / hold / TargetTransform notes) is covered by the coordinator's live-corpus run,
// not fabricated here (headless VRC-constraint fixtures are awkward and low-value).
public class ReportGimmickWidenTests
{
    [SetUp]
    public void SetUp() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

    private static string ReadReport(string rootPath)
    {
        string summary = ReportGimmick.Report(rootPath);
        int i = summary.IndexOf("log=");
        return i >= 0 ? File.ReadAllText(summary.Substring(i + 4).Trim()) : summary;
    }

    [Test]
    public void ParentConstraint_AppearsInEdgeList_WithAxisMask_NoReadMiss()
    {
        var root = new GameObject("Rig");
        var driven = new GameObject("Driven"); driven.transform.SetParent(root.transform);
        var src = new GameObject("Src"); src.transform.SetParent(root.transform);
        var pc = driven.AddComponent<ParentConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });

        string report = ReadReport("Rig");
        StringAssert.Contains("ParentConstraint", report);
        StringAssert.Contains("Rig/Src", report);
        // Unity per-type Axis-flag mask rendered (VRC path would emit "pos*"/"—", never the colon form).
        StringAssert.Contains("pos:", report);
        StringAssert.Contains("rot:", report);
        // The false-READ-MISS this task exists to prevent: a Unity constraint routed through the VRC
        // reflection helpers would resolve zero groups and emit this note.
        StringAssert.DoesNotContain("could not read the affected-axis mask", report);
        // VRC-only idiom notes must NOT attach to a Unity constraint.
        StringAssert.DoesNotContain("TargetTransform indirection", report);
    }

    [Test]
    public void UnityConstraint_FeedbackLoop_TransfersToObservations()
    {
        var root = new GameObject("Rig");
        var driven = new GameObject("Driven"); driven.transform.SetParent(root.transform);
        // Source is a strict descendant of the driven host — the feedback-loop idiom.
        var src = new GameObject("Inner"); src.transform.SetParent(driven.transform);
        var pc = driven.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });

        string report = ReadReport("Rig");
        StringAssert.Contains("feedback loop", report);
    }
}
