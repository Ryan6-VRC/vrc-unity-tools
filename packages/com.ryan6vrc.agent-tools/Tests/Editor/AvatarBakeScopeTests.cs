using System;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AgentTools.Tests
{
    /// <summary>
    /// The bake scope's LIFETIME RULES, driven through the seam constructor's injected callbacks so the
    /// paths that matter are reachable without a composed avatar and a live hook chain. What a real bake
    /// does to a clone is measured live and recorded in <c>AvatarBake</c>'s doc comment; what is asserted
    /// here is everything the scope itself promises — the pairing fires exactly once, fires on the FAILURE
    /// path too, never fires when the chain was not entered, and survives a callback that throws.
    /// <para>An earlier version of this fixture built every case with a null source, which returns before
    /// the chain is ever entered. Three of its tests passed with the code under test deleted, and the two
    /// teardown defects this fixture now covers shipped underneath them.</para>
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
        public void SuccessfulBake_handsBackTheCloneInTheSourcesScene()
        {
            using (var bake = Scope(_source, go => true, () => { }))
            {
                Assert.IsTrue(bake.Ok);
                Assert.IsNull(bake.FailedStage);
                Assert.IsNull(bake.Failure);
                Assert.IsNotNull(bake.Clone);
                Assert.AreEqual("__test_clone", bake.Clone.name);
                // The orphan sweep in RenderThumbnail's teardown depends on this: a clone stranded before the
                // caller moves it is only reachable if it was created into the source's scene.
                Assert.AreEqual(_source.scene, bake.Clone.scene);
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
            // The whole point of pairing from the scope: the SDK's hooks keep state across the pair, and the
            // failure path is where skipping it used to happen.
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
            // Nothing was entered, so nothing is owed — firing the post-callback here would move SDK state
            // that no preprocess had touched.
            Assert.AreEqual(0, post);
        }

        [Test]
        public void DescribeFailure_namesSetupSeparatelyFromTheChain()
        {
            // A setup throw must not be reported as OnPreprocessAvatar — that names a call that never ran.
            var bad = new GameObject("destroyed-before-use");
            UnityEngine.Object.DestroyImmediate(bad);
            var bake = Scope(bad, go => true, () => { });
            bake.Dispose();

            Assert.IsFalse(bake.Ok);
            StringAssert.DoesNotContain("OnPreprocessAvatar", bake.DescribeFailure(),
                "the chain was never entered, so it must not be named");
        }

        [Test]
        public void AThrowingPostCallback_isNotedAndLogged_notRaised()
        {
            var bake = Scope(_source, go => true, () => { throw new InvalidOperationException("cleanup failed"); });

            // Logged as WELL as noted, and this assertion is the reason: CompositionBake consumes the scope
            // with `using` and writes its artifact inside it, so a caller that only carries CleanupNote
            // reports a stale-hook-state failure to nobody. The console line is that caller's only channel.
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex(@"\[AvatarBake\] OnPostprocessAvatar threw InvalidOperationException"));

            // Never throws: Dispose runs inside callers' teardown finallys, where a throw would replace the
            // real pipeline exception with a cleanup one.
            Assert.DoesNotThrow(() => bake.Dispose());
            Assert.AreEqual(" note=postprocess-cleanup-threw: InvalidOperationException", bake.CleanupNote);
        }

        [Test]
        public void CleanupNoteIsNullAfterACleanTeardown()
        {
            var bake = Scope(_source, go => true, () => { });
            bake.Dispose();
            // Null, never "" — callers accumulate with `residualNote +=` then branch on IsNullOrEmpty to
            // decide whether a run may ship its clean-success token.
            Assert.IsNull(bake.CleanupNote);
        }

        [Test]
        public void CloneRefusesAfterTheScopeCloses()
        {
            var bake = Scope(_source, go => true, () => { });
            bake.Dispose();

            // The lifted trap: the post-callback has run and gutted the clone, so a late read is refused
            // rather than silently returning an avatar whose controllers and meshes are destroyed.
            var ex = Assert.Throws<InvalidOperationException>(() => { var _ = bake.Clone; });
            StringAssert.Contains("after the scope closed", ex.Message);
        }

        [Test]
        public void CleanupNoteKeepsTheVerdictTokenShape()
        {
            string note = AvatarBake.FormatCleanupNote(new InvalidOperationException("boom"));

            // Pinned verbatim because this string is spliced into a CALLER's one-line verdict. The leading
            // space is what keeps it from running into the token before it; the key is what a reader greps.
            Assert.AreEqual(" note=postprocess-cleanup-threw: InvalidOperationException", note);
            StringAssert.StartsWith(" note=", AvatarBake.FormatDestroyNote(new Exception("x")));
            // The caller owns the line's tool tag — a note carrying its own would contradict the head.
            StringAssert.DoesNotContain("[AvatarBake]", note);
        }
    }
}
