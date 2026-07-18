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
    /// Scene-View window-grab, because baking (<c>nadena.dev.ndmf.AvatarProcessor.ManualProcessAvatar</c>)
    /// gives real resolved meshes that a fixed-size off-screen camera can render at a guaranteed
    /// 1200×900 — RenderAvatar never bakes, so it must composite through the Scene View instead, capped
    /// to the pane's size and showing NDMF preview proxies. See docs/2026-07-17-render-thumbnail-design.md.
    /// </summary>
    [AgentTool]
    public static class RenderThumbnail
    {
        /// <summary>Case-insensitive vocabulary <see cref="ResolvePose"/> matches against
        /// <c>Editor/Poses/RTPose_&lt;PascalName&gt;.anim</c> before treating <c>pose</c> as an asset
        /// path/GUID.</summary>
        internal static readonly string[] BundledPoses = { "contrapposto", "hand-on-hip" };

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

        /// <summary>
        /// Render a 1200×900 portrait PNG of the baked <paramref name="target"/> avatar and return a
        /// one-line verdict whose <c>png=</c> trailer is the written path. <paramref name="whatIf"/>
        /// preflights (resolve target, assert a VRC_AvatarDescriptor, resolve <paramref name="pose"/>)
        /// and returns without baking or touching the project.
        /// </summary>
        /// <param name="target">avatar root: scene hierarchy path, instance id, or name (first match).</param>
        /// <param name="pose">null =&gt; floor (unposed); a bundled name (see <see cref="BundledPoses"/>);
        /// or a clip asset path/GUID.</param>
        /// <param name="framing">"bust" | "half" | "full" — dolly distance over the SDK's
        /// PositionPortraitCamera transform.</param>
        /// <param name="bg">null =&gt; default backdrop; "#RRGGBB" =&gt; solid color.</param>
        /// <param name="whatIf">preflight only: resolve target/descriptor/pose, report, bake nothing.</param>
        public static string Render(
            string target,
            string pose = null,
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

            if (whatIf)
            {
                if (!ResolvePose(pose, out AnimationClip _, out string poseErr))
                    return Fail(label, poseErr);

                string poseToken = string.IsNullOrEmpty(pose) ? "floor" : pose;
                string ok = string.Format(CultureInfo.InvariantCulture,
                    "[RenderThumbnail] Render {0} whatIf pose={1} descriptor=OK => WOULD-RENDER (no bake)",
                    label, poseToken);
                Debug.Log(ok);
                return ok;
            }

            // ---- Preflight resolution (fail fast, before any mutation) ----
            if (!ResolvePose(pose, out AnimationClip poseClip, out string poseErr))
                return Fail(label, poseErr);
            if (poseClip != null)
                // Floor is the only render path wired in Task 1; a resolved pose clip is honestly not yet
                // sampled onto the baked clone. Task 2 replaces this throw with the AnimationMode sampling.
                throw new NotImplementedException("pose sampling — Task 2");

            float dolly;
            try { dolly = FramingDistance(framing); }
            catch (ArgumentException ex) { return Fail(label, ex.Message); }
            string framingToken = (framing ?? "bust").Trim().ToLowerInvariant();

            Color bgColor = DefaultBackground;
            if (bg != null && !TryParseBg(bg, out bgColor))
                return Fail(label, "unparseable bg '" + bg + "' — expected #RRGGBB or #RRGGBBAA");

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
            var savedActive = Selection.activeGameObject;

            Scene preview = default;
            GameObject baked = null;
            string bakedName = null;      // captured after bake; the ZZZ subfolder is named this
            RenderTexture rt = null;
            Texture2D tex = null;
            string result = null;
            string residualNote = "";

            try
            {
                // ---- Step 2: unique private clone -> bake (the non-destructiveness keystone) ----
                // The stamped name makes the ZZZ_GeneratedAssets subfolder provably exclusive to this run,
                // so NDMF's pre-existing-folder wholesale-delete can never touch a user's kept bake.
                string stamp = DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
                var mine = UnityEngine.Object.Instantiate(root);
                mine.name = root.name + "__rt_" + stamp;
                try { baked = nadena.dev.ndmf.AvatarProcessor.ManualProcessAvatar(mine); }
                finally { if (mine != null) UnityEngine.Object.DestroyImmediate(mine); }
                bakedName = baked.name; // "<name>__rt_<stamp>(Clone)"

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
                // TASK 2: sample pose clip here (after MoveGameObjectToScene + animator.Rebind, before
                // cam.Render). The floor path (Task 1) samples nothing — the baked clone renders at rest.

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
                cam.cullingMask = unchecked((int)0xFFFFFFDF);
                cam.allowHDR = false;                 // Linear project — the sRGB RT below carries the gamma
                // FOV: DO NOT SET — leave Unity's default 60°; PositionPortraitCamera is calibrated to it.
                descriptorBaked.PositionPortraitCamera(cam.transform);
                cam.transform.position -= cam.transform.forward * dolly;   // framing dolly (bust = 0)

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
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply(false);
                RenderTexture.active = prevActive;

                // ---- Silhouette coverage = fraction of pixels differing from the background ----
                // Compare against bgColor.gamma, NOT bgColor: the project is Linear and the RT is sRGB, so the
                // read-back stores the gamma-encoded clear color; comparing raw (linear) bgColor would flag the
                // entire background as "drawn". Reported, not gated on a tuned middle threshold — only ~0 fails.
                Color bgRef = bgColor.gamma;
                Color[] pixels = tex.GetPixels();
                int drawn = 0;
                const float thr = 0.06f;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color p = pixels[i];
                    if (Math.Abs(p.r - bgRef.r) > thr || Math.Abs(p.g - bgRef.g) > thr || Math.Abs(p.b - bgRef.b) > thr)
                        drawn++;
                }
                float fraction = pixels.Length > 0 ? (float)drawn / pixels.Length : 0f;
                int pct = Mathf.RoundToInt(fraction * 100f);

                string prefix = "[RenderThumbnail] Render " + label + " baked pose=floor framing="
                    + framingToken + " silhouette=" + pct.ToString(CultureInfo.InvariantCulture) + "%";

                if (fraction < 0.005f)
                {
                    // Fail loud — do NOT write a blank PNG.
                    result = prefix + " => ERROR silhouette≈0% (nothing drew — isolation/bake failed?)";
                    Debug.LogError(result);
                }
                else
                {
                    string safeLabel = RunLogFormat.Sanitize(label);
                    string path = System.IO.Path.Combine(Application.temporaryCachePath,
                        "renderthumbnail_" + safeLabel + "_" + stamp + ".png");
                    System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                    result = prefix + " => OK | png=" + path;
                    Debug.Log(result);
                }
            }
            finally
            {
                // ---- Step 7: teardown — runs on EVERY exit (success or throw). Every item guarded so a throw
                // partway through setup still tears down cleanly. Order matters. ----
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);

                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();

                if (preview.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(preview); // destroys baked + lights + camera

                // Orphan sweep: any root new in the target's scene since the step-0 snapshot (catches a
                // reference-less clone stranded by a bake that threw after instantiating it, before returning).
                if (targetScene.IsValid())
                {
                    foreach (var go in targetScene.GetRootGameObjects())
                        if (!preRootIds.Contains(go.GetInstanceID()))
                            UnityEngine.Object.DestroyImmediate(go);
                }

                Selection.objects = savedSelection;
                Selection.activeGameObject = savedActive;

                // Restore scene cleanliness for scenes that were clean at snapshot but are now dirty (the bake
                // instantiates the clone into the live scene, dirtying it; the orphan sweep dirties it too).
                var clearDirty = typeof(UnityEditor.SceneManagement.EditorSceneManager)
                    .GetMethod("ClearSceneDirtiness",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                foreach (var kv in sceneDirtyAtStart)
                {
                    Scene sc = kv.Key;
                    if (!kv.Value && sc.IsValid() && sc.isDirty)
                        clearDirty?.Invoke(null, new object[] { sc });
                }

                // Delete this run's unique ZZZ subfolder, then the parent whenever it is now empty (a pre-existing
                // user bake in a sibling subfolder keeps it non-empty, so this only fires when genuinely empty).
                if (bakedName != null)
                {
                    string sub = "Assets/ZZZ_GeneratedAssets/" + bakedName;
                    if (AssetDatabase.IsValidFolder(sub)) AssetDatabase.DeleteAsset(sub);
                    if (AssetDatabase.IsValidFolder(sub))
                        residualNote = " note=cleanup-residual: " + sub + " not removed"; // surfaced, not swallowed

                    if (AssetDatabase.IsValidFolder("Assets/ZZZ_GeneratedAssets"))
                    {
                        var subs = AssetDatabase.GetSubFolders("Assets/ZZZ_GeneratedAssets");
                        var assetsIn = AssetDatabase.FindAssets("", new[] { "Assets/ZZZ_GeneratedAssets" });
                        if (subs.Length == 0 && assetsIn.Length == 0)
                            AssetDatabase.DeleteAsset("Assets/ZZZ_GeneratedAssets");
                    }
                }
            }

            return result + residualNote;
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

        /// <summary>
        /// Resolve <paramref name="pose"/> to a clip: null/empty =&gt; floor (<paramref name="clip"/> null,
        /// no error); a bundled name (case-insensitive, see <see cref="BundledPoses"/>) =&gt; that package
        /// clip; else a value containing '/' or a 32-hex GUID =&gt; loaded as an asset path/GUID; else a
        /// named FAIL enumerating the bundled vocabulary. Does not assert humanoid-ness — see Task 2.
        /// </summary>
        internal static bool ResolvePose(string pose, out AnimationClip clip, out string err)
        {
            clip = null;
            err = null;
            if (string.IsNullOrEmpty(pose)) return true; // floor

            string trimmed = pose.Trim();

            foreach (var bundled in BundledPoses)
            {
                if (!string.Equals(bundled, trimmed, StringComparison.OrdinalIgnoreCase)) continue;

                string bundledPath = "Packages/com.ryan6vrc.avatar-tools/Editor/Poses/RTPose_"
                    + BundledPascalName(bundled) + ".anim";
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(bundledPath);
                if (clip == null)
                {
                    err = "bundled pose '" + bundled + "' is not authored yet (expected " + bundledPath
                        + ") — see Task 3";
                    return false;
                }
                return true;
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
                return true;
            }

            err = "unknown pose '" + pose + "' — bundled: contrapposto, hand-on-hip; or pass a clip asset path/GUID";
            return false;
        }

        private static string BundledPascalName(string bundled)
        {
            switch (bundled)
            {
                case "contrapposto": return "Contrapposto";
                case "hand-on-hip": return "HandOnHip";
                default:
                    throw new ArgumentException("no PascalName mapping for bundled pose '" + bundled + "'");
            }
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
