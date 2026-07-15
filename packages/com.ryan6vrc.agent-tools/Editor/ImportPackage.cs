using System;
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
    /// silently re-execute the snippet. The mitigation here is on the semantics side: the RunLog
    /// lands on disk at a STABLE, package-derived path the moment the import starts, so when the
    /// invoking call times out the agent re-reads that same RunLog in a later cheap call (or calls
    /// <see cref="Verify"/>) instead of re-running the multi-hundred-MB import.
    ///
    /// TWO-PHASE, because <c>AssetDatabase.ImportPackage(path, interactive:false)</c> is ASYNC:
    /// completion arrives on <c>AssetDatabase.importPackageCompleted/.importPackageFailed/
    /// .importPackageCancelled</c> on the main thread, so the invoking <c>execute_code</c> call —
    /// which holds the main thread — CANNOT block-wait for it.
    ///   • <see cref="Import"/> validates, pre-writes the RunLog as <c>status=pending</c>, registers
    ///     the callbacks, kicks off the import, and returns immediately (=&gt; PENDING).
    ///   • <see cref="Verify"/> re-reads that RunLog + walks the expected on-disk root and emits a
    ///     PASS / PENDING / FAIL verdict. Verify is the TRUTH, not the callback — an import that
    ///     triggers script compilation reloads the domain and drops the in-flight callback, leaving
    ///     the RunLog stuck at <c>pending</c>. That is why the on-disk walk, not the callback, decides.
    ///
    /// The deterministic missing-asset / stale-remap deep-check stays <see cref="CheckPackage"/>'s job;
    /// Verify names it rather than duplicating it.
    /// </summary>
    [AgentTool]
    public static class ImportPackage
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;
        private const string Note =
            "callback may be dropped by a domain reload if the import triggers script compilation; " +
            "ImportPackage.Verify (walking the on-disk root) is the truth, not this status";

        // ----- Phase 1: start the import ------------------------------------------------------

        /// <summary>Validate <paramref name="packagePath"/> (exists, <c>.unitypackage</c>), pre-write the
        /// RunLog as <c>pending</c> at a stable package-derived path, register the completion callbacks,
        /// and start the async import — returning immediately without waiting for it. Idempotent: a
        /// re-import overwrites the same RunLog. With <paramref name="whatIf"/> true, validate and report
        /// the plan (including the RunLog path it WOULD write) without importing or writing anything.
        /// Summary: <c>[ImportPackage] STARTED &lt;name&gt; =&gt; PENDING | log=&lt;path&gt;</c>. Bad input is a
        /// bare <c>[ImportPackage] FAIL: …</c> naming the fix, with no trailer.</summary>
        public static string Import(string packagePath, bool whatIf = false)
        {
            string bad = ValidatePackagePath(packagePath, requireExists: true);
            if (bad != null) return Fail(bad);

            string name = Path.GetFileNameWithoutExtension(packagePath);
            string logPath = LogPath(packagePath);

            if (whatIf)
            {
                // Plan only: no import, no file written. `wouldLog=` (not `log=`) so the trailer never
                // points at an artifact that is not on disk.
                var plan = $"[ImportPackage] WHATIF {name}: valid .unitypackage, would import and record => WHATIF | wouldLog={logPath}";
                Debug.Log(plan);
                return plan;
            }

            string written;
            try { written = WriteImportLog(logPath, packagePath, name, "pending", null); }
            catch (Exception e) { return Fail("could not pre-write RunLog at " + logPath + ": " + e.Message); }

            // Register the completion callbacks BEFORE kicking off the import. Each closes over this
            // import's expected name + RunLog path and rewrites the log in place, then unregisters the
            // whole trio. Name-guarded so a concurrent import of a differently-named package is ignored
            // by this handler and left for its own.
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

            // Async: returns before the import finishes (docs/unity.md §Sharp edges "execute_code is
            // async-blind"). The RunLog is already on disk as `pending`, so the contract holds even if
            // this call's transport times out here.
            AssetDatabase.ImportPackage(packagePath, false);

            var summary = $"[ImportPackage] STARTED {name} => PENDING | log={written}";
            Debug.Log(summary);
            return summary;
        }

        // ----- Phase 2: verify the result -----------------------------------------------------

        /// <summary>Re-read the import RunLog for <paramref name="packagePath"/> and, when
        /// <paramref name="expectedRoot"/> (an <c>Assets/…</c> folder) is given, walk it: PASS when the
        /// root exists and holds ≥1 imported asset, PENDING when the RunLog is still <c>pending</c> and
        /// the editor is busy (import likely still running), FAIL otherwise (naming why). Without
        /// <paramref name="expectedRoot"/>, report what the RunLog recorded (on-disk state unverified).
        /// The on-disk walk is authoritative over the callback-written status. Summary:
        /// <c>[ImportPackage] VERIFY &lt;name&gt; (…) =&gt; RESULT | log=&lt;path&gt;</c>; run
        /// <c>CheckPackage.VerifyFolder</c> for deep import health (missing refs / stale remap).</summary>
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

            if (logExists) summary += " | log=" + logPath;
            summary += " | next=run CheckPackage.VerifyFolder for import health";

            if (v == Verdict.Fail) Debug.LogError(summary); else Debug.Log(summary);
            return summary;
        }

        // ----- Pure decision core (unit-tested against the full matrix) -----------------------

        public enum Verdict { Pass, Pending, Fail }

        /// <summary>The verify decision table, factored pure so every branch — including the
        /// editor-busy PENDING one that can't be provoked in a headless test — is directly assertable.
        /// <paramref name="status"/> is the RunLog's recorded status (null = no RunLog on disk).</summary>
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

            // No expected root: trust the RunLog, and say the on-disk state is unverified.
            switch (status)
            {
                case "completed":
                    reason = "RunLog reports completed (on-disk not verified — pass an expectedRoot or run CheckPackage.VerifyFolder)";
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
                    reason = "no RunLog on disk for this package — Import was never started (or wrote elsewhere)";
                    return Verdict.Fail;
            }
        }

        // ----- RunLog IO ----------------------------------------------------------------------

        // Stable, package-derived RunLog path so Verify reconstructs the exact path Import wrote — the
        // whole point of the transport-survivable contract. No timestamp: a re-import overwrites, and the
        // agent always knows where to look. Keyed on the package file's leaf name (the natural handle);
        // two distinct packages sharing a leaf name would collide — acceptable and legible.
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

        // Callback context: swallow write failures (the callback fires later on the main thread; there is
        // no caller to return an error to). The on-disk root walk in Verify is the truth regardless.
        private static void TryRewrite(string logPath, string packagePath, string name, string status, string error)
        {
            try { WriteImportLog(logPath, packagePath, name, status, error); }
            catch (Exception e) { Debug.LogWarning($"[ImportPackage] could not update RunLog {logPath} to {status}: {e.Message}"); }
        }

        private static readonly Regex StatusRe =
            new Regex("\"status\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.Compiled);

        // Lightweight status read — we own the format, and a full JSON parser is not worth the dependency.
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

        // Count on-disk asset files (excluding .meta) under an Assets-relative folder. "Imported files",
        // the non-trivial signal Verify's PASS turns on.
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
            // Application.dataPath = <project>/Assets; asset paths are project-relative ("Assets/…").
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetFolder.Replace('/', Path.DirectorySeparatorChar));
        }

        // The completed/failed/cancelled callbacks hand back the package's display name (file name
        // without path or extension). Match case-insensitively against ours.
        private static bool NameMatches(string callbackName, string ourName) =>
            string.Equals(callbackName, ourName, StringComparison.OrdinalIgnoreCase);

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
