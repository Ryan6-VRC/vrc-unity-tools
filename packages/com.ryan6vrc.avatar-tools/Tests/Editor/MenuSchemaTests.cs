using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

// Behavioral tests for the `menu:` surface — parse (AnimatorSchemaYaml) and validate (SchemaValidation).
// Pure C# over the System.*-only model: no scene, no asset, no VRC SDK. The EMIT side needs the SDK types
// and lives in MenuEmitTests. Run headless via tools/run-editmode-tests.ps1; not via MCP run_tests (wrong
// venue). See docs/verify.md.
public class MenuSchemaTests
{
    private const string Head = @"schema: 1
controller: M_Fx
basis: avatar-root
parameters:
  Enable: bool
  Mode: int
  Sat: float
  Work: { type: float, scratch: true }
layers: []
";

    private static AnimDocument Parse(string menuBlock) => AnimatorSchemaYaml.Parse(Head + menuBlock, "test.yaml");

    // ---------- parse ----------

    [Test]
    public void NoMenuBlock_LeavesMenuNull()
    {
        // The null (not empty-list) sentinel is what emit keys off to write no asset at all, so a document
        // predating this surface must not acquire an empty menu.
        Assert.IsNull(AnimatorSchemaYaml.Parse(Head, "test.yaml").Menu);
    }

