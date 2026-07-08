using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDKBase;
using Driver = VRC.SDKBase.VRC_AvatarParameterDriver;
using TrackingType = VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType;
using PlayableLayer = VRC.SDKBase.VRC_PlayableLayerControl.BlendableLayer;
using LayerCtrlLayer = VRC.SDKBase.VRC_AnimatorLayerControl.BlendableLayer;
using AudioOrder = VRC.SDKBase.VRC_AnimatorPlayAudio.Order;
using AudioApply = VRC.SDKBase.VRC_AnimatorPlayAudio.ApplySettings;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The READ direction, inverse of <see cref="ControllerEmit"/>: a reachability walk that turns an
    /// <see cref="AnimatorController"/> back into an <see cref="AnimDocument"/>. Re-emitting the produced
    /// document reproduces the controller — <see cref="ControllerEmit"/> is the authoritative spec, and every
    /// decoder here inverts one of its emit paths (parameters, clips, layers/states/transitions, motions +
    /// blend trees, the seven behaviour encoders, sub-machines, Entry/Any ladders).
    ///
    /// REACHABILITY: only objects reachable from a layer's state machine become part of the Doc; unreachable
    /// sub-assets (dead weight an owned cleaner would strip) are COUNTED in <see cref="WalkResult.OrphanCount"/>,
    /// never emitted. A dangling motion GUID (asset gone) decodes to a <see cref="GuidRef"/> marked
    /// <c>Unresolved</c> and is listed in <see cref="WalkResult.UnresolvedGuids"/>.
    ///
    /// SCOPE: produces the full document from an emitted/clean or imported controller. Import TOLERANCES —
    /// mixed Write-Defaults hoisted to a modal layer policy + minority per-state overrides (Task 7), and an
    /// empty <c>timeParameter</c> normalized to an unbound motion time with a Note — plus the full REFUSAL
    /// taxonomy: a construct that cannot be expressed in the schema (out-of-vocabulary or malformed input:
    /// whitespace-colliding sibling states, a null state machine, an empty inline clip, a null playAudio clip)
    /// is appended to <see cref="WalkResult.Refusals"/> (named + located) rather than silently approximated or
    /// thrown. Transition durations etc. are decoded EXPLICITLY per transition (never hoisted to inherited
    /// defaults), so the re-emit is exact even though the authoring form may have used inheritance.
    /// </summary>
    public static class ControllerDecompile
    {
        public sealed class WalkResult
        {
            public AnimDocument Doc;
            public int OrphanCount;
            public List<string> UnresolvedGuids = new List<string>();
            public List<string> Refusals = new List<string>();   // out-of-vocabulary constructs, named + located
            public List<string> Notes = new List<string>();      // tolerances applied, informational
        }

        public static WalkResult Walk(AnimatorController controller)
        {
            var ctx = new WalkContext(controller);
            return ctx.Run();
        }

        private sealed class WalkContext
        {
            private readonly AnimatorController _controller;
            private readonly string _controllerPath;
            private readonly WalkResult _result = new WalkResult();
            private readonly AnimDocument _doc = new AnimDocument();

            // Decoded inline clips, keyed by name so a clip shared by several states is emitted once.
            private readonly Dictionary<string, ClipSpec> _clips = new Dictionary<string, ClipSpec>();

            // Per-layer addressing maps, rebuilt for each layer (state names recur across machines, so these
            // are never layer-global lookups keyed by bare name — they are object-keyed).
            private Dictionary<AnimatorState, AnimatorStateMachine> _stateOwner;
            private Dictionary<AnimatorState, string> _statePath;
            private Dictionary<AnimatorStateMachine, string> _smPath;
            private Dictionary<AnimatorStateMachine, AnimatorStateMachine> _smParent;

            // Dangling motion GUIDs recovered from the controller YAML (assets that no longer resolve).
            private readonly Queue<string> _danglingGuids;

            public WalkContext(AnimatorController controller)
            {
                _controller = controller;
                _controllerPath = AssetDatabase.GetAssetPath(controller);
                _danglingGuids = new Queue<string>(RecoverDanglingMotionGuids(controller));
            }

            public WalkResult Run()
            {
                _doc.Schema = 1;
                _doc.ControllerName = _controller.name;

                DecodeParameters();
                foreach (var layer in _controller.layers)
                {
                    if (layer.syncedLayerIndex >= 0)
                    {
                        // A synced layer re-skins another layer's states with override motions/behaviours — a
                        // distinct construct the schema has no vocabulary for yet.
                        _result.Refusals.Add($"layer '{layer.name}': synced layer (syncedLayerIndex={layer.syncedLayerIndex}) is out of vocabulary");
                        continue;
                    }
                    _doc.Layers.Add(DecodeLayer(layer));
                }
                _doc.Clips.AddRange(_clips.Values);

                _result.OrphanCount = CountOrphans();
                _result.Doc = _doc;
                return _result;
            }

            // ----- parameters -----

            private void DecodeParameters()
            {
                foreach (var p in _controller.parameters)
                {
                    if (p.name == ReservedNames.CarrierParam) continue; // compiler scratch, never authored
                    var spec = new ParamSpec { Name = p.name };
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Bool:
                            spec.Type = AnimParamType.Bool; spec.Default = p.defaultBool ? 1f : 0f; break;
                        case AnimatorControllerParameterType.Int:
                            spec.Type = AnimParamType.Int; spec.Default = p.defaultInt; break;
                        case AnimatorControllerParameterType.Trigger:
                            // No schema vocabulary for a trigger param (the SDK/compiler surface is bool/int/float).
                            _result.Refusals.Add($"parameter '{p.name}': Trigger type is out of vocabulary");
                            continue;
                        default:
                            spec.Type = AnimParamType.Float; spec.Default = p.defaultFloat; break;
                    }
                    _doc.Parameters.Add(spec);
                }
            }

            // ----- layers -----

            private Layer DecodeLayer(AnimatorControllerLayer layer)
            {
                var model = new Layer
                {
                    Name = layer.name,
                    Weight = layer.defaultWeight,
                    Blend = layer.blendingMode == AnimatorLayerBlendingMode.Additive ? LayerBlend.Additive : LayerBlend.Override,
                    WriteDefaults = null, // decoded per-state; never hoisted here (that is a Task-7 tolerance)
                };
                if (layer.avatarMask != null) model.Mask = AssetDatabase.GetAssetPath(layer.avatarMask);

                if (layer.stateMachine == null)
                {
                    // A malformed controller: a layer with no state machine has no reachable graph to decode.
                    _result.Refusals.Add($"layer '{layer.name}': has no state machine (null root) — malformed controller");
                    return model; // empty Root, WriteDefaults left null (no states to derive a policy from)
                }
                BuildAddressMaps(layer.stateMachine);
                model.Root = DecodeMachine(layer.stateMachine);
                HoistWriteDefaults(model);
                return model;
            }

            // Derive the layer's Write-Defaults policy as the MODAL per-state WD value across ALL of the layer's
            // states (root + every sub-machine), set it on the Layer, and clear the now-redundant per-state
            // override on every majority state — leaving an explicit State.WriteDefaults only on the minority.
            // Re-emit (s.WriteDefaults ?? layer.WriteDefaults ?? Defaults) then reproduces the SAME per-state WD
            // mix. DETERMINISTIC: on a tie prefer true (trueCount >= falseCount). A uniform-WD layer hoists to a
            // single policy with zero overrides — so it round-trips unchanged, and the Task-9 fixpoint holds.
            private static void HoistWriteDefaults(Layer model)
            {
                var states = new List<State>();
                CollectStates(model.Root, states);
                if (states.Count == 0) return; // nothing to derive from; leave WriteDefaults null

                int trueCount = 0, falseCount = 0;
                foreach (var s in states) { if (s.WriteDefaults == true) trueCount++; else falseCount++; }
                bool policy = trueCount >= falseCount; // tie-break: prefer true
                model.WriteDefaults = policy;
                foreach (var s in states)
                    if (s.WriteDefaults == policy) s.WriteDefaults = null; // majority inherits the layer policy
            }

            private static void CollectStates(StateMachine sm, List<State> into)
            {
                if (sm == null) return;
                into.AddRange(sm.States);
                foreach (var child in sm.Machines) CollectStates(child.Machine, into);
            }

            // Map every state/sub-machine of the layer to its owning machine + path-from-root, so a transition
            // target can be addressed exactly as ControllerEmit.ResolveName consumes it: BARE within the source
            // machine, SLASH-QUALIFIED (from the layer root) for a nested cross-machine target, and '/'-ANCHORED
            // (absolute from root) for a TOP-LEVEL target whose root path is a single bare segment (which would
            // otherwise read as local).
            private void BuildAddressMaps(AnimatorStateMachine root)
            {
                _stateOwner = new Dictionary<AnimatorState, AnimatorStateMachine>();
                _statePath = new Dictionary<AnimatorState, string>();
                _smPath = new Dictionary<AnimatorStateMachine, string>();
                _smParent = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
                Recurse(root, "", null);

                void Recurse(AnimatorStateMachine sm, string path, AnimatorStateMachine parent)
                {
                    if (sm == null) return;
                    _smPath[sm] = path;
                    _smParent[sm] = parent;
                    foreach (var cs in sm.states)
                    {
                        if (cs.state == null) continue;
                        _stateOwner[cs.state] = sm;
                        string es = AddressPath.EscapeSegment(cs.state.name);
                        _statePath[cs.state] = path.Length == 0 ? es : path + "/" + es;
                    }
                    foreach (var child in sm.stateMachines)
                    {
                        if (child.stateMachine == null) continue;
                        string ec = AddressPath.EscapeSegment(child.stateMachine.name);
                        string cp = path.Length == 0 ? ec : path + "/" + ec;
                        Recurse(child.stateMachine, cp, sm);
                    }
                }
            }

            // ----- state machines -----

            private StateMachine DecodeMachine(AnimatorStateMachine sm)
            {
                var model = new StateMachine();

                DetectWhitespaceCollisions(sm);
                foreach (var cs in sm.states)
                    if (cs.state != null) model.States.Add(DecodeState(cs.state, sm));

                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null)
                        model.Machines.Add(new SubMachine { Name = child.stateMachine.name, Machine = DecodeMachine(child.stateMachine) });

                // Default: a DIRECT-state default is authoritative; a sub-machine default is the trailing
                // unconditional entry transition (ControllerEmit adds it last, after the entry ladder). Unity's
                // defaultState getter resolves THROUGH to a child's default when this machine has no direct
                // states, so it is only trusted when it names one of THIS machine's own direct states.
                var directStates = new HashSet<AnimatorState>(sm.states.Where(x => x.state != null).Select(x => x.state));
                bool defaultIsDirectState = sm.defaultState != null && directStates.Contains(sm.defaultState);
                if (defaultIsDirectState) model.DefaultState = AddressPath.EscapeSegment(sm.defaultState.name);

                // Entry ladder. The trailing unconditional entry into a sub-machine is the sub-machine default,
                // not a ladder rung — split it off.
                var entries = sm.entryTransitions;
                int subDefaultIdx = -1;
                if (!defaultIsDirectState && entries.Length > 0)
                {
                    var last = entries[entries.Length - 1];
                    if ((last.conditions == null || last.conditions.Length == 0) && last.destinationStateMachine != null && !last.isExit)
                    {
                        subDefaultIdx = entries.Length - 1;
                        model.DefaultState = AddressPath.EscapeSegment(last.destinationStateMachine.name); // direct child ⇒ escaped bare name
                    }
                }
                for (int i = 0; i < entries.Length; i++)
                {
                    if (i == subDefaultIdx) continue;
                    model.EntryLadder.Add(DecodeEntryTransition(entries[i], sm));
                }

                foreach (var t in sm.anyStateTransitions)
                {
                    var tr = DecodeStateTransition(t, sm, "AnyState in '" + PathLabel(sm) + "'");
                    tr.CanTransitionToSelf = t.canTransitionToSelf;
                    model.AnyLadder.Add(tr);
                }

                if (sm.behaviours != null)
                    foreach (var b in sm.behaviours)
                        DecodeBehaviourInto(model.Behaviours, b, "state-machine '" + PathLabel(sm) + "'");

                return model;
            }

            // Sibling states whose names are equal once trimmed but differ raw (e.g. "S" vs "S ") collide under
            // name-keyed addressing — a transition target `S` cannot disambiguate them. Refuse loudly (named +
            // located), never silently collapse/dedup: both states are still decoded into the machine.
            private void DetectWhitespaceCollisions(AnimatorStateMachine sm)
            {
                var byTrim = new Dictionary<string, List<string>>();
                foreach (var cs in sm.states)
                {
                    if (cs.state == null) continue;
                    string raw = cs.state.name;
                    string key = raw.Trim();
                    if (!byTrim.TryGetValue(key, out var list)) { list = new List<string>(); byTrim[key] = list; }
                    if (!list.Contains(raw)) list.Add(raw);
                }
                foreach (var cs in sm.states) // iterate states (not the dict) so the refusal order is deterministic
                {
                    if (cs.state == null) continue;
                    string key = cs.state.name.Trim();
                    if (byTrim.TryGetValue(key, out var list) && list.Count > 1 && list[0] == cs.state.name)
                        _result.Refusals.Add(
                            $"state-machine '{PathLabel(sm)}': sibling states {string.Join(", ", list.Select(n => "'" + n + "'"))} " +
                            $"collide on trimmed name '{key}' — they differ only by surrounding whitespace");
                }
            }

            private State DecodeState(AnimatorState ast, AnimatorStateMachine owner)
            {
                var st = new State
                {
                    Name = ast.name,
                    Speed = ast.speed,
                    Mirror = ast.mirror,
                    WriteDefaults = ast.writeDefaultValues,
                };
                if (ast.speedParameterActive) st.SpeedParam = ast.speedParameter;
                if (ast.timeParameterActive)
                {
                    // Every vendor Gesture (the SDK HandsLayer2 template) ships timeParameterActive with an EMPTY
                    // timeParameter — a no-op the runtime ignores. TOLERATE it (never a Refusal): normalize to an
                    // unbound motion time (MotionTimeParam == null) + a Note, so re-emit does not fabricate a bind.
                    if (string.IsNullOrEmpty(ast.timeParameter))
                        _result.Notes.Add($"state '{ast.name}': timeParameterActive with an empty timeParameter — normalized to unbound motion time");
                    else
                        st.MotionTimeParam = ast.timeParameter;
                }
                if (ast.mirrorParameterActive)
                    _result.Refusals.Add($"state '{ast.name}': mirror-parameter binding '{ast.mirrorParameter}' is out of vocabulary");
                if (ast.cycleOffsetParameterActive)
                    _result.Refusals.Add($"state '{ast.name}': cycleOffset-parameter binding '{ast.cycleOffsetParameter}' is out of vocabulary");

                if (ast.motion != null)
                    st.Motion = DecodeMotion(ast.motion, "state '" + ast.name + "'");
                else if (HasDanglingMotion(ast))
                {
                    string guid = _danglingGuids.Count > 0 ? _danglingGuids.Dequeue() : "unknown";
                    st.Motion = new MotionRef { RefGuid = new GuidRef { Guid = guid, Unresolved = true } };
                    _result.UnresolvedGuids.Add(guid);
                }
                // else: a genuine clean-empty state (motion: ~) — leave Motion null.

                if (ast.behaviours != null)
                    foreach (var b in ast.behaviours)
                        DecodeBehaviourInto(st.Behaviours, b, "state '" + ast.name + "'");

                foreach (var t in ast.transitions)
                    st.Transitions.Add(DecodeStateTransition(t, owner, "state '" + ast.name + "'"));

                return st;
            }

            // A null motion slot that still carries a serialized object reference (instanceID != 0) is a
            // dangling ref to a since-deleted asset — distinct from a clean-empty state (instanceID 0).
            private static bool HasDanglingMotion(AnimatorState ast)
            {
                var mp = new SerializedObject(ast).FindProperty("m_Motion");
                return mp != null && mp.objectReferenceInstanceIDValue != 0;
            }

            // ----- transitions -----

            private Transition DecodeStateTransition(AnimatorStateTransition t, AnimatorStateMachine srcSm, string loc)
            {
                var tr = new Transition();
                SetTarget(tr, t, srcSm, loc);
                tr.Mute = t.mute;
                tr.Solo = t.solo;

                tr.When = DecodeConditions(t.conditions);
                tr.ExitTime = t.hasExitTime ? t.exitTime : (float?)null;
                tr.Duration = t.duration != 0f ? t.duration : (float?)null;
                tr.FixedDuration = t.hasFixedDuration ? (bool?)null : false;
                tr.Interruption = t.interruptionSource == TransitionInterruptionSource.None
                    ? (TransitionInterruption?)null : MapInterruption(t.interruptionSource, loc);
                tr.OrderedInterruption = t.orderedInterruption ? (bool?)null : false;
                return tr;
            }

            private Transition DecodeEntryTransition(AnimatorTransition t, AnimatorStateMachine srcSm)
            {
                var tr = new Transition();
                SetTarget(tr, t, srcSm, "Entry in '" + PathLabel(srcSm) + "'");
                tr.When = DecodeConditions(t.conditions);
                return tr;
            }

            // Resolve a transition's target into the schema's To/ToExit addressing.
            private void SetTarget(Transition tr, AnimatorTransitionBase t, AnimatorStateMachine srcSm, string loc)
            {
                if (t.isExit) { tr.ToExit = true; return; }
                if (t.destinationState != null) { tr.To = StateTargetName(t.destinationState, srcSm, loc); return; }
                if (t.destinationStateMachine != null) { tr.To = SmTargetName(t.destinationStateMachine, srcSm, loc); return; }
                // A dangling target (neither state, sub-machine, nor Exit) — cannot be addressed.
                _result.Refusals.Add($"transition from {loc}: target resolves to no state/sub-machine/Exit");
            }

            // Every emitted target is per-segment escaped (a '/' in a name survives as '\/'), so ResolveName can
            // split on the UNescaped separator. A single-segment path is a top-level entity, anchored with '/'.
            private string StateTargetName(AnimatorState dest, AnimatorStateMachine srcSm, string loc)
            {
                if (_stateOwner.TryGetValue(dest, out var owner) && owner == srcSm) return AddressPath.EscapeSegment(dest.name); // same machine ⇒ bare
                if (_statePath.TryGetValue(dest, out var path))
                    return AddressPath.Split(path).Count == 1 ? "/" + path : path;
                _result.Refusals.Add($"transition from {loc}: target state '{dest.name}' not found in the layer");
                return dest.name;
            }

            private string SmTargetName(AnimatorStateMachine dest, AnimatorStateMachine srcSm, string loc)
            {
                if (_smParent.TryGetValue(dest, out var parent) && parent == srcSm) return AddressPath.EscapeSegment(dest.name); // direct child ⇒ bare
                if (_smPath.TryGetValue(dest, out var path))
                    // The layer root itself (empty path) is a legal target — "re-enter the layer at its
                    // default" — addressed by the bare '/' anchor. A top-level machine is a single segment,
                    // anchored '/'; a multi-segment path is already root-relative.
                    return path.Length == 0 ? "/" : (AddressPath.Split(path).Count == 1 ? "/" + path : path);
                _result.Refusals.Add($"transition from {loc}: target sub-machine '{dest.name}' not found in the layer");
                return dest.name;
            }

            // Invert ControllerEmit.MapCondOp. Bool truth lives in the MODE there (If/IfNot); recover it as the
            // canonical `Is true` / `Is false` (Op=Is, Value 1/0). Numeric ops carry the threshold as the value.
            private static List<Condition> DecodeConditions(AnimatorCondition[] conds)
            {
                var list = new List<Condition>();
                if (conds == null) return list;
                foreach (var c in conds)
                {
                    var cond = new Condition { Param = c.parameter };
                    switch (c.mode)
                    {
                        case AnimatorConditionMode.If: cond.Op = CondOp.Is; cond.Value = 1f; break;
                        case AnimatorConditionMode.IfNot: cond.Op = CondOp.Is; cond.Value = 0f; break;
                        case AnimatorConditionMode.Greater: cond.Op = CondOp.Greater; cond.Value = c.threshold; break;
                        case AnimatorConditionMode.Less: cond.Op = CondOp.Less; cond.Value = c.threshold; break;
                        case AnimatorConditionMode.Equals: cond.Op = CondOp.Equals; cond.Value = c.threshold; break;
                        case AnimatorConditionMode.NotEqual: cond.Op = CondOp.NotEqual; cond.Value = c.threshold; break;
                        default: cond.Op = CondOp.Is; cond.Value = 1f; break;
                    }
                    list.Add(cond);
                }
                return list;
            }

            // ----- motions / blend trees -----

            private MotionRef DecodeMotion(Motion m, string loc)
            {
                if (m == null) return null;
                if (m is AnimationClip clip)
                {
                    string p = AssetDatabase.GetAssetPath(clip);
                    if (string.IsNullOrEmpty(_controllerPath) || p == _controllerPath)
                    {
                        // Embedded controller sub-asset ⇒ inline ClipSpec.
                        RegisterInlineClip(clip);
                        return new MotionRef { Clip = clip.name };
                    }
                    if (AssetDatabase.IsSubAsset(clip))
                    {
                        // A sub-asset of ANOTHER asset (e.g. one clip inside an FBX / an SDK proxy) — address by
                        // guid + fileID, not a re-embedded copy.
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long fileId);
                        return new MotionRef { RefGuid = new GuidRef { Guid = guid, FileID = fileId } };
                    }
                    return new MotionRef { RefPath = p }; // standalone project .anim
                }
                if (m is BlendTree bt)
                    return new MotionRef { Tree = DecodeTree(bt, loc) };

                _result.Refusals.Add($"motion in {loc}: unsupported Motion type '{m.GetType().Name}'");
                return null;
            }

            private BlendTreeSpec DecodeTree(BlendTree bt, string loc)
            {
                var spec = new BlendTreeSpec { Kind = MapTreeKind(bt.blendType, loc) };
                bool direct = spec.Kind == TreeKind.Direct;
                bool twoD = Is2D(bt.blendType);
                if (!direct)
                {
                    spec.Param = bt.blendParameter;
                    if (twoD) spec.ParamY = bt.blendParameterY;
                }
                // A tree CHILD can carry a dangling motion ref exactly like a state's motion slot can. The
                // recovery regex matches ChildMotion.m_Motion lines too, so a child dangler is in the same queue
                // — decode it HERE (mirroring the state path) or it (a) loses the `unresolved` marker
                // ControllerEmit.BuildMotion preserves, and (b) leaves an orphan guid a later state mis-dequeues.
                var childs = new SerializedObject(bt).FindProperty("m_Childs");
                var kids = bt.children;
                for (int i = 0; i < kids.Length; i++)
                {
                    var ch = kids[i];
                    string childLoc = loc + " (tree child " + i + ")";
                    var tc = new TreeChild
                    {
                        TimeScale = ch.timeScale,
                        Mirror = ch.mirror,
                        CycleOffset = ch.cycleOffset,
                    };
                    if (ch.motion != null) tc.Motion = DecodeMotion(ch.motion, childLoc);
                    else if (ChildHasDanglingMotion(childs, i))
                    {
                        string guid = _danglingGuids.Count > 0 ? _danglingGuids.Dequeue() : "unknown";
                        tc.Motion = new MotionRef { RefGuid = new GuidRef { Guid = guid, Unresolved = true } };
                        _result.UnresolvedGuids.Add(guid);
                    }
                    // else: a genuine empty child (rare) — leave Motion null, never silently dropping a dangler.

                    if (direct) tc.DirectWeight = ch.directBlendParameter;
                    else if (twoD) { tc.PosX = ch.position.x; tc.PosY = ch.position.y; }
                    else tc.Threshold = ch.threshold;
                    spec.Children.Add(tc);
                }
                return spec;
            }

            private static bool ChildHasDanglingMotion(SerializedProperty childs, int i)
            {
                if (childs == null || i >= childs.arraySize) return false;
                var mp = childs.GetArrayElementAtIndex(i).FindPropertyRelative("m_Motion");
                return mp != null && mp.objectReferenceInstanceIDValue != 0;
            }

            // ----- inline clips (invert ControllerEmit.BuildClip) -----

            private void RegisterInlineClip(AnimationClip clip)
            {
                if (_clips.ContainsKey(clip.name)) return;
                _clips[clip.name] = DecodeClip(clip);
            }

            private ClipSpec DecodeClip(AnimationClip clip)
            {
                var spec = new ClipSpec { Name = clip.name };
                var bindings = AnimationUtility.GetCurveBindings(clip);

                // Seconds-only carrier: a single flat curve on the reserved _CompilerNull animator param. Its
                // sole purpose is to give the clip an honest length — recover that as `seconds`, no sets/curves.
                if (bindings.Length == 1 && bindings[0].path == "" && bindings[0].type == typeof(Animator)
                    && bindings[0].propertyName == ReservedNames.CarrierParam)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, bindings[0]);
                    spec.Seconds = curve != null && curve.length > 0 ? curve.keys[curve.length - 1].time : clip.length;
                    return spec;
                }

                // A hand-authored clip with ZERO curve bindings has no animatable content — ControllerEmit.BuildClip
                // would reject the resulting empty ClipSpec (no content, no seconds). Refuse loudly here instead.
                if (bindings.Length == 0)
                {
                    _result.Refusals.Add($"inline clip '{clip.name}': has no animatable content (zero curve bindings) — malformed");
                    return spec;
                }

                float maxConstEnd = 0f;
                foreach (var b in bindings)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    string target = ReconstructBindingTarget(b);
                    if (curve == null || curve.length == 0) continue;
                    if (IsConstant(curve))
                    {
                        spec.Sets[target] = curve.keys[0].value;
                        maxConstEnd = Mathf.Max(maxConstEnd, curve.keys[curve.length - 1].time);
                    }
                    else
                        spec.Curves.Add(new CurveSpec
                        {
                            Binding = target,
                            Keys = curve.keys.Select(k => new Keyframe2(k.time, k.value)).ToList(),
                        });
                }
                // A Set curve carries no keyframes in the schema, so a Sets clip authored with an explicit
                // `seconds:` (emitted as a constant curve stretched to that length) would otherwise re-emit at
                // MinClipLength. Recover the declared length from the constant end time when it exceeds one
                // frame (a plain Set with no seconds sits at MinClipLength ⇒ leave Seconds null). A keyframed
                // curve needs no such recovery — its own last key already carries the length.
                if (spec.Curves.Count == 0 && maxConstEnd > MinClipLength + 1e-4f)
                    spec.Seconds = maxConstEnd;
                return spec;
            }

            // One frame at 60fps — ControllerEmit's honest floor for a content-only clip's derived length.
            private const float MinClipLength = 1f / 60f;

            private static bool IsConstant(AnimationCurve curve)
            {
                float v = curve.keys[0].value;
                for (int i = 1; i < curve.length; i++)
                    if (!Mathf.Approximately(curve.keys[i].value, v)) return false;
                return true;
            }

            // Invert ControllerEmit.ResolveBinding: an animator-property binding (path "", type Animator) is a
            // bare parameter name; otherwise "path/Component.property" (empty path drops the leading slash).
            private static string ReconstructBindingTarget(EditorCurveBinding b)
            {
                if (b.type == typeof(Animator) && b.path == "") return b.propertyName;
                string comp = b.type.Name;
                string body = comp + "." + b.propertyName;
                return b.path.Length == 0 ? body : b.path + "/" + body;
            }

            // ----- behaviours (invert the seven ControllerEmit.Populate* encoders) -----

            private void DecodeBehaviourInto(List<Behaviour> into, StateMachineBehaviour smb, string loc)
            {
                if (smb == null)
                {
                    _result.Refusals.Add($"behaviour on {loc}: missing script (null SMB)");
                    return;
                }
                switch (smb)
                {
                    case VRC_AvatarParameterDriver drv: into.Add(DecodeDriver(drv)); break;
                    case VRC_AnimatorTrackingControl tc: into.Add(DecodeTracking(tc, loc)); break;
                    case VRC_PlayableLayerControl pl: into.Add(DecodePlayableLayer(pl, loc)); break;
                    case VRC_AnimatorLocomotionControl lc: into.Add(DecodeLocomotion(lc)); break;
                    case VRC_AnimatorTemporaryPoseSpace ps: into.Add(DecodePoseSpace(ps)); break;
                    case VRC_AnimatorPlayAudio pa: into.Add(DecodePlayAudio(pa, loc)); break;
                    case VRC_AnimatorLayerControl lyc: into.Add(DecodeLayerControl(lyc, loc)); break;
                    default:
                        _result.Refusals.Add($"behaviour on {loc}: unsupported SMB type '{smb.GetType().Name}' is out of vocabulary");
                        break;
                }
            }

            private static Behaviour DecodeDriver(VRC_AvatarParameterDriver drv)
            {
                // Field-name tokens are shared from ControllerEmit.DriverKeys so encode/decode cannot drift.
                var b = new Behaviour { Kind = "driver" };
                if (drv.localOnly) b.Fields[ControllerEmit.DriverKeys.LocalOnly] = true; // default false is left implicit
                var set = new Dictionary<string, object>();
                var add = new Dictionary<string, object>();
                var copy = new Dictionary<string, object>();
                var random = new Dictionary<string, object>();
                if (drv.parameters != null)
                    foreach (var p in drv.parameters)
                    {
                        switch (p.type)
                        {
                            case Driver.ChangeType.Set: set[p.name] = p.value; break;
                            case Driver.ChangeType.Add: add[p.name] = p.value; break;
                            case Driver.ChangeType.Copy:
                                if (p.convertRange)
                                    copy[p.name] = new Dictionary<string, object>
                                    {
                                        { ControllerEmit.DriverKeys.Source, p.source },
                                        { ControllerEmit.DriverKeys.SourceMin, p.sourceMin }, { ControllerEmit.DriverKeys.SourceMax, p.sourceMax },
                                        { ControllerEmit.DriverKeys.DestMin, p.destMin }, { ControllerEmit.DriverKeys.DestMax, p.destMax },
                                    };
                                else copy[p.name] = p.source;
                                break;
                            case Driver.ChangeType.Random:
                                random[p.name] = new Dictionary<string, object>
                                {
                                    { ControllerEmit.DriverKeys.Min, p.valueMin }, { ControllerEmit.DriverKeys.Max, p.valueMax }, { ControllerEmit.DriverKeys.Chance, p.chance },
                                };
                                break;
                        }
                    }
                if (set.Count > 0) b.Fields[ControllerEmit.DriverKeys.Set] = set;
                if (add.Count > 0) b.Fields[ControllerEmit.DriverKeys.Add] = add;
                if (copy.Count > 0) b.Fields[ControllerEmit.DriverKeys.Copy] = copy;
                if (random.Count > 0) b.Fields[ControllerEmit.DriverKeys.Random] = random;
                return b;
            }

            private Behaviour DecodeTracking(VRC_AnimatorTrackingControl tc, string loc)
            {
                var b = new Behaviour { Kind = "tracking" };
                void Ch(string name, TrackingType v) { if (v != TrackingType.NoChange) b.Fields[name] = Token(TrackingRev, v, loc + " tracking." + name); }
                Ch("head", tc.trackingHead);
                Ch("leftHand", tc.trackingLeftHand);
                Ch("rightHand", tc.trackingRightHand);
                Ch("hip", tc.trackingHip);
                Ch("leftFoot", tc.trackingLeftFoot);
                Ch("rightFoot", tc.trackingRightFoot);
                Ch("leftFingers", tc.trackingLeftFingers);
                Ch("rightFingers", tc.trackingRightFingers);
                Ch("eyes", tc.trackingEyes);
                Ch("mouth", tc.trackingMouth);
                return b;
            }

            private Behaviour DecodePlayableLayer(VRC_PlayableLayerControl c, string loc)
            {
                var b = new Behaviour { Kind = "playableLayer" };
                b.Fields["layer"] = Token(PlayableLayerRev, c.layer, loc + " playableLayer.layer");
                b.Fields["goalWeight"] = c.goalWeight;
                b.Fields["blendDuration"] = c.blendDuration;
                return b;
            }

            private static Behaviour DecodeLocomotion(VRC_AnimatorLocomotionControl c)
            {
                var b = new Behaviour { Kind = "locomotion" };
                b.Fields["disableLocomotion"] = c.disableLocomotion;
                return b;
            }

            private static Behaviour DecodePoseSpace(VRC_AnimatorTemporaryPoseSpace c)
            {
                var b = new Behaviour { Kind = "poseSpace" };
                b.Fields["enterPoseSpace"] = c.enterPoseSpace;
                b.Fields["fixedDelay"] = c.fixedDelay;
                b.Fields["delayTime"] = c.delayTime;
                return b;
            }

            private Behaviour DecodeLayerControl(VRC_AnimatorLayerControl c, string loc)
            {
                var b = new Behaviour { Kind = "layerControl" };
                b.Fields["playable"] = Token(LayerCtrlRev, c.playable, loc + " layerControl.playable");
                b.Fields["layer"] = c.layer;   // the integer layer INDEX (SDK asymmetry vs playableLayer)
                b.Fields["goalWeight"] = c.goalWeight;
                b.Fields["blendDuration"] = c.blendDuration;
                return b;
            }

            private Behaviour DecodePlayAudio(VRC_AnimatorPlayAudio c, string loc)
            {
                // Field-name tokens shared from ControllerEmit.PlayAudioKeys so encode/decode cannot drift.
                var b = new Behaviour { Kind = "playAudio" };
                b.Fields[ControllerEmit.PlayAudioKeys.SourcePath] = c.SourcePath;
                b.Fields[ControllerEmit.PlayAudioKeys.PlaybackOrder] = Token(AudioOrderRev, c.PlaybackOrder, loc + " playAudio.playbackOrder");
                b.Fields[ControllerEmit.PlayAudioKeys.Parameter] = c.ParameterName;
                b.Fields[ControllerEmit.PlayAudioKeys.Volume] = new List<object> { c.Volume.x, c.Volume.y };
                b.Fields[ControllerEmit.PlayAudioKeys.VolumeApply] = Token(AudioApplyRev, c.VolumeApplySettings, loc + " playAudio.volumeApply");
                b.Fields[ControllerEmit.PlayAudioKeys.Pitch] = new List<object> { c.Pitch.x, c.Pitch.y };
                b.Fields[ControllerEmit.PlayAudioKeys.PitchApply] = Token(AudioApplyRev, c.PitchApplySettings, loc + " playAudio.pitchApply");
                b.Fields[ControllerEmit.PlayAudioKeys.Loop] = c.Loop;
                b.Fields[ControllerEmit.PlayAudioKeys.LoopApply] = Token(AudioApplyRev, c.LoopApplySettings, loc + " playAudio.loopApply");
                b.Fields[ControllerEmit.PlayAudioKeys.ClipsApply] = Token(AudioApplyRev, c.ClipsApplySettings, loc + " playAudio.clipsApply");
                b.Fields[ControllerEmit.PlayAudioKeys.DelaySeconds] = c.DelayInSeconds;
                b.Fields[ControllerEmit.PlayAudioKeys.PlayOnEnter] = c.PlayOnEnter;
                b.Fields[ControllerEmit.PlayAudioKeys.StopOnEnter] = c.StopOnEnter;
                b.Fields[ControllerEmit.PlayAudioKeys.PlayOnExit] = c.PlayOnExit;
                b.Fields[ControllerEmit.PlayAudioKeys.StopOnExit] = c.StopOnExit;
                if (c.Clips != null && c.Clips.Length > 0)
                {
                    // A null Clips[] entry (a since-deleted AudioClip) would decode to a null path that
                    // ControllerEmit.AsClips rejects — refuse loudly (named + located), dropping the null entry.
                    var paths = new List<object>();
                    foreach (var clip in c.Clips)
                    {
                        if (clip == null)
                        {
                            _result.Refusals.Add($"behaviour on {loc}: playAudio has a null clip entry (a since-deleted AudioClip)");
                            continue;
                        }
                        paths.Add(AssetDatabase.GetAssetPath(clip));
                    }
                    b.Fields[ControllerEmit.PlayAudioKeys.Clips] = paths;
                }
                return b;
            }

            // ----- orphan reachability (mirror ControllerRules.RuleOrphanSubAsset, plus clip reachability) -----

            private int CountOrphans()
            {
                if (string.IsNullOrEmpty(_controllerPath))
                {
                    _result.Notes.Add("orphan count skipped: controller is not a saved asset.");
                    return 0;
                }
                var reachable = new HashSet<Object>();
                void AddMotion(Motion m)
                {
                    if (m == null || !reachable.Add(m)) return; // adds clip OR tree
                    if (m is BlendTree bt) foreach (var ch in bt.children) AddMotion(ch.motion);
                }
                void AddSm(AnimatorStateMachine sm)
                {
                    if (sm == null || !reachable.Add(sm)) return;
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
                foreach (var layer in _controller.layers)
                    if (layer.syncedLayerIndex < 0) AddSm(layer.stateMachine);

                int orphans = 0;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(_controllerPath))
                {
                    if (o == null || o == _controller) continue;
                    if (!(o is AnimatorStateMachine || o is AnimatorState || o is BlendTree
                          || o is StateMachineBehaviour || o is AnimatorTransitionBase || o is AnimationClip)) continue;
                    if (reachable.Contains(o)) continue;
                    orphans++;
                }
                return orphans;
            }

            private string PathLabel(AnimatorStateMachine sm)
            {
                if (_smPath != null && _smPath.TryGetValue(sm, out var p)) return p.Length == 0 ? "(root)" : p;
                return sm != null ? sm.name : "(null)";
            }

            // TOTAL enum→token lookup: an SDK value with no schema token (a member added to the SDK but not to
            // ControllerEmit's token map) is a located Refusal, never a silent approximation.
            private string Token<TEnum>(Dictionary<TEnum, string> rev, TEnum v, string loc)
            {
                if (rev.TryGetValue(v, out var s)) return s;
                _result.Refusals.Add($"{loc}: SDK value '{v}' has no schema token (out of vocabulary)");
                return v.ToString();
            }

            private TreeKind MapTreeKind(BlendTreeType t, string loc)
            {
                switch (t)
                {
                    case BlendTreeType.Simple1D: return TreeKind.OneD;
                    case BlendTreeType.SimpleDirectional2D: return TreeKind.SimpleDirectional2D;
                    case BlendTreeType.FreeformDirectional2D: return TreeKind.FreeformDirectional2D;
                    case BlendTreeType.FreeformCartesian2D: return TreeKind.FreeformCartesian2D;
                    case BlendTreeType.Direct: return TreeKind.Direct;
                    default:
                        _result.Refusals.Add($"{loc}: blend-tree type '{t}' is out of vocabulary");
                        return TreeKind.Direct;
                }
            }

            private TransitionInterruption MapInterruption(TransitionInterruptionSource s, string loc)
            {
                switch (s)
                {
                    case TransitionInterruptionSource.None: return TransitionInterruption.None;
                    case TransitionInterruptionSource.Source: return TransitionInterruption.Source;
                    case TransitionInterruptionSource.Destination: return TransitionInterruption.Destination;
                    case TransitionInterruptionSource.SourceThenDestination: return TransitionInterruption.SourceThenDestination;
                    case TransitionInterruptionSource.DestinationThenSource: return TransitionInterruption.DestinationThenSource;
                    default:
                        _result.Refusals.Add($"{loc}: transition interruption source '{s}' is out of vocabulary");
                        return TransitionInterruption.None;
                }
            }
        }

        // ----- Unity-enum -> schema-token reverse maps (built FROM ControllerEmit's token maps) -----
        // Single source of truth: these invert ControllerEmit's forward (token→enum) dictionaries, so a new
        // SDK enum member added there is automatically decodable and the two directions cannot drift.

        private static readonly Dictionary<TrackingType, string> TrackingRev = Invert(ControllerEmit.TrackingTokens);
        private static readonly Dictionary<PlayableLayer, string> PlayableLayerRev = Invert(ControllerEmit.PlayableLayerTokens);
        private static readonly Dictionary<LayerCtrlLayer, string> LayerCtrlRev = Invert(ControllerEmit.LayerCtrlTokens);
        private static readonly Dictionary<AudioOrder, string> AudioOrderRev = Invert(ControllerEmit.AudioOrderTokens);
        private static readonly Dictionary<AudioApply, string> AudioApplyRev = Invert(ControllerEmit.AudioApplyTokens);

        private static Dictionary<TEnum, string> Invert<TEnum>(Dictionary<string, TEnum> fwd)
        {
            var rev = new Dictionary<TEnum, string>();
            foreach (var kv in fwd) rev[kv.Value] = kv.Key; // forward maps are 1:1, so the inverse is total
            return rev;
        }

        private static bool Is2D(BlendTreeType t) =>
            t == BlendTreeType.SimpleDirectional2D || t == BlendTreeType.FreeformDirectional2D || t == BlendTreeType.FreeformCartesian2D;

        // ----- dangling-motion GUID recovery (mirror of the ControllerReport/ControllerRules YAML parse) -----

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
    }
}
