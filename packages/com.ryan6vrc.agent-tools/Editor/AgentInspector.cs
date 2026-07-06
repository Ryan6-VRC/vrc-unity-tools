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
    /// <summary>
    /// Minimal, dependency-free observability harness for the AI-assisted VRChat workflow.
    ///
    /// Dumps the selected GameObject(s) -> components -> serialized fields (and optionally the
    /// active scene hierarchy) to JSON under Assets/Agent/Snapshots/, so a coding agent
    /// (Claude Code) can read the live project state as context without manual copy/paste.
    ///
    /// Uses SerializedObject iteration, so it captures *any* component generically — including
    /// VRC Avatar Descriptor, PhysBones/colliders/contacts, VRCFury, and Modular Avatar — with
    /// no per-type code. Object references are recorded by type + name + asset/scene path.
    ///
    /// This is an INSPECTION tool only. It never mutates the project.
    /// </summary>
    [AgentTool]
    public static class AgentInspector
    {
        private const string SnapshotDir = RunLogFormat.SnapshotDir;
        private const int MaxDepth = 6;        // recursion guard for nested serialized data
        private const int MaxArrayElements = 64; // truncation guard for large arrays

        // ----- Selection-driven entry points (read the Editor Selection; SnapshotSelection also used by AgentSelfTest) -----

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
            foreach (var go in objs)
                WriteGameObject(w, go, includeChildren: false);
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
            foreach (var go in objs)
                WriteGameObject(w, go, includeChildren: true);
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
            foreach (var go in scene.GetRootGameObjects())
                WriteGameObject(w, go, includeChildren: true);
            w.EndArray();
            w.EndObject();

            WriteSnapshot(w, "scene_" + Sanitize(scene.name));
        }

        // ----- Agent entry point (path-addressed; the entries above read the Editor Selection) ---------

        /// <summary>
        /// Snapshot the GameObject at a root-relative hierarchy path in the active scene
        /// (e.g. "Avatar/Armature/Hips"). Duplicate-named siblings resolve to the first match,
        /// like Unity's own path lookups. Returns a one-line summary ending with the snapshot
        /// path in-band (<c>… => OK | log=&lt;path&gt;</c>).
        /// </summary>
        public static string Snapshot(string hierarchyPath, bool includeChildren = true)
        {
            var go = FindByHierarchyPath(hierarchyPath);
            if (go == null)
            {
                string err = "[AgentInspector] no GameObject at path '" + hierarchyPath + "' => FAIL";
                Debug.LogError(err);
                return err;
            }

            var w = new JsonWriter();
            w.BeginObject();
            w.Prop("kind", includeChildren ? "selection-snapshot-deep" : "selection-snapshot");
            w.Prop("unityVersion", Application.unityVersion);
            w.Prop("timestampUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            w.Prop("activeScene", SceneManager.GetActiveScene().name);
            w.PropName("objects");
            w.BeginArray();
            WriteGameObject(w, go, includeChildren);
            w.EndArray();
            w.EndObject();

            string prefix = includeChildren ? "selectiondeep_" : "selection_";
            string path = WriteSnapshot(w, prefix + Sanitize(go.name));
            return "[AgentInspector] snapshot " + go.name + " => OK | log=" + path;
        }

        /// <summary>Resolve a root-relative hierarchy path in the active scene; first match wins
        /// among duplicate-named siblings (and among duplicate-named roots, the first root that
        /// resolves the full path).</summary>
        private static GameObject FindByHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segs = path.Trim('/').Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != segs[0]) continue;
                Transform t = root.transform;
                bool ok = true;
                for (int i = 1; i < segs.Length && ok; i++)
                {
                    t = t.Find(segs[i]);
                    if (t == null) ok = false;
                }
                if (ok) return t.gameObject;
            }
            return null;
        }

        // ----- Serialization -----------------------------------------------------------------

        private static void WriteGameObject(JsonWriter w, GameObject go, bool includeChildren, int depth = 0)
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

            w.PropName("components");
            w.BeginArray();
            foreach (var comp in go.GetComponents<Component>())
                WriteComponent(w, comp);
            w.EndArray();

            if (includeChildren && depth < MaxDepth)
            {
                w.PropName("children");
                w.BeginArray();
                foreach (Transform child in go.transform)
                    WriteGameObject(w, child.gameObject, true, depth + 1);
                w.EndArray();
            }
            else if (includeChildren && go.transform.childCount > 0)
            {
                // Hit MaxDepth — signal the cut rather than dropping children silently.
                w.Prop("childrenTruncated", go.transform.childCount);
            }
            w.EndObject();
        }

        private static void WriteComponent(JsonWriter w, Component comp)
        {
            w.BeginObject();
            if (comp == null)
            {
                w.Prop("type", "MISSING (null component / broken script reference)");
                w.EndObject();
                return;
            }

            w.Prop("type", comp.GetType().FullName);
            try
            {
                var so = new SerializedObject(comp);
                var it = so.GetIterator();
                w.PropName("fields");
                w.BeginObject();
                bool enterChildren = true;
                while (it.NextVisible(enterChildren))
                {
                    enterChildren = false; // top-level only; WriteProperty recurses itself
                    if (it.name == "m_Script") continue;
                    w.PropName(it.name);
                    WriteProperty(w, it.Copy(), 0);
                }
                w.EndObject();
            }
            catch (Exception e)
            {
                w.Prop("fieldsError", e.Message);
            }
            w.EndObject();
        }

        private static void WriteProperty(JsonWriter w, SerializedProperty p, int depth)
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
                case SerializedPropertyType.ObjectReference: WriteObjectRef(w, p.objectReferenceValue); break;
                case SerializedPropertyType.ExposedReference: WriteObjectRef(w, p.exposedReferenceValue); break;
                case SerializedPropertyType.AnimationCurve:
                    w.Value(p.animationCurveValue != null ? p.animationCurveValue.length + " keys" : "null"); break;
                case SerializedPropertyType.Bounds:       w.Value(p.boundsValue.ToString()); break;
                default:
                    if (p.isArray && p.propertyType != SerializedPropertyType.String)
                    {
                        WriteArray(w, p, depth);
                    }
                    else if (p.hasVisibleChildren)
                    {
                        WriteStruct(w, p, depth);
                    }
                    else
                    {
                        w.Value(p.propertyType.ToString());
                    }
                    break;
            }
        }

        private static void WriteArray(JsonWriter w, SerializedProperty p, int depth)
        {
            w.BeginObject();
            w.Prop("arraySize", p.arraySize);
            w.PropName("elements");
            w.BeginArray();
            int n = Math.Min(p.arraySize, MaxArrayElements);
            for (int i = 0; i < n; i++)
            {
                var el = p.GetArrayElementAtIndex(i);
                WriteProperty(w, el, depth + 1);
            }
            w.EndArray();
            if (p.arraySize > MaxArrayElements)
                w.Prop("truncated", p.arraySize - MaxArrayElements);
            w.EndObject();
        }

        private static void WriteStruct(JsonWriter w, SerializedProperty p, int depth)
        {
            w.BeginObject();
            var end = p.GetEndProperty();
            var child = p.Copy();
            bool enter = true;
            while (child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end))
            {
                enter = false;
                w.PropName(child.name);
                WriteProperty(w, child.Copy(), depth + 1);
            }
            w.EndObject();
        }

        private static void WriteObjectRef(JsonWriter w, UnityEngine.Object o)
        {
            if (o == null) { w.Value(null); return; }
            w.BeginObject();
            w.Prop("refType", o.GetType().Name);
            w.Prop("name", o.name);
            var assetPath = AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(assetPath)) w.Prop("assetPath", assetPath);
            if (o is Component c) w.Prop("scenePath", GetHierarchyPath(c.transform));
            if (o is GameObject g) w.Prop("scenePath", GetHierarchyPath(g.transform));
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
            EditorGUIUtility.systemCopyBuffer = path; // path also on clipboard for human convenience
            return path;
        }

        private static string Sanitize(string s) => RunLogFormat.Sanitize(s);

        // ----- Tiny dependency-free JSON writer ----------------------------------------------

        private class JsonWriter
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly Stack<bool> _firstStack = new Stack<bool>();
            private int _indent;

            public void BeginObject() { Pre(); _sb.Append("{"); Push(); }
            public void EndObject()   { Pop(); NewLine(); _sb.Append("}"); }
            public void BeginArray()  { Pre(); _sb.Append("["); Push(); }
            public void EndArray()    { Pop(); NewLine(); _sb.Append("]"); }

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
            private void Push() { _firstStack.Push(true); _indent++; }
            private void Pop()  { if (_firstStack.Count > 0) _firstStack.Pop(); _indent--; }
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
