using System.IO;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // A human-authored `name:` survives the compile <-> decompile round trip; an auto-named tree (the
    // positional <State>_BlendTree / <parent>_<i> default) stays nameless in YAML.
    //
    // NOT here: a name carrying a line break. That is the serializer's one funnel guard
    // (AnimatorSchemaEmit.CheckNoLineBreak, reached by every name/path/field through ScalarStr), witnessed
    // once at RoundtripStressTests.Funnel_LineBreakInParamName_Fails — a name-position case only re-walks it.
    public class ControllerNameRoundtripTests
    {
        private const string TestRoot = "Assets/Agent/Scratch/name_roundtrip_tests";

        [SetUp]
        public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

        // Per-test deletion is load-bearing: CompileTo reuses a controller already sitting in its outDir (the
        // compile door's GUID-stable path), so a leaked artifact would change which branch the next case
        // exercises. No AssetDatabase.Refresh() on either side: CreateFolder registers the folder and writes its
        // .meta, and DeleteAsset closes its own import.
        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
            if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
        }

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

        // The state ladder witnesses both ladders: emit funnels every state/AnyState transition through
        // ControllerEmit.ConfigureStateTransition and decode through ControllerDecompile.DecodeStateTransition.
        // The one field each ladder handles outside those two is `canTransitionToSelf`, covered by the
        // addressing.yaml fixture at RoundtripStressTests.Fixpoint_AuthoredYaml and by
        // ControllerDecompileTests.Walk_SelfRung_*.
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

        // The completeness sweep's m_Name membership is per-type, and entry/SMB names are tolerated, not
        // captured. Fails if m_Name leaves EntryTransitionAware or DriverAware. The mirror obligation, that our
        // compiler never emits a named SMB, is asserted on the behaviours fixture at
        // RoundtripStressTests.Fixpoint_AuthoredYaml.
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

        // The whole tree-only key set (param/paramY/children/normalized/name) shares one `if (!hasTree) throw`
        // in AnimatorSchemaYaml's tree-child switch, so one key witnesses all five.
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
