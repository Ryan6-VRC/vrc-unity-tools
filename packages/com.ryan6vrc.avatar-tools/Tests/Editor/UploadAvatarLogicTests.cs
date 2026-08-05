using System;
using System.Threading.Tasks;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

public class UploadAvatarLogicTests
{
    [Test] public void Redact_AvatarAndUserIdsAndUrls()
    {
        var s = "GetAvatar avtr_9f3c1a2b-0000-1111-2222-333344445555 for usr_deadbeef at https://api.vrchat.cloud/1/avatars/avtr_x failed";
        var r = UploadAvatarLogic.RedactIds(s);
        StringAssert.DoesNotContain("avtr_", r);
        StringAssert.DoesNotContain("usr_", r);
        StringAssert.DoesNotContain("https://", r);
        StringAssert.Contains("failed", r);
    }

    [Test] public void Classify_RateLimitIsOwnClass()
        => Assert.AreEqual("rate-limit", UploadAvatarLogic.Classify(429, false, false));
    [Test] public void Classify_ValidationIsReal()
        => Assert.AreEqual("real", UploadAvatarLogic.Classify(null, true, false));
    [Test] public void Classify_ServerErrorIsTransient()
        => Assert.AreEqual("transient", UploadAvatarLogic.Classify(503, false, false));
    // 400, not null: with a null status the fall-through already returns "transient", so the isTimeout
    // branch could be deleted outright and the test would stay green. A 4xx is the one input where the
    // branch is load-bearing — it must beat the "non-429 4xx is real" rule, or a timed-out upload gets
    // classified non-retryable.
    [Test] public void Classify_TimeoutIsTransient()
        => Assert.AreEqual("transient", UploadAvatarLogic.Classify(400, false, true));
    [Test] public void Classify_OtherClientErrorIsReal()
        => Assert.AreEqual("real", UploadAvatarLogic.Classify(403, false, false));

    [Test] public void Ledger_CapsAtThree()
    {
        var l = new UploadAvatarLogic.AttemptLedger();
        for (int i = 0; i < 3; i++) { Assert.IsTrue(l.MayAttempt("a")); l.Record("a"); }
        Assert.IsFalse(l.MayAttempt("a"));
        Assert.IsTrue(l.MayAttempt("b"));
    }

    [Test] public void Ledger_ClearResetsHandle()
    {
        var l = new UploadAvatarLogic.AttemptLedger();
        l.Record("a"); l.Record("a"); l.Clear("a");
        for (int i = 0; i < 3; i++) { Assert.IsTrue(l.MayAttempt("a")); l.Record("a"); }
        Assert.IsFalse(l.MayAttempt("a"));
    }

    [Test] public void Blueprint_EmptyIsFirstUpload()
    {
        Assert.AreEqual("first-upload", UploadAvatarLogic.ClassifyBlueprint(null));
        Assert.AreEqual("first-upload", UploadAvatarLogic.ClassifyBlueprint(""));
        Assert.AreEqual("update", UploadAvatarLogic.ClassifyBlueprint("avtr_x"));
    }

    // ── Login self-heal ─────────────────────────────────────────────────────────────────────────
    //
    // Two layers, and the second is the one that matters. LoginRefusal is a table, and a table test only
    // proves the table. The self-heal CLAIM — "the door kicks the restore itself, and a REFUSE you re-run
    // clears" — is a claim about a SEQUENCE of calls, so the LoginLatch tests below drive Evaluate() more
    // than once against a controllable restore. That is where a real bug would live: kicking on every call
    // (hammering a live account), never clearing the latch, or never converging.

    // A restore the test drives by hand, standing in for CAU's Uploader.TryLogin().
    sealed class FakeRestore
    {
        internal readonly TaskCompletionSource<bool> Tcs = new TaskCompletionSource<bool>();
        internal int Kicks;
        internal bool LoggedIn;
        internal double Now;
        internal Func<(Task<bool>, string)> Fail;   // when set, the kick itself fails (CAU absent/drift)
        internal Action OnKick;                     // the restore's side effect, e.g. signing in synchronously

        internal UploadAvatarLogic.LoginLatch Latch() =>
            new UploadAvatarLogic.LoginLatch(
                () => { Kicks++; OnKick?.Invoke(); return Fail != null ? Fail() : (Tcs.Task, (string)null); },
                () => LoggedIn, () => Now);
    }

