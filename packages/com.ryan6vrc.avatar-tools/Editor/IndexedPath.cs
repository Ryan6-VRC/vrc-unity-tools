using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The package's indexed-path primitive: locate a transform under one root by the root-relative path
    /// it occupies under another, disambiguating duplicate-named siblings by child-order occurrence. Shared
    /// by <see cref="RemapReferencesByPath"/> and <see cref="ScaffoldBuilder"/> so the two never disagree
    /// on which GameObject is the target.
    /// </summary>
    public static class IndexedPath
    {
        /// <summary>
        /// How many same-name siblings precede <paramref name="t"/> in child order; 0 if it has no parent.
        /// The index <see cref="NthChildWithName"/> consumes on the destination side.
        /// </summary>
        public static int SiblingIndexAmongSameName(Transform t)
        {
            var parent = t.parent;
            if (parent == null) return 0;
            int idx = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var ch = parent.GetChild(i);
                if (ch == t) return idx;
                if (ch.name == t.name) idx++;
            }
            return idx;
        }

        /// <summary>
        /// <see cref="SiblingIndexAmongSameName"/> in rename space: the count of preceding siblings whose
        /// <c>Substitute(name, map) == mapped</c>. Symmetric with <see cref="CountChildrenResolvingTo"/> so the
        /// count guard and the dest-lookup index live in the same space; otherwise a mapped-key child and a
        /// literal-<paramref name="mapped"/> sibling can both land on dest occurrence 0 and silently mis-bind.
        /// </summary>
        internal static int SiblingIndexAmongResolvingTo(Transform t, string mapped, IDictionary<string, string> map)
        {
            var parent = t.parent;
            if (parent == null) return 0;
            int idx = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var ch = parent.GetChild(i);
                if (ch == t) return idx;
                if (Substitute(ch.name, map) == mapped) idx++;
            }
            return idx;
        }

        /// <summary>
        /// The <paramref name="n"/>th (0-based) child of <paramref name="parent"/> named
        /// <paramref name="name"/> in child order, or null if there are fewer than n+1 such children.
        /// </summary>
        public static Transform NthChildWithName(Transform parent, string name, int n)
        {
            int seen = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                var ch = parent.GetChild(i);
                if (ch.name == name) { if (seen == n) return ch; seen++; }
            }
            return null;
        }

        /// <summary>
        /// How many children of <paramref name="parent"/> are named <paramref name="name"/>. A minter can
        /// append at an occurrence index only when count == idx; count &lt; idx is unrepresentable.
        /// </summary>
        public static int CountChildrenWithName(Transform parent, string name)
        {
            if (parent == null) return 0;
            int n = 0;
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name == name) n++;
            return n;
        }

        /// <summary>
        /// How many children of <paramref name="parent"/> resolve to <paramref name="mapped"/> under
        /// <paramref name="map"/>. The source side of <see cref="GuardRename"/>: besides children literally
        /// named <paramref name="mapped"/> it counts any whose name is a map key pointing at it, which catches
        /// a mapped key colliding with an unmapped literal sibling (<c>{"Other" ⇒ "Armature"}</c> alongside an
        /// existing <c>Armature</c>).
        /// </summary>
        internal static int CountChildrenResolvingTo(Transform parent, string mapped, IDictionary<string, string> map)
        {
            if (parent == null) return 0;
            int n = 0;
            for (int i = 0; i < parent.childCount; i++)
                if (Substitute(parent.GetChild(i).name, map) == mapped) n++;
            return n;
        }

        /// <summary>
        /// The count-equality guard, shared by <see cref="FindByIndexedPath"/> and
        /// <see cref="ScaffoldBuilder"/>'s host walk so both fail identically. It applies only where a rename
        /// funnels or defunnels siblings onto <paramref name="mapped"/> at this parent, i.e. the source's
        /// resolving-to count differs from its literal count. There the resolving-to count must equal the
        /// dest's literal count, else the occurrence index cannot address a unique dest sibling and this
        /// returns false with a named reason.
        /// </summary>
        internal static bool GuardRename(Transform srcParent, Transform dstParent, string mapped,
                                         IDictionary<string, string> map, out string failReason)
        {
            failReason = null;
            if (map == null) return true;
            int srcResolving = CountChildrenResolvingTo(srcParent, mapped, map);
            int srcLiteral   = CountChildrenWithName(srcParent, mapped);
            if (srcResolving == srcLiteral) return true;   // no rename funnels 'mapped' here
            int dstCount = CountChildrenWithName(dstParent, mapped);
            if (srcResolving != dstCount)
            {
                failReason = "ambiguous rename onto '" + mapped + "': " + srcResolving +
                             " source sibling(s) resolve to it, dest has " + dstCount;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The transform under <paramref name="dstRoot"/> at the indexed path <paramref name="srcTarget"/>
        /// occupies under <paramref name="srcRoot"/>, or null if that path is missing.
        /// <paramref name="srcTarget"/> must be under <paramref name="srcRoot"/>; callers guard that case.
        /// </summary>
        public static Transform FindByIndexedPath(Transform srcRoot, Transform dstRoot, Transform srcTarget)
            => FindByIndexedPath(srcRoot, dstRoot, srcTarget, null, out _);

        /// <summary>
        /// As <see cref="FindByIndexedPath(Transform,Transform,Transform)"/>, but <paramref name="vendorToOwned"/>
        /// substitutes the segment name at each destination lookup. The occurrence index is counted on the
        /// source side among siblings resolving to the mapped name (<see cref="SiblingIndexAmongResolvingTo"/>):
        /// a rename preserves child order, so source occurrence k maps to dest occurrence k under that name.
        ///
        /// A mapped value already on the dest could otherwise bind the wrong sibling, so a
        /// <see cref="GuardRename"/> failure sets <paramref name="failReason"/> and returns null, a signal
        /// distinct from a plain missing path, which returns null with no reason.
        /// </summary>
        public static Transform FindByIndexedPath(Transform srcRoot, Transform dstRoot, Transform srcTarget,
                                                  IDictionary<string, string> vendorToOwned, out string failReason)
        {
            failReason = null;
            var chain = new List<Transform>();
            for (var p = srcTarget; p != null && p != srcRoot; p = p.parent) chain.Add(p);
            chain.Reverse();
            var cur = dstRoot;
            foreach (var seg in chain)
            {
                string mapped = Substitute(seg.name, vendorToOwned);
                int idx = SiblingIndexAmongResolvingTo(seg, mapped, vendorToOwned);
                if (!GuardRename(seg.parent, cur, mapped, vendorToOwned, out failReason)) return null;
                cur = NthChildWithName(cur, mapped, idx);
                if (cur == null) return null;
            }
            return cur;
        }

        /// <summary>
        /// The rename-substitution primitive. Matching is Ordinal, as GameObject-name lookup via
        /// <see cref="NthChildWithName"/> is; never lowercase.
        ///
        /// <para><b>The kit-wide rename-map invariant — canonical here.</b> A rename map's KEY names the
        /// hierarchy its tool WALKS; its VALUE names the hierarchy that tool RESOLVES INTO. Direction therefore
        /// follows traversal and is not a style choice: no tool can flip its map without inverting its own walk,
        /// since a dictionary cannot be indexed by its values.</para>
        ///
        /// <para>Hence the kit runs two opposite directions, both correct. The transplant tools walk vendor and
        /// resolve into ours (<c>vendorToOwned</c>); <c>ConformRenderers</c> walks our renderers and resolves
        /// into the source (<c>ownedToSource</c>). Cardinality follows: <c>vendorToOwned</c> must be injective
        /// or it cannot address a unique dst sibling (<see cref="ValidateRenameMap"/> rejects it), while
        /// <c>ownedToSource</c> is legitimately many-to-one — two owned meshes may take one source renderer's
        /// materials, which inverting it would make inexpressible. <b>Do not "reconcile" the two.</b></para>
        /// </summary>
        internal static string Substitute(string name, IDictionary<string, string> map)
            => map != null && name != null && map.TryGetValue(name, out var v) ? v : name;

        /// <summary>
        /// Door-level validation for a caller-supplied rename map, called once per <c>Run</c> before any
        /// mutation: drop identity entries and entries with a null/empty key or value, then reject a
        /// non-injective map, whose two keys for one value cannot address a unique dst sibling. On a collision
        /// it returns null with the keys in <paramref name="collidingKeys"/> so the caller fails naming them.
        /// Otherwise it returns a fresh Ordinal dictionary, or null when no usable entries remain, the same
        /// no-op as an absent map. This is NOT <c>ConformRenderers.NormalizeRenameMap</c>, which lowercases
        /// for a case-insensitive material match; GameObject-name matching must stay Ordinal.
        /// </summary>
        internal static IDictionary<string, string> ValidateRenameMap(IDictionary<string, string> vendorToOwned,
                                                                    out List<string> collidingKeys)
        {
            collidingKeys = null;
            if (vendorToOwned == null || vendorToOwned.Count == 0) return null;

            var cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
            var valueToKey = new Dictionary<string, string>(StringComparer.Ordinal);   // value → its first key
            var reportedValues = new HashSet<string>(StringComparer.Ordinal);          // firstKey already named
            foreach (var kv in vendorToOwned)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                if (string.Equals(kv.Key, kv.Value, StringComparison.Ordinal)) continue; // identity ⇒ no-op, drop
                if (valueToKey.TryGetValue(kv.Value, out var firstKey))
                {
                    collidingKeys = collidingKeys ?? new List<string>();
                    if (reportedValues.Add(kv.Value)) collidingKeys.Add(firstKey);
                    collidingKeys.Add(kv.Key);
                    continue;   // keep scanning so all colliding keys are named
                }
                valueToKey[kv.Value] = kv.Key;
                cleaned[kv.Key] = kv.Value;
            }
            if (collidingKeys != null) return null;
            return cleaned.Count > 0 ? cleaned : null;
        }
    }
}
