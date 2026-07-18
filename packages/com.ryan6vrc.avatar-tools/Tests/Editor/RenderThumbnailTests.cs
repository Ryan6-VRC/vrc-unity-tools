using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor.Tests
{
    // Task 0: pure helpers + the whatIf preflight only — RenderThumbnail's bake/capture pipeline mutates
    // live objects and is verified live (execute_code), never in NUnit; see
    // docs/2026-07-17-render-thumbnail-design.md §Verification.
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
    }
}
