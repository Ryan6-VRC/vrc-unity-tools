using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;
using VRC.SDK3.Dynamics.PhysBone.Components;

// A body-morph slider can switch between several physbones tuned per size range, all targeting one bone
// (docs/outfits.md). The walk is includeInactive, so they all land in the §5.2 table — and without live
// state the table reads as N chains fighting over one bone. These cover the two halves: the per-row state,
// and the Observations line that names the idiom. Mixed live-state is the whole predicate — an all-live
// pair on one bone (a chain plus its limiter) is a different thing and must stay unclaimed.
public class ReportGimmickVariantStackTests
{
    [SetUp]
    public void SetUp() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

    private static string ReadReport(string rootPath)
    {
        string summary = ReportGimmick.Report(rootPath);
        int i = summary.IndexOf("log=");
        return i >= 0 ? File.ReadAllText(summary.Substring(i + 4).Trim()) : summary;
    }

    private static VRCPhysBone Variant(GameObject parent, string name, Transform target, bool live)
    {
        var host = new GameObject(name);
        host.transform.SetParent(parent.transform);
        var pb = host.AddComponent<VRCPhysBone>();
        pb.rootTransform = target;
        host.SetActive(live);
        return pb;
    }

    [Test]
    public void VariantStack_MixedLiveState_ObservedWithLiveMemberNamed()
    {
        var root = new GameObject("Rig");
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
        Variant(root, "PB_flat", bone.transform, false);
        Variant(root, "PB_medium", bone.transform, true);
        Variant(root, "PB_big", bone.transform, false);

        string summary = ReportGimmick.Report("Rig");
        string report = File.ReadAllText(summary.Substring(summary.IndexOf("log=") + 4).Trim());
        StringAssert.Contains("**dynamics variant stack**", report);
        StringAssert.Contains("Rig/Chain", report);
        StringAssert.Contains("3 physbones", report);
        // Naming the live member is what chains into the next step — a count alone does not.
        StringAssert.Contains("live: `Rig/PB_medium`", report);
        StringAssert.Contains("docs/outfits.md", report);
        StringAssert.Contains("observations=1", summary);
    }

    [Test]
    public void VariantStack_AllMembersLive_NotClaimed()
    {
        var root = new GameObject("Rig");
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
        Variant(root, "PB_chain", bone.transform, true);
        Variant(root, "PB_limit", bone.transform, true);

        string report = ReadReport("Rig");
        StringAssert.DoesNotContain("dynamics variant stack", report);
    }

    [Test]
    public void VariantStack_LowBandWithNoLiveMember_ReportsNone()
    {
        var root = new GameObject("Rig");
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
        Variant(root, "PB_a", bone.transform, false);
        Variant(root, "PB_b", bone.transform, false);

        string report = ReadReport("Rig");
        StringAssert.Contains("**dynamics variant stack**", report);
        StringAssert.Contains("live: none", report);
    }

    // A self-rooted pair sharing one GameObject targets the same bone and is invisible to a
    // rootTransform-only census — the grouping key must be the effective target, not the declared field.
    [Test]
    public void VariantStack_SelfRootedPairOnOneObject_IsGrouped()
    {
        var root = new GameObject("Rig");
        var host = new GameObject("Bone"); host.transform.SetParent(root.transform);
        host.AddComponent<VRCPhysBone>();
        host.AddComponent<VRCPhysBone>().enabled = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("**dynamics variant stack**", report);
        StringAssert.Contains("2 physbones", report);
    }

    // The component's own enable flag and its object's active state are independently sufficient to stop a
    // physbone, and vendors in the corpus use both as the discriminator — so the table carries each.
    [Test]
    public void PhysBoneTable_CarriesEnabledAndActive()
    {
        var root = new GameObject("Rig");
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
        Variant(root, "PB_on", bone.transform, true);
        Variant(root, "PB_objectOff", bone.transform, false);
        Variant(root, "PB_componentOff", bone.transform, true).enabled = false;

        string report = ReadReport("Rig");
        StringAssert.Contains("enabled=1 active=1", report);
        StringAssert.Contains("enabled=1 active=0", report);
        StringAssert.Contains("enabled=0 active=1", report);
    }
}
