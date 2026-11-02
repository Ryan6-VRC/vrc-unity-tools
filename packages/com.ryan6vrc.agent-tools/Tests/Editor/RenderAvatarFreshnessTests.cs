using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// RenderAvatar's freshness layer: everything that decides whether a captured sheet may be vouched for.
// Batchmode has no SceneView, preview scene, preview session, or play mode, so no CaptureCore path is
// reachable here: the live half is Tests/Editor/RenderAvatarFreshnessGate.md. Headless this fixture pins that
// the reflected handles still resolve, that the extracted pure predicates decide correctly, and that the
// diagnostic wording the next agent acts on is present.
public class RenderAvatarFreshnessTests
{
    // Reads the package registry by package ID: an assembly rename or split would blind an assembly-name check
    // the same way it blinds the handles under test.
    private static bool NdmfInstalled()
    {
        foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            if (p.name == "nadena.dev.ndmf") return true;
        return false;
    }

    // ── Reflection canaries: NDMF/Unity internals the gate hangs on ──────────────────────────────────
    // Package present + handle unresolved must FAIL, never skip: a skip is exactly when production goes blind.

    [Test]
    public void ChangeHorizonHandles_ResolveAgainstInstalledNdmf()
    {
        if (!NdmfInstalled())
            Assert.Ignore("nadena.dev.ndmf not installed in this venue — canary has nothing to check");

        Assert.IsTrue(RenderAvatar.ChangeHorizonHandlesResolved,
            "NDMF is installed but a change-horizon handle failed to resolve (ObjectWatcher/PropertyMonitor/" +
            "NDMFSyncContext/ComputeContext member renamed?) — the settle gate's scripted-edit blind spot " +
            "is silently open again; re-pin the reflection handles in RenderAvatar.");
    }

    [Test]
    public void ProxyHandles_ResolveAgainstInstalledNdmf()
    {
        if (!NdmfInstalled())
            Assert.Ignore("nadena.dev.ndmf not installed in this venue — canary has nothing to check");

        Assert.IsTrue(RenderAvatar.ProxyHandlesResolved,
            "NDMF is installed but a proxy-attribution handle failed to resolve " +
            "(NDMFPreview.GetOriginalObjectForProxy / IsPreviewScene renamed?) — kept proxies can no " +
            "longer be identified, so the proxy skin-rebake force-flag silently stops landing and " +
            "backgrounded captures of reactive avatars can return OK-stale again; re-pin the handles.");
    }

    // The walk-up itself is NDMF's, so this canaries the handle, not the walk.
    [Test]
    public void AvatarRootResolverHandle_ResolvesAgainstInstalledNdmf()
    {
        if (!NdmfInstalled())
            Assert.Ignore("nadena.dev.ndmf not installed in this venue — canary has nothing to check");
        Assert.IsTrue(RenderAvatar.AvatarRootResolverHandleResolved,
            "NDMF is installed but RuntimeUtil.FindAvatarInParents didn't resolve to a Transform-returning " +
            "handle — ResolveArmScope classifies every target no-avatar-root, so every reactive avatar routes " +
            "to Settle.Exempt and backgrounded captures can return OK-stale silently; re-pin the resolver handle.");
    }

    // UnityEditor internals on the VRChat-pinned editor, so there is no Ignore branch. A red means the editor
    // itself changed: re-measure the parked-deform states before re-pinning, don't just fix the reflection.
    [Test]
    public void ReloadGuardHandles_ResolveOnPinnedUnity()
    {
        Assert.IsTrue(RenderAvatar.ReloadGuardHandlesResolved,
            "IsScriptReloadRequested / CanReloadAssemblies failed to resolve — the pending-reload guard "
            + "is silently gone and captures under a pending reload certify stale geometry again");
    }

    // Batchmode has no NDMF preview session, so PreviewSession.Current reads null and the sweep must exit at its
    // previews-disabled guard returning EXACTLY "". No elapsed-time bound: SweepNdmfChangeHorizon's pump loop and
    // its deadline sit past that guard, so a bound here guards nothing.
    [Test]
    public void Sweep_Headless_ExitsCleanAtThePreviewsDisabledGuard()
    {
        if (!NdmfInstalled())
            Assert.Ignore("nadena.dev.ndmf not installed in this venue — the sweep can only return its drift note");

        Assert.AreEqual("", RenderAvatar.SweepNdmfChangeHorizon(),
            "the drift note means a change-horizon handle rotted or the sweep body threw; the incomplete " +
            "sentinel means the pump loop ran headless — neither is a legal return with no preview session");
    }

