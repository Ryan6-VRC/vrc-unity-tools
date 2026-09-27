using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Garment vertices behind the nearest body face (a nearest-surface sign, not containment) at the current
    /// pose, edit or play, split by body region and garment group. Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class ReportPenetration
    {
        const string Tag = "[ReportPenetration]";
        const float Window = 0.05f, Behind = 0.002f;   // world metres: nearest-face search radius, and the depth that counts
        internal const string Unmapped = "(unmapped)", Other = "(other)";
        const int TableCap = 24, PivotCap = 30;

        /// <summary>One region × group cell. <c>signed</c> includes <c>behind</c>; the gap sum runs over the signed
        /// vertices not behind, so its mean is the bucket's standoff.</summary>
        public class Bucket { public int behind, signed, edge; public float maxDepthCm, gapSumCm; }

        public struct Result
        {
            public int behind, signed, edgeNearest, sampled; public float maxDepthCm;
            public Dictionary<(string region, string group), Bucket> buckets;
            public int regionBones; public string groupSource;
        }

        public static string Run(string avatarRoot, string bodyMesh, string[] garments, string[] groups = null)
        {
            var h = SceneHandle.Resolve(avatarRoot); if (!h.Ok) return Tag + " FAIL: " + h.Refusal;
            var rt = h.Object.transform; Transform[] gt = null;
            var err = Resolve(rt, bodyMesh, garments, out var body, out var gar) ?? ResolveGroups(rt, groups, out gt);
            if (err != null) return Tag + " FAIL: " + err;
            var r = Measure(rt, body, gar, gt);
            return Tag + " " + h.Object.name + " body=" + body.name + " garments=" + string.Join(",", gar.Select(g => g.name)) + " => OK | " + Format(r) + " | " + Maps(r, rt)
                + "\n" + Legend + "\n" + Table(r.buckets, TableCap);
        }

        internal static string Format(Result r) => "vertsBehindBody=" + r.behind + "/" + r.signed + " edgeNearest=" + r.edgeNearest + " sampled=" + r.sampled
            + " maxDepthCm=" + N(r.maxDepthCm);

        internal static string Maps(Result r, Transform rt) => "groups=" + r.groupSource + " regionBones=" + r.regionBones
            + (r.regionBones == 0 ? " (the Animator on '" + rt.name + "' carries no humanoid Avatar: every region reads " + Unmapped + ")" : "");

        internal const string Legend = "region: the humanoid bone skinning the nearest body face, matched by bone name up the body bone's ancestors (so a costume armature maps too);"
            + " '>X' names the non-humanoid branch below it. group: the garment vertex's innermost group. meanGapCm: mean distance off the face of the bucket's signed vertices"
            + " not behind it, within the 5 cm window; a large gap beside zero behind is a chain held off the body.";

        /// <summary>Each name is a root-relative path or a SkinnedMeshRenderer name unique under the root.</summary>
        internal static string Resolve(Transform rt, string bodyMesh, string[] garments, out SkinnedMeshRenderer body, out SkinnedMeshRenderer[] gar)
        {
            gar = null; var smrs = rt.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            string err = null;
            SkinnedMeshRenderer One(string n)
            {
                if (err != null) return null;
                var at = n != null && n.Contains("/") ? WriteDynamics.Find(rt, n, out _) : null;
                var hits = smrs.Where(s => at != null ? s.transform == at : s.name == n).ToArray();
                if (hits.Length != 1) err = "'" + n + "' names " + hits.Length + " SkinnedMeshRenderers under '" + rt.name + "'"
                    + (hits.Length > 1 ? " (" + string.Join(", ", hits.Select(s => AnimationUtility.CalculateTransformPath(s.transform, rt))) + "); pass a path" : "; present: " + string.Join(", ", smrs.Select(s => s.name)));
                return hits.Length == 1 ? hits[0] : null;
            }
            body = One(bodyMesh);
            if (garments == null || garments.Length == 0) return err ?? "pass at least one garment";
            gar = garments.Select(One).ToArray();
            return err;
        }

        /// <summary>Garment groups: each a root-relative path, or a transform name unique under the root (a play-built
        /// <c>name$suffix</c> matches its bare name). Null or empty leaves <paramref name="gt"/> null, which groups by physbone chain.</summary>
        internal static string ResolveGroups(Transform rt, string[] groups, out Transform[] gt)
        {
            gt = null; if (groups == null || groups.Length == 0) return null;
            var all = rt.GetComponentsInChildren<Transform>(true); var list = new List<Transform>();
            foreach (var g in groups)
            {
                if (g != null && g.Contains("/")) { var t = WriteDynamics.Find(rt, g, out var err); if (t == null) return "groups: " + err; list.Add(t); continue; }
                var hits = all.Where(t => t != rt && (t.name == g || t.name.Split('$')[0] == g)).ToArray();
                if (hits.Length != 1) return "groups: '" + g + "' names " + hits.Length + " transforms under '" + rt.name + "'"
                    + (hits.Length > 1 ? " (" + string.Join(", ", hits.Select(t => AnimationUtility.CalculateTransformPath(t, rt))) + "); pass a path" : "");
                list.Add(hits[0]);
            }
            gt = list.ToArray(); return null;
        }

        /// <summary>Every other garment vertex in world space, against its nearest body triangle within the window. A
        /// nearest point inside a face is signed by that face's normal; one on an edge or vertex has no single face to
        /// sign it by and is counted as <c>edgeNearest</c>, not guessed. Body triangles are gridded only across the
        /// garments' own world-height band. Each vertex lands in the bucket of its nearest face's region and its own
        /// group; <paramref name="groups"/> null groups by the active physbone chains' effective roots.</summary>
        internal static Result Measure(Transform rt, SkinnedMeshRenderer body, SkinnedMeshRenderer[] garments, Transform[] groups)
        {
            string groupSource = "given:" + groups?.Length;
            if (groups == null)
            {
                groups = rt.GetComponentsInChildren<VRCPhysBoneBase>(true).Where(p => p.isActiveAndEnabled).Select(WriteDynamics.EffRoot).Distinct().ToArray();
                groupSource = "chains:" + groups.Length;
            }
            var label = GroupLabels(rt, groups);
            var gv = new List<Vector3>(); var gg = new List<string>(); var m = new Mesh();
            foreach (var g in garments)
            {
                g.BakeMesh(m, true); var w = g.transform.localToWorldMatrix; var vs = m.vertices;
                var bw = g.sharedMesh.boneWeights; var byBone = g.bones.Select(b => GroupOf(b, rt, label)).ToArray();
                for (int i = 0; i < vs.Length; i += 2)
                {
                    gv.Add(w.MultiplyPoint3x4(vs[i]));
                    gg.Add(i < bw.Length && bw[i].boneIndex0 < byBone.Length ? byBone[bw[i].boneIndex0] : Other);
                }
            }
            body.BakeMesh(m, true); var bwm = body.transform.localToWorldMatrix; var bv = m.vertices.Select(v => bwm.MultiplyPoint3x4(v)).ToArray(); var bt = m.triangles;
            Object.DestroyImmediate(m);
            var human = HumanNames(rt);
            var bbw = body.sharedMesh.boneWeights; var region = body.bones.Select(b => Region(b, human)).ToArray();
            var r = new Result { sampled = gv.Count, buckets = new Dictionary<(string, string), Bucket>(), regionBones = human.Count, groupSource = groupSource };
            if (gv.Count == 0) return r;
            float lo = gv.Min(v => v.y) - Window, hi = gv.Max(v => v.y) + Window;
            var grid = new Dictionary<Vector3Int, List<int>>();
            Vector3Int C(Vector3 p) => Vector3Int.FloorToInt(p / Window);
            for (int t = 0; t < bt.Length; t += 3)
            {
                Vector3 a = bv[bt[t]], b = bv[bt[t + 1]], c = bv[bt[t + 2]];
                if (Mathf.Max(a.y, b.y, c.y) < lo || Mathf.Min(a.y, b.y, c.y) > hi) continue;
                Vector3Int mn = C(Vector3.Min(a, Vector3.Min(b, c))), mx = C(Vector3.Max(a, Vector3.Max(b, c)));
                for (int x = mn.x; x <= mx.x; x++) for (int y = mn.y; y <= mx.y; y++) for (int z = mn.z; z <= mx.z; z++)
                { var k = new Vector3Int(x, y, z); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(t); }
            }
            // A face takes the region of its vertex nearest the hit, so a face straddling two regions splits where its skinning does.
            string RegionAt(int t, Vector3 q)
            {
                int v = bt[t]; float d = (bv[v] - q).sqrMagnitude;
                for (int k = 1; k < 3; k++) { float e = (bv[bt[t + k]] - q).sqrMagnitude; if (e < d) { d = e; v = bt[t + k]; } }
                return v < bbw.Length && bbw[v].boneIndex0 < region.Length ? region[bbw[v].boneIndex0] : Unmapped;
            }
            float depth = 0;
            for (int i = 0; i < gv.Count; i++)
            {
                var p = gv[i];
                float best = Window * Window; int bi = -1; Vector3 bq = default; bool face = false; var cp = C(p);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue(cp + new Vector3Int(dx, dy, dz), out var l))
                        foreach (var t in l) { var q = ClosestOnTriangle(p, bv[bt[t]], bv[bt[t + 1]], bv[bt[t + 2]], out bool f); float d = (q - p).sqrMagnitude; if (d < best) { best = d; bi = t; bq = q; face = f; } }
                if (bi < 0) continue;
                var key = (RegionAt(bi, bq), gg[i]);
                if (!r.buckets.TryGetValue(key, out var k)) r.buckets[key] = k = new Bucket();
                if (!face) { r.edgeNearest++; k.edge++; continue; }
                r.signed++; k.signed++;
                float s = Vector3.Dot(p - bq, Vector3.Cross(bv[bt[bi + 1]] - bv[bt[bi]], bv[bt[bi + 2]] - bv[bt[bi]]).normalized);
                if (s < -Behind) { r.behind++; k.behind++; depth = Mathf.Max(depth, -s); k.maxDepthCm = Mathf.Max(k.maxDepthCm, -s * 100f); }
                else k.gapSumCm += s * 100f;
            }
            r.maxDepthCm = depth * 100f;
            return r;
        }

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

        static string Gap(Bucket b) => b.signed > b.behind ? N(b.gapSumCm / (b.signed - b.behind)) : "-";

        /// <summary>The single-pose table, most behind first, capped at <paramref name="cap"/> rows with the rest summed. Pure.</summary>
        internal static string Table(Dictionary<(string region, string group), Bucket> buckets, int cap = TableCap)
        {
            var sb = new System.Text.StringBuilder("region | group | behind/signed | edgeNearest | maxDepthCm | meanGapCm");
            var rows = buckets.OrderByDescending(x => x.Value.behind).ThenByDescending(x => x.Value.signed)
                .ThenBy(x => x.Key.region, System.StringComparer.Ordinal).ThenBy(x => x.Key.group, System.StringComparer.Ordinal).ToList();
            foreach (var x in rows.Take(cap))
                sb.Append("\n" + x.Key.region + " | " + x.Key.group + " | " + x.Value.behind + "/" + x.Value.signed + " | " + x.Value.edge + " | " + (x.Value.behind > 0 ? N(x.Value.maxDepthCm) : "-") + " | " + Gap(x.Value));
            if (rows.Count > cap) sb.Append("\n… " + (rows.Count - cap) + " more buckets: behind " + rows.Skip(cap).Sum(x => x.Value.behind) + ", signed " + rows.Skip(cap).Sum(x => x.Value.signed));
            if (rows.Count == 0) sb.Append("\n(no garment vertex within 5 cm of a body face)");
            return sb.ToString();
        }

        /// <summary>A drive's rows side by side: one line per region × group, one column per row, ordered by each
        /// bucket's worst row. Pure.</summary>
        internal static string Pivot(IList<(string row, Dictionary<(string region, string group), Bucket> buckets)> rows, int cap = PivotCap)
        {
            int Max(System.Func<Bucket, int> f, (string, string) k) => rows.Max(r => r.buckets.TryGetValue(k, out var b) ? f(b) : 0);
            var keys = rows.SelectMany(r => r.buckets.Keys).Distinct().Select(k => (k, behind: Max(b => b.behind, k), signed: Max(b => b.signed, k)))
                .OrderByDescending(x => x.behind).ThenByDescending(x => x.signed).ThenBy(x => x.k.Item1, System.StringComparer.Ordinal).ThenBy(x => x.k.Item2, System.StringComparer.Ordinal).ToList();
            var sb = new System.Text.StringBuilder("penetration by region and group (cell: behind/signed, d max depth cm, g mean gap cm; - no vertex in the bucket; worst bucket first)\nregion | group | "
                + string.Join(" | ", rows.Select(r => r.row)));
            foreach (var x in keys.Take(cap))
                sb.Append("\n" + x.k.Item1 + " | " + x.k.Item2 + " | " + string.Join(" | ", rows.Select(r => r.buckets.TryGetValue(x.k, out var b) ? Cell(b) : "-")));
            if (keys.Count > cap) sb.Append("\n… " + (keys.Count - cap) + " more buckets, none more than " + keys[cap].behind + " behind in any row");
            return sb.ToString();
        }

        static string Cell(Bucket b) => b.behind + "/" + b.signed + (b.behind > 0 ? " d" + N(b.maxDepthCm) : "") + (b.signed > b.behind ? " g" + Gap(b) : "");

        static string N(float f) => f.ToString("F1", CultureInfo.InvariantCulture);

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
