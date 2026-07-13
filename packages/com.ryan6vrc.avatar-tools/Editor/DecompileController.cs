using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The animator-READ-substrate DOOR, mirror of <see cref="CompileController"/>: turn a real
    /// <see cref="AnimatorController"/> back into animator-schema YAML. Ties the verified read pipeline
    /// together — load → reachability walk (<see cref="ControllerDecompile"/>) → serialize
    /// (<see cref="AnimatorSchemaEmit"/>) → Snapshot RunLog. Pure PASS/FAIL contract mirroring the compile
    /// door: a clean run returns the one-line summary with the RunLog path in-band
    /// (<c>… =&gt; OK | log=&lt;path&gt;</c>) and writes the <c>.yaml</c> at <paramref name="outPath"/>; a
    /// <paramref name="whatIf"/> preview writes NO <c>.yaml</c> and returns <c>… =&gt; OK (whatIf) | log=…</c>
    /// (still recording the RunLog, like the compile door's whatIf); any refusal is
    /// <c>[DecompileController] &lt;controller-leaf&gt;: &lt;named + located constructs&gt; =&gt; FAIL | log=&lt;path&gt;</c>
    /// — a Snapshot artifact records the failure, and NO <c>.yaml</c> is written.
    ///
    /// <para>A refusal is the walk surfacing an out-of-vocabulary or malformed construct
    /// (<see cref="ControllerDecompile.WalkResult.Refusals"/>) — refuse loudly rather than emit a lossy
    /// approximation. The walk's incidental data (orphan count, unresolved GUIDs, applied import tolerances)
    /// is folded into the document's reserved <c>_notes</c> block so the emitted <c>.yaml</c> carries it
    /// verbatim; that block is compile-ignored on re-parse, so the yaml round-trips through
    /// <see cref="CompileController"/> unchanged.</para>
    ///
    /// <para>This is a READ tool (it never mutates the controller), so it self-logs to
    /// <see cref="RunLogFormat.SnapshotDir"/> — the read-capture channel — not the verdict RunLog dir.</para>
    /// </summary>
    [AgentTool]
    public static class DecompileController
    {
        /// <summary>Decompile the controller at <paramref name="controllerPath"/> (an <c>Assets/…</c>-relative
        /// asset path) to animator-schema YAML at <paramref name="outPath"/> (a filesystem path). With
        /// <paramref name="whatIf"/> the whole walk + serialize runs but no <c>.yaml</c> is written. With
        /// <paramref name="stripLayout"/> (opt-in, default off) the walk captures NO graph-layout blocks —
        /// the own-a-vendor-controller path, where the vendor's node arrangement is noise. Returns the
        /// one-line summary (see class docs).</summary>
        public static string Decompile(string controllerPath, string outPath, bool whatIf = false, bool stripLayout = false)
        {
            // One refusal label per run: the controller asset's leaf (the thing being decompiled).
            // Path.GetFileName throws on invalid-char paths (Mono/2022.3) — the door must refuse,
            // not crash, so use the non-throwing asset-path leaf.
            string failLabel = RunLogFormat.Leaf(controllerPath);
            if (failLabel.Length == 0) failLabel = "unknown";

            // ── Arg guards (mirror CompileController) ─────────────────────────────────────────────────
            if (string.IsNullOrEmpty(controllerPath)) return Fail(failLabel, controllerPath, "controllerPath is empty");
            if (string.IsNullOrEmpty(outPath)) return Fail(failLabel, controllerPath, "outPath is empty");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null) return Fail(failLabel, controllerPath, "controller not found at: " + controllerPath);

            // ── Reachability walk ─────────────────────────────────────────────────────────────────────
            ControllerDecompile.WalkResult walk;
            try { walk = ControllerDecompile.Walk(controller, stripLayout); }
            catch (Exception e) { return Fail(failLabel, controllerPath, "walk: " + e.GetType().Name + ": " + e.Message); }

            // A refusal is fail-loud: name every out-of-vocabulary construct, write no .yaml.
            if (walk.Refusals.Count > 0)
                return Fail(failLabel, controllerPath, string.Join("  ", walk.Refusals));

            var doc = walk.Doc;

            // ── Fold the walk's incidental data into the reserved _notes block ─────────────────────────
            // AnimatorSchemaEmit renders ReservedNotes under a top-level `_notes:` block; the parser ignores
            // `_`-prefixed top-level keys, so this is inert on re-compile.
            doc.ReservedNotes["orphans"] = walk.OrphanCount;
            doc.ReservedNotes["unresolved"] = walk.UnresolvedGuids.Select(g => (object)g).ToList();
            doc.ReservedNotes["tolerances"] = walk.Notes.Select(n => (object)n).ToList();

            // The serializer's funnel guard throws on a string no scalar can carry (a literal line break in
            // any name/path/field) — surface it as this door's named FAIL, never a torn .yaml.
            string yaml;
            try { yaml = AnimatorSchemaEmit.Serialize(doc); }
            catch (SchemaException e) { return Fail(failLabel, controllerPath, "serialize: " + e.Message); }

            // ── Persist the .yaml (skipped in whatIf; that is the only thing whatIf suppresses) ───────
            if (!whatIf)
            {
                try
                {
                    string dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outPath, yaml);
                    AssetDatabase.Refresh();
                }
                catch (Exception e) { return Fail(failLabel, controllerPath, "could not write '" + outPath + "': " + e.Message); }
            }

            // ── Summary + body → Snapshot RunLog (written in whatIf too, mirroring CompileController) ──
            string name = doc.ControllerName;
            int states = doc.Layers.Sum(l => l.Root.CountStates());
            string flags = (whatIf ? " (whatIf)" : "") + (stripLayout ? " (layout stripped)" : "");
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[DecompileController] {0}: layers={1} states={2} orphans={3} unresolved={4} => OK{5}",
                name, doc.Layers.Count, states, walk.OrphanCount, walk.UnresolvedGuids.Count,
                flags);

            string body = BuildBody(doc, controllerPath, outPath, walk, whatIf);

            string res = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "decompilecontroller_" + name, summary, body, ".md");
            Debug.Log(res);
            return res;
        }

        // ── RunLog body ────────────────────────────────────────────────────────────────────────────────
        private static string BuildBody(AnimDocument doc, string controllerPath, string outPath,
            ControllerDecompile.WalkResult walk, bool whatIf)
        {
            var sb = new StringBuilder();
            sb.Append("# DecompileController: ").Append(doc.ControllerName).Append('\n');
            sb.Append("controller: `").Append(controllerPath).Append("`  \n");
            sb.Append(whatIf ? "**WHATIF — no .yaml written**  \n" : "out: `" + outPath + "`  \n");
            sb.Append("layers=").Append(doc.Layers.Count)
              .Append(" states=").Append(doc.Layers.Sum(l => l.Root.CountStates()))
              .Append(" orphans=").Append(walk.OrphanCount)
              .Append(" unresolved=").Append(walk.UnresolvedGuids.Count).Append("  \n");

            sb.Append("\n## Unresolved motion GUIDs\n\n");
            if (walk.UnresolvedGuids.Count == 0) sb.Append("_(none)_\n");
            else foreach (var g in walk.UnresolvedGuids) sb.Append("- `").Append(g).Append("`\n");

            sb.Append("\n## Import tolerances applied\n\n");
            if (walk.Notes.Count == 0) sb.Append("_(none)_\n");
            else foreach (var n in walk.Notes) sb.Append("- ").Append(n).Append('\n');

            return sb.ToString();
        }

        /// <summary>Refusal tail, mirror of <see cref="CompileController"/>'s: the house grammar —
        /// a named one-line verdict ending <c>=&gt; FAIL | log=</c> plus a minimal artifact on this
        /// tool's channel (<see cref="RunLogFormat.SnapshotDir"/>, where its success log goes) —
        /// replacing the old bare trailer-less line. NOTHING is written at <c>outPath</c> —
        /// unchanged; the artifact is the verdict record.</summary>
        private static string Fail(string label, string controllerPath, string why)
        {
            // Keep the one-line verdict one line (refusal strings can embed raw asset names, which
            // can carry newlines); the artifact body keeps `why` raw.
            string oneLineWhy = why.Replace("\r", " ").Replace("\n", " ");
            string summary = "[DecompileController] " + label + ": " + oneLineWhy + " => FAIL";
            string body = "# DecompileController FAIL\n\n- controller: " + (string.IsNullOrEmpty(controllerPath) ? "(null)" : controllerPath) + "\n- reason: " + why + "\n";
            string res = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "decompilecontroller_" + label, summary, body, ".md");
            Debug.LogError(res);
            return res;
        }
    }

    /// <summary>Menu door for <see cref="DecompileController"/> — resolves a selected/prompted
    /// <c>.controller</c> and an output <c>.yaml</c> path, then delegates. ZERO decompile logic lives here
    /// (Decompile logs its own result).</summary>
    internal static class DecompileControllerMenu
    {
        [MenuItem("Tools/Agent/Animator/Decompile Controller…")]
        private static void Door()
        {
            string ctrlPath = null;
            var sel = Selection.activeObject;
            if (sel != null)
            {
                string p = AssetDatabase.GetAssetPath(sel);
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
                    ctrlPath = p;
            }
            if (ctrlPath == null)
            {
                string abs = EditorUtility.OpenFilePanel("Select an AnimatorController", Application.dataPath, "controller");
                if (string.IsNullOrEmpty(abs)) return;
                ctrlPath = AssetPathUtil.ToProjectRelative(abs);
                if (ctrlPath == null) { Debug.LogError("[DecompileController] the controller must be under this project's Assets/."); return; }
            }

            string name = Path.GetFileNameWithoutExtension(ctrlPath);
            string outPath = EditorUtility.SaveFilePanelInProject("Save decompiled YAML", name, "yaml", "");
            if (string.IsNullOrEmpty(outPath)) return;

            DecompileController.Decompile(ctrlPath, outPath, false);
        }
    }
}
