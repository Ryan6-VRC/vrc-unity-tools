using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

// Layout-preservation tests: in-memory parse + validate of the layout: block. No AssetDatabase / Build.
public class ControllerLayoutTests
{
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
}
