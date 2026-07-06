using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{

    [AgentTool]
    public static class SplitHSVGClips
    {
        private const string TargetPropertyPrefix = "material._MainTexHSVG";

        private enum Channel
        {
            H,
            S,
            V,
            G
        }

        private enum Variant
        {
            Min,
            Max,
            Default
        }

        // ----- UI door: thin shim over the core (reads selection, calls Run, logs) ---------
        [MenuItem("Tools/Ryan6VRC/Create Split HSVG Clips From Selected")]
        private static void CreateSplitClipsFromSelected()
        {
            var paths = Selection.objects
                .OfType<AnimationClip>()
                .Select(AssetDatabase.GetAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
            if (paths.Length == 0) { Debug.LogWarning("Select one or more AnimationClip assets first."); return; }
            Debug.Log(Run(paths));
        }

        /// <summary>Split each source HSVG clip into its per-channel Min/Default/Max variants, written
        /// beside the source (deterministic path, GUID-stable in-place update). Returns the created
        /// variant asset paths. Speaks asset paths.</summary>
        public static string Run(params string[] clipAssetPaths)
        {
            if (clipAssetPaths == null || clipAssetPaths.Length == 0)
                return "[SplitHSVGClips] FAIL: no AnimationClip assets given";

            int sources = 0;
            var created = new System.Collections.Generic.List<string>();
            var orphans = new System.Collections.Generic.List<string>();

            foreach (var path in clipAssetPaths)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) { Debug.LogWarning($"[SplitHSVGClips] Not an AnimationClip, skipping: {path}"); continue; }
                string dir = Path.GetDirectoryName(path).Replace('\\', '/');
                string baseName = Path.GetFileNameWithoutExtension(path);
                var blocked = FindNonClipConflicts(dir, baseName);
                if (blocked.Count > 0)
                    return $"[SplitHSVGClips] FAIL: non-AnimationClip asset(s) occupy target path(s): {string.Join(", ", blocked)}";
                created.AddRange(CreateSplitClips(clip, dir, baseName));
                orphans.AddRange(FindStaleOrphans(dir, baseName));
                sources++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (sources == 0)
                return $"[SplitHSVGClips] FAIL: no valid AnimationClip among {clipAssetPaths.Length} path(s)";

            string orphanNote = orphans.Count > 0
                ? $"; stale _N orphans (retire manually): {string.Join(", ", orphans)}" : "";
            return $"[SplitHSVGClips] split {sources} source(s) into {created.Count} variant(s): " +
                   $"{string.Join(", ", created)}{orphanNote} => PASS";
        }

        // Single source of truth for the 11-variant set — both the writer (CreateSplitClips) and the
        // conflict guard (FindNonClipConflicts) iterate this, so the two can't drift. (H is Min/Max only.)
        private static readonly (Channel channel, Variant variant, float value)[] Variants = {
            (Channel.H, Variant.Min, 0f), (Channel.H, Variant.Max, 1f),
            (Channel.S, Variant.Min, 0f), (Channel.S, Variant.Default, 1f), (Channel.S, Variant.Max, 2f),
            (Channel.V, Variant.Min, 0f), (Channel.V, Variant.Default, 1f), (Channel.V, Variant.Max, 2f),
            (Channel.G, Variant.Min, 0f), (Channel.G, Variant.Default, 1f), (Channel.G, Variant.Max, 2f),
        };

        private static System.Collections.Generic.List<string> CreateSplitClips(
            AnimationClip sourceClip, string directory, string baseName)
        {
            var paths = new System.Collections.Generic.List<string>();
            foreach (var v in Variants)
                paths.Add(CreateSplitClip(sourceClip, directory, baseName, v.channel, v.variant, v.value));
            return paths;
        }

        private static string CreateSplitClip(
            AnimationClip sourceClip, string directory, string baseName,
            Channel channel, Variant variant, float value)
        {
            string componentSuffix = GetComponentSuffix(channel);
            string outputName = $"{baseName}_{channel}_{variant}";
            string outputPath = (directory + "/" + outputName + ".anim");

            var built = new AnimationClip { name = outputName, frameRate = sourceClip.frameRate };
            EditorUtility.CopySerialized(sourceClip, built);
            built.name = outputName;
            foreach (var b in AnimationUtility.GetCurveBindings(built))
                AnimationUtility.SetEditorCurve(built, b, null);
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(built))
                AnimationUtility.SetObjectReferenceCurve(built, b, null);
            foreach (var binding in AnimationUtility.GetCurveBindings(sourceClip))
            {
                if (!IsHSVGComponentBinding(binding.propertyName)) continue;
                if (!binding.propertyName.EndsWith(componentSuffix)) continue;
                var nb = binding;
                nb.propertyName = TargetPropertyPrefix + componentSuffix;
                AnimationUtility.SetEditorCurve(built, nb, BuildConstantCurve(sourceClip, value));
            }

            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(outputPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(built, existing);
                existing.name = outputName;
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(built);
            }
            else
            {
                AssetDatabase.CreateAsset(built, outputPath);
            }
            return outputPath;
        }

        private static System.Collections.Generic.List<string> FindNonClipConflicts(string directory, string baseName)
        {
            var blocked = new System.Collections.Generic.List<string>();
            foreach (var v in Variants)
            {
                string path = directory + "/" + baseName + "_" + v.channel + "_" + v.variant + ".anim";
                var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (obj != null && !(obj is AnimationClip)) blocked.Add(path);
            }
            return blocked;
        }

        private static System.Collections.Generic.List<string> FindStaleOrphans(string directory, string baseName)
        {
            var found = new System.Collections.Generic.List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { directory }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                string n = Path.GetFileNameWithoutExtension(p);
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        n, "^" + System.Text.RegularExpressions.Regex.Escape(baseName) +
                        "_(H|S|V|G)_(Min|Max|Default) \\d+$"))
                    found.Add(p);
            }
            return found;
        }

        private static bool IsHSVGComponentBinding(string propertyName)
        {
            return propertyName == TargetPropertyPrefix + ".x" ||
                   propertyName == TargetPropertyPrefix + ".y" ||
                   propertyName == TargetPropertyPrefix + ".z" ||
                   propertyName == TargetPropertyPrefix + ".w";
        }

        private static string GetComponentSuffix(Channel channel)
        {
            switch (channel)
            {
                case Channel.H:
                    return ".x";
                case Channel.S:
                    return ".y";
                case Channel.V:
                    return ".z";
                case Channel.G:
                    return ".w";
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(channel), channel, null);
            }
        }

        private static AnimationCurve BuildConstantCurve(AnimationClip sourceClip, float value)
        {
            var curve = new AnimationCurve();
            curve.AddKey(new Keyframe(0f, value));
            return curve;
        }
    }
}
