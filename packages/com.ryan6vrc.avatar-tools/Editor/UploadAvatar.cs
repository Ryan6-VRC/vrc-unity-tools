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
            // Bypasses Classify — for deterministic LOCAL failures (missing descriptor, setting-build
            // fail, ceiling) that are always "real", never transient/retryable.
            internal string forcedClass;

            public static UploadOutcome Uploaded()
                => new UploadOutcome { kind = Kind.Uploaded };
            public static UploadOutcome ReservedNoBundle()
                => new UploadOutcome { kind = Kind.ReservedNoBundle };
            public static UploadOutcome Failed(int? httpStatus = null, bool isValidation = false,
                                               bool isTimeout = false, string message = null,
                                               string forcedClass = null)
                => new UploadOutcome
                {
                    kind = Kind.Failed, httpStatus = httpStatus,
                    isValidation = isValidation, isTimeout = isTimeout,
                    message = message, forcedClass = forcedClass
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

        // The in-flight execute batch. Run fires RunCore and returns immediately; the editor update loop
        // pumps CAU's async continuations. The agent polls Status() until it stops reporting "running".
        static System.Threading.Tasks.Task<UploadReport> _inflight;
        // The terminal summary, memoized so Status() is idempotent — the RunLog is written exactly once
        // when the batch first reaches a terminal state, not on every poll. Reset when a new batch starts.
        static string _terminalSummary;

        /// <summary>The status file: this batch's progress, on disk, readable WITHOUT the MCP bridge.
        /// <see cref="Status"/> alone was not enough — it is C# the agent invokes over MCP, and a modal
        /// dialog raised mid-upload (VRCFury's, the SDK's) blocks the editor's main thread, so the poll
        /// itself can never run. The channel that reports the wedge must not depend on the wedged thread.
        /// A file does not: the agent reads it from Bash while MCP is dead. Paired with the terminal write
        /// being self-driven (see <see cref="Watch"/>), a completed batch always lands on disk even if no
        /// poll ever gets through.
        ///
        /// The path is fixed, so every record carries a <c>batchId</c> and <see cref="Run"/> returns it:
        /// match them. Without that the file is indistinguishable from a STALE one, and the stale read is
        /// catastrophic in the exact scenario this file exists for — if the editor is already wedged when
        /// Run is issued, the command never drains, the caller times out, falls back to the file, and reads
        /// the PREVIOUS batch's "done … => PASS". It would conclude an upload that never started succeeded,
        /// on an operation that publishes to a live account.</summary>
        public static string StatusPath => TransplantCore.RunLogDir + "/upload-avatar_status.json";

        // Identity of the current batch, minted in Run and echoed in its return value. Also the batch size,
        // stashed once: `total` otherwise meant avatars.Length on "started" but rows.Count at the terminal,
        // which coincide only because AppendNotAttempted backfills the tail.
        static string _batchId;
        static int _batchTotal;

        // ── The loop (pure, injectable, fully unit-tested) ──────────────────────────────────────

        /// <summary>Drive one pass over <paramref name="avatars"/> with an injected per-avatar delegate.
        /// Semantics: null entry → a named error row + STOP; Uploaded → <paramref name="saveAssets"/> then
        /// an <c>uploaded</c> row + continue; ReservedNoBundle → a <c>reserved-no-bundle</c> row + STOP;
        /// Failed → classify + redact into a <c>failed</c> row + STOP (no further avatars attempted). The
        /// tool never auto-retries — a single pass is the whole contract. The id-persistence verify (which
        /// needs the live id CAU writes) is NOT here; it lives in the real adapter that produces the
        /// outcome. RunCore trusts the outcome and only records rows / counts / stop-semantics.</summary>
        public static async System.Threading.Tasks.Task<UploadReport> RunCore(
            GameObject[] avatars,
            Func<GameObject, System.Threading.Tasks.Task<UploadOutcome>> uploadOne,
            Action saveAssets,
            Action<int, string> onProgress = null)
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
                    AppendNotAttempted(report, avatars, i + 1);
                    return report;
                }

                string state = ClassifyAvatar(go).state;
                // Announce BEFORE awaiting: the upload we are about to start is the one that can raise a
                // modal, and once it does nothing else in this process runs. The status file must already
                // name it, or the agent reads a wedge with no idea which avatar wedged it.
                if (onProgress != null) onProgress(i, go.name);
                var outcome = await uploadOne(go);

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
                        AppendNotAttempted(report, avatars, i + 1);
                        return report;

                    case UploadOutcome.Kind.Failed:
                        report.rows.Add(new Row
                        {
                            handle = go.name, state = state, result = "failed",
                            cls = outcome.forcedClass ??
                                  UploadAvatarLogic.Classify(outcome.httpStatus, outcome.isValidation, outcome.isTimeout),
                            error = UploadAvatarLogic.RedactIds(outcome.message),
                        });
                        report.result = "FAIL";
                        AppendNotAttempted(report, avatars, i + 1);
                        return report;
                }
            }

            report.result = "PASS";
            return report;
        }

        /// <summary>On a halt (null entry, ReservedNoBundle, or Failed) record a <c>not-attempted</c> row for
        /// every remaining avatar so the report is complete — the skill re-feeds the failed + not-attempted
        /// tail. Reads only names; performs no upload and dirties nothing.</summary>
        static void AppendNotAttempted(UploadReport report, GameObject[] avatars, int startExclusive)
        {
            for (int j = startExclusive; j < avatars.Length; j++)
                report.rows.Add(new Row { handle = avatars[j] != null ? avatars[j].name : "(null)",
                                          state = "unknown", result = "not-attempted" });
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

            // Execute path: fire-and-forget the async batch, return immediately. The editor update loop
            // pumps CAU's continuations (blocking here would deadlock the main thread — Task-8 confirmed);
            // the agent polls Status() for progress and the final summary.
            if (_inflight != null && !_inflight.IsCompleted)
                return Refuse("an upload batch is already in flight — poll UploadAvatar.Status()");
            // A completed-but-not-yet-finalized batch: Watch hasn't ticked, so overwriting _inflight here
            // would drop its RunLog entirely — a hole in "the log always lands". Finalize it first
            // (Status() is idempotent and memoized, so this is a no-op if Watch already ran).
            if (_inflight != null) Status();
            if (!CauReflect.TryGetBuilder(out var builder, out var whyBuilder))
                return Refuse(whyBuilder);
            _terminalSummary = null;
            _batchTotal = avatars != null ? avatars.Length : 0;
            _batchId = Guid.NewGuid().ToString("N");

            // The "started" write is MANDATORY, not best-effort: it is what stamps this batch's id over
            // the previous batch's record. If it cannot land, the file still shows the LAST batch's
            // terminal state and a reader would attribute it to this one — so refuse to start, and say so
            // in the return value, the one channel the caller is guaranteed to see.
            if (!WriteStatus("started", "batch started (" + _batchTotal + " avatar(s))", -1, null, out var whyStatus))
            {
                _batchId = null;
                return Refuse("cannot write the status file (" + whyStatus + ") — refusing to start: a " +
                              "stale record would read as this batch's result. Path: " + StatusPath);
            }

            _inflight = RunCore(avatars, go => RealUploadOne(go, builder), () => AssetDatabase.SaveAssets(),
                                (i, name) => WriteStatus("running", "uploading", i, name, out _));
            // Self-driven terminal write: without this the RunLog lands only when the agent polls Status(),
            // so a batch that finishes while MCP is unreachable leaves nothing on disk at all.
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
            return "[upload-avatar] batch started (" + _batchTotal + " avatar(s)) batchId=" + _batchId +
                   "; poll UploadAvatar.Status(), or read " + StatusPath +
                   " from disk (survives a wedged editor) — match its batchId to the one above, or you " +
                   "are reading a previous batch";
        }

        /// <summary>Finalize the batch the moment it completes, rather than when someone asks. Registered on
        /// the editor update loop by <see cref="Run"/> and unregistered as soon as it fires.</summary>
        static void Watch()
        {
            if (_inflight == null || !_inflight.IsCompleted) return;
            EditorApplication.update -= Watch;
            try { Status(); }                    // idempotent: writes the RunLog + memoizes, exactly once
            catch (Exception e) { Debug.LogError("[upload-avatar] terminal write failed: " + e.Message); }
        }

        /// <summary>Write the disk-side status; returns false + why on failure. Only the "started" write
        /// treats that as fatal (see <see cref="Run"/>) — a later one failing leaves a correctly-identified
        /// record that is merely behind, and the RunLog and <see cref="Status"/> remain the authority. It
        /// never throws into the upload: a status write must not fail a batch.
        ///
        /// Written via temp + atomic rename. WriteAllText truncates then writes, and the whole point of
        /// this file is an external poller reading it in a loop — that reader would eventually catch a torn
        /// or empty file and have to guess whether it meant anything.</summary>
        /// <summary>The status record itself — pure, so the invariants a Bash-side reader depends on
        /// (identity present, verdict in <c>phase</c>, ids redacted) are testable without a live batch.</summary>
        internal static string BuildStatusJson(string batchId, string phase, string message,
                                               int index, int total, string handle, string utc)
        {
            var sb = new StringBuilder();
            sb.Append("{\"batchId\":").Append(TransplantCore.Q(batchId));
            sb.Append(",\"phase\":\"").Append(phase).Append("\"");
            sb.Append(",\"message\":").Append(TransplantCore.Q(UploadAvatarLogic.RedactIds(message)));
            sb.Append(",\"total\":").Append(total.ToString(CultureInfo.InvariantCulture));
            if (index >= 0) sb.Append(",\"index\":").Append(index.ToString(CultureInfo.InvariantCulture));
            if (handle != null) sb.Append(",\"handle\":").Append(TransplantCore.Q(handle));
            sb.Append(",\"utc\":").Append(TransplantCore.Q(utc)).Append("}");
            return sb.ToString();
        }

        static bool WriteStatus(string phase, string message, int index, string handle, out string why)
        {
            why = null;
            try
            {
                Directory.CreateDirectory(TransplantCore.RunLogDir);
                var json = BuildStatusJson(_batchId, phase, message, index, _batchTotal, handle,
                                           DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                var tmp = StatusPath + ".tmp";
                File.WriteAllText(tmp, json);
                // Replace, not Delete+Move: this runtime's File.Move has no overwrite overload, and
                // delete-then-move leaves a window in which the poller reads "no file at all".
                // File.Replace maps to ReplaceFile (atomic on NTFS); it requires an existing target,
                // so the very first write is a plain Move.
                if (File.Exists(StatusPath)) File.Replace(tmp, StatusPath, null);
                else File.Move(tmp, StatusPath);
                return true;
            }
            catch (Exception e)
            {
                why = e.Message;
                Debug.LogWarning("[upload-avatar] status write failed: " + e.Message);
                return false;
            }
        }

        /// <summary>An orphaned "running" record — the domain reloaded (script recompile, play-mode entry)
        /// mid-batch, which kills <c>_inflight</c> and the <see cref="Watch"/> registration together but
        /// leaves the file asserting a batch is still in flight, forever. Nothing else would ever correct
        /// it, and a reader cannot distinguish it from a live batch.</summary>
        [InitializeOnLoadMethod]
        static void MarkOrphanedBatchInterrupted()
        {
            try
            {
                if (!File.Exists(StatusPath)) return;
                var txt = File.ReadAllText(StatusPath);
                if (txt.IndexOf("\"phase\":\"running\"", StringComparison.Ordinal) < 0 &&
                    txt.IndexOf("\"phase\":\"started\"", StringComparison.Ordinal) < 0) return;
                File.WriteAllText(StatusPath, txt.TrimEnd().TrimEnd('}') +
                    ",\"interrupted\":\"the editor domain reloaded while this batch was in flight; " +
                    "its outcome is UNKNOWN — check the RunLog and the avatar's blueprintId before re-running\"}");
            }
            catch { /* best-effort: a startup hook must never throw */ }
        }

        /// <summary>Poll the in-flight execute batch. The upload is async-driven — Run returns immediately and
        /// the editor's update loop pumps CAU's continuations; the agent polls this until it stops reporting
        /// "running". The RunLog is written (and the summary memoized) exactly once at the terminal state, so
        /// repeated polling is idempotent — including the FAULTED path, which also gets a RunLog.</summary>
        public static string Status()
        {
            if (_inflight == null) return "[upload-avatar] no batch started";
            if (!_inflight.IsCompleted) return "[upload-avatar] running…";
            if (_terminalSummary != null) return _terminalSummary;

            if (_inflight.IsFaulted)
            {
                var ex = _inflight.Exception != null ? _inflight.Exception.GetBaseException() : null;
                var redacted = UploadAvatarLogic.RedactIds(ex != null ? ex.Message : "unknown");
                var fault = new UploadReport { result = "FAIL" };
                fault.rows.Add(new Row { handle = "(batch)", state = "unknown", result = "failed",
                                         cls = "real", error = redacted });
                string faultLog = WriteRunLog(fault, false, "all");
                _terminalSummary = "[upload-avatar] batch FAULTED: " + redacted + " | log=" + faultLog;
                WriteStatus("faulted", _terminalSummary, -1, null, out _);
                return _terminalSummary;
            }

            var report = _inflight.Result;
            _terminalSummary = BuildSummary(report, false, WriteRunLog(report, false, "all"));
            // The verdict goes in `phase`, not only in the human-readable message: this file exists to be
            // machine-read from Bash during a wedge, and a bare "done" would make a FAILED batch look
            // finished-and-fine to anything that keys on phase.
            WriteStatus(report.result == "PASS" ? "done-pass" : "done-fail", _terminalSummary, -1, null, out _);
            return _terminalSummary;
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
        /// Async-driven (Task-8 confirmed the sync block deadlocked): the returned Task is fire-and-forget
        /// from <see cref="Run"/> and the editor update loop pumps CAU's continuations; poll <see cref="Status"/>.</summary>
        static async System.Threading.Tasks.Task<UploadOutcome> RealUploadOne(GameObject go, object builder)
        {
            // Ledger key is the instance id (names collide across avatars); go.name stays in messages/rows.
            string key = go.GetInstanceID().ToString();

            // Deterministic LOCAL pre-checks: always "real" (never retryable) and they do NOT burn a ledger
            // slot — the ceiling exists to stop hammering a failing UPLOAD, not to cap local misconfig.
            var desc = go.GetComponent<VRCAvatarDescriptor>();
            if (desc == null)
                return UploadOutcome.Failed(message: "no VRCAvatarDescriptor on '" + go.name + "'", forcedClass: "real");
            if (!CauReflect.TryBuildSetting(desc, out var setting, out var why))
                return UploadOutcome.Failed(message: why, forcedClass: "real");
            if (!_ledger.MayAttempt(key))
                return UploadOutcome.Failed(message:
                    "attempt ceiling (" + UploadAvatarLogic.AttemptLedger.MaxAttempts +
                    ") reached for '" + go.name + "'", forcedClass: "real");

            _ledger.Record(key);
            bool wasFirstUpload = ClassifyAvatar(go).state == "first-upload";

            try
            {
                // Async-driven: the editor update loop pumps CAU's continuations (blocking the main
                // thread here deadlocks — CAU's continuations need it). See Run/Status for the drive.
                // A faulted upload throws the real SDK exception → classified below (not swallowed).
                await CauReflect.UploadOne(setting, builder, CancellationToken.None);

                AssetDatabase.SaveAssets();
                if (wasFirstUpload && ClassifyAvatar(go).state == "first-upload")
                    return UploadOutcome.ReservedNoBundle();
                _ledger.Clear(key); // success resets the ceiling — it counts only consecutive failures
                return UploadOutcome.Uploaded();
            }
            catch (Exception e)
            {
                return FailedFromException(e);
            }
        }

        /// <summary>Map a thrown upload exception to a classified <see cref="UploadOutcome.Failed"/>: unwrap
        /// one layer of wrapping, pull the HTTP status, flag validation (by type name) / timeout. This is the
        /// path that keeps 429/validation from being mislabeled transient — kept internal so it is unit-tested
        /// against a fake exception without the live SDK.</summary>
        internal static UploadOutcome FailedFromException(Exception e)
        {
            // e is already normalized by CauReflect.UploadOne (no TargetInvocationException / AggregateException
            // wrapper). The status may sit on the outer SDK exception OR an inner transport cause, so walk the
            // chain and classify from the first link that exposes a signal — do NOT blindly take one inner layer.
            for (var cur = e; cur != null; cur = cur.InnerException)
            {
                int? status = ExtractHttpStatus(cur);
                bool isValidation = cur.GetType().Name.IndexOf("Validation", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isTimeout = cur is TimeoutException;
                if (status.HasValue || isValidation || isTimeout)
                    return UploadOutcome.Failed(httpStatus: status, isValidation: isValidation,
                                                isTimeout: isTimeout, message: e.Message);
            }
            // No classifiable signal anywhere in the chain → fail-safe: non-retryable. Never auto-retry an
            // unknown failure against a real account (covers CAU-drift InvalidOperationException, status-less
            // transport faults, a null/unexpected return). Requires an operator decision, not a silent retry.
            return UploadOutcome.Failed(message: e.Message, forcedClass: "real");
        }

        /// <summary>Best-effort HTTP status off an SDK exception: an int / HttpStatusCode property named
        /// <c>StatusCode</c> or <c>Status</c>, else null. The exact SDK exception shape is a Task-8
        /// live-validation item — this is a reflective placeholder, not a proven mapping.</summary>
        internal static int? ExtractHttpStatus(Exception e)
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

        // Scrub at the choke point: TryGetBuilder/TryBuildSetting embed SDK exception .Message into reason.
        static string Refuse(string reason)
            => "[upload-avatar] all => REFUSE error=" + UploadAvatarLogic.RedactIds(reason);

        // ── Classification / preconditions ──────────────────────────────────────────────────────

        /// <summary>Read-only per-avatar preflight read: (blueprint-state, name-that-would-publish). Reads
        /// PipelineManager.blueprintId via SerializedObject and never surfaces the id itself. No PipelineManager
        /// → treated as first-upload (its absence means the avatar was never uploaded). Mutates nothing.</summary>
        public static (string state, string publishName) ClassifyAvatar(GameObject go)
        {
            if (go == null) return ("unknown", "(null)");
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
                "[upload-avatar]{0} all: uploaded={1} reserved={2} failed={3} not-attempted={4} (transient={5} rate-limit={6} real={7}) => {8} | log={9}",
                marker, c["uploaded"], c["reserved"], c["failed"], c["not-attempted"],
                c["transient"], c["rate-limit"], c["real"], report.result, logPath);
            if (report.result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        static Dictionary<string, int> Counts(UploadReport report)
        {
            var c = new Dictionary<string, int>
            {
                ["uploaded"] = 0, ["reserved"] = 0, ["failed"] = 0, ["not-attempted"] = 0,
                ["transient"] = 0, ["rate-limit"] = 0, ["real"] = 0,
            };
            foreach (var r in report.rows)
            {
                if (r.result == "uploaded") c["uploaded"]++;
                else if (r.result == "reserved-no-bundle") c["reserved"]++;
                else if (r.result == "failed") c["failed"]++;
                else if (r.result == "not-attempted") c["not-attempted"]++;
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
            sb.Append("  \"not-attempted\": ").Append(c["not-attempted"]).Append(",\n");
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
