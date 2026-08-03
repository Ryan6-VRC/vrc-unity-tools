using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Corrects the import settings that hard-fail a driven VRChat upload.
    ///
    /// The VRCSDK's blocking validations read the <b>on-disk importer</b>, not the asset — e.g.
    /// <c>importer.mipmapEnabled &amp;&amp; !importer.streamingMipmaps</c> — so nothing that rewrites the avatar at
    /// build time can satisfy them. NDMF reports the mip case at <c>ErrorSeverity.NonFatal</c> (it does not
    /// block); VRCFury ships a fix but gates its whole builder on a VRCFury component existing, so an
    /// MA-only avatar never gets it, and it works by cloning the texture into the build rather than
    /// correcting anything. The SDK's own remedy is an Auto Fix button no driven build can press. That
    /// leaves a <c>.meta</c> write as the only remedy, which is why this door exists.
    ///
    /// SCOPE RULE: this owns every blocking importer validation whose offender status is <b>intrinsic to the
    /// asset</b>. Two SDK validations are therefore out, permanently and for different reasons — the
    /// expression-menu icon rule (a texture is an offender only because a menu control points at it; nothing
    /// on the asset says so, and its fix clamps to 256px, so applying it by asset type would wreck a
    /// package) and Box→Kaiser mip filtering (reported at <c>OnGUIInformation</c> — advisory, so not ours to
    /// force). Adding a row needs both halves: it blocks, and the asset alone decides.
    ///
    /// Two rows can change what ships — <c>max-texture-size</c> when the source genuinely exceeds the cap,
    /// and <c>legacy-blendshape-normals</c>, which recomputes normals from smoothing groups. They are
    /// applied rather than withheld because they <i>block</i>: withholding leaves an upload that cannot be
    /// made and a fix the operator must apply by hand anyway. The obligation is that no such change is
    /// silent — every affected path is named in the summary and the RunLog.
    ///
    /// There is deliberately NO <c>force</c> parameter. Elsewhere in the kit a vendor write demands
    /// <c>force=true</c> and records the breach loudly (<c>TransplantCore.IsWritableAsset</c>); here the whole
    /// door is the sanction — its scope is exactly the sanctioned class, and every written path is logged.
    /// A <c>force</c> flag would imply an unsanctioned mode that does not exist. Do not add one.
    ///
    /// Writes <c>.meta</c> files only. Source bytes are never touched and every change is revertible;
    /// `docs/LAYOUT.md` §Vendor mutation owns whether that write is permitted under `Assets/Vendor/`.
    /// </summary>
    [AgentTool]
    public static class ConformImportSettings
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;

        internal const string RowMipStreaming = "mip-streaming";
        internal const string RowMaxTextureSize = "max-texture-size";
        internal const string RowMeshReadable = "mesh-readable";
        internal const string RowLegacyNormals = "legacy-blendshape-normals";
        internal const string RowAudioBackgroundLoad = "audio-background-load";

        internal static readonly string[] AllRows =
        {
            RowMipStreaming, RowMaxTextureSize, RowMeshReadable, RowLegacyNormals, RowAudioBackgroundLoad
        };

        private const string ScopeNote = "scope=t:Texture,t:Model,t:AudioClip under this folder only";

        // ----- Public API (callable from execute_code / the import skill) ---------------------

        /// <summary>Conform every offending import setting under an asset folder, recursively.
        /// <paramref name="whatIf"/> previews: identical traversal, nothing written. Returns a one-line
        /// summary ending with the RunLog path (<c>… =&gt; RESULT | log=&lt;path&gt;</c>); a bad-input early return
        /// is a bare <c>[ConformImportSettings] FAIL: …</c> with no trailer.</summary>
        public static string RunFolder(string assetFolderPath, bool whatIf = false)
        {
            if (string.IsNullOrEmpty(assetFolderPath) || !AssetDatabase.IsValidFolder(assetFolderPath))
                return "[ConformImportSettings] FAIL: not a valid asset folder: " + assetFolderPath;

            var r = new Report { Target = assetFolderPath, WhatIf = whatIf };
            Scan(assetFolderPath, r);
            if (!whatIf && r.Findings.Count > 0) Apply(r);
            return Finish(r, RunLogFormat.Leaf(assetFolderPath));
        }

        // ----- Scanning -----------------------------------------------------------------------

        private static void Scan(string folder, Report r)
        {
            var roots = new[] { folder };

            int cap = MaxSdkTextureSize(r);
            foreach (var path in PathsOfType("t:Texture", roots))
            {
                // Null for every asset whose importer is not a TextureImporter: native-format assets
                // (.renderTexture, a CreateAsset texture), .dds via IHVImageFormatImporter, and textures
                // that are sub-assets of an FBX. The SDK skips all of them for the same reason — its own
                // checks are `AssetImporter.GetAtPath(path) as TextureImporter` plus a null test.
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) { r.Skipped++; continue; }
                r.Scanned++;

                if (ti.mipmapEnabled && !ti.streamingMipmaps)
                    r.Add(path, RowMipStreaming, "mipmapped, streaming off", false);

                if (cap > 0 && ti.maxTextureSize > cap)
                {
                    // The SDK's predicate is importer-only, so a 512px source with a 16384 cap is an
                    // offender whose correction changes nothing that ships. Deriving the disclosure from
                    // the real dimensions instead of the row id keeps the render-check obligation honest.
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
                    int longest = tex == null ? 0 : Math.Max(tex.width, tex.height);
                    bool downscales = longest > cap;
                    r.Add(path, RowMaxTextureSize,
                        "cap=" + ti.maxTextureSize + " > " + cap + ", longest edge=" + (tex == null ? "unknown" : longest.ToString(CultureInfo.InvariantCulture)),
                        downscales);
                }
            }

            var legacyProp = LegacyNormalsProperty(r);
            foreach (var path in PathsOfType("t:Model", roots))
            {
                var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                if (mi == null) { r.Skipped++; continue; }
                r.Scanned++;

                if (!mi.isReadable)
                    r.Add(path, RowMeshReadable, "read/write disabled", false);

                if (legacyProp != null
                    && mi.importBlendShapeNormals == ModelImporterNormals.Calculate
                    && !ReadLegacy(legacyProp, mi))
                    r.Add(path, RowLegacyNormals, "blendshape normals = Calculate without legacy", true);
            }

            foreach (var path in PathsOfType("t:AudioClip", roots))
            {
                var ai = AssetImporter.GetAtPath(path) as AudioImporter;
                if (ai == null) { r.Skipped++; continue; }
                r.Scanned++;

                // The SDK tests the CLIP, not the importer (`clip.loadType` / `clip.loadInBackground`) —
                // reading `AudioImporter.defaultSampleSettings` instead diverges wherever a per-platform
                // override exists. The importer is only where the write lands.
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) { r.Skipped++; continue; }
                if (clip.loadType == AudioClipLoadType.DecompressOnLoad && !clip.loadInBackground)
                    r.Add(path, RowAudioBackgroundLoad, "DecompressOnLoad without load-in-background", false);
            }
        }

        private static IEnumerable<string> PathsOfType(string filter, string[] roots)
        {
            return AssetDatabase.FindAssets(filter, roots)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal);
        }

        // ----- Applying -----------------------------------------------------------------------

        private static void Apply(Report r)
        {
            // Coalesce by path BEFORE writing anything. Two reasons, both load-bearing: an FBX can fail
            // both mesh rows and must be reimported once, not twice (the SDK's own fixes call
            // SaveAndReimport independently and pay twice); and reimporting a model destroys and recreates
            // its Mesh objects, so any asset object collected during the scan is dead afterwards. Nothing
            // below dereferences a scanned object — only paths.
            var byPath = r.Findings.GroupBy(f => f.Path).ToList();
            var legacyProp = LegacyNormalsProperty(r);
            int cap = MaxSdkTextureSize(r);

            foreach (var group in byPath)
            {
                var importer = AssetImporter.GetAtPath(group.Key);
                if (importer == null) continue;

                foreach (var f in group)
                {
                    switch (f.Row)
                    {
                        case RowMipStreaming:
                            ((TextureImporter)importer).streamingMipmaps = true;
                            break;
                        case RowMaxTextureSize:
                            ((TextureImporter)importer).maxTextureSize = cap;
                            break;
                        case RowMeshReadable:
                            ((ModelImporter)importer).isReadable = true;
                            break;
                        case RowLegacyNormals:
                            if (legacyProp != null) legacyProp.SetValue(importer, true, null);
                            break;
                        case RowAudioBackgroundLoad:
                            ((AudioImporter)importer).loadInBackground = true;
                            break;
                    }
                }

                // Gate on the .meta write rather than firing and assuming. A false means the flag never
                // reached disk, so importing would apply nothing — counting attempts here would report
                // "conformed N" for a number the operator cannot check without opening every importer.
                // Measured: the setter alone dirties Texture/Model/Audio importers; EditorUtility.SetDirty
                // (which the SDK calls first) is not required.
                if (!AssetDatabase.WriteImportSettingsIfDirty(group.Key))
                    r.Unwritten.Add(group.Key);
            }

            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (var group in byPath)
                    AssetDatabase.ImportAsset(group.Key,
                        ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // Truth is on disk, not in the write call. A row whose importer accepted the setter but whose
            // flag did not survive the import would otherwise re-fire on the next run while this one
            // claimed success, so the reported count is re-derived by re-running each predicate.
            foreach (var f in r.Findings) f.Persisted = !StillOffending(f, legacyProp, cap);
        }

        private static bool StillOffending(Finding f, PropertyInfo legacyProp, int cap)
        {
            var importer = AssetImporter.GetAtPath(f.Path);
            switch (f.Row)
            {
                case RowMipStreaming:
                {
                    var ti = importer as TextureImporter;
                    return ti == null || (ti.mipmapEnabled && !ti.streamingMipmaps);
                }
                case RowMaxTextureSize:
                {
                    var ti = importer as TextureImporter;
                    return ti == null || (cap > 0 && ti.maxTextureSize > cap);
                }
                case RowMeshReadable:
                {
                    var mi = importer as ModelImporter;
                    return mi == null || !mi.isReadable;
                }
                case RowLegacyNormals:
                {
                    var mi = importer as ModelImporter;
                    if (mi == null || legacyProp == null) return true;
                    return mi.importBlendShapeNormals == ModelImporterNormals.Calculate && !ReadLegacy(legacyProp, mi);
                }
                case RowAudioBackgroundLoad:
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(f.Path);
                    return clip == null || (clip.loadType == AudioClipLoadType.DecompressOnLoad && !clip.loadInBackground);
                }
            }
            return false;
        }

        // ----- Vendor pins: one row each, fail loud, never a whole-run failure -----------------

        /// <summary>The SDK's own texture cap (<c>VRCSdkControlPanel.MAX_SDK_TEXTURE_SIZE</c>), read from
        /// metadata rather than hardcoded so a future SDK cap change cannot silently make us wrong.
        /// Returns 0 and skips the row — loudly, in the summary — when the pin does not resolve.</summary>
        private static int MaxSdkTextureSize(Report r)
        {
            if (r.CapCache.HasValue) return r.CapCache.Value;
            int cap = 0;
            var t = VendorReflect.FindType("VRCSdkControlPanel");
            var f = t == null ? null : t.GetField("MAX_SDK_TEXTURE_SIZE", BindingFlags.Public | BindingFlags.Static);
            if (f != null && f.IsLiteral)
            {
                try { cap = Convert.ToInt32(f.GetRawConstantValue(), CultureInfo.InvariantCulture); }
                catch (Exception) { cap = 0; }
            }
            if (cap <= 0) r.SkipRow(RowMaxTextureSize, "VRCSdkControlPanel.MAX_SDK_TEXTURE_SIZE did not resolve");
            r.CapCache = cap;
            return cap;
        }

        /// <summary>Unity's private <c>ModelImporter.legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes</c>
        /// — the same member the SDK reflects for the same reason, and it guards the identical way. A Unity
        /// internal rather than vendor plumbing, so the pin lives here rather than stretching
        /// <see cref="VendorReflect"/>'s stated MA/VRCFury/NDMF charter. Null skips only its own row.</summary>
        private static PropertyInfo LegacyNormalsProperty(Report r)
        {
            if (r.LegacyProbed) return r.LegacyProp;
            r.LegacyProbed = true;
            r.LegacyProp = typeof(ModelImporter).GetProperty(
                "legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (r.LegacyProp == null)
                r.SkipRow(RowLegacyNormals, "ModelImporter.legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes did not resolve");
            return r.LegacyProp;
        }

        private static bool ReadLegacy(PropertyInfo prop, ModelImporter mi)
        {
            try { return (bool)prop.GetValue(mi, null); }
            catch (Exception) { return true; } // unreadable ⇒ treat as satisfied; never flag on a broken read
        }

        // ----- Reporting ----------------------------------------------------------------------

        private sealed class Finding
        {
            public string Path;
            public string Row;
            public string Detail;
            public bool ChangesShipped;
            public bool Persisted;
        }

        private sealed class Report
        {
            public string Target;
            public bool WhatIf;
            public int Scanned;
            public int Skipped;
            public readonly List<Finding> Findings = new List<Finding>();
            public readonly List<string> Unwritten = new List<string>();
            public readonly List<string> SkippedRows = new List<string>();
            public int? CapCache;
            public bool LegacyProbed;
            public PropertyInfo LegacyProp;

            public void Add(string path, string row, string detail, bool changesShipped)
            {
                Findings.Add(new Finding { Path = path, Row = row, Detail = detail, ChangesShipped = changesShipped });
            }

            public void SkipRow(string row, string why)
            {
                if (!SkippedRows.Any(s => s.StartsWith(row, StringComparison.Ordinal)))
                    SkippedRows.Add(row + " (" + why + ")");
            }
        }

        private static string Finish(Report r, string label)
        {
            var perRow = AllRows
                .Select(row => new { row, n = r.Findings.Count(f => f.Row == row) })
                .Where(x => x.n > 0)
                .Select(x => x.row + "=" + x.n)
                .ToList();
            string rows = perRow.Count == 0 ? "none" : string.Join(" ", perRow);

            var shipping = r.Findings.Where(f => f.ChangesShipped).Select(f => f.Path).Distinct().ToList();
            var notPersisted = r.WhatIf
                ? new List<Finding>()
                : r.Findings.Where(f => !f.Persisted).ToList();

            bool pass = notPersisted.Count == 0 && r.Unwritten.Count == 0;
            string result = r.WhatIf ? "PASS" : (pass ? "PASS" : "NOT-PASS");

            string verb = r.WhatIf ? "would conform" : "conformed";
            var sb = new StringBuilder();
            sb.Append("[ConformImportSettings] ").Append(label)
              .Append(r.WhatIf ? " (whatIf)" : "")
              .Append(": ").Append(r.Scanned).Append(" scanned");
            if (r.Skipped > 0) sb.Append(", ").Append(r.Skipped).Append(" not importer-typed");
            sb.Append(" | ").Append(verb).Append(": ").Append(rows);
            if (shipping.Count > 0)
                sb.Append(" | CHANGES WHAT SHIPS on ").Append(shipping.Count)
                  .Append(" path(s) — render check owed: ").Append(string.Join(", ", shipping.Take(5)))
                  .Append(shipping.Count > 5 ? ", …" : "");
            if (notPersisted.Count > 0)
                sb.Append(" | NOT PERSISTED: ")
                  .Append(string.Join(", ", notPersisted.Select(f => f.Row + "@" + f.Path).Take(5)))
                  .Append(notPersisted.Count > 5 ? ", …" : "");
            if (r.SkippedRows.Count > 0)
                sb.Append(" | ROWS SKIPPED: ").Append(string.Join("; ", r.SkippedRows));
            sb.Append(" | ").Append(ScopeNote);
            sb.Append(" => ").Append(result);

            string summary = RunLogFormat.WriteRunLog(RunLogDir, "conformimportsettings_" + label, sb.ToString(), BuildLog(r, label, result), ".json");
            if (result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        private static string BuildLog(Report r, string label, string result)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"conform-import-settings\",\n");
            sb.Append("  \"unityVersion\": ").Append(RunLogFormat.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"target\": ").Append(RunLogFormat.Q(r.Target)).Append(",\n");
            sb.Append("  \"label\": ").Append(RunLogFormat.Q(label)).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(r.WhatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"scanned\": ").Append(r.Scanned).Append(",\n");
            sb.Append("  \"notImporterTyped\": ").Append(r.Skipped).Append(",\n");
            sb.Append("  \"scope\": ").Append(RunLogFormat.Q(ScopeNote)).Append(",\n");
            sb.Append("  \"rowsSkipped\": [");
            sb.Append(string.Join(", ", r.SkippedRows.Select(RunLogFormat.Q)));
            sb.Append("],\n");
            sb.Append("  \"findings\": [\n");
            for (int i = 0; i < r.Findings.Count; i++)
            {
                var f = r.Findings[i];
                sb.Append("    { \"row\": ").Append(RunLogFormat.Q(f.Row))
                  .Append(", \"path\": ").Append(RunLogFormat.Q(f.Path))
                  .Append(", \"detail\": ").Append(RunLogFormat.Q(f.Detail))
                  .Append(", \"changesWhatShips\": ").Append(f.ChangesShipped ? "true" : "false")
                  .Append(", \"written\": ").Append(r.WhatIf ? "false" : (r.Unwritten.Contains(f.Path) ? "false" : "true"))
                  .Append(", \"persisted\": ").Append(r.WhatIf ? "null" : (f.Persisted ? "true" : "false"))
                  .Append(" }").Append(i + 1 < r.Findings.Count ? "," : "").Append("\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"result\": ").Append(RunLogFormat.Q(result)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
