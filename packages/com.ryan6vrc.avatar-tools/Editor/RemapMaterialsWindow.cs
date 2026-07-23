using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    public class RemapMaterialsWindow : EditorWindow
    {
        [SerializeField] private GameObject rootObject;
        private readonly List<Material> fromMaterials = new List<Material>();
        private readonly List<Material> toMaterials = new List<Material>();
        private Vector2 scroll;

        [MenuItem("Tools/Atelier/Remap Materials")]
        private static void OpenWindow()
        {
            GetWindow<RemapMaterialsWindow>("Material Remap");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Material Remap", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scan a root to list the unique materials used beneath it, set a replacement for any of them, " +
                "then Apply to swap those materials across every renderer slot under the root.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            rootObject = (GameObject)EditorGUILayout.ObjectField("Root Object", rootObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) Scan();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan Materials From Root")) Scan();
                using (new EditorGUI.DisabledScope(fromMaterials.Count == 0))
                    if (GUILayout.Button("Apply Remap")) Apply();
            }

            EditorGUILayout.Space();

            if (rootObject == null)
            {
                EditorGUILayout.HelpBox("Assign a root object, then Scan.", MessageType.Warning);
                return;
            }
            if (fromMaterials.Count == 0)
            {
                EditorGUILayout.HelpBox("No materials found. Click 'Scan Materials From Root'.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"Materials ({fromMaterials.Count})", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (int i = 0; i < fromMaterials.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(fromMaterials[i] != null ? fromMaterials[i].name : "(null)", GUILayout.Width(160));
                    EditorGUILayout.LabelField("→", GUILayout.Width(18));
                    toMaterials[i] = (Material)EditorGUILayout.ObjectField(toMaterials[i], typeof(Material), false);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void Scan()
        {
            fromMaterials.Clear();
            toMaterials.Clear();
            if (rootObject == null) return;
            var seen = new HashSet<Material>();
            var rends = new List<Renderer>();
            rends.AddRange(rootObject.GetComponentsInChildren<MeshRenderer>(true));
            rends.AddRange(rootObject.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            foreach (var r in rends)
                foreach (var m in r.sharedMaterials)
                    if (m != null && seen.Add(m)) { fromMaterials.Add(m); toMaterials.Add(m); }
        }

        private void Apply()
        {
            var map = new Dictionary<string, string>();
            var pathOwner = new Dictionary<string, Material>();
            for (int i = 0; i < fromMaterials.Count; i++)
            {
                string fromP = AssetDatabase.GetAssetPath(fromMaterials[i]);
                if (string.IsNullOrEmpty(fromP))
                {
                    Debug.LogWarning($"[RemapMaterials] '{(fromMaterials[i] != null ? fromMaterials[i].name : "null")}' has no standalone asset path (embedded/instance) — cannot remap by path; skipping row.");
                    continue;
                }
                if (pathOwner.TryGetValue(fromP, out Material other) && other != fromMaterials[i])
                {
                    Debug.LogError($"[RemapMaterials] Two distinct materials share the asset path '{fromP}' ('{other.name}' and '{fromMaterials[i].name}', embedded sub-assets). Path-based remap can't tell them apart — aborting. Extract them to standalone .mat assets first.");
                    return;
                }
                pathOwner[fromP] = fromMaterials[i];
                if (toMaterials[i] == fromMaterials[i]) continue; // unchanged row
                string toP = toMaterials[i] != null ? AssetDatabase.GetAssetPath(toMaterials[i]) : "";
                map[fromP] = toP;
            }
            if (map.Count == 0) { Debug.Log("[RemapMaterials] no changed rows to apply."); return; }
            Debug.Log(RemapMaterials.Run(rootObject, map));
            Scan();
        }
    }
}
