using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class UploadAvatarLoopTests
{
    // Helper — build a GameObject with a VRC PipelineManager and set blueprintId via SerializedObject.
    static GameObject MakeAvatar(string name, string blueprint)
    {
        var go = new GameObject(name);
        var pm = go.AddComponent<VRC.Core.PipelineManager>();
        var so = new SerializedObject(pm);
        so.FindProperty("blueprintId").stringValue = blueprint;
        so.ApplyModifiedPropertiesWithoutUndo();
        return go;
    }

    // ClassifyAvatar only — NOT the whatIf door, despite being the closest thing to coverage of it.
    // Run(whatIf: true) is unreachable here: it refuses at CauReflect.IsAvailable (CAU is absent in this
    // venue — Run_RefusesWhenCauAbsent below is the proof) and would then have to clear CheckPreconditions
    // (SDK login, an open Build Control Panel, a Windows build target). So the whatIf ROW FORMATTING —
    // "handle=… state=… publishName=…" and the "(no PipelineManager — never uploaded)" annotation, which is
    // what a caller actually reads before publishing to a live account — has no automated coverage at all.
    // Closing that needs the row builder split out of Run, or a live-venue check.
    [Test]
    public void ClassifyAvatar_ReadsBlueprintStateAndPublishName()
    {
        var a = MakeAvatar("A", blueprint: "");
        var b = MakeAvatar("B", blueprint: "avtr_test");
        try
        {
            Assert.AreEqual("first-upload", UploadAvatar.ClassifyAvatar(a).state);
            Assert.AreEqual("update",       UploadAvatar.ClassifyAvatar(b).state);
            Assert.AreEqual("A",            UploadAvatar.ClassifyAvatar(a).publishName);
        }
        finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
    }

    [Test]
    public void Run_RefusesWhenCauAbsent()
    {
        var go = new GameObject("dummy-avatar");
        try
        {
            string result = UploadAvatar.Run(new[] { go }, whatIf: true);
            StringAssert.Contains("REFUSE", result);
            StringAssert.Contains("continuous-avatar-uploader", result);
        }
        finally { Object.DestroyImmediate(go); }
    }

    [Test]
    public void CauReflect_AbsentIsGracefulNotThrowing()
    {
        // A REAL descriptor, because that is what reaches the CAU-type lookup this test is named for.
        // Passing null short-circuits at the `desc == null` guard one line in — a different branch — leaving
        // the absent-CAU path, the one every caller in this venue takes, entirely uncovered.
        var go = new GameObject("cau-probe");
        try
        {
            var desc = go.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

            // The assertions stay OUTSIDE DoesNotThrow: an assertion failure inside the delegate is itself an
            // exception, so DoesNotThrow would report a wrong reason string as "an unexpected exception".
            bool ok = false; object setting = null; string why = null;
            Assert.DoesNotThrow(() => ok = CauReflect.TryBuildSetting(desc, out setting, out why),
                "a missing CAU must be a named false, never a class-load throw");
            Assert.IsFalse(ok, "CAU is not installed in this venue, so the setting cannot be built");
            Assert.IsNull(setting, "a failed build must hand back nothing, not a half-configured setting");
            StringAssert.Contains("CAU not installed", why,
                "the reason must name the missing package — it is the caller's only fix");

            // The null-argument leg, whose reason must stay distinguishable from CAU's absence: they call for
            // opposite fixes (pass a descriptor vs. install a package).
            Assert.IsFalse(CauReflect.TryBuildSetting(null, out _, out var whyNull));
            StringAssert.Contains("null descriptor", whyNull);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // The login self-heal's reflection half. Its state machine is covered in UploadAvatarLogicTests over an
    // injected fake; what can only be checked here is that a CAU-less venue gets a named null instead of a
    // throw — and that the reason names CAU, since "no saved credentials, go sign in" would be a wrong and
    // expensive instruction to give an operator whose credentials are fine.
    [Test]
    public void CauReflect_LoginKickAbsentIsNamedNotThrowing()
    {
        Task<bool> task = null; string why = null;
        Assert.DoesNotThrow(() => (task, why) = CauReflect.TryLoginKick(),
                            "a missing CAU must be a named null task, never a class-load throw");
        Assert.IsNull(task, "CAU is absent in this venue, so no restore can be in flight");
        StringAssert.Contains("CAU", why, "the reason must name CAU — the caller's fix is not 'sign in'");
    }

    // ── RunCore loop semantics (fake delegate — the real SDK call is live-gated to Task 8) ──────

    [Test]
    public void Loop_StopsOnFirstFailure_AndSavesSuccesses()
    {
        var avatars = new[] { MakeAvatar("A",""), MakeAvatar("B",""), MakeAvatar("C","") };
        int saveCalls = 0;
        var outcomes = new System.Collections.Generic.Queue<UploadAvatar.UploadOutcome>(new[] {
            UploadAvatar.UploadOutcome.Uploaded(),
            UploadAvatar.UploadOutcome.Failed(httpStatus: 500),
            UploadAvatar.UploadOutcome.Uploaded(),
        });
        var report = UploadAvatar.RunCore(avatars,
            _ => System.Threading.Tasks.Task.FromResult(outcomes.Dequeue()),
            () => saveCalls++).GetAwaiter().GetResult();
        Assert.AreEqual("A", report.rows[0].handle); Assert.AreEqual("uploaded", report.rows[0].result);
        Assert.AreEqual("failed", report.rows[1].result); Assert.AreEqual("transient", report.rows[1].cls);
        // C was never attempted — the halt appends a not-attempted tail so the report is complete.
        Assert.AreEqual(3, report.rows.Count);
        Assert.AreEqual("not-attempted", report.rows[2].result);
        Assert.AreEqual(1, saveCalls);
        foreach (var g in avatars) UnityEngine.Object.DestroyImmediate(g);
    }

    // The status file is the agent's ONLY channel when a modal wedges the editor mid-upload: Status() is
    // C# invoked over MCP, and a modal blocks the main thread the poll would have to run on. Progress must
    // therefore be announced BEFORE the upload that could wedge, naming the avatar — announcing after would
    // leave the agent reading a stall with no idea which handle caused it.
    [Test]
    public void Loop_AnnouncesEachAvatarBeforeAttemptingIt()
    {
        var avatars = new[] { MakeAvatar("A",""), MakeAvatar("B","") };
        var announced = new System.Collections.Generic.List<string>();
        var attempted = new System.Collections.Generic.List<string>();
        var report = UploadAvatar.RunCore(avatars,
            go => { attempted.Add(go.name);
                    // At the moment of the attempt, this handle must ALREADY have been announced.
                    Assert.AreEqual(go.name, announced[announced.Count - 1],
                        "progress must be announced before the upload that can raise a modal");
                    return System.Threading.Tasks.Task.FromResult(UploadAvatar.UploadOutcome.Uploaded()); },
            () => {},
            (i, name) => announced.Add(name)).GetAwaiter().GetResult();
        CollectionAssert.AreEqual(new[]{"A","B"}, announced);
        CollectionAssert.AreEqual(new[]{"A","B"}, attempted);
        Assert.AreEqual("PASS", report.result);
        foreach (var g in avatars) UnityEngine.Object.DestroyImmediate(g);
    }

    // A halt must not announce the avatars it never tried — the status file would otherwise name a handle
    // the batch never touched as the one in flight.
    [Test]
    public void Loop_DoesNotAnnounceNotAttemptedTail()
    {
        var avatars = new[] { MakeAvatar("A",""), MakeAvatar("B","") };
        var announced = new System.Collections.Generic.List<string>();
        UploadAvatar.RunCore(avatars,
            _ => System.Threading.Tasks.Task.FromResult(UploadAvatar.UploadOutcome.Failed(httpStatus: 500)),
            () => {},
            (i, name) => announced.Add(name)).GetAwaiter().GetResult();
        CollectionAssert.AreEqual(new[]{"A"}, announced, "B was never attempted, so it must never be announced");
        foreach (var g in avatars) UnityEngine.Object.DestroyImmediate(g);
    }

    // The status file is a FIXED path, so a reader cannot tell this batch's record from the previous
    // batch's without an identity to match. That stale read is the catastrophic one: if the editor is
    // already wedged when Run is issued, the call times out, the agent falls back to the file, and sees
    // the last batch's "done-pass" — concluding an upload that never started succeeded, on an operation
    // that publishes to a live account.
    [Test]
    public void StatusRecord_CarriesTheBatchId()
    {
        var json = UploadAvatar.BuildStatusJson("abc123", "running", "uploading", 0, 2, "AvatarA", "T");
        StringAssert.Contains("\"batchId\":\"abc123\"", json,
            "without an id the record is indistinguishable from a stale one");
    }

    // The verdict must live in `phase`, not only in the human-readable message: this file is machine-read
    // from Bash during a wedge, so a bare "done" would make a FAILED batch look finished-and-fine.
    [Test]
    public void StatusRecord_PutsTheVerdictInPhase()
    {
        StringAssert.Contains("\"phase\":\"done-pass\"",
            UploadAvatar.BuildStatusJson("b", "done-pass", "ok", -1, 1, null, "T"));
        StringAssert.Contains("\"phase\":\"done-fail\"",
            UploadAvatar.BuildStatusJson("b", "done-fail", "nope", -1, 1, null, "T"));
    }

    // The status file is written to disk and read by an external poller — a leaked blueprint id there is
    // as public as one in the RunLog, and message carries SDK exception text.
    [Test]
    public void StatusRecord_RedactsIdsInTheMessage()
    {
        var json = UploadAvatar.BuildStatusJson("b", "done-fail",
            "failed avtr_00000000-1111-2222-3333-444444444444", -1, 1, null, "T");
        StringAssert.DoesNotContain("avtr_00000000-1111-2222-3333-444444444444", json);
    }

    // Avatar names are user data and land in a JSON field — an unescaped quote would produce a record the
    // Bash-side reader cannot parse, exactly when it is the only channel left.
    [Test]
    public void StatusRecord_EscapesAHostileHandle()
    {
        var json = UploadAvatar.BuildStatusJson("b", "running", "uploading", 0, 1, "He said \"hi\"\\n", "T");
        StringAssert.Contains("\\\"hi\\\"", json);
        Assert.DoesNotThrow(() => UnityEngine.JsonUtility.FromJson<StatusProbe>(json),
            "the record must stay parseable with a hostile handle");
    }

    private class StatusProbe { public string batchId; public string phase; public string handle; }

    [Test]
    public void Loop_RateLimitIsNotAutoRetried()
    {
        var a = MakeAvatar("A","");
        int calls = 0;
        var report = UploadAvatar.RunCore(new[]{a},
            _ => { calls++; return System.Threading.Tasks.Task.FromResult(UploadAvatar.UploadOutcome.Failed(httpStatus: 429)); },
            () => {}).GetAwaiter().GetResult();
        Assert.AreEqual(1, calls);
        Assert.AreEqual("rate-limit", report.rows[0].cls);
        UnityEngine.Object.DestroyImmediate(a);
    }

    [Test]
    public void Loop_ReservedNoBundleIsItsOwnState()
    {
        var a = MakeAvatar("A","");
        var report = UploadAvatar.RunCore(new[]{a},
            _ => System.Threading.Tasks.Task.FromResult(UploadAvatar.UploadOutcome.ReservedNoBundle()),
            () => {}).GetAwaiter().GetResult();
        Assert.AreEqual("reserved-no-bundle", report.rows[0].result);
        UnityEngine.Object.DestroyImmediate(a);
    }

    [Test]
    public void Loop_RedactsErrorIds()
    {
        var a = MakeAvatar("A","");
        var report = UploadAvatar.RunCore(new[]{a},
            _ => System.Threading.Tasks.Task.FromResult(UploadAvatar.UploadOutcome.Failed(httpStatus:400, message:"rejected avtr_dead for usr_beef")),
            () => {}).GetAwaiter().GetResult();
        StringAssert.DoesNotContain("avtr_", report.rows[0].error);
        StringAssert.DoesNotContain("usr_",  report.rows[0].error);
        UnityEngine.Object.DestroyImmediate(a);
    }

    [Test]
    public void Loop_ForcedClassRealForLocalFailure()
    {
        var a = MakeAvatar("A","");
        var report = UploadAvatar.RunCore(new[]{a},
            _ => System.Threading.Tasks.Task.FromResult(
                UploadAvatar.UploadOutcome.Failed(message:"no descriptor", forcedClass:"real")),
            () => {}).GetAwaiter().GetResult();
        Assert.AreEqual("real", report.rows[0].cls);
        UnityEngine.Object.DestroyImmediate(a);
    }

    [Test]
    public void ClassifyAvatar_NullReturnsNamedRow_NoThrow()
    {
        Assert.DoesNotThrow(() => {
            var r = UploadAvatar.ClassifyAvatar(null);
            Assert.AreEqual("unknown", r.state);
            Assert.AreEqual("(null)", r.publishName);
        });
    }

    // A fake SDK exception exposing an int StatusCode — proves the now-reachable classification path
    // (CauReflect.UploadOne no longer swallows, so RealUploadOne's catch actually runs).
    class FakeApiException : System.Exception
    {
        public int StatusCode { get; set; }
        public FakeApiException(int s, string m, System.Exception inner = null) : base(m, inner) { StatusCode = s; }
    }

    [Test]
    public void FailedFromException_MapsStatus()
    {
        var o429 = UploadAvatar.FailedFromException(new FakeApiException(429, "rate limited avtr_x"));
        Assert.AreEqual("rate-limit", UploadAvatarLogic.Classify(o429.httpStatus, o429.isValidation, o429.isTimeout));
        var o400 = UploadAvatar.FailedFromException(new FakeApiException(400, "bad"));
        Assert.AreEqual("real", UploadAvatarLogic.Classify(o400.httpStatus, o400.isValidation, o400.isTimeout));
    }

    [Test]
    public void FailedFromException_StatusOnOuter_WrappingInner_StillClassifies()
    {
        // The exact A bug: 429 on the outer, an inner transport cause present.
        var o = UploadAvatar.FailedFromException(new FakeApiException(429, "rate limited", new System.Exception("transport")));
        Assert.AreEqual("rate-limit", UploadAvatarLogic.Classify(o.httpStatus, o.isValidation, o.isTimeout));
    }

    [Test]
    public void FailedFromException_StatusOnInner_IsFound()
    {
        var o = UploadAvatar.FailedFromException(new System.Exception("wrapper", new FakeApiException(400, "bad")));
        Assert.AreEqual("real", UploadAvatarLogic.Classify(o.httpStatus, o.isValidation, o.isTimeout));
    }

    [Test]
    public void FailedFromException_NoSignal_IsNonRetryableReal()
    {
        var o = UploadAvatar.FailedFromException(new System.InvalidOperationException("CAU drift"));
        Assert.AreEqual("real", o.forcedClass);   // fail-safe default, not transient
    }

    // ----- id-persistence verdict (F39) ---------------------------------------------------------
    // Loop_ReservedNoBundleIsItsOwnState above proves the LOOP routes the outcome; these prove what
    // PRODUCES it. That decision's only caller sits behind `await CauReflect.UploadOne` — a real upload
    // to VRChat — so before extraction no test could fail on it however wrong it got.

    // The one cell that means "record reserved, no bundle landed": first-upload going in, still
    // first-upload after SaveAssets persisted whatever CAU wrote back.
    [Test]
    public void IsReservedNoBundle_firstUploadWhoseIdNeverPersisted()
    {
        Assert.IsTrue(UploadAvatar.IsReservedNoBundle(true, "first-upload"));
    }

    // Every other cell is an ordinary success. ClassifyAvatar returns THREE states, not two, so this is a
    // 2x3 table: "unknown" is what a GameObject destroyed across the await classifies as, and it is
    // reachable on both rows. Covering it is what rules out `wasFirstUpload && stateAfter != "update"` — a
    // mutant that survives every other case here while reporting a destroyed first-upload as a reservation,
    // which would halt the batch on an upload that landed.
    //
    // The (was-update -> reads first-upload) cell is the other one worth naming: an id that went missing
    // locally after an upload that did land is an anomaly, not a reservation.
    //
    // The null cell is the caller's short-circuit — RealUploadOne re-reads the state only when
    // wasFirstUpload, so a re-upload passes null rather than paying a second SerializedObject read.
    [TestCase(true,  "update",       TestName = "IsReservedNoBundle_firstUploadThatPersisted_isPlainUpload")]
    [TestCase(true,  "unknown",      TestName = "IsReservedNoBundle_firstUploadWhoseObjectDied_isNotAReservation")]
    [TestCase(false, "update",       TestName = "IsReservedNoBundle_ordinaryReupload_isPlainUpload")]
    [TestCase(false, "unknown",      TestName = "IsReservedNoBundle_reuploadWhoseObjectDied_isPlainUpload")]
    [TestCase(false, "first-upload", TestName = "IsReservedNoBundle_updateWhoseIdWentMissing_isNotAReservation")]
    [TestCase(false, null,           TestName = "IsReservedNoBundle_stateNotEvenRead_isPlainUpload")]
    public void IsReservedNoBundle_everyOtherCell_isPlainUpload(bool wasFirstUpload, string stateAfter)
    {
        Assert.IsFalse(UploadAvatar.IsReservedNoBundle(wasFirstUpload, stateAfter));
    }
}
