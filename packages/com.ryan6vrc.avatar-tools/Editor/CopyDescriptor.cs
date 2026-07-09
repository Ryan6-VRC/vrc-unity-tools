using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
            string marker = whatIf ? " (whatIf)" : "";

            if (ownedRoot == null)
            {
                string err = "[CopyDescriptor] ownedRoot is null => FAIL";
                Debug.LogError(err);
                return err;
            }
            if (vendorSource == null)
            {
                string err = "[CopyDescriptor] vendorSource is null => FAIL";
                Debug.LogError(err);
                return err;
            }

            var data = new RunData
            {
                InstanceName = ownedRoot.name,
                SourceName   = vendorSource.name,
                WhatIf       = whatIf,
            };

            // Execute-path Undo group handle. Stays -1 until the group is opened (after the gates,
            // execute only), so the finally collapses only a real group.
            int undoGroup = -1;

            try
            {
                // ── Gate A: scale / orientation ────────────────────────────────────────────
                string scaleErr = CheckScaleAndOrientation(ownedRoot.transform, vendorSource.transform);
                if (scaleErr != null)
                {
                    data.Result    = "FAIL";
                    data.GateError = "scale/orientation: " + scaleErr;
                    data.Error     = data.GateError;
                    string logPath = WriteRunLog(data, label);
                    string s = "[CopyDescriptor]" + marker + " GATE FAIL (" + data.GateError + ") => FAIL | log=" + logPath;
                    Debug.LogError(s);
                    return s;
                }

                // ── Gate B: blendshape parity ──────────────────────────────────────────────
                string bsErr = CheckBlendshapeParity(
                    ownedRoot, vendorSource,
                    out data.OurFaceMesh, out data.VendorFaceMesh);
                if (bsErr != null)
                {
                    data.Result    = "FAIL";
                    data.GateError = "blendshape parity: " + bsErr;
                    data.Error     = data.GateError;
                    string logPath = WriteRunLog(data, label);
                    string s = "[CopyDescriptor]" + marker + " GATE FAIL (" + data.GateError + ") => FAIL | log=" + logPath;
                    Debug.LogError(s);
                    return s;
                }

                // ── Find vendor descriptor ─────────────────────────────────────────────────
                var vendorDesc = vendorSource.GetComponent<VRCAvatarDescriptor>()
                              ?? vendorSource.GetComponentInChildren<VRCAvatarDescriptor>(true);
                if (vendorDesc == null)
                {
                    data.Result = "FAIL";
                    data.Error  = "VRCAvatarDescriptor not found on vendorSource (" + vendorSource.name + ")";
                    string logPath = WriteRunLog(data, label);
                    string s = "[CopyDescriptor]" + marker + " FAIL: " + data.Error + " => FAIL | log=" + logPath;
                    Debug.LogError(s);
                    return s;
                }

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
                    data.CriticalRefCount = criticalPaths.Count;
                    data.PreviewNote = "gates passed; vendor descriptor found; " + criticalPaths.Count +
                        " critical scene-ref(s) to remap; blueprintId would be cleared; remapped/nulled/leaks/lostRefs realized only on execute";

                    // C2 — preview the viewpoint recompute WITHOUT requiring the not-yet-added owned
                    // descriptor. referenceVpIsBaseline: true so oldVP == the vendorVP the copy will land in
                    // BOTH modes (preview == execute even on an overwrite re-run); mutates nothing (whatIf).
                    // Wrapped so a viewpoint fault can never demote this preview — it is a NON-fatal note.
                    try
                    {
                        var vpPrev = FixViewpoint.Recompute(ownedRoot, vendorSource, whatIf: true, referenceVpIsBaseline: true);
                        data.ViewpointNote = vpPrev.ok
                            ? vpPrev.note
                            : "verify — viewpoint would NOT recompute (" + vpPrev.failReason + "); ViewPosition would stay at copied vendor value";
                    }
                    catch (Exception vex)
                    {
                        data.ViewpointNote = "verify — viewpoint preview errored (" + vex.Message + "); ViewPosition would stay at copied vendor value";
                    }

                    data.Result = "PASS";   // gates passed + descriptor found => preview go
                    string logP = WriteRunLog(data, label);
                    string sPrev = "[CopyDescriptor] (whatIf) " + label + ": gates=passed descriptorFound criticalRefs=" +
                        criticalPaths.Count + " wouldClearBlueprint=yes (remap/leak/lostRef counts realized on execute) viewpoint=[" +
                        data.ViewpointNote + "] => PASS | log=" + logP;
                    Debug.Log(sPrev);
                    return sPrev;
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
                data.Remapped = remapResult.remapped;
                data.Nulled   = remapResult.nulled;

                // ── Fresh PipelineManager (never keep vendor's blueprintId) ────────────────
                data.BlueprintId = ClearOrCreatePipelineManager(ownedRoot);

                // ── Lost-reference check (general; subsumes viseme/eyes/eyelids) ────────────
                // For every critical vendor scene ref snapshotted above, our descriptor must now
                // hold a non-null value at the same property path. A null means the path-remap
                // silently dropped a scene ref — the worst failure mode — so record the property
                // path as an offender and gate PASS on lostRefs == 0.
                data.LostRefs = CountLostRefs(ourDesc, criticalPaths, data.LostRefPaths);

                // ── Leak check ─────────────────────────────────────────────────────────────
                // Any remaining ObjectReference still under vendorSource is a missed remap.
                data.Leaks = CountLeaks(ourDesc, vendorSource.transform);

                // ── Playable layers confirmation ────────────────────────────────────────────
                // Controllers are asset refs; remap leaves them untouched. Confirm they survived.
                data.PlayableLayers = ReportPlayableLayers(ourDesc);

                // ── Spot-checks: viseme mesh + eye transforms (report only) ────────────────
                // These names are reported for legibility; correctness is now enforced generically
                // by the lostRefs check above (a nulled viseme/eye ref shows up there as an offender).
                var visemeSMR  = ourDesc.VisemeSkinnedMesh;
                data.VisemeMesh = visemeSMR != null ? visemeSMR.name : "(null)";

                var eyeLeft   = ourDesc.customEyeLookSettings.leftEye;
                var eyeRight  = ourDesc.customEyeLookSettings.rightEye;
                data.LeftEye  = eyeLeft  != null ? eyeLeft.name  : "(null)";
                data.RightEye = eyeRight != null ? eyeRight.name : "(null)";

                // ── PASS/FAIL ──────────────────────────────────────────────────────────────
                bool pass = data.Leaks == 0
                         && data.LostRefs == 0
                         && string.IsNullOrEmpty(data.BlueprintId);
                data.Result = pass ? "PASS" : "FAIL";

                if (!pass && data.Error == null)
                {
                    var reasons = new List<string>();
                    if (data.Leaks > 0)
                        reasons.Add("leaks=" + data.Leaks);
                    if (data.LostRefs > 0)
                        reasons.Add("lostRefs=" + data.LostRefs + " [" + string.Join(", ", data.LostRefPaths) + "]");
                    if (!string.IsNullOrEmpty(data.BlueprintId))
                        reasons.Add("blueprintId not empty: " + data.BlueprintId);
                    data.Error = string.Join("; ", reasons);
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
                    data.ViewpointNote = vp.ok
                        ? vp.note
                        : "verify — viewpoint NOT recomputed (" + vp.failReason + "); ViewPosition left at copied vendor value";
                }
                catch (Exception vex)
                {
                    data.ViewpointNote = "verify — viewpoint recompute errored (" + vex.Message + "); ViewPosition left at copied vendor value";
                }
            }
            catch (Exception ex)
            {
                data.Result = "FAIL";
                data.Error  = ex.Message;
            }
            finally
            {
                // Collapse the execute-path descriptor mutations into one undo step on every exit
                // (success, an early-FAIL return inside the try, or a mid-run exception). Only when a
                // group was actually opened (execute path, past the gates); whatIf/gate-fail left it -1.
                if (undoGroup != -1) Undo.CollapseUndoOperations(undoGroup);
            }

            string logPath2 = WriteRunLog(data, label);

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CopyDescriptor]" + marker + " {0}: remapped={1}, nulled={2}, leaks={3}, lostRefs={4}, viseme={5}, leftEye={6}, rightEye={7}, blueprint={8}, layers=[{9}], viewpoint=[{10}]{11} => {12} | log={13}",
                label,
                data.Remapped, data.Nulled, data.Leaks, data.LostRefs,
                data.VisemeMesh, data.LeftEye, data.RightEye,
                string.IsNullOrEmpty(data.BlueprintId) ? "<empty>" : data.BlueprintId,
                data.PlayableLayers,
                data.ViewpointNote ?? "(not run)",
                data.Error != null ? " error=" + data.Error : "",
                data.Result, logPath2);

            if (data.Result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
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

        private static string WriteRunLog(RunData data, string label)
        {
            // whatIf writes a preview-shaped log that OMITS the execute-only counters (remapped/nulled/leaks/
            // lostRefs and the post-copy scene-ref reports) — those are unknowable without copying, so emitting
            // them as zeros would lie. The execute writer below is left byte-for-byte unchanged.
            if (data.WhatIf) return WriteWhatIfRunLog(data, label);

            Directory.CreateDirectory(TransplantCore.RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"copy-descriptor\",\n");
            sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(data.WhatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"instance\": ").Append(TransplantCore.Q(data.InstanceName)).Append(",\n");
            sb.Append("  \"source\": ").Append(TransplantCore.Q(data.SourceName)).Append(",\n");
            sb.Append("  \"result\": ").Append(TransplantCore.Q(data.Result)).Append(",\n");
            sb.Append("  \"gateError\": ").Append(TransplantCore.Q(data.GateError)).Append(",\n");
            sb.Append("  \"error\": ").Append(TransplantCore.Q(data.Error)).Append(",\n");
            sb.Append("  \"ourFaceMesh\": ").Append(TransplantCore.Q(data.OurFaceMesh)).Append(",\n");
            sb.Append("  \"vendorFaceMesh\": ").Append(TransplantCore.Q(data.VendorFaceMesh)).Append(",\n");
            sb.Append("  \"remapped\": ").Append(data.Remapped).Append(",\n");
            sb.Append("  \"nulled\": ").Append(data.Nulled).Append(",\n");
            sb.Append("  \"leaks\": ").Append(data.Leaks).Append(",\n");
            sb.Append("  \"lostRefs\": ").Append(data.LostRefs).Append(",\n");
            sb.Append("  \"lostRefPaths\": [");
            for (int i = 0; i < data.LostRefPaths.Count; i++)
            {
                sb.Append(i == 0 ? "" : ", ").Append(TransplantCore.Q(data.LostRefPaths[i]));
            }
            sb.Append("],\n");
            sb.Append("  \"blueprintId\": ").Append(TransplantCore.Q(data.BlueprintId)).Append(",\n");
            sb.Append("  \"visemeMesh\": ").Append(TransplantCore.Q(data.VisemeMesh)).Append(",\n");
            sb.Append("  \"leftEye\": ").Append(TransplantCore.Q(data.LeftEye)).Append(",\n");
            sb.Append("  \"rightEye\": ").Append(TransplantCore.Q(data.RightEye)).Append(",\n");
            sb.Append("  \"playableLayers\": ").Append(TransplantCore.Q(data.PlayableLayers)).Append(",\n");
            sb.Append("  \"viewpointNote\": ").Append(TransplantCore.Q(data.ViewpointNote)).Append("\n");
            sb.Append("}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path  = TransplantCore.RunLogDir + "/copy-descriptor_" + label + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        /// <summary>
        /// whatIf RunLog: only the keys the preview can honestly populate — the gate verdict and, when the
        /// gates passed and the snapshot was taken, descriptorFound / criticalRefCount / wouldClearBlueprint /
        /// previewNote. The execute-only counters (remapped/nulled/leaks/lostRefs and the post-copy scene-ref
        /// reports) are OMITTED, not zeroed, because they are unknowable without actually copying.
        /// </summary>
        private static string WriteWhatIfRunLog(RunData data, string label)
        {
            Directory.CreateDirectory(TransplantCore.RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"copy-descriptor\",\n");
            sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"whatIf\": true,\n");
            sb.Append("  \"instance\": ").Append(TransplantCore.Q(data.InstanceName)).Append(",\n");
            sb.Append("  \"source\": ").Append(TransplantCore.Q(data.SourceName)).Append(",\n");
            sb.Append("  \"result\": ").Append(TransplantCore.Q(data.Result)).Append(",\n");
            sb.Append("  \"gateError\": ").Append(TransplantCore.Q(data.GateError)).Append(",\n");
            sb.Append("  \"error\": ").Append(TransplantCore.Q(data.Error)).Append(",\n");
            sb.Append("  \"ourFaceMesh\": ").Append(TransplantCore.Q(data.OurFaceMesh)).Append(",\n");
            // PreviewNote is set only when the gates passed and the snapshot was taken; on a gate FAIL the
            // preview never reached the descriptor, so the go-preview keys are omitted (no misleading zeros).
            if (data.PreviewNote != null)
            {
                sb.Append("  \"vendorFaceMesh\": ").Append(TransplantCore.Q(data.VendorFaceMesh)).Append(",\n");
                sb.Append("  \"descriptorFound\": true,\n");
                sb.Append("  \"criticalRefCount\": ").Append(data.CriticalRefCount).Append(",\n");
                sb.Append("  \"wouldClearBlueprint\": true,\n");
                sb.Append("  \"viewpointNote\": ").Append(TransplantCore.Q(data.ViewpointNote)).Append(",\n");
                sb.Append("  \"previewNote\": ").Append(TransplantCore.Q(data.PreviewNote)).Append("\n");
            }
            else
            {
                sb.Append("  \"vendorFaceMesh\": ").Append(TransplantCore.Q(data.VendorFaceMesh)).Append("\n");
            }
            sb.Append("}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path  = TransplantCore.RunLogDir + "/copy-descriptor_" + label + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        // ── Data type ─────────────────────────────────────────────────────────────────────────

        private class RunData
        {
            public string InstanceName;
            public string SourceName;
            public bool   WhatIf;
            public int    CriticalRefCount;
            public string PreviewNote;
            public string Result;
            public string GateError;
            public string Error;
            public string OurFaceMesh;
            public string VendorFaceMesh;
            public int    Remapped;
            public int    Nulled;
            public int    Leaks;
            public int    LostRefs;
            public readonly List<string> LostRefPaths = new List<string>();
            public string BlueprintId;
            public string VisemeMesh;
            public string LeftEye;
            public string RightEye;
            public string PlayableLayers;
            /// <summary>
            /// State-asserting FixViewpoint fold note (NON-fatal — never flips the verdict). Success →
            /// "viewpoint recomputed: …"; a miss → "verify — viewpoint NOT recomputed (…); ViewPosition left
            /// at copied vendor value", so a CopyDescriptor PASS is never misread as "viewpoint correct".
            /// </summary>
            public string ViewpointNote;
        }
    }
}
