using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

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
        // The refusal's FAIL summary is emitted via Debug.LogError — expected, or the runner flags an
        // unhandled error log.
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var summary = MoveComponents.Run(inst, root, new[] { "RcNoAnchorProbe" }, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("RcNoAnchorProbe", summary);
        StringAssert.Contains("no relocatable anchor", summary);
        Object.DestroyImmediate(inst);
    }

    // ── Inbound-ref rewire (whatIf side only: the execute side mutates live objects, the forbidden
    //    NUnit venue per docs/verify.md — it is verified behaviorally via execute_code instead) ────

    [Test]
    public void Run_whatIf_predicts_inbound_ref_rewire_for_referenced_colliders()
    {
        // The regression this guards: relocation re-creates the component, so a physbone's
        // colliders[] entry pointing at a relocated collider went null when the original was
        // destroyed. Execute now rewires such refs; whatIf must predict the same count.
        var inst = new GameObject("Inst");
        var bone = new GameObject("Bone"); bone.transform.SetParent(inst.transform);
        var col  = bone.AddComponent<VRCPhysBoneCollider>();
        var pbGo = new GameObject("PB"); pbGo.transform.SetParent(inst.transform);
        var pb   = pbGo.AddComponent<VRCPhysBone>();
        pb.colliders = new List<VRCPhysBoneColliderBase> { col };

        var summary = MoveComponents.Run(inst, inst.transform, new[] { "VRCPhysBoneCollider" },
                                         "AvatarDynamics/PB_Colliders", whatIf: true);

        StringAssert.Contains("=> PASS", summary);
        StringAssert.Contains("moved=1", summary);
        StringAssert.Contains("inboundRefsRewired=1", summary);
        StringAssert.Contains("wouldRewire: VRCPhysBone 'PB'", summary);
        Object.DestroyImmediate(inst);
    }

    [Test]
    public void Run_fails_loud_on_unresolved_type_name()
    {
        // A name that resolves to no Component at all is also a loud FAIL (the other refusal path).
        var inst = new GameObject("Inst");
        var root = inst.transform;
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL")); // refusal logs its FAIL summary — expected
        var summary = MoveComponents.Run(inst, root, new[] { "NoSuchComponentTypeXyz" }, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("NoSuchComponentTypeXyz", summary);
        Object.DestroyImmediate(inst);
    }
}
