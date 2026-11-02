using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// One planned vendor GO in a grafted subtree: its root-relative path and per-component decisions.
    /// </summary>
    sealed class GraftStep
    {
        public Transform vendorGo;
        public string vendorPath;        // root-relative path under vendorSource
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
    /// The read-only plan for one <see cref="GraftHierarchy.Run"/>: the steps, ordered so parents are scaffolded
    /// before children, plus the loud failure surfaces. The same object drives whatIf (print) and execute (replay),
    /// so the two consume identical decisions.
    /// </summary>
    sealed class GraftPlan
    {
        public readonly List<GraftStep> steps = new List<GraftStep>();
        /// <summary>subtreeRoots that resolved to no GameObject under vendorSource — caller FAILs loud, named.</summary>
        public readonly List<string> unresolvedRoots = new List<string>();
        /// <summary>subtree GOs outside the reach root, which cannot occur while every GO comes from under vendorSource — FAIL loud, named.</summary>
        public readonly List<string> unanchorable = new List<string>();

        /// <summary>Ambiguous-rename reasons hit while predicting host counterparts. The planner would read Counterpart's
        /// null as "mint", so surfacing them lets Run FAIL both modes before mutation, matching EnsureHost's guard.</summary>
        public readonly List<string> renameAmbiguities = new List<string>();

        public int GoCount => steps.Count;
        public int MintCount { get { int n = 0; foreach (var s in steps) if (s.hostWillMint) n++; return n; } }
        public int ReuseCount { get { int n = 0; foreach (var s in steps) if (!s.hostWillMint) n++; return n; } }
        public int CopyCount { get { int n = 0; foreach (var s in steps) foreach (var c in s.comps) if (c.copy) n++; return n; } }
        public int SkipCount { get { int n = 0; foreach (var s in steps) foreach (var c in s.comps) if (!c.copy) n++; return n; } }
    }

    /// <summary>Copies named GameObject subtrees wholesale, structure plus every component, from a vendor source
    /// into our instance, remapped against the reach root. VRC dynamics inside a subtree copy verbatim, with no
    /// deep-tier dependency-follow or leaf-anchor recreate.
    /// Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class GraftHierarchy
    {
        /// <param name="vendorToOwned">Optional <c>vendorName ⇒ ownedName</c> map for destination-side child lookups, for
        /// the armature-root name mismatch (owned <c>Armature.1</c> vs vendor <c>Armature</c>). Direction and case rules
        /// are canonical at <see cref="IndexedPath.Substitute"/>.</param>
        public static string Run(GameObject ownedRoot, GameObject vendorSource,
                                 string[] subtreeRoots, IDictionary<string, string> vendorToOwned = null,
                                 bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";
            var log = new RunLog("graft-hierarchy")
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

            // Validated before any mutation. Empty after cleaning returns null, a no-op.
            var cleanRename = IndexedPath.ValidateRenameMap(vendorToOwned, out var collidingKeys);
            if (collidingKeys != null)
            {
                log.result = "FAIL";
                log.error  = "non-injective vendorToOwned (keys collide onto one dest name)";
                log.Offender("vendorToOwned non-injective: keys [" + string.Join(", ", collidingKeys) +
                             "] map onto the same value — cannot address a unique dst sibling");
                return TransplantCore.Finish(log, label);
            }
            if (cleanRename != null) log.Note("vendorToOwned active (" + cleanRename.Count + " entr(y/ies))");

            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot     = ownedRoot.transform;

            // ── Single read-only decision pass → the plan whatIf prints and execute replays ──────────
            var plan = BuildPlan(ownedRoot, vendorSource, subtreeRoots, cleanRename);

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
            if (plan.renameAmbiguities.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "ambiguous vendorToOwned — a mapped name cannot address a unique dest counterpart";
                foreach (var a in plan.renameAmbiguities) log.Offender("vendorToOwned ambiguity: " + a);
                return TransplantCore.Finish(log, label);
            }

            log.Count("subtreeGos",     plan.GoCount);
            log.Count("hostsToMint",    plan.MintCount);
            log.Count("hostsReused",    plan.ReuseCount);
            log.Count("componentsCopy", plan.CopyCount);
            log.Count("presentSkip",    plan.SkipCount);

            if (whatIf)
            {
                // Refs are not classified in preview: only execute reports the rebind/left-external/leak split.
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
                // Pass 1: scaffold every GO and create every planned component copy. Refs wait for pass 2: the session
                // map must be fully populated first, so an intra-subtree ref hits a mapped target rather than
                // path-remapping spuriously.
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

                // Pass 2: ref-remap every created component. A grafted SkinnedMeshRenderer's bones[]/rootBone ride this
                // path, but no fixture grafts a skinned mesh, so that case is unverified.
                int leftExternal = 0;
                int nulledRefs   = 0;
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

                // Pass 3: any ref still pointing into the vendor source is the contract's only ref fault.
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
        /// Builds the read-only plan: resolves each subtree root, predicts host mint-vs-reuse and per-component
        /// copy-vs-present-skip, and records the failures for <see cref="Run"/> to report.
        /// </summary>
        internal static GraftPlan BuildPlan(GameObject ownedRoot, GameObject vendorSource, string[] subtreeRoots,
                                            IDictionary<string, string> vendorToOwned = null)
        {
            var plan = new GraftPlan();
            if (ownedRoot == null || vendorSource == null || subtreeRoots == null) return plan;

            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot     = ownedRoot.transform;

            // Count parity per (dest host transform, type), the skip key CopyComponents uses. Duplicate-named siblings
            // each count their own host; a not-yet-minted dest keys on its unique vendor Transform.
            var plannedSlots = new Dictionary<(Transform host, Type type), int>();

            // De-dup GOs across overlapping/nested subtree roots so a GO is planned once.
            var seen = new HashSet<Transform>();

            foreach (var raw in subtreeRoots)
            {
                var rootPath = raw?.Trim();
                if (string.IsNullOrEmpty(rootPath)) { plan.unresolvedRoots.Add(raw ?? "(null)"); continue; }

                // subtreeRoots are vendor-side paths, so vendorToOwned applies only to the destination lookups below.
                Transform subRoot = ResolveUnderRoot(vendorRoot, rootPath);
                if (subRoot == null) { plan.unresolvedRoots.Add(rootPath); continue; }

                // GetComponentsInChildren<Transform> returns the root first and descends, so parents scaffold first.
                foreach (var vgo in subRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (vgo == null || !seen.Add(vgo)) continue;

                    var step = new GraftStep
                    {
                        vendorGo   = vgo,
                        vendorPath = RelativePath(vendorRoot, vgo),
                    };

                    if (!vgo.IsChildOf(vendorRoot) && vgo != vendorRoot)
                    {
                        plan.unanchorable.Add(step.vendorPath);
                        continue;
                    }
                    Transform dstHost = RemapReferencesByPath.Counterpart(vendorRoot, ourRoot, vgo, vendorToOwned, out var cpFail);
                    if (cpFail != null && !plan.renameAmbiguities.Contains(cpFail)) plan.renameAmbiguities.Add(cpFail);
                    step.hostWillMint = dstHost == null;

                    Transform parityHost = dstHost != null ? dstHost : vgo;
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
        /// Rebinds every object ref on <paramref name="comp"/>: a session-map hit wins, else generic path-remap within
        /// reach. Returns nulled (under-reach refs the remap could not resolve) and external (refs left outside reach).
        /// Mirrors CopyComponents.RemapBySession, plus the external tally.
        /// </summary>
        static (int nulled, int external, List<string> renameWarnings) RemapBySession(
            Component comp, SessionMap session, Transform vendorRoot, Transform ourRoot,
            IDictionary<string, string> vendorToOwned)
        {
            var so = new SerializedObject(comp);
            so.Update();

            // Pass A: session-map hits. A path-remap cannot resolve these: a copied component lives at a new path,
            // so it would miss the target or bind the wrong one of N same-type components.
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
                    Transform t = o is Component oc ? oc.transform : (o is GameObject og ? og.transform : null);
                    bool underReach = t != null && (t == vendorRoot || t.IsChildOf(vendorRoot));
                    if (t != null && !underReach) external++;   // asset/null-host refs aren't counted external
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            // Pass B: generic path-remap for whatever the session map did not cover.
            so.Update();
            var rr = RemapReferencesByPath.Remap(so, vendorRoot, ourRoot, vendorToOwned);
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
                // First same-named child at each level: a subtree root cannot address a duplicate-named sibling. The
                // indexed remap handles those deeper inside the subtree.
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
