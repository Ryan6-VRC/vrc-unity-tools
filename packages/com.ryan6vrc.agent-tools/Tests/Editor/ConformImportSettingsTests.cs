using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// ConformImportSettings proof obligations. Every row is asserted against a REAL importer on a synthesized
// asset rather than a mocked predicate, because the whole tool exists to move on-disk importer state — a
// unit test over the predicate alone would pass while the .meta write silently failed to persist, which is
// the exact failure the tool's disk re-read guards against.
//
// Fixture assets are generated, not committed: the repo keeps no binaries (docs/LAYOUT.md), and all three
// importer types can be produced from bytes — PNG via EncodeToPNG, a plain-text .obj (Unity assigns it a
// ModelImporter), and a hand-written RIFF/WAV header. Measured on 2022.3.22f1: each lands as a genuine
// offender with default import settings, which is why the arrange steps mostly do nothing.
//
// What is deliberately NOT asserted: that a write which fails to persist reports NOT-PASS. Provoking a
// non-persisting importer would mean finding an asset type whose setter lies, and pinning one here would
// bake in an assumption about Unity internals this suite cannot justify. The disk re-read is asserted
// positively instead — the reported count comes back from the importer, not from the write call.
[Category("ConformImportSettings")]
public class ConformImportSettingsTests
{
    private const string TmpName = "AgentConformTmp";
    private const string TmpDir = "Assets/" + TmpName;

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.CreateFolder("Assets", TmpName);
    }

    // Scene objects and in-memory materials the avatar-scope tests build; destroyed after each test.
    private readonly System.Collections.Generic.List<Object> _sceneObjects = new System.Collections.Generic.List<Object>();
    private System.Func<string, bool> _isConformable;

    [TearDown]
    public void TearDown()
    {
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
        if (_isConformable != null) ConformImportSettings.IsConformable = _isConformable;
        foreach (var o in _sceneObjects) if (o != null) Object.DestroyImmediate(o);
        _sceneObjects.Clear();
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        AssetDatabase.Refresh();
        if (!Directory.Exists(RunLogFormat.RunLogDir)) return;
        foreach (var f in Directory.GetFiles(RunLogFormat.RunLogDir, "conformimportsettings_" + TmpName + "_*"))
            File.Delete(f);
    }

    // ── Bad input: bare FAIL, no RunLog trailer ────────────────────────────────────────────────────────

    [Test]
    public void NonFolderPath_IsBareFail_WithNoTrailer()
    {
        var s = ConformImportSettings.Run("Assets/DoesNotExist");
        Assert.That(s, Does.StartWith("[ConformImportSettings] FAIL:"));
        Assert.That(s, Does.Not.Contain("| log="), "a bad-input early return must not claim a RunLog");
    }

    [Test]
    public void EmptyPath_IsBareFail()
    {
        Assert.That(ConformImportSettings.Run(""), Does.StartWith("[ConformImportSettings] FAIL:"));
    }

    [Test]
    public void OverBroadRoots_AreRefusedByName()
    {
        // The folder string is the only bound on a write that is partly lossy, so the roots are the one input
        // that must not be trusted: `Assets` would clamp every oversize cap in the project, and a `Packages`
        // write is discarded by the next `vrc-get resolve` anyway.
        // `Packages` alone is not a valid asset folder, so it is refused one guard earlier with the
        // invalid-folder message — refused either way, which is what matters.
        foreach (var root in new[] { "Assets", "Assets/", "Packages", "Packages/com.vrchat.avatars" })
        {
            var s = ConformImportSettings.Run(root);
            Assert.That(s, Does.StartWith("[ConformImportSettings] FAIL:"), "root not refused: " + root);
            Assert.That(s, Does.Not.Contain("| log="), "a refusal must not claim a RunLog: " + root);
        }
        foreach (var root in new[] { "Assets", "Packages/com.vrchat.avatars" })
            Assert.That(ConformImportSettings.Run(root), Does.Contain("pass the specific"),
                "a refusal on a real folder must name the fix: " + root);
    }

    // ── Clean folder: PASS, nothing to do ──────────────────────────────────────────────────────────────

    [Test]
    public void FolderWithNothingToConform_PassesWithNoRows()
    {
        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("conformed: none"));
        Assert.That(s, Does.Contain("=> PASS"));
        Assert.That(s, Does.Contain("| log="), "a real run must carry its RunLog path in-band");
    }

    // ── mip-streaming ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void DefaultImportedPng_IsAMipStreamingOffender_AndIsConformed()
    {
        var path = WritePng("mip.png");
        var before = (TextureImporter)AssetImporter.GetAtPath(path);
        Assert.That(before.mipmapEnabled, Is.True, "fixture premise: mipmaps on by default");
        Assert.That(before.streamingMipmaps, Is.False, "fixture premise: streaming off by default");

        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("mip-streaming=1"));
        Assert.That(s, Does.Contain("=> PASS"));
        Assert.That(((TextureImporter)AssetImporter.GetAtPath(path)).streamingMipmaps, Is.True);
    }

    [Test]
    public void WhatIf_ReportsTheRow_AndWritesNothing()
    {
        var path = WritePng("preview.png");

        var s = ConformImportSettings.Run(TmpDir, whatIf: true);
        Assert.That(s, Does.Contain("(whatIf)"));
        Assert.That(s, Does.Contain("would conform: mip-streaming=1"));
        Assert.That(((TextureImporter)AssetImporter.GetAtPath(path)).streamingMipmaps, Is.False,
            "whatIf must not touch the importer");
    }

    // ── max-texture-size, and the honesty of the capped-below-source report ────────────────────────────

    [Test]
    public void OversizeCapOnSmallSource_IsConformed_AndNotReportedAsCappedBelowSource()
    {
        // The SDK's predicate is importer-only, so a 64px source with a 16384 cap is an offender whose
        // correction loses nothing. Reporting it off the row id rather than the real source dimensions would
        // fire here, and a report that fires on every hit is one nobody reads.
        var path = WritePng("smallsource.png");
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.maxTextureSize = 16384;
        AssetDatabase.WriteImportSettingsIfDirty(path);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("max-texture-size=1"));
        Assert.That(s, Does.Not.Contain("CAPPED BELOW SOURCE"),
            "a cap above a small source loses no pixels — reporting it would cry wolf on every hit");
        Assert.That(((TextureImporter)AssetImporter.GetAtPath(path)).maxTextureSize, Is.EqualTo(8192));
    }

    // ── mesh-readable ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ObjWithReadWriteDisabled_IsConformed()
    {
        var path = WriteObj("mesh.obj");
        var mi = AssetImporter.GetAtPath(path) as ModelImporter;
        Assert.That(mi, Is.Not.Null, "fixture premise: a plain-text .obj gets a ModelImporter");
        Assert.That(mi.isReadable, Is.False, "fixture premise: read/write off by default");

        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("mesh-readable=1"));
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(path)).isReadable, Is.True);
    }

    // ── legacy-blendshape-normals ──────────────────────────────────────────────────────────────────────

    [Test]
    public void ModelAtCalculateWithoutLegacy_IsConformed_AndTheReflectedWriteReachesDisk()
    {
        var path = WriteObj("normals.obj");
        var mi = (ModelImporter)AssetImporter.GetAtPath(path);
        Assert.That(mi.importBlendShapeNormals, Is.EqualTo(ModelImporterNormals.Calculate),
            "fixture premise: Unity's default is Calculate, which is what makes this row fire at all");
        Assert.That(ReadLegacyFlag(path), Is.False, "fixture premise: legacy off by default");

        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("legacy-blendshape-normals=1"));
        Assert.That(s, Does.Contain("=> PASS"),
            "this row writes through a reflected private member — a setter that fails to dirty the .meta lands as NOT-PASS");
        Assert.That(s, Does.Not.Contain("CAPPED BELOW SOURCE"));
        // The only row whose write goes through reflection is the only one worth reading back through it.
        Assert.That(ReadLegacyFlag(path), Is.True, "the reflected write must reach disk, not just return");
    }

    [Test]
    public void LegacyNormalsRow_NeverClaimsAResolutionLossOrARenderDebt()
    {
        // Nothing to compare against: the upload is blocked without this fix, so no build carrying the old
        // setting ever shipped, and the only alternative remedy (importBlendShapeNormals off Calculate) is a
        // different authoring decision this door does not make. A warning here would be undischargeable.
        WriteObj("normals-only.obj");
        var s = ConformImportSettings.Run(TmpDir, whatIf: true);
        Assert.That(s, Does.Contain("legacy-blendshape-normals=1"));
        Assert.That(s, Does.Not.Contain("CAPPED BELOW SOURCE"));
        Assert.That(s, Does.Not.Contain("render check"));
    }

    private static bool ReadLegacyFlag(string path)
    {
        var mi = (ModelImporter)AssetImporter.GetAtPath(path);
        var p = typeof(ModelImporter).GetProperty(
            "legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.That(p, Is.Not.Null, "fixture premise: the pinned member exists on this Unity");
        return (bool)p.GetValue(mi, null);
    }

    [Test]
    public void ModelNotAtCalculate_IsNotAnOffenderForTheNormalsRow()
    {
        var path = WriteObj("importnormals.obj");
        var mi = (ModelImporter)AssetImporter.GetAtPath(path);
        mi.importBlendShapeNormals = ModelImporterNormals.None;
        AssetDatabase.WriteImportSettingsIfDirty(path);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        var s = ConformImportSettings.Run(TmpDir, whatIf: true);
        Assert.That(s, Does.Not.Contain("legacy-blendshape-normals"),
            "the SDK gates this row on Calculate; ours must too, or we rewrite settings the SDK accepts");
    }

    // ── audio-background-load ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void DecompressOnLoadClipWithoutBackgroundLoad_IsConformed()
    {
        var path = WriteWav("clip.wav");
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        Assert.That(clip, Is.Not.Null, "fixture premise: the hand-written WAV imports");
        Assert.That(clip.loadType, Is.EqualTo(AudioClipLoadType.DecompressOnLoad), "fixture premise");
        Assert.That(clip.loadInBackground, Is.False, "fixture premise");

        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("audio-background-load=1"));
        Assert.That(((AudioImporter)AssetImporter.GetAtPath(path)).loadInBackground, Is.True);
        Assert.That(AssetDatabase.LoadAssetAtPath<AudioClip>(path).loadInBackground, Is.True,
            "the clip is what the SDK tests — assert the flag reached the imported result, not just the importer");
    }

    // ── All rows in one sweep, and the scope note ─────────────────────────────────────────────────────

    [Test]
    public void OneSweepConformsEveryAssetType_AndAlwaysBoundsItsZero()
    {
        WritePng("all.png");
        WriteObj("all.obj");
        WriteWav("all.wav");

        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("mip-streaming=1"));
        Assert.That(s, Does.Contain("mesh-readable=1"));
        Assert.That(s, Does.Contain("audio-background-load=1"));
        Assert.That(s, Does.Contain("scope=t:Texture,t:Model,t:AudioClip under this folder only"),
            "a count is meaningless without the scope it covers");
    }

    [Test]
    public void RerunAfterConforming_FindsNothing()
    {
        WritePng("idem.png");
        WriteObj("idem.obj");
        ConformImportSettings.Run(TmpDir);

        var s = ConformImportSettings.Run(TmpDir);
        Assert.That(s, Does.Contain("conformed: none"),
            "the door is re-runnable by design — a second pass on a clean folder must be a no-op");
    }

    // ── Avatar scope: the SDK panel's own asset set, and the one class it names but never writes ────────

    [Test]
    public void AvatarScope_CollectsMeshesTheWayTheSdkDoes_ThenConformsOnlyThose()
    {
        // Four unreadable OBJs; the SDK panel's mesh walk reaches three of them.
        var filterMesh = WriteObj("filter.obj");        // MeshFilter on an active child
        var skinnedMesh = WriteObj("skinned.obj");      // SkinnedMeshRenderer on an INACTIVE child (walked)
        var particleMesh = WriteObj("particle.obj");    // ParticleSystemRenderer mesh (walked, despite "skinned" wording)
        var editorOnlyMesh = WriteObj("editoronly.obj"); // MeshFilter under an EditorOnly subtree (stripped at upload)

        var root = NewGo("ConformScopeRoot");
        NewGo("Filter", root).AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(filterMesh);
        var skinnedGo = NewGo("Skinned", root);
        skinnedGo.AddComponent<SkinnedMeshRenderer>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(skinnedMesh);
        skinnedGo.SetActive(false);
        var particleGo = NewGo("Particles", root);
        particleGo.AddComponent<ParticleSystem>();
        var psr = particleGo.GetComponent<ParticleSystemRenderer>();
        psr.renderMode = ParticleSystemRenderMode.Mesh;
        var particleMesh2 = WriteObj("particle2.obj");  // slot 1 — the SDK walks every slot, `.mesh` is slot 0 only
        psr.SetMeshes(new[] { AssetDatabase.LoadAssetAtPath<Mesh>(particleMesh), AssetDatabase.LoadAssetAtPath<Mesh>(particleMesh2) });
        var editorOnly = NewGo("Tooling", root);
        editorOnly.tag = "EditorOnly";
        NewGo("Gizmo", editorOnly).AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(editorOnlyMesh);

        var preview = ConformImportSettings.Run(root, whatIf: true);
        Assert.That(preview, Does.Contain("(whatIf)"));
        Assert.That(preview, Does.Contain("would conform: mesh-readable=4"),
            "filter + inactive skinned + both particle slots are the panel's set; the EditorOnly subtree is not");
        Assert.That(preview, Does.Contain("EditorOnly subtrees excluded"), "the scope note must say what was walked");
        Assert.That(preview, Does.Contain("=> PASS"));
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(filterMesh)).isReadable, Is.False, "whatIf must not write");

        var applied = ConformImportSettings.Run(root);
        Assert.That(applied, Does.Contain("conformed: mesh-readable=4"));
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(particleMesh2)).isReadable, Is.True, "particle slot 1 is in the panel's set");
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(filterMesh)).isReadable, Is.True);
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(skinnedMesh)).isReadable, Is.True);
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(particleMesh)).isReadable, Is.True);
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(editorOnlyMesh)).isReadable, Is.False,
            "an avatar scope writes only what the avatar ships — the EditorOnly mesh is untouched");
    }

    [Test]
    public void AvatarScope_TexturesReachThroughMaterials_AndClipsThroughAudioSources()
    {
        var png = WritePng("albedo.png");
        var wav = WriteWav("voice.wav");

        var root = NewGo("ConformScopeRoot");
        var mat = new Material(Shader.Find("Standard"));
        _sceneObjects.Add(mat);
        mat.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(png);
        var meshGo = NewGo("Body", root);
        meshGo.AddComponent<MeshFilter>();
        meshGo.AddComponent<MeshRenderer>().sharedMaterial = mat;
        NewGo("Voice", root).AddComponent<AudioSource>().clip = AssetDatabase.LoadAssetAtPath<AudioClip>(wav);

        var s = ConformImportSettings.Run(root, whatIf: true);
        Assert.That(s, Does.Contain("mip-streaming=1"), "a texture is in scope through the material that binds it");
        Assert.That(s, Does.Contain("audio-background-load=1"), "a clip is in scope through the AudioSource that plays it");
    }

    [Test]
    public void AvatarScope_OffenderItMayNotWrite_IsNamedAndFailsInBothModes_AndIsNeverWritten()
    {
        var mesh = WriteObj("vendorpkg.obj");
        var root = NewGo("ConformScopeRoot");
        NewGo("Prop", root).AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(mesh);

        // The real predicate refuses Packages/; a test cannot plant an offender there, so the seam marks
        // this fixture asset unconformable and the plumbing behind the predicate is what gets asserted.
        _isConformable = ConformImportSettings.IsConformable;
        ConformImportSettings.IsConformable = path => path != mesh;
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true; // a NOT-PASS verdict is logged at error level by design

        var preview = ConformImportSettings.Run(root, whatIf: true);
        Assert.That(preview, Does.Contain("NOT CONFORMABLE HERE"));
        Assert.That(preview, Does.Contain(mesh));
        Assert.That(preview, Does.Contain("=> NOT-PASS"), "a whatIf PASS over an offender this door cannot fix would argue with its own body");

        var applied = ConformImportSettings.Run(root);
        Assert.That(applied, Does.Contain("=> NOT-PASS"));
        Assert.That(applied, Does.Contain("conformed: none"), "the headline counts only what was written");
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(mesh)).isReadable, Is.False, "named, never written");
    }

    [Test]
    public void DefaultConformablePredicate_RefusesOnlyPackages()
    {
        Assert.That(ConformImportSettings.IsConformable("Packages/com.llealloo.audiolink/Samples/Mesh.fbx"), Is.False);
        Assert.That(ConformImportSettings.IsConformable("Packages"), Is.False);
        Assert.That(ConformImportSettings.IsConformable("Assets/Vendor/Outfits/X/Mesh.fbx"), Is.True,
            "Assets/Vendor is writable for this class of setting (LAYOUT.md §Vendor mutation)");
        Assert.That(ConformImportSettings.IsConformable("Assets/PackagesLike/Mesh.fbx"), Is.True);
    }

    [Test]
    public void StringScope_ResolvesAPlacedRoot_AndRefusesWhatIsNeither()
    {
        var mesh = WriteObj("byname.obj");
        var root = NewGo("ConformScopeRoot");
        NewGo("Prop", root).AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(mesh);

        var byPath = ConformImportSettings.Run("ConformScopeRoot", whatIf: true);
        Assert.That(byPath, Does.Contain("would conform: mesh-readable=1"), "a hierarchy path is a scope");

        var neither = ConformImportSettings.Run("NoSuchFolderOrObject", whatIf: true);
        Assert.That(neither, Does.StartWith("[ConformImportSettings] FAIL:"));
        Assert.That(neither, Does.Contain("asset folder").And.Contain("scene object"), "the refusal names both readings");
        Assert.That(neither, Does.Not.Contain("| log="));
    }

    [Test]
    public void AvatarScope_RootTaggedEditorOnly_IsBareFail_NotASilentPass()
    {
        var root = NewGo("ConformScopeRoot");
        root.tag = "EditorOnly";
        var s = ConformImportSettings.Run(root, whatIf: true);
        Assert.That(s, Does.StartWith("[ConformImportSettings] FAIL:").And.Contain("EditorOnly"));
    }

    [Test]
    public void StringScope_AmbiguousName_IsBareFailNamingEachMatch()
    {
        var a = NewGo("ConformScopeRoot");
        var b = NewGo("ConformScopeRootTwin");
        NewGo("Body", a);
        NewGo("Body", b);
        var s = ConformImportSettings.Run("Body", whatIf: true);
        Assert.That(s, Does.StartWith("[ConformImportSettings] FAIL:").And.Contain("ConformScopeRoot/Body").And.Contain("ConformScopeRootTwin/Body"));
    }

    [Test]
    public void AvatarScope_NullRoot_IsBareFail()
    {
        Assert.That(ConformImportSettings.Run((GameObject)null), Does.StartWith("[ConformImportSettings] FAIL:"));
    }

    private GameObject NewGo(string name, GameObject parent = null)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent.transform, false);
        else _sceneObjects.Add(go); // roots own their subtree's destruction
        return go;
    }

    // ── Fixture builders ──────────────────────────────────────────────────────────────────────────────

    private static string WritePng(string name)
    {
        var path = TmpDir + "/" + name;
        var tex = new Texture2D(64, 64);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return path;
    }

    private static string WriteObj(string name)
    {
        var path = TmpDir + "/" + name;
        File.WriteAllText(path, "o probe\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return path;
    }

    private static string WriteWav(string name)
    {
        var path = TmpDir + "/" + name;
        const int sampleRate = 8000;
        const int numSamples = 4000;
        const short bits = 16;
        const short channels = 1;
        int dataLen = numSamples * channels * (bits / 8);
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(new[] { 'R', 'I', 'F', 'F' });
            bw.Write(36 + dataLen);
            bw.Write(new[] { 'W', 'A', 'V', 'E' });
            bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16);
            bw.Write((short)1);
            bw.Write(channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * channels * (bits / 8));
            bw.Write((short)(channels * (bits / 8)));
            bw.Write(bits);
            bw.Write(new[] { 'd', 'a', 't', 'a' });
            bw.Write(dataLen);
            for (int i = 0; i < numSamples; i++) bw.Write((short)(Mathf.Sin(i * 0.05f) * 8000f));
            bw.Flush();
            File.WriteAllBytes(path, ms.ToArray());
        }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return path;
    }
}
