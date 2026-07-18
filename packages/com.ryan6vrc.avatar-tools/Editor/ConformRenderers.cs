using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Assigns vendor materials onto our owned avatar's renderers by matching renderer
    /// name (case-insensitive) — with an optional {ourName → sourceName} override map for meshes
    /// we renamed during normalization — then normalizes every SkinnedMeshRenderer's local bounds
    /// and probe-anchor toward the workspace standard: bounds are ensured >= the standard box (a
    /// deliberately larger box is kept, never shrunk), and the probe-anchor is set to Hips only when
    /// invalid (null or pointing outside ownedRoot) — a valid internal anchor is preserved. The
    /// per-renderer disposition (anchors set/preserved, bounds kept larger) is reported, not silent.
    ///
    /// Materials are assigned by reference — no copies are created. The vendor asset GUIDs are
    /// preserved exactly as-is.
    ///
    /// Call <see cref="Run"/> from MCP execute_code or a menu item.
    /// PASS = all renderers matched, 0 null/default material slots, every SMR's bounds ensured, and the
    ///        Hips anchor was resolved OR the target is a mergeable (no humanoid rig / no 'Hips' — the
    ///        anchor is left as-authored and PASS carries a note, never a FAIL; G25).
    /// </summary>
    [AgentTool]
    public static class ConformRenderers
    {
        // ── Public API ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Copy materials from <paramref name="source"/> onto <paramref name="ownedRoot"/>
        /// by renderer name, then normalize bounds and probe-anchor on every SMR (ensure, not clobber — see the type summary).
        /// Returns a one-line PASS/FAIL summary ending with the RunLog path (<c>… => RESULT | log=&lt;path&gt;</c>);
        /// also Debug.Log/LogError it.
        /// </summary>
        /// <param name="ownedRoot">Our owned avatar root in the scene (has an Animator with a humanoid avatar).</param>
        /// <param name="source">The vendor's dressed source to copy materials FROM (scene GO or prefab).</param>
        /// <param name="ownedToSource">Optional <c>{ourName ⇒ sourceName}</c> overrides for renderers we renamed
        /// during normalization (e.g. our "Body" ← vendor "Face"). Matched case-insensitively. Null/omitted ⇒
        /// match by own name. Unmatched renderers (after overrides) still FAIL, so a missed rename surfaces loudly.
        ///
        /// <para><b>Direction — it runs opposite to the transplant kit's map, deliberately.</b> The kit-wide
        /// invariant (canonical at <see cref="IndexedPath.Substitute"/>) is that a rename map's KEY names the
        /// hierarchy the tool WALKS and its VALUE the hierarchy it RESOLVES INTO. This tool walks OUR renderers
        /// and looks each one up in the source, so the key is ours — whereas <c>CopyComponents</c> /
        /// <c>GraftHierarchy</c> walk the vendor hierarchy and resolve into ours, giving them
        /// <c>vendorToOwned</c>. Same rule, opposite traversal. Consequently this map may be MANY-TO-ONE (two
        /// owned meshes can both take one source renderer's materials) while theirs must be injective;
        /// inverting this one would make that case inexpressible, so do not "reconcile" the two.</para></param>
        public static string Run(GameObject ownedRoot, GameObject source, IDictionary<string, string> ownedToSource = null, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";

            if (ownedRoot == null) return ArgFail(label, whatIf, ownedRoot, source, "ownedRoot is null");
            if (source == null)    return ArgFail(label, whatIf, ownedRoot, source, "source is null");

            var data = new RunData
            {
                instance = ownedRoot.name,
                source   = source.name,
                whatIf   = whatIf,
            };

            // Execute-path Undo group: collapse the analysis+mutation into one undo step. Preview
            // mutates nothing, so it opens no group.
            int undoGroup = -1;
            if (!whatIf)
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("ConformRenderers");
                undoGroup = Undo.GetCurrentGroup();
            }

            try
            {
                // 1. Build name → sharedMaterials map from source (+ set of ambiguous duplicate names)
                var sourceMap = BuildSourceMap(source, out var duplicateNames);
                data.SourceRendererCount = sourceMap.Count;
                data.DuplicateSourceNames = duplicateNames.Count;

                // 2. Assign materials to our instance's renderers by name (with optional
                //    {ourName → sourceName} overrides for meshes we renamed during normalization)
                var renameLower = NormalizeRenameMap(ownedToSource);
                AssignMaterials(ownedRoot, sourceMap, duplicateNames, renameLower, data, whatIf);

                // 3. Normalize bounds + probe-anchor on every SMR under ownedRoot (never-shrink bounds; repair-if-invalid anchor)
                SetBoundsAndAnchor(ownedRoot, data, whatIf);

                // 4. PASS/FAIL
                bool pass = data.Unmatched == 0
                         && data.NullSlots == 0
                         && data.DefaultMatSlots == 0
                         && data.BoundsSet == data.SmrTotal
                         && data.AnchorWarning == null;
                data.result = pass ? "PASS" : "FAIL";
            }
            catch (Exception ex)
            {
                data.result = "FAIL";
                data.error  = ex.Message;
            }
            finally
            {
                if (!whatIf) Undo.CollapseUndoOperations(undoGroup);
            }

            return Finish(data, label);
        }

        // ── Material assignment ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a map from renderer.name.ToLowerInvariant() → a copy of sharedMaterials[].
        /// Covers both SkinnedMeshRenderer and MeshRenderer on the source hierarchy.
        /// When two source renderers share the same lowercased name, the first one wins (matches
        /// vendor convention where the superset FBX is the primary source). Such names are also
        /// collected into <paramref name="duplicateNames"/>: a first-wins match on a duplicated name
        /// is AMBIGUOUS (could pull the wrong outfit/accessory's materials) and is flagged downstream.
        /// </summary>
        private static Dictionary<string, Material[]> BuildSourceMap(GameObject source, out HashSet<string> duplicateNames)
        {
            var map = new Dictionary<string, Material[]>(StringComparer.Ordinal);
            duplicateNames = new HashSet<string>(StringComparer.Ordinal);

            // SkinnedMeshRenderers first
            foreach (var smr in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var key = smr.name.ToLowerInvariant();
                if (!map.ContainsKey(key)) map[key] = (Material[])smr.sharedMaterials.Clone();
                else                       duplicateNames.Add(key);
            }

            // MeshRenderers second (accessories / props)
            foreach (var mr in source.GetComponentsInChildren<MeshRenderer>(true))
            {
                var key = mr.name.ToLowerInvariant();
                if (!map.ContainsKey(key)) map[key] = (Material[])mr.sharedMaterials.Clone();
                else                       duplicateNames.Add(key);
            }

            return map;
        }

        private static void AssignMaterials(GameObject ownedRoot,
                                            Dictionary<string, Material[]> sourceMap,
                                            HashSet<string> duplicateNames,
                                            Dictionary<string, string> renameLower,
                                            RunData data,
                                            bool whatIf)
        {
            // Walk all Renderer types (SMR + MR) on our instance
            var renderers = ownedRoot.GetComponentsInChildren<Renderer>(true);
            data.OurRendererCount = renderers.Length;

            foreach (var rend in renderers)
            {
                var ourKey = rend.name.ToLowerInvariant();
                // Resolve the SOURCE name to look up: an explicit {ourName → sourceName} override (for a
                // mesh we renamed during normalization) wins; otherwise match by our own name.
                string lookupKey = ourKey;
                bool overridden = false;
                if (renameLower != null && renameLower.TryGetValue(ourKey, out var mappedSrc))
                {
                    lookupKey  = mappedSrc;
                    overridden = true;
                }

                if (!sourceMap.TryGetValue(lookupKey, out Material[] sourceMats))
                {
                    data.Unmatched++;
                    data.Offender("unmatched: renderer '" + rend.name + "' — " + (overridden
                        ? "override maps '" + rend.name + "' → '" + lookupKey + "' but the source has no renderer with that name"
                        : "no source renderer with name '" + rend.name + "' (lowercased: '" + ourKey + "')"));
                    continue;
                }

                data.Matched++;
                if (overridden) data.Overrides++;

                // Ambiguity warning: the matched SOURCE name is duplicated in the source, so first-wins
                // may have pulled the wrong renderer's materials. Surfaced; does not affect PASS.
                if (duplicateNames.Contains(lookupKey))
                {
                    data.AmbiguousMatches++;
                    data.Warning("ambiguous-source-name: renderer '" + rend.name + "' — source has multiple renderers named '" +
                                 lookupKey + "'; first-wins may be the wrong material set");
                }

                // Slot-count parity: source material count vs our renderer's submesh count.
                int ourSlots = SubmeshCount(rend);
                if (ourSlots >= 0 && sourceMats.Length != ourSlots)
                {
                    data.SlotCountWarnings++;
                    data.Warning("slot-count: renderer '" + rend.name + "' — source has " + sourceMats.Length +
                                 " material(s) but our mesh has " + ourSlots + " submesh slot(s)");
                }

                // Record the change for Undo and mark dirty; do not save scene
                if (!whatIf)
                {
                    Undo.RecordObject(rend, "ConformRenderers: assign materials on " + rend.name);
                    rend.sharedMaterials = sourceMats; // sourceMats is already a clone of the array; Material refs are vendor assets
                    EditorUtility.SetDirty(rend);
                }

                // Check for null or Default-Material slots. On execute, rend.sharedMaterials IS the
                // just-assigned sourceMats — so scanning the source array in whatIf yields the identical
                // verdict without mutating anything.
                var slotsToScan = whatIf ? sourceMats : rend.sharedMaterials;
                foreach (var mat in slotsToScan)
                {
                    if (mat == null)
                    {
                        data.NullSlots++;
                        data.Offender("null-slot: renderer '" + rend.name + "' — null material slot after assignment");
                    }
                    else if (mat.name == "Default-Material")
                    {
                        data.DefaultMatSlots++;
                        data.Offender("default-material: renderer '" + rend.name +
                                      "' — slot contains Unity built-in Default-Material (likely unassigned)");
                    }
                }
            }
        }

        /// <summary>
        /// Submesh count of the renderer's mesh (the natural material-slot count): sharedMesh for an
        /// SMR, the MeshFilter's sharedMesh for a MeshRenderer. Returns -1 when no mesh is available
        /// (parity check skipped).
        /// </summary>
        private static int SubmeshCount(Renderer rend)
        {
            Mesh mesh = null;
            if (rend is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else
            {
                var mf = rend.GetComponent<MeshFilter>();
                if (mf != null) mesh = mf.sharedMesh;
            }
            return mesh != null ? mesh.subMeshCount : -1;
        }

        /// <summary>
        /// Lowercases an optional {ourName → sourceName} override map for case-insensitive matching.
        /// Returns null when no usable entries were supplied.
        /// </summary>
        private static Dictionary<string, string> NormalizeRenameMap(IDictionary<string, string> ownedToSource)
        {
            if (ownedToSource == null || ownedToSource.Count == 0) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in ownedToSource)
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    d[kv.Key.ToLowerInvariant()] = kv.Value.ToLowerInvariant();
            return d.Count > 0 ? d : null;
        }

        // ── Bounds + anchor ───────────────────────────────────────────────────────────────────

        private static void SetBoundsAndAnchor(GameObject ownedRoot, RunData data, bool whatIf)
        {
            var ourAnimator = ownedRoot.GetComponent<Animator>();
            // GetBoneTransform THROWS when the avatar is null ("Avatar is null") or not humanoid
            // ("Avatar is not of type humanoid") — it never returns null for a Generic rig; only
            // consult it when the rig is actually humanoid, so both non-humanoid cases fall through
            // to the name-based scan below instead of throwing.
            bool isHumanoid = ourAnimator != null && ourAnimator.avatar != null && ourAnimator.avatar.isHuman;
            Transform hips  = isHumanoid ? ourAnimator.GetBoneTransform(HumanBodyBones.Hips) : null;

            if (hips == null)
            {
                if (isHumanoid)
                {
                    // Humanoid rig whose Hips won't resolve is genuinely misconfigured — fail loud
                    // (unchanged from the original behavior), never masked by a name match.
                    data.AnchorWarning = "Animator.GetBoneTransform(Hips) returned null on a humanoid rig — humanoid avatar misconfigured?";
                }
                else
                {
                    // Only a non-humanoid (Generic) or Animator-less mergeable reaches the name-based
                    // fallback: scan under ownedRoot for a transform named "Hips" (case-insensitive,
                    // mirroring NormalizeRenameMap's lowercasing) so the probe anchor still resolves
                    // and the PASS gate can clear. First match in hierarchy order wins (deterministic,
                    // matching BuildSourceMap's first-wins convention); resolving it records a
                    // non-blocking AnchorNote (with duplicate count) rather than clearing silently. A
                    // genuinely missing "Hips" also records a non-blocking AnchorNote (mergeable → PASS, see
                    // the else below — G25); only a misconfigured humanoid rig sets AnchorWarning → FAIL (above).
                    var byName = FindDescendantByName(ownedRoot.transform, "hips", out int hipsCandidates);
                    string reason = ourAnimator == null ? "no Animator" : "non-humanoid rig";
                    if (byName != null)
                    {
                        hips = byName;
                        data.AnchorNote = reason + " — anchor resolved via name-based 'Hips'"
                            + (hipsCandidates > 1 ? " (" + hipsCandidates + " named 'Hips'; first-wins)" : "");
                    }
                    else
                    {
                        // A mergeable (hair/accessory) legitimately has neither a humanoid rig nor a 'Hips'
                        // transform — the exact input own-mergeable prescribes ConformRenderers for. Don't FAIL
                        // on the missing anchor (that trained workers to accept a red verdict — G25): leave every
                        // probeAnchor as-authored (the hips==null guards below skip anchor repair) and PASS with
                        // a note. Materials + bounds still apply. An avatar BASE never reaches here — it carries a
                        // 'Hips' transform, so the name scan above resolves it.
                        data.AnchorNote = reason + " and no 'Hips' under ownedRoot — mergeable: probeAnchor left as-authored, anchor repair skipped";
                    }
                }
            }

            var smrs = ownedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            data.SmrTotal = smrs.Length;

            var workspaceBounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f)); // extents = (1,1,1)

            foreach (var smr in smrs)
            {
                // Disposition is decided from the CURRENT (pre-write) state so preview == execute:
                // count regardless of whatIf, apply the write only on execute.

                // Bounds — ensure >= the anti-cull floor; never shrink a deliberately larger box.
                var ensuredBounds = smr.localBounds;
                ensuredBounds.Encapsulate(workspaceBounds);              // union with the standard 2x2x2
                bool boundsKeptLarger = ensuredBounds != workspaceBounds; // existing exceeded standard on some axis
                if (boundsKeptLarger)
                {
                    data.BoundsKeptLarger++;
                    data.Warning("bounds-kept-larger: renderer '" + smr.name + "' — existing bounds exceed the standard (extents " +
                                 ensuredBounds.extents.ToString("F2") + "); kept — verify intended");
                }

                // Anchor — repair only an invalid ref (null, or points outside ownedRoot); preserve a
                // valid internal anchor. Only meaningful once Hips resolved.
                bool anchorInvalid = smr.probeAnchor == null
                                  || !smr.probeAnchor.IsChildOf(ownedRoot.transform);
                if (hips != null)
                {
                    if (anchorInvalid)
                    {
                        data.AnchorsSet++;
                        if (smr.probeAnchor != null)   // was non-null but external → a stale ref got repaired
                            data.Warning("anchor-repaired-external: renderer '" + smr.name + "' — probeAnchor '" +
                                         smr.probeAnchor.name + "' pointed outside ownedRoot; re-pointed to Hips — investigate the stale ref");
                    }
                    else
                    {
                        data.AnchorsPreserved++;
                        if (smr.probeAnchor != hips)   // valid internal anchor that is not the avatar Hips
                            data.Warning("anchor-preserved-nonhips: renderer '" + smr.name + "' — preserved internal anchor '" +
                                         smr.probeAnchor.name + "' (not Hips); verify no light/reflection-probe seam vs the body");
                    }
                }

                if (!whatIf)
                {
                    Undo.RecordObject(smr, "ConformRenderers: set bounds/anchor on " + smr.name);

                    smr.localBounds = ensuredBounds;

                    if (hips != null)
                    {
                        if (anchorInvalid)
                            smr.probeAnchor = hips;      // repair null/external anchor to Hips
                        if (smr.rootBone == null)
                            smr.rootBone = hips;         // fill rootBone only if currently unset
                    }

                    EditorUtility.SetDirty(smr);
                }

                data.BoundsSet++;
            }
        }

        /// <summary>
        /// First transform at or under <paramref name="root"/> whose name equals <paramref name="lowerName"/>
        /// case-insensitively, in hierarchy (depth-first) order; null if none. <paramref name="count"/>
        /// receives the total number of matches so the caller can flag an ambiguous first-wins pick.
        /// </summary>
        private static Transform FindDescendantByName(Transform root, string lowerName, out int count)
        {
            count = 0;
            Transform first = null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.ToLowerInvariant() == lowerName)
                {
                    if (first == null) first = t;
                    count++;
                }
            }
            return first;
        }

        // ── RunLog output ─────────────────────────────────────────────────────────────────────

        /// <summary>Route an argument-guard failure through the shared envelope tail — never a bare
        /// trailer-less line. Skips the count flush (nothing was measured; zero-counts would read as
        /// measurements) and names the offender explicitly, like OwnMaterial's ArgFail.</summary>
        private static string ArgFail(string label, bool whatIf, GameObject ownedRoot, GameObject source, string msg)
        {
            var data = new RunData
            {
                instance = ownedRoot != null ? ownedRoot.name : null,
                source   = source != null ? source.name : null,
                whatIf   = whatIf,
                result   = "FAIL",
                error    = msg,
            };
            data.Offender(msg);
            return TransplantCore.Finish(data, label);
        }

        /// <summary>Flush the run's int counters into the envelope's ordered <c>counts</c> (core
        /// disposition always; anomaly counters only when nonzero — the mismatch-class ones are also
        /// named by offender/warning rows), fold the anchor verdict into offender/note grammar, then
        /// the shared envelope tail (<see cref="TransplantCore.Finish"/>).</summary>
        private static string Finish(RunData data, string label)
        {
            data.Count("sourceRenderers", data.SourceRendererCount);
            data.Count("ourRenderers", data.OurRendererCount);
            data.Count("matched", data.Matched);
            data.Count("unmatched", data.Unmatched);
            data.Count("nullSlots", data.NullSlots);
            data.Count("defaultMatSlots", data.DefaultMatSlots);
            data.Count("smrTotal", data.SmrTotal);
            data.Count("boundsSet", data.BoundsSet);
            data.Count("anchorsSet", data.AnchorsSet);
            data.Count("anchorsPreserved", data.AnchorsPreserved);
            if (data.Overrides > 0)            data.Count("overrides", data.Overrides);
            if (data.DuplicateSourceNames > 0) data.Count("duplicateSourceNames", data.DuplicateSourceNames);
            if (data.AmbiguousMatches > 0)     data.Count("ambiguousMatches", data.AmbiguousMatches);
            if (data.SlotCountWarnings > 0)    data.Count("slotCountWarnings", data.SlotCountWarnings);
            if (data.BoundsKeptLarger > 0)     data.Count("boundsKeptLarger", data.BoundsKeptLarger);

            if (data.AnchorWarning != null) data.Offender("anchor: " + data.AnchorWarning);
            if (data.AnchorNote != null)    data.Note("anchor: " + data.AnchorNote);

            return TransplantCore.Finish(data, label);
        }

        // ── Data types ────────────────────────────────────────────────────────────────────────

        /// <summary>The package RunLog envelope plus this tool's run counters (flushed into
        /// <c>counts</c> by <see cref="Finish"/> — they are incremented mid-run, which the envelope's
        /// append-only counts list can't do). <c>AnchorWarning</c> stays a field because the PASS gate
        /// consults it; <see cref="Finish"/> folds it into offender grammar.</summary>
        private sealed class RunData : RunLog
        {
            public RunData() : base("conform-renderers") { }

            public string AnchorWarning;
            public string AnchorNote;
            public int SourceRendererCount;
            public int OurRendererCount;
            public int Matched;
            public int Overrides;
            public int Unmatched;
            public int NullSlots;
            public int DefaultMatSlots;
            public int SmrTotal;
            public int BoundsSet;
            public int DuplicateSourceNames;
            public int AmbiguousMatches;
            public int SlotCountWarnings;
            public int AnchorsSet;
            public int AnchorsPreserved;
            public int BoundsKeptLarger;
        }
    }
}
