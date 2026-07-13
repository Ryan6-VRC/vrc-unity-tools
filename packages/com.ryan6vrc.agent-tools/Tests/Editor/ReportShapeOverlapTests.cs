using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// ReportShapeOverlap proof obligations. The pure core (Analyze) reads blendshape deltas off an in-memory
// Mesh, so most tests build a Mesh with AddBlendShapeFrame and known per-vertex deltas and assert the
// touched-set footprints + containment directly — no scene, no fakes, the real GetBlendShapeFrameVertices
// path. Report door tests (scene resolve + summary + log trailer + FAIL branches) run against a throwaway
// Single scene (mirrors CheckSeamTests): fixtures live in it, it is never saved.
[Category("ReportShapeOverlap")]
public class ReportShapeOverlapTests
{
    private GameObject _root;
    private string _logPath;
    private readonly List<Mesh> _meshes = new List<Mesh>();

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // FAIL paths log at Error — expected in negative tests
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _root = new GameObject("ShapeOverlapTestRoot");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
        _root = null;
        foreach (var m in _meshes) if (m != null) UnityEngine.Object.DestroyImmediate(m);
        _meshes.Clear();
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Fixture builders ────────────────────────────────────────────────────────────────────────────

    private Mesh MakeMesh(int vertexCount)
    {
        var m = new Mesh { vertices = new Vector3[vertexCount] };
        _meshes.Add(m);
        return m;
    }

    // Add a blendshape whose delta at each listed (idx, magnitudeMeters) is (mag,0,0); every other vertex 0.
    private static void AddShape(Mesh m, string name, params (int idx, float mag)[] touched)
    {
        var d = new Vector3[m.vertexCount];
        foreach (var (idx, mag) in touched) d[idx] = new Vector3(mag, 0f, 0f);
        m.AddBlendShapeFrame(name, 100f, d, null, null);
    }

    // Shorthand: shape touching a contiguous [lo, hi] vertex span at a uniform magnitude.
    private static void AddSpan(Mesh m, string name, int lo, int hi, float mag)
    {
        var t = new List<(int, float)>();
        for (int i = lo; i <= hi; i++) t.Add((i, mag));
        AddShape(m, name, t.ToArray());
    }

    private static System.Text.RegularExpressions.Regex FailRe =>
        new System.Text.RegularExpressions.Regex(@"\[ReportShapeOverlap\] FAIL:");

    private static ReportShapeOverlap.Footprint Foot(ReportShapeOverlap.Analysis a, string name) =>
        a.Footprints.First(f => f.Name == name);

    private static ReportShapeOverlap.Pair PairOf(ReportShapeOverlap.Analysis a, string x, string y) =>
        a.Pairs.First(p => (p.A == x && p.B == y) || (p.A == y && p.B == x));

    private static string Path(GameObject go)
    {
        var t = go.transform;
        var sb = new System.Text.StringBuilder(t.name);
        while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
        return sb.ToString();
    }

