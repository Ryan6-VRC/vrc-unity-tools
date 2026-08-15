using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using Ryan6Vrc.AgentTools.Editor;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

// ReportGimmick proof obligations. One fixture per DOOR, not per repair wave: the four files this replaces
// each re-declared `ReadReport` and `SetUp => NewScene`, and each leaked the Snapshot markdown its calls
// wrote. Each wave keeps its own banner + narrative below; the plumbing is shared.
public class ReportGimmickTests
{
    [SetUp]
    public void SetUp() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

    // Every door call writes a durable Snapshot markdown (RunLogFormat.SnapshotDir), and nothing prunes that
    // directory — docs/unity-tools.md declares it DURABLE, the operator's own pile. A fixture that does not
    // name what it wrote leaks one file per test into it. Recorded, never globbed: a glob over `gimmick_*.md`
    // would also sweep snapshots the operator took by hand.
    private static readonly List<string> Artifacts = new List<string>();

    [OneTimeTearDown]
    public void DeleteWrittenSnapshots()
    {
        if (Artifacts.Count > 0) AssetDatabase.DeleteAssets(Artifacts.ToArray(), new List<string>());
        Artifacts.Clear();
    }

    // The door's one-line summary, with the Snapshot path it names recorded for teardown. Every call in this
    // fixture goes through here, including the ones that only read the summary — they write a file too.
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

    // ----- §5.2 chain-subtree census -----------------------------------------------------------------
    //
    // A partial take (keeping some of a mergeable's pieces, dropping the rest) leaves chains whose bones no
    // surviving mesh skins and nothing else occupies. The §5.2 table listed those chains identically to
    // running ones, so the digest could not distinguish 46 chains from the 19 still doing work.
    //
    // `chain subtree` is a CENSUS, not a verdict: three counts over one set. The tests below pin both
    // polarities — that an idle chain reads all-zero, and that the load-bearing shapes which carry no skin
    // weights (a rigid prop on the chain, a second component on the bone) read nonzero rather than joining
    // them.

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

    // One vertex per bone, each fully weighted to its own bone — enough for the weight walk to report every
    // listed bone as skinned. The legacy boneWeights setter feeds the same mesh data GetAllBoneWeights reads.
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

    // The motivating case: the mesh that skinned this chain went out with a dropped piece. Three zeros over a
    // non-trivial bone count is the whole signal — `bones` is what makes the two zeros mean something.
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

    // The false-zero this census must not produce. A chain whose payload is a RIGID renderer (charm, bell,
    // earring) carries no skin weights at all and is fully load-bearing; `hosting` is what separates it from
    // the orphan above, and a skin-weights-only census would have called the two identical.
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

    // The row's own physbone is not evidence that something else uses the bone, so a self-rooted lone chain
    // reads zero rather than counting itself.
    [Test]
    public void ChainSubtree_OwnPhysBoneIsNotCountedAsHosting()
    {
        var root = new GameObject("Rig");
        var bone = Child(root, "Bone");
        bone.AddComponent<VRCPhysBone>(); // self-rooted: rootTransform left null

        string report = ReadReport("Rig");
        StringAssert.Contains("bones=1 skinned=0 hosting=0", report);
    }

    // Another chain's physbone on the same bone IS something else using it — the exclusion is identity-scoped
    // to the row, not blanket-by-type.
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

    // The scope decision, lower bound: the sweep reaches past the report root. Aim the digest at an armature
    // and its meshes are siblings — report-root scoping would print `skinned=0` for a fully skinned chain,
    // the exact false zero the cell exists to avoid. This rig carries no descriptor, so it also pins the
    // outermost-ancestor fallback.
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

    // The scope decision, upper bound, over one rig aimed at two levels — the same 15-line fixture served
    // both cases as two tests.
    //
    // `Staging/AvatarA`: a neighbour avatar's renderer still bound to THIS avatar's bones is what duplicating
    // an avatar and leaving a renderer pointed at the source rig produces. Under container scope it would
    // inflate the count, and every co-hosted avatar's whole weight array would be walked to get there.
    //
    // `Staging`: the one invocation where no descriptor is an ancestor of the report root, so the fallback is
    // live. Resolving the scope once from the report root landed on the container here and swept every
    // co-hosted avatar, letting AvatarB's stale renderer inflate AvatarA's chain to skinned=2 while the
    // legend promised the enclosing avatar. Scoping from the ROW's own physbone finds AvatarA's descriptor
    // whichever level the report is aimed at — so one expected count covers both aims, and both directions:
    // the local mesh's bone is counted, the neighbour's is not.
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

