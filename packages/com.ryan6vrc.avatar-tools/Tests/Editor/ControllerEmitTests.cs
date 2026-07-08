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
        // The carrier animates a scratch ANIMATOR parameter (path="", type=Animator), NOT a fake GameObject
        // path — an animator-property binding resolves against any avatar root, so AnimatorLint's broken-binding
        // rule stays clean when the emitted controller is linted against a real avatar.
        Assert.AreEqual("", bindings[0].path, "animator-param carrier, not a scene path");
        Assert.AreEqual(typeof(Animator), bindings[0].type);
        Assert.AreEqual("_CompilerNull", bindings[0].propertyName);

        // The carrier param is DECLARED on the controller (so the curve targets a real parameter, keeping the
        // undeclaredParam rule clean)...
        Assert.IsTrue(r.Controller.parameters.Any(p => p.name == "_CompilerNull" && p.type == AnimatorControllerParameterType.Float),
            "carrier param declared on the controller");
        // ...but kept OUT of the emitted VRCExpressionParameters (it is not the controller's own to expose).
        if (r.Params != null)
            Assert.IsFalse(r.Params.parameters.Any(p => p.name == "_CompilerNull"),
                "carrier param not listed in the emitted VRCExpressionParameters");
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

    // ---- fail-loud + length ----------------------------------------------------------------------

    [Test]
    public void Missing_AvatarMask_Fails_Loud()
    {
        var doc = new AnimDocument { Schema = 1, ControllerName = "T_Mask" };
        doc.Parameters.Add(new ParamSpec { Name = "P", Type = AnimParamType.Float });
        var layer = new Layer { Name = "L", Mask = "Assets/DoesNotExist_9f8e7d.mask" };
        layer.Root.States.Add(new State { Name = "S" });
        layer.Root.DefaultState = "S";
        doc.Layers.Add(layer);
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("avatarMask", ex.Message);
    }

    [Test]
    public void Seconds_Extends_Keyframed_Curve_Length()
    {
        var doc = new AnimDocument { Schema = 1, ControllerName = "T_Len" };
        var clip = new ClipSpec { Name = "c", Seconds = 1.0f };
        clip.Curves.Add(new CurveSpec { Binding = "Prop/Renderer.enabled", Keys = { new Keyframe2(0f, 0f), new Keyframe2(0.2f, 1f) } });
        doc.Clips.Add(clip);
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "S", Motion = new MotionRef { Clip = "c" } });
        layer.Root.DefaultState = "S";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var r);
        Assert.AreEqual(1.0f, r.Clips["c"].length, 1e-3f, "seconds declares the length by holding the last key");
    }

    [Test]
    public void Seconds_Shorter_Than_Last_Key_Fails()
    {
        var doc = new AnimDocument { Schema = 1, ControllerName = "T_Short" };
        var clip = new ClipSpec { Name = "c", Seconds = 0.1f };
        clip.Curves.Add(new CurveSpec { Binding = "Prop/Renderer.enabled", Keys = { new Keyframe2(0f, 0f), new Keyframe2(0.2f, 1f) } });
        doc.Clips.Add(clip);
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("shorter", ex.Message);
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
            - directWeight: W1
              tree: 1d
              param: Blend
              children:
                - { clip: a, threshold: 0 }
                - { clip: b, threshold: 1 }
            - { directWeight: W2, clip: c }
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

    // ---- Nested sub-machines ---------------------------------------------------------------------

    [Test]
    public void Emit_Nested_SubMachine_Produces_Child_StateMachine()
    {
        // Bare `to:`/`default:` resolve in their OWN machine's scope; a cross-machine target is a
        // slash-qualified path from the layer root — Idle→A inside Sub is `Sub/A`. Entry `to: Sub` stays
        // bare because Sub is in the root machine's own scope.
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: Nested_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    states:\n      Idle:\n        motion: ~\n        transitions:\n          - { to: Sub/A, when: [ P is true ] }\n" +
            "    machines:\n      Sub:\n        states:\n          A: { motion: ~ }\n        default: A\n" +
            "    entry:\n      - { to: Sub, when: [ P is true ] }\n" +
            "    default: Idle\n", "test");
        ControllerEmit.Build(doc, out var r);
        var sm = r.Controller.layers[0].stateMachine;
        Assert.AreEqual(1, sm.stateMachines.Length, "one child state machine emitted");
        var sub = sm.stateMachines[0].stateMachine;
        Assert.AreEqual("Sub", sub.name);
        var a = sub.states.First(cs => cs.state.name == "A").state;
        Assert.IsNotNull(a, "declared sub-machine state A is present");
        // Idle's `Sub/A` path target resolves into the child machine's A state.
        var idle = State(sm, "Idle");
        Assert.IsTrue(idle.transitions.Any(t => t.destinationState == a), "Idle wires to Sub/A across the nesting");
        // Entry `to: Sub` wires into the sub-machine.
        Assert.AreEqual(1, sm.entryTransitions.Length);
        Assert.AreEqual(sub, sm.entryTransitions[0].destinationStateMachine, "entry wires into the sub-machine");
    }

    [Test]
    public void SubMachine_As_Default_Wires_Unconditional_Entry()
    {
        // `default:` naming a SUB-MACHINE (not a state) → an unconditional (no-condition) entry transition
        // into that sub-machine, added after any entry ladder so it is the catch-all.
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: SubDefault_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    machines:\n      Sub:\n        states:\n          A: { motion: ~ }\n        default: A\n" +
            "    default: Sub\n", "test");
        ControllerEmit.Build(doc, out var r);
        var sm = r.Controller.layers[0].stateMachine;
        var sub = sm.stateMachines[0].stateMachine;
        // The mechanism is an unconditional entry transition into the sub-machine — NOT a defaultState we
        // set. (Unity's defaultState getter resolves through to the child's own default when the root has no
        // direct states, so it is not a reliable probe here; the entry transition is.)
        Assert.AreEqual(0, sm.states.Length, "root machine has no direct states of its own");
        Assert.AreEqual(1, sm.entryTransitions.Length, "one entry transition for the sub-machine default");
        Assert.AreEqual(sub, sm.entryTransitions[0].destinationStateMachine);
        Assert.AreEqual(0, sm.entryTransitions[0].conditions.Length, "the default entry is unconditional");
    }

    [Test]
    public void Duplicate_State_Name_Across_Sibling_Machines_Wires_Each_Own_Transition()
    {
        // REGRESSION GUARD for the layer-global-index bug: two sibling sub-machines each declare a state "S"
        // whose transition targets a DIFFERENT sibling-local state. Under a global bare-name index the
        // last-emitted "S" would win and both transitions would attach to it / resolve to the wrong target.
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: Dup_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    machines:\n" +
            "      M1:\n        states:\n          S:\n            motion: ~\n            transitions:\n              - { to: X, when: [ P is true ] }\n          X: { motion: ~ }\n        default: S\n" +
            "      M2:\n        states:\n          S:\n            motion: ~\n            transitions:\n              - { to: Y, when: [ P is true ] }\n          Y: { motion: ~ }\n        default: S\n" +
            "    default: M1\n", "test");
        ControllerEmit.Build(doc, out var r);
        var sm = r.Controller.layers[0].stateMachine;
        var m1 = sm.stateMachines.First(cs => cs.stateMachine.name == "M1").stateMachine;
        var m2 = sm.stateMachines.First(cs => cs.stateMachine.name == "M2").stateMachine;

        var s1 = m1.states.First(cs => cs.state.name == "S").state;
        var x = m1.states.First(cs => cs.state.name == "X").state;
        var s2 = m2.states.First(cs => cs.state.name == "S").state;
        var y = m2.states.First(cs => cs.state.name == "Y").state;

        // M1's S is a DIFFERENT object than M2's S, and each carries exactly its own transition.
        Assert.AreNotSame(s1, s2, "each machine has its own distinct S state");
        Assert.AreEqual(1, s1.transitions.Length);
        Assert.AreEqual(x, s1.transitions[0].destinationState, "M1's S wires to M1's X");
        Assert.AreEqual(1, s2.transitions.Length);
        Assert.AreEqual(y, s2.transitions[0].destinationState, "M2's S wires to M2's Y");
    }

    [Test]
    public void Qualified_Path_Resolves_Into_Nested_SubMachine()
    {
        // A deep slash-qualified path `A/inner` from the layer root walks sub-machine A, then its state
        // `inner`. Top-level state Idle → `A/inner`.
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: Path_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    states:\n      Idle:\n        motion: ~\n        transitions:\n          - { to: A/inner, when: [ P is true ] }\n" +
            "    machines:\n      A:\n        states:\n          inner: { motion: ~ }\n        default: inner\n" +
            "    default: Idle\n", "test");
        ControllerEmit.Build(doc, out var r);
        var sm = r.Controller.layers[0].stateMachine;
        var a = sm.stateMachines.First(cs => cs.stateMachine.name == "A").stateMachine;
        var inner = a.states.First(cs => cs.state.name == "inner").state;
        var idle = State(sm, "Idle");
        Assert.AreEqual(1, idle.transitions.Length);
        Assert.AreEqual(inner, idle.transitions[0].destinationState, "Idle resolves A/inner into the nested machine");
    }

    // Fail-loud is the change's core value and nothing upstream backstops it — ResolveName's throws are the
    // only guard against a mis-scoped/typo'd target. Pin both scope rules.

    [Test]
    public void Bare_Target_Does_Not_Leak_Across_Sibling_Scopes()
    {
        // Idle (root scope) targets bare `A`, which exists ONLY inside sub-machine Sub — a bare name must NOT
        // reach across scopes (the cross-machine ref would need the qualified path `Sub/A`).
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: BadBare_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    states:\n      Idle:\n        motion: ~\n        transitions:\n          - { to: A, when: [ P is true ] }\n" +
            "    machines:\n      Sub:\n        states:\n          A: { motion: ~ }\n        default: A\n" +
            "    default: Idle\n", "test");
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("not found in machine", ex.Message);
    }

    [Test]
    public void Qualified_Path_Through_A_State_Fails_Loud()
    {
        // `Idle/foo` — the intermediate segment `Idle` is a STATE, not a sub-machine, so the path cannot be
        // walked.
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: BadPath_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    states:\n      Idle:\n        motion: ~\n        transitions:\n          - { to: Idle/foo, when: [ P is true ] }\n" +
            "    default: Idle\n", "test");
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("is not a sub-machine", ex.Message);
    }

    // ---- Behaviours: the six non-driver VRC SMB kinds --------------------------------------------

    private static AnimatorState SingleStateWithBehaviours(string kindLine)
    {
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: Bhv_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - " + kindLine + "\n" +
            "    default: S\n", "test");
        ControllerEmit.Build(doc, out var r);
        return State(RootSm(r), "S");
    }

    [Test]
    public void Emit_Tracking_Behaviour_Sets_Channels()
    {
        var st = SingleStateWithBehaviours("tracking: { head: animation, leftHand: tracking }");
        var smb = st.behaviours[0] as VRCAnimatorTrackingControl;
        Assert.IsNotNull(smb, "tracking SMB emitted");
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType.Animation, smb.trackingHead);
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType.Tracking, smb.trackingLeftHand);
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType.NoChange, smb.trackingRightHand,
            "an unmentioned channel keeps the SDK default (NoChange)");
    }

    [Test]
    public void Emit_PlayableLayer_Behaviour_Sets_Layer_And_Weight()
    {
        var st = SingleStateWithBehaviours("playableLayer: { layer: fx, goalWeight: 1, blendDuration: 0.25 }");
        var smb = st.behaviours[0] as VRCPlayableLayerControl;
        Assert.IsNotNull(smb, "playableLayer SMB emitted");
        Assert.AreEqual(VRC.SDKBase.VRC_PlayableLayerControl.BlendableLayer.FX, smb.layer);
        Assert.AreEqual(1f, smb.goalWeight, 1e-6f);
        Assert.AreEqual(0.25f, smb.blendDuration, 1e-6f);
    }

    [Test]
    public void Emit_Locomotion_Behaviour_Sets_Disable()
    {
        var st = SingleStateWithBehaviours("locomotion: { disableLocomotion: true }");
        var smb = st.behaviours[0] as VRCAnimatorLocomotionControl;
        Assert.IsNotNull(smb, "locomotion SMB emitted");
        Assert.IsTrue(smb.disableLocomotion);
    }

    [Test]
    public void Emit_PoseSpace_Behaviour_Sets_Fields()
    {
        var st = SingleStateWithBehaviours("poseSpace: { enterPoseSpace: true, delayTime: 0.5 }");
        var smb = st.behaviours[0] as VRCAnimatorTemporaryPoseSpace;
        Assert.IsNotNull(smb, "poseSpace SMB emitted");
        Assert.IsTrue(smb.enterPoseSpace);
        Assert.AreEqual(0.5f, smb.delayTime, 1e-6f);
    }

    [Test]
    public void Emit_LayerControl_Behaviour_Sets_Playable_LayerIndex_And_Weight()
    {
        var st = SingleStateWithBehaviours("layerControl: { playable: gesture, layer: 3, goalWeight: 0.5, blendDuration: 0.1 }");
        var smb = st.behaviours[0] as VRCAnimatorLayerControl;
        Assert.IsNotNull(smb, "layerControl SMB emitted");
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorLayerControl.BlendableLayer.Gesture, smb.playable);
        Assert.AreEqual(3, smb.layer, "the integer layer index");
        Assert.AreEqual(0.5f, smb.goalWeight, 1e-6f);
        Assert.AreEqual(0.1f, smb.blendDuration, 1e-6f);
    }

    [Test]
    public void Emit_PlayAudio_Behaviour_Sets_Order_Flags_Range_And_ApplySettings()
    {
        var st = SingleStateWithBehaviours(
            "playAudio: { sourcePath: Audio/Src, playbackOrder: uniqueRandom, parameter: Idx, " +
            "volume: [ 0.8, 1.0 ], volumeApply: neverApply, pitch: [ 1, 1 ], pitchApply: alwaysApply, " +
            "loop: true, loopApply: applyIfStopped, clipsApply: alwaysApply, delaySeconds: 0.1, " +
            "playOnEnter: true, stopOnEnter: true, playOnExit: true, stopOnExit: true }");
        var smb = st.behaviours[0] as VRCAnimatorPlayAudio;
        Assert.IsNotNull(smb, "playAudio SMB emitted");
        Assert.AreEqual("Audio/Src", smb.SourcePath);
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorPlayAudio.Order.UniqueRandom, smb.PlaybackOrder);
        Assert.AreEqual("Idx", smb.ParameterName);
        Assert.AreEqual(0.8f, smb.Volume.x, 1e-6f);
        Assert.AreEqual(1.0f, smb.Volume.y, 1e-6f);
        Assert.AreEqual(1f, smb.Pitch.x, 1e-6f);
        Assert.AreEqual(1f, smb.Pitch.y, 1e-6f);
        // Each ApplySettings site decodes its token (the enum map's 3 members × 4 sites).
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorPlayAudio.ApplySettings.NeverApply, smb.VolumeApplySettings);
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorPlayAudio.ApplySettings.AlwaysApply, smb.PitchApplySettings);
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorPlayAudio.ApplySettings.ApplyIfStopped, smb.LoopApplySettings);
        Assert.AreEqual(VRC.SDKBase.VRC_AnimatorPlayAudio.ApplySettings.AlwaysApply, smb.ClipsApplySettings);
        Assert.IsTrue(smb.Loop);
        Assert.AreEqual(0.1f, smb.DelayInSeconds, 1e-6f);
        Assert.IsTrue(smb.PlayOnEnter);
        Assert.IsTrue(smb.StopOnEnter);
        Assert.IsTrue(smb.PlayOnExit);
        Assert.IsTrue(smb.StopOnExit);
    }

    [Test]
    public void PlayAudio_Volume_Wrong_Arity_Fails_Loud()
    {
        // AsVector2 requires exactly [min, max]; a single-element list is fail-loud.
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: BadVol_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - playAudio: { volume: [ 0.8 ] }\n" +
            "    default: S\n", "test");
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("playAudio.volume", ex.Message);
        StringAssert.Contains("exactly 2", ex.Message);
    }

    [Test]
    public void PlayAudio_Missing_Clip_Path_Fails_Loud()
    {
        // AsClips loads each path as an AudioClip; an unresolved path is fail-loud, naming the offending path.
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: BadClip_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - playAudio: { clips: [ Assets/DoesNotExist_7c6b5a.wav ] }\n" +
            "    default: S\n", "test");
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("Assets/DoesNotExist_7c6b5a.wav", ex.Message);
        StringAssert.Contains("not found", ex.Message);
    }

    [Test]
    public void Unknown_Behaviour_Field_In_Known_Kind_Fails_Loud()
    {
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: BadField_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - tracking: { nose: animation }\n" +
            "    default: S\n", "test");
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("nose", ex.Message);
        StringAssert.Contains("unknown channel", ex.Message);
    }

    [Test]
    public void Unknown_Behaviour_Kind_Fails_Loud()
    {
        // The parser accepts any single-key behaviour map; emit is the fail-loud gate on the kind.
        var doc = new AnimDocument { Schema = 1, ControllerName = "BadKind_Fx" };
        var layer = new Layer { Name = "L" };
        var s = new State { Name = "S" };
        s.Behaviours.Add(new Ryan6Vrc.AvatarTools.Editor.Behaviour { Kind = "teleport" });
        layer.Root.States.Add(s);
        layer.Root.DefaultState = "S";
        doc.Layers.Add(layer);
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => { ControllerEmit.Build(doc, out _); });
        StringAssert.Contains("teleport", ex.Message);
        StringAssert.Contains("unknown kind", ex.Message);
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

    // ---- Unresolved motion refs (Task 4) ---------------------------------------------------------
    // A ref flagged `unresolved: true` that does NOT resolve is tolerated: null motion + advisory record,
    // not a throw. A bare broken ref (no marker) stays fatal. An all-zero GUID never resolves. NOTE the guid
    // is QUOTED: an all-zero (all-digit) scalar parses as a YAML number, and the guid binder requires a string.

    [Test]
    public void Emit_Unresolved_Guid_Ref_Is_Null_Motion_Not_Throw()
    {
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: Dangle_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n" +
            "      S: { motion: { ref: { guid: \"00000000000000000000000000000000\", unresolved: true } } }\n" +
            "    default: S\n", "mem://dangle");

        ControllerEmit.EmitResult r = null;
        Assert.DoesNotThrow(() => ControllerEmit.Build(doc, out r));

        var s = State(RootSm(r), "S");
        Assert.IsNull(s.motion, "unresolved ref leaves the motion slot null");
        Assert.AreEqual(1, r.UnresolvedRefs.Count, "the unresolved ref is recorded on the result");
        Assert.AreEqual("S", r.UnresolvedRefs[0].state, "advisory names the owning state");
        Assert.AreEqual("00000000000000000000000000000000", r.UnresolvedRefs[0].guid, "verbatim GUID preserved");
    }

    [Test]
    public void Emit_Bare_Broken_Guid_Ref_Throws()
    {
        var doc = AnimatorSchemaYaml.Parse(
            "schema: 1\ncontroller: Dangle2_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n" +
            "      S: { motion: { ref: { guid: \"00000000000000000000000000000000\" } } }\n" +
            "    default: S\n", "mem://dangle2");

        Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.Build(doc, out _));
    }
}
