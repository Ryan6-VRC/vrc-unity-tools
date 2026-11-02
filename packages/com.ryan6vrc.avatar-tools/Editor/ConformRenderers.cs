using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>Assigns vendor materials onto an owned avatar's renderers by renderer name, then ensures
    /// every SkinnedMeshRenderer's bounds and probe anchor meet the renderer-normalization standard.
    /// Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class ConformRenderers
    {
        /// <summary>Returns a one-line PASS/FAIL summary ending with the RunLog path
        /// (<c>… => RESULT | log=&lt;path&gt;</c>), and also logs it.</summary>
        /// <param name="ownedToSource">Optional <c>{ourName ⇒ sourceName}</c> overrides for renderers we renamed.
        /// Keyed on OUR name, opposite to the transplant tools' <c>vendorToOwned</c>: this one may be many-to-one
        /// and matches case-insensitively (<see cref="IndexedPath.Substitute"/>).</param>
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

            int undoGroup = -1;
            if (!whatIf)
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("ConformRenderers");
                undoGroup = Undo.GetCurrentGroup();
            }

            try
            {
                var sourceMap = BuildSourceMap(source, out var duplicateNames);
                data.SourceRendererCount = sourceMap.Count;
                data.DuplicateSourceNames = duplicateNames.Count;

                var renameLower = NormalizeRenameMap(ownedToSource);
                AssignMaterials(ownedRoot, sourceMap, duplicateNames, renameLower, data, whatIf);

                SetBoundsAndAnchor(ownedRoot, data, whatIf);

                if (BuiltinDefaultMaterialUnresolved)
                    data.Warning("default-material-instrument: Unity's built-in Default-Material did not resolve, "
                               + "so that gate rested on the GUID+localId fallback alone — if a slot held the "
                               + "built-in under a different localId it would not have been counted");

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

        private static Dictionary<string, Material[]> BuildSourceMap(GameObject source, out HashSet<string> duplicateNames)
        {
            var map = new Dictionary<string, Material[]>(StringComparer.Ordinal);
            duplicateNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var smr in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var key = smr.name.ToLowerInvariant();
                if (!map.ContainsKey(key)) map[key] = (Material[])smr.sharedMaterials.Clone();
                else                       duplicateNames.Add(key);
            }

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
            var renderers = ownedRoot.GetComponentsInChildren<Renderer>(true);
            data.OurRendererCount = renderers.Length;

            foreach (var rend in renderers)
            {
                var ourKey = rend.name.ToLowerInvariant();
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

                if (duplicateNames.Contains(lookupKey))
                {
                    data.AmbiguousMatches++;
                    data.Warning("ambiguous-source-name: renderer '" + rend.name + "' — source has multiple renderers named '" +
                                 lookupKey + "'; first-wins may be the wrong material set");
                }

                int ourSlots = SubmeshCount(rend);
                if (ourSlots >= 0 && sourceMats.Length != ourSlots)
                {
                    data.SlotCountWarnings++;
                    data.Warning("slot-count: renderer '" + rend.name + "' — source has " + sourceMats.Length +
                                 " material(s) but our mesh has " + ourSlots + " submesh slot(s)");
                }

                if (!whatIf)
                {
                    Undo.RecordObject(rend, "ConformRenderers: assign materials on " + rend.name);
                    rend.sharedMaterials = sourceMats;
                    EditorUtility.SetDirty(rend);
                }

                var slotsToScan = whatIf ? sourceMats : rend.sharedMaterials;
                for (int slot = 0; slot < slotsToScan.Length; slot++)
                {
                    var mat = slotsToScan[slot];
                    if (mat == null)
                    {
                        data.NullSlots++;
                        data.Offender("null-slot: renderer '" + rend.name + "' [" + slot + "] — " + (whatIf
                            ? "the source material set has a null slot; assigning it would leave this renderer with one"
                            : "null material slot after assignment"));
                    }
                    else if (IsBuiltinDefaultMaterial(mat))
                    {
                        data.DefaultMatSlots++;
                        data.Offender("default-material: renderer '" + rend.name + "' [" + slot +
                                      "] — slot holds Unity's built-in Default-Material (" +
                                      AssetDatabase.GetAssetPath(mat) + "); remap this material and re-run");
                    }
                }
            }
        }

        /// <summary>Unity's well-known built-in-extra path and GUID; the localId is Default-Material's slot
        /// within it.</summary>
        private const string BuiltinExtraPath = "Resources/unity_builtin_extra";
        private const string BuiltinExtraGuid = "0000000000000000f000000000000000";
        private const long   DefaultMaterialLocalId = 10303;

        /// <summary>Resolved lazily, NOT in a static field initializer: the built-in resolves through the
        /// AssetDatabase, and resolving at type-load can run before it is ready.</summary>
        private static Material _builtinDefaultMaterial;
        private static bool     _builtinDefaultResolved;

        /// <summary>
        /// True when <paramref name="mat"/> IS Unity's built-in Default-Material, by identity: any project
        /// material can carry the name.
        ///
        /// <para>Reference compare (built-in extras are session singletons, so it is exact), with a GUID+localId
        /// fallback because the built-in handle can fail to resolve and a null handle would match nothing
        /// (<see cref="BuiltinDefaultMaterialUnresolved"/>).</para>
        ///
        /// <para>Built-in RP only, which is all VRChat ships: under URP/HDRP the built-in still exists but is
        /// not what a new renderer receives, so this predicate would under-fire. Do not port it blind.</para>
        /// </summary>
        internal static bool IsBuiltinDefaultMaterial(Material mat)
        {
            if (mat == null) return false;

            if (!_builtinDefaultResolved)
            {
                _builtinDefaultMaterial = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
                _builtinDefaultResolved = true;
            }

            if (_builtinDefaultMaterial != null) return mat == _builtinDefaultMaterial;

            string guid; long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mat, out guid, out localId)) return false;
            return guid == BuiltinExtraGuid && localId == DefaultMaterialLocalId
                && AssetDatabase.GetAssetPath(mat) == BuiltinExtraPath;
        }

        /// <summary>
        /// True when the built-in handle did not resolve, so this run's default-material coverage rests on the
        /// GUID+localId fallback alone. Read AFTER the scan, when the lazy resolve has happened.
        /// </summary>
        internal static bool BuiltinDefaultMaterialUnresolved
        {
            get { return _builtinDefaultResolved && _builtinDefaultMaterial == null; }
        }

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

        /// <summary>Keys differing only in case fold together, last write wins, silently: here they are two
        /// spellings of one renderer name, unlike the collisions <see cref="IndexedPath.ValidateRenameMap"/>
        /// rejects.</summary>
        private static Dictionary<string, string> NormalizeRenameMap(IDictionary<string, string> ownedToSource)
        {
            if (ownedToSource == null || ownedToSource.Count == 0) return null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in ownedToSource)
                if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                    d[kv.Key.ToLowerInvariant()] = kv.Value.ToLowerInvariant();
            return d.Count > 0 ? d : null;
        }

        private static void SetBoundsAndAnchor(GameObject ownedRoot, RunData data, bool whatIf)
        {
            var ourAnimator = ownedRoot.GetComponent<Animator>();
            // GetBoneTransform THROWS when the avatar is null ("Avatar is null") or not humanoid
            // ("Avatar is not of type humanoid"); it never returns null for a Generic rig.
            bool isHumanoid = ourAnimator != null && ourAnimator.avatar != null && ourAnimator.avatar.isHuman;
            Transform hips  = isHumanoid ? ourAnimator.GetBoneTransform(HumanBodyBones.Hips) : null;

            if (hips == null)
            {
                if (isHumanoid)
                {
                    // A humanoid rig whose Hips won't resolve is misconfigured: FAIL, never masked by a name match.
                    data.AnchorWarning = "Animator.GetBoneTransform(Hips) returned null on a humanoid rig — humanoid avatar misconfigured?";
                }
                else
                {
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
                        // A mergeable legitimately has no humanoid rig and no 'Hips', and own-mergeable prescribes
                        // this tool for it, so the missing anchor is a note, not a FAIL.
                        data.AnchorNote = reason + " and no 'Hips' under ownedRoot — mergeable: probeAnchor left as-authored, anchor repair skipped";
                    }
                }
            }

            var smrs = ownedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            data.SmrTotal = smrs.Length;

            var workspaceBounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));

            foreach (var smr in smrs)
            {
                // Disposition is decided from the pre-write state so preview == execute.
                var ensuredBounds = smr.localBounds;
                ensuredBounds.Encapsulate(workspaceBounds);
                bool boundsKeptLarger = ensuredBounds != workspaceBounds;
                if (boundsKeptLarger)
                {
                    data.BoundsKeptLarger++;
                    data.Warning("bounds-kept-larger: renderer '" + smr.name + "' — existing bounds exceed the standard (extents " +
                                 ensuredBounds.extents.ToString("F2") + "); kept — verify intended");
                }

                bool anchorInvalid = smr.probeAnchor == null
                                  || !smr.probeAnchor.IsChildOf(ownedRoot.transform);
                if (hips != null)
                {
                    if (anchorInvalid)
                    {
                        data.AnchorsSet++;
                        if (smr.probeAnchor != null)
                            data.Warning("anchor-repaired-external: renderer '" + smr.name + "' — probeAnchor '" +
                                         smr.probeAnchor.name + "' pointed outside ownedRoot; re-pointed to Hips — investigate the stale ref");
                    }
                    else
                    {
                        data.AnchorsPreserved++;
                        if (smr.probeAnchor != hips)
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
                            smr.probeAnchor = hips;
                        if (smr.rootBone == null)
                            smr.rootBone = hips;
                    }

                    EditorUtility.SetDirty(smr);
                }

                data.BoundsSet++;
            }
        }

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

        /// <summary>Skips <see cref="Finish"/>'s count flush: nothing was measured, and zero counts would read
        /// as measurements.</summary>
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

        /// <summary>Counters are fields, flushed into <c>counts</c> by <see cref="Finish"/>, because they
        /// increment mid-run and the envelope's <c>counts</c> list only appends.</summary>
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
