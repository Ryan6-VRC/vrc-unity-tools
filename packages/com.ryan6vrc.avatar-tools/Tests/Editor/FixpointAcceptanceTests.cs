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
//   `_notes:` block. Those legitimately DIFFER across the round-trip — e.g. GrabProp_Fx has 5 unreachable
//   orphan sub-assets originally but 0 after a clean recompile — so the door's yaml is NOT a fixpoint by
//   construction. Walk(c).Doc carries the semantic intermediate with an EMPTY ReservedNotes (the incidental
//   data lives in the sibling WalkResult fields, never the Doc), so serializing the Doc directly yields the
//   canonical, notes-free intermediate that IS the attractor. Textual identity is the strongest, cheapest
//   witness — a single string compare proves decode∘serialize∘compile lost nothing.
//
// A FIXPOINT BREAK IS A REAL BUG in decode / serialize / compile, to be fixed at the true site — never
// worked around by weakening the assertion here. This round-trip is the compiler's lossless oracle.
//
// GoLoco (GoLocoBaseFullPoses, 597 states / 180 sub-machines / 787 trees) was a planned scale fixture but
// is DEFERRED, not covered here: it legitimately REFUSES decode on two constructs the schema does not model
// — ~126 cross-machine transitions targeting a TOP-LEVEL entity from a NESTED machine (a top-level entity's
// root-relative address is a single bare segment, which the compiler resolves as LOCAL, so it is
// unaddressable from nested scope), and 5 transitions carrying editor mute/solo flags. Both need a schema +
// compiler design decision (a root-anchor addressing syntax; whether to model transition mute/solo) and are
// out of this slice's scope. The `Refusal_*` test below pins the exact dominant blocking construct with a
// minimal reproduction so a future extension has a target to flip.
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
    // bare `[DecompileController] FAIL:` naming the construct, and writes NO .yaml. The construct here — a
    // nested state transitioning to a TOP-LEVEL sub-machine — is the exact dominant blocker on the deferred
    // GoLoco fixture, reproduced minimally.
    [Test]
    public void Refusal_CrossMachineTargetFromNested_Fails_And_Writes_No_Yaml()
    {
        string ctrlPath = TestRoot + "/CrossMachineRefusal_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        var root = rc.layers[0].stateMachine;
        var top = root.AddStateMachine("Top");        // top-level sub-machine (root path "Top", no slash)
        var inner = top.AddStateMachine("Inner");      // nested sub-machine ("Top/Inner")
        var go = inner.AddState("Go");                 // nested state
        rc.AddParameter("x", AnimatorControllerParameterType.Bool);
        var tr = go.AddTransition(top);                // nested → top-level: not addressable in the schema
        tr.AddCondition(AnimatorConditionMode.If, 0, "x");
        AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] FAIL:"));
        string yamlOut = TestRoot + "/refuse_xm.yaml";
        string res = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);

        StringAssert.Contains("FAIL", res);
        StringAssert.Contains("cross-machine", res, "the refusal names the offending construct");
        Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
    }
}
