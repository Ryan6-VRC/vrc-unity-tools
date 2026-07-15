using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// ImportPackage proof obligations. The tool's whole point is a result contract that survives a
// transport timeout, so the tests exercise the contract, NOT a live 60–700MB import:
//   • Decide(...) — the verify decision table, pure, so every branch (including the editor-busy PENDING
//     one that can't be provoked headless) is asserted directly.
//   • RunLog shape — WriteImportLog / ReadStatus round-trip on disk at the stable, package-derived path.
//   • Verify door — plumbing over fabricated RunLogs + temp asset folders (no import performed).
//   • Import input validation + whatIf — bad input is a bare FAIL; whatIf writes nothing.
// The LIVE Import+callback path (real ExportPackage→ImportPackage, async completion) is exercised
// MANUALLY, not here: the async callbacks and their domain-reload-drop risk fit poorly with the serial
// batchmode suite (see the "live-object-mutating tests crash" suite convention). Verify walking the
// on-disk root is the authoritative signal the contract rests on, and that IS covered here.
[Category("ImportPackage")]
public class ImportPackageTests
{
    private const string TmpDir = "Assets/AgentImportPackageTmp";
    private string _logPath;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // FAIL branches log at Error — expected in negative tests
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentImportPackageTmp");
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath)) { File.Delete(_logPath); File.Delete(_logPath + ".meta"); }
        _logPath = null;
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        AssetDatabase.Refresh();
        LogAssert.ignoreFailingMessages = false;
    }

    // A package path that never touches disk — Verify/Decide only need its leaf to derive the log path.
    private static string Pkg(string name) => "C:/vendor/" + name + ".unitypackage";

    // FAIL branches log at Error; ignoreFailingMessages is not reliable, so each negative test declares
    // the expected Error explicitly (the suite's ReportShapeOverlapTests convention).
    private static readonly Regex ErrRe = new Regex(@"\[ImportPackage\]");
    private static void ExpectFail() => LogAssert.Expect(LogType.Error, ErrRe);

    // ── Pure decision table: expectedRoot provided ─────────────────────────────────────────────────────

    [Test]
    public void Decide_rootWithFiles_passesRegardlessOfStatus()
    {
        // On-disk truth wins over a stale RunLog: even a "pending"/"failed" status passes when the root landed.
        foreach (var status in new[] { "pending", "completed", "failed", null })
        {
            var v = ImportPackage.Decide(status, editorBusy: false, rootProvided: true,
                                         rootExists: true, importedFileCount: 12, out var reason);
            Assert.AreEqual(ImportPackage.Verdict.Pass, v, "status=" + (status ?? "none") + ": " + reason);
            StringAssert.Contains("12", reason);
        }
    }

    [Test]
    public void Decide_rootMissing_pendingAndBusy_isPending()
    {
        var v = ImportPackage.Decide("pending", editorBusy: true, rootProvided: true,
                                     rootExists: false, importedFileCount: 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Pending, v, reason);
        StringAssert.Contains("still running", reason);
    }

    [Test]
    public void Decide_rootMissing_idle_isFail()
    {
        var v = ImportPackage.Decide("pending", editorBusy: false, rootProvided: true,
                                     rootExists: false, importedFileCount: 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
        StringAssert.Contains("did not land", reason);
    }

    [Test]
    public void Decide_rootExistsButEmpty_idle_isFail()
    {
        var v = ImportPackage.Decide("completed", editorBusy: false, rootProvided: true,
                                     rootExists: true, importedFileCount: 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
        StringAssert.Contains("empty", reason);
    }

    // ── Pure decision table: no expectedRoot (trust the RunLog, on-disk unverified) ─────────────────────

    [Test]
    public void Decide_noRoot_completed_isPass()
    {
        var v = ImportPackage.Decide("completed", false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Pass, v, reason);
        StringAssert.Contains("not verified", reason);
    }

    [Test]
    public void Decide_noRoot_pendingBusy_isPending()
    {
        var v = ImportPackage.Decide("pending", editorBusy: true, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Pending, v, reason);
    }

    // The dropped-callback case: pending + idle ⇒ FAIL, and the reason names the domain-reload cause.
    [Test]
    public void Decide_noRoot_pendingIdle_isFailNamingDroppedCallback()
    {
        var v = ImportPackage.Decide("pending", editorBusy: false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
        StringAssert.Contains("domain reload", reason);
    }

    [Test]
    public void Decide_noRoot_failed_isFail()
    {
        var v = ImportPackage.Decide("failed", false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
    }

    [Test]
    public void Decide_noRoot_cancelled_isFail()
    {
        var v = ImportPackage.Decide("cancelled", false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
    }

    [Test]
    public void Decide_noRoot_noRunLog_isFail()
    {
        var v = ImportPackage.Decide(null, false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
        StringAssert.Contains("never started", reason);
    }

    // ── RunLog shape + stable path ─────────────────────────────────────────────────────────────────────

    [Test]
    public void RunLog_shape_roundTripsAtStablePath()
    {
        var pkg = Pkg("Costume Set");           // a space ⇒ Sanitize exercised
        var path = ImportPackage.LogPath(pkg);
        // Stable, package-derived, no timestamp — Verify reconstructs the exact path Import wrote.
        Assert.AreEqual("Assets/Agent/RunLogs/import-package_Costume_Set.json", path);

        _logPath = ImportPackage.WriteImportLog(path, pkg, "Costume Set", "pending", null);
        Assert.AreEqual(path, _logPath);
        var body = File.ReadAllText(path);
        StringAssert.Contains("\"kind\": \"import-package\"", body);
        StringAssert.Contains("\"status\": \"pending\"", body);
        StringAssert.Contains("\"packageName\": \"Costume Set\"", body);
        StringAssert.Contains("\"error\": null", body);
        StringAssert.Contains("domain reload", body); // the callback-drop caveat is recorded in the artifact
        Assert.AreEqual("pending", ImportPackage.ReadStatus(path));

        // A re-import overwrites the same file in place (idempotent), it does not spawn a second log.
        ImportPackage.WriteImportLog(path, pkg, "Costume Set", "completed", null);
        Assert.AreEqual("completed", ImportPackage.ReadStatus(path));
    }

    // ── Verify door: over fabricated logs + temp folders (no live import) ───────────────────────────────

    private string FabricateLog(string pkg, string status)
    {
        _logPath = ImportPackage.LogPath(pkg);
        return ImportPackage.WriteImportLog(_logPath, pkg, Path.GetFileNameWithoutExtension(pkg), status, null);
    }

    [Test]
    public void Verify_rootWithImportedFiles_passes()
    {
        var pkg = Pkg("Landed");
        FabricateLog(pkg, "pending"); // stale status; the on-disk root is the truth
        File.WriteAllText(TmpDir + "/asset.txt", "x");
        AssetDatabase.Refresh();

        var r = ImportPackage.Verify(pkg, TmpDir);
        StringAssert.Contains("=> PASS", r);
        StringAssert.Contains("| log=" + _logPath, r);
        StringAssert.Contains("CheckPackage.VerifyFolder", r); // hands off deep health, not duplicated
    }

    [Test]
    public void Verify_pendingLog_idle_noRoot_fails()
    {
        var pkg = Pkg("StuckPending");
        FabricateLog(pkg, "pending");
        ExpectFail();
        var r = ImportPackage.Verify(pkg); // editor idle in batchmode ⇒ pending+idle ⇒ FAIL
        StringAssert.Contains("=> FAIL", r);
        StringAssert.Contains("| log=" + _logPath, r);
    }

    [Test]
    public void Verify_completedLog_noRoot_passes()
    {
        var pkg = Pkg("Done");
        FabricateLog(pkg, "completed");
        var r = ImportPackage.Verify(pkg);
        StringAssert.Contains("=> PASS", r);
    }

    [Test]
    public void Verify_noRunLog_fails_withNoLogTrailer()
    {
        var pkg = Pkg("NeverImported");
        // No fabricated log; ensure none lingers.
        var path = ImportPackage.LogPath(pkg);
        if (File.Exists(path)) File.Delete(path);
        ExpectFail();
        var r = ImportPackage.Verify(pkg);
        StringAssert.Contains("=> FAIL", r);
        Assert.IsFalse(r.Contains("| log="), "no RunLog on disk ⇒ no log trailer: " + r);
    }

    [Test]
    public void Verify_badInput_bareFail()
    {
        ExpectFail();
        var r = ImportPackage.Verify("C:/vendor/not-a-package.zip");
        StringAssert.StartsWith("[ImportPackage] FAIL:", r);
        StringAssert.Contains(".unitypackage", r);
        Assert.IsFalse(r.Contains("| log="), "bad input is a bare FAIL, no trailer");
    }

    // ── Import: input validation + whatIf ──────────────────────────────────────────────────────────────

    [Test]
    public void Import_missingFile_bareFail()
    {
        ExpectFail();
        var r = ImportPackage.Import("C:/vendor/does-not-exist.unitypackage");
        StringAssert.StartsWith("[ImportPackage] FAIL:", r);
        StringAssert.Contains("does not exist", r);
        Assert.IsFalse(r.Contains("| log="), r);
    }

    [Test]
    public void Import_notAPackage_bareFail()
    {
        ExpectFail();
        var r = ImportPackage.Import("C:/vendor/thing.zip");
        StringAssert.StartsWith("[ImportPackage] FAIL:", r);
        StringAssert.Contains(".unitypackage", r);
    }

    [Test]
    public void Import_empty_bareFail()
    {
        ExpectFail();
        var r = ImportPackage.Import("");
        StringAssert.StartsWith("[ImportPackage] FAIL:", r);
        StringAssert.Contains("required", r);
    }

    // whatIf validates + reports the plan (and the log path it WOULD write) without importing or writing.
    [Test]
    public void Import_whatIf_reportsPlan_writesNothing()
    {
        // A real, existing .unitypackage is needed to pass the exists check; export a throwaway one.
        var asset = TmpDir + "/probe.txt";
        File.WriteAllText(asset, "probe");
        AssetDatabase.Refresh();
        var pkgFile = Path.Combine(Path.GetTempPath(), "ImportPackageWhatIf.unitypackage");
        AssetDatabase.ExportPackage(asset, pkgFile);
        Assert.IsTrue(File.Exists(pkgFile), "export produced the probe package");

        var logPath = ImportPackage.LogPath(pkgFile);
        if (File.Exists(logPath)) File.Delete(logPath);
        try
        {
            var r = ImportPackage.Import(pkgFile, whatIf: true);
            StringAssert.Contains("=> WHATIF", r);
            StringAssert.Contains("wouldLog=" + logPath, r);
            Assert.IsFalse(r.Contains("| log="), "whatIf uses wouldLog=, never a log= trailer: " + r);
            Assert.IsFalse(File.Exists(logPath), "whatIf must not write the RunLog");
        }
        finally { if (File.Exists(pkgFile)) File.Delete(pkgFile); }
    }
}