    [Test] public void Refusal_InFlightSaysReRun_DeadlineSaysSignIn()
    {
        var waiting = UploadAvatarLogic.LoginRefusal(UploadAvatarLogic.LoginRestore.InFlight, false, false, true, null);
        StringAssert.Contains("re-run", waiting);
        var expired = UploadAvatarLogic.LoginRefusal(UploadAvatarLogic.LoginRestore.InFlight, false, true, true, null);
        StringAssert.Contains("sign in", expired);
        StringAssert.DoesNotContain("re-run this call", expired);   // the opposite instruction, not a variant
    }

    [Test] public void Refusal_NoSavedCredentialsIsTheOperatorBoundary()
    {
        var r = UploadAvatarLogic.LoginRefusal(UploadAvatarLogic.LoginRestore.Succeeded, restoreReturnedFalse: true,
                                               deadlineExpired: false, retriesRemain: true, failReason: null);
        StringAssert.Contains("no saved credentials", r);
        StringAssert.Contains("sign in", r);
        StringAssert.DoesNotContain("re-run", r);   // repeating a restore cannot invent credentials
    }

    // Succeeded-but-still-signed-out and Failed are NOT interchangeable: one suspects version drift, the
    // other reports a fault. A reader who cannot tell them apart cannot tell a stuck door from a broken
    // login. Both are shown here in their EXHAUSTED form — the retries-remain form is asserted below.
    [Test] public void Refusal_SucceededButSignedOutIsNotTheFailedText()
    {
        var mismatch = UploadAvatarLogic.LoginRefusal(UploadAvatarLogic.LoginRestore.Succeeded, false, false, false, null);
        StringAssert.Contains("will not help", mismatch);
        var failed = UploadAvatarLogic.LoginRefusal(UploadAvatarLogic.LoginRestore.Failed, false, false, false, "socket closed");
        StringAssert.Contains("socket closed", failed);
        Assert.AreNotEqual(mismatch, failed);
    }

    // The text must not tell a caller that re-running is pointless while the latch will in fact retry on
    // the next call — the live venue produced exactly that contradiction before retriesRemain existed.
    [Test] public void Refusal_SameStateSaysReRunWhileAnAttemptRemains()
    {
        var willRetry = UploadAvatarLogic.LoginRefusal(UploadAvatarLogic.LoginRestore.Succeeded, false, false,
                                                       retriesRemain: true, failReason: null);
        StringAssert.Contains("re-run", willRetry);
        StringAssert.DoesNotContain("will not help", willRetry);

        var failedRetryable = UploadAvatarLogic.LoginRefusal(UploadAvatarLogic.LoginRestore.Failed, false, false,
                                                             retriesRemain: true, failReason: "socket closed");
        StringAssert.Contains("re-run", failedRetryable);
    }

    [Test] public void Latch_LoggedInPassesAndNeverKicks()
    {
        var f = new FakeRestore { LoggedIn = true };
        var latch = f.Latch();
        Assert.IsNull(latch.Evaluate());
        Assert.IsNull(latch.Evaluate());
        Assert.AreEqual(0, f.Kicks);
    }

    // Why the re-inspect exists — and it is NOT "TryLogin usually finishes synchronously": measured from a
    // forced-cold editor, the kick returns WaitingForActivation, so the normal cold cost is one re-run.
    // What this covers is the racy case: the login gets restored by another editor surface while we are
    // inside Evaluate, and re-reading is free, so the door should pass rather than refuse on an open door.
    // Note the login must flip as the kick's side effect: pre-setting it would pass at the first check
    // without ever kicking, testing nothing (that was this test's own first version, and it caught itself).
    [Test] public void Latch_LoginRestoredDuringTheKickPassesWithoutARerun()
    {
        var f = new FakeRestore();
        f.OnKick = () => { f.Tcs.SetResult(true); f.LoggedIn = true; };
        var latch = f.Latch();
        Assert.IsNull(latch.Evaluate());
        Assert.AreEqual(1, f.Kicks);
    }

    [Test] public void Latch_SlowRestoreRefusesThenConverges_KickingOnlyOnce()
    {
        var f = new FakeRestore();
        var latch = f.Latch();
        StringAssert.Contains("re-run", latch.Evaluate());
        StringAssert.Contains("re-run", latch.Evaluate());   // still in flight
        Assert.AreEqual(1, f.Kicks, "an in-flight restore must not be re-kicked on every call");
        f.Tcs.SetResult(true); f.LoggedIn = true;
        Assert.IsNull(latch.Evaluate());
        Assert.AreEqual(1, f.Kicks);
    }

