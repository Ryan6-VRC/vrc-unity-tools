using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Behavioral tests for ControllerDecompile — the AnimatorController -> AnimDocument read direction. Like
// ControllerEmitTests these touch the AssetDatabase (Walk reads sub-asset clips/behaviours off a persisted
// controller, and the round-trip emits one first). Run headless via tools/run-editmode-tests.ps1 (or the
// Test Runner window / CI); not via MCP run_tests — wrong venue (live editor). See docs/verify.md.
// TearDown removes the scratch folder each run.
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

    // ---- Review #1: driver ops that interleave change-types or repeat a (type,name) -> refusals -------------

    [Test]
    public void Walk_Driver_Interleaved_And_Duplicate_Ops_Refuse()
    {
        // The schema regroups an ordered driver list into name-keyed set/add/copy/random buckets — faithful
        // ONLY when the list is already bucket-ordered with no repeated (type,name). Emit a driver, then
        // overwrite its parameters with an interleaved + duplicated list the emitter itself never produces.
        const string yaml =
            "schema: 1\ncontroller: DrvOrder_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  X: float\n  Y: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - driver: { set: { X: 1 } }\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);
        var st = emitted.Controller.layers[0].stateMachine.states[0].state;
        var drv = (VRC.SDKBase.VRC_AvatarParameterDriver)st.behaviours[0];
        drv.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
        {
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set, name = "X", value = 1f },
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy, name = "Y", source = "X" },
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set, name = "X", value = 2f },
        };
        EditorUtility.SetDirty(drv);

        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("interleave")), "interleaved change-types -> refusal");
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("repeats operation")), "the duplicate Set X -> refusal");
    }

    // ---- Review #2: two DISTINCT embedded clips sharing a name -> a refusal (not a silent dedup) -----------

    [Test]
    public void Walk_Distinct_SameName_Embedded_Clips_Refuse()
    {
        EnsureScratch();
        var c = new AnimatorController { name = "DupClip_Fx" };
        AssetDatabase.CreateAsset(c, ScratchFolder + "/DupClip_Fx.controller");
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s1 = sm.AddState("S1");
        var s2 = sm.AddState("S2");
        s1.motion = AddEmbeddedClip(c, "dup", 1f);
        s2.motion = AddEmbeddedClip(c, "dup", 2f); // distinct instance, same name
        AssetDatabase.SaveAssets();

        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("dup") && r.Contains("DISTINCT")),
            "two distinct embedded clips sharing a name -> located refusal");
    }

    private static AnimationClip AddEmbeddedClip(AnimatorController c, string name, float v)
    {
        var clip = new AnimationClip { name = name, hideFlags = HideFlags.HideInHierarchy };
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "P"),
            AnimationCurve.Constant(0f, 0.1f, v));
        AssetDatabase.AddObjectToAsset(clip, c);
        return clip;
    }

    // ---- Review #3: a MIXED set+curve clip whose sets run past the last keyframe keeps its length ----------

    [Test]
    public void Walk_Mixed_Set_And_Curve_Recovers_Longer_Seconds()
    {
        EnsureScratch();
        var c = new AnimatorController { name = "MixedClip_Fx" };
        AssetDatabase.CreateAsset(c, ScratchFolder + "/MixedClip_Fx.controller");
        // Declare the two bindings as params so the decoded doc re-emits them as Animator-param curves.
        c.AddParameter("SetP", AnimatorControllerParameterType.Float);
        c.AddParameter("CurveP", AnimatorControllerParameterType.Float);
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        var clip = new AnimationClip { name = "mixed", hideFlags = HideFlags.HideInHierarchy };
        // A constant Set running to t=2.0 ...
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "SetP"),
            AnimationCurve.Constant(0f, 2.0f, 1f));
        // ... plus a keyframed curve whose last key is only at t=1.0.
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "CurveP"),
            new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1.0f, 1f)));
        AssetDatabase.AddObjectToAsset(clip, c);
        st.motion = clip;
        AssetDatabase.SaveAssets();

        var w = ControllerDecompile.Walk(c);
        var decoded = w.Doc.Clips.First(x => x.Name == "mixed");
        Assert.IsTrue(decoded.Seconds.HasValue, "the longer set length is recovered on a mixed clip");
        Assert.AreEqual(2.0f, decoded.Seconds.Value, 1e-3f);
        Assert.AreEqual(1, decoded.Sets.Count, "the constant binding decoded as a Set");
        Assert.AreEqual(1, decoded.Curves.Count, "the keyframed binding decoded as a Curve");

        // Re-emit: the clip keeps length 2.0 (would shrink to the 1.0 curve end without the recovery).
        ControllerEmit.Build(w.Doc, out var r2);
        Assert.AreEqual(2.0f, r2.Clips["mixed"].length, 1e-3f);
    }

    // ---- Review #4: exact-duplicate sibling names (states AND sub-machines) -> located refusals ------------

    [Test]
    public void Walk_Exact_Duplicate_Sibling_States_Refuse()
    {
        var c = new AnimatorController { name = "DupState_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        sm.AddState("S");
        var s2 = sm.AddState("Temp"); s2.name = "S"; // force an exact-duplicate raw name
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("identical sibling names") && r.Contains("states")),
            "two states named 'S' -> located refusal");
        Assert.AreEqual(2, w.Doc.Layers[0].Root.States.Count, "both are still decoded (never collapsed)");
        Object.DestroyImmediate(c);
    }

    [Test]
    public void Walk_Exact_Duplicate_Sibling_SubMachines_Refuse()
    {
        var c = new AnimatorController { name = "DupSm_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        sm.AddStateMachine("M");
        var m2 = sm.AddStateMachine("Temp"); m2.name = "M"; // force an exact-duplicate raw name
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("identical sibling names") && r.Contains("sub-machines")),
            "two sub-machines named 'M' -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review #5: two slots dangling to the SAME missing clip each recover the guid ---------------------

    [Test]
    public void Walk_Two_States_Same_Dangling_Clip_Both_Recover_The_Guid()
    {
        EnsureScratch();
        string clipPath = ScratchFolder + "/shared_src.anim";
        var ext = new AnimationClip { name = "shared_src" };
        AnimationUtility.SetEditorCurve(ext, EditorCurveBinding.FloatCurve("", typeof(Animator), "Blend"),
            AnimationCurve.Constant(0f, 0.1f, 0f));
        AssetDatabase.CreateAsset(ext, clipPath);
        string realGuid = AssetDatabase.AssetPathToGUID(clipPath);

        var doc = new AnimDocument { Schema = 1, ControllerName = "SharedDangle_Fx" };
        doc.Parameters.Add(new ParamSpec { Name = "Blend", Type = AnimParamType.Float });
        var layer = new Layer { Name = "L" };
        layer.Root.States.Add(new State { Name = "A", Motion = new MotionRef { RefPath = clipPath } });
        layer.Root.States.Add(new State { Name = "B", Motion = new MotionRef { RefPath = clipPath } });
        layer.Root.DefaultState = "A";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var emitted);
        string ctrlPath = AssetDatabase.GetAssetPath(emitted.Controller);

        // Rewrite BOTH states' motion refs to a guid that resolves to NOTHING — the imported "missing vendor
        // asset" reality. (DeleteAsset is unreliable here: its guid->path cache lingers within a synchronous
        // test, so the deleted guid still "resolves" and nothing reads as dangling.)
        const string missingGuid = "0123456789abcdef0123456789abcdef";
        Assert.IsEmpty(AssetDatabase.GUIDToAssetPath(missingGuid), "the fake guid must be unresolvable");
        string text = System.IO.File.ReadAllText(ctrlPath).Replace(realGuid, missingGuid);
        System.IO.File.WriteAllText(ctrlPath, text);
        AssetDatabase.ImportAsset(ctrlPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.DeleteAsset(clipPath); // the real clip is now unreferenced

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ctrlPath);
        var w = ControllerDecompile.Walk(controller);
        var a = w.Doc.Layers[0].Root.States.First(s => s.Name == "A");
        var b = w.Doc.Layers[0].Root.States.First(s => s.Name == "B");
        Assert.IsNotNull(a.Motion?.RefGuid, "state A decodes a dangling guid ref");
        Assert.IsNotNull(b.Motion?.RefGuid, "state B decodes a dangling guid ref");
        Assert.AreEqual(missingGuid, a.Motion.RefGuid.Guid, "state A recovers the missing guid");
        Assert.AreEqual(missingGuid, b.Motion.RefGuid.Guid, "state B ALSO recovers it (the old shared FIFO gave the 2nd 'unknown')");
        Assert.AreEqual(2, w.UnresolvedGuids.Count, "both dangling slots are listed");
    }

    // ---- Review #7b: a condition param that can't survive the '<param> <op> <value>' grammar -> refusal ----

    [Test]
    public void Walk_Condition_Param_With_Whitespace_Refuses()
    {
        var c = new AnimatorController { name = "CondWs_Fx" };
        c.AddParameter("Bad Param", AnimatorControllerParameterType.Bool);
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        var t = sm.AddState("T");
        var tr = s.AddTransition(t);
        tr.AddCondition(AnimatorConditionMode.If, 0f, "Bad Param");

        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("Bad Param") && r.Contains("condition")),
            "a condition param carrying whitespace -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review-2 #1: a sub-machine's OUTGOING (on-Exit) transition -> a located refusal ------------------

    [Test]
    public void Walk_SubMachine_Outgoing_Transition_Refuses()
    {
        var c = new AnimatorController { name = "SmTrans_Fx" };
        c.AddLayer("L");
        var root = c.layers[0].stateMachine;
        var sub = root.AddStateMachine("Sub");
        root.AddStateMachineExitTransition(sub); // a transition OUT of Sub — no 'from sub-machine' vocabulary
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("from sub-machine") && r.Contains("Sub")),
            "an outgoing sub-machine transition -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review-2 #2: a real state named 'Exit' addressed bare -> a located refusal -----------------------

    [Test]
    public void Walk_Target_Named_Exit_Refuses()
    {
        var c = new AnimatorController { name = "ExitName_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        var exit = sm.AddState("Exit"); // a real state literally named 'Exit'
        s.AddTransition(exit);          // same-machine ⇒ bare 'Exit' target collides with the exit keyword
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("reserved token 'Exit'")),
            "a bare target named 'Exit' -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review-2 #4: an UNSAVED controller keeps a standalone .anim as a ref (not inlined) ---------------

    [Test]
    public void Walk_Unsaved_Controller_Keeps_Standalone_Clip_As_Ref()
    {
        EnsureScratch();
        string clipPath = ScratchFolder + "/standalone.anim";
        var ext = new AnimationClip { name = "standalone" };
        AnimationUtility.SetEditorCurve(ext, EditorCurveBinding.FloatCurve("", typeof(Animator), "P"),
            AnimationCurve.Constant(0f, 0.1f, 1f));
        AssetDatabase.CreateAsset(ext, clipPath);

        // In-memory controller (no asset path) referencing a SAVED standalone clip: it must decode as a
        // `ref:` (its own path), never inlined — the old guard tested the controller's path and inlined it.
        var c = new AnimatorController { name = "Unsaved_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        st.motion = ext;

        var w = ControllerDecompile.Walk(c);
        var motion = w.Doc.Layers[0].Root.States.First(s => s.Name == "S").Motion;
        Assert.AreEqual(clipPath, motion.RefPath, "standalone clip decodes as a ref even for an unsaved controller");
        Assert.IsNull(motion.Clip, "not inlined");
        Object.DestroyImmediate(c);
        AssetDatabase.DeleteAsset(clipPath);
    }

    // ---- Review-2 (docs #1 / code): an IK-pass layer is out of vocabulary -> a located refusal -----------

    [Test]
    public void Walk_IkPass_Layer_Refuses()
    {
        var c = new AnimatorController { name = "IkPass_Fx" };
        c.AddLayer("L");
        var layers = c.layers;      // sm.layers returns a COPY
        layers[0].iKPass = true;
        c.layers = layers;
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("IK pass")), "an IK-pass layer -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review-3 B: a direct state and direct sub-machine sharing a name -> a located refusal ------------

    [Test]
    public void Walk_State_And_SubMachine_Same_Name_Refuse()
    {
        var c = new AnimatorController { name = "CrossKind_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        sm.AddState("X");
        sm.AddStateMachine("X"); // a sub-machine sharing the state's name (separate Unity collections)
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("both named") && r.Contains("'X'")),
            "a state and a sub-machine of the same name -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review-3 C: a vendor entry transition carrying mute/solo -> a located refusal (read-side #3) ------

    [Test]
    public void Walk_Entry_Transition_With_Mute_Refuses()
    {
        var c = new AnimatorController { name = "EntryMute_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        var et = sm.AddEntryTransition(s); // entry to a STATE = a ladder rung (not the sub-machine-default split)
        et.mute = true;
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("entry transition carries mute/solo")),
            "an entry transition with mute -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review-5: the decode completeness census refuses non-default unconsumed fields -------------------

    [Test]
    public void Walk_State_CycleOffset_Refuses()
    {
        var c = new AnimatorController { name = "CycleOff_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        st.cycleOffset = 0.5f;
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("CycleOffset") && r.Contains("'S'")),
            "a non-default state cycleOffset -> census refusal");
        Object.DestroyImmediate(c);
    }

    [Test]
    public void Walk_State_IkOnFeet_Refuses()
    {
        var c = new AnimatorController { name = "IkFeet_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        st.iKOnFeet = true;
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("IKOnFeet") && r.Contains("'S'")),
            "a state with foot IK (iKOnFeet) -> census refusal");
        Object.DestroyImmediate(c);
    }

    [Test]
    public void Walk_State_Tag_Refuses()
    {
        var c = new AnimatorController { name = "Tag_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        st.tag = "MyTag";
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("'MyTag'")),
            "a state carrying a tag -> census refusal");
        Object.DestroyImmediate(c);
    }

    [Test]
    public void Walk_Transition_Offset_Refuses()
    {
        var c = new AnimatorController { name = "TransOff_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        var t = sm.AddState("T");
        var tr = s.AddTransition(t);
        tr.offset = 0.3f;
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("Offset") && r.Contains("state 'S'")),
            "a non-default transition offset -> census refusal");
        Object.DestroyImmediate(c);
    }

    [Test]
    public void Walk_Plain_State_And_Transition_No_Census_Refusals()
    {
        // The census must not false-positive on a controller whose only non-defaults are consumed fields.
        var c = new AnimatorController { name = "Plain_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        var t = sm.AddState("T");
        s.AddTransition(t);
        var w = ControllerDecompile.Walk(c);
        Assert.IsFalse(w.Refusals.Any(r => r.Contains("no schema field binds it")),
            "a plain state/transition triggers no census refusal");
        Object.DestroyImmediate(c);
    }

    // ---- Review-6 #1 (BLOCKER): a Direct tree's Normalized Blend Values round-trips (not swept-away) --------

    [Test]
    public void Walk_Direct_Tree_NormalizedBlendValues_RoundTrips()
    {
        var doc = new AnimDocument { Schema = 1, ControllerName = "DirectNorm_Fx" };
        doc.Parameters.Add(new ParamSpec { Name = "W", Type = AnimParamType.Float });
        doc.Clips.Add(new ClipSpec { Name = "c", Seconds = 0.1f });
        var layer = new Layer { Name = "L" };
        var tree = new BlendTreeSpec { Kind = TreeKind.Direct, Normalized = false };
        tree.Children.Add(new TreeChild { Motion = new MotionRef { Clip = "c" }, DirectWeight = "W" });
        layer.Root.States.Add(new State { Name = "S", Motion = new MotionRef { Tree = tree } });
        layer.Root.DefaultState = "S";
        doc.Layers.Add(layer);
        ControllerEmit.Build(doc, out var emitted);

        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.IsFalse(w.Refusals.Any(r => r.Contains("NormalizedBlendValues")),
            "normalizedBlendValues is consumed now, not swept-refused");
        var t2 = w.Doc.Layers[0].Root.States.First(s => s.Name == "S").Motion.Tree;
        Assert.IsTrue(t2.Normalized.HasValue, "the Direct tree's normalized value is decoded");
        Assert.AreEqual(false, t2.Normalized.Value, "the explicit normalized value round-trips (would else reset to the construction default)");
    }

    // ---- Review-6 #2: an unknown driver ChangeType -> refusal (no longer dropped from all four buckets) ------

    [Test]
    public void Walk_Driver_Unknown_ChangeType_Refuses()
    {
        const string yaml =
            "schema: 1\ncontroller: DrvBad_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  X: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - driver: { set: { X: 1 } }\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);
        var drv = (VRC.SDKBase.VRC_AvatarParameterDriver)emitted.Controller.layers[0].stateMachine.states[0].state.behaviours[0];
        drv.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
        {
            new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { type = (VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType)99, name = "X", value = 1f },
        };
        EditorUtility.SetDirty(drv);
        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("unknown ChangeType")),
            "an unknown driver ChangeType -> located refusal");
    }

    // ---- Review-6 #3: an unknown AnimatorConditionMode -> refusal (no longer approximated as Is-true) --------

    [Test]
    public void Walk_Unknown_ConditionMode_Refuses()
    {
        var c = new AnimatorController { name = "BadMode_Fx" };
        c.AddParameter("P", AnimatorControllerParameterType.Float);
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        var t = sm.AddState("T");
        var tr = s.AddTransition(t);
        tr.AddCondition((AnimatorConditionMode)99, 0f, "P");
        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("unknown mode")),
            "an unknown condition mode -> located refusal");
        Object.DestroyImmediate(c);
    }

    private static void EnsureScratch()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Agent")) AssetDatabase.CreateFolder("Assets", "Agent");
        if (!AssetDatabase.IsValidFolder("Assets/Agent/Scratch")) AssetDatabase.CreateFolder("Assets/Agent", "Scratch");
        if (!AssetDatabase.IsValidFolder(ScratchFolder)) AssetDatabase.CreateFolder("Assets/Agent/Scratch", "emit");
    }
}
