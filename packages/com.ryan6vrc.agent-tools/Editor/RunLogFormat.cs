using System;
using System.IO;
using System.Text;
using UnityEditor;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The shared RunLog text conventions — JSON string-escape, filename sanitize, the asset-path
    /// leaf label, the canonical output directories, and the body-agnostic artifact writer — used by
    /// every agent-facing tool that emits a one-line summary + a RunLog/Snapshot artifact. One
    /// implementation; sibling packages delegate here so the envelope cannot drift per package.
    /// </summary>
    public static class RunLogFormat
    {
        /// <summary>Canonical verdict/record output directory. The single declaration of this literal;
        /// sibling emitters reference it (kept <c>const</c> so callers can concat it into a path).</summary>
        public const string RunLogDir = "Assets/Agent/RunLogs";

        /// <summary>Canonical read-only-capture output directory. Single declaration of this literal.</summary>
        public const string SnapshotDir = "Assets/Agent/Snapshots";

        /// <summary>
        /// Body-agnostic artifact writer the family's non-transplant emitters converge on. Writes
        /// <paramref name="body"/> verbatim (the caller's full artifact text, markdown or JSON) to
        /// <paramref name="dir"/> under a <see cref="Sanitize"/>d-<paramref name="label"/> + timestamp +
        /// <paramref name="ext"/> filename; refreshes the AssetDatabase;
        /// and on success returns <paramref name="summary"/> with the in-band trailer appended
        /// (<c>summary + " | log=" + path</c>). <paramref name="summary"/> is the one-line verdict WITHOUT
        /// the trailer (ends <c>=&gt; RESULT</c>). On write failure returns a bare-FAIL summary with NO
        /// <c>| log=</c> trailer — the schema never points at an artifact that is not on disk. Does NOT
        /// log: the 5-param signature carries no PASS/FAIL flag, so each caller logs the returned summary
        /// at the right severity (as ReportController/ReportClip/CheckAnimator/CheckAvatar/ReportGimmick do).
        /// </summary>
        public static string WriteRunLog(string dir, string label, string summary, string body, string ext)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var path = dir + "/" + Sanitize(label) + "_" + stamp + ext;
                File.WriteAllText(path, body);
                AssetDatabase.Refresh();
                return summary + " | log=" + path;
            }
            catch (Exception e)
            {
                // The caller already baked its verdict into `summary` (ends "=> OK" / "=> PASS" / "=> FAIL").
                // Replace that verdict with a single honest bare-FAIL (no `| log=` trailer) so the emitted
                // line cannot assert both a verdict and a write failure at once.
                int arrow = summary.LastIndexOf("=> ", StringComparison.Ordinal);
                string head = arrow >= 0 ? summary.Substring(0, arrow) : summary + " ";
                return head + "=> FAIL: write failed: " + e.Message;
            }
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

        /// <summary>Path basename. Null or empty → "". Otherwise <c>TrimEnd('/')</c> then the substring
        /// after the last '/' (the whole trimmed string if none). The RunLog-label leaf helper the
        /// tools converge on.</summary>
        public static string Leaf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return "";
            var p = assetPath.TrimEnd('/');
            int i = p.LastIndexOf('/');
            return i >= 0 ? p.Substring(i + 1) : p;
        }
    }
}
