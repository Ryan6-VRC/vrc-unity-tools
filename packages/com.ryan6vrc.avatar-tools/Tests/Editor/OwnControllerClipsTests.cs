// Behavioral tests for OwnControllerClips. Throwaway on-disk assets under an owned scratch path; assert on
// the returned one-line summary + post-mutation controller/clip state. Headless via tools/run-editmode-tests.ps1.
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

public class OwnControllerClipsTests
{
    const string Root = "Assets/Agent/Scratch/OwnTests_NUnit";
    const string Out = "Assets/Agent/Scratch/OwnTests_NUnit/Owned";
    const string VendorRoot = "Assets/Vendor/OwnTests_NUnit";
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

    void EnsureVendor() { if (!AssetDatabase.IsValidFolder("Assets/Vendor")) { AssetDatabase.CreateFolder("Assets", "Vendor"); _createdVendor = true; } AnimatorTestHelpers.EnsureFolder(VendorRoot); }

    AnimationClip VendorClip(string name)
    {
        EnsureVendor();
        string p = VendorRoot + "/" + name + ".anim";
        var c = AnimatorTestHelpers.MakeClip(p);
        AnimatorTestHelpers.AddFloatCurve(c, "Body", typeof(Transform), "m_LocalPosition.x");
        AnimatorTestHelpers.Save(c, p);
        return c;
    }

    static AnimationClip StateMotionClip(AnimatorController ctrl) => ctrl.layers[0].stateMachine.states[0].state.motion as AnimationClip;

    static UnityEditor.Animations.AnimatorState FindState(AnimatorController ctrl, string name)
    {
        foreach (var cs in ctrl.layers[0].stateMachine.states)
            if (cs.state != null && cs.state.name == name) return cs.state;
        return null;
    }

