using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Corrects the import settings that hard-fail a driven VRChat upload — over an asset folder, or over
    /// everything a placed avatar root references (the set the SDK panel's own validations walk, so
    /// <c>whatIf</c> on a root is the pre-build preview of the panel's verdict).
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
    /// (<c>TransplantCore.IsWritableAsset</c>, 11 sites). Here the scope argument <i>is</i> the scope decision and
    /// there is no second class of asset a flag could unlock — so a <c>force</c> flag would be unfalsifiable, and
    /// the guards that actually matter are the refusal of over-broad folder roots below and the one fixed
    /// per-asset refusal an avatar scope introduces: an offender under <c>Packages/</c> is named, never written,
    /// because a VPM resolve rewrites that tree and the correction would be discarded, not sanctioned. Do not add one.
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
        private const string AvatarScopeNote = "scope=meshes on SkinnedMeshRenderer/MeshFilter/ParticleSystemRenderer, textures on every Renderer's materials, clips on every AudioSource under this root (inactive included, EditorOnly subtrees excluded — the SDK panel's own walk; clips an animator Play Audio behaviour names are NOT collected — still the panel's), 5 rows (menu-icon + mip-filter validations excluded — still the operator's)";

        // ----- Public API (callable from execute_code / the import skill) ---------------------

        /// <summary>Conform every offending import setting in a scope. <paramref name="scope"/> is an asset
        /// folder (recursive) or a placed scene root — hierarchy path, instance id, or unique name — whose
        /// referenced meshes, textures and clips are the set (see <see cref="Run(GameObject, bool)"/>). A folder
        /// wins when the string is both. <paramref name="whatIf"/> previews: identical traversal, nothing written.
        /// Returns a one-line summary ending with the RunLog path (<c>… =&gt; RESULT | log=&lt;path&gt;</c>); a
        /// bad-input early return is a bare <c>[ConformImportSettings] FAIL: …</c> with no trailer.</summary>
        public static string Run(string scope, bool whatIf = false)
        {
            if (string.IsNullOrEmpty(scope))
                return "[ConformImportSettings] FAIL: empty scope: pass an asset folder (e.g. Assets/Vendor/Outfits/<Name>) or a placed avatar root.";
            if (AssetDatabase.IsValidFolder(scope)) return RunFolder(scope, whatIf);
            var handle = SceneHandle.Resolve(scope);
            if (handle.Ok) return Run(handle.Object, whatIf);
            if (handle.Outcome != SceneHandleOutcome.NotFound)
                return "[ConformImportSettings] FAIL: " + handle.Refusal;
            return "[ConformImportSettings] FAIL: neither a valid asset folder nor a scene object: " + scope
                 + " (an asset folder such as Assets/Vendor/Outfits/<Name>, or a placed avatar root's hierarchy path).";
        }

        /// <summary>Conform every offending import setting behind a placed avatar root: the meshes on every
        /// SkinnedMeshRenderer / MeshFilter / ParticleSystemRenderer, the textures on every Renderer's materials,
        /// and the clips on every AudioSource — inactive objects included, <c>EditorOnly</c>-tagged subtrees
        /// excluded — which is the set the VRCSDK panel's own validations collect
        /// (<c>com.vrchat.avatars/Editor/VRCSDK/SDK3A/VRCSdkControlPanelAvatarBuilder.cs</c>: the two
        /// <c>CheckAvatarMeshesFor…</c> checks, <c>VerifyAvatarMipMapStreaming</c>, <c>VerifyMaxTextureSize</c>,
        /// the AudioSource loop in <c>ValidateFeatures</c>). Read from the placed hierarchy, not a build: a mesh
        /// the build merges away is still conformed here, which costs nothing and is where the setting belongs.
        /// An offender under <c>Packages/</c> is named and never written; its presence makes the verdict
        /// <c>NOT-PASS</c> in either mode, because the scope will not end up conformed by this door.</summary>
        public static string Run(GameObject avatarRoot, bool whatIf = false)
        {
            if (avatarRoot == null)
                return "[ConformImportSettings] FAIL: avatar root is null: pass a placed scene root.";

            if (avatarRoot.CompareTag("EditorOnly"))
                return "[ConformImportSettings] FAIL: " + HierarchyPath(avatarRoot)
                     + " is tagged EditorOnly, so the panel never validates it and this scope would scan nothing; pass the avatar root itself.";

            var r = new Report { Target = HierarchyPath(avatarRoot), WhatIf = whatIf, AvatarScope = true };
            var set = CollectAvatarAssets(avatarRoot);
            Scan(set.Textures, set.Models, set.Clips, r);
            foreach (var f in r.Findings) f.Conformable = IsConformable(f.Path);
            if (!whatIf && r.Findings.Any(f => f.Conformable)) Apply(r);
            return Finish(r, RunLogFormat.Leaf(r.Target));
        }

        private static string RunFolder(string assetFolderPath, bool whatIf)
        {

            // The folder argument is the only bound on a write that is partly lossy, so the roots are refused by
            // name rather than trusted. `Assets` would clamp every oversize cap in the project; a `Packages` tree
            // is rewritten by `vrc-get resolve` anyway, so a write there is discarded rather than sanctioned.
            var norm = assetFolderPath.Replace('\\', '/').TrimEnd('/');
            if (string.Equals(norm, "Assets", StringComparison.OrdinalIgnoreCase)
                || string.Equals(norm, "Packages", StringComparison.OrdinalIgnoreCase)
                || norm.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
                return "[ConformImportSettings] FAIL: refusing an over-broad or non-durable root (" + assetFolderPath
                     + "): pass the specific vendor or owned folder to conform, e.g. Assets/Vendor/Outfits/<Name>.";

            var r = new Report { Target = assetFolderPath, WhatIf = whatIf };
            var roots = new[] { assetFolderPath };
            Scan(PathsOfType("t:Texture", roots), PathsOfType("t:Model", roots), PathsOfType("t:AudioClip", roots), r);
            foreach (var f in r.Findings) f.Conformable = true; // the root refusal above already bounded the scope
            if (!whatIf && r.Findings.Count > 0) Apply(r);
            return Finish(r, RunLogFormat.Leaf(assetFolderPath));
        }

        /// <summary>Whether this door may write an offender found through an avatar scope. The one class it
        /// refuses is <c>Packages/</c>: a VPM resolve rewrites that tree, so the write is discarded rather than
        /// sanctioned (the same reason the folder door refuses that root by name). Deliberately narrower than
        /// <c>TransplantCore.IsWritableAsset</c> — <c>Assets/Vendor/</c> IS writable for this class of setting
        /// (`docs/LAYOUT.md` §Vendor mutation). A test seam, restored by its test.</summary>
        internal static Func<string, bool> IsConformable = DefaultIsConformable;

        private static bool DefaultIsConformable(string assetPath)
        {
            var norm = (assetPath ?? "").Replace('\\', '/');
            return !(string.Equals(norm, "Packages", StringComparison.OrdinalIgnoreCase)
                     || norm.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase));
        }

        // ----- Avatar scope: the SDK panel's own asset set ------------------------------------

        private struct AssetSet
        {
            public List<string> Textures;
            public List<string> Models;
            public List<string> Clips;
        }

        private static AssetSet CollectAvatarAssets(GameObject root)
        {
            var textures = new HashSet<string>(StringComparer.Ordinal);
            var models = new HashSet<string>(StringComparer.Ordinal);
            var clips = new HashSet<string>(StringComparer.Ordinal);

            foreach (var go in WalkExcludingEditorOnly(root.transform))
            {
                // Meshes: exactly the three renderer kinds the SDK's GetAllMeshesInGameObjectHierarchy walks.
                // The error string the panel prints says "skinned meshes"; the predicate does not.
                foreach (var smr in go.GetComponents<SkinnedMeshRenderer>()) AddAssetPath(models, smr.sharedMesh);
                foreach (var mf in go.GetComponents<MeshFilter>()) AddAssetPath(models, mf.sharedMesh);
                foreach (var psr in go.GetComponents<ParticleSystemRenderer>())
                {
                    // Every slot, not `.mesh` (slot 0 only): the SDK allocates `meshCount` and calls GetMeshes.
                    var slots = new Mesh[psr.meshCount];
                    psr.GetMeshes(slots);
                    foreach (var m in slots) AddAssetPath(models, m);
                }

                // Textures: every texture slot on every material of every Renderer (both panel texture checks).
                foreach (var rend in go.GetComponents<Renderer>())
                    foreach (var m in rend.sharedMaterials)
                    {
                        if (m == null) continue;
                        foreach (int id in m.GetTexturePropertyNameIDs()) AddAssetPath(textures, m.GetTexture(id));
                    }

                foreach (var src in go.GetComponents<AudioSource>()) AddAssetPath(clips, src.clip);
            }

            textures.ExceptWith(models); // an FBX-embedded texture resolves to the .fbx path: already a model row
            return new AssetSet
            {
                Textures = textures.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                Models = models.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                Clips = clips.OrderBy(p => p, StringComparer.Ordinal).ToList(),
            };
        }

        private static void AddAssetPath(HashSet<string> into, UnityEngine.Object obj)
        {
            if (obj == null) return;
            var path = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(path)) into.Add(path); // a scene-only or built-in object has no importer
        }

        /// <summary>Every GameObject under (and including) <paramref name="root"/>, inactive included, skipping
        /// any subtree whose root carries the <c>EditorOnly</c> tag — the objects VRChat strips at upload and the
        /// panel therefore never validates.</summary>
        private static IEnumerable<GameObject> WalkExcludingEditorOnly(Transform root)
        {
            if (root.CompareTag("EditorOnly")) yield break;
            yield return root.gameObject;
            foreach (Transform child in root)
                foreach (var go in WalkExcludingEditorOnly(child)) yield return go;
        }

        private static string HierarchyPath(GameObject go)
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ----- Scanning -----------------------------------------------------------------------

        private static void Scan(IEnumerable<string> texturePaths, IEnumerable<string> modelPaths, IEnumerable<string> clipPaths, Report r)
        {
            int cap = MaxSdkTextureSize(r);
            foreach (var path in texturePaths)
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
                    // The SDK's predicate is importer-only, so a small source under a huge cap is an offender whose
                    // correction loses nothing. Deriving that from the real dimensions rather than the row id is
                    // what keeps the report from crying wolf on every hit.
                    //
                    // It must be the SOURCE dimensions, not the imported ones: an imported Texture is already
                    // clamped by the active platform's settings, so a 16K source behind a 4K Android override
                    // reads as 4096 and would report "costless" while lowering the default cap really does
                    // downscale it everywhere that override is absent. When the source cannot be measured, the
                    // path is reported rather than assumed safe — an unmeasurable dimension is not a small one.
                    //
                    // This is reported as a fact ("this texture is now capped lower than its source"), never as a
                    // render obligation: the upload is blocked without the fix, so there is no shipped baseline
                    // any render could be compared against.
                    int longest = SourceLongestEdge(ti, r);
                    bool downscales = longest <= 0 || longest > cap;
                    r.Add(path, RowMaxTextureSize,
                        "cap=" + ti.maxTextureSize + " > " + cap + ", source longest edge="
                        + (longest > 0 ? longest.ToString(CultureInfo.InvariantCulture) : "unmeasurable"),
                        downscales);
                }
            }

            var legacyProp = LegacyNormalsProperty(r);
            foreach (var path in modelPaths)
            {
                var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                if (mi == null) { r.Skipped++; continue; }
                r.Scanned++;

                if (!mi.isReadable)
                    r.Add(path, RowMeshReadable, "read/write disabled", false);

                // No disclosure on this row, deliberately. A "render check owed" needs a baseline to compare
                // against and there is none: the upload is BLOCKED without this fix, so no build carrying the old
                // setting ever shipped. The fix is also forced — the only other way to satisfy the SDK is changing
                // importBlendShapeNormals off Calculate, which is `own-base`'s standard anyway. Flagging it would
                // hand the reader an obligation they cannot discharge and a choice they do not have.
                bool legacySet;
                if (legacyProp != null
                    && mi.importBlendShapeNormals == ModelImporterNormals.Calculate
                    && TryReadLegacy(legacyProp, mi, r, path, out legacySet)
                    && !legacySet)
                    r.Add(path, RowLegacyNormals, "blendshape normals = Calculate without legacy", false);
            }

            foreach (var path in clipPaths)
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
            var byPath = r.Findings.Where(f => f.Conformable).GroupBy(f => f.Path).ToList();
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
            foreach (var f in r.Findings.Where(f => f.Conformable)) f.Persisted = !StillOffending(f, legacyProp, cap);
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
            public bool Downscales;
            public bool Persisted;
            public bool Conformable;   // false ⇒ named, never written (avatar scope, under Packages/)
        }

        private sealed class Report
        {
            public string Target;
            public bool WhatIf;
            public bool AvatarScope;
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

            public void Add(string path, string row, string detail, bool downscales)
            {
                Findings.Add(new Finding { Path = path, Row = row, Detail = detail, Downscales = downscales });
            }

            public void SkipRow(string row, string why)
            {
                if (!SkippedRows.Any(s => s.StartsWith(row, StringComparison.Ordinal)))
                    SkippedRows.Add(row + " (" + why + ")");
            }
        }

        private static string Finish(Report r, string label)
        {
            // Only findings this door acts on: a NOT CONFORMABLE path is named in its own clause, and counting it
            // under "conformed:" would claim a write that never happened.
            var perRow = AllRows
                .Select(row => new { row, n = r.Findings.Count(f => f.Row == row && f.Conformable) })
                .Where(x => x.n > 0)
                .Select(x => x.row + "=" + x.n)
                .ToList();
            string rows = perRow.Count == 0 ? "none" : string.Join(" ", perRow);

            var downscaled = r.Findings.Where(f => f.Downscales && f.Conformable).Select(f => f.Path).Distinct().ToList();
            var notPersisted = r.WhatIf
                ? new List<Finding>()
                : r.Findings.Where(f => f.Conformable && !f.Persisted).ToList();
            // Named in both modes and failing in both: the scope will not be conformed by this door, so a
            // whatIf PASS over it would argue with its own body.
            var notConformable = r.Findings.Where(f => !f.Conformable).Select(f => f.Path).Distinct().ToList();

            bool pass = notPersisted.Count == 0 && r.Unwritten.Count == 0 && r.WriteErrors.Count == 0
                     && notConformable.Count == 0;
            string result = pass ? "PASS" : "NOT-PASS";

            string verb = r.WhatIf ? "would conform" : "conformed";
            var sb = new StringBuilder();
            sb.Append("[ConformImportSettings] ").Append(label)
              .Append(r.WhatIf ? " (whatIf)" : "")
              .Append(": ").Append(r.Scanned).Append(" scanned");
            if (r.Skipped > 0) sb.Append(", ").Append(r.Skipped).Append(" not importer-typed");
            sb.Append(" | ").Append(verb).Append(": ").Append(rows);
            if (downscaled.Count > 0)
                sb.Append(" | CAPPED BELOW SOURCE on ").Append(downscaled.Count).Append(" path(s): ")
                  .Append(string.Join(", ", downscaled.Take(5)))
                  .Append(downscaled.Count > 5 ? ", …" : "");
            if (notConformable.Count > 0)
                sb.Append(" | NOT CONFORMABLE HERE (under Packages/, a VPM resolve reverts the write; copy the asset under Assets/ and repoint the reference, or accept the panel's own fix knowing a resolve reverts it) on ")
                  .Append(notConformable.Count).Append(" path(s): ")
                  .Append(string.Join(", ", notConformable.Take(5)))
                  .Append(notConformable.Count > 5 ? ", …" : "");
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
            sb.Append(" | ").Append(r.AvatarScope ? AvatarScopeNote : ScopeNote);
            sb.Append(" => ").Append(result);

            string summary = RunLogFormat.WriteRunLog(RunLogDir, "conformimportsettings_" + label, sb.ToString(), BuildLog(r, label, result), ".json");
            if (result == "PASS") Debug.Log(summary);
            else if (r.WhatIf) Debug.LogWarning(summary); // a preview wrote nothing; the verdict is the finding
            else Debug.LogError(summary);
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
            sb.Append("  \"scope\": ").Append(RunLogFormat.Q(r.AvatarScope ? AvatarScopeNote : ScopeNote)).Append(",\n");
            sb.Append("  \"scopeKind\": ").Append(RunLogFormat.Q(r.AvatarScope ? "avatar" : "folder")).Append(",\n");
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
                  .Append(", \"cappedBelowSource\": ").Append(f.Downscales ? "true" : "false")
                  .Append(", \"conformable\": ").Append(f.Conformable ? "true" : "false")
                  .Append(", \"written\": ").Append(r.WhatIf || !f.Conformable ? "false" : (r.Unwritten.Contains(f.Path) ? "false" : "true"))
                  .Append(", \"persisted\": ").Append(r.WhatIf || !f.Conformable ? "null" : (f.Persisted ? "true" : "false"))
                  .Append(" }").Append(i + 1 < r.Findings.Count ? "," : "").Append("\n");
            }
            sb.Append("  ],\n");
            sb.Append("  \"result\": ").Append(RunLogFormat.Q(result)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
