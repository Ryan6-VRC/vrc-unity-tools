using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Vendor (MA / VRCFury) reflection: by-name type resolution, the MA-installed probe, and the pinned
    /// vendor-method handles a checker invokes rather than re-deriving vendor behavior from field values.
    /// The <c>Func</c> fields are test seams: real vendor APIs always reflect in a live Editor, so a test
    /// swaps one to reach a drift or degrade branch.
    /// </summary>
    internal static class VendorReflect
    {
        /// <summary>Resolves a type by full name across the loaded domain; null when absent. Probes each
        /// assembly by name, because a <c>GetTypes()</c> enumeration can lose the target to an unloadable
        /// sibling type.</summary>
        internal static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); }
                catch { continue; } // dynamic / unloadable assembly — skip
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>MA-present probe independent of any single type resolving: a null <see cref="FindType"/>
        /// with this true means the type drifted (surface it loud); with this false, MA is absent (the
        /// legitimate silent floor).</summary>
        internal static bool ModularAvatarInstalled()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "nadena.dev.modular-avatar.core") return true;
            return false;
        }

        // ── MA AvatarObjectReference.Get(Component) ──────────────────────────────────────────────────────

        /// <summary>Boxes an <c>AvatarObjectReference</c> property. <c>boxedValue</c> throws for unsupported
        /// shapes, so a caller catches and degrades.</summary>
        internal static Func<SerializedProperty, object> GetBoxedValue = p => p.boxedValue;

        /// <summary>Resolves the pinned <c>Get(Component)→GameObject</c> instance overload for a boxed
        /// reference type. Null means drift: the caller runs its explicitly-labeled degraded fallback, loud.</summary>
        internal static Func<Type, MethodInfo> ResolveAorGetOverload = PinAorGetOverloadImpl;

        // Memoized per type, null included: ScanSceneRefs pins the runtime type of anything AOR-shaped (a
        // referencePath + targetObject child pair), so one global slot would let a foreign first arrival
        // answer for every later type. MA's static Get(SerializedProperty) resolves differently from this
        // instance overload (nondestructive.md owns how), so it is never the pin.
        private static readonly System.Collections.Generic.Dictionary<Type, MethodInfo> _aorPins =
            new System.Collections.Generic.Dictionary<Type, MethodInfo>();
        private static MethodInfo PinAorGetOverloadImpl(Type aorType)
        {
            if (_aorPins.TryGetValue(aorType, out var cached)) return cached;
            MethodInfo pin = null;
            try
            {
                foreach (var m in aorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "Get" || m.ReturnType != typeof(GameObject)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Component)) { pin = m; break; }
                }
            }
            catch { pin = null; }
            _aorPins[aorType] = pin;
            return pin;
        }

        internal static string DescribeInvokeError(Exception e)
        {
            var inner = (e as TargetInvocationException)?.InnerException ?? e;
            return inner.GetType().Name + ": " + inner.Message;
        }

        // ── VRCFury AnimationBindingUtils.RewriteRelativePath(string, IReadOnlyList<BindingRewrite>) ─────

        /// <summary>Resolves the pinned <c>RewriteRelativePath(string, IReadOnlyList&lt;BindingRewrite&gt;)→string</c>
        /// internal static, the build's own Path-Rewrite-Rules application, invoked on the caller's live
        /// <c>rewriteBindings</c> list. VRCFury is not FOSS-licensed, so no hand-copy of it lives in this repo.
        /// Null means unreachable (API drift or VRCFury absent): the caller anchors it, loud.</summary>
        internal static Func<MethodInfo> ResolveVrcfRewritePath = PinVrcfRewritePathImpl;

        private static bool _vrcfPinAttempted;
        private static MethodInfo _vrcfRewritePath;
        private static MethodInfo PinVrcfRewritePathImpl()
        {
            if (_vrcfPinAttempted) return _vrcfRewritePath;
            _vrcfPinAttempted = true;
            try
            {
                var t = FindType("VF.Utils.AnimationBindingUtils");
                if (t == null) return null;
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "RewriteRelativePath" || m.ReturnType != typeof(string)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string)
                                       && ps[1].ParameterType.IsGenericType
                                       && ps[1].ParameterType.GetGenericArguments().Length == 1
                                       && ps[1].ParameterType.GetGenericArguments()[0].FullName == "VF.Model.Feature.FullController+BindingRewrite")
                    { _vrcfRewritePath = m; break; }
                }
            }
            catch { _vrcfRewritePath = null; }
            return _vrcfRewritePath;
        }

        // ── VRCFury ArmatureLinkService.GetLinks + the ArmatureLink walk's supporting handles ─────────────

        /// <summary>Every pinned handle <c>CheckSeam</c>'s VRCFury collector invokes, resolved as one set
        /// because none is usable alone. The method and field pins are declared in resolution order, and
        /// that order is load-bearing: each has its own throw site, so a release that drifts two names the
        /// earlier one, and reordering changes which member the operator is sent to.</summary>
        internal sealed class VrcfArmatureLinkPins
        {
            internal Type VrcfType;                     // VF.Model.VRCFury — the component the collector sweeps for
            internal Type ArmLinkType;                  // VF.Model.Feature.ArmatureLink — the `content` shape it acts on
            internal Type VfGoType;                     // VF.Utils.VFGameObject
            internal MethodInfo GetLinks;               // static (ArmatureLink, VFGameObject, VRCFObjectPathCache, VRCFArmatureCache)
            internal MethodInfo PathCacheFactory;       // static VRCFObjectPathCache.GetPerFrame(VFGameObject)
            internal MethodInfo ArmCacheFactory;        // static VRCFArmatureCache.GetPerFrame(VFGameObject)
            internal FieldInfo ContentField;            // VRCFury.content
            internal FieldInfo ForceOneWorldScaleField; // ArmatureLink.forceOneWorldScale
            internal MethodInfo GetScalingFactor;       // static (ArmatureLink, Links) → (float,float,float)
            internal MethodInfo ToVfGo;                 // static op_Implicit(Transform|GameObject) → VFGameObject
            internal bool ToVfGoTakesTransform;         // which arm ToVfGo resolved to — frozen with the pin
            internal MethodInfo FromVfGo;               // static op_Implicit(VFGameObject) → Transform (preferred) | GameObject
        }

        /// <summary>Resolves the ArmatureLink pin set. Null means VRCFury is absent, the silent floor on which
        /// the caller records nothing: the opposite of the seams above, where null means drift. So this has
        /// no catch. Every drift branch throws, and <c>CheckSeam</c>'s ClassifyReflect turns it into an
        /// error-severity REFUSE; swallowed to null, a drift would read downstream as "no seam — bare prop", a
        /// broken tool telling the operator to add a seam that already exists. Not memoized, because a memo
        /// would have to cache a throw.</summary>
        internal static VrcfArmatureLinkPins ResolveVrcfArmatureLink()
        {
            var pins = new VrcfArmatureLinkPins();
            pins.VrcfType = FindType("VF.Model.VRCFury");
            if (pins.VrcfType == null) return null;

            pins.ArmLinkType = FindType("VF.Model.Feature.ArmatureLink");
            pins.VfGoType = FindType("VF.Utils.VFGameObject");
            var svcType = FindType("VF.Service.ArmatureLinkService");
            var pathCacheType = FindType("VF.Builder.VRCFObjectPathCache");
            var armCacheType = FindType("VF.Builder.VRCFArmatureCache");
            if (pins.ArmLinkType == null || svcType == null || pins.VfGoType == null
                || pathCacheType == null || armCacheType == null)
                throw new TypeLoadException("VRCFury ArmatureLink/Service/VFGameObject/cache type missing");

            pins.GetLinks = svcType.GetMethod("GetLinks", BindingFlags.Public | BindingFlags.Static);
            if (pins.GetLinks == null || !ParamTypesAre(pins.GetLinks, pins.ArmLinkType, pins.VfGoType, pathCacheType, armCacheType))
                throw new MissingMethodException("ArmatureLinkService.GetLinks(ArmatureLink, VFGameObject, VRCFObjectPathCache, VRCFArmatureCache)");
            pins.PathCacheFactory = pathCacheType.GetMethod("GetPerFrame", BindingFlags.Public | BindingFlags.Static);
            if (pins.PathCacheFactory == null || !ParamTypesAre(pins.PathCacheFactory, pins.VfGoType))
                throw new MissingMethodException("VRCFObjectPathCache.GetPerFrame(VFGameObject)");
            pins.ArmCacheFactory = armCacheType.GetMethod("GetPerFrame", BindingFlags.Public | BindingFlags.Static);
            if (pins.ArmCacheFactory == null || !ParamTypesAre(pins.ArmCacheFactory, pins.VfGoType))
                throw new MissingMethodException("VRCFArmatureCache.GetPerFrame(VFGameObject)");
            pins.ContentField = pins.VrcfType.GetField("content", BindingFlags.Public | BindingFlags.Instance);
            if (pins.ContentField == null) throw new MissingFieldException("VRCFury.content");
            pins.ForceOneWorldScaleField = pins.ArmLinkType.GetField("forceOneWorldScale", BindingFlags.Public | BindingFlags.Instance);
            if (pins.ForceOneWorldScaleField == null) throw new MissingFieldException("ArmatureLink.forceOneWorldScale");
            // Deliberately weaker than its neighbours, arity plus param 0 only: param 1 is ArmatureLinkService's
            // nested Links, a type the collector never names (it holds whatever GetLinks handed back).
            pins.GetScalingFactor = svcType.GetMethod("GetScalingFactor", BindingFlags.Public | BindingFlags.Static);
            if (pins.GetScalingFactor == null || pins.GetScalingFactor.GetParameters().Length != 2
                || pins.GetScalingFactor.GetParameters()[0].ParameterType != pins.ArmLinkType)
                throw new MissingMethodException("ArmatureLinkService.GetScalingFactor(ArmatureLink, Links)");

            // Both converters are selected on return type as well as parameter: VFGameObject declares four
            // op_Implicit taking VFGameObject (to GameObject, Object, Transform and bool), and GetMethods() order
            // is unspecified, so a parameter-only pin could freeze the bool operator on some runs. Transform wins
            // on both pins, so neither depends on enumeration order. The Object-returning arm is refused rather
            // than ranked third, so a release keeping only that arm fails here as named drift.
            foreach (var m in pins.VfGoType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "op_Implicit") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (m.ReturnType == pins.VfGoType)
                {
                    if (ps[0].ParameterType == typeof(Transform))
                    { pins.ToVfGo = m; pins.ToVfGoTakesTransform = true; }
                    else if (ps[0].ParameterType == typeof(GameObject) && pins.ToVfGo == null)
                    { pins.ToVfGo = m; pins.ToVfGoTakesTransform = false; }
                }
                else if (ps[0].ParameterType == pins.VfGoType)
                {
                    if (m.ReturnType == typeof(Transform)) pins.FromVfGo = m;
                    else if (m.ReturnType == typeof(GameObject) && pins.FromVfGo == null) pins.FromVfGo = m;
                }
            }
            if (pins.ToVfGo == null)
                throw new MissingMethodException("op_Implicit(Transform|GameObject) → VFGameObject");
            if (pins.FromVfGo == null)
                throw new MissingMethodException("op_Implicit(VFGameObject) → Transform|GameObject");
            return pins;
        }

        /// <summary>True when the method's parameter list is exactly the given types, in order. The pins assert
        /// types, not arity: a same-arity retype passes an arity check, then throws a bare ArgumentException at
        /// the invoke instead of naming the drifted member at pin time.</summary>
        internal static bool ParamTypesAre(MethodInfo m, params Type[] types)
        {
            var ps = m.GetParameters();
            if (ps.Length != types.Length) return false;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType != types[i]) return false;
            return true;
        }

        internal static object ToVfGameObject(VrcfArmatureLinkPins pins, GameObject go)
            => pins.ToVfGo.Invoke(null, new object[] { pins.ToVfGoTakesTransform ? (object)go.transform : go });

        internal static Transform FromVfGameObject(VrcfArmatureLinkPins pins, object vfGo)
        {
            if (vfGo == null) return null;
            var result = pins.FromVfGo.Invoke(null, new object[] { vfGo });
            if (result is Transform t) return t;
            if (result is GameObject g) return g.transform;
            throw new MissingMethodException("op_Implicit(VFGameObject) → Transform|GameObject returned "
                                            + (result?.GetType().Name ?? "null"));
        }
    }
}
