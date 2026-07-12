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
    // Behavioral tests for the blend-tree name round-trip (compile <-> decompile): a human-authored `name:`
    // survives; an auto-named tree (the positional <State>_BlendTree / <parent>_<i> default) stays nameless in
    // YAML; a name that cannot round-trip the line-based YAML (a line break) is refused, not mangled. Run
    // headless via tools/run-editmode-tests.ps1 (or the Test Runner window / CI); not via MCP run_tests —
    // wrong venue (live editor). See docs/verify.md.
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
    }
}
