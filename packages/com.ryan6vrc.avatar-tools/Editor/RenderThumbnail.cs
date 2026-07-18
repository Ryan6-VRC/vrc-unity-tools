using System;
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
        public static readonly string[] BundledPoses = { "contrapposto", "hand-on-hip" };

        // Dolly-back distance (meters, added along camera local-forward beyond the SDK-calibrated
        // PositionPortraitCamera transform) per framing. Cosmetic taste defaults — bust is the SDK's own
        // distance (no dolly); half/full back off for more body. Tune after seeing real output.
        private const float BustFramingDistance = 0f;
        private const float HalfFramingDistance = 0.45f;
        private const float FullFramingDistance = 1.1f;

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

            // Task 1: unique-clone bake, preview-scene stage, capture, teardown. Until then, no NDMF call
            // is made and nothing in the project is touched.
            throw new NotImplementedException("RenderThumbnail render pipeline — Task 1");
        }

        // ===== Pure helpers (unit-tested; do not touch the scene or the asset database beyond reads) ====

        /// <summary>Dolly-back distance in meters for a named framing. Throws for anything else.</summary>
        public static float FramingDistance(string framing)
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
        public static bool TryParseBg(string s, out Color c)
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
        public static bool ResolvePose(string pose, out AnimationClip clip, out string err)
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
                default: return bundled; // unreachable — every entry in BundledPoses is mapped above
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
