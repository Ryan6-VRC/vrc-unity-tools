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

        // Tool-owned graph layout. The compiler owns EVERY node position — the state/sub-machine grid AND
        // the four special nodes — so DecompileController has a deterministic baseline to compare against
        // (a value Unity assigns at node creation is version-dependent). Single authority both directions read.
        internal static Vector3 GridState(int i) => new Vector3(300f, 60f * i, 0f);
        internal static Vector3 GridSub(int i)   => new Vector3(600f, 60f * i, 0f);
        internal static readonly Vector3 SpecialEntry  = new Vector3(50f,   0f, 0f);
        internal static readonly Vector3 SpecialAny    = new Vector3(50f,  60f, 0f);
        internal static readonly Vector3 SpecialExit   = new Vector3(50f, 120f, 0f);
        internal static readonly Vector3 SpecialParent = new Vector3(50f, -60f, 0f);

        internal static Vector3? NodePos(MachineLayout l, string name)
            => (l != null && l.Nodes.TryGetValue(AddressPath.EscapeSegment(name), out var xy))
                ? new Vector3(xy[0], xy[1], 0f) : (Vector3?)null;
        private static Vector3 SpecialPos(float[] xy, Vector3 fallback)
            => xy != null ? new Vector3(xy[0], xy[1], 0f) : fallback;

        // Positional default names — the single source both BuildTree (emit) and DecodeTree (decompile) use to
        // tell an auto-generated blend-tree name from a human-authored one.
        internal static string AutoTreeName(string stateName) => stateName + "_BlendTree";
        internal static string AutoChildName(string parentAppliedName, int index) => parentAppliedName + "_" + index;

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

        // Convenience door: emit to the scratch dir, hashing SourcePath for provenance (throwaway builds).
        // CompileController uses the 4-arg overload below.
        public static void Build(AnimDocument doc, out EmitResult result)
            => Build(doc, ScratchRoot, null, out result);

        /// <summary>Emit <paramref name="doc"/> into <paramref name="outDir"/> (an <c>Assets/…</c>-relative
        /// folder). <paramref name="sourceText"/>, when non-null, is the exact source-file text — its BYTES
        /// are hashed into the controller's provenance userData so a later compile can detect an out-of-band
        /// edit (null falls back to hashing <see cref="AnimDocument.SourcePath"/>).
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

        // ----- detached clip content (controller-free; shared by the embedded EmitClips and a future
        // CompileClips door that builds a clip WITHOUT a controller) -----

        /// <summary>Author and return a DETACHED <see cref="AnimationClip"/> (curves/settings only) for
        /// <paramref name="spec"/> — NO controller access, NO sub-asset embedding, NO hideFlags, and (for a
        /// seconds-only clip) NO carrier-param declaration. Those are the CALLER's responsibility. The output
        /// curves/tangents/lengths are identical to the embedded emission path.
        /// <paramref name="paramNames"/> is the set of declared Animator parameter names — the gate that routes
        /// a bare binding to an Animator-property curve (path="") rather than a scene binding.</summary>
        internal static AnimationClip BuildClipContent(ClipSpec spec, HashSet<string> paramNames)
        {
            var clip = new AnimationClip { name = spec.Name, frameRate = 60f };
            bool hasContent = spec.Sets.Count > 0 || spec.Curves.Count > 0;

            if (!hasContent)
            {
                // Seconds-only clip → inert carrier that ONLY gives the clip a genuine length. Bind a flat
                // curve on a scratch ANIMATOR parameter (path="", typeof(Animator)) — NOT a non-existent
                // GameObject path. An animator-property binding resolves against any avatar root (the root
                // always carries an Animator), so CheckAnimator's broken-binding rule stays clean when the
                // emitted controller is linted against a real avatar; a fake GO path would false-FAIL it.
                // DECLARING the carrier param is a CONTROLLER side effect and is left to the caller.
                if (!spec.Seconds.HasValue)
                    throw new EmitException(
                        $"clip '{spec.Name}' declares no set/curves and no seconds — nothing to emit (parser/validator should have caught this)");
                var carrier = EditorCurveBinding.FloatCurve("", typeof(Animator), ReservedNames.CarrierParam);
                var flat = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(spec.Seconds.Value, 0f));
                AnimationUtility.SetEditorCurve(clip, carrier, flat);
                return clip;
            }

            float length = spec.Seconds ?? CurveLength(spec);
            foreach (var kv in spec.Sets)
            {
                var binding = ResolveBinding(kv.Key, paramNames);
                AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, length, kv.Value));
            }
            foreach (var cs in spec.Curves)
                SetKeyedCurve(clip, spec, cs, paramNames);
            return clip;
        }

        // Author one keyframed curve onto `clip`. Split out of BuildClipContent verbatim (unchanged logic).
        private static void SetKeyedCurve(AnimationClip clip, ClipSpec spec, CurveSpec cs, HashSet<string> paramNames)
        {
            var binding = ResolveBinding(cs.Binding, paramNames);
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
            var animCurve = new AnimationCurve(keys.ToArray());
            if (cs.Tangents == CurveTangent.Linear)
                for (int i = 0; i < animCurve.length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(animCurve, i, AnimationUtility.TangentMode.Linear);
                    AnimationUtility.SetKeyRightTangentMode(animCurve, i, AnimationUtility.TangentMode.Linear);
                }
            AnimationUtility.SetEditorCurve(clip, binding, animCurve);
        }

        private static float CurveLength(ClipSpec spec)
        {
            float max = 0f; bool any = false;
            foreach (var cs in spec.Curves)
                foreach (var k in cs.Keys) { if (k.Time > max) max = k.Time; any = true; }
            return any ? max : MinClipLength;
        }

        // A bare identifier naming a declared Animator parameter → an Animator-parameter curve (path="").
        // Otherwise the general scene binding: "path/Component.property", split on the LAST '/' then
        // the FIRST '.'. The first dot is the right split because a component type name never contains a
        // dot, while the property CAN — `blendShape.Smile`, `material._Color` are the common cases a
        // LastIndexOf split would fold into the type name (and then fail to resolve).
        private static EditorCurveBinding ResolveBinding(string target, HashSet<string> paramNames)
        {
            if (paramNames.Contains(target))
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

        // The authored binding vocabulary (docs/animator-schema.md §clips): simple names resolve in exactly
        // these namespaces. Namespaces, not a type list, so a pinned-SDK addition inside a covered family
        // needs no code change — while UI / TMP / arbitrary scripts stay out of scope by construction.
        private static readonly string[] BindingNamespaces =
        {
            "UnityEngine",
            "UnityEngine.Animations",
            "VRC.SDK3.Dynamics.Constraint.Components",
            "VRC.SDK3.Dynamics.Contact.Components",
            "VRC.SDK3.Dynamics.PhysBone.Components",
        };

        // SCOPE (item V, revisited by C1): the original UnityEngine-only ruling is NARROWED, not overturned —
        // resolution is still a closed, documented allowlist (of namespaces), refusing everything else
        // fail-loud, including a non-Component type and a name matching in two namespaces (refuse, never
        // rank). ControllerDecompile.ReconstructBindingTarget round-trips through THIS method as its oracle,
        // so emit and decompile cannot drift; widen the list AND the schema §clips together — never one
        // silently.
        // Memo: emit and decompile both resolve once per binding, and the probe walks every loaded assembly
        // per namespace — a large FX pays it hundreds of times. Successes only (a refusal aborts the compile,
        // so its path is never hot); statics reset on domain reload, so the cache cannot outlive the assembly
        // set it was built from.
        private static readonly Dictionary<string, Type> ResolveCache = new Dictionary<string, Type>();

        internal static Type ResolveComponentType(string typeName)
        {
            if (typeName == "GameObject") return typeof(GameObject); // animatable but not a Component

            // Bare MonoBehaviour is Unity's missing-script placeholder: a deserialized curve whose script
            // no longer resolves reads back as typeof(MonoBehaviour) (measured, 2022.3) — never a real
            // authorable target. Resolving it would round-trip vendor junk silently; refuse by name so the
            // decompile oracle turns a missing-script binding into a named refusal.
            if (typeName == "MonoBehaviour")
                throw new EmitException("clip binding component type 'MonoBehaviour' is not bindable — it is "
                    + "the placeholder Unity reports for a curve whose script cannot be resolved (missing script); "
                    + "bind the concrete component type");

            if (ResolveCache.TryGetValue(typeName, out var cached)) return cached;

            var matches = new List<Type>();
            foreach (var ns in BindingNamespaces)
            {
                string full = ns + "." + typeName;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType(full);
                    if (t != null && typeof(Component).IsAssignableFrom(t)) { matches.Add(t); break; }
                }
            }
            if (matches.Count == 1) { ResolveCache[typeName] = matches[0]; return matches[0]; }
            if (matches.Count > 1)
                throw new EmitException($"clip binding component type '{typeName}' is ambiguous across the "
                    + "supported namespaces: " + string.Join(", ", matches.Select(m => m.FullName))
                    + " — refused rather than guessed (the binding grammar has no qualified form; this needs a resolver decision)");
            throw new EmitException($"clip binding component type '{typeName}' does not resolve to an "
                + "animatable Component in the supported namespaces (" + string.Join(", ", BindingNamespaces)
                + ") — UI / arbitrary scripts are out of scope; check the name against the schema §clips vocabulary");
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
                EmitVrcParameters();  // in-memory only; CompileController persists

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

            // Embedded emission: build each clip's content (controller-free, via the shared
            // ControllerEmit.BuildClipContent) then attach it as a hidden controller sub-asset and record it.
            // The controller-side responsibilities stay here: declaring the carrier param for a seconds-only
            // clip, hiding + embedding the sub-asset. BuildClipContent produces byte-identical curves.
            private void EmitClips()
            {
                foreach (var spec in _doc.Clips)
                {
                    bool secondsOnly = spec.Sets.Count == 0 && spec.Curves.Count == 0;
                    if (secondsOnly) EnsureCarrierParam();               // controller declares _CompilerNull
                    var clip = ControllerEmit.BuildClipContent(spec, _paramNames);
                    clip.hideFlags = HideFlags.HideInHierarchy;          // embedded sub-asset stays hidden
                    AssetDatabase.AddObjectToAsset(clip, _controller);
                    _result.Clips[spec.Name] = clip;
                    _result.ClipList.Add(clip);
                }
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

                    // Pass 1: emit states + sub-machines (arbitrary depth), building the per-machine scope tree
                    // (each scope holds only its OWN direct states + direct sub-machines; see MachineScope for
                    // why target resolution is per-scope, not layer-global).
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
                    var pos = NodePos(model.Layout, s.Name) ?? GridState(i); // authored layout, else the grid
                    var ast = target.AddState(s.Name, pos);
                    ast.writeDefaultValues = s.WriteDefaults ?? layer.WriteDefaults ?? _doc.Defaults.WriteDefaults;
                    ast.speed = s.Speed;
                    if (!string.IsNullOrEmpty(s.SpeedParam)) { ast.speedParameterActive = true; ast.speedParameter = s.SpeedParam; }
                    if (!string.IsNullOrEmpty(s.MotionTimeParam)) { ast.timeParameterActive = true; ast.timeParameter = s.MotionTimeParam; }
                    ast.mirror = s.Mirror;
                    if (s.Motion != null) ast.motion = BuildMotion(s.Motion, AutoTreeName(s.Name), s.Name);
                    EmitBehaviours(s.Behaviours, t => ast.AddStateMachineBehaviour(t));
                    scope.States[s.Name] = ast;
                }

                var machines = model.Machines;
                for (int i = 0; i < machines.Count; i++)
                {
                    var sub = machines[i];
                    var pos = NodePos(model.Layout, sub.Name) ?? GridSub(i); // authored layout, else the grid
                    var childSm = target.AddStateMachine(sub.Name, pos);
                    childSm.hideFlags = HideFlags.HideInHierarchy;
                    scope.Subs[sub.Name] = EmitMachine(layer, sub.Machine, childSm);
                }

                // Special-node positions are ALWAYS written (tool-owned): authored layout values when present,
                // else the shared constants — so decompile has a deterministic baseline. parentStateMachinePosition
                // on a layer-root machine is a harmless no-op (root has no up-node); kept unconditional for simplicity.
                var lay = model.Layout;
                target.entryPosition              = SpecialPos(lay?.Entry,  SpecialEntry);
                target.anyStatePosition           = SpecialPos(lay?.Any,    SpecialAny);
                target.exitPosition               = SpecialPos(lay?.Exit,   SpecialExit);
                target.parentStateMachinePosition = SpecialPos(lay?.Parent, SpecialParent);

                // A state and a sub-machine of the same name are both addressable by bare name, but ResolveName
                // (and the default lookup) resolve states first — the sub-machine would be unreachable and a
                // `default:` ambiguous. Fail loud on the ambiguous document rather than silently favour the state.
                foreach (var subName in scope.Subs.Keys)
                    if (scope.States.ContainsKey(subName))
                        throw new EmitException($"machine '{target.name}': a state and a sub-machine are both named '{subName}' — a bare target or default can't address both (states resolve first)");

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
                // default is a bare LOCAL name (a direct state or sub-machine); unescape it before lookup.
                bool defaultIsState = false;
                string defaultName = string.IsNullOrEmpty(model.DefaultState) ? null : AddressPath.UnescapeSegment(model.DefaultState);
                if (defaultName != null)
                {
                    if (scope.States.TryGetValue(defaultName, out var def)) { target.defaultState = def; defaultIsState = true; }
                    else if (!scope.Subs.ContainsKey(defaultName))
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
                if (!defaultIsState && defaultName != null)
                    target.AddEntryTransition(scope.Subs[defaultName].Target);

                foreach (var sub in model.Machines)
                    WireMachine(layer, sub.Machine, scope.Subs[sub.Name], root);
            }

            private static void RequireTargetName(Transition t, string fromMachine)
            {
                if (t.ToExit || string.IsNullOrEmpty(t.To))
                    throw new EmitException($"entry/any ladder transition in machine '{fromMachine}' has no target (Exit is not a valid target there)");
            }

            // Resolve a transition target to a state (returns true) or a sub-machine (returns false). Three
            // addressing forms, disjoint by shape: a BARE name (no '/') resolves ONLY in `scope` — the
            // referencing machine's own direct states + direct sub-machines. A leading-'/' name is ABSOLUTE
            // from the LAYER `root` (the only way to reach a TOP-LEVEL entity, whose root path is a single bare
            // segment that would otherwise read as local). Any other slash-qualified name is a path from
            // `root`. An unresolved target is fail-loud, naming the offender and the machine it was referenced
            // from — NEVER a silent global fallback.
            private bool ResolveName(string name, MachineScope scope, MachineScope root, string fromMachine,
                out AnimatorState toState, out AnimatorStateMachine toSm)
            {
                toState = null; toSm = null;

                if (name.Length > 0 && name[0] == '/')
                    return ResolveFromRoot(name.Substring(1), root, fromMachine, name, out toState, out toSm);

                // Split on UNescaped '/' — a single segment is a bare LOCAL name (its own '/' survives as '\/').
                var segs = AddressPath.Split(name);
                if (segs.Count == 1)
                {
                    string local = AddressPath.UnescapeSegment(segs[0]);
                    if (scope.States.TryGetValue(local, out toState)) return true;
                    if (scope.Subs.TryGetValue(local, out var sub)) { toSm = sub.Target; return false; }
                    throw new EmitException($"transition target '{name}' not found in machine '{fromMachine}' — a bare name resolves only within its own machine; use a 'Sub/State' path or a '/Top' absolute address for a cross-machine target");
                }

                return ResolveFromRoot(name, root, fromMachine, name, out toState, out toSm);
            }

            // Resolve a root-relative path: every non-final segment is a sub-machine, the final segment a state
            // or a sub-machine. A single segment addresses a direct child of the layer root; an empty path (bare
            // '/') addresses the layer root itself. Segments split on UNescaped '/' then unescape ('\/'->'/',
            // '\\'->'\'). `display` is the original authored token (may carry a leading '/') for error messages.
            private bool ResolveFromRoot(string path, MachineScope root, string fromMachine, string display,
                out AnimatorState toState, out AnimatorStateMachine toSm)
            {
                toState = null; toSm = null;
                if (path.Length == 0) { toSm = root.Target; return false; }
                var segs = AddressPath.Split(path);
                var cur = root;
                for (int i = 0; i < segs.Count - 1; i++)
                {
                    string seg = AddressPath.UnescapeSegment(segs[i]);
                    if (!cur.Subs.TryGetValue(seg, out cur))
                        throw new EmitException($"transition target path '{display}' (from machine '{fromMachine}'): segment '{seg}' is not a sub-machine on the path from the layer root");
                }
                var leaf = AddressPath.UnescapeSegment(segs[segs.Count - 1]);
                if (cur.States.TryGetValue(leaf, out toState)) return true;
                if (cur.Subs.TryGetValue(leaf, out var subm)) { toSm = subm.Target; return false; }
                throw new EmitException($"transition target path '{display}' (from machine '{fromMachine}'): final segment '{leaf}' is neither a state nor a sub-machine");
            }

            private void ConfigureStateTransition(AnimatorStateTransition tr, Transition t)
            {
                if (t.ExitTime.HasValue) { tr.hasExitTime = true; tr.exitTime = t.ExitTime.Value; }
                else tr.hasExitTime = _doc.Defaults.TransitionHasExitTime;
                tr.duration = t.Duration ?? _doc.Defaults.TransitionDuration;
                tr.hasFixedDuration = t.FixedDuration ?? true;
                tr.interruptionSource = MapInterruption(t.Interruption ?? _doc.Defaults.Interruption);
                tr.orderedInterruption = t.OrderedInterruption ?? true;
                tr.mute = t.Mute;
                tr.solo = t.Solo;
                if (!string.IsNullOrEmpty(t.Name)) tr.name = t.Name;
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
                // A human-authored spec.Name overrides the positional default; children then derive from the
                // APPLIED name, not the incoming positional one — otherwise a named parent's children would
                // decompile as explicit gibberish (they'd carry the OLD auto default forever).
                var applied = spec.Name ?? name;
                var bt = new BlendTree { name = applied, hideFlags = HideFlags.HideInHierarchy };
                bt.blendType = MapTreeKind(spec.Kind);
                // A fresh 1D tree defaults to useAutomaticThresholds=true, which makes Unity OVERWRITE our
                // explicit per-child thresholds with even spacing (0, 1/n, …). Disable it BEFORE adding children
                // so the authored thresholds stick. The schema stores threshold VALUES (what runtime uses), so
                // an originally-automatic tree round-trips as manual with identical values — lossless in effect.
                if (spec.Kind == TreeKind.OneD) bt.useAutomaticThresholds = false;
                if (!string.IsNullOrEmpty(spec.Param)) bt.blendParameter = spec.Param;
                if (!string.IsNullOrEmpty(spec.ParamY)) bt.blendParameterY = spec.ParamY;
                // Direct-tree "Normalized Blend Values" — no typed API, so set the serialized field. Only when
                // the document specifies it; otherwise leave Unity's construction default.
                if (spec.Kind == TreeKind.Direct && spec.Normalized.HasValue)
                    using (var so = new SerializedObject(bt))
                    {
                        so.FindProperty("m_NormalizedBlendValues").boolValue = spec.Normalized.Value;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                AssetDatabase.AddObjectToAsset(bt, _controller);
                _result.Trees.Add(bt);

                for (int i = 0; i < spec.Children.Count; i++)
                {
                    var child = spec.Children[i];
                    var childMotion = BuildMotion(child.Motion, AutoChildName(applied, i), stateContext);
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
            // One encoder per kind: materialise the concrete VRC SMB via `add(typeof(...))`, then a per-field
            // switch that sets its fields and throws EmitException (named offender) on any unknown field.
            // The enum-token casing is DEFINED HERE (the token→enum maps below) — camelCase (e.g. `animation`),
            // NOT the PascalCase an enum ToString() (as in ReportController's Markdown) would give.
            // ControllerDecompile builds its reverse (enum→token) maps from these, so this is the single source
            // of truth for both directions.
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
                        case DriverKeys.LocalOnly:
                            drv.localOnly = AsBool(kv.Value, "driver.localOnly");
                            break;
                        case DriverKeys.Set:
                            foreach (var e in AsMap(kv.Value, "driver.set"))
                                ps.Add(new Driver.Parameter { type = Driver.ChangeType.Set, name = e.Key, value = AsFloat(e.Value, "driver.set." + e.Key) });
                            break;
                        case DriverKeys.Add:
                            foreach (var e in AsMap(kv.Value, "driver.add"))
                                ps.Add(new Driver.Parameter { type = Driver.ChangeType.Add, name = e.Key, value = AsFloat(e.Value, "driver.add." + e.Key) });
                            break;
                        case DriverKeys.Copy:
                            foreach (var e in AsMap(kv.Value, "driver.copy"))
                                ps.Add(BuildCopy(e.Key, e.Value));
                            break;
                        case DriverKeys.Random:
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
                        case DriverKeys.Source: p.source = AsString(kv.Value, "driver.copy." + dest + ".source"); break;
                        case DriverKeys.SourceMin: p.sourceMin = AsFloat(kv.Value, "driver.copy." + dest + ".sourceMin"); range = true; break;
                        case DriverKeys.SourceMax: p.sourceMax = AsFloat(kv.Value, "driver.copy." + dest + ".sourceMax"); range = true; break;
                        case DriverKeys.DestMin: p.destMin = AsFloat(kv.Value, "driver.copy." + dest + ".destMin"); range = true; break;
                        case DriverKeys.DestMax: p.destMax = AsFloat(kv.Value, "driver.copy." + dest + ".destMax"); range = true; break;
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
                        case DriverKeys.Min: p.valueMin = AsFloat(kv.Value, "driver.random." + name + ".min"); break;
                        case DriverKeys.Max: p.valueMax = AsFloat(kv.Value, "driver.random." + name + ".max"); break;
                        case DriverKeys.Chance: p.chance = AsFloat(kv.Value, "driver.random." + name + ".chance"); break;
                        default: throw new EmitException($"driver.random.{name}: unknown field '{kv.Key}'");
                    }
                }
                return p;
            }

            // tracking: { <channel>: <state> } — channel is one of the ten VRC tracking channels, state is
            // animation|tracking|noChange (tokens defined by TrackingTokens below). An untouched channel keeps
            // its SDK default (NoChange).
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

            // playAudio: the VRCAnimatorPlayAudio serializable surface. The AudioSource is addressed by
            // hierarchy path (sourcePath) — the SDK resolves the live Source at runtime; clips are asset paths.
            // Volume/pitch are [min, max] ranges.
            private static void PopulatePlayAudio(VRCAnimatorPlayAudio c, Dictionary<string, object> fields)
            {
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case PlayAudioKeys.SourcePath: c.SourcePath = AsString(kv.Value, "playAudio.sourcePath"); break;
                        case PlayAudioKeys.PlaybackOrder: c.PlaybackOrder = ParseEnumToken(kv.Value, AudioOrderTokens, "playAudio.playbackOrder"); break;
                        case PlayAudioKeys.Parameter: c.ParameterName = AsString(kv.Value, "playAudio.parameter"); break;
                        case PlayAudioKeys.Volume: c.Volume = AsVector2(kv.Value, "playAudio.volume"); break;
                        case PlayAudioKeys.VolumeApply: c.VolumeApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.volumeApply"); break;
                        case PlayAudioKeys.Pitch: c.Pitch = AsVector2(kv.Value, "playAudio.pitch"); break;
                        case PlayAudioKeys.PitchApply: c.PitchApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.pitchApply"); break;
                        case PlayAudioKeys.Loop: c.Loop = AsBool(kv.Value, "playAudio.loop"); break;
                        case PlayAudioKeys.LoopApply: c.LoopApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.loopApply"); break;
                        case PlayAudioKeys.Clips: c.Clips = AsClips(kv.Value, "playAudio.clips"); break;
                        case PlayAudioKeys.ClipsApply: c.ClipsApplySettings = ParseEnumToken(kv.Value, AudioApplyTokens, "playAudio.clipsApply"); break;
                        case PlayAudioKeys.DelaySeconds: c.DelayInSeconds = AsFloat(kv.Value, "playAudio.delaySeconds"); break;
                        case PlayAudioKeys.PlayOnEnter: c.PlayOnEnter = AsBool(kv.Value, "playAudio.playOnEnter"); break;
                        case PlayAudioKeys.StopOnEnter: c.StopOnEnter = AsBool(kv.Value, "playAudio.stopOnEnter"); break;
                        case PlayAudioKeys.PlayOnExit: c.PlayOnExit = AsBool(kv.Value, "playAudio.playOnExit"); break;
                        case PlayAudioKeys.StopOnExit: c.StopOnExit = AsBool(kv.Value, "playAudio.stopOnExit"); break;
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
                // A non-integral double here is malformed input (e.g. layerControl.layer: 1.5) — fail loud
                // rather than silently rounding it to a different layer index.
                case double d:
                    if (d != Math.Floor(d))
                        throw new EmitException($"{ctx}: expected an integer, got non-integral {d.ToString(CultureInfo.InvariantCulture)}");
                    return (int)d;
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

        // INTERNAL, so ControllerDecompile builds its reverse (enum→token) maps FROM these — one source of
        // truth. A new SDK enum member added here is automatically decodable; a value present in neither
        // direction is fail-loud on both sides (emit throws; decode refuses), never silently approximated.
        internal static readonly Dictionary<string, TrackingType> TrackingTokens = new Dictionary<string, TrackingType>
        {
            { "noChange", TrackingType.NoChange }, { "tracking", TrackingType.Tracking }, { "animation", TrackingType.Animation },
        };

        internal static readonly Dictionary<string, PlayableLayer> PlayableLayerTokens = new Dictionary<string, PlayableLayer>
        {
            { "action", PlayableLayer.Action }, { "fx", PlayableLayer.FX }, { "gesture", PlayableLayer.Gesture }, { "additive", PlayableLayer.Additive },
        };

        internal static readonly Dictionary<string, LayerCtrlLayer> LayerCtrlTokens = new Dictionary<string, LayerCtrlLayer>
        {
            { "action", LayerCtrlLayer.Action }, { "fx", LayerCtrlLayer.FX }, { "gesture", LayerCtrlLayer.Gesture }, { "additive", LayerCtrlLayer.Additive },
        };

        internal static readonly Dictionary<string, AudioOrder> AudioOrderTokens = new Dictionary<string, AudioOrder>
        {
            { "random", AudioOrder.Random }, { "uniqueRandom", AudioOrder.UniqueRandom }, { "roundabout", AudioOrder.Roundabout }, { "parameter", AudioOrder.Parameter },
        };

        internal static readonly Dictionary<string, AudioApply> AudioApplyTokens = new Dictionary<string, AudioApply>
        {
            { "alwaysApply", AudioApply.AlwaysApply }, { "applyIfStopped", AudioApply.ApplyIfStopped }, { "neverApply", AudioApply.NeverApply },
        };

        // Field-name tokens for the two field-dense behaviour kinds — SHARED with ControllerDecompile so the
        // encode case labels and the decode dictionary keys can never drift apart (a rename breaks both, and
        // the compiler enforces it). Other kinds' field names are few + stable and stay inline.
        internal static class DriverKeys
        {
            public const string LocalOnly = "localOnly", Set = "set", Add = "add", Copy = "copy", Random = "random";
            public const string Source = "source", SourceMin = "sourceMin", SourceMax = "sourceMax", DestMin = "destMin", DestMax = "destMax";
            public const string Min = "min", Max = "max", Chance = "chance";
        }

        internal static class PlayAudioKeys
        {
            public const string SourcePath = "sourcePath", PlaybackOrder = "playbackOrder", Parameter = "parameter";
            public const string Volume = "volume", VolumeApply = "volumeApply", Pitch = "pitch", PitchApply = "pitchApply";
            public const string Loop = "loop", LoopApply = "loopApply", Clips = "clips", ClipsApply = "clipsApply";
            public const string DelaySeconds = "delaySeconds", PlayOnEnter = "playOnEnter", StopOnEnter = "stopOnEnter";
            public const string PlayOnExit = "playOnExit", StopOnExit = "stopOnExit";
        }

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
