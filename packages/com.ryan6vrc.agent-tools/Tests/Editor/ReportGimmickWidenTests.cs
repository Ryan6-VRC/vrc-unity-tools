using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using Ryan6Vrc.AgentTools.Editor;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.ScriptableObjects;

// Widening coverage: Unity IConstraint components must land in ReportGimmick's constraint edge-list with
// their per-type Axis-flag mask and NO false READ-MISS note, and the geometric feedback-loop observation
// must transfer to them — without routing them through the VRC-only reflection helpers. VRC-idiom
// regression (world anchor / hold / TargetTransform notes) is covered by the coordinator's live-corpus run,
// not fabricated here (headless VRC-constraint fixtures are awkward and low-value).
public class ReportGimmickWidenTests
{
    [SetUp]
    public void SetUp() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

    private static string ReadReport(string rootPath)
    {
        string summary = ReportGimmick.Report(rootPath);
        int i = summary.IndexOf("log=");
        return i >= 0 ? File.ReadAllText(summary.Substring(i + 4).Trim()) : summary;
    }

    [Test]
    public void ParentConstraint_AppearsInEdgeList_WithAxisMask_NoReadMiss()
    {
        var root = new GameObject("Rig");
        var driven = new GameObject("Driven"); driven.transform.SetParent(root.transform);
        var src = new GameObject("Src"); src.transform.SetParent(root.transform);
        var pc = driven.AddComponent<ParentConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        // Distinct partial/full masks so the assertions prove AxisFlags' letter assembly, not just the prefix.
        pc.translationAxis = Axis.X | Axis.Z;
        pc.rotationAxis = Axis.X | Axis.Y | Axis.Z;

        string report = ReadReport("Rig");
        StringAssert.Contains("ParentConstraint", report);
        StringAssert.Contains("Rig/Src", report);
        // Unity per-type Axis-flag mask rendered, exact letters (VRC path would emit "pos*"/"—", never the
        // colon form): a partial mask spells its axes, an all-on group collapses to "*".
        StringAssert.Contains("pos:XZ", report);
        StringAssert.Contains("rot:*", report);
        // The false-READ-MISS this task exists to prevent: a Unity constraint routed through the VRC
        // reflection helpers would resolve zero groups and emit this note.
        StringAssert.DoesNotContain("could not read the affected-axis mask", report);
        // VRC-only idiom notes must NOT attach to a Unity constraint.
        StringAssert.DoesNotContain("TargetTransform indirection", report);
    }