    private string ReadLog(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, StringComparison.Ordinal);
        _logPath = i < 0 ? null : result.Substring(i + marker.Length).Trim();
        return _logPath != null && File.Exists(_logPath) ? File.ReadAllText(_logPath) : "";
    }

    // ── Core: containment metric ──────────────────────────────────────────────────────────────────────

    // B's span sits entirely inside A's ⇒ the smaller (B) is fully swallowed ⇒ containment 1.0 (the
    // double-subtraction condition: stocking contains the driven leg shrink).
    [Test]
    public void Containment_fullyContained_isOne()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        AddSpan(m, "B", 5, 9, 0.05f);
        var a = ReportShapeOverlap.Analyze(m, new[] { "A", "B" });

        Assert.AreEqual(10, Foot(a, "A").Touched);
        Assert.AreEqual(5, Foot(a, "B").Touched);
        var p = PairOf(a, "A", "B");
        Assert.AreEqual(5, p.Intersect);
        Assert.AreEqual(5, p.MinFootprint);
        Assert.AreEqual(1.0f, p.Containment, 1e-4f);
    }

    [Test]
    public void Containment_disjoint_isZero()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 4, 0.05f);
        AddSpan(m, "B", 5, 9, 0.05f);
        var a = ReportShapeOverlap.Analyze(m, new[] { "A", "B" });
        Assert.AreEqual(0, PairOf(a, "A", "B").Intersect);
        Assert.AreEqual(0f, PairOf(a, "A", "B").Containment, 1e-4f);
    }

    // Partial overlap below the flag threshold: |A∩B|=2 over min(10,7)=7 ⇒ 0.286 < 0.30 ⇒ not flagged.
    [Test]
    public void Containment_partial_belowFlag()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        AddSpan(m, "B", 8, 14, 0.05f);
        var a = ReportShapeOverlap.Analyze(m, new[] { "A", "B" });
        var p = PairOf(a, "A", "B");
        Assert.AreEqual(2, p.Intersect);
        Assert.AreEqual(7, p.MinFootprint);
        Assert.Less(p.Containment, ReportShapeOverlap.FlagContainment);
        Assert.AreEqual(2f / 7f, p.Containment, 1e-4f);
    }

    // ── Core: the two-tier noise floor ─────────────────────────────────────────────────────────────────

    // The RELATIVE tier earns its place: 10 main verts at 50mm set p95≈50mm ⇒ thr=10%·p95=5mm, so 5 stray
    // verts at 1mm (well ABOVE the 0.1mm absolute floor) are pruned. absFloor alone would keep them
    // (footprint 15); the relative tier drops them to 10. This is the stray-authoring-vertex case.
    [Test]
    public void RelativeFloor_prunesStrayVerts()
    {
        var m = MakeMesh(20);
        var t = new List<(int, float)>();
        for (int i = 0; i < 10; i++) t.Add((i, 0.05f));   // main deformation, 50mm
        for (int i = 10; i < 15; i++) t.Add((i, 0.001f)); // stray, 1mm — above absFloor, below rel tier
        AddShape(m, "S", t.ToArray());
        var a = ReportShapeOverlap.Analyze(m, new[] { "S" });
        var f = Foot(a, "S");
        Assert.AreEqual(10, f.Touched, "relative tier must prune the 1mm stray verts (absFloor alone keeps 15)");
        Assert.Greater(f.Threshold, ReportShapeOverlap.AbsFloorMeters, "thr is driven by the relative tier here");
    }

    // The ABSOLUTE tier is the floor when the shape is uniformly tiny: p95≈0.2mm ⇒ 10%·p95=0.02mm < 0.1mm,
    // so thr clamps to absFloor and the 0.05mm sub-visual verts are pruned while the 0.2mm ones survive.
    [Test]
    public void AbsoluteFloor_prunesSubVisual()
    {
        var m = MakeMesh(20);
        var t = new List<(int, float)>();
        for (int i = 0; i < 5; i++) t.Add((i, 0.0002f));   // 0.2mm — above absFloor
        for (int i = 5; i < 10; i++) t.Add((i, 0.00005f)); // 0.05mm — below absFloor
        AddShape(m, "S", t.ToArray());
        var a = ReportShapeOverlap.Analyze(m, new[] { "S" });
        var f = Foot(a, "S");
        Assert.AreEqual(5, f.Touched, "absFloor prunes the 0.05mm verts");
        Assert.AreEqual(ReportShapeOverlap.AbsFloorMeters, f.Threshold, 1e-6f, "thr clamps to absFloor when p95 tiny");
    }

    // ── Core: robustness ───────────────────────────────────────────────────────────────────────────────

    // A missing shape name is recorded, not fatal; the resolvable shapes still pair.
    [Test]
    public void MissingShape_recorded_notFatal()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        AddSpan(m, "B", 5, 9, 0.05f);
        var a = ReportShapeOverlap.Analyze(m, new[] { "A", "Nope", "B" });
        CollectionAssert.AreEqual(new[] { "Nope" }, a.Missing);
        Assert.IsTrue(Foot(a, "Nope").Missing);
        Assert.AreEqual(1, a.Pairs.Count, "only the two resolvable shapes pair (A×B), never the missing one");
        Assert.AreEqual(1.0f, PairOf(a, "A", "B").Containment, 1e-4f);
    }

    // An all-zero shape touches nothing ⇒ footprint 0 ⇒ containment guards the divide-by-zero (0, no throw).
    [Test]
    public void EmptyShape_noDivideByZero()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        AddShape(m, "Z"); // no touched verts ⇒ all-zero deltas
        var a = ReportShapeOverlap.Analyze(m, new[] { "A", "Z" });
        Assert.AreEqual(0, Foot(a, "Z").Touched);
        var p = PairOf(a, "A", "Z");
        Assert.AreEqual(0, p.MinFootprint);
        Assert.AreEqual(0f, p.Containment, 1e-4f);
    }

    // ── Door: end-to-end Report ────────────────────────────────────────────────────────────────────────

    private GameObject NewSkinnedObject(string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        return go;
    }

    [Test]
    public void Report_endToEnd_summaryAndLog()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        AddSpan(m, "B", 5, 9, 0.05f); // fully contained ⇒ flagged
        var go = NewSkinnedObject("Body", m);

        var r = ReportShapeOverlap.Report(Path(go), new[] { "A", "B" });
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("| log=", r);
        StringAssert.Contains("shapes=2/2", r);
        StringAssert.Contains("flagged=1", r);
        StringAssert.Contains("missing=0", r);

        var body = ReadLog(r);
        StringAssert.Contains("## Footprints", body);
        StringAssert.Contains("## Pairwise containment", body);
        StringAssert.Contains("`A`", body);
        StringAssert.Contains("1.00 *", body); // the contained pair, flagged in the table
    }

    [Test]
    public void Report_missingShape_reportedNotFailed()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        AddSpan(m, "B", 5, 9, 0.05f);
        var go = NewSkinnedObject("Body", m);
        var r = ReportShapeOverlap.Report(Path(go), new[] { "A", "B", "Ghost" });
        StringAssert.Contains("=> OK", r);      // missing name is not a failure
        StringAssert.Contains("shapes=2/3", r);
        StringAssert.Contains("missing=1", r);
        var body = ReadLog(r);
        StringAssert.Contains("**MISSING**", body);
    }

    // ── Door: FAIL branches (bad input names the fix; bare FAIL carries no log trailer) ─────────────────

    [Test]
    public void Report_objectNotFound_bareFail()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var r = ReportShapeOverlap.Report("no-such-object", new[] { "A" });
        StringAssert.StartsWith("[ReportShapeOverlap] FAIL:", r);
        StringAssert.Contains("not found", r);
        Assert.IsFalse(r.Contains("| log="), "a bare FAIL never points at an artifact");
    }

    [Test]
    public void Report_noBlendshapeMesh_fails()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var go = new GameObject("Bare");
        go.transform.SetParent(_root.transform, false); // no SkinnedMeshRenderer at all
        var r = ReportShapeOverlap.Report(Path(go), new[] { "A" });
        StringAssert.StartsWith("[ReportShapeOverlap] FAIL:", r);
        StringAssert.Contains("no SkinnedMeshRenderer with blendshapes", r);
    }

    [Test]
    public void Report_emptyShapeNames_fails()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        var go = NewSkinnedObject("Body", m);
        var r = ReportShapeOverlap.Report(Path(go), new string[0]);
        StringAssert.StartsWith("[ReportShapeOverlap] FAIL:", r);
        StringAssert.Contains("candidate co-active set", r);
    }

    // A null (or blank) shape-name element is malformed input, not a MISSING shape: reject it in the FAIL
    // envelope rather than let GetBlendShapeIndex(null) throw a raw exception out of Report().
    [Test]
    public void Report_nullShapeName_fails()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        var go = NewSkinnedObject("Body", m);
        var r = ReportShapeOverlap.Report(Path(go), new[] { "A", null });
        StringAssert.StartsWith("[ReportShapeOverlap] FAIL:", r);
        StringAssert.Contains("non-empty", r);
        Assert.IsFalse(r.Contains("| log="), "malformed input is a bare FAIL, not a written report");
    }

    // Two child renderers both carrying blendshapes ⇒ ambiguous ⇒ FAIL naming them (point at one).
    [Test]
    public void Report_ambiguousMesh_fails()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var m1 = MakeMesh(20); AddSpan(m1, "A", 0, 9, 0.05f);
        var m2 = MakeMesh(20); AddSpan(m2, "B", 0, 9, 0.05f);
        var parent = new GameObject("Avatar");
        parent.transform.SetParent(_root.transform, false);
        NewChildSkinned(parent, "MeshA", m1);
        NewChildSkinned(parent, "MeshB", m2);
        var r = ReportShapeOverlap.Report(Path(parent), new[] { "A" });
        StringAssert.StartsWith("[ReportShapeOverlap] FAIL:", r);
        StringAssert.Contains("ambiguous", r);
        StringAssert.Contains("MeshA", r);
        StringAssert.Contains("MeshB", r);
    }

    private void NewChildSkinned(GameObject parent, string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
    }
}
