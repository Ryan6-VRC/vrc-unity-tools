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
    /// <summary>Turns a real <see cref="AnimatorController"/> back into animator-schema YAML, the read-side
    /// mirror of <see cref="CompileController"/>.
    /// Contract: docs/animator.md.</summary>
    [AgentTool]
    public static class DecompileController
    {
        /// <summary><paramref name="controllerPath"/> is an <c>Assets/…</c> asset path and
        /// <paramref name="outPath"/> a filesystem path. Returns the one-line verdict with its log path.</summary>
        public static string Run(string controllerPath, string outPath, bool whatIf = false, bool stripLayout = false)
        {
            // Path.GetFileName throws on invalid-char paths (Mono/2022.3), and the door must refuse, not crash,
            // so the label uses the non-throwing asset-path leaf.
            string failLabel = RunLogFormat.Leaf(controllerPath);
            if (failLabel.Length == 0) failLabel = "unknown";

            // ── Arg guards ────────────────────────────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(controllerPath)) return Fail(failLabel, controllerPath, "controllerPath is empty");
            if (string.IsNullOrEmpty(outPath)) return Fail(failLabel, controllerPath, "outPath is empty");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            // An override controller is a present asset, and "controller not found" would send the caller
            // hunting a missing file.
            if (controller == null) return Fail(failLabel, controllerPath, ReportController.RefuseWhy(controllerPath));

            // ── Reachability walk ─────────────────────────────────────────────────────────────────────
            ControllerDecompile.WalkResult walk;
            try { walk = ControllerDecompile.Walk(controller, stripLayout); }
            catch (Exception e) { return Fail(failLabel, controllerPath, "walk: " + e.GetType().Name + ": " + e.Message); }

            // The refusal covers the whole document: emitting only the clean layers would produce a valid,
            // recompilable YAML that silently drops a layer.
            if (walk.Refusals.Count > 0)
                return Fail(failLabel, controllerPath, RefusalScope(walk) + JoinRefusals(walk.Refusals) + RefusalRoute(walk));

            var doc = walk.Doc;

            // ── Fold the walk's incidental data into the reserved _notes block ─────────────────────────
            doc.ReservedNotes["orphans"] = walk.OrphanCount;
            doc.ReservedNotes["unresolved"] = walk.UnresolvedGuids.Select(g => (object)g).ToList();
            doc.ReservedNotes["tolerances"] = walk.Notes.Select(n => (object)n).ToList();

            // AnimatorSchemaEmit.Serialize throws on a literal line break in any string field; surface that as
            // this door's named FAIL, never a torn .yaml.
            string yaml;
            try { yaml = AnimatorSchemaEmit.Serialize(doc); }
            catch (SchemaException e) { return Fail(failLabel, controllerPath, "serialize: " + e.Message); }

            // ── Persist the .yaml ─────────────────────────────────────────────────────────────────────
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

            // ── Summary + body → Snapshot RunLog ───────────────────────────────────────────────────────
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

        /// <summary>How many layers the refusal implicates. Not a clean/dirty split, since nothing decompiled.
        /// "Refused" means <i>carries a refusal</i>, not <i>absent from the document</i>: a synced layer refuses
        /// and is skipped, an iKPass layer refuses and is still decoded.</summary>
        private static string RefusalScope(ControllerDecompile.WalkResult walk)
        {
            var layers = new System.Collections.Generic.HashSet<int>();
            int document = 0;
            foreach (var idx in walk.RefusalLayers) { if (idx < 0) document++; else layers.Add(idx); }
            string s = "refusedLayers=" + layers.Count + "/" + walk.LayerCount;
            if (document > 0) s += " documentScope=" + document;
            return s + " — ";
        }

        /// <summary>The next move, keyed on the scope <see cref="RefusalScope"/> reports.</summary>
        private static string RefusalRoute(ControllerDecompile.WalkResult walk)
        {
            bool anyLayer = walk.RefusalLayers.Any(i => i >= 0);
            string route = "  [route: to READ this controller use ReportController — decompile is for OWNING it.";
            route += anyLayer
                ? " To own the rest, drop the named layer(s) first (CleanController trims by layer NAME without "
                + "parsing contents), then re-decompile"
                : " The refusal is not a layer's, so trimming layers will not clear it — repair the named "
                + "construct on the controller itself, then re-decompile";
            return route + "; `animator.md` owns the round-trip.]";
        }

        /// <summary>Joins refusals into one verdict, collapsing repeats to <c>… (×N)</c> in first-seen order.</summary>
        private static string JoinRefusals(System.Collections.Generic.List<string> refusals)
        {
            var order = new System.Collections.Generic.List<string>();
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var r in refusals)
            {
                if (!counts.ContainsKey(r)) { counts[r] = 0; order.Add(r); }
                counts[r]++;
            }
            return string.Join("  ", order.Select(r => counts[r] > 1 ? r + " (×" + counts[r] + ")" : r));
        }

        /// <summary>Refusal tail: the named FAIL verdict plus a minimal artifact in
        /// <see cref="RunLogFormat.SnapshotDir"/>; nothing is written at <c>outPath</c>.</summary>
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

    /// <summary>Menu door for <see cref="DecompileController"/>.</summary>
    internal static class DecompileControllerMenu
    {
        [MenuItem("Tools/Atelier/Animator/Decompile Controller…")]
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

            DecompileController.Run(ctrlPath, outPath, false);
        }
    }
}
