using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

// Behavioral tests for the DecompileController door — the AGENT-FACING read door that ties
// ControllerDecompile.Walk + AnimatorSchemaEmit.Serialize together. These touch the filesystem (the emitted
// .yaml) and the AssetDatabase (the emitted/loaded .controller). NOT run via MCP run_tests (it crashes the
// editor); run from the Unity Test Runner window or CI. TearDown removes the whole test tree each run.
public class DecompileControllerTests
{
    private const string TestRoot = "Assets/Agent/Scratch/dc_tests";

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(TestRoot);
        AssetDatabase.Refresh(); // register TestRoot as a valid asset folder so emit can nest under it
    }

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
        AssetDatabase.Refresh();
    }

    // A controller emitted from the debounce doc, decompiled to yaml, re-compiled, must lint PASS end-to-end.
    [Test]
    public void Decompile_Then_Recompile_Is_Ok()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        string yamlOut = TestRoot + "/roundtrip.yaml";
        string dec = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);
        StringAssert.Contains("=> OK", dec);
        Assert.IsTrue(File.Exists(yamlOut), "the .yaml is written");

        string rec = CompileController.Compile(Path.GetFullPath(yamlOut), TestRoot + "/out_rt", whatIf: false);
        StringAssert.Contains("=> OK", rec);

        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(TestRoot + "/out_rt/Debounce_Fx.controller");
        Assert.IsNotNull(ctrl, "recompiled controller loads");
        StringAssert.Contains("=> PASS", AnimatorLint.Lint(ctrl, "explicit", null, null, null));
    }

    // whatIf computes everything, writes NO .yaml, and appends " (whatIf)".
    [Test]
    public void WhatIf_Writes_No_Yaml()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        string yamlOut = TestRoot + "/whatif.yaml";
        string dec = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: true);
        StringAssert.Contains("=> OK (whatIf)", dec);
        Assert.IsFalse(File.Exists(yamlOut), "whatIf leaves no .yaml on disk");
    }

    // The emitted .yaml carries a top-level _notes: block with orphan / unresolved / tolerance content.
    [Test]
    public void Decompile_Yaml_Carries_Notes_Block()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, TestRoot + "/emit", "src", out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        string yamlOut = TestRoot + "/notes.yaml";
        DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);
        Assert.IsTrue(File.Exists(yamlOut));

        string yaml = File.ReadAllText(yamlOut);
        StringAssert.Contains("_notes:", yaml, "the reserved notes block is present");
        StringAssert.Contains("orphans", yaml, "notes carry the orphan count");
        StringAssert.Contains("unresolved", yaml, "notes carry the unresolved list");
        StringAssert.Contains("tolerances", yaml, "notes carry the tolerances list");

        // The notes block re-parses inertly (parser skips _-prefixed top-level keys) — the yaml still compiles.
        string rec = CompileController.Compile(Path.GetFullPath(yamlOut), TestRoot + "/out_notes", whatIf: false);
        StringAssert.Contains("=> OK", rec);
    }

    // A controller carrying an out-of-vocabulary construct (a Trigger parameter) -> bare FAIL, no .yaml.
    [Test]
    public void Decompile_Refusal_Fails_And_Writes_No_Yaml()
    {
        string refusingCtrlPath = TestRoot + "/Refuse_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(refusingCtrlPath);
        rc.AddParameter("T", AnimatorControllerParameterType.Trigger); // out of vocabulary
        AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
        string yamlOut = TestRoot + "/refuse.yaml";
        string res = DecompileController.Decompile(refusingCtrlPath, yamlOut, whatIf: false);

        StringAssert.Contains("FAIL", res);
        StringAssert.Contains("Trigger", res, "the refusal names the offending construct");
        Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
    }

    [Test]
    public void Empty_ControllerPath_Fails()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
        string res = DecompileController.Decompile("", TestRoot + "/x.yaml", whatIf: false);
        StringAssert.Contains("FAIL", res);
    }

    [Test]
    public void Nonexistent_Controller_Fails()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
        string res = DecompileController.Decompile(TestRoot + "/nope.controller", TestRoot + "/x.yaml", whatIf: false);
        StringAssert.Contains("FAIL", res);
    }
}
