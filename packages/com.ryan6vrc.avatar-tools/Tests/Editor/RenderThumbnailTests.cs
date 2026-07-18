using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Pure helpers ONLY (FramingDistance / TryParseBg / ResolvePose). Render — including the whatIf
    // preflight and the target resolver it drives — mutates/reads live scene objects and is verified
    // live (execute_code) by the coordinator, never in NUnit; see
    // docs/2026-07-17-render-thumbnail-design.md §Verification. No test here may create a GameObject,
    // add a VRC_AvatarDescriptor, or call RenderThumbnail.Render — that class of EditMode test
    // SIGSEGV-crashes this project's suite.
    [TestFixture]
    public class RenderThumbnailTests
    {
        [Test]
        public void Framing_ThreeDistinctDistances()
        {
            float bust = RenderThumbnail.FramingDistance("bust");
            float half = RenderThumbnail.FramingDistance("half");
            float full = RenderThumbnail.FramingDistance("full");

            Assert.Less(bust, half, "bust must dolly less than half");
            Assert.Less(half, full, "half must dolly less than full");
        }

        [Test]
        public void Framing_Unknown_Throws()
        {
            var ex = Assert.Throws<System.ArgumentException>(() => RenderThumbnail.FramingDistance("zoom"));
            StringAssert.Contains("bust", ex.Message);
        }

        [Test]
        public void Bg_HexParses_GarbageFails()
        {
            Assert.IsTrue(RenderThumbnail.TryParseBg("#204060", out Color c));
            Assert.AreEqual(0x20 / 255f, c.r, 0.01f);

            Assert.IsFalse(RenderThumbnail.TryParseBg("blue", out _));
        }

        [Test]
        public void Pose_Null_IsFloor()
        {
            bool ok = RenderThumbnail.ResolvePose(null, out AnimationClip clip, out string err);

            Assert.IsTrue(ok);
            Assert.IsNull(clip);
            Assert.IsNull(err);
        }

        [Test]
        public void Pose_Unknown_ErrEnumeratesVocab()
        {
            bool ok = RenderThumbnail.ResolvePose("nope", out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            StringAssert.Contains("contrapposto", err);
            StringAssert.Contains("path/GUID", err);
        }

        [Test]
        public void Pose_BundledName_MatchesCaseInsensitive_ButClipNotYetAuthored()
        {
            // "CONTRAPPOSTO" must match the bundled vocabulary case-insensitively — it should fail only
            // because the .anim isn't authored until Task 3, never fall through to the "unknown pose"
            // branch (that would mean the case-insensitive match silently didn't fire).
            bool ok = RenderThumbnail.ResolvePose("CONTRAPPOSTO", out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            StringAssert.DoesNotContain("unknown pose", err);
            StringAssert.Contains("Task 3", err);
        }
    }
}
