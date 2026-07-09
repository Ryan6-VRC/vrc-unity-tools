using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only markdown TOPOLOGY digest of a gimmick subtree — the SPACE-dimension companion to the
    /// H report family's TIME dimension (ReportController / ReportClip / AnimatorLint). Walks a chosen
    /// subtree and renders, as factual tables, its VRC contacts, physbones (+ colliders), constraints
    /// (as a constrained→source edge-list with weights, affected-axis mask, and TargetTransform
    /// indirection made explicit), and its VRCFury AUTHORING inventory — plus a short Observations index
    /// naming only the five mechanically-certain structural idioms (world anchor, feedback loop,
    /// TargetTransform indirection, hold, editor/runtime swap), each carrying a docs/ pointer. So an
    /// agent can hold a whole gimmick in a few thousand tokens.
    ///
    /// Two seams are deliberately NOT crossed: it reports a contact/physbone's DECLARED `parameter`
    /// field but never traces it into an animator (H's domain), and it reports VRCFury features verbatim
    /// but predicts no bake output — no prefix rewrite, no sync-bit tally, no bake diff (J's domain).
    /// No verdict, no heuristic/"suspected" tier: it is a digest like ReportController, not a lint.
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

            // VRCFury is read untyped (no asmdef ref): match the component by full type name, decode via
            // SerializedObject. A project without VRCFury simply finds none and the section is _(none)_.
            var fury = new List<Component>();
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().FullName == "VF.Model.VRCFury") fury.Add(mb);

            var body = new StringBuilder();
            body.Append("# ReportGimmick: ").Append(root.name).Append('\n');
            body.Append("root: `").Append(GetHierarchyPath(root.transform)).Append("`  \n");
            body.Append("_transform handles are full scene-root-absolute paths; under first-match resolution a duplicate-named sibling makes a handle non-unique (a pre-existing family caveat, surfaced here because the interior walk can hit duplicate-named bones)._\n");

            int contactCount = senders.Length + receivers.Length;
            body.Append("\ncontacts=").Append(contactCount)
                .Append(" physbones=").Append(physbones.Length)
                .Append(" constraints=").Append(constraints.Length)
                .Append(" vrcfury=").Append(fury.Count).Append('\n');

            AppendContacts(body, senders, receivers);
            AppendPhysBones(body, physbones, colliders);
            AppendConstraints(body, constraints);
            var applyDuringUploadHosts = AppendVrcFury(body, fury);
            int obsCount = AppendObservations(body, constraints, applyDuringUploadHosts);

            var summary = "[ReportGimmick] " + root.name
                        + ": contacts=" + contactCount
                        + " physbones=" + physbones.Length
                        + " constraints=" + constraints.Length
                        + " vrcfury=" + fury.Count
                        + " observations=" + obsCount + " => OK";
            var result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "gimmick_" + root.name, summary, body.ToString(), ".md");
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

        private static void AppendConstraints(StringBuilder sb, VRCConstraintBase[] constraints)
        {
            sb.Append("\n## Constraints (edge-list: constrained → source)\n\n");
            if (constraints.Length == 0) { sb.Append("_(none)_\n"); return; }
            sb.Append("_source weights normalize by SUM (docs/runtime.md §Constraints) — a weight is not a clamped 0..1 absolute._\n\n");
            sb.Append("| type | constrained transform | source transform | weight | affected axes | note |\n");
            sb.Append("|---|---|---|---|---|---|\n");

            var axisMissTypes = new HashSet<string>(); // fail-loud: types whose axis mask couldn't be read
            foreach (var c in constraints)
            {
                var host = c.transform;
                // Unity fake-null test, never ?? — ?? bypasses UnityEngine.Object's overloaded == and would
                // read a destroyed TargetTransform as a live object instead of falling back to the host.
                var driven = c.TargetTransform != null ? c.TargetTransform : host;
                string type = c.GetType().Name;
                string axes = AxisMask(c, out bool axisMiss);
                if (axisMiss) axisMissTypes.Add(type);
                string note = ConstraintNote(c, host, driven);

                int count = c.Sources.Count;
                if (count == 0)
                {
                    // Never invisible: a source-less constraint still gets a row (its note carries hold/anchor).
                    Row(sb, type, GetHierarchyPath(driven), "(none)", "w=— g=" + F(c.GlobalWeight), axes, note);
                    continue;
                }
                for (int i = 0; i < count; i++)
                {
                    var s = c.Sources[i];
                    // Null-check SourceTransform first: an unwired source slot is legal (seen on real avatars)
                    // and must render (none) rather than NRE mid-report.
                    string src = s.SourceTransform != null ? GetHierarchyPath(s.SourceTransform) : "(none)";
                    string weight = "w=" + F(s.Weight) + " g=" + F(c.GlobalWeight);
                    // Constraint-level note on the first row only (avoid N-fold repetition across sources).
                    Row(sb, type, GetHierarchyPath(driven), src, weight, axes, i == 0 ? note : "");
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

        private static int AppendObservations(StringBuilder sb, VRCConstraintBase[] constraints, List<Transform> applyHosts)
        {
            var lines = new List<string>();
            foreach (var c in constraints)
            {
                var host = c.transform;
                var driven = c.TargetTransform != null ? c.TargetTransform : host;
                int count = c.Sources.Count;

                // a. Per-client world anchor: FreezeToWorld && zero sources.
                if (c.FreezeToWorld && count == 0)
                    lines.Add("**per-client world anchor** — `" + Cell(GetHierarchyPath(driven)) + "` — docs/gimmicks.md §Constraint patterns · World anchors");

                // b. Feedback loop: a non-null source that is a STRICT descendant of driven. The
                //    source != driven guard is load-bearing — IsChildOf is self-inclusive and
                //    self-as-source at partial weight is the "feel"-damping idiom, not a feedback cage.
                for (int i = 0; i < count; i++)
                {
                    var src = c.Sources[i].SourceTransform;
                    if (src != null && src != driven && src.IsChildOf(driven))
                    {
                        lines.Add("**feedback loop (self-referential constraint source)** — `" + Cell(GetHierarchyPath(driven)) + "` ← `" + Cell(GetHierarchyPath(src)) + "` — docs/gimmicks.md §Constraint patterns · Trilateration cage / Crawler servo");
                        break; // one line per constraint is enough for the index
                    }
                }

                // c. TargetTransform indirection.
                if (c.TargetTransform != null && c.TargetTransform != host)
                    lines.Add("**TargetTransform indirection** — `" + Cell(GetHierarchyPath(driven)) + "` (host `" + Cell(host.name) + "`) — docs/runtime.md §Constraints");

                // d. Hold (label matches the §5.3 note — active world anchors are excluded; weight-based
                //    holds carry the static-frame caveat; a pure zero-source hold does not).
                string hold = HoldSuffix(c);
                if (hold != null)
                    lines.Add("**hold** — `" + Cell(GetHierarchyPath(driven)) + "`" + hold + " — docs/runtime.md §Constraints");
            }

            // e. Editor/runtime swap (VRCFury ApplyDuringUpload host).
            foreach (var h in applyHosts)
                lines.Add("**editor/runtime swap** — `" + Cell(GetHierarchyPath(h)) + "` — docs/gimmicks.md §Constraint patterns · Editor/runtime swap");

            sb.Append("\n## Observations\n\n");
            if (lines.Count == 0) sb.Append("_(none)_\n");
            else foreach (var l in lines) sb.Append("- ").Append(l).Append('\n');
            return lines.Count;
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
