using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Behavior-neutral relocation primitive over a caller-supplied component-type list: each component is
    /// re-created on a fresh holder under <c>destPath</c> with its anchor pinned to its original transform.
    /// Runs on the scene instance before prefab conversion, because removing the original prefab-sourced
    /// component is version-finicky post-prefab. One collapsed Undo per call.
    /// Contract: docs/unity-tools.md.
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

            // ── Type-level anchor pre-check, before enumeration, so it refuses even with zero instances ──
            foreach (var t in tr.resolved)
            {
                var d = VrcComponentTable.Lookup(t);
                if (d == null || d.anchorFieldPaths.Length == 0)
                    log.Offender(t.FullName + " has no relocatable anchor in the VRC table (not VRC dynamics — " +
                                 "MA/VRCFury/NDMF/Unity-builtin types are owned elsewhere, never relocated)");
            }
            if (log.offenders.Count > 0)
                return Fail(log, label, "targeted type(s) with no relocatable anchor");

            // ── Instance-level anchor-property pre-check ──────────────────────────────────────────────
            // A null AnchorProp means SDK field rename or table drift, distinct from a present property with
            // a null value, which is implicit self-drive (ResolveAnchor's host fallback). It runs over ALL
            // targeted instances because the affect filter below would hide drift.
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

                    remap[comp] = newComp;
                    moved++;
                    movedNames.Add(holder.name);
                }

                // ── Inbound-ref rewire, then the deferred destroy ─────────────────────────────────────
                // Rewire before destroying: destroyed objects don't match by value, and a physbone's
                // colliders[] entry to a relocated collider would go null.
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
        /// relocated original at its fresh copy. A real run skips the originals as owners and sweeps their
        /// fresh copies, which CopySerialized gave the same outbound refs; whatIf has no fresh copies, so the
        /// originals stand in, keeping the predicted count equal to the executed one when physbone and
        /// collider relocate in the same call.
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

        // ── Anchor probing ───────────────────────────────────────────────────────────────────────────

        /// <summary>The first of the VRC table's declared anchor field paths that resolves; null if none
        /// does (SDK field rename or table drift).</summary>
        static SerializedProperty AnchorProp(SerializedObject so, string[] anchorFieldPaths)
        {
            foreach (var name in anchorFieldPaths)
            {
                var p = so.FindProperty(name);
                if (p != null) return p;
            }
            return null;
        }

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

        static string Fail(RunLog log, string label, string msg)
        {
            log.result = "FAIL";
            log.error  = msg;
            return TransplantCore.Finish(log, label);
        }

    }
}
