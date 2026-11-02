using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.ScriptableObjects;

// The emit half of the `menu:` surface, the VRCExpressionsMenu asset CompileController persists beside the
// controller. The parse and validate half is MenuSchemaTests.
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
        return CompileController.Run(_srcPath, OutDir);
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
        // The serialized numbers VRChat reads, which are not the enum's ordinals (docs/menus.md).
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
        // The in-place branch destroys the in-memory root, which then compares equal to null, so a summary
        // reading it after that under-reports the menu it wrote.
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
        // The in-place write skips Unity's filename-driven rename, so a main asset renamed on disk keeps its
        // old m_Name unless the write syncs it.
        Compile(@"menu:
  - toggle: A
    param: Enable
");
        var stale = Load();
        stale.name = "Stale";
        EditorUtility.SetDirty(stale);
        AssetDatabase.SaveAssetIfDirty(stale);
        Assert.AreEqual("Stale", Load().name, "the fixture must reach the recompile with the old name");
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
        // SchemaValidation is System.*-only and cannot reach ControllerRules, so this refusal lives at emit.
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
        var msg = CompileController.Run(_srcPath, OutDir);
        StringAssert.Contains("FAIL", msg);
        Assert.IsNull(Load());
    }

    [Test]
    public void FailedCompileDoesNotDestroyAPreExistingMenu()
    {
        // With its controller gone, a compile skips ProofCompile, so persisting before the lint would delete
        // a menu CleanupAfterLint cannot restore.
        Compile(@"menu:
  - toggle: A
    param: Enable
");
        var before = File.ReadAllText(MenuPath);
        AssetDatabase.DeleteAsset(OutDir + "/M_Fx.controller");

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
        StringAssert.Contains("FAIL", CompileController.Run(_srcPath, OutDir));
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

    // The RunLog body behind a compile message, where the advisories live.
    private static string RunLogBody(string compileMessage)
    {
        int i = compileMessage.LastIndexOf("| log=", System.StringComparison.Ordinal);
        Assert.Greater(i, -1, "compile message carries no log path: " + compileMessage);
        return File.ReadAllText(compileMessage.Substring(i + "| log=".Length).Trim());
    }

    [Test]
    public void DocumentRelativeIcon_Resolves()
    {
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
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*icon not found.*=> FAIL"));
        var msg = Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: assets/Nope.png\n");
        StringAssert.Contains("FAIL", msg);
        StringAssert.Contains("icon not found", msg);
        Assert.IsNull(Load());
    }

    [Test]
    public void IconThatIsNotATexture_FailsByName()
    {
        // The first compile makes the controller exist as an imported non-texture asset to point at.
        StringAssert.Contains("=> OK", Compile("menu:\n  - toggle: T\n    param: Enable\n"));
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*did not load as a Texture2D.*=> FAIL"));
        var msg = Compile("menu:\n  - toggle: T\n    param: Enable\n    icon: " + OutDir + "/M_Fx.controller\n");
        StringAssert.Contains("FAIL", msg);
        StringAssert.Contains("did not load as a Texture2D", msg);
    }

    [Test]
    public void DocumentRelativeIconResolvesInAssetSpace_IncludingDotDot()
    {
        // A `file:`-mounted package's Packages/… path names no folder on disk, so the path arithmetic must stay
        // in asset space; a `..` hop is the cheapest way to assert the collapse happens there.
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        AnimatorTestHelpers.EnsureFolder(TestRoot + "/sub");
        var nested = TestRoot + "/sub/M_Fx.yaml";
        File.WriteAllText(nested, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: ../assets/Knob.png\n");
        StringAssert.Contains("=> OK", CompileController.Run(nested, OutDir));
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
    }

    [Test]
    public void IconOutsideTheProject_EmitsNullAndAdvises()
    {
        // The vrc-patterns gate compiles every entry this way (ControllerFixpoint.CompileToTemp): a yaml outside
        // the project, its icon beside it, into a host whose AssetDatabase sees neither. Failing here would
        // make `icon:` unauthorable by that library.
        var outside = Path.Combine(Path.GetTempPath(), "menu_icon_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(outside, "assets"));
        try
        {
            var tex = new Texture2D(2, 2);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(outside, "assets", "Knob.png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            var yaml = Path.Combine(outside, "M_Fx.yaml");
            File.WriteAllText(yaml, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: assets/Knob.png\n");

            var msg = CompileController.Run(yaml, OutDir);
            StringAssert.Contains("=> OK", msg);
            Assert.IsNull(Load().controls.Single().icon, "an unreachable icon emits as null, not as a failure");
            StringAssert.Contains("not in the project's AssetDatabase", RunLogBody(msg));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Test]
    public void OutOfProjectCompile_NeverFails_WhicheverSpelling()
    {
        // The regime follows the document's location, so every icon spelling degrades together: a throw on a
        // project-spelled icon would fail a gate run over an entry that compiles fine in a project.
        var outside = Path.Combine(Path.GetTempPath(), "menu_spell_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(outside);
        try
        {
            foreach (var spelling in new[] { "Assets/NotHere/Knob.png", "Packages/com.nobody.nothing/Knob.png", "assets/Absent.png" })
            {
                var yaml = Path.Combine(outside, "M_Fx.yaml");
                File.WriteAllText(yaml, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: " + spelling + "\n");
                var msg = CompileController.Run(yaml, OutDir);
                StringAssert.Contains("=> OK", msg, "spelling must not decide the regime: " + spelling);
                Assert.IsNull(Load().controls.Single().icon, spelling);
            }
        }
        finally { Directory.Delete(outside, true); }
    }

    [Test]
    public void InProjectDocumentGivenAnAbsolutePath_StillResolves()
    {
        // Every interactive door hands CompileController.Run an absolute path (the menu door calls
        // Path.GetFullPath; OpenFilePanel returns absolute), which must not read as out-of-project and null
        // every icon.
        var icon = MakeIcon(TestRoot + "/assets/Knob.png");
        File.WriteAllText(_srcPath, Head + "menu:\n  - toggle: T\n    param: Enable\n    icon: assets/Knob.png\n");
        var msg = CompileController.Run(Path.GetFullPath(_srcPath), OutDir);
        StringAssert.Contains("=> OK", msg);
        Assert.AreEqual(icon, AssetDatabase.GetAssetPath(Load().controls.Single().icon));
        StringAssert.Contains("_(none)_", RunLogBody(msg).Split(new[] { "## Compile advisory: unadjudicated menu icons" }, System.StringSplitOptions.None)[1]
            .Split(new[] { "\n## " }, System.StringSplitOptions.None)[0]);
    }

    [Test]
    public void BackslashSeparatorsResolve_LikeForwardOnes()
    {
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
        StringAssert.Contains("## Compile advisory: unadjudicated menu icons\n\n_(none)_",
            RunLogBody(Compile("menu:\n  - toggle: T\n    param: Enable\n")));
    }

    [Test]
    public void SdkControlCapMatchesTheEchoedConstant()
    {
        Assert.AreEqual(VRCExpressionsMenu.MAX_CONTROLS, MenuLimits.MaxControlsPerMenu);
    }
}
