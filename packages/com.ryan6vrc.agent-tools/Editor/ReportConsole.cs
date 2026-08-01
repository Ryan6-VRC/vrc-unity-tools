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
    /// Unity hands over everything on request. <c>LogEntry.message</c> holds the body and the
    /// callstack concatenated, and <c>LogEntry.callstackTextStartUTF16</c> is the exact index where
    /// the callstack begins — so the two are separated by reading a number Unity already computed,
    /// never by guessing at which lines look like stack frames. Guessing is what loses payload
    /// lines; there is no heuristic here to get wrong.
    ///
    /// INSPECTION ONLY. It does not write to the console it reads — no door here logs, because a
    /// logging console reader pollutes its own next read (and an error it logs is indistinguishable
    /// from a real one to `PlayGate` and to any "console is clean" check). The return value is the
    /// only channel. Clearing is not mirrored here; `read_console`'s `clear` action was its own.
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
        /// <param name="filterText">Keep only entries whose full text contains this (ordinal, case-
        /// sensitive) — the whole body is matched, not just the header.</param>
        /// <param name="count">Maximum entries returned, taken from the newest end. Clamped to 1..500.</param>
        /// <param name="includeStackTrace">Include each entry's callstack. On by default: for an
        /// exception the frames are the diagnosis, and withholding them by default would reproduce the
        /// header-only read this tool exists to replace.</param>
        public static string Report(
            string types = "all",
            string filterText = null,
            int count = 20,
            bool includeStackTrace = true)
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

            int scanned = entries.Count;
            var kept = new List<Entry>();
            foreach (var e in entries)
            {
                if ((TypeBit(e.Kind) & mask) == 0) continue;
                if (!string.IsNullOrEmpty(filterText) && e.Full.IndexOf(filterText, StringComparison.Ordinal) < 0) continue;
                kept.Add(e);
            }

            int matched = kept.Count;
            if (kept.Count > count) kept.RemoveRange(0, kept.Count - count);

            var body = new StringBuilder();
            for (int i = 0; i < kept.Count; i++)
                RenderEntry(kept[i], i + 1, includeStackTrace, body);

            // An entry Unity declined to hand over is named, never absorbed into a clean-looking count.
            string summary = "[ReportConsole] scanned=" + scanned + " matched=" + matched + " shown=" + kept.Count
                + (unreadable > 0 ? " unreadable=" + unreadable : "") + " => OK";

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
            int total;
            try
            {
                // Inside the try: a cast failure on upstream drift must still reach EndGettingEntries,
                // or the console stays locked for the rest of the session.
                total = (int)start.Invoke(null, null);
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
            body = full.Substring(0, stackStart).TrimEnd('\r', '\n');
            stack = full.Substring(stackStart).TrimStart('\r', '\n').TrimEnd('\r', '\n');
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
        private const int ModeStickyError = 1 << 13;
        private const int ModeScriptingException = 1 << 17;
        private const int ModeGraphCompileError = 1 << 20;
        private const int ModeScriptingAssertion = 1 << 21;
        private const int ModeVisualScriptingError = 1 << 22;

        private const int ErrorMask = ModeError | ModeAssert | ModeFatal | ModeAssetImportError
            | ModeScriptingError | ModeScriptCompileError | ModeStickyError | ModeScriptingException
            | ModeGraphCompileError | ModeScriptingAssertion | ModeVisualScriptingError;
        private const int WarningMask = ModeAssetImportWarning | ModeScriptingWarning | ModeScriptCompileWarning;

        /// <summary>Map a <c>LogEntry.mode</c> bitfield to its console type. Error bits win over warning
        /// bits — under-reporting severity is the dangerous direction.</summary>
        public static EntryKind ClassifyMode(int mode)
        {
            if ((mode & ErrorMask) != 0) return EntryKind.Error;
            if ((mode & WarningMask) != 0) return EntryKind.Warning;
            return EntryKind.Log;
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
