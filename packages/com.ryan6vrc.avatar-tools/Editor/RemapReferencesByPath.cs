using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    public static class RemapReferencesByPath
    {
        public struct Result
        {
            public int remapped;
            public int nulled;
            /// <summary>
            /// SerializedProperty paths of the refs this call set null (count == <see cref="nulled"/>).
            /// Lets a caller classify each null against the component's table descriptor — a dropped
            /// soft-dep (e.g. <c>ignoreTransforms</c> entry) is behavior-inert, while a hard-dep null may
            /// block the build. Additive; callers that don't classify (GraftHierarchy) ignore it.
            /// </summary>
            public List<string> nulledPaths;
            /// <summary>
            /// Named ambiguous-rename reasons (A1 count-equality guard) hit while resolving refs under a
            /// <c>renameMap</c>: the ref still NULLS (counted in <see cref="nulled"/>/<see cref="nulledPaths"/>
            /// as a plain missing path), but the reason rides here so the caller can name it rather than let
            /// it hide as an ordinary null. Empty on the null-map path (the guard never fires). Callers that
            /// don't thread a map ignore it.
            /// </summary>
            public List<string> renameWarnings;
        }

        /// <summary>
        /// Rebinds every ObjectReference in <paramref name="so"/> whose target is under
        /// <paramref name="srcRoot"/> to the object at the same indexed hierarchy path under
        /// <paramref name="dstRoot"/>. References whose target is not under srcRoot (assets,
        /// scene-external objects) are left untouched. A reference is set null and counted in
        /// <see cref="Result.nulled"/> when the indexed path is missing under dstRoot OR the
        /// destination object lacks the referenced component type; successful rebinds are counted
        /// in <see cref="Result.remapped"/>. The caller owns <see cref="SerializedObject.Update"/>
        /// beforehand and <c>ApplyModifiedProperties*()</c> afterward.
        ///
        /// <paramref name="renameMap"/> (<c>vendorName ⇒ ownedName</c>, injective, Ordinal — see
        /// <see cref="IndexedPath.ValidateRenameMap"/>) substitutes the destination-side segment name at the
        /// armature-root (and any other renamed) level. Empty/absent/null ⇒ byte-identical to today.
        /// </summary>
        public static Result Remap(SerializedObject so, Transform srcRoot, Transform dstRoot,
                                   IDictionary<string, string> renameMap = null)
        {
            var res = new Result { nulledPaths = new List<string>(), renameWarnings = new List<string>() };
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                var o = it.objectReferenceValue;
                if (o == null) continue;
                Transform t = o is Component c ? c.transform : (o is GameObject g ? g.transform : null);
                if (t == null || !t.IsChildOf(srcRoot)) continue;       // only refs under srcRoot
                var dt = IndexedPath.FindByIndexedPath(srcRoot, dstRoot, t, renameMap, out var failReason);
                if (dt == null)
                {
                    if (failReason != null) res.renameWarnings.Add(it.propertyPath + ": " + failReason);
                    it.objectReferenceValue = null; res.nulled++; res.nulledPaths.Add(it.propertyPath); continue;
                }
                if (o is Transform) it.objectReferenceValue = dt;
                else if (o is GameObject) it.objectReferenceValue = dt.gameObject;
                else
                {
                    // ASSUMPTION: at most one component of a given type per transform. When the
                    // destination has multiple same-type components, GetComponent returns the FIRST,
                    // so remapping to one-of-N same-type components would bind to the wrong one.
                    // Proper N-way disambiguation is deferred until a caller needs it; CopyComponents
                    // sidesteps this entirely by rebinding copied components through the SessionMap
                    // (vendor component → our copy) before this generic path-remap ever runs.
                    var comp = dt.GetComponent(o.GetType());
                    it.objectReferenceValue = comp;
                    if (comp == null) { res.nulled++; res.nulledPaths.Add(it.propertyPath); continue; }
                }
                res.remapped++;
            }
            return res;
        }

        /// <summary>
        /// Read-only sibling of <see cref="Remap"/>: returns the transform under
        /// <paramref name="dstRoot"/> at the same indexed hierarchy path that
        /// <paramref name="srcTarget"/> occupies under <paramref name="srcRoot"/>, or null if that
        /// path is missing under dstRoot (or srcTarget is null / not under srcRoot). For callers
        /// that must relocate a single transform rather than rebind a SerializedObject — e.g.
        /// placing a relocated constraint on its corresponding destination bone.
        /// </summary>
        public static Transform Counterpart(Transform srcRoot, Transform dstRoot, Transform srcTarget)
            => Counterpart(srcRoot, dstRoot, srcTarget, null, out _);

        /// <summary>
        /// As <see cref="Counterpart(Transform,Transform,Transform)"/>, threading a
        /// <paramref name="renameMap"/> (<c>vendorName ⇒ ownedName</c>) into the indexed-path walk so a host
        /// under a renamed armature root resolves. On the A1 ambiguous-rename guard, returns null and sets
        /// <paramref name="failReason"/> (a named signal distinct from a plain missing path). Empty/absent/null
        /// map ⇒ byte-identical to the null-map overload.
        /// </summary>
        public static Transform Counterpart(Transform srcRoot, Transform dstRoot, Transform srcTarget,
                                            IDictionary<string, string> renameMap, out string failReason)
        {
            failReason = null;
            if (srcTarget == null) return null;
            if (srcTarget == srcRoot) return dstRoot;
            if (!srcTarget.IsChildOf(srcRoot)) return null;
            return IndexedPath.FindByIndexedPath(srcRoot, dstRoot, srcTarget, renameMap, out failReason);
        }
    }
}
