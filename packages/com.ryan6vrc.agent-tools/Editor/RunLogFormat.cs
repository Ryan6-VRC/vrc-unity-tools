using System;
using System.IO;
using System.Text;
using UnityEditor;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>The text conventions and artifact writer shared by every tool, in both packages, that
    /// emits a one-line summary plus a RunLog or Snapshot artifact.</summary>
    public static class RunLogFormat
    {
        /// <summary>Verdict and record output directory.</summary>
        public const string RunLogDir = "Assets/Agent/RunLogs";

        /// <summary>Read-only-capture output directory.</summary>
        public const string SnapshotDir = "Assets/Agent/Snapshots";

        /// <summary>
        /// Writes <paramref name="body"/> verbatim to <paramref name="dir"/> and returns
        /// <paramref name="summary"/> (the one-line verdict, ending <c>=&gt; RESULT</c>) with
        /// <c> | log=path</c> appended. <paramref name="label"/> starts with the artifact's <c>kind</c>
        /// (<c>check-package_&lt;target&gt;</c>), so a glob by kind finds it. On write failure the verdict
        /// becomes a bare FAIL with no trailer, so the line never points at an artifact not on disk. Does
        /// not log: each caller logs the returned line at its own severity.
        /// </summary>
        public static string WriteRunLog(string dir, string label, string summary, string body, string ext)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var path = dir + "/" + Sanitize(label) + "_" + stamp + ext;
                File.WriteAllText(path, body);
                PublishArtifact(dir, path);
                return summary + " | log=" + path;
            }
            catch (Exception e)
            {
                int arrow = summary.LastIndexOf("=> ", StringComparison.Ordinal);
                string head = arrow >= 0 ? summary.Substring(0, arrow) : summary + " ";
                return head + "=> FAIL: write failed: " + e.Message;
            }
        }

        /// <summary>
        /// Makes a just-written artifact visible in the Project window by importing only that file, because
        /// every artifact-emitting door pays this call and a project-wide <c>AssetDatabase.Refresh()</c> is
        /// costly. <c>ImportAsset</c> cannot register a file in a folder the database has never seen, and
        /// <c>Directory.CreateDirectory</c> leaves a new folder unseen, so the first write under a fresh
        /// <paramref name="dir"/> still pays one full refresh.
        /// </summary>
        public static void PublishArtifact(string dir, string path)
        {
            if (AssetDatabase.IsValidFolder(dir)) AssetDatabase.ImportAsset(path);
            else AssetDatabase.Refresh();
        }

        /// <summary>JSON string literal for <paramref name="s"/> (quotes included); null → null.</summary>
        public static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }

        /// <summary>Filesystem-safe token for RunLog filenames. Null → "null".</summary>
        public static string Sanitize(string s)
        {
            if (s == null) return "null";
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s.Replace(' ', '_');
        }

        public static string Leaf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return "";
            var p = assetPath.TrimEnd('/');
            int i = p.LastIndexOf('/');
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        /// <summary>Encodes a dynamic value (a name, path, or property read off an asset) for one Markdown
        /// table cell. Backslash needs escaping because a raw '\' immediately before a pipe consumes the
        /// pipe's own '\' escape (GFM counts the backslash run) and leaves the pipe bare, breaking the column.
        /// U+2028/U+2029 are line separators <c>char.IsControl</c> does not catch. Backticks stay unescaped:
        /// backslash escapes are inert inside the code span many call sites wrap the value in, and a stray
        /// backtick is cosmetic and stays inside its own cell.</summary>
        public static string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '|':  sb.Append("\\|"); break;
                    default:   sb.Append(char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c); break;
                }
            return sb.ToString();
        }

        /// <summary>Resolves an asset path or GUID to the loaded asset, or null if it names none. A door
        /// that takes an asset exposes its string overload through here.</summary>
        public static T LoadByPathOrGuid<T>(string handle) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(handle)) return null;
            var guidPath = AssetDatabase.GUIDToAssetPath(handle);
            var path = string.IsNullOrEmpty(guidPath) ? handle : guidPath;
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        /// <summary>Does the asset behind this GUID exist? <see cref="AssetDatabase.GUIDToAssetPath"/> alone
        /// cannot say: a GUID the editor has ever known keeps resolving to its old path after the asset is
        /// deleted, surviving a <c>ForceUpdate</c>, so only a never-known GUID reads as dangling on the path.
        ///
        /// Type-agnostic, because <c>LoadAssetAtPath&lt;Motion&gt;</c> reads an FBX-embedded clip (main asset
        /// a <c>GameObject</c>) and a controller sub-asset as dangling, and both are routine on vendor avatars.
        ///
        /// Asks the database, not the loader: <c>LoadMainAssetAtPath</c> would materialize an FBX's whole
        /// model hierarchy per GUID, and answers "won't load" rather than "isn't there", so a project
        /// mid-reimport would read as broken.</summary>
        public static bool AssetGuidResolves(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return !string.IsNullOrEmpty(path) && AssetDatabase.GetMainAssetTypeAtPath(path) != null;
        }
    }
}
