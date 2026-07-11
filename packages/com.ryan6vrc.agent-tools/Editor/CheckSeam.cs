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
    /// Correspondence is the frameworks' OWN, unioned across EVERY seam component under the mergeable (a
    /// prefab may carry several — no rule forbids it): each MA <c>MergeArmature.GetBonesMapping()</c>
    /// (authoritative, prefix/suffix-resolved) and each VRCFury <c>ArmatureLink</c> via
    /// <c>ArmatureLinkService.GetLinks()</c> (its resolved prop→avatar pairs — note the tuple is FLIPPED
    /// vs MA). CheckSeam never reimplements either matcher, so it can't drift. Both families bind bones
    /// name→name and neither reconciles rest offsets *unless a snap/scale flag is set* — which is the one
    /// regime where the build moves the bones out from under this edit-time measurement. A VRCFury link
    /// with any align/snap flag, <c>keepBoneOffsets=No</c>, or a non-unit scaling factor tags its bones
    /// "will snap"; a scored bone that will snap forces REVIEW (edit-time fit ≠ shipped fit). A mergeable
    /// with no armature-merge seam (BoneProxy single-attach, bare prefab) is REFUSED with routing.
    ///
    /// Verdict — PASS (all weighted bones within a scale-relative ε) / REVIEW (a UNIFORM offset with the
    /// mergeable root aligned to its drop — intrinsic authoring like a head-swap; or a will-snap bone —
    /// possibly intentional, stop-and-ask, NON-passing) / FAIL (a DIFFERENTIAL offset — offsets disagree
    /// across the rig, e.g. a distance gradient from a proportion/scale mismatch; OR a uniform offset with
    /// the mergeable root moved off its identity drop). PASS is emitted only on a fit CheckSeam can
    /// certify; REVIEW and the refusal are both non-passing. Bad input / out-of-scope is a bare
    /// <c>[CheckSeam] FAIL: …</c> with a routing reason and no trailer (family discipline).
    ///
    /// INSPECTION ONLY — mutates no scene state, writes only its RunLog. NEVER throws: every reflective
    /// hop is guarded and degrades to a loud refusal.
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
        private const float EpsilonFloorM = 0.006f;    // never gate tighter than the benign secondary band
        private const float FallbackEpsilonM = 0.008f; // when the Hips→Head span is unavailable
        private const float RootRotToleranceDeg = 0.5f;
        private const float ScaleUnitTolerance = 0.001f;
        private const int MaxOffenderLines = 8;

        private const string MaMergeArmatureTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature";
        private const string MaBoneProxyTypeName = "nadena.dev.modular_avatar.core.ModularAvatarBoneProxy";
        private const string VrcfuryTypeName = "VF.Model.VRCFury";
        private const string VrcfArmatureLinkTypeName = "VF.Model.Feature.ArmatureLink";

        // ── Injectable seam (internal) ──────────────────────────────────────────────────────────────────
        // MA/VRCFury are absent from the SDK-only test venue and their mappings can't be constructed there,
        // so the whole seam-resolution hop is a seam a test swaps to drive scoring/classification with real
        // transforms + weights but injected correspondence. The default IS the real behaviour, validated
        // live on the corpus (MA GetBonesMapping + VRCFury GetLinks both recover the true offsets). Same
        // pattern as CheckAvatar's reflective seams.

        public enum SeamKind { None, MergeArmature, VRCFury, Mixed, BoneProxy }
        public struct SeamBone { public Transform Base; public Transform Merge; public bool WillSnap; }
        public struct Seam
        {
            public SeamKind Kind;
            public List<SeamBone> Bones; // unioned across all seam components; base-side + merge-side + will-snap
            public int LinkCount;        // seam components unioned (report only)
            public string RefuseNote;    // non-null ⇒ not a scorable overlay; bare-FAIL with this routing reason
        }

        /// <summary>Resolve the mergeable's armature-merge correspondence onto the base
        /// (default <see cref="DefaultResolveSeam"/>: union every MA MergeArmature + VRCFury ArmatureLink).</summary>
        internal static Func<GameObject, GameObject, Seam> ResolveSeam = DefaultResolveSeam;

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

            // Resolve every seam component (MA + VRCFury) into one unioned correspondence. Guarded.
            Seam seam;
            try { seam = ResolveSeam(baseGO, mergeGO); }
            catch (Exception e)
            {
                return Refuse("seam resolution threw (" + e.GetType().Name + ": " + e.Message
                    + ") — MA/VRCFury API drift; cannot establish bone correspondence");
            }
            if (seam.RefuseNote != null) return Refuse(seam.RefuseNote);
            var bones = seam.Bones ?? new List<SeamBone>();
            if (bones.Count == 0)
                return Refuse("the mergeable's seam maps no bones — its merge target did not resolve to the base rig (wrong base?); route to refit");

            // The mapping's base side must live under the passed base, and merge side under the passed
            // mergeable — else the seam targets a DIFFERENT avatar (two-avatar scene / wrong base handle)
            // and a verdict "onto <base>" would be confidently mislabeled, the one thing this gate prevents.
            foreach (var sb2 in bones)
            {
                if (sb2.Base != null && !sb2.Base.IsChildOf(baseGO.transform))
                    return Refuse("the seam's merge target resolves OUTSIDE the passed base '" + baseRoot + "' (bone '"
                        + sb2.Base.name + "' is not under it) — wrong base handle, or the seam targets another avatar; pass the base the seam merges onto");
                if (sb2.Merge != null && !sb2.Merge.IsChildOf(mergeGO.transform))
                    return Refuse("a mapped mergeable bone ('" + sb2.Merge.name + "') is not under '" + mergeableRoot
                        + "' — the seam is wired across objects; pass the mergeable that carries the seam");
            }

            // maxW per mergeable bone Transform, aggregated across all the mergeable's SMRs.
            var maxW = MaxWeightPerBone(mergeGO, out string weightErr);
            if (weightErr != null) return Refuse(weightErr);

            // Score each weighted, mapped bone. A bone whose link WILL SNAP at build has an invalid
            // edit-time delta (the build zeroes it) — count it separately; any scored will-snap bone forces
            // REVIEW (edit-time fit is not the shipped fit).
            var scored = new List<Scored>();
            int weightedMapped = 0, willSnapScored = 0;
            var mappedMergeBones = new HashSet<Transform>();
            foreach (var sb2 in bones)
            {
                if (sb2.Base == null || sb2.Merge == null) continue;
                mappedMergeBones.Add(sb2.Merge);
                float w = maxW.TryGetValue(sb2.Merge, out var ww) ? ww : 0f;
                if (w <= 0f) continue;
                weightedMapped++;
                if (sb2.WillSnap) { willSnapScored++; continue; } // build moves it — don't score its edit-time delta
                Vector3 delta = sb2.Merge.position - sb2.Base.position;
                float effDisp = delta.magnitude * w;
                float dist = baseHips != null ? (sb2.Base.position - baseHips.position).magnitude : 0f;
                scored.Add(new Scored { Bone = sb2.Base.name, Delta = delta, MaxW = w, EffDisp = effDisp, DistFromHips = dist });
            }
            int weightedUnmapped = 0; // the mergeable's own new dynamic bones (not in any mapping) — informational
            foreach (var kv in maxW)
                if (kv.Value > 0f && !mappedMergeBones.Contains(kv.Key)) weightedUnmapped++;

            if (weightedMapped == 0)
                return Refuse("no weighted bone of '" + mergeGO.name + "' corresponds to the base rig — the mergeable skins to bones that don't merge (wrong base / not an overlay); route to refit");

            scored.Sort((x, y) => y.EffDisp.CompareTo(x.EffDisp));
            var offenders = scored.FindAll(s => s.EffDisp > eps);
            bool rootAligned = RootAligned(mergeGO.transform, baseGO.transform, eps);

            string result, shape;
            if (willSnapScored > 0)
            {
                // A snap/scale flag will move weighted bones at build — the edit-time delta isn't the
                // shipped fit, so this gate can't certify it. Non-passing, operator decides.
                result = "REVIEW"; shape = "vrcfury-snap";
            }
            else if (offenders.Count == 0)
            {
                result = "PASS"; shape = "coincident";
            }
            else
            {
                // Uniform vs differential, in WEIGHTED (effDisp) space — matching how offenders crossed eps —
                // over the OFFENDING REGION, not the whole rig: a lone root-aligned offender (or coincident
                // Hips/Spine with an offset head cluster) is a uniform region → REVIEW by design, not FAIL.
                // (Whole-rig residual would wrongly flip the head-swap case.)
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

            return Emit(baseGO, mergeGO, seam, weightedMapped, weightedUnmapped, willSnapScored, eps, spanKnown,
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

        // Max weight any single vertex places on each bone Transform, across every SMR under the mergeable.
        // Keyed by Transform (SMR.bones[] entries ARE the scene bone Transforms the mappings return). NOTE:
        // Unity fills unskinned verts with boneIndex0=0/weight0=1, which can inflate an SMR's bones[0] maxW —
        // a conservative over-state (fails safe: it can only raise a bone's effDisp, never hide one), and
        // VRChat meshes are fully skinned in practice.
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

        // ── Seam resolution (MA + VRCFury, unioned; guarded) ─────────────────────────────────────────────

        // Union EVERY seam component under the mergeable — a prefab may carry several MergeArmatures and/or
        // ArmatureLinks. MA pairs never snap; VRCFury pairs carry their link's will-snap decision. If no
        // armature-merge seam is found, route (BoneProxy single-attach / bare prefab). On any reflective
        // drift, return a RefuseNote (fail-loud) rather than a guessed mapping.
        private static Seam DefaultResolveSeam(GameObject baseGO, GameObject mergeGO)
        {
            var bones = new List<SeamBone>();
            int links = 0; bool haveMa = false, haveVrcf = false, haveBoneProxy = false;

            foreach (var c in mergeGO.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var n = c.GetType().FullName;
                if (n == MaMergeArmatureTypeName)
                {
                    var pairs = MaBonesMapping(c);
                    if (pairs == null)
                        return new Seam { RefuseNote = "MA MergeArmature on '" + PathOf(c.gameObject) + "' — GetBonesMapping did not resolve (API drift); cannot score" };
                    haveMa = true; links++;
                    foreach (var (b, m) in pairs) bones.Add(new SeamBone { Base = b, Merge = m, WillSnap = false });
                }
                else if (n == MaBoneProxyTypeName) haveBoneProxy = true;
                else if (n == VrcfuryTypeName)
                {
                    var content = c.GetType().GetField("content")?.GetValue(c);
                    if (content == null || content.GetType().FullName != VrcfArmatureLinkTypeName) continue; // e.g. FullController — not an armature seam
                    var res = VrcfLinkBones(content, baseGO, out bool willSnap);
                    if (res == null)
                        return new Seam { RefuseNote = "VRCFury ArmatureLink on '" + PathOf(c.gameObject) + "' — GetLinks did not resolve (VRCFury API drift); cannot score" };
                    haveVrcf = true; links++;
                    foreach (var (b, m) in res) bones.Add(new SeamBone { Base = b, Merge = m, WillSnap = willSnap });
                }
            }

            if (!haveMa && !haveVrcf)
            {
                if (haveBoneProxy)
                    return new Seam { RefuseNote = "'" + mergeGO.name + "' attaches via MA BoneProxy (single-bone attach, no armature merge to score) — check the proxy target, not rig fit" };
                return new Seam { RefuseNote = "'" + mergeGO.name + "' has no MA MergeArmature or VRCFury ArmatureLink seam — nothing to score; a bare prefab routes to own-mergeable" };
            }
            var kind = haveMa && haveVrcf ? SeamKind.Mixed : (haveMa ? SeamKind.MergeArmature : SeamKind.VRCFury);
            return new Seam { Kind = kind, Bones = bones, LinkCount = links };
        }

        // ModularAvatarMergeArmature.GetBonesMapping() → (base, merge) pairs. Verified live: Item1=base.
        private static List<(Transform, Transform)> MaBonesMapping(Component merge)
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
                if (f1 == null || f2 == null) return null;
                outp.Add((f1.GetValue(item) as Transform, f2.GetValue(item) as Transform));
            }
            return outp;
        }

        // VRCFury ArmatureLink → (base, merge) pairs via ArmatureLinkService.GetLinks (its resolved
        // prop→avatar correspondence). GetLinks returns (Item1=prop/merge, Item2=avatar/base) — FLIPPED vs
        // MA, so we swap. willSnap: the build moves these bones when an align flag, keepBoneOffsets=No, or a
        // non-unit scaling factor is set (ArmatureLinkService snaps worldPosition/Rotation/Scale) — in that
        // regime the edit-time delta isn't the shipped fit. Returns null on reflective drift (→ refuse).
        private static List<(Transform, Transform)> VrcfLinkBones(object model, GameObject baseGO, out bool willSnap)
        {
            willSnap = false;
            if (!EnsureVrcfReflection()) return null;
            try
            {
                var avatarVfgo = _vfToVfgo.Invoke(null, new object[] { baseGO.transform });
                var links = _vfGetLinks.Invoke(null, new object[] { model, avatarVfgo });
                if (links == null) return null;

                willSnap = VrcfWillSnap(model, links);

                var mb = _vfMergeBonesField.GetValue(links) as IEnumerable;
                if (mb == null) return null;
                var outp = new List<(Transform, Transform)>();
                foreach (var item in mb)
                {
                    var t = item.GetType();
                    var prop = _vfToTransform.Invoke(null, new[] { t.GetField("Item1").GetValue(item) }) as Transform;
                    var avatar = _vfToTransform.Invoke(null, new[] { t.GetField("Item2").GetValue(item) }) as Transform;
                    outp.Add((avatar, prop)); // (base, merge) — flip GetLinks' (prop, avatar)
                }
                return outp;
            }
            catch { return null; }
        }

        // A link snaps if any align flag is set, keepBoneOffsets resolves to No, or the resolved scaling
        // factor is non-unit. Reads the model's own resolved fields; unknown/absent fields fail toward
        // "not snapping" only for the align bools (their absence means an older model that can't snap that
        // axis), but an unreadable keepBoneOffsets/scale is treated conservatively as snapping.
        private static bool VrcfWillSnap(object model, object links)
        {
            var mt = model.GetType();
            if (ReadBool(mt, model, "alignPosition") || ReadBool(mt, model, "alignRotation")
                || ReadBool(mt, model, "alignScale") || ReadBool(mt, model, "forceOneWorldScale"))
                return true;
            // keepBoneOffsets2 (enum: Auto/Yes/No). Auto/Yes keep the offset (verified: Auto ships offsets);
            // No snaps. A missing field ⇒ older model without the snap-by-No path ⇒ not snapping.
            var kbo = mt.GetField("keepBoneOffsets2")?.GetValue(model);
            if (kbo != null && string.Equals(kbo.ToString(), "No", StringComparison.OrdinalIgnoreCase)) return true;
            // Non-unit resolved scaling factor (props authored at 10×/100×): edit-time positions are pre-scale.
            try
            {
                var sf = _vfGetScalingFactor.Invoke(null, new object[] { model, links });
                if (sf != null)
                {
                    var st = sf.GetType();
                    foreach (var fn in new[] { "Item1", "Item2", "Item3" })
                    {
                        var f = st.GetField(fn);
                        if (f != null && f.GetValue(sf) is float v && Mathf.Abs(v - 1f) > ScaleUnitTolerance) return true;
                    }
                }
            }
            catch { return true; } // can't resolve scale ⇒ don't certify
            return false;
        }

        private static bool ReadBool(Type t, object obj, string field)
        {
            var f = t.GetField(field);
            return f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(obj);
        }

        // Lazily reflected VRCFury API (VFGameObject implicit conversions + ArmatureLinkService statics).
        private static bool _vfAttempted, _vfOk;
        private static MethodInfo _vfToVfgo, _vfToTransform, _vfGetLinks, _vfGetScalingFactor;
        private static FieldInfo _vfMergeBonesField;
        private static bool EnsureVrcfReflection()
        {
            if (_vfAttempted) return _vfOk;
            _vfAttempted = true;
            try
            {
                Type vfgo = null, svc = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts; try { ts = a.GetTypes(); } catch { continue; }
                    foreach (var t in ts)
                    {
                        if (t.FullName == "VF.Utils.VFGameObject") vfgo = t;
                        else if (t.FullName == "VF.Service.ArmatureLinkService") svc = t;
                    }
                    if (vfgo != null && svc != null) break;
                }
                if (vfgo == null || svc == null) return _vfOk = false;
                foreach (var m in vfgo.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "op_Implicit") continue;
                    var p = m.GetParameters()[0].ParameterType;
                    if (p == typeof(Transform) && m.ReturnType == vfgo) _vfToVfgo = m;
                    else if (p == vfgo && m.ReturnType == typeof(Transform)) _vfToTransform = m;
                }
                _vfGetLinks = svc.GetMethod("GetLinks", BindingFlags.Public | BindingFlags.Static);
                _vfGetScalingFactor = svc.GetMethod("GetScalingFactor", BindingFlags.Public | BindingFlags.Static);
                _vfMergeBonesField = _vfGetLinks?.ReturnType.GetField("mergeBones");
                _vfOk = _vfToVfgo != null && _vfToTransform != null && _vfGetLinks != null
                     && _vfGetScalingFactor != null && _vfMergeBonesField != null;
            }
            catch { _vfOk = false; }
            return _vfOk;
        }

        // ── Output ────────────────────────────────────────────────────────────────────────────────────

        private static string Emit(GameObject baseGO, GameObject mergeGO, Seam seam,
            int weightedMapped, int weightedUnmapped, int willSnapScored, float eps, bool spanKnown,
            float epsFraction, List<Scored> scored, List<Scored> offenders, string result, string shape,
            bool rootAligned)
        {
            float maxEff = scored.Count > 0 ? scored[0].EffDisp : 0f;
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckSeam] {0} onto {1}: seam={2}({3}) weightedMapped={4} maxEff={5:F1}mm eps={6:F1}mm shape={7} => {8}",
                mergeGO.name, baseGO.name, seam.Kind, seam.LinkCount, weightedMapped, maxEff * 1000f, eps * 1000f, shape, result);

            var sb = new StringBuilder();
            sb.Append("# CheckSeam: ").Append(mergeGO.name).Append(" onto ").Append(baseGO.name).Append('\n');
            sb.Append("mergeable: `").Append(PathOf(mergeGO)).Append("`  \n");
            sb.Append("seam: ").Append(seam.Kind).Append(" (").Append(seam.LinkCount).Append(" component(s))  \n\n");
            sb.Append(summary.Substring("[CheckSeam] ".Length)).Append('\n');

            sb.Append("\n## Counts\n\n");
            sb.Append("- weighted + mapped (scored): ").Append(weightedMapped).Append('\n');
            sb.Append("- weighted + will-snap at build (VRCFury; not scored): ").Append(willSnapScored).Append('\n');
            sb.Append("- weighted + unmapped (mergeable's own new dynamic bones — informational): ").Append(weightedUnmapped).Append('\n');
            sb.Append("- ε: ").Append((eps * 1000f).ToString("F2", CultureInfo.InvariantCulture)).Append(" mm (")
              .Append(spanKnown ? epsFraction.ToString("0.###", CultureInfo.InvariantCulture) + " of base Hips→Head span" : "fallback — base Hips/Head unavailable")
              .Append(")\n");
            sb.Append("- root-align: ").Append(rootAligned ? "aligned (mergeable at ~identity local drop)" : "OFFSET (mergeable root moved off its identity drop)").Append('\n');

            sb.Append("\n## Offenders (effDisp > ε, worst first)\n\n");
            if (offenders.Count == 0) sb.Append("_(none — all scored weighted bones within ε)_\n");
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
                Scored lo = offenders[0], hi = offenders[0];
                foreach (var o in offenders) { if (o.DistFromHips < lo.DistFromHips) lo = o; if (o.DistFromHips > hi.DistFromHips) hi = o; }
                sb.Append("- **shape** ").Append(shape.StartsWith("uniform") ? "uniform" : "differential")
                  .Append(string.Format(CultureInfo.InvariantCulture, " (span {0} {1:F1}mm → {2} {3:F1}mm by distance from Hips)\n",
                      lo.Bone, lo.EffDisp * 1000f, hi.Bone, hi.EffDisp * 1000f));
            }

            sb.Append("\n## Verdict\n\n");
            sb.Append(VerdictNote(result, shape)).Append('\n');

            var res = RunLogFormat.WriteRunLog(RunLogDir, "checkseam_" + mergeGO.name + "_onto_" + baseGO.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogWarning(res);
            return res;
        }

        private static string VerdictNote(string result, string shape)
        {
            switch (result)
            {
                case "PASS": return "PASS — every scored weighted bone lands within ε; the seam fits.";
                case "REVIEW":
                    if (shape == "vrcfury-snap")
                        return "REVIEW (non-passing, stop-and-ask) — a VRCFury align/snap flag (or non-unit scaling) will move "
                             + "weighted bones at build, so the edit-time fit is not the shipped fit and cannot be certified here. "
                             + "The operator decides; verify in play mode.";
                    return "REVIEW (non-passing, stop-and-ask) — a uniform offset with the mergeable root aligned: "
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
