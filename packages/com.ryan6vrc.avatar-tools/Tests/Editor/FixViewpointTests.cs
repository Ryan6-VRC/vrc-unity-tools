using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// Pure-math tests for the VRC-free viewpoint core. The test assembly does not reference the VRC SDK, so
// FixViewpoint's descriptor/Animator shell (Recompute / Run) is exercised behaviorally via execute_code
// / the Test Runner, NOT here. ComputeViewpoint depends only on Vector3/Quaternion.
public class FixViewpointTests
{
    const float Tol = 1e-4f;

    // Identity: equal head rotation, s = 1, owned eye mid == ref eye mid → the whole offset is preserved
    // about the same origin, so newVP == vendorVP.
    [Test]
    public void Identity_equal_eye_mids_returns_vendorVP()
    {
        var vendorVP = new Vector3(0f, 1.5f, 0.1f);
        var eyeMid   = new Vector3(0f, 1.5f, 0f);
        var newVP = FixViewpoint.ComputeViewpoint(vendorVP, eyeMid, Quaternion.identity, eyeMid, Quaternion.identity, 1f);
        Assert.That(Vector3.Distance(newVP, vendorVP), Is.LessThan(Tol));
    }

    // Identity math with a shifted owned eye mid: newVP == Oowned + (vendorVP − Oref).
    [Test]
    public void Identity_shifted_owned_eye_mid_preserves_offset()
    {
        var vendorVP  = new Vector3(0f, 1.5f, 0.1f);
        var eyeMidRef = new Vector3(0f, 1.5f, 0f);
        var eyeMidOwn = new Vector3(0f, 1.6f, 0f);
        var newVP = FixViewpoint.ComputeViewpoint(vendorVP, eyeMidRef, Quaternion.identity, eyeMidOwn, Quaternion.identity, 1f);
        Assert.That(Vector3.Distance(newVP, new Vector3(0f, 1.6f, 0.1f)), Is.LessThan(Tol));
    }

    // Uniform scale s = 2 doubles the eye→VP nudge magnitude about the owned eye mid.
    [Test]
    public void Uniform_scale_doubles_nudge()
    {
        var newVP = FixViewpoint.ComputeViewpoint(
            new Vector3(0f, 0f, 0.1f), Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.identity, 2f);
        Assert.That(Vector3.Distance(newVP, new Vector3(0f, 0f, 0.2f)), Is.LessThan(Tol));
    }

    // A 90° head-FRAME delta about Y rotates the +Z offset vector to +X.
    [Test]
    public void Head_rotate_90_about_Y_rotates_offset()
    {
        var newVP = FixViewpoint.ComputeViewpoint(
            new Vector3(0f, 0f, 0.1f), Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.Euler(0, 90, 0), 1f);
        Assert.That(Vector3.Distance(newVP, new Vector3(0.1f, 0f, 0f)), Is.LessThan(Tol));
    }

