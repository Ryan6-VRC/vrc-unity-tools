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
        // must refuse it (previously it resolved silently into a junk binding). 'Object' is what a
        // classID-0 placeholder curve reads back as; same check refuses it.
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.ResolveComponentType("Time"));
        StringAssert.Contains("Time", ex.Message);
        Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.ResolveComponentType("Object"));
    }

    [Test]
    public void Refuses_bare_monobehaviour()
    {
        // A missing-script curve reads back as typeof(MonoBehaviour) (measured, 2022.3): resolving the
        // bare name would silently round-trip vendor junk, so it is refused by name.
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => ControllerEmit.ResolveComponentType("MonoBehaviour"));
        StringAssert.Contains("missing script", ex.Message);
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

    // ---- decompile: an embedded PPtr (object-reference) curve is a located Refusal ------------

    [Test]
    public void Walk_pptr_curve_refuses()
    {
        // A material swap: the schema's set/curves grammar can only author float curves, so decoding
        // around a PPtr curve would silently drop the swap on recompile. (Standalone .anim swaps are
        // unaffected — DecodeMotion keeps a pathed clip as a ref: and never decodes its content.)
        var c = new AnimatorController { name = "Pptr_Fx" };
        c.AddLayer("L");
        var st = c.layers[0].stateMachine.AddState("S");
        var clip = new AnimationClip { name = "swap" };
        var mat = new Material(Shader.Find("Standard"));
        AnimationUtility.SetObjectReferenceCurve(clip,
            EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer), "m_Materials.Array.data[0]"),
            new[] { new ObjectReferenceKeyframe { time = 0f, value = mat } });
        st.motion = clip;

        var w = ControllerDecompile.Walk(c); // must NOT throw
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("PPtr") && r.Contains("m_Materials")),
            "embedded PPtr curve -> located refusal: " + string.Join(" | ", w.Refusals));
        Assert.IsFalse(w.Refusals.Any(r => r.Contains("zero curve bindings")),
            "a PPtr-only clip is refused for the real reason, not as 'no animatable content'");
        Object.DestroyImmediate(mat);
        Object.DestroyImmediate(c);
    }

    // ---- decompile: placeholder bindings a vendor clip really carries are Refusals, not junk YAML ----

    [Test]
    public void Walk_placeholder_bindings_refuse()
    {
        // Serialized producers only reachable through import (no public API fabricates them): a classID-0
        // curve (an OSCmooth-style no-op placeholder) reads back as typeof(UnityEngine.Object), a curve
        // whose script guid resolves to nothing as typeof(MonoBehaviour). Previously the first would emit
        // 'X/Object.NOPE' and the second 'X/MonoBehaviour.someField' — recompilable-looking YAML that drops
        // the (already broken) intent silently. Both must refuse; the valid sibling curve still decodes.
        AnimatorTestHelpers.EnsureFolder(ClipsOut);
        string animPath = ClipsOut + "/placeholders.anim";
        System.IO.File.WriteAllText(animPath, PlaceholderAnimYaml);
        AssetDatabase.ImportAsset(animPath, ImportAssetOptions.ForceSynchronousImport);
        var imported = AssetDatabase.LoadAssetAtPath<AnimationClip>(animPath);
        Assert.IsNotNull(imported, "fixture .anim imported");

        // Instantiate = pathless copy, so DecodeMotion inlines it (a pathed standalone .anim stays a ref:).
        var copy = Object.Instantiate(imported);
        copy.name = "placeholders";
        var c = new AnimatorController { name = "Placeholder_Fx" };
        c.AddLayer("L");
        c.layers[0].stateMachine.AddState("S").motion = copy;

        var w = ControllerDecompile.Walk(c); // must NOT throw
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("UnityEngine.Object")),
            "classID-0 placeholder -> located refusal: " + string.Join(" | ", w.Refusals));
        Assert.IsTrue(w.Refusals.Any(r => r.Contains("UnityEngine.MonoBehaviour")),
            "missing-script binding -> located refusal: " + string.Join(" | ", w.Refusals));
        var spec = w.Doc.Clips.First(x => x.Name == "placeholders");
        var targets = spec.Sets.Keys.Concat(spec.Curves.Select(cs => cs.Binding)).ToList();
        CollectionAssert.AreEquivalent(new[] { "X/Light.m_Intensity" }, targets,
            "the valid sibling decodes (as set or curve); no placeholder binding leaks into the document");

        Object.DestroyImmediate(copy);
        Object.DestroyImmediate(c);
    }

    // A minimal serialized clip: classID-0 curve ('NOPE'), a classID-114 curve with an unresolvable script
    // guid, and a valid Light.m_Intensity curve. Constant single-key curves.
    private const string PlaceholderAnimYaml = @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!74 &7400000
AnimationClip:
  m_ObjectHideFlags: 0
  m_Name: placeholders
  serializedVersion: 6
  m_Legacy: 0
  m_Compressed: 0
  m_UseHighQualityCurve: 1
  m_RotationCurves: []
  m_CompressedRotationCurves: []
  m_EulerCurves: []
  m_PositionCurves: []
  m_ScaleCurves: []
  m_FloatCurves:
  - curve:
      serializedVersion: 2
      m_Curve:
      - serializedVersion: 3
        time: 0
        value: 1
        inSlope: 0
        outSlope: 0
        tangentMode: 136
        weightedMode: 0
        inWeight: 0.33333334
        outWeight: 0.33333334
      m_PreInfinity: 2
      m_PostInfinity: 2
      m_RotationOrder: 4
    attribute: NOPE
    path: X
    classID: 0
    script: {fileID: 0}
  - curve:
      serializedVersion: 2
      m_Curve:
      - serializedVersion: 3
        time: 0
        value: 2
        inSlope: 0
        outSlope: 0
        tangentMode: 136
        weightedMode: 0
        inWeight: 0.33333334
        outWeight: 0.33333334
      m_PreInfinity: 2
      m_PostInfinity: 2
      m_RotationOrder: 4
    attribute: someField
    path: X
    classID: 114
    script: {fileID: 11500000, guid: 00000000000000000000000000000dea, type: 3}
  - curve:
      serializedVersion: 2
      m_Curve:
      - serializedVersion: 3
        time: 0
        value: 3
        inSlope: 0
        outSlope: 0
        tangentMode: 136
        weightedMode: 0
        inWeight: 0.33333334
        outWeight: 0.33333334
      m_PreInfinity: 2
      m_PostInfinity: 2
      m_RotationOrder: 4
    attribute: m_Intensity
    path: X
    classID: 108
    script: {fileID: 0}
  m_PPtrCurves: []
  m_SampleRate: 60
  m_WrapMode: 0
  m_Bounds:
    m_Center: {x: 0, y: 0, z: 0}
    m_Extent: {x: 0, y: 0, z: 0}
  m_ClipBindingConstant:
    genericBindings: []
    pptrCurveMapping: []
  m_AnimationClipSettings:
    serializedVersion: 2
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 0
  m_EditorCurves: []
  m_EulerEditorCurves: []
  m_HasGenericRootTransform: 0
  m_HasMotionFloatCurves: 0
  m_Events: []
";
}
