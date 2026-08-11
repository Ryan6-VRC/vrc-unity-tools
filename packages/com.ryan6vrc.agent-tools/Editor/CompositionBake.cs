using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// <see cref="ReportComposition"/>'s bake mode: measure the composed truth instead of inferring it.
    /// It builds a fresh clone through <see cref="AvatarBake"/> and diffs the built parameter set against
    /// the authored census, so the rewrite a build performs is READ rather than modelled — no vendor
    /// name-mangling grammar is pinned anywhere here, because a mis-pinned one returns plausible strings
    /// with nothing to signal they are wrong.
    ///
    /// <b>Two-phase, by measurement.</b> A full preprocess chain on a complex avatar with an optimizer
    /// installed runs past the MCP transport's patience, and a lost reply on a synchronous call discards a
    /// result the editor actually produced. So <see cref="Begin"/> writes the artifact path FIRST, schedules
    /// the work, and returns <c>PENDING</c> with that path; <see cref="Verify"/> re-reads it. The transport
    /// re-sends a timed-out payload, so <see cref="Begin"/> is idempotent while a bake is in flight — a
    /// duplicate returns the same pending path rather than starting a second build.
    ///
    /// Deferred off <c>EditorApplication.update</c>, never <c>delayCall</c>: an MCP-driven editor is
    /// unfocused, where <c>delayCall</c> queues indefinitely and fires on the click that focuses the window.
    ///
    /// <b>Freshness is by construction.</b> The clone is built here and destroyed after the read; no
    /// previously-baked artifact under <c>com.vrcfury.temp</c> is ever consulted, because those reflect the
    /// VRCFury version that produced them and a stale one can differ structurally from what the installed
    /// version now emits.
    /// </summary>
    internal static class CompositionBake
    {
        private const string Pending = "pending";
        private static readonly Dictionary<int, bool> InFlight = new Dictionary<int, bool>();

        private static string ArtifactPath(GameObject root) =>
            RunLogFormat.SnapshotDir + "/composition-bake_" + RunLogFormat.Sanitize(root.name) + ".md";

        internal static string Begin(GameObject root, ReportComposition.CensusResult census, string paramFilter)
        {
            string path = ArtifactPath(root);
            int key = root.GetInstanceID();
            if (InFlight.TryGetValue(key, out bool running) && running)
                return "[ReportComposition] " + root.name + ": mode=bake => PENDING (already running) | log=" + path;

            // The path is on disk BEFORE the work starts, so a transport timeout loses nothing.
            WriteArtifact(path, "# ReportComposition (bake): " + root.name + "\n\nstatus: " + Pending
                + "\n\nThe bake is running. Re-read this file, or call `ReportComposition.Verify(<avatarRoot>)`.\n"
                + "Do NOT re-issue the bake: a second call while this one is in flight returns this same path.\n");
            InFlight[key] = true;

            var rootRef = root;
            EditorApplication.CallbackFunction step = null;
            step = () =>
            {
                EditorApplication.update -= step;
                try { Run(rootRef, census, paramFilter, path); }
                finally { InFlight[key] = false; }
            };
            EditorApplication.update += step;

            return "[ReportComposition] " + root.name + ": mode=bake => PENDING | log=" + path;
        }

        internal static string Verify(GameObject root)
        {
            string path = ArtifactPath(root);
            string full = FullPath(path);
            if (!File.Exists(full))
                return "[ReportComposition] FAIL: no bake artifact at " + path + " — run Report(<avatarRoot>, bake:true) first";
            string text = File.ReadAllText(full);
            if (text.Contains("status: " + Pending))
                return "[ReportComposition] " + root.name + ": mode=bake => PENDING | log=" + path;
            string head = text.Split('\n').FirstOrDefault(l => l.StartsWith("summary: ", StringComparison.Ordinal));
            return head != null ? head.Substring("summary: ".Length).Trim()
                                : "[ReportComposition] " + root.name + ": mode=bake => OK | log=" + path;
        }

        private static void Run(GameObject root, ReportComposition.CensusResult census, string paramFilter, string path)
        {
            if (root == null)
            {
                WriteArtifact(path, Refusal(path, "the avatar root was destroyed before the bake ran"));
                return;
            }

            GameObject clone;
            string failedStage;
            if (!AvatarBake.Try(root, out clone, out failedStage))
            {
                // A loud refusal naming the stage — NEVER a silent fallback to the authored census. Emitting
                // authored rows under a heading that promises composed truth is the failure this door exists
                // to prevent, so bake mode publishes no table at all when the bake did not happen.
                WriteArtifact(path, Refusal(path, failedStage));
                Debug.LogError("[ReportComposition] " + root.name + ": mode=bake => FAIL (" + failedStage + ") | log=" + path);
                return;
            }

            try
            {
                var built = BuiltParams(clone);
                var diff = Diff(census, built, paramFilter);
                string summary = string.Format(CultureInfo.InvariantCulture,
                    "[ReportComposition] {0}: surfaces={1} params={2} kept={3} renamed={4} dropped={5} merged={6} unattributed={7} mode=bake => OK | log={8}",
                    root.name, census.Surfaces.Count, census.Params.Count,
                    diff.Count(d => d.Category == "kept"), diff.Count(d => d.Category == "renamed"),
                    diff.Count(d => d.Category == "dropped"), diff.Count(d => d.Category == "merged"),
                    diff.Count(d => d.Category == "unattributed"), path);

                var section = new List<string>
                {
                    "| authored | category | built | owning surface |",
                    "| --- | --- | --- | --- |",
                };
                foreach (var d in diff)
                    section.Add("| `" + d.Authored + "` | " + d.Category + " | `" + d.Built + "` | "
                              + RunLogFormat.Cell(d.Surface) + " |");
                section.Add("");
                section.Add("Categories: **kept** the built avatar carries the authored name; **renamed** a built name was "
                          + "attributed to it by suffix and owning surface; **dropped** no built parameter was attributable; "
                          + "**merged** two or more authored names resolved onto one built name; **unattributed** a BUILT "
                          + "parameter matched no authored one.");
                section.Add("Attribution is inference, not a pinned grammar — an `unattributed` row is the honest answer and "
                          + "beats a mapping the tool cannot support.");
                section.Add("Optimizers found on the root: "
                          + (census.Optimizers.Count == 0 ? "(none)" : string.Join(", ", census.Optimizers))
                          + ". The full chain is what ships and is what was measured; disable them yourself for a "
                          + "pre-optimizer view.");

                string body = "summary: " + summary + "\n\n"
                            + ReportComposition.RenderBody(root, census, paramFilter, "bake (measured against a fresh build)", section);
                WriteArtifact(path, body);
                Debug.Log(summary);
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        private struct DiffRow { public string Authored, Built, Category, Surface; }

        /// <summary>Every parameter the BUILT clone declares, from its own descriptor. Read off the clone,
        /// which is the only artifact that carries the post-rewrite names.</summary>
        private static List<string> BuiltParams(GameObject clone)
        {
            var names = new List<string>();
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var ep = d != null ? d.expressionParameters : null;
            if (ep == null || ep.parameters == null) return names;
            foreach (var p in ep.parameters)
                if (p != null && !string.IsNullOrEmpty(p.name)) names.Add(p.name);
            return names;
        }

        private static List<DiffRow> Diff(ReportComposition.CensusResult census, List<string> built, string paramFilter)
        {
            var rows = new List<DiffRow>();
            var unclaimed = new HashSet<string>(built, StringComparer.Ordinal);
            var claimedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var p in census.Params)
            {
                if (built.Contains(p.Name))
                {
                    rows.Add(new DiffRow { Authored = p.Name, Built = p.Name, Category = "kept", Surface = p.Declared });
                    unclaimed.Remove(p.Name);
                    Claim(claimedBy, p.Name, p.Name);
                    continue;
                }
                // A build that prefixes a name leaves the authored name as a SUFFIX of the built one. That is
                // the only shape claimed here; anything else stays unattributed rather than guessed.
                var candidates = built.Where(b => b.EndsWith("/" + p.Name, StringComparison.Ordinal)
                                               || b.EndsWith(p.Name, StringComparison.Ordinal)).ToList();
                if (candidates.Count == 1)
                {
                    rows.Add(new DiffRow { Authored = p.Name, Built = candidates[0], Category = "renamed", Surface = p.Declared });
                    unclaimed.Remove(candidates[0]);
                    Claim(claimedBy, candidates[0], p.Name);
                }
                else if (candidates.Count > 1)
                {
                    rows.Add(new DiffRow { Authored = p.Name, Built = string.Join(" | ", candidates), Category = "unattributed", Surface = p.Declared });
                    foreach (var cnd in candidates) unclaimed.Remove(cnd);
                }
                else
                {
                    rows.Add(new DiffRow { Authored = p.Name, Built = "—", Category = "dropped", Surface = p.Declared });
                }
            }

            foreach (var kv in claimedBy)
                if (kv.Value.Count > 1)
                    rows.Add(new DiffRow { Authored = string.Join(" + ", kv.Value), Built = kv.Key, Category = "merged", Surface = "(two or more authored names on one built name)" });

            foreach (var b in unclaimed)
            {
                if (!string.IsNullOrEmpty(paramFilter) && b.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                rows.Add(new DiffRow { Authored = "—", Built = b, Category = "unattributed", Surface = "(built only)" });
            }
            return rows;
        }

        private static void Claim(Dictionary<string, List<string>> claimedBy, string built, string authored)
        {
            if (!claimedBy.TryGetValue(built, out var l)) claimedBy[built] = l = new List<string>();
            l.Add(authored);
        }

        private static string Refusal(string path, string stage) =>
            "# ReportComposition (bake)\n\nstatus: FAILED\n\nsummary: [ReportComposition] mode=bake => FAIL ("
            + stage + ") | log=" + path
            + "\n\nThe bake did not complete, so this artifact carries NO parameter table. Authored-census rows are "
            + "deliberately not published here: presenting them under a heading that promises composed truth is the "
            + "misread this door exists to prevent. Run `Report(<avatarRoot>)` without the flag for the authored census, "
            + "knowing it is authored.\n";

        private static string FullPath(string assetPath) =>
            Path.Combine(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length), assetPath);

        private static void WriteArtifact(string assetPath, string text)
        {
            try
            {
                string full = FullPath(assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, text);
                RunLogFormat.PublishArtifact(RunLogFormat.SnapshotDir, assetPath);
            }
            catch (Exception e)
            {
                Debug.LogError("[ReportComposition] could not write the bake artifact at " + assetPath + " ("
                             + e.GetType().Name + ") — the bake's result is unrecoverable, so treat this run as not taken.");
            }
        }
    }
}
