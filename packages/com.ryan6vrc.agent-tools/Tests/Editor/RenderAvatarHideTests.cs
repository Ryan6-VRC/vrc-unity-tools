using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// RenderAvatar's hide list — the half that decides whether a sheet may be read as showing what was asked for.
// An entry that resolved to nothing used to be dropped in silence, so the grab rendered the subtree the caller
// had excluded and still returned OK: a contaminated sheet behind a clean verdict.
//
// CaptureCore takes a SceneView at its top, so batchmode cannot reach it (same bound as
// RenderAvatarFreshnessTests). What is asserted here is what an agent actually acts on: the resolution rule,
// the two refusals, and the empty-entry note — all extracted pure for exactly that reason.
public class RenderAvatarHideTests
{
    private GameObject _root;

    [SetUp]
    public void SetUp()
    {
        _root = new GameObject("HideTestRoot");
        var kept = Child(_root, "Kept");
        kept.AddComponent<MeshRenderer>();
        var garment = Child(_root, "Garment");
        garment.AddComponent<MeshRenderer>();
        Child(_root, "EmptyContainer"); // resolves, holds no renderer
    }

    [TearDown]
    public void TearDown()
    {
        if (_root != null) Object.DestroyImmediate(_root);
    }

    private static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    // ── Resolution ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ResolveHideList_resolvableEntry_becomesATargetAndLeavesNothingUnresolved()
    {
        RenderAvatar.ResolveHideList(_root, new[] { "Garment" }, out var targets, out var unresolved, out var caller);

        Assert.AreEqual(1, targets.Count);
        Assert.AreEqual("Garment", targets[0].name);
        Assert.AreEqual(0, unresolved.Count);
        Assert.AreEqual(1, caller.Count);
    }

    [Test]
    public void ResolveHideList_unmatchedEntry_isCapturedNotDropped()
    {
        // The defect itself: before this, a name matching nothing left no trace anywhere.
        RenderAvatar.ResolveHideList(_root, new[] { "NoSuchThing" }, out var targets, out var unresolved, out _);

        Assert.AreEqual(0, targets.Count);
        CollectionAssert.AreEqual(new[] { "NoSuchThing" }, unresolved);
    }

    [Test]
    public void ResolveHideList_mixedList_separatesTheTwoWithoutLosingEither()
    {
        RenderAvatar.ResolveHideList(_root, new[] { "Garment", "Ghost", "Kept" },
            out var targets, out var unresolved, out _);

        Assert.AreEqual(2, targets.Count, "both resolvable entries must still be hidden");
        CollectionAssert.AreEqual(new[] { "Ghost" }, unresolved, "only the unmatched entry is an offender");
    }

    [Test]
    public void ResolveHideList_duplicateEntries_hideOnceButBothAreCallerEntries()
    {
        RenderAvatar.ResolveHideList(_root, new[] { "Garment", "Garment" }, out var targets, out _, out var caller);

        Assert.AreEqual(1, targets.Count, "hiding the same subtree twice is one hide");
        Assert.AreEqual(2, caller.Count, "both entries came from the caller and both were honored");
    }

    // ── The two refusals ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void HideRefusal_capture_namesTheEntryAndTheResolutionRule()
    {
        var s = RenderAvatar.HideRefusal(pinned: false, label: "Avatar", unresolved: new List<string> { "Skirt" });

        StringAssert.Contains("Skirt", s);
        StringAssert.Contains("descendants of the target", s); // the input grammar the caller needs to fix it
        StringAssert.DoesNotContain("re-grab frame A", s);
    }

    // The regression this carve-out exists to prevent: a pinned diff replays frame A's hide list, and the object
    // A hid is frequently the very thing the edit under test deleted. Handing that caller "fix your spelling"
    // would be wrong, and failing it with Capture's message would send it to fix a list it never wrote.
    [Test]
    public void HideRefusal_pinned_blamesFrameANotTheCaller()
    {
        var s = RenderAvatar.HideRefusal(pinned: true, label: "Avatar", unresolved: new List<string> { "Skirt" });

        StringAssert.Contains("frame A", s);
        StringAssert.Contains("re-grab frame A", s);
        StringAssert.DoesNotContain("path relative to it", s);
    }

    [Test]
    public void HideRefusal_multipleEntries_namesEveryOne()
    {
        var s = RenderAvatar.HideRefusal(false, "Avatar", new List<string> { "Skirt", "Shoes" });

        StringAssert.Contains("Skirt", s);
        StringAssert.Contains("Shoes", s);
    }

    // ── The empty-entry note ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void HideEmptyNote_entryWithNoDrawableRenderer_isNamedNotFailed()
    {
        RenderAvatar.ResolveHideList(_root, new[] { "EmptyContainer" }, out _, out _, out var caller);

        var note = RenderAvatar.HideEmptyNote(caller);
        StringAssert.Contains("hideEmpty=[EmptyContainer]", note);
    }

    [Test]
    public void HideEmptyNote_entryThatExcludesSomething_isSilent()
    {
        RenderAvatar.ResolveHideList(_root, new[] { "Garment" }, out _, out _, out var caller);

        Assert.AreEqual("", RenderAvatar.HideEmptyNote(caller),
            "an entry that excludes a real renderer is the normal case and must not add noise");
    }

    [Test]
    public void HideEmptyNote_disabledRendererUnderTheEntry_countsAsEmpty()
    {
        // A renderer that would not have drawn anyway excludes nothing, so the entry is as empty as a bare
        // container — the note tracks what the grab actually dropped, not what the hierarchy contains.
        var garment = _root.transform.Find("Garment").gameObject;
        garment.GetComponent<MeshRenderer>().enabled = false;
        RenderAvatar.ResolveHideList(_root, new[] { "Garment" }, out _, out _, out var caller);

        StringAssert.Contains("hideEmpty=[Garment]", RenderAvatar.HideEmptyNote(caller));
    }
}
