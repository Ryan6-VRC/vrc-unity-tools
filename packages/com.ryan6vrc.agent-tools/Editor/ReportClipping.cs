using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>How garments clip bodies at the current pose, edit or play: the length of the line where their surfaces
    /// cross, and the garment vertices behind each body's nearest face, split by body, body region and garment group.
    /// Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class ReportClipping
    {
        const string Tag = "[ReportClipping]";
        const float Window = 0.05f, Behind = 0.002f;   // world metres: grid cell and nearest-face search radius, and the depth that counts
        internal const string Unmapped = "(unmapped)", Other = "(other)";
        const int TableCap = 24, PivotCap = 30;

        /// <summary>One entry of <c>bodies</c> or <c>garments</c>: a renderer, or the triangles of the submeshes whose
        /// material the entry names after <c>#</c>. <c>label</c> is the entry as given, the handle every output prints.</summary>
        internal class Surface { public SkinnedMeshRenderer r; public string label; public int[] tris; }

        /// <summary>One body × region × group cell. <c>signed</c> includes <c>behind</c>; the gap is summed over the
        /// <c>gapN</c> signed vertices not behind, so its mean is the bucket's standoff.</summary>
        public class Bucket { public int behind, signed, edge, gapN; public float maxDepthCm, gapSumCm, crossCm, throughCm; }

        public struct Result
        {
            public int behind, signed, edgeNearest, sampled; public float maxDepthCm, crossCm, throughCm;
            public Dictionary<(string body, string region, string group), Bucket> buckets;
            public int regionBones; public string groupSource;
        }

        public static string Run(string avatarRoot, string[] bodies, string[] garments, string[] groups = null)
        {
            var h = SceneHandle.Resolve(avatarRoot); if (!h.Ok) return Fail(h.Refusal);
            var rt = h.Object.transform;
            var err = Resolve(rt, bodies, garments, out var body, out var gar); if (err != null) return Fail(err);
            if ((err = ResolveGroups(rt, groups, out var gt)) != null) return Fail(err);
            var r = Measure(rt, body, gar, gt);
            string summary = Tag + " " + DrivePhysBones.FullPath(rt) + " bodies=" + string.Join(",", body.Select(b => b.label)) + " garments=" + string.Join(",", gar.Select(g => g.label))
                + " => OK | " + Format(r) + " | " + Maps(r, rt);
            var line = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "report-clipping_" + RunLogFormat.Sanitize(rt.name), summary, summary + "\n\n" + Legend + "\n\n" + Table(r.buckets, int.MaxValue) + "\n", ".md");
            Debug.Log(line);
            return line + "\n" + Legend + "\n" + Table(r.buckets, TableCap);
        }

        internal static string Format(Result r) => "crossingCm=" + N(r.crossCm) + " vertsBehindBody=" + r.behind + "/" + r.signed + " edgeNearest=" + r.edgeNearest
            + " sampled=" + r.sampled + " maxDepthCm=" + N(r.maxDepthCm) + " throughCm=" + N(r.throughCm);

        internal static string Maps(Result r, Transform rt) => "groups=" + r.groupSource + " regionBones=" + r.regionBones
            + (r.regionBones == 0 ? " (the Animator on '" + rt.name + "' carries no humanoid Avatar: every region reads " + Unmapped + ")" : "");

        internal const string Legend = "Each body is measured apart and the totals sum them. crossingCm: length of the line where the garment's triangles cross the body's, the"
            + " measure of one surface showing through another; it reads zero for a garment lying wholly inside the body. behind/signed: every other garment vertex against"
            + " its nearest body face within 5 cm, signed only where that point is inside a face (edgeNearest otherwise), behind past 2 mm; it reads a deep pass-through"
            + " and misses a shallow crossing. maxDepthCm: deepest garment vertex behind the body, sampled or on a crossing. throughCm: deepest body vertex on a crossing"
            + " out past the garment's face, by the garment's winding. region: the humanoid bone skinning the body face, matched by bone name up its ancestors;"
            + " '>X' names the non-humanoid branch below it. group: the garment vertex's innermost group. meanGapCm: mean distance off the face of the signed vertices"
            + " not behind it; a large gap beside zero behind is a chain held off the body.";

        // ── Handles ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Bodies and garments. A renderer that is both keeps, as a body, only the triangles no garment entry
        /// names, so an optimizer's merge of a ribbon into the body renderer is measured against the rest of it.</summary>
        internal static string Resolve(Transform rt, string[] bodies, string[] garments, out Surface[] body, out Surface[] gar)
        {
            gar = null;
            var err = ResolveSurfaces(rt, bodies, "bodies", out body) ?? ResolveSurfaces(rt, garments, "garments", out gar);
            if (err != null) return err;
            foreach (var b in body)
            {
                var own = gar.Where(g => g.r == b.r).ToArray(); if (own.Length == 0) continue;
                var taken = new HashSet<(int, int, int)>();
                foreach (var g in own) for (int i = 0; i < g.tris.Length; i += 3) taken.Add((g.tris[i], g.tris[i + 1], g.tris[i + 2]));
                var keep = new List<int>();
                for (int i = 0; i < b.tris.Length; i += 3) if (!taken.Contains((b.tris[i], b.tris[i + 1], b.tris[i + 2]))) keep.AddRange(new[] { b.tris[i], b.tris[i + 1], b.tris[i + 2] });
                if (keep.Count == 0) return "bodies: '" + b.label + "' is also a garment and keeps no triangle of its own; narrow one of them to a material with '#'";
                b.tris = keep.ToArray();
            }
            return null;
        }

        /// <summary>Each entry is a root-relative path or a SkinnedMeshRenderer name unique under the root, optionally
        /// followed by <c>#</c> and a material name: an exact name first, else the materials whose names start with it.</summary>
        internal static string ResolveSurfaces(Transform rt, string[] names, string what, out Surface[] s)
        {
            s = null; if (names == null || names.Length == 0) return what + ": pass at least one renderer";
            var smrs = rt.GetComponentsInChildren<SkinnedMeshRenderer>(true); var list = new List<Surface>();
            foreach (var raw in names)
            {
                if (string.IsNullOrEmpty(raw)) return what + ": an entry is empty";
                int hash = raw.IndexOf('#'); string n = hash < 0 ? raw : raw.Substring(0, hash), mat = hash < 0 ? null : raw.Substring(hash + 1);
                var at = n.Contains("/") ? WriteDynamics.Find(rt, n, out _) : null;
                var hits = smrs.Where(x => at != null ? x.transform == at : x.name == n).ToArray();
                if (hits.Length != 1) return what + ": '" + n + "' names " + hits.Length + " SkinnedMeshRenderers under '" + rt.name + "'"
                    + (hits.Length > 1 ? " (" + string.Join(", ", hits.Select(x => AnimationUtility.CalculateTransformPath(x.transform, rt))) + "); pass a path" : "; present: " + string.Join(", ", smrs.Select(x => x.name)));
                var m = hits[0].sharedMesh; if (m == null) return what + ": '" + n + "' has no mesh";
                var mats = hits[0].sharedMaterials; string Mat(int i) => i < mats.Length && mats[i] != null ? mats[i].name : "";
                var keep = Enumerable.Range(0, m.subMeshCount).ToList();
                if (mat != null)
                {
                    keep = keep.Where(i => Mat(i) == mat).ToList();
                    if (keep.Count == 0) keep = Enumerable.Range(0, m.subMeshCount).Where(i => Mat(i).StartsWith(mat, StringComparison.Ordinal)).ToList();
                    if (keep.Count == 0) return what + ": no material on '" + n + "' is or starts '" + mat + "'; its materials: " + string.Join(", ", Enumerable.Range(0, m.subMeshCount).Select(Mat));
                }
                list.Add(new Surface { r = hits[0], label = raw, tris = keep.SelectMany(i => m.GetTriangles(i)).ToArray() });
            }
            s = list.ToArray(); return null;
        }

        /// <summary>Garment groups (see <see cref="ResolveTransforms"/>). Null or empty leaves <paramref name="gt"/>
        /// null, which groups by physbone chain.</summary>
        internal static string ResolveGroups(Transform rt, string[] groups, out Transform[] gt) => ResolveTransforms(rt, groups, "groups", out gt);

        /// <summary>Transforms named by root-relative path, or by a name unique under the root (a play-built
        /// <c>name$suffix</c> matches its bare name). Null or empty leaves <paramref name="ts"/> null.</summary>
        internal static string ResolveTransforms(Transform rt, string[] names, string what, out Transform[] ts)
        {
            ts = null; if (names == null || names.Length == 0) return null;
            var all = rt.GetComponentsInChildren<Transform>(true); var list = new List<Transform>();
            foreach (var g in names)
            {
                if (g != null && g.Contains("/")) { var t = WriteDynamics.Find(rt, g, out var err); if (t == null) return what + ": " + err; list.Add(t); continue; }
                var hits = all.Where(t => t != rt && (t.name == g || t.name.Split('$')[0] == g)).ToArray();
                if (hits.Length != 1) return what + ": '" + g + "' names " + hits.Length + " transforms under '" + rt.name + "'"
                    + (hits.Length > 1 ? " (" + string.Join(", ", hits.Select(t => AnimationUtility.CalculateTransformPath(t, rt))) + "); pass a path" : "");
                list.Add(hits[0]);
            }
            ts = list.ToArray(); return null;
        }

        // ── Measure ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Bakes every renderer once, then per body: every other garment vertex against its nearest body face
        /// within the window, signed only inside a face; the length of each garment-body triangle crossing; and the
        /// depth on either side at the crossing triangles' vertices. A nearest point on an edge has no single face to
        /// sign it by, so an open edge (a hem, a material-narrowed stocking's top) never reads as depth. Body triangles
        /// are gridded only across the garments' own world-height band.</summary>
        internal static Result Measure(Transform rt, Surface[] bodies, Surface[] garments, Transform[] groups)
        {
            string groupSource = "given:" + groups?.Length;
            if (groups == null)
            {
                // A chain held disabled (a control drive's) still groups the garment it moves.
                groups = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.gameObject.activeInHierarchy).Select(WriteDynamics.EffRoot).Distinct().ToArray();
                groupSource = "chains:" + groups.Length;
            }
            var label = GroupLabels(rt, groups); var human = HumanNames(rt);
            var r = new Result { buckets = new Dictionary<(string, string, string), Bucket>(), regionBones = human.Count, groupSource = groupSource };
            var baked = new Dictionary<SkinnedMeshRenderer, Vector3[]>(); var m = new Mesh();
            try
            {
                Vector3[] Bake(SkinnedMeshRenderer s)
                {
                    if (baked.TryGetValue(s, out var p)) return p;
                    s.BakeMesh(m, true); var w = s.transform.localToWorldMatrix; p = m.vertices;
                    for (int i = 0; i < p.Length; i++) p[i] = w.MultiplyPoint3x4(p[i]);
                    return baked[s] = p;
                }
                // Garments as one soup: each renderer's vertices once, each entry's triangles offset into them.
                var gvL = new List<Vector3>(); var gg = new List<string>(); var gtL = new List<int>(); var off = new Dictionary<SkinnedMeshRenderer, int>();
                foreach (var g in garments)
                {
                    if (!off.TryGetValue(g.r, out int o))
                    {
                        off[g.r] = o = gvL.Count; var p = Bake(g.r); var bw = g.r.sharedMesh.boneWeights; var byBone = g.r.bones.Select(b => GroupOf(b, rt, label)).ToArray();
                        for (int i = 0; i < p.Length; i++) { gvL.Add(p[i]); gg.Add(i < bw.Length && bw[i].boneIndex0 < byBone.Length ? byBone[bw[i].boneIndex0] : Other); }
                    }
                    foreach (var t in g.tris) gtL.Add(o + t);
                }
                var gv = gvL.ToArray(); var gt = gtL.ToArray();
                var used = new SortedSet<int>(gt).ToArray(); var sample = used.Where((v, k) => k % 2 == 0).ToArray();
                r.sampled = sample.Length;
                if (used.Length == 0) return r;
                float lo = used.Min(v => gv[v].y) - Window, hi = used.Max(v => gv[v].y) + Window;
                var gS = new Soup(gv, gt, float.NegativeInfinity, float.PositiveInfinity);
                string GroupNear(int t, Vector3 q) => gg[NearestCorner(gv, gt, t, q)];
                var hits = new List<Vector3>(6);

                foreach (var s in bodies)
                {
                    var bv = Bake(s.r); var bt = s.tris;
                    var bbw = s.r.sharedMesh.boneWeights; var region = s.r.bones.Select(b => Region(b, human)).ToArray();
                    string Reg(int v) => v < bbw.Length && bbw[v].boneIndex0 < region.Length ? region[bbw[v].boneIndex0] : Unmapped;
                    string RegionAt(int t, Vector3 q) => Reg(NearestCorner(bv, bt, t, q));
                    Bucket B(string reg, string grp) { var key = (s.label, reg, grp); if (!r.buckets.TryGetValue(key, out var k)) r.buckets[key] = k = new Bucket(); return k; }
                    var bS = new Soup(bv, bt, lo, hi); var bGrid = bS.grid; var gGrid = gS.grid;

                    foreach (int i in sample)
                    {
                        if (!Nearest(gv[i], bS, out int t, out var q, out bool face)) continue;
                        var k = B(RegionAt(t, q), gg[i]);
                        if (!face) { r.edgeNearest++; k.edge++; continue; }
                        r.signed++; k.signed++;
                        float sd = Vector3.Dot(gv[i] - q, FaceNormal(bv, bt, t));
                        if (sd < -Behind) { r.behind++; k.behind++; k.maxDepthCm = Mathf.Max(k.maxDepthCm, -sd * 100f); r.maxDepthCm = Mathf.Max(r.maxDepthCm, -sd * 100f); }
                        else { k.gapSumCm += Mathf.Max(0f, sd) * 100f; k.gapN++; }   // within the 2 mm tolerance sits on the face, not off it
                    }

                    var xG = new HashSet<int>(); var xB = new HashSet<int>(); var seen = new HashSet<long>();
                    foreach (var cell in bGrid)
                    {
                        if (!gGrid.TryGetValue(cell.Key, out var gl)) continue;
                        foreach (int bi in cell.Value) foreach (int gi in gl)
                        {
                            if (!bS.Overlaps(bi, gS, gi) || !seen.Add(((long)bi << 32) | (uint)gi)) continue;
                            float len = CrossLength(bv[bt[bi]], bv[bt[bi + 1]], bv[bt[bi + 2]], gv[gt[gi]], gv[gt[gi + 1]], gv[gt[gi + 2]], hits, out var mid);
                            if (len <= 0f) continue;
                            B(RegionAt(bi, mid), GroupNear(gi, mid)).crossCm += len * 100f; r.crossCm += len * 100f;
                            for (int e = 0; e < 3; e++) { xG.Add(gt[gi + e]); xB.Add(bt[bi + e]); }
                        }
                    }
                    foreach (int v in xG)
                    {
                        if (!Nearest(gv[v], bS, out int t, out var q, out bool face) || !face) continue;
                        float sd = Vector3.Dot(gv[v] - q, FaceNormal(bv, bt, t));
                        if (sd < -Behind) { var k = B(RegionAt(t, q), gg[v]); k.maxDepthCm = Mathf.Max(k.maxDepthCm, -sd * 100f); r.maxDepthCm = Mathf.Max(r.maxDepthCm, -sd * 100f); }
                    }
                    foreach (int v in xB)
                    {
                        if (!Nearest(bv[v], gS, out int t, out var q, out bool face) || !face) continue;
                        float sd = Vector3.Dot(bv[v] - q, FaceNormal(gv, gt, t));
                        if (sd > Behind) { var k = B(Reg(v), GroupNear(t, q)); k.throughCm = Mathf.Max(k.throughCm, sd * 100f); r.throughCm = Mathf.Max(r.throughCm, sd * 100f); }
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(m); }
            return r;
        }

        /// <summary>A row sampled over many frames: each count, length and depth the peak across them, the standoff the
        /// latest frame's. Pure.</summary>
        internal static Result Peak(Result acc, Result cur)
        {
            if (acc.buckets == null) acc = new Result { buckets = new Dictionary<(string, string, string), Bucket>(), regionBones = cur.regionBones, groupSource = cur.groupSource };
            acc.behind = Math.Max(acc.behind, cur.behind); acc.signed = Math.Max(acc.signed, cur.signed); acc.edgeNearest = Math.Max(acc.edgeNearest, cur.edgeNearest);
            acc.sampled = Math.Max(acc.sampled, cur.sampled); acc.maxDepthCm = Mathf.Max(acc.maxDepthCm, cur.maxDepthCm);
            acc.crossCm = Mathf.Max(acc.crossCm, cur.crossCm); acc.throughCm = Mathf.Max(acc.throughCm, cur.throughCm);
            foreach (var kv in cur.buckets)
            {
                if (!acc.buckets.TryGetValue(kv.Key, out var a)) acc.buckets[kv.Key] = a = new Bucket();
                var c = kv.Value;
                a.behind = Math.Max(a.behind, c.behind); a.signed = Math.Max(a.signed, c.signed); a.edge = Math.Max(a.edge, c.edge);
                a.maxDepthCm = Mathf.Max(a.maxDepthCm, c.maxDepthCm); a.crossCm = Mathf.Max(a.crossCm, c.crossCm); a.throughCm = Mathf.Max(a.throughCm, c.throughCm);
                a.gapSumCm = c.gapSumCm; a.gapN = c.gapN;
            }
            return acc;
        }

        static Vector3Int Cell(Vector3 p) => Vector3Int.FloorToInt(p / Window);

        /// <summary>One frame's triangles: each one's bounds and bounding sphere, and their offsets into <c>T</c> by every
        /// grid cell their bounds touch, over the triangles reaching into the world-height band [lo, hi].</summary>
        sealed class Soup
        {
            public readonly Vector3[] P, mn, mx, c; public readonly int[] T; public readonly float[] rad;
            public readonly Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();
            public Soup(Vector3[] P, int[] T, float lo, float hi)
            {
                this.P = P; this.T = T; int n = T.Length / 3; mn = new Vector3[n]; mx = new Vector3[n]; c = new Vector3[n]; rad = new float[n];
                for (int t = 0, k = 0; t < T.Length; t += 3, k++)
                {
                    Vector3 a = P[T[t]], b = P[T[t + 1]], d = P[T[t + 2]];
                    mn[k] = Vector3.Min(a, Vector3.Min(b, d)); mx[k] = Vector3.Max(a, Vector3.Max(b, d)); c[k] = (a + b + d) / 3f;
                    rad[k] = Mathf.Sqrt(Mathf.Max((a - c[k]).sqrMagnitude, Mathf.Max((b - c[k]).sqrMagnitude, (d - c[k]).sqrMagnitude)));
                    if (mx[k].y < lo || mn[k].y > hi) continue;
                    Vector3Int lo3 = Cell(mn[k]), hi3 = Cell(mx[k]);
                    for (int x = lo3.x; x <= hi3.x; x++) for (int y = lo3.y; y <= hi3.y; y++) for (int z = lo3.z; z <= hi3.z; z++)
                    { var key = new Vector3Int(x, y, z); if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>(); l.Add(t); }
                }
            }
            public bool Overlaps(int t, Soup o, int u)
            {
                int k = t / 3, j = u / 3;
                return mn[k].x <= o.mx[j].x && mx[k].x >= o.mn[j].x && mn[k].y <= o.mx[j].y && mx[k].y >= o.mn[j].y && mn[k].z <= o.mx[j].z && mx[k].z >= o.mn[j].z;
            }
        }

        /// <summary>The nearest point of <paramref name="s"/>'s faces to <paramref name="p"/> within the window; a triangle
        /// whose bounding sphere lies farther than the best so far is skipped unmeasured.</summary>
        static bool Nearest(Vector3 p, Soup s, out int tri, out Vector3 q, out bool face)
        {
            float best = Window * Window; tri = -1; q = default; face = false; var cc = Cell(p);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                if (s.grid.TryGetValue(cc + new Vector3Int(dx, dy, dz), out var l))
                    foreach (var t in l)
                    {
                        float away = (p - s.c[t / 3]).magnitude - s.rad[t / 3]; if (away > 0 && away * away >= best) continue;
                        var x = ClosestOnTriangle(p, s.P[s.T[t]], s.P[s.T[t + 1]], s.P[s.T[t + 2]], out bool f); float d = (x - p).sqrMagnitude;
                        if (d < best) { best = d; tri = t; q = x; face = f; }
                    }
            return tri >= 0;
        }

        static Vector3 FaceNormal(Vector3[] P, int[] T, int t) => Vector3.Cross(P[T[t + 1]] - P[T[t]], P[T[t + 2]] - P[T[t]]).normalized;

        static int NearestCorner(Vector3[] P, int[] T, int t, Vector3 q)
        {
            int v = T[t]; float d = (P[v] - q).sqrMagnitude;
            for (int k = 1; k < 3; k++) { float e = (P[T[t + k]] - q).sqrMagnitude; if (e < d) { d = e; v = T[t + k]; } }
            return v;
        }

        /// <summary>Length of the segment where triangles abc and xyz cross, and its midpoint; 0 where they do not cross or
        /// share a corner. Neighbouring faces of one merged mesh meet without crossing, so a pair sharing even one corner is
        /// dropped, and a garment welded to the body loses only the crossings of the triangles at the weld. Pure.</summary>
        internal static float CrossLength(Vector3 a, Vector3 b, Vector3 c, Vector3 x, Vector3 y, Vector3 z, List<Vector3> hits, out Vector3 mid)
        {
            mid = default; hits.Clear();
            if (Shares(a, x, y, z) || Shares(b, x, y, z) || Shares(c, x, y, z)) return 0f;
            SegTri(a, b, x, y, z, hits); SegTri(b, c, x, y, z, hits); SegTri(c, a, x, y, z, hits);
            SegTri(x, y, a, b, c, hits); SegTri(y, z, a, b, c, hits); SegTri(z, x, a, b, c, hits);
            float len = 0f;
            for (int i = 0; i < hits.Count; i++) for (int j = i + 1; j < hits.Count; j++) { float d = (hits[i] - hits[j]).magnitude; if (d > len) { len = d; mid = (hits[i] + hits[j]) * 0.5f; } }
            return len;
        }

        static bool Shares(Vector3 p, Vector3 x, Vector3 y, Vector3 z) => (p - x).sqrMagnitude < 1e-12f || (p - y).sqrMagnitude < 1e-12f || (p - z).sqrMagnitude < 1e-12f;

        // Segment p→q against triangle abc (Möller–Trumbore); adds the hit point.
        static void SegTri(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c, List<Vector3> hits)
        {
            Vector3 dir = q - p, e1 = b - a, e2 = c - a, h = Vector3.Cross(dir, e2); float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-12f) return;
            float f = 1f / det; Vector3 s = p - a; float u = f * Vector3.Dot(s, h); if (u < 0 || u > 1) return;
            Vector3 qq = Vector3.Cross(s, e1); float v = f * Vector3.Dot(dir, qq); if (v < 0 || u + v > 1) return;
            float t = f * Vector3.Dot(e2, qq); if (t < 0 || t > 1) return;
            hits.Add(p + dir * t);
        }

        // ── Regions and groups ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The root Animator's humanoid map, transform name → HumanBodyBones name; empty without a humanoid Avatar.</summary>
        internal static Dictionary<string, string> HumanNames(Transform rt)
        {
            var d = new Dictionary<string, string>(); var a = rt.GetComponent<Animator>();
            if (a == null || a.avatar == null || !a.avatar.isHuman) return d;
            foreach (var hb in a.avatar.humanDescription.human) if (!string.IsNullOrEmpty(hb.boneName)) d[hb.boneName] = hb.humanName.Replace(" ", "");
            return d;
        }

        /// <summary>A body bone's region: its first ancestor-or-self whose bare name (before any <c>$</c>) is a humanoid
        /// bone's, then <c>&gt;</c> and the non-humanoid child the walk came up through, if any. Pure over the hierarchy.</summary>
        internal static string Region(Transform bone, Dictionary<string, string> human)
        {
            Transform below = null;
            for (var t = bone; t != null; below = t, t = t.parent)
                if (human.TryGetValue(t.name.Split('$')[0], out var hb)) return below == null ? hb : hb + ">" + below.name.Split('$')[0];
            return Unmapped;
        }

        /// <summary>Group labels by transform name; a name two groups share becomes each one's root-relative path.</summary>
        internal static Dictionary<Transform, string> GroupLabels(Transform rt, Transform[] groups)
        {
            var dup = new HashSet<string>(groups.GroupBy(g => g.name).Where(x => x.Count() > 1).Select(x => x.Key));
            return groups.Distinct().ToDictionary(g => g, g => dup.Contains(g.name) ? AnimationUtility.CalculateTransformPath(g, rt) : g.name);
        }

        /// <summary>A garment bone's group: its innermost ancestor-or-self that is a group, else <see cref="Other"/>.</summary>
        internal static string GroupOf(Transform bone, Transform rt, Dictionary<Transform, string> label)
        {
            for (var t = bone; t != null && t != rt; t = t.parent) if (label.TryGetValue(t, out var l)) return l;
            return Other;
        }

        // ── Tables ──────────────────────────────────────────────────────────────────────────────────────────

        static string Gap(Bucket b) => b.gapN > 0 ? N(b.gapSumCm / b.gapN) : "-";

        static IEnumerable<KeyValuePair<(string body, string region, string group), Bucket>> Worst(IEnumerable<KeyValuePair<(string body, string region, string group), Bucket>> rows) =>
            rows.OrderByDescending(x => x.Value.crossCm).ThenByDescending(x => x.Value.behind).ThenByDescending(x => x.Value.signed)
                .ThenBy(x => x.Key.body, StringComparer.Ordinal).ThenBy(x => x.Key.region, StringComparer.Ordinal).ThenBy(x => x.Key.group, StringComparer.Ordinal);

        /// <summary>The single-pose table, worst crossing first, capped at <paramref name="cap"/> rows with the rest summed. Pure.</summary>
        internal static string Table(Dictionary<(string body, string region, string group), Bucket> buckets, int cap = TableCap)
        {
            var sb = new System.Text.StringBuilder("body | region | group | crossingCm | behind/signed | edgeNearest | maxDepthCm | throughCm | meanGapCm");
            var rows = Worst(buckets).ToList();
            foreach (var x in rows.Take(cap))
            {
                var b = x.Value;
                sb.Append("\n" + x.Key.body + " | " + x.Key.region + " | " + x.Key.group + " | " + (b.crossCm > 0 ? N(b.crossCm) : "-") + " | " + b.behind + "/" + b.signed + " | " + b.edge
                    + " | " + (b.maxDepthCm > 0 ? N(b.maxDepthCm) : "-") + " | " + (b.throughCm > 0 ? N(b.throughCm) : "-") + " | " + Gap(b));
            }
            if (rows.Count > cap) sb.Append("\n… " + (rows.Count - cap) + " more buckets: crossingCm " + N(rows.Skip(cap).Sum(x => x.Value.crossCm)) + ", behind " + rows.Skip(cap).Sum(x => x.Value.behind) + ", signed " + rows.Skip(cap).Sum(x => x.Value.signed));
            if (rows.Count == 0) sb.Append("\n(no garment vertex within 5 cm of a body face)");
            return sb.ToString();
        }

        /// <summary>A drive's rows side by side: one line per body × region × group, one column per row, ordered by each
        /// bucket's worst row. Pure.</summary>
        internal static string Pivot(IList<(string row, Dictionary<(string body, string region, string group), Bucket> buckets)> rows, int cap = PivotCap)
        {
            float Max(Func<Bucket, float> f, (string, string, string) k) => rows.Max(r => r.buckets.TryGetValue(k, out var b) ? f(b) : 0);
            var keys = rows.SelectMany(r => r.buckets.Keys).Distinct().Select(k => (k, cross: Max(b => b.crossCm, k), behind: Max(b => b.behind, k), signed: Max(b => b.signed, k)))
                .OrderByDescending(x => x.cross).ThenByDescending(x => x.behind).ThenByDescending(x => x.signed)
                .ThenBy(x => x.k.Item1, StringComparer.Ordinal).ThenBy(x => x.k.Item2, StringComparer.Ordinal).ThenBy(x => x.k.Item3, StringComparer.Ordinal).ToList();
            var sb = new System.Text.StringBuilder("clipping by body, region and group (cell: x crossing cm, behind/signed, e edgeNearest, d max depth cm, t through cm, g mean gap cm; - no vertex in the bucket; worst bucket first; on a row sampled every frame each is its peak over the frames and g the bucket's latest)\nbody | region | group | "
                + string.Join(" | ", rows.Select(r => r.row)));
            foreach (var x in keys.Take(cap))
                sb.Append("\n" + x.k.Item1 + " | " + x.k.Item2 + " | " + x.k.Item3 + " | " + string.Join(" | ", rows.Select(r => r.buckets.TryGetValue(x.k, out var b) ? CellText(b) : "-")));
            if (keys.Count > cap) sb.Append("\n… " + (keys.Count - cap) + " more buckets, none crossing more than " + N(keys[cap].cross) + " cm or more than " + keys[cap].behind + " behind in any row");
            return sb.ToString();
        }

        static string CellText(Bucket b) => (b.crossCm > 0 ? "x" + N(b.crossCm) + " " : "") + b.behind + "/" + b.signed + " e" + b.edge + (b.maxDepthCm > 0 ? " d" + N(b.maxDepthCm) : "")
            + (b.throughCm > 0 ? " t" + N(b.throughCm) : "") + (b.gapN > 0 ? " g" + Gap(b) : "");

        static string N(float f) => f.ToString("F1", CultureInfo.InvariantCulture);

        static string Fail(string m) { var s = Tag + " FAIL: " + m; Debug.LogWarning(s); return s; }

        /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection §5.1.5); <paramref name="onFace"/>
        /// is true only when it lies inside the face rather than on an edge or vertex.</summary>
        internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out bool onFace)
        {
            onFace = false;
            Vector3 ab = b - a, ac = c - a, ap = p - a; float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + d1 / (d1 - d3) * ab;
            Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + d2 / (d2 - d6) * ac;
            float va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);
            onFace = true;
            float den = 1f / (va + vb + vc); return a + ab * (vb * den) + ac * (vc * den);
        }
    }
}
