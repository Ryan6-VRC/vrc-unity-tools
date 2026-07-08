using System.IO;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;

// Behavioral tests for the CompileController door. These touch the filesystem (source YAML) and the
// AssetDatabase (the emitted .controller). NOT run via MCP run_tests (it crashes the editor); run from the
// Unity Test Runner window or CI. SetUp writes the debounce source; TearDown removes the whole test tree.
public class CompileControllerTests
{
    private const string TestRoot = "Assets/Agent/Scratch/cc_tests";
    private string _srcPath;

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(TestRoot);
        _srcPath = TestRoot + "/Debounce_Fx.yaml";
        File.WriteAllText(_srcPath, AnimatorSchemaYamlTests.DebounceDoc);
        AssetDatabase.Refresh(); // register TestRoot as a valid asset folder so EnsureFolder can nest under it
    }

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
        AssetDatabase.Refresh();
    }

    [Test]
    public void Compile_Writes_Controller_And_Returns_OK()
    {
        string outDir = TestRoot + "/out1";
        string result = CompileController.Compile(_srcPath, outDir, whatIf: false);

        StringAssert.Contains("=> OK", result);
        Assert.IsTrue(File.Exists(outDir + "/Debounce_Fx.controller"), "controller written to disk");
    }

    [Test]
    public void Compiled_Controller_Passes_AnimatorLint()
    {
        string outDir = TestRoot + "/out_lint";
        CompileController.Compile(_srcPath, outDir, whatIf: false);

        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(outDir + "/Debounce_Fx.controller");
        Assert.IsNotNull(ctrl, "compiled controller loads");

        string lint = AnimatorLint.Lint(ctrl, "explicit", null, null, null);
        StringAssert.Contains("=> PASS", lint);
    }

    [Test]
    public void WhatIf_Writes_Nothing_To_OutDir()
    {
        string outDir2 = TestRoot + "/out2";
        string result = CompileController.Compile(_srcPath, outDir2, whatIf: true);

        StringAssert.Contains("=> OK (whatIf)", result);
        Assert.IsFalse(File.Exists(outDir2 + "/Debounce_Fx.controller"), "whatIf leaves no asset in outDir");
    }

    [Test]
    public void Validation_Failure_Writes_No_Controller()
    {
        string badSrc = TestRoot + "/Bad.yaml";
        File.WriteAllText(badSrc, AnimatorSchemaYamlTests.DebounceDoc.Replace("schema: 1", "schema: 2"));

        string outDir = TestRoot + "/out_bad";
        string result = CompileController.Compile(badSrc, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result);
        Assert.IsFalse(File.Exists(outDir + "/Debounce_Fx.controller"), "no controller on validation failure");
    }

    [Test]
    public void Failing_Recompile_Leaves_Prior_Controller_Intact()
    {
        string outDir = TestRoot + "/out_proof";
        string path = outDir + "/Debounce_Fx.controller";

        // A good compile first.
        CompileController.Compile(_srcPath, outDir, whatIf: false);
        string guid1 = AssetDatabase.AssetPathToGUID(path);
        int layersBefore = AssetDatabase.LoadAssetAtPath<AnimatorController>(path).layers.Length;
        Assert.IsNotEmpty(guid1);
        Assert.Greater(layersBefore, 0, "the good controller has layers");

        // Recompile the SAME controller from a source that parses + validates but fails emit AFTER the
        // in-place strip (a transition to a nonexistent state is a fail-loud emit path, not caught by
        // validation). Without the proof-compile this would leave the prior controller stripped/empty.
        string badSrc = TestRoot + "/BadTarget.yaml";
        File.WriteAllText(badSrc,
            "schema: 1\ncontroller: Debounce_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: float }\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        transitions:\n          - { to: NoSuchState }\n    default: S\n");
        string result = CompileController.Compile(badSrc, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result);
        Assert.IsTrue(File.Exists(path), "prior controller survives a failing recompile");
        Assert.AreEqual(guid1, AssetDatabase.AssetPathToGUID(path), "same GUID — not deleted + recreated");
        var after = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        Assert.IsNotNull(after, "prior controller still loads");
        Assert.AreEqual(layersBefore, after.layers.Length, "prior controller was NOT stripped by the failed recompile");
    }

    [Test]
    public void Recompile_Keeps_Controller_Guid_Stable()
    {
        string outDir = TestRoot + "/out_idem";
        string path = outDir + "/Debounce_Fx.controller";

        CompileController.Compile(_srcPath, outDir, whatIf: false);
        string guid1 = AssetDatabase.AssetPathToGUID(path);

        CompileController.Compile(_srcPath, outDir, whatIf: false);
        string guid2 = AssetDatabase.AssetPathToGUID(path);

        Assert.IsNotEmpty(guid1, "controller has a GUID after the first compile");
        Assert.AreEqual(guid1, guid2, "recompile reuses the controller asset (stable GUID)");
    }
}
