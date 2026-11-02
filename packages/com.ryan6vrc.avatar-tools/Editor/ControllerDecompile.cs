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
#if VRC_FT_SETS_PRESENT
using FtSets = VRC.SDKBase.VRC_AnimatorFaceTrackingBlendshapesSet;
using FtKeys = Ryan6Vrc.AvatarTools.Editor.ControllerEmit.FaceTrackingSetKeys;
#endif

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The READ direction, inverse of <see cref="ControllerEmit"/>: a reachability walk that turns an
    /// <see cref="AnimatorController"/> back into an <see cref="AnimDocument"/> whose re-emit reproduces it.
    /// <see cref="ControllerEmit"/> is the authoritative spec; every decoder here inverts one of its emit paths.
    /// Transition durations and the like decode per transition, never hoisted to <c>defaults:</c>, so the
    /// re-emit is exact. Contract: docs/animator-schema.md §Decompile output.
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
            // Parallel to Refusals: the index of the layer each one belongs to, or -1 for a refusal that is
            // not a layer's (a parameter, or the controller as a whole).
            public List<int> RefusalLayers = new List<int>();
            public int LayerCount;
        }

        // stripLayout suppresses all per-machine layout capture, for owning a vendor controller whose node
        // arrangement is YAML noise ahead of a heavy rewrite.
        public static WalkResult Walk(AnimatorController controller, bool stripLayout = false)
        {
            var ctx = new WalkContext(controller, stripLayout);
            return ctx.Run();
        }

        private sealed class WalkContext
        {
            private readonly AnimatorController _controller;
            private readonly string _controllerPath;
            private readonly bool _stripLayout;
            private readonly WalkResult _result = new WalkResult();
            private readonly AnimDocument _doc = new AnimDocument();

            // Decoded inline clips, keyed by name. _clipObjs holds each name's source clip, so a distinct clip
            // re-using a name is refused rather than deduped as if shared.
            private readonly Dictionary<string, ClipSpec> _clips = new Dictionary<string, ClipSpec>();
            private readonly Dictionary<string, AnimationClip> _clipObjs = new Dictionary<string, AnimationClip>();

            // Multi-parent blend trees, each decoded once into a `trees:` entry its reference sites name.
            // CountTreeParents fills _sharedName (tree to key) before the walk; _sharedDone holds decoded entries.
            private Dictionary<BlendTree, string> _sharedName;
            private readonly Dictionary<string, BlendTreeSpec> _sharedDone = new Dictionary<string, BlendTreeSpec>();

            // Per-layer addressing maps, rebuilt for each layer and keyed by object, because state names recur
            // across machines.
            private Dictionary<AnimatorState, AnimatorStateMachine> _stateOwner;
            private Dictionary<AnimatorState, string> _statePath;
            private Dictionary<AnimatorStateMachine, string> _smPath;
            private Dictionary<AnimatorStateMachine, AnimatorStateMachine> _smParent;
            // The layer being decoded, as the location labels' prefix. _layerIndex is the loop variable over
            // _controller.layers, so it matches the controller's numbering even where a refused layer is never
            // decoded.
            private string _currentLayer;
            // -1 whenever the walk is not inside the per-layer loop — DecodeParameters runs BEFORE it, and a
            // parameter refusal filed against layer 0 would misattribute the offender and inflate the
            // refused-layer count. The loop sets it; everything outside must leave it at -1.
            private int _layerIndex = -1;

            // The one funnel every refusal goes through; it keeps RefusalLayers parallel to Refusals.
            private void Refuse(string text)
            {
                _result.Refusals.Add(text);
                _result.RefusalLayers.Add(_layerIndex);
            }

            // Dangling motion GUIDs recovered from the controller YAML, keyed by the owning serialized object's
            // local fileID so each null motion slot recovers its own guid.
            private readonly Dictionary<long, Queue<string>> _danglingByOwner;

            // Declared animator parameter names, which split the two path-"" Animator binding meanings
            // (ReconstructBindingTarget).
            private readonly HashSet<string> _animParamNames = new HashSet<string>();

            public WalkContext(AnimatorController controller, bool stripLayout)
            {
                _controller = controller;
                _stripLayout = stripLayout;
                _controllerPath = AssetDatabase.GetAssetPath(controller);
                _danglingByOwner = RecoverDanglingMotionGuids(controller);
                foreach (var p in controller.parameters) _animParamNames.Add(p.name);
            }

            // The next dangling guid recorded against this owner (a State or BlendTree), or "unknown" when the
            // controller isn't a saved asset or the owner carries no recovered dangler.
            private string NextDanglingGuid(Object owner)
            {
                if (owner != null
                    && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(owner, out _, out long fid)
                    && _danglingByOwner.TryGetValue(fid, out var q) && q.Count > 0)
                    return q.Dequeue();
                return "unknown";
            }

            public WalkResult Run()
            {
                _doc.Schema = 1;
                _doc.ControllerName = _controller.name;

                DecodeParameters();
                _sharedName = CountTreeParents();
                for (_layerIndex = 0; _layerIndex < _controller.layers.Length; _layerIndex++)
                {
                    var layer = _controller.layers[_layerIndex];
                    if (layer.syncedLayerIndex >= 0)
                    {
                        Refuse($"layer '{layer.name}': synced layer (syncedLayerIndex={layer.syncedLayerIndex}) is out of vocabulary");
                        continue;
                    }
                    _doc.Layers.Add(DecodeLayer(layer));
                }
                _layerIndex = -1; // out of the loop: anything refused below belongs to the document
                _result.LayerCount = _controller.layers.Length;
                _doc.Clips.AddRange(_clips.Values);
                _doc.Trees.AddRange(_sharedDone.Values);   // first-reference order, as _clips is

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
                            Refuse($"parameter '{p.name}': Trigger type is out of vocabulary");
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
                // Indexed, because Unity does not enforce unique layer names — two same-named layers
                // each carrying a same-named state would otherwise still produce identical locations.
                _currentLayer = "[" + _layerIndex + "] '" + layer.name + "'";
                var model = new Layer
                {
                    Name = layer.name,
                    Weight = layer.defaultWeight,
                    Blend = layer.blendingMode == AnimatorLayerBlendingMode.Additive ? LayerBlend.Additive : LayerBlend.Override,
                    WriteDefaults = null, // set from the decoded states by HoistWriteDefaults
                };
                if (layer.avatarMask != null) model.Mask = AssetDatabase.GetAssetPath(layer.avatarMask);

                if (layer.iKPass)
                    Refuse($"layer '{layer.name}': IK pass (iKPass) is out of vocabulary");

                if (layer.stateMachine == null)
                {
                    Refuse($"layer '{layer.name}': has no state machine (null root) — malformed controller");
                    return model;
                }
                BuildAddressMaps(layer.stateMachine);
                model.Root = DecodeMachine(layer.stateMachine, isRoot: true);
                HoistWriteDefaults(model);
                return model;
            }

            // Hoist the modal per-state Write Defaults across the layer's root and sub-machines to the layer, tie
            // preferring true; only minority states keep an override, so re-emit
            // (s.WriteDefaults ?? layer.WriteDefaults ?? Defaults) reproduces the mix.
            private void HoistWriteDefaults(Layer model)
            {
                var states = new List<State>();
                model.Root.CollectStates(states);
                if (states.Count == 0) return;

                int trueCount = 0, falseCount = 0;
                foreach (var s in states) { if (s.WriteDefaults == true) trueCount++; else falseCount++; }
                bool policy = trueCount >= falseCount;
                model.WriteDefaults = policy;
                foreach (var s in states)
                    if (s.WriteDefaults == policy) s.WriteDefaults = null;

                if (trueCount > 0 && falseCount > 0)
                    _result.Notes.Add($"layer '{model.Name}': mixed Write Defaults normalized to modal policy (writeDefaults: {(policy ? "true" : "false")})");
            }

            // Map every state and sub-machine of the layer to its owning machine and path from root, so a target
            // can be addressed as ControllerEmit.ResolveName consumes it: bare within the source machine,
            // slash-qualified from the layer root for a nested cross-machine target, and '/'-anchored for a
            // top-level target, whose single-segment path would otherwise read as local.
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

            private StateMachine DecodeMachine(AnimatorStateMachine sm, bool isRoot)
            {
                var model = new StateMachine();

                DetectSiblingNameCollisions(sm);
                foreach (var cs in sm.states)
                    if (cs.state != null) model.States.Add(DecodeState(cs.state, sm));

                foreach (var child in sm.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    var subModel = new SubMachine { Name = child.stateMachine.name, Machine = DecodeMachine(child.stateMachine, isRoot: false) };
                    var smt = sm.GetStateMachineTransitions(child.stateMachine);
                    if (smt != null)
                        foreach (var t in smt)
                            if (t != null)
                                subModel.OnExit.Add(DecodeSubMachineExitTransition(t, sm, child.stateMachine));
                    model.Machines.Add(subModel);
                }

                // Default. Unity stores one m_DefaultState PPtr per machine, and it means one of three things:
                //
                //   direct state   -> the ordinary `default:`.
                //   foreign state  -> a state nested inside a descendant machine. Legal, durable through
                //                     serialization, and reachable from Unity's "Set as Layer Default State" on a
                //                     nested node. This is `defaultState:` (a root-relative address).
                //   auto-populated -> adding a state to a descendant machine walks up and fills an ancestor's
                //                     empty m_DefaultState. Neither the getter nor a raw SerializedProperty read
                //                     can tell this from an authored foreign default (measured);
                //                     foreignIsAutoPopulation below decides it.
                var directStates = new HashSet<AnimatorState>(sm.states.Where(x => x.state != null).Select(x => x.state));
                bool defaultIsDirectState = sm.defaultState != null && directStates.Contains(sm.defaultState);
                if (defaultIsDirectState) model.DefaultState = AddressPath.EscapeSegment(sm.defaultState.name);

                if (sm.defaultState == null && HasDanglingDefaultState(sm))
                    Refuse($"{MachineLabel(sm)}: the default state reference is broken (the state it "
                        + "named no longer exists) — a decompile would drop it and the rebuild would boot a "
                        + "different state; repair or clear the default in the animator window");

                // The trailing unconditional entry into a direct child sub-machine is what the compiler emits for
                // a `default:` naming a sub-machine, so it is the candidate to fold back into that key. A deeper
                // target stays a ladder rung with a slash-qualified address; as a bare default it would fail to
                // recompile.
                var entries = sm.entryTransitions;
                int foldableIdx = -1;
                AnimatorStateMachine foldTarget = null;
                if (!defaultIsDirectState && entries.Length > 0)
                {
                    var last = entries[entries.Length - 1];
                    if ((last.conditions == null || last.conditions.Length == 0) && last.destinationStateMachine != null && !last.isExit
                        && _smParent.TryGetValue(last.destinationStateMachine, out var lastParent) && lastParent == sm)
                    {
                        foldableIdx = entries.Length - 1;
                        foldTarget = last.destinationStateMachine;
                    }
                }

                // A foreign PPtr needs no key when a rebuild would re-derive it. ControllerEmit adds a machine's
                // direct states before recursing into its children, so auto-population lands on the first state
                // emitted anywhere in the subtree, depth-first, and not on the child machine's own default: a
                // sub-machine with states [X, Y] and default Y hands the ancestor X, because the later
                // `sub.defaultState = Y` does not propagate up (both measured). The predicate needs no entry rung,
                // since a raw vendor machine wires Entry through m_DefaultState alone.
                bool foreignIsAutoPopulation =
                    sm.defaultState != null && !defaultIsDirectState
                    && FirstEmittedState(sm) == sm.defaultState;

                bool defaultIsForeign = sm.defaultState != null && !defaultIsDirectState && !foreignIsAutoPopulation;

                // A foreign default and a trailing unconditional rung co-exist in Unity (measured), so the fold is
                // suppressed: the rung stays an ordinary ladder rung and both facts survive. Folding while dropping
                // the PPtr would silently rewrite which state the layer boots.
                int subDefaultIdx = -1;
                if (foldableIdx >= 0 && !defaultIsForeign)
                {
                    subDefaultIdx = foldableIdx;
                    model.DefaultState = AddressPath.EscapeSegment(foldTarget.name); // direct child ⇒ escaped bare name
                }

                if (defaultIsForeign)
                {
                    // The addressing StateTargetName emits for `to:`: root-relative, per-segment escaped, and
                    // '/'-anchored when it is a single segment.
                    if (_statePath.TryGetValue(sm.defaultState, out var dpath))
                        model.DefaultStatePath = AddressPath.Split(dpath).Count == 1 ? "/" + dpath : dpath;
                    else
                        Refuse($"{MachineLabel(sm)}: the default state '{sm.defaultState.name}' is not "
                            + "reachable from this layer's root state machine, so the schema cannot address it — "
                            + "a decompile would drop it and the rebuild would boot a different state");
                }

                for (int i = 0; i < entries.Length; i++)
                {
                    if (i == subDefaultIdx)
                    {
                        // This rung folds into `default:` and skips DecodeEntryTransition, so it needs its own
                        // mute/solo refusal, name Note and completeness sweep.
                        string dloc = "Entry in " + MachineLabel(sm);
                        if (entries[i].mute || entries[i].solo)
                            Refuse($"transition from {dloc}: the default sub-machine rung carries "
                                + "mute/solo, which a `default:` cannot express — it would recompile as a live "
                                + "unconditional default");
                        if (!string.IsNullOrEmpty(entries[i].name))
                            _result.Notes.Add($"{dloc}: the default sub-machine rung's cosmetic "
                                + $"name '{entries[i].name}' is not carried by the schema and will not survive a recompile");
                        CompletenessSweep(entries[i], EntryTransitionAware, "transition from", dloc);
                        continue;
                    }
                    model.EntryLadder.Add(DecodeEntryTransition(entries[i], sm));
                }

                foreach (var t in sm.anyStateTransitions)
                {
                    var tr = DecodeStateTransition(t, sm, "AnyState in " + MachineLabel(sm));
                    tr.CanTransitionToSelf = t.canTransitionToSelf;
                    model.AnyLadder.Add(tr);
                }

                if (sm.behaviours != null)
                    foreach (var b in sm.behaviours)
                        DecodeBehaviourInto(model.Behaviours, b, MachineLabel(sm));

                model.Layout = _stripLayout ? null : CaptureLayout(sm, isRoot);
                return model;
            }

            // Positions live in the parent's child structs (states[i].position / stateMachines[i].position),
            // not AnimatorState.m_Position, which emit never writes.
            private static MachineLayout CaptureLayout(AnimatorStateMachine sm, bool isRoot)
            {
                if (IsDefaultLayout(sm, isRoot)) return null;
                var l = new MachineLayout();
                foreach (var cs in sm.states)
                    if (cs.state != null)
                        l.Nodes[AddressPath.EscapeSegment(cs.state.name)] = new[] { cs.position.x, cs.position.y };
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null)
                        l.Nodes[AddressPath.EscapeSegment(child.stateMachine.name)] = new[] { child.position.x, child.position.y };
                l.Entry = new[] { sm.entryPosition.x, sm.entryPosition.y };
                l.Any   = new[] { sm.anyStatePosition.x, sm.anyStatePosition.y };
                l.Exit  = new[] { sm.exitPosition.x, sm.exitPosition.y };
                if (!isRoot) l.Parent = new[] { sm.parentStateMachinePosition.x, sm.parentStateMachinePosition.y };
                return l;
            }

            // Relies on opening a controller in Unity's Animator window not moving the special nodes off the
            // tool-owned constants (verified: an opened-but-unedited controller neither dirties nor moves them).
            // If that regresses, drop the special-node comparisons below and compare states and sub-machines
            // only; CaptureLayout still records the special positions whenever a block is emitted.
            private static bool IsDefaultLayout(AnimatorStateMachine sm, bool isRoot)
            {
                int i = 0;
                foreach (var cs in sm.states)
                {
                    if (cs.state == null) continue;
                    if (!Approx(cs.position, ControllerEmit.GridState(i++))) return false;
                }
                int j = 0;
                foreach (var child in sm.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    if (!Approx(child.position, ControllerEmit.GridSub(j++))) return false;
                }
                if (!Approx(sm.entryPosition,    ControllerEmit.SpecialEntry)) return false;
                if (!Approx(sm.anyStatePosition, ControllerEmit.SpecialAny))   return false;
                if (!Approx(sm.exitPosition,     ControllerEmit.SpecialExit))  return false;
                if (!isRoot && !Approx(sm.parentStateMachinePosition, ControllerEmit.SpecialParent)) return false;
                return true;
            }

            private static bool Approx(Vector3 a, Vector3 b)
                => Mathf.Abs(a.x - b.x) < 0.01f && Mathf.Abs(a.y - b.y) < 0.01f;

            // Sibling name collisions are located Refusals, never a silent collapse. Exact duplicates serialize as
            // duplicate YAML keys the parser refuses on re-parse. State names equal once trimmed but differing raw
            // ("S" vs "S ") are a legibility hazard under name-keyed addressing, refused even though quoting could
            // carry them.
            private void DetectSiblingNameCollisions(AnimatorStateMachine sm)
            {
                DetectExactDuplicates(sm.states.Where(x => x.state != null).Select(x => x.state.name), "state", sm);
                DetectExactDuplicates(sm.stateMachines.Where(x => x.stateMachine != null).Select(x => x.stateMachine.name), "sub-machine", sm);
                DetectCrossKindCollisions(sm);
                DetectWhitespaceCollisions(sm);
            }

            // A direct state and a direct sub-machine may share a name (separate Unity collections). ResolveName
            // resolves states first, so a bare target at the sub-machine would silently rewire to the state on
            // recompile.
            private void DetectCrossKindCollisions(AnimatorStateMachine sm)
            {
                var stateNames = new HashSet<string>(sm.states.Where(x => x.state != null).Select(x => x.state.name));
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null && stateNames.Contains(child.stateMachine.name))
                        Refuse(
                            $"{MachineLabel(sm)}: a direct state and a direct sub-machine are both named " +
                            $"'{child.stateMachine.name}' — a bare target or default can't disambiguate them (states resolve first)");
            }

            private void DetectExactDuplicates(IEnumerable<string> rawNames, string kind, AnimatorStateMachine sm)
            {
                var list = rawNames.ToList();
                var counts = new Dictionary<string, int>();
                foreach (var n in list) counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
                var reported = new HashSet<string>();
                foreach (var n in list) // iterate the list (not the dict) so refusal order is deterministic
                    if (counts[n] > 1 && reported.Add(n))
                        Refuse(
                            $"{MachineLabel(sm)}: {counts[n]} sibling {kind}s are named '{n}' — " +
                            "identical sibling names serialize as duplicate keys and cannot round-trip");
            }

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
                        Refuse(
                            $"{MachineLabel(sm)}: sibling states {string.Join(", ", list.Select(n => "'" + n + "'"))} " +
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
                    // The SDK's HandsLayer2 Gesture template ships Idle2 states with timeParameterActive and an
                    // empty timeParameter, a no-op the runtime ignores, so this normalizes to an unbound motion time
                    // with a Note rather than refusing or fabricating a bind on re-emit.
                    if (string.IsNullOrEmpty(ast.timeParameter))
                        _result.Notes.Add($"{StateLabel(ast)}: timeParameterActive with an empty timeParameter — normalized to unbound motion time");
                    else
                        st.MotionTimeParam = ast.timeParameter;
                }
                if (ast.mirrorParameterActive)
                    Refuse($"{StateLabel(ast)}: mirror-parameter binding '{ast.mirrorParameter}' is out of vocabulary");
                if (ast.cycleOffsetParameterActive)
                    Refuse($"{StateLabel(ast)}: cycleOffset-parameter binding '{ast.cycleOffsetParameter}' is out of vocabulary");

                if (ast.motion != null)
                    st.Motion = DecodeMotion(ast.motion, StateLabel(ast), ControllerEmit.AutoTreeName(ast.name));
                else if (HasDanglingMotion(ast))
                {
                    string guid = NextDanglingGuid(ast);
                    st.Motion = new MotionRef { RefGuid = new GuidRef { Guid = guid, Unresolved = true } };
                    _result.UnresolvedGuids.Add(guid);
                }

                if (ast.behaviours != null)
                    foreach (var b in ast.behaviours)
                        DecodeBehaviourInto(st.Behaviours, b, StateLabel(ast));

                // The compiler sets canTransitionToSelf from the target (on exactly for a rung to its own state),
                // so a self rung at flag 1 decodes as plain `to: <self>`. One at flag 0 never fires, and a later
                // sibling whose condition holds at the same time still fires past it (bare Animator), so dropping
                // it is the faithful decode; the recompile would bring it alive. A soloed one silences its non-solo
                // siblings whether or not it can fire, so dropping that would wake them.
                foreach (var t in ast.transitions)
                {
                    if (t != null && !t.isExit && t.destinationState == ast && !t.canTransitionToSelf)
                    {
                        if (t.solo)
                            Refuse($"transition from {StateLabel(ast)} to itself: canTransitionToSelf is off, so "
                                + "it never fires, but it is soloed, which silences the state's other rungs; the "
                                + "schema cannot hold a dead rung, and dropping it would wake them");
                        else
                            _result.Notes.Add($"transition from {StateLabel(ast)} to itself: canTransitionToSelf "
                                + "is off, so it never fires (measured on a bare Animator and in av3emu); dropped, "
                                + "since a recompile would emit it with the flag on");
                        continue;
                    }
                    st.Transitions.Add(DecodeStateTransition(t, owner, StateLabel(ast)));
                }

                CompletenessSweep(ast, StateAware, "", StateLabel(ast));
                return st;
            }

            // ----- decode completeness census -----
            //
            // The decoders are a hand-maintained allowlist, so the census sweeps every serialized property of a
            // decoded object and refuses any non-default scalar its type's *Aware set does not name. A field the
            // decoder forgot, or one a future SDK adds, fails loud until someone consumes it or lists it. An
            // *Aware set names what the decode path consumes, refuses, or ignores as editor-cosmetic. Left
            // unlisted on purpose, so refused when set: m_CycleOffset, m_IKOnFeet, m_Tag (state).
            //
            // Scope: the top-level scalars of AnimatorState, state/any/entry transitions, BlendTree and the eight
            // VRC SMB kinds. Not covered, so only the hand decoders guard them: array-element structs (ChildMotion,
            // ChildAnimatorState, ChildAnimatorStateMachine, AnimatorCondition) and AnimatorControllerLayer (a
            // struct, not SerializedObject-sweepable).
            //
            // AnimatorStateMachine sweeps fine but is excluded: Unity auto-populates its m_DefaultState, whose
            // default is therefore not type-zero, so the sweep would flag nearly every machine, this compiler's
            // own output included. The decode rule in DecodeMachine and the built-graph assertions in
            // CrossMachineDefaultTests guard that field instead.

            private static readonly HashSet<string> UniversalIgnore = new HashSet<string>
            {
                "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
                "m_PrefabInternal", "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_Script",
                "m_EditorClassIdentifier",
            };

            // m_Name is listed per type rather than in UniversalIgnore, so a future swept type that forgets it is
            // refused loud. A state's name is captured by its YAML map key, a BlendTree's and a StateTransition's
            // by the `name:` field (MeaningfulName). An SMB's is editor-cosmetic and ignored. An entry rung's is
            // tolerated with a Note rather than refused: vendor controllers do carry them, and nothing reads them
            // at runtime.
            private static readonly HashSet<string> StateAware = new HashSet<string>
            {
                "m_Speed", "m_Motion", "m_Transitions", "m_WriteDefaultValues", "m_Mirror",
                "m_SpeedParameterActive", "m_MirrorParameterActive", "m_CycleOffsetParameterActive",
                "m_TimeParameterActive", "m_SpeedParameter", "m_MirrorParameter", "m_CycleOffsetParameter",
                "m_TimeParameter", "m_StateMachineBehaviours", "m_Position", "m_Name",
            };

            private static readonly HashSet<string> StateTransitionAware = new HashSet<string>
            {
                "m_Conditions", "m_DstState", "m_DstStateMachine", "m_TransitionDuration", "m_ExitTime",
                "m_TransitionOffset", "m_HasExitTime", "m_HasFixedDuration", "m_InterruptionSource",
                "m_OrderedInterruption", "m_Mute", "m_Solo", "m_CanTransitionToSelf", "m_IsExit", "m_Name",
            };

            private static readonly HashSet<string> EntryTransitionAware = new HashSet<string>
            {
                "m_Conditions", "m_DstState", "m_DstStateMachine", "m_IsExit", "m_Mute", "m_Solo", "m_Name",
            };

            private static readonly HashSet<string> BlendTreeAware = new HashSet<string>
            {
                "m_BlendType", "m_BlendParameter", "m_BlendParameterY", "m_Childs", "m_Name",
                // Inert for the round-trip (child thresholds are read and emitted explicitly), except
                // m_NormalizedBlendValues, which DecodeTree reads for Direct trees.
                "m_MinThreshold", "m_MaxThreshold", "m_UseAutomaticThresholds", "m_NormalizedBlendValues",
            };

            // Per-SMB aware sets. VRC components are MonoBehaviours, so public fields serialize under their own
            // names with no m_ prefix. The Object-level name still serializes as m_Name, which UniversalIgnore does
            // not cover, and is a cosmetic Inspector name ignored here. Each set lists the fields its decoder
            // consumes plus the editor-only debugString.
            //
            // `parameters` names the list, and the sweep stops there: nothing inside a Parameter element is
            // checked, so DecodeDriver captures every field of that struct by hand with no backstop. A field the
            // SDK adds inside Parameter will be silent; when one shows up, extend the sweep one level for this
            // component.
            private static readonly HashSet<string> DriverAware = new HashSet<string> { "parameters", "localOnly", "debugString", "m_Name" };
            private static readonly HashSet<string> TrackingAware = new HashSet<string>
            {
                "trackingHead", "trackingLeftHand", "trackingRightHand", "trackingHip", "trackingLeftFoot",
                "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers", "trackingEyes", "trackingMouth",
                "debugString", "m_Name",
            };
            private static readonly HashSet<string> PlayableLayerAware = new HashSet<string> { "layer", "goalWeight", "blendDuration", "debugString", "m_Name" };
            private static readonly HashSet<string> LocomotionAware = new HashSet<string> { "disableLocomotion", "debugString", "m_Name" };
            private static readonly HashSet<string> PoseSpaceAware = new HashSet<string> { "enterPoseSpace", "fixedDelay", "delayTime", "debugString", "m_Name" };
            private static readonly HashSet<string> LayerControlAware = new HashSet<string> { "playable", "layer", "goalWeight", "blendDuration", "debugString", "m_Name" };
            private static readonly HashSet<string> PlayAudioAware = new HashSet<string>
            {
                "SourcePath", "PlaybackOrder", "ParameterName", "Volume", "VolumeApplySettings", "Pitch",
                "PitchApplySettings", "Loop", "LoopApplySettings", "Clips", "ClipsApplySettings", "DelayInSeconds",
                "PlayOnEnter", "StopOnEnter", "PlayOnExit", "StopOnExit", "debugString", "m_Name",
            };
