using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

// Direct coverage of ControllerFixpoint's comparison logic — the vrc-patterns gate's mechanism, which had
// none. Everything here is constructed in-process: menus via CreateInstance (no asset, no import), entry
// shapes as real directories under the OS temp dir. Nothing boots the gate.
//
// WHAT THIS FILE DOES NOT COVER, stated because a suite that looks uniform is read as uniform:
//
// RunGate's own composition is only partly covered. The exit expression itself is pinned (GateExit), but the
// wiring that FEEDS it — `entryFailed = true` at each of its three sites, and the failedEntries/prefabFailed
// counters — stays inside RunGate, which ends in EditorApplication.Exit and so has no unit door short of
// lifting the loop bodies too. That was declined as more surgery than the coverage is worth, not judged
// impossible. The extraction below moved the *computing* half out and left that *failing* half behind, so
// deleting any one `entryFailed = true` still logs FAIL and exits 0 with this whole suite green.
//
// What covers it instead is an end-to-end gate run against a scratch copy of vrc-patterns with injected
// drift (baseline exit 0; a renamed state, a deleted built menu, and an unclaimed built controller each exit
// 1 with a named FAIL line), recorded in the PR that added this file. Do not read a green run here as "the
// gate fails when it should".
//
// Check(), CompileToTemp, ImportCommitted(Menu), Decode, and CheckPrefabIntegrity are boundary-bound
// (AssetDatabase, a real compile) and stay out. Decode's fixpoint property is owned next door by
// FixpointOracle + FixpointAcceptanceTests + RoundtripStressTests, deliberately not re-litigated here.
//
// FIXTURE VENUE: the temp dir, not the Assets/Agent/Scratch seed folder this suite normally uses. The
// filesystem helpers (ParseControllerName, IsGuidConsumer, the orphan pair) touch no AssetDatabase, so an
// imported seed would buy nothing and cost an import per case. The icon fixture is the one exception and
// does use a real asset folder, because AssetDatabase.GetAssetPath is the thing under test there.
//
// Menus are DESTROYED in TearDown — see the _made field for why that is load-bearing rather than tidiness.
//
// ASSERTIONS ARE EXACT-STRING on every case that carries an offender address. MenuDiff's whole output is an
// address plus a reason; a Does.Contain assertion would let the address construction rot untouched. The one
// exception is the NaN case, whose message interpolates float.ToString() and is culture-dependent.
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

    // ── The SDK shapes every fixture below depends on ──────────────────────────────────────────────
    //
    // MenuDiff's null-handling only makes sense against particular SDK shapes: `parameter` a class (so `?.`
    // means something), `Label` a struct (so only its name can be null), `controls` pre-initialised, `value`
    // defaulting to 1f. Those are asserted here by measurement, not read off the SDK — VRCSDK3A ships as a
    // DLL, and this suite must not rest on a claim no committed artifact can check. Without this case the
    // fixtures encode the assumption silently and an SDK change would surface as a confusing diff elsewhere.
    [Test]
    public void SdkDefaults_AreWhatTheFixturesAssume()
    {
        var fresh = new VRCExpressionsMenu.Control();
        Assert.AreEqual(1f, fresh.value, "fixtures set value explicitly because the default is 1f, not 0f");
        Assert.IsNull(fresh.parameter, "parameter is a reference type, which is why MenuDiff null-conditionals it");
        Assert.IsNull(fresh.icon, "the icon cases rest on this being null by default");

        var page = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
        _made.Add(page);
        Assert.IsNotNull(page.controls, "controls is pre-initialised, so the null-controls case must assign null");

        // A struct, so an ELEMENT is never null and only `name` can be — the asymmetry with subParameters.
        Assert.IsFalse(typeof(VRCExpressionsMenu.Control.Label).IsClass,
            "Label is a struct; MenuDiff coalesces its name rather than null-conditionalling the element");
        Assert.IsTrue(typeof(VRCExpressionsMenu.Control.Parameter).IsClass,
            "Parameter is a class; a null element is representable and MenuDiff coalesces it");
    }

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

    // A null controls list and an empty one are the same tree. `controls` carries a field initializer in the
    // SDK, so null is only reachable by assigning it — which a hand-edited committed asset can do.
    [Test]
    public void MenuDiff_NullControlsListEqualsEmptyList()
    {
        var a = Menu("menu");
        a.controls = null;
        Assert.IsNull(ControllerFixpoint.MenuDiff(a, Menu("menu"), "menu"));
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

    // Control.Parameter is a CLASS, so the code null-conditionals it to "". A null parameter and one named
    // "" are therefore the same control — the compiler emits the latter, a hand-edit can leave the former.
    [Test]
    public void MenuDiff_NullParameterEqualsEmptyName()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].parameter = null;
        var b = Menu("menu", Ctl("Hat", parameter: ""));
        Assert.IsNull(ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_ValueDiffers_AddressedByName()
    {
        var a = Menu("menu", Ctl("Hat", value: 1f));
        var b = Menu("menu", Ctl("Hat", value: 2f));
        Assert.AreEqual("menu 'Hat': value 1 vs 2", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // `style` is an SDK enum, not an int. The two values are interpolated into the expectation rather than
    // spelled out, so the case survives an SDK rename — what it pins is that the field is compared at all and
    // that the pair is reported committed-side first, which is the half that can actually be wrong.
    [Test]
    public void MenuDiff_StyleDiffers_AddressedByName()
    {
        var committed = default(VRCExpressionsMenu.Control.Style);
        var compiled = (VRCExpressionsMenu.Control.Style)3;
        Assert.AreNotEqual(committed, compiled, "fixture guard: the two style values must differ");

        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].style = committed;
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].style = compiled;
        Assert.AreEqual($"menu 'Hat': style {committed} vs {compiled}", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // KNOWN DEFECT, pinned as-is rather than fixed: `value` is compared with !=, so two NaNs report a
    // difference between byte-identical menus. A menu control's `value:` reaches that field through
    // AnimatorSchemaYaml's ToNumber, i.e. InferScalar's double.TryParse then a cast to float, and nothing on
    // that path rejects a non-finite — so a yaml can author `value: NaN`, commit it, and then never be
    // admitted again, because the "regenerate built/" remediation the caller appends cannot change the value.
    // Correct comparison is !x.value.Equals(y.value); this package already has the idiom in RepathClips'
    // FloatEq. Making the change is a change to the gate's comparison, so it is the board's call, not this
    // file's — see docs/local/inbox/F42.md.
    //
    // Not exact-string: float.ToString() renders NaN culture-dependently.
    [Test]
    public void MenuDiff_NaNValue_ReportsDifferenceBetweenIdenticalMenus()
    {
        var a = Menu("menu", Ctl("Hat", value: float.NaN));
        var b = Menu("menu", Ctl("Hat", value: float.NaN));
        var diff = ControllerFixpoint.MenuDiff(a, b, "menu");
        Assert.IsNotNull(diff, "NaN != NaN, so this reports a diff — a defect, not a contract");
        Assert.That(diff, Does.StartWith("menu 'Hat': value "));
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

    // Controls are compared by POSITION, so a reorder surfaces as a name mismatch at the first moved index
    // rather than as a reorder. Intended: built/ is generated and its control order is deterministic.
    [Test]
    public void MenuDiff_ReorderedControls_ReportedAsNameMismatch()
    {
        var a = Menu("menu", Ctl("A"), Ctl("B"));
        var b = Menu("menu", Ctl("B"), Ctl("A"));
        Assert.AreEqual("menu[0]: name 'A' vs 'B'", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // ── MenuDiff: labels (a STRUCT array) ───────────────────────────────────────────────────────────

    [Test]
    public void MenuDiff_LabelCountDiffers()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].labels = new[] { new VRCExpressionsMenu.Control.Label { name = "L" } };
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].labels = new VRCExpressionsMenu.Control.Label[0];
        Assert.AreEqual("menu 'Hat': 1 label(s) vs 0", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_LabelNameDiffers_AddressedByLabelIndex()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].labels = new[] {
            new VRCExpressionsMenu.Control.Label { name = "Up" },
            new VRCExpressionsMenu.Control.Label { name = "Down" } };
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].labels = new[] {
            new VRCExpressionsMenu.Control.Label { name = "Up" },
            new VRCExpressionsMenu.Control.Label { name = "Downward" } };
        Assert.AreEqual("menu 'Hat': label[1] 'Down' vs 'Downward'", ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // Control.Label is a STRUCT, so an element is never null and only its `name` can be — the reason the
    // production code null-coalesces the name instead of null-conditionalling the element.
    [Test]
    public void MenuDiff_NullLabelNameEqualsEmptyName()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].labels = new[] { new VRCExpressionsMenu.Control.Label { name = null } };
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].labels = new[] { new VRCExpressionsMenu.Control.Label { name = "" } };
        Assert.IsNull(ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    [Test]
    public void MenuDiff_NullLabelArrayEqualsEmptyArray()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].labels = null;
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].labels = new VRCExpressionsMenu.Control.Label[0];
        Assert.IsNull(ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // ── MenuDiff: subParameters (a CLASS array) ─────────────────────────────────────────────────────

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

    // Parameter IS a class here, so a null ELEMENT is representable and coalesces to "" — unlike labels.
    [Test]
    public void MenuDiff_NullSubParameterElementEqualsEmptyName()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].subParameters = new VRCExpressionsMenu.Control.Parameter[] { null };
        var b = Menu("menu", Ctl("Hat"));
        b.controls[0].subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = "" } };
        Assert.IsNull(ControllerFixpoint.MenuDiff(a, b, "menu"));
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

    [Test]
    public void MenuDiff_NestedPageNameDiffers_AddressedByParentControl()
    {
        var a = Menu("menu", Ctl("More"));
        a.controls[0].subMenu = Menu("Committed");
        var b = Menu("menu", Ctl("More"));
        b.controls[0].subMenu = Menu("Compiled");
        Assert.AreEqual("menu 'More': page name 'Committed' vs 'Compiled'",
            ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // ── MenuDiff: the icon leg ──────────────────────────────────────────────────────────────────────
    //
    // The gate cannot see this field — it compares AssetDatabase.GetAssetPath on both sides, and at the gate
    // neither entry's assets/ is loaded, so both resolve to "". ControllerFixpoint's own header declares that
    // and this file does not pretend to close it.
    //
    // Reaching the "icons differ" leg needs a REAL imported texture: GetAssetPath returns "" for any
    // non-asset object, so two CreateInstance'd textures compare equal (asserted below).

    const string IconSeed = "Assets/Agent/Scratch/F42IconSeed";
    static Texture2D _icon;

    const string IconPath = IconSeed + "/F42Icon.png";

    // The seed texture is destroyed in a finally: NUnit skips OneTimeTearDown when OneTimeSetUp throws, so an
    // encode or write failure would otherwise leak it with no later chance to clean up.
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        AnimatorTestHelpers.EnsureFolder(IconSeed);
        var tex = new Texture2D(1, 1);
        try
        {
            tex.SetPixel(0, 0, Color.magenta);
            tex.Apply();
            System.IO.File.WriteAllBytes(IconPath, tex.EncodeToPNG());
        }
        finally { UnityEngine.Object.DestroyImmediate(tex); }
        AssetDatabase.ImportAsset(IconPath);
        _icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
        Assert.IsNotNull(_icon, "icon fixture must import — the icon leg is unreachable without a real asset");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _icon = null;
        AssetDatabase.DeleteAsset(IconSeed);
    }

    // The expected path is the LITERAL fixture path, not AssetDatabase.GetAssetPath(_icon): sourcing it from
    // the same accessor the implementation calls would make this assertion agree with a rendering change
    // instead of catching one.
    [Test]
    public void MenuDiff_IconDiffers_NamesBothAssetPaths()
    {
        var a = Menu("menu", Ctl("Hat"));
        a.controls[0].icon = _icon;
        var b = Menu("menu", Ctl("Hat"));
        Assert.AreEqual($"menu 'Hat': icon '{IconPath}' vs '' — regenerate built/",
            ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // The gate's own configuration: both sides null, and they must compare EQUAL — if that regressed, every
    // entry authoring an icon would become permanently unadmittable. Named as its own case because the
    // condition deserves stating; MenuDiff_IdenticalTrees_ReturnsNull covers the same regression incidentally,
    // its controls carrying null icons too.
    [Test]
    public void MenuDiff_BothIconsNull_AreEqual()
    {
        var a = Menu("menu", Ctl("Hat"));
        var b = Menu("menu", Ctl("Hat"));
        Assert.IsNull(a.controls[0].icon, "fixture guard: default icon must be null");
        Assert.IsNull(ControllerFixpoint.MenuDiff(a, b, "menu"));
    }

    // Two DISTINCT in-memory textures compare equal, because neither has an asset path. This is why the gate
    // is blind here even when both sides genuinely carry an icon.
    [Test]
    public void MenuDiff_TwoUnimportedIcons_CompareEqual_TheGateBlindness()
    {
        var t1 = new Texture2D(1, 1);
        var t2 = new Texture2D(1, 1);
        try
        {
            var a = Menu("menu", Ctl("Hat"));
            a.controls[0].icon = t1;
            var b = Menu("menu", Ctl("Hat"));
            b.controls[0].icon = t2;
            Assert.IsNull(ControllerFixpoint.MenuDiff(a, b, "menu"),
                "both resolve to \"\" — the declared gate-level blind spot, asserted so it stays declared");
        }
        finally { UnityEngine.Object.DestroyImmediate(t1); UnityEngine.Object.DestroyImmediate(t2); }
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

    [Test]
    public void GateExit_BothFailed_IsOne()
        => Assert.AreEqual(1, ControllerFixpoint.GateExit(3, 2));

    // ── ForeignProjectPathLines: the committed-prefab provenance predicate ──────────────────────────
    //
    // Pure text in, offender handles out — no AssetDatabase, no live object, so the whole predicate is
    // unit-reachable even though its caller (CheckPrefabIntegrity) is boundary-bound and stays out of this
    // file. What is NOT covered here is the wiring: that CheckPrefabIntegrity calls this and folds its count
    // into offenderCount sits behind that same boundary, exactly like the missing-script and anchor-seam
    // checks beside it.

    [Test]
    public void ForeignProjectPathLines_CleanPrefabText_IsEmpty()
        => Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines(
            "  m_Name: Thing\n  id: 8f0a|Packages/com.example.patterns/entry/assets/Thing.fbx\n"));

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

    // CRLF text must yield the same handle as LF text — a prefab committed through a Windows checkout is
    // the common case, and a stray \r on the end of the echoed line would break the exact-string above.
    [Test]
    public void ForeignProjectPathLines_CrlfTextYieldsTheSameHandle()
        => CollectionAssert.AreEqual(
            new[] { "line 2: id: 8f0a|Assets/Somewhere/Thing.fbx" },
            ControllerFixpoint.ForeignProjectPathLines(
                "  m_Name: Thing\r\n  id: 8f0a|Assets/Somewhere/Thing.fbx\r\n"));

    // Judgment-free: the substring is the WHOLE predicate. No path-shape parse, no allowlist — a bare
    // mention anywhere in the text is the defect, because a VPM package resolves nothing under Assets/.
    [Test]
    public void ForeignProjectPathLines_MatchesAnywhereInTheLine_NotOnlyInAnIdField()
        => CollectionAssert.AreEqual(
            new[] { "line 1: - {fileID: 0, guid: dead, path: Assets/X}" },
            ControllerFixpoint.ForeignProjectPathLines("- {fileID: 0, guid: dead, path: Assets/X}"));

    // Case-sensitive by construction (Ordinal): Unity's folder IS `Assets/`, and a case-insensitive match
    // would start hitting prose in a comment. Pinned so the comparison choice is a deliberate one.
    [Test]
    public void ForeignProjectPathLines_IsCaseSensitive()
        => Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines("path: assets/X"));

    // A trailing-slash-less `Assets` is not a path and does not fire — the slash is part of the predicate.
    [Test]
    public void ForeignProjectPathLines_BareWordAssets_DoesNotFire()
        => Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines("m_Name: MyAssets"));

    // One pathological line cannot swamp the gate's single-line FAIL message.
    [Test]
    public void ForeignProjectPathLines_LongLineIsTruncated()
    {
        var hit = ControllerFixpoint.ForeignProjectPathLines("Assets/" + new string('x', 500))[0];
        Assert.AreEqual("line 1: Assets/" + new string('x', 153) + "…", hit);
    }

    [Test]
    public void ForeignProjectPathLines_EmptyText_IsEmpty()
        => Assert.IsEmpty(ControllerFixpoint.ForeignProjectPathLines(""));

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

    [Test]
    public void MenuBeside_BareFilenameHasNoDirectoryPrefix()
        => Assert.AreEqual("FX_Menu.asset", ControllerFixpoint.MenuBeside("FX.controller"));

    // A dot in a DIRECTORY name must not be mistaken for the controller's extension.
    [Test]
    public void MenuBeside_DottedDirectoryNameSurvives()
        => Assert.AreEqual("a/v1.0/FX_Menu.asset", ControllerFixpoint.MenuBeside("a/v1.0/FX.controller"));

    // ── ParseControllerName: the agreeing cases ────────────────────────────────────────────────────

    [Test]
    public void ParseControllerName_PlainScalar()
        => Assert.AreEqual("FX", ControllerFixpoint.ParseControllerName(Yaml("# header\ncontroller: FX\n")));

    [Test]
    public void ParseControllerName_StripsTrailingComment()
        => Assert.AreEqual("FX", ControllerFixpoint.ParseControllerName(Yaml("controller: FX # the fx layer\n")));

    [Test]
    public void ParseControllerName_NoControllerKey_ReturnsNull()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("clips:\n  - name: a\n")));

    [Test]
    public void ParseControllerName_BareKeyWithNoValue_ReturnsNull()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("controller:\n")));

    [Test]
    public void ParseControllerName_ValueIsOnlyAComment_ReturnsNull()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("controller: # TODO\n")));

    // Prefix match is on "controller:", so a longer key is not a near miss.
    [Test]
    public void ParseControllerName_LongerKeyIsNotAMatch()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("controllerFoo: FX\n")));

    [Test]
    public void ParseControllerName_EmptyFile_ReturnsNull()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("")));

    // ── ParseControllerName: DIVERGENCES from the real parser ──────────────────────────────────────
    //
    // This scanner is not AnimatorSchemaYaml. Where the two disagree, the gate resolves a name the compiler
    // never writes, so built/<name>.controller is absent. Two of these are SILENT (the null cases below).
    // Every one is asserted as current behavior, named as a divergence, and reported in
    // docs/local/inbox/F42.md — changing the scanner is a gate decision, not this file's.
    //
    // None is reachable from the library as committed: all 21 controller: lines are unquoted plain scalars
    // with no comment, no indent, no duplicate, and no space before the colon. These are armed for the next
    // entry, which is the gate's actual subject.
    //
    // SCOPE OF THE COMPARISON: each case below is about how the two readers resolve the controller KEY and its
    // value. The fixtures are bare `controller:` lines, which AnimatorSchemaYaml would refuse outright as
    // documents — Bind requires `basis:` — so "the real parser binds this" below always means the key, never
    // the whole file. The divergence is real for a well-formed library document, where the only difference is
    // the spelling of this one line.

    // Real parser strips the quotes and the compiler writes FX.controller; the gate keeps them, so it looks
    // for a filename containing '"'. That does not merely miss — it is an illegal Windows path, and RunGate
    // feeds it to Path.Combine outside any try, so the run DIES with no [gate] line. See the Interlock test
    // at the bottom of this file, which asserts that; the crash is why this row leads the F42 inbox report.
    [Test]
    public void ParseControllerName_QuotedScalar_KeepsQuotes_Divergence()
        => Assert.AreEqual("\"FX\"", ControllerFixpoint.ParseControllerName(Yaml("controller: \"FX\"\n")),
            "DIVERGENCE: the real parser strips quotes; the gate keeps them and then cannot build a legal path");

    // The real parser needs whitespace before a '#' for it to start a comment, so the true name is FX#odd.
    [Test]
    public void ParseControllerName_HashWithoutLeadingSpace_TruncatesValue_Divergence()
        => Assert.AreEqual("FX", ControllerFixpoint.ParseControllerName(Yaml("controller: FX#odd\n")),
            "DIVERGENCE: the real parser keeps FX#odd — a '#' only opens a comment after whitespace");

    // SILENT: null means RunGate logs SKIP "not a controller document" and never gates the file at all —
    // even for a GUID-consumer entry, because the guidConsumer check sits after the null branch.
    [Test]
    public void ParseControllerName_SpaceBeforeColon_ReturnsNull_SilentDivergence()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("controller : FX\n")),
            "SILENT DIVERGENCE: the real parser trims the key and binds it; the gate sees no controller key");

    // SILENT: same shape — the real parser strips quotes around a KEY too.
    [Test]
    public void ParseControllerName_QuotedKey_ReturnsNull_SilentDivergence()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("\"controller\": FX\n")),
            "SILENT DIVERGENCE: the real parser unquotes the key and binds it; the gate sees no controller key");

    // The real parser REFUSES a duplicate key outright. The gate takes the first and moves on — harmless
    // only because the compile that follows fails loudly on the same file.
    [Test]
    public void ParseControllerName_DuplicateKey_TakesFirst_Divergence()
        => Assert.AreEqual("A", ControllerFixpoint.ParseControllerName(Yaml("controller: A\ncontroller: B\n")),
            "DIVERGENCE: the real parser throws duplicate key; the gate silently takes the first");

    // A non-string scalar: the real parser infers bool/long and then refuses it as a controller name.
    [Test]
    public void ParseControllerName_BooleanLikeScalar_ReturnedAsText_Divergence()
        => Assert.AreEqual("on", ControllerFixpoint.ParseControllerName(Yaml("controller: on\n")),
            "DIVERGENCE: the real parser infers a bool here and refuses it; the gate returns the text");

    // Indentation is invisible to a StartsWith scan. Normally harmless (an indented key is not top-level to
    // the real parser either) — but the real parser takes the FIRST line's indent as the document root, so a
    // uniformly indented document does have this as its top-level key while the gate sees nothing.
    [Test]
    public void ParseControllerName_IndentedKey_ReturnsNull_DivergesOnUniformlyIndentedDocs()
        => Assert.IsNull(ControllerFixpoint.ParseControllerName(Yaml("  controller: FX\n")),
            "DIVERGENCE (degenerate): a uniformly indented document has this as its top-level key");

    // ── IsGuidConsumer: the tier derivation ────────────────────────────────────────────────────────
    //
    // A false answer here means a Module whose built controller went missing passes as a Pattern, so each
    // term of the || matters independently.

    [Test]
    public void IsGuidConsumer_YamlOnly_IsPattern()
    {
        File_(_tmp, "controller.yaml", "controller: FX\n");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

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

    // The .meta filter is OrdinalIgnoreCase, so casing does not smuggle residue past it.
    [Test]
    public void IsGuidConsumer_MetaFilterIsCaseInsensitive()
    {
        File_(Dir("assets"), "mesh.fbx.META");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

    [Test]
    public void IsGuidConsumer_EmptyAssetsDir_IsNotAConsumer()
    {
        Dir("assets");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

    [Test]
    public void IsGuidConsumer_AllThreeShapes()
    {
        File_(_tmp, "entry.prefab");
        Dir("built");
        File_(Dir("assets"), "mesh.fbx");
        Assert.IsTrue(ControllerFixpoint.IsGuidConsumer(_tmp));
    }

    // KNOWN GAP, pinned: assets/ is scanned TOP-LEVEL ONLY, while CheckPrefabIntegrity walks the same tree
    // with AllDirectories. An entry whose only shipped content sits in assets/<subdir>/ therefore reads as a
    // Pattern and loses the built-controller requirement. The live library is flat today; this is armed for
    // the next entry. Reported in docs/local/inbox/F42.md.
    [Test]
    public void IsGuidConsumer_AssetsContentOneLevelDown_IsNotSeen_KnownGap()
    {
        File_(Dir("assets", "Textures"), "skin.png");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp),
            "KNOWN GAP: assets/ is scanned top-level only, so nested content does not make a consumer");
    }

    // KNOWN GAP, pinned: `built` as a FILE satisfies neither Directory.Exists nor File.Exists on any
    // built/<name>.controller, so the whole built/ regime switches off with no diagnostic.
    [Test]
    public void IsGuidConsumer_BuiltIsAFileNotADirectory_IsNotAConsumer_KnownGap()
    {
        File_(_tmp, "built");
        Assert.IsFalse(ControllerFixpoint.IsGuidConsumer(_tmp),
            "KNOWN GAP: a file named 'built' disables the built/ requirement silently");
    }

    // The helper does not swallow a missing dir — RunGate only passes directories it just enumerated, so the
    // guard belongs at that caller. Pinned so a future 'defensive' catch here is a deliberate change.
    [Test]
    public void IsGuidConsumer_MissingEntryDir_Throws()
        => Assert.Throws<DirectoryNotFoundException>(
            () => ControllerFixpoint.IsGuidConsumer(Path.Combine(_tmp, "nope")));

    // ── Orphan detection ───────────────────────────────────────────────────────────────────────────

    [Test]
    public void OrphanControllers_ClaimedController_IsNotAnOrphan()
    {
        var built = Dir("built");
        File_(built, "FX.controller");
        Assert.IsEmpty(ControllerFixpoint.OrphanControllers(built, new[] { "FX" }).ToList());
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

    // The comparer is ORDINAL and lives inside the helper, so this pins the production choice rather than
    // the test's own — the reason `claimed` is passed as a bare sequence.
    [Test]
    public void OrphanControllers_ClaimIsCaseSensitive()
    {
        var built = Dir("built");
        File_(built, "FX.controller");
        CollectionAssert.AreEquivalent(new[] { "FX" },
            ControllerFixpoint.OrphanControllers(built, new[] { "Fx" }).ToList(),
            "claimed is compared Ordinal — 'Fx' does not claim FX.controller");
    }

    [Test]
    public void OrphanControllers_IgnoresNonControllerFiles()
    {
        var built = Dir("built");
        File_(built, "FX.controller");
        File_(built, "FX_Parameters.asset");
        File_(built, "FX.controller.meta");
        Assert.IsEmpty(ControllerFixpoint.OrphanControllers(built, new[] { "FX" }).ToList());
    }

    [Test]
    public void OrphanControllers_MissingBuiltDir_Throws()
        => Assert.Throws<DirectoryNotFoundException>(
            () => ControllerFixpoint.OrphanControllers(Path.Combine(_tmp, "built"), new[] { "FX" }).ToList());

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

    // Degenerate name: "_Menu.asset" derives an EMPTY controller name. Pinned because the Substring
    // arithmetic is what would throw if the glob ever matched something shorter.
    [Test]
    public void OrphanMenus_FileNamedExactlyMenuAsset_DerivesEmptyName_WithoutThrowing()
    {
        var built = Dir("built");
        File_(built, "_Menu.asset");
        CollectionAssert.AreEquivalent(new[] { "_Menu" },
            ControllerFixpoint.OrphanMenus(built, new[] { "FX" }).ToList());
    }

    [Test]
    public void OrphanMenus_ClaimIsCaseSensitive()
    {
        var built = Dir("built");
        File_(built, "FX_Menu.asset");
        CollectionAssert.AreEquivalent(new[] { "FX_Menu" },
            ControllerFixpoint.OrphanMenus(built, new[] { "Fx" }).ToList());
    }

    [Test]
    public void OrphanMenus_IgnoresControllersAndParamAssets()
    {
        var built = Dir("built");
        File_(built, "FX.controller");
        File_(built, "FX_Parameters.asset");
        Assert.IsEmpty(ControllerFixpoint.OrphanMenus(built, new List<string>()).ToList());
    }

    [Test]
    public void OrphanMenus_MissingBuiltDir_Throws()
        => Assert.Throws<DirectoryNotFoundException>(
            () => ControllerFixpoint.OrphanMenus(Path.Combine(_tmp, "built"), new[] { "FX" }).ToList());

    // ── The interlock ──────────────────────────────────────────────────────────────────────────────
    //
    // What makes the §divergence table above survivable rather than a live false-negative. Two branches, and
    // only one of them is loud.

    // The quoted-scalar divergence is WORSE than a misleading FAIL: the parsed name still carries its quote
    // characters, which are illegal in a Windows path, so RunGate's own
    //     Path.Combine(builtDir, name + ".controller")
    // throws ArgumentException before any comparison happens. That call sits OUTSIDE Check's try, so the
    // batchmode gate dies mid-run with no [gate] line and an exit code nobody can attribute.
    //
    // This case asserts the throw at the same expression RunGate uses. It is the reason the quoted-scalar row
    // is reported in docs/local/inbox/F42.md as a crash rather than a diagnostic defect.
    [Test]
    public void Interlock_QuotedNameProducesAnIllegalPath_SoTheGateCrashesRatherThanFails()
    {
        var yaml = File_(_tmp, "controller.yaml", "controller: \"FX\"\n");
        var built = Dir("built");
        File_(built, "FX.controller"); // what the COMPILER writes

        var parsed = ControllerFixpoint.ParseControllerName(yaml);
        Assert.AreEqual("\"FX\"", parsed);
        Assert.IsTrue(ControllerFixpoint.IsGuidConsumer(_tmp),
            "the entry is a GUID-consumer, so the document would be held to its built/ controller");

        // The stable, runtime-independent form of the defect: the parsed name cannot be part of a legal path.
        Assert.GreaterOrEqual(parsed.IndexOfAny(Path.GetInvalidPathChars()), 0,
            "the parsed name carries a character no path may contain");
        // And the consequence on THIS runtime. Path.Combine's invalid-char ArgumentException exists on the
        // Mono profile Unity 2022.3 ships and was removed in .NET Core 2.1+, so if this assert ever fails on a
        // newer runtime the defect has changed shape, not disappeared — the illegal name above still is one.
        Assert.Throws<ArgumentException>(() => Path.Combine(built, parsed + ".controller"),
            "RunGate builds the built/ path exactly this way, outside any try — a quoted name kills the run");
    }

    // Silent branch: the name parses NULL, so RunGate's SKIP fires BEFORE the guidConsumer check and the
    // document is never gated — GUID-consumer or not. The orphan loop is the only remaining net, and it
    // catches this only when the built controller still exists; if built/ is what went missing, nothing does.
    [Test]
    public void Interlock_NullNameSkipsBeforeTheGuidConsumerCheck_SoDivergenceIsSilent()
    {
        var yaml = File_(_tmp, "controller.yaml", "controller : FX\n");
        var built = Dir("built");

        Assert.IsNull(ControllerFixpoint.ParseControllerName(yaml),
            "the real parser binds this key; the gate does not see a controller key at all");
        Assert.IsTrue(ControllerFixpoint.IsGuidConsumer(_tmp),
            "the entry IS a GUID-consumer — and it does not help, because SKIP fires first");
        Assert.IsEmpty(ControllerFixpoint.OrphanControllers(built, new List<string>()).ToList(),
            "built/ is empty, so even the orphan net finds nothing: the document is ungated in silence");
    }
}
