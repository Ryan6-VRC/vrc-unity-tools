using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using Ryan6Vrc.AvatarTools.Tests;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Unity's m_DefaultState is a plain AnimatorState PPtr with NO subtree constraint, so a machine can boot a
// state nested inside a descendant machine ("Set as Layer Default State" on a nested node does exactly this).
// The schema addresses that with `defaultState:` — separate from `default:` because the two facts CO-EXIST in
// Unity rather than being two spellings of one, which these tests pin.
//
// The shape is not hypothetical: it ships in a real vendor FX (two layers of a DAPPI FacePreset controller),
// where losing it rebuilt the layer booting an empty `Disable` state instead of the intended face. It reached a
// perfect TEXTUAL fixpoint while doing so, which is why several of these assert on the built graph rather than
// on the decoded YAML.
//
// Controllers are seeded asset-backed (CreateAnimatorControllerAtPath + TearDown DeleteAsset), matching the
// fixpoint suites — sub-machines and their entry transitions are sub-assets. Seeding uses the C# property
// setters only; per docs/verify.md an EditMode SerializedObject WRITE on an object later destroyed corrupts
// Unity's object registry, and `sm.defaultState = s` is not one.
public class CrossMachineDefaultTests
{
    private const string TestRoot = "Assets/Agent/Scratch/xmdefault";

