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
        // never be silently mis-bound. Sentinel _aorPinAttempted guards a one-time reflect; null MethodInfo
        // ⇒ unreachable (API drift / MA absent).
        private static bool _aorPinAttempted;
        private static MethodInfo _aorGetOverload;
        private static MethodInfo PinAorGetOverloadImpl(Type aorType)
        {
            if (_aorPinAttempted) return _aorGetOverload;
            _aorPinAttempted = true;
            try
            {
                foreach (var m in aorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "Get" || m.ReturnType != typeof(GameObject)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Component)) { _aorGetOverload = m; break; }
                }
            }
            catch { _aorGetOverload = null; }
            return _aorGetOverload;
        }

        // ── VRCFury FullControllerBuilder.RewritePath(FullController, string) ────────────────────────────

        /// <summary>Resolves the pinned <c>RewritePath(FullController, string)→string</c> private static —
        /// the build's own Path-Rewrite-Rules application, invoked on the caller's boxed feature model so the
        /// rule semantics can never drift (and so no near-copy of VRCFury code lives in this repo — VRCFury
        /// is not FOSS-licensed, so a hand-rolled replication is a licensing question as well as a drift
        /// hazard). null ⇒ unreachable (API drift / VRCFury absent) → the caller anchors it, loud.</summary>
        internal static Func<MethodInfo> ResolveVrcfRewritePath = PinVrcfRewritePathImpl;

        private static bool _vrcfPinAttempted;
        private static MethodInfo _vrcfRewritePath;
        private static MethodInfo PinVrcfRewritePathImpl()
        {
            if (_vrcfPinAttempted) return _vrcfRewritePath;
            _vrcfPinAttempted = true;
            try
            {
                var t = FindType("VF.Feature.FullControllerBuilder");
                if (t == null) return null;
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "RewritePath" || m.ReturnType != typeof(string)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType.FullName == "VF.Model.Feature.FullController"
                                       && ps[1].ParameterType == typeof(string))
                    { _vrcfRewritePath = m; break; }
                }
            }
            catch { _vrcfRewritePath = null; }
            return _vrcfRewritePath;
        }
    }
}
