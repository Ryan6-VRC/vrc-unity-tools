using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

// Direct coverage of ControllerFixpoint's comparison logic, the vrc-patterns gate's mechanism. Fixtures are
// in-process and import nothing: menus and params via CreateInstance, entry shapes as real directories
// under the OS temp dir. Nothing boots the gate.
//
// NOT COVERED: the wiring that feeds GateExit. `entryFailed = true` at each of its sites and the
// failedEntries/prefabFailed counters stay inside RunGate, which ends in EditorApplication.Exit and has no
// unit door, so deleting any one `entryFailed = true` still logs FAIL and exits 0 with this suite green. Do
// not read a green run here as "the gate fails when it should"; only an end-to-end gate run against a
// scratch copy of vrc-patterns with injected drift shows that. Perturb several nested entries in one such
// run: a child's failure must add one to failedEntries and must not fail its parent.
//
// Check(), CompileToTemp, ImportCommittedAsset, Decode, and CheckPrefabIntegrity are boundary-bound
// (AssetDatabase, a real compile) and stay out. Decode's fixpoint property is owned by FixpointOracle,
// FixpointAcceptanceTests and RoundtripStressTests.
//
// ADMISSION RULE: a case earns its place iff breaking the line it pins leaves the gate green or
// unattributable, AND no other case here fails on that same edit. Three categories are deliberately absent:
//   - MenuDiff's `style`, `labels` and `icon` legs: the schema cannot author the first two and the gate
//     cannot see an icon (animator-schema.md §menu).
//   - Known divergences and gaps, which fail only when someone FIXES the thing; they live in
//     ControllerFixpoint.cs beside the code they describe.
//   - Null-coalescing legs for a hand-edited asset: built/ is generated and nobody hand-maintains it.
//
// Cases that check a MenuDiff, ParamsDiff or ForeignProjectPathLines address assert the exact string: a
// Does.Contain assertion would let the address construction rot untouched.
public class ControllerFixpointTests
{
    // ── Fixture plumbing ────────────────────────────────────────────────────────────────────────────

    string _tmp;

    // Every ScriptableObject a case creates must be registered here for TearDown to destroy. A leaked SDK
    // ScriptableObject makes AddStateMachineBehaviour return null and fails unrelated suites in this assembly
    // with NullReferenceExceptions (see EmitResult.DestroyUnpersisted).
    readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