    [SetUp]
    public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
    }

    private static AnimatorController New(string name)
        => AnimatorController.CreateAnimatorControllerAtPath(TestRoot + "/" + name + ".controller");

    private static void Persist(AnimatorController c)
    {
        EditorUtility.SetDirty(c);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(AssetDatabase.GetAssetPath(c));
    }

    // The corpus shape: a direct state, sub-machines, ALL-CONDITIONAL entry rungs, and a default pointing at a
    // state inside one of the sub-machines.
    private static AnimatorController SeedCorpusShape(string name, out AnimatorState neutral)
    {
        var c = New(name);
        c.AddParameter("FacePreset", AnimatorControllerParameterType.Int);
        var root = c.layers[0].stateMachine;
        root.AddState("Disable");                       // added first ⇒ Unity auto-defaults here
        var preset0 = root.AddStateMachine("Preset0");
        neutral = preset0.AddState("Neutral");
        preset0.AddState("Fist");
        var other = root.AddStateMachine("Preset1");
        other.AddState("Neutral1");
        var rung = root.AddEntryTransition(preset0);
        rung.AddCondition(AnimatorConditionMode.Equals, 0, "FacePreset");
        var rung2 = root.AddEntryTransition(other);
        rung2.AddCondition(AnimatorConditionMode.Equals, 1, "FacePreset");
        root.defaultState = neutral;                    // the foreign default
        Persist(c);
        return c;
    }

    private static string Roundtrip(AnimatorController c, string tag, out AnimatorController rebuilt)
    {
        string yaml = FixpointOracle.Decode(c);
        rebuilt = FixpointOracle.CompileTo(TestRoot, yaml, c.name, tag);
        return yaml;
    }

    // ---- the rule table ---------------------------------------------------------------------------

    // Row 2: foreign default, no foldable rung -> a root-relative `defaultState:` path.
    [Test]
    public void Foreign_Default_Decodes_As_A_Root_Relative_Path()
    {
        var c = SeedCorpusShape("Corpus", out _);
        string yaml = FixpointOracle.Decode(c);
        StringAssert.Contains("defaultState: Preset0/Neutral", yaml,
            "a default inside a sub-machine addresses root-relatively");
        Assert.IsFalse(yaml.Contains("default: Preset0"),
            "the foreign default must NOT be folded into a bare sub-machine default — that is the silent loss");
    }

    // The whole point: the rebuilt controller boots the SAME state. Text equality alone passed this bug.
    [Test]
    public void Foreign_Default_Survives_The_Round_Trip_As_An_Object()
    {
        var c = SeedCorpusShape("Corpus2", out var neutral);
        Roundtrip(c, "c1", out var rebuilt);

        var rroot = rebuilt.layers[0].stateMachine;
        Assert.IsNotNull(rroot.defaultState, "the rebuilt layer has a default state");
        Assert.AreEqual(neutral.name, rroot.defaultState.name,
            "the rebuilt layer must boot the same state; booting the first direct state is the corpus defect");
        Assert.IsFalse(rroot.states.Any(s => s.state == rroot.defaultState),
            "and it is still a FOREIGN default, not a direct state");
    }

    [Test]
    public void Foreign_Default_Reaches_A_Textual_Fixpoint_Too()
    {
        var c = SeedCorpusShape("Corpus3", out _);
        string y1 = Roundtrip(c, "c1", out var c1);
        string y2 = FixpointOracle.Decode(c1);
        Assert.AreEqual(y1, y2, "the new key round-trips textually as well as structurally");
    }

    // The independent oracle sees it even though nothing here mentions defaultState.
    [Test]
    public void Graph_Digest_Catches_A_Boot_State_Change()
    {
        var c = SeedCorpusShape("Corpus4", out _);
        Roundtrip(c, "c1", out var rebuilt);
        ControllerGraphDigest.AssertSameGraph(c, rebuilt, "raw -> owned");
    }

    // The intersection: a foreign default AND a trailing unconditional rung co-exist in Unity. Folding the rung
    // while dropping the PPtr is the same silent loss one entry rung away from the shape above.
    [Test]
    public void Foreign_Default_And_Trailing_Unconditional_Rung_Both_Survive()
    {
        var c = New("Intersection");
        var root = c.layers[0].stateMachine;
        root.AddState("Disable");
        var sub = root.AddStateMachine("Preset0");
        var neutral = sub.AddState("Neutral");
        sub.AddState("Other");
        root.AddEntryTransition(sub);        // UNCONDITIONAL, to a direct child
        root.defaultState = neutral;         // and a foreign default
        Persist(c);

        string yaml = FixpointOracle.Decode(c);
        StringAssert.Contains("defaultState: Preset0/Neutral", yaml, "the PPtr survives");
        StringAssert.Contains("entry:", yaml, "and the rung stays an ordinary ladder rung rather than folding");

        var rebuilt = FixpointOracle.CompileTo(TestRoot, yaml, "Intersection", "c1");
        var rroot = rebuilt.layers[0].stateMachine;
        Assert.AreEqual("Neutral", rroot.defaultState.name, "the rebuilt layer boots Neutral, not Disable");
        Assert.AreEqual(1, rroot.entryTransitions.Length, "and the entry rung is still there");
    }

    // A default in a branch OTHER than the rung's target — the rung must not be mistaken for the default.
    [Test]
    public void Foreign_Default_In_A_Different_Branch_Than_The_Rung()
    {
        var c = New("OtherBranch");
        var root = c.layers[0].stateMachine;
        root.AddState("Disable");
        var a = root.AddStateMachine("A");
        a.AddState("AState");
        var b = root.AddStateMachine("B");
        var bstate = b.AddState("BState");
        root.AddEntryTransition(a);          // unconditional rung -> A
        root.defaultState = bstate;          // but the default lives in B
        Persist(c);

        string yaml = FixpointOracle.Decode(c);
        StringAssert.Contains("defaultState: B/BState", yaml);
        var rebuilt = FixpointOracle.CompileTo(TestRoot, yaml, "OtherBranch", "c1");
        Assert.AreEqual("BState", rebuilt.layers[0].stateMachine.defaultState.name);
    }

    // Row 3 must NOT drift into row 2. Unity AUTO-POPULATES an empty ancestor m_DefaultState from a descendant,
    // so the ordinary `default: Sub` shape also presents a "foreign" PPtr — and emitting a path for it would
    // churn every existing document while losing the legible bare form.
    [Test]
    public void Auto_Populated_Default_Still_Decodes_As_A_Bare_SubMachine_Default()
    {
        var c = New("AutoPop");
        var root = c.layers[0].stateMachine;   // NO direct states
        var sub = root.AddStateMachine("Sub");
        sub.AddState("N");
        root.AddEntryTransition(sub);
        Persist(c);

        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GetAssetPath(c));
        var rroot = reloaded.layers[0].stateMachine;
        Assert.IsNotNull(rroot.defaultState, "precondition: Unity auto-populated the ancestor's default");

        string yaml = FixpointOracle.Decode(reloaded);
        StringAssert.Contains("default: Sub", yaml, "the bare sub-machine default is preserved");
        Assert.IsFalse(yaml.Contains("defaultState:"),
            "an auto-populated default is re-derived by the rebuild for free — emitting a path would churn "
            + "every existing document for nothing");
    }

    // Depth, and root-relativity. A path computed relative to the EMITTING machine is identical to the correct
    // one for a layer root, so only a nested machine can tell the two implementations apart.
    [Test]
    public void Nested_Machine_Default_Addresses_From_The_Layer_Root()
    {
        var c = New("Deep");
        var root = c.layers[0].stateMachine;
        root.AddState("Top");
        var a = root.AddStateMachine("A");
        a.AddState("AState");
        var bm = a.AddStateMachine("B");
        var deep = bm.AddState("Deep");
        a.defaultState = deep;               // a NESTED machine with a foreign default
        Persist(c);

        string yaml = FixpointOracle.Decode(c);
        StringAssert.Contains("defaultState: A/B/Deep", yaml,
            "root-relative, not relative to the machine that carries the key");

        var rebuilt = FixpointOracle.CompileTo(TestRoot, yaml, "Deep", "c1");
        var ra = rebuilt.layers[0].stateMachine.stateMachines.First(s => s.stateMachine.name == "A").stateMachine;
        Assert.AreEqual("Deep", ra.defaultState.name);
    }

    // A '/' in a state name is escaped per segment in an addressing context.
    [Test]
    public void Slash_In_A_Name_Survives_A_DefaultState_Path()
    {
        var c = New("Slashy");
        var root = c.layers[0].stateMachine;
        root.AddState("Plain");
        var sub = root.AddStateMachine("A/B");
        var st = sub.AddState("C/D");
        root.defaultState = st;
        Persist(c);

        string yaml = FixpointOracle.Decode(c);
        StringAssert.Contains(@"A\/B/C\/D", yaml, "each segment is escaped; the separator is the unescaped '/'");
        var rebuilt = FixpointOracle.CompileTo(TestRoot, yaml, "Slashy", "c1");
        Assert.AreEqual("C/D", rebuilt.layers[0].stateMachine.defaultState.name);
    }

    // A slash-bearing name must also survive the VALIDATOR — it compares an escaped address against raw member
    // names, which used to reject a legitimate `default: A\/B` as dangling.
    [Test]
    public void Slash_In_A_Local_Default_Passes_Validation()
    {
        var c = New("SlashyLocal");
        c.layers[0].stateMachine.AddState("A/B");
        Persist(c);

        string yaml = FixpointOracle.Decode(c);
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://slashlocal");
        var errors = SchemaValidation.Validate(doc);
        Assert.IsEmpty(errors ?? new System.Collections.Generic.List<string>(),
            "a '/' in a state name is legal and must not read as a dangling default");
    }

    // ---- refusals ---------------------------------------------------------------------------------

    [Test]
    public void DefaultState_Path_Naming_A_SubMachine_Is_Refused()
    {
        string yaml =
            "schema: 1\ncontroller: Bad_Fx\nbasis: avatar-root\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n    default: S\n" +
            "    machines:\n      Sub:\n        states:\n          N:\n            motion: ~\n        default: N\n" +
            "    defaultState: Sub\n";
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://badsm");
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.Build(doc, out _));
        StringAssert.Contains("defaultState", ex.Message, "the refusal names the key, not a transition target");
        StringAssert.Contains("sub-machine", ex.Message);
    }

    [Test]
    public void DefaultState_Path_Naming_Nothing_Is_Refused_By_Validation()
    {
        string yaml =
            "schema: 1\ncontroller: Bad2_Fx\nbasis: avatar-root\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n    default: S\n" +
            "    defaultState: Nope/Missing\n";
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://badpath");
        var errors = SchemaValidation.Validate(doc);
        Assert.IsTrue(errors.Any(e => e.Contains("dangling-default-state")),
            "got: " + string.Join(" | ", errors));
    }

    // ---- item 2: a clip binding collision is a refusal, not a silent overwrite ---------------------

    [Test]
    public void Two_Bindings_Reconstructing_To_One_Target_Are_Refused()
    {
        var c = New("Collide");
        c.AddParameter("Prop/MeshRenderer.m_Enabled", AnimatorControllerParameterType.Float);
        var clip = new AnimationClip { name = "both" };
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("", typeof(Animator), "Prop/MeshRenderer.m_Enabled"),
            AnimationCurve.Constant(0, 1, 1f));
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("Prop", typeof(MeshRenderer), "m_Enabled"),
            AnimationCurve.Constant(0, 1, 0f));
        AssetDatabase.AddObjectToAsset(clip, c);
        c.layers[0].stateMachine.AddState("S").motion = clip;
        Persist(c);

        var w = ControllerDecompile.Walk(c);
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("two different bindings")),
            "a collision must refuse rather than let one curve overwrite the other; got: "
            + string.Join(" | ", w.Refusals));
    }

    [Test]
    public void A_Slash_Bearing_Param_Without_A_Dot_Is_Still_Fine()
    {
        // The precedence case a worker sidestepped by renaming: a declared parameter wins over any
        // scene-binding reading, and this name cannot even parse as one (no '.').
        string yaml =
            "schema: 1\ncontroller: Slash_Fx\nbasis: avatar-root\n" +
            "parameters:\n  \"Lantern/GlowSmooth\": { type: float, aap: true }\n" +
            "layers:\n  - name: L\n    states:\n      S:\n        motion: { clip: w }\n    default: S\n" +
            "clips:\n  w: { set: { \"Lantern/GlowSmooth\": 1.0 } }\n";
        var doc = AnimatorSchemaYaml.Parse(yaml, "mem://slashparam");
        ControllerEmit.Build(doc, out var built);
        var clip = (AnimationClip)built.Controller.layers[0].stateMachine.states[0].state.motion;
        var b = AnimationUtility.GetCurveBindings(clip).Single();
        Assert.AreEqual("", b.path, "binds as an animator-parameter curve, not a scene binding");
        Assert.AreEqual(typeof(Animator), b.type);
        Assert.AreEqual("Lantern/GlowSmooth", b.propertyName);

        var w = ControllerDecompile.Walk(built.Controller);
        Assert.IsEmpty(w.Refusals);
        StringAssert.Contains("Lantern/GlowSmooth", AnimatorSchemaEmit.Serialize(w.Doc));
    }
}
