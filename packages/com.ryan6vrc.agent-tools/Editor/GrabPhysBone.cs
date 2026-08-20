using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The primitive set for simulating player manipulation of physbones in play mode: grab, move,
    /// release, step frames, read what is held. Six doors, composed by the caller — <c>Run</c> →
    /// <c>Advance(5)</c> → <c>Move</c> → <c>Advance(5)</c> → … → <c>Release</c> — rather than one
    /// choreography door. It induces manipulation; it asserts nothing and emits no verdict token
    /// beyond OK/PENDING/FAIL, and it is not a sampler.
    ///
    /// <para>Why a tool at all: <c>docs/emulator.md</c> §Induce a physbone grab/pose is mostly
    /// mechanical traps every session re-arms by re-typing — bracket-before-attempt, same-call
    /// <c>GlobalPosition</c>, release-while-iterating, release-by-the-chain-you-meant. Each of those
    /// is unreachable here rather than documented: <see cref="Run"/> brackets and seeds inside the
    /// one call, <see cref="Release"/> materialises the grab list before releasing, and the handle
    /// IS the returned grab's own <c>ChainId</c>, so there is no way to name a chain you did not
    /// actually get.</para>
    ///
    /// <para>No reflection anywhere: the whole SDK surface this drives is public
    /// (<c>PhysBoneManager</c>, its nested <c>Grab</c>, <c>ChainId</c>, <c>VRCPhysBoneBase.chainId</c>),
    /// so an SDK rename or overload change is a COMPILE ERROR here. That is the canary — there is
    /// deliberately no binding-canary test, unlike <c>EmulatorBindingCanaryTests</c>, which exists
    /// only because av3emu is reached reflectively. This tool touches no emulator member at all.</para>
    ///
    /// <para><b>The venue freezes while a grab is held.</b> Pause is a property of the held grab, not
    /// of one <see cref="Advance"/>: a resumed editor runs an unbounded, unrecorded number of frames
    /// between MCP calls (12–200 fps) with the grab live and its target stale, which would make the
    /// exact frame count this tool exists to provide a fiction. <see cref="Run"/>/<see cref="Reach"/>
    /// freeze; <c>Release(resume: true)</c> hands the venue back. The freeze is visible to everything
    /// else in the editor — notably a <c>RenderThumbnailPlay</c> Shoot in flight will trip its own
    /// stall guard.</para>
    ///
    /// <para><b>Play mode only.</b> <c>PhysBoneManager.Inst</c> is null in edit mode, so there is
    /// nothing to grab and nothing needs to survive play entry.</para>
    /// </summary>
    [AgentTool]
    public static class GrabPhysBone
    {
        private const string Tag = "[GrabPhysBone]";

        // If Time.frameCount does not advance for this long the player loop has stalled — a modal, a
        // blocked main thread. Abort rather than spin: a pump stuck in flight refuses every later door
        // for the rest of the session. Mirrors RenderThumbnailPlay's guard of the same name.
        private const double StallSeconds = 20.0;

        // ── Cross-call state ──────────────────────────────────────────────────────────────────────
        //
        // This tool DOES carry cross-call state, and every field below is a plain static that a domain
        // reload wipes. That is survivable for the handle set (av3emu destroys the manager on
        // compilationStarted, so the grabs die with it) but NOT for the two native mutations: the
        // editor stays paused and Time.fixedDeltaTime stays pinned with no managed record of what they
        // were. §Restore record below is the mitigation.

        private static readonly HashSet<string> Owned = new HashSet<string>();
        private static bool _frozen;              // this tool paused the editor
        private static bool _priorPaused;         // what pause was before we froze it
        private static bool _dtPinned;            // this tool wrote Time.fixedDeltaTime
        private static float _savedFixedDt;

        private static EditorApplication.CallbackFunction _pump; // non-null while an Advance is in flight
        private static int _pumpTarget;
        private static int _pumpBaseline = -1;    // -1 until frameCount settles under the new pause
        private static int _pumpLastStepAt = int.MinValue;
        private static int _pumpPrevFrame = -1;
        private static int _pumpStableTicks;
        private static double _pumpLastProgress;
        private static double _pumpStartedAt;
        private static string _pumpResult = "(no Advance yet)";

        // ── Doors ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Grab by chain address: a hierarchy path to the <c>VRCPhysBone</c> host plus an int
        /// bone index. Always available — including on the many rigs that author <c>radius 0</c>, which
        /// the spatial query in <see cref="Reach"/> cannot touch at any grabber radius. Freezes the
        /// venue. Seeds the grab target from the bone's own world position in this same call, so the
        /// chain can never be left pinned at the origin. Returns
        /// <c>[GrabPhysBone] Grab &lt;path&gt;#&lt;bone&gt; =&gt; OK | handle=&lt;A:B&gt; …</c>; every refusal is a bare
        /// <c>FAIL</c> naming the fix.</summary>
        public static string Run(string pbPath, int boneIndex, int grabberId = -1)
        {
            string bad = GuardMutating();
            if (bad != null) return Fail(bad);

            var go = Resolve(pbPath);
            if (go == null)
                return Fail("no scene object at '" + pbPath + "' — pass the hierarchy path of the "
                    + "VRCPhysBone host (or its instance id); note a play-mode avatar is rebuilt, so an "
                    + "edit-mode path may not exist under the built clone");

            var pb = go.GetComponent<VRCPhysBoneBase>();
            if (pb == null)
                return Fail("'" + pbPath + "' carries no VRCPhysBone component — ReportGimmick.Run names "
                    + "the physbone hosts on a subtree");

            var mgr = PhysBoneManager.Inst;
            if (mgr == null) return Fail(NoManager);

            if (pb.bones == null || pb.bones.Count == 0)
                return Fail("chain '" + pbPath + "' is not registered with the manager yet (bones=0) — "
                    + "registration lands the frame after the component enables; GrabPhysBone.Advance(1) then retry");

            if (boneIndex < 0 || boneIndex >= pb.bones.Count)
                return Fail("boneIndex " + boneIndex + " is outside chain '" + pbPath + "' (bones=0.."
                    + (pb.bones.Count - 1) + ")");

            // Bracket. A repeat AttemptGrab on a grabbed chain returns null, which is indistinguishable
            // from a door that never works — so the state is read and named instead of guessed at.
            if (mgr.IsChainGrabbed(pb.chainId))
                return Fail("chain '" + pbPath + "' is already grabbed — GrabPhysBone.Held() names the "
                    + "holder; release it before grabbing again");

            var bone = pb.bones[boneIndex].transform;
            if (bone == null)
                return Fail("bone " + boneIndex + " of '" + pbPath + "' has no transform (the chain was "
                    + "rebuilt under you) — re-read the chain and retry");

            return Attempt(
                () => mgr.AttemptGrab(grabberId, pb.chainId, boneIndex),
                () => bone.position,
                "Grab", pbPath + "#" + boneIndex,
                "AttemptGrab returned null for '" + pbPath + "' bone " + boneIndex
                    + " though the chain read ungrabbed — re-read the chain id, which a recompile re-mints");
        }

        /// <summary>Grab spatially, at a reach point with a radius — what a player actually has. Its own
        /// door rather than an overload of <see cref="Run"/> because the preconditions differ: the
        /// spatial query searches the collision structure, so the physbone's own <c>radius</c> must be
        /// non-zero (it authors as 0, and no grabber radius compensates) and the bone must be simulated
        /// with a non-zero end radius. Reach is sum-of-radii — bone radius + <paramref name="radius"/>
        /// against the distance. Freezes the venue; seeds the target from the reach point.</summary>
        public static string Reach(float x, float y, float z, float radius, int grabberId = -1)
        {
            string bad = GuardMutating();
            if (bad != null) return Fail(bad);

            var mgr = PhysBoneManager.Inst;
            if (mgr == null) return Fail(NoManager);
            if (!(radius > 0f)) return Fail("radius must be > 0 — it is the grabber's reach, added to the bone's own radius");

            var at = new Vector3(x, y, z);
            return Attempt(
                () => mgr.AttemptGrab(grabberId, at, radius, at),
                () => at,
                "Reach", at.ToString("F3") + "r" + radius.ToString("0.###", CultureInfo.InvariantCulture),
                "no grabbable bone within reach of " + at.ToString("F3") + " at radius " + radius
                    + " — the usual cause is the physbone's own `radius` authoring as 0, which no grabber "
                    + "radius compensates for, or an unsimulated leaf bone; GrabPhysBone.Run(path, boneIndex) "
                    + "addresses the chain directly and ignores both");
        }

        /// <summary>Move the held target. Takes the hand/target point in world space and adds the grab's
        /// own <c>LocalOffset</c> internally, matching <c>PhysBoneGrabHelper</c>'s mouse path — the same
        /// convention the grab doors seed with, so the first Move to the point you grabbed at does not
        /// jump the chain.</summary>
        public static string Move(string handle, float x, float y, float z)
        {
            string bad = GuardMutating();
            if (bad != null) return Fail(bad);

            var mgr = PhysBoneManager.Inst;
            if (mgr == null) return Fail(NoManager);

            var g = FindLive(mgr, handle);
            if (g == null) return Fail(StaleHandle(handle));
            if (!Owned.Contains(handle)) return Fail(ForeignHandle(handle));

            var to = new Vector3(x, y, z);
            g.GlobalPosition = to + g.LocalOffset;
            return Ok("Move", handle + " -> " + to.ToString("F3"), "held=" + Owned.Count);
        }

        /// <summary>Release a grab this tool minted; with <paramref name="handle"/> omitted, release all
        /// of them. Never a foreign grab: <c>PhysBoneGrabHelper</c> holds a live grab while the operator
        /// has the mouse down, and releasing it leaves the helper writing <c>GlobalPosition</c> to a dead
        /// grab every frame and releasing it a second time on mouse-up. Foreign grabs are named, not
        /// touched. <paramref name="resume"/> also hands the venue back — a bare
        /// <c>Release(resume: true)</c> with nothing held is the normal way to unfreeze after settling.
        /// </summary>
        public static string Release(string handle = null, bool resume = false)
        {
            if (!Application.isPlaying)
            {
                // Nothing to release — the manager died with play mode. Still honour `resume`, since the
                // freeze is editor state that outlives the session that made it.
                string note = _frozen ? Thaw() : "venue was not frozen by this tool";
                Owned.Clear();
                PersistRecord();
                return Ok("Release", "(not in play mode)", note);
            }

            if (_pump != null) return Fail(PumpInFlight("Release"));

            var mgr = PhysBoneManager.Inst;
            if (mgr == null)
            {
                Owned.Clear();
                string note = resume && _frozen ? Thaw() : FreezeNote();
                PersistRecord();
                return Ok("Release", "(no manager)", "grab records dropped; " + note);
            }

            // Materialise before releasing: releasing while iterating GetGrabs() mutates the collection
            // and silently skips grabs.
            var live = new List<PhysBoneManager.Grab>(mgr.GetGrabs());
            var liveHandles = new List<string>();
            foreach (var g in live) liveHandles.Add(FormatHandle(g.chainId));

            var toRelease = new List<string>();
            var foreign = new List<string>();
            string refusal;
            SelectGrabsToRelease(liveHandles, Owned, handle, toRelease, foreign, out refusal);
            if (refusal != null) return Fail(refusal);

            int released = 0;
            foreach (var g in live)
            {
                var h = FormatHandle(g.chainId);
                if (!toRelease.Contains(h)) continue;
                mgr.ReleaseGrab(g.chainId);
                Owned.Remove(h);
                released++;
            }

            string thaw = resume && _frozen ? Thaw() : FreezeNote();
            PersistRecord();
            return Ok("Release", (handle ?? "(all owned)"),
                "released=" + released + " " + Names("foreign", foreign) + " " + thaw);
        }

        /// <summary>Step exactly <paramref name="frames"/> player-loop frames. The only async door: it
        /// arms a pump on <c>EditorApplication.update</c> and returns immediately (=&gt; PENDING), because
        /// <c>EditorApplication.Step()</c> needs the editor loop to run between frames and the call that
        /// issues it holds the main thread. Poll <see cref="Held"/>. It never waits on a blocked main
        /// thread — the stall guard aborts rather than spinning.
        ///
        /// <para><paramref name="dt"/> (0 = leave alone) pins <c>Time.fixedDeltaTime</c>, which is what a
        /// stepped frame's <c>Time.deltaTime</c> actually equals — <c>Time.captureDeltaTime</c> is ignored
        /// under <c>Step()</c> (measured). <b>It is a GLOBAL clock rescale</b>: the animator, every
        /// FixedUpdate, and the emulator's sync-tick accumulators all advance at the same rescaled rate,
        /// so a choreography stepped at a non-default dt supports no claim about a transition duration, a
        /// sync interval or a debounce window — only about the solve's convergence per unit simulated
        /// time.</para></summary>
        public static string Advance(int frames, float dt = 0f)
        {
            if (!Application.isPlaying) return Fail(NotPlaying("Advance"));
            if (_pump != null) return Fail(PumpInFlight("Advance"));
            if (frames <= 0) return Fail("frames must be > 0");
            if (dt < 0f) return Fail("dt must be >= 0 (0 leaves Time.fixedDeltaTime alone)");

            Freeze();
            if (dt > 0f && !_dtPinned)
            {
                _savedFixedDt = Time.fixedDeltaTime;
                _dtPinned = true;
            }
            if (dt > 0f) Time.fixedDeltaTime = dt;

            _pumpTarget = frames;
            _pumpBaseline = -1;
            _pumpLastStepAt = int.MinValue;
            _pumpPrevFrame = Time.frameCount;
            _pumpStableTicks = 0;
            _pumpStartedAt = EditorApplication.timeSinceStartup;
            _pumpLastProgress = _pumpStartedAt;
            _pumpResult = "(in flight)";
            PersistRecord();

            _pump = Pump;
            EditorApplication.update += _pump;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            string summary = Tag + " Advance " + frames + " frames"
                + (dt > 0f ? " @ dt=" + dt.ToString("0.#####", CultureInfo.InvariantCulture) : " @ dt=Time.fixedDeltaTime")
                + " => PENDING | poll GrabPhysBone.Held()";
            Debug.Log(summary);
            return summary;
        }

        /// <summary>What is held, and how the pump is doing. Pure read — no frame advances, nothing is
        /// released. Classifies every live grab as <c>ours</c>, <c>foreign</c> (another owner's — the SDK
        /// mouse helper, another probe) or <c>stale</c> (its chain no longer resolves, so it can never be
        /// released by chain id and is the one leak this door exists to surface).
        ///
        /// <para>Named <c>Held</c> rather than the family's <c>Status</c> because it has a subject of its
        /// own — what is held, by whom, what has gone stale — and carries pump progress besides, where
        /// <c>RenderThumbnailPlay.Status</c> is a bare poll.</para></summary>
        public static string Held()
        {
            if (!Application.isPlaying)
                return Ok("Held", "(not in play mode)", FreezeNote() + " | last-advance=" + _pumpResult);

            var mgr = PhysBoneManager.Inst;
            if (mgr == null)
                return Ok("Held", "(no manager)",
                    "PhysBoneManager.Inst is null — av3emu destroys it on compilationStarted, so any grab "
                    + "from before a recompile is gone | " + FreezeNote() + " | last-advance=" + _pumpResult);

            var ours = new List<string>();
            var foreign = new List<string>();
            var stale = new List<string>();
            foreach (var g in new List<PhysBoneManager.Grab>(mgr.GetGrabs()))
            {
                var h = FormatHandle(g.chainId);
                if (mgr.FindPhysBone(g.chainId) == null) stale.Add(h);
                else if (Owned.Contains(h)) ours.Add(h);
                else foreign.Add(h);
            }

            string pump = _pump == null
                ? "advance=idle"
                : "advance=" + (_pumpBaseline < 0 ? 0 : Mathf.Max(0, Time.frameCount - _pumpBaseline))
                    + "/" + _pumpTarget + " (in flight)";

            return Ok("Held", Names("ours", ours),
                Names("foreign", foreign) + " " + Names("stale", stale) + " | " + pump
                + " | last-advance=" + _pumpResult + " | " + FreezeNote());
        }

        // ── The pump ──────────────────────────────────────────────────────────────────────────────

        private static void Pump()
        {
            try
            {
                // Fold this tick's observation into the progress counters BEFORE building the record the
                // policy reads, or a tick that DID advance a frame is still judged against the previous
                // tick's staleness and can trip the stall guard on a healthy pump.
                int frame = Time.frameCount;
                if (frame != _pumpPrevFrame)
                {
                    _pumpPrevFrame = frame;
                    _pumpStableTicks = 0;
                    _pumpLastProgress = EditorApplication.timeSinceStartup;
                }
                else
                {
                    _pumpStableTicks++;
                }

                var obs = new PumpObservation
                {
                    IsPlaying = Application.isPlaying,
                    IsPaused = EditorApplication.isPaused,
                    FrameCount = frame,
                    Baseline = _pumpBaseline,
                    TargetFrames = _pumpTarget,
                    LastStepAtFrame = _pumpLastStepAt,
                    StableTicks = _pumpStableTicks,
                    SecondsSinceProgress = EditorApplication.timeSinceStartup - _pumpLastProgress,
                };

                string reason;
                switch (Decide(obs, out reason))
                {
                    case PumpAction.Wait:
                        return;

                    case PumpAction.LatchBaseline:
                        // Arming is racy: the frame in progress when pause was requested still completes,
                        // so the baseline is only trustworthy once frameCount has stopped moving.
                        _pumpBaseline = obs.FrameCount;
                        _pumpLastStepAt = int.MinValue;
                        _pumpLastProgress = EditorApplication.timeSinceStartup;
                        EditorApplication.Step();
                        _pumpLastStepAt = obs.FrameCount;
                        return;

                    case PumpAction.Step:
                        EditorApplication.Step();
                        _pumpLastStepAt = obs.FrameCount;
                        return;

                    case PumpAction.Finish:
                    {
                        int done = obs.FrameCount - _pumpBaseline;
                        double secs = EditorApplication.timeSinceStartup - _pumpStartedAt;
                        FinishPump("advanced " + done + "/" + _pumpTarget + " frames in "
                            + secs.ToString("0.0", CultureInfo.InvariantCulture) + "s ("
                            + (secs > 0.001 ? (done / secs).ToString("0", CultureInfo.InvariantCulture) : "?")
                            + " fps) => OK");
                        return;
                    }

                    default:
                        FinishPump("aborted after " + (_pumpBaseline < 0 ? 0 : obs.FrameCount - _pumpBaseline)
                            + "/" + _pumpTarget + " frames: " + reason + " => FAIL");
                        return;
                }
            }
            catch (Exception e)
            {
                // Nothing is on the caller's stack — this runs on an editor tick long after Advance
                // returned — so an unhandled throw here would leave the pump armed, refusing every later
                // door for the session, with the venue frozen and dt pinned.
                FinishPump("aborted: " + e.GetType().Name + ": " + e.Message + " => FAIL");
            }
        }

        private static void FinishPump(string result)
        {
            if (_pump != null) EditorApplication.update -= _pump;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            _pump = null;
            _pumpResult = result;

            if (_dtPinned)
            {
                Time.fixedDeltaTime = _savedFixedDt;
                _dtPinned = false;
            }
            PersistRecord();
            Debug.Log(Tag + " Advance " + result);
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode && change != PlayModeStateChange.EnteredEditMode) return;
            // The pump may never tick again, so the in-pump isPlaying check cannot be the only detector —
            // and without the unsubscribe the delegate survives into edit mode and throws every tick.
            Owned.Clear();
            if (_pump != null) FinishPump("aborted: play mode exited => FAIL");
            if (_frozen) Thaw();
            PersistRecord();
        }

        // ── Pure decision logic (unit-tested; no Unity state) ─────────────────────────────────────

        internal enum PumpAction { Wait, LatchBaseline, Step, Finish, Abort }

        internal struct PumpObservation
        {
            public bool IsPlaying;
            public bool IsPaused;
            public int FrameCount;
            public int Baseline;          // -1 while arming
            public int TargetFrames;
            public int LastStepAtFrame;   // int.MinValue = no step issued since the baseline latched
            public int StableTicks;       // consecutive ticks with FrameCount unchanged, while arming
            public double SecondsSinceProgress;
        }

        /// <summary>The whole pump policy, as a function of observable state. Split out because every
        /// branch below is a lifecycle hazard that a live test cannot provoke reliably: play-mode exit,
        /// a manual un-pause, a stalled player loop, and the arm-time off-by-one.
        ///
        /// <para>Frames are counted from <c>Time.frameCount</c>, never from issued <c>Step()</c> calls.
        /// <c>Step()</c> is asynchronous — it requests a frame that runs after the editor tick returns —
        /// and <c>EditorApplication.update</c> keeps firing while paused with <c>frameCount</c> frozen, so
        /// counting calls counts editor ticks and the exact-frame guarantee is a fiction.</para></summary>
        internal static PumpAction Decide(PumpObservation o, out string reason)
        {
            reason = null;
            if (!o.IsPlaying) { reason = "play mode exited mid-Advance"; return PumpAction.Abort; }
            if (o.SecondsSinceProgress >= StallSeconds)
            {
                reason = "player loop stalled — Time.frameCount did not advance for "
                    + StallSeconds.ToString("0", CultureInfo.InvariantCulture)
                    + "s; a modal or a blocked main thread, not a slow frame (go list dialogs)";
                return PumpAction.Abort;
            }

            if (o.Baseline < 0)
            {
                // Un-pause is only meaningful once we are past arming: Step() itself sets pause, and the
                // in-progress frame at arm time can be observed either way.
                return o.IsPaused && o.StableTicks >= 2 ? PumpAction.LatchBaseline : PumpAction.Wait;
            }

            if (!o.IsPaused)
            {
                reason = "the editor was un-paused mid-Advance, so frames ran uncounted at wall-clock dt";
                return PumpAction.Abort;
            }
            if (o.FrameCount - o.Baseline >= o.TargetFrames) return PumpAction.Finish;
            return o.FrameCount > o.LastStepAtFrame ? PumpAction.Step : PumpAction.Wait;
        }

        /// <summary>Which live grabs a <see cref="Release"/> should touch, and which are someone else's.
        /// Pure so the ownership rule is assertable with no manager present. A null/empty
        /// <paramref name="requested"/> means "all of ours"; a named one must be both live and ours.</summary>
        internal static void SelectGrabsToRelease(
            IList<string> liveHandles, ICollection<string> owned, string requested,
            List<string> toRelease, List<string> foreign, out string refusal)
        {
            refusal = null;
            toRelease.Clear();
            foreign.Clear();
            foreach (var h in liveHandles) if (!owned.Contains(h)) foreign.Add(h);

            if (string.IsNullOrEmpty(requested))
            {
                foreach (var h in liveHandles) if (owned.Contains(h)) toRelease.Add(h);
                return;
            }
            if (!liveHandles.Contains(requested)) { refusal = StaleHandle(requested); return; }
            if (!owned.Contains(requested)) { refusal = ForeignHandle(requested); return; }
            toRelease.Add(requested);
        }

        // ── Handle codec ──────────────────────────────────────────────────────────────────────────
        //
        // The handle is the returned grab's own ChainId. Both halves are ulong and real ids run close to
        // long.MaxValue (a measured one: 5363680280397112067), so this parses as ulong — a long.Parse
        // here would throw on a legitimate id. `A:B` is deliberately distinct from ChainId.ToString()'s
        // own `A.B` so a hand-pasted ToString cannot be mistaken for a handle this tool minted.

        internal static string FormatHandle(ChainId id) => FormatHandle(id.A, id.B);

        internal static string FormatHandle(ulong a, ulong b) =>
            a.ToString(CultureInfo.InvariantCulture) + ":" + b.ToString(CultureInfo.InvariantCulture);

        internal static bool TryParseHandle(string raw, out ulong a, out ulong b)
        {
            a = 0; b = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            var parts = raw.Split(':');
            return parts.Length == 2
                && ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out a)
                && ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out b);
        }

        // ── Restore record ────────────────────────────────────────────────────────────────────────
        //
        // Unity's default Script Changes While Playing is recompile-and-continue, so a mid-play domain
        // reload is expected rather than exotic. After one, every static above is gone while the editor
        // is STILL paused and Time.fixedDeltaTime is STILL pinned — and every later play session in that
        // editor then runs at the wrong dt with no record of it, silently rescaling every timing claim
        // made afterwards. SessionState is the right shelf: it outlives a domain reload and dies with the
        // editor, which is exactly this state's lifetime. (EditorPrefs would outlive the editor too, and
        // let a record from a crashed session clobber a later one's genuine settings.)

        private const string RecordKey = "Ryan6Vrc.GrabPhysBone.restoreRecord";

        internal struct RestoreRecord
        {
            public bool Frozen;
            public bool PriorPaused;
            public bool DtPinned;
            public float SavedFixedDt;
            public bool PumpArmed;
        }

        /// <summary>Encode the restore record. Inverse of <see cref="TryParseRestoreRecord"/>; the two are
        /// a domain reload apart, so no live test can prove they agree and the round-trip is the proof.</summary>
        internal static string FormatRestoreRecord(RestoreRecord r) =>
            "v1|" + (r.Frozen ? "1" : "0") + "|" + (r.PriorPaused ? "1" : "0") + "|"
            + (r.DtPinned ? "1" : "0") + "|" + r.SavedFixedDt.ToString("R", CultureInfo.InvariantCulture)
            + "|" + (r.PumpArmed ? "1" : "0");

        /// <summary>Decode a record written by <see cref="FormatRestoreRecord"/>. False (record left at
        /// default) for anything not well formed — absent, wrong version, wrong arity, unparseable dt.
        /// Half a restore is worse than none, so a malformed record is rejected whole.</summary>
        internal static bool TryParseRestoreRecord(string raw, out RestoreRecord r)
        {
            r = default(RestoreRecord);
            if (string.IsNullOrEmpty(raw)) return false;
            var p = raw.Split('|');
            float dt;
            if (p.Length != 6 || p[0] != "v1") return false;
            if (!float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out dt)) return false;
            r.Frozen = p[1] == "1";
            r.PriorPaused = p[2] == "1";
            r.DtPinned = p[3] == "1";
            r.SavedFixedDt = dt;
            r.PumpArmed = p[5] == "1";
            return true;
        }

        private static void PersistRecord()
        {
            if (!_frozen && !_dtPinned && _pump == null) { SessionState.EraseString(RecordKey); return; }
            SessionState.SetString(RecordKey, FormatRestoreRecord(new RestoreRecord
            {
                Frozen = _frozen,
                PriorPaused = _priorPaused,
                DtPinned = _dtPinned,
                SavedFixedDt = _savedFixedDt,
                PumpArmed = _pump != null,
            }));
        }

        /// <summary>Domain-reload recovery. Runs once per domain load; a surviving record means a session
        /// was torn down mid-flight with native state still mutated. Restores it, drops the record, and
        /// says so loudly — a silent recovery would leave the agent believing its Advance completed.</summary>
        internal static void RecoverAfterDomainReload()
        {
            RestoreRecord r;
            if (!TryParseRestoreRecord(SessionState.GetString(RecordKey, null), out r))
            {
                SessionState.EraseString(RecordKey);
                return;
            }
            SessionState.EraseString(RecordKey);

            var what = new List<string>();
            if (r.DtPinned)
            {
                Time.fixedDeltaTime = r.SavedFixedDt;
                what.Add("Time.fixedDeltaTime=" + r.SavedFixedDt.ToString("R", CultureInfo.InvariantCulture));
            }
            if (r.Frozen)
            {
                EditorApplication.isPaused = r.PriorPaused;
                what.Add("isPaused=" + r.PriorPaused);
            }
            _frozen = false;
            _dtPinned = false;
            Owned.Clear();

            if (what.Count == 0) return;

            var report = "RECOVERED from a domain reload — restored " + string.Join(", ", what.ToArray())
                + ". A recompile or package import tore down a GrabPhysBone session mid-flight"
                + (r.PumpArmed ? " with an Advance in flight" : "")
                + "; any grab it held died with the PhysBoneManager and its frame count is void";

            // The durable channel is this tool's own next answer, not the console. Measured: a recovery
            // logged from the first delayCall after a domain load lands inside the reload's console clear
            // and the agent sees nothing at all — and a silent recovery is worse than none, because the
            // caller goes on believing its Advance completed. Held() reports this whatever the Console
            // window is set to; the log below is the same news, best-effort, for a human watching.
            _pumpResult = report;
            EditorApplication.delayCall += () => Debug.LogError(Tag + " Recover " + report + " => FAIL");
        }

        // [InitializeOnLoadMethod] on a method, NOT [InitializeOnLoad] on a nested holder class: measured,
        // Unity does not run the static constructor of a private nested type marked [InitializeOnLoad], so
        // the first draft's recovery never fired — a seeded record survived a real domain reload with the
        // timestep left pinned, which is precisely the end state this whole section exists to prevent.
        // A method attribute is discovered wherever the method lives.
        //
        // delayCall rather than the body: restoring pause and the time step during domain load runs before
        // the editor is ready to take either.
        [InitializeOnLoadMethod]
        private static void ScheduleRecovery() => EditorApplication.delayCall += RecoverAfterDomainReload;

        // ── Shared plumbing ───────────────────────────────────────────────────────────────────────

        private const string NoManager =
            "PhysBoneManager.Inst is null — this door is play-mode only, and av3emu also destroys the "
            + "manager on compilationStarted, so a grab from before a recompile is already gone";

        private static string NotPlaying(string door) =>
            door + " is play-mode only: PhysBoneManager.Inst is null in edit mode, so there is nothing to "
            + "grab and nothing to step (docs/emulator.md §Induce a physbone grab/pose)";

        private static string PumpInFlight(string door) =>
            door + " refused: an Advance is in flight. Two pumps double-step, and a target change at an "
            + "unspecified frame is not a primitive anyone can reason about — poll GrabPhysBone.Held() "
            + "until advance=idle";

        private static string StaleHandle(string handle) =>
            "handle '" + handle + "' names no live grab — it was released, or a recompile re-registered "
            + "the chain and minted a new id; GrabPhysBone.Held() lists the live set";

        private static string ForeignHandle(string handle) =>
            "handle '" + handle + "' is a grab this tool did not mint — the SDK's PhysBoneGrabHelper holds "
            + "one while the operator has the mouse down, and taking it would leave that owner writing to "
            + "a dead grab every frame";

        private static string GuardMutating()
        {
            if (!Application.isPlaying) return NotPlaying("This door");
            if (_pump != null) return PumpInFlight("This door");
            return null;
        }

        /// <summary>The shared grab tail: attempt, seed the target in the SAME call, record ownership.
        /// A call that grabs and then throws would leave an origin-grab live with the chain pinned at
        /// 0,0,0, so the seed and the bookkeeping are inside one try with a release on the way out.</summary>
        private static string Attempt(
            Func<PhysBoneManager.Grab> attempt, Func<Vector3> seedPoint,
            string label, string subject, string nullMessage)
        {
            bool frozeHere = !_frozen;
            Freeze();

            PhysBoneManager.Grab g = null;
            try
            {
                g = attempt();
                if (g == null)
                {
                    if (frozeHere) Thaw();
                    return Fail(nullMessage);
                }

                // Same call, always: GlobalPosition defaults to the origin, so a grab that returns without
                // one snaps its chain to 0,0,0. LocalOffset matches PhysBoneGrabHelper's `hit + LocalOffset`
                // convention, which Move also uses.
                g.GlobalPosition = seedPoint() + g.LocalOffset;

                var handle = FormatHandle(g.chainId);
                Owned.Add(handle);
                PersistRecord();
                return Ok(label, subject,
                    "handle=" + handle + " bone=" + g.bone + " grabber=" + g.playerId
                    + " localOffset=" + g.LocalOffset.ToString("F4")
                    + " held=" + Owned.Count + " | " + FreezeNote());
            }
            catch (Exception)
            {
                if (g != null)
                {
                    var mgr = PhysBoneManager.Inst;
                    if (mgr != null) mgr.ReleaseGrab(g.chainId);
                    Owned.Remove(FormatHandle(g.chainId));
                }
                if (frozeHere) Thaw();
                PersistRecord();
                throw;
            }
        }

        private static void Freeze()
        {
            if (_frozen) return;
            _priorPaused = EditorApplication.isPaused;
            EditorApplication.isPaused = true;
            _frozen = true;
            PersistRecord();
        }

        private static string Thaw()
        {
            EditorApplication.isPaused = _priorPaused;
            _frozen = false;
            PersistRecord();
            return "venue resumed (isPaused=" + _priorPaused + ")";
        }

        private static string FreezeNote() =>
            _frozen
                ? "venue FROZEN by this tool — GrabPhysBone.Release(resume: true) hands it back"
                : "venue not frozen";

        private static string Names(string label, List<string> items) =>
            label + "=" + (items.Count == 0 ? "none" : "[" + string.Join(",", items.ToArray()) + "]");

        private static PhysBoneManager.Grab FindLive(PhysBoneManager mgr, string handle)
        {
            ulong a, b;
            if (!TryParseHandle(handle, out a, out b)) return null;
            foreach (var g in new List<PhysBoneManager.Grab>(mgr.GetGrabs()))
                if (g.chainId.A == a && g.chainId.B == b) return g;
            return null;
        }

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

        // ── Scene resolver (path → instance id → name; mirrors CheckAvatar.Resolve, kept local) ────

        private static GameObject Resolve(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var byPath = FindByHierarchyPath(target);
            if (byPath != null) return byPath;

            int id;
            if (int.TryParse(target.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                var go = obj as GameObject;
                if (go != null) return go;
                var comp = obj as Component;
                if (comp != null) return comp.gameObject;
            }

            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                var hit = FindByNameRecursive(root.transform, target);
                if (hit != null) return hit.gameObject;
            }
            return null;
        }

        private static GameObject FindByHierarchyPath(string path)
        {
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

        private static Transform FindByNameRecursive(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var hit = FindByNameRecursive(t.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
