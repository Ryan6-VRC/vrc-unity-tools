using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
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
        /// <summary>Well past the ~30 s worst case measured on a complex avatar with an optimizer installed,
        /// so a slow bake is never called dead; short enough that a discarded callback is not a long wedge.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
        private static readonly Dictionary<int, bool> InFlight = new Dictionary<int, bool>();

        // Keyed on name AND instance id: the path must be STABLE (the caller re-reads it after a transport
        // timeout, so a timestamp is out) but two roots named "Avatar" in one scene would otherwise share a
        // file — B's pending stub overwriting A's result, and Verify(B) returning A's summary as B's.
        private static string ArtifactPath(GameObject root) =>
            RunLogFormat.SnapshotDir + "/composition-bake_" + RunLogFormat.Sanitize(root.name)
            + "_" + root.GetInstanceID().ToString("X", CultureInfo.InvariantCulture) + ".md";

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
            {
                // A domain reload discards the scheduled callback WITHOUT running it and clears the in-memory
                // in-flight flag, leaving the artifact reading `pending` forever. Nothing in the editor can be
                // asked whether the callback still exists, so age is the only available signal — and without
                // it the artifact's own "do not re-issue" instruction wedges the door permanently.
                var m = Regex.Match(text, @"^started: (.+)$", RegexOptions.Multiline);
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var started)
                    && DateTime.UtcNow - started > StaleAfter)
                    return "[ReportComposition] " + root.name + ": mode=bake => STALE (started "
                         + started.ToString("o", CultureInfo.InvariantCulture) + ", over "
                         + StaleAfter.TotalMinutes.ToString(CultureInfo.InvariantCulture)
                         + " min ago and still pending — the scheduled bake was almost certainly discarded by a "
                         + "domain reload; re-issue Report(<avatarRoot>, bake:true)) | log=" + path;
                return "[ReportComposition] " + root.name + ": mode=bake => PENDING | log=" + path;
            }
            string head = text.Split('\n').FirstOrDefault(l => l.StartsWith("summary: ", StringComparison.Ordinal));
            return head != null ? head.Substring("summary: ".Length).Trim()
                                : "[ReportComposition] " + root.name + ": mode=bake => OK | log=" + path;
        }

        private static void Run(GameObject root, ReportComposition.CensusResult census, string paramFilter, string path)
        {
            if (root == null)
            {
                WriteArtifact(path, Refusal(path, "the avatar root was destroyed before the bake ran", "(destroyed)"));
                return;
            }

            GameObject clone;
            string failedStage;
            if (!AvatarBake.Try(root, out clone, out failedStage))
            {
                // A loud refusal naming the stage — NEVER a silent fallback to the authored census. Emitting
                // authored rows under a heading that promises composed truth is the failure this door exists
                // to prevent, so bake mode publishes no table at all when the bake did not happen.
                WriteArtifact(path, Refusal(path, failedStage, root.name));
                Debug.LogError("[ReportComposition] " + root.name + ": mode=bake => FAIL (" + failedStage + ") | log=" + path);
                return;
            }

            try
            {
                string incomplete;
                var built = BuiltDeclarations(clone, out incomplete);
                var diff = Diff(census, built, paramFilter, incomplete == null);
                string summary = string.Format(CultureInfo.InvariantCulture,
                    "[ReportComposition] {0}: surfaces={1} params={2} kept={3} renamed={4} dropped={5} merged={6} unattributed={7} notInScope={8} builtSideUnread={9} mode=bake => OK | log={10}",
                    root.name, census.Surfaces.Count, census.Params.Count,
                    diff.Count(d => d.Category == "kept"), diff.Count(d => d.Category == "renamed"),
                    diff.Count(d => d.Category == "dropped"), diff.Count(d => d.Category == "merged"),
                    diff.Count(d => d.Category == "unattributed"), diff.Count(d => d.Category == "not-in-scope"),
                    diff.Count(d => d.Category == "built-side-unread"), path);

                var section = new List<string>
                {
                    "| authored | category | built | owning surface |",
                    "| --- | --- | --- | --- |",
                };
                foreach (var d in diff)
                    section.Add("| `" + RunLogFormat.Cell(d.Authored) + "` | " + d.Category + " | `" + RunLogFormat.Cell(d.Built) + "` | "
                              + RunLogFormat.Cell(d.Surface) + " |");
                section.Add("");
                if (incomplete != null)
                    section.Add("**The built read was incomplete: " + incomplete + ".** Exact matches and renames below "
                              + "are still measured facts; every unmatched authored name is reported "
                              + "`built-side-unread` rather than `dropped`, because this read cannot tell removal from "
                              + "not-looking. Treat the absence of a row as unknown, not as evidence.");
                section.Add("The built side is a DECLARATION set: the built descriptor's expression parameters union every "
                          + "parameter on every controller the clone plays. Every category below is relative to that.");
                section.Add("Categories: **kept** the built avatar declares the authored name unchanged; **renamed** exactly "
                          + "one built name is a prefixed form of it (the authored name behind a `/` or `_` separator); "
                          + "**dropped** no built declaration was attributable; **merged** two or more authored names "
                          + "resolved onto one built name, replacing their individual rows so the counts still sum; "
                          + "**built-side-unread** no match AND the built read was partial, so removal is not claimed; "
                          + "**unattributed** either a BUILT name no authored one claims, or an authored name more than one "
                          + "built name could be; **not-in-scope** a runtime-written name (physbone suffix, menu "
                          + "sub-parameter) that nothing declares, so a declaration set cannot carry it and its absence "
                          + "means nothing.");
                section.Add("Attribution is inference, not a pinned grammar — an `unattributed` row is the honest answer and "
                          + "beats a mapping the tool cannot support. The separator boundary is the whole of the rule; a "
                          + "bare suffix match would call authored `Toggle` a rename of built `Hair/HairToggle`.");
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

        internal struct DiffRow { public string Authored, Built, Category, Surface; }

        /// <summary>Every parameter name the BUILT clone declares: its descriptor's expression parameters
        /// UNION every parameter on every controller the clone plays. Both halves are needed. The expression
        /// asset alone is a synced-parameter budget, not the avatar's parameter set — a controller-only value
        /// (a driver scratch, a gesture built-in) never appears there, so diffing against it alone put every
        /// such name in <c>dropped</c>, whose plain reading is "the build removed it".</summary>
        internal static List<string> BuiltDeclarations(GameObject clone, out string incompleteReason)
        {
            incompleteReason = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var ep = d != null ? d.expressionParameters : null;
            if (ep != null && ep.parameters != null)
                foreach (var p in ep.parameters)
                    if (p != null && !string.IsNullOrEmpty(p.name)) names.Add(p.name);
            foreach (var anim in clone.GetComponentsInChildren<Animator>(true))
            {
                AddParams(anim != null ? anim.runtimeAnimatorController : null, names);
            }
            int unreadableLayers = 0, authoredLayers = 0;
            if (d != null)
            {
                foreach (var set in new[] { d.baseAnimationLayers, d.specialAnimationLayers })
                {
                    if (set == null) continue;
                    foreach (var l in set)
                    {
                        if (l.isDefault) continue;
                        authoredLayers++;
                        if (l.animatorController == null) unreadableLayers++;
                        else AddParams(l.animatorController, names);
                    }
                }
            }
            // MEASURED, and it is the difference between a report and a false claim: after the preprocess
            // chain the clone's playable-layer slots read null, so the built controllers are not reachable
            // the way an authoring avatar's are. Every authored name would then fail to match and be
            // reported `dropped` — "the build removed it" — when the truth is that this read never saw the
            // built side. The count is surfaced so the diff can decline to make that claim rather than
            // publishing it, and the door stays honest about a partial read instead of dressing it as one.
            if (unreadableLayers > 0)
                incompleteReason = unreadableLayers + " of " + authoredLayers + " non-default playable layers on the "
                    + "built clone hold no controller, so the built side is a PARTIAL read (the preprocess chain "
                    + "does not leave the built controllers in the descriptor's slots)";
            return names.ToList();
        }

        private static void AddParams(RuntimeAnimatorController rac, HashSet<string> into)
        {
            var ac = rac as AnimatorController;
            if (ac == null && rac is AnimatorOverrideController ovr) ac = ovr.runtimeAnimatorController as AnimatorController;
            if (ac == null || ac.parameters == null) return;
            foreach (var p in ac.parameters) if (!string.IsNullOrEmpty(p.name)) into.Add(p.name);
        }

        /// <summary>Is <paramref name="built"/> a prefixed form of <paramref name="authored"/>? The build
        /// prepends a namespace, so the authored name survives as a suffix behind a SEPARATOR. A bare
        /// EndsWith would make authored <c>Toggle</c> a candidate for built <c>Hair/HairToggle</c> and emit a
        /// confident, wrong provenance claim from the one door whose whole thesis is that it never makes one.
        /// The boundary is declared here rather than left implicit, and anything outside it stays
        /// unattributed rather than guessed.</summary>
        internal static bool IsPrefixedForm(string built, string authored)
        {
            if (string.IsNullOrEmpty(built) || string.IsNullOrEmpty(authored)) return false;
            if (built.Length <= authored.Length || !built.EndsWith(authored, StringComparison.Ordinal)) return false;
            char boundary = built[built.Length - authored.Length - 1];
            return boundary == '/' || boundary == '_';
        }

        /// <summary>Diff the authored census against the built declaration set. Pure — it touches no Unity
        /// object — which is what makes it unit-testable, and it is the part of this door most able to lie.
        ///
        /// Only rows the census marked diffable participate. A physbone suffix or a menu sub-parameter is
        /// written by the runtime and declared by nothing, so a declaration set cannot carry it: those are
        /// reported <c>not-in-scope</c> rather than counted as removed.
        ///
        /// <paramref name="paramFilter"/> is applied LAST, to both sides of each row. Filtering the authored
        /// census first — the obvious order — makes the answer depend on the filter: a built name whose
        /// authored counterpart was filtered out has nothing left to claim it and is reported built-only, so
        /// filtering on a surface prefix (the most natural use) would report that whole surface as
        /// unattributed.</summary>
        internal static List<DiffRow> Diff(ReportComposition.CensusResult census, List<string> built, string paramFilter,
                                           bool builtSideComplete = true)
        {
            var builtSet = new HashSet<string>(built, StringComparer.Ordinal);
            var unclaimed = new HashSet<string>(built, StringComparer.Ordinal);
            var claimedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var rowsByBuilt = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var rows = new List<DiffRow>();

            void Claim(string b, string authored, int rowIndex)
            {
                if (!claimedBy.TryGetValue(b, out var l)) claimedBy[b] = l = new List<string>();
                l.Add(authored);
                if (!rowsByBuilt.TryGetValue(b, out var idx)) rowsByBuilt[b] = idx = new List<int>();
                idx.Add(rowIndex);
                unclaimed.Remove(b);
            }

            foreach (var p in census.Params)
            {
                if (!p.Diffable)
                {
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = "—", Category = "not-in-scope",
                        Surface = "runtime-written (physbone suffix / menu sub-parameter) — nothing declares it, so a declaration set cannot carry it",
                    });
                    continue;
                }
                if (builtSet.Contains(p.Name))
                {
                    Claim(p.Name, p.Name, rows.Count);
                    rows.Add(new DiffRow { Authored = p.Name, Built = p.Name, Category = "kept", Surface = p.Declared });
                    continue;
                }
                var candidates = built.Where(b => IsPrefixedForm(b, p.Name)).ToList();
                if (candidates.Count == 1)
                {
                    Claim(candidates[0], p.Name, rows.Count);
                    rows.Add(new DiffRow { Authored = p.Name, Built = candidates[0], Category = "renamed", Surface = p.Declared });
                }
                else if (candidates.Count > 1)
                {
                    // Ambiguous, so nothing is attributed — and the candidates stay UNCLAIMED on purpose, or a
                    // genuinely built-only parameter that happens to match an ambiguous authored name would be
                    // swallowed: never given its own built-only row, and visible only inside a cell attributed
                    // to an unrelated parameter.
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = string.Join(" or ", candidates), Category = "unattributed",
                        Surface = p.Declared + " — more than one built name is a prefixed form of this one",
                    });
                }
                else if (builtSideComplete)
                {
                    rows.Add(new DiffRow { Authored = p.Name, Built = "—", Category = "dropped", Surface = p.Declared });
                }
                else
                {
                    // No match AND the built side is known partial: `dropped` would assert a removal this
                    // read cannot see. Say what is true instead.
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = "?", Category = "built-side-unread",
                        Surface = p.Declared + " — no built name matched, and the built read was incomplete, so removal is NOT the claim",
                    });
                }
            }

            // `merged` REPLACES the rows it summarises rather than riding beside them. Emitting one merged row
            // on top of the two renamed rows that produced it double-counts a single built parameter and makes
            // the category totals exceed the parameter count — a table lying about its own arithmetic.
            var drop = new HashSet<int>();
            var merged = new List<DiffRow>();
            foreach (var kv in claimedBy)
            {
                if (kv.Value.Count <= 1) continue;
                foreach (var i in rowsByBuilt[kv.Key]) drop.Add(i);
                merged.Add(new DiffRow
                {
                    Authored = string.Join(" + ", kv.Value), Built = kv.Key, Category = "merged",
                    Surface = "two or more authored names resolved onto one built name",
                });
            }
            var final = new List<DiffRow>();
            for (int i = 0; i < rows.Count; i++) if (!drop.Contains(i)) final.Add(rows[i]);
            final.AddRange(merged);

            foreach (var b in unclaimed)
                final.Add(new DiffRow { Authored = "—", Built = b, Category = "unattributed", Surface = "(built only)" });

            if (string.IsNullOrEmpty(paramFilter)) return final;
            return final.Where(r =>
                (r.Authored != null && r.Authored.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (r.Built != null && r.Built.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        private static string Refusal(string path, string stage, string name) =>
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
