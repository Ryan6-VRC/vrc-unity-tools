using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Pure helpers ONLY (FramingDistance / TryParseBg / BundledPoses / NormalizeToken / ResolvePose).
    // Everything expression-side resolves against a BAKED avatar, so it is a scene object verified live
    // (execute_code) by the coordinator, never in NUnit; see
    // docs/2026-07-17-render-thumbnail-design.md §Verification. No test here may create a GameObject,
    // add a VRC_AvatarDescriptor, or call RenderThumbnail.Render — that class of EditMode test
    // SIGSEGV-crashes this project's suite. In-memory AnimationClips are fine (no scene object).
    //
    // The pose tests are deliberately GLOB-DRIVEN, never naming a specific RTPose_*: the bundled set is
    // content that U3 churns (it replaces the two hand-authored poses wholesale). A test that hard-codes
    // "clasped" asserts on someone else's content and breaks the moment they land.
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

        // ===== Pose vocabulary = the Poses/ folder glob (no hard-wired array) =====

        [Test]
        public void BundledPoses_IsNonEmpty()
        {
            Assert.IsNotEmpty(RenderThumbnail.BundledPoses(),
                "no RTPose_*.anim found in " + RenderThumbnail.PosesFolder
                + " — the bundled pose vocabulary is sourced entirely from that folder");
        }

        [Test]
        public void BundledPoses_EachResolvesToItsOwnHumanoidClip()
        {
            // Doubles as a content gate: any RTPose_* that is not a humanoid muscle clip would not
            // retarget across rigs, and ResolvePose rejects it pre-bake.
            foreach (var entry in RenderThumbnail.BundledPoses())
            {
                Assert.IsTrue(RenderThumbnail.ResolvePose(entry.Key, out AnimationClip clip, out string err),
                    entry.Key + " (" + entry.Value + ") failed to resolve: " + err);
                // Assert WHICH clip came back, not merely that one did: with first-match-wins, a name
                // collision resolves two entries to the same clip and the test would still pass green.
                Assert.AreEqual(entry.Value, UnityEditor.AssetDatabase.GetAssetPath(clip),
                    "'" + entry.Key + "' resolved to the wrong asset");
                Assert.IsTrue(clip.isHumanMotion, entry.Key + " must be a humanoid muscle clip");
            }
        }

        [Test]
        public void Token_NormalizesCaseAndPunctuation()
        {
            // Shared by pose names and FX state names: "Hand-On-Hip", "hand_on_hip" and "HandOnHip" are
            // one token, as are "Thumbs up" and "thumbsup".
            Assert.AreEqual(RenderThumbnail.NormalizeToken("handonhip"), RenderThumbnail.NormalizeToken("Hand-On-Hip"));
            Assert.AreEqual(RenderThumbnail.NormalizeToken("handonhip"), RenderThumbnail.NormalizeToken("hand_on_hip"));
            Assert.AreEqual("thumbsup", RenderThumbnail.NormalizeToken("Thumbs up"));
        }

        [Test]
        public void Pose_NameMatchesCaseInsensitive()
        {
            string first = RenderThumbnail.BundledPoses().Keys.First();

            Assert.IsTrue(RenderThumbnail.ResolvePose(first.ToUpperInvariant(), out AnimationClip clip, out string err), err);
            Assert.IsNotNull(clip);
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
        public void Pose_Unknown_ErrEnumeratesTheFolder()
        {
            // The advertised vocabulary is derived from disk, so it cannot drift from what ships.
            var bundled = RenderThumbnail.BundledPoses();

            bool ok = RenderThumbnail.ResolvePose("nope", out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            foreach (var name in bundled.Keys)
            {
                StringAssert.Contains(name, err);
                // What it advertises must be what it accepts.
                Assert.IsTrue(RenderThumbnail.ResolvePose(name, out AnimationClip _, out string _),
                    "advertised pose '" + name + "' does not resolve");
            }
            StringAssert.Contains("path/GUID", err);
        }

        [Test]
        public void BundledPoses_NormalizedNamesAreUnique()
        {
            // THIS TEST IS THE ONLY GUARD. There is no runtime collision check: matching is
            // first-match-wins, so two files normalizing to one name would silently make one pose
            // unreachable. Build time is the right place to catch it — poses land by dropping files into
            // the folder, with nobody reviewing this tool.
            var names = RenderThumbnail.BundledPoses().Keys
                .Select(RenderThumbnail.NormalizeToken).ToList();

            CollectionAssert.AllItemsAreUnique(names);
        }

    }
}
