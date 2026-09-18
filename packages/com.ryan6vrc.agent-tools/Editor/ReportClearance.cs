using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only rest-pose clearance digest between a body mesh and the physbone chains hung around it: the
    /// classifier the fix-clipping skill runs before choosing a branch, and the rest-contact gate it re-runs
    /// after a collider write. Per chain joint it measures the gap to the nearest baked body vertex against
    /// the chain's own collision radius there, the gap to every collider the chain references, and prints the
    /// authored facts a collider decision reads (angle limits, existing collider coverage, capsule extent,
    /// which bones weight the body and garment near the chain). Numbers only — a Report, never a verdict.
    ///
    /// Surface: the NDMF preview proxy where one exists (a composed body with its shrink/shape reactions
    /// applied), else the scene instance (reactions sit at weight 0 there — the header says which was read).
    /// Edit mode only, so this is the rest-pose approximation of what a play-mode probe measures exactly:
    /// no physbone solve, no pose. Chain roots under a VRC constraint are pumped once so a cold editor does
    /// not report a stale pose as rest (`emulator.md` §Edit-mode VRC constraints); a pump that cannot land is
    /// reported, never hidden.
    ///
    /// INSPECTION ONLY — bakes into throwaway meshes, writes nothing to the scene or project.
    /// </summary>
    [AgentTool]
    public static class ReportClearance
    {
        // Per-joint radius is radius × radiusCurve(t), t = depth / maxDepth along the chain, which is how the
        // SDK parameterises every per-bone curve. A joint with lateral freedom under this many degrees is
        // flagged `lateralLocked`: a collider cannot swing it sideways, whatever its size.
        internal const float LateralLockDeg = 1f;
        // Body vertices whose nearest chain joint lies within (joint radius + this) feed the weight readout.
        internal const float WeightBandMeters = 0.03f;

        internal struct JointRow
        {
            public string Name; public int Depth; public Vector3 Pos; public float Radius;
            public float BodyGap; public float BodySlack; public float ColliderSlack; public string NearestCollider;
        }

        internal class ChainRow
        {
            public VRCPhysBone Bone; public string Key; public Transform Root; public List<JointRow> Joints = new List<JointRow>();
            public float MinBodySlack = float.PositiveInfinity, MinColliderSlack = float.PositiveInfinity;
            public bool LateralLocked; public string Limits; public string Constraint; public string Colliders;
            public Dictionary<string, float> BodyWeights = new Dictionary<string, float>();
            public Dictionary<string, float> GarmentWeights = new Dictionary<string, float>();
            public int BodyNear, GarmentNear;
        }

        internal struct ColliderRow
        {
            public VRCPhysBoneColliderBase Col; public string Path; public string RootName; public Vector3 A, B; public float Radius;
            public string Shape; public float EndToEnd; public List<string> Referencing; public List<string> NotReferencing;
        }

        // ── Door ────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Digest rest clearance under scene object <paramref name="avatarRoot"/> between the body
        /// SkinnedMeshRenderer at <paramref name="bodyMesh"/> (a path relative to the root, or a descendant name)
        /// and every active physbone chain under the root whose effective root's relative path starts with
        /// <paramref name="chainPrefix"/> (null = every chain). Returns a one-line summary ending
        /// <c>=&gt; OK | log=&lt;path&gt;</c>; a handle that does not resolve, a body with no mesh, or a prefix
        /// matching no chain is a bare <c>[ReportClearance] FAIL: …</c> with no trailer.</summary>
        public static string Run(string avatarRoot, string bodyMesh, string chainPrefix = null)
        {
            var handle = SceneHandle.Resolve(avatarRoot);
            if (!handle.Ok) return Fail("avatarRoot: " + handle.Refusal);
            var root = handle.Object;
            if (string.IsNullOrWhiteSpace(bodyMesh)) return Fail("bodyMesh: pass the body renderer's path under the root or its name");
            var bodyGo = ResolveDescendant(root, bodyMesh);
            if (bodyGo == null) return Fail("bodyMesh: nothing named or at `" + bodyMesh + "` under " + root.name);
            var bodySmr = bodyGo.GetComponent<SkinnedMeshRenderer>();
            if (bodySmr == null || bodySmr.sharedMesh == null) return Fail("bodyMesh: `" + bodyMesh + "` carries no SkinnedMeshRenderer with a mesh");

            var chains = root.GetComponentsInChildren<VRCPhysBone>(true)
                .Where(b => b.enabled && b.gameObject.activeInHierarchy)
                .Where(b => chainPrefix == null || RelPath(root.transform, EffectiveRoot(b)).StartsWith(chainPrefix.Trim('/'), StringComparison.Ordinal)
                                                || EffectiveRoot(b).name == chainPrefix)
                .ToList();
            if (chains.Count == 0) return Fail(chainPrefix == null ? "no active VRCPhysBone under " + root.name
                                                                   : "chainPrefix: no active chain root at or named `" + chainPrefix + "` under " + root.name);

            string pumpNote = PumpConstraints(root, chains);

            var proxies = ProxyMap();
            var bodyPts = Bake(bodySmr, proxies, out string bodySurface);
            var garmentSmrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s != bodySmr && s.gameObject.activeInHierarchy && s.sharedMesh != null).ToList();

            var rows = chains.Select(b => Measure(b, root.transform, bodyPts)).ToList();
            var colliders = ColliderRows(root, rows);
            foreach (var r in rows) FinishChain(r, colliders, bodySmr, garmentSmrs, proxies);

            return Emit(root, bodySmr, bodySurface, rows, colliders, pumpNote, chainPrefix);
        }

        // ── Chains ──────────────────────────────────────────────────────────────────────────────────────────

        internal static Transform EffectiveRoot(VRCPhysBone b) => b.rootTransform != null ? b.rootTransform : b.transform;

        // Joints: the effective root and every descendant not pruned by ignoreTransforms (a listed transform
        // prunes itself and its children — the field's own tooltip). A root with several children under
        // multiChildType=Ignore is not simulated itself; it is still listed, depth 0, so the reader sees it.
        internal static List<Transform> Joints(VRCPhysBone b)
        {
            var root = EffectiveRoot(b);
            var ignore = new HashSet<Transform>(b.ignoreTransforms.Where(t => t != null));
            var list = new List<Transform>();
            void Walk(Transform t) { if (ignore.Contains(t)) return; list.Add(t); foreach (Transform c in t) Walk(c); }
            Walk(root);
            return list;
        }

        private static int Depth(Transform t, Transform root) { int d = 0; while (t != root && t != null) { t = t.parent; d++; } return d; }

        private static ChainRow Measure(VRCPhysBone b, Transform avatar, List<Vector3> bodyPts)
        {
            var row = new ChainRow { Bone = b, Root = EffectiveRoot(b), Key = Key(EffectiveRoot(b).name) };
            var joints = Joints(b);
            int maxDepth = Math.Max(1, joints.Max(j => Depth(j, row.Root)));
            foreach (var j in joints)
            {
                int d = Depth(j, row.Root);
                float t = (float)d / maxDepth;
                float r = b.radius * (b.radiusCurve != null && b.radiusCurve.length > 0 ? b.radiusCurve.Evaluate(t) : 1f);
                float gap = bodyPts.Count == 0 ? float.NaN : NearestDistance(j.position, bodyPts);
                var jr = new JointRow { Name = Key(j.name), Depth = d, Pos = j.position, Radius = r, BodyGap = gap, BodySlack = gap - r,
                                        ColliderSlack = float.PositiveInfinity, NearestCollider = "—" };
                row.Joints.Add(jr);
                if (!float.IsNaN(jr.BodySlack)) row.MinBodySlack = Math.Min(row.MinBodySlack, jr.BodySlack);
            }
            row.Limits = Limits(b, out row.LateralLocked);
            row.Constraint = ConstraintSources(row.Root, avatar);
            return row;
        }

        // Lateral freedom: Hinge locks Z outright; Polar and Angle expose it as maxAngleZ / maxAngleX.
        internal static string Limits(VRCPhysBone b, out bool lateralLocked)
        {
            lateralLocked = false;
            switch (b.limitType)
            {
                case VRCPhysBoneBase.LimitType.None: return "none";
                case VRCPhysBoneBase.LimitType.Angle:
                    lateralLocked = b.maxAngleX < LateralLockDeg;
                    return "Angle max=" + F(b.maxAngleX) + " rot=" + V(b.limitRotation);
                case VRCPhysBoneBase.LimitType.Hinge:
                    lateralLocked = true;
                    return "Hinge max=" + F(b.maxAngleX) + " rot=" + V(b.limitRotation);
                case VRCPhysBoneBase.LimitType.Polar:
                    lateralLocked = b.maxAngleZ < LateralLockDeg;
                    return "Polar maxX=" + F(b.maxAngleX) + " maxZ=" + F(b.maxAngleZ) + " rot=" + V(b.limitRotation);
                default: return b.limitType.ToString();
            }
        }

        // The nearest VRC constraint on the chain root or an ancestor below the avatar root, with its sources:
        // a skirt whose roots are parent-constrained to Chest over a body on Hips is the cause the reader
        // needs to see, and no other number here says it.
        private static string ConstraintSources(Transform root, Transform avatar)
        {
            for (var t = root; t != null && t != avatar; t = t.parent)
            {
                var c = t.GetComponent<VRCConstraintBase>();
                if (c == null) continue;
                var sb = new StringBuilder(c.GetType().Name.Replace("VRC", "").Replace("Constraint", "")).Append('@').Append(Key(t.name)).Append(": ");
                for (int i = 0; i < c.Sources.Count; i++)
                {
                    var s = c.Sources[i];
                    if (i > 0) sb.Append(", ");
                    sb.Append(s.SourceTransform != null ? Key(s.SourceTransform.name) : "null").Append('=').Append(F(s.Weight));
                }
                return sb.ToString();
            }
            return "—";
        }

        // ── Colliders ───────────────────────────────────────────────────────────────────────────────────────

        // Every collider any region chain references, plus every active collider under the root that none
        // does (the audit case: a vendor wired 6 of 9 chains). Capsule: `height` is the core segment, so the
        // rounded ends add 2×radius to the end-to-end extent — printed beside the authored value because a
        // collider sized as if height were end-to-end lands 2r too long.
        private static List<ColliderRow> ColliderRows(GameObject root, List<ChainRow> rows)
        {
            var all = new List<VRCPhysBoneColliderBase>();
            foreach (var c in root.GetComponentsInChildren<VRCPhysBoneCollider>(true)) if (c.gameObject.activeInHierarchy) all.Add(c);
            foreach (var r in rows) foreach (var c in r.Bone.colliders) if (c != null && !all.Contains(c)) all.Add(c);
            var list = new List<ColliderRow>();
            foreach (var c in all)
            {
                var cr = new ColliderRow { Col = c, Path = RelPath(root.transform, c.transform), Radius = c.radius, Referencing = new List<string>(), NotReferencing = new List<string>() };
                var rt = c.rootTransform != null ? c.rootTransform : c.transform;
                cr.RootName = Key(rt.name);
                // rootTransform scale multiplies radius/height in the SDK's shape; measured from lossyScale's
                // largest axis, the same choice the session's bounds check agreed with on a uniformly scaled rig.
                float s = Mathf.Max(rt.lossyScale.x, rt.lossyScale.y, rt.lossyScale.z);
                cr.Radius = c.radius * s;
                var center = rt.TransformPoint(c.position);
                if (c.shapeType == VRCPhysBoneColliderBase.ShapeType.Capsule)
                {
                    var axis = rt.rotation * c.rotation * Vector3.up;
                    float half = c.height * s * 0.5f;
                    cr.A = center - axis * half; cr.B = center + axis * half;
                    cr.EndToEnd = c.height * s + 2f * cr.Radius;
                    cr.Shape = "capsule r=" + Cm(cr.Radius) + " h=" + Cm(c.height * s) + " endToEnd=" + Cm(cr.EndToEnd);
                }
                else if (c.shapeType == VRCPhysBoneColliderBase.ShapeType.Sphere)
                {
                    cr.A = cr.B = center; cr.EndToEnd = 2f * cr.Radius;
                    cr.Shape = "sphere r=" + Cm(cr.Radius);
                }
                else { cr.A = cr.B = center; cr.Radius = 0; cr.EndToEnd = 0; cr.Shape = c.shapeType.ToString().ToLowerInvariant(); }
                foreach (var r in rows) (r.Bone.colliders.Contains(c) ? cr.Referencing : cr.NotReferencing).Add(r.Key);
                list.Add(cr);
            }
            return list;
        }

        private static void FinishChain(ChainRow r, List<ColliderRow> colliders, SkinnedMeshRenderer body, List<SkinnedMeshRenderer> garments,
                                        Dictionary<GameObject, SkinnedMeshRenderer> proxies)
        {
            var refd = colliders.Where(c => r.Bone.colliders.Contains(c.Col) && c.Col.shapeType != VRCPhysBoneColliderBase.ShapeType.Plane).ToList();
            r.Colliders = refd.Count == 0 ? "—" : string.Join(", ", refd.Select(c => Key(c.Col.name)));
            for (int i = 0; i < r.Joints.Count; i++)
            {
                var j = r.Joints[i];
                foreach (var c in refd)
                {
                    float slack = SegmentDistance(j.Pos, c.A, c.B) - c.Radius - j.Radius;
                    if (slack < j.ColliderSlack) { j.ColliderSlack = slack; j.NearestCollider = Key(c.Col.name); }
                }
                r.Joints[i] = j;
                if (!float.IsPositiveInfinity(j.ColliderSlack)) r.MinColliderSlack = Math.Min(r.MinColliderSlack, j.ColliderSlack);
            }
            // Weight readout over the vertices near this chain's joints: the body's bones vs the garment's.
            // Relative motion between those two bone sets is what walks the body into the garment.
            r.BodyNear = WeightsNear(body, proxies, r, r.BodyWeights);
            foreach (var g in garments) r.GarmentNear += WeightsNear(g, proxies, r, r.GarmentWeights);
        }

        // ── Geometry ────────────────────────────────────────────────────────────────────────────────────────

        // Bakes the renderer the preview shows when NDMF has a proxy for this GameObject, else the scene
        // instance; the proxy carries the composed reactions (shrink shapes at their worn weight) the scene
        // instance holds at 0. World-space points either way.
        private static List<Vector3> Bake(SkinnedMeshRenderer smr, Dictionary<GameObject, SkinnedMeshRenderer> proxies, out string surface)
        {
            SkinnedMeshRenderer src = smr;
            surface = "scene";
            if (proxies.TryGetValue(smr.gameObject, out var proxy) && proxy != null && proxy.sharedMesh != null) { src = proxy; surface = "ndmf-proxy"; }
            var m = new Mesh();
            var pts = new List<Vector3>();
            try
            {
                src.BakeMesh(m, true);
                var l2w = src.transform.localToWorldMatrix;
                foreach (var v in m.vertices) pts.Add(l2w.MultiplyPoint3x4(v));
            }
            finally { UnityEngine.Object.DestroyImmediate(m); }
            return pts;
        }

        // Proxy map: preview-scene renderer GameObject → its original, inverted to original → proxy SMR.
        // Public NDMF API reached by reflection (this asmdef has references:[]); drift = empty map = scene
        // surface, which the header names, so a drifted hook degrades loudly rather than silently.
        private static readonly Type NdmfPreviewType = VendorReflect.FindType("nadena.dev.ndmf.preview.NDMFPreview");
        private static readonly MethodInfo MiGetOriginalForProxy = NdmfPreviewType != null
            ? SafeGetMethod(NdmfPreviewType, "GetOriginalObjectForProxy", BindingFlags.Static | BindingFlags.Public) : null;
        private static readonly Type PreviewSceneManagerType = VendorReflect.FindType("nadena.dev.ndmf.preview.NDMFPreviewSceneManager");
        private static readonly MethodInfo MiIsPreviewScene = PreviewSceneManagerType != null
            ? SafeGetMethod(PreviewSceneManagerType, "IsPreviewScene", BindingFlags.Static | BindingFlags.Public) : null;

        private static Dictionary<GameObject, SkinnedMeshRenderer> ProxyMap()
        {
            var map = new Dictionary<GameObject, SkinnedMeshRenderer>();
            if (MiGetOriginalForProxy == null) return map;
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (!s.isLoaded || !IsPreviewScene(s)) continue;
                foreach (var sceneRoot in s.GetRootGameObjects())
                    foreach (var smr in sceneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        GameObject original;
                        try { original = MiGetOriginalForProxy.Invoke(null, new object[] { smr.gameObject }) as GameObject; }
                        catch { return new Dictionary<GameObject, SkinnedMeshRenderer>(); }
                        if (original != null && !map.ContainsKey(original)) map[original] = smr;
                    }
            }
            return map;
        }

        private static bool IsPreviewScene(Scene s)
        {
            if (MiIsPreviewScene != null) { try { return (bool)MiIsPreviewScene.Invoke(null, new object[] { s }); } catch { } }
            return s.name == "___NDMF Preview___";
        }

        private static float NearestDistance(Vector3 p, List<Vector3> pts)
        {
            float best = float.PositiveInfinity;
            for (int i = 0; i < pts.Count; i++) { float d = (pts[i] - p).sqrMagnitude; if (d < best) best = d; }
            return Mathf.Sqrt(best);
        }

        internal static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a; float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return (p - a).magnitude;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return (p - (a + ab * t)).magnitude;
        }

        // Top skin-weight bones over the vertices of `smr` within (joint radius + WeightBandMeters) of any joint
        // of the chain, read off the flat GetAllBoneWeights walk (the top-4 struct truncates). Returns the
        // vertex count that fed the readout; weights are accumulated into `acc` by bone name.
        private static int WeightsNear(SkinnedMeshRenderer smr, Dictionary<GameObject, SkinnedMeshRenderer> proxies, ChainRow r, Dictionary<string, float> acc)
        {
            var mesh = smr.sharedMesh; if (mesh == null) return 0;
            var pts = Bake(smr, proxies, out _);
            if (pts.Count != mesh.vertexCount) return 0; // a proxy with a different topology cannot index this mesh's weights
            var bones = smr.bones;
            var per = mesh.GetBonesPerVertex(); var w = mesh.GetAllBoneWeights();
            int ptr = 0, n = 0;
            for (int v = 0; v < per.Length; v++)
            {
                int count = per[v];
                bool near = false;
                foreach (var j in r.Joints) { float lim = j.Radius + WeightBandMeters; if ((pts[v] - j.Pos).sqrMagnitude <= lim * lim) { near = true; break; } }
                if (near)
                {
                    n++;
                    for (int k = 0; k < count; k++)
                    {
                        var bw = w[ptr + k]; if (bw.weight <= 0f || bw.boneIndex < 0 || bw.boneIndex >= bones.Length || bones[bw.boneIndex] == null) continue;
                        var name = Key(bones[bw.boneIndex].name);
                        acc[name] = (acc.TryGetValue(name, out var o) ? o : 0f) + bw.weight;
                    }
                }
                ptr += count;
            }
            return n;
        }

        // ── Constraint pump ─────────────────────────────────────────────────────────────────────────────────

        // Chain roots (or their ancestors under the avatar) driven by a VRC constraint sit at a stale pose in a
        // cold editor. One RegisterConstraint + UpdateConstraints(finalizeImmediately) settles them for this
        // read (measured, emulator.md); both entry points are non-public, so a drifted handle prints as
        // `constraintPump=unavailable` beside the count and the rest numbers are read as possibly stale.
        private static string PumpConstraints(GameObject root, List<VRCPhysBone> chains)
        {
            var driven = new HashSet<VRCConstraintBase>();
            foreach (var b in chains)
                for (var t = EffectiveRoot(b); t != null && t != root.transform; t = t.parent)
                    foreach (var c in t.GetComponents<VRCConstraintBase>()) driven.Add(c);
            if (driven.Count == 0) return "constraintDriven=0";
            try
            {
                var mgr = typeof(VRCConstraintBase).Assembly.GetType("VRC.Dynamics.VRCConstraintManager");
                var sched = typeof(VRCConstraintBase).Assembly.GetType("VRC.Dynamics.VRCDynamicsScheduler");
                var reg = mgr?.GetMethod("RegisterConstraint", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var upd = sched?.GetMethod("UpdateConstraints", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (reg == null || upd == null) return "constraintDriven=" + driven.Count + " constraintPump=unavailable";
                foreach (var c in driven) reg.Invoke(null, new object[] { c });
                var ps = upd.GetParameters();
                upd.Invoke(null, ps.Length == 1 ? new object[] { true } : Array.Empty<object>());
                return "constraintDriven=" + driven.Count + " constraintPump=ok";
            }
            catch (Exception e) { return "constraintDriven=" + driven.Count + " constraintPump=failed(" + e.GetType().Name + ")"; }
        }

        // ── Output ──────────────────────────────────────────────────────────────────────────────────────────

        private static string Emit(GameObject root, SkinnedMeshRenderer body, string surface, List<ChainRow> rows, List<ColliderRow> colliders,
                                   string pumpNote, string chainPrefix)
        {
            int contact = rows.Count(r => r.MinColliderSlack < 0f);
            int inside = rows.Count(r => r.MinBodySlack < 0f);
            int locked = rows.Count(r => r.LateralLocked);
            var summary = "[ReportClearance] " + root.name + " body=" + body.name + " surface=" + surface + " chains=" + rows.Count
                + " colliders=" + colliders.Count + " restContact=" + contact + " insideBodyAtRest=" + inside + " lateralLocked=" + locked
                + " " + pumpNote + " => OK";

            var sb = new StringBuilder();
            sb.Append("# Clearance: ").Append(root.name).Append(" / ").Append(body.name).Append("\n\n");
            sb.Append("`").Append(summary).Append("`\n\n");
            sb.Append("_Rest pose, edit mode: no physbone solve, no motion. `bodyGap` is joint centre to the nearest baked body vertex (sampling error about half an edge length); `bodySlack` = bodyGap − joint radius, negative = the chain's own collision sphere already overlaps the body standing still, which no collider fixes. `colliderSlack` = surface-to-surface gap to the nearest collider this chain references, negative = touching at rest (constant push: jitter or a hem kick). Surface `ndmf-proxy` = the composed preview with reactions applied; `scene` = raw instance, reactions at 0. `restContactCm=` and `insideBodyCm=` are the tokens a rule reads._\n\n");
            if (chainPrefix != null) sb.Append("chainPrefix=`").Append(chainPrefix).Append("`\n\n");

            sb.Append("## Chains\n\n| chain | joints | restContactCm | insideBodyCm | nearest collider | limits | colliders | root constraint |\n|---|---|---|---|---|---|---|---|\n");
            foreach (var r in rows)
            {
                var worst = r.Joints.OrderBy(j => j.ColliderSlack).FirstOrDefault();
                sb.Append("| `").Append(Cell(r.Key)).Append("` | ").Append(r.Joints.Count)
                  .Append(" | restContactCm=").Append(Cm(float.IsPositiveInfinity(r.MinColliderSlack) ? 0f : Mathf.Max(0f, -r.MinColliderSlack)))
                  .Append(" | insideBodyCm=").Append(Cm(float.IsPositiveInfinity(r.MinBodySlack) || float.IsNaN(r.MinBodySlack) ? 0f : Mathf.Max(0f, -r.MinBodySlack)))
                  .Append(" | ").Append(r.Joints.Count > 0 ? Cell(worst.NearestCollider) : "—")
                  .Append(" | ").Append(Cell(r.Limits)).Append(r.LateralLocked ? " **lateralLocked**" : "")
                  .Append(" | ").Append(Cell(r.Colliders)).Append(" | ").Append(Cell(r.Constraint)).Append(" |\n");
            }

            sb.Append("\n## Joints\n\n| chain | joint | depth | y | radius | bodyGap | bodySlack | colliderSlack | collider |\n|---|---|---|---|---|---|---|---|---|\n");
            foreach (var r in rows)
                foreach (var j in r.Joints)
                    sb.Append("| `").Append(Cell(r.Key)).Append("` | `").Append(Cell(j.Name)).Append("` | ").Append(j.Depth)
                      .Append(" | ").Append(F(root.transform.InverseTransformPoint(j.Pos).y)).Append(" | ").Append(Cm(j.Radius))
                      .Append(" | ").Append(float.IsNaN(j.BodyGap) ? "—" : Cm(j.BodyGap)).Append(" | ").Append(float.IsNaN(j.BodySlack) ? "—" : Cm(j.BodySlack))
                      .Append(" | ").Append(float.IsPositiveInfinity(j.ColliderSlack) ? "—" : Cm(j.ColliderSlack)).Append(" | ").Append(Cell(j.NearestCollider)).Append(" |\n");

            sb.Append("\n## Colliders\n\n_`endToEnd` is the measured extent: capsule `height` is the core segment and the rounded ends add 2×radius. `notReferencing` lists region chains this collider does not cover._\n\n| collider | root | shape | referencing | notReferencing |\n|---|---|---|---|---|\n");
            foreach (var c in colliders)
                sb.Append("| `").Append(Cell(c.Path)).Append("` | `").Append(Cell(c.RootName)).Append("` | ").Append(Cell(c.Shape))
                  .Append(" | ").Append(c.Referencing.Count == 0 ? "—" : Cell(string.Join(", ", c.Referencing)))
                  .Append(" | ").Append(c.NotReferencing.Count == 0 ? "—" : Cell(string.Join(", ", c.NotReferencing))).Append(" |\n");
            if (colliders.Count == 0) sb.Append("| — | — | — | — | — |\n");

            sb.Append("\n## Weights near each chain\n\n_Top skin-weight bones over the body and garment vertices within joint radius + ").Append(Cm(WeightBandMeters))
              .Append(" of the chain; share = summed weight / vertices counted. Body and garment riding different bones is the relative motion that clips._\n\n| chain | body verts | body bones | garment verts | garment bones |\n|---|---|---|---|---|\n");
            foreach (var r in rows)
                sb.Append("| `").Append(Cell(r.Key)).Append("` | ").Append(r.BodyNear).Append(" | ").Append(Cell(TopWeights(r.BodyWeights, r.BodyNear)))
                  .Append(" | ").Append(r.GarmentNear).Append(" | ").Append(Cell(TopWeights(r.GarmentWeights, r.GarmentNear))).Append(" |\n");

            var res = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "clearance_" + body.name, summary, sb.ToString(), ".md");
            Debug.Log(res);
            return res;
        }

        private static string TopWeights(Dictionary<string, float> acc, int n)
        {
            if (n == 0 || acc.Count == 0) return "—";
            return string.Join(", ", acc.OrderByDescending(k => k.Value).Take(4).Select(k => k.Key + "=" + (k.Value / n).ToString("F2", CultureInfo.InvariantCulture)));
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

        private static string Fail(string why)
        {
            var msg = "[ReportClearance] FAIL: " + why;
            Debug.LogError(msg);
            return msg;
        }

        // Play-mode clones suffix bone names with `$…`; strip so a key printed here joins a key printed there.
        internal static string Key(string name) { int i = name.IndexOf('$'); return i < 0 ? name : name.Substring(0, i); }

        private static GameObject ResolveDescendant(GameObject root, string spec)
        {
            var byPath = root.transform.Find(spec.Trim('/'));
            if (byPath != null) return byPath.gameObject;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == spec) return t.gameObject;
            return null;
        }

        internal static string RelPath(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            for (var p = t; p != null && p != root; p = p.parent) parts.Add(Key(p.name));
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static MethodInfo SafeGetMethod(Type type, string name, BindingFlags flags)
        {
            try { return type.GetMethod(name, flags); } catch { return null; }
        }

        private static string Cell(string s) => RunLogFormat.Cell(s);
        private static string F(float f) => f.ToString("F2", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
        internal static string Cm(float meters) => (meters * 100f).ToString("F1", CultureInfo.InvariantCulture) + "cm";
    }
}
