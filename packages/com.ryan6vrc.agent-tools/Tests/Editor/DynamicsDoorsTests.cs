using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

// The pure parts of WriteDynamics, DrivePhysBones and ReportPenetration: table, curve and pose parsing, the ramp and jitter windows, the stale-status line (including the defaults a
// caller relies on by omission, which JsonUtility only honours through field initialisers), the closest-point primitive
// under the penetration count, its region and group bucketing and their tables, the frame gate on a live field set's host cycle, and the drive's restore record, whose writer and reader sit a domain reload apart. The
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

    static Transform Node(string name, Transform parent) { var t = new GameObject(name).transform; t.SetParent(parent, false); return t; }

    // A costume armature's bones are not the avatar's, so a region is found by name, and the non-humanoid branch a
    // body bone hangs from (a butt or breast bone) is what separates it from the rest of its humanoid bone.
    [Test]
    public void Region_mapsByNameUpTheAncestors_andNamesTheBranch()
    {
        var root = new GameObject("Costume").transform;
        try
        {
            var hips = Node("Hips", Node("Armature", root)); var butt = Node("Butt_L", hips); var end = Node("Butt_L_end", butt);
            var merged = Node("Chest$a1b2", hips); var breast = Node("Breast_R", merged);
            var human = new Dictionary<string, string> { { "Hips", "Hips" }, { "Chest", "Chest" } };
            Assert.AreEqual("Hips", ReportPenetration.Region(hips, human));
            Assert.AreEqual("Hips>Butt_L", ReportPenetration.Region(end, human));
            Assert.AreEqual("Chest>Breast_R", ReportPenetration.Region(breast, human));
            Assert.AreEqual(ReportPenetration.Unmapped, ReportPenetration.Region(root, human));
            Assert.AreEqual(ReportPenetration.Unmapped, ReportPenetration.Region(null, human));
        }
        finally { Object.DestroyImmediate(root.gameObject); }
    }

    // Merged ears and tail skin to their own bones, so an innermost-group walk keeps them out of a hair chain's bucket.
    [Test]
    public void GroupOf_takesTheInnermostGroup_andOtherwiseOther()
    {
        var root = new GameObject("Avatar").transform;
        try
        {
            var head = Node("Head", root); var back = Node("Phys", Node("Back", head)); var strand = Node("Strand_2", Node("Strand_1", back));
            var front = Node("Phys", Node("Front", head)); var tail = Node("Tail", root);
            var label = ReportPenetration.GroupLabels(root, new[] { back, front, strand.parent });
            Assert.AreEqual("Head/Back/Phys", label[back]);
            Assert.AreEqual("Strand_1", ReportPenetration.GroupOf(strand, root, label));
            Assert.AreEqual("Head/Front/Phys", ReportPenetration.GroupOf(front, root, label));
            Assert.AreEqual(ReportPenetration.Other, ReportPenetration.GroupOf(tail, root, label));
            Assert.AreEqual(ReportPenetration.Other, ReportPenetration.GroupOf(head, root, label));
        }
        finally { Object.DestroyImmediate(root.gameObject); }
    }

    static Dictionary<(string, string), ReportPenetration.Bucket> Buckets(params (string region, string group, int behind, int signed, float depth, float gapSum)[] b)
    {
        var d = new Dictionary<(string, string), ReportPenetration.Bucket>();
        foreach (var x in b) d[(x.region, x.group)] = new ReportPenetration.Bucket { behind = x.behind, signed = x.signed, maxDepthCm = x.depth, gapSumCm = x.gapSum };
        return d;
    }

    // The standoff is the mean over the signed vertices not behind; a bucket with none has no gap, not a zero one.
    [Test]
    public void Table_ordersByBehind_capsWithASum_andMeansTheGapOverVerticesInFront()
    {
        var t = ReportPenetration.Table(Buckets(("Hips>Butt_L", "Back", 3, 7, 1.3f, 6f), ("Chest", "Front", 9, 9, 2f, 0f), ("Spine", "Back", 0, 4, 0f, 12f), ("Neck", "Back", 0, 1, 0f, 0.5f)), 3).Split('\n');
        Assert.AreEqual("Chest | Front | 9/9 | 0 | 2.0 | -", t[1]);
        Assert.AreEqual("Hips>Butt_L | Back | 3/7 | 0 | 1.3 | 1.5", t[2]);
        Assert.AreEqual("Spine | Back | 0/4 | 0 | - | 3.0", t[3]);
        StringAssert.Contains("1 more buckets: behind 0, signed 1", t[4]);
    }

    // Old-versus-new per region needs every row's reading of one bucket on one line, and an absent bucket told apart from an empty one.
    [Test]
    public void Pivot_putsEachBucketOnOneLine_acrossRows()
    {
        var rest = Buckets(("Hips>Butt_L", "Back", 0, 10, 0f, 8f));
        var down = Buckets(("Hips>Butt_L", "Back", 4, 10, 1.5f, 3f), ("Chest", "Front", 2, 2, 0.5f, 0f));
        var p = ReportPenetration.Pivot(new List<(string, Dictionary<(string, string), ReportPenetration.Bucket>)> { ("rest", rest), ("faceDown", down) }).Split('\n');
        Assert.AreEqual("region | group | rest | faceDown", p[1]);
        Assert.AreEqual("Hips>Butt_L | Back | 0/10 g0.8 | 4/10 d1.5 g0.5", p[2]);
        Assert.AreEqual("Chest | Front | - | 2/2 d0.5", p[3]);
    }
}
