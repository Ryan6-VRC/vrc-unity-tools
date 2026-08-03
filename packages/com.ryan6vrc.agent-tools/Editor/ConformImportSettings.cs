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
    /// The VRCSDK's blocking validations read the <b>on-disk importer</b>, not the asset, so no build pass can
    /// correct them and no universally-present pass can be relied on to mask them (VRCFury's texture clone does
    /// mask the mip check, but gates on a VRCFury component existing). A <c>.meta</c> write is the only remedy.
    /// Which settings and why that write is sanctioned under <c>Assets/Vendor/</c>: `docs/LAYOUT.md` §Vendor
    /// mutation, and the contract is `docs/unity-tools.md`. Adding a row needs both halves of the rule there —
    /// it blocks, and the asset alone decides whether it is an offender.
    ///
    /// There is deliberately NO <c>force</c> parameter, and that is an instruction to whoever maintains this next.
    /// Elsewhere a vendor write takes <c>force=true</c> to override a real per-asset writable decision
    /// (<c>TransplantCore.IsWritableAsset</c>, 11 sites). Here the folder argument <i>is</i> the scope decision and
    /// there is no second class of asset a flag could unlock — so a <c>force</c> flag would be unfalsifiable, and
    /// the guard that actually matters is the refusal of over-broad roots below. Do not add one.
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

        private const string ScopeNote = "scope=t:Texture,t:Model,t:AudioClip under this folder only, 5 rows (menu-icon + mip-filter validations excluded — still the operator's)";

        // ----- Public API (callable from execute_code / the import skill) ---------------------

        /// <summary>Conform every offending import setting under an asset folder, recursively.
        /// <paramref name="whatIf"/> previews: identical traversal, nothing written. Returns a one-line
        /// summary ending with the RunLog path (<c>… =&gt; RESULT | log=&lt;path&gt;</c>); a bad-input early return
        /// is a bare <c>[ConformImportSettings] FAIL: …</c> with no trailer.</summary>
        public static string RunFolder(string assetFolderPath, bool whatIf = false)
        {
            if (string.IsNullOrEmpty(assetFolderPath) || !AssetDatabase.IsValidFolder(assetFolderPath))
                return "[ConformImportSettings] FAIL: not a valid asset folder: " + assetFolderPath;

            // The folder argument is the only bound on a write that is partly lossy, so the roots are refused by
            // name rather than trusted. `Assets` would clamp every oversize cap in the project; a `Packages` tree
            // is rewritten by `vrc-get resolve` anyway, so a write there is discarded rather than sanctioned.
            var norm = assetFolderPath.Replace('\\', '/').TrimEnd('/');
            if (norm == "Assets" || norm == "Packages" || norm.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return "[ConformImportSettings] FAIL: refusing an over-broad or non-durable root (" + assetFolderPath
                     + "): pass the specific vendor or owned folder to conform, e.g. Assets/Vendor/Outfits/<Name>.";

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
                    // The SDK's predicate is importer-only, so a small source under a huge cap is an offender
                    // whose correction changes nothing that ships. Deriving the disclosure from the real
                    // dimensions instead of the row id keeps the render-check obligation honest.
                    //
                    // It must be the SOURCE dimensions, not the imported ones: an imported Texture is already
                    // clamped by the active platform's settings, so a 16K source behind a 4K Android override
                    // reads as 4096 and would report "costless" while lowering the default cap really does
                    // downscale it everywhere that override is absent. When the source cannot be measured, the
                    // path is disclosed rather than assumed safe — an unmeasurable dimension is not a small one.
                    int longest = SourceLongestEdge(ti, r);
                    bool downscales = longest <= 0 || longest > cap;
                    r.Add(path, RowMaxTextureSize,
                        "cap=" + ti.maxTextureSize + " > " + cap + ", source longest edge="
                        + (longest > 0 ? longest.ToString(CultureInfo.InvariantCulture) : "unmeasurable"),
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

                bool legacySet;
                if (legacyProp != null
                    && mi.importBlendShapeNormals == ModelImporterNormals.Calculate
                    && TryReadLegacy(legacyProp, mi, r, path, out legacySet)
                    && !legacySet)
                {
                    // The legacy flag only changes normals on meshes that HAVE blendshapes, and Unity's default
                    // is Calculate-without-legacy — so every default-imported model fires this row while most
                    // are unaffected. Claiming "changes what ships" on all of them is the same dishonesty the
                    // max-texture-size comment above rejects: it trains the reader to ignore the real warning.
                    // Measured pre-write, because the reimport below invalidates every Mesh object.
                    bool hasShapes = AssetDatabase.LoadAllAssetsAtPath(path)
                        .OfType<Mesh>().Any(m => m.blendShapeCount > 0);
                    r.Add(path, RowLegacyNormals,
                        "blendshape normals = Calculate without legacy" + (hasShapes ? "" : " (no blendshapes — shading unaffected)"),
                        hasShapes);
                }
            }

            foreach (var path in PathsOfType("t:AudioClip", roots))
            {
                var ai = AssetImporter.GetAtPath(path) as AudioImporter;
                if (ai == null) { r.Skipped++; continue; }

                // The SDK tests the CLIP, not the importer (`clip.loadType` / `clip.loadInBackground`) —
                // reading `AudioImporter.defaultSampleSettings` instead diverges wherever a per-platform
                // override exists. The importer is only where the write lands.
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) { r.ClipLoadFailures++; continue; }
                r.Scanned++;
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

                // One path's write must never take the run down: earlier paths already have .meta writes on disk,
                // and an escaping exception would skip Finish entirely — no summary, no RunLog, i.e. a vendor
                // mutation with nothing recording it. Record the path, keep going, let the verdict report it.
                try
                {
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
                }
                catch (Exception e)
                {
                    r.WriteErrors.Add(group.Key + ": " + e.GetType().Name + " " + e.Message);
                    continue;
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
                    if (mi.importBlendShapeNormals != ModelImporterNormals.Calculate) return false;
                    // An unreadable flag cannot verify the write, so it counts as still offending rather than
                    // as satisfied — this is the post-condition the reported count is built from.
                    bool set;
                    try { set = (bool)legacyProp.GetValue(mi, null); }
                    catch (Exception) { return true; }
                    return !set;
                }
                case RowAudioBackgroundLoad:
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(f.Path);
                    return clip == null || (clip.loadType == AudioClipLoadType.DecompressOnLoad && !clip.loadInBackground);
                }
            }
            return true; // unknown row ⇒ never claim it persisted
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
        /// internal rather than vendor plumbing, which is why this pin is local — the SDK cap above does go through
        /// <see cref="VendorReflect"/>, whose docblock names the VRCSDK alongside MA/VRCFury/NDMF. Skips only its own row.</summary>
        private static PropertyInfo LegacyNormalsProperty(Report r)
        {
            if (r.LegacyProbed) return r.LegacyProp;
            r.LegacyProbed = true;
            var p = typeof(ModelImporter).GetProperty(
                "legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            // Resolving is not enough to write through: a member turned get-only, or retyped, still resolves,
            // and the row would then fire and throw mid-apply. Pin the shape we actually need, or skip the row.
            if (p == null || !p.CanWrite || p.PropertyType != typeof(bool))
                r.SkipRow(RowLegacyNormals, "ModelImporter.legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes is absent or not a writable bool");
            else
                r.LegacyProp = p;
            return r.LegacyProp;
        }

        /// <summary>The <b>source</b> file's longest edge, or 0 when it cannot be measured. Unity exposes this only
        /// internally and has renamed it across versions, so both known names are probed; an unresolvable pin means
        /// the caller discloses the path instead of assuming it costless. Never use the imported
        /// <see cref="Texture"/>'s dimensions here — those are already clamped by the active platform's settings.</summary>
        private static int SourceLongestEdge(TextureImporter ti, Report r)
        {
            if (!r.SourceDimProbed)
            {
                r.SourceDimProbed = true;
                var t = typeof(TextureImporter);
                r.SourceDimMethod = t.GetMethod("GetSourceTextureWidthAndHeight", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                 ?? t.GetMethod("GetWidthAndHeight", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (r.SourceDimMethod == null)
                    r.Note("source texture dimensions unmeasurable (no GetSourceTextureWidthAndHeight/GetWidthAndHeight) — max-texture-size paths disclosed conservatively");
            }
            if (r.SourceDimMethod == null) return 0;
            try
            {
                var args = new object[] { 0, 0 };
                r.SourceDimMethod.Invoke(ti, args);
                return Math.Max(Convert.ToInt32(args[0], CultureInfo.InvariantCulture),
                                Convert.ToInt32(args[1], CultureInfo.InvariantCulture));
            }
            catch (Exception) { return 0; }
        }

        /// <summary>Which VRChat SDK the rows' <c>OnGUIError</c> severity was taken against — the thing that makes
        /// the write sanctioned, and the thing an SDK upgrade obliges a re-check of.</summary>
        private static string SdkVersions()
        {
            var parts = new List<string>();
            foreach (var id in new[] { "com.vrchat.avatars", "com.vrchat.base" })
            {
                var pi = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + id + "/package.json");
                parts.Add(id + "=" + (pi == null ? "absent" : pi.version));
            }
            return string.Join(" ", parts);
        }

        /// <summary>Reads the legacy flag. A failed read must <b>skip the row loudly</b>, never be swallowed into
        /// "already satisfied" — that would silently narrow the predicate set under a <c>PASS</c>, which is the one
        /// thing the scope note cannot express.</summary>
        private static bool TryReadLegacy(PropertyInfo prop, ModelImporter mi, Report r, string path, out bool value)
        {
            try { value = (bool)prop.GetValue(mi, null); return true; }
            catch (Exception e)
            {
                value = false;
                r.SkipRow(RowLegacyNormals, "unreadable on " + path + ": " + e.GetType().Name);
                return false;
            }
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
            public readonly List<string> WriteErrors = new List<string>();
            public int ClipLoadFailures;
            public bool SourceDimProbed;
            public System.Reflection.MethodInfo SourceDimMethod;
            public readonly List<string> Notes = new List<string>();

            public void Note(string n) { if (!Notes.Contains(n)) Notes.Add(n); }
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

            bool pass = notPersisted.Count == 0 && r.Unwritten.Count == 0 && r.WriteErrors.Count == 0;
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
            if (r.WriteErrors.Count > 0)
                sb.Append(" | WRITE FAILED: ").Append(string.Join("; ", r.WriteErrors.Take(3)))
                  .Append(r.WriteErrors.Count > 3 ? ", …" : "");
            if (r.ClipLoadFailures > 0)
                sb.Append(" | ").Append(r.ClipLoadFailures).Append(" audio clip(s) would not load");
            if (r.SkippedRows.Count > 0)
                sb.Append(" | ROWS SKIPPED: ").Append(string.Join("; ", r.SkippedRows));
            if (r.Notes.Count > 0)
                sb.Append(" | NOTE: ").Append(string.Join("; ", r.Notes));
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
            // The rows are sanctioned because THIS SDK reports them at OnGUIError. A future SDK could demote one
            // to an advisory, at which point writing a vendor .meta for it is no longer sanctioned — so the log
            // records which SDK the sanction was taken against, and an upgrade owes a severity re-check.
            sb.Append("  \"vrchatSdk\": ").Append(RunLogFormat.Q(SdkVersions())).Append(",\n");
            sb.Append("  \"target\": ").Append(RunLogFormat.Q(r.Target)).Append(",\n");
            sb.Append("  \"label\": ").Append(RunLogFormat.Q(label)).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(r.WhatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"scanned\": ").Append(r.Scanned).Append(",\n");
            sb.Append("  \"notImporterTyped\": ").Append(r.Skipped).Append(",\n");
            sb.Append("  \"clipLoadFailures\": ").Append(r.ClipLoadFailures).Append(",\n");
            sb.Append("  \"writeErrors\": [").Append(string.Join(", ", r.WriteErrors.Select(RunLogFormat.Q))).Append("],\n");
            sb.Append("  \"notes\": [").Append(string.Join(", ", r.Notes.Select(RunLogFormat.Q))).Append("],\n");
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
