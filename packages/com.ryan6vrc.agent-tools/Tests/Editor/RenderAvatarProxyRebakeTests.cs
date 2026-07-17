using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// The proxy-rebake fix (V3) hangs on two things that can silently rot: the NDMF proxy-attribution
// reflection handles (proxy -> original), and the gate-arming scan scope (scene root, not target
// subtree — G56). These tests are their drift canaries. Package present + handle unresolved must
// FAIL, never skip (versionDefines/reflection-canary rule: a skip is exactly when production goes
// blind).
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
    // gate — the call site scans from the target's arm-scope root (descriptor ancestor, scene-root
    // fallback). This pins the helper's semantics the call site depends on: subtree scan misses the
    // sibling, ancestor-scope scan catches it — and armedBy names the match by hierarchy path.
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
                "subtree scan unexpectedly sees the sibling — scope semantics changed, retire this test pair deliberately");
            Assert.IsTrue(RenderAvatar.HasReactiveMA(target.transform.root.gameObject, out string armedBy),
                "scene-root scan missed a sibling reactive — the G56 leaf-mesh arming hole is open again");
            Assert.AreEqual("V3_FixtureRoot/Outfit", armedBy,
                "armedBy must be the matched component's hierarchy path — the settle-FAIL messages append it");
        }
        finally { Object.DestroyImmediate(root); }
    }

    // Arm scope (PR #47 review): the scan root is the nearest VRCAvatarDescriptor ancestor, so a
    // NEIGHBOR avatar under a shared container no longer arms a plain target (the old transform.root
    // escape) — while a sibling reactive under the SAME descriptor still arms (G56 stays closed).
    [Test]
    public void FindArmScopeRoot_DescriptorScoped_NeighborAvatarDoesNotArm()
    {
        var container = new GameObject("V3_SharedContainer");
        try
        {
            var avatarA = new GameObject("AvatarA"); avatarA.transform.SetParent(container.transform);
            avatarA.AddComponent<VRCFixture.VRCAvatarDescriptor>();
            var target = new GameObject("Body"); target.transform.SetParent(avatarA.transform);

            var avatarB = new GameObject("AvatarB"); avatarB.transform.SetParent(container.transform);
            avatarB.AddComponent<VRCFixture.VRCAvatarDescriptor>();
            var reactive = new GameObject("Outfit"); reactive.transform.SetParent(avatarB.transform);
            reactive.AddComponent<modular_avatar_fixture.FakeShapeChanger>();

            var scope = RenderAvatar.FindArmScopeRoot(target);
            Assert.AreSame(avatarA, scope, "arm scope must stop at the nearest descriptor ancestor, not transform.root");
            Assert.IsFalse(RenderAvatar.HasReactiveMA(scope, out _),
                "neighbor avatar's reactive armed the gate — the shared-container over-arm is back");

            // Same-avatar sibling still arms: put a reactive under avatarA too.
            var siblingReactive = new GameObject("Skirt"); siblingReactive.transform.SetParent(avatarA.transform);
            siblingReactive.AddComponent<modular_avatar_fixture.FakeShapeChanger>();
            Assert.IsTrue(RenderAvatar.HasReactiveMA(RenderAvatar.FindArmScopeRoot(target), out _),
                "same-descriptor sibling reactive no longer arms — G56 reopened");
        }
        finally { Object.DestroyImmediate(container); }
    }

    [Test]
    public void FindArmScopeRoot_NoDescriptor_FallsBackToSceneRoot()
    {
        var root = new GameObject("V3_NoDescriptorRoot");
        try
        {
            var mid = new GameObject("Mid"); mid.transform.SetParent(root.transform);
            var target = new GameObject("Leaf"); target.transform.SetParent(mid.transform);
            Assert.AreSame(root, RenderAvatar.FindArmScopeRoot(target),
                "without a descriptor ancestor the scan must fall back to transform.root");
        }
        finally { Object.DestroyImmediate(root); }
    }

    // The (c)/(d) FAIL paths (attribution drift → error FAIL; reactive+settled+zero attributed proxies →
    // error FAIL) need a live NDMF preview scene/session, which batchmode never has — the full CaptureCore
    // paths are live-gate-only (RenderAvatarFreshnessGate.md). Headless we pin the extracted decision
    // predicate and that the FAIL-reason constants exist with their load-bearing wording.
    [Test]
    public void ProxyPresenceViolation_TruthTable_AndFailReasonsExist()
    {
        Assert.IsTrue(RenderAvatar.IsProxyPresenceViolation(true, RenderAvatar.Settle.Settled, 0),
            "reactive + live settled session + zero attributed proxies must FAIL — the silent body-drop case");
        Assert.IsFalse(RenderAvatar.IsProxyPresenceViolation(true, RenderAvatar.Settle.Settled, 1));
        Assert.IsFalse(RenderAvatar.IsProxyPresenceViolation(false, RenderAvatar.Settle.Settled, 0),
            "non-reactive targets render originals — no proxies expected");
        Assert.IsFalse(RenderAvatar.IsProxyPresenceViolation(true, RenderAvatar.Settle.Exempt, 0),
            "Exempt = previews disabled/no session — originals render un-suppressed, zero proxies is normal");
        Assert.IsFalse(RenderAvatar.IsProxyPresenceViolation(true, RenderAvatar.Settle.Drift, 0),
            "Drift can't certify a live session exists — never hard-FAIL on it");

        StringAssert.Contains("attribution is unavailable", RenderAvatar.ProxyDriftFailReason);
        StringAssert.Contains("zero proxies attributed", RenderAvatar.ProxyPresenceFailReason);
    }
}

// Namespace deliberately contains "modular_avatar" and the type name contains marker "ShapeChanger"
// so HasReactiveMA's name-based matcher fires without referencing a real MA reactive type.
namespace modular_avatar_fixture
{
    public class FakeShapeChanger : MonoBehaviour { }
}

// Name exactly "VRCAvatarDescriptor" in a VRC-prefixed namespace so FindArmScopeRoot's name-based
// matcher fires without adding an SDK reference (and without instantiating the real descriptor,
// whose AddComponent side effects batchmode doesn't need).
namespace VRCFixture
{
    public class VRCAvatarDescriptor : MonoBehaviour { }
}
