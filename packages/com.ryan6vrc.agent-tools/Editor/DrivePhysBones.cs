using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Play-mode drive of a built avatar: poses named bones in avatar space, holds, then samples every physbone
    /// chain rooted at or under a named root — tip travel from rest (cm), per-frame tip jitter over the hold's last half
    /// second (mm), and, given a body and garments, garment vertices inside the body — and captures five frames per pose.
    /// A <c>rest</c> row always runs first and is the travel baseline. Poses are data:
    /// <c>{"poses":[{"name":"legL90","ops":[{"bone":"Upper_leg.L","pitch":90}]}]}</c> (a bare array also parses). Each op
    /// turns the bone about an avatar axis, applied pitch, yaw, roll, move: +pitch swings what hangs below it forward, +roll
    /// swings it toward the avatar's right, +yaw swings what points forward toward the right — each sense detected against
    /// the avatar's own frame, so a mirrored root still reads right; <c>move</c> is an avatar-space offset in metres, <c>x,y,z</c>. Disables the root's Animator while it runs, restores every moved transform after
    /// each pose and at the end. Arms an EditorApplication.update pump and returns at once; poll <see cref="Status"/>.</summary>
    [AgentTool]
    public static class DrivePhysBones
    {
        [Serializable] public class PoseList { public Pose[] poses = new Pose[0]; }
        [Serializable] public class Pose { public string name; public BoneOp[] ops = new BoneOp[0]; }
        [Serializable] public class BoneOp { public string bone, move = ""; public float pitch, yaw, roll; }

        const string Tag = "[DrivePhysBones]", Key = "Ryan6Vrc.DrivePhysBones.status";
        static EditorApplication.CallbackFunction _pump;

        public static string Status() => SessionState.GetString(Key, "idle: no drive has run this editor session");

        public static string Run(string avatarRoot, string poses, string chainRoot, string bodyMesh = null, string[] garments = null, float hold = 2.5f, string outDir = null)
        {
            if (!EditorApplication.isPlaying) return Fail("play mode only: enter play (isCompiling and isUpdating both false), then call again");
            if (_pump != null) return Fail("a drive is already running: poll DrivePhysBones.Status()");
            if (EditorApplication.isPaused) return Fail("the editor is paused (a held GrabPhysBone freezes it): GrabPhysBone.Release(resume: true) first");
            var err = ParsePoses(poses, out var pl); if (err != null) return Fail(err);
            var h = SceneHandle.Resolve(avatarRoot); if (!h.Ok) return Fail(h.Refusal);
            var rt = h.Object.transform; var all = rt.GetComponentsInChildren<Transform>(true);
            if (!h.Object.activeInHierarchy) return Fail("'" + avatarRoot + "' is inactive: an inactive avatar is never built or simulated, so every number would read zero; activate it in edit mode and re-enter play");
            if (!all.Any(t => t.name.Contains("$"))) return Fail("'" + avatarRoot + "' carries no merged ($-suffixed) bones, so the play build did not run on it; exit play, let the editor settle, enter again");
            var chain = Bone(rt, all, chainRoot, out err); if (chain == null) return Fail(err);
            var pbs = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => WriteDynamics.EffRoot(p).IsChildOf(chain)).ToArray();
            if (pbs.Length == 0) return Fail("no physbone is rooted at or under '" + WriteDynamics.PathOf(rt, chain) + "'");
            var tips = pbs.SelectMany(p => Leaves(WriteDynamics.EffRoot(p), new HashSet<Transform>(p.ignoreTransforms.Where(x => x != null)))).Distinct().ToArray();
            var smrs = rt.GetComponentsInChildren<SkinnedMeshRenderer>(true); SkinnedMeshRenderer body = null; var gar = new SkinnedMeshRenderer[0];
            if (!string.IsNullOrEmpty(bodyMesh))
            {
                body = bodyMesh.Split('|').Select(n => smrs.FirstOrDefault(s => s.name == n)).FirstOrDefault(s => s != null);
                gar = (garments ?? new string[0]).Select(n => smrs.FirstOrDefault(s => s.name == n)).ToArray();
                if (body == null || gar.Length == 0 || gar.Contains(null)) return Fail("body '" + bodyMesh + "' and every garment must name a SkinnedMeshRenderer under the root; present: " + string.Join(", ", smrs.Select(s => s.name)));
            }
            var ops = new List<(Transform bone, BoneOp op)>[pl.poses.Length];
            for (int i = 0; i < ops.Length; i++)
            {
                ops[i] = new List<(Transform, BoneOp)>();
                foreach (var op in pl.poses[i].ops)
                {
                    var b = Bone(rt, all, op.bone, out err); if (b == null) return Fail("poses '" + pl.poses[i].name + "': " + err);
                    ops[i].Add((b, op));
                }
            }
            outDir = Path.GetFullPath(outDir ?? Path.Combine("Temp", "DrivePhysBones", RunLogFormat.Sanitize(rt.name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            Directory.CreateDirectory(outDir);
            var moved = ops.SelectMany(l => l.Select(o => o.bone)).Distinct().ToArray();
            var restP = moved.Select(t => t.localPosition).ToArray(); var restR = moved.Select(t => t.localRotation).ToArray();
            var poseP = (Vector3[])restP.Clone(); var poseR = (Quaternion[])restR.Clone();
            Action restore = () => { for (int i = 0; i < moved.Length; i++) { moved[i].localPosition = restP[i]; moved[i].localRotation = restR[i]; } };
            Action holdPose = () => { for (int i = 0; i < moved.Length; i++) { moved[i].localPosition = poseP[i]; moved[i].localRotation = poseR[i]; } };
            var anim0 = rt.GetComponent<Animator>(); bool animWas = anim0 != null && anim0.enabled; // an emulator may rebuild it: re-fetched per tick
            var cam = new GameObject("__DrivePhysBones_cam").AddComponent<Camera>(); cam.enabled = false; cam.fieldOfView = 28; cam.nearClipPlane = 0.02f;
            cam.cullingMask = rt.GetComponentsInChildren<Renderer>(true).Aggregate(0, (m, r) => m | 1 << r.gameObject.layer); // not an emulator's mirror clone
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.18f, 0.18f, 0.2f);
            var rtex = new RenderTexture(768, 768, 24); var tex = new Texture2D(768, 768, TextureFormat.RGB24, false);
            var log = new StringBuilder("root=" + WriteDynamics.PathOf(null, rt) + " chainRoot=" + WriteDynamics.PathOf(rt, chain) + " physbones=" + pbs.Length + " tips=" + tips.Length + " hold=" + hold + "s body=" + (body ? body.name : "-") + " frames=" + outDir
                + "\npose | tipTravelCm max/mean | jitterMmPerFrame | garmentVertsInsideBody/tested | maxDepthCm\n");
            Vector3[] rest = null, prev = null; var moving = new bool[tips.Length]; var jit = new List<float>(); int row = -1, lastFrame = -1; float t0 = 0; double lastTick = EditorApplication.timeSinceStartup;
            Func<Vector3[]> Tips = () => tips.Select(x => rt.InverseTransformPoint(x.position)).ToArray();
            Action<string> finish = status =>
            {
                EditorApplication.update -= _pump; _pump = null; SessionState.SetString(Key, status + "\n" + log); Debug.Log(Tag + " " + status);
                try { File.WriteAllText(Path.Combine(outDir, "drive.txt"), status + "\n" + log); } catch (Exception) { }
                try { restore(); var a = rt.GetComponent<Animator>(); if (a) a.enabled = animWas; } catch (Exception) { } // after a play exit these are gone
                if (cam) UnityEngine.Object.DestroyImmediate(cam.gameObject); UnityEngine.Object.DestroyImmediate(rtex); UnityEngine.Object.DestroyImmediate(tex);
            };
            _pump = () =>
            {
                try
                {
                    if (!EditorApplication.isPlaying) { finish("aborted: play exited => FAIL"); return; }
                    if (Time.frameCount == lastFrame)
                    {
                        if (EditorApplication.timeSinceStartup - lastTick > 20) finish("aborted: the player loop stalled for 20 s (a modal, a paused editor, or runInBackground off) => FAIL");
                        return;
                    }
                    lastFrame = Time.frameCount; lastTick = EditorApplication.timeSinceStartup;
                    var anim = rt.GetComponent<Animator>(); if (anim) anim.enabled = false; // re-asserted every frame: the emulator re-enables it during its start-up
                    holdPose();                          // and whatever it wrote to a posed bone is overwritten before sampling
                    if (row < 0) { row = 0; t0 = Time.time; return; }
                    var cur = Tips();
                    if (prev != null) { for (int i = 0; i < cur.Length; i++) moving[i] |= cur[i] != prev[i]; if (Time.time - t0 > hold - 0.5f) jit.Add(cur.Select((c, i) => (c - prev[i]).magnitude).Max()); }
                    prev = cur;
                    if (Time.time - t0 < hold) return;
                    string name = row == 0 ? "rest" : pl.poses[row - 1].name;
                    if (row == 0) rest = cur;
                    var mv = cur.Select((c, i) => (c - rest[i]).magnitude * 100).ToArray();
                    var pen = body ? Penetration(rt, body, gar) : (-1, 0, 0f);
                    log.Append(name + " | " + mv.Max().ToString("F1") + "/" + mv.Average().ToString("F1") + " | " + (jit.Count > 0 ? jit.Average() * 1000 : 0).ToString("F2") + " | "
                        + (pen.Item1 < 0 ? "-" : pen.Item1 + "/" + pen.Item2) + " | " + (pen.Item1 < 0 ? "-" : pen.Item3.ToString("F1")) + "\n");
                    Frames(rt, chain.position, tips, cam, rtex, tex, Path.Combine(outDir, RunLogFormat.Sanitize(name)));
                    restore(); jit.Clear(); prev = null; row++; restP.CopyTo(poseP, 0); restR.CopyTo(poseR, 0);
                    if (row > pl.poses.Length)
                    {
                        int still = moving.Count(m => !m); // a settled chain reads exact zero; every tip still for the whole drive is a stopped solver
                        finish(still < tips.Length ? "done => OK | tipsNeverMoved=" + still + "/" + tips.Length + " | frames and drive.txt in " + outDir
                            : "done => FAIL | no tip moved in any frame, so every row reads zero from a solver that did not run; exit play, re-enter, re-run");
                        return;
                    }
                    foreach (var o in ops[row - 1]) Apply(rt, o.bone, o.op);
                    for (int i = 0; i < moved.Length; i++) { poseP[i] = moved[i].localPosition; poseR[i] = moved[i].localRotation; }
                    t0 = Time.time;
                    SessionState.SetString(Key, "running " + row + "/" + pl.poses.Length + "\n" + log);
                }
                catch (Exception e) { finish("error " + e.GetType().Name + ": " + e.Message + " => FAIL"); }
            };
            SessionState.SetString(Key, "running 0/" + pl.poses.Length + "\n" + log); EditorApplication.update += _pump;
            return Tag + " Drive " + rt.name + " => PENDING | " + (pl.poses.Length + 1) + " rows (rest first), " + tips.Length + " tips, hold " + hold + "s; poll Ryan6Vrc.AgentTools.Editor.DrivePhysBones.Status() | frames=" + outDir;
        }

        /// <summary>A world turn about an avatar axis, its sense fixed by a 1° test of where it carries a reference
        /// direction (build body's cone-sign idiom). A probe-bone test is degenerate once the probe lies along the target
        /// axis — a knee after a 90° leg raise moves its foot back in z both ways — so none is used.</summary>
        static void Apply(Transform rt, Transform bone, BoneOp op)
        {
            void Turn(float deg, Vector3 axis, Vector3 from, Vector3 to)
            {
                if (deg != 0) bone.rotation = Quaternion.AngleAxis((Vector3.Dot(Quaternion.AngleAxis(1f, axis) * from, to) > 0 ? 1 : -1) * deg, axis) * bone.rotation;
            }
            Turn(op.pitch, rt.right, -rt.up, rt.forward); Turn(op.yaw, rt.up, rt.forward, rt.right); Turn(op.roll, rt.forward, -rt.up, rt.right);
            var m = string.IsNullOrEmpty(op.move) ? null : WriteDynamics.ParseVector(op.move);
            if (m != null) bone.position += rt.TransformDirection(m.Value);
        }

        static IEnumerable<Transform> Leaves(Transform t, HashSet<Transform> ignore)
        {
            var kids = t.Cast<Transform>().Where(c => !ignore.Contains(c)).ToList();
            return kids.Count == 0 ? new[] { t } : kids.SelectMany(c => Leaves(c, ignore));
        }

        /// <summary>A bone by root-relative path, or by name anywhere under the root (exact name first, then the
        /// play-build <c>name$suffix</c>); an ambiguity refuses with the candidate paths.</summary>
        static Transform Bone(Transform rt, Transform[] all, string name, out string err)
        {
            err = null;
            if (name != null && name.Contains("/")) return WriteDynamics.Find(rt, name, out err);
            var hits = all.Where(t => t.name == name).ToArray();
            if (hits.Length == 0) hits = all.Where(t => t.name.Split('$')[0] == name).ToArray();
            if (hits.Length == 1) return hits[0];
            err = "bone '" + name + "' " + (hits.Length == 0 ? "resolves to nothing under '" + rt.name + "'" : "matches " + hits.Length + ": " + string.Join(", ", hits.Select(x => WriteDynamics.PathOf(rt, x))) + " — pass a path");
            return null;
        }

        static void Frames(Transform rt, Vector3 focus, Transform[] tips, Camera cam, RenderTexture rtex, Texture2D tex, string stem)
        {
            float dist = Mathf.Max(0.6f, 4.5f * tips.Max(t => (t.position - focus).magnitude));
            var views = new[] { ("front", rt.forward), ("back", -rt.forward), ("left", -rt.right), ("right", rt.right), ("frontlow", (rt.forward * 0.8f - rt.up * 0.6f).normalized) };
            foreach (var (label, dir) in views)
            {
                cam.transform.position = focus + dir * dist; cam.transform.LookAt(focus, rt.up);
                cam.targetTexture = rtex; cam.Render(); RenderTexture.active = rtex;
                tex.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); tex.Apply(); RenderTexture.active = null; cam.targetTexture = null;
                File.WriteAllBytes(stem + "_" + label + ".png", tex.EncodeToPNG());
            }
        }

        /// <summary>Garment vertices (every other) inside the body: signed distance to the closest body triangle
        /// within 5 cm, below −2 mm counts. Body triangles are gridded only across the garments' own height band.</summary>
        static (int, int, float) Penetration(Transform rt, SkinnedMeshRenderer body, SkinnedMeshRenderer[] garments)
        {
            var gv = new List<Vector3>(); var m = new Mesh();
            foreach (var g in garments) { g.BakeMesh(m, true); var w = g.transform.localToWorldMatrix; gv.AddRange(m.vertices.Where((v, i) => i % 2 == 0).Select(v => rt.InverseTransformPoint(w.MultiplyPoint3x4(v)))); }
            body.BakeMesh(m, true); var bw = body.transform.localToWorldMatrix; var bv = m.vertices.Select(v => rt.InverseTransformPoint(bw.MultiplyPoint3x4(v))).ToArray(); var bt = m.triangles;
            UnityEngine.Object.DestroyImmediate(m);
            if (gv.Count == 0) return (0, 0, 0f);
            float lo = gv.Min(v => v.y) - 0.05f, hi = gv.Max(v => v.y) + 0.05f, cell = 0.05f;
            var grid = new Dictionary<Vector3Int, List<int>>();
            Vector3Int C(Vector3 p) => Vector3Int.FloorToInt(p / cell);
            for (int t = 0; t < bt.Length; t += 3)
            {
                Vector3 a = bv[bt[t]], b = bv[bt[t + 1]], c = bv[bt[t + 2]];
                if (Mathf.Max(a.y, b.y, c.y) < lo || Mathf.Min(a.y, b.y, c.y) > hi) continue;
                Vector3Int mn = C(Vector3.Min(a, Vector3.Min(b, c))), mx = C(Vector3.Max(a, Vector3.Max(b, c)));
                for (int x = mn.x; x <= mx.x; x++) for (int y = mn.y; y <= mx.y; y++) for (int z = mn.z; z <= mx.z; z++)
                { var k = new Vector3Int(x, y, z); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(t); }
            }
            int inside = 0; float depth = 0;
            foreach (var p in gv)
            {
                float best = 0.0025f; int bi = -1; Vector3 bq = default; var cp = C(p);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue(cp + new Vector3Int(dx, dy, dz), out var l))
                        foreach (var t in l) { var q = ClosestOnTriangle(p, bv[bt[t]], bv[bt[t + 1]], bv[bt[t + 2]]); float d = (q - p).sqrMagnitude; if (d < best) { best = d; bi = t; bq = q; } }
                if (bi < 0) continue;
                float s = Vector3.Dot(p - bq, Vector3.Cross(bv[bt[bi + 1]] - bv[bt[bi]], bv[bt[bi + 2]] - bv[bt[bi]]).normalized);
                if (s < -0.002f) { inside++; depth = Mathf.Max(depth, -s); }
            }
            return (inside, gv.Count, depth * 100f);
        }

        /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection §5.1.5).</summary>
        internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a; float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + d1 / (d1 - d3) * ab;
            Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + d2 / (d2 - d6) * ac;
            float va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);
            float den = 1f / (va + vb + vc); return a + ab * (vb * den) + ac * (vc * den);
        }

        /// <summary>Pose list shape rules. Pure: no scene.</summary>
        internal static string ParsePoses(string text, out PoseList pl)
        {
            pl = null; text = text?.Trim() ?? "";
            if (!text.StartsWith("{") && !text.StartsWith("[") && File.Exists(text)) text = File.ReadAllText(text).Trim();
            if (text.StartsWith("[")) text = "{\"poses\":" + text + "}";
            if (!text.StartsWith("{")) return "poses is JSON ({\"poses\":[{\"name\":…,\"ops\":[{\"bone\":…,\"pitch\":…}]}]} or the bare array) or a path to a file holding it";
            try { pl = JsonUtility.FromJson<PoseList>(text); } catch (ArgumentException e) { return "poses JSON does not parse: " + e.Message; }
            pl.poses = pl.poses ?? new Pose[0];
            if (pl.poses.Length == 0) return "poses is empty; the rest row runs on its own only as a baseline for poses";
            for (int i = 0; i < pl.poses.Length; i++)
            {
                var p = pl.poses[i]; p.ops = p.ops ?? new BoneOp[0];
                if (string.IsNullOrEmpty(p.name) || p.name == "rest") return "poses[" + i + "] needs a name other than 'rest' (the baseline row owns it)";
                if (p.ops.Any(o => string.IsNullOrEmpty(o.bone))) return "poses '" + p.name + "': every op needs a bone";
                if (p.ops.Any(o => !string.IsNullOrEmpty(o.move) && WriteDynamics.ParseVector(o.move) == null)) return "poses '" + p.name + "': move is x,y,z metres";
            }
            return null;
        }

        static string Fail(string m) { var s = Tag + " FAIL: " + m; Debug.LogWarning(s); return s; }
    }
}
