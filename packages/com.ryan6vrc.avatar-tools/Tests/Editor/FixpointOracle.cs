using System.IO;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // The shared round-trip oracle for the fixpoint suites. decode(c) = AnimatorSchemaEmit.Serialize(
    // ControllerDecompile.Walk(c).Doc); a clean decode is refusal-free.
    internal static class FixpointOracle
    {
        internal static string Decode(AnimatorController c, bool requireClean = true)
        {
            var w = ControllerDecompile.Walk(c);
            if (requireClean)
                Assert.IsEmpty(w.Refusals, "fixpoint decode must be refusal-free — got: " + string.Join(" | ", w.Refusals));
            return AnimatorSchemaEmit.Serialize(w.Doc);
        }

        // The yaml stays out of Assets/ because CompileController.Run takes a filesystem path, so an Assets/ path would
        // only cost an asset import per compile. Only outDir must be an Assets/-relative asset folder.
        internal static AnimatorController CompileTo(string testRoot, string yaml, string name, string tag)
        {
            string y = Path.Combine(Path.GetTempPath(), name + "_" + tag + ".yaml");
            File.WriteAllText(y, yaml);
            string outDir = testRoot + "/out_" + name + "_" + tag;
            AnimatorTestHelpers.EnsureFolder(outDir);
            string res = CompileController.Run(y, outDir, whatIf: false);
            // CLASSIFY is a clean compile that carries a finding to route, not a failure: a source controller with
            // dangling motion refs earns it and still round-trips. The fixpoint property is stability, so demanding OK
            // here would pin "the corpus has no broken motions", which is not a fact about this compiler.
            StringAssert.DoesNotContain("=> FAIL", res, "compile (" + tag + ") did not fail");
            Assert.IsTrue(res.Contains("=> OK") || res.Contains("=> CLASSIFY"),
                "compile (" + tag + ") is clean or classifies: " + res);
            var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(outDir + "/" + name + ".controller");
            Assert.IsNotNull(c, "compiled controller (" + tag + ") loads");
            return c;
        }

        // A .yaml under Packages/ may not import as a TextAsset, so read from disk; Unity patches Path.GetFullPath to
        // the package's resolved location even for an out-of-project file: package.
        internal static string ReadPackageText(string assetPath) => File.ReadAllText(Path.GetFullPath(assetPath));
    }
}
