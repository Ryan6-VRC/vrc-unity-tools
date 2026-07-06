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

    // A 90° head-basis delta about Y rotates the +Z offset vector to +X.
    [Test]
    public void Head_rotate_90_about_Y_rotates_offset()
    {
        var newVP = FixViewpoint.ComputeViewpoint(
            new Vector3(0f, 0f, 0.1f), Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.Euler(0, 90, 0), 1f);
        Assert.That(Vector3.Distance(newVP, new Vector3(0.1f, 0f, 0f)), Is.LessThan(Tol));
    }

    // Order-pinning: ref and owned head rotations are BOTH non-identity AND distinct, so
    // `headRotOwned · Inverse(headRotRef)` is distinguishable from the reversed composition — a future
    // order-swap regression fails this (the identity-headRotRef cases above cannot catch it).
    [Test]
    public void Head_rotate_composition_order_is_owned_times_inverse_ref()
    {
        var eyeMidRef = new Vector3(0f, 1.5f, 0f);
        var vendorVP  = new Vector3(0.05f, 1.6f, 0.2f);   // non-trivial nudge (0.05, 0.1, 0.2)
        var eyeMidOwn = new Vector3(0f, 1.5f, 0f);
        var headRotRef   = Quaternion.Euler(0f, 30f, 0f);
        var headRotOwned = Quaternion.Euler(90f, 0f, 0f);

        var result = FixViewpoint.ComputeViewpoint(vendorVP, eyeMidRef, headRotRef, eyeMidOwn, headRotOwned, 1f);

        var nudge    = vendorVP - eyeMidRef;
        var expected = eyeMidOwn + (headRotOwned * Quaternion.Inverse(headRotRef)) * nudge;
        var reversed = eyeMidOwn + (Quaternion.Inverse(headRotRef) * headRotOwned) * nudge;

        Assert.That(Vector3.Distance(result, expected), Is.LessThan(Tol), "Rₒ·R_v⁻¹ order");
        Assert.That(Vector3.Distance(expected, reversed), Is.GreaterThan(0.01f),
            "fixture must be order-discriminating (the two compositions differ)");
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
