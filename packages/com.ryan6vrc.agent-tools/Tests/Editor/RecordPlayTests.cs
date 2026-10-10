using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;
using UnityEngine.TestTools;

// RecordPlay's testable core. NUnit cannot enter play mode, so nothing here records a take: the recording
// itself is proven by execute_code in a real play session (docs/verify.md §Test venue). What is asserted is
// the two pieces a caller's input passes through before anything is read, each of which fails quietly in a
// live run: a column spec that parses to the wrong target records a real-looking column of the wrong thing,
// and a parameter name matched to the wrong prefixed name does the same. The emulator names the door reflects
// are EmulatorBindingCanaryTests'.
public class RecordPlayTests
{
    private static List<RecordPlay.Col> Parse(string spec)
    {
        List<RecordPlay.Col> cols;
        Assert.IsNull(RecordPlay.ParseColumns(spec, out cols), "'" + spec + "' should parse");
        return cols;
    }

    private static string Refusal(string spec)
    {
        List<RecordPlay.Col> cols;
        string bad = RecordPlay.ParseColumns(spec, out cols);
        Assert.IsNotNull(bad, "'" + spec + "' should refuse");
        return bad;
    }

    // ── Column spec ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Columns_emptySpecIsRootColumnsOnly()
    {
        Assert.AreEqual(0, Parse(null).Count);
        Assert.AreEqual(0, Parse("  ").Count);
        Assert.AreEqual(0, Parse(" ; ; ").Count);
    }

    [Test]
    public void Columns_parseEachKindWithItsHeaders()
    {
        var cols = Parse("hips=tf:Armature/Hips; mode=param:Mode; held=weights:Rig/Hold; shown=active:Body;");
        Assert.AreEqual(4, cols.Count);

        Assert.AreEqual(RecordPlay.Kind.Tf, cols[0].Kind);
        Assert.AreEqual("Armature/Hips", cols[0].Target);
        CollectionAssert.AreEqual(new[] { "hips.x", "hips.y", "hips.z", "hips.yaw" }, cols[0].Headers);

        Assert.AreEqual(RecordPlay.Kind.Param, cols[1].Kind);
        Assert.AreEqual("Mode", cols[1].Target);
        Assert.IsNull(cols[1].Channel);
        CollectionAssert.AreEqual(new[] { "mode" }, cols[1].Headers);

        // A weights column's width is its constraint's source count, known only at resolution.
        Assert.AreEqual(RecordPlay.Kind.Weights, cols[2].Kind);
        Assert.IsNull(cols[2].Headers);

        Assert.AreEqual(RecordPlay.Kind.Active, cols[3].Kind);
        CollectionAssert.AreEqual(new[] { "shown" }, cols[3].Headers);
    }

    [Test]
    public void Columns_aliasDefaultsToTheTargetsLastSegment()
    {
        var cols = Parse("tf:Armature/Hips/Spine; param:Toggles/Hat");
        Assert.AreEqual("Spine", cols[0].Alias);
        Assert.AreEqual("Hat", cols[1].Alias);
        Assert.AreEqual("Toggles/Hat", cols[1].Target);
    }

    [Test]
    public void Columns_paramChannelSplitsOffTheTarget()
    {
        var cols = Parse("a=param:Mode@playable; b=param:Mode@mirror");
        Assert.AreEqual("Mode", cols[0].Target);
        Assert.AreEqual("playable", cols[0].Channel);
        Assert.AreEqual("Mode", cols[1].Target);
        Assert.AreEqual("mirror", cols[1].Channel);

        StringAssert.Contains("playable or mirror", Refusal("param:Mode@fx"));
    }

    [Test]
    public void Columns_aChannelSuffixIsAParamThingOnly()
    {
        // An @ in a transform path is part of the name.
        Assert.AreEqual("Rig/Hold@L", Parse("tf:Rig/Hold@L")[0].Target);
    }

    [Test]
    public void Columns_refuseAMalformedEntryNamingTheForm()
    {
        StringAssert.Contains("alias=kind:target", Refusal("Armature/Hips"));
        StringAssert.Contains("alias=kind:target", Refusal("hips=bone:Armature/Hips"));
        StringAssert.Contains("alias=kind:target", Refusal("hips=tf:"));
    }

