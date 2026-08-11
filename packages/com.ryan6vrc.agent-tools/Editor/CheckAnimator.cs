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
        // AvatarObjectReference.AVATAR_ROOT — the referencePath a root-targeting MA scene ref carries.
        // Get(Component) resolves it to the avatar root, so the frame walk below must too; CheckAvatar's
        // scene-ref scan reads the same literal from here.
        internal const string MaAvatarRootSentinel = "$$$AVATAR_ROOT$$$";

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
            GameObject siteAvatarGO = null;           // the avatar this controller was linted ON, for the multiplicity count
            GameObject usedMount = null;              // the mount the basis actually resolved to

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
                siteAvatarGO = avatarGO;
                usedMount = mountGO ?? avatarGO;
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
                var siteGO = FindByHierarchyPath(mergeSite);
                // includeInactive: the no-arg overload skips inactive objects, and an authoring avatar is
                // routinely parked inactive — without this the rider is silently absent in the exact state
                // most lints run in, which is worse than not having it.
                var siteDesc = siteGO != null ? siteGO.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true) : null;
                siteAvatarGO = siteDesc != null ? siteDesc.gameObject : null;
                usedMount = d.Root;
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

            return Emit(controller, r, detection, notes, MergeSiteMultiplicity(controller, siteAvatarGO, usedMount, notes));
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
                    var m = ParseVrcFury(c, controller, notes);
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
        // a fail-loud caller can refuse. CheckAnimator's own Parse* wrappers instead skip SILENTLY:
        // "not our controller" / unreflected ⇒ null.

        internal enum FrameKind { DescriptorLayer, MA, VRCF }

        internal struct FrameResult
        {
            public GameObject Root;       // the binding-basis root (mount, or avatar root for Absolute)
            public FrameKind Kind;
            public bool IsAbsolute;       // MA Absolute pathMode (basis is the avatar root, not a mount)
            public string UnreflectedAnchor; // non-null ⇒ a required frame field or vendor invoke failed to reflect (fail loud)
            // VRCF only: the FullController's "Path Rewrite Rules" (rewriteBindings) as a path transform,
            // applied to each binding path BEFORE the nearest-match ancestor walk (the build applies them in
            // that order). null ⇒ identity (no rules). Returns null for a path a delete-rule drops (the
            // binding vanishes at build — not a real break). CheckAnimator ignores it; CheckAvatar applies it.
            public Func<string, string> PathRewrite;
            // VRCF only: the FullController's rootBindingsApplyToAvatar. When set, an EMPTY-path binding is
            // left at the avatar root by the build's nearest-match rewriter instead of being matched onto the
            // mount, so a resolver must not walk the mount for it.
            public bool RootBindingsApplyToAvatar;
        }

        // MA MergeAnimator: pathMode 0=Relative, 1=Absolute (confirmed live). Absolute ⇒ basis is the avatar
        // root. Relative ⇒ mount at the resolved relativePathRoot, resolved by INVOKING MA's own
        // Get(Component) on the boxed reference with the avatar root as container — the build's literal call
        // (MergeAnimatorProcessor: relativePathRoot.Get(avatarRootTransform)) — and a null from it lands on
        // the component's OWN GameObject, as the build's does. Invoking rather than mirroring is the point:
        // the hand-walked copy of Get's order this method used to carry was wrong twice (the inspector's
        // targetObject-first order, then a fresh copy missing the childless-"Armature" swap), so the order
        // now lives only in MA; nondestructive.md owns why the two Get overloads' orders differ. The boxed
        // copy also keeps Get's internal result cache cold and private — the live component's reference is
        // never touched. The hand-walk survives only as the loud, explicitly-labeled drift fallback below
        // (TryResolveSceneRef's two-tier shape). Returns true iff c is an MA MergeAnimator that references
        // a controller (out via controller).
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
                else if (avatarGO != null)
                {
                    // avatarGO null (no descriptor above the merge site — DetectAuto already says so out
                    // loud) is the build's FindAvatarTransformInParents miss: no invoke, own-GameObject
                    // fallback below, exactly as the build. With a root in hand, Get re-derives the avatar
                    // root internally anyway (FindAvatarTransformInParents walks to the OUTERMOST root), so
                    // a nested descriptor above c can no longer skew the frame the way the nearest-descriptor
                    // avatarGO handed in by DetectAuto could.
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
                        unreflected = unreflected ?? "MA.Get(Component)"; // in-band caveat beside the console line
                        // Degraded self-resolve from the serialized children, in Get(Component)'s order —
                        // loud, because a silent degrade would hide exactly the drift the invoke tier exists
                        // to survive. This copy of the order is fallback-only by design: while MA reflects,
                        // it never runs, so it can no longer drift silently into the primary result.
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

        // Get(Component)'s last step (AvatarObjectReference.cs:110-125, MA issue #308): some avatars carry a
        // second, childless "Armature" to move the VRChat eye position, so a path landing on a childless
        // "Armature" is redirected to the same-named sibling that has children. Applies to the PATH branch
        // only — MA runs it on Find()'s result, not on a targetObject.
        //
        // FALLBACK-ONLY since the invoke tier landed: the primary path invokes MA's real Get, which does this
        // swap itself. The degraded self-resolve still needs it, and skipping it there is silent: the decoy is
        // childless by construction, so mounting at it fails every binding in the merged controller — a flood
        // of false clip-binding offenders under a clean Notes section, with nothing left to say why.
        internal static Transform ResolveArmatureDecoy(Transform resolved)
        {
            if (resolved == null || resolved.name != "Armature" || resolved.childCount != 0) return resolved;
            var parent = resolved.parent;
            if (parent == null) return resolved;
            foreach (Transform sibling in parent)
                if (sibling.name == "Armature" && sibling.childCount > 0) return sibling;
            return resolved;
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
            var pathRewrite = BuildVrcfRewriter(content, ref unreflected);
            // AnimationBindingUtils.ResolveTarget short-circuits on `path == "" && RootBindingsApplyToAvatar`,
            // so with the flag set a root-level binding stays at the AVATAR root instead of being matched onto
            // the mount. A caller resolving it against the mount would read the mount as the animated node.
            var rootToAvatar = content.FindPropertyRelative("rootBindingsApplyToAvatar");
            frame = new FrameResult
            {
                Root = root, Kind = FrameKind.VRCF, IsAbsolute = false, UnreflectedAnchor = unreflected,
                PathRewrite = pathRewrite, // this component's rules only — no cross-controller bleed
                RootBindingsApplyToAvatar = rootToAvatar != null && rootToAvatar.boolValue,
            };
            return true;
        }

        // The VRCFury FullController "Path Rewrite Rules" (content.rewriteBindings) as a path transform. The
        // build runs these BEFORE the nearest-match ancestor walk, so a caller resolves the rewritten path
        // against the ancestor chain. The transform INVOKES the build's own implementation —
        // VF.Utils.AnimationBindingUtils.RewriteRelativePath(path, rules), on THIS content's live
        // rewriteBindings list, so two FullControllers on one mount never cross-contaminate — rather than
        // replicating it: invoked, the rule semantics can't drift, and no near-copy of VRCFury code lives in
        // this repo (VRCFury is not FOSS-licensed — a replication is a licensing question as well as a drift
        // hazard). RewriteRelativePath only reads the rule rows, so handing it the live list mutates nothing.
        // Returns null when there are no rules (identity). The transform returns null for a path a delete
        // rule drops (that binding is removed at build — not a real break).
        //
        // Rules present + RewriteRelativePath unreachable (drift) or the model unboxable ⇒ anchor via
        // `unreflected` (the R-H fail-loud rail) and identity: resolving with a silent identity rewrite would
        // fabricate plausible-but-false binding results with nothing left to say why, so the caveat rides the
        // frame. The anchor string stays "VRCF.RewritePath" — it names OUR pin (ParseVrcFury routes on it),
        // not the vendor method du jour.
        private static Func<string, string> BuildVrcfRewriter(SerializedProperty content, ref string unreflected)
        {
            var arr = content.FindPropertyRelative("rewriteBindings");
            if (arr == null || !arr.isArray || arr.arraySize == 0) return null; // no rules ⇒ identity
            object model = null;
            try { model = content.managedReferenceValue; } catch { /* unboxable ⇒ anchored below */ }
            var mi = VendorReflect.ResolveVrcfRewritePath();
            // The rules list is read off the boxed model by the FIELD the serialized property names, so the
            // list handed to the vendor method is the same live object the build reads — never a re-parse.
            object rules = null;
            if (model != null)
            {
                // NonPublic included so a future [SerializeField]-private migration of the field doesn't
                // desync this read from the SerializedProperty arraySize check above (which would still see
                // rules and anchor the frame for a field that merely changed accessibility).
                var rf = model.GetType().GetField("rewriteBindings",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (rf != null) rules = rf.GetValue(model);
            }
            if (rules == null || mi == null || !mi.GetParameters()[1].ParameterType.IsInstanceOfType(rules))
            {
                unreflected = unreflected ?? "VRCF.RewritePath";
                return null;
            }
            bool warned = false; // the rewriter runs once per binding path; a broken rule row throws for ALL of them
            return path =>
            {
                try { return (string)mi.Invoke(null, new object[] { path, rules }); }
                catch (Exception e)
                {
                    // A post-pin throw is a broken rule row, not API drift; leave the path un-rewritten and
                    // say so ONCE — per-path repeats would flood the console on a real FX controller.
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
            if (!controllers.Contains(controller)) return null; // not OUR controller (silent skip, as before)
            // Rules present but the RewritePath pin unreachable is the one anchor that can ride a frame
            // carrying OUR controller (the field-absent anchors arrive on frames the Contains check already
            // skips), so dropping it here would run the walk with an identity rewrite and say nothing —
            // an inflated brokenBinding count with false sample offenders. Surface it as a note.
            if (frame.UnreflectedAnchor == "VRCF.RewritePath")
                notes.Add("VRCFury Path Rewrite Rules on '" + PathOf(c.gameObject) + "' could not be applied " +
                          "(RewritePath did not reflect — VRCFury API drift/absent), so brokenBinding may be " +
                          "inflated by paths those rules would relocate.");
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
        // means a delete-rule drops that binding at build, so it is skipped (not unresolved). A rewrite
        // yielding a leading-`/` path is VRCFury's absolute form (AnimationBindingUtils.ResolveTarget's
        // absolute branch, nondestructive.md): the build resolves it from the avatar root with no ancestor
        // walk, so the probe here does the same — against the LAST entry of roots only, which every caller
        // builds as the avatar-most root (AncestorChain appends upward; the explicit basis appends avatarGO
        // last). The returned pair always carries the ORIGINAL binding (what the .anim holds — what a repath
        // must target).
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
                    bool absolute = false;
                    if (pathRewrite != null)
                    {
                        string rewritten = pathRewrite(b.path);
                        if (rewritten == null) continue; // a delete-rule drops this binding at build — not a break
                        absolute = rewritten.StartsWith("/");
                        probe.path = absolute ? rewritten.TrimStart('/') : rewritten;
                    }
                    bool resolved = false;
                    if (absolute)
                    {
                        if (roots.Count > 0 &&
                            AnimationUtility.GetAnimatedObject(roots[roots.Count - 1], probe) != null)
                            resolved = true;
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

        // ── Anchor seam ───────────────────────────────────────────────────────────────────────────────
        // The cross-framework break docs/nondestructive.md §Choosing a framework rule 1 names: a clip
        // VRCFury merged that paths through a node Modular Avatar moves is dropped from the built FX,
        // while the reverse survives (VRCFury repaths the merged clips alongside its own moves; MA has no
        // counterpart running late enough to return the favour). The build names the dropped binding and
        // the emptied layer but NEVER the anchor that caused it, which is the whole of this walk's value.
        //
        // This asks a STATE question and nothing else: does any node on a resolved binding's path carry an
        // MA relocator? It models no part of what the build will move — not MergeArmature's recursion
        // boundaries, not where a proxy lands, not whether a wholesale move preserves relative structure.
        // Anything the caller's anchor map contains is an anchor; membership is the caller's to decide.
        // Per docs/tool-design.md §Lifting that keeps the evaluation judgment-free — it asserts a seam
        // exists, never that the seam is wrong. Whether a given crossing is intentional is the reading
        // agent's call, which is why CheckAvatar files this as CLASSIFY and not FAIL.
        internal struct AnchorSeamHit
        {
            public AnimationClip Clip;
            public EditorCurveBinding Binding; // the ORIGINAL binding (what the .anim holds)
            public GameObject Anchor;          // the nearest node on the path carrying a relocator
            public string AnchorLabel;         // that relocator's short type name(s)
        }

        // roots/pathRewrite carry the same meaning as CollectUnresolvedBindings' and are resolved
        // identically, deliberately: the two classes must never disagree about whether a binding resolves,
        // or one break would land in both classes (or neither). A binding that does NOT resolve is skipped
        // here — clip-binding owns it, and a path with no scene node has no ancestry to walk.
        internal static List<AnchorSeamHit> CollectAnchorSeamBreaks(
            AnimatorController controller, List<GameObject> roots, GameObject avatarRoot,
            Dictionary<GameObject, string> anchors, Func<string, string> pathRewrite = null,
            bool rootBindingsApplyToAvatar = false)
        {
            var hits = new List<AnchorSeamHit>();
            if (controller == null || roots == null || roots.Count == 0 || avatarRoot == null
                || anchors == null || anchors.Count == 0) return hits;

            foreach (var clip in AnimatorClipWalk.CollectClips(controller))
            {
                if (clip == null) continue;
                var bindings = new List<EditorCurveBinding>();
                bindings.AddRange(AnimationUtility.GetCurveBindings(clip));
                bindings.AddRange(AnimationUtility.GetObjectReferenceCurveBindings(clip));
                foreach (var b in bindings)
                {
                    if (IsHumanoidAnimatorCurve(b)) continue; // muscle/root curves have no scene object
                    // AnimatorBindingsAlwaysTargetRoot forces path="" on EVERY Animator-typed binding, and
                    // FullControllerBuilder applies it LAST in the combine ("we do this after rewriting paths
                    // to ensure animator bindings all hit \"\""). So the binding lands at the avatar root and
                    // crosses no relocator whatever its authored path said. Not a prediction about a move —
                    // a declared, unconditional rewrite of this whole binding class. The clip-binding walk
                    // keeps the narrower humanoid-only skip: a broken Animator binding is still a break there.
                    if (b.type == typeof(Animator)) continue;
                    var probe = b;
                    bool absolute = false;
                    if (pathRewrite != null)
                    {
                        string rewritten = pathRewrite(b.path);
                        if (rewritten == null) continue; // a delete-rule drops this binding at build
                        absolute = rewritten.StartsWith("/");
                        probe.path = absolute ? rewritten.TrimStart('/') : rewritten;
                    }
                    // Walk the object the binding ACTUALLY resolves to rather than re-finding the path by
                    // string: GetAnimatedObject is the resolver the clip-binding class already trusts, and
                    // taking its result removes a silent-skip branch (a path Transform.Find cannot retrace).
                    // An empty path under rootBindingsApplyToAvatar resolves against the avatar root ALONE —
                    // the build leaves it there, so walking the mount would read the mount as the animated node.
                    // A leading-`/` rewrite output is VRCFury's absolute form and resolves the same way:
                    // avatar root alone, no ancestor walk (CollectUnresolvedBindings mirrors this).
                    var probeRoots = (absolute || (probe.path.Length == 0 && rootBindingsApplyToAvatar))
                        ? new List<GameObject> { avatarRoot } : roots;
                    UnityEngine.Object animated = null;
                    foreach (var root in probeRoots)
                    {
                        animated = AnimationUtility.GetAnimatedObject(root, probe);
                        if (animated != null) break;
                    }
                    var leaf = AsTransform(animated);
                    if (leaf == null) continue;

                    // Leaf → avatar root, INCLUSIVE at both ends, with no exemption anywhere along it. A
                    // relocator on the leaf counts; one on the merge mount counts; one a composer placed
                    // above the module counts. The nearest one is named because it is the innermost node a
                    // repair has to move, and a second anchor further up does not change the repair.
                    for (var t = leaf; t != null; t = t.parent)
                    {
                        if (anchors.TryGetValue(t.gameObject, out var label))
                        {
                            hits.Add(new AnchorSeamHit
                            {
                                Clip = clip, Binding = b, Anchor = t.gameObject, AnchorLabel = label,
                            });
                            break;
                        }
                        if (t.gameObject == avatarRoot) break;
                    }
                }
            }
            return hits;
        }

        // GetAnimatedObject returns the animated Object itself — a GameObject for m_IsActive, a Component
        // (Transform, Renderer, a VRC dynamics behaviour) for everything else. Both carry the transform.
        private static Transform AsTransform(UnityEngine.Object animated)
        {
            var go = animated as GameObject;
            if (go != null) return go.transform;
            var c = animated as Component;
            return c != null ? c.transform : null;
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

        /// <summary>The <c>mergeSites=</c> rider: how many surfaces on this avatar mount the controller
        /// under test, and which one the basis resolved to. Judgment-free multiplicity, reported at the door
        /// that produces the verdict, because basis selection is the agent's call and a wrong one flips
        /// PASS/FAIL on an unchanged controller with nothing in the output to say a second site existed.
        /// It does NOT touch <see cref="DetectAuto"/>'s single-site contract: that method still refuses on
        /// ambiguity AT ONE SITE, while this counts sites across the whole avatar, which is a different
        /// question. Returns null (no token) when there is no avatar to count over or only one site exists —
        /// a rider that fires on every lint is a rider nobody reads.</summary>
        private static string MergeSiteMultiplicity(AnimatorController controller, GameObject avatarGO,
                                                    GameObject usedMount, List<string> notes)
        {
            if (avatarGO == null || controller == null) return null;
            List<MergeSurfaces.Surface> surfaces;
            try
            {
                surfaces = MergeSurfaces.Enumerate(avatarGO, avatarGO.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(),
                    vrcfOnly: false, (c, anchor) => notes.Add("merge-site scan: frame field '" + anchor + "' on "
                        + c.GetType().Name + " @ " + PathOf(c.gameObject) + " did not reflect — the site count below is best-effort."));
            }
            catch (Exception e)
            {
                // The count is a rider, never the verdict: a scan failure says so and the lint stands.
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

            // advisories total = advisory offenders (rules 5-9 + demoted broken bindings, which are
            // already placed in the Advisories list when brokenBinding is demoted).
            int advisories = rep.Advisories.Count;

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckAnimator] {0}: missingMotion={1} undeclaredParam={2} nonFloatBlendParam={3} nonFloatParamCurve={4} driverOnAnimatedParam={5} entryShadow={6} deadTransition={7} brokenBinding={8} advisories={9} => {10}",
                controller.name, rep.MissingMotion, rep.UndeclaredParam, rep.NonFloatBlendParam, rep.NonFloatParamCurve, rep.DriverOnAnimatedParam, rep.EntryShadow, rep.DeadTransition, rep.BrokenBinding, advisories, result);
            // Rides AFTER the verdict token so the summary's leading grammar is unchanged for anything
            // parsing it, and so a reader who stops at the verdict still sees the count that qualifies it.
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

        private static string PathOf(GameObject go) => MergeSurfaces.PathOf(go);
    }
}