    // ── Pure gate predicates: the decisions extracted so batchmode can pin them ──────────────────────

    // A reactive component on a SIBLING of the capture target must still arm the gate: the call site scans
    // HasReactiveMA from the target's arm-scope root. A fake descriptor can't drive ResolveArmScope's real-type
    // resolver, so transform.root stands in for it here.
    [Test]
    public void HasReactiveMA_AncestorScope_CatchesSiblingReactives_AndNamesThem()
    {
        var root = new GameObject("SiblingReactiveRoot");
        try
        {
            var target = new GameObject("Body"); target.transform.SetParent(root.transform);
            var sibling = new GameObject("Outfit"); sibling.transform.SetParent(root.transform);
            sibling.AddComponent<modular_avatar_fixture.FakeShapeChanger>();

            Assert.IsFalse(RenderAvatar.HasReactiveMA(target, out _),
                "subtree scan unexpectedly sees the sibling — scope semantics changed, retire this test deliberately");
            Assert.IsTrue(RenderAvatar.HasReactiveMA(target.transform.root.gameObject, out string armedBy),
                "root scan missed a sibling reactive — the leaf-mesh arming hole is open again");
            Assert.AreEqual("SiblingReactiveRoot/Outfit", armedBy,
                "armedBy must be the matched component's hierarchy path — the settle-FAIL messages append it");
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void AttributionAllNull_TruthTable_AndFailReasonsExist()
    {
        Assert.IsTrue(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Settled, 5, 0),
            "proxies discovered + settled + every attribution null = the silent body-drop — must FAIL");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Settled, 0, 0),
            "zero discovered (at-rest avatar) is normal — must NOT FAIL");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Settled, 5, 5),
            "healthy: discovered and attributed");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Unsettled, 5, 0),
            "unsettled already FAILs upstream — the guard must not double-fire on a mid-rebuild read");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Exempt, 5, 0),
            "Exempt = previews disabled/no session — nothing to certify");

        StringAssert.Contains("attribution is unavailable", RenderAvatar.ProxyDriftFailReason);
        StringAssert.Contains("null for every one", RenderAvatar.ProxyAllNullFailReason);
    }

    // NoAvatarRoot is a legitimate Settle.Exempt; Drift must route to a loud FAIL at the call site, never a
    // silent exempt.
    [Test]
    public void ClassifyArmScope_TriState_DistinguishesDriftFromNoAvatarRoot()
    {
        var go = new GameObject("ArmScopeFixture");
        try
        {
            var t = go.transform;
            Assert.AreEqual(RenderAvatar.ArmScope.Drift, RenderAvatar.ClassifyArmScope(false, null),
                "unusable handle (null or return-type drift) → Drift, whatever the result");
            Assert.AreEqual(RenderAvatar.ArmScope.Drift, RenderAvatar.ClassifyArmScope(false, t),
                "unusable handle → Drift even with a Transform-shaped result");
            Assert.AreEqual(RenderAvatar.ArmScope.NoAvatarRoot, RenderAvatar.ClassifyArmScope(true, null),
                "usable handle + null return = a real plain prop → NoAvatarRoot (legit exempt, NOT drift)");
            Assert.AreEqual(RenderAvatar.ArmScope.Found, RenderAvatar.ClassifyArmScope(true, t),
                "usable handle + Transform return → Found");
            Assert.AreEqual(RenderAvatar.ArmScope.Drift, RenderAvatar.ClassifyArmScope(true, go),
                "usable handle + non-null NON-Transform (return-type drift slipping through) → Drift");
            StringAssert.Contains("resolver", RenderAvatar.ArmScopeResolverDriftFailReason);
        }
        finally { Object.DestroyImmediate(go); }
    }

    // Swept over the WHOLE Settle enum rather than sampled: hand-picking Settled and Drift stays green when
    // production widens to `settle != Settle.Drift`, returning "armed" mid-rebuild and with previews globally
    // off, and a new Settle member arrives already asserted exempt.
    [Test]
    public void GateToken_IsArmedOnlyForAReactiveSettledTarget()
    {
        foreach (RenderAvatar.Settle settle in System.Enum.GetValues(typeof(RenderAvatar.Settle)))
            foreach (bool reactive in new[] { true, false })
            {
                bool shouldArm = reactive && settle == RenderAvatar.Settle.Settled;
                Assert.AreEqual(shouldArm ? "armed" : "exempt", RenderAvatar.GateToken(reactive, settle),
                    "reactive=" + reactive + " settle=" + settle);
            }
    }

    // ── Outcome-canary layer: the end-to-end nudge canary and the pending-reload guard ───────────────
    // Live behaviour: RenderAvatarFreshnessGate.md.

    [Test]
    public void ShouldRunCanary_TruthTable()
    {
        Assert.IsTrue(RenderAvatar.ShouldRunCanary(false, true),
            "edit-mode SMR grab is where the freeze lives — canary must run, FOCUSED OR NOT (a "
            + "kicked-but-not-interacted editor reads focused while still parked; a focus skip "
            + "certified a stale sheet live)");
        Assert.IsFalse(RenderAvatar.ShouldRunCanary(true, true),
            "play mode renders through the live player loop — measured fresh, no canary");
        Assert.IsFalse(RenderAvatar.ShouldRunCanary(false, false),
            "nothing skinned was drawn — nothing can be parked, no canary");
    }

    [Test]
    public void CanaryAlive_ThresholdSitsBetweenFloorAndRealNudge()
    {
        Assert.IsFalse(RenderAvatar.CanaryAlive(0), "byte-identical = dead");
        Assert.IsFalse(RenderAvatar.CanaryAlive(4), "measured no-op floor (0-4 px) must not read alive");
        Assert.IsFalse(RenderAvatar.CanaryAlive(8), "the floor itself is not proof of life");
        Assert.IsTrue(RenderAvatar.CanaryAlive(9), "anything past the floor is a moving mesh");
        Assert.IsTrue(RenderAvatar.CanaryAlive(11124), "a real nudge measures O(10^4)");
    }

    // Representative arithmetic only: the caller computes wpp, so this cannot see a call site drifting back to
    // sv.size units.
    [Test]
    public void CanaryExpectedTilePx_UnitChain()
    {
        float wpp = 2f * 0.67f / 1447f;
        float px = RenderAvatar.CanaryExpectedTilePx(0.090f, wpp, 900, 1024);
        Assert.Greater(px, 40f, "a 90mm shape at body framing must predict well above the 16-px eligibility bar");
        float pxFar = RenderAvatar.CanaryExpectedTilePx(0.090f, 2f * 1.9f / 1447f, 900, 1024);
        Assert.Greater(pxFar, 16f, "avatar-root framing must not silently lose all blendshape eligibility");
        Assert.Less(pxFar, px, "zooming out reduces predicted amplitude");
        Assert.AreEqual(0f, RenderAvatar.CanaryExpectedTilePx(0.1f, 0f, 900, 1024));
        Assert.AreEqual(0f, RenderAvatar.CanaryExpectedTilePx(0.1f, wpp, 0, 1024));
    }

    // The FAIL text is what the next agent executes. Pinned: the remedy is INTERACTIVE focus (programmatic
    // kick-focus does not wake the parked scheduler), the kick outcome, the named nudge so an operator can
    // judge a suspected false-FAIL, and the message's own occlusion caveat.
    [Test]
    public void CanaryDiagnostics_CarryTheRemedy_AndDeclareTheGap()
    {
        var kicked = RenderAvatar.BuildCanaryFailReason("blendshape 'X' on 'Body'", 0, true, "editor foregrounded");
        StringAssert.Contains("blendshape 'X' on 'Body'", kicked);
        StringAssert.Contains("moved 0 px", kicked);
        StringAssert.Contains("focus kick sent (editor foregrounded)", kicked);
        StringAssert.Contains("CLICK INTO", kicked);
        StringAssert.Contains("~10 s", kicked);
        StringAssert.Contains("re-grab", kicked);

        var refused = RenderAvatar.BuildCanaryFailReason("the root of 'Prop'", 3, false, "SetForegroundWindow refused");
        StringAssert.Contains("focus kick failed (SetForegroundWindow refused)", refused);
        StringAssert.Contains("click into", refused);
        StringAssert.Contains("~10 s", refused);
        StringAssert.Contains("enter and exit play mode once with the editor focused", refused);
        StringAssert.Contains("enter and exit play mode once with the editor focused", kicked);
        StringAssert.Contains("occluded", kicked);
        StringAssert.Contains("different angle", kicked);

        // With no probe-worthy shape the summary carries this note instead of a verdict. No code path decides
        // on the constant, so only this pins its wording.
        StringAssert.Contains("canary unavailable", RenderAvatar.CanaryUnavailableNote);
        StringAssert.Contains("cannot be ruled out", RenderAvatar.CanaryUnavailableNote);
    }

    // Exact match only: the placeholder is a flat fill, and near-cyan is ordinary art on a lilToon avatar, so a
    // tolerance would fail honest grabs.
    [Test]
    public void CountPlaceholderPx_ExactMatchOnly_AndNullSafe()
    {
        Assert.AreEqual(0, RenderAvatar.CountPlaceholderPx(null), "null must not throw");
        Assert.AreEqual(0, RenderAvatar.CountPlaceholderPx(new Color32[0]));

        var buf = new Color32[16];
        for (int i = 0; i < buf.Length; i++) buf[i] = new Color32(233, 226, 224, 255);
        Assert.AreEqual(0, RenderAvatar.CountPlaceholderPx(buf));

        buf[3] = new Color32(0, 255, 255, 255);
        buf[4] = new Color32(0, 255, 255, 0);     // alpha must not gate the count
        buf[5] = new Color32(1, 255, 255, 255);   // one channel off is NOT the placeholder
        buf[6] = new Color32(0, 254, 255, 255);
        buf[7] = new Color32(0, 255, 254, 255);
        Assert.AreEqual(2, RenderAvatar.CountPlaceholderPx(buf));
    }

    // The measured partial case: gate doc §Shading cell.
    [Test]
    public void CountPlaceholderPx_CountsTheMeasuredPartialCase_NoInteriorFilter()
    {
        const int side = 512;
        var tile = new Color32[side * side];
        for (int i = 0; i < tile.Length; i++) tile[i] = new Color32(60, 60, 60, 255);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                tile[(100 + y) * side + (100 + x)] = new Color32(0, 255, 255, 255);
        Assert.AreEqual(64, RenderAvatar.CountPlaceholderPx(tile));
    }

    // ShaderUtil.anythingCompiling is editor-global and cannot tell this grab's compilation from an unrelated
    // import's, so the compiling arm must not claim attribution, and the quiet arm must not promise a re-grab
    // will help.
    [Test]
    public void PlaceholderFailReason_NamesTheColourAndSplitsTheRemedyByProbe()
    {
        string compiling = RenderAvatar.BuildPlaceholderFailReason("the served tiles", 64, "front", 64, true);
        string quiet = RenderAvatar.BuildPlaceholderFailReason("the served tiles", 64, "front", 64, false);

        foreach (var s in new[] { compiling, quiet })
        {
            StringAssert.Contains("#00FFFF", s);
            StringAssert.Contains("front", s);
            StringAssert.Contains("64", s);
            StringAssert.DoesNotContain("escaped the guard", s);
        }
        StringAssert.Contains("cannot be attributed", compiling);
        StringAssert.Contains("re-grab first", compiling);
        StringAssert.Contains("not indicated", quiet);
        StringAssert.Contains("Measure the mechanism", quiet);
        Assert.AreNotEqual(compiling, quiet, "the two arms must not collapse to one remedy");

        // Floor 0 with no opt-out: an authored cyan surface would otherwise leave a target permanently
        // un-grabbable, so both arms name the way out. The doc route is a resolvable PATH because a console
        // reader has the string, not the class doc.
        foreach (var s in new[] { compiling, quiet })
        {
            StringAssert.Contains("showGizmos:false", s);
            StringAssert.Contains("hide", s);
            StringAssert.Contains("RenderAvatarFreshnessGate.md", s);
        }
    }

    // A live `OK` is circular as proof (gate doc §Shading cell), so the count is also pinned through the real
    // decode path: a wrong-format buffer fails here rather than in a green live cell.
    [Test]
    public void CountPlaceholderPx_SurvivesThePngRoundTrip()
    {
        const int w = 64, h = 64;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        try
        {
            var px = new Color32[w * h];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(60, 60, 60, 255);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    px[(10 + y) * w + (10 + x)] = new Color32(0, 255, 255, 255);
            Assert.AreEqual(64, RenderAvatar.CountPlaceholderPx(px), "precondition: the source buffer");

            tex.SetPixels32(px);
            tex.Apply(false, false);
            var png = ImageConversion.EncodeToPNG(tex);

            var round = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.IsTrue(ImageConversion.LoadImage(round, png), "PNG must decode");
                Assert.AreEqual(64, RenderAvatar.CountPlaceholderPx(round.GetPixels32()),
                    "PNG is lossless, so the count must survive encode+decode exactly");
            }
            finally { UnityEngine.Object.DestroyImmediate(round); }
        }
        finally { UnityEngine.Object.DestroyImmediate(tex); }
    }

    // Frame A is a prior grab off disk: the live compile probe says nothing about it, so its reason sends the
    // reader to re-grab A rather than this call.
    [Test]
    public void FrameAPlaceholderFailReason_SendsTheReaderToReGrabA()
    {
        string s = RenderAvatar.BuildFrameAPlaceholderFailReason("grab_a.png", 64, "back", 64);
        StringAssert.Contains("#00FFFF", s);
        StringAssert.Contains("grab_a.png", s);
        StringAssert.Contains("back", s);
        StringAssert.Contains("re-grab frame A", s);
        StringAssert.DoesNotContain("still in flight", s);
    }

    [Test]
    public void ReloadPendingFailReason_NamesTheLockOnlyWhenHeld()
    {
        var plain = RenderAvatar.BuildReloadPendingFailReason(false);
        StringAssert.Contains("script reload is pending", plain);
        StringAssert.Contains("re-grab", plain);
        StringAssert.DoesNotContain("LockReloadAssemblies", plain);

        var locked = RenderAvatar.BuildReloadPendingFailReason(true);
        StringAssert.Contains("LockReloadAssemblies", locked);
    }

    // ── FAIL severity split ──────────────────────────────────────────────────────────────────────────
    // The transient settle-gate FAIL logs at Warning so a console-clean gate isn't polluted by the re-grab
    // prompt. Capture returns "no SceneView" before the settle gate headless, so the split is pinned on the two
    // Fail helpers directly.

    [Test]
    public void SettleFail_LogsWarning_NotError()
    {
        LogAssert.Expect(LogType.Warning, new Regex(@"\[RenderAvatar\] Hair => FAIL: preview not settled"));
        string msg = RenderAvatar.FailTransient("Hair", "preview not settled (rebuild in flight)");
        StringAssert.StartsWith("[RenderAvatar] Hair => FAIL:", msg);
    }

    [Test]
    public void GenuineFail_LogsError()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[RenderAvatar\] Hair => FAIL: target not found"));
        string msg = RenderAvatar.Fail("Hair", "target not found");
        StringAssert.StartsWith("[RenderAvatar] Hair => FAIL:", msg);
    }

    [Test]
    public void ResolveFramePrefix_RelativeLandsUnderTestOutput_AbsoluteAndLedPrefixDoNot()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "r6v_frameprefix_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            string bare = RenderAvatar.ResolveFramePrefix("stage_pose", root).Replace('\\', '/');
            StringAssert.EndsWith("/test-output/stage_pose", bare);
            Assert.IsTrue(System.IO.Directory.Exists(System.IO.Path.Combine(root, "test-output")));

            string led = RenderAvatar.ResolveFramePrefix("test-output/sub/a", root).Replace('\\', '/');
            StringAssert.EndsWith("/test-output/sub/a", led);
            StringAssert.DoesNotContain("test-output/test-output", led);

            string abs = System.IO.Path.Combine(root, "elsewhere", "x");
            Assert.AreEqual(abs, RenderAvatar.ResolveFramePrefix(abs, root));
        }
        finally { if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true); }
    }
}

// Namespace deliberately contains "modular_avatar" and the type name contains marker "ShapeChanger"
// so HasReactiveMA's name-based matcher fires without referencing a real MA reactive type.
namespace modular_avatar_fixture
{
    public class FakeShapeChanger : MonoBehaviour { }
}
