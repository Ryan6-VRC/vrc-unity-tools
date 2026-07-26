using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;
using VRC.SDK3.Dynamics.PhysBone.Components;

// A vendor body-morph slider can switch between several physbones tuned per size range, all targeting one
// bone (docs/outfits.md). The walk is includeInactive, so they all land in the §5.2 table — and with no
// live state the table reads as N chains fighting over one bone.
//
// The observation states the MECHANICAL fact (physbones share one target) and renders live state; it does
// not gate on live state and does not name an intent. Gating would key it to scene statics, which the doc
// it routes to says are not authoritative on a driven property — a vendor shipping every variant enabled
// is still a variant set, and would have gone unnamed.
public class ReportGimmickSharedTargetTests
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
    public void SharedTarget_MixedLiveState_ObservedWithLiveMemberNamed()
    {
        var root = new GameObject("Rig");
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
        Variant(root, "PB_flat", bone.transform, false);
        Variant(root, "PB_medium", bone.transform, true);
        Variant(root, "PB_big", bone.transform, false);

        string summary = ReportGimmick.Report("Rig");
        string report = File.ReadAllText(summary.Substring(summary.IndexOf("log=") + 4).Trim());
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
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
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
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
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
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
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
        var host = new GameObject("Bone"); host.transform.SetParent(root.transform);
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
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
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
        var bone = new GameObject("Chain"); bone.transform.SetParent(root.transform);
        Variant(root, "PB_only", bone.transform, true);

        string summary = ReportGimmick.Report("Rig");
        StringAssert.Contains("observations=0", summary);
    }
}
