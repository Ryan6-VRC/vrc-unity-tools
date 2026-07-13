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
    /// Materializes an owned deep-copy of a vendor material (or branches/augments an already-owned one)
    /// and forks exactly the named texture slots into that material's own namespace — every other slot
    /// keeps its source GUID reference. Routing is a function of target identity: <c>outDir</c> given ⇒
    /// own (vendor source) or branch (owned source, copy-to-new); <c>outDir</c> omitted ⇒ augment the
    /// source in place (fork more slots on an already-owned material). The skill holds the judgment of
    /// which slots to fork; this tool executes the copy deterministically and reports a per-slot
    /// provenance table (<c>slots[]</c>) as the caller's verification gate.
    ///
    /// Full behavioral spec: <c>docs/superpowers/specs/2026-07-11-own-material-lean-design.md</c>. This
    /// file implements the full Flow: arg guards, slot-name validation, target-identity routing
    /// (own / branch / augment) with a copy-to-new deep copy, the copy-to-new normalize step (a variant
    /// source is flattened into a standalone material BEFORE the unlock seam — Thry's unlock selects
    /// <c>GetRoot()</c>, so an un-flattened variant would unlock the vendor root instead of O — then a
    /// locked O is unlocked via reflection into <c>Thry.ThryEditor.ShaderOptimizer.UnlockMaterials</c>,
    /// avatar-tools never referencing <c>com.poiyomi.toon</c>, gated by a dialog guard that refuses rather
    /// than risk Thry's blocking <c>DisplayDialog</c> when the original-shader tag can't resolve, and a
    /// still-locked backstop that force-reimports a locked vendor source after unlocking its copy and
    /// asserts the vendor is untouched), and the selective texture fork engine — requested slots resolve
    /// into the per-material texture home <c>H(O) = &lt;O.folder&gt;/&lt;Sanitize(O.name)&gt;/</c> via a
    /// run-local claimed-<c>dst</c> map (reuse/refuse, never a content-hash dedup pass or GUID-suffix
    /// collision escape), untouched slots report their current reference, and the disk-truthful
    /// post-condition rebuilds <c>slots[]</c> from the reloaded asset. The unlock's actual success path is
    /// verified live in Task 7 (TestEditor has no Poiyomi package, so only detection and the
    /// refuse-when-Thry-absent path are unit-testable here).
    ///
    /// Call <see cref="Run"/> from MCP execute_code or a menu item.
    /// </summary>
    [AgentTool]
    public static class OwnMaterial
    {
        // ── Thry (Poiyomi ShaderOptimizer) reflection constants — confirmed against
        // Packages/com.poiyomi.toon/_PoiyomiShaders/Scripts/ThryEditor/Editor/ShaderOptimizer.cs: the type
        // lives in namespace Thry.ThryEditor (not bare Thry). Tag names match ShaderOptimizer's own TAG_*
        // consts verbatim. ──
        const string ThryShaderOptimizerTypeName = "Thry.ThryEditor.ShaderOptimizer";
        const string ThryTagOriginalShader = "OriginalShader";
        const string ThryTagOriginalShaderGuid = "OriginalShaderGUID";

        /// <summary>
        /// Bring <paramref name="materialPath"/> into ownership and fork <paramref name="forkTextureSlots"/>
        /// (shader texture-property names, e.g. <c>_MainTex</c>) into the resulting owned material's own
        /// namespace. <paramref name="outDir"/> given ⇒ own/branch a NEW owned <c>.mat</c> there (named
        /// <paramref name="newName"/>, default the source's own name); omitted ⇒ augment the (already-owned)
        /// source in place. Returns a one-line PASS/FAIL summary ending with the RunLog path
        /// (<c>… =&gt; RESULT | log=&lt;path&gt;</c>); also Debug.Log/LogError's it.
        /// </summary>
        public static string Run(string materialPath, string outDir = null, string[] forkTextureSlots = null,
                                 string newName = null, bool force = false, bool whatIf = false)
        {
            // ── Guards (Flow step 1) ────────────────────────────────────────────────────────────────
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

            // ── Slot-name validation (Flow step 2) — BEFORE any write/routing (no mode resolved yet, so
            //    this FAIL stays label-only). A copy preserves the shader, so validating against S's
            //    texture properties == validating against O's; failing here means a typo never leaves a
            //    half-made copy. ──
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

            // ── Routing (Flow step 3): targetPath is a pure function of target identity. From here on
            //    the mode IS known, so every Fail/Finish call below prefixes it onto the label (the
            //    mode-first one-liner: a wrong-mode run is catchable at a glance). ──
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

                // outDir given but resolves to the source: never a silent reroute — one signal
                // (outDir present/absent), one meaning (copy-to-new vs. augment).
                if (outDirGiven)
                    return Fail(data, modeLabel, "outDir resolves to the source — omit outDir to augment in place");
                // outDir omitted but newName given: closes the "meant to branch, forgot outDir, silently
                // mutated the SOURCE" hole (DELTA 4 — attempt 1 did not have this guard).
                if (!string.IsNullOrEmpty(newName))
                    return Fail(data, modeLabel, "newName requires outDir; omit both to augment in place");
                // A vendor source has nowhere writable to land — it must be OWNED first via an outDir.
                if (!TransplantCore.IsWritableAsset(sourcePath))
                    return Fail(data, modeLabel, "vendor source needs an outDir to own it into a writable bucket");
                // In-place O (== S here) must be neither locked nor a variant — this tool never
                // unlocks/flattens in place (that's the copy-to-new normalize step, Tasks 5/6).
                if (IsLocked(mat))
                    return Fail(data, modeLabel, "owned material is locked; unlock first");
                if (mat.parent != null)
                    return Fail(data, modeLabel, "owned material is a variant; OwnMaterial produces standalone materials");

                // H(O) for the in-place route: O IS S, so O's folder is S's own folder — augment never
                // renames or relocates, unlike the copy-to-new oName/outClean pairing below.
                string inPlaceHome = InPlaceTextureHome(sourcePath, mat.name);

                if (whatIf)
                {
                    data.Note("would augment '" + sourcePath + "' in place");
                    return WhatIfPreview(mat, inPlaceHome, requested, data, modeLabel);
                }

                // Augment: O = S already exists — createdO=false so a FAIL below never deletes the
                // pre-existing (possibly hand-edited) material; only newly-copied textures roll back.
                return ForkSlotsAndPersist(sourcePath, inPlaceHome, requested, data, modeLabel, createdO: false);
            }

            // ── Copy-to-new: own (S vendor) or branch (S owned) — same mechanics, RunLog names which. ──
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

            // H(O) = <O.folder>/<Sanitize(O.name)>/ — same oName used to build targetPath above, so O's
            // eventual on-disk name always matches the home this plans against.
            string textureHome = outClean + "/" + TransplantCore.Sanitize(oName);

            if (whatIf)
            {
                data.Note("would create '" + targetPath + "'");
                if (mat.parent != null)
                    data.Note("would flatten variant source into a standalone material before forking");

                // Reproduce every read-only normalize FAIL on S directly (copy-to-new preserves the
                // shader + tags, so IsLocked(S)/the tag resolution == O's pre-fork). The Thry-resolve
                // check AND the dialog-guard's OriginalShaderResolves are both pure read-only (tags +
                // GUIDToAssetPath/LoadAssetAtPath/Shader.Find, no Thry, no dialog risk), so both preview
                // here — the only un-previewable step is the unlock's actual mutation. On mat (parent
                // chain intact), matching execute's post-flatten computation.
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
            if (!AssetDatabase.CopyAsset(sourcePath, targetPath))
                return Fail(data, coLabel, "CopyAsset failed: " + sourcePath + " -> " + targetPath);

            // ── Normalize O (Flow step 4, copy-to-new only — an in-place O is already normal since the
            //    in-place guard above FAILs a variant/locked owned source before we ever get here).
            //    Flatten a variant into a standalone material BEFORE the unlock below: Thry's unlock
            //    selects m.GetRoot(), so an un-flattened variant would resolve to S's root and unlock THAT
            //    (a vendor mutation) instead of O. ──
            var normalizeTarget = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
            if (normalizeTarget == null)
            {
                AssetDatabase.DeleteAsset(targetPath);
                return Fail(data, coLabel, "owned copy load failed immediately after CopyAsset: " + targetPath);
            }
            if (normalizeTarget.parent != null)
            {
                FlattenVariant(normalizeTarget);
                EditorUtility.SetDirty(normalizeTarget);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
                data.Note("flattened variant source into a standalone material before forking");
            }

            // ── Unlock O if locked (Flow step 4, second half) — gated on O's own state (post-flatten, so
            //    GetRoot() == O), so an already-unlocked branch source no-ops. Thry-resolve check FIRST
            //    (poi simply absent is the common real-world FAIL — surfaces immediately naming Thry,
            //    without ever popping Thry's own modal), THEN the dialog guard (refuses rather than risk
            //    UnlockConcrete's blocking EditorUtility.DisplayDialog when the original-shader tag can't
            //    resolve). Any FAIL here rolls back the just-created O (copy-to-new orphan). ──
            if (IsLocked(normalizeTarget))
            {
                if (!TryResolvePoiUnlock(out _, out _, out string resolveReason))
                {
                    AssetDatabase.DeleteAsset(targetPath);
                    return Fail(data, coLabel, ThryUnresolvedMessage(resolveReason));
                }

                // Dialog guard: Thry's UnlockConcrete pops a blocking EditorUtility.DisplayDialog in a live
                // (non-batch) editor when the original-shader tag is present but does not RESOLVE to a real
                // shader (a stale GUID after a poi reinstall/version bump, or a renamed shader) — a modal
                // that would hang the session. Mirror Thry's own two-step fallback and refuse rather than
                // risk it: GUID tag → GUIDToAssetPath → LoadAssetAtPath<Shader>, else name tag → Shader.Find.
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

                // Hand-rolled rather than IsLocked(reloadedO): this must also FAIL on a null shader (a
                // corrupt post-unlock load), which IsLocked treats as not-locked.
                var reloadedO = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
                if (reloadedO == null || reloadedO.shader == null ||
                    reloadedO.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal))
                {
                    AssetDatabase.DeleteAsset(targetPath);
                    return Fail(data, coLabel, "unlock claimed success but O still reports a locked shader");
                }

                // Still-locked backstop (replaces the cut vendor-byte gate — see the spec's "Cut from
                // attempt 1"): when S was a locked VENDOR material, force-reimport it and assert it is
                // STILL locked. A root-unlock via GetRoot() (an un-flattened variant, which can't happen
                // here — flatten already ran above) or a deleted shared locked-shader folder both change
                // S's shader name, but a folder deletion only surfaces after a reimport — a plain cached
                // load would false-PASS. Never reached in the SDK venue (Thry never resolves, so unlock
                // never succeeds this far); exercised live in Task 7.
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

            // O now exists on disk (createdO=true) — any FAIL from here rolls it back (see
            // ForkSlotsAndPersist's rollback closure).
            return ForkSlotsAndPersist(targetPath, textureHome, requested, data, coLabel, createdO: true);
        }

        // ── Slot forking (Flow steps 5–6) ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>H(O) = &lt;O.folder&gt;/&lt;Sanitize(O.name)&gt;/</c> for the in-place route, where O's folder
        /// is S's own folder (no <c>outDir</c>/<c>newName</c> — augment never renames or relocates).
        /// </summary>
        static string InPlaceTextureHome(string sourcePath, string matName)
        {
            int slash = sourcePath.LastIndexOf('/');
            string folder = slash >= 0 ? sourcePath.Substring(0, slash) : "";
            return folder + "/" + TransplantCore.Sanitize(matName);
        }

        /// <summary>
        /// Read-only preview shared by both routes: full slot dispositioning off <paramref name="src"/>
        /// (S for copy-to-new — an as-yet-uncreated O is byte-identical to it; the caller's owned material
        /// for in-place) against <paramref name="home"/>. Mutates nothing on disk; reproduces every
        /// read-only slot offender identically to execute (same <see cref="BuildPlan"/> call, a fresh
        /// claimed-map — same-run collisions resolve the same way in both modes).
        /// </summary>
        static string WhatIfPreview(Material src, string home, HashSet<string> requested, RunData data, string label)
        {
            // Snapshot BEFORE the first texture-slot getter BuildPlan fires (GetTexture silently wipes the
            // material's in-memory shaderKeywords — memory unity-material-getter-clears-keywords). whatIf
            // saves nothing, but src is the caller's LIVE material instance; leaving it keyword-wiped would
            // be a real side effect of a call that promises to mutate nothing.
            // Restore is in a finally — src is the caller's LIVE material, and whatIf promises to mutate
            // nothing; if BuildPlan throws, a bare post-call restore would be skipped and leave src
            // keyword-wiped with no RunLog (the execute path guards this exact case, see ForkSlotsAndPersist's
            // finally).
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
        /// Shared mutating span for both copy-to-new (own/branch, <paramref name="createdO"/>=true — O was
        /// just created by <c>CopyAsset</c> at <paramref name="targetPath"/>) and in-place (augment,
        /// <paramref name="createdO"/>=false — <paramref name="targetPath"/> IS the caller's pre-existing
        /// owned material): loads O, runs the fork rule (Flow step 5) against <paramref name="textureHome"/>,
        /// persists, then rebuilds the disk-truthful <c>slots[]</c> table from the reloaded asset (Flow step 6).
        /// An unexpected throw still rolls back and emits a FAIL RunLog, never propagates. <c>rollback</c>
        /// ALWAYS cleans up textures <em>this run</em> wrote (never a reused pre-existing texture) but deletes
        /// the material itself only when <paramref name="createdO"/> is true — a FAIL augmenting a
        /// pre-existing owned material must never delete it.
        /// </summary>
        static string ForkSlotsAndPersist(string targetPath, string textureHome, HashSet<string> requested,
                                          RunData data, string label, bool createdO)
        {
            var newlyWrittenTex = new List<string>();     // texture dsts where CopyAsset actually ran this run
            bool homeCreatedThisRun = false;               // did THIS run mkdir H(O)? (prune only then, only if empty)

            // Hoisted above rollback (which needs `owned`/`slotBackup`) and above the try (so the finally
            // can restore the keyword snapshot on EVERY exit path — early return, exception, or
            // fall-through): for AUGMENT, rollback does NOT delete the caller's material, so a restore that
            // only ran on some exits would leave the caller's LIVE in-memory material keyword-wiped with no
            // recovery on any exit the restore forgot.
            Material owned = null;
            string[] keywordsBeforeFork = null;
            List<(string slot, Texture tex, Vector2 scale, Vector2 offset)> slotBackup = null;

            Action rollback = () =>
            {
                foreach (var t in newlyWrittenTex) AssetDatabase.DeleteAsset(t); // never a reused pre-existing texture
                if (createdO)
                {
                    AssetDatabase.DeleteAsset(targetPath);
                }
                else if (owned != null && slotBackup != null)
                {
                    // Augment rollback restores the caller's live slot refs it mutated, since it (correctly)
                    // never deletes the material — otherwise a later row's failure would leave the caller's
                    // LIVE material pointing at textures rollback just deleted above.
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

                // Snapshot BEFORE any texture-slot getter fires (BuildPlan's classification reads every
                // slot). GetTexture silently wipes the material's ENTIRE shaderKeywords array as a side
                // effect (memory unity-material-getter-clears-keywords) — snapshot now, restore right
                // before the persist below. For augment, `owned` IS the caller's live in-scene material —
                // this protects its LIVE keyword state, not just what lands on disk.
                keywordsBeforeFork = owned.shaderKeywords;

                if (!createdO)
                {
                    // Augment mutates the caller's pre-existing material's slots in place (below, per
                    // successful forked row) — snapshot the requested slots' original refs so a LATER row's
                    // failure can roll them back too, not just the textures this run wrote. Snapshotting via
                    // GetTexture fires the keyword wipe, but keywordsBeforeFork above already covers that.
                    slotBackup = new List<(string, Texture, Vector2, Vector2)>();
                    foreach (var slot in requested)
                        slotBackup.Add((slot, owned.GetTexture(slot), owned.GetTextureScale(slot), owned.GetTextureOffset(slot)));
                }

                // ── Fork rule (Flow step 5), off O (byte-identical to S pre-fork on copy-to-new; the
                //    caller's already-owned material, possibly already forked, on in-place). ──
                var sourceToDst = new Dictionary<string, string>(StringComparer.Ordinal);
                var claimedDstToSource = new Dictionary<string, string>(StringComparer.Ordinal);
                var plan = BuildPlan(owned, textureHome, requested, data, sourceToDst, claimedDstToSource);
                if (data.offenders.Count > 0)
                {
                    FillSlotsAndCounts(data, plan); // self-legible slots[]/counts even on a plan-validation FAIL
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
                        if (!AssetDatabase.CopyAsset(row.sourcePath, row.ownedPath))
                        {
                            data.Offender("CopyAsset failed (texture): " + row.sourcePath + " -> " + row.ownedPath);
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

                // G48: enable streaming mip maps on every freshly-forked texture — our OWNED copy, never the
                // Vendor/ source (untouchable). Vendor textures routinely ship it OFF, which penalizes/blocks
                // VRChat upload; the fork is the one moment we own a copy we may fix. Only meaningful when the
                // texture actually has mip maps. A reused pre-existing copy (needsCopy=false) is left alone.
                int mipsEnabled = 0;
                foreach (var texPath in newlyWrittenTex)
                {
                    var ti = AssetImporter.GetAtPath(texPath) as TextureImporter;
                    if (ti == null || !ti.mipmapEnabled || ti.streamingMipmaps) continue;
                    ti.streamingMipmaps = true;
                    ti.SaveAndReimport();
                    mipsEnabled++;
                }
                // The note is deferred to the PASS point below: the save/reload and post-condition gates ahead
                // can still rollback() (deleting these very textures), so a note here would outlive its subject.

                // Re-assert the pre-fork keyword snapshot (see the note by its capture): every texture-slot
                // read/write since then may have silently wiped shaderKeywords.
                owned.shaderKeywords = keywordsBeforeFork;

                EditorUtility.SetDirty(owned);
                AssetDatabase.SaveAssets();

                // ── Disk-truthful post-condition (Flow step 6): reimport + reload, then rebuild slots[] and
                //    the counts from the RELOADED O — never from pre-write intent. ──
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
                // Past every rollback gate — the forked textures are here to stay, so the streaming-mipmap
                // note is honest now (G48).
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
                // Runs on every non-save exit (return or exception) and harmlessly-redundantly after the
                // success path's own pre-save restore — structurally covers early returns without a manual
                // repeat at each one (rollback spares the caller's material on the augment path, so without
                // this the live in-memory material would keep the getter-wiped keywords with no recovery).
                if (owned != null && keywordsBeforeFork != null) owned.shaderKeywords = keywordsBeforeFork;
            }
            return Finish(data, label);
        }

        /// <summary>One planned/observed outcome for a single shader texture slot — the unit <see cref="BuildPlan"/>
        /// produces and both the whatIf preview and the execute writer/rebuild consume.</summary>
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
        /// Classify every texture slot on <paramref name="src"/> (S pre-copy for whatIf/copy-to-new-preview;
        /// the freshly-copied O for copy-to-new execute — byte-identical at this point; the caller's
        /// already-owned material for augment) into exactly one final disposition, recording offenders
        /// (empty/built-in/non-Texture2D/sub-asset/collision on a REQUESTED slot) and notes (unrequested
        /// owned-elsewhere) onto <paramref name="data"/>. Requested, forkable slots get a planned destination
        /// via <see cref="PlanFork"/>, threading <paramref name="sourceToDst"/>/<paramref name="claimedDstToSource"/>
        /// so two slots sharing one source land on ONE planned copy and whatIf/execute agree on the fork
        /// count. Read-only — never touches disk beyond the read-only existence/content check in
        /// <see cref="PlanFork"/> (never mid-plan <c>File.Exists</c> for a same-run collision — the claimed
        /// map is the sole same-run authority; this run's first copy hasn't landed yet).
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

        /// <summary>Outcome of <see cref="PlanFork"/>: either a resolved destination (<paramref name="refused"/>
        /// false) or a refusal naming the occupying <c>dst</c> and the fix (<paramref name="refused"/> true;
        /// <see cref="dstPath"/>/<see cref="needsCopy"/> undefined).</summary>
        struct ForkResolution
        {
            public string dstPath;
            public bool needsCopy;
            public bool refused;
            public string refuseReason;
        }

        /// <summary>
        /// Resolve the destination for forking <paramref name="sourceTexPath"/> into <paramref name="textureHome"/>
        /// against the run-local claimed set, in the spec's fixed order — the SOLE collision authority in both
        /// whatIf and execute, so a collision refuses (or doesn't) identically in both:
        /// (a) this SAME source already claimed a <c>dst</c> this run (two slots, one source) → reuse it;
        /// (b) the <c>dst</c> leaf is already claimed by a DIFFERENT source this run → refuse (same-run
        ///     collision) — never <c>File.Exists</c> here, this run's first copy hasn't landed yet;
        /// (c) <c>dst</c> exists on disk from a PRIOR run (incremental augment) → byte-compare: equal ⇒ reuse
        ///     (don't re-copy), different ⇒ refuse (cross-run collision) — the single pairwise byte-compare
        ///     this tool ever does, not a general dedup pass;
        /// (d) else claim it (<c>dstLeaf → source</c>) and plan a copy.
        /// Read-only.
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

        /// <summary>Fill <c>data.slots</c> + the six <c>slotsX</c> counts straight from a whatIf plan (nothing
        /// was written, so there is no reload to rebuild from — the plan IS the preview).</summary>
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

        /// <summary>Accumulate one slot's final disposition into <c>data</c>'s running tally. Paired with
        /// <see cref="EmitDispositionCounts"/> (which flushes the tally into the six ordered counts) — the
        /// single tally/emit both the whatIf preview and the execute post-reload rebuild share.</summary>
        static void TallyDisposition(RunData data, string disposition)
        {
            data.tally.TryGetValue(disposition, out long n);
            data.tally[disposition] = n + 1;
        }

        /// <summary>Flush the disposition tally into the six <c>slotsX</c> counts, in the spec's fixed order
        /// (a disposition never seen this run emits 0). No <c>slotsBuiltin</c> key — a built-in/non-Texture2D
        /// untouched slot collapses into <c>empty</c>; a requested one is an <c>unforkable</c> offender.</summary>
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

        /// <summary>Segment-boundary containment: <paramref name="path"/> IS <paramref name="home"/> or is
        /// nested under it — never a bare string-prefix match (no false hit on a sibling stem, e.g. "Dress"
        /// vs "Dress_White").</summary>
        static bool UnderHome(string path, string home) =>
            !string.IsNullOrEmpty(path) && (path == home || path.StartsWith(home + "/", StringComparison.Ordinal));

        /// <summary>Byte-for-byte content equality (never GUID identity — a copied asset always mints a
        /// fresh GUID). Any read failure is treated as unequal (forces a refusal, never a silent overwrite).
        /// The single pairwise compare this tool ever does — at an actual on-disk cross-run collision.</summary>
        static bool BytesEqual(string pathA, string pathB)
        {
            byte[] a, b;
            try
            {
                if (new FileInfo(pathA).Length != new FileInfo(pathB).Length) return false; // cheap size gate
                a = File.ReadAllBytes(pathA); b = File.ReadAllBytes(pathB);
            }
            catch { return false; }
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>A material is locked iff its shader is one of Thry's generated <c>Hidden/Locked/…</c>
        /// shaders — shader-name ONLY. Real vendor poi materials carry a STALE non-empty
        /// <c>AllLockedGUIDS</c> tag while fully UNLOCKED (a normal <c>.poiyomi/…</c> shader, live-confirmed
        /// on the AvatarProject corpus — Thry's own <c>IsMaterialLocked</c> returns false there too), so the
        /// tag is never a lock signal; only the shader name counts. Gates both the in-place augment guard
        /// (an owned material must never be locked before this tool touches it) and the copy-to-new unlock
        /// seam below.</summary>
        static bool IsLocked(Material m) =>
            m.shader != null && m.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal);

        static string ThryUnresolvedMessage(string reason) =>
            "locked poi material but Thry ShaderOptimizer not found — is com.poiyomi.toon installed? (" + reason + ")";

        static string UnresolvableOriginalShaderMessage(string tag) =>
            "locked poi material's original-shader tag ('" + tag + "') does not resolve to a shader — cannot safely unlock";

        /// <summary>
        /// Does <paramref name="m"/>'s saved original-shader tag resolve to a REAL shader? Mirrors Thry's
        /// own two-step fallback (the same order <c>UnlockConcrete</c> uses before it pops its blocking
        /// <c>DisplayDialog</c>): the <c>OriginalShaderGUID</c> tag → <c>GUIDToAssetPath</c> →
        /// <c>LoadAssetAtPath&lt;Shader&gt;</c>, else the <c>OriginalShader</c> tag (a shader NAME) →
        /// <c>Shader.Find</c>. Presence alone is not enough — a stale GUID (poi reinstall/version bump) or a
        /// renamed shader leaves the tag present but unresolvable, which is exactly what makes Thry pop the
        /// modal. <paramref name="tag"/> returns whichever tag value was examined (for the refusal message).
        /// </summary>
        static bool OriginalShaderResolves(Material m, out string tag)
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

        /// <summary>
        /// Reflection-resolve <c>Thry.ThryEditor.ShaderOptimizer.UnlockMaterials(IEnumerable&lt;Material&gt;,
        /// ProgressBar)</c> without avatar-tools referencing <c>com.poiyomi.toon</c>: scan every loaded
        /// assembly (mirrors <see cref="TransplantCore.ResolveTypes"/>'s assembly scan) for the type by
        /// full name, then find the public static <c>UnlockMaterials</c> overload whose second parameter is
        /// an enum (Thry's <c>ProgressBar</c>) and whose first accepts a <c>Material[]</c>. Never throws —
        /// any miss is folded into <paramref name="reason"/> and the method returns false.
        /// </summary>
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

        /// <summary>
        /// Unlock <paramref name="o"/> only, via <see cref="TryResolvePoiUnlock"/> + reflection invoke with
        /// the enum's <c>None</c> (=0) member (no progress bar, no cancel). Any reflection miss or thrown
        /// inner exception (<c>TargetInvocationException</c> unwrapped) becomes <c>(false, reason)</c> —
        /// this method never throws out of <see cref="Run"/>.
        /// </summary>
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

        // ── Normalize: variant flatten (Flow step 4) ────────────────────────────────────────────────────

        /// <summary>
        /// Bake a Material Variant into a standalone material: the resolved shader-keyword set,
        /// <c>renderQueue</c>, and every override tag (walking the WHOLE parent chain for tag names, since
        /// a tag never overridden on <paramref name="o"/> itself lives only in an ancestor's own
        /// <c>stringTagMap</c>) are captured FIRST, before touching any shader property value — the first
        /// GetColor/GetFloat/GetVector/GetInt/GetTexture call on a Material instance silently clears its
        /// ENTIRE <c>shaderKeywords</c> array as a side effect (a Unity engine quirk confirmed empirically,
        /// independent of variants, shaders, or this tool); reading keywords/queue/tags before ever touching
        /// a property sidesteps it. Every shader property (float/range/color/vector/int/texture +
        /// tiling/offset) is read off <paramref name="o"/> next — still resolving through its parent chain —
        /// then <c>o.parent</c> is severed and everything is re-applied as <paramref name="o"/>'s own
        /// explicit values. Un-timed: caller still owns <c>SetDirty</c>/<c>SaveAssets</c>/reimport.
        /// </summary>
        static void FlattenVariant(Material o)
        {
            var shader = o.shader;

            // Resolved-enabled keyword set: UNION of two sources, both read BEFORE any shader-property
            // Get/Set below — the first GetColor/GetFloat/GetVector/GetInt/GetTexture call on a Material
            // instance silently clears its ENTIRE shaderKeywords array as a side effect (memory
            // unity-material-getter-clears-keywords; renderQueue, parent, the array getter itself, and
            // IsKeywordEnabled itself do not trigger it), so keywords must be read first or lost outright.
            //   - o.shaderKeywords (the array getter, as ForkSlotsAndPersist takes for a non-variant O):
            //     LOCAL overrides only — but that includes keywords enabled via EnableKeyword that are NOT
            //     in the shader's declared keyword space (common with Poiyomi/lilToon feature toggles),
            //     which the probe below misses entirely.
            //   - a per-name IsKeywordEnabled probe over shader.keywordSpace.keywordNames: resolves the
            //     full effective set through the (about-to-be-severed) parent chain, so it also catches a
            //     keyword inherited-but-never-locally-overridden — but only among shader-DECLARED names.
            // Neither alone is complete; their union is. Residual gap: a keyword that is purely inherited
            // from the parent AND not shader-declared is recoverable by neither method — rare (declared
            // keywords are the normal case for inheritance) and strictly narrower than the old probe-only
            // gap this fixes.
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
        /// Every override-tag NAME ever set anywhere up <paramref name="o"/>'s parent chain (a tag never
        /// overridden on <paramref name="o"/> itself lives only in an ancestor's own serialized
        /// <c>stringTagMap</c> — walking the chain is the only way to find its name; there is no public
        /// enumeration API), each resolved to <paramref name="o"/>'s own effective value via
        /// <c>GetTag(name, false, "")</c> (Thry's own <c>DeleteTags</c> uses this exact
        /// <c>SerializedObject</c>/<c>stringTagMap</c> walk to enumerate tag names).
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

        /// <summary>Route an argument-guard failure through the house RunLog grammar (summary + RunLog +
        /// LogError), like the sibling transplant tools — never a bare trailer-less line.</summary>
        static string ArgFail(string label, string msg)
        {
            var data = new RunData { result = "FAIL", error = msg };
            data.Offender(msg);
            return Finish(data, label);
        }

        /// <summary>Fail an in-flight <see cref="RunData"/> (routing/write-guard stage — after arg/shader
        /// guards, so <c>instance</c>/<c>source</c> are already populated) through the same grammar.</summary>
        static string Fail(RunData data, string label, string msg)
        {
            data.result = "FAIL";
            data.error = msg;
            data.Offender(msg);
            return Finish(data, label);
        }

        /// <summary>Attach the bespoke <c>slots[]</c> section, then the shared envelope tail
        /// (<see cref="TransplantCore.Finish"/>: RunLog + one-line summary + severity log; it also
        /// backfills an offender on an offenderless FAIL). Every OwnMaterial exit funnels through here,
        /// so every RunLog carries a <c>slots</c> key — empty on a pre-plan FAIL.</summary>
        static string Finish(RunData data, string label)
        {
            data.Section("slots", RenderSlots(data.slots));
            return TransplantCore.Finish(data, label);
        }

        /// <summary>Render the per-slot provenance table as a JSON array (the RunLog's <c>slots</c>
        /// section) — see <see cref="SlotRow"/> for the disposition vocabulary.</summary>
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

        /// <summary>One row of the per-slot provenance table (<see cref="RunData.slots"/>). <c>ownedPath</c>
        /// is the owned texture's path for <c>forked</c>/<c>already-owned</c> slots; null otherwise. The six
        /// dispositions: <c>forked</c> / <c>already-owned</c> / <c>unforkable</c> (requested slots only) and
        /// <c>vendor-ref</c> / <c>owned-elsewhere</c> / <c>empty</c> (untouched slots) — see
        /// <see cref="OwnMaterial.BuildPlan"/>.</summary>
        struct SlotRow
        {
            public string slot;
            public bool requested;
            public string disposition;
            public string sourcePath;
            public string ownedPath;
        }

        /// <summary>The shared envelope accumulator plus this tool's bespoke rows: the <see cref="slots"/>
        /// provenance table (rendered into the RunLog's <c>slots</c> section by
        /// <see cref="OwnMaterial.Finish"/>) and its running disposition <see cref="tally"/>
        /// (<see cref="OwnMaterial.TallyDisposition"/>, flushed into the six ordered <c>slotsX</c> counts
        /// by <see cref="OwnMaterial.EmitDispositionCounts"/>).</summary>
        sealed class RunData : RunLog
        {
            public RunData() : base("own-material") { }
            public readonly List<SlotRow> slots = new List<SlotRow>();
            public readonly Dictionary<string, long> tally = new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }
}
