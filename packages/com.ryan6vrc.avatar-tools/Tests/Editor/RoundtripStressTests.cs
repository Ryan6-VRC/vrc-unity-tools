using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Synthetic round-trip stress fixtures spanning the whole schema vocabulary. Three arms:
    //  A (Fixpoint_AuthoredYaml): in-vocabulary breadth via hand-authored YAML — clean textual fixpoint.
    //  B (Refusal_*):            out-of-vocabulary constructs — DecompileController FAIL, no yaml written.
    //  C (Tolerance_*):          messy-but-legal input — decode normalizes it and notes the tolerance.
    public class RoundtripStressTests
    {
        private const string TestRoot = "Assets/Agent/Scratch/stress_tests";
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

        // ── Arm A: authored-YAML clean fixpoint ──────────────────────────────────────────────────
        // The `name` argument MUST equal the fixture's `controller:` value (CompileTo loads <name>.controller).
        [TestCase("blendtrees.yaml", "Blendtrees_Fx")]
        [TestCase("addressing.yaml", "Addressing_Fx")]
        [TestCase("behaviours.yaml", "Behaviours_Fx")]
        [TestCase("clips.yaml", "Clips_Fx")]
        [TestCase("integration.yaml", "Integration_Fx")]
        public void Fixpoint_AuthoredYaml(string fixture, string name)
        {
            string yaml = FixpointOracle.ReadPackageText(FixDir + "/" + fixture);
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, name, "c0");
            string yamlA = FixpointOracle.Decode(c0);
            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, name, "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "authored fixture reaches a textual fixpoint: " + fixture);
            StringAssert.Contains("=> PASS", CheckAnimator.Lint(c1, "explicit", null, null, null));
        }

        // decode(C0)==decode(C1) proves fixpoint-stability, NOT that the AUTHORED arrangement survived.
        // Assert the hand-placed off-grid coordinate reaches the decoded YAML (layout round-trip).
        [Test]
        public void Fixpoint_Addressing_PreservesAuthoredLayout()
        {
            string yaml = FixpointOracle.ReadPackageText(FixDir + "/addressing.yaml");
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "Addressing_Fx", "c0");
            string decoded = FixpointOracle.Decode(c0);
            StringAssert.Contains("layout:", decoded, "the authored arrangement survives decode");
            StringAssert.Contains("[720, 40]", decoded, "the authored off-grid Top coordinate is preserved");
        }

        // ── Arm B: programmatic refusal coverage ─────────────────────────────────────────────────
        // Each seeds ONE out-of-vocabulary construct; DecompileController must FAIL naming it and write no yaml.
        private void AssertRefuses(string tag, System.Action<AnimatorController> seed, string expectedToken)
        {
            string ctrlPath = TestRoot + "/Refuse_" + tag + ".controller";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            seed(rc);
            AssetDatabase.SaveAssets();

            LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
            string yamlOut = TestRoot + "/refuse_" + tag + ".yaml";
            string res = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);

            StringAssert.Contains("FAIL", res);
            StringAssert.Contains(expectedToken, res, "the refusal names the offending construct: " + expectedToken);
            Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
        }

        [Test] public void Refusal_SyncedLayer() =>
            AssertRefuses("synced", rc => AnimatorTestHelpers.AddSyncedLayer(rc), "synced");

        [Test] public void Refusal_TriggerParam() =>
            AssertRefuses("trigger", rc => rc.AddParameter("T", AnimatorControllerParameterType.Trigger), "Trigger");

        [Test] public void Refusal_IkPassLayer() =>
            AssertRefuses("ikpass", rc =>
            {
                var layers = rc.layers;
                layers[0].iKPass = true;
                rc.layers = layers;               // AnimatorControllerLayer is a struct — reassign the array
            }, "IK");

        [Test] public void Refusal_StateTag() =>
            AssertRefuses("tag", rc => rc.layers[0].stateMachine.AddState("S").tag = "MyTag", "Tag");

        [Test] public void Refusal_TransitionOffset() =>
            AssertRefuses("offset", rc =>
            {
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                rc.AddParameter("g", AnimatorControllerParameterType.Bool);
                var t = a.AddTransition(b); t.AddCondition(AnimatorConditionMode.If, 0, "g");
                t.offset = 0.3f;
            }, "Offset");

        [Test] public void Refusal_MirrorParam() =>
            AssertRefuses("mirrorparam", rc =>
            {
                rc.AddParameter("m", AnimatorControllerParameterType.Bool);
                var s = rc.layers[0].stateMachine.AddState("S");
                s.mirrorParameterActive = true; s.mirrorParameter = "m";
            }, "mirror");

        [Test] public void Refusal_SubMachineOnExit() =>
            AssertRefuses("onexit", rc =>
            {
                rc.AddParameter("g", AnimatorControllerParameterType.Bool);
                var root = rc.layers[0].stateMachine;
                var sub = root.AddStateMachine("Sub");
                var dst = root.AddState("Dst");
                var t = root.AddStateMachineExitTransition(sub);
                t.AddCondition(AnimatorConditionMode.If, 0, "g");
                t.destinationState = dst;
            }, "Exit");

        // NOTE: "unsupported SMB type" is NOT witnessable here. AddStateMachineBehaviour<T> needs T's MonoScript
        // in a runtime-valid assembly; a StateMachineBehaviour declared in this Editor-only test asmdef is
        // rejected ("Can't find monoscript") and no-ops, so the seed would decode clean. Same category as the
        // (also excluded) "unsupported motion type" — see the round-trip stress task notes.

        [Test] public void Refusal_UnknownConditionMode() =>
            AssertRefuses("condmode", rc =>
            {
                rc.AddParameter("g", AnimatorControllerParameterType.Bool);
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                var t = a.AddTransition(b);
                t.AddCondition((AnimatorConditionMode)99, 0, "g");
            }, "condition");

        [Test] public void Refusal_UnknownDriverChangeType() =>
            AssertRefuses("changetype", rc =>
            {
                rc.AddParameter("p", AnimatorControllerParameterType.Float);
                var s = rc.layers[0].stateMachine.AddState("S");
                var d = s.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
                {
                    name = "p",
                    type = (VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType)99
                });
            }, "ChangeType");

        [Test] public void Refusal_InterleavedDriverOps() =>
            AssertRefuses("interleave", rc =>
            {
                rc.AddParameter("p", AnimatorControllerParameterType.Float);
                var s = rc.layers[0].stateMachine.AddState("S");
                var d = s.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
                var Set = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set;
                var Add = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Add;
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "p", type = Set });
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "p", type = Add });
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "p", type = Set });
            }, "driver");

        [Test] public void Refusal_IdenticalSiblingStates() =>
            AssertRefuses("dupstate", rc =>
            {
                var sm = rc.layers[0].stateMachine;
                sm.AddState("Dup");
                sm.AddState("tmp").name = "Dup";   // AddState uniquifies its arg; assign the collision directly
            }, "Dup");

        [Test] public void Refusal_StateSubmachineClash() =>
            AssertRefuses("clash", rc =>
            {
                var sm = rc.layers[0].stateMachine;
                sm.AddState("X"); sm.AddStateMachine("X");
            }, "X");

        [Test] public void Refusal_WhitespaceSiblingStates() =>
            AssertRefuses("wsstate", rc =>
            {
                var sm = rc.layers[0].stateMachine;
                sm.AddState("WS"); sm.AddState(" WS");   // differ only by SURROUNDING whitespace (Trim collides)
            }, "whitespace");

        [Test] public void Refusal_BareExitState() =>
            AssertRefuses("exitname", rc =>
            {
                rc.AddParameter("g", AnimatorControllerParameterType.Bool);
                var sm = rc.layers[0].stateMachine;
                var real = sm.AddState("Exit");
                var other = sm.AddState("Other");
                other.AddTransition(real).AddCondition(AnimatorConditionMode.If, 0, "g");
            }, "Exit");

        [Test] public void Refusal_TwoClipsOneName() =>
            AssertRefuses("dupclip", rc =>
            {
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                a.motion = new AnimationClip { name = "same" };
                b.motion = new AnimationClip { name = "same" };
            }, "same");

        [Test] public void Refusal_ConditionParamWhitespace() =>
            AssertRefuses("wsparam", rc =>
            {
                rc.AddParameter("p q", AnimatorControllerParameterType.Bool);
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0, "p q");
            }, "p q");

        [Test] public void Refusal_ConditionParamFlowDelimiter() =>
            AssertRefuses("flowparam", rc =>
            {
                rc.AddParameter("a,b", AnimatorControllerParameterType.Bool);
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0, "a,b");
            }, "a,b");
    }
}
