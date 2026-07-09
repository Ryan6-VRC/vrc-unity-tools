using System.IO;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Synthetic round-trip stress fixtures spanning the whole schema vocabulary. Three arms:
    //  A (Fixpoint_AuthoredYaml): in-vocabulary breadth via hand-authored YAML — clean textual fixpoint.
    //  B (Refusal_*):            out-of-vocabulary constructs — DecompileController FAIL, no yaml written.
    //  C (Tolerance_*):          messy-but-legal input — decode normalizes it and notes the tolerance.
    public class RoundtripStressTests
    {
        private const string TestRoot = "Assets/Agent/Scratch/stress_tests";
        private const string FixDir = "Packages/com.ryan6vrc.avatar-tools/Tests/Editor/Fixtures/RoundtripStress";

        [SetUp]
        public void SetUp()
        {
            Directory.CreateDirectory(TestRoot);
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
            if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
            AssetDatabase.Refresh();
        }

        // ── Arm A: authored-YAML clean fixpoint ──────────────────────────────────────────────────
        // The `name` argument MUST equal the fixture's `controller:` value (CompileTo loads <name>.controller).
        [TestCase("blendtrees.yaml", "Blendtrees_Fx")]
        [TestCase("addressing.yaml", "Addressing_Fx")]
        public void Fixpoint_AuthoredYaml(string fixture, string name)
        {
            string yaml = FixpointOracle.ReadPackageText(FixDir + "/" + fixture);
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, name, "c0");
            string yamlA = FixpointOracle.Decode(c0);
            var c1 = FixpointOracle.CompileTo(TestRoot, yamlA, name, "c1");
            string yamlB = FixpointOracle.Decode(c1);
            Assert.AreEqual(yamlA, yamlB, "authored fixture reaches a textual fixpoint: " + fixture);
            StringAssert.Contains("=> PASS", CheckAnimator.Lint(c1, "explicit", null, null, null));
        }

        // decode(C0)==decode(C1) proves fixpoint-stability, NOT that the AUTHORED arrangement survived.
        // Assert the hand-placed off-grid coordinate reaches the decoded YAML (layout round-trip).
        [Test]
        public void Fixpoint_Addressing_PreservesAuthoredLayout()
        {
            string yaml = FixpointOracle.ReadPackageText(FixDir + "/addressing.yaml");
            var c0 = FixpointOracle.CompileTo(TestRoot, yaml, "Addressing_Fx", "c0");
            string decoded = FixpointOracle.Decode(c0);
            StringAssert.Contains("layout:", decoded, "the authored arrangement survives decode");
            StringAssert.Contains("[720, 40]", decoded, "the authored off-grid Top coordinate is preserved");
        }
    }
}
