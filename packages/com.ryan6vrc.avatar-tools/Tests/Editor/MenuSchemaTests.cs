using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

// The parse (AnimatorSchemaYaml) and validate (SchemaValidation) half of the `menu:` surface: pure C#, with no
// scene, asset or VRC SDK. The emit half is MenuEmitTests.
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
        // Emit keys off null, not an empty list, to write no asset at all.
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
        // `style` is the SDK field this surface deliberately does not author (docs/animator-schema.md §Not yet
        // in the schema), so the refusal also guards that decision.
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - toggle: A
    param: Enable
    style: 2
"));
        StringAssert.Contains("style", e.Message);
    }

    [Test]
    public void IconParsesOnEveryKind()
    {
        var doc = Parse(@"menu:
  - button: B
    param: Enable
    icon: assets/b.png
  - toggle: T
    param: Enable
    icon: assets/t.png
  - radial: R
    param: Sat
    icon: assets/r.png
  - submenu: S
    icon: assets/s.png
    controls:
      - toggle: Inner
        param: Enable
        icon: Assets/Icons/inner.png
");
        CollectionAssert.AreEqual(
            new[] { "assets/b.png", "assets/t.png", "assets/r.png", "assets/s.png" },
            doc.Menu.Select(c => c.Icon).ToArray());
        Assert.AreEqual("Assets/Icons/inner.png", doc.Menu[3].Controls.Single().Icon);
    }

    [Test]
    public void NoIcon_LeavesTheFieldNull()
    {
        // Emit keys off null to leave the control's icon untouched; "" is refused as menu-icon-empty.
        Assert.IsNull(Parse("menu:\n  - toggle: A\n    param: Enable\n").Menu[0].Icon);
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
        StringAssert.Contains("at least one entry", e.Message);
    }

    [Test]
    public void SubMenuWithEmptyControlsList_Fails()
    {
        // `controls: []` binds to an empty non-null list, which a null-only guard would let through.
        var e = Assert.Throws<SchemaException>(() => Parse(@"menu:
  - submenu: Empty
    controls: []
"));
        StringAssert.Contains("at least one entry", e.Message);
    }

    [Test]
    public void RadialWithValue_Fails()
    {
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
    public void TypeRulesReadTheWireType_NotTheAnimatorType()
    {
        const string head = @"schema: 1
controller: W_Fx
basis: avatar-root
parameters:
  Tag: { type: float, vrc: { type: bool } }
  Count: { type: float, vrc: { type: int } }
layers: []
";
        var radial = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(head + @"menu:
  - radial: Knob
    param: Tag
", "t.yaml"));
        Assert.AreEqual(1, radial.Count);
        StringAssert.Contains("menu-radial-type", radial[0]);

        var boolValue = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(head + @"menu:
  - toggle: T
    param: Tag
    value: 5
", "t.yaml"));
        Assert.AreEqual(1, boolValue.Count);
        StringAssert.Contains("menu-bool-value", boolValue[0]);

        var intValue = SchemaValidation.Validate(AnimatorSchemaYaml.Parse(head + @"menu:
  - toggle: T
    param: Count
    value: 1.5
", "t.yaml"));
        Assert.AreEqual(1, intValue.Count);
        StringAssert.Contains("menu-int-value", intValue[0]);
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
    public void IconShapeIsValidated_ButNotItsExistence()
    {
        CollectionAssert.IsEmpty(Errors("menu:\n  - toggle: A\n    param: Enable\n    icon: assets/nope.png\n"));

        var empty = Errors("menu:\n  - toggle: A\n    param: Enable\n    icon: \"\"\n");
        Assert.AreEqual(1, empty.Length);
        StringAssert.Contains("menu-icon-empty", empty[0]);
    }

    [Test]
    public void ValuelessIconKey_IsRefused()
    {
        // YAML binds a bare `icon:` to null, which must not read as "no icon" while `icon: ""` is refused.
        var e = Assert.Throws<SchemaException>(() => Parse("menu:\n  - toggle: A\n    param: Enable\n    icon:\n"));
        StringAssert.Contains("has no value", e.Message);
    }

    [Test]
    public void IconIsTrimmed_SoSpaceCannotHideAnAbsolutePath()
    {
        Assert.AreEqual("assets/a.png", Parse("menu:\n  - toggle: A\n    param: Enable\n    icon: \"  assets/a.png  \"\n").Menu[0].Icon);

        var errs = Errors("menu:\n  - toggle: A\n    param: Enable\n    icon: \" C:/Users/x/Icon.png\"\n");
        Assert.AreEqual(1, errs.Length);
        StringAssert.Contains("menu-icon-absolute", errs[0]);
    }

    [Test]
    public void AbsoluteIconPath_IsRefused()
    {
        foreach (var p in new[] { "C:/Users/x/Icon.png", "/mnt/x/Icon.png", @"\\server\share\Icon.png" })
        {
            var errs = Errors("menu:\n  - toggle: A\n    param: Enable\n    icon: \"" + p.Replace(@"\", @"\\") + "\"\n");
            Assert.AreEqual(1, errs.Length, "expected exactly one error for " + p);
            StringAssert.Contains("menu-icon-absolute", errs[0]);
        }
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
