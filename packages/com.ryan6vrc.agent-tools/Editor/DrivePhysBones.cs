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
        /// <summary><c>ramp</c> is seconds: 0 snaps to the pose in one frame; above 0 the bones ease (smoothstep) from the
        /// previous row's pose to this one over that long, and the hold is timed from arrival.</summary>
        [Serializable] public class Pose { public string name; public BoneOp[] ops = new BoneOp[0]; public float ramp; }
        [Serializable] public class BoneOp { public string bone, move = ""; public float pitch, yaw, roll; }

        const string Tag = "[DrivePhysBones]", Key = "Ryan6Vrc.DrivePhysBones.status", RecordKey = "Ryan6Vrc.DrivePhysBones.restore";
        const string SessionKey = "Ryan6Vrc.DrivePhysBones.playSession", LogKey = "Ryan6Vrc.DrivePhysBones.log";
        const int StableFrames = 60;        // the Animator instance must survive this many evaluated frames before rest is frozen
        const float Still = 1e-5f;          // world metres: a per-frame tip displacement below this is no motion
        static EditorApplication.CallbackFunction _pump;
        internal static bool Running => _pump != null;

        /// <summary>The current play session's drive: running, or its verdict and rows. A record from an earlier play
        /// session is never returned as current; it is named as history, verdict masked, with its log path.</summary>
        public static string Status()
        {
            var s = SessionState.GetString(Key, Tag + " idle: no drive has run this editor session");
            if (s.StartsWith(Tag + " idle")) return s;
            return PlaySession.IsCurrent(SessionState.GetInt(SessionKey, 0)) ? s : PlaySession.Stale(Tag, "drive", s.Split('\n')[0]);
        }

        public static string Run(string avatarRoot, string poses, string chainPrefix = "", string stage = "drive", string bodyMesh = null, string[] garments = null, float hold = 2.5f, string outDir = null, string[] views = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return Fail("play entry is still in progress (the build runs first); call again once EditorApplication.isPlaying reads true — a drive armed now would be dropped by the play-entry domain reload");
            if (!EditorApplication.isPlaying) return Fail("play mode only: enter play (isCompiling and isUpdating both false), then call again");
            if (_pump != null) return Fail("a drive is already running: poll DrivePhysBones.Status()");
            if (SessionState.GetString(RecordKey, "").Length > 0) return Fail("a torn-down drive's restore record is unresolved; it resolves on the next editor tick — poll DrivePhysBones.Status(), then call again");
            if (WriteDynamics.Pending) return Fail("a WriteDynamics field set is still cycling its physbone hosts inactive; poll WriteDynamics.Status(), then call again");
            if (EditorApplication.isPaused) return Fail("the editor is paused (a held GrabPhysBone freezes it): GrabPhysBone.Release(resume: true) first");
            var err = ParsePoses(poses, out var pl); if (err != null) return Fail(err);
            var h = SceneHandle.Resolve(avatarRoot); if (!h.Ok) return Fail(h.Refusal);
            var rt = h.Object.transform;
            if (!h.Object.activeInHierarchy) return Fail("'" + avatarRoot + "' is inactive: an inactive avatar is never built or simulated, so every number would read zero; activate it in edit mode and re-enter play");
            var under = string.IsNullOrEmpty(chainPrefix) ? rt : WriteDynamics.Find(rt, chainPrefix, out err); if (under == null) return Fail(err);
            var pbs = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.isActiveAndEnabled && WriteDynamics.EffRoot(p).IsChildOf(under)).ToArray();
            if (pbs.Length == 0) return Fail("no active, enabled physbone is rooted at or under '" + (under == rt ? rt.name : chainPrefix) + "'");
            // A tip is the far end of its leaf's own segment, so an endpoint-only chain, whose one transform never translates, still
            // reads; a leaf two overlapping chains reach is one tip, measured in the first chain's frame.
            var tips = new List<(Transform leaf, Vector3 ep, Transform frame)>(); var chains = new List<(string path, Transform frame, int[] tips)>();
            foreach (var p in pbs)
            {
                var root = WriteDynamics.EffRoot(p); var frame = root.parent ? root.parent : rt;
                var idx = Leaves(root, new HashSet<Transform>(p.ignoreTransforms.Where(x => x != null))).Select(l =>
                { int i = tips.FindIndex(t => t.leaf == l); if (i < 0) { tips.Add((l, p.endpointPosition, frame)); i = tips.Count - 1; } return i; }).ToArray();
                chains.Add((AnimationUtility.CalculateTransformPath(root, rt), frame, idx));
            }
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
            outDir = Path.GetFullPath(outDir ?? Path.Combine("Temp", "DrivePhysBones", RunLogFormat.Sanitize(rt.name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)));
            if (views.Length > 0) Directory.CreateDirectory(outDir);

            var moved = ops.SelectMany(l => l.Select(o => o.bone)).Distinct().ToArray();
            var restP = new Vector3[moved.Length]; var restR = new Quaternion[moved.Length]; var poseP = new Vector3[moved.Length]; var poseR = new Quaternion[moved.Length];
            void Put(Vector3[] p, Quaternion[] q) { for (int i = 0; i < moved.Length; i++) { moved[i].localPosition = p[i]; moved[i].localRotation = q[i]; } }
            // World metres in an unscaled frame (position and rotation only), so a scaled root skews nothing.
            Vector3 In(Transform f, Vector3 w) => Quaternion.Inverse(f.rotation) * (w - f.position);
            Vector3 Tip(int i) => tips[i].leaf.TransformPoint(tips[i].ep);
            Vector3[] Avatar() => tips.Select((t, i) => In(rt, Tip(i))).ToArray();
            Vector3[] InFrame() => tips.Select((t, i) => In(t.frame, Tip(i))).ToArray();
            (Vector3, Quaternion)[] Frames() => chains.Select(c => (In(rt, c.frame.position), Quaternion.Inverse(rt.rotation) * c.frame.rotation)).ToArray();

            string rootHandle = h.Object.GetInstanceID().ToString(CultureInfo.InvariantCulture), rootPath = FullPath(rt);
            var log = new StringBuilder(); var tipMoved = new bool[tips.Count]; var frameMoved = new bool[chains.Count]; var jitMid = new List<float>(); var jitEnd = new List<float>();
            Vector3[] restAv = null, restIn = null, prevAv = null, prevIn = null; (Vector3, Quaternion)[] frame0 = null;
            bool frozen = false, animEnable = true; int row = 0, lastFrame = -1, stable = 0, lastAnim = int.MinValue, shotFails = 0; string firstShotFail = null;
            float t0 = 0; double lastTick = EditorApplication.timeSinceStartup, armedAt = lastTick;
            // A ramped pose eases from the pose the bones held to the new one; the hold is timed from arrival.
            Vector3[] fromP = null, toP = null; Quaternion[] fromR = null, toR = null; float rampT0 = 0, rampSec = 0; int rampFrames = 0; string approach = "-";
            string started = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), label = "drive-physbones_" + RunLogFormat.Sanitize(rt.name) + "_" + stage, logPath = null;
            int session = PlaySession.Current;
            void Finish(string verdict, string detail)
            {
                EditorApplication.update -= _pump; _pump = null;
                if (frozen) { try { Put(restP, restR); var a = rt.GetComponent<Animator>(); if (a) a.enabled = animEnable; } catch (Exception) { } } // gone after a play exit
                SessionState.EraseString(RecordKey);
                string summary = Tag + " Drive " + rootPath + " => " + verdict + " | stage=" + stage + (detail.Length > 0 ? " | " + detail : ""), line;
                if (logPath == null) line = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, label, summary, log.ToString(), ".md");
                else try { File.WriteAllText(logPath, log.ToString()); RunLogFormat.PublishArtifact(RunLogFormat.RunLogDir, logPath); line = summary + " | log=" + logPath; }
                    catch (Exception e) { line = summary + " | the log named at PENDING was not rewritten: " + e.Message; }
                SessionState.SetString(Key, line + "\n" + log); Debug.Log(line);
            }
            _pump = () =>
            {
                try
                {
                    if (!EditorApplication.isPlaying) { Finish("FAIL", "aborted: play exited"); return; }
                    if (Time.frameCount == lastFrame)
                    {
                        if (EditorApplication.timeSinceStartup - lastTick > 20) Finish("FAIL", "aborted: the player loop stalled for 20 s (a modal, a paused editor, or runInBackground off)");
                        return;
                    }
                    lastFrame = Time.frameCount; lastTick = EditorApplication.timeSinceStartup;
                    if (rt == null) { Finish("FAIL", "aborted: the avatar '" + rootPath + "' was destroyed after Run resolved it; call Run again"); return; }
                    var anim = rt.GetComponent<Animator>();
                    if (!frozen)
                    {
                        // An emulator destroys and re-adds the Animator at start and on reset; rest is whatever it last wrote, so wait it out.
                        int id = anim ? anim.GetInstanceID() : 0;
                        stable = id == lastAnim ? stable + 1 : 0; lastAnim = id;
                        if (stable < StableFrames) { if (lastTick - armedAt > 30) Finish("FAIL", "aborted: the Animator was rebuilt continuously for 30 s, so no rest pose could be frozen"); return; }
                        // A Run issued before the play build or the emulator finished resolved objects they may since have replaced.
                        int live = under == null ? -1 : rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Count(p => p.isActiveAndEnabled && WriteDynamics.EffRoot(p).IsChildOf(under));
                        if (pbs.Any(p => p == null || !p.isActiveAndEnabled) || moved.Any(b => b == null) || tips.Any(t => t.leaf == null) || live != pbs.Length)
                        { Finish("FAIL", "aborted: the avatar was rebuilt after Run resolved it (" + pbs.Length + " chains at the call, " + live + " live now); nothing was posed — call Run again"); return; }
                        frozen = true; animEnable = anim == null || anim.enabled;
                        for (int i = 0; i < moved.Length; i++) { restP[i] = poseP[i] = moved[i].localPosition; restR[i] = poseR[i] = moved[i].localRotation; }
                        SessionState.SetString(RecordKey, FormatRecord(rootPath, animEnable, moved.Select((b, i) => (AnimationUtility.CalculateTransformPath(b, rt), restP[i], restR[i])).ToList()));
                        frame0 = Frames(); t0 = Time.time;
                        SessionState.SetString(Key, Tag + " running '" + stage + "' 0/" + pl.poses.Length + " (rest frozen at frame " + Time.frameCount + ") | log=" + logPath + "\n");
                        log.Append("root=" + rootPath + " stage=" + stage + " started=" + started + " playSession=" + session + " chains=" + chains.Count + " tips=" + tips.Count + " hold=" + N(hold, "0.##") + "s freezeFrame=" + Time.frameCount
                            + " animator=" + id + (anim && !anim.enabled ? "(found disabled)" : "") + " body=" + (body ? body.name : "-") + " frames=" + (views.Length > 0 ? outDir : "-")
                            + "\napproach: snap (one frame) or ramp <s>/<frames> (smoothstep from the previous row's pose; the hold starts on arrival). jitter: mean per-frame max tip step over the last "
                            + N(Mathf.Min(0.5f, hold / 2), "0.##") + " s of each half of the hold, mid/end\n"
                            + "stage | pose | approach | tipTravelCm root max/mean | tipTravelCm inFrame max/mean | jitterMmPerFrame mid/end | vertsBehindBody/signed edgeNearest | maxDepthCm\n");
                    }
                    if (anim) anim.enabled = false;   // re-asserted every frame: an emulator re-enables it
                    if (fromP != null)
                    {
                        rampFrames++; float f = Ramp01(Time.time - rampT0, rampSec);
                        for (int i = 0; i < moved.Length; i++) { poseP[i] = Vector3.Lerp(fromP[i], toP[i], f); poseR[i] = Quaternion.Slerp(fromR[i], toR[i], f); }
                        if (f >= 1f) { fromP = null; t0 = Time.time; approach = "ramp " + N(rampSec, "0.##") + "s/" + rampFrames + "f"; }
                    }
                    Put(poseP, poseR);                // and whatever wrote a posed bone is overwritten before sampling
                    var av = Avatar(); var inF = InFrame();
                    if (row > 0)                      // the control and tip motion count pose rows only: rest is the settling baseline
                    {
                        var fr = Frames();
                        for (int c = 0; c < chains.Count; c++) frameMoved[c] |= (fr[c].Item1 - frame0[c].Item1).magnitude > 1e-4f || Quaternion.Angle(fr[c].Item2, frame0[c].Item2) > 0.05f;
                        if (prevIn != null) for (int i = 0; i < inF.Length; i++) tipMoved[i] |= (inF[i] - prevIn[i]).magnitude > Still;
                    }
                    if (fromP != null) { prevAv = av; prevIn = inF; return; }   // still ramping: the hold has not begun
                    int win = prevAv == null ? 0 : JitterWindow(Time.time - t0, hold);
                    if (win > 0) (win == 1 ? jitMid : jitEnd).Add(av.Select((c, i) => (c - prevAv[i]).magnitude).Max());
                    prevAv = av; prevIn = inF;
                    if (Time.time - t0 < hold) return;
                    string name = row == 0 ? "rest" : pl.poses[row - 1].name;
                    if (row == 0) { restAv = av; restIn = inF; }
                    var mv = av.Select((c, i) => (c - restAv[i]).magnitude * 100).ToArray(); var mf = inF.Select((c, i) => (c - restIn[i]).magnitude * 100).ToArray();
                    string Mm(List<float> j) => j.Count > 0 ? N(j.Average() * 1000, "F2") : "-";   // an empty window is unmeasured, never still
                    log.Append(stage + " | " + name + " | " + approach + " | " + N(mv.Max()) + "/" + N(mv.Average()) + " | " + N(mf.Max()) + "/" + N(mf.Average()) + " | " + Mm(jitMid) + "/" + Mm(jitEnd) + " | ");
                    if (body) { var r = ReportPenetration.Measure(body, gar); log.Append(r.behind + "/" + r.signed + " " + r.edgeNearest + " | " + N(r.maxDepthCm) + "\n"); } else log.Append("- | -\n");
                    if (views.Length > 0)
                    {
                        var shot = RenderAvatar.Run(rootHandle, views, null, 0.15f, false, 512, Path.Combine(outDir, stage + "_" + RunLogFormat.Sanitize(name)));
                        if (!shot.Contains("=> OK")) { shotFails++; firstShotFail = firstShotFail ?? shot; }
                    }
                    jitMid.Clear(); jitEnd.Clear(); prevAv = null; prevIn = null; row++;
                    if (row > pl.poses.Length)
                    {
                        bool NoTip(int c) => chains[c].tips.All(i => !tipMoved[i]);
                        var dead = Enumerable.Range(0, chains.Count).Where(c => frameMoved[c] && NoTip(c)).Select(c => chains[c].path).ToArray();
                        var still = Enumerable.Range(0, chains.Count).Where(c => !frameMoved[c] && NoTip(c)).Select(c => chains[c].path).ToArray();
                        string detail = (dead.Length > 0 ? "no solve: frame moved, no tip moved in it: " + string.Join(", ", dead) + " | " : "")
                            + "stillChains=" + still.Length + "/" + chains.Count + (still.Length > 0 ? " (" + string.Join(", ", still) + ")" : "")
                            + (shotFails > 0 ? " | frames: " + shotFails + " grab(s) failed, first: " + firstShotFail : views.Length > 0 ? " | frames=" + outDir : "");
                        Finish(dead.Length > 0 ? "FAIL" : "OK", detail);
                        return;
                    }
                    var held = (P: (Vector3[])poseP.Clone(), R: (Quaternion[])poseR.Clone());
                    Put(restP, restR);
                    foreach (var o in ops[row - 1]) Apply(rt, o.bone, o.op);
                    for (int i = 0; i < moved.Length; i++) { poseP[i] = moved[i].localPosition; poseR[i] = moved[i].localRotation; }
                    t0 = Time.time; approach = "snap";
                    if (pl.poses[row - 1].ramp > 0)
                    {
                        fromP = held.P; fromR = held.R; toP = (Vector3[])poseP.Clone(); toR = (Quaternion[])poseR.Clone();
                        held.P.CopyTo(poseP, 0); held.R.CopyTo(poseR, 0); Put(poseP, poseR);
                        rampT0 = Time.time; rampSec = pl.poses[row - 1].ramp; rampFrames = 0;
                    }
                    SessionState.SetString(Key, Tag + " running '" + stage + "' " + row + "/" + pl.poses.Length + " | log=" + logPath + "\n" + log);
                }
                catch (Exception e) { Finish("FAIL", "error " + e.GetType().Name + ": " + e.Message); }
            };
            string pending = Tag + " Drive " + rootPath + " => PENDING | stage=" + stage + " started " + started + " in play session " + session + " (entered " + PlaySession.EnteredAt + ") | "
                + (pl.poses.Length + 1) + " rows (rest first), " + chains.Count + " chains, " + tips.Count + " tips, hold " + N(hold, "0.##") + "s; poll Ryan6Vrc.AgentTools.Editor.DrivePhysBones.Status()";
            var armed = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, label, pending, pending + "\n(running: this file is rewritten with the rows when the drive finishes)\n", ".md");
            int at = armed.LastIndexOf(" | log=", StringComparison.Ordinal); if (at >= 0) logPath = armed.Substring(at + 7);
            SessionState.SetInt(SessionKey, session); SessionState.SetString(LogKey, logPath ?? "");
            SessionState.SetString(Key, Tag + " running '" + stage + "': waiting for a stable Animator | log=" + logPath + "\n"); EditorApplication.update += _pump;
            return logPath != null ? armed : pending;
        }

        /// <summary>World turns about the avatar's axes: +pitch swings what hangs below forward, +yaw swings what points
        /// forward to the right, +roll swings what hangs below to the right; <c>move</c> is avatar-space metres.</summary>
        static void Apply(Transform rt, Transform bone, BoneOp op)
        {
            bone.rotation = Quaternion.AngleAxis(op.roll, rt.forward) * Quaternion.AngleAxis(op.yaw, rt.up) * Quaternion.AngleAxis(-op.pitch, rt.right) * bone.rotation;
            var m = string.IsNullOrEmpty(op.move) ? null : WriteDynamics.ParseVector(op.move);
            if (m != null) bone.position += rt.rotation * m.Value;
        }

        static IEnumerable<Transform> Leaves(Transform t, HashSet<Transform> ignore)
        {
            var kids = t.Cast<Transform>().Where(c => !ignore.Contains(c)).ToList();
            return kids.Count == 0 ? new[] { t } : kids.SelectMany(c => Leaves(c, ignore));
        }

        /// <summary>The eased fraction of a ramp <paramref name="elapsed"/> seconds in: smoothstep, 0 at the start, exactly 1
        /// from <paramref name="seconds"/> on; a ramp of 0 or less has already arrived. Pure.</summary>
        internal static float Ramp01(float elapsed, float seconds)
        {
            if (!(seconds > 0)) return 1f;
            float x = Mathf.Clamp01(elapsed / seconds); return x * x * (3 - 2 * x);
        }

        /// <summary>Which jitter window a frame <paramref name="elapsed"/> seconds into a hold falls in: 1 for the last
        /// min(0.5 s, hold/2) of the first half, 2 for the same span ending the hold, else 0. Pure.</summary>
        internal static int JitterWindow(float elapsed, float hold)
        {
            float half = hold / 2, w = Mathf.Min(0.5f, half);
            return elapsed > half - w && elapsed <= half ? 1 : elapsed > hold - w ? 2 : 0;
        }

        static string N(float f, string fmt = "F1") => f.ToString(fmt, CultureInfo.InvariantCulture);

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
            var line = Tag + " Recover => FAIL | a domain reload tore down a drive mid-flight: " + what + "; its rows are void";
            var logPath = SessionState.GetString(LogKey, "");
            if (logPath.Length > 0 && File.Exists(logPath)) try { File.AppendAllText(logPath, "\n" + line + "\n"); line += " | log=" + logPath; } catch (Exception) { }
            SessionState.SetString(Key, line + "\n" + prior);
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
            var files = new Dictionary<string, string> { { "rest", "rest" } };   // names become frame file names: collide as the file system would
            foreach (var p in pl.poses)
            {
                if (string.IsNullOrEmpty(p.name)) return "every pose needs a name";
                var key = RunLogFormat.Sanitize(p.name).ToLowerInvariant();
                if (files.TryGetValue(key, out var other)) return "poses '" + p.name + "' and '" + other + "' become the same frame file name; rename one ('rest' is the baseline row's)";
                files[key] = p.name;
                if (p.ops.Any(o => string.IsNullOrEmpty(o.bone))) return "poses '" + p.name + "': every op needs a bone path";
                if (p.ops.Any(o => !string.IsNullOrEmpty(o.move) && WriteDynamics.ParseVector(o.move) == null)) return "poses '" + p.name + "': move is x,y,z metres";
                if (!(p.ramp >= 0)) return "poses '" + p.name + "': ramp is seconds, 0 (snap) or more";
            }
            return null;
        }

        static string Fail(string m) { var s = Tag + " FAIL: " + m; Debug.LogWarning(s); return s; }
    }

    /// <summary>Which play session a status record belongs to: a counter bumped on every play entry and kept in
    /// SessionState, so it survives the play-entry domain reload. The async doors stamp their record with it, and their
    /// <c>Status()</c> names an earlier session's record as history rather than returning it as current.</summary>
    internal static class PlaySession
    {
        const string Counter = "Ryan6Vrc.PlaySession.counter", Entered = "Ryan6Vrc.PlaySession.entered";
        internal static int Current => SessionState.GetInt(Counter, 0);
        internal static string EnteredAt => SessionState.GetString(Entered, "before this editor session's first play entry");
        internal static bool IsCurrent(int stamp) => stamp == Current;

        /// <summary>An earlier session's record, named as history with its verdict token masked, so no reader can take it
        /// for this session's result. Pure but for the play flag.</summary>
        internal static string Stale(string tag, string what, string firstLine) =>
            tag + " idle this play session (" + (EditorApplication.isPlaying ? "entered " : "the last one entered ") + EnteredAt + "): no " + what
            + " has started in it. The last record is from an earlier play session, not this one's result: " + firstLine.Replace("=> ", "was ");

        [InitializeOnLoadMethod]
        static void Hook() { EditorApplication.playModeStateChanged -= OnPlayMode; EditorApplication.playModeStateChanged += OnPlayMode; }

        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            SessionState.SetInt(Counter, Current + 1); SessionState.SetString(Entered, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }
    }
}
