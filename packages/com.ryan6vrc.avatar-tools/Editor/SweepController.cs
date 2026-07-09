// SweepController — destructive orphaned-sub-asset sweep for an AnimatorController.
//
// The reachability sweep, the dead-end-transition filter (a transition is "used" only if it resolves
// to a state, a sub-state-machine, or isExit), and the post-sweep null-slot compaction
// (RemoveMissingTransitions / KeepValid) idioms are EXTRACTED from DreadScripts' ControllerCleaner
// (as rehosted by VRLabs: dev.vrlabs.controllercleaner v1.0.1, MIT). Reimplemented here against
// TransplantCore's guard/RunLog/whatIf shell, with DreadScripts' threading
// (Task/ConcurrentBag/CancellationTokenSource) dropped to a synchronous walk and its EditorWindow shell
// dropped for the house execute_code door. The reachability walk itself mirrors the synced-layer-correct
// traversal of Ryan6Vrc.AgentTools.Editor.CheckAnimator.RuleOrphanSubAsset (the read-only detector this
// tool is the mutating other half of). This file does NOT depend on or call the source package.
//
// DreadScripts ControllerCleaner © Dreadrith, rehosted by VRLabs — MIT License
// (https://github.com/VRLabs/Controller-Cleaner).

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Sweeps an <see cref="AnimatorController"/>'s orphaned sub-assets — the mutating other half of the
    /// read-only <see cref="CheckAnimator"/>. It (1) DESTROYS every controller sub-asset of the five
    /// animator types (<see cref="AnimatorStateMachine"/> / <see cref="AnimatorState"/> /
    /// <see cref="BlendTree"/> / <see cref="StateMachineBehaviour"/> / <see cref="AnimatorTransitionBase"/>)
    /// that the reachability walk does not reach, (2) treats a non-null but dead-end transition (resolves
    /// to no <c>destinationState</c>, no <c>destinationStateMachine</c>, and not <c>isExit</c>) as
    /// unreachable and removes it too, and (3) compacts the null transition slots left behind. Mutates the
    /// <c>.controller</c> in place; the door is the static <see cref="Sweep"/> call (no menu).
    ///
    /// <para>The walk mirrors <c>CheckAnimator.RuleOrphanSubAsset</c>: synced-layer-correct (a synced
    /// layer's per-state OVERRIDE motions/behaviours via <see cref="AnimatorControllerLayer.GetOverrideMotion"/>
    /// / <c>GetOverrideBehaviours</c> are marked, so an override BlendTree is never false-swept), and
    /// <c>is</c>-based type matching (so VRC <see cref="StateMachineBehaviour"/> subclasses are covered).
    /// The two walks are deliberately allowed to diverge — this one's transition-marking is dead-end-aware,
    /// CheckAnimator's is not — and are kept honest by the differential-oracle test, not by sharing code.</para>
    ///
    /// <para><b>KNOWN SMB BOUND (load-bearing for a remover, inherited from CheckAnimator).</b>
    /// StateMachineBehaviours are marked reachable, but their serialized references are NOT followed. If
    /// some exotic SMB held a controller sub-asset reference, this walk would not see it and could sweep a
    /// live sub-asset. No standard VRC/Unity SMB holds a controller sub-asset ref, so a full
    /// <see cref="SerializedObject"/> sweep would add cost for a purely theoretical case. The risk is
    /// bounded by three nets: (1) <c>whatIf</c> previews the full plan before any destroy; (2) every
    /// removal is named on the RunLog <c>notes</c> (auditable); (3) every controller the writable guard
    /// permits is owned → git-tracked YAML → a bad sweep is a revertable diff.</para>
    ///
    /// <para>Two blocking guards FAIL identically in <c>whatIf</c> and execute: a read-only controller
    /// (under <c>Assets/Vendor</c> or <c>Packages</c>) FAILs unless <c>force</c> (which downgrades to a
    /// loud note and proceeds); a not-a-saved-asset (in-memory) controller is refused (no <c>force</c>
    /// path — nothing on disk to sweep). Call <see cref="Sweep"/> from MCP execute_code.</para>
    /// </summary>
    [AgentTool]
    public static class SweepController
    {
        /// <summary>
        /// Sweep <paramref name="controller"/>'s orphaned sub-assets + dead-end transitions and compact the
        /// null slots left behind, in place. Returns a one-line PASS/FAIL summary ending with the RunLog
        /// path (<c>… =&gt; RESULT | log=&lt;path&gt;</c>). Removals surface under <c>notes</c>; <c>offenders</c>
        /// stays empty on success (preserving <c>offenders ⇔ FAIL</c>). With <paramref name="whatIf"/> the
        /// full plan (same counts + would-remove notes) is computed and NOTHING is mutated.
        /// </summary>
        public static string Sweep(AnimatorController controller, bool force = false, bool whatIf = false)
        {
            if (controller == null) return ArgFail("null-controller", "controller is null");

            string controllerPath = AssetDatabase.GetAssetPath(controller);
            var log = new TransplantRunLog("sweep-controller")
            {
                whatIf = whatIf,
                instance = controller.name,
                source = controllerPath,
            };
            string label = TransplantCore.Sanitize(controller.name);

            try
            {
                // ── Guard: not a saved asset (an in-memory controller has no sub-assets on disk) ──
                //    Must precede the writable guard: an empty path reads as "writable", so only this
                //    guard catches an unsaved controller. No force path — there is nothing to sweep.
                if (string.IsNullOrEmpty(controllerPath))
                {
                    log.Offender("controller '" + controller.name +
                        "' is not a saved asset — no sub-assets on disk to sweep");
                    log.result = "FAIL";
                    log.error = "controller is not a saved asset";
                    return TransplantCore.Finish(log, label);
                }

                // ── Guard: read-only controller (the sweep mutates THIS asset in place) ──
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

                // ── Reachability walk → reachable set + true-dead-end set ──
                var reachable = new HashSet<UnityEngine.Object>();
                var deadEnds = new HashSet<AnimatorTransitionBase>();

                void AddMotion(Motion m)
                {
                    if (m is BlendTree bt && reachable.Add(bt))
                        foreach (var ch in bt.children) AddMotion(ch.motion);
                }
                // The dead-end filter (DreadScripts AddTransitions): a non-null transition is reachable only
                // if it resolves somewhere; otherwise it is a true dead-end (visited on a live array on a
                // reachable state, but going nowhere) → deadEnds, NOT reachable, so it folds into obsolete.
                // Applied to ALL FOUR transition arrays the walk encounters (under-applying skews the oracle).
                void MarkTransitions(IEnumerable<AnimatorTransitionBase> transitions)
                {
                    if (transitions == null) return;
                    foreach (var t in transitions)
                    {
                        if (t == null) continue;
                        if (t.destinationState || t.destinationStateMachine || t.isExit) reachable.Add(t);
                        else deadEnds.Add(t);
                    }
                }
                void AddSm(AnimatorStateMachine sm)
                {
                    if (sm == null || !reachable.Add(sm)) return;
                    // SMB bound (see class docstring): behaviours marked reachable, refs NOT followed.
                    if (sm.behaviours != null) foreach (var b in sm.behaviours) if (b != null) reachable.Add(b);
                    MarkTransitions(sm.anyStateTransitions);
                    MarkTransitions(sm.entryTransitions);
                    foreach (var cs in sm.states)
                    {
                        var st = cs.state;
                        if (st == null) continue;
                        reachable.Add(st);
                        if (st.behaviours != null) foreach (var b in st.behaviours) if (b != null) reachable.Add(b);
                        MarkTransitions(st.transitions);
                        AddMotion(st.motion);
                    }
                    foreach (var child in sm.stateMachines)
                    {
                        if (child.stateMachine == null) continue;
                        MarkTransitions(sm.GetStateMachineTransitions(child.stateMachine));
                        AddSm(child.stateMachine);
                    }
                }
                // A synced layer re-skins the SOURCE layer's states with per-state override motions/behaviours
                // — distinct sub-assets not reachable through the source SM's own states. Mark them, or an
                // override BlendTree false-positives as an orphan. Synced layers contribute no dead-ends
                // (their transitions live on the source layer, walked once as the source's own SM).
                void AddSyncedOverrides(AnimatorStateMachine sm, AnimatorControllerLayer layer)
                {
                    if (sm == null) return;
                    foreach (var cs in sm.states)
                    {
                        if (cs.state == null) continue;
                        AddMotion(layer.GetOverrideMotion(cs.state));
                        var ob = layer.GetOverrideBehaviours(cs.state);
                        if (ob != null) foreach (var b in ob) if (b != null) reachable.Add(b);
                    }
                    foreach (var child in sm.stateMachines)
                        if (child.stateMachine != null) AddSyncedOverrides(child.stateMachine, layer);
                }

                var layers = controller.layers;
                foreach (var layer in layers)
                {
                    if (layer.syncedLayerIndex >= 0)
                        AddSyncedOverrides(layers[layer.syncedLayerIndex].stateMachine, layer);
                    else
                        AddSm(layer.stateMachine);
                }

                // ── Obsolete set + name the plan ──
                // No IsSubAsset filter: LoadAllAssetsAtPath is already path-scoped to this one controller,
                // and AssetDatabase.IsSubAsset returns FALSE for the HideInHierarchy animator sub-objects a
                // controller is made of — which is what real orphans are. Verified live against 67 real-world
                // controllers: of the orphan objects, ~97% were hidden (0 satisfied IsSubAsset). Gating on
                // IsSubAsset (as a naive mirror of CheckAnimator's old predicate did) would leave the actual
                // bloat behind. o != controller + the five-type filter + !reachable is the real gate.
                // Two views of the same set: the List preserves discovery order for the auditable `notes`,
                // the HashSet gives O(1) Contains for the slot-compaction predicate (CountObsoleteSlots).
                var obsolete = new List<UnityEngine.Object>();
                var obsoleteSet = new HashSet<UnityEngine.Object>();
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(controllerPath))
                {
                    if (o == null || o == controller) continue;
                    if (!IsAnimatorSubType(o)) continue;
                    if (reachable.Contains(o)) continue;
                    obsolete.Add(o);
                    obsoleteSet.Add(o);
                    log.Note(o.GetType().Name + " '" + o.name + "'");
                }

                // deadEndTransitions = the true-dead-end members of the obsolete set (deadEnds are always
                // obsolete: non-reachable five-type sub-assets at this path). A TRULY-orphan transition (on
                // an unreachable state / in no live array) was never visited, so it is not in deadEnds — it
                // counts only under `removed`. This is the split the differential oracle depends on.
                int deadEndCount = 0;
                foreach (var o in obsolete)
                    if (o is AnimatorTransitionBase tb && deadEnds.Contains(tb)) deadEndCount++;

                // ── Guarded source state machines (compaction targets) ──
                // Skip syncedLayerIndex >= 0 layers (they share the source layer's SM — compacting via the
                // synced layer would touch the shared SM twice) and null root SMs; each source SM once.
                // This is the guard DreadScripts' RemoveMissingTransitions() lacks (it iterates every layer).
                var compactSms = new List<AnimatorStateMachine>();
                var smSeen = new HashSet<AnimatorStateMachine>();
                void CollectSms(AnimatorStateMachine sm)
                {
                    if (sm == null || !smSeen.Add(sm)) return;
                    compactSms.Add(sm);
                    foreach (var child in sm.stateMachines) CollectSms(child.stateMachine);
                }
                foreach (var layer in layers)
                {
                    if (layer.syncedLayerIndex >= 0) continue;
                    CollectSms(layer.stateMachine);
                }

                // ── slotsCompacted (computed BEFORE any mutation, off the obsolete SET) ──
                // Counting `slot == null || obsolete.Contains(slot)` off the un-mutated arrays makes the
                // count identical in whatIf and execute: in execute the removed slots go null (step: remove),
                // but the count is taken here, not from the post-destroy nulls. This is the source of
                // slotsCompacted in BOTH paths.
                int slotsCompacted = 0;
                foreach (var sm in compactSms)
                {
                    slotsCompacted += CountObsoleteSlots(sm.entryTransitions, obsoleteSet);
                    slotsCompacted += CountObsoleteSlots(sm.anyStateTransitions, obsoleteSet);
                    foreach (var cs in sm.states)
                        if (cs.state != null) slotsCompacted += CountObsoleteSlots(cs.state.transitions, obsoleteSet);
                    foreach (var child in sm.stateMachines)
                        if (child.stateMachine != null)
                            slotsCompacted += CountObsoleteSlots(sm.GetStateMachineTransitions(child.stateMachine), obsoleteSet);
                }

                // Stable count order on the one-liner + JSON; identical in whatIf and execute.
                log.Count("removed", obsolete.Count);
                log.Count("deadEndTransitions", deadEndCount);
                log.Count("slotsCompacted", slotsCompacted);

                // ── whatIf short-circuit: plan built, mutate nothing ──
                if (whatIf)
                {
                    log.result = "PASS";
                    return TransplantCore.Finish(log, label);
                }

                // ── Remove (mirror DreadScripts CleanUpController) ──
                foreach (var o in obsolete)
                {
                    if (o == null) continue;
                    AssetDatabase.RemoveObjectFromAsset(o);
                    UnityEngine.Object.DestroyImmediate(o);
                }

                // ── Compact null slots (guarded) — after removal the stripped slots are null ──
                foreach (var sm in compactSms)
                {
                    sm.entryTransitions = KeepValid(sm.entryTransitions);
                    sm.anyStateTransitions = KeepValid(sm.anyStateTransitions);
                    foreach (var cs in sm.states)
                    {
                        if (cs.state == null) continue;
                        cs.state.transitions = KeepValid(cs.state.transitions);
                        EditorUtility.SetDirty(cs.state);
                    }
                    foreach (var child in sm.stateMachines)
                    {
                        if (child.stateMachine == null) continue;
                        sm.SetStateMachineTransitions(child.stateMachine,
                            KeepValid(sm.GetStateMachineTransitions(child.stateMachine)));
                    }
                    EditorUtility.SetDirty(sm);
                }

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();

                // ── Residual check (execute-only; detects a removal that did not take) ──
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(controllerPath))
                {
                    if (o == null || o == controller) continue;
                    if (!IsAnimatorSubType(o)) continue;
                    if (reachable.Contains(o)) continue;
                    log.Offender("residual obsolete sub-asset survived removal: " +
                        o.GetType().Name + " '" + o.name + "'");
                }

                log.result = log.offenders.Count > 0 ? "FAIL" : "PASS";
                if (log.result == "FAIL") log.error = "obsolete sub-asset survived removal (post-condition)";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error = ex.GetType().Name + ": " + ex.Message;
            }

            return TransplantCore.Finish(log, label);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

        static bool IsAnimatorSubType(UnityEngine.Object o) =>
            o is AnimatorStateMachine || o is AnimatorState || o is BlendTree
            || o is StateMachineBehaviour || o is AnimatorTransitionBase;

        static int CountObsoleteSlots(AnimatorTransitionBase[] arr, HashSet<UnityEngine.Object> obsolete)
        {
            if (arr == null) return 0;
            int n = 0;
            foreach (var t in arr) if (t == null || obsolete.Contains(t)) n++;
            return n;
        }

        /// <summary>DreadScripts' <c>KeepValid</c>: drop null (destroyed) slots. A destroyed
        /// <see cref="UnityEngine.Object"/> is fake-null, so <c>o =&gt; o</c> filters it out.</summary>
        static T[] KeepValid<T>(T[] array) where T : UnityEngine.Object =>
            array == null ? Array.Empty<T>() : array.Where(o => o).ToArray();

        /// <summary>Route an argument-guard failure through the house RunLog grammar (summary + RunLog +
        /// LogError), like the sibling transplant tools — never a bare trailer-less line.</summary>
        static string ArgFail(string label, string msg)
        {
            var log = new TransplantRunLog("sweep-controller") { result = "FAIL", error = msg };
            log.Offender(msg);
            return TransplantCore.Finish(log, label);
        }
    }
}
