using System.IO;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // The shared round-trip oracle. decode(c) = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(c).Doc);
    // a clean decode is refusal-free. Both FixpointAcceptanceTests and RoundtripStressTests read this one
    // authority so the oracle can't drift between them.
    internal static class FixpointOracle
    {
        internal static string Decode(AnimatorController c, bool requireClean = true)
        {
            var w = ControllerDecompile.Walk(c);
            if (requireClean)
                Assert.IsEmpty(w.Refusals, "fixpoint decode must be refusal-free — got: " + string.Join(" | ", w.Refusals));
            return AnimatorSchemaEmit.Serialize(w.Doc);
        }

        // Write yaml to an OS temp file, compile into a fresh asset sub-folder under testRoot, return the loaded
        // controller. The yaml deliberately does NOT live under Assets/: CompileController.Compile takes a
        // filesystem path, reads it with File.ReadAllText and otherwise only uses it for error labels, so an
        // Assets/ path buys nothing and costs one asset import per compile — ~45 of them across the fixpoint
        // suites. Only outDir must be an Assets/-relative asset folder, and EnsureFolder registers it via
        // AssetDatabase.CreateFolder (which writes the .meta too) instead of a project-wide Refresh().
        internal static AnimatorController CompileTo(string testRoot, string yaml, string name, string tag)
        {
            string y = Path.Combine(Path.GetTempPath(), name + "_" + tag + ".yaml");
            File.WriteAllText(y, yaml);
            string outDir = testRoot + "/out_" + name + "_" + tag;
            AnimatorTestHelpers.EnsureFolder(outDir);
            string res = CompileController.Compile(y, outDir, whatIf: false);
            // CLASSIFY is a clean compile that carries a finding to route, not a failure — a source
            // controller with dangling motion refs earns it and still round-trips (GoGoLoco's
            // GoLocoBaseFullPoses ships 4). The fixpoint property is stability, so demanding OK here would
            // pin "the corpus has no broken motions", which is not a fact about this compiler. FAIL is still
            // a failure; the count's own stability across c1/c2 is asserted by the callers' text compare.
            StringAssert.DoesNotContain("=> FAIL", res, "compile (" + tag + ") did not fail");
            Assert.IsTrue(res.Contains("=> OK") || res.Contains("=> CLASSIFY"),
                "compile (" + tag + ") is clean or classifies: " + res);
            var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(outDir + "/" + name + ".controller");
            Assert.IsNotNull(c, "compiled controller (" + tag + ") loads");
            return c;
        }

        // Resolve an in-package fixture (a Packages/… virtual path) to disk and read it. A .yaml under
        // Packages/ may not import as a TextAsset, so read from disk; Unity patches Path.GetFullPath to the
        // package's resolved location even for an out-of-project file: package.
        internal static string ReadPackageText(string assetPath) => File.ReadAllText(Path.GetFullPath(assetPath));
    }
}
