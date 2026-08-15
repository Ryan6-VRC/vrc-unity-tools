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
    // Synthetic round-trip stress fixtures spanning the whole schema vocabulary. Three arms, named by test
    // prefix rather than by letter (the prefix is the index a reader greps):
    //  Fixpoint_*            in-vocabulary breadth via hand-authored YAML — a clean textual fixpoint.
    //  Refusal_* / Funnel_*  out-of-vocabulary constructs — DecompileController FAIL, no yaml written.
    //  Widen_* / Faithful_*  constructs that USED to be refusals. For a widen the assertion is the VALUE
    //                        read back off the recompiled object, because a widen that silently reset to
    //                        Unity's default would still reach a clean fixpoint.
    // Decode-side TOLERANCES (mixed write-defaults, an empty timeParameter) are ControllerDecompile.Walk's
    // decisions, not this door's: they are witnessed in-memory in ControllerDecompileTests, never recompiled
    // here — a compile round adds ~0.2s and re-proves nothing about the tolerance.
    public class RoundtripStressTests
    {
        private const string TestRoot = "Assets/Agent/Scratch/stress_tests";
        private const string FixDir = "Packages/com.ryan6vrc.avatar-tools/Tests/Editor/Fixtures/RoundtripStress";

        [SetUp]
        public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

        // Per-test deletion is load-bearing, not hygiene: several cases assert a refusal writes NO .yaml, so
        // the root has to start empty. No AssetDatabase.Refresh() on either side — CreateFolder registers the
        // folder AND writes its .meta, and DeleteAsset closes its own import.
        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
            if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
        }

        // ── Fixpoint_*: authored-YAML clean fixpoint ─────────────────────────────────────────────
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
            StringAssert.Contains("=> PASS", CheckAnimator.Run(c1, "explicit", null, null, null));
            AssertAuthoredValuesSurvived(fixture, yamlA, c0);
        }

        // decode(C0)==decode(C1) proves fixpoint STABILITY, not that the authored arrangement or the authored
        // VALUES survived — a sign flip, a dropped layout block, or a leaked auto-generated name is stable and
        // therefore invisible to the equality above. Each fixture's highest-risk authored detail is pinned
        // here, on the FIRST decode, so no case needs a second compile of a fixture this one already built.
        private static void AssertAuthoredValuesSurvived(string fixture, string yamlA, AnimatorController c0)
        {
            switch (fixture)
            {
                case "addressing.yaml":
                    StringAssert.Contains("layout:", yamlA, "the authored arrangement survives decode");
                    StringAssert.Contains("[720, 40]", yamlA, "the authored off-grid Top coordinate is preserved");
                    break;

                case "blendtrees.yaml":
                    StringAssert.Contains("timeScale: -1", yamlA, "the negative child timeScale survives decode (no sign flip)");
                    // This fixture's blend trees are deliberately unnamed, so the layer's own "- name: Trees"
                    // must be the document's ONLY `name:` key — an auto-named tree (the positional
                    // <State>_BlendTree / <parent>_<i> default) surfacing as an authored name raises the count.
                    Assert.AreEqual(1, Regex.Matches(yamlA, "name:").Count,
                        "only the layer's own 'name:' key appears — zero blend-tree name keys");
                    break;

                case "behaviours.yaml":
                    // Forward-safety: the completeness sweep IGNORES an SMB's m_Name rather than refusing it,
                    // so our own compiler must never emit a named SMB — otherwise the sweep would silently
                    // drop a name the compiler itself wrote.
                    int smbs = 0;
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(c0)))
                    {
                        if (!(o is StateMachineBehaviour smb)) continue;
                        smbs++;
                        Assert.IsTrue(string.IsNullOrEmpty(smb.name),
                            "compiler-emitted SMB '" + smb.GetType().Name + "' should have an empty m_Name, got '" + smb.name + "'");
                    }
                    Assert.Greater(smbs, 0, "the behaviours fixture emits at least one SMB sub-asset");
                    break;
            }
        }

        // ── Refusal_* / Funnel_*: programmatic refusal coverage ──────────────────────────────────
        // Each seeds ONE out-of-vocabulary construct; DecompileController must FAIL naming it and write no yaml.
        //
        // SCOPE RULE — do not regrow this arm. A CONSTRUCT's refusal is proven at ControllerDecompile.Walk,
        // in memory, for ~1ms per case (ControllerDecompileTests). The DOOR has exactly ONE refusal branch —
        // DecompileController's `if (walk.Refusals.Count > 0) return Fail(…)` — so a case here re-proves that
        // single branch plus the artifact grammar (the FAIL names the offender, no .yaml is written), at the
        // cost of a real CreateAnimatorControllerAtPath + SaveAssets + Decompile + artifact delete (~0.2s).
        // A case earns its place here only by being the sole witness for its construct; add the construct at
        // Walk instead.
        private void AssertRefuses(string tag, System.Action<AnimatorController> seed, string expectedToken)
        {
            string ctrlPath = TestRoot + "/Refuse_" + tag + ".controller";
            var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            seed(rc);
            AssetDatabase.SaveAssets();

            LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] .*=> FAIL"));
            string yamlOut = TestRoot + "/refuse_" + tag + ".yaml";
            string res = DecompileController.Run(ctrlPath, yamlOut, whatIf: false);

            StringAssert.Contains("FAIL", res);
            StringAssert.Contains(expectedToken, res, "the refusal names the offending construct: " + expectedToken);
            Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
            AnimatorTestHelpers.DeleteRefusalArtifact(res);
        }

        [Test] public void Refusal_SyncedLayer() =>
            AssertRefuses("synced", rc => AnimatorTestHelpers.AddSyncedLayer(rc), "synced");

        [Test] public void Refusal_MirrorParam() =>
            AssertRefuses("mirrorparam", rc =>
            {
                rc.AddParameter("m", AnimatorControllerParameterType.Bool);
                var s = rc.layers[0].stateMachine.AddState("S");
                s.mirrorParameterActive = true; s.mirrorParameter = "m";
            }, "mirror");

        // ── Widen_*: constructs that were once refusals ──────────────────────────────────────────
        // Transition offset and sub-machine onExit transitions used to be named refusals here. Both now
        // round-trip, so what has to be pinned is the VALUE surviving a real compile, not the wording of a
        // FAIL: a widen whose value silently resets to Unity's default would still reach a clean fixpoint.
        // Each seeds the construct on a real controller, decodes, recompiles, and reads the value back off
        // the recompiled object.
        // `yaml` is the decode of the HAND-SEEDED controller — the vendor-shaped input, and where to assert the
        // construct reached the schema at all. Fixpoint is asserted between the first and second COMPILED
        // generations, not against `yaml`: a hand-built controller is free to differ from what the compiler
        // emits (it may carry no default at all, which the compiler always writes), and the Fixpoint_* arm's
        // contract is defined from compiled input for that reason. The returned controller is generation 1.
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

        // A repeated driver write is refused for EVERY change type — no Set/Add asymmetry and no widen. The
        // rationale lives at the call site (ControllerDecompile.DetectDriverOrderLoss): the schema CAN
        // represent such a driver, so the refusal is about not NORMALIZING someone else's controller, not
        // about representability.
        //
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

        // A condition param whose TRAILING space collides with the single-space separator is unrepresentable
        // in condition position — the decompile self-check renders + re-splits it and refuses (named + located)
        // rather than emit YAML its own recompile would reject. A TRAILING space is the only unrepresentable
        // case; interior-whitespace and flow-delimiter params are faithful (Faithful_* below).
        [Test] public void Refusal_ConditionParamTrailingSpace() =>
            AssertRefuses("trailingspaceparam", rc =>
            {
                rc.AddParameter(new AnimatorControllerParameter { name = "Fan ", type = AnimatorControllerParameterType.Bool });
                var sm = rc.layers[0].stateMachine;
                var a = sm.AddState("A"); var b = sm.AddState("B");
                a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0, "Fan ");
            }, "does not survive");

        // Funnel line-break guard: a literal newline in ANY emitted string field tears the line-based YAML, so
        // the serializer throws and the door surfaces a named FAIL. ONE site witnesses it, because
        // AnimatorSchemaEmit's block-key writer is literally `Key(s) => ScalarStr(s)` — a state name, a param
        // name and a behaviour string field all reach the same CheckNoLineBreak, so a second site re-walks the
        // identical call.
        [Test] public void Funnel_LineBreakInParamName_Fails() =>
            AssertRefuses("lbparam", rc =>
                rc.AddParameter(new AnimatorControllerParameter { name = "Bad\nName", type = AnimatorControllerParameterType.Bool }),
                "line break");

        // ── Faithful_*: condition params that were once refused ──────────────────────────────────────
        // A condition parameter carrying spaces or a flow delimiter is FAITHFUL — no rename, no refusal: it
        // decompiles cleanly and the emitted condition reaches a textual fixpoint (the comma param emits as
        // ONE quoted scalar).
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
