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

        // decode(C0)==decode(C1) proves stability, not correct VALUES for in-vocab constructs. Spot-check the
        // highest-risk one: the negative timeScale on a blend-tree child (a sign-flip would round-trip green).
        [Test]
        public void Fixpoint_Blendtrees_PreservesNegativeTimeScale()
        {
            string yaml = FixpointOracle.ReadPackageText(FixDir + "/blendtrees.yaml");
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "Blendtrees_Fx", "c0");
            string decoded = FixpointOracle.Decode(c0);
            StringAssert.Contains("timeScale: -1", decoded, "the negative child timeScale survives decode (no sign flip)");
        }

        // ── Arm B: programmatic refusal coverage ─────────────────────────────────────────────────
        // Each seeds ONE out-of-vocabulary construct; DecompileController must FAIL naming it and write no yaml.
        private void AssertRefuses(string tag, System.Action<AnimatorController> seed, string expectedToken)
        {
            string ctrlPath = TestRoot + "/Refuse_" + tag + ".controller";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            seed(rc);
            AssetDatabase.SaveAssets();

            LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] .*=> FAIL"));
            string yamlOut = TestRoot + "/refuse_" + tag + ".yaml";
            string res = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);

            StringAssert.Contains("FAIL", res);
            StringAssert.Contains(expectedToken, res, "the refusal names the offending construct: " + expectedToken);
            Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
            AnimatorTestHelpers.DeleteRefusalArtifact(res);
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
                // Change-types interleave (Set,Add,Set buckets 0,1,0) but no (type,name) repeats — isolates the
                // INTERLEAVE refusal from the DUPLICATE-operation refusal (both share the substring "driver").
                rc.AddParameter("a", AnimatorControllerParameterType.Float);
                rc.AddParameter("b", AnimatorControllerParameterType.Float);
                var s = rc.layers[0].stateMachine.AddState("S");
                var d = s.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
                var Set = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set;
                var Add = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Add;
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "a", type = Set });
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "a", type = Add });
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "b", type = Set });
            }, "interleave");

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
                // Each clip carries a real binding so neither is empty — isolates the DISTINCT-clips-one-name
                // refusal from the "no animatable content" refusal (an empty clip named "same" would trip that
                // one first, and its message ALSO interpolates the clip name, so "same" alone doesn't discriminate).
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                var clipA = new AnimationClip { name = "same" };
                var clipB = new AnimationClip { name = "same" };
                AnimatorTestHelpers.AddFloatCurve(clipA, "SomePath", typeof(UnityEngine.Transform), "m_LocalPosition.x", 1f);
                AnimatorTestHelpers.AddFloatCurve(clipB, "SomePath", typeof(UnityEngine.Transform), "m_LocalPosition.x", 2f);
                a.motion = clipA;
                b.motion = clipB;
            }, "DISTINCT embedded clips");

        // A condition param whose TRAILING space collides with the single-space separator is unrepresentable
        // in condition position — the decompile self-check renders + re-splits it and refuses (named + located)
        // rather than emit YAML its own recompile would reject. Replaces the deleted character-list refusal;
        // interior-whitespace / flow-delimiter params are now faithful (Arm D).
        [Test] public void Refusal_ConditionParamTrailingSpace() =>
            AssertRefuses("trailingspaceparam", rc =>
            {
                rc.AddParameter(new AnimatorControllerParameter { name = "Fan ", type = AnimatorControllerParameterType.Bool });
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0, "Fan ");
            }, "does not survive");

        // Funnel line-break guard (AnimatorSchemaEmit.ScalarStr): a literal newline in ANY emitted string
        // field tears the line-based YAML, so the serializer throws and the door surfaces a named FAIL. One
        // choke point, three representative sites — a param name, a state name (block-key path), and a
        // behaviour string field (playAudio.sourcePath).
        [Test] public void Funnel_LineBreakInParamName_Fails() =>
            AssertRefuses("lbparam", rc =>
                rc.AddParameter(new AnimatorControllerParameter { name = "Bad\nName", type = AnimatorControllerParameterType.Bool }),
                "line break");

        [Test] public void Funnel_LineBreakInStateName_Fails() =>
            AssertRefuses("lbstate", rc =>
                rc.layers[0].stateMachine.AddState("S").name = "Bad\nState",
                "line break");

        [Test] public void Funnel_LineBreakInBehaviourField_Fails() =>
            AssertRefuses("lbbhv", rc =>
            {
                var s = rc.layers[0].stateMachine.AddState("S");
                var pa = s.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAnimatorPlayAudio>();
                pa.SourcePath = "Bad\nPath";
            }, "line break");

        // ── Arm C: import-tolerance coverage (decode-side normalization, neither clean nor refusal) ──
        [Test]
        public void Tolerance_MixedWriteDefaults_HoistsToModal()
        {
            var rc = AnimatorController.CreateAnimatorControllerAtPath(TestRoot + "/MixedWD_Fx.controller");
            var sm = rc.layers[0].stateMachine;
            foreach (var n in new[] { "A", "B", "C" }) sm.AddState(n).writeDefaultValues = true;  // majority on
            sm.AddState("D").writeDefaultValues = false;                                            // minority off
            AssetDatabase.SaveAssets();

            var w = ControllerDecompile.Walk(rc);
            Assert.IsEmpty(w.Refusals, "mixed WD is tolerated, not refused");
            string yaml = AnimatorSchemaEmit.Serialize(w.Doc);
            StringAssert.Contains("writeDefaults: false", yaml, "the minority state keeps an explicit override");
            Assert.IsTrue(w.Notes.Exists(n => n.ToLower().Contains("write default")),
                "the mixed-WD tolerance is recorded in Notes");
        }

        [Test]
        public void Tolerance_EmptyTimeParameter_NormalizesToUnboundMotionTime()
        {
            var rc = AnimatorController.CreateAnimatorControllerAtPath(TestRoot + "/EmptyTP_Fx.controller");
            var s = rc.layers[0].stateMachine.AddState("S");
            s.timeParameterActive = true;
            s.timeParameter = "";                    // active but empty -> the vendor-Gesture tolerance
            AssetDatabase.SaveAssets();

            var w = ControllerDecompile.Walk(rc);
            Assert.IsEmpty(w.Refusals, "empty timeParameter is tolerated, not refused");
            string yaml = AnimatorSchemaEmit.Serialize(w.Doc);
            StringAssert.DoesNotContain("motionTimeParam", yaml, "normalized to unbound motion time");
            Assert.IsTrue(w.Notes.Exists(n => n.ToLower().Contains("time")),
                "the timeParameter tolerance is recorded in Notes");
        }

        // ── Arm D: faithful condition params (formerly refused) ──────────────────────────────────────
        // A condition parameter carrying spaces or a flow delimiter is FAITHFUL now — no rename, no refusal:
        // it decompiles cleanly and the emitted condition reaches a textual fixpoint (the comma param emits as
        // ONE quoted scalar). Converted from the deleted whitespace/delimiter refusal tests.
        private void AssertConditionParamRoundtrips(string tag, string paramName, bool quotedInYaml)
        {
            string ctrlName = "CondRT_" + tag + "_Fx";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(TestRoot + "/" + ctrlName + ".controller");
            rc.AddParameter(paramName, AnimatorControllerParameterType.Bool);
            var sm = rc.layers[0].stateMachine;
            var a = sm.AddState("A"); var b = sm.AddState("B");
            a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0, paramName);
            AssetDatabase.SaveAssets();

            var w = ControllerDecompile.Walk(rc);
            Assert.IsEmpty(w.Refusals,
                "a spaced/delimiter condition param is faithful, not refused: " + string.Join(" | ", w.Refusals));

            string yamlA = AnimatorSchemaEmit.Serialize(w.Doc);
            string cond = paramName + " is true";
            StringAssert.Contains(quotedInYaml ? "[ \"" + cond + "\" ]" : "[ " + cond + " ]", yamlA,
                "the condition emits as " + (quotedInYaml ? "one quoted scalar" : "an unquoted scalar"));

            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, ctrlName, "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "the faithful condition param reaches a textual fixpoint");
        }

        [Test] public void Faithful_SpacedConditionParam_Roundtrips() =>
            AssertConditionParamRoundtrips("spaced", "p q", quotedInYaml: false);

        [Test] public void Faithful_CommaConditionParam_EmitsOneQuotedScalar_Roundtrips() =>
            AssertConditionParamRoundtrips("comma", "a,b", quotedInYaml: true);
    }
}
