using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;

// Behavioral tests for inline-clip component-type resolution (the C1 widening): the namespace-allowlisted
// ResolveComponentType (VRC dynamics + UnityEngine.Animations join the vocabulary), the tightened
// non-Component refusal, and the decompile side's oracle symmetry (an out-of-vocabulary binding type is a
// located Refusal, never YAML that won't recompile). typeof() references double as rename canaries against
// the pinned SDK: a VRC type rename fails these tests at compile time, not silently at resolve time.
// Headless via tools/run-editmode-tests.ps1.
public class ClipBindingTypeTests
{
    private const string ScratchFolder = "Assets/Agent/Scratch/emit";
    private const string ClipsOut = "Assets/Agent/Scratch/ClipBindingType_NUnit";

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(ScratchFolder)) AssetDatabase.DeleteAsset(ScratchFolder);
        if (AssetDatabase.IsValidFolder(ClipsOut)) AssetDatabase.DeleteAsset(ClipsOut);
        if (_yamlPath != null && System.IO.File.Exists(_yamlPath)) System.IO.File.Delete(_yamlPath);
        _yamlPath = null;
    }

    // ---- resolver: VRC dynamics types ---------------------------------------------------------

    private static readonly TestCaseData[] VrcDynamicsCases =
    {
        new TestCaseData("VRCPositionConstraint", typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCPositionConstraint)),
        new TestCaseData("VRCRotationConstraint", typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint)),
        new TestCaseData("VRCScaleConstraint",    typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCScaleConstraint)),
        new TestCaseData("VRCParentConstraint",   typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint)),
        new TestCaseData("VRCAimConstraint",      typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCAimConstraint)),
        new TestCaseData("VRCLookAtConstraint",   typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCLookAtConstraint)),
        new TestCaseData("VRCContactReceiver",    typeof(VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver)),
        new TestCaseData("VRCContactSender",      typeof(VRC.SDK3.Dynamics.Contact.Components.VRCContactSender)),
        new TestCaseData("VRCPhysBone",           typeof(VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone)),
    };

    [TestCaseSource(nameof(VrcDynamicsCases))]
    public void Resolves_vrc_dynamics_types(string simpleName, System.Type expected)
        => Assert.AreEqual(expected, ControllerEmit.ResolveComponentType(simpleName));

    // ---- resolver: UnityEngine.Animations constraints (the sub-namespace gap) -----------------

    private static readonly TestCaseData[] UnityConstraintCases =
    {
        new TestCaseData("PositionConstraint", typeof(UnityEngine.Animations.PositionConstraint)),
        new TestCaseData("RotationConstraint", typeof(UnityEngine.Animations.RotationConstraint)),
        new TestCaseData("ScaleConstraint",    typeof(UnityEngine.Animations.ScaleConstraint)),
        new TestCaseData("ParentConstraint",   typeof(UnityEngine.Animations.ParentConstraint)),
        new TestCaseData("AimConstraint",      typeof(UnityEngine.Animations.AimConstraint)),
        new TestCaseData("LookAtConstraint",   typeof(UnityEngine.Animations.LookAtConstraint)),
    };

    [TestCaseSource(nameof(UnityConstraintCases))]
    public void Resolves_unityengine_animations_types(string simpleName, System.Type expected)
        => Assert.AreEqual(expected, ControllerEmit.ResolveComponentType(simpleName));

    // ---- resolver: the pre-widening surface is unchanged ---------------------------------------

    [TestCase("GameObject")]
    [TestCase("Transform")]
    [TestCase("Renderer")]
    [TestCase("SkinnedMeshRenderer")]
    [TestCase("Light")]
    [TestCase("AudioSource")]
    public void Still_resolves_core_types(string simpleName)
        => Assert.IsNotNull(ControllerEmit.ResolveComponentType(simpleName));

    // ---- resolver: refusals survive the widening -----------------------------------------------

    [Test]
    public void Refuses_non_component_type()
    {
        // UnityEngine.Time IS a UnityEngine-namespace type — the Component-assignability check is what
        // must refuse it (previously it resolved silently into a junk binding).
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.ResolveComponentType("Time"));
        StringAssert.Contains("Time", ex.Message);
    }

    [Test]
    public void Refuses_out_of_scope_and_unknown_types()
    {
        // UnityEngine.UI stays out of scope (simple name 'Image' is under no allowlisted namespace), and
        // the refusal must name the supported surface so the author can fix the document.
        var ui = Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.ResolveComponentType("Image"));
        StringAssert.Contains("Image", ui.Message);
        StringAssert.Contains("VRC.SDK3.Dynamics", ui.Message);
        Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.ResolveComponentType("NoSuchComponentType"));
    }

    // ---- end-to-end: CompileClips emits the new bindings ---------------------------------------

    private string _yamlPath;
    private string WriteYaml(string body)
    {
        _yamlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "clips_" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".yaml");
        System.IO.File.WriteAllText(_yamlPath, body);
        return _yamlPath;
    }

    [Test]
    public void CompileClips_emits_vrc_constraint_and_contact_bindings()
    {
        // Sources.source0.Weight also proves the first-dot split: the property keeps its own dots.
        string body = "schema: 1\nbasis: avatar-root\ncontroller: C1Clips\n" +
            "clips:\n" +
            "  Latch: { set: { \"Cage/VRCPositionConstraint.Sources.source0.Weight\": 1, \"Recv/VRCContactReceiver.allowOthers\": 0 } }\n";
        string s = CompileClips.Compile(WriteYaml(body), ClipsOut);
        StringAssert.Contains("=> PASS", s);

        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipsOut + "/Latch.anim");
        Assert.IsNotNull(clip);
        var bindings = AnimationUtility.GetCurveBindings(clip);
        Assert.IsTrue(bindings.Any(b =>
                b.path == "Cage"
                && b.type == typeof(VRC.SDK3.Dynamics.Constraint.Components.VRCPositionConstraint)
                && b.propertyName == "Sources.source0.Weight"),
            "constraint source-weight binding");
        Assert.IsTrue(bindings.Any(b =>
                b.path == "Recv"
                && b.type == typeof(VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver)
                && b.propertyName == "allowOthers"),
            "contact filter binding");
    }

    // ---- round-trip: newly accepted types decompile back to the same YAML ----------------------

    [Test]
    public void Roundtrip_vrc_bindings_decompile_equal()
    {
        const string doc = @"schema: 1
controller: C1RoundTrip_Fx
basis: avatar-root
role: fx
clips:
  Latch:
    set:
      Cage/VRCPositionConstraint.Sources.source0.Weight: 1
      Cage/VRCScaleConstraint.GlobalWeight: 1
      Recv/VRCContactReceiver.allowOthers: 0
      Node/PositionConstraint.m_Weight: 1
layers:
  - name: L
    states:
      S: { motion: { clip: Latch } }
";
        var src = AnimatorSchemaYaml.Parse(doc, "test");
        ControllerEmit.Build(src, out var emitted);
        var w = ControllerDecompile.Walk(emitted.Controller);

        Assert.AreEqual(0, w.Refusals.Count,
            "all bindings are in-vocabulary: " + string.Join(" | ", w.Refusals));
        var latch = w.Doc.Clips.First(c => c.Name == "Latch");
        CollectionAssert.AreEquivalent(src.Clips[0].Sets.Keys, latch.Sets.Keys,
            "binding targets survive the round-trip verbatim");
    }

    // ---- decompile: an out-of-vocabulary binding type is a located Refusal ---------------------

    [Test]
    public void Walk_out_of_vocabulary_binding_type_refuses()
    {
        // VideoPlayer is a real, bindable Component whose namespace (UnityEngine.Video) is outside the
        // allowlist — the shape of a vendor controller animating a UI/TMP/script component. (A test-local
        // MonoBehaviour won't do: EditorCurveBinding rejects a nested type outright — "Invalid type".)
        var c = new AnimatorController { name = "OutOfVocab_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        var clip = new AnimationClip { name = "mixed" };
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("X", typeof(UnityEngine.Video.VideoPlayer), "m_PlaybackSpeed"),
            AnimationCurve.Constant(0f, 1f, 1f));
        AnimationUtility.SetEditorCurve(clip,
            EditorCurveBinding.FloatCurve("X", typeof(Light), "m_Intensity"),
            AnimationCurve.Constant(0f, 1f, 2f));
        st.motion = clip;

        var w = ControllerDecompile.Walk(c); // must NOT throw, and must NOT emit doomed YAML
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("VideoPlayer")),
            "out-of-vocabulary binding type -> located refusal: " + string.Join(" | ", w.Refusals));
        var mixed = w.Doc.Clips.First(x => x.Name == "mixed");
        Assert.IsTrue(mixed.Sets.ContainsKey("X/Light.m_Intensity"),
            "the in-vocabulary sibling binding still decodes");
        Assert.IsFalse(mixed.Sets.Keys.Any(k => k.Contains("VideoPlayer")),
            "the refused binding must not appear in the document");
        Object.DestroyImmediate(c);
    }
}
