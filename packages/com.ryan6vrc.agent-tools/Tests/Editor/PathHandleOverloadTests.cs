using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// The string overloads of the report/check asset doors: they resolve a path or GUID to the typed asset and
// fail loud, with no `log=` trailer, on a handle that names nothing. Each door's own tests cover its body.
public class PathHandleOverloadTests
{
    private const string Dir = "Assets/Agent/_path_handle_test";
    private string _ctrlPath, _ctrlGuid, _clipPath;

    // A resolving call writes an artifact outside `Dir`, which TearDown does not reach. Delete exactly the
    // paths named in each `log=` trailer; a glob over `controller_*.md` would also sweep snapshots the operator
    // took by hand.
    private static readonly List<string> Artifacts = new List<string>();

    [OneTimeTearDown]
    public void DeleteWrittenArtifacts()
    {
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), new List<string>());
        Artifacts.Clear();
    }

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

    // The `log=` trailer proves a run happened, not that the caller's basis reached it. Emit renders the
    // "basis=…" detection line only in the RunLog body, so the body pins the forwarded value, and a bogus token
    // pins that the string is passed through verbatim rather than re-decided in the overload.
    [Test]
    public void CheckAnimator_ByPath_ForwardsBasisAndRuns()
    {
        // basis=explicit with null roots runs the rule set with no scene needed.
        string r = Track(CheckAnimator.Run(_ctrlPath, "explicit"));
        StringAssert.Contains("log=", r);
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
