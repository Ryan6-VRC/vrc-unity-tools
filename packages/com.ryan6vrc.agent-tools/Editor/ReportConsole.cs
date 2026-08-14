using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only digest of the Unity Editor console — every line of every entry.
    ///
    /// This exists because the MCP <c>read_console</c> door returns only an entry's FIRST line: it
    /// reads the whole message out of Unity and then discards lines 2..N. A build warning whose
    /// payload is a list (VRCFury names each dropped binding and its source clip in the body)
    /// arrives there as its header alone — the count survives and the diagnosis does not.
    ///
    /// Unity hands over each entry whole. <c>LogEntry.message</c> holds the body and the callstack
    /// concatenated, and <c>LogEntry.callstackTextStartUTF16</c> is the exact index where the
    /// callstack begins — so the two are separated by reading a number Unity already computed, never
    /// by guessing at which lines look like stack frames. Guessing is what loses payload lines; there
    /// is no heuristic here to get wrong.
    ///
    /// What it does NOT hand over is the whole console: this enumerates the Console *window's* rows,
    /// so its filters bound the read. `GetCountsByType` is not filtered, so the gap is detectable
    /// and is reported as a count (see <see cref="ConsoleFilterNote"/>).
    ///
    /// INSPECTION ONLY. It does not write to the console it reads — no door here logs, because a
    /// logging console reader pollutes its own next read (and an error it logs is indistinguishable
    /// from a real one to `PlayGate` and to any "console is clean" check). The return value is the
    /// only channel. The one caveat is indirect: past the inline budget the digest spills through
    /// `RunLogFormat.WriteRunLog`, whose asset import can make Unity's own importers log. Nothing this
    /// door writes lands in the console; something it triggers may.
    /// Clearing is not mirrored here; `read_console`'s `clear` action was its own.
    /// </summary>
    [AgentTool]
    public static class ReportConsole
    {
        /// <summary>Inline character budget. Past this the digest spills to a Snapshot artifact and the
        /// summary carries its path in-band, so a noisy console cannot blow out the caller's context.</summary>
        private const int InlineBudget = 12000;

        // ----- Public API ---------------------------------------------------------------------

        /// <summary>
        /// Digest the most recent console entries, oldest first. Returns a one-line summary followed by
        /// the entries; when the text exceeds the inline budget the entries go to a Snapshot instead and
        /// the summary ends with the artifact path in-band (<c>… =&gt; OK | log=&lt;path&gt;</c>).
        /// </summary>
        /// <param name="types">Comma-separated subset of <c>error</c>/<c>warning</c>/<c>log</c>, or
        /// <c>all</c>. Anything else is a bare <c>[ReportConsole] FAIL: …</c> naming it.</param>
        /// <param name="filterText">Keep only entries whose text contains this (ordinal, case-sensitive).
        /// The whole ENTRY is matched — body and callstack — not just the header, so a frame name is a
        /// valid search term. A match also exempts the entry from <paramref name="stripBenign"/>.</param>
        /// <param name="count">Maximum ROWS returned, taken from the newest end. Clamped to 1..500. With
        /// <paramref name="collapseDuplicates"/> on, a row can stand for several identical entries, so the
        /// returned window may reach further back in time than <paramref name="count"/> entries.</param>
        /// <param name="includeStackTrace">Include each entry's callstack. On by default: for an
        /// exception the frames are the diagnosis, and withholding them by default would reproduce the
        /// header-only read this tool exists to replace.</param>
        /// <param name="stripBenign">Drop known-benign console noise and name the counts in the summary.
        /// On by default, and that default is load-bearing: Unity types these entries as genuine errors
        /// (VRCFury routes build progress through <c>VF.Exceptions</c>, which really does carry
        /// <c>kScriptingException</c>), so no amount of faithful type-reading separates them from real
        /// ones, and an unfiltered error read during a build buries the diagnosis it was run to find.
        /// This is not a tidiness default — it was added because the noise repeatedly cost real sessions.
        /// It stays honest by never being silent: the summary names every label and count it removed, so
        /// a dropped entry is always visible as a number even when its text is gone. An entry kept
        /// BECAUSE it matched <paramref name="filterText"/> is exempt — filtering for noise by name and
        /// then having it stripped would defeat the filter.</param>
        /// <param name="collapseDuplicates">Fold byte-identical entries (body + callstack) into one row carrying
        /// a repeat count. On by default, on the same reasoning as <paramref name="stripBenign"/>: a flood of one
        /// repeated message spends the whole <paramref name="count"/> budget teaching one fact (5,672 identical
        /// entries returned 60 verbatim copies in a measured session), and the collapse is never silent — the
        /// summary carries <c>collapsed=</c> and each folded row carries <c>×N</c>. Two bounds worth knowing.
        /// It keeps the LAST occurrence, because <paramref name="count"/> clamps from the newest end and an
        /// oldest-representative row could be clamped away, erasing a recurrence from a read taken to see it.
        /// And a filtered read is exempt WHOLESALE — passing <paramref name="filterText"/> disables collapse for
        /// that call rather than per entry, so the two cannot be combined — which is what keeps the tagged-probe
        /// idiom (`emulator.md`: log a tagged line every N frames, read it back by tag) returning its whole
        /// series, since there the identical repeats ARE the signal. Repeats need not be adjacent: a group folds
        /// onto its newest position, so a read taken to establish SEQUENCE should pass false. Distinct from Unity's own Console "Collapse"
        /// toggle, which hides entries from this read entirely and shows up as <c>UNREACHED=</c>.</param>
        public static string Report(
            string types = "all",
            string filterText = null,
            int count = 20,
            bool includeStackTrace = true,
            bool stripBenign = true,
            bool collapseDuplicates = true)
        {
            int mask;
            string badTypes;
            if (!TryParseTypes(types, out mask, out badTypes))
                return "[ReportConsole] FAIL: unrecognized types '" + badTypes + "' — expects a comma-separated subset of error/warning/log, or all";

            if (count < 1) count = 1;
            if (count > 500) count = 500;

            List<Entry> entries;
            int unreadable;
            string readError;
            if (!TryReadEntries(out entries, out unreadable, out readError))
                return "[ReportConsole] FAIL: " + readError;

            bool filtering = !string.IsNullOrEmpty(filterText);
            int scanned = entries.Count;
            var kept = new List<Entry>();
            var benign = new Dictionary<string, int>();
            foreach (var e in entries)
            {
                if ((TypeBit(e.Kind) & mask) == 0) continue;

                bool matchedFilter = false;
                if (!string.IsNullOrEmpty(filterText))
                {
                    if (e.Full.IndexOf(filterText, StringComparison.Ordinal) < 0) continue;
                    matchedFilter = true;
                }

                if (stripBenign && !matchedFilter)
                {
                    string label = BenignLabel(e.Full);
                    if (label != null)
                    {
                        int seen;
                        benign[label] = benign.TryGetValue(label, out seen) ? seen + 1 : 1;
                        continue;
                    }
                }
                kept.Add(e);
            }

            int matched = kept.Count;
            // Rows carry their own repeat count so the pairing survives the clamp below — a parallel list
            // would have to be trimmed in lockstep, and the one that got out of step would misattribute a
            // count to the wrong message.
            var rows = CollapseDuplicates(kept, collapseDuplicates && !filtering, out int collapsed);
            if (rows.Count > count) rows.RemoveRange(0, rows.Count - count);

            var body = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                RenderEntry(rows[i].Key, i + 1, includeStackTrace, body);
                if (rows[i].Value > 1) body.Append("    (×").Append(rows[i].Value).Append(" identical, collapsed)\n");
            }

            // Neither a stripped entry nor one Unity declined to hand over is absorbed into a
            // clean-looking count: both are named here, so nothing this door removed is invisible.
            // The Console window's own filter state comes first because it bounds everything after
            // it — a `scanned=` computed under a search filter is a view, not the console.
            // One number per stage: scanned -> matched -> collapsed -> shown. `collapsed` is rows REMOVED, not a
            // before/after pair — `matched` already carries the before.
            string summary = "[ReportConsole]" + ConsoleFilterNote(scanned)
                + " scanned=" + scanned + " matched=" + matched
                + (collapsed > 0 ? " collapsed=" + collapsed : "") + " shown=" + rows.Count
                + BenignNote(benign) + (unreadable > 0 ? " unreadable=" + unreadable : "") + " => OK";

            if (body.Length <= InlineBudget)
                return summary + "\n" + body;

            return RunLogFormat.WriteRunLog(
                RunLogFormat.SnapshotDir, "report-console", summary,
                "# ReportConsole\n" + summary + "\n\n" + body, ".md");
        }

        // ----- Entry model --------------------------------------------------------------------

        /// <summary>One console entry. <see cref="Full"/> is Unity's verbatim text and is what
        /// <c>filterText</c> matches; <see cref="Body"/> and <see cref="Stack"/> are its two halves,
        /// split at the index Unity reports.</summary>
        public struct Entry
        {
            public EntryKind Kind;
            public string Full;
            public string Body;
            public string Stack;
            public string File;
            public int Line;
        }

        public enum EntryKind { Log, Warning, Error }

        /// <summary>Fold byte-identical entries into one row carrying its repeat count, keeping each group's
        /// LAST occurrence in that occurrence's position. Keeping the last is the load-bearing half: `count`
        /// clamps from the newest end, so an oldest-representative row for a message that recurred a second ago
        /// could be clamped away entirely — the recurrence would vanish from a read taken to see it.
        /// Identity is body + callstack, so two entries that differ only in where they were thrown stay apart.
        /// Pure over its input, so both properties are assertable without a console.</summary>
        internal static List<KeyValuePair<Entry, int>> CollapseDuplicates(List<Entry> kept, bool enabled, out int collapsed)
        {
            var rows = new List<KeyValuePair<Entry, int>>();
            collapsed = 0;
            if (!enabled)
            {
                foreach (var e in kept) rows.Add(new KeyValuePair<Entry, int>(e, 1));
                return rows;
            }

            var counts = new Dictionary<string, int>();
            foreach (var e in kept)
            {
                var key = e.Body + " " + e.Stack;
                int seen;
                counts[key] = counts.TryGetValue(key, out seen) ? seen + 1 : 1;
            }
            // Walk backwards so the FIRST time a key is seen from the newest end is its last occurrence, then
            // restore chronological order — the row lands where its newest instance sat.
            var emitted = new HashSet<string>();
            for (int i = kept.Count - 1; i >= 0; i--)
            {
                var key = kept[i].Body + " " + kept[i].Stack;
                if (!emitted.Add(key)) { collapsed++; continue; }
                rows.Add(new KeyValuePair<Entry, int>(kept[i], counts[key]));
            }
            rows.Reverse();
            return rows;
        }

        // ----- Console read (reflection into UnityEditor.LogEntries) ---------------------------

        /// <summary>Read every console entry, oldest first. Returns false with a caller-facing reason when
        /// the internal API has drifted — a rename upstream must fail loud, never read as an empty console.
        /// <paramref name="unreadable"/> counts rows Unity refused to hand over, so they can be reported
        /// rather than silently skipped.</summary>
        private static bool TryReadEntries(out List<Entry> entries, out int unreadable, out string error)
        {
            entries = null;
            unreadable = 0;
            error = null;

            var asm = typeof(UnityEditor.Editor).Assembly;
            var logEntriesType = asm.GetType("UnityEditor.LogEntries");
            var logEntryType = asm.GetType("UnityEditor.LogEntry");
            if (logEntriesType == null || logEntryType == null)
            {
                error = "UnityEditor.LogEntries/LogEntry not found — internal console API moved";
                return false;
            }

            const BindingFlags Statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var start = logEntriesType.GetMethod("StartGettingEntries", Statics);
            var end = logEntriesType.GetMethod("EndGettingEntries", Statics);
            var getEntry = logEntriesType.GetMethod("GetEntryInternal", Statics);
            var fMessage = logEntryType.GetField("message", Fields);
            var fMode = logEntryType.GetField("mode", Fields);
            var fFile = logEntryType.GetField("file", Fields);
            var fLine = logEntryType.GetField("line", Fields);
            var fStackStart = logEntryType.GetField("callstackTextStartUTF16", Fields);
            if (start == null || end == null || getEntry == null
                || fMessage == null || fMode == null || fFile == null || fLine == null || fStackStart == null)
            {
                error = "console reflection failed — StartGettingEntries/EndGettingEntries/GetEntryInternal and "
                    + "LogEntry.{message,mode,file,line,callstackTextStartUTF16} must all resolve (internal API drift)";
                return false;
            }

            var list = new List<Entry>();
            // StartGettingEntries is invoked OUTSIDE the try: if it throws, it never acquired the
            // console lock, and a finally that called EndGettingEntries anyway would be an unbalanced
            // end on the one API whose imbalance this pairing exists to prevent. Everything after a
            // successful Start — the cast included — belongs inside, so drift cannot strand the lock.
            object rawTotal;
            try
            {
                rawTotal = start.Invoke(null, null);
            }
            catch (Exception ex)
            {
                error = "console reflection failed at StartGettingEntries: " + ex.Message;
                return false;
            }

            try
            {
                int total = (int)rawTotal;
                var instance = Activator.CreateInstance(logEntryType);
                for (int i = 0; i < total; i++)
                {
                    var args = new object[] { i, instance };
                    if (!(bool)getEntry.Invoke(null, args)) { unreadable++; continue; }
                    var boxed = args[1];

                    string full = fMessage.GetValue(boxed) as string ?? string.Empty;
                    int stackStart = (int)fStackStart.GetValue(boxed);
                    string bodyText, stack;
                    SplitAt(full, stackStart, out bodyText, out stack);

                    list.Add(new Entry
                    {
                        Kind = ClassifyMode((int)fMode.GetValue(boxed)),
                        Full = full,
                        Body = bodyText,
                        Stack = stack,
                        File = fFile.GetValue(boxed) as string ?? string.Empty,
                        Line = (int)fLine.GetValue(boxed),
                    });
                }
            }
            catch (Exception ex)
            {
                // Field types drifting under us (mode becoming uint, say) would otherwise escape as a
                // raw TargetInvocationException, which through execute_code reads as an opaque MCP
                // failure rather than this door's named FAIL. Fail loud, in the documented shape.
                error = "console reflection failed during read: " + ex.Message;
                return false;
            }
            finally
            {
                end.Invoke(null, null);
            }

            entries = list;
            return true;
        }

        // ----- Pure helpers (unit-tested directly) ---------------------------------------------

        /// <summary>Split <paramref name="full"/> at the callstack index Unity reported. An index of 0 or
        /// one outside the string means Unity recorded no callstack, and the whole message is body — the
        /// safe direction, since a body line is never discarded for looking like a frame.</summary>
        public static void SplitAt(string full, int stackStart, out string body, out string stack)
        {
            full = full ?? string.Empty;
            if (stackStart <= 0 || stackStart > full.Length)
            {
                body = full;
                stack = string.Empty;
                return;
            }
            // Remove at most the ONE separator newline on each side. TrimEnd/TrimStart would eat a run,
            // silently deleting a blank line the payload meant to contain — a small version of exactly
            // the loss this tool exists to end.
            body = TrimOneNewlineEnd(full.Substring(0, stackStart));
            stack = TrimOneNewlineEnd(TrimOneNewlineStart(full.Substring(stackStart)));
        }

        /// <summary>Drop one trailing newline (CRLF counts as one), never a run.</summary>
        private static string TrimOneNewlineEnd(string s)
        {
            if (s.EndsWith("\r\n", StringComparison.Ordinal)) return s.Substring(0, s.Length - 2);
            if (s.EndsWith("\n", StringComparison.Ordinal) || s.EndsWith("\r", StringComparison.Ordinal))
                return s.Substring(0, s.Length - 1);
            return s;
        }

        /// <summary>Drop one leading newline (CRLF counts as one), never a run.</summary>
        private static string TrimOneNewlineStart(string s)
        {
            if (s.StartsWith("\r\n", StringComparison.Ordinal)) return s.Substring(2);
            if (s.StartsWith("\n", StringComparison.Ordinal) || s.StartsWith("\r", StringComparison.Ordinal))
                return s.Substring(1);
            return s;
        }

        // LogEntry.mode bit flags, mirroring UnityEditor.ConsoleWindow.Mode. This is the authoritative
        // source for an entry's type; Unity's own console classifies the same way. (It is not a fix for
        // VRCFury's build-progress chatter reading as an error — that really does carry kScriptingException,
        // so it is genuinely error-typed here too. Judge build health from log text, as verify.md says.)
        private const int ModeError = 1 << 0;
        private const int ModeAssert = 1 << 1;
        private const int ModeFatal = 1 << 4;
        private const int ModeAssetImportError = 1 << 6;
        private const int ModeAssetImportWarning = 1 << 7;
        private const int ModeScriptingError = 1 << 8;
        private const int ModeScriptingWarning = 1 << 9;
        private const int ModeScriptCompileError = 1 << 11;
        private const int ModeScriptCompileWarning = 1 << 12;
        private const int ModeScriptingException = 1 << 17;
        private const int ModeScriptingAssertion = 1 << 21;

        // Bits 13, 20 and 22 are deliberately absent. They read as StickyError / GraphCompileError /
        // VisualScriptingError in ConsoleWindow.Mode, but Unity's LogMessageFlags names 13 and 22
        // kStickyLog and kStacktraceIsPostprocessed — and bit 22 is measurably set on an ordinary
        // Debug.LogException, which is not a Visual Scripting entry. Two enums overlay this one int
        // with different meanings, so the name is not evidence. Measured over a live console, adding
        // them changes no classification (every such entry already carries bit 8 or 17), so they buy
        // nothing and would promote a log to an error if the LogMessageFlags reading is the right one.
        private const int ErrorMask = ModeError | ModeAssert | ModeFatal | ModeAssetImportError
            | ModeScriptingError | ModeScriptCompileError | ModeScriptingException | ModeScriptingAssertion;
        private const int WarningMask = ModeAssetImportWarning | ModeScriptingWarning | ModeScriptCompileWarning;

        /// <summary>Map a <c>LogEntry.mode</c> bitfield to its console type. Error bits win over warning
        /// bits — under-reporting severity is the dangerous direction.</summary>
        public static EntryKind ClassifyMode(int mode)
        {
            if ((mode & ErrorMask) != 0) return EntryKind.Error;
            if ((mode & WarningMask) != 0) return EntryKind.Warning;
            return EntryKind.Log;
        }

        /// <summary>
        /// Report how many entries the console holds that this read could not reach, else "".
        ///
        /// The problem: <c>StartGettingEntries</c>/<c>GetEntryInternal</c> enumerate the Console
        /// *window's* rows, and Unity exposes no unfiltered enumerator — every row accessor indexes
        /// that same view. So the window's LogLevel toggles, Collapse and search box all bound the
        /// read. Measured on a console holding 8 entries: 6 with Log hidden, and <b>0 with a search
        /// filter set</b>, at which point an unannotated digest reads <c>scanned=0 … =&gt; OK</c> and
        /// certifies a console full of errors as clean.
        ///
        /// The fix: <c>GetCountsByType</c> is <b>not</b> filtered — measured, it held 4/2/2 across
        /// every state above while the enumerable count fell 8 → 6 → 0. So the true total is readable
        /// even when the entries are not, and the gap is reported as a NUMBER rather than inferred
        /// from flags. That distinction is the point: this is an outcome check, so it catches any
        /// narrowing — including a mechanism this code never enumerated — where a flag-reading check
        /// only catches the ones it thought to look for. The flags are still appended, as the cause.
        ///
        /// Reported, never corrected: clearing the operator's filters would mutate UI state this door
        /// has no business touching. When the hidden entries themselves are needed,
        /// <c>Application.consoleLogPath</c> (Editor.log) is the unfiltered text of record — process-
        /// wide, unstructured and cross-session, so a fallback rather than a second door.
        /// </summary>
        /// <param name="enumerated">How many entries this read actually saw.</param>
        public static string ConsoleFilterNote(int enumerated)
        {
            var les = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
            if (les == null) return "";
            const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var byType = les.GetMethod("GetCountsByType", S);
            if (byType == null) return " console-total=[unknown: GetCountsByType missing]";

            int total;
            try
            {
                var args = new object[] { 0, 0, 0 };
                byType.Invoke(null, args);
                total = (int)args[0] + (int)args[1] + (int)args[2];
            }
            catch (Exception ex)
            {
                return " console-total=[unknown: " + ex.Message + "]";
            }

            if (total <= enumerated) return "";
            return " UNREACHED=" + (total - enumerated) + "/" + total
                + " entries are hidden from this read by the Console window"
                + ConsoleFilterCause(les, S) + " — THIS IS A FILTERED VIEW";
        }

        /// <summary>The Console window's narrowing settings, as the likely cause of a gap. Best-effort
        /// and never the finding itself: the count discrepancy is what is asserted, this only explains
        /// it, so a cause this code cannot name still leaves the gap reported.</summary>
        private static string ConsoleFilterCause(Type les, BindingFlags S)
        {
            try
            {
                var getFlags = les.GetMethod("get_consoleFlags", S);
                var getText = les.GetMethod("GetFilteringText", S);
                if (getFlags == null || getText == null) return "";
                int flags = (int)getFlags.Invoke(null, null);
                string text = getText.Invoke(null, null) as string;
                var causes = new List<string>();
                // ConsoleWindow.ConsoleFlags: Collapse=1, LogLevelLog=128, Warning=256, Error=512.
                if (!string.IsNullOrEmpty(text)) causes.Add("search=\"" + text + "\"");
                if ((flags & 128) == 0) causes.Add("log-hidden");
                if ((flags & 256) == 0) causes.Add("warning-hidden");
                if ((flags & 512) == 0) causes.Add("error-hidden");
                if ((flags & 1) != 0) causes.Add("collapse");
                return causes.Count == 0 ? "" : " [" + string.Join(", ", causes.ToArray()) + "]";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// The label of the known-benign noise family matching this entry, or null when it is signal.
        /// <paramref name="full"/> is the entry's whole text (body and callstack) — several of these
        /// families identify themselves in the stack, not the message.
        ///
        /// These are substring heuristics against third-party and importer output, so they are
        /// re-validated by reading that source, never by a green test run.
        ///
        /// They match the FAMILY, not a safe subset of it — a real failure from one of these sources is
        /// dropped along with its chatter. `[MACS]` takes that package's `Failed to apply patch` too
        /// (deliberately: the whole source is noise here); the others match the callstack as well
        /// as the body, so an exception routed through one of those frames goes with them. That is the
        /// accepted cost of a default-on strip, and the reason the summary always reports the counts:
        /// when one of these families is implicated in a real failure, the count is the trace that says
        /// to re-run with <c>stripBenign: false</c>. `ReportConsoleTests` pins the collisions by name.
        ///
        /// A family is keyed on the patch/asset TARGET it fails against, never on the patching LIBRARY.
        /// Measured 2026-08-13: NDMF, Modular Avatar and VRCFury all apply Harmony patches from
        /// <c>[InitializeOnLoadMethod]</c>, so a family keyed on <c>HarmonyLib</c> frames at startup would
        /// strip a failed VRCFury patch — which changes what every later build does — by default, under a
        /// one-line count. A library is not a source.
        /// </summary>
        public static string BenignLabel(string full)
        {
            string s = full ?? string.Empty;
            // Third-party load chatter (com.mcardellje.macs), Error-typed and Log-typed alike.
            if (s.Contains("[MACS]")) return "MACS third-party load noise";
            // MACS's Harmony patch failure at editor startup. It carries NO [MACS] token — it is a bare
            // HarmonyLib exception — so the family is keyed on the patch target it names. That target is a
            // real UnityEditor API, so the bare name alone would eat any genuine error mentioning it
            // (our own controller tooling included); the "Parameter … not found in method" co-token is what
            // scopes this to a failing patch application. MACS is a human/UI convenience and reaches no
            // agent workflow (operator, 2026-08-13), which is why the whole source is noise.
            if (s.Contains("DestroyBlendTreeRecursive") && s.Contains("not found in method"))
                return "MACS Harmony startup patch failure";
            // Bare "inconsistent result" would eat unrelated errors — require an importer co-token.
            if (s.Contains("inconsistent result")
                && (s.IndexOf("fbx", StringComparison.OrdinalIgnoreCase) >= 0
                    || s.IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0))
                return "FBX importer inconsistent-result noise";
            // VRCFury build progress routes through VF.Exceptions and is genuinely error-typed.
            if (s.Contains("VF.Exceptions") && (s.Contains("Progress (") || s.Contains("Importing ")))
                return "VRCFury build-progress";
            return null;
        }

        private static string BenignNote(Dictionary<string, int> benign)
        {
            if (benign.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kv in benign) parts.Add(kv.Key + ": " + kv.Value);
            return " benign-stripped=[" + string.Join(", ", parts.ToArray()) + "]";
        }

        /// <summary>Parse the <c>types</c> filter into a bit mask over <see cref="EntryKind"/>.</summary>
        private static bool TryParseTypes(string types, out int mask, out string bad)
        {
            mask = 0;
            bad = null;
            if (string.IsNullOrEmpty(types)) types = "all";
            const int all = (1 << (int)EntryKind.Log) | (1 << (int)EntryKind.Warning) | (1 << (int)EntryKind.Error);
            var unknown = new List<string>();
            foreach (var raw in types.Split(','))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                switch (t)
                {
                    case "all": mask |= all; break;
                    case "log": mask |= TypeBit(EntryKind.Log); break;
                    case "warning": mask |= TypeBit(EntryKind.Warning); break;
                    case "error": mask |= TypeBit(EntryKind.Error); break;
                    default: unknown.Add(raw.Trim()); break;
                }
            }
            if (unknown.Count > 0) { bad = string.Join(", ", unknown.ToArray()); return false; }
            if (mask == 0) { bad = types; return false; } // "" / "," names no type; don't quietly mean `all`
            return true;
        }

        private static int TypeBit(EntryKind kind) => 1 << (int)kind;

        // ----- Rendering ----------------------------------------------------------------------

        private static void RenderEntry(Entry e, int index, bool includeStackTrace, StringBuilder body)
        {
            body.Append('[').Append(index).Append("] ").Append(e.Kind.ToString().ToUpperInvariant());
            if (!string.IsNullOrEmpty(e.File)) body.Append("  ").Append(e.File).Append(':').Append(e.Line);
            body.Append('\n').Append(e.Body).Append('\n');
            if (!string.IsNullOrEmpty(e.Stack))
            {
                // Withheld frames are counted, never silently absent — the caller can see there is more.
                if (includeStackTrace) body.Append("--- stack ---\n").Append(e.Stack).Append('\n');
                else body.Append("[+").Append(CountLines(e.Stack)).Append(" stack lines]\n");
            }
            body.Append('\n');
        }

        private static int CountLines(string s)
        {
            int n = 1;
            foreach (char c in s) if (c == '\n') n++;
            return n;
        }
    }
}
