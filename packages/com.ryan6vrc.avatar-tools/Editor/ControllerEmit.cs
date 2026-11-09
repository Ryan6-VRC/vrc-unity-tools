using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
#if VRC_FT_SETS_PRESENT
using FtSets = VRC.SDKBase.VRC_AnimatorFaceTrackingBlendshapesSet;
#endif

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Codegen core: turns a parsed <see cref="AnimDocument"/> into a persisted <see cref="AnimatorController"/>
    /// with embedded clips and blend trees, plus in-memory <see cref="VRCExpressionParameters"/> and menu assets.
    /// Language: docs/animator-schema.md.
    ///
    /// A pure function of the document: authored layout else a fixed grid keyed by document order, names from
    /// the document, no random identity. Recompiling the same document gives Git no churn.
    ///
    /// The controller is persisted before anything attaches to it, because
    /// <c>AnimatorState.AddStateMachineBehaviour</c> and sub-asset embedding need a persisted asset. Reset in
    /// place: <see cref="Build(AnimDocument, string, string, out EmitResult)"/>.
    /// </summary>
    public static class ControllerEmit
    {
        // The floor on a key-derived clip length: one frame at the clips' 60fps. A zero-length clip gets an
        // effective 1s state length (docs/animator-schema.md §clips).
        private const float MinClipLength = 1f / 60f;

        private const string ScratchRoot = "Assets/Agent/Scratch/emit";

        // The compiler owns every node position, the state/sub-machine grid and the four special nodes, so
        // DecompileController has a deterministic baseline; a value Unity assigns at node creation is
        // version-dependent.
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

        internal static string AutoTreeName(string stateName) => stateName + "_BlendTree";
        internal static string AutoChildName(string parentAppliedName, int index) => parentAppliedName + "_" + index;

        public sealed class EmitResult
        {
            public AnimatorController Controller;
            public Dictionary<string, AnimationClip> Clips = new Dictionary<string, AnimationClip>();
            public List<AnimationClip> ClipList = new List<AnimationClip>();
            public List<BlendTree> Trees = new List<BlendTree>();
            public VRCExpressionParameters Params; // null only when every declared param is excluded (built-in / scratch)
            // Null unless the document carries a `menu:` block. MenuChildren holds the sub-menu pages, which
            // CompileController persists as sub-assets of the root's file (docs/animator-schema.md §menu).
            public VRCExpressionsMenu Menu;
            public List<VRCExpressionsMenu> MenuChildren = new List<VRCExpressionsMenu>();
            // Refs marked `unresolved: true` that did not resolve: emitted as a null motion and recorded by owning
            // state and verbatim GUID for a compile advisory. A bare broken ref throws instead.
            public List<(string state, string guid)> UnresolvedRefs = new List<(string state, string guid)>();
            // Menu icons emitted null because the document is outside this project's AssetDatabase, recorded for a
            // compile advisory. Each entry names the control and the path tried. See ResolveIcon.
            public List<(string control, string path)> IconsOutsideProject = new List<(string control, string path)>();
            // Non-null when the provenance stamp did not reach the .controller's .meta on disk; the next compile of
            // this path reads it as hand-authored and warns. CompileController surfaces it. StampProvenance has why
            // this is not a compile failure.
            public string StampFailure;

            /// <summary>Destroy the side assets this result still owns in memory: <see cref="Params"/>,
            /// <see cref="Menu"/> and every <see cref="MenuChildren"/> page. The emit hands them back unpersisted, so
            /// the caller has to end their life.
            /// <para>A survivor is not inert. Leaked SDK ScriptableObjects in this assembly make
            /// <c>AddStateMachineBehaviour</c> start returning null, failing unrelated suites with
            /// NullReferenceExceptions that point at untouched production code (measured: 551/600 tests pass with
            /// the leak, 600/600 without).</para>
            /// <para>One-arg <c>DestroyImmediate</c> with no asset-path guard, on purpose: these are never persisted,
            /// so if that ever breaks Unity errors instead of deleting a real asset.</para></summary>
            public void DestroyUnpersisted()
            {
                if (Params != null) UnityEngine.Object.DestroyImmediate(Params);
                Params = null;
                if (Menu != null) UnityEngine.Object.DestroyImmediate(Menu);
                Menu = null;
                if (MenuChildren != null)
                {
                    foreach (var child in MenuChildren)
                        if (child != null) UnityEngine.Object.DestroyImmediate(child);
                    MenuChildren.Clear();
                }
            }
        }

        public sealed class EmitException : Exception
        {
            public EmitException(string message) : base(message) { }
        }

        /// <summary>Emit to the scratch dir with no provenance stamp, for throwaway builds.
        /// <para>The caller owns the result's side assets and must call
        /// <see cref="EmitResult.DestroyUnpersisted"/> when done; this door persists only the
        /// controller.</para></summary>
        public static void Build(AnimDocument doc, out EmitResult result)
            => Build(doc, ScratchRoot, null, out result);

        /// <summary>Emit <paramref name="doc"/> into <paramref name="outDir"/> (an <c>Assets/…</c>-relative
        /// folder). <paramref name="sourceText"/>, when non-null, is the exact source-file text, hashed into the
        /// controller's provenance userData so a later compile can detect an out-of-band edit.
        /// <para>Null <paramref name="sourceText"/> writes no stamp and leaves any existing one untouched, so it is
        /// for scratch targets only, never a path <see cref="CompileController"/> will later compile to: there the
        /// stale stamp survives this emit, the next compile reads stored == current, and it clobbers an out-of-band
        /// hand-edit with no warning.</para>
        /// An existing controller at the target path is reset in place (its sub-assets stripped and rebuilt)
        /// rather than deleted and recreated, so its GUID and any external reference to it survive a
        /// recompile.</summary>
        public static void Build(AnimDocument doc, string outDir, string sourceText, out EmitResult result)
        {
            if (doc == null) throw new EmitException("Build: document is null");
            if (string.IsNullOrEmpty(doc.ControllerName))
                throw new EmitException("Build: document has no controller name");
            if (string.IsNullOrEmpty(outDir)) throw new EmitException("Build: outDir is empty");

            var ctx = new BuildContext(doc, outDir, sourceText);
            result = ctx.Run();
        }

        // ----- detached clip content (shared by the embedded EmitClips and CompileClips) -----

        /// <summary>Author a detached <see cref="AnimationClip"/> (curves and settings only) for
        /// <paramref name="spec"/>. Embedding it, hiding it and declaring a seconds-only clip's carrier param are the
        /// caller's. <paramref name="paramNames"/> is the set of declared Animator parameter names, the gate that
        /// routes a bare binding to an Animator-property curve (path="") rather than a scene binding.</summary>
        internal static AnimationClip BuildClipContent(ClipSpec spec, HashSet<string> paramNames)
        {
            var clip = new AnimationClip { name = spec.Name, frameRate = 60f };
            bool hasContent = spec.Sets.Count > 0 || spec.Curves.Count > 0;

            if (!hasContent)
            {
                // Seconds-only: a flat curve on a scratch Animator parameter gives the clip its length. An
                // animator-property binding resolves against any avatar root, so CheckAnimator's broken-binding rule
                // stays clean when the controller is linted against a real avatar; a fake GameObject path would
                // false-FAIL it.
                if (!spec.Seconds.HasValue)
                    throw new EmitException(
                        $"clip '{spec.Name}' declares no set/curves and no seconds — nothing to emit (parser/validator should have caught this)");
                var carrier = EditorCurveBinding.FloatCurve("", typeof(Animator), ReservedNames.CarrierParam);
                var flat = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(spec.Seconds.Value, 0f));
                AnimationUtility.SetEditorCurve(clip, carrier, flat);
                ApplyLoop(clip, spec);
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
            ApplyLoop(clip, spec);
            return clip;
        }

        private static void ApplyLoop(AnimationClip clip, ClipSpec spec)
        {
            if (!spec.Loop) return;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        private static void SetKeyedCurve(AnimationClip clip, ClipSpec spec, CurveSpec cs, HashSet<string> paramNames)
        {
            var binding = ResolveBinding(cs.Binding, paramNames);
            var keys = cs.Keys.Select(k => new Keyframe(k.Time, k.Value)).ToList();
            // Unity derives a keyframed clip's length from its last key, so an explicit `seconds:` must be stamped
            // onto the curve or it is silently ignored. With no `seconds:`, hold to this curve's own last key floored
            // at MinClipLength: per curve, never the clip-wide max, so a clip mixing a long and a short curve emits
            // both as authored.
            if (keys.Count > 0)
            {
                var last = keys[0];
                foreach (var k in keys) if (k.time > last.time) last = k;
                if (spec.Seconds.HasValue && spec.Seconds.Value < last.time)
                    throw new EmitException(
                        $"clip '{spec.Name}': seconds={spec.Seconds.Value} is shorter than curve '{cs.Binding}' last key at {last.time}");
                float hold = spec.Seconds ?? Mathf.Max(last.time, MinClipLength);
                if (hold > last.time)
                    keys.Add(new Keyframe(hold, last.value));
            }
            var animCurve = new AnimationCurve(keys.ToArray());
            var mode = cs.Tangents switch
            {
                CurveTangent.Linear  => (AnimationUtility.TangentMode?)AnimationUtility.TangentMode.Linear,
                CurveTangent.Stepped => AnimationUtility.TangentMode.Constant,
                _                    => null,   // Flat: leave the default keyframe tangents (zero) untouched
            };
            if (mode.HasValue)
                for (int i = 0; i < animCurve.length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(animCurve, i, mode.Value);
                    AnimationUtility.SetKeyRightTangentMode(animCurve, i, mode.Value);
                }
            AnimationUtility.SetEditorCurve(clip, binding, animCurve);
        }

        private static float CurveLength(ClipSpec spec)
        {
            float max = 0f;
            foreach (var cs in spec.Curves)
                foreach (var k in cs.Keys) if (k.Time > max) max = k.Time;
            return Mathf.Max(max, MinClipLength);
        }

        // A bare identifier naming a declared Animator parameter is an Animator-parameter curve (path="").
        // Otherwise it is "path/Component.property", split on the last '/' then the first '.': a component type
        // name never contains a dot, while a property can (`blendShape.Smile`, `material._Color`).
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
                    $"clip binding '{target}' is neither a declared Animator parameter nor a 'path/Component.property' scene binding");
            string typeName = compProp.Substring(0, dot);
            string prop = compProp.Substring(dot + 1);
            return EditorCurveBinding.FloatCurve(path, ResolveComponentType(typeName), prop);
        }

        // The authored binding vocabulary: simple names resolve in exactly these namespaces. Widen this list and
        // docs/animator-schema.md §clips together.
        private static readonly string[] BindingNamespaces =
        {
            "UnityEngine",
            "UnityEngine.Animations",
            "VRC.SDK3.Dynamics.Constraint.Components",
            "VRC.SDK3.Dynamics.Contact.Components",
            "VRC.SDK3.Dynamics.PhysBone.Components",
        };

        // ControllerDecompile.ReconstructBindingTarget round-trips through ResolveComponentType as its oracle.
        // Memo: the probe walks every loaded assembly per namespace. Successes only; a domain reload clears it.
        private static readonly Dictionary<string, Type> ResolveCache = new Dictionary<string, Type>();

        internal static Type ResolveComponentType(string typeName)
        {
            if (typeName == "GameObject") return typeof(GameObject); // animatable but not a Component

            // Unity reports a curve whose script no longer resolves as typeof(MonoBehaviour) (measured). Refused by
            // name so the decompile oracle turns a missing-script binding into a named refusal, not a silent
            // round-trip.
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

        private sealed class BuildContext
        {
            private readonly AnimDocument _doc;
            private readonly string _outDir;
            private readonly string _sourceText;   // exact source bytes for provenance; null ⇒ no stamp written
            private readonly EmitResult _result = new EmitResult();
            private readonly HashSet<string> _paramNames;
            private AnimatorController _controller;
            // Every menu page this build created, registered at creation so a mid-build throw can destroy pages
            // not yet in _result. Failure path only.
            private readonly List<VRCExpressionsMenu> _menuPages = new List<VRCExpressionsMenu>();
            // The `trees:` entries by name and their built BlendTrees: built on first reference, and every later
            // reference shares that object. _sharedBuilding is the in-progress set, which catches a cycle.
            private readonly Dictionary<string, BlendTreeSpec> _sharedSpecs;
            private readonly Dictionary<string, BlendTree> _sharedBuilt = new Dictionary<string, BlendTree>();
            private readonly List<string> _sharedBuilding = new List<string>();

            public BuildContext(AnimDocument doc, string outDir, string sourceText)
            {
                _doc = doc;
                _outDir = outDir;
                _sourceText = sourceText;
                // Every declared parameter, not only `aap` ones: a clip writing a bare parameter name binds it as an
                // Animator float curve, which is how VRChat drives any Animator parameter from a clip.
                _paramNames = new HashSet<string>(doc.Parameters.Select(p => p.Name));
                _sharedSpecs = new Dictionary<string, BlendTreeSpec>();
                foreach (var t in doc.Trees) if (t != null && t.Name != null) _sharedSpecs[t.Name] = t;
            }

            public EmitResult Run()
            {
                string path = PathFor(_outDir, _doc.ControllerName);
                EnsureFolder(_outDir);

                // StripSubAssets empties any prior controller at `path` before the fallible emit steps run, so Build
                // alone does not protect a prior asset. CompileController's proof compile does; direct 2-arg callers
                // target disposable scratch.
                //
                // A bare `new AnimatorController()` persists with zero layers; CreateAnimatorControllerAtPath would
                // add a "Base Layer" to orphan.
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

                // Params and the menu pages are in-memory ScriptableObjects that a throw out of Build would strand:
                // `result` is never assigned, so no caller has a handle. EmitResult.DestroyUnpersisted has why a leak
                // matters. The controller and its sub-assets are persisted and are not swept.
                try
                {
                    EmitParameters();
                    EmitClips();          // clips first: states/trees reference them by name
                    EmitLayers();
                    EmitVrcParameters();  // in-memory only; CompileController persists
                    EmitMenu();           // in-memory only; CompileController persists

                    EditorUtility.SetDirty(_controller);
                    AssetDatabase.SaveAssetIfDirty(_controller);

                    StampProvenance(path);

                    // Reload from disk so EmitResult holds the persisted objects, exactly what round-trip
                    // verification sees.
                    ReloadFromDisk(path);
                    return _result;
                }
                catch
                {
                    DestroyUnpersistedSideAssets();
                    throw;
                }
            }

            // Failure path only; on success the caller owns these through EmitResult.DestroyUnpersisted. Pages come
            // from _menuPages because _result.Menu and _result.MenuChildren hold only fully built pages, so calling
            // the result-based door here would leak every partially built page.
            private void DestroyUnpersistedSideAssets()
            {
                if (_result.Params != null) UnityEngine.Object.DestroyImmediate(_result.Params);
                _result.Params = null;
                foreach (var page in _menuPages)
                    if (page != null) UnityEngine.Object.DestroyImmediate(page);
                _menuPages.Clear();
                _result.Menu = null;
                _result.MenuChildren.Clear();
            }

            // Clear the controller's references before destroying the sub-assets they point at. The controller
            // object and its GUID are kept.
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
                        default: // Float; AAP params are ordinary Floats.
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
                    bool secondsOnly = spec.Sets.Count == 0 && spec.Curves.Count == 0;
                    if (secondsOnly) EnsureCarrierParam();
                    var clip = ControllerEmit.BuildClipContent(spec, _paramNames);
                    clip.hideFlags = HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(clip, _controller);
                    _result.Clips[spec.Name] = clip;
                    _result.ClipList.Add(clip);
                }
            }

            // The scratch float a seconds-only carrier animates, declared on first use. Not added to _paramNames: the
            // name is reserved and must not become a bare-param target for authored clip bindings.
            private const string CarrierParam = ReservedNames.CarrierParam;
            private bool _carrierDeclared;
            private void EnsureCarrierParam()
            {
                if (_carrierDeclared) return;
                // Reachable only through a direct Build call, which skips SchemaValidation's refusal of a declared
                // `_CompilerNull`.
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

                    // Two passes: a transition target may name a machine not yet emitted, so wiring waits until the
                    // whole scope tree exists.
                    var rootScope = EmitMachine(layer, layer.Root, sm);
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
                        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(layer.Mask);
                        if (mask == null)
                            throw new EmitException($"layer '{layer.Name}': avatarMask '{layer.Mask}' not found (path did not resolve to an AvatarMask)");
                        acLayer.avatarMask = mask;
                    }
                    layers.Add(acLayer);
                }

                // controller.layers, sm.states and bt.children all return copies: accumulate, then write the whole
                // array back once.
                _controller.layers = layers.ToArray();
            }

            // One machine's resolution scope. Resolution is per machine, never a layer-global name dictionary: Unity
            // scopes state names per machine, the schema permits a name to recur across machines, and a global
            // dictionary would last-write-win and silently mis-wire.
            private sealed class MachineScope
            {
                public AnimatorStateMachine Target;
                public readonly Dictionary<string, AnimatorState> States = new Dictionary<string, AnimatorState>();
                public readonly Dictionary<string, MachineScope> Subs = new Dictionary<string, MachineScope>();
            }

            private MachineScope EmitMachine(Layer layer, StateMachine model, AnimatorStateMachine target)
            {
                var scope = new MachineScope { Target = target };
                var states = model.States;
                for (int i = 0; i < states.Count; i++)
                {
                    var s = states[i];
                    var pos = NodePos(model.Layout, s.Name) ?? GridState(i);
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
                    var pos = NodePos(model.Layout, sub.Name) ?? GridSub(i);
                    var childSm = target.AddStateMachine(sub.Name, pos);
                    childSm.hideFlags = HideFlags.HideInHierarchy;
                    scope.Subs[sub.Name] = EmitMachine(layer, sub.Machine, childSm);
                }

                // Always written, so decompile has a deterministic baseline.
                var lay = model.Layout;
                target.entryPosition              = SpecialPos(lay?.Entry,  SpecialEntry);
                target.anyStatePosition           = SpecialPos(lay?.Any,    SpecialAny);
                target.exitPosition               = SpecialPos(lay?.Exit,   SpecialExit);
                target.parentStateMachinePosition = SpecialPos(lay?.Parent, SpecialParent);

                foreach (var subName in scope.Subs.Keys)
                    if (scope.States.ContainsKey(subName))
                        throw new EmitException($"machine '{target.name}': a state and a sub-machine are both named '{subName}' — a bare target or default can't address both (states resolve first)");

                EmitBehaviours(model.Behaviours, t => target.AddStateMachineBehaviour(t));
                return scope;
            }

            private void WireMachine(Layer layer, StateMachine model, MachineScope scope, MachineScope root)
            {
                var target = scope.Target;

                // A state default sets defaultState; a sub-machine default becomes the unconditional entry rung added
                // after the entry ladder below.
                bool defaultIsState = false;
                string defaultName = string.IsNullOrEmpty(model.DefaultState) ? null : AddressPath.UnescapeSegment(model.DefaultState);
                if (defaultName != null)
                {
                    if (scope.States.TryGetValue(defaultName, out var def)) { target.defaultState = def; defaultIsState = true; }
                    else if (!scope.Subs.ContainsKey(defaultName))
                        throw new EmitException($"machine '{target.name}': default '{model.DefaultState}' is neither a direct state nor a direct sub-machine of this machine");
                }

                // `defaultState:`, the foreign default. Unity's m_DefaultState is a plain AnimatorState PPtr, so a
                // machine can boot a state nested several machines down. It writes only the PPtr and adds no rung,
                // so it composes with a `default:` sub-machine rung. The defaultState setter does not propagate to
                // ancestors (measured), so a child's assignment here cannot rewrite one already made on its parent.
                if (!string.IsNullOrEmpty(model.DefaultStatePath))
                {
                    // Anchor a lone segment: `defaultState:` is root-relative, but ResolveName reads a lone segment
                    // as local, which would bind this machine's own `P` after SchemaValidation checked the root's.
                    string p = model.DefaultStatePath;
                    if (p[0] != '/') p = "/" + p;
                    bool isState;
                    AnimatorState foreignState;
                    try
                    {
                        isState = ResolveName(p, scope, root, target.name, out foreignState, out _);
                    }
                    catch (EmitException e)
                    {
                        throw new EmitException($"machine '{target.name}': defaultState '{p}' does not resolve — {e.Message}");
                    }
                    if (!isState)
                        throw new EmitException($"machine '{target.name}': defaultState '{p}' resolves to a sub-machine, but Unity's default state is a state reference — name a state, or use `default:` for a direct sub-machine");
                    // Measured: Unity silently discards an assignment outside this sub-tree and the previous
                    // default stands.
                    if (!IsInSubtree(target, foreignState))
                        throw new EmitException($"machine '{target.name}': defaultState '{p}' names a state outside this machine's own sub-tree, which Unity silently discards — a machine's default must be one of its own states or one nested inside it");
                    target.defaultState = foreignState;
                }

                // canTransitionToSelf is set on every rung, never left to Unity: AddTransition on a state that
                // already lives in an asset defaults it to false in a GUI Editor and true in batchmode, so the
                // same document would build a rung to its own state live in one Editor and dead in the other.
                // It decides only a rung whose target is its own state; one through Exit or a sub-machine
                // re-enters either way. So `to: <own state>` means re-enter, and every other rung is off.
                foreach (var s in model.States)
                {
                    var from = scope.States[s.Name];
                    foreach (var t in s.Transitions)
                    {
                        AnimatorStateTransition tr;
                        AnimatorState toState = null;
                        if (t.ToExit) tr = from.AddExitTransition();
                        else if (string.IsNullOrEmpty(t.To))
                            throw new EmitException($"transition from '{from.name}' has neither a target nor ToExit");
                        else tr = ResolveName(t.To, scope, root, target.name, out toState, out var toSm)
                            ? from.AddTransition(toState) : from.AddTransition(toSm);
                        tr.canTransitionToSelf = toState == from;
                        ConfigureStateTransition(tr, t);
                    }
                }

                foreach (var t in model.AnyLadder)
                {
                    RequireTargetName(t, target.name);
                    var atr = ResolveName(t.To, scope, root, target.name, out var toState, out var toSm)
                        ? target.AddAnyStateTransition(toState) : target.AddAnyStateTransition(toSm);
                    atr.canTransitionToSelf = t.CanTransitionToSelf;
                    ConfigureStateTransition(atr, t);
                }

                foreach (var t in model.EntryLadder)
                {
                    RequireTargetName(t, target.name);
                    var etr = ResolveName(t.To, scope, root, target.name, out var toState, out var toSm)
                        ? target.AddEntryTransition(toState) : target.AddEntryTransition(toSm);
                    foreach (var c in t.When) etr.AddCondition(MapCondOp(c.Op, c.Value), c.Value, c.Param);
                }

                // Added last so any conditional entry rung above wins first. This sets no `defaultState`, and when a
                // machine has no direct states Unity's AnimatorStateMachine.defaultState getter resolves through to
                // the child machine's own default. So lint and decompile must probe this entry transition, not
                // `defaultState`, for whether a direct-state default was set.
                if (!defaultIsState && defaultName != null)
                    target.AddEntryTransition(scope.Subs[defaultName].Target);

                // Sub-machine onExit ladders hang off this machine (the parent), so their targets resolve in this
                // machine's scope. `to: Exit` is legal here, unlike the entry ladder, and passes the exit up to this
                // machine's own Exit.
                foreach (var sub in model.Machines)
                {
                    var childTarget = scope.Subs[sub.Name].Target;
                    foreach (var t in sub.OnExit)
                    {
                        AnimatorTransition str;
                        if (t.ToExit) str = target.AddStateMachineExitTransition(childTarget);
                        else if (string.IsNullOrEmpty(t.To))
                            throw new EmitException($"onExit transition on sub-machine '{sub.Name}' has neither a target nor 'to: Exit'");
                        else str = ResolveName(t.To, scope, root, target.name, out var toState, out var toSm)
                            ? target.AddStateMachineTransition(childTarget, toState)
                            : target.AddStateMachineTransition(childTarget, toSm);
                        foreach (var c in t.When) str.AddCondition(MapCondOp(c.Op, c.Value), c.Value, c.Param);
                    }
                }

                foreach (var sub in model.Machines)
                    WireMachine(layer, sub.Machine, scope.Subs[sub.Name], root);
            }

            private static void RequireTargetName(Transition t, string fromMachine)
            {
                if (t.ToExit || string.IsNullOrEmpty(t.To))
                    throw new EmitException($"entry/any ladder transition in machine '{fromMachine}' has no target (Exit is not a valid target there)");
            }

            // Returns true for a state, false for a sub-machine. The addressing forms are
            // docs/animator-schema.md §Cross-machine addressing.
            private bool ResolveName(string name, MachineScope scope, MachineScope root, string fromMachine,
                out AnimatorState toState, out AnimatorStateMachine toSm)
            {
                toState = null; toSm = null;

                if (name.Length > 0 && name[0] == '/')
                    return ResolveFromRoot(name.Substring(1), root, fromMachine, name, out toState, out toSm);

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

            private static bool IsInSubtree(AnimatorStateMachine sm, AnimatorState state)
            {
                if (sm == null) return false;
                foreach (var cs in sm.states)
                    if (cs.state == state) return true;
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null && IsInSubtree(child.stateMachine, state)) return true;
                return false;
            }

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
                // exitTime is written even when unused: Unity's default for it differs between a GUI Editor and
                // batchmode (see the state-ladder loop), and an emit must not depend on which one ran it.
                if (t.ExitTime.HasValue) { tr.hasExitTime = true; tr.exitTime = t.ExitTime.Value; }
                else { tr.hasExitTime = _doc.Defaults.TransitionHasExitTime; tr.exitTime = 0f; }
                tr.duration = t.Duration ?? _doc.Defaults.TransitionDuration;
                tr.offset = t.Offset ?? 0f;
                tr.hasFixedDuration = t.FixedDuration ?? true;
                tr.interruptionSource = MapInterruption(t.Interruption ?? _doc.Defaults.Interruption);
                tr.orderedInterruption = t.OrderedInterruption ?? true;
                tr.mute = t.Mute;
                tr.solo = t.Solo;
                if (!string.IsNullOrEmpty(t.Name)) tr.name = t.Name;
                foreach (var c in t.When) tr.AddCondition(MapCondOp(c.Op, c.Value), c.Value, c.Param); // empty When = unconditional
            }

            // ----- motions / blend trees -----

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
                        if (mr.RefGuid.Unresolved)
                        {
                            _result.UnresolvedRefs.Add((stateContext, mr.RefGuid.Guid));
                            return null;
                        }
                        throw new EmitException(GuidRefUnresolved(mr.RefGuid));
                    }
                    return m;
                }
                if (mr.Shared != null) return BuildShared(mr.Shared, stateContext);
                if (mr.Tree != null) return BuildTree(mr.Tree, treeName, stateContext);
                throw new EmitException("motion ref sets none of clip/ref/tree/shared");
            }

            // First reference builds the `trees:` entry; every later one returns the same object, so the controller
            // carries a single sub-asset with N parent PPtrs, measured to serialize as one !u!206 document.
            //
            // The key is passed as BuildTree's positional name because a trees: entry carries no `name:`. Otherwise
            // `spec.Name ?? name` falls through to the caller's positional default, so the built name would depend on
            // which reference site built it first and every child would gain an explicit name: on the next decompile.
            private Motion BuildShared(string name, string stateContext)
            {
                if (_sharedBuilt.TryGetValue(name, out var already)) return already;
                if (!_sharedSpecs.TryGetValue(name, out var spec))
                    throw new EmitException($"motion references shared tree '{name}' not declared under trees:");
                int at = _sharedBuilding.IndexOf(name);
                if (at >= 0)
                {
                    var loop = string.Join(" -> ", _sharedBuilding.Skip(at)) + " -> " + name;
                    throw new EmitException($"shared tree '{name}' contains itself: {loop}");
                }
                _sharedBuilding.Add(name);
                try
                {
                    var bt = BuildTree(spec, name, stateContext);
                    _sharedBuilt[name] = bt;
                    return bt;
                }
                finally { _sharedBuilding.RemoveAt(_sharedBuilding.Count - 1); }
            }

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
                    return null;
                }
                return AssetDatabase.LoadAssetAtPath<Motion>(p);
            }

            private static string GuidRefUnresolved(GuidRef g)
                => $"motion ref guid unresolved: {g.Guid}" + (g.FileID != 0 ? $" fileID:{g.FileID}" : "");

            private BlendTree BuildTree(BlendTreeSpec spec, string name, string stateContext)
            {
                // Children derive from the applied name, not the incoming positional one, or a named parent's
                // children would decompile with explicit names (ControllerDecompile.MeaningfulName compares against
                // the default).
                var applied = spec.Name ?? name;
                var bt = new BlendTree { name = applied, hideFlags = HideFlags.HideInHierarchy };
                bt.blendType = MapTreeKind(spec.Kind);
                // A fresh 1D tree defaults to useAutomaticThresholds=true, which makes Unity overwrite explicit
                // per-child thresholds with even spacing. Disable it before adding children.
                if (spec.Kind == TreeKind.OneD) bt.useAutomaticThresholds = false;
                if (!string.IsNullOrEmpty(spec.Param)) bt.blendParameter = spec.Param;
                if (!string.IsNullOrEmpty(spec.ParamY)) bt.blendParameterY = spec.ParamY;
                // Direct-tree "Normalized Blend Values" has no typed API.
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

            // ----- behaviours -----
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
                        case FaceTrackingSetKeys.Kind:
#if VRC_FT_SETS_PRESENT
                            PopulateFaceTrackingSets((VRCAnimatorFaceTrackingBlendshapesSet)add(typeof(VRCAnimatorFaceTrackingBlendshapesSet)), b.Fields);
                            break;
#else
                            // The parse side is SDK-free, so a document carrying the kind reaches here on any SDK.
                            throw new EmitException(FaceTrackingSetKeys.NeedsSdk);
#endif
                        default:
                            throw new EmitException(
                                $"behaviour: unknown kind '{b.Kind}' (expected driver/tracking/playableLayer/locomotion/poseSpace/playAudio/layerControl/faceTrackingSets)");
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
                        case DriverKeys.PreventRepeats: p.preventRepeats = AsBool(kv.Value, "driver.random." + name + ".preventRepeats"); break;
                        default: throw new EmitException($"driver.random.{name}: unknown field '{kv.Key}'");
                    }
                }
                return p;
            }

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

