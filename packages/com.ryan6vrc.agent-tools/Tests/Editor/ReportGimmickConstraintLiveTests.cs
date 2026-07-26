using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using Ryan6Vrc.AgentTools.Editor;
using VRC.SDK3.Dynamics.Constraint.Components;

// Each constraint family carries a second enable flag beyond the Behaviour's own — VRC `IsActive`,
// Unity `constraintActive` — so an inert constraint used to render identically to a running one.
// The live cell names WHICH flag is down, because that is what the reader acts on.
public class ReportGimmickConstraintLiveTests
{
    [SetUp]
    public void SetUp() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

    private static string ReadReport(string rootPath)
    {
        string summary = ReportGimmick.Report(rootPath);
        int i = summary.IndexOf("log=");
        return i >= 0 ? File.ReadAllText(summary.Substring(i + 4).Trim()) : summary;
    }

    private static GameObject Rig(out GameObject host, out GameObject src)
    {
        var root = new GameObject("Rig");
        host = new GameObject("Driven"); host.transform.SetParent(root.transform);
        src = new GameObject("Src"); src.transform.SetParent(root.transform);
        return root;
    }

    [Test]
    public void VrcConstraint_IsActiveFalse_LiveCellNamesTheFlag()
    {
        GameObject host, src;
        Rig(out host, out src);
        var con = host.AddComponent<VRCParentConstraint>();
        con.IsActive = false;   // Behaviour stays enabled, object stays active

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (IsActive)", report);
    }

    [Test]
    public void VrcConstraint_Running_LiveCellIsOne()
    {
        GameObject host, src;
        Rig(out host, out src);
        var con = host.AddComponent<VRCParentConstraint>();
        con.IsActive = true;

        string report = ReadReport("Rig");
        StringAssert.Contains("VRCParentConstraint", report);
        StringAssert.DoesNotContain("0 (", report);
    }

    [Test]
    public void VrcConstraint_ComponentDisabled_ReportsEnabledNotIsActive()
    {
        GameObject host, src;
        Rig(out host, out src);
        var con = host.AddComponent<VRCParentConstraint>();
        con.IsActive = true;
        con.enabled = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (enabled)", report);
    }

    [Test]
    public void UnityConstraint_ConstraintActiveFalse_LiveCellNamesTheFlag()
    {
        GameObject host, src;
        Rig(out host, out src);
        var pc = host.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        pc.constraintActive = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (constraintActive)", report);
    }

    [Test]
    public void UnityConstraint_InactiveObject_ReportsObject()
    {
        GameObject host, src;
        Rig(out host, out src);
        var pc = host.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        pc.constraintActive = true;
        host.SetActive(false);

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (object)", report);
    }

    // Liveness is relative to the report root, matching the physbone path: parking the whole rig must not
    // relabel every constraint inert.
    [Test]
    public void Constraint_InactiveReportRoot_StaysLive()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var pc = host.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        pc.constraintActive = true;
        root.SetActive(false);

        string report = ReadReport("Rig");
        StringAssert.DoesNotContain("0 (", report);
    }
}
