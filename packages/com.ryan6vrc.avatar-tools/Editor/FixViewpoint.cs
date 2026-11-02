using System.Globalization;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>Recomputes an owned avatar's <c>VRCAvatarDescriptor.ViewPosition</c> from a known-good reference,
    /// re-seating the creator's eye→viewpoint nudge on the owned eyes and tracking head rotation and uniform resize.
    /// Contract: docs/unity-tools.md, plus a named FAIL when a rig's head origin lies on its eye-to-eye line.</summary>
    [AgentTool]
    public static class FixViewpoint
    {
        // Metres: a recompute within 0.1 mm skips the write, so a re-run on unchanged geometry dirties nothing.
        const float WriteEps = 1e-4f;
        const float MagEps = 1e-5f;
        // Head-frame degeneracy floor, as a fraction of interocular distance. It is a definedness floor, not an
        // accuracy one: frame sensitivity goes as δ/|perp|, so no value here guards against a wrong frame on a
        // well-defined rig. Recompute's rotationMm VERIFY note does that.
        const float FrameEps = 0.05f;
        // Millimetres of viewpoint movement from the applied frame rotation above which Recompute raises its
        // VERIFY note. It gates a note on a PASS, never the write.
        const float VerifyMm = 1f;

        // ── Pure math (VRC-free; the NUnit-tested core) ─────────────────────────────────────────────

        /// <summary>
        /// A head-orientation frame from three landmark positions (left eye, right eye, head origin). Returns
        /// false on degenerate landmarks: coincident eyes, or a head origin on the eye-to-eye line.
        ///
        /// <para>Never use <c>head.rotation</c> for this: a bone's local axes are a fact about whichever exporter
        /// wrote the FBX. A Blender round-trip re-expresses every bone basis by a constant 90° while leaving joint
        /// positions identical, so a rotation delta read off two rigs' head bones carries the convention as if it
        /// were geometry (measured: a 38 mm forward viewpoint nudge came back 38 mm UP). A rotated head still moves
        /// its eyes, its children, so positions recover a real rest-pose rotation in full.</para>
        ///
        /// <para>The residual limit: three landmarks pin the frame of a triangle, so moving the head joint within
        /// the sagittal plane (a rest-pose bake or a reproportion can) reads as a real pitch.
        /// <see cref="Recompute"/> raises its VERIFY note because it cannot rule this out.</para>
        ///
        /// <para><paramref name="rise"/> settles this one way only. A rigid rotation about the head origin preserves
        /// it, so a changed rise proves a joint moved. A joint sliding along the arc of constant perpendicular
        /// distance from the eye midpoint turns the frame with rise unchanged, so rise is never a clearance.</para>
        /// </summary>
        /// <param name="rise">Perpendicular head-origin→eye-midpoint distance in units of interocular distance,
        /// invariant to uniform scale and to lateral drift along the eye axis.</param>
        public static bool HeadFrame(Vector3 leftEye, Vector3 rightEye, Vector3 headOrigin,
                                    out Quaternion frame, out float rise)
        {
            frame = Quaternion.identity;
            rise = 0f;
            Vector3 across = rightEye - leftEye;
            float interocular = across.magnitude;
            if (interocular < MagEps) return false;                    // coincident eyes — no X axis
            Vector3 x = across / interocular;
            Vector3 up = (leftEye + rightEye) * 0.5f - headOrigin;
            Vector3 perp = up - Vector3.Dot(up, x) * x;
            if (perp.magnitude < FrameEps * interocular) return false; // head origin on the eye line — no Y axis
            rise = perp.magnitude / interocular;
            Vector3 y = perp.normalized;
            frame = Quaternion.LookRotation(Vector3.Cross(x, y), y);
            return true;
        }

        /// <summary>
        /// Re-seats the creator's eye→viewpoint nudge (<c>vendorVP − eyeMidRef</c>) on the owned eye midpoint, rotated
        /// by the reference→owned head-frame delta and scaled by the interocular ratio, all descriptor-local. The
        /// frames must come from <see cref="HeadFrame"/>, never from the head bones' own rotations.
        /// </summary>
        public static Vector3 ComputeViewpoint(Vector3 vendorVP, Vector3 eyeMidRef, Quaternion frameRef,
                                               Vector3 eyeMidOwned, Quaternion frameOwned, float interocularRatio)
        {
            Vector3 nudge = vendorVP - eyeMidRef;
            Quaternion rotDelta = frameOwned * Quaternion.Inverse(frameRef);
            return eyeMidOwned + interocularRatio * (rotDelta * nudge);
        }

        // ── Recompute core (VRC-typed; writes no RunLog) ────────────────────────────────────────────

        /// <summary>Outcome of <see cref="Recompute"/>.</summary>
        internal struct ViewpointResult
        {
            public bool ok;
            public Vector3 oldVP;
            public Vector3 newVP;
            public float interocularRatio;
            public bool wrote;
            public float frameRotDeg;        // landmark head-frame delta actually applied to the nudge
            public float headBasisDeltaDeg;  // head BONE-basis delta — reported only, never applied
            public float riseDelta;          // landmark-shape change; see HeadFrame's rise note
            public float rotationMm;         // mm the applied frame rotation moves the viewpoint — the VERIFY gate
            public string failReason;   // set (ok == false) on any named FAIL condition
            public string note;         // human-readable state line for the host log (success/unchanged)
            // Which basis each side's eyes came from: a viewpoint from caller-supplied transforms is not
            // checkable later from the rig alone.
            public string eyeSrc;
        }

        /// <summary>
        /// Recomputes the viewpoint from both rigs' descriptor-local landmarks and, unless
        /// <paramref name="whatIf"/>, delta-gates the write under <see cref="Undo.RecordObject"/>. Reads
        /// <c>vendorVP</c> from the reference descriptor because CopyDescriptor's whatIf runs before its copy.
        /// Writes no RunLog: the caller folds this into its own envelope.
        /// </summary>
        /// <param name="referenceVpIsBaseline">True when the caller lands the reference descriptor's VP onto the
        /// owned descriptor around this call (CopyDescriptor's <c>CopySerialized</c>): the baseline (<c>oldVP</c> and
        /// the delta gate) is then the reference VP in both modes, so preview's <c>oldVP → newVP</c> equals execute's
        /// even on an overwrite re-run over a stale owned VP, and a whatIf may run before the owned descriptor exists,
        /// framed on <paramref name="ownedRoot"/>. False (standalone door): the baseline is the owned descriptor's
        /// current VP, and that descriptor is required in both modes.</param>
        internal static ViewpointResult Recompute(GameObject ownedRoot, GameObject referenceRoot, bool whatIf,
                                                  bool referenceVpIsBaseline = false,
                                                  Transform ownedLeftEye = null, Transform ownedRightEye = null,
                                                  Transform referenceLeftEye = null, Transform referenceRightEye = null)
        {
            var r = new ViewpointResult();

            var refDesc = referenceRoot.GetComponent<VRCAvatarDescriptor>()
                       ?? referenceRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (refDesc == null) { r.failReason = "reference has no VRCAvatarDescriptor"; return r; }

            var ownedDesc = ownedRoot.GetComponent<VRCAvatarDescriptor>()
                         ?? ownedRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (ownedDesc == null && (!whatIf || !referenceVpIsBaseline))
            {
                r.failReason = "owned has no VRCAvatarDescriptor";
                return r;
            }

            // ViewPosition is relative to the descriptor's own transform, and the descriptor may sit on a child.
            Transform refFrame   = refDesc.transform;
            Transform ownedFrame = ownedDesc != null ? ownedDesc.transform : ownedRoot.transform;

            if (!ResolveEyesHead(referenceRoot, "reference", out Vector3 refLW, out Vector3 refRW, out Vector3 refHeadPW, out Quaternion refHeadW, out r.failReason, out string refEyeSrc, referenceLeftEye, referenceRightEye)) return r;
            if (!ResolveEyesHead(ownedRoot,     "owned",     out Vector3 owLW,  out Vector3 owRW,  out Vector3 owHeadPW,  out Quaternion owHeadW,  out r.failReason, out string owEyeSrc, ownedLeftEye, ownedRightEye)) return r;
            r.eyeSrc = "eyeSrc=owned:" + owEyeSrc + "/reference:" + refEyeSrc;

            Vector3 refL = refFrame.InverseTransformPoint(refLW);
            Vector3 refR = refFrame.InverseTransformPoint(refRW);
            Vector3 refH = refFrame.InverseTransformPoint(refHeadPW);
            Vector3 owL  = ownedFrame.InverseTransformPoint(owLW);
            Vector3 owR  = ownedFrame.InverseTransformPoint(owRW);
            Vector3 owH  = ownedFrame.InverseTransformPoint(owHeadPW);

            float refInteroc   = (refR - refL).magnitude;
            float ownedInteroc = (owR - owL).magnitude;
            if (refInteroc   < MagEps) { r.failReason = "reference eyes coincident — cannot derive interocular scale"; return r; }
            if (ownedInteroc < MagEps) { r.failReason = "owned eyes coincident — cannot derive interocular scale"; return r; }

            // A degenerate frame is a named FAIL, not a no-rotation fallback: CopyDescriptor then leaves
            // ViewPosition at the copied vendor value.
            if (!HeadFrame(refL, refR, refH, out Quaternion frameRef, out float riseRef))
            { r.failReason = "reference head origin lies on the eye-to-eye line — cannot derive a head frame from landmarks"; return r; }
            if (!HeadFrame(owL, owR, owH, out Quaternion frameOwned, out float riseOwned))
            { r.failReason = "owned head origin lies on the eye-to-eye line — cannot derive a head frame from landmarks"; return r; }
            r.riseDelta = Mathf.Abs(riseRef - riseOwned);

            r.interocularRatio = ownedInteroc / refInteroc;
            Vector3 eyeMidRef   = (refL + refR) * 0.5f;
            Vector3 eyeMidOwned = (owL + owR) * 0.5f;
            Vector3 vendorVP    = refDesc.ViewPosition;

            r.frameRotDeg = Quaternion.Angle(frameRef, frameOwned);
            // The head bone-basis delta, reported and never applied: on a Blender round-tripped rig it reads ~90° of
            // pure axis convention, and generic transform-rotation clips authored against one basis do not retarget
            // onto the other.
            r.headBasisDeltaDeg = Quaternion.Angle(Quaternion.Inverse(refFrame.rotation) * refHeadW,
                                                   Quaternion.Inverse(ownedFrame.rotation) * owHeadW);

            r.newVP = ComputeViewpoint(vendorVP, eyeMidRef, frameRef, eyeMidOwned, frameOwned, r.interocularRatio);
            r.oldVP = referenceVpIsBaseline ? vendorVP
                    : (ownedDesc != null ? ownedDesc.ViewPosition : vendorVP);

            float deltaMm   = (r.newVP - r.oldVP).magnitude * 1000f;
            bool  wouldWrite = (r.newVP - r.oldVP).magnitude > WriteEps;
            if (!whatIf && ownedDesc != null && wouldWrite)
            {
                Undo.RecordObject(ownedDesc, "FixViewpoint: recompute ViewPosition");
                ownedDesc.ViewPosition = r.newVP;
                EditorUtility.SetDirty(ownedDesc);
                r.wrote = true;
            }

            // The VERIFY gate keys on rotationMm, never riseDelta: the joint slide in HeadFrame's docs turns the frame
            // with riseDelta at 0.
            Vector3 nudge = vendorVP - eyeMidRef;
            r.rotationMm = r.interocularRatio
                         * (((frameOwned * Quaternion.Inverse(frameRef)) * nudge) - nudge).magnitude * 1000f;
            string basis = string.Format(CultureInfo.InvariantCulture,
                " headFrameDeg={0:F2} headBoneBasisDeg={1:F2} (bone basis reported, not applied) riseDelta={2:F4} rotationMm={3:F2}",
                r.frameRotDeg, r.headBasisDeltaDeg, r.riseDelta, r.rotationMm);
            if (r.rotationMm > VerifyMm)
                basis += string.Format(CultureInfo.InvariantCulture,
                    " — VERIFY: the head frame rotated the nudge by {0:F2} mm. Three landmarks pin the frame of a"
                    + " TRIANGLE, so a relocated head JOINT reads exactly like a real head rotation and riseDelta"
                    + " does not separate them (a joint slide that preserves the triangle's proportions leaves"
                    + " riseDelta at 0). Confirm the viewpoint in play mode, or keep the reference value",
                    r.rotationMm);
            r.note = (wouldWrite
                ? string.Format(CultureInfo.InvariantCulture,
                    "viewpoint recomputed: {0} → {1} (deltaMm={2:F2}, s={3:F4})",
                    Fmt(r.oldVP), Fmt(r.newVP), deltaMm, r.interocularRatio)
                : "viewpoint unchanged (< ε)") + basis + " " + r.eyeSrc;
            r.ok = true;
            return r;
        }

        // ── Door ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Standalone door over <see cref="Recompute"/>. Returns the one-line PASS/FAIL summary ending with
        /// the RunLog path.</summary>
        public static string Run(GameObject ownedRoot, GameObject referenceRoot, bool whatIf = false,
                                 Transform ownedLeftEye = null, Transform ownedRightEye = null,
                                 Transform referenceLeftEye = null, Transform referenceRightEye = null)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";
            var log = new RunLog("fix-viewpoint")
            {
                whatIf   = whatIf,
                instance = ownedRoot != null ? ownedRoot.name : null,
                source   = referenceRoot != null ? referenceRoot.name : null,
            };

            if (ownedRoot == null || referenceRoot == null)
            {
                log.result = "FAIL";
                log.error  = (ownedRoot == null ? "ownedRoot" : "referenceRoot") + " is null";
                return TransplantCore.Finish(log, label);
            }

            var r = Recompute(ownedRoot, referenceRoot, whatIf, referenceVpIsBaseline: false,
                              ownedLeftEye, ownedRightEye, referenceLeftEye, referenceRightEye);
            if (!r.ok)
            {
                log.result = "FAIL";
                log.error  = r.failReason;
                log.Offender(r.failReason);
                return TransplantCore.Finish(log, label);
            }

            log.Count("wrote", r.wrote ? 1 : 0);
            log.Note(r.note);
            log.result = "PASS";
            return TransplantCore.Finish(log, label);
        }

        // ── Eye/head lookup ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves eye and Head world positions plus the Head rotation (reported, not applied) on
        /// <paramref name="root"/>'s humanoid Animator. <c>GetBoneTransform</c> THROWS off-humanoid, so the humanoid
        /// guard runs first. A missing eye is a named FAIL, never a name-based guess: the agent identifies eye objects
        /// a name rule cannot and passes them as <paramref name="leftEyeOverride"/>/<paramref name="rightEyeOverride"/>.
        /// </summary>
        static bool ResolveEyesHead(GameObject root, string which,
                                    out Vector3 leftEyeW, out Vector3 rightEyeW, out Vector3 headPosW,
                                    out Quaternion headW, out string failReason, out string eyeSrc,
                                    Transform leftEyeOverride = null, Transform rightEyeOverride = null)
        {
            leftEyeW = rightEyeW = headPosW = Vector3.zero;
            headW = Quaternion.identity;
            failReason = null;
            eyeSrc = "rig";

            var animator = root.GetComponent<Animator>() ?? root.GetComponentInChildren<Animator>(true);
            bool isHumanoid = animator != null && animator.avatar != null && animator.avatar.isHuman;
            if (!isHumanoid)
            {
                failReason = which + " rig is not humanoid — FixViewpoint needs mapped eyes + head";
                return false;
            }

            Transform head  = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null)  { failReason = which + " Head unmapped"; return false; }

            Transform left  = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform right = animator.GetBoneTransform(HumanBodyBones.RightEye);
            if ((leftEyeOverride == null) != (rightEyeOverride == null))
            {
                failReason = which + " eye override needs BOTH eyes (the interocular axis is built from the pair)";
                return false;
            }
            if (leftEyeOverride != null)
            {
                left = leftEyeOverride;
                right = rightEyeOverride;
                eyeSrc = "explicit";
            }
            if (left == null)  { failReason = which + " LeftEye unmapped — map it, or pass explicit eye transforms"; return false; }
            if (right == null) { failReason = which + " RightEye unmapped — map it, or pass explicit eye transforms"; return false; }

            leftEyeW  = left.position;
            rightEyeW = right.position;
            headPosW  = head.position;
            headW     = head.rotation;
            return true;
        }

        static string Fmt(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4})", v.x, v.y, v.z);
    }
}
