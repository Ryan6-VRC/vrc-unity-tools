using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The external-clip-write DOOR: compile a clips-file <c>.yaml</c> source into standalone, VISIBLE
    /// <c>.anim</c> assets under <paramref name="outDir"/> — one per declared clip. A clips file is the
    /// same animator-schema surface as a controller (<c>schema: 1</c>, <c>basis: avatar-root</c>) but
    /// carries <c>clips:</c> and NO <c>layers:</c>; its optional <c>parameters:</c> supply the AAP-binding
    /// name set that routes a bare binding to an Animator-property curve.
    ///
    /// <para>Pipeline: parse (<see cref="AnimatorSchemaYaml"/>) → validate (<see cref="SchemaValidation"/>)
    /// + two clips-only guards → BUILD EVERY clip in memory (<see cref="ControllerEmit.BuildClipContent"/>)
    /// → write each GUID-stable in place → RunLog. Result carried on the one-line summary via the
    /// <see cref="TransplantCore.Finish"/> grammar (like <see cref="OwnControllerClips"/>, since this is a
    /// mutating WRITER), each emitted clip's ref path folded into <c>notes=[…]</c>.</para>
    ///
    /// <para><b>BATCH-ATOMIC.</b> Every clip is built in memory FIRST; a single bad binding
    /// (<see cref="ControllerEmit.EmitException"/>) fails the whole run before ANY file is written — never a
    /// partial emit. <b>GUID-STABLE.</b> An existing <c>.anim</c> at the target path is loaded and rewritten
    /// IN PLACE (residual curves/refs/events cleared first), never DeleteAsset+CreateAsset — so its GUID, and
    /// any external reference to it, survives a recompile. <b>EMIT-ONLY.</b> <paramref name="outDir"/> is
    /// never enumerated for pruning: a clip absent from the source is left untouched. External clips are
    /// VISIBLE main assets — never <see cref="HideFlags.HideInHierarchy"/> (that flag is for the
    /// controller-embedded sub-asset path only).</para>
    ///
    /// <para><b>DIVERGENCE GUARD.</b> Before writing, each existing target <c>.anim</c> is compared to the
    /// content stamp WE last wrote for it. A clip whose on-disk content diverges from its stamp — or
    /// carries no stamp at all — was touched OUT OF BAND (a human hand-edit, or a pre-existing hand-authored
    /// <c>.anim</c> we never emitted). Any divergence REFUSES the whole run writing nothing (batch-atomic),
    /// naming each offender — unless <paramref name="force"/>, which overwrites + re-stamps and notes each
    /// clobber. This compares disk-to-stamp, NOT to the YAML: recompiling our own untouched output (identical
    /// OR changed source) rehashes to the stamp and is never diverged, so only a human edit trips the guard.</para>
    /// </summary>
    [AgentTool]
    public static class CompileClips
    {
        /// <summary>Compile the clips-file YAML at <paramref name="sourcePath"/> (a filesystem path) into
        /// external <c>.anim</c> assets under <paramref name="outDir"/> (an <c>Assets/…</c>-relative folder).
        /// With <paramref name="whatIf"/> nothing is written — the summary still carries every would-be ref
        /// path. <paramref name="force"/> = override safety guards: it overrides BOTH the outDir-writability
        /// guard (write owned copies into read-only Assets/Vendor or Packages territory) AND the divergence
        /// guard (overwrite a hand-edited / unstamped existing <c>.anim</c> instead of refusing). Returns the
        /// one-line PASS/FAIL summary with the RunLog path folded on (<c>… => RESULT | log=&lt;path&gt;</c>).</summary>
        public static string Compile(string sourcePath, string outDir, bool force = false, bool whatIf = false)
        {
            var log = new TransplantRunLog("compile-clips") { whatIf = whatIf, source = sourcePath };
            string label = "clips";

            try
            {
                // ── Read + parse the source (clips file = schema:1 + basis:avatar-root + clips:, no layers:) ──
                if (string.IsNullOrEmpty(sourcePath)) return Fail(log, label, "sourcePath is empty");
                if (!File.Exists(sourcePath)) return Fail(log, label, "source file not found: " + sourcePath);
                if (string.IsNullOrEmpty(outDir)) return Fail(log, label, "outDir is null or empty");

                string text = File.ReadAllText(sourcePath);

                AnimDocument doc;
                try { doc = AnimatorSchemaYaml.Parse(text, sourcePath); }
                catch (SchemaException se) { return Fail(log, label, "parse: " + se.Message); }

                label = TransplantCore.Sanitize(doc.ControllerName ?? "clips");

                // ── Document validation. On a clips-only doc this checks schema-version + reserved-param and
                //    SKIPS every layer/state rule (its layer loop sees zero layers) — a non-empty list is a
                //    real defect. ──
                var vErrors = SchemaValidation.Validate(doc);
                if (vErrors.Count > 0)
                {
                    foreach (var e in vErrors) log.Offender(e);
                    return Fail(log, label, "validation failed (" + vErrors.Count + ")");
                }

                // ── Two clips-only guards. A doc with layers is a CONTROLLER (route to CompileController); a
                //    doc with no clips has nothing to emit. Both are fail-loud, named. ──
                if (doc.Layers.Count > 0)
                    return Fail(log, label, "layers present — this is a controller; use CompileController");
                if (doc.Clips.Count == 0)
                    return Fail(log, label, "no clips to emit");

                string outClean = outDir.Replace('\\', '/').TrimEnd('/');

                // ── outDir ownership guard: never write owned copies into read-only territory (Assets/Vendor
                //    or Packages) — the "never alter vendor assets" invariant. force overrides (with a loud
                //    note). Mirrors OwnControllerClips. ──
                if (!TransplantCore.IsWritableAsset(outClean))
                {
                    if (force) log.Note("read-only outDir override (force): " + outClean);
                    else
                    {
                        log.Offender("outDir '" + outClean +
                            "' is read-only (under Assets/Vendor or Packages): choose an owned output folder, or pass force=true");
                        return Fail(log, label, "read-only outDir (pass force=true to override)");
                    }
                }

                // ── Sanitized-filename collision guard (fail-loud, rule 7). Two DISTINCT authored clip names
                //    can sanitize to one filename (e.g. "Wave Left" and "Wave_Left" → "Wave_Left.anim"); the
                //    later write would silently clobber the earlier clip's file under a green PASS. These are
                //    AUTHORED names, so a collision is an authoring error to surface — FAIL naming the colliding
                //    names + shared filename, writing NOTHING (consistent with batch-atomicity). We deliberately
                //    do NOT guid-suffix like OwnControllerClips (that tool dedups vendor churn; here the intent
                //    is legible authored filenames). ──
                // OrdinalIgnoreCase, not Ordinal: the platform filesystem (VRChat-pinned Windows/NTFS) is
                // case-insensitive, so "Wave" and "wave" occupy ONE file. Under Ordinal they'd hash to distinct
                // keys, the guard would pass, and the write loop's case-insensitive LoadAssetAtPath would resolve
                // the second to the first and silently clobber it — the exact failure this guard prevents.
                var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var spec in doc.Clips)
                {
                    string fname = TransplantCore.Sanitize(spec.Name) + ".anim";
                    if (!byFile.TryGetValue(fname, out var names)) byFile[fname] = names = new List<string>();
                    names.Add(spec.Name);
                }
                foreach (var kv in byFile)
                    if (kv.Value.Count > 1)
                        log.Offender("clip names {" + string.Join(", ", kv.Value) +
                            "} all sanitize to one filename '" + kv.Key + "'");
                if (log.offenders.Count > 0)
                    return Fail(log, label, "sanitized-filename collision (nothing written)");

                // The declared Animator-parameter names: the gate BuildClipContent uses to route a bare
                // binding to an Animator-property curve (AAP) rather than a scene binding.
                var paramNames = new HashSet<string>(doc.Parameters.Select(p => p.Name));

                // ── BUILD-ALL-IN-MEMORY FIRST (batch atomicity). A bad binding on ANY clip fails the whole
                //    run here, before a single file is written — never a partial external-clip set on disk. ──
                // built[...] holds detached, unmanaged AnimationClip instances. Each is adopted (CreateAsset) or
                // destroyed (in-place reuse / whatIf) in the write loop below, and its slot nulled the instant it
                // is — so the finally frees ONLY the leftovers an early refusal (bad binding, collision, divergence,
                // read-only) or a whatIf would otherwise strand. Adopted clips (which BECAME the asset) are nulled,
                // never destroyed.
                var built = new Dictionary<string, AnimationClip>();
                try
                {
                foreach (var spec in doc.Clips)
                {
                    try { built[spec.Name] = ControllerEmit.BuildClipContent(spec, paramNames); }
                    catch (ControllerEmit.EmitException ee)
                    {
                        log.Offender("clip '" + spec.Name + "': " + ee.Message);
                        return Fail(log, label, "clip build failed (batch-atomic; nothing written)");
                    }
                }

                // ── Divergence guard (force-gated), scanned as its OWN pass BEFORE any write so a refusal
                //    leaves every .anim untouched (batch-atomic, like the collision/bad-binding guards). For
                //    each already-existing target, `diverged` iff its on-disk content differs from the stamp WE
                //    last wrote for it (ReadContentStamp == null → hand-authored/never emitted here; or hash !=
                //    stamp → edited out of band since our last emit). This compares DISK to our own stamp, not to
                //    the YAML: the stamp records the disk hash, so recompiling our own untouched output — identical
                //    OR changed source — rehashes to the stamp and is NOT diverged (overwrites silently). Only a
                //    human hand-edit or an unstamped pre-existing .anim trips this. force overrides: overwrite +
                //    re-stamp (the reload-stamp loop below) + a loud per-clobber note. ──
                var diverged = new List<string>(); // authored names whose on-disk .anim diverges from our stamp
                foreach (var spec in doc.Clips)
                {
                    string path = outClean + "/" + TransplantCore.Sanitize(spec.Name) + ".anim";
                    var onDisk = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (onDisk == null) continue; // absent target — nothing to clobber
                    string stamp = ReadContentStamp(path);
                    if (stamp == null || HashClipContent(onDisk) != stamp) diverged.Add(spec.Name);
                }
                if (diverged.Count > 0)
                {
                    if (!force)
                    {
                        foreach (var name in diverged)
                            log.Offender("clip '" + name + "' at " + outClean + "/" + TransplantCore.Sanitize(name) +
                                ".anim looks hand-edited (on-disk content diverges from its last-emit stamp): pass " +
                                "force=true to overwrite it, or promote it by removing it from the clips file");
                        return Fail(log, label, "refusing to clobber " + diverged.Count + " diverged clip(s)");
                    }
                    foreach (var name in diverged)
                        log.Note("clobbered diverged clip '" + name + "' (force)");
                }

                if (!whatIf) EnsureFolderExists(outClean);

                // ── Write each clip GUID-stable in place (or, in whatIf, just record the intended path). ──
                int created = 0, reused = 0;
                foreach (var spec in doc.Clips)
                {
                    string path = outClean + "/" + TransplantCore.Sanitize(spec.Name) + ".anim";
                    var b = built[spec.Name];
                    var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

                    if (whatIf)
                    {
                        if (existing != null) reused++; else created++;
                        UnityEngine.Object.DestroyImmediate(b); // nothing written — free the build source
                        built[spec.Name] = null;
                    }
                    else if (existing != null)
                    {
                        // In-place rewrite: keep the asset (and its GUID), clear residual, copy the built
                        // content. NEVER DeleteAsset+CreateAsset or CreateAsset onto an occupied path — both
                        // mint a new GUID and break external references to the clip.
                        ClearAndCopy(existing, b);
                        UnityEngine.Object.DestroyImmediate(b); // detached build source, no longer needed
                        built[spec.Name] = null;
                        reused++;
                    }
                    else
                    {
                        // Absent path → the built clip BECOMES the new asset (visible main asset; no hideFlags).
                        AssetDatabase.CreateAsset(b, path);
                        built[spec.Name] = null; // adopted as the asset — must NOT be destroyed by the finally
                        created++;
                    }
                    log.Note(path);
                }

                if (!whatIf)
                {
                    AssetDatabase.SaveAssets();

                    // ── Content-provenance stamp. Reload each emitted clip FROM DISK and stamp the hash of THAT
                    //    on-disk clip — never the in-memory `built` clip. Float/tangent serialization can drift
                    //    subtly across the in-memory→.anim round-trip, so stamping the disk hash guarantees the
                    //    stamp equals what the divergence guard recomputes from the .anim; an unmodified
                    //    clip can never read as diverged. Nothing to stamp under whatIf (nothing was written). ──
                    foreach (var spec in doc.Clips)
                    {
                        string path = outClean + "/" + TransplantCore.Sanitize(spec.Name) + ".anim";
                        var onDisk = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                        if (onDisk != null) StampContent(path, HashClipContent(onDisk));
                    }
                }

                log.Count("emitted", doc.Clips.Count);
                log.Count("created", created);
                log.Count("reused", reused);
                log.result = "PASS";
                }
                finally
                {
                    // Free any built clip that never became an asset — every adopted/destroyed slot was nulled
                    // above, so this frees only what an early refusal or whatIf stranded (no double-free, and
                    // adopted assets are untouched).
                    foreach (var kv in built)
                        if (kv.Value != null) UnityEngine.Object.DestroyImmediate(kv.Value);
                }
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error = ex.GetType().Name + ": " + ex.Message;
            }

            return TransplantCore.Finish(log, label);
        }

        // ── Content-hash provenance ────────────────────────────────────────────────────────────────────
        //
        // A deterministic, order-INDEPENDENT hash over an emitted clip's CONTENT (curves, object refs, events,
        // settings). The divergence guard recomputes this from disk and refuses to clobber a clip whose content
        // diverges from its stamp; the stamp pass only writes + reads it back. Reuses ControllerEmit.SourceHash so the hash width /
        // format matches the sibling srchash provenance.

        /// <summary>Deterministic content hash of <paramref name="clip"/> — order-independent (binding lists
        /// sorted), so two clips with the same content hash equal regardless of authoring/enumeration order.
        /// Covers frameRate/wrapMode/legacy, every float curve (keys with time/value/in-out-tangent/in-out-weight/
        /// weightedMode in round-trip "R" + pre/post wrap), every object-reference curve (key times + target
        /// instance identity), animation events (incl. messageOptions), and the clip settings. Hashes only
        /// human-editable, round-trip-STABLE fields — deliberately NOT clip.localBounds (auto-derived from the
        /// curves; Unity may recompute it, which would manufacture false divergence).</summary>
        public static string HashClipContent(AnimationClip clip)
        {
            if (clip == null) return ControllerEmit.SourceHash("");

            var sb = new StringBuilder();
            sb.Append("frameRate=").Append(R(clip.frameRate))
              .Append(";wrapMode=").Append(((int)clip.wrapMode).ToString(CultureInfo.InvariantCulture))
              .Append(";legacy=").Append(clip.legacy).Append('\n');

            // Float curves — sorted by path+type+propertyName (order-independent).
            foreach (var b in AnimationUtility.GetCurveBindings(clip)
                         .OrderBy(x => x.path, StringComparer.Ordinal)
                         .ThenBy(x => x.type == null ? "" : x.type.FullName, StringComparer.Ordinal)
                         .ThenBy(x => x.propertyName, StringComparer.Ordinal))
            {
                sb.Append("F ").Append(b.path).Append('|')
                  .Append(b.type == null ? "" : b.type.FullName).Append('|')
                  .Append(b.propertyName).Append('=');
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                if (curve != null)
                {
                    foreach (var k in curve.keys)
                        sb.Append(R(k.time)).Append(':').Append(R(k.value)).Append('/')
                          .Append(R(k.inTangent)).Append('/').Append(R(k.outTangent)).Append('/')
                          .Append(R(k.inWeight)).Append('/').Append(R(k.outWeight)).Append('/')
                          .Append(((int)k.weightedMode).ToString(CultureInfo.InvariantCulture)).Append(';');
                    // Per-curve wrap: human-editable, serialized as m_Pre/PostInfinity, stable disk↔disk.
                    sb.Append('~').Append(((int)curve.preWrapMode).ToString(CultureInfo.InvariantCulture))
                      .Append('/').Append(((int)curve.postWrapMode).ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }

            // Object-reference (PPtr) curves — same sort; key times + STABLE target identity (RefId). Not
            // exercised by CompileClips' own output: the clips: grammar produces only float curves via
            // BuildClipContent. This branch exists so a hand-edited .anim carrying a PPtr curve hashes stably
            // for the divergence guard's check.
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip)
                         .OrderBy(x => x.path, StringComparer.Ordinal)
                         .ThenBy(x => x.type == null ? "" : x.type.FullName, StringComparer.Ordinal)
                         .ThenBy(x => x.propertyName, StringComparer.Ordinal))
            {
                sb.Append("O ").Append(b.path).Append('|')
                  .Append(b.type == null ? "" : b.type.FullName).Append('|')
                  .Append(b.propertyName).Append('=');
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                if (keys != null)
                    foreach (var k in keys)
                        sb.Append(R(k.time)).Append(':').Append(RefId(k.value)).Append(';');
                sb.Append('\n');
            }

            // Animation events — order-preserving (event order is itself content).
            foreach (var e in AnimationUtility.GetAnimationEvents(clip))
                sb.Append("E ").Append(R(e.time)).Append('|').Append(e.functionName).Append('|')
                  .Append(e.stringParameter).Append('|').Append(R(e.floatParameter)).Append('|')
                  .Append(e.intParameter.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(RefId(e.objectReferenceParameter)).Append('|')
                  .Append(((int)e.messageOptions).ToString(CultureInfo.InvariantCulture))
                  .Append('\n');

            // Clip settings (loopTime etc.).
            var s = AnimationUtility.GetAnimationClipSettings(clip);
            sb.Append("S loopTime=").Append(s.loopTime)
              .Append(";loopBlend=").Append(s.loopBlend)
              .Append(";cycleOffset=").Append(R(s.cycleOffset))
              .Append(";startTime=").Append(R(s.startTime))
              .Append(";stopTime=").Append(R(s.stopTime))
              .Append(";mirror=").Append(s.mirror)
              .Append(";level=").Append(R(s.level))
              .Append(";orientationOffsetY=").Append(R(s.orientationOffsetY))
              .Append(";heightFromFeet=").Append(s.heightFromFeet)
              .Append(";keepOriginalOrientation=").Append(s.keepOriginalOrientation)
              .Append(";keepOriginalPositionY=").Append(s.keepOriginalPositionY)
              .Append(";keepOriginalPositionXZ=").Append(s.keepOriginalPositionXZ)
              .Append(";loopBlendOrientation=").Append(s.loopBlendOrientation)
              .Append(";loopBlendPositionY=").Append(s.loopBlendPositionY)
              .Append(";loopBlendPositionXZ=").Append(s.loopBlendPositionXZ)
              .Append(";hasAdditiveReferencePose=").Append(s.hasAdditiveReferencePose)
              .Append(";additiveReferencePoseTime=").Append(R(s.additiveReferencePoseTime))
              .Append('\n');

            return ControllerEmit.SourceHash(sb.ToString());
        }

        // Round-trip float formatting — "R" so serialization is lossless and deterministic.
        static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        // Stable identity for a PPtr target: asset GUID+localFileId (survives domain reload / reimport / restart)
        // rather than a session-local GetInstanceID (the only otherwise non-deterministic term in the hash).
        // Fallback to iid for a scene/non-asset object with no GUID.
        static string RefId(UnityEngine.Object o)
        {
            if (o == null) return "null";
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out var guid, out long lfid)
                ? guid + ":" + lfid.ToString(CultureInfo.InvariantCulture)
                : "iid:" + o.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Stamp <paramref name="hash"/> into the <c>.anim</c>'s importer userData (the same channel as
        /// the controller srchash). Reimports so the stamp persists. No-op on a null importer.</summary>
        public static void StampContent(string animPath, string hash)
        {
            var imp = AssetImporter.GetAtPath(animPath);
            if (imp == null) return;
            imp.userData = "clipcontent:" + hash;
            imp.SaveAndReimport();
        }

        /// <summary>Read back the content stamp from the <c>.anim</c>'s importer userData; null when absent
        /// (unstamped, hand-authored, or a missing importer).</summary>
        public static string ReadContentStamp(string animPath)
        {
            var imp = AssetImporter.GetAtPath(animPath);
            return imp == null ? null : ExtractField(imp.userData, "clipcontent:");
        }

        // Extract a ";"-delimited "<key><value>" field from userData (mirrors CompileController.ExtractField).
        static string ExtractField(string userData, string key)
        {
            if (string.IsNullOrEmpty(userData)) return null;
            int i = userData.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int end = userData.IndexOf(';', i);
            return end < 0 ? userData.Substring(i) : userData.Substring(i, end - i);
        }

        // Replace `target`'s content with `built`'s, clearing residual curves/object-refs/events FIRST so a
        // recompile of a mutated source leaves no stale binding behind. Editing IN PLACE (not a new asset) is
        // what makes the write GUID-stable.
        static void ClearAndCopy(AnimationClip target, AnimationClip built)
        {
            target.ClearCurves();
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(target))
                AnimationUtility.SetObjectReferenceCurve(target, b, (ObjectReferenceKeyframe[])null);
            AnimationUtility.SetAnimationEvents(target, new AnimationEvent[0]);
            foreach (var b in AnimationUtility.GetCurveBindings(built))
                AnimationUtility.SetEditorCurve(target, b, AnimationUtility.GetEditorCurve(built, b));
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(built))
                AnimationUtility.SetObjectReferenceCurve(target, b, AnimationUtility.GetObjectReferenceCurve(built, b));
            AnimationUtility.SetAnimationEvents(target, AnimationUtility.GetAnimationEvents(built));
            AnimationUtility.SetAnimationClipSettings(target, AnimationUtility.GetAnimationClipSettings(built));
            target.frameRate = built.frameRate; // else a reused .anim keeps a stale rate (BuildClipContent sets 60)
            EditorUtility.SetDirty(target);
        }

        // Recursively create an Assets/-relative folder chain (mirrors OwnControllerClips.EnsureFolderExists).
        static void EnsureFolderExists(string assetPath)
        {
            assetPath = assetPath.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            int slash = assetPath.LastIndexOf('/');
            if (slash < 0) return;
            string parent = assetPath.Substring(0, slash);
            string leaf = assetPath.Substring(slash + 1);
            EnsureFolderExists(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // Route a FAIL through the house RunLog grammar (summary + RunLog + LogError). Sets result/error and
        // returns Finish (which backfills an offender from `error` when none was named).
        static string Fail(TransplantRunLog log, string label, string msg)
        {
            log.result = "FAIL";
            log.error = msg;
            return TransplantCore.Finish(log, label);
        }
    }
}
