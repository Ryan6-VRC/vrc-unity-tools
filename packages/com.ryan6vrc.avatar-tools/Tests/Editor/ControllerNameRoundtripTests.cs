using System.IO;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Behavioral tests for the name round-trip (compile <-> decompile) of blend trees and transitions:
    // a human-authored `name:` survives; an auto-named tree (the positional <State>_BlendTree /
    // <parent>_<i> default) stays nameless in YAML; a `name:` on an entry-ladder rung or on a clip tree child
    // is refused at parse (neither emit path reads it). Also covers the completeness sweep's per-type m_Name
    // membership: a cosmetic entry-transition/SMB name is tolerated, not refused. Run headless via
    // tools/run-editmode-tests.ps1 (or the Test Runner window / CI); not via MCP run_tests — wrong venue
    // (live editor). See docs/verify.md.
    //
    // NOT here: a name carrying a LINE BREAK. That is the serializer's one funnel guard
    // (AnimatorSchemaEmit.CheckNoLineBreak, reached by every name/path/field through ScalarStr), witnessed
    // once at RoundtripStressTests.Funnel_LineBreakInParamName_Fails — a name-position case only re-walks it.
    public class ControllerNameRoundtripTests
    {
        private const string TestRoot = "Assets/Agent/Scratch/name_roundtrip_tests";

        [SetUp]
        public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

        // Per-test deletion is load-bearing, not hygiene: CompileTo reuses a controller already sitting in its
        // outDir (the compile door's GUID-stable idempotence path), so a leaked artifact would change which
        // branch the next case exercises. No AssetDatabase.Refresh() on either side — CreateFolder registers
        // the folder AND writes its .meta, and DeleteAsset closes its own import.
        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
            if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
        }

        // ── 1: a named parent + unnamed child — the child does NOT inherit an explicit name, and the whole
        //      document still reaches a fixpoint on a second pass. No separate single-named-tree case: the
        //      parent here IS a named top-level tree (Direct), and test 5 is a named top-level 1D one. ──────
        [Test]
        public void NamedParentTree_UnnamedChildTree_ChildStaysNameless()
        {
            string yaml = @"schema: 1
controller: NamedParent_Fx
basis: avatar-root
role: fx
defaults:
  writeDefaults: on
parameters:
  Dir: { type: float, default: 0.0 }
  W1:  { type: float, default: 0.5 }
layers:
  - name: L
    states:
      Idle:
        motion:
          tree: direct
          name: Locomotion
          children:
            - directWeight: W1
              tree: 1d
              param: Dir
              children:
                - { clip: a, threshold: 0.0 }
                - { clip: b, threshold: 1.0 }
    default: Idle
clips:
  a: { seconds: 0.1 }
  b: { seconds: 0.1 }
";
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "NamedParent_Fx", "c0");

            var w = ControllerDecompile.Walk(c0);
            Assert.IsEmpty(w.Refusals);
            var tree = w.Doc.Layers[0].Root.States.First(s => s.Name == "Idle").Motion.Tree;
            Assert.AreEqual("Locomotion", tree.Name, "the parent tree keeps its authored name");
            Assert.IsNull(tree.Children[0].Motion.Tree.Name, "the nested child tree stays nameless (auto default)");

            string yamlA = AnimatorSchemaEmit.Serialize(w.Doc);
            StringAssert.Contains("name: Locomotion", yamlA, "the parent name surfaces in the yaml");

            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, "NamedParent_Fx", "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "a second compile/decode pass reaches the same fixpoint");
        }

        // ── 2: a default-CONSTRUCTED Unity name ("Blend Tree") is not the schema's auto-generated default
        //      ("<State>_BlendTree") — it is a real (if unintentional) name and must surface ────────────
        [Test]
        public void DefaultConstructedTreeName_SurfacesWhenNotAutoDefault()
        {
            string ctrlPath = TestRoot + "/DefaultName_Fx.controller";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var idle = rc.layers[0].stateMachine.AddState("Idle");
            var bt = new BlendTree { name = "Blend Tree", blendType = BlendTreeType.Direct };
            AssetDatabase.AddObjectToAsset(bt, rc);
            idle.motion = bt;
            AssetDatabase.SaveAssets();

            var w = ControllerDecompile.Walk(rc);
            Assert.IsEmpty(w.Refusals, "a plain default-named tree is not a refusal");
            var tree = w.Doc.Layers[0].Root.States.First(s => s.Name == "Idle").Motion.Tree;
            Assert.AreEqual("Blend Tree", tree.Name,
                "Unity's literal default name differs from the schema auto default ('Idle_BlendTree') and surfaces");

            string yaml = AnimatorSchemaEmit.Serialize(w.Doc);
            StringAssert.Contains("Blend Tree", yaml, "the surfaced name appears in the serialized yaml");
        }

        // ── 3: a named transition round-trips. The STATE ladder is the witness for BOTH ladders: emit funnels
        //      every state/AnyState transition through ControllerEmit.ConfigureStateTransition and decode
        //      through ControllerDecompile.DecodeStateTransition, so an AnyState case re-walks the same two
        //      methods. The ONE field each ladder handles outside those two is `canTransitionToSelf` (assigned
        //      and read beside the shared calls, AnyState only) — and it is not a name; its own round-trip is
        //      covered by the addressing.yaml fixture at RoundtripStressTests.Fixpoint_AuthoredYaml ─────────
        [Test]
        public void NamedStateTransition_Roundtrips()
        {
            string yaml = @"schema: 1
controller: NamedTransition_Fx
basis: avatar-root
role: fx
parameters:
  Go: bool
layers:
  - name: L
    states:
      Idle:
        motion: ~
        transitions:
          - { to: Emote, name: EmoteExit, when: [ Go is true ] }
      Emote:
        motion: ~
    default: Idle
";
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "NamedTransition_Fx", "c0");
            string yamlA = FixpointOracle.Decode(c0);
            StringAssert.Contains("name: EmoteExit", yamlA, "the authored transition name survives decode");

            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, "NamedTransition_Fx", "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "a named transition reaches a textual fixpoint");
        }

        // ── 4: a `name:` on an entry-ladder rung is refused AT PARSE — the entry-emit path never reads a
        //      transition name, so silently accepting one would drop it without a trace ───────────────────
        [Test]
        public void EntryRungName_ThrowsAtParse()
        {
            const string yaml = @"schema: 1
controller: BadEntryName_Fx
basis: avatar-root
role: fx
layers:
  - name: L
    states:
      Idle: { motion: ~ }
    entry:
      - { to: Idle, name: Bad, when: [] }
    default: Idle
";
            var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(yaml, "test"));
            StringAssert.Contains("name", ex.Message);
        }

        // ── 5: a name requiring YAML quoting (a colon) round-trips intact — the first feature to put
        //      arbitrary human text through ScalarStr/NeedsQuote in a name position — for both a blend-tree
        //      name and a transition name ────────────────────────────────────────────────────────────────
        [Test]
        public void QuotingRequiredNames_Roundtrip()
        {
            string yaml = @"schema: 1
controller: QuotedNames_Fx
basis: avatar-root
role: fx
parameters:
  Blend: { type: float, default: 0.0 }
  Go: bool
layers:
  - name: L
    states:
      Idle:
        motion:
          tree: 1d
          name: ""Loco: Motion""
          param: Blend
          children:
            - { clip: a, threshold: 0.0 }
            - { clip: b, threshold: 1.0 }
        transitions:
          - { to: Emote, name: ""Emote: Exit"", when: [ Go is true ] }
      Emote:
        motion: ~
    default: Idle
clips:
  a: { seconds: 0.1 }
  b: { seconds: 0.1 }
";
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "QuotedNames_Fx", "c0");
            string yamlA = FixpointOracle.Decode(c0);
            StringAssert.Contains("name: \"Loco: Motion\"", yamlA, "the quoted tree name round-trips intact");
            StringAssert.Contains("name: \"Emote: Exit\"", yamlA, "the quoted transition name round-trips intact");

            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, "QuotedNames_Fx", "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "a quoting-requiring name reaches a textual fixpoint");
        }

        // ── 6: a vendor entry transition and a vendor SMB carrying a cosmetic Inspector name decompile
        //      WITHOUT refusal — the completeness sweep's m_Name membership lives per-type (not a blanket
        //      ignore), and entry/SMB names are deliberately in the "tolerated, not captured" half of that
        //      split. This is the check that would FAIL if m_Name were removed from UniversalIgnore without
        //      also being added to EntryTransitionAware and the SMB aware sets. The mirror obligation — that
        //      OUR compiler never emits a named SMB, so the sweep never swallows a name it wrote itself — is
        //      asserted on the behaviours fixture at RoundtripStressTests.Fixpoint_AuthoredYaml ────────────
        [Test]
        public void NamedEntryTransitionAndNamedSmb_DecompileToleratesCosmeticNames()
        {
            string ctrlPath = TestRoot + "/CosmeticNames_Fx.controller";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var sm = rc.layers[0].stateMachine;
            var s = sm.AddState("S");
            var et = sm.AddEntryTransition(s);
            et.name = "SomeEntryName";
            var drv = s.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
            drv.name = "SomeSmbName";
            AssetDatabase.SaveAssets();

            var w = ControllerDecompile.Walk(rc);
            Assert.IsEmpty(w.Refusals,
                "cosmetic entry-transition/SMB names are tolerated (ignored), not refused: " + string.Join(" | ", w.Refusals));
        }

        // ── 7: a `name:` on a CLIP tree child is refused at parse. The whole tree-only key set
        //      (param/paramY/children/normalized/name) shares ONE `if (!hasTree) throw` in
        //      AnimatorSchemaYaml's tree-child switch, so one key witnesses all five: they are only meaningful
        //      when the child is itself a nested tree, and on a clip/ref child must be refused rather than
        //      silently dropped ─────────────────────────────────────────────────────────────────────────────
        [Test]
        public void ClipChildName_ThrowsAtParse()
        {
            const string yaml = @"schema: 1
controller: BadClipChildName_Fx
basis: avatar-root
role: fx
parameters:
  Blend: { type: float, default: 0.0 }
layers:
  - name: L
    states:
      Idle:
        motion:
          tree: 1d
          param: Blend
          children:
            - { clip: a, name: WaveClip, threshold: 0.0 }
    default: Idle
clips:
  a: { seconds: 0.1 }
";
            var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(yaml, "test"));
            StringAssert.Contains("name", ex.Message);
            StringAssert.Contains("nested-tree child", ex.Message);
        }

        // ── 8: regression — a `name:` on a NESTED-TREE child (the valid case the hasTree guard must not
        //      break) still parses and round-trips, mirroring test 1's direct→1d nesting with a name added
        //      to the nested child itself ────────────────────────────────────────────────────────────────
        [Test]
        public void NamedNestedTreeChild_Roundtrips()
        {
            string yaml = @"schema: 1
controller: NamedNestedChild_Fx
basis: avatar-root
role: fx
parameters:
  Dir: { type: float, default: 0.0 }
  W1:  { type: float, default: 0.5 }
layers:
  - name: L
    states:
      Idle:
        motion:
          tree: direct
          children:
            - directWeight: W1
              tree: 1d
              name: SubTree
              param: Dir
              children:
                - { clip: a, threshold: 0.0 }
                - { clip: b, threshold: 1.0 }
    default: Idle
clips:
  a: { seconds: 0.1 }
  b: { seconds: 0.1 }
";
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "NamedNestedChild_Fx", "c0");
            string yamlA = FixpointOracle.Decode(c0);
            StringAssert.Contains("name: SubTree", yamlA, "the nested child's authored name survives decode");

            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, "NamedNestedChild_Fx", "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "a named nested-tree child reaches a textual fixpoint");
        }
    }
}
