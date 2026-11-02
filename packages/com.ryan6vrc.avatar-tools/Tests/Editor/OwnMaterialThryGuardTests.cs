// Pins OwnMaterial.OriginalShaderResolves, the dialog guard: Thry answers an unresolvable original-shader tag
// with a blocking DisplayDialog, which in an agent-driven headless run is a hang, so the guard mirrors Thry's
// fallback order and refuses first. Poiyomi is not installed in the test venue, so TryResolvePoiUnlock
// short-circuits before Run consults the guard and these direct calls are its only coverage. Also pins the
// IsLocked near miss OwnMaterialTests cannot express.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class OwnMaterialThryGuardTests
{
    const string Seed = "Assets/Agent/Scratch/OwnMatThryGuardSeed";
    const string ProbeShaderPath = Seed + "/F39Probe.shader";
    const string LockedShaderPath = Seed + "/F39Locked.shader";
    // The probe is Hidden/ but NOT Hidden/Locked/: the near miss a loosened prefix check would swallow.
    const string ProbeShaderName = "Hidden/F39/ProbeShader";
    const string LockedShaderName = "Hidden/Locked/F39Probe";

    // Thry's tag names as LITERALS, deliberately: arranging via OwnMaterial's own constants would make test and
    // production read the same symbol, and the value — whether it matches Thry's actual TAG_* — is the only
    // thing about those constants that can be wrong.
    const string TagGuid = "OriginalShaderGUID";
    const string TagName = "OriginalShader";

    const string DeadGuid = "00000000000000000000000000000000";

    static Shader _probe, _locked;
    static string _probeGuid;

    Material _mat;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        AnimatorTestHelpers.EnsureFolder(Seed);
        _probe = BuildShader(ProbeShaderPath, ProbeShaderName);
        _locked = BuildShader(LockedShaderPath, LockedShaderName);
        _probeGuid = AssetDatabase.AssetPathToGUID(ProbeShaderPath);
        Assert.IsNotEmpty(_probeGuid, "probe shader must have a GUID — the GUID legs are meaningless without one");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _probe = null; _locked = null;
        AssetDatabase.DeleteAsset(Seed);
    }

    // A real shader ASSET, not a builtin: builtins live in unity_builtin_extra and give no usable GUID.
    static Shader BuildShader(string path, string declaredName)
    {
        System.IO.File.WriteAllText(path, "Shader \"" + declaredName + "\" { SubShader { Pass { } } }");
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var s = AssetDatabase.LoadAssetAtPath<Shader>(path);
        Assert.IsNotNull(s, "fixture shader must import: " + path);
        return s;
    }

    [SetUp]
    public void SetUp() => _mat = new Material(_probe);

    [TearDown]
    public void TearDown()
    {
        if (_mat != null) Object.DestroyImmediate(_mat);
    }

    // ----- OriginalShaderResolves: the dialog guard --------------------------------------------

    [Test]
    public void OriginalShaderResolves_noTags_isFalse()
    {
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.IsEmpty(tag ?? "", "with no tags there is no tag value to name in the refusal");
    }

    // Also pins OwnMaterial's ThryTagOriginalShaderGuid constant: a typo there fails here.
    [Test]
    public void OriginalShaderResolves_liveGuidTag_isTrue()
    {
        _mat.SetOverrideTag(TagGuid, _probeGuid);
        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(_probeGuid, tag);
    }

    // THE case this guard exists for: a Poiyomi reinstall leaves the tag present but its GUID pointing at
    // nothing. Treating presence as resolution is what walks into Thry's modal.
    [Test]
    public void OriginalShaderResolves_staleGuidTag_isFalse_andNamesTheGuid()
    {
        _mat.SetOverrideTag(TagGuid, DeadGuid);
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(DeadGuid, tag, "the refusal must name the tag it could not resolve");
    }

    // Also pins OwnMaterial's ThryTagOriginalShader constant: a typo there fails here.
    [Test]
    public void OriginalShaderResolves_liveNameTag_isTrue()
    {
        _mat.SetOverrideTag(TagName, ProbeShaderName);
        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(ProbeShaderName, tag);
    }

    // A renamed shader: the name tag survives, Shader.Find does not.
    [Test]
    public void OriginalShaderResolves_deadNameTag_isFalse_andNamesTheName()
    {
        const string gone = "Hidden/F39/NoSuchShaderAnywhere";
        _mat.SetOverrideTag(TagName, gone);
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(gone, tag);
    }

    // The load-bearing precedence, and the ordering Thry itself uses: a STALE GUID falls through to a live NAME
    // rather than refusing.
    [Test]
    public void OriginalShaderResolves_staleGuid_fallsThroughToLiveName()
    {
        _mat.SetOverrideTag(TagGuid, DeadGuid);
        _mat.SetOverrideTag(TagName, ProbeShaderName);

        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag),
            "a dead GUID beside a live name must resolve via the name, not refuse");
        Assert.AreEqual(ProbeShaderName, tag, "the resolving tag is the one reported");
    }

    [Test]
    public void OriginalShaderResolves_bothTagsDead_isFalse_andPrefersNamingTheGuid()
    {
        _mat.SetOverrideTag(TagGuid, DeadGuid);
        _mat.SetOverrideTag(TagName, "Hidden/F39/AlsoGone");

        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(DeadGuid, tag);
    }

    // ----- IsLocked: only what OwnMaterialTests cannot express ----------------------------------

    // OwnMaterialTests proves a Hidden/Locked/ shader reads locked; nothing there proves a merely-Hidden/ one
    // does not, so loosening the prefix to "Hidden/" passes that whole file and fails only here.
    [Test]
    public void IsLocked_requiresTheFullHiddenLockedPrefix_notJustHidden()
    {
        var lockedMat = new Material(_locked);
        try
        {
            Assert.IsTrue(OwnMaterial.IsLocked(lockedMat), "'Hidden/Locked/' is the lock signal");
            Assert.IsFalse(OwnMaterial.IsLocked(_mat),
                "'Hidden/' alone is not locked — the prefix must stay anchored and complete");
        }
        finally { Object.DestroyImmediate(lockedMat); }
    }

    // NOT COVERED: IsLocked's `m.shader != null` arm. MEASURED: `_mat.shader = null` leaves a substituted shader,
    // not null, so a test written that way answers through the NAME check and still passes with the guard
    // deleted. A genuinely null shader needs the shader ASSET destroyed under a live material; Run's post-unlock
    // check tests for null itself rather than calling IsLocked.
}
