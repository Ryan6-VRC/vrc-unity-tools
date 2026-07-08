using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Ryan6Vrc.AgentTools.Editor;
using Driver = VRC.SDKBase.VRC_AvatarParameterDriver;
using TrackingType = VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType;
using PlayableLayer = VRC.SDKBase.VRC_PlayableLayerControl.BlendableLayer;
using LayerCtrlLayer = VRC.SDKBase.VRC_AnimatorLayerControl.BlendableLayer;
using AudioOrder = VRC.SDKBase.VRC_AnimatorPlayAudio.Order;
using AudioApply = VRC.SDKBase.VRC_AnimatorPlayAudio.ApplySettings;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Codegen core: turns a parsed <see cref="AnimDocument"/> into a real Unity
    /// <see cref="AnimatorController"/> with embedded clips + blend trees, plus a
    /// <see cref="VRCExpressionParameters"/> asset listing the controller's OWN declared params (every
    /// declared param except VRC built-ins and <c>scratch:</c> working params) so the merged FX stays
    /// legible — non-synced params appear with <c>networkSynced=false</c> (free, no sync-bit cost).
    ///
    /// PURE FUNCTION OF THE DOCUMENT. No random identity (AaC's random suffixes are deliberately
    /// rejected): state layout is a fixed grid keyed by document order, names come straight from the
    /// document, so recompiling the same document produces the same graph and Git sees no churn.
    ///
    /// PERSISTENCE BOUNDARY: several sub-asset APIs used here — <c>AnimatorState.AddStateMachineBehaviour</c>,
    /// embedding clips/trees as controller sub-assets — require the controller to be a persisted asset, not an
    /// in-memory object. So Build materialises the controller at <c>&lt;outDir&gt;/&lt;name&gt;.controller</c>
    /// and attaches everything there. The 2-arg overload defaults <c>outDir</c> to the scratch dir under
    /// Assets/Agent/Scratch/emit/; CompileController (the write-substrate door) passes the real output dir or
    /// a whatIf temp. Emission is GUID-STABLE + idempotent: an existing controller at the path is reset in
    /// place, not deleted + recreated. The load-bearing guarantee is topology: the returned controller,
    /// reloaded from disk, has the correct graph.
    /// </summary>
    public static class ControllerEmit
    {
        // A duration-only clip and a Sets-only clip both need a real (non-zero) length or Unity treats
        // the clip as zero-length. One frame at 60fps is the smallest honest floor.
        private const float MinClipLength = 1f / 60f;

        private const string ScratchRoot = "Assets/Agent/Scratch/emit";

        public sealed class EmitResult
        {
            public AnimatorController Controller;
            public Dictionary<string, AnimationClip> Clips = new Dictionary<string, AnimationClip>();
            public List<AnimationClip> ClipList = new List<AnimationClip>();
            public List<BlendTree> Trees = new List<BlendTree>();
            public VRCExpressionParameters Params; // null only when every declared param is excluded (built-in / scratch)
            // Motion refs that carried the `unresolved: true` marker and did NOT resolve — emitted as a null
            // motion (a clean-empty state) instead of a fail-loud throw. Each entry names the owning STATE and
            // the verbatim GUID so a compile advisory can preserve the round-trip note. A BARE broken ref (no
            // marker) is never recorded here — it still throws EmitException.
            public List<(string state, string guid)> UnresolvedRefs = new List<(string state, string guid)>();
        }

        // Fail-loud emission error, named so a coordinator/log points straight at the offending construct.
        public sealed class EmitException : Exception
        {
            public EmitException(string message) : base(message) { }
        }

        // Back-compat door: emit to the scratch dir, hashing SourcePath for provenance (Task 4 tests + any
        // caller that only wants a throwaway build). CompileController uses the 4-arg overload below.
        public static void Build(AnimDocument doc, out EmitResult result)
            => Build(doc, ScratchRoot, null, out result);

        /// <summary>Emit <paramref name="doc"/> into <paramref name="outDir"/> (an <c>Assets/…</c>-relative
        /// folder). <paramref name="sourceText"/>, when non-null, is the exact source-file text — its BYTES
        /// are hashed into the controller's provenance userData so a later compile can detect an out-of-band
        /// edit (Task 5 threads it; null falls back to hashing <see cref="AnimDocument.SourcePath"/>).
        /// IDEMPOTENT + GUID-STABLE: an existing controller at the target path is RESET IN PLACE (its
        /// sub-assets stripped and rebuilt) rather than deleted + recreated, so its GUID — and any external
        /// reference to it — survives a recompile.</summary>
        public static void Build(AnimDocument doc, string outDir, string sourceText, out EmitResult result)
        {
            if (doc == null) throw new EmitException("Build: document is null");
            if (string.IsNullOrEmpty(doc.ControllerName))
                throw new EmitException("Build: document has no controller name");
            if (string.IsNullOrEmpty(outDir)) throw new EmitException("Build: outDir is empty");

            var ctx = new BuildContext(doc, outDir, sourceText);
            result = ctx.Run();
        }

        // Per-build mutable state kept off the static surface so Build stays a pure entry point.
        private sealed class BuildContext
        {
            private readonly AnimDocument _doc;
            private readonly string _outDir;
            private readonly string _sourceText;   // exact source bytes for provenance; null ⇒ hash SourcePath
            private readonly EmitResult _result = new EmitResult();
            private readonly HashSet<string> _paramNames;
            private AnimatorController _controller;

            public BuildContext(AnimDocument doc, string outDir, string sourceText)
            {
                _doc = doc;
                _outDir = outDir;
                _sourceText = sourceText;
                // A clip that writes a bare parameter name (aap or not) binds it as an Animator float curve
                // — the mechanism VRChat uses to drive any Animator parameter from a clip (AAPs are just the
                // float-smoother case). The gate is "is this a declared Animator parameter", not the aap flag.
                _paramNames = new HashSet<string>(doc.Parameters.Select(p => p.Name));
            }

            public EmitResult Run()
            {
                string path = PathFor(_outDir, _doc.ControllerName);
                EnsureFolder(_outDir);

                // NOTE: StripSubAssets (below) empties any prior controller at `path` before the fallible emit
                // steps run, and several of them (transition targets, behaviour kinds, clip bindings) throw
                // AFTER the strip. That is safe here only because the CompileController door PROVES an owned
                // overwrite (a full emit+lint into a throwaway temp) before calling Build for real; direct
                // 2-arg callers target disposable scratch. Do not rely on Build alone to protect a prior asset.

                // GUID-STABLE idempotence: reuse the controller ASSET if one already lives at the path (its
                // GUID + any external reference survives), stripping its sub-assets so the rebuilt graph is a
                // pure function of the document. Only when nothing (or a non-controller) sits there do we
                // create fresh. A bare `new AnimatorController()` persists with ZERO layers — no auto "Base
                // Layer" to orphan (CreateAnimatorControllerAtPath would add one). We own every layer below.
                _controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (_controller != null)
                {
                    StripSubAssets(path);
                    _controller.name = _doc.ControllerName;
                }
                else
                {
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                        AssetDatabase.DeleteAsset(path); // a non-controller squatting the path
                    _controller = new AnimatorController { name = _doc.ControllerName };
                    AssetDatabase.CreateAsset(_controller, path);
                }

                EmitParameters();
                EmitClips();          // clips first: states/trees reference them by name
                EmitLayers();
                EmitVrcParameters();  // in-memory only; Task 5 persists

                EditorUtility.SetDirty(_controller);
                AssetDatabase.SaveAssets();

                StampProvenance(path);

                // Reload from disk so EmitResult holds the authoritative persisted objects (a reimport can
                // remap instances). This makes the returned graph exactly what round-trip verification sees.
                ReloadFromDisk(path);
                return _result;
            }

            // Reset an existing controller for in-place rebuild: detach its layers/parameters, then destroy
            // every animator sub-asset (+ embedded clips/trees) at the path. The top-level controller object
            // — and its GUID — is kept. Clearing the references BEFORE destroying avoids destroying an object
            // the controller still points at.
            private void StripSubAssets(string path)
            {
                _controller.layers = new AnimatorControllerLayer[0];
                _controller.parameters = new AnimatorControllerParameter[0];
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (o == null || o == _controller) continue;
                    if (o is AnimatorStateMachine || o is AnimatorState || o is BlendTree
                        || o is StateMachineBehaviour || o is AnimatorTransitionBase || o is AnimationClip)
                    {
                        AssetDatabase.RemoveObjectFromAsset(o);
                        UnityEngine.Object.DestroyImmediate(o);
                    }
                }
            }

            // ----- parameters -----

            private void EmitParameters()
            {
                foreach (var p in _doc.Parameters)
                {
                    var acp = new AnimatorControllerParameter { name = p.Name };
                    switch (p.Type)
                    {
                        case AnimParamType.Bool:
                            acp.type = AnimatorControllerParameterType.Bool;
                            acp.defaultBool = p.Default != 0f;
                            break;
                        case AnimParamType.Int:
                            acp.type = AnimatorControllerParameterType.Int;
                            acp.defaultInt = Mathf.RoundToInt(p.Default);
                            break;
                        default: // Float — AAP params are ordinary Floats; the aap flag only affects binding.
                            acp.type = AnimatorControllerParameterType.Float;
                            acp.defaultFloat = p.Default;
                            break;
                    }
                    _controller.AddParameter(acp);
                }
            }

            // ----- clips -----

            private void EmitClips()
            {
                foreach (var spec in _doc.Clips)
                {
                    var clip = BuildClip(spec);
                    clip.hideFlags = HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(clip, _controller);
                    _result.Clips[spec.Name] = clip;
                    _result.ClipList.Add(clip);
                }
            }

            private AnimationClip BuildClip(ClipSpec spec)
            {
                var clip = new AnimationClip { name = spec.Name, frameRate = 60f };
                bool hasContent = spec.Sets.Count > 0 || spec.Curves.Count > 0;

                if (!hasContent)
                {
                    // Seconds-only clip → inert carrier that ONLY gives the clip a genuine length. Bind a flat
                    // curve on a scratch ANIMATOR parameter (path="", typeof(Animator)) — NOT a non-existent
                    // GameObject path. An animator-property binding resolves against any avatar root (the root
                    // always carries an Animator), so AnimatorLint's broken-binding rule stays clean when the
                    // emitted controller is linted against a real avatar; a fake GO path would false-FAIL it.
                    if (!spec.Seconds.HasValue)
                        throw new EmitException(
                            $"clip '{spec.Name}' declares no set/curves and no seconds — nothing to emit (parser/validator should have caught this)");
                    EnsureCarrierParam();
                    var carrier = EditorCurveBinding.FloatCurve("", typeof(Animator), CarrierParam);
                    var flat = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(spec.Seconds.Value, 0f));
                    AnimationUtility.SetEditorCurve(clip, carrier, flat);
                    return clip;
                }

                float length = spec.Seconds ?? CurveLength(spec);
                foreach (var kv in spec.Sets)
                {
                    var binding = ResolveBinding(kv.Key);
                    AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, length, kv.Value));
                }
                foreach (var cs in spec.Curves)
                {
                    var binding = ResolveBinding(cs.Binding);
                    var keys = cs.Keys.Select(k => new Keyframe(k.Time, k.Value)).ToList();
                    // Unity derives a keyframed clip's length from its last key, so an explicit `seconds:` must be
                    // stamped onto the curve too or it is silently ignored (animator-schema.md: seconds declares
                    // the length; matters for motion-time / blend-tree timing). Hold the last value out to
                    // `seconds`; refuse a `seconds` shorter than the authored content (it can't truncate a key).
                    if (spec.Seconds.HasValue && keys.Count > 0)
                    {
                        var last = keys[0];
                        foreach (var k in keys) if (k.time > last.time) last = k;
                        if (spec.Seconds.Value < last.time)
                            throw new EmitException(
                                $"clip '{spec.Name}': seconds={spec.Seconds.Value} is shorter than curve '{cs.Binding}' last key at {last.time}");
                        if (spec.Seconds.Value > last.time)
                            keys.Add(new Keyframe(spec.Seconds.Value, last.value)); // hold to the declared length
                    }
                    AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(keys.ToArray()));
                }
                return clip;
            }

            private static float CurveLength(ClipSpec spec)
            {
                float max = 0f; bool any = false;
                foreach (var cs in spec.Curves)
                    foreach (var k in cs.Keys) { if (k.Time > max) max = k.Time; any = true; }
                return any ? max : MinClipLength;
            }

            // The scratch float a seconds-only carrier animates. Declared on the controller on first use (never
            // in doc.Parameters, so it is absent from the emitted VRCExpressionParameters), so the carrier curve
            // targets a real, honest parameter rather than a phantom. It is NOT added to _paramNames: the name is
            // reserved (SchemaValidation refuses a user-declared `_CompilerNull`) and must not become a resolvable
            // bare-param target for authored clip bindings — the carrier binding is constructed directly, below.
            private const string CarrierParam = ReservedNames.CarrierParam;
            private bool _carrierDeclared;
            private void EnsureCarrierParam()
            {
                if (_carrierDeclared) return;
                // _paramNames guards the un-validated 2-arg Build path against a duplicate parameter; the door
                // path can't reach here with `_CompilerNull` already declared (validation refuses it).
                if (!_paramNames.Contains(CarrierParam))
                    _controller.AddParameter(new AnimatorControllerParameter
                    {
                        name = CarrierParam, type = AnimatorControllerParameterType.Float, defaultFloat = 0f,
                    });
                _carrierDeclared = true;
            }

            // A bare identifier naming a declared Animator parameter → an Animator-parameter curve (path="").
            // Otherwise the general scene binding: "path/Component.property", split on the LAST '/' then
            // the FIRST '.'. The first dot is the right split because a component type name never contains a
            // dot, while the property CAN — `blendShape.Smile`, `material._Color` are the common cases a
            // LastIndexOf split would fold into the type name (and then fail to resolve).
            private EditorCurveBinding ResolveBinding(string target)
            {
                if (_paramNames.Contains(target))
                    return EditorCurveBinding.FloatCurve("", typeof(Animator), target);

                int slash = target.LastIndexOf('/');
                string path = slash >= 0 ? target.Substring(0, slash) : "";
                string compProp = slash >= 0 ? target.Substring(slash + 1) : target;

                int dot = compProp.IndexOf('.');
                if (dot < 0)
                    throw new EmitException(
                        $"clip binding '{target}' is neither a declared AAP param nor a 'path/Component.property' scene binding");
                string typeName = compProp.Substring(0, dot);
                string prop = compProp.Substring(dot + 1);
                return EditorCurveBinding.FloatCurve(path, ResolveComponentType(typeName), prop);
            }

            private static Type ResolveComponentType(string typeName)
            {
                switch (typeName)
                {
                    case "GameObject": return typeof(GameObject);
                    case "Transform": return typeof(Transform);
                }
                // Most animatable components live in Transform's assembly (UnityEngine.CoreModule).
                var t = typeof(Transform).Assembly.GetType("UnityEngine." + typeName);
                if (t != null) return t;
                // Fallback: any loaded UnityEngine.Object type whose simple name matches.
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var byNs = asm.GetType("UnityEngine." + typeName);
                    if (byNs != null) return byNs;
                }
                throw new EmitException($"clip binding component type '{typeName}' could not be resolved to a UnityEngine type");
            }

            // ----- layers / states / transitions -----

            private void EmitLayers()
            {
                var layers = new List<AnimatorControllerLayer>();
                foreach (var layer in _doc.Layers)
                {
                    var sm = new AnimatorStateMachine
                    {
                        name = layer.Name ?? "Layer",
                        hideFlags = HideFlags.HideInHierarchy,
                    };
                    AssetDatabase.AddObjectToAsset(sm, _controller);

                    // Pass 1: emit states + sub-machines (arbitrary depth), building a tree of per-machine
                    // scopes (each holds only its OWN direct states + direct sub-machines). Target resolution
                    // is scoped, NOT global: Unity scopes state names per state machine, and the schema lets
                    // the SAME name recur in different machines — a layer-global index would silently mis-wire.
                    var rootScope = EmitMachine(layer, layer.Root, sm);
                    // Pass 2: wire defaults + all transitions, resolving each target in its own machine's scope
                    // (bare names) or by walking a slash-qualified path from the layer root (cross-machine).
                    WireMachine(layer, layer.Root, rootScope, rootScope);

                    var acLayer = new AnimatorControllerLayer
                    {
                        name = layer.Name,
                        defaultWeight = layer.Weight,
                        blendingMode = layer.Blend == LayerBlend.Additive
                            ? AnimatorLayerBlendingMode.Additive
                            : AnimatorLayerBlendingMode.Override,
                        stateMachine = sm,
                    };
                    if (!string.IsNullOrEmpty(layer.Mask))
                    {
                        // Fail loud: a mask path that doesn't resolve must not silently emit an UNMASKED layer
                        // (an unmasked gesture/additive layer animates the whole avatar) — a typo'd/moved path is
                        // a compile error, not a shrug.
                        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(layer.Mask);
                        if (mask == null)
                            throw new EmitException($"layer '{layer.Name}': avatarMask '{layer.Mask}' not found (path did not resolve to an AvatarMask)");
                        acLayer.avatarMask = mask;
                    }
                    layers.Add(acLayer);
                }

                // SHARP EDGE: controller.layers / sm.states / bt.children all return COPIES — accumulate,
                // then write the whole array back ONCE.
                _controller.layers = layers.ToArray();
            }

            // One machine's resolution scope: the emitted state machine plus ONLY its direct states and direct
            // sub-machines (each sub keeps its own MachineScope). Because Unity scopes state names per machine
            // and the schema permits a name to recur across machines, resolution is per-scope — never a single
            // layer-global name dictionary (that would last-write-win and silently mis-wire, e.g. GoLoco's
            // 180 sub-machines with reused state names).
            private sealed class MachineScope
            {
                public AnimatorStateMachine Target;
                public readonly Dictionary<string, AnimatorState> States = new Dictionary<string, AnimatorState>();
                public readonly Dictionary<string, MachineScope> Subs = new Dictionary<string, MachineScope>();
            }

            // Pass 1 — emit this machine's states + child sub-machines (recursing), returning its scope. NO
            // transitions here: a target may name something in a sibling/child machine not yet emitted, so all
            // wiring waits for pass 2 (WireMachine) once the whole scope tree exists.
            private MachineScope EmitMachine(Layer layer, StateMachine model, AnimatorStateMachine target)
            {
                var scope = new MachineScope { Target = target };
                var states = model.States;
                for (int i = 0; i < states.Count; i++)
                {
                    var s = states[i];
                    var pos = new Vector3(300f, 60f * i, 0f); // fixed grid keyed by document order
                    var ast = target.AddState(s.Name, pos);
                    ast.writeDefaultValues = s.WriteDefaults ?? layer.WriteDefaults ?? _doc.Defaults.WriteDefaults;
                    ast.speed = s.Speed;
                    if (!string.IsNullOrEmpty(s.SpeedParam)) { ast.speedParameterActive = true; ast.speedParameter = s.SpeedParam; }
                    if (!string.IsNullOrEmpty(s.MotionTimeParam)) { ast.timeParameterActive = true; ast.timeParameter = s.MotionTimeParam; }
                    ast.mirror = s.Mirror;
                    if (s.Motion != null) ast.motion = BuildMotion(s.Motion, s.Name + "_BlendTree", s.Name);
                    EmitBehaviours(s.Behaviours, t => ast.AddStateMachineBehaviour(t));
                    scope.States[s.Name] = ast;
                }

                var machines = model.Machines;
                for (int i = 0; i < machines.Count; i++)
                {
                    var sub = machines[i];
                    var pos = new Vector3(600f, 60f * i, 0f); // sub-machines to the right of the state grid
                    var childSm = target.AddStateMachine(sub.Name, pos);
                    childSm.hideFlags = HideFlags.HideInHierarchy;
                    scope.Subs[sub.Name] = EmitMachine(layer, sub.Machine, childSm);
                }

                EmitBehaviours(model.Behaviours, t => target.AddStateMachineBehaviour(t));
                return scope;
            }

            // Pass 2 — wire default + transitions for this machine (resolving in its OWN scope + qualified paths
            // from `root`), then recurse into child sub-machines.
            private void WireMachine(Layer layer, StateMachine model, MachineScope scope, MachineScope root)
            {
                var target = scope.Target;

                // default resolves in THIS machine's LOCAL scope only — a machine's default is one of its own
                // direct states or direct sub-machines (Unity has no default into a foreign/nested machine). A
                // state → defaultState; a sub-machine → an unconditional entry transition added AFTER the entry
                // ladder (below) so it stays the last, catch-all entry.
                bool defaultIsState = false;
                if (!string.IsNullOrEmpty(model.DefaultState))
                {
                    if (scope.States.TryGetValue(model.DefaultState, out var def)) { target.defaultState = def; defaultIsState = true; }
                    else if (!scope.Subs.ContainsKey(model.DefaultState))
                        throw new EmitException($"machine '{target.name}': default '{model.DefaultState}' is neither a direct state nor a direct sub-machine of this machine");
                }

                // State transition ladders (ordered per source state = first-match order). `from` is THIS
                // machine's own emitted state, never a global lookup (a same-named state in another machine
                // would attach the transition to the wrong node).
                foreach (var s in model.States)
                {
                    var from = scope.States[s.Name];
                    foreach (var t in s.Transitions)
                    {
                        AnimatorStateTransition tr;
                        if (t.ToExit) tr = from.AddExitTransition();
                        else if (string.IsNullOrEmpty(t.To))
                            throw new EmitException($"transition from '{from.name}' has neither a target nor ToExit");
                        else tr = ResolveName(t.To, scope, root, target.name, out var toState, out var toSm)
                            ? from.AddTransition(toState) : from.AddTransition(toSm);
                        ConfigureStateTransition(tr, t);
                    }
                }

                // AnyState ladder.
                foreach (var t in model.AnyLadder)
                {
                    RequireTargetName(t, target.name);
                    var atr = ResolveName(t.To, scope, root, target.name, out var toState, out var toSm)
                        ? target.AddAnyStateTransition(toState) : target.AddAnyStateTransition(toSm);
                    atr.canTransitionToSelf = t.CanTransitionToSelf;
                    ConfigureStateTransition(atr, t);
                }

                // Entry ladder (no duration / exit-time — conditions only).
                foreach (var t in model.EntryLadder)
                {
                    RequireTargetName(t, target.name);
                    var etr = ResolveName(t.To, scope, root, target.name, out var toState, out var toSm)
                        ? target.AddEntryTransition(toState) : target.AddEntryTransition(toSm);
                    foreach (var c in t.When) etr.AddCondition(MapCondOp(c.Op, c.Value), c.Value, c.Param);
                }

                // default → sub-machine: unconditional catch-all entry, added last so any conditional entry
                // ladder above wins first (first-match order). NOTE: we set NO `defaultState` here — and when
                // a machine has no direct states, Unity's AnimatorStateMachine.defaultState GETTER resolves
                // THROUGH to the child machine's own default. So downstream lint/decompile must not read
                // `defaultState` as a reliable "was a direct-state default set" probe; the entry transition is.
                if (!defaultIsState && !string.IsNullOrEmpty(model.DefaultState))
                    target.AddEntryTransition(scope.Subs[model.DefaultState].Target);

                foreach (var sub in model.Machines)
                    WireMachine(layer, sub.Machine, scope.Subs[sub.Name], root);
            }

            private static void RequireTargetName(Transition t, string fromMachine)
            {
                if (t.ToExit || string.IsNullOrEmpty(t.To))
                    throw new EmitException($"entry/any ladder transition in machine '{fromMachine}' has no target (Exit is not a valid target there)");
            }

            // Resolve a transition target to a state (returns true) or a sub-machine (returns false). A BARE
            // name resolves ONLY in `scope` — the referencing machine's own direct states + direct sub-machines.
            // A SLASH-QUALIFIED name is a path from the LAYER `root`: every non-final segment is a sub-machine,
            // the final segment is a state or a sub-machine. An unresolved target is fail-loud, naming the
            // offender and the machine it was referenced from — NEVER a silent global fallback.
            private bool ResolveName(string name, MachineScope scope, MachineScope root, string fromMachine,
                out AnimatorState toState, out AnimatorStateMachine toSm)
            {
                toState = null; toSm = null;
                if (name.IndexOf('/') < 0)
                {
                    if (scope.States.TryGetValue(name, out toState)) return true;
                    if (scope.Subs.TryGetValue(name, out var sub)) { toSm = sub.Target; return false; }
                    throw new EmitException($"transition target '{name}' not found in machine '{fromMachine}' — a bare name resolves only within its own machine; use a 'Sub/State' path from the layer root for a cross-machine target");
                }

                var segs = name.Split('/');
                var cur = root;
                for (int i = 0; i < segs.Length - 1; i++)
                    if (!cur.Subs.TryGetValue(segs[i], out cur))
                        throw new EmitException($"transition target path '{name}' (from machine '{fromMachine}'): segment '{segs[i]}' is not a sub-machine on the path from the layer root");
                var leaf = segs[segs.Length - 1];
                if (cur.States.TryGetValue(leaf, out toState)) return true;
                if (cur.Subs.TryGetValue(leaf, out var subm)) { toSm = subm.Target; return false; }
                throw new EmitException($"transition target path '{name}' (from machine '{fromMachine}'): final segment '{leaf}' is neither a state nor a sub-machine");
            }

            private void ConfigureStateTransition(AnimatorStateTransition tr, Transition t)
            {
                if (t.ExitTime.HasValue) { tr.hasExitTime = true; tr.exitTime = t.ExitTime.Value; }
                else tr.hasExitTime = _doc.Defaults.TransitionHasExitTime;
                tr.duration = t.Duration ?? _doc.Defaults.TransitionDuration;
                tr.hasFixedDuration = t.FixedDuration ?? true;
                tr.interruptionSource = MapInterruption(t.Interruption ?? _doc.Defaults.Interruption);
                tr.orderedInterruption = t.OrderedInterruption ?? true;
                foreach (var c in t.When) tr.AddCondition(MapCondOp(c.Op, c.Value), c.Value, c.Param); // empty When = unconditional
            }

            // ----- motions / blend trees -----

            // stateContext names the OWNING state (threaded down through blend-tree children too) so an
            // unresolved-marker guid ref can be recorded against its state for the compile advisory.
            private Motion BuildMotion(MotionRef mr, string treeName, string stateContext)
            {
                if (mr == null) return null;
                if (mr.Clip != null)
                {
                    if (!_result.Clips.TryGetValue(mr.Clip, out var clip))
                        throw new EmitException($"motion references clip '{mr.Clip}' not declared under clips:");
                    return clip;
                }
                if (mr.RefPath != null)
                {
                    var m = AssetDatabase.LoadAssetAtPath<Motion>(mr.RefPath);
                    if (m == null) throw new EmitException($"motion ref path not found: {mr.RefPath}");
                    return m;
                }
                if (mr.RefGuid != null)
                {
                    var m = ResolveGuidMotion(mr.RefGuid);
                    if (m == null)
                    {
                        // A ref flagged `unresolved: true` is a KNOWN dangling handle (e.g. an asset absent from
                        // this project) — leave the motion slot null (a clean-empty state) and record it for the
                        // compile advisory, preserving the verbatim GUID for the round-trip note. Only a BARE
                        // broken ref (no marker) is a hard error.
                        if (mr.RefGuid.Unresolved)
                        {
                            // Record the OWNING state (threaded from EmitMachine through any blend-tree nesting),
                            // not the synthetic tree name; the child index is deliberately omitted — the verbatim
                            // guid in the advisory already localizes which ref within the state failed.
                            _result.UnresolvedRefs.Add((stateContext, mr.RefGuid.Guid));
                            return null;
                        }
                        throw new EmitException(GuidRefUnresolved(mr.RefGuid));
                    }
                    return m;
                }
                if (mr.Tree != null) return BuildTree(mr.Tree, treeName, stateContext);
                throw new EmitException("motion ref sets none of clip/ref/tree");
            }

            // Resolve a guid[+fileID] motion ref. A non-zero fileID names a SUB-ASSET (e.g. one clip inside a
            // multi-clip FBX) — select the Motion whose local file id matches, so the schema's fileID is not
            // silently dropped onto the main asset. A zero fileID loads the main asset as a Motion (a plain
            // .anim). Returns null on no match; callers name the failure.
            private static Motion ResolveGuidMotion(GuidRef g)
            {
                string p = AssetDatabase.GUIDToAssetPath(g.Guid);
                if (string.IsNullOrEmpty(p)) return null;
                if (g.FileID != 0)
                {
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p))
                        if (o is Motion mo
                            && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mo, out _, out long lfid)
                            && lfid == g.FileID)
                            return mo;
                    return null; // fileID given but no sub-asset Motion matched
                }
                return AssetDatabase.LoadAssetAtPath<Motion>(p);
            }

            private static string GuidRefUnresolved(GuidRef g)
                => $"motion ref guid unresolved: {g.Guid}" + (g.FileID != 0 ? $" fileID:{g.FileID}" : "");

            private BlendTree BuildTree(BlendTreeSpec spec, string name, string stateContext)
            {
                var bt = new BlendTree { name = name, hideFlags = HideFlags.HideInHierarchy };
                bt.blendType = MapTreeKind(spec.Kind);
                if (!string.IsNullOrEmpty(spec.Param)) bt.blendParameter = spec.Param;
                if (!string.IsNullOrEmpty(spec.ParamY)) bt.blendParameterY = spec.ParamY;
                AssetDatabase.AddObjectToAsset(bt, _controller);
                _result.Trees.Add(bt);

                for (int i = 0; i < spec.Children.Count; i++)
                {
                    var child = spec.Children[i];
                    var childMotion = BuildMotion(child.Motion, name + "_" + i, stateContext);
                    switch (spec.Kind)
                    {
                        case TreeKind.OneD: bt.AddChild(childMotion, child.Threshold); break;
                        case TreeKind.Direct: bt.AddChild(childMotion); break;
                        default: bt.AddChild(childMotion, new Vector2(child.PosX, child.PosY)); break;
                    }
                }

                // children is a COPY — set per-child fields, then write the whole array back.
                var kids = bt.children;
                for (int i = 0; i < kids.Length; i++)
                {
                    var src = spec.Children[i];
                    if (spec.Kind == TreeKind.Direct && !string.IsNullOrEmpty(src.DirectWeight))
                        kids[i].directBlendParameter = src.DirectWeight;
                    kids[i].timeScale = src.TimeScale;
                    kids[i].mirror = src.Mirror;
                    kids[i].cycleOffset = src.CycleOffset;
                }
                bt.children = kids;
                return bt;
            }

            // ----- behaviours (all seven VRC SMB kinds) -----
            // One encoder per kind, each mirroring the driver encoder's shape: materialise the concrete VRC
            // SMB via `add(typeof(...))`, then a per-field switch that sets the SMB's fields and throws
            // EmitException (named offender) on any unknown field.
            //
            // WRITE ↔ READ alignment: this side's field/channel NAMES mirror ControllerReport's read side so
            // decode round-trips — but ControllerReport is a Markdown report, not a decoder: it renders enums
            // via ToString() (PascalCase, e.g. `Animation`) whereas the emit tokens here are camelCase (e.g.
            // `animation`). The enum-token casing is DEFINED HERE (the token→enum maps below), not inherited
            // from the report. And the report only decodes 3 of 7 kinds (driver, tracking, locomotion); the
            // other four — playableLayer, poseSpace, playAudio, layerControl — have NO read side yet, so their
            // token surface here is the authority Task 5's decode must match, not a mirror of existing code.
            private void EmitBehaviours(List<Behaviour> behaviours, Func<Type, StateMachineBehaviour> add)
            {
                if (behaviours == null) return;
                foreach (var b in behaviours)
                {
                    switch (b.Kind)
                    {
                        case "driver":
                            PopulateDriver((VRCAvatarParameterDriver)add(typeof(VRCAvatarParameterDriver)), b.Fields);
                            break;
                        case "tracking":
                            PopulateTracking((VRCAnimatorTrackingControl)add(typeof(VRCAnimatorTrackingControl)), b.Fields);
                            break;
                        case "playableLayer":
                            PopulatePlayableLayer((VRCPlayableLayerControl)add(typeof(VRCPlayableLayerControl)), b.Fields);
                            break;
                        case "locomotion":
                            PopulateLocomotion((VRCAnimatorLocomotionControl)add(typeof(VRCAnimatorLocomotionControl)), b.Fields);
                            break;
                        case "poseSpace":
                            PopulatePoseSpace((VRCAnimatorTemporaryPoseSpace)add(typeof(VRCAnimatorTemporaryPoseSpace)), b.Fields);
                            break;
                        case "playAudio":
                            PopulatePlayAudio((VRCAnimatorPlayAudio)add(typeof(VRCAnimatorPlayAudio)), b.Fields);
                            break;
                        case "layerControl":
                            PopulateLayerControl((VRCAnimatorLayerControl)add(typeof(VRCAnimatorLayerControl)), b.Fields);
                            break;
                        default:
                            throw new EmitException(
                                $"behaviour: unknown kind '{b.Kind}' (expected driver/tracking/playableLayer/locomotion/poseSpace/playAudio/layerControl)");
                    }
                }
            }

            private static void PopulateDriver(VRCAvatarParameterDriver drv, Dictionary<string, object> fields)
            {
                var ps = new List<Driver.Parameter>();
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case "localOnly":
                            drv.localOnly = AsBool(kv.Value, "driver.localOnly");
                            break;
                        case "set":
                            foreach (var e in AsMap(kv.Value, "driver.set"))
                                ps.Add(new Driver.Parameter { type = Driver.ChangeType.Set, name = e.Key, value = AsFloat(e.Value, "driver.set." + e.Key) });
                            break;
                        case "add":
                            foreach (var e in AsMap(kv.Value, "driver.add"))
                                ps.Add(new Driver.Parameter { type = Driver.ChangeType.Add, name = e.Key, value = AsFloat(e.Value, "driver.add." + e.Key) });
                            break;
                        case "copy":
                            foreach (var e in AsMap(kv.Value, "driver.copy"))
                                ps.Add(BuildCopy(e.Key, e.Value));
                            break;
                        case "random":
                            foreach (var e in AsMap(kv.Value, "driver.random"))
                                ps.Add(BuildRandom(e.Key, e.Value));
                            break;
                        default:
                            throw new EmitException($"driver: unknown field '{kv.Key}' (expected set/add/copy/random/localOnly)");
                    }
                }
                drv.parameters = ps;
            }

            // copy: { Dest: Source }  OR  copy: { Dest: { source, sourceMin, sourceMax, destMin, destMax } }
            private static Driver.Parameter BuildCopy(string dest, object value)
            {
                var p = new Driver.Parameter { type = Driver.ChangeType.Copy, name = dest };
                if (value is string src) { p.source = src; p.convertRange = false; return p; }
                var m = AsMap(value, "driver.copy." + dest);
                bool range = false;
                foreach (var kv in m)
                {
                    switch (kv.Key)
                    {
                        case "source": p.source = AsString(kv.Value, "driver.copy." + dest + ".source"); break;
                        case "sourceMin": p.sourceMin = AsFloat(kv.Value, "driver.copy." + dest + ".sourceMin"); range = true; break;
                        case "sourceMax": p.sourceMax = AsFloat(kv.Value, "driver.copy." + dest + ".sourceMax"); range = true; break;
                        case "destMin": p.destMin = AsFloat(kv.Value, "driver.copy." + dest + ".destMin"); range = true; break;
                        case "destMax": p.destMax = AsFloat(kv.Value, "driver.copy." + dest + ".destMax"); range = true; break;
                        default: throw new EmitException($"driver.copy.{dest}: unknown field '{kv.Key}'");
                    }
                }
                p.convertRange = range;
                return p;
            }

            // random: { Name: { min, max, chance } }
            private static Driver.Parameter BuildRandom(string name, object value)
            {
                var p = new Driver.Parameter { type = Driver.ChangeType.Random, name = name };
                var m = AsMap(value, "driver.random." + name);
                foreach (var kv in m)
                {
                    switch (kv.Key)
                    {
                        case "min": p.valueMin = AsFloat(kv.Value, "driver.random." + name + ".min"); break;
                        case "max": p.valueMax = AsFloat(kv.Value, "driver.random." + name + ".max"); break;
                        case "chance": p.chance = AsFloat(kv.Value, "driver.random." + name + ".chance"); break;
                        default: throw new EmitException($"driver.random.{name}: unknown field '{kv.Key}'");
                    }
                }
                return p;
            }

            // tracking: { <channel>: <state> } where channel is one of the ten VRC tracking channels and state
            // is animation|tracking|noChange. Channel NAMES mirror ControllerReport.AppendTracking's channel
            // enumeration; the enum-token casing (camelCase) is defined by TrackingTokens below, not by the
            // report's PascalCase ToString() rendering. An untouched channel keeps its SDK default (NoChange).
            private static void PopulateTracking(VRCAnimatorTrackingControl tc, Dictionary<string, object> fields)
            {
                foreach (var kv in fields)
                {
                    var v = ParseEnumToken(kv.Value, TrackingTokens, "tracking." + kv.Key);
                    switch (kv.Key)
                    {
                        case "head": tc.trackingHead = v; break;
                        case "leftHand": tc.trackingLeftHand = v; break;
                        case "rightHand": tc.trackingRightHand = v; break;
                        case "hip": tc.trackingHip = v; break;
                        case "leftFoot": tc.trackingLeftFoot = v; break;
                        case "rightFoot": tc.trackingRightFoot = v; break;
                        case "leftFingers": tc.trackingLeftFingers = v; break;
                        case "rightFingers": tc.trackingRightFingers = v; break;
                        case "eyes": tc.trackingEyes = v; break;
                        case "mouth": tc.trackingMouth = v; break;
                        default:
                            throw new EmitException($"tracking: unknown channel '{kv.Key}' (expected head/leftHand/rightHand/hip/leftFoot/rightFoot/leftFingers/rightFingers/eyes/mouth)");
                    }
                }
            }

            // playableLayer: { layer: action|fx|gesture|additive, goalWeight: <f>, blendDuration: <f> }
            private static void PopulatePlayableLayer(VRCPlayableLayerControl c, Dictionary<string, object> fields)
            {
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case "layer": c.layer = ParseEnumToken(kv.Value, PlayableLayerTokens, "playableLayer.layer"); break;
                        case "goalWeight": c.goalWeight = AsFloat(kv.Value, "playableLayer.goalWeight"); break;
                        case "blendDuration": c.blendDuration = AsFloat(kv.Value, "playableLayer.blendDuration"); break;
                        default: throw new EmitException($"playableLayer: unknown field '{kv.Key}' (expected layer/goalWeight/blendDuration)");
                    }
                }
            }

            // locomotion: { disableLocomotion: <bool> }
            private static void PopulateLocomotion(VRCAnimatorLocomotionControl c, Dictionary<string, object> fields)
            {
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case "disableLocomotion": c.disableLocomotion = AsBool(kv.Value, "locomotion.disableLocomotion"); break;
                        default: throw new EmitException($"locomotion: unknown field '{kv.Key}' (expected disableLocomotion)");
                    }
                }
            }

            // poseSpace: { enterPoseSpace: <bool>, fixedDelay: <bool>, delayTime: <f> }
            private static void PopulatePoseSpace(VRCAnimatorTemporaryPoseSpace c, Dictionary<string, object> fields)
            {
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case "enterPoseSpace": c.enterPoseSpace = AsBool(kv.Value, "poseSpace.enterPoseSpace"); break;
                        case "fixedDelay": c.fixedDelay = AsBool(kv.Value, "poseSpace.fixedDelay"); break;
                        case "delayTime": c.delayTime = AsFloat(kv.Value, "poseSpace.delayTime"); break;
                        default: throw new EmitException($"poseSpace: unknown field '{kv.Key}' (expected enterPoseSpace/fixedDelay/delayTime)");
                    }
                }
            }

            // layerControl: { playable: action|fx|gesture|additive, layer: <int index>, goalWeight: <f>, blendDuration: <f> }.
            // NOTE the SDK asymmetry vs playableLayer: here `playable` is the target playable enum and `layer` is
            // the integer LAYER INDEX within it.
            private static void PopulateLayerControl(VRCAnimatorLayerControl c, Dictionary<string, object> fields)
            {
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case "playable": c.playable = ParseEnumToken(kv.Value, LayerCtrlTokens, "layerControl.playable"); break;
                        case "layer": c.layer = AsInt(kv.Value, "layerControl.layer"); break;
                        case "goalWeight": c.goalWeight = AsFloat(kv.Value, "layerControl.goalWeight"); break;
                        case "blendDuration": c.blendDuration = AsFloat(kv.Value, "layerControl.blendDuration"); break;
                        default: throw new EmitException($"layerControl: unknown field '{kv.Key}' (expected playable/layer/goalWeight/blendDuration)");
                    }
                }
            }

            // playAudio: the VRCAnimatorPlayAudio serializable surface. The AudioSource itself is addressed by
            // hierarchy path (sourcePath) — the SDK resolves the live Source from it at runtime; clips are asset
            // paths. Volume/pitch are [min, max] ranges. ControllerReport does not yet decode this kind, so this
            // token surface is the authority Task 5's decode must mirror.
            private static void PopulatePlayAudio(VRCAnimatorPlayAudio c, Dictionary<string, object> fields)
            {
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case "sourcePath": c.SourcePath = AsString(kv.Value, "playAudio.sourcePath"); break;
                        case "playbackOrder": c.PlaybackOrder = ParseEnumToken(kv.Value, AudioOrderTokens, "playAudio.playbackOrder"); break;
                        case "parameter": c.ParameterName = AsString(kv.Value, "playAudio.parameter"); break;
                        case "volume": c.Volume = AsVector2(kv.Value, "playAudio.volume"); break;
                        case "volumeApply": c.VolumeApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.volumeApply"); break;
                        case "pitch": c.Pitch = AsVector2(kv.Value, "playAudio.pitch"); break;
                        case "pitchApply": c.PitchApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.pitchApply"); break;
                        case "loop": c.Loop = AsBool(kv.Value, "playAudio.loop"); break;
                        case "loopApply": c.LoopApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.loopApply"); break;
                        case "clips": c.Clips = AsClips(kv.Value, "playAudio.clips"); break;
                        case "clipsApply": c.ClipsApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.clipsApply"); break;
                        case "delaySeconds": c.DelayInSeconds = AsFloat(kv.Value, "playAudio.delaySeconds"); break;
                        case "playOnEnter": c.PlayOnEnter = AsBool(kv.Value, "playAudio.playOnEnter"); break;
                        case "stopOnEnter": c.StopOnEnter = AsBool(kv.Value, "playAudio.stopOnEnter"); break;
                        case "playOnExit": c.PlayOnExit = AsBool(kv.Value, "playAudio.playOnExit"); break;
                        case "stopOnExit": c.StopOnExit = AsBool(kv.Value, "playAudio.stopOnExit"); break;
                        default: throw new EmitException($"playAudio: unknown field '{kv.Key}' (expected sourcePath/playbackOrder/parameter/volume/volumeApply/pitch/pitchApply/loop/loopApply/clips/clipsApply/delaySeconds/playOnEnter/stopOnEnter/playOnExit/stopOnExit)");
                    }
                }
            }

            // ----- VRC expression parameters (in-memory; CompileController persists) -----

            // Emit a VRCExpressionParameters asset listing the controller's OWN declared params — for
            // legibility, so a human inspecting the merged FX sees them like a "Parameters - …" asset — even
            // when they are NOT synced. Excludes VRC built-ins (not ours to declare) and scratch/working
            // params (internal residue). A non-synced param lists networkSynced=false: it costs no sync bit.
            private void EmitVrcParameters()
            {
                var listed = _doc.Parameters
                    .Where(p => !p.Scratch && !ControllerRules.IsVrcReserved(p.Name))
                    .ToList();
                if (listed.Count == 0) return;

                var ep = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                var list = new List<VRCExpressionParameters.Parameter>();
                foreach (var p in listed)
                {
                    var vt = p.Vrc?.VrcType ?? p.Type;
                    list.Add(new VRCExpressionParameters.Parameter
                    {
                        name = p.Name,
                        valueType = MapValueType(vt),
                        defaultValue = p.Default,
                        saved = p.Vrc?.Saved ?? false,
                        networkSynced = p.Vrc?.Synced ?? false,
                    });
                }
                ep.parameters = list.ToArray();
                _result.Params = ep;
            }

            // ----- persistence / provenance -----

            private void StampProvenance(string path)
            {
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null) return;
                // CompileController reads this userData to WARN before clobbering an out-of-band edit. Hash the
                // exact SOURCE BYTES when they were threaded through (the honest signal); fall back to hashing
                // SourcePath for the scratch/2-arg door that never sees the text.
                string src = _doc.SourcePath ?? "";
                string hash = SourceHash(_sourceText ?? src);
                importer.userData = "compiled-from:" + src + ";srchash:" + hash;
                importer.SaveAndReimport();
            }

            private void ReloadFromDisk(string path)
            {
                _result.Controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                _result.Clips.Clear();
                _result.ClipList.Clear();
                _result.Trees.Clear();
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (o is AnimationClip clip) { _result.Clips[clip.name] = clip; _result.ClipList.Add(clip); }
                    else if (o is BlendTree bt) { _result.Trees.Add(bt); }
                }
            }
        }

        // ----- output-path logic (CompileController passes the real outDir; default is the scratch dir) -----

        private static string PathFor(string outDir, string controllerName) => outDir + "/" + controllerName + ".controller";

        private static void EnsureFolder(string assetFolder)
        {
            var parts = assetFolder.Split('/');
            string cur = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        // ----- construct -> Unity enum maps -----

        // Bool conditions carry their truth in the VALUE (`is true` = Is/1, `is false` = Is/0); Unity
        // encodes it in the MODE (If/IfNot) and ignores the threshold — so Is/IsNot must fold the value
        // into If vs IfNot, or `is false` wrongly reads as `If` (fires on true). Numeric ops pass through.
        private static AnimatorConditionMode MapCondOp(CondOp op, float value)
        {
            bool truthy = value != 0f;
            switch (op)
            {
                case CondOp.Is:    return truthy ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
                case CondOp.IsNot: return truthy ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If;
                case CondOp.Greater: return AnimatorConditionMode.Greater;
                case CondOp.Less: return AnimatorConditionMode.Less;
                case CondOp.Equals: return AnimatorConditionMode.Equals;
                case CondOp.NotEqual: return AnimatorConditionMode.NotEqual;
                default: throw new EmitException($"unhandled condition op '{op}'");
            }
        }

        private static TransitionInterruptionSource MapInterruption(TransitionInterruption i)
        {
            switch (i)
            {
                case TransitionInterruption.None: return TransitionInterruptionSource.None;
                case TransitionInterruption.Source: return TransitionInterruptionSource.Source;
                case TransitionInterruption.Destination: return TransitionInterruptionSource.Destination;
                case TransitionInterruption.SourceThenDestination: return TransitionInterruptionSource.SourceThenDestination;
                case TransitionInterruption.DestinationThenSource: return TransitionInterruptionSource.DestinationThenSource;
                default: throw new EmitException($"unhandled interruption '{i}'");
            }
        }

        private static BlendTreeType MapTreeKind(TreeKind k)
        {
            switch (k)
            {
                case TreeKind.OneD: return BlendTreeType.Simple1D;
                case TreeKind.SimpleDirectional2D: return BlendTreeType.SimpleDirectional2D;
                case TreeKind.FreeformDirectional2D: return BlendTreeType.FreeformDirectional2D;
                case TreeKind.FreeformCartesian2D: return BlendTreeType.FreeformCartesian2D;
                case TreeKind.Direct: return BlendTreeType.Direct;
                default: throw new EmitException($"unhandled tree kind '{k}'");
            }
        }

        private static VRCExpressionParameters.ValueType MapValueType(AnimParamType t)
        {
            switch (t)
            {
                case AnimParamType.Bool: return VRCExpressionParameters.ValueType.Bool;
                case AnimParamType.Int: return VRCExpressionParameters.ValueType.Int;
                default: return VRCExpressionParameters.ValueType.Float;
            }
        }

        // ----- neutral-scalar coercion (parser yields long/double/bool/string) -----

        private static float AsFloat(object v, string ctx)
        {
            switch (v)
            {
                case long l: return l;
                case double d: return (float)d;
                case int i: return i;
                case float f: return f;
                case bool b: return b ? 1f : 0f;
                default: throw new EmitException($"{ctx}: expected a number, got {(v == null ? "null" : v.GetType().Name)}");
            }
        }

        private static bool AsBool(object v, string ctx)
        {
            if (v is bool b) return b;
            if (v is long l) return l != 0;
            if (v is double d) return d != 0;
            throw new EmitException($"{ctx}: expected a boolean, got {(v == null ? "null" : v.GetType().Name)}");
        }

        private static string AsString(object v, string ctx)
        {
            if (v is string s) return s;
            throw new EmitException($"{ctx}: expected a string, got {(v == null ? "null" : v.GetType().Name)}");
        }

        private static Dictionary<string, object> AsMap(object v, string ctx)
        {
            if (v is Dictionary<string, object> m) return m;
            throw new EmitException($"{ctx}: expected a mapping, got {(v == null ? "null" : v.GetType().Name)}");
        }

        private static int AsInt(object v, string ctx)
        {
            switch (v)
            {
                case long l: return (int)l;
                case int i: return i;
                case double d: return (int)Math.Round(d);
                default: throw new EmitException($"{ctx}: expected an integer, got {(v == null ? "null" : v.GetType().Name)}");
            }
        }

        // A [min, max] flow list → Vector2 (VRCAnimatorPlayAudio volume/pitch ranges).
        private static Vector2 AsVector2(object v, string ctx)
        {
            if (!(v is List<object> list))
                throw new EmitException($"{ctx}: expected a [min, max] list, got {(v == null ? "null" : v.GetType().Name)}");
            if (list.Count != 2)
                throw new EmitException($"{ctx}: expected exactly 2 numbers, got {list.Count}");
            return new Vector2(AsFloat(list[0], ctx + "[0]"), AsFloat(list[1], ctx + "[1]"));
        }

        // A list of project asset paths → AudioClip[]. A path that doesn't resolve to a clip is fail-loud
        // (consistent with the avatarMask / motion-ref path handling).
        private static AudioClip[] AsClips(object v, string ctx)
        {
            if (!(v is List<object> list))
                throw new EmitException($"{ctx}: expected a list of asset paths, got {(v == null ? "null" : v.GetType().Name)}");
            var clips = new AudioClip[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                string path = AsString(list[i], ctx + "[" + i + "]");
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) throw new EmitException($"{ctx}: audio clip not found at '{path}'");
                clips[i] = clip;
            }
            return clips;
        }

        // Decode a schema token to its SDK enum via the kind's token map; an unknown token is fail-loud with
        // the accepted set. Enum fields carry their truth in a token, not a raw number.
        private static T ParseEnumToken<T>(object v, Dictionary<string, T> map, string ctx) where T : struct
        {
            string s = AsString(v, ctx);
            if (map.TryGetValue(s, out var e)) return e;
            throw new EmitException($"{ctx}: unknown value '{s}' (expected {string.Join("/", map.Keys)})");
        }

        // ----- schema-token → SDK-enum maps (the enum-valued behaviour fields) -----

        private static readonly Dictionary<string, TrackingType> TrackingTokens = new Dictionary<string, TrackingType>
        {
            { "noChange", TrackingType.NoChange }, { "tracking", TrackingType.Tracking }, { "animation", TrackingType.Animation },
        };

        private static readonly Dictionary<string, PlayableLayer> PlayableLayerTokens = new Dictionary<string, PlayableLayer>
        {
            { "action", PlayableLayer.Action }, { "fx", PlayableLayer.FX }, { "gesture", PlayableLayer.Gesture }, { "additive", PlayableLayer.Additive },
        };

        private static readonly Dictionary<string, LayerCtrlLayer> LayerCtrlTokens = new Dictionary<string, LayerCtrlLayer>
        {
            { "action", LayerCtrlLayer.Action }, { "fx", LayerCtrlLayer.FX }, { "gesture", LayerCtrlLayer.Gesture }, { "additive", LayerCtrlLayer.Additive },
        };

        private static readonly Dictionary<string, AudioOrder> AudioOrderTokens = new Dictionary<string, AudioOrder>
        {
            { "random", AudioOrder.Random }, { "uniqueRandom", AudioOrder.UniqueRandom }, { "roundabout", AudioOrder.Roundabout }, { "parameter", AudioOrder.Parameter },
        };

        private static readonly Dictionary<string, AudioApply> AudioApplyTokens = new Dictionary<string, AudioApply>
        {
            { "alwaysApply", AudioApply.AlwaysApply }, { "applyIfStopped", AudioApply.ApplyIfStopped }, { "neverApply", AudioApply.NeverApply },
        };

        /// <summary>The provenance source hash — first 8 bytes of the MD5 of <paramref name="s"/>, hex.
        /// CompileController computes this over the current source text and compares it to the <c>srchash:</c>
        /// stamped in a controller's userData to detect drift before overwriting.</summary>
        public static string SourceHash(string s)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
