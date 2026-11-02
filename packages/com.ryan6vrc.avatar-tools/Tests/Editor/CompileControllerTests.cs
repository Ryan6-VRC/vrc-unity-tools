using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

public class CompileControllerTests
{
    private const string TestRoot = "Assets/Agent/Scratch/cc_tests";
    private string _srcPath;

    [SetUp]
    public void SetUp()
    {
        AnimatorTestHelpers.EnsureFolder(TestRoot);
        _srcPath = TestRoot + "/Debounce_Fx.yaml";
        File.WriteAllText(_srcPath, AnimatorSchemaYamlTests.DebounceDoc);
    }

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
    }

    [Test]
    public void WhatIf_Writes_Nothing_To_OutDir()
    {
        string outDir2 = TestRoot + "/out2";
        string result = CompileController.Run(_srcPath, outDir2, whatIf: true);

        StringAssert.Contains("=> OK (whatIf)", result);
        Assert.IsFalse(File.Exists(outDir2 + "/Debounce_Fx.controller"), "whatIf leaves no asset in outDir");
    }

    [Test]
    public void Validation_Failure_Writes_No_Controller()
    {
        string badSrc = TestRoot + "/Bad.yaml";
        File.WriteAllText(badSrc, AnimatorSchemaYamlTests.DebounceDoc.Replace("schema: 1", "schema: 2"));

        string outDir = TestRoot + "/out_bad";
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*validation failed.*=> FAIL"));
        string result = CompileController.Run(badSrc, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result);
        Assert.IsFalse(File.Exists(outDir + "/Debounce_Fx.controller"), "no controller on validation failure");
        StringAssert.Contains("| log=", result, "a refusal carries the in-band artifact trailer (R4)");
        string artifact = result.Substring(result.IndexOf("log=") + 4);
        Assert.IsTrue(File.Exists(artifact), "the refusal artifact is on disk: " + artifact);
        StringAssert.Contains("validation failed", File.ReadAllText(artifact), "the artifact names the reason");
        AssetDatabase.DeleteAsset(artifact);
    }

    [Test]
    public void NonFloatParamCurve_Fails_The_Compile_Door()
    {
        // ControllerRulesTests pins the detection; this pins the wiring. Every compile/lint verdict site
        // enumerates the error counters by name rather than testing Errors.Count, so a new rule that only
        // populates Errors would let the door report OK.
        //
        // The bad document must trip this rule and nothing else: a second firing rule would keep the verdict
        // FAIL, and the offender string is built from lint.Errors regardless of wiring, so both assertions
        // would still pass with this rule's counter unwired. No driver touches Level, so driverOnAnimatedParam
        // stays silent.
        string badSrc = TestRoot + "/BoolCurve.yaml";
        File.WriteAllText(badSrc, AnimatorSchemaYamlTests.DebounceDoc
            .Replace("Level: { type: float, aap: true }", "Level: bool"));

        string outDir = TestRoot + "/out_boolcurve";
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*post-emit graph lint.*=> FAIL"));
        string result = CompileController.Run(badSrc, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result, "a bool parameter curve must fail the compile, not warn");
        StringAssert.Contains("nonFloatParamCurve", result, "the refusal names the rule");
        Assert.IsFalse(File.Exists(outDir + "/Debounce_Fx.controller"), "nothing persists on a lint refusal");
    }

    [Test]
    public void DriverOnAnimatedParam_Fails_The_Compile_Door()
    {
        // Wiring test, as above: pointing the debounce driver at `Level`, which `hold_on` animates as a
        // parameter curve, makes the driver write dead.
        string badSrc = TestRoot + "/DriverOnAap.yaml";
        File.WriteAllText(badSrc, AnimatorSchemaYamlTests.DebounceDoc
            .Replace("- driver: { set: { Debounced: 1 } }", "- driver: { set: { Level: 1.0 } }"));

        string outDir = TestRoot + "/out_driveraap";
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*post-emit graph lint.*=> FAIL"));
        string result = CompileController.Run(badSrc, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result, "a driver op on a clip-bound param must fail the compile");
        StringAssert.Contains("driverOnAnimatedParam", result, "the refusal names the rule");
        Assert.IsFalse(File.Exists(outDir + "/Debounce_Fx.controller"), "nothing persists on a lint refusal");
    }

    [Test]
    public void Failing_Recompile_Leaves_Prior_Controller_Intact()
    {
        string outDir = TestRoot + "/out_proof";
        string path = outDir + "/Debounce_Fx.controller";

        CompileController.Run(_srcPath, outDir, whatIf: false);
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
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*emit:.*=> FAIL"));
        string result = CompileController.Run(badSrc, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result);
        Assert.IsTrue(File.Exists(path), "prior controller survives a failing recompile");
        Assert.AreEqual(guid1, AssetDatabase.AssetPathToGUID(path), "same GUID — not deleted + recreated");
        var after = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        Assert.IsNotNull(after, "prior controller still loads");
        Assert.AreEqual(layersBefore, after.layers.Length, "prior controller was NOT stripped by the failed recompile");
        AnimatorTestHelpers.DeleteRefusalArtifact(result);
    }

    [Test]
    public void Recompile_Keeps_Controller_Guid_Stable()
    {
        string outDir = TestRoot + "/out_idem";
        string path = outDir + "/Debounce_Fx.controller";

        CompileController.Run(_srcPath, outDir, whatIf: false);
        string guid1 = AssetDatabase.AssetPathToGUID(path);
        Assert.IsTrue(File.Exists(path), "controller written to disk");

        CompileController.Run(_srcPath, outDir, whatIf: false);
        string guid2 = AssetDatabase.AssetPathToGUID(path);

        Assert.IsNotEmpty(guid1, "controller has a GUID after the first compile");
        Assert.AreEqual(guid1, guid2, "recompile reuses the controller asset (stable GUID)");
    }

    // An `unresolved: true` motion ref emits a null motion, the clean-empty idiom every downstream lint is
    // whitelisted against, so the rebuilt controller reports missingMotion=0 where its source reported the
    // break. The verdict carries the drop instead.
    [Test]
    public void Compile_With_Unresolved_Ref_Classifies_And_Advises()
    {
        string src = TestRoot + "/Dangle_Fx.yaml";
        File.WriteAllText(src,
            "schema: 1\ncontroller: Dangle_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n" +
            "      S: { motion: { ref: { guid: \"00000000000000000000000000000000\", unresolved: true } } }\n" +
            "    default: S\n");

        string outDir = TestRoot + "/out_unresolved";
        string result = CompileController.Run(src, outDir, whatIf: false);

        StringAssert.Contains("=> CLASSIFY", result);
        StringAssert.Contains("unresolvedRefs=1", result);
        StringAssert.DoesNotContain("=> OK", result, "an OK here would certify the dropped motion");

        const string marker = "| log=";
        int i = result.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "result carries the RunLog path in-band");
        string logPath = result.Substring(i + marker.Length).Trim();
        Assert.IsTrue(File.Exists(logPath), "RunLog file exists at " + logPath);

        string body = File.ReadAllText(logPath);
        StringAssert.Contains("unresolved motion ref", body, "body carries the unresolved-ref advisory");
        StringAssert.Contains("00000000000000000000000000000000", body, "advisory names the GUID");
        StringAssert.Contains("`S`", body, "advisory names the state");
    }

    // ── OSC-unsafe parameter names advisory ────────────────────────────────────────────────────────
    private const string OscHeader = "## Compile advisory: OSC-unsafe parameter names";

    // The OSC-unsafe-names section is rendered last in the RunLog body, so header-to-end is the whole section.
    private string OscAdvisorySection(string yaml, string leaf)
    {
        string src = TestRoot + "/" + leaf + ".yaml";
        File.WriteAllText(src, yaml);
        string result = CompileController.Run(src, TestRoot + "/out_" + leaf, whatIf: false);
        StringAssert.Contains("=> OK", result);

        const string marker = "| log=";
        int i = result.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "result carries the RunLog path in-band");
        string body = File.ReadAllText(result.Substring(i + marker.Length).Trim());
        int h = body.IndexOf(OscHeader, System.StringComparison.Ordinal);
        Assert.Greater(h, -1, "RunLog body has the OSC-unsafe-names section");
        return body.Substring(h);
    }

    [Test]
    public void Osc_Advisory_Lists_Unsafe_Names_By_Class()
    {
        string section = OscAdvisorySection(
            "schema: 1\ncontroller: Osc_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  Hair ribbon: bool\n  Fan#1: bool\n  Clean: bool\n" +
            "layers:\n  - name: L\n    states:\n      S: { motion: ~ }\n    default: S\n",
            "spaced");

        StringAssert.Contains("`Hair ribbon`", section, "the spaced param is named (backtick-quoted so the space shows)");
        StringAssert.Contains("contains a space", section, "the space character class is stated");
        StringAssert.Contains("`Fan#1`", section, "the metacharacter param is named");
        StringAssert.Contains("OSC pattern metacharacter", section, "the metacharacter class is stated");
        StringAssert.DoesNotContain("`Clean`", section, "an OSC-safe param is not listed");
    }

    [Test]
    public void Osc_Advisory_Skips_Scratch_Param()
    {
        string section = OscAdvisorySection(
            "schema: 1\ncontroller: Osc_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  Scratch#tmp: { type: bool, scratch: true }\n" +
            "layers:\n  - name: L\n    states:\n      S: { motion: ~ }\n    default: S\n",
            "scratch");

        StringAssert.DoesNotContain("Scratch#tmp", section, "a scratch param is animator-internal — never on the OSC surface, so not advised");
        // Same oscUnsafeNames.Count==0 path a fully clean document takes, so the none-found form needs no
        // second test of its own.
        StringAssert.Contains("_(none)_", section, "with only a scratch offender the section renders the none-found form");
    }
}
