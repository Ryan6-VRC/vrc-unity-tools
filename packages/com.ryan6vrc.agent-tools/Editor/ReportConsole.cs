using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only digest of the Unity Editor console, every line of every entry. The MCP
    /// <c>read_console</c> door returns only an entry's first line, so a build warning whose payload is
    /// a list (VRCFury names each dropped binding and its source clip in the body) arrives there as its
    /// header alone.
    ///
    /// Unity's <c>LogEntry.message</c> holds the body and callstack concatenated, and
    /// <c>LogEntry.callstackTextStartUTF16</c> is the exact index where the callstack begins, so the split
    /// reads that number and never guesses which lines look like stack frames.
    ///
    /// Nothing it writes lands in the console, but past the inline budget the digest spills through
    /// `RunLogFormat.WriteRunLog`, whose asset import can make Unity's own importers log.
    /// Contract: docs/unity-tools.md.
    /// </summary>
    [AgentTool]
    public static class ReportConsole
    {
        /// <summary>Inline character budget for the entries; past it <see cref="Run"/> spills them to a Snapshot.</summary>
        private const int InlineBudget = 12000;

        // ----- Public API ---------------------------------------------------------------------

        /// <summary>
        /// Digest the most recent console entries, oldest first. Returns a one-line summary followed by
        /// the entries; when the text exceeds the inline budget the entries go to a Snapshot instead and
        /// the summary ends with the artifact path in-band (<c>… =&gt; OK | log=&lt;path&gt;</c>).
        /// </summary>
        /// <param name="types">Comma-separated subset of <c>error</c>/<c>warning</c>/<c>log</c>, or
        /// <c>all</c>; empty means all. Anything else is a bare <c>[ReportConsole] FAIL: …</c> naming it.</param>
        /// <param name="filterText">Keep only entries whose text contains this (ordinal, case-sensitive).
        /// Body and callstack are both matched, so a frame name is a valid search term. A match exempts the
        /// entry from <paramref name="stripBenign"/>, and any filter turns <paramref name="collapseDuplicates"/>
        /// off for the whole call.</param>
        /// <param name="count">Maximum rows returned, from the newest end, clamped to 1..500.</param>
        /// <param name="includeStackTrace">Include each entry's callstack.</param>
        /// <param name="stripBenign">Drop known-benign console noise (<see cref="BenignLabel"/>), naming every
        /// label and count in the summary.</param>
        /// <param name="collapseDuplicates">Fold byte-identical entries into one row carrying a repeat count;
        /// the summary carries <c>collapsed=</c> and each folded row carries <c>×N</c>. Repeats need not be
        /// adjacent and a group folds onto its newest position, so a read taken to establish sequence should
        /// pass false. Distinct from Unity's own Console "Collapse" toggle, which hides entries from this read
        /// entirely and shows up as <c>UNREACHED=</c>.</param>
        public static string Run(
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
            var rows = CollapseDuplicates(kept, collapseDuplicates && !filtering, out int collapsed);
            if (rows.Count > count) rows.RemoveRange(0, rows.Count - count);

            var body = new StringBuilder();
            for (int i = 0; i < rows.Count; i++)
            {
                RenderEntry(rows[i].Key, i + 1, includeStackTrace, body);
                if (rows[i].Value > 1) body.Append("    (×").Append(rows[i].Value).Append(" identical, collapsed)\n");
            }

            // One number per stage: scanned -> matched -> collapsed -> shown. `collapsed` is rows removed, not a
            // before/after pair; `matched` already carries the before.
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
        /// last occurrence in that occurrence's position: `count` clamps from the newest end, so an
        /// oldest-representative row for a message that recurred a second ago could be clamped away, and the
        /// recurrence would vanish from a read taken to see it. Identity is body + callstack, so two entries
        /// that differ only in where they were thrown stay apart.</summary>
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
                var key = e.Body + "\0" + e.Stack;
                int seen;
                counts[key] = counts.TryGetValue(key, out seen) ? seen + 1 : 1;
            }
            var emitted = new HashSet<string>();
            for (int i = kept.Count - 1; i >= 0; i--)
            {
                var key = kept[i].Body + "\0" + kept[i].Stack;
                if (!emitted.Add(key)) { collapsed++; continue; }
                rows.Add(new KeyValuePair<Entry, int>(kept[i], counts[key]));
            }
            rows.Reverse();
            return rows;
        }

        // ----- Console read (reflection into UnityEditor.LogEntries) ---------------------------

        /// <summary>Read every row the Console window exposes, oldest first. Returns false with a caller-facing
        /// reason when the internal API has drifted. <paramref name="unreadable"/> counts rows Unity refused to
        /// hand over.</summary>
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
            // StartGettingEntries stays outside the try: if it throws, it never acquired the console lock,
            // and a finally calling EndGettingEntries would be an unbalanced end. Everything after a successful
            // Start, the cast included, belongs inside, so drift cannot strand the lock.
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
                // Field-type drift (mode becoming uint, say) would otherwise surface through execute_code as an
                // opaque MCP failure rather than this door's named FAIL.
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

        // ----- Helpers ------------------------------------------------------------------------

        /// <summary>Split <paramref name="full"/> at the callstack index Unity reported. An index of 0 or
        /// one outside the string means Unity recorded no callstack, and the whole message is body.</summary>
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
            // silently deleting a blank line the payload meant to contain.
            body = TrimOneNewlineEnd(full.Substring(0, stackStart));
            stack = TrimOneNewlineEnd(TrimOneNewlineStart(full.Substring(stackStart)));
        }

        private static string TrimOneNewlineEnd(string s)
        {
            if (s.EndsWith("\r\n", StringComparison.Ordinal)) return s.Substring(0, s.Length - 2);
            if (s.EndsWith("\n", StringComparison.Ordinal) || s.EndsWith("\r", StringComparison.Ordinal))
                return s.Substring(0, s.Length - 1);
            return s;
        }

        private static string TrimOneNewlineStart(string s)
        {
            if (s.StartsWith("\r\n", StringComparison.Ordinal)) return s.Substring(2);
            if (s.StartsWith("\n", StringComparison.Ordinal) || s.StartsWith("\r", StringComparison.Ordinal))
                return s.Substring(1);
            return s;
        }

        // LogEntry.mode bit flags, mirroring UnityEditor.ConsoleWindow.Mode: the authoritative source for an
        // entry's type.
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

        // Bits 13, 20 and 22 stay out. They read as StickyError / GraphCompileError / VisualScriptingError in
        // ConsoleWindow.Mode, but Unity's LogMessageFlags names 13 and 22 kStickyLog and
        // kStacktraceIsPostprocessed, and bit 22 is measurably set on an ordinary Debug.LogException. Two enums
        // overlay this one int, so the name is not evidence, and adding them would promote a log to an error
        // if the LogMessageFlags reading is the right one.
        private const int ErrorMask = ModeError | ModeAssert | ModeFatal | ModeAssetImportError
            | ModeScriptingError | ModeScriptCompileError | ModeScriptingException | ModeScriptingAssertion;
        private const int WarningMask = ModeAssetImportWarning | ModeScriptingWarning | ModeScriptCompileWarning;

        /// <summary>Map a <c>LogEntry.mode</c> bitfield to its console type.</summary>
        public static EntryKind ClassifyMode(int mode)
        {
            if ((mode & ErrorMask) != 0) return EntryKind.Error;
            if ((mode & WarningMask) != 0) return EntryKind.Warning;
            return EntryKind.Log;
        }

        /// <summary>
        /// Report how many entries the console holds that this read could not reach, else "".
        ///
        /// <c>StartGettingEntries</c>/<c>GetEntryInternal</c> enumerate the Console window's rows and Unity
        /// exposes no unfiltered enumerator, so the window's LogLevel toggles, Collapse and search box all
        /// bound the read: under a search filter an unannotated digest reads <c>scanned=0 … =&gt; OK</c> over
        /// a console full of errors. <c>GetCountsByType</c> is not filtered, so the gap is reported as a
        /// number, which catches any narrowing where reading the flags catches only the ones it looks for.
        /// The gap is never corrected: clearing the operator's filters would mutate UI state.
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

        /// <summary>The Console window's narrowing settings, as the likely cause of a gap. Best-effort: the
        /// count discrepancy is the finding.</summary>
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
        /// <paramref name="full"/> is the entry's whole text: several families identify themselves in the
        /// callstack, not the message.
        ///
        /// These substring heuristics against third-party and importer output are re-validated by reading
        /// that source, never by a green test run. They match the family, not a safe subset: a real failure
        /// from one of these sources goes with its chatter, `[MACS]` taking that package's
        /// `Failed to apply patch` deliberately. When a family is implicated in a real failure, the
        /// summary's count is the trace that says to re-run with <c>stripBenign: false</c>.
        /// `ReportConsoleTests` pins the collisions by name.
        ///
        /// A family keys on the target it fails against, never the patching library: NDMF, MA and VRCFury
        /// all Harmony-patch at startup, so a <c>HarmonyLib</c>-keyed family would strip a failed VRCFury
        /// patch by default.
        /// </summary>
        public static string BenignLabel(string full)
        {
            string s = full ?? string.Empty;
            // Third-party load chatter (com.mcardellje.macs), Error-typed and Log-typed alike.
            if (s.Contains("[MACS]")) return "MACS third-party load noise";
            // MACS's Harmony patch failure at startup: a bare HarmonyLib exception carrying NO [MACS] token,
            // so it keys on the patch target it names. That target is a real UnityEditor API, so the co-token
            // scopes this to a failing patch application rather than any error mentioning it.
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
            if (mask == 0) { bad = types; return false; } // "," names no type; don't quietly mean `all`
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
