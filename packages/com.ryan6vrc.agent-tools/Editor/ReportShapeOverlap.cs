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
    /// deform, locating the double-subtraction of a worn base shape and an outfit <c>ShapeChanger</c>.
    /// Contract: docs/unity-tools.md.
    ///
    /// Method (per <see cref="Analyze"/>): the authored per-vertex deltas of each shape's last frame
    /// (weight-independent) define a <i>touched set</i>, the vertices moved past the larger of
    /// <see cref="AbsFloorMeters"/> and <see cref="RelFrac"/> of the shape's own ~95th-pctile magnitude; the
    /// relative tier discards stray authoring verts on messy assets. Overlap is containment,
    /// <c>|A∩B| / min(|A|,|B|)</c>: whether the smaller shape's territory is swallowed by the larger.
    /// </summary>
    [AgentTool]
    public static class ReportShapeOverlap
    {
        // ── Noise/flag knobs: conservative floors, not tuned to any sampled asset. ──────────────────────────
        internal const float AbsFloorMeters = 0.0001f; // 0.1mm: sub-visual authoring jitter floor
        internal const float RelFrac        = 0.10f;   // or this fraction of the shape's own p95, whichever is larger
        internal const float PercentileScale = 0.95f;  // "shape scale" = 95th-pctile of nonzero |delta|
        internal const float FlagContainment = 0.30f;  // containment ≥ this ⇒ flagged (attention, not verdict)

        // ── Data the pure core returns ──────────────────────────────────────────────────────────────────────
        internal struct Footprint { public string Name; public bool Missing; public int Touched; public float P95; public float Threshold; }
        internal struct Pair { public string A; public string B; public int Intersect; public int MinFootprint; public float Containment; }

        // Per-shape record of the MA ShapeChanger reaction(s) declaring it. ChangeType/Value are the last
        // declared; every declaration is kept in Declares so two rows asking for different (type,value) surface
        // as a Conflict.
        internal class ReactionInfo
        {
            public int ChangeType;          // MA ShapeChangeType of the last declaration (Delete=0, Set=1)
            public float Value;             // Set value of the last declaration (irrelevant for Delete)
            public readonly List<(int type, float value)> Declares = new List<(int type, float value)>();
            public bool Conflict => Declares.Distinct().Count() > 1;
            // Nearest parked ancestor (activeSelf=false, not toggle-driven) above the declaring ShapeChanger, set
            // only when every declaration of this shape is parked: one live declaration owns the shape.
            public string InactiveAncestor;
            internal bool AnyDeclarationLive;
        }

        // Names is the co-active union Analyze receives; Reactions carries what each shape's rows declared, for
        // Emit's resolved-target column.
        internal class Ingested
        {
            public List<string> Names = new List<string>();
            public Dictionary<string, int> ReactionTypes = new Dictionary<string, int>();
            public Dictionary<string, ReactionInfo> Reactions = new Dictionary<string, ReactionInfo>();
            // Scene paths of the other GameObjects that ShapeChanger rows under outfitRoot resolved to. Printed only
            // beside a reacted=0, where "reacts to nothing here" and "reacts, to a sibling mesh" otherwise read identically.
            public SortedSet<string> ElsewhereTargets = new SortedSet<string>(StringComparer.Ordinal);
            // The caller's own root is inactive: a note, never charged to the rows, because a caller who staged
            // the piece off would otherwise read every declaration as parked.
            public bool OutfitRootInactive;
            // The ObjectToggle probe drifted, so toggle-driven ancestors cannot be told from parked ones and the
            // active-state signal is withheld.
            public bool ActiveStateUnknown;
            // The ShapeChanger type or a member drifted, so the sweep's empty result does not mean "nothing reacts".
            public bool ReactionsUnknown;
            // Whether ANY MA MeshCutter sits under outfitRoot: the other anti-clip mechanism, which this tool
            // cannot read and whose presence is what makes reacted=0 ambiguous. Not scoped to this mesh (see
            // CutterPresence).
            public CutterState MeshCutters = CutterState.NotScanned;
        }

        /// <summary>Whether another anti-clip mechanism is present, as far as this tool can tell.
        /// <c>NotScanned</c> (no <c>outfitRoot</c>) and <c>DriftUnknown</c> both mean not answered.</summary>
        internal enum CutterState { NotScanned, Absent, Present, DriftUnknown }
        internal class Analysis
        {
            public int VertexCount;
            public List<Footprint> Footprints = new List<Footprint>();
            public List<Pair> Pairs = new List<Pair>();
            public List<string> Missing = new List<string>();
        }

        // ── Door ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Digest blendshape overlap on the mesh under scene object <paramref name="meshObject"/>.
        /// <paramref name="shapeNames"/> and <paramref name="outfitRoot"/> feed the co-active set
        /// (<see cref="BuildAnalyzeSet"/>).</summary>
        public static string Run(string meshObject, string[] shapeNames = null, string outfitRoot = null)
        {
            var handle = SceneHandle.Resolve(meshObject);
            if (!handle.Ok) return Fail("meshObject: " + handle.Refusal);
            var go = handle.Object;
            var smr = ResolveMesh(go, out var why);
            if (smr == null) return Fail(why);
            var mesh = smr.sharedMesh;

            var passed = shapeNames ?? Array.Empty<string>();
            if (passed.Any(string.IsNullOrWhiteSpace))
                return Fail("shape names must be non-empty — a blank entry means a malformed candidate set");

            GameObject outfitGO = null;
            if (outfitRoot != null)
            {
                var outfitHandle = SceneHandle.Resolve(outfitRoot);
                if (!outfitHandle.Ok) return Fail("outfitRoot: " + outfitHandle.Refusal);
                outfitGO = outfitHandle.Object;
            }

            var ingested = BuildAnalyzeSet(smr, passed, outfitGO);
            var analysis = Analyze(mesh, ingested.Names);
            return Emit(go, smr, mesh, analysis, ingested, outfitGO != null);
        }

        // ── Ingestion: assemble the co-active shape-name union fed to Analyze ────────────────────────────────

        /// <summary>Order-preserving, deduped union of the caller-passed set, the shapes at nonzero weight on
        /// <paramref name="smr"/>, and (with <paramref name="outfitRoot"/>) the MA <c>ShapeChanger</c> rows under
        /// it that write <paramref name="smr"/>'s GameObject.</summary>
        internal static Ingested BuildAnalyzeSet(SkinnedMeshRenderer smr, IEnumerable<string> passed, GameObject outfitRoot)
        {
            var result = new Ingested();
            var seen = new HashSet<string>();
            void Add(string n) { if (!string.IsNullOrEmpty(n) && seen.Add(n)) result.Names.Add(n); }

            if (passed != null) foreach (var n in passed) Add(n);

            var mesh = smr.sharedMesh;
            if (mesh != null)
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    if (Mathf.Abs(smr.GetBlendShapeWeight(i)) > 0f)
                        Add(mesh.GetBlendShapeName(i));

            if (outfitRoot != null)
            {
                IngestShapeChangers(outfitRoot, smr.gameObject, result, Add);
                result.MeshCutters = CutterPresence(outfitRoot);
            }

            return result;
        }

        /// <summary>Whether any MA <c>MeshCutter</c> sits under <paramref name="outfitRoot"/>: presence, never a
        /// count or a read of what one removes.
        ///
        /// <para><c>Absent</c> means no cutter under the root, not "nothing cuts this mesh". A cutter cuts whatever
        /// its <c>Object</c> reference names, and MA supports cutters parented under their target mesh, so a
        /// cutter on the base body cutting the torso under a dress is invisible to this scan. Widening it needs a
        /// second root argument.</para>
        ///
        /// <para>Within the root the probe over-warns: unscoped to the mesh, and counting a filterless (MA-inert)
        /// cutter as present. Do not re-add a target filter: <c>AvatarObjectReference.Get</c> returns null when the
        /// container has no avatar root among its parents, so on a staging scene or an isolated prefab every row
        /// would be skipped and print a counted zero indistinguishable from a real one.</para></summary>
        private static CutterState CutterPresence(GameObject outfitRoot)
        {
            var mcType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarMeshCutter");
            bool found = false;
            if (mcType != null)
                // includeInactive: a cutter on a disabled object is the normal MENU-TOGGLED cut — MA builds a
                // condition from ancestor activeSelf rather than skipping it — so it is live anti-clip, not dead weight.
                foreach (var comp in outfitRoot.GetComponentsInChildren(mcType, true))
                    if (comp != null) { found = true; break; }

            var state = DecideCutterState(mcType, VendorReflect.ModularAvatarInstalled(), found);
            if (state == CutterState.DriftUnknown)
                Debug.LogWarning("[ReportShapeOverlap] MA is installed but ModularAvatarMeshCutter did not resolve " +
                    "(type renamed/moved); meshCutters signal WITHHELD — reacted=0 under this outfitRoot is " +
                    "unqualified (outfits.md anti-clip idioms; unity-tools.md ReportShapeOverlap).");
            return state;
        }

        internal static CutterState DecideCutterState(Type mcType, bool maInstalled, bool anyCutterFound)
        {
            if (mcType == null) return maInstalled ? CutterState.DriftUnknown : CutterState.Absent;
            return anyCutterFound ? CutterState.Present : CutterState.Absent;
        }

        // Reflectively read MA ShapeChanger rows under `outfitRoot` and ingest the ShapeName of every row whose
        // write-target resolves to `bodyGO`; a Delete row is ingested like any other. This asmdef has no MA assembly
        // reference, so everything is by-name reflection. A single throwing component or row warns and the sweep
        // continues, so one stale ref can't drop the rest of the weight-0 ShapeChangers.
        private static void IngestShapeChangers(GameObject outfitRoot, GameObject bodyGO, Ingested result, Action<string> add)
        {
            var scType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger");
            if (scType == null)
            {
                // A null type means MA absent (silent), or MA installed with the type renamed or moved. The runtime
                // assembly's name tells them apart.
                if (VendorReflect.ModularAvatarInstalled())
                {
                    result.ReactionsUnknown = true;
                    Debug.LogWarning("[ReportShapeOverlap] MA is installed but ModularAvatarShapeChanger did not resolve " +
                        "(type renamed/moved, or its assembly failed to load types); reactions signal WITHHELD. The co-active " +
                        "set may be missing weight-0 ShapeChanger shapes.");
                }
                return;
            }

            var shapesProp = scType.GetProperty("Shapes", BindingFlags.Public | BindingFlags.Instance);
            var csType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ChangedShape");
            var objField = csType?.GetField("Object");
            var nameField = csType?.GetField("ShapeName");
            var ctField = csType?.GetField("ChangeType");
            var valField = csType?.GetField("Value");
            var aorType = VendorReflect.FindType("nadena.dev.modular_avatar.core.AvatarObjectReference");
            var getMethod = aorType == null ? null : VendorReflect.ResolveAorGetOverload(aorType);
            if (shapesProp == null || objField == null || nameField == null || ctField == null || valField == null || getMethod == null)
            {
                result.ReactionsUnknown = true;
                Debug.LogWarning("[ReportShapeOverlap] MA ShapeChanger reflection drift — a member (Shapes / " +
                    "ChangedShape.Object|ShapeName|ChangeType|Value / AvatarObjectReference.Get) did not resolve; reactions " +
                    "signal WITHHELD. The co-active set may be missing weight-0 ShapeChanger shapes.");
                return;
            }

            var toggleDriven = CollectToggleDriven(AvatarRootOf(outfitRoot) ?? outfitRoot);
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
                            if (target != bodyGO)
                            {
                                if (target != null) result.ElsewhereTargets.Add(PathOf(target));
                                continue;
                            }

                            if (changeType != 0 && changeType != 1)
                                Debug.LogWarning("[ReportShapeOverlap] MA ShapeChangeType drift on shape '" + shapeName +
                                    "': unexpected value " + changeType + " (expected Delete=0 / Set=1); rendered UNKNOWN. " +
                                    "MA may have added a shape-change mode this tool does not model.");

                            add(shapeName);
                            result.ReactionTypes[shapeName] = changeType; // last-wins type
                            if (!result.Reactions.TryGetValue(shapeName, out var ri))
                            {
                                ri = new ReactionInfo();
                                result.Reactions[shapeName] = ri;
                            }
                            ri.ChangeType = changeType;
                            ri.Value = value;
                            // The walk stops at the caller's root: above it, "inactive" describes how the caller
                            // staged the scene, not how the avatar is authored.
                            // MA's initial state is the last initially-active group, not the last declared
                            // (ReactiveObjectAnalyzer PreprocessShapes), so liveness is a disjunction over
                            // declarations. An inverted changer behind a parked ancestor is how "apply while the
                            // piece is off" is authored, and is initially active (ReactionRule.InitiallyActive
                            // XORs Inverted).
                            var parked = FindInactiveAncestor(comp.transform, outfitRoot.transform, toggleDriven);
                            bool inverted = ReadInverted(comp);
                            bool live = (parked == null) ^ inverted;
                            if (live) { ri.AnyDeclarationLive = true; ri.InactiveAncestor = null; }
                            else if (!ri.AnyDeclarationLive && parked != null) ri.InactiveAncestor = PathOf(parked.gameObject);
                            // MA ignores a Delete row's Value (the shape is a vertex selector, not a weight it
                            // writes), so two Deletes with differing stale Value fields must not read as a conflict.
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
        // MA gates a reaction on its ancestors' active state:
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

        /// <summary>The avatar root at or above <paramref name="go"/>, or null. MA scans toggles from the avatar
        /// root, and the ordinary wardrobe layout parks the `ObjectToggle` on a menu object ABOVE the outfit, so
        /// scoping the scan to the caller's root would miss it and annotate a driven ancestor as parked.</summary>
        internal static GameObject AvatarRootOf(GameObject go)
        {
            var descType = VendorReflect.FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descType == null || go == null) return null;
            for (var t = go.transform; t != null; t = t.parent)
                if (t.GetComponent(descType) != null) return t.gameObject;
            return null;
        }

        /// <summary>An MA reactive component's <c>Inverted</c> flag; false when unreadable.</summary>
        internal static bool ReadInverted(UnityEngine.Object comp)
        {
            if (comp == null) return false;
            try
            {
                var pi = comp.GetType().GetProperty("Inverted", BindingFlags.Public | BindingFlags.Instance);
                if (pi != null) return pi.GetValue(comp) is bool pb && pb;
                var fi = comp.GetType().GetField("Inverted", BindingFlags.Public | BindingFlags.Instance);
                return fi != null && fi.GetValue(comp) is bool fb && fb;
            }
            catch { return false; }
        }

        /// <summary>Every GameObject an MA <c>ObjectToggle</c> under <paramref name="scope"/> drives ACTIVE. MA
        /// exempts a toggle target from constant-folding, so an inactive one is a live conditional rather than a
        /// dead branch. A row driving the object off is excluded because it resolves to inactive. Returns null,
        /// never an empty set, when the probe cannot be trusted, so the caller withholds the signal.</summary>
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
            var activeField = toType?.GetField("Active");
            var aorType = VendorReflect.FindType("nadena.dev.modular_avatar.core.AvatarObjectReference");
            var getMethod = aorType == null ? null : VendorReflect.ResolveAorGetOverload(aorType);
            if (objectsProp == null || objField == null || activeField == null || getMethod == null)
            {
                Debug.LogWarning("[ReportShapeOverlap] MA ObjectToggle reflection drift (Objects / ToggledObject.Object|Active / " +
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
                            if (!(activeField.GetValue(row) is bool drivesActive) || !drivesActive) continue;
                            if (getMethod.Invoke(objRef, new object[] { comp }) is GameObject target) driven.Add(target);
                        }
                        catch (Exception e)
                        {
                            // A dropped row shrinks the driven set, which would annotate a genuinely-driven
                            // ancestor as parked. The set is no longer trustworthy, so withhold it entirely.
                            Debug.LogWarning("[ReportShapeOverlap] skipped an ObjectToggle row on '" +
                                PathOf(comp.gameObject) + "' (" + e.GetType().Name + "): " + e.Message +
                                " — active-state annotation withheld for this run.");
                            return null;
                        }
                    }
                }
                catch (Exception e)
                {
                    // A dropped component shrinks the driven set the same way a dropped row does.
                    Debug.LogWarning("[ReportShapeOverlap] skipped an ObjectToggle component on '" +
                        PathOf(comp.gameObject) + "' (" + e.GetType().Name + "): " + e.Message +
                        " — active-state annotation withheld for this run.");
                    return null;
                }
            }
            return driven;
        }

        /// <summary>Whether any reaction declares this shape: the MISMATCH suppression rule. A parked ancestor does
        /// not release it, because `AnalyzeConstants` keeps a condition alive when any clip binds that object's
        /// `m_IsActive`, evaluated against a build-time animation index that no edit-time read has (`asc == null`
        /// outside a build).</summary>
        internal static bool Covers(Ingested ingested, string shape)
            => ingested.Reactions.ContainsKey(shape);

        internal static bool IsParkedDeclaration(Ingested ingested, string shape)
            => ingested.Reactions.TryGetValue(shape, out var ri) && ri.InactiveAncestor != null;

        /// <summary>The nearest parked ancestor between <paramref name="from"/> (inclusive) and
        /// <paramref name="stopAt"/> (exclusive), skipping toggle-driven ones.</summary>
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
                    float cont = minfp == 0 ? 0f : (float)inter / minfp;
                    a.Pairs.Add(new Pair { A = resolved[x], B = resolved[y], Intersect = inter, MinFootprint = minfp, Containment = cont });
                }
            a.Pairs.Sort((p, q) => q.Containment.CompareTo(p.Containment));
            return a;
        }

        /// <summary>The cutter-presence token, printed beside <c>reacted=</c> to qualify it: <c>present</c> never
        /// says the mesh is correctly anti-clipped, and <c>absent</c> is scoped to the outfit root
        /// (<see cref="CutterPresence"/>).</summary>
        internal static string MeshCutterPart(CutterState state)
            => state == CutterState.Present ? " meshCutters=present"
             : state == CutterState.Absent ? " meshCutters=absent"
             : ""; // NotScanned and DriftUnknown print no token — the latter rides the WITHHELD note

        internal static string WithheldNotes(Ingested ingested)
            => (ingested.ActiveStateUnknown ? " | note: active-state signal WITHHELD (ObjectToggle probe drifted)" : "")
             + (ingested.ReactionsUnknown ? " | note: reactions signal WITHHELD (MA ShapeChanger drift)" : "")
             + (ingested.MeshCutters == CutterState.DriftUnknown
                    ? " | note: meshCutters signal WITHHELD (MA MeshCutter type drift)" : "");

        // ── Output ──────────────────────────────────────────────────────────────────────────────────────────
        private static string Emit(GameObject go, SkinnedMeshRenderer smr, Mesh mesh, Analysis a, Ingested ingested,
                                   bool outfitRootPassed)
        {
            int requested = a.Footprints.Count;
            int resolved = requested - a.Missing.Count;
            int pairFlagged = a.Pairs.Count(p => p.Containment >= FlagContainment);

            int reacted = ingested.Reactions.Count;
            int worn = 0, mismatchCount = 0, inactiveDeclared = 0;
            foreach (var name in ingested.Names)
            {
                int wi = mesh.GetBlendShapeIndex(name);
                bool isWorn = wi >= 0 && Mathf.Abs(smr.GetBlendShapeWeight(wi)) > 0f;
                if (isWorn) worn++;
                if (IsParkedDeclaration(ingested, name)) inactiveDeclared++;
                if (isWorn && !Covers(ingested, name)) mismatchCount++;
            }

            // `reacted=0` must say whether the scan ran, so this is gated on the scan and not on an empty result.
            string emptyNote = outfitRootPassed ? "" : " | note: reactions NOT scanned — no outfitRoot passed";
            if (reacted == 0 && ingested.ElsewhereTargets.Count > 0)
                emptyNote += " | note: no ShapeChanger row targets this mesh; rows under outfitRoot target " +
                             string.Join(", ", ingested.ElsewhereTargets);
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[ReportShapeOverlap] {0}: shapes={1}/{2} reacted={3}{11} inactiveDeclared={4} worn={5} mismatch={6} pairs={7} flagged={8} missing={9} => OK{10}{12}{13}",
                mesh.name, resolved, requested, reacted, inactiveDeclared, worn, mismatchCount, a.Pairs.Count, pairFlagged, a.Missing.Count,
                ingested.OutfitRootInactive ? " | note: outfitRoot itself is inactive — not charged to any row" : "",
                MeshCutterPart(ingested.MeshCutters), WithheldNotes(ingested), emptyNote);

            var sb = new StringBuilder();
            sb.Append("# ReportShapeOverlap: ").Append(mesh.name).Append('\n');
            sb.Append("object: `").Append(PathOf(go)).Append("`  \n");
            sb.Append("mesh: `").Append(mesh.name).Append("`  verts=").Append(a.VertexCount)
              .Append("  shapes=").Append(mesh.blendShapeCount).Append("\n\n");
            sb.Append(summary.Substring("[ReportShapeOverlap] ".Length)).Append('\n');

            // ── Resolution — one row per union shape ────────────────────────────────────────────────────────
            sb.Append("\n## Resolution — reaction / current weight / resolved-target / overlap\n");
            sb.Append("_resolved-target: Set→its value, Delete→deleted (no weight is written; MA removes the shape's selected " +
                "vertices at build or NaNimates them, by the rule `outfits.md` states, and `ReportComposition` bake:true's " +
                "triangle diff shows which), " +
                "no reaction→0 (declared-or-zero). " +
                "**MISMATCH** marks a row worn (weight≠0) that NOTHING declares — the double-subtraction hazard; " +
                "disposition is not `current≠resolved-target`, and a declared row is not flagged however far its weight sits " +
                "from its target. **`[inactive: <path>]` is an annotation, not a flag**: every declaration of that shape sits " +
                "behind a parked ancestor, so it may not apply at the initial state — read the named ancestor and rule. It is " +
                "not promoted to MISMATCH because proving a declaration dead needs MA's build-time animation index (any clip " +
                "binding the ancestor's `m_IsActive` keeps it live), which no edit-time read has; flagging without it would " +
                "invent offenders on every FX-toggled wardrobe. Toggle-driven and inverted declarations are already excluded " +
                "here (mechanism: `ReportShapeOverlap.cs` §Build-effective active state)._\n\n");
            sb.Append("| shape | reaction | current | resolved-target | overlap | disposition |\n");
            sb.Append("| --- | --- | --- | --- | --- | --- |\n");
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

        // MA ShapeChangeType is {Delete=0, Set=1}.
        private static string ReactionLabel(int changeType, float value) =>
            changeType == 0 ? "Delete"
            : changeType == 1 ? "Set=" + Num(value)
            : "UNKNOWN(" + changeType.ToString(CultureInfo.InvariantCulture) + ")";
        private static string ResolvedTarget(int changeType, float value) =>
            changeType == 0 ? "deleted" : changeType == 1 ? Num(value) : "unknown";

        // A nonzero weight must never render as "0": worn/mismatch classification is strictly `!= 0`, so a tiny
        // residual is counted worn, and "0.###" would print it "0", a row contradicting its own mismatch= count.
        private static string Num(float v)
        {
            if (v == 0f) return "0";
            string s = v.ToString("0.###", CultureInfo.InvariantCulture);
            return s == "0" || s == "-0" ? v.ToString("G4", CultureInfo.InvariantCulture) : s;
        }

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