#if VRC_FT_SETS_PRESENT
            // Fields an element's type leaves inert are written at the SDK inspector's new-element values (weight 1,
            // ranges 0/1/0/1, empty sourceParameter), so a compiled element and an inspector-made one agree on what
            // neither reads. The bounds refused below are the inspector's clamps, and MaxEntries mirrors the SDK's
            // MaxBlendSetsPerBehavior.
            private static void PopulateFaceTrackingSets(VRCAnimatorFaceTrackingBlendshapesSet c, Dictionary<string, object> fields)
            {
                var list = new List<FtSets.BlendSet>();
                foreach (var kv in fields)
                {
                    switch (kv.Key)
                    {
                        case FaceTrackingSetKeys.Sets:
                            foreach (var e in AsMap(kv.Value, "faceTrackingSets.sets"))
                                list.Add(BuildBlendSet(e.Key, e.Value));
                            break;
                        default:
                            throw new EmitException($"faceTrackingSets: unknown field '{kv.Key}' (expected sets)");
                    }
                }
                if (list.Count > FaceTrackingSetKeys.MaxEntries)
                    throw new EmitException($"faceTrackingSets.sets: {list.Count} entries, but the SDK processes only the first "
                        + $"{FaceTrackingSetKeys.MaxEntries} per behaviour — split them across two behaviours on the state");
                c.blendSets = list;
            }

            private static FtSets.BlendSet BuildBlendSet(string name, object value)
            {
                string ctx = "faceTrackingSets.sets." + name;
                if (string.IsNullOrWhiteSpace(name))
                    throw new EmitException("faceTrackingSets.sets: an entry has an empty Blendshapes Set name — it would name no set");
                var e = new FtSets.BlendSet
                {
                    blendshapesSetName = name, type = FtSets.SourceType.Value, sourceParameter = "", weight = 1f,
                    convertRange = false, sourceMin = 0f, sourceMax = 1f, destMin = 0f, destMax = 1f, lerpSpeed = 0f,
                };
                switch (value)
                {
                    case string src:
                        e.type = FtSets.SourceType.Parameter;
                        e.sourceParameter = RequireSource(src, ctx);
                        return e;
                    case bool _:
                        throw new EmitException($"{ctx}: expected a weight (0..1), a parameter name, or a map, got a boolean — quote a parameter named like one");
                    case Dictionary<string, object> m:
                        return BuildBlendSetMap(e, m, ctx);
                    case null:
                        throw new EmitException($"{ctx}: expected a weight (0..1), a parameter name, or a map, got null");
                    default:
                        e.weight = Bounded(FtNum(value, ctx), 0f, 1f, ctx);
                        return e;
                }
            }

            private static FtSets.BlendSet BuildBlendSetMap(FtSets.BlendSet e, Dictionary<string, object> m, string ctx)
            {
                bool hasWeight = m.ContainsKey(FaceTrackingSetKeys.Weight), hasSource = m.ContainsKey(FaceTrackingSetKeys.Source);
                if (hasWeight && hasSource)
                    throw new EmitException($"{ctx}: carries both weight and source — an entry is either a constant weight or a parameter read");
                if (!hasWeight && !hasSource)
                    throw new EmitException($"{ctx}: needs weight (a constant) or source (a parameter)");
                bool range = false;
                foreach (var kv in m)
                {
                    string k = ctx + "." + kv.Key;
                    switch (kv.Key)
                    {
                        case FaceTrackingSetKeys.Weight: e.weight = Bounded(FtNum(kv.Value, k), 0f, 1f, k); break;
                        case FaceTrackingSetKeys.Source:
                            e.type = FtSets.SourceType.Parameter;
                            e.sourceParameter = RequireSource(AsString(kv.Value, k), ctx);
                            break;
                        case FaceTrackingSetKeys.SourceMin: e.sourceMin = FtNum(kv.Value, k); range = true; break;
                        case FaceTrackingSetKeys.SourceMax: e.sourceMax = FtNum(kv.Value, k); range = true; break;
                        case FaceTrackingSetKeys.DestMin: e.destMin = Bounded(FtNum(kv.Value, k), 0f, 1f, k); range = true; break;
                        case FaceTrackingSetKeys.DestMax: e.destMax = Bounded(FtNum(kv.Value, k), 0f, 1f, k); range = true; break;
                        case FaceTrackingSetKeys.LerpSpeed: e.lerpSpeed = Bounded(FtNum(kv.Value, k), 0f, 100f, k); break;
                        default:
                            throw new EmitException($"{ctx}: unknown field '{kv.Key}' (expected weight/source/sourceMin/sourceMax/destMin/destMax/lerpSpeed)");
                    }
                }
                if (range && !hasSource)
                    throw new EmitException($"{ctx}: a range key applies only to a source entry — a constant weight has nothing to remap");
                e.convertRange = range;
                return e;
            }

            private static string RequireSource(string src, string ctx)
            {
                if (string.IsNullOrWhiteSpace(src))
                    throw new EmitException($"{ctx}: empty source parameter — the entry would read nothing");
                return src;
            }

            private static float FtNum(object v, string ctx)
            {
                if (v is bool) throw new EmitException($"{ctx}: expected a number, got a boolean");
                float f = AsFloat(v, ctx);
                if (float.IsNaN(f) || float.IsInfinity(f))
                    throw new EmitException($"{ctx}: {(v is double d ? d.ToString(CultureInfo.InvariantCulture) : f.ToString(CultureInfo.InvariantCulture))} is not a finite float");
                return f;
            }

            private static float Bounded(float v, float min, float max, string ctx)
            {
                if (!(v >= min && v <= max))
                    throw new EmitException($"{ctx}: {v.ToString(CultureInfo.InvariantCulture)} is outside "
                        + $"{min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}, the range the SDK inspector clamps it to");
                return v;
            }
