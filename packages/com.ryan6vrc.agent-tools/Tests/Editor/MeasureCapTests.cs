using System;
using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// The pure parts of the measurement time limits: a pose program's simulated total and the refusal that names it, the
// per-run cap decision, and the rolling budget's arithmetic and refusal. The pump's teardown on expiry mutates a live
// avatar, so it is proven by execute_code in play (docs/verify.md §Test venue), not here.
public class MeasureCapTests
{
    static DrivePhysBones.PoseList Program(string json) { Assert.IsNull(DrivePhysBones.ParsePoses(json, out var pl)); return pl; }

    [Test]
    public void ProgramSeconds_countsRestHold_rampsAndEachRowsHold()
    {
        // rest 2.5 + a (0 + 2.5 drive hold) + b (1 ramp + 6 own hold) + c (0.5 ramp + 0 hold) = 12.5
        var pl = Program("[{\"name\":\"a\"},{\"name\":\"b\",\"ramp\":1,\"hold\":6},{\"name\":\"c\",\"ramp\":0.5,\"hold\":0}]");
        Assert.AreEqual(12.5f, DrivePhysBones.ProgramSeconds(pl, 2.5f), 1e-4f);
        Assert.AreEqual(3.5f + 7f + 0.5f + 3.5f, DrivePhysBones.ProgramSeconds(pl, 3.5f), 1e-4f);
    }

    [Test]
    public void ProgramRefusal_fitsAtTheCap_andNamesTotalCapAndLongestRowsOverIt()
    {
        var pl = Program("[{\"name\":\"short\",\"hold\":1},{\"name\":\"kick\",\"hold\":100},{\"name\":\"slow\",\"ramp\":5,\"hold\":70},{\"name\":\"mid\",\"hold\":2}]");
        // 2 + 1 + 100 + 75 + 2 = 180: exactly the cap fits.
        Assert.IsNull(DrivePhysBones.ProgramRefusal(pl, 2f, 180f));
        var r = DrivePhysBones.ProgramRefusal(pl, 2f, 150f);
        StringAssert.Contains("180 simulated seconds over 5 rows", r);
        StringAssert.Contains("150 s time cap", r);
        StringAssert.Contains("'kick' 100 s, 'slow' 75 s, 'mid' 2 s", r);
        StringAssert.Contains("the operator's limit", r);
        StringAssert.Contains("rather than raising maxSeconds", r);
        StringAssert.DoesNotContain("Split", r);
    }

    [Test]
    public void CapNote_namesOnlyARaisedCap()
    {
        Assert.AreEqual("timeCap=180s", DrivePhysBones.CapNote(MeasureCap.DefaultSeconds));
        Assert.AreEqual("timeCap=90s", DrivePhysBones.CapNote(90f));
        StringAssert.Contains("raised above the 180s default", DrivePhysBones.CapNote(240f));
    }

    [Test]
    public void Passed_isStrictlyOverTheCap()
    {
        Assert.IsFalse(MeasureCap.Passed(180.0, 180f));
        Assert.IsTrue(MeasureCap.Passed(180.01, 180f));
    }

    [TestCase(0f)] [TestCase(-5f)] [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)]
    public void SecondsError_refusesNonPositiveAndNonFinite(float s) => Assert.IsNotNull(MeasureCap.SecondsError(s));

    static readonly DateTime T0 = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    static MeasureCap.Entry E(long id, string stage, double startS, double? endS, float cap = 180f) =>
        new MeasureCap.Entry { id = id, stage = stage, start = T0.AddSeconds(startS), end = endS.HasValue ? T0.AddSeconds(endS.Value) : (DateTime?)null, cap = cap };

    [Test]
    public void Spent_countsANestedRunOnce_clipsToTheWindow_andChargesAnOpenRunToItsCap()
    {
        var ledger = new List<MeasureCap.Entry>
        {
            E(1, "harness", 0, 500, 600f),    // a hand-written pump's run
            E(2, "sweep", 100, 220),          // a drive it armed inside that run: no extra charge
            E(3, "old", 1000, 1100),
            E(4, "dead", 1200, null, 180f),   // never closed: charged 180 s at most
        };
        Assert.AreEqual(500 + 100 + 180, MeasureCap.Spent(ledger, T0.AddSeconds(2000), 3000f, out var by), 1e-6);
        Assert.AreEqual("harness", by[0].stage);
        // A window from t=300 on keeps only the harness's last 200 s.
        Assert.AreEqual(200 + 100 + 180, MeasureCap.Spent(ledger, T0.AddSeconds(2100), 1800f, out _), 1e-6);
    }

    [Test]
    public void BudgetRefusal_nullUnderBudget_elseNamesUseStagesAndWhenItFrees()
    {
        var ledger = new List<MeasureCap.Entry> { E(1, "control", 0, 400, 600f), E(2, "found", 500, 900, 600f) };
        Assert.IsNull(MeasureCap.BudgetRefusal(ledger, T0.AddSeconds(1000), 900f, 1800f));
        ledger.Add(E(3, "cand1", 1000, 1150));
        var r = MeasureCap.BudgetRefusal(ledger, T0.AddSeconds(1200), 900f, 1800f);
        StringAssert.Contains("950 s of wall clock in the last 30 min against a 900 s budget", r);
        StringAssert.Contains("control 400 s, found 400 s, cand1 150 s", r);
        // 950 s in the window; it drops under 900 once t=0..51 has left it, at t=1851.
        StringAssert.Contains(T0.AddSeconds(1851).ToLocalTime().ToString("HH:mm:ss"), r);
    }

    [Test]
    public void Ledger_roundTrips()
    {
        var ledger = new List<MeasureCap.Entry> { E(1, "a b", 0, 10), E(2, "open", 20, null, 90f) };
        var back = MeasureCap.Parse(MeasureCap.Format(ledger));
        Assert.AreEqual(2, back.Count);
        Assert.AreEqual(ledger[0].start, back[0].start); Assert.AreEqual(ledger[0].end, back[0].end); Assert.AreEqual("a b", back[0].stage);
        Assert.IsNull(back[1].end); Assert.AreEqual(90f, back[1].cap);
    }
}
