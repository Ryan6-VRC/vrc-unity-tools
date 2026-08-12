using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.ScriptableObjects;

// CompileController's in-memory side assets (the VRCExpressionParameters, the menu tree) must not survive a
// compile unowned. A leaked SDK ScriptableObject in this assembly is not inert: ControllerFixpointTests's
// `_made` field records the measurement — leak → 551/600, destroy → 600/600, with unrelated suites failing
// on NullReferenceExceptions from ControllerEmit.AddStateMachineBehaviour returning null.
//
// FOUR exits hand the side assets to nobody, and each gets its own case here because they are independently
// reachable and independently deletable:
//   A  the reuse-over-existing params branch (a recompile into a folder that already holds the asset)
//   B  ProofCompile's finally (every overwrite compile)
//   C  the post-emit lint failure (a fresh compile whose graph fails the lint)
//   D  a throw out of ControllerEmit.Build itself, where the EmitResult never reaches the caller at all
//
// WHY AN INSTANCE-ID DELTA AND NOT A COUNT. Resources.FindObjectsOfTypeAll is domain-global, and this
// assembly ALREADY holds unowned VRCExpressionParameters that these cases do not cause — the 2-arg
// ControllerEmit.Build door mints one per call and its direct callers do not persist it. An absolute
// assertion is therefore red on correct code in a full unfiltered run and green when run filtered: it grades
// test ordering, not the fix. Only instances that appear ACROSS the measured call are this code's fault.
public class SideAssetLifecycleTests
{
    private const string TestRoot = "Assets/Agent/Scratch/sideasset_tests";
    private const string OutDir = TestRoot + "/out";
    private string _srcPath;

    private string ParamsPath => OutDir + "/S_Fx_Parameters.asset";
    private string ControllerPath => OutDir + "/S_Fx.controller";

    [SetUp]
    public void SetUp()
    {
        AnimatorTestHelpers.EnsureFolder(TestRoot);
        _srcPath = TestRoot + "/S_Fx.yaml";
    }

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(TestRoot);

    // Both params are plain custom names — NOT scratch:, NOT VRC built-ins — so EmitVrcParameters emits and
    // the asset is actually written. A document whose params are all excluded yields built.Params == null,
    // never enters the reuse branch, and would make every case below pass while testing nothing. The
    // precondition assertion in the Site-A case is what keeps that from happening silently.
    private const string Head = @"schema: 1
controller: S_Fx
basis: avatar-root
parameters:
  Enable: bool
  Sat: float
layers: []
";

    private string Compile(string body) { File.WriteAllText(_srcPath, body); return CompileController.Compile(_srcPath, OutDir); }