    // Order-pinning: ref and owned head frames are BOTH non-identity AND distinct, so
    // `frameOwned · Inverse(frameRef)` is distinguishable from the reversed composition — a future
    // order-swap regression fails this (the identity-frameRef cases above cannot catch it).
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
    }

    // ── HeadFrame: the landmark frame, and the defect it exists to close ──────────────────────────

    // Realistic humanoid landmarks in descriptor-local metres: 62 mm interocular, eyes 90 mm above and
    // 60 mm forward of the head bone origin.
    static readonly Vector3 EyeL  = new Vector3(-0.031f, 1.500f, 0.060f);
    static readonly Vector3 EyeR  = new Vector3(+0.031f, 1.500f, 0.060f);
    static readonly Vector3 HeadO = new Vector3(0f, 1.410f, 0f);
    static Vector3 EyeMid => (EyeL + EyeR) * 0.5f;
    // The recorded vendor nudge that exposed the defect: 3.5 mm up, 38.4 mm forward of the eye midpoint.
    static Vector3 VendorVP => EyeMid + new Vector3(0f, 0.0035f, 0.0384f);

    // REGRESSION, and the reason HeadFrame exists. Rotating the nudge by the head BONE-basis delta turns a
    // pure coordinate convention into 54 mm of geometry: a Blender round-trip leaves every bone basis 90°
    // about X from the source's while joint positions match exactly, and the old algebra returned the forward
    // nudge as an UPWARD one — measured on a real owned base, and reported as PASS. This pins the arithmetic
    // of that failure, which the next two tests are checked against.
    [Test]
    public void Bone_basis_delta_would_have_moved_the_viewpoint_54mm()
    {
        var conventionOnly = Quaternion.Euler(-90f, 0f, 0f);   // Blender Z-up vs Unity Y-up
        var wrong = FixViewpoint.ComputeViewpoint(VendorVP, EyeMid, Quaternion.identity, EyeMid, conventionOnly, 1f);
        var nudgeOut = wrong - EyeMid;

        Assert.That(nudgeOut.y * 1000f, Is.EqualTo(38.4f).Within(0.2f), "38 mm forward comes back as 38 mm UP");
        Assert.That(nudgeOut.z * 1000f, Is.EqualTo(-3.5f).Within(0.2f), "3.5 mm up comes back as 3.5 mm BACK");
        Assert.That((wrong - VendorVP).magnitude * 1000f, Is.EqualTo(54f).Within(1f));
    }

    // …and the fix: identical landmark GEOMETRY yields identical frames, whatever the bone bases do. This is
    // the measured case (eye midpoints matching to 0.0002 mm, every bone 90° apart in world orientation), and
    // the correct answer is that the viewpoint does not move at all.
    [Test]
    public void Landmark_frames_coincide_across_a_pure_convention_difference()
    {
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameOwned, out _));
        Assert.That(Quaternion.Angle(frameRef, frameOwned), Is.LessThan(1e-3f));

        var newVP = FixViewpoint.ComputeViewpoint(VendorVP, EyeMid, frameRef, EyeMid, frameOwned, 1f);
        Assert.That(Vector3.Distance(newVP, VendorVP), Is.LessThan(Tol), "viewpoint must land unchanged");
    }

    // The capability the fix must NOT cost: a genuine rest-pose head rotation moves the eyes (they are the
    // head bone's children), so the landmark frame recovers it from geometry alone and the nudge follows.
    [Test]
    public void Landmark_frame_still_tracks_a_real_head_rotation()
    {
        var pitch = Quaternion.Euler(20f, 0f, 0f);
        Vector3 Rot(Vector3 p) => HeadO + pitch * (p - HeadO);

        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));
        Assert.IsTrue(FixViewpoint.HeadFrame(Rot(EyeL), Rot(EyeR), HeadO, out var frameOwned, out float riseOwned));
        Assert.That(Quaternion.Angle(frameRef, frameOwned), Is.EqualTo(20f).Within(0.01f));
        // A rigid rotation about the head origin leaves the landmark triangle's proportions untouched. This is
        // the one direction of the rise implication that holds; see the joint-slide test below for why the
        // converse does not, and why the VERIFY gate cannot be built on it.
        Assert.That(riseOwned, Is.EqualTo(riseRef).Within(1e-4f), "a real rotation does not change rise");

        var eyeMidOwn = (Rot(EyeL) + Rot(EyeR)) * 0.5f;
        var newVP = FixViewpoint.ComputeViewpoint(VendorVP, EyeMid, frameRef, eyeMidOwn, frameOwned, 1f);
        Assert.That(Vector3.Distance(newVP, eyeMidOwn + pitch * (VendorVP - EyeMid)), Is.LessThan(Tol));
    }

    // The residual limit, made visible. Three landmarks pin the frame of a TRIANGLE, so moving the head JOINT
    // within the sagittal plane produces a frame delta indistinguishable in kind from a real pitch — and a
    // rest-pose bake or a reproportion can do exactly that.
    [Test]
    public void Moved_head_joint_rotates_the_frame_exactly_like_a_head_rotation()
    {
        // Same eyes; the head joint slides 30 mm back along the sagittal axis — no rotation anywhere.
        var movedHead = HeadO - new Vector3(0f, 0f, 0.030f);
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, movedHead, out var frameMoved, out float riseMoved));

        Assert.That(Quaternion.Angle(frameRef, frameMoved), Is.GreaterThan(5f),
            "the frame moves, and reads exactly like a head rotation — this is the limit, not a bug");
        // This particular slide DOES change rise, which is what made rise look like a discriminator. The next
        // test is the one showing it cannot serve as a clearance.
        Assert.That(Mathf.Abs(riseMoved - riseRef), Is.GreaterThan(0.02f));
    }

    // THE COUNTEREXAMPLE that killed the old `riseDelta > 0.02 && frameRotDeg > 1` gate. rise is the landmark
    // triangle's PROPORTION, so it is preserved exactly by a joint slide along the arc of constant
    // perpendicular distance from the eye midpoint: the frame turns a full 20° with riseDelta == 0. A gate
    // carrying a riseDelta conjunct therefore stayed SILENT on a 13 mm viewpoint error, in one of the exact
    // cases the note exists for. This is why the gate now keys on millimetres moved.
    [Test]
    public void A_joint_slide_can_turn_the_frame_20_degrees_while_rise_is_unchanged()
    {
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, HeadO, out var frameRef, out float riseRef));

        // Rotate the (eyeMid → headOrigin) vector 20° about the eye axis, preserving its LENGTH. Nothing
        // rotates; only the joint's position changes.
        var toHead = HeadO - EyeMid;
        var slidHead = EyeMid + Quaternion.AngleAxis(20f, Vector3.right) * toHead;
        Assert.IsTrue(FixViewpoint.HeadFrame(EyeL, EyeR, slidHead, out var frameSlid, out float riseSlid));

        Assert.That((slidHead - HeadO).magnitude * 1000f, Is.GreaterThan(30f),
            "fixture must be a joint move of realistic magnitude — a rest-pose bake does this");
        Assert.That(Mathf.Abs(riseSlid - riseRef), Is.LessThan(1e-5f),
            "rise is INVARIANT here, so the old gate's riseDelta conjunct could never fire");
        Assert.That(Quaternion.Angle(frameRef, frameSlid), Is.EqualTo(20f).Within(0.01f),
            "yet the frame turns a full 20°, and the nudge turns with it");

        // …and the consequence the new gate measures: a real viewpoint error, invisible to rise.
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
        // rise must be scale-free too, or a resized owned rig would trip the shape-residual note on a
        // legitimate uniform resize — the case the interocular ratio already handles.
        Assert.That(riseScaled, Is.EqualTo(riseRef).Within(1e-4f));
    }

    // Degenerate landmarks refuse rather than returning a noise-amplified frame — the caller turns a false
    // return into a named FAIL, which CopyDescriptor folds into "left at the copied vendor value".
    [Test]
    public void Degenerate_landmarks_refuse()
    {
        Assert.IsFalse(FixViewpoint.HeadFrame(EyeL, EyeR, EyeMid, out _, out _), "head origin AT the eye midpoint");
        Assert.IsFalse(FixViewpoint.HeadFrame(EyeL, EyeR, EyeMid + new Vector3(0.5f, 0f, 0f), out _, out _),
            "head origin ON the eye-to-eye line");
        Assert.IsFalse(FixViewpoint.HeadFrame(EyeL, EyeL, HeadO, out _, out _), "coincident eyes");
    }

    // Head/body translate: shifting the owned eye mid shifts newVP by the same delta (offset preserved).
    [Test]
    public void Body_translate_shifts_result_by_same_delta()
    {
        var vendorVP  = new Vector3(0f, 1.5f, 0.1f);
        var eyeMidRef = new Vector3(0f, 1.5f, 0f);
        var delta     = new Vector3(0.2f, 0f, 0f);
        var newVP = FixViewpoint.ComputeViewpoint(vendorVP, eyeMidRef, Quaternion.identity, eyeMidRef + delta, Quaternion.identity, 1f);
        Assert.That(Vector3.Distance(newVP, vendorVP + delta), Is.LessThan(Tol));
    }
}
