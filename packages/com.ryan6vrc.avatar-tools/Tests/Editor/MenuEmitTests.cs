using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.ScriptableObjects;

// Behavioral tests for the emitted VRCExpressionsMenu: the asset CompileController persists beside the
// controller, its sub-asset tree, and the lifecycle rules (GUID held across recompiles, stale asset swept).
// These need the SDK types and the AssetDatabase; the parse/validate half is MenuSchemaTests. Run headless
// via tools/run-editmode-tests.ps1; not via MCP run_tests — wrong venue. See docs/verify.md.
public class MenuEmitTests
{
    private const string TestRoot = "Assets/Agent/Scratch/menu_tests";
    private const string OutDir = TestRoot + "/out";
    private string _srcPath;

    private const string Head = @"schema: 1
controller: M_Fx
basis: avatar-root
parameters:
  Enable: bool
  Sat: float
layers: []
";

    private string MenuPath => OutDir + "/M_Fx_Menu.asset";

    [SetUp]
    public void SetUp()
    {
        AnimatorTestHelpers.EnsureFolder(TestRoot);
        _srcPath = TestRoot + "/M_Fx.yaml";
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(TestRoot);

    private string Compile(string menuBlock)
    {
        File.WriteAllText(_srcPath, Head + menuBlock);
        return CompileController.Compile(_srcPath, OutDir);
    }

    private VRCExpressionsMenu Load() => AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(MenuPath);

    [Test]
    public void NoMenuBlock_WritesNoAsset()
    {
        StringAssert.Contains("=> OK", Compile(""));
        Assert.IsNull(Load());
    }

    [Test]
    public void ControlTypesLandAsTheSerializedNumbers()
    {
        // The docs/menus.md trap: the serialized ints are NOT the enum ordinals. Emitting through the typed
        // field makes the mistake unrepresentable, and this asserts the bytes VRChat actually reads.
        Compile(@"menu:
  - button: B
    param: Enable
  - toggle: T
    param: Enable
  - radial: R
    param: Sat
  - submenu: S
    controls:
      - toggle: Inner
        param: Enable
");
        var text = File.ReadAllText(MenuPath);
        StringAssert.Contains("type: 101", text);   // Button
        StringAssert.Contains("type: 102", text);   // Toggle
        StringAssert.Contains("type: 203", text);   // RadialPuppet
        StringAssert.Contains("type: 103", text);   // SubMenu
    }

    [Test]
    public void RadialParamRidesSubParameters_NotParameter()
    {
        Compile(@"menu:
  - radial: R
    param: Sat
");
        var c = Load().controls.Single();
        Assert.AreEqual("", c.parameter.name, "a radial's `parameter` is the SDK's optional on-open bool, unused here");
        Assert.AreEqual("Sat", c.subParameters.Single().name);
    }

    [Test]
    public void SubMenuPagesAreSubAssetsOfTheOneFile()
    {
        // The whole tree travels under one GUID, so a consumer references only the root and no page can be
        // orphaned into a loose asset nobody points at.
        Compile(@"menu:
  - submenu: Colors
    controls:
      - toggle: Red
        param: Enable
");
        var root = Load();
        var child = root.controls.Single().subMenu;
        Assert.IsNotNull(child);
        Assert.AreEqual(MenuPath, AssetDatabase.GetAssetPath(child), "a sub-menu page must live inside the root asset");
        Assert.AreEqual("Red", child.controls.Single().name);
    }

    [Test]
    public void RecompileHoldsTheGuid()
    {
        // A consumer's FullController `menus:` row references this file by GUID; a delete+recreate would
        // silently break every install.
        Compile(@"menu:
  - toggle: A
    param: Enable
");
        var guid = AssetDatabase.AssetPathToGUID(MenuPath);
        Compile(@"menu:
  - toggle: A
    param: Enable
  - toggle: B
    param: Enable
");
        Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(MenuPath));
        Assert.AreEqual(2, Load().controls.Count);
    }

    [Test]
    public void RecompileSweepsStaleSubMenuPages()
    {
        // Without the wholesale churn, a renamed or deleted sub-menu lingers inside the file forever.
        Compile(@"menu:
  - submenu: Gone
    controls:
      - toggle: A
        param: Enable
  - submenu: Kept
    controls:
      - toggle: B
        param: Enable
");
        Assert.AreEqual(3, AssetDatabase.LoadAllAssetsAtPath(MenuPath).Length, "root + 2 pages");

        Compile(@"menu:
  - submenu: Kept
    controls:
      - toggle: B
        param: Enable
");
        var all = AssetDatabase.LoadAllAssetsAtPath(MenuPath);
        Assert.AreEqual(2, all.Length, "root + 1 page — the dropped page must not linger");
        CollectionAssert.DoesNotContain(all.Select(a => a.name).ToArray(), "M_Fx_Menu_Gone");
    }

    [Test]
    public void RecompileReportsTheMenuInTheSummary()
    {
        // The in-place branch destroys the in-memory root, and a destroyed UnityEngine.Object compares
        // equal to null — so the summary has to key off a flag captured before that, or every recompile
        // silently under-reports a menu that was in fact written.
        Compile(@"menu:
  - toggle: A
    param: Enable
");
        var second = Compile(@"menu:
  - toggle: A
    param: Enable
");
        StringAssert.Contains("menu=1c/1p", second);
    }

    [Test]
    public void InPlaceWrite_SyncsTheObjectName()
    {
        // An entry moving its menu into built/ renames the file; the in-place write is the path that skips
        // Unity's filename-driven rename, so m_Name would otherwise stay stale forever.
        Compile(@"menu:
  - toggle: A
    param: Enable
");
        Compile(@"menu:
  - toggle: A
    param: Enable
  - toggle: B
    param: Enable
");
        Assert.AreEqual("M_Fx_Menu", Load().name);
    }

    [Test]
    public void DroppingTheMenuBlock_DeletesTheAsset()
    {
        Compile(@"menu:
  - toggle: A
    param: Enable
");
        Assert.IsNotNull(Load());
        Compile("");
        Assert.IsNull(Load(), "a menu asset must not outlive the block that declared it");
    }

    [Test]
    public void ControlOnAVrcBuiltIn_FailsTheCompile()
    {
        // EmitVrcParameters excludes built-ins from the params asset exactly as it excludes scratch params,
        // so VRChat never sees the name and the control is inert. SchemaValidation catches the scratch half
        // but is System.*-only and cannot reach ControllerRules, so this half is refused at emit.
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*emit:.*IsLocal.*=> FAIL"));
        File.WriteAllText(_srcPath, @"schema: 1
controller: M_Fx
basis: avatar-root
parameters:
  IsLocal: bool
layers: []
menu:
  - toggle: Local
    param: IsLocal
");
        var msg = CompileController.Compile(_srcPath, OutDir);
        StringAssert.Contains("FAIL", msg);
        Assert.IsNull(Load());
    }

    [Test]
    public void FailedCompileDoesNotDestroyAPreExistingMenu()
    {
        // "Nothing written on failure" covers side assets. A FRESH compile over a folder that already holds
        // a menu skips ProofCompile — there is no prior controller to protect — so persisting before the
        // lint would delete an asset CleanupAfterLint cannot restore.
        Compile(@"menu:
  - toggle: A
    param: Enable
");
        var before = File.ReadAllText(MenuPath);
        AssetDatabase.DeleteAsset(OutDir + "/M_Fx.controller");   // controller gone, menu stays

        // A document whose graph fails the lint: an unconditional state hop with no exit time.
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*graph lint.*=> FAIL"));
        File.WriteAllText(_srcPath, @"schema: 1
controller: M_Fx
basis: avatar-root
parameters:
  Enable: bool
layers:
  - name: L
    states:
      A:
        motion: ~
        transitions:
          - { to: B, when: [] }
      B:
        motion: ~
    default: A
menu:
  - toggle: A
    param: Enable
");
        StringAssert.Contains("FAIL", CompileController.Compile(_srcPath, OutDir));
        Assert.IsTrue(File.Exists(MenuPath), "a failed compile must not delete the pre-existing menu asset");
        Assert.AreEqual(before, File.ReadAllText(MenuPath), "nor overwrite it");
    }

    [Test]
    public void OverflowingPage_FailsTheCompileAndWritesNothing()
    {
        var yaml = "menu:\n" + string.Concat(Enumerable.Range(0, MenuLimits.MaxControlsPerMenu + 1)
            .Select(i => $"  - toggle: T{i}\n    param: Enable\n"));
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*validation failed.*menu-overflow.*=> FAIL"));
        var msg = Compile(yaml);
        StringAssert.Contains("FAIL", msg);
        StringAssert.Contains("menu-overflow", msg);
        Assert.IsNull(Load());
    }

    // ---------- icons ----------

    // A real imported 2x2 PNG under TestRoot. Encoded rather than checked in: the emit path needs a genuine
    // Texture2D the AssetDatabase imported, and a fixture binary would be one more thing to keep alive.
    private string MakeIcon(string projectPath)
    {
        AnimatorTestHelpers.EnsureFolder(Path.GetDirectoryName(projectPath).Replace('\\', '/'));
        var tex = new Texture2D(2, 2);
        tex.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
        tex.Apply();
        File.WriteAllBytes(projectPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(projectPath, ImportAssetOptions.ForceSynchronousImport);
        return projectPath;
    }

    // The RunLog body behind a compile message ("… | log=<path>"), where the advisories live.
    private static string RunLogBody(string compileMessage)
    {
        int i = compileMessage.LastIndexOf("| log=", System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "compile message carries no log path: " + compileMessage);
        return File.ReadAllText(compileMessage.Substring(i + "| log=".Length).Trim());
    }

    [Test]
    public void DocumentRelativeIcon_Resolves()
    {
        // The portable form, and the one a library entry uses: the icon sits beside the yaml, named without
        // reference to where the pair is mounted.
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        StringAssert.Contains("=> OK", Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: assets/Knob.png\n"));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
    }

    [Test]
    public void ProjectPathIcon_Resolves()
    {
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        StringAssert.Contains("=> OK", Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: " + icon + "\n"));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
    }

    [Test]
    public void IconOnANestedControl_Resolves()
    {
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        Compile(@"menu:
  - submenu: S
    icon: assets/Knob.png
    controls:
      - toggle: Inner
        param: Enable
        icon: assets/Knob.png
");
        var outer = Load().controls.Single();
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(outer.icon));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(outer.subMenu.controls.Single().icon));
    }

    [Test]
    public void MissingIcon_FailsTheCompileAndWritesNothing()
    {
        // The authoring mistake: a path that resolves to nothing at all. Fatal, deliberately NOT degraded —
        // there is no authored marker distinguishing an intended-dangling icon from a typo, and a silently
        // icon-less control is exactly what the author would not notice.
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*icon not found.*=> FAIL"));
        var msg = Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: assets/Nope.png\n");
        StringAssert.Contains("FAIL", msg);
        StringAssert.Contains("icon not found", msg);
        Assert.IsNull(Load());
    }

    [Test]
    public void IconThatIsNotATexture_FailsByName()
    {
        // Present, in the project, imported — but not an image. Distinguished from "not found" because the
        // repair differs: this one is a real file at a wrong path. The first compile is what makes the
        // controller exist as an imported non-texture asset to point at.
        StringAssert.Contains("=> OK", Compile("menu:\n  - toggle: T\n    param: Enable\n"));
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*did not load as a Texture2D.*=> FAIL"));
        var msg = Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: " + OutDir + "/M_Fx.controller\n");
        StringAssert.Contains("FAIL", msg);
        StringAssert.Contains("did not load as a Texture2D", msg);
    }

    [Test]
    public void DocumentRelativeIconResolvesInAssetSpace_IncludingDotDot()
    {
        // The path arithmetic must stay in ASSET space. Doing it on the filesystem happens to work under
        // Assets/ and silently breaks for a `file:`-mounted package, whose Packages/… path names no folder
        // on disk — measured: every icon in a mounted vrc-patterns emitted null before this was fixed. A
        // `..` hop is the cheapest way to assert the collapse happens without leaving that space.
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        AnimatorTestHelpers.EnsureFolder(TestRoot + "/sub");
        var nested = TestRoot + "/sub/M_Fx.yaml";
        File.WriteAllText(nested, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: ../assets/Knob.png\n");
        StringAssert.Contains("=> OK", CompileController.Compile(nested, OutDir));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
    }

    [Test]
    public void IconOutsideTheProject_EmitsNullAndAdvises()
    {
        // THE GATE CASE, reproduced exactly: a yaml compiled from a filesystem path outside the project,
        // with its icon beside it, into a host whose AssetDatabase cannot see either. The compile must
        // SUCCEED with a null icon — failing here would make `icon:` unauthorable by the vrc-patterns
        // library, whose gate compiles every entry this way (ControllerFixpoint.CompileToTemp).
        var outside = Path.Combine(Path.GetTempPath(), "f11_icon_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(outside, "assets"));
        try
        {
            var tex = new Texture2D(2, 2);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(outside, "assets", "Knob.png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            var yaml = Path.Combine(outside, "M_Fx.yaml");
            File.WriteAllText(yaml, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: assets/Knob.png\n");

            var msg = CompileController.Compile(yaml, OutDir);
            StringAssert.Contains("=> OK", msg);
            Assert.IsNull(Load().controls.Single().icon, "an unreachable icon emits as null, not as a failure");
            StringAssert.Contains("not in the project's AssetDatabase", RunLogBody(msg));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Test]
    public void OutOfProjectCompile_NeverFails_WhicheverSpelling()
    {
        // The regime is decided by the DOCUMENT's location, so every icon spelling must degrade together.
        // Keying on the icon's spelling instead made a project-spelled icon throw here — which would fail
        // the whole gate run for an entry that compiles perfectly well in a project. The lowercase
        // `assets/…` in the test above sidesteps that branch entirely, so it could not have caught it.
        var outside = Path.Combine(Path.GetTempPath(), "f11_spell_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(outside);
        try
        {
            foreach (var spelling in new[] { "Assets/NotHere/Knob.png", "Packages/com.nobody.nothing/Knob.png", "assets/Absent.png" })
            {
                var yaml = Path.Combine(outside, "M_Fx.yaml");
                File.WriteAllText(yaml, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: " + spelling + "\n");
                var msg = CompileController.Compile(yaml, OutDir);
                StringAssert.Contains("=> OK", msg, "spelling must not decide the regime: " + spelling);
                Assert.IsNull(Load().controls.Single().icon, spelling);
            }
        }
        finally { Directory.Delete(outside, true); }
    }

    [Test]
    public void InProjectDocumentGivenAnAbsolutePath_StillResolves()
    {
        // Every interactive door hands Compile an ABSOLUTE path (the menu door calls Path.GetFullPath;
        // OpenFilePanel returns absolute). Testing only the `Assets/…` spelling hid a total failure of the
        // field there: the document read as out-of-project, and every icon silently emitted null.
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        File.WriteAllText(_srcPath, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: assets/Knob.png\n");
        var msg = CompileController.Compile(Path.GetFullPath(_srcPath), OutDir);
        StringAssert.Contains("=> OK", msg);
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
        StringAssert.Contains("_(none)_", RunLogBody(msg).Split(new[] { "## Compile advisory: unadjudicated menu icons" }, System.StringSplitOptions.None)[1]
            .Split(new[] { "\n## " }, System.StringSplitOptions.None)[0]);
    }

    [Test]
    public void BackslashSeparatorsResolve_LikeForwardOnes()
    {
        // A Windows-spelled path must not behave differently in-project (where it would fail an asset-space
        // lookup) than at the gate (where Path.Combine accepts it).
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        StringAssert.Contains("=> OK", Compile(@"menu:
  - toggle: T
    param: Enable
    icon: assets\Knob.png
"));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
    }

    [Test]
    public void DotSegmentsCollapse_InBothSpellings()
    {
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        StringAssert.Contains("=> OK", Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: assets/../assets/Knob.png\n"));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));

        StringAssert.Contains("=> OK", Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: "
            + TestRoot + "/assets/../assets/Knob.png\n"));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
    }

    [Test]
    public void NoIcons_AdvisorySectionReadsNone()
    {
        // The section is unconditional, so a document with no icons must still read cleanly rather than
        // suggest something was skipped.
        StringAssert.Contains("## Compile advisory: unadjudicated menu icons\n\n_(none)_",
            RunLogBody(Compile("menu:\n  - toggle: T\n    param: Enable\n")));
    }

    [Test]
    public void SdkControlCapMatchesTheEchoedConstant()
    {
        // SchemaValidation is System.*-only and mirrors the cap; this is the assertion that keeps the echo
        // honest if the SDK ever moves it (ControllerEmit throws on the same mismatch during a real emit).
        Assert.AreEqual(VRCExpressionsMenu.MAX_CONTROLS, MenuLimits.MaxControlsPerMenu);
    }
}
