using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
    public static class AnimatorLint
    {
        // VRChat reserved/built-in animator parameters — referenced constantly, declared nowhere, so
        // they must be exempt from undeclaredParam or the rule FAILs every real controller. This mirrors
        // the SDK's reserved-parameter list (VRCExpressionParameters / the avatar-parameters docs); it is
        // the source of truth, this array only tracks it. If the SDK exposes this set at compile time,
        // prefer querying it over this literal.
        private static readonly string[] VrcReservedParams =
        {
            "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight",
            "GestureRightWeight", "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude",
            "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation",
            "Earmuffs", "IsOnFriendsList", "AvatarVersion", "ScaleModified", "ScaleFactor",
            "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent",
        };

        // ----- Public API ---------------------------------------------------------------------------

        /// <summary>Lint <paramref name="controller"/> against the v1 rule set. <paramref name="basis"/>
        /// is <c>auto</c> (detect the binding-basis root from a merge component at <paramref name="mergeSite"/>)
        /// or <c>explicit</c> (caller names <paramref name="avatarRoot"/> / <paramref name="mountRoot"/> as
        /// active-scene hierarchy paths). Returns a one-line summary; a real run ends with the RunLog path
        /// in-band (<c>… =&gt; RESULT | log=&lt;path&gt;</c>). A bad-input/refusal early return is a bare
        /// <c>[AnimatorLint] FAIL: …</c> with no trailer.</summary>
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

            // ---- Collect the state/state-machine topology once (owning layers only) -------------------
            var states = new List<StateCtx>();
            var machines = new List<SmCtx>();
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                if (layer.syncedLayerIndex >= 0) continue;   // synced layers re-skin the source layer's states
                if (layer.stateMachine == null) continue;
                CollectSm(layer.stateMachine, layer.name, li, "", states, machines);
            }

            var rep = new Report { Controller = controller };
            rep.BrokenBindingIsError = !buildRewrite;

            // ---- Error-tier rules ---------------------------------------------------------------------
            var dangling = RecoverDanglingMotionGuids(controller);
            RuleMissingMotion(states, dangling, rep);
            RuleUndeclaredParam(controller, states, machines, rep);
            RuleEntryShadow(machines, rep);
            RuleBrokenBinding(controller, roots, buildRewrite, pathRewrite, rep, notes);

            // ---- Advisory-tier rules ------------------------------------------------------------------
            RuleWdInconsistency(states, rep);
            RuleOrphanSubAsset(controller, notes, rep);
            RuleDeadLayer(controller, rep);
            RuleCrossPackageAndArchive(controller, rep);

            return Emit(rep, detection, notes);
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

        // ----- Reusable frame detection (shared with AvatarLint) ------------------------------------
        // A "frame" is the binding-basis root a merge component establishes for the controller(s) it
        // mounts, plus how it was derived. The Try* helpers run on ANY subtree component (they self-check
        // type + controller reference), DISCOVER the referenced controller(s) so a caller can enumerate
        // mount sites, and set UnreflectedAnchor (naming a required frame field that failed to reflect) so
        // a fail-loud caller can refuse. AnimatorLint's own Parse* wrappers keep the historical SILENT
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
            // binding vanishes at build — not a real break). AnimatorLint ignores it; AvatarLint applies it.
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

        // ----- Rule 1: missingMotion (error) --------------------------------------------------------
        // A state whose motion reference is present but the asset is gone. NEVER fires on a clean-empty
        // motion (instanceID 0) — that is a standard idiom. The dangling GUID is the only surviving handle.
        private static void RuleMissingMotion(List<StateCtx> states, List<string> dangling, Report rep)
        {
            foreach (var s in states)
            {
                var st = s.State;
                if (st == null || st.motion != null) continue;
                var mp = new SerializedObject(st).FindProperty("m_Motion");
                if (mp == null || mp.objectReferenceInstanceIDValue == 0) continue; // clean-empty
                rep.MissingMotion++;
                string guid = dangling.Count == 1 ? "guid=" + dangling[0]
                            : dangling.Count > 1 ? "guid ∈ {" + string.Join(", ", dangling) + "}"
                            : "guid unrecoverable from controller YAML";
                rep.Errors.Add(new Offender { Kind = "missingMotion", Where = s.Path, Detail = "dangling motion reference — " + guid });
            }
        }

        // ----- Rule 2: undeclaredParam (error) ------------------------------------------------------
        private static void RuleUndeclaredParam(AnimatorController controller, List<StateCtx> states,
            List<SmCtx> machines, Report rep)
        {
            var declared = new HashSet<string>();
            foreach (var p in controller.parameters) declared.Add(p.name);
            var exempt = new HashSet<string>(VrcReservedParams);

            // name -> first location it was referenced (for the offender handle)
            var referenced = new Dictionary<string, string>();
            void Ref(string name, string where)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (!referenced.ContainsKey(name)) referenced[name] = where;
            }
            void Conds(AnimatorCondition[] conds, string where)
            {
                if (conds == null) return;
                foreach (var cd in conds) Ref(cd.parameter, where);
            }

            foreach (var m in machines)
            {
                foreach (var t in m.Sm.anyStateTransitions) Conds(t.conditions, m.Path + " AnyState");
                foreach (var t in m.Sm.entryTransitions) Conds(t.conditions, m.Path + " Entry");
                // Sub-state-machine → sub-state-machine transitions carry conditions too; without this a
                // param used only on an SM→SM transition escapes the rule (false-negative).
                foreach (var child in m.Sm.stateMachines)
                    if (child.stateMachine != null)
                        foreach (var t in m.Sm.GetStateMachineTransitions(child.stateMachine))
                            Conds(t.conditions, m.Path + " → " + child.stateMachine.name);
                if (m.Sm.behaviours != null) foreach (var b in m.Sm.behaviours) DriverParams(b, m.Path + " (SM behaviour)", Ref);
            }
            foreach (var s in states)
            {
                var st = s.State;
                foreach (var t in st.transitions) Conds(t.conditions, s.Path);
                if (st.speedParameterActive) Ref(st.speedParameter, s.Path + " speedParameter");
                if (st.timeParameterActive) Ref(st.timeParameter, s.Path + " motionTime");
                if (st.mirrorParameterActive) Ref(st.mirrorParameter, s.Path + " mirrorParameter");
                if (st.cycleOffsetParameterActive) Ref(st.cycleOffsetParameter, s.Path + " cycleOffset");
                BlendParams(st.motion, s.Path, Ref);
                if (st.behaviours != null) foreach (var b in st.behaviours) DriverParams(b, s.Path + " (driver)", Ref);
            }

            foreach (var kv in referenced)
            {
                if (declared.Contains(kv.Key) || exempt.Contains(kv.Key)) continue;
                rep.UndeclaredParam++;
                rep.Errors.Add(new Offender
                {
                    Kind = "undeclaredParam", Where = kv.Value,
                    Detail = "parameter `" + kv.Key + "` referenced but not declared on the controller (exempt set tracks the VRC SDK reserved list)"
                });
            }
        }

        private static void BlendParams(Motion m, string where, Action<string, string> reff)
        {
            if (!(m is BlendTree bt)) return;
            if (bt.blendType != BlendTreeType.Direct)
            {
                reff(bt.blendParameter, where + " blendParameter");
                if (Is2D(bt.blendType)) reff(bt.blendParameterY, where + " blendParameterY");
            }
            foreach (var ch in bt.children)
            {
                if (bt.blendType == BlendTreeType.Direct) reff(ch.directBlendParameter, where + " directBlendParameter");
                BlendParams(ch.motion, where, reff);
            }
        }

        private static void DriverParams(StateMachineBehaviour b, string where, Action<string, string> reff)
        {
            if (!(b is VRC.SDKBase.VRC_AvatarParameterDriver drv) || drv.parameters == null) return;
            foreach (var p in drv.parameters)
            {
                reff(p.name, where);
                if (p.type == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy) reff(p.source, where + " (source)");
            }
        }

        // ----- Rule 3: entryShadow (error, deterministic) -------------------------------------------
        // An earlier UNCONDITIONAL entry transition makes every later entry transition unreachable.
        private static void RuleEntryShadow(List<SmCtx> machines, Report rep)
        {
            foreach (var m in machines)
            {
                var ets = m.Sm.entryTransitions;
                int firstUncond = -1;
                for (int i = 0; i < ets.Length; i++)
                    if (ets[i].conditions == null || ets[i].conditions.Length == 0) { firstUncond = i; break; }
                if (firstUncond < 0 || firstUncond >= ets.Length - 1) continue;
                int shadowed = ets.Length - 1 - firstUncond;
                rep.EntryShadow += shadowed;
                rep.Errors.Add(new Offender
                {
                    Kind = "entryShadow", Where = m.Path,
                    Detail = shadowed + " entry transition(s) after the unconditional entry at index " + firstUncond + " are unreachable"
                });
            }
        }

        // ----- Rule 4: brokenBinding (error, or advisory under a build-rewrite auto site) -----------
        private static void RuleBrokenBinding(AnimatorController controller, List<GameObject> roots,
            bool buildRewrite, Func<string, string> pathRewrite, Report rep, List<string> notes)
        {
            if (roots.Count == 0)
            {
                notes.Add("broken-binding rule skipped: no basis root available to resolve clip bindings against.");
                return;
            }
            // Demoted (build-rewrite auto) bindings are collapsed into ONE advisory with a small sample:
            // under MA/VRCFury each unresolved binding is expected pre-build (paths get rewritten), so
            // hundreds of identical per-line advisories would only drown the digest. On the error-tier
            // path each broken binding is a genuine named failure and gets its own line.
            var demotedSamples = new List<string>();

            foreach (var (clip, b) in CollectUnresolvedBindings(controller, roots, pathRewrite))
            {
                rep.BrokenBinding++;
                if (rep.BrokenBindingIsError)
                    rep.Errors.Add(new Offender
                    {
                        Kind = "brokenBinding", Where = clip.name,
                        Detail = "binding path='" + b.path + "' type=" + b.type.Name + " prop='" + b.propertyName + "' resolves to no object"
                    });
                else if (demotedSamples.Count < 3)
                    demotedSamples.Add(clip.name + ":" + b.path);
            }

            if (!rep.BrokenBindingIsError && rep.BrokenBinding > 0)
                rep.Advisories.Add(new Offender
                {
                    Kind = "brokenBinding (demoted)", Where = rep.BrokenBinding + " binding(s)",
                    Detail = "unresolvable in the authored scene; MA/VRCFury rewrite binding paths at build so this cannot be verified pre-build"
                            + (demotedSamples.Count > 0 ? " — e.g. " + string.Join(", ", demotedSamples) : "")
                });
        }

        // Shared binding walk (reused by AvatarLint): every clip a controller references, both float and
        // objref bindings, humanoid muscle/root curves skipped, each resolved against ANY of roots (first
        // hit ⇒ resolved). Returns the unresolved (clip, binding) pairs in AnimatorLint's traversal order
        // (clip-outer; float-then-objref inner) so a caller renders offenders in the exact same sequence.
        // <paramref name="pathRewrite"/> (default null ⇒ identity, AnimatorLint's behavior) transforms each
        // binding path before resolution — AvatarLint passes the VRCF FullController rewriter so a binding is
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

        // ----- Rule 5: wdInconsistency (advisory) — WITHIN one layer only ---------------------------
        private static void RuleWdInconsistency(List<StateCtx> states, Report rep)
        {
            var byLayer = new Dictionary<int, int[]>(); // li -> [on, off]
            var layerName = new Dictionary<int, string>();
            foreach (var s in states)
            {
                if (!byLayer.TryGetValue(s.LayerIndex, out var oo)) { oo = new int[2]; byLayer[s.LayerIndex] = oo; layerName[s.LayerIndex] = s.LayerName; }
                if (s.State.writeDefaultValues) oo[0]++; else oo[1]++;
            }
            foreach (var kv in byLayer)
            {
                if (kv.Value[0] > 0 && kv.Value[1] > 0)
                    rep.Advisories.Add(new Offender
                    {
                        Kind = "wdInconsistency", Where = "layer '" + layerName[kv.Key] + "'",
                        Detail = "states disagree on Write Defaults (on=" + kv.Value[0] + " off=" + kv.Value[1] + ")"
                    });
            }
        }

        // ----- Rule 6: orphanSubAsset (advisory) — complete reachability walk -----------------------
        private static void RuleOrphanSubAsset(AnimatorController controller, List<string> notes, Report rep)
        {
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) { notes.Add("orphan-sub-asset rule skipped: controller is not a saved asset."); return; }

            var reachable = new HashSet<UnityEngine.Object>();
            void AddMotion(Motion m)
            {
                if (m is BlendTree bt && reachable.Add(bt))
                    foreach (var ch in bt.children) AddMotion(ch.motion);
            }
            void AddSm(AnimatorStateMachine sm)
            {
                if (sm == null || !reachable.Add(sm)) return;
                // KNOWN ADVISORY BOUND: StateMachineBehaviours are marked reachable but their serialized
                // refs are NOT followed. No standard VRC/Unity SMB holds a controller sub-asset ref, so a
                // full SerializedObject sweep would add cost for a purely theoretical case — and this rule
                // is advisory-tier, so even a false-positive orphan never flips the verdict.
                if (sm.behaviours != null) foreach (var b in sm.behaviours) if (b != null) reachable.Add(b);
                foreach (var t in sm.anyStateTransitions) if (t != null) reachable.Add(t);
                foreach (var t in sm.entryTransitions) if (t != null) reachable.Add(t);
                foreach (var cs in sm.states)
                {
                    var st = cs.state;
                    if (st == null) continue;
                    reachable.Add(st);
                    if (st.behaviours != null) foreach (var b in st.behaviours) if (b != null) reachable.Add(b);
                    foreach (var t in st.transitions) if (t != null) reachable.Add(t);
                    AddMotion(st.motion);
                }
                foreach (var child in sm.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    foreach (var t in sm.GetStateMachineTransitions(child.stateMachine)) if (t != null) reachable.Add(t);
                    AddSm(child.stateMachine);
                }
            }
            // A synced layer's per-state OVERRIDE motions/behaviours (layer.GetOverrideMotion/Behaviours)
            // are distinct sub-assets NOT reachable through the source SM's own states — mark them, or an
            // override BlendTree false-positives as an orphan. Mirrors AnimatorClipWalk's synced handling.
            void AddSyncedOverrides(AnimatorStateMachine sm, AnimatorControllerLayer layer)
            {
                if (sm == null) return;
                foreach (var cs in sm.states)
                {
                    if (cs.state == null) continue;
                    AddMotion(layer.GetOverrideMotion(cs.state));
                    var ob = layer.GetOverrideBehaviours(cs.state);
                    if (ob != null) foreach (var b in ob) if (b != null) reachable.Add(b);
                }
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null) AddSyncedOverrides(child.stateMachine, layer);
            }
            var layers = controller.layers;
            foreach (var layer in layers)
            {
                if (layer.syncedLayerIndex >= 0)
                    AddSyncedOverrides(layers[layer.syncedLayerIndex].stateMachine, layer);
                else
                    AddSm(layer.stateMachine);
            }

            // No IsSubAsset gate: LoadAllAssetsAtPath is already path-scoped to this one controller, and
            // AssetDatabase.IsSubAsset returns FALSE for HideInHierarchy sub-objects — which is what a
            // controller's own states/machines/transitions/blend-trees are. Verified live against 67
            // real-world controllers: of the orphan objects, ~97% were hidden (0 satisfied IsSubAsset), so
            // an IsSubAsset gate would have hidden the real dead weight this rule exists to name (and that
            // SweepController exists to remove). o != controller + the five-type filter is the real gate.
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || o == controller) continue;
                if (!(o is AnimatorStateMachine || o is AnimatorState || o is BlendTree
                      || o is StateMachineBehaviour || o is AnimatorTransitionBase)) continue;
                if (reachable.Contains(o)) continue;
                rep.Advisories.Add(new Offender
                {
                    Kind = "orphanSubAsset", Where = o.GetType().Name + " '" + o.name + "'",
                    Detail = "sub-asset reachable from no layer state machine (dead weight an owned cleaner would strip)"
                });
            }
        }

        // ----- Rule 7: deadLayer (advisory) ---------------------------------------------------------
        private static void RuleDeadLayer(AnimatorController controller, Report rep)
        {
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                if (layer.defaultWeight != 0f) continue;
                if (layer.syncedLayerIndex >= 0) continue;
                if (CountStates(layer.stateMachine) > 0) continue;
                if (HasAnyBehaviour(layer.stateMachine)) continue; // driver-only weight-0 layer is a valid idiom
                rep.Advisories.Add(new Offender
                {
                    Kind = "deadLayer", Where = "layer '" + layer.name + "' (index " + li + ")",
                    Detail = "defaultWeight=0 with no states and no behaviours — cannot affect output. Known limit: a cross-layer VRCAnimatorLayerControl / weight driver can revive it, so this is advisory only"
                });
            }
        }

        // ----- Rule 8/9: crossPackageRef + archiveClip (advisory) -----------------------------------
        private static void RuleCrossPackageAndArchive(AnimatorController controller, Report rep)
        {
            foreach (var clip in AnimatorClipWalk.CollectClips(controller))
            {
                if (clip == null || AssetDatabase.IsSubAsset(clip)) continue;
                string path = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path)) continue;

                if (path.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    var seg = path.Split('/');
                    string pkg = seg.Length > 1 ? seg[1] : "";
                    if (!pkg.StartsWith("com.vrchat.", StringComparison.Ordinal))
                        rep.Advisories.Add(new Offender
                        {
                            Kind = "crossPackageRef", Where = "`" + clip.name + "`",
                            Detail = "clip lives under a removable VPM package `" + pkg + "` (`" + path + "`) — breaks if that dep is removed"
                        });
                }

                if (("/" + path + "/").IndexOf("/Archive/", StringComparison.Ordinal) >= 0)
                    rep.Advisories.Add(new Offender
                    {
                        Kind = "archiveClip", Where = "`" + clip.name + "`",
                        Detail = "load-bearing clip under an Archive/ path (`" + path + "`)"
                    });
            }
        }

        // ----- Topology collection ------------------------------------------------------------------

        private static void CollectSm(AnimatorStateMachine sm, string layerName, int li, string prefix,
            List<StateCtx> states, List<SmCtx> machines)
        {
            if (sm == null) return;
            string smPath = layerName + " : " + (prefix.Length == 0 ? "(root)" : prefix.TrimEnd('/'));
            machines.Add(new SmCtx { Sm = sm, Path = smPath });
            foreach (var cs in sm.states)
                if (cs.state != null)
                    states.Add(new StateCtx { State = cs.state, Path = layerName + " : " + prefix + cs.state.name, LayerIndex = li, LayerName = layerName });
            foreach (var child in sm.stateMachines)
                if (child.stateMachine != null)
                    CollectSm(child.stateMachine, layerName, li, prefix + child.stateMachine.name + "/", states, machines);
        }

        private static int CountStates(AnimatorStateMachine sm)
        {
            if (sm == null) return 0;
            int n = sm.states.Length;
            foreach (var c in sm.stateMachines) n += CountStates(c.stateMachine);
            return n;
        }

        private static bool HasAnyBehaviour(AnimatorStateMachine sm)
        {
            if (sm == null) return false;
            if (sm.behaviours != null && sm.behaviours.Length > 0) return true;
            foreach (var cs in sm.states)
                if (cs.state != null && cs.state.behaviours != null && cs.state.behaviours.Length > 0) return true;
            foreach (var c in sm.stateMachines)
                if (HasAnyBehaviour(c.stateMachine)) return true;
            return false;
        }

        // ----- Dangling-motion GUID recovery (parse controller YAML once) ---------------------------
        private static List<string> RecoverDanglingMotionGuids(AnimatorController controller)
        {
            var result = new List<string>();
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return result;
            try
            {
                var text = File.ReadAllText(path);
                var seen = new HashSet<string>();
                foreach (Match m in Regex.Matches(text, @"m_Motion:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*\d+\}"))
                {
                    var g = m.Groups[1].Value;
                    if (seen.Add(g) && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g)))
                        result.Add(g);
                }
            }
            catch { /* binary-serialized or unreadable — no guids recoverable */ }
            return result;
        }

        // ----- Output -------------------------------------------------------------------------------

        private static string Emit(Report rep, string detection, List<string> notes)
        {
            bool errorTierFired = rep.MissingMotion > 0 || rep.UndeclaredParam > 0 || rep.EntryShadow > 0
                                  || (rep.BrokenBindingIsError && rep.BrokenBinding > 0);
            string result = errorTierFired ? "FAIL" : "PASS";

            // advisories total = advisory offenders (rules 5-9 + demoted broken bindings, which are
            // already placed in the Advisories list when brokenBinding is demoted).
            int advisories = rep.Advisories.Count;

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[AnimatorLint] {0}: missingMotion={1} undeclaredParam={2} entryShadow={3} brokenBinding={4} advisories={5} => {6}",
                rep.Controller.name, rep.MissingMotion, rep.UndeclaredParam, rep.EntryShadow, rep.BrokenBinding, advisories, result);

            var sb = new StringBuilder();
            sb.Append("# AnimatorLint: ").Append(rep.Controller.name).Append('\n');
            string assetPath = AssetDatabase.GetAssetPath(rep.Controller);
            sb.Append("asset: `").Append(string.IsNullOrEmpty(assetPath) ? "(unsaved)" : assetPath).Append("`  \n");
            sb.Append(detection).Append("  \n");
            foreach (var n in notes) sb.Append("> note: ").Append(n).Append("  \n");
            sb.Append('\n').Append(summary.Substring("[AnimatorLint] ".Length)).Append('\n');

            sb.Append("\n## Errors\n\n");
            if (rep.Errors.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.Errors) AppendOffender(sb, o);

            sb.Append("\n## Advisories\n\n");
            if (rep.Advisories.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.Advisories) AppendOffender(sb, o);

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "animatorlint_" + rep.Controller.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogError(res);
            return res;
        }

        private static void AppendOffender(StringBuilder sb, Offender o) =>
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
            string err = "[AnimatorLint] FAIL: " + why;
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

        private static bool Is2D(BlendTreeType t) =>
            t == BlendTreeType.SimpleDirectional2D || t == BlendTreeType.FreeformDirectional2D || t == BlendTreeType.FreeformCartesian2D;

        // ----- Types --------------------------------------------------------------------------------

        private struct StateCtx { public AnimatorState State; public string Path; public int LayerIndex; public string LayerName; }
        private struct SmCtx { public AnimatorStateMachine Sm; public string Path; }
        private struct Offender { public string Kind; public string Where; public string Detail; }

        private class Report
        {
            public AnimatorController Controller;
            public int MissingMotion, UndeclaredParam, EntryShadow, BrokenBinding;
            public bool BrokenBindingIsError;
            public readonly List<Offender> Errors = new List<Offender>();
            public readonly List<Offender> Advisories = new List<Offender>();
        }
    }
}
