using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// These edit-mode tests cover the TIER-AGNOSTIC and CONSERVATIVE-tier behavior of BuildPlan, using synthetic
// MonoBehaviour types because they resolve to the conservative tier (VrcComponentTable.Lookup == null) and
// nothing the SDK ships does:
//   - type-name resolution surfacing (unresolved list)
//   - conservative host-present → planned copy; host-absent → flagged-missing
//   - count parity per (host, type), including the full-re-run boundary
//   - rollup counts and flaggedMissingHosts formatting (ForceKey shape)
// Deep-tier parity (recreate-vs-flag discriminator, dedup lever, hard/soft criticality) needs a real vendor
// avatar and is exercised live via execute_code in the open editor (the operator's gate).

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
        Child(ours, "A");   // counterpart present, no component yet (M=0)

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
        var ours = new GameObject("V");   // no "A" child → host absent

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

    // Count parity per (host, type): N vendor, M already present → max(0, N-M) copies and the remainder
    // PresentSkip, always N steps. M == N is the full-re-run boundary (nothing copied, everything skipped) —
    // the case that decides whether a second run of a transplant is a no-op or a duplication.
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
        Assert.AreEqual(2, plan.InPlace);   // independent (host,type) groups, both M=0

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Duplicate_named_sibling_hosts_are_planned_independently_not_collapsed()
    {
        // REGRESSION GUARD for the index-aware parity key. Two same-named sibling hosts ("Col") under one
        // parent "P"; vendor has one CcProbeA on each. The dest M is made ASYMMETRIC on the NON-FIRST
        // sibling: Col[0] has M=0, Col[1] has M=2. That asymmetry is what makes the test discriminate
        // old vs new:
        //
        //   NEW (index-aware key, parityHost = resolved dest Transform): Col[0] m=0 → InPlace;
        //        Col[1] m=2, consumed 0 < 2 → PresentSkip  ⇒  InPlace=1, PresentSkip=1.
        //   OLD (name-path key "P/Col", M via Transform.Find → first Col = Col[0] = 0 for BOTH vendor
        //        slots sharing one counter): both InPlace  ⇒  InPlace=2, PresentSkip=0.
        //
        // old=2/0, new=1/1 — they diverge, so this fixture actually fails against the bug. (The symmetric
        // "both M=0" and the "Col[0] M=1 / Col[1] M=0" variants both coincide old==new and guard nothing;
        // the pre-populated sibling MUST be the non-first one.)
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

    // A2: a non-injective rename map is rejected at the Run door, before any mutation (nothing minted).
    [Test]
    public void Run_rejects_non_injective_rename_map_before_mutation()
    {
        var vendor = new GameObject("V");
        var va = Child(vendor, "A"); va.AddComponent<CopyComponentsPlanTests_Ns.CcProbeA>();
        var ours = new GameObject("V");
        int before = ours.transform.childCount;

        var badMap = new Dictionary<string, string> { { "A", "X" }, { "B", "X" } };   // two keys → one value
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
