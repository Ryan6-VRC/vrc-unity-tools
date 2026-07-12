using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <para>The <paramref name="force"/> divergence guard (refuse to clobber an out-of-band-edited clip) is
    /// a LATER task — for now <c>force</c> is accepted but the in-place write always overwrites.</para>
    /// </summary>
    [AgentTool]
    public static class CompileClips
    {
        /// <summary>Compile the clips-file YAML at <paramref name="sourcePath"/> (a filesystem path) into
        /// external <c>.anim</c> assets under <paramref name="outDir"/> (an <c>Assets/…</c>-relative folder).
        /// With <paramref name="whatIf"/> nothing is written — the summary still carries every would-be ref
        /// path. <paramref name="force"/> = override safety guards; in Task 2 it overrides ONLY the
        /// outDir-writability guard (write owned copies into read-only Assets/Vendor or Packages territory);
        /// a later task widens it to also override the divergence-refuse. Returns the one-line PASS/FAIL
        /// summary with the RunLog path folded on (<c>… => RESULT | log=&lt;path&gt;</c>).</summary>
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
                var byFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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
                var built = new Dictionary<string, AnimationClip>();
                foreach (var spec in doc.Clips)
                {
                    try { built[spec.Name] = ControllerEmit.BuildClipContent(spec, paramNames); }
                    catch (ControllerEmit.EmitException ee)
                    {
                        log.Offender("clip '" + spec.Name + "': " + ee.Message);
                        return Fail(log, label, "clip build failed (batch-atomic; nothing written)");
                    }
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
                    }
                    else if (existing != null)
                    {
                        // In-place rewrite: keep the asset (and its GUID), clear residual, copy the built
                        // content. NEVER DeleteAsset+CreateAsset or CreateAsset onto an occupied path — both
                        // mint a new GUID and break external references to the clip.
                        ClearAndCopy(existing, b);
                        UnityEngine.Object.DestroyImmediate(b); // detached build source, no longer needed
                        reused++;
                    }
                    else
                    {
                        // Absent path → the built clip BECOMES the new asset (visible main asset; no hideFlags).
                        AssetDatabase.CreateAsset(b, path);
                        created++;
                    }
                    log.Note(path);
                }

                if (!whatIf) AssetDatabase.SaveAssets();

                log.Count("emitted", doc.Clips.Count);
                log.Count("created", created);
                log.Count("reused", reused);
                log.result = "PASS";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error = ex.GetType().Name + ": " + ex.Message;
            }

            return TransplantCore.Finish(log, label);
        }

        // Replace `target`'s content with `built`'s, clearing residual curves/object-refs/events FIRST so a
        // recompile of a mutated source leaves no stale binding behind. Editing IN PLACE (not a new asset) is
        // what makes the write GUID-stable. Verbatim from the Task-2 design sketch.
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
