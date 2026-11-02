using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The CheckAnimator rule set, shared by the saved-asset door (<see cref="CheckAnimator"/>) and the in-memory
    /// controller door (<c>CompileController</c>). The caller resolves the binding basis, derives the verdict and
    /// renders the result; this class owns only defect detection. Schema-certain rules populate
    /// <see cref="LintResult.Errors"/> and every heuristic <see cref="LintResult.Advisories"/>; the tiers are named in
    /// docs/animator.md §Reading a controller.
    /// </summary>
    public static class ControllerRules
    {
        // VRChat reserved/built-in animator parameters, tracking the avatar-parameters docs. `IsAnimatorEnabled`
        // and `PreviewMode` are documented thinly and are easy to mistake for author-declared names; both are
        // built-ins, in VRCFury's VRChatGlobalParams (FullControllerBuilder.cs) and av3emulator's
        // BUILTIN_PARAMETERS (LyumaAv3Runtime.cs).
        private static readonly string[] VrcReservedParams =
        {
            "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight",
            "GestureRightWeight", "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude",
            "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation",
            "Earmuffs", "IsOnFriendsList", "AvatarVersion", "IsAnimatorEnabled", "PreviewMode",
            "ScaleModified", "ScaleFactor",
            "ScaleFactorInverse", "EyeHeightAsMeters", "EyeHeightAsPercent",
        };

        private static readonly HashSet<string> VrcReservedSet = new HashSet<string>(VrcReservedParams);

        /// <summary>The one predicate over <see cref="VrcReservedParams"/>. The compiler's VRCExpressionParameters
        /// emitter also uses it, to keep built-ins out of the emitted params asset.</summary>
        public static bool IsVrcReserved(string name) => name != null && VrcReservedSet.Contains(name);

        // ----- Public API ---------------------------------------------------------------------------

        /// <summary><paramref name="roots"/> are the candidate basis roots, mount first; an EMPTY list skips the
        /// broken-binding rule and records a note. <paramref name="brokenBindingIsError"/> false demotes broken bindings
        /// to one collapsed advisory (a build-rewrite auto site). <paramref name="absoluteRoot"/>, null unless the frame
        /// is a VRCFury FullController, is the avatar root a leading-`/` binding path resolves from.</summary>
        public static LintResult Run(AnimatorController controller, List<GameObject> roots,
                                     bool brokenBindingIsError, Func<string, string> pathRewrite,
                                     GameObject absoluteRoot = null)
        {
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

            var rep = new LintResult { BrokenBindingIsError = brokenBindingIsError };

            // ---- Error-tier rules ---------------------------------------------------------------------
            var dangling = RecoverDanglingMotionGuids(controller);
            RuleMissingMotion(states, dangling, rep);
            RuleUndeclaredParam(controller, states, machines, rep);
            RuleNonFloatBlendParam(controller, states, rep);
            var curveWritten = ParamCurveWrites(controller);
            RuleNonFloatParamCurve(controller, curveWritten, rep);
            RuleDriverOnAnimatedParam(controller, states, machines, curveWritten, rep);
            RuleEntryShadow(machines, rep);
            RuleDeadTransition(states, rep);
            RuleBrokenBinding(controller, roots, pathRewrite, absoluteRoot, rep);

            // ---- Advisory-tier rules ------------------------------------------------------------------
            RuleWdInconsistency(states, rep);
            RuleDeadSelfRung(states, rep);
            RuleOrphanSubAsset(controller, rep);
            RuleDeadLayer(controller, rep);
            RuleCrossPackageAndArchive(controller, rep);

            return rep;
        }

        // ----- Rule 1: missingMotion (error) --------------------------------------------------------
        // A state whose motion reference is present but the asset is gone. NEVER fires on a clean-empty
        // motion (instanceID 0), a standard idiom.
        private static void RuleMissingMotion(List<StateCtx> states, List<string> dangling, LintResult rep)
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
                rep.Errors.Add(new LintOffender { Kind = "missingMotion", Where = s.Path, Detail = "dangling motion reference — " + guid });
            }
        }

        // ----- Rule 2: undeclaredParam (error) ------------------------------------------------------
        private static void RuleUndeclaredParam(AnimatorController controller, List<StateCtx> states,
            List<SmCtx> machines, LintResult rep)
        {
            var declared = new HashSet<string>();
            foreach (var p in controller.parameters) declared.Add(p.name);

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
                foreach (var child in m.Sm.stateMachines)
                    if (child.stateMachine != null)
                        foreach (var t in m.Sm.GetStateMachineTransitions(child.stateMachine))
                            Conds(t.conditions, m.Path + " → " + child.stateMachine.name);
                if (m.Sm.behaviours != null)
                    foreach (var b in m.Sm.behaviours)
                    {
                        DriverParams(b, m.Path + " (SM behaviour)", Ref);
                        FaceTrackingSetParams(b, m.Path + " (SM behaviour)", Ref);
                    }
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
                if (st.behaviours != null)
                    foreach (var b in st.behaviours)
                    {
                        DriverParams(b, s.Path + " (driver)", Ref);
                        FaceTrackingSetParams(b, s.Path + " (faceTrackingSets)", Ref);
                    }
            }

            foreach (var kv in referenced)
            {
                if (declared.Contains(kv.Key) || IsVrcReserved(kv.Key)) continue;
                rep.UndeclaredParam++;
                rep.Errors.Add(new LintOffender
                {
                    Kind = "undeclaredParam", Where = kv.Value,
                    Detail = "parameter `" + kv.Key + "` referenced but not declared on the controller (exempt set tracks the VRC SDK reserved list)"
                });
            }
        }

        // ----- Rule 2b: nonFloatBlendParam (error, deterministic) -----------------------------------
        // A blend tree evaluates only Float parameters: a name-matched Int/Bool never errors at runtime, the
        // tree reads 0 and sits on its zero branch forever (measured). Declared params only: an undeclared name
        // is Rule 2's offender.
        private static void RuleNonFloatBlendParam(AnimatorController controller, List<StateCtx> states, LintResult rep)
        {
            var types = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in controller.parameters) if (!types.ContainsKey(p.name)) types[p.name] = p.type;

            var reported = new HashSet<string>(); // first referencing site per parameter, like Rule 2
            foreach (var s in states)
            {
                if (s.State == null) continue;
                BlendParams(s.State.motion, s.Path, (name, where) =>
                {
                    if (string.IsNullOrEmpty(name) || reported.Contains(name)) return;
                    if (!types.TryGetValue(name, out var t) || t == AnimatorControllerParameterType.Float) return;
                    reported.Add(name);
                    rep.NonFloatBlendParam++;
                    rep.Errors.Add(new LintOffender
                    {
                        Kind = "nonFloatBlendParam", Where = where,
                        Detail = "blend parameter `" + name + "` is declared " + t +
                                 " — a blend tree evaluates only Float parameters, so this input reads 0 and cannot respond to the parameter; declare it Float (or blend on a Float copy)"
                    });
                });
            }
        }

        // ----- Rule 3b: nonFloatParamCurve (error) --------------------------------------------------
        // An animator-parameter curve whose parameter is declared Bool or Int. The curve emits correctly and
        // is inert (measured, stock Unity), and it still BINDS the parameter, locking out every writer outside
        // the animation system (runtime.md §Animator evaluation).
        private static void RuleNonFloatParamCurve(AnimatorController controller,
            Dictionary<string, string> curveWritten, LintResult rep)
        {
            if (curveWritten.Count == 0) return;
            var types = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in controller.parameters) if (!types.ContainsKey(p.name)) types[p.name] = p.type;

            foreach (var kv in curveWritten)
            {
                if (!types.TryGetValue(kv.Key, out var t)) continue;
                // Bool and Int are the measured cases. Trigger falls in here too, unmeasured: a curve on a
                // Trigger is no working idiom either way.
                if (t == AnimatorControllerParameterType.Float) continue;

                rep.NonFloatParamCurve++;
                rep.Errors.Add(new LintOffender
                {
                    Kind = "nonFloatParamCurve",
                    Where = "clip `" + kv.Value + "`",
                    Detail = "animates parameter `" + kv.Key + "`, which is declared " + t +
                             " — a parameter curve writes only Float parameters, so this curve sets nothing. " +
                             "It still binds the parameter, which hands it to the animation system and locks " +
                             "out every writer outside it: driver, expression menu, contact, script " +
                             "(runtime.md). Drive this parameter from a parameter driver and let no clip " +
                             "animate it; declaring it Float instead leaves it bound"
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
            => DriverOps(b, where, (name, w, isSource) => reff(name, isSource ? w + " (source)" : w));

        /// <summary>Every parameter a driver behaviour touches, with the direction: <c>isSource</c> is true for
        /// a <c>Copy</c>'s READ side and false for the op's write destination (set/add/copy/random all key their
        /// destination the same way).</summary>
        private static void DriverOps(StateMachineBehaviour b, string where, Action<string, string, bool> visit)
        {
            if (!(b is VRC.SDKBase.VRC_AvatarParameterDriver drv) || drv.parameters == null) return;
            foreach (var p in drv.parameters)
            {
                visit(p.name, where, false);
                if (p.type == VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy) visit(p.source, where, true);
            }
        }

        /// <summary>Every parameter a face-tracking Blendshapes Set behaviour reads: the sourceParameter of each
        /// Parameter-type entry. It deliberately does NOT feed driverOnAnimatedParam: whether this behaviour's
        /// state-entry read of a clip-bound parameter sees the animated value or the declared default has not been
        /// measured.</summary>
        private static void FaceTrackingSetParams(StateMachineBehaviour b, string where, Action<string, string> reff)
        {
#if VRC_FT_SETS_PRESENT
            if (!(b is VRC.SDKBase.VRC_AnimatorFaceTrackingBlendshapesSet ft) || ft.blendSets == null) return;
            foreach (var e in ft.blendSets)
                if (e != null && e.type == VRC.SDKBase.VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter)
                    reff(e.sourceParameter, where + " (source)");
#endif
        }

        // ----- Rule 3c: driverOnAnimatedParam (error) -----------------------------------------------
        // A driver op, either direction, on a parameter some clip param-curve-writes. Measured: a driver READ
        // yields the parameter's declared default rather than the clip's value, and a driver WRITE never reaches
        // any animator reader (runtime.md §Animator evaluation).
        private static void RuleDriverOnAnimatedParam(AnimatorController controller, List<StateCtx> states,
            List<SmCtx> machines, Dictionary<string, string> curveWritten, LintResult rep)
        {
            if (curveWritten.Count == 0) return;

            // One offender per (parameter, direction), the first site as the handle, as in Rule 2.
            var reported = new HashSet<string>();
            Action<string, string, bool> visit = (name, where, isSource) =>
            {
                if (string.IsNullOrEmpty(name)) return;
                if (!curveWritten.TryGetValue(name, out var clip)) return;
                if (!reported.Add((isSource ? "r:" : "w:") + name)) return;

                rep.DriverOnAnimatedParam++;
                rep.Errors.Add(new LintOffender
                {
                    Kind = "driverOnAnimatedParam", Where = where,
                    Detail = "driver " + (isSource ? "reads" : "writes") + " parameter `" + name +
                             "`, which clip `" + clip + "` animates as a parameter curve — the animation system " +
                             "owns a clip-bound parameter (runtime.md), so " + (isSource
                                 ? "this read yields the parameter's declared default, never what the clip is " +
                                   "producing. To read that animated value, use a blend tree or a transition " +
                                   "condition"
                                 : "no animator reader observes this write. If the intended consumer is outside " +
                                   "the animator (an OSC-out signal, a saved value) the write is misdirected " +
                                   "rather than pointless") +
                             ". Give the driver a parameter no clip in this controller animates"
                });
            };

            foreach (var m in machines)
                if (m.Sm.behaviours != null)
                    foreach (var b in m.Sm.behaviours) DriverOps(b, m.Path + " (SM behaviour)", visit);
            foreach (var s in states)
                if (s.State != null && s.State.behaviours != null)
                    foreach (var b in s.State.behaviours) DriverOps(b, s.Path + " (driver)", visit);

            // Run skips synced layers, but a synced layer carries its OWN per-state behaviour overrides, and a
            // driver installed there is as dead as any other.
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                if (layer.syncedLayerIndex < 0 || layer.syncedLayerIndex >= layers.Length) continue;
                var src = layers[layer.syncedLayerIndex].stateMachine;
                if (src == null) continue;
                WalkSyncedOverrideDrivers(src, layer, layer.name ?? ("layer " + li), visit);
            }
        }

        private static void WalkSyncedOverrideDrivers(AnimatorStateMachine sm, AnimatorControllerLayer layer,
            string layerName, Action<string, string, bool> visit)
        {
            if (sm == null) return;
            foreach (var cs in sm.states)
            {
                if (cs.state == null) continue;
                var ov = layer.GetOverrideBehaviours(cs.state);
                if (ov == null) continue;
                foreach (var b in ov)
                    DriverOps(b, "synced layer '" + layerName + "' state '" + cs.state.name + "' (override driver)", visit);
            }
            foreach (var child in sm.stateMachines)
                if (child.stateMachine != null) WalkSyncedOverrideDrivers(child.stateMachine, layer, layerName, visit);
        }

        /// <summary>Every DECLARED parameter some reachable clip writes as an animator parameter curve, mapped to
        /// the first clip that does. Reads its clips from <see cref="AnimatorClipWalk.CollectClips"/> because
        /// <see cref="Run"/> skips synced layers, whose per-state override motions bind real parameters.
        /// Humanoid muscle and TDOF curves share the path-less <c>Animator</c> binding shape but name no parameter,
        /// so they fall out with no muscle allowlist to maintain.</summary>
        private static Dictionary<string, string> ParamCurveWrites(AnimatorController controller)
        {
            var written = new Dictionary<string, string>();
            var declared = new HashSet<string>();
            foreach (var p in controller.parameters) declared.Add(p.name);
            if (declared.Count == 0) return written;

            foreach (var clip in AnimatorClipWalk.CollectClips(controller))
            {
                if (clip == null) continue;
                foreach (var b in AnimationUtility.GetCurveBindings(clip))
                {
                    if (b.type != typeof(Animator) || !string.IsNullOrEmpty(b.path)) continue;
                    if (!declared.Contains(b.propertyName)) continue;
                    if (!written.ContainsKey(b.propertyName)) written[b.propertyName] = clip.name;
                }
            }
            return written;
        }

        /// <summary>Which parameters a controller READS and which it WRITES, each mapped to every site that
        /// does it: a census, not a classification, so a latch appears in both. The clip-curve half covers DECLARED
        /// parameters only (<see cref="ParamCurveWrites"/>); a caller reporting provenance should say so rather than
        /// imply the census is exhaustive.</summary>
        internal static ParamUsage CollectParamUsage(AnimatorController controller)
        {
            var usage = new ParamUsage
            {
                Reads = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Writes = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            };
            if (controller == null) return usage;

            void At(Dictionary<string, List<string>> into, string name, string where)
            {
                if (string.IsNullOrEmpty(name)) return;
                if (!into.TryGetValue(name, out var l)) into[name] = l = new List<string>();
                if (!l.Contains(where)) l.Add(where);
            }
            void Read(string name, string where) => At(usage.Reads, name, where);
            void Write(string name, string where) => At(usage.Writes, name, where);
            void Conds(AnimatorCondition[] conds, string where)
            {
                if (conds == null) return;
                foreach (var cd in conds) Read(cd.parameter, where);
            }

            var states = new List<StateCtx>();
            var machines = new List<SmCtx>();
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                if (layer.syncedLayerIndex >= 0) continue; // handled by the synced-override walk below
                if (layer.stateMachine == null) continue;
                CollectSm(layer.stateMachine, layer.name, li, "", states, machines);
            }

            foreach (var m in machines)
            {
                foreach (var t in m.Sm.anyStateTransitions) Conds(t.conditions, m.Path + " AnyState");
                foreach (var t in m.Sm.entryTransitions) Conds(t.conditions, m.Path + " Entry");
                foreach (var child in m.Sm.stateMachines)
                    if (child.stateMachine != null)
                        foreach (var t in m.Sm.GetStateMachineTransitions(child.stateMachine))
                            Conds(t.conditions, m.Path + " → " + child.stateMachine.name);
                if (m.Sm.behaviours != null)
                    foreach (var b in m.Sm.behaviours)
                    {
                        DriverOps(b, m.Path + " (SM driver)", (n, w, isSource) =>
                        { if (isSource) Read(n, w); else Write(n, w); });
                        FaceTrackingSetParams(b, m.Path + " (SM faceTrackingSets)", Read);
                    }
            }
            foreach (var s in states)
            {
                var st = s.State;
                foreach (var t in st.transitions) Conds(t.conditions, s.Path);
                if (st.speedParameterActive) Read(st.speedParameter, s.Path + " speedParameter");
                if (st.timeParameterActive) Read(st.timeParameter, s.Path + " motionTime");
                if (st.mirrorParameterActive) Read(st.mirrorParameter, s.Path + " mirrorParameter");
                if (st.cycleOffsetParameterActive) Read(st.cycleOffsetParameter, s.Path + " cycleOffset");
                BlendParams(st.motion, s.Path, Read);
                if (st.behaviours != null)
                    foreach (var b in st.behaviours)
                    {
                        DriverOps(b, s.Path + " (driver)", (n, w, isSource) =>
                        { if (isSource) Read(n, w); else Write(n, w); });
                        FaceTrackingSetParams(b, s.Path + " (faceTrackingSets)", Read);
                    }
            }

            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                if (layer.syncedLayerIndex < 0 || layer.syncedLayerIndex >= layers.Length) continue;
                var src = layers[layer.syncedLayerIndex].stateMachine;
                if (src == null) continue;
                WalkSyncedOverrideDrivers(src, layer, layer.name ?? ("layer " + li), (n, w, isSource) =>
                { if (isSource) Read(n, w); else Write(n, w); });
            }

            foreach (var kv in ParamCurveWrites(controller))
                Write(kv.Key, "clip `" + kv.Value + "` (parameter curve)");

            return usage;
        }

        internal struct ParamUsage
        {
            public Dictionary<string, List<string>> Reads;
            public Dictionary<string, List<string>> Writes;
        }

        // ----- Rule 3: entryShadow (error, deterministic) -------------------------------------------
        // An earlier UNCONDITIONAL entry transition makes every later entry transition unreachable.
        private static void RuleEntryShadow(List<SmCtx> machines, LintResult rep)
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
                rep.Errors.Add(new LintOffender
                {
                    Kind = "entryShadow", Where = m.Path,
                    Detail = shadowed + " entry transition(s) after the unconditional entry at index " + firstUncond + " are unreachable"
                });
            }
        }

        // ----- Rule 3d: deadTransition — a state transition that provably can never fire ------------
        // ONE precise shape: an outgoing state→state transition with NO conditions AND hasExitTime==false that
        // is NOT a to-Exit transition. Unity needs a condition or an exit time to ever activate such a
        // transition, so it is inert and silently stalls the machine.
        //
        // Do not flag an exit-time transition from a MOTIONLESS (motion==null) state: an empty state has a
        // default 1s length and its clock advances in real time, so the transition fires on schedule (measured
        // with manual Animator.Update), and VRCFury's tracking-control timer relies on it
        // (TrackingConflictResolverService.cs, Idle→Refresh).
        private static void RuleDeadTransition(List<StateCtx> states, LintResult rep)
        {
            foreach (var s in states)
            {
                var st = s.State;
                if (st == null) continue;
                foreach (var t in st.transitions)
                {
                    if (t == null || t.isExit) continue;
                    bool noConds = t.conditions == null || t.conditions.Length == 0;
                    if (noConds && !t.hasExitTime)
                    {
                        rep.DeadTransition++;
                        rep.Errors.Add(new LintOffender
                        {
                            Kind = "deadTransition",
                            Where = "state '" + st.name + "' -> '" + TransitionDest(t) + "'",
                            Detail = "no conditions and no exit time — never fires"
                        });
                    }
                }
            }
        }

        private static string TransitionDest(AnimatorStateTransition t)
        {
            if (t.destinationStateMachine != null) return t.destinationStateMachine.name;
            if (t.destinationState != null) return t.destinationState.name;
            return "(none)";
        }

        // ----- Rule 4: brokenBinding (error, or advisory under a build-rewrite auto site) -----------
        private static void RuleBrokenBinding(AnimatorController controller, List<GameObject> roots,
            Func<string, string> pathRewrite, GameObject absoluteRoot, LintResult rep)
        {
            if (roots.Count == 0)
            {
                rep.Notes.Add("broken-binding rule skipped: no basis root available to resolve clip bindings against.");
                return;
            }
            var demotedSamples = new List<string>();

            foreach (var (clip, b) in CheckAnimator.CollectUnresolvedBindings(controller, roots, pathRewrite, absoluteRoot))
            {
                rep.BrokenBinding++;
                if (rep.BrokenBindingIsError)
                    rep.Errors.Add(new LintOffender
                    {
                        Kind = "brokenBinding", Where = clip.name,
                        Detail = "binding path='" + b.path + "' type=" + b.type.Name + " prop='" + b.propertyName + "' resolves to no object"
                    });
                else if (demotedSamples.Count < 3)
                    demotedSamples.Add(clip.name + ":" + b.path);
            }

            if (!rep.BrokenBindingIsError && rep.BrokenBinding > 0)
                rep.Advisories.Add(new LintOffender
                {
                    Kind = "brokenBinding (demoted)", Where = rep.BrokenBinding + " binding(s)",
                    Detail = "unresolvable in the authored scene; MA/VRCFury rewrite binding paths at build so this cannot be verified pre-build"
                            + (demotedSamples.Count > 0 ? " — e.g. " + string.Join(", ", demotedSamples) : "")
                });
        }

        // ----- Rule 5: wdInconsistency (advisory) — WITHIN one layer only ---------------------------
        private static void RuleWdInconsistency(List<StateCtx> states, LintResult rep)
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
                    rep.Advisories.Add(new LintOffender
                    {
                        Kind = "wdInconsistency", Where = "layer '" + layerName[kv.Key] + "'",
                        Detail = "states disagree on Write Defaults (on=" + kv.Value[0] + " off=" + kv.Value[1] + ")"
                    });
            }
        }

        // ----- Rule 5b: deadSelfRung (advisory) — a state's rung to itself with canTransitionToSelf off ----
        // The flag decides only a rung whose destination is its own state (a return through Exit or a
        // sub-machine re-enters either way); off, that rung never fires, and a later sibling whose condition
        // holds still fires past it. Advisory, not error: a vendor rung like this is inert unless soloed (the
        // Detail says so then). In a GUI Editor, AddTransition on an asset-backed state defaults the flag off.
        // A muted rung is skipped: it never fires whatever the flag.
        private static void RuleDeadSelfRung(List<StateCtx> states, LintResult rep)
        {
            foreach (var s in states)
            {
                var st = s.State;
                if (st == null) continue;
                foreach (var t in st.transitions)
                {
                    if (t == null || t.isExit || t.mute || t.destinationState != st || t.canTransitionToSelf) continue;
                    rep.Advisories.Add(new LintOffender
                    {
                        Kind = "deadSelfRung",
                        Where = "layer '" + s.LayerName + "' state '" + st.name + "' -> itself",
                        Detail = "canTransitionToSelf is off, so this rung never fires (measured on a bare Animator "
                               + "and in av3emu) and the state's drivers never re-run through it"
                               + (t.solo ? "; it is soloed, so it also silences every non-solo rung out of this state" : "")
                               + "; if it was meant to re-enter, recompile from source or set the flag"
                    });
                }
            }
        }

        // ----- Rule 6: orphanSubAsset (advisory) — complete reachability walk -----------------------
        private static void RuleOrphanSubAsset(AnimatorController controller, LintResult rep)
        {
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) { rep.Notes.Add("orphan-sub-asset rule skipped: controller is not a saved asset."); return; }

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
                // full SerializedObject sweep would add cost for a purely theoretical case.
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
            // override BlendTree false-positives as an orphan.
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

            // No IsSubAsset gate: AssetDatabase.IsSubAsset returns FALSE for HideInHierarchy sub-objects, which is
            // what a controller's own states/machines/transitions/blend-trees are, so it would hide the dead weight
            // this rule exists to name. LoadAllAssetsAtPath is already path-scoped to this one controller.
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o == null || o == controller) continue;
                if (!(o is AnimatorStateMachine || o is AnimatorState || o is BlendTree
                      || o is StateMachineBehaviour || o is AnimatorTransitionBase)) continue;
                if (reachable.Contains(o)) continue;
                rep.Advisories.Add(new LintOffender
                {
                    Kind = "orphanSubAsset", Where = o.GetType().Name + " '" + o.name + "'",
                    Detail = "sub-asset reachable from no layer state machine (dead weight an owned cleaner would strip)"
                });
            }
        }

        // ----- Rule 7: deadLayer (advisory) ---------------------------------------------------------
        private static void RuleDeadLayer(AnimatorController controller, LintResult rep)
        {
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                if (layer.defaultWeight != 0f) continue;
                if (layer.syncedLayerIndex >= 0) continue;
                if (CountStates(layer.stateMachine) > 0) continue;
                if (HasAnyBehaviour(layer.stateMachine)) continue; // driver-only weight-0 layer is a valid idiom
                rep.Advisories.Add(new LintOffender
                {
                    Kind = "deadLayer", Where = "layer '" + layer.name + "' (index " + li + ")",
                    Detail = "defaultWeight=0 with no states and no behaviours — cannot affect output. Known limit: a cross-layer VRCAnimatorLayerControl / weight driver can revive it, so this is advisory only"
                });
            }
        }

        // ----- Rule 8/9: crossPackageRef + archiveClip (advisory) -----------------------------------
        private static void RuleCrossPackageAndArchive(AnimatorController controller, LintResult rep)
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
                        rep.Advisories.Add(new LintOffender
                        {
                            Kind = "crossPackageRef", Where = "`" + clip.name + "`",
                            Detail = "clip lives under a removable VPM package `" + pkg + "` (`" + path + "`) — breaks if that dep is removed"
                        });
                }

                if (("/" + path + "/").IndexOf("/Archive/", StringComparison.Ordinal) >= 0)
                    rep.Advisories.Add(new LintOffender
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
                // fileID is signed: an FBX-embedded clip's localID is often NEGATIVE (referenced type: 3).
                // Matching only unsigned drops those, leaving a real broken motion unnamed in the offender.
                foreach (Match m in Regex.Matches(text, @"m_Motion:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*\d+\}"))
                {
                    var g = m.Groups[1].Value;
                    if (seen.Add(g) && !RunLogFormat.AssetGuidResolves(g))
                        result.Add(g);
                }
            }
            catch { /* binary-serialized or unreadable — no guids recoverable */ }
            return result;
        }

        // ----- Helpers ------------------------------------------------------------------------------

        private static bool Is2D(BlendTreeType t) =>
            t == BlendTreeType.SimpleDirectional2D || t == BlendTreeType.FreeformDirectional2D || t == BlendTreeType.FreeformCartesian2D;

        // ----- Types --------------------------------------------------------------------------------

        private struct StateCtx { public AnimatorState State; public string Path; public int LayerIndex; public string LayerName; }
        private struct SmCtx { public AnimatorStateMachine Sm; public string Path; }
    }

    // ----- Public result types (the compiler door reads these) --------------------------------------

    /// <summary>One detected defect. <see cref="Kind"/> is the rule name, <see cref="Where"/> a topology/asset
    /// handle, <see cref="Detail"/> the human-readable explanation. Rendered verbatim by CheckAnimator.Emit.</summary>
    public struct LintOffender { public string Kind; public string Where; public string Detail; }

    /// <summary>The raw outcome of <see cref="ControllerRules.Run"/>. <see cref="BrokenBindingIsError"/> records the
    /// tier the broken-binding rule ran at, so the caller can fold it into the verdict.</summary>
    public sealed class LintResult
    {
        public int MissingMotion, UndeclaredParam, NonFloatBlendParam, NonFloatParamCurve, DriverOnAnimatedParam, EntryShadow, BrokenBinding, DeadTransition;
        public bool BrokenBindingIsError;
        public readonly List<LintOffender> Errors = new List<LintOffender>();
        public readonly List<LintOffender> Advisories = new List<LintOffender>();
        public readonly List<string> Notes = new List<string>(); // rule-produced caveats, in rule order
    }
}
