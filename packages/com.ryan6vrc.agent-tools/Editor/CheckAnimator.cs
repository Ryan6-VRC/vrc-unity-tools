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
        // a fail-loud caller can refuse. CheckAnimator's own Parse* wrappers instead skip SILENTLY:
        // "not our controller" / unreflected ⇒ null.

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

        // ----- Anchor seam: the binding an MA build-time move kills under a VRCFury merge ---------------
        //
        // nondestructive.md §Reference hardening owns the mechanism. MA's passes run inside NDMF at -11000;
        // VRCFury applies at -10000 and re-resolves every merged binding against the POST-MA hierarchy by a
        // nearest-match prefix walk up from the FullController's object. A binding pathing through a node MA
        // moved out of the module subtree finds no valid prefix, and the merged FX stops driving the object
        // the author meant.
        //
        // ONE DIRECTION ONLY, and the asymmetry is a fact about the two builds rather than a scope decision.
        // VRCFury tracks its own moves: ArmatureLink relocates through ObjectMoveService, which records
        // (oldPath, newPath) and - in the same pass, after the move loop - calls ApplyDeferred() to rewrite
        // every clip in ControllersService.GetAllUsedControllers(). That set is the descriptor's controllers,
        // which by then already carry whatever MA merged at -11000, so an MA MergeAnimator clip pathing
        // through an ArmatureLinked node is REPAIRED, not broken. FeatureOrder says so outright: FullController
        // is ordered before ArmatureLink because it "needs to happen before any objects are moved, so otherwise
        // the imported animations would not be adjusted to point to the new moved object paths". MA has no
        // counterpart running late enough to do the same for VRCFury, because a FullController's clips are not
        // on the avatar yet at -11000. Hence: MA movers under a VRCFury merge break; the reverse does not, and
        // neither does a move and a merge in the SAME framework (each build repaths what it moved).
        //
        // This is the family's only BUILD-PREDICTION predicate. Every artifact a pre-build check can see is
        // VALID - the authored prefab really does contain the path, and the binding resolves right now - so
        // unlike every other class here it asserts something about what the build will MOVE. Two properties
        // hold it up, and a third is deliberately NOT claimed:
        //   - the frame is MEASURED, not predicted. `roots` is the same nearest-match ancestor chain the
        //     binding is resolved against (ClipRewritersService walks upward from the mount, first match wins,
        //     and so does AncestorChain), so the root a binding FIRST resolves at is the frame the build picks.
        //     Nothing here replicates VRCFury's per-binding prop-root-vs-avatar-root choice - it reads the
        //     answer off the walk.
        //   - the animated LEAF is INCLUDED. A mover on the leaf kills the binding by the identical walk, so
        //     excluding it would buy nothing but a false negative.
        //   - NOT claimed: false-positive-free in general. The walk stops at the mover and never asks where the
        //     moved node LANDS, so a proxy whose target is the avatar root itself can leave a higher prefix
        //     that still validates - a binding that survives, reported anyway. That needs a proxy target of the
        //     avatar root rather than the documented AsChildAtRoot-onto-a-humanoid-bone idiom, so it is a
        //     narrow over-report, not a general one. Closing it means modelling the post-move hierarchy.
        internal struct AnchorSeamHit
        {
            public AnimationClip Clip;
            public EditorCurveBinding Binding; // the ORIGINAL binding (what the .anim holds)
            public GameObject Mover;           // the node MA relocates at build
            public string MoverLabel;          // the component that will move it
        }

        // Whether a mover carries its whole subtree to one new parent, which decides the FRAME-ROOT case.
        // A wholesale mover at the frame root is the documented safe anchor: the module moves intact and its
        // component-relative paths still resolve, so it is excluded. A SCATTERING mover is not safe there:
        // MergeArmature reparents each matched bone individually onto a different base bone (MergeArmatureHook
        // recurses per child with its own childNewParent) and renames them, so an interior binding dies even
        // though the mover sits at the frame root.
        internal struct MoverInfo
        {
            public string Label;
            public bool Scatters;
        }

        // The nodes MA relocates at build, over `scanRoot` (the avatar root in CheckAvatar; the entry prefab
        // in the vrc-patterns gate). Scanned from a root rather than from the merged module, because a mover
        // need not sit inside it.
        //
        // This is an ENUMERATED ALLOWLIST against the MA version pinned in this venue, NOT a derived property
        // of the framework - MA has no "I move objects" interface to reflect on. Re-derive it after an MA bump
        // by grepping the Editor passes for SetParent; every entry below is one such call site:
        //   BoneProxy              BoneProxyProcessor             reparents its own GameObject
        //   MergeArmature          MergeArmatureHook              reparents each bone under it, individually
        //   WorldFixedObject       WorldFixedObjectProcessor      reparents its own GameObject under a generated root
        //   VisibleHeadAccessory   VisibleHeadAccessoryProcessor  reparents its own GameObject under a shim
        //   ReplaceObject          ReplaceObjectPass              reparents its own GameObject onto the target's parent
        // ReplaceObject ALSO reparents the replaced object's children onto the replacement, and that second
        // move is not modelled: the replaced object is named by an AvatarObjectReference this walk does not
        // resolve. A binding pathing through the REPLACED object's children is a known false negative.
        //
        // A mover whose target does not resolve moves nothing at build (BoneProxyProcessor guards its
        // SetParent on `proxy.Target != null && ValidateTarget(...) == OK`), but is still counted: an
        // unresolved anchor is a broken module, not a licence to animate through it.
        internal static Dictionary<GameObject, MoverInfo> CollectMovers(GameObject scanRoot)
        {
            var movers = new Dictionary<GameObject, MoverInfo>();
            if (scanRoot == null) return movers;
            foreach (var c in scanRoot.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string label = null;
                bool scatters = false;
                switch (c.GetType().FullName)
                {
                    case "nadena.dev.modular_avatar.core.ModularAvatarBoneProxy": label = "MA BoneProxy"; break;
                    case "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature": label = "MA MergeArmature"; scatters = true; break;
                    case "nadena.dev.modular_avatar.core.ModularAvatarWorldFixedObject": label = "MA WorldFixedObject"; break;
                    case "nadena.dev.modular_avatar.core.ModularAvatarVisibleHeadAccessory": label = "MA VisibleHeadAccessory"; break;
                    case "nadena.dev.modular_avatar.core.ModularAvatarReplaceObject": label = "MA ReplaceObject"; break;
                }
                if (label == null) continue;
                // First writer wins, except that a scattering mover upgrades the entry: two movers on one
                // GameObject is pathological, and reporting the one with the WIDER consequence is the safe read.
                if (movers.TryGetValue(c.gameObject, out var existing) && !(scatters && !existing.Scatters)) continue;
                movers[c.gameObject] = new MoverInfo { Label = label, Scatters = scatters };
            }
            return movers;
        }

        // Walk a VRCFury-merged controller's bindings and report the ones an MA move kills at build. Mirrors
        // CollectUnresolvedBindings' traversal, skips, and rewrite order exactly; the difference is that it
        // acts on the bindings that DO resolve. One hit per binding: the walk stops at the first mover found
        // going up from the leaf, which is the node closest to the break.
        internal static List<AnchorSeamHit> CollectAnchorSeamBreaks(
            AnimatorController controller, List<GameObject> roots,
            Dictionary<GameObject, MoverInfo> movers, Func<string, string> pathRewrite = null)
        {
            var hits = new List<AnchorSeamHit>();
            if (controller == null || roots == null || roots.Count == 0 || movers == null || movers.Count == 0)
                return hits;

            foreach (var clip in AnimatorClipWalk.CollectClips(controller))
            {
                if (clip == null) continue;
                var bindings = new List<EditorCurveBinding>();
                bindings.AddRange(AnimationUtility.GetCurveBindings(clip));
                bindings.AddRange(AnimationUtility.GetObjectReferenceCurveBindings(clip));
                foreach (var b in bindings)
                {
                    if (IsHumanoidAnimatorCurve(b)) continue;
                    // An Animator-typed binding is retargeted to the avatar's own Animator whatever its
                    // authored path (FullControllerBuilder composes AnimatorBindingsAlwaysTargetRoot after the
                    // nearest-match walk), so no node on its path can break it.
                    if (b.type == typeof(Animator)) continue;
                    var probe = b;
                    if (pathRewrite != null)
                    {
                        string rewritten = pathRewrite(b.path);
                        if (rewritten == null) continue; // a delete-rule drops this binding at build
                        probe.path = rewritten;
                    }
                    GameObject frame = null;
                    foreach (var root in roots)
                        if (AnimationUtility.GetAnimatedObject(root, probe) != null) { frame = root; break; }
                    if (frame == null) continue;              // unresolved - the broken-binding class owns it
                    if (string.IsNullOrEmpty(probe.path)) continue; // the leaf IS the frame root: nothing between

                    var leaf = frame.transform.Find(probe.path);
                    if (leaf == null) continue; // resolved by a route Find cannot retrace - never guess a chain
                    for (var t = leaf; t != null; t = t.parent)
                    {
                        bool atFrameRoot = t == frame.transform;
                        if (movers.TryGetValue(t.gameObject, out var m) && (!atFrameRoot || m.Scatters))
                        {
                            hits.Add(new AnchorSeamHit { Clip = clip, Binding = b, Mover = t.gameObject, MoverLabel = m.Label });
                            break;
                        }
                        if (atFrameRoot) break;
                    }
                }
            }
            return hits;
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
                "[CheckAnimator] {0}: missingMotion={1} undeclaredParam={2} nonFloatBlendParam={3} entryShadow={4} deadTransition={5} brokenBinding={6} advisories={7} => {8}",
                controller.name, rep.MissingMotion, rep.UndeclaredParam, rep.NonFloatBlendParam, rep.EntryShadow, rep.DeadTransition, rep.BrokenBinding, advisories, result);

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
