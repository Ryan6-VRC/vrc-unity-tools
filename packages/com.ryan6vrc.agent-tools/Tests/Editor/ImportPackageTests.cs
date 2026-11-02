using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// ImportPackage's result contract, over fabricated RunLogs and temp folders rather than a live import. The
// live Import+callback path (async completion, domain-reload drop) is exercised manually; of it, only
// NameMatches, the callback guard, is covered here.
[Category("ImportPackage")]
public class ImportPackageTests
{
    private const string BackslashLiteral = "\\";
    private const string TmpDir = "Assets/AgentImportPackageTmp";
    private string _logPath;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true;
    }

    [TearDown]
    public void TearDown()
    {
        bool touchedAssets = false;
        if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath))
        { File.Delete(_logPath); File.Delete(_logPath + ".meta"); touchedAssets = true; }
        _logPath = null;
        if (AssetDatabase.IsValidFolder(TmpDir)) { AssetDatabase.DeleteAsset(TmpDir); touchedAssets = true; }
        if (touchedAssets) AssetDatabase.Refresh();
        LogAssert.ignoreFailingMessages = false;
    }

    private static void EnsureTmpDir()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", "AgentImportPackageTmp");
    }

    private static string Pkg(string name) => "C:/vendor/" + name + ".unitypackage";

    // FAIL branches log at Error; ignoreFailingMessages is not reliable, so each negative test declares
    // the expected Error explicitly.
    private static readonly Regex ErrRe = new Regex(@"\[ImportPackage\]");
    private static void ExpectFail() => LogAssert.Expect(LogType.Error, ErrRe);

    // ── Pure decision table: expectedRoot provided ─────────────────────────────────────────────────────

    [Test]
    public void Decide_rootWithFiles_passesRegardlessOfStatus()
    {
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

    [Test]
    public void Decide_noRoot_pendingIdle_isFailNamingDroppedCallback()
    {
        var v = ImportPackage.Decide("pending", editorBusy: false, rootProvided: false, false, 0, out var reason);
        Assert.AreEqual(ImportPackage.Verdict.Fail, v, reason);
        StringAssert.Contains("domain reload", reason);
    }

    // The verdict alone proves nothing here: `default` also FAILs, so deleting either switch arm would keep
    // a verdict-only assert green. The reason token distinguishes the arm from the fall-through.
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

        // A re-import overwrites the same file in place.
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
        StringAssert.Contains("CheckPackage.Run", r);
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
    // These cases pin the callback argument shape recorded at NameMatches.

    [Test]
    public void NameMatches_fullPathMinusExtension_matches()
    {
        // The shape Unity hands back. Both separators, because a caller may pass either and Path.GetFileName
        // accepts both on Windows.
        Assert.IsTrue(ImportPackage.NameMatches(@"C:\vendor\Foo", "Foo"));
        Assert.IsTrue(ImportPackage.NameMatches("C:/vendor/Foo", "Foo"));
    }

    [Test]
    public void NameMatches_bareLeafName_matches()
    {
        Assert.IsTrue(ImportPackage.NameMatches("Foo", "Foo"));
    }

    [Test]
    public void NameMatches_multiDotPackageName_matches()
    {
        // Vendor names carry version dots, and Import's GetFileNameWithoutExtension strips only `.unitypackage`,
        // so the guard takes the leaf and never re-strips an extension off what arrives.
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

    [Test]
    public void Import_empty_bareFail()
    {
        ExpectFail();
        var r = ImportPackage.Run("");
        StringAssert.StartsWith("[ImportPackage] FAIL:", r);
        StringAssert.Contains("required", r);
    }

    [Test]
    public void Import_whatIf_reportsPlan_writesNothing()
    {
        // The fixture only has to make ValidatePackagePath's File.Exists arm true: the whatIf branch returns
        // after that check plus LogPath's string work, never opening, parsing, or importing the file.
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
    // Seller-wrapped packages make a wrong guess routine (VRCLens lands under Assets/Hirabiki/VRCLens), and the
    // completion callbacks carry only a package name, so the pointer reports how far the guess survived.

    [Test]
    public void NearestExistingRoot_missBelowARealFolder_namesItAndItsChildren()
    {
        var s = ImportPackage.DescribeNearestExistingRoot("Assets/VRCLens", "C:/pkgs/VRCLens.unitypackage",
            _ => new List<string> { "Assets/Hirabiki", "Assets/Vendor" });

        StringAssert.Contains("deepest existing is 'Assets'", s);
        StringAssert.Contains("Hirabiki", s);
        StringAssert.Contains("re-run Verify(", s);
        StringAssert.Contains("C:/pkgs/VRCLens.unitypackage", s);
    }

    // The hint is a C# call the reader may paste, so an unescaped Windows path would not compile.
    [Test]
    public void NearestExistingRoot_windowsPath_isEscapedForPasting()
    {
        string winPath = "C:" + BackslashLiteral + "temp" + BackslashLiteral + "new" + BackslashLiteral + "pack.unitypackage";

        var s = ImportPackage.DescribeNearestExistingRoot("Assets/Nope", winPath,
            _ => new List<string> { "Assets/Vendor" });

        StringAssert.Contains("C:" + BackslashLiteral + BackslashLiteral + "temp", s);
        Assert.IsFalse(s.Contains("C:" + BackslashLiteral + "temp"  + BackslashLiteral + "new"), "raw separators would not survive a paste: " + s);
    }

    [Test]
    public void NearestExistingRoot_folderWithNoSubfolders_saysSoRatherThanEmptyBrackets()
    {
        var s = ImportPackage.DescribeNearestExistingRoot("Assets/Nope", "p.unitypackage", _ => new List<string>());

        StringAssert.Contains("no subfolders", s);
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
        var s = ImportPackage.DescribeNearestExistingRoot("Packages/Whatever", "p.unitypackage", _ => new List<string>());

        StringAssert.Contains("no part of", s);
        StringAssert.Contains("rooted at Assets/", s);
    }
}
