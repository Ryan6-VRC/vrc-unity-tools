using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;
using VRC.SDK3.Dynamics.PhysBone.Components;

// A partial take (keeping some of a mergeable's pieces, dropping the rest) leaves chains whose bones no
// surviving mesh skins and nothing else occupies. The §5.2 table listed those chains identically to running
// ones, so the digest could not distinguish 46 chains from the 19 still doing work.
//
// `chain subtree` is a CENSUS, not a verdict: three counts over one set. The tests below pin both polarities
// — that an idle chain reads all-zero, and that the load-bearing shapes which carry no skin weights (a rigid
// prop on the chain, a second component on the bone) read nonzero rather than joining them.
public class ReportGimmickChainSubtreeTests
{
    [SetUp]
    public void SetUp() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

    private static string ReadReport(string rootPath)
    {
        string summary = ReportGimmick.Report(rootPath);
        int i = summary.IndexOf("log=");
        return i >= 0 ? File.ReadAllText(summary.Substring(i + 4).Trim()) : summary;
    }

    private static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform);
        return go;
    }

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

    // The scope decision, and the least derivable thing here: the skinned-mesh sweep is scene-root-absolute,
    // not report-root-scoped. Aim the digest at an armature and its meshes are siblings — scoping to the
    // report root would print `skinned=0` for a fully skinned chain, the exact false zero the cell exists to
    // avoid.
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

    // The census is verdict-free, and the legend is what keeps a reader from building the verdict anyway.
    // Both halves are load-bearing: that a zero is not evidence, and that a nonzero is an upper bound.
    [Test]
    public void ChainSubtree_LegendStatesZeroIsNotAVerdictAndNonzeroIsAnUpperBound()
    {
        var root = new GameObject("Rig");
        Child(root, "Bone").AddComponent<VRCPhysBone>();

        string report = ReadReport("Rig");
        StringAssert.Contains("Reported, not judged.", report);
        StringAssert.Contains("All-zero is NOT a dead chain", report);
        StringAssert.Contains("name-merge onto a base bone reads `skinned=0`", report);
        StringAssert.Contains("nonzero is an upper bound", report);
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
}
