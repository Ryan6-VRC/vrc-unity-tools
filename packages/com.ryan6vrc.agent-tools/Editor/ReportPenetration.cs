using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>Garment vertices inside a body mesh at the current pose, edit or play. Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class ReportPenetration
    {
        const string Tag = "[ReportPenetration]";

        public static string Run(string avatarRoot, string bodyMesh, string[] garments)
        {
            var h = SceneHandle.Resolve(avatarRoot); if (!h.Ok) return Tag + " FAIL: " + h.Refusal;
            var err = Resolve(h.Object.transform, bodyMesh, garments, out var body, out var gar);
            if (err != null) return Tag + " FAIL: " + err;
            var (inside, tested, depthCm) = Measure(h.Object.transform, body, gar);
            return Tag + " " + h.Object.name + " body=" + body.name + " garments=" + string.Join(",", gar.Select(g => g.name))
                + " => OK | inside=" + inside + "/" + tested + " maxDepthCm=" + depthCm.ToString("F1");
        }

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

        /// <summary>Every other garment vertex, signed distance to its closest body triangle within 5 cm; below
        /// −2 mm counts as inside. Body triangles are gridded only across the garments' own height band.</summary>
        internal static (int, int, float) Measure(Transform rt, SkinnedMeshRenderer body, SkinnedMeshRenderer[] garments)
        {
            var gv = new List<Vector3>(); var m = new Mesh();
            foreach (var g in garments) { g.BakeMesh(m, true); var w = g.transform.localToWorldMatrix; gv.AddRange(m.vertices.Where((v, i) => i % 2 == 0).Select(v => rt.InverseTransformPoint(w.MultiplyPoint3x4(v)))); }
            body.BakeMesh(m, true); var bw = body.transform.localToWorldMatrix; var bv = m.vertices.Select(v => rt.InverseTransformPoint(bw.MultiplyPoint3x4(v))).ToArray(); var bt = m.triangles;
            Object.DestroyImmediate(m);
            if (gv.Count == 0) return (0, 0, 0f);
            float lo = gv.Min(v => v.y) - 0.05f, hi = gv.Max(v => v.y) + 0.05f, cell = 0.05f;
            var grid = new Dictionary<Vector3Int, List<int>>();
            Vector3Int C(Vector3 p) => Vector3Int.FloorToInt(p / cell);
            for (int t = 0; t < bt.Length; t += 3)
            {
                Vector3 a = bv[bt[t]], b = bv[bt[t + 1]], c = bv[bt[t + 2]];
                if (Mathf.Max(a.y, b.y, c.y) < lo || Mathf.Min(a.y, b.y, c.y) > hi) continue;
                Vector3Int mn = C(Vector3.Min(a, Vector3.Min(b, c))), mx = C(Vector3.Max(a, Vector3.Max(b, c)));
                for (int x = mn.x; x <= mx.x; x++) for (int y = mn.y; y <= mx.y; y++) for (int z = mn.z; z <= mx.z; z++)
                { var k = new Vector3Int(x, y, z); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(t); }
            }
            int inside = 0; float depth = 0;
            foreach (var p in gv)
            {
                float best = 0.0025f; int bi = -1; Vector3 bq = default; var cp = C(p);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue(cp + new Vector3Int(dx, dy, dz), out var l))
                        foreach (var t in l) { var q = ClosestOnTriangle(p, bv[bt[t]], bv[bt[t + 1]], bv[bt[t + 2]]); float d = (q - p).sqrMagnitude; if (d < best) { best = d; bi = t; bq = q; } }
                if (bi < 0) continue;
                float s = Vector3.Dot(p - bq, Vector3.Cross(bv[bt[bi + 1]] - bv[bt[bi]], bv[bt[bi + 2]] - bv[bt[bi]]).normalized);
                if (s < -0.002f) { inside++; depth = Mathf.Max(depth, -s); }
            }
            return (inside, gv.Count, depth * 100f);
        }

        /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection §5.1.5).</summary>
        internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a; float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap); if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + d1 / (d1 - d3) * ab;
            Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + d2 / (d2 - d6) * ac;
            float va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);
            float den = 1f / (va + vb + vc); return a + ab * (vb * den) + ac * (vc * den);
        }
    }
}
