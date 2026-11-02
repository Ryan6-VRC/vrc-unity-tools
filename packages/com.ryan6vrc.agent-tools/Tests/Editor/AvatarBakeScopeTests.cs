using System;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AgentTools.Tests
{
    /// <summary>
    /// The bake scope's lifetime rules, driven through the seam constructor's injected callbacks, so no
    /// test here runs a real hook chain.
    /// </summary>
    public class AvatarBakeScopeTests
    {
        private GameObject _source;

        [SetUp] public void SetUp() { _source = new GameObject("bake-scope-src"); }
        [TearDown] public void TearDown() { if (_source != null) UnityEngine.Object.DestroyImmediate(_source); }

        private static AvatarBakeScope Scope(GameObject src, Func<GameObject, bool> pre, Action post)
        {
            return new AvatarBakeScope(src, "__test_clone", pre, post);
        }

        [Test]
        public void SuccessfulBake_handsBackTheCloneInTheActiveScene()
        {
            using (var bake = Scope(_source, go => true, () => { }))
            {
                Assert.IsTrue(bake.Ok);
                Assert.IsNull(bake.FailedStage);
                Assert.IsNull(bake.Failure);
                Assert.IsNotNull(bake.Clone);
                Assert.AreEqual("__test_clone", bake.Clone.name);
                Assert.AreEqual(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), bake.Clone.scene);
            }
        }

        [Test]
        public void ASourceOutsideTheActiveScene_isClonedIntoTheActiveScene()
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var other = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_source, other);
                using (var bake = Scope(_source, go => true, () => { }))
                {
                    Assert.AreEqual(active, bake.Clone.scene);
                    Assert.AreNotEqual(other, bake.Clone.scene);
                }
            }
            finally
            {
                // Closing the scene destroys _source with it; TearDown's null check then skips it.
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(other);
            }
        }

        [Test]
        public void Dispose_destroysTheCloneAndFiresThePairing()
        {
            int post = 0;
            GameObject clone;
            var bake = Scope(_source, go => true, () => post++);
            clone = bake.Clone;
            Assert.AreEqual(0, post, "the pairing must not fire before the caller has read the clone");

            bake.Dispose();
            Assert.AreEqual(1, post);
            Assert.IsTrue(clone == null, "the scope owns the clone's lifetime");
        }

        [Test]
        public void DisposeIsIdempotent_andFiresThePairingExactlyOnce()
        {
            int post = 0;
            var bake = Scope(_source, go => true, () => post++);
            bake.Dispose();
            bake.Dispose();
            bake.Dispose();
            Assert.AreEqual(1, post);
        }

        [Test]
        public void ARefusedBake_stillOwesThePairing()
        {
            int post = 0;
            var bake = Scope(_source, go => false, () => post++);
            Assert.IsFalse(bake.Ok);
            Assert.IsNotNull(bake.FailedStage);
            Assert.IsNull(bake.Failure, "a refusal is not a crash — the channels stay disjoint");

            bake.Dispose();
            // The SDK's hooks keep state across the pair, so a refused bake owes the post-callback too.
            Assert.AreEqual(1, post);
        }

        [Test]
        public void AThrownHook_stillOwesThePairing_andKeepsTheException()
        {
            int post = 0;
            var boom = new InvalidOperationException("hook exploded");
            var bake = Scope(_source, go => { throw boom; }, () => post++);

            Assert.IsFalse(bake.Ok);
            Assert.AreSame(boom, bake.Failure, "kept as the exception, so a caller can chain it and keep the stack");
            Assert.IsNull(bake.FailedStage);
            Assert.IsTrue(bake.EnteredChain);
            StringAssert.Contains("OnPreprocessAvatar threw InvalidOperationException", bake.DescribeFailure());

            bake.Dispose();
            Assert.AreEqual(1, post);
        }

        [Test]
        public void AFailureBeforeTheChain_doesNotFireThePairing_andSaysSo()
        {
            int post = 0;
            var bake = Scope(null, go => true, () => post++);

            Assert.IsFalse(bake.Ok);
            Assert.AreEqual("clone (source was null)", bake.FailedStage);
            Assert.IsFalse(bake.EnteredChain);

            bake.Dispose();
            Assert.AreEqual(0, post);
        }

        [Test]
        public void ADestroyedSource_readsAsNull_andDoesNotNameTheChain()
        {
            var bad = new GameObject("destroyed-before-use");
            UnityEngine.Object.DestroyImmediate(bad);
            var bake = Scope(bad, go => true, () => { });
            bake.Dispose();

            Assert.IsFalse(bake.Ok);
            Assert.AreEqual("clone (source was null)", bake.FailedStage);
            StringAssert.DoesNotContain("OnPreprocessAvatar", bake.DescribeFailure(),
                "the chain was never entered, so it must not be named");
        }

        [Test]
        public void DescribeThrow_namesSetupSeparatelyFromTheChain()
        {
            var e = new InvalidOperationException("boom");
            Assert.AreEqual("bake clone setup threw InvalidOperationException: boom", AvatarBakeScope.DescribeThrow(e, false));
            Assert.AreEqual("OnPreprocessAvatar threw InvalidOperationException: boom", AvatarBakeScope.DescribeThrow(e, true));
        }

        [Test]
        public void AThrowingPostCallback_isNotedAndLogged_notRaised()
        {
            var bake = Scope(_source, go => true, () => { throw new InvalidOperationException("cleanup failed"); });

            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(@"\[AvatarBake\] OnPostprocessAvatar threw InvalidOperationException"));

            Assert.DoesNotThrow(() => bake.Dispose());
            Assert.AreEqual(" note=postprocess-cleanup-threw: InvalidOperationException", bake.CleanupNote);
        }

        [Test]
        public void CleanupNoteIsNullAfterACleanTeardown()
        {
            var bake = Scope(_source, go => true, () => { });
            bake.Dispose();
            Assert.IsNull(bake.CleanupNote);
        }

        [Test]
        public void CloneRefusesAfterTheScopeCloses()
        {
            var bake = Scope(_source, go => true, () => { });
            bake.Dispose();

            var ex = Assert.Throws<InvalidOperationException>(() => { var _ = bake.Clone; });
            StringAssert.Contains("after the scope closed", ex.Message);
        }

        [Test]
        public void CleanupNoteKeepsTheVerdictTokenShape()
        {
            string note = AvatarBake.FormatCleanupNote(new InvalidOperationException("boom"));

            Assert.AreEqual(" note=postprocess-cleanup-threw: InvalidOperationException", note);
            StringAssert.StartsWith(" note=", AvatarBake.FormatDestroyNote(new Exception("x")));
            StringAssert.DoesNotContain("[AvatarBake]", note);
        }
    }
}
