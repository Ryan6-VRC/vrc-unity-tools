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
    }
}
