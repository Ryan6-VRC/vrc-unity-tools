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

        [Test] public void Refusal_MirrorParam() =>
            AssertRefuses("mirrorparam", rc =>
            {
                rc.AddParameter("m", AnimatorControllerParameterType.Bool);
                var s = rc.layers[0].stateMachine.AddState("S");
                s.mirrorParameterActive = true; s.mirrorParameter = "m";
            }, "mirror");

        // ── Arm D: widened constructs — formerly Arm B refusals ──────────────────────────────────
        // Transition offset and sub-machine onExit transitions used to be named refusals here. Both now
        // round-trip, so what has to be pinned is the VALUE surviving a real compile, not the wording of a
        // FAIL: a widen whose value silently resets to Unity's default would still reach a clean fixpoint.
        // Each seeds the construct on a real controller, decodes, recompiles, and reads the value back off
        // the recompiled object.
        // `yaml` is the decode of the HAND-SEEDED controller — the vendor-shaped input, and where to assert the
        // construct reached the schema at all. Fixpoint is asserted between the first and second COMPILED
        // generations, not against `yaml`: a hand-built controller is free to differ from what the compiler
        // emits (it may carry no default at all, which the compiler always writes), and Arm A's contract is
        // defined from compiled input for that reason. The returned controller is generation 1.
        private AnimatorController SeedAndRoundTrip(string tag, System.Action<AnimatorController> seed, out string yaml)
        {
            string name = "Widen_" + tag;
            var rc = AnimatorController.CreateAnimatorControllerAtPath(TestRoot + "/" + name + ".controller");
            seed(rc);
            AssetDatabase.SaveAssets();
            yaml = FixpointOracle.Decode(rc);
            var c1 = FixpointOracle.CompileTo(TestRoot, yaml, name, "c1");
            string yamlA = FixpointOracle.Decode(c1);
            var c2 = FixpointOracle.CompileTo(TestRoot, yamlA, name, "c2");
            Assert.AreEqual(yamlA, FixpointOracle.Decode(c2), "the widened construct reaches a textual fixpoint");
            return c1;
        }

        [Test]
        public void Widen_TransitionOffset_survives_the_round_trip()
        {
            var back = SeedAndRoundTrip("offset", rc =>
            {
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                rc.AddParameter("g", AnimatorControllerParameterType.Bool);
                var t = a.AddTransition(b); t.AddCondition(AnimatorConditionMode.If, 0, "g");
                t.offset = 0.3f;
            }, out string yaml);

            StringAssert.Contains("offset: 0.3", yaml, "the offset reaches the emitted YAML");
            var sm2 = back.layers[0].stateMachine;
            var from = System.Array.Find(sm2.states, s => s.state.name == "A").state;
            Assert.That(from.transitions[0].offset, Is.EqualTo(0.3f).Within(1e-5f),
                "the recompiled transition carries the offset, not Unity's 0 — a reset would still fixpoint");
        }

        // The pose-picker shape the widen exists for: one clip, speed 0, two states told apart ONLY by their
        // incoming offset. Zeroing it collapses both onto frame 0, so the two offsets must stay distinct.
        [Test]
        public void Widen_TransitionOffset_keeps_two_pose_picker_edges_distinct()
        {
            var back = SeedAndRoundTrip("posepicker", rc =>
            {
                var sm = rc.layers[0].stateMachine;
                rc.AddParameter("g", AnimatorControllerParameterType.Bool);
                var closed = sm.AddState("Closed"); var open = sm.AddState("Open");
                closed.speed = 0f; open.speed = 0f;
                var there = closed.AddTransition(open); there.AddCondition(AnimatorConditionMode.If, 0, "g");
                there.offset = 1f;                                     // enter Open at the clip's END
                var back2 = open.AddTransition(closed); back2.AddCondition(AnimatorConditionMode.IfNot, 0, "g");
                back2.offset = 0f;                                     // enter Closed at the start
            }, out _);

            var sm3 = back.layers[0].stateMachine;
            var c = System.Array.Find(sm3.states, s => s.state.name == "Closed").state;
            var o = System.Array.Find(sm3.states, s => s.state.name == "Open").state;
            Assert.That(c.transitions[0].offset, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(o.transitions[0].offset, Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void Widen_SubMachineOnExit_survives_the_round_trip()
        {
            var back = SeedAndRoundTrip("onexit", rc =>
            {
                rc.AddParameter("g", AnimatorControllerParameterType.Bool);
                var root = rc.layers[0].stateMachine;
                var sub = root.AddStateMachine("Sub");
                sub.AddState("Inner");
                var dst = root.AddState("Dst");
                // AddStateMachineTransition, not AddStateMachineExitTransition + destinationState: the latter
                // leaves isExit set, so the object claims both a destination and an exit, and the decoder
                // (isExit first) reads it as a plain exit. That is a contradictory object no Inspector can
                // author — the two real shapes are a targeted edge (here) and an exit edge (next test).
                var t = root.AddStateMachineTransition(sub, dst);
                t.AddCondition(AnimatorConditionMode.If, 0, "g");
            }, out string yaml);

            StringAssert.Contains("onExit:", yaml, "the sub-machine's outgoing edge reaches the emitted YAML");
            var root2 = back.layers[0].stateMachine;
            var sub2 = System.Array.Find(root2.stateMachines, m => m.stateMachine.name == "Sub").stateMachine;
            var edges = root2.GetStateMachineTransitions(sub2);
            Assert.That(edges.Length, Is.EqualTo(1), "exactly the one seeded outgoing edge, on the PARENT machine");
            Assert.That(edges[0].destinationState.name, Is.EqualTo("Dst"));
            Assert.That(edges[0].conditions[0].parameter, Is.EqualTo("g"));
        }

        // `to: Exit` on an onExit list means "pass the child's exit up to THIS machine's Exit" — a shape the
        // entry ladder cannot express, so it needs its own pin.
        [Test]
        public void Widen_SubMachineOnExit_can_target_the_parent_Exit()
        {
            var back = SeedAndRoundTrip("onexit_exit", rc =>
            {
                var root = rc.layers[0].stateMachine;
                var sub = root.AddStateMachine("Sub");
                sub.AddState("Inner");
                root.AddStateMachineExitTransition(sub);               // unconditional, straight to Exit
            }, out string yaml);

            StringAssert.Contains("to: Exit", yaml);
            var root2 = back.layers[0].stateMachine;
            var sub2 = System.Array.Find(root2.stateMachines, m => m.stateMachine.name == "Sub").stateMachine;
            Assert.IsTrue(root2.GetStateMachineTransitions(sub2)[0].isExit);
        }

        // A repeated driver Set is behaviourally redundant — the later write supersedes the earlier one in
        // full — so it is tolerated with a Note rather than refused. Add is NOT: two Adds move a parameter
        // twice, and a name-keyed bucket can only hold one. That asymmetry is the whole of the widen.
        [Test]
        public void Repeated_driver_Set_refuses_even_though_the_later_write_supersedes()
        {
            AssertRefuses("driverset", rc =>
            {
                rc.AddParameter("p", AnimatorControllerParameterType.Float);
                var s = rc.layers[0].stateMachine.AddState("S");
                var d = s.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "p", value = 1f });
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "p", value = 2f });
            }, "repeats operation");
        }

        // The counterexample that killed the tolerance, kept as a fixture because it is why the refusal is
        // unconditional. Every Copy sits in ONE bucket, so a read-after-write between two writes to the same
        // parameter can never trip the interleave check — and the name-keyed bucket hoists the second write
        // ahead of the read (a Dictionary overwrite keeps the original insertion slot), so C would come back
        // holding D's value instead of B's. Single-change-type, so nothing about the list looks disordered.
        [Test]
        public void Repeated_driver_Copy_refuses_though_a_single_bucket_never_interleaves()
        {
            AssertRefuses("drivercopy", rc =>
            {
                foreach (var n in new[] { "A", "B", "C", "D" })
                    rc.AddParameter(n, AnimatorControllerParameterType.Float);
                var s = rc.layers[0].stateMachine.AddState("S");
                var d = s.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
                var copy = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy;
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "A", source = "B", type = copy });
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "C", source = "A", type = copy });
                d.parameters.Add(new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter { name = "A", source = "D", type = copy });
            }, "repeats operation");
        }

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
