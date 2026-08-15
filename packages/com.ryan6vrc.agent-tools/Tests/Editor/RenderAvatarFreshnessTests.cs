using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// RenderAvatar's freshness layer — everything that decides whether a captured sheet may be vouched for.
// One fixture because it is one production surface reached four ways, grown one file per repair wave (#38
// FAIL severity → #42 change horizon → #47 proxy/arm-scope → #73 outcome canary); the tell was two of those
// files carrying a byte-identical private NdmfInstalled().
//
// Batchmode has no SceneView, preview scene, preview session, or play mode, so no CaptureCore path is
// reachable here: the live half is Tests/Editor/RenderAvatarFreshnessGate.md, run over execute_code against
// an MA-composed avatar. Headless this fixture pins exactly three things — that the reflected handles still
// resolve, that the extracted pure predicates decide correctly, and that the diagnostic wording the next
// agent acts on is present.
public class RenderAvatarFreshnessTests
{
    // Package-presence signal independent of the reflection path under test: the package registry by package
    // ID — survives an assembly rename/split that would blind an assembly-name check the same way it blinds
    // the handles.
    private static bool NdmfInstalled()
    {
        foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            if (p.name == "nadena.dev.ndmf") return true;
        return false;
    }

    // ── Reflection canaries: NDMF/Unity internals the gate hangs on ──────────────────────────────────
    // Package present + handle unresolved must FAIL, never skip (the versionDefines/reflection-canary rule:
    // a skip is exactly when production goes blind). These are the cheapest real coverage in the repo — they
    // resolve live handles, so a silent upstream rename reds the suite instead of blinding the gate.

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

    // NDMF installed + the handle unusable must red-FAIL, never Ignore: there is no name fallback, so drift →
    // no-avatar-root for every target → every reactive avatar routes to Settle.Exempt and backgrounded
    // captures return OK-stale silently. The canary folds a RETURN-TYPE check (see
    // AvatarRootResolverHandleResolved): a bare != null would stay green through a Transform→GameObject
    // return drift that fails at runtime. Resolver SEMANTICS delegate to NDMF's own (tested) walk-up — we
    // canary the handle, not the walk.
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

    // The reload-guard handles are UnityEditor internals on a VRChat-PINNED editor version — they must
    // resolve on this venue, always (no Ignore branch). A red here means the pinned-Unity assumption itself
    // broke (editor upgraded?): re-measure the parked-deform states before re-pinning, don't just fix the
    // reflection.
    [Test]
    public void ReloadGuardHandles_ResolveOnPinnedUnity()
    {
        Assert.IsTrue(RenderAvatar.ReloadGuardHandlesResolved,
            "IsScriptReloadRequested / CanReloadAssemblies failed to resolve — the pending-reload guard "
            + "is silently gone and captures under a pending reload certify stale geometry again");
    }

    // The sweep through its real call path, not just its handles. Batchmode has no NDMF preview session, so
    // PreviewSession.Current reads null and the sweep must exit at its previews-disabled guard returning
    // EXACTLY "" — nothing to sweep. Every other return is a live regression: the drift note means a handle
    // rotted OR the body threw (the catch-all returns that note too), and the incomplete sentinel means the
    // pump loop ran where it cannot. The previous assertion accepted the drift note and skipped its check
    // entirely on "", so both of those read as a pass. There is deliberately no elapsed-time bound: headless
    // the pump loop is unreachable (ProbeSettle returns Exempt on the first iteration), so a bound here
    // guards nothing — the sweep's ~250 ms cap is a live-gate property (RenderAvatarFreshnessGate.md).
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

