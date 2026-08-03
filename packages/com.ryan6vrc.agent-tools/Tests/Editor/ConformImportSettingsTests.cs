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

    [TearDown]
    public void TearDown()
    {
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
        var s = ConformImportSettings.RunFolder("Assets/DoesNotExist");
        Assert.That(s, Does.StartWith("[ConformImportSettings] FAIL:"));
        Assert.That(s, Does.Not.Contain("| log="), "a bad-input early return must not claim a RunLog");
    }

    [Test]
    public void EmptyPath_IsBareFail()
    {
        Assert.That(ConformImportSettings.RunFolder(""), Does.StartWith("[ConformImportSettings] FAIL:"));
    }

    // ── Clean folder: PASS, nothing to do ──────────────────────────────────────────────────────────────

    [Test]
    public void FolderWithNothingToConform_PassesWithNoRows()
    {
        var s = ConformImportSettings.RunFolder(TmpDir);
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

        var s = ConformImportSettings.RunFolder(TmpDir);
        Assert.That(s, Does.Contain("mip-streaming=1"));
        Assert.That(s, Does.Contain("=> PASS"));
        Assert.That(((TextureImporter)AssetImporter.GetAtPath(path)).streamingMipmaps, Is.True);
    }

    [Test]
    public void WhatIf_ReportsTheRow_AndWritesNothing()
    {
        var path = WritePng("preview.png");

        var s = ConformImportSettings.RunFolder(TmpDir, whatIf: true);
        Assert.That(s, Does.Contain("(whatIf)"));
        Assert.That(s, Does.Contain("would conform: mip-streaming=1"));
        Assert.That(((TextureImporter)AssetImporter.GetAtPath(path)).streamingMipmaps, Is.False,
            "whatIf must not touch the importer");
    }

    // ── max-texture-size, and the honesty of the render-check disclosure ───────────────────────────────

    [Test]
    public void OversizeCapOnSmallSource_IsConformed_ButNotReportedAsChangingWhatShips()
    {
        // The SDK's predicate is importer-only, so a 64px source with a 16384 cap is an offender whose
        // correction is cost-free. Deriving the disclosure from the row id instead of the real dimensions
        // would warn here, training the reader to ignore the warning that matters.
        var path = WritePng("smallsource.png");
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.maxTextureSize = 16384;
        AssetDatabase.WriteImportSettingsIfDirty(path);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        var s = ConformImportSettings.RunFolder(TmpDir);
        Assert.That(s, Does.Contain("max-texture-size=1"));
        Assert.That(s, Does.Not.Contain("CHANGES WHAT SHIPS"),
            "a cap above a small source loses no pixels — do not claim a render check is owed");
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

        var s = ConformImportSettings.RunFolder(TmpDir);
        Assert.That(s, Does.Contain("mesh-readable=1"));
        Assert.That(((ModelImporter)AssetImporter.GetAtPath(path)).isReadable, Is.True);
    }

    // ── legacy-blendshape-normals, including its disclosure ────────────────────────────────────────────

    [Test]
    public void ModelAtCalculateWithoutLegacy_IsConformed_AndDisclosedAsChangingWhatShips()
    {
        var path = WriteObj("normals.obj");
        var mi = (ModelImporter)AssetImporter.GetAtPath(path);
        mi.importBlendShapeNormals = ModelImporterNormals.Calculate;
        AssetDatabase.WriteImportSettingsIfDirty(path);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        var s = ConformImportSettings.RunFolder(TmpDir);
        Assert.That(s, Does.Contain("legacy-blendshape-normals=1"));
        Assert.That(s, Does.Contain("CHANGES WHAT SHIPS"),
            "recomputing normals shifts shading — the disclosure is the whole obligation for this row");
        Assert.That(s, Does.Contain(path));
    }

    [Test]
    public void ModelNotAtCalculate_IsNotAnOffenderForTheNormalsRow()
    {
        var path = WriteObj("importnormals.obj");
        var mi = (ModelImporter)AssetImporter.GetAtPath(path);
        mi.importBlendShapeNormals = ModelImporterNormals.None;
        AssetDatabase.WriteImportSettingsIfDirty(path);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        var s = ConformImportSettings.RunFolder(TmpDir, whatIf: true);
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

        var s = ConformImportSettings.RunFolder(TmpDir);
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

        var s = ConformImportSettings.RunFolder(TmpDir);
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
        ConformImportSettings.RunFolder(TmpDir);

        var s = ConformImportSettings.RunFolder(TmpDir);
        Assert.That(s, Does.Contain("conformed: none"),
            "the door is re-runnable by design — a second pass on a clean folder must be a no-op");
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
