using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Behavioral tests for ControllerDecompile — the AnimatorController -> AnimDocument read direction. Like
// ControllerEmitTests these touch the AssetDatabase (Walk reads sub-asset clips/behaviours off a persisted
// controller, and the round-trip emits one first). NOT run via MCP run_tests (it crashes the editor); run
// from the Unity Test Runner window or CI. TearDown removes the scratch folder each run.
public class ControllerDecompileTests
{
    private const string ScratchFolder = "Assets/Agent/Scratch/emit";

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(ScratchFolder))
            AssetDatabase.DeleteAsset(ScratchFolder);
    }

    private static float AsF(object o)
    {
        switch (o) { case float f: return f; case double d: return (float)d; case long l: return l; case int i: return i; case bool b: return b ? 1f : 0f; }
        return 0f;
    }

    // ---- Round-trip: debounce ---------------------------------------------------------------------

    [Test]
    public void Walk_Roundtrips_An_Emitted_Controller()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, out var emitted);
        var w = ControllerDecompile.Walk(emitted.Controller);

        Assert.AreEqual(src.Layers.Count, w.Doc.Layers.Count, "layer count");
        Assert.AreEqual(src.Parameters.Count, w.Doc.Parameters.Count, "param count (carrier _CompilerNull skipped)");
        Assert.IsFalse(w.Doc.Parameters.Any(p => p.Name == "_CompilerNull"), "reserved carrier param not decoded");

        var srcStates = src.Layers[0].Root.States.ConvertAll(s => s.Name);
        var gotStates = w.Doc.Layers[0].Root.States.ConvertAll(s => s.Name);
        CollectionAssert.AreEquivalent(srcStates, gotStates, "state names");

        Assert.AreEqual(0, w.Refusals.Count, "debounce is fully in-vocabulary");
        Assert.AreEqual(0, w.OrphanCount, "no orphans in a clean emit");

        // Default state.
        Assert.AreEqual("Idle", w.Doc.Layers[0].Root.DefaultState);

        // A transition's To + conditions: Idle -> Pending on RawInput is true.
        var idle = w.Doc.Layers[0].Root.States.First(s => s.Name == "Idle");
        var toPending = idle.Transitions.First(t => t.To == "Pending");
        Assert.AreEqual(1, toPending.When.Count);
        Assert.AreEqual("RawInput", toPending.When[0].Param);
        Assert.AreEqual(CondOp.Is, toPending.When[0].Op);
        Assert.AreEqual(1f, toPending.When[0].Value, 1e-6f);
        Assert.IsNull(toPending.ExitTime, "no exit time on Idle->Pending");

        // Pending -> Active is the unconditional exitTime=1 transition.
        var pending = w.Doc.Layers[0].Root.States.First(s => s.Name == "Pending");
        var toActive = pending.Transitions.First(t => t.To == "Active");
        Assert.AreEqual(0, toActive.When.Count, "timer-elapsed transition is unconditional");
        Assert.AreEqual(1f, toActive.ExitTime.Value, 1e-4f);

        // Active carries a driver { set: { Debounced: 1 } }.
        var active = w.Doc.Layers[0].Root.States.First(s => s.Name == "Active");
        Assert.AreEqual(1, active.Behaviours.Count);
        var drv = active.Behaviours[0];
        Assert.AreEqual("driver", drv.Kind);
        var sets = (Dictionary<string, object>)drv.Fields["set"];
        Assert.AreEqual(1f, AsF(sets["Debounced"]), 1e-6f);

        // Inline clips decoded: timer is seconds-only, hold_on is a Set.
        var timer = w.Doc.Clips.First(c => c.Name == "timer");
        Assert.AreEqual(0f, timer.Sets.Count + timer.Curves.Count, "timer is duration-only");
        Assert.AreEqual(0.2f, timer.Seconds.Value, 1e-3f);
        var hold = w.Doc.Clips.First(c => c.Name == "hold_on");
        Assert.AreEqual(1f, hold.Sets["Debounced"], 1e-6f);
    }

    // ---- Round-trip: nesting + a non-driver behaviour ---------------------------------------------

    private const string NestedDoc =
        "schema: 1\ncontroller: NestedRT_Fx\nbasis: avatar-root\nrole: fx\n" +
        "parameters:\n  P: { type: bool }\n" +
        "layers:\n  - name: L\n" +
        "    states:\n      Idle:\n        motion: ~\n" +
        "        behaviours:\n          - tracking: { head: animation, leftHand: tracking }\n" +
        "        transitions:\n          - { to: Sub/A, when: [ P is true ] }\n" +
        "    machines:\n      Sub:\n        states:\n          A:\n            motion: ~\n            transitions:\n              - { to: B, when: [ P is false ] }\n          B: { motion: ~ }\n        default: A\n" +
        "    entry:\n      - { to: Sub, when: [ P is true ] }\n" +
        "    default: Idle\n";

    [Test]
    public void Walk_Roundtrips_Nested_With_NonDriver_Behaviour()
    {
        var src = AnimatorSchemaYaml.Parse(NestedDoc, "test");
        ControllerEmit.Build(src, out var emitted);
        var w = ControllerDecompile.Walk(emitted.Controller);

        Assert.AreEqual(0, w.Refusals.Count, "nested doc is fully in-vocabulary");
        var root = w.Doc.Layers[0].Root;

        // Sub-machine decoded.
        Assert.AreEqual(1, root.Machines.Count, "one sub-machine");
        var sub = root.Machines[0];
        Assert.AreEqual("Sub", sub.Name);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, sub.Machine.States.ConvertAll(s => s.Name));
        Assert.AreEqual("A", sub.Machine.DefaultState);

        // Cross-machine addressing: Idle -> Sub/A (slash-qualified).
        var idle = root.States.First(s => s.Name == "Idle");
        Assert.AreEqual("Sub/A", idle.Transitions[0].To, "cross-machine target is a slash path from the layer root");

        // Same-machine addressing: A -> B (bare).
        var a = sub.Machine.States.First(s => s.Name == "A");
        Assert.AreEqual("B", a.Transitions[0].To, "same-machine target is bare");

        // Entry ladder into the sub-machine.
        Assert.AreEqual(1, root.EntryLadder.Count);
        Assert.AreEqual("Sub", root.EntryLadder[0].To);

        // The non-driver behaviour round-trips its Fields keyed by the camelCase tokens.
        var trk = idle.Behaviours.First(b => b.Kind == "tracking");
        Assert.AreEqual("animation", (string)trk.Fields["head"]);
        Assert.AreEqual("tracking", (string)trk.Fields["leftHand"]);
        Assert.IsFalse(trk.Fields.ContainsKey("rightHand"), "untouched channel (NoChange) is not emitted");
    }

    // ---- Orphan counting --------------------------------------------------------------------------

    [Test]
    public void Walk_Counts_Orphan_SubAsset_Without_Emitting_It()
    {
        var src = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        ControllerEmit.Build(src, out var emitted);
        string path = AssetDatabase.GetAssetPath(emitted.Controller);

        // Attach an unreferenced clip sub-asset: reachable from no layer state machine.
        var orphan = new AnimationClip { name = "ORPHAN", hideFlags = HideFlags.HideInHierarchy };
        AssetDatabase.AddObjectToAsset(orphan, emitted.Controller);
        AssetDatabase.SaveAssets();

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        var w = ControllerDecompile.Walk(controller);

        Assert.AreEqual(1, w.OrphanCount, "the unreferenced clip is counted as an orphan");
        Assert.IsFalse(w.Doc.Clips.Any(c => c.Name == "ORPHAN"), "orphan is not emitted into the Doc");
    }

    // ---- Item 1: a blend-tree child's dangling motion ref is decoded, not dropped -----------------

    [Test]
    public void Walk_TreeChild_Dangling_Ref_Is_Marked_Unresolved_And_ReEmits()
    {
        // Emit a controller whose tree child references a real external clip, then delete that clip so the
        // child's m_Motion becomes a dangling guid ref (an imported-controller reality). Walk must decode it
        // to GuidRef{Unresolved} + list the guid — NOT silently drop it (the pre-fix bug) — and re-emitting
        // the decoded doc must round-trip the unresolved marker (ControllerEmit tolerates it as a null motion).
        if (!AssetDatabase.IsValidFolder("Assets/Agent")) AssetDatabase.CreateFolder("Assets", "Agent");
        if (!AssetDatabase.IsValidFolder("Assets/Agent/Scratch")) AssetDatabase.CreateFolder("Assets/Agent", "Scratch");
        if (!AssetDatabase.IsValidFolder(ScratchFolder)) AssetDatabase.CreateFolder("Assets/Agent/Scratch", "emit");
        string clipPath = ScratchFolder + "/dangle_child.anim";
        var extClip = new AnimationClip { name = "dangle_child" };
        AnimationUtility.SetEditorCurve(extClip, EditorCurveBinding.FloatCurve("", typeof(Animator), "Blend"),
            AnimationCurve.Constant(0f, 0.1f, 0f));
        AssetDatabase.CreateAsset(extClip, clipPath);

        var doc = new AnimDocument { Schema = 1, ControllerName = "TreeDangle_Fx" };
        doc.Parameters.Add(new ParamSpec { Name = "Blend", Type = AnimParamType.Float });
        var layer = new Layer { Name = "L" };
        var owner = new State { Name = "Owner" };
        var tree = new BlendTreeSpec { Kind = TreeKind.OneD, Param = "Blend" };
        tree.Children.Add(new TreeChild { Motion = new MotionRef { RefPath = clipPath }, Threshold = 0f });
        owner.Motion = new MotionRef { Tree = tree };
        layer.Root.States.Add(owner);
        layer.Root.DefaultState = "Owner";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        AssetDatabase.DeleteAsset(clipPath); // now the tree child's ref dangles
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);

        var w = ControllerDecompile.Walk(controller);
        var child = w.Doc.Layers[0].Root.States.First(s => s.Name == "Owner").Motion.Tree.Children[0];
        Assert.IsNotNull(child.Motion, "the dangling child motion is decoded, not dropped");
        Assert.IsNotNull(child.Motion.RefGuid, "decoded as a guid ref");
        Assert.IsTrue(child.Motion.RefGuid.Unresolved, "marked unresolved");
        Assert.AreEqual(1, w.UnresolvedGuids.Count, "the child's guid is listed");

        // Re-emit the decoded doc: ControllerEmit preserves the unresolved marker (null motion + advisory),
        // attributed to the owning state — proving the marker survives decode -> re-emit.
        ControllerEmit.Build(w.Doc, out var r2);
        var reTree = FirstState(r2, "Owner").motion as BlendTree;
        Assert.IsNotNull(reTree);
        Assert.IsNull(reTree.children[0].motion, "re-emitted child motion is null (tolerated unresolved ref)");
        Assert.AreEqual(1, r2.UnresolvedRefs.Count, "re-emit records the unresolved ref");
        Assert.AreEqual("Owner", r2.UnresolvedRefs[0].state, "attributed to the owning state");
    }

    private static AnimatorState FirstState(ControllerEmit.EmitResult r, string name) =>
        r.Controller.layers[0].stateMachine.states.First(cs => cs.state.name == name).state;

    // ---- Item 3: round-trip the four behaviour kinds with no dedicated test ------------------------

    [Test]
    public void Walk_Roundtrips_PlayableLayer_PoseSpace_LayerControl_PlayAudio_Fields()
    {
        const string yaml =
            "schema: 1\ncontroller: FourBhv_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n" +
            "          - playableLayer: { layer: fx, goalWeight: 1, blendDuration: 0.25 }\n" +
            "          - poseSpace: { enterPoseSpace: true, fixedDelay: false, delayTime: 0.5 }\n" +
            "          - layerControl: { playable: gesture, layer: 3, goalWeight: 0.5, blendDuration: 0.1 }\n" +
            "          - playAudio: { sourcePath: Audio/Src, playbackOrder: uniqueRandom, parameter: Idx, " +
            "volume: [ 0.8, 1.0 ], volumeApply: neverApply, pitch: [ 1, 1 ], pitchApply: alwaysApply, " +
            "loop: true, loopApply: applyIfStopped, clipsApply: alwaysApply, delaySeconds: 0.1, " +
            "playOnEnter: true, stopOnEnter: false, playOnExit: true, stopOnExit: false }\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);
        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.AreEqual(0, w.Refusals.Count, "all four kinds are in-vocabulary");
        var bhvs = w.Doc.Layers[0].Root.States.First(s => s.Name == "S").Behaviours;

        var pl = bhvs.First(b => b.Kind == "playableLayer").Fields;
        Assert.AreEqual("fx", (string)pl["layer"]);
        Assert.AreEqual(1f, AsF(pl["goalWeight"]), 1e-6f);
        Assert.AreEqual(0.25f, AsF(pl["blendDuration"]), 1e-6f);

        var ps = bhvs.First(b => b.Kind == "poseSpace").Fields;
        Assert.AreEqual(true, (bool)ps["enterPoseSpace"]);
        Assert.AreEqual(false, (bool)ps["fixedDelay"]);
        Assert.AreEqual(0.5f, AsF(ps["delayTime"]), 1e-6f);

        var lc = bhvs.First(b => b.Kind == "layerControl").Fields;
        Assert.AreEqual("gesture", (string)lc["playable"]);
        Assert.AreEqual(3, (int)lc["layer"]);
        Assert.AreEqual(0.5f, AsF(lc["goalWeight"]), 1e-6f);
        Assert.AreEqual(0.1f, AsF(lc["blendDuration"]), 1e-6f);

        var pa = bhvs.First(b => b.Kind == "playAudio").Fields;
        Assert.AreEqual("Audio/Src", (string)pa["sourcePath"]);
        Assert.AreEqual("uniqueRandom", (string)pa["playbackOrder"]);
        Assert.AreEqual("Idx", (string)pa["parameter"]);
        var vol = (List<object>)pa["volume"];
        Assert.AreEqual(0.8f, AsF(vol[0]), 1e-6f);
        Assert.AreEqual(1.0f, AsF(vol[1]), 1e-6f);
        Assert.AreEqual("neverApply", (string)pa["volumeApply"]);
        Assert.AreEqual("alwaysApply", (string)pa["pitchApply"]);
        Assert.AreEqual("applyIfStopped", (string)pa["loopApply"]);
        Assert.AreEqual("alwaysApply", (string)pa["clipsApply"]);
        Assert.AreEqual(true, (bool)pa["loop"]);
        Assert.AreEqual(0.1f, AsF(pa["delaySeconds"]), 1e-6f);
        Assert.AreEqual(true, (bool)pa["playOnEnter"]);
        Assert.AreEqual(false, (bool)pa["stopOnEnter"]);
        Assert.AreEqual(true, (bool)pa["playOnExit"]);
        Assert.AreEqual(false, (bool)pa["stopOnExit"]);
    }

    // ---- Item 4: a Set clip authored with `seconds:` keeps its length across decode ----------------

    [Test]
    public void Walk_Set_Clip_With_Seconds_Recovers_Length()
    {
        var doc = new AnimDocument { Schema = 1, ControllerName = "SetSeconds_Fx" };
        doc.Parameters.Add(new ParamSpec { Name = "P", Type = AnimParamType.Float });
        var clip = new ClipSpec { Name = "c", Seconds = 0.5f };
        clip.Sets["P"] = 1f;
        doc.Clips.Add(clip);
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "S", Motion = new MotionRef { Clip = "c" } });
        layer.Root.DefaultState = "S";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var emitted);

        var w = ControllerDecompile.Walk(emitted.Controller);
        var c2 = w.Doc.Clips.First(x => x.Name == "c");
        Assert.AreEqual(0.5f, c2.Seconds.Value, 1e-3f, "the explicit length is recovered from the constant curve");
        Assert.AreEqual(1f, c2.Sets["P"], 1e-6f);

        // Re-emit: the length survives (would collapse to MinClipLength without the recovery).
        ControllerEmit.Build(w.Doc, out var r2);
        Assert.AreEqual(0.5f, r2.Clips["c"].length, 1e-3f, "re-emitted clip keeps the declared length");
    }

    [Test]
    public void Walk_Plain_Set_Clip_Leaves_Seconds_Null()
    {
        // A Set clip with NO authored seconds sits at MinClipLength — the recovery must NOT invent a seconds.
        var doc = new AnimDocument { Schema = 1, ControllerName = "PlainSet_Fx" };
        doc.Parameters.Add(new ParamSpec { Name = "P", Type = AnimParamType.Float });
        var clip = new ClipSpec { Name = "c" };
        clip.Sets["P"] = 1f;
        doc.Clips.Add(clip);
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "S", Motion = new MotionRef { Clip = "c" } });
        layer.Root.DefaultState = "S";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var emitted);

        var w = ControllerDecompile.Walk(emitted.Controller);
        var c2 = w.Doc.Clips.First(x => x.Name == "c");
        Assert.IsFalse(c2.Seconds.HasValue, "a plain Set clip (MinClipLength) does not gain a spurious seconds");
    }

    // ---- Task 7 item 1: mixed WD hoists to a modal layer policy + minority overrides, re-emits the same mix --

    [Test]
    public void Walk_MixedWD_Hoists_Modal_Policy_And_ReEmits_Same_Mix()
    {
        // Two states, one WD-true one WD-false: a 1/1 tie, resolved to the stated tie-break (prefer true).
        var doc = new AnimDocument { Schema = 1, ControllerName = "MixedWD_Fx" };
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "A", Motion = null, WriteDefaults = true });
        layer.Root.States.Add(new State { Name = "B", Motion = null, WriteDefaults = false });
        layer.Root.DefaultState = "A";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var emitted);

        var w = ControllerDecompile.Walk(emitted.Controller);
        var L = w.Doc.Layers[0];
        Assert.AreEqual(true, L.WriteDefaults, "modal WD policy on a 1/1 tie prefers true");

        var a = L.Root.States.First(s => s.Name == "A");
        var b = L.Root.States.First(s => s.Name == "B");
        Assert.IsFalse(a.WriteDefaults.HasValue, "majority state's override is cleared (inherits the layer policy)");
        Assert.AreEqual(false, b.WriteDefaults, "minority state keeps an explicit override");

        // Re-emit the decoded doc: the per-state WD mix is reproduced exactly.
        ControllerEmit.Build(w.Doc, out var r2);
        Assert.IsTrue(FirstState(r2, "A").writeDefaultValues, "A re-emits WD true (from layer policy)");
        Assert.IsFalse(FirstState(r2, "B").writeDefaultValues, "B re-emits WD false (from its override)");
    }

    // ---- Task 7 item 1 / acceptance #5: a uniform-WD layer hoists to a policy with ZERO overrides ------------

    [Test]
    public void Walk_UniformWD_Hoists_Policy_With_No_Overrides()
    {
        var doc = new AnimDocument { Schema = 1, ControllerName = "UniformWD_Fx" };
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "A", WriteDefaults = false });
        layer.Root.States.Add(new State { Name = "B", WriteDefaults = false });
        layer.Root.DefaultState = "A";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var emitted);

        var w = ControllerDecompile.Walk(emitted.Controller);
        var L = w.Doc.Layers[0];
        Assert.AreEqual(false, L.WriteDefaults, "uniform WD-false hoists to a false layer policy");
        Assert.IsTrue(L.Root.States.All(s => !s.WriteDefaults.HasValue), "no per-state overrides remain");

        ControllerEmit.Build(w.Doc, out var r2);
        Assert.IsFalse(FirstState(r2, "A").writeDefaultValues);
        Assert.IsFalse(FirstState(r2, "B").writeDefaultValues);
    }

    // ---- Task 7 item 2: timeParameterActive + empty timeParameter -> unbound motion time + a Note ------------

    [Test]
    public void Walk_Empty_TimeParameter_Normalizes_To_Null_With_Note()
    {
        var c = new AnimatorController { name = "EmptyTP_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        st.timeParameterActive = true;
        st.timeParameter = ""; // the SDK HandsLayer2 template shape

        var w = ControllerDecompile.Walk(c);
        var s = w.Doc.Layers[0].Root.States.First(x => x.Name == "S");
        Assert.IsNull(s.MotionTimeParam, "empty timeParameter is not bound");
        Assert.AreEqual(0, w.Refusals.Count, "an empty timeParameter is tolerated, never refused");
        Assert.IsTrue(w.Notes.Any(n => n.Contains("timeParameter")), "a Note records the normalization");
        Object.DestroyImmediate(c);
    }

    [Test]
    public void Walk_NonEmpty_TimeParameter_Binds_Without_A_Note()
    {
        // A real motion-time binding (the vendor Shinano/CasualStroll scrubber shape) is decoded verbatim —
        // NOT the empty-param normalization, so no Note is recorded for it.
        var c = new AnimatorController { name = "MotionTimeTP_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        st.timeParameterActive = true;
        st.timeParameter = "MotionTime";

        var w = ControllerDecompile.Walk(c);
        var s = w.Doc.Layers[0].Root.States.First(x => x.Name == "S");
        Assert.AreEqual("MotionTime", s.MotionTimeParam, "a non-empty timeParameter is bound verbatim");
        Assert.AreEqual(0, w.Refusals.Count, "a real motion-time binding is in-vocabulary");
        Assert.IsFalse(w.Notes.Any(n => n.Contains("timeParameter")), "no normalization Note for a real binding");
        Object.DestroyImmediate(c);
    }

    // ---- Task 7 item 3: sibling states differing only by trailing whitespace -> a located Refusal ------------

    [Test]
    public void Walk_Whitespace_Sibling_States_Refuse_Naming_Both()
    {
        var c = new AnimatorController { name = "WsCollide_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        sm.AddState("S");
        sm.AddState("S "); // trailing space

        var w = ControllerDecompile.Walk(c);
        Assert.AreEqual(1, w.Refusals.Count(r => r.Contains("whitespace")), "one whitespace-collision refusal");
        var refusal = w.Refusals.First(r => r.Contains("whitespace"));
        StringAssert.Contains("'S'", refusal, "names the first sibling");
        StringAssert.Contains("'S '", refusal, "names the second sibling");
        // Both states are still decoded — never silently collapsed/dedup'd.
        Assert.AreEqual(2, w.Doc.Layers[0].Root.States.Count, "both colliding states remain decoded");
        Object.DestroyImmediate(c);
    }

    // ---- Task 7 item 4a: a layer with a null state machine -> a located Refusal (not an NRE) -----------------

    [Test]
    public void Walk_Null_StateMachine_Refuses()
    {
        var c = new AnimatorController { name = "NullSM_Fx" };
        c.layers = new[] { new AnimatorControllerLayer { name = "L", defaultWeight = 1f, stateMachine = null } };

        var w = ControllerDecompile.Walk(c); // must NOT throw
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("no state machine")), "null state machine -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Task 7 item 4b: an empty inline clip (zero bindings) -> a located Refusal (not silently empty) ------

    [Test]
    public void Walk_Empty_Inline_Clip_Refuses()
    {
        var c = new AnimatorController { name = "EmptyClip_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        st.motion = new AnimationClip { name = "empty" }; // no curve bindings

        var w = ControllerDecompile.Walk(c);
        // Assert on the message text, not the clip NAME ("empty") — a rename must not satisfy it.
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("zero curve bindings")),
            "an inline clip with zero bindings -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Task 7 item 4c: a null playAudio clip entry -> a located Refusal (not an NRE) -----------------------

    [Test]
    public void Walk_Null_PlayAudio_Clip_Refuses()
    {
        // Emit a playAudio behaviour (ControllerEmit adds the SMB on a persisted controller), then null out its
        // Clips entry to model a since-deleted AudioClip — the reachable-import reality this refusal guards.
        const string yaml =
            "schema: 1\ncontroller: NullAudio_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n" +
            "          - playAudio: { sourcePath: Audio/Src, playbackOrder: uniqueRandom, parameter: Idx, " +
            "volume: [ 0.8, 1.0 ], volumeApply: neverApply, pitch: [ 1, 1 ], pitchApply: alwaysApply, " +
            "loop: true, loopApply: applyIfStopped, clipsApply: alwaysApply, delaySeconds: 0.1, " +
            "playOnEnter: true, stopOnEnter: false, playOnExit: true, stopOnExit: false }\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);
        var st = emitted.Controller.layers[0].stateMachine.states[0].state;
        var pa = (VRC.SDKBase.VRC_AnimatorPlayAudio)st.behaviours[0];
        pa.Clips = new AudioClip[] { null };
        EditorUtility.SetDirty(pa);

        var w = ControllerDecompile.Walk(emitted.Controller); // must NOT throw
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("playAudio") && r.Contains("null clip")),
            "a null playAudio clip entry -> located refusal");
    }
}
