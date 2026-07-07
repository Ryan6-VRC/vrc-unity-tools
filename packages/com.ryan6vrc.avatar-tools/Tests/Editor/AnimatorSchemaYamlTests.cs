using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

// Behavioral tests for AnimatorSchemaYaml. The model + parser depend only on System.*, so these exercise
// pure C# — no scene, no asset, no VRC SDK. NOT run via MCP run_tests (it crashes the editor); run from the
// Unity Test Runner window or CI.
public class AnimatorSchemaYamlTests
{
    // The canonical debounce document. Later tasks reference AnimatorSchemaYamlTests.DebounceDoc verbatim.
    public const string DebounceDoc = @"schema: 1
controller: Debounce_Fx
basis: avatar-root
role: fx
defaults:
  writeDefaults: on
  transition: { duration: 0, exitTime: none, interruption: none }
parameters:
  RawInput: bool
  Debounced: bool
layers:
  - name: Debounce
    states:
      Idle:
        motion: ~
        transitions:
          - { to: Pending, when: [ RawInput is true ] }
      Pending:
        motion: { clip: timer }
        transitions:
          - { to: Active, when: [], exitTime: 1.0 }
          - { to: Idle,   when: [ RawInput is false ] }
      Active:
        motion: { clip: hold_on }
        behaviours:
          - driver: { set: { Debounced: 1 } }
        transitions:
          - { to: Idle, when: [ RawInput is false ] }
    default: Idle
clips:
  timer:   { seconds: 0.2 }
  hold_on: { set: { Debounced: 1 } }
_notes: { source: hand-authored }
";

    [Test]
    public void Parses_Debounce_Shape()
    {
        var doc = AnimatorSchemaYaml.Parse(DebounceDoc, "mem://debounce");

        Assert.AreEqual(1, doc.Schema);
        Assert.AreEqual("Debounce_Fx", doc.ControllerName);
        Assert.AreEqual(BindingBasis.AvatarRoot, doc.Basis);
        Assert.AreEqual(ControllerRole.Fx, doc.Role);
        Assert.AreEqual("mem://debounce", doc.SourcePath);

        // defaults
        Assert.IsTrue(doc.Defaults.WriteDefaults);
        Assert.AreEqual(0f, doc.Defaults.TransitionDuration);
        Assert.IsFalse(doc.Defaults.TransitionHasExitTime);
        Assert.AreEqual(TransitionInterruption.None, doc.Defaults.Interruption);

        // parameters (shorthand)
        Assert.AreEqual(2, doc.Parameters.Count);
        Assert.AreEqual("RawInput", doc.Parameters[0].Name);
        Assert.AreEqual(AnimParamType.Bool, doc.Parameters[0].Type);
        Assert.IsNull(doc.Parameters[0].Vrc);
        Assert.AreEqual("Debounced", doc.Parameters[1].Name);
        Assert.AreEqual(AnimParamType.Bool, doc.Parameters[1].Type);

        // layer
        Assert.AreEqual(1, doc.Layers.Count);
        var layer = doc.Layers[0];
        Assert.AreEqual("Debounce", layer.Name);
        Assert.AreEqual(3, layer.Root.States.Count);
        Assert.AreEqual("Idle", layer.Root.DefaultState);

        var idle = layer.Root.States[0];
        Assert.AreEqual("Idle", idle.Name);
        Assert.IsNull(idle.Motion);   // motion: ~
        Assert.AreEqual(1, idle.Transitions.Count);
        Assert.AreEqual("Pending", idle.Transitions[0].To);
        Assert.IsFalse(idle.Transitions[0].ToExit);
        Assert.AreEqual(1, idle.Transitions[0].When.Count);
        Assert.AreEqual("RawInput", idle.Transitions[0].When[0].Param);
        Assert.AreEqual(CondOp.Is, idle.Transitions[0].When[0].Op);
        Assert.AreEqual(1f, idle.Transitions[0].When[0].Value);

        var pending = layer.Root.States[1];
        Assert.AreEqual("Pending", pending.Name);
        Assert.IsNotNull(pending.Motion);
        Assert.AreEqual("timer", pending.Motion.Clip);
        Assert.AreEqual(2, pending.Transitions.Count);
        // the unconditional (timer-elapsed) transition carries an explicit exitTime
        var unconditional = pending.Transitions[0];
        Assert.AreEqual("Active", unconditional.To);
        Assert.AreEqual(0, unconditional.When.Count);
        Assert.IsTrue(unconditional.ExitTime.HasValue);
        Assert.AreEqual(1.0f, unconditional.ExitTime.Value);

        var active = layer.Root.States[2];
        Assert.AreEqual("hold_on", active.Motion.Clip);
        Assert.AreEqual(1, active.Behaviours.Count);
        Assert.AreEqual("driver", active.Behaviours[0].Kind);
        Assert.IsTrue(active.Behaviours[0].Fields.ContainsKey("set"));

        // clips
        Assert.AreEqual(2, doc.Clips.Count);
        var timer = doc.Clips.Find(c => c.Name == "timer");
        Assert.IsNotNull(timer);
        Assert.IsTrue(timer.Seconds.HasValue);
        Assert.AreEqual(0.2f, timer.Seconds.Value, 1e-6f);
        var hold = doc.Clips.Find(c => c.Name == "hold_on");
        Assert.IsNotNull(hold);
        Assert.AreEqual(1f, hold.Sets["Debounced"]);

        // reserved note (underscore-prefixed) never becomes a typed field
        Assert.IsTrue(doc.ReservedNotes.ContainsKey("_notes"));
    }

