using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// F26 (live-reachable vs orphan-only broken-motion split) + F27 (per-layer states=N). Before F26 the
// header listed every dangling m_Motion guid in the controller YAML — including residue on states no
// live layer reaches — so a residue-heavy controller read as N broken motions. Before F27 a 0-state
// layer rendered as a blank section indistinguishable from a truncated report.
public class ReportControllerLiveOrphanTests
{
    private const string Dir = "Assets/Agent/_live_orphan_test";
    private AnimatorController _ctrl;

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(Dir);
        _ctrl = AnimatorController.CreateAnimatorControllerAtPath(Dir + "/lo.controller");
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(Dir);

    private static string PathFrom(string summary)
    {
        int i = summary.IndexOf("log=");
        return i >= 0 ? summary.Substring(i + 4).Trim() : summary;
    }

    // F27: a fresh controller's single "Base Layer" has an empty state machine → states=0, not a blank
    // section. Adding a state moves it to states=1.
    [Test]
    public void ZeroStateLayer_ReportsStatesZero_NotBlank()
    {
        string report = File.ReadAllText(PathFrom(ReportController.Report(_ctrl)));
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

        string report = File.ReadAllText(PathFrom(ReportController.Report(_ctrl)));
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

        string report = File.ReadAllText(PathFrom(ReportController.Report(reloaded)));
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

        string report = File.ReadAllText(PathFrom(ReportController.Report(reloaded)));
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

        string summaryLine = ReportController.Report(reloaded);
        string report = File.ReadAllText(PathFrom(summaryLine));

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
