using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// GraftHierarchy copies every component type wholesale and its session-map remap is type-blind, so a plain
// MonoBehaviour carrying an object ref stands in for any authoring-unit component.
namespace GraftHierarchyTests_Ns
{
    public class GhProbe : MonoBehaviour
    {
        public GameObject linkedGo;
    }
}

public class GraftHierarchyTests
{
    static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    [Test]
    public void Graft_scaffolds_full_subtree_with_verbatim_local_trs()
    {
        var vendor = new GameObject("V");
        var menu = Child(vendor, "Menu");
        var tail = Child(menu, "Tail");
        tail.transform.localPosition = new Vector3(1, 2, 3);
        tail.transform.localScale    = new Vector3(2, 2, 2);
        var leaf = Child(tail, "Leaf");
        leaf.transform.localPosition = new Vector3(0, 0.5f, 0);

        var ours = new GameObject("V");

        var summary = GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, whatIf: false);
        StringAssert.Contains("=> PASS", summary);

        var dMenu = ours.transform.Find("Menu");
        Assert.IsNotNull(dMenu, "Menu scaffolded");
        var dTail = dMenu.Find("Tail");
        Assert.IsNotNull(dTail, "Tail scaffolded");
        Assert.IsNotNull(dTail.Find("Leaf"), "Leaf scaffolded");
        Assert.AreEqual(new Vector3(1, 2, 3), dTail.localPosition, "verbatim vendor local position");
        Assert.AreEqual(new Vector3(2, 2, 2), dTail.localScale,    "verbatim vendor local scale");
        Assert.AreEqual(new Vector3(0, 0.5f, 0), dTail.Find("Leaf").localPosition);

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Graft_copies_all_components_and_rebinds_internal_ref_via_session_map()
    {
        var vendor = new GameObject("V");
        var menu = Child(vendor, "Menu");
        var tail = Child(menu, "Tail");
        var leaf = Child(tail, "Leaf");
        var probe = tail.AddComponent<GraftHierarchyTests_Ns.GhProbe>();
        probe.linkedGo = leaf;

        var ours = new GameObject("V");

        var summary = GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, whatIf: false);
        StringAssert.Contains("=> PASS", summary);

