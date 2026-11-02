using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

// Pure C# over the System.*-only model: no scene, no VRC SDK.
public class SchemaValidationTests
{
    private static AnimDocument DocWithCondition(string paramName, AnimParamType type, CondOp op, float value)
    {
        var doc = new AnimDocument { Schema = 1 };
        doc.Parameters.Add(new ParamSpec { Name = paramName, Type = type });
        var layer = new Layer { Name = "L" };
        var st = new State { Name = "S" };
        var tr = new Transition { To = "T" };
        tr.When.Add(new Condition { Param = paramName, Op = op, Value = value });
        st.Transitions.Add(tr);
        layer.Root.States.Add(st);
        doc.Layers.Add(layer);
        return doc;
    }

    private static bool Any(List<string> errors, params string[] fragments)
    {
        foreach (var e in errors)
        {
            bool all = true;
            foreach (var f in fragments) if (!e.Contains(f)) { all = false; break; }
            if (all) return true;
        }
        return false;
    }

    private static AnimDocument DocWithTree(BlendTreeSpec tree, params ParamSpec[] declared)
    {
        var doc = new AnimDocument { Schema = 1 };
        foreach (var p in declared) doc.Parameters.Add(p);
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "S", Motion = new MotionRef { Tree = tree } });
        doc.Layers.Add(layer);
        return doc;
    }

    private static ParamSpec P(string name, AnimParamType t) => new ParamSpec { Name = name, Type = t };

    [Test]
    public void Unknown_Schema_Version_Errors()
    {
        var doc = new AnimDocument { Schema = 2 };
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "schema", "2"), "expected a schema-version error naming 2");
    }

    [Test]
    public void Reserved_Carrier_Param_Is_Rejected()
    {
        var doc = new AnimDocument { Schema = 1 };
        doc.Parameters.Add(new ParamSpec { Name = ReservedNames.CarrierParam, Type = AnimParamType.Float });
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "reserved-param", ReservedNames.CarrierParam),
            "declaring the reserved carrier param must be refused");
    }

    [Test]
    public void Greater_On_Bool_Param_Errors()
    {
        var doc = DocWithCondition("B", AnimParamType.Bool, CondOp.Greater, 0.5f);
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "B", "greater"), "expected op/type error naming B and greater");
    }

    [Test]
    public void Equals_On_Float_Param_Errors()
    {
        var doc = DocWithCondition("F", AnimParamType.Float, CondOp.Equals, 1f);
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "F", "equals"), "expected op/type error naming F and equals");
    }

    [Test]
    public void Is_On_Int_Param_Errors()
    {
        var doc = DocWithCondition("I", AnimParamType.Int, CondOp.Is, 1f);
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "I", "is"), "expected op/type error naming I and is");
    }

    [Test]
    public void Undeclared_Param_Condition_Is_Skipped()
    {
        // Not this rule's concern: the controller-level undeclared-param check owns it.
        var doc = new AnimDocument { Schema = 1 };
        var layer = new Layer { Name = "L" };
        var st = new State { Name = "S" };
        var tr = new Transition { To = "T" };
        tr.When.Add(new Condition { Param = "Ghost", Op = CondOp.Greater, Value = 1f });
        st.Transitions.Add(tr);
        layer.Root.States.Add(st);
        doc.Layers.Add(layer);
        Assert.IsEmpty(SchemaValidation.Validate(doc));
    }

    [Test]
    public void Dangling_Default_State_Errors()
    {
        var doc = new AnimDocument { Schema = 1 };
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "Real" });
        layer.Root.DefaultState = "Nope";
        doc.Layers.Add(layer);
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "Nope"), "expected a dangling-default error naming Nope");
    }

    [Test]
    public void Dangling_Clip_Ref_Errors()
    {
        var doc = new AnimDocument { Schema = 1 };
        var layer = new Layer { Name = "L" };
        var st = new State { Name = "S", Motion = new MotionRef { Clip = "ghost" } };
        layer.Root.States.Add(st);
        doc.Layers.Add(layer);
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "ghost"), "expected a dangling-clip error naming ghost");
    }

    [Test]
    public void BaseFx_Under_Three_Layers_Errors()
    {
        var doc = new AnimDocument { Schema = 1, Role = ControllerRole.BaseFx };
        doc.Layers.Add(new Layer { Name = "Only" });
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "base-fx"), "expected a base-fx-floor error");
    }

    [Test]
    public void Valid_Per_State_WD_Override_No_Error()
    {
        var doc = new AnimDocument { Schema = 1 };
        var layer = new Layer { Name = "L", WriteDefaults = true };
        layer.Root.States.Add(new State { Name = "S", WriteDefaults = false });
        doc.Layers.Add(layer);
        Assert.IsEmpty(SchemaValidation.Validate(doc));
    }

    [Test]
    public void Clean_Debounce_Has_No_Errors()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "mem");
        Assert.IsEmpty(SchemaValidation.Validate(doc));
    }

    [Test]
    public void Missing_Basis_Throws_At_Parse()
    {
        const string doc = @"schema: 1
controller: NoBasis
parameters:
  P: bool
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, null));
        StringAssert.Contains("basis", ex.Message);
    }

    // A key-derived length floors (the author declared nothing); a declared zero is a mistake and is refused rather
    // than rounded up.
    [TestCase("seconds", "0")]
    [TestCase("seconds", "-1")]
    [TestCase("length", "0")]
    public void NonPositive_Clip_Length_Throws_At_Parse(string key, string value)
    {
        string doc = @"schema: 1
controller: BadLen
basis: avatar-root
clips:
  c:
    " + key + ": " + value + @"
    set: { Prop/Renderer.enabled: 1 }
layers:
  - name: L
    states:
      S:
        motion: { clip: c }
    default: S
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, null));
        StringAssert.Contains("must be positive", ex.Message);
    }

    // ── blend-axis-type: a blend-tree axis must be a Float animator param ──────────────────────────
    // Undeclared axes are skipped: the undeclared-param lint owns them.

    // The cases below vary the SITE (1D, 2D Y axis, Direct child weight, nested tree), not the non-float type:
    // the rule is one type check, but each site has its own call path.
    [Test]
    public void Int_1D_Blend_Axis_Errors()
    {
        var tree = new BlendTreeSpec { Kind = TreeKind.OneD, Param = "Axis" };
        var errors = SchemaValidation.Validate(DocWithTree(tree, P("Axis", AnimParamType.Int)));
        Assert.IsTrue(Any(errors, "blend-axis-type", "Axis"), "an int 1D blend axis must be flagged");
    }

    [Test]
    public void Int_2D_Blend_AxisY_Errors()
    {
        var tree = new BlendTreeSpec { Kind = TreeKind.FreeformCartesian2D, Param = "X", ParamY = "Y" };
        var errors = SchemaValidation.Validate(DocWithTree(tree,
            P("X", AnimParamType.Float), P("Y", AnimParamType.Int)));
        Assert.IsTrue(Any(errors, "blend-axis-type", "Y"), "an int 2D Y axis must be flagged");
    }

    [Test]
    public void Int_Direct_Child_Weight_Errors()
    {
        var tree = new BlendTreeSpec { Kind = TreeKind.Direct };
        tree.Children.Add(new TreeChild { DirectWeight = "W" });
        var errors = SchemaValidation.Validate(DocWithTree(tree, P("W", AnimParamType.Int)));
        Assert.IsTrue(Any(errors, "blend-axis-type", "W"), "an int Direct child weight must be flagged");
    }

    [Test]
    public void Int_Axis_In_Nested_Tree_Errors()
    {
        var inner = new BlendTreeSpec { Kind = TreeKind.OneD, Param = "Inner" };
        var outer = new BlendTreeSpec { Kind = TreeKind.OneD, Param = "Outer" };
        outer.Children.Add(new TreeChild { Motion = new MotionRef { Tree = inner } });
        var errors = SchemaValidation.Validate(DocWithTree(outer,
            P("Outer", AnimParamType.Float), P("Inner", AnimParamType.Int)));
        Assert.IsTrue(Any(errors, "blend-axis-type", "Inner"), "an int axis in a nested tree must be flagged");
    }

    [Test]
    public void Float_Blend_Axis_No_Error()
    {
        var tree = new BlendTreeSpec { Kind = TreeKind.OneD, Param = "Axis" };
        Assert.IsEmpty(SchemaValidation.Validate(DocWithTree(tree, P("Axis", AnimParamType.Float))));
    }

    [Test]
    public void Undeclared_Blend_Axis_Is_Skipped()
    {
        var tree = new BlendTreeSpec { Kind = TreeKind.OneD, Param = "Ghost" };
        Assert.IsEmpty(SchemaValidation.Validate(DocWithTree(tree)));
    }
}
