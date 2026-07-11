using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Scene-scoped INSPECTION gate: does a placed mergeable's armature actually FIT the base rig,
    /// BEFORE the seam bakes at build? The fit signal is mechanical and cheap — a per-bone world-space
    /// offset — and it catches misfits that survive a model-read render at every tier (fitting run-1
    /// G9/G12/G14): a normalized load-bearing root scale (G-T3), a mergeable authored for a swapped head.
    /// Renders are operator evidence; this is the agent-usable fit gate that runs first.
    ///
    /// Metric — worst-case vertex displacement. For each bone the mergeable's SkinnedMeshRenderers
    /// weight, take the maximum weight any single vertex places on it (<c>maxW</c>, aggregated per bone
    /// Transform across all the mergeable's SMRs), and score
    /// <c>effDisp = worldPositionDelta(mergeBone, baseBone) * maxW</c> — the first-order worst-case
    /// displacement of the vertex most dependent on that bone once the base drives it. Position-only is
    /// sufficient: a mis-scale propagates to descendant bone POSITIONS (a hierarchy), so it surfaces as
    /// the deltas the metric already measures; only an isolated weighted LEAF bone's own scale/rotation
    /// is blind (a stated limitation, not built).
    ///
    /// Matching is MA's OWN — <c>ModularAvatarMergeArmature.GetBonesMapping()</c> returns the
    /// authoritative (base, merge) pairs, already prefix/suffix-resolved and already limited to the
    /// bones that actually merge (the mergeable's new dynamic bones aren't in it). CheckSeam never
    /// reimplements MA's matching, so it can't drift from it. v1 scores MA MergeArmature seams only; a
    /// VRCFury ArmatureLink / BoneProxy / bare-prefab mergeable is REFUSED with routing (VRCFury exposes
    /// no mapping API — refusing beats guessing a mapping and emitting a false verdict).
    ///
    /// Verdict — PASS (all weighted bones within a scale-relative ε) / REVIEW (a UNIFORM offset with the
    /// mergeable root aligned to its drop — intrinsic authoring like a head-swap: possibly intentional,
    /// stop-and-ask, NON-passing) / FAIL (a DIFFERENTIAL offset — offsets disagree across the rig, e.g. a
    /// distance gradient from a proportion/scale mismatch; OR a uniform offset with the mergeable root
    /// moved off its identity drop). PASS is emitted only on a fit CheckSeam can certify; REVIEW and the
    /// refusal are both non-passing. Bad input / out-of-scope is a bare <c>[CheckSeam] FAIL: …</c> with a
    /// routing reason and no trailer (family discipline).
    ///
    /// INSPECTION ONLY — mutates no scene state, writes only its RunLog. NEVER throws: the one reflective
    /// hop (GetBonesMapping) is guarded and degrades to a loud refusal.
    /// </summary>
    [AgentTool]
    public static class CheckSeam
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;

        // ε is scale-relative: this fraction of the base Hips→Head span (so the gate is invariant to
        // non-normalized / child-scale rigs). Surfaced every run and overridable. ~2% clears the benign
        // band — lightly-weighted secondary bones (physbone-driven breast/skirt tips) reach ~4–5 mm effDisp
        // on a correct compose (measured); real misfits are 34–64 mm, so the margin is wide either side.
        private const float DefaultEpsilonFraction = 0.02f;
        // Floor under the scale-relative value: never gate tighter than the benign secondary band.
        private const float EpsilonFloorM = 0.006f;
        // Fallback ε when the Hips→Head span is unavailable (non-humanoid base / missing head bone).
        private const float FallbackEpsilonM = 0.008f;
        private const float RootRotToleranceDeg = 0.5f;
        private const int MaxOffenderLines = 8;

        private const string MaMergeArmatureTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";
        private const string MaBoneProxyTypeName = "nadena.dev.modular_avatar.core.ModularAvatarBoneProxy";
        private const string VrcfuryTypeName = "VF.Model.VRCFury";

        // ── Injectable seams (internal) ─────────────────────────────────────────────────────────────────
        // The MA type is absent from the SDK-only test venue and GetBonesMapping can't be constructed there,
        // so these two hops are seams a test swaps to drive the scoring/classification with real transforms +
        // weights but a faked mapping. Defaults ARE the real behaviour (validated live on the real corpus:
        // GetBonesMapping returns (base, merge) pairs). Same pattern as CheckAvatar's reflective seams.

        public enum SeamKind { None, MergeArmature, BoneProxy, VRCFury }
        public struct SeamInfo { public SeamKind Kind; public Component Component; }

        /// <summary>Classify the mergeable's attach seam (default <see cref="DefaultDetectSeam"/>).</summary>
        internal static Func<GameObject, SeamInfo> DetectSeam = DefaultDetectSeam;

        /// <summary>Resolve the (base, merge) bone correspondence for a MergeArmature component
        /// (default <see cref="DefaultResolveMapping"/> → <c>GetBonesMapping()</c>).</summary>
        internal static Func<Component, List<(Transform, Transform)>> ResolveMapping = DefaultResolveMapping;

        // ── Public API ────────────────────────────────────────────────────────────────────────────────

        /// <summary>Gate the fit of the placed mergeable at <paramref name="mergeableRoot"/> onto the
        /// avatar at <paramref name="baseRoot"/> (both scene handles: hierarchy path, else instance id,
        /// else name — mirrors CheckAvatar). Returns a one-line summary; a scored run ends with the
        /// RunLog path in-band (<c>… =&gt; PASS|REVIEW|FAIL | log=&lt;path&gt;</c>). Bad input or an
        /// out-of-scope mergeable is a bare <c>[CheckSeam] FAIL: &lt;reason + routing&gt;</c> (no trailer).
        /// <paramref name="epsilonFraction"/> overrides the scale-relative ε (fraction of Hips→Head).</summary>
        public static string Inspect(string baseRoot, string mergeableRoot, float epsilonFraction = DefaultEpsilonFraction)
        {
            var baseGO = Resolve(baseRoot);
            if (baseGO == null)
                return Refuse("base root '" + baseRoot + "' not found — tried hierarchy path, instance id, then name in the active scene");
            var mergeGO = Resolve(mergeableRoot);
            if (mergeGO == null)
                return Refuse("mergeable root '" + mergeableRoot + "' not found — tried hierarchy path, instance id, then name in the active scene");
            if (baseGO == mergeGO)
                return Refuse("base and mergeable resolved to the same object '" + baseGO.name + "'");

            var descriptor = baseGO.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                return Refuse("'" + baseRoot + "' has no VRCAvatarDescriptor — the base must be the avatar (descriptor) root");

            // Base humanoid rig → scale-relative ε (Hips→Head span) + the Hips anchor for the gradient span.
            var animator = baseGO.GetComponent<Animator>();
            Transform baseHips = null, baseHead = null;
            if (animator != null && animator.isHuman)
            {
                baseHips = animator.GetBoneTransform(HumanBodyBones.Hips);
                baseHead = animator.GetBoneTransform(HumanBodyBones.Head);
            }
            bool spanKnown = baseHips != null && baseHead != null;
            float span = spanKnown ? (baseHead.position - baseHips.position).magnitude : 0f;
            float eps = spanKnown ? Mathf.Max(epsilonFraction * span, EpsilonFloorM) : FallbackEpsilonM;

            // Scope gate: v1 scores MA MergeArmature only. Route everything else.
            var seam = DetectSeam(mergeGO);
            if (seam.Kind != SeamKind.MergeArmature)
                return Refuse(ScopeRoutingReason(seam.Kind, mergeGO));
            var merge = seam.Component;

            // MA's authoritative (base, merge) pairs. Guarded — the one throwing hop.
            List<(Transform baseBone, Transform mergeBone)> pairs;
            try { pairs = ResolveMapping(merge); }
            catch (Exception e)
            {
                return Refuse("MergeArmature.GetBonesMapping threw (" + e.GetType().Name + ": " + e.Message
                    + ") — cannot establish bone correspondence; check the seam's merge target");
            }
            if (pairs == null || pairs.Count == 0)
                return Refuse("MergeArmature on '" + PathOf(merge != null ? merge.gameObject : mergeGO) + "' maps no bones — its merge target did not resolve to the base rig (wrong base?); route to refit");

            // The mapping's base side must actually live under the passed base — else the seam targets a
            // DIFFERENT avatar (a two-avatar scene, or a wrong base handle) and a verdict "onto <base>" would
            // be confidently mislabeled, the one thing this gate exists to prevent. Verify both sides.
            foreach (var (b, m) in pairs)
            {
                if (b != null && !b.IsChildOf(baseGO.transform))
                    return Refuse("the seam's merge target resolves OUTSIDE the passed base '" + baseRoot + "' (bone '"
                        + b.name + "' is not under it) — wrong base handle, or the seam targets another avatar; pass the base the seam merges onto");
                if (m != null && !m.IsChildOf(mergeGO.transform))
                    return Refuse("a mapped mergeable bone ('" + m.name + "') is not under '" + mergeableRoot
                        + "' — the seam is wired across objects; pass the mergeable that carries the seam");
            }

            // maxW per mergeable bone Transform, aggregated across all the mergeable's SMRs.
            var maxW = MaxWeightPerBone(mergeGO, out string weightErr);
            if (weightErr != null) return Refuse(weightErr);

            // Score.
            var scored = new List<Scored>();
            int weightedMapped = 0;
            var mappedMergeBones = new HashSet<Transform>();
            foreach (var (b, m) in pairs)
            {
                if (b == null || m == null) continue;
                mappedMergeBones.Add(m);
                float w = maxW.TryGetValue(m, out var ww) ? ww : 0f;
                if (w <= 0f) continue;
                weightedMapped++;
                Vector3 delta = m.position - b.position;
                float effDisp = delta.magnitude * w;
                float dist = baseHips != null ? (b.position - baseHips.position).magnitude : 0f;
                scored.Add(new Scored { Bone = b.name, Delta = delta, MaxW = w, EffDisp = effDisp, DistFromHips = dist });
            }
            // Weighted bones the mergeable adds that MA does NOT map (new dynamic bones — informational).
            int weightedUnmapped = 0;
            foreach (var kv in maxW)
                if (kv.Value > 0f && !mappedMergeBones.Contains(kv.Key)) weightedUnmapped++;

            if (scored.Count == 0)
                return Refuse("no weighted bone of '" + mergeGO.name + "' corresponds to the base rig — the mergeable skins to bones that don't merge (wrong base / not an overlay); route to refit");

            scored.Sort((x, y) => y.EffDisp.CompareTo(x.EffDisp));
            var offenders = scored.FindAll(s => s.EffDisp > eps);

            string result;
            string shape;      // for the summary/offender diagnostic
            bool rootAligned = RootAligned(mergeGO.transform, baseGO.transform, eps);

            if (offenders.Count == 0)
            {
                result = "PASS"; shape = "coincident";
            }
            else
            {
                // Uniform vs differential, measured in WEIGHTED (effDisp) space — matching how offenders
                // crossed eps — and over the OFFENDING REGION, not the whole rig: a lone root-aligned
                // offender (or coincident Hips/Spine with an offset head cluster) is a uniform region →
                // REVIEW by design, not FAIL. (Whole-rig residual would wrongly flip the head-swap case.)
                Vector3 mean = Vector3.zero;
                foreach (var o in offenders) mean += o.Delta * o.MaxW;
                mean /= offenders.Count;
                float residualMax = 0f;
                foreach (var o in offenders) residualMax = Mathf.Max(residualMax, (o.Delta * o.MaxW - mean).magnitude);
                bool uniform = residualMax <= eps;

                if (!uniform) { result = "FAIL"; shape = "differential"; }
                else if (!rootAligned) { result = "FAIL"; shape = "uniform,root-moved"; }
                else { result = "REVIEW"; shape = "uniform,root-aligned"; }
            }

            return Emit(baseGO, mergeGO, merge, pairs.Count, weightedMapped, weightedUnmapped, eps, spanKnown,
                        epsilonFraction, scored, offenders, result, shape, rootAligned);
        }

        // ── Scoring helpers ───────────────────────────────────────────────────────────────────────────

        private struct Scored
        {
            public string Bone;
            public Vector3 Delta;
            public float MaxW;
            public float EffDisp;
            public float DistFromHips;
        }

        // Max weight any single vertex places on each bone Transform, across every SMR under the
        // mergeable. Keyed by Transform (SMR.bones[] entries ARE the scene bone Transforms, the same
        // objects GetBonesMapping returns). NOTE: Unity fills unskinned verts with boneIndex0=0/weight0=1,
        // which can inflate an SMR's bones[0] maxW — a conservative over-state (fails safe: it can only
        // raise a bone's effDisp, never hide one), and VRChat meshes are fully skinned in practice.
        private static Dictionary<Transform, float> MaxWeightPerBone(GameObject mergeGO, out string error)
        {
            error = null;
            var maxW = new Dictionary<Transform, float>();
            foreach (var smr in mergeGO.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                var bones = smr.bones;
                if (bones == null || bones.Length == 0) continue;
                BoneWeight[] bw;
                try { bw = mesh.boneWeights; } // throws if the mesh is Read/Write-disabled — honour never-throw
                catch (Exception e)
                {
                    error = "could not read skin weights for '" + PathOf(smr.gameObject) + "' (" + e.GetType().Name
                          + ") — mesh Read/Write may be disabled; cannot score fit";
                    return null;
                }
                foreach (var w in bw)
                {
                    Accumulate(maxW, bones, w.boneIndex0, w.weight0);
                    Accumulate(maxW, bones, w.boneIndex1, w.weight1);
                    Accumulate(maxW, bones, w.boneIndex2, w.weight2);
                    Accumulate(maxW, bones, w.boneIndex3, w.weight3);
                }
            }
            return maxW;
        }

        private static void Accumulate(Dictionary<Transform, float> maxW, Transform[] bones, int idx, float w)
        {
            if (w <= 0f || idx < 0 || idx >= bones.Length) return;
            var t = bones[idx];
            if (t == null) return;
            if (!maxW.TryGetValue(t, out var cur) || w > cur) maxW[t] = w;
        }

        // The drop convention (compose-mergeable step 2) places the mergeable at identity LOCAL transform
        // under the avatar root, i.e. its world pose COINCIDES with the avatar root's. Measured in WORLD
        // space against the avatar root (not parent-local): parent-local would read "aligned" for a
        // mergeable tucked under an intermediate offset/rotated container, and would mismatch the
        // world-space eps under a non-unit avatar scale. A root shifted off its drop is a wrong drop;
        // combined with a uniform bone offset it escalates REVIEW→FAIL. Scale is not checked here — a
        // mis-scale surfaces as the bone deltas.
        private static bool RootAligned(Transform mergeRoot, Transform avatarRoot, float eps)
        {
            return (mergeRoot.position - avatarRoot.position).magnitude <= eps
                && Quaternion.Angle(mergeRoot.rotation, avatarRoot.rotation) <= RootRotToleranceDeg;
        }

        // ── MA reflection (guarded) ─────────────────────────────────────────────────────────────────────

        // Real seam classification: MergeArmature wins; else name a VRCFury / BoneProxy attach for routing.
        private static SeamInfo DefaultDetectSeam(GameObject mergeGO)
        {
            Component boneProxy = null, vrcfury = null;
            foreach (var c in mergeGO.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var n = c.GetType().FullName;
                if (n == MaMergeArmatureTypeName) return new SeamInfo { Kind = SeamKind.MergeArmature, Component = c };
                if (n == MaBoneProxyTypeName && boneProxy == null) boneProxy = c;
                else if (n == VrcfuryTypeName && vrcfury == null) vrcfury = c;
            }
            if (vrcfury != null) return new SeamInfo { Kind = SeamKind.VRCFury, Component = vrcfury };
            if (boneProxy != null) return new SeamInfo { Kind = SeamKind.BoneProxy, Component = boneProxy };
            return new SeamInfo { Kind = SeamKind.None, Component = null };
        }

        // ModularAvatarMergeArmature.GetBonesMapping() → List<(Transform base, Transform merge)>.
        // Verified live: Item1 is the base-side bone, Item2 the mergeable-side bone.
        private static List<(Transform, Transform)> DefaultResolveMapping(Component merge)
        {
            var mi = merge.GetType().GetMethod("GetBonesMapping", BindingFlags.Public | BindingFlags.Instance);
            if (mi == null) return null;
            var raw = mi.Invoke(merge, null) as IEnumerable;
            if (raw == null) return null;
            var outp = new List<(Transform, Transform)>();
            foreach (var item in raw)
            {
                var t = item.GetType();
                var f1 = t.GetField("Item1"); var f2 = t.GetField("Item2");
                if (f1 == null || f2 == null) continue;
                var a = f1.GetValue(item) as Transform;
                var b = f2.GetValue(item) as Transform;
                outp.Add((a, b));
            }
            return outp;
        }

        // Why the mergeable is out of scope, with the route out (family: a bare FAIL names the fix).
        private static string ScopeRoutingReason(SeamKind kind, GameObject mergeGO)
        {
            switch (kind)
            {
                case SeamKind.VRCFury:
                    return "'" + mergeGO.name + "' uses a VRCFury seam — CheckSeam v1 scores MA MergeArmature seams; "
                         + "align the armature in edit mode and verify by eye (VRCFury ArmatureLink support is a fast-follow)";
                case SeamKind.BoneProxy:
                    return "'" + mergeGO.name + "' attaches via MA BoneProxy (single-bone attach, no armature merge to score) — check the proxy target, not rig fit";
                default:
                    return "'" + mergeGO.name + "' has no MA MergeArmature seam — nothing to score; a bare prefab routes to own-mergeable";
            }
        }

        // ── Output ────────────────────────────────────────────────────────────────────────────────────

        private static string Emit(GameObject baseGO, GameObject mergeGO, Component merge,
            int mappedCount, int weightedMapped, int weightedUnmapped, float eps, bool spanKnown,
            float epsFraction, List<Scored> scored, List<Scored> offenders, string result, string shape,
            bool rootAligned)
        {
            float maxEff = scored.Count > 0 ? scored[0].EffDisp : 0f;
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckSeam] {0} onto {1}: weightedMapped={2} maxEff={3:F1}mm eps={4:F1}mm shape={5} => {6}",
                mergeGO.name, baseGO.name, weightedMapped, maxEff * 1000f, eps * 1000f, shape, result);

            var sb = new StringBuilder();
            sb.Append("# CheckSeam: ").Append(mergeGO.name).Append(" onto ").Append(baseGO.name).Append('\n');
            sb.Append("mergeable: `").Append(PathOf(mergeGO)).Append("`  \n");
            sb.Append("seam: MA MergeArmature @ `").Append(PathOf(merge != null ? merge.gameObject : mergeGO)).Append("`  \n\n");
            sb.Append(summary.Substring("[CheckSeam] ".Length)).Append('\n');

            sb.Append("\n## Counts\n\n");
            sb.Append("- mapped bones (MA GetBonesMapping): ").Append(mappedCount).Append('\n');
            sb.Append("- weighted + mapped (scored): ").Append(weightedMapped).Append('\n');
            sb.Append("- weighted + unmapped (mergeable's own new dynamic bones — informational): ").Append(weightedUnmapped).Append('\n');
            sb.Append("- ε: ").Append((eps * 1000f).ToString("F2", CultureInfo.InvariantCulture)).Append(" mm (")
              .Append(spanKnown ? epsFraction.ToString("0.###", CultureInfo.InvariantCulture) + " of base Hips→Head span" : "fallback — base Hips/Head unavailable")
              .Append(")\n");
            sb.Append("- root-align: ").Append(rootAligned ? "aligned (mergeable at ~identity local drop)" : "OFFSET (mergeable root moved off its identity drop)").Append('\n');

            sb.Append("\n## Offenders (effDisp > ε, worst first)\n\n");
            if (offenders.Count == 0) sb.Append("_(none — all weighted bones within ε)_\n");
            else
            {
                int shown = 0;
                foreach (var o in offenders)
                {
                    if (shown++ >= MaxOffenderLines) { sb.Append("- … ").Append(offenders.Count - MaxOffenderLines).Append(" more\n"); break; }
                    sb.Append(string.Format(CultureInfo.InvariantCulture,
                        "- **bone-offset** `{0}` (effDisp={1:F1}mm, delta={2:F1}mm, maxW={3:F2}, distFromHips={4:F0}mm)\n",
                        o.Bone, o.EffDisp * 1000f, o.Delta.magnitude * 1000f, o.MaxW, o.DistFromHips * 1000f));
                }
                // Shape line: uniform (a rigid shift) vs differential (a gradient/scatter).
                Scored lo = offenders[0], hi = offenders[0];
                foreach (var o in offenders) { if (o.DistFromHips < lo.DistFromHips) lo = o; if (o.DistFromHips > hi.DistFromHips) hi = o; }
                sb.Append("- **shape** ").Append(shape.StartsWith("uniform") ? "uniform" : "differential")
                  .Append(string.Format(CultureInfo.InvariantCulture, " (span {0} {1:F1}mm → {2} {3:F1}mm by distance from Hips)\n",
                      lo.Bone, lo.EffDisp * 1000f, hi.Bone, hi.EffDisp * 1000f));
            }

            sb.Append("\n## Verdict\n\n");
            sb.Append(VerdictNote(result)).Append('\n');

            var res = RunLogFormat.WriteRunLog(RunLogDir, "checkseam_" + mergeGO.name + "_onto_" + baseGO.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogWarning(res);
            return res;
        }

        private static string VerdictNote(string result)
        {
            switch (result)
            {
                case "PASS": return "PASS — every weighted bone lands within ε; the seam fits.";
                case "REVIEW": return "REVIEW (non-passing, stop-and-ask) — a uniform offset with the mergeable root aligned: "
                    + "the offset is intrinsic to the mergeable's authoring (e.g. authored for a different head — a head-swap). "
                    + "Possibly intentional; the operator decides. Do NOT proceed on this without confirmation.";
                default: return "FAIL — the seam does not fit: either the offsets disagree across the rig (a gradient/scatter — "
                    + "proportion or scale not matched; a correct compose pre-scales the armature so it has none), or a uniform "
                    + "offset with the mergeable root moved off its identity drop (re-drop at identity / align the armature root).";
            }
        }

        // ── Bad-input / out-of-scope refusal (bare FAIL, no trailer) ──────────────────────────────────

        private static string Refuse(string why)
        {
            string err = "[CheckSeam] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        // ── Scene resolver (path → instance id → name; mirrors CheckAvatar.Resolve, kept local) ────────

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

            foreach (var rootGo in SceneManager.GetActiveScene().GetRootGameObjects())
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
