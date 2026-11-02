using System.IO;
using System.Text;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// AgentInspector's size note. The artifact is never truncated, so the summary is what warns a caller that a
// Read would pass the agent harness's file-read cap. The cap is a FOREIGN constant (the harness's, not Unity's
// or the asset's) that nothing here can detect drifting, so the guards that matter are that size is reported
// unconditionally and measured in BYTES from disk.
public class AgentInspectorSizeTests
{
    private string _tmp;

    [SetUp]
    public void SetUp() => _tmp = Path.Combine(Path.GetTempPath(), "d3_sizenote_" + Path.GetRandomFileName());

    [TearDown]
    public void TearDown() { if (File.Exists(_tmp)) File.Delete(_tmp); }

    [Test]
    public void SizeNote_underTheCap_reportsSizeAndOffersNoHint()
    {
        File.WriteAllText(_tmp, new string('x', 1000));

        var s = AgentInspector.SizeNote(_tmp, "Avatar", includeChildren: true);

        StringAssert.Contains("bytes=1000", s);
        StringAssert.DoesNotContain("read cap", s);
    }

    [Test]
    public void SizeNote_pastTheCap_namesTheNarrowerDoorWithTheRealSignature()
    {
        File.WriteAllText(_tmp, new string('x', 300 * 1024));

        var s = AgentInspector.SizeNote(_tmp, "Avatar/Body", includeChildren: true, followAssets: true);

        StringAssert.Contains("read cap", s);
        // The hint is a string, so nothing else ties it to the 3-arg Run signature unity-tools.md documents.
        StringAssert.Contains("includeChildren: false", s);
        StringAssert.Contains("followAssets: false", s);
        StringAssert.Contains("Avatar/Body", s);
    }

    [Test]
    public void SizeNote_pastTheCapWithFollowAssetsAlreadyOff_doesNotPrescribeIt()
    {
        File.WriteAllText(_tmp, new string('x', 300 * 1024));

        var s = AgentInspector.SizeNote(_tmp, "Avatar/Body", includeChildren: false, followAssets: false);

        StringAssert.Contains("read cap", s);
        StringAssert.DoesNotContain("followAssets", s);
        StringAssert.Contains("deeper path", s);
    }

    // The artifact is UTF-8 and Japanese vendor asset names are routine, so a char count under-reports bytes
    // and would promise a fit that fails at Read time.
    [Test]
    public void SizeNote_multiByteContent_measuresBytesNotCharacters()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 1000; i++) sb.Append("あ"); // 1000 chars, 3000 UTF-8 bytes
        File.WriteAllText(_tmp, sb.ToString(), new UTF8Encoding(false));

        var s = AgentInspector.SizeNote(_tmp, "Avatar", includeChildren: false);

        StringAssert.Contains("bytes=3000", s);
    }

    [Test]
    public void SizeNote_unstattableFile_staysSilentRatherThanSinkingTheCall()
    {
        Assert.AreEqual("", AgentInspector.SizeNote(_tmp + "_absent", "Avatar", true));
    }
}
