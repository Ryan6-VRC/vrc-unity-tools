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

    /// <summary>One predicted decision in a <see cref="CopyPlan"/>.</summary>
    public sealed class CopyStep
    {
        public Component vendorComponent;
        public Type componentType;
        /// <summary>Root-relative path of the vendor host GO under the vendor source (for reporting / force keys).</summary>
        public string vendorHostPath;
        /// <summary>
        /// Predicted root-relative path of the destination host under our instance; null for
        /// <c>FlaggedMissing</c>. A report label the executor never reads: hosts resolve through the rename-aware
        /// <see cref="ScaffoldBuilder.EnsureHost"/>, and a predicted recreate or scaffold path stays vendor-named
        /// under a rename.
        /// </summary>
        public string destHostPath;
        public CopyAction action;
        /// <summary>True if the vendor component is a deep-tier (VRC table) type; false = conservative tier.</summary>
        public bool isDeepTier;
        /// <summary>True if this component was pulled in as a deep hard-dependency rather than listed by type.</summary>
        public bool pulledAsHardDep;
        /// <summary>
        /// Deep-tier only: true when the host appears in some vendor SMR.bones[], false for a non-bone holder.
        /// Drives the [bone]/[holder] flagged-missing note.
        /// </summary>
        public bool isBone;
        /// <summary>
        /// Deep-tier references predicted not to resolve. Empty for conservative tier and for fully-resolved
        /// deep components.
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
    /// The plan a <c>CopyComponents</c> run will execute, built without scene mutation. Steps are in topo order
    /// (colliders, contacts, physbones, constraints, then conservative) so a dependent's refs resolve against
    /// already-planned identities.
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
        /// Predicted hard-dep nulls on components we will copy; <see cref="CopyComponents.Run"/> surfaces them
        /// in whatIf as "verify — may block build".
        /// </summary>
        public readonly List<string> verifyMayBlockBuild = new List<string>();

        /// <summary>
        /// VRC-table / SDK drift: a deep-tier descriptor's dependency field-path that resolves to no
        /// SerializedProperty on the actual component (a mistyped table entry or an SDK field rename).
        /// </summary>
        public readonly List<string> tableDriftOffenders = new List<string>();

        /// <summary>
        /// Ambiguous-rename reasons from planner host resolution, deduped. <c>Counterpart</c> returns null for
        /// one, which the planner alone would read as "mint" and PASS a whatIf that execute's <c>EnsureHost</c>
        /// FAILs, so a non-empty list FAILs both modes before any mutation.
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

        int CountOf(CopyAction a, bool deep)
        {
            int n = 0;
            foreach (var s in steps) if (s.action == a && s.isDeepTier == deep) n++;
            return n;
        }

        public int DeepInPlace => CountOf(CopyAction.InPlace, true);

        /// <summary>
        /// Conservative-tier copies planned. A conservative host is always present (else it is FlaggedMissing),
        /// so in practice these are all InPlace.
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

        public string RollupLine()
        {
            return string.Format(
                "deepInPlace={0}, recreateLeaf={1}, scaffold={2}, presentSkip={3}, flaggedMissing={4}, hardDepNull={5}, conservative={6}",
                DeepInPlace, RecreateLeaf, Scaffold, PresentSkip, FlaggedMissing, HardDepNull, Conservative);
        }
    }

    /// <summary>
    /// Reproduces vendor components of the named types onto our rig: <see cref="BuildPlan"/> predicts with no
    /// scene mutation, <see cref="Run"/> previews or replays that plan.
    /// Contract: docs/unity-tools.md.
    /// </summary>
    [AgentTool]
    public static class CopyComponents
    {
        // ── Public plan builder ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the plan with no scene mutation. <paramref name="force"/> entries are <c>vendorRelativePath ::
        /// ComponentType</c> keys (from a prior plan's <see cref="CopyPlan.flaggedMissingHosts"/>) that
        /// promote a missing deep-tier host from <c>FlaggedMissing</c> to <c>Scaffold</c>.
        /// </summary>
        public static CopyPlan BuildPlan(GameObject ownedRoot, GameObject vendorSource,
                                         string[] typeNames, string[] force = null,
                                         IDictionary<string, string> vendorToOwned = null)
        {
            var plan = new CopyPlan();
            if (ownedRoot == null || vendorSource == null) return plan;

            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot    = ownedRoot.transform;

            var tr = TransplantCore.ResolveTypes(typeNames);
            plan.unresolvedTypeNames.AddRange(tr.unresolved);
            var selected = tr.resolved;

            var forceSet = new HashSet<string>(force ?? Array.Empty<string>(), StringComparer.Ordinal);

            var vendorBones = CollectBones(vendorSource);

            // ── Predicted resolution map (vendor identity → predicted outcome) ──────────────────────
            var willCopyComponent = new Dictionary<Component, bool>();

            // Vendor transforms the executor will mint (see RegisterPredictedChain).
            var predictedDestPath = new Dictionary<Transform, string>();

            // Count-parity counter, keyed on a host Transform rather than a name path: two same-named sibling
            // vendor hosts resolve to two distinct dest Transforms, and a name-path key would collide them (the
            // second wrongly PresentSkip). The Transform is the resolved dest (InPlace) or the unique vendor host
            // (recreate/scaffold, where the dest doesn't exist yet so its M is 0).
            var plannedSlots = new Dictionary<(Transform host, Type type), int>();

            // ── Build the work order in topo order over VRC table deps ──────────────────────────────
            var allVendor = vendorSource.GetComponentsInChildren<Component>(true);

            var seed = new List<Component>();
            foreach (var c in allVendor)
            {
                if (c == null) continue;
                if (MatchesSelection(c, selected)) seed.Add(c);
            }

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

            var planned = new HashSet<Component>();

            // Deep tier in dependency order: colliders/contacts (no deps) → physbones (dep: colliders) →
            // constraints (dep: Sources). A physbone's hard-dep colliders are pulled here if not seeded.
            foreach (var c in seedColliders)   PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);
            foreach (var c in seedContacts)    PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);
            foreach (var c in seedPhysBones)   PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);
            foreach (var c in seedConstraints) PlanDeepComponent(c, false, plan, vendorRoot, ourRoot, vendorBones, forceSet, willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);

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
        /// Plan one deep-tier component, recursively pulling its hard-dep referents. Its outcome enters the
        /// resolution map before its ref predictions, so a dependent reads a consistent map.
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
            Transform vendorHost = vc.transform;
            Transform dstHost = CounterpartRec(vendorRoot, ourRoot, vendorHost, vendorToOwned, plan);

            Transform parityHost;
            int m;
            bool isBone = vendorBones.Contains(vendorHost);
            step.isBone = isBone;
            Transform leafParentDst = (vendorHost.parent != null)
                ? CounterpartRec(vendorRoot, ourRoot, vendorHost.parent, vendorToOwned, plan) : null;
            if (dstHost != null)
            {
                step.action = CopyAction.InPlace;
                step.destHostPath = RelativePath(ourRoot, dstHost);
                parityHost = dstHost;
                m = ComponentCountOn(dstHost, step.componentType);
            }
            else if (d.leafRecreateEligible && !isBone && vendorHost.parent != null && leafParentDst != null)
            {
                // Non-bone leaf anchor (Col_*/offset GO) whose PARENT bone counterpart exists → recreate.
                step.action = CopyAction.RecreateLeaf;
                step.destHostPath = PredictedRecreatePath(vendorRoot, ourRoot, vendorHost);
                parityHost = vendorHost;
                m = 0;
            }
            else if (forceSet.Contains(step.ForceKey))
            {
                step.action = CopyAction.Scaffold;
                step.destHostPath = ScaffoldDestPath(vendorRoot, vendorHost);
                parityHost = vendorHost;
                m = 0;
            }
            else
            {
                step.action = CopyAction.FlaggedMissing;
                step.destHostPath = null;
                plan.flaggedMissingHosts.Add(step.ForceKey);
                willCopyComponent[vc] = false;
                plan.steps.Add(step);
                return;
            }

            // ── Count-parity per (index-aware host identity, type) ──────────────────────────────────
            // The first M vendor slots of a (host,type) group are PresentSkip; the rest keep the copy action.
            var parityKey = (parityHost, step.componentType);
            int alreadyConsumed = plannedSlots.TryGetValue(parityKey, out var pc) ? pc : 0;
            plannedSlots[parityKey] = alreadyConsumed + 1;

            if (alreadyConsumed < m)
            {
                step.action = CopyAction.PresentSkip;
                willCopyComponent[vc] = false;
                plan.steps.Add(step);
                return;
            }

            willCopyComponent[vc] = true;
            if (step.action == CopyAction.RecreateLeaf || step.action == CopyAction.Scaffold)
                RegisterPredictedChain(vendorRoot, vendorHost, predictedDestPath);

            // ── Hard-dep follow + criticality predictions ───────────────────────────────────────────
            foreach (var field in d.hardDepFieldPaths)
            {
                if (field.IndexOf("[]", StringComparison.Ordinal) >= 0)
                    PlanTokenHardDep(vc, field, plan, vendorRoot, ourRoot, vendorBones, forceSet,
                                     willCopyComponent, predictedDestPath, plannedSlots, planned, step, vendorToOwned);
                else
                    PlanArrayHardDep(vc, field, plan, vendorRoot, ourRoot, vendorBones, forceSet,
                                     willCopyComponent, predictedDestPath, plannedSlots, planned, step, vendorToOwned);
            }

            foreach (var field in d.softDepFieldPaths)
                PredictSoftDeps(vc, field, vendorRoot, ourRoot, plan, step, vendorToOwned);

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
        /// Plan a "Collection[].Field" token hard dep (VRC constraint <c>Sources[].SourceTransform</c>). The
        /// referent is a Transform, so a source with no counterpart and no scheduled mint is a HardDepNull.
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

            if (VrcComponentTable.Lookup(refComp) != null && !planned.Contains(refComp))
                PlanDeepComponent(refComp, true, plan, vendorRoot, ourRoot, vendorBones, forceSet,
                                  willCopyComponent, predictedDestPath, plannedSlots, planned, vendorToOwned);

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
        /// Plan one conservative component. No ref predictions: the generic remapper carries its refs at
        /// execution time.
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

            // Count-parity, not a boolean "type present → skip": VRCFury features co-host many VF.Model.VRCFury
            // on one GO and contacts/colliders stack, so a boolean would drop all but one.
            var parityKey = (dstHost, step.componentType);
            int m = ComponentCountOn(dstHost, step.componentType);
            int alreadyConsumed = plannedSlots.TryGetValue(parityKey, out var pc) ? pc : 0;
            plannedSlots[parityKey] = alreadyConsumed + 1;

            step.action = alreadyConsumed < m ? CopyAction.PresentSkip : CopyAction.InPlace;
            plan.steps.Add(step);
        }

        // ── Prediction helpers (read-only) ────────────────────────────────────────────────────────

        static bool MatchesSelection(Component c, List<Type> selected)
        {
            var ct = c.GetType();
            foreach (var t in selected) if (t.IsAssignableFrom(ct)) return true;
            return false;
        }

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

        /// <summary>The count-parity M: components of <paramref name="type"/> already on the resolved dest host.</summary>
        static int ComponentCountOn(Transform host, Type type)
        {
            if (host == null) return 0;
            int n = 0;
            foreach (var c in host.GetComponents(type)) if (c != null) n++;
            return n;
        }

        static bool WillTransformExist(Transform vendorTransform, Dictionary<Transform, string> predictedDestPath)
            => predictedDestPath.ContainsKey(vendorTransform);

        static string PredictedRecreatePath(Transform vendorRoot, Transform ourRoot, Transform vendorHost)
        {
            var parentDst = RemapReferencesByPath.Counterpart(vendorRoot, ourRoot, vendorHost.parent);
            string parentPath = RelativePath(ourRoot, parentDst);
            return string.IsNullOrEmpty(parentPath) ? vendorHost.name : parentPath + "/" + vendorHost.name;
        }

        /// <summary>
        /// Predicted dest path of a force-scaffolded host: the vendor-relative path, because
        /// <see cref="ScaffoldBuilder.EnsureHost"/> reuses or mints each level under the vendor segment name.
        /// Under a rename it mints the mapped name instead, and this label stays vendor-named.
        /// </summary>
        static string ScaffoldDestPath(Transform vendorRoot, Transform vendorHost)
            => RelativePath(vendorRoot, vendorHost);

        /// <summary>
        /// Record every root-to-host vendor-chain level for a host the executor will recreate or scaffold.
        /// Without this, a later constraint <c>Sources[].SourceTransform</c> at an intermediate scaffolded bone
        /// is mis-predicted as HardDepNull, a spurious "verify — may block build" that execute would not show.
        /// </summary>
        static void RegisterPredictedChain(Transform vendorRoot, Transform vendorHost,
                                           Dictionary<Transform, string> predictedDestPath)
        {
            if (vendorHost == null) return;

            var chain = new List<Transform>();
            for (var p = vendorHost; p != null && p != vendorRoot; p = p.parent) chain.Add(p);
            chain.Reverse();

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
        /// Rename-aware <see cref="RemapReferencesByPath.Counterpart"/> that records an ambiguous-rename
        /// failReason into <see cref="CopyPlan.renameAmbiguities"/>, deduped. Every planner host-resolution
        /// decision routes through here. A plain missing path has a null failReason and records nothing.
        /// </summary>
        static Transform CounterpartRec(Transform vendorRoot, Transform ourRoot, Transform srcTarget,
                                        IDictionary<string, string> vendorToOwned, CopyPlan plan)
        {
            var dt = RemapReferencesByPath.Counterpart(vendorRoot, ourRoot, srcTarget, vendorToOwned, out var fr);
            if (fr != null && !plan.renameAmbiguities.Contains(fr)) plan.renameAmbiguities.Add(fr);
            return dt;
        }

        static string RelativePath(Transform root, Transform t)
        {
            if (t == null || root == null) return null;
            if (t == root) return "";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != root; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static string DriftMsg(CopyStep step, string fieldPath)
            => (step.componentType != null ? step.componentType.Name : "(null)") + "." + fieldPath +
               " on '" + step.vendorHostPath + "' (no such SerializedProperty — VRC table / SDK drift)";

        /// <summary>
        /// First SerializedProperty among casing candidates for a field name (the SDK's serialized casing
        /// varies). Null when none match: callers record it as table/SDK drift (<see cref="DriftMsg"/>) rather
        /// than dropping the dependency.
        /// </summary>
        static SerializedProperty FindProperty(SerializedObject so, string field)
        {
            var p = so.FindProperty(field);
            if (p != null) return p;
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
        /// Build the plan via <see cref="BuildPlan"/> and either preview it (<paramref name="whatIf"/> true:
        /// report, mutate nothing) or replay it against the scene under a single Undo group; both paths consume
        /// the same <see cref="CopyPlan"/>. Returns a one-line PASS/FAIL summary ending with the RunLog path
        /// (<c>… => RESULT | log=&lt;path&gt;</c>). Contract: docs/unity-tools.md.
        ///
        /// FAIL before any mutation: a null root, a non-injective or ambiguous rename map, an unresolved type
        /// name, VRC-table/SDK drift. FAIL in execute: a host or AddComponent failure, a vendor-source leak, an
        /// exception. A flagged-missing host is PASS. A ref left null on a copied component is a "verify — may
        /// block build" note, not a FAIL: a null VRCF propBone or constraint source can abort the downstream
        /// build.
        /// </summary>
        /// <param name="vendorToOwned">Optional <c>vendorName ⇒ ownedName</c> map for the armature-root name
        /// mismatch (owned <c>Armature.1</c> vs vendor <c>Armature</c>). Direction and case rules:
        /// <see cref="IndexedPath.Substitute"/> / <see cref="IndexedPath.ValidateRenameMap"/>.</param>
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

            var cleanRename = IndexedPath.ValidateRenameMap(vendorToOwned, out var collidingKeys);
            if (collidingKeys != null)
            {
                log.result = "FAIL";
                log.error  = "non-injective vendorToOwned (keys collide onto one dest name)";
                log.Offender("vendorToOwned non-injective: keys [" + string.Join(", ", collidingKeys) +
                             "] map onto the same value — cannot address a unique dst sibling");
                return TransplantCore.Finish(log, label);
            }
            if (cleanRename != null)
                log.Note("vendorToOwned active (" + cleanRename.Count +
                         " entr(y/ies)) — predicted dest-path labels stay vendor-named; minted GOs use the mapped name");

            var plan = BuildPlan(ownedRoot, vendorSource, typeNames, force, cleanRename);

            if (plan.unresolvedTypeNames.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "unresolved type names";
                foreach (var u in plan.unresolvedTypeNames) log.Offender("type-name: " + u);
                return TransplantCore.Finish(log, label);
            }

            log.Count("deepInPlace",    plan.DeepInPlace);
            log.Count("recreateLeaf",   plan.RecreateLeaf);
            log.Count("scaffold",       plan.Scaffold);
            log.Count("presentSkip",    plan.PresentSkip);
            log.Count("flaggedMissing", plan.FlaggedMissing);
            log.Count("conservative",   plan.Conservative);

            // Conservative hosts stay untagged: they have no scaffold path, so a [holder] label would
            // advertise a force the executor ignores.
            foreach (var step in plan.steps)
            {
                if (step.action != CopyAction.FlaggedMissing) continue;
                string cls = step.isDeepTier ? (step.isBone ? " [bone]" : " [holder]") : "";
                log.Note("flagged-missing host" + cls +
                         " (PASS — read & iterate, force/re-prune/accept): " + step.ForceKey);
            }
            foreach (var d in plan.tableDriftOffenders)
                log.Offender("VRC-table/SDK drift: " + d);

            if (plan.tableDriftOffenders.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "VRC-table/SDK drift — declared dependency field(s) no longer resolve";
                return TransplantCore.Finish(log, label);
            }

            if (plan.renameAmbiguities.Count > 0)
            {
                log.result = "FAIL";
                log.error  = "ambiguous vendorToOwned — a mapped name cannot address a unique dest counterpart";
                foreach (var a in plan.renameAmbiguities) log.Offender("vendorToOwned ambiguity: " + a);
                return TransplantCore.Finish(log, label);
            }

            // Predicted notes are whatIf-only: execute's remap pass re-derives the same gaps from the actual
            // null refs, so emitting both would double-log every hard-dep null.
            if (whatIf)
            {
                foreach (var v in plan.verifyMayBlockBuild)
                    log.Note("verify — may block build: " + v);

                log.result = "PASS";
                return TransplantCore.Finish(log, label);
            }

            // ── EXECUTE: replay the plan step-by-step under one Undo group ───────────────────────────
            var session = new SessionMap();
            var created = new List<(Component vendor, Component ours, CopyStep step)>();
            Transform vendorRoot = vendorSource.transform;
            Transform ourRoot     = ownedRoot.transform;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("CopyComponents");
            int group = Undo.GetCurrentGroup();

            try
            {
                // Pass 0 — register PRESENT-SKIP vendor components into the session map before Pass 1 creates
                // anything, so GetComponents sees only pre-existing dst components. A created component that
                // references a present-skipped same-type sibling would otherwise fall through to the generic
                // path-remap, whose GetComponent(type) returns the FIRST of N co-hosted same-type components and
                // binds the wrong sibling. The first M slots of a (host,type) group pair, in order, with the M
                // pre-existing dst components.
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
                            continue;
                        case CopyAction.HardDepNull:
                        case CopyAction.InPlace:
                        case CopyAction.RecreateLeaf:
                        case CopyAction.Scaffold:
                            break;
                        default:
                            continue;
                    }
                    if (step.vendorComponent == null) continue;

                    // Call EnsureHost for every copy action: it reuses an existing chain or mints one. Do not
                    // route that decision off the action, which also carries criticality: the planner promotes a
                    // forced Scaffold with an uncopied hard dep to HardDepNull, and a no-mint path would FAIL it.
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
                    // EnsureHost records every chain level but returns early for a root host, which this records.
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

                // Pass 2 — ref-remap every created component, now that the map is fully populated. Soft-dep
                // drops stay as inert NULL slots, not compacted out of the array. Every null here is a non-fatal
                // verify note: a deep-tier null on a declared soft-dep field is inert, any other may block.
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

                // Pass 3 — any ref on a created component still pointing into the vendor source is one the
                // remap failed to rebind, a hard ref that leaked rather than nulled → FAIL.
                var createdComps = new List<Component>(created.Count);
                foreach (var c in created) createdComps.Add(c.ours);
                int leaks = TransplantCore.SweepVendorLeaks(createdComps, vendorRoot, log);

                int copied = 0;
                foreach (var c in createdComps) if (c != null) copied++;
                log.Count("componentsCreated", copied);
                log.Count("conservativeNulledRefs", conservativeNulledRefs);
                log.Count("vendorLeaks", leaks);

                log.result = leaks == 0 ? "PASS" : "FAIL";
            }
            catch (Exception ex)
            {
                log.result = "FAIL";
                log.error  = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                // A partial run is still one collapsible Undo step.
                Undo.CollapseUndoOperations(group);
            }

            return TransplantCore.Finish(log, label);
        }

        /// <summary>
        /// Whether a nulled property path belongs to a declared soft-dep field (<c>ignoreTransforms</c> matches
        /// <c>ignoreTransforms.Array.data[3]</c>), whose null is behavior-inert rather than a may-block.
        /// </summary>
        static bool IsSoftDepPath(VrcComponentDescriptor d, string path)
        {
            if (d == null || string.IsNullOrEmpty(path)) return false;
            foreach (var f in d.softDepFieldPaths)
            {
                if (path == f || path.StartsWith(f + ".", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Rebind every object reference on <paramref name="comp"/>: a SessionMap hit wins, else
        /// <see cref="RemapReferencesByPath.Remap"/> resolves within reach or nulls. Map hits go first because a
        /// copied collider lives at a new path under a recreated anchor, where a path-remap would miss it or
        /// bind the wrong one of N same-type components. Returns the paths of refs left null for the caller to
        /// classify.
        /// </summary>
        static List<string> RemapBySession(Component comp, SessionMap session, Transform vendorRoot, Transform ourRoot,
                                            IDictionary<string, string> vendorToOwned, RunLog log)
        {
            var so = new SerializedObject(comp);
            so.Update();

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
