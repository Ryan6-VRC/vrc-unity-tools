using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The venue-independent spine shared by both <see cref="RenderThumbnail"/> (edit mode) and
    /// <see cref="RenderThumbnailPlay"/> (play mode): the camera solve (framing → distance/oblique/aim),
    /// the capture core (sRGB RT → ReadPixels → empty-frame guard → PNG), head-facing extraction, and the
    /// pose/expression/target resolvers both front-ends resolve <em>identically</em>. Nothing here touches a
    /// bake, a preview scene, a playable graph, or the emulator — that split is what makes play a MODE of
    /// one tool rather than a fork of it. A front-end owns only how the avatar is produced, posed, and how
    /// its expression is applied; the geometry and the pixels are here, computed once.
    /// </summary>
    internal static class RenderThumbnailCore
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
        // FACE framings (bust, half) are a FIXED subject height in metres — a face is ~one absolute size
        // across stylized anime avatars regardless of how tall the viewpoint sits — scaled only by the
        // avatar's ROOT scale, so a tall viewpoint no longer zooms the shot out and framing reads
        // consistently across bodies. Some crown crop is DESIRABLE (a thumbnail reads small; a tight frame
        // reads as deliberate) so these do not chase full crown clearance. FULL is different: it is a BODY
        // frame, so it still tracks viewpoint height — a taller body needs a taller span to seat the feet —
        // via the per-framing follow at the camera solve below.
        private const float ReferenceViewHeight = 1.6f;
        private const float BustSpan = 0.34f, BustAimDrop = 0.12f;
        private const float HalfSpan = 0.60f, HalfAimDrop = 0.18f;
        private const float FullSpan = 2.00f, FullAimDrop = 0.37f;
        // How much FULL framing tracks viewpoint height (bust/half never track — face-sized and fixed).
        // 1 = full spans proportionally to the viewpoint so the feet stay seated on a tall body. Measured
        // across four bodies (viewpoints 1.04–1.36): the face framings hold size and full seats the feet.
        // Left as a dial for an unusually proportioned body.
        private const float ViewHeightFollow = 1f;

        // Camera angles, degrees. The camera tracks the posed head ONE-FOR-ONE (no follow coefficient): at
        // any partial coefficient the face-to-lens angle becomes a function of the pose, sweeping frontal to
        // looking-away across one library; at 1:1 it is exactly DefaultOblique everywhere, which is the
        // point. DefaultOblique then sets the shot's obliquity — measured, the bundled poses barely twist the
        // torso, so obliquity has to come from the camera. ObliqueDeadband resolves the near-frontal poses
        // (many turn the head only a few degrees) to one consistent side instead of letting retarget noise pick.
        private const float DefaultOblique = 13f;
        private const float ObliqueDeadband = 5f;
        private const float PitchFollow = 0.5f;
        private const float MaxTrackYaw = 60f;    // guards a user clip, never reached by the bundled poses
        private const float MaxCamPitch = 20f;    // ditto: PitchFollow peaks near 15 deg on this library
        private const float LateralOffset = 0.06f; // fraction of span, looking-room in the landscape frame
        internal const float MinFov = 10f, MaxFov = 90f;

        // Solid background when no bg override is passed (a preview scene has no skybox; SolidColor clear
        // is the deterministic backdrop). A named, neutral mid-dark gray.
        internal static readonly Color DefaultBackground = new Color(0.23f, 0.23f, 0.24f);
        private const int RampHeight = 256;       // gradient ramp texture, 1 x N, stretched by the blit

        // Empty-frame guard: a pixel counts as "drawn" when it differs from ITS OWN ROW's column-0 pixel by
        // more than this per-channel byte delta (~0.06 of 255). Sampling the rendered image per row — rather
        // than computing the expected background — is what keeps this color-space-agnostic and correct under
        // a gradient; a computed reference in a Linear project writing an sRGB target reads the whole
        // background as "drawn" and defeats the guard. Genuinely boolean: the fail condition is ZERO drawn
        // pixels, so there is no tuned fraction here to drift.
        private const int SilhouetteChannelThreshold = 16;

        // ===== Camera solve =====

        /// <summary>The camera placement produced by <see cref="SolveCamera"/>: everything the front-end
        /// needs to seat the capture camera and the (edit-mode) light rig, plus the resolved
        /// <see cref="CamYaw"/> for the verdict so a saturated clamp is visible rather than silent.</summary>
        internal struct CameraSolution
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Quaternion LightHolderRotation; // camera-relative rig yaw; play mode uses scene lights and ignores it
            internal float CamYaw;                   // resolved (tracking + oblique), for the verdict
        }

        /// <summary>
        /// Solve the capture camera from the posed geometry — pure, venue-independent. The camera tracks the
        /// head one-for-one, then adds the oblique; the aim drops below the view point and laterally away
        /// from the gaze; the distance is solved from the span and FOV. Identical for both front-ends: the
        /// only difference between edit and play is how <paramref name="viewpoint"/>/<paramref name="headYaw"/>
        /// were produced (AnimationMode sample vs Base-layer injection + settle).
        /// </summary>
        internal static CameraSolution SolveCamera(
            string framingToken, float span, float aimDrop, float fov, float? yaw,
            float headYaw, float headPitch, Vector3 viewpoint, Quaternion rootRot,
            float rootLossyScaleY, float viewPositionY, float rootEulerY)
        {
            // The deadband decides the oblique's side for the near-frontal poses (many of the bundled clips
            // turn the head only a few degrees) rather than letting retarget noise pick, which would swing the
            // camera 2*DefaultOblique and shoot the same pose from opposite sides on two avatars.
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
            Quaternion orbit = Quaternion.AngleAxis(camYaw, Vector3.up) * Quaternion.AngleAxis(camPitch, Vector3.right);
            Vector3 screenRight = rootRot * (orbit * Vector3.left);   // left: the camera looks back along -forward

            // bust/half are fixed subject heights (root-scaled only); full is a body frame and tracks
            // viewpoint so the feet stay seated. framingToken is already lowercased by the caller.
            float viewFollow = framingToken == "full" ? ViewHeightFollow : 0f;
            float scaledSpan = span * rootLossyScaleY
                * Mathf.Lerp(1f, viewPositionY / ReferenceViewHeight, viewFollow);
            float distance = (scaledSpan * 0.5f) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

            // Aim below the view point (the eyes sit near the TOP of the subject, not its centre) and
            // laterally away from the side the face is turned, which opens looking-room in a landscape
            // frame that would otherwise waste its width on a vertical subject.
            // Vector3.up, not the root's up: camera roll stays world-level below, so the drop that decides
            // where the head sits in frame has to share that basis. Divergent only for a tilted root.
            Vector3 aim = viewpoint
                        - Vector3.up * (scaledSpan * aimDrop)
                        + screenRight * (scaledSpan * LateralOffset) * -lateralSign;

            Vector3 pos = aim + rootRot * (orbit * (Vector3.forward * distance));
            return new CameraSolution
            {
                Position = pos,
                Rotation = Quaternion.LookRotation(aim - pos, Vector3.up),
                // The rig is camera-relative by construction; without this, orbiting lights a yawed shot from
                // behind. Edit mode applies it to its 3-point holder; play mode uses the scratch scene's own
                // lights and ignores it.
                LightHolderRotation = Quaternion.Euler(0f, rootEulerY + camYaw, 0f),
                CamYaw = camYaw,
            };
        }

        /// <summary>Yaw of a direction in degrees, signed about +Y: 0 = +Z, positive toward +X (which is
        /// screen-LEFT for a camera on the +Z side). Taken off the forward vector rather than eulers so
        /// there is no wrap or gimbal question, and so it matches the measured pose-angle table.</summary>
        internal static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        /// <summary>Elevation of a direction in degrees. Positive = pointing up (chin raised).</summary>
        internal static float PitchOf(Vector3 dir) =>
            Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        /// <summary>
        /// Head facing as a DELTA FROM BIND, in the root's basis, extracted off the forward vector.
        /// Unity does not normalize humanoid bone axes, so anything derived from where the bone's own +Z
        /// points is rig-dependent. Take the DELTA ROTATION FIRST, then extract once — extracting an angle
        /// from each forward vector and subtracting is NOT equivalent: it cancels a constant offset but not
        /// the axis dependence, and it fails hardest on the most common convention (a bone whose +Z runs UP
        /// the neck — how Blender authors orient a bone, most of the VRChat population): subtract-form yields
        /// yaw 0.0 (tracking silently dead) and inverts pitch (a chin-up pose shot from below). The delta
        /// form returns the true 30/+20 for every rest orientation, including arbitrary skew.
        /// </summary>
        internal static void HeadFacing(Quaternion rootRot, Transform headBone, Quaternion restHeadLocal,
            out float headYaw, out float headPitch)
        {
            Quaternion posedHeadLocal = Quaternion.Inverse(rootRot) * headBone.rotation;
            Vector3 deltaFwd = (posedHeadLocal * Quaternion.Inverse(restHeadLocal)) * Vector3.forward;
            headYaw = YawOf(deltaFwd);      // Atan2 already yields [-180,180]
            headPitch = PitchOf(deltaFwd);
        }

        // ===== Capture core =====

        /// <summary>The outcome of a <see cref="Capture"/>: whether anything drew, the PNG bytes (null when
        /// nothing drew — the front-end fails loud rather than writing a blank), and where the view point
        /// landed in the frame (reported, not gated — a bad crop is visible in the PNG).</summary>
        internal struct CaptureResult
        {
            internal int Drawn;
            internal byte[] Png;
            internal Vector3 HeadViewport;
        }

        /// <summary>
        /// Render an already-positioned, already-culled <paramref name="cam"/> to a fixed W×H sRGB target and
        /// read it back: optional gradient backdrop, off-screen render, empty-frame guard, PNG encode. Owns the
        /// full lifecycle of the RT/readback texture/gradient ramp/command buffer so a throw mid-capture leaks
        /// nothing. The front-end configures the camera (clear, background = <paramref name="bgTop"/>,
        /// cullingMask, fov, scene) and supplies the view point to project; everything from the render down is
        /// identical across venues.
        /// </summary>
        internal static CaptureResult Capture(Camera cam, Color bgTop, Color bgBottom, Vector3 viewpoint, int W, int H)
        {
            RenderTexture rt = null;
            Texture2D tex = null;
            Texture2D ramp = null;
            UnityEngine.Rendering.CommandBuffer bgCmd = null;
            try
            {
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

                rt = new RenderTexture(W, H, 24, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB)
                {
                    // sRGB is MANDATORY: the project is Linear; a default linear RT ships a dark, wrong-gamma PNG.
                    antiAliasing = Math.Max(1, QualitySettings.antiAliasing)
                };
                cam.targetTexture = rt;
                cam.pixelRect = new Rect(0, 0, W, H);
                cam.Render();

                // Where the face landed, while the camera still exists. The VIEW POINT, not the aim — the camera
                // is aimed exactly at the aim by construction, so projecting that would print (0.5,0.5) forever.
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
                    RenderTexture.active = prevActive;
                }

                // ---- Empty-frame guard: did ANYTHING draw? ----
                // Reference = each row's own column-0 pixel of the read-back texture, NOT a computed color.
                // Sampling the render is what keeps this color-space-agnostic (a computed reference in a Linear
                // project writing an sRGB target reads the whole background as "drawn" and defeats the guard),
                // and per-row is what makes it exact under a vertical gradient, which is constant along a row.
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

                byte[] png = drawn > 0 ? tex.EncodeToPNG() : null;
                return new CaptureResult { Drawn = drawn, Png = png, HeadViewport = headViewport };
            }
            finally
            {
                if (bgCmd != null)
                {
                    // Remove before the camera may be destroyed by a preview-scene close; CommandBuffer wraps
                    // native memory and the ramp is asset-less, so both leak without explicit disposal.
                    if (cam != null) cam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.BeforeForwardOpaque, bgCmd);
                    bgCmd.Dispose();
                }
                if (ramp != null) UnityEngine.Object.DestroyImmediate(ramp);
                if (rt != null)
                {
                    if (cam != null) cam.targetTexture = null;
                    UnityEngine.Object.DestroyImmediate(rt);
                }
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        // ===== Framing / background / token helpers (pure) =====

        /// <summary>The subject height a named framing covers, in metres, and how far below the view point
        /// to aim, as a fraction of that span. Throws for anything else. The face framings (bust/half) are
        /// taken as absolute metres scaled only by the avatar's root scale; full additionally tracks the
        /// avatar's viewpoint height (see the per-framing follow at the camera solve).
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
        internal static UnityEditor.Animations.AnimatorControllerLayer[] FxLayers(GameObject baked)
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

        internal static bool IsGuid(string s)
        {
            if (s.Length != 32) return false;
            foreach (char ch in s)
                if (!Uri.IsHexDigit(ch)) return false;
            return true;
        }

        // ===== Target resolution: hierarchy-path -> instance-id -> name (mirrors RenderAvatar's resolver) =====

        internal static GameObject Resolve(string target)
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
    }
}
