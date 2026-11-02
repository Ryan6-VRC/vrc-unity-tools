// Pins the Enter-Play-Mode Options record codec RenderThumbnailPlay writes to SessionState. One Run writes the
// record and whatever Run follows a domain reload reads it, which a batchmode test can't provoke, so a writer
// and parser that disagreed would present only as "no record survived", whose end state is the operator's
// project settings cemented to the forced both-reload-disabled pair. Only a direct round-trip can fail on that.
using NUnit.Framework;
using UnityEditor;
using Ryan6Vrc.AvatarTools.Editor;

public class RenderThumbnailPlayOptionsRecordTests
{
    [TestCase(true, EnterPlayModeOptions.None)]
    [TestCase(false, EnterPlayModeOptions.None)]
    [TestCase(true, EnterPlayModeOptions.DisableDomainReload)]
    [TestCase(true, EnterPlayModeOptions.DisableSceneReload)]
    [TestCase(true, EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload)]
    [TestCase(false, EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload)]
    public void OptionsRecord_roundTrips(bool enabled, EnterPlayModeOptions opts)
    {
        string raw = RenderThumbnailPlay.FormatOptionsRecord(enabled, opts);

        bool gotEnabled;
        EnterPlayModeOptions gotOpts;
        Assert.IsTrue(RenderThumbnailPlay.TryParseOptionsRecord(raw, out gotEnabled, out gotOpts),
            "a record this codec wrote must parse: '" + raw + "'");
        Assert.AreEqual(enabled, gotEnabled, "enabled flag survived the round-trip");
        Assert.AreEqual(opts, gotOpts, "options survived the round-trip");
    }

    // Restoring half a settings pair would silently replace the operator's other half with a default.
    [TestCase("", TestName = "OptionsRecord_rejects_empty")]
    [TestCase(null, TestName = "OptionsRecord_rejects_null")]
    [TestCase("1", TestName = "OptionsRecord_rejects_missingOptionsField")]
    [TestCase("1|2|3", TestName = "OptionsRecord_rejects_extraField")]
    [TestCase("1|", TestName = "OptionsRecord_rejects_emptyOptionsField")]
    [TestCase("1|notAnInt", TestName = "OptionsRecord_rejects_nonIntegerOptions")]
    public void OptionsRecord_malformed_isRejectedWhole(string raw)
    {
        bool enabled = true;
        EnterPlayModeOptions opts = EnterPlayModeOptions.DisableDomainReload;

        bool ok = RenderThumbnailPlay.TryParseOptionsRecord(raw, out enabled, out opts);

        Assert.IsFalse(ok, "malformed record must not parse: '" + (raw ?? "<null>") + "'");
        Assert.IsFalse(enabled, "a rejected record leaves no half-applied enabled flag");
        Assert.AreEqual(default(EnterPlayModeOptions), opts, "a rejected record leaves no half-applied options");
    }

    // A wrong-valued enabled field parses as a legitimate false, not a rejection: the arity rule and the flag
    // rule are separate.
    [Test]
    public void OptionsRecord_enabledField_isExactlyOne_notTruthiness()
    {
        bool enabled;
        EnterPlayModeOptions opts;

        Assert.IsTrue(RenderThumbnailPlay.TryParseOptionsRecord("0|3", out enabled, out opts));
        Assert.IsFalse(enabled, "'0' is disabled");

        Assert.IsTrue(RenderThumbnailPlay.TryParseOptionsRecord("true|3", out enabled, out opts));
        Assert.IsFalse(enabled, "only the literal '1' means enabled — the writer emits nothing else");
    }

    // Run's `optsChanged` inverts this: when the operator is ALREADY both-reload-disabled we changed nothing,
    // and its summary must not promise a restore on End.
    [Test]
    public void IsBothReloadDisabled_requiresEnabled_andBothFlags()
    {
        var both = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;

        Assert.IsTrue(RenderThumbnailPlay.IsBothReloadDisabled(true, both));

        Assert.IsFalse(RenderThumbnailPlay.IsBothReloadDisabled(false, both),
            "the options toggle being off means the flags are inert");
        Assert.IsFalse(RenderThumbnailPlay.IsBothReloadDisabled(true, EnterPlayModeOptions.DisableDomainReload),
            "domain reload alone is not the forced state");
        Assert.IsFalse(RenderThumbnailPlay.IsBothReloadDisabled(true, EnterPlayModeOptions.DisableSceneReload),
            "scene reload alone is not the forced state");
        Assert.IsFalse(RenderThumbnailPlay.IsBothReloadDisabled(true, EnterPlayModeOptions.None));
    }

    [Test]
    public void OptionsEndRow_claimsARestoreOnlyWhenRunChangedThePair()
    {
        StringAssert.Contains("restored", RenderThumbnailPlay.OptionsEndRow(false, EnterPlayModeOptions.None));
        StringAssert.Contains("left as found", RenderThumbnailPlay.OptionsEndRow(true, RenderThumbnailPlay.ForcedOptions));
    }

    // Reads the production constant rather than restating it: a second copy here would keep passing after
    // ForcedOptions was narrowed to one flag.
    [Test]
    public void ForcedPair_isRecognisedAsAlreadyForced()
    {
        Assert.IsTrue(RenderThumbnailPlay.IsBothReloadDisabled(true, RenderThumbnailPlay.ForcedOptions),
            "ApplyForcedOptions' pair must read back as already-forced, or optsChanged inverts");
    }

    // The round-trip proves the two halves AGREE; it cannot see a format both halves changed together.
    // That matters because the hazard is intra-session — a recompile is what causes the reload, so build N
    // writes the record and build N+1 reads it. Pin the wire format itself.
    [Test]
    public void OptionsRecord_wireFormat_isEnabledFlagPipeOptionsInt()
    {
        Assert.AreEqual("1|3", RenderThumbnailPlay.FormatOptionsRecord(true, RenderThumbnailPlay.ForcedOptions),
            "DisableDomainReload(1)|DisableSceneReload(2) = 3 — a later build must still read this record");
        Assert.AreEqual("0|0", RenderThumbnailPlay.FormatOptionsRecord(false, EnterPlayModeOptions.None));
    }
}
