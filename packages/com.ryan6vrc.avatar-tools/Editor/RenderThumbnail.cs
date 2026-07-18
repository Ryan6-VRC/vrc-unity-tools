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

        // Dolly-back distance (meters, added along camera local-forward beyond the SDK-calibrated
        // PositionPortraitCamera transform) per framing. Cosmetic taste defaults — bust is the SDK's own
        // distance (no dolly); half/full back off for more body. Tune after seeing real output.
        private const float BustFramingDistance = 0f;
        private const float HalfFramingDistance = 0.45f;
        private const float FullFramingDistance = 1.1f;

        // Solid background when no bg override is passed (a preview scene has no skybox; SolidColor clear
        // is the deterministic backdrop). A named, neutral mid-dark gray.
        private static readonly Color DefaultBackground = new Color(0.23f, 0.23f, 0.24f);

        // 3-point directional rig — cosmetic TASTE DEFAULTS, named + here so the operator tunes them after
        // seeing real output. For a directional light only rotation matters. The avatar faces +Z and the
        // portrait camera sits on the +Z side looking -Z, so (from the camera's view) screen-left = +X world
        // and screen-right = -X world; each euler below aims the light's forward (photon direction) onto the
        // avatar accordingly. Shadows off in v1 for a clean, artifact-free portrait.
        private const float KeyIntensity = 1.2f;
        private static readonly Vector3 KeyEuler = new Vector3(40f, 220f, 0f);   // key : front-upper-left
        private const float FillIntensity = 0.5f;
        private static readonly Vector3 FillEuler = new Vector3(15f, 140f, 0f);  // fill: front-right, softer
        private const float RimIntensity = 1.0f;
        private static readonly Vector3 RimEuler = new Vector3(30f, 340f, 0f);   // rim : behind + above

        // Silhouette measurement: a pixel counts as "drawn" when it differs from the sampled corner
        // background by more than this per-channel byte delta (~0.06 of 255); a run whose drawn fraction
        // is below MinSilhouetteFraction fails loud (nothing rendered).
        private const int SilhouetteChannelThreshold = 16;
        private const float MinSilhouetteFraction = 0.005f;

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
        /// <param name="pose">null =&gt; floor (unposed); a bundled token (see <see cref="PoseCatalog"/>);
        /// or a clip asset path/GUID.</param>
        /// <param name="expression">null =&gt; no expression (a fully supported outcome); else a gesture
        /// slot (<c>Open</c>, <c>Peace</c>), a clip name, or a clip asset path/GUID, composited with
        /// <paramref name="pose"/>. Slot and clip name resolve against the BAKED FX controller; a
        /// path/GUID is an escape hatch loaded as given. An unknown value enumerates what this avatar
        /// offers.</param>
        /// <param name="framing">"bust" | "half" | "full" — dolly distance over the SDK's
        /// PositionPortraitCamera transform.</param>
        /// <param name="bg">null =&gt; default backdrop; "#RRGGBB" =&gt; solid color.</param>
        /// <param name="whatIf">preflight only: resolve target/descriptor/pose/expression, report, bake
        /// nothing.</param>
        public static string Render(
            string target,
            string pose = null,
            string expression = null,
            string framing = "bust",
            string bg = null,
            bool whatIf = false)
        {
            var root = Resolve(target);
            if (root == null)
                return Fail(target, "target not found — tried hierarchy path, instance id, then name in the active scene");
            string label = root.name;

            var descriptor = root.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();
            if (descriptor == null)
                return Fail(label, "no VRC_AvatarDescriptor on '" + label + "'");

            // Validate framing + bg up front so BOTH whatIf and the render path reject a bad framing/bg before
            // proceeding — a whatIf WOULD-RENDER must never precede an immediate render FAIL on the same inputs.
            float dolly;
            try { dolly = FramingDistance(framing); }
            catch (ArgumentException ex) { return Fail(label, ex.Message); }
            string framingToken = (framing ?? "bust").Trim().ToLowerInvariant();

            Color bgColor = DefaultBackground;
            if (bg != null && !TryParseBg(bg, out bgColor))
                return Fail(label, "unparseable bg '" + bg + "' — expected #RRGGBB or #RRGGBBAA");

            if (whatIf)
            {
                if (!ResolvePose(pose, out AnimationClip _, out string poseErr))
                    return Fail(label, poseErr);
                if (!PreflightExpression(expression, out string exprErr, out bool exprDeferred))
                    return Fail(label, exprErr);

                string poseToken = string.IsNullOrEmpty(pose) ? "floor" : pose;
                string exprToken = string.IsNullOrEmpty(expression) ? "none" : expression;
                if (exprDeferred) exprToken += " (resolved at bake)";
                string ok = string.Format(CultureInfo.InvariantCulture,
                    "[RenderThumbnail] Render {0} whatIf pose={1} expression={2} descriptor=OK => WOULD-RENDER (no bake)",
                    label, poseToken, exprToken);
                Debug.Log(ok);
                return ok;
            }

            // ---- Preflight resolution (fail fast, before any mutation) ----
            if (!ResolvePose(pose, out AnimationClip poseClip, out string renderPoseErr))
                return Fail(label, renderPoseErr);
            if (!PreflightExpression(expression, out string renderExprErr, out bool _))
                return Fail(label, renderExprErr);
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

                // ---- Step 5: pose ----
                // The floor path (poseClip == null) samples nothing — the baked clone renders at rest,
                // byte-for-byte the Task-1 path. A resolved pose is SAMPLED AND HELD here: StopAnimationMode
                // is deliberately NOT called before cam.Render() below — EndSampling leaves the pose applied,
                // it does not revert it. Reverting happens once, defensively, in the outer teardown `finally`
                // (below) after capture, so a throw mid-sampling still can't leak animation-mode state.
                // How much the pose moves the head RELATIVE TO THE ROOT — the part PositionPortraitCamera does
                // NOT handle. Sampling also moves the avatar root to origin (undoing NDMF's +2 Z shift), but the
                // SDK's framing runs AFTER sampling so it already accounts for the root move; a world-space head
                // delta would double-count it and fling the camera past the avatar. Root-local isolates the
                // head's own drop. Zero on the floor path, so that branch stays byte-for-byte unchanged; Step 6
                // adds it to follow the head.
                Vector3 poseHeadDelta = Vector3.zero;
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
                    var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                    Vector3 restHead = headBone != null ? headBone.position : baked.transform.position;
                    Vector3 restRoot = baked.transform.position;
                    animator.Rebind();
                    AnimationMode.StartAnimationMode();
                    AnimationMode.BeginSampling();
                    try { AnimationMode.SampleAnimationClip(baked, poseClip, poseClip.length); }
                    finally { AnimationMode.EndSampling(); }
                    Vector3 posedHead = headBone != null ? headBone.position : baked.transform.position;
                    Vector3 posedRoot = baked.transform.position;
                    poseHeadDelta = (posedHead - posedRoot) - (restHead - restRoot);
                }

                // ---- Step 5b: expression, applied AFTER the pose and deliberately NOT through the
                // animation system. A second SampleAnimationClip re-runs the Animator's humanoid solver
                // and PARTIALLY UNDOES the pose — the left upper arm moved (301.6,303.5,76.7) ->
                // (321.6,344.4,33.2). Pose and expression bind disjoint properties, not disjoint systems.
                // Writing weights straight onto the renderers leaves the pose byte-identical.
                int shapesApplied = 0, shapesTotal = 0, shapesIgnored = 0;
                string resolvedExpression = null;
                if (wantExpression)
                {
                    // AUTHORITATIVE resolution: on the baked clone, whose FX controller holds the clips the
                    // bake actually rewrote. A slot selector therefore lands on the post-bake clip, whose
                    // bindings match the post-bake meshes by construction.
                    if (!ResolveExpressionOn(baked, expression, out AnimationClip bakedExpr, out string bakedErr))
                        throw new InvalidOperationException("after bake: " + bakedErr);

                    resolvedExpression = bakedExpr.name;
                    shapesApplied = ApplyExpression(baked, bakedExpr, out shapesTotal, out shapesIgnored);
                    if (shapesApplied == 0)
                        throw new InvalidOperationException(
                            "expression '" + expressionName + "' (resolved to clip '" + resolvedExpression
                            + "') moved no blendshape on the BAKED avatar — 0 of " + shapesTotal
                            + " blendShape curves landed on a live renderer. The bake renamed, merged, or "
                            + "dropped the face mesh, or the clip belongs to a different body.");
                }

                // ---- Step 6: camera + off-screen sRGB capture (§Capture, source-verified) ----
                var descriptorBaked = baked.GetComponent<VRC.SDKBase.VRC_AvatarDescriptor>();
                if (descriptorBaked == null)
                    throw new InvalidOperationException("baked clone has no VRC_AvatarDescriptor — bake dropped it");

                var camGO = EditorUtility.CreateGameObjectWithHideFlags("__rt_cam", HideFlags.DontSave, typeof(Camera));
                SceneManager.MoveGameObjectToScene(camGO, preview);
                var cam = camGO.GetComponent<Camera>();
                cam.enabled = false;
                cam.scene = preview;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = bgColor;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                cam.cullingMask = ~(1 << 5); // exclude UI layer (5) — world-space canvases would render into the thumbnail
                cam.allowHDR = false;                 // Linear project — the sRGB RT below carries the gamma
                // FOV: DO NOT SET — leave Unity's default 60°; PositionPortraitCamera is calibrated to it.
                descriptorBaked.PositionPortraitCamera(cam.transform);
                cam.transform.position -= cam.transform.forward * dolly;   // framing dolly (bust = 0)
                cam.transform.position += poseHeadDelta; // follow the head when a pose reset the body position (floor => zero)

                const int W = 1200, H = 900;
                rt = new RenderTexture(W, H, 24, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB)
                {
                    // sRGB is MANDATORY: the project is Linear; a default linear RT ships a dark, wrong-gamma PNG.
                    antiAliasing = Math.Max(1, QualitySettings.antiAliasing)
                };
                cam.targetTexture = rt;
                cam.pixelRect = new Rect(0, 0, W, H);
                cam.Render();

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

                // ---- Silhouette coverage = fraction of pixels differing from the background ----
                // Reference = a SAMPLED corner pixel of the read-back texture, NOT a computed color. This is
                // color-space-agnostic: it sidesteps the Linear-project/sRGB-RT gamma bookkeeping entirely
                // (a computed bgColor.gamma read the whole background as "drawn" → silhouette 100%, and also
                // defeated the empty-guard). GetPixel(0,0) is a background corner (bottom-left in Unity's
                // texture coords) — a corner is background for any portrait framing.
                // Reported, not gated on a tuned middle threshold — only ~0 fails.
                Color32 bgRef = tex.GetPixel(0, 0);
                Color32[] pixels = tex.GetPixels32();
                int drawn = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    if (Math.Abs(p.r - bgRef.r) > SilhouetteChannelThreshold
                        || Math.Abs(p.g - bgRef.g) > SilhouetteChannelThreshold
                        || Math.Abs(p.b - bgRef.b) > SilhouetteChannelThreshold)
                        drawn++;
                }
                float fraction = pixels.Length > 0 ? (float)drawn / pixels.Length : 0f;
                int pct = Mathf.RoundToInt(fraction * 100f);

                // Everything about the expression that a reader needs to judge the portrait: which clip the
                // selector actually resolved to post-bake, how many curves landed on a DRAWN renderer, and
                // how many non-blendShape curves this tool does not apply at all.
                string shapesToken = "";
                if (wantExpression)
                {
                    shapesToken = "(" + resolvedExpression + ") shapes="
                        + shapesApplied.ToString(CultureInfo.InvariantCulture)
                        + "/" + shapesTotal.ToString(CultureInfo.InvariantCulture);
                    if (shapesIgnored > 0)
                        shapesToken += " ignored=" + shapesIgnored.ToString(CultureInfo.InvariantCulture);
                    shapesToken = " " + shapesToken;
                }
                prefix = "[RenderThumbnail] Render " + label + " baked pose=" + poseName
                    + " expression=" + expressionName + shapesToken + " framing="
                    + framingToken + " silhouette=" + pct.ToString(CultureInfo.InvariantCulture) + "%";

                if (fraction < MinSilhouetteFraction)
                {
                    // Fail loud, uniform (=> FAIL:), keeping the REAL measured values — do NOT write a blank PNG.
                    result = Fail(label, "silhouette " + pct.ToString(CultureInfo.InvariantCulture) + "% below "
                        + MinSilhouetteFraction.ToString(CultureInfo.InvariantCulture) + " (pose=" + poseName
                        + " expression=" + expressionName + " framing=" + framingToken + ") — nothing drew");
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
                // ---- Step 7: teardown — runs on EVERY exit (success or throw). Every item guarded so a throw
                // partway through setup still tears down cleanly. Order matters. ----
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);

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

                // The SDK's preprocess/postprocess pair is a contract: OnPreprocessAvatar was called, so
                // OnPostprocessAvatar must be too. It fires every hook's OWN cleanup (NDMF's temporary-asset
                // sweep among them) rather than this tool guessing folder names it does not control.
                // Generated assets that outlive it are not chased — every project accumulates some.
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
        /// Write the clip's <c>blendShape.*</c> curves straight onto the baked clone's renderers.
        /// Returns how many landed; <paramref name="total"/> receives how many were tried and
        /// <paramref name="ignored"/> how many non-blendShape curves were passed over (an expression IS
        /// its blendshape curves, so nothing else is applied — but a caller must be able to see that a
        /// clip carrying toggles or material swaps renders only partly).
        /// <para>Applying rather than sampling is what keeps the pose intact (see the call site). The
        /// counts are what keep the verdict honest: a clip authored for a different body, or a bake that
        /// renamed the face mesh, lands fewer than it tried.</para>
        /// </summary>
        private static int ApplyExpression(GameObject baked, AnimationClip clip, out int total, out int ignored)
        {
            int applied = 0;
            total = 0;
            ignored = 0;
            // Skip causes are counted separately so a zero/partial landing NAMES its reason instead of
            // leaving the reader a four-way guess.
            int pathMiss = 0, noMesh = 0, shapeMiss = 0, notDrawn = 0, emptyCurve = 0;

            // Material and sprite swaps are objectReference curves, a SEPARATE list from the float curves
            // below — counting only the float list would report ignored=0 for a clip that visibly swaps a
            // material, which is precisely the "renders only partly" case ignored= exists to reveal.
            ignored += AnimationUtility.GetObjectReferenceCurveBindings(clip).Length;

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.propertyName == null
                    || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    // Counted, not silently dropped: a clip carrying transform/material/toggle curves is
                    // only PARTLY rendered by this tool, and the caller has to be able to see that.
                    ignored++;
                    continue;
                }
                total++;

                Transform t = string.IsNullOrEmpty(binding.path)
                    ? baked.transform
                    : baked.transform.Find(binding.path);
                if (t == null) { pathMiss++; continue; }

                var smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr == null || smr.sharedMesh == null) { noMesh++; continue; }

                // A weight written to a disabled or inactive renderer changes nothing visible. Counting it
                // as "landed" is exactly the lie shapes=N/N must not tell — optimizers and MA routinely
                // leave blush/star-eye meshes inactive by default.
                if (!smr.enabled || !smr.gameObject.activeInHierarchy) { notDrawn++; continue; }

                int index = smr.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring("blendShape.".Length));
                if (index < 0) { shapeMiss++; continue; }

                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) { emptyCurve++; continue; }

                // Evaluate at clip.length, matching the pose's sample-at-clip.length convention. Curves
                // default to ClampForever, so for the single-key clips expressions almost always are this
                // is the only key; for a multi-key clip it is deliberately the END state.
                smr.SetBlendShapeWeight(index, curve.Evaluate(clip.length));
                applied++;
            }

            if (applied == 0 && total > 0)
                Debug.LogWarning("[RenderThumbnail] expression clip '" + clip.name + "' landed nothing — "
                    + "pathMiss=" + pathMiss + " noMesh=" + noMesh + " notDrawn=" + notDrawn
                    + " shapeMiss=" + shapeMiss + " emptyCurve=" + emptyCurve);
            return applied;
        }

        // ===== Pure helpers (unit-tested; do not touch the scene or the asset database beyond reads) ====

        /// <summary>Dolly-back distance in meters for a named framing. Throws for anything else.</summary>
        internal static float FramingDistance(string framing)
        {
            switch ((framing ?? "").Trim().ToLowerInvariant())
            {
                case "bust": return BustFramingDistance;
                case "half": return HalfFramingDistance;
                case "full": return FullFramingDistance;
                default:
                    throw new ArgumentException(
                        "unknown framing '" + framing + "' — valid: bust, half, full", nameof(framing));
            }
        }

        /// <summary>Parses a solid backdrop color. Hex only (<c>#RRGGBB</c>/<c>#RRGGBBAA</c>) — a leading
        /// '#' is required even though <see cref="ColorUtility.TryParseHtmlString"/> would otherwise also
        /// accept some CSS color names, which this tool's contract deliberately excludes.</summary>
        internal static bool TryParseBg(string s, out Color c)
        {
            c = default;
            if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
            return ColorUtility.TryParseHtmlString(s, out c);
        }

        /// <summary>One bundled pose: <c>Token</c> is the normalized match key, <c>Label</c> the readable
        /// name as it appears on disk (what the unknown-pose error advertises — any casing/punctuation of
        /// it normalizes back to Token), and <c>Path</c> the asset it loads.</summary>
        internal struct PoseEntry
        {
            public string Token;
            public string Label;
            public string Path;
        }

        /// <summary>
        /// The bundled pose vocabulary, globbed from <see cref="PosesFolder"/>: every
        /// <c>RTPose_&lt;Name&gt;.anim</c> becomes the token <c>normalize(&lt;Name&gt;)</c>. Sorted for a
        /// stable error message. Content-driven by design — adding a pose is dropping a file.
        /// </summary>
        internal static List<PoseEntry> PoseCatalog()
        {
            var entries = new List<PoseEntry>();
            if (!AssetDatabase.IsValidFolder(PosesFolder)) return entries;

            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { PosesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets descends child folders; a Poses/Archive/RTPose_Old.anim must not become
                // advertised vocabulary, so the glob is pinned to this folder exactly.
                if (System.IO.Path.GetDirectoryName(path).Replace('\\', '/') != PosesFolder) continue;
                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith("RTPose_", StringComparison.OrdinalIgnoreCase)) continue;
                entries.Add(new PoseEntry
                {
                    Token = NormalizeToken(file),
                    Label = file.Substring("RTPose_".Length),
                    Path = path,
                });
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.Token, b.Token));

            return entries;
        }

        /// <summary>
        /// Fold a pose name to its match key: drop a leading <c>RTPose_</c>, lowercase, and strip every
        /// non-alphanumeric character. This is what makes <c>hand-on-hip</c>, <c>hand_on_hip</c> and
        /// <c>HandOnHip</c> the same token, replacing the old hand-maintained name→PascalCase switch.
        /// </summary>
        internal static string NormalizeToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.StartsWith("RTPose_", StringComparison.OrdinalIgnoreCase)) s = s.Substring("RTPose_".Length);

            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>
        /// Resolve <paramref name="pose"/> to a clip: null/empty =&gt; floor (<paramref name="clip"/> null,
        /// no error); a bundled token (see <see cref="PoseCatalog"/>, matched via
        /// <see cref="NormalizeToken"/>) =&gt; that package clip; else a value containing '/' or a 32-hex
        /// GUID =&gt; loaded as an asset path/GUID; else a named FAIL enumerating the globbed vocabulary.
        /// Any loaded clip that is not <c>isHumanMotion</c> (a generic/transform clip) fails loud here,
        /// before any bake — such a clip would not retarget across rigs.
        /// </summary>
        internal static bool ResolvePose(string pose, out AnimationClip clip, out string err)
        {
            clip = null;
            err = null;
            if (string.IsNullOrEmpty(pose)) return true; // floor

            string trimmed = pose.Trim();
            var catalog = PoseCatalog();

            // Two files normalizing to one token (RTPose_Hand_On_Hip vs RTPose_HandOnHip) would make the
            // winner depend on FindAssets order through an unstable sort while the error advertised both
            // as reachable. The glob's whole point is that poses land without anyone reviewing this file,
            // which is exactly when such a collision ships unnoticed. Surfaced as a FAIL verdict rather
            // than an exception: every consumer parses the one-line grammar, and a throw here would also
            // poison calls that pass no pose at all.
            for (int i = 1; i < catalog.Count; i++)
                if (catalog[i].Token == catalog[i - 1].Token)
                {
                    err = "two bundled poses normalize to the same token '" + catalog[i].Token + "': "
                        + catalog[i - 1].Path + " and " + catalog[i].Path
                        + " — rename one (tokens ignore case and punctuation)";
                    return false;
                }

            // A bundled token never contains '/', so the vocabulary lookup can safely precede the
            // path/GUID branch.
            if (trimmed.IndexOf('/') < 0)
            {
                string key = NormalizeToken(trimmed);
                foreach (var entry in catalog)
                {
                    if (entry.Token != key) continue;

                    clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.Path);
                    if (clip == null)
                    {
                        err = "bundled pose '" + entry.Token + "' did not load from " + entry.Path;
                        return false;
                    }
                    if (!clip.isHumanMotion)
                    {
                        err = "clip '" + entry.Path + "' is not a humanoid muscle clip (isHumanMotion=false)";
                        clip = null;
                        return false;
                    }
                    return true;
                }
            }

            string assetPath = null;
            if (trimmed.IndexOf('/') >= 0)
            {
                assetPath = trimmed;
            }
            else if (IsGuid(trimmed))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(trimmed);
                if (string.IsNullOrEmpty(assetPath))
                {
                    err = "GUID '" + trimmed + "' did not resolve to any asset";
                    return false;
                }
            }

            if (assetPath != null)
            {
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (clip == null)
                {
                    err = "no AnimationClip found at '" + assetPath + "'";
                    return false;
                }
                if (!clip.isHumanMotion)
                {
                    err = "clip '" + assetPath + "' is not a humanoid muscle clip (isHumanMotion=false)";
                    clip = null;
                    return false;
                }
                return true;
            }

            var tokens = new List<string>();
            foreach (var entry in catalog) tokens.Add(entry.Label);
            err = "unknown pose '" + pose + "' — bundled: "
                + (tokens.Count > 0 ? string.Join(", ", tokens.ToArray()) : "(none under " + PosesFolder + ")")
                + "; or pass a clip asset path/GUID";
            return false;
        }

        /// <summary>
        /// Pre-bake gate for <c>expression</c>, deliberately narrow: it rejects only what a pre-bake read
        /// can actually adjudicate — an asset selector (path/GUID) that will not load or is not an
        /// expression clip. A bare slot or clip name is <b>deferred</b> (<paramref name="deferred"/> true),
        /// never rejected.
        /// <para>Gesture layers are frequently <i>installed during preprocess</i>: on a VRCFury/MA avatar
        /// the descriptor's FX slot is often <c>isDefault</c> (no controller at all) or the stock hands
        /// layer whose states hold <c>proxy_hands_*</c> muscle clips. Enumerating pre-bake there yields
        /// nothing, so gating on it would hard-fail exactly the composed avatars this tool exists for —
        /// and would empty the discovery route besides. The authoritative resolve happens on the baked
        /// clone.</para>
        /// </summary>
        internal static bool PreflightExpression(string expression, out string err, out bool deferred)
        {
            err = null;
            deferred = false;
            if (string.IsNullOrEmpty(expression)) return true;

            if (!IsAssetSelector(expression.Trim()))
            {
                deferred = true;   // a slot/clip name is only knowable post-bake
                return true;
            }
            return ResolveExpressionOn(null, expression, out AnimationClip _, out err);
        }

        /// <summary>True when the selector names an asset (contains '/' or is a 32-hex GUID) rather than a
        /// gesture slot or clip name.</summary>
        private static bool IsAssetSelector(string s)
        {
            return s.IndexOf('/') >= 0 || IsGuid(s);
        }

        /// <summary>One selectable expression: the gesture <c>Slot</c> it sits on (the portable name —
        /// state names like <c>Peace</c>/<c>Open</c> are stable across vendors where clip names are not),
        /// the <c>ClipName</c>, and the <c>Clip</c> itself.</summary>
        internal struct ExpressionEntry
        {
            public string Slot;
            public string ClipName;
            public AnimationClip Clip;
        }

        /// <summary>
        /// The avatar's selectable expressions, from its FX controller's gesture layers (1–2 by the
        /// VRChat convention — worlds depend on gesture expressions living there, so authors and
        /// optimizers hold to it). <see cref="IsExpressionClip"/> gates membership, which drops the
        /// 0-binding <c>Dummy</c>/<c>Empty</c> idles.
        /// <para><b>Call this on the BAKED avatar.</b> The bake renames and merges, so a clip from the
        /// pre-bake asset can bind shapes the baked avatar no longer has. The slot survives the bake;
        /// the clip's identity does not.</para>
        /// </summary>
        internal static List<ExpressionEntry> ExpressionCatalog(GameObject avatarRoot)
        {
            var entries = new List<ExpressionEntry>();
            if (avatarRoot == null) return entries;

            var descriptor = avatarRoot.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null || descriptor.baseAnimationLayers == null) return entries;

            UnityEditor.Animations.AnimatorController fx = null;
            foreach (var layer in descriptor.baseAnimationLayers)
                if (layer.type == VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType.FX)
                    fx = layer.animatorController as UnityEditor.Animations.AnimatorController;
            if (fx == null) return entries;

            // Deduped on (slot, clip) — NOT on the clip alone. Layers 1 and 2 mirror each other, which is
            // what needs collapsing, but vendors also map several slots to ONE clip (Victory and Peace both
            // playing the same smile). Keying on the clip would register whichever slot came first and make
            // the other a hard miss on a name the operator can read in the Animator.
            var seen = new HashSet<string>();
            for (int i = 1; i <= 2 && i < fx.layers.Length; i++)
                CollectExpressionStates(fx.layers[i].stateMachine, seen, entries);
            return entries;
        }

        /// <summary>Walk a gesture layer's states and sub-state-machines, registering the first expression
        /// clip each state offers. A state's motion may be a BlendTree (analog-gesture layers), whose
        /// children are searched rather than skipped.</summary>
        private static void CollectExpressionStates(
            UnityEditor.Animations.AnimatorStateMachine sm, HashSet<string> seen, List<ExpressionEntry> entries)
        {
            if (sm == null) return;

            foreach (var cs in sm.states)
            {
                var clip = FirstExpressionClip(cs.state.motion);
                if (clip == null) continue;
                if (!seen.Add(cs.state.name + " " + clip.name)) continue;
                entries.Add(new ExpressionEntry { Slot = cs.state.name, ClipName = clip.name, Clip = clip });
            }
            foreach (var child in sm.stateMachines)
                CollectExpressionStates(child.stateMachine, seen, entries);
        }

        /// <summary>The first clip in a motion (itself, or depth-first through a BlendTree) that reads as a
        /// facial expression. Null when the motion offers none.</summary>
        private static AnimationClip FirstExpressionClip(Motion motion)
        {
            if (motion is AnimationClip clip)
                return IsExpressionClip(clip, out string _) ? clip : null;

            if (motion is UnityEditor.Animations.BlendTree tree)
                foreach (var child in tree.children)
                {
                    var hit = FirstExpressionClip(child.motion);
                    if (hit != null) return hit;
                }
            return null;
        }

        /// <summary>
        /// Resolve <paramref name="expression"/> against <paramref name="avatarRoot"/>: null/empty =&gt; no
        /// expression (<paramref name="clip"/> null, no error — the supported outcome for an avatar with no
        /// facial clips, not a degraded one); a gesture slot or clip name (matched via
        /// <see cref="NormalizeToken"/> against <see cref="ExpressionCatalog"/>); or an asset path/GUID as
        /// an escape hatch. There is no BUNDLED vocabulary — expressions are avatar-specific — but the
        /// avatar supplies its own, which the unknown-expression error enumerates.
        /// </summary>
        internal static bool ResolveExpressionOn(
            GameObject avatarRoot, string expression, out AnimationClip clip, out string err)
        {
            clip = null;
            err = null;
            if (string.IsNullOrEmpty(expression)) return true; // no expression

            string trimmed = expression.Trim();

            // Escape hatch: an explicit asset. Honoured as given — but a pre-bake asset may bind shape
            // names the baked avatar no longer has, which the verdict's shapes=applied/total then shows.
            string assetPath = null;
            if (trimmed.IndexOf('/') >= 0) assetPath = trimmed;
            else if (IsGuid(trimmed))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(trimmed);
                if (string.IsNullOrEmpty(assetPath))
                {
                    err = "GUID '" + trimmed + "' did not resolve to any asset";
                    return false;
                }
            }

            if (assetPath != null)
            {
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                if (clip == null)
                {
                    err = "no AnimationClip found at '" + assetPath + "'";
                    return false;
                }
                if (!IsExpressionClip(clip, out string why))
                {
                    err = "clip '" + assetPath + "' " + why;
                    clip = null;
                    return false;
                }
                return true;
            }

            var catalog = ExpressionCatalog(avatarRoot);
            string key = NormalizeToken(trimmed);
            foreach (var entry in catalog)
                if (NormalizeToken(entry.Slot) == key || NormalizeToken(entry.ClipName) == key)
                {
                    clip = entry.Clip;
                    return true;
                }

            if (catalog.Count == 0)
            {
                err = "expression '" + expression + "' — this avatar exposes no facial expressions on its "
                    + "FX gesture layers (nothing on layers 1-2 binds blendShape curves). Render without "
                    + "an expression, or pass a clip asset path/GUID explicitly";
                return false;
            }
            var offered = new List<string>();
            foreach (var entry in catalog) offered.Add(entry.Slot + "=" + entry.ClipName);
            err = "unknown expression '" + expression + "' — this avatar offers: "
                + string.Join(", ", offered.ToArray()) + "; or pass a clip asset path/GUID";
            return false;
        }

        /// <summary>
        /// A facial expression clip is the mirror image of a pose clip: NOT <c>isHumanMotion</c> (muscle
        /// curves would sample over the pose rather than composite with it) and carrying at least one
        /// <c>blendShape.*</c> binding. Measured on real vendor FX: expression clips are 100%
        /// blendShape curves on the face mesh, while gesture/pose clips are 100% muscle — the two bind
        /// disjoint sets and cannot fight, which is what makes compositing them safe.
        /// </summary>
        internal static bool IsExpressionClip(AnimationClip clip, out string err)
        {
            err = null;
            if (clip == null) { err = "is null"; return false; }

            if (clip.isHumanMotion)
            {
                err = "is a humanoid muscle clip (isHumanMotion=true) — that is a body pose, pass it as `pose`";
                return false;
            }

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (binding.propertyName != null
                    && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    return true;

            err = "has no blendShape.* curves — not a facial expression";
            return false;
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
