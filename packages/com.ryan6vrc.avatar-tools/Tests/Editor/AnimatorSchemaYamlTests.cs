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
basis: avatar-root
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
    public void Parse_Binds_SubMachine_And_Ladders()
    {
        const string doc =
            "schema: 1\ncontroller: Nested_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n" +
            "  - name: L\n" +
            "    states:\n      Idle: { motion: ~ }\n" +
            "    machines:\n" +
            "      Sub:\n" +
            "        states:\n          A: { motion: ~ }\n" +
            "        default: A\n" +
            "    entry:\n" +
            "      - { to: Sub, when: [ P is true ] }\n" +
            "    any:\n" +
            "      - { to: Idle, when: [ P is false ], canTransitionToSelf: false }\n" +
            "    default: Idle\n";
        var d = AnimatorSchemaYaml.Parse(doc, "test");
        var root = d.Layers[0].Root;
        Assert.AreEqual(1, root.Machines.Count);
        Assert.AreEqual("Sub", root.Machines[0].Name);
        Assert.AreEqual("A", root.Machines[0].Machine.DefaultState);
        Assert.AreEqual(1, root.EntryLadder.Count);
        Assert.AreEqual("Sub", root.EntryLadder[0].To);
        Assert.AreEqual(1, root.AnyLadder.Count);
        Assert.IsFalse(root.AnyLadder[0].CanTransitionToSelf);
    }

    [Test]
    public void Parse_Nests_SubMachines_Two_Levels()
    {
        const string doc =
            "schema: 1\ncontroller: Nested2_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n" +
            "  - name: L\n" +
            "    states:\n      Idle: { motion: ~ }\n" +
            "    machines:\n" +
            "      Outer:\n" +
            "        states:\n          O: { motion: ~ }\n" +
            "        default: O\n" +
            "        machines:\n" +
            "          Inner:\n" +
            "            states:\n              I: { motion: ~ }\n" +
            "            default: I\n" +
            "    default: Idle\n";
        var d = AnimatorSchemaYaml.Parse(doc, "test");
        var outer = d.Layers[0].Root.Machines[0];
        Assert.AreEqual("Outer", outer.Name);
        Assert.AreEqual("O", outer.Machine.DefaultState);
        Assert.AreEqual(1, outer.Machine.Machines.Count);
        var inner = outer.Machine.Machines[0];
        Assert.AreEqual("Inner", inner.Name);
        Assert.AreEqual("I", inner.Machine.DefaultState);
    }

    [Test]
    public void Unknown_Machine_Field_Throws_By_Name()
    {
        const string doc =
            "schema: 1\ncontroller: Bad_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n" +
            "  - name: L\n" +
            "    states:\n      Idle: { motion: ~ }\n" +
            "    machines:\n" +
            "      Sub:\n        bogus: 1\n" +
            "    default: Idle\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "test"));
        StringAssert.Contains("bogus", ex.Message);
    }

    [Test]
    public void CanTransitionToSelf_On_Entry_Ladder_Throws()
    {
        const string doc =
            "schema: 1\ncontroller: Bad_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n" +
            "  - name: L\n" +
            "    states:\n      Idle: { motion: ~ }\n" +
            "    entry:\n" +
            "      - { to: Idle, canTransitionToSelf: true }\n" +
            "    default: Idle\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "test"));
        StringAssert.Contains("canTransitionToSelf", ex.Message);
    }

    [Test]
    public void Mute_On_Entry_Ladder_Throws()
    {
        // Entry transitions honor no mute/solo (the entry-emit path never reads them); accepting the field
        // would silently drop it. Refuse it there, mirroring the canTransitionToSelf precedent.
        const string doc =
            "schema: 1\ncontroller: Bad_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      Idle: { motion: ~ }\n" +
            "    machines:\n      Sub:\n        states:\n          A: { motion: ~ }\n        default: A\n" +
            "    entry:\n      - { to: Sub, mute: true }\n" +
            "    default: Idle\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "test"));
        StringAssert.Contains("mute", ex.Message);
    }

    // Tree fields are honored only on their own kind (build/decode); a misplaced one would silently erase
    // through compile→decompile, so the parser refuses it rather than accept-and-drop.

    [Test]
    public void Normalized_On_NonDirect_Tree_Throws()
    {
        const string doc =
            "schema: 1\ncontroller: T_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: { tree: 1d, param: P, normalized: true }\n    default: S\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "test"));
        StringAssert.Contains("normalized", ex.Message);
    }

    [Test]
    public void Param_On_Direct_Tree_Throws()
    {
        const string doc =
            "schema: 1\ncontroller: T_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: { tree: direct, param: P }\n    default: S\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "test"));
        StringAssert.Contains("param", ex.Message);
    }

    [Test]
    public void ParamY_On_1D_Tree_Throws()
    {
        const string doc =
            "schema: 1\ncontroller: T_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: { tree: 1d, param: P, paramY: P }\n    default: S\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "test"));
        StringAssert.Contains("paramY", ex.Message);
    }

    [Test]
    public void CanTransitionToSelf_On_State_Transition_Throws()
    {
        // A state transition ignores canTransitionToSelf (only the AnyState ladder honors it). Accepting it
        // silently would drop the field — fail loud instead (allowSelf now defaults false for state lists).
        const string doc =
            "schema: 1\ncontroller: Bad_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n" +
            "  - name: L\n" +
            "    states:\n" +
            "      Idle:\n        motion: ~\n        transitions:\n          - { to: Idle, canTransitionToSelf: true }\n" +
            "    default: Idle\n";
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(doc, "test"));
        StringAssert.Contains("canTransitionToSelf", ex.Message);
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
basis: avatar-root
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