    [Test]
    public void Copies_vendor_clip_and_retargets_state_motion()
    {
        var v = VendorClip("Look");
        string cp = Root + "/Fx.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        ctrl.layers[0].stateMachine.AddState("S").motion = v;
        AnimatorTestHelpers.Save(ctrl, cp);

        string s = OwnControllerClips.Run(ctrl, Out);
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "copiesMade"));
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "retargets"));
        Assert.AreEqual(0, AnimatorTestHelpers.Count(s, "residual"));
        Assert.IsTrue(AssetDatabase.LoadAssetAtPath<AnimationClip>(Out + "/Look.anim") != null, "owned copy exists");
        StringAssert.StartsWith(Out, AssetDatabase.GetAssetPath(StateMotionClip(ctrl)));
    }

    [Test]
    public void Same_named_clips_get_guid_suffixed_not_collided()
    {
        EnsureVendor();
        AnimatorTestHelpers.EnsureFolder(VendorRoot + "/a");
        AnimatorTestHelpers.EnsureFolder(VendorRoot + "/b");
        var c1 = AnimatorTestHelpers.MakeClip(VendorRoot + "/a/Clip.anim");
        var c2 = AnimatorTestHelpers.MakeClip(VendorRoot + "/b/Clip.anim");
        AnimatorTestHelpers.AddFloatCurve(c1, "X", typeof(Transform), "m_LocalPosition.x"); AnimatorTestHelpers.Save(c1, VendorRoot + "/a/Clip.anim");
        AnimatorTestHelpers.AddFloatCurve(c2, "Y", typeof(Transform), "m_LocalPosition.x"); AnimatorTestHelpers.Save(c2, VendorRoot + "/b/Clip.anim");
        string cp = Root + "/Fx2.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        var sm = ctrl.layers[0].stateMachine;
        sm.AddState("S1").motion = c1; sm.AddState("S2").motion = c2;
        AnimatorTestHelpers.Save(ctrl, cp);

        string s = OwnControllerClips.Run(ctrl, Out);
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(2, AnimatorTestHelpers.Count(s, "copiesMade"));
        Assert.AreEqual(2, AssetDatabase.FindAssets("t:AnimationClip", new[] { Out }).Length, "two distinct owned copies");
        // End-state, not just file count: each state must retarget to its OWN distinct owned copy. A map
        // pointing both states at one copy (orphaning the other file) would still pass the count assertions.
        var m1 = FindState(ctrl, "S1").motion as AnimationClip;
        var m2 = FindState(ctrl, "S2").motion as AnimationClip;
        string p1 = AssetDatabase.GetAssetPath(m1), p2 = AssetDatabase.GetAssetPath(m2);
        StringAssert.StartsWith(Out, p1, "S1 retargeted to an owned copy under Out");
        StringAssert.StartsWith(Out, p2, "S2 retargeted to an owned copy under Out");
        Assert.AreNotEqual(p1, p2, "each state points at its own distinct copy, not one shared");
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(p1, "X"), "S1's copy carries source c1's X binding");
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(p2, "Y"), "S2's copy carries source c2's Y binding");
    }

    [Test]
    public void Retargets_nested_blendtree_and_synced_override()
    {
        var vTree = VendorClip("InTree");
        var vOverride = VendorClip("InOverride");
        string cp = Root + "/Fx3.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        var sm = ctrl.layers[0].stateMachine;
        var outer = new BlendTree { name = "Outer", blendType = BlendTreeType.Direct };
        var inner = new BlendTree { name = "Inner", blendType = BlendTreeType.Direct };
        AssetDatabase.AddObjectToAsset(outer, ctrl); AssetDatabase.AddObjectToAsset(inner, ctrl);
        inner.AddChild(vTree); outer.AddChild(inner);
        var st = sm.AddState("Tree"); st.motion = outer;
        AnimatorTestHelpers.AddSyncedLayer(ctrl, 0);
        var layers = ctrl.layers; layers[1].SetOverrideMotion(st, vOverride); ctrl.layers = layers;
        AnimatorTestHelpers.Save(ctrl, cp);

        string s = OwnControllerClips.Run(ctrl, Out);
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(0, AnimatorTestHelpers.Count(s, "residual"), "both nested + override slots retargeted");
        Assert.GreaterOrEqual(AnimatorTestHelpers.Count(s, "retargets"), 2);
        // residual=0 only proves no slot still points at an in-scope VENDOR clip; a slot retargeted to the
        // WRONG owned clip is still owned → residual=0. Inspect the actual end-state slots (re-walk ctrl,
        // which survives Run's reimport; the pre-Run inner/st refs may be stale). Both must be owned copies.
        var treeState = FindState(ctrl, "Tree");
        var innerBt = (treeState.motion as BlendTree).children[0].motion as BlendTree;
        var innerChild = innerBt.children[0].motion as AnimationClip;
        Assert.IsNotNull(innerChild, "nested BT child is a clip after retarget");
        StringAssert.StartsWith(Out, AssetDatabase.GetAssetPath(innerChild), "nested BT child retargeted to an owned copy");
        Assert.AreNotEqual(vTree, innerChild, "nested BT child no longer the vendor original");
        var overrideMotion = ctrl.layers[1].GetOverrideMotion(treeState) as AnimationClip;
        Assert.IsNotNull(overrideMotion, "synced override is a clip after retarget");
        StringAssert.StartsWith(Out, AssetDatabase.GetAssetPath(overrideMotion), "synced override retargeted to an owned copy");
        Assert.AreNotEqual(vOverride, overrideMotion, "synced override no longer the vendor original");
    }

    [Test]
    public void Preexisting_divergent_copy_is_reused_not_overwritten()
    {
        var v = VendorClip("Shared");
        AnimatorTestHelpers.EnsureFolder(Out);
        var foreign = AnimatorTestHelpers.MakeClip(Out + "/Shared.anim");
        AnimatorTestHelpers.AddFloatCurve(foreign, "Totally/Different", typeof(Transform), "m_LocalScale.x");
        AnimatorTestHelpers.Save(foreign, Out + "/Shared.anim");
        string cp = Root + "/Fx4.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        ctrl.layers[0].stateMachine.AddState("S").motion = v;
        AnimatorTestHelpers.Save(ctrl, cp);

        string s = OwnControllerClips.Run(ctrl, Out);
        StringAssert.Contains("=> PASS", s);
        StringAssert.Contains("differs from source", s);
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(Out + "/Shared.anim", "Totally/Different"), "existing copy NOT overwritten");
    }

    [Test]
    public void Second_run_is_idempotent()
    {
        var v = VendorClip("Idem");
        string cp = Root + "/Fx5.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        ctrl.layers[0].stateMachine.AddState("S").motion = v;
        AnimatorTestHelpers.Save(ctrl, cp);

        OwnControllerClips.Run(ctrl, Out);
        string again = OwnControllerClips.Run(ctrl, Out);
        StringAssert.Contains("=> PASS", again);
        Assert.AreEqual(0, AnimatorTestHelpers.Count(again, "copiesMade"));
        Assert.AreEqual(0, AnimatorTestHelpers.Count(again, "retargets"));
    }

    [Test]
    public void Readonly_controller_fails_unless_force()
    {
        var v = VendorClip("ROC");
        EnsureVendor();
        string cp = VendorRoot + "/ro.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        ctrl.layers[0].stateMachine.AddState("S").motion = v;
        AnimatorTestHelpers.Save(ctrl, cp);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("read-only controller"));
        string fail = OwnControllerClips.Run(ctrl, Out);
        StringAssert.Contains("=> FAIL", fail);
        StringAssert.Contains("read-only controller", fail);

        string forced = OwnControllerClips.Run(ctrl, Out, OwnControllerClips.Scope.VendorOnly, force: true);
        StringAssert.Contains("=> PASS", forced);
        StringAssert.Contains("read-only controller override (force)", forced);
    }

    [Test]
    public void Readonly_outDir_fails_unless_force()
    {
        var v = VendorClip("ROO");
        string cp = Root + "/Fx6.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        ctrl.layers[0].stateMachine.AddState("S").motion = v;
        AnimatorTestHelpers.Save(ctrl, cp);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("read-only outDir"));
        string fail = OwnControllerClips.Run(ctrl, VendorRoot + "/out");
        StringAssert.Contains("=> FAIL", fail);
        StringAssert.Contains("read-only outDir", fail);

        string forced = OwnControllerClips.Run(ctrl, VendorRoot + "/out", OwnControllerClips.Scope.VendorOnly, force: true);
        StringAssert.Contains("=> PASS", forced);
        StringAssert.Contains("read-only outDir override (force)", forced);
    }

    [Test]
    public void Subasset_clip_is_refused()
    {
        string cp = Root + "/Fx7.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        var sub = new AnimationClip { name = "Embedded" };
        AssetDatabase.AddObjectToAsset(sub, ctrl);
        ctrl.layers[0].stateMachine.AddState("S").motion = sub;
        AnimatorTestHelpers.Save(ctrl, cp);
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("sub-asset"));
        string fail = OwnControllerClips.Run(ctrl, Out, OwnControllerClips.Scope.All);
        StringAssert.Contains("=> FAIL", fail);
        StringAssert.Contains("sub-asset", fail);
    }

    [Test]
    public void VendorOnly_skips_writable_clip_All_forks_it()
    {
        var vendor = VendorClip("V");
        string ownClipPath = Root + "/Owned.anim";
        var own = AnimatorTestHelpers.MakeClip(ownClipPath);
        AnimatorTestHelpers.AddFloatCurve(own, "Z", typeof(Transform), "m_LocalPosition.x");
        AnimatorTestHelpers.Save(own, ownClipPath);
        string cp = Root + "/Fx8.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
        var sm = ctrl.layers[0].stateMachine;
        sm.AddState("Sv").motion = vendor; sm.AddState("So").motion = own;
        AnimatorTestHelpers.Save(ctrl, cp);

        string vendorOnly = OwnControllerClips.Run(ctrl, Out, OwnControllerClips.Scope.VendorOnly, whatIf: true);
        Assert.AreEqual(1, AnimatorTestHelpers.Count(vendorOnly, "clipsInScope"), "only the vendor clip");
        string all = OwnControllerClips.Run(ctrl, Out, OwnControllerClips.Scope.All, whatIf: true);
        Assert.AreEqual(2, AnimatorTestHelpers.Count(all, "clipsInScope"), "vendor + own");
    }

    // Spike (2026-07-08) verdict: controller=WRITE LANDED — SaveAssets bypasses the OS read-only attribute,
    // so a silent no-op on an immutable .controller is NOT fabricable in EditMode on this Unity/OS combo.
    // The residual scan's clean path is covered by the happy-path/recursive tests; the FAIL branch is
    // exercised in production against Packages/ immutables. Gap kept loud + on the record per plan.
    [Test]
    public void Residual_failbranch_not_fabricable()
    {
        Assert.Ignore("Spike controller=WRITE LANDED: silent no-op on an immutable controller not fabricable in " +
                      "EditMode (SaveAssets bypasses the OS read-only attribute). Residual clean path covered by " +
                      "the happy-path/recursive tests; FAIL branch exercised in production.");
    }
}
