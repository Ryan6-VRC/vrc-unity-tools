using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Headless end-to-end proof of the observe -> modify -> verify loop, plus the followAssets
    /// asset-expansion contract. Run via:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///     -executeMethod Ryan6Vrc.AgentTools.Editor.AgentSelfTest.RunHeadless -logFile &lt;log&gt;
    /// Exit code 0 = pass, 1 = any failure. The before and after selection snapshots land in
    /// Assets/Agent/Snapshots/ for diffing.
    /// </summary>
    public static class AgentSelfTest
    {
        // AddComponent rejects a MonoBehaviour WITH a MonoScript in an Editor assembly ("editor script
        // ... needs to be outside the 'Editor' folder"), so this holder must have NO MonoScript and stays
        // a nested type; it is only ever added to a transient scene object, never saved. AgentTestNode
        // needs the opposite; see its file.
        private sealed class AgentTestHolder : MonoBehaviour { public AgentTestNode root; }

        private const string ScratchRoot = "Assets/Agent/Scratch";
        private const string FixtureFolder = ScratchRoot + "/AgentInspectorAssetTest";

        public static void RunHeadless()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "AgentTestCube";
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = 2.0f;
                rb.useGravity = true;
                go.transform.position = new Vector3(0f, 1f, 0f);

                Selection.objects = new UnityEngine.Object[] { go };

                AgentInspector.SnapshotSelection();

                rb.mass = 7.5f;
                rb.useGravity = false;
                go.transform.position = new Vector3(1f, 2f, 3f);
                go.name = "AgentTestCube_Modified";
                Selection.objects = new UnityEngine.Object[] { go };

                AgentInspector.SnapshotSelection();

                RunAssetExpansionCases();

                Debug.Log("[AgentSelfTest] PASS — observe/modify/verify + asset-expansion cases green");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[AgentSelfTest] FAIL — " + e);
                EditorApplication.Exit(1);
            }
        }

        // Each case is its own Snapshot call, so it gets a fresh WalkState and an independent budget.
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
                {
                    var a = MakeNode("A", "ALABEL_UNIQUE");
                    var b = MakeNode("B", "BLABEL_ONCE");
                    a.children = new[] { b };
                    b.children = new[] { a };
                    holder.root = a;
                    var json = SnapshotJson("AgentAssetTestHolder");
                    Assert(Count(json, "\"alreadyDumped\"") == 1, "cycle: exactly one alreadyDumped stub (got " + Count(json, "\"alreadyDumped\"") + ")");
                    Assert(Count(json, "BLABEL_ONCE") == 1, "cycle: B expanded exactly once (got " + Count(json, "BLABEL_ONCE") + ")");
                    // Window each expanded stub from its name to its own "fields" opener, not a bare
                    // json.Contains: these then fail if expansion regresses, rather than passing on the
                    // unconditional identity fold-in. A's first occurrence is the expanded root; the revisit
                    // stub, carrying alreadyDumped instead of fields, appears later and deeper.
                    string aWin = Window(json, "\"name\": \"A\"", "\"fields\"");
                    Assert(aWin.Contains("\"assetPath\"") && aWin.Contains("\"guid\"") && aWin.Contains("\"fileId\""),
                        "cycle: expanded A is named by assetPath+guid+fileId");
                    string bWin = Window(json, "\"name\": \"B\"", "\"fields\"");
                    Assert(bWin.Contains("\"assetPath\"") && bWin.Contains("\"guid\"") && bWin.Contains("\"fileId\""),
                        "cycle: expanded B is named by assetPath+guid+fileId");
                }

                {
                    int n = AgentInspector.MaxAssetDepth + 2;
                    var chain = new AgentTestNode[n];
                    for (int i = 0; i < n; i++) chain[i] = MakeNode("chain" + i, "CHAIN" + i);
                    for (int i = 0; i < n - 1; i++) chain[i].children = new[] { chain[i + 1] };
                    holder.root = chain[0];
                    var json = SnapshotJson("AgentAssetTestHolder");
                    Assert(json.Contains("\"assetDepthCapped\""), "depth: chain cut with assetDepthCapped at MaxAssetDepth");
                }

                // A shallow TREE (root -> groups -> leaves), because a flat array of MaxExpandedAssets+1
                // leaves would also hit WriteArray's element cap and could not show which bound fired.
                {
                    int leafCount = AgentInspector.MaxExpandedAssets + 1;
                    const int leavesPerGroup = 8;                          // far below WriteArray's element cap
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

        // The followAssets walk reads the live in-memory instance, so children and root wired on the
        // managed objects need no SaveAssets.
        private static AgentTestNode MakeNode(string assetName, string label)
        {
            var node = ScriptableObject.CreateInstance<AgentTestNode>();
            node.label = label;
            AssetDatabase.CreateAsset(node, FixtureFolder + "/" + assetName + ".asset");
            return node;
        }

        private static string SnapshotJson(string hierarchyPath)
        {
            var summary = AgentInspector.Run(hierarchyPath, includeChildren: true, followAssets: true);
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
