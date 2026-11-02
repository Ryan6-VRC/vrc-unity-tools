using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Markdown digest of an <see cref="AnimationClip"/>'s authored bindings, one row per float or
    /// objectReference curve.
    /// Contract: docs/animator.md.</summary>
    [AgentTool]
    public static class ReportClip
    {
        public static string Run(string clipPathOrGuid)
        {
            var clip = RunLogFormat.LoadByPathOrGuid<AnimationClip>(clipPathOrGuid);
            if (clip == null)
            {
                var err = "[ReportClip] FAIL: no AnimationClip at '" + clipPathOrGuid + "' — expects an asset path or GUID (for a folder use ReportFolder)";
                Debug.LogError(err);
                return err;
            }
            return Run(clip);
        }

        public static string Run(AnimationClip clip)
        {
            if (clip == null)
            {
                const string err = "[ReportClip] FAIL: clip not found";
                Debug.LogError(err);
                return err;
            }

            var body = new StringBuilder();
            body.Append("# ReportClip: ").Append(clip.name).Append('\n');
            body.Append("asset: ").Append(AssetDatabase.GetAssetPath(clip)).Append("\n\n");
            int bindings = RenderClip(clip, body);

            string summary = "[ReportClip] " + clip.name + ": bindings=" + bindings + " => OK";
            string result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "clip_" + clip.name, summary, body.ToString(), ".md");
            Debug.Log(result);
            return result;
        }

        public static string ReportFolder(string assetFolderPath)
        {
            if (string.IsNullOrEmpty(assetFolderPath) || !AssetDatabase.IsValidFolder(assetFolderPath))
            {
                string err = "[ReportClip] FAIL: not a valid asset folder: " + assetFolderPath;
                Debug.LogError(err);
                return err;
            }

            var body = new StringBuilder();
            body.Append("# ReportClip (folder): ").Append(assetFolderPath).Append("\n\n");

            int clips = 0, bindings = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { assetFolderPath }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) continue;
                clips++;
                body.Append("## ").Append(clip.name).Append('\n');
                body.Append("asset: ").Append(path).Append("\n\n");
                bindings += RenderClip(clip, body);
                body.Append('\n');
            }

            string leaf = RunLogFormat.Leaf(assetFolderPath);
            string summary = "[ReportClip] " + leaf + " (folder, " + clips + " clips, " + bindings + " bindings) => OK";
            string result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "clips_" + leaf, summary, body.ToString(), ".md");
            Debug.Log(result);
            return result;
        }

        private static int RenderClip(AnimationClip clip, StringBuilder body)
        {
            var floats = AnimationUtility.GetCurveBindings(clip);
            var refs = AnimationUtility.GetObjectReferenceCurveBindings(clip);

            body.Append("| path | type | propertyName | keys |\n");
            body.Append("| --- | --- | --- | --- |\n");

            foreach (var b in floats)
                Row(body, b, FloatKeys(clip, b));
            foreach (var b in refs)
                Row(body, b, RefKeys(clip, b));

            body.Append('\n');
            return floats.Length + refs.Length;
        }

        private static void Row(StringBuilder body, EditorCurveBinding b, string keys)
        {
            string path = string.IsNullOrEmpty(b.path) ? "(root)" : b.path;
            body.Append("| ").Append(Cell(path))
                .Append(" | ").Append(Cell(b.type != null ? b.type.Name : "<null>"))
                .Append(" | ").Append(Cell(b.propertyName))
                .Append(" | ").Append(Cell(keys))
                .Append(" |\n");
        }

        private static string FloatKeys(AnimationClip clip, EditorCurveBinding b)
        {
            var curve = AnimationUtility.GetEditorCurve(clip, b);
            var keys = curve != null ? curve.keys : null;
            if (keys == null || keys.Length == 0) return "0 keys";
            string first = keys[0].value.ToString("F3", CultureInfo.InvariantCulture);
            string last = keys[keys.Length - 1].value.ToString("F3", CultureInfo.InvariantCulture);
            return keys.Length + " keys, " + first + "→" + last;
        }

        private static string RefKeys(AnimationClip clip, EditorCurveBinding b)
        {
            var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
            if (keys == null || keys.Length == 0) return "0 keys";
            var names = new List<string>();
            foreach (var k in keys)
            {
                string n = k.value != null ? k.value.name : "<none>";
                if (names.Count == 0 || names[names.Count - 1] != n) names.Add(n);
            }
            return keys.Length + " keys: " + string.Join(", ", names);
        }

        private static string Cell(string s) => RunLogFormat.Cell(s);
    }
}
