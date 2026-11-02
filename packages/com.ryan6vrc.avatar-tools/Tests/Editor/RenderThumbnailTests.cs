using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Scene-free helpers only. Expression resolution needs a BAKED avatar, so it is verified live
    // (execute_code), never in NUnit. No test here may create a GameObject, add a VRC_AvatarDescriptor, or
    // call RenderThumbnail.Run: docs/verify.md §Test venue.
    //
    // The pose tests stay glob-driven and name no specific RTPose_*: the bundled set is content, and it
    // changes by dropping files into the folder.
    [TestFixture]
    public class RenderThumbnailTests
    {
        // The framing requirement, not the calibration: assert the span and drop orderings plus feet in frame
        // at `full`, and do not echo the private calibration consts.
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

            // Full crown clearance is deliberately not asserted: a thumbnail displays small, a tight crop
            // reads as intentional, and chasing clearance on tall anime hair would cost the bust crop. Feet
            // in frame is what ordering alone cannot give: 0.001/0.001/10 is ordered and destroys the framing.
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

        // The camera solve's sign convention, which is invisible in code review and inverts the feature if
        // wrong: the automatic oblique must land on the SAME side as the head's turn.
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

        // The subtract form is computed here too and asserted to disagree, because a rest orientation where the
        // two forms agree would pass while proving nothing.
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
                // A head turn composes ONTO the rest orientation, so `rest` survives into the input.
                Vector3 restFwd = rest * Vector3.forward;
                Vector3 yawPosed = (yawTurn * rest) * Vector3.forward;
                Vector3 pitchPosed = (chinUp * rest) * Vector3.forward;

                // `rootRot` is identity here, so the posed rotation in the root's basis is exactly `turn * rest`.
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

            // The subtract form's failure on the most common convention (bone +Z up the neck): yaw tracking
            // silently dead, and pitch inverted, so a chin-up pose is shot from below.
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
            // retarget across rigs, and ResolvePose rejects it.
            //
            // Also the only name-collision check: BundledPoses keys on the raw filename and ResolvePose takes
            // the first normalized match, so two files whose NORMALIZED names collide leave one unreachable.
            // The uniqueness assert comes first because it names the collision; the path assert below reports
            // it as a mystery mismatch.
            var normalized = RenderThumbnailCore.BundledPoses().Keys
                .Select(RenderThumbnailCore.NormalizeToken).ToList();
            CollectionAssert.AllItemsAreUnique(normalized);

            foreach (var entry in RenderThumbnailCore.BundledPoses())
            {
                Assert.IsTrue(RenderThumbnailCore.ResolvePose(entry.Key, out AnimationClip clip, out string err),
                    entry.Key + " (" + entry.Value + ") failed to resolve: " + err);
                Assert.AreEqual(entry.Value, UnityEditor.AssetDatabase.GetAssetPath(clip),
                    "'" + entry.Key + "' resolved to the wrong asset");
                Assert.IsTrue(clip.isHumanMotion, entry.Key + " must be a humanoid muscle clip");
            }
        }

        [Test]
        public void Token_NormalizesCaseAndPunctuation()
        {
            // Shared by pose names and FX state names.
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
            var bundled = RenderThumbnailCore.BundledPoses();

            bool ok = RenderThumbnailCore.ResolvePose("nope", out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            foreach (var name in bundled.Keys)
            {
                StringAssert.Contains(name, err);
                Assert.IsTrue(RenderThumbnailCore.ResolvePose(name, out AnimationClip _, out string _),
                    "advertised pose '" + name + "' does not resolve");
            }
            StringAssert.Contains("path/GUID", err);
        }

        // ----- Camera knobs -----------------------------------------------------------------------------

        // Default-knob solves captured by reflection, NOT computed from this code, which would pass by
        // construction. Cases 3 and 4 saturate the head-follow pitch clamp in both directions (fwd.y =
        // ∓sin 20°), which a widened total clamp would silently change.
        private static CameraSolutionCase[] Goldens => new[]
        {
            new CameraSolutionCase("bust", 30f, null, 0f, 0f, new Vector3(0f, 1.2f, 0f), 0f, 1f, 1.2f,
                new Vector3(0.16259706f, 1.15920007f, 0.613598764f), new Vector3(-0.2249511f, 0f, -0.9743701f), 13f),
            new CameraSolutionCase("half", 30f, -20f, 10f, 12f, new Vector3(0.1f, 1.3f, 0.05f), 0f, 1f, 1.3f,
                new Vector3(-0.128807187f, 1.30903161f, 1.14031422f), new Vector3(0.1726969f, -0.104528494f, -0.979413033f), -10f),
            new CameraSolutionCase("full", 40f, null, -8f, 60f, new Vector3(0f, 1.1f, 0f), 35f, 1.2f, 1.1f,
                new Vector3(0.41922757f, 1.26474643f, 2.09065318f), new Vector3(-0.2273322f, -0.342020124f, -0.9117798f), -21f),
            new CameraSolutionCase("bust", 25f, 0f, 3f, -50f, new Vector3(-0.2f, 1.25f, 0.3f), -90f, 0.9f, 1.25f,
                new Vector3(-0.84762913f, 0.9772387f, 0.333940864f), new Vector3(0.9384048f, 0.3420202f, -0.04917995f), 3f),
        };

        internal struct CameraSolutionCase
        {
            internal string Token; internal float Fov; internal float? Yaw; internal float HeadYaw, HeadPitch;
            internal Vector3 Viewpoint; internal float RootEulerY, Scale, ViewY;
            internal Vector3 Pos, Fwd; internal float CamYaw;
            internal CameraSolutionCase(string token, float fov, float? yaw, float headYaw, float headPitch,
                Vector3 viewpoint, float rootEulerY, float scale, float viewY, Vector3 pos, Vector3 fwd, float camYaw)
            {
                Token = token; Fov = fov; Yaw = yaw; HeadYaw = headYaw; HeadPitch = headPitch; Viewpoint = viewpoint;
                RootEulerY = rootEulerY; Scale = scale; ViewY = viewY; Pos = pos; Fwd = fwd; CamYaw = camYaw;
            }

            internal RenderThumbnailCore.CameraSolution Solve(float zoom = 1f, float pitch = 0f, float headroom = 0f)
            {
                RenderThumbnailCore.FramingGeometry(Token, out float span, out float drop);
                return RenderThumbnailCore.SolveCamera(Token, span, drop, Fov, Yaw, HeadYaw, HeadPitch, Viewpoint,
                    Quaternion.Euler(0f, RootEulerY, 0f), Scale, ViewY, RootEulerY, zoom, pitch, headroom);
            }
        }

        private static void AssertNear(Vector3 expected, Vector3 actual, string what)
            => Assert.Less((expected - actual).magnitude, 1e-4f, what + ": expected " + expected.ToString("F5")
                + ", got " + actual.ToString("F5"));

        [Test]
        public void Camera_DefaultKnobs_ReproduceThePreKnobSolve()
        {
            foreach (var c in Goldens)
            {
                var sol = c.Solve();
                AssertNear(c.Pos, sol.Position, c.Token + " position");
                AssertNear(c.Fwd, sol.Rotation * Vector3.forward, c.Token + " forward");
                Assert.AreEqual(c.CamYaw, sol.CamYaw, 1e-4f, c.Token + " camYaw");
            }
        }

        [Test]
        public void Camera_ZoomScalesDistance_HeadroomRaisesAim_PitchRaisesCamera()
        {
            var c = Goldens[0];   // bust, frontal-ish, no head pitch
            var baseSol = c.Solve();

            // Every term of the camera's offset from the view point — the aim-drop, the lateral looking-room
            // and the solved distance — is proportional to the span at a fixed orientation.
            var zoomed = c.Solve(zoom: 1.25f);
            AssertNear((baseSol.Position - c.Viewpoint) / 1.25f, zoomed.Position - c.Viewpoint,
                "zoom scales the camera's whole offset from the view point by 1/zoom");
            Assert.AreEqual(1f, Vector3.Dot(baseSol.Rotation * Vector3.forward, zoomed.Rotation * Vector3.forward),
                1e-5f, "zoom leaves the view direction unchanged");

            RenderThumbnailCore.FramingGeometry("bust", out float span, out float _);
            float d0 = (span * 0.5f) / Mathf.Tan(c.Fov * 0.5f * Mathf.Deg2Rad);
            Vector3 aim0 = baseSol.Position + baseSol.Rotation * Vector3.forward * d0;

            var lifted = c.Solve(headroom: 0.2f);
            Vector3 aimH = lifted.Position + lifted.Rotation * Vector3.forward * d0;
            Assert.AreEqual(aim0.y + 0.2f * span, aimH.y, 1e-4f, "headroom raises the aim by headroom × span");

            var high = c.Solve(pitch: 10f);
            Assert.Greater(high.Position.y, baseSol.Position.y, "positive pitch puts the camera higher");
            Assert.AreEqual(10f, high.CamElevation, 1e-4f, "camPitch reports elevation in the knob's sign");
            Assert.Less((high.Rotation * Vector3.forward).y, 0f, "a higher camera looks down");
        }

        [Test]
        public void Camera_PitchOffset_AddsAfterTheHeadFollowClamp()
        {
            var sat = Goldens[2];   // headPitch 60: head-follow saturates at 20° elevation
            Assert.AreEqual(20f, sat.Solve().CamElevation, 1e-4f, "head-follow keeps its own ±20 clamp");
            Assert.AreEqual(35f, sat.Solve(pitch: 20f).CamElevation, 1e-4f, "the offset stacks, then the total clamps at 35");
            Assert.AreEqual(0f, sat.Solve(pitch: -20f).CamElevation, 1e-4f, "the offset is not eaten by the inner clamp");
        }

        [Test]
        public void Knobs_RefuseOutOfRangeAndNaN_AndTheTooCloseCorner()
        {
            Assert.IsNull(RenderThumbnailCore.ValidateCameraKnobs(30f, 1f, 0f, 0f));
            Assert.IsNull(RenderThumbnailCore.ValidateCameraKnobs(90f, 1f, 0f, 0f), "today's closest shot stays legal");
            StringAssert.Contains("zoom", RenderThumbnailCore.ValidateCameraKnobs(30f, 1.7f, 0f, 0f));
            StringAssert.Contains("zoom", RenderThumbnailCore.ValidateCameraKnobs(30f, float.NaN, 0f, 0f));
            StringAssert.Contains("pitch", RenderThumbnailCore.ValidateCameraKnobs(30f, 1f, -21f, 0f));
            StringAssert.Contains("pitch", RenderThumbnailCore.ValidateCameraKnobs(30f, 1f, float.NaN, 0f));
            StringAssert.Contains("headroom", RenderThumbnailCore.ValidateCameraKnobs(30f, 1f, 0f, 0.31f));
            StringAssert.Contains("headroom", RenderThumbnailCore.ValidateCameraKnobs(30f, 1f, 0f, float.NaN));
            // zoom 1.6 is in range, but at fov 90 it would put the camera inside the head.
            StringAssert.Contains("lower zoom or fov", RenderThumbnailCore.ValidateCameraKnobs(90f, 1.6f, 0f, 0f));
        }
    }
}
