using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

// ReportClearance proof obligations, over a synthetic rig in a throwaway Single scene (mirrors
// ReportGimmickTests): a body SMR whose vertices form a known ring, one physbone chain of known joint
// positions and radius, and one capsule collider at a known offset — so every printed distance is a number
// the test computed by hand, not one the tool computed twice.
[Category("ReportClearance")]
public class ReportClearanceTests
{
    private static readonly List<string> Artifacts = new List<string>();
    private readonly List<Mesh> _meshes = new List<Mesh>();
    private GameObject _root;
    private static readonly Regex FailRe = new Regex(@"^\[ReportClearance\] FAIL: ");

    [OneTimeTearDown]
    public void DeleteWrittenSnapshots()
    {
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), new List<string>());
        Artifacts.Clear();
    }

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _root = new GameObject("ClearanceRoot");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root != null) Object.DestroyImmediate(_root);
        foreach (var m in _meshes) if (m != null) Object.DestroyImmediate(m);
        _meshes.Clear();
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────────────────

    private static GameObject Child(GameObject parent, string name, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.transform.localPosition = localPos;
        return go;
    }

    // A body: a ring of vertices of radius `ringR` at height `y` around the world Y axis, skinned to `bone`.
    private SkinnedMeshRenderer Body(string name, Transform bone, float ringR, float y, int n = 64)
    {
        var go = Child(_root, name, Vector3.zero);
        var mesh = new Mesh(); _meshes.Add(mesh);
        var v = new Vector3[n]; var bw = new BoneWeight[n];
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            v[i] = new Vector3(Mathf.Sin(a) * ringR, y, Mathf.Cos(a) * ringR);
            bw[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        }
        mesh.vertices = v; mesh.boneWeights = bw; mesh.bindposes = new[] { Matrix4x4.identity };
        mesh.triangles = Enumerable.Range(0, n - 2).SelectMany(i => new[] { 0, i + 1, i + 2 }).ToArray();
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh; smr.bones = new[] { bone };
        return smr;
    }

    private static string LogPath(string summary)
    {
        int i = summary.IndexOf("| log=");
        Assert.That(i, Is.GreaterThan(0), "no log trailer in: " + summary);
        var path = summary.Substring(i + 6).Trim();
        Artifacts.Add(path);
        return path;
    }

    // ── Refusals ───────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void UnresolvedRootIsBareFail()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var r = ReportClearance.Run("NoSuchObject", "Body");
        StringAssert.StartsWith("[ReportClearance] FAIL:", r);
        StringAssert.DoesNotContain("| log=", r);
    }

    [Test]
    public void BodyWithoutRendererIsBareFail()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        Child(_root, "Body", Vector3.zero);
        Child(_root, "Chain", Vector3.zero).AddComponent<VRCPhysBone>();
        var r = ReportClearance.Run(_root.name, "Body");
        StringAssert.StartsWith("[ReportClearance] FAIL: bodyMesh", r);
    }

    [Test]
    public void PrefixMatchingNoChainIsBareFail()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var hips = Child(_root, "Hips", Vector3.zero);
        Body("Body", hips.transform, 0.1f, 0f);
        Child(_root, "Chain", Vector3.zero).AddComponent<VRCPhysBone>();
        var r = ReportClearance.Run(_root.name, "Body", "Skirt");
        StringAssert.StartsWith("[ReportClearance] FAIL: chainPrefix", r);
    }

    // ── Measurement ────────────────────────────────────────────────────────────────────────────────────

    // Body ring r=0.10 at y=0. Chain root at (0.13, 0.03, 0) with one child 0.10 below it; radius 0.02, no
    // curve. Nearest body vertex to the root is on the ring at azimuth 90°: (0.10, 0, 0) → gap = sqrt(0.03²+0.03²)
    // = 0.0424, slack = 0.0224; that vertex is inside the weight band (radius + 0.03), so the body readout
    // sees Hips. Collider: capsule on Hips, position (0.13, 0, 0), core half-height 0.05 along local Y,
    // radius 0.02 — its axis passes through the root joint (segment distance 0 → colliderSlack = −0.04) and
    // the child at (0.13, −0.07, 0) sits 0.02 below its end (slack −0.02), so the chain's rest contact is
    // 4 cm, and the collider's end-to-end reads 0.10 + 2×0.02 = 0.14.
    [Test]
    public void ChainGapsAndColliderContactReadAsComputed()
    {
        var hips = Child(_root, "Hips", Vector3.zero);
        Body("Body", hips.transform, 0.10f, 0f);
        var chainRoot = Child(hips, "Skirt_1", new Vector3(0.13f, 0.03f, 0f));
        Child(chainRoot, "Skirt_1.001", new Vector3(0f, -0.10f, 0f));
        var pb = chainRoot.AddComponent<VRCPhysBone>();
        pb.radius = 0.02f; pb.radiusCurve = new AnimationCurve();
        pb.limitType = VRCPhysBoneBase.LimitType.Hinge; pb.maxAngleX = 30f;
        var colGo = Child(hips, "Col_Pelvis", Vector3.zero);
        var col = colGo.AddComponent<VRCPhysBoneCollider>();
        col.rootTransform = hips.transform; col.shapeType = VRCPhysBoneColliderBase.ShapeType.Capsule;
        col.radius = 0.02f; col.height = 0.10f; col.position = new Vector3(0.13f, 0f, 0f); col.rotation = Quaternion.identity;
        pb.colliders.Add(col);
        // A second chain the collider does NOT reference, so the audit column has content.
        var other = Child(hips, "Skirt_2", new Vector3(-0.15f, 0.05f, 0f));
        other.AddComponent<VRCPhysBone>().radius = 0.02f;

        var r = ReportClearance.Run(_root.name, "Body");
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("chains=2", r);
        StringAssert.Contains("colliders=1", r);
        StringAssert.Contains("restContact=1", r);
        StringAssert.Contains("insideBodyAtRest=0", r);
        StringAssert.Contains("lateralLocked=1", r);
        StringAssert.Contains("surface=scene", r);
        var body = File.ReadAllText(LogPath(r));
        StringAssert.Contains("restContactCm=4.0cm", body);
        StringAssert.Contains("endToEnd=14.0cm", body);
        StringAssert.Contains("| 4.2cm | 2.2cm |", body);   // bodyGap | bodySlack on the root joint
        StringAssert.Contains("**lateralLocked**", body);
        StringAssert.Contains("| Skirt_1 | Skirt_2 |", body);   // referencing | notReferencing on the collider row
        StringAssert.Contains("Hips=1.00", body);              // body weights near the chain
    }

    // Same rig under a root scaled ×2: every world distance doubles (bake must not apply the scale twice), the
    // joint radius scales with the joint, and the capsule's extent scales with its root. Root joint: gap
    // 2×0.0424 = 8.5cm, radius 4.0cm, slack 4.5cm; capsule endToEnd 2×0.14 = 28.0cm.
    [Test]
    public void ScaledRootDoublesEveryDistanceOnce()
    {
        _root.transform.localScale = Vector3.one * 2f;
        var hips = Child(_root, "Hips", Vector3.zero);
        Body("Body", hips.transform, 0.10f, 0f);
        var chainRoot = Child(hips, "Skirt_1", new Vector3(0.13f, 0.03f, 0f));
        Child(chainRoot, "Skirt_1.001", new Vector3(0f, -0.10f, 0f));
        var pb = chainRoot.AddComponent<VRCPhysBone>();
        pb.radius = 0.02f; pb.radiusCurve = new AnimationCurve();
        var col = Child(hips, "Col_Pelvis", Vector3.zero).AddComponent<VRCPhysBoneCollider>();
        col.rootTransform = hips.transform; col.shapeType = VRCPhysBoneColliderBase.ShapeType.Capsule;
        col.radius = 0.02f; col.height = 0.10f; col.position = new Vector3(0.13f, 0f, 0f); col.rotation = Quaternion.identity;
        pb.colliders.Add(col);

        var r = ReportClearance.Run(_root.name, "Body");
        var body = File.ReadAllText(LogPath(r));
        StringAssert.Contains("| 8.5cm | 4.5cm |", body);
        StringAssert.Contains("endToEnd=28.0cm", body);
        StringAssert.Contains("restContactCm=8.0cm", body);   // 2 × the unscaled 4.0cm contact
    }

    // A disabled collider is out of the SDK's collision scene: listed with a marker, never measured.
    [Test]
    public void DisabledColliderIsListedNotMeasured()
    {
        var hips = Child(_root, "Hips", Vector3.zero);
        Body("Body", hips.transform, 0.10f, 0f);
        var chainRoot = Child(hips, "Skirt_1", new Vector3(0.13f, 0.03f, 0f));
        Child(chainRoot, "Skirt_1.001", new Vector3(0f, -0.10f, 0f));
        var pb = chainRoot.AddComponent<VRCPhysBone>(); pb.radius = 0.02f;
        var col = Child(hips, "Col_Pelvis", Vector3.zero).AddComponent<VRCPhysBoneCollider>();
        col.rootTransform = hips.transform; col.shapeType = VRCPhysBoneColliderBase.ShapeType.Capsule;
        col.radius = 0.02f; col.height = 0.10f; col.position = new Vector3(0.13f, 0f, 0f);
        col.enabled = false;
        pb.colliders.Add(col);

        var r = ReportClearance.Run(_root.name, "Body");
        StringAssert.Contains("restContact=0", r);
        var body = File.ReadAllText(LogPath(r));
        StringAssert.Contains("Col_Pelvis (inactive)", body);
        StringAssert.Contains("| `Skirt_1` | 2 | — |", body);
    }

    [Test]
    public void SegmentSegmentDistanceHandlesCrossingAndParallel()
    {
        Assert.AreEqual(1f, ReportClearance.SegmentSegmentDistance(Vector3.zero, Vector3.right, new Vector3(0.5f, 1f, -1f), new Vector3(0.5f, 1f, 1f)), 1e-5f);
        Assert.AreEqual(2f, ReportClearance.SegmentSegmentDistance(Vector3.zero, Vector3.right, new Vector3(3f, 0f, 0f), new Vector3(4f, 0f, 0f)), 1e-5f);
        Assert.AreEqual(0f, ReportClearance.SegmentSegmentDistance(Vector3.zero, Vector3.up, new Vector3(-1f, 0.5f, 0f), new Vector3(1f, 0.5f, 0f)), 1e-5f);
    }

    // A simulated joint inside the ring: root at (0.09, 0.10, 0) with its child 0.10 below at (0.09, 0, 0),
    // radius 0.03 → the child's gap is 0.01, slack −0.02 → insideBodyCm=2.0cm and the summary counts it, with
    // no collider at all. The root joint is never counted (the anchor is not simulated), and a chain with no
    // collider prints `—` for rest contact rather than a clean zero.
    [Test]
    public void ChainInsideBodyAtRestIsCountedWithoutAnyCollider()
    {
        var hips = Child(_root, "Hips", Vector3.zero);
        Body("Body", hips.transform, 0.10f, 0f);
        var chainRoot = Child(hips, "Skirt_1", new Vector3(0.09f, 0.10f, 0f));
        Child(chainRoot, "Skirt_1.001", new Vector3(0f, -0.10f, 0f));
        chainRoot.AddComponent<VRCPhysBone>().radius = 0.03f;

        var r = ReportClearance.Run(_root.name, "Body", "Skirt_1");   // bare root name scopes too
        StringAssert.Contains("insideBodyAtRest=1", r);
        StringAssert.Contains("restContact=0", r);
        StringAssert.Contains("colliders=0", r);
        var body = File.ReadAllText(LogPath(r));
        StringAssert.Contains("insideBodyCm=2.0cm", body);
        StringAssert.Contains("| `Skirt_1` | 2 | — |", body);       // no measurable collider → dash, not 0.0cm
    }

    [Test]
    public void PlayModeSuffixIsStrippedFromKeys()
    {
        Assert.AreEqual("Skirt_1.L", ReportClearance.Key("Skirt_1.L$1234"));
        Assert.AreEqual("Skirt_1.L", ReportClearance.Key("Skirt_1.L"));
    }

    [Test]
    public void SegmentDistanceClampsToEndpoints()
    {
        var a = Vector3.zero; var b = Vector3.up;
        Assert.AreEqual(0.5f, ReportClearance.SegmentDistance(new Vector3(0.5f, 0.5f, 0f), a, b), 1e-5f);
        Assert.AreEqual(1f, ReportClearance.SegmentDistance(new Vector3(0f, 2f, 0f), a, b), 1e-5f);
    }
}