#endif

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

            // ----- VRC expression parameters -----

            // The params asset's contract is docs/animator-schema.md §parameters. ControllerFixpoint.ParamsDiff is
            // what catches a filter change altering it silently.
            private void EmitVrcParameters()
            {
                foreach (var p in _doc.Parameters)
                    if (p.Vrc != null && p.Vrc.HasEncoding && ControllerRules.IsVrcReserved(p.Name))
                        throw new EmitException($"parameter '{p.Name}' is a VRC built-in, which the params asset leaves out, so nothing carries its vrc.bits/signed/lerp; VRChat fixes a built-in's encoding");
                var listed = _doc.Parameters
                    .Where(p => !p.Scratch && !ControllerRules.IsVrcReserved(p.Name))
                    .ToList();
                if (listed.Count == 0) return;

                var ep = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                var list = new List<VRCExpressionParameters.Parameter>();
                foreach (var p in listed)
                {
                    var vt = p.Vrc?.VrcType ?? p.Type;
                    var par = new VRCExpressionParameters.Parameter
                    {
                        name = p.Name,
                        valueType = MapValueType(vt),
                        defaultValue = p.Default,
                        saved = p.Vrc?.Saved ?? false,
                        networkSynced = p.Vrc?.Synced ?? false,
                    };
                    if (p.Vrc != null && p.Vrc.HasEncoding) ApplyEncoding(par, p, vt);
                    list.Add(par);
                }
                ep.parameters = list.ToArray();
                _result.Params = ep;
            }

            // The author states signed/unsigned; the SDK stores a flip of the wire type's default (float signed, int
            // unsigned). SchemaValidation has already refused every placement and range these fields cannot take.
            private static void ApplyEncoding(VRCExpressionParameters.Parameter par, ParamSpec p, AnimParamType wireType)
            {
#if VRC_PARAM_ENCODING_PRESENT
                var v = p.Vrc;
                if (v.Bits.HasValue) par.bitWidth = (byte)v.Bits.Value;
                if (v.Signed.HasValue) par.flippedRepresentation = v.Signed.Value != (wireType == AnimParamType.Float);
                if (v.Lerp.HasValue)
                {
                    par.useCustomLerpDuration = true;
                    par.customLerpDuration = v.Lerp.Value;
                }
#else
                throw new EmitException($"parameter '{p.Name}' declares vrc.bits/signed/lerp, which need com.vrchat.avatars 3.10.6-beta.4 or newer; upgrade this project's SDK or drop the fields");
#endif
            }

            // ----- VRC expressions menu -----

            // VRChat's control type is written through the typed `type` field, never through
            // SerializedProperty.enumValueIndex. The serialized numbers (101 Button, 102 Toggle, 103 SubMenu,
            // 203 RadialPuppet) are not the enum's ordinals, so assigning one to enumValueIndex is out of range and
            // Unity silently discards it, leaving a childless Button (docs/menus.md).
            private void EmitMenu()
            {
                if (_doc.Menu == null) return;

                if (VRCExpressionsMenu.MAX_CONTROLS != MenuLimits.MaxControlsPerMenu)
                    throw new EmitException(
                        $"menu: SDK VRCExpressionsMenu.MAX_CONTROLS is {VRCExpressionsMenu.MAX_CONTROLS} but MenuLimits.MaxControlsPerMenu mirrors {MenuLimits.MaxControlsPerMenu} — update the echo in AnimatorSchema.cs");

                // SchemaValidation refuses a control on a scratch param but is System.*-only and cannot reach
                // ControllerRules, so the built-in half of that exclusion is checked here.
                RefuseReservedMenuParams(_doc.Menu, "menu");

                _result.Menu = BuildMenuPage(_doc.Menu, _doc.ControllerName + "_Menu", "menu");
            }

            private static void RefuseReservedMenuParams(List<MenuControl> controls, string where)
            {
                foreach (var c in controls)
                {
                    if (c == null) continue;
                    if (c.Param != null && ControllerRules.IsVrcReserved(c.Param))
                        throw new EmitException(
                            $"menu: {where} '{c.Name}' drives the VRC built-in '{c.Param}' — built-ins are excluded from the emitted VRCExpressionParameters, so the control would be inert on the avatar");
                    if (c.Controls != null) RefuseReservedMenuParams(c.Controls, $"{where} '{c.Name}'");
                }
            }

            private VRCExpressionsMenu BuildMenuPage(List<MenuControl> controls, string assetName, string where)
            {
                var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                _menuPages.Add(menu);
                menu.name = assetName;
                menu.controls = new List<VRCExpressionsMenu.Control>();

                foreach (var c in controls)
                {
                    var control = new VRCExpressionsMenu.Control
                    {
                        name = c.Name,
                        type = MapControlType(c.Kind),
                        // A null `parameter` serializes as an empty name, which is what a bare sub-menu carries.
                        parameter = new VRCExpressionsMenu.Control.Parameter { name = c.Param ?? "" },
                        subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                        labels = new VRCExpressionsMenu.Control.Label[0],
                    };

                    if (c.Kind == MenuControlKind.Radial)
                    {
                        // A radial writes nothing on press; the parameter IS the position, so it moves to
                        // subParameters and `parameter` stays empty (the SDK's optional on-open bool, unused here).
                        control.parameter = new VRCExpressionsMenu.Control.Parameter { name = "" };
                        control.subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = c.Param } };
                    }
                    else
                    {
                        control.value = c.Value;
                    }

                    if (c.Icon != null) control.icon = ResolveIcon(c.Icon, $"{where} '{c.Name}'");

                    if (c.Kind == MenuControlKind.SubMenu)
                    {
                        var child = BuildMenuPage(c.Controls, assetName + "_" + SanitizeAssetName(c.Name),
                            $"{where} '{c.Name}'");
                        _result.MenuChildren.Add(child);
                        control.subMenu = child;
                    }

                    menu.controls.Add(control);
                }
                return menu;
            }

            // Resolve an authored `icon:` to a Texture2D. Three outcomes:
            //   1. It loads → the icon is set.
            //   2. It does not load and the document is outside the AssetDatabase → a null icon and a compile
            //      advisory, whether or not the file is on disk. The compiler cannot tell a correct path from a wrong
            //      one there; this is the vrc-patterns gate (docs/animator-schema.md §menu).
            //   3. It does not load and the document is in the AssetDatabase → EmitException.
            // The branch is on whether the document is in the AssetDatabase, never on how a path is spelled. An
            // in-project document routinely arrives as an absolute path, and a package's `Packages/<name>/…` names no
            // folder on disk, so a `StartsWith("Assets/")` test misroutes both, silently, into the null icon.
            // AssetPathUtil.ToLogicalAssetPath owns the canonicalization.
            private Texture2D ResolveIcon(string authored, string where)
            {
                authored = authored.Replace('\\', '/');
                bool projectSpelled = authored.StartsWith("Assets/", StringComparison.Ordinal)
                                   || authored.StartsWith("Packages/", StringComparison.Ordinal);
                var docAsset = AssetPathUtil.ToLogicalAssetPath(_doc.SourcePath);

                if (docAsset != null)
                {
                    string iconPath;
                    if (projectSpelled) iconPath = CollapseDotSegments(authored);
                    else
                    {
                        int cut = docAsset.LastIndexOf('/');
                        iconPath = CollapseDotSegments((cut < 0 ? "" : docAsset.Substring(0, cut + 1)) + authored);
                    }
                    return LoadIconAsset(iconPath, authored, where);
                }

                if (projectSpelled)
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(CollapseDotSegments(authored));
                    if (tex != null) return tex;
                    _result.IconsOutsideProject.Add((where, authored));
                    return null;
                }

                if (string.IsNullOrEmpty(_doc.SourcePath))
                    throw new EmitException(
                        $"{where}: icon '{authored}' is document-relative but this document has no source path to resolve it against — write a project path (Assets/… or Packages/…) instead");

                var docDir = Path.GetDirectoryName(Path.GetFullPath(_doc.SourcePath.Replace('\\', '/'))) ?? "";
                var abs = Path.GetFullPath(Path.Combine(docDir, authored)).Replace('\\', '/');
                _result.IconsOutsideProject.Add((where, File.Exists(abs) ? abs : abs + " (no such file)"));
                return null;
            }

            // AssetPathToGUID, not File.Exists: a package path has no meaning to the filesystem.
            private static Texture2D LoadIconAsset(string projectPath, string authored, string where)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(projectPath);
                if (tex != null) return tex;
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(projectPath)))
                    throw new EmitException(
                        $"{where}: icon '{authored}' is at '{projectPath}' but did not load as a Texture2D — check the file is an image the project has imported");
                throw new EmitException($"{where}: icon not found — '{authored}' resolved to '{projectPath}'");
            }

            // Path.GetFullPath would collapse these only by rooting the result at the filesystem, which a package
            // path must not do.
            private static string CollapseDotSegments(string p)
            {
                var outSeg = new List<string>();
                foreach (var seg in p.Split('/'))
                {
                    if (seg.Length == 0 || seg == ".") continue;
                    if (seg == ".." && outSeg.Count > 0 && outSeg[outSeg.Count - 1] != "..") outSeg.RemoveAt(outSeg.Count - 1);
                    else outSeg.Add(seg);
                }
                return string.Join("/", outSeg);
            }

            private static VRCExpressionsMenu.Control.ControlType MapControlType(MenuControlKind k)
            {
                switch (k)
                {
                    case MenuControlKind.Button:  return VRCExpressionsMenu.Control.ControlType.Button;
                    case MenuControlKind.Toggle:  return VRCExpressionsMenu.Control.ControlType.Toggle;
                    case MenuControlKind.SubMenu: return VRCExpressionsMenu.Control.ControlType.SubMenu;
                    case MenuControlKind.Radial:  return VRCExpressionsMenu.Control.ControlType.RadialPuppet;
                    default: throw new EmitException($"menu: unhandled control kind '{k}'");
                }
            }

            private static string SanitizeAssetName(string name)
            {
                var sb = new StringBuilder(name.Length);
                foreach (var ch in name)
                    sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
                return sb.ToString();
            }

            // ----- persistence / provenance -----

            private void StampProvenance(string path)
            {
                // No source bytes, no stamp; see the null contract on Build.
                if (_sourceText == null) return;

                var importer = AssetImporter.GetAtPath(path);
                if (importer == null) return;
                // `compiled-from:` carries no value on purpose: its presence is the signal that a compile wrote this
                // controller. Never rename the key: every stamped controller on disk spells it this way, so a rename
                // makes all of them read as no-provenance and fire the hand-authored warning.
                string hash = SourceHash(_sourceText);
                string stamp = "compiled-from:;srchash:" + hash;
                importer.userData = stamp;

                // Write the .meta, do not reimport: the stamp changes nothing about the imported artifact.
                // WriteImportSettingsIfDirty leaves the asset artifact-stale, deferring that import to the next
                // Refresh, where deferred imports coalesce (measured ~6x over ten controllers).
                bool wrote = AssetDatabase.WriteImportSettingsIfDirty(path);

                // Verify against the .meta on disk. `wrote` cannot answer this: false is the ordinary
                // idempotent-recompile result and true says only that a write was attempted. Nor can
                // AssetImporter.GetAtPath: it returns the cached in-memory importer, which reports the intended
                // string whether or not a byte landed.
                string metaPath = path + ".meta";
                if (File.Exists(metaPath) && File.ReadAllText(metaPath).Contains(stamp)) return;

                // Report, never throw. The controller is already stripped and rebuilt, CleanupAfterEmit restores
                // nothing here, and ProofCompile stamps a different path so it cannot cover a failure at this one
                // (a read-only .meta, a VCS lock). Throwing would return FAIL with the replaced controller on disk,
                // falsifying CompileController's promise that a failing overwrite leaves the prior controller
                // untouched.
                _result.StampFailure = "provenance stamp not persisted to " + metaPath
                    + " (import-settings write reported " + wrote + ", and the stamp is absent from the .meta on"
                    + " disk) — the controller compiled correctly, but the NEXT compile of this path reads it as"
                    + " carrying no provenance and warns that it was hand-authored.";
                Debug.LogError("[CompileController] " + _result.StampFailure);
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

        // ----- output path -----

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
                case double d:
                    if (d != Math.Floor(d))
                        throw new EmitException($"{ctx}: expected an integer, got non-integral {d.ToString(CultureInfo.InvariantCulture)}");
                    return (int)d;
                default: throw new EmitException($"{ctx}: expected an integer, got {(v == null ? "null" : v.GetType().Name)}");
            }
        }

        private static Vector2 AsVector2(object v, string ctx)
        {
            if (!(v is List<object> list))
                throw new EmitException($"{ctx}: expected a [min, max] list, got {(v == null ? "null" : v.GetType().Name)}");
            if (list.Count != 2)
                throw new EmitException($"{ctx}: expected exactly 2 numbers, got {list.Count}");
            return new Vector2(AsFloat(list[0], ctx + "[0]"), AsFloat(list[1], ctx + "[1]"));
        }

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

        private static T ParseEnumToken<T>(object v, Dictionary<string, T> map, string ctx) where T : struct
        {
            string s = AsString(v, ctx);
            if (map.TryGetValue(s, out var e)) return e;
            throw new EmitException($"{ctx}: unknown value '{s}' (expected {string.Join("/", map.Keys)})");
        }

        // ----- schema-token → SDK-enum maps (the enum-valued behaviour fields) -----

        // ControllerDecompile builds its reverse (enum→token) maps from these, so a value present in neither
        // direction is refused on both sides.
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

        // Shared with ControllerDecompile: the encode case labels and decode dictionary keys cannot drift apart.
        internal static class DriverKeys
        {
            public const string LocalOnly = "localOnly", Set = "set", Add = "add", Copy = "copy", Random = "random";
            public const string Source = "source", SourceMin = "sourceMin", SourceMax = "sourceMax", DestMin = "destMin", DestMax = "destMax";
            public const string Min = "min", Max = "max", Chance = "chance", PreventRepeats = "preventRepeats";
        }

        // Shared with ControllerDecompile. Declared outside the SDK guard so the kind is a known name on every SDK:
        // below com.vrchat.avatars 3.10.6 emit refuses it as unsupported by the installed SDK, not as an unknown
        // kind.
        internal static class FaceTrackingSetKeys
        {
            public const string Kind = "faceTrackingSets", Sets = "sets";
            public const string Weight = "weight", Source = "source", SourceMin = "sourceMin", SourceMax = "sourceMax";
            public const string DestMin = "destMin", DestMax = "destMax", LerpSpeed = "lerpSpeed";
            public const int MaxEntries = 64;
            public const string NeedsSdk = "behaviour: faceTrackingSets needs com.vrchat.avatars >= 3.10.6 "
                + "(VRCAnimatorFaceTrackingBlendshapesSet), and this project's SDK is below 3.10.6 final — a 3.10.6 "
                + "prerelease counts as below, since the gate is the com.vrchat.avatars >= 3.10.6 versionDefine";
        }

        internal static class PlayAudioKeys
        {
            public const string SourcePath = "sourcePath", PlaybackOrder = "playbackOrder", Parameter = "parameter";
            public const string Volume = "volume", VolumeApply = "volumeApply", Pitch = "pitch", PitchApply = "pitchApply";
            public const string Loop = "loop", LoopApply = "loopApply", Clips = "clips", ClipsApply = "clipsApply";
            public const string DelaySeconds = "delaySeconds", PlayOnEnter = "playOnEnter", StopOnEnter = "stopOnEnter";
            public const string PlayOnExit = "playOnExit", StopOnExit = "stopOnExit";
        }

        /// <summary>The provenance source hash, which CompileController compares to the <c>srchash:</c> stamped
        /// in a controller's userData to detect drift before overwriting.</summary>
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
