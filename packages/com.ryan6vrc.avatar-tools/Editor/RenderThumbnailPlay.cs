using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
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
    /// <c>Begin(target)</c> (edit mode: open/ready the venue, enable the emulator, isolate to one active
    /// avatar) → caller enters play (the SDK build-on-play runs, spawning the runtimes) → <c>Shoot(…)</c>
    /// (play mode: attaches to the built local avatar on first call, then poses/settles/composes/captures
    /// ASYNCHRONOUSLY, returning a tag the caller polls; one Begin serves many Shoots) → caller exits play →
    /// <c>End()</c> (edit mode: reopen the venue scene from disk). Play entry/exit is left to the caller
    /// (<c>manage_editor play/stop</c>) because <c>EditorApplication.isPlaying</c> toggles only on the next
    /// editor tick — a tool method cannot both flip it and observe the result.</para>
    ///
    /// <para><b>Reproducibility is explicitly NOT a goal</b> (every thumbnail is a fresh avatar version).
    /// Settle is never asserted — the verdict NAMES any chain still moving at capture (refusal over silent
    /// cleverness). All emulator access is reflection into <c>LyumaAv3Runtime</c> privates (the emulator is an
    /// optional dependency this package must not reference); <see cref="Begin"/> asserts every reflected member
    /// is present and FAILS LOUD, named, on drift rather than silently rendering something wrong.</para>
    ///
    /// <para><b>Venue.</b> The avatar is rendered in a loaded scene (the active scene, or one opened via
    /// <c>scenePath</c>) — lit by THAT scene's lights (operator-customizable, unlike edit mode's fixed rig),
    /// backdrop from the camera clear. On <see cref="End"/> the venue scene is reopened from disk, discarding
    /// the emulator control, deactivations, pose, and play-mode edits (mandatory when Enter-Play-Mode scene
    /// reload is disabled — see <see cref="Begin"/>).</para>
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

        // ===== Session state (survives across MCP calls: no domain reload occurs while we stay in play) =====
        private static bool _prepared;             // Begin ran (edit-mode prep done)
        private static bool _attached;             // isolated to the built local avatar (post-play-entry)
        private static string _targetName;
        private static string _venueScenePath;     // reopened from disk on End
        private static string _preBeginScenePath;  // restored on End if Begin opened a different venue
        private static GameObject _root;           // the built LOCAL avatar
        private static int _localLayer;            // cull target — the local runtime's actual layer (name may not resolve)
        private static object _localRuntime;       // LyumaAv3Runtime (reflected)
        private static RuntimeAnimatorController _origFxController; // always compose onto THIS, never a prior clone

        // Per-Shoot disposables (destroyed before the next Shoot and on End — HideAndDontSave in-memory
        // objects survive play exit under DisableDomainReload, so teardown must destroy them explicitly).
        private static readonly List<UnityEngine.Object> _shootDisposables = new List<UnityEngine.Object>();
        private static EditorApplication.CallbackFunction _shootUpdate; // non-null while a Shoot is in flight
        private static string _lastShootResult = "(no shot yet)";

        // ===== Reflected LyumaAv3Runtime members (resolved + drift-checked in Begin) =====
        private static Type _runtimeType, _emulatorType, _boolParamType, _floatParamType;
        private static FieldInfo _fIsLocal, _fBools, _fFloats, _fPlayableMixer, _fPlayables, _fAllControllers, _fFxIndex;
        private static FieldInfo _fBoolName, _fBoolValue, _fFloatName, _fFloatValue, _fFloatExprValue;

        /// <summary>
        /// EDIT-MODE prep for a play session: open the venue (if <paramref name="scenePath"/> given), resolve
        /// <paramref name="target"/>, assert the Enter-Play-Mode options and the emulator's reflected members,
        /// enable the emulator, and isolate to one active avatar (PlayGate's precondition). Does NOT enter play
        /// — after this returns READY, the caller enters play (<c>manage_editor play</c>) so the SDK builds the
        /// avatar, then calls <see cref="Shoot"/>.
        /// </summary>
        /// <param name="target">avatar root: hierarchy path, instance id, or name — resolved in the venue scene.</param>
        /// <param name="scenePath">optional venue scene to open (Single) first; null =&gt; use the active scene.</param>
        public static string Begin(string target, string scenePath = null)
        {
            if (_prepared) return Fail("a play session is already prepared — call End() first (renders are serialized)");
            if (Application.isPlaying) return Fail("already in play mode — exit play (manage_editor stop) before Begin()");

            // Enter-Play-Mode options: the frame-driven Shoot state machine and this cross-call session state
            // both depend on the domain NOT reloading on play entry, and the reopen-from-disk cleanup on
            // scene-reload being disabled. Assert both; name the fix rather than silently misbehave.
            if (!EditorSettings.enterPlayModeOptionsEnabled
                || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0
                || (EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableSceneReload) == 0)
                return Fail("Enter-Play-Mode Options must be enabled with BOTH 'Reload Domain' and 'Reload Scene' "
                    + "DISABLED (Project Settings > Editor > Enter Play Mode Settings) — the session survives across "
                    + "calls and reopens the scene from disk only under those; current="
                    + (EditorSettings.enterPlayModeOptionsEnabled ? EditorSettings.enterPlayModeOptions.ToString() : "disabled"));

            if (!ResolveEmulatorReflection(out string driftErr)) return Fail(driftErr);

            if (scenePath != null)
            {
                if (!System.IO.File.Exists(scenePath))
                    return Fail("scenePath '" + scenePath + "' does not exist");
                _preBeginScenePath = SceneManager.GetActiveScene().path;
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath,
                    UnityEditor.SceneManagement.OpenSceneMode.Single);
            }
            else _preBeginScenePath = null;

            var venue = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(venue.path))
                return Fail("the venue scene is unsaved (no disk path) — save it or pass a scenePath; End() reopens it from disk");

            var target0 = RenderThumbnailCore.Resolve(target);
            if (target0 == null) return Fail("target '" + target + "' not found in the venue scene");
            var descriptor = target0.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null) return Fail("no VRCAvatarDescriptor on '" + target0.name + "'");

            // PlayGate wants exactly one active avatar. Deactivate every OTHER active avatar in the loaded
            // scenes (restored by the End reopen-from-disk). The target itself must be active to build.
            var deactivated = new List<string>();
            foreach (var other in UnityEngine.Object.FindObjectsOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>())
            {
                if (other == descriptor) continue;
                if (other.gameObject.activeInHierarchy) { other.gameObject.SetActive(false); deactivated.Add(other.name); }
            }
            if (!target0.activeInHierarchy) target0.SetActive(true);

            // The emulator does not auto-spawn; the venue needs an enabled control object. Create/enable it
            // (per-scene; the End reopen discards it if the on-disk scene never had one).
            bool emuMade = EnsureEmulatorEnabled(venue);

            _venueScenePath = venue.path;
            _targetName = target0.name;
            _prepared = true;
            _attached = false;
            _shootDisposables.Clear();
            _shootUpdate = null;
            _lastShootResult = "(no shot yet)";

            return Ok("Begin " + _targetName + " => READY-TO-PLAY"
                + (emuMade ? " emulator=created" : " emulator=present")
                + (deactivated.Count > 0 ? " deactivated=[" + string.Join(",", deactivated) + "]" : "")
                + " — enter play (manage_editor play), then Shoot(...)");
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
            if (!_attached && !Attach(out string attachErr)) return Fail(attachErr);

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
            }
            string poseName = string.IsNullOrEmpty(pose) ? "idle" : pose;
            string expressionName = string.IsNullOrEmpty(expression) ? "none" : expression;

            // Destroy the prior Shoot's composed/pose controllers before building this one (compose always onto
            // the ORIGINAL baked FX, never an accumulating clone).
            DisposeShootObjects();

            var animator = _root.GetComponent<Animator>();
            var descriptor = _root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            Vector3 viewpoint = _root.transform.TransformPoint(descriptor.ViewPosition);
            float viewPositionY = descriptor.ViewPosition.y;

            // --- Rest head reference: read BEFORE injecting the pose (idle-neutral). Core.HeadFacing takes the
            // delta, so the reference need not be the exact bind — idle faces forward, and the extraction is
            // rig-orientation-invariant either way. ---
            Transform headBone = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
            Quaternion restHeadLocal = headBone != null
                ? Quaternion.Inverse(_root.transform.rotation) * headBone.rotation : Quaternion.identity;
            Vector3 viewInHead = headBone != null ? headBone.InverseTransformPoint(viewpoint) : Vector3.zero;

            // --- Inject pose on the Base playable (input 1 = playables[0]); the graph wins over the runtime
            // controller. A bare AnimationClipPlayable collapses the humanoid root, so wrap in a 1-state
            // controller. Floor (no clip) leaves the emulator's own Base running. ---
            var mixer = (AnimationLayerMixerPlayable)_fPlayableMixer.GetValue(_localRuntime);
            var graph = PlayableExtensions.GetGraph(mixer);
            var playables = (System.Collections.IList)_fPlayables.GetValue(_localRuntime);
            if (poseClip != null)
                SwapPlayable(graph, mixer, playables, 0, WrapClip(poseClip, "__rtp_pose"));

            // --- Compose the expression as a full-weight FX override onto a clone of the baked FX, swapped in
            // as the FX playable (verified: the avatar's own toggles keep running because the clone's parameters
            // are unchanged, so the runtime's per-index param caches stay valid). ---
            if (exprClip != null)
            {
                int fxIndex = (int)_fFxIndex.GetValue(_localRuntime);
                SwapPlayable(graph, mixer, playables, fxIndex, ComposeFxOverride(exprClip));
            }

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
                    if (headBone != null)
                    {
                        vp = headBone.TransformPoint(viewInHead); // follow the head through the pose
                        RenderThumbnailCore.HeadFacing(_root.transform.rotation, headBone, restHeadLocal, out headYaw, out headPitch);
                    }
                    var sol = RenderThumbnailCore.SolveCamera(framingToken, span, aimDrop, fov, yaw, headYaw, headPitch,
                        vp, _root.transform.rotation, _root.transform.lossyScale.y, viewPositionY, _root.transform.eulerAngles.y);

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
        /// EDIT-MODE teardown: reopen the venue scene from disk (discarding the emulator control,
        /// deactivations, pose, and every play-mode edit) and destroy the session's in-memory controllers.
        /// Call AFTER exiting play (<c>manage_editor stop</c>).
        /// </summary>
        public static string End()
        {
            if (!_prepared) return Fail("no prepared session");
            if (Application.isPlaying) return Fail("still in play mode — exit play (manage_editor stop) before End()");
            if (_shootUpdate != null) { EditorApplication.update -= _shootUpdate; _shootUpdate = null; }
            DisposeShootObjects();

            string reopened = "";
            try
            {
                if (!string.IsNullOrEmpty(_venueScenePath) && System.IO.File.Exists(_venueScenePath))
                {
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(_venueScenePath,
                        UnityEditor.SceneManagement.OpenSceneMode.Single);
                    reopened = " reopened=" + _venueScenePath;
                    // Restore the operator's pre-Begin scene if Begin opened a different venue.
                    if (!string.IsNullOrEmpty(_preBeginScenePath) && _preBeginScenePath != _venueScenePath
                        && System.IO.File.Exists(_preBeginScenePath))
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(_preBeginScenePath,
                            UnityEditor.SceneManagement.OpenSceneMode.Single);
                        reopened += " restored=" + _preBeginScenePath;
                    }
                }
            }
            finally
            {
                _prepared = false; _attached = false; _root = null; _localRuntime = null; _origFxController = null;
                _venueScenePath = null; _preBeginScenePath = null; _targetName = null;
            }
            return Ok("End => OK — venue" + (reopened.Length > 0 ? reopened : " had no disk path to reopen"));
        }

        // ===== Attach: isolate to the built local avatar (first Shoot, in play) =====

        private static bool Attach(out string err)
        {
            err = null;
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

            _origFxController = GetFxController(_localRuntime);
            if (_origFxController == null)
            { err = "no FX controller on the built avatar — expression composition has nothing to compose onto"; return false; }

            _attached = true;
            return true;
        }

        // ===== Playable-graph surgery =====

        /// <summary>Disconnect mixer input (index+1), connect <paramref name="newPlayable"/> at the prior
        /// weight, update the runtime's <c>playables[index]</c> so its per-frame param push targets the new
        /// playable, and destroy the replaced one. Base = index 0 (input 1); FX = fxIndex (input fxIndex+1).</summary>
        private static void SwapPlayable(PlayableGraph graph, AnimationLayerMixerPlayable mixer,
            System.Collections.IList playables, int index, AnimatorControllerPlayable newPlayable)
        {
            int input = index + 1;
            float weight = PlayableExtensions.GetInputWeight(mixer, input);
            var old = (AnimatorControllerPlayable)playables[index];
            graph.Disconnect(mixer, input);
            graph.Connect(newPlayable, 0, mixer, input);
            PlayableExtensions.SetInputWeight(mixer, input, weight > 0f ? weight : 1f);
            // The runtime drives params by iterating playables[i] against per-index caches; updating this slot
            // is what keeps the composed clone's toggles driven. allControllers is NOT touched — it feeds only
            // the emulator's debug/view-only features, and its parameter set is unchanged by an added layer.
            playables[index] = newPlayable;
            graph.DestroyPlayable(old);
        }

        /// <summary>Wrap a clip in a 1-state in-memory controller (tracked for disposal) and make a playable
        /// of it. A bare clip playable collapses the humanoid root; the controller wrapper holds it.</summary>
        private static AnimatorControllerPlayable WrapClip(AnimationClip clip, string name)
        {
            var ctrl = new UnityEditor.Animations.AnimatorController { name = name, hideFlags = HideFlags.HideAndDontSave };
            var sm = new UnityEditor.Animations.AnimatorStateMachine { name = name + "_sm", hideFlags = HideFlags.HideAndDontSave };
            var st = sm.AddState("s"); st.motion = clip; st.writeDefaultValues = false; sm.defaultState = st;
            ctrl.AddLayer(new UnityEditor.Animations.AnimatorControllerLayer { name = "base", defaultWeight = 1f, stateMachine = sm });
            _shootDisposables.Add(sm); _shootDisposables.Add(ctrl);
            var graph = PlayableExtensions.GetGraph((AnimationLayerMixerPlayable)_fPlayableMixer.GetValue(_localRuntime));
            return AnimatorControllerPlayable.Create(graph, ctrl);
        }

        /// <summary>Clone the baked FX controller and append a full-weight OVERRIDE layer holding the
        /// expression clip — the composition proven to render while the avatar's own FX toggles keep running.
        /// Composing onto <see cref="_origFxController"/> (never the prior clone) keeps parameters identical, so
        /// the runtime's per-index param caches stay valid.</summary>
        private static AnimatorControllerPlayable ComposeFxOverride(AnimationClip exprClip)
        {
            var clone = UnityEngine.Object.Instantiate(_origFxController) as UnityEditor.Animations.AnimatorController;
            clone.name = _origFxController.name + "_rtpcompose"; clone.hideFlags = HideFlags.HideAndDontSave;
            var sm = new UnityEditor.Animations.AnimatorStateMachine { name = "__rtp_expr", hideFlags = HideFlags.HideAndDontSave };
            var st = sm.AddState("expr"); st.motion = exprClip; st.writeDefaultValues = false; sm.defaultState = st;
            clone.AddLayer(new UnityEditor.Animations.AnimatorControllerLayer
            {
                name = "__rtp_expr_override", defaultWeight = 1f, stateMachine = sm,
                blendingMode = UnityEditor.Animations.AnimatorLayerBlendingMode.Override
            });
            _shootDisposables.Add(sm); _shootDisposables.Add(clone);
            var graph = PlayableExtensions.GetGraph((AnimationLayerMixerPlayable)_fPlayableMixer.GetValue(_localRuntime));
            return AnimatorControllerPlayable.Create(graph, clone);
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
            _runtimeType = _emulatorType = _boolParamType = _floatParamType = null;
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
            _fBools = Req(_runtimeType, "Bools", BF, missing);
            _fFloats = Req(_runtimeType, "Floats", BF, missing);
            _fPlayableMixer = Req(_runtimeType, "playableMixer", BF, missing);
            _fPlayables = Req(_runtimeType, "playables", BF, missing);
            _fAllControllers = Req(_runtimeType, "allControllers", BF, missing);
            _fFxIndex = Req(_runtimeType, "fxIndex", BF, missing);

            // Parameter mirror element types (nested); resolve name/value accessors for FX drive.
            _boolParamType = _runtimeType.GetNestedType("BoolParam", BF) ?? FindNested("BoolParam");
            _floatParamType = _runtimeType.GetNestedType("FloatParam", BF) ?? FindNested("FloatParam");
            if (_boolParamType != null) { _fBoolName = _boolParamType.GetField("name", BF); _fBoolValue = _boolParamType.GetField("value", BF); }
            if (_floatParamType != null) { _fFloatName = _floatParamType.GetField("name", BF); _fFloatValue = _floatParamType.GetField("value", BF); _fFloatExprValue = _floatParamType.GetField("expressionValue", BF); }

            if (missing.Count > 0)
                { err = "LyumaAv3Runtime drifted — missing member(s): " + string.Join(", ", missing) + " (the emulator version is not the one this render targets)"; return false; }
            return true;
        }

        private static FieldInfo Req(Type t, string name, BindingFlags bf, List<string> missing)
        {
            var f = t.GetField(name, bf);
            if (f == null) missing.Add(name);
            return f;
        }

        private static Type FindNested(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types) if (t.Name == simpleName && t.FullName != null && t.FullName.StartsWith("Lyuma.")) return t;
            }
            return null;
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

        private static bool EnsureEmulatorEnabled(Scene venue)
        {
            foreach (var e in UnityEngine.Object.FindObjectsOfType(_emulatorType, true))
            {
                var b = (UnityEngine.Behaviour)e;
                if (b.gameObject.scene != venue) continue;
                if (b.enabled && b.gameObject.activeInHierarchy) return false; // present + enabled
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
