using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    [AgentTool]
    public static class RemapMaterials
    {
        /// Keys/values are material asset paths; a from-key path remaps every material sharing that path (a standalone .mat = exactly one; embedded FBX sub-assets all share the FBX path, so extract them to .mat first). A null/empty 'to' value clears the slot (destructive).
        public static string Run(GameObject root, IDictionary<string, string> replacements, bool whatIf = false)
        {
            if (root == null) return "[RemapMaterials] FAIL: root is null";
            if (replacements == null || replacements.Count == 0) return "[RemapMaterials] FAIL: no replacements given";

            foreach (var k in replacements.Keys)
                if (string.IsNullOrEmpty(k))
                    return "[RemapMaterials] FAIL: a replacement key (from-material asset path) is empty — an embedded/instance material with no standalone asset can't be remapped by path";

            var fromSet = new HashSet<string>(replacements.Keys);
            foreach (var to in replacements.Values)
                if (!string.IsNullOrEmpty(to) && fromSet.Contains(to))
                    return $"[RemapMaterials] FAIL: overlapping map (a 'to' is also a 'from': {to}) — not idempotent";

            var resolved = new Dictionary<string, Material>();
            foreach (var kv in replacements)
            {
                Material toMat = null;
                if (!string.IsNullOrEmpty(kv.Value))
                {
                    toMat = AssetDatabase.LoadAssetAtPath<Material>(kv.Value);
                    if (toMat == null) return $"[RemapMaterials] FAIL: 'to' material not found at {kv.Value}";
                }
                resolved[kv.Key] = toMat;
            }

            var renderers = new List<Renderer>();
            renderers.AddRange(root.GetComponentsInChildren<MeshRenderer>(true));
            renderers.AddRange(root.GetComponentsInChildren<SkinnedMeshRenderer>(true));

            // Fail loud on the ambiguity the window also guards: two DISTINCT materials sharing one
            // matched asset path make a path-keyed remap ambiguous (embedded FBX sub-assets).
            var firstAtPath = new Dictionary<string, Material>();
            foreach (var r in renderers)
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    string p = AssetDatabase.GetAssetPath(m);
                    if (string.IsNullOrEmpty(p) || !resolved.ContainsKey(p)) continue;
                    if (firstAtPath.TryGetValue(p, out Material other))
                    {
                        if (other != m)
                            return $"[RemapMaterials] FAIL: two distinct materials share asset path '{p}' ('{other.name}' and '{m.name}', embedded sub-assets) — extract to standalone .mat first";
                    }
                    else firstAtPath[p] = m;
                }

            var hits = new Dictionary<string, int>();
            foreach (var k in replacements.Keys) hits[k] = 0;
            int slots = 0, touched = 0;

            foreach (var r in renderers)
            {
                Material[] shared = r.sharedMaterials;
                Material[] next = (Material[])shared.Clone();
                bool changed = false;
                for (int i = 0; i < shared.Length; i++)
                {
                    string curPath = shared[i] != null ? AssetDatabase.GetAssetPath(shared[i]) : null;
                    if (!string.IsNullOrEmpty(curPath) && resolved.TryGetValue(curPath, out Material mapped))
                    {
                        hits[curPath]++;
                        slots++;
                        if (!whatIf) { next[i] = mapped; changed = true; }
                    }
                }
                if (!whatIf && changed)
                {
                    Undo.RecordObject(r, "Remap Materials");
                    r.sharedMaterials = next;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(r);
                    EditorUtility.SetDirty(r);
                    touched++;
                }
            }

            var zero = new List<string>();
            foreach (var kv in hits) if (kv.Value == 0) zero.Add(kv.Key);
            string zn = zero.Count > 0 ? $"; 0-match: {string.Join(", ", zero)}" : "";
            string rootPath = GetHierarchyPath(root.transform);

            if (whatIf)
                return $"[RemapMaterials] (whatIf) would remap {slots} slot(s){zn}, root {rootPath} => PASS";
            return $"[RemapMaterials] remapped {slots} slot(s) across {touched} renderer(s){zn}, root {rootPath} => PASS";
        }

        private static string GetHierarchyPath(Transform t)
        {
            var stack = new Stack<string>();
            for (Transform c = t; c != null; c = c.parent) stack.Push(c.name);
            return string.Join("/", stack.ToArray());
        }
    }
}
