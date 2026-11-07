using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class UploadAvatarLoopTests
{
    static GameObject MakeAvatar(string name, string blueprint)
    {
        var go = new GameObject(name);
        var pm = go.AddComponent<VRC.Core.PipelineManager>();
        var so = new SerializedObject(pm);
        so.FindProperty("blueprintId").stringValue = blueprint;
        so.ApplyModifiedPropertiesWithoutUndo();
        return go;
    }

    // The local read only. Run(whatIf: true) refuses at CauReflect.IsAvailable because CAU is absent in
    // this venue (Run_RefusesWhenCauAbsent), so what a caller reads before publishing is covered through
    // WhatIfCore and Preflight.
    [Test]
    public void ClassifyAvatar_ReadsBlueprintStateAndSceneName()
    {
        var a = MakeAvatar("A", blueprint: "");
        var b = MakeAvatar("B", blueprint: "avtr_test");
        try
        {
            Assert.AreEqual("first-upload", UploadAvatar.ClassifyAvatar(a).state);
            Assert.AreEqual("update",       UploadAvatar.ClassifyAvatar(b).state);
            Assert.AreEqual("A",            UploadAvatar.ClassifyAvatar(a).sceneName);
            Assert.AreEqual("B",            UploadAvatar.ClassifyAvatar(b).sceneName,
                "the scene name, not what an update publishes under: only the live read knows that");
        }
        finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
    }

    // ── Live-record preflight (fake read — the real one is a GET against a live account) ─────

    const string Me = "usr_me";

    static System.Func<string, Task<UploadAvatarLogic.RecordRead>> Reads(
        System.Collections.Generic.Dictionary<string, UploadAvatarLogic.RecordRead> byId,
        System.Collections.Generic.List<string> asked = null)
        => id =>
        {
            if (asked != null) asked.Add(id);
            return Task.FromResult(byId.TryGetValue(id, out var r) ? r : UploadAvatarLogic.RecordRead.NotFound());
        };

    [Test]
    public void Preflight_ClassifiesFromTheLiveRecord_AndNeverFetchesAnIdlessRow()
    {
        var fresh = MakeAvatar("Fresh", blueprint: "");
        var live = MakeAvatar("SceneName", blueprint: "avtr_live");
        var gone = MakeAvatar("Gone", blueprint: "avtr_gone");
        var asked = new System.Collections.Generic.List<string>();
        try
        {
            var sights = UploadAvatar.Preflight(new[] { fresh, live, gone, null },
                Reads(new System.Collections.Generic.Dictionary<string, UploadAvatarLogic.RecordRead>
                {
                    ["avtr_live"] = UploadAvatarLogic.RecordRead.Found("Published Name", Me, false),
                }, asked), Me).GetAwaiter().GetResult();

            Assert.AreEqual("first-upload", sights[0].state);
            Assert.AreEqual("Fresh", sights[0].publishName);
            Assert.AreEqual("update", sights[1].state);
            Assert.AreEqual("Published Name", sights[1].publishName, "an update publishes under the LIVE name");
            Assert.AreEqual("not-visible", sights[2].state);
            Assert.IsTrue(sights[2].Blocks);
            Assert.IsTrue(sights[3].Blocks, "a null entry stops the batch");
            CollectionAssert.AreEqual(new[] { "avtr_live", "avtr_gone" }, asked);
        }
        finally { Object.DestroyImmediate(fresh); Object.DestroyImmediate(live); Object.DestroyImmediate(gone); }
    }

    [Test]
    public void RunBatch_UploadsNothingWhenARowIsNotVisible()
    {
        var ok = MakeAvatar("Ok", blueprint: "avtr_ok");
        var gone = MakeAvatar("Gone", blueprint: "avtr_gone");
        int uploads = 0;
        try
        {
            var report = UploadAvatar.RunBatch(new[] { ok, gone },
                Reads(new System.Collections.Generic.Dictionary<string, UploadAvatarLogic.RecordRead>
                {
                    ["avtr_ok"] = UploadAvatarLogic.RecordRead.Found("Ok", Me, false),
                }), Me,
                go => { uploads++; return Task.FromResult(UploadAvatar.UploadOutcome.Uploaded()); },
                () => { }).GetAwaiter().GetResult();

            Assert.AreEqual(0, uploads, "the row AHEAD of the offender must not upload either");
            Assert.AreEqual("FAIL", report.result);
            Assert.AreEqual("not-attempted", report.rows[0].result);
            Assert.AreEqual("failed", report.rows[1].result);
            Assert.AreEqual("real", report.rows[1].cls);
            StringAssert.Contains("clear the id yourself", report.rows[1].error);
            StringAssert.DoesNotContain("avtr_", report.rows[1].error);
        }
        finally { Object.DestroyImmediate(ok); Object.DestroyImmediate(gone); }
    }

    [Test]
    public void RunBatch_AFailedReadTakesItsClassFromTheStatus()
    {
        var a = MakeAvatar("A", blueprint: "avtr_a");
        try
        {
            var report = UploadAvatar.RunBatch(new[] { a },
                Reads(new System.Collections.Generic.Dictionary<string, UploadAvatarLogic.RecordRead>
                {
                    ["avtr_a"] = UploadAvatarLogic.RecordRead.Failed(503, "service unavailable"),
                }), Me,
                go => Task.FromResult(UploadAvatar.UploadOutcome.Uploaded()),
                () => { }).GetAwaiter().GetResult();

            Assert.AreEqual("failed", report.rows[0].result);
            Assert.AreEqual("transient", report.rows[0].cls);

            // No status is no signal: never retryable.
            report = UploadAvatar.RunBatch(new[] { a },
                Reads(new System.Collections.Generic.Dictionary<string, UploadAvatarLogic.RecordRead>
                {
                    ["avtr_a"] = UploadAvatarLogic.RecordRead.Failed(null, "no answer"),
                }), Me,
                go => Task.FromResult(UploadAvatar.UploadOutcome.Uploaded()),
                () => { }).GetAwaiter().GetResult();
            Assert.AreEqual("real", report.rows[0].cls);
        }
        finally { Object.DestroyImmediate(a); }
    }

    [Test]
    public void RunBatch_RowsCarryThePreflightState()
    {
        var pending = MakeAvatar("Pending", blueprint: "avtr_p");
        try
        {
            var report = UploadAvatar.RunBatch(new[] { pending },
                Reads(new System.Collections.Generic.Dictionary<string, UploadAvatarLogic.RecordRead>
                {
                    ["avtr_p"] = UploadAvatarLogic.RecordRead.Found("Pending", Me, true),
                }), Me,
                go => Task.FromResult(UploadAvatar.UploadOutcome.Uploaded()),
                () => { }).GetAwaiter().GetResult();

            Assert.AreEqual("PASS", report.result);
            Assert.AreEqual("pending-upload", report.rows[0].state);
        }
        finally { Object.DestroyImmediate(pending); }
    }

    [Test]
    public void WhatIfCore_PrintsTheLiveNameAndTheImportCount()
    {
        var live = MakeAvatar("SceneName", blueprint: "avtr_live");
        try
        {
            string line = UploadAvatar.WhatIfCore(new[] { live },
                Reads(new System.Collections.Generic.Dictionary<string, UploadAvatarLogic.RecordRead>
                {
                    ["avtr_live"] = UploadAvatarLogic.RecordRead.Found("Published Name", Me, false),
                }), Me, go => (2, 0)).GetAwaiter().GetResult();

            StringAssert.Contains("handle=SceneName state=update publishName=\"Published Name\" importOffenders=2", line);
            StringAssert.Contains("ConformImportSettings.Run", line);
            StringAssert.EndsWith("=> PASS", line, "an import offender is reported, not failed");
        }
        finally { Object.DestroyImmediate(live); }
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
        // A real descriptor: null short-circuits at the `desc == null` guard and never reaches the CAU-type
        // lookup.
        var go = new GameObject("cau-probe");
        try
        {
            var desc = go.AddComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();

            // The assertions stay outside DoesNotThrow: an assertion failure inside the delegate is itself an
            // exception, which DoesNotThrow would report as "an unexpected exception".
            bool ok = false; object setting = null; string why = null;
            Assert.DoesNotThrow(() => ok = CauReflect.TryBuildSetting(desc, out setting, out why),
                "a missing CAU must be a named false, never a class-load throw");
            Assert.IsFalse(ok, "CAU is not installed in this venue, so the setting cannot be built");
            Assert.IsNull(setting, "a failed build must hand back nothing, not a half-configured setting");
            StringAssert.Contains("CAU not installed", why,
                "the reason must name the missing package — it is the caller's only fix");

            // The null-argument reason must stay distinguishable from CAU's absence: they call for different
            // fixes.
            Assert.IsFalse(CauReflect.TryBuildSetting(null, out _, out var whyNull));
            StringAssert.Contains("null descriptor", whyNull);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // The login self-heal's reflection half; UploadAvatarLogicTests covers its state machine. The reason
    // must name CAU, since "no saved credentials, go sign in" would misdirect an operator whose credentials
    // are fine.
    [Test]
    public void CauReflect_LoginKickAbsentIsNamedNotThrowing()
    {
        Task<bool> task = null; string why = null;
        Assert.DoesNotThrow(() => (task, why) = CauReflect.TryLoginKick(),
                            "a missing CAU must be a named null task, never a class-load throw");
        Assert.IsNull(task, "CAU is absent in this venue, so no restore can be in flight");
        StringAssert.Contains("CAU", why, "the reason must name CAU — the caller's fix is not 'sign in'");
    }

    // ── RunCore loop semantics (fake delegate — the real SDK call is live-only) ─────────────────

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
        Assert.AreEqual(3, report.rows.Count);
        Assert.AreEqual("not-attempted", report.rows[2].result);
        Assert.AreEqual(1, saveCalls);
        foreach (var g in avatars) UnityEngine.Object.DestroyImmediate(g);
    }

    // The status file is the agent's only channel when a modal wedges the editor mid-upload, because a
    // modal blocks the main thread Status() runs on. Announcing after the attempt would leave a stall
    // naming no handle.
    [Test]
    public void Loop_AnnouncesEachAvatarBeforeAttemptingIt()
    {
        var avatars = new[] { MakeAvatar("A",""), MakeAvatar("B","") };
        var announced = new System.Collections.Generic.List<string>();
        var attempted = new System.Collections.Generic.List<string>();
        var report = UploadAvatar.RunCore(avatars,
            go => { attempted.Add(go.name);
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

    // The status file would otherwise name a handle the batch never touched as the one in flight.
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

    // The status file's path is fixed, so only the batchId tells this batch's record from a stale "done-pass"
    // that would report an upload that never started as a success.
    [Test]
    public void StatusRecord_CarriesTheBatchId()
    {
        var json = UploadAvatar.BuildStatusJson("abc123", "running", "uploading", 0, 2, "AvatarA", "T");
        StringAssert.Contains("\"batchId\":\"abc123\"", json,
            "without an id the record is indistinguishable from a stale one");
    }

    // This file is machine-read from Bash during a wedge, so a bare "done" would make a failed batch look
    // fine.
    [Test]
    public void StatusRecord_PutsTheVerdictInPhase()
    {
        StringAssert.Contains("\"phase\":\"done-pass\"",
            UploadAvatar.BuildStatusJson("b", "done-pass", "ok", -1, 1, null, "T"));
        StringAssert.Contains("\"phase\":\"done-fail\"",
            UploadAvatar.BuildStatusJson("b", "done-fail", "nope", -1, 1, null, "T"));
    }

    // The message carries SDK exception text, and a blueprint id in this file is as public as one in the
    // RunLog.
    [Test]
    public void StatusRecord_RedactsIdsInTheMessage()
    {
        var json = UploadAvatar.BuildStatusJson("b", "done-fail",
            "failed avtr_00000000-1111-2222-3333-444444444444", -1, 1, null, "T");
        StringAssert.DoesNotContain("avtr_00000000-1111-2222-3333-444444444444", json);
    }

    [Test]
    public void StatusRecord_EscapesAHostileHandle()
    {
        var json = UploadAvatar.BuildStatusJson("b", "running", "uploading", 0, 1, "He said \"hi\"\\n", "T");
        StringAssert.Contains("\\\"hi\\\"", json);
        Assert.DoesNotThrow(() => UnityEngine.JsonUtility.FromJson<StatusProbe>(json),
            "the record must stay parseable with a hostile handle");
    }

    private class StatusProbe { public string batchId; public string phase; public string handle; public bool running; public bool done; public int index; }

    // Waiters guess a boolean far more often than they read phase, so the record carries the two they guess,
    // and both must agree with phase for every value it takes.
    [TestCase("started",     true,  false)]
    [TestCase("running",     true,  false)]
    [TestCase("done-pass",   false, true)]
    [TestCase("done-fail",   false, true)]
    [TestCase("faulted",     false, true)]
    [TestCase("interrupted", false, true)]
    public void StatusRecord_RunningAndDoneFollowThePhase(string phase, bool running, bool done)
    {
        var json = UploadAvatar.BuildStatusJson("b", phase, "m", -1, 1, null, "T");
        StringAssert.Contains("\"running\":" + (running ? "true" : "false"), json);
        StringAssert.Contains("\"done\":" + (done ? "true" : "false"), json);
        var p = UnityEngine.JsonUtility.FromJson<StatusProbe>(json);
        Assert.AreEqual(running, p.running);
        Assert.AreEqual(done, p.done);
    }

    // Run's text is the done test a Bash waiter copies, so every terminal phase must be in it.
    [TestCase("done-pass")]
    [TestCase("done-fail")]
    [TestCase("faulted")]
    [TestCase("interrupted")]
    public void DoneTest_NamesEveryTerminalPhase(string phase)
    {
        Assert.IsTrue(UploadAvatar.IsTerminalPhase(phase));
        StringAssert.Contains(phase, UploadAvatar.DoneTest);
    }

    // Python's open() and PowerShell's Get-Content share read and write but not delete, which makes the
    // atomic replace fail for as long as the waiter holds the file. The write must still land, or the
    // record stays at "running" after the batch ends and the waiter loops forever.
    [Test]
    public void WriteStatusFile_LandsWhileAReaderHoldsTheFileWithoutDeleteSharing()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uploadstatus_" + System.Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(dir, "status.json");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(path, UploadAvatar.BuildStatusJson("b", "running", "uploading", 0, 1, "A", "T"));
            var done = UploadAvatar.BuildStatusJson("b", "done-pass", "ok", -1, 1, null, "T");
            using (new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                Assert.IsTrue(UploadAvatar.WriteStatusFile(path, done, out var why), "the write was refused: " + why);
            Assert.AreEqual(done, System.IO.File.ReadAllText(path));
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    // A holder that shares nothing refuses every write; the terminal record is retried, not dropped.
    [Test]
    public void PendingWrite_FailsUnderAnExclusiveLockAndLandsOnceItLifts()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uploadstatus_" + System.Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(dir, "status.json");
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(path, "{}");
            var pending = new UploadAvatar.PendingWrite(path, UploadAvatar.BuildStatusJson("b", "done-fail", "x", -1, 1, null, "T"));
            using (new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            {
                Assert.IsFalse(pending.Attempt());
                Assert.IsNotNull(pending.why, "a refused write names why, for Status() to relay");
            }
            Assert.IsTrue(pending.Attempt());
            StringAssert.Contains("\"phase\":\"done-fail\"", System.IO.File.ReadAllText(path));
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    // A domain reload mid-batch leaves the record in flight forever. Rewriting the phase, not appending a
    // side field, is what ends a waiter keyed on phase or on "running".
    [Test]
    public void MarkInterrupted_RewritesAnInFlightRecordAsTerminal()
    {
        var marked = UploadAvatar.MarkInterrupted(UploadAvatar.BuildStatusJson("abc", "running", "uploading", 1, 3, "B", "T"));
        Assert.IsNotNull(marked);
        var p = UnityEngine.JsonUtility.FromJson<StatusProbe>(marked);
        Assert.AreEqual("abc", p.batchId);
        Assert.AreEqual("interrupted", p.phase);
        Assert.IsTrue(p.done);
        Assert.IsFalse(p.running);
        Assert.AreEqual(1, p.index);
        Assert.AreEqual("B", p.handle);
        StringAssert.Contains("UNKNOWN", marked);
    }

    [TestCase("done-pass")]
    [TestCase("faulted")]
    [TestCase("interrupted")]
    public void MarkInterrupted_LeavesATerminalRecordAlone(string phase)
    {
        Assert.IsNull(UploadAvatar.MarkInterrupted(UploadAvatar.BuildStatusJson("b", phase, "m", -1, 1, null, "T")));
    }

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
        (string state, string sceneName) r = default;
        Assert.DoesNotThrow(() => r = UploadAvatar.ClassifyAvatar(null));
        Assert.AreEqual("unknown", r.state);
        Assert.AreEqual("(null)", r.sceneName);
    }

    // A fake in the SDK's ApiErrorException shape: status and server text are public fields and Message is
    // the content-free default.
    class FakeApiException : System.Exception
    {
        public System.Net.HttpStatusCode StatusCode;
        public string ErrorMessage;
        public FakeApiException(int s, string m, System.Exception inner = null)
            : base("Exception of type 'FakeApiException' was thrown.", inner) { StatusCode = (System.Net.HttpStatusCode)s; ErrorMessage = m; }
    }

    [Test]
    public void FailedFromException_404_NamesStatusAndServerText()
    {
        var o = UploadAvatar.FailedFromException(new FakeApiException(404, "Avatar not found"));
        Assert.AreEqual(404, o.httpStatus);
        Assert.AreEqual("real", UploadAvatarLogic.Classify(o.httpStatus, o.isValidation, o.isTimeout));
        StringAssert.Contains("not visible to this account", o.message);
        StringAssert.Contains("Avatar not found", o.message);
        StringAssert.DoesNotContain("was thrown", o.message);
    }

    [Test]
    public void FailedFromException_422_DoesNotBlameMetadataText()
    {
        var o = UploadAvatar.FailedFromException(new FakeApiException(422, "Avatar failed validation"));
        StringAssert.Contains("(422)", o.message);
        StringAssert.Contains("Avatar failed validation", o.message);
        StringAssert.DoesNotContain("change the text", o.message);
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

    // ----- id-persistence verdict ---------------------------------------------------------------
    // Loop_ReservedNoBundleIsItsOwnState above proves the loop routes the outcome; these prove what
    // produces it.

    [Test]
    public void IsReservedNoBundle_firstUploadWhoseIdNeverPersisted()
    {
        Assert.IsTrue(UploadAvatar.IsReservedNoBundle(true, "first-upload"));
    }

    // "unknown" is what a GameObject destroyed across the await classifies as. Covering it rules out the
    // mutant `wasFirstUpload && stateAfter != "update"`, which would halt the batch on an upload that
    // landed. The null cell is RealUploadOne's short-circuit: a re-upload passes null rather than
    // re-reading the state.
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
