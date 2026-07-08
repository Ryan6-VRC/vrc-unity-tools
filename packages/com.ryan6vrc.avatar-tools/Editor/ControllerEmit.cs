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

                    var byName = new Dictionary<string, AnimatorState>();
                    var states = layer.Root.States;
                    for (int i = 0; i < states.Count; i++)
                    {
                        var s = states[i];
                        var pos = new Vector3(300f, 60f * i, 0f); // fixed grid keyed by document order
                        var ast = sm.AddState(s.Name, pos);
                        ast.writeDefaultValues = s.WriteDefaults ?? layer.WriteDefaults ?? _doc.Defaults.WriteDefaults;
                        ast.speed = s.Speed;
                        if (!string.IsNullOrEmpty(s.SpeedParam)) { ast.speedParameterActive = true; ast.speedParameter = s.SpeedParam; }
                        if (!string.IsNullOrEmpty(s.MotionTimeParam)) { ast.timeParameterActive = true; ast.timeParameter = s.MotionTimeParam; }
                        ast.mirror = s.Mirror;
                        if (s.Motion != null) ast.motion = BuildMotion(s.Motion, s.Name + "_BlendTree");
                        EmitBehaviours(s.Behaviours, t => ast.AddStateMachineBehaviour(t));
                        byName[s.Name] = ast;
                    }

                    if (!string.IsNullOrEmpty(layer.Root.DefaultState))
                    {
                        if (byName.TryGetValue(layer.Root.DefaultState, out var def)) sm.defaultState = def;
                        else throw new EmitException($"layer '{layer.Name}': default state '{layer.Root.DefaultState}' is not a state in this machine");
                    }

                    // State transition ladders (ordered per source state = first-match order).
                    for (int i = 0; i < states.Count; i++)
                        foreach (var t in states[i].Transitions)
                            ConfigureStateTransition(MakeStateTransition(byName[states[i].Name], t, byName), t);

                    // AnyState ladder.
                    foreach (var t in layer.Root.AnyLadder)
                    {
                        var atr = sm.AddAnyStateTransition(ResolveTargetState(t, byName));
                        atr.canTransitionToSelf = t.CanTransitionToSelf;
                        ConfigureStateTransition(atr, t);
                    }

                    // Entry ladder (no duration / exit-time — conditions only).
                    foreach (var t in layer.Root.EntryLadder)
                    {
                        var etr = sm.AddEntryTransition(ResolveTargetState(t, byName));
                        foreach (var c in t.When) etr.AddCondition(MapCondOp(c.Op, c.Value), c.Value, c.Param);
                    }

                    EmitBehaviours(layer.Root.Behaviours, t => sm.AddStateMachineBehaviour(t));

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

            private AnimatorState ResolveTargetState(Transition t, Dictionary<string, AnimatorState> byName)
            {
                if (t.ToExit || string.IsNullOrEmpty(t.To))
                    throw new EmitException("entry/any ladder transition has no target state (Exit is not a valid target there)");
                if (!byName.TryGetValue(t.To, out var to))
                    throw new EmitException($"transition target state '{t.To}' not found in layer");
                return to;
            }

            private AnimatorStateTransition MakeStateTransition(AnimatorState from, Transition t, Dictionary<string, AnimatorState> byName)
            {
                if (t.ToExit) return from.AddExitTransition();
                if (string.IsNullOrEmpty(t.To))
                    throw new EmitException($"transition from '{from.name}' has neither a target nor ToExit");
                if (!byName.TryGetValue(t.To, out var to))
                    throw new EmitException($"transition target state '{t.To}' not found in layer");
                return from.AddTransition(to);
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

            private Motion BuildMotion(MotionRef mr, string treeName)
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
                    if (m == null) throw new EmitException(GuidRefUnresolved(mr.RefGuid));
                    return m;
                }
                if (mr.Tree != null) return BuildTree(mr.Tree, treeName);
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

            private BlendTree BuildTree(BlendTreeSpec spec, string name)
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
                    var childMotion = BuildMotion(child.Motion, name + "_" + i);
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

            // ----- behaviours (driver fully; the other six kinds fail loud) -----

            private void EmitBehaviours(List<Behaviour> behaviours, Func<Type, StateMachineBehaviour> add)
            {
                if (behaviours == null) return;
                foreach (var b in behaviours)
                {
                    if (b.Kind == "driver")
                    {
                        var drv = (VRCAvatarParameterDriver)add(typeof(VRCAvatarParameterDriver));
                        PopulateDriver(drv, b.Fields);
                    }
                    else
                    {
                        throw new EmitException(
                            $"behaviour kind '{b.Kind}' declared but emission not implemented in pair 1 — driver only; others land with pair 2 fixtures");
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
