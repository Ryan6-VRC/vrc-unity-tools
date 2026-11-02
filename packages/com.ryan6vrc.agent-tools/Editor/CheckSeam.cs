using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Scene-scoped fit gate: does a mergeable's humanoid skeleton coincide with a base's, before any render —
    /// <c>PASS</c> / <c>NOT-PASS</c> / bare <c>REFUSE</c>. <see cref="Run"/> pairs through the seam's own
    /// mapping, <see cref="CheckBare"/> by bone name; both share one gate.
    /// Contract: docs/unity-tools.md.
    /// </summary>
    [AgentTool]
    public static partial class CheckSeam
    {
        // ── Data the pure core consumes ───────────────────────────────────────────────────────────────
        internal struct BonePair { public Transform Base; public Transform Merge; }

        internal class SeamResolution
        {
            public List<BonePair> Pairs = new List<BonePair>(); // union of MA + VRCFury, base↔merge
            public string ScaleBakeReason;                      // non-null ⇒ VRCFury applies scale at bake ⇒ REFUSE
            public string ReflectError;                         // non-null ⇒ genuine API drift (tool broken vs pkg) ⇒ REFUSE (error)
            public string UnresolvableReason;                   // non-null ⇒ seam exists but won't resolve onto THIS base ⇒ REFUSE (abstain)
        }

        internal class HumanoidMap
        {
            public HashSet<Transform> Bones = new HashSet<Transform>(); // empty ⇒ base not humanoid ⇒ REFUSE
            public float SpanMm;                                        // Hips→Head world distance, mm (for ε)
        }

        // ── Injectable seams (default = real reflection; tests swap fakes) ─────────────────────────────
        internal static Func<GameObject, GameObject, SeamResolution> ResolveSeam = DefaultResolveSeam;
        internal static Func<GameObject, HumanoidMap> ResolveHumanoid = DefaultResolveHumanoid;

        // Emit/REFUSE label per door, so a RunLog or a refusal line is never mistaken for the other door's.
        private const string SeamLabel = "CheckSeam";
        private const string BareLabel = "CheckSeam:bare";

        // The seam door's ε, declared once: Emit prints the formula from these, so no literal goes there.
        private const float EpsFloorMm = 0.5f;
        private const float EpsSpanFraction = 0.003f;
        // Bare-door tolerance floor: BareOffsetFormat's resolution. See the guard in CheckBare.
        private const float MinToleranceMm = 0.0001f;
        private const string BareOffsetFormat = "F4";
        private const string SeamOffsetFormat = "F1";
        private const string SeamBandFormat = "F2";

        // ── Doors ─────────────────────────────────────────────────────────────────────────────────────

        public static string Run(string baseRoot, string mergeableRoot)
        {
            var baseHandle = SceneHandle.Resolve(baseRoot);
            if (!baseHandle.Ok) return RefuseMisuse("baseRoot: " + baseHandle.Refusal);
            var baseGO = baseHandle.Object;
            var mergeHandle = SceneHandle.Resolve(mergeableRoot);
            if (!mergeHandle.Ok) return RefuseMisuse("mergeableRoot: " + mergeHandle.Refusal);
            var mergeGO = mergeHandle.Object;

            var human = ResolveHumanoid(baseGO);
            if (human.Bones.Count == 0)
                return RefuseAbstain("base '" + baseRoot + "' has no humanoid Avatar — cannot certify fit (clothes-on-a-body is the domain)");

            var seam = ResolveSeam(baseGO, mergeGO);
            if (seam.ReflectError != null) return RefuseMisuse("seam resolution failed: " + seam.ReflectError);
            if (seam.UnresolvableReason != null) return RefuseAbstain(seam.UnresolvableReason);
            if (seam.ScaleBakeReason != null) return RefuseAbstain(seam.ScaleBakeReason);
            if (seam.Pairs.Count == 0)
            {
                // The skill routes differently on each zero-pair case, so each gets its own string. MergeArmature
                // is tested first: what it did with the base armature outranks any anchor beside it. Reaching
                // here means its target resolved: a null target and a VRCFury ArmatureLink that resolves nothing
                // both left above as the UnresolvableReason abstain.
                if (HasMergeArmature(mergeGO))
                    return RefuseAbstain("MergeArmature on '" + mergeableRoot +
                        "' resolved its merge target but matched zero bones — nothing zips. Two shapes reach " +
                        "here and the seam cannot tell them apart: the phantom-bone failure (a prefix/suffix or " +
                        "bone-naming mismatch, repaired on the merge component) or a mergeable whose bones are " +
                        "all legitimately outfit-specific (nondestructive.md: an unmatched bone is kept, not a " +
                        "defect). Read the bone names against the base before repairing. Either way this is NOT " +
                        "the bare-prop case — the seam exists and the geometry needs no work");
                if (HasBoneProxy(mergeGO))
                    return RefuseAbstain("bone-proxy attachment on '" + mergeableRoot +
                        "' (offset-tolerant by design) — no scorable seam; verify the baked result");
                return RefuseAbstain("no seam component on '" + mergeableRoot +
                    "' — bare prop; route to own-mergeable to add a seam. To score coincidence before a seam " +
                    "exists (a fresh refit output, an unplaced mergeable), use CheckBare with an explicit " +
                    "maxOffsetMm instead");
            }

            float eps = Mathf.Max(EpsFloorMm, EpsSpanFraction * human.SpanMm);
            return Gate(baseGO, mergeGO, human, seam.Pairs, eps, false);
        }

        /// <summary>
        /// Pre-seam door onto the same gate as <see cref="Run"/>: pairs by bone name, consults no seam component,
        /// and takes a caller-stated tolerance with no default.
        /// Contract: docs/unity-tools.md.
        /// </summary>
        public static string CheckBare(string baseRoot, string mergeableRoot, float maxOffsetMm)
        {
            var baseHandle = SceneHandle.Resolve(baseRoot);
            if (!baseHandle.Ok) return RefuseMisuse("baseRoot: " + baseHandle.Refusal, BareLabel);
            var baseGO = baseHandle.Object;
            var mergeHandle = SceneHandle.Resolve(mergeableRoot);
            if (!mergeHandle.Ok) return RefuseMisuse("mergeableRoot: " + mergeHandle.Refusal, BareLabel);
            var mergeGO = mergeHandle.Object;
            // Only base-inside-mergeable sweeps the base's own bones into the merge-side name index. The reverse
            // nesting stays allowed: a mergeable placed under the base, not yet seamed, is worth scoring.
            if (mergeGO == baseGO)
                return RefuseMisuse("base and mergeable resolve to the same object ('" + PathOf(baseGO) +
                    "') — name-matching it against itself PASSes at any tolerance and certifies nothing", BareLabel);
            if (IsUnder(baseGO.transform, mergeGO))
                return RefuseMisuse("base '" + PathOf(baseGO) + "' is inside mergeable '" + PathOf(mergeGO) +
                    "' — name-matching would pair the base's own bones with themselves at zero offset and PASS " +
                    "at any tolerance. Name the two roots the other way round, or narrow the mergeable root to " +
                    "the subtree that excludes the base", BareLabel);
            // Below MinToleranceMm the report cannot render what it gates on: offsets print at the bare door's
            // fixed precision, so a tighter tolerance would round both ε and a genuine offender to zero and a
            // NOT-PASS would read as clean.
            if (!(maxOffsetMm >= MinToleranceMm) || float.IsInfinity(maxOffsetMm))
                return RefuseMisuse("maxOffsetMm must be a finite value >= " +
                    MinToleranceMm.ToString("G", CultureInfo.InvariantCulture) + "mm (got " +
                    maxOffsetMm.ToString("R", CultureInfo.InvariantCulture) +
                    ") — state the tolerance your verification doctrine implies; there is no default", BareLabel);

            var human = ResolveHumanoid(baseGO);
            if (human.Bones.Count == 0)
                return RefuseAbstain("base '" + baseRoot + "' has no humanoid Avatar — cannot certify fit " +
                    "(clothes-on-a-body is the domain)", BareLabel);

            var pairs = CollectByName(mergeGO, human, out string refusal);
            if (refusal != null) return RefuseAbstain(refusal, BareLabel);
            if (pairs.Count == 0)
                return RefuseAbstain("no humanoid bone name on base '" + baseRoot + "' matches any transform " +
                    "under '" + mergeableRoot + "' — the two skeletons share no bone names, so coincidence is " +
                    "unmeasurable. Check the roots are the ones you meant (a refit output keeps its SOURCE " +
                    "base's bone names, which a differently-named target base will not match)", BareLabel);

            return Gate(baseGO, mergeGO, human, pairs, maxOffsetMm, true);
        }

        // Only humanoid bones are collected, so the bare door's non-humanoid context partition is empty by
        // construction.
        private static List<BonePair> CollectByName(GameObject mergeGO, HumanoidMap human, out string refusal)
        {
            refusal = null;
            var pairs = new List<BonePair>();

            var baseByName = new Dictionary<string, Transform>();
            foreach (var b in human.Bones)
            {
                if (b == null) continue;
                if (baseByName.TryGetValue(b.name, out var other))
                {
                    refusal = "base has two humanoid bones both named '" + b.name + "' (" +
                        PathOf(other.gameObject) + " vs " + PathOf(b.gameObject) + ") — name-matching cannot " +
                        "tell them apart. Rename one on the base, or seam the mergeable and use Run";
                    return pairs;
                }
                baseByName[b.name] = b;
            }

            var byName = new Dictionary<string, List<Transform>>();
            foreach (var t in mergeGO.GetComponentsInChildren<Transform>(true))
            {
                if (!byName.TryGetValue(t.name, out var list)) byName[t.name] = list = new List<Transform>();
                list.Add(t);
            }

            foreach (var kv in baseByName)
            {
                if (!byName.TryGetValue(kv.Key, out var hits)) continue; // mergeable lacks this bone — legitimate
                if (hits.Count > 1)
                {
                    refusal = "bone name '" + kv.Key + "' is ambiguous under mergeable '" + mergeGO.name +
                        "': " + hits.Count + " transforms carry it (" + PathOf(hits[0].gameObject) + " vs " +
                        PathOf(hits[1].gameObject) + ") — name-matching would score an arbitrary one. The scan " +
                        "includes INACTIVE objects, so a disabled backup armature counts. Rename or remove carriers " +
                        "until exactly one remains (a physbone collider named after the bone it guards is the " +
                        "usual duplicate, and renaming it on the probe costs nothing), or seam the mergeable and " +
                        "use Run. Narrowing " +
                        "the mergeable root works ONLY if the duplicate sits outside the mesh subtree — narrowing " +
                        "past a sibling of the skinned meshes drops every weight and refuses again for a different, " +
                        "worse-reading reason";
                    return pairs;
                }
                pairs.Add(new BonePair { Base = kv.Value, Merge = hits[0] });
            }
            return pairs;
        }

        // ── Shared gate ───────────────────────────────────────────────────────────────────────────────
        // Everything from pair validation to Emit, for both doors. `bare` selects wording, the RunLog name and
        // the decimal precision, never the verdict.
        private static string Gate(GameObject baseGO, GameObject mergeGO, HumanoidMap human,
            List<BonePair> pairs, float eps, bool bare)
        {
            string label = bare ? BareLabel : SeamLabel;
            string offFmt = bare ? BareOffsetFormat : SeamOffsetFormat;
            foreach (var p in pairs)
            {
                if (p.Base == null || p.Merge == null) return RefuseAbstain("bone pair has a null bone", label);
                // Backstop behind CheckBare's containment guard, for any collector that yields a self-pair.
                if (p.Base == p.Merge)
                    return RefuseAbstain("bone '" + p.Base.name + "' (" + PathOf(p.Base.gameObject) +
                        ") is paired with itself — the two roots overlap, so this would score 0 and certify nothing", label);
                if (!IsUnder(p.Base, baseGO) || !IsUnder(p.Merge, mergeGO))
                    return RefuseAbstain("pairing targets a different avatar (a mapped bone is not under its root)", label);
            }
            // Two seams mapping one base bone to different merge bones. Unreachable from the bare door, whose
            // pairs are one per unique base bone name.
            var byBase = new Dictionary<Transform, Transform>();
            foreach (var p in pairs)
            {
                if (byBase.TryGetValue(p.Base, out var other) && other != p.Merge)
                    return RefuseAbstain("seams disagree on base bone '" + p.Base.name + "' (" +
                        PathOf(other.gameObject) + " vs " + PathOf(p.Merge.gameObject) + ")", label);
                byBase[p.Base] = p.Merge;
            }
            // MA and VRCFury can each contribute the same (Base,Merge) pair, which the conflict loop keeps. Deduped,
            // a single genuine proxy bone cannot be double-counted past the ≤1 proxy REFUSE.
            var seen = new HashSet<(Transform, Transform)>();
            pairs = pairs.Where(p => seen.Add((p.Base, p.Merge))).ToList();

            var maxW = MaxWeights(mergeGO);
            const float WEIGHT = 0.1f;
            var weightedHum = new List<BonePair>();
            foreach (var p in pairs)
                if (human.Bones.Contains(p.Base) && maxW.TryGetValue(p.Merge, out var wt) && wt >= WEIGHT)
                    weightedHum.Add(p);

            if (weightedHum.Count <= 1)
            {
                string bone = weightedHum.Count == 1 ? weightedHum[0].Base.name : "(none)";
                float d = weightedHum.Count == 1
                    ? Vector3.Distance(weightedHum[0].Base.position, weightedHum[0].Merge.position) * 1000f
                    : 0f;
                string delta = ", delta=" + d.ToString(offFmt, CultureInfo.InvariantCulture) + "mm";
                // Root-choice, not mergeable quality, and true at BOTH doors: a seam-door caller who narrows past
                // the meshes lands in exactly this state and would otherwise be told to "verify the baked result".
                if (maxW.Count == 0)
                    return RefuseAbstain("no skinned weights under mergeable root '" + PathOf(mergeGO) + "'" +
                        " — nothing under it binds a bone, so coincidence cannot be scored. The usual cause is a " +
                        "root narrowed past the meshes (SkinnedMeshRenderers sitting as SIBLINGS of the armature, " +
                        "not under it): pass the root that contains both. This is not evidence about the " +
                        "mergeable's quality", label);

                // The doors must not share this sentence. At the seam door one weighted humanoid bone is what a
                // correct hair, hat or earring looks like. At the bare door it is a failed warp or a bone-naming
                // break, and "verify the bake" would send the reader to inspect a result that should be rebuilt.
                if (bare)
                {
                    return RefuseAbstain("only " + weightedHum.Count + " shared weighted humanoid bone: " + bone + delta +
                        " — too few to certify coincidence. Two whole skeletons matching on at most one skinned " +
                        "bone name is a failed transfer or a bone-naming break, not a close fit: rebuild the " +
                        "mergeable rather than inspecting this one", label);
                }
                return RefuseAbstain("single humanoid attachment: " + bone + delta +
                    " — offset-tolerant accessory/proxy, verify the baked result", label);
            }

            var hipsBase = HipsOf(baseGO); // null ⇒ fromHips reports 0; it is a report field and never gates
            var allOffsets = new List<(string bone, float mm, float fromHips)>();
            foreach (var p in weightedHum)
            {
                float mm = Vector3.Distance(p.Base.position, p.Merge.position) * 1000f;
                float fromHips = hipsBase != null ? Vector3.Distance(hipsBase.position, p.Base.position) * 1000f : 0f;
                allOffsets.Add((p.Base.name, mm, fromHips));
            }
            var offenders = allOffsets.Where(o => o.mm > eps).ToList();
            offenders.Sort((a, b) => b.mm.CompareTo(a.mm)); // worst first: Emit's maxOffset reads [0]

            // Non-humanoid bones never gate: a weighted leaf (physbone/collider end-bone) only counts, and a
            // weighted non-leaf surfaces as context.
            var smrBones = new HashSet<Transform>();
            foreach (var smr in mergeGO.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var b in smr.bones) if (b != null) smrBones.Add(b);
            bool IsLeaf(Transform t) { foreach (Transform c in t) if (smrBones.Contains(c)) return false; return true; }

            int dropped = 0;
            var context = new List<(string bone, float mm, float fromHips)>();
            foreach (var p in pairs)
            {
                if (human.Bones.Contains(p.Base)) continue;
                if (!maxW.TryGetValue(p.Merge, out var wt) || wt < WEIGHT) continue;
                if (IsLeaf(p.Merge)) { dropped++; continue; }
                float mm = Vector3.Distance(p.Base.position, p.Merge.position) * 1000f;
                float fromHips = hipsBase != null ? Vector3.Distance(hipsBase.position, p.Base.position) * 1000f : 0f;
                context.Add((p.Base.name, mm, fromHips));
            }
            context.Sort((a, b) => b.mm.CompareTo(a.mm));
            return Emit(baseGO, mergeGO, weightedHum.Count, offenders, allOffsets, context, dropped, eps, bare);
        }

        // ── Output ─────────────────────────────────────────────────────────────────────────────────────────
        // Context and dropped never shift the verdict; they ride the summary and body so a PASS beside wild
        // context deltas does not read like a clean one.
        private static string Emit(GameObject baseGO, GameObject mergeGO, int weightedCount,
            List<(string bone, float mm, float fromHips)> offenders,
            List<(string bone, float mm, float fromHips)> allOffsets,
            List<(string bone, float mm, float fromHips)> context, int dropped, float eps, bool bare)
        {
            int m = offenders.Count, k = context.Count;
            string verdict = m == 0 ? "PASS" : "NOT-PASS";
            // The bare door's tolerance can be ~0.001mm, where the seam door's F1/F2 rounds every magnitude to
            // zero and an offender would read as clean, so the precision tracks the door.
            string label = bare ? BareLabel : SeamLabel;
            string offFmt = bare ? BareOffsetFormat : SeamOffsetFormat;
            string bandFmt = bare ? BareOffsetFormat : SeamBandFormat;

            // NOT-PASS puts the worst humanoid offset on the one-liner, so it reads sub-mm noise vs wrong base at
            // a glance. PASS surfaces the sub-ε band instead: widening ε absorbs base drift but retires the "few
            // peripheral bones just over ε" flag. Descriptive only: what to do about a near-ε bone is
            // compose-mergeable's call, not this tool's.
            string withinEpsBand = null; // PASS-only body line + summary tail
            string maxTail;
            if (m > 0)
            {
                maxTail = " maxOffset=" + offenders[0].mm.ToString(offFmt, CultureInfo.InvariantCulture) + "mm";
            }
            else
            {
                float maxW = allOffsets.Count > 0 ? allOffsets.Max(o => o.mm) : 0f;
                float medW = Median(allOffsets.Select(o => o.mm));
                withinEpsBand = "maxWithinEps=" + maxW.ToString(bandFmt, CultureInfo.InvariantCulture)
                    + "mm (median " + medW.ToString(bandFmt, CultureInfo.InvariantCulture) + "mm)";
                maxTail = " " + withinEpsBand;
            }
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[{0}] {1}→{2}: weightedHumanoid={3} offenders={4}{5} context={6} dropped={7} => {8}",
                label, mergeGO.name, baseGO.name, weightedCount, m, maxTail, k, dropped, verdict);

            var sb = new StringBuilder();
            sb.Append("# ").Append(label).Append(": ").Append(mergeGO.name).Append(" → ").Append(baseGO.name).Append('\n');
            sb.Append("mergeable: `").Append(PathOf(mergeGO)).Append("`  \n");
            sb.Append("base: `").Append(PathOf(baseGO)).Append("`  \n\n");
            sb.Append(summary.Substring(("[" + label + "] ").Length)).Append('\n');

            string epsProvenance = bare
                ? "caller-supplied maxOffsetMm; pairs matched by bone name"
                : string.Format(CultureInfo.InvariantCulture, "max({0}mm, {1}%·Hips→Head span); pairs from the seam's own mapping",
                    EpsFloorMm.ToString("G", CultureInfo.InvariantCulture),
                    (EpsSpanFraction * 100f).ToString("G", CultureInfo.InvariantCulture));
            sb.Append("\n## Gate — weighted humanoid bones (ε=")
              .Append(eps.ToString(bandFmt, CultureInfo.InvariantCulture))
              .Append("mm, ").Append(epsProvenance).Append(")\n\n");
            if (offenders.Count == 0)
            {
                sb.Append("_(all within ε)_\n");
                if (withinEpsBand != null) sb.Append(withinEpsBand).Append('\n');
            }
            else foreach (var o in offenders)
                sb.Append(bare ? "- **bone-offset** bone=`" : "- **seam-offset** bone=`").Append(o.bone)
                  .Append("` offset=").Append(o.mm.ToString(offFmt, CultureInfo.InvariantCulture))
                  .Append("mm fromHips=").Append(o.fromHips.ToString("F1", CultureInfo.InvariantCulture))
                  .Append("mm\n");

            if (bare)
            {
                sb.Append("\nNo context partition: name-matching collects humanoid bones only, so non-humanoid " +
                          "deltas are neither scored nor reported here. Use the seam door once the mergeable is placed.\n");
            }
            else
            {
                sb.Append("\n## Context — non-humanoid weighted bones (ungated; interpret in context)\n\n");
                if (context.Count == 0) sb.Append("_(none)_\n");
                else foreach (var c in context)
                    sb.Append("- bone=`").Append(c.bone)
                      .Append("` offset=").Append(c.mm.ToString(offFmt, CultureInfo.InvariantCulture))
                      .Append("mm fromHips=").Append(c.fromHips.ToString("F1", CultureInfo.InvariantCulture))
                      .Append("mm\n");

                sb.Append("\nDropped: ").Append(dropped)
                  .Append(" non-humanoid end-bones (physbone/collider tuning)\n");
            }

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir,
                (bare ? "checkseam-bare_" : "checkseam_") + mergeGO.name, summary, sb.ToString(), ".md");
            if (verdict == "PASS") Debug.Log(res); else Debug.LogWarning(res);
            return res;
        }

        private static float Median(IEnumerable<float> values)
        {
            var s = values.OrderBy(v => v).ToList();
            if (s.Count == 0) return 0f;
            int mid = s.Count / 2;
            return s.Count % 2 == 1 ? s[mid] : 0.5f * (s[mid - 1] + s[mid]);
        }

        private static Transform HipsOf(GameObject baseGO)
        {
            var anim = baseGO.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.isHuman) return null;
            return anim.GetBoneTransform(HumanBodyBones.Hips);
        }

        // Merge Transform → max vertex weight, over ALL influences per vertex, not the legacy top-4
        // mesh.boneWeights view: a humanoid bone at ≥WEIGHT that never lands in a vertex's top 4 would be
        // dropped, flipping the count.
        private static Dictionary<Transform, float> MaxWeights(GameObject mergeGO)
        {
            var w = new Dictionary<Transform, float>();
            foreach (var smr in mergeGO.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh; if (mesh == null) continue;
                var bones = smr.bones;
                var bonesPerVertex = mesh.GetBonesPerVertex(); // NativeArray<byte>, one count per vertex
                var weights = mesh.GetAllBoneWeights();        // NativeArray<BoneWeight1>, flat, grouped by vertex
                int ptr = 0;
                for (int v = 0; v < bonesPerVertex.Length; v++)
                {
                    int count = bonesPerVertex[v];
                    for (int j = 0; j < count; j++)
                    {
                        var bw1 = weights[ptr + j];
                        int idx = bw1.boneIndex;
                        if (idx < 0 || idx >= bones.Length || bones[idx] == null) continue;
                        var t = bones[idx];
                        if (!w.TryGetValue(t, out var cur) || bw1.weight > cur) w[t] = bw1.weight;
                    }
                    ptr += count;
                }
            }
            return w;
        }

        private static bool IsUnder(Transform t, GameObject root)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur == root.transform) return true;
            return false;
        }

        // REFUSE severity split: misuse (bad input / broken environment the caller must fix) logs at ERROR;
        // valid-abstain (a legitimate scene the gate simply can't certify — no humanoid, no seam, proxy,
        // scale-at-bake, …) logs at WARNING.
        private static string RefuseMisuse(string why, string label = SeamLabel) { var e = "[" + label + "] REFUSE: " + why; Debug.LogError(e); return e; }
        private static string RefuseAbstain(string why, string label = SeamLabel) { var e = "[" + label + "] REFUSE: " + why; Debug.LogWarning(e); return e; }

        // ── Seam defaults ──────────────────────────────────────────────────────────────────────────────────

        // Union of MA GetBonesMapping (base,merge) and VRCFury GetLinks().mergeBones (merge,base, flipped). Two
        // layers of guard: each collector runs under its own try here (catching setup throws from type, method
        // and field resolution), and inside each collector every component iterates under its own try (catching
        // a per-component throw and continuing the sweep).
        internal static SeamResolution ResolveMergeMap(GameObject scopeGO, GameObject avatarGO)
        {
            var res = new SeamResolution();
            try { CollectMaPairs(scopeGO, res); } catch (Exception e) { ClassifyReflect(e, res); }
            try { CollectVrcfPairs(scopeGO, avatarGO, res); } catch (Exception e) { ClassifyReflect(e, res); }
            return res;
        }

        // Reflection wraps a real throw in TargetInvocationException, so unwrap it. Genuine API drift (the tool
        // broken against the installed package) => ReflectError (misuse/error). A seam that exists but won't
        // resolve onto this base => UnresolvableReason (valid abstain). Every vendor pin in VendorReflect throws
        // into this split, so those pins must not swallow.
        internal static void ClassifyReflect(Exception e, SeamResolution res)
        {
            // First carrier wins across BOTH fields: the guards continue past a throw, and a later throw must
            // not upgrade an earlier abstain (UnresolvableReason/warning) to misuse (ReflectError/error).
            if (res.ReflectError != null || res.UnresolvableReason != null) return;
            // A RAW ArgumentException/TargetParameterCountException — not unwrapped from a
            // TargetInvocationException shell — was thrown by the reflection layer itself (argument binding
            // failed before the vendor method ran): that is API drift, whatever the pins missed. A
            // vendor-thrown ArgumentException arrives wrapped and still classifies below.
            if (!(e is System.Reflection.TargetInvocationException) &&
                (e is ArgumentException || e is System.Reflection.TargetParameterCountException))
            {
                res.ReflectError = e.GetType().Name + " (reflective invoke rejected its arguments — vendor API drift): " + e.Message;
                return;
            }
            var real = (e as System.Reflection.TargetInvocationException)?.InnerException ?? e;
            if (real is MissingMethodException || real is MissingFieldException ||
                real is MissingMemberException || real is TypeLoadException)
                res.ReflectError = real.GetType().Name + ": " + real.Message;
            else
                // The inner exception's own account, with no guessed cause on top: a guess would print beside
                // text that already names the real one.
                res.UnresolvableReason = "seam present but does not resolve onto this base: " +
                    real.GetType().Name + ": " + real.Message;
        }

        private static SeamResolution DefaultResolveSeam(GameObject baseGO, GameObject mergeGO)
            => ResolveMergeMap(mergeGO, baseGO);

        // A ModularAvatar BoneProxy is the anchor-style seam neither pair collector resolves, so it lands in the
        // zero-pairs REFUSE, but it is a legitimate offset-tolerant attachment, not a bare prop. Reflected by
        // name (this asmdef has no MA reference). Null type (MA absent) or no component ⇒ false ⇒ treated as bare.
        private static bool HasBoneProxy(GameObject mergeGO)
        {
            var t = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
            return t != null && mergeGO.GetComponentInChildren(t, true) != null;
        }

        private static bool HasMergeArmature(GameObject mergeGO)
        {
            var t = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            return t != null && mergeGO.GetComponentInChildren(t, true) != null;
        }

        // MA: ModularAvatarMergeArmature.GetBonesMapping() → List<(Transform base, Transform merge)> (Item1=base).
        // Returns matched descendants only (not the root pair) — that is fine, the descendants carry the offset.
        // NULL is a different answer from empty and must not collapse into it: GetBonesMapping returns null
        // when `mergeTarget.Get(this)` finds nothing, i.e. there is no base to match AGAINST, and the repair is
        // the mergeTarget — not the bone naming the zero-match REFUSE would prescribe.
        private static void CollectMaPairs(GameObject mergeGO, SeamResolution res)
        {
            var maType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            if (maType == null) return; // MA not installed ⇒ no MA seam
            // A drifted signature must fail as Missing* → ReflectError, not land in ClassifyReflect's abstain arm.
            // The RETURN type is asserted too: the `as IEnumerable` cast below would turn a drifted one into null,
            // emitting the mergeTarget repair for a component whose target is fine.
            var getMapping = maType.GetMethod("GetBonesMapping", BindingFlags.Public | BindingFlags.Instance);
            if (getMapping == null || getMapping.GetParameters().Length != 0
                || !typeof(System.Collections.IEnumerable).IsAssignableFrom(getMapping.ReturnType))
                throw new MissingMethodException("ModularAvatarMergeArmature.GetBonesMapping() : IEnumerable");
            foreach (var comp in mergeGO.GetComponentsInChildren(maType, true))
            {
                try
                {
                    var mapping = getMapping.Invoke(comp, null) as System.Collections.IEnumerable;
                    if (mapping == null)
                    {
                        if (res.UnresolvableReason == null) // first carrier wins, as in ClassifyReflect
                            res.UnresolvableReason = "MergeArmature on '" + PathOf(comp.gameObject) +
                                "' resolves no merge target, so there is no base armature to match against — its " +
                                "mergeTarget is unset, points off this avatar, or was broken by a rename. Fix the " +
                                "mergeTarget; the bone naming is not in question yet. If the mergeable is not " +
                                "placed on the base at all (a fresh refit output beside a target body), that is " +
                                "the pre-seam case: score it with CheckBare and an explicit maxOffsetMm instead";
                        continue;
                    }
                    foreach (var item in mapping)
                    {
                        var tt = item.GetType();
                        var item1 = tt.GetField("Item1"); var item2 = tt.GetField("Item2");
                        if (item1 == null) throw new MissingFieldException(tt.Name, "Item1");
                        if (item2 == null) throw new MissingFieldException(tt.Name, "Item2");
                        res.Pairs.Add(new BonePair { Base = item1.GetValue(item) as Transform, Merge = item2.GetValue(item) as Transform });
                    }
                }
                catch (Exception e) { ClassifyReflect(e, res); }
            }
        }

        // VRCFury: VendorReflect.ResolveVrcfArmatureLink owns the pins; a drift THROWS there, into ResolveMergeMap's
        // guard, not the per-component try below. GetLinks' .mergeBones is a Stack<(prop/merge, avatar/base)> of
        // VFGameObjects: flipped vs MA. The GetPerFrame caches materialise LAZILY on the first ArmatureLink content,
        // so (a) a snapshot throw (VRCFArmatureCache walks the humanoid Avatar asset and throws on a half-imported
        // one) classifies per-component, and (b) an avatar carrying no ArmatureLink, every CheckAvatar lint, never
        // pays the full-hierarchy capture. GetPerFrame is at most one editor update tick old, NOT fresh: a
        // mutate-then-check sequence inside a single editor callback can read a pre-mutation snapshot. GetLinks
        // throws (empty linkTo / link inside armature / bad Hips) AND returns null (propBone == null); both are
        // resolution failures.
        private static void CollectVrcfPairs(GameObject mergeGO, GameObject avatarGO, SeamResolution res)
        {
            var pins = VendorReflect.ResolveVrcfArmatureLink();
            if (pins == null) return; // VRCFury not installed ⇒ no VRCFury seam

            var avatarVfGo = VendorReflect.ToVfGameObject(pins, avatarGO);
            object objectPaths = null, armatureCache = null; // lazy — see the collector comment above

            foreach (var comp in mergeGO.GetComponentsInChildren(pins.VrcfType, true))
            {
                try
                {
                    var content = pins.ContentField.GetValue(comp);
                    if (content == null || !pins.ArmLinkType.IsInstanceOfType(content)) continue;
                    if (objectPaths == null) objectPaths = pins.PathCacheFactory.Invoke(null, new object[] { avatarVfGo });
                    if (armatureCache == null) armatureCache = pins.ArmCacheFactory.Invoke(null, new object[] { avatarVfGo });
                    var links = pins.GetLinks.Invoke(null, new object[] { content, avatarVfGo, objectPaths, armatureCache });
                    if (links == null) throw new NullReferenceException("GetLinks returned null (propBone == null)");

                    // A scaled bake makes edit-time world-position coincidence meaningless (the baker rescales the
                    // whole prop), so the edit-time pose cannot certify it: REFUSE (abstain).
                    if (res.ScaleBakeReason == null) // GetScalingFactor(model, links) → (float,float,float); Item3 = factor
                    {
                        bool forceOne = (bool)pins.ForceOneWorldScaleField.GetValue(content);
                        var factorTuple = pins.GetScalingFactor.Invoke(null, new object[] { content, links });
                        float factor = 1f;
                        if (factorTuple != null)
                        {
                            var item3 = factorTuple.GetType().GetField("Item3");
                            if (item3 == null) throw new MissingFieldException(factorTuple.GetType().Name, "Item3");
                            factor = (float)item3.GetValue(factorTuple);
                        }
                        if (forceOne || Mathf.Abs(1f - factor) > 1e-4f)
                            res.ScaleBakeReason = "scaled at bake (forceOneWorldScale / non-unit scale) — edit-time coincidence unverifiable, check the baked result";
                    }

                    var mergeBonesField = links.GetType().GetField("mergeBones");
                    if (mergeBonesField == null) throw new MissingFieldException(links.GetType().Name, "mergeBones");
                    var mergeBones = mergeBonesField.GetValue(links) as System.Collections.IEnumerable;
                    if (mergeBones == null) continue;
                    foreach (var pair in mergeBones)
                    {
                        var pt = pair.GetType();
                        var item1 = pt.GetField("Item1"); var item2 = pt.GetField("Item2");
                        if (item1 == null) throw new MissingFieldException(pt.Name, "Item1");
                        if (item2 == null) throw new MissingFieldException(pt.Name, "Item2");
                        var mergeVf = item1.GetValue(pair); // prop/merge
                        var baseVf = item2.GetValue(pair);  // avatar/base
                        res.Pairs.Add(new BonePair { Base = VendorReflect.FromVfGameObject(pins, baseVf),
                                                     Merge = VendorReflect.FromVfGameObject(pins, mergeVf) });
                    }
                }
                catch (Exception e) { ClassifyReflect(e, res); }
            }
        }

        private static HumanoidMap DefaultResolveHumanoid(GameObject baseGO)
        {
            var map = new HumanoidMap();
            var anim = baseGO.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.isHuman) return map;
            Transform hips = null, head = null;
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var t = anim.GetBoneTransform((HumanBodyBones)i);
                if (t == null) continue;
                map.Bones.Add(t);
                if ((HumanBodyBones)i == HumanBodyBones.Hips) hips = t;
                if ((HumanBodyBones)i == HumanBodyBones.Head) head = t;
            }
            map.SpanMm = (hips != null && head != null) ? Vector3.Distance(hips.position, head.position) * 1000f : 0f;
            return map;
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
