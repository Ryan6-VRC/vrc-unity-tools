using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The venue-independent spine of <see cref="RenderThumbnail"/> (edit mode) and
    /// <see cref="RenderThumbnailPlay"/> (play mode): camera solve, capture, head-facing extraction and the
    /// pose/expression resolvers. A front-end owns only how the avatar is produced and posed and how its
    /// expression is applied; nothing here touches a bake, a preview scene, a playable graph or the emulator.
    /// Contract: docs/unity-tools.md.
    /// </summary>
    internal static class RenderThumbnailCore
    {
        internal const string PosesFolder = "Packages/com.ryan6vrc.avatar-tools/Editor/Poses";

        // Framing = the subject height the frame covers, in metres at ReferenceViewHeight; the camera distance
        // is solved from it and the FOV. AimDrop lowers the aim below the view point as a fraction of that span,
        // because the eyes sit near the top of the subject.
        //
        // Bust and half are fixed subject heights scaled only by root scale, because a face is about one size
        // across stylized avatars however tall the viewpoint sits; they crop the crown on purpose. Full is a
        // body frame and also tracks viewpoint height, so a taller body still seats its feet.
        private const float ReferenceViewHeight = 1.6f;
        private const float BustSpan = 0.34f, BustAimDrop = 0.12f;
        private const float HalfSpan = 0.60f, HalfAimDrop = 0.18f;
        private const float FullSpan = 2.00f, FullAimDrop = 0.37f;
        // How far full's span tracks viewpoint height: 1 = proportional.
        private const float ViewHeightFollow = 1f;

        // Camera angles, degrees. Yaw tracks the posed head one-for-one: any partial coefficient makes the
        // face-to-lens angle a function of the pose, and at 1:1 it is DefaultOblique everywhere.
        private const float DefaultOblique = 13f;
        private const float ObliqueDeadband = 5f;
        private const float PitchFollow = 0.5f;
        private const float MaxTrackYaw = 60f;
        private const float MaxCamPitch = 20f;
        // Caller camera knobs, refused outside these ranges by ValidateCameraKnobs. Each is relative to the
        // framing token (zoom scales its span, headroom shifts its aim, pitch offsets its head-follow), so no
        // knob authors the camera outright.
        internal const float MinZoom = 0.6f, MaxZoom = 1.6f;
        internal const float MaxPitchOffset = 20f;
        internal const float MaxHeadroom = 0.3f;
        private const float MaxTotalPitch = 35f;
        private const float LateralOffset = 0.06f; // fraction of span, looking-room in the landscape frame
        internal const float MinFov = 10f, MaxFov = 90f;

        // Backdrop when no bg override is passed: a preview scene has no skybox.
        internal static readonly Color DefaultBackground = new Color(0.23f, 0.23f, 0.24f);
        private const int RampHeight = 256;

        // Empty-frame guard: a pixel counts as drawn when it differs from its own row's column-0 pixel by more
        // than this per-channel byte delta.
        private const int SilhouetteChannelThreshold = 16;

        // ===== Camera solve =====

        /// <summary><see cref="SolveCamera"/>'s result. The resolved angles go in the verdict so a saturated
        /// clamp is visible.</summary>
        internal struct CameraSolution
        {
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal Quaternion LightHolderRotation; // camera-relative rig yaw; play mode uses scene lights and ignores it
            internal float CamYaw;                   // resolved (tracking + oblique), for the verdict
            internal float CamElevation;             // resolved pitch, positive = camera above, looking down
        }

        /// <summary>
        /// Solve the capture camera from the posed geometry. Pure and identical for both front-ends: they differ
        /// only in how <paramref name="viewpoint"/> and <paramref name="headYaw"/> were produced.
        /// </summary>
        internal static CameraSolution SolveCamera(
            string framingToken, float span, float aimDrop, float fov, float? yaw,
            float headYaw, float headPitch, Vector3 viewpoint, Quaternion rootRot,
            float rootLossyScaleY, float viewPositionY, float rootEulerY,
            float zoom = 1f, float pitch = 0f, float headroom = 0f)
        {
            // ObliqueDeadband holds near-frontal poses to one side; letting retarget noise pick would swing the
            // camera 2*DefaultOblique and shoot one pose from opposite sides on two avatars.
            float obliqueSign = headYaw < -ObliqueDeadband ? -1f : 1f;
            // One shot offset drives both the orbit and the looking-room, so an explicit yaw cannot orbit the
            // camera one way while the lateral aim opens the other.
            float shotOffset = yaw ?? (DefaultOblique * obliqueSign);
            // Zero gets no lateral shift: at yaw:0 the face is dead-on the lens, with no gaze in frame to leave
            // room for.
            float lateralSign = shotOffset > 0f ? 1f : (shotOffset < 0f ? -1f : 0f);
            float camYaw = Mathf.Clamp(headYaw, -MaxTrackYaw, MaxTrackYaw) + shotOffset;
            // A positive rotation about +X lowers the camera. The head term is negated because a chin-raised
            // pose is shot from slightly above (from below it is a nostril shot); the elevation `pitch` subtracts.
            float camPitch = Mathf.Clamp(
                Mathf.Clamp(-headPitch * PitchFollow, -MaxCamPitch, MaxCamPitch) - pitch,
                -MaxTotalPitch, MaxTotalPitch);

            // Angles apply in the root's basis so an avatar sitting rotated in the scene still photographs
            // frontally.
            Quaternion orbit = Quaternion.AngleAxis(camYaw, Vector3.up) * Quaternion.AngleAxis(camPitch, Vector3.right);
            Vector3 screenRight = rootRot * (orbit * Vector3.left);   // left: the camera looks back along -forward

            // framingToken is already lowercased by the caller.
            float viewFollow = framingToken == "full" ? ViewHeightFollow : 0f;
            float scaledSpan = span * rootLossyScaleY
                * Mathf.Lerp(1f, viewPositionY / ReferenceViewHeight, viewFollow) / zoom;
            float distance = (scaledSpan * 0.5f) / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);

            // Aim below the view point and laterally off the gaze, opening looking-room in a landscape frame.
            // Vector3.up, not the root's up: camera roll stays world-level below, so the drop that decides
            // where the head sits in frame has to share that basis. Divergent only for a tilted root.
            Vector3 aim = viewpoint
                        - Vector3.up * (scaledSpan * (aimDrop - headroom))
                        + screenRight * (scaledSpan * LateralOffset) * -lateralSign;

            Vector3 pos = aim + rootRot * (orbit * (Vector3.forward * distance));
            return new CameraSolution
            {
                Position = pos,
                Rotation = Quaternion.LookRotation(aim - pos, Vector3.up),
                // Keeps the rig camera-relative; without this, orbiting lights a yawed shot from behind.
                LightHolderRotation = Quaternion.Euler(0f, rootEulerY + camYaw, 0f),
                CamYaw = camYaw,
                CamElevation = -camPitch,
            };
        }

        /// <summary>Yaw of a direction in degrees, signed about +Y: 0 = +Z, positive toward +X (which is
        /// screen-LEFT for a camera on the +Z side).</summary>
        internal static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        /// <summary>Elevation of a direction in degrees. Positive = pointing up (chin raised).</summary>
        internal static float PitchOf(Vector3 dir) =>
            Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        /// <summary>
        /// Head facing as a delta from bind, in the root's basis. Unity does not normalize humanoid bone axes, so
        /// anything read off where the bone's own +Z points is rig-dependent. Take the delta rotation first, then
        /// extract once: extracting an angle from each forward vector and subtracting fails on a bone whose +Z
        /// runs up the neck (how Blender authors orient a bone, most of the VRChat population), yielding yaw 0
        /// and inverted pitch.
        /// </summary>
        internal static void HeadFacing(Quaternion rootRot, Transform headBone, Quaternion restHeadLocal,
            out float headYaw, out float headPitch)
        {
            HeadFacing(Quaternion.Inverse(rootRot) * headBone.rotation, restHeadLocal, out headYaw, out headPitch);
        }

        /// <summary>The scene-free half of the overload above, taking the posed head rotation already in the
        /// root's basis, so a test can vary the rest orientation without a live bone (`docs/verify.md` §Test
        /// venue).</summary>
        internal static void HeadFacing(Quaternion posedHeadLocal, Quaternion restHeadLocal,
            out float headYaw, out float headPitch)
        {
            Vector3 deltaFwd = (posedHeadLocal * Quaternion.Inverse(restHeadLocal)) * Vector3.forward;
            headYaw = YawOf(deltaFwd);
            headPitch = PitchOf(deltaFwd);
        }

        // ===== Capture core =====

        internal const int CaptureWidth = 1200;
        internal const int CaptureHeight = 900;

        /// <summary>The outcome of a <see cref="Capture"/>. <c>Png</c> is null when nothing drew, and the front-end
        /// fails loud rather than writing a blank. <c>HeadViewport</c> is where the view point landed: reported,
        /// not gated, since a bad crop is visible in the PNG.</summary>
        internal struct CaptureResult
        {
            internal int Drawn;
            internal byte[] Png;
            internal Vector3 HeadViewport;
        }

        /// <summary>
        /// Render an already-positioned, already-culled <paramref name="cam"/> to an sRGB target and read it back:
        /// optional gradient backdrop, empty-frame guard, PNG encode. The front-end configures the camera (clear,
        /// background = <paramref name="bgTop"/>, cullingMask, fov, scene) and supplies the view point to project.
        /// </summary>
        internal static CaptureResult Capture(Camera cam, Color bgTop, Color bgBottom, Vector3 viewpoint)
        {
            const int W = CaptureWidth, H = CaptureHeight;
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
                // A bespoke Camera.Render() compiles a cold shader variant synchronously only while
                // ShaderUtil.allowAsyncCompilation is false. It can be true at an edit-mode capture, which then
                // draws the flat #00FFFF placeholder under an OK verdict. The project pref
                // (EditorSettings.asyncShaderCompilation, RenderAvatar's lever) does not reach this path. No pixel
                // scan backs this pin, because a saturated cyan emission renders exact #00FFFF too.
                bool allowAsyncWas = ShaderUtil.allowAsyncCompilation;
                ShaderUtil.allowAsyncCompilation = false;
                try { cam.Render(); }
                finally { ShaderUtil.allowAsyncCompilation = allowAsyncWas; }

                // The view point, not the aim: the camera looks exactly at the aim, so projecting that always
                // prints (0.5,0.5).
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

                // Reference = each row's own column-0 pixel of the read-back texture, not a computed color: a
                // computed reference in a Linear project writing an sRGB target reads the whole background as
                // drawn and defeats the guard, and a vertical gradient is constant along a row.
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

        /// <summary>Validate the camera knobs, after <c>fov</c> itself has passed. Null when valid, else the
        /// refusal reason. The range tests are negated so NaN is refused too. The joint bound holds the camera no
        /// closer than fov 90 at zoom 1: closer puts it inside the hair, which renders as hair interior and still
        /// passes the empty-frame guard.</summary>
        internal static string ValidateCameraKnobs(float fov, float zoom, float pitch, float headroom)
        {
            var ic = CultureInfo.InvariantCulture;
            if (!(zoom >= MinZoom && zoom <= MaxZoom))
                return "zoom " + zoom.ToString(ic) + " out of range — expected " + MinZoom.ToString(ic) + "–"
                    + MaxZoom.ToString(ic) + " (a scale on the framing's span: >1 tighter, <1 wider)";
            if (!(pitch >= -MaxPitchOffset && pitch <= MaxPitchOffset))
                return "pitch " + pitch.ToString(ic) + " out of range — expected ±" + MaxPitchOffset.ToString(ic)
                    + " degrees of camera elevation (positive = camera higher, looking down)";
            if (!(headroom >= -MaxHeadroom && headroom <= MaxHeadroom))
                return "headroom " + headroom.ToString(ic) + " out of range — expected ±" + MaxHeadroom.ToString(ic)
                    + " (a fraction of the framed span; positive = more frame above the head)";
            float nearness = zoom * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            if (nearness > Mathf.Tan(MaxFov * 0.5f * Mathf.Deg2Rad) + 1e-4f)
                return "zoom " + zoom.ToString(ic) + " at fov " + fov.ToString(ic) + " puts the camera closer than the "
                    + "tool allows (zoom × tan(fov/2) must stay ≤ 1, the closest fov 90 already reached) — lower zoom or fov";
            return null;
        }

        /// <summary>The knobs as the verdict echoes them — the values to pass back to reproduce a shot, to the printed precision.</summary>
        internal static string KnobToken(float zoom, float pitch, float headroom)
            => "zoom=" + zoom.ToString("0.##", CultureInfo.InvariantCulture)
               + " pitch=" + pitch.ToString("0.#", CultureInfo.InvariantCulture)
               + " headroom=" + headroom.ToString("0.##", CultureInfo.InvariantCulture);

        // ===== Framing / background / token helpers (pure) =====

        /// <summary>The subject height a named framing covers, in metres, and how far below the view point
        /// to aim, as a fraction of that span. Throws for anything else.</summary>
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
        /// <c>#TOP:#BOTTOM</c> gives a vertical two-stop gradient. A leading '#' is required because
        /// <see cref="ColorUtility.TryParseHtmlString"/> also accepts some CSS color names, which this tool
        /// refuses.</summary>
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
        /// The bundled poses: readable name (the file name minus <c>RTPose_</c>) to asset path, sorted so the
        /// unknown-pose error is stable. Adding a pose is dropping an <c>RTPose_*.anim</c> file in
        /// <see cref="PosesFolder"/>; no name list is hard-wired.
        /// </summary>
        internal static SortedDictionary<string, string> BundledPoses()
        {
            var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!AssetDatabase.IsValidFolder(PosesFolder)) return found;

            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { PosesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                // FindAssets descends child folders; a clip under a Poses/ subfolder must not become vocabulary.
                if (System.IO.Path.GetDirectoryName(path).Replace('\\', '/') != PosesFolder) continue;
                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith("RTPose_", StringComparison.OrdinalIgnoreCase)) continue;
                found[file.Substring("RTPose_".Length)] = path;
            }
            return found;
        }

        /// <summary>
        /// Fold a pose or FX state name to its match key, so <c>hand-on-hip</c>, <c>hand_on_hip</c> and
        /// <c>HandOnHip</c> are one token.
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
        /// <para>A clip that is not <c>isHumanMotion</c> is refused: it would not retarget across rigs, and
        /// <c>SampleAnimationClip</c> is a silent no-op for it on a humanoid, so the verdict would claim a pose
        /// over an unposed avatar.</para>
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
        /// <para>Resolving on the baked avatar is the mechanism: the bake renames and merges blendshapes, so a
        /// pre-bake clip can bind names the baked avatar no longer has, while a state name survives.</para>
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

            // Two distinct errors: the answer to "no such name" is another name, and every retry is a full SDK
            // bake that can wedge the editor on a hook's modal dialog. A blend-tree state is not walked into.
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

        // ----- Session RunLogs ------------------------------------------------------------------
        /// <summary>Write one session-verb RunLog and return <paramref name="summary"/> with the trailer.</summary>
        internal static string WriteSessionLog(string kind, string label, string summary, string body)
            => Ryan6Vrc.AgentTools.Editor.RunLogFormat.WriteRunLog(
                Ryan6Vrc.AgentTools.Editor.RunLogFormat.RunLogDir,
                kind + "_" + Ryan6Vrc.AgentTools.Editor.RunLogFormat.Sanitize(label), summary, body, ".md");

        /// <summary>Re-attach a capture's trailers after logging, `png=` first. A verdict carrying `png=` must
        /// never reach <see cref="WriteSessionLog"/>: on a failed write that writer truncates at the last
        /// <c>"=&gt; "</c> and substitutes <c>=&gt; FAIL: write failed</c>, turning a successful capture into a
        /// failure and dropping the `png=` token <c>UpdateAvatarRecord</c> consumes. Only the <c>| log=</c> trailer
        /// depends on the write.</summary>
        internal static string SpliceTrailers(string logged, string verdictWithoutPng, string pngPath)
        {
            const string marker = " | log=";
            int at = logged.IndexOf(marker, System.StringComparison.Ordinal);
            if (at < 0) return verdictWithoutPng + " | png=" + pngPath;   // write failed: keep the true verdict
            return verdictWithoutPng + " | png=" + pngPath + logged.Substring(at);
        }

        /// <summary>Make the RunLog dir visible to the AssetDatabase BEFORE play is entered.
        /// <c>PublishArtifact</c> falls back to a full <c>AssetDatabase.Refresh()</c> on the first write into a
        /// dir the database has never seen, and a refresh during play can import and reload the domain, ending
        /// the session being logged.</summary>
        internal static void EnsureRunLogDirImported()
        {
            System.IO.Directory.CreateDirectory(Ryan6Vrc.AgentTools.Editor.RunLogFormat.RunLogDir);
            if (!UnityEditor.AssetDatabase.IsValidFolder(Ryan6Vrc.AgentTools.Editor.RunLogFormat.RunLogDir))
                UnityEditor.AssetDatabase.Refresh();
        }
    }
}
