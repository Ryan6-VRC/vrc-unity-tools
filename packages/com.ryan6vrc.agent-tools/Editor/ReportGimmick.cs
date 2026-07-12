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
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only markdown TOPOLOGY digest of a gimmick subtree — the SPACE-dimension companion to the
    /// H report family's TIME dimension (ReportController / ReportClip / CheckAnimator). Walks a chosen
    /// subtree and renders, as factual tables, its VRC contacts, physbones (+ colliders), constraints
    /// (as a constrained→source edge-list with weights, affected-axis mask, and TargetTransform
    /// indirection made explicit), and its VRCFury AUTHORING inventory — plus a short Observations index
    /// naming only the five mechanically-certain structural idioms (world anchor, feedback loop,
    /// TargetTransform indirection, hold, editor/runtime swap), each carrying a docs/ pointer. So an
    /// agent can hold a gimmick subtree in a few thousand tokens — the digest scales with component
    /// count (the tier-2 census names renderers/animators too), so a whole-avatar subtree is a
    /// proportionally large, honest digest, not a compact one; scope the root to the gimmick.
    ///
    /// Two seams are deliberately NOT crossed: it reports a contact/physbone's DECLARED `parameter`
    /// field but never traces it into an animator (H's domain), and it reports VRCFury features verbatim
    /// but predicts no bake output — no prefix rewrite, no sync-bit tally, no bake diff (J's domain).
    /// No verdict, no heuristic/"suspected" tier: it is a digest like ReportController, not a lint.
    ///
    /// SUBTREE-COMPLETE BY CONSTRUCTION: the tier-1 tables above interpret the known gimmick families
    /// (contacts, physbones+colliders, VRC/Unity constraints, VRCFury), and a generic tier-2 "Other
    /// components" census then names EVERY remaining component (Modular Avatar, VRCLens, custom scripts)
    /// plus every MISSING/broken-script slot — with its object-reference seam and a SHALLOW scalar peek
    /// (top-level + one struct level; arrays as `name[N]`; no asset-following, no deep recursion). Nothing
    /// in the subtree is invisible. AgentInspector is the door to exhaustive/nested/asset depth beyond
    /// that shallow peek. VRCFury stays tier-1 (not folded into tier-2) because its seam is a nested
    /// polymorphic `content[]` managed-reference array that a generic top-level walk renders as noise;
    /// MA and most scripts expose their seam as top-level fields the shallow peek reads directly.
    ///
    /// INSPECTION ONLY — never mutates. Emits one line carrying the artifact path in-band.
    /// </summary>
    [AgentTool]
    public static class ReportGimmick
    {
        // ----- Agent entry point ------------------------------------------------------------------

        /// <summary>Digest the gimmick subtree rooted at <paramref name="rootPath"/> (a scene hierarchy
        /// path in the active scene) to markdown under Snapshots/. Returns a one-line summary ending with
        /// the artifact path in-band (<c>… =&gt; OK | log=&lt;path&gt;</c>); a null/empty/unresolved
        /// <paramref name="rootPath"/> is a bare-FAIL with no trailer (nothing was written). A valid but
        /// component-empty subtree is an honest zero-count artifact, not a refusal.</summary>
        public static string Report(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath)) return Refuse("rootPath is null/empty");
            var root = FindByHierarchyPath(rootPath);
            if (root == null) return Refuse("rootPath '" + rootPath + "' did not resolve to a GameObject in the active scene");

            // ---- Collect once (full descent; includeInactive is mandatory — runtime-swap scaffolding
            //      lives on inactive GameObjects and must be seen). Bounded by component count, not
            //      transform count, so no depth cap: a large honest count is a real signal. -------------
            var senders     = root.GetComponentsInChildren<VRCContactSender>(true);
            var receivers   = root.GetComponentsInChildren<VRCContactReceiver>(true);
            var physbones   = root.GetComponentsInChildren<VRCPhysBone>(true);
            var colliders   = root.GetComponentsInChildren<VRCPhysBoneCollider>(true);
            var constraints = root.GetComponentsInChildren<VRCConstraintBase>(true);
            // Interface query, not a concrete-type union: Unity constraints derive from Behaviour (not
            // MonoBehaviour), so a per-type list would let an unlisted Unity constraint slip through. VRC
            // constraints do NOT implement UnityEngine's IConstraint, so the two families never overlap.
            var unityConstraints = root.GetComponentsInChildren<IConstraint>(true);
            var constraintRows = BuildConstraintRows(constraints, unityConstraints);
            int constraintCount = constraints.Length + unityConstraints.Length;

            // VRCFury is read untyped (no asmdef ref): match the component by full type name, decode via
            // SerializedObject. A project without VRCFury simply finds none and the section is _(none)_.
            var fury = new List<Component>();
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().FullName == "VF.Model.VRCFury") fury.Add(mb);

            var body = new StringBuilder();

            // Tier-1 exclusion set for the tier-2 census: every component a table above already interprets.
            var tier1 = new HashSet<Component>();
            foreach (var a in senders) tier1.Add(a);
            foreach (var a in receivers) tier1.Add(a);
            foreach (var a in physbones) tier1.Add(a);
            foreach (var a in colliders) tier1.Add(a);
            foreach (var a in constraints) tier1.Add(a);
            foreach (var a in unityConstraints) tier1.Add((Component)a);
            foreach (var a in fury) tier1.Add(a);

            int contactCount = senders.Length + receivers.Length;
            // Header count line is emitted AFTER the tier-1 tables/observations run, because `other` is only
            // known once AppendOther has walked the subtree — so build the digest body first, then prepend.
            AppendContacts(body, senders, receivers);
            AppendPhysBones(body, physbones, colliders);
            AppendConstraints(body, constraintRows);
            var applyDuringUploadHosts = AppendVrcFury(body, fury);
            int obsCount = AppendObservations(body, constraintRows, applyDuringUploadHosts);
            int other = AppendOther(body, root, tier1);

            // Header carries the count line, which needs `other` — known only after AppendOther walked the
            // subtree — so the digest body is built first, then wrapped by the header into the final doc.
            var doc = new StringBuilder();
            doc.Append("# ReportGimmick: ").Append(root.name).Append('\n');
            doc.Append("root: `").Append(GetHierarchyPath(root.transform)).Append("`  \n");
            doc.Append("_transform handles are full scene-root-absolute paths; under first-match resolution a duplicate-named sibling makes a handle non-unique (a pre-existing family caveat, surfaced here because the interior walk can hit duplicate-named bones)._\n");
            doc.Append("\ncontacts=").Append(contactCount)
                  .Append(" physbones=").Append(physbones.Length)
                  .Append(" constraints=").Append(constraintCount)
                  .Append(" vrcfury=").Append(fury.Count)
                  .Append(" other=").Append(other).Append('\n');
            doc.Append(body);

            var summary = "[ReportGimmick] " + root.name
                        + ": contacts=" + contactCount
                        + " physbones=" + physbones.Length
                        + " constraints=" + constraintCount
                        + " vrcfury=" + fury.Count
                        + " other=" + other
                        + " observations=" + obsCount + " => OK";
            var result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "gimmick_" + root.name, summary, doc.ToString(), ".md");
            Debug.Log(result);
            return result;
        }

        // ----- Contacts (§5.1) --------------------------------------------------------------------

        private static void AppendContacts(StringBuilder sb, VRCContactSender[] senders, VRCContactReceiver[] receivers)
        {
            sb.Append("\n## Contacts\n\n");
            if (senders.Length == 0 && receivers.Length == 0) { sb.Append("_(none)_\n"); return; }
            sb.Append("| transform | kind | shape | tags | receiverType | filters | localOnly | parameter | rootTransform |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|\n");

            // Senders carry no receiverType / filters / parameter (those are receiver-only fields).
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

        // ----- PhysBones + collider companion (§5.2) ----------------------------------------------

        private static void AppendPhysBones(StringBuilder sb, VRCPhysBone[] bones, VRCPhysBoneCollider[] colliders)
        {
            sb.Append("\n## PhysBones\n\n");
            if (bones.Length == 0) sb.Append("_(none)_\n");
            else
            {
                sb.Append("| transform | rootTransform | parameter prefix | grab/pose | forces | immobile | flags |\n");
                sb.Append("|---|---|---|---|---|---|---|\n");
                foreach (var b in bones)
                    sb.Append("| `").Append(Cell(GetHierarchyPath(b.transform))).Append("` | ")
                      .Append(RootIndirection(b.rootTransform, b.transform)).Append(" | ")
                      .Append(string.IsNullOrEmpty(b.parameter) ? "—" : "`" + Cell(b.parameter) + "`").Append(" | ")
                      .Append("grab=").Append(b.allowGrabbing).Append(" pose=").Append(b.allowPosing).Append(" grabMove=").Append(F(b.grabMovement)).Append(" | ")
                      .Append("pull=").Append(F(b.pull)).Append(" spring=").Append(F(b.spring)).Append(" stiffness=").Append(F(b.stiffness)).Append(" | ")
                      .Append(F(b.immobile)).Append(" (").Append(b.immobileType).Append(") | ")
                      .Append("isAnimated=").Append(b.isAnimated ? "1" : "0").Append(" resetWhenDisabled=").Append(b.resetWhenDisabled ? "1" : "0").Append(" |\n");
            }

            // Colliders are ingredients, not behaviour — a minimal companion table.
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

        // ----- Constraints edge-list (§5.3) -------------------------------------------------------

        // One edge-list row's worth of constraint facts, family-neutral. VRC and Unity constraints fill
        // this through separate extractors — they share the rendered Row(), never each other's readers.
        private struct ConstraintRow
        {
            public string Type;
            public string Driven;                        // hierarchy path of the driven transform
            public (string src, float weight)[] Sources; // src="(none)" when unwired; empty => source-less
            public float GlobalWeight;
            public string Axes;                          // rendered mask, or "—"
            public bool AxisMiss;                        // VRC-only; Unity never sets this
            public string Note;                          // VRC-only idioms; "" for Unity
            public Transform DrivenTransform;            // for the feedback-loop observation
            public Transform[] SourceTransforms;         // for the feedback-loop observation
            public VRCConstraintBase Vrc;                // family discriminator; null for Unity constraints
        }

        // Normalize both constraint families into one row list. Order is VRC-first, then Unity — VRC output
        // stays byte-for-byte what it was (VRC rows render through the same reads as before).
        private static ConstraintRow[] BuildConstraintRows(VRCConstraintBase[] vrc, IConstraint[] unity)
        {
            var rows = new List<ConstraintRow>(vrc.Length + unity.Length);
            foreach (var c in vrc) rows.Add(FromVrc(c));
            foreach (var c in unity) rows.Add(FromUnity(c));
            return rows.ToArray();
        }

        // VRC extractor — reuses the EXISTING VRC reads (TargetTransform-or-host driven, Sources with
        // (none) for unwired slots, AxisMask, ConstraintNote) unchanged, so VRC rows are identical to
        // the pre-widening output.
        private static ConstraintRow FromVrc(VRCConstraintBase c)
        {
            var host = c.transform;
            // Unity fake-null test, never ?? — ?? bypasses UnityEngine.Object's overloaded == and would
            // read a destroyed TargetTransform as a live object instead of falling back to the host.
            var driven = c.TargetTransform != null ? c.TargetTransform : host;
            var sources = new List<(string, float)>();
            var srcTransforms = new List<Transform>();
            int count = c.Sources.Count;
            for (int i = 0; i < count; i++)
            {
                var s = c.Sources[i];
                // Null-check SourceTransform first: an unwired source slot is legal (seen on real avatars)
                // and must render (none) rather than NRE mid-report.
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
            };
        }

        // Unity IConstraint extractor. Unity constraints always drive their own host (no TargetTransform),
        // have no FreezeToWorld/hold, and read axes from per-type Axis flags — so Note stays "" and AxisMiss
        // stays false. Only the geometric feedback-loop observation (source ⊂ driven) transfers.
        private static ConstraintRow FromUnity(IConstraint c)
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
            };
        }

        // Per-type Axis-flag mask for Unity constraints. Each type exposes its own flags; LookAt has none.
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
            sb.Append("| type | constrained transform | source transform | weight | affected axes | note |\n");
            sb.Append("|---|---|---|---|---|---|\n");

            var axisMissTypes = new HashSet<string>(); // fail-loud: types whose axis mask couldn't be read
            foreach (var row in rows)
            {
                if (row.AxisMiss) axisMissTypes.Add(row.Type);

                if (row.Sources.Length == 0)
                {
                    // Never invisible: a source-less constraint still gets a row (its note carries hold/anchor).
                    Row(sb, row.Type, row.Driven, "(none)", "w=— g=" + F(row.GlobalWeight), row.Axes, row.Note);
                    continue;
                }
                for (int i = 0; i < row.Sources.Length; i++)
                {
                    var s = row.Sources[i];
                    string weight = "w=" + F(s.weight) + " g=" + F(row.GlobalWeight);
                    // Constraint-level note on the first row only (avoid N-fold repetition across sources).
                    Row(sb, row.Type, row.Driven, s.src, weight, row.Axes, i == 0 ? row.Note : "");
                }
            }
            foreach (var tn in axisMissTypes)
                sb.Append("\n> note: could not read the affected-axis mask for `").Append(Cell(tn))
                  .Append("` (Affects* members not resolvable by name) — the `—` in its axes column is a READ MISS, not all-axes-off\n");
        }

        private static void Row(StringBuilder sb, string type, string driven, string src, string weight, string axes, string note)
        {
            sb.Append("| ").Append(type)
              .Append(" | `").Append(Cell(driven)).Append("` | ")
              .Append(src == "(none)" ? "(none)" : "`" + Cell(src) + "`").Append(" | ")
              .Append(Cell(weight)).Append(" | ").Append(Cell(axes)).Append(" | ")
              .Append(string.IsNullOrEmpty(note) ? "" : Cell(note)).Append(" |\n");
        }

        // The mechanically-decidable per-constraint facts (§5.3 note column).
        private static string ConstraintNote(VRCConstraintBase c, Transform host, Transform driven)
        {
            var parts = new List<string>();
            if (c.TargetTransform != null && c.TargetTransform != host)
                parts.Add("TargetTransform indirection (host=`" + host.name + "`)");
            if (c.FreezeToWorld)
                // The world-anchor IDIOM is the source-less case only (parity with observation §6a);
                // a FreezeToWorld constraint that still has sources is just a frozen-frame flag.
                parts.Add(c.Sources.Count == 0 ? "FreezeToWorld (per-client world anchor)" : "FreezeToWorld");
            string hold = HoldSuffix(c);
            if (hold != null) parts.Add("hold" + hold);
            if (c.Locked) parts.Add("locked");
            return string.Join("; ", parts);
        }

        // Hold = writes nothing at runtime. Returns null if NOT a hold; otherwise the parenthetical
        // SUFFIX to append after "hold" ("" for a plain hold). The kinds, and why the caveat differs:
        //  - an ACTIVE FreezeToWorld (GlobalWeight != 0) is NOT a hold — it actively freezes to world;
        //  - a PURE zero-source constraint (no FreezeToWorld) is a genuine structural no-op → plain "hold"
        //    (there is nothing animatable that could drive it — sources are structural, not a clip target);
        //  - a hold that arises from ZEROED WEIGHTS or GlobalWeight==0 carries a static-frame caveat,
        //    because a toggle clip can animate those weights (H's domain, invisible here);
        //  - the ≥2-non-null-all-zero sub-case keeps the richer rest-shape wording.
        private static string HoldSuffix(VRCConstraintBase c)
        {
            if (c.FreezeToWorld && !Mathf.Approximately(c.GlobalWeight, 0f)) return null; // active world anchor
            int count = c.Sources.Count;
            if (count == 0) return ""; // pure zero-source structural no-op (or inert FreezeToWorld+GlobalWeight==0)
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
            // The caveat is earned only when a NON-NULL source exists whose weight is zeroed — that
            // weight is a clip animation target (H's domain, invisible here). All source slots unwired
            // (nonNull==0) is structural, like zero-source: nothing animatable to drive it → plain "hold".
            if (nonNull >= 1 && Mathf.Approximately(total, 0f))
                return " (static frame — clip-driven weighting not visible here)";
            if (nonNull == 0) return ""; // count>0 but every source slot unwired — structural no-op
            return null; // non-null sources contribute non-zero weight — not a hold
        }

        // The nine Affects* bools live per-type on intermediate bases (Parent has pos+rot; Position/
        // Rotation/Aim/Scale have their one group; LookAt has none), NOT on VRCConstraintBase — so read
        // them by reflection-by-name and include only the group(s) actually present on this component.
        // `miss` is set true when a NON-LookAt type resolves ZERO axis groups: `—` legitimately means
        // "LookAt / all-off", so a reflection miss (e.g. an SDK field→property rename) must be loud, not
        // an honest-looking `—`. The reader (AppendConstraints) turns `miss` into a `> note:`.
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
            // LookAt genuinely has no per-axis Affects group; any other type resolving none is a miss.
            miss = groupsResolved == 0 && t.Name != "VRCLookAtConstraint";
            return parts.Count == 0 ? "—" : string.Join(" ", parts);
        }

        private static string AxisGroup(VRCConstraintBase c, Type t, string prefix, string label, ref int groupsResolved)
        {
            // Resolve each axis member as a property OR field (robust to an SDK field→property change);
            // the three travel as a trio, so a trio that resolves counts as a present group (whatever its
            // truth values) — that is what distinguishes "group absent" from "read miss" for `miss`.
            bool? x = ReadBoolMember(c, t, prefix + "X");
            bool? y = ReadBoolMember(c, t, prefix + "Y");
            bool? z = ReadBoolMember(c, t, prefix + "Z");
            if (x == null || y == null || z == null) return null; // this type has no such axis group
            groupsResolved++;
            if (!x.Value && !y.Value && !z.Value) return null; // present but fully off — omit
            if (x.Value && y.Value && z.Value) return label + "*";
            return label + (x.Value ? "X" : "") + (y.Value ? "Y" : "") + (z.Value ? "Z" : "");
        }

        // A bool member read as property-or-field; null when neither resolves as a bool. internal (shared):
        // PlayGateCore reads the emulator flags through this same SDK field→property-robust reflection
        // rather than duplicating it — the reuse pattern GetHierarchyPath already set.
        internal static bool? ReadBoolMember(object c, Type t, string name)
        {
            var p = t.GetProperty(name);
            if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(c);
            var f = t.GetField(name);
            if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(c);
            return null;
        }

        // ----- VRCFury authoring inventory (§5.4) — returns the ApplyDuringUpload host set for §6e ----

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

                // Fail loud when the feature type itself is not resolvable by name: observation §6e
                // (editor/runtime swap) keys off the ApplyDuringUpload leaf, so an unnamed feature must
                // never read as an honest "not that type".
                if (string.IsNullOrEmpty(tn))
                    sb.Append("  > note: feature `managedReferenceFullTypename` is empty — type not resolvable by name; FullController/ApplyDuringUpload decode (and editor/runtime-swap observation) may be incomplete\n");

                if (leaf == "FullController")
                    AppendFullController(sb, content, hostPath);
                else if (leaf == "ApplyDuringUpload")
                {
                    // Dedup: two VRCFury components on one GameObject would otherwise spam identical §6e lines.
                    if (!applyHosts.Contains(c.transform)) applyHosts.Add(c.transform);
                    sb.Append("  - toggles this GameObject at upload (editor/runtime-swap ingredient)\n");
                }
            }
            return applyHosts;
        }

        // Decode the FullController asset references (where H's ReportController picks up). Fail loud with
        // a `> note:` whenever an expected serialized array/wrapper is not resolvable — a skipped check
        // must never read as an honest zero.
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

        // ----- Observations (§6) — five mechanically-certain idioms, each a fact + docs pointer -------

        private static int AppendObservations(StringBuilder sb, ConstraintRow[] rows, List<Transform> applyHosts)
        {
            var lines = new List<string>();
            foreach (var row in rows)
            {
                var driven = row.DrivenTransform;
                var c = row.Vrc; // null for Unity constraints — the VRC-only idioms below are skipped

                // a. Per-client world anchor: FreezeToWorld && zero sources (VRC idiom only).
                if (c != null && c.FreezeToWorld && row.Sources.Length == 0)
                    lines.Add("**per-client world anchor** — `" + Cell(GetHierarchyPath(driven)) + "` — docs/gimmicks.md §Constraint patterns · World anchors");

                // b. Feedback loop: a non-null source that is a STRICT descendant of driven. The one
                //    geometric idiom that transfers to Unity constraints too. The source != driven guard
                //    is load-bearing — IsChildOf is self-inclusive and self-as-source at partial weight is
                //    the "feel"-damping idiom, not a feedback cage.
                foreach (var src in row.SourceTransforms)
                {
                    if (src != null && src != driven && src.IsChildOf(driven))
                    {
                        lines.Add("**feedback loop (self-referential constraint source)** — `" + Cell(GetHierarchyPath(driven)) + "` ← `" + Cell(GetHierarchyPath(src)) + "` — docs/gimmicks.md §Constraint patterns · Trilateration cage / Crawler servo");
                        break; // one line per constraint is enough for the index
                    }
                }

                // c. TargetTransform indirection (VRC idiom only).
                if (c != null && c.TargetTransform != null && c.TargetTransform != c.transform)
                    lines.Add("**TargetTransform indirection** — `" + Cell(GetHierarchyPath(driven)) + "` (host `" + Cell(c.transform.name) + "`) — docs/runtime.md §Constraints");

                // d. Hold (VRC idiom only — label matches the §5.3 note; active world anchors are excluded;
                //    weight-based holds carry the static-frame caveat; a pure zero-source hold does not).
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

            sb.Append("\n## Observations\n\n");
            if (lines.Count == 0) sb.Append("_(none)_\n");
            else foreach (var l in lines) sb.Append("- ").Append(l).Append('\n');
            return lines.Count;
        }

        // ----- Other components (tier-2 generic census) -------------------------------------------

        // Tier-2 generic inventory. Domain-blind: names every component a tier-1 table didn't, plus MISSING
        // scripts, with its object-ref seam and a SHALLOW scalar peek (one struct level, no array/asset
        // expansion). Depth beyond that is AgentInspector's job. Returns the row count for other=N.
        private static int AppendOther(StringBuilder sb, GameObject root, HashSet<Component> tier1)
        {
            sb.Append("\n## Other components\n\n");
            sb.Append("_generic shallow inventory — type, host, object-reference attachments, and top-level scalar fields; for exhaustive/nested/asset depth use `AgentInspector.Snapshot(<host path>)`._\n\n");
            int count = 0;
            var rows = new StringBuilder();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                // GetComponents<Component>() (not <MonoBehaviour>) so a MISSING script surfaces as null.
                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp == null) { rows.Append("- **MISSING (broken script)** on `").Append(Cell(GetHierarchyPath(t))).Append("`\n"); count++; continue; }
                    if (comp is Transform) continue;                 // excludes RectTransform too
                    if (tier1.Contains(comp)) continue;              // physbones/contacts/colliders/constraints/VRCFury
                    rows.Append("- **").Append(Cell(comp.GetType().FullName)).Append("** on `").Append(Cell(GetHierarchyPath(t))).Append("`\n");
                    AppendShallowFields(rows, comp);
                    count++;
                }
            }
            if (count == 0) sb.Append("_(none)_\n"); else sb.Append(rows);
            return count;
        }

        // One-struct-level peek: object refs (name + path + guid) and primitive/enum/string fields. Arrays
        // render as name[N]; no recursion past one struct level; no asset-following; m_Script skipped.
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
                        // Distinguish a clean-empty slot from a dangling ref (asset deleted) — same
                        // empty-vs-broken idiom ReportController.MotionNullCell uses; a broken seam must
                        // never collapse to invisible. Clean-null stays hidden (census noise).
                        if (p.objectReferenceInstanceIDValue != 0)
                            sb.Append(pad).Append("- ").Append(Cell(p.name)).Append(" → (broken: dangling reference)\n");
                        return;
                    }
                    string path = AssetDatabase.GetAssetPath(o);
                    // Asset ref → asset path; scene ref (Component or GameObject) → its hierarchy path.
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
                        { e = false; EmitField(sb, ch.Copy(), indent + 1, allowStruct: false); } // exactly one level
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

        // A rootTransform that is non-null (fake-null) and != the host means the shape/chain acts at that
        // transform, not the host — parity with constraint TargetTransform indirection. Blank/self → —.
        private static string RootIndirection(Transform rootTransform, Transform host)
        {
            if (rootTransform != null && rootTransform != host)
                return "`" + Cell(GetHierarchyPath(rootTransform)) + "`";
            return "—";
        }

        // ----- Scene resolver (duplicated from AgentInspector.FindByHierarchyPath — kept local so this
        //        tool adds no cross-file coupling; first match wins among duplicate-named siblings) -----
        private static GameObject FindByHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segs = path.Trim('/').Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != segs[0]) continue;
                Transform t = root.transform;
                bool ok = true;
                for (int i = 1; i < segs.Length && ok; i++)
                {
                    t = t.Find(segs[i]);
                    if (t == null) ok = false;
                }
                if (ok) return t.gameObject;
            }
            return null;
        }

        // Full scene-root-absolute hierarchy path (copied verbatim from AgentInspector.GetHierarchyPath).
        // internal (not private): PlayGateCore reuses this one path grammar rather than authoring a second.
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

        // Keep cell text on one table row: escape the column delimiter and collapse newlines.
        private static string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
