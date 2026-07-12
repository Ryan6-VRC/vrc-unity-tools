using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Self-driving VRChat upload for a batch of avatars, driving CAU (optional) by reflection.
    /// Operator-gated (the skill's explicit "upload now" is the trigger), fail-loud, no auto-work-around.
    /// PASS/REFUSE/FAIL — REFUSE = environment not ready (CAU absent, not logged in, panel closed,
    /// wrong build target); FAIL = a genuine upload rejection. Spec: 2026-07-11-upload-readiness-design.md.
    ///
    /// The loop lives in <see cref="RunCore"/>, which is pure over an injectable per-avatar delegate + a
    /// saveAssets action so all stop/classify/redact semantics are unit-testable with a fake delegate. The
    /// REAL upload adapter (<see cref="RealUploadOne"/>) wraps <see cref="CauReflect.UploadOne"/> and owns
    /// the two decisions that need live SDK state: the id-persistence verify (Uploaded vs ReservedNoBundle)
    /// and the per-handle attempt ceiling. RunCore trusts the <see cref="UploadOutcome"/> it is handed.
    /// </summary>
    [AgentTool]
    public static class UploadAvatar
    {
        // ── Outcome / report types ────────────────────────────────────────────────────────────

        /// <summary>The verdict the per-avatar delegate hands back to <see cref="RunCore"/>. Factory-only
        /// (the kind is private) so callers can't fabricate an inconsistent state.</summary>
        public struct UploadOutcome
        {
            internal enum Kind { Uploaded, ReservedNoBundle, Failed }
            internal Kind kind;
            internal int? httpStatus;
            internal bool isValidation;
            internal bool isTimeout;
            internal string message;

            public static UploadOutcome Uploaded()
                => new UploadOutcome { kind = Kind.Uploaded };
            public static UploadOutcome ReservedNoBundle()
                => new UploadOutcome { kind = Kind.ReservedNoBundle };
            public static UploadOutcome Failed(int? httpStatus = null, bool isValidation = false,
                                               bool isTimeout = false, string message = null)
                => new UploadOutcome
                {
                    kind = Kind.Failed, httpStatus = httpStatus,
                    isValidation = isValidation, isTimeout = isTimeout, message = message
                };
        }

        /// <summary>One RunLog row. No <c>blueprintId</c> field by construction — the id never enters
        /// output. <c>cls</c> is the failure class (transient|rate-limit|real); empty for a success row.</summary>
        public struct Row { public string handle, state, result, cls, error; }

        public sealed class UploadReport
        {
            public List<Row> rows = new List<Row>();
            public string result; // PASS / FAIL / REFUSE
        }

        // Per-handle attempt ceiling, static so a skill-driven re-invocation of Run is capped across
        // calls (account safety must not depend on skill prose). Cleared on each SUCCESS so it counts
        // only CONSECUTIVE failures — legitimate successful re-uploads of the same avatar never trip it.
        // Consulted only by the REAL adapter — RunCore stays a single, ledger-free pass so the
        // fake-delegate tests are unaffected.
        static readonly UploadAvatarLogic.AttemptLedger _ledger = new UploadAvatarLogic.AttemptLedger();

        // ── The loop (pure, injectable, fully unit-tested) ──────────────────────────────────────

        /// <summary>Drive one pass over <paramref name="avatars"/> with an injected per-avatar delegate.
        /// Semantics: null entry → a named error row + STOP; Uploaded → <paramref name="saveAssets"/> then
        /// an <c>uploaded</c> row + continue; ReservedNoBundle → a <c>reserved-no-bundle</c> row + STOP;
        /// Failed → classify + redact into a <c>failed</c> row + STOP (no further avatars attempted). The
        /// tool never auto-retries — a single pass is the whole contract. The id-persistence verify (which
        /// needs the live id CAU writes) is NOT here; it lives in the real adapter that produces the
        /// outcome. RunCore trusts the outcome and only records rows / counts / stop-semantics.</summary>
        public static UploadReport RunCore(GameObject[] avatars,
                                           Func<GameObject, UploadOutcome> uploadOne,
                                           Action saveAssets)
        {
            var report = new UploadReport();
            int n = avatars != null ? avatars.Length : 0;

            for (int i = 0; i < n; i++)
            {
                var go = avatars[i];
                if (go == null)
                {
                    report.rows.Add(new Row { handle = "(null)", state = "unknown",
                                              result = "failed", error = "null avatar entry" });
                    report.result = "FAIL";
                    return report;
                }

                string state = ClassifyAvatar(go).state;
                var outcome = uploadOne(go);

                switch (outcome.kind)
                {
                    case UploadOutcome.Kind.Uploaded:
                        saveAssets();
                        report.rows.Add(new Row { handle = go.name, state = state, result = "uploaded" });
                        break;

                    case UploadOutcome.Kind.ReservedNoBundle:
                        report.rows.Add(new Row { handle = go.name, state = state,
                                                  result = "reserved-no-bundle" });
                        report.result = "FAIL";
                        return report;

                    case UploadOutcome.Kind.Failed:
                        report.rows.Add(new Row
                        {
                            handle = go.name, state = state, result = "failed",
                            cls = UploadAvatarLogic.Classify(outcome.httpStatus, outcome.isValidation, outcome.isTimeout),
                            error = UploadAvatarLogic.RedactIds(outcome.message),
                        });
                        report.result = "FAIL";
                        return report;
                }
            }

            report.result = "PASS";
            return report;
        }

        // ── Entry point ─────────────────────────────────────────────────────────────────────────

        public static string Run(GameObject[] avatars, bool whatIf = false)
        {
            if (!CauReflect.IsAvailable)
                return Refuse("CAU not installed; self-driving upload unavailable — install " +
                              "com.anatawa12.continuous-avatar-uploader (ALCOM), or upload via the SDK panel");

            var refuse = CheckPreconditions();
            if (refuse != null) return Refuse(refuse);

            if (whatIf)
            {
                // Per-avatar read-only classification. Uploads nothing, dirties nothing.
                int n = avatars != null ? avatars.Length : 0;
                var rows = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    var go = avatars[i];
                    var (state, publishName) = ClassifyAvatar(go);
                    bool noPm = go == null || go.GetComponent<PipelineManager>() == null;
                    if (rows.Length > 0) rows.Append("; ");
                    rows.Append("handle=").Append(go != null ? go.name : "(null)")
                        .Append(" state=").Append(state)
                        .Append(" publishName=").Append(publishName);
                    if (noPm) rows.Append(" (no PipelineManager — never uploaded)");
                }
                return "[upload-avatar] (whatIf) " + n + " avatar(s): " + rows + " => PASS";
            }

            // Execute path: acquire the builder once, then drive the real per-avatar upload through RunCore.
            if (!CauReflect.TryGetBuilder(out var builder, out var whyBuilder))
                return Refuse(whyBuilder);

            var report = RunCore(avatars, go => RealUploadOne(go, builder), () => AssetDatabase.SaveAssets());
            string logPath = WriteRunLog(report, whatIf: false, label: "all");
            return BuildSummary(report, whatIf: false, logPath);
        }

        /// <summary>The REAL per-avatar upload: attempt-ceiling gate → build the CAU setting →
        /// <see cref="CauReflect.UploadOne"/> → id-persistence verify. This is where the two live-state
        /// decisions live so RunCore stays pure:
        ///   • Uploaded vs ReservedNoBundle — after the upload we <c>SaveAssets()</c> (CAU writes the new
        ///     blueprintId into the PipelineManager; SaveAssets persists it) and re-read the state. A
        ///     first-upload whose id still didn't persist means the record was reserved but no bundle
        ///     landed → ReservedNoBundle.
        ///   • The <see cref="UploadAvatarLogic.AttemptLedger"/> caps re-attempts per handle at 3.
        ///
        /// LIVE-INTEGRATION RISK (Task-8): CAU's UploadSingle is async and may need editor update ticks to
        /// progress; blocking on it via GetAwaiter().GetResult() from this synchronous tool MAY deadlock
        /// the main thread and force an async/coroutine pump refactor. Not verifiable headless (CAU absent).</summary>
        static UploadOutcome RealUploadOne(GameObject go, object builder)
        {
            string handle = go.name;

            if (!_ledger.MayAttempt(handle))
                return UploadOutcome.Failed(message:
                    "attempt ceiling (" + UploadAvatarLogic.AttemptLedger.MaxAttempts +
                    ") reached for '" + handle + "'; not re-attempting");
            _ledger.Record(handle);

            var desc = go.GetComponent<VRCAvatarDescriptor>();
            if (desc == null)
                return UploadOutcome.Failed(message: "no VRCAvatarDescriptor on '" + handle + "'");

            if (!CauReflect.TryBuildSetting(desc, out var setting, out var why))
                return UploadOutcome.Failed(message: why);

            bool wasFirstUpload = ClassifyAvatar(go).state == "first-upload";

            try
            {
                // Synchronous block on the async upload — see the RISK note above.
                bool ok = CauReflect.UploadOne(setting, builder, CancellationToken.None)
                            .GetAwaiter().GetResult();
                if (!ok)
                    return UploadOutcome.Failed(message: "CAU UploadSingle returned false");

                AssetDatabase.SaveAssets();
                if (wasFirstUpload && ClassifyAvatar(go).state == "first-upload")
                    return UploadOutcome.ReservedNoBundle();
                _ledger.Clear(handle); // success resets the ceiling — it counts only consecutive failures
                return UploadOutcome.Uploaded();
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                return UploadOutcome.Failed(
                    httpStatus:   ExtractHttpStatus(inner),
                    isValidation: inner.GetType().Name.IndexOf("Validation", StringComparison.OrdinalIgnoreCase) >= 0,
                    isTimeout:    inner is TimeoutException,
                    message:      inner.Message);
            }
        }

        /// <summary>Best-effort HTTP status off an SDK exception: an int / HttpStatusCode property named
        /// <c>StatusCode</c> or <c>Status</c>, else null. The exact SDK exception shape is a Task-8
        /// live-validation item — this is a reflective placeholder, not a proven mapping.</summary>
        static int? ExtractHttpStatus(Exception e)
        {
            foreach (var name in new[] { "StatusCode", "Status", "HttpStatusCode" })
            {
                var p = e.GetType().GetProperty(name);
                if (p == null) continue;
                var v = p.GetValue(e);
                if (v is int i) return i;
                if (v != null && v.GetType().IsEnum)
                {
                    try { return Convert.ToInt32(v); } catch { }
                }
            }
            return null;
        }

        static string Refuse(string reason)
            => "[upload-avatar] all => REFUSE error=" + reason;

        // ── Classification / preconditions ──────────────────────────────────────────────────────

        /// <summary>Read-only per-avatar preflight read: (blueprint-state, name-that-would-publish). Reads
        /// PipelineManager.blueprintId via SerializedObject and never surfaces the id itself. No PipelineManager
        /// → treated as first-upload (its absence means the avatar was never uploaded). Mutates nothing.</summary>
        public static (string state, string publishName) ClassifyAvatar(GameObject go)
        {
            var pm = go.GetComponent<PipelineManager>();
            if (pm == null) return (UploadAvatarLogic.ClassifyBlueprint(null), go.name);
            var prop = new SerializedObject(pm).FindProperty("blueprintId");
            string id = prop != null ? prop.stringValue : pm.blueprintId;
            return (UploadAvatarLogic.ClassifyBlueprint(id), go.name);
        }

        /// <summary>The upload REFUSE register: returns the first unmet precondition (with its named fix) or
        /// null when the environment is upload-ready. Order is cheap-to-costly.</summary>
        static string CheckPreconditions()
        {
            if (EditorApplication.isPlaying)
                return "in Play mode — exit Play mode before uploading";
            if (!APIUser.IsLoggedIn)
                return "not logged into the VRChat SDK — open the Control Panel and sign in";
            if (!CauReflect.TryGetBuilder(out _, out var why))
                return why;
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                return "build target is " + EditorUserBuildSettings.activeBuildTarget +
                       "; switch to Windows (StandaloneWindows64)";
            return null;
        }

        // ── RunLog + summary ─────────────────────────────────────────────────────────────────────

        static string BuildSummary(UploadReport report, bool whatIf, string logPath)
        {
            var c = Counts(report);
            string marker = whatIf ? " (whatIf)" : "";
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[upload-avatar]{0} all: uploaded={1} reserved={2} failed={3} (transient={4} rate-limit={5} real={6}) => {7} | log={8}",
                marker, c["uploaded"], c["reserved"], c["failed"],
                c["transient"], c["rate-limit"], c["real"], report.result, logPath);
            if (report.result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        static Dictionary<string, int> Counts(UploadReport report)
        {
            var c = new Dictionary<string, int>
            {
                ["uploaded"] = 0, ["reserved"] = 0, ["failed"] = 0,
                ["transient"] = 0, ["rate-limit"] = 0, ["real"] = 0,
            };
            foreach (var r in report.rows)
            {
                if (r.result == "uploaded") c["uploaded"]++;
                else if (r.result == "reserved-no-bundle") c["reserved"]++;
                else if (r.result == "failed") c["failed"]++;
                if (r.cls == "transient") c["transient"]++;
                else if (r.cls == "rate-limit") c["rate-limit"]++;
                else if (r.cls == "real") c["real"]++;
            }
            return c;
        }

        /// <summary>Tool-local structured RunLog: shared envelope (kind="upload-avatar") + counts + a
        /// bespoke <c>avatars[]</c> array of {handle, state, result, class, error}. No row carries a
        /// blueprintId (the Row type has no such field). Modeled on ConformRenderers.WriteRunLog.</summary>
        static string WriteRunLog(UploadReport report, bool whatIf, string label)
        {
            var c = Counts(report);
            Directory.CreateDirectory(TransplantCore.RunLogDir);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"upload-avatar\",\n");
            sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(whatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"instance\": ").Append(TransplantCore.Q(label)).Append(",\n");
            sb.Append("  \"source\": ").Append(TransplantCore.Q(null)).Append(",\n");
            sb.Append("  \"result\": ").Append(TransplantCore.Q(report.result)).Append(",\n");
            sb.Append("  \"error\": ").Append(TransplantCore.Q(null)).Append(",\n");
            sb.Append("  \"uploaded\": ").Append(c["uploaded"]).Append(",\n");
            sb.Append("  \"reserved\": ").Append(c["reserved"]).Append(",\n");
            sb.Append("  \"failed\": ").Append(c["failed"]).Append(",\n");
            sb.Append("  \"transient\": ").Append(c["transient"]).Append(",\n");
            sb.Append("  \"rate-limit\": ").Append(c["rate-limit"]).Append(",\n");
            sb.Append("  \"real\": ").Append(c["real"]).Append(",\n");
            sb.Append("  \"avatars\": [");

            for (int i = 0; i < report.rows.Count; i++)
            {
                var r = report.rows[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"handle\": ").Append(TransplantCore.Q(r.handle))
                  .Append(", \"state\": ").Append(TransplantCore.Q(r.state))
                  .Append(", \"result\": ").Append(TransplantCore.Q(r.result))
                  .Append(", \"class\": ").Append(TransplantCore.Q(r.cls))
                  .Append(", \"error\": ").Append(TransplantCore.Q(r.error)).Append(" }");
            }

            sb.Append(report.rows.Count > 0 ? "\n  ]\n}" : "]\n}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path  = TransplantCore.RunLogDir + "/upload-avatar_" + TransplantCore.Sanitize(label) + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }
    }
}
