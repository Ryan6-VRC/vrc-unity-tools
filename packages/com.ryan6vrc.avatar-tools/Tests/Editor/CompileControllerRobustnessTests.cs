using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

// Edge-case robustness tests for CompileController (kickoff item V). Behavioral, through the Compile
// door — same venue as CompileControllerTests: headless via tools/run-editmode-tests.ps1, never MCP
// run_tests. FAIL/WARN logs are declared with LogAssert.Expect so the runner doesn't count them as
// failures.
public class CompileControllerRobustnessTests
{
    private const string TestRoot = "Assets/Agent/Scratch/ccrobust_tests";

    [SetUp]
    public void SetUp()
    {
        // EnsureFolder (AssetDatabase.CreateFolder) registers TestRoot as a valid asset folder as it creates
        // it — what the old Directory.CreateDirectory + project-wide Refresh() pair was really buying.
        AnimatorTestHelpers.EnsureFolder(TestRoot);
    }

    [TearDown]
    public void TearDown()
    {
        // No trailing Refresh(): DeleteAsset already tells the AssetDatabase the folder is gone, and the raw
        // Directory.Delete is only a fallback for a folder the AssetDatabase never adopted.
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
    }

    // Read the RunLog body a successful compile writes in-band (`… | log=<path>`).
    private static string ReadRunLogBody(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "result carries the RunLog path in-band: " + result);
        string logPath = result.Substring(i + marker.Length).Trim();
        Assert.IsTrue(File.Exists(logPath), "RunLog file exists at " + logPath);
        return File.ReadAllText(logPath);
    }

    // ── Gap 2 — a layer too large to bound cheaply must SURFACE that the frame-latency advisory was
    // skipped, not vanish silently. The >128-state node-count guard previously returned no advisory AND
    // no skip note, unlike the step-budget (over-dense) path.
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

    // ── Gap 3 — clobbering a controller with NO compile provenance (hand-authored, or produced by another
    // tool) must warn before it is stripped in place. Previously the warning fired only for controllers we
    // ourselves had stamped — blind to the highest-value clobber case.
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

    // ── The stamp is written with WriteImportSettingsIfDirty instead of SaveAndReimport, so the ONLY proof
    // it persisted is the .meta text on disk. AssetImporter.GetAtPath cannot testify here — it hands back the
    // cached in-memory importer, which reports the assigned value whether or not a byte was written.
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

        // The idempotent recompile: the stamp string is unchanged, so the importer is never dirty and
        // WriteImportSettingsIfDirty returns FALSE while the .meta is already correct. Gating on that return
        // alone would fire a spurious failure right here.
        StringAssert.Contains("=> OK", CompileController.Run(src, outDir, whatIf: false));
        StringAssert.Contains("compiled-from:;srchash:", File.ReadAllText(metaPath),
            "the unchanged stamp is still on disk after a no-op recompile, and nothing reported a failure");
    }

    // ── Controllers stamped before the path was dropped carry `compiled-from:<path>;srchash:<hash>` (26 of
    // them are committed in vrc-patterns). Both signals must still read that form: the key is found by
    // IndexOf, and ExtractField must stop the hash at the ';' the new form does not have.
    [Test]
    public void Legacy_Stamp_With_A_Path_Still_Parses_As_Provenance()
    {
        string outDir = TestRoot + "/out_legacy";
        string src = TestRoot + "/Debounce_Fx.yaml";
        File.WriteAllText(src, AnimatorSchemaYamlTests.DebounceDoc);
        StringAssert.Contains("=> OK", CompileController.Run(src, outDir, whatIf: false));

        // Restamp in the old shape, with a hash that cannot match the source — so a correct parse reaches
        // signal (b), "srchash differs", rather than signal (a), "no provenance at all".
        var imp = AssetImporter.GetAtPath(outDir + "/Debounce_Fx.controller");
        imp.userData = "compiled-from:Packages/com.ryan6vrc.patterns/x/controller.yaml;srchash:deadbeefdeadbeef";
        imp.SaveAndReimport();

        LogAssert.Expect(LogType.Warning, new Regex(
            @"\[CompileController\] overwriting .*Debounce_Fx\.controller.*srchash=deadbeefdeadbeef differs"));
        StringAssert.Contains("=> OK", CompileController.Run(src, outDir, whatIf: false));
    }

    // ── Gap 1 (scope decision, recorded as an executable contract) — inline clip bindings resolve
    // UnityEngine-namespace component types ONLY. A binding to a UI / VRC-SDK component (here
    // UnityEngine.UI.Image, whose simple name is not under the UnityEngine namespace) is refused
    // fail-loud, by design — not silently mis-emitted. Guards against a future silent broadening.
    [Test]
    public void Inline_Binding_To_NonUnityEngine_Component_Is_Refused()
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
            "the refusal names the UnityEngine-namespace scope limit so it reads as intentional");
        Assert.IsFalse(File.Exists(outDir + "/Ui_Fx.controller"), "no controller written on the refused binding");
        AnimatorTestHelpers.DeleteRefusalArtifact(result);
    }

    // ── Gap 4 — "nothing written on failure" extends to FOLDERS. A fresh compile into a not-yet-existing
    // nested outDir that then fails emit must leave no leftover empty folders it created.
    [Test]
    public void Failed_Fresh_Compile_Leaves_No_Empty_Folders()
    {
        string outDir = TestRoot + "/g4_new/deep"; // neither segment exists yet
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
        Assert.IsFalse(AssetDatabase.IsValidFolder(TestRoot + "/g4_new"), "freshly-created ancestor folder removed too");
        AnimatorTestHelpers.DeleteRefusalArtifact(result);
    }
}
