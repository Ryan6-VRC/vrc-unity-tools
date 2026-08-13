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
        Assert.IsTrue(rows.Any(r => r.Built == "Hair/HairToggle" && r.Category == "built-only"),
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
        Assert.AreEqual("ambiguous", CategoryOf(rows, "P"));
        Assert.AreEqual(2, rows.Count(r => r.Authored == "—" && r.Category == "built-only"),
            "both candidates must still surface as built-only: " + Dump(rows));
    }

    // ── exact-claim precedence: a tie-breaker, never evidence of removal ──────────────────────────────

    [Test]
    public void AnExactlyClaimedBuiltName_isNotAnotherRowsRenameCandidate()
    {
        // The measured demo-avatar shape: a gimmick declares its own internal `Sync/D/Prop/C` on its
        // controller, so the census carries it as an authored name of its own — and it ENDS WITH the avatar's
        // shorter `Prop/C`. Without precedence both it and the build-minted `VF1_Prop/C` are candidates, so the
        // real rename is reported ambiguous and the built name it should have claimed is orphaned as built-only.
        var rows = CompositionBake.Diff(Census(("Prop/C", true), ("Sync/D/Prop/C", true)),
            new List<string> { "Sync/D/Prop/C", "VF1_Prop/C" }, null);
        Assert.AreEqual("renamed", CategoryOf(rows, "Prop/C"), Dump(rows));
        Assert.AreEqual("VF1_Prop/C", rows.First(r => r.Authored == "Prop/C").Built, Dump(rows));
        Assert.AreEqual("kept", CategoryOf(rows, "Sync/D/Prop/C"), Dump(rows));
        Assert.AreEqual(0, rows.Count(r => r.Category == "ambiguous"), Dump(rows));
    }

    [Test]
    public void AResolutionByExclusion_disclosesWhatItExcluded_inTheRowItself()
    {
        // Exclusion deletes the evidence of a possible merge, so the surviving row must carry that where a
        // reader checking THIS row sees it. A legend sentence cannot be checked against a specific row.
        var rows = CompositionBake.Diff(Census(("Prop/C", true), ("Sync/D/Prop/C", true)),
            new List<string> { "Sync/D/Prop/C", "VF1_Prop/C" }, null);
        var caveat = rows.First(r => r.Authored == "Prop/C").Caveat;
        Assert.IsTrue(caveat != null && caveat.Contains("Sync/D/Prop/C"),
            "the excluded candidate must be named in the row: " + caveat);
        Assert.IsTrue(caveat.Contains("merged"),
            "the row must name the reading it forecloses: " + caveat);
    }

    [Test]
    public void AMergedRow_carriesTheExclusionCaveatOfEveryRowItReplaced()
    {
        // The row that replaces two claims is the most confident statement the table makes, and it is assembled
        // from rows that may each have reached their claim only because precedence excluded a rival. Emitting the
        // generic merged surface and discarding their caveats launders exactly the disclosure the renamed branch
        // exists to make — and silently, because the foreclosed reading vanishes with the row that named it.
        //
        // `P` sees candidates X/P (exact-claimed, excluded) and VF_Z/P; `Z/P` sees only VF_Z/P. Both claim
        // VF_Z/P, so both rows are replaced by one merged row — and `P -> X/P` is the reading being foreclosed.
        var rows = CompositionBake.Diff(Census(("P", true), ("X/P", true), ("Z/P", true)),
            new List<string> { "X/P", "VF_Z/P" }, null);
        var m = rows.Where(r => r.Category == "merged").ToList();
        Assert.AreEqual(1, m.Count, Dump(rows));
        Assert.IsTrue(m[0].Caveat != null && m[0].Caveat.Contains("X/P"),
            "the merged row must carry the excluded candidate its replaced row named: " + m[0].Caveat);
    }

    [Test]
    public void AnAmbiguousRow_alsoDisclosesWhatExclusionTrimmed()
    {
        // Worse here than on a renamed row: `ambiguous` is the one category whose whole purpose is honesty about
        // not knowing, so a candidate list silently trimmed by precedence makes the trimming unknowable.
        var rows = CompositionBake.Diff(Census(("P", true), ("A/P", true)),
            new List<string> { "A/P", "X/P", "Y/P" }, null);
        var row = rows.First(r => r.Authored == "P");
        Assert.AreEqual("ambiguous", row.Category, Dump(rows));
        Assert.IsTrue(row.Caveat != null && row.Caveat.Contains("A/P"),
            "the trimmed candidate must be named: " + row.Caveat);
    }

    [Test]
    public void ExclusionThatWouldEmptyTheCandidateSet_isNotApplied_soAMergeStaysAMerge()
    {
        // The ordinary idiom of an inner parameter exposed under a name the outer avatar also declares (MA
        // `remapTo`). Both authored names resolve onto the one built name: that is a MERGE. Applying exclusion
        // here would empty `Toggle`'s candidate set and drop it through to `dropped` — a confident false
        // "the build removed it", from the one door whose thesis is that it never makes such a claim.
        var rows = CompositionBake.Diff(Census(("Toggle", true), ("Hair/Toggle", true)),
            new List<string> { "Hair/Toggle" }, null);
        Assert.AreEqual(0, rows.Count(r => r.Category == "dropped"), Dump(rows));
        Assert.AreEqual(1, rows.Count(r => r.Category == "merged"), Dump(rows));
    }

    [Test]
    public void ANonDiffableRow_neverJoinsTheExactClaimSet()
    {
        // `Prop/Foo` reaches the census only as a runtime-written name, so it claims nothing. If it were
        // allowed to exact-claim the built `Prop/Foo`, that name would be excluded from `Foo`'s candidates
        // while no row ever claimed it — and one built name would read `dropped` ("the build removed it") AND
        // `built-only` ("present, unclaimed") in the same table.
        var rows = CompositionBake.Diff(Census(("Prop/Foo", false), ("Foo", true)),
            new List<string> { "Prop/Foo" }, null);
        Assert.AreEqual("not-in-scope", CategoryOf(rows, "Prop/Foo"), Dump(rows));
        Assert.AreEqual("renamed", CategoryOf(rows, "Foo"), Dump(rows));
        Assert.AreEqual(0, rows.Count(r => r.Category == "dropped"), Dump(rows));
        Assert.AreEqual(0, rows.Count(r => r.Category == "built-only"),
            "the built name was claimed, so it is not also built-only: " + Dump(rows));
    }

    [Test]
    public void TheVerdictsDoNotDependOnCensusOrder()
    {
        var forward = CompositionBake.Diff(Census(("Prop/C", true), ("Sync/D/Prop/C", true)),
            new List<string> { "Sync/D/Prop/C", "VF1_Prop/C" }, null);
        var reversed = CompositionBake.Diff(Census(("Sync/D/Prop/C", true), ("Prop/C", true)),
            new List<string> { "Sync/D/Prop/C", "VF1_Prop/C" }, null);
        Assert.AreEqual(CategoryOf(forward, "Prop/C"), CategoryOf(reversed, "Prop/C"), Dump(reversed));
        Assert.AreEqual(CategoryOf(forward, "Sync/D/Prop/C"), CategoryOf(reversed, "Sync/D/Prop/C"), Dump(reversed));
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

    // ── a VRChat reserved name is not unexplained build output ────────────────────────────────────────

    [Test]
    public void AReservedName_isLabelledAsSuch_ratherThanReadingAsAnOrphan()
    {
        // `Grounded` is declared nowhere and referenced everywhere, so no authored row can ever claim it.
        // Left as `built-only` it reads as a dozen unexplained names the build invented. The predicate is
        // ControllerRules.IsVrcReserved — the same one the undeclared-param rule and the controller compiler
        // use, so no second list exists to disagree with it.
        var rows = CompositionBake.Diff(Census(("Real", true)), new List<string> { "Real", "Grounded" }, null);
        Assert.AreEqual("kept", CategoryOf(rows, "Real"), Dump(rows));
        Assert.IsTrue(rows.Any(r => r.Built == "Grounded" && r.Category == "vrc-reserved"), Dump(rows));
    }

    [Test]
    public void AReservedNameTheAvatarItselfDeclares_isStillKept_notRelabelled()
    {
        // The label is applied only to rows nothing claimed, so it can never override a verdict: an avatar
        // that does declare a reserved name in its own parameters asset is `kept`, as measured.
        var rows = CompositionBake.Diff(Census(("Grounded", true)), new List<string> { "Grounded" }, null);
        Assert.AreEqual("kept", CategoryOf(rows, "Grounded"), Dump(rows));
        Assert.AreEqual(0, rows.Count(r => r.Category == "vrc-reserved"), Dump(rows));
    }

    private static string Dump(List<CompositionBake.DiffRow> rows) =>
        "\n" + string.Join("\n", rows.Select(r => r.Authored + " | " + r.Category + " | " + r.Built));
}
