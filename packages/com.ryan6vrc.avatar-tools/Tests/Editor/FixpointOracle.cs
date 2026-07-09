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

        // Write yaml to a temp file under testRoot, compile into a fresh sub-folder, return the loaded controller.
        internal static AnimatorController CompileTo(string testRoot, string yaml, string name, string tag)
        {
            string y = testRoot + "/" + name + "_" + tag + ".yaml";
            File.WriteAllText(y, yaml);
            string outDir = testRoot + "/out_" + name + "_" + tag;
            Directory.CreateDirectory(outDir);
            AssetDatabase.Refresh();
            string res = CompileController.Compile(Path.GetFullPath(y), outDir, whatIf: false);
            StringAssert.Contains("=> OK", res, "compile (" + tag + ") is clean");
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
