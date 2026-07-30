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
        // The framing REQUIREMENT, not the calibration. Echoing the six private consts made this file an
        // edit on every recalibration while proving nothing a recalibration could get wrong; what actually
        // has to hold is the span/drop ordering (spans are subject heights now, not dolly distances — an
        // ordering-only check on spans alone would survive that semantic swap, so the drops are ordered too)
        // plus the one measured constraint: the feet stay in frame at `full`.
        [Test]
        public void Framing_SpansAreSubjectHeights()
        {
            RenderThumbnailCore.FramingGeometry("bust", out float bustSpan, out float bustDrop);
            RenderThumbnailCore.FramingGeometry("half", out float halfSpan, out float halfDrop);
            RenderThumbnailCore.FramingGeometry("full", out float fullSpan, out float fullDrop);

            Assert.Less(bustSpan, halfSpan, "bust is the tightest subject height");
            Assert.Less(halfSpan, fullSpan, "full is a whole-body span");
            // The eyes sit near the TOP of the subject, so the aim has to drop further as the span grows —
            // a single coefficient cuts the feet off at full framing.
            Assert.Less(bustDrop, halfDrop, "half aims lower than bust");
            Assert.Less(halfDrop, fullDrop, "full aims lower still");

            // Deliberately NOT asserting full crown clearance: some crop is wanted (a thumbnail is
            // displayed small, and a tight one reads as intentional), and chasing it on tall anime hair
            // would cost the bust crop. Feet in frame at `full` IS load-bearing, though — and it is what
            // ordering alone cannot give: 0.001/0.001/10 is ordered and destroys the framing.
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
        // side as the head's turn (measured off a real rig — see the pose-angle study table). Pitch shares
        // the fixture: same direction vectors, the other axis of the same convention.
        [Test]
        public void YawOf_IsSignedAboutY_PositiveTowardX()
        {
            Assert.AreEqual(0f, RenderThumbnailCore.YawOf(Vector3.forward), 0.01f);
            Assert.AreEqual(90f, RenderThumbnailCore.YawOf(Vector3.right), 0.01f);
            Assert.AreEqual(-90f, RenderThumbnailCore.YawOf(Vector3.left), 0.01f);

            Assert.AreEqual(0f, RenderThumbnailCore.PitchOf(Vector3.forward), 0.01f);
            Assert.Greater(RenderThumbnailCore.PitchOf(new Vector3(0f, 1f, 1f)), 0f, "chin raised reads positive");
            Assert.Less(RenderThumbnailCore.PitchOf(new Vector3(0f, -1f, 1f)), 0f);
        }

        // The rig-portability property the whole head-tracking feature rests on, and the one the canonical
        // YawOf/PitchOf test above CANNOT catch by construction.
        //
        // Unity does not normalize humanoid bone axes. HeadFacing therefore takes the DELTA rotation first
        // (posed · rest⁻¹) and extracts one angle from it. Extracting an angle from each forward vector and
        // SUBTRACTING is the tempting-and-wrong alternative: it cancels a constant offset but not the axis
        // dependence. This fixture pins both forms side by side, so the numbers the subtract form actually
        // produces are on the record rather than asserted from the docstring.
        //
        // This BINDS to production: it drives the quaternion-only `HeadFacing` overload that the Transform door
        // delegates to, so a switch to the subtract form reds this test. The subtract form is computed here too,
        // and asserted to disagree — without that, a fixture whose rest orientation happens to make the two
        // forms agree would pass while proving nothing.
        [Test]
        public void HeadAngles_AreInvariantToTheRestBoneOrientation()
        {
            var rests = new[]
            {
                Quaternion.identity,                      // bone +Z = character forward
                Quaternion.Euler(-90f, 0f, 0f),           // bone +Z = up, along the neck (the Blender default)
                Quaternion.Euler(0f, 90f, 0f),            // bone +Z = sideways
                Quaternion.Euler(23f, 41f, 17f),          // arbitrary skew
            };
            var yawTurn = Quaternion.AngleAxis(30f, Vector3.up);
            var chinUp = Quaternion.AngleAxis(-20f, Vector3.right);

            foreach (var rest in rests)
            {
                // A real head turn composes ONTO the bone's rest orientation, so this is what the posed bone
                // reports; `rest` genuinely survives into the input, which is the whole point of varying it.
                Vector3 restFwd = rest * Vector3.forward;
                Vector3 yawPosed = (yawTurn * rest) * Vector3.forward;
                Vector3 pitchPosed = (chinUp * rest) * Vector3.forward;

                // Production, not a re-implementation: `rootRot` is identity here, so the posed bone rotation in
                // the root's basis is exactly `turn * rest`.
                RenderThumbnailCore.HeadFacing(yawTurn * rest, rest, out float yawDeg, out _);
                RenderThumbnailCore.HeadFacing(chinUp * rest, rest, out _, out float pitchDeg);

                Assert.AreEqual(30f, yawDeg, 0.01f,
                    "a 30 deg head turn must read 30 deg whatever way the head bone happens to point");
                Assert.AreEqual(20f, pitchDeg, 0.01f,
                    "a chin raise must read positive whatever way the head bone happens to point");

                float yawSubtract = RenderThumbnailCore.YawOf(yawPosed) - RenderThumbnailCore.YawOf(restFwd);
                float pitchSubtract = RenderThumbnailCore.PitchOf(pitchPosed) - RenderThumbnailCore.PitchOf(restFwd);

                if (rest == Quaternion.identity)
                {
                    // Identity rest is exactly where the two forms agree — which is why every canonical
                    // fixture built on it is blind to the difference.
                    Assert.AreEqual(30f, yawSubtract, 0.01f);
                    Assert.AreEqual(20f, pitchSubtract, 0.01f);
                }
                else
                {
                    Assert.That(Mathf.Abs(yawSubtract - 30f) > 0.5f || Mathf.Abs(pitchSubtract - 20f) > 0.5f,
                        "fixture must discriminate: rest " + rest.eulerAngles + " left the subtract form "
                        + "reading the truth, so it cannot witness the defect");
                }
            }

            // The measured defect itself, on the most common convention (bone +Z up the neck): tracking
            // silently dead in yaw, and pitch inverted — a chin-up pose shot from below.
            var neckUp = Quaternion.Euler(-90f, 0f, 0f);
            Assert.AreEqual(0f,
                RenderThumbnailCore.YawOf((yawTurn * neckUp) * Vector3.forward)
                - RenderThumbnailCore.YawOf(neckUp * Vector3.forward), 0.01f,
                "subtract form reads 0 deg of yaw for a real 30 deg turn");
            Assert.AreEqual(-20f,
                RenderThumbnailCore.PitchOf((chinUp * neckUp) * Vector3.forward)
                - RenderThumbnailCore.PitchOf(neckUp * Vector3.forward), 0.01f,
                "subtract form inverts a chin raise");
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
            //
            // Name-collision guard, too. BundledPoses keys on the raw filename, so two files whose
            // NORMALIZED names collide are two entries, and ResolvePose's first-match-wins makes one of them
            // unreachable — silently, since there is no runtime collision check. Build time is the only place
            // to catch it: poses land by dropping files into the folder with nobody reviewing this tool. The
            // uniqueness assert comes first because it names the collision; the per-entry path assert below
            // catches it too, but reports it as a mystery path mismatch.
            var normalized = RenderThumbnailCore.BundledPoses().Keys
                .Select(RenderThumbnailCore.NormalizeToken).ToList();
            CollectionAssert.AllItemsAreUnique(normalized);

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
    }
}
