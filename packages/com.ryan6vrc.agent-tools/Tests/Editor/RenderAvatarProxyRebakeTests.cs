using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// The freshness gate hangs on reflected NDMF handles that can silently rot: the proxy-attribution
// handle (proxy -> original, feeds the skin-rebake force-flag) and the avatar-root resolver handle
// (FindArmScopeRoot -> gate arming). These tests are their drift canaries. Package present + handle
// unresolved must FAIL, never skip (versionDefines/reflection-canary rule: a skip is exactly when
// production goes blind).
public class RenderAvatarProxyRebakeTests
{
    private static bool NdmfInstalled()
    {
        foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            if (p.name == "nadena.dev.ndmf") return true;
        return false;
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

    // G56 scope rule: a reactive component on a SIBLING of the capture target must still arm the
    // gate — the call site scans HasReactiveMA from the target's arm-scope root (the outermost avatar
    // root, resolved by FindArmScopeRoot). This pins the helper's scan semantics the call site depends
    // on: a subtree scan misses the sibling, a root scan catches it — and armedBy names the match by
    // hierarchy path. (FindArmScopeRoot's own resolution is delegated to NDMF and canaried separately —
    // a fake descriptor can't drive the real-type resolver, so here we stand in transform.root.)
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

    // The attribution-integrity guard (narrowed from the deleted presence assert): proxies discovered +
    // session settled + every one attributes to null = the silent body-drop, and nothing else FAILs. It
    // needs a live NDMF preview scene/session, which batchmode never has — the full CaptureCore path is
    // live-gate-only (RenderAvatarFreshnessGate.md). Headless we pin the extracted predicate + that the
    // FAIL-reason constants exist with their load-bearing wording. This test also re-homes the
    // ProxyDriftFailReason assertion (the ONLY headless check of that kept attribution-drift constant),
    // which lived on the now-deleted presence truth-table.
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

        StringAssert.Contains("attribution is unavailable", RenderAvatar.ProxyDriftFailReason); // re-homed from the deleted test
        StringAssert.Contains("null for every one", RenderAvatar.ProxyAllNullFailReason);
    }

    // Handle canary for the avatar-root resolver (FindArmScopeRoot → gate arming). NDMF installed + the
    // handle unresolved must red-FAIL, never Ignore: there is no name fallback, so drift → null for every
    // target → every reactive avatar routes to Settle.Exempt and backgrounded captures return OK-stale
    // silently. Resolver SEMANTICS delegate to NDMF's own (tested) walk-up — we canary the handle, not
    // re-test the walk (a fake VRCAvatarDescriptor can't drive the real-type resolver).
    [Test]
    public void AvatarRootResolverHandle_ResolvesAgainstInstalledNdmf()
    {
        if (!NdmfInstalled())
            Assert.Ignore("nadena.dev.ndmf not installed in this venue — canary has nothing to check");
        Assert.IsTrue(RenderAvatar.AvatarRootResolverHandleResolved,
            "NDMF is installed but RuntimeUtil.FindAvatarInParents didn't resolve — FindArmScopeRoot returns " +
            "null for every target, so every reactive avatar routes to Settle.Exempt and backgrounded " +
            "captures can return OK-stale silently; re-pin the resolver handle.");
    }
}

// Namespace deliberately contains "modular_avatar" and the type name contains marker "ShapeChanger"
// so HasReactiveMA's name-based matcher fires without referencing a real MA reactive type.
namespace modular_avatar_fixture
{
    public class FakeShapeChanger : MonoBehaviour { }
}