        var dTail = ours.transform.Find("Menu/Tail");
        var dLeaf = ours.transform.Find("Menu/Tail/Leaf");
        var dProbe = dTail.GetComponent<GraftHierarchyTests_Ns.GhProbe>();
        Assert.IsNotNull(dProbe, "probe component copied onto dest Tail");
        Assert.IsNotNull(dProbe.linkedGo, "internal ref not nulled");
        Assert.AreSame(dLeaf.gameObject, dProbe.linkedGo, "internal ref rebound to DEST Leaf via session map");
        Assert.AreNotSame(leaf, dProbe.linkedGo, "internal ref no longer points into the vendor source");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    // preSeedMenu: the destination already holds the subtree root, so the first run reuses it by path.
    [TestCase(false)]
    [TestCase(true)]
    public void Graft_is_idempotent_no_duplicate_gos_or_components(bool preSeedMenu)
    {
        var vendor = new GameObject("V");
        var menu = Child(vendor, "Menu");
        var tail = Child(menu, "Tail");
        tail.AddComponent<GraftHierarchyTests_Ns.GhProbe>();

        var ours = new GameObject("V");
        if (preSeedMenu) Child(ours, "Menu");

        GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, whatIf: false);
        var summary2 = GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, whatIf: false);
        StringAssert.Contains("=> PASS", summary2);

        int menuCount = 0;
        for (int i = 0; i < ours.transform.childCount; i++)
            if (ours.transform.GetChild(i).name == "Menu") menuCount++;
        Assert.AreEqual(1, menuCount, "Menu not duplicated");

        var dMenu = ours.transform.Find("Menu");
        int tailCount = 0;
        for (int i = 0; i < dMenu.childCount; i++)
            if (dMenu.GetChild(i).name == "Tail") tailCount++;
        Assert.AreEqual(1, tailCount, "Tail not duplicated");

        Assert.IsNotNull(ours.transform.Find("Menu/Tail"), "Tail scaffolded under the (possibly reused) Menu");
        var probes = ours.transform.Find("Menu/Tail").GetComponents<GraftHierarchyTests_Ns.GhProbe>();
        Assert.AreEqual(1, probes.Length, "probe not duplicated (count parity)");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Graft_fails_loud_on_unresolved_subtree_root()
    {
        var vendor = new GameObject("V");
        Child(vendor, "Menu");
        var ours = new GameObject("V");

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var summary = GraftHierarchy.Run(ours, vendor, new[] { "NoSuchSubtree" }, whatIf: false);
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("NoSuchSubtree", summary);

        Assert.IsNull(ours.transform.Find("NoSuchSubtree"));

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void WhatIf_previews_without_mutating()
    {
        var vendor = new GameObject("V");
        var menu = Child(vendor, "Menu");
        Child(menu, "Tail").AddComponent<GraftHierarchyTests_Ns.GhProbe>();
        var ours = new GameObject("V");

        var summary = GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, whatIf: true);
        StringAssert.Contains("(whatIf)", summary);
        StringAssert.Contains("=> PASS", summary);
        Assert.IsNull(ours.transform.Find("Menu"), "whatIf must not scaffold");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Graft_rename_rebinds_ref_crossing_renamed_armature_root()
    {
        var vendor = new GameObject("V");
        var vArm = Child(vendor, "Armature");
        var vHips = Child(vArm, "Hips");
        var vMenu = Child(vendor, "Menu");
        var vTail = Child(vMenu, "Tail");
        var probe = vTail.AddComponent<GraftHierarchyTests_Ns.GhProbe>();
        probe.linkedGo = vHips;

        var ours = new GameObject("V");
        var oArm = Child(ours, "Armature.1");
        Child(oArm, "Hips");

        var map = new Dictionary<string, string> { { "Armature", "Armature.1" } };
        var summary = GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, map, whatIf: false);
        StringAssert.Contains("=> PASS", summary);

        var dProbe = ours.transform.Find("Menu/Tail").GetComponent<GraftHierarchyTests_Ns.GhProbe>();
        Assert.IsNotNull(dProbe.linkedGo, "cross-armature ref not nulled under the rename");
        Assert.AreSame(ours.transform.Find("Armature.1/Hips").gameObject, dProbe.linkedGo,
            "rebound to the owned renamed armature's Hips");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    // Twin of the rename test above: the map is the only thing that changes the outcome.
    [Test]
    public void Graft_null_map_leaves_cross_armature_ref_nulled()
    {
        var vendor = new GameObject("V");
        var vArm = Child(vendor, "Armature");
        var vHips = Child(vArm, "Hips");
        var vMenu = Child(vendor, "Menu");
        var vTail = Child(vMenu, "Tail");
        vTail.AddComponent<GraftHierarchyTests_Ns.GhProbe>().linkedGo = vHips;

        var ours = new GameObject("V");
        var oArm = Child(ours, "Armature.1");
        Child(oArm, "Hips");

        var summary = GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, null, whatIf: false);
        StringAssert.Contains("=> PASS", summary);   // a nulled under-reach ref is reported, not a FAIL

        var dProbe = ours.transform.Find("Menu/Tail").GetComponent<GraftHierarchyTests_Ns.GhProbe>();
        Assert.IsNull(dProbe.linkedGo, "no rename → the vendor 'Armature' has no counterpart → ref nulls");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }

    [Test]
    public void Graft_rejects_non_injective_rename_map_before_mutation()
    {
        var vendor = new GameObject("V");
        Child(vendor, "Menu");
        var ours = new GameObject("V");

        var badMap = new Dictionary<string, string> { { "Armature", "X" }, { "Other", "X" } };
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var summary = GraftHierarchy.Run(ours, vendor, new[] { "Menu" }, badMap, whatIf: false);
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("non-injective", summary);
        Assert.IsNull(ours.transform.Find("Menu"), "nothing grafted on a non-injective-map FAIL");

        Object.DestroyImmediate(vendor);
        Object.DestroyImmediate(ours);
    }
}
