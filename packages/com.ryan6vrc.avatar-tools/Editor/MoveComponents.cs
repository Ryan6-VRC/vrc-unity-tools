using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Behavior-neutral relocation primitive — <c>MoveDynamics</c> generalized to a caller-supplied
    /// component-TYPE list (replacing the old single <c>mode</c>). For every component of a selected
    /// type whose effective anchor (its table anchor field, else its host GameObject's transform)
    /// descends from <paramref name="targetRoot"/>, the component is re-created on a fresh holder
    /// GameObject under <c>destPath</c> with its anchor PINNED back to that original transform — so
    /// behavior is unchanged — and the original is removed. Inbound serialized references to a moved
    /// component (a physbone's <c>colliders[]</c> entry to a relocated collider) are rewired to the
    /// fresh copy before the original is destroyed, so relocation is behavior-neutral for referenced
    /// types too, not just for anchors. The host bone it lived on is never moved
    /// (optimizers, not us, flatten the skeleton). No opinion about WHAT should move; the operator
    /// chooses via the (<c>targetRoot</c>, <c>typeNames</c>, <c>destPath</c>) calls they make.
    ///
    /// DELIBERATELY VRC-TABLE-BOUND (unlike Copy/Graft, which are open-extensible): a relocated type
    /// MUST have an anchor field in <see cref="VrcComponentTable"/> — that anchor is how the component
    /// is pinned. A targeted type with NO table anchor (Modular Avatar / VRCFury / NDMF, and Unity
    /// built-in constraints, which have no table row) FAILS LOUD by name BEFORE any mutation: without a
    /// pinnable anchor, moving the component would change its behavior, so Relocate refuses categorically
    /// (NDMF owns those components' build-time placement). The <c>mode → typeNames</c> change is signature
    /// uniformity with CopyComponents, not new open extensibility.
    ///
    /// Runs on the SCENE INSTANCE before prefab conversion (removing the original prefab-sourced
    /// component is version-finicky post-prefab). Idempotent: a component already living on a holder
    /// under <c>destPath</c> is counted <c>alreadyPlaced</c> and skipped. One collapsed Undo per call.
    /// </summary>
    [AgentTool]
    public static class MoveComponents
    {
        public static Transform ResolveAnchor(Transform explicitAnchor, Transform host)
            => explicitAnchor != null ? explicitAnchor : host;

        public static bool IsUnderOrEqual(Transform anchor, Transform targetRoot)
            => anchor != null && targetRoot != null && (anchor == targetRoot || anchor.IsChildOf(targetRoot));

        public static string Run(GameObject ownedRoot, Transform targetRoot, string[] typeNames,
                                 string destPath, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";
            var log = new RunLog("move-components")
            {
                whatIf   = whatIf,
                instance = ownedRoot != null ? ownedRoot.name : null,
                source   = targetRoot != null ? targetRoot.name : null,
            };

            if (ownedRoot == null) return Fail(log, label, "ownedRoot is null");
            if (targetRoot == null)  return Fail(log, label, "targetRoot is null (path resolved to no GameObject — typo?)");
            if (string.IsNullOrEmpty(destPath)) return Fail(log, label, "destPath is null/empty");

            // ── Resolve the selection types; surface unresolved/ambiguous loudly. ────────────────────
            var tr = TransplantCore.ResolveTypes(typeNames);
            if (tr.unresolved.Count > 0)
            {
                foreach (var u in tr.unresolved) log.Offender("type-name: " + u);
                return Fail(log, label, "unresolved type names");
            }
            if (tr.resolved.Count == 0)
                return Fail(log, label, "no type names given (typeNames is null/empty)");

            // ── Type-level anchor pre-check (the key refusal). ───────────────────────────────────────
            // Each selected type MUST resolve to a VRC table descriptor with a non-empty anchorFieldPaths;
            // a type with no row, or a row with no anchor field, cannot be pinned → relocating it would
            // change behavior → FAIL LOUD by name, BEFORE any enumeration or mutation, even if zero
            // instances exist on the avatar. This is how Relocate refuses MA/VRCF/NDMF and Unity built-in
            // constraints (no table row → no anchor).
            foreach (var t in tr.resolved)
            {
                var d = VrcComponentTable.Lookup(t);
                if (d == null || d.anchorFieldPaths.Length == 0)
                    log.Offender(t.FullName + " has no relocatable anchor in the VRC table (not VRC dynamics — " +
                                 "MA/VRCFury/NDMF/Unity-builtin types are owned elsewhere, never relocated)");
            }
            if (log.offenders.Count > 0)
                return Fail(log, label, "targeted type(s) with no relocatable anchor");

            // ── Instance-level anchor-property pre-check (drift, uniform across all instances) ────────
            // The type-level check above guarantees the table declares an anchor; this confirms the anchor
            // property is actually PRESENT on each real instance. AnchorProp returns null only when NONE of
            // the declared field-paths resolve to a SerializedProperty (SDK field renamed / table drift) —
            // distinct from the legitimate "property present but value null = implicit self-drive" case,
            // which is handled at mint time by ResolveAnchor's host fallback. A drifted instance must FAIL
            // LOUD by name BEFORE any mutation, and uniformly — whether or not it survives the affect
            // filter below. (Without this, a filtered-out drifted instance was silently dropped, while only
            // a minted one surfaced — drift was reported non-uniformly. Note: the affect filter itself can
            // hide drift, so the check runs over ALL targeted instances, not just affected ones.)
            foreach (var comp in Enumerate(ownedRoot, tr.resolved))
            {
                if (comp == null) continue;
                var d = VrcComponentTable.Lookup(comp);   // non-null (pre-checked at type level)
                var so = new SerializedObject(comp);
                if (AnchorProp(so, d.anchorFieldPaths) == null)
                    log.Offender(comp.GetType().Name + " '" + comp.gameObject.name +
                                 "': no anchor property among [" + string.Join(", ", d.anchorFieldPaths) +
                                 "] (SDK field renamed / VRC-table drift?)");
            }
            if (log.offenders.Count > 0)
                return Fail(log, label, "anchor property absent on instance(s) — VRC-table / SDK drift");

            // ── Enumerate + affect-filter + mint-and-rewire ──────────────────────────────────────────
            int group = -1;
            Transform dest = null;
            if (!whatIf)
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("MoveComponents");
                group = Undo.GetCurrentGroup();
                dest = GetOrCreatePath(ownedRoot.transform, destPath);
            }

            int moved = 0, alreadyPlaced = 0;
            var movedNames = new List<string>();
            // original → fresh copy, for the deferred destroy + inbound-ref rewire below
            // (whatIf maps to null — the set of would-move originals, no fresh copies exist).
            var remap = new Dictionary<Component, Component>();

            try
            {
                foreach (var comp in Enumerate(ownedRoot, tr.resolved))
                {
                    if (comp == null) continue;

                    var d = VrcComponentTable.Lookup(comp);   // non-null (pre-checked at type level)
                    var so = new SerializedObject(comp);
                    var ap = AnchorProp(so, d.anchorFieldPaths);
                    Transform explicitAnchor = ap != null ? ap.objectReferenceValue as Transform : null;
                    Transform anchor = ResolveAnchor(explicitAnchor, comp.transform);

                    if (!IsUnderOrEqual(anchor, targetRoot)) continue;

                    if (whatIf)
                    {
                        // Preview only — count what WOULD move; mutate nothing.
                        // alreadyPlaced here is a name-match approximation of the real run's identity
                        // test (parent == dest); preview-only, never gates execution (the dest container
                        // usually isn't minted in a dry run, so identity isn't available).
                        if (comp.transform.parent != null && comp.transform.parent.name == LeafName(destPath))
                            { alreadyPlaced++; continue; }
                        moved++;
                        movedNames.Add(comp.gameObject.name);
                        remap[comp] = null;
                        log.Note("would relocate " + comp.GetType().Name + " '" + comp.gameObject.name +
                                 "' (anchor '" + anchor.name + "') under '" + destPath + "'");
                        continue;
                    }

                    if (comp.transform.parent == dest) { alreadyPlaced++; continue; }

                    var holder = new GameObject(comp.gameObject.name);
                    Undo.RegisterCreatedObjectUndo(holder, "MoveComponents");
                    holder.transform.SetParent(dest, false);

                    var newComp = Undo.AddComponent(holder, comp.GetType());
                    if (newComp == null)
                    {
                        log.Offender(comp.GetType().Name + " '" + comp.gameObject.name + "': AddComponent failed on holder");
                        Undo.DestroyObjectImmediate(holder);
                        continue;
                    }
                    EditorUtility.CopySerialized(comp, newComp);

                    var nso = new SerializedObject(newComp);
                    var nap = AnchorProp(nso, d.anchorFieldPaths);
                    if (nap == null)
                    {
                        log.Offender(comp.GetType().Name + " '" + comp.gameObject.name +
                                     "': no anchor property among [" + string.Join(", ", d.anchorFieldPaths) +
                                     "] (SDK field renamed / VRC-table drift?)");
                        Undo.DestroyObjectImmediate(holder);
                        continue;
                    }
                    nap.objectReferenceValue = anchor;   // pin to the ORIGINAL transform — behavior-neutral
                    nso.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(newComp);

                    remap[comp] = newComp;               // destroy deferred until inbound refs are rewired
                    moved++;
                    movedNames.Add(holder.name);
                }

                // ── Inbound-ref rewire, then the deferred destroy ─────────────────────────────────────
                // Re-creating a component orphans every serialized reference other components hold to the
                // ORIGINAL instance — a physbone's colliders[] entry to a relocated collider would go null
                // when the original is destroyed. Sweep the hierarchy and repoint such refs at the fresh
                // copies BEFORE destroying the originals (destroyed objects don't match by value). This is
                // what keeps the relocation behavior-neutral for referenced types, not just for anchors.
                int inboundRefsRewired = remap.Count > 0 ? RewireInboundRefs(ownedRoot, remap, whatIf, log) : 0;
                if (!whatIf)
                    foreach (var pair in remap)
                        if (pair.Key != null) Undo.DestroyObjectImmediate(pair.Key);

                log.Count("moved", moved);
                log.Count("alreadyPlaced", alreadyPlaced);
                log.Count("inboundRefsRewired", inboundRefsRewired);
                foreach (var n in movedNames) log.Note((whatIf ? "wouldMove: " : "moved: ") + n);
                log.result = log.offenders.Count == 0 ? "PASS" : "FAIL";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error  = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                if (!whatIf && group >= 0) Undo.CollapseUndoOperations(group);
            }

            return TransplantCore.Finish(log, label);
        }

        // ── Inbound-ref rewire ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Repoint every serialized ObjectReference under <paramref name="root"/> that targets a
        /// relocated ORIGINAL at its fresh copy (<paramref name="remap"/> value); whatIf counts and
        /// notes without writing. In a real run the originals themselves are skipped as owners (they
        /// are about to be destroyed; their fresh copies — CopySerialized carries outbound refs — are
        /// swept instead); in whatIf no fresh copies exist, so the originals stand in for them,
        /// keeping the predicted count equal to the executed one when physbone and collider relocate
        /// in the same call. Returns the (would-be) rewired ref count.
        /// </summary>
        static int RewireInboundRefs(GameObject root, Dictionary<Component, Component> remap,
                                     bool whatIf, RunLog log)
        {
            int rewired = 0;
            foreach (var owner in root.GetComponentsInChildren<Component>(true))
            {
                if (owner == null) continue;
                if (!whatIf && remap.ContainsKey(owner)) continue;
                var so = new SerializedObject(owner);
                var it = so.GetIterator();
                bool dirty = false;
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var v = it.objectReferenceValue as Component;
                    if (v == null || !remap.TryGetValue(v, out var fresh)) continue;
                    if (!whatIf) { it.objectReferenceValue = fresh; dirty = true; }
                    rewired++;
                    log.Note((whatIf ? "wouldRewire: " : "rewired: ") + owner.GetType().Name + " '" +
                             owner.gameObject.name + "'." + it.propertyPath + " → " + v.GetType().Name +
                             " '" + v.gameObject.name + "'");
                }
                if (dirty) { so.ApplyModifiedProperties(); EditorUtility.SetDirty(owner); }
            }
            return rewired;
        }

        // ── Anchor probing — driven by the type's table anchorFieldPaths (first found wins) ──────────

        /// <summary>
        /// The serialized property that anchors this component's effect, probed in the order the VRC
        /// table declares for the component's type (e.g. <c>rootTransform</c> for physbones/colliders/
        /// contacts, <c>TargetTransform</c>/casings for VRC constraints). First property found wins;
        /// null if none of the declared candidates resolve (SDK field rename / table drift).
        /// </summary>
        static SerializedProperty AnchorProp(SerializedObject so, string[] anchorFieldPaths)
        {
            foreach (var name in anchorFieldPaths)
            {
                var p = so.FindProperty(name);
                if (p != null) return p;
            }
            return null;
        }

        /// <summary>Every component under <paramref name="root"/> assignable to any selected type.</summary>
        static IEnumerable<Component> Enumerate(GameObject root, List<Type> selected)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var ct = c.GetType();
                foreach (var t in selected)
                    if (t.IsAssignableFrom(ct)) { yield return c; break; }
            }
        }

        // ── Path helpers ─────────────────────────────────────────────────────────────────────────────

        static string LeafName(string path)
        {
            var parts = path.Split('/');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                var seg = parts[i].Trim();
                if (seg.Length > 0) return seg;
            }
            return path;
        }

        /// <summary>
        /// Resolve <paramref name="path"/> ('/'-separated) under <paramref name="root"/>, creating any
        /// missing segment as an empty child GO (identity local transform) and returning the leaf. Reuses
        /// an existing child of the exact segment name so re-runs don't duplicate the container.
        /// </summary>
        static Transform GetOrCreatePath(Transform root, string path)
        {
            Transform current = root;
            foreach (var raw in path.Split('/'))
            {
                var seg = raw.Trim();
                if (seg.Length == 0) continue;
                Transform next = current.Find(seg);
                if (next == null)
                {
                    var go = new GameObject(seg);
                    Undo.RegisterCreatedObjectUndo(go, "MoveComponents");
                    go.transform.SetParent(current, false);
                    next = go.transform;
                }
                current = next;
            }
            return current;
        }

        // ── Finish / Fail (shared TransplantCore RunLog + summary, like CopyComponents) ───────────────

        static string Fail(RunLog log, string label, string msg)
        {
            log.result = "FAIL";
            log.error  = msg;
            return TransplantCore.Finish(log, label);
        }

    }
}
