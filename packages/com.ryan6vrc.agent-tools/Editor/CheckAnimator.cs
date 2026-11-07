using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only PASS/FAIL lint for an <see cref="AnimatorController"/>: schema-certain rules set the
    /// verdict and heuristics stay advisory. <c>basis=auto</c> applies a VRCFury FullController's
    /// <c>rewriteBindings</c> before resolving, so a path a declared rule relocates does not inflate
    /// the demoted brokenBinding count.
    /// Contract: docs/animator.md §Reading a controller.
    /// </summary>
    [AgentTool]
    public static class CheckAnimator
    {
        // AvatarObjectReference.AVATAR_ROOT: the referencePath a root-targeting MA scene ref carries.
        // Get(Component) resolves it to the avatar root, so the frame walk below must too.
        internal const string MaAvatarRootSentinel = "$$$AVATAR_ROOT$$$";

        /// <summary>Path/GUID door: lints the controller <paramref name="controllerPathOrGuid"/> names, or
        /// returns a bare <c>[CheckAnimator] FAIL: …</c> echoing a handle that names none.</summary>
        public static string Run(string controllerPathOrGuid, string basis = "auto",
                                  string mergeSite = null, string avatarRoot = null, string mountRoot = null)
        {
            var controller = RunLogFormat.LoadByPathOrGuid<AnimatorController>(controllerPathOrGuid);
            if (controller == null)
                return Refuse(ReportController.RefuseWhy(controllerPathOrGuid));
            return Run(controller, basis, mergeSite, avatarRoot, mountRoot);
        }

        /// <summary>Lint <paramref name="controller"/>. <paramref name="basis"/> is <c>auto</c> (detect the
        /// binding-basis root from a merge component at <paramref name="mergeSite"/>) or <c>explicit</c>
        /// (caller names <paramref name="avatarRoot"/> / <paramref name="mountRoot"/> as active-scene
        /// handles: path, instance id or unique name). Returns a one-line summary ending in the RunLog path
        /// (<c>… =&gt; RESULT | log=&lt;path&gt;</c>), or a bare <c>[CheckAnimator] FAIL: …</c> on bad input.</summary>
        public static string Run(AnimatorController controller, string basis = "auto",
                                  string mergeSite = null, string avatarRoot = null, string mountRoot = null)
        {
            if (controller == null) return Refuse("controller not found");
            if (basis != "auto" && basis != "explicit")
                return Refuse("unknown basis '" + basis + "' (valid: auto, explicit)");

            var roots = new List<GameObject>();
            bool buildRewrite;                        // demotes broken-binding to advisory when true
            string detection;
            var notes = new List<string>();           // non-offender caveats (e.g. avatar root not found)
            Func<string, string> pathRewrite = null;  // VRCF FullController rewriteBindings under basis=auto (else identity)
            GameObject siteAvatarGO = null;           // the avatar this controller was linted ON, for the multiplicity count
            GameObject usedMount = null;
            GameObject absoluteRoot = null;           // VRCF auto site only: where a leading-`/` binding resolves from

            if (basis == "explicit")
            {
                GameObject avatarGO = null, mountGO = null;
                if (avatarRoot != null)
                {
                    var avatarHandle = SceneHandle.Resolve(avatarRoot);
                    if (!avatarHandle.Ok) return Refuse("avatarRoot: " + avatarHandle.Refusal);
                    avatarGO = avatarHandle.Object;
                }
                if (mountRoot != null)
                {
                    var mountHandle = SceneHandle.Resolve(mountRoot);
                    if (!mountHandle.Ok) return Refuse("mountRoot: " + mountHandle.Refusal);
                    mountGO = mountHandle.Object;
                }
                if (mountGO != null) roots.Add(mountGO);   // mount preferred on tie
                if (avatarGO != null) roots.Add(avatarGO);
                siteAvatarGO = avatarGO;
                usedMount = mountGO ?? avatarGO;
                buildRewrite = false;
                detection = "basis=explicit avatar(" + PathOf(avatarGO) + ") mount(" + PathOf(mountGO) + ")";
                if (roots.Count == 0)
                    notes.Add("neither avatarRoot nor mountRoot supplied — broken-binding rule skipped (no basis root).");
            }
            else // auto
            {
                if (string.IsNullOrEmpty(mergeSite))
                    return Refuse("basis=auto requires mergeSite (a scene GameObject path holding a merge component that references this controller)");
                var d = DetectAuto(controller, mergeSite, notes);
                if (d.Refusal != null) return Refuse(d.Refusal);
                var siteGO = SceneHandle.Resolve(mergeSite).Object;
                // includeInactive: the no-arg overload skips inactive objects, and an authoring avatar is
                // routinely parked inactive.
                var siteDesc = siteGO != null ? siteGO.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true) : null;
                siteAvatarGO = siteDesc != null ? siteDesc.gameObject : null;
                usedMount = d.Root;
                if (d.Root != null) roots.Add(d.Root);
                buildRewrite = d.BuildRewrite;
                detection = d.DetectionLine;
                pathRewrite = d.PathRewrite;
                // VRCFury builds the OUTERMOST descriptor and resolves a leading-`/` binding from it, so the
                // absolute root is the topmost descriptor above the site, not the nearest.
                if (d.Vrcf && siteGO != null)
                {
                    var above = siteGO.GetComponentsInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
                    if (above.Length > 0) absoluteRoot = above[above.Length - 1].gameObject;
                }
            }

            var r = ControllerRules.Run(controller, roots, !buildRewrite, pathRewrite, absoluteRoot);
            notes.AddRange(r.Notes);

            return Emit(controller, r, detection, notes, MergeSiteMultiplicity(controller, siteAvatarGO, usedMount, notes));
        }

        // ----- auto basis detection -------------------------------------------------------------------
        // Components match by type name, so a missing MA or VRCFury assembly reads as the zero-component
        // refusal.

        private struct AutoResult { public string Refusal; public GameObject Root; public bool BuildRewrite; public string DetectionLine; public Func<string, string> PathRewrite; public bool Vrcf; }

        private static AutoResult DetectAuto(AnimatorController controller, string mergeSite, List<string> notes)
        {
            var siteHandle = SceneHandle.Resolve(mergeSite);
            if (!siteHandle.Ok)
                return new AutoResult { Refusal = "mergeSite: " + siteHandle.Refusal };
            var site = siteHandle.Object;

            var descriptor = site.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
            GameObject avatarGO = descriptor != null ? descriptor.gameObject : null;
            if (avatarGO == null)
                notes.Add("no VRCAvatarDescriptor found above mergeSite '" + mergeSite + "' — avatar-root basis unresolved.");

            var mergeMatches = new List<AutoResult>();
            AutoResult? plainAnimator = null;
            foreach (var c in site.GetComponents<Component>())
            {
                if (c == null) continue;
                string fn = c.GetType().FullName ?? "";
                if (fn == "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator")
                {
                    var m = ParseMergeAnimator(c, controller, avatarGO, notes);
                    if (m != null) mergeMatches.Add(m.Value);
                }
                else if (fn == "VF.Model.VRCFury")
                {
                    var m = ParseVrcFury(c, controller, notes);
                    if (m != null) mergeMatches.Add(m.Value);
                }
                else if (plainAnimator == null && c is Animator anim && anim.runtimeAnimatorController == controller)
                {
                    // A plain Animator's authored paths are avatar-root-relative.
                    plainAnimator = new AutoResult
                    {
                        Root = avatarGO, BuildRewrite = false,
                        DetectionLine = "basis=auto→avatar(" + PathOf(avatarGO) + ") [plain Animator]"
                    };
                }
            }

            if (mergeMatches.Count > 1)
                return new AutoResult { Refusal = "multiple merge components reference this controller at '" + mergeSite + "' — cannot pick a basis" };
            if (mergeMatches.Count == 1)
                return mergeMatches[0];
            if (plainAnimator != null)
                return plainAnimator.Value;
            return new AutoResult { Refusal = "no merge component referencing this controller at '" + mergeSite + "'" };
        }

        // ----- Frame detection (shared with CheckAvatar and MergeSurfaces) ------------------------------
        // A frame is the binding-basis root a merge component establishes for the controllers it mounts.
        // The Try* helpers take any component, self-check its type and return the controllers it references.
        // A required field absent from the SerializedObject is API drift: it sets UnreflectedAnchor even
        // with no controller to walk, because a silent skip on a real component would be a false PASS. A
        // present-but-null animator or an empty controllers list is an intentional empty and stays quiet.
        // The Parse* wrappers here drop any frame not carrying this controller.

        internal enum FrameKind { DescriptorLayer, MA, VRCF }

        /// <summary>Describe a runtime controller these doors cannot lint, or null for one they can (or
        /// for genuine absence). An <c>AnimatorOverrideController</c> is a sibling of
        /// <c>AnimatorController</c> under <c>RuntimeAnimatorController</c>, so every <c>as
        /// AnimatorController</c> here reads a live one as an empty slot. The count is of non-null entries,
        /// <c>overridesCount</c> being the whole base clip table. A base that is itself an override is
        /// named, not followed.</summary>
        /// <param name="includeSelfPath">false when the caller already echoed this handle.</param>
        internal static string DescribeUnlintableController(UnityEngine.Object rac, bool includeSelfPath = true)
        {
            var ovr = rac as AnimatorOverrideController;
            if (ovr == null) return null;
            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
            try { ovr.GetOverrides(overrides); } catch { /* report what we can */ }
            int set = 0;
            foreach (var kv in overrides) if (kv.Value != null) set++;
            var b = ovr.runtimeAnimatorController;
            string bas = b == null ? "no base"
                : b is AnimatorOverrideController ? "base is itself an override controller: '" + AssetDatabase.GetAssetPath(b) + "'"
                : "base '" + AssetDatabase.GetAssetPath(b) + "'";
            return "AnimatorOverrideController" + (includeSelfPath ? " '" + AssetDatabase.GetAssetPath(ovr) + "'" : "")
                 + " (" + bas + ", " + set + " clip substitutions) — not lintable here; the base is a separate "
                 + "asset and reading it omits those substitutions";
        }

        internal struct FrameResult
        {
            public GameObject Root;       // the binding-basis root (mount, or avatar root for Absolute)
            public FrameKind Kind;
            public bool IsAbsolute;       // MA Absolute pathMode (basis is the avatar root, not a mount)
            public string UnreflectedAnchor; // non-null ⇒ a required frame field or vendor invoke failed to reflect (fail loud)
            // Controllers this surface mounts that no door here can lint (an AnimatorOverrideController),
            // one entry each, so an avatar-wide verdict cannot come back clean having walked nothing.
            public List<string> Unlintable;
            // VRCF only: this FullController's rewriteBindings as BuildVrcfRewriter's transform. null means
            // no rules.
            public Func<string, string> PathRewrite;
            // VRCF only: the FullController's rootBindingsApplyToAvatar. When set, the build leaves an
            // empty-path binding at the avatar root (AnimationBindingUtils.ResolveTarget short-circuits on
            // it), so a resolver must not walk the mount for it.
            public bool RootBindingsApplyToAvatar;
        }

        // MA MergeAnimator: pathMode 0=Relative, 1=Absolute. Absolute's basis is the avatar root. Relative
        // mounts at relativePathRoot, resolved by invoking MA's own Get(Component) on the boxed reference
        // with the avatar root as container, the build's literal call (MergeAnimatorProcessor:
        // relativePathRoot.Get(avatarRootTransform)). Returns true for an MA MergeAnimator that references
        // a controller (out via controller), and with controller null for one carrying a drift anchor or
        // an Unlintable entry.
        internal static bool TryMaFrame(Component c, GameObject avatarGO,
            out AnimatorController controller, out FrameResult frame)
        {
            controller = null;
            frame = default;
            if (c == null || c.GetType().FullName != "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator")
                return false;

            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return false; }

            var animProp = so.FindProperty("animator");
            if (animProp == null)
            {
                frame = new FrameResult { Root = avatarGO, Kind = FrameKind.MA, IsAbsolute = false, UnreflectedAnchor = "MA.animator" };
                return true;
            }
            controller = animProp.objectReferenceValue as AnimatorController;
            if (controller == null)
            {
                // An override controller here is not an intentional empty: the component really merges a
                // controller, and returning false would drop the frame from every door at once.
                string unlintable = DescribeUnlintableController(animProp.objectReferenceValue);
                if (unlintable == null) return false;
                frame = new FrameResult
                {
                    Root = avatarGO, Kind = FrameKind.MA, IsAbsolute = false,
                    Unlintable = new List<string> { unlintable },
                };
                return true;
            }

            var pathModeProp = so.FindProperty("pathMode");
            string unreflected = pathModeProp == null ? "MA.pathMode" : null;
            bool absolute = pathModeProp != null && pathModeProp.enumValueIndex == 1;
            GameObject root;
            if (absolute)
            {
                root = avatarGO;
            }
            else
            {
                root = null;
                var rel = so.FindProperty("relativePathRoot");
                if (rel == null)
                {
                    unreflected = unreflected ?? "MA.relativePathRoot";
                }
                else if (avatarGO != null)
                {
                    // Get re-derives the outermost avatar root itself, so a nested descriptor above c cannot
                    // skew the frame.
                    string reason = null;
                    object boxed = null;
                    try { boxed = VendorReflect.GetBoxedValue(rel); }
                    catch (Exception e) { reason = "boxedValue threw (" + e.GetType().Name + ")"; }
                    if (reason == null && boxed == null) reason = "boxedValue was null";
                    if (reason == null)
                    {
                        var mi = VendorReflect.ResolveAorGetOverload(boxed.GetType());
                        if (mi == null) reason = "Get(Component) overload unreachable (MA API drift / absent)";
                        else
                        {
                            try { root = mi.Invoke(boxed, new object[] { avatarGO.transform }) as GameObject; }
                            catch (Exception e) { reason = "Get(Component) invoke threw (" + VendorReflect.DescribeInvokeError(e) + ")"; }
                        }
                    }
                    if (reason != null)
                    {
                        unreflected = unreflected ?? "MA.Get(Component)";
                        // Drift fallback only: self-resolve from the serialized children in Get(Component)'s order.
                        Debug.LogWarning("[CheckAnimator] MA relativePathRoot resolve degraded on " + PathOf(c.gameObject)
                                       + " (" + reason + ") — self-resolving from serialized children (empty referencePath first, then an in-avatar targetObject, then the sentinel, then the path).");
                        var target = rel.FindPropertyRelative("targetObject");
                        var refPath = rel.FindPropertyRelative("referencePath");
                        string path = refPath != null ? refPath.stringValue : "";
                        var tgo = target != null ? target.objectReferenceValue as GameObject : null;
                        if (!string.IsNullOrEmpty(path))
                        {
                            if (tgo != null && tgo.transform.IsChildOf(avatarGO.transform)) root = tgo;
                            else if (path == MaAvatarRootSentinel) root = avatarGO;
                            else
                            {
                                var t = ResolveArmatureDecoy(avatarGO.transform.Find(path));
                                root = t != null ? t.gameObject : null;
                            }
                        }
                    }
                }
                if (root == null) root = c.gameObject; // unresolved relativePathRoot ⇒ own GameObject, as the build
            }
            frame = new FrameResult { Root = root, Kind = FrameKind.MA, IsAbsolute = absolute, UnreflectedAnchor = unreflected };
            return true;
        }

        // Get(Component)'s last step (AvatarObjectReference.cs): some avatars carry a second, childless
        // "Armature" to move the VRChat eye position, so a path landing on a childless "Armature" is
        // redirected to the same-named sibling that has children. Applies to the path branch only: MA runs
        // it on Find()'s result, not on a targetObject. Only the degraded self-resolve calls this, because
        // the invoked Get does the swap itself; skipping it there would mount at the decoy and fail every
        // binding in the merged controller.
        internal static Transform ResolveArmatureDecoy(Transform resolved)
        {
            if (resolved == null || resolved.name != "Armature" || resolved.childCount != 0) return resolved;
            var parent = resolved.parent;
            if (parent == null) return resolved;
            foreach (Transform sibling in parent)
                if (sibling.name == "Armature" && sibling.childCount > 0) return sibling;
            return resolved;
        }

        private static AutoResult? ParseMergeAnimator(Component c, AnimatorController controller, GameObject avatarGO, List<string> notes)
        {
            if (!TryMaFrame(c, avatarGO, out var discovered, out var frame)) return null;
            if (discovered != controller) return null;
            if (frame.UnreflectedAnchor != null)
                notes.Add("MA MergeAnimator on '" + PathOf(c.gameObject) + "': '" + frame.UnreflectedAnchor + "' did not " +
                          "reflect (Modular Avatar API drift/absent), so its mount is best-effort and brokenBinding may be wrong.");
            return frame.IsAbsolute
                ? new AutoResult
                {
                    Root = frame.Root, BuildRewrite = true,
                    DetectionLine = "basis=auto→avatar(" + PathOf(frame.Root) + ") [MA MergeAnimator, Absolute]"
                }
                : new AutoResult
                {
                    Root = frame.Root, BuildRewrite = true,
                    DetectionLine = "basis=auto→mount(" + PathOf(frame.Root) + ") [MA MergeAnimator]"
                };
        }

        // VRCFury FullController: content is a [SerializeReference]; FullController iff its managed type
        // name ends in "FullController". Mounts every content.controllers[i].controller.objRef. Mount =
        // content.rootObjOverride, else the own GameObject. Returns true for a FullController, whichever
        // controllers it lists, and for a VRCFury component whose content field is absent.
        internal static bool TryVrcfFrame(Component c,
            out List<AnimatorController> controllers, out FrameResult frame)
        {
            controllers = new List<AnimatorController>();
            frame = default;
            if (c == null || c.GetType().FullName != "VF.Model.VRCFury") return false;

            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return false; }

            var content = so.FindProperty("content");
            if (content == null)
            {
                frame = new FrameResult { Root = c.gameObject, Kind = FrameKind.VRCF, IsAbsolute = false, UnreflectedAnchor = "VRCF.content" };
                return true;
            }
            var tn = content.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(tn) || !tn.EndsWith("FullController")) return false;

            string unreflected = null;
            List<string> vrcfUnlintable = null;
            var controllersProp = content.FindPropertyRelative("controllers");
            if (controllersProp == null || !controllersProp.isArray)
            {
                unreflected = "VRCF.content.controllers";
            }
            else
            {
                for (int i = 0; i < controllersProp.arraySize; i++)
                {
                    var el = controllersProp.GetArrayElementAtIndex(i);
                    var ctrl = el.FindPropertyRelative("controller");
                    var objRef = ctrl != null ? ctrl.FindPropertyRelative("objRef") : null;
                    if (objRef != null && objRef.objectReferenceValue is AnimatorController ac) controllers.Add(ac);
                    else if (objRef != null)
                    {
                        string unlintable = DescribeUnlintableController(objRef.objectReferenceValue);
                        if (unlintable != null) (vrcfUnlintable ?? (vrcfUnlintable = new List<string>())).Add(unlintable);
                    }
                }
            }

            GameObject root = null;
            var over = content.FindPropertyRelative("rootObjOverride");
            if (over != null && over.objectReferenceValue is GameObject go) root = go;
            if (root == null) root = c.gameObject;
            var pathRewrite = BuildVrcfRewriter(content, ref unreflected);
            var rootToAvatar = content.FindPropertyRelative("rootBindingsApplyToAvatar");
            frame = new FrameResult
            {
                Root = root, Kind = FrameKind.VRCF, IsAbsolute = false, UnreflectedAnchor = unreflected,
                PathRewrite = pathRewrite,
                RootBindingsApplyToAvatar = rootToAvatar != null && rootToAvatar.boolValue,
                Unlintable = vrcfUnlintable,
            };
            return true;
        }

        // The VRCFury FullController "Path Rewrite Rules" (content.rewriteBindings) as a path transform. The
        // build runs these before the nearest-match ancestor walk. The transform invokes the build's own
        // VF.Utils.AnimationBindingUtils.RewriteRelativePath on this content's live list, which it only
        // reads, so two FullControllers on one mount never cross-contaminate. Do not replicate it: VRCFury
        // is not FOSS-licensed, so a near-copy here is a licensing question as well as a drift hazard.
        // Returns null when there are no rules (identity). The transform returns null for a path a delete
        // rule drops at build, which is not a real break.
        //
        // Rules present with RewriteRelativePath unreachable or the model unboxable anchor via
        // `unreflected` and return identity: the walk may then report false broken bindings, so the caveat
        // must ride the frame.
        private static Func<string, string> BuildVrcfRewriter(SerializedProperty content, ref string unreflected)
        {
            var arr = content.FindPropertyRelative("rewriteBindings");
            if (arr == null || !arr.isArray || arr.arraySize == 0) return null;
            object model = null;
            try { model = content.managedReferenceValue; } catch { /* unboxable ⇒ anchored below */ }
            var mi = VendorReflect.ResolveVrcfRewritePath();
            object rules = null;
            if (model != null)
            {
                var rf = model.GetType().GetField("rewriteBindings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (rf != null) rules = rf.GetValue(model);
            }
            if (rules == null || mi == null || !mi.GetParameters()[1].ParameterType.IsInstanceOfType(rules))
            {
                unreflected = unreflected ?? "VRCF.RewritePath";
                return null;
            }
            bool warned = false;
            return path =>
            {
                try { return (string)mi.Invoke(null, new object[] { path, rules }); }
                catch (Exception e)
                {
                    // A throw past the pin is a broken rule row, not API drift, and it recurs on every path:
                    // leave the path un-rewritten and warn once.
                    if (!warned)
                    {
                        warned = true;
                        Debug.LogWarning("[CheckAnimator] VRCFury RewriteRelativePath invoke threw (" + VendorReflect.DescribeInvokeError(e)
                                       + ") on '" + path + "' — paths left un-rewritten (repeat throws for this rewriter suppressed).");
                    }
                    return path;
                }
            };
        }

        private static AutoResult? ParseVrcFury(Component c, AnimatorController controller, List<string> notes)
        {
            if (!TryVrcfFrame(c, out var controllers, out var frame)) return null;
            if (!controllers.Contains(controller)) return null;
            // VRCF.RewritePath is the one anchor that can ride a frame carrying this controller: the
            // field-absent anchors arrive with no controllers listed, so the Contains check skips them.
            if (frame.UnreflectedAnchor == "VRCF.RewritePath")
                notes.Add("VRCFury Path Rewrite Rules on '" + PathOf(c.gameObject) + "' could not be applied " +
                          "(RewritePath did not reflect — VRCFury API drift/absent), so brokenBinding may be " +
                          "inflated by paths those rules would relocate.");
            return new AutoResult
            {
                Root = frame.Root, BuildRewrite = true, Vrcf = true,
                DetectionLine = "basis=auto→mount(" + PathOf(frame.Root) + ") [VRCFury FullController]",
                PathRewrite = frame.PathRewrite,
            };
        }

        // Shared binding walk (also CheckAvatar's): every clip a controller references, float and objref
        // bindings, humanoid curves skipped, each resolved against any of roots. Returns the unresolved
        // pairs, each carrying the original binding: what the .anim holds and what a repath must target.
        // pathRewrite (null = identity) runs on each path first, as the build does; a null from it is a
        // delete rule dropping the binding at build, so it is skipped rather than unresolved.
        // absoluteRoot is the avatar root on a VRCFury FullController frame, else null. Under VRCFury a
        // path starting with `/` after the rewrite resolves from the avatar root with no ancestor walk
        // (AnimationBindingUtils.ResolveTarget), so the probe uses absoluteRoot alone. With absoluteRoot
        // null, a leading `/` is probed literally against roots.
        internal static List<(AnimationClip clip, EditorCurveBinding binding)> CollectUnresolvedBindings(
            AnimatorController controller, List<GameObject> roots, Func<string, string> pathRewrite = null,
            GameObject absoluteRoot = null)
        {
            var unresolved = new List<(AnimationClip, EditorCurveBinding)>();
            foreach (var clip in AnimatorClipWalk.CollectClips(controller))
            {
                if (clip == null) continue;
                var bindings = new List<EditorCurveBinding>();
                bindings.AddRange(AnimationUtility.GetCurveBindings(clip));
                bindings.AddRange(AnimationUtility.GetObjectReferenceCurveBindings(clip));
                foreach (var b in bindings)
                {
                    if (IsHumanoidAnimatorCurve(b)) continue;
                    var probe = b;
                    if (pathRewrite != null)
                    {
                        string rewritten = pathRewrite(b.path);
                        if (rewritten == null) continue;
                        probe.path = rewritten;
                    }
                    bool resolved = false;
                    if (absoluteRoot != null && probe.path.StartsWith("/"))
                    {
                        probe.path = probe.path.TrimStart('/');
                        resolved = AnimationUtility.GetAnimatedObject(absoluteRoot, probe) != null;
                    }
                    else
                    {
                        foreach (var root in roots)
                            if (AnimationUtility.GetAnimatedObject(root, probe) != null) { resolved = true; break; }
                    }
                    if (resolved) continue;
                    unresolved.Add((clip, b));
                }
            }
            return unresolved;
        }

        // Skip humanoid muscle + root/IK-goal curves: they animate the Animator itself and have no scene
        // object, so GetAnimatedObject can return null on a valid clip. Keyed on type+name, NOT empty path
        // — a genuine broken root-level (path=="") non-muscle binding must still be caught.
        private static HashSet<string> _muscleNames;
        private static readonly string[] HumanoidCurvePrefixes =
        {
            "RootT", "RootQ", "MotionT", "MotionQ", "LeftFootT", "LeftFootQ", "RightFootT", "RightFootQ",
            "LeftHandT", "LeftHandQ", "RightHandT", "RightHandQ",
        };
        internal static bool IsHumanoidAnimatorCurve(EditorCurveBinding b)
        {
            if (b.type != typeof(Animator)) return false;
            if (_muscleNames == null)
            {
                _muscleNames = new HashSet<string>();
                try { foreach (var n in HumanTrait.MuscleName) _muscleNames.Add(n); } catch { /* API absent */ }
            }
            if (_muscleNames.Contains(b.propertyName)) return true;
            foreach (var pre in HumanoidCurvePrefixes)
                if (b.propertyName.StartsWith(pre, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The <c>mergeSites=</c> rider: how many surfaces on this avatar mount the controller
        /// under test and which one the basis resolved to, since a wrong basis flips PASS/FAIL on an unchanged
        /// controller. <see cref="DetectAuto"/> refuses on ambiguity at one site; this counts across the
        /// avatar. Returns null (no token) with no avatar to count over or at most one site.</summary>
        private static string MergeSiteMultiplicity(AnimatorController controller, GameObject avatarGO,
                                                    GameObject usedMount, List<string> notes)
        {
            if (avatarGO == null || controller == null) return null;
            List<MergeSurfaces.Surface> surfaces;
            try
            {
                surfaces = MergeSurfaces.Enumerate(avatarGO, avatarGO.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(),
                    (c, anchor) => notes.Add("merge-site scan: frame field '" + anchor + "' on "
                        + c.GetType().Name + " @ " + PathOf(c.gameObject) + " did not reflect — the site count below is best-effort."),
                    onUnlintable: (c, what) => notes.Add("merge-site scan: surface @ " + PathOf(c.gameObject)
                        + " mounts " + what + " — it is not counted among the merge sites below."));
            }
            catch (Exception e)
            {
                notes.Add("merge-site scan failed (" + e.GetType().Name + ") — mergeSites not counted.");
                return null;
            }
            var mounts = new List<string>();
            foreach (var s in surfaces)
                if (s.Controller == controller) mounts.Add(PathOf(s.Mount));
            if (mounts.Count <= 1) return null;
            return string.Format(CultureInfo.InvariantCulture, "mergeSites={0} (used: {1})",
                mounts.Count, usedMount != null ? PathOf(usedMount) : "(none)");
        }

        private static string Emit(AnimatorController controller, LintResult rep, string detection, List<string> notes, string mergeSites)
        {
            bool errorTierFired = rep.MissingMotion > 0 || rep.UndeclaredParam > 0 || rep.NonFloatBlendParam > 0
                                  || rep.NonFloatParamCurve > 0 || rep.DriverOnAnimatedParam > 0
                                  || rep.EntryShadow > 0 || rep.DeadTransition > 0
                                  || (rep.BrokenBindingIsError && rep.BrokenBinding > 0);
            string result = errorTierFired ? "FAIL" : "PASS";

            int advisories = rep.Advisories.Count;

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckAnimator] {0}: missingMotion={1} undeclaredParam={2} nonFloatBlendParam={3} nonFloatParamCurve={4} driverOnAnimatedParam={5} entryShadow={6} deadTransition={7} brokenBinding={8} advisories={9} => {10}",
                controller.name, rep.MissingMotion, rep.UndeclaredParam, rep.NonFloatBlendParam, rep.NonFloatParamCurve, rep.DriverOnAnimatedParam, rep.EntryShadow, rep.DeadTransition, rep.BrokenBinding, advisories, result);
            // After the verdict token, so the summary's leading grammar is unchanged for anything parsing it.
            if (mergeSites != null) summary += " | " + mergeSites;

            var sb = new StringBuilder();
            sb.Append("# CheckAnimator: ").Append(controller.name).Append('\n');
            string assetPath = AssetDatabase.GetAssetPath(controller);
            sb.Append("asset: `").Append(string.IsNullOrEmpty(assetPath) ? "(unsaved)" : assetPath).Append("`  \n");
            sb.Append(detection).Append("  \n");
            foreach (var n in notes) sb.Append("> note: ").Append(n).Append("  \n");
            sb.Append('\n').Append(summary.Substring("[CheckAnimator] ".Length)).Append('\n');

            sb.Append("\n## Errors\n\n");
            if (rep.Errors.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.Errors) AppendOffender(sb, o);

            sb.Append("\n## Advisories\n\n");
            if (rep.Advisories.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.Advisories) AppendOffender(sb, o);

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "check-animator_" + controller.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogError(res);
            return res;
        }

        private static void AppendOffender(StringBuilder sb, LintOffender o) =>
            sb.Append("- **").Append(o.Kind).Append("** ").Append(o.Where).Append(" — ").Append(o.Detail).Append('\n');

        private static string Refuse(string why)
        {
            string err = "[CheckAnimator] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        private static string PathOf(GameObject go) => MergeSurfaces.PathOf(go);
    }
}
