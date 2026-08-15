using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// F39: the report/check ASSET doors take the typed asset object; these string overloads resolve an asset
// path OR GUID to that same object (so an agent holding only a path/GUID need not pre-LoadAssetAtPath) and
// fail loud — echoing the handle, no `log=` trailer — on a handle that names nothing. The digest/lint
// bodies are covered by each door's own tests; this fixture exercises only the string entry points and
// their resolution + bad-input contract.
public class PathHandleOverloadTests
{
    private const string Dir = "Assets/Agent/_path_handle_test";
    private string _ctrlPath, _ctrlGuid, _clipPath;

    // A resolving call writes an artifact outside `Dir`, so the per-test teardown below does not reach it:
    // ReportController/ReportClip write a Snapshot markdown, which docs/unity-tools.md declares DURABLE — the
    // operator's own pile, pruned by nothing. Record the path each call named in its `| log=` trailer and delete
    // exactly those; a glob over `controller_*.md` would also sweep snapshots the operator took by hand. Keying
    // on the trailer rather than on which door wrote it also survives a door changing output channel.
    private static readonly List<string> Artifacts = new List<string>();

    [OneTimeTearDown]
    public void DeleteWrittenArtifacts()
    {
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), new List<string>());
        Artifacts.Clear();
    }

    // Every door call in this fixture goes through here — including the ones that only assert on the returned
    // summary, which still wrote their artifact, and the bad-handle ones, whose contract is that there is no
    // trailer to record.
    private static string Track(string summary)
    {
        int i = summary.IndexOf("log=");
        if (i >= 0) Artifacts.Add(summary.Substring(i + 4).Trim());
        return summary;
    }

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(Dir);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(Dir + "/ph.controller");
        _ctrlPath = AssetDatabase.GetAssetPath(ctrl);
        _ctrlGuid = AssetDatabase.AssetPathToGUID(_ctrlPath);
        _clipPath = Dir + "/ph.anim";
        AssetDatabase.CreateAsset(new AnimationClip { name = "phclip" }, _clipPath);
        AssetDatabase.SaveAssets();
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(Dir);

    [Test]
    public void ReportController_ByPath_SucceedsAndWritesLog()
    {
        string r = Track(ReportController.Run(_ctrlPath));
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("log=", r);
    }

    [Test]
    public void ReportController_ByGuid_Resolves()
    {
        StringAssert.Contains("=> OK", Track(ReportController.Run(_ctrlGuid)));
    }

    [Test]
    public void ReportController_BadHandle_FailsLoud_NoTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex("no AnimatorController at"));
        string r = Track(ReportController.Run("Assets/Nope/missing.controller"));
        StringAssert.Contains("FAIL", r);
        StringAssert.Contains("missing.controller", r);   // echoes the failed handle
        StringAssert.DoesNotContain("log=", r);
    }

    // The `log=` trailer alone did not earn this test's name: it proves a run happened, not that the caller's
    // basis reached it. The forwarded value is only observable in the RunLog BODY — Emit renders the
    // "basis=…" detection line there, never into the returned summary — so the body is what pins it, and a
    // bogus token pins that the string is passed through verbatim rather than re-decided in the overload.
    [Test]
    public void CheckAnimator_ByPath_ForwardsBasisAndRuns()
    {
        // basis=explicit with null roots is the descriptor-borne case — runs the rule set, no scene needed.
        string r = Track(CheckAnimator.Run(_ctrlPath, "explicit"));
        StringAssert.Contains("log=", r);                 // a real run wrote a RunLog
        string body = File.ReadAllText(r.Substring(r.IndexOf("log=") + "log=".Length));
        StringAssert.Contains("basis=explicit", body);

        LogAssert.Expect(LogType.Error, new Regex("unknown basis 'no-such-basis'"));
        StringAssert.Contains("no-such-basis", Track(CheckAnimator.Run(_ctrlPath, "no-such-basis")));
    }

    [Test]
    public void CheckAnimator_BadHandle_FailsLoud_NoTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex("no AnimatorController at"));
        string r = Track(CheckAnimator.Run("Assets/Nope/missing.controller"));
        StringAssert.Contains("FAIL", r);
        StringAssert.DoesNotContain("log=", r);
    }

    [Test]
    public void ReportClip_ByPath_SucceedsAndWritesLog()
    {
        string r = Track(ReportClip.Run(_clipPath));
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("log=", r);
    }

    [Test]
    public void ReportClip_BadHandle_FailsLoud_NoTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex("no AnimationClip at"));
        string r = Track(ReportClip.Run("Assets/Nope/missing.anim"));
        StringAssert.Contains("FAIL", r);
        StringAssert.DoesNotContain("log=", r);
    }
}
