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
    /// mixed Write-Defaults hoisted to a modal layer policy + minority per-state overrides, and an
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

        // stripLayout (opt-in, default off) suppresses ALL per-machine layout capture — the own-a-vendor-
        // controller path, where the vendor's node arrangement is YAML noise ahead of a heavy rewrite. Default
        // stays capture-on (byte-identical to before the flag).
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

            // Decoded inline clips, keyed by name so a clip shared by several states is emitted once. The
            // parallel _clipObjs holds the source AnimationClip per name so a DISTINCT clip re-using a name
            // (which the name-keyed schema would silently collapse) is refused, not deduped as if shared.
            private readonly Dictionary<string, ClipSpec> _clips = new Dictionary<string, ClipSpec>();
            private readonly Dictionary<string, AnimationClip> _clipObjs = new Dictionary<string, AnimationClip>();

            // Per-layer addressing maps, rebuilt for each layer (state names recur across machines, so these
            // are never layer-global lookups keyed by bare name — they are object-keyed).
            private Dictionary<AnimatorState, AnimatorStateMachine> _stateOwner;
            private Dictionary<AnimatorState, string> _statePath;
            private Dictionary<AnimatorStateMachine, string> _smPath;
            private Dictionary<AnimatorStateMachine, AnimatorStateMachine> _smParent;

            // Dangling motion GUIDs recovered from the controller YAML (assets that no longer resolve),
            // keyed by the OWNING serialized object's local fileID so each null motion slot recovers its OWN
            // guid. A shared FIFO mis-attributes: it dedups by unique guid (several slots → one entry) and
            // drains in walk order, which need not match the YAML fill order.
            private readonly Dictionary<long, Queue<string>> _danglingByOwner;

            public WalkContext(AnimatorController controller, bool stripLayout)
            {
                _controller = controller;
                _stripLayout = stripLayout;
                _controllerPath = AssetDatabase.GetAssetPath(controller);
                _danglingByOwner = RecoverDanglingMotionGuids(controller);
            }

            // The next dangling guid recorded against this owning object (a State or BlendTree), in the
            // object's own serialization order. "unknown" when the controller isn't a saved asset or the
            // owner carries no recovered dangler.
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
                    WriteDefaults = null, // decoded per-state; never hoisted here
                };
                if (layer.avatarMask != null) model.Mask = AssetDatabase.GetAssetPath(layer.avatarMask);

                // An IK-pass layer runs a humanoid IK solve pass the schema has no vocabulary for; decoding
                // it silently would drop the pass on recompile. Refuse (named + located).
                if (layer.iKPass)
                    _result.Refusals.Add($"layer '{layer.name}': IK pass (iKPass) is out of vocabulary");

                if (layer.stateMachine == null)
                {
                    // A malformed controller: a layer with no state machine has no reachable graph to decode.
                    _result.Refusals.Add($"layer '{layer.name}': has no state machine (null root) — malformed controller");
                    return model; // empty Root, WriteDefaults left null (no states to derive a policy from)
                }
                BuildAddressMaps(layer.stateMachine);
                model.Root = DecodeMachine(layer.stateMachine, isRoot: true);
                HoistWriteDefaults(model);
                return model;
            }

            // Derive the layer's Write-Defaults policy as the MODAL per-state WD value across ALL of the layer's
            // states (root + every sub-machine), set it on the Layer, and clear the now-redundant per-state
            // override on every majority state — leaving an explicit State.WriteDefaults only on the minority.
            // Re-emit (s.WriteDefaults ?? layer.WriteDefaults ?? Defaults) then reproduces the SAME per-state WD
            // mix. DETERMINISTIC: on a tie prefer true (trueCount >= falseCount). A uniform-WD layer hoists to a
            // single policy with zero overrides — so it round-trips unchanged.
            private void HoistWriteDefaults(Layer model)
            {
                var states = new List<State>();
                model.Root.CollectStates(states);
                if (states.Count == 0) return; // nothing to derive from; leave WriteDefaults null

                int trueCount = 0, falseCount = 0;
                foreach (var s in states) { if (s.WriteDefaults == true) trueCount++; else falseCount++; }
                bool policy = trueCount >= falseCount; // tie-break: prefer true
                model.WriteDefaults = policy;
                foreach (var s in states)
                    if (s.WriteDefaults == policy) s.WriteDefaults = null; // majority inherits the layer policy

                // A genuinely mixed layer (both WD values present) had its per-state split normalized to a single
                // modal policy + minority overrides — an applied import tolerance. Record it in _notes.tolerances,
                // mirroring the timeParameterActive note (a uniform-WD layer applied no tolerance, so no note).
                if (trueCount > 0 && falseCount > 0)
                    _result.Notes.Add($"layer '{model.Name}': mixed Write Defaults normalized to modal policy (writeDefaults: {(policy ? "true" : "false")})");
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

            private StateMachine DecodeMachine(AnimatorStateMachine sm, bool isRoot)
            {
                var model = new StateMachine();

                DetectSiblingNameCollisions(sm);
                foreach (var cs in sm.states)
                    if (cs.state != null) model.States.Add(DecodeState(cs.state, sm));

                foreach (var child in sm.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    // A sub-machine can carry OUTGOING transitions (fired when it reaches Exit) that the schema
                    // has no "from sub-machine" vocabulary for. CountOrphans walks them (so they leave no orphan
                    // signal), but the emitted YAML would drop them — refuse (named + located) instead.
                    var smt = sm.GetStateMachineTransitions(child.stateMachine);
                    if (smt != null && smt.Length > 0)
                        _result.Refusals.Add($"state-machine '{PathLabel(sm)}': sub-machine '{child.stateMachine.name}' has {smt.Length} outgoing state-machine transition(s) (on Exit) — 'from sub-machine' transitions are out of vocabulary");
                    model.Machines.Add(new SubMachine { Name = child.stateMachine.name, Machine = DecodeMachine(child.stateMachine, isRoot: false) });
                }

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
                    // The trailing unconditional entry is the sub-machine default ONLY when its target is a
                    // DIRECT child (the compiler only ever emits that shape). A target deeper in the tree is a
                    // genuine cross-machine entry rung, not a default — leaving it in the ladder decodes it with
                    // a proper slash-qualified address instead of mislabeling it as a bare (direct-child) default
                    // that would fail to recompile.
                    if ((last.conditions == null || last.conditions.Length == 0) && last.destinationStateMachine != null && !last.isExit
                        && _smParent.TryGetValue(last.destinationStateMachine, out var lastParent) && lastParent == sm)
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

                model.Layout = _stripLayout ? null : CaptureLayout(sm, isRoot);
                return model;
            }

            // Positions live in the PARENT's child structs (states[i].position / stateMachines[i].position),
            // not AnimatorState.m_Position (which emit never writes). Special-node positions are the machine's
            // own properties. Returns null when every node sits at its tool-owned grid/constant default, so a
            // never-arranged machine stays layout-free.
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

            // The omit-decision compares special-node positions (entry/any/exit, and parent for sub-machines) against
            // the tool-owned constants, so a never-arranged machine stays layout-free. This relies on opening a
            // controller in Unity's Animator window NOT perturbing those positions off the constants — verified: an
            // opened-but-unedited controller neither dirties nor moves them (Unity serializes node positions to persist
            // them). If that ever regresses, the fallback is to drop the three special-node comparisons below
            // (grid-on-states/subs only); CaptureLayout still records the special positions whenever a block is emitted
            // for state/sub reasons.
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

            // Two flavours of sibling name collision, each a located Refusal (never a silent collapse):
            //   (a) EXACT raw duplicates — two states, or two sub-machines, with the identical name. They
            //       serialize as duplicate YAML keys the parser refuses on re-parse, so a decompile that
            //       returned OK here would hand back a non-round-trippable document.
            //   (b) whitespace collisions — names equal once trimmed but differing raw (e.g. "S" vs "S ") —
            //       a legibility hazard under name-keyed addressing, refused even though quoting could carry
            //       them, because two near-identical sibling names are almost always a mistake.
            private void DetectSiblingNameCollisions(AnimatorStateMachine sm)
            {
                DetectExactDuplicates(sm.states.Where(x => x.state != null).Select(x => x.state.name), "state", sm);
                DetectExactDuplicates(sm.stateMachines.Where(x => x.stateMachine != null).Select(x => x.stateMachine.name), "sub-machine", sm);
                DetectCrossKindCollisions(sm);
                DetectWhitespaceCollisions(sm);
            }

            // A direct state and a direct sub-machine may share a name (separate Unity collections). A bare
            // target or `default:` addresses by name, and ResolveName resolves states before sub-machines — so
            // a transition to the sub-machine would silently rewire to the state on recompile. Refuse (located).
            private void DetectCrossKindCollisions(AnimatorStateMachine sm)
            {
                var stateNames = new HashSet<string>(sm.states.Where(x => x.state != null).Select(x => x.state.name));
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null && stateNames.Contains(child.stateMachine.name))
                        _result.Refusals.Add(
                            $"state-machine '{PathLabel(sm)}': a direct state and a direct sub-machine are both named " +
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
                        _result.Refusals.Add(
                            $"state-machine '{PathLabel(sm)}': {counts[n]} sibling {kind}s are named '{n}' — " +
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
                    string guid = NextDanglingGuid(ast);
                    st.Motion = new MotionRef { RefGuid = new GuidRef { Guid = guid, Unresolved = true } };
                    _result.UnresolvedGuids.Add(guid);
                }
                // else: a genuine clean-empty state (motion: ~) — leave Motion null.

                if (ast.behaviours != null)
                    foreach (var b in ast.behaviours)
                        DecodeBehaviourInto(st.Behaviours, b, "state '" + ast.name + "'");

                foreach (var t in ast.transitions)
                    st.Transitions.Add(DecodeStateTransition(t, owner, "state '" + ast.name + "'"));

                CompletenessSweep(ast, StateAware, "state", "'" + ast.name + "'");
                return st;
            }

            // ----- decode completeness census (make the decoder witness its own coverage) -----
            //
            // The decoders above are a hand-maintained allowlist: a Unity field they don't explicitly bind (or
            // refuse) is SILENTLY dropped on import — the recurring silent-loss family. The census inverts that:
            // it sweeps every serialized property of a decoded object and REFUSES any non-default one the decode
            // path does not account for. So a behaviour-affecting field the decoder forgot — or one a future SDK
            // adds — fails loud until someone consciously classifies it (consume it, or list it below), instead
            // of dropping on omission. Complex/collection properties (arrays, structs, vectors) are left to the
            // recursive decoders; the sweep judges only the scalar leaf types it can compare to a default.
            //
            // Each `*Aware` set names the scalar properties the decode path consumes, refuses, or deliberately
            // ignores as editor-cosmetic. A property here is NOT swept; everything else non-default is refused.
            // NOT listed (⇒ swept ⇒ refused when set): m_CycleOffset, m_IKOnFeet, m_Tag (state); m_TransitionOffset
            // (transition) — the four this census catches.
            //
            // SCOPE (what the census actually covers — the rest stays a hand-allowlist): the TOP-LEVEL scalar
            // fields of AnimatorState, state/any/entry transitions, BlendTree, and the seven VRC SMB kinds.
            // NOT covered — array-element structs (ChildMotion / AnimatorCondition at depth > 0, guarded by the
            // recursive decoders' own explicit binding) and the layer / state-machine families (structs, not
            // UnityEngine.Objects, so not SerializedObject-sweepable — their few fields are consumed/refused
            // explicitly). Within scope the class is closed; outside it, the hand decoders remain the guard.

            private static readonly HashSet<string> UniversalIgnore = new HashSet<string>
            {
                "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
                "m_PrefabInternal", "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_Script",
                "m_EditorClassIdentifier", "m_Name",
            };

            private static readonly HashSet<string> StateAware = new HashSet<string>
            {
                "m_Speed", "m_Motion", "m_Transitions", "m_WriteDefaultValues", "m_Mirror",
                "m_SpeedParameterActive", "m_MirrorParameterActive", "m_CycleOffsetParameterActive",
                "m_TimeParameterActive", "m_SpeedParameter", "m_MirrorParameter", "m_CycleOffsetParameter",
                "m_TimeParameter", "m_StateMachineBehaviours", "m_Position",
            };

            private static readonly HashSet<string> StateTransitionAware = new HashSet<string>
            {
                "m_Conditions", "m_DstState", "m_DstStateMachine", "m_TransitionDuration", "m_ExitTime",
                "m_HasExitTime", "m_HasFixedDuration", "m_InterruptionSource", "m_OrderedInterruption",
                "m_Mute", "m_Solo", "m_CanTransitionToSelf", "m_IsExit",
            };

            private static readonly HashSet<string> EntryTransitionAware = new HashSet<string>
            {
                "m_Conditions", "m_DstState", "m_DstStateMachine", "m_IsExit", "m_Mute", "m_Solo",
            };

            private static readonly HashSet<string> BlendTreeAware = new HashSet<string>
            {
                "m_BlendType", "m_BlendParameter", "m_BlendParameterY", "m_Childs",
                // m_MinThreshold/m_MaxThreshold/m_UseAutomaticThresholds are inert for the round-trip (child
                // thresholds are read/emitted explicitly). m_NormalizedBlendValues IS consumed (Direct trees,
                // above) — kept here so the sweep doesn't double-refuse it.
                "m_MinThreshold", "m_MaxThreshold", "m_UseAutomaticThresholds", "m_NormalizedBlendValues",
            };

            // Per-SMB aware sets (VRC components are MonoBehaviours: public fields serialize under their own
            // names, no m_ prefix; the MonoBehaviour internals are covered by UniversalIgnore). Each lists the
            // fields its decoder consumes plus the editor-only debugString. A non-default field NOT here — one a
            // decoder forgot, or a future SDK adds — is refused by the sweep.
            private static readonly HashSet<string> DriverAware = new HashSet<string> { "parameters", "localOnly", "debugString" };
            private static readonly HashSet<string> TrackingAware = new HashSet<string>
            {
                "trackingHead", "trackingLeftHand", "trackingRightHand", "trackingHip", "trackingLeftFoot",
                "trackingRightFoot", "trackingLeftFingers", "trackingRightFingers", "trackingEyes", "trackingMouth", "debugString",
            };
            private static readonly HashSet<string> PlayableLayerAware = new HashSet<string> { "layer", "goalWeight", "blendDuration", "debugString" };
            private static readonly HashSet<string> LocomotionAware = new HashSet<string> { "disableLocomotion", "debugString" };
            private static readonly HashSet<string> PoseSpaceAware = new HashSet<string> { "enterPoseSpace", "fixedDelay", "delayTime", "debugString" };
            private static readonly HashSet<string> LayerControlAware = new HashSet<string> { "playable", "layer", "goalWeight", "blendDuration", "debugString" };
            private static readonly HashSet<string> PlayAudioAware = new HashSet<string>
            {
                "SourcePath", "PlaybackOrder", "ParameterName", "Volume", "VolumeApplySettings", "Pitch",
                "PitchApplySettings", "Loop", "LoopApplySettings", "Clips", "ClipsApplySettings", "DelayInSeconds",
                "PlayOnEnter", "StopOnEnter", "PlayOnExit", "StopOnExit", "debugString",
            };

            private void CompletenessSweep(Object o, HashSet<string> aware, string kind, string loc)
            {
                if (o == null) return;
                using (var so = new SerializedObject(o))
                {
                    var it = so.GetIterator();
                    for (bool ok = it.Next(true); ok; ok = it.Next(false)) // Next (not NextVisible) — hidden fields count too
                    {
                        if (it.depth != 0) continue; // top-level scalars only; collections are the decoders' job
                        string n = it.name;
                        if (UniversalIgnore.Contains(n) || aware.Contains(n)) continue;
                        if (NonDefaultScalar(it, out string shown))
                            _result.Refusals.Add($"{kind} {loc}: field '{Strip(n)}'{shown} is out of vocabulary — no schema field binds it (silently dropped)");
                    }
                }
            }

            private static string Strip(string n) => n.StartsWith("m_") ? n.Substring(2) : n;

            // True when a scalar leaf property differs from TYPE-ZERO (false / 0 / "" / enum-index-0 / null);
            // `shown` is a value hint for the refusal. Non-scalar / unknown types return false (left to the
            // recursive decoders) so the sweep never false-positives on an array, vector, or struct it does not
            // model. FORWARD-SAFETY: type-zero is the true default for every field currently swept. If a future
            // SDK adds a non-aware field whose real Unity default is NON-zero (a float defaulting to 1, an enum
            // whose default index ≠ 0), this would refuse the compiler's OWN freshly-emitted output — a global
            // fixpoint break (louder than a per-construct miss, so still fail-loud). Fix then: add it to the
            // type's *Aware set (if consumed/inert) or compare against a freshly-constructed instance's value.
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
                        return false; // vectors / generic structs / arrays — not the sweep's concern
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

            // ----- transitions -----

            private Transition DecodeStateTransition(AnimatorStateTransition t, AnimatorStateMachine srcSm, string loc)
            {
                var tr = new Transition();
                SetTarget(tr, t, srcSm, loc);
                tr.Mute = t.mute;
                tr.Solo = t.solo;

                tr.When = DecodeConditions(t.conditions, loc);
                tr.ExitTime = t.hasExitTime ? t.exitTime : (float?)null;
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
                string loc = "Entry in '" + PathLabel(srcSm) + "'";
                // The entry ladder cannot express mute/solo (the parser refuses them there, and the emit path
                // never reads them) — an entry transition carrying either would be a silent drop. Refuse it,
                // the read-side mirror of the parser's entry mute/solo refusal.
                if (t.mute || t.solo)
                    _result.Refusals.Add($"transition from {loc}: entry transition carries mute/solo, which the entry ladder cannot express");
                SetTarget(tr, t, srcSm, loc);
                tr.When = DecodeConditions(t.conditions, loc);
                CompletenessSweep(t, EntryTransitionAware, "transition from", loc);
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
                if (_stateOwner.TryGetValue(dest, out var owner) && owner == srcSm) return BareTarget(dest.name, loc); // same machine ⇒ bare
                if (_statePath.TryGetValue(dest, out var path))
                    return AddressPath.Split(path).Count == 1 ? "/" + path : path;
                _result.Refusals.Add($"transition from {loc}: target state '{dest.name}' not found in the layer");
                return dest.name;
            }

            // A bare (same-machine / direct-child) target is emitted as the escaped name. The parser reserves
            // the exact token 'Exit' for the exit pseudo-target, so a real state/sub-machine named 'Exit'
            // addressed bare would recompile as exit-to-nowhere — refuse it (escaping can't disambiguate: the
            // parser strips quotes before the keyword check). Multi-segment paths never collide (only exact 'Exit').
            private string BareTarget(string name, string loc)
            {
                string enc = AddressPath.EscapeSegment(name);
                if (enc == "Exit")
                    _result.Refusals.Add($"transition from {loc}: target '{name}' encodes to the reserved token 'Exit' — a bare 'Exit' target reads as an exit transition on recompile");
                return enc;
            }

            private string SmTargetName(AnimatorStateMachine dest, AnimatorStateMachine srcSm, string loc)
            {
                if (_smParent.TryGetValue(dest, out var parent) && parent == srcSm) return BareTarget(dest.name, loc); // direct child ⇒ bare
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
            private List<Condition> DecodeConditions(AnimatorCondition[] conds, string loc)
            {
                var list = new List<Condition>();
                if (conds == null) return list;
                foreach (var c in conds)
                {
                    // A condition serializes as the plain 3-token string '<param> <op> <value>' inside a flow
                    // list — a param carrying whitespace (breaks the token split) or a flow delimiter (breaks
                    // the list) cannot round-trip. Refuse it (named + located) rather than emit a broken scalar.
                    if (!string.IsNullOrEmpty(c.parameter) && ParamBreaksConditionGrammar(c.parameter))
                        _result.Refusals.Add($"transition from {loc}: condition parameter '{c.parameter}' contains whitespace or a flow delimiter (,[]{{}}) — it cannot be expressed in the '<param> <op> <value>' condition grammar");
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
                            // An unknown/future/corrupt mode — refuse rather than approximate as `Is true`
                            // (consistent with MapTreeKind / MapInterruption, which refuse their unknowns).
                            _result.Refusals.Add($"transition from {loc}: condition on '{c.parameter}' has an unknown mode '{c.mode}' — out of vocabulary");
                            continue;
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
                    // Inline only a clip with NO path of its own (an in-memory / embedded sub-asset) or one
                    // whose path IS the controller's. Testing the CLIP's path (not the controller's) keeps a
                    // standalone .anim a `ref:` even when the controller itself is unsaved.
                    if (string.IsNullOrEmpty(p) || p == _controllerPath)
                    {
                        // Embedded controller sub-asset ⇒ inline ClipSpec.
                        RegisterInlineClip(clip, loc);
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
                using (var btSo = new SerializedObject(bt))
                {
                    // Direct trees carry a behaviour-affecting "Normalized Blend Values" toggle (sum-to-1 vs
                    // raw additive) with no typed API — read it verbatim so it round-trips (else a vendor tree
                    // recompiles to the construction default, a silently different pose). No public property.
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
                        if (ch.motion != null) tc.Motion = DecodeMotion(ch.motion, childLoc);
                        else if (ChildHasDanglingMotion(childs, i))
                        {
                            // Owner is the blend tree itself: its m_Childs danglers drain in child order,
                            // matching this loop, so each child recovers its OWN guid.
                            string guid = NextDanglingGuid(bt);
                            tc.Motion = new MotionRef { RefGuid = new GuidRef { Guid = guid, Unresolved = true } };
                            _result.UnresolvedGuids.Add(guid);
                        }
                        // else: a genuine empty child (rare) — leave Motion null, never silently dropping a dangler.

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

            // ----- inline clips (invert ControllerEmit.BuildClip) -----

            private void RegisterInlineClip(AnimationClip clip, string loc)
            {
                if (_clipObjs.TryGetValue(clip.name, out var existing))
                {
                    // Same instance re-referenced by another state ⇒ correct dedup (emit it once). A DISTINCT
                    // clip carrying the same name ⇒ the name-keyed clips: map would collapse them (the second's
                    // curves lost) — refuse loudly (named + located) instead.
                    if (!ReferenceEquals(existing, clip))
                        _result.Refusals.Add(
                            $"inline clip name '{clip.name}' (referenced from {loc}) is shared by two DISTINCT embedded clips — " +
                            "the schema keys clips by name, which would collapse them");
                    return;
                }
                _clipObjs[clip.name] = clip;
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
                // MinClipLength. Recover the declared length from the constant end whenever it exceeds what the
                // keyframed curves already carry (their own last key) — so a MIXED set+curve clip whose sets run
                // past the last keyframe keeps its length, not only a curves-absent clip. Floor is MinClipLength
                // (a plain Set with no seconds sits there ⇒ leave Seconds null).
                float maxCurveEnd = 0f;
                foreach (var cs in spec.Curves)
                    foreach (var k in cs.Keys)
                        if (k.Time > maxCurveEnd) maxCurveEnd = k.Time;
                if (maxConstEnd > Mathf.Max(maxCurveEnd, MinClipLength) + 1e-4f)
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
                    case VRC_AvatarParameterDriver drv: into.Add(DecodeDriver(drv, loc)); break;
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
                SweepSmb(smb, loc);
            }

            // Census a known SMB against its per-kind aware set (unknown types are already refused above).
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
                    default: return; // null or unsupported (already refused)
                }
                CompletenessSweep(smb, aware, "behaviour on", loc);
            }

            private Behaviour DecodeDriver(VRC_AvatarParameterDriver drv, string loc)
            {
                // Field-name tokens are shared from ControllerEmit.DriverKeys so encode/decode cannot drift.
                var b = new Behaviour { Kind = "driver" };
                if (drv.localOnly) b.Fields[ControllerEmit.DriverKeys.LocalOnly] = true; // default false is left implicit
                DetectDriverOrderLoss(drv, loc);
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
                            default:
                                // An unknown/future ChangeType would be dropped from all four buckets — refuse it.
                                _result.Refusals.Add($"behaviour on {loc}: driver operation on '{p.name}' has an unknown ChangeType '{p.type}' — out of vocabulary");
                                break;
                        }
                    }
                if (set.Count > 0) b.Fields[ControllerEmit.DriverKeys.Set] = set;
                if (add.Count > 0) b.Fields[ControllerEmit.DriverKeys.Add] = add;
                if (copy.Count > 0) b.Fields[ControllerEmit.DriverKeys.Copy] = copy;
                if (random.Count > 0) b.Fields[ControllerEmit.DriverKeys.Random] = random;
                return b;
            }

            // VRC_AvatarParameterDriver.parameters is an ORDERED list the runtime applies top-to-bottom, but the
            // schema regroups it into name-keyed set/add/copy/random buckets re-applied in that fixed order. That
            // is faithful ONLY when the source list is already in bucket order with no repeated (type,name). Two
            // ways it loses information, each a located Refusal (never a silent reorder/collapse):
            //   - INTERLEAVING: a change-type appears after a later-bucket type (e.g. Set, Copy, Set). Re-emit
            //     hoists all Sets ahead of the Copy, changing what the Copy reads.
            //   - DUPLICATE: the same (type, name) appears twice. The name-keyed bucket keeps only the last.
            private void DetectDriverOrderLoss(VRC_AvatarParameterDriver drv, string loc)
            {
                if (drv.parameters == null || drv.parameters.Count == 0) return;
                int prev = -1;
                bool interleaved = false;
                var seen = new HashSet<(Driver.ChangeType, string)>();
                foreach (var p in drv.parameters)
                {
                    int bucket = DriverBucket(p.type);
                    if (bucket < prev) interleaved = true;
                    prev = bucket;
                    if (!seen.Add((p.type, p.name)))
                        _result.Refusals.Add(
                            $"behaviour on {loc}: driver repeats operation {p.type} '{p.name}' — the schema's name-keyed buckets keep only the last write");
                }
                if (interleaved)
                    _result.Refusals.Add(
                        $"behaviour on {loc}: driver operations interleave change-types (Set/Add/Copy/Random) — the schema re-applies them grouped by type, which would change their apply order");
            }

            private static int DriverBucket(Driver.ChangeType t)
            {
                switch (t)
                {
                    case Driver.ChangeType.Set: return 0;
                    case Driver.ChangeType.Add: return 1;
                    case Driver.ChangeType.Copy: return 2;
                    case Driver.ChangeType.Random: return 3;
                    default: return 4; // an unknown ChangeType — a distinct bucket so it never conflates with Random
                                       // for the interleave check (DecodeDriver refuses it outright regardless)
                }
            }

            // True when the param can't survive the '<param> <op> <value>' condition grammar (whitespace or a
            // flow delimiter); the refusal that consumes this is raised in DecodeConditions.
            private static bool ParamBreaksConditionGrammar(string param)
            {
                foreach (char c in param)
                    if (char.IsWhiteSpace(c) || ",[]{}".IndexOf(c) >= 0) return true;
                return false;
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

        // ----- dangling-motion GUID recovery (mirror of the ReportController/ControllerRules YAML parse) -----

        // Map each dangling motion guid to the local fileID of the serialized object that owns it (a State or a
        // BlendTree), in that object's own serialization order. A .controller text serializes each object as a
        // block headed "--- !u!<class> &<fileID>"; a State block holds one m_Motion, a BlendTree block holds one
        // per m_Childs entry (in child order). Keying by owner fileID lets each null motion slot recover its OWN
        // guid at walk time (AssetDatabase.TryGetGUIDAndLocalFileIdentifier gives the same fileID), independent
        // of how many slots dangle or whether walk order matches file order.
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
                        if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g))) continue; // still resolves — not dangling
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
