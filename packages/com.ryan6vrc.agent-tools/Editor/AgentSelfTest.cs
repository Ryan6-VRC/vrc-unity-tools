using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Headless end-to-end proof of the observe -> modify -> verify loop.
    /// Run via:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///     -executeMethod Ryan6Vrc.AgentTools.Editor.AgentSelfTest.RunHeadless -logFile &lt;log&gt;
    /// Creates a test object, snapshots it, mutates it, snapshots again. Two snapshot
    /// JSONs land in Assets/Agent/Snapshots/ for the agent to diff. Exit code 0 = pass.
    /// Also available in the GUI under Tools/Agent/Self Test (observe-modify-verify).
    /// </summary>
    public static class AgentSelfTest
    {
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

                Debug.Log("[AgentSelfTest] PASS — wrote before/after snapshots to Assets/Agent/Snapshots/");
                if (exitOnFinish) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[AgentSelfTest] FAIL — " + e);
                if (exitOnFinish) EditorApplication.Exit(1);
            }
        }
    }
}
