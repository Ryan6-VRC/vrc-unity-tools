using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

// Direct coverage of ControllerFixpoint's comparison logic — the vrc-patterns gate's mechanism, which had
// none. Everything here is constructed in-process: menus via CreateInstance (no asset, no import), entry
// shapes as real directories under the OS temp dir. Nothing boots the gate.
//
// WHAT THIS FILE DOES NOT COVER, stated because a suite that looks uniform is read as uniform:
//
// RunGate's own composition is only partly covered. Which directories it gates and what it calls them are
// pinned (EnumerateEntries, EntryLabel) alongside the exit expression (GateExit), but the wiring that FEEDS
// that expression — `entryFailed = true` at each of its three sites, and the failedEntries/prefabFailed
// counters — stays inside RunGate, which ends in EditorApplication.Exit and so has no unit door short of
// lifting the loop bodies too. That was declined as more surgery than the coverage is worth, not judged
// impossible. The extractions moved the *computing* half out and left that *failing* half behind, so
// deleting any one `entryFailed = true` still logs FAIL and exits 0 with this whole suite green.
//
// What covers it instead is an end-to-end gate run against a scratch copy of vrc-patterns with injected
// drift, recorded in the PR that made each change. Do not read a green run here as "the gate fails when it
// should". Two rounds exist, and the second is not a re-run of the first:
//   - the PR that added this file: baseline exit 0; a renamed state, a deleted built menu, and an unclaimed
//     built controller each exit 1 with a named FAIL line;
//   - the PR that made entry enumeration recursive: three nested entries perturbed IN ONE RUN, expecting
//     19/22 rather than three separate 21/22s — because the arithmetic recursion newly exercises is that a
//     CHILD entry's failure increments failedEntries by one and does NOT fail its parent, and that a
//     parent+child pair is neither collapsed nor double-counted. Three isolated runs prove none of that.
//
// Check(), CompileToTemp, ImportCommittedAsset, Decode, and CheckPrefabIntegrity are boundary-bound
// (AssetDatabase, a real compile) and stay out. Decode's fixpoint property is owned next door by
// FixpointOracle + FixpointAcceptanceTests + RoundtripStressTests, deliberately not re-litigated here.
//
// FIXTURE VENUE: the temp dir, and nothing else. Every helper covered here (ParseControllerName,
// IsGuidConsumer, the orphan trio, the diff pair) touches no AssetDatabase, so an imported seed would buy
// nothing and cost an import per case. There is no asset fixture at all — the icon leg that needed one is
// no longer covered, for the reason the admission rule below gives.
//
// Menus are DESTROYED in TearDown — see the _made field for why that is load-bearing rather than tidiness.
//
// ADMISSION RULE, and it is what sets this file's size: a case earns its place iff breaking the line it
// pins leaves the gate green or unattributable, AND no other case here fails on that same edit. Three
// categories sit outside it and are deliberately absent, so their absence is not read as an oversight.
// Fields the schema cannot author — `icon`, `style`, `labels` (animator-schema.md §menu) — are compared by
// MenuDiff but can never differ at the gate, because the emitter writes neither side; the comparison is
// kept for the reason MenuDiff's header gives, and pinning it guarded nothing. Known divergences and known
// gaps are design records rather than guards — they fail only when someone FIXES the thing — so they live
// in ControllerFixpoint.cs beside the code they describe. And null-coalescing legs for a hand-edited
// asset model a shape the class header rules out: built/ is generated and nobody hand-maintains it.
//
// ASSERTIONS ARE EXACT-STRING on every case that carries an offender address. MenuDiff's whole output is an
// address plus a reason; a Does.Contain assertion would let the address construction rot untouched.
public class ControllerFixpointTests
{
    // ── Fixture plumbing ────────────────────────────────────────────────────────────────────────────

    string _tmp;

