using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

public class ReportControllerTests
{
    private const string Dir = "Assets/Agent/_report_controller_test";

    // Record what the door wrote and delete exactly that; a glob over `controller_*.md` would also sweep
    // snapshots the operator took by hand.
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

    // Per-test path: several tests rewrite the controller's YAML on disk and reimport it, so a shared asset
    // path would carry one test's dangling guids into the next.
    [SetUp]
    public void SetUp() => _ctrl = AnimatorController.CreateAnimatorControllerAtPath(
        Dir + "/" + TestContext.CurrentContext.Test.MethodName + ".controller");

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
    // These pin each outgoing edge's condition and target as well as its presence: a rendered edge with no
    // condition is indistinguishable from an unconditional one.

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

        // Assert the destination token: a bare `Contains("Exit")` is satisfied by the "onExit" label itself.
        StringAssert.Contains("- `Sub` (state machine) onExit → Exit [unconditional]", report);
    }

    [Test]
    public void SubMachineWithNoOutgoingEdges_RendersNoOnExitLine()
    {
        var root = _ctrl.layers[0].stateMachine;
        root.AddStateMachine("Sub").AddState("Inner");

        string report = ReadReport(_ctrl);

        StringAssert.DoesNotContain("onExit", report);
    }

    // ----- Blend-tree child: empty vs broken --------------------------------------------------------
    //
    // The empty case also guards the "m_Childs" serialized key: a wrong key nulls the child property, so the
    // empty child would render the "(broken: …unreadable)" fallback instead of "(empty)".

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

        // Dangle only the child's motion ref, targeted by the clip's local fileID. A blanket m_Motion
        // rewrite would also break the state->blendtree link, so the tree would not expand and the test
        // would pass without exercising the child path.
        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipLocalId);
        string yaml = File.ReadAllText(path);
        yaml = Regex.Replace(yaml, @"m_Motion: \{fileID: " + clipLocalId + @"\}",
            "m_Motion: {fileID: 7400000, guid: deadbeefdeadbeefdeadbeefdeadbeef, type: 2}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        string report = ReadReport(reloaded);
        // Pin the child cell: RecoverDanglingMotions fills the top-level "Broken motion GUIDs" list from
        // the raw YAML however the child renders, so asserting the guid alone is vacuous.
        StringAssert.Contains("@0 (broken: guid=deadbeefdeadbeefdeadbeefdeadbeef)", report);
        StringAssert.DoesNotContain("@0 (empty)", report);
    }

    // ----- Live-reachable vs orphan-only broken motions, and per-layer states=N ---------------------

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

    // A synced layer's override motion is stored in the controller's main-object block, not a state block.
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

    // The override API returns null for a dangling override exactly as for none, so the cell used to fall back
    // to the source's live clip and print "(inherited source)" on what plays nothing at runtime.
    [Test]
    public void SyncedLayer_DanglingOverride_RendersBroken_NotInheritedSource()
    {
        const string OvGuid = "aaaa1111bbbb2222cccc3333dddd4444";

        var src = _ctrl.layers[0].stateMachine.AddState("Src");
        var live = new AnimationClip { name = "sourceClip" };
        AssetDatabase.AddObjectToAsset(live, _ctrl);
        src.motion = live;
        _ctrl.AddLayer("Synced");
        var ls = _ctrl.layers;
        ls[1].syncedLayerIndex = 0;
        _ctrl.layers = ls;
        var ov = new AnimationClip { name = "override" };
        AssetDatabase.AddObjectToAsset(ov, _ctrl);

        var so = new SerializedObject(_ctrl);
        var motions = so.FindProperty("m_AnimatorLayers").GetArrayElementAtIndex(1).FindPropertyRelative("m_Motions");
        motions.arraySize = 1;
        var e0 = motions.GetArrayElementAtIndex(0);
        e0.FindPropertyRelative("m_State").objectReferenceValue = src;
        e0.FindPropertyRelative("m_Motion").objectReferenceValue = ov;
        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ov, out _, out long ovLocalId);
        string yaml = File.ReadAllText(path);
        string ovRef = "m_Motion: {fileID: " + ovLocalId + "}";
        StringAssert.Contains(ovRef, yaml, "precondition: override motion serialized into the controller's main block");
        File.WriteAllText(path, yaml.Replace(ovRef, "m_Motion: {fileID: 7400000, guid: " + OvGuid + ", type: 2}"));
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        string report = ReadReport(AssetDatabase.LoadAssetAtPath<AnimatorController>(path));
        int syncedAt = report.IndexOf("_synced layer");
        Assert.That(syncedAt, Is.GreaterThanOrEqualTo(0), "precondition: the layer reads as synced");
        string synced = report.Substring(syncedAt);
        StringAssert.Contains("motion: (broken: dangling synced override guid=" + OvGuid + ")", synced);
        StringAssert.DoesNotContain("(inherited source)", synced);
        StringAssert.Contains("`sourceClip`", report.Substring(0, syncedAt), "the source layer's own cell is untouched");
    }

    // A synced state with no override inherits the source state's motion, so its dangling reference is broken
    // there too. The source layer's block renders the same cell, so the assert reads the synced block alone.
    [Test]
    public void SyncedLayer_DanglingInheritedSource_RendersBroken_NotEmpty()
    {
        const string SrcGuid = "99990000aaaabbbbccccddddeeeeffff";

        var st = _ctrl.layers[0].stateMachine.AddState("Src");
        var clip = new AnimationClip { name = "doomed" };
        AssetDatabase.AddObjectToAsset(clip, _ctrl);
        st.motion = clip;
        _ctrl.AddLayer("Synced");
        var ls = _ctrl.layers;
        ls[1].syncedLayerIndex = 0;
        _ctrl.layers = ls;
        EditorUtility.SetDirty(_ctrl); AssetDatabase.SaveAssets();

        string path = AssetDatabase.GetAssetPath(_ctrl);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out _, out long clipLocalId);
        string yaml = File.ReadAllText(path);
        yaml = Regex.Replace(yaml, @"m_Motion: \{fileID: " + clipLocalId + @"\}",
            "m_Motion: {fileID: 7400000, guid: " + SrcGuid + ", type: 2}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        string report = ReadReport(reloaded);
        int syncedAt = report.IndexOf("_synced layer");
        Assert.That(syncedAt, Is.GreaterThanOrEqualTo(0), "precondition: the layer reads as synced");
        string synced = report.Substring(syncedAt);
        StringAssert.Contains("motion: (broken: guid=" + SrcGuid + ")", synced);
        StringAssert.DoesNotContain("motion: (empty)", synced);
    }

    // A dangling motion whose inner fileID is negative (an FBX-embedded clip's hash-derived localID, referenced
    // type: 3) must not be dropped by a \d+ that cannot match the sign.
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
        yaml = Regex.Replace(yaml, @"m_Motion: \{fileID: " + clipLocalId + @"\}",
            "m_Motion: {fileID: -8823450917, guid: " + NegGuid + ", type: 3}");
        File.WriteAllText(path, yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);

        string report = ReadReport(reloaded);
        StringAssert.Contains(NegGuid, report);
    }

    [Test]
    public void BrokenMotions_SplitLiveReachableFromOrphanResidue()
    {
        const string LiveGuid   = "deadbeefdeadbeefdeadbeefdeadbeef";
        const string OrphanGuid = "0000000000000000000000000000dead";

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

        // Not reimported: the in-memory controller (what the walk sees) stays clean, while
        // RecoverDanglingMotions reads this residue off disk. The parser keys only on the &fileID header and
        // m_Motion, so the block can be this small.
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
        Assert.That(liveGuidAt, Is.GreaterThan(liveHdr).And.LessThan(orphanHdr), "live guid not in live-reachable section");
        Assert.That(orphanGuidAt, Is.GreaterThan(orphanHdr), "orphan guid not in orphan-only section");
        StringAssert.Contains("brokenMotions=1live/1orphan", summaryLine);
    }
}
