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
}
