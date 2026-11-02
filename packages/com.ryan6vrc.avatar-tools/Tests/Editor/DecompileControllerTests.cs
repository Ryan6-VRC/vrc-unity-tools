using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

public class DecompileControllerTests
{
    private const string TestRoot = "Assets/Agent/Scratch/dc_tests";

    // Every door call here writes an artifact outside TestRoot: Decompile a Snapshot markdown (success AND refusal),
    // Compile/Lint a RunLog. Snapshots are durable (CLAUDE.md §Layout), so record the path each call named in its
    // `| log=` trailer and delete exactly those; a glob over `decompilecontroller_*.md` would also sweep snapshots
    // the operator took by hand. Track is the fixture's only artifact owner, so a refusal test asserts its artifact
    // is on disk and leaves it there.
    private static readonly List<string> Artifacts = new List<string>();

    [OneTimeTearDown]
    public void DeleteWrittenArtifacts()
    {
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), new List<string>());
        Artifacts.Clear();
    }

    // Every door call goes through here, even a summary-only one: it wrote its artifact too.
    private static string Track(string summary)
    {
        int i = summary.IndexOf("log=", System.StringComparison.Ordinal);
        if (i >= 0) Artifacts.Add(summary.Substring(i + 4).Trim());
        return summary;
    }

    private readonly AnimatorTestHelpers.UnownedSideAssetSweep _paramSweep =
        new AnimatorTestHelpers.UnownedSideAssetSweep();

    [SetUp]
    public void SetUp()
    {
        _paramSweep.Begin();
        AnimatorTestHelpers.EnsureFolder(TestRoot);
    }

    [TearDown]
    public void TearDown()
    {
        _paramSweep.End();
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
    }

    [Test]
    public void Decompile_Then_Recompile_Is_Ok()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        string yamlOut = TestRoot + "/roundtrip.yaml";
        string dec = Track(DecompileController.Run(ctrlPath, yamlOut, whatIf: false));
        StringAssert.Contains("=> OK", dec);
        Assert.IsTrue(File.Exists(yamlOut), "the .yaml is written");

        const string marker = "| log=";
        int i = dec.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "the OK line carries the RunLog path in-band");
        string logPath = dec.Substring(i + marker.Length).Trim();
        Assert.IsTrue(File.Exists(logPath), "the Snapshot RunLog exists at " + logPath);

        string rec = Track(CompileController.Run(Path.GetFullPath(yamlOut), TestRoot + "/out_rt", whatIf: false));
        StringAssert.Contains("=> OK", rec);

        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(TestRoot + "/out_rt/Debounce_Fx.controller");
        Assert.IsNotNull(ctrl, "recompiled controller loads");
        StringAssert.Contains("=> PASS", Track(CheckAnimator.Run(ctrl, "explicit", null, null, null)));
    }

    [Test]
    public void WhatIf_Writes_No_Yaml()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        string yamlOut = TestRoot + "/whatif.yaml";
        string dec = Track(DecompileController.Run(ctrlPath, yamlOut, whatIf: true));
        StringAssert.Contains("=> OK (whatIf)", dec);
        Assert.IsFalse(File.Exists(yamlOut), "whatIf leaves no .yaml on disk");
    }

    [Test]
    public void Decompile_Yaml_Carries_Notes_Block()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        string yamlOut = TestRoot + "/notes.yaml";
        Track(DecompileController.Run(ctrlPath, yamlOut, whatIf: false));
        Assert.IsTrue(File.Exists(yamlOut));

        string yaml = File.ReadAllText(yamlOut);
        StringAssert.Contains("_notes:", yaml, "the reserved notes block is present");
        StringAssert.Contains("orphans", yaml, "notes carry the orphan count");
        StringAssert.Contains("unresolved", yaml, "notes carry the unresolved list");
        StringAssert.Contains("tolerances", yaml, "notes carry the tolerances list");

        // The parser sets _-prefixed top-level keys aside, so the yaml still compiles.
        string rec = Track(CompileController.Run(Path.GetFullPath(yamlOut), TestRoot + "/out_notes", whatIf: false));
        StringAssert.Contains("=> OK", rec);
    }

    [Test]
    public void Decompile_Refusal_Fails_And_Writes_No_Yaml()
    {
        string refusingCtrlPath = TestRoot + "/Refuse_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(refusingCtrlPath);
        rc.AddParameter("T", AnimatorControllerParameterType.Trigger); // out of vocabulary
        AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] .*=> FAIL"));
        string yamlOut = TestRoot + "/refuse.yaml";
        string res = Track(DecompileController.Run(refusingCtrlPath, yamlOut, whatIf: false));

        StringAssert.Contains("FAIL", res);
        StringAssert.Contains("Trigger", res, "the refusal names the offending construct");
        Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
        StringAssert.Contains("| log=", res, "a refusal carries the in-band artifact trailer (R4)");
        string artifact = res.Substring(res.IndexOf("log=") + 4);
        Assert.IsTrue(File.Exists(artifact), "the refusal artifact is on disk: " + artifact);
    }

    // Both directions are asserted on ONE staged arrangement: without the default decompile carrying the dragged
    // coordinate, a decompiler that captured no layout at all would pass the flag's test.
    [Test]
    public void StripLayout_Writes_Yaml_Without_Layout()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
        var sm = emitted.Controller.layers[0].stateMachine;
        var arr = sm.states;
        arr[0].position = new Vector3(777, 888, 0); // arrange off-grid so a default decompile WOULD emit layout
        sm.states = arr;
        EditorUtility.SetDirty(sm);
        AssetDatabase.SaveAssets();
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        string defaultOut = TestRoot + "/arranged.yaml";
        StringAssert.Contains("=> OK", Track(DecompileController.Run(ctrlPath, defaultOut, whatIf: false)));
        string arrangedYaml = File.ReadAllText(defaultOut);
        StringAssert.Contains("layout:", arrangedYaml, "an arranged controller decompiles WITH a layout block");
        StringAssert.Contains("[777, 888]", arrangedYaml, "and the block carries the dragged coordinate");

        string yamlOut = TestRoot + "/stripped.yaml";
        string dec = Track(DecompileController.Run(ctrlPath, yamlOut, whatIf: false, stripLayout: true));
        StringAssert.Contains("=> OK", dec);
        Assert.IsTrue(File.Exists(yamlOut), "the .yaml is written");
        Assert.IsFalse(File.ReadAllText(yamlOut).Contains("layout:"), "stripLayout writes no layout block");
    }

    // One case per refusal REASON: asserting only "FAIL" would pass for a door that refuses everything. The outPath
    // case uses a REAL controller so it proves the outPath guard whatever the guard order.
    [TestCase("", "/x.yaml", "controllerPath is empty")]
    [TestCase("REAL", "", "outPath is empty")]
    // The wording is ReportController.RefuseWhy's, shared with the agent-tools asset doors.
    [TestCase("/nope.controller", "/x.yaml", "no AnimatorController at")]
    public void Arg_Guard_Failures_Name_Their_Reason(string ctrlSpec, string outSpec, string reason)
    {
        string ctrlPath;
        if (ctrlSpec == "REAL")
        {
            var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
            ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
            ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);
        }
        else ctrlPath = ctrlSpec.Length == 0 ? "" : TestRoot + ctrlSpec;

        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] .*=> FAIL"));
        string res = Track(DecompileController.Run(ctrlPath, outSpec.Length == 0 ? "" : TestRoot + outSpec, whatIf: false));

        StringAssert.Contains("FAIL", res);
        StringAssert.Contains(reason, res, "the refusal names its own reason, not just FAIL");
    }
}