    // G56 scope rule: a reactive component on a SIBLING of the capture target must still arm the gate — the
    // call site scans HasReactiveMA from the target's arm-scope root (the outermost avatar root, resolved by
    // ResolveArmScope). This pins the helper's scan semantics the call site depends on: a subtree scan misses
    // the sibling, a root scan catches it — and armedBy names the match by hierarchy path. (ResolveArmScope's
    // own resolution is delegated to NDMF and canaried above — a fake descriptor can't drive the real-type
    // resolver, so here we stand in transform.root.)
    [Test]
    public void HasReactiveMA_AncestorScope_CatchesSiblingReactives_AndNamesThem()
    {
        var root = new GameObject("V3_FixtureRoot");
        try
        {
            var target = new GameObject("Body"); target.transform.SetParent(root.transform);
            var sibling = new GameObject("Outfit"); sibling.transform.SetParent(root.transform);
            sibling.AddComponent<modular_avatar_fixture.FakeShapeChanger>();

            Assert.IsFalse(RenderAvatar.HasReactiveMA(target, out _),
                "subtree scan unexpectedly sees the sibling — scope semantics changed, retire this test deliberately");
            Assert.IsTrue(RenderAvatar.HasReactiveMA(target.transform.root.gameObject, out string armedBy),
                "root scan missed a sibling reactive — the G56 leaf-mesh arming hole is open again");
            Assert.AreEqual("V3_FixtureRoot/Outfit", armedBy,
                "armedBy must be the matched component's hierarchy path — the settle-FAIL messages append it");
        }
        finally { Object.DestroyImmediate(root); }
    }

