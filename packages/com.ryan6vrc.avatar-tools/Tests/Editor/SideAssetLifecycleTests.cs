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

// CompileController's in-memory side assets, the VRCExpressionParameters and the menu tree, must not survive
// a compile unowned: EmitResult.DestroyUnpersisted has what a leak does to unrelated suites.
//
// Four exits hand the side assets to nobody, and each gets its own case because they are independently
// reachable and independently deletable:
//   A  the reuse-over-existing params branch (a recompile into a folder that already holds the asset)
//   B  ProofCompile's finally (every overwrite compile)
//   C  the post-emit lint failure (a fresh compile whose graph fails the lint)
//   D  a throw out of ControllerEmit.Build itself, where the EmitResult never reaches the caller at all
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

    // Plain custom names, neither `scratch:` nor VRC built-ins: a document whose params are all excluded
    // yields built.Params == null, never enters the reuse branch, and makes every case below pass while
    // testing nothing.
    private const string Head = @"schema: 1
controller: S_Fx
basis: avatar-root
parameters:
  Enable: bool
  Sat: float
layers: []
";

    // Sites A, B and E compile this, not Head: without a `menu:` block built.Menu is null and the menu half
    // of DestroyUnpersisted is unreachable. The submenu populates MenuChildren, a separate line of that
    // destroy.
    private const string HeadWithMenu = Head + @"menu:
  - toggle: A
    param: Enable
  - submenu: Page
    controls:
      - toggle: B
        param: Enable
";

    private string Compile(string body) { File.WriteAllText(_srcPath, body); return CompileController.Run(_srcPath, OutDir); }

    // Both types in one sequence: the destroy sites treat them as one unit, and a per-type oracle would leave
    // the menu half of every site unpinned.
    private static IEnumerable<UnityEngine.Object> UnownedSideAssets() =>
        Resources.FindObjectsOfTypeAll<VRCExpressionParameters>().Cast<UnityEngine.Object>()
            .Concat(Resources.FindObjectsOfTypeAll<VRCExpressionsMenu>())
            .Where(o => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)));

    // An instance-ID delta, not a count: Resources.FindObjectsOfTypeAll is domain-global and the 2-arg
    // ControllerEmit.Build door mints instances not every suite destroys, so an absolute assertion is red on
    // correct code in a full run and green when filtered.
    private static HashSet<int> Snapshot() =>
        new HashSet<int>(UnownedSideAssets().Select(o => o.GetInstanceID()));

    private static void AssertNoneAdded(HashSet<int> before, string what)
    {
        var added = UnownedSideAssets().Where(o => !before.Contains(o.GetInstanceID())).ToList();
        if (added.Count == 0) return;

        // Destroy before failing: a detected leak left in the domain would fail unrelated suites downstream
        // (EmitResult.DestroyUnpersisted), and this finding would read as their collateral.
        var described = string.Join(", ", added.Select(o => o.GetType().Name + " '" + o.name + "'"));
        foreach (var o in added) UnityEngine.Object.DestroyImmediate(o);
        Assert.Fail(what + " left " + added.Count + " unowned side asset(s) behind: " + described);
    }

    [Test]
    public void RecompileOverAnExistingParamsAsset_HoldsTheGuidAndLeaksNothing()
    {
        // Site A. ProofCompile also runs on this second compile, so Site B has its own isolated case below.
        StringAssert.Contains("=> OK", Compile(HeadWithMenu));

        var guid = AssetDatabase.AssetPathToGUID(ParamsPath);
        // AssetPathToGUID returns "" for a missing path, so the equality assertion below would pass on two
        // empty strings if the document emitted no params asset.
        Assert.IsNotEmpty(guid, "precondition: the first compile must actually write a params asset, or the "
            + "reuse branch under test is never entered");

        var before = Snapshot();
        // The match token carries no newline. HeadWithMenu is a verbatim string, so its line endings are the
        // .cs file's own, and with no .gitattributes those differ by checkout. A "…float\n" token silently
        // no-ops under CRLF, recompiling the unchanged document and failing the on-disk assertion below for a
        // reason that names nothing. The inserted \n is safe: AnimatorSchemaYaml normalizes line endings.
        StringAssert.Contains("=> OK", Compile(HeadWithMenu.Replace("  Sat: float", "  Sat: float\n  Extra: bool")));
        AssertNoneAdded(before, "the reuse-over-existing params path");

        Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(ParamsPath),
            "a consumer's FullController prms: row resolves this by GUID; reuse must be in place");
        // built.Params is destroyed before SaveAssets runs, so only the file shows the handoff survived
        // serialization.
        StringAssert.Contains("Extra", File.ReadAllText(ParamsPath),
            "the handed-off parameter array must reach disk after the source object is destroyed");
    }

    [Test]
    public void OverwriteCompile_ProofCompileLeaksNothing()
    {
        // Site B in isolation. Deleting only the params asset leaves the controller in place, so
        // controllerPreExisted is true and ProofCompile runs, while the main path takes the fresh CreateAsset
        // branch, which hands ownership off. Any survivor is ProofCompile's.
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
        // Site C. A fresh compile skips ProofCompile, so the lint-failure exit is reached with the params and
        // menu still in memory and nothing persisted.
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
        // Site D, inside ControllerEmit. The EmitResult out-parameter is never assigned when Build throws, so
        // only ControllerEmit can clean this up.
        //
        // The throw has to come from EmitMenu, past SchemaValidation. A menu control on a VRC built-in
        // never gets there: `menu-undeclared-param` rejects it at the document stage, Build is never
        // called, and the case passes while testing nothing. An unresolvable icon does reach it.
        //
        // Order matters in the document below: the submenu registers into MenuChildren first, then the icon
        // on the next control throws, leaving an in-flight root page in neither _result.Menu nor
        // _result.MenuChildren. Only ControllerEmit's page-tracking list reaches that page, and this case
        // pins the list.
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
        // Site E, the 2-arg ControllerEmit.Build door: it persists only the controller and hands the side
        // assets back, so EmitResult.DestroyUnpersisted is the caller's door and this is its oracle.
        var src = AnimatorSchemaYaml.Parse(HeadWithMenu, "test");
        var before = Snapshot();
        ControllerEmit.Build(src, out var built);
        try
        {
            Assert.IsNotNull(built.Params, "2-arg door emitted no params asset — this case would test nothing");
            Assert.IsNotNull(built.Menu, "2-arg door emitted no menu — the menu half would test nothing");
            Assert.IsNotEmpty(built.MenuChildren, "no submenu page — MenuChildren is its own line of the destroy");

            built.DestroyUnpersisted();
            AssertNoneAdded(before, "the 2-arg Build door followed by EmitResult.DestroyUnpersisted");

            Assert.DoesNotThrow(() => built.DestroyUnpersisted(), "a second DestroyUnpersisted must be a no-op");
            Assert.IsNull(built.Params, "Params must be nulled, not left dangling at a destroyed object");
            Assert.IsNull(built.Menu, "Menu must be nulled, not left dangling at a destroyed object");
            Assert.IsEmpty(built.MenuChildren, "MenuChildren must be cleared");
        }
        finally
        {
            // Sweep here too: a failing precondition assert throws before the in-body DestroyUnpersisted and
            // would leak what this file polices. Idempotent, so the in-body call stays the thing under test.
            built?.DestroyUnpersisted();
            // This door persists the controller into ControllerEmit's own scratch dir, which TearDown's
            // TestRoot delete does not reach.
            AssetDatabase.DeleteAsset("Assets/Agent/Scratch/emit/S_Fx.controller");
        }
    }
}
