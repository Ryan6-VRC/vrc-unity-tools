using System.IO;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;

// Both thumbnail doors wrote no RunLog at all — they borrowed RunLogFormat.Sanitize for a temp PNG name and
// nothing else — so the fitting sweep's "which doors were driven" signal was blind to both arcs and End()'s
// restore claim rested on its own return string. The capture paths themselves need a real avatar and (for
// play) the player loop, so what is pinned here is the envelope every verb now routes through.
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

    // The kind-prefixed filename is what makes a RunLog sweep able to tell the arcs apart — the fitting
    // session's signal is exactly "which doors were driven", so begin/shoot/end must not share one name.
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

    // The family's real write-failure contract, asserted rather than assumed: RunLogFormat REPLACES the
    // verdict with a bare FAIL instead of returning the verdict without a trailer, so a line can never assert
    // both a verdict and a failed write. A test written to the intuitive contract would fail against the code.
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
