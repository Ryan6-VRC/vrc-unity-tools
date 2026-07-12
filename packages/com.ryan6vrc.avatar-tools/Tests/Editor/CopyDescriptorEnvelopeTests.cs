using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// R3 migration pins: CopyDescriptor reports through the shared RunLog envelope. Arg-guard FAILs must
// write a RunLog with the | log= trailer (previously a bare trailer-less line with no artifact), and
// gate FAILs must land in offender grammar (previously the bespoke gateError key).
public class CopyDescriptorEnvelopeTests
{
    static string LogPathOf(string summary) => summary.Substring(summary.IndexOf("log=") + 4);

    [Test]
    public void Null_arg_fails_through_the_runlog_grammar_not_a_bare_line()
    {
        LogAssert.Expect(LogType.Error, new Regex("ownedRoot is null"));
        var s = CopyDescriptor.Run(null, null);
        StringAssert.Contains("error=ownedRoot is null => FAIL | log=", s);
        var path = LogPathOf(s);
        Assert.IsTrue(System.IO.File.Exists(path), "guard FAIL must write a RunLog: " + path);
        UnityEditor.AssetDatabase.DeleteAsset(path);
    }

    [Test]
    public void Gate_fail_names_the_offender_in_the_envelope()
    {
        var owned = new GameObject("Owned");
        owned.transform.localScale = new Vector3(2f, 2f, 2f); // gate A: scale mismatch vs vendor
        var vendor = new GameObject("Vendor");
        LogAssert.Expect(LogType.Error, new Regex("gate scale/orientation"));
        var s = CopyDescriptor.Run(owned, vendor);
        StringAssert.Contains("offenders=[gate scale/orientation", s);

        var path = LogPathOf(s);
        var json = System.IO.File.ReadAllText(path);
        StringAssert.Contains("\"kind\": \"copy-descriptor\"", json);
        StringAssert.Contains("\"offenders\": [", json);
        StringAssert.DoesNotContain("\"gateError\"", json);
        UnityEditor.AssetDatabase.DeleteAsset(path);
        Object.DestroyImmediate(owned);
        Object.DestroyImmediate(vendor);
    }
}
