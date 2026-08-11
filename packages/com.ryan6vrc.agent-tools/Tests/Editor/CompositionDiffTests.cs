using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

/// <summary>
/// The bake diff is the part of <c>ReportComposition</c> most able to lie: it is the only place the door
/// asserts a mapping between an authored name and a built one, and every category it prints is read as a
/// claim about what the build did. It is also pure — names in, rows out, no <c>UnityEngine.Object</c>
/// touched — so <c>docs/verify.md</c>'s NUnit-must-not-mutate rule does not reach it and there is no excuse
/// for leaving it to the live venue.
///
/// Each case below is a defect this logic actually had.
/// </summary>
public class CompositionDiffTests
{
    private static ReportComposition.CensusResult Census(params (string name, bool diffable)[] rows)
    {
        var c = new ReportComposition.CensusResult();
        foreach (var (name, diffable) in rows)
        {
            var r = new ReportComposition.ParamRow { Name = name, Diffable = diffable };
            r.DeclaredAt.Add("test surface");
            c.Params.Add(r);
        }
        return c;
    }

    private static string CategoryOf(List<CompositionBake.DiffRow> rows, string authored) =>
        rows.Where(r => r.Authored == authored).Select(r => r.Category).FirstOrDefault();

    // ── the separator boundary ───────────────────────────────────────────────────────────────────────

    [Test]
    public void APrefixMustEndAtASeparator_orAnUnrelatedNameIsCalledARename()
    {
        // `Hair/HairToggle` merely ENDS WITH `Toggle`. A bare suffix test calls that a rename and prints a
        // confident, wrong provenance claim from the one door whose thesis is that it never makes one.
        var rows = CompositionBake.Diff(Census(("Toggle", true)), new List<string> { "Hair/HairToggle" }, null);
        Assert.AreEqual("dropped", CategoryOf(rows, "Toggle"));
        Assert.IsTrue(rows.Any(r => r.Built == "Hair/HairToggle" && r.Category == "unattributed"),
            "the built name nothing claimed must still appear as built-only");
    }

    [TestCase("VF122_Damping", true)]
    [TestCase("Prefix/Damping", true)]
    [TestCase("XDamping", false)]
    [TestCase("Damping", false)]   // equal length is `kept`, not a prefixed form
    public void IsPrefixedForm_acceptsOnlyASlashOrUnderscoreBoundary(string built, bool expected)
        => Assert.AreEqual(expected, CompositionBake.IsPrefixedForm(built, "Damping"));

    [Test]
    public void TheMeasuredVrcFuryShape_isARename()
    {
        var rows = CompositionBake.Diff(Census(("Reconstruction/Damping", true)),
            new List<string> { "VF122_Reconstruction/Damping" }, null);
        Assert.AreEqual("renamed", CategoryOf(rows, "Reconstruction/Damping"));
    }

    // ── merged replaces, it does not ride alongside ──────────────────────────────────────────────────

    [Test]
    public void Merged_replacesTheRowsItSummarises_soTheCountsStillSum()
    {
        // Two authored names both resolving onto one built name. Emitting `merged` ON TOP of the two
        // `renamed` rows double-counts one built parameter and makes the categories exceed params=.
        var rows = CompositionBake.Diff(Census(("A/P", true), ("B/P", true)),
            new List<string> { "VF1_A/P", "VF1_B/P", "Shared" }, null);
        // Construct the genuine collision: one built name both authored names are a prefixed form of.
        var merged = CompositionBake.Diff(Census(("P", true), ("X/P", true)),
            new List<string> { "VF1_X/P" }, null);
        Assert.AreEqual(1, merged.Count(r => r.Category == "merged"), "one merged row: " + Dump(merged));
        Assert.AreEqual(0, merged.Count(r => r.Category == "renamed"),
            "the renamed rows it summarises must be gone, not kept beside it: " + Dump(merged));
        Assert.AreEqual(2, rows.Count(r => r.Category == "renamed"), Dump(rows));
    }

    // ── ambiguity must not swallow a built-only name ─────────────────────────────────────────────────

    [Test]
    public void AnAmbiguousMatch_leavesEveryCandidateStillClaimable()
    {
        // Two built names are each a prefixed form of `P`. Nothing may be attributed — and removing them
        // from the unclaimed set would hide a genuinely built-only parameter inside a cell belonging to an
        // unrelated authored name.
        var rows = CompositionBake.Diff(Census(("P", true)), new List<string> { "A/P", "B/P" }, null);
        Assert.AreEqual("unattributed", CategoryOf(rows, "P"));
        Assert.AreEqual(2, rows.Count(r => r.Authored == "—" && r.Category == "unattributed"),
            "both candidates must still surface as built-only: " + Dump(rows));
    }

    // ── scope: a runtime-written name is not "dropped" ───────────────────────────────────────────────

    [Test]
    public void ARuntimeWrittenName_isNotInScope_ratherThanDropped()
    {
        // A physbone suffix is declared by nothing, so a declaration set cannot carry it. Counting it as
        // `dropped` reads as "the build removed it" and manufactures a hundred false rows on a real avatar.
        var rows = CompositionBake.Diff(Census(("Tail_IsGrabbed", false), ("Real", true)),
            new List<string> { "Real" }, null);
        Assert.AreEqual("not-in-scope", CategoryOf(rows, "Tail_IsGrabbed"));
        Assert.AreEqual("kept", CategoryOf(rows, "Real"));
        Assert.AreEqual(0, rows.Count(r => r.Category == "dropped"), Dump(rows));
    }

    // ── the filter must not change the answer ────────────────────────────────────────────────────────

    [Test]
    public void ParamFilter_narrowsTheView_withoutChangingAnyCategory()
    {
        var census = Census(("Toggle", true), ("Other", true));
        var built = new List<string> { "Hair/Toggle", "Other" };
        var unfiltered = CompositionBake.Diff(census, built, null);
        var filtered = CompositionBake.Diff(census, built, "Hair");

        Assert.AreEqual("renamed", CategoryOf(unfiltered, "Toggle"));
        // Filtering the authored census FIRST leaves the built name with nothing to claim it, and reports
        // the whole filtered-on surface as built-only — the answer changing with the question.
        Assert.AreEqual("renamed", CategoryOf(filtered, "Toggle"),
            "the filter is a view, not an input to attribution: " + Dump(filtered));
        Assert.IsFalse(filtered.Any(r => r.Authored == "Other"), "the filter must still narrow: " + Dump(filtered));
    }

    [Test]
    public void APartialBuiltRead_reportsUnreadRatherThanDropped()
    {
        // The difference between "the build removed this" and "I did not see the built side". Only the
        // first is a claim, and only a complete read may make it.
        var rows = CompositionBake.Diff(Census(("A", true)), new List<string>(), null, builtSideComplete: false);
        Assert.AreEqual("built-side-unread", CategoryOf(rows, "A"), Dump(rows));
        Assert.AreEqual(0, rows.Count(r => r.Category == "dropped"), Dump(rows));
    }

    [Test]
    public void AnEmptyBuiltSet_dropsEverythingDiffableAndNothingElse()
    {
        var rows = CompositionBake.Diff(Census(("A", true), ("B", false)), new List<string>(), null);
        Assert.AreEqual("dropped", CategoryOf(rows, "A"));
        Assert.AreEqual("not-in-scope", CategoryOf(rows, "B"));
    }

    private static string Dump(List<CompositionBake.DiffRow> rows) =>
        "\n" + string.Join("\n", rows.Select(r => r.Authored + " | " + r.Category + " | " + r.Built));
}
