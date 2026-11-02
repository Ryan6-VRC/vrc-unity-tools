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
    /// Compiles a clips-file <c>.yaml</c> into standalone, visible <c>.anim</c> assets under <c>outDir</c>, one per
    /// declared clip. A clips file is the animator-schema surface (<c>schema: 1</c>, <c>basis: avatar-root</c>) with
    /// <c>clips:</c> and no <c>layers:</c>; its optional <c>parameters:</c> supply the names that route a bare
    /// binding to an Animator-property curve. The summary follows <see cref="TransplantCore.Finish"/>, with each
    /// emitted clip's ref path in <c>notes=[…]</c>.
    ///
    /// <para><b>Batch-atomic.</b> Every clip is built in memory and every guard runs before the first write, so a
    /// refusal writes no <c>.anim</c>. <b>GUID-stable.</b> An existing <c>.anim</c> is cleared and rewritten in place,
    /// never DeleteAsset+CreateAsset or CreateAsset onto the occupied path: both mint a new GUID and break external
    /// references. <b>Emit-only.</b> <c>outDir</c> is never pruned: a clip absent from the source is left untouched.</para>
    ///
    /// <para><b>Divergence guard.</b> Each existing target is compared to the content stamp last written for it, not
    /// to the YAML, so recompiling untouched output from identical or changed source never trips it. A clip whose
    /// on-disk content diverges from its stamp, or that carries none, was touched out of band: the whole run refuses,
    /// naming each offender, unless <c>force</c>, which overwrites, re-stamps and notes each clobber. Because the guard
    /// trusts the stamp, a clip written but not stamped FAILs this run naming it, rather than reading as hand-edited
    /// on the next one.</para>
    /// </summary>
    [AgentTool]
    public static class CompileClips
    {
        /// <summary>Compile the clips-file YAML at <paramref name="sourcePath"/> (a filesystem path) into external
        /// <c>.anim</c> assets under <paramref name="outDir"/> (an <c>Assets/…</c>-relative folder). With
        /// <paramref name="whatIf"/> nothing is written, and the summary still carries every would-be ref path.
        /// <paramref name="force"/> overrides both the read-only-outDir guard (Assets/Vendor, Packages) and the divergence
        /// guard. Returns the one-line summary with the RunLog path folded on (<c>… => RESULT | log=&lt;path&gt;</c>).</summary>
        // `whatIf` is third on both compile doors: a third positional meaning `force` here and `whatIf` on
        // CompileController.Run would turn a preview into an overwrite that also defeats the read-only-outDir guard.
        public static string Run(string sourcePath, string outDir, bool whatIf = false, bool force = false)
        {
            var log = new RunLog("compile-clips") { whatIf = whatIf, source = sourcePath };
            string label = "clips";

            try
            {
                if (string.IsNullOrEmpty(sourcePath)) return Fail(log, label, "sourcePath is empty");
                if (!File.Exists(sourcePath)) return Fail(log, label, "source file not found: " + sourcePath);
                if (string.IsNullOrEmpty(outDir)) return Fail(log, label, "outDir is null or empty");

                string text = File.ReadAllText(sourcePath);

                AnimDocument doc;
                try { doc = AnimatorSchemaYaml.Parse(text, sourcePath); }
                catch (SchemaException se) { return Fail(log, label, "parse: " + se.Message); }

                label = TransplantCore.Sanitize(doc.ControllerName ?? "clips");

                var vErrors = SchemaValidation.Validate(doc);
                if (vErrors.Count > 0)
                {
                    foreach (var e in vErrors) log.Offender(e);
                    return Fail(log, label, "validation failed (" + vErrors.Count + ")");
                }

                if (doc.Layers.Count > 0)
                    return Fail(log, label, "layers present — this is a controller; use CompileController");
                if (doc.Clips.Count == 0)
                    return Fail(log, label, "no clips to emit");

                string outClean = outDir.Replace('\\', '/').TrimEnd('/');

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

                // Two authored names can sanitize to one filename, and the later write would clobber the earlier clip.
                // OrdinalIgnoreCase because NTFS is case-insensitive: "Wave" and "wave" occupy one file.
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

                var paramNames = new HashSet<string>(doc.Parameters.Select(p => p.Name));

                // Detached clips. The write loop adopts or destroys each and nulls its slot, so the finally frees only
                // what a refusal or exception strands.
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

                // Divergence guard: see the class doc.
                var diverged = new List<string>();
                foreach (var spec in doc.Clips)
                {
                    string path = outClean + "/" + TransplantCore.Sanitize(spec.Name) + ".anim";
                    var onDisk = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (onDisk == null) continue;
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

                if (!whatIf) TransplantCore.EnsureFolderExists(outClean);

                int created = 0, reused = 0;
                foreach (var spec in doc.Clips)
                {
                    string path = outClean + "/" + TransplantCore.Sanitize(spec.Name) + ".anim";
                    var b = built[spec.Name];
                    var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

                    if (whatIf)
                    {
                        if (existing != null) reused++; else created++;
                        UnityEngine.Object.DestroyImmediate(b);
                        built[spec.Name] = null;
                    }
                    else if (existing != null)
                    {
                        ClearAndCopy(existing, b);
                        UnityEngine.Object.DestroyImmediate(b);
                        built[spec.Name] = null;
                        reused++;
                    }
                    else
                    {
                        AssetDatabase.CreateAsset(b, path);
                        built[spec.Name] = null;
                        created++;
                    }
                    log.Note(path);
                }

                if (!whatIf)
                {
                    foreach (var spec in doc.Clips)
                    {
                        string clipPath = outClean + "/" + TransplantCore.Sanitize(spec.Name) + ".anim";
                        var emittedClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                        if (emittedClip != null) AssetDatabase.SaveAssetIfDirty(emittedClip);
                    }

                    // Stamp the hash of the clip reloaded from disk, never the in-memory `built` clip: float and tangent
                    // serialization can drift across the .anim round-trip, and the divergence guard recomputes from disk.
                    // Stage every stamp outside the asset-editing batch and persist them inside it (StageContentStamp says
                    // why). Deferring the imports is safe because the stamp is only read on a later call.
                    int unstamped = 0;
                    var toPersist = new List<AssetImporter>();
                    foreach (var spec in doc.Clips)
                    {
                        string path = outClean + "/" + TransplantCore.Sanitize(spec.Name) + ".anim";
                        var onDisk = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                        var imp = onDisk != null ? StageContentStamp(path, HashClipContent(onDisk)) : null;
                        if (imp != null) { toPersist.Add(imp); continue; }
                        unstamped++;
                        log.Offender("clip '" + spec.Name + "' was written to " + path + " but could not be stamped (" +
                            (onDisk == null ? "the written .anim did not load back" : "no importer at that path") + ")");
                    }
                    // StopAssetEditing belongs in a finally: an exception thrown inside the batch would
                    // otherwise leave the AssetDatabase with imports paused for the rest of the Editor session.
                    if (toPersist.Count > 0)
                    {
                        AssetDatabase.StartAssetEditing();
                        try
                        {
                            foreach (var imp in toPersist) imp.SaveAndReimport();
                        }
                        finally
                        {
                            AssetDatabase.StopAssetEditing();
                        }
                    }
                    // Raised after the batch so one bad path costs only its own stamp.
                    if (unstamped > 0)
                        return Fail(log, label, "wrote " + doc.Clips.Count + " clip(s) but " + unstamped +
                            " could not be stamped — those will read as diverged on the next compile");
                }

                log.Count("emitted", doc.Clips.Count);
                log.Count("created", created);
                log.Count("reused", reused);
                log.result = "PASS";
                }
                finally
                {
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

        /// <summary>Order-independent content hash over the human-editable, round-trip-stable fields of
        /// <paramref name="clip"/>. Excludes <c>clip.localBounds</c>: it derives from the curves and Unity may recompute
        /// it, which would manufacture false divergence.</summary>
        public static string HashClipContent(AnimationClip clip)
        {
            if (clip == null) return ControllerEmit.SourceHash("");

            var sb = new StringBuilder();
            sb.Append("frameRate=").Append(R(clip.frameRate))
              .Append(";wrapMode=").Append(((int)clip.wrapMode).ToString(CultureInfo.InvariantCulture))
              .Append(";legacy=").Append(clip.legacy).Append('\n');

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
                    sb.Append('~').Append(((int)curve.preWrapMode).ToString(CultureInfo.InvariantCulture))
                      .Append('/').Append(((int)curve.postWrapMode).ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('\n');
            }

            // CompileClips' own output never reaches this branch: the clips: grammar produces only float curves. It
            // exists so a hand-edited .anim carrying a PPtr curve hashes stably for the divergence guard.
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

            // Unsorted: event order is itself content.
            foreach (var e in AnimationUtility.GetAnimationEvents(clip))
                sb.Append("E ").Append(R(e.time)).Append('|').Append(e.functionName).Append('|')
                  .Append(e.stringParameter).Append('|').Append(R(e.floatParameter)).Append('|')
                  .Append(e.intParameter.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(RefId(e.objectReferenceParameter)).Append('|')
                  .Append(((int)e.messageOptions).ToString(CultureInfo.InvariantCulture))
                  .Append('\n');

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

        static string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);

        // Asset GUID+localFileId, because an instance ID is session-local and would churn the hash. Only a scene
        // or non-asset object with no GUID falls back to one.
        static string RefId(UnityEngine.Object o)
        {
            if (o == null) return "null";
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out var guid, out long lfid)
                ? guid + ":" + lfid.ToString(CultureInfo.InvariantCulture)
                : "iid:" + o.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Write <paramref name="hash"/> into the <c>.anim</c>'s importer userData (the channel the controller
        /// srchash uses) and return that importer unpersisted; null when there is no importer. The caller persists with
        /// <c>SaveAndReimport</c> inside one <c>StartAssetEditing</c> batch, since one import per clip otherwise dominates
        /// a compile, and calls this outside that batch, since AssetDatabase reads are unreliable while importing is
        /// paused. A caller must fail loud on null: an unstamped clip reads as hand-edited on the next compile.</summary>
        public static AssetImporter StageContentStamp(string animPath, string hash)
        {
            var imp = AssetImporter.GetAtPath(animPath);
            if (imp == null) return null;
            imp.userData = "clipcontent:" + hash;
            return imp;
        }

        /// <summary>The content stamp in the <c>.anim</c>'s importer userData; null when unstamped or no importer.</summary>
        public static string ReadContentStamp(string animPath)
        {
            var imp = AssetImporter.GetAtPath(animPath);
            return imp == null ? null : ExtractField(imp.userData, "clipcontent:");
        }

        static string ExtractField(string userData, string key)
        {
            if (string.IsNullOrEmpty(userData)) return null;
            int i = userData.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int end = userData.IndexOf(';', i);
            return end < 0 ? userData.Substring(i) : userData.Substring(i, end - i);
        }

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

        static string Fail(RunLog log, string label, string msg)
        {
            log.result = "FAIL";
            log.error = msg;
            return TransplantCore.Finish(log, label);
        }
    }
}
