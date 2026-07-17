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
        string r = ReportController.Report(_ctrlPath);
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("log=", r);
    }

    [Test]
    public void ReportController_ByGuid_Resolves()
    {
        StringAssert.Contains("=> OK", ReportController.Report(_ctrlGuid));
    }

    [Test]
    public void ReportController_BadHandle_FailsLoud_NoTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex("no AnimatorController at"));
        string r = ReportController.Report("Assets/Nope/missing.controller");
        StringAssert.Contains("FAIL", r);
        StringAssert.Contains("missing.controller", r);   // echoes the failed handle
        StringAssert.DoesNotContain("log=", r);
    }

    [Test]
    public void CheckAnimator_ByPath_ForwardsBasisAndRuns()
    {
        // basis=explicit with null roots is the descriptor-borne case — runs the rule set, no scene needed.
        string r = CheckAnimator.Lint(_ctrlPath, "explicit");
        StringAssert.Contains("log=", r);                 // a real run wrote a RunLog
    }

    [Test]
    public void CheckAnimator_BadHandle_FailsLoud_NoTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex("no AnimatorController at"));
        string r = CheckAnimator.Lint("Assets/Nope/missing.controller");
        StringAssert.Contains("FAIL", r);
        StringAssert.DoesNotContain("log=", r);
    }

    [Test]
    public void ReportClip_ByPath_SucceedsAndWritesLog()
    {
        string r = ReportClip.Report(_clipPath);
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("log=", r);
    }

    [Test]
    public void ReportClip_BadHandle_FailsLoud_NoTrailer()
    {
        LogAssert.Expect(LogType.Error, new Regex("no AnimationClip at"));
        string r = ReportClip.Report("Assets/Nope/missing.anim");
        StringAssert.Contains("FAIL", r);
        StringAssert.DoesNotContain("log=", r);
    }
}
