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
    // gate — the call site scans from the target's scene root. This pins the helper's semantics the
    // call site depends on: subtree scan misses the sibling, root scan catches it.
    [Test]
    public void HasReactiveMA_SceneRootScope_CatchesSiblingReactives()
    {
        var root = new GameObject("V3_FixtureRoot");
        try
        {
            var target = new GameObject("Body"); target.transform.SetParent(root.transform);
            var sibling = new GameObject("Outfit"); sibling.transform.SetParent(root.transform);
            sibling.AddComponent<modular_avatar_fixture.FakeShapeChanger>();

            Assert.IsFalse(RenderAvatar.HasReactiveMA(target),
                "subtree scan unexpectedly sees the sibling — scope semantics changed, retire this test pair deliberately");
            Assert.IsTrue(RenderAvatar.HasReactiveMA(target.transform.root.gameObject),
                "scene-root scan missed a sibling reactive — the G56 leaf-mesh arming hole is open again");
        }
        finally { Object.DestroyImmediate(root); }
    }
}

// Namespace deliberately contains "modular_avatar" and the type name contains marker "ShapeChanger"
// so HasReactiveMA's name-based matcher fires without an MA dependency (this asmdef references: []).
namespace modular_avatar_fixture
{
    public class FakeShapeChanger : MonoBehaviour { }
}
