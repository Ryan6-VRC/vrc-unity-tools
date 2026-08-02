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
    /// Scene-scoped fit gate: mechanically certify whether a mergeable's humanoid skeleton coincides with a
    /// base's before any render — <c>PASS</c> / <c>NOT-PASS</c> / bare <c>REFUSE</c>. It scores position, not
    /// intent: it counts weighted humanoid bones and gates on edit-time world-position coincidence.
    ///
    /// Two doors, one gate. <see cref="Check"/> reflects the seam's own mapping (Modular Avatar
    /// <c>GetBonesMapping</c> / VRCFury <c>ArmatureLinkService.GetLinks</c>) and derives ε from the base's
    /// scale — the composed case. <see cref="CheckBare"/> pairs by BONE NAME and takes the tolerance from the
    /// caller — the pre-seam case, where a mergeable sits beside a base with no mapping to reflect (a fresh
    /// refit output, an unplaced mergeable). They differ only in where the pairs come from; the ≤1 proxy
    /// floor, the coincidence gate, and Emit are shared. The non-humanoid context partition is the one thing
    /// that is not: name-matching collects humanoid bones only, so it is empty by construction at the bare door.
    ///
    /// Pure-core + injectable seams (mirrors <see cref="CheckAvatar"/>): each door resolves two scene roots and
    /// calls <see cref="ResolveHumanoid"/>, then collects pairs — <see cref="Check"/> through
    /// <see cref="ResolveSeam"/>, <see cref="CheckBare"/> by name, consulting no seam at all — then a shared
    /// pure scoring core. The seam defaults do the real reflection; tests swap fakes.
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

        // The seam door's ε, declared once. Emit prints the formula in its body header, so a literal there
        // would be a second home for these two numbers inside one file — the same shape that let the doc's
        // "0.2%" drift away from the code for as long as it did.
        private const float EpsFloorMm = 0.5f;
        private const float EpsSpanFraction = 0.003f;
        // Bare-door tolerance floor: BareOffsetFormat's resolution. See the guard in CheckBare.
        private const float MinToleranceMm = 0.0001f;
        private const string BareOffsetFormat = "F4";
        private const string SeamOffsetFormat = "F1";
        private const string SeamBandFormat = "F2";

        // ── Doors ─────────────────────────────────────────────────────────────────────────────────────

        public static string Check(string baseRoot, string mergeableRoot)
        {
            var baseGO = Resolve(baseRoot);
            if (baseGO == null) return RefuseMisuse("base root '" + baseRoot + "' not found in the active scene");
            var mergeGO = Resolve(mergeableRoot);
            if (mergeGO == null) return RefuseMisuse("mergeable root '" + mergeableRoot + "' not found in the active scene");

            var human = ResolveHumanoid(baseGO);
            if (human.Bones.Count == 0)
                return RefuseAbstain("base '" + baseRoot + "' has no humanoid Avatar — cannot certify fit (clothes-on-a-body is the domain)");

            var seam = ResolveSeam(baseGO, mergeGO);
            if (seam.ReflectError != null) return RefuseMisuse("seam resolution failed: " + seam.ReflectError);
            if (seam.UnresolvableReason != null) return RefuseAbstain(seam.UnresolvableReason);
            if (seam.ScaleBakeReason != null) return RefuseAbstain(seam.ScaleBakeReason);
            if (seam.Pairs.Count == 0)
            {
                // Name what it found: a MergeArmature that matched nothing, a BoneProxy (offset-tolerant
                // anchor, verify the bake), and a genuinely bare prop (route to own-mergeable to add a seam)
                // all yield zero scorable pairs, but the skill routes DIFFERENTLY on each — one string for
                // several was the G26 conflation. MergeArmature is tested first: where one is present, what it
                // did with the base armature is the scene's most specific fact, and it outranks any anchor
                // sitting alongside it. Reaching here means the merge target RESOLVED (a null one is the
                // UnresolvableReason abstain CollectMaPairs raises above) and matching still produced nothing.
                // A VRCFury ArmatureLink that resolves nothing lands in that same abstain, by throwing.
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

            // ε from the base's own scale — the seam door has no caller tolerance to take.
            float eps = Mathf.Max(EpsFloorMm, EpsSpanFraction * human.SpanMm);
            return Gate(baseGO, mergeGO, human, seam.Pairs, eps, false);
        }

        /// <summary>
        /// Resolver-free coincidence gate: same verdict grammar as <see cref="Check"/>, but the base↔merge
        /// pairs are matched by BONE NAME and no seam component is consulted. For the pre-seam case a seam
        /// resolver cannot score — a raw refit output beside a target body, an unplaced mergeable — where
        /// <see cref="Check"/> correctly REFUSEs because there is no mapping to reflect. Pre-seam is not
        /// seamless: a raw refit output does carry a MergeArmature, it simply has no base to resolve against
        /// yet, so <see cref="Check"/> lands on the mergeTarget abstain rather than the bare-prop REFUSE.
        ///
        /// <paramref name="maxOffsetMm"/> has no default on purpose. The known regimes differ by orders of
        /// magnitude — a warp solver's residue is ~0.001mm (millimetre-scale there is a wrong result, not
        /// slop) while pre-seam staging is millimetre-scale legitimately — so a default sized for one is a
        /// silent trap for the other. The caller states the tolerance its own verification doctrine implies.
        /// </summary>
        public static string CheckBare(string baseRoot, string mergeableRoot, float maxOffsetMm)
        {
            var baseGO = Resolve(baseRoot);
            if (baseGO == null) return RefuseMisuse("base root '" + baseRoot + "' not found in the active scene", BareLabel);
            var mergeGO = Resolve(mergeableRoot);
            if (mergeGO == null) return RefuseMisuse("mergeable root '" + mergeableRoot + "' not found in the active scene", BareLabel);
            // Name-matching a skeleton against itself pairs every bone with itself at distance 0 and PASSes at
            // any tolerance. The seam door cannot reach this (no seam maps a root onto itself); the bare door
            // has to refuse it, and the same object is only the visible half. The dangerous half is CONTAINMENT:
            // when the base sits INSIDE the mergeable, the merge-side name index sweeps in the base's own bones,
            // so every bone the mergeable does not itself carry a copy of pairs (b, b) — and nothing downstream
            // catches it, because such a bone genuinely is under both roots. The reverse nesting is fine and
            // stays allowed: a mergeable placed under the base (not yet seamed) is a case worth scoring.
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
            // NOT-PASS would read as clean. Infinity clears any `> 0` test and would PASS everything.
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

        // Name-matched base↔merge pairing for the bare door. Consults NO seam component: a pair is a base
        // humanoid bone and the transform under the mergeable root carrying the same name. Ambiguity on
        // either side REFUSEs rather than picking arbitrarily — a refit output that carries a duplicated
        // armature copy would otherwise be scored against whichever duplicate the walk reached first, and a
        // PASS from that certifies nothing. Only humanoid bones are collected, so the bare door's non-humanoid
        // context partition is empty by construction.
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
                        "tell them apart. Rename one on the base, or seam the mergeable and use Check";
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
                        "includes INACTIVE objects, so a disabled backup armature counts: remove or rename the " +
                        "duplicate, narrow the mergeable root past it, or seam the mergeable and use Check";
                    return pairs;
                }
                pairs.Add(new BonePair { Base = kv.Value, Merge = hits[0] });
            }
            return pairs;
        }

        // ── Shared gate ───────────────────────────────────────────────────────────────────────────────
        // Everything from pair validation to Emit, identical for both doors. `bare` selects the label, the
        // RunLog name, the ε provenance note, and the decimal precision (a 0.001mm regime is invisible at the
        // seam door's F1/F2) — never the verdict.
        private static string Gate(GameObject baseGO, GameObject mergeGO, HumanoidMap human,
            List<BonePair> pairs, float eps, bool bare)
        {
            string label = bare ? BareLabel : SeamLabel;
            string offFmt = bare ? BareOffsetFormat : SeamOffsetFormat;
            foreach (var p in pairs)
            {
                if (p.Base == null || p.Merge == null) return RefuseAbstain("bone pair has a null bone", label);
                // A pair whose two sides are the SAME transform scores 0 and certifies nothing. CheckBare's
                // containment guard is what should have caught this; keep the assertion here so no future
                // collector can reintroduce a self-pair silently.
                if (p.Base == p.Merge)
                    return RefuseAbstain("bone '" + p.Base.name + "' (" + PathOf(p.Base.gameObject) +
                        ") is paired with itself — the two roots overlap, so this would score 0 and certify nothing", label);
                if (!IsUnder(p.Base, baseGO) || !IsUnder(p.Merge, mergeGO))
                    return RefuseAbstain("pairing targets a different avatar (a mapped bone is not under its root)", label);
            }
            // conflict: the same base bone mapped to two different merge bones (MA and VRCFury disagree).
            // Unreachable from the bare door, whose pairs are one-per-unique-base-bone-name by construction.
            var byBase = new Dictionary<Transform, Transform>();
            foreach (var p in pairs)
            {
                if (byBase.TryGetValue(p.Base, out var other) && other != p.Merge)
                    return RefuseAbstain("seams disagree on base bone '" + p.Base.name + "' (" +
                        PathOf(other.gameObject) + " vs " + PathOf(p.Merge.gameObject) + ")", label);
                byBase[p.Base] = p.Merge;
            }
            // MA and VRCFury can each contribute the SAME (Base,Merge) pair; the conflict loop above keeps an
            // identical duplicate (it only rejects a base mapped to two DIFFERENT merges). Dedupe by reference
            // identity so a single genuine proxy bone isn't double-counted past the ≤1 proxy REFUSE.
            var seen = new HashSet<(Transform, Transform)>();
            pairs = pairs.Where(p => seen.Add((p.Base, p.Merge))).ToList();

            // Count weighted humanoid bones: a pair qualifies iff its BASE is humanoid and a mergeable SMR skins
            // its MERGE side at ≥ WEIGHT. Join on the merge side — SMR.bones[] reference the merge transforms.
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
                // Same count, opposite meanings — so the two doors must not share the sentence. At the seam
                // door one weighted humanoid bone is what a correct hair/hat/earring looks like. At the bare
                // door it means two whole skeletons shared at most one skinned humanoid bone NAME, which for a
                // refit output is a failed warp or a bone-naming break, and "verify the bake" would send the
                // reader to inspect a result that should be rebuilt.
                if (bare)
                    return RefuseAbstain("only " + weightedHum.Count + " shared weighted humanoid bone: " + bone + delta +
                        " — too few to certify coincidence. Two whole skeletons matching on at most one skinned " +
                        "bone name is a failed transfer or a bone-naming break, not a close fit: rebuild the " +
                        "mergeable rather than inspecting this one", label);
                return RefuseAbstain("single humanoid attachment: " + bone + delta +
                    " — offset-tolerant accessory/proxy, verify the baked result", label);
            }

            // ≥2 weighted humanoid ⇒ coincidence gate: compare edit-time WORLD positions at ε tolerance.
            var hipsBase = HipsOf(baseGO); // fromHips anchor; null ⇒ report 0 (robust, non-load-bearing)
            // Collect EVERY weighted-humanoid offset (not just the > ε subset): the > ε ones are offenders, but
            // the sub-ε band rides through to Emit so a PASS can surface maxWithinEps for the downstream skill.
            var allOffsets = new List<(string bone, float mm, float fromHips)>();
            foreach (var p in weightedHum)
            {
                float mm = Vector3.Distance(p.Base.position, p.Merge.position) * 1000f;
                float fromHips = hipsBase != null ? Vector3.Distance(hipsBase.position, p.Base.position) * 1000f : 0f;
                allOffsets.Add((p.Base.name, mm, fromHips));
            }
            var offenders = allOffsets.Where(o => o.mm > eps).ToList();
            offenders.Sort((a, b) => b.mm.CompareTo(a.mm)); // worst (largest offset) first

            // Non-humanoid mapped bones NEVER gate (they legitimately deviate on a correct fit). Partition the
            // weighted ones: leaves (physbone/collider end-bones) drop to a count; weighted non-leaves surface as
            // ungated CONTEXT deltas. Leaf = no child transform among the mergeable's SMR bones[] set.
            var smrBones = new HashSet<Transform>();
            foreach (var smr in mergeGO.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var b in smr.bones) if (b != null) smrBones.Add(b);
            bool IsLeaf(Transform t) { foreach (Transform c in t) if (smrBones.Contains(c)) return false; return true; }

            int dropped = 0;
            var context = new List<(string bone, float mm, float fromHips)>();
            foreach (var p in pairs)
            {
                if (human.Bones.Contains(p.Base)) continue;                          // humanoid ⇒ the gate above
                if (!maxW.TryGetValue(p.Merge, out var wt) || wt < WEIGHT) continue; // unweighted ⇒ ignore
                if (IsLeaf(p.Merge)) { dropped++; continue; }                        // end-bone ⇒ count only
                float mm = Vector3.Distance(p.Base.position, p.Merge.position) * 1000f;
                float fromHips = hipsBase != null ? Vector3.Distance(hipsBase.position, p.Base.position) * 1000f : 0f;
                context.Add((p.Base.name, mm, fromHips));
            }
            context.Sort((a, b) => b.mm.CompareTo(a.mm)); // worst (largest offset) first
            return Emit(baseGO, mergeGO, weightedHum.Count, offenders, allOffsets, context, dropped, eps, bare);
        }

        // ── Output (mirrors CheckAvatar.Emit: summary + markdown body + WriteRunLog + severity-by-verdict) ─
        // Verdict is a pure function of the humanoid offender count — context (k) and dropped (d) are the
        // non-humanoid partition (ungated) and NEVER shift PASS/NOT-PASS; they ride the summary + body so a
        // PASS beside wild context deltas doesn't read like a clean one.
        private static string Emit(GameObject baseGO, GameObject mergeGO, int weightedCount,
            List<(string bone, float mm, float fromHips)> offenders,
            List<(string bone, float mm, float fromHips)> allOffsets,
            List<(string bone, float mm, float fromHips)> context, int dropped, float eps, bool bare)
        {
            int m = offenders.Count, k = context.Count;
            string verdict = m == 0 ? "PASS" : "NOT-PASS";
            // Both doors share every string; only the label and the decimal precision differ. The bare door's
            // tolerance regime can be ~0.001mm, where the seam door's F1/F2 rounds every magnitude to zero and
            // an offender would read as clean — the precision must track the tolerance, not the tool.
            string label = bare ? BareLabel : SeamLabel;
            string offFmt = bare ? BareOffsetFormat : SeamOffsetFormat;  // offender + context magnitudes
            string bandFmt = bare ? BareOffsetFormat : SeamBandFormat;   // within-ε band, and ε in the body header

            // NOT-PASS: maxOffset carries the worst humanoid seam-offset magnitude onto the one-liner so it
            // reads sub-mm-noise vs wrong-base at a glance (offenders sorted worst-first). PASS: widening ε
            // absorbs base drift but retires the "few peripheral bones just over ε" flag, so surface the sub-ε
            // band instead — maxWithinEps (max + median of the within-ε offsets) for the downstream skill.
            // Descriptive only — the disposition doctrine (fix-to-PASS vs accept-with-flag) stays A8's, not
            // this tool's. NEVER emit maxOffset on PASS (G37 invariant).
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

            // ε's provenance rides the header: the seam door derives it, the bare door was handed it. A reader
            // of the RunLog alone must be able to tell which, because only one of them is the caller's to move.
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

            // The bare door collects humanoid bones only, so both partitions below are empty by construction —
            // printing "_(none)_" and "Dropped: 0" every single run would advertise an affordance that door
            // does not have. State the reason once instead.
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

        // Median of a set of offsets (mm) — even count averages the two middles. Empty ⇒ 0 (never reached on
        // PASS, where ≥2 weighted-humanoid offsets always exist).
        private static float Median(IEnumerable<float> values)
        {
            var s = values.OrderBy(v => v).ToList();
            if (s.Count == 0) return 0f;
            int mid = s.Count / 2;
            return s.Count % 2 == 1 ? s[mid] : 0.5f * (s[mid - 1] + s[mid]);
        }

        // Base Hips transform for the fromHips report distance; null unless the base has a humanoid Animator
        // (tests inject the HumanoidMap and carry no Animator ⇒ null ⇒ fromHips 0, which is fine — it is a
        // report field, never gates).
        private static Transform HipsOf(GameObject baseGO)
        {
            var anim = baseGO.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.isHuman) return null;
            return anim.GetBoneTransform(HumanBodyBones.Hips);
        }

        // Merge Transform → max vertex weight across every mergeable SMR, over ALL influences per vertex (not the
        // legacy top-4 mesh.boneWeights view — a humanoid bone at ≥WEIGHT that never lands in a vertex's top 4
        // would otherwise be dropped, flipping the count). Walks the flat GetAllBoneWeights() array by the
        // per-vertex GetBonesPerVertex() counts. Reads sharedMesh (not .mesh), null-checks bones[] entries.
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
        // scale-at-bake, …) logs at WARNING. Same bare "[{label}] REFUSE: {why}" string either way; the label
        // says which door refused, so a bare-door refusal is never read as the seam door's.
        private static string RefuseMisuse(string why, string label = SeamLabel) { var e = "[" + label + "] REFUSE: " + why; Debug.LogError(e); return e; }
        private static string RefuseAbstain(string why, string label = SeamLabel) { var e = "[" + label + "] REFUSE: " + why; Debug.LogWarning(e); return e; }

        // ── Seam defaults (real reflection lands in Tasks 2–3, 7; stubs so the field initializers compile) ─

        // Real reflection: union MA GetBonesMapping (base,merge) + VRCFury GetLinks().mergeBones (merge,base,
        // flipped). Two layers of guard, so nothing escapes: each collector runs under its own try here (catching
        // pre-loop/setup throws — the type/method/field resolution), and inside each collector every component
        // iterates under its own try (catching a per-component throw and continuing the sweep). ClassifyReflect
        // maps each catch — Missing*/TypeLoad ⇒ ReflectError (API drift), everything else (incl. a null GetLinks,
        // which throws NullReferenceException) ⇒ UnresolvableReason (won't resolve onto this base). Validated
        // end-to-end by the live corpus (Task 8), not by unit tests (the SDK-only TestEditor has no MA/VRCFury).
        // CollectVrcfPairs also sets ScaleBakeReason on a scaled bake ⇒ REFUSE.
        internal static SeamResolution ResolveMergeMap(GameObject scopeGO, GameObject avatarGO)
        {
            var res = new SeamResolution();
            try { CollectMaPairs(scopeGO, res); } catch (Exception e) { ClassifyReflect(e, res); }
            try { CollectVrcfPairs(scopeGO, avatarGO, res); } catch (Exception e) { ClassifyReflect(e, res); }
            return res;
        }

        // Reflection surfaces a real throw wrapped in TargetInvocationException — unwrap it. Genuine API drift
        // (the tool broken against the installed package) => ReflectError (misuse/error). A seam that exists but
        // won't resolve onto this base => UnresolvableReason (valid abstain).
        private static void ClassifyReflect(Exception e, SeamResolution res)
        {
            // First carrier wins across BOTH fields. The pre-refactor single outer try aborted on the first
            // throw and set exactly one reason; because the per-collector/per-component guards now continue,
            // a later throw must not upgrade an earlier abstain (UnresolvableReason/warning) to misuse
            // (ReflectError/error). Preserves Check()'s REFUSE severity and removes iteration-order dependence.
            if (res.ReflectError != null || res.UnresolvableReason != null) return;
            var real = (e as System.Reflection.TargetInvocationException)?.InnerException ?? e;
            if (real is MissingMethodException || real is MissingFieldException ||
                real is MissingMemberException || real is TypeLoadException)
                res.ReflectError = real.GetType().Name + ": " + real.Message;
            else
                res.UnresolvableReason = "seam present but does not resolve onto this base (likely an incompatible " +
                    "or independent rig): " + real.GetType().Name + ": " + real.Message;
        }

        private static SeamResolution DefaultResolveSeam(GameObject baseGO, GameObject mergeGO)
            => ResolveMergeMap(mergeGO, baseGO);

        // A ModularAvatar BoneProxy is the anchor-style seam neither pair collector resolves (it maps no
        // humanoid bones), so it lands in the zero-pairs REFUSE — but it is a legitimate offset-tolerant
        // attachment, not a bare prop. Reflected by name (this asmdef has no MA reference). Null type
        // (MA absent) or no component ⇒ false ⇒ treated as bare (the honest floor when we can't confirm one).
        private static bool HasBoneProxy(GameObject mergeGO)
        {
            var t = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarBoneProxy");
            return t != null && mergeGO.GetComponentInChildren(t, true) != null;
        }

        // The seam whose PRESENCE separates a naming mismatch from a bare prop in the zero-pairs REFUSE:
        // CollectMaPairs already ran, so a MergeArmature here means GetBonesMapping() matched nothing.
        // Reflected by name and failing to false on an absent MA, exactly as HasBoneProxy does.
        private static bool HasMergeArmature(GameObject mergeGO)
        {
            var t = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            return t != null && mergeGO.GetComponentInChildren(t, true) != null;
        }

        // MA: ModularAvatarMergeArmature.GetBonesMapping() → List<(Transform base, Transform merge)> (Item1=base).
        // Returns matched descendants only (not the root pair) — that is fine, the descendants carry the offset.
        // NULL is a different answer from empty and must not collapse into it: GetBonesMapping returns null
        // when `mergeTarget.Get(this)` finds nothing, i.e. there is no base to match AGAINST, and the repair is
        // the mergeTarget — not the bone naming the zero-match REFUSE would prescribe. (VRCFury's collector
        // reaches the same abstain by throwing on a null GetLinks; MA hands back null, so it is caught here.)
        private static void CollectMaPairs(GameObject mergeGO, SeamResolution res)
        {
            var maType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            if (maType == null) return; // MA not installed ⇒ no MA seam
            var getMapping = maType.GetMethod("GetBonesMapping", BindingFlags.Public | BindingFlags.Instance);
            if (getMapping == null) throw new MissingMethodException("ModularAvatarMergeArmature.GetBonesMapping");
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

        // VRCFury: for each VF.Model.VRCFury whose `content` is a VF.Model.Feature.ArmatureLink, call
        // VF.Service.ArmatureLinkService.GetLinks(model, avatarObj) (static). Its .mergeBones is a
        // Stack<(VFGameObject prop/merge, VFGameObject avatar/base)> — flipped vs MA. Reflection will NOT auto-
        // apply the implicit Transform↔VFGameObject operators, so op_Implicit is invoked explicitly both ways.
        // GetLinks throws (empty linkTo / link inside armature / bad Hips) AND returns null (propBone == null) —
        // both are resolution failures (thrown → caught upstream; null → thrown here → caught upstream).
        private static void CollectVrcfPairs(GameObject mergeGO, GameObject avatarGO, SeamResolution res)
        {
            var vrcfType = VendorReflect.FindType("VF.Model.VRCFury");
            if (vrcfType == null) return; // VRCFury not installed ⇒ no VRCFury seam
            var armLinkType = VendorReflect.FindType("VF.Model.Feature.ArmatureLink");
            var svcType = VendorReflect.FindType("VF.Service.ArmatureLinkService");
            var vfGoType = VendorReflect.FindType("VF.Utils.VFGameObject");
            if (armLinkType == null || svcType == null || vfGoType == null)
                throw new TypeLoadException("VRCFury ArmatureLink/Service/VFGameObject type missing");

            var getLinks = svcType.GetMethod("GetLinks", BindingFlags.Public | BindingFlags.Static);
            if (getLinks == null) throw new MissingMethodException("ArmatureLinkService.GetLinks");
            var contentField = vrcfType.GetField("content", BindingFlags.Public | BindingFlags.Instance);
            if (contentField == null) throw new MissingFieldException("VRCFury.content");
            // Scale-at-bake detection: forceOneWorldScale (bool field on the ArmatureLink model) OR a non-unit
            // GetScalingFactor Item3. A scaled bake makes edit-time world-position coincidence meaningless (the
            // baker rescales the whole prop), so we can't certify from the edit-time pose — REFUSE (abstain).
            var forceField = armLinkType.GetField("forceOneWorldScale", BindingFlags.Public | BindingFlags.Instance);
            if (forceField == null) throw new MissingFieldException("ArmatureLink.forceOneWorldScale");
            var getScaling = svcType.GetMethod("GetScalingFactor", BindingFlags.Public | BindingFlags.Static);
            if (getScaling == null) throw new MissingMethodException("ArmatureLinkService.GetScalingFactor");

            var avatarVfGo = ToVfGameObject(vfGoType, avatarGO);

            foreach (var comp in mergeGO.GetComponentsInChildren(vrcfType, true))
            {
                try
                {
                    var content = contentField.GetValue(comp);
                    if (content == null || !armLinkType.IsInstanceOfType(content)) continue; // not an ArmatureLink feature
                    var links = getLinks.Invoke(null, new object[] { content, avatarVfGo });
                    if (links == null) throw new NullReferenceException("GetLinks returned null (propBone == null)");

                    if (res.ScaleBakeReason == null) // GetScalingFactor(model, links) → (float,float,float); Item3 = factor
                    {
                        bool forceOne = (bool)forceField.GetValue(content);
                        var factorTuple = getScaling.Invoke(null, new object[] { content, links });
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
                        res.Pairs.Add(new BonePair { Base = FromVfGameObject(vfGoType, baseVf), Merge = FromVfGameObject(vfGoType, mergeVf) });
                    }
                }
                catch (Exception e) { ClassifyReflect(e, res); }
            }
        }

        // Transform/GameObject → VFGameObject via the explicit op_Implicit (reflection won't apply it implicitly).
        private static object ToVfGameObject(Type vfGoType, GameObject go)
        {
            foreach (var m in vfGoType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "op_Implicit" || m.ReturnType != vfGoType) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                if (ps[0].ParameterType == typeof(Transform)) return m.Invoke(null, new object[] { go.transform });
                if (ps[0].ParameterType == typeof(GameObject)) return m.Invoke(null, new object[] { go });
            }
            throw new MissingMethodException("op_Implicit(Transform|GameObject) → VFGameObject");
        }

        // VFGameObject → Transform via the explicit op_Implicit (either the Transform or the GameObject operator).
        private static Transform FromVfGameObject(Type vfGoType, object vfGo)
        {
            if (vfGo == null) return null;
            foreach (var m in vfGoType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "op_Implicit") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != vfGoType) continue;
                var result = m.Invoke(null, new object[] { vfGo });
                if (result is Transform t) return t;
                if (result is GameObject g) return g.transform;
            }
            throw new MissingMethodException("op_Implicit(VFGameObject) → Transform|GameObject");
        }

        private static HumanoidMap DefaultResolveHumanoid(GameObject baseGO)
        {
            var map = new HumanoidMap();
            var anim = baseGO.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.isHuman) return map; // empty ⇒ REFUSE upstream
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

        // ── Scene resolver (path → instance id → name; copied verbatim from CheckAvatar.Resolve) ────────

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