#if VRC_FT_SETS_PRESENT
            // `blendSets` names the list, so nothing inside an element is swept; DecodeFaceTrackingSets reads every
            // element field by hand.
            private static readonly HashSet<string> FaceTrackingSetsAware = new HashSet<string> { "blendSets", "debugString", "m_Name" };
#endif

            private void CompletenessSweep(Object o, HashSet<string> aware, string kind, string loc)
            {
                if (o == null) return;
                using (var so = new SerializedObject(o))
                {
                    var it = so.GetIterator();
                    for (bool ok = it.Next(true); ok; ok = it.Next(false)) // Next (not NextVisible) — hidden fields count too
                    {
                        if (it.depth != 0) continue;
                        string n = it.name;
                        if (UniversalIgnore.Contains(n) || aware.Contains(n)) continue;
                        if (NonDefaultScalar(it, out string shown))
                            Refuse($"{(kind.Length == 0 ? loc : kind + " " + loc)}: field "
                                + $"'{Strip(n)}'{shown} is set, and no schema "
                                + "field binds it — a decompile would lose it, so this run refuses instead");
                    }
                }
            }

            private static string Strip(string n) => n.StartsWith("m_") ? n.Substring(2) : n;

            // True when a scalar leaf differs from type-zero (false / 0 / "" / enum-index-0 / null); `shown` is a
            // value hint for the refusal. Other property types return false, left to the recursive decoders.
            // Assumes type-zero is the real default of every swept field. A future SDK field with a non-zero
            // default (a float defaulting to 1) would refuse the compiler's own freshly emitted output everywhere;
            // add it to the type's *Aware set if consumed or inert, or compare against a freshly constructed
            // instance.
            private static bool NonDefaultScalar(SerializedProperty p, out string shown)
            {
                shown = "";
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Boolean: return p.boolValue;
                    case SerializedPropertyType.Integer:
                        if (p.intValue != 0) { shown = $" ({p.intValue})"; return true; } return false;
                    case SerializedPropertyType.Float:
                        if (Mathf.Abs(p.floatValue) > 1e-6f) { shown = $" ({p.floatValue})"; return true; } return false;
                    case SerializedPropertyType.String:
                        if (!string.IsNullOrEmpty(p.stringValue)) { shown = $" ('{p.stringValue}')"; return true; } return false;
                    case SerializedPropertyType.Enum:
                        if (p.enumValueIndex != 0) { shown = $" ({p.enumValueIndex})"; return true; } return false;
                    case SerializedPropertyType.ObjectReference:
                        return p.objectReferenceValue != null || p.objectReferenceInstanceIDValue != 0;
                    default:
                        return false;
                }
            }

            // A null motion slot that still carries a serialized object reference (instanceID != 0) is a
            // dangling ref to a since-deleted asset — distinct from a clean-empty state (instanceID 0).
            private static bool HasDanglingMotion(AnimatorState ast)
            {
                using (var so = new SerializedObject(ast))
                {
                    var mp = so.FindProperty("m_Motion");
                    return mp != null && mp.objectReferenceInstanceIDValue != 0;
                }
            }

            // The state a rebuild's auto-population lands on (see foreignIsAutoPopulation in DecodeMachine). Mirrors
            // EmitMachine's order, so change one and this must follow.
            private static AnimatorState FirstEmittedState(AnimatorStateMachine sm)
            {
                foreach (var cs in sm.states)
                    if (cs.state != null) return cs.state;
                foreach (var child in sm.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    var s = FirstEmittedState(child.stateMachine);
                    if (s != null) return s;
                }
                return null;
            }

            // The default-state twin of HasDanglingMotion. `sm.defaultState == null` is Unity's overloaded null,
            // true both for an unset field and for a PPtr to a destroyed state, so the caller's null check alone
            // cannot tell them apart, and a broken default would decode to no key and rebuild booting whichever
            // state was added first.
            private static bool HasDanglingDefaultState(AnimatorStateMachine sm)
            {
                using (var so = new SerializedObject(sm))
                {
                    var dp = so.FindProperty("m_DefaultState");
                    return dp != null && dp.objectReferenceInstanceIDValue != 0;
                }
            }

            // ----- transitions -----

            private Transition DecodeStateTransition(AnimatorStateTransition t, AnimatorStateMachine srcSm, string loc)
            {
                var tr = new Transition();
                SetTarget(tr, t, srcSm, loc);
                tr.Mute = t.mute;
                tr.Solo = t.solo;
                tr.Name = MeaningfulName(t.name, "");

                tr.When = DecodeConditions(t.conditions, loc);
                tr.ExitTime = t.hasExitTime ? t.exitTime : (float?)null;
                tr.Offset = t.offset != 0f ? t.offset : (float?)null;
                tr.Duration = t.duration != 0f ? t.duration : (float?)null;
                tr.FixedDuration = t.hasFixedDuration ? (bool?)null : false;
                tr.Interruption = t.interruptionSource == TransitionInterruptionSource.None
                    ? (TransitionInterruption?)null : MapInterruption(t.interruptionSource, loc);
                tr.OrderedInterruption = t.orderedInterruption ? (bool?)null : false;
                CompletenessSweep(t, StateTransitionAware, "transition from", loc);
                return tr;
            }

            private Transition DecodeEntryTransition(AnimatorTransition t, AnimatorStateMachine srcSm)
            {
                var tr = new Transition();
                string loc = "Entry in " + MachineLabel(srcSm);
                // The read-side mirror of the parser's entry mute/solo refusal.
                if (t.mute || t.solo)
                    Refuse($"transition from {loc}: entry transition carries mute/solo, which the entry ladder cannot express");
                if (!string.IsNullOrEmpty(t.name))
                    _result.Notes.Add($"transition from {loc}: entry rung's cosmetic name '{t.name}' is not carried by the schema and will not survive a recompile");
                SetTarget(tr, t, srcSm, loc);
                tr.When = DecodeConditions(t.conditions, loc);
                CompletenessSweep(t, EntryTransitionAware, "transition from", loc);
                return tr;
            }

            // A sub-machine's outgoing (on-Exit) transition, fired when it reaches Exit. Same AnimatorTransition
            // type as an entry rung, so the same fields and mute/solo refusal apply; `parentSm` is both the machine
            // the transition hangs off and the scope its target resolves in.
            private Transition DecodeSubMachineExitTransition(AnimatorTransition t, AnimatorStateMachine parentSm,
                                                              AnimatorStateMachine childSm)
            {
                var tr = new Transition();
                string loc = "onExit of sub-machine '" + childSm.name + "' in " + MachineLabel(parentSm);
                if (t.mute || t.solo)
                    Refuse($"transition from {loc}: carries mute/solo, which an onExit list cannot express");
                // The entry ladder tolerates a cosmetic name. onExit refuses one because no surveyed controller
                // carries a name on this rung, so a silent drop would be new loss. Widen to a decoded `name:` if a
                // real controller turns out to carry one.
                if (!string.IsNullOrEmpty(t.name))
                    Refuse($"transition from {loc}: carries the name '{t.name}', which an onExit list does not yet bind");
                SetTarget(tr, t, parentSm, loc);
                tr.When = DecodeConditions(t.conditions, loc);
                CompletenessSweep(t, EntryTransitionAware, "transition from", loc);
                return tr;
            }

            private void SetTarget(Transition tr, AnimatorTransitionBase t, AnimatorStateMachine srcSm, string loc)
            {
                // isExit and a destination are mutually exclusive in the Inspector, but both setters are public, so
                // a script or hand-edited YAML can set them together; Unity's precedence is not guessed at here.
                if (t.isExit && (t.destinationState != null || t.destinationStateMachine != null))
                {
                    Refuse($"transition from {loc}: carries isExit AND a destination "
                        + $"('{(t.destinationState != null ? t.destinationState.name : t.destinationStateMachine.name)}') — "
                        + "contradictory, and only a script can author it; clear one of the two on the source asset");
                    tr.ToExit = true;
                    return;
                }
                if (t.isExit) { tr.ToExit = true; return; }
                if (t.destinationState != null) { tr.To = StateTargetName(t.destinationState, srcSm, loc); return; }
                if (t.destinationStateMachine != null) { tr.To = SmTargetName(t.destinationStateMachine, srcSm, loc); return; }
                Refuse($"transition from {loc}: target resolves to no state/sub-machine/Exit");
            }

            // Every emitted target is per-segment escaped (a '/' in a name survives as '\/'), so ResolveName can
            // split on the unescaped separator.
            private string StateTargetName(AnimatorState dest, AnimatorStateMachine srcSm, string loc)
            {
                if (_stateOwner.TryGetValue(dest, out var owner) && owner == srcSm) return BareTarget(dest.name, loc); // same machine ⇒ bare
                if (_statePath.TryGetValue(dest, out var path))
                    return AddressPath.Split(path).Count == 1 ? "/" + path : path;
                Refuse($"transition from {loc}: target state '{dest.name}' not found in the layer");
                return dest.name;
            }

            // The parser reserves the exact token 'Exit' for the exit pseudo-target and strips quotes before the
            // keyword check, so escaping can't disambiguate a real state or sub-machine named 'Exit' addressed
            // bare. Multi-segment paths never collide.
            private string BareTarget(string name, string loc)
            {
                string enc = AddressPath.EscapeSegment(name);
                if (enc == "Exit")
                    Refuse($"transition from {loc}: target '{name}' encodes to the reserved token 'Exit' — a bare 'Exit' target reads as an exit transition on recompile");
                return enc;
            }

            private string SmTargetName(AnimatorStateMachine dest, AnimatorStateMachine srcSm, string loc)
            {
                if (_smParent.TryGetValue(dest, out var parent) && parent == srcSm) return BareTarget(dest.name, loc); // direct child ⇒ bare
                if (_smPath.TryGetValue(dest, out var path))
                    // The layer root itself (empty path) is a legal target, "re-enter the layer at its default",
                    // addressed by the bare '/' anchor.
                    return path.Length == 0 ? "/" : (AddressPath.Split(path).Count == 1 ? "/" + path : path);
                Refuse($"transition from {loc}: target sub-machine '{dest.name}' not found in the layer");
                return dest.name;
            }

            private List<Condition> DecodeConditions(AnimatorCondition[] conds, string loc)
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
                        default:
                            Refuse($"transition from {loc}: condition on '{c.parameter}' has an unknown mode '{c.mode}' — out of vocabulary");
                            continue;
                    }
                    // Render the condition as emit will and re-split it with the parser's grammar. A mismatch (a
                    // param with a trailing space collides with the single-space separator) cannot round-trip.
                    string bad = ConditionSelfCheckFailure(cond);
                    if (bad != null)
                        Refuse($"transition from {loc}: condition on '{cond.Param}' {bad}");
                    list.Add(cond);
                }
                return list;
            }

            private static string ConditionSelfCheckFailure(Condition c)
            {
                try
                {
                    var back = AnimatorSchemaYaml.ParseCondition(AnimatorSchemaEmit.RawCondString(c));
                    if (back.Param != c.Param || back.Op != c.Op || back.Value != c.Value)
                        return "does not survive the '<param> <op> <value>' grammar (its serialized form re-splits differently)";
                    return null;
                }
                catch (SchemaException e)
                {
                    return "does not survive the '<param> <op> <value>' grammar: " + e.Message;
                }
            }

            // ----- motions / blend trees -----

            // A name survives decode only when it differs from the positional default the compiler would apply
            // (ControllerEmit.AutoTreeName/AutoChildName). A line break in a name is caught by the serializer's
            // guard (AnimatorSchemaEmit.ScalarStr), not here.
            private static string MeaningfulName(string actual, string autoDefault)
            {
                if (string.IsNullOrEmpty(actual) || actual == autoDefault) return null;
                return actual;
            }

            private MotionRef DecodeMotion(Motion m, string loc, string expectedTreeName)
            {
                if (m == null) return null;
                if (m is AnimationClip clip)
                {
                    string p = AssetDatabase.GetAssetPath(clip);
                    // Testing the clip's path, not the controller's, keeps a standalone .anim a `ref:` even when
                    // the controller is unsaved.
                    if (string.IsNullOrEmpty(p) || p == _controllerPath)
                    {
                        RegisterInlineClip(clip, loc);
                        return new MotionRef { Clip = clip.name };
                    }
                    if (AssetDatabase.IsSubAsset(clip))
                    {
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long fileId);
                        return new MotionRef { RefGuid = new GuidRef { Guid = guid, FileID = fileId } };
                    }
                    return new MotionRef { RefPath = p };
                }
                if (m is BlendTree bt)
                {
                    // A multi-parent tree becomes one `trees:` entry the reference sites name.
                    if (_sharedName != null && _sharedName.TryGetValue(bt, out var key))
                    {
                        if (!_sharedDone.ContainsKey(key))
                        {
                            // Registered before decoding the body, so first-reference ordering survives. No cycle
                            // guard is needed: Unity's importer breaks a BlendTree cycle on load, nulling the
                            // offending child and logging "BlendTree cycle detected" (measured, including with the
                            // cycle written into the asset text; UnityItselfRefusesToBuildABlendTreeCycle pins it).
                            _sharedDone[key] = null;
                            var spec = DecodeTree(bt, loc, bt.name);
                            // Passing bt.name as the expected name makes DecodeTree null Name. BlendTreeSpec.Name
                            // holds the map key for a `trees:` entry, and EmitTrees suppresses the `name:` line
                            // (BindTrees refuses one in the body).
                            spec.Name = key;
                            _sharedDone[key] = spec;
                        }
                        return new MotionRef { Shared = key };
                    }
                    return new MotionRef { Tree = DecodeTree(bt, loc, expectedTreeName) };
                }

                Refuse($"motion in {loc}: unsupported Motion type '{m.GetType().Name}'");
                return null;
            }

            private BlendTreeSpec DecodeTree(BlendTree bt, string loc, string expectedName)
            {
                var spec = new BlendTreeSpec { Kind = MapTreeKind(bt.blendType, loc) };
                spec.Name = MeaningfulName(bt.name, expectedName);
                bool direct = spec.Kind == TreeKind.Direct;
                bool twoD = Is2D(bt.blendType);
                if (!direct)
                {
                    spec.Param = bt.blendParameter;
                    if (twoD) spec.ParamY = bt.blendParameterY;
                }
                using (var btSo = new SerializedObject(bt))
                {
                    // A Direct tree's "Normalized Blend Values" toggle (sum-to-1 vs raw additive) changes the pose
                    // and has no public property, so read the serialized field.
                    if (direct)
                        spec.Normalized = btSo.FindProperty("m_NormalizedBlendValues").boolValue;
                    var childs = btSo.FindProperty("m_Childs");
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
                        if (ch.motion != null) tc.Motion = DecodeMotion(ch.motion, childLoc, ControllerEmit.AutoChildName(bt.name, i));
                        else if (ChildHasDanglingMotion(childs, i))
                        {
                            // Decoded like a state's dangling motion, keeping the `unresolved` marker
                            // ControllerEmit.BuildMotion preserves.
                            string guid = NextDanglingGuid(bt);
                            tc.Motion = new MotionRef { RefGuid = new GuidRef { Guid = guid, Unresolved = true } };
                            _result.UnresolvedGuids.Add(guid);
                        }

                        if (direct) tc.DirectWeight = ch.directBlendParameter;
                        else if (twoD) { tc.PosX = ch.position.x; tc.PosY = ch.position.y; }
                        else tc.Threshold = ch.threshold;
                        spec.Children.Add(tc);
                    }
                }
                CompletenessSweep(bt, BlendTreeAware, "blend tree", loc);
                return spec;
            }

            private static bool ChildHasDanglingMotion(SerializedProperty childs, int i)
            {
                if (childs == null || i >= childs.arraySize) return false;
                var mp = childs.GetArrayElementAtIndex(i).FindPropertyRelative("m_Motion");
                return mp != null && mp.objectReferenceInstanceIDValue != 0;
            }

            // ----- inline clips (invert ControllerEmit.BuildClipContent) -----

            private void RegisterInlineClip(AnimationClip clip, string loc)
            {
                if (_clipObjs.TryGetValue(clip.name, out var existing))
                {
                    if (!ReferenceEquals(existing, clip))
                        Refuse(
                            $"inline clip name '{clip.name}' (referenced from {loc}) is shared by two DISTINCT embedded clips — " +
                            "the schema keys clips by name, which would collapse them");
                    return;
                }
                _clipObjs[clip.name] = clip;
                _clips[clip.name] = DecodeClip(clip);
            }

            private ClipSpec DecodeClip(AnimationClip clip)
            {
                var spec = new ClipSpec { Name = clip.name, Loop = AnimationUtility.GetAnimationClipSettings(clip).loopTime };
                var bindings = AnimationUtility.GetCurveBindings(clip);

                // Seconds-only carrier: one curve on the reserved carrier param, there only to give the clip a
                // length.
                if (bindings.Length == 1 && bindings[0].path == "" && bindings[0].type == typeof(Animator)
                    && bindings[0].propertyName == ReservedNames.CarrierParam)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, bindings[0]);
                    spec.Seconds = curve != null && curve.length > 0 ? curve.keys[curve.length - 1].time : clip.length;
                    return spec;
                }

                // PPtr curves (a material swap, a sprite flipbook) are a separate binding list the float-curve loop
                // below never sees, and no schema form carries them.
                var pptr = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var b in pptr)
                    Refuse($"inline clip '{clip.name}': object-reference (PPtr) curve on "
                        + $"'{b.path}/{(b.type == null ? "<missing script>" : b.type.Name)}.{b.propertyName}' is out of "
                        + "vocabulary — no schema form animates asset references");

                if (bindings.Length == 0)
                {
                    if (pptr.Length == 0)
                        Refuse($"inline clip '{clip.name}': has no animatable content (zero curve bindings) — malformed");
                    return spec;
                }

                // Two distinct bindings can reconstruct to the same schema target: an animator-parameter curve on a
                // parameter literally named "Prop/MeshRenderer.m_Enabled" and a real MeshRenderer.m_Enabled curve at
                // path "Prop". `set:`/`curves:` are keyed by that text, so the second would overwrite the first.
                var seenTargets = new Dictionary<string, EditorCurveBinding>();

                float maxConstEnd = 0f;
                foreach (var b in bindings)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    string target = ReconstructBindingTarget(b, clip.name);
                    if (target == null || curve == null || curve.length == 0) continue;
                    if (seenTargets.TryGetValue(target, out var prior))
                    {
                        Refuse($"inline clip '{clip.name}': two different bindings both address "
                            + $"'{target}' in the schema — {DescribeBinding(prior)} and {DescribeBinding(b)}. The "
                            + "clip grammar keys curves by that text, so one would silently overwrite the other; "
                            + "rename the declared parameter so it cannot also read as a 'path/Component.property' "
                            + "scene binding");
                        continue;
                    }
                    seenTargets[target] = b;
                    // A constant curve with flat tangents collapses to a Set. A linear or stepped one stays a Curve,
                    // because `set:` carries no tangent marker.
                    var cls = ClassifyTangents(curve);
                    if (cls == null)
                    {
                        Refuse($"inline clip '{clip.name}': curve '{target}' uses a tangent shape "
                            + "the schema can't round-trip (not flat/linear/stepped -- auto/clamped-auto are "
                            + "Unity-recomputed, free carries explicit tangent values the [t,v] form can't "
                            + "express); author it as a hand-owned .anim");
                        continue;
                    }
                    if (IsConstant(curve) && cls == CurveTangent.Flat)
                    {
                        spec.Sets[target] = curve.keys[0].value;
                        maxConstEnd = Mathf.Max(maxConstEnd, curve.keys[curve.length - 1].time);
                    }
                    else
                        spec.Curves.Add(new CurveSpec
                        {
                            Binding = target,
                            Keys = curve.keys.Select(k => new Keyframe2(k.time, k.value)).ToList(),
                            Tangents = cls.Value,
                        });
                }
                // A Set carries no keyframes, so recover a declared `seconds:` from the constant end when it exceeds
                // both the keyframed curves' last key and MinClipLength, where a plain Set with no seconds sits.
                float maxCurveEnd = 0f;
                foreach (var cs in spec.Curves)
                    foreach (var k in cs.Keys)
                        if (k.Time > maxCurveEnd) maxCurveEnd = k.Time;
                if (maxConstEnd > Mathf.Max(maxCurveEnd, MinClipLength) + 1e-4f)
                    spec.Seconds = maxConstEnd;
                return spec;
            }

            // Matches ControllerEmit's private MinClipLength: one frame at 60fps, the floor for a content-only
            // clip's derived length.
            private const float MinClipLength = 1f / 60f;

            private static bool IsConstant(AnimationCurve curve)
            {
                float v = curve.keys[0].value;
                for (int i = 1; i < curve.length; i++)
                    if (!Mathf.Approximately(curve.keys[i].value, v)) return false;
                return true;
            }

            // Mode tests run before the value test: a constant-valued linear curve has zero-slope tangents, so a
            // value-first flat test would mislabel it flat, and a Constant curve's tangents read as +/-inf. A
            // zero-tangent Auto/ClampedAuto/Free curve interpolates identically to the emitter's default-Free flat,
            // so it folds to flat. Non-zero custom/auto tangents, or tangents mixed across keys/sides, return null
            // and the caller refuses.
            private static CurveTangent? ClassifyTangents(AnimationCurve curve)
            {
                if (AllKeysMode(curve, AnimationUtility.TangentMode.Linear))   return CurveTangent.Linear;
                if (AllKeysMode(curve, AnimationUtility.TangentMode.Constant)) return CurveTangent.Stepped;
                if (AllKeysZeroTangent(curve))                                 return CurveTangent.Flat;
                return null;
            }

            private static bool AllKeysMode(AnimationCurve curve, AnimationUtility.TangentMode mode)
            {
                if (curve.length == 0) return false;
                for (int i = 0; i < curve.length; i++)
                    if (AnimationUtility.GetKeyLeftTangentMode(curve, i) != mode
                     || AnimationUtility.GetKeyRightTangentMode(curve, i) != mode)
                        return false;
                return true;
            }

            private static bool AllKeysZeroTangent(AnimationCurve curve)
            {
                if (curve.length == 0) return false;
                var keys = curve.keys;   // curve.keys copies the array on every access — read it once
                for (int i = 0; i < keys.Length; i++)
                {
                    // A weighted key is not emitter-flat even at zero slope: the weight sets the Bezier handle
                    // length independent of slope, and the [t,v] schema carries no weight.
                    if (keys[i].weightedMode != WeightedMode.None) return false;
                    if (!Mathf.Approximately(keys[i].inTangent, 0f) || !Mathf.Approximately(keys[i].outTangent, 0f))
                        return false;
                }
                return true;
            }

            // Names a binding by its unreconstructed parts: at a collision the two offenders are indistinguishable
            // once reconstructed.
            private static string DescribeBinding(EditorCurveBinding b)
                => b.type == typeof(Animator) && b.path.Length == 0
                    ? $"an animator-parameter curve on '{b.propertyName}'"
                    : $"a scene binding at path '{b.path}' on {(b.type == null ? "<unresolved type>" : b.type.Name)}.{b.propertyName}";

            // Inverts ControllerEmit.ResolveBinding. A path-"" Animator binding is a parameter write (the AAP form,
            // emitted bare) when the property names a declared controller parameter, otherwise an animator or
            // humanoid-muscle property curve ("RightHand.Index.1 Stretched", emitted "Animator.<property>").
            // Everything else is "path/Component.property", with an empty path dropping the leading slash. The emit
            // resolver is the oracle: a binding whose type's simple name would not resolve back to the same type
            // could never recompile, so it is refused, returning a null the caller skips.
            private string ReconstructBindingTarget(EditorCurveBinding b, string clipName)
            {
                if (b.type == typeof(Animator) && b.path == "")
                    return _animParamNames.Contains(b.propertyName) ? b.propertyName : "Animator." + b.propertyName;
                System.Type roundtrip = null;
                if (b.type != null)
                    try { roundtrip = ControllerEmit.ResolveComponentType(b.type.Name); }
                    catch (ControllerEmit.EmitException) { /* refused below, with the binding's location */ }
                // The null check comes first: with b.type null, `roundtrip != b.type` is false and the refusal
                // would be skipped into an NRE below.
                if (b.type == null || roundtrip != b.type)
                {
                    Refuse($"inline clip '{clipName}': binding "
                        + (b.path.Length == 0 ? "" : $"'{b.path}' ")
                        + $"on component type '{(b.type == null ? "<unresolved type>" : b.type.FullName)}' is out of "
                        + "vocabulary — the compiler could not re-emit it (schema §clips namespace allowlist)");
                    return null;
                }
                string body = b.type.Name + "." + b.propertyName;
                return b.path.Length == 0 ? body : b.path + "/" + body;
            }

            // ----- behaviours (invert the eight ControllerEmit.Populate* encoders) -----

            private void DecodeBehaviourInto(List<Behaviour> into, StateMachineBehaviour smb, string loc)
            {
                if (smb == null)
                {
                    Refuse($"behaviour on {loc}: missing script (null SMB)");
                    return;
                }
                switch (smb)
                {
                    case VRC_AvatarParameterDriver drv: into.Add(DecodeDriver(drv, loc)); break;
                    case VRC_AnimatorTrackingControl tc: into.Add(DecodeTracking(tc, loc)); break;
                    case VRC_PlayableLayerControl pl: into.Add(DecodePlayableLayer(pl, loc)); break;
                    case VRC_AnimatorLocomotionControl lc: into.Add(DecodeLocomotion(lc)); break;
                    case VRC_AnimatorTemporaryPoseSpace ps: into.Add(DecodePoseSpace(ps)); break;
                    case VRC_AnimatorPlayAudio pa: into.Add(DecodePlayAudio(pa, loc)); break;
                    case VRC_AnimatorLayerControl lyc: into.Add(DecodeLayerControl(lyc, loc)); break;
#if VRC_FT_SETS_PRESENT
                    case FtSets ft: into.Add(DecodeFaceTrackingSets(ft, loc)); break;
#endif
                    default:
                        Refuse($"behaviour on {loc}: unsupported SMB type '{smb.GetType().Name}' is out of vocabulary");
                        break;
                }
                SweepSmb(smb, loc);
            }

            private void SweepSmb(StateMachineBehaviour smb, string loc)
            {
                HashSet<string> aware;
                switch (smb)
                {
                    case VRC_AvatarParameterDriver _: aware = DriverAware; break;
                    case VRC_AnimatorTrackingControl _: aware = TrackingAware; break;
                    case VRC_PlayableLayerControl _: aware = PlayableLayerAware; break;
                    case VRC_AnimatorLocomotionControl _: aware = LocomotionAware; break;
                    case VRC_AnimatorTemporaryPoseSpace _: aware = PoseSpaceAware; break;
                    case VRC_AnimatorPlayAudio _: aware = PlayAudioAware; break;
                    case VRC_AnimatorLayerControl _: aware = LayerControlAware; break;
#if VRC_FT_SETS_PRESENT
                    case FtSets _: aware = FaceTrackingSetsAware; break;
#endif
                    default: return; // unsupported, already refused
                }
                CompletenessSweep(smb, aware, "behaviour on", loc);
            }

            private Behaviour DecodeDriver(VRC_AvatarParameterDriver drv, string loc)
            {
                var b = new Behaviour { Kind = "driver" };
                if (drv.localOnly) b.Fields[ControllerEmit.DriverKeys.LocalOnly] = true;
                DetectDriverOrderLoss(drv, loc);
                var set = new Dictionary<string, object>();
                var add = new Dictionary<string, object>();
                var copy = new Dictionary<string, object>();
                var random = new Dictionary<string, object>();
                // Order within a bucket matters because Copy reads a parameter (`Copy B←A; Copy C←B` differs if the
                // two swap), and the interleave check cannot see it. That order rides on Dictionary insertion order
                // through this map, the YAML writer, the YAML reader and ControllerEmit's iteration;
                // Walk_Driver_BucketOrder_Survives_A_Round_Trip pins it.
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
                                var roll = new Dictionary<string, object>
                                {
                                    { ControllerEmit.DriverKeys.Min, p.valueMin }, { ControllerEmit.DriverKeys.Max, p.valueMax }, { ControllerEmit.DriverKeys.Chance, p.chance },
                                };
                                // Written only when set. The SDK Inspector offers it on a Random-onto-Int and nowhere
                                // else.
                                if (p.preventRepeats) roll[ControllerEmit.DriverKeys.PreventRepeats] = true;
                                random[p.name] = roll;
                                break;
                            default:
                                Refuse($"behaviour on {loc}: driver operation on '{p.name}' has an unknown ChangeType '{p.type}' — out of vocabulary");
                                break;
                        }
                    }
                if (set.Count > 0) b.Fields[ControllerEmit.DriverKeys.Set] = set;
                if (add.Count > 0) b.Fields[ControllerEmit.DriverKeys.Add] = add;
                if (copy.Count > 0) b.Fields[ControllerEmit.DriverKeys.Copy] = copy;
                if (random.Count > 0) b.Fields[ControllerEmit.DriverKeys.Random] = random;
                return b;
            }

            // VRC_AvatarParameterDriver.parameters is an ordered list the runtime applies top-to-bottom, but the
            // schema regroups it into name-keyed set/add/copy/random buckets re-applied in that fixed order. Each
            // way that loses information is a located Refusal:
            //   - Interleaving: a change type after a later-bucket type (Set, Copy, Set). Re-emit hoists the Sets
            //     ahead of the Copy, changing what the Copy reads.
            //   - Repeat: the same (type, name) twice. The name-keyed bucket holds one entry.
            //
            // A repeat is refused for every type and never normalized, even one whose later write plainly
            // supersedes the earlier. `Copy A←B; Copy C←A; Copy A←D` is single-bucket, so the interleave check stays
            // quiet, yet the Dictionary overwrite keeps A's original insertion slot and hoists the later write ahead
            // of the read, so C ends up holding D's value instead of B's.
            private void DetectDriverOrderLoss(VRC_AvatarParameterDriver drv, string loc)
            {
                if (drv.parameters == null || drv.parameters.Count == 0) return;
                int prev = -1;
                bool interleaved = false;
                var seen = new Dictionary<(Driver.ChangeType, string), Driver.Parameter>();
                foreach (var p in drv.parameters)
                {
                    int bucket = DriverBucket(p.type);
                    if (bucket < prev) interleaved = true;
                    prev = bucket;
                    if (seen.TryGetValue((p.type, p.name), out var first))
                    {
                        // Say only what is observable: the operands are equal or they differ. Claim no runtime
                        // consequence, since `Add` accumulates (two `Add X 2` add 4), and no intent. An unknown
                        // ChangeType has no operand this decoder can read, so it claims neither.
                        string detail = !IsKnownChangeType(p.type)
                            ? "This decoder does not model that change type, so it cannot say whether the two "
                              + "entries differ; the repeat is refused on the shape alone."
                            : SameDriverOperand(first, p)
                            ? $"Both entries carry the same operand ({DriverOperand(p)}); the ordered pair still "
                              + "has no schema form, and whether the repeat is harmless depends on the change "
                              + "type — Add accumulates, Set and Copy do not."
                            : $"The two entries carry DIFFERENT operands ({DriverOperand(first)} then "
                              + $"{DriverOperand(p)}), which the single-entry form cannot express at all.";
                        Refuse(
                            $"behaviour on {loc}: driver repeats operation {p.type} '{p.name}' — the schema holds " +
                            "one entry per parameter, so a recompile would not reproduce this driver as authored. " +
                            detail + " Resolve it on the controller (hand-own the behaviour, or delete the " +
                            "redundant entry at the source) rather than in the document.");
                    }
                    else seen[(p.type, p.name)] = p;
                }
                if (interleaved)
                    Refuse(
                        $"behaviour on {loc}: driver operations interleave change-types (Set/Add/Copy/Random) — the schema re-applies them grouped by type, which would change their apply order");
            }

            // A driver Parameter is a union discriminated by `type`: the fields carrying its operand differ per
            // change type, and the unused ones hold stale or default junk that must not enter the comparison.
            // Exact equality, because the message reports these as "the same operand".
            private static bool SameDriverOperand(Driver.Parameter a, Driver.Parameter b)
            {
                switch (a.type)
                {
                    case Driver.ChangeType.Set:
                    case Driver.ChangeType.Add:
                        return a.value == b.value;
                    case Driver.ChangeType.Copy:
                        if (a.source != b.source || a.convertRange != b.convertRange) return false;
                        return !a.convertRange
                            || (a.sourceMin == b.sourceMin && a.sourceMax == b.sourceMax
                                && a.destMin == b.destMin && a.destMax == b.destMax);
                    case Driver.ChangeType.Random:
                        return a.valueMin == b.valueMin && a.valueMax == b.valueMax
                            && a.chance == b.chance && a.preventRepeats == b.preventRepeats;
                    default:
                        return false;   // unknown ChangeType — already refused separately; never claim "same"
                }
            }

            private static bool IsKnownChangeType(Driver.ChangeType t)
                => t == Driver.ChangeType.Set || t == Driver.ChangeType.Add
                   || t == Driver.ChangeType.Copy || t == Driver.ChangeType.Random;

            private static string DriverOperand(Driver.Parameter p)
            {
                switch (p.type)
                {
                    case Driver.ChangeType.Set:
                    case Driver.ChangeType.Add:
                        return p.value.ToString();
                    case Driver.ChangeType.Copy:
                        return p.convertRange
                            ? $"'{p.source}' remapped [{p.sourceMin},{p.sourceMax}]->[{p.destMin},{p.destMax}]"
                            : $"'{p.source}'";
                    case Driver.ChangeType.Random:
                        return $"min={p.valueMin} max={p.valueMax} chance={p.chance}"
                            + (p.preventRepeats ? " preventRepeats" : "");
                    default:
                        return "<unknown>";
                }
            }

            private static int DriverBucket(Driver.ChangeType t)
            {
                switch (t)
                {
                    case Driver.ChangeType.Set: return 0;
                    case Driver.ChangeType.Add: return 1;
                    case Driver.ChangeType.Copy: return 2;
                    case Driver.ChangeType.Random: return 3;
                    default: return 4; // unknown: a distinct bucket so the interleave check never conflates it with Random
                }
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

#if VRC_FT_SETS_PRESENT
            // Fields the element's type leaves inert (a Value element's sourceParameter/convertRange/ranges, a
            // Parameter element's weight, a Parameter element's ranges while convertRange is off) are tolerated, not
            // refused: the SDK inspector hides them for that type and keeps whatever they held (it writes weight 1
            // and ranges 0/1/0/1 on every new element, and leaves the old values behind when an author flips the
            // type), so a refusal would turn away ordinary inspector-made behaviours over fields the author cannot
            // see. They recompile at the inspector's new-element values; a tolerance note reports any that held
            // anything else.
            private Behaviour DecodeFaceTrackingSets(FtSets c, string loc)
            {
                var b = new Behaviour { Kind = FtKeys.Kind };
                var sets = new Dictionary<string, object>();
                var hidden = new List<string>();
                int count = c.blendSets == null ? 0 : c.blendSets.Count;
                if (count > FtKeys.MaxEntries)
                    Refuse($"behaviour on {loc}: faceTrackingSets carries {count} entries; the SDK processes only the first "
                        + $"{FtKeys.MaxEntries} and the compiler refuses more");
                for (int i = 0; i < count; i++)
                {
                    var e = c.blendSets[i];
                    string at = $"behaviour on {loc}: faceTrackingSets entry {i}";
                    if (e == null) { Refuse(at + " is null"); continue; }
                    string name = e.blendshapesSetName ?? "";
                    if (name.Trim().Length == 0) { Refuse(at + " has an empty Blendshapes Set name, which the compiler refuses"); continue; }
                    if (sets.ContainsKey(name))
                    {
                        Refuse($"{at} repeats Blendshapes Set '{name}' — the schema holds one entry per set name");
                        continue;
                    }
                    string ctx = $"{at} ('{name}')";
                    if (!InRange(e.lerpSpeed, 0f, 100f)) { Refuse($"{ctx}: lerpSpeed {e.lerpSpeed} is outside 0..100, which the compiler refuses"); continue; }
                    switch (e.type)
                    {
                        case FtSets.SourceType.Value:
                            if (!InRange(e.weight, 0f, 1f)) { Refuse($"{ctx}: weight {e.weight} is outside 0..1, which the compiler refuses"); continue; }
                            {
                                var held = new List<string>();
                                if (!string.IsNullOrEmpty(e.sourceParameter)) held.Add($"sourceParameter '{e.sourceParameter}'");
                                if (e.convertRange) held.Add("convertRange on");
                                if (!InspectorRanges(e)) held.Add(RangeText(e));
                                if (held.Count > 0) hidden.Add($"'{name}' (Value): {string.Join(", ", held)}");
                            }
                            if (e.lerpSpeed == 0f) sets[name] = e.weight;
                            else sets[name] = new Dictionary<string, object> { { FtKeys.Weight, e.weight }, { FtKeys.LerpSpeed, e.lerpSpeed } };
                            break;
                        case FtSets.SourceType.Parameter:
                            if (string.IsNullOrWhiteSpace(e.sourceParameter)) { Refuse(ctx + ": a Parameter entry with an empty source parameter, which the compiler refuses"); continue; }
                            if (e.convertRange && !(InRange(e.destMin, 0f, 1f) && InRange(e.destMax, 0f, 1f)))
                            {
                                Refuse($"{ctx}: destination range {e.destMin}..{e.destMax} leaves 0..1, which the compiler refuses");
                                continue;
                            }
                            if (e.convertRange && !(IsFinite(e.sourceMin) && IsFinite(e.sourceMax)))
                            {
                                Refuse($"{ctx}: source range {e.sourceMin}..{e.sourceMax} is not finite, which the compiler refuses");
                                continue;
                            }
                            {
                                var held = new List<string>();
                                if (e.weight != 1f) held.Add($"weight {e.weight.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                                if (!e.convertRange && !InspectorRanges(e)) held.Add(RangeText(e) + " with convertRange off");
                                if (held.Count > 0) hidden.Add($"'{name}' (Parameter): {string.Join(", ", held)}");
                            }
                            if (!e.convertRange && e.lerpSpeed == 0f) { sets[name] = e.sourceParameter; break; }
                            var m = new Dictionary<string, object> { { FtKeys.Source, e.sourceParameter } };
                            if (e.convertRange)
                            {
                                m[FtKeys.SourceMin] = e.sourceMin; m[FtKeys.SourceMax] = e.sourceMax;
                                m[FtKeys.DestMin] = e.destMin; m[FtKeys.DestMax] = e.destMax;
                            }
                            if (e.lerpSpeed != 0f) m[FtKeys.LerpSpeed] = e.lerpSpeed;
                            sets[name] = m;
                            break;
                        default:
                            Refuse($"{ctx}: unknown source type '{e.type}' — out of vocabulary");
                            break;
                    }
                }
                if (sets.Count > 0) b.Fields[FtKeys.Sets] = sets;
                if (hidden.Count > 0)
                    _result.Notes.Add($"behaviour on {loc}: faceTrackingSets fields hidden for an entry's source type held "
                        + $"non-default values, which recompile at the inspector's new-element values (weight 1, ranges 0/1/0/1, "
                        + $"no source): {string.Join("; ", hidden)}");
                return b;
            }

            private static bool InspectorRanges(FtSets.BlendSet e) =>
                e.sourceMin == 0f && e.sourceMax == 1f && e.destMin == 0f && e.destMax == 1f;

            private static string RangeText(FtSets.BlendSet e)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                return $"ranges {e.sourceMin.ToString(ci)}..{e.sourceMax.ToString(ci)} -> {e.destMin.ToString(ci)}..{e.destMax.ToString(ci)}";
            }

            private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

            // NaN fails both comparisons.
            private static bool InRange(float v, float min, float max) => v >= min && v <= max;
#endif

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
                var b = new Behaviour { Kind = "playAudio" };
                b.Fields[ControllerEmit.PlayAudioKeys.SourcePath] = c.SourcePath;
                b.Fields[ControllerEmit.PlayAudioKeys.PlaybackOrder] = Token(AudioOrderRev, c.PlaybackOrder, loc + " playAudio.playbackOrder");
                // Omit an empty parameter: `parameter: <null>` would make CompileController refuse the re-compile.
                if (!string.IsNullOrEmpty(c.ParameterName))
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
                    var paths = new List<object>();
                    foreach (var clip in c.Clips)
                    {
                        if (clip == null)
                        {
                            Refuse($"behaviour on {loc}: playAudio has a null clip entry (a since-deleted AudioClip)");
                            continue;
                        }
                        paths.Add(AssetDatabase.GetAssetPath(clip));
                    }
                    b.Fields[ControllerEmit.PlayAudioKeys.Clips] = paths;
                }
                return b;
            }

            // ----- shared blend trees: which BlendTrees have more than one parent slot -----

            // Sharing is a property of the live graph, so this counts parent edges over the reachable set the
            // decoder walks. Do not copy CountOrphans below; each of its behaviours would silently break the
            // round-trip here:
            //
            //  - It dedupes visits (`!reachable.Add(m)`). Here only the recursion is gated, or every tree counts 1
            //    and nothing is ever shared.
            //  - It bails on an unsaved controller, which here would send every in-memory controller down the
            //    inline path, so the tests would pass with the feature inert.
            //  - It sweeps LoadAllAssetsAtPath. An orphan tree referencing a live one would push that tree to 2
            //    parents; the recompile drops orphans, so the next decompile would see 1 and inline.
            //
            // A tree reached twice through one parent (the same motion in two child slots) counts 2, which is
            // sharing.
            private Dictionary<BlendTree, string> CountTreeParents()
            {
                var parents = new Dictionary<BlendTree, int>();
                var descended = new HashSet<BlendTree>();
                var order = new List<BlendTree>();

                void Visit(Motion m)
                {
                    if (!(m is BlendTree bt)) return;
                    if (!parents.ContainsKey(bt)) { parents[bt] = 0; order.Add(bt); }
                    parents[bt]++;
                    if (!descended.Add(bt)) return;   // gate the RECURSION only — the edge above is always counted
                    foreach (var ch in bt.children) Visit(ch.motion);
                }
                void VisitSm(AnimatorStateMachine sm, HashSet<AnimatorStateMachine> seen)
                {
                    if (sm == null || !seen.Add(sm)) return;
                    foreach (var cs in sm.states) if (cs.state != null) Visit(cs.state.motion);
                    foreach (var child in sm.stateMachines) VisitSm(child.stateMachine, seen);
                }
                var seenSm = new HashSet<AnimatorStateMachine>();
                foreach (var layer in _controller.layers)
                    if (layer.syncedLayerIndex < 0) VisitSm(layer.stateMachine, seenSm);

                // Name collisions and unnamed trees inline rather than refuse: a refusal's remedy, renaming, would
                // mean writing to a vendor asset.
                var byName = new Dictionary<string, int>();
                foreach (var bt in order)
                    if (parents[bt] >= 2 && !string.IsNullOrEmpty(bt.name))
                        byName[bt.name] = byName.TryGetValue(bt.name, out var n) ? n + 1 : 1;

                var shared = new Dictionary<BlendTree, string>();
                foreach (var bt in order)
                {
                    if (parents[bt] < 2 || string.IsNullOrEmpty(bt.name)) continue;
                    if (byName[bt.name] > 1)
                    {
                        _result.Notes.Add($"blend tree '{bt.name}' has {parents[bt]} parents but shares its name "
                            + "with another shared tree — inlined per parent rather than shared.");
                        continue;
                    }
                    shared[bt] = bt.name;
                }
                return shared;
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
                    if (m == null || !reachable.Add(m)) return;
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

            // Both labels are layer-qualified because Refusals and Notes are one controller-wide list and same-named
            // states across layers are ordinary: every SDK template ships an "Idle".
            private string StateLabel(AnimatorState st)
            {
                string layer = _currentLayer == null ? "" : "layer " + _currentLayer + " ";
                return layer + "state '" + (st != null ? st.name : "(null)") + "'";
            }

            private string MachineLabel(AnimatorStateMachine sm)
            {
                string layer = _currentLayer == null ? "" : "layer " + _currentLayer + " ";
                if (_smPath != null && _smPath.TryGetValue(sm, out var p))
                    return layer + "machine '" + (p.Length == 0 ? "(root)" : p) + "'";
                return layer + "machine '" + (sm != null ? sm.name : "(null)") + "'";
            }

            private string Token<TEnum>(Dictionary<TEnum, string> rev, TEnum v, string loc)
            {
                if (rev.TryGetValue(v, out var s)) return s;
                Refuse($"{loc}: SDK value '{v}' has no schema token (out of vocabulary)");
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
                        Refuse($"{loc}: blend-tree type '{t}' is out of vocabulary");
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
                        Refuse($"{loc}: transition interruption source '{s}' is out of vocabulary");
                        return TransitionInterruption.None;
                }
            }
        }

        // ----- Unity-enum -> schema-token reverse maps -----

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

        // ----- dangling-motion GUID recovery (mirror of the ReportController/ControllerRules YAML parse) -----

        // Map each dangling motion guid to the local fileID of the serialized object that owns it, the same fileID
        // AssetDatabase.TryGetGUIDAndLocalFileIdentifier gives at walk time. A .controller text serializes each
        // object as a block headed "--- !u!<class> &<fileID>"; a State block holds one m_Motion, a BlendTree
        // block holds one per m_Childs entry, in child order.
        private static Dictionary<long, Queue<string>> RecoverDanglingMotionGuids(AnimatorController controller)
        {
            var map = new Dictionary<long, Queue<string>>();
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return map;
            try
            {
                var text = File.ReadAllText(path);
                var heads = Regex.Matches(text, @"^--- !u!\d+ &(-?\d+)", RegexOptions.Multiline);
                for (int i = 0; i < heads.Count; i++)
                {
                    long fileId = long.Parse(heads[i].Groups[1].Value);
                    int start = heads[i].Index;
                    int end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
                    string block = text.Substring(start, end - start);
                    foreach (Match m in Regex.Matches(block, @"m_Motion:\s*\{fileID:\s*-?\d+,\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*\d+\}"))
                    {
                        var g = m.Groups[1].Value;
                        // Not a path test: a deleted asset's GUID keeps resolving to its old path, so the path
                        // test would skip the ordinary dangling case and the slot would degrade to "unknown".
                        if (Ryan6Vrc.AgentTools.Editor.RunLogFormat.AssetGuidResolves(g)) continue; // still resolves — not dangling
                        if (!map.TryGetValue(fileId, out var q)) { q = new Queue<string>(); map[fileId] = q; }
                        q.Enqueue(g);
                    }
                }
            }
            catch { /* binary-serialized or unreadable — no guids recoverable */ }
            return map;
        }
    }
}
