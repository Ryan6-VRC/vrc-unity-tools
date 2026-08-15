using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// ReportController proof obligations. One fixture per DOOR, not per repair wave: the three files this
// replaces each minted their own scratch folder (created and deleted per test, 9× a run) and each
// re-declared `PathFrom`, and none of them deleted the Snapshot markdown their calls wrote.
public class ReportControllerTests
{
    private const string Dir = "Assets/Agent/_report_controller_test";

    // Snapshots are DURABLE per docs/unity-tools.md — the operator's own pile, pruned by nothing. Record what
    // the door wrote and delete exactly that; a glob over `controller_*.md` would also sweep snapshots the
    // operator took by hand.
    private static readonly List<string> Artifacts = new List<string>();

    private AnimatorController _ctrl;

    [OneTimeSetUp]
    public void CreateScratchDir() => Directory.CreateDirectory(Dir);

    [OneTimeTearDown]
    public void DeleteScratchDirAndSnapshots()
    {
        var doomed = new List<string>(Artifacts) { Dir };
        AssetDatabase.DeleteAssets(doomed.ToArray(), new List<string>());
        Artifacts.Clear();
    }

    // A fresh controller per test at a per-test path. Several tests rewrite the controller's YAML on disk and
    // reimport it, so a shared asset path would carry one test's dangling guids into the next.
    [SetUp]
    public void SetUp() => _ctrl = AnimatorController.CreateAnimatorControllerAtPath(
        Dir + "/" + TestContext.CurrentContext.Test.MethodName + ".controller");

    // The digest body, with the Snapshot path it names recorded for teardown.
    private static string ReadReport(AnimatorController c)
    {
        string summary = ReportController.Run(c);
        return File.ReadAllText(Track(summary));
    }

    private static string Track(string summary)
    {
        int i = summary.IndexOf("log=");
        string path = i >= 0 ? summary.Substring(i + 4).Trim() : summary;
        if (i >= 0) Artifacts.Add(path);
        return path;
    }

    // ----- Sub-machine exit edges -------------------------------------------------------------------
    //
    // A sub-machine's OUTGOING (on-Exit) edges are the only way out of that sub-machine, and the digest walked
    // AnyState, Entry, and each state's ladder while never reading GetStateMachineTransitions. So a layer built
    // around sub-machines rendered a machine with no exit and gave the reader no sign control flow was omitted —
    // a silent gap, not a loud one, in the door an agent uses instead of opening the Animator window. Measured at
    // 3 of 6 controllers in a real vendor prop package, which is also why the schema grew `onExit:`.
    //
    // These pin both that the edges appear and that their CONDITIONS and TARGET do, since a rendered edge with no
    // condition is indistinguishable from an unconditional one — the difference between "always leaves" and
    // "leaves when g".

    [Test]
    public void TargetedOutgoingEdge_IsRenderedWithItsConditionAndDestination()
    {
        _ctrl.AddParameter("g", AnimatorControllerParameterType.Bool);
        var root = _ctrl.layers[0].stateMachine;
        var sub = root.AddStateMachine("Sub");
        sub.AddState("Inner");
        var dst = root.AddState("Dst");
        var t = root.AddStateMachineTransition(sub, dst);
        t.AddCondition(AnimatorConditionMode.If, 0, "g");

        string report = ReadReport(_ctrl);

        // The WHOLE line, not fragments: `Dst` alone is matched by the state's own `### \`Dst\`` header and
        // `g` by the Parameters table and every layer's `weight=`, so a fragment triple passes on a row that
        // rendered `→ (none) [unconditional]`.
        StringAssert.Contains("- `Sub` (state machine) onExit → `Dst` [`g` = true]", report);
    }

