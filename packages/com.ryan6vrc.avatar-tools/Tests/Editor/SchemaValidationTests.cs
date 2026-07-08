using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

// Behavioral tests for SchemaValidation. Pure C# over the System.*-only model — no scene, no VRC SDK.
// NOT run via MCP run_tests (it crashes the editor); run from the Unity Test Runner window or CI.
public class SchemaValidationTests
{
    // One layer with one state carrying one transition-condition — the minimal vehicle for op/type rules.
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

    [Test]
    public void Unknown_Schema_Version_Errors()
    {
        var doc = new AnimDocument { Schema = 2 };
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(Any(errors, "schema", "2"), "expected a schema-version error naming 2");
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
        // Condition references a param that is NOT declared -> not this rule's concern, no crash, no error.
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
        // Layer WD on, one state WD off — a legal per-state override. Must produce NO error.
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
}
