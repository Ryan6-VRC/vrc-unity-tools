using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;
using Ryan6Vrc.AgentTools.Editor;

// SweepTestDummySmb (the orphan-SMB fixture type) lives in its own same-named file — Unity requires a
// StateMachineBehaviour subclass to be filed that way to resolve its script asset.

// Behavioral tests for SweepController. Each test builds a throwaway controller as an on-disk asset
// under an owned scratch path (sub-assets only exist on a saved asset) and asserts on the returned
// one-line summary + the post-sweep asset state. Run headless via tools/run-editmode-tests.ps1 (or
// the Test Runner window / CI); not via MCP run_tests — wrong venue (live editor). See docs/verify.md.
public class SweepControllerTests
{
    const string Root = "Assets/Agent/Scratch/SweepTests_NUnit";
    const string VendorRoot = "Assets/Vendor/SweepTests_NUnit";
    bool _createdVendor;

    [SetUp]
    public void SetUp()
    {
        EnsureFolder(Root);
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Root);
        AssetDatabase.DeleteAsset(VendorRoot);
        // Never delete the real Assets/Vendor tree — only a Vendor root WE minted for the read-only test.
        if (_createdVendor && AssetDatabase.IsValidFolder("Assets/Vendor")
            && AssetDatabase.FindAssets("", new[] { "Assets/Vendor" }).Length == 0)
        {
            AssetDatabase.DeleteAsset("Assets/Vendor");
        }
        _createdVendor = false;
    }

    // ── Test 1: hidden-orphan removal (the whole point) + AnimatorLint advises it ──────────────────

    [Test]
    public void Hidden_orphan_removed_and_AnimatorLint_advises_it()
    {
        string path = Root + "/Hidden.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        ctrl.layers[0].stateMachine.AddState("A");
        // The realistic orphan: an animator sub-object Unity hides (IsSubAsset returns false for it).
        var hidden = AddOrphan<AnimatorState>(ctrl, "HID_Orphan", hide: true);
        Save(ctrl, path);

        // AnimatorLint (post companion fix) must advise the hidden orphan — the detect half of the handoff.
        var lintOrphans = AnimatorLintOrphanTokens(ctrl, path);
        Assert.Contains("AnimatorState 'HID_Orphan'", lintOrphans,
            "AnimatorLint must advise the HideInHierarchy orphan (regression: a reintroduced IsSubAsset gate hides it)");

        string summary = SweepController.Sweep(ctrl, false, false);
        Assert.GreaterOrEqual(Count(summary, "removed"), 1);
        StringAssert.Contains("=> PASS", summary);
        Assert.IsFalse(HasSubObjectNamed(path, "HID_Orphan"),
            "hidden orphan must be gone (regression: a reintroduced IsSubAsset gate would leave it)");
        Assert.IsTrue(HasSubObjectNamed(path, "A"), "reachable content untouched");
    }

    // ── Test 2: dead-end vs truly-orphan transition tally ─────────────────────────────────────────

    [Test]
    public void DeadEnd_and_truly_orphan_transition_tally_distinctly()
    {
        string path = Root + "/DeadEnd.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var sm = ctrl.layers[0].stateMachine;
        var a = sm.AddState("A");
        var b = sm.AddState("B");
        a.AddTransition(b);            // valid (to a state) — survives
        a.AddExitTransition();         // valid (isExit) — survives
        var dead = a.AddTransition(b); dead.destinationState = null; // dead-end on a reachable state
        AddOrphan<AnimatorStateTransition>(ctrl, "ORPH_T", hide: false); // truly-orphan transition (never visited)
        Save(ctrl, path);

        string summary = SweepController.Sweep(ctrl, false, false);
        // removed = dead-end + truly-orphan transition = 2; deadEndTransitions counts ONLY the dead-end.
        Assert.AreEqual(2, Count(summary, "removed"));
        Assert.AreEqual(1, Count(summary, "deadEndTransitions"));
        // The valid transitions survive: A keeps its to-state + exit transition.
        var a2 = FindState(ctrl, "A");
        Assert.AreEqual(2, a2.transitions.Length);
        foreach (var t in a2.transitions)
            Assert.IsTrue(t.destinationState != null || t.isExit, "only resolving transitions survive");
    }

    // ── Test 3: guarded null-slot compaction (synced source SM compacted exactly once) ────────────

    [Test]
    public void Compaction_leaves_no_null_slots_and_synced_source_counted_once()
    {
        string path = Root + "/Compact.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var sm = ctrl.layers[0].stateMachine;
        var a = sm.AddState("A");
        var b = sm.AddState("B");
        var dead = a.AddTransition(b); dead.destinationState = null; // one compactable slot on the source
        AddSyncedLayer(ctrl);          // a synced layer shares layer 0's source SM
        Save(ctrl, path);

        string summary = SweepController.Sweep(ctrl, false, false);
        // The shared source SM's one dead-end slot is counted ONCE, not once-per-referring-layer.
        Assert.AreEqual(1, Count(summary, "slotsCompacted"));
        StringAssert.Contains("=> PASS", summary);
        // No null slot survives in the source SM's transition array.
        var a2 = FindState(ctrl, "A");
        foreach (var t in a2.transitions) Assert.IsNotNull(t, "no null transition slot may remain");
    }

    // ── Test 4: synced-layer correctness (override motion NOT swept) ──────────────────────────────

    [Test]
    public void Synced_layer_override_motion_is_preserved()
    {
        string path = Root + "/Synced.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var a = ctrl.layers[0].stateMachine.AddState("A");
        AddSyncedLayer(ctrl);
        var overrideBt = AddOrphan<BlendTree>(ctrl, "OverrideBT", hide: false);
        // Establish the per-state override on the synced layer (ctrl.layers returns a COPY — reassign).
        var layers = ctrl.layers;
        layers[1].SetOverrideMotion(a, overrideBt);
        ctrl.layers = layers;
        AddOrphan<AnimatorState>(ctrl, "UnrelatedOrphan", hide: true); // ensure the sweep removes something
        Save(ctrl, path);
        Assert.IsNotNull(ctrl.layers[1].GetOverrideMotion(FindState(ctrl, "A")), "fixture: override established");

        string summary = SweepController.Sweep(ctrl, false, false);
        Assert.AreEqual(1, Count(summary, "removed"), "only the unrelated orphan is swept");
        Assert.IsTrue(HasSubObjectNamed(path, "OverrideBT"),
            "a synced-layer override motion is reachable and must NOT be swept (the axis DreadScripts got wrong)");
        Assert.IsNotNull(ctrl.layers[1].GetOverrideMotion(FindState(ctrl, "A")), "override still set");
    }

    // ── Test 5: read-only controller guard ────────────────────────────────────────────────────────

    [Test]
    public void Readonly_controller_fails_unless_force()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Vendor")) { AssetDatabase.CreateFolder("Assets", "Vendor"); _createdVendor = true; }
        EnsureFolder(VendorRoot);
        string path = VendorRoot + "/RO.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        ctrl.layers[0].stateMachine.AddState("A");
        AddOrphan<AnimatorState>(ctrl, "VendorOrphan", hide: true);
        Save(ctrl, path);

        string fail = SweepController.Sweep(ctrl, false, false);
        StringAssert.Contains("=> FAIL", fail);
        StringAssert.Contains("read-only", fail);
        StringAssert.Contains("RO.controller", fail);
        Assert.IsTrue(HasSubObjectNamed(path, "VendorOrphan"), "nothing removed on a refused read-only sweep");

        // whatIf FAILs identically (preview == execute verdict).
        StringAssert.Contains("=> FAIL", SweepController.Sweep(ctrl, false, true));

        // force → PASS with a loud override note.
        string forced = SweepController.Sweep(ctrl, true, false);
        StringAssert.Contains("=> PASS", forced);
        StringAssert.Contains("read-only controller override (force)", forced);
        Assert.IsFalse(HasSubObjectNamed(path, "VendorOrphan"), "force sweeps the orphan");
    }

    // ── Test 6: not-a-saved-asset guard ───────────────────────────────────────────────────────────

    [Test]
    public void Unsaved_controller_is_refused()
    {
        var mem = new AnimatorController { name = "InMemory" };
        string summary = SweepController.Sweep(mem, false, false);
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("not a saved asset", summary);
        // No force path — force still refuses.
        StringAssert.Contains("=> FAIL", SweepController.Sweep(mem, true, false));
        Object.DestroyImmediate(mem);
    }

    // ── Test 7: whatIf parity + idempotence ───────────────────────────────────────────────────────

    [Test]
    public void WhatIf_matches_execute_and_second_execute_is_empty()
    {
        string path = Root + "/Idem.controller";
        var ctrl = BuildMixedFixture(path);

        string preview = SweepController.Sweep(ctrl, false, true);
        StringAssert.Contains("(whatIf)", preview);
        StringAssert.Contains("=> PASS", preview);
        // whatIf mutated nothing.
        Assert.IsTrue(HasSubObjectNamed(path, "HID_Orphan"), "whatIf must not remove anything");

        string exec = SweepController.Sweep(ctrl, false, false);
        Assert.AreEqual(Count(preview, "removed"), Count(exec, "removed"));
        Assert.AreEqual(Count(preview, "deadEndTransitions"), Count(exec, "deadEndTransitions"));
        Assert.AreEqual(Count(preview, "slotsCompacted"), Count(exec, "slotsCompacted"));

        string again = SweepController.Sweep(ctrl, false, false);
        Assert.AreEqual(0, Count(again, "removed"));
        Assert.AreEqual(0, Count(again, "deadEndTransitions"));
        Assert.AreEqual(0, Count(again, "slotsCompacted"));
        StringAssert.Contains("=> PASS", again);
    }

    // ── Test 8: differential oracle vs AnimatorLint ───────────────────────────────────────────────

    [Test]
    public void Differential_oracle_holds_against_AnimatorLint()
    {
        string path = Root + "/Oracle.controller";
        var ctrl = BuildMixedFixture(path); // reachable A/B + dead-end + hidden orphan + orphan BT + truly-orphan T

        string preview = SweepController.Sweep(ctrl, false, true);
        var sweep = new HashSet<string>(Notes(preview));
        int deadEnds = Count(preview, "deadEndTransitions");
        var lint = new HashSet<string>(AnimatorLintOrphanTokens(ctrl, path));

        // Oracle: {would-remove} − {dead-ends} == {AnimatorLint orphan advisories}. Realized as:
        //  (a) every advisory is a would-remove (AnimatorLint ⊆ Sweep),
        //  (b) the would-removes AnimatorLint omits are exactly the dead-ends: |Sweep − Lint| == deadEnds,
        //      and each is transition-typed. The fixture names its truly-orphan transition so the only
        //      empty-named transition token is the dead-end (no token collision).
        foreach (var l in lint) Assert.IsTrue(sweep.Contains(l), "AnimatorLint advisory not in would-remove: " + l);
        var extra = new List<string>();
        foreach (var s in sweep) if (!lint.Contains(s)) extra.Add(s);
        Assert.AreEqual(deadEnds, extra.Count, "Sweep − AnimatorLint must be exactly the dead-end set");
        foreach (var e in extra)
            StringAssert.StartsWith("AnimatorStateTransition", e, "the extra members are dead-end transitions");
    }

    // ── Test 9: nested sub-state-machine — dead-end SM→SM transition + orphan SMB ─────────────────
    // The one destructive write-back the flat single-layer tests never exercise: the
    // GetStateMachineTransitions / SetStateMachineTransitions compaction (step 9) and orphan
    // StateMachineBehaviour removal.

    [Test]
    public void Nested_deadend_smt_and_orphan_smb_are_swept_and_written_back()
    {
        string path = Root + "/Nested.controller";
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var root = ctrl.layers[0].stateMachine;
        var childA = root.AddStateMachine("ChildA");
        var childB = root.AddStateMachine("ChildB");
        childA.AddState("ChildAState");                        // reachable nested content
        // A valid SM→SM transition FROM childA (source=childA, so it lives under
        // GetStateMachineTransitions(childA)) with a destination — survives.
        root.AddStateMachineTransition(childA, childB);
        // A true dead-end SM→SM transition FROM childA — resolves nowhere, not exit.
        var dead = root.AddStateMachineTransition(childA, childB);
        dead.destinationStateMachine = null; dead.destinationState = null;
        // Orphan StateMachineBehaviour (reachable from nothing).
        var orphanSmb = ScriptableObject.CreateInstance<SweepTestDummySmb>();
        orphanSmb.name = "ORPHAN_SMB";
        AssetDatabase.AddObjectToAsset(orphanSmb, ctrl);
        Save(ctrl, path);

        string summary = SweepController.Sweep(ctrl, false, false);
        // removed = dead-end SM→SM transition + orphan SMB; deadEndTransitions counts only the dead-end.
        Assert.AreEqual(2, Count(summary, "removed"));
        Assert.AreEqual(1, Count(summary, "deadEndTransitions"));
        Assert.AreEqual(1, Count(summary, "slotsCompacted"));
        StringAssert.Contains("=> PASS", summary);

        Assert.IsFalse(HasSubObjectNamed(path, "ORPHAN_SMB"), "orphan StateMachineBehaviour must be swept");
        Assert.IsTrue(HasSubObjectNamed(path, "ChildAState"), "reachable nested state survives");

        // The write-back path the flat tests miss: the dead-end slot is compacted out of
        // GetStateMachineTransitions(childA) — exactly the resolving transition remains, no null slot.
        var cA = FindChildSm(ctrl, "ChildA");
        var smt = ctrl.layers[0].stateMachine.GetStateMachineTransitions(cA);
        Assert.AreEqual(1, smt.Length, "only the resolving SM→SM transition survives");
        foreach (var t in smt) Assert.IsNotNull(t, "no null SM→SM transition slot may remain post-compaction");
        Assert.IsNotNull(smt[0].destinationStateMachine, "surviving SM→SM transition keeps its destination");
    }

    // ── Fixture + assertion helpers ───────────────────────────────────────────────────────────────

    // reachable A/B with a dead-end on A + a hidden orphan state + a visible orphan BlendTree + a
    // named truly-orphan transition. Matches the live-validated "Main"/"Oracle" fixtures.
    static AnimatorController BuildMixedFixture(string path)
    {
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
        var sm = ctrl.layers[0].stateMachine;
        var a = sm.AddState("A");
        var b = sm.AddState("B");
        a.AddTransition(b);
        var dead = a.AddTransition(b); dead.destinationState = null;
        AddOrphan<AnimatorState>(ctrl, "HID_Orphan", hide: true);
        AddOrphan<BlendTree>(ctrl, "VIS_OrphanBT", hide: false);
        AddOrphan<AnimatorStateTransition>(ctrl, "ORPH_T", hide: false);
        Save(ctrl, path);
        return ctrl;
    }

    static T AddOrphan<T>(AnimatorController ctrl, string name, bool hide) where T : Object, new()
    {
        var o = new T { name = name };
        AssetDatabase.AddObjectToAsset(o, ctrl);
        if (hide) o.hideFlags = HideFlags.HideInHierarchy;
        return o;
    }

    static void AddSyncedLayer(AnimatorController ctrl)
    {
        var synced = new AnimatorControllerLayer { name = "SyncedLayer", syncedLayerIndex = 0, defaultWeight = 1f };
        var list = new List<AnimatorControllerLayer>(ctrl.layers) { synced };
        ctrl.layers = list.ToArray();
    }

    static void Save(AnimatorController ctrl, string path)
    {
        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path);
    }

    static AnimatorState FindState(AnimatorController ctrl, string name)
    {
        foreach (var cs in ctrl.layers[0].stateMachine.states)
            if (cs.state != null && cs.state.name == name) return cs.state;
        return null;
    }

    static AnimatorStateMachine FindChildSm(AnimatorController ctrl, string name)
    {
        foreach (var cm in ctrl.layers[0].stateMachine.stateMachines)
            if (cm.stateMachine != null && cm.stateMachine.name == name) return cm.stateMachine;
        return null;
    }

    static bool HasSubObjectNamed(string path, string name)
    {
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            if (o != null && o.name == name) return true;
        return false;
    }

    // Run the (fixed) AnimatorLint and parse its markdown RunLog for orphanSubAsset "Type 'name'" tokens.
    static List<string> AnimatorLintOrphanTokens(AnimatorController ctrl, string path)
    {
        var tokens = new List<string>();
        string ret = AnimatorLint.Lint(ctrl, "explicit", null, null, null);
        int li = ret.IndexOf("log=");
        if (li < 0) return tokens;
        string rel = ret.Substring(li + 4).Trim();
        string proj = Application.dataPath;
        proj = proj.Substring(0, proj.Length - "Assets".Length); // project root (dataPath ends in /Assets)
        string abs = proj + rel;
        if (!File.Exists(abs)) return tokens;
        foreach (var line in File.ReadAllText(abs).Split('\n'))
        {
            int oi = line.IndexOf("**orphanSubAsset**");
            if (oi < 0) continue;
            string after = line.Substring(oi + "**orphanSubAsset**".Length);
            int dash = after.IndexOf(" — ");
            if (dash >= 0) after = after.Substring(0, dash);
            tokens.Add(after.Trim());
        }
        return tokens;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        string parent = path.Substring(0, slash);
        string leaf = path.Substring(slash + 1);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    static int Count(string summary, string key)
    {
        int i = summary.IndexOf(key + "=");
        Assert.GreaterOrEqual(i, 0, "count '" + key + "' missing in: " + summary);
        i += key.Length + 1;
        int j = i;
        while (j < summary.Length && char.IsDigit(summary[j])) j++;
        return int.Parse(summary.Substring(i, j - i));
    }

    static List<string> Notes(string summary)
    {
        var list = new List<string>();
        int ni = summary.IndexOf("notes=[");
        if (ni < 0) return list;
        int e = summary.IndexOf("]", ni);
        string body = summary.Substring(ni + 7, e - (ni + 7));
        foreach (var p in body.Split(';'))
        {
            var s = p.Trim();
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }
}
