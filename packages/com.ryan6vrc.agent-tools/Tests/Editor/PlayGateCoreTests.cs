using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ryan6Vrc.AgentTools.Editor;

// Synthetic stand-in for the Av3Emulator control object. Av3Emulator is NOT loaded in the test project, so
// declaring its exact FullName here cannot collide; the core detects it by GetType().FullName and reads
// these two bool fields by reflection — the same path the real emulator hits. (The SDK descriptor, VRCFury,
// and GestureManager ARE loaded, so those fixtures use the real types via reflection AddComponent instead —
// see the helpers below — keeping the test asmdef a clean mirror with no VRC compile reference.)
namespace Lyuma.Av3Emulator.Runtime
{
    public class Av3Emulator : MonoBehaviour
    {
        public bool RunPreprocessAvatarHook = true;
        public bool EnablePlayerContactPermissions = false;
    }
}

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

    // Real VF.Model.VRCFury component; featureFullName == null leaves content undecodable (no managed ref).
    private void AddVrcFury(GameObject avatar, string featureFullName)
    {
        var vt = Resolve("VF.Model.VRCFury");
        Assert.IsNotNull(vt, "VF.Model.VRCFury must resolve (VRCFury present)");
        var comp = avatar.AddComponent(vt);
        if (featureFullName != null)
        {
            var ft = Resolve(featureFullName);
            Assert.IsNotNull(ft, featureFullName + " must resolve");
            var so = new SerializedObject(comp);
            so.FindProperty("content").managedReferenceValue = Activator.CreateInstance(ft);
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    private void NewGestureManager(bool enabled)
    {
        var t = Resolve("BlackStartX.GestureManager.GestureManager");
        Assert.IsNotNull(t, "GestureManager must resolve");
        var b = (Behaviour)NewGo("GM").AddComponent(t);
        b.enabled = enabled;
    }

    private void NewEmulator(bool run, bool perm, bool enabled = true)
    {
        var e = NewGo("Emu").AddComponent<Lyuma.Av3Emulator.Runtime.Av3Emulator>();
        e.RunPreprocessAvatarHook = run;
        e.EnablePlayerContactPermissions = perm;
        e.enabled = enabled;
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

    // ── Rule 2a: more than one active descriptor ────────────────────────────────────────────────────

    [Test]
    public void MultiAvatar_fails_with_named_offender()
    {
        NewAvatar("AvatarA");
        NewAvatar("AvatarB");
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass);
        CollectionAssert.Contains(Tags(r), "More than 1 avatar enabled");
        StringAssert.Contains("AvatarA", r.Offenders.First(o => o.Tag == "More than 1 avatar enabled").Message);
        StringAssert.Contains("AvatarB", r.Offenders.First(o => o.Tag == "More than 1 avatar enabled").Message);
    }

    // ── Rule 2b: VRCFury Fix-Write-Defaults ─────────────────────────────────────────────────────────

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

    // ── Rule 2c: GestureManager ─────────────────────────────────────────────────────────────────────

    [Test]
    public void EnabledGestureManager_fails()
    {
        NewAvatar("Avatar");
        NewGestureManager(enabled: true);
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsFalse(r.Pass);
        CollectionAssert.Contains(Tags(r), "Gesture Manager");
    }

    [Test]
    public void DisabledGestureManager_passes()
    {
        NewAvatar("Avatar");
        NewGestureManager(enabled: false);
        var r = PlayGateCore.Evaluate(_scene);
        Assert.IsTrue(r.Pass, "a disabled GestureManager is not a hazard: " + string.Join(",", Tags(r)));
    }

    // ── Rule 3: emulator config (only when an enabled emulator control object exists) ────────────────

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
