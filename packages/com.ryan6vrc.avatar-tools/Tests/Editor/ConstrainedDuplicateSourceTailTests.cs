using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;

// ConstrainedDuplicate rewrites a constraint's source list to a single driver. The SDK list type makes that
// a two-step defect: `Clear()` resets `totalLength` and leaves the SLOTS holding their old transforms, so a
// constraint that had several sources keeps them past the new length — where the editor still solves them
// and the client does not (docs/runtime.md §Constraints). The door would author the exact silent no-op
// `ReportGimmick.ScanConstraintLengths` exists to report, and nothing in the public API would show it:
// `Sources.Count` returns `totalLength`, so the residue is invisible to every API-side reader and to the
// inspector. The read side is proven in the agent-tools suite; this pins the WRITE side.
public class ConstrainedDuplicateSourceTailTests
{
    // The public API cannot address a slot past the length, so both the setup and the assertion go through
    // SerializedObject — that asymmetry is the whole reason the defect survives an ordinary code review.
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
            var a = new GameObject("A"); a.transform.SetParent(root.transform);
            var b = new GameObject("B"); b.transform.SetParent(root.transform);
            var c = new GameObject("C"); c.transform.SetParent(root.transform);
            var driver = new GameObject("Driver"); driver.transform.SetParent(root.transform);

            var con = host.AddComponent<VRCParentConstraint>();
            WriteSources(con, 3, a.transform, b.transform, c.transform);

            Assert.IsTrue(ConstrainedDuplicate.TryReplaceSources(con, driver.transform));

            // The door's own contract: exactly the driver solves.
            var so = new SerializedObject(con);
            Assert.AreEqual(1, so.FindProperty("Sources.totalLength").intValue);
            Assert.AreSame(driver.transform,
                so.FindProperty("Sources.source0.SourceTransform").objectReferenceValue);

            // And nothing survives behind it. Asserted over every keyable slot rather than just source1/2,
            // so a future door that writes a longer list cannot leave a tail this test never looks at.
            for (int i = 1; i < 16; i++)
                Assert.IsNull(so.FindProperty("Sources.source" + i + ".SourceTransform").objectReferenceValue,
                    "slot " + i + " still holds a transform past totalLength=1 — the editor solves it and " +
                    "the client ignores it");

            // The end-to-end statement of the same fact, through the reader that would report it.
            CollectionAssert.IsEmpty(ReportGimmick.ScanConstraintLengths(root));
        }
        finally { Object.DestroyImmediate(root); }
    }
}
