using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;
using UnityEngine.TestTools;

// ReportConsole exists because the MCP read_console door returns only an entry's first line. These pin
// the properties that make it a fix rather than a second opinion:
//
//   1. THE WHOLE ENTRY COMES BACK, multi-line payload included, read live from the Editor console.
//   2. THE BODY/STACK SPLIT IS UNITY'S, not a guess. `callstackTextStartUTF16` is an index Unity
//      computed; a missing or out-of-range one keeps the whole message as body, which is the safe
//      direction. No line is ever discarded for looking like a stack frame.
//   3. TYPE COMES FROM mode BITS, with error beating warning.
//   4. NOTHING IS DROPPED SILENTLY — withheld frames and stripped entries are counted, and a bad
//      filter fails loud.
//
// Two hazards for anyone adding a case here. This fixture must not assert on console CLEANLINESS:
// ReportConsole writes nothing to the console, but the tests around it log freely. And it must not
// assert on entry TEXT under a large `count` — in a full suite run the console carries enough
// entries to push the digest past the inline budget, where the return value is a summary plus a
// Snapshot path and the text is on disk. Keep `count` small when asserting on text; a case that
// passes filtered and fails in the suite is this, not a real defect.
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

    // Only the single separator newline is removed. A trailing blank line the payload meant to have
    // is content, and TrimEnd would have eaten the whole run of them.
    [Test]
    public void SplitAt_keepsABlankLineAtTheBoundary()
    {
        string body, stack;
        // "head" 0-3, newlines at 4/5/6, callstack from index 7.
        ReportConsole.SplitAt("head\n\n\nFoo:Bar ()", 7, out body, out stack);
        Assert.AreEqual("head\n\n", body);   // one separator gone, the deliberate blank kept
        Assert.AreEqual("Foo:Bar ()", stack);
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

    // ClassifyMode against LIVE entries, not against ErrorMask's own constants. Asserting that
    // `1<<20 => Error` is true by construction and stays green even if every bit label is wrong --
    // which is how three unjustified bits got into the mask. This logs one entry of each severity
    // and requires the classification Unity itself would give, so a wrong bit shows up as a wrong
    // answer about a real entry.
    [Test]
    public void ClassifyMode_matchesUnityOnLiveEntriesOfEachSeverity()
    {
        string token = "RCSEV-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        // The error and exception below are the point of the test, so the framework must not treat
        // them as unhandled failures. Scoped tightly and restored.
        LogAssert.ignoreFailingMessages = true;
        try
        {
            Debug.Log(token + " plain log");
            Debug.LogWarning(token + " plain warning");
            Debug.LogError(token + " plain error");
            Debug.LogException(new System.InvalidOperationException(token + " exception"));
        }
        finally
        {
            LogAssert.ignoreFailingMessages = false;
        }

        var kinds = new System.Collections.Generic.Dictionary<string, ReportConsole.EntryKind>();
        var asm = typeof(UnityEditor.Editor).Assembly;
        var les = asm.GetType("UnityEditor.LogEntries");
        var le = asm.GetType("UnityEditor.LogEntry");
        const System.Reflection.BindingFlags S =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        const System.Reflection.BindingFlags I =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var fMsg = le.GetField("message", I);
        var fMode = le.GetField("mode", I);
        var getEntry = les.GetMethod("GetEntryInternal", S);
        int n = (int)les.GetMethod("StartGettingEntries", S).Invoke(null, null);
        try
        {
            var inst = System.Activator.CreateInstance(le);
            for (int i = 0; i < n; i++)
            {
                var args = new object[] { i, inst };
                if (!(bool)getEntry.Invoke(null, args)) continue;
                string m = fMsg.GetValue(args[1]) as string ?? "";
                if (!m.Contains(token)) continue;
                var kind = ReportConsole.ClassifyMode((int)fMode.GetValue(args[1]));
                if (m.Contains("plain log")) kinds["log"] = kind;
                else if (m.Contains("plain warning")) kinds["warning"] = kind;
                else if (m.Contains("plain error")) kinds["error"] = kind;
                else if (m.Contains("exception")) kinds["exception"] = kind;
            }
        }
        finally { les.GetMethod("EndGettingEntries", S).Invoke(null, null); }

        Assert.AreEqual(ReportConsole.EntryKind.Log, kinds["log"]);
        Assert.AreEqual(ReportConsole.EntryKind.Warning, kinds["warning"]);
        Assert.AreEqual(ReportConsole.EntryKind.Error, kinds["error"]);
        Assert.AreEqual(ReportConsole.EntryKind.Error, kinds["exception"]);
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

    // ----- 5. The benign strip: on by default, never silent, never self-defeating ------------

    [Test]
    public void BenignLabel_namesEachKnownFamily()
    {
        Assert.AreEqual("MACS third-party load noise",
            ReportConsole.BenignLabel("<color=#007076>[MACS]</color>: Applying patches"));
        Assert.AreEqual("DestroyBlendTreeRecursive",
            ReportConsole.BenignLabel("something\nDestroyBlendTreeRecursive at foo"));
        Assert.AreEqual("FBX importer inconsistent-result noise",
            ReportConsole.BenignLabel("Import of Foo.fbx gave an inconsistent result"));
        Assert.AreEqual("VRCFury build-progress",
            ReportConsole.BenignLabel("VF.Exceptions: Progress (3/9)"));
    }

    // The predicates must not eat a real diagnostic that merely shares a word with one of them.
    // Each of these is one token away from a family above.
    [Test]
    public void BenignLabel_realDiagnosticIsSignal()
    {
        Assert.IsNull(ReportConsole.BenignLabel("NullReferenceException in AvatarBuilder"));
        Assert.IsNull(ReportConsole.BenignLabel("inconsistent result"));      // no fbx/import co-token
        Assert.IsNull(ReportConsole.BenignLabel("VF.Exceptions: real build failure"));
        Assert.IsNull(ReportConsole.BenignLabel(null));
    }

    // The strip takes the whole FAMILY, real failures included -- these pin that honestly rather than
    // letting the suite imply a safety it does not have. If one of these ever needs to survive, the
    // predicate has to narrow; until then the reported count is the only trace, which is why the
    // summary must always carry it (next test).
    [Test]
    public void BenignLabel_swallowsRealFailuresOfTheSameFamily()
    {
        Assert.AreEqual("MACS third-party load noise",
            ReportConsole.BenignLabel("[MACS] Failed to apply patch: target method missing"));
        Assert.AreEqual("FBX importer inconsistent-result noise",
            ReportConsole.BenignLabel("Import of avatar.fbx failed: inconsistent result in mesh topology"));
        Assert.AreEqual("VRCFury build-progress",
            ReportConsole.BenignLabel("VF.Exceptions: build failed during Progress (4/9)"));
        // Matched via the CALLSTACK, not the body: an exception routed through this frame goes too.
        Assert.AreEqual("DestroyBlendTreeRecursive",
            ReportConsole.BenignLabel("NullReferenceException\nVF.DestroyBlendTreeRecursive (at X.cs:1)"));
    }

    // Stripping must be visible in the summary. A dropped entry whose count is not reported is the
    // silent loss this whole tool exists to end -- the text may go, the number may not.
    [Test]
    public void Report_stripIsNeverSilent()
    {
        Debug.LogWarning("[MACS] Applying patches " + System.Guid.NewGuid().ToString("N").Substring(0, 6));

        // The strip counts are tallied over every matched entry before `count` trims the list, so a
        // small count still reports the full tally -- and keeps the digest inline (see above).
        string result = ReportConsole.Report(types: "all", count: 5);

        StringAssert.Contains("benign-stripped=[", result);
        StringAssert.Contains("MACS third-party load noise", result);
    }

    // Filtering FOR noise by name must still return it, or the filter defeats itself.
    [Test]
    public void Report_filterTextExemptsAnEntryFromTheBenignStrip()
    {
        string token = "RCMACS-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning("[MACS] Applying patches " + token);

        string result = ReportConsole.Report(types: "all", filterText: token, count: 5);

        StringAssert.Contains(token, result);
        StringAssert.Contains("shown=1", result);
    }

    [Test]
    public void Report_stripBenignOff_keepsTheNoise()
    {
        string token = "RCRAW-" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        Debug.LogWarning("[MACS] Applying patches " + token);

        // count is deliberately small: a large one lets ambient console volume push the digest past
        // the inline budget, where the return value is a summary + artifact path and the entry text
        // is on disk instead. The entry just logged is the newest, so a small count still contains it.
        string kept = ReportConsole.Report(types: "all", count: 5, stripBenign: false);
        StringAssert.Contains(token, kept);
    }

    // ----- 6. The Console window's filters bound the read, and must be declared ---------------

    // The most dangerous state this door can be in: a search filter set in the Console window makes
    // GetEntryInternal enumerate almost nothing, so an unannotated digest reports `scanned=0 => OK`
    // on a console full of errors -- a cleaner loss than the truncation this tool replaces. Measured
    // on a live console: 8 entries, 6 with Log hidden, 0 with a search filter. The summary must say so.
    [Test]
    public void Report_declaresTheConsoleWindowsOwnFilterState()
    {
        var asm = typeof(UnityEditor.Editor).Assembly;
        var les = asm.GetType("UnityEditor.LogEntries");
        const System.Reflection.BindingFlags S =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var getFlags = les.GetMethod("get_consoleFlags", S);
        var setFlags = les.GetMethod("set_consoleFlags", S);
        var getText = les.GetMethod("GetFilteringText", S);
        var setText = les.GetMethod("SetFilteringText", S);
        Assert.NotNull(getFlags, "consoleFlags accessor missing — the disclosure cannot work");
        Assert.NotNull(getText, "GetFilteringText missing — the disclosure cannot work");

        int origFlags = (int)getFlags.Invoke(null, null);
        string origText = getText.Invoke(null, null) as string;
        try
        {
            // Unfiltered: nothing to declare.
            setFlags.Invoke(null, new object[] { origFlags | 128 | 256 | 512 });
            setText.Invoke(null, new object[] { "" });
            StringAssert.DoesNotContain("UNREACHED=", ReportConsole.Report(count: 1));

            // Log severity hidden.
            setFlags.Invoke(null, new object[] { (origFlags | 128 | 256 | 512) & ~128 });
            string hidden = ReportConsole.Report(count: 1);
            // The gap is asserted as a COUNT against GetCountsByType (which is not filtered), so the
            // check survives a narrowing mechanism this code never enumerated. The flag name is only
            // the appended cause.
            StringAssert.Contains("UNREACHED=", hidden);
            StringAssert.Contains("log-hidden", hidden);
            StringAssert.Contains("FILTERED VIEW", hidden);

            // Search box set — the total-blackout case.
            setFlags.Invoke(null, new object[] { origFlags | 128 | 256 | 512 });
            setText.Invoke(null, new object[] { "zzz-no-such-text-zzz" });
            string searched = ReportConsole.Report(count: 1);
            StringAssert.Contains("UNREACHED=", searched);
            StringAssert.Contains("search=\"zzz-no-such-text-zzz\"", searched);
            StringAssert.Contains("FILTERED VIEW", searched);
            StringAssert.Contains("scanned=0", searched);   // the blackout case, now declared
        }
        finally
        {
            setText.Invoke(null, new object[] { origText ?? "" });
            setFlags.Invoke(null, new object[] { origFlags });
        }

        Assert.AreEqual(origFlags, (int)getFlags.Invoke(null, null), "console flags must be restored");
        Assert.AreEqual(origText ?? "", getText.Invoke(null, null) as string ?? "", "filter text must be restored");
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
