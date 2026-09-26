using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

// The pure parts of WriteDynamics, DrivePhysBones and ReportPenetration: table, curve and pose parsing, the ramp and jitter windows, the stale-status line (including the defaults a
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

    [TestCase("{\"physbones\":[{\"physbone\":\"A\",\"remove\":true,\"set\":[\"pull=1\"]}]}", "remove takes no")]
    [TestCase("{\"physbones\":[{\"physbone\":\"A\",\"remove\":true,\"create\":true}]}", "remove takes no")]
    [TestCase("{\"physbones\":[{\"physbone\":\"A\",\"copy\":\"B\"}]}", "copy names create's donor")]
    [TestCase("{\"physbones\":[{\"physbone\":\"A\"}]}", "does nothing")]
    [TestCase("{\"colliders\":[{\"collider\":\"A\"}]}", "colliders[0]")]
    [TestCase("{\"colliders\":[{\"collider\":\"A\",\"set\":[\"radius\"]}]}", "colliders[0]")]
    [TestCase("{\"constraints\":[{\"target\":\"A\",\"remove\":true,\"sources\":[{\"path\":\"B\"}]}]}", "remove takes no sources")]
    public void Table_createRemoveAndColliderRows_refuseMalformed(string json, string named)
    {
        StringAssert.Contains(named, WriteDynamics.ParseTable(json, out _));
    }

    [Test]
    public void Table_createAndColliderRows_parse()
    {
        Assert.IsNull(WriteDynamics.ParseTable("{\"physbones\":[{\"physbone\":\"Hips/Skirt\",\"create\":true,\"copy\":\"Hips/Skirt/S01\"},{\"physbone\":\"Hips/Old\",\"remove\":true}],"
            + "\"colliders\":[{\"collider\":\"pbc/hips\",\"set\":[\"radius=0.14\",\"rotation=0,0,90\"]}],\"constraints\":[{\"target\":\"T\",\"type\":\"VRCParentConstraint\",\"remove\":true}]}", out var t));
        Assert.IsTrue(t.physbones[0].create); Assert.AreEqual("Hips/Skirt/S01", t.physbones[0].copy); Assert.IsFalse(t.physbones[0].remove);
        Assert.IsTrue(t.physbones[1].remove); Assert.AreEqual("", t.physbones[1].copy);
        Assert.AreEqual("pbc/hips", t.colliders[0].collider); Assert.AreEqual(2, t.colliders[0].set.Length);
        Assert.IsTrue(t.constraints[0].remove);
        Assert.IsNull(WriteDynamics.ParseTable("{\"constraints\":[{\"target\":\"A\",\"sources\":[{\"path\":\"B\"}]}]}", out t));
        Assert.IsFalse(t.constraints[0].remove);
        Assert.AreEqual(0, t.colliders.Length);
    }

    // A physbone curve crosses JsonUtility as a string: time:value keys joined by straight lines, the shape a hand-keyed
    // radius curve took (flat to mid-chain, then rising to full radius at the tip).
    [Test]
    public void Curve_timeValueKeys_joinLinearly()
    {
        Assert.IsNull(WriteDynamics.ParseCurve("0:0.47, 0.5:0.47, 1:1", out var c));
        Assert.AreEqual(3, c.length);
        Assert.AreEqual(0.47f, c.Evaluate(0.25f), 1e-5f);
        Assert.AreEqual(0.735f, c.Evaluate(0.75f), 1e-5f);
        Assert.IsNull(WriteDynamics.ParseCurve("", out c));
        Assert.AreEqual(0, c.length);
        StringAssert.Contains("time:value", WriteDynamics.ParseCurve("0,0.5", out _));
        StringAssert.Contains("strictly increase", WriteDynamics.ParseCurve("0:1,0:2", out _));
    }

    // A ramp must land exactly on the pose (the hold is timed from arrival) and start exactly at the held pose.
    [Test]
    public void Ramp_easesFromZeroAndArrivesExactly()
    {
        Assert.AreEqual(0f, DrivePhysBones.Ramp01(0f, 1f));
        Assert.AreEqual(0.5f, DrivePhysBones.Ramp01(0.5f, 1f), 1e-6f);
        Assert.Less(DrivePhysBones.Ramp01(0.1f, 1f), 0.1f);
        Assert.AreEqual(1f, DrivePhysBones.Ramp01(1f, 1f));
        Assert.AreEqual(1f, DrivePhysBones.Ramp01(1.7f, 1f));
        Assert.AreEqual(1f, DrivePhysBones.Ramp01(0f, 0f));
    }

    // Decay versus a limit cycle needs two jitter readings per hold, each late in its half so the snap transient at
    // the hold's start falls in neither.
    [TestCase(2.5f, 0.70f, 0)]
    [TestCase(2.5f, 1.00f, 1)]
    [TestCase(2.5f, 1.25f, 1)]
    [TestCase(2.5f, 1.60f, 0)]
    [TestCase(2.5f, 2.10f, 2)]
    [TestCase(2.5f, 2.50f, 2)]
    [TestCase(0.6f, 0.20f, 1)]
    [TestCase(0.6f, 0.45f, 2)]
    public void Jitter_windowsCloseEachHalfOfTheHold(float hold, float elapsed, int window)
    {
        Assert.AreEqual(window, DrivePhysBones.JitterWindow(elapsed, hold));
    }

    [Test]
    public void Poses_rampDefaultsToSnap_andRefusesNegative()
    {
        Assert.IsNull(DrivePhysBones.ParsePoses("[{\"name\":\"a\"},{\"name\":\"b\",\"ramp\":1}]", out var pl));
        Assert.AreEqual(0f, pl.poses[0].ramp);
        Assert.AreEqual(1f, pl.poses[1].ramp);
        StringAssert.Contains("ramp", DrivePhysBones.ParsePoses("[{\"name\":\"a\",\"ramp\":-1}]", out _));
    }

    // Status() must never hand back an earlier play session's result as current: the stale line keeps the log path but
    // loses the verdict token a reader would match on.
    [Test]
    public void StaleRecord_masksItsVerdict_andKeepsItsLog()
    {
        var s = PlaySession.Stale("[DrivePhysBones]", "drive", "[DrivePhysBones] Drive A => OK | stage=new | log=Assets/Agent/RunLogs/x.md");
        StringAssert.DoesNotContain("=> OK", s);
        StringAssert.Contains("was OK", s);
        StringAssert.Contains("log=Assets/Agent/RunLogs/x.md", s);
        StringAssert.Contains("earlier play session", s);
    }
}
