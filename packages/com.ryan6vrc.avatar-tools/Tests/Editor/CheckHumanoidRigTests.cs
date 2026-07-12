using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// R5 drift pins: CheckHumanoidRig's artifact filename matches its JSON kind (was
// reproportion_freshness_*, inherited from the tool's reproportion-flow origin), and a FAIL exit
// carries the | log= trailer via the shared writer (the old bespoke writer swallowed a write
// failure into a warning and returned a trailer-less summary that still asserted its verdict).
public class CheckHumanoidRigTests
{
    [Test]
    public void Fail_path_writes_a_kind_prefixed_artifact_with_trailer()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[CheckHumanoidRig\] .*=> FAIL"));
        string s = CheckHumanoidRig.Run("Assets/NoSuchModel.fbx");
        StringAssert.Contains("no ModelImporter at path", s);
        StringAssert.Contains("| log=", s);

        string path = s.Substring(s.IndexOf("log=") + 4);
        Assert.IsTrue(System.IO.File.Exists(path), "FAIL writes a RunLog: " + path);
        StringAssert.StartsWith("check-humanoid-rig_", System.IO.Path.GetFileName(path));
        StringAssert.Contains("\"kind\": \"check-humanoid-rig\"", System.IO.File.ReadAllText(path));
        UnityEditor.AssetDatabase.DeleteAsset(path);
    }
}