    // rootTransform indirection: the set starts at the target, so the host carrying the component is not in
    // it. Counting from the host instead would silently report the wrong subtree for every indirected chain.
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

    // The census is verdict-free, and the legend is what keeps a reader from building the verdict anyway: a
    // zero is not evidence, and membership is not motion.
    //
    // The emit is one branch (`bones.Length > 0`) shared with the table itself, so the companion no-physbones
    // test pins the other side of it.
    //
    // Two assertions, because they catch different failures and NEITHER subsumes the other:
    //
    // (a) The emitted line equals the canon constant. This proves the legend reaches the report intact and
    //     untruncated — but it is deliberately blind to the constant's CONTENT: both sides are the same string,
    //     so deleting a clause from the constant keeps this green. It is a delivery check, not a content check.
    // (b) Each load-bearing clause is present in the CONSTANT. This is what survives a reword (the wording may
    //     change freely) while failing a deletion, which (a) cannot do. `ReportGimmick`'s own docstring states
    //     both polarities are load-bearing: drop "all-zero is not a dead chain" and a reader deletes a
    //     name-merged bone reading `skinned=0`; drop the membership-vs-motion clause and they read the count
    //     as which bones the chain MOVES, which `multiChildType` and `endpointPosition` still decide.
    //
    // The former "nonzero is an upper bound" clause is deliberately gone and its absence is asserted: the
    // counts are the SDK's own chain membership now, exclusions already applied, so pinning that caveat would
    // pin one the code no longer earns. The membership-vs-motion clause is the part of it that stayed true.
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

    // The exclusions the old hierarchy walk could not see, now applied because the set comes from the
    // component itself. `ignoreTransforms` prunes the listed transform AND its descendants (the field's own
    // Inspector tooltip says so), which is exactly why a hand-rolled subtraction was rejected: the descendant
    // half is what a naive "remove the listed transforms" gets wrong, and it gets it wrong in the direction
    // that inflates the count back toward the hierarchy walk.
    //
    // Hierarchy subtree here is 4 (ChainRoot, Keep, Dropped, DroppedChild). The SDK's chain is 2. Asserting
    // the absence of `bones=4` is what makes this a real discrimination rather than a coincidence — the old
    // implementation would have printed it.
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

    // `endpointPosition` is the one input that could put a transform-less member in the chain, and the cell
    // skips such a member rather than counting it. Asserted rather than reasoned about: the SDK's `Bone` is a
    // struct, so a transform-less entry is a null FIELD and not a null element, and whether the endpoint
    // produces one at all is a property of the SDK rather than of this code. Measured here it does not — the
    // count is unchanged and nothing throws — which is what makes the skip a guard against a destroyed
    // transform rather than against the endpoint.
    [Test]
    public void ChainSubtree_endpointPosition_changesNoCountAndDoesNotThrow()
    {
        var root = new GameObject("Rig");
        var chain = Chain(root, "Tail", 3);
        var pb = PhysBone(Child(root, "PB_tail"), chain);
        pb.endpointPosition = new Vector3(0f, 0.05f, 0f);

        StringAssert.Contains("bones=3", ReadReport("Rig"));
    }

    // The legend paragraph, isolated so the comparison is against the legend and nothing else: its tokens
    // (`skinned=0`) also occur in the census CELL, so a whole-document match would pass with it deleted.
    // Fails loud rather than returning null — an absent legend is the branch defect, not a null-deref.
    private static string LegendLine(string report) => LegendLine(report, "_`chain subtree`");

    private static string LegendLine(string report, string startsWith)
    {
        foreach (var line in report.Split('\n'))
            if (line.StartsWith(startsWith)) return line;
        Assert.Fail("no legend line starting `" + startsWith + "`:\n" + report);
        return null;
    }

    // No physbones ⇒ no table, so no legend and no census — the _(none)_ branch is untouched.
    [Test]
    public void ChainSubtree_NoPhysBones_NoCensusOrLegend()
    {
        var root = new GameObject("Rig");
        Child(root, "Bone");

        string report = ReadReport("Rig");
        StringAssert.DoesNotContain("chain subtree", report);
        // NOT "bones=" — the header's own `physbones=0` count contains that substring. Assert on the census
        // cell's own shape, which nothing else in the report can produce.
        StringAssert.DoesNotContain("skinned=", report);
    }

