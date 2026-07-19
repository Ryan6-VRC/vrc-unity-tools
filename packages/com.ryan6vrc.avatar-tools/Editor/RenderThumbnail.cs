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
    /// actually uploads (optimizers included).
    /// <para>This is the <b>edit-mode front-end</b> — the default, deterministic, single synchronous call.
    /// The venue-independent camera solve, capture core, and pose/expression resolvers live in
    /// <see cref="RenderThumbnailCore"/>; the settled-dynamics/resolved-FX <b>play-mode</b> front-end is
    /// <see cref="RenderThumbnailPlay"/>. Edit mode does not simulate physbones (they need the player loop)
    /// and applies the expression by direct blendshape write — both correct here, neither possible in play,
    /// which is exactly why play is a separate front-end on the same spine.</para>
    /// </summary>
    [AgentTool]
    public static class RenderThumbnail
    {
        // 3-point directional rig — cosmetic TASTE DEFAULTS, named + here so the operator tunes them after
        // seeing real output. Edit-mode-only: a NewPreviewScene has no lights, so this tool supplies them.
        // (Play mode renders in the scratch scene under the operator's OWN lights/backdrop, so it has no rig.)
        // For a directional light only rotation matters. The rig is CAMERA-relative: the eulers below are
        // authored for a camera on the avatar's +Z side, and the holder is yawed to match the solved camera
        // (RenderThumbnailCore.CameraSolution.LightHolderRotation), so orbiting never lights a shot from
        // behind. From that camera, screen-left = +X world and screen-right = -X world. Shadows off, and
        // MEASURED so, not assumed: lilToon/Poiyomi gate shadow-receiving behind material toggles vendors
        // rarely enable, so a cast shadow is invisible on the toon shaders that dominate — enabling it only
        // costs a shadow-map pass and risks artifacts for no visible gain (probed on lilToon + Poiyomi,
        // key-only and full-rig, hand-near-face poses). Total intensity 1.4, validated against real lilToon +
        // Poiyomi renders: neither clips at 1.4 with allowHDR off (the old 2.7 total clipped faces to flat
        // white), and it reads a touch poppier than the 1.2 it replaced. The ratio key:fill:rim is held; the
        // total is what was swept.
        private const float KeyIntensity = 0.8167f;
        private static readonly Vector3 KeyEuler = new Vector3(40f, 220f, 0f);   // key : front-upper-left
        private const float FillIntensity = 0.35f;
        private static readonly Vector3 FillEuler = new Vector3(15f, 140f, 0f);  // fill: front-right, softer
        private const float RimIntensity = 0.2333f;
        private static readonly Vector3 RimEuler = new Vector3(30f, 340f, 0f);   // rim : behind + above

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
        /// <param name="pose">null =&gt; floor (unposed); a bundled name (the <c>Editor/Poses/RTPose_*</c>
        /// glob); or a clip asset path/GUID.</param>
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
            var root = RenderThumbnailCore.Resolve(target);
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
            try { RenderThumbnailCore.FramingGeometry(framing, out span, out aimDrop); }
            catch (ArgumentException ex) { return Fail(label, ex.Message); }
            string framingToken = (framing ?? "bust").Trim().ToLowerInvariant();

            Color bgTop = RenderThumbnailCore.DefaultBackground, bgBottom = RenderThumbnailCore.DefaultBackground;
            if (bg != null && !RenderThumbnailCore.TryParseBg(bg, out bgTop, out bgBottom))
                return Fail(label, "unparseable bg '" + bg + "' — expected #RRGGBB, #RRGGBBAA, or #TOP:#BOTTOM");

            // Bounded because the distance solve goes as 1/tan(fov/2): at 90 a bust frame puts the camera
            // 0.23 m from the view point, inside the hair mesh, which renders as hair interior and would
            // sail past the empty-frame guard.
            if (!(fov >= RenderThumbnailCore.MinFov && fov <= RenderThumbnailCore.MaxFov))
                return Fail(label, "fov " + fov.ToString(CultureInfo.InvariantCulture) + " out of range — expected "
                    + RenderThumbnailCore.MinFov.ToString(CultureInfo.InvariantCulture) + "–"
                    + RenderThumbnailCore.MaxFov.ToString(CultureInfo.InvariantCulture));
            if (yaw.HasValue && (float.IsNaN(yaw.Value) || float.IsInfinity(yaw.Value)))
                return Fail(label, "yaw must be a finite number of degrees, or null for the automatic oblique");

            string camToken = "fov=" + fov.ToString("0.#", CultureInfo.InvariantCulture)
                + " yaw=" + (yaw.HasValue ? yaw.Value.ToString("0.#", CultureInfo.InvariantCulture) : "auto");

            if (whatIf)
            {
                if (!RenderThumbnailCore.ResolvePose(pose, out AnimationClip _, out string poseErr))
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
            if (!RenderThumbnailCore.ResolvePose(pose, out AnimationClip poseClip, out string renderPoseErr))
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

                    // Head facing as a DELTA FROM BIND, in the root's basis — rig-orientation-invariant (see
                    // RenderThumbnailCore.HeadFacing). This is the venue-independent extraction; only the way
                    // the head got posed (AnimationMode here vs Base-layer injection in play) differs.
                    RenderThumbnailCore.HeadFacing(baked.transform.rotation, headBone, restHeadLocal,
                        out headYaw, out headPitch);
                }

                // ---- Step 5b: expression, applied AFTER the pose and deliberately NOT through the
                // animation system. A second SampleAnimationClip re-runs the Animator's humanoid solver
                // and PARTIALLY UNDOES the pose — the left upper arm moved (301.6,303.5,76.7) ->
                // (321.6,344.4,33.2). Pose and expression bind disjoint properties, not disjoint systems.
                // Writing weights straight onto the renderers leaves the pose byte-identical.
                // (In PLAY this direct write does not render — the playable graph wins — so RenderThumbnailPlay
                // composes the expression into the FX controller instead. Edit mode has no graph, so the
                // direct write is both correct and the cheaper path here.)
                string resolvedExpression = null;
                if (wantExpression)
                {
                    var bakedExpr = RenderThumbnailCore.FindExpressionClip(baked, expression, out string bakedErr);
                    if (bakedExpr == null) throw new InvalidOperationException(bakedErr);

                    resolvedExpression = bakedExpr.name;
                    if (ApplyExpression(baked, bakedExpr) == 0)
                        throw new InvalidOperationException(
                            "expression '" + expressionName + "' (clip '" + resolvedExpression
                            + "') moved no blendshape on the baked avatar — it binds shapes this avatar "
                            + "does not have, or only meshes that are not drawn");
                }

                // ---- Step 6: camera + off-screen sRGB capture, via the shared spine ----
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
                // Deferred the BeforeForwardOpaque command buffer (the gradient) never fires.
                cam.renderingPath = RenderingPath.Forward;

                var solution = RenderThumbnailCore.SolveCamera(
                    framingToken, span, aimDrop, fov, yaw, headYaw, headPitch, viewpoint,
                    baked.transform.rotation, baked.transform.lossyScale.y,
                    descriptorBaked.ViewPosition.y, baked.transform.eulerAngles.y);
                cam.transform.position = solution.Position;
                cam.transform.rotation = solution.Rotation;
                // The rig is camera-relative by construction; without this, orbiting lights a yawed shot from
                // behind. After MakeLight, which sets world rotation and then parents worldPositionStays.
                lightHolder.transform.rotation = solution.LightHolderRotation;
                float camYaw = solution.CamYaw;

                const int W = 1200, H = 900;
                var capture = RenderThumbnailCore.Capture(cam, bgTop, bgBottom, viewpoint, W, H);
                Vector3 headViewport = capture.HeadViewport;
                bool nothingDrew = capture.Drawn == 0;

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
                    System.IO.File.WriteAllBytes(pngPath, capture.Png);
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
                // The capture core owns its own RT/readback/gradient lifecycle (destroyed inside Capture),
                // so teardown no longer chases them.
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

        /// <summary>
        /// Write the clip's <c>blendShape.*</c> curves straight onto the baked clone's renderers and
        /// return how many landed on a DRAWN one. Only blendShape curves are applied — an expression is
        /// its blendshape curves. Applying rather than sampling is what keeps the pose intact (see the
        /// call site); a zero return is the caller's signal that the clip and the baked avatar do not
        /// match. <b>Edit-mode only</b>: in play the playable graph overwrites a direct SetBlendShapeWeight,
        /// so RenderThumbnailPlay composes the expression into the FX controller instead.
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

        internal static string Fail(string label, string reason)
        {
            string msg = "[RenderThumbnail] Render " + (string.IsNullOrEmpty(label) ? "?" : label) + " => FAIL: " + reason;
            Debug.LogError(msg);
            return msg;
        }
    }
}
