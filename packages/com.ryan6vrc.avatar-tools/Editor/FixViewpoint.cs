using System.Globalization;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Recomputes an owned avatar's <c>VRCAvatarDescriptor.ViewPosition</c> (a descriptor-local meters
    /// vector) so a known-good viewpoint tracks new geometry — after a reproportion/bake or a descriptor
    /// transplant onto a rig whose eyes/head moved. The viewpoint is NOT a snap-to-eyes: a good VP sits a
    /// deliberate nudge FORWARD of the eye bones (which live inside the skull). This tool preserves that
    /// creator nudge while re-seating it on the owned eye midpoint, tracking head translate / rotate /
    /// uniform resize — deriving the offset from a REAL reference baseline every time (the schema can't lie:
    /// no fabricated <c>s = 1</c>, no reference-less guess).
    ///
    /// Head orientation is tracked through a frame derived from LANDMARK POSITIONS (eyes + head origin), never
    /// from the head bone's own rotation — see <see cref="HeadFrame"/> for why that distinction decides whether
    /// the answer is right at all.
    ///
    /// Three levels (tooldesign — pure core / thin door): <see cref="HeadFrame"/> + <see cref="ComputeViewpoint"/>
    /// are the VRC-free, NUnit-tested math; <see cref="Recompute"/> is the VRC-typed core (descriptor resolve +
    /// isHuman-guarded eyes/head + descriptor-local conversion + eps guards + delta-gated write), writing NO
    /// RunLog so a host tool (CopyDescriptor) folds it into its own log; <see cref="Run"/> is the standalone door.
    ///
    /// PASS = viewpoint recomputed (or unchanged within ε). Standalone FAILs named on: no reference/owned
    /// descriptor, non-humanoid rig, unmapped eyes, coincident eyes, or a head origin on the eye-to-eye line
    /// (degenerate landmarks). RunLog kind <c>fix-viewpoint</c>.
    /// </summary>
    [AgentTool]
    public static class FixViewpoint
    {
        // Delta-gate: skip the write (and the dirty) when the recompute lands within 0.1 mm of the current
        // value — a re-run on unchanged geometry recomputes the same VP, so it stays idempotent-clean.
        const float WriteEps = 1e-4f;
        // Interocular magnitude floor: below this the eyes are coincident and the scale ratio is undefined.
        const float MagEps = 1e-5f;
        // Head-frame degeneracy floor, as a FRACTION of interocular distance (scale-free, so it holds on a
        // centimetre rig and a metre rig alike): below this the head origin is on the eye-to-eye line and the up
        // axis is undefined. It is a definedness floor, NOT an accuracy one — frame sensitivity goes as
        // δ/|perp|, so a rig sitting just above the floor is still noise-prone, and no achievable value fixes
        // that (a real rig measures ~1.5–1.8, some 30× above it). The guard against a WRONG frame on a
        // well-defined rig is Recompute's rotationMm VERIFY note, not this constant.
        const float FrameEps = 0.05f;
        // VERIFY floor: millimetres of viewpoint movement attributable to the applied frame rotation, above
        // which Recompute asks the caller to confirm. 1 mm sits between the two populations rather than
        // splitting either — a convention-identical owned/vendor pair reads 0.0000° of frame rotation, while
        // a joint-relocation artifact reads ~13 mm. It gates a NOTE on a PASS, never the write: the
        // arithmetic is not in doubt, only whether the caller wants a rotation nothing can attribute.
        const float VerifyMm = 1f;

        // ── Pure math (VRC-free; the NUnit-tested core) ─────────────────────────────────────────────

        /// <summary>
        /// A head-orientation frame built from three LANDMARK POSITIONS — left eye, right eye, head origin —
        /// rather than from the head bone's own rotation. Returns false (frame undefined) on degenerate
        /// landmarks: coincident eyes, or a head origin on the eye-to-eye line.
        ///
        /// <para>POSITIONS, NOT <c>head.rotation</c>: a bone's local axes are a fact about whichever exporter
        /// wrote the FBX, not about the avatar. A Blender round-trip re-expresses every bone basis by a constant
        /// 90° while leaving joint positions identical — so a rotation delta read off two rigs' head bones is
        /// that convention PLUS any real difference, with no way to separate them, and the tool then applies the
        /// convention as if it were geometry (measured: a 38 mm forward viewpoint nudge came back 38 mm UP).
        /// Landmark positions carry no convention. A genuinely rotated head still moves its eyes — they are its
        /// children — so a real rest-pose rotation is recovered in full.</para>
        ///
        /// <para>Basis: X across the eyes, Y the component of (eyeMid − headOrigin) perpendicular to it, Z their
        /// cross product. Three non-collinear points determine a rotation, so this is not one heuristic among
        /// several — it is the frame those landmarks have.</para>
        ///
        /// <para>THE RESIDUAL LIMIT: three landmarks pin the frame of a TRIANGLE, so the frame delta is zero
        /// when the two landmark triangles are SIMILAR — not, as is tempting to assume, exactly when the two
        /// heads are oriented alike. Moving the head JOINT within the sagittal plane (a rest-pose bake or a
        /// reproportion can) is indistinguishable from a real pitch and produces a frame delta of the same
        /// kind. This is a far smaller error than the bone-basis bug it replaces — and unlike that one it is
        /// reported: <see cref="Recompute"/> raises a VERIFY note whenever the applied rotation moves the
        /// viewpoint at all materially, because a joint relocation is exactly what it cannot rule out.</para>
        ///
        /// <para>WHAT <paramref name="rise"/> SETTLES, AND WHAT IT CANNOT. The implication runs ONE WAY: a
        /// rigid rotation about the head origin preserves it exactly, so a changed rise proves a joint moved.
        /// The converse does not hold — slide the joint along the arc of constant perpendicular distance from
        /// the eye midpoint and the triangle's proportions survive while the frame turns freely. On realistic
        /// landmarks (62 mm interocular, eyes 90 mm up / 60 mm forward of the joint) a 37.6 mm slide turns the
        /// frame 20° with rise identical to six decimals, displacing a 38.6 mm nudge by 13.4 mm. So rise is a
        /// discriminator for the pure cases and never a clearance, which is why the VERIFY gate keys on
        /// millimetres moved instead.</para>
        /// </summary>
        /// <param name="rise">Perpendicular head-origin→eye-midpoint distance in units of interocular distance —
        /// the landmark triangle's proportion, invariant to uniform scale, to lateral drift along the eye axis,
        /// and (see above) to a joint slide along its own constant-distance arc.</param>
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
        /// The similarity-frame viewpoint recompute, in DESCRIPTOR-LOCAL space:
        /// <c>newVP = eyeMidOwned + s · (Rₒ · R_v⁻¹) · (vendorVP − eyeMidRef)</c>, where
        /// <c>Rₒ · R_v⁻¹ = frameOwned · Quaternion.Inverse(frameRef)</c> is the head-orientation delta from
        /// reference to owned, and <c>s = interocularRatio</c> scales the eye→VP nudge. Preserves the creator's
        /// eye→viewpoint nudge (<c>vendorVP − eyeMidRef</c>) while re-seating it on the owned eye midpoint and
        /// tracking head rotate + uniform head resize. Depends only on Vector3/Quaternion.
        ///
        /// <para>The frames MUST come from <see cref="HeadFrame"/>, never from the head bones' own rotations —
        /// that distinction is the whole correctness argument, and it lives in HeadFrame's docs.</para>
        /// </summary>
        public static Vector3 ComputeViewpoint(Vector3 vendorVP, Vector3 eyeMidRef, Quaternion frameRef,
                                               Vector3 eyeMidOwned, Quaternion frameOwned, float interocularRatio)
        {
            Vector3 nudge = vendorVP - eyeMidRef;                          // creator's eye→VP offset
            Quaternion rotDelta = frameOwned * Quaternion.Inverse(frameRef); // Rₒ · R_v⁻¹
            return eyeMidOwned + interocularRatio * (rotDelta * nudge);
        }

        // ── Recompute core (VRC-typed; writes no RunLog) ────────────────────────────────────────────

        /// <summary>Outcome of <see cref="Recompute"/>. VP vectors + ratio are floats (they ride the host
        /// tool's summary/note, not the long-only counts channel).</summary>
        internal struct ViewpointResult
        {
            public bool ok;
            public Vector3 oldVP;
            public Vector3 newVP;
            public float interocularRatio;
            public bool wrote;
            public float frameRotDeg;        // landmark head-frame delta actually applied to the nudge
            public float headBasisDeltaDeg;  // head BONE-basis delta — reported only, never applied
            public float riseDelta;          // landmark-SHAPE change; see HeadFrame's residual-limit note
            public float rotationMm;         // mm the applied frame rotation moves the viewpoint — the VERIFY gate
            public string failReason;   // set (ok == false) on any named FAIL condition
            public string note;         // human-readable state line for the host log (success/unchanged)
            // Which basis each side's eyes came from, per side. Reported so a viewpoint computed from
            // caller-supplied transforms is never read as one the rig itself declared — the two are equally
            // valid inputs and only one of them is checkable later from the rig alone.
            public string eyeSrc;
        }

        /// <summary>
        /// Resolve descriptors + isHuman-guarded eyes/head on both rigs, convert to descriptor-local,
        /// eps-guard the interocular magnitudes, call <see cref="ComputeViewpoint"/>, and — when not
        /// <paramref name="whatIf"/> and an owned descriptor exists — DELTA-GATE the write (write
        /// <c>ownedDesc.ViewPosition</c> under <see cref="Undo.RecordObject"/> only when the change exceeds ε).
        /// Reads <c>vendorVP</c> from the REFERENCE descriptor uniformly (in CopyDescriptor's execute path this
        /// equals the just-copied value; reading the reference keeps whatIf — which runs before the copy —
        /// correct). Writes no RunLog: the caller folds this into its own envelope.
        ///
        /// Owned-descriptor gating: the owned descriptor is REQUIRED to write (execute), but NOT to preview.
        /// In <paramref name="whatIf"/> with no owned descriptor (CopyDescriptor's preview, before the
        /// descriptor is added), the owned <b>frame</b> falls back to <paramref name="ownedRoot"/>'s transform
        /// (where the descriptor will land) and <c>oldVP</c> reports the vendorVP the copy will land — so
        /// preview's <c>oldVP → newVP</c> equals execute's.
        /// </summary>
        /// <param name="referenceVpIsBaseline">True when the CALLER lands the reference descriptor's VP onto
        /// the owned descriptor around this recompute (CopyDescriptor's <c>CopySerialized</c>) — then the
        /// pre-recompute baseline (<c>oldVP</c>, and the delta-gate reference) is the REFERENCE vendorVP in
        /// BOTH whatIf and execute, so preview's <c>oldVP → newVP</c> equals execute's even on an overwrite
        /// re-run where an owned descriptor already holds a stale VP. False (standalone door): the baseline is
        /// the owned descriptor's current VP.</param>
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
            // Owned descriptor is required to WRITE, and the standalone door (referenceVpIsBaseline == false)
            // needs it in BOTH modes so preview == execute — a missing-descriptor door FAIL fires identically
            // at whatIf and execute. Only CopyDescriptor's baseline preview (referenceVpIsBaseline == true,
            // whatIf) legitimately runs before the descriptor is added, so it alone skips this guard.
            if (ownedDesc == null && (!whatIf || !referenceVpIsBaseline))
            {
                r.failReason = "owned has no VRCAvatarDescriptor";
                return r;
            }

            // Frame origin = the descriptor's OWN transform (ViewPosition is expressed relative to it, and the
            // descriptor may sit on a child). Owned preview with no descriptor yet → the root it will land on.
            Transform refFrame   = refDesc.transform;
            Transform ownedFrame = ownedDesc != null ? ownedDesc.transform : ownedRoot.transform;

            // Per side, because the mixed case is the likely one — a vendor reference with unmapped eyes
            // beside an owned rig that maps them, or the reverse after a re-export.
            if (!ResolveEyesHead(referenceRoot, "reference", out Vector3 refLW, out Vector3 refRW, out Vector3 refHeadPW, out Quaternion refHeadW, out r.failReason, out string refEyeSrc, referenceLeftEye, referenceRightEye)) return r;
            if (!ResolveEyesHead(ownedRoot,     "owned",     out Vector3 owLW,  out Vector3 owRW,  out Vector3 owHeadPW,  out Quaternion owHeadW,  out r.failReason, out string owEyeSrc, ownedLeftEye, ownedRightEye)) return r;
            r.eyeSrc = "eyeSrc=owned:" + owEyeSrc + "/reference:" + refEyeSrc;

            // World → descriptor-local. Every landmark is a POSITION about the frame; the head bone rotations
            // are converted too, but only to report how far the two rigs' bone bases disagree (see below) —
            // they no longer steer the recompute.
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

            // Landmark head frames. A degenerate one is a named FAIL rather than a silent no-rotation fallback:
            // the caller (CopyDescriptor) folds a FAIL into "ViewPosition left at copied vendor value", which is
            // the right answer on a rig whose head origin sits on its own eye line.
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
            // The head BONE-basis delta, reported and never applied. It is what a previous version of this tool
            // rotated the nudge by, and on a Blender round-tripped rig it reads ~90° of pure axis convention. It
            // still tells a caller something FixViewpoint no longer cares about: generic transform-rotation
            // clips authored against one basis do not retarget onto the other.
            r.headBasisDeltaDeg = Quaternion.Angle(Quaternion.Inverse(refFrame.rotation) * refHeadW,
                                                   Quaternion.Inverse(ownedFrame.rotation) * owHeadW);

            r.newVP = ComputeViewpoint(vendorVP, eyeMidRef, frameRef, eyeMidOwned, frameOwned, r.interocularRatio);
            // oldVP = the pre-recompute baseline. When the caller lands vendorVP onto owned around this call
            // (CopyDescriptor), that's the reference vendorVP in BOTH modes (so preview == execute even on an
            // overwrite re-run); otherwise (standalone door) it's the owned descriptor's current VP.
            r.oldVP = referenceVpIsBaseline ? vendorVP
                    : (ownedDesc != null ? ownedDesc.ViewPosition : vendorVP);

            // wouldWrite drives BOTH the actual write gate and the note wording, so whatIf's note predicts
            // execute's ("recomputed" vs "unchanged (< ε)") — preview == execute for the note, not just the VP.
            float deltaMm   = (r.newVP - r.oldVP).magnitude * 1000f;
            bool  wouldWrite = (r.newVP - r.oldVP).magnitude > WriteEps;
            if (!whatIf && ownedDesc != null && wouldWrite)
            {
                Undo.RecordObject(ownedDesc, "FixViewpoint: recompute ViewPosition");
                ownedDesc.ViewPosition = r.newVP;
                EditorUtility.SetDirty(ownedDesc);
                r.wrote = true;
            }

            // headBasisDeltaDeg rides both wordings: on an unchanged viewpoint a large value is precisely the
            // case that used to move it wrongly, so a caller comparing runs can see the difference is known and
            // deliberately not applied.
            //
            // The VERIFY gate keys on rotationMm — the millimetres the applied rotation moves the viewpoint —
            // and never on riseDelta, which cannot carry that weight: a joint slide along its own
            // constant-distance arc turns the frame freely with riseDelta at 0, so a riseDelta conjunct gates
            // the warning OFF in one of the exact cases it exists for (HeadFrame's docs carry the geometry and
            // the measurement). rise stays reported, as a discriminator for the pure cases. Keying on the
            // consequence is also scale-free and bounds the harm directly: a rotation too small to move the
            // viewpoint earns no warning however many degrees it reads.
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

        /// <summary>
        /// Recompute <paramref name="ownedRoot"/>'s descriptor-local <c>ViewPosition</c> from
        /// <paramref name="referenceRoot"/>'s known-good viewpoint + both rigs' eyes/head. Returns a one-line
        /// PASS/FAIL summary ending with the RunLog path; also Debug.Log/LogError's it.
        /// </summary>
        /// <param name="ownedRoot">Our owned avatar root (its VRCAvatarDescriptor's ViewPosition is written).</param>
        /// <param name="referenceRoot">REQUIRED known-good baseline (vendor source, or the pre-reshape prior
        /// version) whose descriptor + eyes/head the offset is derived from.</param>
        /// <param name="ownedLeftEye">Explicit eye transforms for a HUMANOID rig whose eyes are unmapped —
        /// live vendor config. Both of a side together; Head stays humanoid-resolved, so a non-humanoid rig
        /// still FAILs. Omit them and behaviour is unchanged. See <c>ResolveEyesHead</c>.</param>
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

        // ── Eye/head lookup — isHuman-guarded, kept LOCAL ───────────────────────────────────────────

        /// <summary>
        /// Resolve LeftEye/RightEye world positions + Head world POSITION and rotation on
        /// <paramref name="root"/>'s humanoid Animator. The head position is the third landmark
        /// <see cref="HeadFrame"/> needs; the rotation is reported, not applied.
        /// <c>GetBoneTransform</c> THROWS off-humanoid, so the humanoid guard runs first.
        /// Head is a required humanoid bone; eyes are optional and may be null even on a humanoid rig — a
        /// missing eye/head is a named FAIL (no name-based guess: a name-guessed "eye" viewpoint is worse than
        /// a loud FAIL, and the driving LLM resolves a genuinely-missing-eyes case better than a code fallback).
        ///
        /// <paramref name="leftEyeOverride"/>/<paramref name="rightEyeOverride"/> are that resolution's door —
        /// unmapped eyes with the eye OBJECTS present is live vendor config, and the agent identifies them
        /// where a name rule cannot. Nothing is guessed; the caller asserts. Both eyes are required together
        /// (<see cref="HeadFrame"/>'s X axis is <c>rightEye - leftEye</c>, so a midpoint cannot serve) and are
        /// consulted only AFTER the humanoid and Head checks, Head staying humanoid-resolved — a non-humanoid
        /// rig still FAILs. <paramref name="eyeSrc"/> reports which basis was used.
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
            // One override without the other is a caller error, not a half-basis to blend: taking one
            // supplied eye beside one mapped eye would compute an interocular axis across two frames.
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
