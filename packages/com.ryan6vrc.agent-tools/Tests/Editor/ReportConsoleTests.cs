using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

// ReportConsole exists because the MCP read_console door returns only an entry's first line. These pin
// the properties that make it a fix rather than a second opinion:
//
//   1. THE WHOLE ENTRY COMES BACK, multi-line payload included, read live from the Editor console.
//   2. THE BODY/STACK SPLIT IS UNITY'S, not a guess. `callstackTextStartUTF16` is an index Unity
//      computed; a missing or out-of-range one keeps the whole message as body, which is the safe
//      direction. No line is ever discarded for looking like a stack frame.
//   3. TYPE COMES FROM mode BITS, with error beating warning.
//   4. NOTHING IS DROPPED SILENTLY — withheld frames are counted, and a bad filter fails loud.
//
// Deliberately not asserted: the Snapshot spill path (RunLogFormat owns its own tests). Note this
// fixture must not assert on console CLEANLINESS — ReportConsole writes nothing to the console, but
// the tests around it log freely.
public class ReportConsoleTests
{
    // ----- 1. The defect: every line of a multi-line entry survives the live read -------------

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

    // The shape this tool was built for: a header plus a list payload, logged through Debug so the
    // entry carries a real callstack after it. Every payload line must land in the BODY, above the
    // "--- stack ---" marker — the split must not eat into the list.
    [Test]
    public void Report_listPayloadStaysAboveTheStack()
    {
        string token = "RCLIST-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning(token + " Removed 3 properties from animation clips that targeted objects that do not exist:"
            + "\n(Armature/Hips GameObject m_IsActive) from ClipA"
            + "\n(Body SkinnedMeshRenderer blendShape.Smile) from ClipB"
            + "\n(Root/Tail Transform m_LocalScale.x) from ClipC");

        string result = ReportConsole.Report(types: "warning", filterText: token, count: 5);

