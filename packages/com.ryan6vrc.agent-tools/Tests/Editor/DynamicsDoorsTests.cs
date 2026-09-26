using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

// The pure parts of WriteDynamics, DrivePhysBones and ReportPenetration: table and pose parsing (including the defaults a
// caller relies on by omission, which JsonUtility only honours through field initialisers), the closest-point primitive
// under the penetration count, the frame gate on a live field set's host cycle, and the drive's restore record, whose writer and reader sit a domain reload apart. The
// writes and the drive mutate live objects, so they are proven by execute_code on a real avatar (docs/verify.md §Test
// venue), not here.
public class DynamicsDoorsTests
{
    [Test]
    public void Table_omittedFields_takeTheirDefaults()
    {
        Assert.IsNull(WriteDynamics.ParseTable("{\"constraints\":[{\"target\":\"A\",\"sources\":[{\"path\":\"B\"}]}]}", out var t));
        var c = t.constraints[0];
        Assert.AreEqual("VRCRotationConstraint", c.type);
        Assert.AreEqual(1f, c.globalWeight);
        Assert.IsTrue(c.locked);
        Assert.AreEqual(1f, c.sources[0].weight);
        Assert.AreEqual(0, t.moves.Length);
        Assert.AreEqual(0, t.nodes.Length);
    }

    [Test]
    public void Table_nodeWithoutCollider_hasEmptyShape()
    {
        Assert.IsNull(WriteDynamics.ParseTable("{\"nodes\":[{\"parent\":\"Armature/Hips\",\"name\":\"Hinge\"}]}", out var t));
        Assert.AreEqual("", t.nodes[0].collider.shape);
        Assert.AreEqual("", t.nodes[0].rotation);
    }

    [TestCase("{\"physbones\":[{\"physbone\":\"A\",\"set\":[\"pull\"]}]}", "physbones[0]")]
    [TestCase("{\"moves\":[{\"physbone\":\"A\",\"from\":\"B\"}]}", "moves[0]")]
    [TestCase("{\"nodes\":[{\"parent\":\"A\",\"name\":\"x/y\"}]}", "nodes[0]")]
    [TestCase("{\"constraints\":[{\"sources\":[]}]}", "constraints[0]")]
    [TestCase("moves: []", "JSON object")]
    public void Table_malformedRows_refuseByIndex(string json, string named)
    {
        StringAssert.Contains(named, WriteDynamics.ParseTable(json, out _));
    }

    [Test]
    public void Vector_parsesCommaTriplesOnly()
    {
        Assert.AreEqual(new Vector3(55, 0, -1.5f), WriteDynamics.ParseVector("55, 0, -1.5"));
        Assert.AreEqual(new Vector3(1, 2, 3), WriteDynamics.ParseVector("(1,2,3)"));
        Assert.IsNull(WriteDynamics.ParseVector("1,2"));
        Assert.IsNull(WriteDynamics.ParseVector("a,b,c"));
    }

    // A live field set reaches the solver only once its host has been inactive across a frame boundary: re-activating
    // on the arming frame is the same-frame toggle that leaves the chain on its old fields.
    [Test]
    public void HostCycle_reactivatesOnlyOnALaterFrame()
    {
        Assert.IsFalse(WriteDynamics.CycleDue(120, 120));
        Assert.IsTrue(WriteDynamics.CycleDue(120, 121));
    }

    [Test]
    public void Poses_bareArrayParses_andOpsDefaultToZero()
    {
        Assert.IsNull(DrivePhysBones.ParsePoses("[{\"name\":\"legL90\",\"ops\":[{\"bone\":\"UpperLeg_L\",\"pitch\":90}]}]", out var pl));
        var op = pl.poses[0].ops[0];
        Assert.AreEqual(90f, op.pitch);
        Assert.AreEqual(0f, op.yaw);
        Assert.AreEqual("", op.move);
    }

    [TestCase("[]", "empty")]
    [TestCase("[{\"name\":\"rest\"}]", "rest")]
    [TestCase("[{\"name\":\"a\",\"ops\":[{\"pitch\":1}]}]", "bone")]
    [TestCase("[{\"name\":\"a\",\"ops\":[{\"bone\":\"B\",\"move\":\"1,2\"}]}]", "move")]
    [TestCase("[{\"name\":\"Sit\"},{\"name\":\"sit\"}]", "same frame file name")]
    [TestCase("[{\"name\":\"leg L\"},{\"name\":\"leg_L\"}]", "same frame file name")]
    [TestCase("[{\"name\":\"REST\"}]", "'rest'")]
    public void Poses_malformed_refuse(string json, string named)
    {
        StringAssert.Contains(named, DrivePhysBones.ParsePoses(json, out _));
    }

    [Test]
    public void ClosestOnTriangle_signsOnlyAFaceInterior()
    {
        Vector3 a = Vector3.zero, b = Vector3.right, c = Vector3.up;
        Assert.AreEqual(new Vector3(0.25f, 0.25f, 0), ReportPenetration.ClosestOnTriangle(new Vector3(0.25f, 0.25f, 2), a, b, c, out bool face));
        Assert.IsTrue(face);
        Assert.AreEqual(a, ReportPenetration.ClosestOnTriangle(new Vector3(-1, -1, 0), a, b, c, out face));
        Assert.IsFalse(face);
        Assert.AreEqual(new Vector3(0.5f, 0, 0), ReportPenetration.ClosestOnTriangle(new Vector3(0.5f, -3, 1), a, b, c, out face));
        Assert.IsFalse(face);
        var h = ReportPenetration.ClosestOnTriangle(new Vector3(1, 1, 0), a, b, c, out face);
        Assert.AreEqual(0.5f, h.x, 1e-5f); Assert.AreEqual(0.5f, h.y, 1e-5f); Assert.IsFalse(face);
    }

    [Test]
    public void RestoreRecord_roundTrips_andRejectsMalformed()
    {
        var bones = new List<(string, Vector3, Quaternion)> { ("Armature/Hips/UpperLeg_L", new Vector3(0.1f, -0.2f, 3e-7f), new Quaternion(0.1f, 0.2f, 0.3f, 0.9273618f)) };
        var raw = DrivePhysBones.FormatRecord("MANUKA_lilToon", true, bones);
        Assert.IsTrue(DrivePhysBones.TryParseRecord(raw, out var root, out var enable, out var back));
        Assert.AreEqual("MANUKA_lilToon", root);
        Assert.IsTrue(enable);
        Assert.AreEqual(bones, back);
        Assert.IsFalse(DrivePhysBones.TryParseRecord(raw.Replace("\t0.1,", "\tx,"), out _, out _, out _));
        Assert.IsFalse(DrivePhysBones.TryParseRecord("v0\nA\n1", out _, out _, out _));
    }
}
