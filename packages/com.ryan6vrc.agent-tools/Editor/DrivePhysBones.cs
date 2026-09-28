using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.Dynamics.ManagedTypes;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Play-mode drive: pose, hold, sample the physbone tips under a root. Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class DrivePhysBones
    {
        [Serializable] public class PoseList { public Pose[] poses = new Pose[0]; }
        /// <summary><c>ramp</c> is seconds: 0 snaps to the pose in one frame; above 0 the bones ease (smoothstep) from the
        /// previous row's pose to this one over that long, and the hold is timed from arrival. <c>hold</c> is seconds,
        /// below 0 taking the drive's. <c>sample</c> is when the clipping measure runs: <c>end</c> of the hold,
        /// <c>every</c> frame of the ramp and hold, or <c>none</c>.</summary>
        [Serializable] public class Pose { public string name; public BoneOp[] ops = new BoneOp[0]; public float ramp; public float hold = -1; public string sample = "end"; }
        [Serializable] public class BoneOp { public string bone, move = ""; public float pitch, yaw, roll; }

        const string Tag = "[DrivePhysBones]", Key = "Ryan6Vrc.DrivePhysBones.status", RecordKey = "Ryan6Vrc.DrivePhysBones.restore";
        const string SessionKey = "Ryan6Vrc.DrivePhysBones.playSession", LogKey = "Ryan6Vrc.DrivePhysBones.log";
        const string ClockKey = "Ryan6Vrc.DrivePhysBones.clock", ControlKey = "Ryan6Vrc.DrivePhysBones.control", RestKey = "Ryan6Vrc.DrivePhysBones.rest.";
        internal const float Step = 1f / 60f;   // the solver's virtual step, pinned as every frame's delta for the drive's length
        const int StableFrames = 60;        // the Animator instance must survive this many evaluated frames before rest is frozen
        const float Still = 1e-5f;          // world metres: a per-frame tip displacement below this is no motion
        // A followed target this far off its rest place in its source's frame is not being driven. A running constraint holds it
        // exactly, even behind a physbone source mid-swing; an idle one in a strand shows up as a few degrees of one joint's bend.
        const float DriftCm = 0.5f, DriftDeg = 2f;
        static readonly string[] Samples = { "end", "every", "none" };
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

        public static string Run(string avatarRoot, string poses, string[] chains = null, string stage = "drive", string[] bodies = null, string[] garments = null, string[] groups = null,
            float hold = 2.5f, string[] views = null, string outDir = null, bool control = false, string baseline = null)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return Fail("play entry is still in progress (the build runs first); call again once EditorApplication.isPlaying reads true — a drive armed now would be dropped by the play-entry domain reload");
            if (!EditorApplication.isPlaying) return Fail("play mode only: enter play (isCompiling and isUpdating both false), then call again");
            if (_pump != null) return Fail("a drive is already running: poll DrivePhysBones.Status()");
            if (SessionState.GetString(RecordKey, "").Length > 0) return Fail("a torn-down drive's restore record is unresolved; it resolves on the next editor tick — poll DrivePhysBones.Status(), then call again");
            if (WriteDynamics.Pending) return Fail("a WriteDynamics field set is still cycling its physbone hosts inactive; poll WriteDynamics.Status(), then call again");
            if (EditorApplication.isPaused) return Fail("the editor is paused (a held GrabPhysBone freezes it): GrabPhysBone.Release(resume: true) first");
            if (!(hold >= 0)) return Fail("hold is seconds, 0 or more");
            var err = ParsePoses(poses, out var pl); if (err != null) return Fail(err);
            var h = SceneHandle.Resolve(avatarRoot); if (!h.Ok) return Fail(h.Refusal);
            var rt = h.Object.transform;
            if (!h.Object.activeInHierarchy) return Fail("'" + avatarRoot + "' is inactive: an inactive avatar is never built or simulated, so every number would read zero; activate it in edit mode and re-enter play");
            if ((err = ReportClipping.ResolveTransforms(rt, chains, "chains", out var scope)) != null) return Fail(err);
            var pbs = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.isActiveAndEnabled && InScope(p, scope)).ToArray();
            if (pbs.Length == 0) return Fail("no active, enabled physbone is rooted at or under " + (scope == null ? "'" + rt.name + "'" : string.Join(", ", chains)));
            // A tip is the far end of its leaf's own segment, so an endpoint-only chain, whose one transform never translates, still
            // reads; a leaf two overlapping chains reach is one tip, measured in the first chain's frame.
            var tips = new List<(Transform leaf, Vector3 ep, Transform frame)>(); var rigs = new List<(string path, string host, Transform frame, int[] tips)>();
            foreach (var p in pbs)
            {
                var root = WriteDynamics.EffRoot(p); var frame = root.parent ? root.parent : rt;
                var idx = Leaves(root, new HashSet<Transform>(p.ignoreTransforms.Where(x => x != null))).Select(l =>
                { int i = tips.FindIndex(t => t.leaf == l); if (i < 0) { tips.Add((l, p.endpointPosition, frame)); i = tips.Count - 1; } return i; }).ToArray();
                rigs.Add((ReportClearance.RelPath(rt, root), FullPath(p.transform), frame, idx));   // bare names, as `chains` takes them
            }
            ReportClipping.Surface[] body = null, gar = null;
            if (bodies != null && bodies.Length > 0) { if ((err = ReportClipping.Resolve(rt, bodies, garments, out body, out gar)) != null) return Fail(err); }
            else if (garments != null && garments.Length > 0) return Fail("garments are measured against bodies: pass bodies too");
            if (body == null && groups != null && groups.Length > 0) return Fail("groups splits the clipping measure, so it needs bodies and garments");
            if (body == null && pl.poses.FirstOrDefault(p => p.sample == "every") is Pose ev) return Fail("poses '" + ev.name + "': sample 'every' times the clipping measure, which needs bodies and garments");
            if ((err = ReportClipping.ResolveGroups(rt, groups, out var groupT)) != null) return Fail(err);
            string baseRaw = null;
            if (!string.IsNullOrEmpty(baseline) && (baseRaw = SessionState.GetString(RestKey + RunLogFormat.Sanitize(baseline), "")).Length == 0)
                return Fail("baseline: no drive of stage '" + baseline + "' has recorded a rest row in this editor session; drive that stage first");
            var ops = new List<(Transform bone, BoneOp op)>[pl.poses.Length];
            for (int i = 0; i < ops.Length; i++)
            {
                ops[i] = new List<(Transform, BoneOp)>();
                foreach (var op in pl.poses[i].ops) { var b = Bone(rt, op.bone, out err); if (b == null) return Fail("poses '" + pl.poses[i].name + "': " + err); ops[i].Add((b, op)); }
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
            (Vector3, Quaternion)[] Frames() => rigs.Select(c => (In(rt, c.frame.position), Quaternion.Inverse(rt.rotation) * c.frame.rotation)).ToArray();
            string Mode(int r) => r == 0 || body == null ? "end" : pl.poses[r - 1].sample;
            float HoldOf(int r) => r > 0 && pl.poses[r - 1].hold >= 0 ? pl.poses[r - 1].hold : hold;

            string rootHandle = h.Object.GetInstanceID().ToString(CultureInfo.InvariantCulture), rootPath = FullPath(rt);
            var pen = new List<(string row, Dictionary<(string body, string region, string group), ReportClipping.Bucket> buckets)>(); string penMaps = null;
            ReportClipping.Result acc = default; int accFrames = 0; var perFrame = new StringBuilder();
            string shift = null, shiftToken = null;
            // A single-source parent constraint holds its target fixed in its source's frame, world or local as it solves, while it executes.
            var follow = new List<(Transform tgt, Transform src, bool local, Vector3 p, Quaternion q, float cm, float deg)>();
            var log = new StringBuilder(); var tipMoved = new bool[tips.Count]; var frameMoved = new bool[rigs.Count]; var jitMid = new List<float>(); var jitEnd = new List<float>();
            Vector3[] restAv = null, restIn = null, prevAv = null, prevIn = null; (Vector3, Quaternion)[] frame0 = null;
            bool frozen = false, held = false, animEnable = true; int row = 0, lastFrame = -1, stable = 0, lastAnim = int.MinValue, shotFails = 0; string firstShotFail = null;
            float t0 = 0, rowT0 = 0; double lastTick = EditorApplication.timeSinceStartup, armedAt = lastTick;
            // A ramped pose eases from the pose the bones held to the new one; the hold is timed from arrival.
            Vector3[] fromP = null, toP = null; Quaternion[] fromR = null, toR = null; float rampT0 = 0, rampSec = 0; int rampFrames = 0; string approach = "-";
            string started = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), label = "drive-physbones_" + RunLogFormat.Sanitize(rt.name) + "_" + stage, logPath = null;
            int session = PlaySession.Current;
            void Finish(string verdict, string detail)
            {
                EditorApplication.update -= _pump; _pump = null;
                RestoreClock();
                if (held) { foreach (var p in pbs) if (p != null) p.enabled = true; }
                SessionState.EraseString(ControlKey);
                if (shift != null) log.Append("\n" + shift + "\n");
                if (pen.Count > 0) log.Append("\n" + penMaps + "\n" + ReportClipping.Pivot(pen) + "\n");
                if (frozen) { try { Put(restP, restR); var a = rt.GetComponent<Animator>(); if (a) a.enabled = animEnable; } catch (Exception) { } } // gone after a play exit
                SessionState.EraseString(RecordKey);
                if (perFrame.Length > 0 && logPath != null)
                    try { var tsv = Path.ChangeExtension(logPath, null) + "_frames.tsv"; File.WriteAllText(tsv, "row\tt\tcrossingCm\tbehind\tmaxDepthCm\tthroughCm\n" + perFrame); RunLogFormat.PublishArtifact(RunLogFormat.RunLogDir, tsv); detail += " | perFrame=" + tsv; }
                    catch (Exception e) { detail += " | the per-frame table was not written: " + e.Message; }
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
                        int live = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Count(p => p.isActiveAndEnabled && InScope(p, scope));
                        if (pbs.Any(p => p == null || !p.isActiveAndEnabled) || moved.Any(b => b == null) || tips.Any(t => t.leaf == null) || live != pbs.Length)
                        { Finish("FAIL", "aborted: the avatar was rebuilt after Run resolved it (" + pbs.Length + " chains at the call, " + live + " live now), as it can be in the first seconds of play; nothing was posed — call Run again"); return; }
                        frozen = true; animEnable = anim == null || anim.enabled;
                        for (int i = 0; i < moved.Length; i++) { restP[i] = poseP[i] = moved[i].localPosition; restR[i] = poseR[i] = moved[i].localRotation; }
                        SessionState.SetString(RecordKey, FormatRecord(rootPath, animEnable, moved.Select((b, i) => (AnimationUtility.CalculateTransformPath(b, rt), restP[i], restR[i])).ToList()));
                        frame0 = Frames(); t0 = rowT0 = Time.time;
                        // A target a pose op writes, or one a chain anywhere on the avatar simulates, moves off its source legitimately.
                        var simRoots = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.isActiveAndEnabled).Select(WriteDynamics.EffRoot).ToArray();
                        foreach (var c in rt.GetComponentsInChildren<VRCConstraintBase>(true))
                        {
                            var src = Followed(c); var tgt = c.GetEffectiveTargetTransform();
                            if (src == null || tgt == null || moved.Contains(tgt) || simRoots.Any(r => tgt != r && tgt.IsChildOf(r))) continue;
                            var (fp, fq) = InSource(tgt, src, c.SolveInLocalSpace); follow.Add((tgt, src, c.SolveInLocalSpace, fp, fq, 0f, 0f));
                        }
                        if (control)   // after the scan, so a held chain's targets stay out of the followed set as a simulated chain's are
                        {
                            SessionState.SetString(ControlKey, string.Join("\n", pbs.Select(p => FullPath(p.transform) + "\t" + Array.IndexOf(p.GetComponents<VRCPhysBoneBase>(), p))));
                            foreach (var p in pbs) p.enabled = false;
                            held = true;
                        }
                        SessionState.SetString(Key, Tag + " running '" + stage + "' 0/" + pl.poses.Length + " (rest frozen at frame " + Time.frameCount + ") | log=" + logPath + "\n");
                        log.Append("root=" + rootPath + " stage=" + stage + " started=" + started + " playSession=" + session + " chains=" + rigs.Count + " tips=" + tips.Count + " hold=" + N(hold, "0.##") + "s dt=" + N(Step, "0.#####")
                            + "s freezeFrame=" + Time.frameCount + " animator=" + id + (anim && !anim.enabled ? "(found disabled)" : "") + " bodies=" + (body != null ? string.Join(",", body.Select(b => b.label)) : "-")
                            + " garments=" + (gar != null ? string.Join(",", gar.Select(g => g.label)) : "-") + " control=" + (control ? pbs.Length + " chains held disabled" : "no") + " baseline=" + (baseline ?? "-")
                            + " followedConstraints=" + follow.Count + " frames=" + (views.Length > 0 ? outDir : "-")
                            + "\nclock: every frame advances dt, so ramp and hold are simulated seconds. approach: snap (one frame) or ramp <s>/<frames> (smoothstep from the previous row's pose; the hold starts on arrival)."
                            + " jitter: mean per-frame max tip step over the last min(0.5 s, hold/2) of each half of the hold, mid/end. sample: when the clipping measure ran; on 'every <n>f' each clipping column is its peak over the row's frames\n"
                            + "stage | pose | approach | tipTravelCm root max/mean | tipTravelCm inFrame max/mean | jitterMmPerFrame mid/end | sample | crossingCm | vertsBehindBody/signed edgeNearest | maxDepthCm | throughCm\n");
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
                        for (int c = 0; c < rigs.Count; c++) frameMoved[c] |= (fr[c].Item1 - frame0[c].Item1).magnitude > 1e-4f || Quaternion.Angle(fr[c].Item2, frame0[c].Item2) > 0.05f;
                        if (prevIn != null) for (int i = 0; i < inF.Length; i++) tipMoved[i] |= (inF[i] - prevIn[i]).magnitude > Still;
                    }
                    if (Mode(row) == "every")
                    {
                        var m = ReportClipping.Measure(rt, body, gar, groupT); acc = ReportClipping.Peak(acc, m); accFrames++;
                        perFrame.Append(pl.poses[row - 1].name + "\t" + N(Time.time - rowT0, "0.000") + "\t" + N(m.crossCm) + "\t" + m.behind + "\t" + N(m.maxDepthCm) + "\t" + N(m.throughCm) + "\n");
                    }
                    if (fromP != null) { prevAv = av; prevIn = inF; return; }   // still ramping: the hold has not begun
                    float hd = HoldOf(row);
                    int win = prevAv == null ? 0 : JitterWindow(Time.time - t0, hd);
                    if (win > 0) (win == 1 ? jitMid : jitEnd).Add(av.Select((c, i) => (c - prevAv[i]).magnitude).Max());
                    prevAv = av; prevIn = inF;
                    if (Time.time - t0 < hd) return;
                    string name = row == 0 ? "rest" : pl.poses[row - 1].name;
                    if (row == 0)
                    {
                        restAv = av; restIn = inF;
                        var now = tips.Select((t, i) => (ReportClearance.RelPath(rt, t.leaf), av[i])).ToList();
                        SessionState.SetString(RestKey + stage, FormatRest(session, started, now));
                        if (baseRaw != null && TryParseRest(baseRaw, out int bs, out string bt, out var basePos))
                        {
                            var byChain = rigs.Select(c => (c.path, (IList<(string, Vector3)>)c.tips.Select(i => now[i]).ToList())).ToList();
                            shift = "rest shift against baseline '" + baseline + "' (its rest row at " + bt + ", play session " + bs + "): each chain's tips, avatar space\n" + RestShift(basePos, byChain, out float mx, out float mean);
                            shiftToken = "restShiftCm=" + N(mx) + "/" + N(mean) + " vs " + baseline;
                        }
                    }
                    var mv = av.Select((c, i) => (c - restAv[i]).magnitude * 100).ToArray(); var mf = inF.Select((c, i) => (c - restIn[i]).magnitude * 100).ToArray();
                    string Mm(List<float> j) => j.Count > 0 ? N(j.Average() * 1000, "F2") : "-";   // an empty window is unmeasured, never still
                    log.Append(stage + " | " + name + " | " + approach + " | " + N(mv.Max()) + "/" + N(mv.Average()) + " | " + N(mf.Max()) + "/" + N(mf.Average()) + " | " + Mm(jitMid) + "/" + Mm(jitEnd) + " | ");
                    string mode = Mode(row);
                    if (body != null && mode != "none")
                    {
                        var r = mode == "every" ? acc : ReportClipping.Measure(rt, body, gar, groupT); pen.Add((name, r.buckets)); penMaps = penMaps ?? ReportClipping.Maps(r, rt);
                        log.Append((mode == "every" ? "every " + accFrames + "f" : "end") + " | " + N(r.crossCm) + " | " + r.behind + "/" + r.signed + " " + r.edgeNearest + " | " + N(r.maxDepthCm) + " | " + N(r.throughCm) + "\n");
                    }
                    else log.Append((body == null ? "-" : "none") + " | - | - | - | -\n");
                    acc = default; accFrames = 0;
                    for (int i = 0; i < follow.Count; i++)
                    {
                        var f = follow[i]; if (f.tgt == null || f.src == null) continue;
                        var (fp, fq) = InSource(f.tgt, f.src, f.local);
                        follow[i] = (f.tgt, f.src, f.local, f.p, f.q, Mathf.Max(f.cm, (fp - f.p).magnitude * 100), Mathf.Max(f.deg, Quaternion.Angle(fq, f.q)));
                    }
                    if (views.Length > 0)
                    {
                        var shot = RenderAvatar.Run(rootHandle, views, null, 0.15f, false, 512, Path.Combine(outDir, stage + "_" + RunLogFormat.Sanitize(name)));
                        if (!shot.Contains("=> OK")) { shotFails++; firstShotFail = firstShotFail ?? shot; }
                    }
                    jitMid.Clear(); jitEnd.Clear(); prevAv = null; prevIn = null; row++;
                    if (row > pl.poses.Length)
                    {
                        var idle = follow.Where(f => f.cm > DriftCm || f.deg > DriftDeg).ToArray();
                        bool NoTip(int c) => rigs[c].tips.All(i => !tipMoved[i]);
                        var dead = control ? new string[0] : Enumerable.Range(0, rigs.Count).Where(c => frameMoved[c] && NoTip(c)).Select(c => rigs[c].host).ToArray();
                        var still = Enumerable.Range(0, rigs.Count).Where(c => !frameMoved[c] && NoTip(c)).Select(c => rigs[c].host).ToArray();
                        string detail = (idle.Length > 0 ? "idle constraints: " + idle.Length + " of " + follow.Count + " single-source parent constraints drifted in their source's frame ("
                                + string.Join(", ", idle.Take(5).Select(f => AnimationUtility.CalculateTransformPath(f.tgt, rt) + " " + N(f.cm) + "cm/" + N(f.deg, "0") + "deg")) + (idle.Length > 5 ? ", …" : "")
                                + "), so they are not executing and every row read off their targets is void. A play entry that registers constraints without running them does this;"
                                + " stop play, refresh with a script reload, wait for the editor to be ready, re-enter play and drive again | " : "")
                            + (control ? "control: " + pbs.Length + " chains held disabled, re-enabled at the end | " : "")
                            + (dead.Length > 0 ? "no solve: frame moved, no tip moved in it: " + string.Join(", ", dead) + " | " : "")
                            + (control ? "" : "stillChains=" + still.Length + "/" + rigs.Count + (still.Length > 0 ? " (" + string.Join(", ", still) + ")" : "") + " | ")
                            + (shiftToken != null ? shiftToken + " | " : "")
                            + "followedConstraints=" + follow.Count + (follow.Count > 0 ? " maxDrift=" + N(follow.Max(f => f.cm), "F2") + "cm/" + N(follow.Max(f => f.deg), "F2") + "deg" : "")
                            + (shotFails > 0 ? " | frames: " + shotFails + " grab(s) failed, first: " + firstShotFail : views.Length > 0 ? " | frames=" + outDir : "");
                        Finish(dead.Length > 0 || idle.Length > 0 ? "FAIL" : "OK", detail);
                        return;
                    }
                    var heldPose = (P: (Vector3[])poseP.Clone(), R: (Quaternion[])poseR.Clone());
                    Put(restP, restR);
                    foreach (var o in ops[row - 1]) Apply(rt, o.bone, o.op);
                    for (int i = 0; i < moved.Length; i++) { poseP[i] = moved[i].localPosition; poseR[i] = moved[i].localRotation; }
                    t0 = rowT0 = Time.time; approach = "snap";
                    if (pl.poses[row - 1].ramp > 0)
                    {
                        fromP = heldPose.P; fromR = heldPose.R; toP = (Vector3[])poseP.Clone(); toR = (Quaternion[])poseR.Clone();
                        heldPose.P.CopyTo(poseP, 0); heldPose.R.CopyTo(poseR, 0); Put(poseP, poseR);
                        rampT0 = Time.time; rampSec = pl.poses[row - 1].ramp; rampFrames = 0;
                    }
                    SessionState.SetString(Key, Tag + " running '" + stage + "' " + row + "/" + pl.poses.Length + " | log=" + logPath + "\n" + log);
                }
                catch (Exception e) { Finish("FAIL", "error " + e.GetType().Name + ": " + e.Message); }
            };
            string pending = Tag + " Drive " + rootPath + " => PENDING | stage=" + stage + " started " + started + " in play session " + session + " (entered " + PlaySession.EnteredAt + ") | "
                + (pl.poses.Length + 1) + " rows (rest first), " + rigs.Count + " chains" + (control ? " held disabled" : "") + ", " + tips.Count + " tips, hold " + N(hold, "0.##") + "s, clock pinned to "
                + N(Step, "0.#####") + "s a frame; poll Ryan6Vrc.AgentTools.Editor.DrivePhysBones.Status()";
            var armed = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, label, pending, pending + "\n(running: this file is rewritten with the rows when the drive finishes)\n", ".md");
            int at = armed.LastIndexOf(" | log=", StringComparison.Ordinal); if (at >= 0) logPath = armed.Substring(at + 7);
            SessionState.SetInt(SessionKey, session); SessionState.SetString(LogKey, logPath ?? "");
            if (!SessionState.GetBool(ClockKey + ".set", false)) { SessionState.SetFloat(ClockKey, Time.captureDeltaTime); SessionState.SetBool(ClockKey + ".set", true); }
            Time.captureDeltaTime = Step;
            SessionState.SetString(Key, Tag + " running '" + stage + "': waiting for a stable Animator | log=" + logPath + "\n"); EditorApplication.update += _pump;
            return logPath != null ? armed : pending;
        }

        static bool InScope(VRCPhysBoneBase p, Transform[] scope) => scope == null || scope.Any(s => WriteDynamics.EffRoot(p).IsChildOf(s));

        /// <summary>A pose op's bone: a root-relative path, else a humanoid bone name the avatar maps, as the clipping
        /// report prints it (<c>LeftUpperLeg</c>).</summary>
        static Transform Bone(Transform rt, string name, out string err)
        {
            var b = WriteDynamics.Find(rt, name, out err); if (b != null) return b;
            if (name.Contains("/") || name.Length == 0 || char.IsDigit(name[0]) || !Enum.TryParse(name, false, out HumanBodyBones hb) || hb == HumanBodyBones.LastBone)
            { err += " (nor is it a humanoid bone name such as LeftUpperLeg)"; return null; }
            var a = rt.GetComponent<Animator>();
            var t = a != null && a.isHuman ? a.GetBoneTransform(hb) : null;
            err = t != null ? null : "'" + name + "' is a humanoid bone the Animator on '" + rt.name + "' does not map";
            return t;
        }

        static void RestoreClock()
        {
            if (!SessionState.GetBool(ClockKey + ".set", false)) return;
            Time.captureDeltaTime = SessionState.GetFloat(ClockKey, 0f); SessionState.EraseFloat(ClockKey); SessionState.EraseBool(ClockKey + ".set");
        }

        /// <summary>World turns about the avatar's axes: +pitch swings what hangs below forward, +yaw swings what points
        /// forward to the right, +roll swings what hangs below to the right; <c>move</c> is avatar-space metres.</summary>
        static void Apply(Transform rt, Transform bone, BoneOp op)
        {
            bone.rotation = Quaternion.AngleAxis(op.roll, rt.forward) * Quaternion.AngleAxis(op.yaw, rt.up) * Quaternion.AngleAxis(-op.pitch, rt.right) * bone.rotation;
            var m = string.IsNullOrEmpty(op.move) ? null : WriteDynamics.ParseVector(op.move);
            if (m != null) bone.position += rt.rotation * m.Value;
        }

        /// <summary>The one source a constraint follows rigidly, or null: an active, locked parent constraint on all six
        /// axes, not frozen to world, with exactly one weighted source, it and the constraint both at full weight.</summary>
        static Transform Followed(VRCConstraintBase c)
        {
            if (!(c is VRCParentConstraintBase p) || !c.isActiveAndEnabled || !c.IsActive || !c.Locked || c.GlobalWeight < 0.999f || c.FreezeToWorld) return null;
            if (!(p.AffectsPositionX && p.AffectsPositionY && p.AffectsPositionZ && p.AffectsRotationX && p.AffectsRotationY && p.AffectsRotationZ)) return null;
            var w = Enumerable.Range(0, c.Sources.Count).Select(i => c.Sources[i]).Where(s => s.Weight > 0).ToArray();
            return w.Length == 1 && w[0].Weight >= 0.999f ? w[0].SourceTransform : null;
        }

        /// <summary>A transform's place in another's unscaled frame: world poses, or for a constraint solving in local space
        /// the two local poses, which is the relation such a constraint holds fixed while it runs.</summary>
        static (Vector3, Quaternion) InSource(Transform t, Transform s, bool local) => local
            ? (Quaternion.Inverse(s.localRotation) * (t.localPosition - s.localPosition), Quaternion.Inverse(s.localRotation) * t.localRotation)
            : (Quaternion.Inverse(s.rotation) * (t.position - s.position), Quaternion.Inverse(s.rotation) * t.rotation);

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

        internal static string FullPath(Transform t) => t.parent == null ? t.name : FullPath(t.parent) + "/" + t.name;

        // ── Rest rows: a drive's rest tips, kept per stage for a later drive's baseline ─────────────────────

        static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        internal static string FormatRest(int session, string started, IList<(string tip, Vector3 p)> tips) =>
            "v1\t" + session.ToString(CultureInfo.InvariantCulture) + "\t" + started + string.Concat(tips.Select(t => "\n" + t.tip + "\t" + R(t.p.x) + "," + R(t.p.y) + "," + R(t.p.z)));

        internal static bool TryParseRest(string raw, out int session, out string started, out Dictionary<string, Vector3> tips)
        {
            session = 0; started = null; tips = new Dictionary<string, Vector3>();
            var lines = (raw ?? "").Split('\n'); var head = lines[0].Split('\t');
            if (head.Length != 3 || head[0] != "v1" || !int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out session)) return false;
            started = head[2];
            foreach (var l in lines.Skip(1))
            {
                var f = l.Split('\t'); if (f.Length != 2) return false;
                var c = f[1].Split(',').Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN).ToArray();
                if (c.Length != 3 || c.Any(float.IsNaN)) return false;
                tips[f[0]] = new Vector3(c[0], c[1], c[2]);
            }
            return true;
        }

        /// <summary>Each chain's tips against the baseline's by tip path: max and mean shift, worst chain first, and the
        /// tips the baseline never recorded. Pure.</summary>
        internal static string RestShift(Dictionary<string, Vector3> baseline, IList<(string chain, IList<(string tip, Vector3 p)> tips)> chains, out float max, out float mean)
        {
            var rows = new List<(string chain, float max, float mean, string worst)>(); int missing = 0; float sum = 0; int n = 0; max = 0;
            foreach (var c in chains)
            {
                float cm = 0, cs = 0; int cn = 0; string worst = "-";
                foreach (var t in c.tips)
                {
                    if (!baseline.TryGetValue(t.tip, out var b)) { missing++; continue; }
                    float d = (t.p - b).magnitude * 100; cs += d; cn++; if (d > cm) { cm = d; worst = t.tip.Substring(t.tip.LastIndexOf('/') + 1); }
                }
                if (cn == 0) continue;
                rows.Add((c.chain, cm, cs / cn, worst)); sum += cs; n += cn; max = Mathf.Max(max, cm);
            }
            mean = n > 0 ? sum / n : 0;
            var sb = new StringBuilder("chain | maxCm | meanCm | worst tip");
            foreach (var r in rows.OrderByDescending(r => r.max).ThenBy(r => r.chain, StringComparer.Ordinal).Take(30)) sb.Append("\n" + r.chain + " | " + N(r.max) + " | " + N(r.mean) + " | " + r.worst);
            if (rows.Count > 30) sb.Append("\n… " + (rows.Count - 30) + " more chains");
            if (missing > 0) sb.Append("\n" + missing + " tips have no baseline reading: the baseline drove a different chain set");
            return sb.ToString();
        }

        // ── Restore record: survives a domain reload that drops the pump with bones posed ──────────────

        internal static string FormatRecord(string rootPath, bool enableAnimator, List<(string path, Vector3 p, Quaternion q)> bones)
        {
            return "v1\n" + rootPath + "\n" + (enableAnimator ? "1" : "0") + string.Concat(bones.Select(b => "\n" + b.path + "\t" + R(b.p.x) + "," + R(b.p.y) + "," + R(b.p.z) + "\t" + R(b.q.x) + "," + R(b.q.y) + "," + R(b.q.z) + "," + R(b.q.w)));
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
            RestoreClock();
            string control = SessionState.GetString(ControlKey, ""), controlNote = "";
            if (control.Length > 0)
            {
                int n = 0, all = 0;
                foreach (var l in control.Split('\n'))
                {
                    var f = l.Split('\t'); all++;
                    var h = EditorApplication.isPlaying && f.Length == 2 ? SceneHandle.Resolve(f[0]) : new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };
                    var comps = h.Ok ? h.Object.GetComponents<VRCPhysBoneBase>() : new VRCPhysBoneBase[0];
                    if (int.TryParse(f.Length == 2 ? f[1] : "", out int i) && i >= 0 && i < comps.Length) { comps[i].enabled = true; n++; }
                }
                SessionState.EraseString(ControlKey);
                controlNote = "; re-enabled " + n + "/" + all + " chains a control drive held disabled";
            }
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
            var line = Tag + " Recover => FAIL | a domain reload tore down a drive mid-flight: " + what + controlNote + "; its rows are void";
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
                if (float.IsNaN(p.hold)) return "poses '" + p.name + "': hold is seconds, 0 or more, or below 0 for the drive's";
                if (!Samples.Contains(p.sample)) return "poses '" + p.name + "': sample is one of " + string.Join(", ", Samples);
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
