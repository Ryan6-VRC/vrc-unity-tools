using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Renders a baked-avatar thumbnail via a dedicated off-screen camera rather than RenderAvatar's
    /// Scene-View window-grab, because baking (the VRC SDK preprocess chain,
    /// <c>VRCBuildPipelineCallbacks.OnPreprocessAvatar</c>) gives real resolved meshes that a fixed-size
    /// off-screen camera can render at a guaranteed 1200×900 — RenderAvatar never bakes, so it must
    /// composite through the Scene View instead, capped to the pane's size and showing NDMF preview
    /// proxies. The bake is the FULL SDK chain, not NDMF alone, so the portrait shows the avatar that
    /// actually uploads (optimizers included). See docs/2026-07-17-render-thumbnail-design.md.
    /// </summary>
    [AgentTool]
    public static class RenderThumbnail
    {
        /// <summary>The bundled pose vocabulary is this folder's <c>RTPose_*.anim</c> glob — there is no
        /// hard-wired name list. Poses are added by dropping files here, and the unknown-pose error
        /// enumerates the glob, so the advertised vocabulary cannot drift from what actually ships.</summary>
        internal const string PosesFolder = "Packages/com.ryan6vrc.avatar-tools/Editor/Poses";

        // Framing = the SUBJECT HEIGHT the frame covers, in meters at ReferenceViewHeight; the camera
        // distance is solved from it and the FOV, so the two are independent knobs. AimDrop lowers the aim
        // below the view point as a fraction of that span: the anchor (the eyes) sits near the TOP of the
        // subject, not its center, so one coefficient cannot serve three spans.
        //
        // Spans are operator-judged taste, nudged ~7% wider than the first pass, which cropped the crown
        // tightly enough to read as an accident. Some crop is DESIRABLE — a thumbnail is displayed small,
        // and a tight one reads as deliberate — so these do not chase full crown clearance, which on tall
        // anime hair would cost the bust crop entirely. See docs/2026-07-18-render-thumbnail-camera.md.
        private const float ReferenceViewHeight = 1.6f;
        private const float BustSpan = 0.48f, BustAimDrop = 0.12f;
        private const float HalfSpan = 0.96f, HalfAimDrop = 0.12f;
        private const float FullSpan = 2.00f, FullAimDrop = 0.37f;

        // Camera angles, degrees. The camera tracks the posed head ONE-FOR-ONE (no follow coefficient): at
        // any partial coefficient the face-to-lens angle becomes a function of the pose, sweeping frontal to
        // looking-away across one library; at 1:1 it is exactly DefaultOblique everywhere, which is the
        // point. DefaultOblique then sets the shot's obliquity — measured, the bundled poses twist the torso
        // <8 deg, so obliquity has to come from the camera. ObliqueDeadband resolves the near-frontal poses
        // (9 of 23 turn the head <5 deg) to one consistent side instead of letting retarget noise pick.
        private const float DefaultOblique = 13f;
        private const float ObliqueDeadband = 5f;
        private const float PitchFollow = 0.5f;
        private const float MaxTrackYaw = 60f;    // guards a user clip, never reached by the bundled poses
        private const float MaxCamPitch = 20f;    // ditto: PitchFollow peaks near 15 deg on this library
        private const float LateralOffset = 0.06f; // fraction of span, looking-room in the landscape frame
        private const float MinFov = 10f, MaxFov = 90f;

        // Solid background when no bg override is passed (a preview scene has no skybox; SolidColor clear
        // is the deterministic backdrop). A named, neutral mid-dark gray.
        private static readonly Color DefaultBackground = new Color(0.23f, 0.23f, 0.24f);
        private const int RampHeight = 256;       // gradient ramp texture, 1 x N, stretched by the blit

        // 3-point directional rig — cosmetic TASTE DEFAULTS, named + here so the operator tunes them after
        // seeing real output. For a directional light only rotation matters. The rig is CAMERA-relative:
        // the eulers below are authored for a camera on the avatar's +Z side, and the holder is yawed to
        // match the solved camera, so orbiting never lights a shot from behind. From that camera, screen-left
        // = +X world and screen-right = -X world. Shadows off for a clean, artifact-free portrait.
        // Total intensity is ~1.2: lilToon/Poiyomi are calibrated so a single directional near 1.0 is correct
        // exposure, and allowHDR is off, so the old 2.7 total clipped faces to flat white.
        private const float KeyIntensity = 0.70f;
        private static readonly Vector3 KeyEuler = new Vector3(40f, 220f, 0f);   // key : front-upper-left
        private const float FillIntensity = 0.30f;
        private static readonly Vector3 FillEuler = new Vector3(15f, 140f, 0f);  // fill: front-right, softer
        private const float RimIntensity = 0.20f;
        private static readonly Vector3 RimEuler = new Vector3(30f, 340f, 0f);   // rim : behind + above

        // Empty-frame guard: a pixel counts as "drawn" when it differs from ITS OWN ROW's column-0 pixel by
        // more than this per-channel byte delta (~0.06 of 255). Sampling the rendered image per row — rather
        // than computing the expected background — is what keeps this color-space-agnostic and correct under
        // a gradient; a computed reference in a Linear project writing an sRGB target reads the whole
        // background as "drawn" and defeats the guard. Genuinely boolean: the fail condition is ZERO drawn
        // pixels, so there is no tuned fraction here to drift.
        private const int SilhouetteChannelThreshold = 16;

        // Cached once — EditorSceneManager.ClearSceneDirtiness is internal, reflected to restore a scene
        // that was clean at snapshot. Null means the API drifted; teardown surfaces that (fail-loud) rather
        // than silently leaving a scene dirty.
        private static readonly System.Reflection.MethodInfo ClearSceneDirtinessMethod =
            typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(
                "ClearSceneDirtiness",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// Render a 1200×900 portrait PNG of the baked <paramref name="target"/> avatar and return a
        /// one-line verdict whose <c>png=</c> trailer is the written path. <paramref name="whatIf"/>
        /// preflights (resolve target, assert a VRC_AvatarDescriptor, resolve <paramref name="pose"/>)
        /// and returns without baking or touching the project.
        /// </summary>
        /// <param name="target">avatar root: scene hierarchy path, instance id, or name (first match).</param>
        /// <param name="pose">null =&gt; floor (unposed); a bundled name (see <see cref="BundledPoses"/>);
        /// or a clip asset path/GUID.</param>
        /// <param name="expression">null =&gt; no expression (a fully supported outcome); else a state name
        /// on the baked FX controller (a gesture slot such as <c>Open</c>/<c>Peace</c>), or a clip asset
        /// path/GUID, whose blendShape curves are composited with <paramref name="pose"/>. Resolved after
        /// the bake — read the controller with ReportController to see what a given avatar offers.</param>
        /// <param name="framing">"bust" | "half" | "full" — the subject height the frame covers. The
        /// camera distance is solved from it and <paramref name="fov"/>, so the two are independent.</param>
        /// <param name="bg">null =&gt; default backdrop; "#RRGGBB" =&gt; solid; "#TOP:#BOTTOM" =&gt;
        /// vertical two-stop gradient.</param>
        /// <param name="fov">vertical field of view in degrees (10–90). Changes the LOOK, not the framing:
        /// narrower backs the camera off and flattens perspective.</param>
        /// <param name="yaw">null =&gt; an automatic flattering oblique, signed to the side the pose turns
        /// its head. A number is an OFFSET added to head-tracking, not an absolute heading — so
        /// <c>yaw: 0</c> means "track the head with no oblique", not "frontal". Positive orbits the camera
        /// toward world +X (screen-left).</param>
        /// <param name="whatIf">preflight only: resolve target/descriptor/pose, report, bake nothing.
        /// <paramref name="expression"/> is echoed unresolved — it needs the baked controller.</param>
        public static string Render(
            string target,
            string pose = null,
            string expression = null,
            string framing = "bust",
            string bg = null,
            float fov = 30f,
            float? yaw = null,
            bool whatIf = false)
        {
            var root = Resolve(target);
            if (root == null)
                return Fail(target, "target not found — tried hierarchy path, instance id, then name in the active scene");
            string label = root.name;

            var descriptor = root.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();
            if (descriptor == null)
                return Fail(label, "no VRC_AvatarDescriptor on '" + label + "'");

            // Validate framing + bg + camera up front so BOTH whatIf and the render path reject bad inputs
            // before proceeding — a whatIf WOULD-RENDER must never precede an immediate render FAIL on the
            // same inputs.
            float span, aimDrop;
            try { FramingGeometry(framing, out span, out aimDrop); }
            catch (ArgumentException ex) { return Fail(label, ex.Message); }
            string framingToken = (framing ?? "bust").Trim().ToLowerInvariant();

            Color bgTop = DefaultBackground, bgBottom = DefaultBackground;
            if (bg != null && !TryParseBg(bg, out bgTop, out bgBottom))
                return Fail(label, "unparseable bg '" + bg + "' — expected #RRGGBB, #RRGGBBAA, or #TOP:#BOTTOM");

            // Bounded because the distance solve goes as 1/tan(fov/2): at 90 a bust frame puts the camera
            // 0.23 m from the view point, inside the hair mesh, which renders as hair interior and would
            // sail past the empty-frame guard.
            if (!(fov >= MinFov && fov <= MaxFov))
                return Fail(label, "fov " + fov.ToString(CultureInfo.InvariantCulture) + " out of range — expected "
                    + MinFov.ToString(CultureInfo.InvariantCulture) + "–" + MaxFov.ToString(CultureInfo.InvariantCulture));
            if (yaw.HasValue && (float.IsNaN(yaw.Value) || float.IsInfinity(yaw.Value)))
                return Fail(label, "yaw must be a finite number of degrees, or null for the automatic oblique");

            string camToken = "fov=" + fov.ToString("0.#", CultureInfo.InvariantCulture)
                + " yaw=" + (yaw.HasValue ? yaw.Value.ToString("0.#", CultureInfo.InvariantCulture) : "auto");

            if (whatIf)
            {
                if (!ResolvePose(pose, out AnimationClip _, out string poseErr))
                    return Fail(label, poseErr);

                string poseToken = string.IsNullOrEmpty(pose) ? "floor" : pose;
                // Not preflighted: an expression resolves against the BAKED controller, which whatIf by
                // definition does not have. Echoed so the caller can see what will be attempted.
                string exprToken = string.IsNullOrEmpty(expression) ? "none" : expression + " (at bake)";
                string ok = string.Format(CultureInfo.InvariantCulture,
                    "[RenderThumbnail] Render {0} whatIf pose={1} expression={2} framing={3} {4} descriptor=OK => WOULD-RENDER (no bake)",
                    label, poseToken, exprToken, framingToken, camToken);
                Debug.Log(ok);
                return ok;
            }

            // ---- Preflight resolution (fail fast, before any mutation) ----
            if (!ResolvePose(pose, out AnimationClip poseClip, out string renderPoseErr))
                return Fail(label, renderPoseErr);
            bool wantExpression = !string.IsNullOrEmpty(expression);
            // poseName is the ORIGINAL pose argument (for verdict display), not the resolved asset path —
            // matches the whatIf branch's poseToken convention above. Same for expressionName.
            string poseName = string.IsNullOrEmpty(pose) ? "floor" : pose;
            string expressionName = string.IsNullOrEmpty(expression) ? "none" : expression;

            // ---- Snapshots for restore (before any mutation), used by the finally teardown ----
            Scene targetScene = root.scene;
            var sceneDirtyAtStart = new Dictionary<Scene, bool>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                if (sc.IsValid()) sceneDirtyAtStart[sc] = sc.isDirty;
            }
            var preRootIds = new HashSet<int>();
            foreach (var go in targetScene.GetRootGameObjects()) preRootIds.Add(go.GetInstanceID());
            var savedSelection = Selection.objects;
            // activeObject (not activeGameObject): restoring the latter would null a non-GameObject selection
            // (an asset/material — common in this workflow). Snapshot AnimationMode too, so teardown stops it
            // only when WE started it — never ending an operator's in-progress Animation-window recording.
            UnityEngine.Object savedActive = Selection.activeObject;
            bool animModeAtStart = AnimationMode.InAnimationMode();

            Scene preview = default;
            GameObject baked = null;
            // Set the instant preprocess is entered: teardown owes the SDK its paired OnPostprocessAvatar
            // even when a hook throws midway.
            bool bakePreprocessed = false;
            RenderTexture rt = null;
            Texture2D tex = null;
            Texture2D ramp = null;                                  // gradient backdrop source, if any
            UnityEngine.Rendering.CommandBuffer bgCmd = null;       // draws it inside the camera's pass
            string result = null;      // non-null on the silhouette-fail / non-success paths only
            string prefix = null;      // the OK verdict head, consumed after teardown once residualNote is known
            string pngPath = null;     // the written PNG path, ditto
            string residualNote = "";

            // OUTER try — converts any exception from the pipeline (broken rig, null bake, non-humanoid clip)
            // into a Fail verdict, the house convention. Its catch sits OUTSIDE the inner try/finally, so
            // teardown runs and populates residualNote BEFORE the catch builds its return string.
            try
            {
            try
            {
                // ---- Step 2: unique private clone -> bake (the non-destructiveness keystone) ----
                // The GUID suffix makes any generated-asset subfolder provably exclusive to this run
                // (regardless of timer resolution), so a pre-existing-folder wholesale-delete can never
                // touch a user's kept bake.
                //
                // The bake door is the SDK preprocess chain, NOT ManualProcessAvatar — see
                // nondestructive.md §The bake door. Unlike ManualProcessAvatar (clone in, new object
                // out), this mutates IN PLACE, so `mine` IS the baked avatar with nothing else to destroy.
                string stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
                var mine = UnityEngine.Object.Instantiate(root);
                string mineName = root.name + "__rt_" + stamp;
                mine.name = mineName;
                mine.SetActive(true); // an inactive avatar is not a valid preprocess target

                bool preprocessOk;
                bakePreprocessed = true;
                try
                {
                    preprocessOk = VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks
                        .OnPreprocessAvatar(mine);
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(mine);
                    throw;
                }
                if (!preprocessOk)
                {
                    UnityEngine.Object.DestroyImmediate(mine);
                    // A hook REFUSED the build (VRCFury misconfiguration, a failed optimizer pass, an SDK
                    // validation). It logs its own reason to the console; surface that it happened.
                    throw new InvalidOperationException(
                        "VRC build preprocess refused '" + label + "' — a build hook blocked it (its reason "
                        + "is in the Unity console). The avatar would not upload in this state either.");
                }
                baked = mine;

                // ---- Step 3: preview scene ----
                preview = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
                SceneManager.MoveGameObjectToScene(baked, preview);

                // ---- Step 4: 3-point rig IN the preview scene (NO RenderSettings writes — those are global
                // and would leak into + dirty the live scene; CAU stays lights-only for this reason). The
                // camera's SolidColor clear is the background (no backdrop quad in v1). ----
                var lightHolder = EditorUtility.CreateGameObjectWithHideFlags("__rt_lights", HideFlags.DontSave);
                SceneManager.MoveGameObjectToScene(lightHolder, preview);

                Light MakeLight(string n, float intensity, Vector3 euler)
                {
                    var go = EditorUtility.CreateGameObjectWithHideFlags(n, HideFlags.DontSave, typeof(Light));
                    SceneManager.MoveGameObjectToScene(go, preview);
                    var l = go.GetComponent<Light>();
                    l.type = LightType.Directional;
                    l.color = Color.white;
                    l.intensity = intensity;
                    l.shadows = LightShadows.None;
                    go.transform.rotation = Quaternion.Euler(euler);
                    go.transform.SetParent(lightHolder.transform, true);
                    return l;
                }
                MakeLight("__rt_key", KeyIntensity, KeyEuler);
                MakeLight("__rt_fill", FillIntensity, FillEuler);
                MakeLight("__rt_rim", RimIntensity, RimEuler);

                // ---- Step 5: view point, then pose ----
                // The view point (the creator-authored viewball) is the camera's target. It is computed on
                // EVERY path — the floor path is the default and must keep rendering on a non-humanoid rig,
                // which has no head bone — and it needs no Animator: ViewPosition is root-local.
                var descriptorBaked = baked.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();
                if (descriptorBaked == null)
                    throw new InvalidOperationException("baked clone has no VRC_AvatarDescriptor — bake dropped it");

                // Pose-INDEPENDENT subject scale. Deliberately not the posed view point's world height: that
                // drops when the avatar sits, which would shrink the span on exactly the seated poses and
                // destroy framing comparability across the library. lossyScale because span feeds a world
                // metre distance and VRChat avatars are routinely scaled at the root.
                float viewHeight = descriptorBaked.ViewPosition.y * baked.transform.lossyScale.y;
                Vector3 viewpoint = baked.transform.TransformPoint(descriptorBaked.ViewPosition);
                // Degenerate viewball or a zero-scaled parent collapses span AND distance to 0: the camera
                // lands exactly on `aim`, LookRotation gets a zero vector, and the run would surface as
                // "nothing drew" — naming the wrong cause. A misplaced viewball is the operator's to see; an
                // unusable one is ours to name.
                if (!(viewHeight > 0.01f))
                    throw new InvalidOperationException(
                        "unusable view height " + viewHeight.ToString("0.###", CultureInfo.InvariantCulture)
                        + " m on '" + label + "' — ViewPosition.y="
                        + descriptorBaked.ViewPosition.y.ToString("0.###", CultureInfo.InvariantCulture)
                        + " times root scale " + baked.transform.lossyScale.y.ToString("0.###", CultureInfo.InvariantCulture)
                        + "; the camera solve has no subject scale to work from");

                // A resolved pose is SAMPLED AND HELD here: StopAnimationMode is deliberately NOT called
                // before cam.Render() below — EndSampling leaves the pose applied, it does not revert it.
                // Reverting happens once, defensively, in the outer teardown `finally` (below) after capture,
                // so a throw mid-sampling still can't leak animation-mode state.
                float headYaw = 0f, headPitch = 0f;   // both exactly zero on the floor path
                if (poseClip != null)
                {
                    var animator = baked.GetComponent<UnityEngine.Animator>();
                    if (animator == null)
                        throw new InvalidOperationException(
                            "baked clone has no Animator — cannot sample pose '" + poseName + "'");
                    if (!animator.isHuman)
                        // Generic rig: GetBoneTransform(Head) is null and SampleAnimationClip is a silent no-op,
                        // yet the verdict would still claim pose=<name>. Fail loud instead (outer catch -> Fail).
                        throw new InvalidOperationException(
                            "baked avatar '" + label + "' is not humanoid — cannot sample pose '" + poseName + "'");

                    // Rebind FIRST, then read rest: the sampled clip is applied relative to the BIND pose, so
                    // that is what "rest" has to mean. Anchoring to the pre-Rebind scene pose instead would
                    // bake a per-avatar constant into the view-point offset and into every angle below.
                    animator.Rebind();
                    var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                    if (headBone == null)
                        throw new InvalidOperationException(
                            "baked avatar '" + label + "' has no Head bone — cannot aim a posed portrait");

                    // Re-parent the view point onto the head so it follows the head's TRANSLATION AND
                    // ROTATION through the pose. Eye bones are not used: many rigs lack them, and a bake can
                    // rename them, whereas Head is mandatory on the humanoid rig asserted above.
                    Vector3 viewInHead = headBone.InverseTransformPoint(viewpoint);
                    Quaternion restHeadLocal = Quaternion.Inverse(baked.transform.rotation) * headBone.rotation;

                    AnimationMode.StartAnimationMode();
                    AnimationMode.BeginSampling();
                    try { AnimationMode.SampleAnimationClip(baked, poseClip, poseClip.length); }
                    finally { AnimationMode.EndSampling(); }

                    viewpoint = headBone.TransformPoint(viewInHead);

                    // Head facing as a DELTA FROM BIND, in the root's basis. Unity does not normalize humanoid
                    // bone axes, so anything derived from where the bone's own +Z points is rig-dependent.
                    //
                    // Take the DELTA ROTATION FIRST, then extract once. Extracting an angle from each forward
                    // vector and subtracting is NOT equivalent: it cancels a constant offset but not the axis
                    // dependence, and it fails hardest on the most common convention. Measured, for a 30 deg
                    // yaw + 20 deg chin-raise applied to a bone whose +Z runs UP the neck (Blender authors
                    // orient a bone along its length, which is most of the VRChat population): subtract-form
                    // yields yaw 0.0 — tracking silently dead, every shot getting the bare oblique — and
                    // pitch -20 instead of +20, which would shoot a chin-up pose from BELOW. The delta form
                    // returns 30/+20 for every rest orientation, including arbitrary skew.
                    //
                    // Still extracted off the forward VECTOR rather than eulers: no wrap or gimbal question.
                    Quaternion posedHeadLocal = Quaternion.Inverse(baked.transform.rotation) * headBone.rotation;
                    Vector3 deltaFwd = (posedHeadLocal * Quaternion.Inverse(restHeadLocal)) * Vector3.forward;
                    headYaw = YawOf(deltaFwd);      // Atan2 already yields [-180,180]
                    headPitch = PitchOf(deltaFwd);
                }

                // ---- Step 5b: expression, applied AFTER the pose and deliberately NOT through the
                // animation system. A second SampleAnimationClip re-runs the Animator's humanoid solver
                // and PARTIALLY UNDOES the pose — the left upper arm moved (301.6,303.5,76.7) ->
                // (321.6,344.4,33.2). Pose and expression bind disjoint properties, not disjoint systems.
                // Writing weights straight onto the renderers leaves the pose byte-identical.
                string resolvedExpression = null;
                if (wantExpression)
                {
                    var bakedExpr = FindExpressionClip(baked, expression, out string bakedErr);
                    if (bakedExpr == null) throw new InvalidOperationException(bakedErr);

                    resolvedExpression = bakedExpr.name;
                    if (ApplyExpression(baked, bakedExpr) == 0)
                        throw new InvalidOperationException(
                            "expression '" + expressionName + "' (clip '" + resolvedExpression
                            + "') moved no blendshape on the baked avatar — it binds shapes this avatar "
                            + "does not have, or only meshes that are not drawn");
                }

                // ---- Step 6: camera + off-screen sRGB capture (§Capture, source-verified) ----
                var camGO = EditorUtility.CreateGameObjectWithHideFlags("__rt_cam", HideFlags.DontSave, typeof(Camera));
                SceneManager.MoveGameObjectToScene(camGO, preview);
                var cam = camGO.GetComponent<Camera>();
                cam.enabled = false;
                cam.scene = preview;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = bgTop;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                cam.cullingMask = ~(1 << 5); // exclude UI layer (5) — world-space canvases would render into the thumbnail
                cam.allowHDR = false;                 // Linear project — the sRGB RT below carries the gamma
                cam.fieldOfView = fov;
                // Forward explicitly: renderingPath defaults to the project's tier settings, and under
                // Deferred the BeforeForwardOpaque command buffer below never fires — the gradient would
                // silently not draw and a solid background would still sail past the empty-frame guard.
                cam.renderingPath = RenderingPath.Forward;

                // The camera tracks the head one-for-one, then adds the oblique. The deadband decides the
                // oblique's side for the near-frontal poses (measured: 9 of 23 turn the head <5 deg) rather
                // than letting retarget noise pick, which would swing the camera 2*DefaultOblique and shoot
                // the same pose from opposite sides on two avatars.
                float obliqueSign = headYaw < -ObliqueDeadband ? -1f : 1f;
                // ONE shot offset drives both the orbit and the looking-room, so an explicit yaw cannot orbit
                // the camera one way while the lateral aim opens the other — which is what keying the offset
                // off headYaw did: yaw:-30 against a +10 head turn shifted the subject INTO its own gaze.
                float shotOffset = yaw ?? (DefaultOblique * obliqueSign);
                // Zero is its own case, not a tie broken toward positive: at yaw:0 the camera tracks the head
                // exactly, so the face is dead-on the lens and there is no gaze in frame to leave room for.
                float lateralSign = shotOffset > 0f ? 1f : (shotOffset < 0f ? -1f : 0f);
                float camYaw = Mathf.Clamp(headYaw, -MaxTrackYaw, MaxTrackYaw) + shotOffset;
                // Negated: a positive rotation about +X lowers the camera, and a chin-RAISED pose has to be
                // shot from slightly ABOVE (from below it is a nostril shot).
                float camPitch = Mathf.Clamp(-headPitch * PitchFollow, -MaxCamPitch, MaxCamPitch);

                // Angles apply in the posed clone's root basis so an avatar sitting rotated in the scene still
                // photographs frontally — the case that matters most on the floor path, where no sampling
                // overwrites root rotation.
                Quaternion rootRot = baked.transform.rotation;
                Quaternion orbit = Quaternion.AngleAxis(camYaw, Vector3.up) * Quaternion.AngleAxis(camPitch, Vector3.right);
                Vector3 screenRight = rootRot * (orbit * Vector3.left);   // left: the camera looks back along -forward

                float scaledSpan = span * (viewHeight / ReferenceViewHeight);
                float distance = (scaledSpan * 0.5f) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

                // Aim below the view point (the eyes sit near the TOP of the subject, not its centre) and
                // laterally away from the side the face is turned, which opens looking-room in a landscape
                // frame that would otherwise waste its width on a vertical subject.
                // Vector3.up, not the root's up: camera roll stays world-level below, so the drop that decides
                // where the head sits in frame has to share that basis. Divergent only for a tilted root.
                Vector3 aim = viewpoint
                            - Vector3.up * (scaledSpan * aimDrop)
                            + screenRight * (scaledSpan * LateralOffset) * -lateralSign;

                cam.transform.position = aim + rootRot * (orbit * (Vector3.forward * distance));
                cam.transform.rotation = Quaternion.LookRotation(aim - cam.transform.position, Vector3.up);

                // The rig is camera-relative by construction; without this, orbiting lights a yawed shot from
                // behind. After MakeLight, which sets world rotation and then parents worldPositionStays.
                lightHolder.transform.rotation = Quaternion.Euler(0f, baked.transform.eulerAngles.y + camYaw, 0f);

                // Gradient backdrop: draw the ramp inside the camera's own pass, after its clear. Blitting into
                // the RT before Render and clearing depth only would rely on colour contents surviving the
                // render-target switch, which is not contractual on an MSAA target.
                if (bgTop != bgBottom)
                {
                    ramp = new Texture2D(1, RampHeight, TextureFormat.RGBA32, false);
                    ramp.hideFlags = HideFlags.DontSave;
                    ramp.wrapMode = TextureWrapMode.Clamp;
                    ramp.filterMode = FilterMode.Bilinear;
                    for (int i = 0; i < RampHeight; i++)   // row 0 is the BOTTOM of the blit
                        ramp.SetPixel(0, i, Color.Lerp(bgBottom, bgTop, i / (RampHeight - 1f)));
                    ramp.Apply(false);
                    bgCmd = new UnityEngine.Rendering.CommandBuffer { name = "__rt_bg_gradient" };
                    bgCmd.Blit(ramp, UnityEngine.Rendering.BuiltinRenderTextureType.CurrentActive);
                    cam.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeForwardOpaque, bgCmd);
                }

                const int W = 1200, H = 900;
                rt = new RenderTexture(W, H, 24, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB)
                {
                    // sRGB is MANDATORY: the project is Linear; a default linear RT ships a dark, wrong-gamma PNG.
                    antiAliasing = Math.Max(1, QualitySettings.antiAliasing)
                };
                cam.targetTexture = rt;
                cam.pixelRect = new Rect(0, 0, W, H);
                cam.Render();

                // Where the face landed, while the camera still exists. The VIEW POINT, not `aim` — the camera
                // is aimed exactly at `aim` by construction, so projecting that would print (0.5,0.5) forever.
                Vector3 headViewport = cam.WorldToViewportPoint(viewpoint);

                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                try
                {
                    tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    tex.Apply(false);
                }
                finally
                {
                    // Always release the active RT — otherwise a throw here leaves rt active when the outer
                    // finally DestroyImmediates it.
                    RenderTexture.active = prevActive;
                }

                // ---- Empty-frame guard: did ANYTHING draw? ----
                // Reference = each row's own column-0 pixel of the read-back texture, NOT a computed color.
                // Sampling the render is what keeps this color-space-agnostic (a computed reference in a Linear
                // project writing an sRGB target reads the whole background as "drawn" and defeats the guard),
                // and per-row is what makes it exact under a vertical gradient, which is constant along a row.
                // A subject that reaches column 0 inverts at most that row: ~0.1% of the frame against a 0.5%
                // threshold. Boolean by design — the old reported percentage was never judged on.
                Color32[] pixels = tex.GetPixels32();
                int drawn = 0;
                for (int y = 0; y < H; y++)
                {
                    Color32 rowRef = pixels[y * W];
                    for (int x = 0; x < W; x++)
                    {
                        Color32 p = pixels[y * W + x];
                        if (Math.Abs(p.r - rowRef.r) > SilhouetteChannelThreshold
                            || Math.Abs(p.g - rowRef.g) > SilhouetteChannelThreshold
                            || Math.Abs(p.b - rowRef.b) > SilhouetteChannelThreshold)
                            drawn++;
                    }
                }
                // Strictly zero, not a fraction. Under the per-row reference a blank frame reads EXACTLY 0
                // (measured), so the old 0.005 floor bought nothing and cost a lie: at 1200x900 it failed up
                // to 5,399 genuinely-drawn pixels while asserting "nothing drew", and withheld the PNG that
                // would have shown the operator what actually happened. The budget puts a visible-in-the-image
                // problem in the operator's hands, not the tool's.
                bool nothingDrew = drawn == 0;

                // Which clip the selector actually resolved to post-bake, and where the camera ended up — the
                // things the reader cannot infer from the arguments. camYaw is RESOLVED (tracking + oblique),
                // so a saturated clamp is visible rather than silent; head is the view point's viewport
                // position, origin bottom-left, REPORTED not gated — a bad crop is visible in the PNG, and
                // failing would withhold the very image that would show it.
                string shapesToken = wantExpression ? " (" + resolvedExpression + ")" : "";
                prefix = "[RenderThumbnail] Render " + label + " baked pose=" + poseName
                    + " expression=" + expressionName + shapesToken + " framing=" + framingToken
                    + " fov=" + fov.ToString("0.#", CultureInfo.InvariantCulture)
                    + " headYaw=" + headYaw.ToString("0.#", CultureInfo.InvariantCulture)
                    + " camYaw=" + camYaw.ToString("0.#", CultureInfo.InvariantCulture)
                    + " head=(" + headViewport.x.ToString("0.00", CultureInfo.InvariantCulture)
                    + "," + headViewport.y.ToString("0.00", CultureInfo.InvariantCulture) + ")";

                if (nothingDrew)
                {
                    // Fail loud, uniform (=> FAIL:) — do NOT write a blank PNG. Named honestly: a frame the
                    // subject fills EDGE TO EDGE also reads uniform, because then column 0 is subject too.
                    result = Fail(label, "every pixel matches its row's background reference (pose=" + poseName
                        + " expression=" + expressionName + " framing=" + framingToken + " camYaw="
                        + camYaw.ToString("0.#", CultureInfo.InvariantCulture)
                        + ") — nothing drew, or the subject fills the frame entirely");
                }
                else
                {
                    string safeLabel = RunLogFormat.Sanitize(label);
                    pngPath = System.IO.Path.Combine(Application.temporaryCachePath,
                        "renderthumbnail_" + safeLabel + "_" + stamp + ".png");
                    System.IO.File.WriteAllBytes(pngPath, tex.EncodeToPNG());
                    // Verdict is built AFTER teardown (once residualNote is known) — leave `result` null to
                    // signal the success path, so a cleanup-failed run never ships an "=> OK | png=" token.
                }
            }
            finally
            {
                // ---- Step 7: teardown — runs on EVERY exit (success or throw). Order matters.
                // The body is itself wrapped so the SDK postprocess pairing below can never be skipped:
                // the steps here are guarded against null, not against throwing (the reflected
                // ClearSceneDirtiness can raise TargetInvocationException; DestroyImmediate can raise on
                // an already-destroyed object), and losing the pairing would leak exactly what it exists
                // to clean while replacing the real pipeline exception with a teardown one. ----
                try
                {
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                // The camera owns the command buffer and ClosePreviewScene destroys it, but CommandBuffer wraps
                // native memory and the ramp is an asset-less texture: a contact-sheet run leaks one of each per
                // render without these.
                if (bgCmd != null) bgCmd.Dispose();
                if (ramp != null) UnityEngine.Object.DestroyImmediate(ramp);

                if (!animModeAtStart && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();

                if (preview.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(preview); // destroys baked + lights + camera

                // Orphan sweep: any root new in the target's scene since the step-0 snapshot. Our own `mine`
                // is already destroyed by the inner finally and `baked` by ClosePreviewScene — this is the
                // backstop for a stray NDMF intermediate, or a `baked` stranded by a bake that threw after
                // instantiating the clone into the live scene but before we could move it to the preview.
                if (targetScene.IsValid())
                {
                    foreach (var go in targetScene.GetRootGameObjects())
                        if (!preRootIds.Contains(go.GetInstanceID()))
                            UnityEngine.Object.DestroyImmediate(go);
                }

                Selection.objects = savedSelection;
                Selection.activeObject = savedActive;

                // Restore scene cleanliness for scenes that were clean at snapshot but are now dirty (the bake
                // instantiates the clone into the live scene, dirtying it; the orphan sweep dirties it too).
                foreach (var kv in sceneDirtyAtStart)
                {
                    Scene sc = kv.Key;
                    if (kv.Value || !sc.IsValid() || !sc.isDirty) continue;
                    if (ClearSceneDirtinessMethod != null)
                        ClearSceneDirtinessMethod.Invoke(null, new object[] { sc });
                    else
                        // API drifted — surface it rather than silently leaving a scene dirty (rule 7).
                        residualNote += " note=scene-dirty-uncleared: EditorSceneManager.ClearSceneDirtiness internals drifted";
                }

                }
                finally
                {
                    // The SDK's preprocess/postprocess pair is a contract: OnPreprocessAvatar was called,
                    // so OnPostprocessAvatar must be too. It fires every hook's OWN cleanup (NDMF's
                    // temporary-asset sweep among them) rather than this tool guessing folder names it
                    // does not control. Assets that outlive it are not chased — projects accumulate some.
                    if (bakePreprocessed)
                    {
                        try { VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPostprocessAvatar(); }
                        catch (Exception cleanupEx)
                        {
                            // Surfaced, not swallowed — but never fatal: the PNG is already written.
                            residualNote += " note=postprocess-cleanup-threw: " + cleanupEx.GetType().Name;
                        }
                    }
                }
            }

                // Verdict assembled HERE, after teardown, so residualNote (a cleanup failure) is known before we
                // decide whether a success run may ship its load-bearing "=> OK | png=" token.
                if (result != null) return result + residualNote;   // silhouette-fail / non-success paths
                if (string.IsNullOrEmpty(residualNote))
                {
                    string ok = prefix + " => OK | png=" + pngPath;
                    Debug.Log(ok);
                    return ok;
                }
                // Rendered fine, but teardown left residue — NO success-form png= token (a machine consumer must
                // not read this as clean success); the residue is the headline, the path is informational only.
                string degraded = prefix + " => CLEANUP-FAILED — png written to " + pngPath + residualNote;
                Debug.LogError(degraded);
                return degraded;
            }
            catch (Exception ex)
            {
                // Inner finally has already run (teardown complete, residualNote populated) — surface the pipeline
                // exception as a Fail verdict, keeping the type + stack so a deep NDMF/MA pass failure is nameable.
                Debug.LogException(ex);
                return Fail(label, ex.GetType().Name + ": " + ex.Message) + residualNote;
            }
        }

        /// <summary>Yaw of a direction in degrees, signed about +Y: 0 = +Z, positive toward +X (which is
        /// screen-LEFT for a camera on the +Z side). Taken off the forward vector rather than eulers so
        /// there is no wrap or gimbal question, and so it matches the measured pose-angle table.</summary>
        internal static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        /// <summary>Elevation of a direction in degrees. Positive = pointing up (chin raised).</summary>
        internal static float PitchOf(Vector3 dir) =>
            Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        /// <summary>
        /// Write the clip's <c>blendShape.*</c> curves straight onto the baked clone's renderers and
        /// return how many landed on a DRAWN one. Only blendShape curves are applied — an expression is
        /// its blendshape curves. Applying rather than sampling is what keeps the pose intact (see the
        /// call site); a zero return is the caller's signal that the clip and the baked avatar do not
        /// match.
        /// </summary>
        private static int ApplyExpression(GameObject baked, AnimationClip clip)
        {
            int applied = 0;

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.propertyName == null
                    || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;

                Transform t = string.IsNullOrEmpty(binding.path)
                    ? baked.transform
                    : baked.transform.Find(binding.path);
                var smr = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                if (smr == null || smr.sharedMesh == null) continue;

                // A weight written to a disabled or inactive renderer changes nothing visible, so it does
                // not count as landed — optimizers and MA routinely leave blush/star-eye meshes inactive.
                if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;

                int index = smr.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length));
                if (index < 0) continue;

                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                // Evaluate at clip.length, matching the pose's sample-at-clip.length convention. Curves
                // default to ClampForever, so for the single-key clips expressions almost always are this
                // is the only key; for a multi-key clip it is deliberately the END state.
                smr.SetBlendShapeWeight(index, curve.Evaluate(clip.length));
                applied++;
            }
            return applied;
        }

        // ===== Pure helpers (unit-tested; do not touch the scene or the asset database beyond reads) ====

        /// <summary>The subject height a named framing covers (meters, at <see cref="ReferenceViewHeight"/>)
        /// and how far below the view point to aim, as a fraction of that span. Throws for anything else.
        /// <para>NOT a dolly distance — that was the v1 quantity, and it was calibrated to a fixed 60° FOV.
        /// A span is FOV-independent: the camera distance is solved from the two.</para></summary>
        internal static void FramingGeometry(string framing, out float span, out float aimDrop)
        {
            switch ((framing ?? "").Trim().ToLowerInvariant())
            {
                case "bust": span = BustSpan; aimDrop = BustAimDrop; return;
                case "half": span = HalfSpan; aimDrop = HalfAimDrop; return;
                case "full": span = FullSpan; aimDrop = FullAimDrop; return;
                default:
                    throw new ArgumentException(
                        "unknown framing '" + framing + "' — valid: bust, half, full", nameof(framing));
            }
        }

        /// <summary>Parses a backdrop: <c>#RRGGBB</c>/<c>#RRGGBBAA</c> gives one color in both outs (solid),
        /// <c>#TOP:#BOTTOM</c> gives a vertical two-stop gradient. Hex only — a leading '#' is required even
        /// though <see cref="ColorUtility.TryParseHtmlString"/> would otherwise also accept some CSS color
        /// names, which this tool's contract deliberately excludes. The ':' is unambiguous against
        /// <c>#RRGGBBAA</c>, so the two forms cannot be confused.</summary>
        internal static bool TryParseBg(string s, out Color top, out Color bottom)
        {
            top = default; bottom = default;
            if (string.IsNullOrEmpty(s)) return false;

            int split = s.IndexOf(':');
            if (split < 0)
            {
                if (s[0] != '#' || !ColorUtility.TryParseHtmlString(s, out top)) return false;
                bottom = top;
                return true;
            }

            string a = s.Substring(0, split), b = s.Substring(split + 1);
            if (a.Length == 0 || b.Length == 0 || a[0] != '#' || b[0] != '#') return false;
            return ColorUtility.TryParseHtmlString(a, out top) && ColorUtility.TryParseHtmlString(b, out bottom);
        }

        /// <summary>
        /// The bundled poses: readable name (the file name minus <c>RTPose_</c>) to asset path, sorted so
        /// the unknown-pose error is stable. The folder IS the vocabulary — adding a pose is dropping a
        /// file, and the error is derived from disk, so what the tool advertises cannot drift from what
        /// ships.
        /// </summary>
        internal static SortedDictionary<string, string> BundledPoses()
        {
            var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(PosesFolder)) return found;

            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { PosesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets descends child folders; a Poses/Archive/RTPose_Old.anim must not become
                // advertised vocabulary, so the glob is pinned to this folder exactly.
                if (System.IO.Path.GetDirectoryName(path).Replace('\\', '/') != PosesFolder) continue;
                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith("RTPose_", StringComparison.OrdinalIgnoreCase)) continue;
                found[file.Substring("RTPose_".Length)] = path;
            }
            return found;
        }

        /// <summary>
        /// Fold a name to its match key: lowercase, drop every non-alphanumeric character. This is what
        /// makes <c>hand-on-hip</c>, <c>hand_on_hip</c> and <c>HandOnHip</c> one token, for both pose
        /// names and FX state names.
        /// </summary>
        internal static string NormalizeToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>
        /// Resolve <paramref name="pose"/> to a clip: null/empty =&gt; floor (<paramref name="clip"/> null,
        /// no error); a clip asset path or 32-hex GUID loads directly; anything else is matched (via
        /// <see cref="NormalizeToken"/>) against <see cref="BundledPoses"/>, first match wins.
        /// <para>The one guard that has to stay: a clip that is not <c>isHumanMotion</c> would not retarget
        /// across rigs, and <c>SampleAnimationClip</c> is a SILENT no-op for it on a humanoid — so without
        /// this the verdict would claim <c>pose=&lt;name&gt;</c> over an unposed avatar. Unlike the
        /// expression side, nothing downstream catches it.</para>
        /// </summary>
        internal static bool ResolvePose(string pose, out AnimationClip clip, out string err)
        {
            clip = null;
            err = null;
            if (string.IsNullOrEmpty(pose)) return true; // floor

            string trimmed = pose.Trim();
            string source;

            if (trimmed.IndexOf('/') >= 0 || IsGuid(trimmed))
            {
                source = IsGuid(trimmed) ? AssetDatabase.GUIDToAssetPath(trimmed) : trimmed;
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(source);
                if (clip == null)
                {
                    err = "no AnimationClip at '" + trimmed + "'";
                    return false;
                }
            }
            else
            {
                var bundled = BundledPoses();
                string key = NormalizeToken(trimmed);
                source = null;
                foreach (var entry in bundled)
                    if (NormalizeToken(entry.Key) == key) { source = entry.Value; break; }

                if (source == null)
                {
                    var names = new List<string>(bundled.Keys);
                    err = "unknown pose '" + pose + "' — bundled: "
                        + (names.Count > 0 ? string.Join(", ", names.ToArray()) : "(none in " + PosesFolder + ")")
                        + "; or pass a clip asset path/GUID";
                    return false;
                }
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(source);
                if (clip == null)
                {
                    err = "bundled pose '" + pose + "' did not load from " + source;
                    return false;
                }
            }

            if (!clip.isHumanMotion)
            {
                err = "clip '" + source + "' is not a humanoid muscle clip (isHumanMotion=false)";
                clip = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Find the clip for <paramref name="expression"/> on the BAKED avatar: a clip asset path or
        /// 32-hex GUID loads directly; anything else is matched (via <see cref="NormalizeToken"/>)
        /// against the state names of the baked FX controller's layers. Null with a null
        /// <paramref name="err"/> means no expression was asked for.
        /// <para>Resolving on the baked avatar rather than the source asset is the whole mechanism: the
        /// bake renames and merges blendshapes, so a pre-bake clip can bind names the baked avatar no
        /// longer has, while a state name survives. This makes no judgement about what "is" an
        /// expression — it resolves the name the caller chose. Choosing is the caller's job.</para>
        /// </summary>
        internal static AnimationClip FindExpressionClip(GameObject baked, string expression, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(expression)) return null;

            string trimmed = expression.Trim();

            if (trimmed.IndexOf('/') >= 0 || IsGuid(trimmed))
            {
                string path = IsGuid(trimmed) ? AssetDatabase.GUIDToAssetPath(trimmed) : trimmed;
                var asset = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (asset == null) err = "no AnimationClip at '" + trimmed + "'";
                return asset;
            }

            string key = NormalizeToken(trimmed);
            bool nameMatched = false;   // matched a state, but its motion was not a clip
            foreach (var layer in FxLayers(baked))
            {
                if (layer.stateMachine == null) continue;
                foreach (var cs in layer.stateMachine.states)
                {
                    if (NormalizeToken(cs.state.name) != key) continue;
                    nameMatched = true;
                    var clip = cs.state.motion as AnimationClip;
                    if (clip != null) return clip;
                }
            }

            // Two different failures, and conflating them costs real time: the caller's answer to "that
            // name doesn't exist" is to try another name, and every retry is a full SDK bake that can
            // wedge the editor on a hook's modal dialog. Say which it is. (A blend-tree state is not
            // walked into — that would be re-growing the traversal heuristics this path exists without.)
            err = nameMatched
                ? "state '" + expression + "' matched but holds no clip (blend tree, or empty) — pass a "
                  + "clip asset path/GUID instead"
                : "no state named '" + expression + "' on the baked FX controller — read the controller "
                  + "with ReportController to see what it offers, or pass a clip asset path/GUID";
            return null;
        }

        /// <summary>The baked avatar's FX layers, or empty when it has no FX controller.</summary>
        private static UnityEditor.Animations.AnimatorControllerLayer[] FxLayers(GameObject baked)
        {
            var descriptor = baked != null
                ? baked.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>()
                : null;
            if (descriptor == null || descriptor.baseAnimationLayers == null)
                return new UnityEditor.Animations.AnimatorControllerLayer[0];

            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.type == VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX
                    && layer.animatorController is UnityEditor.Animations.AnimatorController fx)
                    return fx.layers;
            return new UnityEditor.Animations.AnimatorControllerLayer[0];
        }


        private static bool IsGuid(string s)
        {
            if (s.Length != 32) return false;
            foreach (char ch in s)
                if (!Uri.IsHexDigit(ch)) return false;
            return true;
        }

        // ===== Target resolution: mirrors RenderAvatar's hierarchy-path -> instance-id -> name resolver =
        // (RenderAvatar.Resolve is private to Ryan6VRC.AgentTools.Editor, so this duplicates its small,
        // stable logic rather than reaching for it by reflection.)

        private static GameObject Resolve(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var byPath = FindByHierarchyPath(target);
            if (byPath != null) return byPath;

            if (int.TryParse(target.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                if (obj is GameObject go) return go;
                if (obj is Component comp) return comp.gameObject;
            }

            var scene = SceneManager.GetActiveScene();
            foreach (var rootGo in scene.GetRootGameObjects())
            {
                var hit = FindByNameRecursive(rootGo.transform, target);
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
            foreach (Transform child in t)
            {
                var hit = FindByNameRecursive(child, name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static string Fail(string label, string reason)
        {
            string msg = "[RenderThumbnail] Render " + (string.IsNullOrEmpty(label) ? "?" : label) + " => FAIL: " + reason;
            Debug.LogError(msg);
            return msg;
        }
    }
}
