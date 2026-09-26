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
        [Serializable] public class Table { public Node[] nodes = new Node[0]; public Move[] moves = new Move[0]; public PhysBoneSet[] physbones = new PhysBoneSet[0]; public ColliderSet[] colliders = new ColliderSet[0]; public ConstraintRow[] constraints = new ConstraintRow[0]; }
        [Serializable] public class Node { public string parent, name, rotation = ""; public bool physbone; public NodeCollider collider = new NodeCollider(); }
        [Serializable] public class NodeCollider { public string shape = "", position = "0,0,0", rotation = "0,0,0"; public float radius = 0.05f, height = 0.2f; }
        [Serializable] public class Move { public string physbone; public string from; public string[] to = new string[0]; }
        /// <summary><c>create</c> adds a physbone on the bone <c>physbone</c> names, before the moves run, optionally a copy of the
        /// physbone <c>copy</c> names less its <c>rootTransform</c> and <c>parameter</c>; <c>remove</c> destroys the one it names.</summary>
        [Serializable] public class PhysBoneSet { public string physbone; public string[] set = new string[0]; public bool create, remove; public string copy = ""; }
        [Serializable] public class ColliderSet { public string collider; public string[] set = new string[0]; }
        [Serializable] public class ConstraintRow { public string target; public string type = "VRCRotationConstraint"; public Source[] sources = new Source[0]; public float globalWeight = 1f; public bool locked = true; public bool remove; }
        [Serializable] public class Source { public string path; public float weight = 1f; }

        const string Tag = "[WriteDynamics]", Key = "Ryan6Vrc.WriteDynamics.status", SessionKey = "Ryan6Vrc.WriteDynamics.playSession";
        const int KeyableSlots = 16;
        static EditorApplication.CallbackFunction _pump;
        internal static bool Pending => _pump != null;

        /// <summary>The last live write's result: PENDING until its physbone hosts have re-activated, then its OK line and rows.</summary>
        public static string Status()
        {
            var s = SessionState.GetString(Key, Tag + " idle: no live physbone write has run this editor session");
            if (s.StartsWith(Tag + " idle")) return s;
            if (!PlaySession.IsCurrent(SessionState.GetInt(SessionKey, 0))) return PlaySession.Stale(Tag, "live physbone write", s.Split('\n')[0]);
            if (_pump != null && EditorApplication.isPaused) return s + "\n" + Tag + " the editor is paused, so no frame can pass and the hosts stay inactive: GrabPhysBone.Release(resume: true) or unpause";
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
            if (play && (t.moves.Length + t.nodes.Length > 0 || t.physbones.Any(p => p.create || p.remove) || t.constraints.Any(c => c.remove)))
                return Fail("nodes, moves, creates and removes are edit-mode only (the solver's chains are built at play entry); exit play and re-run");
            if (play && t.physbones.Length + t.colliders.Length > 0 && DrivePhysBones.Running) return Fail("a DrivePhysBones drive is running and a live physbone or collider write changes its chains mid-sample; wait for DrivePhysBones.Status() to finish");
            if (play && t.physbones.Length > 0 && _pump != null) return Fail("a live field set is still cycling its physbone hosts; poll WriteDynamics.Status(), then call again");
            if (play && t.physbones.Length > 0 && EditorApplication.isPaused) return Fail("the editor is paused (a held GrabPhysBone freezes it), so no frame can pass for a field set to reach the solver: GrabPhysBone.Release(resume: true) first");
            GameObject go;
            if (isPrefab) { if (AssetDatabase.LoadAssetAtPath<GameObject>(root) == null) return Fail("no prefab asset at '" + root + "'"); go = PrefabUtility.LoadPrefabContents(root); }
            else { var h = SceneHandle.Resolve(root); if (!h.Ok) return Fail(h.Refusal); go = h.Object; }
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("WriteDynamics");
            try
            {
                // Every mutation is undo-recorded into one group, so a refusal, a whatIf or a write-time throw reverts it whole.
                var log = new List<string>(); Action writes = null; var named = new List<VRCPhysBoneBase>(); var baked = new List<(string target, VRCConstraintBase con)>();
                err = MakeNodes(go.transform, t.nodes, log, out int made);
                if (err == null) err = MakePhysBones(go.transform, t.physbones, log);
                if (err == null) err = Plan(go.transform, t, play, log, named, baked, out writes);
                if (err == null && !whatIf) try { writes(); } catch (Exception e) { err = "a write threw and the whole table was rolled back: " + e.GetType().Name + ": " + e.Message; }
                string offsets = err == null && !whatIf ? BakedOffsets(baked, log) : "";
                if (err != null || whatIf) Undo.RevertAllDownToGroup(group);
                if (err != null) return Fail(err);
                if (!whatIf && isPrefab) PrefabUtility.SaveAsPrefabAsset(go, root);
                string summary = Tag + (whatIf ? " Preview " : " Write ") + root + (play ? " (live)" : "") + " => OK | nodes=" + made + " moves=" + t.moves.Sum(m => m.to.Length) + " physbones=" + t.physbones.Length
                    + " colliders=" + t.colliders.Length + " constraints=" + t.constraints.Length + offsets + (whatIf ? " | whatIf: nothing written" : isPrefab ? " | saved" : play ? " | reverts on play exit" : " | scene dirtied, not saved");
                if (play && !whatIf && named.Count > 0) { SessionState.SetInt(SessionKey, PlaySession.Current); return CycleHosts(go.transform, root, summary, named, log); }
                var line = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "write-dynamics_" + RunLogFormat.Leaf(root), summary, string.Join("\n", log) + "\n", ".md");
                Debug.Log(line);
                return line + "\n" + string.Join("\n", log.Take(12)) + (log.Count > 12 ? "\n… " + (log.Count - 12) + " more rows in the log" : "");
            }
            finally { if (isPrefab) PrefabUtility.UnloadPrefabContents(go); }
        }

        /// <summary>Resolves and validates every row and returns the writes as one action over objects resolved here,
        /// so nothing is looked up again once writing starts. Returns the first refusal, or null.</summary>
        static string Plan(Transform root, Table t, bool play, List<string> log, List<VRCPhysBoneBase> named, List<(string, VRCConstraintBase)> baked, out Action writes)
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
                if (s.create && s.set.Length == 0) continue;   // created already, nothing to set; a move row may split it
                var at = Find(root, s.physbone, out err); if (at == null) return err;
                var pb = ResolvePhysBone(root, s.physbone, targets, out err); if (pb == null) return err;
                bool moved = targets.ContainsKey(at);
                if (s.remove)
                {
                    if (moved || targets.ContainsValue(pb)) return "physbones '" + s.physbone + "' is moved by this table; drop the move rather than removing what it moves";
                    log.Add("remove physbone `" + PathOf(root, pb.transform) + "` (root `" + PathOf(root, EffRoot(pb)) + "`)");
                    steps.Add(() => Undo.DestroyObjectImmediate(pb));
                    continue;
                }
                if (!moved && targets.ContainsValue(pb)) return "physbone '" + s.physbone + "' is moved by this table; set fields on its new roots";
                var so = new SerializedObject(pb);
                foreach (var kv in s.set) if ((err = SetProp(root, so, kv)) != null) return "physbones '" + s.physbone + "': " + err;
                if (play && pb.transform == root) return "physbones '" + s.physbone + "' is hosted on '" + root.name + "' itself, and a live field set applies only by cycling its host inactive, which would restart the whole avatar; move it to a holder in edit mode";
                log.Add("set `" + s.physbone + "` " + string.Join(" ", s.set));
                if (play && !named.Contains(pb)) named.Add(pb); // play has no moves, so the live physbone is pb
                steps.Add(() =>
                {
                    var live = moved ? copies[at] : pb; var lso = new SerializedObject(live);
                    foreach (var kv in s.set) SetProp(root, lso, kv);
                    lso.ApplyModifiedProperties();
                });
            }
            foreach (var r in t.colliders)
            {
                var at = Find(root, r.collider, out err); if (at == null) return "colliders '" + r.collider + "': " + err;
                var cols = at.GetComponents<VRCPhysBoneColliderBase>();
                if (cols.Length != 1) return "colliders '" + r.collider + "' carries " + cols.Length + " physbone colliders; this row names exactly one" + (cols.Length == 0 ? " (a new one is a nodes row's collider)" : "");
                var col = cols[0]; var so = new SerializedObject(col);
                foreach (var kv in r.set) if ((err = SetProp(root, so, kv)) != null) return "colliders '" + r.collider + "': " + err;
                log.Add("collider `" + r.collider + "` " + string.Join(" ", r.set) + (play ? " (ApplyConfigurationChanges)" : ""));
                steps.Add(() =>
                {
                    var lso = new SerializedObject(col);
                    foreach (var kv in r.set) SetProp(root, lso, kv);
                    lso.ApplyModifiedProperties();
                    // In play the solver keeps the shape it built at enable: a field write, a collider GameObject cycle and a
                    // physbone host cycle all leave it on the old one. This call is the route that reaches it.
                    if (play) col.ApplyConfigurationChanges();
                });
            }
            var seen = new HashSet<Transform>();
            foreach (var c in t.constraints)
            {
                var target = Find(root, c.target, out err); if (target == null) return err;
                if (!seen.Add(target)) return "constraints '" + c.target + "' appears in two rows; one row per target";
                if (c.remove)
                {
                    var rtype = TypeCache.GetTypesDerivedFrom<VRCConstraintBase>().FirstOrDefault(x => !x.IsAbstract && x.Name == c.type);
                    var gone = rtype == null ? null : target.GetComponent(rtype);
                    if (gone == null) return "constraints '" + c.target + "': remove names no " + c.type + " on it (set type to the one it carries)";
                    log.Add("remove " + c.type + " on `" + c.target + "`");
                    steps.Add(() => Undo.DestroyObjectImmediate(gone));
                    continue;
                }
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
                steps.Add(() => baked.Add((c.target, WriteConstraint(existing ?? (VRCConstraintBase)Undo.AddComponent(target.gameObject, type), src, c))));
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
        static string CycleHosts(Transform root, string handle, string summary, List<VRCPhysBoneBase> named, List<string> log)
        {
            var hosts = named.Select(p => p.gameObject).Distinct().ToList();
            var cycled = hosts.Where(h => h.activeInHierarchy).ToArray();
            foreach (var h in hosts.Except(cycled)) log.Add("host `" + PathOf(root, h.transform) + "` is inactive: its chain takes the written fields when it activates");
            int collateral = cycled.SelectMany(h => h.GetComponentsInChildren<VRCPhysBoneBase>()).Distinct().Count(p => !named.Contains(p));
            if (cycled.Length == 0)
            {
                var done = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "write-dynamics_" + RunLogFormat.Leaf(handle), summary + " | reinit: none, every host is inactive and its chain takes the written fields when it activates", string.Join("\n", log) + "\n", ".md");
                SessionState.SetString(Key, done + "\n" + string.Join("\n", log)); Debug.Log(done);
                return done + "\n" + string.Join("\n", log.Take(12)) + (log.Count > 12 ? "\n… " + (log.Count - 12) + " more rows in the log" : "");
            }
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
                int back = 0; foreach (var h in cycled) if (h != null) { h.SetActive(true); back++; }
                var line = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "write-dynamics_" + RunLogFormat.Leaf(handle), summary + " | reinit: " + back
                    + " hosts cycled inactive frames " + armed + "->" + Time.frameCount + " (collateral: " + collateral + " other physbones beneath them" + (back < cycled.Length ? "; " + (cycled.Length - back) + " hosts destroyed before re-activation" : "")
                    + "); each chain restarted from its current pose, which is now its rest, and any grab on it stops acting", string.Join("\n", log) + "\n", ".md");
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

        /// <summary>Creates each <c>create</c> row's physbone on the bone it names, before the moves resolve, so a move row
        /// may split it: a bare one, or a copy of the <c>copy</c> physbone less the two fields that name the donor's own
        /// chain (<c>rootTransform</c>, so it roots at its host; <c>parameter</c>, which one animator name cannot serve
        /// twice). A bone that already hosts or roots a physbone is refused. Undo-registered like the nodes.</summary>
        static string MakePhysBones(Transform root, PhysBoneSet[] rows, List<string> log)
        {
            var pbType = TypeCache.GetTypesDerivedFrom<VRCPhysBoneBase>().First(x => !x.IsAbstract);
            foreach (var r in rows.Where(r => r.create))
            {
                var at = Find(root, r.physbone, out var err); if (at == null) return "physbones '" + r.physbone + "': " + err;
                var there = root.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.transform == at || EffRoot(p) == at).ToList();
                if (there.Count > 0) return "physbones '" + r.physbone + "': create on a bone that already hosts or roots " + there.Count + " physbone(s) (" + string.Join(", ", there.Select(p => PathOf(root, p.transform) + " -> " + PathOf(root, EffRoot(p)))) + "); drop create to set its fields";
                VRCPhysBoneBase donor = null;
                if (r.copy.Length > 0 && (donor = ResolvePhysBone(root, r.copy, new Dictionary<Transform, VRCPhysBoneBase>(), out err)) == null) return "physbones '" + r.physbone + "' copy: " + err;
                var pb = (VRCPhysBoneBase)Undo.AddComponent(at.gameObject, donor != null ? donor.GetType() : pbType);
                if (donor != null) { EditorUtility.CopySerialized(donor, pb); pb.rootTransform = null; pb.parameter = ""; }
                log.Add("create physbone `" + r.physbone + "`" + (donor != null ? " as a copy of `" + PathOf(root, donor.transform) + "` (rootTransform and parameter cleared)" : " (bare)"));
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
        static VRCConstraintBase WriteConstraint(VRCConstraintBase con, Transform[] src, ConstraintRow row)
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
            return con;
        }

        /// <summary>The rotation offset each written constraint holds after its write, as an angle: a locked row's Activate
        /// bakes the gap between target and sources into it, so a large one says the sources sit in a far-off frame.
        /// <c>RotationOffset</c> where the type has one (rotation, aim, look-at), else the largest per-source
        /// <c>ParentRotationOffset</c> (parent); a position or scale constraint has none and is skipped.</summary>
        static string BakedOffsets(List<(string target, VRCConstraintBase con)> baked, List<string> log)
        {
            var deg = new List<float>();
            foreach (var (target, con) in baked)
            {
                var so = new SerializedObject(con); var ro = so.FindProperty("RotationOffset"); float a;
                if (ro != null) a = Quaternion.Angle(Quaternion.identity, Quaternion.Euler(ro.vector3Value));
                else
                {
                    if (con.GetType().Name != "VRCParentConstraint") continue;
                    int n = Math.Min(so.FindProperty("Sources.totalLength").intValue, KeyableSlots);
                    if (n == 0) continue;
                    a = Enumerable.Range(0, n).Max(i => Quaternion.Angle(Quaternion.identity, Quaternion.Euler(so.FindProperty("Sources.source" + i + ".ParentRotationOffset").vector3Value)));
                }
                deg.Add(a); log.Add("baked `" + target + "` |rotationOffset|=" + a.ToString("0.0", CultureInfo.InvariantCulture) + " deg");
            }
            return deg.Count == 0 ? "" : " | baked |rotationOffset| deg mean=" + deg.Average().ToString("0.00", CultureInfo.InvariantCulture) + " max=" + deg.Max().ToString("0.0", CultureInfo.InvariantCulture) + " over " + deg.Count;
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
                case SerializedPropertyType.Quaternion:
                    var r = ParseVector(val); if (r == null) return bad + "; expected x,y,z Euler degrees"; p.quaternionValue = Quaternion.Euler(r.Value); return null;
                case SerializedPropertyType.AnimationCurve:
                    var cerr = ParseCurve(val, out var curve); if (cerr != null) return bad + "; " + cerr; p.animationCurveValue = curve; return null;
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

        /// <summary>A curve value is <c>time:value</c> keys, comma-separated, times strictly increasing, joined by straight
        /// lines (linear tangents both sides): <c>0:0.47,0.5:0.47,1:1</c>. Empty clears the curve (no keys). Pure.</summary>
        internal static string ParseCurve(string s, out AnimationCurve curve)
        {
            curve = new AnimationCurve(); var inv = CultureInfo.InvariantCulture;
            if (s.Trim().Length == 0) return null;
            var keys = new List<Keyframe>();
            foreach (var k in s.Split(','))
            {
                var tv = k.Split(':');
                if (tv.Length != 2 || !float.TryParse(tv[0], NumberStyles.Float, inv, out var t) || !float.TryParse(tv[1], NumberStyles.Float, inv, out var v)) return "expected time:value keys, comma-separated (0:0.47,0.5:0.47,1:1)";
                if (keys.Count > 0 && t <= keys[keys.Count - 1].time) return "key times must strictly increase";
                keys.Add(new Keyframe(t, v));
            }
            curve = new AnimationCurve(keys.ToArray());
            for (int i = 0; i < curve.length; i++) { AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear); AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear); }
            return null;
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
            if (!text.StartsWith("{")) return "the table is a JSON object ({\"nodes\":[…],\"moves\":[…],\"physbones\":[…],\"colliders\":[…],\"constraints\":[…]}) or a path to a file holding one";
            try { t = JsonUtility.FromJson<Table>(text); } catch (ArgumentException e) { return "table JSON does not parse: " + e.Message; }
            for (int i = 0; i < t.nodes.Length; i++)
                if (string.IsNullOrEmpty(t.nodes[i].parent) || string.IsNullOrEmpty(t.nodes[i].name) || t.nodes[i].name.Contains("/")) return "nodes[" + i + "] needs parent and a bare name";
            for (int i = 0; i < t.moves.Length; i++)
                if (string.IsNullOrEmpty(t.moves[i].physbone) || string.IsNullOrEmpty(t.moves[i].from) || t.moves[i].to.Length == 0) return "moves[" + i + "] needs physbone, from, and a non-empty to";
            for (int i = 0; i < t.physbones.Length; i++)
            {
                var r = t.physbones[i];
                if (string.IsNullOrEmpty(r.physbone) || r.set.Any(s => s.IndexOf('=') < 1)) return "physbones[" + i + "] needs physbone and a set of \"field=value\" strings";
                if (r.remove && (r.create || r.set.Length > 0 || r.copy.Length > 0)) return "physbones[" + i + "]: remove takes no create, copy or set";
                if (r.copy.Length > 0 && !r.create) return "physbones[" + i + "]: copy names create's donor; add \"create\":true";
                if (!r.create && !r.remove && r.set.Length == 0) return "physbones[" + i + "] does nothing; a row sets fields, creates or removes";
            }
            for (int i = 0; i < t.colliders.Length; i++)
                if (string.IsNullOrEmpty(t.colliders[i].collider) || t.colliders[i].set.Length == 0 || t.colliders[i].set.Any(s => s.IndexOf('=') < 1)) return "colliders[" + i + "] needs collider (the GameObject carrying it) and a set of \"field=value\" strings";
            for (int i = 0; i < t.constraints.Length; i++)
            {
                if (string.IsNullOrEmpty(t.constraints[i].target) || t.constraints[i].sources.Any(s => string.IsNullOrEmpty(s.path))) return "constraints[" + i + "] needs a target and every source a path";
                if (t.constraints[i].remove && t.constraints[i].sources.Length > 0) return "constraints[" + i + "]: remove takes no sources";
            }
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
