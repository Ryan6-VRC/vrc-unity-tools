using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Dumps GameObjects, their components and serialized fields to JSON under the snapshot directory, by a generic
    /// <c>SerializedObject</c> walk with no per-type code. That file is its only write.
    /// Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class AgentInspector
    {
        private const string SnapshotDir = RunLogFormat.SnapshotDir;
        private const int MaxDepth = 6;        // caps both property nesting and child-GameObject recursion
        private const int MaxArrayElements = 64;

        // Asset-expansion bounds, separate from MaxDepth. AgentSelfTest sizes its fixtures from these.
        internal const int MaxAssetDepth = 8;      // asset-hop depth: how many asset boundaries the walk has crossed
        internal const int MaxExpandedAssets = 64; // walk-wide expansion budget (one shared counter per Snapshot call)

        private sealed class WalkState
        {
            public bool FollowAssets;
            public readonly HashSet<string> Dumped = new HashSet<string>(); // "guid:localId" keys already inlined
            public int Budget;
            public int SkippedForBudget; // ref *sites* left unexpanded due to Budget==0 (walk-wide, signaled at top level)
        }

        // ----- Selection-driven entry points -----

        public static void SnapshotSelection()
        {
            var objs = Selection.gameObjects;
            if (objs == null || objs.Length == 0)
            {
                Debug.LogWarning("[AgentInspector] Nothing selected. Select one or more GameObjects.");
                return;
            }

            var w = new JsonWriter();
            w.BeginObject();
            w.Prop("kind", "selection-snapshot");
            w.Prop("unityVersion", Application.unityVersion);
            w.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.Prop("activeScene", SceneManager.GetActiveScene().name);
            w.PropName("objects");
            w.BeginArray();
            var st = new WalkState { FollowAssets = false, Budget = MaxExpandedAssets };
            foreach (var go in objs)
                WriteGameObject(w, go, includeChildren: false, st);
            w.EndArray();
            w.EndObject();

            var label = objs.Length == 1 ? Sanitize(objs[0].name) : (objs.Length + "-objects");
            WriteSnapshot(w, "selection_" + label);
        }

        public static void SnapshotSelectionDeep()
        {
            var objs = Selection.gameObjects;
            if (objs == null || objs.Length == 0)
            {
                Debug.LogWarning("[AgentInspector] Nothing selected.");
                return;
            }

            var w = new JsonWriter();
            w.BeginObject();
            w.Prop("kind", "selection-snapshot-deep");
            w.Prop("unityVersion", Application.unityVersion);
            w.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.PropName("objects");
            w.BeginArray();
            var st = new WalkState { FollowAssets = false, Budget = MaxExpandedAssets };
            foreach (var go in objs)
                WriteGameObject(w, go, includeChildren: true, st);
            w.EndArray();
            w.EndObject();

            var label = objs.Length == 1 ? Sanitize(objs[0].name) : (objs.Length + "-objects");
            WriteSnapshot(w, "selectiondeep_" + label);
        }

        public static void SnapshotScene()
        {
            var scene = SceneManager.GetActiveScene();
            var w = new JsonWriter();
            w.BeginObject();
            w.Prop("kind", "scene-hierarchy-snapshot");
            w.Prop("unityVersion", Application.unityVersion);
            w.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.Prop("scene", scene.name);
            w.Prop("scenePath", scene.path);
            w.PropName("roots");
            w.BeginArray();
            var st = new WalkState { FollowAssets = false, Budget = MaxExpandedAssets };
            foreach (var go in scene.GetRootGameObjects())
                WriteGameObject(w, go, includeChildren: true, st);
            w.EndArray();
            w.EndObject();

            WriteSnapshot(w, "scene_" + Sanitize(scene.name));
        }

        // ----- Agent entry point (path-addressed) -----

        /// <summary>
        /// Snapshots the GameObject at a root-relative hierarchy path in the active scene
        /// (e.g. "Avatar/Armature/Hips"). Duplicate-named siblings refuse, naming each match. Returns a
        /// one-line summary ending with the snapshot path (<c>… => OK … | log=&lt;path&gt;</c>).
        /// <paramref name="followAssets"/> inlines each saved-ScriptableObject ref's fields under its stub,
        /// recursively, bounded and cut-signaled.
        /// </summary>
        public static string Run(string hierarchyPath, bool includeChildren = true, bool followAssets = false)
        {
            var handle = SceneHandle.Resolve(hierarchyPath);
            var go = handle.Object;
            if (go == null)
            {
                string err = "[AgentInspector] " + handle.Refusal + " => FAIL";
                Debug.LogError(err);
                return err;
            }

            var st = new WalkState { FollowAssets = followAssets, Budget = MaxExpandedAssets };
            var w = new JsonWriter();
            w.BeginObject();
            w.Prop("kind", includeChildren ? "selection-snapshot-deep" : "selection-snapshot");
            w.Prop("unityVersion", Application.unityVersion);
            w.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.Prop("activeScene", SceneManager.GetActiveScene().name);
            w.PropName("objects");
            w.BeginArray();
            WriteGameObject(w, go, includeChildren, st);
            w.EndArray();
            // The walk-wide count of refs skipped for budget; each site also carries budgetSkipped.
            if (st.SkippedForBudget > 0) w.Prop("assetsTruncated", st.SkippedForBudget);
            w.EndObject();

            string prefix = includeChildren ? "selectiondeep_" : "selection_";
            string path = WriteSnapshot(w, prefix + Sanitize(go.name));
            return "[AgentInspector] snapshot " + go.name + " => OK" + SizeNote(path, hierarchyPath, includeChildren, followAssets)
                 + " | log=" + path;
        }

        // The agent harness's file-read cap, a FOREIGN constant nothing here can detect drifting. So the size is
        // reported unconditionally (a drifted threshold costs a missing hint, never a false claim) and the hint
        // is worded as approximate, because the real limit is not purely byte-shaped.
        private const long ReadCapBytes = 256 * 1024;

        /// <summary>Byte size of the written artifact, plus the narrower-door hint when it lands past the read cap.</summary>
        internal static string SizeNote(string path, string hierarchyPath, bool includeChildren, bool followAssets = false)
        {
            long bytes;
            try { bytes = new FileInfo(path).Length; }
            catch { return ""; }
            string note = " bytes=" + bytes;
            if (bytes <= ReadCapBytes) return note;
            return note + " (past the ~" + (ReadCapBytes / 1024) + "KB read cap — narrow it: "
                 + (includeChildren
                    ? "Run(\"" + hierarchyPath + "\", includeChildren: false" + (followAssets ? ", followAssets: false" : "") + "), or snapshot a deeper path"
                    : followAssets ? "re-run with followAssets: false, or snapshot a deeper path"
                                   : "snapshot a deeper path — this call is already at its narrowest flags")
                 + ")";
        }

        // ----- Serialization -----------------------------------------------------------------

        private static void WriteGameObject(JsonWriter w, GameObject go, bool includeChildren, WalkState st, int depth = 0)
        {
            w.BeginObject();
            w.Prop("name", go.name);
            w.Prop("path", GetHierarchyPath(go.transform));
            w.Prop("activeSelf", go.activeSelf);
            w.Prop("tag", go.tag);
            w.Prop("layer", LayerMask.LayerToName(go.layer));
            var prefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (prefab != null)
                w.Prop("prefabSource", AssetDatabase.GetAssetPath(prefab));
            w.Prop("isPrefabInstance", PrefabUtility.IsPartOfPrefabInstance(go));
            // Only the outermost root can be handed to ReportPrefab: from a scene, a nested root answers with the
            // outermost's lists.
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                w.Prop("prefabOverridesDoor", "ReportPrefab.Run(\"" + GetHierarchyPath(go.transform) + "\")");

            w.PropName("components");
            w.BeginArray();
            foreach (var comp in go.GetComponents<Component>())
                WriteComponent(w, comp, st);
            w.EndArray();

            if (includeChildren && depth < MaxDepth)
            {
                w.PropName("children");
                w.BeginArray();
                foreach (Transform child in go.transform)
                    WriteGameObject(w, child.gameObject, true, st, depth + 1);
                w.EndArray();
            }
            else if (includeChildren && go.transform.childCount > 0)
            {
                w.Prop("childrenTruncated", go.transform.childCount);
            }
            w.EndObject();
        }

        private static void WriteComponent(JsonWriter w, Component comp, WalkState st)
        {
            w.BeginObject();
            if (comp == null)
            {
                w.Prop("type", "MISSING (null component / broken script reference)");
                w.EndObject();
                return;
            }

            w.Prop("type", comp.GetType().FullName);
            WriteFields(w, comp, st, 0);
            w.EndObject();
        }

        /// <summary>Emits <paramref name="target"/>'s visible properties as a <c>fields</c> object. A throw degrades
        /// to a <c>fieldsError</c> instead of aborting the snapshot; the catch closes whatever the walk left open,
        /// so the JSON stays parseable.</summary>
        private static void WriteFields(JsonWriter w, UnityEngine.Object target, WalkState st, int assetDepth)
        {
            int depth = w.Depth;
            try
            {
                var so = new SerializedObject(target);
                var it = so.GetIterator();
                w.PropName("fields");
                w.BeginObject();
                bool enterChildren = true;
                while (it.NextVisible(enterChildren))
                {
                    enterChildren = false; // top-level only; WriteProperty recurses itself
                    if (it.name == "m_Script") continue;
                    w.PropName(it.name);
                    WriteProperty(w, it.Copy(), 0, st, assetDepth);
                }
                w.EndObject();
            }
            catch (Exception e)
            {
                w.UnwindTo(depth);
                w.Prop("fieldsError", e.Message);
            }
        }

        private static void WriteProperty(JsonWriter w, SerializedProperty p, int depth, WalkState st, int assetDepth)
        {
            if (depth > MaxDepth) { w.Value("<max-depth>"); return; }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:      w.Value(p.longValue); break;
                case SerializedPropertyType.Boolean:      w.Value(p.boolValue); break;
                case SerializedPropertyType.Float:        w.Value(p.doubleValue); break;
                case SerializedPropertyType.String:       w.Value(p.stringValue); break;
                case SerializedPropertyType.Enum:
                    w.Value(p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length
                        ? p.enumDisplayNames[p.enumValueIndex] : p.intValue.ToString());
                    break;
                case SerializedPropertyType.Vector2:      w.Value(p.vector2Value.ToString("F4")); break;
                case SerializedPropertyType.Vector3:      w.Value(p.vector3Value.ToString("F4")); break;
                case SerializedPropertyType.Vector4:      w.Value(p.vector4Value.ToString("F4")); break;
                case SerializedPropertyType.Quaternion:   w.Value(p.quaternionValue.eulerAngles.ToString("F2") + " (euler)"); break;
                case SerializedPropertyType.Color:        w.Value("#" + ColorUtility.ToHtmlStringRGBA(p.colorValue)); break;
                case SerializedPropertyType.LayerMask:    w.Value(p.intValue); break;
                case SerializedPropertyType.ObjectReference: WriteObjectRef(w, p.objectReferenceValue, st, assetDepth); break;
                case SerializedPropertyType.ExposedReference: WriteObjectRef(w, p.exposedReferenceValue, st, assetDepth); break;
                case SerializedPropertyType.AnimationCurve:
                    w.Value(p.animationCurveValue != null ? WriteDynamics.FormatCurve(p.animationCurveValue) : "null"); break;
                case SerializedPropertyType.Bounds:       w.Value(p.boundsValue.ToString()); break;
                default:
                    if (p.isArray && p.propertyType != SerializedPropertyType.String)
                    {
                        WriteArray(w, p, depth, st, assetDepth);
                    }
                    else if (p.hasVisibleChildren)
                    {
                        WriteStruct(w, p, depth, st, assetDepth);
                    }
                    else
                    {
                        w.Value(p.propertyType.ToString());
                    }
                    break;
            }
        }

        private static void WriteArray(JsonWriter w, SerializedProperty p, int depth, WalkState st, int assetDepth)
        {
            w.BeginObject();
            w.Prop("arraySize", p.arraySize);
            w.PropName("elements");
            w.BeginArray();
            int n = Math.Min(p.arraySize, MaxArrayElements);
            for (int i = 0; i < n; i++)
            {
                var el = p.GetArrayElementAtIndex(i);
                WriteProperty(w, el, depth + 1, st, assetDepth);
            }
            w.EndArray();
            if (p.arraySize > MaxArrayElements)
                w.Prop("truncated", p.arraySize - MaxArrayElements);
            w.EndObject();
        }

        private static void WriteStruct(JsonWriter w, SerializedProperty p, int depth, WalkState st, int assetDepth)
        {
            w.BeginObject();
            var end = p.GetEndProperty();
            var child = p.Copy();
            bool enter = true;
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                w.PropName(child.name);
                WriteProperty(w, child.Copy(), depth + 1, st, assetDepth);
            }
            w.EndObject();
        }

        private static void WriteObjectRef(JsonWriter w, UnityEngine.Object o, WalkState st, int assetDepth)
        {
            if (o == null) { w.Value(null); return; }
            w.BeginObject();
            w.Prop("refType", o.GetType().Name);
            w.Prop("name", o.name);
            var assetPath = AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(assetPath)) w.Prop("assetPath", assetPath);
            if (o is Component c) w.Prop("scenePath", GetHierarchyPath(c.transform));
            if (o is GameObject g) w.Prop("scenePath", GetHierarchyPath(g.transform));

            // guid/fileId mark real assets only: a ref with no assetPath (a scene object) or an all-zero GUID gets
            // neither, and never expands. fileId distinguishes sub-assets sharing one file GUID, so the dedup key
            // needs it too.
            bool haveId = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long localId)
                          && !string.IsNullOrEmpty(assetPath)
                          && !string.IsNullOrEmpty(guid) && guid != "00000000000000000000000000000000";
            if (haveId)
            {
                w.Prop("guid", guid);
                w.Prop("fileId", localId);
            }

            if (haveId && st.FollowAssets && o is ScriptableObject)
            {
                string key = guid + ":" + localId;
                if (st.Dumped.Contains(key))
                {
                    w.Prop("alreadyDumped", true);
                }
                else if (assetDepth >= MaxAssetDepth)
                {
                    // Consumes no budget and stays out of Dumped: the asset may still expand on a shallower branch.
                    w.Prop("assetDepthCapped", true);
                }
                else if (st.Budget == 0)
                {
                    st.SkippedForBudget++;
                    w.Prop("budgetSkipped", true);
                }
                else
                {
                    // The key enters Dumped BEFORE the dump, so cycle-safety survives a throw inside WriteFields.
                    st.Dumped.Add(key);
                    st.Budget--;
                    WriteFields(w, o, st, assetDepth + 1);
                }
            }
            w.EndObject();
        }

        // ----- Helpers -----------------------------------------------------------------------

        private static string GetHierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static string WriteSnapshot(JsonWriter w, string baseName)
        {
            Directory.CreateDirectory(SnapshotDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = $"{SnapshotDir}/{baseName}_{stamp}.json";
            File.WriteAllText(path, w.ToString());
            AssetDatabase.Refresh();
            Debug.Log($"[AgentInspector] snapshot {baseName} => OK | log={path}");
            return path;
        }

        private static string Sanitize(string s) => RunLogFormat.Sanitize(s);

        // ----- Tiny dependency-free JSON writer ----------------------------------------------

        internal class JsonWriter
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly Stack<bool> _firstStack = new Stack<bool>();
            private readonly Stack<char> _closers = new Stack<char>();
            private int _indent;

            public void BeginObject() { Pre(); _sb.Append("{"); Push('}'); }
            public void EndObject()   { Pop(); NewLine(); _sb.Append("}"); }
            public void BeginArray()  { Pre(); _sb.Append("["); Push(']'); }
            public void EndArray()    { Pop(); NewLine(); _sb.Append("]"); }

            /// <summary>How many objects and arrays are open; the mark <see cref="UnwindTo"/> takes.</summary>
            public int Depth => _firstStack.Count;

            /// <summary>Closes everything opened past <paramref name="depth"/>, answering a name still waiting for
            /// its value with null.</summary>
            public void UnwindTo(int depth)
            {
                if (_suppressPre) Value(null);
                while (_firstStack.Count > depth)
                {
                    char closer = _closers.Peek();
                    Pop();
                    NewLine();
                    _sb.Append(closer);
                }
            }

            public void PropName(string name) { Pre(); _sb.Append(Quote(name)).Append(": "); _suppressPre = true; }
            public void Prop(string name, string val)  { PropName(name); Value(val); }
            public void Prop(string name, bool val)    { PropName(name); Value(val); }
            public void Prop(string name, long val)    { PropName(name); Value(val); }
            public void Prop(string name, int val)     { PropName(name); Value(val); }

            public void Value(string s) { Pre(); _sb.Append(s == null ? "null" : Quote(s)); }
            public void Value(bool b)   { Pre(); _sb.Append(b ? "true" : "false"); }
            public void Value(long l)   { Pre(); _sb.Append(l.ToString(CultureInfo.InvariantCulture)); }
            public void Value(double d) { Pre(); _sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); }

            private bool _suppressPre;
            private void Pre()
            {
                if (_suppressPre) { _suppressPre = false; return; }
                if (_firstStack.Count > 0)
                {
                    if (!_firstStack.Peek()) _sb.Append(",");
                    _firstStack.Pop(); _firstStack.Push(false);
                    NewLine();
                }
            }
            private void Push(char closer) { _firstStack.Push(true); _closers.Push(closer); _indent++; }
            private void Pop()  { if (_firstStack.Count > 0) { _firstStack.Pop(); _closers.Pop(); } _indent--; }
            private void NewLine() { _sb.Append('\n').Append(new string(' ', _indent * 2)); }

            private static string Quote(string s)
            {
                var sb = new StringBuilder("\"");
                foreach (var c in s)
                {
                    switch (c)
                    {
                        case '"':  sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                return sb.Append("\"").ToString();
            }

            public override string ToString() => _sb.ToString();
        }
    }
}
