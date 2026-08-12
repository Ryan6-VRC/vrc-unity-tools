using System;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AgentTools.Tests
{
    /// <summary>
    /// The bake scope's STATE MACHINE, exercised on paths that never enter the SDK chain. What a real bake
    /// does to a clone is not unit-testable — it needs a composed avatar, the full hook chain, and a live
    /// project — so that half is measured live and recorded in <c>AvatarBake</c>'s doc comment. What is
    /// testable here is everything a caller can get wrong: reading past the scope, disposing twice, and the
    /// exact shape of the note that gets spliced into a caller's verdict line.
    /// </summary>
    public class AvatarBakeScopeTests
    {
        [Test]
        public void NullSource_failsWithoutEnteringTheChain()
        {
            using (var bake = AvatarBake.Begin(null))
            {
                Assert.IsFalse(bake.Ok);
                Assert.AreEqual("clone (source was null)", bake.FailedStage);
                // Not a crash — so the crash channel stays empty and a caller reporting `Failure` first
                // does not describe a null argument as a thrown build hook.
                Assert.IsNull(bake.Failure);
            }
        }

        [Test]
        public void CloneRefusesAfterTheScopeCloses()
        {
            var bake = AvatarBake.Begin(null);
            bake.Dispose();

            // The lifted trap: a post-callback has run and gutted the clone, so a late read is refused rather
            // than silently returning an avatar whose controllers and meshes are destroyed.
            var ex = Assert.Throws<InvalidOperationException>(() => { var _ = bake.Clone; });
            StringAssert.Contains("after the scope closed", ex.Message);
        }

        [Test]
        public void DisposeIsIdempotent()
        {
            var bake = AvatarBake.Begin(null);
            bake.Dispose();
            Assert.DoesNotThrow(() => bake.Dispose());
        }

        [Test]
        public void CleanupNoteIsNullOnACleanTeardown()
        {
            var bake = AvatarBake.Begin(null);
            bake.Dispose();
            // Null, never "" — callers accumulate this with `residualNote +=` and then branch on
            // IsNullOrEmpty to decide whether a run may ship its clean-success token. An empty string would
            // survive that branch; a stray space would not.
            Assert.IsNull(bake.CleanupNote);
        }

        [Test]
        public void CleanupNoteKeepsTheVerdictTokenShape()
        {
            string note = AvatarBake.FormatCleanupNote(new InvalidOperationException("boom"));

            // Pinned verbatim because this string is spliced into a CALLER's one-line verdict. The leading
            // space is what keeps it from running into the token before it; the key is what a reader greps.
            Assert.AreEqual(" note=postprocess-cleanup-threw: InvalidOperationException", note);
            // The caller owns the line's tool tag — a note carrying its own would contradict the head.
            StringAssert.DoesNotContain("[AvatarBake]", note);
        }

        [Test]
        public void CleanupNoteAppendsWithoutDisturbingTheEmptyCase()
        {
            // How callers actually consume it: `string += null` is a no-op, so a clean bake leaves the
            // accumulator empty and the clean-success branch intact.
            string residual = "";
            residual += (string)null;
            Assert.IsTrue(string.IsNullOrEmpty(residual));

            residual += AvatarBake.FormatCleanupNote(new Exception("x"));
            Assert.IsFalse(string.IsNullOrEmpty(residual));
            Assert.IsTrue(residual.StartsWith(" note="), "the note must stay separable from what precedes it");
        }
    }
}
