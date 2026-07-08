using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Generative round-trip tests for AnimatorSchemaEmit — the AnimDocument -> YAML write-back. The parser is the
// spec: Parse(Serialize(doc)) must reproduce doc for every construct, and Serialize must be idempotent through
// a parse (the Task-9 textual fixpoint). Most tests are pure (Parse only, System.*); the emit->Walk->Serialize
// ->Parse ones touch the AssetDatabase (they build + decompile a real controller). NOT run via MCP run_tests
// (it crashes the editor); run from the Unity Test Runner window or CI.
public class AnimatorSchemaEmitTests
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

    // ---- debounce round-trip + idempotence --------------------------------------------------------

    [Test]
    public void Serialize_Then_Parse_Roundtrips_Debounce()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        string yaml = AnimatorSchemaEmit.Serialize(doc);
        var back = AnimatorSchemaYaml.Parse(yaml, "roundtrip");

        Assert.AreEqual(doc.Schema, back.Schema);
        Assert.AreEqual(doc.ControllerName, back.ControllerName);
        Assert.AreEqual(doc.Basis, back.Basis);
        Assert.AreEqual(doc.Role, back.Role);
        Assert.AreEqual(doc.Defaults.WriteDefaults, back.Defaults.WriteDefaults);
        Assert.AreEqual(doc.Parameters.Count, back.Parameters.Count);
        Assert.AreEqual(doc.Parameters[0].Name, back.Parameters[0].Name);
        Assert.AreEqual(doc.Parameters[0].Type, back.Parameters[0].Type);

        var srcRoot = doc.Layers[0].Root;
        var backRoot = back.Layers[0].Root;
        Assert.AreEqual(srcRoot.States.Count, backRoot.States.Count);
        Assert.AreEqual(srcRoot.DefaultState, backRoot.DefaultState);

        var pending = backRoot.States.First(s => s.Name == "Pending");
        Assert.AreEqual("timer", pending.Motion.Clip);
        var unconditional = pending.Transitions.First(t => t.To == "Active");
        Assert.AreEqual(0, unconditional.When.Count);
        Assert.AreEqual(1f, unconditional.ExitTime.Value, 1e-6f);

        var active = backRoot.States.First(s => s.Name == "Active");
        Assert.AreEqual("driver", active.Behaviours[0].Kind);
        var set = (Dictionary<string, object>)active.Behaviours[0].Fields["set"];
        Assert.AreEqual(1f, AsF(set["Debounced"]), 1e-6f);

        Assert.AreEqual(doc.Clips.Count, back.Clips.Count);
        Assert.AreEqual(0.2f, back.Clips.First(c => c.Name == "timer").Seconds.Value, 1e-6f);

        // idempotence — the fixpoint depends on it.
        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "serialize is idempotent through a parse");
    }

    [Test]
    public void Serialize_Notes_Block_Present_But_Ignored_On_Reparse()
    {
        var doc = AnimatorSchemaYaml.Parse(AnimatorSchemaYamlTests.DebounceDoc, "test");
        doc.ReservedNotes["orphans"] = 3;
        string yaml = AnimatorSchemaEmit.Serialize(doc);

        StringAssert.Contains("_notes:", yaml);
        StringAssert.Contains("orphans: 3", yaml);
        Assert.DoesNotThrow(() => AnimatorSchemaYaml.Parse(yaml, "reparse"));

        var back = AnimatorSchemaYaml.Parse(yaml, "reparse");
        Assert.IsTrue(back.ReservedNotes.ContainsKey("_notes"), "notes re-parse under the reserved _notes key");
        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "notes rendering is idempotent (no per-pass nesting growth)");
    }

    // ---- sub-machine + Entry/Any ladders + cross-machine targets ----------------------------------

    [Test]
    public void Serialize_Roundtrips_SubMachine_And_Ladders()
    {
        const string src =
            "schema: 1\ncontroller: Nested_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    states:\n      Idle:\n        motion: ~\n" +
            "        transitions:\n          - { to: Sub/A, when: [ P is true ] }\n" +
            "    machines:\n      Sub:\n        states:\n          A:\n            motion: ~\n            transitions:\n              - { to: B, when: [ P is false ] }\n          B: { motion: ~ }\n        default: A\n" +
            "    entry:\n      - { to: Sub, when: [ P is true ] }\n" +
            "    any:\n      - { to: Idle, when: [ P is false ], canTransitionToSelf: false }\n" +
            "    default: Idle\n";
        var doc = AnimatorSchemaYaml.Parse(src, "test");
        string yaml = AnimatorSchemaEmit.Serialize(doc);
        var back = AnimatorSchemaYaml.Parse(yaml, "roundtrip");

        var root = back.Layers[0].Root;
        Assert.AreEqual(1, root.Machines.Count);
        var sub = root.Machines[0];
        Assert.AreEqual("Sub", sub.Name);
        Assert.AreEqual("A", sub.Machine.DefaultState);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, sub.Machine.States.ConvertAll(s => s.Name));

        // cross-machine (slash) vs same-machine (bare) targets survive.
        Assert.AreEqual("Sub/A", root.States.First(s => s.Name == "Idle").Transitions[0].To);
        Assert.AreEqual("B", sub.Machine.States.First(s => s.Name == "A").Transitions[0].To);

        Assert.AreEqual(1, root.EntryLadder.Count);
        Assert.AreEqual("Sub", root.EntryLadder[0].To);
        Assert.AreEqual(1, root.AnyLadder.Count);
        Assert.IsFalse(root.AnyLadder[0].CanTransitionToSelf);

        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "idempotent");
    }

    // ---- longform parameters + vrc: block ---------------------------------------------------------

    [Test]
    public void Serialize_Roundtrips_Long_Parameters_And_Vrc()
    {
        const string src =
            "schema: 1\ncontroller: Params\nbasis: avatar-root\n" +
            "parameters:\n" +
            "  Short: float\n" +
            "  Long:\n    type: int\n    default: 3\n    aap: true\n    vrc: { synced: true, saved: false, osc: true, type: float }\n";
        var doc = AnimatorSchemaYaml.Parse(src, "test");
        string yaml = AnimatorSchemaEmit.Serialize(doc);
        var back = AnimatorSchemaYaml.Parse(yaml, "roundtrip");

        Assert.AreEqual(AnimParamType.Float, back.Parameters[0].Type);
        var lng = back.Parameters[1];
        Assert.AreEqual(AnimParamType.Int, lng.Type);
        Assert.AreEqual(3f, lng.Default);
        Assert.IsTrue(lng.Aap);
        Assert.IsNotNull(lng.Vrc);
        Assert.IsTrue(lng.Vrc.Synced);
        Assert.IsTrue(lng.Vrc.Osc);
        Assert.AreEqual(AnimParamType.Float, lng.Vrc.VrcType);

        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "idempotent");
    }

    // ---- all seven behaviour kinds (Fields keyed by the camelCase emit tokens) --------------------

    [Test]
    public void Serialize_Roundtrips_All_Seven_Behaviour_Kinds()
    {
        const string src =
            "schema: 1\ncontroller: Bhv_Fx\nbasis: avatar-root\nrole: fx\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
            "        behaviours:\n" +
            "          - driver: { localOnly: true, set: { A: 1 }, add: { B: 0.5 }, copy: { C: Src, D: { source: Src2, sourceMin: 0, sourceMax: 1, destMin: 0, destMax: 2 } }, random: { E: { min: 0, max: 1, chance: 0.5 } } }\n" +
            "          - tracking: { head: animation, leftHand: tracking }\n" +
            "          - playableLayer: { layer: fx, goalWeight: 1, blendDuration: 0.25 }\n" +
            "          - locomotion: { disableLocomotion: true }\n" +
            "          - poseSpace: { enterPoseSpace: true, fixedDelay: false, delayTime: 0.5 }\n" +
            "          - layerControl: { playable: gesture, layer: 3, goalWeight: 0.5, blendDuration: 0.1 }\n" +
            "          - playAudio: { sourcePath: Audio/Src, playbackOrder: uniqueRandom, parameter: Idx, volume: [ 0.8, 1 ], volumeApply: neverApply, pitch: [ 1, 1 ], pitchApply: alwaysApply, loop: true }\n" +
            "    default: S\n";
        var doc = AnimatorSchemaYaml.Parse(src, "test");
        string yaml = AnimatorSchemaEmit.Serialize(doc);
        var back = AnimatorSchemaYaml.Parse(yaml, "roundtrip");

        var bhvs = back.Layers[0].Root.States[0].Behaviours;
        CollectionAssert.AreEqual(
            new[] { "driver", "tracking", "playableLayer", "locomotion", "poseSpace", "layerControl", "playAudio" },
            bhvs.ConvertAll(b => b.Kind));

        var drv = bhvs[0].Fields;
        Assert.AreEqual(true, (bool)drv["localOnly"]);
        Assert.AreEqual(1f, AsF(((Dictionary<string, object>)drv["set"])["A"]), 1e-6f);
        var copyD = (Dictionary<string, object>)((Dictionary<string, object>)drv["copy"])["D"];
        Assert.AreEqual(2f, AsF(copyD["destMax"]), 1e-6f);
        var trk = bhvs[1].Fields;
        Assert.AreEqual("animation", (string)trk["head"]);
        var pa = bhvs[6].Fields;
        var vol = (List<object>)pa["volume"];
        Assert.AreEqual(0.8f, AsF(vol[0]), 1e-6f);

        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "idempotent");
    }

    // ---- blend trees: nested 1d inside a direct tree, thresholds + directWeight --------------------

    [Test]
    public void Serialize_Roundtrips_Nested_Blend_Trees()
    {
        const string src =
            "schema: 1\ncontroller: Tree_Fx\nbasis: avatar-root\n" +
            "parameters:\n  Blend: float\n  W: float\n  W2: float\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion:\n          tree: direct\n          children:\n" +
            "            - tree: 1d\n              param: Blend\n              directWeight: W\n              children:\n                - { clip: a, threshold: 0 }\n                - { clip: b, threshold: 1 }\n" +
            "            - { clip: c, directWeight: W2 }\n    default: S\n" +
            "clips:\n  a: { seconds: 0.1 }\n  b: { seconds: 0.1 }\n  c: { seconds: 0.1 }\n";
        var doc = AnimatorSchemaYaml.Parse(src, "test");
        string yaml = AnimatorSchemaEmit.Serialize(doc);
        var back = AnimatorSchemaYaml.Parse(yaml, "roundtrip");

        var tree = back.Layers[0].Root.States[0].Motion.Tree;
        Assert.AreEqual(TreeKind.Direct, tree.Kind);
        Assert.AreEqual(2, tree.Children.Count);
        var nested = tree.Children[0].Motion.Tree;
        Assert.AreEqual(TreeKind.OneD, nested.Kind);
        Assert.AreEqual("Blend", nested.Param);
        Assert.AreEqual("W", tree.Children[0].DirectWeight);
        Assert.AreEqual(0f, nested.Children[0].Threshold, 1e-6f);
        Assert.AreEqual(1f, nested.Children[1].Threshold, 1e-6f);
        Assert.AreEqual("W2", tree.Children[1].DirectWeight);

        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "idempotent");
    }

    // ---- quoting traps: infer-as-nonstring scalars are quoted; a dangling guid ref ----------------

    [Test]
    public void Serialize_Quotes_Infer_Traps_And_Unresolved_Guid()
    {
        const string src =
            "schema: 1\ncontroller: Q\nbasis: avatar-root\n" +
            "parameters:\n  on: bool\n" +
            "layers:\n  - name: L\n    states:\n      Idle:\n        motion: ~\n" +
            "        transitions:\n          - { to: \"off\", when: [ on is true ] }\n" +
            "      \"off\":\n        motion: { ref: { guid: \"00000000000000000000000000000000\", unresolved: true } }\n" +
            "    default: Idle\n";
        var doc = AnimatorSchemaYaml.Parse(src, "test");
        string yaml = AnimatorSchemaEmit.Serialize(doc);

        // A param named `on`, a state named `off`, and the all-zero GUID all read back as non-strings unless
        // quoted — the serializer must quote them.
        StringAssert.Contains("\"on\":", yaml);
        StringAssert.Contains("\"off\"", yaml);
        StringAssert.Contains("\"00000000000000000000000000000000\"", yaml);

        var back = AnimatorSchemaYaml.Parse(yaml, "roundtrip");
        Assert.AreEqual("on", back.Parameters[0].Name);
        var off = back.Layers[0].Root.States.First(s => s.Name == "off");
        Assert.IsTrue(off.Motion.RefGuid.Unresolved);
        Assert.AreEqual("00000000000000000000000000000000", off.Motion.RefGuid.Guid);
        Assert.AreEqual("off", back.Layers[0].Root.States.First(s => s.Name == "Idle").Transitions[0].To);

        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "idempotent");
    }

    // ---- Decompile -> Serialize -> Parse: proves the write-back half of the fixpoint on a real graph

    [Test]
    public void Emit_Walk_Serialize_Parse_Roundtrips_Nested_With_Behaviour()
    {
        const string src =
            "schema: 1\ncontroller: NestedRT_Fx\nbasis: avatar-root\nrole: fx\n" +
            "parameters:\n  P: { type: bool }\n" +
            "layers:\n  - name: L\n" +
            "    states:\n      Idle:\n        motion: ~\n" +
            "        behaviours:\n          - tracking: { head: animation, leftHand: tracking }\n" +
            "        transitions:\n          - { to: Sub/A, when: [ P is true ] }\n" +
            "    machines:\n      Sub:\n        states:\n          A:\n            motion: ~\n            transitions:\n              - { to: B, when: [ P is false ] }\n          B: { motion: ~ }\n        default: A\n" +
            "    entry:\n      - { to: Sub, when: [ P is true ] }\n" +
            "    default: Idle\n";
        var parsed = AnimatorSchemaYaml.Parse(src, "test");
        ControllerEmit.Build(parsed, out var emitted);
        var walked = ControllerDecompile.Walk(emitted.Controller);
        Assert.AreEqual(0, walked.Refusals.Count, "nested doc is fully in-vocabulary");

        // Serialize the DECODED document, then parse it back — the write-back half of Decompile->Compile.
        string yaml = AnimatorSchemaEmit.Serialize(walked.Doc);
        var back = AnimatorSchemaYaml.Parse(yaml, "roundtrip");

        var root = back.Layers[0].Root;
        Assert.AreEqual(1, root.Machines.Count);
        Assert.AreEqual("Sub", root.Machines[0].Name);
        Assert.AreEqual("A", root.Machines[0].Machine.DefaultState);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, root.Machines[0].Machine.States.ConvertAll(s => s.Name));
        Assert.AreEqual("Sub/A", root.States.First(s => s.Name == "Idle").Transitions[0].To);
        Assert.AreEqual("B", root.Machines[0].Machine.States.First(s => s.Name == "A").Transitions[0].To);
        Assert.AreEqual(1, root.EntryLadder.Count);
        Assert.AreEqual("Sub", root.EntryLadder[0].To);

        var trk = root.States.First(s => s.Name == "Idle").Behaviours.First(b => b.Kind == "tracking").Fields;
        Assert.AreEqual("animation", (string)trk["head"]);
        Assert.AreEqual("tracking", (string)trk["leftHand"]);
        Assert.IsFalse(trk.ContainsKey("rightHand"), "an untouched channel stays unemitted");

        // The re-emitted controller graph matches (behaviour + nested machine survive the full round).
        ControllerEmit.Build(back, out var emitted2);
        var w2 = ControllerDecompile.Walk(emitted2.Controller);
        Assert.AreEqual(0, w2.Refusals.Count);
        Assert.AreEqual(walked.Doc.Layers[0].Root.States.Count, w2.Doc.Layers[0].Root.States.Count);

        // Serialize is idempotent through the parse of a decoded document.
        Assert.AreEqual(yaml, AnimatorSchemaEmit.Serialize(back), "idempotent through a parse");
    }
}