    [Test]
    public void Columns_refuseAnAliasAFixedColumnOrAnEarlierEntryTook()
    {
        foreach (var taken in new[] { "frame", "t", "dt", "who", "root" })
            StringAssert.Contains("is taken", Refusal(taken + "=param:Mode"));
        StringAssert.Contains("is taken", Refusal("tf:Armature/root"));
        StringAssert.Contains("is taken", Refusal("a=param:One; a=param:Two"));
        StringAssert.Contains("is taken", Refusal("tf:Left/Hand; tf:Right/Hand"));
    }

    [Test]
    public void Columns_refuseAnAliasThatWouldBreakTheCsv()
    {
        // A defaulted alias inherits whatever the target's last segment holds.
        StringAssert.Contains("must be non-empty", Refusal("tf:Armature/Upper Arm"));
        StringAssert.Contains("must be non-empty", Refusal("tf:Armature/"));
        Assert.AreEqual("arm", Parse("arm=tf:Armature/Upper Arm")[0].Alias);
    }

    // ── Parameter name match ──────────────────────────────────────────────────────────────────────

    private static string Match(string target, out string why, params string[] names) =>
        RecordPlay.MatchName(new HashSet<string>(names), target, out why);

    [Test]
    public void Strip_removesAVrcFuryPrefixAndAModularAvatarSuffix()
    {
        Assert.AreEqual("Mode", RecordPlay.Strip("VF12_Mode"));
        Assert.AreEqual("Mode", RecordPlay.Strip("Mode$a1b2c3"));
        Assert.AreEqual("Mode", RecordPlay.Strip("VF3_Mode$a1b2c3"));
        Assert.AreEqual("Rig/Mode", RecordPlay.Strip("Rig/Mode"));
        // Only a leading VF<digits>_ is a prefix.
        Assert.AreEqual("VFX_Mode", RecordPlay.Strip("VFX_Mode"));
        Assert.AreEqual("My_VF1_Mode", RecordPlay.Strip("My_VF1_Mode"));
    }

    [Test]
    public void MatchName_findsTheOnePrefixedName()
    {
        string why;
        Assert.AreEqual("VF12_Mode", Match("Mode", out why, "VF12_Mode", "GestureLeft"));
        Assert.IsNull(why);
        Assert.AreEqual("Mode$a1b2c3", Match("Mode", out why, "Mode$a1b2c3", "GestureLeft"));
    }

    [Test]
    public void MatchName_anExactNameWinsOverAStrippedOne()
    {
        string why;
        Assert.AreEqual("Mode", Match("Mode", out why, "Mode", "VF12_Mode"));
        Assert.AreEqual("VF12_Mode", Match("VF12_Mode", out why, "Mode", "VF12_Mode"));
    }

    [Test]
    public void MatchName_twoCandidatesRefuseNamingBoth()
    {
        string why;
        Assert.IsNull(Match("Mode", out why, "VF1_Mode", "VF2_Mode"));
        StringAssert.Contains("VF1_Mode, VF2_Mode", why);
        StringAssert.Contains("name one exactly", why);
    }

    [Test]
    public void MatchName_aMissListsTheNamesContainingTheTarget()
    {
        string why;
        Assert.IsNull(Match("Mod", out why, "VF1_Mode", "GestureLeft"));
        StringAssert.Contains("no parameter named 'Mod'", why);
        StringAssert.Contains("VF1_Mode", why);
        StringAssert.DoesNotContain("GestureLeft", why);
    }

    // ── The door outside play ─────────────────────────────────────────────────────────────────────

    [Test]
    public void EveryDoor_refusesOutsideATake()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"^\[RecordPlay\] FAIL: play mode only"));
        StringAssert.Contains("FAIL: play mode only", RecordPlay.Run("never/written.csv", ""));

        LogAssert.Expect(LogType.Error, new Regex(@"^\[RecordPlay\] FAIL: no take running"));
        StringAssert.Contains("FAIL: no take running", RecordPlay.MoveAvatar(1f, 0f, 0f, 10));
        LogAssert.Expect(LogType.Error, new Regex(@"^\[RecordPlay\] FAIL: no take running"));
        StringAssert.Contains("FAIL: no take running", RecordPlay.Mark("x"));
        LogAssert.Expect(LogType.Error, new Regex(@"^\[RecordPlay\] FAIL: no take running"));
        StringAssert.Contains("FAIL: no take running", RecordPlay.End());

        StringAssert.Contains("no take running", RecordPlay.Status());
    }
}
