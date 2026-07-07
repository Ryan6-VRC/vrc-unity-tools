using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ryan6Vrc.AgentTools.Editor;

// All non-SDK fixtures (VRCAvatarDescriptor, VRCFury, GestureManager, LyumaAv3Emulator) are the REAL
// installed types, resolved by name via reflection AddComponent — the same path the core detects them on.
// No synthetic stand-in matching a hazard's FullName exists: such a stub (equal to a possibly-wrong const)
// is exactly what let the emulator rule go dead-but-green once, so the emulator tests must drive the real
// type. The fail-closed reflection degradation is instead covered directly via GimmickReport.ReadBoolMember
// (a same-FullName-but-fieldless stub would reintroduce that masking risk and make Resolve ambiguous).

public class PlayGateCoreTests
{
    // Each test gets an isolated preview scene so Evaluate sees only that test's objects — no dependence on
    // the Editor's real open scene, and Ryan's scenes are never mutated.
    private Scene _scene;

    [SetUp]    public void SetUp()    { _scene = EditorSceneManager.NewPreviewScene(); }
    [TearDown] public void TearDown() { EditorSceneManager.ClosePreviewScene(_scene); } // destroys its objects

    private static Type Resolve(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private GameObject NewGo(string name)
    {
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, _scene);
        return go;
    }

    private GameObject NewAvatar(string name)
    {
        var t = Resolve("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
        Assert.IsNotNull(t, "VRCAvatarDescriptor must resolve (VRC SDK present)");
        var go = NewGo(name);
        go.AddComponent(t);
        return go;
    }

    // Real VF.Model.VRCFury component. featureFullName == null explicitly nulls content (undecodable).
    private void AddVrcFury(GameObject avatar, string featureFullName)
    {
        var vt = Resolve("VF.Model.VRCFury");
        Assert.IsNotNull(vt, "VF.Model.VRCFury must resolve (VRCFury present)");
        var comp = avatar.AddComponent(vt);
        var so = new SerializedObject(comp);
        if (featureFullName != null)
        {
            var ft = Resolve(featureFullName);
            Assert.IsNotNull(ft, featureFullName + " must resolve");
            so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(ft);
        }
        else
        {
            so.FindProperty("content").managedReferenceValue = null; // force the undecodable path
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private void NewGestureManager(bool enabled)
    {
        var t = Resolve("BlackStartX.GestureManager.GestureManager");
        Assert.IsNotNull(t, "GestureManager must resolve");
        var b = (Behaviour)NewGo("GM").AddComponent(t);
        b.enabled = enabled;
    }

    // Real Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator control object (public bool config fields set by
    // reflection). This is the exact type the core matches — a synthetic stub would mask a wrong const.
    private void NewEmulator(bool run, bool perm, bool enabled = true)
    {
        var t = Resolve("Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator");
        Assert.IsNotNull(t, "LyumaAv3Emulator must resolve (lyuma.av3emulator present)");
        var b = (Behaviour)NewGo("Emu").AddComponent(t);
        t.GetField("RunPreprocessAvatarHook").SetValue(b, run);
        t.GetField("EnablePlayerContactPermissions").SetValue(b, perm);
        b.enabled = enabled;
    }

    private static List<string> Tags(PlayGateCore.PlayGateResult r) => r.Offenders.Select(o => o.Tag).ToList();

    // ── Rule 1: no active descriptor ────────────────────────────────────────────────────────────────

    [Test]
    public void ZeroDescriptor_passes_silently()
    {
        NewGo("JustAProp"); // a non-avatar / codegen / bake scene is never gated
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass);
        Assert.AreEqual(0, r.Offenders.Count);
    }

    [Test]
    public void OneAvatar_no_hazards_passes()
    {
        NewAvatar("Avatar");
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "one clean avatar should pass: " + string.Join(",", Tags(r)));
        Assert.AreEqual(0, r.Offenders.Count);
    }

    // A deactivated top-level backup avatar must NOT count toward the active total — otherwise the gate
    // false-positives "More than 1 avatar enabled" and its own "deactivate all but one" fix does nothing.
    // (Regression: GetComponentsInChildren(false) on Unity 2022.3 returns the queried inactive root's own
    // component, so rule 1 gathers includeInactive:true then filters on activeInHierarchy.)
    [Test]
    public void OneActive_plus_one_deactivated_backup_avatar_passes()
    {
        NewAvatar("ActiveAvatar");
        var backup = NewAvatar("DeactivatedBackup");
        backup.SetActive(false);
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "a deactivated backup avatar must not count as active: " + string.Join(",", Tags(r)));
    }

