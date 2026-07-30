using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// The freshness OUTCOME layer (V5): the pending-reload guard and the end-to-end nudge canary. Both
// exist because two measured editor states (pending script reload; play round-trip exited unfocused —
// 2026-07-29) defeat every mechanism gate at once while gate=armed keeps certifying. Batchmode has no
// SceneView or preview session, so the live behavior is gate-doc territory
// (RenderAvatarFreshnessGate.md V4, with on-demand arming recipes); headless we pin the pure gate
// pieces and the diagnostic wording the next agent will act on.
public class RenderAvatarCanaryTests
{
    // The reload-guard handles are UnityEditor internals on a VRChat-PINNED editor version — they must
    // resolve on this venue, always. A red here means the pinned-Unity assumption itself broke (editor
    // upgraded?): re-measure the parked-deform states before re-pinning, don't just fix the reflection.
    [Test]
    public void ReloadGuardHandles_ResolveOnPinnedUnity()
    {
        Assert.IsTrue(RenderAvatar.ReloadGuardHandlesResolved,
            "IsScriptReloadRequested / CanReloadAssemblies failed to resolve — the pending-reload guard "
            + "is silently gone and captures under a pending reload certify stale geometry again");
    }

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

    [Test]
    public void CanaryUnavailableNote_DeclaresTheGap()
    {
        StringAssert.Contains("canary unavailable", RenderAvatar.CanaryUnavailableNote);
        StringAssert.Contains("cannot be ruled out", RenderAvatar.CanaryUnavailableNote);
    }

    // The FAIL text is doctrine the next agent executes — pin the load-bearing parts: the remedy is
    // INTERACTIVE focus (~10 s of programmatic kick-focus measurably does NOT wake the parked
    // scheduler; a human clicking in does), the kick outcome, and the named nudge so an operator can
    // judge a suspected false-FAIL.
    [Test]
    public void CanaryFailReason_CarriesTheMeasuredRemedy()
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
}
