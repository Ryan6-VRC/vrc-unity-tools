using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// A resolvable non-VRC Component whose VrcComponentTable.Lookup is null (no row) — used to exercise
// the "resolved type, but no table anchor" refusal branch without any VRC SDK reference. TypeCache
// resolves it by simple name after compile, exactly as a real MA/VRCFury type would resolve.
namespace MoveComponentsTests_Ns
{
    public class RcNoAnchorProbe : MonoBehaviour { }
}

public class MoveComponentsTests
{
    [Test]
    public void ResolveAnchor_prefers_explicit_else_host()
    {
        var host = new GameObject("H").transform;
        var root = new GameObject("Root").transform;
        Assert.AreEqual(host, MoveComponents.ResolveAnchor(null, host));
        Assert.AreEqual(root, MoveComponents.ResolveAnchor(root, host));
        Object.DestroyImmediate(host.gameObject); Object.DestroyImmediate(root.gameObject);
    }

    [Test]
    public void IsUnderOrEqual_matches_self_and_descendants_only()
    {
        var t = new GameObject("T").transform;
        var child = new GameObject("C").transform; child.SetParent(t);
        var outside = new GameObject("O").transform;
        Assert.IsTrue(MoveComponents.IsUnderOrEqual(t, t));
        Assert.IsTrue(MoveComponents.IsUnderOrEqual(child, t));
        Assert.IsFalse(MoveComponents.IsUnderOrEqual(outside, t));
        Object.DestroyImmediate(t.gameObject); Object.DestroyImmediate(outside.gameObject);
    }

    // ── Type-level no-anchor refusal (the headline new behavior) ────────────────────────────────

    [Test]
    public void Run_fails_loud_on_resolvable_type_with_no_table_anchor()
    {
        // RcNoAnchorProbe resolves (it's a real Component) but has no VRC table row → no anchor →
        // relocating it would change behavior → categorical FAIL, BEFORE any mutation, naming the type.
        // This is exactly how Relocate refuses MA/VRCFury/NDMF and Unity built-in constraints.
        var inst = new GameObject("Inst");
        var root = inst.transform;
        var summary = MoveComponents.Run(inst, root, new[] { "RcNoAnchorProbe" }, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("RcNoAnchorProbe", summary);
        StringAssert.Contains("no relocatable anchor", summary);
        Object.DestroyImmediate(inst);
    }

    [Test]
    public void Run_fails_loud_on_unresolved_type_name()
    {
        // A name that resolves to no Component at all is also a loud FAIL (the other refusal path).
        var inst = new GameObject("Inst");
        var root = inst.transform;
        var summary = MoveComponents.Run(inst, root, new[] { "NoSuchComponentTypeXyz" }, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("NoSuchComponentTypeXyz", summary);
        Object.DestroyImmediate(inst);
    }
}
