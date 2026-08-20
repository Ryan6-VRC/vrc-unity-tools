using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// GrabPhysBone's testable core. NUnit cannot enter play mode, so nothing here claims a behavioural result —
// the grab arc itself is proven by execute_code in a real play session (docs/verify.md §Test venue). What is
// asserted is exactly the logic that was FACTORED OUT to be assertable, because each piece fails in a way a
// live run cannot reliably provoke or would not survive to report:
//
//   • the SessionState restore-record codec — its writer and reader sit a DOMAIN RELOAD apart, so no live
//     test can cross that boundary; a disagreement between them looks like "no record survived", whose end
//     state is a permanently mutated Time.fixedDeltaTime silently rescaling every later timing claim;
//   • the pump policy — play-mode exit, a manual un-pause, a stalled player loop and the arm-time off-by-one
//     are lifecycle hazards a live run provokes only by accident;
//   • the ownership rule — its failure releases the OPERATOR'S mouse grab, which a headless run has none of;
//   • the handle codec — a real ChainId half runs close to long.MaxValue, so a long.Parse would throw on a
//     legitimate id and there is no small-value test that would notice.
//
// There is deliberately NO SDK binding canary here. VRC.Dynamics.dll is a precompiled auto-referenced plugin
// and GrabPhysBone compiles against it directly with no reflection, so an SDK rename or overload change is a
// compile error — the compile IS the canary, for BINDING. (EmulatorBindingCanaryTests exists because av3emu
// is reached reflectively; that reasoning does not transfer.) It guards none of the measured SEMANTICS the
// tool rests on — grabberId inertness, LocalOffset as the seed convention, a stepped frame's dt following
// fixedDeltaTime — each of which an SDK update can change while still compiling. Those are re-measured, not
// asserted here, and no green run below should be read as covering them.
public class GrabPhysBoneTests
{
    // ── Handle codec ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Handle_roundTrips()
    {
        string h = GrabPhysBone.FormatHandle(5363680280397112067UL, 0UL);
        Assert.AreEqual("5363680280397112067:0", h);

        ulong a, b;
        Assert.IsTrue(GrabPhysBone.TryParseHandle(h, out a, out b));
        Assert.AreEqual(5363680280397112067UL, a);
        Assert.AreEqual(0UL, b);
    }

    // The measured id above already sits within a factor of ~3 of long.MaxValue, so ids past it are ordinary
    // rather than adversarial. A long.Parse implementation throws here on a perfectly legitimate handle.
    [Test]
    public void Handle_parsesUlongAboveLongMaxValue()
    {
        const ulong big = 18446744073709551615UL; // ulong.MaxValue
        ulong a, b;
        Assert.IsTrue(GrabPhysBone.TryParseHandle(GrabPhysBone.FormatHandle(big, big), out a, out b));
        Assert.AreEqual(big, a);
        Assert.AreEqual(big, b);
    }

    [Test]
    public void Handle_rejectsMalformed()
    {
        ulong a, b;
        Assert.IsFalse(GrabPhysBone.TryParseHandle(null, out a, out b));
        Assert.IsFalse(GrabPhysBone.TryParseHandle("", out a, out b));
        Assert.IsFalse(GrabPhysBone.TryParseHandle("123", out a, out b), "one half is not a handle");
        Assert.IsFalse(GrabPhysBone.TryParseHandle("1:2:3", out a, out b));
        Assert.IsFalse(GrabPhysBone.TryParseHandle("-1:0", out a, out b), "a chain id half is unsigned");
        Assert.IsFalse(GrabPhysBone.TryParseHandle("x:0", out a, out b));
        // ChainId.ToString() renders `A.B`. The separator here is deliberately different, so a pasted
        // ToString cannot be mistaken for a handle this tool minted and silently half-parse.
        Assert.IsFalse(GrabPhysBone.TryParseHandle("1.2", out a, out b));
    }

    // ── Restore record codec ──────────────────────────────────────────────────────────────────────

    [Test]
    public void RestoreRecord_roundTrips()
    {
        var written = new GrabPhysBone.RestoreRecord
        {
            Frozen = true,
            PriorPaused = false,
            DtPinned = true,
            SavedFixedDt = 0.0166666667f,
            PumpArmed = true,
            Owned = new[] { "5363680280397112067:0", "42:7" },
        };

        GrabPhysBone.RestoreRecord read;
        Assert.IsTrue(GrabPhysBone.TryParseRestoreRecord(GrabPhysBone.FormatRestoreRecord(written), out read));
        Assert.AreEqual(written.Frozen, read.Frozen);
        Assert.AreEqual(written.PriorPaused, read.PriorPaused);
        Assert.AreEqual(written.DtPinned, read.DtPinned);
        Assert.AreEqual(written.SavedFixedDt, read.SavedFixedDt, "the saved timestep must survive exactly — "
            + "a rounded restore silently rescales every later frame");
        Assert.AreEqual(written.PumpArmed, read.PumpArmed);
        // Ownership has to survive too: Release refuses a grab this tool did not mint, so ownership lost
        // to a reload turns that refusal into a lock on a grab nothing can drop.
        CollectionAssert.AreEqual(written.Owned, read.Owned);
    }