    [SetUp]
    public void SetUp()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "f42_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tmp);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (_tmp != null && Directory.Exists(_tmp)) Directory.Delete(_tmp, true);
        }
        finally
        {
            _tmp = null;
            foreach (var o in _made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _made.Clear();
        }
    }

    string Dir(params string[] segments)
    {
        var full = Path.Combine(new[] { _tmp }.Concat(segments).ToArray());
        Directory.CreateDirectory(full);
        return full;
    }

    string File_(string dir, string name, string content = "x")
    {
        var p = Path.Combine(dir, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    string Yaml(string content)
    {
        var p = Path.Combine(_tmp, "controller.yaml");
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    VRCExpressionsMenu Menu(string name, params VRCExpressionsMenu.Control[] controls)
    {
        var m = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
        m.name = name;
        m.controls = controls.ToList();
        _made.Add(m);
        return m;
    }

    static VRCExpressionsMenu.Control Ctl(
        string name,
        VRCExpressionsMenu.Control.ControlType type = VRCExpressionsMenu.Control.ControlType.Toggle,
        string parameter = "P",
        float value = 1f)
        => new VRCExpressionsMenu.Control
        {
            name = name,
            type = type,
            parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
            value = value,
        };

    VRCExpressionParameters Params(params VRCExpressionParameters.Parameter[] ps)
    {
        var p = ScriptableObject.CreateInstance<VRCExpressionParameters>();
        p.parameters = ps;
        _made.Add(p);
        return p;
    }

    static VRCExpressionParameters.Parameter Prm(
        string name,
        VRCExpressionParameters.ValueType valueType = VRCExpressionParameters.ValueType.Bool,
        bool saved = false,
        float defaultValue = 0f,
        bool networkSynced = false)
        => new VRCExpressionParameters.Parameter
        {
            name = name,
            valueType = valueType,
            saved = saved,
            defaultValue = defaultValue,
            networkSynced = networkSynced,
        };

    // ── MenuDiff: the equal case, and the page/count legs ───────────────────────────────────────────

    [Test]
    public void MenuDiff_IdenticalTrees_ReturnsNull()
    {
        var a = Menu("menu", Ctl("Toggle A"), Ctl("Toggle B"));
        var b = Menu("menu", Ctl("Toggle A"), Ctl("Toggle B"));
        Assert.IsNull(ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_PageNameDiffers_NamesBothSides()
    {
        var diff = ControllerFixpoint.MenuDiff(Menu("Committed"), Menu("Compiled"), "menu");
        Assert.AreEqual("menu: page name 'Committed' vs 'Compiled'", diff);
    }

    [Test]
    public void MenuDiff_ControlCountDiffers_ReportsCommittedFirst()
    {
        var a = Menu("menu", Ctl("A"), Ctl("B"));
        var b = Menu("menu", Ctl("A"));
        Assert.AreEqual("menu: committed has 2 control(s), compiled has 1", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // ── MenuDiff: the per-control legs, and the address transition ──────────────────────────────────

    [Test]
    public void MenuDiff_ControlNameDiffers_AddressedByIndexNotName()
    {
        var a = Menu("menu", Ctl("Hat"));
        var b = Menu("menu", Ctl("Cap"));
        Assert.AreEqual("menu[0]: name 'Hat' vs 'Cap'", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_TypeDiffers_AddressedByName()
    {
        var a = Menu("menu", Ctl("Hat", VRCExpressionsMenu.Control.ControlType.Toggle));
        var b = Menu("menu", Ctl("Hat", VRCExpressionsMenu.Control.ControlType.Button));
        Assert.AreEqual("menu 'Hat': type Toggle vs Button", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_ParameterDiffers_AddressedByName()
    {
        var a = Menu("menu", Ctl("Hat", parameter: "Hat/On"));
        var b = Menu("menu", Ctl("Hat", parameter: "Hat/Off"));
        Assert.AreEqual("menu 'Hat': parameter 'Hat/On' vs 'Hat/Off'", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_ValueDiffers_AddressedByName()
    {
        var a = Menu("menu", Ctl("Hat", value: 1f));
        var b = Menu("menu", Ctl("Hat", value: 2f));
        Assert.AreEqual("menu 'Hat': value 1 vs 2", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_LastControlIsCompared()
    {
        var a = Menu("menu", Ctl("C1"), Ctl("C2"), Ctl("C3", parameter: "Right"));
        var b = Menu("menu", Ctl("C1"), Ctl("C2"), Ctl("C3", parameter: "Wrong"));
        Assert.AreEqual("menu 'C3': parameter 'Right' vs 'Wrong'", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // ── MenuDiff: subParameters ─────────────────────────────────────────────────────────────────────
    //
    // Covered where `labels`, `style` and `icon` are not: a radial's knob rides subParameters[0]
    // (ControllerEmit), so this array is a function of the yaml and can drift.

    [Test]
    public void MenuDiff_SubParameterCountDiffers()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "X" } };
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].subParameters = new VRCExpressionsMenu.Control.Parameter[0];
        Assert.AreEqual("menu 'Hat': 1 subParameter(s) vs 0", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_SubParameterNameDiffers_AddressedByIndex()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].subParameters = new[] {
            new VRCExpressionsMenu.Control.Parameter { name = "X" },
            new VRCExpressionsMenu.Control.Parameter { name = "Y" } };
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].subParameters = new[] {
            new VRCExpressionsMenu.Control.Parameter { name = "X" },
            new VRCExpressionsMenu.Control.Parameter { name = "Z" } };
        Assert.AreEqual("menu 'Hat': subParameter[1] 'Y' vs 'Z'", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // ── MenuDiff: sub-menu recursion ────────────────────────────────────────────────────────────────

    [Test]
    public void MenuDiff_SubMenuOnOneSideOnly()
    {
        var a = Menu("menu", Ctl("More"));
        a.controls[0].subMenu = Menu("Page");
        var b = Menu("menu", Ctl("More"));
        Assert.AreEqual("menu 'More': one side has a sub-menu and the other does not",
            ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_NestedDiff_AddressedByParentControlChain()
    {
        VRCExpressionsMenu Nest(string leafParam)
        {
            var page2 = Menu("Deep", Ctl("Leaf", parameter: leafParam));
            var page1 = Menu("Page", Ctl("Down"));
            page1.controls[0].subMenu = page2;
            var root = Menu("menu", Ctl("More"));
            root.controls[0].subMenu = page1;
            return root;
        }
        Assert.AreEqual("menu 'More' 'Down' 'Leaf': parameter 'Right' vs 'Wrong'",
            ControllerFixpoint.MenuDiff(Nest("Right"), Nest("Wrong"), "menu"));
    }

    // ── MenuPresence: whether MenuDiff is called at all ────────────────────────────────────────────

    [Test]
    public void MenuPresence_BothPresent_Compares()
    {
        var (pass, msg) = ControllerFixpoint.MenuPresence(Menu("menu"), true);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Compare, pass);
        Assert.IsNull(msg);
    }

    [Test]
    public void MenuPresence_NeitherPresent_Skips()
    {
        var (pass, msg) = ControllerFixpoint.MenuPresence(null, false);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Skip, pass);
        Assert.IsNull(msg);
    }

    // The two asymmetric cases carry different remediations, so the message is asserted as well as the verdict.
    // The expected strings are duplicated from MenuPresence, their canon, as a managed echo (tool-design.md
    // §Duplication): a shared constant would make a rewrite of either message invisible.
    [Test]
    public void MenuPresence_CommittedOnly_FailsTellingAuthorToDeleteOrRestore()
    {
        var (pass, msg) = ControllerFixpoint.MenuPresence(null, true);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Fail, pass);
        Assert.AreEqual("built/ ships a menu asset the yaml no longer emits — delete it or restore the menu: block", msg);
    }

    [Test]
    public void MenuPresence_FreshOnly_FailsTellingAuthorToRegenerate()
    {
        var (pass, msg) = ControllerFixpoint.MenuPresence(Menu("menu"), false);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Fail, pass);
        Assert.AreEqual("yaml emits a menu but built/ has none — regenerate built/", msg);
    }

    // ── GateExit: the only thing gate.ps1 actually reads ───────────────────────────────────────────

    [Test]
    public void GateExit_NothingFailed_IsZero()
        => Assert.AreEqual(0, ControllerFixpoint.GateExit(0, 0));

    [Test]
    public void GateExit_AnEntryFailed_IsOne()
        => Assert.AreEqual(1, ControllerFixpoint.GateExit(1, 0));

    [Test]
    public void GateExit_OnlyPrefabIntegrityFailed_IsOne()
        => Assert.AreEqual(1, ControllerFixpoint.GateExit(0, 1));

    // ── ModelImporterOffense: the SDK panel's two mesh-importer refusals, read off committed .meta text ─

    static string[] ModelMeta(string readable, string bsNormals, string legacy) => new[]
    {
        "fileFormatVersion: 2", "ModelImporter:", "  animations:", "    isReadable: " + readable,
        "  tangentSpace:",
        "    legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes: " + legacy,
        "    blendShapeNormalImportMode: " + bsNormals,
    };

    [Test]
    public void ModelImporterOffense_ReadableWithLegacy_Passes()
        => Assert.IsNull(ControllerFixpoint.ModelImporterOffense(ModelMeta("1", "1", "1")));

    [Test]
    public void ModelImporterOffense_NormalsNoneWithoutLegacy_Passes()
        => Assert.IsNull(ControllerFixpoint.ModelImporterOffense(ModelMeta("1", "2", "0")));

    [Test]
    public void ModelImporterOffense_BothRefusals_NamesBoth()
        => Assert.AreEqual("Read/Write disabled (isReadable: 0); blendshape normals = Calculate without legacy",
            ControllerFixpoint.ModelImporterOffense(ModelMeta("0", "1", "0")));

    [Test]
    public void ModelImporterOffense_FieldAbsent_Fails()
        => Assert.AreEqual("isReadable absent",
            ControllerFixpoint.ModelImporterOffense(ModelMeta("1", "1", "1").Where(l => !l.Contains("isReadable")).ToArray()));

    [Test]
    public void ModelImporterOffense_NotAModelImporter_IsIgnored()
        => Assert.IsNull(ControllerFixpoint.ModelImporterOffense(new[] { "fileFormatVersion: 2", "TextureImporter:", "  isReadable: 0" }));

    // ── ForeignProjectPathLines: the committed-prefab provenance predicate ──────────────────────────

    // The leak shape this exists for: a cached `<guid>|<path>` reference back-filled with the inspecting
    // project's own layout.
    [Test]
    public void ForeignProjectPathLines_BackFilledProjectPath_IsReportedWithItsLine()
        => CollectionAssert.AreEqual(
            new[] { "line 2: id: 8f0a|Assets/Somewhere/Thing.fbx" },
            ControllerFixpoint.ForeignProjectPathLines(
                "  m_Name: Thing\n  id: 8f0a|Assets/Somewhere/Thing.fbx\n  m_Enabled: 1\n"));

    [Test]
    public void ForeignProjectPathLines_EveryHitIsReported_OneBasedLineNumbers()
        => CollectionAssert.AreEqual(
            new[] { "line 1: a: Assets/One", "line 3: c: Assets/Three" },
            ControllerFixpoint.ForeignProjectPathLines("a: Assets/One\nb: clean\nc: Assets/Three"));

    // `Assets/` must start a path segment, or a project's own MyAssets/ folder is a hard FAIL carrying a
    // diagnostic about a VRCFury back-fill that never happened.
    [Test]
    public void ForeignProjectPathLines_WordEndingInAssetsWithSlash_DoesNotFire()
    {
        Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines("m_Name: MyAssets/Thing"));
        Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines("path: MyProjectAssets/Sub/x.mat"));
    }

    [Test]
    public void ForeignProjectPathLines_AssetsAtSegmentBoundary_Fires()
    {
        Assert.IsNotEmpty(ControllerFixpoint.ForeignProjectPathLines("id: abc|Assets/Avatars/X.controller"));
        Assert.IsNotEmpty(ControllerFixpoint.ForeignProjectPathLines("path: Some/Where/Assets/X.mat"));
    }

    // ── MenuBeside: the re-derived filename convention ─────────────────────────────────────────────
    //
    // CompileController builds this path independently. These cases pin this copy against literals and
    // MenuEmitTests pins the compiler's, so drift in either goes red.

    [Test]
    public void MenuBeside_DerivesMenuAssetBesideController()
        => Assert.AreEqual("a/b/FX_Menu.asset", ControllerFixpoint.MenuBeside("a/b/FX.controller"));

    [Test]
    public void MenuBeside_NormalizesBackslashesToForwardSlashes()
        => Assert.AreEqual("a/b/FX_Menu.asset", ControllerFixpoint.MenuBeside(@"a\b\FX.controller"));

    // ── ParseControllerName: the agreeing cases ────────────────────────────────────────────────────

    [Test]
    public void ParseControllerName_PlainScalar()
        => Assert.AreEqual("FX", ControllerFixpoint.ParseControllerName(Yaml("# header\ncontroller: FX\n")));

    [Test]
    public void ParseControllerName_NoControllerKey_ReturnsNull()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("clips:\n  - name: a\n")));

    [Test]
    public void ParseControllerName_BareKeyWithNoValue_ReturnsNull()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("controller:\n")));

    // ── EnumerateEntries / EntryLabel: which directories the gate gates, and what it calls them ────
    //
    // Deep discovery and the ordinal sort are left to the end-to-end gate run over vrc-patterns, whose log
    // names every entry by root-relative path.

    // The candidate entry is .git/refs/x because no segment below .git starts with a dot, so a leaf-name
    // filter over SearchOption.AllDirectories finds it where a check at every segment does not.
    [Test]
    public void EnumerateEntries_DotDirectoryAndItsDescendants_ArePruned()
    {
        File_(Dir(".git", "refs", "x"), "controller.yaml", "controller: FX\n");
        Assert.IsEmpty(ControllerFixpoint.EnumerateEntries(_tmp));
    }

    [Test]
    public void EnumerateEntries_ControllerYamlUnderReservedDir_IsNotAnEntry()
    {
        File_(Dir("e", "built"), "controller.yaml", "controller: FX\n");
        File_(Dir("e", "assets"), "controller.yaml", "controller: FX\n");
        File_(Dir("e"), "controller.yaml", "controller: FX\n");
        Assert.AreEqual(new[] { Path.Combine(_tmp, "e") },
            ControllerFixpoint.EnumerateEntries(_tmp).ToArray(),
            "only the entry itself — a controller.yaml under built/ or assets/ is content");
    }

    // This case needs its own parent: NTFS is case-insensitive, so creating "Assets" beside an existing
    // "assets" returns the first directory, and a mis-cased fixture added to the test above is silently the
    // lowercase one and passes against an ordinal compare.
    [Test]
    public void EnumerateEntries_MisCasedReservedDir_IsStillReserved()
    {
        File_(Dir("f", "Assets"), "controller.yaml", "controller: FX\n");
        File_(Dir("f"), "controller.yaml", "controller: FX\n");
        Assert.AreEqual(new[] { Path.Combine(_tmp, "f") },
            ControllerFixpoint.EnumerateEntries(_tmp).ToArray(),
            "a mis-cased Assets/ is the same directory to IsGuidConsumer's probe, so this walk must agree");
    }

    [Test]
    public void EnumerateEntries_EntryNestedInsideAnEntry_YieldsBoth()
    {
        File_(Dir("outer"), "controller.yaml", "controller: FX\n");
        File_(Dir("outer", "inner"), "controller.yaml", "controller: FX\n");
        Assert.AreEqual(new[] { Path.Combine(_tmp, "outer"), Path.Combine(_tmp, "outer", "inner") },
            ControllerFixpoint.EnumerateEntries(_tmp).ToArray());
    }

    [Test]
    public void EntryLabel_TwoEntriesSharingALeafName_GetDistinctLabels()
    {
        var top = ControllerFixpoint.EntryLabel(_tmp, Path.Combine(_tmp, "object-sync"));
        var nested = ControllerFixpoint.EntryLabel(
            _tmp, Path.Combine(_tmp, "compositions", "object-sync-demo", "object-sync"));
        Assert.AreEqual("object-sync", top);
        Assert.AreEqual("compositions/object-sync-demo/object-sync", nested);
        Assert.AreNotEqual(top, nested, "two entries with one leaf name must not share a log address");
    }

    // ── IsGuidConsumer: the tier derivation ────────────────────────────────────────────────────────
    //
    // A false answer here means a Module whose built controller went missing passes as a Pattern, so each
    // term of the || matters independently.

    [Test]
    public void IsGuidConsumer_PrefabPresent()
    {
        File_(_tmp, "entry.prefab");
        Assert.IsTrue(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

    [Test]
    public void IsGuidConsumer_BuiltDirPresent_EvenWhenEmpty()
    {
        Dir("built");
        Assert.IsTrue(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

    [Test]
    public void IsGuidConsumer_AssetsWithRealFile()
    {
        File_(Dir("assets"), "mesh.fbx");
        Assert.IsTrue(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

    // A .meta with no asset beside it is import residue, not shipped content.
    [Test]
    public void IsGuidConsumer_AssetsWithOnlyMetaFiles_IsNotAConsumer()
    {
        var assets = Dir("assets");
        File_(assets, "mesh.fbx.meta");
        File_(assets, "other.meta");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

    // Only this case pins the top-level read: widen the *.prefab glob to AllDirectories and a nested Module's
    // prefab promotes its parent Pattern, which then fails for a built controller it was never meant to ship.
    [Test]
    public void IsGuidConsumer_PrefabOneLevelDown_IsNotSeen()
    {
        File_(Dir("nested"), "entry.prefab");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp),
            "a nested entry's prefab must not promote its parent's tier — EnumerateEntries rests on this");
    }

    // ── Orphan detection ───────────────────────────────────────────────────────────────────────────

    // Every other fixture claims either the exact name or a wholly different one, so this case alone pins the
    // Ordinal comparer each helper builds. On Windows a built artifact can differ from its claim only by case;
    // an ignore-case comparer reads it as claimed and the orphan pass goes quiet.
    [Test]
    public void OrphanClaimsAreOrdinal_AcrossAllThreeHelpers()
    {
        var built = Dir("built");
        File_(built, "FX.controller");
        File_(built, "FX_Menu.asset");
        File_(built, "FX_Parameters.asset");

        var miscased = new[] { "Fx" };
        CollectionAssert.AreEquivalent(new[] { "FX" },
            ControllerFixpoint.OrphanControllers(built, miscased).ToList(), "controllers");
        CollectionAssert.AreEquivalent(new[] { "FX_Menu" },
            ControllerFixpoint.OrphanMenus(built, miscased).ToList(), "menus");
        CollectionAssert.AreEquivalent(new[] { "FX_Parameters" },
            ControllerFixpoint.OrphanParams(built, miscased).ToList(), "params");
    }

    [Test]
    public void OrphanControllers_UnclaimedController_IsReported()
    {
        var built = Dir("built");
        File_(built, "FX.controller");
        File_(built, "Gesture.controller");
        CollectionAssert.AreEquivalent(new[] { "Gesture" },
            ControllerFixpoint.OrphanControllers(built, new[] { "FX" }).ToList());
    }

    [Test]
    public void OrphanMenus_ClaimedMenu_IsNotAnOrphan()
    {
        var built = Dir("built");
        File_(built, "FX_Menu.asset");
        Assert.IsEmpty(ControllerFixpoint.OrphanMenus(built, new[] { "FX" }).ToList());
    }

    [Test]
    public void OrphanMenus_UnclaimedMenu_ReportsAssetStemNotControllerName()
    {
        var built = Dir("built");
        File_(built, "Gesture_Menu.asset");
        CollectionAssert.AreEquivalent(new[] { "Gesture_Menu" },
            ControllerFixpoint.OrphanMenus(built, new[] { "FX" }).ToList());
    }

    // ── ParamsDiff / ParamsPresence / ParamsBeside / OrphanParams ──────────────────────────────────
    //
    // Fixtures are CreateInstance plus a plain managed field assignment — none of the three mutations
    // docs/verify.md §Test venue forbids.

    [Test]
    public void ParamsDiff_IdenticalLists_ReturnsNull()
        => Assert.IsNull(ControllerFixpoint.ParamsDiff(
            Params(Prm("A"), Prm("B")), Params(Prm("A"), Prm("B")), "params"));

    // A `scratch:` flip or a reserved-name change drops or adds an entry, so it lands on the count leg and
    // never reaches the per-index loop. The count leg must therefore name the parameter: a bare "104 vs 103"
    // names no offender.
    [Test]
    public void ParamsDiff_DroppedParam_CountLegNamesTheOffender()
        => Assert.AreEqual(
            "params: committed has 2 parameter(s), compiled has 1 — compiled dropped 'IsAnimatorEnabled', added (none)",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("Toggle"), Prm("IsAnimatorEnabled")), Params(Prm("Toggle")), "params"));

    [Test]
    public void ParamsDiff_AddedParam_CountLegNamesTheOffender()
        => Assert.AreEqual(
            "params: committed has 1 parameter(s), compiled has 2 — compiled dropped (none), added 'Extra'",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("Toggle")), Params(Prm("Toggle"), Prm("Extra")), "params"));

    // Lengths differ but the name sets match, so both difference lists come back empty. Without its own leg
    // this emits "dropped (none), added (none)".
    [Test]
    public void ParamsDiff_DuplicatedName_SaysSoRatherThanNamingNothing()
        => Assert.AreEqual(
            "params: committed has 3 parameter(s), compiled has 2 — same names either side, so one of them declares a name twice",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("A"), Prm("B"), Prm("B")), Params(Prm("A"), Prm("B")), "params"));

    [Test]
    public void ParamsDiff_NameDiffers_AddressedByIndexNotName()
        => Assert.AreEqual("params[0]: name 'A' vs 'B'",
            ControllerFixpoint.ParamsDiff(Params(Prm("A")), Params(Prm("B")), "params"));

    [Test]
    public void ParamsDiff_ValueTypeDiffers_AddressedByName()
        => Assert.AreEqual("params 'Hat': valueType Bool vs Int",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("Hat")), Params(Prm("Hat", valueType: VRCExpressionParameters.ValueType.Int)), "params"));

    [Test]
    public void ParamsDiff_NetworkSyncedDiffers_AddressedByName()
        => Assert.AreEqual("params 'Hat': networkSynced True vs False",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("Hat", networkSynced: true)), Params(Prm("Hat")), "params"));

    [Test]
    public void ParamsDiff_SavedDiffers_AddressedByName()
        => Assert.AreEqual("params 'Hat': saved True vs False",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("Hat", saved: true)), Params(Prm("Hat")), "params"));

    [Test]
    public void ParamsDiff_DefaultValueDiffers_AddressedByName()
        => Assert.AreEqual("params 'Hat': defaultValue 0 vs 1",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("Hat")), Params(Prm("Hat", defaultValue: 1f)), "params"));

    [Test]
    public void ParamsDiff_LastParamIsCompared()
        => Assert.AreEqual("params 'C': networkSynced False vs True",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("A"), Prm("B"), Prm("C")),
                Params(Prm("A"), Prm("B"), Prm("C", networkSynced: true)), "params"));

    // ParamsPresence: whether ParamsDiff is called at all.

    [Test]
    public void ParamsPresence_NeitherSideHasOne_Skips()
    {
        var (pass, msg) = ControllerFixpoint.ParamsPresence(null, false);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Skip, pass);
        Assert.IsNull(msg);
    }

    [Test]
    public void ParamsPresence_BothSidesHaveOne_Compares()
    {
        var (pass, msg) = ControllerFixpoint.ParamsPresence(Params(Prm("A")), true);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Compare, pass);
        Assert.IsNull(msg);
    }

    [Test]
    public void ParamsPresence_CommittedOnly_FailsNamingBothRemedies()
    {
        var (pass, msg) = ControllerFixpoint.ParamsPresence(null, true);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Fail, pass);
        Assert.AreEqual("built/ ships a params asset the yaml no longer emits — delete it if those parameters " +
                        "were removed on purpose, or restore them if a `scratch:` flag or the reserved-name set " +
                        "wrongly emptied the list", msg);
    }

    // Must NOT be a bare "regenerate built/". A document that emitted nothing and now emits something can be a
    // `scratch:` flipped off or a name leaving the reserved set, where regenerating launders the change.
    [Test]
    public void ParamsPresence_FreshOnly_FailsWithTheOtherMessage()
    {
        var (pass, msg) = ControllerFixpoint.ParamsPresence(Params(Prm("A")), false);
        Assert.AreEqual(ControllerFixpoint.MenuPass.Fail, pass);
        Assert.AreEqual("yaml emits parameters but built/ has none — regenerating built/ is the fix ONLY if " +
                        "the yaml's own parameter list changed on purpose. If a `scratch:` flag or " +
                        "ControllerRules.VrcReservedParams changed, settle that first: regenerating blind " +
                        "commits it as though reviewed", msg);
    }

    // ParamsBeside: this case pins this copy of CompileController's filename formula against literals; see the
    // helper's header for why it is not shared.

    [Test]
    public void ParamsBeside_DerivesParamsAssetBesideController()
        => Assert.AreEqual("a/b/FX_Parameters.asset", ControllerFixpoint.ParamsBeside("a/b/FX.controller"));

    [Test]
    public void OrphanParams_ClaimedParams_IsNotAnOrphan()
    {
        var built = Dir("built");
        File_(built, "FX_Parameters.asset");
        Assert.IsEmpty(ControllerFixpoint.OrphanParams(built, new[] { "FX" }).ToList());
    }

    [Test]
    public void OrphanParams_UnclaimedParams_ReportsAssetStemNotControllerName()
    {
        var built = Dir("built");
        File_(built, "Gesture_Parameters.asset");
        CollectionAssert.AreEquivalent(new[] { "Gesture_Parameters" },
            ControllerFixpoint.OrphanParams(built, new[] { "FX" }).ToList());
    }

    // ── The committed-.meta presence guard ──────────────────────────────────────────────────────────
    //
    // The importer around this is AssetDatabase-bound and stays out of this suite; only the condition is pure
    // filesystem.

    [Test]
    public void MissingCommittedMeta_PassesWhenTheMetaIsThere()
    {
        var asset = File_(_tmp, "FX.controller", "%YAML 1.1\n");
        File_(_tmp, "FX.controller.meta", "guid: 1111111111111111\n");
        Assert.IsNull(ControllerFixpoint.MissingCommittedMeta(asset));
    }

    [Test]
    public void MissingCommittedMeta_NamesTheArtifactAndRefusesTheLaunderingFix()
    {
        var asset = File_(_tmp, "FX.controller", "%YAML 1.1\n");   // no .meta beside it

        var msg = ControllerFixpoint.MissingCommittedMeta(asset);
        Assert.IsNotNull(msg, "a .meta-less import mints a fresh GUID and every structural comparison still "
            + "passes — presence is the only thing that can catch it");
        StringAssert.Contains(asset, msg, "the offender must be named: the gate line is all the reader gets");
        // The obvious remedy is the wrong one: regenerating built/ mints a new GUID too and turns the gate green
        // on the break.
        StringAssert.Contains("Do NOT", msg, "the message must refuse the fix that launders the defect");
    }

}
