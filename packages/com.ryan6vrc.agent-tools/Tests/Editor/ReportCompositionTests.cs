using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.ScriptableObjects;

/// <summary>
/// The two properties worth pinning here are the ones a reader's conclusions rest on: that the parameter
/// census reports READS and WRITES from the same traversals the lint rules use, and that an empty writers
/// cell says why it is empty. An empty cell that looks like a finding is the exact misread this door was
/// built to stop, so it is asserted rather than left to review.
///
/// The surface enumeration itself is not re-tested here: <c>CheckAvatar</c> was rewired onto the same
/// lifted core, so its whole existing fixture is the regression net for it, and duplicating that here
/// would be two homes for one assertion.
/// </summary>
public class ReportCompositionTests
{
    private readonly List<Object> _assets = new List<Object>();

    [SetUp]
    public void SetUp() => EditorSceneManagerNewScene();

    private static void EditorSceneManagerNewScene() =>
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

    [TearDown]
    public void TearDown()
    {
        foreach (var a in _assets)
        {
            var p = AssetDatabase.GetAssetPath(a);
            if (!string.IsNullOrEmpty(p)) AssetDatabase.DeleteAsset(p);
        }
        _assets.Clear();
    }

    // ── CollectParamUsage: the reads/writes split ────────────────────────────────────────────────────

    [Test]
    public void ParamUsage_splitsReadsFromWrites_acrossConditionsDriversAndBlendTrees()
    {
        var c = new AnimatorController();
        c.AddParameter("Gate", AnimatorControllerParameterType.Bool);
        c.AddParameter("Axis", AnimatorControllerParameterType.Float);
        c.AddParameter("Written", AnimatorControllerParameterType.Float);
        c.AddParameter("CopiedFrom", AnimatorControllerParameterType.Float);
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;

        var a = sm.AddState("A");
        var b = sm.AddState("B");
        var t = a.AddTransition(b);
        t.AddCondition(AnimatorConditionMode.If, 0, "Gate");

        var tree = new BlendTree { blendType = BlendTreeType.Simple1D, blendParameter = "Axis" };
        b.motion = tree;

        var drv = a.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
        drv.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
        {
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Written", type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set, value = 1f },
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "Written", source = "CopiedFrom", type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy },
        };

        var usage = ControllerRules.CollectParamUsage(c);

