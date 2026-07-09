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
}
