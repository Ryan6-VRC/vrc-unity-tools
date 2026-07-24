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
    /// The CheckAnimator rule set, extracted from <see cref="CheckAnimator"/> so the SAME rules can run on an
    /// in-memory <see cref="AnimatorController"/> (a future compiler's write-substrate door) as on a saved
    /// asset (CheckAnimator's inspection door). <see cref="Run"/> takes the already-resolved binding-basis
    /// <paramref name="roots"/> / <paramref name="brokenBindingIsError"/> tier flag / <paramref name="pathRewrite"/>
    /// and returns the raw <see cref="LintResult"/> — counts, error/advisory offenders, and rule-produced
    /// notes. CheckAnimator keeps ownership of basis resolution (auto/explicit) and of rendering the result
    /// (summary line + RunLog markdown); ControllerRules owns only the defect detection.
    ///
    /// The two-tier discipline lives here: the schema-certain rules (missing motion, undeclared parameter,
    /// non-float blend parameter, deterministic entry shadow, a dead transition, and — when
    /// <paramref name="brokenBindingIsError"/> — a null-resolving clip binding) populate
    /// <see cref="LintResult.Errors"/>; every heuristic populates <see cref="LintResult.Advisories"/>.
    /// The verdict (error-tier fired ⇒ FAIL) is computed by the caller from these counts.
    ///
    /// The shared binding/frame helpers (<c>CollectUnresolvedBindings</c>, the <c>Try*Frame</c> detectors)
    /// stay on <see cref="CheckAnimator"/> — they are also CheckAvatar's; the broken-binding rule calls them.
    /// </summary>
    public static class ControllerRules
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

        private static readonly HashSet<string> VrcReservedSet = new HashSet<string>(VrcReservedParams);

        /// <summary>True when <paramref name="name"/> is a VRChat reserved/built-in animator parameter
        /// (IsLocal, Viseme, GestureLeft, …) — declared nowhere yet referenced everywhere, so avatar tooling
        /// must exempt it. The one authoritative predicate over <see cref="VrcReservedParams"/>: the undeclared-param
        /// rule uses it, and the compiler's VRCExpressionParameters emitter reuses it to keep built-ins out of the
        /// emitted params asset (they cost nothing and aren't the controller's own to declare).</summary>
        public static bool IsVrcReserved(string name) => name != null && VrcReservedSet.Contains(name);

        // ----- Public API ---------------------------------------------------------------------------

        /// <summary>Run the v1 rule set against <paramref name="controller"/> with the binding basis already
        /// resolved by the caller. <paramref name="roots"/> are the candidate basis roots (mount-first); an
        /// EMPTY list skips the broken-binding rule (records a note, counts nothing) exactly as a
        /// no-basis-root CheckAnimator run does. <paramref name="brokenBindingIsError"/> places broken bindings
        /// at error-tier (true) or demotes them to a single collapsed advisory (false — a build-rewrite auto
        /// site). <paramref name="pathRewrite"/> (null ⇒ identity) rewrites each binding path before
        /// resolution (a VRCFury FullController's rewriteBindings). Returns the raw counts + offenders +
        /// rule notes; the caller computes the verdict and renders the report.</summary>
        public static LintResult Run(AnimatorController controller, List<GameObject> roots,
                                     bool brokenBindingIsError, Func<string, string> pathRewrite)
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
            RuleEntryShadow(machines, rep);
            RuleDeadTransition(states, rep);
            RuleBrokenBinding(controller, roots, pathRewrite, rep);

            // ---- Advisory-tier rules ------------------------------------------------------------------
            RuleWdInconsistency(states, rep);
            RuleOrphanSubAsset(controller, rep);
            RuleDeadLayer(controller, rep);
            RuleCrossPackageAndArchive(controller, rep);

            return rep;
        }

        // ----- Rule 1: missingMotion (error) --------------------------------------------------------
        // A state whose motion reference is present but the asset is gone. NEVER fires on a clean-empty
        // motion (instanceID 0) — that is a standard idiom. The dangling GUID is the only surviving handle.
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
        // A blend tree evaluates only Float parameters: a name-matched Int/Bool never errors at runtime —
        // the tree reads 0 and sits on its zero branch forever (measured on a live avatar). Declared
        // params only: an undeclared name is Rule 2's offender, and a VRC reserved built-in carries no
        // declared type on this controller to assert against.
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
                                 " — a blend tree evaluates only Float parameters, so this reads 0 and the tree never leaves its zero branch; declare it Float (or blend on a Float copy)"
                    });
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

        // ----- Rule 3b: deadTransition — a state transition that provably can never fire ------------
        // ONE precise shape (conservative by design — anything outside it is left alone): an outgoing
        // state→state transition with NO conditions AND hasExitTime==false that is NOT a to-Exit transition.
        // Unity needs a condition or an exit time to ever activate such a transition, so it is inert and
        // silently stalls the machine. Surveyed across 144 real controllers (a live personal-avatar corpus): ZERO hits,
        // so error-tier carries no false-positive risk — the pattern simply does not occur in working avatars.
        //
        // NOT flagged: an exit-time transition from a MOTIONLESS (motion==null) state. An earlier draft
        // flagged these on the theory that a motionless state's normalizedTime never advances so the exit
        // gate can't be met — that theory is FALSE. An empty state has a default 1s length and its clock
        // advances in real time; the transition fires normally. Proven two ways: manual Animator.Update on an
        // empty state transitions on schedule, and shipped VRCFury Action-layer timer states (BlendOut→Restore
        // Tracking, Idle→Refresh) rely on exactly this idiom on real avatars. Flagging it produced 16 false
        // positives in the same 144-controller survey (9 of them also had conditions that fire the transition).
        // Do not re-add that case. to-Exit transitions are excluded (they fire by their own rules).
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
            Func<string, string> pathRewrite, LintResult rep)
        {
            if (roots.Count == 0)
            {
                rep.Notes.Add("broken-binding rule skipped: no basis root available to resolve clip bindings against.");
                return;
            }
            // Demoted (build-rewrite auto) bindings are collapsed into ONE advisory with a small sample:
            // under MA/VRCFury each unresolved binding is expected pre-build (paths get rewritten), so
            // hundreds of identical per-line advisories would only drown the digest. On the error-tier
            // path each broken binding is a genuine named failure and gets its own line.
            var demotedSamples = new List<string>();

            foreach (var (clip, b) in CheckAnimator.CollectUnresolvedBindings(controller, roots, pathRewrite))
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
            // an IsSubAsset gate would have hidden the real dead weight this rule exists to name (and that a
            // Decompile→Compile round-trip strips). o != controller + the five-type filter is the real gate.
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

    /// <summary>The raw outcome of <see cref="ControllerRules.Run"/>: per-rule counts, the error/advisory
    /// offender lists, and any rule-produced notes (a skipped-rule caveat). The caller derives the verdict
    /// (error-tier fired ⇒ FAIL) and renders the report. <see cref="BrokenBindingIsError"/> records the tier
    /// the broken-binding rule ran at, so the caller can fold it into the verdict.</summary>
    public sealed class LintResult
    {
        public int MissingMotion, UndeclaredParam, NonFloatBlendParam, EntryShadow, BrokenBinding, DeadTransition;
        public bool BrokenBindingIsError;
        public readonly List<LintOffender> Errors = new List<LintOffender>();
        public readonly List<LintOffender> Advisories = new List<LintOffender>();
        public readonly List<string> Notes = new List<string>(); // rule-produced caveats, in rule order
    }
}
