using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using Ryan6Vrc.AgentTools.Editor;
using nadena.dev.modular_avatar.core;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

public class ReportGimmickTests
{
    [SetUp]
    public void SetUp() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

    // Record what the door wrote and delete exactly that; a glob over `gimmick_*.md` would also sweep
    // snapshots the operator took by hand.
    private static readonly List<string> Artifacts = new List<string>();

    [OneTimeTearDown]
    public void DeleteWrittenSnapshots()
    {
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), new List<string>());
        Artifacts.Clear();
    }

    // Every door call in this fixture goes through here, including summary-only reads: each writes a Snapshot.
    private static string Summary(string rootPath)
    {
        string summary = ReportGimmick.Run(rootPath);
        int i = summary.IndexOf("log=");
        if (i >= 0) Artifacts.Add(summary.Substring(i + 4).Trim());
        return summary;
    }

    private static string Body(string summary)
    {
        int i = summary.IndexOf("log=");
        return i >= 0 ? File.ReadAllText(summary.Substring(i + 4).Trim()) : summary;
    }

    private static string ReadReport(string rootPath) => Body(Summary(rootPath));

    private static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform);
        return go;
    }

    // ----- Chain-subtree census ----------------------------------------------------------------------
    //
    // Three counts over one set. The tests pin both polarities: an idle chain reads all-zero, and the
    // load-bearing shapes which carry no skin weights (a rigid prop on the chain, a second component on the
    // bone) read nonzero.

    // A chain of `length` bones descending from `parent`; returns the topmost.
    private static Transform Chain(GameObject parent, string name, int length)
    {
        var top = Child(parent, name);
        var cur = top;
        for (int i = 1; i < length; i++) cur = Child(cur, name + "_" + i);
        return top.transform;
    }

    private static VRCPhysBone PhysBone(GameObject host, Transform rootTransform)
    {
        var pb = host.AddComponent<VRCPhysBone>();
        pb.rootTransform = rootTransform;
        return pb;
    }

    // The legacy boneWeights setter feeds the same mesh data GetAllBoneWeights reads.
    private static SkinnedMeshRenderer Skin(GameObject host, params Transform[] bones)
    {
        var mesh = new Mesh();
        mesh.vertices = new Vector3[bones.Length];
        var bw = new BoneWeight[bones.Length];
        var poses = new Matrix4x4[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            bw[i].boneIndex0 = i;
            bw[i].weight0 = 1f;
            poses[i] = Matrix4x4.identity;
        }
        mesh.boneWeights = bw;
        mesh.bindposes = poses;
        var smr = host.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
        return smr;
    }

    // The partial-take case: the mesh that skinned this chain went out with a dropped piece, leaving zero
    // `skinned` and `hosting` over a nonzero `bones`.
    [Test]
    public void ChainSubtree_ChainNoMeshSkinsAndNothingOccupies_ReadsAllZero()
    {
        var root = new GameObject("Rig");
        var kept = Chain(root, "Kept", 2);
        var orphan = Chain(root, "Sleeve", 3);
        PhysBone(Child(root, "PB_sleeve"), orphan);
        Skin(Child(root, "SurvivingMesh"), kept, kept.GetChild(0)); // skins the kept chain only

        string report = ReadReport("Rig");
        StringAssert.Contains("bones=3 skinned=0 hosting=0", report);
    }

    [Test]
    public void ChainSubtree_MeshSkinsTheChain_CountsSkinnedBones()
    {
        var root = new GameObject("Rig");
        var chain = Chain(root, "Tail", 3);
        PhysBone(Child(root, "PB_tail"), chain);
        Skin(Child(root, "TailMesh"), chain, chain.GetChild(0));

        string report = ReadReport("Rig");
        StringAssert.Contains("bones=3 skinned=2 hosting=0", report);
    }

    // A rigid renderer on the chain (charm, bell, earring) carries no skin weights and is fully load-bearing;
    // `hosting` is what separates it from the all-zero chain above.
    [Test]
    public void ChainSubtree_RigidRendererOnChain_ReadsHostingNotZero()
    {
        var root = new GameObject("Rig");
        var chain = Chain(root, "Charm", 2);
        chain.GetChild(0).gameObject.AddComponent<MeshRenderer>();
        PhysBone(Child(root, "PB_charm"), chain);

        string report = ReadReport("Rig");
        StringAssert.Contains("bones=2 skinned=0 hosting=1", report);
    }

    [Test]
    public void ChainSubtree_OwnPhysBoneIsNotCountedAsHosting()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Bone");
        bone.AddComponent<VRCPhysBone>(); // self-rooted: rootTransform left null

        string report = ReadReport("Rig");
        StringAssert.Contains("bones=1 skinned=0 hosting=0", report);
    }

    [Test]
    public void ChainSubtree_SecondPhysBoneOnTheBone_CountsAsHosting()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Bone");
        bone.AddComponent<VRCPhysBone>();
        bone.AddComponent<VRCPhysBone>();

        string report = ReadReport("Rig");
        StringAssert.Contains("bones=1 skinned=0 hosting=1", report);
    }

    // Aimed at an armature, the meshes are siblings of the report root, so the sweep must reach past it. This
    // rig carries no descriptor, so it also pins the outermost-ancestor fallback.
    [Test]
    public void ChainSubtree_MeshOutsideTheReportRoot_StillCountsAsSkinned()
    {
        var avatar = new GameObject("Avatar");
        var armature = Child(avatar, "Armature");
        var chain = Chain(armature, "Hair", 2);
        PhysBone(Child(armature, "PB_hair"), chain);
        Skin(Child(avatar, "Body"), chain, chain.GetChild(0)); // sibling of the report root

        string report = ReadReport("Avatar/Armature");
        StringAssert.Contains("bones=2 skinned=2", report);
    }

    // AvatarB's renderer still bound to AvatarA's bones is what duplicating an avatar and leaving a renderer on
    // the source rig produces. `Staging` is the aim with no descriptor above the report root: scope resolves from
    // the row's own physbone, not the report root, so one expected count covers both aims.
    [TestCase("Staging/AvatarA")]
    [TestCase("Staging")]
    public void ChainSubtree_ScopeIsTheRowsOwnAvatar_WhicheverLevelIsReported(string reportRoot)
    {
        var staging = new GameObject("Staging");

        var avatarA = Child(staging, "AvatarA");
        avatarA.AddComponent<VRCAvatarDescriptor>();
        var chain = Chain(avatarA, "Hair", 2);
        PhysBone(Child(avatarA, "PB_hair"), chain);
        Skin(Child(avatarA, "BodyA"), chain);                 // in scope   → Hair counts

        var avatarB = Child(staging, "AvatarB");
        avatarB.AddComponent<VRCAvatarDescriptor>();
        Skin(Child(avatarB, "BodyB"), chain.GetChild(0));     // out of scope → Hair_1 must not

        string report = ReadReport(reportRoot);
        StringAssert.Contains("bones=2 skinned=1 hosting=0", report);
    }

    [Test]
    public void ChainSubtree_RootTransformIndirection_CountsFromTargetNotHost()
    {
        var root = new GameObject("Rig");
        var chain = Chain(root, "Skirt", 3);
        var host = Child(root, "PB_skirt");
        host.AddComponent<MeshRenderer>(); // would be counted if the set started at the host
        PhysBone(host, chain);

        string report = ReadReport("Rig");
        StringAssert.Contains("bones=3 skinned=0 hosting=0", report);
    }

    // Two kinds of assertion, because neither subsumes the other. The equality proves the legend reaches the
    // report untruncated, but both sides are the same string, so deleting a clause from the constant keeps it
    // green. The clause checks survive a reword and fail that deletion: without the all-zero clause a reader
    // deletes a name-merged bone reading `skinned=0`, and without the membership clause they read the count as
    // which bones the chain moves.
    [Test]
    public void ChainSubtree_CensusRowIsAccompaniedByItsLegend()
    {
        var root = new GameObject("Rig");
        Child(root, "Bone").AddComponent<VRCPhysBone>();

        Assert.AreEqual(ReportGimmick.ChainSubtreeLegend.TrimEnd('\n'), LegendLine(ReadReport("Rig")));

        StringAssert.Contains("All-zero is NOT a dead chain", ReportGimmick.ChainSubtreeLegend);
        StringAssert.Contains("not a claim about which bones move", ReportGimmick.ChainSubtreeLegend);
        Assert.IsFalse(ReportGimmick.ChainSubtreeLegend.Contains("upper bound"),
            "exclusions are applied now — an upper-bound caveat would be one the code no longer earns");
    }

    [Test]
    public void PhysBoneRow_MarksEveryCurvedField_AndCarriesTheCurveLegend()
    {
        var root = new GameObject("Rig");
        var curved = Child(root, "Curved").AddComponent<VRCPhysBone>();
        curved.radius = 0.03f;
        curved.radiusCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        curved.gravityCurve = AnimationCurve.Linear(0f, 1f, 1f, 0.5f);
        Child(root, "Plain").AddComponent<VRCPhysBone>();

        string report = ReadReport("Rig");
        StringAssert.Contains("| 0.03~ |", report);
        StringAssert.Contains("| gravity radius |", report, "an unprinted field's curve (gravity) must still be named");
        StringAssert.Contains("| — |", report, "an uncurved row says so rather than leaving the cell blank");
        Assert.AreEqual(ReportGimmick.CurveLegend.TrimEnd('\n'), LegendLine(report, "_`~`"));
        StringAssert.Contains("the scalar times the curve", ReportGimmick.CurveLegend);
    }

    // `ignoreTransforms` prunes the listed transform and its descendants (the field's Inspector tooltip), so
    // the SDK's chain here is 2 against a hierarchy subtree of 4 (ChainRoot, Keep, Dropped, DroppedChild).
    [Test]
    public void ChainSubtree_countsTheSdkChain_notTheHierarchySubtree()
    {
        var root = new GameObject("Rig");
        var chainRoot = Child(root, "ChainRoot");
        Child(chainRoot, "Keep");
        var dropped = Child(chainRoot, "Dropped");
        Child(dropped, "DroppedChild");

        var pb = chainRoot.AddComponent<VRCPhysBone>();
        pb.ignoreTransforms = new List<Transform> { dropped.transform };

        var report = ReadReport("Rig");
        StringAssert.Contains("bones=2", report);
        Assert.IsFalse(report.Contains("bones=4"), "the ignored transform's CHILD must be pruned with it");
    }

    // Whether the endpoint adds a transform-less chain member is the SDK's call; this pins that it does not.
    [Test]
    public void ChainSubtree_endpointPosition_changesNoCountAndDoesNotThrow()
    {
        var root = new GameObject("Rig");
        var chain = Chain(root, "Tail", 3);
        var pb = PhysBone(Child(root, "PB_tail"), chain);
        pb.endpointPosition = new Vector3(0f, 0.05f, 0f);

        StringAssert.Contains("bones=3", ReadReport("Rig"));
    }

    // Isolates the legend paragraph: its tokens (`skinned=0`) also occur in the census cell, so a
    // whole-document match would pass with the legend deleted.
    private static string LegendLine(string report) => LegendLine(report, "_`chain subtree`");

    private static string LegendLine(string report, string startsWith)
    {
        foreach (var line in report.Split('\n'))
            if (line.StartsWith(startsWith)) return line;
        Assert.Fail("no legend line starting `" + startsWith + "`:\n" + report);
        return null;
    }

    [Test]
    public void ChainSubtree_NoPhysBones_NoCensusOrLegend()
    {
        var root = new GameObject("Rig");
        Child(root, "Bone");

        string report = ReadReport("Rig");
        StringAssert.DoesNotContain("chain subtree", report);
        // Not "bones=": the header's `physbones=0` count contains that substring.
        StringAssert.DoesNotContain("skinned=", report);
    }

    // ----- Shared physbone target (the per-range variant stack) --------------------------------------
    //
    // A vendor body-morph slider can switch between several physbones tuned per size range, all targeting one
    // bone (docs/outfits.md). The observation renders live state and never gates on it: a scene's static
    // enable flags are not authoritative on a property an animator drives.

    private static VRCPhysBone Variant(GameObject parent, string name, Transform target, bool live)
    {
        var host = Child(parent, name);
        var pb = host.AddComponent<VRCPhysBone>();
        pb.rootTransform = target;
        host.SetActive(live);
        return pb;
    }

    [Test]
    public void SharedTarget_MixedLiveState_ObservedWithLiveMemberNamed()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Chain");
        Variant(root, "PB_flat", bone.transform, false);
        Variant(root, "PB_medium", bone.transform, true);
        Variant(root, "PB_big", bone.transform, false);

        string summary = Summary("Rig");
        string report = Body(summary);
        StringAssert.Contains("**physbones share one target**", report);
        StringAssert.Contains("Rig/Chain", report);
        StringAssert.Contains("3 physbones", report);
        StringAssert.Contains("live: `Rig/PB_medium`", report);
        StringAssert.Contains("docs/outfits.md", report);
        StringAssert.Contains("observations=1", summary);
    }

    [Test]
    public void SharedTarget_AllMembersLive_StillObserved()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Chain");
        Variant(root, "PB_a", bone.transform, true);
        Variant(root, "PB_b", bone.transform, true);

        string report = ReadReport("Rig");
        StringAssert.Contains("**physbones share one target**", report);
        StringAssert.Contains("live: `Rig/PB_a`, `Rig/PB_b`", report);
    }

    [Test]
    public void SharedTarget_NoLiveMember_ReportsNone()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Chain");
        Variant(root, "PB_a", bone.transform, false);
        Variant(root, "PB_b", bone.transform, false);

        string report = ReadReport("Rig");
        StringAssert.Contains("live: none", report);
    }

    [Test]
    public void SharedTarget_InactiveReportRoot_LivenessStaysRelative()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Chain");
        Variant(root, "PB_on", bone.transform, true);
        Variant(root, "PB_off", bone.transform, false);
        root.SetActive(false);

        string report = ReadReport("Rig");
        StringAssert.Contains("live: `Rig/PB_on`", report);
        StringAssert.DoesNotContain("live: none", report);
    }

    // A self-rooted pair is invisible to a rootTransform-only grouping, and one path names both members, hence
    // the component ordinal.
    [Test]
    public void SharedTarget_SelfRootedPairOnOneObject_IsGroupedAndDisambiguated()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Bone");
        host.AddComponent<VRCPhysBone>();
        host.AddComponent<VRCPhysBone>().enabled = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("**physbones share one target**", report);
        StringAssert.Contains("2 physbones", report);
        StringAssert.Contains("live: `Rig/Bone [VRCPhysBone#0]`", report);
    }

    // Vendors use either flag to switch variants, so the table carries both. Its `active` is the absolute
    // state; only the observation is root-relative.
    [Test]
    public void PhysBoneTable_CarriesEnabledAndActive()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Chain");
        Variant(root, "PB_on", bone.transform, true);
        Variant(root, "PB_objectOff", bone.transform, false);
        Variant(root, "PB_componentOff", bone.transform, true).enabled = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("enabled=1 active=1", report);
        StringAssert.Contains("enabled=1 active=0", report);
        StringAssert.Contains("enabled=0 active=1", report);
    }

    [Test]
    public void SinglePhysBoneOnATarget_IsNotObserved()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Chain");
        Variant(root, "PB_only", bone.transform, true);

        StringAssert.Contains("observations=0", Summary("Rig"));
    }

    // ----- Constraint live cell ---------------------------------------------------------------------
    //
    // Each constraint family carries a second enable flag beyond the Behaviour's own: VRC `IsActive`, Unity
    // `constraintActive`. The live cell names which flag is down.

    private static GameObject Rig(out GameObject host, out GameObject src)
    {
        var root = new GameObject("Rig");
        host = Child(root, "Driven");
        src = Child(root, "Src");
        return root;
    }

    // The live-cell negatives must be scoped to one row: the physbone table's `immobile` cell also renders
    // `0 (All)`, so a whole-document DoesNotContain("0 (") holds only on a fixture with no physbones.
    private static string ConstraintRow(string report, string type, string driven)
    {
        foreach (var line in report.Split('\n'))
            if (line.StartsWith("| " + type + " |") && line.Contains("`" + driven + "`")) return line;
        Assert.Fail("no constraints-table row for " + type + " on " + driven + ":\n" + report);
        return null;
    }

    [Test]
    public void VrcConstraint_IsActiveFalse_LiveCellNamesTheFlag()
    {
        GameObject host, src;
        Rig(out host, out src);
        var con = host.AddComponent<VRCParentConstraint>();
        con.IsActive = false;   // Behaviour stays enabled, object stays active

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (IsActive)", ConstraintRow(report, "VRCParentConstraint", "Rig/Driven"));
    }

    [Test]
    public void VrcConstraint_Running_LiveCellIsOne()
    {
        GameObject host, src;
        Rig(out host, out src);
        var con = host.AddComponent<VRCParentConstraint>();
        con.IsActive = true;

        string report = ReadReport("Rig");
        StringAssert.DoesNotContain("0 (", ConstraintRow(report, "VRCParentConstraint", "Rig/Driven"));
    }

    [Test]
    public void VrcConstraint_ComponentDisabled_ReportsEnabledNotIsActive()
    {
        GameObject host, src;
        Rig(out host, out src);
        var con = host.AddComponent<VRCParentConstraint>();
        con.IsActive = true;
        con.enabled = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (enabled)", ConstraintRow(report, "VRCParentConstraint", "Rig/Driven"));
    }

    [Test]
    public void UnityConstraint_ConstraintActiveFalse_LiveCellNamesTheFlag()
    {
        GameObject host, src;
        Rig(out host, out src);
        var pc = host.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        pc.constraintActive = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (constraintActive)", ConstraintRow(report, "PositionConstraint", "Rig/Driven"));
    }

    [Test]
    public void UnityConstraint_InactiveObject_ReportsObject()
    {
        GameObject host, src;
        Rig(out host, out src);
        var pc = host.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        pc.constraintActive = true;
        host.SetActive(false);

        string report = ReadReport("Rig");
        StringAssert.Contains("0 (object)", ConstraintRow(report, "PositionConstraint", "Rig/Driven"));
    }

    [Test]
    public void Constraint_InactiveReportRoot_StaysLive()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var pc = host.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        pc.constraintActive = true;
        root.SetActive(false);

        string report = ReadReport("Rig");
        StringAssert.DoesNotContain("0 (", ConstraintRow(report, "PositionConstraint", "Rig/Driven"));
    }

    // ----- Unity IConstraint components -------------------------------------------------------------

    [Test]
    public void ParentConstraint_AppearsInEdgeList_WithAxisMask_NoReadMiss()
    {
        var root = new GameObject("Rig");
        var driven = Child(root, "Driven");
        var src = Child(root, "Src");
        var pc = driven.AddComponent<ParentConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });
        pc.translationAxis = Axis.X | Axis.Z;
        pc.rotationAxis = Axis.X | Axis.Y | Axis.Z;

        string report = ReadReport("Rig");
        StringAssert.Contains("ParentConstraint", report);
        StringAssert.Contains("Rig/Src", report);
        // Unity per-type Axis-flag mask rendered, exact letters (VRC path would emit "pos*"/"—", never the
        // colon form): a partial mask spells its axes, an all-on group collapses to "*".
        StringAssert.Contains("pos:XZ", report);
        StringAssert.Contains("rot:*", report);
        // A Unity constraint routed through the VRC reflection helpers would resolve zero groups and emit
        // this note.
        StringAssert.DoesNotContain("could not read the affected-axis mask", report);
        StringAssert.DoesNotContain("TargetTransform indirection", report);
    }

    [Test]
    public void UnityConstraint_FeedbackLoop_TransfersToObservations()
    {
        var root = new GameObject("Rig");
        var driven = Child(root, "Driven");
        var src = Child(driven, "Inner");
        var pc = driven.AddComponent<PositionConstraint>();
        pc.AddSource(new ConstraintSource { sourceTransform = src.transform, weight = 1f });

        string report = ReadReport("Rig");
        StringAssert.Contains("feedback loop", report);
    }

    // ----- Tier-2 generic "Other components" census -------------------------------------------

    private class TierTwoProbe : MonoBehaviour
    {
        public UnityEngine.Object reference;
        public string label;
    }

    [Test]
    public void CustomMonoBehaviour_ObjectRefAndScalar_SurfaceInOtherCensus()
    {
        var root = new GameObject("Rig");
        var target = Child(root, "Target");
        var host = Child(root, "Host");
        var probe = host.AddComponent<TierTwoProbe>();
        probe.reference = target.transform; // a Component → the object-ref seam renders its hierarchy path
        probe.label = "peek-me";

        string report = ReadReport("Rig");
        StringAssert.Contains("Other components", report);
        StringAssert.Contains("TierTwoProbe", report);
        StringAssert.Contains("Rig/Host", report);
        StringAssert.Contains("reference", report);
        StringAssert.Contains("Rig/Target", report);
        StringAssert.Contains("peek-me", report);
        StringAssert.Contains("other=1", report);
    }

    [Test]
    public void DanglingObjectRef_RendersBroken_NotDropped()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Host");
        var probe = host.AddComponent<TierTwoProbe>();
        var clip = new AnimationClip { name = "doomed" };
        const string assetPath = "Assets/Agent/_rbs_dangling.anim";
        AssetDatabase.CreateAsset(clip, assetPath);
        probe.reference = clip;
        AssetDatabase.DeleteAsset(assetPath); // probe.reference is now a dangling (missing) ref

        string report = ReadReport("Rig");
        StringAssert.Contains("(broken: dangling reference)", report);
    }

    [Test]
    public void ModularAvatarMenuItem_ControlNameAndType_SurfaceInPeek()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "MenuHost");
        var mi = host.AddComponent<ModularAvatarMenuItem>();
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
        // Upper bound of the one-struct-level peek: parameter.name is a second struct level (Control →
        // parameter → name), which is AgentInspector's depth.
        StringAssert.DoesNotContain("MyDrivenParam", report);
    }

    // ----- Raycasts ------------------------------------------------------------------------------
    //
    // The tier-2 census cannot carry a ray: LayerMask has no case in the shallow peek's switch and a clean-null
    // object ref emits no line, so the mask and a null `resultTransform` would both vanish. This table renders
    // both as cells.
    //
    // No test here may assert a VRChat layer name: names come from the project, so the same mask reads
    // `10(PlayerLocal)` in a venue whose layer setup has run and `10` in one whose has not.

    private static VRCRaycast Ray(GameObject host, string parameter, Transform result)
    {
        var r = host.AddComponent<VRCRaycast>();
        r.Parameter = parameter;
        r.ResultTransform = result;
        return r;
    }

    // Slices to one section, because "(none)" is also how every empty section renders.
    private static string Section(string report, string heading)
    {
        int start = report.IndexOf(heading);
        if (start < 0) return "";
        int next = report.IndexOf("\n## ", start + heading.Length);
        return next < 0 ? report.Substring(start) : report.Substring(start, next - start);
    }

    [Test]
    public void Raycast_CustomLayerMask_RendersIndexAlwaysAndNameWhenTheProjectHasOne()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Origin");
        var r = Ray(host, "Ray", Child(host, "Hit").transform);
        r.RaycastCollisionMode = VRCRaycast.CollisionMode.HitCustomLayers;
        r.CustomCollisionLayers = (1 << 0) | (1 << 31);

        string rays = Section(ReadReport("Rig"), "## Raycasts");
        StringAssert.Contains("HitCustomLayers", rays);
        // Pins the name annotation on a layer Unity always names, ascending index order and the separator. A
        // prefix of the second entry stays venue-neutral: a venue naming layer 31 renders `31(Something)`.
        StringAssert.Contains("0(Default),31", rays);
    }

    [Test]
    public void Raycast_EmptyAndFullMasks_ReadAsWordsNotBitLists()
    {
        var root = new GameObject("Rig");
        var a = Child(root, "A");
        Ray(a, "A", Child(a, "HitA").transform).CustomCollisionLayers = 0;
        var b = Child(root, "B");
        Ray(b, "B", Child(b, "HitB").transform).CustomCollisionLayers = ~0;

        string rays = Section(ReadReport("Rig"), "## Raycasts");
        StringAssert.Contains("layers=(none)", rays);
        StringAssert.Contains("layers=everything", rays); // never a 32-entry cell
    }

    // The assertion pins the cell, not the row: a fresh VRCRaycast defaults to an all-off mask (measured), so
    // this row also renders `layers=(none)` and a bare Contains("(none)") stays green with an empty result cell.
    [Test]
    public void Raycast_NoResultTransform_RendersNoneRatherThanVanishing()
    {
        var root = new GameObject("Rig");
        Ray(Child(root, "Origin"), "Ray", null);

        string rays = Section(ReadReport("Rig"), "## Raycasts");
        StringAssert.Contains("Rig/Origin", rays);
        StringAssert.Contains("| (none) |", rays);
    }

    // The same delivery and clause assertions as ChainSubtree_CensusRowIsAccompaniedByItsLegend, for the same
    // reason.
    [Test]
    public void Raycast_TableIsAccompaniedByItsLegend()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Origin");
        Ray(host, "Ray", Child(host, "Hit").transform);

        Assert.AreEqual(ReportGimmick.RaycastLegend.TrimEnd('\n'),
                        LegendLine(ReadReport("Rig"), "_`layers`"));

        StringAssert.Contains("Layer NAMES come from the project's TagManager", ReportGimmick.RaycastLegend);
        StringAssert.Contains("not the post-build name", ReportGimmick.RaycastLegend);
    }

    [Test]
    public void Raycast_ParameterPrefix_RendersTheDerivedTriple()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Origin");
        Ray(host, "SelectiveAnimation/Ray", Child(host, "Hit").transform);

        string rays = Section(ReadReport("Rig"), "## Raycasts");
        StringAssert.Contains("`SelectiveAnimation/Ray` + _Hit/_Ratio/_Distance", rays);
    }

    // Two rays on one origin is a shipped idiom (a player ray and a world ray).
    [Test]
    public void Raycast_TwoOnOneHost_DisambiguatedByComponentOrdinal()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Origin");
        Ray(host, "Player", Child(host, "PlayerHit").transform);
        Ray(host, "World", Child(host, "WallHit").transform);

        string rays = Section(ReadReport("Rig"), "## Raycasts");
        StringAssert.Contains("[VRCRaycast#0]", rays);
        StringAssert.Contains("[VRCRaycast#1]", rays);
    }

    [Test]
    public void Raycast_DisabledComponent_ReadsNotLiveWithItsReason()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Origin");
        Ray(host, "Ray", Child(host, "Hit").transform).enabled = false;

        StringAssert.Contains("0 (enabled)", Section(ReadReport("Rig"), "## Raycasts"));
    }

    [Test]
    public void Raycast_CountedInHeader_AndNoLongerInTheTierTwoCensus()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Origin");
        Ray(host, "Player", Child(host, "PlayerHit").transform);
        Ray(host, "World", Child(host, "WallHit").transform);

        string report = ReadReport("Rig");
        StringAssert.Contains("raycasts=2", report);
        StringAssert.Contains("other=0", report);
        StringAssert.DoesNotContain("VRCRaycast", Section(report, "## Other components"));
    }

    [Test]
    public void OtherCount_ExcludesTierOneAndTransforms()
    {
        var root = new GameObject("Rig");
        Child(root, "A").AddComponent<TierTwoProbe>();
        Child(root, "B").AddComponent<ModularAvatarMenuItem>();
        Child(root, "C").AddComponent<PositionConstraint>();

        string report = ReadReport("Rig");
        // TierTwoProbe + ModularAvatarMenuItem; the tier-1 constraint and the four Transforms must not count.
        StringAssert.Contains("other=2", report);
    }

    // ----- Constraint source length -----------------------------------------------------------------
    //
    // A VRC constraint keeps 16 keyable source slots behind a `totalLength`, and the two runtimes disagree
    // about a slot filled past it: the editor solves every slot holding a transform, the client solves
    // exactly the first `totalLength`. Such a slot passes every play-mode check and does nothing in-game.
    // `Sources.Count` returns `totalLength`, so the constraints table cannot see the slot; this is an
    // observation with its own SerializedObject read.

    // Goes through SerializedObject because the public API cannot address a slot past the length.
    private static void WriteSources(VRCConstraintBase c, int totalLength, params Transform[] slots)
    {
        var so = new SerializedObject(c);
        for (int i = 0; i < slots.Length; i++)
        {
            so.FindProperty("Sources.source" + i + ".SourceTransform").objectReferenceValue = slots[i];
            so.FindProperty("Sources.source" + i + ".Weight").floatValue = 1f;
        }
        so.FindProperty("Sources.totalLength").intValue = totalLength;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static string OneOffender(System.Collections.Generic.List<string> lines)
    {
        var found = lines.FindAll(l => !l.StartsWith(CheckAvatar.DegradedPrefix)
                                    && !l.StartsWith(CheckAvatar.ScopePrefix));
        Assert.AreEqual(1, found.Count, "expected exactly one offender line, got:\n" + string.Join("\n", lines));
        return found[0];
    }

    [Test]
    public void ScanConstraintLengths_SlotPastLength_NamesTheSlotAndTheLength()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var other = Child(root, "Other");
        var con = host.AddComponent<VRCParentConstraint>();
        WriteSources(con, 1, src.transform, other.transform);   // slot1 filled, length says 1

        string line = OneOffender(ReportGimmick.ScanConstraintLengths(root));
        StringAssert.Contains("source1", line);
        StringAssert.Contains("Other", line);
        StringAssert.Contains("totalLength`=1", line);
        StringAssert.Contains("raise `Sources.totalLength`", line);
        StringAssert.Contains("clear the slot", line);
    }

    [Test]
    public void ScanConstraintLengths_PastLengthSlotRepeatsAnInLengthSource_StatesTheRepetitionAsAFact()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var con = host.AddComponent<VRCParentConstraint>();
        WriteSources(con, 1, src.transform, src.transform);     // slot1 repeats slot0

        string line = OneOffender(ReportGimmick.ScanConstraintLengths(root));
        StringAssert.Contains("already a source inside the length", line);
        StringAssert.DoesNotContain("residue", line);
        StringAssert.DoesNotContain("harmless", line);
    }

    [Test]
    public void ScanConstraintLengths_LengthCoversEveryFilledSlot_IsClean()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var other = Child(root, "Other");
        var con = host.AddComponent<VRCParentConstraint>();
        WriteSources(con, 2, src.transform, other.transform);   // both slots inside the length

        CollectionAssert.IsEmpty(ReportGimmick.ScanConstraintLengths(root));
    }

    [Test]
    public void ScanConstraintLengths_InactiveHost_StillScannedAndMarkedNotLive()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var other = Child(root, "Other");
        var con = host.AddComponent<VRCParentConstraint>();
        WriteSources(con, 1, src.transform, other.transform);
        host.SetActive(false);

        string line = OneOffender(ReportGimmick.ScanConstraintLengths(root));
        StringAssert.Contains("not-live", line);
    }

    [Test]
    public void ScanConstraintLengths_LengthCoversEveryKeyableSlot_ClaimsNoScopeBound()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var slots = new Transform[16];
        for (int i = 0; i < slots.Length; i++) slots[i] = Child(root, "S" + i).transform;
        var con = host.AddComponent<VRCParentConstraint>();
        WriteSources(con, 16, slots);

        CollectionAssert.IsEmpty(ReportGimmick.ScanConstraintLengths(root));
    }

    [Test]
    public void ScanConstraintLengths_NullRoot_DegradesRatherThanReadingClean()
    {
        var lines = ReportGimmick.ScanConstraintLengths(null);
        Assert.AreEqual(1, lines.Count);
        StringAssert.StartsWith(CheckAvatar.DegradedPrefix, lines[0]);
    }

    [Test]
    public void Run_ConstraintSourcePastLength_ReachesTheObservationsIndex()
    {
        GameObject host, src;
        var root = Rig(out host, out src);
        var other = Child(root, "Other");
        var con = host.AddComponent<VRCParentConstraint>();
        WriteSources(con, 1, src.transform, other.transform);

        string report = ReadReport("Rig");
        StringAssert.Contains("**constraint source past totalLength**", report);
    }
}
