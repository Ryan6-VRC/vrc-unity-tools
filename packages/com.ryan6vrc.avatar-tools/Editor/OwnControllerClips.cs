using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Closes the CleanController gap: CleanController copies the CONTROLLER (GUID-stable) but its states still reference
    /// VENDOR clips by GUID. <see cref="OwnControllerClips"/> materializes owned <c>.anim</c> copies of the
    /// referenced clips and RETARGETS the controller's motion refs to them — across every motion slot:
    /// states (recursing sub-state-machines at any depth), <see cref="BlendTree"/> children (recursing
    /// nested trees), AND synced-layer per-state override motions
    /// (<see cref="AnimatorControllerLayer.GetOverrideMotion"/> / <c>SetOverrideMotion</c>). With
    /// <c>scope=All</c> it also forks your OWN gimmick's clips.
    ///
    /// Copy is absent-only (existence via <see cref="AssetDatabase.LoadMainAssetAtPath"/>): an absent owned
    /// copy triggers <see cref="AssetDatabase.CopyAsset"/> (GUID-stable, like CleanController); an EXISTING owned copy
    /// is reused as-is and never re-copied (re-copying would clobber the owner's hand-edits). Idempotent
    /// re-runs (a re-run with every copy present + every ref retargeted is PASS with 0 copies / 0 retargets).
    /// After retargeting, a post-condition re-scans every motion slot and FAILs loud naming any in-scope clip
    /// still referenced (closing the copy-set vs. retarget-walk asymmetry). This tool MUTATES the controller.
    ///
    /// Chains into <see cref="RepathClips"/> (UC2 = OwnControllerClips → RepathClips). Call
    /// <see cref="Run"/> from MCP execute_code.
    ///
    /// <para><b>Frame boundary — the CALLER owns frame-correctness.</b> Like <see cref="RepathClips"/>, this
    /// operates on ONE controller and knows nothing of the avatar. Copying + retargeting a controller's clip
    /// refs is frame-agnostic (it repoints motion slots, not binding paths), but the caller composing an
    /// avatar must still enumerate every animator itself — descriptor layers, Modular Avatar MergeAnimator
    /// (its own <c>pathMode</c>/<c>relativePathRoot</c>), and VRCFury FullControllers (their own mount-relative
    /// rewrite settings) — and own each one deliberately; there is no whole-avatar sweep here.</para>
    /// </summary>
    [AgentTool]
    public static class OwnControllerClips
    {
        /// <summary><c>VendorOnly</c> = copy only clips under Vendor/Packages (default);
        /// <c>All</c> = copy every referenced clip (fork-my-own-gimmick).</summary>
        public enum Scope { VendorOnly, All }

        /// <summary>
        /// Materialize owned copies of <paramref name="controller"/>'s in-scope clips under
        /// <paramref name="outDir"/> and retarget every motion slot to them. Returns a one-line PASS/FAIL
        /// summary ending with the RunLog path (<c>… => RESULT | log=&lt;path&gt;</c>).
        /// </summary>
        public static string Run(AnimatorController controller, string outDir, Scope scope = Scope.VendorOnly,
                                 bool force = false, bool whatIf = false)
        {
            if (controller == null) return ArgFail("null-controller", "controller is null");
            if (string.IsNullOrEmpty(outDir))
                return ArgFail(TransplantCore.Sanitize(controller.name), "outDir is null or empty");

            string controllerPath = AssetDatabase.GetAssetPath(controller);
            var log = new TransplantRunLog("own-controller-clips")
            {
                whatIf = whatIf,
                instance = controller.name,
                source = controllerPath,
            };
            string label = TransplantCore.Sanitize(controller.name);

            try
            {
                // ── Controller-writable guard (retarget mutates the controller asset) ──
                if (!TransplantCore.IsWritableAsset(controllerPath))
                {
                    if (force) log.Note("read-only controller override (force): " + controllerPath);
                    else
                    {
                        log.Offender("controller '" + TransplantCore.Leaf(controllerPath) + "' (" + controllerPath +
                            ") is read-only: copy it to an owned path first, or pass force=true");
                        log.result = "FAIL";
                        log.error = "read-only controller (pass force=true to override)";
                        return TransplantCore.Finish(log, label);
                    }
                }

                // ── outDir ownership guard: never write owned copies into read-only territory
                //    (Assets/Vendor or Packages) — the "never alter vendor assets" invariant ──
                string outClean = outDir.Replace('\\', '/').TrimEnd('/');
                if (!TransplantCore.IsWritableAsset(outClean))
                {
                    if (force) log.Note("read-only outDir override (force): " + outClean);
                    else
                    {
                        log.Offender("outDir '" + outClean +
                            "' is read-only (under Assets/Vendor or Packages): choose an owned output folder, or pass force=true");
                        log.result = "FAIL";
                        log.error = "read-only outDir (pass force=true to override)";
                        return TransplantCore.Finish(log, label);
                    }
                }

                // ── Deduped referenced clip set from the shared motion-slot walk (one grammar; not
                //    animationClips — so copy-set, retarget walk, and residual scan are self-consistent) ──
                var referenced = AnimatorClipWalk.CollectClips(controller);
                log.Count("clipsReferenced", referenced.Count);

                var inScope = new List<AnimationClip>();
                foreach (var clip in referenced)
                {
                    string p = AssetDatabase.GetAssetPath(clip);
                    bool vendor = !TransplantCore.IsWritableAsset(p);
                    if (scope == Scope.VendorOnly && !vendor) continue;
                    inScope.Add(clip);
                }
                log.Count("clipsInScope", inScope.Count);

                // A clip that is a SUB-ASSET (embedded in a controller/fbx) cannot be materialized as a
                // standalone .anim via CopyAsset — fail loud rather than copy the wrong container.
                foreach (var clip in inScope)
                {
                    if (!AssetDatabase.IsMainAsset(clip))
                        log.Offender("clip '" + clip.name + "' (" + AssetDatabase.GetAssetPath(clip) +
                            ") is a sub-asset, not a standalone .anim — cannot materialize an owned copy");
                }
                if (log.offenders.Count > 0)
                {
                    log.result = "FAIL";
                    log.error = "in-scope clip is a sub-asset";
                    return TransplantCore.Finish(log, label);
                }

                // ── O4 target-path plan: leaf collision-grouped, deterministic + order-independent ──
                var leafGroups = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var clip in inScope)
                {
                    string leaf = TransplantCore.Sanitize(clip.name);
                    int n; leafGroups.TryGetValue(leaf, out n); leafGroups[leaf] = n + 1;
                }
                var targetPath = new Dictionary<AnimationClip, string>();
                foreach (var clip in inScope)
                {
                    string leaf = TransplantCore.Sanitize(clip.name);
                    string fname = leaf;
                    if (leafGroups[leaf] > 1)
                    {
                        string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(clip));
                        fname = leaf + "__" + (guid.Length >= 8 ? guid.Substring(0, 8) : guid);
                    }
                    targetPath[clip] = outClean + "/" + fname + ".anim";
                }

                // A clip already living under outDir is ALREADY owned — never re-copy or retarget it (this,
                // not exact target-path equality, is what keeps scope=All re-runs idempotent: a re-run's
                // recomputed collision suffix can differ from the copy's, but the copy is still under outDir).
                System.Func<AnimationClip, bool> alreadyOwned = (c) =>
                    AssetDatabase.GetAssetPath(c).Replace('\\', '/').StartsWith(outClean + "/", StringComparison.Ordinal);

                // ── whatIf: report would-copy / would-reuse / would-retarget; mutate nothing ──
                if (whatIf)
                {
                    int wouldCopy = 0, wouldReuse = 0;
                    var wouldOwn = new HashSet<AnimationClip>();
                    foreach (var clip in inScope)
                    {
                        if (alreadyOwned(clip)) { wouldReuse++; continue; }
                        wouldOwn.Add(clip);
                        if (AssetDatabase.LoadMainAssetAtPath(targetPath[clip]) != null) wouldReuse++;
                        else wouldCopy++;
                    }
                    int wouldRetarget = CountReferencingSlots(controller, wouldOwn);
                    log.Count("copiesMade", wouldCopy);
                    log.Count("copiesReused", wouldReuse);
                    log.Count("retargets", wouldRetarget);
                    foreach (var clip in wouldOwn)
                        log.Note(clip.name + " -> " + targetPath[clip]);
                    log.result = "PASS";
                    return TransplantCore.Finish(log, label);
                }

                // ── Copy (absent-only) and build source → owned map (already-owned clips excluded) ──
                var map = new Dictionary<AnimationClip, AnimationClip>();
                int copiesMade = 0, copiesReused = 0;
                var toOwn = new List<AnimationClip>();
                foreach (var clip in inScope)
                {
                    if (alreadyOwned(clip)) { copiesReused++; continue; }
                    toOwn.Add(clip);
                }
                if (toOwn.Count > 0) EnsureFolderExists(outClean);
                foreach (var clip in toOwn)
                {
                    string src = AssetDatabase.GetAssetPath(clip);
                    string dst = targetPath[clip];
                    bool exists = AssetDatabase.LoadMainAssetAtPath(dst) != null;
                    if (!exists)
                    {
                        if (!AssetDatabase.CopyAsset(src, dst))
                        {
                            log.Offender("CopyAsset failed: " + src + " -> " + dst);
                            continue;
                        }
                        copiesMade++;
                    }
                    else copiesReused++;
                    var owned = AssetDatabase.LoadAssetAtPath<AnimationClip>(dst);
                    if (owned == null) { log.Offender("owned copy load failed (or wrong type): " + dst); continue; }
                    // Blind-reuse guard: a PRE-EXISTING target with the same leaf could be a foreign clip or a
                    // hand-edited copy of a different source. Compare binding-sets; on a structural mismatch
                    // WARN (still PASS) and reuse the existing clip — never overwrite (idempotent; preserves
                    // hand-edits). A structural match is a silent, expected reuse.
                    if (exists && !BindingSetsMatch(clip, owned))
                        log.Warning("'" + TransplantCore.Leaf(dst) + "' already in outDir differs from source '" +
                            clip.name + "' — hand-edit or foreign collision; reusing existing (not overwritten)");
                    map[clip] = owned;
                }
                if (log.offenders.Count > 0)
                {
                    log.result = "FAIL";
                    log.error = "clip copy failure";
                    return TransplantCore.Finish(log, label);
                }
                log.Count("copiesMade", copiesMade);
                log.Count("copiesReused", copiesReused);

                // ── Retarget every motion slot ──
                int retargets = RetargetAll(controller, map);
                log.Count("retargets", retargets);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();

                // ── Post-condition, DISK-TRUTHFUL: reimport the controller (a force-override write onto an
                //    immutable controller silently no-ops — SaveAssets can't persist it), then re-scan every
                //    motion slot on the reloaded asset. FAIL naming any in-scope clip still referenced (a
                //    residual means the walk missed a slot OR the write did not land). ──
                var scanTarget = controller;
                if (!string.IsNullOrEmpty(controllerPath))
                {
                    AssetDatabase.ImportAsset(controllerPath, ImportAssetOptions.ForceUpdate);
                    var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
                    if (reloaded != null) scanTarget = reloaded;
                }
                var residual = new List<string>();
                ScanResidual(scanTarget, new HashSet<AnimationClip>(map.Keys), residual);
                foreach (var r in residual) log.Offender("residual in-scope clip after retarget: " + r);
                log.Count("residual", residual.Count);

                log.result = log.offenders.Count > 0 ? "FAIL" : "PASS";
                if (log.result == "FAIL") log.error = "retarget missed a slot or write did not land (post-condition)";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error = ex.GetType().Name + ": " + ex.Message;
            }

            return TransplantCore.Finish(log, label);
        }

        // ── Motion-slot walk (retarget) ──────────────────────────────────────────────────────────────

        /// <summary>Retarget every motion slot whose motion is a map key to its owned copy; returns the
        /// retarget count. Walks states (recursing sub-state-machines at any depth), blend-tree children
        /// (recursing nested trees), and synced-layer per-state override motions.</summary>
        static int RetargetAll(AnimatorController controller, Dictionary<AnimationClip, AnimationClip> map)
        {
            int[] count = new int[1];

            System.Action<BlendTree> retargetBt = null;
            retargetBt = (bt) =>
            {
                var kids = bt.children;
                bool changed = false;
                for (int i = 0; i < kids.Length; i++)
                {
                    var clip = kids[i].motion as AnimationClip;
                    if (clip != null && map.ContainsKey(clip)) { kids[i].motion = map[clip]; changed = true; count[0]++; }
                    else { var cbt = kids[i].motion as BlendTree; if (cbt != null) retargetBt(cbt); }
                }
                if (changed) bt.children = kids;
            };
            System.Func<Motion, Motion> repoint = (m) =>
            {
                var clip = m as AnimationClip;
                if (clip != null && map.ContainsKey(clip)) { count[0]++; return map[clip]; }
                var bt = m as BlendTree; if (bt != null) retargetBt(bt);
                return m;
            };
            System.Action<AnimatorStateMachine> walkStates = null;
            walkStates = (sm) =>
            {
                if (sm == null) return;
                foreach (var cs in sm.states)
                {
                    var nm = repoint(cs.state.motion);
                    if (!ReferenceEquals(nm, cs.state.motion)) cs.state.motion = nm;
                }
                foreach (var css in sm.stateMachines) walkStates(css.stateMachine);
            };

            var layers = controller.layers;
            foreach (var layer in layers)
            {
                if (layer.syncedLayerIndex >= 0)
                {
                    var srcSm = layers[layer.syncedLayerIndex].stateMachine;
                    WalkSynced(layer, srcSm, map, retargetBt, count);
                }
                else
                {
                    walkStates(layer.stateMachine);
                }
            }
            controller.layers = layers; // persist synced-layer override edits
            return count[0];
        }

        static void WalkSynced(AnimatorControllerLayer layer, AnimatorStateMachine sm,
                               Dictionary<AnimationClip, AnimationClip> map,
                               System.Action<BlendTree> retargetBt, int[] count)
        {
            if (sm == null) return;
            foreach (var cs in sm.states)
            {
                var om = layer.GetOverrideMotion(cs.state);
                if (om == null) continue;
                var clip = om as AnimationClip;
                if (clip != null && map.ContainsKey(clip)) { layer.SetOverrideMotion(cs.state, map[clip]); count[0]++; }
                else { var bt = om as BlendTree; if (bt != null) retargetBt(bt); }
            }
            foreach (var css in sm.stateMachines) WalkSynced(layer, css.stateMachine, map, retargetBt, count);
        }

        // ── Read-only scans ────────────────────────────────────────────────────────────────────────

        /// <summary>Count motion slots (states + blend-tree children + synced overrides) whose motion is in
        /// <paramref name="clips"/>. Read-only; used for the whatIf would-retarget count.</summary>
        static int CountReferencingSlots(AnimatorController controller, HashSet<AnimationClip> clips)
        {
            var hits = new List<string>();
            ScanResidual(controller, clips, hits);
            return hits.Count;
        }

        /// <summary>Append a named slot descriptor for every motion slot whose motion is in
        /// <paramref name="clips"/>. Read-only.</summary>
        static void ScanResidual(AnimatorController controller, HashSet<AnimationClip> clips, List<string> hits)
        {
            System.Action<BlendTree, string> scanBt = null;
            scanBt = (bt, where) =>
            {
                foreach (var ch in bt.children)
                {
                    var clip = ch.motion as AnimationClip;
                    if (clip != null && clips.Contains(clip)) hits.Add(where + " > blendTree '" + bt.name + "' child '" + clip.name + "'");
                    else { var cbt = ch.motion as BlendTree; if (cbt != null) scanBt(cbt, where); }
                }
            };
            System.Action<AnimatorStateMachine, string> scanSm = null;
            scanSm = (sm, where) =>
            {
                if (sm == null) return;
                foreach (var cs in sm.states)
                {
                    var clip = cs.state.motion as AnimationClip;
                    if (clip != null && clips.Contains(clip)) hits.Add(where + " state '" + cs.state.name + "' -> '" + clip.name + "'");
                    else { var bt = cs.state.motion as BlendTree; if (bt != null) scanBt(bt, where + " state '" + cs.state.name + "'"); }
                }
                foreach (var css in sm.stateMachines) scanSm(css.stateMachine, where + "/" + css.stateMachine.name);
            };

            var layers = controller.layers;
            foreach (var layer in layers)
            {
                if (layer.syncedLayerIndex >= 0)
                {
                    var srcSm = layers[layer.syncedLayerIndex].stateMachine;
                    System.Action<AnimatorStateMachine, string> scanSync = null;
                    scanSync = (sm, where) =>
                    {
                        if (sm == null) return;
                        foreach (var cs in sm.states)
                        {
                            var om = layer.GetOverrideMotion(cs.state);
                            var clip = om as AnimationClip;
                            if (clip != null && clips.Contains(clip)) hits.Add(where + " override state '" + cs.state.name + "' -> '" + clip.name + "'");
                            else { var bt = om as BlendTree; if (bt != null) scanBt(bt, where + " override state '" + cs.state.name + "'"); }
                        }
                        foreach (var css in sm.stateMachines) scanSync(css.stateMachine, where);
                    };
                    scanSync(srcSm, "layer '" + layer.name + "'(synced)");
                }
                else
                {
                    scanSm(layer.stateMachine, "layer '" + layer.name + "'");
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

        static void EnsureFolderExists(string assetPath)
        {
            assetPath = assetPath.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            int slash = assetPath.LastIndexOf('/');
            if (slash < 0) return;
            string parent = assetPath.Substring(0, slash);
            string leaf = assetPath.Substring(slash + 1);
            EnsureFolderExists(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>Route an argument-guard failure through the house RunLog grammar (summary + RunLog +
        /// LogError), like the sibling transplant tools — never a bare trailer-less line.</summary>
        static string ArgFail(string label, string msg)
        {
            var log = new TransplantRunLog("own-controller-clips") { result = "FAIL", error = msg };
            log.Offender(msg);
            return TransplantCore.Finish(log, label);
        }

        /// <summary>Structural binding-set equality: two clips share the same set of binding keys
        /// (path + declaring type + property + float/PPtr discriminant), across float and objectReference
        /// curves. Used to decide whether a pre-existing owned copy is a match for its source (silent reuse)
        /// or a foreign/hand-edited divergence (loud warning, still reused — never overwritten).</summary>
        static bool BindingSetsMatch(AnimationClip a, AnimationClip b)
        {
            return ClipBindingKeys(a).SetEquals(ClipBindingKeys(b));
        }

        static HashSet<string> ClipBindingKeys(AnimationClip c)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in AnimationUtility.GetCurveBindings(c)) keys.Add(BKey(x));
            foreach (var x in AnimationUtility.GetObjectReferenceCurveBindings(c)) keys.Add(BKey(x));
            return keys;
        }

        static string BKey(EditorCurveBinding b)
            => b.path + "\0" + (b.type != null ? b.type.FullName : "?") + "\0" + b.propertyName + "\0" + (b.isPPtrCurve ? "1" : "0");
    }
}
