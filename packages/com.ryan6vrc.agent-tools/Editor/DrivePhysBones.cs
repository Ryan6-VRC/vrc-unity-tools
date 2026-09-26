using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Play-mode drive: pose, hold, sample the physbone tips under a root. Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class DrivePhysBones
    {
        [Serializable] public class PoseList { public Pose[] poses = new Pose[0]; }
        [Serializable] public class Pose { public string name; public BoneOp[] ops = new BoneOp[0]; }
        [Serializable] public class BoneOp { public string bone, move = ""; public float pitch, yaw, roll; }

        const string Tag = "[DrivePhysBones]", Key = "Ryan6Vrc.DrivePhysBones.status", RecordKey = "Ryan6Vrc.DrivePhysBones.restore";
        const int StableFrames = 60;        // the Animator instance must survive this many evaluated frames before rest is frozen
        static EditorApplication.CallbackFunction _pump;
        internal static bool Running => _pump != null;

        public static string Status() => SessionState.GetString(Key, Tag + " idle: no drive has run this editor session");

        public static string Run(string avatarRoot, string poses, string chainRoot, string stage = "drive", string bodyMesh = null, string[] garments = null, float hold = 2.5f, string outDir = null, string[] views = null)
        {
            if (!EditorApplication.isPlaying) return Fail("play mode only: enter play (isCompiling and isUpdating both false), then call again");
            if (_pump != null) return Fail("a drive is already running: poll DrivePhysBones.Status()");
            if (SessionState.GetString(RecordKey, "").Length > 0) return Fail("a torn-down drive's restore record is unresolved; it resolves on the next editor tick — poll DrivePhysBones.Status(), then call again");
            if (EditorApplication.isPaused) return Fail("the editor is paused (a held GrabPhysBone freezes it): GrabPhysBone.Release(resume: true) first");
            var err = ParsePoses(poses, out var pl); if (err != null) return Fail(err);
            var h = SceneHandle.Resolve(avatarRoot); if (!h.Ok) return Fail(h.Refusal);
            var rt = h.Object.transform;
            if (!h.Object.activeInHierarchy) return Fail("'" + avatarRoot + "' is inactive: an inactive avatar is never built or simulated, so every number would read zero; activate it in edit mode and re-enter play");
            if (!rt.GetComponentsInChildren<Transform>(true).Any(t => t.name.Contains("$"))) return Fail("'" + avatarRoot + "' carries no merged ($-suffixed) bones, so the play build did not run on it; exit play, let the editor settle, enter again");
            var chain = WriteDynamics.Find(rt, chainRoot, out err); if (chain == null) return Fail(err);
            var pbs = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.isActiveAndEnabled && WriteDynamics.EffRoot(p).IsChildOf(chain)).ToArray();
            if (pbs.Length == 0) return Fail("no active, enabled physbone is rooted at or under '" + chainRoot + "'");
            // A tip is the far end of its leaf's own segment, so an endpoint-only chain, whose one transform never translates, still reads.
            var tips = pbs.SelectMany(p => Leaves(WriteDynamics.EffRoot(p), new HashSet<Transform>(p.ignoreTransforms.Where(x => x != null))).Select(l => (leaf: l, ep: p.endpointPosition, frame: WriteDynamics.EffRoot(p).parent))).Distinct().ToArray();
            SkinnedMeshRenderer body = null; SkinnedMeshRenderer[] gar = null;
            if (bodyMesh != null && (err = ReportPenetration.Resolve(rt, bodyMesh, garments, out body, out gar)) != null) return Fail(err);
            var ops = new List<(Transform bone, BoneOp op)>[pl.poses.Length];
            for (int i = 0; i < ops.Length; i++)
            {
                ops[i] = new List<(Transform, BoneOp)>();
                foreach (var op in pl.poses[i].ops) { var b = WriteDynamics.Find(rt, op.bone, out err); if (b == null) return Fail("poses '" + pl.poses[i].name + "': " + err); ops[i].Add((b, op)); }
            }
            stage = RunLogFormat.Sanitize(string.IsNullOrEmpty(stage) ? "drive" : stage);
            views = views ?? new[] { "front", "back", "left", "right" };
            outDir = Path.GetFullPath(outDir ?? Path.Combine("Temp", "DrivePhysBones", RunLogFormat.Sanitize(rt.name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
            Directory.CreateDirectory(outDir);

            var moved = ops.SelectMany(l => l.Select(o => o.bone)).Distinct().ToArray();
            var restP = new Vector3[moved.Length]; var restR = new Quaternion[moved.Length]; var poseP = new Vector3[moved.Length]; var poseR = new Quaternion[moved.Length];
            void Put(Vector3[] p, Quaternion[] q) { for (int i = 0; i < moved.Length; i++) { moved[i].localPosition = p[i]; moved[i].localRotation = q[i]; } }
            Vector3 Tip((Transform leaf, Vector3 ep, Transform frame) t) => t.leaf.TransformPoint(t.ep);
            Vector3[] Avatar() => tips.Select(t => rt.InverseTransformPoint(Tip(t))).ToArray();
            Vector3[] InFrame() => tips.Select(t => t.frame ? t.frame.InverseTransformPoint(Tip(t)) : Tip(t)).ToArray();
            Matrix4x4[] Frames() => tips.Select(t => t.frame ? rt.worldToLocalMatrix * t.frame.localToWorldMatrix : Matrix4x4.identity).ToArray();

            string rootHandle = h.Object.GetInstanceID().ToString(CultureInfo.InvariantCulture), rootPath = FullPath(rt);
            var log = new StringBuilder(); var moving = new bool[tips.Length]; var jit = new List<float>();
            Vector3[] rest = null, prevAv = null, prevLocal = null; Matrix4x4[] frame0 = null;
            bool frozen = false, animEnable = true, frameMoved = false; int row = 0, lastFrame = -1, stable = 0, lastAnim = int.MinValue, frameFails = 0; string firstFrameFail = null;
            float t0 = 0; double lastTick = EditorApplication.timeSinceStartup, armedAt = lastTick;
            void Finish(string verdict)
            {
                EditorApplication.update -= _pump; _pump = null;
                if (frozen) { try { Put(restP, restR); var a = rt.GetComponent<Animator>(); if (a) a.enabled = animEnable; } catch (Exception) { } } // gone after a play exit
                SessionState.EraseString(RecordKey);
                string drive = Path.Combine(outDir, stage + "_drive.txt"), status = Tag + " " + verdict;
                try { File.WriteAllText(drive, log.ToString()); status += " | " + drive; } catch (Exception e) { status = Tag + " " + verdict + " | drive.txt write FAILED: " + e.Message; }
                SessionState.SetString(Key, status + "\n" + log); Debug.Log(status);
            }
            _pump = () =>
            {
                try
                {
                    if (!EditorApplication.isPlaying) { Finish("aborted: play exited => FAIL"); return; }
                    if (Time.frameCount == lastFrame)
                    {
                        if (EditorApplication.timeSinceStartup - lastTick > 20) Finish("aborted: the player loop stalled for 20 s (a modal, a paused editor, or runInBackground off) => FAIL");
                        return;
                    }
                    lastFrame = Time.frameCount; lastTick = EditorApplication.timeSinceStartup;
                    var anim = rt.GetComponent<Animator>();
                    if (!frozen)
                    {
                        // An emulator destroys and re-adds the Animator at start and on reset; rest is whatever it last wrote, so wait it out.
                        int id = anim ? anim.GetInstanceID() : 0;
                        stable = id == lastAnim ? stable + 1 : 0; lastAnim = id;
                        if (stable < StableFrames) { if (lastTick - armedAt > 30) Finish("aborted: the Animator was rebuilt continuously for 30 s, so no rest pose could be frozen => FAIL"); return; }
                        frozen = true; animEnable = anim == null || anim.enabled;
                        for (int i = 0; i < moved.Length; i++) { restP[i] = poseP[i] = moved[i].localPosition; restR[i] = poseR[i] = moved[i].localRotation; }
                        SessionState.SetString(RecordKey, FormatRecord(rootPath, animEnable, moved.Select((b, i) => (AnimationUtility.CalculateTransformPath(b, rt), restP[i], restR[i])).ToList()));
                        frame0 = Frames(); t0 = Time.time; SessionState.SetString(Key, Tag + " running 0/" + pl.poses.Length + " (rest frozen at frame " + Time.frameCount + ")\n");
                        log.Append("root=" + rootPath + " chainRoot=" + AnimationUtility.CalculateTransformPath(chain, rt) + " physbones=" + pbs.Length + " tips=" + tips.Length + " hold=" + hold + "s freezeFrame=" + Time.frameCount
                            + " animator=" + id + (anim && !anim.enabled ? "(found disabled)" : "") + " body=" + (body ? body.name : "-") + " frames=" + outDir + "\nstage | pose | tipTravelCm max/mean | jitterMmPerFrame | garmentVertsInsideBody/tested | maxDepthCm\n");
                    }
                    if (anim) anim.enabled = false;   // re-asserted every frame: an emulator re-enables it
                    Put(poseP, poseR);                // and whatever wrote a posed bone is overwritten before sampling
                    var av = Avatar(); var local = InFrame(); var fr = Frames();
                    for (int i = 0; i < fr.Length && !frameMoved; i++) frameMoved = !Approximately(fr[i], frame0[i]);
                    if (prevLocal != null) for (int i = 0; i < local.Length; i++) moving[i] |= (local[i] - prevLocal[i]).sqrMagnitude > 1e-12f;
                    if (prevAv != null && Time.time - t0 > hold - 0.5f) jit.Add(av.Select((c, i) => (c - prevAv[i]).magnitude).Max());
                    prevAv = av; prevLocal = local;
                    if (Time.time - t0 < hold) return;
                    string name = row == 0 ? "rest" : pl.poses[row - 1].name;
                    if (row == 0) rest = av;
                    var mv = av.Select((c, i) => (c - rest[i]).magnitude * 100).ToArray();
                    var pen = body ? ReportPenetration.Measure(rt, body, gar) : (-1, 0, 0f);
                    log.Append(stage + " | " + name + " | " + mv.Max().ToString("F1") + "/" + mv.Average().ToString("F1") + " | " + (jit.Count > 0 ? jit.Average() * 1000 : 0).ToString("F2") + " | "
                        + (pen.Item1 < 0 ? "- | -" : pen.Item1 + "/" + pen.Item2 + " | " + pen.Item3.ToString("F1")) + "\n");
                    var shot = RenderAvatar.Run(rootHandle, views, null, 0.15f, false, 512, Path.Combine(outDir, stage + "_" + RunLogFormat.Sanitize(name)));
                    if (!shot.Contains("=> OK")) { frameFails++; firstFrameFail = firstFrameFail ?? shot; }
                    jit.Clear(); prevAv = null; row++;
                    if (row > pl.poses.Length)
                    {
                        int still = moving.Count(m => !m);
                        Finish(still == tips.Length && frameMoved ? "done => FAIL | the chain frames moved but no tip moved within its frame in any frame: the solver did not run"
                            : frameFails > 0 ? "done => FAIL | " + frameFails + " frame grab(s) failed; first: " + firstFrameFail
                            : "done => OK | tipsStillInFrame=" + still + "/" + tips.Length + " chainFrameMoved=" + frameMoved.ToString().ToLowerInvariant());
                        return;
                    }
                    Put(restP, restR);
                    foreach (var o in ops[row - 1]) Apply(rt, o.bone, o.op);
                    for (int i = 0; i < moved.Length; i++) { poseP[i] = moved[i].localPosition; poseR[i] = moved[i].localRotation; }
                    t0 = Time.time;
                    SessionState.SetString(Key, Tag + " running " + row + "/" + pl.poses.Length + "\n" + log);
                }
                catch (Exception e) { Finish("error " + e.GetType().Name + ": " + e.Message + " => FAIL"); }
            };
            SessionState.SetString(Key, Tag + " running: waiting for a stable Animator\n"); EditorApplication.update += _pump;
            return Tag + " Drive " + rt.name + " => PENDING | " + (pl.poses.Length + 1) + " rows (rest first), " + tips.Length + " tips, hold " + hold + "s; poll Ryan6Vrc.AgentTools.Editor.DrivePhysBones.Status() | frames=" + outDir;
        }

        /// <summary>World turns about the avatar's axes: +pitch swings what hangs below forward, +yaw swings what points
        /// forward to the right, +roll swings what hangs below to the right; <c>move</c> is avatar-space metres.</summary>
        static void Apply(Transform rt, Transform bone, BoneOp op)
        {
            bone.rotation = Quaternion.AngleAxis(op.roll, rt.forward) * Quaternion.AngleAxis(op.yaw, rt.up) * Quaternion.AngleAxis(-op.pitch, rt.right) * bone.rotation;
            var m = string.IsNullOrEmpty(op.move) ? null : WriteDynamics.ParseVector(op.move);
            if (m != null) bone.position += rt.TransformDirection(m.Value);
        }

        static IEnumerable<Transform> Leaves(Transform t, HashSet<Transform> ignore)
        {
            var kids = t.Cast<Transform>().Where(c => !ignore.Contains(c)).ToList();
            return kids.Count == 0 ? new[] { t } : kids.SelectMany(c => Leaves(c, ignore));
        }

        static bool Approximately(Matrix4x4 a, Matrix4x4 b) { for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-5f) return false; return true; }

        static string FullPath(Transform t) => t.parent == null ? t.name : FullPath(t.parent) + "/" + t.name;

        // ── Restore record: survives a domain reload that drops the pump with bones posed ──────────────

        internal static string FormatRecord(string rootPath, bool enableAnimator, List<(string path, Vector3 p, Quaternion q)> bones)
        {
            string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
            return "v1\n" + rootPath + "\n" + (enableAnimator ? "1" : "0") + string.Concat(bones.Select(b => "\n" + b.path + "\t" + F(b.p.x) + "," + F(b.p.y) + "," + F(b.p.z) + "\t" + F(b.q.x) + "," + F(b.q.y) + "," + F(b.q.z) + "," + F(b.q.w)));
        }

        internal static bool TryParseRecord(string raw, out string rootPath, out bool enableAnimator, out List<(string path, Vector3 p, Quaternion q)> bones)
        {
            rootPath = null; enableAnimator = false; bones = new List<(string, Vector3, Quaternion)>();
            var lines = (raw ?? "").Split('\n');
            if (lines.Length < 3 || lines[0] != "v1") return false;
            rootPath = lines[1]; enableAnimator = lines[2] == "1";
            foreach (var l in lines.Skip(3))
            {
                var f = l.Split('\t'); if (f.Length != 3) return false;
                var p = f[1].Split(',').Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN).ToArray();
                var q = f[2].Split(',').Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN).ToArray();
                if (p.Length != 3 || q.Length != 4 || p.Concat(q).Any(float.IsNaN)) return false;
                bones.Add((f[0], new Vector3(p[0], p[1], p[2]), new Quaternion(q[0], q[1], q[2], q[3])));
            }
            return true;
        }

        [InitializeOnLoadMethod]
        static void Install()
        {
            // On `update`, not `delayCall`: delayCall is not pumped while the Editor is unfocused (unity.md §Sharp edges).
            EditorApplication.CallbackFunction once = null;
            once = () => { EditorApplication.update -= once; Recover(); };
            EditorApplication.update += once;
        }

        static void Recover()
        {
            var raw = SessionState.GetString(RecordKey, ""); var prior = Status();   // an absent key reads "", never null
            if (raw.Length == 0 && !prior.StartsWith(Tag + " running")) return;
            string what = "nothing was posed yet";
            if (raw.Length > 0 && TryParseRecord(raw, out var rootPath, out var enable, out var bones))
            {
                var h = EditorApplication.isPlaying ? SceneHandle.Resolve(rootPath) : new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };
                if (!h.Ok) what = EditorApplication.isPlaying ? "the avatar '" + rootPath + "' no longer resolves, so its posed bones were not restored" : "play had exited, taking the posed avatar with it";
                else
                {
                    int n = 0; foreach (var b in bones) { var t = WriteDynamics.Find(h.Object.transform, b.path, out _); if (t) { t.localPosition = b.p; t.localRotation = b.q; n++; } }
                    var a = h.Object.GetComponent<Animator>(); if (a) a.enabled = enable;
                    what = "restored " + n + "/" + bones.Count + " posed bones and the Animator";
                }
            }
            SessionState.EraseString(RecordKey);
            SessionState.SetString(Key, Tag + " RECOVERED from a domain reload mid-drive: " + what + "; its rows are void => FAIL\n" + prior);
        }

        /// <summary>Pose list shape rules. Pure: no scene.</summary>
        internal static string ParsePoses(string text, out PoseList pl)
        {
            pl = null; text = text?.Trim() ?? "";
            if (!text.StartsWith("{") && !text.StartsWith("[") && File.Exists(text)) text = File.ReadAllText(text).Trim();
            if (text.StartsWith("[")) text = "{\"poses\":" + text + "}";
            if (!text.StartsWith("{")) return "poses is JSON ({\"poses\":[{\"name\":…,\"ops\":[{\"bone\":…,\"pitch\":…}]}]} or the bare array) or a path to a file holding it";
            try { pl = JsonUtility.FromJson<PoseList>(text); } catch (ArgumentException e) { return "poses JSON does not parse: " + e.Message; }
            if (pl.poses.Length == 0) return "poses is empty; the rest row is a baseline for poses, not a drive of its own";
            foreach (var p in pl.poses)
            {
                if (string.IsNullOrEmpty(p.name) || p.name == "rest") return "every pose needs a name other than 'rest' (the baseline row owns it)";
                if (p.ops.Any(o => string.IsNullOrEmpty(o.bone))) return "poses '" + p.name + "': every op needs a bone path";
                if (p.ops.Any(o => !string.IsNullOrEmpty(o.move) && WriteDynamics.ParseVector(o.move) == null)) return "poses '" + p.name + "': move is x,y,z metres";
            }
            return null;
        }

        static string Fail(string m) { var s = Tag + " FAIL: " + m; Debug.LogWarning(s); return s; }
    }
}