    // Every menu built by a case is tracked and destroyed here. This is LOAD-BEARING, not hygiene: an earlier
    // revision let the CreateInstance'd VRCExpressionsMenu objects leak, and the ~50 survivors broke 48
    // UNRELATED tests in this assembly — ControllerEmit's AddStateMachineBehaviour started returning null, so
    // controller-emit and decompile suites failed with NullReferenceExceptions pointing at production code
    // this change never touched. Measured: leak → 551/600; destroy → 600/600, with nothing else altered.
    // If you add a case that creates a ScriptableObject, register it.
    readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();

    [SetUp]
    public void SetUp()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "f42_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tmp);
    }

    // The destroy loop is in a finally because the recursive delete above it CAN throw — a transient lock or
    // AV scan on Windows raises IOException/UnauthorizedAccessException. Ordered the other way, one flaky
    // temp delete would skip the destroy and leak the whole case's menus, reproducing the 48-failure cascade
    // described on _made. A bad delete must cost this one test, not the suites downstream of it.
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

    // An entry dir under the temp venue. Files are written, never imported — these helpers read the
    // filesystem directly, so an AssetDatabase round trip would only slow the case down.
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

    // Every field the caller does not set stays at its SDK default, so a case's diff is exactly the field it
    // varied. `value` defaults to 1f, not 0f — measured by SdkDefaults_AreWhatTheFixturesAssume below rather
    // than asserted from the SDK's source, which is not on disk here.
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

    // Registered in _made for the same reason menus are — a leaked ScriptableObject in this assembly is what
    // broke 48 unrelated tests, and a VRCExpressionParameters leaks exactly as readily as a menu.
    VRCExpressionParameters Params(params VRCExpressionParameters.Parameter[] ps)
    {
        var p = ScriptableObject.CreateInstance<VRCExpressionParameters>();
        p.parameters = ps;
        _made.Add(p);
        return p;
    }

    // Every field the caller does not set stays at its SDK default, so a case's diff is exactly the field it
    // varied — the same contract Ctl above holds to.
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

    // The name leg is addressed POSITIONALLY (menu[i]) because a name mismatch is what makes the name
    // useless as an address; every leg after it uses the name. Pinning both halves of that switch.
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

    // The loop bound and the index-in-address together: three controls, the only difference in the LAST.
    // A single-control fixture cannot distinguish `i < ac.Count` from `i < ac.Count - 1`.
    [Test]
    public void MenuDiff_LastControlIsCompared()
    {
        var a = Menu("menu", Ctl("C1"), Ctl("C2"), Ctl("C3", parameter: "Right"));
        var b = Menu("menu", Ctl("C1"), Ctl("C2"), Ctl("C3", parameter: "Wrong"));
        Assert.AreEqual("menu 'C3': parameter 'Right' vs 'Wrong'", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // ── MenuDiff: subParameters ─────────────────────────────────────────────────────────────────────
    //
    // Covered where `labels`, `style` and `icon` are not, and the difference is authorability: a radial's
    // knob rides subParameters[0] (ControllerEmit), so this array is a function of the yaml and can drift.
    // The other three the emitter never writes — see MenuDiff's own header for why they are compared anyway.

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

    // The recursion's address is the parent CONTROL ("menu 'More'"), not the parent PAGE ("menu") — the
    // difference between passing `w` and passing `where` down, and the whole point of addressing a nested
    // offender. Asserted as a literal full string at two levels deep.
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
    //
    // MenuDiff at full branch coverage proves the comparator correct and says nothing about whether the
    // caller invokes it. These four cases are that decision.

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

    // The two asymmetric cases carry DIFFERENT remediations — one says delete or restore, the other says
    // regenerate — so the message is asserted, not just the verdict: told the wrong one, an author does the
    // wrong thing. The expected strings are duplicated from the implementation deliberately rather than
    // routed through a shared constant; a constant would still catch a mis-wired branch but would make a
    // REWRITE of either message invisible, and the wording is the part that instructs a human. This file is
    // the declared review invariant for that pair (tool-design.md §Duplication: a managed echo names its
    // canon, which is MenuPresence).
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
    //
    // Every [gate] FAIL line this tool logs is decoration if this returns 0 anyway. The failing-half wiring
    // around it (entryFailed, the two counters) still has no unit door and is covered only by the end-to-end
    // acceptance run described in the file header — but the expression itself is now pinned.

    [Test]
    public void GateExit_NothingFailed_IsZero()
        => Assert.AreEqual(0, ControllerFixpoint.GateExit(0, 0));

    [Test]
    public void GateExit_AnEntryFailed_IsOne()
        => Assert.AreEqual(1, ControllerFixpoint.GateExit(1, 0));

    // The prefab-integrity pass is the only one guarding hand-authored content, so its failures must reach
    // the exit code independently of the document passes — an && flipped to || would discard exactly this.
    [Test]
    public void GateExit_OnlyPrefabIntegrityFailed_IsOne()
        => Assert.AreEqual(1, ControllerFixpoint.GateExit(0, 1));

    // ── ForeignProjectPathLines: the committed-prefab provenance predicate ──────────────────────────
    //
    // Pure text in, offender handles out — no AssetDatabase, no live object, so the whole predicate is
    // unit-reachable even though its caller (CheckPrefabIntegrity) is boundary-bound and stays out of this
    // file. What is NOT covered here is the wiring: that CheckPrefabIntegrity calls this and folds its count
    // into offenderCount sits behind that same boundary, exactly like the missing-script and anchor-seam
    // checks beside it.

    // The leak shape this exists for: a cached `<guid>|<path>` reference back-filled with the inspecting
    // project's own layout. Exact-string on the handle — the line number and the echoed text ARE the fix
    // instruction's address, and a Does.Contain assertion would let either rot.
    [Test]
    public void ForeignProjectPathLines_BackFilledProjectPath_IsReportedWithItsLine()
        => CollectionAssert.AreEqual(
            new[] { "line 2: id: 8f0a|Assets/Somewhere/Thing.fbx" },
            ControllerFixpoint.ForeignProjectPathLines(
                "  m_Name: Thing\n  id: 8f0a|Assets/Somewhere/Thing.fbx\n  m_Enabled: 1\n"));

    // Line numbers are 1-based (an editor's gutter) and every hit is reported, not just the first — six
    // such lines in one prefab is the case that motivated the check.
    [Test]
    public void ForeignProjectPathLines_EveryHitIsReported_OneBasedLineNumbers()
        => CollectionAssert.AreEqual(
            new[] { "line 1: a: Assets/One", "line 3: c: Assets/Three" },
            ControllerFixpoint.ForeignProjectPathLines("a: Assets/One\nb: clean\nc: Assets/Three"));

    // The case the test above LOOKS like it covers and does not: a word ending in "Assets" followed by
    // a slash. `Assets/` must start a path segment, or a project's own MyAssets/ folder is a hard FAIL
    // carrying a diagnostic about a VRCFury back-fill that never happened.
    [Test]
    public void ForeignProjectPathLines_WordEndingInAssetsWithSlash_DoesNotFire()
    {
        Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines("m_Name: MyAssets/Thing"));
        Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines("path: MyProjectAssets/Sub/x.mat"));
    }

    // …while a real segment boundary still fires, whatever precedes it.
    [Test]
    public void ForeignProjectPathLines_AssetsAtSegmentBoundary_Fires()
    {
        Assert.IsNotEmpty(ControllerFixpoint.ForeignProjectPathLines("id: abc|Assets/Avatars/X.controller"));
        Assert.IsNotEmpty(ControllerFixpoint.ForeignProjectPathLines("path: Some/Where/Assets/X.mat"));
    }

    // ── MenuBeside: the re-derived filename convention ─────────────────────────────────────────────
    //
    // CompileController builds the same path independently (emitDir + "/" + name + "_Menu.asset"). These
    // cases are what keeps the two copies in step; if the compiler's formula moves, one of them fails.

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
    // DELIBERATELY NOT COVERED HERE: that entries are found at depth 1, 2 and 3, and the ordinal sort. The
    // end-to-end gate run over vrc-patterns names all 22 entries by root-relative path in its log, which is
    // that assertion against the real corpus rather than a fixture imitating it. What the corpus cannot
    // exercise is the two prunes (nothing in the library ships a controller.yaml under a reserved or dot
    // directory) and the leaf-name collision, so those are the cases here.

    // Checked at EVERY segment, not the leaf: this fixture's candidate entry is .git/refs/x, and no segment
    // below .git starts with a dot. A leaf-name filter over SearchOption.AllDirectories therefore finds it
    // and walks the whole of .git, while looking equivalent to this.
    [Test]
    public void EnumerateEntries_DotDirectoryAndItsDescendants_ArePruned()
    {
        File_(Dir(".git", "refs", "x"), "controller.yaml", "controller: FX\n");
        Assert.IsEmpty(ControllerFixpoint.EnumerateEntries(_tmp));
    }

    // built/ and assets/ are reserved inside an entry, so a controller.yaml under either is content — a
    // vendor sample, a stashed document — not an entry. Gating one invents a phantom Pattern.
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

    // The reserved-name compare is OrdinalIgnoreCase, and this is the case that pins it. IT NEEDS ITS OWN
    // PARENT: NTFS is case-insensitive, so creating "assets" and then "Assets" beside it returns the FIRST
    // directory rather than making a second — a mis-cased fixture added to the test above is silently the
    // lowercase one, and the assertion passes against an ordinal implementation. Measured, not reasoned:
    // Directory.CreateDirectory("e/assets") then ("e/Assets") leaves e holding exactly { "assets" }, while
    // ("f/Assets") alone leaves f holding literally { "Assets" }.
    //
    // What it defends, on Windows specifically: IsGuidConsumer probes with Directory.Exists, which is
    // case-insensitive there, so an ordinal compare here would let this directory read as reserved for tier
    // derivation while this walk descended into it and gated a phantom Pattern inside it.
    [Test]
    public void EnumerateEntries_MisCasedReservedDir_IsStillReserved()
    {
        File_(Dir("f", "Assets"), "controller.yaml", "controller: FX\n");
        File_(Dir("f"), "controller.yaml", "controller: FX\n");
        Assert.AreEqual(new[] { Path.Combine(_tmp, "f") },
            ControllerFixpoint.EnumerateEntries(_tmp).ToArray(),
            "a mis-cased Assets/ is the same directory to IsGuidConsumer's probe, so this walk must agree");
    }

    // The shape the whole recursion exists for (object-sync/ holds object-sync/y/). A walk that stopped
    // descending once it found an entry passes every other case in this section and still misses it.
    [Test]
    public void EnumerateEntries_EntryNestedInsideAnEntry_YieldsBoth()
    {
        File_(Dir("outer"), "controller.yaml", "controller: FX\n");
        File_(Dir("outer", "inner"), "controller.yaml", "controller: FX\n");
        Assert.AreEqual(new[] { Path.Combine(_tmp, "outer"), Path.Combine(_tmp, "outer", "inner") },
            ControllerFixpoint.EnumerateEntries(_tmp).ToArray());
    }

    // THE INVARIANT THE OTHER TWO DO NOT COMPOSE, and the reason this helper exists at all. Both cases
    // above pass against an EntryLabel that fell back to Path.GetFileName more eagerly — and that
    // implementation collapses vrc-patterns' two object-sync entries onto one address, in a gate whose
    // failure message is its product.
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

    // The PREFAB half of the same top-level-only read, and it is load-bearing in a way the assets/ case below
    // is not: EnumerateEntries' recursion rests on a parent never reading as a GUID-consumer because of a
    // CHILD entry's prefab, and this is the only case that pins it. Widen the *.prefab glob at the head of
    // IsGuidConsumer's || to AllDirectories and a nested Module's prefab promotes its parent Pattern, which
    // then fails for a missing built controller it was never meant to ship — while the case below, and every
    // other test here, stays green. The natural way to close the gap below is to widen BOTH globs in that one
    // expression, which is exactly the edit this forbids.
    [Test]
    public void IsGuidConsumer_PrefabOneLevelDown_IsNotSeen()
    {
        File_(Dir("nested"), "entry.prefab");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp),
            "a nested entry's prefab must not promote its parent's tier — EnumerateEntries rests on this");
    }

    // ── Orphan detection ───────────────────────────────────────────────────────────────────────────

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

    // The reported name is the ASSET's stem (FX_Menu), while the claim is checked against the controller
    // name (FX) — the caller appends ".asset" to what comes back.
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

    // THE CASE THIS WHOLE PASS EXISTS FOR. A `scratch:` flip or a reserved-name change drops (or adds) exactly
    // one entry, so it always lands on the count leg and never reaches the per-index loop. The count leg must
    // therefore NAME the parameter — a bare "104 vs 103" is the byte-compare diagnostic this pass rejects.
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

    // Equal length, differing names: addressed by INDEX, because no name is agreed yet. Same transition
    // MenuDiff makes, and the reason a reorder reports the way the next case says it does.
    // Lengths differ but the name SETS match, so both difference lists come back empty. Without its own leg
    // this emits "dropped (none), added (none)" and names nothing — the diagnostic this whole pass rejects.
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

    // networkSynced earns its own case where the other scalars share one: this is the field that costs a sync
    // bit on the built avatar, so a silent flip here is the regression with consequences beyond legibility.
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

    // The fourth scalar. It carried no case of its own until now — its only coverage was a NaN record that
    // pinned this comparison MISFIRING rather than working, so retiring that record would have left the leg
    // bare. A finite pair is what the emitter actually produces.
    [Test]
    public void ParamsDiff_DefaultValueDiffers_AddressedByName()
        => Assert.AreEqual("params 'Hat': defaultValue 0 vs 1",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("Hat")), Params(Prm("Hat", defaultValue: 1f)), "params"));

    // The loop bound and the index-in-address together — a single-entry fixture cannot distinguish
    // `i < ap.Length` from `i < ap.Length - 1`.
    [Test]
    public void ParamsDiff_LastParamIsCompared()
        => Assert.AreEqual("params 'C': networkSynced False vs True",
            ControllerFixpoint.ParamsDiff(
                Params(Prm("A"), Prm("B"), Prm("C")),
                Params(Prm("A"), Prm("B"), Prm("C", networkSynced: true)), "params"));

    // ParamsPresence: whether ParamsDiff is called at all. The two Fail legs must be DISTINGUISHABLE, which is
    // the whole reason the refusal strings live in the helper rather than at the call site.

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

    // Must NOT be a bare "regenerate built/". A document that emitted nothing and now emits something can be
    // a `scratch:` flipped off or a name leaving the reserved set, where regenerating launders the change —
    // the same trap the ParamsDiff call site refuses. The two Fail legs stay distinguishable by lead clause.
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

    // ParamsBeside: the re-derived filename convention. This case is the ONLY thing keeping this copy in step
    // with CompileController's own formula — see the helper's header for why they are not shared.

    [Test]
    public void ParamsBeside_DerivesParamsAssetBesideController()
        => Assert.AreEqual("a/b/FX_Parameters.asset", ControllerFixpoint.ParamsBeside("a/b/FX.controller"));

    // OrphanParams: a committed params asset whose controller no document claims.

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
    // The importer around this is AssetDatabase-bound and stays out of this suite (see the header). The
    // CONDITION is pure filesystem, so it gets a door here — otherwise the guard is verified once by a
    // one-off end-to-end gate run and never again, which is the failure mode this file's header already
    // records for `entryFailed = true`: delete the line and everything stays green.

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
        // The obvious remedy is the wrong one, and saying so is the point of the message — regenerating
        // built/ mints a new GUID too and turns the gate green on the break. Same shape as the params-diff
        // message and ForeignProjectPathLines.
        StringAssert.Contains("Do NOT", msg, "the message must refuse the fix that launders the defect");
    }

}
