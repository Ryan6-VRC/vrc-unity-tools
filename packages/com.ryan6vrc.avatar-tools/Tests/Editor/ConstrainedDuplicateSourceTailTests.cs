using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

// The SDK list's `Clear()` resets `totalLength` and leaves the slots holding their old transforms, so a source
// survives past the new length, where the editor still solves it and the client does not (docs/runtime.md
// §Constraints). `Sources.Count` returns `totalLength`, so the public API cannot show the residue.
public class ConstrainedDuplicateSourceTailTests
{
    // The public API cannot address a slot past the length, so both the setup and the assertion go through
    // SerializedObject.
    private static void WriteSources(VRCConstraintBase c, int totalLength, params Transform[] slots)
    {
        var so = new SerializedObject(c);
        for (int i = 0; i < slots.Length; i++)
        {
            so.FindProperty("Sources.source" + i + ".SourceTransform").objectReferenceValue = slots[i];
            so.FindProperty("Sources.source" + i + ".Weight").floatValue = 1f;
        }
        so.FindProperty("Sources.totalLength").intValue = totalLength;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    [Test]
    public void ReplacingSources_LeavesNoTransformInASlotPastTheNewLength()
    {
        var root = new GameObject("Rig");
        try
        {
            var host = new GameObject("Driven");
            host.transform.SetParent(root.transform);
            var driver = new GameObject("Driver"); driver.transform.SetParent(root.transform);

            // Seed every keyable slot, with a length of 3. Seeding only the three inside the length
            // would leave slots 3-15 already null, so the 1..15 assertion below would hold for 13 of
            // its 15 slots no matter what the door did.
            var slots = new Transform[16];
            for (int i = 0; i < slots.Length; i++)
            {
                var s = new GameObject("S" + i);
                s.transform.SetParent(root.transform);
                slots[i] = s.transform;
            }

            var con = host.AddComponent<VRCParentConstraint>();
            WriteSources(con, 3, slots);

            Assert.IsTrue(ConstrainedDuplicate.TryReplaceSources(con, driver.transform));

            var so = new SerializedObject(con);
            Assert.AreEqual(1, so.FindProperty("Sources.totalLength").intValue);
            Assert.AreSame(driver.transform,
                so.FindProperty("Sources.source0.SourceTransform").objectReferenceValue);

            for (int i = 1; i < 16; i++)
                Assert.IsNull(so.FindProperty("Sources.source" + i + ".SourceTransform").objectReferenceValue,
                    "slot " + i + " still holds a transform past totalLength=1 — the editor solves it and " +
                    "the client ignores it");

            // The end-to-end statement of the same fact, through the reader that would report it.
            CollectionAssert.IsEmpty(ReportGimmick.ScanConstraintLengths(root));
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void WhatIf_WarnsWhenPhysBonesSitUnderTheConstrainedHierarchy()
    {
        var root = new GameObject("Rig");
        try
        {
            Assert.That(ConstrainedDuplicate.Run(root, whatIf: true), Does.Not.Contain("warnings=["));
            var chain = new GameObject("Chain"); chain.transform.SetParent(root.transform);
            chain.AddComponent<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>();
            Assert.That(ConstrainedDuplicate.Run(root, whatIf: true), Does.Contain("1 VRCPhysBone chain(s) on the constrained bones"));
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void WhatIf_CountsAPhysBoneOnAParentWhoseRootTransformIsInsideTheConstrainedRoot()
    {
        var holder = new GameObject("Holder");
        try
        {
            var rig = new GameObject("Rig"); rig.transform.SetParent(holder.transform);
            var bone = new GameObject("Bone"); bone.transform.SetParent(rig.transform);
            var pb = holder.AddComponent<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>();
            pb.rootTransform = bone.transform;
            Assert.That(ConstrainedDuplicate.Run(rig, whatIf: true), Does.Contain("1 VRCPhysBone chain(s) on the constrained bones"));
        }
        finally { Object.DestroyImmediate(holder); }
    }
}
