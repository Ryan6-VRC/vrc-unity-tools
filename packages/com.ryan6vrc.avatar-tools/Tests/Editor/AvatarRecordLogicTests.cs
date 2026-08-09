using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

public class AvatarRecordLogicTests
{
    // ── REFUSE register ─────────────────────────────────────────────────────────────────────────

    // Each status maps to a DIFFERENT instruction, which is the whole point of splitting them: "sign in",
    // "not yours", "check the blueprintId" and "wait" are opposite next moves, so collapsing any two would
    // leave a reader unable to act on the refusal.
    [Test] public void Refuse_401_NamesSignIn()
        => StringAssert.Contains("sign in", AvatarRecordLogic.RefuseForStatus(401, null));
    [Test] public void Refuse_403_NamesOwnership()
        => StringAssert.Contains("not yours", AvatarRecordLogic.RefuseForStatus(403, null));
    [Test] public void Refuse_404_NamesBlueprintId()
        => StringAssert.Contains("blueprintId", AvatarRecordLogic.RefuseForStatus(404, null));
    [Test] public void Refuse_429_SaysDoNotLoop()
        => StringAssert.Contains("do not retry in a loop", AvatarRecordLogic.RefuseForStatus(429, null));
    [Test] public void Refuse_5xx_IsNamedTransient()
        => StringAssert.Contains("transient", AvatarRecordLogic.RefuseForStatus(503, null));

    // 404 must not claim "not found": the API returns it both for a nonexistent record and for one this
    // account cannot see, and the door cannot distinguish them. Asserting the wrong one sends the reader
    // hunting for a deleted avatar when the real cause is the wrong account.
    [Test] public void Refuse_404_DoesNotAssertNonexistence()
    {
        var r = AvatarRecordLogic.RefuseForStatus(404, null);
        StringAssert.Contains("not visible to this account", r);
    }

    // The server's real text lives in ErrorMessage and is the only content-bearing part of the failure —
    // it must survive into the refusal, scrubbed.
    [Test] public void Refuse_ForwardsServerMessage()
        => StringAssert.Contains("This avatar is unavailable.",
                                 AvatarRecordLogic.RefuseForStatus(404, "This avatar is unavailable."));

    [Test] public void Refuse_ScrubsIdsOutOfServerMessage()
    {
        var r = AvatarRecordLogic.RefuseForStatus(404, "no avtr_9f3c1a2b-0000-1111-2222-333344445555 here");
        StringAssert.DoesNotContain("avtr_", r);
    }

    [Test] public void Refuse_NoStatusStillSaysSomethingTrue()
        => StringAssert.Contains("no HTTP status", AvatarRecordLogic.RefuseForStatus(null, null));

    // ── Name validation ─────────────────────────────────────────────────────────────────────────

    [Test] public void Validate_NullRejected() => Assert.IsNotNull(AvatarRecordLogic.ValidateNewName(null));
    [Test] public void Validate_EmptyRejected() => Assert.IsNotNull(AvatarRecordLogic.ValidateNewName("   "));
    [Test] public void Validate_UntrimmedRejected() => Assert.IsNotNull(AvatarRecordLogic.ValidateNewName(" x "));
    [Test] public void Validate_OrdinaryNameAccepted() => Assert.IsNull(AvatarRecordLogic.ValidateNewName("Probe 0.2s"));

    // ── Server-side sanitization ────────────────────────────────────────────────────────────────

    [Test] public void Landing_ExactIsReportedExact()
        => StringAssert.Contains("exactly", AvatarRecordLogic.DescribeNameLanding("Probe A", "Probe A"));

    // The measured case: ASCII period (U+002E) submitted, ONE DOT LEADER (U+2024) landed. The door must
    // flag it AND show the code points — the two strings are visually identical, so a plain "submitted X,
    // landed X" would read as a no-op bug rather than a substitution.
    [Test] public void Landing_SanitizedIsFlaggedWithCodePoints()
    {
        var r = AvatarRecordLogic.DescribeNameLanding("Probe 0.2s", "Probe 0․2s");
        StringAssert.Contains("SANITIZED", r);
        StringAssert.Contains("U+2024", r);
    }

    [Test] public void CodePoints_RendersEachChar()
        => Assert.AreEqual("[U+0041 U+2024]", AvatarRecordLogic.CodePoints("A․"));

    // ── Compare-and-swap guard ──────────────────────────────────────────────────────────────────

    [Test] public void Cas_MatchPasses()
        => Assert.IsNull(AvatarRecordLogic.CheckExpectedName("Probe A", "Probe A"));

    [Test] public void Cas_NullExpectationRefusesAndNamesTheReadDoor()
    {
        var r = AvatarRecordLogic.CheckExpectedName(null, "Probe A");
        Assert.IsNotNull(r);
        StringAssert.Contains("ReportAvatarRecord", r);
    }

    // The guard's reason for existing: a stale blueprintId retargets the write onto a different live
    // record. The refusal must show BOTH names, or the caller cannot tell which of the two is wrong.
    [Test] public void Cas_MismatchRefusesShowingBothNames()
    {
        var r = AvatarRecordLogic.CheckExpectedName("Probe A", "Someone Else's Avatar");
        Assert.IsNotNull(r);
        StringAssert.Contains("Probe A", r);
        StringAssert.Contains("Someone Else's Avatar", r);
    }

    // A caller who typed the ASCII form of a name the server sanitized must be REFUSED, not silently
    // accepted — otherwise the guard passes on a name that is not the live one and stops proving anything.
    [Test] public void Cas_SanitizedHomoglyphIsNotTreatedAsEqual()
    {
        var r = AvatarRecordLogic.CheckExpectedName("Probe 0.2s", "Probe 0․2s");
        Assert.IsNotNull(r);
        StringAssert.Contains("U+2024", r);
    }
}
