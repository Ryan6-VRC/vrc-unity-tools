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
        Assert.AreEqual(1f, hold.Sets["Level"], 1e-6f);   // a float AAP, not the bool output — see DebounceDoc
    }

    // ---- Round-trip: nesting + a non-driver behaviour ---------------------------------------------
    // Lives in AnimatorSchemaEmitTests.Emit_Walk_Serialize_Parse_Roundtrips_Nested_With_Behaviour, not here: on
    // the same seeded document it makes every assertion this file would (sub-machine name/states/default, the
    // Sub/A cross-machine target, the bare same-machine target, the entry rung, the tracking Fields including
    // rightHand's absence, refusal-free) and then carries the doc through Serialize→Parse→re-emit. A Walk-only
    // copy here would be a prefix of it.

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
        // to GuidRef{Unresolved} + list the guid — NOT silently drop it — and re-emitting
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

    // ---- mixed WD hoists to a modal layer policy + minority overrides, re-emits the same mix --

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
        Assert.AreEqual(0, w.Refusals.Count, "mixed WD is TOLERATED, not refused");
        Assert.IsTrue(w.Notes.Any(n => n.Contains("mixed Write Defaults")),
            "the hoist is a tolerance, so a Note records it — the caller's only trace that WD was rewritten");

        var a = L.Root.States.First(s => s.Name == "A");
        var b = L.Root.States.First(s => s.Name == "B");
        Assert.IsFalse(a.WriteDefaults.HasValue, "majority state's override is cleared (inherits the layer policy)");
        Assert.AreEqual(false, b.WriteDefaults, "minority state keeps an explicit override");

        // Re-emit the decoded doc: the per-state WD mix is reproduced exactly.
        ControllerEmit.Build(w.Doc, out var r2);
        Assert.IsTrue(FirstState(r2, "A").writeDefaultValues, "A re-emits WD true (from layer policy)");
        Assert.IsFalse(FirstState(r2, "B").writeDefaultValues, "B re-emits WD false (from its override)");
    }

    // ---- a uniform-WD layer hoists to a policy with ZERO overrides ------------

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

    // ---- timeParameterActive + empty timeParameter -> unbound motion time + a Note ------------

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

    // ---- sibling states differing only by trailing whitespace -> a located Refusal ------------

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

    // ---- a layer with a null state machine -> a located Refusal (not an NRE) -----------------

    [Test]
    public void Walk_Null_StateMachine_Refuses()
    {
        var c = new AnimatorController { name = "NullSM_Fx" };
        c.layers = new[] { new AnimatorControllerLayer { name = "L", defaultWeight = 1f, stateMachine = null } };

        var w = ControllerDecompile.Walk(c); // must NOT throw
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("no state machine")), "null state machine -> located refusal");
        Object.DestroyImmediate(c);
    }

    // ---- an empty inline clip (zero bindings) -> a located Refusal (not silently empty) ------

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

    // ---- a null playAudio clip entry -> a located Refusal (not an NRE) -----------------------

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

    // ---- a condition param carrying INTERIOR whitespace survives the verbatim-prefix grammar ----
    // Under the right-anchored condition grammar a spaced param name is FAITHFUL: it decodes cleanly and
    // survives verbatim. Only whitespace that collides with the single-space separator (a trailing space)
    // is refused — by the decompile self-check, covered in RoundtripStressTests.

    [Test]
    public void Walk_Condition_Param_With_Interior_Whitespace_Is_Faithful()
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
        Assert.IsEmpty(w.Refusals,
            "an interior-whitespace condition param is faithful, not refused: " + string.Join(" | ", w.Refusals));
        var cond = w.Doc.Layers[0].Root.States.First(x => x.Name == "S").Transitions[0].When[0];
        Assert.AreEqual("Bad Param", cond.Param, "the spaced param name survives verbatim");
        Object.DestroyImmediate(c);
    }

    // ---- A sub-machine's OUTGOING (on-Exit) transition -> decoded onto the PARENT's model ----------------
    // Formerly a located refusal (Review-2 #1). The schema now carries these as the sub-machine's onExit list;
    // what this pins is that the edge lands on the parent's own SubMachine entry, since that is the machine it
    // hangs off and the scope its target resolves in — decoding it onto the child would round-trip wrong.

    [Test]
    public void Walk_SubMachine_Outgoing_Transition_Decodes_Onto_The_Parent()
    {
        var c = new AnimatorController { name = "SmTrans_Fx" };
        c.AddLayer("L");
        var root = c.layers[0].stateMachine;
        var sub = root.AddStateMachine("Sub");
        root.AddStateMachineExitTransition(sub);
        var w = ControllerDecompile.Walk(c);
        Assert.IsEmpty(w.Refusals, "an outgoing sub-machine transition is now in vocabulary");
        var subModel = w.Doc.Layers[0].Root.Machines.Single(m => m.Name == "Sub");
        Assert.AreEqual(1, subModel.OnExit.Count, "the edge decodes onto the parent's SubMachine entry");
        Assert.IsTrue(subModel.OnExit[0].ToExit, "an exit transition decodes as 'to: Exit'");
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

    // Formerly a census refusal. m_TransitionOffset is now bound by the schema's `offset:`, so what has to be
    // pinned is the opposite: it decodes to the value AND stops being swept. A widen that bound the field but
    // left it out of StateTransitionAware would refuse its own freshly-emitted output.
    [Test]
    public void Walk_Transition_Offset_Decodes_And_Is_No_Longer_Swept()
    {
        var c = new AnimatorController { name = "TransOff_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        var t = sm.AddState("T");
        var tr = s.AddTransition(t);
        tr.offset = 0.3f;
        var w = ControllerDecompile.Walk(c);
        Assert.IsEmpty(w.Refusals, "a non-default transition offset is now in vocabulary");
        var decoded = w.Doc.Layers[0].Root.States.Single(x => x.Name == "S").Transitions[0];
        Assert.That(decoded.Offset, Is.EqualTo(0.3f).Within(1e-5f));
        Object.DestroyImmediate(c);
    }

    // A zero offset must stay ABSENT from the model, not decode as an explicit 0 — the emitter only writes
    // `offset:` when the field is set, so a decoded 0 would add a line to every transition in every vendor
    // controller and churn the whole corpus's yaml.
    [Test]
    public void Walk_Transition_Zero_Offset_Stays_Absent()
    {
        var c = new AnimatorController { name = "TransOffZero_Fx" };
        c.AddLayer("L");
        var sm = c.layers[0].stateMachine;
        var s = sm.AddState("S");
        s.AddTransition(sm.AddState("T"));
        var w = ControllerDecompile.Walk(c);
        Assert.IsNull(w.Doc.Layers[0].Root.States.Single(x => x.Name == "S").Transitions[0].Offset);
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

    // ---- a Direct tree's Normalized Blend Values round-trips (not swept-away) --------

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

    // ---- an unknown driver ChangeType -> refusal (not dropped from all four buckets) ------

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

    // ---- an unknown AnimatorConditionMode -> refusal (not approximated as Is-true) --------

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

    // ---- an exotic (auto/clamped-auto) tangent shape -> a located Refusal, not a silent flatten ----

    [Test]
    public void ExoticTangentCurve_Refused_NotFlattened()
    {
        var c = new AnimatorController { name = "ExoticTangent_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        var clip = new AnimationClip { name = "Exotic", frameRate = 60f };
        // Monotonic 3-key shape with UNEVEN slopes either side of the middle key: a peak/valley (local
        // extremum) shape makes ClampedAuto specifically zero the tangent there (it clamps extrema to
        // avoid overshoot) and would wrongly pass the flat test; a middle key that is NOT an extremum,
        // with a slope discontinuity, gets a genuine non-zero clamped-auto tangent.
        var curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.1f, 0.9f), new Keyframe(0.5f, 1f));
        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
        }
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Prop", typeof(Renderer), "enabled"), curve);
        st.motion = clip;

        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("tangent")), "exotic tangents -> named refusal: " + string.Join(" | ", w.Refusals));
        StringAssert.DoesNotContain("Prop/Renderer.enabled", AnimatorSchemaEmit.Serialize(w.Doc));
        Object.DestroyImmediate(c);
    }

    // ---- a WEIGHTED zero-slope curve -> a located Refusal, not folded to flat (weights reshape the
    //      segment and the [t,v] schema can't express them; matches the tool's weightedMode-significant
    //      contract elsewhere — CompileClips hashes weightedMode / refuses a weighted edit) ------------

    [Test]
    public void WeightedZeroTangentCurve_Refused_NotFoldedToFlat()
    {
        var c = new AnimatorController { name = "WeightedZero_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        var clip = new AnimationClip { name = "WeightedZero", frameRate = 60f };
        // Both keys have zero slopes (so the value-only flat test would pass) BUT carry weighted handles
        // with non-default weights — a weighted flat tangent still eases differently, so this must refuse.
        var k0 = new Keyframe(0f, 0f) { inTangent = 0f, outTangent = 0f, weightedMode = WeightedMode.Both, inWeight = 0.05f, outWeight = 0.05f };
        var k1 = new Keyframe(0.5f, 1f) { inTangent = 0f, outTangent = 0f, weightedMode = WeightedMode.Both, inWeight = 0.05f, outWeight = 0.05f };
        var curve = new AnimationCurve(k0, k1);
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Prop", typeof(Renderer), "enabled"), curve);
        st.motion = clip;

        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("tangent")), "weighted zero-slope tangents -> named refusal: " + string.Join(" | ", w.Refusals));
        StringAssert.DoesNotContain("Prop/Renderer.enabled", AnimatorSchemaEmit.Serialize(w.Doc));
        Object.DestroyImmediate(c);
    }

    // ---- path-"" Animator bindings: the declared-param discriminator ------------------------------
    // A muscle curve ("RightHand.Index.1 Stretched") and an AAP write share the binding shape
    // (path "", type Animator); only the declared-parameter set tells them apart. The muscle form
    // must round-trip as "Animator.<property>" — the bare-name emit is reserved for real params
    // (bare non-param output was doomed yaml: the compiler refused it as an undeclared binding).

    private const string MuscleDoc =
        "schema: 1\ncontroller: MuscleRT_Gesture\nbasis: mount-root\nrole: gesture\n" +
        "parameters:\n  Mode: { type: int }\n  Aap: { type: float, aap: true }\n" +
        "layers:\n  - name: L\n" +
        "    states:\n      Open: { motion: ~, transitions: [ { to: Grip, when: [ Mode equals 2 ] } ] }\n" +
        "      Grip:\n        motion: { clip: grip }\n        transitions:\n          - { to: Open, when: [ Mode notEqual 2 ] }\n" +
        "    default: Open\n" +
        "clips:\n  grip:\n    set:\n" +
        "      \"Animator.RightHand.Index.1 Stretched\": -0.5\n" +
        "      Aap: 1.0\n";

    [Test]
    public void MuscleBinding_Roundtrips_As_AnimatorProperty_And_Param_Stays_Bare()
    {
        var src = AnimatorSchemaYaml.Parse(MuscleDoc, "test");
        ControllerEmit.Build(src, out var emitted);
        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.AreEqual(0, w.Refusals.Count, "muscle binding is in-vocabulary: " + string.Join(" | ", w.Refusals));

        var grip = w.Doc.Clips.First(c => c.Name == "grip");
        Assert.IsTrue(grip.Sets.ContainsKey("Animator.RightHand.Index.1 Stretched"),
            "muscle curve emits the Animator.<property> form, got: " + string.Join(", ", grip.Sets.Keys));
        Assert.IsTrue(grip.Sets.ContainsKey("Aap"), "declared param write stays bare");

        // Fixpoint: serialize -> reparse -> rebuild -> rewalk reproduces the same canonical string.
        var y1 = AnimatorSchemaEmit.Serialize(w.Doc);
        var doc2 = AnimatorSchemaYaml.Parse(y1, "roundtrip");
        ControllerEmit.Build(doc2, out var emitted2);
        var w2 = ControllerDecompile.Walk(emitted2.Controller);
        Assert.AreEqual(0, w2.Refusals.Count, "second walk refusal: " + string.Join(" | ", w2.Refusals));
        Assert.AreEqual(y1, AnimatorSchemaEmit.Serialize(w2.Doc), "muscle-binding doc is on the fixpoint");
    }

    // ---- W8: driver Random carries preventRepeats ------------------------------------------------

    [Test]
    public void Walk_Driver_Random_PreventRepeats_Roundtrips()
    {
        // preventRepeats is the SDK's fourth Random field (drawn by the Inspector for a Random onto an Int).
        // It used to be dropped on decode, so a vendor's no-repeat roll came back as a plain one — a silent
        // behavioral change, not a cosmetic one. Assert the VALUE off the recompiled object, because a decode
        // that captured the key but reset it to the SDK default would still reach a clean textual fixpoint.
        const string yaml =
            "schema: 1\ncontroller: PrevRep_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  Roll: int\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - driver: { random: { Roll: { min: 0, max: 7, preventRepeats: true } } }\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);

        var built = (VRC.SDKBase.VRC_AvatarParameterDriver)
            emitted.Controller.layers[0].stateMachine.states[0].state.behaviours[0];
        Assert.IsTrue(built.parameters[0].preventRepeats, "emit put preventRepeats on the SDK Parameter");

        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.AreEqual(0, w.Refusals.Count, "refusal: " + string.Join(" | ", w.Refusals));

        // Round-trip and read the flag off the SECOND build — the hop the drop used to happen on.
        var doc2 = AnimatorSchemaYaml.Parse(AnimatorSchemaEmit.Serialize(w.Doc), "roundtrip");
        ControllerEmit.Build(doc2, out var emitted2);
        var again = (VRC.SDKBase.VRC_AvatarParameterDriver)
            emitted2.Controller.layers[0].stateMachine.states[0].state.behaviours[0];
        Assert.IsTrue(again.parameters[0].preventRepeats, "preventRepeats survived decompile -> recompile");
    }

    [Test]
    public void Walk_Driver_Random_Without_PreventRepeats_Stays_Off_The_Document()
    {
        // The complement: false is the SDK default, so it must NOT appear in the emitted YAML — otherwise
        // every existing document churns the first time it round-trips through this build.
        const string yaml =
            "schema: 1\ncontroller: PrevRepOff_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  Roll: int\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - driver: { random: { Roll: { min: 0, max: 7 } } }\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);
        var w = ControllerDecompile.Walk(emitted.Controller);
        StringAssert.DoesNotContain("preventRepeats", AnimatorSchemaEmit.Serialize(w.Doc),
            "an unset preventRepeats must stay implicit");
    }

    // ---- W8: intra-bucket driver order is a real dependency, so pin it ----------------------------

    [Test]
    public void Walk_Driver_BucketOrder_Survives_A_Round_Trip()
    {
        // `Copy B<-A; Copy C<-B` reads a parameter the PREVIOUS op wrote, so the two are not commutative —
        // and both live in the same bucket, which is exactly what DetectDriverOrderLoss's interleave check
        // cannot see (it fires on the bucket index decreasing, i.e. across change-types). Order is carried
        // only by Dictionary insertion-order enumeration, on four separate hops: the decode bucket, the YAML
        // writer, the YAML reader's map, and ControllerEmit's iteration. That is a BCL implementation detail
        // rather than a documented guarantee, so this test is the contract — swap any hop to a hash-ordered
        // map and it fails here instead of silently rewriting what a driver does.
        const string yaml =
            "schema: 1\ncontroller: DrvChain_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  A: float\n  B: float\n  C: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n          - driver: { copy: { B: A, C: B } }\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);

        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.AreEqual(0, w.Refusals.Count, "refusal: " + string.Join(" | ", w.Refusals));

        var doc2 = AnimatorSchemaYaml.Parse(AnimatorSchemaEmit.Serialize(w.Doc), "roundtrip");
        ControllerEmit.Build(doc2, out var emitted2);
        var drv = (VRC.SDKBase.VRC_AvatarParameterDriver)
            emitted2.Controller.layers[0].stateMachine.states[0].state.behaviours[0];

        var chain = drv.parameters.Select(p => p.name + "<-" + p.source).ToList();
        Assert.AreEqual(new List<string> { "B<-A", "C<-B" }, chain,
            "the same-bucket copy chain must re-emit in authored order; got " + string.Join(", ", chain));
    }

    // ---- W8: a transition carrying BOTH isExit and a destination ----------------------------------

    [Test]
    public void Walk_Transition_IsExit_With_Destination_Refuses()
    {
        // No Inspector authors this, but both setters are public, so a script or a hand-edited asset can.
        // SetTarget used to take isExit first and return, dropping the destination without a word — a silent
        // normalization of a contradiction the decoder can neither represent nor adjudicate.
        const string yaml =
            "schema: 1\ncontroller: ExitClash_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        transitions:\n          - { to: T, when: [ P greater 0.5 ] }\n" +
            "      T:\n        motion: ~\n" +
            "    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);

        var tr = emitted.Controller.layers[0].stateMachine.states
            .First(cs => cs.state.name == "S").state.transitions[0];
        Assert.IsNotNull(tr.destinationState, "fixture precondition: the transition has a destination");
        tr.isExit = true;   // now contradictory
        EditorUtility.SetDirty(tr);

        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("isExit AND a destination")),
            "isExit + destination -> located refusal; got: " + string.Join(" | ", w.Refusals));
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("'T'")), "the refusal names the dropped destination");
    }

    // ---- W8: a refusal location names its layer ---------------------------------------------------

    [Test]
    public void Walk_Refusal_Location_Names_Its_Layer()
    {
        // Refusals are reported as one controller-wide list, and both location vocabularies used to omit the
        // layer: a root machine rendered as a bare "(root)", a state as "state 'S'". Two layers that each
        // carry a state of the same name — the ordinary case, every SDK template ships an "Idle" — therefore
        // produced identical lines. Both layers here are named S on purpose, so ONLY the layer can tell them
        // apart, and the refusal is raised from a state-scoped site (the vocabulary the original finding
        // missed; it named PathLabel alone).
        const string yaml =
            "schema: 1\ncontroller: LayerLoc_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: float\n" +
            "layers:\n" +
            "  - name: Alpha\n    states:\n      S:\n        motion: ~\n    default: S\n" +
            "  - name: Beta\n    states:\n      S:\n        motion: ~\n    default: S\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);

        // Provoke one refusal per layer from the SAME site, so only the layer name can tell them apart.
        foreach (var layer in emitted.Controller.layers)
        {
            var drv = (VRC.SDKBase.VRC_AvatarParameterDriver)
                layer.stateMachine.states[0].state.AddStateMachineBehaviour(
                    typeof(VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver));
            drv.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
            {
                new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter {
                    type = (VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType)99, name = "X", value = 1f },
            };
            EditorUtility.SetDirty(drv);
        }

        // Cover BOTH location vocabularies, since they are built by different helpers. The state-scoped
        // completeness sweep stayed unqualified when this test first shipped — it asserted only
        // Contains("Alpha") and passed straight over the gap.
        foreach (var layer in emitted.Controller.layers)
        {
            var st = layer.stateMachine.states[0].state;
            st.mirrorParameterActive = true;
            st.mirrorParameter = "Mirror";     // named, so the refusal is not the empty-value shape
            EditorUtility.SetDirty(st);

            // …and a MACHINE-scoped refusal, from a behaviour on the state machine itself.
            var smDrv = (VRC.SDKBase.VRC_AvatarParameterDriver)
                layer.stateMachine.AddStateMachineBehaviour(
                    typeof(VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver));
            smDrv.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
            {
                new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter {
                    type = (VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType)99, name = "Y", value = 1f },
            };
            EditorUtility.SetDirty(smDrv);
        }

        var w = ControllerDecompile.Walk(emitted.Controller);
        foreach (var expected in new[] { "Alpha", "Beta" })
        {
            Assert.IsTrue(w.Refusals.Any(r => r.Contains(expected) && r.Contains("state 'S'")),
                "a STATE-scoped refusal names layer " + expected + ": " + string.Join(" | ", w.Refusals));
            Assert.IsTrue(w.Refusals.Any(r => r.Contains(expected) && r.Contains("machine '(root)'")),
                "a MACHINE-scoped refusal names layer " + expected + ": " + string.Join(" | ", w.Refusals));
        }
        // Both labels carry their own quotes, so a caller adding a second pair renders the layer name as
        // though it were the entity's name — "machine 'layer 'Alpha' (root)'". Pin the shape, not just the
        // presence of the word.
        Assert.IsFalse(w.Refusals.Any(r => r.Contains("'layer ")),
            "a location nested the layer inside another entity's quotes: " + string.Join(" | ", w.Refusals));
    }

    // ---- review: the folded default rung decodes under the SAME guarantees as a ladder rung -------

    [Test]
    public void Walk_Default_SubMachine_Rung_Refuses_Mute()
    {
        // A `default:` naming a direct sub-machine is emitted as a trailing unconditional entry rung, which
        // DecodeMachine folds back into `default:` and skips past DecodeEntryTransition. It used to skip that
        // method's mute/solo refusal along with it: a MUTED rung — inert in the source — then recompiled as a
        // LIVE unconditional default, a behavioural rewrite rather than a cosmetic loss, and `default:` has
        // nowhere to carry mute. The dropped-name Note was copied into that branch; these guards were not.
        const string yaml =
            "schema: 1\ncontroller: DefRungMute_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: float\n" +
            "layers:\n  - name: L\n    machines:\n      Sub:\n        states:\n          Inner:\n" +
            "            motion: ~\n        default: Inner\n    default: Sub\n";
        var src = AnimatorSchemaYaml.Parse(yaml, "test");
        ControllerEmit.Build(src, out var emitted);

        var root = emitted.Controller.layers[0].stateMachine;
        Assert.Greater(root.entryTransitions.Length, 0, "fixture precondition: the default emitted an entry rung");
        var rung = root.entryTransitions[root.entryTransitions.Length - 1];
        Assert.IsNotNull(rung.destinationStateMachine, "fixture precondition: that rung targets the sub-machine");

        // Clean first — the added guards must not invent a refusal on the ordinary shape.
        Assert.AreEqual(0, ControllerDecompile.Walk(emitted.Controller).Refusals.Count,
            "an unmuted default rung must still decode clean");

        rung.mute = true;
        EditorUtility.SetDirty(rung);

        var w = ControllerDecompile.Walk(emitted.Controller);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("default sub-machine rung carries mute/solo")),
            "a muted default rung -> refusal; got: " + string.Join(" | ", w.Refusals));
        // Located like every sibling entry refusal, layer included, so one controller-wide list stays readable.
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("transition from Entry in layer [0] 'L'")),
            "the refusal carries the standard located prefix: " + string.Join(" | ", w.Refusals));
    }

    private static void EnsureScratch()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Agent")) AssetDatabase.CreateFolder("Assets", "Agent");
        if (!AssetDatabase.IsValidFolder("Assets/Agent/Scratch")) AssetDatabase.CreateFolder("Assets/Agent", "Scratch");
        if (!AssetDatabase.IsValidFolder(ScratchFolder)) AssetDatabase.CreateFolder("Assets/Agent/Scratch", "emit");
    }
}
