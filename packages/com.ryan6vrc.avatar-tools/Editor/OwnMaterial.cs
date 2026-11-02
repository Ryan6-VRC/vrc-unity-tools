using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Materializes an owned copy of a vendor material, or branches or augments an owned one, and forks the
    /// named texture slots into its own namespace. Contract: docs/unity-tools.md.
    ///
    /// In comments here, S is the source material, O the owned result, and H(O) its texture home
    /// <c>&lt;O.folder&gt;/&lt;Sanitize(O.name)&gt;/</c>. The unlock's success path needs Poiyomi installed;
    /// without it, tests reach only lock detection and the refusal when Thry is absent.
    /// </summary>
    [AgentTool]
    public static class OwnMaterial
    {
        // Reflection names from Packages/com.poiyomi.toon/_PoiyomiShaders/Scripts/ThryEditor/Editor/ShaderOptimizer.cs;
        // the tags are its TAG_* consts.
        const string ThryShaderOptimizerTypeName = "Thry.ThryEditor.ShaderOptimizer";
        const string ThryTagOriginalShader = "OriginalShader";
        const string ThryTagOriginalShaderGuid = "OriginalShaderGUID";

        /// <summary><paramref name="forkTextureSlots"/> are shader texture-property names, e.g. <c>_MainTex</c>.
        /// <paramref name="newName"/> defaults to the source's own name and requires <paramref name="outDir"/>.</summary>
        public static string Run(string materialPath, string outDir = null, string[] forkTextureSlots = null,
                                 string newName = null, bool force = false, bool whatIf = false)
        {
            if (string.IsNullOrEmpty(materialPath))
                return ArgFail("null-material", "materialPath is null or empty");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
                return ArgFail(TransplantCore.Sanitize(TransplantCore.Leaf(materialPath)),
                    "materialPath '" + materialPath + "' did not load as a Material (missing, wrong asset type, or invalid path)");

            string label = TransplantCore.Sanitize(mat.name);

            var shader = mat.shader;
            if (shader == null || shader.name == "Hidden/InternalErrorShader")
            {
                string shaderName = shader == null ? "(null)" : shader.name;
                return ArgFail(label, "material '" + mat.name + "' (" + materialPath + ") has a broken/missing shader ('" +
                    shaderName + "') — cannot enumerate texture slots");
            }

            var data = new RunData
            {
                whatIf = whatIf,
                instance = mat.name,
                source = materialPath,
            };

            var requestedSlots = forkTextureSlots ?? Array.Empty<string>();
            var shaderTexProps = mat.GetTexturePropertyNames();
            foreach (var slot in requestedSlots)
            {
                if (Array.IndexOf(shaderTexProps, slot) < 0)
                    data.Offender("no texture property '" + slot + "' on shader '" + shader.name + "'");
            }
            if (data.offenders.Count > 0)
            {
                data.result = "FAIL";
                data.error = "requested slot is not a texture property on the shader";
                return Finish(data, label);
            }
            var requested = new HashSet<string>(requestedSlots, StringComparer.Ordinal);

            string sourcePath = materialPath.Replace('\\', '/');
            bool outDirGiven = !string.IsNullOrEmpty(outDir);
            string outClean = outDirGiven ? outDir.Replace('\\', '/').TrimEnd('/') : null;
            string oName = !string.IsNullOrEmpty(newName) ? newName : mat.name;
            string targetPath = outDirGiven
                ? outClean + "/" + TransplantCore.Sanitize(oName) + ".mat"
                : sourcePath;

            bool inPlace = targetPath == sourcePath;
            if (inPlace)
            {
                string modeLabel = "augment " + label;

                if (outDirGiven)
                    return Fail(data, modeLabel, "outDir resolves to the source — omit outDir to augment in place");
                if (!string.IsNullOrEmpty(newName))
                    return Fail(data, modeLabel, "newName requires outDir; omit both to augment in place");
                if (!TransplantCore.IsWritableAsset(sourcePath))
                    return Fail(data, modeLabel, "vendor source needs an outDir to own it into a writable bucket");
                if (IsLocked(mat))
                    return Fail(data, modeLabel, "owned material is locked; unlock first");
                if (mat.parent != null)
                    return Fail(data, modeLabel, "owned material is a variant; OwnMaterial produces standalone materials");

                string inPlaceHome = InPlaceTextureHome(sourcePath, mat.name);

                if (whatIf)
                {
                    data.Note("would augment '" + sourcePath + "' in place");
                    return WhatIfPreview(mat, inPlaceHome, requested, data, modeLabel);
                }

                return ForkSlotsAndPersist(sourcePath, inPlaceHome, requested, data, modeLabel, createdO: false);
            }

            string mode = TransplantCore.IsWritableAsset(sourcePath) ? "branch" : "own";
            string coLabel = mode + " " + label;

            if (!AssetDatabase.IsValidFolder(outClean) && File.Exists(outClean))
                return Fail(data, coLabel, "outDir '" + outClean + "' resolves to a file, not a folder");

            if (!TransplantCore.IsWritableAsset(targetPath))
            {
                if (force) data.Note("read-only target override (force): " + targetPath);
                else
                    return Fail(data, coLabel, "target '" + targetPath +
                        "' is read-only (under Assets/Vendor or Packages): choose an owned outDir, or pass force=true");
            }

            if (AssetDatabase.LoadMainAssetAtPath(targetPath) != null)
                return Fail(data, coLabel, "an owned material already exists at '" + targetPath +
                    "' — pass it as materialPath to fork more slots, or choose another newName");

            string textureHome = outClean + "/" + TransplantCore.Sanitize(oName);

            if (whatIf)
            {
                data.Note("would create '" + targetPath + "'");
                if (mat.parent != null)
                    data.Note("would flatten variant source into a standalone material before forking");

                // Keep in step with execute's unlock guards below: each read-only FAIL previews here against S,
                // whose shader and tags the copy inherits. Only the unlock's own mutation cannot be previewed.
                if (IsLocked(mat))
                {
                    if (!TryResolvePoiUnlock(out _, out _, out string wiReason))
                        return Fail(data, coLabel, ThryUnresolvedMessage(wiReason));
                    if (!OriginalShaderResolves(mat, out string wiTag))
                        return Fail(data, coLabel, UnresolvableOriginalShaderMessage(wiTag));
                    data.Note("would unlock locked-poi material via Thry.ThryEditor.ShaderOptimizer.UnlockMaterials");
                }
                return WhatIfPreview(mat, textureHome, requested, data, coLabel);
            }

            TransplantCore.EnsureFolderExists(outClean);
            if (!TransplantCore.CopyAssetFile(sourcePath, targetPath))
                return Fail(data, coLabel, "asset copy failed: " + sourcePath + " -> " + targetPath);

            // Flatten before the unlock below: Thry's unlock selects m.GetRoot(), so on an unflattened variant it
            // would unlock S's root (a vendor mutation) instead of O.
            var normalizeTarget = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
            if (normalizeTarget == null)
            {
                AssetDatabase.DeleteAsset(targetPath);
                return Fail(data, coLabel, "owned copy failed to load: " + targetPath);
            }
            if (normalizeTarget.parent != null)
            {
                FlattenVariant(normalizeTarget);
                EditorUtility.SetDirty(normalizeTarget);
                AssetDatabase.SaveAssetIfDirty(normalizeTarget);
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                data.Note("flattened variant source into a standalone material before forking");
            }

            if (IsLocked(normalizeTarget))
            {
                if (!TryResolvePoiUnlock(out _, out _, out string resolveReason))
                {
                    AssetDatabase.DeleteAsset(targetPath);
                    return Fail(data, coLabel, ThryUnresolvedMessage(resolveReason));
                }

                if (!OriginalShaderResolves(normalizeTarget, out string origTag))
                {
                    AssetDatabase.DeleteAsset(targetPath);
                    return Fail(data, coLabel, UnresolvableOriginalShaderMessage(origTag));
                }

                var (unlocked, unlockReason) = PoiUnlock(normalizeTarget);
                if (!unlocked)
                {
                    AssetDatabase.DeleteAsset(targetPath);
                    return Fail(data, coLabel, "locked poi material but Thry ShaderOptimizer unlock failed: " +
                        unlockReason + " — is com.poiyomi.toon installed?");
                }

                // Not IsLocked(reloadedO): this must also FAIL on a null shader, which IsLocked treats as not-locked.
                var reloadedO = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
                if (reloadedO == null || reloadedO.shader == null ||
                    reloadedO.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal))
                {
                    AssetDatabase.DeleteAsset(targetPath);
                    return Fail(data, coLabel, "unlock claimed success but O still reports a locked shader");
                }

                // Still-locked backstop for a locked vendor S: a root-unlock or a deleted shared locked-shader
                // folder both change S's shader name, but a folder deletion only surfaces after a reimport, so a
                // plain cached load would false-PASS.
                if (!TransplantCore.IsWritableAsset(sourcePath))
                {
                    AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceUpdate);
                    var reloadedS = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
                    if (reloadedS == null || !IsLocked(reloadedS))
                    {
                        AssetDatabase.DeleteAsset(targetPath);
                        return Fail(data, coLabel, "still-locked backstop failed: vendor source '" + sourcePath +
                            "' is no longer locked after unlocking its copy — unlock reached the shared vendor " +
                            "material (root-unlock via GetRoot, or a deleted shared locked-shader folder)");
                    }
                }

                data.Note("unlocked locked-poi material via Thry.ThryEditor.ShaderOptimizer.UnlockMaterials");
            }

            return ForkSlotsAndPersist(targetPath, textureHome, requested, data, coLabel, createdO: true);
        }

        // ── Slot forking ────────────────────────────────────────────────────────────────────────────────

        static string InPlaceTextureHome(string sourcePath, string matName)
        {
            int slash = sourcePath.LastIndexOf('/');
            string folder = slash >= 0 ? sourcePath.Substring(0, slash) : "";
            return folder + "/" + TransplantCore.Sanitize(matName);
        }

        /// <summary>
        /// Read-only preview shared by both routes. <paramref name="src"/> stands in for O: S on copy-to-new,
        /// the caller's owned material in place.
        /// </summary>
        static string WhatIfPreview(Material src, string home, HashSet<string> requested, RunData data, string label)
        {
            // src is the caller's live material and whatIf mutates nothing, but BuildPlan's getters wipe its
            // keywords (see FlattenVariant), so the finally restores them even if BuildPlan throws.
            var kw = src.shaderKeywords;
            try
            {
                var sourceToDst = new Dictionary<string, string>(StringComparer.Ordinal);
                var claimedDstToSource = new Dictionary<string, string>(StringComparer.Ordinal);
                var plan = BuildPlan(src, home, requested, data, sourceToDst, claimedDstToSource);

                FillSlotsAndCounts(data, plan);
                if (data.offenders.Count > 0)
                {
                    data.result = "FAIL";
                    data.error = "slot fork validation failed";
                }
                else data.result = "PASS";
                return Finish(data, label);
            }
            finally { src.shaderKeywords = kw; }
        }

        /// <summary>
        /// The mutating span both routes share. With <paramref name="createdO"/>, O was just copied to
        /// <paramref name="targetPath"/> and a FAIL deletes it; otherwise that path is the caller's own material,
        /// which a FAIL never deletes. Forks into <paramref name="textureHome"/>, saves, and builds
        /// <c>slots[]</c> from the reloaded asset. A throw becomes a rolled-back FAIL RunLog.
        /// </summary>
        static string ForkSlotsAndPersist(string targetPath, string textureHome, HashSet<string> requested,
                                          RunData data, string label, bool createdO)
        {
            var newlyWrittenTex = new List<string>();
            bool homeCreatedThisRun = false;

            // Declared outside the try because the finally restores the keyword snapshot on every exit: augment's
            // rollback spares the caller's live material, which would otherwise keep the wiped keywords.
            Material owned = null;
            string[] keywordsBeforeFork = null;
            List<(string slot, Texture tex, Vector2 scale, Vector2 offset)> slotBackup = null;

            Action rollback = () =>
            {
                foreach (var t in newlyWrittenTex) AssetDatabase.DeleteAsset(t);
                if (createdO)
                {
                    AssetDatabase.DeleteAsset(targetPath);
                }
                else if (owned != null && slotBackup != null)
                {
                    // Augment keeps the caller's live material, so point its slots back at the pre-fork textures.
                    foreach (var b in slotBackup)
                    {
                        owned.SetTexture(b.slot, b.tex);
                        owned.SetTextureScale(b.slot, b.scale);
                        owned.SetTextureOffset(b.slot, b.offset);
                    }
                }
                if (homeCreatedThisRun && AssetDatabase.IsValidFolder(textureHome) &&
                    AssetDatabase.FindAssets("", new[] { textureHome }).Length == 0)
                    AssetDatabase.DeleteAsset(textureHome);
            };

            try
            {
                owned = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
                if (owned == null)
                {
                    rollback();
                    return Fail(data, label, "owned copy load failed (or wrong type): " + targetPath);
                }

                // Snapshot before BuildPlan's slot getters wipe the keywords (see FlattenVariant). For augment,
                // `owned` is the caller's live material, so this protects its in-memory state as well as the disk.
                keywordsBeforeFork = owned.shaderKeywords;

                if (!createdO)
                {
                    slotBackup = new List<(string, Texture, Vector2, Vector2)>();
                    foreach (var slot in requested)
                        slotBackup.Add((slot, owned.GetTexture(slot), owned.GetTextureScale(slot), owned.GetTextureOffset(slot)));
                }

                var sourceToDst = new Dictionary<string, string>(StringComparer.Ordinal);
                var claimedDstToSource = new Dictionary<string, string>(StringComparer.Ordinal);
                var plan = BuildPlan(owned, textureHome, requested, data, sourceToDst, claimedDstToSource);
                if (data.offenders.Count > 0)
                {
                    FillSlotsAndCounts(data, plan);
                    rollback();
                    data.result = "FAIL";
                    data.error = "slot fork validation failed";
                    return Finish(data, label);
                }

                bool anyForkNeeded = false;
                foreach (var row in plan) if (row.disposition == "forked" && row.needsCopy) { anyForkNeeded = true; break; }
                if (anyForkNeeded)
                {
                    homeCreatedThisRun = !AssetDatabase.IsValidFolder(textureHome);
                    TransplantCore.EnsureFolderExists(textureHome);
                }

                foreach (var row in plan)
                {
                    if (row.disposition != "forked") continue;
                    if (row.needsCopy)
                    {
                        if (!TransplantCore.CopyAssetFile(row.sourcePath, row.ownedPath))
                        {
                            data.Offender("asset copy failed (texture): " + row.sourcePath + " -> " + row.ownedPath);
                            continue;
                        }
                        newlyWrittenTex.Add(row.ownedPath);
                    }
                    var ownedTex = AssetDatabase.LoadAssetAtPath<Texture>(row.ownedPath);
                    if (ownedTex == null)
                    {
                        data.Offender("owned texture copy failed to load (or wrong type): " + row.ownedPath);
                        continue;
                    }
                    owned.SetTexture(row.slot, ownedTex);
                    if (string.Equals(Path.GetExtension(row.ownedPath), ".psd", StringComparison.OrdinalIgnoreCase))
                        data.Warning("slot '" + row.slot + "' owned copy is a .psd (" + row.ownedPath +
                            ") — export a PNG per the own-material skill's follow-up");
                }
                if (data.offenders.Count > 0)
                {
                    FillSlotsAndCounts(data, plan);
                    rollback();
                    data.result = "FAIL";
                    data.error = "texture fork failed";
                    return Finish(data, label);
                }

                // Enable streaming mip maps on every texture this run copied, never on the Vendor/ source. Vendor
                // textures routinely ship it off, which the VRCSDK refuses an upload over, and the fork is the one
                // moment we own a copy we may fix. StopAssetEditing must run: an escaped exception would otherwise
                // leave the whole Editor with importing disabled.
                var mipsToImport = new List<string>();
                foreach (var texPath in newlyWrittenTex)
                {
                    var ti = AssetImporter.GetAtPath(texPath) as TextureImporter;
                    if (ti == null || !ti.mipmapEnabled || ti.streamingMipmaps) continue;
                    ti.streamingMipmaps = true;
                    if (AssetDatabase.WriteImportSettingsIfDirty(texPath)) mipsToImport.Add(texPath);
                    else data.Warning("streaming mip maps not persisted for " + texPath +
                        " (import settings write reported nothing written) — enable it by hand before upload");
                }
                if (mipsToImport.Count > 0)
                {
                    AssetDatabase.StartAssetEditing();
                    try
                    {
                        foreach (var texPath in mipsToImport)
                            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
                    }
                    finally { AssetDatabase.StopAssetEditing(); }
                }
                int mipsEnabled = mipsToImport.Count;

                // Before the save, so wiped keywords do not persist.
                owned.shaderKeywords = keywordsBeforeFork;

                EditorUtility.SetDirty(owned);
                AssetDatabase.SaveAssetIfDirty(owned);

                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
                if (reloaded == null)
                {
                    rollback();
                    return Fail(data, label, "owned material failed to reload after save: " + targetPath);
                }

                foreach (var row in plan)
                {
                    string disposition = row.disposition;
                    string ownedPathFinal = row.ownedPath;

                    if (row.requested && (disposition == "forked" || disposition == "already-owned"))
                    {
                        var rt = reloaded.GetTexture(row.slot);
                        string rp = rt != null ? AssetDatabase.GetAssetPath(rt) : null;
                        bool landed = rt != null && UnderHome(rp, textureHome);
                        if (!landed)
                        {
                            data.Offender("fork did not land: slot '" + row.slot + "'" +
                                (rt != null ? " (still " + rp + ")" : " (empty)"));
                            disposition = "unforkable";
                            ownedPathFinal = null;
                        }
                        else
                        {
                            ownedPathFinal = rp;
                        }
                    }

                    data.slots.Add(new SlotRow
                    {
                        slot = row.slot,
                        requested = row.requested,
                        disposition = disposition,
                        sourcePath = row.sourcePath,
                        ownedPath = ownedPathFinal,
                    });
                    TallyDisposition(data, disposition);
                }
                EmitDispositionCounts(data);

                if (data.offenders.Count > 0)
                {
                    rollback();
                    data.result = "FAIL";
                    data.error = "fork did not land (post-condition)";
                    return Finish(data, label);
                }
                // Past every rollback gate, so this note cannot outlive the textures it names.
                if (mipsEnabled > 0)
                    data.Note("enabled streaming mip maps on " + mipsEnabled + " forked texture(s) (VRChat upload defense; owned copies only)");
                data.result = "PASS";
                return Finish(data, label);
            }
            catch (Exception ex)
            {
                data.result = "FAIL";
                data.error = ex.GetType().Name + ": " + ex.Message;
                data.Offender("unexpected exception during fork: " + ex.GetType().Name + ": " + ex.Message);
                rollback();
            }
            finally
            {
                if (owned != null && keywordsBeforeFork != null) owned.shaderKeywords = keywordsBeforeFork;
            }
            return Finish(data, label);
        }

        struct PlanRow
        {
            public string slot;
            public bool requested;
            public string disposition; // forked | already-owned | unforkable | vendor-ref | owned-elsewhere | empty
            public string sourcePath;
            public string ownedPath;   // intended dst for forked/already-owned; else null
            public bool needsCopy;     // meaningful only when disposition == "forked"
        }

        /// <summary>
        /// Classifies every texture slot on <paramref name="src"/> into one final disposition, recording offenders
        /// and notes on <paramref name="data"/>. Writes nothing to disk, so whatIf and execute share it.
        /// </summary>
        static List<PlanRow> BuildPlan(Material src, string textureHome, HashSet<string> requested, RunData data,
                                       Dictionary<string, string> sourceToDst, Dictionary<string, string> claimedDstToSource)
        {
            var rows = new List<PlanRow>();
            foreach (var slot in src.GetTexturePropertyNames())
            {
                var tex = src.GetTexture(slot);
                bool isReq = requested.Contains(slot);
                string texPath = tex != null ? AssetDatabase.GetAssetPath(tex) : null;

                string disposition; string ownedPath = null; bool needsCopy = false;

                if (tex == null)
                {
                    disposition = "empty";
                    if (isReq)
                    {
                        data.Offender("slot '" + slot + "' is empty — nothing to fork");
                        disposition = "unforkable";
                    }
                }
                else if (IsBuiltinPath(texPath))
                {
                    disposition = "empty";
                    if (isReq)
                    {
                        data.Offender("slot '" + slot + "' references a built-in texture (" + texPath + ") — not forkable");
                        disposition = "unforkable";
                    }
                }
                else if (!(tex is Texture2D))
                {
                    disposition = "empty";
                    if (isReq)
                    {
                        data.Offender("slot '" + slot + "' is a " + tex.GetType().Name + ", not a Texture2D — not forkable");
                        disposition = "unforkable";
                    }
                }
                else
                {
                    bool subAsset = !AssetDatabase.IsMainAsset(tex);
                    bool underHome = UnderHome(texPath, textureHome);
                    string natural = underHome ? "already-owned"
                        : (TransplantCore.IsWritableAsset(texPath) ? "owned-elsewhere" : "vendor-ref");

                    if (!isReq)
                    {
                        disposition = natural;
                        if (natural == "already-owned") ownedPath = texPath;
                        if (natural == "owned-elsewhere")
                            data.Note("slot '" + slot + "' references another material's owned texture (shared, not requested): " + texPath);
                    }
                    else if (subAsset)
                    {
                        data.Offender("slot '" + slot + "' texture is a sub-asset (embedded, e.g. inside an FBX) — not forkable");
                        disposition = "unforkable";
                    }
                    else if (natural == "already-owned")
                    {
                        disposition = "already-owned";
                        ownedPath = texPath;
                    }
                    else
                    {
                        var resolution = PlanFork(texPath, textureHome, sourceToDst, claimedDstToSource);
                        if (resolution.refused)
                        {
                            data.Offender(resolution.refuseReason);
                            disposition = "unforkable";
                        }
                        else
                        {
                            disposition = "forked";
                            ownedPath = resolution.dstPath;
                            needsCopy = resolution.needsCopy;
                        }
                    }
                }

                rows.Add(new PlanRow
                {
                    slot = slot, requested = isReq, disposition = disposition,
                    sourcePath = texPath, ownedPath = ownedPath, needsCopy = needsCopy,
                });
            }
            return rows;
        }

        struct ForkResolution
        {
            public string dstPath;
            public bool needsCopy;
            public bool refused;
            public string refuseReason;
        }

        /// <summary>
        /// Resolves the fork destination, the sole collision authority in both whatIf and execute, so a collision
        /// refuses identically in both. In order:
        /// (a) this source already claimed a <c>dst</c> this run (two slots, one source) → reuse it;
        /// (b) a different source claimed the <c>dst</c> this run → refuse, on the claimed map and never
        ///     <c>File.Exists</c>, because this run's first copy has not landed yet;
        /// (c) <c>dst</c> exists on disk from a prior run → byte-compare: equal reuses it, different refuses;
        /// (d) otherwise claim it and plan a copy.
        /// </summary>
        static ForkResolution PlanFork(string sourceTexPath, string textureHome,
                                       Dictionary<string, string> sourceToDst, Dictionary<string, string> claimedDstToSource)
        {
            if (sourceToDst.TryGetValue(sourceTexPath, out var existingDst))
                return new ForkResolution { dstPath = existingDst, needsCopy = false };

            string leaf = Path.GetFileName(sourceTexPath);
            string dst = textureHome + "/" + leaf;

            if (claimedDstToSource.TryGetValue(dst, out var claimant) && claimant != sourceTexPath)
            {
                return new ForkResolution
                {
                    refused = true,
                    refuseReason = "two different source textures named '" + leaf + "' collide in '" + textureHome +
                        "' ('" + claimant + "' already claimed '" + dst + "' this run, '" + sourceTexPath +
                        "' also wants it) — fork them into separate owned materials, or leave one slot unforked",
                };
            }

            if (File.Exists(dst))
            {
                if (BytesEqual(dst, sourceTexPath))
                {
                    sourceToDst[sourceTexPath] = dst;
                    claimedDstToSource[dst] = sourceTexPath;
                    return new ForkResolution { dstPath = dst, needsCopy = false };
                }
                return new ForkResolution
                {
                    refused = true,
                    refuseReason = "'" + dst + "' already exists on disk from a prior run with content different from '" +
                        sourceTexPath + "' — two different source textures collide at this owned path; fork into a " +
                        "different owned material, or delete/reconcile the stale texture at '" + dst + "'",
                };
            }

            sourceToDst[sourceTexPath] = dst;
            claimedDstToSource[dst] = sourceTexPath;
            return new ForkResolution { dstPath = dst, needsCopy = true };
        }

        static void FillSlotsAndCounts(RunData data, List<PlanRow> plan)
        {
            foreach (var row in plan)
            {
                data.slots.Add(new SlotRow
                {
                    slot = row.slot, requested = row.requested, disposition = row.disposition,
                    sourcePath = row.sourcePath, ownedPath = row.ownedPath,
                });
                TallyDisposition(data, row.disposition);
            }
            EmitDispositionCounts(data);
        }

        static void TallyDisposition(RunData data, string disposition)
        {
            data.tally.TryGetValue(disposition, out long n);
            data.tally[disposition] = n + 1;
        }

        /// <summary>There is no <c>slotsBuiltin</c> count: an untouched built-in or non-Texture2D slot counts as
        /// <c>empty</c>, a requested one as <c>unforkable</c>.</summary>
        static void EmitDispositionCounts(RunData data)
        {
            long Get(string d) { data.tally.TryGetValue(d, out long n); return n; }
            data.Count("slotsForked", Get("forked"));
            data.Count("slotsAlreadyOwned", Get("already-owned"));
            data.Count("slotsUnforkable", Get("unforkable"));
            data.Count("slotsOwnedElsewhere", Get("owned-elsewhere"));
            data.Count("slotsVendorRef", Get("vendor-ref"));
            data.Count("slotsEmpty", Get("empty"));
        }

        static bool IsBuiltinPath(string texPath) =>
            string.IsNullOrEmpty(texPath) ||
            texPath.StartsWith("Resources/", StringComparison.Ordinal) ||
            texPath.IndexOf("unity_builtin_extra", StringComparison.Ordinal) >= 0;

        /// <summary>Segment-boundary containment, not a string prefix: home "Dress" must not match "Dress_White".</summary>
        static bool UnderHome(string path, string home) =>
            !string.IsNullOrEmpty(path) && (path == home || path.StartsWith(home + "/", StringComparison.Ordinal));

        /// <summary>Compares bytes, not GUIDs: a copied asset always mints a fresh GUID.</summary>
        static bool BytesEqual(string pathA, string pathB)
        {
            byte[] a, b;
            try
            {
                if (new FileInfo(pathA).Length != new FileInfo(pathB).Length) return false;
                a = File.ReadAllBytes(pathA); b = File.ReadAllBytes(pathB);
            }
            catch { return false; }
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Keys on the shader name alone: Thry's generated <c>Hidden/Locked/…</c> shaders. Never read the
        /// <c>AllLockedGUIDS</c> tag, which real vendor Poiyomi materials carry stale and non-empty while fully
        /// unlocked.</summary>
        internal static bool IsLocked(Material m) =>
            m.shader != null && m.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal);

        static string ThryUnresolvedMessage(string reason) =>
            "locked poi material but Thry ShaderOptimizer not found — is com.poiyomi.toon installed? (" + reason + ")";

        static string UnresolvableOriginalShaderMessage(string tag) =>
            "locked poi material's original-shader tag ('" + tag + "') does not resolve to a shader — cannot safely unlock";

        /// <summary>
        /// Whether <paramref name="m"/>'s original-shader tag resolves to a real shader. When it does not (a stale
        /// GUID after a Poiyomi reinstall, or a renamed shader), Thry's <c>UnlockConcrete</c> pops a blocking
        /// <c>EditorUtility.DisplayDialog</c> that would hang the session, so callers refuse instead. Mirrors Thry's
        /// fallback order: GUID tag → <c>GUIDToAssetPath</c> → <c>LoadAssetAtPath&lt;Shader&gt;</c>, else name tag →
        /// <c>Shader.Find</c>. <paramref name="tag"/> returns the value examined, for the refusal message.
        /// </summary>
        internal static bool OriginalShaderResolves(Material m, out string tag)
        {
            string guid = m.GetTag(ThryTagOriginalShaderGuid, false, "");
            if (!string.IsNullOrEmpty(guid))
            {
                tag = guid;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<Shader>(path) != null) return true;
            }

            string name = m.GetTag(ThryTagOriginalShader, false, "");
            if (!string.IsNullOrEmpty(name))
            {
                tag = name;
                if (Shader.Find(name) != null) return true;
            }

            tag = string.IsNullOrEmpty(guid) ? name : guid;
            return false;
        }

        /// <summary>Resolved by reflection because avatar-tools must not reference <c>com.poiyomi.toon</c>.</summary>
        static bool TryResolvePoiUnlock(out MethodInfo method, out Type progressBarType, out string reason)
        {
            method = null;
            progressBarType = null;

            Type shaderOptimizerType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(ThryShaderOptimizerTypeName, false); } catch { /* malformed/dynamic assembly — skip */ }
                if (t != null) { shaderOptimizerType = t; break; }
            }
            if (shaderOptimizerType == null)
            {
                reason = "type '" + ThryShaderOptimizerTypeName + "' not found in any loaded assembly";
                return false;
            }

            foreach (var m in shaderOptimizerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "UnlockMaterials") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2 || !ps[1].ParameterType.IsEnum) continue;
                if (!ps[0].ParameterType.IsAssignableFrom(typeof(Material[]))) continue;
                method = m;
                progressBarType = ps[1].ParameterType;
                break;
            }
            if (method == null)
            {
                reason = "method 'UnlockMaterials(IEnumerable<Material>, ProgressBar)' not found on " + ThryShaderOptimizerTypeName;
                return false;
            }

            reason = null;
            return true;
        }

        static (bool ok, string reason) PoiUnlock(Material o)
        {
            try
            {
                if (!TryResolvePoiUnlock(out var method, out var progressBarType, out string resolveReason))
                    return (false, resolveReason);

                object none = Enum.ToObject(progressBarType, 0);
                object result;
                try
                {
                    result = method.Invoke(null, new object[] { new[] { o }, none });
                }
                catch (TargetInvocationException tie)
                {
                    var inner = tie.InnerException ?? (Exception)tie;
                    return (false, inner.GetType().Name + ": " + inner.Message);
                }
                return (result is bool ok && ok) ? (true, null) : (false, "UnlockMaterials returned false");
            }
            catch (Exception ex)
            {
                return (false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ── Normalize: variant flatten ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bakes a Material Variant into a standalone material: captures keywords, <c>renderQueue</c> and override
        /// tags, then every shader property through the parent chain, then severs <c>o.parent</c> and re-applies
        /// them all as <paramref name="o"/>'s own values. The caller owns the save.
        /// </summary>
        static void FlattenVariant(Material o)
        {
            var shader = o.shader;

            // Read keywords before any shader-property Get/Set below: the first GetColor/GetFloat/GetVector/GetInt/
            // GetTexture call on a Material instance silently clears its ENTIRE shaderKeywords array, a Unity quirk
            // (renderQueue, parent, the array getter and IsKeywordEnabled do not trigger it).
            // The resolved set is the union of two sources, because neither alone is complete:
            //   - o.shaderKeywords: local overrides only, including keywords enabled via EnableKeyword that are not
            //     in the shader's declared keyword space (common with Poiyomi/lilToon feature toggles).
            //   - IsKeywordEnabled over shader.keywordSpace.keywordNames: resolves through the parent chain, but
            //     only among shader-declared names.
            // Residual gap: a keyword purely inherited from the parent and not shader-declared is recoverable by
            // neither.
            var enabledKeywords = new HashSet<string>(o.shaderKeywords, StringComparer.Ordinal);
            foreach (var kw in shader.keywordSpace.keywordNames)
                if (o.IsKeywordEnabled(kw)) enabledKeywords.Add(kw);

            int renderQueue = o.renderQueue;
            var tags = CaptureOverrideTags(o);

            var floats = new List<(string name, float val)>();
            var colors = new List<(string name, Color val)>();
            var vectors = new List<(string name, Vector4 val)>();
            var ints = new List<(string name, int val)>();
            var textures = new List<(string name, Texture tex, Vector2 scale, Vector2 offset)>();

            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++)
            {
                string name = shader.GetPropertyName(i);
                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        colors.Add((name, o.GetColor(name)));
                        break;
                    case ShaderPropertyType.Vector:
                        vectors.Add((name, o.GetVector(name)));
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        floats.Add((name, o.GetFloat(name)));
                        break;
                    case ShaderPropertyType.Int:
                        ints.Add((name, o.GetInt(name)));
                        break;
                    case ShaderPropertyType.Texture:
                        textures.Add((name, o.GetTexture(name), o.GetTextureScale(name), o.GetTextureOffset(name)));
                        break;
                }
            }

            o.parent = null;

            foreach (var p in floats) o.SetFloat(p.name, p.val);
            foreach (var p in colors) o.SetColor(p.name, p.val);
            foreach (var p in vectors) o.SetVector(p.name, p.val);
            foreach (var p in ints) o.SetInt(p.name, p.val);
            foreach (var p in textures)
            {
                o.SetTexture(p.name, p.tex);
                o.SetTextureScale(p.name, p.scale);
                o.SetTextureOffset(p.name, p.offset);
            }
            var kwArray = new string[enabledKeywords.Count];
            enabledKeywords.CopyTo(kwArray);
            o.shaderKeywords = kwArray;
            o.renderQueue = renderQueue;
            foreach (var kv in tags) o.SetOverrideTag(kv.Key, kv.Value);
        }

        /// <summary>
        /// Override-tag names from <paramref name="o"/>'s whole parent chain, each resolved to its effective value.
        /// No public API enumerates tags, and a tag never overridden on <paramref name="o"/> lives only in an
        /// ancestor's serialized <c>stringTagMap</c>, so this walks it.
        /// </summary>
        static List<KeyValuePair<string, string>> CaptureOverrideTags(Material o)
        {
            var tagNames = new HashSet<string>(StringComparer.Ordinal);
            for (var m = o; m != null; m = m.parent)
            {
                var it = new SerializedObject(m).GetIterator();
                while (it.Next(true))
                {
                    if (it.name != "stringTagMap") continue;
                    for (int i = 0; i < it.arraySize; i++)
                        tagNames.Add(it.GetArrayElementAtIndex(i).displayName);
                }
            }
            var tags = new List<KeyValuePair<string, string>>();
            foreach (var name in tagNames)
                tags.Add(new KeyValuePair<string, string>(name, o.GetTag(name, false, "")));
            return tags;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

        static string ArgFail(string label, string msg)
        {
            var data = new RunData { result = "FAIL", error = msg };
            data.Offender(msg);
            return Finish(data, label);
        }

        static string Fail(RunData data, string label, string msg)
        {
            data.result = "FAIL";
            data.error = msg;
            data.Offender(msg);
            return Finish(data, label);
        }

        /// <summary>Every exit funnels here, so every RunLog carries a <c>slots</c> key, empty on a pre-plan FAIL.</summary>
        static string Finish(RunData data, string label)
        {
            data.Section("slots", RenderSlots(data.slots));
            return TransplantCore.Finish(data, label);
        }

        static string RenderSlots(List<SlotRow> slots)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"slot\": ").Append(TransplantCore.Q(s.slot))
                  .Append(", \"requested\": ").Append(s.requested ? "true" : "false")
                  .Append(", \"disposition\": ").Append(TransplantCore.Q(s.disposition))
                  .Append(", \"sourcePath\": ").Append(TransplantCore.Q(s.sourcePath))
                  .Append(", \"ownedPath\": ").Append(TransplantCore.Q(s.ownedPath))
                  .Append(" }");
            }
            sb.Append(slots.Count > 0 ? "\n  ]" : "]");
            return sb.ToString();
        }

        // ── Data types ────────────────────────────────────────────────────────────────────────────────

        /// <summary>One row of the RunLog's <c>slots</c> table, in <see cref="PlanRow"/>'s disposition vocabulary.</summary>
        struct SlotRow
        {
            public string slot;
            public bool requested;
            public string disposition;
            public string sourcePath;
            public string ownedPath;
        }

        sealed class RunData : RunLog
        {
            public RunData() : base("own-material") { }
            public readonly List<SlotRow> slots = new List<SlotRow>();
            public readonly Dictionary<string, long> tally = new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }
}
