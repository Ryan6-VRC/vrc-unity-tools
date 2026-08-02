// Pins OwnMaterial's two Thry/Poiyomi guards — the lock detector and the dialog guard that stands in
// front of the reflected UnlockMaterials call.
//
// Why these need their own cases: Poiyomi is not installed in the test venue, so TryResolvePoiUnlock
// always misses and the unlock seam behind these guards can never run here. Every end-to-end path through
// Run() therefore exercises the guards' NO answers only incidentally and their YES answers not at all —
// the tool's behavior in this venue is identical whether the guards are right or catastrophically wrong.
// Same omission shape as ImportPackage.NameMatches before F36: a decision sitting inside a boundary the
// suite legitimately can't exercise, riding on its untestable caller instead of carrying its own case.
//
// OriginalShaderResolves is the higher-stakes of the two. It exists to mirror Thry's own two-step fallback
// BEFORE Thry reaches it, because Thry answers an unresolvable tag with a blocking DisplayDialog — which in
// an agent-driven headless run is not a prompt but a hang. Getting the precedence or either resolution step
// wrong means the tool walks into that modal exactly when it was built not to.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class OwnMaterialThryGuardTests
{
    const string TmpDir = "Assets/AgentOwnMaterialThryTmp";
    const string ShaderPath = TmpDir + "/F39ProbeShader.shader";
    // Declared name inside the .shader source — what Shader.Find resolves, distinct from the asset path.
    const string ShaderName = "Hidden/F39/ProbeShader";

    Shader _shader;
    string _shaderGuid;
    Material _mat;

    [SetUp]
    public void SetUp()
    {
        if (!AssetDatabase.IsValidFolder(TmpDir))
            AssetDatabase.CreateFolder("Assets", "AgentOwnMaterialThryTmp");

        // A real shader ASSET (not a builtin) so its GUID is a genuine, resolvable project GUID — the
        // builtin shaders live in unity_builtin_extra and do not give a usable one.
        System.IO.File.WriteAllText(ShaderPath,
            "Shader \"" + ShaderName + "\" { SubShader { Pass { } } }");
        AssetDatabase.ImportAsset(ShaderPath, ImportAssetOptions.ForceSynchronousImport);

        _shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        Assert.IsNotNull(_shader, "fixture shader must import — the GUID legs below are meaningless without it");
        _shaderGuid = AssetDatabase.AssetPathToGUID(ShaderPath);
        Assert.IsNotEmpty(_shaderGuid, "fixture shader must have a GUID");

        _mat = new Material(_shader);
    }

    [TearDown]
    public void TearDown()
    {
        if (_mat != null) Object.DestroyImmediate(_mat);
        if (AssetDatabase.IsValidFolder(TmpDir)) AssetDatabase.DeleteAsset(TmpDir);
        AssetDatabase.Refresh();
    }

    // ----- OriginalShaderResolves: the dialog guard --------------------------------------------

    // No tags at all — nothing to restore, so the unlock must not be attempted.
    [Test]
    public void OriginalShaderResolves_noTags_isFalse()
    {
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.IsEmpty(tag ?? "", "with no tags there is no tag value to name in the refusal");
    }

    // The GUID leg's happy path: tag resolves through GUIDToAssetPath → LoadAssetAtPath<Shader>.
    [Test]
    public void OriginalShaderResolves_liveGuidTag_isTrue()
    {
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShaderGuid, _shaderGuid);
        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(_shaderGuid, tag);
    }

    // THE case this guard exists for: a poi reinstall or version bump leaves the tag present but its GUID
    // pointing at nothing. Presence is not resolution — treating it as resolution is what walks into Thry's
    // modal. The refusal must name the stale GUID, since that is the operator's only handle on the problem.
    [Test]
    public void OriginalShaderResolves_staleGuidTag_isFalse_andNamesTheGuid()
    {
        const string dead = "00000000000000000000000000000000";
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShaderGuid, dead);
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(dead, tag, "the refusal must name the tag it could not resolve");
    }

    // The NAME leg's happy path, reached when no GUID tag is present.
    [Test]
    public void OriginalShaderResolves_liveNameTag_isTrue()
    {
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShader, ShaderName);
        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(ShaderName, tag);
    }

    // A renamed shader: the name tag survives, Shader.Find does not.
    [Test]
    public void OriginalShaderResolves_deadNameTag_isFalse_andNamesTheName()
    {
        const string gone = "Hidden/F39/NoSuchShaderAnywhere";
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShader, gone);
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(gone, tag);
    }

    // The load-bearing precedence: a STALE GUID must fall through to a live NAME rather than refusing.
    // This is the ordering Thry itself uses, and the whole reason the guard is a two-step rather than one.
    // Collapse it to GUID-only and a recoverable material gets refused; collapse it to name-only and a
    // material Thry could restore by GUID takes the slow path.
    [Test]
    public void OriginalShaderResolves_staleGuid_fallsThroughToLiveName()
    {
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShaderGuid, "00000000000000000000000000000000");
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShader, ShaderName);

        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag),
            "a dead GUID beside a live name must resolve via the name, not refuse");
        Assert.AreEqual(ShaderName, tag, "the resolving tag is the one reported");
    }

    // Both tags present, neither resolves: refuse, and name the GUID — it is the more specific handle.
    [Test]
    public void OriginalShaderResolves_bothTagsDead_isFalse_andPrefersNamingTheGuid()
    {
        const string dead = "00000000000000000000000000000000";
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShaderGuid, dead);
        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShader, "Hidden/F39/AlsoGone");

        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(dead, tag);
    }

    // ----- IsLocked: the lock detector ----------------------------------------------------------

    // Locked is a fact about the SHADER NAME, never the tag. A poi material carries the OriginalShader tag
    // whether or not it is locked, so keying on tag presence would report every poi material as locked.
    [Test]
    public void IsLocked_readsTheShaderName_notTheTag()
    {
        Assert.IsFalse(OwnMaterial.IsLocked(_mat), "an unlocked material on a normally-named shader");

        _mat.SetOverrideTag(OwnMaterial.ThryTagOriginalShader, ShaderName);
        Assert.IsFalse(OwnMaterial.IsLocked(_mat),
            "the OriginalShader tag is present on unlocked poi materials too — it is not a lock signal");
    }

    // The positive case, plus the near-miss beside it: the fixture shader is declared "Hidden/F39/…" —
    // Hidden, but not Hidden/Locked/ — which is what a check loosened to "Hidden/" would wrongly catch.
    [Test]
    public void IsLocked_requiresTheHiddenLockedPrefix()
    {
        const string lockedPath = TmpDir + "/F39LockedShader.shader";
        System.IO.File.WriteAllText(lockedPath,
            "Shader \"Hidden/Locked/F39Probe\" { SubShader { Pass { } } }");
        AssetDatabase.ImportAsset(lockedPath, ImportAssetOptions.ForceSynchronousImport);

        var lockedShader = AssetDatabase.LoadAssetAtPath<Shader>(lockedPath);
        Assert.IsNotNull(lockedShader, "locked-name fixture shader must import");

        var lockedMat = new Material(lockedShader);
        try
        {
            Assert.IsTrue(OwnMaterial.IsLocked(lockedMat),
                "a 'Hidden/Locked/' shader name is the lock signal");
            Assert.IsFalse(OwnMaterial.IsLocked(_mat),
                "'Hidden/' alone is not locked — only the 'Hidden/Locked/' prefix is");
        }
        finally { Object.DestroyImmediate(lockedMat); }
    }

    // A material whose shader failed to load at all must not read as locked (it would send a broken
    // material down the unlock seam instead of failing on its own terms).
    [Test]
    public void IsLocked_nullShader_isFalse()
    {
        _mat.shader = null;
        Assert.IsFalse(OwnMaterial.IsLocked(_mat));
    }
}
