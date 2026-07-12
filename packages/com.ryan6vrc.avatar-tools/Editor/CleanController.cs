using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Produces a clean controller (typically FX) keeping only the named layers (plus the base layer 0,
    /// always retained), empty VRCExpressionParameters, and empty VRCExpressionsMenu, then
    /// wires them into our avatar's VRCAvatarDescriptor.
    ///
    /// Used at the start of a new build run to discard the vendor's elaborate FX
    /// (outfit toggles, etc.) and start fresh. Re-running is idempotent and GUID-stable:
    /// outputs use stable, label-free names so variations of one base avatar SHARE them in a
    /// common folder. Existing outputs are REUSED in place (preserving asset GUIDs so any
    /// descriptor already pointing at them stays wired) — never delete-recreated:
    ///   - empty params/menu: load the existing asset and reuse it (ensure-empty in place if
    ///     something external wrote into it — visible RunLog note, still PASS);
    ///   - clean controller: load the existing asset and re-run the layer-trim on it in place.
    /// To force a fresh derive of the controller, the operator deletes the asset manually.
    ///
    /// Caller passes the layer names to keep (e.g. the base + Left/Right hand gesture layers);
    /// all other layers are dropped. Layer 0 is always kept (VRChat requires a base layer).
    /// Selection is name-based, not index-based — a requested name that is absent or matches
    /// more than one layer FAILs loud rather than silently dropping a layer.
    ///
    /// Call <see cref="Run"/> from MCP execute_code.
    /// PASS = every requested layer survives by name in the clean controller; descriptor
    /// FX/params/menu wired and empty.
    /// </summary>
    [AgentTool]
    public static class CleanController
    {
        // ── Public API ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Duplicate <paramref name="sourceFx"/>, keep only the named layers (plus base layer 0),
        /// prune the controller's parameter list to those the kept layers reference,
        /// create empty VRCExpressionParameters + VRCExpressionsMenu, and wire them into
        /// <paramref name="ownedRoot"/>'s VRCAvatarDescriptor.
        /// Returns a one-line PASS/FAIL summary ending with the RunLog path (<c>… => RESULT | log=&lt;path&gt;</c>);
        /// also Debug.Log/LogError it.
        /// </summary>
        /// <param name="sourceFx">Vendor FX controller to base the clean one on.</param>
        /// <param name="ownedRoot">Our avatar root in the scene (has VRCAvatarDescriptor).</param>
        /// <param name="outDir">Asset folder for the outputs (e.g. "Assets/Agent/Scratch").</param>
        /// <param name="keepLayerNames">Layer names to keep; layer 0 is always kept regardless.</param>
        public static string Run(AnimatorController sourceFx, GameObject ownedRoot, string outDir, string[] keepLayerNames, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";

            if (sourceFx == null)
            {
                string err = "[CleanController] sourceFx is null => FAIL";
                Debug.LogError(err);
                return err;
            }
            if (ownedRoot == null)
            {
                string err = "[CleanController] ownedRoot is null => FAIL";
                Debug.LogError(err);
                return err;
            }
            if (string.IsNullOrEmpty(outDir))
            {
                string err = "[CleanController] outDir is null or empty => FAIL";
                Debug.LogError(err);
                return err;
            }

            var data = new RunData
            {
                SourceFxName = sourceFx.name,
                InstanceName = ownedRoot.name,
                OutDir       = outDir,
                WhatIf       = whatIf,
            };

            try
            {
                // ── whatIf: report-only preview — resolve preconditions and report the plan,
                //    creating/modifying NO asset and touching NO descriptor. Returns before any
                //    EnsureFolderExists/CopyAsset/CreateAsset/SaveAssets/descriptor wiring. ─────
                if (whatIf)
                {
                    string previewSourceFxPath = AssetDatabase.GetAssetPath(sourceFx);
                    if (string.IsNullOrEmpty(previewSourceFxPath))
                    {
                        data.Result = "FAIL";
                        data.Error  = "sourceFx has no asset path (runtime/in-memory controller not supported)";
                        return FinishEarly(data, label);
                    }

                    string previewDestPath = outDir.TrimEnd('/') + "/" + sourceFx.name + "_Clean.controller";
                    data.CleanFxReused = AssetDatabase.LoadMainAssetAtPath(previewDestPath) != null;
                    data.CleanFxPath   = previewDestPath;
                    // Verdict must equal execute's. Execute typed-loads a REUSED controller and FAILs if the
                    // asset at that path is the wrong type — mirror that (a new controller is copied from the
                    // source, always valid, so only the reuse case is checkable at preview time).
                    if (data.CleanFxReused && AssetDatabase.LoadAssetAtPath<AnimatorController>(previewDestPath) == null)
                    {
                        data.Result = "FAIL";
                        data.Error  = "asset at " + previewDestPath + " exists but is not an AnimatorController";
                        return FinishEarly(data, label);
                    }

                    // Keep-set from the SOURCE controller's layers. The source and the would-be-copied
                    // controller share layers, so this is exactly the set execute would keep.
                    var srcLayers = sourceFx.layers;
                    var previewAllNames = new string[srcLayers.Length];
                    for (int i = 0; i < srcLayers.Length; i++) previewAllNames[i] = srcLayers[i].name;
                    string previewSelErr;
                    int[] previewKeepIdx = SelectLayersToKeep(previewAllNames, keepLayerNames, out previewSelErr);
                    if (previewKeepIdx == null)
                    {
                        data.Result = "FAIL";
                        data.Error  = previewSelErr;
                        return FinishEarly(data, label);
                    }
                    var previewKeptNames = new string[previewKeepIdx.Length];
                    for (int i = 0; i < previewKeepIdx.Length; i++) previewKeptNames[i] = srcLayers[previewKeepIdx[i]].name;
                    data.LayerCount = previewKeepIdx.Length;
                    data.LayerNames = string.Join("/", previewKeptNames);

                    // Predicted parameter prune, from the same layer set the trim would keep.
                    // Counts are relative to the SOURCE controller; on a reused (already-pruned)
                    // clean controller the execute-time drop count lands lower (whatIf predicts
                    // the verdict, counts land on execute — the CopyDescriptor precedent).
                    var previewKeptLayers = new AnimatorControllerLayer[previewKeepIdx.Length];
                    for (int i = 0; i < previewKeepIdx.Length; i++) previewKeptLayers[i] = srcLayers[previewKeepIdx[i]];
                    var previewReferenced = CollectReferencedParameters(previewKeptLayers, srcLayers);
                    int previewParamsKept = 0;
                    var previewParamsDropped = new List<string>();
                    foreach (var p in sourceFx.parameters)
                    {
                        if (previewReferenced.Contains(p.name)) previewParamsKept++;
                        else previewParamsDropped.Add(p.name);
                    }
                    data.CtrlParamsKept = previewParamsKept;
                    data.CtrlParamsDropped = previewParamsDropped.Count;
                    data.CtrlParamsDroppedNames = string.Join("/", previewParamsDropped.ToArray());

                    string previewParamsPath = outDir.TrimEnd('/') + "/VRCExpressionParameters_Empty.asset";
                    string previewMenuPath   = outDir.TrimEnd('/') + "/VRCExpressionsMenu_Empty.asset";
                    data.ParamsReused = AssetDatabase.LoadMainAssetAtPath(previewParamsPath) != null;
                    data.ParamsPath   = previewParamsPath;
                    data.ParamCount   = 0;
                    data.MenuReused   = AssetDatabase.LoadMainAssetAtPath(previewMenuPath) != null;
                    data.MenuPath     = previewMenuPath;
                    data.MenuControls = 0;
                    // Mirror execute's typed-load of a REUSED params/menu asset (execute FAILs on a wrong-typed
                    // asset at the path; a fresh one is created empty and is always valid).
                    if (data.ParamsReused && AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(previewParamsPath) == null)
                    {
                        data.Result = "FAIL";
                        data.Error  = "asset at " + previewParamsPath + " exists but is not a VRCExpressionParameters";
                        return FinishEarly(data, label);
                    }
                    if (data.MenuReused && AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(previewMenuPath) == null)
                    {
                        data.Result = "FAIL";
                        data.Error  = "asset at " + previewMenuPath + " exists but is not a VRCExpressionsMenu";
                        return FinishEarly(data, label);
                    }

                    var previewDesc = ownedRoot.GetComponent<VRCAvatarDescriptor>()
                                   ?? ownedRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
                    if (previewDesc == null)
                    {
                        data.Result = "FAIL";
                        data.Error  = "VRCAvatarDescriptor not found on ownedRoot (" + ownedRoot.name + ")";
                        return FinishEarly(data, label);
                    }

                    // Slots we WOULD wire (reported under the (whatIf) marker): FX iff the descriptor
                    // has a base FX anim layer; params/menu would always be wired on execute.
                    bool previewHasFxLayer = false;
                    var previewBaseLayers = previewDesc.baseAnimationLayers;
                    if (previewBaseLayers != null)
                        foreach (var l in previewBaseLayers)
                            if (l.type == VRCAvatarDescriptor.AnimLayerType.FX) { previewHasFxLayer = true; break; }
                    data.FxWired     = previewHasFxLayer;
                    data.ParamsWired = true;
                    data.MenuWired   = true;
                    // Execute gates PASS on the FX base layer being wired (fxVerified); with no base FX layer it
                    // FAILs "descriptor FX not wired". The preview verdict must equal that, not a blanket PASS.
                    if (!previewHasFxLayer)
                    {
                        data.Result = "FAIL";
                        data.Error  = "descriptor FX not wired to clean controller (no base FX layer to wire)";
                        return FinishEarly(data, label);
                    }

                    data.Result = "PASS";
                    string previewLog = WriteRunLog(data, label);
                    return BuildSummary(data, label, previewLog);
                }

                // ── Ensure outDir exists ───────────────────────────────────────────────────
                TransplantCore.EnsureFolderExists(outDir);

                // ── Step 1: Clean FX controller → <name>_Clean.controller ─────────────────
                // Create-if-missing / reuse-if-present (preserve GUID). If absent, copy the source
                // and trim. If present, load the existing asset and re-trim its layers IN PLACE so
                // its GUID is preserved (any descriptor already pointing at it stays wired) and it
                // stays current with keepLayerNames. To force a fresh derive, the operator deletes
                // the asset manually.
                string sourceFxPath = AssetDatabase.GetAssetPath(sourceFx);
                if (string.IsNullOrEmpty(sourceFxPath))
                {
                    data.Result = "FAIL";
                    data.Error  = "sourceFx has no asset path (runtime/in-memory controller not supported)";
                    return FinishEarly(data, label);
                }

                string cleanName = sourceFx.name + "_Clean.controller";
                string destPath  = outDir.TrimEnd('/') + "/" + cleanName;

                // Existence test: LoadMainAssetAtPath (not AssetPathToGUID — that returns a stale
                // ghost GUID for a deleted-but-not-yet-reimported path, a false positive that would
                // wrongly take the reuse branch and load null).
                bool ctrlExists = AssetDatabase.LoadMainAssetAtPath(destPath) != null;
                if (!ctrlExists)
                {
                    bool copied = AssetDatabase.CopyAsset(sourceFxPath, destPath);
                    if (!copied)
                    {
                        data.Result = "FAIL";
                        data.Error  = "AssetDatabase.CopyAsset failed: " + sourceFxPath + " -> " + destPath;
                        return FinishEarly(data, label);
                    }
                }
                data.CleanFxReused = ctrlExists;

                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(destPath);
                if (ctrl == null)
                {
                    data.Result = "FAIL";
                    data.Error  = "LoadAssetAtPath returned null for " + destPath;
                    return FinishEarly(data, label);
                }
                data.CleanFxPath = destPath;

                // ── Step 2: Trim layers — keep base 0 + each named layer ─────────────────
                var allNames = new string[ctrl.layers.Length];
                for (int i = 0; i < ctrl.layers.Length; i++) allNames[i] = ctrl.layers[i].name;
                string selErr;
                int[] keepIdx = SelectLayersToKeep(allNames, keepLayerNames, out selErr);
                if (keepIdx == null) { data.Result = "FAIL"; data.Error = selErr; return FinishEarly(data, label); }
                var keepSet = new HashSet<int>(keepIdx);
                for (int i = ctrl.layers.Length - 1; i >= 0; i--)
                    if (!keepSet.Contains(i)) ctrl.RemoveLayer(i);   // descending; ctrl.layers recomputed each access

                data.LayerCount = ctrl.layers.Length;
                var names = new string[data.LayerCount];
                for (int i = 0; i < data.LayerCount; i++)
                    names[i] = ctrl.layers[i].name;
                data.LayerNames = string.Join("/", names);

                // ── Step 2b: prune the parameter list to kept-layer references ────────────
                // A dropped layer's parameters otherwise survive the copy forever (vendor
                // controllers carry dozens). Referenced = transition conditions, state
                // speed/time/mirror/cycle-offset bindings, blend-tree params, and VRC
                // parameter-driver reads/writes. Idempotent: a re-run drops nothing new.
                PruneParameters(ctrl, data);

                EditorUtility.SetDirty(ctrl);

                // ── Step 3: Empty VRCExpressionParameters ────────────────────────────────
                // Stable, label-free shared name + create-if-missing / reuse-if-present (preserve
                // GUID) so variations of one base share this asset. If present, reuse it; if it has
                // pre-existing content (something external wrote into it), ensure-empty IN PLACE —
                // visible note, still PASS. Never delete-recreate (that churns the GUID).
                string paramsPath = outDir.TrimEnd('/') + "/VRCExpressionParameters_Empty.asset";
                // Existence test via LoadMainAssetAtPath (see controller note: AssetPathToGUID can
                // return a stale ghost GUID for a deleted-but-not-reimported path).
                bool paramsExists = AssetDatabase.LoadMainAssetAtPath(paramsPath) != null;
                VRCExpressionParameters exprParams;
                if (paramsExists)
                {
                    exprParams = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(paramsPath);
                    if (exprParams == null)
                    {
                        data.Result = "FAIL";
                        data.Error  = "asset at " + paramsPath + " exists but is not a VRCExpressionParameters";
                        return FinishEarly(data, label);
                    }
                    int preCount = exprParams.parameters != null ? exprParams.parameters.Length : 0;
                    if (preCount > 0)
                    {
                        data.ReuseNote = AppendNote(data.ReuseNote,
                            "reused VRCExpressionParameters_Empty.asset had " + preCount + " pre-existing param(s) — cleared to empty");
                        exprParams.parameters = new VRCExpressionParameters.Parameter[0];
                        EditorUtility.SetDirty(exprParams);
                    }
                }
                else
                {
                    exprParams = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                    exprParams.parameters = new VRCExpressionParameters.Parameter[0];
                    AssetDatabase.CreateAsset(exprParams, paramsPath);
                }
                data.ParamsReused = paramsExists;
                data.ParamsPath   = paramsPath;
                data.ParamCount   = 0;

                // ── Step 4: Empty VRCExpressionsMenu ─────────────────────────────────────
                string menuPath = outDir.TrimEnd('/') + "/VRCExpressionsMenu_Empty.asset";
                bool menuExists = AssetDatabase.LoadMainAssetAtPath(menuPath) != null;
                VRCExpressionsMenu exprMenu;
                if (menuExists)
                {
                    exprMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(menuPath);
                    if (exprMenu == null)
                    {
                        data.Result = "FAIL";
                        data.Error  = "asset at " + menuPath + " exists but is not a VRCExpressionsMenu";
                        return FinishEarly(data, label);
                    }
                    int preCount = exprMenu.controls != null ? exprMenu.controls.Count : 0;
                    if (preCount > 0)
                    {
                        data.ReuseNote = AppendNote(data.ReuseNote,
                            "reused VRCExpressionsMenu_Empty.asset had " + preCount + " pre-existing control(s) — cleared to empty");
                        exprMenu.controls.Clear();
                        EditorUtility.SetDirty(exprMenu);
                    }
                    else if (exprMenu.controls == null)
                    {
                        exprMenu.controls = new List<VRCExpressionsMenu.Control>();
                    }
                }
                else
                {
                    exprMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    exprMenu.controls = new List<VRCExpressionsMenu.Control>();
                    AssetDatabase.CreateAsset(exprMenu, menuPath);
                }
                data.MenuReused   = menuExists;
                data.MenuPath     = menuPath;
                data.MenuControls = 0;

                // Persist the copied controller with trimmed layers
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // ── Step 5: Wire the descriptor ───────────────────────────────────────────
                var desc = ownedRoot.GetComponent<VRCAvatarDescriptor>()
                        ?? ownedRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
                if (desc == null)
                {
                    data.Result = "FAIL";
                    data.Error  = "VRCAvatarDescriptor not found on ownedRoot (" + ownedRoot.name + ")";
                    return FinishEarly(data, label);
                }

                Undo.RecordObject(desc, "CleanController: wire clean controller + empty expressions");

                desc.customizeAnimationLayers = true;

                // baseAnimationLayers is a struct array — must read-modify-write each element
                bool fxWired       = false;
                var  baseLayers    = desc.baseAnimationLayers;
                if (baseLayers != null)
                {
                    for (int i = 0; i < baseLayers.Length; i++)
                    {
                        if (baseLayers[i].type == VRCAvatarDescriptor.AnimLayerType.FX)
                        {
                            baseLayers[i].animatorController = ctrl;
                            baseLayers[i].isDefault          = false;
                            baseLayers[i].isEnabled          = true;
                            fxWired = true;
                        }
                    }
                    desc.baseAnimationLayers = baseLayers;
                }
                data.FxWired = fxWired;

                desc.customExpressions    = true;
                desc.expressionParameters = exprParams;
                desc.expressionsMenu      = exprMenu;

                data.ParamsWired = desc.expressionParameters == exprParams;
                data.MenuWired   = desc.expressionsMenu      == exprMenu;

                EditorUtility.SetDirty(desc);
                // Do NOT save the scene.

                // ── Step 6: Verify ────────────────────────────────────────────────────────
                // Re-read descriptor state for verification
                bool fxVerified = false;
                var  verifyLayers = desc.baseAnimationLayers;
                if (verifyLayers != null)
                {
                    foreach (var l in verifyLayers)
                    {
                        if (l.type == VRCAvatarDescriptor.AnimLayerType.FX
                            && l.animatorController == ctrl
                            && !l.isDefault
                            && l.isEnabled)
                        {
                            fxVerified = true;
                            break;
                        }
                    }
                }

                bool paramsVerified = desc.expressionParameters != null
                                   && desc.expressionParameters == exprParams
                                   && (desc.expressionParameters.parameters == null
                                       || desc.expressionParameters.parameters.Length == 0);
                bool menuVerified   = desc.expressionsMenu != null
                                   && desc.expressionsMenu == exprMenu
                                   && (desc.expressionsMenu.controls == null
                                       || desc.expressionsMenu.controls.Count == 0);

                // Re-scan the final controller by name — every requested layer must survive (no magic count).
                var finalNames = new HashSet<string>();
                foreach (var l in ctrl.layers) finalNames.Add(l.name);
                var missingRequested = new List<string>();
                foreach (var name in keepLayerNames ?? new string[0])
                    if (!finalNames.Contains(name)) missingRequested.Add(name);
                bool layersVerified = missingRequested.Count == 0;

                bool pass = layersVerified
                         && fxVerified
                         && paramsVerified
                         && menuVerified;

                data.Result = pass ? "PASS" : "FAIL";

                if (!pass && data.Error == null)
                {
                    var reasons = new List<string>();
                    if (!layersVerified)
                        reasons.Add("requested layer(s) missing from clean controller: " + string.Join(", ", missingRequested.ToArray()));
                    if (!fxVerified)
                        reasons.Add("descriptor FX not wired to clean controller (isDefault or wrong ref)");
                    if (!paramsVerified)
                        reasons.Add("expressionParameters not empty or not wired");
                    if (!menuVerified)
                        reasons.Add("expressionsMenu not empty or not wired");
                    data.Error = string.Join("; ", reasons);
                }
            }
            catch (Exception ex)
            {
                data.Result = "FAIL";
                data.Error  = ex.GetType().Name + ": " + ex.Message;
            }

            string logPath = WriteRunLog(data, label);
            return BuildSummary(data, label, logPath);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Indices to keep: always 0 (VRChat requires a base layer) plus each requested name. Returns null +
        /// sets <paramref name="error"/> when a requested name is absent or matches more than one layer
        /// (Animator layer names aren't unique — an ambiguous keep can't be trusted). Pure; unit-tested.
        /// </summary>
        public static int[] SelectLayersToKeep(string[] allLayerNames, string[] keepLayerNames, out string error)
        {
            error = null;
            var keep = new SortedSet<int> { 0 };
            foreach (var name in keepLayerNames ?? new string[0])
            {
                var hits = new List<int>();
                for (int i = 0; i < allLayerNames.Length; i++) if (allLayerNames[i] == name) hits.Add(i);
                if (hits.Count == 0) { error = "requested layer '" + name + "' absent from source FX"; return null; }
                if (hits.Count > 1)  { error = "requested layer '" + name + "' is ambiguous (" + hits.Count + " matches)"; return null; }
                keep.Add(hits[0]);
            }
            var arr = new int[keep.Count]; keep.CopyTo(arr); return arr;
        }

        /// <summary>Append a note to a "; "-joined note string (visible reuse/clear signals).</summary>
        private static string AppendNote(string existing, string note)
        {
            return string.IsNullOrEmpty(existing) ? note : existing + "; " + note;
        }

        // ── Parameter pruning ─────────────────────────────────────────────────────────────────

        private static void PruneParameters(AnimatorController ctrl, RunData data)
        {
            var referenced = CollectReferencedParameters(ctrl.layers, ctrl.layers);
            var kept = new List<AnimatorControllerParameter>();
            var dropped = new List<string>();
            foreach (var p in ctrl.parameters)
            {
                if (referenced.Contains(p.name)) kept.Add(p);
                else dropped.Add(p.name);
            }
            if (dropped.Count > 0) ctrl.parameters = kept.ToArray();
            data.CtrlParamsKept = kept.Count;
            data.CtrlParamsDropped = dropped.Count;
            data.CtrlParamsDroppedNames = string.Join("/", dropped.ToArray());
        }

        /// <summary>Every controller-parameter name the kept layers reference: transition
        /// conditions, state speed/time/mirror/cycle-offset bindings, blend-tree blend params
        /// (incl. direct-blend children), and VRC parameter-driver reads/writes. A synced layer
        /// (<c>syncedLayerIndex &gt;= 0</c>) has a null stateMachine — it borrows the source
        /// layer's structure and carries per-state override motions/behaviours — so it resolves
        /// against <paramref name="allLayers"/> (mirrors <c>AnimatorClipWalk</c>'s synced-layer
        /// handling). Pass the array the kept layers' indices refer to: the trimmed
        /// <c>ctrl.layers</c> on execute, the source controller's layers in the whatIf preview.</summary>
        internal static HashSet<string> CollectReferencedParameters(
            IEnumerable<AnimatorControllerLayer> keptLayers, AnimatorControllerLayer[] allLayers)
        {
            var used = new HashSet<string>();
            foreach (var layer in keptLayers)
            {
                if (layer == null) continue;
                if (layer.syncedLayerIndex >= 0)
                {
                    // Kept synced layer: params come from the source layer's structure plus this
                    // layer's per-state overrides. A dropped source layer is a broken sync however
                    // params are counted (CheckAnimator territory) — collect what still resolves.
                    if (allLayers != null && layer.syncedLayerIndex < allLayers.Length)
                    {
                        var src = allLayers[layer.syncedLayerIndex];
                        if (src != null && src.stateMachine != null)
                        {
                            WalkStateMachine(src.stateMachine, used);
                            WalkSyncedOverrides(layer, src.stateMachine, used);
                        }
                    }
                    continue;
                }
                if (layer.stateMachine != null)
                    WalkStateMachine(layer.stateMachine, used);
            }
            return used;
        }

        private static void WalkSyncedOverrides(AnimatorControllerLayer synced,
            AnimatorStateMachine srcSm, HashSet<string> used)
        {
            foreach (var cs in srcSm.states)
            {
                if (cs.state == null) continue;
                WalkMotion(synced.GetOverrideMotion(cs.state), used);
                AddBehaviourParams(synced.GetOverrideBehaviours(cs.state), used);
            }
            foreach (var child in srcSm.stateMachines)
                if (child.stateMachine != null)
                    WalkSyncedOverrides(synced, child.stateMachine, used);
        }

        private static void WalkStateMachine(AnimatorStateMachine sm, HashSet<string> used)
        {
            foreach (var t in sm.anyStateTransitions) AddConditions(t.conditions, used);
            foreach (var t in sm.entryTransitions) AddConditions(t.conditions, used);
            AddBehaviourParams(sm.behaviours, used);
            foreach (var cs in sm.states)
            {
                var st = cs.state;
                if (st == null) continue;
                foreach (var t in st.transitions) AddConditions(t.conditions, used);
                if (st.speedParameterActive && !string.IsNullOrEmpty(st.speedParameter)) used.Add(st.speedParameter);
                if (st.timeParameterActive && !string.IsNullOrEmpty(st.timeParameter)) used.Add(st.timeParameter);
                if (st.mirrorParameterActive && !string.IsNullOrEmpty(st.mirrorParameter)) used.Add(st.mirrorParameter);
                if (st.cycleOffsetParameterActive && !string.IsNullOrEmpty(st.cycleOffsetParameter)) used.Add(st.cycleOffsetParameter);
                AddBehaviourParams(st.behaviours, used);
                WalkMotion(st.motion, used);
            }
            foreach (var child in sm.stateMachines)
            {
                if (child.stateMachine == null) continue;
                foreach (var t in sm.GetStateMachineTransitions(child.stateMachine))
                    AddConditions(t.conditions, used);
                WalkStateMachine(child.stateMachine, used);
            }
        }

        private static void WalkMotion(Motion m, HashSet<string> used)
        {
            var bt = m as BlendTree;
            if (bt == null) return;
            if (bt.blendType != BlendTreeType.Direct && !string.IsNullOrEmpty(bt.blendParameter))
                used.Add(bt.blendParameter);
            bool is2D = bt.blendType == BlendTreeType.SimpleDirectional2D
                     || bt.blendType == BlendTreeType.FreeformDirectional2D
                     || bt.blendType == BlendTreeType.FreeformCartesian2D;
            if (is2D && !string.IsNullOrEmpty(bt.blendParameterY)) used.Add(bt.blendParameterY);
            foreach (var child in bt.children)
            {
                if (bt.blendType == BlendTreeType.Direct && !string.IsNullOrEmpty(child.directBlendParameter))
                    used.Add(child.directBlendParameter);
                WalkMotion(child.motion, used);
            }
        }

        private static void AddConditions(AnimatorCondition[] conditions, HashSet<string> used)
        {
            if (conditions == null) return;
            foreach (var c in conditions)
                if (!string.IsNullOrEmpty(c.parameter)) used.Add(c.parameter);
        }

        private static void AddBehaviourParams(StateMachineBehaviour[] behaviours, HashSet<string> used)
        {
            if (behaviours == null) return;
            foreach (var b in behaviours)
            {
                var driver = b as VRC.SDKBase.VRC_AvatarParameterDriver;
                if (driver == null || driver.parameters == null) continue;
                foreach (var p in driver.parameters)
                {
                    if (!string.IsNullOrEmpty(p.name)) used.Add(p.name);
                    if (!string.IsNullOrEmpty(p.source)) used.Add(p.source); // Copy op reads source
                }
            }
        }

        /// <summary>Write the RunLog and return the error summary line. BuildSummary already
        /// Debug.LogErrors any non-PASS result, so this path must NOT log again — doing so
        /// double-logged every early-abort FAIL (the end-of-Run FAIL path logs once via BuildSummary).</summary>
        private static string FinishEarly(RunData data, string label)
        {
            string logPath = WriteRunLog(data, label);
            return BuildSummary(data, label, logPath);
        }

        private static string BuildSummary(RunData data, string label, string logPath)
        {
            // descriptorWired segment: show FX|params|menu with (MISSING) on anything unwired
            string wiredFx     = data.FxWired     ? "FX"           : "FX(MISSING)";
            string wiredParams = data.ParamsWired  ? "|params"      : "|params(MISSING)";
            string wiredMenu   = data.MenuWired    ? "|menu"        : "|menu(MISSING)";
            string wired       = wiredFx + wiredParams + wiredMenu;

            string extra = data.Error != null ? " error=" + data.Error : "";
            if (data.ReuseNote != null) extra += " note=" + data.ReuseNote;

            // reuse markers: (reuse) when we loaded an existing asset, (new) when we created it
            string ctrlTag   = data.CleanFxReused ? "(reuse)" : "(new)";
            string paramsTag = data.ParamsReused  ? "(reuse)" : "(new)";
            string menuTag   = data.MenuReused    ? "(reuse)" : "(new)";

            string marker = data.WhatIf ? " (whatIf)" : "";
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CleanController]" + marker + " {0}: controller={1}{2}(layers={3}: {4}; ctrlParams kept={5} dropped={6}), exprParams={7}{8}, menu={9}{10}, descriptorWired={11}{12} => {13} | log={14}",
                label,
                (data.SourceFxName ?? "?") + "_Clean",
                ctrlTag,
                data.LayerCount,
                data.LayerNames  ?? "?",
                data.CtrlParamsKept,
                data.CtrlParamsDropped,
                data.ParamCount,
                paramsTag,
                data.MenuControls,
                menuTag,
                wired,
                extra,
                data.Result ?? "FAIL",
                logPath);

            if (data.Result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        // ── RunLog ────────────────────────────────────────────────────────────────────────────

        private static string WriteRunLog(RunData data, string label)
        {
            Directory.CreateDirectory(TransplantCore.RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"clean-controller\",\n");
            sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(data.WhatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"instance\": ").Append(TransplantCore.Q(data.InstanceName)).Append(",\n");
            sb.Append("  \"sourceFx\": ").Append(TransplantCore.Q(data.SourceFxName)).Append(",\n");
            sb.Append("  \"outDir\": ").Append(TransplantCore.Q(data.OutDir)).Append(",\n");
            sb.Append("  \"cleanFxPath\": ").Append(TransplantCore.Q(data.CleanFxPath)).Append(",\n");
            sb.Append("  \"cleanFxReused\": ").Append(data.CleanFxReused ? "true" : "false").Append(",\n");
            sb.Append("  \"layerCount\": ").Append(data.LayerCount).Append(",\n");
            sb.Append("  \"layerNames\": ").Append(TransplantCore.Q(data.LayerNames)).Append(",\n");
            sb.Append("  \"ctrlParamsKept\": ").Append(data.CtrlParamsKept).Append(",\n");
            sb.Append("  \"ctrlParamsDropped\": ").Append(data.CtrlParamsDropped).Append(",\n");
            sb.Append("  \"ctrlParamsDroppedNames\": ").Append(TransplantCore.Q(data.CtrlParamsDroppedNames)).Append(",\n");
            sb.Append("  \"paramsPath\": ").Append(TransplantCore.Q(data.ParamsPath)).Append(",\n");
            sb.Append("  \"paramsReused\": ").Append(data.ParamsReused ? "true" : "false").Append(",\n");
            sb.Append("  \"paramCount\": ").Append(data.ParamCount).Append(",\n");
            sb.Append("  \"menuPath\": ").Append(TransplantCore.Q(data.MenuPath)).Append(",\n");
            sb.Append("  \"menuReused\": ").Append(data.MenuReused ? "true" : "false").Append(",\n");
            sb.Append("  \"menuControls\": ").Append(data.MenuControls).Append(",\n");
            sb.Append("  \"reuseNote\": ").Append(TransplantCore.Q(data.ReuseNote)).Append(",\n");
            sb.Append("  \"fxWired\": ").Append(data.FxWired     ? "true" : "false").Append(",\n");
            sb.Append("  \"paramsWired\": ").Append(data.ParamsWired ? "true" : "false").Append(",\n");
            sb.Append("  \"menuWired\": ").Append(data.MenuWired   ? "true" : "false").Append(",\n");
            sb.Append("  \"result\": ").Append(TransplantCore.Q(data.Result)).Append(",\n");
            sb.Append("  \"error\": ").Append(TransplantCore.Q(data.Error)).Append("\n");
            sb.Append("}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path  = TransplantCore.RunLogDir + "/clean-controller_" + label + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        // ── Data type ─────────────────────────────────────────────────────────────────────────

        private class RunData
        {
            public string InstanceName;
            public string SourceFxName;
            public string OutDir;
            public bool   WhatIf;
            public string CleanFxPath;
            public bool   CleanFxReused;
            public int    LayerCount;
            public string LayerNames;
            public int    CtrlParamsKept;
            public int    CtrlParamsDropped;
            public string CtrlParamsDroppedNames;
            public string ParamsPath;
            public bool   ParamsReused;
            public int    ParamCount;
            public string MenuPath;
            public bool   MenuReused;
            public int    MenuControls;
            public string ReuseNote;
            public bool   FxWired;
            public bool   ParamsWired;
            public bool   MenuWired;
            public string Result;
            public string Error;
        }
    }
}
