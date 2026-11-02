using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

// A resolvable Component with no VrcComponentTable row, for the no-anchor refusal. TypeCache resolves it by
// simple name, as it would a real MA or VRCFury type.
namespace MoveComponentsTests_Ns
{
    public class RcNoAnchorProbe : MonoBehaviour { }
}

public class MoveComponentsTests
{
    [Test]
    public void Run_fails_loud_on_resolvable_type_with_no_table_anchor()
    {
        var inst = new GameObject("Inst");
        var root = inst.transform;
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var summary = MoveComponents.Run(inst, root, new[] { "RcNoAnchorProbe" }, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("RcNoAnchorProbe", summary);
        StringAssert.Contains("no relocatable anchor", summary);
        Object.DestroyImmediate(inst);
    }

    // whatIf side only: the execute side mutates live objects, which docs/verify.md forbids in NUnit.
    [Test]
    public void Run_whatIf_predicts_inbound_ref_rewire_for_referenced_colliders()
    {
        // Relocation re-creates the component, so a physbone's colliders[] entry pointing at a relocated
        // collider needs rewiring; whatIf must predict the count execute rewires.
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
        var inst = new GameObject("Inst");
        var root = inst.transform;
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var summary = MoveComponents.Run(inst, root, new[] { "NoSuchComponentTypeXyz" }, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("NoSuchComponentTypeXyz", summary);
        Object.DestroyImmediate(inst);
    }
}
