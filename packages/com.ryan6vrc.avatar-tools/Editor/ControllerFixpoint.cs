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
    // A committed built .controller lives at an arbitrary --root filesystem path, not under the
    // project, so it is copied into Assets/ (with its committed GUID) to be imported and loaded.
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
            var loaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(ToAssetsRelative(ctrl));
            if (loaded == null) throw new Exception("emitted .controller failed to load: " + ctrl);
            return loaded;
        }

        // Copy ONE committed built controller (+ its .meta) into Assets/ so the AssetDatabase imports
        // it with the committed GUID, then load it. Only the named controller is copied — a multi-
        // controller entry (an FX + Gesture pair) would otherwise import every sibling's committed
        // GUID once per checked document, colliding across scratch dirs. Assumes the package under
        // test is NOT also loaded in this host — else the committed GUID exists twice (a collision).
        static AnimatorController ImportCommitted(string builtControllerPath, string destAssetsDir)
        {
            var full = Path.GetFullPath(destAssetsDir);
            Directory.CreateDirectory(full);
            var src = Path.GetFullPath(builtControllerPath);
            File.Copy(src, Path.Combine(full, Path.GetFileName(src)), true);
            if (File.Exists(src + ".meta"))
                File.Copy(src + ".meta", Path.Combine(full, Path.GetFileName(src) + ".meta"), true);
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

        // Reads the `controller:` name off a schema document without compiling it. Null when the file
        // carries no such key (e.g. a CompileClips document) — the caller decides what that means.
        static string ParseControllerName(string yamlPath)
        {
            foreach (var line in File.ReadLines(yamlPath))
            {
                if (!line.StartsWith("controller:", StringComparison.Ordinal)) continue;
                var v = line.Substring("controller:".Length);
                int hash = v.IndexOf('#');
                if (hash >= 0) v = v.Substring(0, hash);
                v = v.Trim();
                return v.Length == 0 ? null : v;
            }
            return null;
        }

        // -executeMethod entrypoint. Args after `--`: --root <dir>. An entry is a non-dot <dir>/* folder
        // containing controller.yaml; EVERY top-level *.yaml in it with a `controller:` key is gated
        // (a multi-controller entry ships an FX + Gesture pair), each against built/<name>.controller.
        // A built controller no document claims is drift and fails the entry. Exits 0 iff all pass.
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

            int failedEntries = 0, checkedDocs = 0;
            foreach (var dir in entries)
            {
                var entry = Path.GetFileName(dir);
                bool entryFailed = false;
                var builtDir = Path.Combine(dir, "built");

                // Tier is derived from files present; a GUID-consumer shape (a prefab, a non-empty assets/,
                // or a built/ dir) MUST ship a built .controller per document. Without this, a Module/
                // Asset-bound entry whose built controller went missing would silently pass as a Pattern.
                var assetsDir = Path.Combine(dir, "assets");
                bool guidConsumer = Directory.GetFiles(dir, "*.prefab").Length > 0
                    || Directory.Exists(builtDir)
                    || (Directory.Exists(assetsDir) && Directory.GetFiles(assetsDir)
                            .Any(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)));

                var claimed = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var yaml in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var doc = $"{entry}/{Path.GetFileName(yaml)}";
                    var name = ParseControllerName(yaml);
                    if (name == null)
                    {
                        // Not a controller document (a clips file has its own compile path) — named, not silent.
                        Debug.Log($"[gate] SKIP {doc}: no controller: key (not a controller document)");
                        continue;
                    }
                    claimed.Add(name);
                    var built = Path.Combine(builtDir, name + ".controller");
                    var builtExists = File.Exists(built);
                    if (!builtExists && guidConsumer)
                    {
                        Debug.Log($"[gate] FAIL {doc}: GUID-consumer entry (prefab/assets/built) has no built/{name}.controller");
                        entryFailed = true; continue;
                    }

                    checkedDocs++;
                    var (ok, msg) = Check(yaml, builtExists ? built : null);
                    Debug.Log($"[gate] {(ok ? "PASS" : "FAIL")} {doc}: {msg}");
                    if (!ok) entryFailed = true;
                }

                // A committed controller no document claims is drift (a renamed/deleted yaml left its
                // built form behind) — the silent-skip this multi-yaml gate exists to prevent.
                if (Directory.Exists(builtDir))
                    foreach (var orphan in Directory.GetFiles(builtDir, "*.controller")
                        .Select(Path.GetFileNameWithoutExtension).Where(n => !claimed.Contains(n)))
                    {
                        Debug.Log($"[gate] FAIL {entry}: built/{orphan}.controller matches no yaml document (drift)");
                        entryFailed = true;
                    }

                if (entryFailed) failedEntries++;
            }
            Debug.Log($"[gate] {entries.Count - failedEntries}/{entries.Count} entries passed ({checkedDocs} documents)");
            EditorApplication.Exit(failedEntries == 0 ? 0 : 1);
        }
    }
}
