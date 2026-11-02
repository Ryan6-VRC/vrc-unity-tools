using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// Pure-math tests for the VRC-free viewpoint core. FixViewpoint's descriptor/Animator shell (Recompute, Run) is
// not covered here; it is exercised via execute_code (docs/verify.md §Test venue).
public class FixViewpointTests
{
    const float Tol = 1e-4f;

    [TestCase(0f, 0f, 0f)]
    [TestCase(0f, 0.1f, 0f)]
    [TestCase(0.2f, 0f, 0f)]
    public void Identity_shifted_owned_eye_mid_preserves_offset(float dx, float dy, float dz)
    {
        var vendorVP  = new Vector3(0f, 1.5f, 0.1f);
        var eyeMidRef = new Vector3(0f, 1.5f, 0f);
        var delta     = new Vector3(dx, dy, dz);
        var newVP = FixViewpoint.ComputeViewpoint(vendorVP, eyeMidRef, Quaternion.identity,
                                                 eyeMidRef + delta, Quaternion.identity, 1f);
        Assert.That(Vector3.Distance(newVP, vendorVP + delta), Is.LessThan(Tol));
    }

    // The eye mids sit at a realistic 1.5 m, not the origin: with zeroed eye mids the wrong form
    // s·(eyeMidOwned + R·nudge) is arithmetically identical to the right one, and this is the only s != 1 test in
    // the file.
    [Test]
    public void Uniform_scale_doubles_nudge()
    {
        var eyeMid = new Vector3(0f, 1.5f, 0f);
        var newVP = FixViewpoint.ComputeViewpoint(
            eyeMid + new Vector3(0f, 0f, 0.1f), eyeMid, Quaternion.identity, eyeMid, Quaternion.identity, 2f);
        Assert.That(Vector3.Distance(newVP, eyeMid + new Vector3(0f, 0f, 0.2f)), Is.LessThan(Tol));
    }

    [Test]
    public void Head_rotate_composition_order_is_owned_times_inverse_ref()
    {
        var eyeMidRef = new Vector3(0f, 1.5f, 0f);
        var vendorVP  = new Vector3(0.05f, 1.6f, 0.2f);   // non-trivial nudge (0.05, 0.1, 0.2)
        var eyeMidOwn = new Vector3(0f, 1.5f, 0f);
        var frameRef   = Quaternion.Euler(0f, 30f, 0f);
        var frameOwned = Quaternion.Euler(90f, 0f, 0f);

        var result = FixViewpoint.ComputeViewpoint(vendorVP, eyeMidRef, frameRef, eyeMidOwn, frameOwned, 1f);

        var nudge    = vendorVP - eyeMidRef;
        var expected = eyeMidOwn + (frameOwned * Quaternion.Inverse(frameRef)) * nudge;
        var reversed = eyeMidOwn + (Quaternion.Inverse(frameRef) * frameOwned) * nudge;

        Assert.That(Vector3.Distance(result, expected), Is.LessThan(Tol), "Rₒ·R_v⁻¹ order");
        Assert.That(Vector3.Distance(expected, reversed), Is.GreaterThan(0.01f),
            "fixture must be order-discriminating (the two compositions differ)");

        // A readable special case: a 90° owned frame about Y takes the +Z nudge to +X, so a lost or transposed
        // rotation shows as an axis.
        var rotated = FixViewpoint.ComputeViewpoint(
            new Vector3(0f, 0f, 0.1f), Vector3.zero, Quaternion.identity, Vector3.zero,
            Quaternion.Euler(0f, 90f, 0f), 1f);
        Assert.That(Vector3.Distance(rotated, new Vector3(0.1f, 0f, 0f)), Is.LessThan(Tol));
    }

    // ── HeadFrame: the landmark frame, its capability, and its residual limit ─────────────────────
    // The bone-basis defect HeadFrame exists to close is not testable here: no argument to ComputeViewpoint or
    // HeadFrame is a bone basis. FixViewpoint.HeadFrame's docstring owns it.

    // Humanoid landmarks in descriptor-local metres.
    static readonly Vector3 EyeL  = new Vector3(-0.031f, 1.500f, 0.060f);
    static readonly Vector3 EyeR  = new Vector3(+0.031f, 1.500f, 0.060f);
    static readonly Vector3 HeadO = new Vector3(0f, 1.410f, 0f);
    static Vector3 EyeMid => (EyeL + EyeR) * 0.5f;
    static Vector3 VendorVP => EyeMid + new Vector3(0f, 0.0035f, 0.0384f);

