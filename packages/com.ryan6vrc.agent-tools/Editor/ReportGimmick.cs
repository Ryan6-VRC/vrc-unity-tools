using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only markdown topology digest of a gimmick subtree: tables for contacts, raycasts, physbones,
    /// constraints and VRCFury features, an index of mechanically certain idioms, and a tier-2 census of
    /// every other component. Contract: docs/unity-tools.md.
    ///
    /// Reports what is declared, with no verdict: it traces no `parameter` into an animator and predicts no
    /// VRCFury bake output. VRCFury stays tier-1 because its seam is a nested managed-reference `content[]`
    /// that the census's shallow peek would render as noise. The digest scales with component count, so
    /// scope the root to the gimmick.
    /// </summary>
    [AgentTool]
    public static class ReportGimmick
    {
        internal const string ChainSubtreeLegend = "_`chain subtree` = the bones the row's physbone itself reports as its chain (the SDK's own set, so the exclusions the component declares — `ignoreTransforms`, `ignoreOtherPhysBones` — are already applied; a virtual endpoint bone has no transform and is not counted): `bones` counts them, `skinned` how many some SkinnedMeshRenderer skins at nonzero weight (swept over the avatar enclosing THAT ROW's physbone, so a mesh outside the reported chain still counts while a co-hosted neighbour avatar's does not; a row with no enclosing avatar descriptor falls back to its outermost ancestor), `hosting` how many carry a component besides Transform and this row's own physbone — counted over the bones the chain KEPT, so a nested chain excluded by `ignoreOtherPhysBones` is not among them and appears as its own row instead. Reported, not judged. All-zero is NOT a dead chain: a pre-bake bone that will name-merge onto a base bone reads `skinned=0` because the base mesh skins the BASE transform, and a chain whose consumer sits outside it (a constraint reading it as a source) reads all-zero while load-bearing. Chain MEMBERSHIP is not a claim about which bones move — `multiChildType` and `endpointPosition` decide what is actually simulated._\n";

        internal const string CurveLegend = "_`~` marks a field whose curve has keys: its value at a joint is the scalar times the curve at that joint's depth along the chain, so rows matching on every scalar can still differ. `curved` names every field carrying a curve, printed in this table or not. The keys are `AgentInspector`'s to read, on the physbone's host._\n";

        internal const string RaycastLegend = "_`layers` renders under every mode, but the component consults it only under `HitCustomLayers` — a mask left behind by another mode still shows here. Layer NAMES come from the project's TagManager, not from the raycast: a bare index means this project does not name that layer, never that the mask is empty, which renders `(none)`. `params` concatenates the AUTHORED prefix, so it is not the post-build name — VRCFury and Modular Avatar both rewrite that prefix at build. A `result` of `(none)` leaves the ray with no output transform to write._\n";

        // ----- Agent entry point ------------------------------------------------------------------

        /// <summary>Digest the subtree at <paramref name="rootPath"/>, a scene handle in the active scene, to
        /// markdown under Snapshots/. Returns a one-line summary ending with the artifact path
        /// (<c>… =&gt; OK | log=&lt;path&gt;</c>); a null, empty or unresolved <paramref name="rootPath"/> is
        /// a bare FAIL that writes nothing. A component-empty subtree writes a zero-count artifact.</summary>
        public static string Run(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath)) return Refuse("rootPath is null/empty");
            var handle = SceneHandle.Resolve(rootPath);
            if (!handle.Ok) return Refuse("rootPath: " + handle.Refusal);
            var root = handle.Object;

            // includeInactive is mandatory: runtime-swap scaffolding lives on inactive GameObjects.
            var senders     = root.GetComponentsInChildren<VRCContactSender>(true);
            var receivers   = root.GetComponentsInChildren<VRCContactReceiver>(true);
            var raycasts    = root.GetComponentsInChildren<VRCRaycast>(true);
            var physbones   = root.GetComponentsInChildren<VRCPhysBone>(true);
            var colliders   = root.GetComponentsInChildren<VRCPhysBoneCollider>(true);
            var constraints = root.GetComponentsInChildren<VRCConstraintBase>(true);
            // Interface query, because a per-type list would let an unlisted Unity constraint slip through.
            // VRC constraints do not implement UnityEngine's IConstraint, so the two families never overlap.
            var unityConstraints = root.GetComponentsInChildren<IConstraint>(true);
            var constraintRows = BuildConstraintRows(constraints, unityConstraints, root.transform);
            int constraintCount = constraints.Length + unityConstraints.Length;

            // The asmdef does not reference VRCFury, so it is matched by full type name and decoded through
            // SerializedObject.
            var fury = new List<Component>();
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().FullName == "VF.Model.VRCFury") fury.Add(mb);

            var body = new StringBuilder();

            var tier1 = new HashSet<Component>();
            foreach (var a in senders) tier1.Add(a);
            foreach (var a in receivers) tier1.Add(a);
            foreach (var a in raycasts) tier1.Add(a);
            foreach (var a in physbones) tier1.Add(a);
            foreach (var a in colliders) tier1.Add(a);
            foreach (var a in constraints) tier1.Add(a);
            foreach (var a in unityConstraints) tier1.Add((Component)a);
            foreach (var a in fury) tier1.Add(a);

            int contactCount = senders.Length + receivers.Length;
            // The header's count line needs `other`, known only after AppendOther, so the body is built first.
            AppendContacts(body, senders, receivers);
            AppendRaycasts(body, raycasts, root);
            AppendPhysBones(body, physbones, colliders, root);
            AppendConstraints(body, constraintRows);
            var applyDuringUploadHosts = AppendVrcFury(body, fury);
            int obsCount = AppendObservations(body, constraintRows, applyDuringUploadHosts, physbones, root);
            int other = AppendOther(body, root, tier1);

            var doc = new StringBuilder();
            doc.Append("# ReportGimmick: ").Append(root.name).Append('\n');
            doc.Append("root: `").Append(GetHierarchyPath(root.transform)).Append("`  \n");
            doc.Append("_transform handles are full scene-root-absolute paths; under first-match resolution a duplicate-named sibling makes a handle non-unique._\n");
            doc.Append("\ncontacts=").Append(contactCount)
                  .Append(" raycasts=").Append(raycasts.Length)
                  .Append(" physbones=").Append(physbones.Length)
                  .Append(" constraints=").Append(constraintCount)
                  .Append(" vrcfury=").Append(fury.Count)
                  .Append(" other=").Append(other).Append('\n');
            doc.Append(body);

            var summary = "[ReportGimmick] " + root.name
                        + ": contacts=" + contactCount
                        + " raycasts=" + raycasts.Length
                        + " physbones=" + physbones.Length
                        + " constraints=" + constraintCount
                        + " vrcfury=" + fury.Count
                        + " other=" + other
                        + " observations=" + obsCount + " => OK";
            var result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "gimmick_" + root.name, summary, doc.ToString(), ".md");
            Debug.Log(result);
            return result;
        }

        // ----- Contacts ---------------------------------------------------------------------------

        private static void AppendContacts(StringBuilder sb, VRCContactSender[] senders, VRCContactReceiver[] receivers)
        {
            sb.Append("\n## Contacts\n\n");
            if (senders.Length == 0 && receivers.Length == 0) { sb.Append("_(none)_\n"); return; }
            sb.Append("| transform | kind | shape | tags | receiverType | filters | localOnly | parameter | rootTransform |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|\n");

            foreach (var s in senders)
                sb.Append("| `").Append(Cell(GetHierarchyPath(s.transform))).Append("` | sender | ")
                  .Append(Cell(Shape(s.shapeType, s.radius, s.height))).Append(" | ")
                  .Append(Cell(Tags(s.collisionTags))).Append(" | — | — | ")
                  .Append(s.localOnly ? "true" : "false").Append(" | — | ")
                  .Append(RootIndirection(s.rootTransform, s.transform)).Append(" |\n");

            foreach (var r in receivers)
                sb.Append("| `").Append(Cell(GetHierarchyPath(r.transform))).Append("` | receiver | ")
                  .Append(Cell(Shape(r.shapeType, r.radius, r.height))).Append(" | ")
                  .Append(Cell(Tags(r.collisionTags))).Append(" | ")
                  .Append(r.receiverType).Append(" | ")
                  .Append("self=").Append(r.allowSelf ? "1" : "0").Append(" others=").Append(r.allowOthers ? "1" : "0").Append(" | ")
                  .Append(r.localOnly ? "true" : "false").Append(" | ")
                  .Append(string.IsNullOrEmpty(r.parameter) ? "—" : "`" + Cell(r.parameter) + "`").Append(" | ")
                  .Append(RootIndirection(r.rootTransform, r.transform)).Append(" |\n");
        }

        // ----- Raycasts -----------------------------------------------------------------------------

        private static void AppendRaycasts(StringBuilder sb, VRCRaycast[] rays, GameObject root)
        {
            sb.Append("\n## Raycasts\n\n");
            if (rays.Length == 0) { sb.Append("_(none)_\n"); return; }
            sb.Append("| transform | ray | collision | result | params | on miss | result rotation | live |\n");
            sb.Append("|---|---|---|---|---|---|---|---|\n");
            foreach (var r in rays)
                sb.Append("| `").Append(Cell(RaycastHandle(r, rays))).Append("` | ")
                  .Append(Cell("dir=" + V3(r.RaycastDirection) + " dist=" + F(r.Distance)
                               + (r.ApplyTransformScale ? " (×transform scale)" : ""))).Append(" | ")
                  .Append(Cell(r.RaycastCollisionMode + " layers=" + LayerMaskCell(r.CustomCollisionLayers))).Append(" | ")
                  .Append(r.ResultTransform != null ? "`" + Cell(GetHierarchyPath(r.ResultTransform)) + "`" : "(none)").Append(" | ")
                  .Append(Cell(ParamCell(r.Parameter))).Append(" | ")
                  .Append(r.BehaviorOnMiss).Append(" | ")
                  .Append(Cell(r.ApplyRotation ? "align=" + V3(r.AlignmentAxis) : "off")).Append(" | ")
                  .Append(LiveCell(NotLiveReason(r, root.transform))).Append(" |\n");

            sb.Append('\n').Append(RaycastLegend);
        }

        private static string ParamCell(string prefix) =>
            string.IsNullOrEmpty(prefix) ? "—" : "`" + prefix + "` + _Hit/_Ratio/_Distance";

        // Index-first with the name only as an annotation, because `LayerMask.LayerToName` reads the project's
        // TagManager: one mask renders `10(PlayerLocal)` where VRChat's layer setup has run and a bare `10` where
        // it has not. A name-first cell would print an empty string there, which reads as "no layer".
        private static string LayerMaskCell(int mask)
        {
            if (mask == 0) return "(none)";
            if (mask == ~0) return "everything";
            var parts = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                string name = LayerMask.LayerToName(i);
                parts.Add(string.IsNullOrEmpty(name) ? i.ToString() : i + "(" + name + ")");
            }
            return string.Join(",", parts.ToArray());
        }

        // Two raycasts on one GameObject is a shipped idiom (`vrc-patterns/selective-animation`), so the path
        // alone is ambiguous. Same component-ordinal fallback as HostHandle.
        private static string RaycastHandle(VRCRaycast r, VRCRaycast[] all)
        {
            int sharing = 0;
            foreach (var o in all) if (o.transform == r.transform) sharing++;
            string path = GetHierarchyPath(r.transform);
            if (sharing < 2) return path;
            var onHost = r.GetComponents<VRCRaycast>();
            for (int i = 0; i < onHost.Length; i++)
                if (ReferenceEquals(onHost[i], r)) return path + " [VRCRaycast#" + i + "]";
            return path;
        }

        private static string V3(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";

        // ----- PhysBones + collider companion -----------------------------------------------------

        private static void AppendPhysBones(StringBuilder sb, VRCPhysBone[] bones, VRCPhysBoneCollider[] colliders,
                                            GameObject reportRoot)
        {
            sb.Append("\n## PhysBones\n\n");
            if (bones.Length == 0) sb.Append("_(none)_\n");
            else
            {
                var scopeCache = new Dictionary<Transform, HashSet<Transform>>();
                sb.Append("| transform | rootTransform | parameter prefix | grab/pose | forces | radius | immobile | curved | flags | chain subtree |\n");
                sb.Append("|---|---|---|---|---|---|---|---|---|---|\n");
                // The walk includes inactive objects, and a physbone needs both `enabled` and `active` to run,
                // so they tell which of several physbones sharing one target bone actually runs.
                foreach (var b in bones)
                    sb.Append("| `").Append(Cell(GetHierarchyPath(b.transform))).Append("` | ")
                      .Append(RootIndirection(b.rootTransform, b.transform)).Append(" | ")
                      .Append(string.IsNullOrEmpty(b.parameter) ? "—" : "`" + Cell(b.parameter) + "`").Append(" | ")
                      .Append("grab=").Append(b.allowGrabbing).Append(" pose=").Append(b.allowPosing).Append(" grabMove=").Append(F(b.grabMovement)).Append(" | ")
                      .Append("pull=").Append(F(b.pull)).Append(Tilde(b.pullCurve)).Append(" spring=").Append(F(b.spring)).Append(Tilde(b.springCurve))
                      .Append(" stiffness=").Append(F(b.stiffness)).Append(Tilde(b.stiffnessCurve)).Append(" | ")
                      .Append(F(b.radius)).Append(Tilde(b.radiusCurve)).Append(" | ")
                      .Append(F(b.immobile)).Append(Tilde(b.immobileCurve)).Append(" (").Append(b.immobileType).Append(") | ")
                      .Append(CurvedCell(b)).Append(" | ")
                      .Append("enabled=").Append(b.enabled ? "1" : "0")
                      .Append(" active=").Append(b.gameObject.activeInHierarchy ? "1" : "0")
                      .Append(" isAnimated=").Append(b.isAnimated ? "1" : "0").Append(" resetWhenDisabled=").Append(b.resetWhenDisabled ? "1" : "0").Append(" | ")
                      .Append(ChainSubtreeCell(b, SkinnedBonesFor(b, reportRoot, scopeCache))).Append(" |\n");

                sb.Append('\n').Append(ChainSubtreeLegend);
                sb.Append('\n').Append(CurveLegend);
            }

            sb.Append("\n### PhysBone colliders\n\n");
            if (colliders.Length == 0) sb.Append("_(none)_\n");
            else
            {
                sb.Append("| transform | shape | insideBounds |\n|---|---|---|\n");
                foreach (var c in colliders)
                    sb.Append("| `").Append(Cell(GetHierarchyPath(c.transform))).Append("` | ")
                      .Append(Cell(Shape(c.shapeType, c.radius, c.height))).Append(" | ")
                      .Append(c.insideBounds ? "true" : "false").Append(" |\n");
            }
        }

        // ----- Constraints edge-list --------------------------------------------------------------

        private struct ConstraintRow
        {
            public string Type;
            public string Driven;                        // hierarchy path of the driven transform
            public (string src, float weight)[] Sources; // src="(none)" when unwired; empty => source-less
            public float GlobalWeight;
            public string Axes;                          // rendered mask, or "—"
            public bool AxisMiss;                        // VRC-only; Unity never sets this
            public string Note;                          // VRC-only idioms; "" for Unity
            public Transform DrivenTransform;
            public Transform[] SourceTransforms;
            public VRCConstraintBase Vrc;                // family discriminator; null for Unity constraints
            public string NotLive;                       // null when running; else which flag is down
        }

        private static ConstraintRow[] BuildConstraintRows(VRCConstraintBase[] vrc, IConstraint[] unity, Transform root)
        {
            var rows = new List<ConstraintRow>(vrc.Length + unity.Length);
            foreach (var c in vrc) rows.Add(FromVrc(c, root));
            foreach (var c in unity) rows.Add(FromUnity(c, root));
            return rows.ToArray();
        }

        private static ConstraintRow FromVrc(VRCConstraintBase c, Transform root)
        {
            var host = c.transform;
            // Never ??: it bypasses UnityEngine.Object's overloaded == and reads a destroyed TargetTransform
            // as live instead of falling back to the host.
            var driven = c.TargetTransform != null ? c.TargetTransform : host;
            var sources = new List<(string, float)>();
            var srcTransforms = new List<Transform>();
            int count = c.Sources.Count;
            for (int i = 0; i < count; i++)
            {
                var s = c.Sources[i];
                sources.Add((s.SourceTransform != null ? GetHierarchyPath(s.SourceTransform) : "(none)", s.Weight));
                if (s.SourceTransform != null) srcTransforms.Add(s.SourceTransform);
            }
            string axes = AxisMask(c, out bool miss);
            return new ConstraintRow
            {
                Type = c.GetType().Name,
                Driven = GetHierarchyPath(driven),
                Sources = sources.ToArray(),
                GlobalWeight = c.GlobalWeight,
                Axes = axes,
                AxisMiss = miss,
                Note = ConstraintNote(c, host, driven),
                DrivenTransform = driven,
                SourceTransforms = srcTransforms.ToArray(),
                Vrc = c,
                NotLive = NotLiveReason(c, root) ?? (c.IsActive ? null : "IsActive"),
            };
        }

        // Unity constraints drive their own host and have no TargetTransform or FreezeToWorld, so Note stays
        // "" and the feedback loop is the only constraint observation that applies to them.
        private static ConstraintRow FromUnity(IConstraint c, Transform root)
        {
            var comp = (Component)c;
            var host = comp.transform;
            var sources = new List<(string, float)>();
            var srcTransforms = new List<Transform>();
            var list = new List<ConstraintSource>();
            c.GetSources(list);
            foreach (var s in list)
            {
                sources.Add((s.sourceTransform != null ? GetHierarchyPath(s.sourceTransform) : "(none)", s.weight));
                if (s.sourceTransform != null) srcTransforms.Add(s.sourceTransform);
            }
            return new ConstraintRow
            {
                Type = comp.GetType().Name,
                Driven = GetHierarchyPath(host),
                Sources = sources.ToArray(),
                GlobalWeight = c.weight,
                Axes = UnityAxes(c),
                AxisMiss = false,
                Note = "",
                DrivenTransform = host,
                SourceTransforms = srcTransforms.ToArray(),
                Vrc = null,
                NotLive = NotLiveReason(comp, root) ?? (c.constraintActive ? null : "constraintActive"),
            };
        }

        private static string UnityAxes(IConstraint c)
        {
            switch (c)
            {
                case ParentConstraint p:   return "pos:" + AxisFlags(p.translationAxis) + " rot:" + AxisFlags(p.rotationAxis);
                case PositionConstraint p: return "pos:" + AxisFlags(p.translationAxis);
                case RotationConstraint r: return "rot:" + AxisFlags(r.rotationAxis);
                case ScaleConstraint s:    return "scale:" + AxisFlags(s.scalingAxis);
                case AimConstraint a:      return "rot:" + AxisFlags(a.rotationAxis);
                default:                   return "—"; // LookAtConstraint / unknown: no per-axis flags
            }
        }

        private static string AxisFlags(Axis a)
        {
            if (a == Axis.None) return "off";
            if (a == (Axis.X | Axis.Y | Axis.Z)) return "*";
            return (a.HasFlag(Axis.X) ? "X" : "") + (a.HasFlag(Axis.Y) ? "Y" : "") + (a.HasFlag(Axis.Z) ? "Z" : "");
        }

        private static void AppendConstraints(StringBuilder sb, ConstraintRow[] rows)
        {
            sb.Append("\n## Constraints (edge-list: constrained → source)\n\n");
            if (rows.Length == 0) { sb.Append("_(none)_\n"); return; }
            sb.Append("_source weights normalize by SUM (docs/runtime.md §Constraints) — a weight is not a clamped 0..1 absolute._\n");
            sb.Append("_affected-axes form differs by family (the `type` column disambiguates): VRC omits an off group and writes `pos*`/`posXY`; Unity writes per-group `pos:*`/`pos:XZ`/`pos:off`._\n\n");
            sb.Append("| type | constrained transform | source transform | weight | live | affected axes | note |\n");
            sb.Append("|---|---|---|---|---|---|---|\n");

            var axisMissTypes = new HashSet<string>();
            foreach (var row in rows)
            {
                if (row.AxisMiss) axisMissTypes.Add(row.Type);

                if (row.Sources.Length == 0)
                {
                    Row(sb, row.Type, row.Driven, "(none)", "w=— g=" + F(row.GlobalWeight), LiveCell(row.NotLive), row.Axes, row.Note);
                    continue;
                }
                for (int i = 0; i < row.Sources.Length; i++)
                {
                    var s = row.Sources[i];
                    string weight = "w=" + F(s.weight) + " g=" + F(row.GlobalWeight);
                    Row(sb, row.Type, row.Driven, s.src, weight, LiveCell(row.NotLive), row.Axes, i == 0 ? row.Note : "");
                }
            }
            foreach (var tn in axisMissTypes)
                sb.Append("\n> note: could not read the affected-axis mask for `").Append(Cell(tn))
                  .Append("` (Affects* members not resolvable by name) — the `—` in its axes column is a READ MISS, not all-axes-off\n");
        }

        private static void Row(StringBuilder sb, string type, string driven, string src, string weight, string live, string axes, string note)
        {
            sb.Append("| ").Append(type)
              .Append(" | `").Append(Cell(driven)).Append("` | ")
              .Append(src == "(none)" ? "(none)" : "`" + Cell(src) + "`").Append(" | ")
              .Append(Cell(weight)).Append(" | ").Append(Cell(live)).Append(" | ").Append(Cell(axes)).Append(" | ")
              .Append(string.IsNullOrEmpty(note) ? "" : Cell(note)).Append(" |\n");
        }

        private static string ConstraintNote(VRCConstraintBase c, Transform host, Transform driven)
        {
            var parts = new List<string>();
            if (c.TargetTransform != null && c.TargetTransform != host)
                parts.Add("TargetTransform indirection (host=`" + host.name + "`)");
            if (c.FreezeToWorld)
                // The world-anchor idiom is the source-less case only, as in observation a; a FreezeToWorld
                // constraint that still has sources is just a frozen-frame flag.
                parts.Add(c.Sources.Count == 0 ? "FreezeToWorld (per-client world anchor)" : "FreezeToWorld");
            string hold = HoldSuffix(c);
            if (hold != null) parts.Add("hold" + hold);
            if (c.Locked) parts.Add("locked");
            return string.Join("; ", parts);
        }

        // Hold = writes nothing at runtime. Returns null if not a hold, else the suffix to append after "hold".
        // A clip this digest cannot see can animate a weight or GlobalWeight, so a hold resting on a zero in
        // either carries the static-frame caveat. No source, or no wired source under a nonzero GlobalWeight,
        // is structural and reads plain.
        private static string HoldSuffix(VRCConstraintBase c)
        {
            if (c.FreezeToWorld && !Mathf.Approximately(c.GlobalWeight, 0f)) return null; // actively freezes to world
            int count = c.Sources.Count;
            if (count == 0) return "";
            if (Mathf.Approximately(c.GlobalWeight, 0f))
                return " (static frame — clip-driven weighting not visible here)";
            int nonNull = 0, nonNullZero = 0; float total = 0f;
            for (int i = 0; i < count; i++)
            {
                var s = c.Sources[i];
                if (s.SourceTransform == null) continue;
                nonNull++;
                total += s.Weight;
                if (Mathf.Approximately(s.Weight, 0f)) nonNullZero++;
            }
            if (nonNull >= 2 && nonNullZero == nonNull)
                return " (≥2 sources all weight 0 — static rest shape; any clip-driven selection is not visible here)";
            if (nonNull >= 1 && Mathf.Approximately(total, 0f))
                return " (static frame — clip-driven weighting not visible here)";
            if (nonNull == 0) return "";
            return null;
        }

        // The Affects* bools live per type on intermediate bases (Parent has pos+rot; Position, Rotation, Aim
        // and Scale their one group; LookAt none), not on VRCConstraintBase, so they are read by name. `miss`
        // flags a non-LookAt type resolving zero groups: `—` legitimately means LookAt or all-off, so an SDK
        // rename must not read as one.
        private static string AxisMask(VRCConstraintBase c, out bool miss)
        {
            var t = c.GetType();
            var parts = new List<string>();
            int groupsResolved = 0;
            string pos = AxisGroup(c, t, "AffectsPosition", "pos", ref groupsResolved);
            string rot = AxisGroup(c, t, "AffectsRotation", "rot", ref groupsResolved);
            string scale = AxisGroup(c, t, "AffectsScale", "scale", ref groupsResolved);
            if (pos != null) parts.Add(pos);
            if (rot != null) parts.Add(rot);
            if (scale != null) parts.Add(scale);
            miss = groupsResolved == 0 && t.Name != "VRCLookAtConstraint";
            return parts.Count == 0 ? "—" : string.Join(" ", parts);
        }

        private static string AxisGroup(VRCConstraintBase c, Type t, string prefix, string label, ref int groupsResolved)
        {
            // A trio that resolves counts as a present group whatever its values; that is what separates
            // "group absent" from a read miss.
            bool? x = ReadBoolMember(c, t, prefix + "X");
            bool? y = ReadBoolMember(c, t, prefix + "Y");
            bool? z = ReadBoolMember(c, t, prefix + "Z");
            if (x == null || y == null || z == null) return null;
            groupsResolved++;
            if (!x.Value && !y.Value && !z.Value) return null;
            if (x.Value && y.Value && z.Value) return label + "*";
            return label + (x.Value ? "X" : "") + (y.Value ? "Y" : "") + (z.Value ? "Z" : "");
        }

        // A bool member read as property or field, so an SDK field→property change does not break it; null
        // when neither resolves as a bool.
        internal static bool? ReadBoolMember(object c, Type t, string name)
        {
            var p = t.GetProperty(name);
            if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(c);
            var f = t.GetField(name);
            if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(c);
            return null;
        }

        // ----- VRCFury authoring inventory — returns the ApplyDuringUpload hosts for observation e ----

        private static List<Transform> AppendVrcFury(StringBuilder sb, List<Component> fury)
        {
            var applyHosts = new List<Transform>();
            sb.Append("\n## VRCFury authoring inventory\n\n");
            if (fury.Count == 0) { sb.Append("_(none)_\n"); return applyHosts; }

            foreach (var c in fury)
            {
                var so = new SerializedObject(c);
                var content = so.FindProperty("content");
                string tn = content != null ? content.managedReferenceFullTypename : null;
                string leaf = string.IsNullOrEmpty(tn) ? "(unresolved feature)" : tn.Substring(tn.LastIndexOf('.') + 1);
                string hostPath = GetHierarchyPath(c.transform);
                sb.Append("- **").Append(Cell(leaf)).Append("** on `").Append(Cell(hostPath)).Append("`\n");

                if (content == null) { sb.Append("  > note: VRCFury `content` property not resolvable — feature undecodable\n"); continue; }

                // Observation e keys off the ApplyDuringUpload leaf, so an unnamed feature must never read as
                // an honest "not that type".
                if (string.IsNullOrEmpty(tn))
                    sb.Append("  > note: feature `managedReferenceFullTypename` is empty — type not resolvable by name; FullController/ApplyDuringUpload decode (and editor/runtime-swap observation) may be incomplete\n");

                if (leaf == "FullController")
                    AppendFullController(sb, content, hostPath);
                else if (leaf == "ApplyDuringUpload")
                {
                    if (!applyHosts.Contains(c.transform)) applyHosts.Add(c.transform);
                    sb.Append("  - toggles this GameObject at upload (editor/runtime-swap ingredient)\n");
                }
            }
            return applyHosts;
        }

        private static void AppendFullController(StringBuilder sb, SerializedProperty content, string hostPath)
        {
            AppendAssetArray(sb, content, "controllers", "controller", "controller");
            AppendAssetArray(sb, content, "menus", "menu", "menu");
            AppendAssetArray(sb, content, "prms", "parameters", "params");
            var root = content.FindPropertyRelative("rootObjOverride");
            if (root != null && root.objectReferenceValue != null)
                sb.Append("  - rootObjOverride: `").Append(Cell(root.objectReferenceValue.name)).Append("` (mount)\n");
        }

        private static void AppendAssetArray(StringBuilder sb, SerializedProperty content, string arrayName, string wrapperName, string label)
        {
            var arr = content.FindPropertyRelative(arrayName);
            if (arr == null || !arr.isArray)
            {
                sb.Append("  > note: FullController `").Append(arrayName).Append("` array not resolvable by name — asset inventory may be incomplete\n");
                return;
            }
            for (int i = 0; i < arr.arraySize; i++)
            {
                var wrapper = arr.GetArrayElementAtIndex(i).FindPropertyRelative(wrapperName);
                var objRef = wrapper != null ? wrapper.FindPropertyRelative("objRef") : null;
                if (objRef == null)
                {
                    sb.Append("  > note: FullController `").Append(arrayName).Append('[').Append(i).Append("].").Append(wrapperName).Append(".objRef` not resolvable — asset inventory may be incomplete\n");
                    continue;
                }
                var obj = objRef.objectReferenceValue;
                if (obj == null) { sb.Append("  - ").Append(label).Append(": (unset)\n"); continue; }
                string p = AssetDatabase.GetAssetPath(obj);
                sb.Append("  - ").Append(label).Append(": `").Append(Cell(obj.name)).Append('`')
                  .Append(string.IsNullOrEmpty(p) ? "" : " (`" + Cell(p) + "`)").Append('\n');
            }
        }

        // ----- Observations — mechanically certain idioms, each a fact + docs pointer ----------------

        private static int AppendObservations(StringBuilder sb, ConstraintRow[] rows, List<Transform> applyHosts,
                                              VRCPhysBone[] physbones, GameObject root)
        {
            var lines = new List<string>();
            foreach (var row in rows)
            {
                var driven = row.DrivenTransform;
                var c = row.Vrc;

                // a. Per-client world anchor.
                if (c != null && c.FreezeToWorld && row.Sources.Length == 0)
                    lines.Add("**per-client world anchor** — `" + Cell(GetHierarchyPath(driven)) + "` — docs/gimmicks.md §Constraint patterns · World anchors");

                // b. Feedback loop: a source strictly below driven, on either constraint family. The
                //    source != driven guard is load-bearing: IsChildOf is self-inclusive, and self-as-source
                //    at partial weight is the "feel"-damping idiom, not a feedback cage.
                foreach (var src in row.SourceTransforms)
                {
                    if (src != null && src != driven && src.IsChildOf(driven))
                    {
                        lines.Add("**feedback loop (self-referential constraint source)** — `" + Cell(GetHierarchyPath(driven)) + "` ← `" + Cell(GetHierarchyPath(src)) + "` — docs/gimmicks.md §Constraint patterns · Position-from-proximity tracking");
                        break;
                    }
                }

                // c. TargetTransform indirection.
                if (c != null && c.TargetTransform != null && c.TargetTransform != c.transform)
                    lines.Add("**TargetTransform indirection** — `" + Cell(GetHierarchyPath(driven)) + "` (host `" + Cell(c.transform.name) + "`) — docs/runtime.md §Constraints");

                // d. Hold, with the label the Constraints note column uses.
                if (c != null)
                {
                    string hold = HoldSuffix(c);
                    if (hold != null)
                        lines.Add("**hold** — `" + Cell(GetHierarchyPath(driven)) + "`" + hold + " — docs/runtime.md §Constraints");
                }
            }

            // e. Editor/runtime swap (VRCFury ApplyDuringUpload host).
            foreach (var h in applyHosts)
                lines.Add("**editor/runtime swap** — `" + Cell(GetHierarchyPath(h)) + "` — docs/gimmicks.md §Constraint patterns · Editor/runtime swap");

            // f. Several physbones on one target bone. The label is the mechanical fact, not an intent: the
            //    same shape is a per-range variant set a body-morph slider switches between (docs/outfits.md),
            //    a chain plus its limiter, or an accidental duplicate, and nothing in the subtree distinguishes
            //    them. Live state is rendered, never gated on: scene statics are not authoritative on a property
            //    a layer drives, and a vendor shipping every variant enabled is still a variant set.
            foreach (var g in GroupPhysBonesByTarget(physbones))
            {
                if (g.Value.Count < 2) continue;
                var live = new List<string>();
                foreach (var b in g.Value)
                    if (LiveUnder(b, root.transform))
                        live.Add("`" + Cell(HostHandle(b, g.Value)) + "`");
                lines.Add("**physbones share one target** — `" + Cell(GetHierarchyPath(g.Key)) + "` ← "
                          + g.Value.Count + " physbones, live: "
                          + (live.Count == 0 ? "none" : string.Join(", ", live.ToArray()))
                          + " — docs/outfits.md §The FX controller is the authoritative map");
            }

            // g. A constraint source past its list length; ScanConstraintLengths owns the mechanism.
            foreach (var line in ScanConstraintLengths(root))
                lines.Add(line.StartsWith(CheckAvatar.DegradedPrefix, StringComparison.Ordinal) ||
                          line.StartsWith(CheckAvatar.ScopePrefix, StringComparison.Ordinal)
                    ? line
                    : "**constraint source past totalLength** — " + line);

            sb.Append("\n## Observations\n\n");
            if (lines.Count == 0) sb.Append("_(none)_\n");
            else foreach (var l in lines) sb.Append("- ").Append(l).Append('\n');
            int observed = 0;
            foreach (var l in lines)
                if (!l.StartsWith(CheckAvatar.DegradedPrefix, StringComparison.Ordinal) &&
                    !l.StartsWith(CheckAvatar.ScopePrefix, StringComparison.Ordinal)) observed++;
            return observed;
        }

        // ----- Constraint source length ------------------------------------------------------------

        /// <summary>Which branch applies is the author's call: the serialized text cannot tell a source that
        /// was meant to solve from one an edit left behind.</summary>
        private const string SourceLengthFix =
            "Fix: raise `Sources.totalLength` past it, or clear the slot — both `SerializedObject` " +
            "writes (docs/runtime.md §Constraints)";

        /// <summary>Fires iff some constraint declared a length past the keyable slots, so the scan could not
        /// read all its sources. Bounds what a zero count covers.</summary>
        internal const string SourceLengthScopeLine =
            "only the 16 keyable slots (`source0`…`source15`) are read; a constraint whose `Sources.totalLength` " +
            "exceeds them keeps its remaining sources in `overflowList`, which this scan does not open";

        /// <summary>The keyable half of a VRC constraint's source list — <c>source0</c>…<c>source15</c>, the
        /// slots an animator can bind. Sources past these live in <c>overflowList</c> (docs/runtime.md
        /// §Constraints).</summary>
        private const int KeyableSlots = 16;

        /// <summary>
        /// The constraint source-length class alone, on any GameObject root, so a bare module prefab scans. The
        /// editor solves every keyable slot holding a transform and the client exactly the first
        /// <c>totalLength</c> (docs/runtime.md §Constraints), so a slot past it works in every play-mode pass
        /// and does nothing in-game.
        ///
        /// Returns one line per offending slot, plus any fail-loud note prefixed with
        /// <see cref="CheckAvatar.DegradedPrefix"/> and any scope bound prefixed with
        /// <see cref="CheckAvatar.ScopePrefix"/>. No offender line means the scan ran and found nothing.
        ///
        /// Read through <see cref="SerializedObject"/>, never the SDK API: <c>Sources.Count</c> returns
        /// <c>totalLength</c>, so a past-length slot is invisible to <c>Sources[i]</c> and to the edge-list table
        /// built from it. Addressed with <c>FindProperty</c>, never a <c>NextVisible</c> walk, which does not
        /// surface <c>Sources.totalLength</c> (measured) and would pass every constraint silently.
        /// </summary>
        public static List<string> ScanConstraintLengths(GameObject root)
        {
            var lines = new List<string>();
            if (root == null)
            {
                lines.Add(CheckAvatar.DegradedPrefix + "null root — nothing was scanned");
                return lines;
            }

            // includeInactive: toggled-off layers are a composed avatar's resting state, and the defect is
            // serialized either way.
            var overflowed = new List<string>();
            foreach (var c in root.GetComponentsInChildren<VRCConstraintBase>(true))
            {
                if (c == null) continue;
                string path = GetHierarchyPath(c.transform);
                var so = new SerializedObject(c);
                var lenProp = so.FindProperty("Sources.totalLength");
                if (lenProp == null)
                {
                    lines.Add(CheckAvatar.DegradedPrefix + "`" + Cell(path) + "` (" + c.GetType().Name +
                              ") exposes no `Sources.totalLength` — its source list was not read");
                    continue;
                }

                int len = lenProp.intValue;
                if (len >= KeyableSlots)
                {
                    if (len > KeyableSlots) overflowed.Add("`" + Cell(path) + "`");
                    continue;
                }

                // For the duplicate fact below; object identity, never name.
                var inLength = new HashSet<UnityEngine.Object>();
                for (int i = 0; i < len; i++)
                {
                    var p = so.FindProperty("Sources.source" + i + ".SourceTransform");
                    if (p != null && p.objectReferenceValue != null) inLength.Add(p.objectReferenceValue);
                }

                for (int i = len; i < KeyableSlots; i++)
                {
                    var p = so.FindProperty("Sources.source" + i + ".SourceTransform");
                    if (p == null || p.objectReferenceValue == null) continue;

                    var wProp = so.FindProperty("Sources.source" + i + ".Weight");
                    string weight = wProp == null
                        ? "?"
                        : wProp.floatValue.ToString("0.####", CultureInfo.InvariantCulture);
                    var srcT = p.objectReferenceValue as Transform;
                    string srcName = srcT != null ? GetHierarchyPath(srcT) : p.objectReferenceValue.name;

                    // Rendered, never gated on: an inert constraint's slot is the same authored defect, and a
                    // layer can raise IsActive at any time.
                    string live = NotLiveReason(c, root.transform) ?? (c.IsActive ? null : "IsActive");

                    lines.Add("`" + Cell(path) + "` (" + c.GetType().Name + "): `source" + i + "` → `" +
                              Cell(srcName) + "` weight=" + weight + " sits past `totalLength`=" + len +
                              ", so the editor solves it and the client ignores it" +
                              (live == null ? "" : " [not-live: " + live + "]") +
                              (inLength.Contains(p.objectReferenceValue)
                                  ? "; the same transform is already a source inside the length"
                                  : "") +
                              ". " + SourceLengthFix);
                }
            }

            if (overflowed.Count > 0)
                lines.Add(CheckAvatar.ScopePrefix + SourceLengthScopeLine + " — " +
                          string.Join(", ", overflowed.ToArray()));
            return lines;
        }

        // ----- Other components (tier-2 generic census) -------------------------------------------

        private static int AppendOther(StringBuilder sb, GameObject root, HashSet<Component> tier1)
        {
            sb.Append("\n## Other components\n\n");
            sb.Append("_generic shallow inventory — type, host, object-reference attachments, and top-level scalar fields; for exhaustive/nested/asset depth use `AgentInspector.Run(<host path>)`._\n\n");
            int count = 0;
            var rows = new StringBuilder();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                // GetComponents<Component>() (not <MonoBehaviour>) so a MISSING script surfaces as null.
                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) { rows.Append("- **MISSING (broken script)** on `").Append(Cell(GetHierarchyPath(t))).Append("`\n"); count++; continue; }
                    if (comp is Transform) continue;
                    if (tier1.Contains(comp)) continue;
                    rows.Append("- **").Append(Cell(comp.GetType().FullName)).Append("** on `").Append(Cell(GetHierarchyPath(t))).Append("`\n");
                    AppendShallowFields(rows, comp);
                    count++;
                }
            }
            if (count == 0) sb.Append("_(none)_\n"); else sb.Append(rows);
            return count;
        }

        private static void AppendShallowFields(StringBuilder sb, Component comp)
        {
            var so = new SerializedObject(comp);
            var it = so.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false; // top-level iteration; EmitField descends exactly one struct level itself
                if (it.name == "m_Script") continue;
                EmitField(sb, it, 1, allowStruct: true);
            }
        }

        private static void EmitField(StringBuilder sb, SerializedProperty p, int indent, bool allowStruct)
        {
            string pad = new string(' ', indent * 2);
            switch (p.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    var o = p.objectReferenceValue;
                    if (o == null)
                    {
                        // A dangling ref (asset deleted) reads null with a nonzero instance id and must stay
                        // visible; a clean-empty slot is census noise and stays hidden.
                        if (p.objectReferenceInstanceIDValue != 0)
                            sb.Append(pad).Append("- ").Append(Cell(p.name)).Append(" → (broken: dangling reference)\n");
                        return;
                    }
                    string path = AssetDatabase.GetAssetPath(o);
                    string handle = !string.IsNullOrEmpty(path) ? path
                                  : o is Component oc ? GetHierarchyPath(oc.transform)
                                  : o is GameObject go ? GetHierarchyPath(go.transform)
                                  : "";
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string guid, out long _);
                    sb.Append(pad).Append("- ").Append(Cell(p.name)).Append(" → `").Append(Cell(o.name)).Append('`');
                    if (!string.IsNullOrEmpty(handle)) sb.Append(" (`").Append(Cell(handle)).Append("`)");
                    if (!string.IsNullOrEmpty(guid) && guid != "00000000000000000000000000000000") sb.Append(" guid=").Append(guid);
                    sb.Append('\n');
                    return;
                case SerializedPropertyType.Integer: case SerializedPropertyType.Boolean:
                case SerializedPropertyType.Float:   case SerializedPropertyType.String:
                case SerializedPropertyType.Enum:
                    sb.Append(pad).Append("- ").Append(Cell(p.name)).Append(" = ").Append(Cell(ScalarText(p))).Append('\n');
                    return;
                default:
                    if (p.isArray && p.propertyType != SerializedPropertyType.String)
                        sb.Append(pad).Append("- ").Append(Cell(p.name)).Append('[').Append(p.arraySize).Append("]\n");
                    else if (p.hasVisibleChildren && allowStruct)
                    {
                        sb.Append(pad).Append("- ").Append(Cell(p.name)).Append(":\n");
                        var end = p.GetEndProperty(); var ch = p.Copy(); bool e = true;
                        while (ch.NextVisible(e) && !SerializedProperty.EqualContents(ch, end))
                        { e = false; EmitField(sb, ch.Copy(), indent + 1, allowStruct: false); }
                    }
                    return;
            }
        }

        private static string ScalarText(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue.ToString();
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:   return F((float)p.doubleValue);
                case SerializedPropertyType.String:  string s = p.stringValue ?? ""; return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
                case SerializedPropertyType.Enum:    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length ? p.enumDisplayNames[p.enumValueIndex] : p.intValue.ToString();
                default: return p.propertyType.ToString();
            }
        }

        // ----- Field renderers --------------------------------------------------------------------

        private static string Shape(object shapeType, float radius, float height)
        {
            string st = shapeType != null ? shapeType.ToString() : "?";
            string s = st + " r=" + F(radius);
            if (st == "Capsule") s += " h=" + F(height);
            return s;
        }

        private static string Tags(List<string> tags)
        {
            if (tags == null || tags.Count == 0) return "—";
            return string.Join(",", tags.ToArray());
        }

        // Where the chain census sweeps for skinned meshes: the enclosing avatar, or a bare rig's outermost
        // ancestor. includeInactive is load-bearing: the default overload skips inactive objects, so it would
        // fall back to a staging container for exactly the parked avatars most likely to share one.
        private static Transform WeightScope(GameObject from)
        {
            var descriptor = from.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
            return descriptor != null ? descriptor.transform : from.transform.root;
        }

        // Resolved per row from the physbone, not the report root, which may sit above every descriptor (a
        // staging container) or below one (an armature, whose skinning meshes are its siblings).
        private static HashSet<Transform> SkinnedBonesFor(VRCPhysBone b, GameObject reportRoot,
                                                          Dictionary<Transform, HashSet<Transform>> cache)
        {
            var scope = WeightScope(b != null ? b.gameObject : reportRoot);
            if (scope == null) scope = reportRoot.transform.root;
            HashSet<Transform> set;
            if (!cache.TryGetValue(scope, out set)) { set = SkinnedBones(scope); cache[scope] = set; }
            return set;
        }

        // GetAllBoneWeights, not the legacy mesh.boneWeights, which drops an influence outside a vertex's top
        // four.
        private static HashSet<Transform> SkinnedBones(Transform scope)
        {
            var skinned = new HashSet<Transform>();
            if (scope == null) return skinned;
            foreach (var smr in scope.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                var mesh = smr.sharedMesh; if (mesh == null) continue;
                var bones = smr.bones;
                var bonesPerVertex = mesh.GetBonesPerVertex(); // one count per vertex
                var weights = mesh.GetAllBoneWeights();        // flat, grouped by vertex
                int ptr = 0;
                for (int v = 0; v < bonesPerVertex.Length; v++)
                {
                    int count = bonesPerVertex[v];
                    for (int j = 0; j < count; j++)
                    {
                        var w = weights[ptr + j];
                        if (w.weight <= 0f) continue;
                        int idx = w.boneIndex;
                        if (idx < 0 || idx >= bones.Length || bones[idx] == null) continue;
                        skinned.Add(bones[idx]);
                    }
                    ptr += count;
                }
            }
            return skinned;
        }

        // Three counts over the bones the component itself says are in its chain. The set comes from
        // `InitTransforms` because a hand-rolled exclusion fabricates an all-zero row: `ignoreTransforms` prunes
        // each listed transform and its children, `ignoreOtherPhysBones` prunes a nested chain's root and
        // everything under it, and an `ignoreTransforms` entry that is the chain root is a no-op.
        //
        // Measured in edit mode, `InitTransforms` does not throw or dirty anything and is idempotent. In play
        // mode the runtime has already built the list and is simulating it, so it is read as found.
        private static string ChainSubtreeCell(VRCPhysBone b, HashSet<Transform> skinned)
        {
            var chainRoot = EffectiveTarget(b);
            if (chainRoot == null) return "—";

            if (!Application.isPlaying) b.InitTransforms(true);
            var chain = b.bones;
            if (chain == null) return "—";

            int total = 0, skinnedCount = 0, hosting = 0;
            foreach (var bone in chain)
            {
                // Measured: `endpointPosition` does not introduce a null entry, so this guards a transform
                // destroyed under a live edit, not the endpoint.
                var t = bone.transform;
                if (t == null) continue;
                total++;
                if (skinned.Contains(t)) skinnedCount++;
                if (HostsBeyondOwnChain(t, b)) hosting++;
            }
            return "bones=" + total + " skinned=" + skinnedCount + " hosting=" + hosting;
        }

        // Type-blind, because enumerating families would miss the next one. A MISSING script counts: treating
        // it as empty could hand a reader a false all-zero row.
        private static bool HostsBeyondOwnChain(Transform t, VRCPhysBone self)
        {
            foreach (var c in t.GetComponents<Component>())
            {
                if (c is Transform) continue;
                if (ReferenceEquals(c, self)) continue;
                return true; // includes the null (MISSING-script) slot
            }
            return false;
        }

        // The SDK's own null-resolving getter, not a hand-rolled `rootTransform ?? transform`. A self-rooted
        // pair sharing one GameObject groups under this key too, which a rootTransform-only census misses.
        private static Transform EffectiveTarget(VRCPhysBone b)
        {
            var t = b.GetRootTransform();
            return t != null ? t : b.transform;
        }

        // Why a component is not running, or null when it is, relative to the report root: absolute
        // activeInHierarchy would invert the signal on an inactive root. The PhysBones table still prints
        // absolute `active`. A caller adds its family's own flag (VRCConstraintBase.IsActive,
        // IConstraint.constraintActive).
        private static string NotLiveReason(Component c, Transform root)
        {
            var beh = c as Behaviour;
            if (beh != null && !beh.enabled) return "enabled";
            for (var t = c.transform; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf) return "object";
            return null;
        }

        private static bool LiveUnder(VRCPhysBone b, Transform root) => NotLiveReason(b, root) == null;

        private static string LiveCell(string reason) => reason == null ? "1" : "0 (" + reason + ")";

        // Two physbones on one GameObject share a path, so it falls back to a component ordinal: GetComponents
        // order is stable, and an instance id is not stable across reloads.
        private static string HostHandle(VRCPhysBone b, List<VRCPhysBone> group)
        {
            int sharing = 0;
            foreach (var o in group) if (o.transform == b.transform) sharing++;
            string path = GetHierarchyPath(b.transform);
            if (sharing < 2) return path;
            var onHost = b.GetComponents<VRCPhysBone>();
            for (int i = 0; i < onHost.Length; i++)
                if (ReferenceEquals(onHost[i], b))
                    return path + " [VRCPhysBone#" + i + "]";
            return path;
        }

        // Grouped in first-seen order: a Dictionary's iteration order is unspecified and this feeds a RunLog
        // that must be byte-stable.
        private static List<KeyValuePair<Transform, List<VRCPhysBone>>> GroupPhysBonesByTarget(VRCPhysBone[] bones)
        {
            var order = new List<KeyValuePair<Transform, List<VRCPhysBone>>>();
            var index = new Dictionary<Transform, int>();
            foreach (var b in bones)
            {
                var t = EffectiveTarget(b);
                if (t == null) continue;
                int i;
                if (!index.TryGetValue(t, out i))
                {
                    i = order.Count;
                    index[t] = i;
                    order.Add(new KeyValuePair<Transform, List<VRCPhysBone>>(t, new List<VRCPhysBone>()));
                }
                order[i].Value.Add(b);
            }
            return order;
        }

        private static string RootIndirection(Transform rootTransform, Transform host)
        {
            if (rootTransform != null && rootTransform != host)
                return "`" + Cell(GetHierarchyPath(rootTransform)) + "`";
            return "—";
        }

        // Full scene-root-absolute hierarchy path.
        internal static string GetHierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        // ----- Helpers ----------------------------------------------------------------------------

        private static string Refuse(string why)
        {
            string err = "[ReportGimmick] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static bool HasKeys(AnimationCurve c) => c != null && c.length > 0;
        private static string Tilde(AnimationCurve c) => HasKeys(c) ? "~" : "";

        private static string CurvedCell(VRCPhysBoneBase b)
        {
            var all = new (string name, AnimationCurve curve)[]
            {
                ("pull", b.pullCurve), ("spring", b.springCurve), ("stiffness", b.stiffnessCurve),
                ("gravity", b.gravityCurve), ("gravityFalloff", b.gravityFalloffCurve), ("immobile", b.immobileCurve),
                ("radius", b.radiusCurve), ("maxAngleX", b.maxAngleXCurve), ("maxAngleZ", b.maxAngleZCurve),
                ("limitRotationX", b.limitRotationXCurve), ("limitRotationY", b.limitRotationYCurve), ("limitRotationZ", b.limitRotationZCurve),
                ("maxStretch", b.maxStretchCurve), ("stretchMotion", b.stretchMotionCurve), ("maxSquish", b.maxSquishCurve),
            };
            var curved = new List<string>();
            foreach (var c in all) if (HasKeys(c.curve)) curved.Add(c.name);
            return curved.Count == 0 ? "—" : string.Join(" ", curved);
        }

        private static string Cell(string s) => RunLogFormat.Cell(s);
    }
}
