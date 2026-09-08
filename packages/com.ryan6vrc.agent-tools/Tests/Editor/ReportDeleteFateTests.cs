using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;

// ReportDeleteFate's proof obligations, split the way the door itself is: the CENSUS (what the authoring
// declares, and the two asymmetries a reader gets wrong — MA's cancel rule is about declaration ORDER, and a
// cutter's ancestors shared with its renderer contribute no condition) and the PURE JOINS over the verbatim
// names MA emits (the NaNimated bone key, the ActiveSelfProxy parameter, the mechanism precedence, the
// verdict). Everything past that is one bake away and is proven by running the door on a real composed
// avatar through execute_code, which is where docs/verify.md sends mutation-and-build behaviour; an NUnit
// fixture cannot run an SDK preprocess chain without the modals and the object-registry hazard that file
// owns.
//
// The six mechanisms of the rule are covered here as the census facts each one turns on: an unconditional
// Delete (nothing above it), a menu-conditional one, one under a parked ancestor, one cancelled by a later
// Set, and a cutter both on and off the renderer it cuts.
[Category("ReportDeleteFate")]
public class ReportDeleteFateTests
{
    private GameObject _avatar;
    private readonly List<Mesh> _meshes = new List<Mesh>();

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // the refusal branches log at Error, by contract
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _avatar = new GameObject("DeleteFateAvatar");
        _avatar.AddComponent<VRCAvatarDescriptor>();
    }

    [TearDown]
    public void TearDown()
    {
        if (_avatar != null) Object.DestroyImmediate(_avatar);
        _avatar = null;
        foreach (var m in _meshes) if (m != null) Object.DestroyImmediate(m);
        _meshes.Clear();
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Fixture builders ────────────────────────────────────────────────────────────────────────────

    private GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    /// <summary>A skinned mesh carrying the named blendshapes. MA skips any row whose shape the mesh does
    /// not have, so a fixture without real shapes would census nothing and every assertion below would pass
    /// vacuously.</summary>
    private GameObject Body(GameObject parent, string name, params string[] shapes)
    {
        var go = Child(parent, name);
        var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
        mesh.triangles = new[] { 0, 1, 2 };
        foreach (var s in shapes)
            mesh.AddBlendShapeFrame(s, 100f, new[] { Vector3.up, Vector3.zero, Vector3.zero },
                                    null, null);
        _meshes.Add(mesh);
        go.AddComponent<SkinnedMeshRenderer>().sharedMesh = mesh;
        return go;
    }

    private ModularAvatarShapeChanger Changer(GameObject host, GameObject target,
                                              params (string shape, ShapeChangeType type)[] rows)
    {
        var sc = host.AddComponent<ModularAvatarShapeChanger>();
        var list = new List<ChangedShape>();
        foreach (var (shape, type) in rows)
        {
            var objRef = new AvatarObjectReference();
            objRef.Set(target);
            list.Add(new ChangedShape { Object = objRef, ShapeName = shape, ChangeType = type, Value = 50f });
        }
        sc.Shapes = list;
        return sc;
    }

    private ModularAvatarMeshCutter Cutter(GameObject host, GameObject target, params string[] shapes)
    {
        var cut = host.AddComponent<ModularAvatarMeshCutter>();
        var objRef = new AvatarObjectReference();
        objRef.Set(target);
        cut.Object = objRef;
        host.AddComponent<nadena.dev.modular_avatar.core.vertex_filters.VertexFilterByShapeComponent>()
            .Shapes = shapes.ToList();
        return cut;
    }

    // ── Census ──────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Census_declaresDeleteRowsOnly_andCarriesTheJoinKey()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF", "Nipple_OFF");
        var costume = Child(_avatar, "Costume");
        Changer(costume, body,
            ("Chest_Pasties_OFF", ShapeChangeType.Delete),
            ("Nipple_OFF", ShapeChangeType.Set));

        var census = ReportDeleteFate.TakeCensus(_avatar);

        Assert.IsNull(census.Drift, "MA resolved, so the census must not refuse");
        // A Set declares no removal, so it is not a row here — it is only ever the CANCELLER of one.
        Assert.AreEqual(1, census.Rows.Count, "only the Delete row is a declared removal");
        var row = census.Rows[0];
        Assert.AreEqual("shape", row.Kind);
        Assert.AreEqual("Body_Base", row.RendererName);
        Assert.AreEqual("DeleteFateAvatar/Costume", row.HostPath);
        // The exact string MA spells into the NaNimated bone's name. Asserted verbatim, because a key that is
        // plausible but not identical matches nothing and the door then reports every NaNimation as build-time.
        Assert.AreEqual("Body_Base (UnityEngine.SkinnedMeshRenderer).deletedShape.Chest_Pasties_OFF",
                        row.TargetPropKey);
    }

    [Test]
    public void Census_skipsARowWhoseShapeTheMeshDoesNotHave()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF");
        Changer(Child(_avatar, "Costume"), body, ("NoSuchShape", ShapeChangeType.Delete));

        // MA's FindShapes early-returns on GetBlendShapeIndex < 0, so this row never becomes a rule at all.
        // Censusing it would put a permanently-unmatchable row in the table and force a FAIL on every run.
        Assert.IsEmpty(ReportDeleteFate.TakeCensus(_avatar).Rows);
    }

    [Test]
    public void Census_namesTheNearestMenuItemAncestor()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF");
        var menu = Child(_avatar, "AvatarMenu");
        var pasties = Child(menu, "Pasties");
        pasties.AddComponent<ModularAvatarMenuItem>();
        Changer(pasties, body, ("Chest_Pasties_OFF", ShapeChangeType.Delete));

        var row = ReportDeleteFate.TakeCensus(_avatar).Rows.Single();
        Assert.AreEqual("DeleteFateAvatar/AvatarMenu/Pasties", row.MenuItemAncestor,
            "a menu item at or above the changer is what nothing can fold to a constant");
    }

    [Test]
    public void Census_namesTheNearestParkedAncestor()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF");
        var costume = Child(_avatar, "Costume");
        var bra = Child(costume, "Bra");
        bra.SetActive(false);
        Changer(bra, body, ("Chest_Pasties_OFF", ShapeChangeType.Delete));

        var row = ReportDeleteFate.TakeCensus(_avatar).Rows.Single();
        Assert.AreEqual("DeleteFateAvatar/Costume/Bra", row.ParkedAncestor);
    }

    // MA's cancel rule keys on the LAST declaration in hierarchy order, so the direction is the whole of it:
    // a Set after the Delete voids it, a Set before it does not. Both directions are asserted, because a
    // census that ignored order would report a working hide as cancelled and vice versa.

    [Test]
    public void Census_aLaterSetOnTheSameShapeCancelsTheDelete()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF");
        Changer(Child(_avatar, "A_Costume"), body, ("Chest_Pasties_OFF", ShapeChangeType.Delete));
        Changer(Child(_avatar, "B_Menu"), body, ("Chest_Pasties_OFF", ShapeChangeType.Set));

        var row = ReportDeleteFate.TakeCensus(_avatar).Rows.Single();
        Assert.AreEqual("DeleteFateAvatar/B_Menu", row.LaterSetHost);
    }

    [Test]
    public void Census_anEarlierSetDoesNotCancelTheDelete()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF");
        Changer(Child(_avatar, "A_Menu"), body, ("Chest_Pasties_OFF", ShapeChangeType.Set));
        Changer(Child(_avatar, "B_Costume"), body, ("Chest_Pasties_OFF", ShapeChangeType.Delete));

        Assert.IsNull(ReportDeleteFate.TakeCensus(_avatar).Rows.Single().LaterSetHost);
    }

    [Test]
    public void Census_aSetOnADifferentShapeDoesNotCancel()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF", "Hip_Pasties_OFF");
        Changer(Child(_avatar, "A_Costume"), body, ("Chest_Pasties_OFF", ShapeChangeType.Delete));
        Changer(Child(_avatar, "B_Menu"), body, ("Hip_Pasties_OFF", ShapeChangeType.Set));

        Assert.IsNull(ReportDeleteFate.TakeCensus(_avatar).Rows.Single().LaterSetHost,
            "the keys differ, so nothing is cancelled — MA keys on deletedShape.<name>, not on the renderer");
    }

    // ── The cutter asymmetry ────────────────────────────────────────────────────────────────────────
    // FindMeshCutter passes the cut renderer as the rule's affected object, so BuildConditions skips every
    // ancestor the renderer also sits under. That is the whole reason a cutter reads as more reliable than a
    // ShapeChanger, and getting it backwards would report every unconditional cutter as parked.

    [Test]
    public void Census_aCutterOnItsOwnRendererIgnoresASharedParkedAncestor()
    {
        var wardrobe = Child(_avatar, "Wardrobe");
        wardrobe.SetActive(false);
        var body = Body(wardrobe, "Body_Base", "Chest_Pasties_OFF");
        Cutter(body, body, "Chest_Pasties_OFF");

        var row = ReportDeleteFate.TakeCensus(_avatar).Rows.Single();
        Assert.AreEqual("cutter", row.Kind);
        Assert.IsTrue(row.AffectedObjectSkip);
        Assert.IsNull(row.ParkedAncestor,
            "`Wardrobe` is an ancestor of the cut renderer too, so MA builds no condition from it");
    }

    [Test]
    public void Census_aCutterOffItsRendererStillSeesItsOwnParkedAncestor()
    {
        var body = Body(_avatar, "Body_Base", "Chest_Pasties_OFF");
        var costume = Child(_avatar, "Costume");
        costume.SetActive(false);
        Cutter(Child(costume, "CutHost"), body, "Chest_Pasties_OFF");

        var row = ReportDeleteFate.TakeCensus(_avatar).Rows.Single();
        Assert.AreEqual("DeleteFateAvatar/Costume", row.ParkedAncestor);
        StringAssert.Contains("Chest_Pasties_OFF", row.FilterSummary);
        Assert.AreEqual("VertexFilterByShape", row.FilterTokens.Single(),
            "the leading token of the filter's own ToString() is the only handle for attributing its bone");
    }

    [Test]
    public void Census_aShapeChangerOnTheRendererStillSeesTheSharedParkedAncestor()
    {
        // The mirror of the cutter case, and the asymmetry that makes anti-clip authoring surprising: a
        // ShapeChanger passes a null affected object, so no ancestor is ever skipped.
        var wardrobe = Child(_avatar, "Wardrobe");
        wardrobe.SetActive(false);
        var body = Body(wardrobe, "Body_Base", "Chest_Pasties_OFF");
        Changer(body, body, ("Chest_Pasties_OFF", ShapeChangeType.Delete));

        Assert.AreEqual("DeleteFateAvatar/Wardrobe",
                        ReportDeleteFate.TakeCensus(_avatar).Rows.Single().ParkedAncestor);
    }

    // ── Pure joins over MA's verbatim names ─────────────────────────────────────────────────────────

    [Test]
    public void NaNimatedKey_roundTripsTheNameMaBuilds()
    {
        // Built here the way MA builds it, from the pinned prefix and the pinned renderer suffix, so the test
        // fails if either literal drifts from what the door parses.
        string key = "Body_Base" + ReportDeleteFate.RendererSuffix + ".deletedShape.Chest_Pasties_OFF";
        string goName = ReportDeleteFate.NaNimatedBonePrefix + ReportDeleteFate.Mangle(key);

        Assert.AreEqual(key, ReportDeleteFate.NaNimatedKeyOf(goName));
        Assert.IsNull(ReportDeleteFate.NaNimatedKeyOf("NaNimatedBuffer$1234"),
            "the BUFFER object is not a bone and carries no key");
        Assert.IsNull(ReportDeleteFate.NaNimatedKeyOf("Hips"));
    }

    [Test]
    public void Mangle_appliesToBothSides_becauseItIsNotInvertible()
    {
        // MA replaces '/' with '_' in the object name. Two distinct shapes can collapse onto one name, so the
        // door compares MANGLED forms rather than un-mangling — un-mangling would invent a distinction the
        // build does not have and attribute a bone to the wrong row.
        Assert.AreEqual(ReportDeleteFate.Mangle("R.deletedShape.A/B"),
                        ReportDeleteFate.Mangle("R.deletedShape.A_B"));
        Assert.AreEqual("R.deletedShape.A_B", ReportDeleteFate.Mangle("R.deletedShape.A/B"));
    }

    [Test]
    public void AncestorNamedBy_stripsTheDisambiguator_andRejectsEverythingElse()
    {
        Assert.AreEqual("Costume_Shoes",
            ReportDeleteFate.AncestorNamedBy("__MA/ActiveSelfProxy/Costume_Shoes##3"));
        // The suffix is not optional in MA's format, but a name with none must still parse rather than
        // returning a string ending in '#'.
        Assert.AreEqual("Glove", ReportDeleteFate.AncestorNamedBy("__MA/ActiveSelfProxy/Glove"));
        Assert.IsNull(ReportDeleteFate.AncestorNamedBy("__MA/AutoParam/Pasties$a1bd797f615d"),
            "a menu item's auto parameter names no ancestor");
        Assert.IsNull(ReportDeleteFate.AncestorNamedBy("GestureLeft"));
    }

    [Test]
    public void ClassifyConditional_prefersTheAnimatedAncestorOverAMenuItemAbove()
    {
        var underMenu = new ReportDeleteFate.DeclaredRow { MenuItemAncestor = "Avatar/Menu/Pasties" };
        var plain = new ReportDeleteFate.DeclaredRow();

        // A row can sit under a menu item AND under an animated ancestor. The parameter set is measured off
        // the layer MA actually generated, so it is the stronger evidence and names a repair handle the menu
        // item does not.
        Assert.AreEqual(ReportDeleteFate.MechAncestorAnimated,
            ReportDeleteFate.ClassifyConditional(underMenu, new[] { "__MA/ActiveSelfProxy/Cloth_Tights##1" }));
        Assert.AreEqual(ReportDeleteFate.MechMenuItem,
            ReportDeleteFate.ClassifyConditional(underMenu, new[] { "__MA/AutoParam/Pasties$a1bd797f615d" }));
        Assert.AreEqual(ReportDeleteFate.MechAncestorAnimated,
            ReportDeleteFate.ClassifyConditional(plain, new[] { "__MA/ActiveSelfProxy/Glove##0" }));
        // Nothing explains it. That is a defect in the door — MA generated a layer, so something made the row
        // conditional — and the caller must fail loud rather than print an unexplained NaNimation.
        Assert.IsNull(ReportDeleteFate.ClassifyConditional(plain, new string[0]));
        Assert.IsNull(ReportDeleteFate.ClassifyConditional(plain, new[] { "SomeAuthoredParam" }));
    }

    [Test]
    public void NearestParked_skipsAncestorsSharedWithTheAffectedObject()
    {
        var outer = Child(_avatar, "Outer");
        outer.SetActive(false);
        var inner = Child(outer, "Inner");
        var host = Child(inner, "Host");

        Assert.AreEqual("DeleteFateAvatar/Outer",
            ReportDeleteFate.NearestParked(host.transform, _avatar.transform, null));
        Assert.IsNull(ReportDeleteFate.NearestParked(host.transform, _avatar.transform, host.transform),
            "every ancestor is shared with the affected object, so none contributes a condition");
    }

    // ── Verdict grammar ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Verdict_isCheckAvatarsGrammar()
    {
        var clean = new[] { ReportDeleteFate.FateBuildTime, ReportDeleteFate.FateDropped };
        Assert.AreEqual("PASS", ReportDeleteFate.VerdictOf(clean, 60000, 70000));
        // Over budget with every row clean is still a finding: the triangles are what the operator rules on.
        Assert.AreEqual("CLASSIFY", ReportDeleteFate.VerdictOf(clean, 76788, 70000));
        Assert.AreEqual("CLASSIFY", ReportDeleteFate.VerdictOf(
            new[] { ReportDeleteFate.FateBuildTime, ReportDeleteFate.FateNaNimation }, 100, 70000));
        Assert.AreEqual("CLASSIFY", ReportDeleteFate.VerdictOf(
            new[] { ReportDeleteFate.FateCancelled }, 100, 70000));
        // FAIL is the door confessing, never a fact about the avatar — so it outranks every finding.
        Assert.AreEqual("FAIL", ReportDeleteFate.VerdictOf(
            new[] { ReportDeleteFate.FateNaNimation, ReportDeleteFate.FateUnresolved }, 999999, 70000));
        Assert.AreEqual("PASS", ReportDeleteFate.VerdictOf(new string[0], 0, 70000),
            "an avatar declaring no removal at all is a clean answer, not a refusal");
    }

    // ── Door refusals: bad input is a bare FAIL with no trailer ─────────────────────────────────────

    [Test]
    public void Run_refusesBadInput_withNoLogTrailer()
    {
        foreach (var (call, why) in new[]
                 {
                     (ReportDeleteFate.Run("NoSuchRoot"), "root not found"),
                     (ReportDeleteFate.Run("DeleteFateAvatar", -1), "negative budget"),
                 })
        {
            StringAssert.StartsWith("[ReportDeleteFate] FAIL:", call, why);
            StringAssert.DoesNotContain("| log=", call, why + " — a refusal to run publishes no artifact");
        }
    }

    [Test]
    public void Run_refusesARootWithNoDescriptor()
    {
        var bare = new GameObject("BareRoot");
        try
        {
            var r = ReportDeleteFate.Run("BareRoot");
            StringAssert.StartsWith("[ReportDeleteFate] FAIL:", r);
            StringAssert.Contains("VRCAvatarDescriptor", r, "the refusal names the fix");
        }
        finally { Object.DestroyImmediate(bare); }
    }

    // The worktree hazard docs/dispatched-work.md names, in its Unity form: a venue's baked package pointer
    // can resolve to a checkout other than the one under test, and a green run then says nothing about these
    // sources. The runner prints the venue's `packages=` token; this prints the assembly that actually ran, so
    // the two can be read against each other from one log.
    [Test]
    public void Report_theAssemblyUnderTest()
    {
        Debug.Log("[ReportDeleteFateTests] tested assembly: "
                  + typeof(ReportDeleteFate).Assembly.Location);
        Assert.Pass(typeof(ReportDeleteFate).Assembly.Location);
    }
}
