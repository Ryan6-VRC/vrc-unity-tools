using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Pure helpers ONLY (FramingGeometry / TryParseBg / YawOf / PitchOf / BundledPoses / NormalizeToken /
    // ResolvePose).
    // Everything expression-side resolves against a BAKED avatar, so it is a scene object verified live
    // (execute_code) by the coordinator, never in NUnit. No test here may create a GameObject,
    // add a VRC_AvatarDescriptor, or call RenderThumbnail.Render — that class of EditMode test
    // SIGSEGV-crashes this project's suite. In-memory AnimationClips are fine (no scene object).
    //
    // The pose tests are deliberately GLOB-DRIVEN, never naming a specific RTPose_*: the bundled set is
    // content that U3 churns (it replaces the two hand-authored poses wholesale). A test that hard-codes
    // "clasped" asserts on someone else's content and breaks the moment they land.
    [TestFixture]
    public class RenderThumbnailTests
    {
        // Asserts the VALUES, not just their order: framing changed meaning (dolly distance -> subject
        // span), and an ordering-only assertion stays green straight through a semantic swap like that.
        [Test]
        public void Framing_SpansAreSubjectHeights()
        {
            RenderThumbnailCore.FramingGeometry("bust", out float bustSpan, out float bustDrop);
            RenderThumbnailCore.FramingGeometry("half", out float halfSpan, out float halfDrop);
            RenderThumbnailCore.FramingGeometry("full", out float fullSpan, out float fullDrop);

            Assert.AreEqual(0.34f, bustSpan, 0.001f, "bust is a fixed face-sized height, decoupled from viewpoint");
            Assert.AreEqual(0.60f, halfSpan, 0.001f, "half is a fixed face/chest height, decoupled from viewpoint");
            Assert.AreEqual(2.00f, fullSpan, 0.001f, "full stays a whole-body span (still viewpoint-tracked)");

            // The eyes sit near the TOP of the subject, so the aim has to drop further as the span grows —
            // a single coefficient cuts the feet off at full framing.
            Assert.AreEqual(0.12f, bustDrop, 0.001f);
            Assert.AreEqual(0.18f, halfDrop, 0.001f, "half aims lower than bust (U5: 0.12 -> 0.18)");
            Assert.AreEqual(0.37f, fullDrop, 0.001f, "full must aim lower to seat the feet in frame — "
                + "ordering alone would pass 0.001/0.001/10 and destroy the framing");

            // Deliberately NOT asserting full crown clearance: some crop is wanted (a thumbnail is
            // displayed small, and a tight one reads as intentional), and chasing it on tall anime hair
            // would cost the bust crop. Feet in frame at `full` IS load-bearing, though.
            const float WorstFeetBelowEyes = 0.95f, Ref = 1.6f;
            Assert.Greater(fullSpan / Ref * (0.5f + fullDrop), WorstFeetBelowEyes,
                "full framing must seat the feet — measured, the lowest drawn point sits ~0.95 x view "
                + "height below the view point across the vendor bases");
        }

        [Test]
        public void Framing_Unknown_Throws()
        {
            var ex = Assert.Throws<System.ArgumentException>(
                () => RenderThumbnailCore.FramingGeometry("zoom", out _, out _));
            StringAssert.Contains("bust", ex.Message);
        }

        [Test]
        public void Bg_SolidHexParses_GarbageFails()
        {
            Assert.IsTrue(RenderThumbnailCore.TryParseBg("#204060", out Color top, out Color bottom));
            Assert.AreEqual(0x20 / 255f, top.r, 0.01f);
            Assert.AreEqual(top, bottom, "a solid bg must yield an identical pair — that is what selects the "
                + "solid-clear path over the gradient command buffer");

            Assert.IsFalse(RenderThumbnailCore.TryParseBg("blue", out _, out _));
        }

        [Test]
        public void Bg_GradientPairParses()
        {
            Assert.IsTrue(RenderThumbnailCore.TryParseBg("#204060:#8090A0", out Color top, out Color bottom));
            Assert.AreEqual(0x20 / 255f, top.r, 0.01f, "the FIRST stop is the top of the frame");
            Assert.AreEqual(0x80 / 255f, bottom.r, 0.01f);
            Assert.AreNotEqual(top, bottom);

            // #RRGGBBAA must keep resolving as one solid colour — the ':' is what distinguishes the forms.
            Assert.IsTrue(RenderThumbnailCore.TryParseBg("#204060FF", out Color solid, out Color solidB));
            Assert.AreEqual(solid, solidB);

            Assert.IsFalse(RenderThumbnailCore.TryParseBg("#204060:", out _, out _));
            Assert.IsFalse(RenderThumbnailCore.TryParseBg("#204060:8090A0", out _, out _), "both stops need '#'");
        }

        // The camera solve's sign convention, which is invisible in code review and inverts the whole
        // feature if wrong: positive yaw points toward +X, and the automatic oblique must land on the SAME
        // side as the head's turn (measured off a real rig — see the pose-angle study table).
        [Test]
        public void YawOf_IsSignedAboutY_PositiveTowardX()
        {
            Assert.AreEqual(0f, RenderThumbnailCore.YawOf(Vector3.forward), 0.01f);
            Assert.AreEqual(90f, RenderThumbnailCore.YawOf(Vector3.right), 0.01f);
            Assert.AreEqual(-90f, RenderThumbnailCore.YawOf(Vector3.left), 0.01f);
        }

        [Test]
        public void PitchOf_IsPositiveLookingUp()
        {
            Assert.AreEqual(0f, RenderThumbnailCore.PitchOf(Vector3.forward), 0.01f);
            Assert.Greater(RenderThumbnailCore.PitchOf(new Vector3(0f, 1f, 1f)), 0f, "chin raised reads positive");
            Assert.Less(RenderThumbnailCore.PitchOf(new Vector3(0f, -1f, 1f)), 0f);
        }

        // The rig-portability property the whole head-tracking feature rests on, and the one the canonical
        // YawOf/PitchOf tests above CANNOT catch by construction.
        //
        // Unity does not normalize humanoid bone axes. Extracting an angle from each forward vector and
        // subtracting cancels a constant offset but NOT the axis dependence: on a head bone whose +Z runs up
        // the neck — how Blender authors orient a bone, so most of the VRChat population — that form returns
        // yaw 0 for every pose (tracking silently dead) and inverts pitch (a chin-up pose shot from below).
        // Taking the delta rotation FIRST and extracting once is invariant to the rest orientation.
        [Test]
        public void HeadAngles_AreInvariantToTheRestBoneOrientation()
        {
            var rests = new[]
            {
                Quaternion.identity,                      // bone +Z = character forward
                Quaternion.Euler(-90f, 0f, 0f),           // bone +Z = up, along the neck
                Quaternion.Euler(0f, 90f, 0f),            // bone +Z = sideways
                Quaternion.Euler(23f, 41f, 17f),          // arbitrary skew
            };
            var yawTurn = Quaternion.AngleAxis(30f, Vector3.up);
            var chinUp = Quaternion.AngleAxis(-20f, Vector3.right);

            foreach (var rest in rests)
            {
                Vector3 yawFwd = (yawTurn * rest * Quaternion.Inverse(rest)) * Vector3.forward;
                Vector3 pitchFwd = (chinUp * rest * Quaternion.Inverse(rest)) * Vector3.forward;

                Assert.AreEqual(30f, RenderThumbnailCore.YawOf(yawFwd), 0.01f,
                    "a 30 deg head turn must read 30 deg whatever way the head bone happens to point");
                Assert.AreEqual(20f, RenderThumbnailCore.PitchOf(pitchFwd), 0.01f,
                    "a chin raise must read positive whatever way the head bone happens to point");
            }
        }

        // ===== Pose vocabulary = the Poses/ folder glob (no hard-wired array) =====

        [Test]
        public void BundledPoses_IsNonEmpty()
        {
            Assert.IsNotEmpty(RenderThumbnailCore.BundledPoses(),
                "no RTPose_*.anim found in " + RenderThumbnailCore.PosesFolder
                + " — the bundled pose vocabulary is sourced entirely from that folder");
        }

        [Test]
        public void BundledPoses_EachResolvesToItsOwnHumanoidClip()
        {
            // Doubles as a content gate: any RTPose_* that is not a humanoid muscle clip would not
            // retarget across rigs, and ResolvePose rejects it pre-bake.
            foreach (var entry in RenderThumbnailCore.BundledPoses())
            {
                Assert.IsTrue(RenderThumbnailCore.ResolvePose(entry.Key, out AnimationClip clip, out string err),
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
            Assert.AreEqual(RenderThumbnailCore.NormalizeToken("handonhip"), RenderThumbnailCore.NormalizeToken("Hand-On-Hip"));
            Assert.AreEqual(RenderThumbnailCore.NormalizeToken("handonhip"), RenderThumbnailCore.NormalizeToken("hand_on_hip"));
            Assert.AreEqual("thumbsup", RenderThumbnailCore.NormalizeToken("Thumbs up"));
        }

        [Test]
        public void Pose_NameMatchesCaseInsensitive()
        {
            string first = RenderThumbnailCore.BundledPoses().Keys.First();

            Assert.IsTrue(RenderThumbnailCore.ResolvePose(first.ToUpperInvariant(), out AnimationClip clip, out string err), err);
            Assert.IsNotNull(clip);
        }

        [Test]
        public void Pose_Null_IsFloor()
        {
            bool ok = RenderThumbnailCore.ResolvePose(null, out AnimationClip clip, out string err);

            Assert.IsTrue(ok);
            Assert.IsNull(clip);
            Assert.IsNull(err);
        }

        [Test]
        public void Pose_Unknown_ErrEnumeratesTheFolder()
        {
            // The advertised vocabulary is derived from disk, so it cannot drift from what ships.
            var bundled = RenderThumbnailCore.BundledPoses();

            bool ok = RenderThumbnailCore.ResolvePose("nope", out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            foreach (var name in bundled.Keys)
            {
                StringAssert.Contains(name, err);
                // What it advertises must be what it accepts.
                Assert.IsTrue(RenderThumbnailCore.ResolvePose(name, out AnimationClip _, out string _),
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
            var names = RenderThumbnailCore.BundledPoses().Keys
                .Select(RenderThumbnailCore.NormalizeToken).ToList();

            CollectionAssert.AllItemsAreUnique(names);
        }

    }
}
