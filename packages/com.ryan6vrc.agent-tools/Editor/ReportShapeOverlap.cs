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

        // Minimal ingestion record fed to Analyze. Names is the ORDERED, deduped co-active union
        // ({passed} ∪ {worn} ∪ {reaction-targeted}). ReactionTypes captures each reaction-driven shape's MA
        // ShapeChangeType (Delete=0, Set=1) so Task 2 can render the resolution table — this task only ingests.
        internal class Ingested
        {
            public List<string> Names = new List<string>();
            public Dictionary<string, int> ReactionTypes = new Dictionary<string, int>();
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
            return Emit(go, mesh, analysis);
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
        // no MA assembly reference, so everything is by-name reflection (mirrors CheckSeam.FindType).
        //
        // Failure doctrine (this tool exists to catch the invisible weight-0 ShapeChangers, so a silent truncation
        // is the worst outcome): MA absent ⇒ silent no-op (the honest floor). MA PRESENT but a renamed member ⇒
        // a loud one-line drift warning + no reactions (the "reflection canary goes silently green" trap — never
        // let an MA update quietly strip the co-active set). A single throwing component/row ⇒ warn and CONTINUE
        // the sweep (per-component + per-row guards), so one stale/deleted ref can't drop the rest — including the
        // very weight-0 ShapeChangers this tool exists to surface.
        private static void IngestShapeChangers(GameObject outfitRoot, GameObject bodyGO, Ingested result, Action<string> add)
        {
            var scType = FindType("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger");
            if (scType == null) return; // MA not installed ⇒ no reactions (silent — the legitimate absent path)

            // Member handles resolved once. Any null here means MA is installed but its API drifted from ours —
            // surface it loudly rather than return a silently-empty reaction set the agent would trust.
            var shapesProp = scType.GetProperty("Shapes", BindingFlags.Public | BindingFlags.Instance);
            var csType = FindType("nadena.dev.modular_avatar.core.ChangedShape");
            var objField = csType?.GetField("Object");
            var nameField = csType?.GetField("ShapeName");
            var ctField = csType?.GetField("ChangeType");
            var aorType = FindType("nadena.dev.modular_avatar.core.AvatarObjectReference");
            var getMethod = aorType?.GetMethod("Get", new[] { typeof(Component) }); // Get(Component container)
            if (shapesProp == null || objField == null || nameField == null || ctField == null || getMethod == null)
            {
                Debug.LogWarning("[ReportShapeOverlap] MA ShapeChanger reflection drift — a member (Shapes / " +
                    "ChangedShape.Object|ShapeName|ChangeType / AvatarObjectReference.Get) did not resolve; reactions " +
                    "NOT ingested. The co-active set may be missing weight-0 ShapeChanger shapes.");
                return;
            }

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

                            // AvatarObjectReference.Get can throw (TargetInvocationException) on a stale/deleted ref.
                            var target = getMethod.Invoke(objRef, new object[] { comp }) as GameObject;
                            if (target != bodyGO) continue; // only rows that write the resolved body mesh

                            add(shapeName);
                            result.ReactionTypes[shapeName] = changeType; // captured for the Task-2 resolution table
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

        internal static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == fullName);

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
        private static string Emit(GameObject go, Mesh mesh, Analysis a)
        {
            int requested = a.Footprints.Count;
            int resolved = requested - a.Missing.Count;
            int flagged = a.Pairs.Count(p => p.Containment >= FlagContainment);

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[ReportShapeOverlap] {0}: shapes={1}/{2} pairs={3} flagged={4} missing={5} => OK",
                mesh.name, resolved, requested, a.Pairs.Count, flagged, a.Missing.Count);

            var sb = new StringBuilder();
            sb.Append("# ReportShapeOverlap: ").Append(mesh.name).Append('\n');
            sb.Append("object: `").Append(PathOf(go)).Append("`  \n");
            sb.Append("mesh: `").Append(mesh.name).Append("`  verts=").Append(a.VertexCount)
              .Append("  shapes=").Append(mesh.blendShapeCount).Append("\n\n");
            sb.Append(summary.Substring("[ReportShapeOverlap] ".Length)).Append('\n');

            sb.Append("\n## Footprints — touched vertices per shape\n");
            sb.Append("_thr = max(").Append(Mm(AbsFloorMeters)).Append("mm, ")
              .Append((RelFrac * 100f).ToString("F0", CultureInfo.InvariantCulture)).Append("% of p95); %verts flags an oversized footprint (stray-vertex authoring)._\n\n");
            sb.Append("| shape | p95 (mm) | thr (mm) | touched | %verts |\n");
            sb.Append("| --- | --- | --- | --- | --- |\n");
            foreach (var f in a.Footprints)
            {
                if (f.Missing) { sb.Append("| `").Append(f.Name).Append("` | — | — | **MISSING** | — |\n"); continue; }
                float pct = a.VertexCount == 0 ? 0f : 100f * f.Touched / a.VertexCount;
                sb.Append("| `").Append(f.Name).Append("` | ").Append(Mm(f.P95)).Append(" | ").Append(Mm(f.Threshold))
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
                    sb.Append("| `").Append(p.A).Append("` | `").Append(p.B).Append("` | ").Append(p.Intersect)
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