    private static HashSet<int> UnownedParams() =>
        new HashSet<int>(Resources.FindObjectsOfTypeAll<VRCExpressionParameters>()
            .Where(p => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(p)))
            .Select(p => p.GetInstanceID()));

    private static void AssertNoneAdded(HashSet<int> before, string what)
    {
        var added = Resources.FindObjectsOfTypeAll<VRCExpressionParameters>()
            .Where(p => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(p)))
            .Where(p => !before.Contains(p.GetInstanceID()))
            .Select(p => p.name).ToList();
        Assert.IsEmpty(added, what + " left " + added.Count + " unowned VRCExpressionParameters behind: "
            + string.Join(", ", added));
    }

    [Test]
    public void RecompileOverAnExistingParamsAsset_HoldsTheGuidAndLeaksNothing()
    {
        // Site A. Also covers Site B incidentally (ProofCompile runs on this second compile), which is why
        // Site B has its own case below — otherwise a regression in either reads as one indistinguishable red.
        StringAssert.Contains("=> OK", Compile(Head));

        var guid = AssetDatabase.AssetPathToGUID(ParamsPath);
        // AssetPathToGUID returns "" (not null) for a path that does not exist, so an equality assertion on
        // two empty strings would pass on a document that emitted no params asset at all. Pin it positively.
        Assert.IsNotEmpty(guid, "precondition: the first compile must actually write a params asset, or the "
            + "reuse branch under test is never entered");

        var before = UnownedParams();
        StringAssert.Contains("=> OK", Compile(Head.Replace("  Sat: float\n", "  Sat: float\n  Extra: bool\n")));
        AssertNoneAdded(before, "the reuse-over-existing params path");

        Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(ParamsPath),
            "a consumer's FullController prms: row resolves this by GUID; reuse must be in place");
        // The handoff has to survive SERIALIZATION, not just the destroy: built.Params is dropped before
        // SaveAssets runs, so a fix that lands correct in memory and empty on disk would pass an in-memory
        // assertion. Read the file.
        StringAssert.Contains("Extra", File.ReadAllText(ParamsPath),
            "the handed-off parameter array must reach disk after the source object is destroyed");
    }

    [Test]
    public void OverwriteCompile_ProofCompileLeaksNothing()
    {
        // Site B in isolation. Deleting only the PARAMS asset leaves the controller in place, so
        // controllerPreExisted is true and ProofCompile runs, while the main path takes the fresh
        // CreateAsset branch (which hands ownership off and cannot leak). Any survivor is ProofCompile's.
        StringAssert.Contains("=> OK", Compile(Head));
        Assert.IsTrue(File.Exists(ControllerPath), "precondition: an overwrite needs the controller to survive");
        AssetDatabase.DeleteAsset(ParamsPath);

        var before = UnownedParams();
        StringAssert.Contains("=> OK", Compile(Head));
        AssertNoneAdded(before, "ProofCompile's emit");
    }

    [Test]
    public void FreshCompileFailingTheLint_LeaksNothing()
    {
        // Site C. A FRESH compile (nothing pre-existing) skips ProofCompile, so the lint-failure exit at the
        // side-asset-persist boundary is reached with the params and menu still in memory and nothing
        // persisted — the lint deliberately runs before the writes, which is what creates the exposure.
        var before = UnownedParams();
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*graph lint.*=> FAIL"));
        StringAssert.Contains("FAIL", Compile(@"schema: 1
controller: S_Fx
basis: avatar-root
parameters:
  Enable: bool
layers:
  - name: L
    states:
      A:
        motion: ~
        transitions:
          - { to: B, when: [] }
      B:
        motion: ~
    default: A
menu:
  - toggle: A
    param: Enable
"));
        Assert.IsFalse(File.Exists(ParamsPath), "precondition: nothing is persisted on the lint-failure exit");
        AssertNoneAdded(before, "the post-emit lint failure exit");
    }

    [Test]
    public void EmitThrowingMidBuild_LeaksNothing()
    {
        // Site D — inside ControllerEmit, not CompileController. The EmitResult is an out-parameter that is
        // never assigned when Build throws, so the caller's catch has no handle on the side assets: only
        // ControllerEmit can clean this up.
        //
        // THE THROW HAS TO COME FROM EmitMenu, past SchemaValidation. The obvious document — a menu control
        // on a VRC built-in — never gets there: `menu-undeclared-param` (SchemaValidation) rejects it at the
        // document stage, Build is never called, and the case passes while testing NOTHING. Measured, not
        // reasoned: that draft went green against deliberately unfixed code.
        //
        // An unresolvable icon does reach it (ResolveIcon's third outcome). By then EmitVrcParameters has
        // minted Params AND BuildMenuPage has CreateInstance'd the root page — a page that is not yet in
        // _result.Menu and not in _result.MenuChildren, so it is reachable only from ControllerEmit's own
        // page-tracking list. This case is why that list exists.
        var before = UnownedParams();
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*=> FAIL"));
        StringAssert.Contains("FAIL", Compile(Head + @"menu:
  - toggle: A
    param: Enable
    icon: " + TestRoot + @"/no_such_icon.png
"));
        AssertNoneAdded(before, "a throw out of ControllerEmit.Build");
    }
}
