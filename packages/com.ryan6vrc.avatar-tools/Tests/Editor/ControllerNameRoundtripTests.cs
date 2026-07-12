using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Behavioral tests for the name round-trip (compile <-> decompile) of blend trees AND state/AnyState
    // transitions: a human-authored `name:` survives; an auto-named tree (the positional <State>_BlendTree /
    // <parent>_<i> default) or an unnamed transition stays nameless in YAML; a name that cannot round-trip the
    // line-based YAML (a line break) is refused, not mangled; a `name:` on an entry-ladder rung is refused at
    // parse (the entry emit path never reads it). Also covers the completeness sweep's per-type m_Name
    // membership: a cosmetic entry-transition/SMB name is tolerated (not refused), and our own compiler never
    // emits a named SMB (forward-safety). Run headless via tools/run-editmode-tests.ps1 (or the Test Runner
    // window / CI); not via MCP run_tests — wrong venue (live editor). See docs/verify.md.
    public class ControllerNameRoundtripTests
    {
        private const string TestRoot = "Assets/Agent/Scratch/name_roundtrip_tests";
        private const string FixDir = "Packages/com.ryan6vrc.avatar-tools/Tests/Editor/Fixtures/RoundtripStress";

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(TestRoot);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
            if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
            AssetDatabase.Refresh();
        }

        // ── 1: a named top-level tree round-trips ────────────────────────────────────────────────────
        [Test]
        public void NamedTopLevelTree_Roundtrips()
        {
            string yaml = @"schema: 1
controller: NamedTree_Fx
basis: avatar-root
role: fx
defaults:
  writeDefaults: on
parameters:
  Blend: { type: float, default: 0.0 }
layers:
  - name: L
    states:
      Idle:
        motion:
          tree: 1d
          name: Locomotion
          param: Blend
          children:
            - { clip: a, threshold: 0.0 }
            - { clip: b, threshold: 1.0 }
    default: Idle
clips:
  a: { seconds: 0.1 }
  b: { seconds: 0.1 }
";
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "NamedTree_Fx", "c0");
            string yamlA = FixpointOracle.Decode(c0);
            StringAssert.Contains("name: Locomotion", yamlA, "the authored tree name survives decode");

            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, "NamedTree_Fx", "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "a named tree reaches a textual fixpoint");
        }

        // ── 2: a named parent + unnamed child — the child does NOT inherit an explicit name, and the whole
        //      document still reaches a fixpoint on a second pass ──────────────────────────────────────
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

        // ── 3: an auto-named tree stays nameless — no new `name:` key appears anywhere in the decoded
        //      document, and the existing whole-vocabulary fixture recompiles byte-identical ───────────
        [Test]
        public void UnnamedTree_DecompilesWithNoNameKey()
        {
            string yaml = FixpointOracle.ReadPackageText(FixDir + "/blendtrees.yaml");
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "Blendtrees_Fx", "c0");
            string decoded = FixpointOracle.Decode(c0);

            // The fixture's ONLY `name:` occurrence is the layer's own name field ("- name: Trees") — none of
            // its (deliberately unnamed) blend trees should surface a `name:` key.
            int nameKeyCount = decoded.Split(new[] { "name:" }, StringSplitOptions.None).Length - 1;
            Assert.AreEqual(1, nameKeyCount, "only the layer's own 'name:' key appears — zero new blend-tree name keys");

            var c1 = FixpointOracle.CompileTo(TestRoot, decoded, "Blendtrees_Fx", "c1");
            string decoded2 = FixpointOracle.Decode(c1);
            Assert.AreEqual(decoded, decoded2, "recompiles byte-identical (fixpoint)");
        }

        // ── 4: a default-CONSTRUCTED Unity name ("Blend Tree") is not the schema's auto-generated default
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

        // ── 5: a name containing a line break cannot round-trip the line-based YAML — refuse it rather
        //      than silently mangle it ──────────────────────────────────────────────────────────────────
        [Test]
        public void TreeNameWithLineBreak_IsRefused()
        {
            string ctrlPath = TestRoot + "/NewlineName_Fx.controller";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var idle = rc.layers[0].stateMachine.AddState("Idle");
            var bt = new BlendTree { name = "Bad\nName", blendType = BlendTreeType.Direct };
            AssetDatabase.AddObjectToAsset(bt, rc);
            idle.motion = bt;
            AssetDatabase.SaveAssets();

            LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
            string yamlOut = TestRoot + "/newlinename.yaml";
            string res = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);

            StringAssert.Contains("FAIL", res);
            StringAssert.Contains("line break", res, "the refusal names the offending construct");
            Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
        }

        // ── 6: a named STATE-ladder transition round-trips ──────────────────────────────────────────────
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

        // ── 7: a named ANYSTATE-ladder transition round-trips too — proves the shared
        //      ConfigureStateTransition path (not just the state ladder) assigns the name ─────────────────
        [Test]
        public void NamedAnyStateTransition_Roundtrips()
        {
            string yaml = @"schema: 1
controller: NamedAnyTransition_Fx
basis: avatar-root
role: fx
parameters:
  Go: bool
layers:
  - name: L
    states:
      Idle:
        motion: ~
      Emote:
        motion: ~
    any:
      - { to: Emote, name: AnyEmote, when: [ Go is true ], canTransitionToSelf: false }
    default: Idle
";
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "NamedAnyTransition_Fx", "c0");
            string yamlA = FixpointOracle.Decode(c0);
            StringAssert.Contains("name: AnyEmote", yamlA, "the authored AnyState transition name survives decode");

            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, "NamedAnyTransition_Fx", "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "a named AnyState transition reaches a textual fixpoint");
        }

        // ── 8: a `name:` on an entry-ladder rung is refused AT PARSE — the entry-emit path never reads a
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

        // ── 9: a transition name containing a line break cannot round-trip the line-based YAML — refuse it
        //      rather than silently mangle it (the transition mirror of test 5) ───────────────────────────
        [Test]
        public void TransitionNameWithLineBreak_IsRefused()
        {
            string ctrlPath = TestRoot + "/NewlineTransitionName_Fx.controller";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var idle = rc.layers[0].stateMachine.AddState("Idle");
            var tr = idle.AddExitTransition();
            tr.name = "Bad\nName";
            AssetDatabase.SaveAssets();

            LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
            string yamlOut = TestRoot + "/newlinetransitionname.yaml";
            string res = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);

            StringAssert.Contains("FAIL", res);
            StringAssert.Contains("line break", res, "the refusal names the offending construct");
            Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
        }

        // ── 10: a name requiring YAML quoting (a colon) round-trips intact — the first feature to put
        //       arbitrary human text through ScalarStr/NeedsQuote in a name position — for both a blend-tree
        //       name and a transition name ───────────────────────────────────────────────────────────────
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

        // ── 11: a vendor entry transition and a vendor SMB carrying a cosmetic Inspector name decompile
        //       WITHOUT refusal — the completeness sweep's m_Name membership now lives per-type (not a blanket
        //       ignore), and entry/SMB names are deliberately in the "tolerated, not captured" half of that
        //       split. This is the check that would FAIL if m_Name were removed from UniversalIgnore without
        //       also being added to EntryTransitionAware and the SMB aware sets ─────────────────────────────
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

        // ── 12: forward-safety — an SMB emitted by OUR OWN compiler always carries an empty m_Name, so the
        //       sweep (which ignores, not refuses, an SMB's m_Name) never trips on the compiler's own output ──
        [Test]
        public void CompilerEmittedSmb_HasEmptyName()
        {
            string yaml = FixpointOracle.ReadPackageText(FixDir + "/behaviours.yaml");
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "Behaviours_Fx", "c0");
            string path = AssetDatabase.GetAssetPath(c0);
            var behaviours = AssetDatabase.LoadAllAssetsAtPath(path).OfType<StateMachineBehaviour>().ToList();
            Assert.IsNotEmpty(behaviours, "the behaviours fixture emits at least one SMB sub-asset");
            foreach (var smb in behaviours)
                Assert.IsTrue(string.IsNullOrEmpty(smb.name),
                    $"compiler-emitted SMB '{smb.GetType().Name}' should have an empty m_Name, got '{smb.name}'");
        }
    }
}
