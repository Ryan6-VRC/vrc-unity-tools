using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

public class CompileControllerRobustnessTests
{
    private const string TestRoot = "Assets/Agent/Scratch/ccrobust_tests";

    [SetUp]
    public void SetUp()
    {
        AnimatorTestHelpers.EnsureFolder(TestRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
    }

    private static string ReadRunLogBody(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "result carries the RunLog path in-band: " + result);
        string logPath = result.Substring(i + marker.Length).Trim();
        Assert.IsTrue(File.Exists(logPath), "RunLog file exists at " + logPath);
        return File.ReadAllText(logPath);
    }

    [Test]
    public void FrameLatency_Advisory_Skip_Is_Surfaced_For_Oversized_Layer()
    {
        var sb = new StringBuilder();
        sb.Append("schema: 1\ncontroller: Big_Fx\nbasis: avatar-root\nrole: fx\n");
        sb.Append("layers:\n  - name: Big\n    states:\n");
        for (int i = 0; i < 130; i++) // > 128 → trips the node-count guard
            sb.Append("      S").Append(i).Append(": { motion: ~ }\n");
        sb.Append("    default: S0\n");

        string src = TestRoot + "/Big_Fx.yaml";
        File.WriteAllText(src, sb.ToString());

        string outDir = TestRoot + "/out_big";
        string result = CompileController.Run(src, outDir, whatIf: false);

        StringAssert.Contains("=> OK", result); // oversized layer still compiles — advisory is not a failure
        string body = ReadRunLogBody(result);
        StringAssert.Contains("frame-latency advisory skipped", body,
            "an oversized layer must surface that its frame-latency advisory was skipped (fail-loud), not vanish");
        StringAssert.Contains("Big", body, "the skip note names the offending layer");
    }

    [Test]
    public void Recompile_Over_HandAuthored_Controller_Warns()
    {
        string outDir = TestRoot + "/out_handauth";
        AnimatorTestHelpers.EnsureFolder(outDir); // CreateAsset below needs a REGISTERED asset folder

        // A hand-authored controller sitting at the exact target path — created directly, so it carries
        // NO `compiled-from:` provenance userData.
        string path = outDir + "/Debounce_Fx.controller";
        var handAuthored = new AnimatorController { name = "Debounce_Fx" };
        AssetDatabase.CreateAsset(handAuthored, path);
        AssetDatabase.SaveAssets();
        Assert.IsTrue(File.Exists(path), "hand-authored controller staged at the target path");

        string src = TestRoot + "/Debounce_Fx.yaml";
        File.WriteAllText(src, AnimatorSchemaYamlTests.DebounceDoc);

        LogAssert.Expect(LogType.Warning,
            new Regex(@"\[CompileController\] overwriting .*Debounce_Fx\.controller.*provenance"));
        string result = CompileController.Run(src, outDir, whatIf: false);

        StringAssert.Contains("=> OK", result); // warn-only: the compile still succeeds
    }

    // ── Asserts on the .meta TEXT because AssetImporter.GetAtPath cannot testify to persistence at all
    // (StampProvenance owns why).
    [Test]
    public void Provenance_Stamp_Reaches_The_Meta_On_Disk_And_Survives_An_Idempotent_Recompile()
    {
        string outDir = TestRoot + "/out_stamp";
        string src = TestRoot + "/Debounce_Fx.yaml";
        File.WriteAllText(src, AnimatorSchemaYamlTests.DebounceDoc);

        StringAssert.Contains("=> OK", CompileController.Run(src, outDir, whatIf: false));
        string metaPath = outDir + "/Debounce_Fx.controller.meta";
        Assert.IsTrue(File.Exists(metaPath), "the compiled controller has a .meta");
        StringAssert.Contains("compiled-from:;srchash:", File.ReadAllText(metaPath),
            "the stamp reached the .meta ON DISK without a reimport — and carries the bare key, no path");

        // The idempotent recompile: an unchanged stamp never dirties the importer, so
        // WriteImportSettingsIfDirty returns false while the .meta is already correct. A guard that trusted
        // that return would fire a spurious failure right here.
        StringAssert.Contains("=> OK", CompileController.Run(src, outDir, whatIf: false));
        StringAssert.Contains("compiled-from:;srchash:", File.ReadAllText(metaPath),
            "the unchanged stamp is still on disk after a no-op recompile, and nothing reported a failure");
    }

