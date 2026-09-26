using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Writes a caller-authored VRC dynamics table onto a prefab or scene root. Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class WriteDynamics
    {
        [Serializable] public class Table { public Node[] nodes = new Node[0]; public Move[] moves = new Move[0]; public PhysBoneSet[] physbones = new PhysBoneSet[0]; public ConstraintRow[] constraints = new ConstraintRow[0]; }
        [Serializable] public class Node { public string parent, name, rotation = ""; public bool physbone; public NodeCollider collider = new NodeCollider(); }
        [Serializable] public class NodeCollider { public string shape = "", position = "0,0,0", rotation = "0,0,0"; public float radius = 0.05f, height = 0.2f; }
        [Serializable] public class Move { public string physbone; public string from; public string[] to = new string[0]; }
        [Serializable] public class PhysBoneSet { public string physbone; public string[] set = new string[0]; }
        [Serializable] public class ConstraintRow { public string target; public string type = "VRCRotationConstraint"; public Source[] sources = new Source[0]; public float globalWeight = 1f; public bool locked = true; }
        [Serializable] public class Source { public string path; public float weight = 1f; }

        const string Tag = "[WriteDynamics]", Key = "Ryan6Vrc.WriteDynamics.status";
        const int KeyableSlots = 16;
        static EditorApplication.CallbackFunction _pump;
        internal static bool Pending => _pump != null;

        /// <summary>The last live write's result: PENDING until its physbone hosts have re-activated, then its OK line and rows.</summary>
        public static string Status()
        {
            var s = SessionState.GetString(Key, Tag + " idle: no live physbone write has run this editor session");
            return _pump == null && s.Contains("=> PENDING") ? s.Replace("=> PENDING", "=> FAIL") + " | torn down before the hosts re-activated (play exit or domain reload); re-check the hosts named above" : s;
        }

        /// <summary>A host deactivated on frame <paramref name="armed"/> re-activates once a later frame has run: a physbone
        /// takes a live field set only after its host GameObject has been inactive across a frame boundary.</summary>
        internal static bool CycleDue(int armed, int now) => now > armed;

        public static string Run(string root, string table, bool whatIf = false)
        {
            var err = ParseTable(table, out var t);
            if (err != null) return Fail(err);
            bool play = EditorApplication.isPlaying, isPrefab = root != null && root.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
            if (play && isPrefab) return Fail("a prefab asset is edit-mode only; in play pass the live scene root");
            if (isPrefab && !whatIf && (root.Replace('\\', '/').StartsWith("Assets/Vendor/") || root.Replace('\\', '/').StartsWith("Packages/")))
                return Fail("'" + root + "' is vendor or package content, which this door never saves; write a scene instance or an owned prefab (whatIf may still preview it)");
            if (play && t.moves.Length + t.nodes.Length > 0) return Fail("nodes and moves are edit-mode only (a physbone root cannot move under a running solver); exit play and re-run");
            if (play && t.physbones.Length > 0 && DrivePhysBones.Running) return Fail("a DrivePhysBones drive is running and a live field set re-initialises its chains mid-sample; wait for DrivePhysBones.Status() to finish");
            if (play && t.physbones.Length > 0 && _pump != null) return Fail("a live field set is still cycling its physbone hosts; poll WriteDynamics.Status(), then call again");
            if (play && t.physbones.Length > 0 && EditorApplication.isPaused) return Fail("the editor is paused (a held GrabPhysBone freezes it), so no frame can pass for a field set to reach the solver: GrabPhysBone.Release(resume: true) first");
            GameObject go;
            if (isPrefab) { if (AssetDatabase.LoadAssetAtPath<GameObject>(root) == null) return Fail("no prefab asset at '" + root + "'"); go = PrefabUtility.LoadPrefabContents(root); }
            else { var h = SceneHandle.Resolve(root); if (!h.Ok) return Fail(h.Refusal); go = h.Object; }
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("WriteDynamics");
            try
            {
                // Every mutation is undo-recorded into one group, so a refusal, a whatIf or a write-time throw reverts it whole.
                var log = new List<string>(); Action writes = null; var hosts = new List<GameObject>();
                err = MakeNodes(go.transform, t.nodes, log, out int made);
                if (err == null) err = Plan(go.transform, t, play, log, hosts, out writes);
                if (err == null && !whatIf) try { writes(); } catch (Exception e) { err = "a write threw and the whole table was rolled back: " + e.GetType().Name + ": " + e.Message; }
                if (err != null || whatIf) Undo.RevertAllDownToGroup(group);
                if (err != null) return Fail(err);
                if (!whatIf && isPrefab) PrefabUtility.SaveAsPrefabAsset(go, root);
                string summary = Tag + (whatIf ? " Preview " : " Write ") + root + (play ? " (live)" : "") + " => OK | nodes=" + made + " moves=" + t.moves.Sum(m => m.to.Length) + " physbones=" + t.physbones.Length
                    + " constraints=" + t.constraints.Length + (whatIf ? " | whatIf: nothing written" : isPrefab ? " | saved" : play ? " | reverts on play exit" : " | scene dirtied, not saved");
                if (play && !whatIf && hosts.Count > 0) return CycleHosts(go.transform, root, summary, hosts, log);
                var line = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "write-dynamics_" + RunLogFormat.Leaf(root), summary, string.Join("\n", log) + "\n", ".md");
                Debug.Log(line);
                return line + "\n" + string.Join("\n", log.Take(12)) + (log.Count > 12 ? "\n… " + (log.Count - 12) + " more rows in the log" : "");
            }
            finally { if (isPrefab) PrefabUtility.UnloadPrefabContents(go); }
        }

        /// <summary>Resolves and validates every row and returns the writes as one action over objects resolved here,
        /// so nothing is looked up again once writing starts. Returns the first refusal, or null.</summary>
        static string Plan(Transform root, Table t, bool play, List<string> log, List<GameObject> hosts, out Action writes)
        {
            string err; writes = null; var steps = new List<Action>();
            var targets = new Dictionary<Transform, VRCPhysBoneBase>();   // move destination -> the physbone moving there
            var copies = new Dictionary<Transform, VRCPhysBoneBase>();    // filled at write time by the moves
            foreach (var m in t.moves)
            {
                var pb = ResolvePhysBone(root, m.physbone, targets, out err); if (pb == null) return err;
                var from = Find(root, m.from, out err); if (from == null) return err;
                if (EffRoot(pb) != from) return "physbone '" + m.physbone + "' is rooted at '" + PathOf(root, EffRoot(pb)) + "', not '" + m.from + "'";
                if (targets.ContainsValue(pb)) return "physbone '" + m.physbone + "' is moved by two rows; list every destination in one row's `to`";
                var tos = new Transform[m.to.Length];
                for (int i = 0; i < tos.Length; i++)
                {
                    if ((tos[i] = Find(root, m.to[i], out err)) == null) return err;
                    if (tos[i] == from || !tos[i].IsChildOf(from)) return "'" + m.to[i] + "' is not a descendant of '" + m.from + "'";
                    var ign = pb.ignoreTransforms.FirstOrDefault(x => x != null && tos[i].IsChildOf(x));
                    if (ign != null) return "'" + m.to[i] + "' sits under '" + PathOf(root, ign) + "', which physbone '" + m.physbone + "' ignores: that chain belongs to another physbone";
                    if (targets.ContainsKey(tos[i]) || root.GetComponentsInChildren<VRCPhysBoneBase>(true).Any(p => EffRoot(p) == tos[i])) return "'" + m.to[i] + "' is already a physbone root";
                    targets[tos[i]] = pb;
                    log.Add("move `" + PathOf(root, pb.transform) + "` root `" + m.from + "` -> `" + m.to[i] + "`" + (tos.Length > 1 && !string.IsNullOrEmpty(pb.parameter) ? " (parameter '" + pb.parameter + "' dropped: one name cannot serve " + tos.Length + " chains)" : ""));
                }
                steps.Add(() => MovePhysBone(pb, tos, copies));
            }
            foreach (var s in t.physbones)
            {
                var at = Find(root, s.physbone, out err); if (at == null) return err;
                var pb = ResolvePhysBone(root, s.physbone, targets, out err); if (pb == null) return err;
                bool moved = targets.ContainsKey(at);
                if (!moved && targets.ContainsValue(pb)) return "physbone '" + s.physbone + "' is moved by this table; set fields on its new roots";
                var so = new SerializedObject(pb);
                foreach (var kv in s.set) if ((err = SetProp(root, so, kv)) != null) return "physbones '" + s.physbone + "': " + err;
                if (play && pb.transform == root) return "physbones '" + s.physbone + "' is hosted on '" + root.name + "' itself, and a live field set applies only by cycling its host inactive, which would restart the whole avatar; move it to a holder in edit mode";
                log.Add("set `" + s.physbone + "` " + string.Join(" ", s.set));
                if (play && !hosts.Contains(pb.gameObject)) hosts.Add(pb.gameObject); // play has no moves, so the live physbone is pb
                steps.Add(() =>
                {
                    var live = moved ? copies[at] : pb; var lso = new SerializedObject(live);
                    foreach (var kv in s.set) SetProp(root, lso, kv);
                    lso.ApplyModifiedProperties();
                });
            }
            var seen = new HashSet<Transform>();
            foreach (var c in t.constraints)
            {
                var target = Find(root, c.target, out err); if (target == null) return err;
                if (!seen.Add(target)) return "constraints '" + c.target + "' appears in two rows; one row per target";
                var type = TypeCache.GetTypesDerivedFrom<VRCConstraintBase>().FirstOrDefault(x => !x.IsAbstract && x.Name == c.type);
                if (type == null) return "constraints '" + c.target + "': no VRC constraint type '" + c.type + "' (VRCRotationConstraint, VRCParentConstraint, VRCPositionConstraint, VRCScaleConstraint, VRCAimConstraint, VRCLookAtConstraint)";
                if (c.sources.Length > KeyableSlots) return "constraints '" + c.target + "': " + c.sources.Length + " sources; past " + KeyableSlots + " they land in overflowList, which no animator can key — split the table";
                var src = new Transform[c.sources.Length];
                for (int i = 0; i < src.Length; i++) if ((src[i] = Find(root, c.sources[i].path, out err)) == null) return "constraints '" + c.target + "': " + err;
                var existing = (VRCConstraintBase)target.GetComponent(type);
                string row = "`" + c.target + "` " + c.type + " [" + string.Join(", ", c.sources.Select(x => x.path + "@" + x.weight.ToString("0.###", CultureInfo.InvariantCulture))) + "] gw=" + c.globalWeight.ToString("0.###", CultureInfo.InvariantCulture);
                if (play)
                {
                    if (existing == null) return "constraints '" + c.target + "': no live " + c.type + "; play rewrites weights only — author the constraint in edit mode";
                    if (existing.Sources.Count != src.Length || Enumerable.Range(0, src.Length).Any(i => existing.Sources[i].SourceTransform != src[i]))
                        return "constraints '" + c.target + "': the live source list differs from the row; play rewrites weights only, in the live source order";
                    log.Add("weights " + row);
                    steps.Add(() => { Undo.RecordObject(existing, "WriteDynamics"); for (int i = 0; i < src.Length; i++) { var x = existing.Sources[i]; x.Weight = c.sources[i].weight; existing.Sources[i] = x; } existing.GlobalWeight = c.globalWeight; });
                    continue;
                }
                log.Add((existing ? "rewrite " : "add ") + row + (c.locked ? " locked" : " unlocked"));
                steps.Add(() => WriteConstraint(existing ?? (VRCConstraintBase)Undo.AddComponent(target.gameObject, type), src, c));
            }
            writes = () => { foreach (var step in steps) step(); };
            return null;
        }

        /// <summary>A live field set lands on the component but reaches the solver only after its host GameObject has been
        /// inactive across a frame boundary; toggling the component or the GameObject within one frame, or the chain's root
        /// GameObject when the host is a separate holder, leaves the chain on its old fields. A component toggle split across
        /// frames applied on a bare rig but not on a composed avatar, so it is not the route. Every active host goes inactive
        /// now and back on the first later frame, off <c>update</c> (never <c>delayCall</c>, which an unfocused editor does not
        /// pump), and the result lands in <see cref="Status"/>. Everything under a host cycles with it, so a physbone the table
        /// never named re-initialises too and is counted as collateral.</summary>
        static string CycleHosts(Transform root, string handle, string summary, List<GameObject> hosts, List<string> log)
        {
            var cycled = hosts.Where(h => h.activeInHierarchy).ToArray();
            foreach (var h in hosts.Except(cycled)) log.Add("host `" + PathOf(root, h.transform) + "` is inactive: its chain takes the written fields when it activates");
            int collateral = cycled.SelectMany(h => h.GetComponentsInChildren<VRCPhysBoneBase>()).Distinct().Count(p => !hosts.Contains(p.gameObject));
            int armed = Time.frameCount;
            foreach (var h in cycled) h.SetActive(false);
            string names = string.Join(", ", cycled.Select(h => "`" + PathOf(root, h.transform) + "`"));
            string pending = Tag + " Write " + handle + " (live) => PENDING | " + cycled.Length + " physbone hosts inactive since frame " + armed + " (" + names + "); poll Ryan6Vrc.AgentTools.Editor.WriteDynamics.Status()";
            SessionState.SetString(Key, pending + "\n" + string.Join("\n", log));
            _pump = () =>
            {
                if (EditorApplication.isPlaying && !CycleDue(armed, Time.frameCount)) return;
                EditorApplication.update -= _pump; _pump = null;
                if (!EditorApplication.isPlaying) { SessionState.SetString(Key, Tag + " Write " + handle + " (live) => FAIL | play exited before the hosts re-activated; the write reverted with play"); return; }
                foreach (var h in cycled) if (h != null) h.SetActive(true);
                var line = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "write-dynamics_" + RunLogFormat.Leaf(handle), summary + " | reinit: " + cycled.Length
                    + " hosts cycled inactive frames " + armed + "->" + Time.frameCount + " (collateral: " + collateral + " other physbones beneath them); each chain restarted from its current pose, which is now its rest, and any grab on it dropped", string.Join("\n", log) + "\n", ".md");
                SessionState.SetString(Key, line + "\n" + string.Join("\n", log)); Debug.Log(line);
            };
            EditorApplication.update += _pump;
            return pending + "\n" + string.Join("\n", log.Take(12)) + (log.Count > 12 ? "\n… " + (log.Count - 12) + " more rows in the log" : "");
        }

        /// <summary>Creates each absent node (an existing one is left alone): an empty child at the parent's origin,
        /// world <c>rotation</c> as <c>x,y,z</c> Euler (blank keeps the parent's), with an optional bare physbone and an
        /// optional collider on itself. Undo-registered, so the caller's group revert removes them.</summary>
        static string MakeNodes(Transform root, Node[] nodes, List<string> log, out int made)
        {
            made = 0;
            var colType = TypeCache.GetTypesDerivedFrom<VRCPhysBoneColliderBase>().First(x => !x.IsAbstract);
            var pbType = TypeCache.GetTypesDerivedFrom<VRCPhysBoneBase>().First(x => !x.IsAbstract);
            foreach (var n in nodes)
            {
                var parent = Find(root, n.parent, out var err); if (parent == null) return "nodes '" + n.name + "': " + err;
                if (parent.Cast<Transform>().Any(c => c.name == n.name)) { log.Add("node `" + n.parent + "/" + n.name + "` exists, left alone"); continue; }
                var rot = n.rotation.Length == 0 ? parent.eulerAngles : ParseVector(n.rotation);
                var c = n.collider; var cp = ParseVector(c.position); var cr = ParseVector(c.rotation);
                if (rot == null || cp == null || cr == null) return "nodes '" + n.name + "': rotation, collider.position and collider.rotation are x,y,z";
                bool hasCol = c.shape.Length > 0;
                if (hasCol && !Enum.GetNames(typeof(VRCPhysBoneColliderBase.ShapeType)).Any(x => string.Equals(x, c.shape, StringComparison.OrdinalIgnoreCase)))
                    return "nodes '" + n.name + "': collider.shape '" + c.shape + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(VRCPhysBoneColliderBase.ShapeType)));
                var go = new GameObject(n.name); Undo.RegisterCreatedObjectUndo(go, "WriteDynamics"); made++;
                go.transform.SetParent(parent, false); go.transform.rotation = Quaternion.Euler(rot.Value);
                if (n.physbone) go.AddComponent(pbType);
                if (hasCol)
                {
                    var col = (VRCPhysBoneColliderBase)go.AddComponent(colType);
                    col.shapeType = (VRCPhysBoneColliderBase.ShapeType)Enum.Parse(typeof(VRCPhysBoneColliderBase.ShapeType), c.shape, true);
                    col.radius = c.radius; col.height = c.height; col.position = cp.Value; col.rotation = Quaternion.Euler(cr.Value);
                }
                log.Add("node `" + n.parent + "/" + n.name + "` rot=" + rot.Value.ToString("F1") + (n.physbone ? " +physbone" : "") + (hasCol ? " +collider " + c.shape + " r=" + c.radius.ToString("0.###", CultureInfo.InvariantCulture) + " h=" + c.height.ToString("0.###", CultureInfo.InvariantCulture) : ""));
            }
            return null;
        }

        /// <summary>One copy per destination, on the original's holder when it used one (the vendor's idiom), else on
        /// the bone. A fan-out drops <c>parameter</c>: one animator name cannot serve several chains.</summary>
        static void MovePhysBone(VRCPhysBoneBase pb, Transform[] tos, Dictionary<Transform, VRCPhysBoneBase> copies)
        {
            bool onHolder = pb.rootTransform != null && pb.rootTransform != pb.transform;
            foreach (var to in tos)
            {
                var copy = (VRCPhysBoneBase)Undo.AddComponent(onHolder ? pb.gameObject : to.gameObject, pb.GetType());
                EditorUtility.CopySerialized(pb, copy);
                copy.rootTransform = onHolder ? to : null;
                if (tos.Length > 1) copy.parameter = "";
                copies[to] = copy;
            }
            Undo.DestroyObjectImmediate(pb);
        }

        /// <summary>The one reliable write path for constraint sources (runtime.md §Constraints): slots and
        /// totalLength through SerializedObject, stale slots past the new length cleared (the editor solves any slot
        /// holding a transform), then Activate so a locked constraint bakes the current pose as rest.</summary>
        static void WriteConstraint(VRCConstraintBase con, Transform[] src, ConstraintRow row)
        {
            var so = new SerializedObject(con);
            for (int i = 0; i < KeyableSlots; i++)
            {
                string p = "Sources.source" + i + ".";
                so.FindProperty(p + "SourceTransform").objectReferenceValue = i < src.Length ? src[i] : null;
                so.FindProperty(p + "Weight").floatValue = i < src.Length ? row.sources[i].weight : 0f;
                so.FindProperty(p + "ParentPositionOffset").vector3Value = Vector3.zero;
                so.FindProperty(p + "ParentRotationOffset").vector3Value = Vector3.zero;
            }
            so.FindProperty("Sources.totalLength").intValue = src.Length;
            so.FindProperty("GlobalWeight").floatValue = row.globalWeight;
            so.FindProperty("IsActive").boolValue = true;
            so.FindProperty("Locked").boolValue = row.locked;
            so.ApplyModifiedProperties();
            if (row.locked) { Undo.RecordObject(con, "WriteDynamics"); con.ActivateConstraint(); }
        }

        /// <summary>Writes one <c>field=value</c> onto a SerializedObject (unapplied). Floats, ints, bools, enums by
        /// name, Vector3 as <c>x,y,z</c>, an object ref as a root-relative path, a ref array as <c>;</c>-separated paths.</summary>
        internal static string SetProp(Transform root, SerializedObject so, string kv)
        {
            int eq = kv.IndexOf('='); string key = kv.Substring(0, eq).Trim(), val = kv.Substring(eq + 1).Trim();
            var p = so.FindProperty(key);
            if (p == null) return "no serialized field '" + key + "' on " + so.targetObject.GetType().Name;
            string bad = "'" + key + "' (" + p.propertyType + ") cannot take '" + val + "'";
            var inv = CultureInfo.InvariantCulture;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Float: if (!float.TryParse(val, NumberStyles.Float, inv, out var f)) return bad; p.floatValue = f; return null;
                case SerializedPropertyType.Integer: if (!int.TryParse(val, NumberStyles.Integer, inv, out var n)) return bad; p.intValue = n; return null;
                case SerializedPropertyType.Boolean: if (!bool.TryParse(val, out var b)) return bad; p.boolValue = b; return null;
                case SerializedPropertyType.Enum:
                    int e = Array.FindIndex(p.enumNames, x => string.Equals(x, val, StringComparison.OrdinalIgnoreCase));
                    if (e < 0) return bad + "; expected one of " + string.Join(", ", p.enumNames); p.enumValueIndex = e; return null;
                case SerializedPropertyType.Vector3:
                    var v = ParseVector(val); if (v == null) return bad + "; expected x,y,z"; p.vector3Value = v.Value; return null;
                case SerializedPropertyType.ObjectReference: return SetRef(root, p, val);
            }
            if (!p.isArray || !p.arrayElementType.StartsWith("PPtr")) return "'" + key + "' is a " + p.propertyType + " field, which this door does not write";
            var parts = val.Length == 0 ? new string[0] : val.Split(';');
            p.arraySize = parts.Length;
            for (int i = 0; i < parts.Length; i++) { var err = SetRef(root, p.GetArrayElementAtIndex(i), parts[i].Trim()); if (err != null) return err; }
            return null;
        }

        static string SetRef(Transform root, SerializedProperty p, string path)
        {
            if (path.Length == 0 || path == "null") { p.objectReferenceValue = null; return null; }
            var t = Find(root, path, out var err); if (t == null) return err;
            foreach (var c in t.GetComponents<Component>()) { p.objectReferenceValue = c; if (p.objectReferenceValue == c) return null; }
            return "'" + path + "' carries nothing assignable to '" + p.propertyPath + "'";
        }

        internal static Vector3? ParseVector(string s)
        {
            var a = s.Trim('(', ')', ' ').Split(','); var inv = CultureInfo.InvariantCulture;
            if (a.Length != 3 || !float.TryParse(a[0], NumberStyles.Float, inv, out var x) || !float.TryParse(a[1], NumberStyles.Float, inv, out var y) || !float.TryParse(a[2], NumberStyles.Float, inv, out var z)) return null;
            return new Vector3(x, y, z);
        }

        /// <summary>The table's shape rules, checked before anything resolves. Pure: no scene, no assets.</summary>
        internal static string ParseTable(string text, out Table t)
        {
            t = null; text = text?.Trim() ?? "";
            if (!text.StartsWith("{") && File.Exists(text)) text = File.ReadAllText(text).Trim();
            if (!text.StartsWith("{")) return "the table is a JSON object ({\"nodes\":[…],\"moves\":[…],\"physbones\":[…],\"constraints\":[…]}) or a path to a file holding one";
            try { t = JsonUtility.FromJson<Table>(text); } catch (ArgumentException e) { return "table JSON does not parse: " + e.Message; }
            for (int i = 0; i < t.nodes.Length; i++)
                if (string.IsNullOrEmpty(t.nodes[i].parent) || string.IsNullOrEmpty(t.nodes[i].name) || t.nodes[i].name.Contains("/")) return "nodes[" + i + "] needs parent and a bare name";
            for (int i = 0; i < t.moves.Length; i++)
                if (string.IsNullOrEmpty(t.moves[i].physbone) || string.IsNullOrEmpty(t.moves[i].from) || t.moves[i].to.Length == 0) return "moves[" + i + "] needs physbone, from, and a non-empty to";
            for (int i = 0; i < t.physbones.Length; i++)
                if (string.IsNullOrEmpty(t.physbones[i].physbone) || t.physbones[i].set.Any(s => s.IndexOf('=') < 1)) return "physbones[" + i + "] needs physbone and a set of \"field=value\" strings";
            for (int i = 0; i < t.constraints.Length; i++)
                if (string.IsNullOrEmpty(t.constraints[i].target) || t.constraints[i].sources.Any(s => string.IsNullOrEmpty(s.path))) return "constraints[" + i + "] needs a target and every source a path";
            return null;
        }

        /// <summary>A root-relative path, segment by segment; a segment also matches its play-build name
        /// (<c>name$suffix</c>). Refuses a miss or an ambiguity by name rather than picking.</summary>
        internal static Transform Find(Transform root, string path, out string err)
        {
            var frontier = new List<Transform> { root };
            foreach (var seg in (path ?? "").Trim('/').Split('/'))
                frontier = frontier.SelectMany(f => f.Cast<Transform>().Where(c => c.name == seg || c.name.Split('$')[0] == seg)).ToList();
            err = frontier.Count == 1 ? null : frontier.Count == 0 ? "'" + path + "' resolves to nothing under '" + root.name + "'"
                : "'" + path + "' matches " + frontier.Count + " objects under '" + root.name + "': " + string.Join(", ", frontier.Select(x => PathOf(root, x)));
            return err == null ? frontier[0] : null;
        }

        /// <summary>A physbone by the path of its host GameObject or its effective root; a pending move target
        /// resolves to the physbone moving there.</summary>
        static VRCPhysBoneBase ResolvePhysBone(Transform root, string path, Dictionary<Transform, VRCPhysBoneBase> pending, out string err)
        {
            var t = Find(root, path, out err); if (t == null) return null;
            if (pending.TryGetValue(t, out var moved)) return moved;
            var hits = root.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.transform == t || EffRoot(p) == t).ToList();
            if (hits.Count == 1) return hits[0];
            err = hits.Count == 0 ? "no physbone hosted on or rooted at '" + path + "'"
                : "'" + path + "' names " + hits.Count + " physbones (host -> root: " + string.Join(", ", hits.Select(p => PathOf(root, p.transform) + " -> " + PathOf(root, EffRoot(p)))) + "); pass a path that names one";
            return null;
        }

        internal static Transform EffRoot(VRCPhysBoneBase pb) => pb.rootTransform != null ? pb.rootTransform : pb.transform;

        static string PathOf(Transform root, Transform t) => AnimationUtility.CalculateTransformPath(t, root);

        static string Fail(string m) { var s = Tag + " FAIL: " + m; Debug.LogWarning(s); return s; }
    }
}
