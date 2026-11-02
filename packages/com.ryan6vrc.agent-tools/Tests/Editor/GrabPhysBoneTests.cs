using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// GrabPhysBone's testable core: the restore-record codec, pump policy, ownership rule and handle codec,
// factored out because a live run cannot reliably provoke their failures. The grab arc needs play mode, so it
// is proven by execute_code in a real play session (docs/verify.md §Test venue).
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

    // A ChainId half is a ulong and the id above already sits within a factor of 2 of long.MaxValue, so a
    // long.Parse implementation would throw on a legitimate handle.
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

        // A record written with no grab held has an empty ownership field, so it is well formed.
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
    // on the first tick is one or two frames short.
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

    // EditorApplication.Step() is asynchronous: a second Step issued before the first lands advances the frame
    // count by less than the calls issued.
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
    // reports would be a fiction.
    [Test]
    public void Decide_abortsWhenUnpausedAfterTheBaselineLatched()
    {
        string why;
        Assert.AreEqual(GrabPhysBone.PumpAction.Abort,
            GrabPhysBone.Decide(Obs(103, 100, 5, 102, paused: false), out why));
        StringAssert.Contains("un-paused", why);
    }

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
        // Even at the target count, a stalled pump aborts rather than finishing on a stale frame number.
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
