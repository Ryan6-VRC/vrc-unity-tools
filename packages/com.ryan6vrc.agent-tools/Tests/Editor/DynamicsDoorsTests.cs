using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

// The pure parts of WriteDynamics, DrivePhysBones and ReportClipping: table, curve and pose parsing, the ramp and jitter windows, the stale-status line (including the defaults a
// caller relies on by omission, which JsonUtility only honours through field initialisers), the closest-point primitive
// under the clipping measure, its crossing length, its region and group bucketing and their tables, the frame gate on a live field set's host cycle, and the drive's restore record, whose writer and reader sit a domain reload apart. The
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
        Assert.AreEqual(new Vector3(0.25f, 0.25f, 0), ReportClipping.ClosestOnTriangle(new Vector3(0.25f, 0.25f, 2), a, b, c, out bool face));
        Assert.IsTrue(face);
        Assert.AreEqual(a, ReportClipping.ClosestOnTriangle(new Vector3(-1, -1, 0), a, b, c, out face));
        Assert.IsFalse(face);
        Assert.AreEqual(new Vector3(0.5f, 0, 0), ReportClipping.ClosestOnTriangle(new Vector3(0.5f, -3, 1), a, b, c, out face));
        Assert.IsFalse(face);
        var h = ReportClipping.ClosestOnTriangle(new Vector3(1, 1, 0), a, b, c, out face);
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
        StringAssert.Contains("time:value", WriteDynamics.ParseCurve("0:1:2", out _));
    }

    // A read must hand a write the curve it read. A hand-keyed curve with smooth tangents (Unity's default for a
    // key added in the inspector) printed as bare time:value would write back piecewise-linear, matching on every
    // key and differing everywhere between them — the loss no scalar comparison shows.
    [Test]
    public void Curve_formatThenParse_roundTripsLinearAndAuthoredTangents()
    {
        Assert.IsNull(WriteDynamics.ParseCurve("0:0,0.4:0,1:1", out var linear));
        Assert.AreEqual("0:0,0.4:0,1:1", WriteDynamics.FormatCurve(linear), "a linear curve prints in the short form");

        var smooth = new AnimationCurve(new Keyframe(0f, 0f, 0f, 0f), new Keyframe(0.4f, 0f, 0f, 0f), new Keyframe(1f, 1f, 2.5f, 0f));
        var text = WriteDynamics.FormatCurve(smooth);
        StringAssert.Contains("1:1:2.5:0", text);
        Assert.IsNull(WriteDynamics.ParseCurve(text, out var back), text);
        foreach (var t in new[] { 0.1f, 0.3f, 0.55f, 0.7f, 0.9f })
            Assert.AreEqual(smooth.Evaluate(t), back.Evaluate(t), 1e-5f, "t=" + t + " via " + text);

        Assert.AreEqual("", WriteDynamics.FormatCurve(new AnimationCurve()));
        Assert.AreEqual("", WriteDynamics.FormatCurve(null));
    }

    [Test]
    public void Curve_weightedTangents_printAFormParseCurveRefuses()
    {
        var k = new Keyframe(1f, 1f, 0f, 0f, 0.5f, 0.5f) { weightedMode = WeightedMode.Both };
        var text = WriteDynamics.FormatCurve(new AnimationCurve(new Keyframe(0f, 0f), k));
        StringAssert.Contains("weighted tangents", text);
        Assert.IsNotNull(WriteDynamics.ParseCurve(text, out _), "a curve the form cannot carry must not read back as a different one");
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

    [TestCase("every", 1)]
    [TestCase("every:3", 3)]
    [TestCase("end", 0)]
    [TestCase("none", 0)]
    [TestCase("every:0", -1)]
    [TestCase("every:", -1)]
    [TestCase("every:+2", -1)]
    [TestCase("every:2.5", -1)]
    public void EveryStride_readsEveryAndEveryN(string sample, int stride) => Assert.AreEqual(stride, DrivePhysBones.EveryStride(sample));

    [Test]
    public void Poses_sampleTakesEveryN_andRefusesAMalformedStride()
    {
        Assert.IsNull(DrivePhysBones.ParsePoses("[{\"name\":\"a\",\"sample\":\"every:3\"}]", out var pl));
        Assert.AreEqual("every:3", pl.poses[0].sample);
        StringAssert.Contains("every:N", DrivePhysBones.ParsePoses("[{\"name\":\"a\",\"sample\":\"every:0\"}]", out _));
    }

    // The cap refuses on this total: rest 2.5 + a (2.5 drive hold) + b (1 ramp + 6 own hold) + c (0.5 ramp + 0 hold) = 12.5.
    [Test]
    public void ProgramSeconds_countsRestHold_rampsAndEachRowsHold()
    {
        Assert.IsNull(DrivePhysBones.ParsePoses("[{\"name\":\"a\"},{\"name\":\"b\",\"ramp\":1,\"hold\":6},{\"name\":\"c\",\"ramp\":0.5,\"hold\":0}]", out var pl));
        Assert.AreEqual(12.5f, DrivePhysBones.ProgramSeconds(pl, 2.5f), 1e-4f);
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
            Assert.AreEqual("Hips", ReportClipping.Region(hips, human));
            Assert.AreEqual("Hips>Butt_L", ReportClipping.Region(end, human));
            Assert.AreEqual("Chest>Breast_R", ReportClipping.Region(breast, human));
            Assert.AreEqual(ReportClipping.Unmapped, ReportClipping.Region(root, human));
            Assert.AreEqual(ReportClipping.Unmapped, ReportClipping.Region(null, human));
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
            var label = ReportClipping.GroupLabels(root, new[] { back, front, strand.parent });
            Assert.AreEqual("Head/Back/Phys", label[back]);
            Assert.AreEqual("Strand_1", ReportClipping.GroupOf(strand, root, label));
            Assert.AreEqual("Head/Front/Phys", ReportClipping.GroupOf(front, root, label));
            Assert.AreEqual(ReportClipping.Other, ReportClipping.GroupOf(tail, root, label));
            Assert.AreEqual(ReportClipping.Other, ReportClipping.GroupOf(head, root, label));
        }
        finally { Object.DestroyImmediate(root.gameObject); }
    }

    static Dictionary<(string, string, string), ReportClipping.Bucket> Buckets(params (string region, string group, int behind, int signed, float depth, float gapSum, float cross)[] b)
    {
        var d = new Dictionary<(string, string, string), ReportClipping.Bucket>();
        foreach (var x in b) d[("Body", x.region, x.group)] = new ReportClipping.Bucket { behind = x.behind, signed = x.signed, gapN = x.signed - x.behind, maxDepthCm = x.depth, gapSumCm = x.gapSum, crossCm = x.cross };
        return d;
    }

    // A crossing ranks above any vertex count, since a vertex count reads zero through a shallow one; the standoff is the
    // mean over the signed vertices not behind, and a bucket with none has no gap, not a zero one.
    [Test]
    public void Table_ordersByCrossingThenBehind_capsWithASum_andMeansTheGapOverVerticesInFront()
    {
        var t = ReportClipping.Table(Buckets(("Hips>Butt_L", "Back", 3, 7, 1.3f, 6f, 0f), ("Chest", "Front", 9, 9, 2f, 0f, 0f), ("Spine", "Back", 0, 4, 0f, 12f, 4.5f), ("Neck", "Back", 0, 1, 0f, 0.5f, 0f)), 3).Split('\n');
        Assert.AreEqual("body | region | group | crossingCm | behind/signed | edgeNearest | maxDepthCm | throughCm | meanGapCm", t[0]);
        Assert.AreEqual("Body | Spine | Back | 4.5 | 0/4 | 0 | - | - | 3.0", t[1]);
        Assert.AreEqual("Body | Chest | Front | - | 9/9 | 0 | 2.0 | - | -", t[2]);
        Assert.AreEqual("Body | Hips>Butt_L | Back | - | 3/7 | 0 | 1.3 | - | 1.5", t[3]);
        StringAssert.Contains("1 more buckets: crossingCm 0.0, behind 0, signed 1", t[4]);
    }

    // Old-versus-new per region needs every row's reading of one bucket on one line, and an absent bucket told apart from an empty one.
    [Test]
    public void Pivot_putsEachBucketOnOneLine_acrossRows()
    {
        var rest = Buckets(("Hips>Butt_L", "Back", 0, 10, 0f, 8f, 0f));
        var down = Buckets(("Hips>Butt_L", "Back", 4, 10, 1.5f, 3f, 0f), ("Chest", "Front", 2, 2, 0.5f, 0f, 1.2f));
        down[("Body", "Chest", "Front")].edge = 3;
        var p = ReportClipping.Pivot(new List<(string, Dictionary<(string, string, string), ReportClipping.Bucket>)> { ("rest", rest), ("faceDown", down) }).Split('\n');
        Assert.AreEqual("body | region | group | rest | faceDown", p[1]);
        Assert.AreEqual("Body | Chest | Front | - | x1.2 2/2 e3 d0.5", p[2]);
        Assert.AreEqual("Body | Hips>Butt_L | Back | 0/10 e0 g0.8 | 4/10 e0 d1.5 g0.5", p[3]);
    }

    // Two triangles crossing along a known segment: the vertical one's slice through z = 0 runs x 0.25 → 0.75 at y 0.5,
    // all inside the flat one. Moved clear of the plane it reads nothing, and a pair sharing a corner reads nothing either,
    // even crossing beyond it: faces of one merged mesh meet at shared corners without crossing.
    [Test]
    public void CrossLength_measuresTheSegmentTwoTrianglesShare()
    {
        var hits = new List<Vector3>();
        Vector3 a = Vector3.zero, b = new Vector3(2, 0, 0), c = new Vector3(0, 2, 0);
        Vector3 x = new Vector3(0.25f, 0.5f, -1), y = new Vector3(1.25f, 0.5f, -1), z = new Vector3(0.25f, 0.5f, 1);
        Assert.AreEqual(0.5f, ReportClipping.CrossLength(a, b, c, x, y, z, hits, out var mid), 1e-5f);
        Assert.AreEqual(0.5f, mid.x, 1e-5f); Assert.AreEqual(0.5f, mid.y, 1e-5f); Assert.AreEqual(0f, mid.z, 1e-5f);
        var up = new Vector3(0, 0, 3);
        Assert.AreEqual(0f, ReportClipping.CrossLength(a, b, c, x + up, y + up, z + up, hits, out _));
        Assert.AreEqual(0f, ReportClipping.CrossLength(a, b, c, a, new Vector3(1, 1, -1), new Vector3(1, 1, 1), hits, out _));
    }

    static SkinnedMeshRenderer Sheet(Transform root, string name, Vector3[] v, int[][] submeshes, Material[] mats = null)
    {
        var t = Node(name, root); var mesh = new Mesh { vertices = v, subMeshCount = submeshes.Length };
        for (int i = 0; i < submeshes.Length; i++) mesh.SetTriangles(submeshes[i], i);
        mesh.boneWeights = v.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1f }).ToArray(); mesh.bindposes = new[] { Matrix4x4.identity };
        var smr = t.gameObject.AddComponent<SkinnedMeshRenderer>(); smr.sharedMesh = mesh; smr.bones = new[] { root }; if (mats != null) smr.sharedMaterials = mats;
        return smr;
    }

    static void Drop(Transform root) { foreach (var s in root.GetComponentsInChildren<SkinnedMeshRenderer>()) Object.DestroyImmediate(s.sharedMesh); Object.DestroyImmediate(root.gameObject); }

    static readonly Vector3[] Quad = { new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0), new Vector3(0.5f, 1, 0), new Vector3(-0.5f, 1, 0) };   // faces +z

    // A garment sheet through a body sheet along x = z = 0 for y 0.2 → 0.8: 60 cm of crossing, split over both faces of
    // each. Of the two sampled garment vertices one sits 3 cm behind the body and one 3 cm in front of it.
    [Test]
    public void Measure_sumsTheCrossing_andSignsTheSampledVertices()
    {
        var root = new GameObject("ClipRoot").transform;
        try
        {
            Sheet(root, "Body", Quad, new[] { new[] { 0, 1, 2, 0, 2, 3 } });
            Sheet(root, "Skirt", new[] { new Vector3(0, 0.2f, -0.03f), new Vector3(0, 0.8f, -0.03f), new Vector3(0, 0.8f, 0.03f), new Vector3(0, 0.2f, 0.03f) }, new[] { new[] { 0, 1, 2, 0, 2, 3 } });
            Assert.IsNull(ReportClipping.Resolve(root, new[] { "Body" }, new[] { "Skirt" }, out var bs, out var gs));
            var r = ReportClipping.Measure(root, bs, gs, null);
            Assert.AreEqual(60f, r.crossCm, 0.1f);
            Assert.AreEqual(2, r.sampled); Assert.AreEqual(2, r.signed); Assert.AreEqual(1, r.behind);
            Assert.AreEqual(3f, r.maxDepthCm, 0.01f);
            Assert.AreEqual(0f, r.throughCm);
            Assert.AreEqual(60f, r.buckets[("Body", ReportClipping.Unmapped, ReportClipping.Other)].crossCm, 0.1f);
        }
        finally { Drop(root); }
    }

    // A body tip poking 1 cm out through a flat garment: a crossing 3.3 cm long, and the tip read as through the sheet.
    [Test]
    public void Measure_readsABodyVertexOutThroughTheGarment()
    {
        var root = new GameObject("ClipRoot").transform;
        try
        {
            Sheet(root, "Body", new[] { new Vector3(0, 0.4f, -0.05f), new Vector3(0, 0.6f, -0.05f), new Vector3(0.1f, 0.5f, 0.01f) }, new[] { new[] { 0, 1, 2 } });
            Sheet(root, "Skirt", Quad, new[] { new[] { 0, 1, 2, 0, 2, 3 } });
            Assert.IsNull(ReportClipping.Resolve(root, new[] { "Body" }, new[] { "Skirt" }, out var bs, out var gs));
            var r = ReportClipping.Measure(root, bs, gs, null);
            Assert.AreEqual(3.33f, r.crossCm, 0.02f);
            Assert.AreEqual(1f, r.throughCm, 0.01f);
        }
        finally { Drop(root); }
    }

    // A stocking merged into another renderer is named by its material; a ribbon merged into the body renderer is a
    // garment there, and the body keeps only the rest.
    [Test]
    public void Resolve_narrowsByMaterial_andSplitsARendererThatIsBoth()
    {
        var root = new GameObject("ClipRoot").transform;
        var mats = new[] { new Material(Shader.Find("Standard")) { name = "Skin" }, new Material(Shader.Find("Standard")) { name = "Stocking_Black" } };
        try
        {
            Sheet(root, "Body", Quad, new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } }, mats);
            Assert.IsNull(ReportClipping.ResolveSurfaces(root, new[] { "Body#Stocking" }, "bodies", out var s));
            CollectionAssert.AreEqual(new[] { 0, 2, 3 }, s[0].tris);
            Assert.AreEqual("Body#Stocking", s[0].label);
            Assert.IsNull(ReportClipping.ResolveSurfaces(root, new[] { "Body#Skin" }, "bodies", out s));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, s[0].tris);
            StringAssert.Contains("Skin, Stocking_Black", ReportClipping.ResolveSurfaces(root, new[] { "Body#Lace" }, "bodies", out _));
            Assert.IsNull(ReportClipping.Resolve(root, new[] { "Body" }, new[] { "Body#Stocking" }, out var bs, out _));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, bs[0].tris);
            StringAssert.Contains("keeps no triangle", ReportClipping.Resolve(root, new[] { "Body" }, new[] { "Body" }, out _, out _));
        }
        finally { Drop(root); foreach (var m in mats) Object.DestroyImmediate(m); }
    }

    // A row sampled every frame reports each metric at its worst frame, which need not be one frame; the standoff is the
    // latest frame's, since a peak of a mean describes no frame at all.
    [Test]
    public void Peak_keepsEachMetricsWorstFrame_andTheLatestStandoff()
    {
        ReportClipping.Result R(float cross, int behind, int signed, float gap, int gapN) => new ReportClipping.Result
        {
            crossCm = cross, behind = behind, signed = signed,
            buckets = new Dictionary<(string, string, string), ReportClipping.Bucket> { { ("Body", "LeftUpperLeg", "Skirt"), new ReportClipping.Bucket { crossCm = cross, behind = behind, signed = signed, gapSumCm = gap, gapN = gapN } } }
        };
        var acc = ReportClipping.Peak(ReportClipping.Peak(default, R(2f, 1, 5, 4f, 4)), R(1f, 3, 4, 1f, 1));
        Assert.AreEqual(2f, acc.crossCm); Assert.AreEqual(3, acc.behind); Assert.AreEqual(5, acc.signed);
        var b = acc.buckets[("Body", "LeftUpperLeg", "Skirt")];
        Assert.AreEqual(2f, b.crossCm); Assert.AreEqual(3, b.behind); Assert.AreEqual(1f, b.gapSumCm); Assert.AreEqual(1, b.gapN);
    }

    // hold and sample are left out of most rows, so their defaults are the drive's hold and one measure at its end; an
    // explicit hold of 0 is a row that measures the motion and moves on, not an omission.
    [Test]
    public void Poses_holdAndSample_defaultToTheDrivesHoldAndTheEnd()
    {
        Assert.IsNull(DrivePhysBones.ParsePoses("[{\"name\":\"a\"},{\"name\":\"b\",\"hold\":0,\"sample\":\"every\"}]", out var pl));
        Assert.Less(pl.poses[0].hold, 0f); Assert.AreEqual("end", pl.poses[0].sample);
        Assert.AreEqual(0f, pl.poses[1].hold); Assert.AreEqual("every", pl.poses[1].sample);
        StringAssert.Contains("sample is end, every, every:N", DrivePhysBones.ParsePoses("[{\"name\":\"a\",\"sample\":\"often\"}]", out _));
    }

    // A baseline is another drive's rest row, possibly from an earlier play session, so it round-trips through text.
    [Test]
    public void RestRow_roundTrips_andRejectsMalformed()
    {
        var raw = DrivePhysBones.FormatRest("Root/Av", 3, "12:00:00", new List<(string, Vector3)> { ("Skirt/S1/tip", new Vector3(0.1f, -0.2f, 0.3f)) });
        Assert.IsTrue(DrivePhysBones.TryParseRest(raw, out string root, out int session, out string started, out var tips));
        Assert.AreEqual("Root/Av", root); Assert.AreEqual(3, session); Assert.AreEqual("12:00:00", started); Assert.AreEqual(new Vector3(0.1f, -0.2f, 0.3f), tips["Skirt/S1/tip"]);
        Assert.IsFalse(DrivePhysBones.TryParseRest(raw.Replace("0.1,", "x,"), out _, out _, out _, out _));
        Assert.IsFalse(DrivePhysBones.TryParseRest("v1\t1\tx", out _, out _, out _, out _));   // a record without its root
    }

    // What a fix costs at rest: each chain's tips against the baseline's, worst chain first, and a tip the baseline never
    // drove named as such rather than read as unmoved.
    [Test]
    public void RestShift_ranksChainsByTheirWorstTip_andNamesTipsWithoutABaseline()
    {
        var baseline = new Dictionary<string, Vector3> { { "S1/tip", Vector3.zero }, { "S2/tip", Vector3.zero } };
        var chains = new List<(string, IList<(string, Vector3)>)>
        {
            ("S2", new List<(string, Vector3)> { ("S2/tip", new Vector3(0, 0.01f, 0)), ("S2/new", Vector3.zero) }),
            ("S1", new List<(string, Vector3)> { ("S1/tip", new Vector3(0.03f, 0, 0)) }),
        };
        var t = DrivePhysBones.RestShift(baseline, chains, out float max, out float mean).Split('\n');
        Assert.AreEqual(3f, max, 1e-4f); Assert.AreEqual(2f, mean, 1e-4f);
        Assert.AreEqual("S1 | 3.0 | 3.0 | tip", t[1]);
        Assert.AreEqual("S2 | 1.0 | 1.0 | tip", t[2]);
        StringAssert.Contains("1 tips have no baseline reading", t[3]);
    }
}
