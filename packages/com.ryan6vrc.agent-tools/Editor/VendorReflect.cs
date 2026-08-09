using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one home for vendor (MA / VRCFury / NDMF) reflection plumbing: by-name type resolution, the
    /// MA-installed probe, and the pinned vendor-method handles the checkers INVOKE instead of re-deriving
    /// vendor behavior from field values (a hand-copy of a vendor resolution order was correct once and
    /// drifted silently — twice; invoking the real method can't drift, and a broken pin fails loud).
    /// The two resolver seams are injectable exactly like <see cref="CheckAvatar"/>'s frame seams: real
    /// vendor APIs always reflect in a live Editor, so the drift/degrade branches are otherwise
    /// unexercisable — a test swaps a seam in SetUp and restores it in TearDown. Defaults are the real
    /// behavior, and the seams run unconditionally on the hot path (no test-only conditional in production).
    /// </summary>
    internal static class VendorReflect
    {
        /// <summary>Resolve a type by full name across the loaded domain; null when genuinely absent.
        /// Probes each assembly by name (<c>asm.GetType</c>), so one unloadable sibling type can never hide
        /// the target the way a <c>GetTypes()</c> enumeration drop could.</summary>
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

        /// <summary>MA-present probe independent of any single type resolving: the runtime assembly name is
        /// far more stable than a type's FullName, so a null <see cref="FindType"/> WITH this true means the
        /// type drifted (surface loud), vs MA genuinely absent (the legitimate silent floor). An assembly
        /// rename would defeat it, but that is a far larger break.</summary>
        internal static bool ModularAvatarInstalled()
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "nadena.dev.modular-avatar.core") return true;
            return false;
        }

        // ── MA AvatarObjectReference.Get(Component) ──────────────────────────────────────────────────────

        /// <summary>Boxes an <c>AvatarObjectReference</c> property (default <c>p.boxedValue</c>, which THROWS
        /// for unsupported shapes). A test swaps a throwing variant to exercise the caught-and-degrade path.</summary>
        internal static Func<SerializedProperty, object> GetBoxedValue = p => p.boxedValue;

        /// <summary>Resolves the pinned <c>Get(Component)→GameObject</c> instance overload for a boxed
        /// reference type (default <see cref="PinAorGetOverloadImpl"/>; null ⇒ unreachable/drift → the
        /// caller runs its explicitly-labeled degraded fallback, loud).</summary>
        internal static Func<Type, MethodInfo> ResolveAorGetOverload = PinAorGetOverloadImpl;

        // Cached pinned instance overload: AvatarObjectReference.Get(Component) -> GameObject. Pinned by
        // parameter type == Component AND return type == GameObject, so a future/other Get overload (e.g. the
        // static Get(SerializedProperty), whose resolution order differs — nondestructive.md owns why) can
        // never be silently mis-bound. Memoized PER TYPE (null included): ScanSceneRefs pins the runtime
        // type of anything AOR-*shaped* (a referencePath + targetObject child pair), so a single global slot
        // would let whichever type arrived first answer for every later one — and a foreign first arrival
        // would silently blind every consumer of the pin.
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

        /// <summary>The diagnostic name+message for a failed reflective invoke: unwraps the
        /// <see cref="TargetInvocationException"/> shell <c>MethodInfo.Invoke</c> puts around whatever the
        /// vendor method threw, so a reason string names the actual cause instead of always reading
        /// "TargetInvocationException".</summary>
        internal static string DescribeInvokeError(Exception e)
        {
            var inner = (e as TargetInvocationException)?.InnerException ?? e;
            return inner.GetType().Name + ": " + inner.Message;
        }

        // ── VRCFury AnimationBindingUtils.RewriteRelativePath(string, IReadOnlyList<BindingRewrite>) ─────

        /// <summary>Resolves the pinned <c>RewriteRelativePath(string, IReadOnlyList&lt;BindingRewrite&gt;)→string</c>
        /// internal static — the build's own Path-Rewrite-Rules application (moved out of
        /// <c>FullControllerBuilder.RewritePath(FullController, string)</c> at VRCFury 1.1380.0), invoked on the
        /// caller's live <c>rewriteBindings</c> list so the rule semantics can never drift (and so no near-copy
        /// of VRCFury code lives in this repo — VRCFury is not FOSS-licensed, so a hand-rolled replication is a
        /// licensing question as well as a drift hazard). null ⇒ unreachable (API drift / VRCFury absent) → the
        /// caller anchors it, loud. Param order is (path, rules) — the reverse of the pre-1.1414 method.</summary>
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
                    // Pinned by shape, not just name: ps[0] the path, ps[1] a generic collection whose element
                    // type is FullController.BindingRewrite — so a future overload can never silently mis-bind.
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
        /// because none of them is usable alone. Field order is the RESOLUTION order: a release that drifts
        /// two members names the earlier one, so reordering these silently changes which member the operator
        /// is sent to.</summary>
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
        }

        /// <summary>Resolves the ArmatureLink pin set. <b>null means VRCFury is ABSENT</b> — the legitimate
        /// silent floor, on which the caller returns without recording anything. That is the OPPOSITE of the
        /// two seams above, where null means drift and the caller runs a loud degraded fallback, so this
        /// method deliberately has <b>no catch</b>: every drift branch THROWS, and <c>CheckSeam</c>'s
        /// ClassifyReflect (which owns that taxonomy) maps Missing*/TypeLoad onto an error-severity REFUSE.
        /// Swallowing a drift to null here would instead hand the collector an empty result, which reads
        /// downstream as "no seam — bare prop" at warning: a broken tool telling the operator to add a seam
        /// that already exists. Not memoized either, because a memo would have to cache a throw.</summary>
        internal static VrcfArmatureLinkPins ResolveVrcfArmatureLink()
        {
            var pins = new VrcfArmatureLinkPins();
            pins.VrcfType = FindType("VF.Model.VRCFury");
            if (pins.VrcfType == null) return null; // VRCFury not installed ⇒ no VRCFury seam

            pins.ArmLinkType = FindType("VF.Model.Feature.ArmatureLink");
            pins.VfGoType = FindType("VF.Utils.VFGameObject");
            var svcType = FindType("VF.Service.ArmatureLinkService");
            var pathCacheType = FindType("VF.Builder.VRCFObjectPathCache");
            var armCacheType = FindType("VF.Builder.VRCFArmatureCache");
            if (pins.ArmLinkType == null || svcType == null || pins.VfGoType == null
                || pathCacheType == null || armCacheType == null)
                throw new TypeLoadException("VRCFury ArmatureLink/Service/VFGameObject/cache type missing");

            // The full parameter SHAPE is asserted at pin time — types, not arity alone — so a signature
            // change fails HERE as named drift. Arity alone passes on 1.1380.0 (param 3 is
            // IReadOnlyList<VRCFObjectPathCache> there) and the invoke then throws ArgumentException into
            // ClassifyReflect's wrong arm; the type assert is what closes that.
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
            // Deliberately a WEAKER assert than its neighbours — arity plus param[0] only. Its param 1 is
            // VRCFury's internal `Links`, a type the collector never names (it holds whatever GetLinks handed
            // back), so asserting it would pin a type nothing else here resolves. Tightening this to
            // ParamTypesAre would fail a pin that passes today.
            pins.GetScalingFactor = svcType.GetMethod("GetScalingFactor", BindingFlags.Public | BindingFlags.Static);
            if (pins.GetScalingFactor == null || pins.GetScalingFactor.GetParameters().Length != 2
                || pins.GetScalingFactor.GetParameters()[0].ParameterType != pins.ArmLinkType)
                throw new MissingMethodException("ArmatureLinkService.GetScalingFactor(ArmatureLink, Links)");
            return pins;
        }

        /// <summary>True when the method's parameter list is exactly the given types, in order. The pins assert
        /// TYPES, not arity: a same-arity retype (measured — GetLinks param 3 across 1.1380.0→1.1400.0) passes
        /// every arity check and then throws ArgumentException at the invoke, into the wrong arm of
        /// <c>CheckSeam</c>'s ClassifyReflect.</summary>
        internal static bool ParamTypesAre(MethodInfo m, params Type[] types)
        {
            var ps = m.GetParameters();
            if (ps.Length != types.Length) return false;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType != types[i]) return false;
            return true;
        }

        /// <summary>Transform/GameObject → VFGameObject via the explicit op_Implicit (reflection won't apply
        /// it implicitly).</summary>
        internal static object ToVfGameObject(Type vfGoType, GameObject go)
        {
            foreach (var m in vfGoType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "op_Implicit" || m.ReturnType != vfGoType) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (ps[0].ParameterType == typeof(Transform)) return m.Invoke(null, new object[] { go.transform });
                if (ps[0].ParameterType == typeof(GameObject)) return m.Invoke(null, new object[] { go });
            }
            throw new MissingMethodException("op_Implicit(Transform|GameObject) → VFGameObject");
        }

        /// <summary>VFGameObject → Transform via the explicit op_Implicit (either the Transform or the
        /// GameObject operator).</summary>
        internal static Transform FromVfGameObject(Type vfGoType, object vfGo)
        {
            if (vfGo == null) return null;
            foreach (var m in vfGoType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "op_Implicit") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != vfGoType) continue;
                var result = m.Invoke(null, new object[] { vfGo });
                if (result is Transform t) return t;
                if (result is GameObject g) return g.transform;
            }
            throw new MissingMethodException("op_Implicit(VFGameObject) → Transform|GameObject");
        }
    }
}
