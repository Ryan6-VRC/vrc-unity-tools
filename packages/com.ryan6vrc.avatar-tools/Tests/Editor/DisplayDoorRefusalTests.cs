using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

// Refusal tests for the two display doors. These are the checks that do NOT need the DebugDisplay shader
// to exist, so they hold from the first commit: target resolution, the Packages/ read-only policy, the
// wrong-shader guard, and the ordering between them. The doors' happy paths need a real display material
// and are verified once the shader lands (spec §7 rung 10).
//
// Ordering is itself a claim under test: the read-only refusal must fire BEFORE the asset load, so a
// Packages/ path is refused for the right reason rather than for not existing.
// Headless via tools/run-editmode-tests.ps1.
public class DisplayDoorRefusalTests
{
    const string ScratchFolder = "Assets/Agent/Scratch/display-doors";
    string _matPath;

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(ScratchFolder);
        AssetDatabase.Refresh();
        _matPath = ScratchFolder + "/NotADisplay.mat";
        var mat = new Material(Shader.Find("Standard"));
        AssetDatabase.CreateAsset(mat, _matPath);
        AssetDatabase.SaveAssets();
    }

    [TearDown]
    public void TearDown()
    {
        AssetDatabase.DeleteAsset(_matPath);
        AssetDatabase.Refresh();
    }

    // ── SetDisplayEntry ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Set_Refuses_A_Null_Path()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        var summary = SetDisplayEntry.Run(null, 0, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("materialPath", summary);
    }

    [Test]
    public void Set_Refuses_A_Packages_Target_Naming_The_Copy_Fix()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        // The shipped preset lives at Packages/com.ryan6vrc.patterns/debug-display/assets/WorldCoords.mat.
        // LAYOUT.md makes that read-only, so the door must refuse and say what to do instead.
        var summary = SetDisplayEntry.Run(
            "Packages/com.ryan6vrc.patterns/debug-display/assets/WorldCoords.mat", 0, "POS X:");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("read-only", summary);
        StringAssert.Contains("TEMPLATE", summary);
        StringAssert.Contains("Assets/", summary);
    }

    [Test]
    public void Set_Refuses_A_Vendor_Target_Too()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        var summary = SetDisplayEntry.Run("Assets/Vendor/Some/Display.mat", 0, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("read-only", summary);
    }

    [Test]
    public void Set_Refuses_A_Missing_Asset_Under_Assets()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        var summary = SetDisplayEntry.Run(ScratchFolder + "/NoSuchThing.mat", 0, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("no Material", summary);
    }

    [Test]
    public void Set_Refuses_A_Material_On_Another_Shader_Naming_Both()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        var summary = SetDisplayEntry.Run(_matPath, 0, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("Standard", summary);
        StringAssert.Contains(DisplayGlyphs.ShaderName, summary);
    }

    [Test]
    public void Set_Refuses_An_Out_Of_Range_Entry_Index()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        // The index is a pure argument check and runs before any policy or I/O, so it is named for what
        // it is rather than masked by the wrong-shader guard this scratch material would otherwise trip.
        var summary = SetDisplayEntry.Run(_matPath, DisplayGlyphs.MaxEntries, "X");
        StringAssert.Contains("=> FAIL", summary);
        StringAssert.Contains("out of range", summary);
        StringAssert.DoesNotContain(DisplayGlyphs.ShaderName, summary);
    }

    [Test]
    public void Set_ReadOnly_Refusal_Precedes_The_Asset_Load()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        // A Packages/ path that does not exist must still be refused as READ-ONLY, not as missing —
        // otherwise the policy guard is decorative and a real Packages/ material would slip past it.
        var summary = SetDisplayEntry.Run("Packages/does.not.exist/Nope.mat", 0, "X");
        StringAssert.Contains("read-only", summary);
        StringAssert.DoesNotContain("no Material", summary);
    }

    [Test]
    public void Set_WhatIf_Writes_Nothing_Even_On_A_Valid_Looking_Call()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[set-display-entry\].*=> FAIL"));
        // The scratch material is on the wrong shader so this refuses anyway; the assertion that matters
        // is that a whatIf run never dirties an asset. Recorded here so the flag's contract has a test
        // from the start rather than after the shader lands.
        var before = File.GetLastWriteTimeUtc(_matPath);
        SetDisplayEntry.Run(_matPath, 0, "X", whatIf: true);
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(_matPath));
    }

    // ── ReportDisplay ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Report_Refuses_A_Missing_Asset()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[report-display\].*=> ERROR"));
        var summary = ReportDisplay.Run(ScratchFolder + "/NoSuchThing.mat");
        StringAssert.Contains("=> ERROR", summary);
        StringAssert.Contains("no Material", summary);
    }

    [Test]
    public void Report_Refuses_A_Material_On_Another_Shader()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[report-display\].*=> ERROR"));
        var summary = ReportDisplay.Run(_matPath);
        StringAssert.Contains("=> ERROR", summary);
        StringAssert.Contains(DisplayGlyphs.ShaderName, summary);
    }

    [Test]
    public void Report_Reads_A_Packages_Path_Without_Refusing_On_Policy()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[report-display\].*=> ERROR"));
        // Reading a shipped template is legitimate — the read-only policy governs writes only. This one
        // fails on the missing asset, which is the correct reason, and proves the write-guard was not
        // copy-pasted onto the read door.
        var summary = ReportDisplay.Run("Packages/does.not.exist/Nope.mat");
        StringAssert.DoesNotContain("read-only", summary);
        StringAssert.Contains("no Material", summary);
    }
}