    // ── Rule 2a: more than one active descriptor (unconditional) ────────────────────────────────────

    [Test]
    public void MultiAvatar_fails_with_named_offender()
    {
        NewAvatar("AvatarA");
        NewAvatar("AvatarB");
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass);
        CollectionAssert.Contains(Tags(r), "More than 1 avatar enabled");
        var msg = r.Offenders.First(o => o.Tag == "More than 1 avatar enabled").Message;
        StringAssert.Contains("AvatarA", msg);
        StringAssert.Contains("AvatarB", msg);
    }

    // ── Rule 2b: VRCFury Fix-Write-Defaults (unconditional) ─────────────────────────────────────────

    [Test]
    public void VrcFury_without_FWD_fails()
    {
        var a = NewAvatar("Avatar");
        AddVrcFury(a, "VF.Model.Feature.Toggle"); // a real, non-FWD feature
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass);
        Assert.AreEqual(1, r.Offenders.Count, "only the VRCFury offender expected");
        Assert.AreEqual("VRCFury", r.Offenders[0].Tag);
        StringAssert.Contains("Fix Write Defaults", r.Offenders[0].Fix);
    }

    [Test]
    public void VrcFury_with_FWD_passes()
    {
        var a = NewAvatar("Avatar");
        AddVrcFury(a, PlayGateCore.FwdFeatureFullName); // the real Fix-Write-Defaults feature
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "FWD present should pass: " + string.Join(",", Tags(r)));
    }

    // Degradation branch (fail-closed): a VRCFury component whose feature is undecodable must block loud —
    // never read as an honest "no FWD needed / all clear".
    [Test]
    public void VrcFury_undecodable_feature_fails_loud()
    {
        var a = NewAvatar("Avatar");
        AddVrcFury(a, null); // content nulled → managedReferenceFullTypename empty → undecodable
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass);
        Assert.AreEqual("VRCFury", r.Offenders.Single().Tag);
        StringAssert.Contains("undecodable", r.Offenders[0].Message);
    }

    // VRCFury under a TOGGLED-OFF child must still be seen (the build processes inactive objects) — an
    // active-only scan would fail open and let the blocking modal pop.
    [Test]
    public void VrcFury_on_inactive_child_is_still_seen()
    {
        var a = NewAvatar("Avatar");
        var child = NewGo("OffChild");
        child.transform.SetParent(a.transform);
        child.SetActive(false);
        AddVrcFury(child, "VF.Model.Feature.Toggle"); // no FWD, on an inactive child
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass, "VRCFury on an inactive child must still be detected");
        CollectionAssert.Contains(Tags(r), "VRCFury");
    }

    // ── Rule 3a: GestureManager — only a hazard alongside a LIVE emulator ────────────────────────────

    [Test]
    public void EnabledGestureManager_with_emulator_fails()
    {
        NewAvatar("Avatar");
        NewGestureManager(enabled: true);
        NewEmulator(run: true, perm: false); // good config → the only offender is the GM
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass);
        Assert.AreEqual("Gesture Manager", r.Offenders.Single().Tag);
    }

    [Test]
    public void EnabledGestureManager_without_emulator_passes()
    {
        NewAvatar("Avatar");
        NewGestureManager(enabled: true); // standalone GM, no emulator → allowed
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "a standalone enabled GestureManager (no emulator) is allowed: " + string.Join(",", Tags(r)));
    }

    [Test]
    public void DisabledGestureManager_with_emulator_passes()
    {
        NewAvatar("Avatar");
        NewGestureManager(enabled: false);
        NewEmulator(run: true, perm: false);
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "a disabled GestureManager is not a hazard: " + string.Join(",", Tags(r)));
    }

    // ── Rule 3b: emulator config (only when an enabled emulator control object exists) ───────────────

    [Test]
    public void EmulatorPresent_misconfigured_fails()
    {
        NewAvatar("Avatar");
        NewEmulator(run: false, perm: true); // both flags wrong
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass);
        // a single "Emulator config" offender that names both wrong flags (not one per flag)
        Assert.AreEqual(1, r.Offenders.Count(o => o.Tag == "Emulator config"));
        var msg = r.Offenders.First(o => o.Tag == "Emulator config").Message;
        StringAssert.Contains("RunPreprocessAvatarHook", msg);
        StringAssert.Contains("EnablePlayerContactPermissions", msg);
    }

    [Test]
    public void EmulatorPresent_configured_passes()
    {
        NewAvatar("Avatar");
        NewEmulator(run: true, perm: false); // the correct config
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "a correctly-configured emulator should pass: " + string.Join(",", Tags(r)));
    }

    [Test]
    public void EmulatorAbsent_ignores_emulator_flags()
    {
        NewAvatar("Avatar"); // no emulator at all — a legitimate rung-2 bake
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "emulator flags are moot with no emulator present: " + string.Join(",", Tags(r)));
    }

    [Test]
    public void DisabledEmulator_ignores_emulator_rules()
    {
        NewAvatar("Avatar");
        NewGestureManager(enabled: true);                     // would fail IF the emulator counted
        NewEmulator(run: false, perm: true, enabled: false);  // disabled → moot
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "a disabled emulator arms neither the GM nor the config rule: " + string.Join(",", Tags(r)));
    }

    // ── Fail-closed reflection degradation (the emulator-field safety property) ──────────────────────
    // Drives GimmickReport.ReadBoolMember directly: a missing member returns null, which is what routes
    // CheckEmulatorConfig into its loud "field(s) not reflectable" block. (Testing the integration branch
    // would need a stub with the emulator's exact FullName but no fields — the masking anti-pattern.)

    private class BoolProbe { public bool Flag = true; }

    [Test]
    public void ReadBoolMember_reads_present_bool_field()
    {
        var o = new BoolProbe();
        Assert.AreEqual(true, GimmickReport.ReadBoolMember(o, o.GetType(), "Flag"));
    }

    [Test]
    public void ReadBoolMember_returns_null_for_missing_member()
    {
        var o = new BoolProbe();
        Assert.IsNull(GimmickReport.ReadBoolMember(o, o.GetType(), "NoSuchMember"));
    }

    // ── OverlaySummaryLine (deterministic, no text-width measurement) ────────────────────────────────

    private static List<PlayGateCore.Offender> Offs(params string[] tags) =>
        tags.Select(t => new PlayGateCore.Offender { Tag = t }).ToList();

    [Test] public void Overlay_zero_offenders_is_empty() => Assert.AreEqual("", PlayGateCore.OverlaySummaryLine(Offs()));
    [Test] public void Overlay_one_offender()  => Assert.AreEqual("(A)", PlayGateCore.OverlaySummaryLine(Offs("A")));
    [Test] public void Overlay_two_offenders()  => Assert.AreEqual("(A) (B)", PlayGateCore.OverlaySummaryLine(Offs("A", "B")));

    [Test]
    public void Overlay_three_offenders_shows_plus_n()
    {
        Assert.AreEqual("(More than 1 avatar enabled) (VRCFury) +1 (see log)",
            PlayGateCore.OverlaySummaryLine(Offs("More than 1 avatar enabled", "VRCFury", "Gesture Manager")));
    }
}