        Assert.IsTrue(usage.Reads.ContainsKey("Gate"), "a transition condition is a READ");
        Assert.IsTrue(usage.Reads.ContainsKey("Axis"), "a blend-tree axis is a READ");
        Assert.IsTrue(usage.Writes.ContainsKey("Written"), "a driver destination is a WRITE");
        // The direction that is easy to get backwards, and the one that decides whether a provenance table
        // says "nothing writes this": a Copy's SOURCE is read, never written.
        Assert.IsTrue(usage.Reads.ContainsKey("CopiedFrom"), "a driver Copy source is a READ");
        Assert.IsFalse(usage.Writes.ContainsKey("CopiedFrom"), "a driver Copy source must NOT be reported as a writer");
        Assert.IsFalse(usage.Reads.ContainsKey("Written"), "a Set destination is not a read");
    }

    [Test]
    public void ParamUsage_onANullController_isEmptyRatherThanThrowing()
    {
        var usage = ControllerRules.CollectParamUsage(null);
        Assert.AreEqual(0, usage.Reads.Count);
        Assert.AreEqual(0, usage.Writes.Count);
    }

    // ── The door's refusals ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void UnresolvableHandle_isABareFail_withNoArtifactTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[ReportComposition\] FAIL:"));
        var r = ReportComposition.Report("NoSuchRoot_xyz");
        StringAssert.StartsWith("[ReportComposition] FAIL:", r);
        Assert.IsFalse(r.Contains("| log="), "a refusal must not point at an artifact: " + r);
    }

    [Test]
    public void ARootWithNoDescriptor_refusesRatherThanReportingAnEmptyComposition()
    {
        new GameObject("Bare");
        LogAssert.Expect(LogType.Error, new Regex(@"\[ReportComposition\] FAIL:"));
        var r = ReportComposition.Report("Bare");
        StringAssert.Contains("has no VRCAvatarDescriptor", r);
    }

    // ── The rendering mandate: an empty cell must not read as a finding ──────────────────────────────

    [Test]
    public void AParameterNoScannedSurfaceWrites_carriesTheScopeNoteInTheCell()
    {
        BuildAvatar("ScopeAvatar", "Unwritten");
        var r = ReportComposition.Report("ScopeAvatar");
        StringAssert.Contains("=> OK", r);
        var body = ReadArtifact(r);
        StringAssert.Contains("`Unwritten`", body, "the declared parameter must appear: " + body);

        // Assert on the ROW, not on the document. Both constants are also printed unconditionally in the
        // trailing Scope section, so a whole-body Contains passes even when the cell is empty — which is
        // exactly the property this test exists to pin, and exactly what it failed to pin before.
        string row = RowFor(body, "Unwritten");
        StringAssert.Contains(ReportComposition.ScopeWriters, row,
            "the scope note must be IN the writers cell, not only in the footer: " + row);
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(row, @"\|\s*\|\s*\|"),
            "no cell in the row may be blank — a blank writers cell reads as a finding: " + row);
        StringAssert.Contains(ReportComposition.ScopeAuthoredNames, body,
            "plain mode must decline to claim the built names: " + body);
    }

    [Test]
    public void ParamFilter_narrowsTheTable_andSaysThatItDid()
    {
        BuildAvatar("FilterAvatar", "Keep/Me", "Drop/Me");
        var r = ReportComposition.Report("FilterAvatar", false, "Keep");
        var body = ReadArtifact(r);
        StringAssert.Contains("`Keep/Me`", body, body);
        Assert.IsFalse(body.Contains("| `Drop/Me` |"), "the filter must actually narrow the table: " + body);
        StringAssert.Contains("paramFilter:", body, "a narrowed table must say it is narrowed, or its counts lie: " + body);
    }

    [Test]
    public void TheTierTwoCensusNamesWhatNoTableRead_soZeroMeansEmpty()
    {
        var root = BuildAvatar("CensusAvatar", "P");
        root.AddComponent<BoxCollider>(); // a component no table above interprets
        var r = ReportComposition.Report("CensusAvatar");
        StringAssert.Contains("UnityEngine.BoxCollider", ReadArtifact(r),
            "an uninterpreted component must be censused, or other=0 means nobody looked");
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────────

    // Objects are left for the next test's NewScene(Single) to take, rather than DestroyImmediate'd —
    // docs/verify.md §Test venue forbids building-then-destroying live objects this fixture has mutated,
    // and the sibling CheckHumanoidRigAvatarTests already follows that rule.
    private GameObject BuildAvatar(string name, params string[] declaredParams)
    {
        var root = new GameObject(name);
        var d = root.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
        var ep = ScriptableObject.CreateInstance<VRCExpressionParameters>();
        var list = new List<VRCExpressionParameters.Parameter>();
        foreach (var p in declaredParams)
            list.Add(new VRCExpressionParameters.Parameter
            {
                name = p, valueType = VRCExpressionParameters.ValueType.Bool,
                defaultValue = 0f, saved = false, networkSynced = true,
            });
        ep.parameters = list.ToArray();
        string path = "Assets/" + name + "_Params.asset";
        AssetDatabase.CreateAsset(ep, path);
        _assets.Add(ep);
        d.expressionParameters = ep;
        return root;
    }

    /// <summary>The one table row whose first cell names <paramref name="param"/>. Asserting on the whole
    /// document cannot distinguish a cell from a footnote, and this door's contract is about the cell.</summary>
    private static string RowFor(string body, string param)
    {
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("| `" + param + "`", System.StringComparison.Ordinal)) return line;
        Assert.Fail("no table row for `" + param + "` in:\n" + body);
        return null;
    }

    private static string ReadArtifact(string summary)
    {
        const string marker = "| log=";
        int i = summary.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "expected an artifact trailer: " + summary);
        string p = summary.Substring(i + marker.Length).Trim();
        string full = Path.Combine(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length), p);
        Assert.IsTrue(File.Exists(full), "artifact missing at " + p);
        return File.ReadAllText(full);
    }
}
