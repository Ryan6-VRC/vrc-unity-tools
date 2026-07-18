using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The package's canonical indexed-path hierarchy primitive: locate a transform under one root by
    /// the same root-relative path it occupies under another, disambiguating duplicate-named siblings
    /// by child-order occurrence rather than by name alone. A bare name is ambiguous when a parent has
    /// several children sharing it (common on VRChat rigs — e.g. multiple <c>Col_*</c> anchors or hair
    /// chains); the disambiguation counts how many same-name siblings precede the target in child order
    /// and selects the Nth same-name child on the destination side, so the right one of N is always
    /// chosen. <c>ConstrainedDuplicate</c>'s segment keys delegate here.
    ///
    /// Shared, unchanged, by <see cref="RemapReferencesByPath"/> (read-only path resolution) and
    /// <see cref="ScaffoldBuilder"/> (which reuses an existing child at the indexed path or mints one) —
    /// the single source of truth so the two never silently disagree on which GameObject is "the" target.
    /// Pure UnityEngine/Transform helpers; no editor or VRC dependencies.
    /// </summary>
    public static class IndexedPath
    {
        /// <summary>
        /// How many same-name siblings appear before <paramref name="t"/> in its parent's child list
        /// (its occurrence index among siblings sharing its name); 0 if it has no parent. Pairs with
        /// <see cref="NthChildWithName"/> to address one of several duplicate-named children
        /// deterministically.
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
        /// <paramref name="t"/>'s occurrence index among siblings that RESOLVE to <paramref name="mapped"/>
        /// under <paramref name="map"/> (count of preceding siblings whose <c>Substitute(name, map) == mapped</c>).
        /// The rename-space analogue of <see cref="SiblingIndexAmongSameName"/>, kept SYMMETRIC with
        /// <see cref="CountChildrenResolvingTo"/> so the count guard and the dest-lookup index live in the same
        /// space — otherwise a mapped-key child and a literal-<paramref name="mapped"/> sibling can both land on
        /// dest occurrence 0 (silent mis-bind). With a null/non-funneling map, <c>mapped == t.name</c> and this
        /// reduces to <see cref="SiblingIndexAmongSameName"/> — byte-identical to today.
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
        /// The inverse of <see cref="SiblingIndexAmongSameName"/>: given an occurrence index from the
        /// source side, returns the corresponding duplicate-named child on the destination side.
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
        /// How many children of <paramref name="parent"/> are named <paramref name="name"/>. Pairs with
        /// <see cref="NthChildWithName"/> / <see cref="SiblingIndexAmongSameName"/> so a minter can verify
        /// it is appending at exactly the right occurrence index (count == idx) rather than minting a
        /// same-named sibling at a LOWER occurrence than the source's (count &lt; idx → unrepresentable).
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
        /// How many children of <paramref name="parent"/> RESOLVE to <paramref name="mapped"/> under
        /// <paramref name="map"/> — i.e. <c>Substitute(child.name, map) == mapped</c>. The source side of the
        /// A1 count-equality guard: it counts not just children literally named <paramref name="mapped"/> but
        /// also any whose name is a map key pointing at it, so two effective source names collapsing onto one
        /// dest name (a mapped key colliding with an unmapped literal sibling, e.g. <c>{"Other" ⇒ "Armature"}</c>
        /// alongside an existing <c>Armature</c>) is caught — the exact silent-mis-bind A1 exists to stop.
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
        /// The A1 count-equality guard, shared by <see cref="FindByIndexedPath"/> and
        /// <see cref="ScaffoldBuilder"/>'s host walk so both fail-loud identically. It applies ONLY where a
        /// rename actually funnels (or defunnels) siblings onto <paramref name="mapped"/> at this parent — i.e.
        /// the source's resolving-to-<paramref name="mapped"/> count differs from its literal count of that
        /// name; otherwise (null map, or a plain segment with no rename effect) it is a no-op, keeping the
        /// null-map path byte-identical. When it applies, the source's resolving-to count MUST equal the dest's
        /// literal count of <paramref name="mapped"/>, else the occurrence index cannot address a unique dest
        /// sibling → return false with a named reason (never a silent wrong-sibling bind).
        /// </summary>
        internal static bool GuardRename(Transform srcParent, Transform dstParent, string mapped,
                                         IDictionary<string, string> map, out string failReason)
        {
            failReason = null;
            if (map == null) return true;
            int srcResolving = CountChildrenResolvingTo(srcParent, mapped, map);
            int srcLiteral   = CountChildrenWithName(srcParent, mapped);
            if (srcResolving == srcLiteral) return true;   // no rename funnels 'mapped' here → not an A1 case
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
        /// Returns the transform under <paramref name="dstRoot"/> at the same indexed path that
        /// <paramref name="srcTarget"/> occupies under <paramref name="srcRoot"/>, or null if that path
        /// is missing under dstRoot. Walks the root-exclusive segment chain top→target, resolving each
        /// segment by its duplicate-named-sibling occurrence index (see the type summary). Allocation is
        /// limited to the segment list — no path strings are built. <paramref name="srcTarget"/> must be
        /// under <paramref name="srcRoot"/>; callers guard that case.
        /// </summary>
        public static Transform FindByIndexedPath(Transform srcRoot, Transform dstRoot, Transform srcTarget)
            => FindByIndexedPath(srcRoot, dstRoot, srcTarget, null, out _);

        /// <summary>
        /// As <see cref="FindByIndexedPath(Transform,Transform,Transform)"/>, but a <paramref name="vendorToOwned"/>
        /// (<c>vendorName ⇒ ownedName</c>, source-name key → destination-name value) substitutes the segment
        /// NAME at each destination-side <see cref="NthChildWithName"/> lookup. The occurrence index is still
        /// counted on the SOURCE side, but in the resolving-to-<c>mapped</c> space
        /// (<see cref="SiblingIndexAmongResolvingTo"/>) so it stays symmetric with the dst lookup — rename
        /// preserves child order, so a source segment at occurrence k among siblings resolving to the mapped
        /// name maps to the dst child at occurrence k under that name. An empty/absent/null map ⇒ every
        /// <see cref="Substitute"/> returns its input ⇒ byte-identical to the null-map overload.
        ///
        /// A1 count-equality guard (silent-mis-bind hole, fail-loud): the dst lookup uses the MAPPED name, so
        /// a mapped value that already exists on the dst (or a non-injective map slipping a validation gap)
        /// could silently bind the WRONG sibling. <see cref="GuardRename"/> closes it — where a rename actually
        /// funnels source siblings onto <c>mapped</c> at a parent (its resolving-to count differs from its
        /// literal count), it requires
        /// <c>CountChildrenResolvingTo(srcParent, mapped, map) == CountChildrenWithName(dstParent, mapped)</c>;
        /// on mismatch it sets <paramref name="failReason"/> (a named signal, distinct from a plain missing
        /// path) and this returns null. A null or non-funneling map never trips it.
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
                // The destination segment name is the mapped one; the occurrence index is counted on the
                // SOURCE side in the SAME (resolving-to-mapped) space as the dest lookup, so a mapped-key child
                // and a literal-mapped sibling never collapse onto one dest occurrence. Null/non-funneling map
                // ⇒ mapped == seg.name and this reduces to same-name indexing — byte-identical to today.
                string mapped = Substitute(seg.name, vendorToOwned);
                int idx = SiblingIndexAmongResolvingTo(seg, mapped, vendorToOwned);
                if (!GuardRename(seg.parent, cur, mapped, vendorToOwned, out failReason)) return null;
                cur = NthChildWithName(cur, mapped, idx);
                if (cur == null) return null;
            }
            return cur;
        }

        /// <summary>
        /// The rename-substitution primitive: <c>map != null &amp;&amp; map.TryGetValue(name, out v) ? v : name</c>.
        /// Null-safe and empty-safe on its own — walk primitives call it per segment with no re-validation.
        /// Direction is <c>vendorName ⇒ ownedName</c> (source-name key → destination-name value): the walk
        /// matches a SOURCE segment name against DEST children, so the key is the source (vendor) name.
        /// Matching is Ordinal/case-sensitive (GameObject-name lookup via <see cref="NthChildWithName"/> is);
        /// never lowercase.
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
        /// Door-level validation for a caller-supplied rename map (called once per <c>Run</c>, before any
        /// mutation): drop entries with a null/empty key or value, then REJECT a non-injective map — two
        /// keys mapping to one value cannot address a unique dst sibling (schema-can't-lie). On a collision,
        /// the colliding keys are returned in <paramref name="collidingKeys"/> and the method returns null so
        /// the caller FAILs loud, naming them. On success, returns a fresh <b>Ordinal</b> dictionary (name
        /// matching is case-sensitive), or null when no usable entries remain (empty-after-cleaning ⇒ the
        /// same no-op as an absent map). This is NOT <c>ConformRenderers.NormalizeRenameMap</c> — that one
        /// lowercases (case-insensitive material match); GameObject-name matching must stay Ordinal.
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
                    // Name the first key of THIS collision group once, then every later colliding key — so a
                    // map with several independent collisions ({A⇒X,B⇒X,C⇒Y,D⇒Y}) reports all of A,B,C,D.
                    if (reportedValues.Add(kv.Value)) collidingKeys.Add(firstKey);
                    collidingKeys.Add(kv.Key);
                    continue;   // keep scanning so all colliding keys are named
                }
                valueToKey[kv.Value] = kv.Key;
                cleaned[kv.Key] = kv.Value;
            }
            if (collidingKeys != null) return null;   // non-injective → caller FAILs loud
            return cleaned.Count > 0 ? cleaned : null;
        }
    }
}
