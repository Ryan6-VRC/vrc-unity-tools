using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only digest of the Unity Editor console — every line of every entry, including the
    /// multi-line bodies that carry the diagnosis.
    ///
    /// This exists because the MCP <c>read_console</c> door returns only an entry's FIRST line: it
    /// reads the whole message out of Unity and then discards lines 2..N with a
    /// <c>message.Split('\n')[0]</c>. Unity is not the limit — <c>LogEntries.GetEntryInternal</c>
    /// hands back the complete text, which is what this tool reads. A build warning whose payload is
    /// a list (VRCFury's "Removed N properties from animation clips that targeted objects that do
    /// not exist:" names each dropped path, property and source clip in its body) arrives here
    /// intact; through <c>read_console</c> the count survives and the diagnosis does not.
    ///
    /// Two behaviors deliberately differ from that door, both because they were defects:
    ///   * TYPE comes from the entry's <c>mode</c> BITS, not from substring-matching the text.
    ///     Matching on "Exception" mis-tags benign lines (VRCFury routes build-progress chatter
    ///     through <c>VF.Exceptions</c>) as errors.
    ///   * The stack trace is split off as a CONTIGUOUS TRAILING RUN of stack-frame lines, scanned
    ///     backwards from the end. A forward scan for the first "stack-looking" line silently eats
    ///     every payload line after it. Nothing between the header and the stack can be lost here;
    ///     when the split is uncertain the text is kept, never dropped.
    ///
    /// INSPECTION ONLY — never mutates the console or project. (Clearing the console is
    /// <c>read_console</c>'s <c>clear</c> action, deliberately not mirrored here.)
    /// </summary>
    [AgentTool]
    public static class ReportConsole
    {
        /// <summary>Inline character budget. Past this the digest spills to a RunLog artifact and the
        /// summary carries its path in-band, so a noisy console can never blow out the caller's context.</summary>
        private const int InlineBudget = 12000;

        // ----- Public API ---------------------------------------------------------------------

        /// <summary>
        /// Digest the most recent console entries, newest last. Returns a one-line summary followed by
        /// the entries; when the text exceeds the inline budget the entries go to a RunLog instead and
        /// the summary ends with the artifact path in-band (<c>… =&gt; OK | log=&lt;path&gt;</c>).
        /// </summary>
        /// <param name="types">Comma-separated subset of <c>error</c>/<c>warning</c>/<c>log</c>, or
        /// <c>all</c>. Unrecognized names are a bare <c>[ReportConsole] FAIL: …</c> naming them.</param>
        /// <param name="filterText">Keep only entries whose FULL text contains this (ordinal, case-
        /// sensitive) — the whole body is matched, not just the header.</param>
        /// <param name="count">Maximum entries returned, taken from the newest end. Clamped to 1..500.</param>
        /// <param name="includeStackTrace">Append each entry's stack frames. Off by default: the frames
        /// are usually noise, and dropping them is safe here because the payload is never inside them.</param>
        /// <param name="stripBenign">Drop known-benign noise and name the counts in the summary — never
        /// silent. An entry kept BECAUSE it matched <paramref name="filterText"/> is exempt: filtering
        /// for noise by name should not then have it stripped.</param>
        public static string Report(
            string types = "all",
            string filterText = null,
            int count = 20,
            bool includeStackTrace = false,
            bool stripBenign = true)
        {
            int mask;
            string badTypes;
            if (!TryParseTypes(types, out mask, out badTypes))
                return Fail("unrecognized types '" + badTypes + "' — expects a comma-separated subset of error/warning/log, or all");

            if (count < 1) count = 1;
            if (count > 500) count = 500;

            List<Entry> entries;
            string readError;
            if (!TryReadEntries(out entries, out readError))
                return Fail(readError);

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
                    string label = BenignLabel(e.Body, e.Stack);
                    if (label != null)
                    {
                        int n;
                        benign[label] = benign.TryGetValue(label, out n) ? n + 1 : 1;
                        continue;
                    }
                }
                kept.Add(e);
            }

            int matched = kept.Count;
            if (kept.Count > count) kept.RemoveRange(0, kept.Count - count);

            var body = new StringBuilder();
            for (int i = 0; i < kept.Count; i++)
                RenderEntry(kept[i], i + 1, includeStackTrace, body);

            string summary = "[ReportConsole] scanned=" + scanned + " matched=" + matched
                + " shown=" + kept.Count + BenignNote(benign) + " => OK";

            if (body.Length <= InlineBudget)
            {
                string inline = summary + "\n" + body;
                Debug.Log(summary);
                return inline;
            }

            string header = "# ReportConsole\n" + summary + "\n\n";
            string result = RunLogFormat.WriteRunLog(
                RunLogFormat.SnapshotDir, "report-console", summary, header + body, ".md");
            Debug.Log(result);
            return result;
        }

        // ----- Entry model --------------------------------------------------------------------

        /// <summary>One console entry, already split. <see cref="Full"/> is Unity's verbatim text and is
        /// what <c>filterText</c> matches; <see cref="Body"/> + <see cref="Stack"/> partition it exactly
        /// (concatenating them reproduces <see cref="Full"/> up to the separating newline).</summary>
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
        /// the internal API has drifted — a rename upstream must fail loud, never read as an empty console.</summary>
        private static bool TryReadEntries(out List<Entry> entries, out string error)
        {
            entries = null;
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
            if (start == null || end == null || getEntry == null || fMessage == null || fMode == null)
            {
                error = "console reflection failed — StartGettingEntries/EndGettingEntries/GetEntryInternal/"
                    + "LogEntry.message must all resolve; one is missing (internal API drift)";
                return false;
            }

            var list = new List<Entry>();
            int total = (int)start.Invoke(null, null);
            try
            {
                var instance = Activator.CreateInstance(logEntryType);
                for (int i = 0; i < total; i++)
                {
                    var args = new object[] { i, instance };
                    if (!(bool)getEntry.Invoke(null, args)) continue;
                    var boxed = args[1];

                    string full = fMessage.GetValue(boxed) as string ?? string.Empty;
                    string stack;
                    string bodyText = SplitStackSuffix(full, out stack);

                    list.Add(new Entry
                    {
                        Kind = ClassifyMode((int)fMode.GetValue(boxed)),
                        Full = full,
                        Body = bodyText,
                        Stack = stack,
                        File = fFile != null ? fFile.GetValue(boxed) as string ?? string.Empty : string.Empty,
                        Line = fLine != null ? (int)fLine.GetValue(boxed) : 0,
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

        // LogEntry.mode bit flags. Classifying on these is why this tool does not inherit the
        // substring-matching mis-tag that makes VRCFury's progress chatter read as an error.
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

        private const int ErrorMask = ModeError | ModeAssert | ModeFatal | ModeAssetImportError
            | ModeScriptingError | ModeScriptCompileError | ModeScriptingException | ModeScriptingAssertion;
        private const int WarningMask = ModeAssetImportWarning | ModeScriptingWarning | ModeScriptCompileWarning;

        /// <summary>Map a <c>LogEntry.mode</c> bitfield to its console type. Error bits win over warning
        /// bits (an entry carrying both is an error).</summary>
        public static EntryKind ClassifyMode(int mode)
        {
            if ((mode & ErrorMask) != 0) return EntryKind.Error;
            if ((mode & WarningMask) != 0) return EntryKind.Warning;
            return EntryKind.Log;
        }

        /// <summary>
        /// Split Unity's concatenated <c>message</c> into body and stack trace, scanning BACKWARDS from
        /// the end for a contiguous run of stack-frame lines. Returns the body; <paramref name="stack"/>
        /// receives the frames (empty when none are found).
        ///
        /// Backwards is the whole point: a forward scan for the first stack-looking line drops every
        /// payload line that happens to follow it, which is how a list-payload warning loses its list.
        /// Here only a true trailing run can be removed, so no interior line is reachable by the split.
        /// A message that is entirely stack frames keeps its first line as the body — an entry always
        /// has a header.
        /// </summary>
        public static string SplitStackSuffix(string full, out string stack)
        {
            stack = string.Empty;
            if (string.IsNullOrEmpty(full)) return full ?? string.Empty;

            string[] lines = full.Split('\n');
            int firstFrame = lines.Length;
            for (int i = lines.Length - 1; i >= 1; i--)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0)
                {
                    // A blank line inside the trailing run is tolerated only if frames continue above it.
                    if (firstFrame <= i + 1) continue;
                    break;
                }
                if (!IsStackFrame(line)) break;
                firstFrame = i;
            }
            if (firstFrame >= lines.Length) return full;

            stack = string.Join("\n", lines, firstFrame, lines.Length - firstFrame).TrimEnd('\r', '\n');
            return string.Join("\n", lines, 0, firstFrame).TrimEnd('\r', '\n');
        }

        /// <summary>True when a line looks like a managed stack frame. Deliberately narrow — a false
        /// positive costs a dropped payload line, so this demands frame punctuation, not just a dot.</summary>
        private static bool IsStackFrame(string line)
        {
            string t = line.Trim();
            if (t.Length == 0) return false;
            // "  at Foo.Bar () [0x00000] in <hash>:0" — .NET-style frame.
            if (t.StartsWith("at ", StringComparison.Ordinal) && t.Contains("(")) return true;
            // "Type:Method (args)" — Unity's own frame grammar, optionally " (at Assets/X.cs:12)".
            if (t.Contains(" (at ") && t.Contains(":")) return true;
            int paren = t.IndexOf(" (", StringComparison.Ordinal);
            if (paren <= 0 || !t.EndsWith(")", StringComparison.Ordinal)) return false;
            string head = t.Substring(0, paren);
            return head.IndexOf(':') > 0 && head.IndexOf(' ') < 0;
        }

        // Known-benign console noise. Each predicate reads the entry's body and stack; the label is what
        // the summary names when one is dropped. These are output-string heuristics against third-party
        // and importer text, so they are re-validated by reading upstream source, never by a green run.
        private static readonly KeyValuePair<string, Func<string, string, bool>>[] BenignPatterns =
        {
            Benign("MACS third-party load noise", (m, s) => m.Contains("[MACS]")),
            Benign("DestroyBlendTreeRecursive", (m, s) => (m + s).Contains("DestroyBlendTreeRecursive")),
            Benign("FBX importer inconsistent-result noise", (m, s) =>
            {
                string blob = m + s;
                return blob.Contains("inconsistent result")
                    && (blob.IndexOf("fbx", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0);
            }),
            Benign("VRCFury build-progress", (m, s) =>
            {
                string blob = m + s;
                return blob.Contains("VF.Exceptions") && (blob.Contains("Progress (") || blob.Contains("Importing "));
            }),
        };

        private static KeyValuePair<string, Func<string, string, bool>> Benign(string label, Func<string, string, bool> p)
            => new KeyValuePair<string, Func<string, string, bool>>(label, p);

        /// <summary>The benign label matching this entry, or null when it is signal.</summary>
        public static string BenignLabel(string message, string stack)
        {
            string m = message ?? string.Empty;
            string s = stack ?? string.Empty;
            foreach (var pattern in BenignPatterns)
                if (pattern.Value(m, s)) return pattern.Key;
            return null;
        }

        /// <summary>Parse the <c>types</c> filter into a bit mask over <see cref="EntryKind"/>.</summary>
        private static bool TryParseTypes(string types, out int mask, out string bad)
        {
            mask = 0;
            bad = null;
            if (string.IsNullOrEmpty(types)) types = "all";
            var unknown = new List<string>();
            foreach (var raw in types.Split(','))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                switch (t)
                {
                    case "all": mask |= TypeBit(EntryKind.Log) | TypeBit(EntryKind.Warning) | TypeBit(EntryKind.Error); break;
                    case "log": mask |= TypeBit(EntryKind.Log); break;
                    case "warning": mask |= TypeBit(EntryKind.Warning); break;
                    case "error": mask |= TypeBit(EntryKind.Error); break;
                    default: unknown.Add(raw.Trim()); break;
                }
            }
            if (unknown.Count > 0) { bad = string.Join(", ", unknown.ToArray()); return false; }
            if (mask == 0) mask = TypeBit(EntryKind.Log) | TypeBit(EntryKind.Warning) | TypeBit(EntryKind.Error);
            return true;
        }

        private static int TypeBit(EntryKind kind) => 1 << (int)kind;

        // ----- Rendering ----------------------------------------------------------------------

        private static void RenderEntry(Entry e, int index, bool includeStackTrace, StringBuilder body)
        {
            body.Append('[').Append(index).Append("] ").Append(e.Kind.ToString().ToUpperInvariant());
            if (!string.IsNullOrEmpty(e.File)) body.Append("  ").Append(e.File).Append(':').Append(e.Line);
            body.Append('\n');
            body.Append(e.Body).Append('\n');
            if (includeStackTrace && !string.IsNullOrEmpty(e.Stack))
                body.Append("--- stack ---\n").Append(e.Stack).Append('\n');
            body.Append('\n');
        }

        private static string BenignNote(Dictionary<string, int> benign)
        {
            if (benign.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kv in benign) parts.Add(kv.Key + ": " + kv.Value);
            return " benign-stripped=" + string.Join(", ", parts.ToArray());
        }

        private static string Fail(string why)
        {
            string err = "[ReportConsole] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }
    }
}
