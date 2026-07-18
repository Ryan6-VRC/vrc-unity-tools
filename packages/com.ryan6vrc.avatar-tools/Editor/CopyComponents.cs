using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Per-vendor-component decision the planner reached. One <see cref="CopyAction"/> describes the
    /// fate of one (or one slot of a count-parity group of) vendor component:
    ///   - <c>InPlace</c>        — copy onto an existing dest host counterpart.
    ///   - <c>RecreateLeaf</c>   — host (non-bone leaf anchor, e.g. Col_*) is missing but its parent
    ///                             bone counterpart exists; recreate the depth-1 anchor GO and copy.
    ///   - <c>Scaffold</c>       — a forced missing bone (named in <c>force</c>); build a depth-N
    ///                             transforms-only chain to it and copy.
    ///   - <c>PresentSkip</c>    — count-parity already satisfied this (host,type) slot; no copy.
    ///   - <c>FlaggedMissing</c> — host missing and NOT recreatable/forced; flag, never build. PASS.
    ///   - <c>HardDepNull</c>    — the component IS copied, but a hard-dependency referent will not be
    ///                             copied → that ref entry is predicted null + flagged (verify-may-block).
    /// </summary>
    public enum CopyAction { InPlace, RecreateLeaf, Scaffold, PresentSkip, FlaggedMissing, HardDepNull }

    /// <summary>
    /// One predicted decision in a <see cref="CopyPlan"/>, built against a predicted resolution map with
    /// NO scene mutation. Carries the vendor component, the resolved/predicted destination host PATH
    /// (root-relative string under the dest instance), the tier flag, and per-reference predictions.
    /// The executor REPLAYS these steps, so each must be complete data — no decision deferred.
    /// </summary>
    public sealed class CopyStep
    {
        /// <summary>The vendor component this decision is about (read-only; never mutated by the planner).</summary>
        public Component vendorComponent;
        /// <summary>The vendor component's runtime type (cached; survives even if vendorComponent is collected).</summary>
        public Type componentType;
        /// <summary>Root-relative path of the vendor host GO under the vendor source (for reporting / force keys).</summary>
        public string vendorHostPath;
        /// <summary>
        /// Predicted root-relative path of the destination host GO under our instance. For an existing
        /// counterpart this is its current path; for a recreate/scaffold it is the path the executor will
        /// mint; null when <see cref="action"/> is <c>FlaggedMissing</c> (no host predicted).
        /// </summary>
        public string destHostPath;
        /// <summary>The planner's verdict for this component.</summary>
        public CopyAction action;
        /// <summary>True if the vendor component is a deep-tier (VRC table) type; false = conservative tier.</summary>
        public bool isDeepTier;
        /// <summary>True if this component was pulled in as a deep hard-dependency rather than listed by type.</summary>
        public bool pulledAsHardDep;
        /// <summary>
        /// Deep-tier only: true when the resolved/flagged host is a skeleton bone (appears in some
        /// SMR.bones[]), false when it is a non-bone holder GameObject. Set at the deep-tier
        /// host-resolution branch; left at its default for conservative steps, which are never
        /// classified (they have no force/scaffold path, so a [holder] label would be a dead affordance).
        /// Drives the [bone]/[holder] flagged-missing note. Never enters <see cref="ForceKey"/>.
        /// </summary>
        public bool isBone;
        /// <summary>
        /// Per-reference predictions for this component's deep-tier reference fields: hard-dep entries
        /// predicted to null (target not copied) and soft-dep entries silently dropped (target absent).
        /// Empty for conservative tier and for fully-resolved deep components.
        /// </summary>
        public readonly List<RefPrediction> refs = new List<RefPrediction>();

        /// <summary>Stable key for the flagged-missing list and the <c>force</c> set: <c>vendorRelativePath :: ComponentType</c>.</summary>
        public string ForceKey => vendorHostPath + " :: " + (componentType != null ? componentType.Name : "(null)");
    }

    /// <summary>How one reference field (or array slot) on a copied deep-tier component is predicted to resolve.</summary>
    public enum RefDisposition { HardDepNull, SoftDepDropped }

    /// <summary>One predicted reference outcome on a copied component (deep tier only).</summary>
    public struct RefPrediction
    {
        public string fieldPath;             // the SerializedProperty path token (e.g. "colliders", "Sources[].SourceTransform")
        public string targetVendorPath;      // root-relative path of the vendor referent
        public RefDisposition disposition;
    }

    /// <summary>
    /// The complete, pure, replayable plan a <c>CopyComponents</c> run will execute. Built in topo order
    /// (colliders → physbones → contacts → constraints, deep tier entirely before conservative) against a
    /// predicted resolution map (vendor identity → predicted outcome) so a dependent's refs resolve
    /// against already-planned identities. Mutates nothing. <see cref="BuildPlan"/> is the unit-tested
    /// core; preview and execution cannot diverge because both consume this exact object.
    /// </summary>
    public sealed class CopyPlan
    {
        public readonly List<CopyStep> steps = new List<CopyStep>();

        /// <summary>Names that failed type-name resolution (unknown OR ambiguous) — caller fails loud.</summary>
        public readonly List<string> unresolvedTypeNames = new List<string>();

        /// <summary>
        /// Flagged-missing HOSTS, formatted <c>vendorRelativePath :: ComponentType</c>. These double as
        /// the keys the operator copies into <c>force</c>. A non-empty list is PASS (expected subset).
        /// </summary>
        public readonly List<string> flaggedMissingHosts = new List<string>();

        /// <summary>
        /// Predicted nulled refs on components we WILL copy (hard-dep targets that won't be copied). Each
        /// entry is surfaced prominently as "verify — may block build": a nulled VRCF propBone / constraint
        /// source can abort the downstream VRCF/SDK build (unlike MA, which self-heals). Realized mostly by
        /// the executor; the planner records the predicted hard-dep nulls here.
        /// </summary>
        public readonly List<string> verifyMayBlockBuild = new List<string>();

        /// <summary>
        /// Loud named offenders for VRC-table / SDK drift: a deep-tier descriptor declared a dependency
        /// field-path that no longer resolves to a SerializedProperty on the actual component (a mistyped
        /// table entry or an SDK field rename). Without this, that hard/soft dep would be silently dropped
        /// with no signal. Entries: <c>ComponentType.fieldPath on vendorRelativePath (no such property)</c>.
        /// </summary>
        public readonly List<string> tableDriftOffenders = new List<string>();

        /// <summary>
        /// A1 ambiguous-rename reasons hit while PREDICTING host counterparts under a <c>vendorToOwned</c>. A
        /// planner <c>Counterpart</c> reads an A1-null as "mint/scaffold", so without surfacing it a whatIf
        /// preview would PASS while execute's <c>EnsureHost</c> A1 guard FAILs (preview ≠ execute). A non-empty
        /// list is a hard precondition FAIL in BOTH modes, before any mutation. Deduped.
        /// </summary>
        public readonly List<string> renameAmbiguities = new List<string>();

        // ── Count rollups (computed from steps) ─────────────────────────────────────────────────────

        public int CountOf(CopyAction a)
        {
            int n = 0;
            foreach (var s in steps) if (s.action == a) n++;
            return n;
        }

        public int InPlace        => CountOf(CopyAction.InPlace);
        public int RecreateLeaf   => CountOf(CopyAction.RecreateLeaf);
        public int Scaffold       => CountOf(CopyAction.Scaffold);
        public int PresentSkip    => CountOf(CopyAction.PresentSkip);
        public int FlaggedMissing => CountOf(CopyAction.FlaggedMissing);
        public int HardDepNull    => CountOf(CopyAction.HardDepNull);

        /// <summary>Deep-tier (VRC table) steps with the given action.</summary>
        int CountOf(CopyAction a, bool deep)
        {
            int n = 0;
            foreach (var s in steps) if (s.action == a && s.isDeepTier == deep) n++;
            return n;
        }

        /// <summary>Deep-tier in-place copies (physbones/contacts/constraints onto existing hosts).</summary>
        public int DeepInPlace => CountOf(CopyAction.InPlace, true);

        /// <summary>
        /// Conservative-tier copies actually planned (any copy action on a non-deep component). A
        /// conservative host is always present (else it is FlaggedMissing), so in practice this is the
        /// conservative InPlace count.
        /// </summary>
        public int Conservative
        {
            get
            {
                int n = 0;
                foreach (var s in steps)
                    if (!s.isDeepTier &&
                        (s.action == CopyAction.InPlace || s.action == CopyAction.RecreateLeaf ||
                         s.action == CopyAction.Scaffold || s.action == CopyAction.HardDepNull))
                        n++;
                return n;
            }
        }

        /// <summary>
        /// One-line rollup for the console; the same numbers the operator iterates on. The in-place count is
        /// split into a deep-tier field (<c>deepInPlace</c>) and a conservative field (<c>conservative</c>),
        /// alongside the recreate/scaffold/skip/flagged/hard-dep-null counts.
        /// </summary>
        public string RollupLine()
        {
            return string.Format(
                "deepInPlace={0}, recreateLeaf={1}, scaffold={2}, presentSkip={3}, flaggedMissing={4}, hardDepNull={5}, conservative={6}",
                DeepInPlace, RecreateLeaf, Scaffold, PresentSkip, FlaggedMissing, HardDepNull, Conservative);
        }
    }

    /// <summary>
    /// The pure, scene-non-mutating PLAN BUILDER for <c>CopyComponents</c>. Builds a complete
    /// <see cref="CopyPlan"/> from a vendor source and our instance against a predicted resolution map.
    /// Reads vendor serialized data read-only (SerializedObject) and predicts dest host counterparts via
    /// <see cref="RemapReferencesByPath.Counterpart"/> / <see cref="IndexedPath"/>. NEVER calls
    /// AddComponent / scaffold / CopySerialized — prediction only.
    ///
    /// The two tiers: deep (VRC table) gets dependency-follow, leaf-anchor recreate,
    /// force/scaffold, hard/soft criticality; conservative (table miss) is copy-if-host-present, else
    /// flagged-missing, never scaffold/follow-dep.
    /// </summary>
    [AgentTool]
    public static class CopyComponents
    {
        // ── Public plan builder ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the pure whatIf plan. <paramref name="force"/> entries are <c>vendorRelativePath ::
        /// ComponentType</c> keys (from a prior plan's <see cref="CopyPlan.flaggedMissingHosts"/>) that
        /// promote a missing-bone host from <c>FlaggedMissing</c> to <c>Scaffold</c>. No scene mutation.
        /// </summary>
        public static CopyPlan BuildPlan(GameObject ownedRoot, GameObject vendorSource,
                                         string[] typeNames, string[] force = null,
                                         IDictionary<string, string> vendorToOwned = null)
        {
            var plan = new CopyPlan();
            if (ownedRoot == null || vendorSource == null) return plan;

            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot    = ownedRoot.transform;

            // Resolve the selection types; surface failures (caller fails loud).
            var tr = TransplantCore.ResolveTypes(typeNames);
            plan.unresolvedTypeNames.AddRange(tr.unresolved);
            var selected = tr.resolved;   // matched by assignability below

            var forceSet = new HashSet<string>(force ?? Array.Empty<string>(), StringComparer.Ordinal);

            // Bone set: a vendor host transform is a "bone" iff it appears in any vendor SMR.bones[].
            var vendorBones = CollectBones(vendorSource);

            // ── Predicted resolution map (vendor identity → predicted outcome) ──────────────────────
            // What the executor WILL produce, recorded as we plan in topo order so a dependent's refs
            // resolve against already-planned identities. Components: vendor component → predicted to be
            // copied (true) or not (false). Transforms: vendor host transform → predicted dest path.
            var willCopyComponent = new Dictionary<Component, bool>();

            // Vendor host transform → predicted dest host path. Records EVERY level a recreate/scaffold
            // will mint (mirroring ScaffoldBuilder.EnsureHost's level-by-level recording), not just the
            // leaf, so a later constraint Sources[] ref at an intermediate scaffolded bone is not
            // mis-predicted as HardDepNull.
            var predictedDestPath = new Dictionary<Transform, string>();

            // Count-parity counter, keyed on an INDEX-AWARE host IDENTITY (a Transform) + type — NOT a
            // name path. Two same-named sibling vendor hosts resolve (via Counterpart/IndexedPath) to two
            // DISTINCT dest transforms; a name-path key would collide them into one counter (the second
            // wrongly PresentSkip) and miscount M off only the first same-named child. The identity is the
            // resolved dest Transform (InPlace) or the unique vendor host Transform (recreate/scaffold,
            // where the dest doesn't exist yet so its M is 0).
            var plannedSlots = new Dictionary<(Transform host, Type type), int>();

            // ── Build the work order in topo order over VRC table deps ──────────────────────────────
            // Seed = vendor components matching the selected types. Deep tier first (colliders → contacts
            // → physbones → constraints), conservative after. Within deep tier, hard-dep referents are
            // pulled into the plan if not already present (the dedup lever).
            var allVendor = vendorSource.GetComponentsInChildren<Component>(true);

            var seed = new List<Component>();
            foreach (var c in allVendor)
            {
                if (c == null) continue;
                if (MatchesSelection(c, selected)) seed.Add(c);
            }

            // Partition the seed by topo group.
            var seedColliders   = new List<Component>();
            var seedContacts    = new List<Component>();
            var seedPhysBones   = new List<Component>();
            var seedConstraints = new List<Component>();
            var seedConservative = new List<Component>();
            foreach (var c in seed)
            {
                switch (Bucket(c))
                {
                    case TopoGroup.Collider:     seedColliders.Add(c);    break;
                    case TopoGroup.Contact:      seedContacts.Add(c);     break;
                    case TopoGroup.PhysBone:     seedPhysBones.Add(c);    break;
                    case TopoGroup.Constraint:   seedConstraints.Add(c);  break;
                    default:                     seedConservative.Add(c); break;
                }
            }

            // Track which vendor components have already been planned (avoid double-planning a pulled dep).
            var planned = new HashSet<Component>();

            // Deep tier in dependency order: colliders/contacts (no deps) → physbones (dep: colliders) →
            // constraints (dep: Sources). A physbone's hard-dep colliders are pulled here if not seeded.
            foreach (var c in seedColliders)   PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);
            foreach (var c in seedContacts)    PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);
            foreach (var c in seedPhysBones)   PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);
            foreach (var c in seedConstraints) PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);

            // Conservative tier last, so a conservative ref to a deep component would resolve via the map.
            foreach (var c in seedConservative) PlanConservativeComponent(c, plan, vendorRoot, ourRoot, plannedSlots, planned, vendorToOwned);

            return plan;
        }

        // ── Topo bucketing ──────────────────────────────────────────────────────────────────────────

        static TopoGroup Bucket(Component c)
        {
            var d = VrcComponentTable.Lookup(c);
            return d == null ? TopoGroup.Conservative : d.group;
        }

        // ── Deep-tier planning ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Plan one deep-tier component (and recursively pull its hard-dep referents). Records its
        /// predicted outcome into the resolution map BEFORE recording ref predictions, so a later
        /// dependent reads a consistent map. Count-parity per (predicted dest host path, type).
        /// </summary>
        static void PlanDeepComponent(Component vc, bool pulledAsHardDep, CopyPlan plan,
                                      Transform vendorRoot, Transform ourRoot, HashSet<Transform> vendorBones,
                                      HashSet<string> forceSet,
                                      Dictionary<Component, bool> willCopyComponent,
                                      Dictionary<Transform, string> predictedDestPath,
                                      Dictionary<(Transform host, Type type), int> plannedSlots,
                                      HashSet<Component> planned,
                                      IDictionary<string, string> vendorToOwned)
        {
            if (vc == null || planned.Contains(vc)) return;
            planned.Add(vc);

            var d = VrcComponentTable.Lookup(vc);   // non-null (deep tier)
            var step = new CopyStep
            {
                vendorComponent = vc,
                componentType   = vc.GetType(),
                vendorHostPath  = RelativePath(vendorRoot, vc.transform),
                isDeepTier      = true,
                pulledAsHardDep = pulledAsHardDep,
            };

            // ── Host resolution (predicted, no mutation) ────────────────────────────────────────────
            // parityHost is the INDEX-AWARE identity the count-parity counter keys on: the resolved dest
            // Transform when it exists (InPlace), else the unique vendor host Transform (recreate/scaffold,
            // whose dest doesn't exist yet → M is 0). m is the initial dest count of this type.
            Transform vendorHost = vc.transform;
            Transform dstHost = CounterpartRec(vendorRoot, ourRoot, vendorHost, vendorToOwned, plan);

            Transform parityHost;
            int m;
            bool isBone = vendorBones.Contains(vendorHost);
            step.isBone = isBone;   // deep-tier classification, surfaced in the flagged-missing note
            Transform leafParentDst = (vendorHost.parent != null)
                ? CounterpartRec(vendorRoot, ourRoot, vendorHost.parent, vendorToOwned, plan) : null;
            if (dstHost != null)
            {
                step.action = CopyAction.InPlace;
                step.destHostPath = RelativePath(ourRoot, dstHost);
                parityHost = dstHost;                               // index-aware resolved dest
                m = ComponentCountOn(dstHost, step.componentType);
            }
            else if (d.leafRecreateEligible && !isBone && vendorHost.parent != null && leafParentDst != null)
            {
                // Non-bone leaf anchor (Col_*/offset GO) whose PARENT bone counterpart exists → recreate.
                step.action = CopyAction.RecreateLeaf;
                step.destHostPath = PredictedRecreatePath(vendorRoot, ourRoot, vendorHost);
                parityHost = vendorHost;                            // dest minted → unique vendor identity
                m = 0;
            }
            else if (forceSet.Contains(step.ForceKey))
            {
                // Missing bone (or missing-host non-recreatable) named in force → depth-N scaffold. The
                // executor scaffolds via ScaffoldBuilder.EnsureHost, which mirrors the vendor root→host
                // chain by INDEXED path under ourRoot — so the predicted dest path is that same chain.
                step.action = CopyAction.Scaffold;
                step.destHostPath = ScaffoldDestPath(vendorRoot, vendorHost);
                parityHost = vendorHost;                            // dest minted → unique vendor identity
                m = 0;
            }
            else
            {
                // Missing host, not recreatable, not forced → flagged-missing (PASS, never build).
                step.action = CopyAction.FlaggedMissing;
                step.destHostPath = null;
                plan.flaggedMissingHosts.Add(step.ForceKey);
                willCopyComponent[vc] = false;
                plan.steps.Add(step);
                return;
            }

            // ── Count-parity per (index-aware host identity, type) ──────────────────────────────────
            // Plan N steps total per (host,type) group: the first max(0,N-M) get the copy action, the
            // rest PresentSkip. Realized by comparing slots already consumed in this run against M.
            var parityKey = (parityHost, step.componentType);
            int alreadyConsumed = plannedSlots.TryGetValue(parityKey, out var pc) ? pc : 0;
            plannedSlots[parityKey] = alreadyConsumed + 1;

            if (alreadyConsumed < m)
            {
                // This vendor slot is covered by an already-present dest component → PresentSkip
                // (host is known/present; destHostPath already set above).
                step.action = CopyAction.PresentSkip;
                willCopyComponent[vc] = false;
                plan.steps.Add(step);
                return;
            }

            // We WILL copy this component. For recreate/scaffold, register every vendor-chain level the
            // executor will mint, so a later dep at an INTERMEDIATE scaffolded bone resolves (not just the
            // leaf host). InPlace hosts already have counterparts (deps resolve via Counterpart directly,
            // before WillTransformExist is consulted), so they need no registration.
            willCopyComponent[vc] = true;
            if (step.action == CopyAction.RecreateLeaf || step.action == CopyAction.Scaffold)
                RegisterPredictedChain(vendorRoot, vendorHost, predictedDestPath);

            // ── Hard-dep follow + criticality predictions ───────────────────────────────────────────
            // For physbones, pull referenced colliders into the plan FIRST (the dedup lever), then predict
            // each entry: copied → resolves; not copied → HardDepNull (flag). Constraints: Sources[].
            foreach (var field in d.hardDepFieldPaths)
            {
                if (field.IndexOf("[]", StringComparison.Ordinal) >= 0)
                    PlanTokenHardDep(vc, field, plan, vendorRoot, ourRoot, vendorBones, forceSet,
                                     willCopyComponent, predictedDestPath, plannedSlots, planned, step, vendorToOwned);
                else
                    PlanArrayHardDep(vc, field, plan, vendorRoot, ourRoot, vendorBones, forceSet,
                                     willCopyComponent, predictedDestPath, plannedSlots, planned, step, vendorToOwned);
            }

            // Soft deps: a referent that is absent under our hierarchy is silently dropped (no flag).
            foreach (var field in d.softDepFieldPaths)
                PredictSoftDeps(vc, field, vendorRoot, ourRoot, plan, step, vendorToOwned);

            // Promote to HardDepNull action if any hard-dep entry was predicted null on a copied component.
            foreach (var rp in step.refs)
                if (rp.disposition == RefDisposition.HardDepNull)
                {
                    step.action = CopyAction.HardDepNull;
                    plan.verifyMayBlockBuild.Add(step.ForceKey + " :: hard-dep '" + rp.fieldPath +
                                                 "' → '" + rp.targetVendorPath + "' will be null");
                }

            plan.steps.Add(step);
        }

        /// <summary>Plan a plain array-of-object-refs hard dep (e.g. physbone <c>colliders</c>).</summary>
        static void PlanArrayHardDep(Component vc, string field, CopyPlan plan,
                                     Transform vendorRoot, Transform ourRoot, HashSet<Transform> vendorBones,
                                     HashSet<string> forceSet,
                                     Dictionary<Component, bool> willCopyComponent,
                                     Dictionary<Transform, string> predictedDestPath,
                                     Dictionary<(Transform host, Type type), int> plannedSlots,
                                     HashSet<Component> planned, CopyStep step,
                                     IDictionary<string, string> vendorToOwned)
        {
            var so = new SerializedObject(vc);
            var prop = FindProperty(so, field);
            if (prop == null) { plan.tableDriftOffenders.Add(DriftMsg(step, field)); return; }
            if (!prop.isArray) return;

            for (int i = 0; i < prop.arraySize; i++)
            {
                var el = prop.GetArrayElementAtIndex(i);
                if (el.propertyType != SerializedPropertyType.ObjectReference) continue;
                var refComp = el.objectReferenceValue as Component;
                FollowHardDepRef(refComp, field, plan, vendorRoot, ourRoot, vendorBones, forceSet,
                                 willCopyComponent, predictedDestPath, plannedSlots, planned, step, vendorToOwned);
            }
        }

        /// <summary>
        /// Plan a "Collection[].Field" token hard dep (VRC constraint <c>Sources[].SourceTransform</c>):
        /// iterate the array property, follow Field on each element. The referent is a Transform, so the
        /// "copied" notion is whether its host transform is reachable; a constraint source pointing at an
        /// uncopiable vendor transform is a HardDepNull.
        /// </summary>
        static void PlanTokenHardDep(Component vc, string token, CopyPlan plan,
                                     Transform vendorRoot, Transform ourRoot, HashSet<Transform> vendorBones,
                                     HashSet<string> forceSet,
                                     Dictionary<Component, bool> willCopyComponent,
                                     Dictionary<Transform, string> predictedDestPath,
                                     Dictionary<(Transform host, Type type), int> plannedSlots,
                                     HashSet<Component> planned, CopyStep step,
                                     IDictionary<string, string> vendorToOwned)
        {
            int br = token.IndexOf("[]", StringComparison.Ordinal);
            string arrayField = token.Substring(0, br);
            string elemField  = token.Substring(br + 2).TrimStart('.');

            var so = new SerializedObject(vc);
            var arrProp = FindProperty(so, arrayField);
            if (arrProp == null) { plan.tableDriftOffenders.Add(DriftMsg(step, token)); return; }
            if (!arrProp.isArray) return;

            for (int i = 0; i < arrProp.arraySize; i++)
            {
                var el = arrProp.GetArrayElementAtIndex(i);
                var fieldProp = el.FindPropertyRelative(elemField);
                if (fieldProp == null || fieldProp.propertyType != SerializedPropertyType.ObjectReference) continue;
                var o = fieldProp.objectReferenceValue;
                if (o == null) continue;   // implicit/self source — generic remap handles it, not a hard-dep null
                Transform t = AsTransform(o);
                if (t == null || !t.IsChildOf(vendorRoot)) continue;   // external/world-anchor → left for placement, not flagged

                // The source transform's counterpart must exist (or be scheduled to exist). For a constraint
                // source that's a transform (not a copied component), "will be present" = counterpart exists.
                var dt = CounterpartRec(vendorRoot, ourRoot, t, vendorToOwned, plan);
                if (dt == null && !WillTransformExist(t, predictedDestPath))
                {
                    step.refs.Add(new RefPrediction
                    {
                        fieldPath        = token,
                        targetVendorPath = RelativePath(vendorRoot, t),
                        disposition      = RefDisposition.HardDepNull,
                    });
                }
            }
        }

        /// <summary>
        /// Follow one hard-dep component referent: pull it into the plan (if a deep type and not yet
        /// planned), then predict whether the entry resolves (copied / present) or nulls (HardDepNull).
        /// </summary>
        static void FollowHardDepRef(Component refComp, string field, CopyPlan plan,
                                     Transform vendorRoot, Transform ourRoot, HashSet<Transform> vendorBones,
                                     HashSet<string> forceSet,
                                     Dictionary<Component, bool> willCopyComponent,
                                     Dictionary<Transform, string> predictedDestPath,
                                     Dictionary<(Transform host, Type type), int> plannedSlots,
                                     HashSet<Component> planned, CopyStep step,
                                     IDictionary<string, string> vendorToOwned)
        {
            if (refComp == null) return;
            Transform rt = refComp.transform;
            if (!rt.IsChildOf(vendorRoot) && rt != vendorRoot)
                return;   // external/shared referent (out of reach) → left untouched, not a hard-dep null

            // Pull the referent into the plan if it's deep-tier and not already planned (the dedup lever).
            if (VrcComponentTable.Lookup(refComp) != null && !planned.Contains(refComp))
                PlanDeepComponent(refComp, true, plan, vendorRoot, ourRoot, vendorBones, forceSet,
                                  willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);

            // Now predict the entry: copied → resolves through the session map; not copied → null + flag.
            bool willCopy = willCopyComponent.TryGetValue(refComp, out var wc) && wc;
            bool willBePresent = willCopy ||
                                 CounterpartRec(vendorRoot, ourRoot, rt, vendorToOwned, plan) != null;
            if (!willBePresent)
            {
                step.refs.Add(new RefPrediction
                {
                    fieldPath        = field,
                    targetVendorPath = RelativePath(vendorRoot, rt),
                    disposition      = RefDisposition.HardDepNull,
                });
            }
        }

        /// <summary>Predict soft-dep entries (e.g. <c>ignoreTransforms</c>): absent referent → silently dropped, no flag.</summary>
        static void PredictSoftDeps(Component vc, string field, Transform vendorRoot, Transform ourRoot, CopyPlan plan, CopyStep step,
                                    IDictionary<string, string> vendorToOwned)
        {
            var so = new SerializedObject(vc);
            var prop = FindProperty(so, field);
            if (prop == null) { plan.tableDriftOffenders.Add(DriftMsg(step, field)); return; }
            if (!prop.isArray) return;

            for (int i = 0; i < prop.arraySize; i++)
            {
                var el = prop.GetArrayElementAtIndex(i);
                if (el.propertyType != SerializedPropertyType.ObjectReference) continue;
                var o = el.objectReferenceValue;
                if (o == null) continue;
                Transform t = AsTransform(o);
                if (t == null || !t.IsChildOf(vendorRoot)) continue;   // external → left untouched
                if (CounterpartRec(vendorRoot, ourRoot, t, vendorToOwned, plan) == null)
                {
                    step.refs.Add(new RefPrediction
                    {
                        fieldPath        = field,
                        targetVendorPath = RelativePath(vendorRoot, t),
                        disposition      = RefDisposition.SoftDepDropped,
                    });
                }
            }
        }

        // ── Conservative-tier planning ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Plan one conservative (non-VRC) component: host present → planned copy (InPlace) under
        /// count-parity; host absent → FlaggedMissing. NEVER scaffold / follow-dep. No ref predictions
        /// (the generic remapper carries internal refs at execution time; nothing predicted here).
        /// </summary>
        static void PlanConservativeComponent(Component vc, CopyPlan plan,
                                              Transform vendorRoot, Transform ourRoot,
                                              Dictionary<(Transform host, Type type), int> plannedSlots,
                                              HashSet<Component> planned,
                                              IDictionary<string, string> vendorToOwned)
        {
            if (vc == null || planned.Contains(vc)) return;
            planned.Add(vc);

            var step = new CopyStep
            {
                vendorComponent = vc,
                componentType   = vc.GetType(),
                vendorHostPath  = RelativePath(vendorRoot, vc.transform),
                isDeepTier      = false,
            };

            Transform dstHost = CounterpartRec(vendorRoot, ourRoot, vc.transform, vendorToOwned, plan);
            if (dstHost == null)
            {
                step.action = CopyAction.FlaggedMissing;
                step.destHostPath = null;
                plan.flaggedMissingHosts.Add(step.ForceKey);
                plan.steps.Add(step);
                return;
            }

            step.destHostPath = RelativePath(ourRoot, dstHost);

            // Count-parity per (index-aware dest host identity, type). VRCFury features co-host many
            // VF.Model.VRCFury on one GO, contacts/colliders stack — so a boolean "type present → skip"
            // would drop all but one. A conservative host is always an existing dest Transform (else it
            // is FlaggedMissing above), so dstHost is the index-aware identity and M counts off it.
            var parityKey = (dstHost, step.componentType);
            int m = ComponentCountOn(dstHost, step.componentType);
            int alreadyConsumed = plannedSlots.TryGetValue(parityKey, out var pc) ? pc : 0;
            plannedSlots[parityKey] = alreadyConsumed + 1;

            step.action = alreadyConsumed < m ? CopyAction.PresentSkip : CopyAction.InPlace;
            plan.steps.Add(step);
        }

        // ── Prediction helpers (read-only) ────────────────────────────────────────────────────────

        /// <summary>A name matches by assignability against any resolved type (base name catches subclass).</summary>
        static bool MatchesSelection(Component c, List<Type> selected)
        {
            var ct = c.GetType();
            foreach (var t in selected) if (t.IsAssignableFrom(ct)) return true;
            return false;
        }

        /// <summary>The set of every transform referenced by any vendor SkinnedMeshRenderer.bones[].</summary>
        static HashSet<Transform> CollectBones(GameObject vendorSource)
        {
            var set = new HashSet<Transform>();
            foreach (var smr in vendorSource.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                var bones = smr.bones;
                if (bones == null) continue;
                foreach (var b in bones) if (b != null) set.Add(b);
            }
            return set;
        }

        /// <summary>
        /// Count of components of <paramref name="type"/> already present on the resolved dest host
        /// Transform — the count-parity M. Counts off the INDEX-AWARE resolved Transform (not a name
        /// <c>Find</c>), so duplicate-named siblings each count their own host.
        /// </summary>
        static int ComponentCountOn(Transform host, Type type)
        {
            if (host == null) return 0;
            int n = 0;
            foreach (var c in host.GetComponents(type)) if (c != null) n++;
            return n;
        }

        /// <summary>Whether a vendor transform is scheduled to exist on the dest side (recreated/scaffolded by a prior step).</summary>
        static bool WillTransformExist(Transform vendorTransform, Dictionary<Transform, string> predictedDestPath)
            => predictedDestPath.ContainsKey(vendorTransform);

        /// <summary>Predicted dest path of a recreated leaf anchor: parent counterpart path + "/" + vendor leaf name.</summary>
        static string PredictedRecreatePath(Transform vendorRoot, Transform ourRoot, Transform vendorHost)
        {
            var parentDst = RemapReferencesByPath.Counterpart(vendorRoot, ourRoot, vendorHost.parent);
            string parentPath = RelativePath(ourRoot, parentDst);
            return string.IsNullOrEmpty(parentPath) ? vendorHost.name : parentPath + "/" + vendorHost.name;
        }

        /// <summary>
        /// Predicted dest path of a force-scaffolded host: the same root→host segment names the executor's
        /// <see cref="ScaffoldBuilder.EnsureHost"/> will mint under <paramref name="ourRoot"/> by indexed
        /// path. The vendor segment names ARE the dest names (EnsureHost reuses-or-mints each level), so a
        /// vendor-relative path under our root is exactly what the executor produces.
        /// </summary>
        static string ScaffoldDestPath(Transform vendorRoot, Transform vendorHost)
            => RelativePath(vendorRoot, vendorHost);

        /// <summary>
        /// Record every vendor-chain level the executor will mint for this host into
        /// <paramref name="predictedDestPath"/>, mirroring <see cref="ScaffoldBuilder.EnsureHost"/>'s
        /// level-by-level recording (predicted, no mutation). For RecreateLeaf this is the parent chain to
        /// the leaf; for Scaffold it is the full root→host chain; for InPlace the chain already exists but
        /// the leaf is still recorded for completeness. Without this, a later constraint
        /// <c>Sources[].SourceTransform</c> pointing at an INTERMEDIATE scaffolded bone would be
        /// mis-predicted as HardDepNull (a spurious "verify — may block build" → preview≠execute).
        /// </summary>
        static void RegisterPredictedChain(Transform vendorRoot, Transform vendorHost,
                                           Dictionary<Transform, string> predictedDestPath)
        {
            if (vendorHost == null) return;

            // Root-exclusive vendor chain, top → host (matches EnsureHost's walk).
            var chain = new List<Transform>();
            for (var p = vendorHost; p != null && p != vendorRoot; p = p.parent) chain.Add(p);
            chain.Reverse();

            // Build each level's predicted dest path from the vendor segment names under our root.
            var sb = new StringBuilder();
            foreach (var seg in chain)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(seg.name);
                predictedDestPath[seg] = sb.ToString();
            }
        }

        static Transform AsTransform(UnityEngine.Object o)
            => o is Component c ? c.transform : (o is GameObject g ? g.transform : null);

        /// <summary>
        /// Rename-aware <see cref="RemapReferencesByPath.Counterpart"/> that RECORDS an A1 ambiguous-rename
        /// failReason into <paramref name="plan"/> (deduped). Every planner host-resolution decision routes
        /// through here so a pathological vendorToOwned surfaces as a door-level FAIL in BOTH whatIf and execute,
        /// rather than a whatIf-only PASS (preview ≠ execute). A plain missing path (failReason == null)
        /// records nothing — it stays a normal mint/flag decision.
        /// </summary>
        static Transform CounterpartRec(Transform vendorRoot, Transform ourRoot, Transform srcTarget,
                                        IDictionary<string, string> vendorToOwned, CopyPlan plan)
        {
            var dt = RemapReferencesByPath.Counterpart(vendorRoot, ourRoot, srcTarget, vendorToOwned, out var fr);
            if (fr != null && !plan.renameAmbiguities.Contains(fr)) plan.renameAmbiguities.Add(fr);
            return dt;
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

        /// <summary>Named offender for VRC-table / SDK drift on a declared dependency field-path.</summary>
        static string DriftMsg(CopyStep step, string fieldPath)
            => (step.componentType != null ? step.componentType.Name : "(null)") + "." + fieldPath +
               " on '" + step.vendorHostPath + "' (no such SerializedProperty — VRC table / SDK drift)";

        /// <summary>
        /// First SerializedProperty among casing candidates for a field name (the SDK's serialized casing
        /// varies). Returns null when NONE match — a declared table field that no longer exists. Callers
        /// MUST treat null on a declared dependency field as table/SDK drift and record a named offender
        /// (<see cref="DriftMsg"/>) rather than silently dropping the dependency.
        /// </summary>
        static SerializedProperty FindProperty(SerializedObject so, string field)
        {
            var p = so.FindProperty(field);
            if (p != null) return p;
            // Probe common casings (m_Field / lower-first) as a backstop.
            char[] chars = field.ToCharArray();
            if (chars.Length > 0)
            {
                chars[0] = char.IsUpper(chars[0]) ? char.ToLowerInvariant(chars[0]) : char.ToUpperInvariant(chars[0]);
                p = so.FindProperty(new string(chars));
                if (p != null) return p;
            }
            return so.FindProperty("m_" + field);
        }

        // ── Executor / Run ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the plan via <see cref="BuildPlan"/> and either preview it (<paramref name="whatIf"/>
        /// true → report + mutate nothing) or REPLAY it against the scene under a single Undo group.
        /// Both paths consume the SAME <see cref="CopyPlan"/> object, so preview and execution cannot
        /// diverge. Returns a one-line PASS/FAIL summary ending with the RunLog path
        /// (<c>… => RESULT | log=&lt;path&gt;</c>); also Debug.Log/LogError's it and writes a
        /// <c>copy-components</c> RunLog.
        ///
        /// PASS/FAIL contract:
        ///   - A flagged-missing HOST is PASS — surfaced as a prominent named list the operator iterates.
        ///   - A nulled ref on a component we DID copy is surfaced separately as "verify — may block
        ///     build" (a null VRCF propBone / constraint source aborts the downstream build) — NOT a FAIL.
        ///   - tableDriftOffenders are surfaced too.
        ///   - FAIL only on: a vendor-source leak, an AddComponent/scaffold failure, an unresolved
        ///     hard ref that leaked, or an unresolved type name.
        /// </summary>
        /// <param name="vendorToOwned">Optional <c>vendorName ⇒ ownedName</c> correspondence (source-name key →
        /// destination-name value) for destination-side child lookups — the armature-root name mismatch case
        /// (owned <c>Armature.1</c> vs vendor <c>Armature</c>). Because every host/ref path resolves from the
        /// reach root, one entry (<c>"Armature" ⇒ "Armature.1"</c>) covers hosts AND relocated-component
        /// anchor refs uniformly. Validated once here (drop empties, reject non-injective → FAIL), then
        /// threaded to host prediction / scaffold / ref-remap. Empty/absent/null ⇒ byte-identical to today.
        /// Direction/case rationale is canonical at <see cref="IndexedPath.Substitute"/> /
        /// <see cref="IndexedPath.ValidateRenameMap"/>.
        /// <c>ConformRenderers</c> runs the OPPOSITE direction under the same invariant, so the two maps are
        /// not interchangeable — see <see cref="IndexedPath.Substitute"/> before reusing one for both.</param>
        public static string Run(GameObject ownedRoot, GameObject vendorSource,
                                 string[] typeNames, string[] force = null,
                                 IDictionary<string, string> vendorToOwned = null, bool whatIf = false)
        {
            string label = ownedRoot != null ? TransplantCore.Sanitize(ownedRoot.name) : "null-instance";
            var log = new RunLog("copy-components")
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

            // Validate the rename map ONCE before any mutation: a non-injective map cannot address a unique
            // dst sibling → FAIL loud, naming the colliding keys (A2). Empty-after-cleaning ⇒ null ⇒ no-op.
            var cleanRename = IndexedPath.ValidateRenameMap(vendorToOwned, out var collidingKeys);
            if (collidingKeys != null)
            {
                log.result = "FAIL";
                log.error  = "non-injective vendorToOwned (keys collide onto one dest name)";
                log.Offender("vendorToOwned non-injective: keys [" + string.Join(", ", collidingKeys) +
                             "] map onto the same value — cannot address a unique dst sibling");
                return TransplantCore.Finish(log, label);
            }
            // The planner's predicted dest-path REPORT strings are built from vendor segment names (report-only;
            // no decision reads them — real resolution goes through the rename-aware Counterpart/EnsureHost). A
            // note keeps a vendor-named 'Armature/…' label from being misread when a rename is active.
            if (cleanRename != null)
                log.Note("vendorToOwned active (" + cleanRename.Count +
                         " entr(y/ies)) — predicted dest-path labels stay vendor-named; minted GOs use the mapped name");

            // ── Build the plan (pure; both whatIf and execute consume this exact object) ─────────────
            var plan = BuildPlan(ownedRoot, vendorSource, typeNames, force, cleanRename);

            // Type-name resolution is a hard precondition — fail loud before any mutation.
            if (plan.unresolvedTypeNames.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "unresolved type names";
                foreach (var u in plan.unresolvedTypeNames) log.Offender("type-name: " + u);
                return TransplantCore.Finish(log, label);
            }

            // Plan-level rollup counts + planner predictions echoed straight into the log (division of
            // labor: the planner PREDICTS; the executor REPORTS actual + carries these through).
            log.Count("deepInPlace",    plan.DeepInPlace);
            log.Count("recreateLeaf",   plan.RecreateLeaf);
            log.Count("scaffold",       plan.Scaffold);
            log.Count("presentSkip",    plan.PresentSkip);
            log.Count("flaggedMissing", plan.FlaggedMissing);
            log.Count("conservative",   plan.Conservative);

            // Iterate steps (not the flaggedMissingHosts string list) so each note can carry the
            // deep-tier [holder]/[bone] classification. Conservative hosts stay untagged — they have no
            // scaffold path, so a [holder] label would advertise a force the executor ignores. The note
            // prefix and the appended ForceKey are otherwise identical to before (readers unaffected).
            foreach (var step in plan.steps)
            {
                if (step.action != CopyAction.FlaggedMissing) continue;
                string cls = step.isDeepTier ? (step.isBone ? " [bone]" : " [holder]") : "";
                log.Note("flagged-missing host" + cls +
                         " (PASS — read & iterate, force/re-prune/accept): " + step.ForceKey);
            }
            foreach (var d in plan.tableDriftOffenders)
                log.Offender("VRC-table/SDK drift: " + d);

            // Table/SDK drift is a hard precondition for BOTH modes — fail loud before any mutation,
            // mirroring the unresolvedTypeNames guard above. The whatIf path below would FAIL on drift
            // anyway, but execute must NOT fall through and mutate the scene (knowingly skipping a
            // declared dependency field) only to set FAIL at the end — that leaves a partially-mutated
            // avatar. Guard here so NO component is created on drift, in either mode.
            if (plan.tableDriftOffenders.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "VRC-table/SDK drift — declared dependency field(s) no longer resolve";
                return TransplantCore.Finish(log, label);
            }

            // A1 ambiguous-rename hard precondition — FAIL both modes before any mutation, so a pathological
            // vendorToOwned the planner would otherwise read as "mint" (whatIf PASS) matches execute's EnsureHost
            // A1 FAIL. Named, deterministic; nothing is mutated.
            if (plan.renameAmbiguities.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "ambiguous vendorToOwned — a mapped name cannot address a unique dest counterpart";
                foreach (var a in plan.renameAmbiguities) log.Offender("vendorToOwned ambiguity: " + a);
                return TransplantCore.Finish(log, label);
            }

            // The planner's PREDICTED verify-may-block-build notes are surfaced only in whatIf (preview).
            // In execute mode the realized remap pass (below) re-derives the SAME gaps from the actual
            // null refs, so emitting the predicted ones here too would double-log every hard-dep null.
            if (whatIf)
            {
                foreach (var v in plan.verifyMayBlockBuild)
                    log.Note("verify — may block build: " + v);

                // Preview only — mutate nothing. Drift already early-returned FAIL above; flagged hosts
                // and verify-may-block are PASS-with-notes.
                log.result = "PASS";
                return TransplantCore.Finish(log, label);
            }

            // ── EXECUTE: replay the plan step-by-step under one Undo group ───────────────────────────
            var session = new SessionMap();
            var created = new List<(Component vendor, Component ours, CopyStep step)>();
            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot     = ownedRoot.transform;

            // Hoisted so the finally collapses the group on EVERY exit (success, early-FAIL return, or a
            // mid-run exception) — a single grouped undo regardless of how the run ends.
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("CopyComponents");
            int group = Undo.GetCurrentGroup();

            try
            {
                // Pass 0 — register PRESENT-SKIP vendor components into the session map, BEFORE Pass 1
                // creates anything (so GetComponents sees only pre-existing dst components). A created
                // component that references a present-skipped same-type sibling would otherwise fall
                // through to the generic path-remap, whose GetComponent(type) returns the FIRST of N
                // co-hosted same-type components — binding the wrong sibling (the stray-duplicate-Col_*
                // case). The first M slots of a (host,type) group are exactly the M pre-existing dst
                // components in child order; pairing by a running occurrence counter k maps each
                // present-skip vendor component to its corresponding dst component. Running before any
                // creation makes this order-robust (no created component perturbs the counts).
                var presentSkipSlot = new Dictionary<(Transform host, Type type), int>();
                foreach (var step in plan.steps)
                {
                    if (step.action != CopyAction.PresentSkip || step.vendorComponent == null) continue;
                    // Present-skip implies host + component already exist — EnsureHost reuses, never mints.
                    // vendorToOwned is load-bearing HERE: omitting it re-resolves the present-skip host under the
                    // vendor name, silently minting a parallel 'Armature' and mis-mapping present-skip slots.
                    Transform host = ScaffoldBuilder.EnsureHost(vendorRoot, ourRoot, step.vendorComponent.transform,
                                                                out string psFail, session, "CopyComponents", cleanRename);
                    if (host == null)
                    {
                        // Host unresolvable → skip mapping safely (no worse than today). Under an active rename
                        // an A1 ambiguity here would otherwise be silent — name it so the present-skip slot gap
                        // is legible.
                        if (psFail != null && cleanRename != null)
                            log.Warning("present-skip host '" + step.vendorHostPath + "' unresolved under rename — " + psFail);
                        continue;
                    }
                    var key = (host, step.componentType);
                    int k = presentSkipSlot.TryGetValue(key, out var kc) ? kc : 0;
                    var comps = host.GetComponents(step.componentType);
                    if (k < comps.Length)
                    {
                        session.AddComponent(step.vendorComponent, comps[k]);
                        presentSkipSlot[key] = k + 1;
                    }
                    // k >= comps.Length: more present-skips than pre-existing dst components → skip safely.
                }

                // Pass 1 — create every copied component on its host, in the plan's topo order, recording
                // vendor→ours identities into the session map. Refs are NOT rebound yet: the map must be
                // fully populated first so a dependent (physbone colliders[], constraint Sources[]) hits a
                // mapped target rather than spuriously path-remapping or flagging.
                foreach (var step in plan.steps)
                {
                    switch (step.action)
                    {
                        case CopyAction.PresentSkip:
                        case CopyAction.FlaggedMissing:
                            continue;   // no component created (flagged-missing host = PASS, reported via notes)
                        case CopyAction.HardDepNull:
                        case CopyAction.InPlace:
                        case CopyAction.RecreateLeaf:
                        case CopyAction.Scaffold:
                            break;      // copy actions — fall through
                        default:
                            continue;
                    }
                    if (step.vendorComponent == null) continue;

                    // Resolve the destination host UNCONDITIONALLY via EnsureHost, regardless of the
                    // step's action. The action also doubles as a criticality flag — a forced Scaffold
                    // (or a RecreateLeaf) whose component ALSO has an uncopied hard dep is PROMOTED to
                    // HardDepNull by the planner — so routing the mint/reuse decision off the action
                    // would send a genuinely-missing-and-forced host down a no-mint path and FAIL a
                    // legitimate force. EnsureHost is the single correct placement primitive: for an
                    // already-existing chain it reuses down via the same index-aware IndexedPath walk
                    // Counterpart uses and returns the identical existing host (mints nothing); for a
                    // recreate/scaffold it mints the transforms-only chain (vendor verbatim local TRS,
                    // bounded by reach) and records every level into the session map. Steps that create
                    // NO component (PresentSkip / FlaggedMissing) were already skipped above.
                    Transform vendorHost = step.vendorComponent.transform;
                    Transform host = ScaffoldBuilder.EnsureHost(vendorRoot, ourRoot, vendorHost,
                                                                out string hostFail, session, "CopyComponents", cleanRename);
                    if (host == null)
                    {
                        log.result = "FAIL";
                        log.error  = "scaffold/host resolution failed";
                        log.Offender(step.componentType.Name + " on '" + step.vendorHostPath +
                                     "': could not resolve/mint dest host (action=" + step.action + ") — " + hostFail);
                        return TransplantCore.Finish(log, label);
                    }
                    // EnsureHost records the host transform; record an InPlace host too so a later dep
                    // pointing at this exact bone resolves via the map as well as via Counterpart.
                    session.AddTransform(vendorHost, host);

                    var newComp = Undo.AddComponent(host.gameObject, step.componentType);
                    if (newComp == null)
                    {
                        log.result = "FAIL";
                        log.error  = "AddComponent failed";
                        log.Offender(step.componentType.Name + " on '" + step.vendorHostPath +
                                     "': AddComponent returned null");
                        return TransplantCore.Finish(log, label);
                    }
                    EditorUtility.CopySerialized(step.vendorComponent, newComp);
                    session.AddComponent(step.vendorComponent, newComp);
                    created.Add((step.vendorComponent, newComp, step));
                }

                // Pass 2 — ref-remap on EVERY created component, now that the map is fully populated.
                // Per object-ref: map-hit (SessionMap component/transform) → use the mapped target; else
                // path-remap within reach (RemapReferencesByPath.Remap); else it nulls + is flagged. This
                // single pass subsumes any explicit old→new collider map — physbone colliders[]
                // resolve through the SessionMap because colliders were created first by topo order.
                //
                // Soft-dep drops (e.g. ignoreTransforms whose bone was pruned) are realized here as inert
                // NULL slots, NOT compacted out of the array: a null ignoreTransforms entry is
                // behavior-inert (the physbone ignores nothing for that slot), the prior tool didn't
                // compact, and compaction would be unverified code.
                //
                // Reporting is classified per nulled path so execute matches preview (which already splits
                // SoftDepDropped-as-inert from hard-as-may-block): for a DEEP-tier component, a null whose
                // path starts with a declared softDepFieldPath is reported as inert (NOT counted may-block);
                // any other deep-tier null (a hard-dep field or an unclassified ref) keeps the "verify — may
                // block build" wording. A CONSERVATIVE-tier null is a genuine lost INTERNAL ref (no table
                // descriptor), kept as-is. All are NON-fatal "verify" signals per the PASS/FAIL contract —
                // the operator inspects and repairs at placement; nothing here FAILs.
                int conservativeNulledRefs = 0;
                foreach (var (vendor, ours, step) in created)
                {
                    if (ours == null) continue;
                    var nulledPaths = RemapBySession(ours, session, vendorRoot, ourRoot, cleanRename, log);
                    if (nulledPaths.Count == 0) continue;

                    if (step.isDeepTier)
                    {
                        var d = VrcComponentTable.Lookup(step.componentType);
                        int inert = 0, mayBlock = 0;
                        foreach (var p in nulledPaths)
                            if (IsSoftDepPath(d, p)) inert++; else mayBlock++;

                        if (mayBlock > 0)
                            log.Note("verify — may block build: " + step.componentType.Name + " '" +
                                     ours.gameObject.name + "' has " + mayBlock +
                                     " unresolved deep-tier ref(s) left null after remap");
                        if (inert > 0)
                            log.Note("soft-dep dropped (inert): " + step.componentType.Name + " '" +
                                     ours.gameObject.name + "' has " + inert +
                                     " soft-dep ref(s) dropped to null (behavior-inert — e.g. pruned ignoreTransforms)");
                    }
                    else
                    {
                        conservativeNulledRefs += nulledPaths.Count;
                        log.Note("verify — may block build: conservative " + step.componentType.Name + " '" +
                                 ours.gameObject.name + "' has " + nulledPaths.Count +
                                 " unresolved internal ref(s) left null after remap (lost under-reach ref)");
                    }
                }

                // Pass 3 — vendor-leak sweep (shared). Any ref on a created component still pointing INTO
                // the vendor source is one the remap failed to rebind → FAIL. This catches a hard ref that
                // leaked rather than nulled.
                var createdComps = new List<Component>(created.Count);
                foreach (var c in created) createdComps.Add(c.ours);
                int leaks = TransplantCore.SweepVendorLeaks(createdComps, vendorRoot, log);

                // Realized counts from the actual execution (alongside the plan rollup).
                int copied = 0;
                foreach (var c in createdComps) if (c != null) copied++;
                log.Count("componentsCreated", copied);
                log.Count("conservativeNulledRefs", conservativeNulledRefs);
                log.Count("vendorLeaks", leaks);

                // Drift already early-returned FAIL above (no mutation on drift), so the verdict here is
                // purely the vendor-leak sweep.
                log.result = leaks == 0 ? "PASS" : "FAIL";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error  = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                // Single grouped undo on EVERY exit path — success, an early-FAIL return inside the try,
                // or a mid-run exception — so a partial run is still one collapsible Undo step.
                Undo.CollapseUndoOperations(group);
            }

            return TransplantCore.Finish(log, label);
        }

        /// <summary>
        /// Whether a nulled SerializedProperty <paramref name="path"/> belongs to one of the descriptor's
        /// declared soft-dep fields (e.g. <c>ignoreTransforms</c> → <c>ignoreTransforms.Array.data[3]</c>).
        /// A soft-dep field name is the leading token before the first '.'; a property path starting with
        /// it is a soft-dep entry → its null is behavior-inert, not a may-block. False when the descriptor
        /// is null or no soft-dep field matches.
        /// </summary>
        static bool IsSoftDepPath(VrcComponentDescriptor d, string path)
        {
            if (d == null || string.IsNullOrEmpty(path)) return false;
            foreach (var f in d.softDepFieldPaths)
            {
                // softDepFieldPaths are bare field names (e.g. "ignoreTransforms"); the serialized path is
                // that name optionally followed by ".Array.data[i]". Match the field token exactly.
                if (path == f || path.StartsWith(f + ".", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Rebind every object reference on <paramref name="comp"/>: SessionMap hit (component or
        /// transform) wins; else delegate to <see cref="RemapReferencesByPath.Remap"/> for the
        /// path-remap-within-reach / null-if-missing behavior. Returns the SerializedProperty paths of
        /// refs left null (under-reach refs the remap could not resolve) so the caller can classify each
        /// against the component's table descriptor (inert soft-dep drop vs. may-block hard-dep null).
        /// </summary>
        static List<string> RemapBySession(Component comp, SessionMap session, Transform vendorRoot, Transform ourRoot,
                                            IDictionary<string, string> vendorToOwned, RunLog log)
        {
            var so = new SerializedObject(comp);
            so.Update();

            // First, the map-hit pass: rebind refs the session map knows about (copied components, minted
            // transforms). These are exactly the refs RemapReferencesByPath cannot resolve correctly on
            // its own — a copied collider lives at a NEW path under a recreated anchor, and a path-remap
            // would either miss it or bind the wrong one of N same-type components. After this pass, any
            // under-reach ref still pointing at a vendor object is handled by the generic path-remap below.
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
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            // Then the generic path-remap for whatever the session map didn't cover (refs whose target is
            // an un-copied but path-present transform under the dest, e.g. a physbone rootTransform that
            // wasn't itself a copied component). Refs outside reach are left untouched.
            so.Update();
            var rr = RemapReferencesByPath.Remap(so, vendorRoot, ourRoot, vendorToOwned);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(comp);
            if (rr.renameWarnings != null)
                foreach (var w in rr.renameWarnings)
                    log?.Warning("ambiguous rename on copied " + comp.GetType().Name + " '" +
                                 comp.gameObject.name + "': " + w);
            return rr.nulledPaths ?? new List<string>();
        }
    }
}