    // A genuine rest-pose head rotation moves the eyes, the head bone's children, so the landmark frame recovers it
    // from geometry alone and the nudge follows.
    [Test]
    public void Landmark_frame_still_tracks_a_real_head_rotation()
    {
        var pitch = Quaternion.Euler(20f, 0f, 0f);
        Vector3 Rot(Vector3 p) => HeadO + pitch * (p - HeadO);

        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));
        Assert.IsTrue(FixViewpoint.HeadFrame(Rot(EyeL), Rot(EyeR), HeadO, out var frameOwned, out float riseOwned));
        Assert.That(Quaternion.Angle(frameRef, frameOwned), Is.EqualTo(20f).Within(0.01f));
        // A rigid rotation about the head origin leaves the landmark triangle's proportions untouched. Only this
        // direction of the rise implication holds; the joint-slide test below shows the converse does not.
        Assert.That(riseOwned, Is.EqualTo(riseRef).Within(1e-4f), "a real rotation does not change rise");

        var eyeMidOwn = (Rot(EyeL) + Rot(EyeR)) * 0.5f;
        var newVP = FixViewpoint.ComputeViewpoint(VendorVP, EyeMid, frameRef, eyeMidOwn, frameOwned, 1f);
        Assert.That(Vector3.Distance(newVP, eyeMidOwn + pitch * (VendorVP - EyeMid)), Is.LessThan(Tol));
    }

    // Three landmarks pin the frame of a TRIANGLE, so moving the head JOINT within the sagittal plane produces a
    // frame delta indistinguishable in kind from a real pitch, and a rest-pose bake or a reproportion can do that.
    [Test]
    public void Moved_head_joint_rotates_the_frame_exactly_like_a_head_rotation()
    {
        var movedHead = HeadO - new Vector3(0f, 0f, 0.030f);
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, movedHead, out var frameMoved, out float riseMoved));

        Assert.That(Quaternion.Angle(frameRef, frameMoved), Is.GreaterThan(5f),
            "the frame moves, and reads exactly like a head rotation — this is the limit, not a bug");
        // This slide changes rise, but rise is no clearance: the next test turns the frame with rise unchanged.
        Assert.That(Mathf.Abs(riseMoved - riseRef), Is.GreaterThan(0.02f));
    }

    // rise is the landmark triangle's PROPORTION, so a joint slide along the arc of constant perpendicular distance
    // from the eye midpoint preserves it exactly while the frame turns a full 20°. A gate on riseDelta would stay
    // silent on this 13 mm viewpoint error, which is why Recompute's VERIFY note keys on rotationMm.
    [Test]
    public void A_joint_slide_can_turn_the_frame_20_degrees_while_rise_is_unchanged()
    {
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));

        // Nothing rotates; only the joint's position changes.
        var toHead = HeadO - EyeMid;
        var slidHead = EyeMid + Quaternion.AngleAxis(20f, Vector3.right) * toHead;
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, slidHead, out var frameSlid, out float riseSlid));

        Assert.That((slidHead - HeadO).magnitude * 1000f, Is.GreaterThan(30f),
            "fixture must be a joint move of realistic magnitude — a rest-pose bake does this");
        Assert.That(Mathf.Abs(riseSlid - riseRef), Is.LessThan(1e-5f),
            "rise is INVARIANT here, so the old gate's riseDelta conjunct could never fire");
        Assert.That(Quaternion.Angle(frameRef, frameSlid), Is.EqualTo(20f).Within(0.01f),
            "yet the frame turns a full 20°, and the nudge turns with it");

        // The consequence rotationMm measures: a real viewpoint error, invisible to rise.
        var newVP = FixViewpoint.ComputeViewpoint(VendorVP, EyeMid, frameRef, EyeMid, frameSlid, 1f);
        Assert.That((newVP - VendorVP).magnitude * 1000f, Is.GreaterThan(10f),
            "the frame rotation displaces the viewpoint materially while riseDelta reads 0");
    }

    // The frame is scale-free, so a uniform resize rides the interocular ratio alone and can never leak in
    // as a spurious rotation.
    [Test]
    public void Landmark_frame_is_invariant_under_uniform_scale()
    {
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL * 2f, EyeR * 2f, HeadO * 2f, out var frameScaled, out float riseScaled));
        Assert.That(Quaternion.Angle(frameRef, frameScaled), Is.LessThan(1e-3f));
        // rise must be scale-free too, or Recompute's riseDelta would report a shape change on a legitimate uniform
        // resize, the case the interocular ratio already handles.
        Assert.That(riseScaled, Is.EqualTo(riseRef).Within(1e-4f));
    }

    [Test]
    public void Degenerate_landmarks_refuse()
    {
        Assert.IsFalse(FixViewpoint.HeadFrame(EyeL, EyeR, EyeMid, out _, out _), "head origin AT the eye midpoint");
        Assert.IsFalse(FixViewpoint.HeadFrame(EyeL, EyeR, EyeMid + new Vector3(0.5f, 0f, 0f), out _, out _),
            "head origin ON the eye-to-eye line");
        Assert.IsFalse(FixViewpoint.HeadFrame(EyeL, EyeL, HeadO, out _, out _), "coincident eyes");
    }
}
