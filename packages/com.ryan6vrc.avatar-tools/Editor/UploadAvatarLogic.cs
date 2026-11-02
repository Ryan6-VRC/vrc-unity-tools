using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>VRC-free decision helpers for UploadAvatar — unit-tested, no editor/SDK deps.</summary>
    internal static class UploadAvatarLogic
    {
        static readonly Regex Url = new Regex(@"https?://\S+", RegexOptions.Compiled);
        static readonly Regex Avtr = new Regex(@"avtr_[A-Za-z0-9\-]+", RegexOptions.Compiled);
        static readonly Regex Usr  = new Regex(@"usr_[A-Za-z0-9\-]+",  RegexOptions.Compiled);

        /// <summary>Scrub avatar/user IDs and URLs from any string before it enters output or a RunLog
        /// (public-repo hygiene — forwarded SDK error strings routinely embed these).</summary>
        internal static string RedactIds(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Url.Replace(s, "<redacted-url>");
            s = Avtr.Replace(s, "<redacted-id>");
            s = Usr.Replace(s, "<redacted-id>");
            return s;
        }

        internal static string Classify(int? httpStatus, bool isValidationException, bool isTimeout)
        {
            if (isValidationException) return "real";
            if (httpStatus == 429) return "rate-limit";
            if (isTimeout) return "transient";
            if (httpStatus.HasValue && httpStatus >= 500) return "transient";
            if (httpStatus.HasValue && httpStatus >= 400) return "real";
            return "transient";
        }

        internal static string ClassifyBlueprint(string blueprintId)
            => string.IsNullOrEmpty(blueprintId) ? "first-upload" : "update";

        // ── Live-record preflight ───────────────────────────────────────────────────────────────

        internal const string StateFirstUpload = "first-upload";
        internal const string StateUpdate = "update";
        internal const string StatePendingUpload = "pending-upload";
        internal const string StateNotVisible = "not-visible";
        internal const string StateUnknown = "unknown";

        /// <summary>What one read of a row's live record came back with.</summary>
        internal struct RecordRead
        {
            internal bool found, pendingUpload;
            internal int? status;
            internal string name, authorId, error;

            internal static RecordRead Found(string name, string authorId, bool pendingUpload)
                => new RecordRead { found = true, name = name, authorId = authorId, pendingUpload = pendingUpload };
            internal static RecordRead NotFound() => new RecordRead { status = 404 };
            internal static RecordRead Failed(int? status, string error)
                => new RecordRead { status = status, error = error ?? "no reason reported" };
        }

        /// <summary>One row's preflight verdict: what the upload would do with it, and the name it would
        /// publish under. A non-null <c>error</c> means the batch must not start.</summary>
        internal struct RowSight
        {
            internal string handle, state, publishName, error;
            // Set only when the read itself failed, so the row's failure class comes from the transport
            // status rather than being called a defect of the avatar.
            internal bool readFailed;
            internal int? status;
            internal bool Blocks => error != null;
        }

        // The SDK's Build Control Panel fetches the selected avatar's record, and on a 404 or another
        // account's record it blanks the local id (VRCSdkControlPanelAvatarBuilder.ClearAvatarData, in the
        // SDK's shipped source), so the next upload mints a new avatar under the scene name.
        internal const string NotVisibleError =
            "the local blueprint id names no record the signed-in account owns (missing, or another " +
            "account's — the API does not say which); the SDK panel clears such an id when it loads the " +
            "avatar, and an upload then mints a new avatar under the scene name — sign in as the owning " +
            "account, or clear the id yourself to upload as new";

        internal static RowSight Sight(string handle, bool hasLocalId, RecordRead read, string currentUserId)
        {
            var s = new RowSight { handle = handle };
            if (!hasLocalId)
            {
                // The one path on which the uploader sends a name, and the name it sends is the scene object's.
                s.state = StateFirstUpload; s.publishName = handle;
            }
            else if (read.found && string.IsNullOrEmpty(currentUserId))
            {
                s.state = StateUnknown; s.readFailed = true;
                s.error = "the SDK names no signed-in user, so the record's owner cannot be checked — " +
                          "sign in through the Build Control Panel";
            }
            else if (read.found && read.authorId != currentUserId)
            {
                s.state = StateNotVisible; s.error = NotVisibleError;
            }
            else if (read.found)
            {
                // A reserved record with no bundle yet: the uploader treats it as new (fresh thumbnail) but
                // the name was fixed when the record was reserved.
                s.state = read.pendingUpload ? StatePendingUpload : StateUpdate;
                s.publishName = read.name;
            }
            else if (read.status == 404)
            {
                s.state = StateNotVisible; s.error = NotVisibleError;
            }
            else
            {
                s.state = StateUnknown; s.readFailed = true; s.status = read.status;
                s.error = "could not read the live record: " + read.error;
            }
            return s;
        }

        /// <summary>The whatIf line. An import-settings offender does not fail the verdict: a build pass can
        /// mask one (VRCFury's texture clone masks the mip check), so it is not certain to block the upload.</summary>
        internal static string WhatIfSummary(RowSight[] sights, int[] importOffenders, int[] rowsUnscanned)
        {
            var rows = new System.Text.StringBuilder();
            bool blocked = false, offenders = false;
            for (int i = 0; i < sights.Length; i++)
            {
                var s = sights[i];
                if (i > 0) rows.Append("; ");
                rows.Append("handle=").Append(s.handle).Append(" state=").Append(s.state);
                if (s.publishName != null) rows.Append(" publishName=").Append(AvatarRecordLogic.Quote(s.publishName));
                // Said, not omitted: a missing field would read as a clean scan.
                if (importOffenders[i] < 0) rows.Append(" importOffenders=unscanned");
                else
                {
                    rows.Append(" importOffenders=").Append(importOffenders[i]);
                    if (rowsUnscanned[i] > 0) rows.Append(" (").Append(rowsUnscanned[i]).Append(" validation row(s) unscanned)");
                    offenders |= importOffenders[i] > 0;
                }
                if (s.Blocks) { rows.Append(" error=").Append(RedactIds(s.error)); blocked = true; }
            }
            return "[upload-avatar] (whatIf) " + sights.Length + " avatar(s): " + rows +
                   (offenders ? " | importOffenders are import settings the SDK's validations reject — " +
                                "ConformImportSettings.Run(<avatar root>, whatIf: true) names them" : "") +
                   " => " + (blocked ? "FAIL" : "PASS");
        }

        // ── Login self-heal ─────────────────────────────────────────────────────────────────────

        /// <summary>State of the saved-credential restore this door kicks. <c>Succeeded</c> is the task having
        /// run to completion, not proof of a login: a restore that returns false with no saved credentials
        /// completes just as cleanly, and CAU's own VerifyCredentials conflates the two (it returns true for
        /// a completed task whatever its result). Whether you are logged in is only ever
        /// <c>APIUser.IsLoggedIn</c>.</summary>
        internal enum LoginRestore { None, InFlight, Succeeded, Failed }

        /// <summary>How long an unfinished restore may stay unfinished before the door stops saying
        /// "re-run in a moment". CAU's TryLogin builds a TaskCompletionSource with no timeout and no
        /// cancellation, so a fetch that invokes neither callback would otherwise leave the door telling
        /// the caller to poll forever, with no escape short of a recompile.</summary>
        internal const double RestoreDeadlineSeconds = 30.0;

        /// <summary>The refusal text for a not-logged-in door, given the restore's state. Keep the rows
        /// distinguishable: "re-run" and "re-running will not help" are opposite instructions.
        /// <paramref name="retriesRemain"/> flips the same state from "re-run" to the operator's sign-in.</summary>
        internal static string LoginRefusal(LoginRestore restore, bool restoreReturnedFalse,
                                            bool deadlineExpired, bool retriesRemain, string failReason)
        {
            const string signIn = " — open the Build Control Panel and sign in";
            switch (restore)
            {
                case LoginRestore.InFlight:
                    return deadlineExpired
                        ? "not logged into the VRChat SDK: the saved-credential restore has not completed in " +
                          RestoreDeadlineSeconds + "s" + signIn
                        : "restoring saved VRChat credentials — re-run this call in a moment";
                case LoginRestore.Succeeded:
                    if (restoreReturnedFalse)
                        return "not logged into the VRChat SDK and there are no saved credentials to restore" + signIn;
                    // Usually the login was lost after that restore (a sign-out in the panel), which a fresh
                    // kick fixes, so retry before calling it drift.
                    return retriesRemain
                        ? "the saved-credential restore reported success but the SDK is signed out — " +
                          "re-run this call to retry the restore"
                        : "the saved-credential restore reported success but the SDK is still signed out after " +
                          "retrying (suspect a CAU/SDK version mismatch) — re-running will not help" + signIn;
                case LoginRestore.Failed:
                    return "the saved-credential restore failed (" + (failReason ?? "no reason reported") + ")" +
                           (retriesRemain ? " — re-run this call to retry it" : signIn);
                default:
                    return "not logged into the VRChat SDK" + signIn;
            }
        }

        /// <summary>The not-logged-in self-heal, over injected dependencies so it is testable without CAU or a
        /// live SDK login. A domain reload clears APIUser.IsLoggedIn while saved credentials survive, and
        /// whichever SDK/CAU window repaints first may restore it, so a bare check refuses non-deterministically.
        ///
        /// It never awaits. The SDK's InitialFetchCurrentUser callback requires the editor update loop
        /// (measured: with the main thread blocked 3s the callback landed only ~8ms after the block
        /// released), so blocking on the restore would hang the editor. The normal cost of a cold door is one
        /// re-run: the kick returns WaitingForActivation (measured, from a forced-cold editor), this call
        /// refuses with "re-run in a moment", and the next one passes.</summary>
        internal sealed class LoginLatch
        {
            internal const int MaxKicks = 2;

            readonly Func<(Task<bool> task, string failReason)> _kick;
            readonly Func<bool> _isLoggedIn;
            readonly Func<double> _now;

            Task<bool> _task;
            double _kickedAt;
            int _kicks;
            volatile string _failReason;

            internal LoginLatch(Func<(Task<bool>, string)> kick, Func<bool> isLoggedIn, Func<double> now)
            {
                _kick = kick; _isLoggedIn = isLoggedIn; _now = now;
            }

            internal int Kicks => _kicks;

            internal LoginRestore State =>
                _task == null ? LoginRestore.None :
                !_task.IsCompleted ? LoginRestore.InFlight :
                _task.Status == TaskStatus.RanToCompletion ? LoginRestore.Succeeded :
                LoginRestore.Failed;   // Faulted or Canceled — never read .Result on either

            internal bool ReturnedFalse =>
                _task != null && _task.Status == TaskStatus.RanToCompletion && !_task.Result;

            internal bool DeadlineExpired =>
                _task != null && !_task.IsCompleted && _now() - _kickedAt > RestoreDeadlineSeconds;

            // CAU gives its restore task no timeout and no cancellation, so one past the deadline may never
            // complete; abandoning it is safe because its Observe continuation still holds it. A restore that
            // returned false means no saved credentials, and repeating it cannot change the answer.
            bool MayKick => _task == null ||
                            (_kicks < MaxKicks &&
                             (State == LoginRestore.Failed ||
                              (State == LoginRestore.InFlight && DeadlineExpired) ||
                              (State == LoginRestore.Succeeded && !ReturnedFalse)));

            /// <summary>Null when the door may proceed, otherwise the refusal.</summary>
            internal string Evaluate()
            {
                if (_isLoggedIn()) { Clear(); return null; }

                if (MayKick)
                {
                    var (task, failReason) = _kick();
                    if (task == null)
                        return "not logged into the VRChat SDK, and the saved-credential restore could not " +
                               "be started (" + (failReason ?? "no reason reported") +
                               ") — open the Build Control Panel and sign in";
                    _task = task; _kickedAt = _now(); _kicks++; _failReason = null;
                    Observe(task);
                }

                // Re-inspect: the kick normally leaves the restore pending, but the login may have been
                // restored by another editor surface while we were in here — then this call passes.
                if (_isLoggedIn()) { Clear(); return null; }

                return LoginRefusal(State, ReturnedFalse, DeadlineExpired, _kicks < MaxKicks, _failReason);
            }

            /// <summary>Read the outcome at kick time: a faulted restore nobody observes is finalized into
            /// TaskScheduler.UnobservedTaskException, console noise on an unrelated GC. Runs on whatever thread
            /// completed the task (ExecuteSynchronously only hints inline), so string assignment only, no Unity
            /// API. The ReferenceEquals guard stops an abandoned task's late continuation overwriting the live
            /// attempt's reason.</summary>
            void Observe(Task<bool> task) => task.ContinueWith(t =>
            {
                if (!ReferenceEquals(_task, t)) return;
                if (t.IsFaulted)
                    _failReason = RedactIds((t.Exception?.GetBaseException())?.Message ?? "unknown error");
                else if (t.IsCanceled)
                    _failReason = "the restore was canceled";
            }, TaskContinuationOptions.ExecuteSynchronously);

            void Clear() { _task = null; _kicks = 0; _failReason = null; _kickedAt = 0; }
        }

        /// <summary>Per-handle hard attempt cap so account-safety never depends on skill prose.</summary>
        internal sealed class AttemptLedger
        {
            internal const int MaxAttempts = 3;
            readonly Dictionary<string, int> _n = new Dictionary<string, int>();
            internal bool MayAttempt(string handle)
                => !_n.TryGetValue(handle, out var c) || c < MaxAttempts;
            internal void Record(string handle)
                => _n[handle] = (_n.TryGetValue(handle, out var c) ? c : 0) + 1;
            internal void Clear(string handle) => _n.Remove(handle);
        }
    }
}
