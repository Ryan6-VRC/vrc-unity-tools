using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using VRC.Dynamics;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// A frame-stepped play-mode recorder: declared columns, one CSV row per emulator runtime per player
    /// frame, under <see cref="RunLogFormat.RunLogDir"/>. Doors: <see cref="Run"/> starts a take,
    /// <see cref="MoveAvatar"/> scripts the local avatar root, <see cref="Mark"/> labels a phase,
    /// <see cref="End"/> ends the take, <see cref="Status"/> reports. The meta-repo's
    /// <c>tools/report_play.py</c> reads the file.
    ///
    /// <para>The file: <c>#</c> lines open it, the call and one <c>resolve</c> line per column naming what it
    /// resolved to. The header row is <c>frame,t,dt,who,root.x,root.y,root.z,root.yaw</c> and then the declared
    /// columns; <c>who</c> is <c>L</c> for the local runtime and <c>C1</c>, <c>C2</c>… for clones in order of
    /// appearance, never renumbered. Body <c>#</c> lines are events, each <c># f=&lt;frame&gt; …</c>: a clone
    /// picked up with its own <c>resolve</c> lines, a clone gone, a move, a mark, a gap, a parameter lookup
    /// that missed (its cell left empty), and the stop with its reason. Rows reach disk every frame.</para>
    ///
    /// <para>It records and never steps: <c>GrabPhysBone.Advance</c> owns the clock. One
    /// <c>EditorApplication.update</c> callback acts once per new <c>Time.frameCount</c>, reading every runtime
    /// first and only then applying the scripted move and the clone-root copy, so both land in the next frame
    /// and no read sees a moved root against pins that have not re-solved.</para>
    ///
    /// <para>The emulator is reached by reflection only, through <c>EmulatorBinding</c>'s names, so this file
    /// carries no hard dependency on av3emu. Emulator objects are resolved to names every
    /// row, never cached: the emulator rebuilds its parameter entries and can rebuild its playable graph
    /// mid-session, and a cached entry would freeze without an error. The one thing kept across rows is the
    /// handle of the playable a <c>param</c> column was last verified against: the check that the playable
    /// still carries the parameter allocates a managed parameter per entry per row, which on an FX controller
    /// of some hundreds of parameters slowed a run many times over, and a rebuilt graph hands back a different
    /// handle, so the check re-runs exactly when it can fail.</para>
    /// </summary>
    [AgentTool]
    public static class RecordPlay
    {
        private const string Tag = "[RecordPlay]";

        private static readonly string[] Reserved = { "frame", "t", "dt", "who", "root" };
        private static readonly Regex ColumnSpec = new Regex(@"^(?:(?<alias>[^=:]+)=)?(?<kind>tf|weights|param|active):(?<target>.+)$");
        private static readonly Regex VrcFuryPrefix = new Regex(@"^VF\d+_");

        private static Take _take;
        private static string _lastStop = "(no take yet)";

        // ── Doors ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Start a take. <paramref name="file"/> is a <c>.csv</c> path relative to
        /// <see cref="RunLogFormat.RunLogDir"/> that must not exist yet. Returns the absolute path, the handle
        /// <c>report_play.py</c> takes, with the current frame and every column's resolution on the local
        /// runtime.</summary>
        public static string Run(string file, string columns, bool clones = true, bool alwaysAnimate = true, bool copyRoot = true)
        {
            if (!EditorApplication.isPlaying) return Fail("play mode only: enter play, then call RecordPlay.Run");
            if (_take != null) return Fail("a take is running into " + _take.Path + ": RecordPlay.End() it first");

            string miss;
            var emu = Emu.Resolve(out miss);
            if (emu == null) return Fail(miss);

            string root = Root();
            if (string.IsNullOrEmpty(file) || Path.IsPathRooted(file) || !file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return Fail("file must be a .csv path relative to " + RunLogFormat.RunLogDir + "/, e.g. \"my-run/walk.csv\"");
            string path = Path.GetFullPath(Path.Combine(root, file));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Fail("file '" + file + "' leaves " + RunLogFormat.RunLogDir + "/");
            if (File.Exists(path)) return Fail(path + " exists: name a fresh take");

            List<Col> cols;
            string bad = ParseColumns(columns, out cols);
            if (bad != null) return Fail(bad);

            Component local;
            bad = FindLocal(emu, out local);
            if (bad != null) return Fail(bad);

            var take = new Take { Emu = emu, Path = path, Cols = cols, Clones = clones, AlwaysAnimate = alwaysAnimate, CopyRoot = copyRoot };
            var rt = new Rt { Who = "L", C = local, Root = local.transform };
            var res = new StringBuilder();
            foreach (var c in cols)
            {
                string why;
                var r = Resolve(emu, rt, c, out why);
                if (r == null) return Fail("column '" + c.Alias + "' (" + c.Spec + ") on the local runtime: " + why);
                rt.Res.Add(r);
                res.Append(res.Length > 0 ? "; " : "").Append(c.Alias).Append('=').Append(r.Describe);
            }
            take.Local = rt;
            take.Runtimes.Add(rt);

            var head = new StringBuilder();
            head.Append("# RecordPlay take ").Append(file.Replace('\\', '/')).Append(" started at frame ").Append(Time.frameCount).Append('\n');
            head.Append("# call: columns=\"").Append(OneLine(columns ?? "")).Append("\" clones=").Append(clones).Append(" alwaysAnimate=").Append(alwaysAnimate)
                .Append(" copyRoot=").Append(copyRoot).Append('\n');
            AppendResolution(head, rt, cols);
            head.Append("frame,t,dt,who,root.x,root.y,root.z,root.yaw");
            foreach (var c in cols) foreach (var h in c.Headers) head.Append(',').Append(h);
            head.Append('\n');

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, head.ToString());

            _take = take;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            return Ok("Record", path, "frame=" + Time.frameCount + " | L: " + res);
        }

        /// <summary>Move the local avatar root by a world offset and a yaw, linearly over
        /// <paramref name="frames"/> frames. The callback logs the start pose at the next frame boundary it
        /// sees, and the first step lands in the frame after that line.</summary>
        public static string MoveAvatar(float dx, float dz, float dyaw, int frames)
        {
            if (_take == null) return Fail("no take running: RecordPlay.Run first; a move is recorded with its take");
            if (frames <= 0) return Fail("frames must be > 0");
            if (_take.PendingMove != null || _take.Move != null)
                return Fail("a move is in progress; wait until its frames have run (RecordPlay.Status() shows it)");
            _take.PendingMove = new Motion { Dx = dx, Dz = dz, Dyaw = dyaw, Frames = frames };
            return Ok("Move", "the local avatar", "dx=" + F(dx) + " dz=" + F(dz) + " dyaw=" + F(dyaw) + " over " + frames
                + " frames from the next frame boundary");
        }

        /// <summary>Write a <c>#</c> event line carrying <paramref name="text"/> at the next frame boundary,
        /// labelling the rows that follow.</summary>
        public static string Mark(string text)
        {
            if (_take == null) return Fail("no take running: RecordPlay.Run first");
            _take.Marks.Add(OneLine(text ?? ""));
            return Ok("Mark", _take.Path, text);
        }

        /// <summary>End the take: writes the stop line and returns the path and row counts.</summary>
        public static string End()
        {
            if (_take == null) return Fail("no take running; the last one stopped: " + _lastStop);
            string counts = Counts(_take);
            string path = _take.Path;
            Stop("End()");
            // For the Project window only, and only here: the other stops run inside play exit or a domain
            // reload, and the editor's next refresh picks the file up without help.
            string asset = RunLogFormat.RunLogDir + path.Substring(Root().Length).Replace('\\', '/');
            RunLogFormat.PublishArtifact(asset.Substring(0, asset.LastIndexOf('/')), asset);
            return Ok("End", path, counts);
        }

        /// <summary>Pure read: whether a take is running, its file, rows per runtime, last frame, gaps, any
        /// move in progress, and the last stop reason.</summary>
        public static string Status()
        {
            if (_take == null) return Tag + " Status: no take running | last stop: " + _lastStop;
            var t = _take;
            string move = t.PendingMove != null ? "pending" : t.Move != null ? (t.Move.Step + "/" + t.Move.Frames) : "none";
            return Tag + " Status: running into " + t.Path + " | last frame=" + t.Last + " | " + Counts(t) + " | gaps=" + t.Gaps
                + " | move=" + move + " | last stop: " + _lastStop;
        }

        // ── The callback ──────────────────────────────────────────────────────────────────────────

        private static void Tick()
        {
            var t = _take;
            if (t == null) { EditorApplication.update -= Tick; return; }
            var sb = new StringBuilder();
            try
            {
                if (!EditorApplication.isPlaying) { Stop("left play mode"); return; }
                int f = Time.frameCount;
                if (f == t.Last) return;   // update fires several times per player frame

                if (t.Last >= 0 && f > t.Last + 1)
                {
                    t.Gaps++;
                    sb.Append("# f=").Append(f).Append(" gap: frames ").Append(t.Last + 1).Append("..").Append(f - 1).Append(" not seen\n");
                }
                t.Last = f;

                if (t.Local.C == null) { Flush(t, sb); Stop("the local runtime is gone"); return; }
                if (t.Clones) PickUpClones(t, f, sb);
                foreach (var m in t.Marks) sb.Append("# f=").Append(f).Append(" mark: ").Append(m).Append('\n');
                t.Marks.Clear();

                // Read every runtime before anything moves.
                for (int i = 0; i < t.Runtimes.Count; i++)
                {
                    var rt = t.Runtimes[i];
                    if (rt.Gone) continue;
                    if (rt.C == null)
                    {
                        rt.Gone = true;
                        sb.Append("# f=").Append(f).Append(' ').Append(rt.Who).Append(" gone\n");
                        continue;
                    }
                    // A row is built whole before it joins the frame, so a throw mid-row leaves no half row.
                    var row = new StringBuilder();
                    Row(t, rt, f, row);
                    sb.Append(row);
                    int n;
                    t.Rows.TryGetValue(rt.Who, out n);
                    t.Rows[rt.Who] = n + 1;
                }

                // Then move: these writes land in the next frame.
                Transform lr = t.Local.Root;
                if (t.PendingMove != null)
                {
                    var m = t.PendingMove;
                    t.PendingMove = null;
                    m.X0 = lr.position.x; m.Z0 = lr.position.z; m.Yaw0 = lr.eulerAngles.y;
                    t.Move = m;
                    sb.Append("# f=").Append(f).Append(" move: from x=").Append(F(m.X0)).Append(" z=").Append(F(m.Z0)).Append(" yaw=").Append(F(m.Yaw0))
                        .Append(" by dx=").Append(F(m.Dx)).Append(" dz=").Append(F(m.Dz)).Append(" dyaw=").Append(F(m.Dyaw))
                        .Append(" over ").Append(m.Frames).Append(" frames, first step in the next frame\n");
                }
                if (t.Move != null)
                {
                    var m = t.Move;
                    m.Step++;
                    float s = m.Step / (float)m.Frames;
                    lr.SetPositionAndRotation(new Vector3(m.X0 + m.Dx * s, lr.position.y, m.Z0 + m.Dz * s), Quaternion.Euler(0f, m.Yaw0 + m.Dyaw * s, 0f));
                    if (m.Step >= m.Frames) t.Move = null;
                }
                if (t.CopyRoot)
                    foreach (var rt in t.Runtimes)
                        if (rt != t.Local && !rt.Gone && rt.C != null) rt.Root.SetPositionAndRotation(lr.position, lr.rotation);

                Flush(t, sb);
            }
            catch (Exception e)
            {
                // A throwing update subscriber starves every later one (GrabPhysBone.Advance hangs), so a
                // fault ends the take here, in the file, rather than escaping. What the frame had already
                // built goes down first.
                try { Flush(t, sb); } catch (Exception) { }
                Stop("exception: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void PickUpClones(Take t, int f, StringBuilder sb)
        {
            var list = t.Emu.Clones.GetValue(t.Local.C) as IList;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i] as Component;
                if (c == null || t.Seen.Contains(c)) continue;
                t.Seen.Add(c);
                var rt = new Rt { Who = "C" + (++t.CloneCount), C = c, Root = c.transform };
                string animate = "";
                if (t.AlwaysAnimate)
                {
                    var a = c.GetComponent<Animator>();
                    if (a != null) { a.cullingMode = AnimatorCullingMode.AlwaysAnimate; animate = ", Animator set AlwaysAnimate"; }
                    else animate = ", no Animator to set AlwaysAnimate on";
                }
                sb.Append("# f=").Append(f).Append(' ').Append(rt.Who).Append(" picked up: ").Append(c.gameObject.name)
                    .Append(" (NonLocalClones[").Append(i).Append("])").Append(animate).Append('\n');
                foreach (var col in t.Cols)
                {
                    string why;
                    var r = Resolve(t.Emu, rt, col, out why);
                    rt.Res.Add(r);
                    sb.Append("# f=").Append(f).Append(" resolve ").Append(rt.Who).Append(' ').Append(col.Alias).Append(": ")
                        .Append(r != null ? r.Describe : "UNRESOLVED, cells left empty: " + why).Append('\n');
                }
                t.Runtimes.Add(rt);
            }
        }

        private static void Row(Take t, Rt rt, int f, StringBuilder sb)
        {
            var root = rt.Root;
            sb.Append(f).Append(',').Append(F(Time.time)).Append(',').Append(Time.deltaTime.ToString("F5", CultureInfo.InvariantCulture))
                .Append(',').Append(rt.Who).Append(',').Append(Tf(root));
            for (int i = 0; i < t.Cols.Count; i++)
            {
                var c = t.Cols[i];
                var r = rt.Res[i];
                if (r == null) { for (int k = 0; k < c.Headers.Length; k++) sb.Append(','); continue; }
                switch (c.Kind)
                {
                    case Kind.Tf: sb.Append(',').Append(Tf(r.Transform)); break;
                    case Kind.Active: sb.Append(',').Append(r.Transform.gameObject.activeSelf ? '1' : '0'); break;
                    case Kind.Weights:
                    {
                        if (r.Constraint == null) throw new MissingReferenceException("the VRC constraint on " + c.Target + " was destroyed");
                        var src = r.Constraint.Sources;
                        for (int k = 0; k < c.Headers.Length; k++) sb.Append(',').Append(k < src.Count ? F(src[k].Weight) : "");
                        break;
                    }
                    case Kind.Param:
                    {
                        string v, why;
                        if (ReadParam(t.Emu, rt.C, r.Param, out v, out why)) sb.Append(',').Append(v);
                        else
                        {
                            sb.Append(',');
                            t.MissEvents.Append("# f=").Append(f).Append(' ').Append(rt.Who).Append(' ').Append(c.Alias).Append(" missed: ").Append(why).Append('\n');
                        }
                        break;
                    }
                }
            }
            sb.Append('\n');
            if (t.MissEvents.Length > 0) { sb.Append(t.MissEvents); t.MissEvents.Length = 0; }
        }

        // ── Resolution ────────────────────────────────────────────────────────────────────────────

        internal static string ParseColumns(string spec, out List<Col> cols)
        {
            cols = new List<Col>();
            if (string.IsNullOrWhiteSpace(spec)) return null;   // root columns only
            var aliases = new HashSet<string>(Reserved);
            foreach (var raw in spec.Split(';'))
            {
                var e = raw.Trim();
                if (e.Length == 0) continue;
                var m = ColumnSpec.Match(e);
                if (!m.Success)
                    return "column '" + e + "' is not alias=kind:target with kind tf, weights, param or active, e.g. \"hips=tf:Armature/Hips\"";
                var c = new Col { Spec = e, Target = m.Groups["target"].Value.Trim() };
                switch (m.Groups["kind"].Value)
                {
                    case "tf": c.Kind = Kind.Tf; break;
                    case "weights": c.Kind = Kind.Weights; break;
                    case "param": c.Kind = Kind.Param; break;
                    default: c.Kind = Kind.Active; break;
                }
                if (c.Kind == Kind.Param)
                {
                    int at = c.Target.LastIndexOf('@');
                    if (at >= 0)
                    {
                        c.Channel = c.Target.Substring(at + 1);
                        c.Target = c.Target.Substring(0, at);
                        if (c.Channel != "playable" && c.Channel != "mirror")
                            return "column '" + e + "': the channel after @ is playable or mirror";
                    }
                }
                string alias = m.Groups["alias"].Success ? m.Groups["alias"].Value.Trim() : c.Target.Substring(c.Target.LastIndexOf('/') + 1);
                if (alias.Length == 0 || alias.IndexOfAny(new[] { ',', ' ', '"', '#' }) >= 0)
                    return "column '" + e + "': alias '" + alias + "' must be non-empty with no comma, space, quote or #";
                if (!aliases.Add(alias))
                    return "column '" + e + "': alias '" + alias + "' is taken (reserved: " + string.Join(", ", Reserved) + "); give it alias=";
                c.Alias = alias;
                if (c.Kind == Kind.Tf) c.Headers = new[] { alias + ".x", alias + ".y", alias + ".z", alias + ".yaw" };
                else if (c.Kind != Kind.Weights) c.Headers = new[] { alias };
                cols.Add(c);
            }
            return null;
        }

        private static string FindLocal(Emu emu, out Component local)
        {
            local = null;
            var locals = new List<Component>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(emu.Runtime))
            {
                var c = (Component)o;
                if ((bool)emu.IsLocal.GetValue(c) && !(bool)emu.IsMirror.GetValue(c) && !(bool)emu.IsShadow.GetValue(c)) locals.Add(c);
            }
            if (locals.Count == 0)
                return "no local emulator runtime: the scene needs an enabled Avatars 3.0 Emulator Control and one active avatar (emulator.md §Setup)";
            if (locals.Count > 1)
                return "more than one local runtime (" + string.Join(", ", locals.Select(c => c.gameObject.name)) + "): deactivate all but the avatar under test";
            local = locals[0];
            return null;
        }

        private static Res Resolve(Emu emu, Rt rt, Col c, out string why)
        {
            why = null;
            if (c.Kind == Kind.Param)
            {
                var p = ResolveParam(emu, rt.C, c.Target, c.Channel, out why);
                return p == null ? null : new Res { Param = p, Describe = "param " + p.Name + " [" + p.Channel + " " + p.TypeName + "]" };
            }
            var tr = rt.Root.Find(c.Target);
            if (tr == null)
            {
                why = "no transform '" + c.Target + "' under " + rt.Root.name + DeepestPrefix(rt.Root, c.Target);
                return null;
            }
            if (c.Kind != Kind.Weights) return new Res { Transform = tr, Describe = (c.Kind == Kind.Tf ? "tf " : "active ") + c.Target };

            var con = tr.GetComponent<VRCConstraintBase>();
            if (con == null) { why = "'" + c.Target + "' carries no VRC constraint"; return null; }
            int n = con.Sources.Count;
            if (c.Headers == null)
            {
                c.Headers = new string[n];
                for (int k = 0; k < n; k++) c.Headers[k] = c.Alias + ".w" + k;
            }
            return new Res { Transform = tr, Constraint = con, Describe = "weights " + c.Target + " (" + con.GetType().Name + ", " + n + " sources)" };
        }

        private static string DeepestPrefix(Transform root, string path)
        {
            var parts = path.Split('/');
            var cur = root;
            int i = 0;
            for (; i < parts.Length; i++)
            {
                var next = cur.Find(parts[i]);
                if (next == null) break;
                cur = next;
            }
            var kids = new List<string>();
            foreach (Transform k in cur) kids.Add(k.name);
            return " (resolved as far as '" + string.Join("/", parts.Take(i)) + "', whose children are: " + string.Join(", ", kids.Take(20))
                + (kids.Count > 20 ? ", …" : "") + ")";
        }

        private static ParamRef ResolveParam(Emu emu, Component rt, string target, string channel, out string why)
        {
            why = null;
            var mirror = new Dictionary<string, char>();
            foreach (var pair in new[] { (emu.FloatIdx, 'f'), (emu.IntIdx, 'i'), (emu.BoolIdx, 'b') })
            {
                var d = pair.Item1.GetValue(rt) as Dictionary<string, int>;
                if (d == null) continue;
                foreach (var k in d.Keys) if (!mirror.ContainsKey(k)) mirror[k] = pair.Item2;
            }
            var names = new HashSet<string>(mirror.Keys);
            var playables = emu.Playables.GetValue(rt) as IList;
            if (playables != null)
                foreach (var o in playables)
                {
                    var p = (AnimatorControllerPlayable)o;
                    if (!p.IsValid()) continue;
                    for (int j = 0; j < p.GetParameterCount(); j++) names.Add(p.GetParameter(j).name);
                }

            string name = MatchName(names, target, out why);
            if (name == null) return null;
            int hash = Animator.StringToHash(name);
            int curve = -1, any = -1;
            AnimatorControllerParameterType curveType = 0, anyType = 0;
            if (playables != null)
                for (int i = 0; i < playables.Count; i++)
                {
                    var p = (AnimatorControllerPlayable)playables[i];
                    if (!p.IsValid()) continue;
                    for (int j = 0; j < p.GetParameterCount(); j++)
                    {
                        var ap = p.GetParameter(j);
                        if (ap.name != name) continue;
                        if (any < 0) { any = i; anyType = ap.type; }
                        if (curve < 0 && p.IsParameterControlledByCurve(hash)) { curve = i; curveType = ap.type; }
                    }
                }

            bool inMirror = mirror.ContainsKey(name);
            if (channel == "mirror" && !inMirror) { why = "'" + name + "' is not in the runtime's parameter mirror"; return null; }
            if (channel == "playable" && any < 0) { why = "'" + name + "' is in no playable controller"; return null; }

            bool usePlayable = channel == "playable" || (channel == null && (curve >= 0 || !inMirror));
            if (usePlayable)
            {
                int idx = curve >= 0 ? curve : any;
                var type = curve >= 0 ? curveType : anyType;
                return new ParamRef { Name = name, Hash = hash, Playable = idx, PType = type, Channel = "playable " + idx + (curve >= 0 ? " (animated)" : ""),
                    TypeName = type.ToString().ToLowerInvariant() };
            }
            char kind = mirror[name];
            return new ParamRef { Name = name, Hash = hash, Playable = -1, MirrorKind = kind, Channel = "mirror",
                TypeName = kind == 'f' ? "float" : kind == 'i' ? "int" : "bool" };
        }

        /// <summary>The one name in <paramref name="names"/> a <c>param</c> target means: an exact name wins,
        /// else the single name equal to it once <see cref="Strip"/>ped. None or several returns null with
        /// the reason in <paramref name="why"/>.</summary>
        internal static string MatchName(ICollection<string> names, string target, out string why)
        {
            why = null;
            if (names.Contains(target)) return target;
            var hits = names.Where(n => Strip(n) == target).OrderBy(n => n).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) { why = "'" + target + "' matches " + string.Join(", ", hits) + ": name one exactly"; return null; }
            var near = names.Where(n => n.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n).Take(10).ToList();
            why = "no parameter named '" + target + "' (after stripping a VRCFury VF<n>_ prefix and a Modular Avatar $ suffix)"
                + (near.Count > 0 ? "; names containing it: " + string.Join(", ", near) : "");
            return null;
        }

        internal static string Strip(string n)
        {
            var m = VrcFuryPrefix.Match(n);
            if (m.Success) n = n.Substring(m.Length);
            int d = n.IndexOf('$');
            return d >= 0 ? n.Substring(0, d) : n;
        }

        private static bool ReadParam(Emu emu, Component rt, ParamRef p, out string value, out string why)
        {
            value = null; why = null;
            if (p.Playable >= 0)
            {
                var list = emu.Playables.GetValue(rt) as IList;
                if (list == null || p.Playable >= list.Count) { why = "playable " + p.Playable + " no longer exists"; return false; }
                var pl = (AnimatorControllerPlayable)list[p.Playable];
                if (!pl.IsValid()) { why = "playable " + p.Playable + " is no longer valid (the emulator rebuilt its graph)"; return false; }
                // A rebuilt graph can reorder the list; a playable without the parameter would read a flat 0.
                // Verified once per handle: the scan allocates a parameter per entry, and the handle changes
                // with the graph.
                var handle = pl.GetHandle();
                if (handle != p.VerifiedHandle)
                {
                    bool has = false;
                    for (int j = 0; j < pl.GetParameterCount() && !has; j++) has = pl.GetParameter(j).nameHash == p.Hash;
                    if (!has) { why = "playable " + p.Playable + " no longer carries '" + p.Name + "'"; return false; }
                    p.VerifiedHandle = handle;
                }
                switch (p.PType)
                {
                    case AnimatorControllerParameterType.Float: value = F(pl.GetFloat(p.Hash)); return true;
                    case AnimatorControllerParameterType.Int: value = pl.GetInteger(p.Hash).ToString(CultureInfo.InvariantCulture); return true;
                    default: value = pl.GetBool(p.Hash) ? "1" : "0"; return true;
                }
            }
            var idxField = p.MirrorKind == 'f' ? emu.FloatIdx : p.MirrorKind == 'i' ? emu.IntIdx : emu.BoolIdx;
            var listField = p.MirrorKind == 'f' ? emu.Floats : p.MirrorKind == 'i' ? emu.Ints : emu.Bools;
            var d = idxField.GetValue(rt) as Dictionary<string, int>;
            var entries = listField.GetValue(rt) as IList;
            int idx;
            if (d == null || entries == null || !d.TryGetValue(p.Name, out idx) || idx >= entries.Count)
            {
                why = "'" + p.Name + "' left the runtime's parameter mirror";
                return false;
            }
            var entry = entries[idx];
            if (p.MirrorKind == 'f') value = F((float)emu.FloatValue.GetValue(entry));
            else if (p.MirrorKind == 'i') value = ((int)emu.IntValue.GetValue(entry)).ToString(CultureInfo.InvariantCulture);
            else value = (bool)emu.BoolValue.GetValue(entry) ? "1" : "0";
            return true;
        }

        private static void AppendResolution(StringBuilder sb, Rt rt, List<Col> cols)
        {
            for (int i = 0; i < cols.Count; i++)
                sb.Append("# resolve ").Append(rt.Who).Append(' ').Append(cols[i].Alias).Append(": ").Append(rt.Res[i].Describe).Append('\n');
        }

        // ── Stop and plumbing ─────────────────────────────────────────────────────────────────────

        private static void Stop(string reason)
        {
            var t = _take;
            _take = null;
            EditorApplication.update -= Tick;
            if (t == null) return;
            reason = OneLine(reason);
            _lastStop = reason + " at frame " + t.Last + " (" + t.Path + ")";
            var tail = new StringBuilder();
            foreach (var m in t.Marks) tail.Append("# f=").Append(t.Last).Append(" mark never reached a frame: ").Append(m).Append('\n');
            if (t.PendingMove != null) tail.Append("# f=").Append(t.Last).Append(" move never started: the take stopped first\n");
            tail.Append("# f=").Append(t.Last).Append(" stopped: ").Append(reason).Append('\n');
            try { File.AppendAllText(t.Path, tail.ToString()); }
            catch (Exception e) { _lastStop += "; the stop line could not be written: " + e.Message; }
            Debug.Log(Tag + " stopped: " + _lastStop);
        }

        private static void Flush(Take t, StringBuilder sb)
        {
            if (sb.Length > 0) File.AppendAllText(t.Path, sb.ToString());
        }

        private static string Root() =>
            Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, RunLogFormat.RunLogDir));

        private static string Counts(Take t) =>
            "rows " + (t.Rows.Count == 0 ? "none" : string.Join(" ", t.Runtimes.Select(r => { int n; t.Rows.TryGetValue(r.Who, out n); return r.Who + "=" + n; })));

        private static string Tf(Transform t)
        {
            var p = t.position;
            return F(p.x) + "," + F(p.y) + "," + F(p.z) + "," + F(t.eulerAngles.y);
        }

        private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

        private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

        private static string Ok(string label, string subject, string detail)
        {
            var s = Tag + " " + label + " " + subject + " => OK | " + detail;
            Debug.Log(s);
            return s;
        }

        private static string Fail(string message)
        {
            var s = Tag + " FAIL: " + message;
            Debug.LogError(s);
            return s;
        }

        [InitializeOnLoadMethod]
        private static void Install()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
        }

        // Play entry and exit may skip the domain reload (Enter Play Mode Options), so a take must end
        // here or its subscription would carry into the next play session.
        private static void OnPlayModeChanged(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.ExitingPlayMode && _take != null) Stop("left play mode");
        }

        private static void OnBeforeReload()
        {
            if (_take != null) Stop("domain reload");
        }

        // ── Types ─────────────────────────────────────────────────────────────────────────────────

        internal enum Kind { Tf, Weights, Param, Active }

        internal sealed class Col
        {
            public string Alias, Spec, Target, Channel;
            public Kind Kind;
            public string[] Headers;
        }

        private sealed class ParamRef
        {
            public string Name, Channel, TypeName;
            public int Hash, Playable;
            public char MirrorKind;
            public AnimatorControllerParameterType PType;
            public PlayableHandle VerifiedHandle;
        }

        private sealed class Res
        {
            public Transform Transform;
            public VRCConstraintBase Constraint;
            public ParamRef Param;
            public string Describe;
        }

        private sealed class Rt
        {
            public string Who;
            public Component C;
            public Transform Root;
            public bool Gone;
            public readonly List<Res> Res = new List<Res>();
        }

        private sealed class Motion
        {
            public float Dx, Dz, Dyaw, X0, Z0, Yaw0;
            public int Frames, Step;
        }

        private sealed class Take
        {
            public Emu Emu;
            public string Path;
            public List<Col> Cols;
            public bool Clones, AlwaysAnimate, CopyRoot;
            public Rt Local;
            public readonly List<Rt> Runtimes = new List<Rt>();
            public readonly HashSet<Component> Seen = new HashSet<Component>();
            public int CloneCount, Gaps, Last = -1;
            public readonly Dictionary<string, int> Rows = new Dictionary<string, int>();
            public readonly List<string> Marks = new List<string>();
            public readonly StringBuilder MissEvents = new StringBuilder();
            public Motion PendingMove, Move;
        }

        /// <summary>The emulator surface this door reflects, resolved up front; a miss refuses the whole Run.</summary>
        private sealed class Emu
        {
            public Type Runtime;
            public FieldInfo IsLocal, IsMirror, IsShadow, Clones, Playables, Floats, Ints, Bools, FloatIdx, IntIdx, BoolIdx, IntValue, BoolValue;
            public PropertyInfo FloatValue;

            public static Emu Resolve(out string miss)
            {
                miss = null;
                var t = EmulatorBinding.ResolveType(EmulatorBinding.RuntimeFullName);
                if (t == null) { miss = "Av3Emulator is not installed (" + EmulatorBinding.RuntimeFullName + " not found)"; return null; }
                const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                const BindingFlags any = pub | BindingFlags.NonPublic;
                var e = new Emu
                {
                    Runtime = t,
                    IsLocal = t.GetField(EmulatorBinding.IsLocal, pub),
                    IsMirror = t.GetField(EmulatorBinding.IsMirrorClone, pub),
                    IsShadow = t.GetField(EmulatorBinding.IsShadowClone, pub),
                    Clones = t.GetField(EmulatorBinding.NonLocalClones, pub),
                    Playables = t.GetField(EmulatorBinding.Playables, any),
                    Floats = t.GetField(EmulatorBinding.Floats, pub),
                    Ints = t.GetField(EmulatorBinding.Ints, pub),
                    Bools = t.GetField(EmulatorBinding.Bools, pub),
                    FloatIdx = t.GetField(EmulatorBinding.FloatToIndex, pub),
                    IntIdx = t.GetField(EmulatorBinding.IntToIndex, pub),
                    BoolIdx = t.GetField(EmulatorBinding.BoolToIndex, pub),
                };
                var missing = new List<string>();
                foreach (var f in typeof(Emu).GetFields())
                    if (f.FieldType == typeof(FieldInfo) && f.Name != "IntValue" && f.Name != "BoolValue" && f.GetValue(e) == null) missing.Add(f.Name);
                if (missing.Count == 0)
                {
                    e.FloatValue = Element(e.Floats)?.GetProperty(EmulatorBinding.ExportedValue, pub);
                    e.IntValue = Element(e.Ints)?.GetField(EmulatorBinding.ParamEntryValue, pub);
                    e.BoolValue = Element(e.Bools)?.GetField(EmulatorBinding.ParamEntryValue, pub);
                    if (e.FloatValue == null) missing.Add("FloatParam." + EmulatorBinding.ExportedValue);
                    if (e.IntValue == null) missing.Add("IntParam." + EmulatorBinding.ParamEntryValue);
                    if (e.BoolValue == null) missing.Add("BoolParam." + EmulatorBinding.ParamEntryValue);
                }
                if (missing.Count > 0)
                {
                    miss = "the installed Av3Emulator lacks members this door reads: " + string.Join(", ", missing)
                        + "; an emulator update renamed them, so re-pin the names in EmulatorBinding";
                    return null;
                }
                return e;
            }

            private static Type Element(FieldInfo listField) =>
                listField.FieldType.IsGenericType ? listField.FieldType.GetGenericArguments()[0] : null;
        }
    }
}
