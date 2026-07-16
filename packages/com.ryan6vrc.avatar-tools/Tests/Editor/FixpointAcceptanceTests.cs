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

// ── ACCEPTANCE GATE: the animator READ/WRITE substrate is a lossless inverse pair ────────────────────
//
// THE FIXPOINT MECHANIC (the acceptance witness). Let decode(c) = AnimatorSchemaEmit.Serialize(
// ControllerDecompile.Walk(c).Doc) — the canonical, notes-free semantic intermediate (Walk(c).Doc has an
// EMPTY ReservedNotes; incidental orphan/unresolved/tolerance data lives in the sibling WalkResult fields,
// so serializing the Doc directly yields the fixpoint attractor). Textual identity of two such intermediates
// is the strongest, cheapest witness — one string compare proves decode∘serialize∘compile lost nothing.
// A FIXPOINT BREAK IS A REAL BUG in decode / serialize / compile, fixed at the true site — never worked
// around by weakening the assertion. This round-trip is the compiler's lossless oracle.
//
// TWO FORMS, one shared helper:
//   • TIGHT (clean fixtures — no broken refs): decode(C0) == decode(C1), the spec's literal "second
//     decompile identical to the first." GrabProp/ContactTracker satisfy this AND the stabilized form.
//   • STABILIZED (GoLoco): own the controller ONCE — C1 = compile(decode(C0)) — then assert the OWNED form
//     is an exact fixpoint: decode(C1) == decode(compile(decode(C1))), byte-for-byte, with Lint PASS on C1.
//     This is the stronger, more meaningful theorem for a raw VENDOR controller: the substrate round-trips
//     the owned form perfectly; only the raw→first-compile step canonicalizes constructs the schema cannot
//     represent. For GoLocoBaseFullPoses (597 states / 180 sub-machines / 787 trees) that raw→owned step is
//     PROVEN here to normalize EXACTLY two benign, documented categories and nothing the substrate models:
//       (1) 4 genuinely-broken VENDOR motion refs (missing clip assets in this GoGo copy) — decoded
//           `unresolved:true` then compiled to a null motion slot: the acknowledged
//           unresolved-ref degradation, and
//       (2) 1 Unity resolve-through defaultState (the getter resolves through a state-less machine to a
//           nested default — not an authored field; see ControllerDecompile) canonicalized to an explicit
//           representable default.
//     Every other byte is identical and NO authored default changes — the diff-category assertions below
//     prove it. Separately, the RAW C0 decode carries 3 entry-transition mute/solo refusals (editor debug
//     residue the entry ladder can't express); they never reach the YAML (entry transitions don't emit
//     mute/solo), so they are NOT a raw→owned byte diff — they are why the raw vendor decode is not
//     refusal-free (the door refuses it), while the OWNED form is. GoLoco is in the passing [TestCase] set
//     via the stabilized form.
//
// Run headless via tools/run-editmode-tests.ps1 (or the Test Runner window / batchmode CI); not via MCP
// run_tests — wrong venue (live editor). See docs/verify.md. The GrabProp/ContactTracker fixtures live
// in-package under Tests/Editor/Fixtures (committed, controller + Animations with GUID-preserving .metas),
// so those cases always run here; the GoLoco vendor fixture is external (not license-clean to commit) and
// its case self-Ignores in a project lacking it.
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

    // clean=true  → also assert the TIGHT fixpoint decode(C0)==decode(C1).
    // clean=false → assert the raw→owned diff is ONLY the documented normalization: after removing `default:`
    //               lines (resolve-through canonicalization), the sole remaining diffs are exactly
    //               expectedBrokenRefs `unresolved`→empty child slots, and no authored default is lost.
    [TestCase("Packages/com.ryan6vrc.avatar-tools/Tests/Editor/Fixtures/GestureTools/GrabProp/GrabProp_Fx.controller", "GrabProp_Fx", true, 0)]
    [TestCase("Packages/com.ryan6vrc.avatar-tools/Tests/Editor/Fixtures/GestureTools/ContactTracker/ContactTracker_Fx.controller", "ContactTracker_Fx", true, 0)]
    [TestCase("Packages/gogoloco/Runtime/GoGo/GoLoco/Controllers/Heavy_Controler/GoLocoBaseFullPoses.controller", "GoLocoBaseFullPoses", false, 4)]
    public void Fixpoint(string fixturePath, string name, bool clean, int expectedBrokenRefs)
    {
        var c0 = AssetDatabase.LoadAssetAtPath<AnimatorController>(fixturePath);
        if (c0 == null) Assert.Ignore("fixture not present in this project: " + fixturePath);

        // Raw C0: a clean fixture must decode refusal-free; a raw vendor (stabilized) fixture may carry
        // refusals the first compile normalizes (GoLoco's 3 entry mute/solo). The OWNED forms below always must.
        string yamlA = FixpointOracle.Decode(c0, requireClean: clean); // raw vendor decompile
        var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, name, "c1"); // own it once
        string yamlB = FixpointOracle.Decode(c1);           // the owned form

        // STABILIZED fixpoint: the owned form round-trips byte-for-byte.
        var c2 = FixpointOracle.CompileTo(TestRoot, yamlB, name, "c2");
        string yamlC = FixpointOracle.Decode(c2);
        Assert.AreEqual(yamlB, yamlC, "stabilized fixpoint: the owned form's decompile is textually identical after a recompile");
        StringAssert.Contains("=> PASS", CheckAnimator.Lint(c1, "explicit", null, null, null));

        if (clean)
            // No broken refs ⇒ the RAW vendor already round-trips: the spec's literal tight fixpoint.
            Assert.AreEqual(yamlA, yamlB, "clean fixture: the second decompile is identical to the first (tight fixpoint)");
        else
            AssertRawToOwnedIsOnlyDocumentedNormalization(yamlA, yamlB, expectedBrokenRefs);
    }

    // Prove the raw→owned step changed ONLY (a) resolve-through defaults and (b) genuinely-broken refs.
    private static void AssertRawToOwnedIsOnlyDocumentedNormalization(string yamlA, string yamlB, int expectedBrokenRefs)
    {
        // (a) Every authored (representable) default in the raw form survives unchanged in the owned form —
        // the owned form may add canonicalized resolve-through defaults, but must never drop or alter one.
        var da = DefaultCounts(yamlA);
        var db = DefaultCounts(yamlB);
        foreach (var kv in da)
        {
            db.TryGetValue(kv.Key, out int bc);
            Assert.GreaterOrEqual(bc, kv.Value, "an authored default was lost or changed by the first compile: '" + kv.Key + "'");
        }

        // (b) With `default:` lines removed from both, the remaining content is byte-identical EXCEPT the
        // broken-ref child slots (an `unresolved:true` child in the raw form → an empty child in the owned).
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

    // Named refusal (the acceptance's fail-loud arm): an out-of-vocabulary construct → the door returns
    // `[DecompileController] … => FAIL | log=` naming the construct, and writes NO .yaml. A Trigger parameter has
    // no schema representation (the vocabulary is Bool/Int/Float) and is refused PERMANENTLY.
    [Test]
    public void Refusal_TriggerParam_Fails_And_Writes_No_Yaml()
    {
        string ctrlPath = TestRoot + "/TriggerRefusal_Fx.controller";
        var rc = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        rc.AddParameter("T", AnimatorControllerParameterType.Trigger); // out of vocabulary
        AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex(@"\[DecompileController\] .*=> FAIL"));
        string yamlOut = TestRoot + "/refuse_trigger.yaml";
        string res = DecompileController.Decompile(ctrlPath, yamlOut, whatIf: false);

        StringAssert.Contains("FAIL", res);
        StringAssert.Contains("Trigger", res, "the refusal names the offending construct");
        Assert.IsFalse(File.Exists(yamlOut), "a refusal writes no .yaml");
        AnimatorTestHelpers.DeleteRefusalArtifact(res);
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

        string yaml1 = FixpointOracle.Decode(rc);
        StringAssert.Contains("to: Foo\\/Bar", yaml1, "the local reference escapes the '/'");
        StringAssert.Contains("A/Foo\\/Bar", yaml1, "the cross-machine reference escapes the segment's '/'");

        var c1 = FixpointOracle.CompileTo(TestRoot, yaml1, "SlashName_Fx", "c1");
        string yaml2 = FixpointOracle.Decode(c1);
        Assert.AreEqual(yaml1, yaml2, "the slash-in-name controller reaches a textual fixpoint");
        StringAssert.Contains("=> PASS", CheckAnimator.Lint(c1, "explicit", null, null, null));
    }

    // A `tangents: linear` curve must decompile back to the SAME map form (never silently downgrade to the
    // bare-list form flat curves use), and that owned form must itself reach a byte-identical fixpoint on a
    // second compile→decompile — the substrate doesn't just parse the marker once, it OWNS it durably.
    [Test]
    public void Roundtrip_LinearTangentCurve_SerializesAsMapForm_And_ReachesFixpoint()
    {
        const string yaml = @"schema: 1
controller: LinearTangent_Fx
basis: avatar-root
clips:
  c:
    curves:
      Prop/Renderer.enabled:
        tangents: linear
        keys: [[0, 0], [1, 1]]
layers:
  - name: L
    states:
      S:
        motion: { clip: c }
    default: S
";
        var c1 = FixpointOracle.CompileTo(TestRoot, yaml, "LinearTangent_Fx", "c1");
        string yamlB = FixpointOracle.Decode(c1);
        StringAssert.Contains("Prop/Renderer.enabled: { tangents: linear, keys: [ [0, 0], [1, 1] ] }", yamlB,
            "the linear-tangent curve decompiles back to the map form, not the bare-list form flat curves use");

        var c2 = FixpointOracle.CompileTo(TestRoot, yamlB, "LinearTangent_Fx", "c2");
        string yamlC = FixpointOracle.Decode(c2);
        Assert.AreEqual(yamlB, yamlC, "tight fixpoint: the owned linear-tangent form round-trips byte-for-byte");
        StringAssert.Contains("=> PASS", CheckAnimator.Lint(c1, "explicit", null, null, null));
    }

    // Regression: a CONSTANT-value linear curve (both keys hold the same value) is `IsConstant` but must NOT
    // collapse to `set:` — `set:` has no tangent marker, so downgrading it silently drops `tangents: linear`
    // and breaks the fixpoint. It has to stay the map form like any other linear curve.
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
    }

    [Test]
    public void Roundtrip_SteppedPulse_StaysMapForm()
    {
        const string yaml = @"schema: 1
controller: SteppedPulse_Fx
basis: avatar-root
clips:
  c:
    curves:
      Prop/Renderer.enabled: { tangents: stepped, keys: [ [0, 0], [0.25, 1], [0.5, 0] ] }
layers:
  - name: L
    states:
      S:
        motion: { clip: c }
    default: S
";
        var c1 = FixpointOracle.CompileTo(TestRoot, yaml, "SteppedPulse_Fx", "c1");
        string yamlB = FixpointOracle.Decode(c1);
        StringAssert.Contains("tangents: stepped", yamlB);
        var c2 = FixpointOracle.CompileTo(TestRoot, yamlB, "SteppedPulse_Fx", "c2");
        Assert.AreEqual(yamlB, FixpointOracle.Decode(c2), "tight fixpoint on the stepped curve");
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
