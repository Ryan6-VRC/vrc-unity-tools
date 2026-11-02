using System.IO;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;

// The capture paths need a real avatar and (for play) the player loop, so these tests pin only the RunLog
// envelope every verb routes through.
public class RenderThumbnailRunLogTests
{
    private readonly System.Collections.Generic.List<string> _written = new System.Collections.Generic.List<string>();

    [TearDown]
    public void TearDown()
    {
        foreach (var p in _written) if (File.Exists(p)) File.Delete(p);
        _written.Clear();
    }

    private string Record(string summary)
    {
        const string marker = "| log=";
        int i = summary.IndexOf(marker, System.StringComparison.Ordinal);
        if (i >= 0) _written.Add(summary.Substring(i + marker.Length).Trim());
        return summary;
    }

    [Test]
    public void SessionLog_writesTheArtifactAndCarriesTheTrailerInBand()
    {
        var s = Record(RenderThumbnailCore.WriteSessionLog(
            "renderthumbnailplay-begin", "TestAvatar", "Begin TestAvatar => READY-TO-PLAY", "# body\n"));

        StringAssert.Contains("=> READY-TO-PLAY", s);
        StringAssert.Contains("| log=", s);
        Assert.AreEqual(1, _written.Count);
        Assert.IsTrue(File.Exists(_written[0]), "the trailer must point at an artifact that is on disk: " + s);
        StringAssert.Contains("# body", File.ReadAllText(_written[0]));
    }

    // A RunLog sweep tells the arcs apart by this kind prefix, so begin, shoot and end must not share a name.
    [Test]
    public void SessionLog_filenameCarriesTheVerbKind()
    {
        Record(RenderThumbnailCore.WriteSessionLog("renderthumbnailplay-end", "TestAvatar", "End => OK", "x"));

        StringAssert.Contains("renderthumbnailplay-end_TestAvatar", Path.GetFileName(_written[0]));
    }

    [Test]
    public void SessionLog_landsUnderTheDeclaredRunLogDir()
    {
        Record(RenderThumbnailCore.WriteSessionLog("renderthumbnail", "TestAvatar", "=> OK", "x"));

        StringAssert.StartsWith(RunLogFormat.RunLogDir, _written[0].Replace('\\', '/'));
    }

    [Test]
    public void SessionLog_writeFailure_rewritesTheVerdictAndOmitsTheTrailer()
    {
        // A genuinely unwritable dir — an absent one would just be created, nested and all.
        var s = RunLogFormat.WriteRunLog("Assets/Agent/RunLogs/d3<>|invalid", "x", "Shoot => OK", "body", ".md");

        StringAssert.Contains("=> FAIL", s);
        StringAssert.DoesNotContain("=> OK", s);
        Assert.IsFalse(s.Contains("| log="), "no trailer may point at an artifact that was never written: " + s);
    }
}
