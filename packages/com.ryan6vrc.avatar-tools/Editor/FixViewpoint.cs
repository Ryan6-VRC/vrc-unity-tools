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
    /// Three levels (tooldesign — pure core / thin door): <see cref="ComputeViewpoint"/> is the VRC-free,
    /// NUnit-tested math; <see cref="Recompute"/> is the VRC-typed core (descriptor resolve + isHuman-guarded
    /// eyes/head + descriptor-local conversion + eps guards + delta-gated write), writing NO RunLog so a host
    /// tool (CopyDescriptor) folds it into its own log; <see cref="Run"/> is the standalone door.
    ///
    /// PASS = viewpoint recomputed (or unchanged within ε). Standalone FAILs named on: no reference/owned
    /// descriptor, non-humanoid rig, unmapped eyes, or coincident eyes. RunLog kind <c>fix-viewpoint</c>.
    /// </summary>
    [AgentTool]
    public static class FixViewpoint
    {
        // Delta-gate: skip the write (and the dirty) when the recompute lands within 0.1 mm of the current
        // value — a re-run on unchanged geometry recomputes the same VP, so it stays idempotent-clean.
        const float WriteEps = 1e-4f;
        // Interocular magnitude floor: below this the eyes are coincident and the scale ratio is undefined.
        const float MagEps = 1e-5f;

        // ── Pure math (VRC-free; the NUnit-tested core) ─────────────────────────────────────────────

        /// <summary>
        /// The similarity-frame viewpoint recompute, in DESCRIPTOR-LOCAL space:
        /// <c>newVP = eyeMidOwned + s · (Rₒ · R_v⁻¹) · (vendorVP − eyeMidRef)</c>, where
        /// <c>Rₒ · R_v⁻¹ = headRotOwned · Quaternion.Inverse(headRotRef)</c> is the head-orientation delta
        /// from reference to owned, and <c>s = interocularRatio</c> scales the eye→VP nudge. Preserves the
        /// creator's eye→viewpoint nudge (<c>vendorVP − eyeMidRef</c>) while re-seating it on the owned eye
        /// midpoint and tracking head rotate + uniform head resize. Depends only on Vector3/Quaternion.
        /// </summary>
        public static Vector3 ComputeViewpoint(Vector3 vendorVP, Vector3 eyeMidRef, Quaternion headRotRef,
                                               Vector3 eyeMidOwned, Quaternion headRotOwned, float interocularRatio)
        {
            Vector3 nudge = vendorVP - eyeMidRef;                              // creator's eye→VP offset
            Quaternion rotDelta = headRotOwned * Quaternion.Inverse(headRotRef); // Rₒ · R_v⁻¹
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
            public string failReason;   // set (ok == false) on any named FAIL condition
            public string note;         // human-readable state line for the host log (success/unchanged)
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
                                                  bool referenceVpIsBaseline = false)
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

            if (!ResolveEyesHead(referenceRoot, "reference", out Vector3 refLW, out Vector3 refRW, out Quaternion refHeadW, out r.failReason)) return r;
            if (!ResolveEyesHead(ownedRoot,     "owned",     out Vector3 owLW,  out Vector3 owRW,  out Quaternion owHeadW,  out r.failReason)) return r;

            // World → descriptor-local (positions about the frame; head basis relative to the frame rotation).
            Vector3 refL = refFrame.InverseTransformPoint(refLW);
            Vector3 refR = refFrame.InverseTransformPoint(refRW);
            Vector3 owL  = ownedFrame.InverseTransformPoint(owLW);
            Vector3 owR  = ownedFrame.InverseTransformPoint(owRW);
            Quaternion headRotRef   = Quaternion.Inverse(refFrame.rotation)   * refHeadW;
            Quaternion headRotOwned = Quaternion.Inverse(ownedFrame.rotation) * owHeadW;

            float refInteroc   = (refR - refL).magnitude;
            float ownedInteroc = (owR - owL).magnitude;
            if (refInteroc   < MagEps) { r.failReason = "reference eyes coincident — cannot derive interocular scale"; return r; }
            if (ownedInteroc < MagEps) { r.failReason = "owned eyes coincident — cannot derive interocular scale"; return r; }

            r.interocularRatio = ownedInteroc / refInteroc;
            Vector3 eyeMidRef   = (refL + refR) * 0.5f;
            Vector3 eyeMidOwned = (owL + owR) * 0.5f;
            Vector3 vendorVP    = refDesc.ViewPosition;

            r.newVP = ComputeViewpoint(vendorVP, eyeMidRef, headRotRef, eyeMidOwned, headRotOwned, r.interocularRatio);
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

            r.note = wouldWrite
                ? string.Format(CultureInfo.InvariantCulture,
                    "viewpoint recomputed: {0} → {1} (deltaMm={2:F2}, s={3:F4})",
                    Fmt(r.oldVP), Fmt(r.newVP), deltaMm, r.interocularRatio)
                : "viewpoint unchanged (< ε)";
            r.ok = true;
            return r;
        }

        // ── Door ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Recompute <paramref name="ownedRoot"/>'s descriptor-local <c>ViewPosition</c> from
        /// <paramref name="referenceRoot"/>'s known-good viewpoint + both rigs' eyes/head. Returns a one-line
        /// PASS/FAIL summary ending with the RunLog path; also Debug.Log/LogError's it and copies the path to
        /// the clipboard.
        /// </summary>
        /// <param name="ownedRoot">Our owned avatar root (its VRCAvatarDescriptor's ViewPosition is written).</param>
        /// <param name="referenceRoot">REQUIRED known-good baseline (vendor source, or the pre-reshape prior
        /// version) whose descriptor + eyes/head the offset is derived from.</param>
        public static string Run(GameObject ownedRoot, GameObject referenceRoot, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";
            var log = new TransplantRunLog("fix-viewpoint")
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

            var r = Recompute(ownedRoot, referenceRoot, whatIf);
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
        /// Resolve LeftEye/RightEye world positions + Head world rotation on <paramref name="root"/>'s
        /// humanoid Animator. <c>GetBoneTransform</c> THROWS off-humanoid, so the humanoid guard runs first.
        /// Head is a required humanoid bone; eyes are optional and may be null even on a humanoid rig — a
        /// missing eye/head is a named FAIL (no name-based guess: a name-guessed "eye" viewpoint is worse than
        /// a loud FAIL, and the driving LLM resolves a genuinely-missing-eyes case better than a code fallback).
        /// </summary>
        static bool ResolveEyesHead(GameObject root, string which,
                                    out Vector3 leftEyeW, out Vector3 rightEyeW, out Quaternion headW,
                                    out string failReason)
        {
            leftEyeW = rightEyeW = Vector3.zero;
            headW = Quaternion.identity;
            failReason = null;

            var animator = root.GetComponent<Animator>() ?? root.GetComponentInChildren<Animator>(true);
            bool isHumanoid = animator != null && animator.avatar != null && animator.avatar.isHuman;
            if (!isHumanoid)
            {
                failReason = which + " rig is not humanoid — FixViewpoint needs mapped eyes + head";
                return false;
            }

            Transform head  = animator.GetBoneTransform(HumanBodyBones.Head);
            Transform left  = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform right = animator.GetBoneTransform(HumanBodyBones.RightEye);
            if (head == null)  { failReason = which + " Head unmapped"; return false; }
            if (left == null)  { failReason = which + " LeftEye unmapped"; return false; }
            if (right == null) { failReason = which + " RightEye unmapped"; return false; }

            leftEyeW  = left.position;
            rightEyeW = right.position;
            headW     = head.rotation;
            return true;
        }

        static string Fmt(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4})", v.x, v.y, v.z);
    }
}
