using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;
using UnityEngine.TestTools;

// ReportConsole exists because the MCP read_console door returns only an entry's first line. These pin the
// three properties that make this tool a fix rather than a second opinion:
//
//   1. NO INTERIOR LINE CAN BE LOST. The stack split scans backwards for a contiguous trailing run of
//      frames, so a payload line can never be dropped for looking frame-ish. The differential test below
//      is revert-only: it is built to FAIL under a forward scan for the first stack-looking line, which is
//      the algorithm that loses a VRCFury list payload.
//   2. TYPE COMES FROM mode BITS, not from substring-matching the message. Text-matching "Exception"
//      mis-tags VRCFury's build-progress chatter as an error.
//   3. A BENIGN STRIP IS NEVER SILENT, and never removes something the caller filtered FOR by name.
//
// Deliberately not asserted here: the RunLog spill path (covered by RunLogFormat's own tests) and the
// live-console read itself beyond one end-to-end round trip — the reflection into UnityEditor.LogEntries
// fails loud by contract, so a drift shows up as a named FAIL rather than an empty result.
public class ReportConsoleTests
{
    // ----- 1. The defect: interior payload lines survive the stack split ---------------------

    // A VRCFury-shaped warning: a header, then a list payload whose lines start uppercase and contain
    // dots (so a forward "first stack-looking line" scan classifies the FIRST payload line as the start
    // of the stack and eats all three), then the real frames. Every payload line must be in the body.
    [Test]
    public void SplitStackSuffix_keepsInteriorPayloadLines_thatLookLikeFrames()
    {
        string full = string.Join("\n", new[]
        {
            "Removed 3 properties from animation clips that targeted objects that do not exist:",
            "Assets/Vendor/Foo/Body.fbx : blendShape.Smile (from Clip_A.anim)",
            "Assets/Vendor/Foo/Hair.fbx : m_IsActive (from Clip_B.anim)",
            "Assets/Vendor/Foo/Tail.fbx : m_LocalScale.x (from Clip_C.anim)",
            "UnityEngine.Debug:LogWarning (object)",
            "VF.Builder.Foo:Run () (at Assets/VRCFury/Foo.cs:12)",
        });

        string stack;
        string body = ReportConsole.SplitStackSuffix(full, out stack);

        StringAssert.Contains("Body.fbx : blendShape.Smile", body);
        StringAssert.Contains("Hair.fbx : m_IsActive", body);
        StringAssert.Contains("Tail.fbx : m_LocalScale.x", body);
        StringAssert.Contains("UnityEngine.Debug:LogWarning", stack);
        StringAssert.DoesNotContain("UnityEngine.Debug:LogWarning", body);
    }

    // No frames at all: nothing may be split off. This is the case where a wrong split is most costly,
    // because there is no stack to justify losing anything.
    [Test]
    public void SplitStackSuffix_noFrames_returnsWholeMessage()
    {
        string full = "line one\nline two\nline three";
        string stack;
        string body = ReportConsole.SplitStackSuffix(full, out stack);

        Assert.AreEqual(full, body);
        Assert.AreEqual("", stack);
    }

    // An entry always has a header, so a message that is entirely frames keeps its first line as body
    // rather than collapsing to an empty digest.
    [Test]
    public void SplitStackSuffix_allFrames_keepsFirstLineAsBody()
    {
        string full = "UnityEngine.Debug:Log (object)\nFoo.Bar:Baz () (at Assets/X.cs:3)";
        string stack;
        string body = ReportConsole.SplitStackSuffix(full, out stack);

        Assert.AreEqual("UnityEngine.Debug:Log (object)", body);
        StringAssert.Contains("Foo.Bar:Baz", stack);
    }

    [Test]
    public void SplitStackSuffix_nullOrEmpty_isSafe()
    {
        string stack;
        Assert.AreEqual("", ReportConsole.SplitStackSuffix(null, out stack));
        Assert.AreEqual("", stack);
        Assert.AreEqual("", ReportConsole.SplitStackSuffix("", out stack));
        Assert.AreEqual("", stack);
    }

    // Prose that merely contains dots and capitals is not a frame — the narrow predicate is what keeps
    // property 1 true. A sentence ending in a parenthetical is the nearest miss.
    [Test]
    public void SplitStackSuffix_prosePayload_isNotMistakenForFrames()
    {
        string full = "Build failed.\nThe avatar Foo.Bar was not found (check the scene)";
        string stack;
        string body = ReportConsole.SplitStackSuffix(full, out stack);

        Assert.AreEqual(full, body);
        Assert.AreEqual("", stack);
    }

