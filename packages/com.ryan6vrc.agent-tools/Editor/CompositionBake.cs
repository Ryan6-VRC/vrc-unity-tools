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

            // `using`, so the SDK's paired post-callback fires on EVERY exit — including the refusal return
            // below, which sits inside the scope precisely because that early return used to skip the pairing.
            // The read happens INSIDE the scope by necessity, not by style: the post-callback destroys the
            // clone's generated assets, so every playable-layer controller BuiltDeclarations exists to read is
            // null once the scope closes (AvatarBake's doc comment has the measurement).
            using (var bake = AvatarBake.Begin(root))
            {
                if (!bake.Ok)
                {
                    // A loud refusal naming the stage — NEVER a silent fallback to the authored census. Emitting
                    // authored rows under a heading that promises composed truth is the failure this door exists
                    // to prevent, so bake mode publishes no table at all when the bake did not happen. A hook
                    // that THREW is reported as the crash it was, not as a refusal that never happened.
                    string failedStage = bake.DescribeFailure();
                    WriteArtifact(path, Refusal(path, failedStage, root.name));
                    Debug.LogError("[ReportComposition] " + root.name + ": mode=bake => FAIL (" + failedStage + ") | log=" + path);
                    return;
                }

                var clone = bake.Clone;
                string incomplete;
                var readNotes = new List<string>();
                var built = BuiltDeclarations(clone, readNotes, out incomplete);
                var diff = Diff(census, built, paramFilter, incomplete == null);
                string summary = string.Format(CultureInfo.InvariantCulture,
                    // `unattributed=` is deliberately GONE rather than kept with a narrower meaning: it used to
                    // count ambiguity and built-only rows together, so preserving the key while the number moves
                    // (145 → 8 on one measured avatar) would leave a reader comparing two artifacts with no
                    // signal that the denominator changed. Renaming both halves makes the change visible.
                    "[ReportComposition] {0}: surfaces={1} params={2} kept={3} renamed={4} dropped={5} merged={6} ambiguous={7} builtOnly={8} vrcReserved={9} notInScope={10} builtSideUnread={11} mode=bake => OK | log={12}",
                    root.name, census.Surfaces.Count, census.Params.Count,
                    diff.Count(d => d.Category == "kept"), diff.Count(d => d.Category == "renamed"),
                    diff.Count(d => d.Category == "dropped"), diff.Count(d => d.Category == "merged"),
                    diff.Count(d => d.Category == "ambiguous"), diff.Count(d => d.Category == "built-only"),
                    diff.Count(d => d.Category == "vrc-reserved"),
                    diff.Count(d => d.Category == "not-in-scope"),
                    diff.Count(d => d.Category == "built-side-unread"), path);

                var section = new List<string>
                {
                    "| authored | category | built | owning surface |",
                    "| --- | --- | --- | --- |",
                };
                foreach (var d in diff)
                    section.Add("| `" + RunLogFormat.Cell(d.Authored) + "` | " + d.Category + " | `" + RunLogFormat.Cell(d.Built) + "` | "
                              + RunLogFormat.Cell(d.Surface + d.Caveat) + " |");
                section.Add("");
                // Each note ends with a hard break and the block ends with a blank line, matching RenderBody's own
                // note convention: without both, CommonMark lazy continuation folds every following paragraph —
                // the incompleteness warning, the category legend, the arithmetic — into this blockquote.
                foreach (var n in readNotes) section.Add("> built-side read note: " + n + "  ");
                if (readNotes.Count > 0) section.Add("");
                if (incomplete != null)
                    section.Add("**The built read was incomplete: " + incomplete + ".** Exact matches and renames below "
                              + "are still measured facts; every unmatched authored name is reported "
                              + "`built-side-unread` rather than `dropped`, because this read cannot tell removal from "
                              + "not-looking. Treat the absence of a row as unknown, not as evidence.");
                section.Add("The built side is a DECLARATION set: the built descriptor's expression parameters union every "
                          + "parameter on every controller the clone plays. Every category below is relative to that.");
                section.Add("Categories: **kept** the built avatar declares the authored name unchanged; **renamed** exactly "
                          + "one built name is a prefixed form of it (the authored name behind a `/` or `_` separator); "
                          + "**dropped** no built name is a prefixed form of it at all; **merged** two or more authored names "
                          + "resolved onto one built name, replacing their individual rows so the counts still sum; "
                          + "**built-side-unread** no match AND the built read was partial, so removal is not claimed; "
                          + "**ambiguous** an authored name more than one built name could be, none attributed; "
                          + "**built-only** a BUILT name no authored one claims — a measured fact, and the normal home of "
                          + "build-minted internals and SDK-supplied names; **vrc-reserved** the same, for a name on VRChat's "
                          + "reserved list (`ControllerRules.IsVrcReserved`, the one predicate the lint rules and the "
                          + "controller compiler also use) — split out because those rows are the SDK's, not this avatar's, "
                          + "and reading a dozen of them as unexplained build output is the misread; **not-in-scope** a runtime-written name (physbone "
                          + "suffix, menu sub-parameter) that nothing declares, so a declaration set cannot carry it and its "
                          + "absence means nothing.");
                section.Add("Attribution is inference, not a pinned grammar — an `ambiguous` row is the honest answer and "
                          + "beats a mapping the tool cannot support. The separator boundary is the whole of the rule; a "
                          + "bare suffix match would call authored `Toggle` a rename of built `Hair/HairToggle`.");
                section.Add("**A built name already claimed by exact match is not another row's rename** — an exact match is "
                          + "the stronger claim. That precedence is a tie-breaker only: where it would empty a candidate set "
                          + "it is not applied, so `dropped` never rests on it. Where it decided a row — `renamed` or "
                          + "`ambiguous` — that row says so in its own owning-surface cell and names what was excluded, "
                          + "because the reading it forecloses is `merged`; and where such a row was itself replaced by a "
                          + "`merged` row, the merged row carries the same disclosure.");
                section.Add("**The arithmetic.** Authored rows (`kept` + `renamed` + `dropped` + `ambiguous` + "
                          + "`built-side-unread` + `not-in-scope`, plus each `merged` row standing in for the two or more it "
                          + "replaced) sum to `params=`; `built-only` and `vrc-reserved` rows are additional and belong to no "
                          + "authored name. One "
                          + "built name can appear BOTH inside an `A or B` cell and as its own `built-only` row: an ambiguous "
                          + "match deliberately claims nothing, or a genuinely built-only parameter would be hidden inside a "
                          + "cell belonging to an unrelated authored name. That is not double-counting.");
                section.Add("Optimizers found on the root: "
                          + (census.Optimizers.Count == 0 ? "(none)" : string.Join(", ", census.Optimizers))
                          + ". The full chain is what ships and is what was measured; disable them yourself for a "
                          + "pre-optimizer view.");

                string body = "summary: " + summary + "\n\n"
                            + ReportComposition.RenderBody(root, census, paramFilter, "bake (measured against a fresh build)", section);
                WriteArtifact(path, body);
                Debug.Log(summary);
            }   // scope closes: the clone is destroyed and OnPostprocessAvatar fires, in that order
        }

        /// <summary>One row of the diff. <c>Caveat</c> is kept OUT of <c>Surface</c> rather than concatenated into
        /// it so it can be CARRIED when a row is replaced: the <c>merged</c> pass destroys the rows it summarises,
        /// and a caveat living inside their prose died with them. Rendered immediately after <c>Surface</c>, so the
        /// cell reads the same either way.</summary>
        internal struct DiffRow { public string Authored, Built, Category, Surface, Caveat; }

        /// <summary>Every parameter name the BUILT clone declares: its descriptor's expression parameters
        /// UNION every parameter on every controller the clone plays. Both halves are needed. The expression
        /// asset alone is a synced-parameter budget, not the avatar's parameter set — a controller-only value
        /// (a driver scratch, a gesture built-in) never appears there, so diffing against it alone put every
        /// such name in <c>dropped</c>, whose plain reading is "the build removed it".
        /// <para>Two channels for an unreadable controller, and the split is deliberate.
        /// <paramref name="incompleteReason"/> is set only for a <b>playable-layer slot</b> — the avatar's own
        /// declaration set — because a non-null value downgrades every unmatched row on the avatar.
        /// <paramref name="notes"/> takes anything else (a child <c>Animator</c> playing a controller this read
        /// cannot resolve), naming it without downgrading a single category.</para></summary>
        internal static List<string> BuiltDeclarations(GameObject clone, List<string> notes, out string incompleteReason)
        {
            incompleteReason = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var ep = d != null ? d.expressionParameters : null;
            if (ep != null && ep.parameters != null)
                foreach (var p in ep.parameters)
                    if (p != null && !string.IsNullOrEmpty(p.name)) names.Add(p.name);
            // A controller here that cannot be read is NOTED, never a global downgrade: a nested
            // AnimatorOverrideController on a child prop is legal and defeats the one-hop cast below, and
            // downgrading a whole avatar's diff over one prop would be a far bigger lie than the prop's
            // absent parameters. Only the descriptor's own playable layers carry the avatar's declaration set.
            foreach (var anim in clone.GetComponentsInChildren<Animator>(true))
            {
                var rac = anim != null ? anim.runtimeAnimatorController : null;
                if (!AddParams(rac, names))
                    notes.Add("the `Animator` at `" + MergeSurfaces.PathOf(anim.gameObject) + "` plays `" + rac.name
                            + "` (" + rac.GetType().Name + "), which this read cannot resolve to an AnimatorController — "
                            + "its parameters are absent from the built side below. Not a partial read of the AVATAR's "
                            + "declaration set (the playable layers carry that), so no category is downgraded.");
            }
            var unreadableSlots = new List<string>();
            int slotsWithController = 0;
            if (d != null)
            {
                foreach (var set in new[] { d.baseAnimationLayers, d.specialAnimationLayers })
                {
                    if (set == null) continue;
                    foreach (var l in set)
                    {
                        // The `isDefault` guard stays. Measured post-bake, every slot reads isDefault=false with
                        // a real controller, so it never fires — but the SDK keeps the flag and a non-null
                        // controller mutually exclusive only along its INSPECTOR path, so a programmatic write
                        // can leave a default-flagged slot holding a controller the built avatar does not play.
                        // Reading that would union parameters from a controller nothing plays: a false `kept`
                        // masking a real `dropped`, which is the failure direction that matters here.
                        if (l.isDefault) continue;
                        // An EMPTY slot declares nothing, and that is all it means. It used to be counted as
                        // evidence the built side went unread — see the history note below.
                        if (l.animatorController == null) continue;
                        slotsWithController++;
                        if (!AddParams(l.animatorController, names))
                            unreadableSlots.Add(l.type + " → `" + l.animatorController.name + "` ("
                                              + l.animatorController.GetType().Name + ")");
                    }
                }
            }
            // The one condition that makes this a genuinely PARTIAL read: a playable layer holds a controller
            // whose parameters could not be read. Every authored name would then fail to match and be reported
            // `dropped` — "the build removed it" — when the truth is that this read never saw them.
            //
            // It used to key on an empty non-default slot instead, explained as "the preprocess chain does not
            // leave the built controllers in the descriptor's slots". That was true only of a clone read AFTER
            // the paired OnPostprocessAvatar had swept its generated assets (atelier vrc-unity-tools#118 fixed
            // the lifetime; AvatarBake's doc comment has the measurement). Measured after the fix: 8/8 slots
            // hold real controllers under `Packages/nadena.dev.ndmf/__Generated/`, on a VRCFury avatar and an
            // MA-only one alike. An empty slot is authoring state, and emptying one on a baked clone left the
            // built name set IDENTICAL — nothing was unread — while the old arm downgraded every unmatched row
            // on the avatar and suppressed the one claim bake mode exists to make.
            if (unreadableSlots.Count > 0)
                incompleteReason = unreadableSlots.Count + " playable-layer slot(s) hold a controller this read "
                    + "could not resolve to an AnimatorController (" + string.Join("; ", unreadableSlots)
                    + "), so their parameters are absent and the built side is a PARTIAL read";
            // The retarget above dropped a FALSE arm, and with it the only automated detection of the arm's one
            // TRUE case: a clone holding no controller in any slot and declaring nothing. That is the state #118
            // measured on a clone read after its generated assets were swept — not an authoring state, and not
            // survivable, because every authored name would fall to `dropped`: a confident "the build removed it"
            // across a whole avatar. Kept as a second arm rather than trusted to the caller, because the only
            // thing standing between this door and that state is AvatarBakeScope's lifetime, which this codebase
            // has already gotten wrong once and silently. Judgment-free, and unable to fire on a healthy bake:
            // a real built avatar always has a controller in a slot, and an avatar that truly declares nothing
            // has no diffable census rows for the flag to downgrade.
            else if (d != null && slotsWithController == 0 && names.Count == 0)
                incompleteReason = "the built clone's descriptor holds no playable-layer controller at all and its "
                    + "expression parameters declare nothing, so NOTHING of the built side was readable — the shape "
                    + "of a clone read after its generated assets were swept, not of an avatar with nothing on it";
            return names.ToList();
        }

        /// <summary>Union <paramref name="rac"/>'s parameters into <paramref name="into"/>. Returns <b>false</b>
        /// only when a controller was PRESENT and could not be read as an <c>AnimatorController</c> — the one
        /// condition that makes a built read partial, and a silent return before this.
        /// <para>A null controller returns true, deliberately: an absent controller declares nothing, and a
        /// child <c>Animator</c> with no controller is the common case on any avatar — counting it would make
        /// the incompleteness flag non-null essentially always and delete <c>dropped</c> as a category. An empty
        /// parameter list returns true too: that read succeeded and found nothing.</para></summary>
        private static bool AddParams(RuntimeAnimatorController rac, HashSet<string> into)
        {
            if (rac == null) return true;
            var ac = rac as AnimatorController;
            if (ac == null && rac is AnimatorOverrideController ovr) ac = ovr.runtimeAnimatorController as AnimatorController;
            if (ac == null) return false;
            if (ac.parameters == null) return true;
            foreach (var p in ac.parameters) if (!string.IsNullOrEmpty(p.name)) into.Add(p.name);
            return true;
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
        /// unattributed.
        /// <para>Precedence is filter-INDEPENDENT for a reason worth recording, since the live census reaching
        /// this method is already narrowed: an excludable built name <c>b</c> ends with a separator plus the
        /// authored name it would claim, so any filter matching that short name necessarily matches <c>b</c>
        /// too (matching is substring). A filter can therefore drop the short row but never strip the
        /// exact-claimer of a row it kept — so no verdict can flip on the filter.</para></summary>
        internal static List<DiffRow> Diff(ReportComposition.CensusResult census, List<string> built, string paramFilter,
                                           bool builtSideComplete = true)
        {
            var builtSet = new HashSet<string>(built, StringComparer.Ordinal);
            var unclaimed = new HashSet<string>(built, StringComparer.Ordinal);
            var claimedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var rowsByBuilt = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var rows = new List<DiffRow>();

            // Pass 1 — every built name an authored row claims by EXACT match. The predicate is exactly the
            // `kept` branch's own, and must stay that way: the natural shortcut ("census names ∩ built") lets a
            // NON-diffable row in, and a non-diffable row claims nothing in pass 2 — so its name would be
            // excluded from a candidate set while no row ever claims it, and one built name would read
            // `dropped` ("the build removed it") AND `built-only` ("present, unclaimed") in the same table.
            var exactClaim = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in census.Params)
                if (p.Diffable && builtSet.Contains(p.Name)) exactClaim.Add(p.Name);

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
                var raw = built.Where(b => IsPrefixedForm(b, p.Name)).ToList();
                // Pass 2 — a built name already claimed by exact match is not this row's rename: an exact match
                // is a stronger claim than a prefix inference. Without this, a gimmick that declares its own
                // internal `ObjectSync/D/Prop/PX/C` alongside the avatar's `Prop/PX/C` made every internal name
                // a spurious candidate for the shorter one: 63 of 271 rows on the demo avatar reported ambiguous
                // while the 63 real renames were orphaned as built-only. One defect, counted twice.
                //
                // But exclusion is a TIE-BREAKER, never evidence of removal — so it applies only while it leaves
                // a candidate standing. Emptying the set and falling through to `dropped` would assert "the build
                // removed it" on the ordinary idiom of an inner parameter exposed under a name the outer avatar
                // also declares (MA `remapTo`): authored `Toggle` + `Hair/Toggle` onto built `Hair/Toggle` is a
                // MERGE, and the fallback keeps it reported as one. `dropped` therefore still fires iff no built
                // name is a prefixed form at all — precisely the pre-precedence condition, so this rule cannot
                // manufacture a false removal.
                var narrowed = raw.Where(b => !exactClaim.Contains(b)).ToList();
                var candidates = narrowed.Count > 0 ? narrowed : raw;
                if (candidates.Count == 1)
                {
                    Claim(candidates[0], p.Name, rows.Count);
                    // Disclosed PER ROW, not once in the legend. Exclusion deletes the evidence of a possible
                    // merge, so a row that reads as a confident rename because its rivals were excluded has to
                    // carry that fact where a reader checking THIS row will see it — a legend sentence cannot be
                    // checked against a specific row, and these are exactly the rows the rule is adopted for.
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = candidates[0], Category = "renamed", Surface = p.Declared,
                        Caveat = candidates.Count == raw.Count ? null : ExclusionNote(raw, narrowed),
                    });
                }
                else if (candidates.Count > 1)
                {
                    // Ambiguous, so nothing is attributed — and the candidates stay UNCLAIMED on purpose, or a
                    // genuinely built-only parameter that happens to match an ambiguous authored name would be
                    // swallowed: never given its own built-only row, and visible only inside a cell attributed
                    // to an unrelated parameter.
                    //
                    // Exclusion is disclosed here too, and it matters MORE than on a renamed row: this is the one
                    // category whose whole purpose is honesty about not knowing, so a candidate list silently
                    // trimmed by precedence would make the trimming unknowable from the artifact.
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = string.Join(" or ", candidates), Category = "ambiguous",
                        Surface = p.Declared + " — more than one built name is a prefixed form of this one",
                        Caveat = candidates.Count == raw.Count ? null : ExclusionNote(raw, narrowed),
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
                // Every caveat on a replaced row is CARRIED onto the row that replaces it. A merged row is the most
                // confident statement this table makes — "these names became that one" — and it is assembled from
                // rows that may each have reached their claim only because precedence excluded a rival. Dropping
                // their caveats here would launder exactly the disclosure the renamed branch exists to make, and
                // silently: the foreclosed reading vanishes with the row that named it.
                var carried = new List<string>();
                foreach (var i in rowsByBuilt[kv.Key])
                {
                    drop.Add(i);
                    if (!string.IsNullOrEmpty(rows[i].Caveat) && !carried.Contains(rows[i].Caveat))
                        carried.Add(rows[i].Caveat);
                }
                merged.Add(new DiffRow
                {
                    Authored = string.Join(" + ", kv.Value), Built = kv.Key, Category = "merged",
                    Surface = "two or more authored names resolved onto one built name",
                    Caveat = carried.Count == 0 ? null : string.Concat(carried),
                });
            }
            var final = new List<DiffRow>();
            for (int i = 0; i < rows.Count; i++) if (!drop.Contains(i)) final.Add(rows[i]);
            final.AddRange(merged);

            // A built name no authored one claims. This is a MEASURED fact, not an inference failure — which is
            // why it no longer shares a category with ambiguity: build-minted internals and SDK-supplied names
            // are the normal population here, and the note says so IN THE CELL, following the empty-writers-cell
            // precedent, because a bare `(built only)` on a hundred rows reads as a hundred findings.
            foreach (var b in unclaimed)
                final.Add(ControllerRules.IsVrcReserved(b)
                    ? new DiffRow
                    {
                        Authored = "—", Built = b, Category = "vrc-reserved",
                        Surface = "(built only — a VRChat reserved parameter: declared nowhere, referenced everywhere, "
                                + "so no authored row can claim it)",
                    }
                    : new DiffRow
                    {
                        Authored = "—", Built = b, Category = "built-only",
                        Surface = "(built only — build-minted or SDK-supplied; nothing authored claims it, which is not a finding)",
                    });

            if (string.IsNullOrEmpty(paramFilter)) return final;
            return final.Where(r =>
                (r.Authored != null && r.Authored.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (r.Built != null && r.Built.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        /// <summary>What a `renamed` row owes its reader when exclusion is what made it unambiguous: which
        /// candidates were removed, why they were removable, and the alternative reading this row is NOT.</summary>
        private static string ExclusionNote(List<string> raw, List<string> narrowed)
        {
            var excluded = raw.Where(b => !narrowed.Contains(b)).Select(b => "`" + b + "`");
            return " — the single candidate only after excluding " + string.Join(", ", excluded)
                 + ", each already claimed by an authored name of its own (an exact match beats a prefix "
                 + "inference). If the build instead MERGED this name onto one of those, the truth is `merged` "
                 + "and this row does not say so.";
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