    [Test]
    public void UnityConstraint_FeedbackLoop_TransfersToObservations()
    {
        var root = new GameObject("Rig");
        var driven = new GameObject("Driven"); driven.transform.SetParent(root.transform);
        // Source is a strict descendant of the driven host — the feedback-loop idiom.
        var src = new GameObject("Inner"); src.transform.SetParent(driven.transform);
        var pc = driven.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });

        string report = ReadReport("Rig");
        StringAssert.Contains("feedback loop", report);
    }

    // ----- Tier-2 generic "Other components" census (F18b) -------------------------------------

    // A component outside every tier-1 family (custom gimmick script here) must still be named, with its
    // object-reference seam (field → target name + hierarchy path) and its top-level scalar fields peeked.
    private class TierTwoProbe : MonoBehaviour
    {
        public UnityEngine.Object reference;
        public string label;
    }

    [Test]
    public void CustomMonoBehaviour_ObjectRefAndScalar_SurfaceInOtherCensus()
    {
        var root = new GameObject("Rig");
        var target = new GameObject("Target"); target.transform.SetParent(root.transform);
        var host = new GameObject("Host"); host.transform.SetParent(root.transform);
        var probe = host.AddComponent<TierTwoProbe>();
        probe.reference = target.transform; // a Component → the object-ref seam renders its hierarchy path
        probe.label = "peek-me";

        string report = ReadReport("Rig");
        StringAssert.Contains("Other components", report);
        StringAssert.Contains("TierTwoProbe", report);            // type
        StringAssert.Contains("Rig/Host", report);                // host
        StringAssert.Contains("reference", report);               // object-ref field name
        StringAssert.Contains("Rig/Target", report);              // object-ref seam: resolved hierarchy path
        StringAssert.Contains("peek-me", report);                 // scalar string peek
        StringAssert.Contains("other=1", report);                 // header/summary count
    }

    // A dangling object reference (asset deleted while a field still points at it) must render broken,
    // not collapse to invisible like a clean-empty slot — same empty-vs-dangling idiom the F11 fix uses.
    [Test]
    public void DanglingObjectRef_RendersBroken_NotDropped()
    {
        var root = new GameObject("Rig");
        var host = new GameObject("Host"); host.transform.SetParent(root.transform);
        var probe = host.AddComponent<TierTwoProbe>();
        var clip = new AnimationClip { name = "doomed" };
        const string assetPath = "Assets/Agent/_rbs_dangling.anim";
        UnityEditor.AssetDatabase.CreateAsset(clip, assetPath);
        probe.reference = clip;
        UnityEditor.AssetDatabase.DeleteAsset(assetPath); // probe.reference is now a dangling (missing) ref

        string report = ReadReport("Rig");
        StringAssert.Contains("(broken: dangling reference)", report);
    }

    [Test]
    public void ModularAvatarMenuItem_ControlNameAndType_SurfaceInPeek()
    {
        var root = new GameObject("Rig");
        var host = new GameObject("MenuHost"); host.transform.SetParent(root.transform);
        var mi = host.AddComponent<ModularAvatarMenuItem>();
        // Control is one struct level below the component; its top-level scalars (name, type) surface in the
        // shallow peek. parameter.name is a SECOND struct level (the documented boundary) and does not.
        mi.Control = new VRCExpressionsMenu.Control
        {
            name = "MyToggleControl",
            type = VRCExpressionsMenu.Control.ControlType.Toggle,
            parameter = new VRCExpressionsMenu.Control.Parameter { name = "MyDrivenParam" },
            value = 1f,
        };

        string report = ReadReport("Rig");
        StringAssert.Contains("ModularAvatarMenuItem", report);
        StringAssert.Contains("MyToggleControl", report);         // control name (one struct level)
        StringAssert.Contains("Toggle", report);                  // control type enum (one struct level)
        // Upper bound of the one-struct-level peek: parameter.name is a SECOND struct level (Control →
        // parameter → name) and must NOT surface in-tool — it's AgentInspector's depth (design decision A).
        StringAssert.DoesNotContain("MyDrivenParam", report);
    }

    [Test]
    public void ModularAvatarMergeArmature_ReferencePath_SurfacesOneStructLevel()
    {
        var root = new GameObject("Rig");
        var host = new GameObject("Outfit"); host.transform.SetParent(root.transform);
        var ma = host.AddComponent<ModularAvatarMergeArmature>();
        ma.mergeTarget.referencePath = "Armature/Hips"; // AvatarObjectReference is one struct level under mergeTarget

        string report = ReadReport("Rig");
        StringAssert.Contains("ModularAvatarMergeArmature", report);
        StringAssert.Contains("referencePath", report);           // the path-string field name
        StringAssert.Contains("Armature/Hips", report);           // its value, one struct level deep
    }

    [Test]
    public void OtherCount_ExcludesTierOneAndTransforms()
    {
        var root = new GameObject("Rig");
        var a = new GameObject("A"); a.transform.SetParent(root.transform);
        a.AddComponent<TierTwoProbe>();
        var b = new GameObject("B"); b.transform.SetParent(root.transform);
        b.AddComponent<ModularAvatarMenuItem>();
        // A tier-1 Unity constraint AND four Transforms are present; neither must inflate other=N.
        var c = new GameObject("C"); c.transform.SetParent(root.transform);
        c.AddComponent<PositionConstraint>();

        string report = ReadReport("Rig");
        // other counts one row per single-visit component, so a tier-1 constraint (rendered in the
        // constraints TABLE, not the census) and the four Transforms cannot inflate it: probe + menuitem = 2.
        StringAssert.Contains("other=2", report);
    }
}
