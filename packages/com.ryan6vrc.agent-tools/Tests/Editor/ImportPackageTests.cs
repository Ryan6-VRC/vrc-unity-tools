using System;
using System.Collections.Generic;
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
//   • NameMatches — the callback guard, pure, pinned against the argument shape Unity was MEASURED to hand
//     back. Left unpinned, a wrong guess at that shape held every RunLog at `pending` across 26 imports.
// The LIVE Import+callback path (real ExportPackage→ImportPackage, async completion) is still exercised
// MANUALLY: the async callbacks and their domain-reload-drop risk fit poorly with the serial batchmode
// suite (see the "live-object-mutating tests crash" suite convention). Verify walking the on-disk root is
// the authoritative signal the contract rests on, and that IS covered here. What the manual half must not
// be trusted to catch again is a guard that can never fire — hence NameMatches being pulled out as pure.
[Category("ImportPackage")]
public class ImportPackageTests
{
    private const string BackslashLiteral = "\\";
    private const string TmpDir = "Assets/AgentImportPackageTmp";
    private string _logPath;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // FAIL branches log at Error — expected in negative tests
    }

    [TearDown]
    public void TearDown()
    {
        bool touchedAssets = false;
        if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath))
        { File.Delete(_logPath); File.Delete(_logPath + ".meta"); touchedAssets = true; }
        _logPath = null;
        if (AssetDatabase.IsValidFolder(TmpDir)) { AssetDatabase.DeleteAsset(TmpDir); touchedAssets = true; }
        // Refresh only when a file actually went away. An unconditional project-wide Refresh here (plus the
        // matching CreateFolder that used to run in SetUp) charged every test in this fixture ~53 ms of
        // AssetDatabase work, and most of them — the whole pure Decide table — touch no asset at all.
        if (touchedAssets) AssetDatabase.Refresh();
        LogAssert.ignoreFailingMessages = false;
    }

    // Created on demand: only the on-disk-root Verify case needs a real folder in the project.
    private static void EnsureTmpDir()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentImportPackageTmp");
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

    // The verdict alone proves nothing here: `default` also FAILs, so deleting either switch arm would keep
    // a verdict-only assert green. The reason token is what distinguishes the arm from the fall-through
    // ("Import was never started"), and it is the only thing the agent reads.
    [Test]
    public void Decide_noRoot_failed_isFail()
    {
        var v = ImportPackage.Decide("failed", false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
        StringAssert.Contains("the import failed", reason);
    }

    [Test]
    public void Decide_noRoot_cancelled_isFail()
    {
        var v = ImportPackage.Decide("cancelled", false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
        StringAssert.Contains("was cancelled", reason);
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
        EnsureTmpDir();
        File.WriteAllText(TmpDir + "/asset.txt", "x");
        AssetDatabase.Refresh();

        var r = ImportPackage.Verify(pkg, TmpDir);
        StringAssert.Contains("=> PASS", r);
        StringAssert.Contains("| log=" + _logPath, r);
        StringAssert.Contains("CheckPackage.Run", r); // hands off deep health, not duplicated
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

    // ── The callback name guard ────────────────────────────────────────────────────────────────────────
    // The one thing the live-import path turns on, and the one thing the "exercise the contract, not a live
    // import" split above left unpinned — so a wrong guess at the callback's argument shape shipped and held
    // every RunLog at `pending` for 26 imports. Pure, so it costs nothing to assert here; the shape in
    // NameMatches's comment is measured, and these cases are what "measured" has to keep meaning.

    [Test]
    public void NameMatches_fullPathMinusExtension_matches()
    {
        // The shape Unity actually hands back. Both separators: a caller's forward-slash path arrives back
        // the way it was passed, and Windows treats / as an alternate separator either way.
        Assert.IsTrue(ImportPackage.NameMatches(@"C:\vendor\Foo", "Foo"));
        Assert.IsTrue(ImportPackage.NameMatches("C:/vendor/Foo", "Foo"));
    }

    [Test]
    public void NameMatches_bareLeafName_matches()
    {
        // The shape this code once assumed. Still accepted — a leaf is its own leaf — so a Unity version or
        // a failed/cancelled callback that does hand back a bare name is not a new stuck-pending bug.
        Assert.IsTrue(ImportPackage.NameMatches("Foo", "Foo"));
    }

    [Test]
    public void NameMatches_multiDotPackageName_matches()
    {
        // Real vendor names carry version dots (`Uruki_Final_v1.2.unitypackage`), and Import derives ourName
        // with GetFileNameWithoutExtension — which strips only `.unitypackage`, leaving `Uruki_Final_v1.2`.
        // The guard must take the leaf, never re-strip an extension off what arrives.
        Assert.IsTrue(ImportPackage.NameMatches(@"C:\vendor\Uruki_Final_v1.2", "Uruki_Final_v1.2"));
    }

    [Test]
    public void NameMatches_caseInsensitive()
    {
        Assert.IsTrue(ImportPackage.NameMatches(@"C:\vendor\FOO", "foo"));
    }

    [Test]
    public void NameMatches_differentPackage_doesNotMatch()
    {
        // The guard still has to do its job: a concurrent import of another package is left for its own
        // handler, and a same-named leaf under a different folder is the collision LogPath already accepts.
        Assert.IsFalse(ImportPackage.NameMatches(@"C:\vendor\Other", "Foo"));
        Assert.IsFalse(ImportPackage.NameMatches(@"C:\vendor\FooBar", "Foo"));
        Assert.IsFalse(ImportPackage.NameMatches(null, "Foo"));
        Assert.IsFalse(ImportPackage.NameMatches("", "Foo"));
    }

    // ── Import: input validation + whatIf ──────────────────────────────────────────────────────────────

    [Test]
    public void Import_missingFile_bareFail()
    {
        ExpectFail();
        var r = ImportPackage.Run("C:/vendor/does-not-exist.unitypackage");
        StringAssert.StartsWith("[ImportPackage] FAIL:", r);
        StringAssert.Contains("does not exist", r);
        Assert.IsFalse(r.Contains("| log="), r);
    }

    // (No Import-side ".zip" case: both doors share one ValidatePackagePath, whose extension arm
    // Verify_badInput_bareFail already pins. What was Import-specific — that validation returns BEFORE any
    // RunLog is written — is what Import_missingFile_bareFail's no-trailer assert proves.)

    [Test]
    public void Import_empty_bareFail()
    {
        ExpectFail();
        var r = ImportPackage.Run("");
        StringAssert.StartsWith("[ImportPackage] FAIL:", r);
        StringAssert.Contains("required", r);
    }

    // whatIf validates + reports the plan (and the log path it WOULD write) without importing or writing.
    [Test]
    public void Import_whatIf_reportsPlan_writesNothing()
    {
        // The fixture only has to make ValidatePackagePath's File.Exists arm true: the whatIf branch returns
        // after that check plus LogPath's string work, never opening, parsing, or importing the file. A real
        // AssetDatabase.ExportPackage tarball here cost 6.7 s — the slowest test in the whole suite, ~4% of
        // in-test time — to produce bytes nothing read.
        var pkgFile = Path.Combine(Path.GetTempPath(), "ImportPackageWhatIf.unitypackage");
        File.WriteAllText(pkgFile, "stub");

        var logPath = ImportPackage.LogPath(pkgFile);
        if (File.Exists(logPath)) File.Delete(logPath);
        try
        {
            var r = ImportPackage.Run(pkgFile, whatIf: true);
            StringAssert.Contains("=> WHATIF", r);
            StringAssert.Contains("wouldLog=" + logPath, r);
            Assert.IsFalse(r.Contains("| log="), "whatIf uses wouldLog=, never a log= trailer: " + r);
            Assert.IsFalse(File.Exists(logPath), "whatIf must not write the RunLog");
        }
        finally { if (File.Exists(pkgFile)) File.Delete(pkgFile); }
    }

    // ── expectedRoot miss: name what exists ───────────────────────────────────────────────────────────
    // Seller-wrapped packages make a wrong guess routine (VRCLens lands under Assets/Hirabiki/VRCLens), and no
    // imported-path list exists to consult — the completion callbacks carry only a package name. So the pointer
    // reports how far the guess survived, which is knowable and enough to aim the retry. The subfolder lister is
    // injected so the walk's branches are assertable without staging folders on disk; `Decide` stays pure and
    // filesystem-free, which is exactly why this lives outside it.

    [Test]
    public void NearestExistingRoot_missBelowARealFolder_namesItAndItsChildren()
    {
        var s = ImportPackage.DescribeNearestExistingRoot("Assets/VRCLens", "C:/pkgs/VRCLens.unitypackage",
            _ => new List<string> { "Assets/Hirabiki", "Assets/Vendor" });

        StringAssert.Contains("deepest existing is 'Assets'", s);
        StringAssert.Contains("Hirabiki", s);
        // The output must chain into the next call, not merely describe the failure.
        StringAssert.Contains("re-run Verify(", s);
        StringAssert.Contains("C:/pkgs/VRCLens.unitypackage", s);
    }

    // The hint is a C# call the reader may paste, so a Windows path must be escaped or the separators become
    // escape sequences and it will not compile — the same "remedy you cannot actually run" defect this batch
    // fixes elsewhere.
    [Test]
    public void NearestExistingRoot_windowsPath_isEscapedForPasting()
    {
        string winPath = "C:" + BackslashLiteral + "temp" + BackslashLiteral + "new" + BackslashLiteral + "pack.unitypackage";

        var s = ImportPackage.DescribeNearestExistingRoot("Assets/Nope", winPath,
            _ => new List<string> { "Assets/Vendor" });

        // Q() doubles each separator, so the emitted call compiles when pasted.
        StringAssert.Contains("C:" + BackslashLiteral + BackslashLiteral + "temp", s);
        Assert.IsFalse(s.Contains("C:" + BackslashLiteral + "temp"  + BackslashLiteral + "new"), "raw separators would not survive a paste: " + s);
    }

    [Test]
    public void NearestExistingRoot_folderWithNoSubfolders_saysSoRatherThanEmptyBrackets()
    {
        var s = ImportPackage.DescribeNearestExistingRoot("Assets/Nope", "p.unitypackage", _ => new List<string>());

        StringAssert.Contains("no subfolders", s);
        // Telling the caller to append "<one of those>" when there is nothing to append is an unfollowable step.
        StringAssert.DoesNotContain("one of those", s);
    }

    [Test]
    public void NearestExistingRoot_manyChildren_capsAndCountsTheRest()
    {
        var many = new List<string>();
        for (int i = 0; i < 12; i++) many.Add("Assets/F" + i);

        var s = ImportPackage.DescribeNearestExistingRoot("Assets/Nope", "p.unitypackage", _ => many);

        StringAssert.Contains("F0", s);
        StringAssert.Contains("+4 more", s);
        StringAssert.DoesNotContain("F11", s);
    }

    [Test]
    public void NearestExistingRoot_nothingExists_saysThePathIsNotRootedWhereTheDatabaseLooks()
    {
        // No surviving prefix at all: "deepest existing is nothing" would be useless, so the caller is told the
        // path is not somewhere the AssetDatabase can see.
        var s = ImportPackage.DescribeNearestExistingRoot("Packages/Whatever", "p.unitypackage", _ => new List<string>());

        StringAssert.Contains("no part of", s);
        StringAssert.Contains("rooted at Assets/", s);
    }
}
