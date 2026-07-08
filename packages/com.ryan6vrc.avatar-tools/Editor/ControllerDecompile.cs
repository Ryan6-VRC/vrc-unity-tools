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
    /// SCOPE (Task 5): produces the full document from an emitted/clean controller. Import TOLERANCES (mixed-WD
    /// hoisting, empty-timeParameter normalization, whitespace collisions) and the full REFUSAL taxonomy are
    /// Task 7 — here, a construct that cannot be expressed in the schema is appended to
    /// <see cref="WalkResult.Refusals"/> (named + located) rather than silently approximated. Write-Defaults,
    /// transition durations, etc. are decoded EXPLICITLY per state/transition (never hoisted to inherited
    /// defaults), so the re-emit is exact even though the authoring form that produced the controller may have
    /// used inheritance.
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

                BuildAddressMaps(layer.stateMachine);
                model.Root = DecodeMachine(layer.stateMachine);
                return model;
            }

            // Map every state/sub-machine of the layer to its owning machine + path-from-root, so a transition
            // target can be addressed exactly as ControllerEmit.ResolveName consumes it: BARE within the source
            // machine, SLASH-QUALIFIED (from the layer root) across machines.
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
                        _statePath[cs.state] = path.Length == 0 ? cs.state.name : path + "/" + cs.state.name;
                    }
                    foreach (var child in sm.stateMachines)
                    {
                        if (child.stateMachine == null) continue;
                        string cp = path.Length == 0 ? child.stateMachine.name : path + "/" + child.stateMachine.name;
                        Recurse(child.stateMachine, cp, sm);
                    }
                }
            }

            // ----- state machines -----

            private StateMachine DecodeMachine(AnimatorStateMachine sm)
            {
                var model = new StateMachine();

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
                if (defaultIsDirectState) model.DefaultState = sm.defaultState.name;

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
                        model.DefaultState = last.destinationStateMachine.name; // always a direct child ⇒ bare name
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
                if (ast.timeParameterActive) st.MotionTimeParam = ast.timeParameter;
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
                if (t.mute || t.solo)
                    _result.Refusals.Add($"transition from {loc}: mute/solo flags are out of vocabulary");

                tr.When = DecodeConditions(t.conditions);
                tr.ExitTime = t.hasExitTime ? t.exitTime : (float?)null;
                tr.Duration = t.duration != 0f ? t.duration : (float?)null;
                tr.FixedDuration = t.hasFixedDuration ? (bool?)null : false;
                tr.Interruption = t.interruptionSource == TransitionInterruptionSource.None
                    ? (TransitionInterruption?)null : MapInterruption(t.interruptionSource);
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

            private string StateTargetName(AnimatorState dest, AnimatorStateMachine srcSm, string loc)
            {
                if (_stateOwner.TryGetValue(dest, out var owner) && owner == srcSm) return dest.name; // same machine ⇒ bare
                if (_statePath.TryGetValue(dest, out var path))
                {
                    if (path.IndexOf('/') < 0)
                        _result.Refusals.Add($"transition from {loc}: cross-machine target state '{dest.name}' sits at the layer root and is not addressable from a nested machine");
                    return path;
                }
                _result.Refusals.Add($"transition from {loc}: target state '{dest.name}' not found in the layer");
                return dest.name;
            }

            private string SmTargetName(AnimatorStateMachine dest, AnimatorStateMachine srcSm, string loc)
            {
                if (_smParent.TryGetValue(dest, out var parent) && parent == srcSm) return dest.name; // direct child ⇒ bare
                if (_smPath.TryGetValue(dest, out var path))
                {
                    if (path.IndexOf('/') < 0)
                        _result.Refusals.Add($"transition from {loc}: cross-machine target sub-machine '{dest.name}' is not addressable from a nested machine");
                    return path;
                }
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
                var spec = new BlendTreeSpec { Kind = MapTreeKind(bt.blendType) };
                bool direct = spec.Kind == TreeKind.Direct;
                bool twoD = Is2D(bt.blendType);
                if (!direct)
                {
                    spec.Param = bt.blendParameter;
                    if (twoD) spec.ParamY = bt.blendParameterY;
                }
                foreach (var ch in bt.children)
                {
                    var tc = new TreeChild
                    {
                        Motion = DecodeMotion(ch.motion, loc + " (tree child)"),
                        TimeScale = ch.timeScale,
                        Mirror = ch.mirror,
                        CycleOffset = ch.cycleOffset,
                    };
                    if (direct) tc.DirectWeight = ch.directBlendParameter;
                    else if (twoD) { tc.PosX = ch.position.x; tc.PosY = ch.position.y; }
                    else tc.Threshold = ch.threshold;
                    spec.Children.Add(tc);
                }
                return spec;
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

                foreach (var b in bindings)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    string target = ReconstructBindingTarget(b);
                    if (curve == null || curve.length == 0) continue;
                    if (IsConstant(curve))
                        spec.Sets[target] = curve.keys[0].value;
                    else
                        spec.Curves.Add(new CurveSpec
                        {
                            Binding = target,
                            Keys = curve.keys.Select(k => new Keyframe2(k.time, k.value)).ToList(),
                        });
                }
                return spec;
            }

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
                    case VRC_AnimatorTrackingControl tc: into.Add(DecodeTracking(tc)); break;
                    case VRC_PlayableLayerControl pl: into.Add(DecodePlayableLayer(pl)); break;
                    case VRC_AnimatorLocomotionControl lc: into.Add(DecodeLocomotion(lc)); break;
                    case VRC_AnimatorTemporaryPoseSpace ps: into.Add(DecodePoseSpace(ps)); break;
                    case VRC_AnimatorPlayAudio pa: into.Add(DecodePlayAudio(pa)); break;
                    case VRC_AnimatorLayerControl lyc: into.Add(DecodeLayerControl(lyc)); break;
                    default:
                        _result.Refusals.Add($"behaviour on {loc}: unsupported SMB type '{smb.GetType().Name}' is out of vocabulary");
                        break;
                }
            }

            private static Behaviour DecodeDriver(VRC_AvatarParameterDriver drv)
            {
                var b = new Behaviour { Kind = "driver" };
                if (drv.localOnly) b.Fields["localOnly"] = true; // default false is left implicit
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
                                        { "source", p.source }, { "sourceMin", p.sourceMin }, { "sourceMax", p.sourceMax },
                                        { "destMin", p.destMin }, { "destMax", p.destMax },
                                    };
                                else copy[p.name] = p.source;
                                break;
                            case Driver.ChangeType.Random:
                                random[p.name] = new Dictionary<string, object>
                                {
                                    { "min", p.valueMin }, { "max", p.valueMax }, { "chance", p.chance },
                                };
                                break;
                        }
                    }
                if (set.Count > 0) b.Fields["set"] = set;
                if (add.Count > 0) b.Fields["add"] = add;
                if (copy.Count > 0) b.Fields["copy"] = copy;
                if (random.Count > 0) b.Fields["random"] = random;
                return b;
            }

            private static Behaviour DecodeTracking(VRC_AnimatorTrackingControl tc)
            {
                var b = new Behaviour { Kind = "tracking" };
                void Ch(string name, TrackingType v) { if (v != TrackingType.NoChange) b.Fields[name] = TrackingToken(v); }
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

            private static Behaviour DecodePlayableLayer(VRC_PlayableLayerControl c)
            {
                var b = new Behaviour { Kind = "playableLayer" };
                b.Fields["layer"] = PlayableLayerToken(c.layer);
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

            private static Behaviour DecodeLayerControl(VRC_AnimatorLayerControl c)
            {
                var b = new Behaviour { Kind = "layerControl" };
                b.Fields["playable"] = LayerCtrlToken(c.playable);
                b.Fields["layer"] = c.layer;   // the integer layer INDEX (SDK asymmetry vs playableLayer)
                b.Fields["goalWeight"] = c.goalWeight;
                b.Fields["blendDuration"] = c.blendDuration;
                return b;
            }

            private Behaviour DecodePlayAudio(VRC_AnimatorPlayAudio c)
            {
                var b = new Behaviour { Kind = "playAudio" };
                b.Fields["sourcePath"] = c.SourcePath;
                b.Fields["playbackOrder"] = AudioOrderToken(c.PlaybackOrder);
                b.Fields["parameter"] = c.ParameterName;
                b.Fields["volume"] = new List<object> { c.Volume.x, c.Volume.y };
                b.Fields["volumeApply"] = AudioApplyToken(c.VolumeApplySettings);
                b.Fields["pitch"] = new List<object> { c.Pitch.x, c.Pitch.y };
                b.Fields["pitchApply"] = AudioApplyToken(c.PitchApplySettings);
                b.Fields["loop"] = c.Loop;
                b.Fields["loopApply"] = AudioApplyToken(c.LoopApplySettings);
                b.Fields["clipsApply"] = AudioApplyToken(c.ClipsApplySettings);
                b.Fields["delaySeconds"] = c.DelayInSeconds;
                b.Fields["playOnEnter"] = c.PlayOnEnter;
                b.Fields["stopOnEnter"] = c.StopOnEnter;
                b.Fields["playOnExit"] = c.PlayOnExit;
                b.Fields["stopOnExit"] = c.StopOnExit;
                if (c.Clips != null && c.Clips.Length > 0)
                {
                    var paths = new List<object>();
                    foreach (var clip in c.Clips) paths.Add(clip != null ? AssetDatabase.GetAssetPath(clip) : null);
                    b.Fields["clips"] = paths;
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
        }

        // ----- Unity-enum -> schema-token maps (inverse of ControllerEmit's token maps) -----

        private static bool Is2D(BlendTreeType t) =>
            t == BlendTreeType.SimpleDirectional2D || t == BlendTreeType.FreeformDirectional2D || t == BlendTreeType.FreeformCartesian2D;

        private static TreeKind MapTreeKind(BlendTreeType t)
        {
            switch (t)
            {
                case BlendTreeType.Simple1D: return TreeKind.OneD;
                case BlendTreeType.SimpleDirectional2D: return TreeKind.SimpleDirectional2D;
                case BlendTreeType.FreeformDirectional2D: return TreeKind.FreeformDirectional2D;
                case BlendTreeType.FreeformCartesian2D: return TreeKind.FreeformCartesian2D;
                default: return TreeKind.Direct;
            }
        }

        private static TransitionInterruption MapInterruption(TransitionInterruptionSource s)
        {
            switch (s)
            {
                case TransitionInterruptionSource.Source: return TransitionInterruption.Source;
                case TransitionInterruptionSource.Destination: return TransitionInterruption.Destination;
                case TransitionInterruptionSource.SourceThenDestination: return TransitionInterruption.SourceThenDestination;
                case TransitionInterruptionSource.DestinationThenSource: return TransitionInterruption.DestinationThenSource;
                default: return TransitionInterruption.None;
            }
        }

        private static string TrackingToken(TrackingType v)
        {
            switch (v) { case TrackingType.Tracking: return "tracking"; case TrackingType.Animation: return "animation"; default: return "noChange"; }
        }

        private static string PlayableLayerToken(PlayableLayer v)
        {
            switch (v) { case PlayableLayer.Action: return "action"; case PlayableLayer.FX: return "fx"; case PlayableLayer.Gesture: return "gesture"; default: return "additive"; }
        }

        private static string LayerCtrlToken(LayerCtrlLayer v)
        {
            switch (v) { case LayerCtrlLayer.Action: return "action"; case LayerCtrlLayer.FX: return "fx"; case LayerCtrlLayer.Gesture: return "gesture"; default: return "additive"; }
        }

        private static string AudioOrderToken(AudioOrder v)
        {
            switch (v) { case AudioOrder.Random: return "random"; case AudioOrder.UniqueRandom: return "uniqueRandom"; case AudioOrder.Roundabout: return "roundabout"; default: return "parameter"; }
        }

        private static string AudioApplyToken(AudioApply v)
        {
            switch (v) { case AudioApply.AlwaysApply: return "alwaysApply"; case AudioApply.ApplyIfStopped: return "applyIfStopped"; default: return "neverApply"; }
        }

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
