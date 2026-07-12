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
    [Test] public void Classify_TimeoutIsTransient()
        => Assert.AreEqual("transient", UploadAvatarLogic.Classify(null, false, true));
    [Test] public void Classify_OtherClientErrorIsReal()
        => Assert.AreEqual("real", UploadAvatarLogic.Classify(403, false, false));

    [Test] public void Ledger_CapsAtThree()
    {
        var l = new UploadAvatarLogic.AttemptLedger();
        for (int i = 0; i < 3; i++) { Assert.IsTrue(l.MayAttempt("a")); l.Record("a"); }
        Assert.IsFalse(l.MayAttempt("a"));
        Assert.IsTrue(l.MayAttempt("b"));
    }

    [Test] public void Blueprint_EmptyIsFirstUpload()
    {
        Assert.AreEqual("first-upload", UploadAvatarLogic.ClassifyBlueprint(null));
        Assert.AreEqual("first-upload", UploadAvatarLogic.ClassifyBlueprint(""));
        Assert.AreEqual("update", UploadAvatarLogic.ClassifyBlueprint("avtr_x"));
    }
}
