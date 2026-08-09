using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using System.Linq;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only geometric digest of how a set of blendshapes on ONE mesh overlap in the vertices they
    /// deform. Built for the F38 hazard: a base body shape left worn (e.g. <c>Shrink_stocking</c>) while a
    /// composed outfit's <c>ShapeChanger</c> drives its own shrink over the SAME body vertices — the two
    /// subtractions stack into an inverted limb, invisible to the render sheet and to CheckSeam/CheckAvatar.
    ///
    /// This is a <b>Report, not a Check</b>: it measures geometry and flags candidate pairs, but never emits a
    /// PASS/FAIL verdict — whether an overlap is a bug or a wanted coupling depends on the FX / ShapeChanger
    /// graph the agent reads, not on the deltas. The agent supplies the candidate co-active set; the tool
    /// returns which pairs geometrically collide, and the agent adjudicates wanted-vs-defect.
    ///
    /// Method (per <see cref="Analyze"/>): the authored per-vertex deltas of each shape's last frame
    /// (weight-independent) define a <i>touched set</i> — vertices moved past a two-tier noise floor
    /// (<see cref="AbsFloorMeters"/> OR <see cref="RelFrac"/> of the shape's own ~95th-pctile magnitude; the
    /// relative tier discards stray authoring verts on messy assets). Overlap is reported as containment:
    /// <c>|A∩B| / min(|A|,|B|)</c> — "is the smaller shape's territory swallowed by the larger", which is
    /// exactly the double-subtraction condition. Thresholds are deliberately NOT calibrated to any one asset;
    /// they are conservative floors, and the report leaves the judgement to the reader.
    ///
    /// INSPECTION ONLY — never mutates the mesh or project.
    /// </summary>
    [AgentTool]
    public static class ReportShapeOverlap
    {
        // ── Noise/flag knobs. Conservative floors, NOT tuned to any sampled asset — a clean vendor mesh
        //    never fires the relative tier, but a messy one will, so both stay. The flag threshold classifies
        //    (marks a pair for the reader's attention); it is not a verdict. ────────────────────────────────
        internal const float AbsFloorMeters = 0.0001f; // 0.1mm: sub-visual authoring jitter floor
        internal const float RelFrac        = 0.10f;   // OR 10% of the shape's own p95 magnitude
        internal const float PercentileScale = 0.95f;  // "shape scale" = 95th-pctile of nonzero |delta|
        internal const float FlagContainment = 0.30f;  // containment ≥ this ⇒ flagged (attention, not verdict)

        // ── Data the pure core returns (directly asserted by tests) ─────────────────────────────────────────
        internal struct Footprint { public string Name; public bool Missing; public int Touched; public float P95; public float Threshold; }
        internal struct Pair { public string A; public string B; public int Intersect; public int MinFootprint; public float Containment; }

        // Per-shape record of the MA ShapeChanger reaction(s) declaring it. ChangeType/Value are the LAST
        // declared (matching ReactionTypes' last-wins), but every declaration is kept in Declares so two rows
        // asking for different (type,value) surface as a Conflict instead of silently collapsing to last-wins.
        internal class ReactionInfo
        {
            public int ChangeType;          // MA ShapeChangeType of the last declaration (Delete=0, Set=1)
            public float Value;             // Set value of the last declaration (irrelevant for Delete)
            public readonly List<(int type, float value)> Declares = new List<(int type, float value)>();
            public bool Conflict => Declares.Distinct().Count() > 1; // 2+ differing (type,value) rows on one shape
            // Path of the nearest ancestor that is activeSelf=false and NOT toggle-driven, between the declaring
            // ShapeChanger and the caller's outfitRoot; null when the chain is clear. A declaration behind one
            // does not apply at the build's initial state, so it must not suppress this shape's MISMATCH.
            public string InactiveAncestor;
        }

        // Minimal ingestion record fed to Analyze. Names is the ORDERED, deduped co-active union
        // ({passed} ∪ {worn} ∪ {reaction-targeted}). ReactionTypes captures each reaction-driven shape's MA
        // ShapeChangeType (Delete=0, Set=1); Reactions additionally carries the declared Value(s) + conflict so
        // Emit can render the resolved-target column and surface multi-declaration conflicts.
        internal class Ingested
        {
            public List<string> Names = new List<string>();
            public Dictionary<string, int> ReactionTypes = new Dictionary<string, int>();
            public Dictionary<string, ReactionInfo> Reactions = new Dictionary<string, ReactionInfo>();
            // The caller's own root is inactive: reported once as a note, never charged to the rows. Turning a
            // piece off is the prescribed way to drive this tool (`map-outfit-shapes`), so treating the root
            // like an authored inactive ancestor would mark every declaration dead in one stroke.
            public bool OutfitRootInactive;
            // The ObjectToggle probe drifted, so toggle-driven ancestors cannot be told from genuinely-parked
            // ones. The active-state signal is then WITHHELD rather than guessed: a wrong guess here invents
            // MISMATCH rows, and inventing offenders is worse than the blind spot this signal closes.
            public bool ActiveStateUnknown;
        }
        internal class Analysis
        {
            public int VertexCount;
            public List<Footprint> Footprints = new List<Footprint>();
            public List<Pair> Pairs = new List<Pair>();
            public List<string> Missing = new List<string>();
        }

        // ── Door ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Digest blendshape overlap on the mesh under scene object <paramref name="meshObject"/>.
        /// <paramref name="shapeNames"/> is the candidate co-active set the agent derived from the FX /
        /// ShapeChanger graph. Returns a one-line summary ending with the artifact path in-band
        /// (<c>… => OK | log=&lt;path&gt;</c>); misuse (object/mesh/names bad) is a bare
        /// <c>[ReportShapeOverlap] FAIL: …</c> with no trailer. A shape name absent from the mesh is NOT a
        /// failure — it is reported as <c>MISSING</c> and the resolvable shapes are still analysed.</summary>
        public static string Report(string meshObject, string[] shapeNames = null, string outfitRoot = null)
        {
            var go = Resolve(meshObject);
            if (go == null) return Fail("scene object '" + meshObject + "' not found in the active scene");
            var smr = ResolveMesh(go, out var why); // only returns an SMR whose mesh has blendShapeCount > 0
            if (smr == null) return Fail(why);
            var mesh = smr.sharedMesh;

            var passed = shapeNames ?? Array.Empty<string>();
            // A null/blank passed element would throw in GetBlendShapeIndex; reject it here rather than let a raw
            // exception escape the FAIL envelope. Promoting blank names to FAIL is the honest signal for a
            // malformed graph read (an unset shape row) — a benign MISSING would mask it.
            if (passed.Any(string.IsNullOrWhiteSpace))
                return Fail("shape names must be non-empty — a blank entry means a malformed candidate set");

            GameObject outfitGO = null;
            if (outfitRoot != null)
            {
                outfitGO = Resolve(outfitRoot);
                if (outfitGO == null) return Fail("outfit root '" + outfitRoot + "' not found in the active scene");
            }

            // The set fed to Analyze is the co-active union: caller-passed ∪ scene-worn ∪ MA ShapeChanger
            // reactions that write THIS mesh (weight 0 at edit time, so the caller can't see them).
            var ingested = BuildAnalyzeSet(smr, passed, outfitGO);
            if (ingested.Names.Count == 0)
                return Fail("no shape names — pass the candidate co-active set (the shapes you believe are on together)");

            var analysis = Analyze(mesh, ingested.Names);
            return Emit(go, smr, mesh, analysis, ingested);
        }

        // ── Ingestion: assemble the co-active shape-name union fed to Analyze ────────────────────────────────

        /// <summary>Union of the caller-passed set, the shapes currently at nonzero weight on
        /// <paramref name="smr"/> (read off the SMR, not the Mesh), and — when <paramref name="outfitRoot"/> is
        /// non-null — the MA <c>ShapeChanger</c> rows under it that write <paramref name="smr"/>'s GameObject.
        /// Order-preserving, deduped. Reaction rows also record their <c>ShapeChangeType</c> for Task 2.</summary>
        internal static Ingested BuildAnalyzeSet(SkinnedMeshRenderer smr, IEnumerable<string> passed, GameObject outfitRoot)
        {
            var result = new Ingested();
            var seen = new HashSet<string>();
            void Add(string n) { if (!string.IsNullOrEmpty(n) && seen.Add(n)) result.Names.Add(n); }

            if (passed != null) foreach (var n in passed) Add(n);

            // Worn: shapes at nonzero weight on the RESOLVED SMR (not a re-fetched GetComponent).
            var mesh = smr.sharedMesh;
            if (mesh != null)
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    if (Mathf.Abs(smr.GetBlendShapeWeight(i)) > 0f)
                        Add(mesh.GetBlendShapeName(i));

            if (outfitRoot != null)
                IngestShapeChangers(outfitRoot, smr.gameObject, result, Add);

            return result;
        }

        // Reflectively read MA ModularAvatarShapeChanger rows under `outfitRoot` and ingest the ShapeName of every
        // row whose write-target resolves to `bodyGO` (the resolved body SMR's GameObject). ChangeType is captured
        // (Delete=0, Set=1); a Delete row is ingested like any other declared shape, not dropped. This asmdef has
        // no MA assembly reference, so everything is by-name reflection (VendorReflect.FindType).
        //
        // Failure doctrine (this tool exists to catch the invisible weight-0 ShapeChangers, so a silent truncation
        // is the worst outcome): MA absent ⇒ silent no-op (the honest floor). MA PRESENT but a renamed member —
        // OR a renamed/moved ModularAvatarShapeChanger TYPE itself — ⇒ a loud one-line drift warning + no
        // reactions (the "reflection canary goes silently green" trap — never let an MA update quietly strip the
        // co-active set). A single throwing component/row ⇒ warn and CONTINUE the sweep (per-component + per-row
        // guards), so one stale/deleted ref can't drop the rest — including the very weight-0 ShapeChangers this
        // tool exists to surface.
        private static void IngestShapeChangers(GameObject outfitRoot, GameObject bodyGO, Ingested result, Action<string> add)
        {
            var scType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger");
            if (scType == null)
            {
                // A null type is ambiguous: MA genuinely absent (silent — the legitimate floor) vs MA installed
                // but the type renamed/moved (or its assembly failed to load types). Probe MA presence by the
                // runtime assembly — far more stable than any one type's FullName — so the drifted case is loud,
                // not a silent `=> OK` with every reaction dropped (the worst outcome for this tool).
                if (VendorReflect.ModularAvatarInstalled())
                    Debug.LogWarning("[ReportShapeOverlap] MA is installed but ModularAvatarShapeChanger did not resolve " +
                        "(type renamed/moved, or its assembly failed to load types); reactions NOT ingested. The co-active " +
                        "set may be missing weight-0 ShapeChanger shapes.");
                return;
            }

            // Member handles resolved once. Any null here means MA is installed but its API drifted from ours —
            // surface it loudly rather than return a silently-empty reaction set the agent would trust.
            var shapesProp = scType.GetProperty("Shapes", BindingFlags.Public | BindingFlags.Instance);
            var csType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ChangedShape");
            var objField = csType?.GetField("Object");
            var nameField = csType?.GetField("ShapeName");
            var ctField = csType?.GetField("ChangeType");
            var valField = csType?.GetField("Value");
            var aorType = VendorReflect.FindType("nadena.dev.modular_avatar.core.AvatarObjectReference");
            var getMethod = aorType == null ? null : VendorReflect.ResolveAorGetOverload(aorType); // strict pin: Get(Component)→GameObject
            if (shapesProp == null || objField == null || nameField == null || ctField == null || valField == null || getMethod == null)
            {
                Debug.LogWarning("[ReportShapeOverlap] MA ShapeChanger reflection drift — a member (Shapes / " +
                    "ChangedShape.Object|ShapeName|ChangeType|Value / AvatarObjectReference.Get) did not resolve; reactions " +
                    "NOT ingested. The co-active set may be missing weight-0 ShapeChanger shapes.");
                return;
            }

            // Resolved once per run, not per row: the probe walks every ObjectToggle under the root.
            var toggleDriven = CollectToggleDriven(outfitRoot);
            result.ActiveStateUnknown = toggleDriven == null;
            result.OutfitRootInactive = !outfitRoot.activeSelf;

            foreach (var comp in outfitRoot.GetComponentsInChildren(scType, true))
            {
                if (comp == null) continue;
                try
                {
                    var shapes = shapesProp.GetValue(comp) as System.Collections.IEnumerable;
                    if (shapes == null) continue;
                    foreach (var row in shapes)
                    {
                        try
                        {
                            if (row == null) continue;
                            var objRef = objField.GetValue(row);
                            var shapeName = nameField.GetValue(row) as string;
                            if (objRef == null || string.IsNullOrEmpty(shapeName)) continue;
                            int changeType = Convert.ToInt32(ctField.GetValue(row), CultureInfo.InvariantCulture);
                            float value = Convert.ToSingle(valField.GetValue(row), CultureInfo.InvariantCulture);

                            // AvatarObjectReference.Get can throw (TargetInvocationException) on a stale/deleted ref.
                            var target = getMethod.Invoke(objRef, new object[] { comp }) as GameObject;
                            if (target != bodyGO) continue; // only rows that write the resolved body mesh

                            if (changeType != 0 && changeType != 1)
                                Debug.LogWarning("[ReportShapeOverlap] MA ShapeChangeType drift on shape '" + shapeName +
                                    "': unexpected value " + changeType + " (expected Delete=0 / Set=1); rendered UNKNOWN. " +
                                    "MA may have added a shape-change mode this tool does not model.");

                            add(shapeName);
                            result.ReactionTypes[shapeName] = changeType; // last-wins type (Task 1 handoff)
                            if (!result.Reactions.TryGetValue(shapeName, out var ri))
                            {
                                ri = new ReactionInfo();
                                result.Reactions[shapeName] = ri;
                            }
                            ri.ChangeType = changeType;
                            ri.Value = value;
                            // Walk stops AT the caller's root: above it, "inactive" describes how the caller
                            // staged the scene, not how the avatar is authored. Last declaration wins, matching
                            // ChangeType/Value above — a shape declared by both a live and a parked changer is
                            // owned by whichever the ingest saw last, and the row names which.
                            var parked = FindInactiveAncestor(comp.transform, outfitRoot.transform, toggleDriven);
                            ri.InactiveAncestor = parked == null ? null : PathOf(parked.gameObject);
                            // Canonicalize: MA discards a Delete row's Value (it forces the shape to 100), so two
                            // Deletes with differing stale Value fields must NOT read as a conflict — compare Delete
                            // on type only. Set keeps its value, so Set=100 vs Set=50 still conflicts.
                            ri.Declares.Add((changeType, changeType == 0 ? 0f : value));
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[ReportShapeOverlap] skipped a ShapeChanger row on '" +
                                PathOf(comp.gameObject) + "' (" + e.GetType().Name + "): " + e.Message);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ReportShapeOverlap] skipped a ShapeChanger component on '" +
                        PathOf(comp.gameObject) + "' (" + e.GetType().Name + "): " + e.Message);
                }
            }
        }

        // ── Build-effective active state ─────────────────────────────────────────────────────────────────────
        //
        // MA gates a reaction on its ancestors' active state, and the census must say so or a declaration that
        // cannot fire reads as covering a shape it does not cover — a worn-and-undeclared shape then hides
        // behind a dead row (the double-subtraction this tool exists to surface).
        //
        // The mechanism, read off MA 1.18.1 rather than assumed (canon: `nondestructive.md`):
        //   • ReactiveObjectAnalyzer.LocateReactions.BuildConditions walks the ShapeChanger's ancestors to the
        //     avatar root and adds ONE activeSelf ControlCondition per ancestor. It is a condition, not a drop.
        //     Its affected-object skip does NOT apply here: that skip is guarded on `affectedObject != null`
        //     and FindShapes calls ObjectRule(key, changer, value) with the 3-arg form, so ShapeChangers pass
        //     null and every ancestor gets a condition. (Only FindMeshCutter passes an affected object.)
        //   • ReactiveObjectAnalyzer.AnalyzeConstants then marks a condition constant only when its object is
        //     neither an ObjectToggle target nor m_IsActive-animated, and drops the whole action group when a
        //     constant condition is unsatisfied. So a TOGGLE-DRIVEN inactive ancestor is alive — the standard
        //     shipping shape (outfit parked off in the scene, toggle default-on) — and only a parked, undriven
        //     ancestor kills the group.
        //   • MA never consults the ShapeChanger component's own `enabled`, so neither does this.
        //
        // What this asserts is therefore an EDIT-TIME state, not a build outcome: AnalyzeConstants no-ops
        // without a build context, and whether a clip animates an ancestor is not evaluated here.
        private static readonly string ObjectToggleTypeName = "nadena.dev.modular_avatar.core.ModularAvatarObjectToggle";

        /// <summary>Every GameObject an MA <c>ObjectToggle</c> under <paramref name="scope"/> drives. MA exempts
        /// these from its constant-folding, so an inactive one is a live conditional rather than a dead branch.
        /// Returns null — never an empty set — when the probe cannot be trusted, so the caller withholds the
        /// signal instead of reading "no toggles" off a drifted reflection.</summary>
        internal static HashSet<GameObject> CollectToggleDriven(GameObject scope)
        {
            var otType = VendorReflect.FindType(ObjectToggleTypeName);
            if (otType == null)
            {
                if (!VendorReflect.ModularAvatarInstalled()) return new HashSet<GameObject>(); // MA absent: nothing drives anything
                Debug.LogWarning("[ReportShapeOverlap] MA is installed but ModularAvatarObjectToggle did not resolve; " +
                    "toggle-driven ancestors cannot be identified, so the active-state signal is withheld.");
                return null;
            }
            var objectsProp = otType.GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
            var toType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ToggledObject");
            var objField = toType?.GetField("Object");
            var aorType = VendorReflect.FindType("nadena.dev.modular_avatar.core.AvatarObjectReference");
            var getMethod = aorType == null ? null : VendorReflect.ResolveAorGetOverload(aorType);
            if (objectsProp == null || objField == null || getMethod == null)
            {
                Debug.LogWarning("[ReportShapeOverlap] MA ObjectToggle reflection drift (Objects / ToggledObject.Object / " +
                    "AvatarObjectReference.Get did not resolve); toggle-driven ancestors cannot be identified, so the " +
                    "active-state signal is withheld.");
                return null;
            }

            var driven = new HashSet<GameObject>();
            foreach (var comp in scope.GetComponentsInChildren(otType, true))
            {
                if (comp == null) continue;
                try
                {
                    if (!(objectsProp.GetValue(comp) is System.Collections.IEnumerable rows)) continue;
                    foreach (var row in rows)
                    {
                        try
                        {
                            var objRef = objField.GetValue(row);
                            if (objRef == null) continue;
                            if (getMethod.Invoke(objRef, new object[] { comp }) is GameObject target) driven.Add(target);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning("[ReportShapeOverlap] skipped an ObjectToggle row on '" +
                                PathOf(comp.gameObject) + "' (" + e.GetType().Name + "): " + e.Message);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[ReportShapeOverlap] skipped an ObjectToggle component on '" +
                        PathOf(comp.gameObject) + "' (" + e.GetType().Name + "): " + e.Message);
                }
            }
            return driven;
        }

        /// <summary>Whether a live declaration owns this shape — the single MISMATCH suppression rule, used by
        /// both the count and the row so the two can never disagree. A declaration reachable only from behind a
        /// parked ancestor does not apply at the initial state, so it covers nothing.</summary>
        internal static bool Covers(Ingested ingested, string shape)
            => ingested.Reactions.TryGetValue(shape, out var ri) && ri.InactiveAncestor == null;

        /// <summary>A shape declared, but only from behind a parked ancestor — counted so the reader sees the
        /// population without diffing the table.</summary>
        internal static bool IsDeadDeclaration(Ingested ingested, string shape)
            => ingested.Reactions.TryGetValue(shape, out var ri) && ri.InactiveAncestor != null;

        /// <summary>The nearest parked ancestor between <paramref name="from"/> (inclusive) and
        /// <paramref name="stopAt"/> (exclusive), skipping toggle-driven ones. Null when the chain is clear.
        /// Pure over its inputs, so every branch is assertable without building an MA graph.</summary>
        internal static Transform FindInactiveAncestor(Transform from, Transform stopAt, HashSet<GameObject> toggleDriven)
        {
            if (toggleDriven == null) return null; // signal withheld — see Ingested.ActiveStateUnknown
            for (var cur = from; cur != null && cur != stopAt; cur = cur.parent)
            {
                if (cur.gameObject.activeSelf) continue;
                if (toggleDriven.Contains(cur.gameObject)) continue;
                return cur;
            }
            return null;
        }

        // ── Pure core ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Compute touched-set footprints and pairwise containment for <paramref name="names"/> on
        /// <paramref name="mesh"/>. Missing names are recorded (not resolved into pairs). Deterministic; no
        /// scene or project access.</summary>
        internal static Analysis Analyze(Mesh mesh, IList<string> names)
        {
            int vc = mesh.vertexCount;
            var a = new Analysis { VertexCount = vc };
            var deltas = new Vector3[vc];
            var touched = new List<bool[]>();   // parallel to `resolved`
            var footprint = new List<int>();    // parallel to `resolved`
            var resolved = new List<string>();

            foreach (var nm in names)
            {
                int si = mesh.GetBlendShapeIndex(nm);
                if (si < 0)
                {
                    a.Missing.Add(nm);
                    a.Footprints.Add(new Footprint { Name = nm, Missing = true });
                    continue;
                }
                int fc = mesh.GetBlendShapeFrameCount(si);
                mesh.GetBlendShapeFrameVertices(si, fc - 1, deltas, null, null); // last frame = full deformation

                var mags = new float[vc];
                var nz = new List<float>();
                for (int i = 0; i < vc; i++) { float m = deltas[i].magnitude; mags[i] = m; if (m > 1e-9f) nz.Add(m); }
                nz.Sort();
                float p95 = nz.Count == 0 ? 0f : nz[Mathf.Min(nz.Count - 1, (int)(nz.Count * PercentileScale))];
                float thr = Mathf.Max(AbsFloorMeters, RelFrac * p95);

                var t = new bool[vc]; int fp = 0;
                for (int i = 0; i < vc; i++) if (mags[i] > thr) { t[i] = true; fp++; }

                touched.Add(t); footprint.Add(fp); resolved.Add(nm);
                a.Footprints.Add(new Footprint { Name = nm, Missing = false, Touched = fp, P95 = p95, Threshold = thr });
            }

            for (int x = 0; x < resolved.Count; x++)
                for (int y = x + 1; y < resolved.Count; y++)
                {
                    var tx = touched[x]; var ty = touched[y];
                    int inter = 0;
                    for (int i = 0; i < vc; i++) if (tx[i] && ty[i]) inter++;
                    int minfp = Mathf.Min(footprint[x], footprint[y]);
                    float cont = minfp == 0 ? 0f : (float)inter / minfp; // empty shape ⇒ undefined ⇒ 0 (reported)
                    a.Pairs.Add(new Pair { A = resolved[x], B = resolved[y], Intersect = inter, MinFootprint = minfp, Containment = cont });
                }
            a.Pairs.Sort((p, q) => q.Containment.CompareTo(p.Containment)); // worst (most-contained) first
            return a;
        }

        // ── Output (Report envelope: summary + markdown body + WriteRunLog to Snapshots; no verdict token) ──
        // Beyond the geometric footprint/pairwise digest, Emit renders the RESOLUTION view the de-conflict skill
        // reads: per union shape its reaction, current static weight, resolved-target, and overlap — plus a full
        // weight audit and the worn-but-undeclared MISMATCH flag. It stays a Report: the table states facts and
        // marks worn-but-undeclared rows for the reader; the accept-vs-CaptureDiff decision lives in the skill.
        private static string Emit(GameObject go, SkinnedMeshRenderer smr, Mesh mesh, Analysis a, Ingested ingested)
        {
            int requested = a.Footprints.Count;
            int resolved = requested - a.Missing.Count;
            int pairFlagged = a.Pairs.Count(p => p.Containment >= FlagContainment);

            // reacted = union shapes declared by a reaction; worn = union shapes at nonzero static weight;
            // mismatch = worn-but-undeclared rows (the disposition signal — NOT a subset boundary of worn, since
            // worn also counts declared-and-worn shapes; the two fields answer different questions).
            int reacted = ingested.Reactions.Count;
            int worn = 0, mismatchCount = 0, inactiveDeclared = 0;
            foreach (var name in ingested.Names)
            {
                int wi = mesh.GetBlendShapeIndex(name);
                bool isWorn = wi >= 0 && Mathf.Abs(smr.GetBlendShapeWeight(wi)) > 0f;
                if (isWorn) worn++;
                if (IsDeadDeclaration(ingested, name)) inactiveDeclared++;
                if (isWorn && !Covers(ingested, name)) mismatchCount++;
            }

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[ReportShapeOverlap] {0}: shapes={1}/{2} reacted={3} inactiveDeclared={4} worn={5} mismatch={6} pairs={7} flagged={8} missing={9} => OK{10}{11}",
                mesh.name, resolved, requested, reacted, inactiveDeclared, worn, mismatchCount, a.Pairs.Count, pairFlagged, a.Missing.Count,
                ingested.OutfitRootInactive ? " | note: outfitRoot itself is inactive — not charged to any row" : "",
                ingested.ActiveStateUnknown ? " | note: active-state signal WITHHELD (ObjectToggle probe drifted)" : "");

            var sb = new StringBuilder();
            sb.Append("# ReportShapeOverlap: ").Append(mesh.name).Append('\n');
            sb.Append("object: `").Append(PathOf(go)).Append("`  \n");
            sb.Append("mesh: `").Append(mesh.name).Append("`  verts=").Append(a.VertexCount)
              .Append("  shapes=").Append(mesh.blendShapeCount).Append("\n\n");
            sb.Append(summary.Substring("[ReportShapeOverlap] ".Length)).Append('\n');

            // ── Resolution — one row per union shape ────────────────────────────────────────────────────────
            sb.Append("\n## Resolution — reaction / current weight / resolved-target / overlap\n");
            sb.Append("_resolved-target: Set→its value, Delete→100 (bakes fully-applied), no reaction→0 (declared-or-zero). " +
                "**MISMATCH** marks a row worn (weight≠0) that nothing live declares — the double-subtraction hazard; " +
                "disposition is not `current≠resolved-target`. A row declared only from behind a parked ancestor " +
                "(`[inactive: …]`) counts as undeclared: MA conditions each reaction on its ancestors' active state, so that " +
                "declaration does not apply at the initial state and cannot own the shape. Toggle-driven ancestors are NOT " +
                "parked — an outfit switched off in the scene but driven by an ObjectToggle still declares. This reads " +
                "edit-time state only; whether a build drops the reaction outright is not evaluated here " +
                "(mechanism: `nondestructive.md`)._\n\n");
            sb.Append("| shape | reaction | current | resolved-target | overlap | disposition |\n");
            sb.Append("| --- | --- | --- | --- | --- | --- |\n");
            // Per-shape max containment, precomputed in one pass over the pairs (an O(1) lookup per union
            // shape instead of re-scanning a.Pairs — which is itself O(shapes²) — for every shape).
            var maxOverlap = new Dictionary<string, float>();
            foreach (var p in a.Pairs)
            {
                if (!maxOverlap.TryGetValue(p.A, out var oa) || p.Containment > oa) maxOverlap[p.A] = p.Containment;
                if (!maxOverlap.TryGetValue(p.B, out var ob) || p.Containment > ob) maxOverlap[p.B] = p.Containment;
            }
            foreach (var name in ingested.Names)
            {
                var f = a.Footprints.First(x => x.Name == name);
                bool present = !f.Missing;
                int idx = present ? mesh.GetBlendShapeIndex(name) : -1;
                float weight = idx >= 0 ? smr.GetBlendShapeWeight(idx) : 0f;
                ingested.Reactions.TryGetValue(name, out var ri);
                bool isWorn = present && Mathf.Abs(weight) > 0f;
                // Worn-but-not-live-declared — still the anti-flood invariant, with a declaration behind a parked
                // ancestor counting as no declaration rather than as cover.
                bool mismatch = isWorn && !Covers(ingested, name);

                string reactionCell, resolvedTarget;
                if (ri == null) { reactionCell = "none"; resolvedTarget = "0"; }
                else if (ri.Conflict)
                {
                    reactionCell = "CONFLICT: " + string.Join(" / ",
                        ri.Declares.Select(d => ReactionLabel(d.type, d.value)));
                    resolvedTarget = "conflict";
                }
                else
                {
                    reactionCell = ReactionLabel(ri.ChangeType, ri.Value);
                    resolvedTarget = ResolvedTarget(ri.ChangeType, ri.Value);
                }
                // Name the offender on the row, not just in the count — the ancestor IS the repair handle.
                if (ri != null && ri.InactiveAncestor != null)
                    reactionCell += " `[inactive: " + Cell(ri.InactiveAncestor) + "]`";

                string overlapCell = maxOverlap.TryGetValue(name, out var ov)
                    ? ov.ToString("F2", CultureInfo.InvariantCulture) : "—";

                sb.Append("| `").Append(Cell(name)).Append("` | ").Append(reactionCell).Append(" | ")
                  .Append(present ? Num(weight) : "—").Append(" | ").Append(resolvedTarget).Append(" | ")
                  .Append(overlapCell).Append(" | ").Append(mismatch ? "**MISMATCH**" : "—").Append(" |\n");
            }

            // ── Weight audit — every blendshape on the mesh (cheap; NOT footprinted) ─────────────────────────
            sb.Append("\n## Weight audit — every blendshape on the mesh, with its current static weight\n");
            sb.Append("_the full weight state (the resolution table above is scoped to the co-active union; this is not)._\n\n");
            sb.Append("| shape | weight |\n| --- | --- |\n");
            for (int i = 0; i < mesh.blendShapeCount; i++)
                sb.Append("| `").Append(Cell(mesh.GetBlendShapeName(i))).Append("` | ")
                  .Append(Num(smr.GetBlendShapeWeight(i))).Append(" |\n");

            sb.Append("\n## Footprints — touched vertices per shape\n");
            sb.Append("_thr = max(").Append(Mm(AbsFloorMeters)).Append("mm, ")
              .Append((RelFrac * 100f).ToString("F0", CultureInfo.InvariantCulture)).Append("% of p95); %verts flags an oversized footprint (stray-vertex authoring)._\n\n");
            sb.Append("| shape | p95 (mm) | thr (mm) | touched | %verts |\n");
            sb.Append("| --- | --- | --- | --- | --- |\n");
            foreach (var f in a.Footprints)
            {
                if (f.Missing) { sb.Append("| `").Append(Cell(f.Name)).Append("` | — | — | **MISSING** | — |\n"); continue; }
                float pct = a.VertexCount == 0 ? 0f : 100f * f.Touched / a.VertexCount;
                sb.Append("| `").Append(Cell(f.Name)).Append("` | ").Append(Mm(f.P95)).Append(" | ").Append(Mm(f.Threshold))
                  .Append(" | ").Append(f.Touched).Append(" | ").Append(pct.ToString("F1", CultureInfo.InvariantCulture)).Append("% |\n");
            }

            sb.Append("\n## Pairwise containment — |A∩B| / min(|A|,|B|)\n");
            sb.Append("_≥ ").Append(FlagContainment.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" flagged * (the smaller shape's zone is mostly swallowed by the larger — the double-subtraction condition). A flag is a place to LOOK, not a verdict: resolve wanted-vs-defect from the FX / ShapeChanger graph._\n\n");
            if (a.Pairs.Count == 0) sb.Append("_(fewer than two resolvable shapes — no pairs)_\n");
            else
            {
                sb.Append("| A | B | intersect | min-fp | containment |\n");
                sb.Append("| --- | --- | --- | --- | --- |\n");
                foreach (var p in a.Pairs)
                {
                    string flag = p.Containment >= FlagContainment ? " *" : "";
                    sb.Append("| `").Append(Cell(p.A)).Append("` | `").Append(Cell(p.B)).Append("` | ").Append(p.Intersect)
                      .Append(" | ").Append(p.MinFootprint).Append(" | ")
                      .Append(p.Containment.ToString("F2", CultureInfo.InvariantCulture)).Append(flag).Append(" |\n");
                }
            }

            var res = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "shape-overlap_" + mesh.name, summary, sb.ToString(), ".md");
            Debug.Log(res);
            return res;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

        private static string Mm(float meters) => (meters * 1000f).ToString("F3", CultureInfo.InvariantCulture);

        private static string Cell(string s) => RunLogFormat.Cell(s);

        // MA ShapeChangeType is {Delete=0, Set=1}. Render any other value loudly as UNKNOWN(n) with an
        // `unknown` resolved-target rather than silently defaulting to Set — a new MA mode or a corrupt
        // serialized enum is exactly the "reflection goes silently green on MA drift" trap this tool guards.
        private static string ReactionLabel(int changeType, float value) =>
            changeType == 0 ? "Delete"
            : changeType == 1 ? "Set=" + Num(value)
            : "UNKNOWN(" + changeType.ToString(CultureInfo.InvariantCulture) + ")";
        private static string ResolvedTarget(int changeType, float value) =>
            changeType == 0 ? "100" : changeType == 1 ? Num(value) : "unknown";

        // Compact weight/value formatter: integral weights render clean (100, 0, 42), fractional keep up to 3 dp.
        // A NONZERO weight must never render as "0": worn/mismatch classification is strictly `!= 0`, so a tiny
        // residual (float noise from imperfect zeroing) is counted worn yet "0.###" would print it "0" — a row
        // contradicting its own mismatch= count. Fall back to a round-tripping form so the magnitude stays visible.
        private static string Num(float v)
        {
            if (v == 0f) return "0";
            string s = v.ToString("0.###", CultureInfo.InvariantCulture);
            return s == "0" || s == "-0" ? v.ToString("G4", CultureInfo.InvariantCulture) : s;
        }

        // Resolve the SkinnedMeshRenderer carrying blendshapes from the named scene object (its mesh is .sharedMesh;
        // weight reads come off the SMR itself). Prefer the SMR on the object; else a unique blendshape-bearing SMR
        // among its descendants. Two or more is ambiguous — REFUSE and name them, so the agent points at the specific
        // renderer (speak the substrate: it is a scene object it already holds during de-conflict).
        private static SkinnedMeshRenderer ResolveMesh(GameObject go, out string why)
        {
            why = null;
            var own = go.GetComponent<SkinnedMeshRenderer>();
            if (own != null && own.sharedMesh != null && own.sharedMesh.blendShapeCount > 0) return own;

            var withShapes = go.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s.sharedMesh != null && s.sharedMesh.blendShapeCount > 0)
                .ToList();
            if (withShapes.Count == 1) return withShapes[0];
            if (withShapes.Count == 0) { why = "no SkinnedMeshRenderer with blendshapes on or under '" + go.name + "'"; return null; }
            why = "ambiguous: " + withShapes.Count + " blendshape meshes under '" + go.name + "' (" +
                  string.Join(", ", withShapes.Select(s => s.name)) + ") — point at the specific renderer";
            return null;
        }

        private static string Fail(string why)
        {
            var e = "[ReportShapeOverlap] FAIL: " + why;
            Debug.LogError(e);
            return e;
        }

        // ── Scene resolver (path → instance id → recursive name; mirrors CheckSeam.Resolve) ────────────────

        private static GameObject Resolve(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var byPath = FindByHierarchyPath(target);
            if (byPath != null) return byPath;

            if (int.TryParse(target.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                if (obj is GameObject go) return go;
                if (obj is Component comp) return comp.gameObject;
            }

            foreach (var rootGo in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                var hit = FindByNameRecursive(rootGo.transform, target);
                if (hit != null) return hit.gameObject;
            }
            return null;
        }

        private static GameObject FindByHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segs = path.Trim('/').Split('/');
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
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

        private static Transform FindByNameRecursive(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform child in t)
            {
                var hit = FindByNameRecursive(child, name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static string PathOf(GameObject go)
        {
            if (go == null) return "—";
            var t = go.transform;
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }
    }
}
