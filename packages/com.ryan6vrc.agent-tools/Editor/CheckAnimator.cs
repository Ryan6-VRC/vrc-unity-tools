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
    /// Read-only lint for an <see cref="AnimatorController"/> in the AI-assisted VRChat workflow.
    /// DETECTS the defects an owned controller-cleaner would remove — it never mutates the controller.
    ///
    /// Two tiers, and the tier line is load-bearing: only the schema-certain rules (a dangling motion
    /// GUID, an undeclared parameter, a deterministically-shadowed entry transition, a null-resolving
    /// clip binding) sit at error-tier and can flip the verdict to FAIL. Every heuristic — WD
    /// disagreement, orphan sub-assets, dead layers, cross-package/archive refs — is advisory and NEVER
    /// flips the verdict. A heuristic at error-tier would let the schema lie, so it doesn't get to.
    ///
    /// Binding resolution needs a basis root: the GameObject an authored clip path is relative to.
    /// Under <c>basis=auto</c> the tool reads the merge component at a scene <c>mergeSite</c> (MA
    /// MergeAnimator / VRCFury FullController) to detect that root the way the build will, and — because
    /// those frameworks rewrite binding paths at build — demotes the broken-binding rule to advisory so
    /// an authored-scene resolve can't false-FAIL. It ALSO applies a VRCFury FullController's own
    /// <c>rewriteBindings</c> rules before resolving, so the (demoted) broken-binding COUNT is truthful:
    /// without it, a path a declared rule relocates reads as unresolvable and inflates the count with
    /// false sample offenders — a lying diagnostic even though the verdict stays PASS. Under
    /// <c>basis=explicit</c> the caller names the roots, no rewrite applies, and broken-binding stays error-tier.
    ///
    /// A typo must never read as pervasive rot: every unresolved input is a bare-FAIL naming the miss,
    /// with no artifact trailer. INSPECTION ONLY.
    /// </summary>
    [AgentTool]
    public static class CheckAnimator
    {
        // ----- Public API ---------------------------------------------------------------------------

        /// <summary>Path/GUID overload: resolve <paramref name="controllerPathOrGuid"/> (an asset path or a
        /// GUID) to the <see cref="AnimatorController"/> and lint it, forwarding the basis args. A handle
        /// that names no controller is the same bare <c>[CheckAnimator] FAIL: …</c> (echoing the handle) as
        /// a null controller.</summary>
        public static string Lint(string controllerPathOrGuid, string basis = "auto",
                                  string mergeSite = null, string avatarRoot = null, string mountRoot = null)
        {
            var controller = RunLogFormat.LoadByPathOrGuid<AnimatorController>(controllerPathOrGuid);
            if (controller == null)
                return Refuse("no AnimatorController at '" + controllerPathOrGuid + "' — expects an asset path or GUID");
            return Lint(controller, basis, mergeSite, avatarRoot, mountRoot);
        }

        /// <summary>Lint <paramref name="controller"/> against the v1 rule set. <paramref name="basis"/>
        /// is <c>auto</c> (detect the binding-basis root from a merge component at <paramref name="mergeSite"/>)
        /// or <c>explicit</c> (caller names <paramref name="avatarRoot"/> / <paramref name="mountRoot"/> as
        /// active-scene hierarchy paths). Returns a one-line summary; a real run ends with the RunLog path
        /// in-band (<c>… =&gt; RESULT | log=&lt;path&gt;</c>). A bad-input/refusal early return is a bare
        /// <c>[CheckAnimator] FAIL: …</c> with no trailer.</summary>
        public static string Lint(AnimatorController controller, string basis = "auto",
                                  string mergeSite = null, string avatarRoot = null, string mountRoot = null)
        {
            if (controller == null) return Refuse("controller not found");
            if (basis != "auto" && basis != "explicit")
                return Refuse("unknown basis '" + basis + "' (valid: auto, explicit)");

            // ---- Resolve the binding-basis root(s) + the build-rewrite flag --------------------------
            var roots = new List<GameObject>();      // candidate roots, mount-first
            bool buildRewrite;                        // demotes broken-binding to advisory when true
            string detection;                         // the "basis=…" line rendered atop the body
            var notes = new List<string>();           // non-offender caveats (e.g. avatar root not found)
            Func<string, string> pathRewrite = null;  // VRCF FullController rewriteBindings under basis=auto (else identity)

            if (basis == "explicit")
            {
                GameObject avatarGO = null, mountGO = null;
                if (avatarRoot != null)
                {
                    avatarGO = FindByHierarchyPath(avatarRoot);
                    if (avatarGO == null) return Refuse("avatarRoot '" + avatarRoot + "' did not resolve to a GameObject");
                }
                if (mountRoot != null)
                {
                    mountGO = FindByHierarchyPath(mountRoot);
                    if (mountGO == null) return Refuse("mountRoot '" + mountRoot + "' did not resolve to a GameObject");
                }
                if (mountGO != null) roots.Add(mountGO);   // mount preferred on tie
                if (avatarGO != null) roots.Add(avatarGO);
                buildRewrite = false;                       // explicit never demotes
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
                if (d.Root != null) roots.Add(d.Root);
                buildRewrite = d.BuildRewrite;
                detection = d.DetectionLine;
                pathRewrite = d.PathRewrite;
            }

            // ---- Run the shared rule set on the resolved basis. The rule methods, topology collectors,
            //      and report data live in ControllerRules so a future compiler can run the SAME rules on an
            //      in-memory controller; CheckAnimator owns only basis resolution (above) and rendering (below).
            //      brokenBindingIsError = !buildRewrite: a build-rewrite auto site demotes broken bindings.
            var r = ControllerRules.Run(controller, roots, !buildRewrite, pathRewrite);
            notes.AddRange(r.Notes); // rule-produced caveats (skipped rules), after the basis-resolution notes

            return Emit(controller, r, detection, notes);
        }

        // ----- auto basis detection (untyped SerializedObject reads; missing MA/VRCFury assemblies -----
        //        degrade to "no such component" → the standard zero-component refusal) -----------------

        private struct AutoResult { public string Refusal; public GameObject Root; public bool BuildRewrite; public string DetectionLine; public Func<string, string> PathRewrite; }

        private static AutoResult DetectAuto(AnimatorController controller, string mergeSite, List<string> notes)
        {
            var site = FindByHierarchyPath(mergeSite);
            if (site == null)
                return new AutoResult { Refusal = "mergeSite '" + mergeSite + "' did not resolve to a GameObject" };

            var descriptor = site.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            GameObject avatarGO = descriptor != null ? descriptor.gameObject : null;
            if (avatarGO == null)
                notes.Add("no VRCAvatarDescriptor found above mergeSite '" + mergeSite + "' — avatar-root basis unresolved.");

            // Merge components (MA/VRCFury) drive the basis and the ambiguity check. A plain Animator is
            // NOT a merge component — kept separate as a last-resort fallback so it can never inflate the
            // "multiple merge components" refusal when an Animator co-locates with an MA/VRCFury component.
            var mergeMatches = new List<AutoResult>();
            AutoResult? plainAnimator = null;
            foreach (var c in site.GetComponents<Component>())
            {
                if (c == null) continue;
                string fn = c.GetType().FullName ?? "";
                if (fn == "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator")
                {
                    var m = ParseMergeAnimator(c, controller, avatarGO);
                    if (m != null) mergeMatches.Add(m.Value);
                }
                else if (fn == "VF.Model.VRCFury")
                {
                    var m = ParseVrcFury(c, controller);
                    if (m != null) mergeMatches.Add(m.Value);
                }
                else if (plainAnimator == null && c is Animator anim && anim.runtimeAnimatorController == controller)
                {
                    // Plain Animator / descriptor playable layer — authored paths are avatar-root-relative.
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

        // ----- Reusable frame detection (shared with CheckAvatar) ------------------------------------
        // A "frame" is the binding-basis root a merge component establishes for the controller(s) it
        // mounts, plus how it was derived. The Try* helpers run on ANY subtree component (they self-check
        // type + controller reference), DISCOVER the referenced controller(s) so a caller can enumerate
        // mount sites, and set UnreflectedAnchor (naming a required frame field that failed to reflect) so
        // a fail-loud caller can refuse. CheckAnimator's own Parse* wrappers keep the historical SILENT
        // skip: "not our controller" / unreflected ⇒ the same null they returned before.

        internal enum FrameKind { DescriptorLayer, MA, VRCF }

        internal struct FrameResult
        {
            public GameObject Root;       // the binding-basis root (mount, or avatar root for Absolute)
            public FrameKind Kind;
            public bool IsAbsolute;       // MA Absolute pathMode (basis is the avatar root, not a mount)
            public string UnreflectedAnchor; // non-null ⇒ a required frame field failed to reflect (fail loud)
            // VRCF only: the FullController's "Path Rewrite Rules" (rewriteBindings) as a path transform,
            // applied to each binding path BEFORE the nearest-match ancestor walk (the build applies them in
            // that order). null ⇒ identity (no rules). Returns null for a path a delete-rule drops (the
            // binding vanishes at build — not a real break). CheckAnimator ignores it; CheckAvatar applies it.
            public Func<string, string> PathRewrite;
        }

        // MA MergeAnimator: pathMode 0=Relative, 1=Absolute (confirmed live). Relative ⇒ mount at the
        // resolved relativePathRoot (an AvatarObjectReference: targetObject, else referencePath resolved
        // avatar-root-relative, else the component's OWN GameObject). Absolute ⇒ basis is the avatar root.
        // Returns true iff c is an MA MergeAnimator that references a controller (out via controller).
        internal static bool TryMaFrame(Component c, GameObject avatarGO,
            out AnimatorController controller, out FrameResult frame)
        {
            controller = null;
            frame = default;
            if (c == null || c.GetType().FullName != "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator")
                return false;

            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return false; } // B6: parity with ScanSceneRefs' guard

            // B2: a REQUIRED field that is ABSENT (FindProperty → null) is API drift — surface it loud (return
            // true with an anchor) even when there is no controller to walk; the loud warning is the point. A
            // present-but-null animator is an intentional empty (stay quiet, return false).
            var animProp = so.FindProperty("animator");
            if (animProp == null)
            {
                frame = new FrameResult { Root = avatarGO, Kind = FrameKind.MA, IsAbsolute = false, UnreflectedAnchor = "MA.animator" };
                return true;
            }
            controller = animProp.objectReferenceValue as AnimatorController;
            if (controller == null) return false; // present-but-null: intentional empty, not drift

            var pathModeProp = so.FindProperty("pathMode");
            string unreflected = pathModeProp == null ? "MA.pathMode" : null; // required frame field absent (drift)
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
                    unreflected = unreflected ?? "MA.relativePathRoot"; // B2: field absent (drift) — anchor before the best-effort fallback
                }
                else
                {
                    var target = rel.FindPropertyRelative("targetObject");
                    var refPath = rel.FindPropertyRelative("referencePath");
                    if (target != null && target.objectReferenceValue is GameObject tgo) root = tgo;
                    else if (refPath != null && !string.IsNullOrEmpty(refPath.stringValue) && avatarGO != null)
                    {
                        var t = avatarGO.transform.Find(refPath.stringValue);
                        root = t != null ? t.gameObject : null;
                    }
                }
                if (root == null) root = c.gameObject; // empty/absent relativePathRoot ⇒ own GameObject best-effort
            }
            frame = new FrameResult { Root = root, Kind = FrameKind.MA, IsAbsolute = absolute, UnreflectedAnchor = unreflected };
            return true;
        }

        private static AutoResult? ParseMergeAnimator(Component c, AnimatorController controller, GameObject avatarGO)
        {
            if (!TryMaFrame(c, avatarGO, out var discovered, out var frame)) return null;
            if (discovered != controller) return null; // not OUR controller (silent skip, as before)
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
        // name ends in "FullController". Mounts every content.controllers[i].controller.objRef (out via
        // controllers so a caller can DISCOVER them). Mount = content.rootObjOverride, else the OWN GO.
        // Returns true iff c is a VRCFury FullController (regardless of which controllers it lists).
        internal static bool TryVrcfFrame(Component c,
            out List<AnimatorController> controllers, out FrameResult frame)
        {
            controllers = new List<AnimatorController>();
            frame = default;
            if (c == null || c.GetType().FullName != "VF.Model.VRCFury") return false;

            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return false; } // B6

            // B1: the content field ABSENT (FindProperty → null) is API drift — surface it loud rather than
            // silently skipping (a silent skip on a real FullController would be a forbidden false PASS).
            var content = so.FindProperty("content");
            if (content == null)
            {
                frame = new FrameResult { Root = c.gameObject, Kind = FrameKind.VRCF, IsAbsolute = false, UnreflectedAnchor = "VRCF.content" };
                return true;
            }
            var tn = content.managedReferenceFullTypename;
            // The ONLY silent skip: a present, typed feature that is genuinely not a FullController.
            if (string.IsNullOrEmpty(tn) || !tn.EndsWith("FullController")) return false;

            // Typed as FullController but the controllers list can't decode (field renamed / not an array) is
            // drift — anchor it. An empty-but-present array is a legit zero-controller FullController (stays quiet).
            string unreflected = null;
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
                }
            }

            GameObject root = null;
            var over = content.FindPropertyRelative("rootObjOverride");
            if (over != null && over.objectReferenceValue is GameObject go) root = go;
            if (root == null) root = c.gameObject;
            frame = new FrameResult
            {
                Root = root, Kind = FrameKind.VRCF, IsAbsolute = false, UnreflectedAnchor = unreflected,
                PathRewrite = BuildVrcfRewriter(content), // this component's rules only — no cross-controller bleed
            };
            return true;
        }

        // Extract the VRCFury FullController "Path Rewrite Rules" (content.rewriteBindings: from/to/delete)
        // and build a path transform replicating VF.Feature.FullControllerBuilder.RewritePath (+
        // ClipRewritersService.Join). The build runs these BEFORE the nearest-match ancestor walk, so a
        // caller resolves the rewritten path against the ancestor chain. Reads only THIS content's rules, so
        // two FullControllers on one mount (e.g. one with rules, one without) never cross-contaminate.
        // Returns null when there are no rules (identity). The transform returns null for a path a delete
        // rule drops (that binding is removed at build — not a real break).
        private static Func<string, string> BuildVrcfRewriter(SerializedProperty content)
        {
            var arr = content.FindPropertyRelative("rewriteBindings");
            if (arr == null || !arr.isArray || arr.arraySize == 0) return null;
            var rules = new List<(string from, string to, bool delete)>();
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                var f = el.FindPropertyRelative("from");
                var t = el.FindPropertyRelative("to");
                var d = el.FindPropertyRelative("delete");
                rules.Add((f != null ? f.stringValue : "", t != null ? t.stringValue : "", d != null && d.boolValue));
            }
            return path =>
            {
                foreach (var (rawFrom, rawTo, delete) in rules)
                {
                    string from = TrimTrailingSlashes(rawFrom ?? "");
                    string to = TrimTrailingSlashes(rawTo ?? "");
                    if (from == "")
                    {
                        path = VrcfJoin(to, path);
                        if (delete) return null;
                    }
                    else if (path.StartsWith(from + "/", StringComparison.Ordinal))
                    {
                        path = VrcfJoin(to, path.Substring(from.Length + 1));
                        if (delete) return null;
                    }
                    else if (path == from)
                    {
                        path = to;
                        if (delete) return null;
                    }
                }
                return path;
            };
        }

        private static string TrimTrailingSlashes(string s)
        {
            while (s.EndsWith("/", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            return s;
        }

        // Replicates VF.Service.ClipRewritersService.Join (allowAdvancedOperators=true): '/'-join with a
        // leading-'/' reset, '..' pop, and '.'/'' segments omitted.
        private static string VrcfJoin(string a, string b)
        {
            var ret = new List<string>();
            foreach (var path in new[] { a, b })
            {
                if (path.StartsWith("/", StringComparison.Ordinal)) ret.Clear();
                foreach (var part in path.Split('/'))
                {
                    if (part == ".." && ret.Count > 0 && ret[ret.Count - 1] != "..") ret.RemoveAt(ret.Count - 1);
                    else if (part == "." || part == "") { /* omit */ }
                    else ret.Add(part);
                }
            }
            return string.Join("/", ret);
        }

        private static AutoResult? ParseVrcFury(Component c, AnimatorController controller)
        {
            if (!TryVrcfFrame(c, out var controllers, out var frame)) return null;
            if (!controllers.Contains(controller)) return null; // not OUR controller (silent skip, as before)
            return new AutoResult
            {
                Root = frame.Root, BuildRewrite = true,
                DetectionLine = "basis=auto→mount(" + PathOf(frame.Root) + ") [VRCFury FullController]",
                // Honour THIS FullController's rewriteBindings so the (demoted) broken-binding count is
                // truthful — without it, paths a declared rule relocates read as unresolvable (a lying count).
                PathRewrite = frame.PathRewrite,
            };
        }

        // Shared binding walk (reused by CheckAvatar): every clip a controller references, both float and
        // objref bindings, humanoid muscle/root curves skipped, each resolved against ANY of roots (first
        // hit ⇒ resolved). Returns the unresolved (clip, binding) pairs in CheckAnimator's traversal order
        // (clip-outer; float-then-objref inner) so a caller renders offenders in the exact same sequence.
        // <paramref name="pathRewrite"/> (default null ⇒ identity, CheckAnimator's behavior) transforms each
        // binding path before resolution — CheckAvatar passes the VRCF FullController rewriter so a binding is
        // resolved the way the build will (rewriteBindings then nearest-match). A rewrite returning null
        // means a delete-rule drops that binding at build, so it is skipped (not unresolved). The returned
        // pair always carries the ORIGINAL binding (what the .anim holds — what a repath must target).
        internal static List<(AnimationClip clip, EditorCurveBinding binding)> CollectUnresolvedBindings(
            AnimatorController controller, List<GameObject> roots, Func<string, string> pathRewrite = null)
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
                    if (IsHumanoidAnimatorCurve(b)) continue; // muscle/root curves have no scene object
                    var probe = b; // struct copy — preserves type/propertyName/isPPtrCurve, only path may change
                    if (pathRewrite != null)
                    {
                        string rewritten = pathRewrite(b.path);
                        if (rewritten == null) continue; // a delete-rule drops this binding at build — not a break
                        probe.path = rewritten;
                    }
                    bool resolved = false;
                    foreach (var root in roots)
                        if (AnimationUtility.GetAnimatedObject(root, probe) != null) { resolved = true; break; }
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

        // ----- Output -------------------------------------------------------------------------------

        private static string Emit(AnimatorController controller, LintResult rep, string detection, List<string> notes)
        {
            bool errorTierFired = rep.MissingMotion > 0 || rep.UndeclaredParam > 0 || rep.NonFloatBlendParam > 0
                                  || rep.EntryShadow > 0 || rep.DeadTransition > 0
                                  || (rep.BrokenBindingIsError && rep.BrokenBinding > 0);
            string result = errorTierFired ? "FAIL" : "PASS";

            // advisories total = advisory offenders (rules 5-9 + demoted broken bindings, which are
            // already placed in the Advisories list when brokenBinding is demoted).
            int advisories = rep.Advisories.Count;

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckAnimator] {0}: missingMotion={1} undeclaredParam={2} entryShadow={3} deadTransition={4} brokenBinding={5} advisories={6} => {7}",
                controller.name, rep.MissingMotion, rep.UndeclaredParam, rep.EntryShadow, rep.DeadTransition, rep.BrokenBinding, advisories, result);

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

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "animatorlint_" + controller.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogError(res);
            return res;
        }

        private static void AppendOffender(StringBuilder sb, LintOffender o) =>
            sb.Append("- **").Append(o.Kind).Append("** ").Append(o.Where).Append(" — ").Append(o.Detail).Append('\n');

        // ----- Scene resolver (duplicated from AgentInspector.FindByHierarchyPath — kept local so this
        //        tool adds no cross-file coupling) ------------------------------------------------------
        private static GameObject FindByHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
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

        // ----- Helpers ------------------------------------------------------------------------------

        private static string Refuse(string why)
        {
            string err = "[CheckAnimator] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        private static string PathOf(GameObject go)
        {
            if (go == null) return "—";
            var t = go.transform;
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }
    }
}
