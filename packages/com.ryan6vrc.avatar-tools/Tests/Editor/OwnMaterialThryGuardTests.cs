// Pins OwnMaterial's Thry/Poiyomi dialog guard, plus two properties of the lock detector that the
// end-to-end tests next door cannot express.
//
// OriginalShaderResolves is the one with a real coverage gap. Poiyomi is not installed in the test venue,
// so TryResolvePoiUnlock short-circuits at OwnMaterial.cs:235 BEFORE the dialog guard at :246 is ever
// consulted — OwnMaterialTests.cs:1035 records exactly that. The guard's answers, both of them, are
// unreachable through any public door here, so the tool behaves identically whether it is right or
// catastrophically wrong. Same omission shape as ImportPackage.NameMatches before F36: a decision inside a
// boundary the suite legitimately can't exercise, riding on its untestable caller.
//
// It exists to mirror Thry's own two-step fallback BEFORE Thry reaches it, because Thry answers an
// unresolvable tag with a blocking DisplayDialog — in an agent-driven headless run, not a prompt but a hang.
//
// IsLocked is NOT such a gap, and this file does not pretend otherwise: OwnMaterialTests drives its YES
// answer end-to-end through Run() three times (:360, :1031, :1048) off a real Hidden/Locked shader, and
// pins tag-presence-is-not-a-lock-signal at :1067. Only two properties are missing there, both added here:
// the Hidden/-but-not-Hidden/Locked/ near miss (loosening the prefix passes every existing test), and the
// null-shader contract, which OwnMaterial.cs:260-261 hand-rolls a backstop for precisely because IsLocked
// reports null as not-locked.
//
// The tag names are written as LITERALS below, deliberately. Arranging via OwnMaterial's own constants
// would make test and production read the same symbol, and the value — whether it matches Thry's actual
// TAG_* — is the only thing about those constants that can be wrong.
//
// COST (OwnMaterialTests.cs:11-13): shader imports are real HLSL compiles. Both are [OneTimeSetUp], in a
// seed folder outside the per-test TearDown, mirroring that file.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class OwnMaterialThryGuardTests
{
    const string Seed = "Assets/Agent/Scratch/OwnMatThryGuardSeed";
    const string ProbeShaderPath = Seed + "/F39Probe.shader";
    const string LockedShaderPath = Seed + "/F39Locked.shader";
    // Declared names inside the .shader sources — what Shader.Find resolves, distinct from the asset paths.
    // The probe is Hidden/ but NOT Hidden/Locked/: the near miss a loosened prefix check would swallow.
    const string ProbeShaderName = "Hidden/F39/ProbeShader";
    const string LockedShaderName = "Hidden/Locked/F39Probe";

    // Thry's tag names, as literals — see the header. A drift in OwnMaterial's constants must fail here.
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

    // No tags at all — nothing to restore, so the unlock must not be attempted.
    [Test]
    public void OriginalShaderResolves_noTags_isFalse()
    {
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.IsEmpty(tag ?? "", "with no tags there is no tag value to name in the refusal");
    }

    // The GUID leg's happy path: tag resolves through GUIDToAssetPath -> LoadAssetAtPath<Shader>.
    // Doubles as the pin on OwnMaterial's OriginalShaderGUID constant — a typo there fails here.
    [Test]
    public void OriginalShaderResolves_liveGuidTag_isTrue()
    {
        _mat.SetOverrideTag(TagGuid, _probeGuid);
        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(_probeGuid, tag);
    }

    // THE case this guard exists for: a poi reinstall or version bump leaves the tag present but its GUID
    // pointing at nothing. Presence is not resolution — treating it as resolution is what walks into Thry's
    // modal. The refusal must name the stale GUID, the operator's only handle on the problem.
    [Test]
    public void OriginalShaderResolves_staleGuidTag_isFalse_andNamesTheGuid()
    {
        _mat.SetOverrideTag(TagGuid, DeadGuid);
        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(DeadGuid, tag, "the refusal must name the tag it could not resolve");
    }

    // The NAME leg's happy path, reached when no GUID tag is present. Pins the OriginalShader constant.
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

    // The load-bearing precedence: a STALE GUID must fall through to a live NAME rather than refusing.
    // This is the ordering Thry itself uses, and the whole reason the guard is two steps. Collapse it to
    // GUID-only and a recoverable material gets refused; collapse it to name-only and one Thry could
    // restore by GUID takes the slow path.
    [Test]
    public void OriginalShaderResolves_staleGuid_fallsThroughToLiveName()
    {
        _mat.SetOverrideTag(TagGuid, DeadGuid);
        _mat.SetOverrideTag(TagName, ProbeShaderName);

        Assert.IsTrue(OwnMaterial.OriginalShaderResolves(_mat, out string tag),
            "a dead GUID beside a live name must resolve via the name, not refuse");
        Assert.AreEqual(ProbeShaderName, tag, "the resolving tag is the one reported");
    }

    // Both tags present, neither resolves: refuse, and name the GUID — the more specific handle.
    [Test]
    public void OriginalShaderResolves_bothTagsDead_isFalse_andPrefersNamingTheGuid()
    {
        _mat.SetOverrideTag(TagGuid, DeadGuid);
        _mat.SetOverrideTag(TagName, "Hidden/F39/AlsoGone");

        Assert.IsFalse(OwnMaterial.OriginalShaderResolves(_mat, out string tag));
        Assert.AreEqual(DeadGuid, tag);
    }

    // ----- IsLocked: only what OwnMaterialTests cannot express ----------------------------------

    // The near miss. OwnMaterialTests proves a Hidden/Locked/ shader reads locked; nothing there proves a
    // merely-Hidden/ one does not, so loosening the prefix to "Hidden/" passes that whole file and fails
    // only here — which would send every hidden-shader material down an unnecessary Thry unlock.
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

    // NOT COVERED, and deliberately not faked: IsLocked's `m.shader != null` arm. MEASURED here —
    // `_mat.shader = null` leaves a substituted shader, not null, so a test written that way answers
    // through the NAME check while appearing to exercise the null guard. Deleting the guard would still
    // pass it. A false pin in a file whose whole subject is tests that can actually fail is worse than an
    // acknowledged gap, so the case is gone rather than left green. Reaching a genuinely null shader needs
    // the shader ASSET destroyed under a live material; OwnMaterial.cs:260-261 hand-rolls its own backstop
    // for that state and is the real protection.
}