    // ----- §5.2 shared physbone target (the per-range variant stack) ---------------------------------
    //
    // A vendor body-morph slider can switch between several physbones tuned per size range, all targeting one
    // bone (docs/outfits.md). The walk is includeInactive, so they all land in the §5.2 table — and with no
    // live state the table reads as N chains fighting over one bone.
    //
    // The observation states the MECHANICAL fact (physbones share one target) and renders live state; it does
    // not gate on live state and does not name an intent. Gating would key it to scene statics, which the doc
    // it routes to says are not authoritative on a driven property — a vendor shipping every variant enabled
    // is still a variant set, and would have gone unnamed.

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
        // Naming the live member is what chains into the next step — a count alone does not.
        StringAssert.Contains("live: `Rig/PB_medium`", report);
        StringAssert.Contains("docs/outfits.md", report);
        StringAssert.Contains("observations=1", summary);
    }

    // The case the dropped all-live gate used to hide. A vendor may ship every variant enabled because the
    // WD-ON layer overwrites them at frame 1; that is still a shared target and must be named.
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

    // Liveness is relative to the REPORT ROOT. Absolute activeInHierarchy would call every member not-live
    // the moment the avatar is parked inactive — ordinary workflow — inverting the one signal that matters.
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

    // A self-rooted pair on one GameObject shares a target and is invisible to a rootTransform-only census.
    // Its transform path is ambiguous, so the live member is named by component ordinal — otherwise the
    // report cannot say which of the two is live, which is the whole point of naming it.
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

    // The component's own enable flag and its object's active state are independently sufficient to stop a
    // physbone, and vendors in the corpus use each as the discriminator — so the table carries both. The
    // table's `active` stays the raw absolute substrate fact; only the observation is root-relative.
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

    // ----- §5.3 constraint live cell ----------------------------------------------------------------
    //
    // Each constraint family carries a second enable flag beyond the Behaviour's own — VRC `IsActive`,
    // Unity `constraintActive` — so an inert constraint used to render identically to a running one.
    // The live cell names WHICH flag is down, because that is what the reader acts on.

    private static GameObject Rig(out GameObject host, out GameObject src)
    {
        var root = new GameObject("Rig");
        host = Child(root, "Driven");
        src = Child(root, "Src");
        return root;
    }

    // One constraints-table row, addressed by its type + constrained transform. The live-cell negatives must
    // be scoped to it: `0 (` is a three-character fragment, and the physbone table's own `immobile` cell
    // renders `0 (All)` — so a whole-document DoesNotContain held only while a fixture had no physbones.
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

    // Liveness is relative to the report root, matching the physbone path: parking the whole rig must not
    // relabel every constraint inert.
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

    // ----- Unity IConstraint widening ---------------------------------------------------------------
    //
    // Unity IConstraint components must land in the constraint edge-list with their per-type Axis-flag mask
    // and NO false READ-MISS note, and the geometric feedback-loop observation must transfer to them —
    // without routing them through the VRC-only reflection helpers. VRC-idiom regression (world anchor /
    // hold / TargetTransform notes) is covered by the coordinator's live-corpus run, not fabricated here
    // (headless VRC-constraint fixtures are awkward and low-value).

    [Test]
    public void ParentConstraint_AppearsInEdgeList_WithAxisMask_NoReadMiss()
    {
        var root = new GameObject("Rig");
        var driven = Child(root, "Driven");
        var src = Child(root, "Src");
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
        var driven = Child(root, "Driven");
        // Source is a strict descendant of the driven host — the feedback-loop idiom.
        var src = Child(driven, "Inner");
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
        var target = Child(root, "Target");
        var host = Child(root, "Host");
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

    // Control is one struct level below the component; its top-level scalars (name, type) surface in the
    // shallow peek, and `parameter.name` is the documented SECOND-level boundary that must not. The same
    // one-level branch carries ModularAvatarMergeArmature's `mergeTarget.referencePath`, which had its own
    // test asserting nothing this one does not.
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
        // Upper bound of the one-struct-level peek: parameter.name is a SECOND struct level (Control →
        // parameter → name) and must NOT surface in-tool — it's AgentInspector's depth (design decision A).
        StringAssert.DoesNotContain("MyDrivenParam", report);
    }

    // ----- Raycasts (F2) -------------------------------------------------------------------------
    //
    // Before this table a VRCRaycast fell to the tier-2 census, which reads `collisionMode = Hit Custom
    // Layers` and then prints nothing for the mask — LayerMask has no case in the shallow peek's switch, is
    // neither an array nor a struct with visible children, and so falls through silently. That mask is the
    // whole discriminator of a player-masked ray, and the same peek drops a null `resultTransform` entirely
    // (a clean-null object ref emits no line at all), which is exactly the configuration that fails silently
    // in Unity. Both are facts here, in cells; neither is a verdict.
    //
    // The layer assertions are deliberately split. `0(Default)` pins the NAME annotation on a layer Unity
    // reserves and never leaves blank; bit 31 pins only that the INDEX renders, which holds whether or not
    // the venue's TagManager names it. Nothing here may assert a VRChat layer name — those come from the
    // project, so the same mask reads `10(PlayerLocal)` in a venue whose layer setup has run and `10` in one
    // whose has not, and a test pinning the name would encode the author's venue.

    private static VRCRaycast Ray(GameObject host, string parameter, Transform result)
    {
        var r = host.AddComponent<VRCRaycast>();
        r.Parameter = parameter;
        r.ResultTransform = result;
        return r;
    }

    // The report sliced to one section, so an assertion cannot be satisfied by a coincidence elsewhere in the
    // digest — "(none)" in particular is also how every empty section renders.
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
        // One assertion pinning three things at once: the name annotation on a layer Unity always names,
        // ascending index order, and the join separator. It stays venue-neutral by matching a PREFIX of the
        // second entry — a venue that does name layer 31 renders `31(Something)` and still satisfies it,
        // while a bare `Contains("31")` would be satisfied by any digit that drifts into the row.
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

    // The silent-death configuration: no result transform, so the component has nothing to write. The tier-2
    // census rendered this as an ABSENT line, indistinguishable from a field that does not exist.
    //
    // The assertion pins the CELL, not the row. A fresh VRCRaycast defaults to an all-off mask (measured), so
    // this very row also renders `layers=(none)` in its collision cell — a bare Contains("(none)") passes on
    // that alone and stays green while the result column regresses to empty. Sectioning the report is not
    // enough when the coincidence lives inside the section; the delimiters are what discriminate.
    [Test]
    public void Raycast_NoResultTransform_RendersNoneRatherThanVanishing()
    {
        var root = new GameObject("Rig");
        Ray(Child(root, "Origin"), "Ray", null);

        string rays = Section(ReadReport("Rig"), "## Raycasts");
        StringAssert.Contains("Rig/Origin", rays);
        StringAssert.Contains("| (none) |", rays);
    }

    // The legend makes the same promise ChainSubtreeLegend does, so it earns the same pair of assertions —
    // (a) delivered intact and untruncated, blind to content; (b) the load-bearing clauses present in the
    // CONSTANT, which survives a reword but fails a deletion. Without (b) a prose pass can drop the layer
    // sentence — the one the constant's own doc comment calls load-bearing — against a green suite.
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

    // The prefix is concatenated locally, the way the component does it — not traced into an animator, and
    // not the post-build name, which VRCFury and MA rewrite.
    [Test]
    public void Raycast_ParameterPrefix_RendersTheDerivedTriple()
    {
        var root = new GameObject("Rig");
        var host = Child(root, "Origin");
        Ray(host, "SelectiveAnimation/Ray", Child(host, "Hit").transform);

        string rays = Section(ReadReport("Rig"), "## Raycasts");
        StringAssert.Contains("`SelectiveAnimation/Ray` + _Hit/_Ratio/_Distance", rays);
    }

    // Two rays on one origin is the shipped idiom (a player ray and a world ray), so the path alone cannot
    // identify a row — the same ambiguity HostHandle solves for physbones sharing a GameObject.
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

    // Tier-1 promotion is only honest if the table replaces the census rows rather than doubling them: the
    // count line gains raycasts=N, and `other` must not still be counting the same components.
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
        // A tier-1 Unity constraint AND four Transforms are present; neither must inflate other=N.
        Child(root, "C").AddComponent<PositionConstraint>();

        string report = ReadReport("Rig");
        // other counts one row per single-visit component, so a tier-1 constraint (rendered in the
        // constraints TABLE, not the census) and the four Transforms cannot inflate it: probe + menuitem = 2.
        StringAssert.Contains("other=2", report);
    }
}
