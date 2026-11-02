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
    /// Compiles an animator-schema <c>.yaml</c> into an <see cref="AnimatorController"/> plus its expression-parameter
    /// and menu side assets: parse, validate, emit, graph lint, persist. Verdicts are <c>=&gt; OK</c> and
    /// <c>=&gt; CLASSIFY</c> (an unresolved motion ref was dropped), either with <c>(whatIf)</c>, and <c>=&gt; FAIL</c>
    /// naming the source leaf, each ending <c>| log=&lt;path&gt;</c>. Advisories go in the RunLog body and never fail
    /// the compile.
    /// Contract: docs/animator.md §The compile/decompile substrate. Schema: docs/animator-schema.md.
    /// </summary>
    [AgentTool]
    public static class CompileController
    {
        /// <summary>Compile the animator-schema YAML at <paramref name="sourcePath"/> (a filesystem path) into
        /// a controller under <paramref name="outDir"/> (an <c>Assets/…</c>-relative folder). With
        /// <paramref name="whatIf"/> the whole pipeline runs against a scratch temp that is deleted before
        /// return — the outDir is never touched. Returns the one-line summary (see class docs).</summary>
        public static string Run(string sourcePath, string outDir, bool whatIf = false)
        {
            string failLabel = SafeLeaf(sourcePath);

            if (string.IsNullOrEmpty(sourcePath)) return Fail(failLabel, sourcePath, "sourcePath is empty");
            if (!File.Exists(sourcePath)) return Fail(failLabel, sourcePath, "source file not found: " + sourcePath);
            string text;
            try { text = File.ReadAllText(sourcePath); }
            catch (Exception e) { return Fail(failLabel, sourcePath, "could not read source '" + sourcePath + "': " + e.Message); }

            AnimDocument doc;
            try { doc = AnimatorSchemaYaml.Parse(text, sourcePath); }
            catch (SchemaException se) { return Fail(failLabel, sourcePath, "parse: " + se.Message); }
            catch (Exception e) { return Fail(failLabel, sourcePath, "parse: " + e.GetType().Name + ": " + e.Message); }

            if (string.IsNullOrEmpty(doc.ControllerName)) return Fail(failLabel, sourcePath, "document declares no controller name");
            if (string.IsNullOrEmpty(outDir)) return Fail(failLabel, sourcePath, "outDir is empty");

            var vErrors = SchemaValidation.Validate(doc);
            if (vErrors.Count > 0)
                return Fail(failLabel, sourcePath, "validation failed (" + vErrors.Count + "): " + string.Join("  ", vErrors));

            string name = doc.ControllerName;
            string cleanOut = NormalizeDir(outDir);
            string finalPath = cleanOut + "/" + name + ".controller";
            bool controllerPreExisted = !whatIf && AssetDatabase.LoadAssetAtPath<AnimatorController>(finalPath) != null;

            // Proof compile. ControllerEmit strips a prior controller in place, and several emit steps (transition
            // targets, behaviour kinds, clip bindings) throw after that strip, so a mid-emit or lint failure would
            // leave it stripped. Emission is a pure function of (document, AssetDatabase), so a proof that passes in a
            // throwaway temp means the real emit over the same inputs cannot throw or fail lint either.
            if (controllerPreExisted)
            {
                string proofDir = ScratchTemp();
                string proofFail = ProofCompile(doc, proofDir, text);
                AssetDatabase.DeleteAsset(proofDir);
                if (proofFail != null) return Fail(failLabel, sourcePath, proofFail);
            }

            string tempFolder = whatIf ? ScratchTemp() : null;
            string emitDir = whatIf ? tempFolder : cleanOut;
            string paramsPath = emitDir + "/" + name + "_Parameters.asset";
            string menuPath = emitDir + "/" + name + "_Menu.asset";

            // Captured before emit's EnsureFolder creates them, so a failed fresh compile can roll them back.
            List<string> newFolders = whatIf ? null : NewFolders(cleanOut);

            if (!whatIf) WarnIfOutOfBand(finalPath, text);

            ControllerEmit.EmitResult built;
            try { ControllerEmit.Build(doc, emitDir, text, out built); }
            catch (ControllerEmit.EmitException ee) { CleanupAfterEmit(whatIf, tempFolder, finalPath, controllerPreExisted, newFolders); return Fail(failLabel, sourcePath, "emit: " + ee.Message); }
            catch (Exception e) { CleanupAfterEmit(whatIf, tempFolder, finalPath, controllerPreExisted, newFolders); return Fail(failLabel, sourcePath, "emit: " + e.GetType().Name + ": " + e.Message); }

            // Captured BEFORE the side-asset writes below (which destroy the in-memory root): a destroyed
            // UnityEngine.Object compares equal to null, so a later `built.Menu != null` would read false.
            bool emittedMenu = built.Menu != null;

            // Lint before the side assets persist. A fresh compile over a folder already holding a params or menu
            // asset skips ProofCompile, and CleanupAfterLint removes only what this run created, so persisting first
            // would let a lint failure delete or overwrite an asset it cannot restore.
            var lint = ControllerRules.Run(built.Controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);
            if (lint.MissingMotion > 0 || lint.UndeclaredParam > 0 || lint.NonFloatBlendParam > 0 || lint.NonFloatParamCurve > 0 || lint.DriverOnAnimatedParam > 0 || lint.EntryShadow > 0 || lint.DeadTransition > 0)
            {
                CleanupAfterLint(whatIf, tempFolder, finalPath, controllerPreExisted, newFolders);
                built?.DestroyUnpersisted();
                string offenders = string.Join("  ", lint.Errors.Select(o => o.Kind + " @ " + o.Where + ": " + o.Detail));
                return Fail(failLabel, sourcePath, "post-emit graph lint (" + lint.Errors.Count + "): " + offenders);
            }

            // ControllerEmit builds the side assets in memory only. An existing one is overwritten in place, never
            // deleted and recreated, so the GUID a consumer holds survives a recompile. Null Params means no parameter
            // survived (all built-in or scratch), so a stale asset is dropped.
            var existingParams = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(paramsPath);
            if (built.Params != null)
            {
                if (existingParams != null)
                {
                    existingParams.parameters = built.Params.parameters;
                    EditorUtility.SetDirty(existingParams);
                    // The handed-off array is managed, so it outlives this destroy.
                    UnityEngine.Object.DestroyImmediate(built.Params);
                }
                else AssetDatabase.CreateAsset(built.Params, paramsPath);
            }
            else if (existingParams != null) AssetDatabase.DeleteAsset(paramsPath);

            // A consumer's FullController `menus:` row references this file by GUID. Sub-menu pages are sub-assets of it,
            // so the tree travels under one GUID. Every prior page is removed first, or a renamed or deleted sub-menu
            // would linger inside the file.
            var existingMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(menuPath);
            if (built.Menu != null)
            {
                if (existingMenu != null)
                {
                    foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(menuPath))
                    {
                        if (sub == null || sub == existingMenu) continue;
                        AssetDatabase.RemoveObjectFromAsset(sub);
                        UnityEngine.Object.DestroyImmediate(sub, true);
                    }
                    existingMenu.controls = built.Menu.controls;
                    // A main asset renamed on disk otherwise keeps its old m_Name: the in-place write skips Unity's
                    // filename-driven rename.
                    existingMenu.name = built.Menu.name;
                    // Drop the in-memory root that handed off its controls, or a recompile leaves an unowned ScriptableObject.
                    UnityEngine.Object.DestroyImmediate(built.Menu);
                }
                else
                {
                    AssetDatabase.CreateAsset(built.Menu, menuPath);
                    existingMenu = built.Menu;
                }
                foreach (var child in built.MenuChildren) AssetDatabase.AddObjectToAsset(child, menuPath);
                EditorUtility.SetDirty(existingMenu);
            }
            else if (existingMenu != null)
            {
                // Reachable by accident: DecompileController emits no `menu:` block, so recompiling a decompiled document
                // into its own folder deletes the menu asset and the GUID a consumer's FullController `menus:` row
                // resolves. The gate cannot catch it: both sides then agree the menu is absent.
                Debug.LogWarning("[CompileController] '" + sourcePath + "' declares no menu: block, deleting the existing '"
                    + menuPath + "'. Any FullController menus: row referencing it will dangle. If this source was "
                    + "produced by DecompileController, the block was dropped by the decompile, not by you.");
                AssetDatabase.DeleteAsset(menuPath);
            }

            var frameLatency = FrameLatencyAdvisories(doc);
            var unresolvedRefs = UnresolvedRefAdvisories(built);
            var oscUnsafeNames = OscUnsafeNameAdvisories(doc);
            var outsideIcons = IconAdvisories(built);

            int states = doc.Layers.Sum(l => l.Root.CountStates());
            string menuPart = !emittedMenu ? "" :
                string.Format(CultureInfo.InvariantCulture, " menu={0}c/{1}p", CountControls(doc.Menu), built.MenuChildren.Count + 1);
            // CLASSIFY (CheckAvatar's verdict for a finding to route), not OK: the null slot ControllerEmit writes for an
            // unresolved ref lints clean, so `=> OK` would certify the drop (docs/animator.md §The compile/decompile substrate).
            string verdict = unresolvedRefs.Count > 0 ? "CLASSIFY" : "OK";
            string unresolvedPart = unresolvedRefs.Count > 0
                ? string.Format(CultureInfo.InvariantCulture, " unresolvedRefs={0}", unresolvedRefs.Count) : "";
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CompileController] {0}: layers={1} states={2} params={3}{4}{5} => {6}{7}",
                name, doc.Layers.Count, states, doc.Parameters.Count, menuPart, unresolvedPart, verdict,
                whatIf ? " (whatIf)" : "");

            string body = BuildBody(doc, finalPath, lint, frameLatency, unresolvedRefs, oscUnsafeNames, outsideIcons,
                built.StampFailure, whatIf);

            if (whatIf) { if (tempFolder != null) AssetDatabase.DeleteAsset(tempFolder); }
            else
            {
                AssetDatabase.SaveAssetIfDirty(built.Controller);
                var paramsAsset = AssetDatabase.LoadMainAssetAtPath(paramsPath);
                if (paramsAsset != null) AssetDatabase.SaveAssetIfDirty(paramsAsset);
                var menuAsset = AssetDatabase.LoadMainAssetAtPath(menuPath);
                if (menuAsset != null) AssetDatabase.SaveAssetIfDirty(menuAsset);
            }

            string res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "compilecontroller_" + name, summary, body, ".md");
            Debug.Log(res);
            return res;
        }

        // Warns before a recompile strips a controller with no provenance or a different srchash; never blocks. The
        // srchash covers the source text, so a hand-edit to the controller under an unchanged source does not warn.
        private static void WarnIfOutOfBand(string finalPath, string sourceText)
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(finalPath);
            if (existing == null) return;
            var importer = AssetImporter.GetAtPath(finalPath);
            string ud = importer != null ? importer.userData : null;
            if (string.IsNullOrEmpty(ud) || ud.IndexOf("compiled-from:", StringComparison.Ordinal) < 0)
            {
                Debug.LogWarning("[CompileController] overwriting '" + finalPath + "': the on-disk controller carries no "
                    + "compile provenance — it was hand-authored or produced by another tool. This recompile strips it "
                    + "in place and replaces it from the YAML source; any manual edits will be lost.");
                return;
            }
            string stored = ExtractField(ud, "srchash:");
            string current = ControllerEmit.SourceHash(sourceText ?? "");
            if (!string.IsNullOrEmpty(stored) && stored != current)
                Debug.LogWarning("[CompileController] overwriting '" + finalPath + "': provenance srchash=" + stored
                    + " differs from the current source (" + current + ") — the on-disk controller was compiled from a "
                    + "different source; this recompile replaces it, along with any hand-edit made to the asset.");
        }

        private static string ExtractField(string userData, string key)
        {
            int i = userData.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int end = userData.IndexOf(';', i);
            return end < 0 ? userData.Substring(i) : userData.Substring(i, end - i);
        }

        // The longest simple chain of firing transitions per layer. A conditional hop resolves a frame later and an
        // exit-time hop (explicit or via the doc default) waits out its source state's clip; a codec walk advances
        // almost entirely through exit-time hops, so both kinds count. Dead transitions and to-Exit are excluded, and
        // only chains of two or more hops surface.
        private static List<string> FrameLatencyAdvisories(AnimDocument doc)
        {
            var lines = new List<string>();
            bool defaultExitTime = doc.Defaults.TransitionHasExitTime;
            foreach (var layer in doc.Layers)
            {
                var (chain, skipReason) = LongestFiringChain(layer, defaultExitTime);
                if (skipReason != null)
                    lines.Add("layer '" + (layer.Name ?? "(unnamed)")
                        + "' frame-latency advisory skipped — " + skipReason);
                else if (chain != null && chain.Count >= 3)
                    lines.Add("layer '" + (layer.Name ?? "(unnamed)") + "' chain '" + string.Join("→", chain)
                        + "' is " + (chain.Count - 1) + " hops of latency (each conditional hop ~1 frame; an "
                        + "exit-time hop costs its state's clip length)");
            }
            return lines;
        }

        private static (List<string> chain, string skipReason) LongestFiringChain(Layer layer, bool defaultExitTime)
        {
            var states = new List<State>();
            layer.Root.CollectStates(states);
            if (states.Count == 0) return (null, null);
            if (states.Count > 128)                            // node-count guard (longest-path is NP-hard)
                return (null, "layer has " + states.Count + " states (>128) — too many to bound cheaply");
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
                    if (!conditional && !exitTimed) continue;
                    if (names.Contains(t.To)) adj[s.Name].Add(t.To);
                }
            }

            // The node guard does not bound edge density, so a step budget caps the DFS and reports over-budget rather
            // than a truncated chain.
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
            return overBudget ? (null, "transition graph too dense to bound cheaply") : (best, null);
        }

        private static List<string> UnresolvedRefAdvisories(ControllerEmit.EmitResult built)
        {
            var lines = new List<string>();
            if (built == null || built.UnresolvedRefs == null) return lines;
            foreach (var (state, guid) in built.UnresolvedRefs)
                lines.Add("state `" + (string.IsNullOrEmpty(state) ? "(unnamed)" : state)
                    + "` has an unresolved motion ref (guid=" + guid
                    + ") — emitted as a null motion; resolve the asset or drop the ref");
            return lines;
        }

        private static List<string> IconAdvisories(ControllerEmit.EmitResult built)
        {
            var lines = new List<string>();
            if (built == null || built.IconsOutsideProject == null) return lines;
            foreach (var (control, path) in built.IconsOutsideProject)
                lines.Add(control + " — icon `" + path + "` could not be resolved: this document is not in "
                    + "the project's AssetDatabase, so the icon was left unset. Normal when compiling from "
                    + "outside the project; the in-project compile that regenerates the built asset is the "
                    + "one that validates it");
            return lines;
        }

        // BuildBody's section line states the hazard. Scratch params are compiler-internal, never on the OSC surface.
        // Not gated on the vrc: meta: a freshly decompiled vendor FX carries none, the case this advisory exists to catch.
        private const string OscPatternMetachars = "#*,?[]{}";
        private static List<string> OscUnsafeNameAdvisories(AnimDocument doc)
        {
            var lines = new List<string>();
            foreach (var p in doc.Parameters)
            {
                if (p == null || p.Scratch) continue;
                string name = p.Name ?? "";
                bool hasSpace = name.IndexOf(' ') >= 0;
                string metas = new string(name.Where(c => OscPatternMetachars.IndexOf(c) >= 0).Distinct().ToArray());
                if (!hasSpace && metas.Length == 0) continue;
                string cls = hasSpace && metas.Length > 0
                    ? "contains a space and OSC pattern metacharacter(s) `" + metas + "`"
                    : hasSpace ? "contains a space"
                    : "contains OSC pattern metacharacter(s) `" + metas + "`";
                // Backtick-quote the name so a leading/trailing space is visible and `# * [ ]` etc. don't
                // render as Markdown.
                lines.Add("`" + name + "` — " + cls);
            }
            return lines;
        }

        private static string BuildBody(AnimDocument doc, string finalPath, LintResult lint,
            List<string> frameLatency, List<string> unresolvedRefs,
            List<string> oscUnsafeNames, List<string> outsideIcons, string stampFailure, bool whatIf)
        {
            var sb = new StringBuilder();
            sb.Append("# CompileController: ").Append(doc.ControllerName).Append('\n');
            sb.Append("source: `").Append(doc.SourcePath ?? "(none)").Append("`  \n");
            sb.Append(whatIf ? "**WHATIF — no asset written**  \n" : "out: `" + finalPath + "`  \n");
            sb.Append("layers=").Append(doc.Layers.Count)
              .Append(" states=").Append(doc.Layers.Sum(l => l.Root.CountStates()))
              .Append(" params=").Append(doc.Parameters.Count).Append("  \n");

            sb.Append("\n## Post-emit graph lint\n\n");
            // Derived, not a literal: the gates in Run and ProofCompile enumerate rule counts by hand, so an error-tier
            // rule they omit would reach this line with Errors non-empty.
            sb.Append("missingMotion=").Append(lint.MissingMotion)
              .Append(" undeclaredParam=").Append(lint.UndeclaredParam)
              .Append(" entryShadow=").Append(lint.EntryShadow)
              .Append(" deadTransition=").Append(lint.DeadTransition)
              .Append(" (errors ").Append(lint.Errors.Count).Append(") — ")
              .Append(lint.Errors.Count == 0 ? "PASS" : "FAIL").Append('\n');
            if (lint.Advisories.Count > 0)
                foreach (var o in lint.Advisories)
                    sb.Append("- advisory: **").Append(o.Kind).Append("** ").Append(o.Where).Append(" — ").Append(o.Detail).Append('\n');

            sb.Append("\n## Compile advisory: frame latency\n\n");
            if (frameLatency.Count == 0) sb.Append("_(none — no chain of two or more hops)_\n");
            else foreach (var l in frameLatency) sb.Append("- ").Append(l).Append('\n');

            sb.Append("\n## Compile advisory: unresolved motion refs\n\n");
            if (unresolvedRefs.Count == 0) sb.Append("_(none)_\n");
            else foreach (var l in unresolvedRefs) sb.Append("- ").Append(l).Append('\n');

            sb.Append("\n## Compile advisory: unadjudicated menu icons\n\n");
            if (outsideIcons.Count == 0) sb.Append("_(none)_\n");
            else foreach (var l in outsideIcons) sb.Append("- ").Append(l).Append('\n');

            if (stampFailure != null)
                sb.Append("\n## Provenance stamp NOT written\n\n- ").Append(stampFailure).Append('\n');

            sb.Append("\n## Compile advisory: OSC-unsafe parameter names\n\n");
            if (oscUnsafeNames.Count == 0) sb.Append("_(none)_\n");
            else
            {
                sb.Append("VRChat's OSC interface replaces a space with `_` (a resulting collision can crash the "
                    + "client), and `# * , ? [ ] { }` are OSC pattern metacharacters — a hazard only if the param "
                    + "is OSC-exposed, which the compiler can't know (hence advisory).\n");
                foreach (var l in oscUnsafeNames) sb.Append("- ").Append(l).Append('\n');
            }

            return sb.ToString();
        }

        private static string NormalizeDir(string dir)
        {
            dir = dir.Replace('\\', '/');
            while (dir.EndsWith("/", StringComparison.Ordinal)) dir = dir.Substring(0, dir.Length - 1);
            return dir;
        }

        private static string ScratchTemp() => "Assets/Agent/Scratch/compile_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        private static int CountControls(List<MenuControl> controls)
        {
            if (controls == null) return 0;
            int n = 0;
            foreach (var c in controls) { n++; if (c != null && c.Controls != null) n += CountControls(c.Controls); }
            return n;
        }

        // Proves a real overwrite will succeed before it strips the prior controller. Returns null on success, or the
        // FAIL reason. Side assets are not persisted, because lint reads the in-memory controller, so the caller only
        // has to sweep `dir`.
        private static string ProofCompile(AnimDocument doc, string dir, string text)
        {
            ControllerEmit.EmitResult built;
            try { ControllerEmit.Build(doc, dir, text, out built); }
            catch (ControllerEmit.EmitException ee) { return "emit: " + ee.Message; }
            catch (Exception e) { return "emit: " + e.GetType().Name + ": " + e.Message; }

            try
            {
                var lint = ControllerRules.Run(built.Controller, new List<GameObject>(), brokenBindingIsError: false, pathRewrite: null);
                if (lint.MissingMotion > 0 || lint.UndeclaredParam > 0 || lint.NonFloatBlendParam > 0 || lint.NonFloatParamCurve > 0 || lint.DriverOnAnimatedParam > 0 || lint.EntryShadow > 0 || lint.DeadTransition > 0)
                    return "post-emit graph lint (" + lint.Errors.Count + "): "
                         + string.Join("  ", lint.Errors.Select(o => o.Kind + " @ " + o.Where + ": " + o.Detail));
                return null;
            }
            finally
            {
                // Unguarded on every exit, success included: nothing here persists a side asset. See EmitResult.DestroyUnpersisted.
                built?.DestroyUnpersisted();
            }
        }

        // An overwrite never reaches here mid-strip: ProofCompile clears the emit in a temp first, so a pre-existing
        // controller is left intact.
        private static void CleanupAfterEmit(bool whatIf, string tempFolder, string finalPath, bool controllerPreExisted,
            List<string> newFolders)
        {
            if (whatIf) { if (tempFolder != null && AssetDatabase.IsValidFolder(tempFolder)) AssetDatabase.DeleteAsset(tempFolder); return; }
            if (!controllerPreExisted) { AssetDatabase.DeleteAsset(finalPath); DeleteEmptyNewFolders(newFolders); }
        }

        // Lint runs before any side asset is written, so only the controller and the folders that carried it roll
        // back. ProofCompile makes this unreachable for an overwrite; the guards keep the atomicity local.
        private static void CleanupAfterLint(bool whatIf, string tempFolder, string finalPath,
            bool controllerPreExisted, List<string> newFolders)
        {
            if (whatIf) { if (tempFolder != null && AssetDatabase.IsValidFolder(tempFolder)) AssetDatabase.DeleteAsset(tempFolder); return; }
            if (!controllerPreExisted) AssetDatabase.DeleteAsset(finalPath);
            if (!controllerPreExisted) DeleteEmptyNewFolders(newFolders);
        }

        // Deepest last, so rollback deletes in reverse.
        private static List<string> NewFolders(string dir)
        {
            var missing = new List<string>();
            var parts = dir.Split('/');
            string cur = parts.Length > 0 ? parts[0] : dir; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                cur = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(cur)) missing.Add(cur);
            }
            return missing;
        }

        private static void DeleteEmptyNewFolders(List<string> newFolders)
        {
            if (newFolders == null) return;
            for (int i = newFolders.Count - 1; i >= 0; i--)
            {
                string f = newFolders[i];
                if (!AssetDatabase.IsValidFolder(f)) continue;
                string abs = Path.GetFullPath(f);
                if (Directory.Exists(abs) && Directory.GetFileSystemEntries(abs).Length == 0)
                    AssetDatabase.DeleteAsset(f);
            }
        }

        /// <summary>Refusal tail: a named one-line verdict ending <c>=&gt; FAIL | log=</c> plus a minimal RunLog artifact;
        /// nothing reaches <c>outDir</c>. <paramref name="label"/> is the source file's leaf because pre-parse stages
        /// don't know the controller name.</summary>
        private static string Fail(string label, string sourcePath, string why)
        {
            // Exception messages and asset names can carry raw newlines; the artifact body keeps `why` raw.
            string oneLineWhy = why.Replace("\r", " ").Replace("\n", " ");
            string summary = "[CompileController] " + label + ": " + oneLineWhy + " => FAIL";
            string body = "# CompileController FAIL\n\n- source: " + (string.IsNullOrEmpty(sourcePath) ? "(null)" : sourcePath) + "\n- reason: " + why + "\n";
            string res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "compilecontroller_" + label, summary, body, ".md");
            Debug.LogError(res);
            return res;
        }

        /// <summary>Non-throwing leaf of a filesystem path — <c>Path.GetFileName</c> throws
        /// <c>ArgumentException</c> on invalid-char paths (Mono/2022.3), which would crash the door
        /// before its guards can refuse; splits on both separators (filesystem paths carry
        /// backslashes, unlike <see cref="RunLogFormat.Leaf"/>'s asset paths).</summary>
        private static string SafeLeaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "unknown";
            int i = path.LastIndexOfAny(new[] { '/', '\\' });
            string leaf = i >= 0 ? path.Substring(i + 1) : path;
            return leaf.Length == 0 ? "unknown" : leaf;
        }
    }

    /// <summary>Menu door for <see cref="CompileController"/>: picks a <c>.yaml</c> and an output folder, then delegates.</summary>
    internal static class CompileControllerMenu
    {
        [MenuItem("Tools/Atelier/Animator/Compile Controller…")]
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
            string outDir = AssetPathUtil.ToProjectRelative(abs);
            if (outDir == null) { Debug.LogError("[CompileController] output folder must be under this project's Assets/."); return; }

            CompileController.Run(src, outDir, false);
        }
    }
}
