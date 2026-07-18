using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

namespace Ryan6Vrc.AvatarTools.Tests
{
    // Pure helpers ONLY (FramingDistance / TryParseBg / PoseCatalog / ResolvePose / IsExpressionClip /
    // ResolveExpression). Render — including the whatIf preflight, the target resolver it drives, and
    // the baked-clone binding assertion — mutates/reads live scene objects and is verified live
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
        public void PoseCatalog_IsNonEmpty()
        {
            var catalog = RenderThumbnail.PoseCatalog();

            Assert.IsNotEmpty(catalog,
                "no RTPose_*.anim found under " + RenderThumbnail.PosesFolder
                + " — the bundled pose vocabulary is sourced entirely from that glob");
        }

        [Test]
        public void PoseCatalog_EveryEntryResolvesAndIsHumanoid()
        {
            // Doubles as a content gate on the bundled set: any RTPose_* that is not a humanoid muscle
            // clip would not retarget across rigs, and ResolvePose rejects it pre-bake.
            foreach (var entry in RenderThumbnail.PoseCatalog())
            {
                Assert.IsTrue(RenderThumbnail.ResolvePose(entry.Token, out AnimationClip clip, out string err),
                    entry.Token + " (" + entry.Path + ") failed to resolve: " + err);
                Assert.IsNotNull(clip, entry.Token + " resolved null");
                Assert.IsTrue(clip.isHumanMotion, entry.Token + " must be a humanoid muscle clip");
            }
        }

        [Test]
        public void PoseToken_NormalizesCaseAndPunctuation()
        {
            // "Hand-On-Hip", "hand_on_hip" and "handonhip" are the same token; RTPose_HandOnHip.anim
            // normalizes to that same key, which is what lets the glob replace the old switch.
            Assert.AreEqual(RenderThumbnail.NormalizeToken("handonhip"), RenderThumbnail.NormalizeToken("Hand-On-Hip"));
            Assert.AreEqual(RenderThumbnail.NormalizeToken("handonhip"), RenderThumbnail.NormalizeToken("hand_on_hip"));
            Assert.AreEqual("handonhip", RenderThumbnail.NormalizeToken("RTPose_HandOnHip"));
        }

        [Test]
        public void Pose_CatalogTokenMatchesCaseInsensitive()
        {
            var first = RenderThumbnail.PoseCatalog().First();

            Assert.IsTrue(RenderThumbnail.ResolvePose(first.Token.ToUpperInvariant(), out AnimationClip clip, out string err), err);
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
        public void Pose_Unknown_ErrEnumeratesTheGlob()
        {
            // The advertised vocabulary is derived from disk, so it can never drift from what ships.
            var catalog = RenderThumbnail.PoseCatalog();

            bool ok = RenderThumbnail.ResolvePose("nope", out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            foreach (var entry in catalog)
            {
                StringAssert.Contains(entry.Label, err);
                // What it advertises must be what it accepts: the readable Label round-trips to Token.
                Assert.AreEqual(entry.Token, RenderThumbnail.NormalizeToken(entry.Label),
                    "advertised label '" + entry.Label + "' does not normalize back to its match token");
            }
            StringAssert.Contains("path/GUID", err);
        }

        // ===== Expression: path/GUID only, validated as the mirror image of pose =====

        [Test]
        public void Expression_Null_IsNone()
        {
            // No expression is a first-class outcome, not a degraded one: an avatar with no facial
            // clips renders body-pose-only.
            bool ok = RenderThumbnail.ResolveExpression(null, out AnimationClip clip, out string err);

            Assert.IsTrue(ok);
            Assert.IsNull(clip);
            Assert.IsNull(err);
        }

        [Test]
        public void Expression_BareName_ErrPointsAtDiscovery()
        {
            // Expressions are avatar-specific, so there is deliberately NO bundled vocabulary. The error
            // has to hand the caller the discovery route instead of enumerating something.
            bool ok = RenderThumbnail.ResolveExpression("smile", out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            StringAssert.Contains("path/GUID", err);
            StringAssert.Contains("ReportController", err);
        }

        [Test]
        public void Expression_MissingAsset_Fails()
        {
            bool ok = RenderThumbnail.ResolveExpression("Packages/com.ryan6vrc.avatar-tools/Editor/nope.anim",
                out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            StringAssert.Contains("no AnimationClip", err);
        }

        [Test]
        public void Expression_PoseClipRejected_NamingThePoseArg()
        {
            // The load-bearing swap guard: a humanoid muscle clip passed as `expression` would sample
            // over the pose instead of compositing with it. Fail loud, and say which arg it belongs to.
            var pose = RenderThumbnail.PoseCatalog().First();

            bool ok = RenderThumbnail.ResolveExpression(pose.Path, out AnimationClip clip, out string err);

            Assert.IsFalse(ok);
            Assert.IsNull(clip);
            StringAssert.Contains("pose", err);
        }

        [Test]
        public void IsExpressionClip_AcceptsBlendShapeCurves()
        {
            var clip = new AnimationClip();
            clip.SetCurve("Body", typeof(SkinnedMeshRenderer), "blendShape.eye_smile_1",
                AnimationCurve.Constant(0f, 0f, 100f));

            Assert.IsTrue(RenderThumbnail.IsExpressionClip(clip, out string err), err);
        }

        [Test]
        public void IsExpressionClip_RejectsTransformOnlyClip()
        {
            // A clip that moves bones but touches no blendshape is not a facial expression — most
            // likely a toggle or a gesture clip handed to the wrong arg.
            var clip = new AnimationClip();
            clip.SetCurve("Armature/Hips", typeof(Transform), "m_LocalPosition.x",
                AnimationCurve.Constant(0f, 0f, 1f));

            Assert.IsFalse(RenderThumbnail.IsExpressionClip(clip, out string err));
            StringAssert.Contains("blendShape", err);
        }
    }
}
