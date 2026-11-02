using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>Edit-mode portrait PNG of an avatar after the full VRC SDK preprocess chain, so it shows what
    /// uploads, optimizers included. A bake gives resolved meshes that an off-screen camera renders at a fixed
    /// resolution; RenderAvatar never bakes, so it composites through the Scene View, capped to the pane's size
    /// and showing NDMF preview proxies.
    /// Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class RenderThumbnail
    {
        // 3-point directional rig with cosmetic defaults: a NewPreviewScene has no lights, so this tool supplies
        // them (play mode renders under the scene's own lights). Camera-relative: the eulers are authored for a
        // camera on the avatar's +Z side, where screen-left = +X world, and the holder is yawed to match the
        // solved camera. Shadows stay off because lilToon and Poiyomi gate shadow-receiving behind material
        // toggles vendors rarely enable, so a cast shadow is invisible on them and only costs a shadow-map
        // pass. Neither shader clips at the total intensity of 1.4 with allowHDR off.
        private const float KeyIntensity = 0.8167f;
        private static readonly Vector3 KeyEuler = new Vector3(40f, 220f, 0f);   // key : front-upper-left
        private const float FillIntensity = 0.35f;
        private static readonly Vector3 FillEuler = new Vector3(15f, 140f, 0f);  // fill: front-right, softer
        private const float RimIntensity = 0.2333f;
        private static readonly Vector3 RimEuler = new Vector3(30f, 340f, 0f);   // rim : behind + above

        // Internal API, reflected to restore a scene that was clean at snapshot. Null means it drifted, which
        // teardown reports rather than leaving a scene silently dirty.
        private static readonly System.Reflection.MethodInfo ClearSceneDirtinessMethod =
            typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(
                "ClearSceneDirtiness",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// Render a portrait PNG of the baked <paramref name="target"/> avatar at
        /// <see cref="RenderThumbnailCore.CaptureWidth"/>×<see cref="RenderThumbnailCore.CaptureHeight"/> and return a
        /// one-line verdict whose <c>png=</c> trailer is the written path.
        /// </summary>
        /// <param name="target">avatar root: scene hierarchy path, instance id, or name (refused if not unique).</param>
        /// <param name="pose">null =&gt; floor (unposed); a bundled name (the <c>Editor/Poses/RTPose_*</c>
        /// glob); or a clip asset path/GUID.</param>
        /// <param name="expression">null =&gt; no expression (a fully supported outcome); else a state name
        /// on the baked FX controller (a gesture slot such as <c>Open</c>/<c>Peace</c>), or a clip asset
        /// path/GUID, whose blendShape curves are composited with <paramref name="pose"/>. Resolved after
        /// the bake — read the controller with ReportController to see what a given avatar offers.</param>
        /// <param name="framing">"bust" | "half" | "full": the subject height the frame covers.</param>
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
        /// <param name="zoom">scale on the framing's span (0.6–1.6): &gt;1 tighter, &lt;1 wider. Jointly bounded:
        /// zoom × tan(fov/2) ≤ 1, the closest shot fov 90 already reaches, so a high zoom at a wide fov is refused.</param>
        /// <param name="pitch">camera elevation offset in degrees (±20), added to head-follow: positive =
        /// camera higher, looking down.</param>
        /// <param name="headroom">aim shift as a fraction of the framed span (±0.3): positive opens more frame
        /// above the head — the knob for a prop over the crown.</param>
        public static string Run(
            string target,
            string pose = null,
            string expression = null,
            string framing = "bust",
            string bg = null,
            float fov = 30f,
            float? yaw = null,
            bool whatIf = false,
            float zoom = 1f,
            float pitch = 0f,
            float headroom = 0f)
        {
            var handle = Ryan6Vrc.AgentTools.Editor.SceneHandle.Resolve(target);
            if (!handle.Ok) return Fail(target, handle.Refusal);
            var root = handle.Object;
            string label = root.name;

            var descriptor = root.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();
            if (descriptor == null)
                return Fail(label, "no VRC_AvatarDescriptor on '" + label + "'");

            float span, aimDrop;
            try { RenderThumbnailCore.FramingGeometry(framing, out span, out aimDrop); }
            catch (ArgumentException ex) { return Fail(label, ex.Message); }
            string framingToken = (framing ?? "bust").Trim().ToLowerInvariant();

            Color bgTop = RenderThumbnailCore.DefaultBackground, bgBottom = RenderThumbnailCore.DefaultBackground;
            if (bg != null && !RenderThumbnailCore.TryParseBg(bg, out bgTop, out bgBottom))
                return Fail(label, "unparseable bg '" + bg + "' — expected #RRGGBB, #RRGGBBAA, or #TOP:#BOTTOM");

            // Bounded because the camera closes in as 1/tan(fov/2): any nearer than fov 90 puts it inside the hair
            // mesh, which renders as hair interior and passes the empty-frame guard.
            if (!(fov >= RenderThumbnailCore.MinFov && fov <= RenderThumbnailCore.MaxFov))
                return Fail(label, "fov " + fov.ToString(CultureInfo.InvariantCulture) + " out of range — expected "
                    + RenderThumbnailCore.MinFov.ToString(CultureInfo.InvariantCulture) + "–"
                    + RenderThumbnailCore.MaxFov.ToString(CultureInfo.InvariantCulture));
            if (yaw.HasValue && (float.IsNaN(yaw.Value) || float.IsInfinity(yaw.Value)))
                return Fail(label, "yaw must be a finite number of degrees, or null for the automatic oblique");
            string knobErr = RenderThumbnailCore.ValidateCameraKnobs(fov, zoom, pitch, headroom);
            if (knobErr != null) return Fail(label, knobErr);

            string camToken = "fov=" + fov.ToString("0.#", CultureInfo.InvariantCulture)
                + " yaw=" + (yaw.HasValue ? yaw.Value.ToString("0.#", CultureInfo.InvariantCulture) : "auto")
                + " " + RenderThumbnailCore.KnobToken(zoom, pitch, headroom);

            if (whatIf)
            {
                if (!RenderThumbnailCore.ResolvePose(pose, out AnimationClip _, out string poseErr))
                    return Fail(label, poseErr);

                string poseToken = string.IsNullOrEmpty(pose) ? "floor" : pose;
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
            // activeObject, not activeGameObject: restoring the latter would null a non-GameObject selection.
            // AnimationMode is snapshotted so teardown stops it only when this run started it, never ending an
            // operator's Animation-window recording.
            UnityEngine.Object savedActive = Selection.activeObject;
            bool animModeAtStart = AnimationMode.InAnimationMode();

            Scene preview = default;
            GameObject baked = null;
            // Not in a `using`: the scope is disposed inside the teardown, after the preview scene closes, while
            // its CleanupNote is read later, when the verdict is assembled.
            AvatarBakeScope bake = null;
            string result = null;      // non-null on the silhouette-fail / non-success paths only
            string prefix = null;      // the OK verdict head, consumed after teardown once residualNote is known
            string pngPath = null;     // the written PNG path, ditto
            string residualNote = "";

            try
            {
            try
            {
                // ---- clone and bake ----
                string stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
                bake = AvatarBake.Begin(root, root.name + "__rt_" + stamp);
                if (!bake.Ok)
                {
                    string why = bake.DescribeFailure();
                    if (bake.Failure != null)
                        // Spelled into the message as well as chained: the outer catch formats the verdict from
                        // the OUTER exception, so an unspelled inner type never reaches the returned line.
                        throw new InvalidOperationException(
                            "VRC build preprocess failed on '" + label + "' — " + why, bake.Failure);
                    throw new InvalidOperationException(
                        "VRC build preprocess refused '" + label + "' — " + why
                        + (bake.EnteredChain ? " The avatar would not upload in this state either." : ""));
                }
                baked = bake.Clone;

                // ---- preview scene and lights ----
                preview = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
                SceneManager.MoveGameObjectToScene(baked, preview);

                // Lights only, no RenderSettings: those are global and would leak into and dirty the live scene.
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

                // ---- view point, then pose ----
                // The view point (the creator-authored viewball) is the camera's target. It needs no Animator:
                // the default floor path must keep rendering on a non-humanoid rig, which has no head bone.
                var descriptorBaked = baked.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();
                if (descriptorBaked == null)
                    throw new InvalidOperationException("baked clone has no VRC_AvatarDescriptor — bake dropped it");

                // Subject scale is pose-independent: SolveCamera takes ViewPosition.y and the root's lossyScale,
                // never the posed view point's world height, which drops when the avatar sits and would shrink
                // the span on exactly the seated poses. This product only gates on a usable value.
                float viewHeight = descriptorBaked.ViewPosition.y * baked.transform.lossyScale.y;
                Vector3 viewpoint = baked.transform.TransformPoint(descriptorBaked.ViewPosition);
                if (!(viewHeight > 0.01f))
                    throw new InvalidOperationException(
                        "unusable view height " + viewHeight.ToString("0.###", CultureInfo.InvariantCulture)
                        + " m on '" + label + "' — ViewPosition.y="
                        + descriptorBaked.ViewPosition.y.ToString("0.###", CultureInfo.InvariantCulture)
                        + " times root scale " + baked.transform.lossyScale.y.ToString("0.###", CultureInfo.InvariantCulture)
                        + "; the camera solve has no subject scale to work from");

                // A resolved pose is sampled and held: EndSampling leaves it applied, so StopAnimationMode must
                // not run before the capture. Teardown stops it.
                float headYaw = 0f, headPitch = 0f;   // both exactly zero on the floor path
                if (poseClip != null)
                {
                    var animator = baked.GetComponent<UnityEngine.Animator>();
                    if (animator == null)
                        throw new InvalidOperationException(
                            "baked clone has no Animator — cannot sample pose '" + poseName + "'");
                    if (!animator.isHuman)
                        // On a generic rig GetBoneTransform(Head) is null and SampleAnimationClip silently does
                        // nothing, yet the verdict would still claim pose=<name>.
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

                    // The view point rides the head through the pose. Head, not eye bones: many rigs lack them
                    // and a bake can rename them, whereas Head is mandatory on a humanoid rig.
                    Vector3 viewInHead = headBone.InverseTransformPoint(viewpoint);
                    Quaternion restHeadLocal = Quaternion.Inverse(baked.transform.rotation) * headBone.rotation;

                    AnimationMode.StartAnimationMode();
                    AnimationMode.BeginSampling();
                    try { AnimationMode.SampleAnimationClip(baked, poseClip, poseClip.length); }
                    finally { AnimationMode.EndSampling(); }

                    viewpoint = headBone.TransformPoint(viewInHead);

                    RenderThumbnailCore.HeadFacing(baked.transform.rotation, headBone, restHeadLocal,
                        out headYaw, out headPitch);
                }

                // ---- expression ----
                // Applied after the pose and straight onto the renderers, not through the animation system: a
                // second SampleAnimationClip re-runs the Animator's humanoid solver and partially undoes the
                // pose, though the two bind disjoint properties.
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

                // ---- camera and off-screen capture ----
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
                cam.allowHDR = false;                 // Linear project: Capture's sRGB RT carries the gamma
                cam.fieldOfView = fov;
                // Forward explicitly: renderingPath defaults to the project's tier settings, and under
                // Deferred the BeforeForwardOpaque command buffer (the gradient) never fires.
                cam.renderingPath = RenderingPath.Forward;

                var solution = RenderThumbnailCore.SolveCamera(
                    framingToken, span, aimDrop, fov, yaw, headYaw, headPitch, viewpoint,
                    baked.transform.rotation, baked.transform.lossyScale.y,
                    descriptorBaked.ViewPosition.y, baked.transform.eulerAngles.y, zoom, pitch, headroom);
                cam.transform.position = solution.Position;
                cam.transform.rotation = solution.Rotation;
                // After MakeLight, which sets world rotation and then parents with worldPositionStays.
                lightHolder.transform.rotation = solution.LightHolderRotation;
                float camYaw = solution.CamYaw;

                var capture = RenderThumbnailCore.Capture(cam, bgTop, bgBottom, viewpoint);
                Vector3 headViewport = capture.HeadViewport;
                bool nothingDrew = capture.Drawn == 0;

                // head is the view point's viewport position, origin bottom-left, reported and not gated: failing
                // on a bad crop would withhold the image that shows it.
                string shapesToken = wantExpression ? " (" + resolvedExpression + ")" : "";
                prefix = "[RenderThumbnail] Render " + label + " baked pose=" + poseName
                    + " expression=" + expressionName + shapesToken + " framing=" + framingToken
                    + " fov=" + fov.ToString("0.#", CultureInfo.InvariantCulture)
                    + " " + RenderThumbnailCore.KnobToken(zoom, pitch, headroom)
                    + " headYaw=" + headYaw.ToString("0.#", CultureInfo.InvariantCulture)
                    + " camYaw=" + camYaw.ToString("0.#", CultureInfo.InvariantCulture)
                    + " headPitch=" + headPitch.ToString("0.#", CultureInfo.InvariantCulture)
                    + " camPitch=" + solution.CamElevation.ToString("0.#", CultureInfo.InvariantCulture)
                    + " head=(" + headViewport.x.ToString("0.00", CultureInfo.InvariantCulture)
                    + "," + headViewport.y.ToString("0.00", CultureInfo.InvariantCulture) + ")";

                if (nothingDrew)
                {
                    // No blank PNG. The wording hedges because a frame the subject fills edge to edge also reads
                    // uniform: column 0 is then subject too.
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
                }
            }
            finally
            {
                // ---- teardown, on every exit ----
                // The nested try is there so a throw from these steps (the reflected ClearSceneDirtiness,
                // DestroyImmediate on a destroyed object) cannot skip bake.Dispose in the inner finally.
                try
                {
                if (!animModeAtStart && AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();

                if (preview.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(preview); // destroys baked + lights + camera

                // Orphan sweep: any root new in the target's scene since preRootIds. The backstop for a stray NDMF
                // intermediate, or a clone stranded by a throw before it reached the preview. It reaches that
                // clone only while the target sits in the active scene, where AvatarBake instantiates it.
                if (targetScene.IsValid())
                {
                    foreach (var go in targetScene.GetRootGameObjects())
                        if (!preRootIds.Contains(go.GetInstanceID()))
                            UnityEngine.Object.DestroyImmediate(go);
                }

                Selection.objects = savedSelection;
                Selection.activeObject = savedActive;

                foreach (var kv in sceneDirtyAtStart)
                {
                    Scene sc = kv.Key;
                    if (kv.Value || !sc.IsValid() || !sc.isDirty) continue;
                    if (ClearSceneDirtinessMethod != null)
                        ClearSceneDirtinessMethod.Invoke(null, new object[] { sc });
                    else
                        residualNote += " note=scene-dirty-uncleared: EditorSceneManager.ClearSceneDirtiness internals drifted";
                }

                }
                finally
                {
                    // Last, in a finally the body above cannot skip: closing the scope fires the SDK's paired
                    // OnPostprocessAvatar, which runs every hook's own cleanup (NDMF's temporary-asset sweep among
                    // them). Assets that outlive it are not chased. Dispose never throws, so it cannot replace the
                    // real pipeline exception with a teardown one.
                    if (bake != null)
                    {
                        bake.Dispose();
                        residualNote += bake.CleanupNote;   // null on a clean teardown; `+= null` is a no-op
                    }
                }
            }

                if (result != null) return result + residualNote;
                if (string.IsNullOrEmpty(residualNote))
                {
                    string logged = RenderThumbnailCore.WriteSessionLog("renderthumbnail", label, prefix + " => OK",
                        "# RenderThumbnail\n\n- target: `" + label + "`\n- png: " + pngPath + "\n"
                        + "- verdict line: " + prefix + " => OK\n");
                    string ok = RenderThumbnailCore.SpliceTrailers(logged, prefix + " => OK", pngPath);
                    Debug.Log(ok);
                    return ok;
                }
                // Rendered, but teardown left residue: no success-form png= token, so a machine consumer cannot
                // read this as clean success. The path is informational.
                string degraded = prefix + " => CLEANUP-FAILED — png written to " + pngPath + residualNote;
                Debug.LogError(degraded);
                return degraded;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return Fail(label, ex.GetType().Name + ": " + ex.Message) + residualNote;
            }
        }

        /// <summary>
        /// Write the clip's <c>blendShape.*</c> curves straight onto the baked clone's renderers and return how
        /// many landed on a DRAWN one; zero means the clip and the baked avatar do not match.
        /// <b>Edit-mode only</b>: in play the playable graph overwrites a direct SetBlendShapeWeight, so
        /// RenderThumbnailPlay composes the expression into the FX controller instead.
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

                // clip.length, matching the pose's sampling: for a multi-key clip this is deliberately the END state.
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