    [Test]
    public void Long_And_Shorthand_Parameters_Both_Parse()
    {
        const string doc = @"schema: 1
controller: Params
parameters:
  Short: float
  Long:
    type: int
    default: 3
    aap: true
    vrc: { synced: true, saved: false, osc: true, type: float }
";
        var d = AnimatorSchemaYaml.Parse(doc, null);
        Assert.AreEqual(AnimParamType.Float, d.Parameters[0].Type);
        var lng = d.Parameters[1];
        Assert.AreEqual(AnimParamType.Int, lng.Type);
        Assert.AreEqual(3f, lng.Default);
        Assert.IsTrue(lng.Aap);
        Assert.IsNotNull(lng.Vrc);
        Assert.IsTrue(lng.Vrc.Synced);
        Assert.IsFalse(lng.Vrc.Saved);
        Assert.IsTrue(lng.Vrc.Osc);
        Assert.AreEqual(AnimParamType.Float, lng.Vrc.VrcType);
    }

    [Test]
    public void Duplicate_State_Key_Throws()
    {
        const string doc = @"schema: 1
controller: Dup
layers:
  - name: L
    states:
      A:
        motion: ~
      A:
        motion: ~
    default: A
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, null));
        StringAssert.Contains("A", ex.Message);
    }

    [Test]
    public void Duplicate_Parameter_Key_Throws()
    {
        const string doc = @"schema: 1
controller: Dup
parameters:
  P: bool
  P: int
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, null));
        StringAssert.Contains("P", ex.Message);
    }

    [Test]
    public void Anchor_Is_Refused_By_Name()
    {
        const string doc = @"schema: 1
controller: &x Foo
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, null));
        StringAssert.Contains("anchor", ex.Message.ToLowerInvariant());
    }

    [Test]
    public void Underscore_Top_Level_Key_Goes_To_ReservedNotes()
    {
        const string doc = @"schema: 1
controller: R
_meta: { a: 1, b: two }
";
        var d = AnimatorSchemaYaml.Parse(doc, null);
        Assert.IsTrue(d.ReservedNotes.ContainsKey("_meta"));
    }

    [Test]
    public void Block_Scalar_Is_Refused_By_Name()
    {
        const string doc = @"schema: 1
controller: R
note: |
  block text
";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, null));
        StringAssert.Contains("block scalar", ex.Message.ToLowerInvariant());
    }
}
