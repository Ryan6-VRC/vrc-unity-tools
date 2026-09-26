using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

// The pure parts of WriteDynamics and DrivePhysBones: table and pose parsing (including the defaults a caller relies on
// by omission, which JsonUtility only honours through field initialisers) and the closest-point primitive under the
// penetration count. The writes and the drive mutate live objects, so they are proven by execute_code on a real
// avatar (docs/verify.md §Test venue), not here.
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
    public void Poses_malformed_refuse(string json, string named)
    {
        StringAssert.Contains(named, DrivePhysBones.ParsePoses(json, out _));
    }

    [Test]
    public void ClosestOnTriangle_coversFaceEdgeAndVertexRegions()
    {
        Vector3 a = Vector3.zero, b = Vector3.right, c = Vector3.up;
        Assert.AreEqual(new Vector3(0.25f, 0.25f, 0), DrivePhysBones.ClosestOnTriangle(new Vector3(0.25f, 0.25f, 2), a, b, c));
        Assert.AreEqual(a, DrivePhysBones.ClosestOnTriangle(new Vector3(-1, -1, 0), a, b, c));
        Assert.AreEqual(new Vector3(0.5f, 0, 0), DrivePhysBones.ClosestOnTriangle(new Vector3(0.5f, -3, 1), a, b, c));
        var h = DrivePhysBones.ClosestOnTriangle(new Vector3(1, 1, 0), a, b, c);
        Assert.AreEqual(0.5f, h.x, 1e-5f); Assert.AreEqual(0.5f, h.y, 1e-5f);
    }
}
