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

    [Test]
    public void WhatIf_ClassifiesBlueprintState()
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
        Assert.DoesNotThrow(() => {
            bool ok = CauReflect.TryBuildSetting(null, out _, out var why);
            Assert.IsFalse(ok);
            Assert.IsFalse(string.IsNullOrEmpty(why));
        });
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
        var report = UploadAvatar.RunCore(avatars, _ => outcomes.Dequeue(), () => saveCalls++);
        Assert.AreEqual("A", report.rows[0].handle); Assert.AreEqual("uploaded", report.rows[0].result);
        Assert.AreEqual("failed", report.rows[1].result); Assert.AreEqual("transient", report.rows[1].cls);
        Assert.AreEqual(2, report.rows.Count);
        Assert.AreEqual(1, saveCalls);
        foreach (var g in avatars) UnityEngine.Object.DestroyImmediate(g);
    }

    [Test]
    public void Loop_RateLimitIsNotAutoRetried()
    {
        var a = MakeAvatar("A","");
        int calls = 0;
        var report = UploadAvatar.RunCore(new[]{a},
            _ => { calls++; return UploadAvatar.UploadOutcome.Failed(httpStatus: 429); }, () => {});
        Assert.AreEqual(1, calls);
        Assert.AreEqual("rate-limit", report.rows[0].cls);
        UnityEngine.Object.DestroyImmediate(a);
    }

    [Test]
    public void Loop_ReservedNoBundleIsItsOwnState()
    {
        var a = MakeAvatar("A","");
        var report = UploadAvatar.RunCore(new[]{a},
            _ => UploadAvatar.UploadOutcome.ReservedNoBundle(), () => {});
        Assert.AreEqual("reserved-no-bundle", report.rows[0].result);
        UnityEngine.Object.DestroyImmediate(a);
    }

    [Test]
    public void Loop_RedactsErrorIds()
    {
        var a = MakeAvatar("A","");
        var report = UploadAvatar.RunCore(new[]{a},
            _ => UploadAvatar.UploadOutcome.Failed(httpStatus:400, message:"rejected avtr_dead for usr_beef"), () => {});
        StringAssert.DoesNotContain("avtr_", report.rows[0].error);
        StringAssert.DoesNotContain("usr_",  report.rows[0].error);
        UnityEngine.Object.DestroyImmediate(a);
    }
}
