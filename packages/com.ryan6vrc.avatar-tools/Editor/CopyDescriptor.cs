using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Transplants the vendor's VRCAvatarDescriptor onto our owned avatar, remapping every
    /// scene reference from the vendor hierarchy to ours via <see cref="RemapReferencesByPath"/>.
    ///
    /// Two safety gates abort early if the transplant would silently produce wrong results:
    ///   A) Scale/orientation: the descriptor's view position, eye offsets, and collider offsets
    ///      are authored in world-space scale — a mismatch means they'd be wrong on our avatar.
    ///   B) Blendshape parity: viseme and eyelid bindings are by blendshape index, so the face
    ///      mesh must have the same blendshape names in the same order on both sides.
    ///
    /// The PipelineManager's blueprintId is always cleared so we never inherit the vendor's
    /// uploaded avatar ID.
    ///
    /// Call <see cref="Run"/> from MCP execute_code.
    /// PASS = gate passed, 0 vendor-hierarchy leaks, blueprintId empty.
    /// </summary>
    [AgentTool]
    public static class CopyDescriptor
    {
        // ── Public API ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Transplant the VRCAvatarDescriptor from <paramref name="vendorSource"/> onto
        /// <paramref name="ownedRoot"/>, remapping all scene refs to our hierarchy.
        /// Returns a one-line PASS/FAIL summary ending with the RunLog path (<c>… => RESULT | log=&lt;path&gt;</c>);
        /// also Debug.Log/LogError it.
        /// </summary>
        /// <param name="ownedRoot">Our owned avatar root in the scene (Animator already configured).</param>
        /// <param name="vendorSource">The vendor's dressed source carrying the VRCAvatarDescriptor to copy.</param>
        public static string Run(GameObject ownedRoot, GameObject vendorSource, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";

            if (ownedRoot == null)     return ArgFail(label, whatIf, ownedRoot, vendorSource, "ownedRoot is null");
            if (vendorSource == null)  return ArgFail(label, whatIf, ownedRoot, vendorSource, "vendorSource is null");

            var data = new RunData
            {
                instance = ownedRoot.name,
                source   = vendorSource.name,
                whatIf   = whatIf,
            };

            // Execute-path Undo group handle. Stays -1 until the group is opened (after the gates,
            // execute only), so the finally collapses only a real group.
            int undoGroup = -1;

            try
            {
                // ── Gate A: scale / orientation ────────────────────────────────────────────
                string scaleErr = CheckScaleAndOrientation(ownedRoot.transform, vendorSource.transform);
                if (scaleErr != null)
                    return Fail(data, label, "gate scale/orientation: " + scaleErr);

                // ── Gate B: blendshape parity ──────────────────────────────────────────────
                string bsErr = CheckBlendshapeParity(
                    ownedRoot, vendorSource,
                    out string ourFaceMesh, out string vendorFaceMesh);
                // Note each side independently — on a one-sided parity FAIL the side that WAS found is
                // the diagnostic separating "wrong vendor object" from "our copy broken".
                if (ourFaceMesh != null)    data.Note("ourFaceMesh='" + ourFaceMesh + "'");
                if (vendorFaceMesh != null) data.Note("vendorFaceMesh='" + vendorFaceMesh + "'");
                if (bsErr != null)
                    return Fail(data, label, "gate blendshape parity: " + bsErr);

                // ── Find vendor descriptor ─────────────────────────────────────────────────
                var vendorDesc = vendorSource.GetComponent<VRCAvatarDescriptor>()
                              ?? vendorSource.GetComponentInChildren<VRCAvatarDescriptor>(true);
                if (vendorDesc == null)
                    return Fail(data, label, "VRCAvatarDescriptor not found on vendorSource (" + vendorSource.name + ")");

                // ── Snapshot critical scene refs on the VENDOR descriptor ──────────────────
                // Before copying, record every ObjectReference property on the vendor descriptor
                // whose value is non-null AND under the vendor hierarchy — these are the scene refs
                // (viseme mesh, eye/eyelid transforms, collider transforms, …) we MUST preserve.
                // Keyed by serialized property path so the post-remap check is field-agnostic:
                // a path-remap that nulls ANY of them (broken lip-sync / eye-tracking / etc.) is a
                // lostRef and must FAIL, rather than special-casing viseme/eyes per field.
                var criticalPaths = SnapshotSceneRefPaths(vendorDesc, vendorSource.transform);

                // ── whatIf: preview go/no-go, mutate nothing ───────────────────────────────
                // Gates passed and the vendor descriptor was found → preview go. The remap/leak/
                // lostRef realities only exist after the copy, so we report the critical scene-ref
                // count that WOULD be remapped and return BEFORE any AddComponent/CopySerialized/
                // remap/blueprint-clear.
                if (whatIf)
                {
                    data.Count("criticalRefs", criticalPaths.Count);
                    data.Note("gates passed; vendor descriptor found; blueprintId would be cleared; " +
                        "remapped/nulled/leaks/lostRefs realized only on execute");

                    // C2 — preview the viewpoint recompute WITHOUT requiring the not-yet-added owned
                    // descriptor. referenceVpIsBaseline: true so oldVP == the vendorVP the copy will land in
                    // BOTH modes (preview == execute even on an overwrite re-run); mutates nothing (whatIf).
                    // Wrapped so a viewpoint fault can never demote this preview — it is a NON-fatal note.
                    try
                    {
                        var vpPrev = FixViewpoint.Recompute(ownedRoot, vendorSource, whatIf: true, referenceVpIsBaseline: true);
                        data.Note("viewpoint: " + (vpPrev.ok
                            ? vpPrev.note
                            : "verify — viewpoint would NOT recompute (" + vpPrev.failReason + "); ViewPosition would stay at copied vendor value"));
                    }
                    catch (Exception vex)
                    {
                        data.Note("viewpoint: verify — viewpoint preview errored (" + vex.Message + "); ViewPosition would stay at copied vendor value");
                    }

                    data.result = "PASS";   // gates passed + descriptor found => preview go
                    return TransplantCore.Finish(data, label);
                }

                // ── Execute-path Undo group: descriptor add/overwrite + CopySerialized +
                //    remap-apply + PipelineManager add/clear collapse into one undo step. ──────
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("CopyDescriptor");
                undoGroup = Undo.GetCurrentGroup();

                // ── Get or add descriptor on ownedRoot ───────────────────────────────────
                // AddComponent may also auto-add PipelineManager via RequireComponent.
                var ourDesc  = ownedRoot.GetComponent<VRCAvatarDescriptor>();
                bool wasAdded = ourDesc == null;
                if (wasAdded)
                    ourDesc = Undo.AddComponent<VRCAvatarDescriptor>(ownedRoot);
                else
                    Undo.RecordObject(ourDesc, "CopyDescriptor: overwrite VRCAvatarDescriptor");

                // ── Copy all serialized fields (vendor → ours) ─────────────────────────────
                EditorUtility.CopySerialized(vendorDesc, ourDesc);
                EditorUtility.SetDirty(ourDesc);

                // ── Remap scene refs via shared helper ─────────────────────────────────────
                // Rebinds viseme mesh, eye-look transforms, eyelid mesh, collider transforms, etc.
                // from the vendor hierarchy to the same indexed hierarchy paths under ours.
                // Asset references (playable layer controllers) are NOT under srcRoot, so they
                // are left untouched by the helper.
                var so          = new SerializedObject(ourDesc);
                var remapResult = RemapReferencesByPath.Remap(so, vendorSource.transform, ownedRoot.transform);
                so.ApplyModifiedPropertiesWithoutUndo();
                data.Count("remapped", remapResult.remapped);
                data.Count("nulled", remapResult.nulled);

                // ── Fresh PipelineManager (never keep vendor's blueprintId) ────────────────
                string blueprintId = ClearOrCreatePipelineManager(ownedRoot);
                data.Note("blueprint=" + (string.IsNullOrEmpty(blueprintId) ? "<empty>" : blueprintId));

                // ── Lost-reference check (general; subsumes viseme/eyes/eyelids) ────────────
                // For every critical vendor scene ref snapshotted above, our descriptor must now
                // hold a non-null value at the same property path. A null means the path-remap
                // silently dropped a scene ref — the worst failure mode — so record the property
                // path as an offender and gate PASS on lostRefs == 0.
                var lostRefPaths = new List<string>();
                int lostRefs = CountLostRefs(ourDesc, criticalPaths, lostRefPaths);
                data.Count("lostRefs", lostRefs);
                foreach (var p in lostRefPaths)
                    data.Offender("lostRef: '" + p + "' nulled by the remap (broken lip-sync/eye-tracking/collider ref)");

                // ── Leak check ─────────────────────────────────────────────────────────────
                // Any remaining ObjectReference still under vendorSource is a missed remap.
                int leaks = CountLeaks(ourDesc, vendorSource.transform);
                data.Count("leaks", leaks);
                if (leaks > 0)
                    data.Offender("leaks=" + leaks + ": descriptor ref(s) still point into the vendor hierarchy (missed remap)");

                // ── Playable layers confirmation ────────────────────────────────────────────
                // Controllers are asset refs; remap leaves them untouched. Confirm they survived.
                data.Note("layers=[" + ReportPlayableLayers(ourDesc) + "]");

                // ── Spot-checks: viseme mesh + eye transforms (report only) ────────────────
                // These names are reported for legibility; correctness is now enforced generically
                // by the lostRefs check above (a nulled viseme/eye ref shows up there as an offender).
                var visemeSMR = ourDesc.VisemeSkinnedMesh;
                data.Note("viseme=" + (visemeSMR != null ? visemeSMR.name : "(null)"));

                var eyeLeft   = ourDesc.customEyeLookSettings.leftEye;
                var eyeRight  = ourDesc.customEyeLookSettings.rightEye;
                data.Note("leftEye=" + (eyeLeft != null ? eyeLeft.name : "(null)")
                        + " rightEye=" + (eyeRight != null ? eyeRight.name : "(null)"));

                // ── PASS/FAIL ──────────────────────────────────────────────────────────────
                if (!string.IsNullOrEmpty(blueprintId))
                    data.Offender("blueprintId not cleared: " + blueprintId);
                bool pass = leaks == 0
                         && lostRefs == 0
                         && string.IsNullOrEmpty(blueprintId);
                data.result = pass ? "PASS" : "FAIL";
                if (!pass)
                {
                    // The per-item offenders above carry the detail; error carries the joined headline
                    // so a FAIL RunLog never reads "error": null (and matches CleanController's verify FAIL).
                    var reasons = new List<string>();
                    if (leaks > 0)    reasons.Add("leaks=" + leaks);
                    if (lostRefs > 0) reasons.Add("lostRefs=" + lostRefs);
                    if (!string.IsNullOrEmpty(blueprintId)) reasons.Add("blueprintId not cleared");
                    data.error = string.Join("; ", reasons);
                }

                // ── Viewpoint recompute (C6, NON-fatal) ────────────────────────────────────────────
                // After the leak/lostRef gates, recompute ViewPosition from the vendor reference + the
                // owned eyes/head. referenceVpIsBaseline: true — the just-copied vendorVP is the delta-gate
                // baseline (write only when |newVP − copiedVP| > ε). The recompute core writes inside this
                // open Undo group. It rides as a prominent STATE-asserting note and does NOT flip the verdict:
                // a recompute miss (no eyes) leaves the copied ViewPosition in place — exactly today's
                // no-recompute behavior, not a regression that should FAIL. Wrapped so a viewpoint fault can
                // never demote the copy verdict (the real gates are leaks/lostRefs/blueprint).
                try
                {
                    var vp = FixViewpoint.Recompute(ownedRoot, vendorSource, whatIf: false, referenceVpIsBaseline: true);
                    data.Note("viewpoint: " + (vp.ok
                        ? vp.note
                        : "verify — viewpoint NOT recomputed (" + vp.failReason + "); ViewPosition left at copied vendor value"));
                }
                catch (Exception vex)
                {
                    data.Note("viewpoint: verify — viewpoint recompute errored (" + vex.Message + "); ViewPosition left at copied vendor value");
                }
            }
            catch (Exception ex)
            {
                data.result = "FAIL";
                data.error  = ex.Message;
            }
            finally
            {
                // Collapse the execute-path descriptor mutations into one undo step on every exit
                // (success, an early-FAIL return inside the try, or a mid-run exception). Only when a
                // group was actually opened (execute path, past the gates); whatIf/gate-fail left it -1.
                if (undoGroup != -1) Undo.CollapseUndoOperations(undoGroup);
            }

            return TransplantCore.Finish(data, label);
        }

        // ── Gate A: scale / orientation ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns a diagnostic string if scale or rotation are mismatched, null on pass.
        /// Scale: element-wise relative tolerance 1e-3.
        /// Rotation: quaternion angle must be &lt;1°.
        /// </summary>
        private static string CheckScaleAndOrientation(Transform ours, Transform vendor)
        {
            Vector3 os = ours.lossyScale;
            Vector3 vs = vendor.lossyScale;
            for (int i = 0; i < 3; i++)
            {
                float refVal = Mathf.Abs(vs[i]);
                float diff   = Mathf.Abs(os[i] - vs[i]);
                float tol    = refVal < 1e-6f ? 1e-3f : refVal * 1e-3f;
                if (diff > tol)
                    return string.Format(CultureInfo.InvariantCulture,
                        "scale component [{0}] ours={1:G6} vendor={2:G6} (diff={3:G3}, tol={4:G3})",
                        i, os[i], vs[i], diff, tol);
            }

            float angle = Quaternion.Angle(ours.rotation, vendor.rotation);
            if (angle > 1f)
                return string.Format(CultureInfo.InvariantCulture,
                    "rotation angle={0:F3}° (threshold=1°)", angle);

            return null; // pass
        }

        // ── Gate B: blendshape parity ─────────────────────────────────────────────────────────

        /// <summary>
        /// Finds the face SMR (most blendshapes) on each side and compares the full name list in
        /// order. Returns a diagnostic string on mismatch, null on pass.
        /// </summary>
        private static string CheckBlendshapeParity(
            GameObject ourRoot, GameObject vendorRoot,
            out string ourFaceMeshName, out string vendorFaceMeshName)
        {
            ourFaceMeshName    = null;
            vendorFaceMeshName = null;

            var ourSMR    = FindFaceSMR(ourRoot);
            var vendorSMR = FindFaceSMR(vendorRoot);

            if (ourSMR == null || ourSMR.sharedMesh == null)
                return "could not find face SkinnedMeshRenderer on ownedRoot (need ≥1 SMR with blendshapes)";
            if (vendorSMR == null || vendorSMR.sharedMesh == null)
                return "could not find face SkinnedMeshRenderer on vendorSource (need ≥1 SMR with blendshapes)";

            ourFaceMeshName    = ourSMR.name;
            vendorFaceMeshName = vendorSMR.name;

            var ourMesh   = ourSMR.sharedMesh;
            var vendMesh  = vendorSMR.sharedMesh;
            int ourCount  = ourMesh.blendShapeCount;
            int vendCount = vendMesh.blendShapeCount;

            if (ourCount != vendCount)
                return string.Format(CultureInfo.InvariantCulture,
                    "blendshape count: ours({0})={1} vs vendor({2})={3}",
                    ourFaceMeshName, ourCount, vendorFaceMeshName, vendCount);

            for (int i = 0; i < ourCount; i++)
            {
                string on = ourMesh.GetBlendShapeName(i);
                string vn = vendMesh.GetBlendShapeName(i);
                if (on != vn)
                    return string.Format(
                        "blendshape[{0}] name mismatch: ours='{1}' vendor='{2}'", i, on, vn);
            }

            return null; // pass
        }

        /// <summary>Returns the SkinnedMeshRenderer with the most blendshapes under root.</summary>
        private static SkinnedMeshRenderer FindFaceSMR(GameObject root)
        {
            SkinnedMeshRenderer best = null;
            int bestCount = -1;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                int n = smr.sharedMesh.blendShapeCount;
                if (n > bestCount) { bestCount = n; best = smr; }
            }
            return best;
        }

        // ── PipelineManager ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ensures ownedRoot has a PipelineManager with an EMPTY blueprintId.
        /// VRCAvatarDescriptor carries [RequireComponent(typeof(PipelineManager))], so one may
        /// already exist (possibly carrying the vendor's ID from the EditorUtility.CopySerialized
        /// step). We always clear the field via SerializedObject.
        /// Returns the blueprintId after clearing (must be "" for PASS).
        /// </summary>
        private static string ClearOrCreatePipelineManager(GameObject ownedRoot)
        {
            var pm = ownedRoot.GetComponent<PipelineManager>();
            if (pm == null)
            {
                Undo.RecordObject(ownedRoot, "CopyDescriptor: AddComponent PipelineManager");
                pm = Undo.AddComponent<PipelineManager>(ownedRoot);
            }
            else
            {
                Undo.RecordObject(pm, "CopyDescriptor: clear PipelineManager blueprintId");
            }

            // Clear via SerializedObject to handle both serialized-field and backing-field edge cases.
            var pmSo   = new SerializedObject(pm);
            var bpProp = pmSo.FindProperty("blueprintId");
            if (bpProp != null && bpProp.propertyType == SerializedPropertyType.String)
            {
                bpProp.stringValue = "";
                pmSo.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorUtility.SetDirty(pm);

            // Read back the live public field to confirm the clear took effect.
            return pm.blueprintId ?? "";
        }

        // ── Lost-reference check ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Snapshot the serialized property paths of every ObjectReference on <paramref name="desc"/>
        /// whose value is non-null and lives under <paramref name="vendorRoot"/>. These are the scene
        /// refs the remap must preserve; keyed by property path so the post-remap check is generic.
        /// </summary>
        private static List<string> SnapshotSceneRefPaths(VRCAvatarDescriptor desc, Transform vendorRoot)
        {
            var paths = new List<string>();
            var so = new SerializedObject(desc);
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                var o = it.objectReferenceValue;
                if (o == null) continue;
                Transform t = o is Component c ? c.transform
                            : o is GameObject g ? g.transform
                            : null;
                if (t == null || !t.IsChildOf(vendorRoot)) continue;
                paths.Add(it.propertyPath);
            }
            return paths;
        }

        /// <summary>
        /// For each snapshotted critical path, count those that are null on our descriptor after the
        /// remap (a silently dropped scene ref). Records each offending property path in
        /// <paramref name="offenders"/>. The descriptor layout matches the vendor's (it was produced
        /// by CopySerialized), so the property paths line up one-to-one.
        /// </summary>
        private static int CountLostRefs(VRCAvatarDescriptor ourDesc, List<string> criticalPaths, List<string> offenders)
        {
            int lost = 0;
            var so = new SerializedObject(ourDesc);
            foreach (var path in criticalPaths)
            {
                var p = so.FindProperty(path);
                if (p == null
                    || p.propertyType != SerializedPropertyType.ObjectReference
                    || p.objectReferenceValue == null)
                {
                    lost++;
                    offenders.Add(path);
                }
            }
            return lost;
        }

        // ── Leak check ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Count ObjectReferences on the descriptor that still point into the vendor hierarchy.
        /// A clean remap should leave zero; any non-zero count means a path was missing under ours.
        /// </summary>
        private static int CountLeaks(VRCAvatarDescriptor ourDesc, Transform vendorRoot)
        {
            int leaks = 0;
            var so = new SerializedObject(ourDesc);
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                var o = it.objectReferenceValue;
                if (o == null) continue;
                Transform t = o is Component c ? c.transform
                            : o is GameObject g ? g.transform
                            : null;
                if (t == null) continue;
                if (t.IsChildOf(vendorRoot)) leaks++;
            }
            return leaks;
        }

        // ── Playable layers ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Summarises which playable-layer controllers are non-null after the copy.
        /// Controllers are asset references (not scene refs) so the remap leaves them untouched.
        /// </summary>
        private static string ReportPlayableLayers(VRCAvatarDescriptor desc)
        {
            var sb = new StringBuilder();
            AppendLayers(sb, desc.baseAnimationLayers);
            AppendLayers(sb, desc.specialAnimationLayers);
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        private static void AppendLayers(StringBuilder sb, VRCAvatarDescriptor.CustomAnimLayer[] layers)
        {
            if (layers == null) return;
            foreach (var layer in layers)
            {
                if (layer.isDefault) continue; // ignore empty/unset default layers
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(layer.type.ToString()).Append('=');
                sb.Append(layer.animatorController != null ? layer.animatorController.name : "(null)");
            }
        }

        // ── RunLog output ─────────────────────────────────────────────────────────────────────
        // The shared envelope replaces the old dual writers (execute-shaped + whatIf-shaped): counts
        // are emitted only where added, so the whatIf log honestly omits the execute-only counters
        // (remapped/nulled/leaks/lostRefs are unknowable without copying) with a single writer.

        /// <summary>Route an argument-guard failure through the shared envelope tail — never a bare
        /// trailer-less line (the guards previously returned one, with no RunLog at all).</summary>
        private static string ArgFail(string label, bool whatIf, GameObject ownedRoot, GameObject source, string msg)
        {
            var data = new RunData
            {
                instance = ownedRoot != null ? ownedRoot.name : null,
                source   = source != null ? source.name : null,
                whatIf   = whatIf,
            };
            return Fail(data, label, msg);
        }

        /// <summary>Fail an in-flight run (gate / missing-descriptor stage) through the same grammar:
        /// named offender + <c>error</c>, shared envelope tail.</summary>
        private static string Fail(RunData data, string label, string msg)
        {
            data.result = "FAIL";
            data.error  = msg;
            data.Offender(msg);
            return TransplantCore.Finish(data, label);
        }

        // ── Data type ─────────────────────────────────────────────────────────────────────────

        /// <summary>The package RunLog envelope; counts/offenders/notes are added at the site that
        /// computes them (state assertions — viseme/eyes/blueprint/layers/viewpoint — ride as notes,
        /// so the one-line summary still carries them; the viewpoint note is the NON-fatal
        /// state-asserting FixViewpoint fold that never flips the verdict).</summary>
        private sealed class RunData : TransplantRunLog
        {
            public RunData() : base("copy-descriptor") { }
        }
    }
}
