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

// CompileController's in-memory side assets — the VRCExpressionParameters AND the menu tree — must not
// survive a compile unowned. A leaked SDK ScriptableObject in this assembly is not inert:
// ControllerFixpointTests's `_made` field records the measurement — leak → 551/600, destroy → 600/600, with
// unrelated suites failing on NullReferenceExceptions from ControllerEmit.AddStateMachineBehaviour.
//
// FOUR exits hand the side assets to nobody, and each gets its own case because they are independently
// reachable and independently deletable:
//   A  the reuse-over-existing params branch (a recompile into a folder that already holds the asset)
//   B  ProofCompile's finally (every overwrite compile)
//   C  the post-emit lint failure (a fresh compile whose graph fails the lint)
//   D  a throw out of ControllerEmit.Build itself, where the EmitResult never reaches the caller at all
//
// BOTH TYPES ARE ASSERTED, on every case. An earlier revision watched VRCExpressionParameters only, which
// left the menu half of every destroy site unpinned — site D could delete ControllerEmit's whole page
// tracking list and still pass, and the sites A/B document carried no `menu:` block at all, so built.Menu
// was null on the very path the fix calls most-travelled. A case that cannot fail for the mechanism it
// names is decoration.
//
// WHY AN INSTANCE-ID DELTA AND NOT A COUNT. Resources.FindObjectsOfTypeAll is domain-global, and this
// assembly ALREADY holds unowned instances these cases do not cause — the 2-arg ControllerEmit.Build door
// mints one per call and its direct callers do not persist it. An absolute assertion is therefore red on
// correct code in a full unfiltered run and green when run filtered: it grades test ordering, not the fix.
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

    // Sites A and B compile THIS, not Head: without a menu: block built.Menu is null and the menu half of
    // DestroyUnpersistedSideAssets is unreachable on those paths. The submenu is load-bearing too — it is
    // what populates MenuChildren, a separate line of the destroy from built.Menu.
    private const string HeadWithMenu = Head + @"menu:
  - toggle: A
    param: Enable
  - submenu: Page
    controls:
      - toggle: B
        param: Enable
