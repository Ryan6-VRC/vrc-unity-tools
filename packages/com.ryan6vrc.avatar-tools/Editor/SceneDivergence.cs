using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Answers the one question a scene-restoring teardown needs: <b>would reopening this scene from disk
    /// lose anything a human would call real work?</b> <c>Scene.isDirty</c> cannot answer it in either
    /// direction — measured both ways on a live venue: a scene reads dirty with content identical to disk
    /// (a stamp-and-revert; Modular Avatar's load-time stamp), and a scene reads CLEAN while its in-memory
    /// content differs from disk (a prefab instance moved and scaled, with the dirty flag cleared behind it).
    /// The first blocks legitimate work; the second is silent data loss.
    ///
    /// <para><b>Mechanism.</b> Serialize the in-memory scene to a throwaway copy under <c>Temp/</c> and diff
    /// that text against the scene's file on disk. The copy-save is the only way to see what the scene
    /// currently IS rather than what Unity remembers being told about it. Measured: 19–113 ms per scene
    /// (152k-line scene = 113 ms), deterministic (two consecutive copy-saves of a 3.87 MB scene were
    /// byte-identical), and it does not change <c>isDirty</c>.</para>
    ///
    /// <para><b>A copy-save is not inert.</b> It fires every <c>sceneSaving</c> / <c>OnWillSaveAssets</c>
    /// handler in the project. NDMF's ProxyManager resets preview state on any save (ambient — an operator's
    /// own save does the same). Worse, NDMF's and Modular Avatar's preview-scene managers DESTROY every root
    /// of their preview scene when it is the scene being saved, and <c>___NDMF Preview___</c> is a real,
    /// loaded, enumerated scene. That is why callers must only probe scenes whose path is under
    /// <c>Assets/</c> (see <see cref="IsProbeable"/>): package scenes are never the operator's work, and
    /// skipping them removes the whole hazard without taking a dependency on either framework.</para>
    /// </summary>
    internal static class SceneDivergence
    {
        // Framework churn: content the load re-manufactures, so restoring from disk loses nothing real.
        // A CLOSED, MEASURED list (AvatarProject corpus, 2026-08-15) — MA's load-time version stamp,
        // VRCFury's version bump on deserialize, and the editor's selection-mode field. Its failure mode is
        // deliberately a loud refuse: a framework upgrade that adds a serialized field will make affected
        // scenes refuse until someone saves them, and the refusal NAMES the residual keys so the next reader
        // can tell allowlist rot from real work and extend this list rather than distrust the gate.
        private static readonly HashSet<string> ChurnKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "_modularAvatarVersionTag",
            "UpdatedAtVersion",
            "MinimumVersion",
            "vrcfuryVersion",
            "m_selectionMode",
        };

        // Past this many differing lines the divergence is self-evidently large — refuse rather than pay
        // Myers' O((N+M)·D) to characterize it. Also bounds the trace this keeps for the backtrack.
        internal const int MaxEditScript = 500;

        // Offenders quoted in the summary. Enough to recognize your own edit, few enough to read.
        private const int SummarySamples = 5;

        /// <summary>Is this scene one the gate should probe at all? Only scenes under <c>Assets/</c>: a
        /// package scene is never the operator's work, and two of them destroy themselves on save.</summary>
        internal static bool IsProbeable(Scene scene)
        {
            return !string.IsNullOrEmpty(scene.path)
                   && scene.path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when reopening <paramref name="scene"/> from disk would discard real work; <paramref
        /// name="summary"/> then names the scene and what differs. <b>Fails closed</b> — a save that throws
        /// or returns false, a missing or empty copy, an unreadable disk file, an over-cap diff: every one of
        /// them returns true. A tool that exists to not lose work must never read its own probe failure as
        /// "nothing to protect".
        /// </summary>
        internal static bool WouldRestoreLoseWork(Scene scene, out string summary)
        {
            string label = string.IsNullOrEmpty(scene.name) ? "<untitled>" : scene.name;
            string copyPath = "Temp/r27probe-" + DateTime.UtcNow.Ticks + "-" + scene.handle + ".unity";
            try
            {
                if (!EditorSceneManager.SaveScene(scene, copyPath, true))
                {
                    summary = label + ": could not serialize the scene for comparison (SaveScene returned false)"
                              + " — refusing, since nothing here can tell whether the restore would lose work";
                    return true;
                }
                if (!File.Exists(copyPath) || new FileInfo(copyPath).Length == 0)
                {
                    summary = label + ": the comparison copy was not written (or is empty) — refusing";
                    return true;
                }

                string memText = File.ReadAllText(copyPath);
                string diskText = File.ReadAllText(Path.GetFullPath(scene.path));
                string detail;
                if (!Classify(diskText, memText, out detail)) { summary = null; return false; }
                summary = label + ": " + detail;
                return true;
            }
            catch (Exception ex)
            {
                summary = label + ": comparison against disk failed (" + ex.GetType().Name + ": " + ex.Message
                          + ") — refusing rather than guess";
                return true;
            }
            finally
            {
                TryDelete(copyPath);
                // SaveScene writes a .meta beside the copy even outside Assets/ — sweep both or Temp/ silts up.
                TryDelete(copyPath + ".meta");
            }
        }

        /// <summary>
        /// Serialize every probeable loaded scene to a throwaway copy under <c>Temp/</c> and return the paths
        /// written — the backstop for a difference <see cref="Classify"/> did not recognize as work.
        /// </summary>
        /// <remarks>
        /// Taken by the caller BEFORE it mutates anything. A copy taken at teardown instead would carry the
        /// session's own edits (deactivated avatars, the emulator object, whatever play left behind) and hand
        /// back a "restore" that silently reintroduces them. Best-effort by design: the gate has already
        /// passed, so a copy that fails is a missing safety net, not a reason to refuse the session — the
        /// caller reports which scenes got one. Unity will not open a scene outside <c>Assets/</c>, so
        /// recovering from one of these means copying it into the project first; whoever prints these paths
        /// says so.
        /// </remarks>
        internal static List<string> WriteRescueCopies(out List<string> failed)
        {
            var written = new List<string>();
            failed = new List<string>();
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!IsProbeable(s)) continue;
                string path = "Temp/rescue-" + stamp + "-" + i + "-" + SafeFileName(s.name) + ".unity";
                try
                {
                    if (EditorSceneManager.SaveScene(s, path, true)) written.Add(path);
                    else failed.Add(s.name);
                }
                catch (Exception) { failed.Add(s.name); }
            }
            return written;
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "scene";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        /// <summary>
        /// Pure verdict over two scene-YAML texts: true when the in-memory text (<paramref name="memText"/>)
        /// holds content the on-disk text does not, or lacks content the disk text has, once the classes
        /// below are discounted. <paramref name="summary"/> is null on false.
        /// </summary>
        /// <remarks>
        /// <para><b>There is no safe side of the diff.</b> The restore replaces memory with disk, so any
        /// divergence is a loss of memory state — a deletion-only edit (an object removed in memory) shows up
        /// as disk-only lines and is real work the reopen undoes. Only two classes may be discounted: churn
        /// the load re-manufactures (<see cref="ChurnKeys"/>), and prefab-override entries whose target is a
        /// dangling <c>{fileID: 0}</c>, which Unity prunes AT SAVE — so the copy never emits them and the disk
        /// side's copies are noise. A fileID-0 target cannot be resolved and so cannot be edited: nothing real
        /// hides in that class.</para>
        /// <para><b>Myers, not a line multiset.</b> A bag of lines is permutation-blind, and permutations are
        /// ordinary work: flipping which avatar is active (<c>m_IsActive</c> 1→0 on one object, 0→1 on
        /// another), a sibling reorder, a component-list reorder. Every one leaves bag counts identical and
        /// would be silently accepted, then reverted. The 143k-line cascade this design was first measured
        /// against came from comparing by line INDEX, not from an LCS — Myers is order-sensitive and, with the
        /// diff small (D≈115 on the worst corpus scene), cheap.</para>
        /// </remarks>
        internal static bool Classify(string diskText, string memText, out string summary)
        {
            summary = null;
            if (string.Equals(diskText, memText, StringComparison.Ordinal)) return false;

            List<string> disk = StripDanglingOverrides(SplitLines(diskText));
            List<string> mem = StripDanglingOverrides(SplitLines(memText));

            List<int> memOnly, diskOnly;
            if (!TryDiff(disk, mem, out memOnly, out diskOnly))
            {
                summary = "in-memory content differs from disk in more than " + MaxEditScript
                          + " lines — too large to characterize, and far too large to be framework churn";
                return true;
            }

            memOnly = DropChurn(memOnly, mem);
            diskOnly = DropChurn(diskOnly, disk);
            if (memOnly.Count == 0 && diskOnly.Count == 0) return false;

            var sb = new StringBuilder();
            sb.Append("in-memory content differs from disk — ").Append(memOnly.Count)
              .Append(" line(s) only in memory, ").Append(diskOnly.Count).Append(" only on disk");
            AppendSamples(sb, "in memory", memOnly, mem);
            AppendSamples(sb, "on disk", diskOnly, disk);
            summary = sb.ToString();
            return true;
        }

        private static void AppendSamples(StringBuilder sb, string side, List<int> indices, List<string> lines)
        {
            if (indices.Count == 0) return;
            sb.Append("; ").Append(side).Append(": ");
            for (int i = 0; i < indices.Count && i < SummarySamples; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Describe(indices[i], lines));
            }
            if (indices.Count > SummarySamples) sb.Append(", …");
        }

        /// <summary>
        /// One offender, said in a way its owner can recognize. A bare <c>value: -3</c> identifies nothing —
        /// the property it belongs to sits a line or two above it in the same modification entry, so carry it
        /// down: <c>m_LocalPosition.x=-3</c>.
        /// </summary>
        private static string Describe(int index, List<string> lines)
        {
            string line = lines[index].Trim();
            if (KeyOf(line) != "value") return line;
            for (int i = index - 1; i >= 0 && i >= index - 3; i--)
            {
                if (KeyOf(lines[i]) != "propertyPath") continue;
                string prop = lines[i].Trim();
                int colon = prop.IndexOf(':');
                return prop.Substring(colon + 1).Trim() + "=" + line.Substring(line.IndexOf(':') + 1).Trim();
            }
            return line;
        }

        private static List<int> DropChurn(List<int> indices, List<string> lines)
        {
            var kept = new List<int>();
            foreach (int i in indices) if (!ChurnKeys.Contains(KeyOf(lines[i]))) kept.Add(i);
            return kept;
        }

        /// <summary>
        /// The YAML key a line sets — the token before its first <c>:</c>, with any <c>- </c> sequence marker
        /// dropped. Matched as a KEY, never as a substring: the corpus carries
        /// <c>propertyPath: MinimumVersion</c> lines where an allowlisted name appears as a VALUE, and a
        /// substring match would swallow half of a real override and keep the other half.
        /// </summary>
        internal static string KeyOf(string line)
        {
            string s = line.Trim();
            if (s.StartsWith("- ", StringComparison.Ordinal)) s = s.Substring(2).TrimStart();
            int colon = s.IndexOf(':');
            return colon < 0 ? string.Empty : s.Substring(0, colon);
        }

        /// <summary>
        /// Drop prefab-modification entries whose target is a dangling <c>{fileID: 0}</c> — Unity prunes them
        /// at save, so they appear as disk-only noise on every scene carrying missing-target residue (10 such
        /// entries in one measured venue scene alone).
        /// </summary>
        /// <remarks>
        /// Matched on the exact markers <c>- target: </c> and <c>- targetCorrespondingSourceObject: </c>
        /// (m_Modifications and m_AddedGameObjects respectively) — NOT a loose <c>target\w*</c> pattern, which
        /// would conflate the two shapes. The mapping may wrap onto a following line, and the entry's
        /// remaining fields (<c>propertyPath</c>, a possibly EMPTY <c>value</c>, <c>objectReference</c>,
        /// <c>insertionIndex</c>) are consumed by indentation, so no field list has to be maintained here.
        ///
        /// Known, accepted false-refuse: if disk carries a dangling ADDED-object entry and the live instance
        /// still has the object, the copy re-emits the resolved entry — memory-only residual, so the gate
        /// refuses. Measured only by hand-corrupting a scene file; the remedy (save the scene) is the one the
        /// refusal already prints.
        /// </remarks>
        internal static List<string> StripDanglingOverrides(List<string> lines)
        {
            var kept = new List<string>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                bool isEntry = trimmed.StartsWith("- target: ", StringComparison.Ordinal)
                               || trimmed.StartsWith("- targetCorrespondingSourceObject: ", StringComparison.Ordinal);
                if (!isEntry) { kept.Add(line); continue; }

                int indent = line.Length - trimmed.Length;
                // The mapping may wrap; gather it up to its closing brace before judging the fileID.
                int mapEnd = i;
                var mapping = new StringBuilder(trimmed);
                while (mapping.ToString().IndexOf('}') < 0 && mapEnd + 1 < lines.Count)
                {
                    mapEnd++;
                    mapping.Append(' ').Append(lines[mapEnd].Trim());
                }
                if (!IsNullMapping(mapping.ToString())) { kept.Add(line); continue; }

                // Dangling: swallow the whole entry — its mapping lines, then every line indented deeper than
                // the "- " marker (its remaining fields), stopping at the next entry or the end of the block.
                i = mapEnd;
                while (i + 1 < lines.Count)
                {
                    string next = lines[i + 1];
                    string nextTrimmed = next.TrimStart();
                    if (nextTrimmed.Length == 0) break;
                    int nextIndent = next.Length - nextTrimmed.Length;
                    if (nextIndent <= indent) break;
                    i++;
                }
            }
            return kept;
        }

        // "{fileID: 0}" — a null reference. "{fileID: 0, guid: …}" cannot occur (a guid'd ref has a real
        // fileID), so the fileID alone decides.
        private static bool IsNullMapping(string mapping)
        {
            int brace = mapping.IndexOf('{');
            if (brace < 0) return false;
            int close = mapping.IndexOf('}', brace);
            if (close < 0) return false;
            string inner = mapping.Substring(brace + 1, close - brace - 1).Trim();
            return inner == "fileID: 0";
        }

        private static List<string> SplitLines(string text)
        {
            return new List<string>(text.Replace("\r\n", "\n").Split('\n'));
        }

        /// <summary>
        /// Myers O((N+M)·D) diff, capped at <see cref="MaxEditScript"/> edits. Returns false when the script
        /// exceeds the cap (the caller refuses on that alone). Common prefix/suffix are trimmed first, which
        /// is what keeps a 152k-line scene with a 115-line diff cheap.
        /// </summary>
        private static bool TryDiff(List<string> a, List<string> b, out List<int> bOnly, out List<int> aOnly)
        {
            bOnly = new List<int>();
            aOnly = new List<int>();

            int start = 0;
            while (start < a.Count && start < b.Count && a[start] == b[start]) start++;
            int endA = a.Count - 1, endB = b.Count - 1;
            while (endA >= start && endB >= start && a[endA] == b[endB]) { endA--; endB--; }

            int n = endA - start + 1, m = endB - start + 1;
            if (n <= 0 && m <= 0) return true;
            // One side is entirely consumed: the rest is a pure insert or a pure delete, no search needed.
            if (n <= 0) { for (int i = 0; i < m; i++) bOnly.Add(start + i); return bOnly.Count <= MaxEditScript; }
            if (m <= 0) { for (int i = 0; i < n; i++) aOnly.Add(start + i); return aOnly.Count <= MaxEditScript; }

            int max = Math.Min(n + m, MaxEditScript);
            int offset = max + 1;
            var v = new int[2 * max + 3];
            // One snapshot per depth, taken BEFORE that depth's step — the state the backtrack reads to work
            // out which move (down = insert, right = delete) reached each point.
            var trace = new List<int[]>(max + 1);

            for (int d = 0; d <= max; d++)
            {
                trace.Add((int[])v.Clone());
                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset])) x = v[k + 1 + offset];
                    else x = v[k - 1 + offset] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[start + x] == b[start + y]) { x++; y++; }
                    v[k + offset] = x;
                    if (x >= n && y >= m) { Backtrack(a, b, start, n, m, trace, offset, bOnly, aOnly); return true; }
                }
            }
            return false; // over the cap
        }

        // Walk the recorded snapshots back from the end point (n, m), collecting the inserted (b-only) and
        // deleted (a-only) lines. Diagonal runs between edits are matches and contribute nothing. Both lists
        // are reversed at the end so the summary quotes offenders in file order.
        private static void Backtrack(List<string> a, List<string> b, int start, int n, int m,
                                      List<int[]> trace, int offset, List<int> bOnly, List<int> aOnly)
        {
            int x = n, y = m;
            for (int d = trace.Count - 1; d >= 0; d--)
            {
                int[] v = trace[d];
                int k = x - y;
                int prevK;
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset])) prevK = k + 1;
                else prevK = k - 1;
                int prevX = v[prevK + offset];
                int prevY = prevX - prevK;

                while (x > prevX && y > prevY) { x--; y--; } // back down the snake

                if (d > 0)
                {
                    if (x == prevX) bOnly.Add(start + prevY); // a down move inserted b's line
                    else aOnly.Add(start + prevX);            // a right move deleted a's line
                }
                x = prevX; y = prevY;
            }
            bOnly.Reverse();
            aOnly.Reverse();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* Temp/ litter is not worth a throw */ }
        }
    }
}