    [Test]
    public void ParsesEachKind_NameInValuePosition()
    {
        var doc = Parse(@"menu:
  - button: Fire
    param: Enable
  - toggle: Wear
    param: Enable
  - radial: Saturation
    param: Sat
  - submenu: More
    controls:
      - toggle: Inner
        param: Enable
");
        Assert.AreEqual(4, doc.Menu.Count);
        CollectionAssert.AreEqual(
            new[] { MenuControlKind.Button, MenuControlKind.Toggle, MenuControlKind.Radial, MenuControlKind.SubMenu },
            doc.Menu.Select(c => c.Kind).ToArray());
        CollectionAssert.AreEqual(
            new[] { "Fire", "Wear", "Saturation", "More" }, doc.Menu.Select(c => c.Name).ToArray());
        Assert.AreEqual("Inner", doc.Menu[3].Controls.Single().Name);
    }

    [Test]
    public void ValueDefaultsToOne_AndIsOverridable()
    {
        var doc = Parse(@"menu:
  - toggle: A
    param: Enable
  - toggle: B
    param: Mode
    value: 3
");
        Assert.AreEqual(1f, doc.Menu[0].Value);
        Assert.AreEqual(3f, doc.Menu[1].Value);
    }

    [Test]
    public void NestedSubMenus_RecurseToAnyDepth()
    {
        var doc = Parse(@"menu:
  - submenu: L1
    controls:
      - submenu: L2
        controls:
          - button: Deep
            param: Enable
");
        Assert.AreEqual("Deep", doc.Menu[0].Controls[0].Controls[0].Name);
    }

    [Test]
    public void UnknownControlField_FailsByName()
    {
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - toggle: A
    param: Enable
    icon: something
"));
        StringAssert.Contains("icon", e.Message);
    }

    [Test]
    public void NoKindKey_FailsAndListsTheKinds()
    {
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - param: Enable
"));
        StringAssert.Contains("no control kind", e.Message);
    }

    [Test]
    public void TwoKindsInOneEntry_Fails()
    {
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - toggle: A
    button: B
    param: Enable
"));
        StringAssert.Contains("two control kinds", e.Message);
    }

    [Test]
    public void ControlsOnANonSubMenu_Fails()
    {
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - toggle: A
    param: Enable
    controls: []
"));
        StringAssert.Contains("submenu field", e.Message);
    }

    [Test]
    public void SubMenuWithoutControls_Fails()
    {
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - submenu: Empty
"));
        StringAssert.Contains("needs 'controls'", e.Message);
    }

    [Test]
    public void RadialWithValue_Fails()
    {
        // A radial writes nothing on press; accepting `value:` would silently drop it on emit.
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - radial: Knob
    param: Sat
    value: 1
"));
        StringAssert.Contains("no 'value'", e.Message);
    }

    // ---------- validate ----------

    private static string[] Errors(string menuBlock) => SchemaValidation.Validate(Parse(menuBlock)).ToArray();

    [Test]
    public void WellFormedMenu_Validates()
    {
        CollectionAssert.IsEmpty(Errors(@"menu:
  - toggle: A
    param: Enable
  - radial: K
    param: Sat
  - submenu: S
    controls:
      - button: B
        param: Mode
        value: 2
"));
    }

    [Test]
    public void OverflowingPage_IsRefused()
    {
        // Nine controls on one page. The SDK's own inspector silently truncates to MAX_CONTROLS, so this
        // is a destructive defect, not a style note — hence an error rather than an advisory.
        var yaml = "menu:\n" + string.Concat(Enumerable.Range(0, MenuLimits.MaxControlsPerMenu + 1)
            .Select(i => $"  - toggle: T{i}\n    param: Enable\n"));
        var errs = Errors(yaml);
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-overflow", errs[0]);
        StringAssert.Contains($"{MenuLimits.MaxControlsPerMenu + 1} controls", errs[0]);
    }

    [Test]
    public void OverflowIsPerPage_NotPerTree()
    {
        // A full page plus a sub-menu holding another full page is legal — which is the whole point of
        // sub-menus, and the case a tree-wide count would wrongly reject.
        var page = string.Concat(Enumerable.Range(0, MenuLimits.MaxControlsPerMenu - 1)
            .Select(i => $"  - toggle: T{i}\n    param: Enable\n"));
        var inner = string.Concat(Enumerable.Range(0, MenuLimits.MaxControlsPerMenu)
            .Select(i => $"      - toggle: I{i}\n        param: Enable\n"));
        CollectionAssert.IsEmpty(Errors("menu:\n" + page + "  - submenu: More\n    controls:\n" + inner));
    }

    [Test]
    public void UndeclaredParam_IsRefused()
    {
        var errs = Errors(@"menu:
  - toggle: A
    param: Nope
");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-undeclared-param", errs[0]);
    }

    [Test]
    public void ScratchParam_IsRefused()
    {
        // A scratch param never reaches the params asset, so VRChat never sees the name and the control is
        // inert on the avatar — invisible in the built menu, which is why it has to fail at compile.
        var errs = Errors(@"menu:
  - toggle: A
    param: Work
");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-scratch-param", errs[0]);
    }

    [Test]
    public void RadialOnNonFloat_IsRefused()
    {
        var errs = Errors(@"menu:
  - radial: K
    param: Enable
");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-radial-type", errs[0]);
    }

    [Test]
    public void NonBinaryValueOnBool_IsRefused()
    {
        var errs = Errors(@"menu:
  - toggle: A
    param: Enable
    value: 5
");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-bool-value", errs[0]);
    }

    [Test]
    public void FractionalValueOnInt_IsRefused()
    {
        var errs = Errors(@"menu:
  - toggle: A
    param: Mode
    value: 1.5
");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-int-value", errs[0]);
    }

    [Test]
    public void ParamlessNonSubMenuControl_IsRefused()
    {
        var errs = Errors(@"menu:
  - button: Dead
");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-no-param", errs[0]);
    }

    [Test]
    public void BareSubMenu_NeedsNoParam()
    {
        CollectionAssert.IsEmpty(Errors(@"menu:
  - submenu: S
    controls:
      - toggle: A
        param: Enable
"));
    }

    [Test]
    public void DefectInANestedPage_NamesThatPage()
    {
        var errs = Errors(@"menu:
  - submenu: Colors
    controls:
      - toggle: Red
        param: Nope
");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("Colors", errs[0]);
        StringAssert.Contains("Red", errs[0]);
    }
}