    // Half a restore is worse than none: restoring pause while leaving the timestep pinned leaves the editor
    // looking healthy and running at the wrong dt. So a malformed record is rejected whole.
    [Test]
    public void RestoreRecord_rejectsMalformedWhole()
    {
        GrabPhysBone.RestoreRecord r;
        Assert.IsFalse(GrabPhysBone.TryParseRestoreRecord(null, out r));
        Assert.IsFalse(GrabPhysBone.TryParseRestoreRecord("", out r));
        Assert.IsFalse(GrabPhysBone.TryParseRestoreRecord("v2|1|0|1|0.02", out r), "wrong arity");
        Assert.IsFalse(GrabPhysBone.TryParseRestoreRecord("v1|1|0|1|0.02|1", out r),
            "a superseded version is rejected, not partly read — v1 carried no ownership field");
        Assert.IsFalse(GrabPhysBone.TryParseRestoreRecord("v2|1|0|1|notafloat|1|", out r), "unparseable dt");
        Assert.AreEqual(default(float), r.SavedFixedDt, "a rejected record leaves nothing half-applied");
        Assert.IsFalse(r.Frozen);
        Assert.IsFalse(r.DtPinned);
        Assert.IsNull(r.Owned);

        // An empty ownership field is a well-formed record, not a malformed one — every door but a grab
        // writes it that way.
        Assert.IsTrue(GrabPhysBone.TryParseRestoreRecord("v2|1|0|0|0.02|0|", out r));
        CollectionAssert.IsEmpty(r.Owned);
    }

    // ── Pump policy ───────────────────────────────────────────────────────────────────────────────

    private static GrabPhysBone.PumpObservation Obs(
        int frame, int baseline, int target, int lastStepAt,
        bool playing = true, bool paused = true, int stable = 2, double sinceProgress = 0.0)
    {
        return new GrabPhysBone.PumpObservation
        {
            IsPlaying = playing,
            IsPaused = paused,
            FrameCount = frame,
            Baseline = baseline,
            TargetFrames = target,
            LastStepAtFrame = lastStepAt,
            StableTicks = stable,
            SecondsSinceProgress = sinceProgress,
        };
    }