";

    private string Compile(string body) { File.WriteAllText(_srcPath, body); return CompileController.Compile(_srcPath, OutDir); }

    // Both side-asset types in one sequence: the destroy sites treat them as one unit, so the oracle should
    // too, and a per-type helper invites exactly the half-coverage this file's header describes.
    private static IEnumerable<UnityEngine.Object> UnownedSideAssets() =>
        Resources.FindObjectsOfTypeAll<VRCExpressionParameters>().Cast<UnityEngine.Object>()
            .Concat(Resources.FindObjectsOfTypeAll<VRCExpressionsMenu>())
            .Where(o => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)));

    private static HashSet<int> Snapshot() =>
        new HashSet<int>(UnownedSideAssets().Select(o => o.GetInstanceID()));

    private static void AssertNoneAdded(HashSet<int> before, string what)
    {
        var added = UnownedSideAssets().Where(o => !before.Contains(o.GetInstanceID())).ToList();
        if (added.Count == 0) return;

        // Describe, then DESTROY, then fail. Leaving a detected leak in the domain reproduces the cascade
        // this suite exists to prevent: one honest red here would scatter NullReferenceExceptions across
        // unrelated suites downstream, and the real finding would be read as collateral of those.
        var described = string.Join(", ", added.Select(o => o.GetType().Name + " '" + o.name + "'"));
        foreach (var o in added) UnityEngine.Object.DestroyImmediate(o);
        Assert.Fail(what + " left " + added.Count + " unowned side asset(s) behind: " + described);
    }

    [Test]
    public void RecompileOverAnExistingParamsAsset_HoldsTheGuidAndLeaksNothing()
    {
        // Site A. Also covers Site B incidentally (ProofCompile runs on this second compile), which is why
        // Site B has its own case below — otherwise a regression in either reads as one indistinguishable red.
        StringAssert.Contains("=> OK", Compile(HeadWithMenu));

        var guid = AssetDatabase.AssetPathToGUID(ParamsPath);
        // AssetPathToGUID returns "" (not null) for a path that does not exist, so an equality assertion on
        // two empty strings would pass on a document that emitted no params asset at all. Pin it positively.
        Assert.IsNotEmpty(guid, "precondition: the first compile must actually write a params asset, or the "
            + "reuse branch under test is never entered");

        var before = Snapshot();
        StringAssert.Contains("=> OK", Compile(HeadWithMenu.Replace("  Sat: float\n", "  Sat: float\n  Extra: bool\n")));
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
        StringAssert.Contains("=> OK", Compile(HeadWithMenu));
        Assert.IsTrue(File.Exists(ControllerPath), "precondition: an overwrite needs the controller to survive");
        AssetDatabase.DeleteAsset(ParamsPath);

        var before = Snapshot();
        StringAssert.Contains("=> OK", Compile(HeadWithMenu));
        AssertNoneAdded(before, "ProofCompile's emit");
    }

    [Test]
    public void FreshCompileFailingTheLint_LeaksNothing()
    {
        // Site C. A FRESH compile (nothing pre-existing) skips ProofCompile, so the lint-failure exit at the
        // side-asset-persist boundary is reached with the params and menu still in memory and nothing
        // persisted — the lint deliberately runs before the writes, which is what creates the exposure.
        var before = Snapshot();
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*graph lint.*=> FAIL"));
        var summary = Compile(@"schema: 1
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
  - submenu: Page
    controls:
      - toggle: B
        param: Enable
");
        StringAssert.Contains("FAIL", summary);
        // A refusal writes a RunLog outside TestRoot, which TearDown does not reach.
        AnimatorTestHelpers.DeleteRefusalArtifact(summary);
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
        // An unresolvable icon does reach it (ResolveIcon's third outcome). ORDER MATTERS in the document
        // below: the submenu is built and registered into MenuChildren FIRST, then the icon on the next
        // control throws — so at throw time there is one registered child page AND one in-flight root page
        // that is in neither _result.Menu nor _result.MenuChildren. The in-flight one is reachable only from
        // ControllerEmit's own page-tracking list, and this case is what pins that list's existence.
        var before = Snapshot();
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*=> FAIL"));
        var summary = Compile(Head + @"menu:
  - submenu: Page
    controls:
      - toggle: B
        param: Enable
  - toggle: A
    param: Enable
    icon: " + TestRoot + @"/no_such_icon.png
");
        StringAssert.Contains("FAIL", summary);
        AnimatorTestHelpers.DeleteRefusalArtifact(summary);
        AssertNoneAdded(before, "a throw out of ControllerEmit.Build");
    }

    [Test]
    public void TwoArgBuildDoor_CallerDestroysUnpersisted_LeaksNothing()
    {
        // Site E — the 2-arg ControllerEmit.Build door, the one with no owner in production: it persists the
        // CONTROLLER only and hands the side assets back, so EmitResult.DestroyUnpersisted is the CALLER's
        // door and this is its oracle. Sites A-D cover CompileController, which owns its own.
        //
        // The document carries a menu WITH a submenu deliberately: params alone would pass with the Menu and
        // MenuChildren lines of the door deleted — the half-coverage this file's header describes.
        var src = AnimatorSchemaYaml.Parse(HeadWithMenu, "test");
        var before = Snapshot();
        ControllerEmit.Build(src, out var built);
        try
        {
            // Preconditions, not decoration: if the door minted neither type there is nothing to leak and the
            // case cannot fail. Both must be present BEFORE the destroy for the assertion after it to mean
            // anything.
            Assert.IsNotNull(built.Params, "2-arg door emitted no params asset — this case would test nothing");
            Assert.IsNotNull(built.Menu, "2-arg door emitted no menu — the menu half would test nothing");
            Assert.IsNotEmpty(built.MenuChildren, "no submenu page — MenuChildren is its own line of the destroy");

            built.DestroyUnpersisted();
            AssertNoneAdded(before, "the 2-arg Build door followed by EmitResult.DestroyUnpersisted");

            // Idempotent: the fields are nulled, so a caller that sweeps twice — a finally under a catch that
            // already swept — must neither fault nor double-destroy.
            Assert.DoesNotThrow(() => built.DestroyUnpersisted(), "a second DestroyUnpersisted must be a no-op");
            Assert.IsNull(built.Params, "Params must be nulled, not left dangling at a destroyed object");
            Assert.IsNull(built.Menu, "Menu must be nulled, not left dangling at a destroyed object");
            Assert.IsEmpty(built.MenuChildren, "MenuChildren must be cleared");
        }
        finally
        {
            // Sweep here too, not only in the body. A precondition assert above throws BEFORE the in-body
            // DestroyUnpersisted, and the trigger for one of those asserts is exactly the regression they
            // watch for — the door ceasing to emit a type — so a red here would leak the very side assets
            // this file exists to police, scattering the 551/600 cascade across unrelated suites and burying
            // its own finding as collateral. Idempotent by design, so the in-body call stays the thing under
            // test. Same "describe, then DESTROY, then fail" order AssertNoneAdded uses.
            built?.DestroyUnpersisted();
            // This door persists the controller into ControllerEmit's OWN scratch dir, not OutDir, so
            // TearDown's TestRoot delete does not reach it.
            AssetDatabase.DeleteAsset("Assets/Agent/Scratch/emit/S_Fx.controller");
        }
    }
}
