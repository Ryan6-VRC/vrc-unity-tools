using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The animator-write-substrate DOOR: compile an animator-schema <c>.yaml</c> source into a real
    /// <see cref="AnimatorController"/> asset. Ties the verified pipeline together —
    /// parse (<see cref="AnimatorSchemaYaml"/>) → validate (<see cref="SchemaValidation"/>) →
    /// emit (<see cref="ControllerEmit"/>) → graph lint (<see cref="ControllerRules"/>) → atomic persist →
    /// RunLog. Pure PASS/FAIL contract: a real compile returns the one-line summary with the RunLog path
    /// in-band (<c>… =&gt; OK | log=&lt;path&gt;</c>); a whatIf preview leaves NOTHING on disk and returns
    /// <c>… =&gt; OK (whatIf) | log=&lt;path&gt;</c>; any refusal is a bare
    /// <c>[CompileController] FAIL: &lt;reason / named offenders&gt;</c> with no trailer and no asset written.
    ///
    /// <para>ATOMICITY: nothing reaches <c>outDir</c> unless parse + validate + emit + graph-lint all pass. Parse
    /// or validate failure writes no asset (emission never runs). A whatIf run emits to a scratch temp, lints
    /// there, then deletes it. A recompile OVER an existing controller resets it in place for GUID stability
    /// (see <see cref="ControllerEmit"/>), which strips it before the fallible emit steps — so an overwrite is
    /// first PROVEN by a full emit+lint into a throwaway temp (see <c>ProofCompile</c>); the prior controller is
    /// touched only after that proof passes clean. A failing overwrite (bad emit or lint) therefore leaves the
    /// prior good controller untouched. A FRESH compile has nothing to protect — its partial is just deleted.</para>
    ///
    /// <para>IDEMPOTENCE: recompiling the same source to the same <c>outDir</c> reuses the controller at
    /// <c>&lt;outDir&gt;/&lt;name&gt;.controller</c> and keeps its GUID (ControllerEmit resets sub-assets in
    /// place rather than delete + recreate). Emission is a pure function of the document, so a layer/clip
    /// absent from a second source does not survive.</para>
    ///
    /// <para>The RunLog body carries the compile-only advisories — they never fail the compile: the
    /// per-layer FRAME-LATENCY of the longest conditional-transition chain (one transition per frame — the
    /// cost of a multi-hop codec) and the DRIVER-ISOLATION conflicts (a VRCAvatarParameterDriver Set/Add
    /// targeting a param a clip also writes — runtime.md: a driver cannot set a clip-written/AAP param).</para>
    /// </summary>
    [AgentTool]
    public static class CompileController
    {
        /// <summary>Compile the animator-schema YAML at <paramref name="sourcePath"/> (a filesystem path) into
        /// a controller under <paramref name="outDir"/> (an <c>Assets/…</c>-relative folder). With
        /// <paramref name="whatIf"/> the whole pipeline runs against a scratch temp that is deleted before
        /// return — the outDir is never touched. Returns the one-line summary (see class docs).</summary>
        public static string Compile(string sourcePath, string outDir, bool whatIf = false)
        {
            // ── 1. Read the source file ──────────────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(sourcePath)) return Fail("sourcePath is empty");
            if (!File.Exists(sourcePath)) return Fail("source file not found: " + sourcePath);
            string text;
            try { text = File.ReadAllText(sourcePath); }
            catch (Exception e) { return Fail("could not read source '" + sourcePath + "': " + e.Message); }

            // ── 2. Parse (throws SchemaException on malformed text — no asset written) ────────────────
            AnimDocument doc;
            try { doc = AnimatorSchemaYaml.Parse(text, sourcePath); }
            catch (SchemaException se) { return Fail("parse: " + se.Message); }
            catch (Exception e) { return Fail("parse: " + e.GetType().Name + ": " + e.Message); }

            if (string.IsNullOrEmpty(doc.ControllerName)) return Fail("document declares no controller name");
            if (string.IsNullOrEmpty(outDir)) return Fail("outDir is empty");

            // ── 3. Document validation (named offenders → FAIL, nothing written) ─────────────────────
            var vErrors = SchemaValidation.Validate(doc);
            if (vErrors.Count > 0)
                return Fail("validation failed (" + vErrors.Count + "): " + string.Join("  ", vErrors));

            string name = doc.ControllerName;
            string cleanOut = NormalizeDir(outDir);
            string finalPath = cleanOut + "/" + name + ".controller";
            bool controllerPreExisted = !whatIf && AssetDatabase.LoadAssetAtPath<AnimatorController>(finalPath) != null;

            // ── 4a. PROOF COMPILE — the atomicity guarantee for an overwrite ─────────────────────────
            // ControllerEmit strips a prior controller IN PLACE (for GUID stability), and several emit steps
            // (transition targets, behaviour kinds, clip bindings) throw AFTER that strip — so a mid-emit or
            // lint failure on a real overwrite would leave the prior good controller stripped/empty. Before
            // touching it, run the WHOLE emit+lint pipeline against a throwaway temp; only if it passes clean
            // do we compile for real. A fresh compile needs no proof (a partial is just deleted); whatIf already
            // emits to a temp. Emission is a pure function of (document, AssetDatabase), so a proof that passes
            // guarantees the real in-place emit — same inputs — cannot throw or fail lint either.
            if (controllerPreExisted)
            {
                string proofDir = ScratchTemp();
                string proofFail = ProofCompile(doc, proofDir, text);
                AssetDatabase.DeleteAsset(proofDir);
                if (proofFail != null) return Fail(proofFail); // prior controller left untouched
            }

            string tempFolder = whatIf ? ScratchTemp() : null;
            string emitDir = whatIf ? tempFolder : cleanOut;
            string paramsPath = emitDir + "/" + name + "_Parameters.asset";

            // ── Out-of-band drift warning (real compile only, when an existing compiled asset is present) ─
            if (!whatIf) WarnIfOutOfBand(finalPath, text);

            // ── 4b. Emit into the target (real outDir, or the whatIf temp) ────────────────────────────
            ControllerEmit.EmitResult built;
            try { ControllerEmit.Build(doc, emitDir, text, out built); }
            catch (ControllerEmit.EmitException ee) { CleanupAfterEmit(whatIf, tempFolder, finalPath, controllerPreExisted); return Fail("emit: " + ee.Message); }
            catch (Exception e) { CleanupAfterEmit(whatIf, tempFolder, finalPath, controllerPreExisted); return Fail("emit: " + e.GetType().Name + ": " + e.Message); }

            // Persist the VRCExpressionParameters side-asset (ControllerEmit builds it in-memory only). REUSE an
            // existing asset IN PLACE — overwrite its parameters, never delete+recreate — so its GUID survives a
            // recompile (mirroring the controller's reset-in-place: any expressionParameters reference stays
            // valid, and an unchanged source yields no Git churn). Create only when absent; and when NO param
            // survives (all built-in/scratch), drop any stale asset so it can't linger. In whatIf the whole
            // emit lands in the temp folder and is swept with it.
            var existingParams = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(paramsPath);
            bool paramsPreExisted = existingParams != null;
            if (built.Params != null)
            {
                if (existingParams != null) { existingParams.parameters = built.Params.parameters; EditorUtility.SetDirty(existingParams); }
                else AssetDatabase.CreateAsset(built.Params, paramsPath);
            }
            else if (existingParams != null) AssetDatabase.DeleteAsset(paramsPath);

            // ── 5. Pre-emission graph lint (roots empty ⇒ broken-binding rule skipped: no scene) ─────
            var lint = ControllerRules.Run(built.Controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);
            if (lint.MissingMotion > 0 || lint.UndeclaredParam > 0 || lint.EntryShadow > 0 || lint.DeadTransition > 0)
            {
                CleanupAfterLint(whatIf, tempFolder, finalPath, paramsPath, controllerPreExisted, paramsPreExisted);
                string offenders = string.Join("  ", lint.Errors.Select(o => o.Kind + " @ " + o.Where + ": " + o.Detail));
                return Fail("post-emit graph lint (" + lint.Errors.Count + "): " + offenders);
            }

            // ── 6. Compile-only advisories (into the RunLog body; never fail the compile) ────────────
            var frameLatency = FrameLatencyAdvisories(doc);
            var driverIsolation = DriverIsolationAdvisories(doc);

            int states = doc.Layers.Sum(l => CountStates(l.Root));
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CompileController] {0}: layers={1} states={2} params={3} => OK{4}",
                name, doc.Layers.Count, states, doc.Parameters.Count, whatIf ? " (whatIf)" : "");

            string body = BuildBody(doc, finalPath, lint, frameLatency, driverIsolation, whatIf);

            // ── 7/8. Finalize: whatIf sweeps the temp; a real compile saves the asset ────────────────
            if (whatIf) { if (tempFolder != null) AssetDatabase.DeleteAsset(tempFolder); }
            else AssetDatabase.SaveAssets();

            string res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "compilecontroller_" + name, summary, body, ".md");
            Debug.Log(res);
            return res;
        }

        // ── Out-of-band edit detection ───────────────────────────────────────────────────────────────
        // Before clobbering an EXISTING compiled controller, compare the srchash stamped in its provenance
        // (the source that produced it) against the current source's hash. A mismatch means the on-disk
        // controller no longer corresponds to the source we are compiling — it was compiled from a different
        // source or hand-edited out of band. Best-effort: WARN and proceed, never block.
        private static void WarnIfOutOfBand(string finalPath, string sourceText)
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(finalPath);
            if (existing == null) return; // fresh compile — nothing to clobber
            var importer = AssetImporter.GetAtPath(finalPath);
            string ud = importer != null ? importer.userData : null;
            if (string.IsNullOrEmpty(ud) || ud.IndexOf("compiled-from:", StringComparison.Ordinal) < 0)
                return; // not compiled by us (no provenance) — no drift to assert
            string stored = ExtractField(ud, "srchash:");
            string current = ControllerEmit.SourceHash(sourceText ?? "");
            if (!string.IsNullOrEmpty(stored) && stored != current)
                Debug.LogWarning("[CompileController] overwriting '" + finalPath + "': provenance srchash=" + stored
                    + " differs from the current source (" + current + ") — the on-disk controller was compiled from a "
                    + "different source or hand-edited out of band; this recompile will clobber those changes.");
        }

        private static string ExtractField(string userData, string key)
        {
            int i = userData.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int end = userData.IndexOf(';', i);
            return end < 0 ? userData.Substring(i) : userData.Substring(i, end - i);
        }

        // ── Advisory: frame latency (longest FIRING-transition chain per layer) ───────────────────────
        // A multi-hop chain pays latency at every hop: a conditional hop resolves a frame later, and an
        // exit-time hop waits out its source state's clip. The binary codec walk (Start→Test0→…) advances
        // almost entirely through exit-time hops, so BOTH edge kinds must count or the advisory reads empty on
        // the very fixture built to demonstrate it. Report the longest simple chain of FIRING transitions
        // (conditional, or exit-time — explicit or via the doc default); a dead transition (no condition + no
        // exit time) never fires and is excluded, as is to-Exit. Only genuine multi-hop chains (≥2 hops) surface.
        private static List<string> FrameLatencyAdvisories(AnimDocument doc)
        {
            var lines = new List<string>();
            bool defaultExitTime = doc.Defaults.TransitionHasExitTime;
            foreach (var layer in doc.Layers)
            {
                var (chain, overBudget) = LongestFiringChain(layer, defaultExitTime);
                if (overBudget)
                    lines.Add("layer '" + (layer.Name ?? "(unnamed)")
                        + "' frame-latency advisory skipped — transition graph too dense to bound cheaply");
                else if (chain != null && chain.Count >= 3)
                    lines.Add("layer '" + (layer.Name ?? "(unnamed)") + "' chain '" + string.Join("→", chain)
                        + "' is " + (chain.Count - 1) + " hops of latency (each conditional hop ~1 frame; an "
                        + "exit-time hop costs its state's clip length)");
            }
            return lines;
        }

        // Returns (longest firing chain, overBudget). overBudget=true means the DFS hit its step budget and the
        // result is not trustworthy — the caller surfaces that rather than a possibly-wrong chain.
        private static (List<string> chain, bool overBudget) LongestFiringChain(Layer layer, bool defaultExitTime)
        {
            var states = new List<State>();
            CollectStates(layer.Root, states);
            if (states.Count == 0 || states.Count > 128) return (null, false); // pathological-size guard
            var names = new HashSet<string>(states.Select(s => s.Name));

            var adj = new Dictionary<string, List<string>>();
            foreach (var s in states)
            {
                if (!adj.ContainsKey(s.Name)) adj[s.Name] = new List<string>();
                foreach (var t in s.Transitions)
                {
                    if (t == null || t.ToExit || string.IsNullOrEmpty(t.To)) continue;
                    bool conditional = t.When != null && t.When.Count > 0;
                    bool exitTimed = t.ExitTime.HasValue || (!conditional && defaultExitTime);
                    if (!conditional && !exitTimed) continue; // dead transition — never fires
                    if (names.Contains(t.To)) adj[s.Name].Add(t.To);
                }
            }

            // Longest simple path is NP-hard; the node-count guard bounds nodes but not edge density, so a densely
            // connected layer could still explode. Bound the DFS by a step budget — an advisory is never worth
            // hanging a compile — and report over-budget rather than a truncated (wrong) chain.
            const int StepBudget = 50000;
            int steps = 0;
            bool overBudget = false;
            var best = new List<string>();
            var onPath = new HashSet<string>();
            var cur = new List<string>();
            void Dfs(string node)
            {
                if (overBudget) return;
                if (++steps > StepBudget) { overBudget = true; return; }
                cur.Add(node); onPath.Add(node);
                if (cur.Count > best.Count) best = new List<string>(cur);
                foreach (var nx in adj[node])
                    if (!onPath.Contains(nx)) Dfs(nx);
                cur.RemoveAt(cur.Count - 1); onPath.Remove(node);
            }
            foreach (var s in states) { Dfs(s.Name); if (overBudget) break; }
            return overBudget ? (null, true) : (best, false);
        }

        // ── Advisory: AAP / driver isolation ─────────────────────────────────────────────────────────
        // A VRCAvatarParameterDriver Set/Add cannot durably set a parameter a clip also writes: the clip's
        // animator curve overwrites it every frame it is active (runtime.md). Flag every driver target that
        // collides with a clip-written (declared) parameter.
        private static List<string> DriverIsolationAdvisories(AnimDocument doc)
        {
            var paramNames = new HashSet<string>(doc.Parameters.Select(p => p.Name));
            var written = new Dictionary<string, string>(); // param -> first clip that writes it
            foreach (var c in doc.Clips)
            {
                foreach (var k in c.Sets.Keys)
                    if (paramNames.Contains(k) && !written.ContainsKey(k)) written[k] = c.Name;
                foreach (var cs in c.Curves)
                    if (cs.Binding != null && paramNames.Contains(cs.Binding) && !written.ContainsKey(cs.Binding)) written[cs.Binding] = c.Name;
            }

            var lines = new List<string>();
            if (written.Count == 0) return lines;
            foreach (var layer in doc.Layers)
                WalkDrivers(layer.Root, layer.Name ?? "(unnamed)", written, lines);
            return lines;
        }

        private static void WalkDrivers(StateMachine sm, string layer, Dictionary<string, string> written, List<string> lines)
        {
            if (sm == null) return;
            foreach (var b in sm.Behaviours) DriverConflicts(b, "layer '" + layer + "' (SM behaviour)", written, lines);
            foreach (var s in sm.States)
            {
                if (s == null) continue;
                foreach (var b in s.Behaviours) DriverConflicts(b, "layer '" + layer + "' state '" + s.Name + "'", written, lines);
            }
            foreach (var sub in sm.Machines)
                if (sub != null && sub.Machine != null) WalkDrivers(sub.Machine, layer, written, lines);
        }

        private static void DriverConflicts(Behaviour b, string where, Dictionary<string, string> written, List<string> lines)
        {
            if (b == null || b.Kind != "driver" || b.Fields == null) return;
            // Every driver op whose map KEY is the destination param collides identically with a clip that
            // writes that param — set/add and equally copy/random (their dest is the map key too).
            foreach (var key in new[] { "set", "add", "copy", "random" })
            {
                if (!b.Fields.TryGetValue(key, out var val) || !(val is Dictionary<string, object> map)) continue;
                foreach (var e in map)
                    if (written.TryGetValue(e.Key, out var clip))
                        lines.Add("driver `" + key + "` at " + where + " targets `" + e.Key
                            + "` which clip `" + clip + "` also writes — a driver cannot set a clip-written (AAP) param (runtime.md)");
            }
        }

        // ── RunLog body ──────────────────────────────────────────────────────────────────────────────
        private static string BuildBody(AnimDocument doc, string finalPath, LintResult lint,
            List<string> frameLatency, List<string> driverIsolation, bool whatIf)
        {
            var sb = new StringBuilder();
            sb.Append("# CompileController: ").Append(doc.ControllerName).Append('\n');
            sb.Append("source: `").Append(doc.SourcePath ?? "(none)").Append("`  \n");
            sb.Append(whatIf ? "**WHATIF — no asset written**  \n" : "out: `" + finalPath + "`  \n");
            sb.Append("layers=").Append(doc.Layers.Count)
              .Append(" states=").Append(doc.Layers.Sum(l => CountStates(l.Root)))
              .Append(" params=").Append(doc.Parameters.Count).Append("  \n");

            sb.Append("\n## Post-emit graph lint\n\n");
            sb.Append("missingMotion=").Append(lint.MissingMotion)
              .Append(" undeclaredParam=").Append(lint.UndeclaredParam)
              .Append(" entryShadow=").Append(lint.EntryShadow)
              .Append(" deadTransition=").Append(lint.DeadTransition)
              .Append(" (errors ").Append(lint.Errors.Count).Append(") — PASS\n");
            if (lint.Advisories.Count > 0)
                foreach (var o in lint.Advisories)
                    sb.Append("- advisory: **").Append(o.Kind).Append("** ").Append(o.Where).Append(" — ").Append(o.Detail).Append('\n');

            sb.Append("\n## Compile advisory: frame latency\n\n");
            if (frameLatency.Count == 0) sb.Append("_(none — no multi-hop conditional chain)_\n");
            else foreach (var l in frameLatency) sb.Append("- ").Append(l).Append('\n');

            sb.Append("\n## Compile advisory: driver isolation (AAP)\n\n");
            if (driverIsolation.Count == 0) sb.Append("_(none)_\n");
            else foreach (var l in driverIsolation) sb.Append("- ").Append(l).Append('\n');

            return sb.ToString();
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────
        private static void CollectStates(StateMachine sm, List<State> into)
        {
            if (sm == null) return;
            foreach (var s in sm.States) if (s != null) into.Add(s);
            foreach (var sub in sm.Machines) if (sub != null && sub.Machine != null) CollectStates(sub.Machine, into);
        }

        private static int CountStates(StateMachine sm)
        {
            if (sm == null) return 0;
            int n = sm.States.Count;
            foreach (var sub in sm.Machines) if (sub != null && sub.Machine != null) n += CountStates(sub.Machine);
            return n;
        }

        private static string NormalizeDir(string dir)
        {
            dir = dir.Replace('\\', '/');
            while (dir.EndsWith("/", StringComparison.Ordinal)) dir = dir.Substring(0, dir.Length - 1);
            return dir;
        }

        private static string ScratchTemp() => "Assets/Agent/Scratch/compile_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        // Run the full emit + graph-lint pipeline into <paramref name="dir"/> (a throwaway temp) purely to prove
        // a real overwrite will succeed before it strips the prior controller. Returns null on success, or the
        // FAIL reason (same shape as the real paths). Skips persisting the params side-asset — lint reads the
        // in-memory controller, not that asset — so the caller only has to sweep <paramref name="dir"/>.
        private static string ProofCompile(AnimDocument doc, string dir, string text)
        {
            ControllerEmit.EmitResult built;
            try { ControllerEmit.Build(doc, dir, text, out built); }
            catch (ControllerEmit.EmitException ee) { return "emit: " + ee.Message; }
            catch (Exception e) { return "emit: " + e.GetType().Name + ": " + e.Message; }

            var lint = ControllerRules.Run(built.Controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);
            if (lint.MissingMotion > 0 || lint.UndeclaredParam > 0 || lint.EntryShadow > 0 || lint.DeadTransition > 0)
                return "post-emit graph lint (" + lint.Errors.Count + "): "
                     + string.Join("  ", lint.Errors.Select(o => o.Kind + " @ " + o.Where + ": " + o.Detail));
            return null;
        }

        // Emit failed before any params side-asset was written. whatIf sweeps its temp. A FRESH compile deletes
        // the just-created partial. An overwrite never reaches here mid-strip — ProofCompile clears the emit in
        // a temp before the prior controller is touched — so a pre-existing controller is left intact; the guard
        // is belt-and-suspenders.
        private static void CleanupAfterEmit(bool whatIf, string tempFolder, string finalPath, bool controllerPreExisted)
        {
            if (whatIf) { if (tempFolder != null && AssetDatabase.IsValidFolder(tempFolder)) AssetDatabase.DeleteAsset(tempFolder); return; }
            if (!controllerPreExisted) AssetDatabase.DeleteAsset(finalPath);
        }

        // Post-emit lint failed after a successful emit — roll back so nothing that fails lint reaches outDir.
        // ProofCompile makes this unreachable for an overwrite (an overwrite is proven lint-clean first), but the
        // guards make the atomicity local rather than resting on that cross-function invariant: delete ONLY a
        // freshly-created controller / params asset, never one that pre-existed the compile.
        private static void CleanupAfterLint(bool whatIf, string tempFolder, string finalPath, string paramsPath,
            bool controllerPreExisted, bool paramsPreExisted)
        {
            if (whatIf) { if (tempFolder != null && AssetDatabase.IsValidFolder(tempFolder)) AssetDatabase.DeleteAsset(tempFolder); return; }
            if (!controllerPreExisted) AssetDatabase.DeleteAsset(finalPath);
            if (!paramsPreExisted && !string.IsNullOrEmpty(paramsPath)) AssetDatabase.DeleteAsset(paramsPath);
        }

        private static string Fail(string why)
        {
            string err = "[CompileController] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }
    }

    /// <summary>Menu door for <see cref="CompileController"/> — resolves a selected/prompted <c>.yaml</c> and
    /// an output folder, then delegates. ZERO compile logic lives here (Compile logs its own result).</summary>
    internal static class CompileControllerMenu
    {
        [MenuItem("Tools/Agent/Animator/Compile Controller…")]
        private static void Door()
        {
            string src = null;
            var sel = Selection.activeObject;
            if (sel != null)
            {
                string p = AssetDatabase.GetAssetPath(sel);
                if (!string.IsNullOrEmpty(p) &&
                    (p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
                    src = Path.GetFullPath(p);
            }
            if (src == null) src = EditorUtility.OpenFilePanel("Select animator-schema YAML", Application.dataPath, "yaml");
            if (string.IsNullOrEmpty(src)) return;

            string abs = EditorUtility.SaveFolderPanel("Output folder (must be under Assets)", Application.dataPath, "");
            if (string.IsNullOrEmpty(abs)) return;
            string outDir = ToProjectRelative(abs);
            if (outDir == null) { Debug.LogError("[CompileController] output folder must be under this project's Assets/."); return; }

            CompileController.Compile(src, outDir, false);
        }

        private static string ToProjectRelative(string abs)
        {
            abs = abs.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (abs == data) return "Assets";
            return abs.StartsWith(data + "/", StringComparison.Ordinal) ? "Assets" + abs.Substring(data.Length) : null;
        }
    }
}