    // Arming is racy: the frame in progress when pause was requested still completes, so a baseline latched
    // on the first tick is one or two frames short — fatal for a door whose only promise is an exact count.
    [Test]
    public void Decide_waitsForFrameCountToSettleBeforeLatching()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Wait,
            GrabPhysBone.Decide(Obs(100, -1, 5, int.MinValue, stable: 0), out why));
        Assert.AreEqual(GrabPhysBone.PumpAction.Wait,
            GrabPhysBone.Decide(Obs(100, -1, 5, int.MinValue, stable: 1), out why));
        Assert.AreEqual(GrabPhysBone.PumpAction.LatchBaseline,
            GrabPhysBone.Decide(Obs(100, -1, 5, int.MinValue, stable: 2), out why));
    }

    [Test]
    public void Decide_waitsWhileArmingUntilPauseTakes()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Wait,
            GrabPhysBone.Decide(Obs(100, -1, 5, int.MinValue, paused: false, stable: 9), out why),
            "no baseline is trustworthy until the editor is actually paused");
    }

    // Step() is asynchronous, so a second Step issued before the first lands would advance the frame count
    // by less than the calls issued — which is exactly why frames are counted from Time.frameCount and a
    // step is only issued once the previous one has landed.
    [Test]
    public void Decide_stepsOnlyAfterThePreviousStepLanded()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Step,
            GrabPhysBone.Decide(Obs(100, 100, 5, int.MinValue), out why), "first step after the latch");
        Assert.AreEqual(GrabPhysBone.PumpAction.Wait,
            GrabPhysBone.Decide(Obs(100, 100, 5, 100), out why), "issued at frame 100, not landed yet");
        Assert.AreEqual(GrabPhysBone.PumpAction.Step,
            GrabPhysBone.Decide(Obs(101, 100, 5, 100), out why), "landed — frame advanced");
    }

    [Test]
    public void Decide_finishesAtExactlyTheTargetFrameCount()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Step,
            GrabPhysBone.Decide(Obs(104, 100, 5, 103), out why), "4 of 5 advanced");
        Assert.AreEqual(GrabPhysBone.PumpAction.Finish,
            GrabPhysBone.Decide(Obs(105, 100, 5, 104), out why), "5 of 5");
        Assert.AreEqual(GrabPhysBone.PumpAction.Finish,
            GrabPhysBone.Decide(Obs(106, 100, 5, 104), out why), "overshoot still finishes, never loops");
    }

    // The single-frame case, which `Run`'s own not-registered-yet refusal prescribes ("Advance(1) then
    // retry") — so it is the most-called shape and the one an off-by-one would break first.
    [Test]
    public void Decide_advanceOfOneFrameFinishesAfterExactlyOneFrame()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Step,
            GrabPhysBone.Decide(Obs(100, 100, 1, int.MinValue), out why));
        Assert.AreEqual(GrabPhysBone.PumpAction.Wait,
            GrabPhysBone.Decide(Obs(100, 100, 1, 100), out why), "step issued, not landed");
        Assert.AreEqual(GrabPhysBone.PumpAction.Finish,
            GrabPhysBone.Decide(Obs(101, 100, 1, 100), out why));
    }

    [Test]
    public void Decide_abortsOnPlayModeExit()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Abort,
            GrabPhysBone.Decide(Obs(103, 100, 5, 102, playing: false), out why));
        StringAssert.Contains("play mode", why);
    }

    // A manual un-pause mid-pump means frames ran uncounted at wall-clock dt, so the frame count the door
    // reports would be a fiction. Abort loudly rather than finish on a number nobody can trust.
    [Test]
    public void Decide_abortsWhenUnpausedAfterTheBaselineLatched()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Abort,
            GrabPhysBone.Decide(Obs(103, 100, 5, 102, paused: false), out why));
        StringAssert.Contains("un-paused", why);
    }

    // Without this the pump spins forever on a blocked main thread, and since a second Advance is refused
    // while one is in flight, a single stall bricks every door for the rest of the session.
    [Test]
    public void Decide_abortsOnAStalledPlayerLoop()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Abort,
            GrabPhysBone.Decide(Obs(103, 100, 5, 102, sinceProgress: 21.0), out why));
        StringAssert.Contains("stalled", why);
    }

    [Test]
    public void Decide_stallBeatsEveryOtherOutcome()
    {
        string why;
        // Even at the target count, a pump that has not seen a frame for 20s is reporting about a player
        // loop that stopped — the honest end state is the abort, not a Finish on a stale frame number.
        Assert.AreEqual(GrabPhysBone.PumpAction.Abort,
            GrabPhysBone.Decide(Obs(105, 100, 5, 104, sinceProgress: 60.0), out why));
    }

    // ── Ownership ─────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] Live = { "10:0", "20:0", "30:0" };

    private static HashSet<string> Owns(params string[] handles) => new HashSet<string>(handles);

    [Test]
    public void Select_bareReleaseTakesOnlyOwnedGrabs()
    {
        var release = new List<string>();
        var foreign = new List<string>();
        string refusal;
        GrabPhysBone.SelectGrabsToRelease(Live, Owns("10:0", "30:0"), null, release, foreign, out refusal);

        Assert.IsNull(refusal);
        CollectionAssert.AreEquivalent(new[] { "10:0", "30:0" }, release);
        CollectionAssert.AreEquivalent(new[] { "20:0" }, foreign,
            "a foreign grab is named, never taken — the SDK's PhysBoneGrabHelper holds one while the "
            + "operator has the mouse down, and releasing it leaves the helper writing to a dead grab");
    }

    [Test]
    public void Select_namedOwnedHandleReleasesJustThatOne()
    {
        var release = new List<string>();
        var foreign = new List<string>();
        string refusal;
        GrabPhysBone.SelectGrabsToRelease(Live, Owns("10:0", "30:0"), "30:0", release, foreign, out refusal);

        Assert.IsNull(refusal);
        CollectionAssert.AreEqual(new[] { "30:0" }, release);
    }

    [Test]
    public void Select_refusesAForeignHandleAndNamesWhy()
    {
        var release = new List<string>();
        var foreign = new List<string>();
        string refusal;
        GrabPhysBone.SelectGrabsToRelease(Live, Owns("10:0"), "20:0", release, foreign, out refusal);

        Assert.IsNotNull(refusal);
        StringAssert.Contains("did not mint", refusal);
        CollectionAssert.IsEmpty(release, "a refusal releases nothing at all");
    }

    // A recompile re-registers chains and mints new ids, so a handle from before it names nothing — the
    // commonest stale case, and the refusal has to say so rather than reading as "already released".
    [Test]
    public void Select_refusesAHandleThatNamesNoLiveGrab()
    {
        var release = new List<string>();
        var foreign = new List<string>();
        string refusal;
        GrabPhysBone.SelectGrabsToRelease(Live, Owns("10:0"), "99:0", release, foreign, out refusal);

        Assert.IsNotNull(refusal);
        StringAssert.Contains("no live grab", refusal);
        StringAssert.Contains("recompile", refusal);
        CollectionAssert.IsEmpty(release);
    }

    [Test]
    public void Select_bareReleaseWithNothingOwnedIsAQuietNoOp()
    {
        var release = new List<string>();
        var foreign = new List<string>();
        string refusal;
        GrabPhysBone.SelectGrabsToRelease(Live, Owns(), null, release, foreign, out refusal);

        Assert.IsNull(refusal, "handing the venue back with nothing held is the normal unfreeze path");
        CollectionAssert.IsEmpty(release);
        CollectionAssert.AreEquivalent(Live, foreign);
    }
}