        int stackAt = result.IndexOf("--- stack ---", System.StringComparison.Ordinal);
        Assert.Greater(stackAt, 0, "a Debug-logged entry should carry a callstack");
        string body = result.Substring(0, stackAt);
        StringAssert.Contains("m_IsActive) from ClipA", body);
        StringAssert.Contains("blendShape.Smile) from ClipB", body);
        StringAssert.Contains("m_LocalScale.x) from ClipC", body);
    }

    // filterText matches the FULL entry, not just the header — otherwise a caller cannot search for the
    // payload text this tool exists to surface.
    [Test]
    public void Report_filterTextMatchesTheBody_notJustTheHeader()
    {
        string token = "RCBODY-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning("unremarkable header line\nneedle-" + token + " in the body");

        string result = ReportConsole.Report(types: "warning", filterText: "needle-" + token, count: 5);

        StringAssert.Contains("needle-" + token, result);
        StringAssert.Contains("shown=1", result);
    }

    // ----- 2. The split is Unity's index, and its absence is safe ----------------------------

    [Test]
    public void SplitAt_usesTheReportedIndex()
    {
        string body, stack;
        ReportConsole.SplitAt("header\npayload line\nUnityEngine.Debug:Log ()", 19, out body, out stack);

        Assert.AreEqual("header\npayload line", body);
        Assert.AreEqual("UnityEngine.Debug:Log ()", stack);
    }

    // No callstack recorded: the whole message is body. Nothing may be split off on a guess.
    [Test]
    public void SplitAt_zeroIndex_keepsWholeMessageAsBody()
    {
        string body, stack;
        ReportConsole.SplitAt("line one\nline two\nline three", 0, out body, out stack);

        Assert.AreEqual("line one\nline two\nline three", body);
        Assert.AreEqual("", stack);
    }

    // An index Unity reports that we cannot honour (drift, or a truncated message) must degrade to
    // "keep everything" rather than throwing or trimming blind.
    [Test]
    public void SplitAt_outOfRangeIndex_keepsWholeMessageAsBody()
    {
        string body, stack;
        ReportConsole.SplitAt("short", 9999, out body, out stack);
        Assert.AreEqual("short", body);
        Assert.AreEqual("", stack);

        ReportConsole.SplitAt(null, 5, out body, out stack);
        Assert.AreEqual("", body);
        Assert.AreEqual("", stack);
    }

    // ----- 3. Type comes from mode bits ------------------------------------------------------

    [Test]
    public void ClassifyMode_readsBits_notText()
    {
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 0));   // Error
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 17));  // ScriptingException
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 8));   // ScriptingError
        Assert.AreEqual(ReportConsole.EntryKind.Warning, ReportConsole.ClassifyMode(1 << 9)); // ScriptingWarning
        Assert.AreEqual(ReportConsole.EntryKind.Warning, ReportConsole.ClassifyMode(1 << 7)); // AssetImportWarning
        Assert.AreEqual(ReportConsole.EntryKind.Log, ReportConsole.ClassifyMode(1 << 10));    // ScriptingLog
    }

    // The error bits that are easy to omit: an entry carrying one of these must not read as Log, or
    // `types:"error"` misses it silently. Verified against UnityEditor.ConsoleWindow.Mode.
    [Test]
    public void ClassifyMode_coversTheLessObviousErrorBits()
    {
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 4));  // Fatal
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 13)); // StickyError
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 20)); // GraphCompileError
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode(1 << 22)); // VisualScriptingError
    }

    [Test]
    public void ClassifyMode_errorBitsWinOverWarningBits()
    {
        Assert.AreEqual(ReportConsole.EntryKind.Error, ReportConsole.ClassifyMode((1 << 0) | (1 << 9)));
    }

    // ----- 4. Nothing is dropped silently -----------------------------------------------------

    // Suppressed frames must still be announced, or `includeStackTrace:false` reproduces the
    // header-only read this tool replaces.
    [Test]
    public void Report_withoutStackTrace_countsTheWithheldFrames()
    {
        string token = "RCSTK-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning(token + " header only");

        string result = ReportConsole.Report(types: "warning", filterText: token, count: 5, includeStackTrace: false);

        StringAssert.Contains(token + " header only", result);
        StringAssert.Contains("stack lines]", result);
        StringAssert.DoesNotContain("--- stack ---", result);
    }

    [Test]
    public void Report_unknownType_failsLoudAndNamesIt()
    {
        string result = ReportConsole.Report(types: "wraning");
        StringAssert.Contains("[ReportConsole] FAIL", result);
        StringAssert.Contains("wraning", result);
    }

    // A types string naming nothing must not quietly widen to `all` — that would hand back errors to a
    // caller who asked for something else.
    [Test]
    public void Report_emptyTypeList_failsRatherThanMeaningAll()
    {
        StringAssert.Contains("[ReportConsole] FAIL", ReportConsole.Report(types: ","));
    }

    // The door must not write to the console it reads: a logging reader pollutes its own next read, and
    // a logged error is indistinguishable from a real one to PlayGate and to any clean-console check.
    // Both the success and the failure path are checked — the failure path is where a LogError is most
    // tempting and most damaging.
    [Test]
    public void Report_writesNothingToTheConsole()
    {
        string token = "RCQUIET-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning(token + " marker");

        int before = CountEntries();
        ReportConsole.Report(types: "all", filterText: token, count: 5);
        ReportConsole.Report(types: "bogus-type-name");
        int after = CountEntries();

        Assert.AreEqual(before, after, "ReportConsole must not add console entries (success or FAIL path)");
    }

    private static int CountEntries()
    {
        var asm = typeof(UnityEditor.Editor).Assembly;
        var t = asm.GetType("UnityEditor.LogEntries");
        const System.Reflection.BindingFlags S =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        int n = (int)t.GetMethod("StartGettingEntries", S).Invoke(null, null);
        t.GetMethod("EndGettingEntries", S).Invoke(null, null);
        return n;
    }
}
