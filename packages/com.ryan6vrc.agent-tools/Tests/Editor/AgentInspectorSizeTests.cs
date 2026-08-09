using System.IO;
using System.Text;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// AgentInspector's size note. A whole-hierarchy snapshot of a complex vendor avatar runs past the agent
// harness's file-read cap (measured: 639KB), and the caller used to learn that only when the Read failed —
// after paying for the walk. The artifact stays complete on purpose; the SUMMARY is what gets windowed.
//
// The cap is a FOREIGN constant (a property of the harness, not of Unity or the asset), so the two guards
// below are the ones that matter: size is reported unconditionally, and it is measured in BYTES from disk.
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

        var s = AgentInspector.SizeNote(_tmp, "Avatar/Body", includeChildren: true);

        StringAssert.Contains("read cap", s);
        // Must chain into an actual next call, and must match the 3-arg signature unity-tools.md documents —
        // a hint naming a stale overload teaches the wrong door.
        StringAssert.Contains("includeChildren: false", s);
        StringAssert.Contains("followAssets: false", s);
        StringAssert.Contains("Avatar/Body", s);
    }

    // The measurement bug this guards: the artifact is UTF-8 and Japanese vendor asset names are routine in
    // this venue, so a char count under-reports bytes and would promise a fit that fails at Read time.
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
        // The artifact is already written by this point; a stat failure must not turn a good snapshot into a
        // failed one, so the note degrades to nothing.
        Assert.AreEqual("", AgentInspector.SizeNote(_tmp + "_absent", "Avatar", true));
    }
}
