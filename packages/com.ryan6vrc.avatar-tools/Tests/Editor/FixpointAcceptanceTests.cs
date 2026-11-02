using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using Ryan6Vrc.AvatarTools.Tests;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

// ACCEPTANCE GATE: the animator read/write substrate is a lossless inverse pair. decode is FixpointOracle.Decode.
// Walk(c).Doc has empty ReservedNotes (orphan/unresolved/tolerance data sits in sibling WalkResult fields), so its
// serialization is the fixpoint attractor and one string compare proves decode∘serialize∘compile lost nothing.
// A fixpoint break is a real bug in decode, serialize or compile: fix it at the true site, never by weakening
// the assertion.
//
// Two forms:
//   Tight (clean fixtures, no broken refs): decode(C0) == decode(C1).
//   Stabilized (a raw vendor controller): C1 = compile(decode(C0)), then decode(C1) == decode(compile(decode(C1))).
//   Only the raw→first-compile step may canonicalize what the schema cannot represent.
//
// The stabilized form depends on GoLocoBaseFullPoses from the non-redistributable `gogoloco` package. In a project
// without it the case self-Ignores, the clean=false helpers never execute, and the form has no coverage; only the
// two in-package fixtures run. The Ignore is deliberate, since a clone without the package could not satisfy a hard
// failure. Keep the machinery.
public class FixpointAcceptanceTests
{
    private const string TestRoot = "Assets/Agent/Scratch/fixpoint_tests";

    [SetUp]
    public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

