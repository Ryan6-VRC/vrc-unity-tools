using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Resets an owned avatar's FX to a controller trimmed to the named layers, with empty expression
    /// params and menu, wired into its VRCAvatarDescriptor. Contract: docs/animator.md.
    /// A reused params or menu asset that something external wrote into is emptied in place (RunLog note,
    /// still PASS). To force a fresh derive of the controller, the operator deletes the asset manually.
    /// </summary>
    [AgentTool]
    public static class CleanController
    {
        public static string Run(AnimatorController sourceFx, GameObject ownedRoot, string outDir, string[] keepLayerNames, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";

            if (sourceFx == null)          return ArgFail(label, whatIf, sourceFx, ownedRoot, "sourceFx is null");
            if (ownedRoot == null)         return ArgFail(label, whatIf, sourceFx, ownedRoot, "ownedRoot is null");
            if (string.IsNullOrEmpty(outDir)) return ArgFail(label, whatIf, sourceFx, ownedRoot, "outDir is null or empty");

            var data = new RunData
            {
                source   = sourceFx.name,
                instance = ownedRoot.name,
                whatIf   = whatIf,
            };
            data.Note("outDir=" + outDir);

            try
            {
                // whatIf: report-only preview. Its verdict must equal execute's, so every execute-time
                // FAIL checkable without writing is mirrored in this block.
                if (whatIf)
                {
                    string previewSourceFxPath = AssetDatabase.GetAssetPath(sourceFx);
                    if (string.IsNullOrEmpty(previewSourceFxPath))
                    {
                        return Fail(data, label, "sourceFx has no asset path (runtime/in-memory controller not supported)");
                    }

                    string previewDestPath = outDir.TrimEnd('/') + "/" + sourceFx.name + "_Clean.controller";
                    data.CleanFxReused = AssetDatabase.LoadMainAssetAtPath(previewDestPath) != null;
                    data.CleanFxPath   = previewDestPath;
                    if (data.CleanFxReused && AssetDatabase.LoadAssetAtPath<AnimatorController>(previewDestPath) == null)
                    {
                        return Fail(data, label, "asset at " + previewDestPath + " exists but is not an AnimatorController");
                    }

                    // Reads the SOURCE's layers where execute reads a reused clean controller's: a name an
                    // earlier run trimmed passes here and FAILs on execute.
                    var srcLayers = sourceFx.layers;
                    var previewAllNames = new string[srcLayers.Length];
                    for (int i = 0; i < srcLayers.Length; i++) previewAllNames[i] = srcLayers[i].name;
                    string previewSelErr;
                    int[] previewKeepIdx = SelectLayersToKeep(previewAllNames, keepLayerNames, out previewSelErr);
                    if (previewKeepIdx == null)
                    {
                        return Fail(data, label, previewSelErr);
                    }
                    var previewKeptNames = new string[previewKeepIdx.Length];
                    for (int i = 0; i < previewKeepIdx.Length; i++) previewKeptNames[i] = srcLayers[previewKeepIdx[i]].name;
                    data.LayerCount = previewKeepIdx.Length;
                    data.LayerNames = string.Join("/", previewKeptNames);

                    // Counts are relative to the SOURCE controller; on a reused (already-pruned)
                    // clean controller the execute-time drop count lands lower.
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
                    if (data.ParamsReused && AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(previewParamsPath) == null)
                    {
                        return Fail(data, label, "asset at " + previewParamsPath + " exists but is not a VRCExpressionParameters");
                    }
                    if (data.MenuReused && AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(previewMenuPath) == null)
                    {
                        return Fail(data, label, "asset at " + previewMenuPath + " exists but is not a VRCExpressionsMenu");
                    }

                    var previewDesc = ownedRoot.GetComponent<VRCAvatarDescriptor>()
                                   ?? ownedRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
                    if (previewDesc == null)
                    {
                        return Fail(data, label, "VRCAvatarDescriptor not found on ownedRoot (" + ownedRoot.name + ")");
                    }

                    bool previewHasFxLayer = false;
                    var previewBaseLayers = previewDesc.baseAnimationLayers;
                    if (previewBaseLayers != null)
                        foreach (var l in previewBaseLayers)
                            if (l.type == VRCAvatarDescriptor.AnimLayerType.FX) { previewHasFxLayer = true; break; }
                    data.FxWired     = previewHasFxLayer;
                    data.ParamsWired = true;
                    data.MenuWired   = true;
                    if (!previewHasFxLayer)
                    {
                        return Fail(data, label, "descriptor FX not wired to clean controller (no base FX layer to wire)");
                    }

                    data.result = "PASS";
                    return FinishFull(data, label);
                }

                TransplantCore.EnsureFolderExists(outDir);

                // ── Step 1: Clean FX controller → <name>_Clean.controller ─────────────────
                string sourceFxPath = AssetDatabase.GetAssetPath(sourceFx);
                if (string.IsNullOrEmpty(sourceFxPath))
                {
                    return Fail(data, label, "sourceFx has no asset path (runtime/in-memory controller not supported)");
                }

                string cleanName = sourceFx.name + "_Clean.controller";
                string destPath  = outDir.TrimEnd('/') + "/" + cleanName;

                // Not AssetPathToGUID: it returns a stale ghost GUID for a deleted-but-not-yet-reimported
                // path, which would wrongly take the reuse branch and load null.
                bool ctrlExists = AssetDatabase.LoadMainAssetAtPath(destPath) != null;
                if (!ctrlExists)
                {
                    bool copied = TransplantCore.CopyAssetFile(sourceFxPath, destPath);
                    if (!copied)
                    {
                        return Fail(data, label, "asset copy failed: " + sourceFxPath + " -> " + destPath);
                    }
                }
                data.CleanFxReused = ctrlExists;

                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(destPath);
                if (ctrl == null)
                {
                    return Fail(data, label, "LoadAssetAtPath returned null for " + destPath);
                }
                data.CleanFxPath = destPath;

                // ── Step 2: Trim layers — keep base 0 + each named layer ─────────────────
                var allNames = new string[ctrl.layers.Length];
                for (int i = 0; i < ctrl.layers.Length; i++) allNames[i] = ctrl.layers[i].name;
                string selErr;
                int[] keepIdx = SelectLayersToKeep(allNames, keepLayerNames, out selErr);
                if (keepIdx == null) return Fail(data, label, selErr);
                var keepSet = new HashSet<int>(keepIdx);
                for (int i = ctrl.layers.Length - 1; i >= 0; i--)
                    if (!keepSet.Contains(i)) ctrl.RemoveLayer(i);

                data.LayerCount = ctrl.layers.Length;
                var names = new string[data.LayerCount];
                for (int i = 0; i < data.LayerCount; i++)
                    names[i] = ctrl.layers[i].name;
                data.LayerNames = string.Join("/", names);

                // ── Step 2b: prune the parameter list to kept-layer references ────────────
                PruneParameters(ctrl, data);

                EditorUtility.SetDirty(ctrl);

                // ── Step 3: Empty VRCExpressionParameters ────────────────────────────────
                string paramsPath = outDir.TrimEnd('/') + "/VRCExpressionParameters_Empty.asset";
                bool paramsExists = AssetDatabase.LoadMainAssetAtPath(paramsPath) != null;
                VRCExpressionParameters exprParams;
                if (paramsExists)
                {
                    exprParams = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(paramsPath);
                    if (exprParams == null)
                    {
                        return Fail(data, label, "asset at " + paramsPath + " exists but is not a VRCExpressionParameters");
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
                        return Fail(data, label, "asset at " + menuPath + " exists but is not a VRCExpressionsMenu");
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

                AssetDatabase.SaveAssetIfDirty(ctrl);
                AssetDatabase.SaveAssetIfDirty(exprParams);
                AssetDatabase.SaveAssetIfDirty(exprMenu);

                // ── Step 5: Wire the descriptor ───────────────────────────────────────────
                var desc = ownedRoot.GetComponent<VRCAvatarDescriptor>()
                        ?? ownedRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
                if (desc == null)
                {
                    return Fail(data, label, "VRCAvatarDescriptor not found on ownedRoot (" + ownedRoot.name + ")");
                }

                Undo.RecordObject(desc, "CleanController: wire clean controller + empty expressions");

                desc.customizeAnimationLayers = true;

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

                data.result = pass ? "PASS" : "FAIL";

                if (!pass && data.error == null)
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
                    foreach (var r in reasons) data.Offender(r);
                    data.error = string.Join("; ", reasons);
                }
            }
            catch (Exception ex)
            {
                data.result = "FAIL";
                data.error  = ex.GetType().Name + ": " + ex.Message;
            }

            return FinishFull(data, label);
        }

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

        private static string AppendNote(string existing, string note)
        {
            return string.IsNullOrEmpty(existing) ? note : existing + "; " + note;
        }

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

        /// <summary>A synced layer (<c>syncedLayerIndex &gt;= 0</c>) has a null stateMachine: it borrows
        /// the source layer's structure and carries per-state override motions and behaviours, so it
        /// resolves against <paramref name="allLayers"/>: the trimmed <c>ctrl.layers</c> on execute, the
        /// source controller's layers in the whatIf preview.</summary>
        internal static HashSet<string> CollectReferencedParameters(
            IEnumerable<AnimatorControllerLayer> keptLayers, AnimatorControllerLayer[] allLayers)
        {
            var used = new HashSet<string>();
            foreach (var layer in keptLayers)
            {
                if (layer == null) continue;
                if (layer.syncedLayerIndex >= 0)
                {
                    // A dropped source layer is a broken sync however params are counted; collect what
                    // still resolves.
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
#if VRC_FT_SETS_PRESENT
                if (b is VRC.SDKBase.VRC_AnimatorFaceTrackingBlendshapesSet ft)
                {
                    if (ft.blendSets != null)
                        foreach (var e in ft.blendSets)
                            if (e != null && e.type == VRC.SDKBase.VRC_AnimatorFaceTrackingBlendshapesSet.SourceType.Parameter
                                && !string.IsNullOrEmpty(e.sourceParameter))
                                used.Add(e.sourceParameter);
                    continue;
                }
#endif
                var driver = b as VRC.SDKBase.VRC_AvatarParameterDriver;
                if (driver == null || driver.parameters == null) continue;
                foreach (var p in driver.parameters)
                {
                    if (!string.IsNullOrEmpty(p.name)) used.Add(p.name);
                    if (!string.IsNullOrEmpty(p.source)) used.Add(p.source); // Copy op reads source
                }
            }
        }

        private static string ArgFail(string label, bool whatIf, AnimatorController sourceFx, GameObject ownedRoot, string msg)
        {
            var data = new RunData
            {
                source   = sourceFx != null ? sourceFx.name : null,
                instance = ownedRoot != null ? ownedRoot.name : null,
                whatIf   = whatIf,
            };
            data.result = "FAIL";
            data.error  = msg;
            data.Offender(msg);
            return TransplantCore.Finish(data, label);
        }

        /// <summary>Early-abort tail, on the standard envelope summary: the bespoke wiring summary would
        /// render zero counts as measurements when nothing completed.</summary>
        private static string FinishEarly(RunData data, string label)
        {
            data.Offender(data.error);
            FlushPathNotes(data);
            if (data.ReuseNote != null) data.Note(data.ReuseNote);
            return TransplantCore.Finish(data, label);
        }

        private static string Fail(RunData data, string label, string msg)
        {
            data.result = "FAIL";
            data.error  = msg;
            return FinishEarly(data, label);
        }

        private static string FinishFull(RunData data, string label)
        {
            data.Count("layers", data.LayerCount);
            data.Count("ctrlParamsKept", data.CtrlParamsKept);
            data.Count("ctrlParamsDropped", data.CtrlParamsDropped);
            data.Count("exprParams", data.ParamCount);
            data.Count("menuControls", data.MenuControls);
            FlushPathNotes(data);
            if (!string.IsNullOrEmpty(data.LayerNames))
                data.Note("layers kept: " + data.LayerNames);
            if (!string.IsNullOrEmpty(data.CtrlParamsDroppedNames))
                data.Note("ctrlParams dropped: " + data.CtrlParamsDroppedNames);
            data.Note("descriptorWired=" + WiredString(data));
            if (data.ReuseNote != null) data.Note(data.ReuseNote);

            data.EnsureFailHasOffender();
            string logPath = TransplantCore.WriteRunLog(data, label);
            return BuildSummary(data, label, logPath);
        }

        /// <summary>Shared by both tails: a mid-flow FAIL can leave a created or trimmed controller behind,
        /// and its artifact must say so.</summary>
        private static void FlushPathNotes(RunData data)
        {
            if (data.CleanFxPath != null)
                data.Note("controller=" + data.CleanFxPath + (data.CleanFxReused ? " (reuse)" : " (new)"));
            if (data.ParamsPath != null)
                data.Note("params=" + data.ParamsPath + (data.ParamsReused ? " (reuse)" : " (new)"));
            if (data.MenuPath != null)
                data.Note("menu=" + data.MenuPath + (data.MenuReused ? " (reuse)" : " (new)"));
        }

        private static string WiredString(RunData data)
        {
            return (data.FxWired     ? "FX"      : "FX(MISSING)")
                 + (data.ParamsWired ? "|params" : "|params(MISSING)")
                 + (data.MenuWired   ? "|menu"   : "|menu(MISSING)");
        }

        private static string BuildSummary(RunData data, string label, string logPath)
        {
            string wired = WiredString(data);

            string extra = data.error != null ? " error=" + data.error : "";
            if (data.ReuseNote != null) extra += " note=" + data.ReuseNote;

            string ctrlTag   = data.CleanFxReused ? "(reuse)" : "(new)";
            string paramsTag = data.ParamsReused  ? "(reuse)" : "(new)";
            string menuTag   = data.MenuReused    ? "(reuse)" : "(new)";

            // The tag is the envelope kind because guard and early-abort paths emit the envelope's standard
            // line; a per-path tag would make transcript greps miss exactly the FAILs.
            string marker = data.whatIf ? " (whatIf)" : "";
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[clean-controller]" + marker + " {0}: controller={1}{2}(layers={3}: {4}; ctrlParams kept={5} dropped={6}), exprParams={7}{8}, menu={9}{10}, descriptorWired={11}{12} => {13} | log={14}",
                label,
                (data.source ?? "?") + "_Clean",
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
                data.result ?? "FAIL",
                logPath);

            if (data.result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        /// <summary>Fields, not direct <c>Count</c>/<c>Note</c> calls, because the bespoke wiring summary
        /// renders them structurally; <see cref="FinishFull"/> flushes them into the envelope.</summary>
        private sealed class RunData : RunLog
        {
            public RunData() : base("clean-controller") { }

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
        }
    }
}