    // ── Older compiles wrote `compiled-from:<path>;srchash:<hash>`, and those controllers are committed, so
    // WarnIfOutOfBand must keep reading that form.
    [Test]
    public void Legacy_Stamp_With_A_Path_Still_Parses_As_Provenance()
    {
        string outDir = TestRoot + "/out_legacy";
        string src = TestRoot + "/Debounce_Fx.yaml";
        File.WriteAllText(src, AnimatorSchemaYamlTests.DebounceDoc);
        StringAssert.Contains("=> OK", CompileController.Run(src, outDir, whatIf: false));

        // Restamp in the old shape, with a hash that cannot match the source, so a correct parse warns that the
        // srchash differs rather than that there is no provenance.
        var imp = AssetImporter.GetAtPath(outDir + "/Debounce_Fx.controller");
        imp.userData = "compiled-from:Packages/com.ryan6vrc.patterns/x/controller.yaml;srchash:deadbeefdeadbeef";
        imp.SaveAndReimport();

        LogAssert.Expect(LogType.Warning, new Regex(
            @"\[CompileController\] overwriting .*Debounce_Fx\.controller.*srchash=deadbeefdeadbeef differs"));
        StringAssert.Contains("=> OK", CompileController.Run(src, outDir, whatIf: false));
    }

    // ── UnityEngine.UI.Image is outside ControllerEmit.BindingNamespaces, so a binding to it is refused
    // fail-loud rather than mis-emitted.
    [Test]
    public void Inline_Binding_To_Component_Outside_Allowlisted_Namespaces_Is_Refused()
    {
        string src = TestRoot + "/Ui_Fx.yaml";
        File.WriteAllText(src,
            "schema: 1\ncontroller: Ui_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S: { motion: { clip: hide } }\n    default: S\n" +
            "clips:\n  hide: { set: { \"Panel/Image.enabled\": 0 } }\n");

        string outDir = TestRoot + "/out_ui";
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*emit:.*Image.*=> FAIL"));
        string result = CompileController.Run(src, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result);
        StringAssert.Contains("UnityEngine", result,
            "the refusal names the supported-namespace scope limit so it reads as intentional");
        Assert.IsFalse(File.Exists(outDir + "/Ui_Fx.controller"), "no controller written on the refused binding");
        AnimatorTestHelpers.DeleteRefusalArtifact(result);
    }

    [Test]
    public void Failed_Fresh_Compile_Leaves_No_Empty_Folders()
    {
        string outDir = TestRoot + "/new_ancestor/deep"; // neither segment exists yet
        Assert.IsFalse(AssetDatabase.IsValidFolder(outDir), "precondition: outDir does not exist yet");

        // Parses + validates but fails emit AFTER folder creation (transition to a nonexistent state — a
        // fail-loud emit path, not caught by validation).
        string badSrc = TestRoot + "/BadFresh.yaml";
        File.WriteAllText(badSrc,
            "schema: 1\ncontroller: BadFresh_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        transitions:\n          - { to: NoSuchState }\n    default: S\n");

        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*emit:.*=> FAIL"));
        string result = CompileController.Run(badSrc, outDir, whatIf: false);

        StringAssert.Contains("FAIL", result);
        Assert.IsFalse(AssetDatabase.IsValidFolder(outDir), "freshly-created leaf folder removed on a failed compile");
        Assert.IsFalse(AssetDatabase.IsValidFolder(TestRoot + "/new_ancestor"), "freshly-created ancestor folder removed too");
        AnimatorTestHelpers.DeleteRefusalArtifact(result);
    }
    [Test]
    public void Compile_Persists_Its_Outputs_Without_Saving_Unrelated_Dirty_Assets()
    {
        var probe = new AnimatorTestHelpers.DirtyMaterialProbe(TestRoot, "Unrelated");
        string src = TestRoot + "/Scoped_Fx.yaml";
        File.WriteAllText(src, AnimatorSchemaYamlTests.DebounceDoc.Replace("Debounce_Fx", "Scoped_Fx"));

        string result = CompileController.Run(src, TestRoot + "/scoped_out", whatIf: false);

        StringAssert.Contains("=> OK", result);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<AnimatorController>(
            TestRoot + "/scoped_out/Scoped_Fx.controller"), "the requested controller persisted");
        probe.AssertWasNotSaved();
    }

}
