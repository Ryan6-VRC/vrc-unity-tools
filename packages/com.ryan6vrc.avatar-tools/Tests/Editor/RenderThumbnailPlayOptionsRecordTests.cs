// Pins the Enter-Play-Mode Options record codec RenderThumbnailPlay writes to SessionState — the pair of
// pure decisions that a domain reload separates, and that the live path therefore cannot prove agree.
//
// Why these exist as their own cases rather than riding along: the record is written in one Begin and read
// by whatever Begin runs after a recompile wiped the statics. A batchmode test can't provoke that reload, so
// a writer and a parser that disagreed on the format would present as "no record survived" — which is the
// recoverable-looking symptom whose end state is the operator's project settings cemented to our forced
// both-reload-disabled pair. Nothing in an end-to-end run can fail on that; only a direct round-trip can.
// Same omission shape as ImportPackage.NameMatches before F36: a pure decision inside a boundary the suite
// legitimately can't exercise, riding on its untestable caller instead of carrying its own case.
using NUnit.Framework;
using UnityEditor;
using Ryan6Vrc.AvatarTools.Editor;

public class RenderThumbnailPlayOptionsRecordTests
{
    // The load-bearing one: every pair the writer can emit must come back out of the parser unchanged.
    // "No writer can emit a malformed record" is an assertion the code comment makes; this is what keeps it true.
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

    // A malformed record is rejected WHOLE. Restoring half a settings pair is worse than refusing to restore,
    // because the operator's other half is then silently replaced by a default.
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

    // "|3" parses arity-wise but its enabled field is not "1" — that is a legitimate false, not a rejection.
    // Pinned separately from the malformed set so the arity rule and the flag rule can't be conflated.
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

    // Begin's `optsChanged` inverts this: when the operator is ALREADY both-reload-disabled we changed
    // nothing, and End must not claim to restore anything. Getting it wrong is how a forced pair gets
    // mistaken for the operator's own setting.
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

    // The forced pair ApplyForcedOptions writes must be the same pair IsBothReloadDisabled recognises.
    // If these two ever drift apart, every Begin reports `playmode-reload=disabled` and then restores
    // over the operator's settings on a state it never actually changed.
    [Test]
    public void ForcedPair_isRecognisedAsAlreadyForced()
    {
        var forced = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        Assert.IsTrue(RenderThumbnailPlay.IsBothReloadDisabled(true, forced),
            "ApplyForcedOptions' pair must read back as already-forced, or optsChanged inverts");
    }
}