    // Deletion is per-test because the refusal test asserts its .yaml is absent, so the root has to start empty.
    // No AssetDatabase.Refresh() on either side: CreateFolder writes the .meta and DeleteAsset closes its own import.
    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
    }

    // Neither clean fixture is redundant. GrabProp alone covers exit-time transitions and default fixed duration
    // (an absent `fixedDuration` key). ContactTracker alone covers a blend tree, a nested sub-machine and Float
    // Greater/Less compares.
    [TestCase("Packages/com.ryan6vrc.avatar-tools/Tests/Editor/Fixtures/GestureTools/GrabProp/GrabProp_Fx.controller", "GrabProp_Fx", true, 0)]
    [TestCase("Packages/com.ryan6vrc.avatar-tools/Tests/Editor/Fixtures/GestureTools/ContactTracker/ContactTracker_Fx.controller", "ContactTracker_Fx", true, 0)]
    [TestCase("Packages/gogoloco/Runtime/GoGo/GoLoco/Controllers/Heavy_Controler/GoLocoBaseFullPoses.controller", "GoLocoBaseFullPoses", false, 4)]
    public void Fixpoint(string fixturePath, string name, bool clean, int expectedBrokenRefs)
    {
        var c0 = AssetDatabase.LoadAssetAtPath<AnimatorController>(fixturePath);
        if (c0 == null) Assert.Ignore("fixture not present in this project: " + fixturePath);

        // A raw vendor fixture may carry refusals the first compile normalizes (GoLoco's entry mute/solo, which never
        // reach the YAML); the owned forms below must decode refusal-free.
        string yamlA = FixpointOracle.Decode(c0, requireClean: clean);
        var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, name, "c1");
        string yamlB = FixpointOracle.Decode(c1);

        var c2 = FixpointOracle.CompileTo(TestRoot, yamlB, name, "c2");
        string yamlC = FixpointOracle.Decode(c2);
        Assert.AreEqual(yamlB, yamlC, "stabilized fixpoint: the owned form's decompile is textually identical after a recompile");
        StringAssert.Contains("=> PASS", CheckAnimator.Run(c1, "explicit", null, null, null));

        if (clean)
            Assert.AreEqual(yamlA, yamlB, "clean fixture: the second decompile is identical to the first (tight fixpoint)");
        else
            AssertRawToOwnedIsOnlyDocumentedNormalization(yamlA, yamlB, expectedBrokenRefs);

        // The compares above run through one oracle, so a property the schema does not model drops identically on both
        // sides and still passes. The raw→owned step is where information is lost, so the independent digest has to
        // agree there; on the recompile step both sides already agree on any wrong value.
        if (clean)
            ControllerGraphDigest.AssertSameGraph(c0, c1, "raw -> owned");
        else
            // Broken motion refs legitimately become null slots across this step, so whole-digest equality would fail;
            // only the default-state lines have to match.
            Assert.AreEqual(DefaultStateLines(ControllerGraphDigest.Of(c0)), DefaultStateLines(ControllerGraphDigest.Of(c1)),
                "raw -> owned must not change which state any machine boots");
    }

    // The "(default)" rows of a ReportController digest: the independent reading of every machine's boot state.
    private static string DefaultStateLines(string digest)
    {
        var keep = new List<string>();
        foreach (var line in digest.Split('\n'))
            if (line.EndsWith("(default)")) keep.Add(line.Trim());
        return string.Join("\n", keep);
    }

    // The raw→owned step may change only two things: a `default:` added where Unity's defaultState getter resolved
    // through a machine with no direct states (not an authored field), and a broken vendor motion ref, decoded
    // `unresolved:true` and compiled to an empty slot.
    private static void AssertRawToOwnedIsOnlyDocumentedNormalization(string yamlA, string yamlB, int expectedBrokenRefs)
    {
        var da = DefaultCounts(yamlA);
        var db = DefaultCounts(yamlB);
        foreach (var kv in da)
        {
            db.TryGetValue(kv.Key, out int bc);
            Assert.GreaterOrEqual(bc, kv.Value, "an authored default was lost or changed by the first compile: '" + kv.Key + "'");
        }

        var a = StripDefaultLines(yamlA);
        var b = StripDefaultLines(yamlB);
        Assert.AreEqual(a.Count, b.Count, "after removing default: lines the line counts match (defaults are the only insertions)");
        int diffs = 0, brokenRefDiffs = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] == b[i]) continue;
            diffs++;
            if (a[i].Contains("unresolved") && !b[i].Contains("unresolved")) brokenRefDiffs++;
        }
        Assert.AreEqual(brokenRefDiffs, diffs, "every non-default diff is an unresolved→empty broken-ref slot (no unexplained drift)");
        Assert.AreEqual(expectedBrokenRefs, diffs, "exactly the known count of broken vendor refs differ");
        Assert.IsFalse(yamlB.Contains("unresolved"), "the owned form carries no unresolved refs (they collapsed on the first compile)");
    }

    private static List<string> StripDefaultLines(string yaml)
    {
        var outl = new List<string>();
        foreach (var ln in yaml.Split('\n'))
            if (!Regex.IsMatch(ln, @"^\s*default:")) outl.Add(ln);
        return outl;
    }

    private static Dictionary<string, int> DefaultCounts(string yaml)
    {
        var d = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(yaml, @"(?m)^\s*default: (.*)$"))
        {
            string k = m.Groups[1].Value.Trim();
            d[k] = d.TryGetValue(k, out int c) ? c + 1 : 1;
        }
        return d;
    }

    // A Trigger parameter has no schema representation (the vocabulary is Bool/Int/Float) and is refused permanently.
    [Test]
    public void Refusal_TriggerParam_Fails_And_Writes_No_Yaml()
    {
        string ctrlPath = TestRoot + "/TriggerRefusal_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        rc.AddParameter("T", AnimatorControllerParameterType.Trigger);
        AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] .*=> FAIL"));
        string yamlOut = TestRoot + "/refuse_trigger.yaml";
        string res = DecompileController.Run(ctrlPath, yamlOut, whatIf: false);

        StringAssert.Contains("FAIL", res);
        StringAssert.Contains("Trigger", res, "the refusal names the offending construct");
        Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
        AnimatorTestHelpers.DeleteRefusalArtifact(res);
    }

    [Test]
    public void Roundtrip_SlashInName_LocalAndCrossMachine()
    {
        string ctrlPath = TestRoot + "/SlashName_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        rc.AddParameter("g", AnimatorControllerParameterType.Bool);
        var root = rc.layers[0].stateMachine;
        var mA = root.AddStateMachine("A");
        var slash = mA.AddState("Foo/Bar");
        var other = mA.AddState("Other");
        var mB = root.AddStateMachine("B");
        var bx = mB.AddState("BX");
        other.AddTransition(slash).AddCondition(AnimatorConditionMode.If, 0, "g");
        bx.AddTransition(slash).AddCondition(AnimatorConditionMode.If, 0, "g");
        AssetDatabase.SaveAssets();

        string yaml1 = FixpointOracle.Decode(rc);
        StringAssert.Contains("to: Foo\\/Bar", yaml1, "the local reference escapes the '/'");
        StringAssert.Contains("A/Foo\\/Bar", yaml1, "the cross-machine reference escapes the segment's '/'");

        var c1 = FixpointOracle.CompileTo(TestRoot, yaml1, "SlashName_Fx", "c1");
        string yaml2 = FixpointOracle.Decode(c1);
        Assert.AreEqual(yaml1, yaml2, "the slash-in-name controller reaches a textual fixpoint");
        StringAssert.Contains("=> PASS", CheckAnimator.Run(c1, "explicit", null, null, null));
    }

    // Tangent coverage is two cases, one per ClassifyTangents branch (Linear and Stepped), each constant-value on
    // purpose: only a constant curve can tempt the decoder into the `set:` short form, and `set:` carries no tangent
    // marker, so that downgrade drops `tangents:` and breaks the fixpoint. Do not add a non-constant or longer-curve
    // variant here; the emit-side tangent modes are ControllerEmitTests' Curve_Tangents_* pair.
    [Test]
    public void Roundtrip_ConstantValueLinearCurve_StaysMapForm_NotSet()
    {
        const string yaml = @"schema: 1
controller: ConstLinear_Fx
basis: avatar-root
clips:
  c:
    curves:
      Prop/Renderer.enabled:
        tangents: linear
        keys: [[0, 0], [1, 0]]
layers:
  - name: L
    states:
      S:
        motion: { clip: c }
    default: S
";
        var c1 = FixpointOracle.CompileTo(TestRoot, yaml, "ConstLinear_Fx", "c1");
        string yamlB = FixpointOracle.Decode(c1);
        StringAssert.Contains("Prop/Renderer.enabled: { tangents: linear, keys: [ [0, 0], [1, 0] ] }", yamlB,
            "a constant-value linear curve keeps the map form; it must not downgrade to set: and lose the marker");
        StringAssert.DoesNotContain("set:", yamlB,
            "the constant linear curve must NOT collapse to a set: write (that drops tangents: linear)");

        var c2 = FixpointOracle.CompileTo(TestRoot, yamlB, "ConstLinear_Fx", "c2");
        Assert.AreEqual(yamlB, FixpointOracle.Decode(c2), "tight fixpoint on the constant linear curve");
        StringAssert.Contains("=> PASS", CheckAnimator.Run(c1, "explicit", null, null, null));
    }

    [Test]
    public void Roundtrip_ConstantValueSteppedCurve_StaysMapForm_NotSet()
    {
        const string yaml = @"schema: 1
controller: ConstStepped_Fx
basis: avatar-root
clips:
  c:
    curves:
      Prop/Renderer.enabled: { tangents: stepped, keys: [ [0, 1], [0.5, 1] ] }
layers:
  - name: L
    states:
      S:
        motion: { clip: c }
    default: S
";
        var c1 = FixpointOracle.CompileTo(TestRoot, yaml, "ConstStepped_Fx", "c1");
        string yamlB = FixpointOracle.Decode(c1);
        StringAssert.Contains("tangents: stepped", yamlB);
        StringAssert.DoesNotContain("set:", yamlB, "constant-value stepped must NOT collapse to set: (drops the marker)");
        Assert.AreEqual(yamlB, FixpointOracle.Decode(FixpointOracle.CompileTo(TestRoot, yamlB, "ConstStepped_Fx", "c2")));
    }
}
