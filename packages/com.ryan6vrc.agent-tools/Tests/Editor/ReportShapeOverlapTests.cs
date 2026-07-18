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
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;

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

    // ── ShapeChanger reaction ingestion (Task 1) ───────────────────────────────────────────────────────
    // AvatarObjectReference.Get resolves relative to an avatar root, so fixtures hang off a VRCAvatarDescriptor
    // (registered as an avatar-root type by NDMF's VRChat platform at editor load). The body SMR and the
    // ShapeChanger-bearing outfit both live under it; the reaction's Object points at the body's GameObject.

    private GameObject NewAvatarRoot(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        go.AddComponent<VRCAvatarDescriptor>();
        return go;
    }

    private GameObject NewChildBody(GameObject parent, string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        go.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        return go;
    }

    private ModularAvatarShapeChanger AddShapeChanger(GameObject avatar, string name, GameObject target,
        params (string shape, ShapeChangeType type)[] rows)
    {
        var outfit = new GameObject(name);
        outfit.transform.SetParent(avatar.transform, false);
        var sc = outfit.AddComponent<ModularAvatarShapeChanger>();
        var list = new List<ChangedShape>();
        foreach (var (shape, type) in rows)
        {
            var objRef = new AvatarObjectReference();
            objRef.Set(target); // resolve relative to the avatar root the outfit hangs under
            list.Add(new ChangedShape { Object = objRef, ShapeName = shape, ChangeType = type, Value = 100f });
        }
        sc.Shapes = list;
        return sc;
    }

    // A Set-mode ShapeChanger writing the body mesh contributes its shape to the analyzed set even though its
    // scene weight is 0 (the caller passes only the worn Stocking; Shrink_Hip is pulled in by the reaction).
    [Test]
    public void Report_shapeChangerSet_ingestedAtZeroWeight()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        AddSpan(m, "Stocking", 5, 14, 0.05f);
        var body = NewChildBody(avatar, "Body", m); // weights default 0
        AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));

        var r = ReportShapeOverlap.Report(Path(body), new[] { "Stocking" }, Path(avatar));
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("shapes=2/2", r); // Stocking (passed) + Shrink_Hip (reaction, weight 0)
        StringAssert.Contains("`Shrink_Hip`", ReadLog(r));
    }

    // A ShapeChanger whose write-target is a DIFFERENT mesh is ignored (the reaction to OtherBody must not be
    // pulled into Body's analyzed set — even though Shrink_Hip exists on Body's mesh, so a mis-ingest would show).
    [Test]
    public void Report_shapeChangerDifferentMesh_ignored()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Stocking", 5, 14, 0.05f);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);

        var m2 = MakeMesh(20);
        AddSpan(m2, "Shrink_Hip", 0, 9, 0.05f);
        var other = NewChildBody(avatar, "OtherBody", m2);

        AddShapeChanger(avatar, "Outfit", other, ("Shrink_Hip", ShapeChangeType.Set));

        var r = ReportShapeOverlap.Report(Path(body), new[] { "Stocking" }, Path(avatar));
        StringAssert.Contains("shapes=1/1", r); // only the passed Stocking; the OtherBody reaction is excluded
    }

    // A Delete-mode row is ingested like any other declared shape (not dropped), and its ChangeType (Delete=0)
    // is captured on the ingestion record for the Task-2 resolution table.
    [Test]
    public void BuildAnalyzeSet_deleteRow_ingestedWithTypeCaptured()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Del_Shape", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChanger(avatar, "Outfit", body, ("Del_Shape", ShapeChangeType.Delete));
        var smr = body.GetComponent<SkinnedMeshRenderer>();

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new string[0], avatar);
        CollectionAssert.Contains(ing.Names, "Del_Shape");
        Assert.AreEqual(0, ing.ReactionTypes["Del_Shape"], "Delete captured as ChangeType 0");
    }

    // A Set row captures ChangeType 1 (the mirror of the Delete case, over the same ingestion path).
    [Test]
    public void BuildAnalyzeSet_setRow_typeCapturedAsOne()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Set_Shape", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChanger(avatar, "Outfit", body, ("Set_Shape", ShapeChangeType.Set));
        var smr = body.GetComponent<SkinnedMeshRenderer>();

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new string[0], avatar);
        Assert.AreEqual(1, ing.ReactionTypes["Set_Shape"], "Set captured as ChangeType 1");
    }

    // The {worn} tier: a shape at nonzero weight on the resolved SMR is ingested off the SMR (not the Mesh),
    // while a zero-weight sibling is not. No outfit root ⇒ the MA path is untouched (MA-absent-safe).
    [Test]
    public void BuildAnalyzeSet_wornShape_ingestedFromNonzeroWeight()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "WornShape", 0, 9, 0.05f);
        AddSpan(m, "Idle", 10, 14, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var smr = body.GetComponent<SkinnedMeshRenderer>();
        smr.SetBlendShapeWeight(m.GetBlendShapeIndex("WornShape"), 100f);

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new string[0], null);
        CollectionAssert.Contains(ing.Names, "WornShape");
        CollectionAssert.DoesNotContain(ing.Names, "Idle");
    }
}
