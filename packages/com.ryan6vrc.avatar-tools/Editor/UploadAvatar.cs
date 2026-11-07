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
    /// Contract: docs/unity-tools.md §Publish.
    ///
    /// <see cref="RunCore"/> is the loop, pure over an injected per-avatar delegate. The real adapter,
    /// <see cref="RealUploadOne"/>, owns the two decisions that need live SDK state: the id-persistence
    /// verify and the per-handle attempt ceiling.
    /// </summary>
    [AgentTool]
    public static class UploadAvatar
    {
        // ── Outcome / report types ────────────────────────────────────────────────────────────

        /// <summary>The verdict the per-avatar delegate hands back to <see cref="RunCore"/>.</summary>
        public struct UploadOutcome
        {
            internal enum Kind { Uploaded, ReservedNoBundle, Failed }
            internal Kind kind;
            internal int? httpStatus;
            internal bool isValidation;
            internal bool isTimeout;
            internal string message;
            // Bypasses Classify, for a failure that must never read as retryable.
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

        /// <summary>One RunLog row. No <c>blueprintId</c> field by construction, so the id cannot enter
        /// output. <c>cls</c> is the failure class (transient|rate-limit|real), set only on a failed row.</summary>
        public struct Row { public string handle, state, result, cls, error; }

        public sealed class UploadReport
        {
            public List<Row> rows = new List<Row>();
            public string result; // PASS / FAIL / REFUSE
        }

        // Static so re-invoking Run stays capped across calls.
        static readonly UploadAvatarLogic.AttemptLedger _ledger = new UploadAvatarLogic.AttemptLedger();

        static System.Threading.Tasks.Task<UploadReport> _inflight;
        static string _terminalSummary;
        // The terminal record while it has not reached disk: a reader holding the file can refuse the write.
        static PendingWrite _pendingTerminal;
        // The in-flight whatIf. Its live reads progress only on the editor update loop, so it is kicked and
        // polled like the batch.
        static System.Threading.Tasks.Task<string> _preview;
        static bool _lastWasPreview;

        /// <summary>This batch's progress on disk, readable from Bash when <see cref="Status"/> cannot run:
        /// a modal raised mid-upload (VRCFury's, the SDK's) blocks the main thread, and the MCP poll with it.
        ///
        /// The path is fixed, so match the record's <c>batchId</c> to the one <see cref="Run"/> returned. If
        /// the editor is already wedged when Run is issued, the command never drains, and a caller falling
        /// back to the file reads the previous batch's verdict for an upload that never started.</summary>
        public static string StatusPath => TransplantCore.RunLogDir + "/upload-avatar_status.json";

        static string _batchId;
        static int _batchTotal;

        // ── The loop ────────────────────────────────────────────────────────────────────────────

        /// <summary>One pass over <paramref name="avatars"/>, stopping at the first row that does not upload.
        /// The tool never auto-retries: a single pass is the whole contract.</summary>
        public static async System.Threading.Tasks.Task<UploadReport> RunCore(
            GameObject[] avatars,
            Func<GameObject, System.Threading.Tasks.Task<UploadOutcome>> uploadOne,
            Action saveAssets,
            Action<int, string> onProgress = null,
            Func<int, string> stateOf = null)
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

                // The preflight's state when a batch ran one: it read the live record, which the local
                // id alone cannot stand in for.
                string state = stateOf != null ? stateOf(i) : ClassifyAvatar(go).state;
                // Announce before awaiting: this upload can raise a modal, after which nothing else in this
                // process runs, so the status file must already name the avatar.
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

        /// <summary>On a halt, a <c>not-attempted</c> row for every remaining avatar: the upload-avatar skill
        /// re-feeds the failed and not-attempted tail.</summary>
        static void AppendNotAttempted(UploadReport report, GameObject[] avatars, int startIndex)
        {
            for (int j = startIndex; j < avatars.Length; j++)
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

            if (_preview != null && !_preview.IsCompleted)
                return Refuse("a whatIf is still reading live records — poll UploadAvatar.Status()");

            if (whatIf)
            {
                if (_inflight != null && !_inflight.IsCompleted)
                    return Refuse("an upload batch is in flight — poll UploadAvatar.Status()");
                if (_inflight != null) BatchStatus(); // finalize before Status() starts answering for the whatIf
                int n = avatars != null ? avatars.Length : 0;
                _preview = WhatIfCore(avatars, LiveRead, CurrentUserId(), go =>
                {
                    int offenders = ConformImportSettings.CountOffenders(go, out int unscanned);
                    return (offenders, unscanned);
                });
                _lastWasPreview = true;
                // A batch of first uploads fetches nothing and is already complete.
                return _preview.IsCompleted
                    ? Status()
                    : "[upload-avatar] (whatIf) reading " + n + " live record(s); poll UploadAvatar.Status()";
            }

            // Fire-and-forget: the editor update loop pumps CAU's continuations, and blocking here would
            // deadlock the main thread.
            if (_inflight != null && !_inflight.IsCompleted)
                return Refuse("an upload batch is already in flight — poll UploadAvatar.Status()");
            // Watch may not have ticked yet, and overwriting an unfinalized _inflight would drop its RunLog.
            if (_inflight != null) BatchStatus();
            if (!CauReflect.TryGetBuilder(out var builder, out var whyBuilder))
                return Refuse(whyBuilder);
            _terminalSummary = null;
            StopTerminalRetry();
            _batchTotal = avatars != null ? avatars.Length : 0;
            _batchId = Guid.NewGuid().ToString("N");

            if (!WriteStatus("started", "batch started (" + _batchTotal + " avatar(s))", -1, null, out var whyStatus))
            {
                _batchId = null;
                return Refuse("cannot write the status file (" + whyStatus + ") — refusing to start: a " +
                              "stale record would read as this batch's result. Path: " + StatusPath);
            }

            _lastWasPreview = false;
            _inflight = RunBatch(avatars, LiveRead, CurrentUserId(),
                                 go => RealUploadOne(go, builder), () => AssetDatabase.SaveAssets(),
                                 (i, name) => WriteStatus("running", "uploading", i, name, out _));
            EditorApplication.update -= Watch;
            EditorApplication.update += Watch;
            return "[upload-avatar] batch started (" + _batchTotal + " avatar(s)) batchId=" + _batchId +
                   "; poll UploadAvatar.Status() until it stops answering \"running…\". When the editor is " +
                   "wedged, read " + StatusPath + " instead: match its batchId to the one above or you are " +
                   "reading a previous batch, and " + DoneTest;
        }

        /// <summary>What a reader of the status file keys on: the record's own fields, stated once.</summary>
        internal const string DoneTest =
            "the batch is finished when \"done\":true (phase done-pass, done-fail, faulted or interrupted) and " +
            "still in flight while \"running\":true (phase started or running)";

        static readonly string[] TerminalPhases = { "done-pass", "done-fail", "faulted", "interrupted" };

        internal static bool IsTerminalPhase(string phase) => Array.IndexOf(TerminalPhases, phase) >= 0;

        /// <summary>Finalize the batch the moment it completes, so the RunLog and the status file land even
        /// when MCP is unreachable and no poll arrives.</summary>
        static void Watch()
        {
            if (_inflight == null || !_inflight.IsCompleted) return;
            EditorApplication.update -= Watch;
            try { BatchStatus(); }               // idempotent: writes the RunLog + memoizes, exactly once
            catch (Exception e) { Debug.LogError("[upload-avatar] terminal write failed: " + e.Message); }
        }

        /// <summary>The status record, pure so the invariants a Bash-side reader depends on (identity present,
        /// verdict in <c>phase</c>, ids redacted) are testable without a live batch.</summary>
        internal static string BuildStatusJson(string batchId, string phase, string message,
                                               int index, int total, string handle, string utc)
        {
            var sb = new StringBuilder();
            sb.Append("{\"batchId\":").Append(TransplantCore.Q(batchId));
            sb.Append(",\"phase\":\"").Append(phase).Append("\"");
            bool done = IsTerminalPhase(phase);
            sb.Append(",\"running\":").Append(done ? "false" : "true");
            sb.Append(",\"done\":").Append(done ? "true" : "false");
            sb.Append(",\"message\":").Append(TransplantCore.Q(UploadAvatarLogic.RedactIds(message)));
            sb.Append(",\"total\":").Append(total.ToString(CultureInfo.InvariantCulture));
            if (index >= 0) sb.Append(",\"index\":").Append(index.ToString(CultureInfo.InvariantCulture));
            if (handle != null) sb.Append(",\"handle\":").Append(TransplantCore.Q(handle));
            sb.Append(",\"utc\":").Append(TransplantCore.Q(utc)).Append("}");
            return sb.ToString();
        }

        /// <summary>Returns false with <paramref name="why"/> on failure and never throws into the upload. Only
        /// the "started" write treats a failure as fatal (see <see cref="Run"/>): a later one leaves a
        /// correctly-identified record that is merely behind.
        ///
        /// Temp + rename, because WriteAllText truncates first and a poller reading in a loop would eventually
        /// catch a torn or empty file.</summary>
        static bool WriteStatus(string phase, string message, int index, string handle, out string why)
        {
            var json = BuildStatusJson(_batchId, phase, message, index, _batchTotal, handle,
                                       DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            if (WriteStatusFile(StatusPath, json, out why)) return true;
            Debug.LogWarning("[upload-avatar] status write failed: " + why);
            return false;
        }

        /// <summary>Temp + replace, falling back to an in-place overwrite. A reader that opened the file without
        /// delete sharing (Python's <c>open</c>, PowerShell's <c>Get-Content</c>) makes the replace fail for as
        /// long as it holds the handle, but it shares write, so the overwrite lands.</summary>
        internal static bool WriteStatusFile(string path, string json, out string why)
        {
            why = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                try
                {
                    // Replace, not Delete+Move: this runtime's File.Move has no overwrite overload, and
                    // delete-then-move leaves a window in which the poller reads "no file at all".
                    // File.Replace maps to ReplaceFile (atomic on NTFS); it requires an existing target,
                    // so the very first write is a plain Move.
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                }
                catch (Exception)
                {
                    File.WriteAllText(path, json);
                    File.Delete(tmp);
                }
                return true;
            }
            catch (Exception e)
            {
                why = e.Message;
                return false;
            }
        }

        /// <summary>One status record still owed to disk; <see cref="Attempt"/> is retried until it lands.</summary>
        internal sealed class PendingWrite
        {
            readonly string _path, _json;
            internal string why;
            internal PendingWrite(string path, string json) { _path = path; _json = json; }
            internal bool Attempt() => WriteStatusFile(_path, _json, out why);
        }

        const double TerminalRetrySeconds = 60;
        static double _terminalRetryUntil;

        /// <summary>The terminal record is the one a waiter ends on, so a refused write is retried on later
        /// editor ticks rather than dropped, and named by <see cref="Status"/> until it lands.</summary>
        static void WriteTerminalStatus(string phase, string message)
        {
            StopTerminalRetry();
            var pending = new PendingWrite(StatusPath, BuildStatusJson(_batchId, phase, message, -1, _batchTotal, null,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
            if (pending.Attempt()) return;
            _pendingTerminal = pending;
            _terminalRetryUntil = EditorApplication.timeSinceStartup + TerminalRetrySeconds;
            EditorApplication.update += RetryTerminalStatus;
        }

        static void RetryTerminalStatus()
        {
            if (_pendingTerminal == null || _pendingTerminal.Attempt()) { StopTerminalRetry(); return; }
            if (EditorApplication.timeSinceStartup < _terminalRetryUntil) return;
            EditorApplication.update -= RetryTerminalStatus;
            Debug.LogError("[upload-avatar] the status file still reads in flight: its terminal write failed for " +
                           TerminalRetrySeconds + " s (" + _pendingTerminal.why + "). Status() has the verdict.");
        }

        static void StopTerminalRetry()
        {
            EditorApplication.update -= RetryTerminalStatus;
            _pendingTerminal = null;
        }

        /// <summary>The verdict, plus a warning while the status file has not caught up with it.</summary>
        static string TerminalAnswer()
            => _pendingTerminal == null ? _terminalSummary
               : _terminalSummary + " | the status file at " + StatusPath + " was NOT updated to this verdict (" +
                 _pendingTerminal.why + "); trust this line, not the file";

        /// <summary>A domain reload mid-batch (script recompile, play-mode entry) kills <c>_inflight</c> and the
        /// <see cref="Watch"/> registration together but leaves the file asserting a batch in flight, forever.
        /// Nothing else corrects that record, and a reader cannot distinguish it from a live batch.</summary>
        [InitializeOnLoadMethod]
        static void MarkOrphanedBatchInterrupted()
        {
            try
            {
                if (!File.Exists(StatusPath)) return;
                var marked = MarkInterrupted(File.ReadAllText(StatusPath));
                if (marked != null) WriteStatusFile(StatusPath, marked, out _);
            }
            catch { /* best-effort: a startup hook must never throw */ }
        }

        /// <summary>An in-flight record rewritten as the terminal <c>interrupted</c> phase, keeping its batch and
        /// avatar, or null when the record is already terminal or unreadable.</summary>
        internal static string MarkInterrupted(string json)
        {
            var r = new StatusFields();
            try { JsonUtility.FromJsonOverwrite(json, r); } catch { return null; }
            if (r.phase != "running" && r.phase != "started") return null;
            return BuildStatusJson(r.batchId, "interrupted",
                "the editor domain reloaded while this batch was in flight; its outcome is UNKNOWN — check the " +
                "RunLog and the avatar's blueprintId before re-running", r.index, r.total, r.handle,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        [Serializable]
        class StatusFields { public string batchId, phase, handle; public int total; public int index = -1; }

        /// <summary>Poll the in-flight batch; repeated polling is idempotent. After a <c>whatIf</c> it answers
        /// for that read instead, until the next batch starts.</summary>
        public static string Status() => _lastWasPreview ? PreviewStatus() : BatchStatus();

        static string PreviewStatus()
        {
            if (!_preview.IsCompleted) return "[upload-avatar] (whatIf) reading live records…";
            if (_preview.IsFaulted || _preview.IsCanceled)
            {
                var ex = _preview.Exception != null ? _preview.Exception.GetBaseException() : null;
                return "[upload-avatar] (whatIf) all => FAIL error=" +
                       UploadAvatarLogic.RedactIds(ex != null ? ex.Message : "the read was canceled");
            }
            return _preview.Result;
        }

        static string BatchStatus()
        {
            if (_inflight == null)
                return "[upload-avatar] no batch or whatIf in flight in this editor domain — a domain reload " +
                       "drops one mid-read, so re-run a whatIf you were polling";
            if (!_inflight.IsCompleted) return "[upload-avatar] running… batchId=" + _batchId + " (not finished)";
            if (_terminalSummary != null) return TerminalAnswer();

            if (_inflight.IsFaulted)
            {
                var ex = _inflight.Exception != null ? _inflight.Exception.GetBaseException() : null;
                var redacted = UploadAvatarLogic.RedactIds(ex != null ? ex.Message : "unknown");
                var fault = new UploadReport { result = "FAIL" };
                fault.rows.Add(new Row { handle = "(batch)", state = "unknown", result = "failed",
                                         cls = "real", error = redacted });
                string faultLog = TryWriteRunLog(fault);
                _terminalSummary = "[upload-avatar] batch FAULTED: " + redacted + " | log=" + faultLog;
                WriteTerminalStatus("faulted", _terminalSummary);
                return TerminalAnswer();
            }

            var report = _inflight.Result;
            _terminalSummary = BuildSummary(report, false, TryWriteRunLog(report));
            WriteTerminalStatus(report.result == "PASS" ? "done-pass" : "done-fail", _terminalSummary);
            return TerminalAnswer();
        }

        // A RunLog failure must not cost the terminal status write that follows it.
        static string TryWriteRunLog(UploadReport report)
        {
            try { return WriteRunLog(report, false, "all"); }
            catch (Exception e) { return "(not written: " + e.Message + ")"; }
        }

        /// <summary>The real per-avatar upload. CAU writes a first upload's new blueprintId into the
        /// PipelineManager and <c>SaveAssets()</c> persists it, so re-reading the state afterwards tells
        /// Uploaded from ReservedNoBundle (<see cref="IsReservedNoBundle"/>).</summary>
        static async System.Threading.Tasks.Task<UploadOutcome> RealUploadOne(GameObject go, object builder)
        {
            // Ledger key is the instance id (names collide across avatars); go.name stays in messages/rows.
            string key = go.GetInstanceID().ToString();

            // Local pre-checks burn no ledger slot: the ceiling stops hammering a failing upload, not local
            // misconfiguration.
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
            bool wasFirstUpload = ClassifyAvatar(go).state == UploadAvatarLogic.ClassifyBlueprint(null);

            try
            {
                await CauReflect.UploadOne(setting, builder, CancellationToken.None);

                AssetDatabase.SaveAssets();
                string stateAfter = wasFirstUpload ? ClassifyAvatar(go).state : null;
                if (IsReservedNoBundle(wasFirstUpload, stateAfter))
                    return UploadOutcome.ReservedNoBundle();
                _ledger.Clear(key); // success resets the ceiling — it counts only consecutive failures
                return UploadOutcome.Uploaded();
            }
            catch (Exception e)
            {
                return FailedFromException(e);
            }
        }

        /// <summary>An avatar that was a first-upload going in and is still one after <c>SaveAssets()</c> means
        /// VRChat reserved the record but no bundle landed. Any other cell is an ordinary success, including
        /// an update that now reads first-upload: the id went missing locally after an upload that did land,
        /// which must not halt the batch as a reservation. <paramref name="stateAfterUpload"/> takes any of
        /// ClassifyAvatar's three states, including <c>"unknown"</c> for a GameObject destroyed across the
        /// await, so the test is membership in first-upload, never non-membership in update.</summary>
        internal static bool IsReservedNoBundle(bool wasFirstUpload, string stateAfterUpload)
            => wasFirstUpload && stateAfterUpload == UploadAvatarLogic.ClassifyBlueprint(null);

        /// <summary>Map a thrown upload exception to a classified <see cref="UploadOutcome.Failed"/>, from the
        /// first link of its chain that exposes a status, a validation type name or a timeout. A status-bearing
        /// link reports <see cref="UploadRefusal"/>'s text, not its <c>Message</c>: see
        /// <c>AvatarRecordLogic.RefuseForStatus</c>.</summary>
        internal static UploadOutcome FailedFromException(Exception e)
        {
            // The status may sit on the outer SDK exception or an inner transport cause.
            for (var cur = e; cur != null; cur = cur.InnerException)
            {
                bool hasStatus = VrcApiReflect.TryReadApiFields(cur, out var status, out var serverMessage);
                bool isValidation = cur.GetType().Name.IndexOf("Validation", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isTimeout = cur is TimeoutException;
                if (hasStatus || isValidation || isTimeout)
                    return UploadOutcome.Failed(httpStatus: status, isValidation: isValidation, isTimeout: isTimeout,
                                                message: hasStatus ? UploadRefusal(status, serverMessage) : e.Message);
            }
            // No classifiable signal anywhere in the chain: an unknown failure (CAU drift, a status-less
            // transport fault) is never retryable against a live account.
            return UploadOutcome.Failed(message: e.Message, forcedClass: "real");
        }

        // The record doors' text, except 422: theirs tells the caller to change a name, description or tag, and an
        // upload's 422 can be a rejection of the avatar itself, where no text is at fault.
        static string UploadRefusal(int? status, string serverMessage)
            => status == 422
                ? "VRChat rejected the upload as unprocessable (422)"
                  + (string.IsNullOrEmpty(serverMessage) ? "" : " — server said: " + AvatarRecordLogic.Escape(serverMessage))
                : AvatarRecordLogic.RefuseForStatus(status, serverMessage);

        // Scrub at the choke point: TryGetBuilder/TryBuildSetting embed SDK exception .Message into reason.
        static string Refuse(string reason)
            => "[upload-avatar] all => REFUSE error=" + UploadAvatarLogic.RedactIds(reason);

        // ── Classification / preconditions ──────────────────────────────────────────────────────

        /// <summary>The local blueprint state and the scene name. A first upload publishes under the scene name;
        /// an update publishes under the live record's name, which only the preflight reads.</summary>
        public static (string state, string sceneName) ClassifyAvatar(GameObject go)
        {
            if (go == null) return ("unknown", "(null)");
            return (UploadAvatarLogic.ClassifyBlueprint(LocalBlueprintId(go)), go.name);
        }

        // Never surfaced.
        static string LocalBlueprintId(GameObject go)
        {
            var pm = go.GetComponent<PipelineManager>();
            if (pm == null) return null;
            var prop = new SerializedObject(pm).FindProperty("blueprintId");
            return prop != null ? prop.stringValue : pm.blueprintId;
        }

        // ── Live-record preflight ───────────────────────────────────────────────────────────────

        internal static async System.Threading.Tasks.Task<UploadAvatarLogic.RowSight[]> Preflight(
            GameObject[] avatars,
            Func<string, System.Threading.Tasks.Task<UploadAvatarLogic.RecordRead>> read,
            string currentUserId)
        {
            int n = avatars != null ? avatars.Length : 0;
            var sights = new UploadAvatarLogic.RowSight[n];
            for (int i = 0; i < n; i++)
            {
                var go = avatars[i];
                if (go == null)
                {
                    sights[i] = new UploadAvatarLogic.RowSight { handle = "(null)", error = "null avatar entry",
                                                                 state = UploadAvatarLogic.StateUnknown };
                    continue;
                }
                // Read the name BEFORE the await: the object can be destroyed across it.
                string handle = go.name, id = LocalBlueprintId(go);
                bool hasId = !string.IsNullOrEmpty(id);
                var record = hasId ? await read(id) : default;
                sights[i] = UploadAvatarLogic.Sight(handle, hasId, record, currentUserId);
            }
            return sights;
        }

        internal static UploadReport BlockedReport(UploadAvatarLogic.RowSight[] sights)
        {
            bool any = false;
            foreach (var s in sights) any |= s.Blocks;
            if (!any) return null;

            var report = new UploadReport { result = "FAIL" };
            foreach (var s in sights)
                report.rows.Add(s.Blocks
                    ? new Row { handle = s.handle, state = s.state, result = "failed",
                                // No status is no classifiable signal, which is never retryable here.
                                cls = s.readFailed && s.status.HasValue
                                    ? UploadAvatarLogic.Classify(s.status, false, false) : "real",
                                error = UploadAvatarLogic.RedactIds(s.error) }
                    : new Row { handle = s.handle, state = s.state, result = "not-attempted" });
            return report;
        }

        internal static async System.Threading.Tasks.Task<UploadReport> RunBatch(
            GameObject[] avatars,
            Func<string, System.Threading.Tasks.Task<UploadAvatarLogic.RecordRead>> read,
            string currentUserId,
            Func<GameObject, System.Threading.Tasks.Task<UploadOutcome>> uploadOne,
            Action saveAssets,
            Action<int, string> onProgress = null)
        {
            var sights = await Preflight(avatars, read, currentUserId);
            var blocked = BlockedReport(sights);
            if (blocked != null) return blocked;
            return await RunCore(avatars, uploadOne, saveAssets, onProgress, i => sights[i].state);
        }

        internal static async System.Threading.Tasks.Task<string> WhatIfCore(
            GameObject[] avatars,
            Func<string, System.Threading.Tasks.Task<UploadAvatarLogic.RecordRead>> read,
            string currentUserId,
            Func<GameObject, (int offenders, int unscanned)> importScan)
        {
            int n = avatars != null ? avatars.Length : 0;
            int[] offenders = new int[n], unscanned = new int[n];
            // Before the first await, while every object is known to be alive.
            for (int i = 0; i < n; i++)
                (offenders[i], unscanned[i]) = avatars[i] != null ? importScan(avatars[i]) : (-1, 0);
            var sights = await Preflight(avatars, read, currentUserId);
            return UploadAvatarLogic.WhatIfSummary(sights, offenders, unscanned);
        }

        const int LiveReadTimeoutMs = 30000;

        static string CurrentUserId() => APIUser.CurrentUser != null ? APIUser.CurrentUser.id : null;

        static async System.Threading.Tasks.Task<UploadAvatarLogic.RecordRead> LiveRead(string id)
        {
            if (!VrcApiReflect.TryGetAvatar(id, out var raw, out var why))
                return UploadAvatarLogic.RecordRead.Failed(null, why);
            var task = (System.Threading.Tasks.Task)raw;
            // Bounded: the SDK's read takes no timeout, and one that never completed would hold the
            // in-flight slot, refusing every later Run until a domain reload.
            await System.Threading.Tasks.Task.WhenAny(task, System.Threading.Tasks.Task.Delay(LiveReadTimeoutMs));
            if (!task.IsCompleted)
                return UploadAvatarLogic.RecordRead.Failed(null, "no answer from the VRChat API in " +
                                                                 (LiveReadTimeoutMs / 1000) + "s");
            if (task.IsCanceled) return UploadAvatarLogic.RecordRead.Failed(null, "the read was canceled");
            if (task.IsFaulted)
            {
                int? status = VrcApiReflect.FaultStatus(raw);
                return status == 404
                    ? UploadAvatarLogic.RecordRead.NotFound()
                    : UploadAvatarLogic.RecordRead.Failed(status, VrcApiReflect.DescribeFault(raw));
            }
            var record = VrcApiReflect.Result(raw);
            if (string.IsNullOrEmpty(VrcApiReflect.GetString(record, "ID")))
                return UploadAvatarLogic.RecordRead.NotFound();
            var pending = record.GetType().GetProperty("PendingUpload");
            return UploadAvatarLogic.RecordRead.Found(
                VrcApiReflect.GetName(record), VrcApiReflect.GetString(record, "AuthorId"),
                pending != null && pending.GetValue(record) is bool b && b);
        }

        /// <summary>The upload REFUSE register: the first unmet precondition with its named fix, or null when
        /// the environment is upload-ready. The login check self-heals (<see cref="UploadAvatarLogic.LoginLatch"/>).
        /// Internal so the two-call self-heal sequence has a test seam.</summary>
        internal static string CheckPreconditions()
        {
            if (EditorApplication.isPlaying)
                return "in Play mode — exit Play mode before uploading";
            // Above the login: no login can fix this, and the restore costs a network round-trip and a re-run.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                return "build target is " + EditorUserBuildSettings.activeBuildTarget +
                       "; switch to Windows (StandaloneWindows64)";
            var notLoggedIn = CauReflect.LoginLatch.Evaluate();
            if (notLoggedIn != null)
                return notLoggedIn;
            if (!CauReflect.TryGetBuilder(out _, out var why))
                return why;
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
                  .Append(", \"error\": ").Append(TransplantCore.Q(r.error))
                  .Append(" }");
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
