using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
    /// PASS = all renderers matched, 0 null/default material slots, every SMR's bounds ensured,
    ///        and the Hips anchor target was resolved.
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
        /// <param name="renameMap">Optional {ourName → sourceName} overrides for renderers we renamed during
        /// normalization (e.g. our "Body" ← vendor "Face"). Matched case-insensitively. Null/omitted ⇒ match by
        /// own name. Unmatched renderers (after overrides) still FAIL, so a missed rename surfaces loudly.</param>
        public static string Run(GameObject ownedRoot, GameObject source, IDictionary<string, string> renameMap = null, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";

            if (ownedRoot == null) return ArgFail(label, whatIf, ownedRoot, source, "ownedRoot is null");
            if (source == null)    return ArgFail(label, whatIf, ownedRoot, source, "source is null");

            var data = new RunData
            {
                InstanceName  = ownedRoot.name,
                SourceName    = source.name,
                WhatIf        = whatIf,
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
                var renameLower = NormalizeRenameMap(renameMap);
                AssignMaterials(ownedRoot, sourceMap, duplicateNames, renameLower, data, whatIf);

                // 3. Normalize bounds + probe-anchor on every SMR under ownedRoot (never-shrink bounds; repair-if-invalid anchor)
                SetBoundsAndAnchor(ownedRoot, data, whatIf);

                // 4. PASS/FAIL
                bool pass = data.Unmatched == 0
                         && data.NullSlots == 0
                         && data.DefaultMatSlots == 0
                         && data.BoundsSet == data.SmrTotal
                         && data.AnchorWarning == null;
                data.Result = pass ? "PASS" : "FAIL";
            }
            catch (Exception ex)
            {
                data.Result = "FAIL";
                data.Error  = ex.Message;
            }
            finally
            {
                if (!whatIf) Undo.CollapseUndoOperations(undoGroup);
            }

            string logPath = WriteRunLog(data, label);

            string warnSeg = "";
            if (data.Overrides > 0) warnSeg += " overrides=" + data.Overrides;
            if (data.AmbiguousMatches > 0 || data.SlotCountWarnings > 0)
                warnSeg += " warnings=[ambiguous=" + data.AmbiguousMatches + ", slotCount=" + data.SlotCountWarnings + "]";
            if (data.AnchorsSet > 0 || data.AnchorsPreserved > 0)
                warnSeg += " anchors=[set=" + data.AnchorsSet + ", preserved=" + data.AnchorsPreserved + "]";
            if (data.BoundsKeptLarger > 0)
                warnSeg += " boundsKeptLarger=" + data.BoundsKeptLarger;

            string marker = whatIf ? " (whatIf)" : "";
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[ConformRenderers]" + marker + " {0}: meshes matched={1}/{2}, nullSlots={3}, defaultMat={4}, boundsSet={5}/{6}, anchor={7}{8}{9}{10}{11} => {12} | log={13}",
                label,
                data.Matched, data.OurRendererCount,
                data.NullSlots, data.DefaultMatSlots,
                data.BoundsSet, data.SmrTotal,
                data.AnchorWarning == null ? "Hips" : "none",
                warnSeg,
                data.AnchorNote != null ? " anchorNote=" + data.AnchorNote : "",
                data.AnchorWarning != null ? " anchorWarn=" + data.AnchorWarning : "",
                data.Error != null ? " error=" + data.Error : "",
                data.Result, logPath);

            if (data.Result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
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
                    data.Mismatches.Add(new Mismatch
                    {
                        RendererName = rend.name,
                        Kind         = "unmatched",
                        Detail       = overridden
                            ? "override maps '" + rend.name + "' → '" + lookupKey + "' but the source has no renderer with that name"
                            : "no source renderer with name '" + rend.name + "' (lowercased: '" + ourKey + "')"
                    });
                    continue;
                }

                data.Matched++;
                if (overridden) data.Overrides++;

                // Ambiguity warning: the matched SOURCE name is duplicated in the source, so first-wins
                // may have pulled the wrong renderer's materials. Surfaced; does not affect PASS.
                if (duplicateNames.Contains(lookupKey))
                {
                    data.AmbiguousMatches++;
                    data.Mismatches.Add(new Mismatch
                    {
                        RendererName = rend.name,
                        Kind         = "ambiguous-source-name",
                        Detail       = "source has multiple renderers named '" + lookupKey +
                                       "'; first-wins may be the wrong material set"
                    });
                }

                // Slot-count parity: source material count vs our renderer's submesh count.
                int ourSlots = SubmeshCount(rend);
                if (ourSlots >= 0 && sourceMats.Length != ourSlots)
                {
                    data.SlotCountWarnings++;
                    data.Mismatches.Add(new Mismatch
                    {
                        RendererName = rend.name,
                        Kind         = "slot-count",
                        Detail       = "source has " + sourceMats.Length + " material(s) but our mesh has " +
                                       ourSlots + " submesh slot(s)"
                    });
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
                        data.Mismatches.Add(new Mismatch
                        {
                            RendererName = rend.name,
                            Kind         = "null-slot",
                            Detail       = "null material slot after assignment"
                        });
                    }
                    else if (mat.name == "Default-Material")
                    {
                        data.DefaultMatSlots++;
                        data.Mismatches.Add(new Mismatch
                        {
                            RendererName = rend.name,
                            Kind         = "default-material",
                            Detail       = "slot contains Unity built-in Default-Material (likely unassigned)"
                        });
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
        private static Dictionary<string, string> NormalizeRenameMap(IDictionary<string, string> renameMap)
        {
            if (renameMap == null || renameMap.Count == 0) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in renameMap)
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
                    // genuinely missing "Hips" still sets AnchorWarning → FAIL.
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
                        data.AnchorWarning = reason + " and no transform named 'Hips' under ownedRoot";
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
                    data.Mismatches.Add(new Mismatch
                    {
                        RendererName = smr.name,
                        Kind         = "bounds-kept-larger",
                        Detail       = "existing bounds exceed the standard (extents " +
                                       ensuredBounds.extents.ToString("F2") + "); kept — verify intended"
                    });
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
                            data.Mismatches.Add(new Mismatch
                            {
                                RendererName = smr.name,
                                Kind         = "anchor-repaired-external",
                                Detail       = "probeAnchor '" + smr.probeAnchor.name +
                                               "' pointed outside ownedRoot; re-pointed to Hips — investigate the stale ref"
                            });
                    }
                    else
                    {
                        data.AnchorsPreserved++;
                        if (smr.probeAnchor != hips)   // valid internal anchor that is not the avatar Hips
                            data.Mismatches.Add(new Mismatch
                            {
                                RendererName = smr.name,
                                Kind         = "anchor-preserved-nonhips",
                                Detail       = "preserved internal anchor '" + smr.probeAnchor.name +
                                               "' (not Hips); verify no light/reflection-probe seam vs the body"
                            });
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

        /// <summary>Route an argument-guard failure through the house RunLog grammar (summary + RunLog +
        /// LogError), like the sibling tools — never a bare trailer-less line. Uses this tool's own
        /// RunData/WriteRunLog (its RunLog shape is bespoke), with the <c>error=… =&gt; FAIL | log=…</c>
        /// tail matching the main summary's grammar.</summary>
        private static string ArgFail(string label, bool whatIf, GameObject ownedRoot, GameObject source, string msg)
        {
            var data = new RunData
            {
                InstanceName = ownedRoot != null ? ownedRoot.name : null,
                SourceName   = source != null ? source.name : null,
                WhatIf       = whatIf,
                Result       = "FAIL",
                Error        = msg,
            };
            string logPath = WriteRunLog(data, label);
            string summary = "[ConformRenderers]" + (whatIf ? " (whatIf)" : "") + " " + label +
                             ": error=" + msg + " => FAIL | log=" + logPath;
            Debug.LogError(summary);
            return summary;
        }

        private static string WriteRunLog(RunData data, string label)
        {
            Directory.CreateDirectory(TransplantCore.RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"conform-renderers\",\n");
            sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(data.WhatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"instance\": ").Append(TransplantCore.Q(data.InstanceName)).Append(",\n");
            sb.Append("  \"source\": ").Append(TransplantCore.Q(data.SourceName)).Append(",\n");
            sb.Append("  \"result\": ").Append(TransplantCore.Q(data.Result)).Append(",\n");
            sb.Append("  \"error\": ").Append(TransplantCore.Q(data.Error)).Append(",\n");
            sb.Append("  \"sourceRenderers\": ").Append(data.SourceRendererCount).Append(",\n");
            sb.Append("  \"ourRenderers\": ").Append(data.OurRendererCount).Append(",\n");
            sb.Append("  \"matched\": ").Append(data.Matched).Append(",\n");
            sb.Append("  \"overrides\": ").Append(data.Overrides).Append(",\n");
            sb.Append("  \"unmatched\": ").Append(data.Unmatched).Append(",\n");
            sb.Append("  \"nullSlots\": ").Append(data.NullSlots).Append(",\n");
            sb.Append("  \"defaultMatSlots\": ").Append(data.DefaultMatSlots).Append(",\n");
            sb.Append("  \"smrTotal\": ").Append(data.SmrTotal).Append(",\n");
            sb.Append("  \"boundsSet\": ").Append(data.BoundsSet).Append(",\n");
            sb.Append("  \"duplicateSourceNames\": ").Append(data.DuplicateSourceNames).Append(",\n");
            sb.Append("  \"ambiguousMatches\": ").Append(data.AmbiguousMatches).Append(",\n");
            sb.Append("  \"slotCountWarnings\": ").Append(data.SlotCountWarnings).Append(",\n");
            sb.Append("  \"anchorWarning\": ").Append(TransplantCore.Q(data.AnchorWarning)).Append(",\n");
            sb.Append("  \"anchorNote\": ").Append(TransplantCore.Q(data.AnchorNote)).Append(",\n");
            sb.Append("  \"anchorsSet\": ").Append(data.AnchorsSet).Append(",\n");
            sb.Append("  \"anchorsPreserved\": ").Append(data.AnchorsPreserved).Append(",\n");
            sb.Append("  \"boundsKeptLarger\": ").Append(data.BoundsKeptLarger).Append(",\n");
            sb.Append("  \"mismatches\": [");

            for (int i = 0; i < data.Mismatches.Count; i++)
            {
                var m = data.Mismatches[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"renderer\": ").Append(TransplantCore.Q(m.RendererName))
                  .Append(", \"kind\": ").Append(TransplantCore.Q(m.Kind))
                  .Append(", \"detail\": ").Append(TransplantCore.Q(m.Detail)).Append(" }");
            }

            sb.Append(data.Mismatches.Count > 0 ? "\n  ]\n}" : "]\n}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path  = TransplantCore.RunLogDir + "/conform-renderers_" + label + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        // ── Data types ────────────────────────────────────────────────────────────────────────

        private class RunData
        {
            public string InstanceName;
            public string SourceName;
            public bool   WhatIf;
            public string Result;
            public string Error;
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
            public readonly List<Mismatch> Mismatches = new List<Mismatch>();
        }

        private struct Mismatch
        {
            public string RendererName;
            public string Kind;
            public string Detail;
        }
    }
}
