using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

// ── ACCEPTANCE GATE: the animator READ/WRITE substrate is a lossless inverse pair ────────────────────
//
// THE FIXPOINT MECHANIC (the acceptance witness):
//   yaml1 = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(C0).Doc)   // C0 = a real controller
//   CompileController.Compile(yaml1) -> C1
//   yaml2 = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(C1).Doc)
//   ASSERT yaml1 == yaml2  (textual identity — the fixpoint)  AND  AnimatorLint(C1) => PASS
//
// WHY textual identity of Serialize(Walk(c).Doc), and NOT the DecompileController door's .yaml:
//   The door folds source-INCIDENTAL notes (orphan count, unresolved GUIDs, applied tolerances) into a
//   `_notes:` block. Those legitimately DIFFER across the round-trip (e.g. GrabProp_Fx has 5 unreachable
//   orphan sub-assets originally but 0 after a clean recompile), so the door's yaml is NOT a fixpoint by
//   construction. Walk(c).Doc carries the semantic intermediate with an EMPTY ReservedNotes (the incidental
//   data lives in the sibling WalkResult fields, never the Doc), so serializing the Doc directly yields the
//   canonical, notes-free intermediate that IS the attractor. Textual identity is the strongest, cheapest
//   witness — a single string compare proves decode∘serialize∘compile lost nothing.
//
// A FIXPOINT BREAK IS A REAL BUG in decode / serialize / compile, to be fixed at the true site — never
// worked around by weakening the assertion here. This round-trip is the compiler's lossless oracle.
//
// GoLoco (GoLocoBaseFullPoses, 597 states / 180 sub-machines / 787 trees) is the intended scale fixture and
// now round-trips EVERYTHING the substrate models — cross-machine/root-anchored addressing, mute/solo, 1D
// blend-tree thresholds, and a state literally named "FBT InStation/Action" (the '/' survives via per-segment
// escaping; see Roundtrip_SlashInName_LocalAndCrossMachine). It is NOT yet in the [TestCase] set for ONE
// remaining reason unrelated to the substrate's coverage: in the Plum-Remy project it references 4 clip
// assets that do not exist (genuinely broken refs, objectReferenceValue == null). By design a broken ref is
// decoded into the Doc as `unresolved: true` (Task 6, tested) yet compiles to a NULL motion slot (Task 4,
// tested) — so a recompile turns those 4 children empty and strict textual identity cannot hold. Every OTHER
// byte of GoLoco's ~843 KB intermediate is identical across the round-trip. Reconciling that (relax the
// GoLoco assertion to "modulo unresolved refs", import the 4 clips, or preserve broken refs on compile) is a
// coordinator decision; until then GoLoco stays out of the [TestCase] set.
//
// NOT run via MCP run_tests (it crashes the editor); run from the Test Runner window or batchmode CI. The
// two fixpoint fixtures live in the Plum-Remy project; in a project lacking them the case self-Ignores.
public class FixpointAcceptanceTests
{
    private const string TestRoot = "Assets/Agent/Scratch/fixpoint_tests";

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(TestRoot);
        AssetDatabase.Refresh(); // register TestRoot as a valid asset folder so compile can nest under it
    }

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
        AssetDatabase.Refresh();
    }

    // Decompile→Compile→Decompile reaches a textual fixpoint AND the recompile lints PASS.
    [TestCase("Assets/GestureTools/GrabProp/GrabProp_Fx.controller", "GrabProp_Fx")]
    [TestCase("Assets/GestureTools/ContactTracker/ContactTracker_Fx.controller", "ContactTracker_Fx")]
    public void Fixpoint(string fixturePath, string name)
    {
        var c0 = AssetDatabase.LoadAssetAtPath<AnimatorController>(fixturePath);
        if (c0 == null) Assert.Ignore("fixture not present in this project: " + fixturePath);

        // First decompile — the canonical, notes-free intermediate.
        string yaml1 = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(c0).Doc);
        string y1Path = TestRoot + "/" + name + "_1.yaml";
        File.WriteAllText(y1Path, yaml1);

        // Compile it back to a fresh controller (C1).
        string outDir = TestRoot + "/out_" + name;
        Directory.CreateDirectory(outDir);
        AssetDatabase.Refresh();
        string comp = CompileController.Compile(Path.GetFullPath(y1Path), outDir, whatIf: false);
        StringAssert.Contains("=> OK", comp, "the intermediate compiles cleanly");

        var c1 = AssetDatabase.LoadAssetAtPath<AnimatorController>(outDir + "/" + name + ".controller");
        Assert.IsNotNull(c1, "recompiled controller loads");

        // Second decompile — must be BYTE-FOR-BYTE identical to the first: the fixpoint.
        string yaml2 = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(c1).Doc);
        Assert.AreEqual(yaml1, yaml2, "fixpoint: the second decompiled intermediate must be textually identical to the first");

        // The recompiled controller is graph-clean.
        StringAssert.Contains("=> PASS", AnimatorLint.Lint(c1, "explicit", null, null, null));
    }

    // Named refusal (the acceptance's fail-loud arm): an out-of-vocabulary construct → the door returns a
    // bare `[DecompileController] FAIL:` naming the construct, and writes NO .yaml. A Trigger parameter has
    // no schema representation (the vocabulary is Bool/Int/Float) and is refused PERMANENTLY — unlike the
    // cross-machine construct, which the root-anchor addressing now makes valid.
    [Test]
    public void Refusal_TriggerParam_Fails_And_Writes_No_Yaml()
    {
        string ctrlPath = TestRoot + "/TriggerRefusal_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        rc.AddParameter("T", AnimatorControllerParameterType.Trigger); // out of vocabulary
        AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
        string yamlOut = TestRoot + "/refuse_trigger.yaml";
        string res = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);

        StringAssert.Contains("FAIL", res);
        StringAssert.Contains("Trigger", res, "the refusal names the offending construct");
        Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
    }

    // A state whose NAME contains the addressing path separator '/', referenced BOTH same-machine (local) and
    // cross-machine (a from-root path). Per-segment escaping ('/'->'\/') must let both references resolve after
    // escape → serialize → parse → compile → re-decode, and the round-trip must reach a textual fixpoint. This
    // is the durable witness for the name-escaping extension (the exact GoLoco "FBT InStation/Action" shape).
    [Test]
    public void Roundtrip_SlashInName_LocalAndCrossMachine()
    {
        string ctrlPath = TestRoot + "/SlashName_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        rc.AddParameter("g", AnimatorControllerParameterType.Bool);
        var root = rc.layers[0].stateMachine;
        var mA = root.AddStateMachine("A");
        var slash = mA.AddState("Foo/Bar");   // name literally contains the path separator
        var other = mA.AddState("Other");
        var mB = root.AddStateMachine("B");
        var bx = mB.AddState("BX");
        other.AddTransition(slash).AddCondition(AnimatorConditionMode.If, 0, "g"); // local same-machine ref
        bx.AddTransition(slash).AddCondition(AnimatorConditionMode.If, 0, "g");    // cross-machine ref (A/Foo\/Bar)
        AssetDatabase.SaveAssets();

        string yaml1 = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(rc).Doc);
        StringAssert.Contains("to: Foo\\/Bar", yaml1, "the local reference escapes the '/'");
        StringAssert.Contains("A/Foo\\/Bar", yaml1, "the cross-machine reference escapes the segment's '/'");

        string y1Path = TestRoot + "/slashname.yaml";
        File.WriteAllText(y1Path, yaml1);
        string outDir = TestRoot + "/out_slashname";
        Directory.CreateDirectory(outDir);
        AssetDatabase.Refresh();
        string comp = CompileController.Compile(Path.GetFullPath(y1Path), outDir, whatIf: false);
        StringAssert.Contains("=> OK", comp);

        var c1 = AssetDatabase.LoadAssetAtPath<AnimatorController>(outDir + "/SlashName_Fx.controller");
        Assert.IsNotNull(c1, "recompiled controller loads");

        string yaml2 = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(c1).Doc);
        Assert.AreEqual(yaml1, yaml2, "the slash-in-name controller reaches a textual fixpoint");
        StringAssert.Contains("=> PASS", AnimatorLint.Lint(c1, "explicit", null, null, null));
    }
}
