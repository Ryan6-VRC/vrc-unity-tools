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

// The pure core (Analyze) reads blendshape deltas off an in-memory Mesh, so its tests need no scene. Door tests
// run in a throwaway Single scene that is never saved.
[Category("ReportShapeOverlap")]
public class ReportShapeOverlapTests
{
    private GameObject _root;
    private readonly List<Mesh> _meshes = new List<Mesh>();

    // A Report writes a Snapshot markdown. Record the path each call named in its `| log=` trailer and delete
    // exactly those; a glob over `shape-overlap_*.md` would also sweep snapshots the operator took by hand.
    private static readonly List<string> Artifacts = new List<string>();

    [OneTimeTearDown]
    public void DeleteWrittenSnapshots()
    {
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), new List<string>());
        Artifacts.Clear();
    }

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
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Fixture builders ────────────────────────────────────────────────────────────────────────────

    private Mesh MakeMesh(int vertexCount)
    {
        var m = new Mesh { vertices = new Vector3[vertexCount] };
        _meshes.Add(m);
        return m;
    }

    // Magnitudes are meters.
    private static void AddShape(Mesh m, string name, params (int idx, float mag)[] touched)
    {
        var d = new Vector3[m.vertexCount];
        foreach (var (idx, mag) in touched) d[idx] = new Vector3(mag, 0f, 0f);
        m.AddBlendShapeFrame(name, 100f, d, null, null);
    }

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

    private const string LogMarker = "| log=";

    // The door, recording the Snapshot path it named for teardown. Every Report call in this fixture goes through
    // here, because a call that asserts only on the returned summary wrote its artifact too.
    private static string Report(string meshObject, string[] shapeNames = null, string outfitRoot = null)
    {
        string summary = ReportShapeOverlap.Run(meshObject, shapeNames, outfitRoot);
        int i = summary.IndexOf(LogMarker, StringComparison.Ordinal);
        if (i >= 0) Artifacts.Add(summary.Substring(i + LogMarker.Length).Trim());
        return summary;
    }

    private static string ReadLog(string result)
    {
        int i = result.IndexOf(LogMarker, StringComparison.Ordinal);
        if (i < 0) return "";
        string path = result.Substring(i + LogMarker.Length).Trim();
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    // ── Core: containment metric ──────────────────────────────────────────────────────────────────────

    // The double-subtraction condition: a stocking shape contains the driven leg shrink.
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

        var r = Report(Path(go), new[] { "A", "B" });
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
        var r = Report(Path(go), new[] { "A", "B", "Ghost" });
        StringAssert.Contains("=> OK", r);      // missing name is not a failure
        StringAssert.Contains("shapes=2/3", r);
        StringAssert.Contains("missing=1", r);
        var body = ReadLog(r);
        StringAssert.Contains("**MISSING**", body);
    }

    // ── Door: FAIL branches (bad input names the fix; bare FAIL carries no log trailer) ─────────────────

    [Test]
    public void Report_noBlendshapeMesh_fails()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var go = new GameObject("Bare");
        go.transform.SetParent(_root.transform, false); // no SkinnedMeshRenderer at all
        var r = Report(Path(go), new[] { "A" });
        StringAssert.StartsWith("[ReportShapeOverlap] FAIL:", r);
        StringAssert.Contains("no SkinnedMeshRenderer with blendshapes", r);
    }

    // All three sources are optional and the tool assembles the set itself, so "nothing is on together here" is
    // what the caller asked, and it still writes its artifact and names it in the trailer.
    [Test]
    public void Report_emptyCoActiveSet_isAnAnswerNotARefusal()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        var go = NewSkinnedObject("Body", m);   // shape exists, worn at 0, no outfitRoot passed
        var r = Report(Path(go), new string[0]);
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("shapes=0/0", r);
        StringAssert.Contains("log=", r);       // the artifact a bare FAIL never wrote
        // The zero is only readable if the run says whether the reaction source was consulted at all.
        StringAssert.Contains("NOT scanned — no outfitRoot passed", r);
        StringAssert.DoesNotContain("meshCutters=", r, "the cutter scan never ran, so no verdict on it may print");
    }

    // A cutter anti-clips by vertex FILTER, so this outfit is anti-clipped with no ShapeChanger row anywhere and
    // `reacted=0` alone would read as "nothing anti-clips".
    [Test]
    public void Report_meshCutterUnderOutfitRoot_saysPresentBesideReacted()
    {
        var avatar = NewAvatarRoot("Av");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var cutter = new GameObject("Cutter");                  // under the root; no ShapeChanger anywhere
        cutter.transform.SetParent(avatar.transform, false);
        cutter.AddComponent<ModularAvatarMeshCutter>();

        var r = Report(Path(body), new string[0], Path(avatar));
        StringAssert.Contains("reacted=0", r);
        StringAssert.Contains("meshCutters=present", r);
        // Adjacent to reacted=, because the token's only job is to qualify that number.
        StringAssert.IsMatch(@"reacted=0 meshCutters=present", r);
    }

    // `absent` says the scan ran, unlike the no-outfitRoot case above, which prints no token. It means "none under
    // this root", never "nothing cuts".
    [Test]
    public void Report_noMeshCutter_saysAbsentSoTheScanIsLegible()
    {
        var avatar = NewAvatarRoot("Av");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);

        var r = Report(Path(body), new string[0], Path(avatar));
        StringAssert.Contains("meshCutters=absent", r);
        StringAssert.DoesNotContain("WITHHELD", r);
        StringAssert.DoesNotContain("NOT scanned", r, "outfitRoot was passed — the scan ran");
    }

    // No scene fixture can reach this branch, because MA always resolves in a live Editor.
    [Test]
    public void DecideCutterState_typeDriftWithMaInstalled_isDriftUnknownNeverAbsent()
    {
        Assert.AreEqual(ReportShapeOverlap.CutterState.DriftUnknown,
            ReportShapeOverlap.DecideCutterState(null, maInstalled: true, anyCutterFound: false),
            "MA present with the type unresolved is drift; rendering it as a clean `absent` is the exact " +
            "false all-clear this signal exists to prevent");
    }

    [Test]
    public void DecideCutterState_maAbsent_isAbsentNotWithheld()
    {
        Assert.AreEqual(ReportShapeOverlap.CutterState.Absent,
            ReportShapeOverlap.DecideCutterState(null, maInstalled: false, anyCutterFound: false));
    }

    [Test]
    public void MeshCutterPart_rendersEachStateExactly()
    {
        Assert.AreEqual(" meshCutters=present", ReportShapeOverlap.MeshCutterPart(ReportShapeOverlap.CutterState.Present));
        Assert.AreEqual(" meshCutters=absent", ReportShapeOverlap.MeshCutterPart(ReportShapeOverlap.CutterState.Absent));
        // Both non-answers print NO token — an unanswered question must never render as a value. DriftUnknown
        // is carried by its WITHHELD note instead (WithheldNotes_..., below).
        Assert.AreEqual("", ReportShapeOverlap.MeshCutterPart(ReportShapeOverlap.CutterState.DriftUnknown));
        Assert.AreEqual("", ReportShapeOverlap.MeshCutterPart(ReportShapeOverlap.CutterState.NotScanned));
    }

    // Both MA drift paths withhold on the SUMMARY, not only to the console: a drifted ShapeChanger type otherwise
    // yields `reacted=0`, indistinguishable from a clean scan. Neither is reachable from a scene fixture, so this
    // asserts the rendering, and asserts them TOGETHER because the two types share a namespace: the realistic
    // rename sets both.
    [Test]
    public void WithheldNotes_spellsEveryDriftTheSameWay()
    {
        var clean = new ReportShapeOverlap.Ingested();
        Assert.AreEqual("", ReportShapeOverlap.WithheldNotes(clean), "a clean run withholds nothing");

        var both = new ReportShapeOverlap.Ingested
        {
            ReactionsUnknown = true,
            MeshCutters = ReportShapeOverlap.CutterState.DriftUnknown,
        };
        var notes = ReportShapeOverlap.WithheldNotes(both);
        StringAssert.Contains("| note: reactions signal WITHHELD (MA ShapeChanger drift)", notes);
        StringAssert.Contains("| note: meshCutters signal WITHHELD (MA MeshCutter type drift)", notes);

        var actives = new ReportShapeOverlap.Ingested { ActiveStateUnknown = true };
        StringAssert.Contains("signal WITHHELD", ReportShapeOverlap.WithheldNotes(actives),
            "the active-state-unknown signal must use the shared WITHHELD spelling");
    }

    // The note is gated on the SCAN, not on an empty result: with a caller-supplied set and no outfitRoot,
    // `reacted=0` would otherwise be indistinguishable from "scanned, and nothing reacts".
    [Test]
    public void Report_shapesPassedWithoutOutfitRoot_stillNamesTheUnscannedSource()
    {
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        AddSpan(m, "B", 5, 14, 0.05f);
        var go = NewSkinnedObject("Body", m);
        var r = Report(Path(go), new[] { "A", "B" });
        StringAssert.Contains("shapes=2/2", r);
        StringAssert.Contains("reacted=0", r);
        StringAssert.Contains("NOT scanned — no outfitRoot passed", r);
    }

    [Test]
    public void Report_nullShapeName_fails()
    {
        LogAssert.Expect(LogType.Error, FailRe);
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 9, 0.05f);
        var go = NewSkinnedObject("Body", m);
        var r = Report(Path(go), new[] { "A", null });
        StringAssert.StartsWith("[ReportShapeOverlap] FAIL:", r);
        StringAssert.Contains("non-empty", r);
        Assert.IsFalse(r.Contains("| log="), "malformed input is a bare FAIL, not a written report");
    }

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
        var r = Report(Path(parent), new[] { "A" });
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

    // ── ShapeChanger reaction ingestion ────────────────────────────────────────────────────────────────
    // AvatarObjectReference.Get resolves relative to an avatar root, so fixtures hang off a VRCAvatarDescriptor
    // (registered as an avatar-root type by NDMF's VRChat platform at editor load).

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
            objRef.Set(target);
            list.Add(new ChangedShape { Object = objRef, ShapeName = shape, ChangeType = type, Value = 100f });
        }
        sc.Shapes = list;
        return sc;
    }

    // ── Build-effective active state ──────────────────────────────────────────────────────────────────
    // MA conditions every reaction on its ancestors' activeSelf, so a declaration behind a PARKED ancestor may
    // not apply at the initial state. Whether that ancestor is toggle-driven decides it, and a wrong call in
    // either direction misleads the reader, so both directions are asserted.

    private GameObject Interpose(GameObject child, string name)
    {
        var mid = new GameObject(name);
        mid.transform.SetParent(child.transform.parent, false);
        child.transform.SetParent(mid.transform, false);
        return mid;
    }

    private ModularAvatarObjectToggle AddObjectToggle(GameObject avatar, string name, GameObject target, bool active)
    {
        var host = new GameObject(name);
        host.transform.SetParent(avatar.transform, false);
        var ot = host.AddComponent<ModularAvatarObjectToggle>();
        var objRef = new AvatarObjectReference();
        objRef.Set(target);
        ot.Objects = new List<ToggledObject> { new ToggledObject { Object = objRef, Active = active } };
        return ot;
    }

    // Proving the declaration dead needs MA's build-time animation index (any clip binding the ancestor's
    // m_IsActive keeps the reaction live), which no edit-time read has, so flagging would invent offenders on
    // every FX-toggled wardrobe.
    [Test]
    public void Report_declaredOnlyBehindParkedAncestor_annotatedNotFlagged()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var sc = AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        var parked = Interpose(sc.gameObject, "ParkedGroup");
        parked.SetActive(false);
        body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(m.GetBlendShapeIndex("Shrink_Hip"), 100f);

        var summary = Report(Path(body), new string[0], Path(avatar));
        var row = ResolutionRow(ReadLog(summary), "Shrink_Hip");
        StringAssert.Contains("ParkedGroup", row);          // the ancestor is the repair handle: name it
        StringAssert.Contains("inactiveDeclared=1", summary); // and count it, so the reader sees the population
        Assert.IsFalse(row.Contains("MISMATCH"),
            "a parked declaration is annotated for the reader to rule on, never promoted to a verdict: " + row);
    }

    // An outfit switched off in the scene but driven by an ObjectToggle is a LIVE conditional, not a dead branch
    // (MA exempts toggle targets from constant-folding).
    [Test]
    public void Report_parkedAncestorDrivenByObjectToggle_stillDeclares()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var sc = AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        var parked = Interpose(sc.gameObject, "ToggledGroup");
        parked.SetActive(false);
        AddObjectToggle(avatar, "Toggle", parked, true);
        body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(m.GetBlendShapeIndex("Shrink_Hip"), 100f);

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        var row = ResolutionRow(md, "Shrink_Hip");
        Assert.IsFalse(row.Contains("MISMATCH"),
            "a toggle-driven inactive ancestor is a live conditional — flagging it invents an offender: " + row);
        Assert.IsFalse(row.Contains("inactive:"), "a toggle-driven ancestor is not parked: " + row);
    }

    // MA's affected-object skip is guarded on `affectedObject != null`, and FindShapes calls ObjectRule with the
    // 3-arg form, so ShapeChangers pass null and EVERY ancestor gets a condition, including one that is also an
    // ancestor of the body. Mirroring MeshCutter's skip here would wrongly read this ancestor as not parked.
    [Test]
    public void Report_parkedAncestorAlsoAncestorOfBody_stillCountsAsParked()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var shared = new GameObject("SharedGroup");
        shared.transform.SetParent(avatar.transform, false);
        var body = NewChildBody(shared, "Body", m);
        var sc = AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        sc.transform.SetParent(shared.transform, false); // changer and body share the parked ancestor
        shared.SetActive(false);
        body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(m.GetBlendShapeIndex("Shrink_Hip"), 100f);

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        var row = ResolutionRow(md, "Shrink_Hip");
        StringAssert.Contains("SharedGroup", row);
        Assert.IsFalse(row.Contains("MISMATCH"), "annotated, not flagged: " + row);
    }

    // The caller's own root is not authoring state: a caller who staged the piece off would otherwise read every
    // declaration as parked.
    [Test]
    public void Report_outfitRootItselfInactive_isANoteNotAWaveOfMismatches()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(m.GetBlendShapeIndex("Shrink_Hip"), 100f);
        avatar.SetActive(false);

        var summary = Report(Path(body), new string[0], Path(avatar));
        StringAssert.Contains("outfitRoot itself is inactive", summary);
        var row = ResolutionRow(ReadLog(summary), "Shrink_Hip");
        Assert.IsFalse(row.Contains("inactive:"), "the caller's own root must not be charged to a row: " + row);
    }

    // MA scans toggles from the AVATAR root, and the ordinary wardrobe layout parks the ObjectToggle on a menu
    // object ABOVE the outfit.
    [Test]
    public void Report_toggleAboveTheOutfitRoot_stillCountsAsDriven()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var outfit = new GameObject("Outfit"); outfit.transform.SetParent(avatar.transform, false);
        var sc = AddShapeChanger(avatar, "Changer", body, ("Shrink_Hip", ShapeChangeType.Set));
        sc.transform.SetParent(outfit.transform, false);
        var parked = Interpose(sc.gameObject, "ToggledGroup");
        parked.SetActive(false);
        AddObjectToggle(avatar, "MenuToggle", parked, true); // sits above `outfit`, the root we pass below

        var md = ReadLog(Report(Path(body), new string[0], Path(outfit)));
        Assert.IsFalse(ResolutionRow(md, "Shrink_Hip").Contains("inactive:"),
            "a toggle above the caller's root still drives the ancestor: " + ResolutionRow(md, "Shrink_Hip"));
    }

    // `Inverted` is how "apply this while the piece is OFF" is authored, and ReactionRule.InitiallyActive XORs it.
    [Test]
    public void Report_invertedChangerBehindParkedAncestor_isLiveNotParked()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var sc = AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        sc.Inverted = true;
        Interpose(sc.gameObject, "ParkedGroup").SetActive(false);

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        Assert.IsFalse(ResolutionRow(md, "Shrink_Hip").Contains("inactive:"),
            "an inverted declaration behind a parked ancestor is initially active: " + ResolutionRow(md, "Shrink_Hip"));
    }

    // MA's initial state is the last INITIALLY-ACTIVE group, not the last declared.
    [Test]
    public void Report_oneLiveAndOneParkedDeclaration_readsAsLive()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChanger(avatar, "LiveOutfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        var parkedSc = AddShapeChanger(avatar, "ParkedOutfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        Interpose(parkedSc.gameObject, "ParkedGroup").SetActive(false);

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        Assert.IsFalse(ResolutionRow(md, "Shrink_Hip").Contains("inactive:"),
            "a live declaration owns the shape regardless of enumeration order: " + ResolutionRow(md, "Shrink_Hip"));
    }

    // A toggle row driving the ancestor OFF resolves to inactive, so toggle MEMBERSHIP alone does not make it driven.
    [Test]
    public void Report_toggleDrivingTheAncestorOff_isNotTreatedAsDriven()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var sc = AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        var parked = Interpose(sc.gameObject, "ParkedGroup");
        parked.SetActive(false);
        AddObjectToggle(avatar, "Toggle", parked, false); // drives it OFF

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        StringAssert.Contains("inactive:", ResolutionRow(md, "Shrink_Hip"));
    }

    [Test]
    public void Report_liveChain_declarationStillSuppresses()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var sc = AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        Interpose(sc.gameObject, "LiveGroup"); // present but active
        body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(m.GetBlendShapeIndex("Shrink_Hip"), 100f);

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        var row = ResolutionRow(md, "Shrink_Hip");
        Assert.IsFalse(row.Contains("MISMATCH"), "a live declaration still owns its shape: " + row);
    }

    [Test]
    public void Report_componentDisabledOnLiveChain_stillDeclares()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var sc = AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));
        sc.enabled = false;
        body.GetComponent<SkinnedMeshRenderer>().SetBlendShapeWeight(m.GetBlendShapeIndex("Shrink_Hip"), 100f);

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        var row = ResolutionRow(md, "Shrink_Hip");
        Assert.IsFalse(row.Contains("MISMATCH"), "MA ignores the component's enabled flag, so this must too: " + row);
    }

    [Test]
    public void Report_shapeChangerSet_ingestedAtZeroWeight()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        AddSpan(m, "Stocking", 5, 14, 0.05f);
        var body = NewChildBody(avatar, "Body", m); // weights default 0
        AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));

        var r = Report(Path(body), new[] { "Stocking" }, Path(avatar));
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("shapes=2/2", r); // Stocking (passed) + Shrink_Hip (reaction, weight 0)
        StringAssert.Contains("`Shrink_Hip`", ReadLog(r));
    }

    // Shrink_Hip also exists on Face's mesh, so a mis-ingest would show. The summary names where the rows went
    // because a census pointed at the wrong mesh otherwise prints the same `reacted=0` as an outfit that reacts to
    // nothing. Face and Torso are not substrings of each other, so the note naming the wrong mesh could not pass.
    [Test]
    public void Report_shapeChangerDifferentMesh_ignoredAndNamed()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Stocking", 5, 14, 0.05f);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Face", m);

        var m2 = MakeMesh(20);
        AddSpan(m2, "Shrink_Hip", 0, 9, 0.05f);
        var other = NewChildBody(avatar, "Torso", m2);

        AddShapeChangerRows(avatar, "Outfit", ("Shrink_Hip", ShapeChangeType.Set, other), ("Shrink_Hip", ShapeChangeType.Set, null));

        var r = Report(Path(body), new[] { "Stocking" }, Path(avatar));
        StringAssert.Contains("shapes=1/1", r); // only the passed Stocking; the Torso reaction is excluded
        StringAssert.Contains("reacted=0", r);
        // The note is the summary's last field and the RunLog trailer follows it directly, so this pins that the
        // Torso path is the WHOLE list: a null target rendered as `—` (PathOf(null)) or the Face path would break it.
        StringAssert.Contains("rows under outfitRoot target " + Path(other) + " | log=", r);
        StringAssert.DoesNotContain("target " + Path(body), r);
    }

    // The sibling note is gated on reacted=0: once any row lands on this mesh the census is on the right mesh, so
    // a sibling row is the ordinary filtered case and nothing prints.
    [Test]
    public void Report_shapeChangerOnThisAndSibling_noSiblingNote()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Face", m);
        var m2 = MakeMesh(20);
        AddSpan(m2, "Shrink_Hip", 0, 9, 0.05f);
        var other = NewChildBody(avatar, "Torso", m2);

        AddShapeChangerRows(avatar, "Outfit", ("Shrink_Hip", ShapeChangeType.Set, body), ("Shrink_Hip", ShapeChangeType.Set, other));

        var r = Report(Path(body), new string[0], Path(avatar));
        StringAssert.Contains("reacted=1", r);
        StringAssert.DoesNotContain("rows under outfitRoot target", r);
    }

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

    private ModularAvatarShapeChanger AddShapeChangerRows(GameObject avatar, string name,
        params (string shape, ShapeChangeType type, GameObject target)[] rows)
    {
        var outfit = new GameObject(name);
        outfit.transform.SetParent(avatar.transform, false);
        var sc = outfit.AddComponent<ModularAvatarShapeChanger>();
        var list = new List<ChangedShape>();
        foreach (var (shape, type, target) in rows)
        {
            var objRef = new AvatarObjectReference();
            if (target != null) objRef.Set(target); // else leave unset ⇒ Get resolves to null
            list.Add(new ChangedShape { Object = objRef, ShapeName = shape, ChangeType = type, Value = 100f });
        }
        sc.Shapes = list;
        return sc;
    }

    [Test]
    public void BuildAnalyzeSet_nullTargetRow_skippedSiblingIngested()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Valid", 0, 9, 0.05f);
        AddSpan(m, "Nulled", 10, 14, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChangerRows(avatar, "Outfit",
            ("Nulled", ShapeChangeType.Set, null),   // unset Object ⇒ resolves to null ⇒ skipped
            ("Valid",  ShapeChangeType.Set, body));  // sibling on the SAME component ⇒ still ingested
        var smr = body.GetComponent<SkinnedMeshRenderer>();

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new string[0], avatar);
        CollectionAssert.Contains(ing.Names, "Valid");
        CollectionAssert.DoesNotContain(ing.Names, "Nulled");
    }

    [Test]
    public void BuildAnalyzeSet_passedAndReaction_dedupedTypeRecorded()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Dual", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChanger(avatar, "Outfit", body, ("Dual", ShapeChangeType.Set));
        var smr = body.GetComponent<SkinnedMeshRenderer>();

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new[] { "Dual" }, avatar);
        Assert.AreEqual(1, ing.Names.Count(n => n == "Dual"), "passed ∪ reaction dedupes to one entry");
        Assert.AreEqual(1, ing.ReactionTypes["Dual"], "reaction ChangeType still recorded on the deduped shape");
    }

    [Test]
    public void BuildAnalyzeSet_multipleShapeChangers_allIngested()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "A", 0, 4, 0.05f);
        AddSpan(m, "B", 5, 9, 0.05f);
        AddSpan(m, "C", 10, 14, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChanger(avatar, "Outfit1", body, ("A", ShapeChangeType.Set), ("B", ShapeChangeType.Delete)); // multi-row
        AddShapeChanger(avatar, "Outfit2", body, ("C", ShapeChangeType.Set));                                // 2nd component
        var smr = body.GetComponent<SkinnedMeshRenderer>();

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new string[0], avatar);
        CollectionAssert.IsSubsetOf(new[] { "A", "B", "C" }, ing.Names);
        Assert.AreEqual(1, ing.ReactionTypes["A"]); // Set
        Assert.AreEqual(0, ing.ReactionTypes["B"]); // Delete
        Assert.AreEqual(1, ing.ReactionTypes["C"]); // Set
    }

    // ── Resolution table + weight audit + disposition + summary ─────────────────────────────────────────

    private ModularAvatarShapeChanger AddShapeChangerValued(GameObject avatar, string name, GameObject target,
        params (string shape, ShapeChangeType type, float value)[] rows)
    {
        var outfit = new GameObject(name);
        outfit.transform.SetParent(avatar.transform, false);
        var sc = outfit.AddComponent<ModularAvatarShapeChanger>();
        var list = new List<ChangedShape>();
        foreach (var (shape, type, value) in rows)
        {
            var objRef = new AvatarObjectReference();
            objRef.Set(target);
            list.Add(new ChangedShape { Object = objRef, ShapeName = shape, ChangeType = type, Value = value });
        }
        sc.Shapes = list;
        return sc;
    }

    private static string ResolutionRow(string body, string shape)
    {
        int start = body.IndexOf("## Resolution", StringComparison.Ordinal);
        if (start < 0) return "";
        int end = body.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
        string section = end < 0 ? body.Substring(start) : body.Substring(start, end - start);
        foreach (var line in section.Split('\n'))
            if (line.Contains("`" + shape + "`")) return line;
        return "";
    }

    // A reaction-declared shape sits at static weight 0 because the reaction owns it at runtime; flagging every
    // correctly-owned row would flood the report.
    [Test]
    public void Report_reactionDeclaredAtZeroWeight_notFlagged()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_Hip", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m); // weight 0
        AddShapeChanger(avatar, "Outfit", body, ("Shrink_Hip", ShapeChangeType.Set));

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        var row = ResolutionRow(md, "Shrink_Hip");
        StringAssert.Contains("Set=", row);                       // it IS reaction-declared
        Assert.IsFalse(row.Contains("MISMATCH"), "reaction-declared row is never flagged, even at static weight 0");
        // no table DATA row anywhere is flagged (the legend line explaining the marker is not a "| `" data row).
        foreach (var line in md.Split('\n'))
            if (line.StartsWith("| `", StringComparison.Ordinal))
                Assert.IsFalse(line.Contains("MISMATCH"), "no data row should be flagged: " + line);
    }

    [Test]
    public void Report_wornButUndeclared_flaggedMismatch()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Shrink_stocking", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var smr = body.GetComponent<SkinnedMeshRenderer>();
        smr.SetBlendShapeWeight(m.GetBlendShapeIndex("Shrink_stocking"), 100f); // worn, undeclared

        var md = ReadLog(Report(Path(body), new string[0], Path(avatar)));
        var row = ResolutionRow(md, "Shrink_stocking");
        StringAssert.Contains("**MISMATCH**", row);
        StringAssert.Contains("none", row); // no reaction owns it
    }

    // A Delete's resolved target is `deleted` whatever its Value field holds; Value=30 here so a leaked value
    // could not read as the token.
    [Test]
    public void Report_deleteReaction_resolvedTargetIsDeleted()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Del_Shape", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChangerValued(avatar, "Outfit", body, ("Del_Shape", ShapeChangeType.Delete, 30f));

        var row = ResolutionRow(ReadLog(Report(Path(body), new string[0], Path(avatar))), "Del_Shape");
        StringAssert.Contains("Delete", row);
        StringAssert.Contains("| deleted |", row); // resolved-target column = deleted for a Delete, never its Value
    }

    [Test]
    public void Report_setReaction_resolvedTargetIsValue()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Set_Shape", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChangerValued(avatar, "Outfit", body, ("Set_Shape", ShapeChangeType.Set, 42f));

        var row = ResolutionRow(ReadLog(Report(Path(body), new string[0], Path(avatar))), "Set_Shape");
        StringAssert.Contains("Set=42", row);
        StringAssert.Contains("| 42 |", row); // resolved-target column = the Set value
    }

    [Test]
    public void Report_multiReactionDifferentValue_conflict()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Clash", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChangerValued(avatar, "Outfit1", body, ("Clash", ShapeChangeType.Set, 50f));
        AddShapeChangerValued(avatar, "Outfit2", body, ("Clash", ShapeChangeType.Set, 80f));
        var smr = body.GetComponent<SkinnedMeshRenderer>();

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new string[0], avatar);
        Assert.IsTrue(ing.Reactions["Clash"].Conflict, "two Set reactions at different values conflict");

        var row = ResolutionRow(ReadLog(Report(Path(body), new string[0], Path(avatar))), "Clash");
        StringAssert.Contains("CONFLICT", row);
        StringAssert.Contains("50", row);
        StringAssert.Contains("80", row); // both values surfaced, not collapsed
    }

    [Test]
    public void Report_weightAudit_listsAllShapes_footprintsStayScoped()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "InUnion", 0, 9, 0.05f);
        AddSpan(m, "Idle1", 10, 12, 0.05f);
        AddSpan(m, "Idle2", 13, 15, 0.05f);
        var body = NewChildBody(avatar, "Body", m);

        var md = ReadLog(Report(Path(body), new[] { "InUnion" }, Path(avatar)));
        StringAssert.Contains("## Weight audit", md);
        StringAssert.Contains("`Idle1`", md);
        StringAssert.Contains("`Idle2`", md);

        int fstart = md.IndexOf("## Footprints", StringComparison.Ordinal);
        string footprintsOnward = md.Substring(fstart); // footprints + pairwise
        StringAssert.DoesNotContain("`Idle1`", footprintsOnward); // audit-only shape is never footprinted
        StringAssert.DoesNotContain("`Idle2`", footprintsOnward);
    }

    [Test]
    public void Report_summary_reactedWornMissingOk_noVerdict()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Worn1", 0, 4, 0.05f);
        AddSpan(m, "React1", 5, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var smr = body.GetComponent<SkinnedMeshRenderer>();
        smr.SetBlendShapeWeight(m.GetBlendShapeIndex("Worn1"), 100f);     // worn, undeclared
        AddShapeChanger(avatar, "Outfit", body, ("React1", ShapeChangeType.Set)); // reacted, weight 0

        var r = Report(Path(body), new string[0], Path(avatar));
        StringAssert.Contains("=> OK", r);
        StringAssert.Contains("reacted=1", r);
        StringAssert.Contains("worn=1", r);
        StringAssert.Contains("missing=0", r);
        Assert.IsFalse(r.Contains("PASS") || r.Contains("=> FAIL"), "a Report emits no verdict token");
    }

    // MA ignores a Delete's Value (the shape is a vertex selector), so conflict detection must compare Delete on
    // type only.
    [Test]
    public void Report_twoDeleteRowsDifferingValues_noConflict()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "DelClash", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChangerValued(avatar, "Outfit1", body, ("DelClash", ShapeChangeType.Delete, 100f));
        AddShapeChangerValued(avatar, "Outfit2", body, ("DelClash", ShapeChangeType.Delete, 30f));
        var smr = body.GetComponent<SkinnedMeshRenderer>();

        var ing = ReportShapeOverlap.BuildAnalyzeSet(smr, new string[0], avatar);
        Assert.IsFalse(ing.Reactions["DelClash"].Conflict, "two Deletes (any stored values) are not a conflict");

        var row = ResolutionRow(ReadLog(Report(Path(body), new string[0], Path(avatar))), "DelClash");
        StringAssert.Contains("Delete", row);
        StringAssert.Contains("| deleted |", row);   // resolved-target deleted, not "conflict"
        StringAssert.DoesNotContain("CONFLICT", row);
    }

    [Test]
    public void Report_summary_mismatchCountsWornUndeclaredOnly()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "WornUndeclared", 0, 4, 0.05f);
        AddSpan(m, "WornDeclared", 5, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var smr = body.GetComponent<SkinnedMeshRenderer>();
        smr.SetBlendShapeWeight(m.GetBlendShapeIndex("WornUndeclared"), 100f); // worn, no reaction
        smr.SetBlendShapeWeight(m.GetBlendShapeIndex("WornDeclared"), 100f);   // worn AND declared
        AddShapeChanger(avatar, "Outfit", body, ("WornDeclared", ShapeChangeType.Set));

        var r = Report(Path(body), new string[0], Path(avatar));
        StringAssert.Contains("worn=2", r);     // both are worn
        StringAssert.Contains("mismatch=1", r); // only the undeclared one is a mismatch
    }

    // A NONZERO weight below "0.###" resolution (a float-noise residual) is classified worn, so rendering it as
    // "0" would contradict the row's own mismatch= count.
    [Test]
    public void Report_tinyResidualWeight_wornButNotRenderedZero()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Residual", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        var smr = body.GetComponent<SkinnedMeshRenderer>();
        smr.SetBlendShapeWeight(m.GetBlendShapeIndex("Residual"), 0.0004f); // worn (strictly != 0), sub-0.###

        var r = Report(Path(body), new string[0], Path(avatar));
        StringAssert.Contains("worn=1", r);
        StringAssert.Contains("mismatch=1", r);
        var row = ResolutionRow(ReadLog(r), "Residual");
        StringAssert.Contains("**MISMATCH**", row);
        StringAssert.Contains("0.0004", row); // real magnitude visible, not collapsed to "0"
    }

    // A wrong hardcoded runtime-assembly name would read "absent" even with MA installed, silently defeating the
    // drift warning.
    [Test]
    public void ModularAvatarInstalled_trueWhenReferenced()
    {
        Assert.IsTrue(VendorReflect.ModularAvatarInstalled(),
            "MA is referenced by the test asmdef; the presence probe's assembly name must match");
    }

    // ── Rendering: markdown-cell encoding + unknown-enum drift + max overlap ────────────────────────────

    // RunLogFormatTests pins the encoder's own rules; this proves only that it is wired into the render path.
    [Test]
    public void Report_shapeNameWithPipe_escapedInRunLog()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "a|b", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);

        var md = ReadLog(Report(Path(body), new[] { "a|b" }, Path(avatar)));
        StringAssert.Contains(@"`a\|b`", md);     // pipe backslash-escaped inside the code cell
        StringAssert.DoesNotContain("`a|b`", md); // never the raw, column-breaking form
    }

    // (ShapeChangeType)2 stands in for a future MA mode or a corrupt serialized enum.
    [Test]
    public void Report_unknownChangeType_renderedUnknownNotSet()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Drifted", 0, 9, 0.05f);
        var body = NewChildBody(avatar, "Body", m);
        AddShapeChanger(avatar, "Outfit", body, ("Drifted", (ShapeChangeType)2));
        LogAssert.Expect(LogType.Warning,
            new System.Text.RegularExpressions.Regex(@"ShapeChangeType drift.*Drifted"));

        var row = ResolutionRow(ReadLog(Report(Path(body), new string[0], Path(avatar))), "Drifted");
        StringAssert.Contains("UNKNOWN(2)", row);
        StringAssert.Contains("| unknown |", row); // resolved-target = unknown, not a fabricated Set value
        Assert.IsFalse(row.Contains("Set="), "an unknown change type must not render as Set");
    }

    [Test]
    public void Report_resolutionOverlapColumn_reportsMaxContainment()
    {
        var avatar = NewAvatarRoot("Avatar");
        var m = MakeMesh(20);
        AddSpan(m, "Big", 0, 14, 0.05f);  // 15 verts
        AddSpan(m, "Small", 0, 9, 0.05f); // 10 verts, fully inside Big ⇒ containment(Big,Small)=10/10=1.00
        AddSpan(m, "Mid", 5, 14, 0.05f);  // 10 verts, overlaps Small on 5..9 ⇒ containment(Small,Mid)=5/10=0.50
        var body = NewChildBody(avatar, "Body", m);

        var small = ResolutionRow(
            ReadLog(Report(Path(body), new[] { "Big", "Small", "Mid" }, Path(avatar))), "Small");
        StringAssert.Contains("1.00", small);     // the MAX of Small's two containments
        StringAssert.DoesNotContain("0.50", small); // not the lesser pair — proves max-selection, not clobber/min
    }
}