    // The attribution-integrity guard: proxies discovered + session settled + every one attributes to null =
    // the silent body-drop, and nothing else FAILs. Also the ONLY headless check of the two attribution FAIL
    // constants' load-bearing wording.
    [Test]
    public void AttributionAllNull_TruthTable_AndFailReasonsExist()
    {
        Assert.IsTrue(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Settled, 5, 0),
            "proxies discovered + settled + every attribution null = the silent body-drop — must FAIL");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Settled, 0, 0),
            "zero discovered (at-rest avatar) is normal — must NOT FAIL (the deleted presence false-FAIL)");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Settled, 5, 5),
            "healthy: discovered and attributed");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Unsettled, 5, 0),
            "unsettled already FAILs upstream — the guard must not double-fire on a mid-rebuild read");
        Assert.IsFalse(RenderAvatar.IsAttributionAllNull(RenderAvatar.Settle.Exempt, 5, 0),
            "Exempt = previews disabled/no session — nothing to certify");

        StringAssert.Contains("attribution is unavailable", RenderAvatar.ProxyDriftFailReason);
        StringAssert.Contains("null for every one", RenderAvatar.ProxyAllNullFailReason);
    }

    // The resolver must be tri-state so Drift ≠ NoAvatarRoot. NoAvatarRoot is a legitimate Settle.Exempt;
    // Drift (handle unusable OR a non-Transform invoke result — the return-type drift that slips a bare-null
    // canary) must route to a loud FAIL at the call site, never a silent exempt.
    [Test]
    public void ClassifyArmScope_TriState_DistinguishesDriftFromNoAvatarRoot()
    {
        var go = new GameObject("V4_ArmScopeFixture");
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

    // Every reflection-drift/unsettled state FAILs before the OK return, so the summary gate token is exactly
    // armed|exempt — never a "drift" value the docs don't enumerate.
    //
    // Swept over the WHOLE Settle enum rather than sampled. `settle` is 4-valued (Exempt/Settled/Unsettled/
    // Drift), so hand-picking Settled and Drift models it as a boolean term and leaves Unsettled and Exempt
    // unasserted: widening production to `settle != Settle.Drift` keeps every sampled case green while
    // returning "armed" mid-rebuild and with previews globally off — a render certified freshness-gated when
    // it is not, which is the one thing this gate exists to prevent. Enumerating also means a NEW Settle
    // member cannot silently widen the gate; it arrives already asserted exempt.
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
    // Both exist because two measured editor states (pending script reload; play round-trip exited unfocused
    // — 2026-07-29) defeat every mechanism gate at once while gate=armed keeps certifying. Live behavior is
    // gate-doc territory (RenderAvatarFreshnessGate.md V4, with on-demand arming recipes); headless we pin
    // the pure pieces and the diagnostic wording the next agent will act on.

    [Test]
    public void ShouldRunCanary_TruthTable()
    {
        Assert.IsTrue(RenderAvatar.ShouldRunCanary(false, true),
            "edit-mode SMR grab is where the freeze lives — canary must run, FOCUSED OR NOT (a "
            + "kicked-but-not-interacted editor reads focused while still parked; a focus skip "
            + "certified a stale sheet live, 2026-07-29)");
        Assert.IsFalse(RenderAvatar.ShouldRunCanary(true, true),
            "play mode renders through the live player loop — measured fresh 2026-07-29, no canary");
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

    // The unit chain the council flagged (PR #73 finding 4): worldDelta is already lossy-scaled by the
    // caller; the swing realizes ≥ half of it; worldPerSourcePx comes from the LIVE canary camera
    // (2·ortho / camH); side→tileRes is the compared tiles' downscale. Pin representative arithmetic
    // so a future "simplification" back to sv.size units red-fails.
    [Test]
    public void CanaryExpectedTilePx_UnitChain()
    {
        // 90mm shape, camera ortho 0.67 over 1447 source px → wpp ≈ 9.26e-4 m/px; side 900 → tile 1024.
        float wpp = 2f * 0.67f / 1447f;
        float px = RenderAvatar.CanaryExpectedTilePx(0.090f, wpp, 900, 1024);
        Assert.Greater(px, 40f, "a 90mm shape at body framing must predict well above the 16-px eligibility bar");
        // Same shape at whole-avatar framing (ortho 1.9): smaller but still eligible.
        float pxFar = RenderAvatar.CanaryExpectedTilePx(0.090f, 2f * 1.9f / 1447f, 900, 1024);
        Assert.Greater(pxFar, 16f, "avatar-root framing must not silently lose all blendshape eligibility");
        Assert.Less(pxFar, px, "zooming out reduces predicted amplitude");
        // Degenerate inputs never divide by zero or go negative.
        Assert.AreEqual(0f, RenderAvatar.CanaryExpectedTilePx(0.1f, 0f, 900, 1024));
        Assert.AreEqual(0f, RenderAvatar.CanaryExpectedTilePx(0.1f, wpp, 0, 1024));
    }

    // The FAIL text is doctrine the next agent executes — pin the load-bearing parts: the remedy is
    // INTERACTIVE focus (~10 s of programmatic kick-focus measurably does NOT wake the parked
    // scheduler; a human clicking in does), the kick outcome, and the named nudge so an operator can
    // judge a suspected false-FAIL.
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
        // The escalation cure: a focused play round-trip cured the specimen the click could not.
        StringAssert.Contains("enter and exit play mode once with the editor focused", refused);
        StringAssert.Contains("enter and exit play mode once with the editor focused", kicked);
        // The occlusion self-doubt (council finding 6): the message carries its own false-FAIL caveat,
        // because the message — not the gate doc — is what the next agent acts on.
        StringAssert.Contains("occluded", kicked);
        StringAssert.Contains("different angle", kicked);

        // The other half of the same disclosure: when no shape is probe-worthy the summary carries a NOTE
        // instead of a verdict, and both phrases are the payload — "canary unavailable" is what an operator
        // greps for, "cannot be ruled out" is the admission that this sheet is not freshness-certified.
        // Asserted here rather than as its own case because no code path DECIDES on this constant; only its
        // wording is load-bearing, so a rewrite that softened it to a shrug would otherwise stay green.
        StringAssert.Contains("canary unavailable", RenderAvatar.CanaryUnavailableNote);
        StringAssert.Contains("cannot be ruled out", RenderAvatar.CanaryUnavailableNote);
    }

    // The scan is the outcome half of the shading guard: batchmode cannot render a pixel, so what IS
    // assertable headlessly is the counter's discipline. Exact match only — the placeholder is a flat fill,
    // and near-cyan is ordinary art on a lilToon avatar, so a tolerance would fail honest grabs. Alpha is
    // deliberately unexamined (Downscale's Sample hardcodes it to 255), which is why no alpha case appears
    // here: a test over alpha-0 data would assert against input the tool cannot produce.
    [Test]
    public void CountPlaceholderPx_ExactMatchOnly_AndNullSafe()
    {
        Assert.AreEqual(0, RenderAvatar.CountPlaceholderPx(null), "null must not throw");
        Assert.AreEqual(0, RenderAvatar.CountPlaceholderPx(new Color32[0]));

        var buf = new Color32[16];
        for (int i = 0; i < buf.Length; i++) buf[i] = new Color32(233, 226, 224, 255); // the measured hair tone
        Assert.AreEqual(0, RenderAvatar.CountPlaceholderPx(buf));

        buf[3] = new Color32(0, 255, 255, 255);
        buf[4] = new Color32(0, 255, 255, 0);     // alpha must not gate the count
        buf[5] = new Color32(1, 255, 255, 255);   // one channel off is NOT the placeholder
        buf[6] = new Color32(0, 254, 255, 255);
        buf[7] = new Color32(0, 255, 254, 255);
        Assert.AreEqual(2, RenderAvatar.CountPlaceholderPx(buf));
    }

    // The measured partial case: 64 px on a sheet that otherwise reads healthy (gate doc §Shading cell,
    // 2026-08-13). It is the reason the floor is 0 and the reason no blob/interior filter is applied — a
    // region this small has no interior pixels at all and is invisible to one.
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

    // Same reasoning as the canary/reload constants above: no code path decides on this wording, but the
    // wording is what an agent acts on. The payload is the placeholder colour (what an operator matches
    // against a suspect sheet by eye), the offending angle, and — load-bearing — two materially different
    // remedies. ShaderUtil.anythingCompiling is editor-global and cannot tell this grab's compilation from
    // an unrelated import's, so the compiling arm must not claim attribution, and the quiet arm must not
    // promise a re-grab will help. An earlier draft of the retired note claimed "the guard did not cover
    // some path"; this pins the corrected claim so a rewrite cannot quietly restore the over-claim.
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
            StringAssert.DoesNotContain("did not cover", s);
        }
        StringAssert.Contains("cannot be attributed", compiling);
        StringAssert.Contains("re-grab first", compiling);
        StringAssert.Contains("not indicated", quiet);
        StringAssert.Contains("Measure the mechanism", quiet);
        Assert.AreNotEqual(compiling, quiet, "the two arms must not collapse to one remedy");

        // The terminal case has to end somewhere the reader can act: floor 0 with no opt-out means an
        // authored cyan surface would otherwise leave a target permanently un-grabbable. Both arms carry it,
        // and the doc route is a resolvable PATH — a console reader has the string, not the class doc.
        foreach (var s in new[] { compiling, quiet })
        {
            StringAssert.Contains("showGizmos:false", s);
            StringAssert.Contains("hide", s);
            StringAssert.Contains("RenderAvatarFreshnessGate.md", s);
        }
    }

    // The counter is exercised through the real decode path, not just over hand-built buffers: the gate
    // doc's live Measure row can only report "the grab returned OK", which is circular as proof, so what is
    // assertable headlessly is that an encoded-then-decoded sheet still counts exactly. A scan reading a
    // mis-strided or wrong-format buffer fails here rather than in a green live cell.
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

    // Frame A is a prior grab off disk: the live compile probe says nothing about it, so its reason must
    // name the diff-specific damage (fabricated or masked `changed` px) and send the reader to re-grab A
    // rather than this call.
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
    // G17: the transient settle-gate FAIL must log at Warning (an expected "re-grab" retry condition), while
    // genuine failures stay at Error — so a console-clean gate isn't polluted by the re-grab prompt. Capture
    // can't reach the settle gate headless (it returns "no SceneView" first), so the split is verified on the
    // two Fail helpers directly; the call-site wiring (settle gate → FailTransient) is a one-line read.

    [Test]
    public void SettleFail_LogsWarning_NotError()
    {
        // Expect(Warning) both asserts the severity IS Warning and marks it handled. Were it an Error, the
        // Warning-expect would go unmatched AND the Error would be an unhandled-error test failure.
        LogAssert.Expect(LogType.Warning, new Regex(@"\[RenderAvatar\] Hair => FAIL: preview not settled"));
        string msg = RenderAvatar.FailTransient("Hair", "preview not settled (rebuild in flight)");
        StringAssert.StartsWith("[RenderAvatar] Hair => FAIL:", msg); // same FAIL contract string as Fail
    }

    [Test]
    public void GenuineFail_LogsError()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[RenderAvatar\] Hair => FAIL: target not found"));
        string msg = RenderAvatar.Fail("Hair", "target not found");
        StringAssert.StartsWith("[RenderAvatar] Hair => FAIL:", msg);
    }
}

// Namespace deliberately contains "modular_avatar" and the type name contains marker "ShapeChanger"
// so HasReactiveMA's name-based matcher fires without referencing a real MA reactive type.
namespace modular_avatar_fixture
{
    public class FakeShapeChanger : MonoBehaviour { }
}