    [Test]
    public void ExitEdge_IsRenderedRatherThanOmitted()
    {
        var root = _ctrl.layers[0].stateMachine;
        var sub = root.AddStateMachine("Sub");
        sub.AddState("Inner");
        root.AddStateMachineExitTransition(sub);   // unconditional, straight up to the parent's Exit

        string report = ReadReport(_ctrl);

        // `Contains("Exit")` was satisfied by the "onExit" label itself, so this passed while the destination
        // rendered `(none)` — the exact defect it was written to catch. Assert the destination token.
        StringAssert.Contains("- `Sub` (state machine) onExit → Exit [unconditional]", report);
    }

    // A sub-machine with no outgoing edges must not acquire an onExit line — the row is evidence, so a
    // fabricated one would read as an exit path that does not exist.
    [Test]
    public void SubMachineWithNoOutgoingEdges_RendersNoOnExitLine()
    {
        var root = _ctrl.layers[0].stateMachine;
        root.AddStateMachine("Sub").AddState("Inner");

        string report = ReadReport(_ctrl);

        StringAssert.DoesNotContain("onExit", report);
    }

    // ----- Blend-tree child: empty vs broken (F11) --------------------------------------------------
    //
    // Before the fix, AppendBlendTree printed a bare "(empty)" for any null child motion — a dangling child
    // (asset deleted) was indistinguishable from an intentionally-empty slot. These two tests pin both poles;
    // the EMPTY case also guards the "m_Childs" serialized key — a wrong key nulls the child property, so the
    // empty child would render the loud "(broken: …unreadable)" fallback instead of "(empty)".

    [Test]
    public void EmptyBlendTreeChild_RendersEmpty()
    {
        _ctrl.AddParameter("blend", AnimatorControllerParameterType.Float);
        var sm = _ctrl.layers[0].stateMachine;
        var st = sm.AddState("Blend");
        var bt = new BlendTree { name = "bt", blendType = BlendTreeType.Simple1D, blendParameter = "blend" };
        AssetDatabase.AddObjectToAsset(bt, _ctrl);
        st.motion = bt;
        bt.AddChild((Motion)null, 0f);
        var clip = new AnimationClip { name = "live" };
        AssetDatabase.AddObjectToAsset(clip, _ctrl);
        bt.AddChild(clip, 1f);
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string report = ReadReport(_ctrl);
        StringAssert.Contains("(empty)", report);
        StringAssert.Contains("`live`", report);
    }

    [Test]
    public void DanglingBlendTreeChild_RendersBroken_NotEmpty()
    {
        _ctrl.AddParameter("blend", AnimatorControllerParameterType.Float);
        var sm = _ctrl.layers[0].stateMachine;
        var st = sm.AddState("Blend");
        var bt = new BlendTree { name = "bt", blendType = BlendTreeType.Simple1D, blendParameter = "blend" };
        AssetDatabase.AddObjectToAsset(bt, _ctrl);
        st.motion = bt;
        var clip = new AnimationClip { name = "doomed" };
        AssetDatabase.AddObjectToAsset(clip, _ctrl);
        bt.AddChild(clip, 0f);
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        // Rewrite ONLY the blend-tree child's motion ref (the one pointing at the sub-asset clip) into
        // a dangling external guid, then reimport. Targeting the clip's exact local fileID leaves the
        // state->blendtree link intact — so the tree still expands and the CHILD path is truly
        // exercised (a blanket m_Motion rewrite would also break the state motion and pass vacuously).
        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipLocalId);
        string yaml = File.ReadAllText(path);
        yaml = Regex.Replace(yaml, @"m_Motion: \{fileID: " + clipLocalId + @"\}",
            "m_Motion: {fileID: 7400000, guid: deadbeefdeadbeefdeadbeefdeadbeef, type: 2}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        string report = ReadReport(reloaded);
        // Pin the CHILD cell directly, not merely the top-level "Broken motion GUIDs" list —
        // RecoverDanglingMotionGuids fills that list from the raw YAML regardless of how the child
        // renders, so asserting the guid alone is vacuous. The @0 child must render broken, not (empty).
        StringAssert.Contains("@0 (broken: guid=deadbeefdeadbeefdeadbeefdeadbeef)", report);
        StringAssert.DoesNotContain("@0 (empty)", report);
    }

