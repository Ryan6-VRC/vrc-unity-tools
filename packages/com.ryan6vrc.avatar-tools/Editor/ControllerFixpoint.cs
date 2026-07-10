using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    // Gate infrastructure for a directory of vrc-patterns entries — NOT an [AgentTool] and NOT a
    // [MenuItem] (no callable door, so no TOOLS.md row). It reuses the shipped compile/decompile
    // primitives: decode(c) = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(c).Doc), the same
    // canonical string the fixpoint tests trust. Drift is decompile-equality, never a byte diff.
    //
    // Decoding a controller that lives under Packages/ throws (dangling-guid recovery reads the asset
    // by path); so a committed built controller is copied into Assets/ before it is decoded.
    public static class ControllerFixpoint
    {
        static string Decode(AnimatorController c, out string refusal)
        {
            var w = ControllerDecompile.Walk(c);
            if (w.Refusals != null && w.Refusals.Count != 0) { refusal = string.Join("; ", w.Refusals); return null; }
            refusal = null;
            return AnimatorSchemaEmit.Serialize(w.Doc);
        }

        static string ToAssetsRelative(string abs)
        {
            var proj = Directory.GetCurrentDirectory().Replace('\\', '/');
            abs = Path.GetFullPath(abs).Replace('\\', '/');
            return abs.StartsWith(proj + "/", StringComparison.Ordinal) ? abs.Substring(proj.Length + 1) : abs;
        }

        // Compile a yaml at a filesystem path into a temp Assets/ folder and load the emitted controller.
        static AnimatorController CompileToTemp(string yamlPath, string tempAssetsDir)
        {
            Directory.CreateDirectory(Path.GetFullPath(tempAssetsDir));
            var msg = CompileController.Compile(yamlPath, ToAssetsRelative(tempAssetsDir));
            if (msg == null || msg.IndexOf("=> OK", StringComparison.Ordinal) < 0)
                throw new Exception("compile failed: " + msg);
            AssetDatabase.Refresh();
            var ctrl = Directory.GetFiles(Path.GetFullPath(tempAssetsDir), "*.controller").FirstOrDefault();
            if (ctrl == null) throw new Exception("no .controller emitted into " + tempAssetsDir);
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(ToAssetsRelative(ctrl));
        }

        // Copy a committed built/ folder (its .controller + .meta and any siblings) into Assets/ so the
        // AssetDatabase imports it with the committed GUID, then load the controller.
        static AnimatorController ImportCommitted(string builtControllerPath, string destAssetsDir)
        {
            var full = Path.GetFullPath(destAssetsDir);
            Directory.CreateDirectory(full);
            var srcDir = Path.GetDirectoryName(Path.GetFullPath(builtControllerPath));
            foreach (var f in Directory.GetFiles(srcDir))
                File.Copy(f, Path.Combine(full, Path.GetFileName(f)), true);
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(
                ToAssetsRelative(Path.Combine(full, Path.GetFileName(builtControllerPath))));
        }

        // (ok, message). yamlPath: filesystem path to controller.yaml. builtControllerPath: filesystem
        // path to a committed built .controller (asset-bound/module tiers), or null for a Pattern entry.
        public static (bool ok, string msg) Check(string yamlPath, string builtControllerPath)
        {
            var scratch = "Assets/_fixpoint_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                var cFresh = CompileToTemp(yamlPath, scratch + "/a");
                var yFresh = Decode(cFresh, out var r1);
                if (yFresh == null) return (false, "fresh decompile refused: " + r1);

                var midYaml = Path.GetFullPath(scratch + "/mid.yaml");
                File.WriteAllText(midYaml, yFresh);
                var cRound = CompileToTemp(midYaml, scratch + "/b");
                var yRound = Decode(cRound, out var r2);
                if (yRound == null) return (false, "round-trip decompile refused: " + r2);
                if (yFresh != yRound) return (false, "round-trip drift (yaml not on the fixpoint)");

                if (builtControllerPath != null)
                {
                    var cCommitted = ImportCommitted(builtControllerPath, scratch + "/committed");
                    if (cCommitted == null) return (false, "committed controller failed to import");
                    var yCommitted = Decode(cCommitted, out var r3);
                    if (yCommitted == null) return (false, "committed decompile refused: " + r3);
                    if (yCommitted != yFresh) return (false, "committed built/ differs from compile(yaml) — regenerate built/");
                }
                return (true, "OK");
            }
            catch (Exception e) { return (false, e.Message); }
            finally { AssetDatabase.DeleteAsset(scratch); }
        }

        // -executeMethod entrypoint. Args after `--`: --root <dir>. Globs <dir>/*/controller.yaml
        // (skipping dot-folders), Checks each, exits 0 iff all pass.
        public static void RunGate()
        {
            string root = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--root") root = args[i + 1];
            if (root == null) { Debug.LogError("[gate] --root <dir> required"); EditorApplication.Exit(2); return; }
            if (!Directory.Exists(root)) { Debug.LogError("[gate] root not found: " + root); EditorApplication.Exit(2); return; }

            var entries = Directory.GetDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .Where(d => File.Exists(Path.Combine(d, "controller.yaml")))
                .OrderBy(d => d, StringComparer.Ordinal).ToList();

            int failed = 0;
            foreach (var dir in entries)
            {
                var yaml = Path.Combine(dir, "controller.yaml");
                var builtDir = Path.Combine(dir, "built");
                var built = Directory.Exists(builtDir)
                    ? Directory.GetFiles(builtDir, "*.controller").FirstOrDefault() : null;
                var (ok, msg) = Check(yaml, built);
                Debug.Log($"[gate] {(ok ? "PASS" : "FAIL")} {Path.GetFileName(dir)}: {msg}");
                if (!ok) failed++;
            }
            Debug.Log($"[gate] {entries.Count - failed}/{entries.Count} passed");
            EditorApplication.Exit(failed == 0 ? 0 : 1);
        }
    }
}
