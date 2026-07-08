using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Driver = VRC.SDKBase.VRC_AvatarParameterDriver;

// Behavioral tests for ControllerEmit. These DO touch the AssetDatabase (emission persists the controller
// to a scratch path so sub-asset APIs — AddStateMachineBehaviour, embedded clips/trees — work). NOT run via
// MCP run_tests (it crashes the editor); run from the Unity Test Runner window or CI. TearDown removes the
// scratch folder each run.
public class ControllerEmitTests
{
    private const string ScratchFolder = "Assets/Agent/Scratch/emit";

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(ScratchFolder))
            AssetDatabase.DeleteAsset(ScratchFolder);
    }

    private static AnimatorStateMachine RootSm(ControllerEmit.EmitResult r) =>
        r.Controller.layers[0].stateMachine;

    private static AnimatorState State(AnimatorStateMachine sm, string name) =>
        sm.states.First(cs => cs.state.name == name).state;

    // ---- Debounce topology -----------------------------------------------------------------------

    [Test]
    public void Debounce_Topology_Is_Correct()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "mem://debounce");
        ControllerEmit.Build(doc, out var r);

        Assert.AreEqual(1, r.Controller.layers.Length, "one layer");
        var sm = RootSm(r);
        Assert.AreEqual(3, sm.states.Length, "three states");
        Assert.IsNotNull(sm.defaultState);
        Assert.AreEqual("Idle", sm.defaultState.name, "default state is Idle");
    }

    [Test]
    public void Debounce_ExitTime_Only_On_Pending_To_Active()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "mem://debounce");
        ControllerEmit.Build(doc, out var r);
        var sm = RootSm(r);

        var pending = State(sm, "Pending");
        var toActive = pending.transitions.First(t => t.destinationState != null && t.destinationState.name == "Active");
        Assert.IsTrue(toActive.hasExitTime, "Pending->Active has exit time");
        Assert.AreEqual(1.0f, toActive.exitTime, 1e-4f);
        Assert.AreEqual(0, toActive.conditions.Length, "the timer-elapsed transition is unconditional");

        var idle = State(sm, "Idle");
        var toPending = idle.transitions.First(t => t.destinationState != null && t.destinationState.name == "Pending");
        Assert.IsFalse(toPending.hasExitTime, "no exit time elsewhere (Idle->Pending)");
        Assert.AreEqual(1, toPending.conditions.Length);
        Assert.AreEqual(AnimatorConditionMode.If, toPending.conditions[0].mode);
        Assert.AreEqual("RawInput", toPending.conditions[0].parameter);
    }

    [Test]
    public void Timer_Clip_Is_A_CompilerNull_Carrier()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "mem://debounce");
        ControllerEmit.Build(doc, out var r);

        Assert.IsTrue(r.Clips.ContainsKey("timer"));
        var timer = r.Clips["timer"];
        Assert.AreEqual(0.2f, timer.length, 1e-3f, "carrier gives the clip a real ~0.2s length");

        var bindings = AnimationUtility.GetCurveBindings(timer);
        Assert.AreEqual(1, bindings.Length, "exactly one carrier curve");
        Assert.AreEqual("_CompilerNull", bindings[0].path);
        Assert.AreEqual(typeof(GameObject), bindings[0].type);
        Assert.AreEqual("m_IsActive", bindings[0].propertyName);
    }

    [Test]
    public void Active_State_Has_Set_Debounced_Driver()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "mem://debounce");
        ControllerEmit.Build(doc, out var r);
        var active = State(RootSm(r), "Active");

        Assert.AreEqual(1, active.behaviours.Length, "one behaviour");
        var drv = active.behaviours[0] as VRCAvatarParameterDriver;
        Assert.IsNotNull(drv, "behaviour is a VRCAvatarParameterDriver");
        Assert.AreEqual(1, drv.parameters.Count);
        Assert.AreEqual(Driver.ChangeType.Set, drv.parameters[0].type);
        Assert.AreEqual("Debounced", drv.parameters[0].name);
        Assert.AreEqual(1f, drv.parameters[0].value, 1e-6f);
    }

    // ---- AAP / animator-parameter clip binding ---------------------------------------------------

    [Test]
    public void Aap_Set_Clip_Binds_To_Animator_Parameter()
    {
        const string yaml = @"schema: 1
controller: AapTest
basis: avatar-root
parameters:
  Smoothed: { type: float, aap: true }
clips:
  aap_min: { set: { Smoothed: 0 } }
layers:
  - name: L
    states:
      S:
        motion: { clip: aap_min }
    default: S
";
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://aap");
        ControllerEmit.Build(doc, out var r);

        var clip = r.Clips["aap_min"];
        var bindings = AnimationUtility.GetCurveBindings(clip);
        Assert.AreEqual(1, bindings.Length);
        Assert.AreEqual(typeof(Animator), bindings[0].type, "animator-param binding, not a scene path");
        Assert.AreEqual("", bindings[0].path);
        Assert.AreEqual("Smoothed", bindings[0].propertyName);
    }

    // ---- VRC expression parameters ---------------------------------------------------------------

    [Test]
    public void Expression_Parameters_List_All_NonBuiltin_NonScratch()
    {
        // For legibility the asset lists the controller's OWN params even when NOT synced: A (plain bool,
        // networkSynced=false) and B (vrc synced+saved). It EXCLUDES a VRC built-in (IsLocal) and a scratch
        // working param (Work).
        const string yaml = @"schema: 1
controller: ParamList
basis: avatar-root
parameters:
  A: bool
  B: { type: bool, default: true, vrc: { synced: true, saved: true } }
  IsLocal: bool
  Work: { type: float, scratch: true }
";
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://paramlist");
        ControllerEmit.Build(doc, out var r);

        Assert.IsNotNull(r.Params, "an expression-parameters asset was produced");
        var names = r.Params.parameters.Select(p => p.name).ToList();
        Assert.AreEqual(2, r.Params.parameters.Length, "exactly A and B are listed");
        Assert.IsTrue(names.Contains("A"), "plain param A is listed");
        Assert.IsTrue(names.Contains("B"), "vrc param B is listed");
        Assert.IsFalse(names.Contains("IsLocal"), "VRC built-in IsLocal is excluded");
        Assert.IsFalse(names.Contains("Work"), "scratch working param Work is excluded");

        var a = r.Params.parameters.First(p => p.name == "A");
        Assert.AreEqual(VRCExpressionParameters.ValueType.Bool, a.valueType);
        Assert.IsFalse(a.networkSynced, "non-synced param appears with networkSynced=false (no sync-bit cost)");
        Assert.IsFalse(a.saved);

        var b = r.Params.parameters.First(p => p.name == "B");
        Assert.AreEqual(VRCExpressionParameters.ValueType.Bool, b.valueType);
        Assert.AreEqual(1f, b.defaultValue, 1e-6f);
        Assert.IsTrue(b.networkSynced, "vrc synced param stays synced");
        Assert.IsTrue(b.saved, "vrc saved param stays saved");
    }

    [Test]
    public void All_Excluded_Params_Leave_Params_Null()
    {
        // Every declared param is excluded (a built-in + a scratch) → nothing to list → null Params.
        const string yaml = @"schema: 1
controller: AllExcluded
basis: avatar-root
parameters:
  IsLocal: bool
  Work: { type: float, scratch: true }
";
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://excluded");
        ControllerEmit.Build(doc, out var r);
        Assert.IsNull(r.Params, "all params excluded (built-in + scratch) => null Params");
    }

    // ---- Direct blend tree with per-child weight + nested 1D --------------------------------------

    [Test]
    public void Direct_Tree_With_Nested_1D_RoundTrips()
    {
        const string yaml = @"schema: 1
controller: TreeTest
basis: avatar-root
parameters:
  W1: float
  W2: float
  Blend: float
clips:
  a: { set: { W1: 0 } }
  b: { set: { W1: 1 } }
  c: { set: { W2: 0 } }
layers:
  - name: L
    states:
      S:
        motion:
          tree: direct
          children:
            - weight: W1
              tree: 1d
              param: Blend
              children:
                - { clip: a, threshold: 0 }
                - { clip: b, threshold: 1 }
            - { weight: W2, clip: c }
    default: S
";
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://tree");
        ControllerEmit.Build(doc, out var r);

        var motion = State(RootSm(r), "S").motion as BlendTree;
        Assert.IsNotNull(motion, "state motion is a blend tree");
        Assert.AreEqual(BlendTreeType.Direct, motion.blendType);
        Assert.AreEqual(2, motion.children.Length);

        var c0 = motion.children[0];
        var c1 = motion.children[1];
        Assert.AreEqual("W1", c0.directBlendParameter);
        Assert.AreEqual("W2", c1.directBlendParameter);

        var nested = c0.motion as BlendTree;
        Assert.IsNotNull(nested, "first child is a nested tree");
        Assert.AreEqual(BlendTreeType.Simple1D, nested.blendType);
        Assert.AreEqual("Blend", nested.blendParameter);
        Assert.AreEqual(2, nested.children.Length);
        Assert.AreEqual(0f, nested.children[0].threshold, 1e-6f);
        Assert.AreEqual(1f, nested.children[1].threshold, 1e-6f);

        Assert.IsTrue(r.Trees.Count >= 2, "parent + nested trees are tracked");
    }

    // ---- Determinism -----------------------------------------------------------------------------

    [Test]
    public void Second_Build_Is_Structurally_Identical()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "mem://debounce");

        ControllerEmit.Build(doc, out var r1);
        var sm1 = RootSm(r1);
        // Snapshot BEFORE the second build (it resets the asset in place at the same scratch path,
        // destroying r1's sub-asset objects while keeping the top-level controller + its GUID).
        var snap1 = sm1.states.ToDictionary(cs => cs.state.name, cs => cs.position);

        ControllerEmit.Build(doc, out var r2);
        var sm2 = RootSm(r2);
        var snap2 = sm2.states.ToDictionary(cs => cs.state.name, cs => cs.position);

        Assert.AreEqual(snap1.Count, snap2.Count, "same state count");
        foreach (var kv in snap1)
        {
            Assert.IsTrue(snap2.ContainsKey(kv.Key), "state " + kv.Key + " present in both builds");
            Assert.AreEqual(kv.Value, snap2[kv.Key], "state " + kv.Key + " keeps the same grid position");
        }
    }
}