    // ----- 2. Type comes from mode bits ------------------------------------------------------

    [Test]
    public void ClassifyMode_readsBits_notText()
    {
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 0));   // kError
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 17));  // kScriptingException
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 8));   // kScriptingError
        Assert.AreEqual(ReportConsole.EntryKind.Warning, ReportConsole.ClassifyMode(1 << 9)); // kScriptingWarning
        Assert.AreEqual(ReportConsole.EntryKind.Warning, ReportConsole.ClassifyMode(1 << 7)); // kAssetImportWarning
        Assert.AreEqual(ReportConsole.EntryKind.Log, ReportConsole.ClassifyMode(1 << 10));    // kScriptingLog
    }

    // An entry carrying both bits is an error: under-reporting severity is the dangerous direction.
    [Test]
    public void ClassifyMode_errorBitsWinOverWarningBits()
    {
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode((1 << 0) | (1 << 9)));
    }

    // ----- 3. Benign classification ----------------------------------------------------------

    [Test]
    public void BenignLabel_namesEachKnownPattern()
    {
        Assert.AreEqual("MACS third-party load noise",
            ReportConsole.BenignLabel("<color=#007076>[MACS]</color>: Applying patches", ""));
        Assert.AreEqual("DestroyBlendTreeRecursive",
            ReportConsole.BenignLabel("something", "DestroyBlendTreeRecursive at foo"));
        Assert.AreEqual("FBX importer inconsistent-result noise",
            ReportConsole.BenignLabel("Import of Foo.fbx gave an inconsistent result", ""));
        Assert.AreEqual("VRCFury build-progress",
            ReportConsole.BenignLabel("VF.Exceptions: Progress (3/9)", ""));
    }

    // The predicate must not eat a real error that merely shares a word with one of the patterns.
    [Test]
    public void BenignLabel_realDiagnosticIsSignal()
    {
        Assert.IsNull(ReportConsole.BenignLabel("NullReferenceException in AvatarBuilder", ""));
        Assert.IsNull(ReportConsole.BenignLabel("inconsistent result", "")); // no fbx/import co-token
        Assert.IsNull(ReportConsole.BenignLabel("VF.Exceptions: real build failure", ""));
    }

    [Test]
    public void BenignLabel_nullsAreSafe()
    {
        Assert.IsNull(ReportConsole.BenignLabel(null, null));
    }

    // ----- End-to-end: a real multi-line entry survives the live console read ------------------

    // The whole point, against the actual Editor console: log a multi-line warning, read it back, and
    // require every payload line. read_console returns only the first of these.
    [Test]
    public void Report_returnsEveryLineOfAMultiLineEntry()
    {
        string token = "RCTEST-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning(token + " header\nPAYLOAD-A alpha\nPAYLOAD-B beta\nPAYLOAD-C gamma");

        string result = ReportConsole.Report(types: "warning", filterText: token, count: 5);

        StringAssert.Contains(token + " header", result);
        StringAssert.Contains("PAYLOAD-A alpha", result);
        StringAssert.Contains("PAYLOAD-B beta", result);
        StringAssert.Contains("PAYLOAD-C gamma", result);
        StringAssert.Contains("=> OK", result);
    }

    // filterText matches the FULL entry, not just the header — otherwise a caller cannot search for the
    // very payload text this tool exists to surface.
    [Test]
    public void Report_filterTextMatchesTheBody_notJustTheHeader()
    {
        string token = "RCBODY-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning("unremarkable header line\nneedle-" + token + " in the body");

        string result = ReportConsole.Report(types: "warning", filterText: "needle-" + token, count: 5);

        StringAssert.Contains("needle-" + token, result);
        StringAssert.Contains("shown=1", result);
    }

    [Test]
    public void Report_unknownType_failsLoudAndNamesIt()
    {
        LogAssert.ignoreFailingMessages = true;
        try
        {
            string result = ReportConsole.Report(types: "wraning");
            StringAssert.Contains("[ReportConsole] FAIL", result);
            StringAssert.Contains("wraning", result);
        }
        finally
        {
            LogAssert.ignoreFailingMessages = false;
        }
    }
}
