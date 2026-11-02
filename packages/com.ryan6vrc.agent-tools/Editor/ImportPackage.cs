using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The heavy-import door: a <c>.unitypackage</c> import whose result contract survives a
    /// transport timeout. MCP-for-Unity's transport times out on a 60–700MB
    /// <see cref="AssetDatabase.ImportPackage(string,bool)"/> and its connection-level retry can
    /// silently re-execute the snippet. Contract: docs/unity-tools.md.
    /// </summary>
    [AgentTool]
    public static class ImportPackage
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;
        private const string Note =
            "callback may be dropped by a domain reload if the import triggers script compilation; " +
            "ImportPackage.Verify (walking the on-disk root) is the truth, not this status";

        // ----- Phase 1: start the import ------------------------------------------------------

        /// <summary>Pre-write the RunLog as <c>pending</c>, register the completion callbacks and start the
        /// async import, returning immediately. With <paramref name="whatIf"/> true, validate and report the
        /// plan without importing or writing anything.</summary>
        public static string Run(string packagePath, bool whatIf = false)
        {
            string bad = ValidatePackagePath(packagePath, requireExists: true);
            if (bad != null) return Fail(bad);

            string name = Path.GetFileNameWithoutExtension(packagePath);
            string logPath = LogPath(packagePath);

            if (whatIf)
            {
                var plan = $"[ImportPackage] WHATIF {name}: valid .unitypackage, would import and record => WHATIF | wouldLog={logPath}";
                Debug.Log(plan);
                return plan;
            }

            string written;
            try { written = WriteImportLog(logPath, packagePath, name, "pending", null); }
            catch (Exception e) { return Fail("could not pre-write RunLog at " + logPath + ": " + e.Message); }

            // Name-guarded: a concurrent import of a differently-named package is left for its own handler.
            AssetDatabase.ImportPackageCallback onDone = null;
            AssetDatabase.ImportPackageFailedCallback onFail = null;
            AssetDatabase.ImportPackageCallback onCancel = null;

            Action unhook = () =>
            {
                AssetDatabase.importPackageCompleted -= onDone;
                AssetDatabase.importPackageFailed -= onFail;
                AssetDatabase.importPackageCancelled -= onCancel;
            };

            onDone = pkg =>
            {
                if (!NameMatches(pkg, name)) return;
                TryRewrite(logPath, packagePath, name, "completed", null);
                unhook();
            };
            onFail = (pkg, err) =>
            {
                if (!NameMatches(pkg, name)) return;
                TryRewrite(logPath, packagePath, name, "failed", err);
                unhook();
            };
            onCancel = pkg =>
            {
                if (!NameMatches(pkg, name)) return;
                TryRewrite(logPath, packagePath, name, "cancelled", null);
                unhook();
            };

            AssetDatabase.importPackageCompleted += onDone;
            AssetDatabase.importPackageFailed += onFail;
            AssetDatabase.importPackageCancelled += onCancel;

            // Async: returns before the import finishes (docs/unity.md §Sharp edges).
            AssetDatabase.ImportPackage(packagePath, false);

            var summary = $"[ImportPackage] STARTED {name} => PENDING | log={written}";
            Debug.Log(summary);
            return summary;
        }

        // ----- Phase 2: verify the result -----------------------------------------------------

        /// <summary>Re-read the import RunLog for <paramref name="packagePath"/> and, when
        /// <paramref name="expectedRoot"/> (an <c>Assets/…</c> folder) is given, walk it.
        /// <see cref="Decide"/> is the verdict table.</summary>
        public static string Verify(string packagePath, string expectedRoot = null)
        {
            string bad = ValidatePackagePath(packagePath, requireExists: false);
            if (bad != null) return Fail(bad);

            string name = Path.GetFileNameWithoutExtension(packagePath);
            string logPath = LogPath(packagePath);
            bool logExists = File.Exists(logPath);
            string status = logExists ? ReadStatus(logPath) : null; // null = no RunLog on disk

            bool rootProvided = !string.IsNullOrEmpty(expectedRoot);
            bool rootExists = rootProvided && AssetDatabase.IsValidFolder(expectedRoot);
            int fileCount = rootExists ? CountImportedFiles(expectedRoot) : 0;
            bool editorBusy = EditorApplication.isCompiling || EditorApplication.isUpdating;

            string reason;
            Verdict v = Decide(status, editorBusy, rootProvided, rootExists, fileCount, out reason);

            string rootLabel = rootProvided ? expectedRoot : "-";
            var summary = string.Format(CultureInfo.InvariantCulture,
                "[ImportPackage] VERIFY {0} (status={1}, root={2}, files={3}, busy={4}): {5} => {6}",
                name, status ?? "none", rootLabel, fileCount, editorBusy ? "yes" : "no", reason, Token(v));

            // A wrong expectedRoot is routine, since seller-wrapped packages bury their content, and the
            // completion callbacks carry only a package name, so the folder the import created is unknowable.
            // How far down the guess survives is enough to aim the retry.
            if (v == Verdict.Fail && rootProvided && !rootExists)
                summary += " | " + DescribeNearestExistingRoot(expectedRoot, packagePath);

            if (logExists) summary += " | log=" + logPath;
            summary += " | next=run CheckPackage.Run for import health";

            if (v == Verdict.Fail) Debug.LogError(summary); else Debug.Log(summary);
            return summary;
        }

        // ----- Miss pointer ---------------------------------------------------------------------

        /// <summary>Name the deepest existing prefix of a missed <paramref name="expectedRoot"/> and what it
        /// actually contains, so the caller can re-aim without a second round trip.</summary>
        internal static string DescribeNearestExistingRoot(string expectedRoot, string packagePath,
                                                          Func<string, List<string>> listSubfolders = null)
        {
            listSubfolders = listSubfolders ?? (p => new List<string>(AssetDatabase.GetSubFolders(p)));

            var parts = expectedRoot.Replace('\\', '/').Split('/');
            string deepest = null;
            var probe = "";
            for (int i = 0; i < parts.Length; i++)
            {
                probe = i == 0 ? parts[0] : probe + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(probe)) break;
                deepest = probe;
            }
            if (deepest == null) return "no part of '" + expectedRoot + "' exists — is the path rooted at Assets/?";

            var children = listSubfolders(deepest);
            string names;
            if (children.Count == 0) names = "no subfolders";
            else
            {
                const int Cap = 8;
                var leaves = new List<string>();
                for (int i = 0; i < children.Count && i < Cap; i++) leaves.Add(RunLogFormat.Leaf(children[i]));
                names = string.Join(", ", leaves.ToArray()) + (children.Count > Cap ? ", +" + (children.Count - Cap) + " more" : "");
            }
            string pkg = RunLogFormat.Q(packagePath);
            return children.Count == 0
                ? "deepest existing is '" + deepest + "' containing [" + names
                  + "] — nothing was imported under it, so the package landed elsewhere (or not at all); re-run "
                  + "Verify(" + pkg + ") with no expectedRoot to read the import log's own status"
                : "deepest existing is '" + deepest + "' containing [" + names
                  + "] — re-run Verify(" + pkg + ", expectedRoot: " + RunLogFormat.Q(deepest + "/<one of those>") + ")";
        }

        // ----- Pure decision core ---------------------------------------------------------------

        public enum Verdict { Pass, Pending, Fail }

        /// <summary>The verify decision table. <paramref name="status"/> is the RunLog's recorded status
        /// (null = no RunLog on disk).</summary>
        public static Verdict Decide(string status, bool editorBusy, bool rootProvided,
                                     bool rootExists, int importedFileCount, out string reason)
        {
            if (rootProvided)
            {
                if (rootExists && importedFileCount > 0)
                {
                    reason = importedFileCount + " imported file(s) under the expected root";
                    return Verdict.Pass;
                }
                if (status == "pending" && editorBusy)
                {
                    reason = "expected root not yet populated and the editor is busy — import still running";
                    return Verdict.Pending;
                }
                reason = rootExists
                    ? "expected root exists but is empty — import did not land (status=" + (status ?? "none") + ")"
                    : "expected root does not exist — import did not land (status=" + (status ?? "none") + ")";
                return Verdict.Fail;
            }

            switch (status)
            {
                case "completed":
                    reason = "RunLog reports completed (on-disk not verified — pass an expectedRoot or run CheckPackage.Run)";
                    return Verdict.Pass;
                case "pending" when editorBusy:
                    reason = "RunLog pending and the editor is busy — import still running";
                    return Verdict.Pending;
                case "pending":
                    reason = "RunLog stuck at pending with the editor idle — the completion callback was likely dropped by a domain reload; pass an expectedRoot to confirm on disk";
                    return Verdict.Fail;
                case "failed":
                    reason = "RunLog reports the import failed";
                    return Verdict.Fail;
                case "cancelled":
                    reason = "RunLog reports the import was cancelled";
                    return Verdict.Fail;
                default:
                    reason = "no RunLog on disk for this package — Run was never started (or wrote elsewhere)";
                    return Verdict.Fail;
            }
        }

        // ----- RunLog IO ----------------------------------------------------------------------

        // Stable so Verify reconstructs the exact path Run wrote; a re-import overwrites. Keyed on the
        // package file's leaf name, so two distinct packages sharing a leaf name collide.
        internal static string LogPath(string packagePath) =>
            RunLogDir + "/import-package_" + RunLogFormat.Sanitize(Path.GetFileNameWithoutExtension(packagePath)) + ".json";

        internal static string WriteImportLog(string logPath, string packagePath, string name, string status, string error)
        {
            Directory.CreateDirectory(RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"import-package\",\n");
            sb.Append("  \"unityVersion\": ").Append(RunLogFormat.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(RunLogFormat.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"packagePath\": ").Append(RunLogFormat.Q(packagePath)).Append(",\n");
            sb.Append("  \"packageName\": ").Append(RunLogFormat.Q(name)).Append(",\n");
            sb.Append("  \"status\": ").Append(RunLogFormat.Q(status)).Append(",\n");
            sb.Append("  \"error\": ").Append(RunLogFormat.Q(error)).Append(",\n");
            sb.Append("  \"note\": ").Append(RunLogFormat.Q(Note)).Append("\n");
            sb.Append("}");
            File.WriteAllText(logPath, sb.ToString());
            AssetDatabase.Refresh();
            return logPath;
        }

        // Callback context: there is no caller to return an error to, and Verify's root walk decides
        // regardless.
        private static void TryRewrite(string logPath, string packagePath, string name, string status, string error)
        {
            try { WriteImportLog(logPath, packagePath, name, status, error); }
            catch (Exception e) { Debug.LogWarning($"[ImportPackage] could not update RunLog {logPath} to {status}: {e.Message}"); }
        }

        private static readonly Regex StatusRe =
            new Regex("\"status\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        internal static string ReadStatus(string logPath)
        {
            try
            {
                var m = StatusRe.Match(File.ReadAllText(logPath));
                return m.Success ? m.Groups[1].Value : null;
            }
            catch { return null; }
        }

        // ----- Helpers ------------------------------------------------------------------------

        private static int CountImportedFiles(string assetFolder)
        {
            string full = ToFullPath(assetFolder);
            if (!Directory.Exists(full)) return 0;
            int n = 0;
            foreach (var f in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                if (!f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }

        private static string ToFullPath(string assetFolder)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetFolder.Replace('/', Path.DirectorySeparatorChar));
        }

        // Measured (Unity 2022.3.22f1, live import): a completion callback hands back the package's full
        // path minus extension — `C:\vendor\Foo` for `C:\vendor\Foo.unitypackage` — not its leaf name.
        // Take the leaf of whatever arrives, which also accepts a bare name.
        internal static bool NameMatches(string callbackName, string ourName) =>
            !string.IsNullOrEmpty(callbackName) &&
            string.Equals(Path.GetFileName(callbackName), ourName, StringComparison.OrdinalIgnoreCase);

        private static string ValidatePackagePath(string packagePath, bool requireExists)
        {
            if (string.IsNullOrEmpty(packagePath)) return "packagePath is required";
            if (!packagePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                return "not a .unitypackage: " + packagePath;
            if (requireExists && !File.Exists(packagePath)) return "file does not exist: " + packagePath;
            return null;
        }

        private static string Token(Verdict v) => v == Verdict.Pass ? "PASS" : v == Verdict.Pending ? "PENDING" : "FAIL";

        private static string Fail(string reason)
        {
            var s = "[ImportPackage] FAIL: " + reason;
            Debug.LogError(s);
            return s;
        }
    }
}
