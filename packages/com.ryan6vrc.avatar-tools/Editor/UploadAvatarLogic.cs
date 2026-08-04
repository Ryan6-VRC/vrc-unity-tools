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
        /// It NEVER awaits. The SDK's InitialFetchCurrentUser callback can require the editor update loop
        /// (measured: with the main thread blocked 3s the callback landed only ~8ms after the block
        /// released), so blocking on the restore would hang the editor — the same hazard UploadAvatar.Run
        /// already names for the batch itself. Instead: kick, re-inspect in the same call (the restore often
        /// completes synchronously, so the common path costs no re-run at all), and otherwise refuse with
        /// an instruction to re-run.
        ///
        /// A failed restore is re-kicked once (<see cref="MaxKicks"/> total). The environment is genuinely
        /// racy — a domain reload clears APIUser.IsLoggedIn while saved credentials survive, and whichever
        /// editor window repaints first may restore it — so one transient fault must not poison the door
        /// until the next recompile. The bound is what keeps that from becoming an unbounded retry against
        /// a live account. Because a failure is re-kicked while attempts remain, a <c>Failed</c> state that
        /// reaches a refusal has by construction exhausted them.</summary>
        internal sealed class LoginLatch
        {
            internal const int MaxKicks = 2;

            readonly Func<(Task<bool> task, string failReason)> _kick;
            readonly Func<bool> _isLoggedIn;
            readonly Func<double> _now;

            Task<bool> _task;
            double _kickedAt;
            int _kicks;
            string _failReason;

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

            // A spent latch is re-kicked while attempts remain. Two states qualify, for the same reason —
            // the latch's record has gone stale relative to the SDK: a failed restore (transient), and a
            // restore that succeeded while we are signed out anyway (the login was lost afterwards — a
            // sign-out in the panel — which a fresh kick fixes). A restore that returned FALSE is not
            // re-kicked: that is "no saved credentials", and repeating it cannot change the answer.
            bool MayKick => _task == null ||
                            (_kicks < MaxKicks &&
                             (State == LoginRestore.Failed ||
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

                // Re-inspect: the restore frequently completes synchronously, and then this call passes.
                if (_isLoggedIn()) { Clear(); return null; }

                return LoginRefusal(State, ReturnedFalse, DeadlineExpired, _kicks < MaxKicks, _failReason);
            }

            /// <summary>Read the outcome AT KICK TIME, not at read time. A faulted restore that nobody
            /// observes is finalized into TaskScheduler.UnobservedTaskException — console noise on an
            /// unrelated GC — and clearing the latch on a later success would drop the last reference
            /// before anything read it. Caches a redacted string so the door never touches the Task's
            /// exception itself. Runs on whatever thread completed the task: string assignment only,
            /// no Unity API.</summary>
            void Observe(Task<bool> task) => task.ContinueWith(t =>
            {
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
