using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Layout-preservation tests: the parse + validate cases run purely in memory; the Compile_* cases below
// call ControllerEmit.Build, which persists a controller to the scratch folder — TearDown removes it.
public class ControllerLayoutTests
{
    private const string ScratchFolder = "Assets/Agent/Scratch/emit";

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(ScratchFolder))
            AssetDatabase.DeleteAsset(ScratchFolder);
    }

    // A minimal two-state layer with an authored layout block placing Idle off-grid.
    private const string LayoutDoc = @"
schema: 1
controller: LayoutTest_Fx
basis: avatar-root
role: fx
parameters: { Flag: bool }
layers:
  - name: L
    states:
      Idle: { motion: ~, transitions: [ { to: Go, when: [ Flag is true ] } ] }
      Go:   { motion: ~, transitions: [ { to: Idle, when: [ Flag is false ] } ] }
    default: Idle
    layout:
      nodes: { Idle: [111, 222], Go: [333, 444] }
      entry: [10, 20]
      any:   [10, 80]
      exit:  [10, 140]
";

    [Test]
    public void Parse_Populates_Layout_Model()
    {
        var doc = AnimatorSchemaYaml.Parse(LayoutDoc, "test");
        var layout = doc.Layers[0].Root.Layout;
        Assert.IsNotNull(layout, "layout block parsed");
        Assert.AreEqual(new[] { 111f, 222f }, layout.Nodes["Idle"]);
        Assert.AreEqual(new[] { 333f, 444f }, layout.Nodes["Go"]);
        Assert.AreEqual(new[] { 10f, 140f }, layout.Exit);
        Assert.IsNull(layout.Parent, "no parent authored");
    }

    [Test]
    public void Parse_Rejects_Malformed_Coordinate()
    {
        var bad = LayoutDoc.Replace("Idle: [111, 222]", "Idle: [111]");
        Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(bad, "test"));
    }

    [Test]
    public void Parse_Rejects_NonNumeric_Coordinate()
    {
        var bad = LayoutDoc.Replace("Idle: [111, 222]", "Idle: [a, b]");
        Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(bad, "test"));
    }

    [Test]
    public void Parse_Rejects_Unknown_Layout_Key()
    {
        var bad = LayoutDoc.Replace("entry: [10, 20]", "bogus: [1, 2]\n      entry: [10, 20]");
        Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(bad, "test"));
    }

    [Test]
    public void Validate_Rejects_Unknown_Layout_Node()
    {
        var bad = LayoutDoc.Replace("Go: [333, 444]", "Ghost: [333, 444]");
        var doc = AnimatorSchemaYaml.Parse(bad, "test");
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(errors.Exists(e => e.Contains("dangling-layout") && e.Contains("Ghost")),
            "unknown layout node flagged: " + string.Join(" | ", errors));
    }

    [Test]
    public void Validate_Accepts_Resolving_Layout_Nodes()
    {
        var doc = AnimatorSchemaYaml.Parse(LayoutDoc, "test");
        var errors = SchemaValidation.Validate(doc);
        Assert.IsFalse(errors.Exists(e => e.Contains("dangling-layout")), string.Join(" | ", errors));
    }

    [Test]
    public void Compile_Honors_Listed_And_Grids_Omitted()
    {
        // Author only Idle; Go is omitted from nodes -> grid slot 1.
        var doc = AnimatorSchemaYaml.Parse(
            LayoutDoc.Replace("nodes: { Idle: [111, 222], Go: [333, 444] }", "nodes: { Idle: [111, 222] }"),
            "test");
        ControllerEmit.Build(doc, out var res);
        var sm = res.Controller.layers[0].stateMachine;
        var idle = System.Array.Find(sm.states, cs => cs.state.name == "Idle");
        var go   = System.Array.Find(sm.states, cs => cs.state.name == "Go");
        Assert.AreEqual(new Vector3(111, 222, 0), idle.position, "Idle honored");
        // Go is the 2nd state in document order -> grid slot i=1 -> (300, 60).
        Assert.AreEqual(new Vector3(300, 60, 0), go.position, "Go grid-fallback");
    }

    [Test]
    public void Compile_Writes_Special_Constants_When_Unauthored()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(doc, out var res);
        var sm = res.Controller.layers[0].stateMachine;
        Assert.AreEqual(ControllerEmit.SpecialEntry, sm.entryPosition);
        Assert.AreEqual(ControllerEmit.SpecialAny,   sm.anyStatePosition);
        Assert.AreEqual(ControllerEmit.SpecialExit,  sm.exitPosition);
    }

    // Move a state off-grid in a persisted controller (ChildAnimatorState is a struct -> edit a copy, write
    // the whole array back), then save.
    private static void MoveState(AnimatorStateMachine sm, string name, Vector3 to)
    {
        var arr = sm.states;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i].state.name == name) { arr[i].position = to; }
        sm.states = arr;
        EditorUtility.SetDirty(sm);
        AssetDatabase.SaveAssets();
    }

    [Test]
    public void Decompile_Emits_Layout_For_Arranged_Controller()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(doc, out var res);
        var sm = res.Controller.layers[0].stateMachine;
        MoveState(sm, sm.states[0].state.name, new Vector3(777, 888, 0));

        var w = ControllerDecompile.Walk(res.Controller);
        var layout = w.Doc.Layers[0].Root.Layout;
        Assert.IsNotNull(layout, "arranged controller emits a layout block");
        Assert.AreEqual(new[] { 777f, 888f }, layout.Nodes[AddressPath.EscapeSegment(sm.states[0].state.name)]);
    }

    [Test]
    public void Decompile_Omits_Layout_For_Grid_Controller()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(doc, out var res);
        var w = ControllerDecompile.Walk(res.Controller);
        Assert.IsNull(w.Doc.Layers[0].Root.Layout, "never-arranged controller stays layout-free");
    }

    [Test]
    public void Escaped_Node_Name_Roundtrips()
    {
        // A state whose name contains '/'. The states: key is RAW ("A/B" -> State.Name). Every ESCAPED
        // reference (transition target, default, layout node key) is the address form the producers emit:
        // EscapeSegment gives "A\/B", and double-quoted YAML needs the backslash doubled ("A\\/B") because
        // the reader halves \\ -> \ (an unknown \/ would instead drop the backslash). This is exactly what
        // ControllerDecompile + AnimatorSchemaEmit round-trip.
        var src = @"
schema: 1
controller: Esc_Fx
basis: avatar-root
role: fx
parameters: { Flag: bool }
layers:
  - name: L
    states:
      ""A/B"": { motion: ~, transitions: [ { to: ""A\\/B"", when: [ Flag is true ], exitTime: 1.0 } ] }
    default: ""A\\/B""
    layout:
      nodes: { ""A\\/B"": [123, 456] }
";
        var doc = AnimatorSchemaYaml.Parse(src, "esc");
        Assert.IsEmpty(SchemaValidation.Validate(doc).FindAll(e => e.Contains("dangling-layout")));
        ControllerEmit.Build(doc, out var res);
        var cs = res.Controller.layers[0].stateMachine.states[0];
        Assert.AreEqual("A/B", cs.state.name);
        Assert.AreEqual(new Vector3(123, 456, 0), cs.position, "escaped-name node placed");
    }

    [Test]
    public void Serialize_Renders_Layout_Flow_And_RFormat()
    {
        var doc = AnimatorSchemaYaml.Parse(LayoutDoc, "test");
        var yaml = AnimatorSchemaEmit.Serialize(doc);
        StringAssert.Contains("layout:", yaml);
        StringAssert.Contains("nodes: {", yaml);       // flow form
        StringAssert.Contains("[111, 222]", yaml);
        Assert.IsFalse(yaml.Contains("parent:"), "no parent authored -> not emitted");
    }
}
