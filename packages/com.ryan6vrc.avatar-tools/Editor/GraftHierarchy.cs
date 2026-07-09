using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// One planned action for a single vendor GO inside a grafted subtree: the vendor transform, its
    /// root-relative path, and the per-component copy/skip decisions. Built in a single read-only pass
    /// (no scene mutation); <see cref="GraftHierarchy.Run"/> PRINTS it for whatIf and REPLAYS it for
    /// execute, so preview and execution consume identical decisions by construction.
    /// </summary>
    sealed class GraftStep
    {
        public Transform vendorGo;
        public string vendorPath;        // root-relative path under vendorSource ("" == subtree-root-is-source, never happens)
        public bool hostWillMint;        // true: EnsureHost will create this dest GO; false: an existing GO is reused
        public readonly List<GraftCompDecision> comps = new List<GraftCompDecision>();
    }

    /// <summary>One component's copy decision on a vendor GO: copy a fresh instance, or present-skip (count parity).</summary>
    struct GraftCompDecision
    {
        public Component vendorComponent;
        public Type componentType;
        public bool copy;                // true: AddComponent + CopySerialized; false: PresentSkip (dest already has >= N)
    }

    /// <summary>
    /// The complete read-only plan for one <see cref="GraftHierarchy.Run"/>: the ordered subtree steps
    /// (top→down so parents are scaffolded before children) plus the loud failure surfaces. Mutates
    /// nothing to build. The exact same object drives whatIf (print) and execute (replay).
    /// </summary>
    sealed class GraftPlan
    {
        public readonly List<GraftStep> steps = new List<GraftStep>();
        /// <summary>subtreeRoots that resolved to no GameObject under vendorSource — caller FAILs loud, named.</summary>
        public readonly List<string> unresolvedRoots = new List<string>();
        /// <summary>subtree GOs whose dest host could not be anchored within reach (EnsureHost would return null) — FAIL loud, named.</summary>
        public readonly List<string> unanchorable = new List<string>();

        /// <summary>A1 ambiguous-rename reasons hit while predicting host counterparts under a renameMap. A
        /// planner Counterpart reads an A1-null as "mint", so surfacing it here lets Run FAIL BOTH modes before
        /// mutation, matching execute's EnsureHost A1 guard (preview == execute). Deduped.</summary>
        public readonly List<string> renameAmbiguities = new List<string>();

        public int GoCount => steps.Count;
        public int MintCount { get { int n = 0; foreach (var s in steps) if (s.hostWillMint) n++; return n; } }
        public int ReuseCount { get { int n = 0; foreach (var s in steps) if (!s.hostWillMint) n++; return n; } }
        public int CopyCount { get { int n = 0; foreach (var s in steps) foreach (var c in s.comps) if (c.copy) n++; return n; } }
        public int SkipCount { get { int n = 0; foreach (var s in steps) foreach (var c in s.comps) if (!c.copy) n++; return n; } }
    }

    /// <summary>
    /// Third transplant tool (sibling to <see cref="CopyComponents"/> / <see cref="MoveComponents"/>):
    /// copies named GameObject SUBTREES WHOLESALE — the full structure plus ALL components on every GO —
    /// from a vendor source into our instance, remapped against the reach root. This is how an operator
    /// pulls an outfit's authoring tree / menu without listing every GameObject.
    ///
    /// Distinct, inverted contract vs <see cref="CopyComponents"/> (which is WHY it is a separate tool):
    ///   - a different SELECTION MODEL — subtree-wholesale, copy-ALL-component-types (no tier/type list);
    ///   - a missing destination host is EXPECTED AND NORMAL (you are scaffolding), NEVER a flag.
    /// It does NOT invoke deep-tier dependency-follow or leaf-anchor recreate — the whole structure is
    /// scaffolded wholesale, so those are moot; any VRC dynamics inside a grafted subtree are copied
    /// verbatim and remapped like everything else.
    ///
    /// Reach root = (<paramref name="vendorSource"/>, <paramref name="ownedRoot"/>): the remap boundary.
    /// A grafted ref whose target is UNDER vendorSource rebinds to the destination counterpart (e.g. a
    /// VRCFury toggle's <c>obj → Kemono_Tail</c> rebinds to the destination avatar's tail when reach =
    /// avatar); a ref outside reach (assets, other-avatar/external objects) is left alone and reported,
    /// not failed.
    ///
    /// Idempotent: scaffold-GO reuse-by-path (a GO already at the target indexed path is reused, never
    /// duplicated) + count-parity skip per (dest host, component type) — re-running a graft duplicates
    /// nothing.
    ///
    /// PASS = subtree grafted and every under-reach ref rebound. FAIL = a ref leaked into the vendor
    /// source, or an AddComponent / scaffold / anchor failure. Out-of-reach refs are reported (left for
    /// placement-repair), not failed. RunLog kind <c>graft-hierarchy</c>.
    /// </summary>
    [AgentTool]
    public static class GraftHierarchy
    {
        /// <param name="renameMap">Optional <c>vendorName ⇒ ownedName</c> correspondence (source-name key →
        /// destination-name value) for the destination-side child lookups — the armature-root name mismatch
        /// case (owned <c>Armature.1</c> vs vendor <c>Armature</c>). Validated once here (drop empties, reject
        /// non-injective → FAIL), then threaded to host resolution / scaffold / ref-remap. Empty/absent/null
        /// ⇒ byte-identical to today. Direction/case rationale is canonical at
        /// <see cref="IndexedPath.Substitute"/> / <see cref="IndexedPath.ValidateRenameMap"/>.</param>
        public static string Run(GameObject ownedRoot, GameObject vendorSource,
                                 string[] subtreeRoots, IDictionary<string, string> renameMap = null,
                                 bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";
            var log = new TransplantRunLog("graft-hierarchy")
            {
                whatIf   = whatIf,
                instance = ownedRoot != null ? ownedRoot.name : null,
                source   = vendorSource != null ? vendorSource.name : null,
            };

            if (ownedRoot == null || vendorSource == null)
            {
                log.result = "FAIL";
                log.error  = (ownedRoot == null ? "ownedRoot" : "vendorSource") + " is null";
                return TransplantCore.Finish(log, label);
            }
            if (subtreeRoots == null || subtreeRoots.Length == 0)
            {
                log.result = "FAIL";
                log.error  = "subtreeRoots is null/empty (nothing to graft)";
                return TransplantCore.Finish(log, label);
            }

            // Validate the rename map ONCE before any mutation: a non-injective map cannot address a unique
            // dst sibling → FAIL loud, naming the colliding keys (A2). Empty-after-cleaning ⇒ null ⇒ no-op.
            var cleanRename = IndexedPath.ValidateRenameMap(renameMap, out var collidingKeys);
            if (collidingKeys != null)
            {
                log.result = "FAIL";
                log.error  = "non-injective renameMap (keys collide onto one dest name)";
                log.Offender("renameMap non-injective: keys [" + string.Join(", ", collidingKeys) +
                             "] map onto the same value — cannot address a unique dst sibling");
                return TransplantCore.Finish(log, label);
            }
            if (cleanRename != null) log.Note("renameMap active (" + cleanRename.Count + " entr(y/ies))");

            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot     = ownedRoot.transform;

            // ── Single read-only decision pass → the plan whatIf prints and execute replays ──────────
            var plan = BuildPlan(ownedRoot, vendorSource, subtreeRoots, cleanRename);

            // A subtreeRoot that names no vendor GO, or a GO unanchorable within reach, is a tool fault →
            // FAIL loud, named, before any mutation.
            if (plan.unresolvedRoots.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "unresolved subtree root(s)";
                foreach (var r in plan.unresolvedRoots) log.Offender("subtree-root not found under vendor source: '" + r + "'");
                return TransplantCore.Finish(log, label);
            }
            if (plan.unanchorable.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "subtree GO(s) unanchorable within reach";
                foreach (var u in plan.unanchorable) log.Offender("cannot anchor within reach: '" + u + "'");
                return TransplantCore.Finish(log, label);
            }
            // A1 ambiguous-rename hard precondition — FAIL both modes before any mutation (preview == execute).
            if (plan.renameAmbiguities.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "ambiguous renameMap — a mapped name cannot address a unique dest counterpart";
                foreach (var a in plan.renameAmbiguities) log.Offender("renameMap ambiguity: " + a);
                return TransplantCore.Finish(log, label);
            }

            log.Count("subtreeGos",     plan.GoCount);
            log.Count("hostsToMint",    plan.MintCount);
            log.Count("hostsReused",    plan.ReuseCount);
            log.Count("componentsCopy", plan.CopyCount);
            log.Count("presentSkip",    plan.SkipCount);

            if (whatIf)
            {
                // Preview: print the subtree, the scaffold to create, and the planned component copies.
                // Refs are not classified here (no scene to remap against yet); execute reports the actual
                // rebind/left-external/leak split. Preview mutates nothing.
                foreach (var s in plan.steps)
                    log.Note((s.hostWillMint ? "mint" : "reuse") + " host '" + s.vendorPath + "' (" +
                             s.comps.Count + " component(s), " + CountCopies(s) + " to copy)");
                log.result = "PASS";
                return TransplantCore.Finish(log, label);
            }

            // ── EXECUTE: replay the plan under a single Undo group ────────────────────────────────────
            var session = new SessionMap();
            var created = new List<(Component vendor, Component ours)>();

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("GraftHierarchy");
            int group = Undo.GetCurrentGroup();

            try
            {
                // Pass 1 — scaffold every GO in every subtree (top→down so parents exist before children),
                // recording vendor→dst transforms into the session map. EnsureHost reuses an existing GO at
                // the target indexed path (idempotency) or mints a transforms-only GO with the vendor's
                // verbatim local TRS, bounded by reach. Then create every planned component copy onto its
                // host. Refs are NOT rebound yet — the session map must be fully populated first so an
                // intra-subtree ref hits a mapped target in pass 2 rather than path-remapping spuriously.
                foreach (var step in plan.steps)
                {
                    Transform host = ScaffoldBuilder.EnsureHost(vendorRoot, ourRoot, step.vendorGo,
                                                                out string hostFail, session, "GraftHierarchy", cleanRename);
                    if (host == null)
                    {
                        log.result = "FAIL";
                        log.error  = "scaffold/anchor failed";
                        log.Offender("could not anchor/mint dest host for '" + step.vendorPath + "' within reach — " + hostFail);
                        return TransplantCore.Finish(log, label);
                    }
                    session.AddTransform(step.vendorGo, host);

                    foreach (var dec in step.comps)
                    {
                        if (!dec.copy || dec.vendorComponent == null) continue;
                        var newComp = Undo.AddComponent(host.gameObject, dec.componentType);
                        if (newComp == null)
                        {
                            log.result = "FAIL";
                            log.error  = "AddComponent failed";
                            log.Offender(dec.componentType.Name + " on '" + step.vendorPath + "': AddComponent returned null");
                            return TransplantCore.Finish(log, label);
                        }
                        EditorUtility.CopySerialized(dec.vendorComponent, newComp);
                        session.AddComponent(dec.vendorComponent, newComp);
                        created.Add((dec.vendorComponent, newComp));
                    }
                }

                // Pass 2 — ref-remap on every created component, now that the session map is fully
                // populated. Per object-ref: session map-hit (copied component / scaffolded transform)
                // wins; else generic path-remap WITHIN REACH (RemapReferencesByPath); else it nulls. Refs
                // outside reach are left untouched (assets, external/other-avatar objects). A grafted
                // SkinnedMeshRenderer's bones[]/rootBone ride this same path (no current fixture grafts a
                // skinned mesh — exercised by construction, not fixture-verified).
                int leftExternal = 0;   // refs left pointing outside reach (reported, NOT failed)
                int nulledRefs   = 0;   // under-reach refs the remap could not resolve (reported)
                foreach (var (vendor, ours) in created)
                {
                    if (ours == null) continue;
                    var (nulled, external, renameWarns) = RemapBySession(ours, session, vendorRoot, ourRoot, cleanRename);
                    nulledRefs   += nulled;
                    leftExternal += external;
                    if (nulled > 0)
                        log.Note("verify: grafted " + ours.GetType().Name + " '" + ours.gameObject.name +
                                 "' has " + nulled + " under-reach ref(s) left null after remap");
                    foreach (var w in renameWarns)
                        log.Warning("ambiguous rename on grafted " + ours.GetType().Name + " '" +
                                    ours.gameObject.name + "': " + w);
                }

                // Pass 3 — vendor-leak sweep (shared). Any ref on a created component still pointing INTO
                // the vendor source is one the remap failed to rebind → FAIL (the contract's only ref-fault).
                var createdComps = new List<Component>(created.Count);
                foreach (var c in created) createdComps.Add(c.ours);
                int leaks = TransplantCore.SweepVendorLeaks(createdComps, vendorRoot, log);

                int copied = 0;
                foreach (var c in createdComps) if (c != null) copied++;
                log.Count("componentsCreated", copied);
                log.Count("refsLeftExternal",  leftExternal);
                log.Count("refsNulled",        nulledRefs);
                log.Count("vendorLeaks",       leaks);

                if (leftExternal > 0)
                    log.Note("refsLeftExternal=" + leftExternal +
                             " ref(s) point outside reach (left for placement-repair — not a failure)");

                log.result = leaks == 0 ? "PASS" : "FAIL";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error  = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }

            return TransplantCore.Finish(log, label);
        }

        // ── Pure read-only plan builder ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the read-only graft plan: resolve each subtree root, enumerate its GOs top→down, predict
        /// host mint-vs-reuse and per-component copy-vs-present-skip (count parity), and record unresolved
        /// roots / unanchorable GOs as loud failure surfaces. Mutates nothing. Internal so tests can drive
        /// it; <see cref="Run"/> is the public surface.
        /// </summary>
        internal static GraftPlan BuildPlan(GameObject ownedRoot, GameObject vendorSource, string[] subtreeRoots,
                                            IDictionary<string, string> renameMap = null)
        {
            var plan = new GraftPlan();
            if (ownedRoot == null || vendorSource == null || subtreeRoots == null) return plan;

            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot     = ownedRoot.transform;

            // Count-parity per (dest host transform identity, type): the same skip key CopyComponents uses.
            // Keyed on the resolved dest Transform when it exists (so duplicate-named siblings each count
            // their own host), else the unique vendor Transform (dest not yet minted → M is 0).
            var plannedSlots = new Dictionary<(Transform host, Type type), int>();

            // De-dup GOs across overlapping/nested subtree roots so a GO is planned once.
            var seen = new HashSet<Transform>();

            foreach (var raw in subtreeRoots)
            {
                var rootPath = raw?.Trim();
                if (string.IsNullOrEmpty(rootPath)) { plan.unresolvedRoots.Add(raw ?? "(null)"); continue; }

                // ResolveUnderRoot does NOT take renameMap: subtreeRoots are author-given VENDOR-side paths
                // resolved against the vendor hierarchy, so a vendorName⇒ownedName substitution would never
                // fire. Rename applies only to DESTINATION-child lookups (Counterpart / EnsureHost below).
                Transform subRoot = ResolveUnderRoot(vendorRoot, rootPath);
                if (subRoot == null) { plan.unresolvedRoots.Add(rootPath); continue; }

                // Enumerate the subtree top→down (parent before child) so the executor scaffolds parents
                // first. GetComponentsInChildren<Transform> returns the root first then descends in order.
                foreach (var vgo in subRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (vgo == null || !seen.Add(vgo)) continue;

                    var step = new GraftStep
                    {
                        vendorGo   = vgo,
                        vendorPath = RelativePath(vendorRoot, vgo),
                    };

                    // Mint-vs-reuse: does a dest counterpart already exist at this indexed path? If not,
                    // EnsureHost will mint it. Out-of-reach (not under vendorRoot) is impossible here (vgo
                    // is under subRoot under vendorRoot), but guard the contract anyway — fail loud.
                    if (!vgo.IsChildOf(vendorRoot) && vgo != vendorRoot)
                    {
                        plan.unanchorable.Add(step.vendorPath);
                        continue;
                    }
                    Transform dstHost = RemapReferencesByPath.Counterpart(vendorRoot, ourRoot, vgo, renameMap, out var cpFail);
                    if (cpFail != null && !plan.renameAmbiguities.Contains(cpFail)) plan.renameAmbiguities.Add(cpFail);
                    step.hostWillMint = dstHost == null;

                    // Per-component copy decision: copy ALL component types (it's an authoring unit), under
                    // count parity per (host identity, type). Transform itself is the scaffold, never copied.
                    Transform parityHost = dstHost != null ? dstHost : vgo;   // index-aware identity
                    foreach (var comp in vgo.GetComponents<Component>())
                    {
                        if (comp == null || comp is Transform) continue;
                        var type = comp.GetType();
                        int m = dstHost != null ? ComponentCountOn(dstHost, type) : 0;
                        var key = (parityHost, type);
                        int consumed = plannedSlots.TryGetValue(key, out var pc) ? pc : 0;
                        plannedSlots[key] = consumed + 1;
                        step.comps.Add(new GraftCompDecision
                        {
                            vendorComponent = comp,
                            componentType   = type,
                            copy            = consumed >= m,   // first M slots covered by present dest comps
                        });
                    }

                    plan.steps.Add(step);
                }
            }
            return plan;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Rebind every object ref on <paramref name="comp"/>: session map-hit (component/transform) wins;
        /// else generic path-remap within reach. Returns (nulled, leftExternal): under-reach refs the
        /// remap could not resolve, and refs left pointing outside reach (untouched). Mirrors
        /// CopyComponents.RemapBySession, plus an external-ref tally for the reporting contract.
        /// </summary>
        static (int nulled, int external, List<string> renameWarnings) RemapBySession(
            Component comp, SessionMap session, Transform vendorRoot, Transform ourRoot,
            IDictionary<string, string> renameMap)
        {
            var so = new SerializedObject(comp);
            so.Update();

            // Pass A — session map-hit: rebind refs to copied components / scaffolded transforms. These are
            // exactly the refs the generic path-remap cannot resolve (a copied component lives at a NEW
            // path; a path-remap would miss it or bind the wrong one of N same-type components). Also tally
            // refs that point OUTSIDE reach (left untouched here and by the path-remap below) so the run can
            // report them as left-for-placement.
            int external = 0;
            var it = so.GetIterator();
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                var o = it.objectReferenceValue;
                if (o == null) continue;

                if (o is Component vc && session.TryGetComponent(vc, out var mc) && mc != null)
                    it.objectReferenceValue = mc;
                else if (o is Transform vt && session.TryGetTransform(vt, out var mt) && mt != null)
                    it.objectReferenceValue = mt;
                else if (o is GameObject vg && session.TryGetTransform(vg.transform, out var mgt) && mgt != null)
                    it.objectReferenceValue = mgt.gameObject;
                else
                {
                    // Not a session-map hit. Is it an under-reach scene object the path-remap will handle,
                    // or an out-of-reach ref left for placement-repair?
                    Transform t = o is Component oc ? oc.transform : (o is GameObject og ? og.transform : null);
                    bool underReach = t != null && (t == vendorRoot || t.IsChildOf(vendorRoot));
                    if (t != null && !underReach) external++;   // asset/null-host refs aren't counted external
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            // Pass B — generic path-remap for whatever the session map didn't cover (under-reach scene
            // objects at a path-present transform). Refs outside reach are left untouched.
            so.Update();
            var rr = RemapReferencesByPath.Remap(so, vendorRoot, ourRoot, renameMap);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(comp);
            return (rr.nulled, external, rr.renameWarnings ?? new List<string>());
        }

        /// <summary>Resolve a "/"-separated path under <paramref name="root"/> by indexed-path walk; null if absent.</summary>
        static Transform ResolveUnderRoot(Transform root, string path)
        {
            var segs = path.Split('/');
            Transform cur = root;
            foreach (var seg in segs)
            {
                if (string.IsNullOrEmpty(seg)) continue;
                // First same-named child at each level (subtreeRoots are author-given paths; duplicate-named
                // top-level subtree roots are not a case the operator addresses — the indexed remap handles
                // duplicate-named siblings DEEPER inside the subtree once the root is found).
                Transform next = IndexedPath.NthChildWithName(cur, seg, 0);
                if (next == null) return null;
                cur = next;
            }
            return cur == root ? null : cur;   // an empty/root-only path grafts nothing → unresolved
        }

        static int ComponentCountOn(Transform host, Type type)
        {
            if (host == null) return 0;
            int n = 0;
            foreach (var c in host.GetComponents(type)) if (c != null) n++;
            return n;
        }

        static int CountCopies(GraftStep s)
        {
            int n = 0;
            foreach (var c in s.comps) if (c.copy) n++;
            return n;
        }

        /// <summary>Root-relative path of <paramref name="t"/> under <paramref name="root"/> ("" when t == root).</summary>
        static string RelativePath(Transform root, Transform t)
        {
            if (t == null || root == null) return null;
            if (t == root) return "";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != root; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
