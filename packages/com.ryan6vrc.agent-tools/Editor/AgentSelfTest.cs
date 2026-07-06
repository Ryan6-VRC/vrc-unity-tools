using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Headless end-to-end proof of the observe -> modify -> verify loop, plus the followAssets
    /// asset-expansion contract.
    /// Run via:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///     -executeMethod Ryan6Vrc.AgentTools.Editor.AgentSelfTest.RunHeadless -logFile &lt;log&gt;
    /// Creates a test object, snapshots it, mutates it, snapshots again (two JSONs land in
    /// Assets/Agent/Snapshots/ for the agent to diff), then drives followAssets:true over a small
    /// ScriptableObject graph and asserts cycle-safety, the asset-hop depth cap, the walk-wide budget
    /// signal, and path+GUID naming. Exit code 0 = pass; any assertion failure throws -> Exit(1).
    /// Also available in the GUI under Tools/Agent/Self Test (observe-modify-verify).
    /// </summary>
    public static class AgentSelfTest
    {
        // Fixtures are dependency-free (no VRChat/MA/VRCFury). AgentTestNode's self-referential children
        // array builds every graph shape below. The two fixture types split on a Unity constraint:
        //   - AgentTestNode is CreateAsset'd, so it MUST have a MonoScript (AssetDatabase can neither
        //     persist nor reload a MonoScript-less ScriptableObject) -> its own name-matched file.
        //   - AgentTestHolder is only ever AddComponent'd to a transient scene object (never saved), and
        //     a MonoBehaviour WITH a MonoScript in an Editor assembly is rejected by AddComponent
        //     ("editor script ... needs to be outside the 'Editor' folder"), so it must have NO
        //     MonoScript -> it stays a nested type here.
        private sealed class AgentTestHolder : MonoBehaviour { public AgentTestNode root; }

        private const string ScratchRoot = "Assets/Agent/Scratch";
        private const string FixtureFolder = ScratchRoot + "/AgentInspectorAssetTest";

        [MenuItem("Tools/Agent/Self Test (observe-modify-verify)")]
        public static void RunFromMenu() { Run(exitOnFinish: false); }

        public static void RunHeadless() { Run(exitOnFinish: true); }

        private static void Run(bool exitOnFinish)
        {
            try
            {
                // In interactive (GUI) mode, don't silently discard the user's open scene —
                // NewScene(..., Single) would replace it. Headless runs have no scene to protect.
                if (!exitOnFinish && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                // --- create a test subject ---
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "AgentTestCube";
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = 2.0f;
                rb.useGravity = true;
                go.transform.position = new Vector3(0f, 1f, 0f);

                Selection.objects = new UnityEngine.Object[] { go };

                // --- OBSERVE (before) ---
                AgentInspector.SnapshotSelection();

                // --- MODIFY ---
                rb.mass = 7.5f;
                rb.useGravity = false;
                go.transform.position = new Vector3(1f, 2f, 3f);
                go.name = "AgentTestCube_Modified";
                Selection.objects = new UnityEngine.Object[] { go };

                // --- OBSERVE (after) ---
                AgentInspector.SnapshotSelection();

                // --- followAssets asset-expansion contract ---
                RunAssetExpansionCases();

                Debug.Log("[AgentSelfTest] PASS — observe/modify/verify + asset-expansion cases green");
                if (exitOnFinish) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[AgentSelfTest] FAIL — " + e);
                if (exitOnFinish) EditorApplication.Exit(1);
            }
        }

        // ----- Asset-expansion cases (each its own Snapshot call => fresh WalkState => independent budget) -----

        private static void RunAssetExpansionCases()
        {
            AssetDatabase.Refresh(); // import any pre-existing physical Scratch folder before CreateFolder
            EnsureFolder("Assets", "Agent");
            EnsureFolder("Assets/Agent", "Scratch");
            if (AssetDatabase.IsValidFolder(FixtureFolder)) AssetDatabase.DeleteAsset(FixtureFolder);
            AssetDatabase.CreateFolder(ScratchRoot, "AgentInspectorAssetTest");

            GameObject holderGo = null;
            try
            {
                holderGo = new GameObject("AgentAssetTestHolder");
                var holder = holderGo.AddComponent<AgentTestHolder>();
                // Case 1 — cycle-safety + naming (A <-> B). B expands once; the revisit of A stubs
                // with alreadyDumped; both expanded refs carry assetPath + guid + fileId.
                {
                    var a = MakeNode("A", "ALABEL_UNIQUE");
                    var b = MakeNode("B", "BLABEL_ONCE");
                    a.children = new[] { b };
                    b.children = new[] { a };
                    holder.root = a;
                    var json = SnapshotJson("AgentAssetTestHolder");
                    Assert(Count(json, "\"alreadyDumped\"") == 1, "cycle: exactly one alreadyDumped stub (got " + Count(json, "\"alreadyDumped\"") + ")");
                    Assert(Count(json, "BLABEL_ONCE") == 1, "cycle: B expanded exactly once (got " + Count(json, "BLABEL_ONCE") + ")");
                    // Prove the path+GUID naming on the FOLLOWED (expanded) stubs specifically — window
                    // each from its name to its own "fields" opener. Scoping to the expansion (not a bare
                    // json.Contains) means these fail if expansion regresses, rather than passing on the
                    // unconditional fold-in alone. A's first occurrence is the expanded root (the revisit
                    // stub, carrying alreadyDumped instead of fields, appears later and deeper).
                    string aWin = Window(json, "\"name\": \"A\"", "\"fields\"");
                    Assert(aWin.Contains("\"assetPath\"") && aWin.Contains("\"guid\"") && aWin.Contains("\"fileId\""),
                        "cycle: expanded A is named by assetPath+guid+fileId");
                    string bWin = Window(json, "\"name\": \"B\"", "\"fields\"");
                    Assert(bWin.Contains("\"assetPath\"") && bWin.Contains("\"guid\"") && bWin.Contains("\"fileId\""),
                        "cycle: expanded B is named by assetPath+guid+fileId");
                }

                // Case 2 — asset-hop depth cap. Linear chain of MaxAssetDepth+2 nodes via children[0];
                // the walk is cut at the asset-hop bound, not silently dropped.
                {
                    int n = AgentInspector.MaxAssetDepth + 2;
                    var chain = new AgentTestNode[n];
                    for (int i = 0; i < n; i++) chain[i] = MakeNode("chain" + i, "CHAIN" + i);
                    for (int i = 0; i < n - 1; i++) chain[i].children = new[] { chain[i + 1] };
                    holder.root = chain[0];
                    var json = SnapshotJson("AgentAssetTestHolder");
                    Assert(json.Contains("\"assetDepthCapped\""), "depth: chain cut with assetDepthCapped at MaxAssetDepth");
                }

                // Case 3 — walk-wide budget signal. Distinct expandable assets exceed MaxExpandedAssets,
                // but the graph is a shallow TREE (root -> groups -> leaves) so no single children array
                // approaches WriteArray's element cap. This isolates the signal to BUDGET exhaustion:
                // a flat array of MaxExpandedAssets+1 leaves would also hit the array cap, so it couldn't
                // distinguish which bound fired (and would break silently if the two constants ever
                // diverged). leavesPerGroup is kept well below the array cap so no array truncates.
                {
                    int leafCount = AgentInspector.MaxExpandedAssets + 1; // more leaves than the budget alone holds
                    const int leavesPerGroup = 8;                          // « WriteArray's element cap; no array truncates
                    int groupCount = (leafCount + leavesPerGroup - 1) / leavesPerGroup;
                    var root = MakeNode("fanRoot", "FANROOT");
                    var groups = new AgentTestNode[groupCount];
                    int made = 0;
                    for (int g = 0; g < groupCount; g++)
                    {
                        groups[g] = MakeNode("group" + g, "GROUP" + g);
                        int take = Math.Min(leavesPerGroup, leafCount - made);
                        var leaves = new AgentTestNode[take];
                        for (int i = 0; i < take; i++) { leaves[i] = MakeNode("leaf" + made, "LEAF" + made); made++; }
                        groups[g].children = leaves;
                    }
                    root.children = groups;
                    holder.root = root;
                    var json = SnapshotJson("AgentAssetTestHolder");
                    Assert(json.Contains("\"budgetSkipped\""), "budget: inline budgetSkipped marker present");
                    Assert(json.Contains("\"assetsTruncated\""), "budget: top-level assetsTruncated present");
                }
            }
            finally
            {
                if (holderGo != null) UnityEngine.Object.DestroyImmediate(holderGo);
                AssetDatabase.DeleteAsset(FixtureFolder);
                AssetDatabase.Refresh();
            }
        }

        // ----- Fixture + assertion helpers ------------------------------------------------------

        // CreateAsset gives each node a real path + GUID immediately (the filter needs a non-empty
        // asset path); the followAssets walk reads the live in-memory instance, so no SaveAssets is
        // needed — children/root are wired on the managed objects just below and read straight back.
        private static AgentTestNode MakeNode(string assetName, string label)
        {
            var node = ScriptableObject.CreateInstance<AgentTestNode>();
            node.label = label;
            AssetDatabase.CreateAsset(node, FixtureFolder + "/" + assetName + ".asset");
            return node;
        }

        private static string SnapshotJson(string hierarchyPath)
        {
            var summary = AgentInspector.Snapshot(hierarchyPath, includeChildren: true, followAssets: true);
            int idx = summary.IndexOf("log=", StringComparison.Ordinal);
            if (idx < 0) throw new Exception("Snapshot returned no log= trailer: " + summary);
            var file = summary.Substring(idx + 4).Trim();
            return File.ReadAllText(file);
        }

        private static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static int Count(string haystack, string needle)
        {
            int c = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; }
            return c;
        }

        // Substring from the first occurrence of `start` up to (and including) the first `end` after it.
        // Used to scope an assertion to one ref stub's own fields — e.g. name -> its "fields" opener.
        private static string Window(string s, string start, string end)
        {
            int a = s.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return "";
            int b = s.IndexOf(end, a, StringComparison.Ordinal);
            if (b < 0) return "";
            return s.Substring(a, b - a + end.Length);
        }

        private static void Assert(bool cond, string msg)
        {
            if (!cond) throw new Exception("[AgentSelfTest] assertion failed: " + msg);
        }
    }
}