    // ----- Live-reachable vs orphan-only broken motions (F26) + per-layer states=N (F27) ------------
    //
    // Before F26 the header listed every dangling m_Motion guid in the controller YAML — including residue on
    // states no live layer reaches — so a residue-heavy controller read as N broken motions. Before F27 a
    // 0-state layer rendered as a blank section indistinguishable from a truncated report.

    // F27: a fresh controller's single "Base Layer" has an empty state machine → states=0, not a blank
    // section. Adding a state moves it to states=1.
    [Test]
    public void ZeroStateLayer_ReportsStatesZero_NotBlank()
    {
        string report = ReadReport(_ctrl);
        StringAssert.Contains("states=0", report);
    }

    [Test]
    public void LayerStateCount_CountsStatesIncludingSubMachines()
    {
        var sm = _ctrl.layers[0].stateMachine;
        sm.AddState("A");
        var sub = sm.AddStateMachine("Sub");
        sub.AddState("B");
        sub.AddState("C");
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string report = ReadReport(_ctrl);
        StringAssert.Contains("states=3", report); // A + B + C (recurses the sub-state-machine)
    }

    // F26 regression: a synced layer's dangling OVERRIDE motion (stored in the controller's main-object
    // block, not a state block) must land under live-reachable, not be mislabeled orphan residue.
    [Test]
    public void BrokenMotions_SyncedLayerOverride_isLiveReachable_notOrphan()
    {
        const string OvGuid = "11112222333344445555666677778888";

        _ctrl.layers[0].stateMachine.AddState("Src");
        _ctrl.AddLayer("Synced");
        var ls = _ctrl.layers;
        ls[1].syncedLayerIndex = 0; // synced to the base layer
        _ctrl.layers = ls;
        var srcState = _ctrl.layers[0].stateMachine.states[0].state;
        var ov = new AnimationClip { name = "override" };
        AssetDatabase.AddObjectToAsset(ov, _ctrl);

        // Write the synced-layer override motion directly (the typed SetOverrideMotion on a re-fetched layer
        // does not persist): m_AnimatorLayers[1].m_Motions[0] = { m_State: srcState, m_Motion: ov }.
        var so = new SerializedObject(_ctrl);
        var motions = so.FindProperty("m_AnimatorLayers").GetArrayElementAtIndex(1).FindPropertyRelative("m_Motions");
        Assert.IsNotNull(motions, "synced layer must expose m_Motions");
        motions.arraySize = 1;
        var e0 = motions.GetArrayElementAtIndex(0);
        var stateProp = e0.FindPropertyRelative("m_State");
        var motionProp = e0.FindPropertyRelative("m_Motion");
        Assert.IsNotNull(stateProp, "override entry must expose m_State");
        Assert.IsNotNull(motionProp, "override entry must expose m_Motion");
        stateProp.objectReferenceValue = srcState;
        motionProp.objectReferenceValue = ov;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ov, out _, out long ovLocalId);
        string yaml = File.ReadAllText(path);
        string ovRef = "m_Motion: {fileID: " + ovLocalId + "}";
        StringAssert.Contains(ovRef, yaml, "precondition: override motion serialized into the controller's main block");
        yaml = yaml.Replace(ovRef, "m_Motion: {fileID: 7400000, guid: " + OvGuid + ", type: 2}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        string report = ReadReport(reloaded);
        int liveHdr = report.IndexOf("live-reachable");
        int orphanHdr = report.IndexOf("orphan-only");
        int ovAt = report.IndexOf(OvGuid);
        Assert.That(ovAt, Is.GreaterThan(liveHdr).And.LessThan(orphanHdr),
            "synced-layer override break must be live-reachable, not orphan residue");
    }

