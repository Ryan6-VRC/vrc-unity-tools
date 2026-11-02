using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// Synthetic MonoBehaviour types resolve to the conservative tier (VrcComponentTable.Lookup == null). Deep-tier
// parity needs a real vendor avatar and is exercised live via execute_code in the open editor.

namespace CopyComponentsPlanTests_Ns
{
    public class CcProbeA : MonoBehaviour { }
    public class CcProbeB : MonoBehaviour { }
}

public class CopyComponentsPlanTests
{
    static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    [Test]
    public void NullArgs_yield_empty_plan()
    {
        var plan = CopyComponents.BuildPlan(null, null, new[] { "CcProbeA" });
        Assert.AreEqual(0, plan.steps.Count);
    }

    [Test]
    public void Unresolved_type_name_is_surfaced()
    {
        var vendor = new GameObject("V");
        var ours   = new GameObject("O");
        var plan = CopyComponents.BuildPlan(ours, vendor, new[] { "NoSuchTypeXyz" });
        Assert.AreEqual(1, plan.unresolvedTypeNames.Count);
        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Conservative_host_present_plans_inplace_copy()
    {
        var vendor = new GameObject("V");
        var va = Child(vendor, "A"); va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        var ours = new GameObject("V");
        Child(ours, "A");

        var plan = CopyComponents.BuildPlan(ours, vendor, new[] { "CcProbeA" });
        Assert.AreEqual(1, plan.steps.Count);
        Assert.AreEqual(CopyAction.InPlace, plan.steps[0].action);
        Assert.IsFalse(plan.steps[0].isDeepTier);
        Assert.AreEqual("A", plan.steps[0].destHostPath);
        Assert.AreEqual(1, plan.Conservative);

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Conservative_host_absent_is_flagged_missing_never_scaffold()
    {
        var vendor = new GameObject("V");
        var va = Child(vendor, "A"); va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        var ours = new GameObject("V");

        var plan = CopyComponents.BuildPlan(ours, vendor, new[] { "CcProbeA" });
        Assert.AreEqual(1, plan.steps.Count);
        Assert.AreEqual(CopyAction.FlaggedMissing, plan.steps[0].action);
        Assert.AreEqual(0, plan.Scaffold);
        Assert.AreEqual(1, plan.FlaggedMissing);
        Assert.AreEqual(1, plan.flaggedMissingHosts.Count);
        // Force key must stay EXACTLY "path :: Type" — classification rides in the note, never the key.
        Assert.AreEqual("A :: CcProbeA", plan.flaggedMissingHosts[0]);
        Assert.IsFalse(plan.steps[0].isDeepTier, "conservative host must not be deep-classified");
        Assert.IsFalse(plan.steps[0].isBone, "conservative step leaves isBone at its default");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    // N vendor, M already present. M == N is the full-re-run boundary: the case that decides whether a second
    // run of a transplant is a no-op or a duplication.
    [TestCase(3, 1, 2, 1)]
    [TestCase(2, 2, 0, 2)]
    public void CountParity_plans_the_shortfall_and_skips_the_remainder(
        int vendorCount, int ourCount, int expectedInPlace, int expectedSkip)
    {
        var vendor = new GameObject("V");
        var va = Child(vendor, "A");
        for (int i = 0; i < vendorCount; i++) va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();

        var ours = new GameObject("V");
        var oa = Child(ours, "A");
        for (int i = 0; i < ourCount; i++) oa.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();

        var plan = CopyComponents.BuildPlan(ours, vendor, new[] { "CcProbeA" });
        Assert.AreEqual(vendorCount, plan.steps.Count, "one step per vendor component");
        Assert.AreEqual(expectedInPlace, plan.InPlace);
        Assert.AreEqual(expectedSkip, plan.PresentSkip);

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Plan_is_pure_no_components_added_to_dest()
    {
        var vendor = new GameObject("V");
        var va = Child(vendor, "A"); va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        var ours = new GameObject("V");
        var oa = Child(ours, "A");

        int before = oa.GetComponents<Component>().Length;
        CopyComponents.BuildPlan(ours, vendor, new[] { "CcProbeA" });
        int after = oa.GetComponents<Component>().Length;
        Assert.AreEqual(before, after, "BuildPlan must not mutate the destination");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Two_types_counted_independently_per_host()
    {
        var vendor = new GameObject("V");
        var va = Child(vendor, "A");
        va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeB>();

        var ours = new GameObject("V");
        Child(ours, "A");

        var plan = CopyComponents.BuildPlan(ours, vendor, new[] { "CcProbeA", "CcProbeB" });
        Assert.AreEqual(2, plan.steps.Count);
        Assert.AreEqual(2, plan.InPlace);

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Duplicate_named_sibling_hosts_are_planned_independently_not_collapsed()
    {
        // The pre-populated dest sibling must be the non-first one. A parity key on the name path "P/Col" reads
        // M through Transform.Find, which lands on Col[0] for both vendor slots, and only this asymmetry makes it
        // plan differently (InPlace=2) from the index-aware key (1/1). Both-M=0 and Col[0]-M=1 plan the same
        // under either key and guard nothing.
        var vendor = new GameObject("V");
        var vp = Child(vendor, "P");
        var vc0 = Child(vp, "Col"); vc0.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        var vc1 = Child(vp, "Col"); vc1.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();

        var ours = new GameObject("V");
        var op = Child(ours, "P");
        Child(op, "Col");                                              // Col[0]: M=0
        var oc1 = Child(op, "Col");                                    // Col[1]: M=2
        oc1.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        oc1.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();

        var plan = CopyComponents.BuildPlan(ours, vendor, new[] { "CcProbeA" });
        Assert.AreEqual(2, plan.steps.Count, "both duplicate-named sibling hosts must be planned");
        Assert.AreEqual(1, plan.InPlace, "Col[0] (M=0) is its own slot → one InPlace copy");
        Assert.AreEqual(1, plan.PresentSkip, "Col[1] (M=2) is its own slot → PresentSkip; not collapsed into Col[0]'s counter");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Run_rejects_non_injective_rename_map_before_mutation()
    {
        var vendor = new GameObject("V");
        var va = Child(vendor, "A"); va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        var ours = new GameObject("V");
        int before = ours.transform.childCount;

        var badMap = new Dictionary<string, string> { { "A", "X" }, { "B", "X" } };
        // The refusal's FAIL summary is emitted via Debug.LogError — expected, or the runner flags an
        // unhandled error log.
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var summary = CopyComponents.Run(ours, vendor, new[] { "CcProbeA" }, null, badMap, whatIf: false);
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("non-injective", summary);
        Assert.AreEqual(before, ours.transform.childCount, "nothing minted on a non-injective-map FAIL");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }
}
