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

        /// <summary>transient | rate-limit | real. 429 is its own class (never auto-retried);
        /// a ValidationException or any non-429 4xx is real; other 5xx / timeout is transient.</summary>
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

        /// <summary>What one read of a row's live record came back with. Factory-only so a caller cannot
        /// build a found record with a status, or a failure with a name.</summary>
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

        // A local id is only as good as the record behind it. The SDK's Build Control Panel fetches the
        // selected avatar's record, and on a 404 or another account's record it blanks the local id
        // (VRCSdkControlPanelAvatarBuilder.ClearAvatarData, in the SDK's shipped source), so the next upload
        // mints a new avatar under the scene name. Classifying by "holds an id" therefore promises an update
        // that never happens.
        internal const string NotVisibleError =
            "the local blueprint id names no record the signed-in account owns (missing, or another " +
            "account's — the API does not say which); the SDK panel clears such an id when it loads the " +
            "avatar, and an upload then mints a new avatar under the scene name — sign in as the owning " +
            "account, or clear the id yourself to upload as new";

        /// <summary>Classify one row from its local id and the read of its live record.
        /// <paramref name="read"/> is ignored when there is no local id — nothing was fetched.</summary>
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

        /// <summary>The whatIf line. <paramref name="importOffenders"/>[i] is row i's count of import
        /// settings the SDK's validations reject, or -1 where no scan ran (a null entry, a root the scan refuses); <paramref name="rowsUnscanned"/>[i]
        /// is how many validation rows that scan could not evaluate. An offender does not fail the verdict:
        /// a build pass can mask one (VRCFury's texture clone masks the mip check), so it is not certain to
        /// block the upload.</summary>
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

        /// <summary>State of the saved-credential restore this door kicks. <c>Succeeded</c> is the TASK
        /// having run to completion — NOT proof of a login: CAU's own VerifyCredentials conflates the two
        /// (it returns <c>task.IsCompleted</c> for a task that completed <c>false</c>), and a restore that
        /// returns false with no saved credentials completes just as cleanly as one that signs you in.
        /// Whether you are logged in is only ever <c>APIUser.IsLoggedIn</c>.</summary>
        internal enum LoginRestore { None, InFlight, Succeeded, Failed }

        /// <summary>How long an unfinished restore may stay unfinished before the door stops saying
        /// "re-run in a moment". CAU's TryLogin builds a TaskCompletionSource with no timeout and no
        /// cancellation, so a fetch that invokes neither callback would otherwise leave the door telling
        /// the caller to poll forever, with no escape short of a recompile.</summary>
        internal const double RestoreDeadlineSeconds = 30.0;

        /// <summary>The refusal text for a not-logged-in door, given the restore's state. Every row names
        /// what to do next, and the rows are distinguishable on purpose: "re-run" and "re-running will not
        /// help" are opposite instructions, so collapsing them would leave a reader unable to tell whether
        /// the door is waiting, retrying, or stuck. <paramref name="retriesRemain"/> is what keeps the text
        /// honest: the same state says "re-run" while an attempt is left and names the operator boundary
        /// once they are spent. Returns the refusal; never null (the caller has already established that we
        /// are not logged in).</summary>
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
                    // Restore claimed success, SDK says signed out. Usually the login was lost AFTER that
                    // restore (a sign-out in the panel), which a fresh kick fixes — so retry before calling
                    // it drift.
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

        /// <summary>The not-logged-in self-heal, as a state machine over injected dependencies so it is
        /// testable without CAU or a live SDK login — a headless venue has neither, which is why the door's
        /// own preflight cannot cover this.
        ///
        /// It NEVER awaits. The SDK's InitialFetchCurrentUser callback requires the editor update loop
        /// (measured: with the main thread blocked 3s the callback landed only ~8ms after the block
        /// released), so blocking on the restore would hang the editor — the same hazard UploadAvatar.Run
        /// already names for the batch itself. So the normal cost of a cold door is ONE re-run: the kick
        /// returns WaitingForActivation (measured, from a forced-cold editor), this call refuses with
        /// "re-run in a moment", and the next one passes.
        ///
        /// The re-inspect after the kick is not that path — it catches the login being restored by someone
        /// else. The environment is genuinely racy: a domain reload clears APIUser.IsLoggedIn while saved
        /// credentials survive, and whichever SDK/CAU window repaints first may restore it, so the state can
        /// change under us between the entry check and the kick returning. Re-reading is free; skipping it
        /// would refuse on a door that is already open.
        ///
        /// A stale restore is re-kicked while attempts remain (<see cref="MaxKicks"/> total) so one
        /// transient fault cannot poison the door until the next recompile, bounded so a live account never
        /// sees an unbounded retry.</summary>
        internal sealed class LoginLatch
        {
            internal const int MaxKicks = 2;

            readonly Func<(Task<bool> task, string failReason)> _kick;
            readonly Func<bool> _isLoggedIn;
            readonly Func<double> _now;

            Task<bool> _task;
            double _kickedAt;
            int _kicks;
            // Written by Observe's continuation, which ExecuteSynchronously only HINTS will run inline —
            // read on the main thread. volatile so a captured reason cannot be lost to a missed publication,
            // degrading a named refusal to "no reason reported".
            volatile string _failReason;

            internal LoginLatch(Func<(Task<bool>, string)> kick, Func<bool> isLoggedIn, Func<double> now)
            {
                _kick = kick; _isLoggedIn = isLoggedIn; _now = now;
            }

            /// <summary>Kicks spent on the current latch — read by tests to prove the door kicks once per
            /// call-sequence rather than once per call.</summary>
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

            // A spent latch is re-kicked while attempts remain. Three states qualify, for the same reason —
            // the latch's record has gone stale relative to the SDK: a failed restore (transient); one that
            // succeeded while we are signed out anyway (the login was lost afterwards — a sign-out in the
            // panel — which a fresh kick fixes); and one still unfinished past the deadline, whose task CAU
            // gave no timeout and no cancellation, so it may never complete at all. Abandoning that stale
            // task is safe: its Observe continuation still holds it, so nothing is left unobserved.
            // A restore that returned FALSE is not re-kicked: that is "no saved credentials", and repeating
            // it cannot change the answer.
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

            /// <summary>Read the outcome AT KICK TIME, not at read time. A faulted restore that nobody
            /// observes is finalized into TaskScheduler.UnobservedTaskException — console noise on an
            /// unrelated GC — and clearing the latch on a later success would drop the last reference
            /// before anything read it. Caches a redacted string so the door never touches the Task's
            /// exception itself. Runs on whatever thread completed the task: string assignment only,
            /// no Unity API.
            ///
            /// The ReferenceEquals guard keeps an ABANDONED task quiet: a re-kick replaces _task, and the
            /// old task's continuation may land afterwards — without the guard it would overwrite the live
            /// attempt's reason (or repopulate one the re-kick had just cleared) with a stale one.</summary>
            void Observe(Task<bool> task) => task.ContinueWith(t =>
            {
                if (!ReferenceEquals(_task, t)) return;
                if (t.IsFaulted)
                    _failReason = RedactIds((t.Exception?.GetBaseException())?.Message ?? "unknown error");
                else if (t.IsCanceled)
                    _failReason = "the restore was canceled";
            }, TaskContinuationOptions.ExecuteSynchronously);

            /// <summary>Drop the latch once logged in, so a later sign-out (a domain reload clears the
            /// login) starts from a full attempt budget rather than an exhausted one.</summary>
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
