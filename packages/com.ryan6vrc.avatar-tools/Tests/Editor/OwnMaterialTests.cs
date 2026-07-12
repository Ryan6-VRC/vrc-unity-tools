// Behavioral tests for OwnMaterial. Task 2: targetPath routing (own/branch/augment), copy-to-new deep
// copy (fork-nothing — every slot lands "vendor-ref", since forking itself is Task 3), the in-place
// augment guards (owned/not-locked/not-variant), every routing FAIL named with its fix, the mode-first
// one-liner, and whatIf parity (reproduces every routing FAIL, creates nothing on disk). No fork/normalize
// (flatten/unlock) yet — Tasks 3/5/6. Throwaway on-disk assets under an owned scratch path; assert on the
// returned one-line summary + the RunLog JSON. Headless via tools/run-editmode-tests.ps1.
using NUnit.Framework;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

public class OwnMaterialTests
{
    const string Scratch = "Assets/Agent/Scratch/OwnMatTests";
    const string Owned = "Assets/Agent/Scratch/OwnMatTests/Owned";
    const string Branched = "Assets/Agent/Scratch/OwnMatTests/Branched";
    const string VendorRoot = "Assets/Vendor/OwnMatTests";
    bool _createdVendor;

    [SetUp] public void SetUp() { AnimatorTestHelpers.EnsureFolder(Scratch); }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(Scratch);
        AssetDatabase.DeleteAsset(VendorRoot);
        if (_createdVendor && AssetDatabase.IsValidFolder("Assets/Vendor")
            && AssetDatabase.FindAssets("", new[] { "Assets/Vendor" }).Length == 0)
            AssetDatabase.DeleteAsset("Assets/Vendor");
        _createdVendor = false;
    }

    void EnsureVendor()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Vendor")) { AssetDatabase.CreateFolder("Assets", "Vendor"); _createdVendor = true; }
        AnimatorTestHelpers.EnsureFolder(VendorRoot);
    }

    // Standard shader has real texture slots (_MainTex, _BumpMap, _EmissionMap, _MetallicGlossMap …) and
    // is lilToon/poi-free — plenty for guard/slot-validation/routing coverage without either package
    // installed.
    Material VendorMat(string name)
    {
        EnsureVendor();
        var m = new Material(Shader.Find("Standard"));
        string p = VendorRoot + "/" + name + ".mat";
        AssetDatabase.CreateAsset(m, p);
        return AssetDatabase.LoadAssetAtPath<Material>(p);
    }

    // An already-owned material — lives under Owned (writable), not Vendor — for branch/augment coverage.
    Material OwnedMat(string name)
    {
        AnimatorTestHelpers.EnsureFolder(Owned);
        var m = new Material(Shader.Find("Standard"));
        string p = Owned + "/" + name + ".mat";
        AssetDatabase.CreateAsset(m, p);
        return AssetDatabase.LoadAssetAtPath<Material>(p);
    }

    // Mints a real "Hidden/Locked/…" shader asset (no poi package needed) so the in-place augment guard's
    // locked-detection (IsLocked: shader name starts with "Hidden/Locked/") is testable without Thry.
    Shader LockedShader()
    {
        string path = Scratch + "/TestLock.shader";
        if (AssetDatabase.LoadAssetAtPath<Shader>(path) != null)
            return AssetDatabase.LoadAssetAtPath<Shader>(path);
        File.WriteAllText(path,
@"Shader ""Hidden/Locked/TestLock"" {
    Properties { _MainTex (""Tex"", 2D) = ""white"" {} }
    SubShader {
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""
            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };
            v2f vert (appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            fixed4 frag (v2f i) : SV_Target { return fixed4(1,1,1,1); }
            ENDCG
        }
    }
}");
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<Shader>(path);
    }

    // ── Arg guards (Flow step 1) — unchanged from Task 1 ────────────────────────────────────────────

    [Test] public void Null_material_path_fails()
    {
        LogAssert.Expect(LogType.Error, new Regex("materialPath is null or empty"));
        StringAssert.Contains("=> FAIL", OwnMaterial.Run(null, Owned));
    }

    [Test] public void Empty_material_path_fails()
    {
        LogAssert.Expect(LogType.Error, new Regex("materialPath is null or empty"));
        StringAssert.Contains("=> FAIL", OwnMaterial.Run("", Owned));
    }

    [Test] public void Missing_material_fails()
    {
        LogAssert.Expect(LogType.Error, new Regex("did not load as a Material"));
        StringAssert.Contains("=> FAIL", OwnMaterial.Run("Assets/DoesNotExist.mat", Owned));
    }

    [Test] public void Broken_shader_fails()
    {
        var m = VendorMat("Broken");
        m.shader = Shader.Find("Hidden/InternalErrorShader");
        EditorUtility.SetDirty(m); AssetDatabase.SaveAssets();
        LogAssert.Expect(LogType.Error, new Regex("broken/missing shader"));
        StringAssert.Contains("=> FAIL", OwnMaterial.Run(AssetDatabase.GetAssetPath(m), Owned));
    }

    // ── Slot-name validation (Flow step 2) — before any write, before routing (no mode yet) ─────────

    [Test] public void Bad_slot_name_fails_before_any_write()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_NotASlot" });
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("_NotASlot", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat"), "no half-made copy on a bad slot name");
        Assert.IsFalse(AssetDatabase.IsValidFolder(Owned), "no folder created before slot validation passes");
    }

    [Test] public void Multiple_bad_slots_each_named_offender()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_NotASlot", "_AlsoNotASlot" });
        StringAssert.Contains("_NotASlot", s);
        StringAssert.Contains("_AlsoNotASlot", s);
    }

    [Test] public void Real_texture_slot_passes_slot_validation()
    {
        // A real Standard-shader texture property (_MainTex) must clear slot validation and reach routing
        // (own, PASS) — never the slot-offender FAIL. Task 2 doesn't fork yet, so the requested slot still
        // reports vendor-ref (Task 3 wires the fork disposition); this only proves validation didn't block.
        var v = VendorMat("Dress");
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", s);
        StringAssert.DoesNotContain("no texture property", s);
    }

    // ── Offenders ⇔ FAIL invariant ──────────────────────────────────────────────────────────────────

    [Test] public void Offender_present_implies_fail()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_NotASlot" });
        bool hasOffenders = s.Contains("offenders=[");
        Assert.IsTrue(hasOffenders, "expected an offenders=[...] segment");
        StringAssert.Contains("=> FAIL", s);
    }

    // ── Copy-to-new core: own (vendor source) — fork-nothing, all slots vendor-ref ─────────────────

    [Test] public void Own_vendor_source_creates_standalone_copy_all_vendor_ref()
    {
        var v = VendorMat("Dress");
        v.SetFloat("_Metallic", 0.73f);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned);
        StringAssert.Contains("=> PASS", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        Assert.IsNotNull(o, "own must create the copy at outDir/<name>.mat");
        Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(v)),
            AssetDatabase.AssetPathToGUID(Owned + "/Dress.mat"), "copy must be a standalone asset (fresh GUID)");
        Assert.AreEqual(v.shader.name, o.shader.name, "copy preserves the source shader");
        Assert.AreEqual(0.73f, o.GetFloat("_Metallic"), 0.0001f, "copy byte-preserves the source's property edits");

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.DoesNotContain("\"disposition\": \"forked\"", json);
        StringAssert.Contains("\"disposition\": \"vendor-ref\"", json);
        StringAssert.Contains("\"slot\": \"_MainTex\"", json);
        StringAssert.Contains("\"slotsForked\": 0", json);
    }

    // ── Mode-first one-liner ────────────────────────────────────────────────────────────────────────

    [Test] public void Own_prefixes_mode_own()
    {
        var v = VendorMat("Dress");
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned);
        StringAssert.IsMatch(@"\[own-material\] own ", s);
    }

    [Test] public void Branch_prefixes_mode_branch()
    {
        var b = OwnedMat("Base");
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(b), Branched, forkTextureSlots: null, newName: "Variant");
        StringAssert.Contains("=> PASS", s);
        StringAssert.IsMatch(@"\[own-material\] branch ", s);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Branched + "/Variant.mat"));
    }

    [Test] public void Augment_prefixes_mode_augment()
    {
        var owned = OwnedMat("Base");
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned));
        StringAssert.Contains("=> PASS", s);
        StringAssert.IsMatch(@"\[own-material\] augment ", s);
    }

    // ── Branch: owned source, copy-to-new inherits edits ───────────────────────────────────────────

    [Test] public void Branch_owned_source_creates_distinct_new_copy()
    {
        var b = OwnedMat("Base");
        b.SetFloat("_Metallic", 0.5f);
        EditorUtility.SetDirty(b); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(b), Branched, newName: "Variant");
        StringAssert.Contains("=> PASS", s);

        var branched = AssetDatabase.LoadAssetAtPath<Material>(Branched + "/Variant.mat");
        Assert.IsNotNull(branched);
        Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(b)),
            AssetDatabase.AssetPathToGUID(Branched + "/Variant.mat"));
        Assert.AreEqual(0.5f, branched.GetFloat("_Metallic"), 0.0001f, "branch inherits source's property edits");
    }

    // ── Routing FAILs, each named with its fix ─────────────────────────────────────────────────────

    [Test] public void Vendor_source_no_outDir_fails()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v));
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("needs an outDir", s);
    }

    [Test] public void OutDir_resolving_to_source_fails()
    {
        var owned = OwnedMat("Base");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned), Owned); // outDir/Base.mat == source
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("omit outDir to augment", s);
    }

    [Test] public void NewName_without_outDir_fails()
    {
        var owned = OwnedMat("Base");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned), null, null, newName: "Renamed");
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("newName requires outDir", s);
    }

    [Test] public void TargetPath_exists_fails_no_clobber()
    {
        var v = VendorMat("Dress");
        string first = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned);
        StringAssert.Contains("=> PASS", first);

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string second = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned);
        StringAssert.Contains("=> FAIL", second);
        StringAssert.Contains("already exists", second);
    }

    [Test] public void OutDir_under_Vendor_fails_unless_force()
    {
        var v = VendorMat("Dress");
        string forcedOutDir = VendorRoot + "/ForcedOwned";

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string blocked = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), forcedOutDir);
        StringAssert.Contains("=> FAIL", blocked);
        StringAssert.Contains("read-only", blocked);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(forcedOutDir + "/Dress.mat"));

        string forced = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), forcedOutDir, force: true);
        StringAssert.Contains("=> PASS", forced);
        StringAssert.Contains("notes=[", forced);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(forcedOutDir + "/Dress.mat"), "force must proceed and write the target");
    }

    [Test] public void OutDir_resolving_to_a_file_fails()
    {
        var v = VendorMat("Dress");
        string filePath = Scratch + "/NotAFolder.asset";
        var dummy = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(dummy, filePath);

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), filePath);
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("not a folder", s);
    }

    // ── In-place augment guards ─────────────────────────────────────────────────────────────────────

    [Test] public void InPlace_locked_source_fails()
    {
        var owned = OwnedMat("Locked");
        owned.shader = LockedShader();
        EditorUtility.SetDirty(owned); AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned));
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("locked", s);
    }

    [Test] public void InPlace_variant_source_fails()
    {
        var baseMat = OwnedMat("Base");
        var variant = new Material(baseMat.shader) { parent = baseMat };
        AssetDatabase.CreateAsset(variant, Owned + "/Variant.mat");

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(Owned + "/Variant.mat");
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("variant", s);
    }

    [Test] public void Augment_in_place_resolves_O_equals_S()
    {
        var owned = OwnedMat("Base");
        string beforeGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(owned));

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned));
        StringAssert.Contains("=> PASS", s);
        string afterGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(owned));
        Assert.AreEqual(beforeGuid, afterGuid, "augment must not create a new asset — O resolves to S in place");
    }

    // ── whatIf parity: reproduces every routing FAIL, creates nothing ─────────────────────────────

    [Test] public void WhatIf_own_creates_no_asset_on_disk()
    {
        var v = VendorMat("Dress");
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, whatIf: true);
        StringAssert.Contains("=> PASS", s);
        StringAssert.IsMatch(@"\[own-material\] \(whatIf\) own ", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat"), "whatIf must create no asset");
        Assert.IsFalse(AssetDatabase.IsValidFolder(Owned), "whatIf must not even create the outDir folder");
    }

    [Test] public void WhatIf_reproduces_outDir_resolves_to_source_fail()
    {
        var owned = OwnedMat("Base");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned), Owned, whatIf: true);
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("omit outDir to augment", s);
    }

    [Test] public void WhatIf_reproduces_target_exists_fail_and_writes_nothing()
    {
        var v = VendorMat("Dress");
        OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned); // real copy first

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, whatIf: true);
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("already exists", s);
    }

    [Test] public void WhatIf_reproduces_outDir_under_Vendor_fail()
    {
        var v = VendorMat("Dress");
        string forcedOutDir = VendorRoot + "/ForcedOwnedWhatIf";
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), forcedOutDir, whatIf: true);
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("read-only", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(forcedOutDir + "/Dress.mat"));
    }

    // ── Coverage gaps folded in from Task 1's review ────────────────────────────────────────────────

    [Test] public void WhatIf_is_serialized_true_in_runlog()
    {
        var v = VendorMat("Dress");
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, whatIf: true);
        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"whatIf\": true", json);
    }

    [Test] public void Early_argfail_serializes_null_instance_and_source()
    {
        LogAssert.Expect(LogType.Error, new Regex("did not load as a Material"));
        string s = OwnMaterial.Run("Assets/DoesNotExist.mat", Owned);
        string logPath = ExtractLogPath(s);
        Assert.IsTrue(File.Exists(logPath), "RunLog file must exist at the path in the summary: " + logPath);

        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"kind\": \"own-material\"", json);
        StringAssert.Contains("\"unityVersion\"", json);
        StringAssert.Contains("\"timestampUtc\"", json);
        StringAssert.Contains("\"instance\": null", json);
        StringAssert.Contains("\"source\": null", json);
        StringAssert.Contains("\"result\": \"FAIL\"", json);
        StringAssert.Contains("\"slots\": []", json);
    }

    // ── RunLog shape: envelope + counts + offenders/notes/warnings + slots[] ──────────────────────

    [Test] public void RunLog_carries_envelope_with_populated_instance_and_empty_slots_on_bad_slot_fail()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_NotASlot" });
        string logPath = ExtractLogPath(s);
        Assert.IsTrue(File.Exists(logPath), "RunLog file must exist at the path in the summary: " + logPath);

        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"kind\": \"own-material\"", json);
        StringAssert.Contains("\"unityVersion\"", json);
        StringAssert.Contains("\"timestampUtc\"", json);
        StringAssert.Contains("\"whatIf\": false", json);
        StringAssert.Contains("\"instance\": \"Dress\"", json);
        StringAssert.Contains("\"source\"", json);
        StringAssert.Contains("\"result\": \"FAIL\"", json);
        StringAssert.Contains("\"offenders\": [", json);
        StringAssert.Contains("\"notes\": [", json);
        StringAssert.Contains("\"warnings\": [", json);
        StringAssert.Contains("\"slots\": []", json); // fails before any slot listing runs
        StringAssert.Contains("_NotASlot", json);
    }

    static string ExtractLogPath(string summary)
    {
        int i = summary.IndexOf("log=");
        Assert.GreaterOrEqual(i, 0, "summary missing 'log=' trailer: " + summary);
        return summary.Substring(i + 4);
    }
}
