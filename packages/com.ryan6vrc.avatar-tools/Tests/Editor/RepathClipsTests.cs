using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// Behavioral tests for RepathClips. Throwaway on-disk assets under an owned scratch path; assert on the
// returned one-line summary + post-mutation clip bindings. Headless via tools/run-editmode-tests.ps1.
public class RepathClipsTests
{
    const string Root = "Assets/Agent/Scratch/RepathTests_NUnit";
    const string VendorRoot = "Assets/Vendor/RepathTests_NUnit";
    bool _createdVendor;

    [SetUp] public void SetUp() { AnimatorTestHelpers.EnsureFolder(Root); }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
        AssetDatabase.DeleteAsset(VendorRoot);
        if (_createdVendor && AssetDatabase.IsValidFolder("Assets/Vendor")
            && AssetDatabase.FindAssets("", new[] { "Assets/Vendor" }).Length == 0)
            AssetDatabase.DeleteAsset("Assets/Vendor");
        _createdVendor = false;
    }

    static AnimatorController BuildWithClip(string ctrlPath, string clipPath, params string[] bindingPaths)
    {
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
        var clip = AnimatorTestHelpers.MakeClip(clipPath);
        for (int i = 0; i < bindingPaths.Length; i++)
            AnimatorTestHelpers.AddFloatCurve(clip, bindingPaths[i], typeof(Transform), "m_LocalPosition.x", v: i + 1);
        AnimatorTestHelpers.Save(clip, clipPath);
        ctrl.layers[0].stateMachine.AddState("A").motion = clip;
        AnimatorTestHelpers.Save(ctrl, ctrlPath);
        return ctrl;
    }

    static float CurveValueAt(string clipPath, string bindingPath)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        var b = EditorCurveBinding.FloatCurve(bindingPath, typeof(Transform), "m_LocalPosition.x");
        var c = AnimationUtility.GetEditorCurve(clip, b);
        return c != null && c.length > 0 ? c[0].value : float.NaN;
    }

    [Test]
    public void SegmentSafe_match_hits_self_and_descendant_never_sibling_prefix()
    {
        string cp = Root + "/Seg.controller", clip = Root + "/Seg.anim";
        var ctrl = BuildWithClip(cp, clip, "Armature/Hips", "Armature/Hips/Spine", "Armature/HipsFoo");

        string s = RepathClips.Run(ctrl, new[] { "Armature/Hips" }, new[] { "Armature/Pelvis" });
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(2, AnimatorTestHelpers.Count(s, "bindingsRewritten"), "Hips + Hips/Spine, not HipsFoo");
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(clip, "Armature/Pelvis"));
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(clip, "Armature/Pelvis/Spine"));
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(clip, "Armature/HipsFoo"), "sibling-prefix untouched");
        Assert.IsFalse(AnimatorTestHelpers.ClipHasBinding(clip, "Armature/Hips"));
    }

    [Test]
    public void Collision_two_sources_onto_one_target_fails_unless_force()
    {
        string cp = Root + "/Col.controller", clip = Root + "/Col.anim";
        var ctrl = BuildWithClip(cp, clip, "A", "B");
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("curve collision"));
        string fail = RepathClips.Run(ctrl, new[] { "A", "B" }, new[] { "T", "T" });
        StringAssert.Contains("=> FAIL", fail);
        StringAssert.Contains("curve collision", fail);
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(clip, "A"), "nothing rewritten on a refused collision");

        // force overrides the collision GUARD, but the write-landed read-back is unconditional (never
        // bypassed by force — see RepathClips.cs). A genuine 2-sources-into-1-target collapse is lossy
        // (only the last-written curve survives at T), so the read-back correctly flags a content
        // mismatch and the run still FAILs, now via the write-landed guard instead of the collision guard.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("write did not land"));
        string forced = RepathClips.Run(ctrl, new[] { "A", "B" }, new[] { "T", "T" }, force: true);
        StringAssert.Contains("curve collision override (force)", forced);
        StringAssert.Contains("=> FAIL", forced);
        StringAssert.Contains("write did not land", forced);
    }

    [Test]
    public void Collision_move_onto_occupied_stayer_fails_unless_force()
    {
        string cp = Root + "/Stay.controller", clip = Root + "/Stay.anim";
        var ctrl = BuildWithClip(cp, clip, "S", "A");
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("curve collision"));
        string fail = RepathClips.Run(ctrl, new[] { "A" }, new[] { "S" });
        StringAssert.Contains("=> FAIL", fail);
        StringAssert.Contains("curve collision", fail);
        Assert.AreEqual(1f, CurveValueAt(clip, "S"), "stayer curve (value 1) not overwritten by A (value 2)");

        string forced = RepathClips.Run(ctrl, new[] { "A" }, new[] { "S" }, force: true);
        StringAssert.Contains("curve collision override (force)", forced);
    }

    [Test]
    public void Cyclic_swap_moves_curve_content_across_both_bindings()
    {
        string cp = Root + "/Swap.controller", clip = Root + "/Swap.anim";
        var ctrl = BuildWithClip(cp, clip, "A", "B");   // A=1, B=2
        string s = RepathClips.Run(ctrl, new[] { "A", "B" }, new[] { "B", "A" });
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(2f, CurveValueAt(clip, "A"), "A now holds B's original curve");
        Assert.AreEqual(1f, CurveValueAt(clip, "B"), "B now holds A's original curve");
    }

    [Test]
    public void WriteLanded_readback_passes_on_normal_owned_rewrite()
    {
        string cp = Root + "/RB.controller", clip = Root + "/RB.anim";
        var ctrl = BuildWithClip(cp, clip, "Old/Path");
        string s = RepathClips.Run(ctrl, new[] { "Old/Path" }, new[] { "New/Path" });
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(0, AnimatorTestHelpers.CountOrZero(s, "writeLandedFailures"));
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(clip, "New/Path"));
        Assert.IsFalse(AnimatorTestHelpers.ClipHasBinding(clip, "Old/Path"));
    }

    [Test]
    public void WhatIf_matches_execute_and_second_run_is_empty()
    {
        string cp = Root + "/Idem.controller", clip = Root + "/Idem.anim";
        var ctrl = BuildWithClip(cp, clip, "X");

        string preview = RepathClips.Run(ctrl, new[] { "X" }, new[] { "Y" }, whatIf: true);
        StringAssert.Contains("(whatIf)", preview);
        StringAssert.Contains("=> PASS", preview);
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(clip, "X"), "whatIf mutates nothing");

        string exec = RepathClips.Run(ctrl, new[] { "X" }, new[] { "Y" });
        Assert.AreEqual(AnimatorTestHelpers.Count(preview, "bindingsRewritten"),
                        AnimatorTestHelpers.Count(exec, "bindingsRewritten"));

        string again = RepathClips.Run(ctrl, new[] { "X" }, new[] { "Y" });
        StringAssert.Contains("=> PASS", again);
        Assert.AreEqual(0, AnimatorTestHelpers.Count(again, "bindingsRewritten"));
    }

    [Test]
    public void Readonly_clip_fails_unless_force()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Vendor")) { AssetDatabase.CreateFolder("Assets", "Vendor"); _createdVendor = true; }
        AnimatorTestHelpers.EnsureFolder(VendorRoot);
        string cp = Root + "/Own.controller", clip = VendorRoot + "/RO.anim";
        var c = AnimatorTestHelpers.MakeClip(clip);
        AnimatorTestHelpers.AddFloatCurve(c, "P", typeof(Transform), "m_LocalPosition.x");
        AnimatorTestHelpers.Save(c, clip);
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        ctrl.layers[0].stateMachine.AddState("A").motion = c;
        AnimatorTestHelpers.Save(ctrl, cp);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("read-only clip"));
        string fail = RepathClips.Run(ctrl, new[] { "P" }, new[] { "Q" });
        StringAssert.Contains("=> FAIL", fail);
        StringAssert.Contains("read-only clip", fail);

        string forced = RepathClips.Run(ctrl, new[] { "P" }, new[] { "Q" }, force: true);
        StringAssert.Contains("read-only clip override (force)", forced);
    }

    [Test]
    public void Invalid_moves_fail_precondition()
    {
        string cp = Root + "/Inv.controller", clip = Root + "/Inv.anim";
        var ctrl = BuildWithClip(cp, clip, "P");
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("empty/null oldPath|duplicate oldPath"));
        string emptyOld = RepathClips.Run(ctrl, new[] { "" }, new[] { "Q" });
        StringAssert.Contains("=> FAIL", emptyOld);

        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("duplicate oldPath"));
        string dup = RepathClips.Run(ctrl, new[] { "P", "P" }, new[] { "Q", "R" });
        StringAssert.Contains("=> FAIL", dup);
    }

    [Test]
    public void Stale_move_matches_nothing_warns_but_passes()
    {
        string cp = Root + "/Stale.controller", clip = Root + "/Stale.anim";
        var ctrl = BuildWithClip(cp, clip, "Real");
        string s = RepathClips.Run(ctrl, new[] { "Ghost" }, new[] { "AlsoGhost" });
        StringAssert.Contains("=> PASS", s);
        StringAssert.Contains("stale move?", s);
    }

    // Spike (2026-07-08) verdict: anim=WRITE LANDED — SaveAssets bypasses the OS read-only attribute, so a
    // silent no-op on an immutable .anim is NOT fabricable in EditMode on this Unity/OS combo. The read-back's
    // happy path is covered by WriteLanded_readback_passes_on_normal_owned_rewrite; the FAIL branch is
    // exercised in production against Packages/ immutables. Gap kept loud + on the record per plan.
    [Test]
    public void WriteLanded_readback_failbranch_not_fabricable()
    {
        Assert.Ignore("Spike anim=WRITE LANDED: silent no-op on an immutable .anim not fabricable in EditMode " +
                      "(SaveAssets bypasses the OS read-only attribute). Happy path covered by " +
                      "WriteLanded_readback_passes_on_normal_owned_rewrite; FAIL branch exercised in production.");
    }
}