    // The deadline must spend the reserved attempt, not merely change the sentence. CAU's TryLogin has no
    // timeout and no cancellation, so a fetch that invokes neither callback pends forever; if the expired
    // cell could not re-kick, the second attempt would sit unreachable and the only escape from a wedged
    // restore would be a recompile — which is exactly what the deadline exists to avoid.
    [Test] public void Latch_InFlightPastDeadlineReKicksAndCanRecover()
    {
        var f = new FakeRestore();
        var latch = f.Latch();
        StringAssert.Contains("re-run", latch.Evaluate());
        Assert.AreEqual(1, f.Kicks);

        f.Now += UploadAvatarLogic.RestoreDeadlineSeconds + 1;   // the restore has wedged
        f.OnKick = () => f.LoggedIn = true;                      // a fresh kick gets through
        Assert.IsNull(latch.Evaluate(), "an expired restore must be re-kicked, not just re-worded");
        Assert.AreEqual(2, f.Kicks);
    }

    [Test] public void Latch_InFlightPastDeadlineWithNoAttemptsLeftNamesTheOperatorBoundary()
    {
        var f = new FakeRestore();
        var latch = f.Latch();
        latch.Evaluate();
        f.Now += UploadAvatarLogic.RestoreDeadlineSeconds + 1;
        latch.Evaluate();                                        // spends the last attempt, still wedged
        Assert.AreEqual(UploadAvatarLogic.LoginLatch.MaxKicks, f.Kicks);

        // The re-kick re-stamps the clock, so the fresh attempt gets its own full deadline before this
        // escalates — a re-kick that inherited the old stamp would escalate instantly.
        StringAssert.Contains("re-run", latch.Evaluate());
        f.Now += UploadAvatarLogic.RestoreDeadlineSeconds + 1;
        StringAssert.Contains("sign in", latch.Evaluate());
    }

    // Bounded re-kick: a transient fault must not poison the door until the next recompile (the environment
    // is racy by measurement), but the retry is capped so a live account never sees an unbounded loop.
    [Test] public void Latch_FailedRestoreIsReKickedOnce_ThenRefusesWithTheReason()
    {
        var f = new FakeRestore();
        var latch = f.Latch();
        latch.Evaluate();
        f.Tcs.SetException(new InvalidOperationException("transient socket failure"));
        var second = latch.Evaluate();          // re-kicks; the fake hands back the same faulted task
        Assert.AreEqual(UploadAvatarLogic.LoginLatch.MaxKicks, f.Kicks);
        latch.Evaluate();
        Assert.AreEqual(UploadAvatarLogic.LoginLatch.MaxKicks, f.Kicks, "the re-kick must be bounded");
        StringAssert.Contains("transient socket failure", second);
    }

    // Found in the live venue, not by a fake: a login lost AFTER a successful restore leaves the latch
    // holding a completed-true task, and the door declared CAU/SDK drift — telling the caller re-running
    // would not help, when a fresh kick is precisely what fixes it. A stale record is re-kicked.
    [Test] public void Latch_LoginLostAfterASuccessfulRestoreIsReKickedNotDeclaredDrift()
    {
        var f = new FakeRestore();
        var latch = f.Latch();
        StringAssert.Contains("re-run", latch.Evaluate());   // kick 1, still in flight
        f.Tcs.SetResult(true);                               // the restore completes true...
        Assert.AreEqual(1, f.Kicks);                         // ...but the login is lost before the next call
        f.OnKick = () => f.LoggedIn = true;                  // a fresh kick is what recovers it
        Assert.IsNull(latch.Evaluate(), "a stale Succeeded latch must be re-kicked, not declared drift");
        Assert.AreEqual(2, f.Kicks);
    }

    [Test] public void Latch_LoginClearsTheLatchSoALaterSignOutGetsAFullBudget()
    {
        var f = new FakeRestore();
        var latch = f.Latch();
        latch.Evaluate();
        f.Tcs.SetResult(true); f.LoggedIn = true;
        Assert.IsNull(latch.Evaluate());
        f.LoggedIn = false;                     // a domain reload clears the login; credentials survive
        latch.Evaluate();
        Assert.AreEqual(2, f.Kicks, "a cleared latch must kick again rather than stay exhausted");
    }

    // A CAU-less venue must never latch: the kick fails, the refusal names why, and the next call is free
    // to try again rather than being stuck behind a spent attempt budget.
    [Test] public void Latch_KickFailureNamesTheReasonAndDoesNotLatch()
    {
        var f = new FakeRestore { Fail = () => (null, "CAU TryLogin not resolved (CAU absent, or CAU drift)") };
        var latch = f.Latch();
        var r = latch.Evaluate();
        StringAssert.Contains("CAU", r);
        StringAssert.Contains("sign in", r);
        latch.Evaluate();
        Assert.AreEqual(2, f.Kicks, "a failed kick must leave the latch empty, not spend the budget");
    }
}
