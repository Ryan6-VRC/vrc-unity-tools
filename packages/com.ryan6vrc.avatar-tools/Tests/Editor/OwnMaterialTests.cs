// Behavioral tests for OwnMaterial. Task 1: arg guards (materialPath empty/unloadable, broken/missing
// shader) + slot-name validation, and the tool-local RunLog shape (envelope + slots[]). Every valid call
// FAILs "not implemented past guards" — routing/copy/fork/unlock land in later tasks. Throwaway on-disk
// assets under an owned scratch path; assert on the returned one-line summary + the RunLog JSON. Headless
// via tools/run-editmode-tests.ps1.
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
    // is lilToon/poi-free — plenty for guard/slot-validation coverage without either package installed.
    Material VendorMat(string name)
    {
        EnsureVendor();
        var m = new Material(Shader.Find("Standard"));
        string p = VendorRoot + "/" + name + ".mat";
        AssetDatabase.CreateAsset(m, p);
        return AssetDatabase.LoadAssetAtPath<Material>(p);
    }

    // ── Arg guards (Flow step 1) ────────────────────────────────────────────────────────────────────

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

    // ── Slot-name validation (Flow step 2) — before any write ─────────────────────────────────────

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
        // A real Standard-shader texture property (_MainTex) must clear slot validation and reach the
        // Task-1 stub FAIL ("not implemented past guards"), never the slot-offender FAIL.
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("not implemented past guards"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_MainTex" });
        StringAssert.Contains("=> FAIL", s);
        StringAssert.Contains("not implemented past guards", s);
        StringAssert.DoesNotContain("no texture property", s);
    }

    [Test] public void No_requested_slots_still_reaches_stub_fail()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("not implemented past guards"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned);
        StringAssert.Contains("not implemented past guards", s);
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

    // ── RunLog shape: envelope + counts + offenders/notes/warnings + slots[] ──────────────────────

    [Test] public void RunLog_carries_envelope_and_empty_slots_array()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("not implemented past guards"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned);
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
        StringAssert.Contains("\"error\": \"not implemented past guards\"", json);
        StringAssert.Contains("\"offenders\": [", json);
        StringAssert.Contains("\"notes\": [", json);
        StringAssert.Contains("\"warnings\": [", json);
        StringAssert.Contains("\"slots\": []", json); // Task 1: fork planner not wired yet — always empty
    }

    [Test] public void RunLog_offenders_name_the_bad_slot()
    {
        var v = VendorMat("Dress");
        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        string s = OwnMaterial.Run(AssetDatabase.GetAssetPath(v), Owned, new[] { "_NotASlot" });
        string logPath = ExtractLogPath(s);
        string json = File.ReadAllText(logPath);
        StringAssert.Contains("\"result\": \"FAIL\"", json);
        StringAssert.Contains("_NotASlot", json);
    }

    static string ExtractLogPath(string summary)
    {
        int i = summary.IndexOf("log=");
        Assert.GreaterOrEqual(i, 0, "summary missing 'log=' trailer: " + summary);
        return summary.Substring(i + 4);
    }
}
