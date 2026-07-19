using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The <b>play-mode</b> front-end of RenderThumbnail: renders what players actually see — hair/cloth
    /// SETTLED by the real physbone solver and FX toggles/materials RESOLVED — sharing
    /// <see cref="RenderThumbnailCore"/>'s camera solve and capture core with the edit-mode
    /// <see cref="RenderThumbnail"/>. Play mode is strictly opt-in and additive; the edit path is the
    /// unchanged default.
    ///
    /// <para><b>Why physbones need play.</b> Their solver is a Burst job driven only by the player loop; it
    /// never ticks in edit mode or in an off-player-loop preview scene, so settle requires PLAY into a real
    /// loaded scene. Settle spans ~60–90 player-loop frames that advance only BETWEEN editor ticks, so no
    /// single synchronous call can settle-then-capture.</para>
    ///
    /// <para><b>Lifecycle — the caller drives the play toggle around three tool calls:</b>
    /// <c>Begin(target)</c> (edit mode: snapshot the scene setup, open/ready the venue, enable the emulator,
    /// isolate to one active avatar) → caller enters play (the SDK build-on-play runs, spawning the runtimes)
    /// → <c>Shoot(…)</c> (play mode: attaches to the built local avatar on first call, then
    /// poses/settles/composes/captures ASYNCHRONOUSLY, returning a tag the caller polls; one Begin serves many
    /// Shoots) → caller exits play → <c>End()</c> (restore the scene setup from disk). Play entry/exit is left
    /// to the caller (<c>manage_editor play/stop</c>) because <c>EditorApplication.isPlaying</c> toggles only
    /// on the next editor tick — a tool method cannot both flip it and observe the result.</para>
    ///
    /// <para><b>Playable-graph discipline.</b> A Shoot poses on the Base playable and composes the expression
    /// into the FX playable by <em>swapping</em> mixer inputs. The emulator's ORIGINAL Base/FX playables are
    /// cached at attach and never destroyed — a slot a shot does not override is reconnected to its original,
    /// and only the tool's own inserted playables are destroyed. Without this, a second shot that drops a pose
    /// or expression would render off a destroyed playable while the verdict claimed the prior state.</para>
    ///
    /// <para><b>Reproducibility is explicitly NOT a goal</b> (every thumbnail is a fresh avatar version).
    /// Settle is never asserted — the verdict NAMES any chain still moving at capture (refusal over silent
    /// cleverness). All emulator access is reflection into <c>LyumaAv3Runtime</c> privates (the emulator is an
    /// optional dependency this package must not reference); <see cref="Begin"/> asserts every member the tool
    /// actually reads is present and FAILS LOUD, named, on drift.</para>
    ///
    /// <para><b>Venue.</b> The avatar is rendered in a loaded scene (the active scene, or one opened via
    /// <c>scenePath</c>) — lit by THAT scene's lights (operator-customizable, unlike edit mode's fixed rig),
    /// backdrop from the camera clear. On <see cref="End"/> the whole scene setup is restored from disk,
    /// discarding the emulator control, deactivations, pose, and play-mode edits (mandatory when
    /// Enter-Play-Mode scene reload is disabled). <see cref="Begin"/> refuses if any loaded scene has unsaved
    /// edits, since that restore would lose them.</para>
    /// </summary>
    [AgentTool]
    public static class RenderThumbnailPlay
    {
        // ~60-90 frames at the solver's fixed 60 Hz; hair/skirt/ribbons converge, underdamped props may not.
        private const int DefaultSettleFrames = 90;
        // A chain that moved more than this (world metres, mean of its bones) between the last two settle ticks
        // is reported "still moving" at capture. ~0.3 mm — below it is solver noise; above it the operator sees
        // named chains and decides. NOT a settle assertion — the shot is taken regardless.
        private const float MovingLeafDeltaM = 0.0003f;
        private const int CaptureW = 1200, CaptureH = 900;

        /// <summary>One mixer slot: the emulator's ORIGINAL playable (cached, never destroyed) and its input
        /// weight, plus whatever the tool has currently inserted there (destroyed when replaced).</summary>
        private struct SlotState
        {
            public AnimatorControllerPlayable orig;
            public float origWeight;
            public AnimatorControllerPlayable inserted;
            public bool hasInserted;
        }

        // ===== Session state (survives across MCP calls: no domain reload occurs while we stay in play) =====
        private static bool _prepared;             // Begin ran (edit-mode prep done)
        private static bool _attached;             // isolated to the built local avatar (post-play-entry)
        private static string _targetName;
        private static SceneSetup[] _sceneSetup;   // snapshot at Begin; restored from disk on End
        private static GameObject _root;           // the built LOCAL avatar
        private static int _localLayer;            // cull target — the local runtime's actual layer (name may not resolve)
        private static object _localRuntime;       // LyumaAv3Runtime (reflected)
        private static RuntimeAnimatorController _origFxController; // clone source for the expression override (may be null)
        private static int _fxIndex = -1;
        private static SlotState _base, _fx;        // the two slots the tool swaps

        // Neutral head baseline, captured ONCE at attach (pre-pose idle) — Core.HeadFacing takes the delta, so
        // re-sampling per shot would measure the new pose against the prior shot's still-posed head.
        private static Transform _headBone;
        private static Quaternion _restHeadLocal;
        private static Vector3 _viewInHead;
        private static float _viewPositionY;

        // Blendshape bindings any expression drove this session, keyed "path|blendShape.Name". A composed
        // override with WriteDefaults off leaves its shapes stuck on the renderer after the override is
        // removed, so a later shot must drive the now-unused ones back to neutral (0). Reset once, then
        // dropped — a held-0 shape needs no further reset. Cleared per attach.
        private static Dictionary<string, EditorCurveBinding> _touchedExpr = new Dictionary<string, EditorCurveBinding>();

        // In-memory controllers the current shot inserted (destroyed before the next shot / on End — HideAndDontSave
        // objects survive play exit under DisableDomainReload, so teardown must destroy them explicitly).
        private static readonly List<UnityEngine.Object> _shootDisposables = new List<UnityEngine.Object>();
        private static EditorApplication.CallbackFunction _shootUpdate; // non-null while a Shoot is in flight
        private static string _lastShootResult = "(no shot yet)";

        // ===== Reflected LyumaAv3Runtime members — ONLY those the tool reads (asserted in Begin) =====
        private static Type _runtimeType, _emulatorType;
        private static FieldInfo _fIsLocal, _fPlayableMixer, _fPlayables, _fFxIndex;

        /// <summary>
        /// EDIT-MODE prep for a play session: snapshot the scene setup, open the venue (if
        /// <paramref name="scenePath"/> given), resolve <paramref name="target"/>, assert the Enter-Play-Mode
        /// options and the emulator's reflected members, enable the emulator, and isolate to one active avatar
        /// (PlayGate's precondition). Does NOT enter play — after this returns READY, the caller enters play
        /// (<c>manage_editor play</c>) so the SDK builds the avatar, then calls <see cref="Shoot"/>.
        /// </summary>
        /// <param name="target">avatar root: hierarchy path, instance id, or name — resolved in the venue scene.</param>
        /// <param name="scenePath">optional venue scene to open (Single) first; null =&gt; use the active scene.</param>
        public static string Begin(string target, string scenePath = null)
        {
            if (_prepared) return Fail("a play session is already prepared — call End() first (renders are serialized)");
            if (Application.isPlaying) return Fail("already in play mode — exit play (manage_editor stop) before Begin()");

            // Enter-Play-Mode options: the frame-driven Shoot state machine and this cross-call session state
            // both depend on the domain NOT reloading on play entry, and the restore-from-disk cleanup on
            // scene-reload being disabled. Assert both; name the fix rather than silently misbehave.
            if (!EditorSettings.enterPlayModeOptionsEnabled
                || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0
                || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableSceneReload) == 0)
                return Fail("Enter-Play-Mode Options must be enabled with BOTH 'Reload Domain' and 'Reload Scene' "
                    + "DISABLED (Project Settings > Editor > Enter Play Mode Settings) — the session survives across "
                    + "calls and restores the scene setup from disk only under those; current="
                    + (EditorSettings.enterPlayModeOptionsEnabled ? EditorSettings.enterPlayModeOptions.ToString() : "disabled"));

            if (!ResolveEmulatorReflection(out string driftErr)) return Fail(driftErr);

            // End restores the scene setup by reopening from disk — so any unsaved edit in a loaded scene would
            // be lost. Refuse loud rather than discard the operator's work; also guarantees every scene in the
            // snapshot has a disk path to restore.
            var unsaved = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isDirty || string.IsNullOrEmpty(s.path))
                    unsaved.Add(string.IsNullOrEmpty(s.name) ? "<untitled>" : s.name);
            }
            if (unsaved.Count > 0)
                return Fail("unsaved/unsaved-to-disk scene(s) loaded: [" + string.Join(",", unsaved) + "] — save or "
                    + "close them first; End() restores the scene setup from disk and would lose the edits");

            _sceneSetup = EditorSceneManager.GetSceneManagerSetup();

            if (scenePath != null)
            {
                if (!System.IO.File.Exists(scenePath)) { AbortBegin(); return Fail("scenePath '" + scenePath + "' does not exist"); }
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            }

            var venue = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(venue.path)) { AbortBegin(); return Fail("the venue scene is unsaved (no disk path) — save it or pass a scenePath"); }

            var target0 = RenderThumbnailCore.Resolve(target);
            if (target0 == null) { AbortBegin(); return Fail("target '" + target + "' not found in the venue scene"); }
            var descriptor = target0.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null) { AbortBegin(); return Fail("no VRCAvatarDescriptor on '" + target0.name + "'"); }

            // PlayGate wants exactly one active avatar. Deactivate every OTHER active avatar in the loaded
            // scenes (restored by the End setup-restore). The target itself must be active to build.
            var deactivated = new List<string>();
            foreach (var other in UnityEngine.Object.FindObjectsOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>())
            {
                if (other == descriptor) continue;
                if (other.gameObject.activeInHierarchy) { other.gameObject.SetActive(false); deactivated.Add(other.name); }
            }
            if (!target0.activeInHierarchy) target0.SetActive(true);

            bool emuMade = EnsureEmulatorEnabled(venue);

            _targetName = target0.name;
            _prepared = true;
            _attached = false;
            _shootUpdate = null;
            _lastShootResult = "(no shot yet)";

            return Ok("Begin " + _targetName + " => READY-TO-PLAY"
                + (emuMade ? " emulator=created/enabled" : " emulator=present")
                + (deactivated.Count > 0 ? " deactivated=[" + string.Join(",", deactivated) + "]" : "")
                + " — enter play (manage_editor play), then Shoot(...)");
        }

        // Undo a Begin that fails after the scene setup was snapshotted (restore the operator's scenes).
        private static void AbortBegin()
        {
            try { if (_sceneSetup != null && _sceneSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(_sceneSetup); }
            catch { /* best-effort restore on an already-broken failure path */ }
            _sceneSetup = null;
        }

        /// <summary>
        /// Pose, settle, compose the expression, and capture — asynchronously across editor ticks (play mode).
        /// On the first call it attaches to the built local avatar (isolate by IsLocal, cull-guard). Returns
        /// immediately with <c>STARTED tag=&lt;t&gt;</c>; when the settle completes it logs (and stores for
        /// <see cref="Status"/>) the verdict line <c>[RenderThumbnailPlay] &lt;tag&gt; Shoot … =&gt; OK | png=… |
        /// moving=[…]</c>. Poll <c>read_console</c> on the tag, or call <see cref="Status"/>.
        /// </summary>
        /// <param name="pose">null =&gt; the emulator idle (a moving pose — prefer a clip); a bundled name or a
        /// clip asset path/GUID (resolved identically to edit mode).</param>
        /// <param name="expression">null =&gt; none; else an FX state name or a clip asset path/GUID, composed as
        /// a full-weight FX override so the avatar's own toggles keep running and the expression wins.</param>
        /// <param name="framing">"bust" | "half" | "full".</param>
        /// <param name="bg">null =&gt; default backdrop; "#RRGGBB" solid; "#TOP:#BOTTOM" gradient.</param>
        /// <param name="fov">vertical FOV degrees (10–90).</param>
        /// <param name="yaw">null =&gt; automatic oblique; else an offset added to head tracking.</param>
        /// <param name="settleFrames">player-loop frames to settle before capture (default 90).</param>
        public static string Shoot(string pose = null, string expression = null, string framing = "bust",
            string bg = null, float fov = 30f, float? yaw = null, int settleFrames = DefaultSettleFrames)
        {
            if (!_prepared) return Fail("no prepared session — call Begin(target) first");
            if (!Application.isPlaying) return Fail("not in play mode — enter play (manage_editor play) after Begin(), then Shoot()");
            if (_shootUpdate != null) return Fail("a Shoot is already in flight — poll its tag / Status() until it completes (renders are serialized)");
            // Re-attach when the built avatar is gone (a prior play exit destroyed it while the session persisted).
            if (!_attached || _root == null) { if (!Attach(out string attachErr)) return Fail(attachErr); }

            // Validate framing/bg/fov up front (mirrors edit mode's fail-fast).
            float span, aimDrop;
            try { RenderThumbnailCore.FramingGeometry(framing, out span, out aimDrop); }
            catch (ArgumentException ex) { return Fail(ex.Message); }
            string framingToken = (framing ?? "bust").Trim().ToLowerInvariant();
            Color bgTop = RenderThumbnailCore.DefaultBackground, bgBottom = RenderThumbnailCore.DefaultBackground;
            if (bg != null && !RenderThumbnailCore.TryParseBg(bg, out bgTop, out bgBottom))
                return Fail("unparseable bg '" + bg + "' — expected #RRGGBB, #RRGGBBAA, or #TOP:#BOTTOM");
            if (!(fov >= RenderThumbnailCore.MinFov && fov <= RenderThumbnailCore.MaxFov))
                return Fail("fov " + fov.ToString(CultureInfo.InvariantCulture) + " out of range 10–90");
            if (yaw.HasValue && (float.IsNaN(yaw.Value) || float.IsInfinity(yaw.Value)))
                return Fail("yaw must be finite, or null for the automatic oblique");

            if (!RenderThumbnailCore.ResolvePose(pose, out AnimationClip poseClip, out string poseErr))
                return Fail(poseErr);

            AnimationClip exprClip = null;
            if (!string.IsNullOrEmpty(expression))
            {
                exprClip = RenderThumbnailCore.FindExpressionClip(_root, expression, out string exprErr);
                if (exprClip == null) return Fail(exprErr);
                if (_origFxController == null)
                    return Fail("no FX controller on the built avatar — cannot compose an expression; take an "
                        + "expression-free play thumbnail, or use edit mode");
                if (!(_origFxController is UnityEditor.Animations.AnimatorController))
                    return Fail("the FX layer holds a " + _origFxController.GetType().Name + " (an override controller), "
                        + "which the play-mode expression compositor cannot clone — take this expression in edit mode");
                if (_fxIndex < 0)
                    return Fail("no FX playable slot on the runtime — cannot compose the expression");
                // Parity with edit mode's zero-binding guard: an expression that resolves no blendshape on this
                // avatar renders an unchanged face under a verdict that claims it. Fail loud instead.
                if (!ExpressionBindsAny(_root, exprClip))
                    return Fail("expression '" + expression + "' (clip '" + exprClip.name + "') moves no blendshape on "
                        + "the built avatar — it binds shapes this avatar lacks, or only meshes that are not drawn");
            }
            string poseName = string.IsNullOrEmpty(pose) ? "idle" : pose;
            string expressionName = string.IsNullOrEmpty(expression) ? "none" : expression;

            var descriptor = _root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            Vector3 viewpoint = _root.transform.TransformPoint(descriptor.ViewPosition);

            // --- Build this shot's playables, then swap them in; the emulator originals are preserved. New
            // controllers go to a LOCAL list, adopted only after the prior shot's playables are destroyed. ---
            var mixer = (AnimationLayerMixerPlayable)_fPlayableMixer.GetValue(_localRuntime);
            var graph = PlayableExtensions.GetGraph(mixer);
            var playables = (System.Collections.IList)_fPlayables.GetValue(_localRuntime);
            var newDisp = new List<UnityEngine.Object>();

            AnimatorControllerPlayable? posePlayable = poseClip != null
                ? WrapClip(poseClip, "__rtp_pose", graph, newDisp) : (AnimatorControllerPlayable?)null;
            // The FX override clip is this shot's expression PLUS reset-to-0 for any shape a prior shot drove
            // that this one doesn't (null when there is no expression and nothing to clear — then the FX slot
            // returns to the emulator original). Only reachable with a clonable FX after the expression checks.
            AnimationClip fxClip = BuildExpressionClip(exprClip, newDisp);
            AnimatorControllerPlayable? exprPlayable = (fxClip != null && _fxIndex >= 0 && _origFxController is UnityEditor.Animations.AnimatorController)
                ? ComposeFxOverride(fxClip, graph, newDisp) : (AnimatorControllerPlayable?)null;

            SetSlot(graph, mixer, playables, 0, ref _base, posePlayable);       // Base: pose, or restore original
            if (_fxIndex >= 0 && (exprPlayable.HasValue || _fx.hasInserted))    // FX: compose, or restore original
                SetSlot(graph, mixer, playables, _fxIndex, ref _fx, exprPlayable);

            // Prior shot's inserted playables are now destroyed (by SetSlot); destroy its controllers, adopt this shot's.
            DisposeShootObjects();
            _shootDisposables.AddRange(newDisp);

            // --- Physbone chains to watch for the moving-chains verdict ---
            var chains = CollectChains(_root);

            // --- Frame-driven state machine: settle N frames, then solve + capture + log. ---
            string tag = "RTP-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            int injectFrame = Time.frameCount;
            var prevLeaf = SampleLeaves(chains);
            var stillMoving = new List<string>();

            EditorApplication.CallbackFunction step = null;
            step = () =>
            {
                try
                {
                    if (!Application.isPlaying) { FinishShoot(step, tag, "[RenderThumbnailPlay] " + tag + " Shoot => FAIL: play exited mid-settle"); return; }
                    int elapsed = Time.frameCount - injectFrame;

                    // Measure per-chain motion each tick; the "still moving" set is recomputed from the LAST
                    // interval, so it reflects convergence at capture, not accumulated history.
                    var nowLeaf = SampleLeaves(chains);
                    stillMoving.Clear();
                    for (int i = 0; i < chains.Count; i++)
                        if ((nowLeaf[i] - prevLeaf[i]).magnitude > MovingLeafDeltaM) stillMoving.Add(chains[i].name);
                    prevLeaf = nowLeaf;

                    if (elapsed < settleFrames) return; // keep settling

                    // ---- capture ----
                    float headYaw = 0f, headPitch = 0f;
                    Vector3 vp = viewpoint;
                    if (_headBone != null)
                    {
                        vp = _headBone.TransformPoint(_viewInHead); // follow the head through the pose
                        RenderThumbnailCore.HeadFacing(_root.transform.rotation, _headBone, _restHeadLocal, out headYaw, out headPitch);
                    }
                    var sol = RenderThumbnailCore.SolveCamera(framingToken, span, aimDrop, fov, yaw, headYaw, headPitch,
                        vp, _root.transform.rotation, _root.transform.lossyScale.y, _viewPositionY, _root.transform.eulerAngles.y);

                    var camGO = EditorUtility.CreateGameObjectWithHideFlags("__rtp_cam", HideFlags.DontSave, typeof(Camera));
                    SceneManager.MoveGameObjectToScene(camGO, _root.scene);
                    var cam = camGO.GetComponent<Camera>();
                    cam.enabled = false;
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = bgTop;
                    cam.nearClipPlane = 0.01f;
                    cam.farClipPlane = 100f;
                    cam.cullingMask = 1 << _localLayer; // ONLY the local avatar's layer — a co-located clone draws over it otherwise
                    cam.allowHDR = false;
                    cam.fieldOfView = fov;
                    cam.renderingPath = RenderingPath.Forward;
                    cam.transform.position = sol.Position;
                    cam.transform.rotation = sol.Rotation;

                    RenderThumbnailCore.CaptureResult cap;
                    try { cap = RenderThumbnailCore.Capture(cam, bgTop, bgBottom, vp, CaptureW, CaptureH); }
                    finally { UnityEngine.Object.DestroyImmediate(camGO); }

                    string movingToken = stillMoving.Count == 0 ? "none"
                        : "[" + string.Join(",", stillMoving.Take(12)) + (stillMoving.Count > 12 ? ",+" + (stillMoving.Count - 12) : "") + "]";
                    string head = "head=(" + cap.HeadViewport.x.ToString("0.00", CultureInfo.InvariantCulture)
                        + "," + cap.HeadViewport.y.ToString("0.00", CultureInfo.InvariantCulture) + ")";
                    string common = "Shoot " + _root.name + " pose=" + poseName + " expression=" + expressionName
                        + " framing=" + framingToken + " fov=" + fov.ToString("0.#", CultureInfo.InvariantCulture)
                        + " headYaw=" + headYaw.ToString("0.#", CultureInfo.InvariantCulture)
                        + " camYaw=" + sol.CamYaw.ToString("0.#", CultureInfo.InvariantCulture)
                        + " " + head + " settled=" + elapsed + "f moving=" + movingToken;

                    string verdict;
                    if (cap.Drawn == 0)
                        verdict = "[RenderThumbnailPlay] " + tag + " " + common + " => FAIL: nothing drew (every pixel "
                            + "matches its row background) — check the local-layer cull, or the subject fills the frame";
                    else
                    {
                        string png = System.IO.Path.Combine(Application.temporaryCachePath,
                            "renderthumbnailplay_" + RunLogFormat.Sanitize(_root.name) + "_" + tag.Substring(4) + ".png");
                        System.IO.File.WriteAllBytes(png, cap.Png);
                        verdict = "[RenderThumbnailPlay] " + tag + " " + common + " => OK | png=" + png;
                    }
                    FinishShoot(step, tag, verdict);
                }
                catch (Exception ex)
                {
                    FinishShoot(step, tag, "[RenderThumbnailPlay] " + tag + " Shoot => FAIL: " + ex.GetType().Name + ": " + ex.Message);
                }
            };

            _shootUpdate = step;
            EditorApplication.update += step;
            return Ok("Shoot STARTED tag=" + tag + " — settling " + settleFrames + " frames; poll read_console on '"
                + tag + "' (or Status()) for the verdict");
        }

        /// <summary>The last completed Shoot's verdict line (the same text logged with its tag), or a
        /// still-settling note. Convenience alternative to polling <c>read_console</c> on the tag.</summary>
        public static string Status()
        {
            if (!_prepared) return "[RenderThumbnailPlay] Status => no prepared session";
            if (_shootUpdate != null) return "[RenderThumbnailPlay] Status => a Shoot is still settling";
            return _lastShootResult;
        }

        /// <summary>
        /// EDIT-MODE teardown: restore the scene setup from disk (discarding the emulator control,
        /// deactivations, pose, and every play-mode edit) and destroy the session's in-memory controllers.
        /// Call AFTER exiting play (<c>manage_editor stop</c>).
        /// </summary>
        public static string End()
        {
            if (!_prepared) return Fail("no prepared session");
            if (Application.isPlaying) return Fail("still in play mode — exit play (manage_editor stop) before End()");
            if (_shootUpdate != null) { EditorApplication.update -= _shootUpdate; _shootUpdate = null; }
            DisposeShootObjects();

            string restored = "";
            try
            {
                if (_sceneSetup != null && _sceneSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(_sceneSetup);
                    restored = " restored=" + string.Join(",", _sceneSetup.Where(s => s != null).Select(s => System.IO.Path.GetFileNameWithoutExtension(s.path)));
                }
            }
            finally
            {
                _prepared = false; _attached = false; _root = null; _localRuntime = null; _origFxController = null;
                _sceneSetup = null; _targetName = null; _fxIndex = -1; _base = default; _fx = default;
            }
            return Ok("End => OK — scene setup" + (restored.Length > 0 ? restored : " had nothing to restore"));
        }

        // ===== Attach: isolate to the built local avatar (first Shoot, in play) =====

        private static bool Attach(out string err)
        {
            err = null;
            // A prior session's in-memory controllers survive play exit under DisableDomainReload; clear them.
            DisposeShootObjects();
            _base = default; _fx = default; _attached = false;
            _touchedExpr.Clear();

            _localRuntime = FindLocalRuntime();
            if (_localRuntime == null)
            { err = "no local LyumaAv3Runtime — the emulator control may be disabled, or the build spawned no runtimes (read the console)"; return false; }
            _root = ((Component)_localRuntime).gameObject;
            _localLayer = _root.layer;

            // Guard the cull: the local must sit on a layer no clone shares, else 1<<layer draws a clone over it.
            foreach (var rt in UnityEngine.Object.FindObjectsOfType(_runtimeType))
            {
                if (ReferenceEquals(rt, _localRuntime)) continue;
                if (((Component)rt).gameObject.layer == _localLayer)
                { err = "local avatar shares layer " + _localLayer + " with a clone runtime — cannot cull cleanly; the emulator's layer separation drifted"; return false; }
            }

            // Cache the emulator's ORIGINAL Base/FX playables + weights (never destroyed). FX may be absent
            // (a bare avatar) — expression shots fail loud in Shoot; pose/settle/capture need no FX.
            var mixer = (AnimationLayerMixerPlayable)_fPlayableMixer.GetValue(_localRuntime);
            var playables = (System.Collections.IList)_fPlayables.GetValue(_localRuntime);
            _base = new SlotState { orig = (AnimatorControllerPlayable)playables[0], origWeight = PlayableExtensions.GetInputWeight(mixer, 1) };
            _fxIndex = (int)_fFxIndex.GetValue(_localRuntime);
            if (_fxIndex >= 0 && _fxIndex < playables.Count)
                _fx = new SlotState { orig = (AnimatorControllerPlayable)playables[_fxIndex], origWeight = PlayableExtensions.GetInputWeight(mixer, _fxIndex + 1) };
            _origFxController = GetFxController(_localRuntime); // may be null — only expression shots need it

            // Neutral head baseline (pre-pose idle), captured ONCE.
            var animator = _root.GetComponent<Animator>();
            _headBone = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            var desc = _root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            _viewPositionY = desc.ViewPosition.y;
            Vector3 vp0 = _root.transform.TransformPoint(desc.ViewPosition);
            _restHeadLocal = _headBone != null ? Quaternion.Inverse(_root.transform.rotation) * _headBone.rotation : Quaternion.identity;
            _viewInHead = _headBone != null ? _headBone.InverseTransformPoint(vp0) : Vector3.zero;

            _attached = true;
            return true;
        }

        // ===== Playable-graph surgery =====

        /// <summary>Point a mixer slot at <paramref name="ours"/> (the tool's inserted playable) or, when null,
        /// back at the emulator's cached ORIGINAL. Destroys the slot's previously-inserted playable (never the
        /// original) and updates <c>playables[index]</c> so the runtime's per-frame param push targets whatever
        /// now occupies the slot.</summary>
        private static void SetSlot(PlayableGraph graph, AnimationLayerMixerPlayable mixer,
            System.Collections.IList playables, int index, ref SlotState slot, AnimatorControllerPlayable? ours)
        {
            int input = index + 1;
            graph.Disconnect(mixer, input);
            if (slot.hasInserted && ((Playable)slot.inserted).IsValid()) graph.DestroyPlayable((Playable)slot.inserted);

            var desired = ours ?? slot.orig;
            graph.Connect(desired, 0, mixer, input);
            PlayableExtensions.SetInputWeight(mixer, input, slot.origWeight); // honor the original weight (incl. 0)
            playables[index] = desired;

            slot.inserted = ours ?? default;
            slot.hasInserted = ours.HasValue;
        }

        /// <summary>Wrap a clip in a 1-state in-memory controller (tracked in <paramref name="sink"/>) and make
        /// a playable of it. A bare clip playable collapses the humanoid root; the controller wrapper holds it.</summary>
        private static AnimatorControllerPlayable WrapClip(AnimationClip clip, string name, PlayableGraph graph, List<UnityEngine.Object> sink)
        {
            var ctrl = new UnityEditor.Animations.AnimatorController { name = name, hideFlags = HideFlags.HideAndDontSave };
            var sm = new UnityEditor.Animations.AnimatorStateMachine { name = name + "_sm", hideFlags = HideFlags.HideAndDontSave };
            var st = sm.AddState("s"); st.motion = clip; st.writeDefaultValues = false; sm.defaultState = st;
            ctrl.AddLayer(new UnityEditor.Animations.AnimatorControllerLayer { name = "base", defaultWeight = 1f, stateMachine = sm });
            sink.Add(sm); sink.Add(ctrl);
            return AnimatorControllerPlayable.Create(graph, ctrl);
        }

        /// <summary>Clone the baked FX controller and append a full-weight OVERRIDE layer holding the
        /// expression clip — the composition proven to render while the avatar's own FX toggles keep running.
        /// Composing onto <see cref="_origFxController"/> (never a prior clone) keeps parameters identical, so
        /// the runtime's per-index param caches stay valid. Caller guarantees it is an AnimatorController.</summary>
        private static AnimatorControllerPlayable ComposeFxOverride(AnimationClip exprClip, PlayableGraph graph, List<UnityEngine.Object> sink)
        {
            var clone = UnityEngine.Object.Instantiate((UnityEditor.Animations.AnimatorController)_origFxController);
            clone.name = _origFxController.name + "_rtpcompose"; clone.hideFlags = HideFlags.HideAndDontSave;
            var sm = new UnityEditor.Animations.AnimatorStateMachine { name = "__rtp_expr", hideFlags = HideFlags.HideAndDontSave };
            var st = sm.AddState("expr"); st.motion = exprClip; st.writeDefaultValues = false; sm.defaultState = st;
            clone.AddLayer(new UnityEditor.Animations.AnimatorControllerLayer
            {
                name = "__rtp_expr_override", defaultWeight = 1f, stateMachine = sm,
                blendingMode = UnityEditor.Animations.AnimatorLayerBlendingMode.Override
            });
            sink.Add(sm); sink.Add(clone);
            return AnimatorControllerPlayable.Create(graph, clone);
        }

        /// <summary>
        /// The clip the FX override layer plays for this shot: <paramref name="src"/>'s blendShape curves,
        /// PLUS a reset-to-0 for every shape a prior shot's expression drove that <paramref name="src"/> does
        /// not (a WD-off override leaves its shapes stuck on the renderer, so an amortized re-shoot must drive
        /// the now-unused ones back). Returns null when there is no expression AND nothing to clear — the FX
        /// slot then returns to the emulator original. Reset target is 0, the neutral for expression shapes.
        /// </summary>
        private static AnimationClip BuildExpressionClip(AnimationClip src, List<UnityEngine.Object> sink)
        {
            var current = new Dictionary<string, EditorCurveBinding>();
            if (src != null)
                foreach (var b in AnimationUtility.GetCurveBindings(src))
                    if (b.propertyName != null && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                        current[b.path + "|" + b.propertyName] = b;

            var resets = _touchedExpr.Where(kv => !current.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
            _touchedExpr = current; // shapes driven now; the reset ones are held at 0 and need no further reset
            if (current.Count == 0 && resets.Count == 0) return null;

            var clip = new AnimationClip { name = "__rtp_exprclip", hideFlags = HideFlags.HideAndDontSave };
            if (src != null)
                foreach (var kv in current)
                {
                    var curve = AnimationUtility.GetEditorCurve(src, kv.Value);
                    if (curve != null) AnimationUtility.SetEditorCurve(clip, kv.Value, curve);
                }
            foreach (var b in resets)
                AnimationUtility.SetEditorCurve(clip, b, AnimationCurve.Constant(0f, 1f, 0f));
            sink.Add(clip);
            return clip;
        }

        /// <summary>Does <paramref name="clip"/> drive at least one blendshape that exists on a DRAWN renderer
        /// of <paramref name="root"/>? Mirrors edit mode's zero-binding guard (blendShape curves only).</summary>
        private static bool ExpressionBindsAny(GameObject root, AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.propertyName == null || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                Transform t = string.IsNullOrEmpty(binding.path) ? root.transform : root.transform.Find(binding.path);
                var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr == null || smr.sharedMesh == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                if (smr.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length)) >= 0) return true;
            }
            return false;
        }

        // ===== Settle measurement =====

        /// <summary>One physbone chain to watch: its name and the transforms whose motion signals it is
        /// still ringing.</summary>
        private struct Chain { public string name; public Transform[] bones; }

        private static List<Chain> CollectChains(GameObject root)
        {
            var chains = new List<Chain>();
            foreach (var pb in root.GetComponentsInChildren<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>(true))
            {
                var chainRoot = pb.rootTransform != null ? pb.rootTransform : pb.transform;
                if (chainRoot == null) continue;
                var bones = chainRoot.GetComponentsInChildren<Transform>(true);
                chains.Add(new Chain { name = chainRoot.name, bones = bones });
            }
            return chains;
        }

        /// <summary>Per-chain representative world position (mean of its bones) — differenced between ticks to
        /// gauge motion. Mean, not just the leaf, so a whole ringing chain registers even if the tip is short.</summary>
        private static Vector3[] SampleLeaves(List<Chain> chains)
        {
            var pos = new Vector3[chains.Count];
            for (int i = 0; i < chains.Count; i++)
            {
                var b = chains[i].bones;
                if (b == null || b.Length == 0) { pos[i] = Vector3.zero; continue; }
                Vector3 sum = Vector3.zero; int n = 0;
                foreach (var t in b) { if (t != null) { sum += t.position; n++; } }
                pos[i] = n > 0 ? sum / n : Vector3.zero;
            }
            return pos;
        }

        // ===== Teardown helpers =====

        private static void FinishShoot(EditorApplication.CallbackFunction step, string tag, string verdict)
        {
            EditorApplication.update -= step;
            if (ReferenceEquals(_shootUpdate, step)) _shootUpdate = null;
            _lastShootResult = verdict;
            if (verdict.Contains("=> FAIL")) Debug.LogError(verdict); else Debug.Log(verdict);
        }

        private static void DisposeShootObjects()
        {
            foreach (var o in _shootDisposables)
                if (o != null) UnityEngine.Object.DestroyImmediate(o);
            _shootDisposables.Clear();
        }

        // ===== Emulator reflection =====

        private static bool ResolveEmulatorReflection(out string err)
        {
            err = null;
            _runtimeType = _emulatorType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                {
                    if (t.FullName == "Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime") _runtimeType = t;
                    else if (t.FullName == "Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator") _emulatorType = t;
                }
            }
            if (_runtimeType == null || _emulatorType == null)
                { err = "the Av3 Emulator (Lyuma.Av3Emulator) is not present in this project — play-mode render needs it"; return false; }

            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var missing = new List<string>();
            _fIsLocal = Req(_runtimeType, "IsLocal", BF, missing);
            _fPlayableMixer = Req(_runtimeType, "playableMixer", BF, missing);
            _fPlayables = Req(_runtimeType, "playables", BF, missing);
            _fFxIndex = Req(_runtimeType, "fxIndex", BF, missing);
            if (missing.Count > 0)
                { err = "LyumaAv3Runtime drifted — missing member(s) the render reads: " + string.Join(", ", missing) + " (the emulator version is not the one this render targets)"; return false; }
            return true;
        }

        private static FieldInfo Req(Type t, string name, BindingFlags bf, List<string> missing)
        {
            var f = t.GetField(name, bf);
            if (f == null) missing.Add(name);
            return f;
        }

        private static object FindLocalRuntime()
        {
            foreach (var rt in UnityEngine.Object.FindObjectsOfType(_runtimeType))
                if ((bool)_fIsLocal.GetValue(rt)) return rt;
            return null;
        }

        private static RuntimeAnimatorController GetFxController(object runtime)
        {
            var desc = ((Component)runtime).GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            foreach (var l in desc.baseAnimationLayers)
                if (l.type == VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX && l.animatorController != null)
                    return l.animatorController;
            return null;
        }

        /// <summary>True if the tool created/enabled a control; false if an enabled one already existed. Scans
        /// ALL loaded scenes so an emulator owned by an additively-loaded scene isn't duplicated.</summary>
        private static bool EnsureEmulatorEnabled(Scene venue)
        {
            foreach (var e in UnityEngine.Object.FindObjectsOfType(_emulatorType, true))
            {
                var b = (UnityEngine.Behaviour)e;
                if (b.enabled && b.gameObject.activeInHierarchy) return false; // an enabled control exists in some loaded scene
            }
            foreach (var e in UnityEngine.Object.FindObjectsOfType(_emulatorType, true))
            {
                var b = (UnityEngine.Behaviour)e;
                if (b.gameObject.scene != venue) continue;
                b.enabled = true; b.gameObject.SetActive(true);
                return true;
            }
            EditorApplication.ExecuteMenuItem("Tools/Avatars 3.0 Emulator/Enable"); // creates one in the active (venue) scene
            return true;
        }

        private static string Ok(string body) { string m = "[RenderThumbnailPlay] " + body; Debug.Log(m); return m; }
        private static string Fail(string reason) { string m = "[RenderThumbnailPlay] => FAIL: " + reason; Debug.LogError(m); return m; }
    }
}
