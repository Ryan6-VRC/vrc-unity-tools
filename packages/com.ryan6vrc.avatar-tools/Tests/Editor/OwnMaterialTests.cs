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

    // Distinct-content PNG fixture per call (varying fill color) — real bytes on disk so the claimed-map's
    // cross-run byte-compare (PlanFork step c) has genuine content to compare, not a placeholder.
    static int _texFill;
    Texture2D MakeTexture(string folder, string name)
    {
        AnimatorTestHelpers.EnsureFolder(folder);
        var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var c = new Color32((byte)(40 + (_texFill++ * 53) % 200), 120, 200, 255);
        var pixels = new Color32[16];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = c;
        tex.SetPixels32(pixels);
        tex.Apply();
        byte[] png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        string path = folder + "/" + name + ".png";
        File.WriteAllBytes(path, png);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // A plain ScriptableObject "container" asset so we can embed a Texture2D as a NON-main sub-asset
    // (AssetDatabase.IsMainAsset == false) without needing a real FBX import — the same shape an
    // FBX-embedded texture takes for the "sub-asset — not forkable" disposition.
    class SubAssetHolder : ScriptableObject { }

    Texture2D SubAssetTex(string path, string subName)
    {
        var holder = ScriptableObject.CreateInstance<SubAssetHolder>();
        AssetDatabase.CreateAsset(holder, path);
        var tex = new Texture2D(2, 2) { name = subName };
        AssetDatabase.AddObjectToAsset(tex, holder);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            if (o is Texture2D t && t.name == subName) return t;
        return null;
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
        // (own, PASS) — never the slot-offender FAIL. A requested slot needs an actual texture assigned to
        // be forkable (an empty requested slot is an unforkable offender), so assign one.
        var v = VendorMat("Dress");
        var tex = MakeTexture(VendorRoot, "Diffuse");
        v.SetTexture("_MainTex", tex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

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
        var mainTex = MakeTexture(VendorRoot, "Diffuse");
        v.SetTexture("_MainTex", mainTex);
        v.SetFloat("_Metallic", 0.73f);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned); // no forkTextureSlots — fork nothing
        StringAssert.Contains("=> PASS", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        Assert.IsNotNull(o, "own must create the copy at outDir/<name>.mat");
        Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(v)),
            AssetDatabase.AssetPathToGUID(Owned + "/Dress.mat"), "copy must be a standalone asset (fresh GUID)");
        Assert.AreEqual(v.shader.name, o.shader.name, "copy preserves the source shader");
        Assert.AreEqual(0.73f, o.GetFloat("_Metallic"), 0.0001f, "copy byte-preserves the source's property edits");
        Assert.AreEqual(AssetDatabase.GetAssetPath(mainTex), AssetDatabase.GetAssetPath(o.GetTexture("_MainTex")),
            "fork-nothing keeps every slot on its current (vendor) reference");

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

    // The spec calls out that `force` has NO effect on the in-place augment route (the target is the
    // already-owned source — nothing to write-guard, so nothing to override). A force:true augment must
    // fork identically to force:false and emit no force-override note.
    [Test] public void Force_is_inert_on_in_place_augment()
    {
        var owned = OwnedMat("Base");
        var tex = MakeTexture(VendorRoot, "Body");
        owned.SetTexture("_MainTex", tex);
        EditorUtility.SetDirty(owned); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned), null, new[] { "_MainTex" }, force: true);
        StringAssert.Contains("=> PASS", s);
        StringAssert.Contains("augment", s);

        // Fork landed under H(O) exactly as a force:false augment would (Owned/Base/Body.png).
        var reloaded = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Base.mat");
        StringAssert.StartsWith(Owned + "/Base/", AssetDatabase.GetAssetPath(reloaded.GetTexture("_MainTex")));

        // No copy-to-new write-guard was consulted, so no force-override note was recorded.
        string json = File.ReadAllText(ExtractLogPath(s));
        StringAssert.DoesNotContain("force", json, "force must be inert on the augment route — no override note");
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

    // ── Fork engine (Task 3): H(O), the claimed-map collision resolution, disk-truthful slots[] ──────

    [Test] public void Fork_one_slot_disk_truthful()
    {
        var v = VendorMat("Dress");
        var mainTex = MakeTexture(VendorRoot, "Diffuse");
        var bumpTex = MakeTexture(VendorRoot, "Normal");
        v.SetTexture("_MainTex", mainTex);
        v.SetTexture("_BumpMap", bumpTex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "slotsForked"));

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        Assert.IsNotNull(o);
        string home = Owned + "/Dress";

        var ownedMain = o.GetTexture("_MainTex");
        Assert.IsNotNull(ownedMain, "requested slot must resolve to an owned texture");
        string ownedMainPath = AssetDatabase.GetAssetPath(ownedMain);
        StringAssert.StartsWith(home + "/", ownedMainPath);
        Assert.AreEqual("Diffuse.png", Path.GetFileName(ownedMainPath), "forked leaf keeps the source basename");
        Assert.AreNotEqual(AssetDatabase.AssetPathToGUID(VendorRoot + "/Diffuse.png"),
            AssetDatabase.AssetPathToGUID(ownedMainPath), "forked texture is a standalone copy (fresh GUID)");

        var ownedBump = o.GetTexture("_BumpMap");
        Assert.AreEqual(AssetDatabase.GetAssetPath(bumpTex), AssetDatabase.GetAssetPath(ownedBump),
            "untouched slot keeps its vendor GUID reference");

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"slot\": \"_MainTex\", \"requested\": true, \"disposition\": \"forked\"", json);
        StringAssert.Contains("\"disposition\": \"vendor-ref\"", json);
    }

    // G48: a forked texture's OWNED copy gets streaming mip maps enabled (VRChat upload defense), while the
    // Vendor/ source is left untouched. Source is set vendor-like (mips ON, streaming OFF) deterministically.
    [Test] public void Fork_enables_streaming_mipmaps_on_owned_copy_not_vendor()
    {
        var v = VendorMat("Dress");
        var srcTex = MakeTexture(VendorRoot, "Diffuse");
        string srcPath = AssetDatabase.GetAssetPath(srcTex);
        var srcTi = (TextureImporter)AssetImporter.GetAtPath(srcPath);
        srcTi.mipmapEnabled = true;
        srcTi.streamingMipmaps = false; // vendor-like: the state the finding names as an upload hazard
        srcTi.SaveAndReimport();
        v.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(srcPath));
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        string ownedTexPath = AssetDatabase.GetAssetPath(o.GetTexture("_MainTex"));
        var ownedTi = (TextureImporter)AssetImporter.GetAtPath(ownedTexPath);
        Assert.IsTrue(ownedTi.streamingMipmaps, "forked owned texture must have streaming mip maps enabled (G48)");

        var reSrcTi = (TextureImporter)AssetImporter.GetAtPath(srcPath);
        Assert.IsFalse(reSrcTi.streamingMipmaps, "vendor source must be left untouched (streaming still off)");

        StringAssert.Contains("streaming mip maps", s); // surfaced as a note on the one-liner
    }

    [Test] public void Two_slots_one_source_share_one_copy()
    {
        var v = VendorMat("Dress");
        var tex = MakeTexture(VendorRoot, "Shared");
        v.SetTexture("_MainTex", tex);
        v.SetTexture("_DetailAlbedoMap", tex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex", "_DetailAlbedoMap" });
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(2, AnimatorTestHelpers.Count(s, "slotsForked"), "both requested slots report forked");

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        string p1 = AssetDatabase.GetAssetPath(o.GetTexture("_MainTex"));
        string p2 = AssetDatabase.GetAssetPath(o.GetTexture("_DetailAlbedoMap"));
        Assert.AreEqual(p1, p2, "two slots sharing one source share one owned copy (claimed-map reuse)");

        string home = Owned + "/Dress";
        Assert.AreEqual(1, AssetDatabase.FindAssets("t:Texture2D", new[] { home }).Length,
            "only one texture file landed in H(O)");
    }

    [Test] public void Two_materials_forking_one_vendor_texture_get_independent_copies()
    {
        var tex = MakeTexture(VendorRoot, "Shared");
        var v1 = VendorMat("DressA"); v1.SetTexture("_MainTex", tex); EditorUtility.SetDirty(v1); AssetDatabase.SaveAssets();
        var v2 = VendorMat("DressB"); v2.SetTexture("_MainTex", tex); EditorUtility.SetDirty(v2); AssetDatabase.SaveAssets();

        string s1 = OwnMaterial.Run(AssetDatabase.GetAssetPath(v1), Owned, new[] { "_MainTex" });
        string s2 = OwnMaterial.Run(AssetDatabase.GetAssetPath(v2), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", s1);
        StringAssert.Contains("=> PASS", s2);

        var o1 = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/DressA.mat");
        var o2 = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/DressB.mat");
        string p1 = AssetDatabase.GetAssetPath(o1.GetTexture("_MainTex"));
        string p2 = AssetDatabase.GetAssetPath(o2.GetTexture("_MainTex"));
        Assert.AreNotEqual(p1, p2, "each owned material forks the shared vendor texture into its own H(O)");
        StringAssert.Contains("/DressA/", p1);
        StringAssert.Contains("/DressB/", p2);
    }

    [Test] public void Keywords_preserved_on_disk_after_fork()
    {
        // An arbitrary marker string, not "_EMISSION" — the memory's array getter (mat.shaderKeywords)
        // preserves ANY enabled keyword string verbatim, including ones outside the shader's own
        // keywordSpace, so a synthetic marker exercises the exact array-preservation mechanism under
        // test without depending on the Standard shader's own emission-toggle keyword semantics.
        const string marker = "OWNMAT_TEST_KEYWORD";
        var v = VendorMat("Dress");
        var tex = MakeTexture(VendorRoot, "Emit");
        v.SetTexture("_EmissionMap", tex);
        v.EnableKeyword(marker);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();
        CollectionAssert.Contains(v.shaderKeywords, marker, "fixture: the source material must carry the marker before Run");

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_EmissionMap" });
        StringAssert.Contains("=> PASS", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        CollectionAssert.Contains(o.shaderKeywords, marker,
            "fork must not wipe shaderKeywords (memory: unity-material-getter-clears-keywords)");
    }

    [Test] public void Same_run_collision_refuses_and_names_occupying_dst()
    {
        var v = VendorMat("Dress");
        var texA = MakeTexture(VendorRoot + "/A", "Tex");
        var texB = MakeTexture(VendorRoot + "/B", "Tex"); // same leaf "Tex.png", different source/content
        v.SetTexture("_MainTex", texA);
        v.SetTexture("_DetailAlbedoMap", texB);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex", "_DetailAlbedoMap" });
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("collide", s);
        StringAssert.Contains("Tex", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat"), "FAIL must roll back the created O");
    }

    [Test] public void Same_run_collision_whatIf_refuses_identically()
    {
        var v = VendorMat("Dress");
        var texA = MakeTexture(VendorRoot + "/A", "Tex");
        var texB = MakeTexture(VendorRoot + "/B", "Tex");
        v.SetTexture("_MainTex", texA);
        v.SetTexture("_DetailAlbedoMap", texB);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex", "_DetailAlbedoMap" }, whatIf: true);
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("collide", s);
        Assert.IsFalse(AssetDatabase.IsValidFolder(Owned), "whatIf must create nothing — not even the outDir folder");
    }

    [Test] public void Cross_run_collision_refuses()
    {
        var v = VendorMat("Dress");
        var texA = MakeTexture(VendorRoot + "/A", "Tex");
        v.SetTexture("_MainTex", texA);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string first = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", first);

        // Augment with a NEW slot whose source shares the "Tex" basename but has different bytes — the
        // second call's plan sees H(O)/Tex.png already on disk from the first run.
        var texB = MakeTexture(VendorRoot + "/B", "Tex");
        var owned = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        owned.SetTexture("_DetailAlbedoMap", texB);
        EditorUtility.SetDirty(owned); AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string second = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned), null, new[] { "_DetailAlbedoMap" });
        StringAssert.Contains("=> FAIL", second);
        StringAssert.Contains("prior run", second);
    }

    // whatIf parity for the CROSS-run case (mirrors Same_run_collision_whatIf_refuses_identically): a
    // preview that would hit an on-disk different-source collision refuses exactly as execute would, and
    // writes nothing (the on-disk existence + byte-compare in PlanFork step c is pure read-only).
    [Test] public void Cross_run_collision_whatIf_refuses_identically()
    {
        var v = VendorMat("Dress");
        var texA = MakeTexture(VendorRoot + "/A", "Tex");
        v.SetTexture("_MainTex", texA);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string first = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", first);

        // A NEW slot whose source shares the "Tex" basename but has different bytes — H(O)/Tex.png is
        // already on disk from the first run. Previewing the augment must refuse the same way.
        var texB = MakeTexture(VendorRoot + "/B", "Tex");
        var owned = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        owned.SetTexture("_DetailAlbedoMap", texB);
        EditorUtility.SetDirty(owned); AssetDatabase.SaveAssets();

        string dstBefore = AssetDatabase.GetAssetPath(
            AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat").GetTexture("_MainTex"));

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string second = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned), null, new[] { "_DetailAlbedoMap" }, whatIf: true);
        StringAssert.Contains("=> FAIL", second);
        StringAssert.Contains("prior run", second);

        // whatIf wrote nothing: the first run's Tex.png is untouched (same GUID) and no B-sourced copy landed.
        string dstAfter = AssetDatabase.GetAssetPath(
            AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat").GetTexture("_MainTex"));
        Assert.AreEqual(dstBefore, dstAfter, "whatIf must not overwrite the prior run's forked texture");
    }

    [Test] public void Cross_run_augment_reuses_dst_for_same_source_no_recopy()
    {
        var v = VendorMat("Dress");
        var texA = MakeTexture(VendorRoot, "Tex");
        v.SetTexture("_MainTex", texA);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string first = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", first);
        var owned = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        string dstPath = AssetDatabase.GetAssetPath(owned.GetTexture("_MainTex"));
        string dstGuid = AssetDatabase.AssetPathToGUID(dstPath);

        // A second, DIFFERENT slot pointing at the SAME (still-vendor) source texA — the prior run already
        // landed H(O)/Tex.png with texA's bytes, so this is a byte-EQUAL cross-run collision → reuse.
        owned.SetTexture("_DetailAlbedoMap", texA);
        EditorUtility.SetDirty(owned); AssetDatabase.SaveAssets();

        string second = OwnMaterial.Run(AssetDatabase.GetAssetPath(owned), null, new[] { "_DetailAlbedoMap" });
        StringAssert.Contains("=> PASS", second);

        var reloaded = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        string dst2 = AssetDatabase.GetAssetPath(reloaded.GetTexture("_DetailAlbedoMap"));
        Assert.AreEqual(dstPath, dst2, "second call reuses the prior run's dst for the same source");
        Assert.AreEqual(dstGuid, AssetDatabase.AssetPathToGUID(dst2), "reuse must not re-copy (same GUID)");
    }

    [Test] public void Forked_psd_leaf_warns_but_still_forks()
    {
        var v = VendorMat("Dress");
        var tex = MakeTexture(VendorRoot, "Pattern");

        // Re-file the same PNG bytes under a .psd extension — Unity's image decoder sniffs content by
        // signature, not filename, so a PNG payload named ".psd" still imports as a normal Texture2D; only
        // the leaf's EXTENSION (what the fork Warning keys off) differs from a hand-authored PSD.
        string psdPath = VendorRoot + "/Pattern.psd";
        File.WriteAllBytes(psdPath, File.ReadAllBytes(AssetDatabase.GetAssetPath(tex)));
        AssetDatabase.ImportAsset(psdPath, ImportAssetOptions.ForceSynchronousImport);
        var psdTex = AssetDatabase.LoadAssetAtPath<Texture2D>(psdPath);
        Assert.IsNotNull(psdTex, "fixture: a PNG payload saved with a .psd extension must still import as a " +
            "Texture2D (Unity's image decoder sniffs content, not extension) — if this assert fails, the " +
            "fixture needs fixing, not the OwnMaterial fork logic");

        v.SetTexture("_MainTex", psdTex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", s);
        StringAssert.Contains("warnings=[", s);
        StringAssert.Contains(".psd", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        var ownedTex = o.GetTexture("_MainTex");
        Assert.IsNotNull(ownedTex);
        StringAssert.EndsWith(".psd", AssetDatabase.GetAssetPath(ownedTex));

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"disposition\": \"forked\"", json);
    }

    [Test] public void Requested_unforkable_slots_each_named_offender_disposition_self_legible()
    {
        var v = VendorMat("Dress");
        var rt = new RenderTexture(4, 4, 0);
        AssetDatabase.CreateAsset(rt, VendorRoot + "/Rt.renderTexture");
        var subTex = SubAssetTex(VendorRoot + "/Holder.asset", "Embedded");
        Assert.IsNotNull(subTex, "fixture: sub-asset texture must be embedded under the holder asset");

        // _MainTex left null (empty, requested); _ParallaxMap = built-in; _OcclusionMap = RenderTexture
        // (non-Texture2D); _EmissionMap = sub-asset (embedded, e.g. inside an FBX). _DetailMask left null
        // and NOT requested — proves an untouched empty slot isn't conflated with a requested unforkable one.
        v.SetTexture("_ParallaxMap", Texture2D.whiteTexture);
        v.SetTexture("_OcclusionMap", rt);
        v.SetTexture("_EmissionMap", subTex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned,
            new[] { "_MainTex", "_ParallaxMap", "_OcclusionMap", "_EmissionMap" });
        StringAssert.Contains("=> FAIL", s);
        Assert.AreEqual(4, AnimatorTestHelpers.Count(s, "slotsUnforkable"));

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"slot\": \"_MainTex\", \"requested\": true, \"disposition\": \"unforkable\"", json);
        StringAssert.Contains("\"slot\": \"_ParallaxMap\", \"requested\": true, \"disposition\": \"unforkable\"", json);
        StringAssert.Contains("\"slot\": \"_OcclusionMap\", \"requested\": true, \"disposition\": \"unforkable\"", json);
        StringAssert.Contains("\"slot\": \"_EmissionMap\", \"requested\": true, \"disposition\": \"unforkable\"", json);
        StringAssert.Contains("\"slot\": \"_DetailMask\", \"requested\": false, \"disposition\": \"empty\"", json);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat"), "FAIL must roll back the created O");
    }

    // ── Coverage gaps folded in from Task 3's review ────────────────────────────────────────────────

    [Test] public void Untouched_slot_pointing_at_another_owned_materials_texture_reports_owned_elsewhere_with_note()
    {
        // First material: fork a texture, so it has an owned copy under its own H(O).
        var baseMat = VendorMat("Base");
        var baseTex = MakeTexture(VendorRoot, "BaseTex");
        baseMat.SetTexture("_MainTex", baseTex);
        EditorUtility.SetDirty(baseMat); AssetDatabase.SaveAssets();
        string baseSummary = OwnMaterial.Run(AssetDatabase.GetAssetPath(baseMat), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", baseSummary);
        var ownedBase = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Base.mat");
        var ownedBaseTex = ownedBase.GetTexture("_MainTex");
        string ownedBaseTexPath = AssetDatabase.GetAssetPath(ownedBaseTex);
        StringAssert.StartsWith(Owned + "/Base/", ownedBaseTexPath);

        // Second material: an UNtouched slot (not requested) points at the FIRST material's owned
        // texture — a texture under a DIFFERENT owned material's H(O).
        var dress = VendorMat("Dress");
        dress.SetTexture("_BumpMap", ownedBaseTex);
        EditorUtility.SetDirty(dress); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(dress), Owned); // fork nothing on Dress
        StringAssert.Contains("=> PASS", s);

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"slot\": \"_BumpMap\", \"requested\": false, \"disposition\": \"owned-elsewhere\"", json);
        // Assert the ACTUAL note text (not the always-present "notes": [ header, and not the path alone
        // which also appears in the slot row's sourcePath) so deleting the Note() call would fail this test.
        StringAssert.Contains(
            "slot '_BumpMap' references another material's owned texture (shared, not requested): " + ownedBaseTexPath,
            json);
    }

    [Test] public void WhatIf_own_with_fork_request_reports_slotsForked_and_creates_no_asset()
    {
        var v = VendorMat("Dress");
        var tex = MakeTexture(VendorRoot, "Diffuse");
        v.SetTexture("_MainTex", tex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" }, whatIf: true);
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "slotsForked"), "a real forkTextureSlots request previews as a would-fork");
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat"), "whatIf must create no owned material");
        Assert.IsFalse(File.Exists(Owned + "/Dress/Diffuse.png"), "whatIf must create no owned texture");
        Assert.IsFalse(AssetDatabase.IsValidFolder(Owned), "whatIf must not even create the outDir folder");
    }

    // ── Task 4: augment idempotent + additive incremental fork ────────────────────────────────────

    [Test] public void Augment_is_idempotent_and_additive()
    {
        var v = VendorMat("Dress");
        var mainTex = MakeTexture(VendorRoot, "Body");
        var bumpTex = MakeTexture(VendorRoot, "Bump");
        v.SetTexture("_MainTex", mainTex);
        v.SetTexture("_BumpMap", bumpTex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string first = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" }); // first own
        StringAssert.Contains("=> PASS", first);
        string ownedPath = Owned + "/Dress.mat";
        string mainTexPathBefore = AssetDatabase.GetAssetPath(
            AssetDatabase.LoadAssetAtPath<Material>(ownedPath).GetTexture("_MainTex"));
        string mainTexGuidBefore = AssetDatabase.AssetPathToGUID(mainTexPathBefore);

        // AUGMENT in place (no outDir): re-request the already-forked _MainTex plus a NEW _BumpMap.
        string s = OwnMaterial.Run(ownedPath, null, new[] { "_MainTex", "_BumpMap" });
        StringAssert.Contains("=> PASS", s);
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "slotsForked"));       // _BumpMap
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "slotsAlreadyOwned")); // _MainTex: re-requested, already under H(O)

        var o = AssetDatabase.LoadAssetAtPath<Material>(ownedPath);
        string mainTexPathAfter = AssetDatabase.GetAssetPath(o.GetTexture("_MainTex"));
        Assert.AreEqual(mainTexPathBefore, mainTexPathAfter, "already-owned _MainTex must not move");
        Assert.AreEqual(mainTexGuidBefore, AssetDatabase.AssetPathToGUID(mainTexPathAfter),
            "already-owned _MainTex must not be re-copied (same GUID) — idempotent");
        StringAssert.StartsWith(Owned + "/Dress/", AssetDatabase.GetAssetPath(o.GetTexture("_BumpMap")));

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"slot\": \"_MainTex\", \"requested\": true, \"disposition\": \"already-owned\"", json);
        StringAssert.Contains("\"slot\": \"_BumpMap\", \"requested\": true, \"disposition\": \"forked\"", json);
    }

    [Test] public void Augment_whatIf_reports_would_augment_and_would_fork_without_mutating()
    {
        var v = VendorMat("Dress");
        var mainTex = MakeTexture(VendorRoot, "Body");
        var bumpTex = MakeTexture(VendorRoot, "Bump");
        v.SetTexture("_MainTex", mainTex);
        v.SetTexture("_BumpMap", bumpTex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string own = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" }); // own; _BumpMap still vendor on O
        StringAssert.Contains("=> PASS", own);
        string ownedPath = Owned + "/Dress.mat";
        var before = AssetDatabase.GetAssetPath(
            AssetDatabase.LoadAssetAtPath<Material>(ownedPath).GetTexture("_BumpMap"));

        string s = OwnMaterial.Run(ownedPath, null, new[] { "_MainTex", "_BumpMap" }, whatIf: true);
        StringAssert.Contains("=> PASS", s);
        StringAssert.IsMatch(@"\[own-material\] \(whatIf\) augment ", s);
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "slotsForked"));       // _BumpMap would fork
        Assert.AreEqual(1, AnimatorTestHelpers.Count(s, "slotsAlreadyOwned")); // _MainTex would reuse
        Assert.IsFalse(File.Exists(Owned + "/Dress/Bump.png"), "whatIf wrote no owned _BumpMap texture");

        var after = AssetDatabase.GetAssetPath(
            AssetDatabase.LoadAssetAtPath<Material>(ownedPath).GetTexture("_BumpMap"));
        Assert.AreEqual(before, after, "whatIf left the owned _BumpMap slot unchanged");

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("would augment", json);
    }

    // ── Task 5: variant flatten on copy-to-new, before the (Task 6) unlock seam ───────────────────────

    [Test] public void Variant_source_is_flattened_on_own()
    {
        var parent = VendorMat("Base");             // Standard shader, under Vendor
        parent.SetFloat("_Glossiness", 0.9f);
        parent.SetOverrideTag("OwnMatTestTag", "vendor-tag-value");
        // Texture + non-default scale/offset on the PARENT (inherited, never overridden on the variant —
        // mirrors the inherited-_Glossiness axis) so all three of GetTexture/GetTextureScale/
        // GetTextureOffset — the most elaborate branch in FlattenVariant — are exercised through flatten.
        var mainTex = MakeTexture(VendorRoot, "Body");
        parent.SetTexture("_MainTex", mainTex);
        parent.SetTextureScale("_MainTex", new Vector2(2, 3));
        parent.SetTextureOffset("_MainTex", new Vector2(0.1f, 0.2f));
        parent.EnableKeyword("_EMISSION");
        EditorUtility.SetDirty(parent); AssetDatabase.SaveAssets();

        // Create + save the bare variant BEFORE setting any override on it, then set overrides on the
        // reloaded, asset-backed instance — matches how a Variant is actually authored (Inspector edits
        // always hit an asset-backed object; touching properties on a never-saved Variant collapses its
        // inherited keyword state immediately, in-memory — a Unity scripting quirk unrelated to this tool).
        var variant = new Material(parent) { parent = parent };
        string vp = VendorRoot + "/Var.mat";
        AssetDatabase.CreateAsset(variant, vp); AssetDatabase.SaveAssets();

        var v = AssetDatabase.LoadAssetAtPath<Material>(vp);
        v.SetColor("_Color", Color.red);            // child override
        // 2100: a valid custom queue for the Standard shader's default Opaque blend mode.
        v.renderQueue = 2100;
        // Re-affirm _EMISSION LAST, right before saving: touching another property value first (SetColor/
        // renderQueue above) silently drops a Variant's purely-inherited (never locally-touched) keyword
        // state — re-asserting it makes it this child's own local override, which then survives.
        v.EnableKeyword("_EMISSION");
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(vp, Owned, System.Array.Empty<string>(), newName: "Flat");
        StringAssert.Contains("=> PASS", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Flat.mat");
        // Keyword FIRST: the first GetColor/GetFloat/GetTexture call on a freshly-loaded Material instance
        // silently clears its shaderKeywords as a side effect (memory unity-material-getter-clears-
        // keywords' variant exception) — IsKeywordEnabled/parent don't trigger it, but reading the keyword
        // AFTER another property getter already has would falsely fail.
        Assert.IsTrue(o.IsKeywordEnabled("_EMISSION"), "keyword carried");
        Assert.IsNull(o.parent, "flattened to standalone");
        Assert.AreEqual(Color.red, o.GetColor("_Color"), "child override carried");
        Assert.AreEqual(0.9f, o.GetFloat("_Glossiness"), 1e-4f, "inherited value baked");
        Assert.AreEqual(2100, o.renderQueue, "renderQueue carried");
        Assert.AreEqual("vendor-tag-value", o.GetTag("OwnMatTestTag", false, ""), "override tag carried");
        // Texture triple: GetTexture + GetTextureScale + GetTextureOffset all baked from the parent onto
        // the standalone (the inherited texture reference plus its non-default tiling/offset).
        Assert.AreEqual(AssetDatabase.GetAssetPath(mainTex), AssetDatabase.GetAssetPath(o.GetTexture("_MainTex")),
            "inherited texture reference baked");
        Assert.AreEqual(new Vector2(2, 3), o.GetTextureScale("_MainTex"), "texture scale carried");
        Assert.AreEqual(new Vector2(0.1f, 0.2f), o.GetTextureOffset("_MainTex"), "texture offset carried");
    }

    [Test] public void Variant_undeclared_local_keyword_survives_flatten()
    {
        // The old FlattenVariant rebuilt its keyword set ONLY by probing shader.keywordSpace.keywordNames +
        // IsKeywordEnabled — a keyword enabled via EnableKeyword that is NOT declared in the shader's
        // keyword space (common with Poiyomi/lilToon feature toggles) was invisible to that probe and got
        // silently dropped on flatten. This marker is deliberately NOT a Standard-shader keyword (mirrors
        // how Keywords_preserved_on_disk_after_fork uses OWNMAT_TEST_KEYWORD for the non-variant path) —
        // this test fails against the keywordSpace-probe-only code and passes once the array getter
        // (o.shaderKeywords, which preserves local overrides verbatim regardless of declared-ness) is
        // unioned in.
        const string marker = "OWNMAT_UNDECLARED_KW";
        var parent = VendorMat("Base");              // Standard shader, under Vendor
        EditorUtility.SetDirty(parent); AssetDatabase.SaveAssets();

        // Same authoring order as Variant_source_is_flattened_on_own: create + save the bare variant first,
        // then set overrides on the reloaded, asset-backed instance.
        var variant = new Material(parent) { parent = parent };
        string vp = VendorRoot + "/VarUndeclared.mat";
        AssetDatabase.CreateAsset(variant, vp); AssetDatabase.SaveAssets();

        var v = AssetDatabase.LoadAssetAtPath<Material>(vp);
        v.EnableKeyword(marker);                     // local override, undeclared in the Standard shader's keywordSpace
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();
        CollectionAssert.Contains(v.shaderKeywords, marker, "fixture: the variant must carry the marker before Run");

        string s = OwnMaterial.Run(vp, Owned, System.Array.Empty<string>(), newName: "FlatUndeclared");
        StringAssert.Contains("=> PASS", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/FlatUndeclared.mat");
        // Observe via the array getter, NOT IsKeywordEnabled: Unity's IsKeywordEnabled reports only
        // shader-DECLARED (keyword-space) keywords and returns false for an undeclared one even when it's
        // enabled and present — o.shaderKeywords is the only observable for it (same as
        // Keywords_preserved_on_disk_after_fork). Against the pre-fix empty-seed flatten the marker was
        // never re-applied, so it's absent here; the union fix carries it through.
        CollectionAssert.Contains(o.shaderKeywords, marker,
            "flatten must union the array-getter's local overrides with the keywordSpace probe — an " +
            "undeclared EnableKeyword survives only via the array getter");
    }

    [Test] public void Nonvariant_own_is_unaffected_by_flatten()
    {
        var v = VendorMat("Dress");
        var tex = MakeTexture(VendorRoot, "Body");
        v.SetTexture("_MainTex", tex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", s);

        var o = AssetDatabase.LoadAssetAtPath<Material>(Owned + "/Dress.mat");
        Assert.IsNull(o.parent, "non-variant stays parentless (was already null — flatten never runs)");

        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.DoesNotContain("flattened variant", json, "flatten path must not run for a non-variant copy");
    }

    static string ExtractLogPath(string summary)
    {
        int i = summary.IndexOf("log=");
        Assert.GreaterOrEqual(i, 0, "summary missing 'log=' trailer: " + summary);
        return summary.Substring(i + 4);
    }

    // ── Task 6: locked-poi detection + Thry reflection unlock ──────────────────────────────────────
    // TestEditor has no Poiyomi package, so Thry.ThryEditor.ShaderOptimizer can never resolve — these
    // tests exercise the copy-to-new locked-detection + the refuse-when-Thry-absent FAIL path (named,
    // whatIf-parity) and the shader-name-only IsLocked predicate (stale AllLockedGUIDS tag on a normal
    // shader must NOT read as locked). Unlock success + the still-locked backstop's happy path are
    // live-only, verified in Task 7 (AvatarProject, which has poi installed).

    [Test] public void Locked_source_without_thry_fails_named()
    {
        // A REAL Hidden/Locked/… shader is what IsLocked keys on. TestEditor has no Poiyomi, so
        // Thry.ThryEditor.ShaderOptimizer won't resolve -> FAIL naming Thry (the common real case: poi
        // absent; the Thry-resolve check fires before the dialog guard, so the missing original-shader
        // tag is never reached).
        var m = VendorMat("LockedVendor");
        m.shader = LockedShader();
        EditorUtility.SetDirty(m); AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(m), Owned, System.Array.Empty<string>(), newName: "L");
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("Thry", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/L.mat"), "no orphan on unlock FAIL");
    }

    [Test] public void WhatIf_locked_without_thry_previews_fail()
    {
        // whatIf detects on the SOURCE S, so S carries the real Hidden/Locked shader — same FAIL as
        // execute (the Thry-resolve check runs read-only in whatIf too).
        var m = VendorMat("LockedVendor2");
        m.shader = LockedShader();
        EditorUtility.SetDirty(m); AssetDatabase.SaveAssets();

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(m), Owned, System.Array.Empty<string>(), newName: "L2", whatIf: true);
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("Thry", s);
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/L2.mat"), "whatIf wrote nothing");
    }

    // Regression for the live-found false-positive: real vendor poi materials carry a STALE non-empty
    // AllLockedGUIDS tag while fully UNLOCKED (normal shader). Detection must NOT treat the tag as
    // locked (only the Hidden/Locked/… shader name counts) — else the tool runs an unnecessary Thry
    // unlock (and, with poi absent, FAILs) on a perfectly ownable material.
    [Test] public void Stale_alllockedguids_tag_with_normal_shader_is_not_locked()
    {
        var v = VendorMat("StaleTag");                    // Standard shader (normal, resolvable)
        v.SetOverrideTag("AllLockedGUIDS", "deadbeef");   // stale tag, as unlocked vendor poi materials carry
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, System.Array.Empty<string>(), newName: "StaleOwned");
        StringAssert.Contains("=> PASS", s);              // owned as a normal material, no Thry attempt
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/StaleOwned.mat"));
    }

    [Test] public void Normal_material_unaffected_by_locked_detection()
    {
        var v = VendorMat("PlainDress");
        var tex = MakeTexture(VendorRoot, "PlainBody");
        v.SetTexture("_MainTex", tex);
        EditorUtility.SetDirty(v); AssetDatabase.SaveAssets();

        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> PASS", s);
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(Owned + "/PlainDress.mat"));
    }
}