    // F26 regression: a dangling motion whose INNER fileID is negative (an FBX-embedded clip's hash-derived
    // localID, referenced type: 3) must still be collected — not dropped by a \d+ that can't match the sign.
    [Test]
    public void BrokenMotions_NegativeFileID_isCollected_notDropped()
    {
        const string NegGuid = "abcdef01abcdef01abcdef01abcdef01";

        var sm = _ctrl.layers[0].stateMachine;
        var st = sm.AddState("Live");
        var clip = new AnimationClip { name = "doomed" };
        AssetDatabase.AddObjectToAsset(clip, _ctrl);
        st.motion = clip;
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipLocalId);
        string yaml = File.ReadAllText(path);
        // Negative inner fileID + type: 3 — the FBX-embedded-clip dangling shape.
        yaml = Regex.Replace(yaml, @"m_Motion: \{fileID: " + clipLocalId + @"\}",
            "m_Motion: {fileID: -8823450917, guid: " + NegGuid + ", type: 3}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        string report = ReadReport(reloaded);
        StringAssert.Contains(NegGuid, report); // pre-fix: dropped entirely (\d+ misses the '-')
    }

    // F26: one live state carries a dangling motion (guid A); an orphan AnimatorState block carrying a
    // different dangling motion (guid B) is appended to the YAML but wired into no layer. The split must
    // list A under live-reachable and B under orphan-only — never both under one undifferentiated count.
    [Test]
    public void BrokenMotions_SplitLiveReachableFromOrphanResidue()
    {
        const string LiveGuid   = "deadbeefdeadbeefdeadbeefdeadbeef";
        const string OrphanGuid = "0000000000000000000000000000dead";

        // Live state with a real sub-asset clip motion, then dangle exactly that motion ref.
        var sm = _ctrl.layers[0].stateMachine;
        var st = sm.AddState("Live");
        var clip = new AnimationClip { name = "doomed" };
        AssetDatabase.AddObjectToAsset(clip, _ctrl);
        st.motion = clip;
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipLocalId);
        string yaml = File.ReadAllText(path);
        yaml = Regex.Replace(yaml, @"m_Motion: \{fileID: " + clipLocalId + @"\}",
            "m_Motion: {fileID: 7400000, guid: " + LiveGuid + ", type: 2}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        // Append an orphan AnimatorState block — a dangling motion no live layer references. NOT
        // reimported: the in-memory controller (what the walk sees) stays clean, while RecoverDanglingMotions
        // reads this residue off disk. Minimal block: the parser keys only on the &fileID header + m_Motion.
        File.AppendAllText(path,
            "\n--- !u!1102 &9111111111\nAnimatorState:\n  m_Name: OrphanResidue\n" +
            "  m_Motion: {fileID: 7400000, guid: " + OrphanGuid + ", type: 2}\n");

        string summaryLine = ReportController.Run(reloaded);
        string report = File.ReadAllText(Track(summaryLine));

        int liveHdr   = report.IndexOf("live-reachable");
        int orphanHdr  = report.IndexOf("orphan-only");
        int liveGuidAt = report.IndexOf(LiveGuid);
        int orphanGuidAt = report.IndexOf(OrphanGuid);

        Assert.That(liveHdr, Is.GreaterThanOrEqualTo(0), "live-reachable header missing");
        Assert.That(orphanHdr, Is.GreaterThan(liveHdr), "orphan-only header must follow live-reachable");
        // Live guid sits in the live-reachable section (between the two headers); orphan guid after orphan header.
        Assert.That(liveGuidAt, Is.GreaterThan(liveHdr).And.LessThan(orphanHdr), "live guid not in live-reachable section");
        Assert.That(orphanGuidAt, Is.GreaterThan(orphanHdr), "orphan guid not in orphan-only section");
        // Summary one-liner (the returned string, not the artifact body) carries the at-a-glance split.
        StringAssert.Contains("brokenMotions=1live/1orphan", summaryLine);
    }
}
