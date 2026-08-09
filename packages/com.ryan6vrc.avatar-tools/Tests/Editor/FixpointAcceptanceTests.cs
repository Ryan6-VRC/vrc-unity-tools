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
//     represent.
//
// THE STABILIZED ARM IS CONDITIONAL ON ITS FIXTURE, AND THE FIXTURE IS EXTERNAL. GoLocoBaseFullPoses ships
// with the `gogoloco` package, which is NOT in this workspace's vpm-manifest.json; when it is absent the
// GoLoco [TestCase] hits `Assert.Ignore`, and `AssertRawToOwnedIsOnlyDocumentedNormalization`,
// `StripDefaultLines` and `DefaultCounts` never execute. So: in a project WITHOUT the package, the
// stabilized (`clean=false`) path — and with it every claim below about raw→owned normalization — has NO
// coverage at all; only the two tight-fixpoint fixtures run. The Ignore is deliberate, not a gap to close
// with a hard failure: the vendor fixture is not license-clean to commit, so a third-party clone would fail
// a test it cannot satisfy. Keep the machinery; do not delete it to match the fixture's absence.
//
// WHEN the GoLoco fixture DOES resolve, the raw→owned step is proven to normalize exactly two benign,
// documented categories on that controller (597 states / 180 sub-machines / 787 trees) and nothing the
// substrate models:
//   (1) 4 genuinely-broken VENDOR motion refs (missing clip assets in that GoGo copy) — decoded
//       `unresolved:true` then compiled to a null motion slot: the acknowledged unresolved-ref degradation, and
//   (2) 1 Unity resolve-through defaultState (the getter resolves through a state-less machine to a nested
//       default — not an authored field; see ControllerDecompile) canonicalized to an explicit representable
//       default.
// Every other byte is identical and NO authored default changes — the diff-category assertions below are what
// prove it, for that fixture, on a run where it was present. Separately, the RAW C0 decode carries 3
// entry-transition mute/solo refusals (editor debug residue the entry ladder can't express); they never reach
// the YAML (entry transitions don't emit mute/solo), so they are NOT a raw→owned byte diff — they are why the
// raw vendor decode is not refusal-free (the door refuses it) while the OWNED form is.
//
// Run headless via tools/run-editmode-tests.ps1 (or the Test Runner window / batchmode CI); not via MCP
// run_tests — wrong venue (live editor). See docs/verify.md. The GrabProp/ContactTracker fixtures live
// in-package under Tests/Editor/Fixtures (committed, controller + Animations with GUID-preserving .metas),
// so those two cases always run here.
public class FixpointAcceptanceTests
{
    private const string TestRoot = "Assets/Agent/Scratch/fixpoint_tests";

    [SetUp]
    public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

    // Per-test deletion is load-bearing, not hygiene: the refusal case below asserts a refusal writes NO
    // .yaml, so the root has to start empty. No AssetDatabase.Refresh() on either side — CreateFolder
    // registers the folder AND writes its .meta, and DeleteAsset closes its own import.
    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
    }

    // clean=true  → also assert the TIGHT fixpoint decode(C0)==decode(C1).
    // clean=false → assert the raw→owned diff is ONLY the documented normalization: after removing `default:`
    //               lines (resolve-through canonicalization), the sole remaining diffs are exactly
    //               expectedBrokenRefs `unresolved`→empty child slots, and no authored default is lost.
    //
    // BOTH clean fixtures are load-bearing — neither construct census contains the other, so neither can be
    // dropped as redundant. GrabProp is the sole witness for exit-time transitions (7× m_HasExitTime: 1;
    // ContactTracker has none) and for default fixed duration (16× m_HasFixedDuration: 1, which decodes to an
    // ABSENT `fixedDuration` key). ContactTracker is the sole witness for a blend tree, a nested sub-machine,
    // Float Greater/Less compares, and m_HasFixedDuration: 0 throughout — the other arm of the same ternary in
    // ControllerDecompile.DecodeStateTransition. Re-run the census (grep m_HasExitTime / m_HasFixedDuration /
    // m_BlendType / m_ConditionMode over both .controller files) before proposing to drop either.
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

        // Everything above compares decode(X) to decode(Y) through ONE oracle, so a property the schema does not
        // model is dropped identically on both sides and still passes. That is not a gap in the assertions, it is
        // structural — and it is how a cross-machine defaultState reached a perfect textual fixpoint while the
        // two controllers booted different states. The raw→owned step is where information is actually lost, so
        // that is where a SECOND, independent reading has to agree. Asserting it on the recompile step instead
        // would buy nothing: by then both sides already agree on the wrong value.
        if (clean)
            ControllerGraphDigest.AssertSameGraph(c0, c1, "raw -> owned");
        else
            // A raw vendor fixture legitimately changes across this step (broken motion refs become null slots),
            // so whole-digest equality would fail for a documented reason. Narrow it to the default-state lines:
            // the class this gate exists for, and one with no remaining legitimate normalization now that a
            // resolve-through default folds to the same bare `default:` on both sides.
            Assert.AreEqual(DefaultStateLines(ControllerGraphDigest.Of(c0)), DefaultStateLines(ControllerGraphDigest.Of(c1)),
                "raw -> owned must not change which state any machine boots");
    }

    // The "(default)" rows of a ReportController digest, in order — the independent reading of every machine's
    // boot state.
    private static string DefaultStateLines(string digest)
    {
        var keep = new List<string>();
        foreach (var line in digest.Split('\n'))
            if (line.EndsWith("(default)")) keep.Add(line.Trim());
        return string.Join("\n", keep);
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

    // TANGENT COVERAGE IS TWO CASES, ONE PER ClassifyTangents BRANCH (Linear and Stepped/Constant), and each is
    // the CONSTANT-VALUE variant on purpose: a curve whose keys all hold the same value is `IsConstant`, which
    // is the only input that can tempt the decoder into the `set:` short form — and `set:` carries no tangent
    // marker, so that downgrade silently drops `tangents:` and breaks the fixpoint. The constant case therefore
    // subsumes the non-constant one (it asserts the map form AND `DoesNotContain("set:")`), and key COUNT is
    // irrelevant: ClassifyTangents/AllKeysMode loop every key uniformly, so a 3-key pulse walks the same branch
    // as a 2-key one. Do not add a non-constant or longer-curve variant here; the emit-side tangent modes are
    // ControllerEmitTests' Curve_Tangents_* pair.
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
        StringAssert.Contains("=> PASS", CheckAnimator.Lint(c1, "explicit", null, null, null));
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
