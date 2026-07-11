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
    /// Scene-scoped SEAM gate: mechanically certify whether a mergeable's humanoid skeleton coincides with a
    /// base's before any render — <c>PASS</c> / <c>NOT-PASS</c> / bare <c>REFUSE</c>. It scores position, not
    /// intent: it reflects the seam mapping (Modular Avatar <c>GetBonesMapping</c> / VRCFury
    /// <c>ArmatureLinkService.GetLinks</c>), counts weighted humanoid bones, and gates on world-position
    /// coincidence at an ε tolerance.
    ///
    /// Pure-core + injectable seams (mirrors <see cref="CheckAvatar"/>): the door resolves two scene roots and
    /// calls the <see cref="ResolveHumanoid"/> / <see cref="ResolveSeam"/> seams (defaults do the real
    /// reflection; tests swap fakes) then a pure scoring core. This is the Task 1 skeleton: door + dual-root
    /// resolve + bare refusal; the humanoid map, seam mapping, count/gate, and Emit land in later tasks.
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
            public string ReflectError;                         // non-null ⇒ a reflective hop failed ⇒ REFUSE
        }

        internal class HumanoidMap
        {
            public HashSet<Transform> Bones = new HashSet<Transform>(); // empty ⇒ base not humanoid ⇒ REFUSE
            public float SpanMm;                                        // Hips→Head world distance, mm (for ε)
        }

        // ── Injectable seams (default = real reflection; tests swap fakes) ─────────────────────────────
        internal static Func<GameObject, GameObject, SeamResolution> ResolveSeam = DefaultResolveSeam;
        internal static Func<GameObject, HumanoidMap> ResolveHumanoid = DefaultResolveHumanoid;

        // ── Door ──────────────────────────────────────────────────────────────────────────────────────

        public static string Check(string baseRoot, string mergeableRoot)
        {
            var baseGO = Resolve(baseRoot);
            if (baseGO == null) return Refuse("base root '" + baseRoot + "' not found in the active scene");
            var mergeGO = Resolve(mergeableRoot);
            if (mergeGO == null) return Refuse("mergeable root '" + mergeableRoot + "' not found in the active scene");

            var human = ResolveHumanoid(baseGO);
            if (human.Bones.Count == 0)
                return Refuse("base '" + baseRoot + "' has no humanoid Avatar — cannot certify fit (clothes-on-a-body is the domain)");

            var seam = ResolveSeam(baseGO, mergeGO);
            if (seam.ReflectError != null) return Refuse("seam resolution failed: " + seam.ReflectError);
            if (seam.ScaleBakeReason != null) return Refuse(seam.ScaleBakeReason); // field stays null until Task 7
            if (seam.Pairs.Count == 0) return Refuse("no scorable seam component on '" + mergeableRoot + "'");
            foreach (var p in seam.Pairs)
            {
                if (p.Base == null || p.Merge == null) return Refuse("seam pair has a null bone");
                if (!IsUnder(p.Base, baseGO) || !IsUnder(p.Merge, mergeGO))
                    return Refuse("seam targets a different avatar (a mapped bone is not under its root)");
            }
            // conflict: the same base bone mapped to two different merge bones (MA and VRCFury disagree)
            var byBase = new Dictionary<Transform, Transform>();
            foreach (var p in seam.Pairs)
            {
                if (byBase.TryGetValue(p.Base, out var other) && other != p.Merge)
                    return Refuse("seams disagree on base bone '" + p.Base.name + "' (" + other.name + " vs " + p.Merge.name + ")");
                byBase[p.Base] = p.Merge;
            }

            // Count weighted humanoid bones: a pair qualifies iff its BASE is humanoid and a mergeable SMR skins
            // its MERGE side at ≥ WEIGHT. Join on the merge side — SMR.bones[] reference the merge transforms.
            var maxW = MaxWeights(mergeGO);
            const float WEIGHT = 0.1f;
            var weightedHum = new List<BonePair>();
            foreach (var p in seam.Pairs)
                if (human.Bones.Contains(p.Base) && maxW.TryGetValue(p.Merge, out var wt) && wt >= WEIGHT)
                    weightedHum.Add(p);

            if (weightedHum.Count <= 1) // offset-independent bone-proxy (hair/earring/hat/tail) or bare prop
            {
                string bone = weightedHum.Count == 1 ? weightedHum[0].Base.name : "(none)";
                float d = weightedHum.Count == 1
                    ? Vector3.Distance(weightedHum[0].Base.position, weightedHum[0].Merge.position) * 1000f
                    : 0f;
                return Refuse("single humanoid attachment: " + bone + ", delta=" + d.ToString("F1", CultureInfo.InvariantCulture) +
                    "mm — offset-tolerant accessory/proxy, verify the baked result");
            }

            // ≥2 weighted humanoid ⇒ coincidence gate: compare edit-time WORLD positions at ε tolerance.
            float eps = Mathf.Max(0.5f, 0.002f * human.SpanMm);
            var hipsBase = HipsOf(baseGO); // fromHips anchor; null ⇒ report 0 (robust, non-load-bearing)
            var offenders = new List<(string bone, float mm, float fromHips)>();
            foreach (var p in weightedHum)
            {
                float mm = Vector3.Distance(p.Base.position, p.Merge.position) * 1000f;
                if (mm > eps)
                {
                    float fromHips = hipsBase != null ? Vector3.Distance(hipsBase.position, p.Base.position) * 1000f : 0f;
                    offenders.Add((p.Base.name, mm, fromHips));
                }
            }
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
            foreach (var p in seam.Pairs)
            {
                if (human.Bones.Contains(p.Base)) continue;                          // humanoid ⇒ the gate above
                if (!maxW.TryGetValue(p.Merge, out var wt) || wt < WEIGHT) continue; // unweighted ⇒ ignore
                if (IsLeaf(p.Merge)) { dropped++; continue; }                        // end-bone ⇒ count only
                float mm = Vector3.Distance(p.Base.position, p.Merge.position) * 1000f;
                float fromHips = hipsBase != null ? Vector3.Distance(hipsBase.position, p.Base.position) * 1000f : 0f;
                context.Add((p.Base.name, mm, fromHips));
            }
            context.Sort((a, b) => b.mm.CompareTo(a.mm)); // worst (largest offset) first
            return Emit(baseGO, mergeGO, weightedHum.Count, offenders, context, dropped, eps);
        }

        // ── Output (mirrors CheckAvatar.Emit: summary + markdown body + WriteRunLog + severity-by-verdict) ─
        // Verdict is a pure function of the humanoid offender count — context (k) and dropped (d) are the
        // non-humanoid partition (ungated) and NEVER shift PASS/NOT-PASS; they ride the summary + body so a
        // PASS beside wild context deltas doesn't read like a clean one.
        private static string Emit(GameObject baseGO, GameObject mergeGO, int weightedCount,
            List<(string bone, float mm, float fromHips)> offenders,
            List<(string bone, float mm, float fromHips)> context, int dropped, float eps)
        {
            int m = offenders.Count, k = context.Count;
            string verdict = m == 0 ? "PASS" : "NOT-PASS";

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckSeam] {0}→{1}: weightedHumanoid={2} offenders={3} context={4} dropped={5} => {6}",
                mergeGO.name, baseGO.name, weightedCount, m, k, dropped, verdict);

            var sb = new StringBuilder();
            sb.Append("# CheckSeam: ").Append(mergeGO.name).Append(" → ").Append(baseGO.name).Append('\n');
            sb.Append("mergeable: `").Append(PathOf(mergeGO)).Append("`  \n");
            sb.Append("base: `").Append(PathOf(baseGO)).Append("`  \n\n");
            sb.Append(summary.Substring("[CheckSeam] ".Length)).Append('\n');

            sb.Append("\n## Gate — weighted humanoid bones (ε=")
              .Append(eps.ToString("F2", CultureInfo.InvariantCulture)).Append("mm)\n\n");
            if (offenders.Count == 0) sb.Append("_(all within ε)_\n");
            else foreach (var o in offenders)
                sb.Append("- **seam-offset** bone=`").Append(o.bone)
                  .Append("` offset=").Append(o.mm.ToString("F1", CultureInfo.InvariantCulture))
                  .Append("mm fromHips=").Append(o.fromHips.ToString("F1", CultureInfo.InvariantCulture))
                  .Append("mm\n");

            sb.Append("\n## Context — non-humanoid weighted bones (ungated; interpret in context)\n\n");
            if (context.Count == 0) sb.Append("_(none)_\n");
            else foreach (var c in context)
                sb.Append("- bone=`").Append(c.bone)
                  .Append("` offset=").Append(c.mm.ToString("F1", CultureInfo.InvariantCulture))
                  .Append("mm fromHips=").Append(c.fromHips.ToString("F1", CultureInfo.InvariantCulture))
                  .Append("mm\n");

            sb.Append("\nDropped: ").Append(dropped)
              .Append(" non-humanoid end-bones (physbone/collider tuning)\n");

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "checkseam_" + mergeGO.name, summary, sb.ToString(), ".md");
            if (verdict == "PASS") Debug.Log(res); else Debug.LogWarning(res);
            return res;
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

        // Merge Transform → max vertex weight across every mergeable SMR (top-4 influences per vertex). Reads
        // sharedMesh (not .mesh), null-checks bones[] entries. Keyed on the merge-side transforms the pairs carry.
        private static Dictionary<Transform, float> MaxWeights(GameObject mergeGO)
        {
            var w = new Dictionary<Transform, float>();
            foreach (var smr in mergeGO.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh; if (mesh == null) continue;
                var bones = smr.bones; var bw = mesh.boneWeights;
                void Acc(int idx, float wt)
                {
                    if (idx < 0 || idx >= bones.Length || bones[idx] == null) return;
                    var t = bones[idx];
                    if (!w.TryGetValue(t, out var cur) || wt > cur) w[t] = wt;
                }
                foreach (var v in bw)
                {
                    Acc(v.boneIndex0, v.weight0); Acc(v.boneIndex1, v.weight1);
                    Acc(v.boneIndex2, v.weight2); Acc(v.boneIndex3, v.weight3);
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

        private static string Refuse(string why) // valid-abstain vs misuse severity handled in Task 8
        {
            string err = "[CheckSeam] REFUSE: " + why;
            Debug.LogError(err);
            return err;
        }

        // ── Seam defaults (real reflection lands in Tasks 2–3, 7; stubs so the field initializers compile) ─

        // Real reflection: union MA GetBonesMapping (base,merge) + VRCFury GetLinks().mergeBones (merge,base,
        // flipped). Wrap the whole body — any thrown hop OR a null GetLinks becomes ReflectError, never an
        // escaping exception. Validated end-to-end by the live corpus (Task 8), not by unit tests (the SDK-only
        // TestEditor has no MA/VRCFury). Scale detection (ScaleBakeReason) is Task 7 — not read here.
        private static SeamResolution DefaultResolveSeam(GameObject baseGO, GameObject mergeGO)
        {
            var res = new SeamResolution();
            try
            {
                CollectMaPairs(mergeGO, res);
                CollectVrcfPairs(mergeGO, baseGO, res);
            }
            catch (Exception e)
            {
                res.ReflectError = e.GetType().Name + ": " + e.Message;
            }
            return res;
        }

        private static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == fullName);

        // MA: ModularAvatarMergeArmature.GetBonesMapping() → List<(Transform base, Transform merge)> (Item1=base).
        // Returns matched descendants only (not the root pair) — that is fine, the descendants carry the offset.
        private static void CollectMaPairs(GameObject mergeGO, SeamResolution res)
        {
            var maType = FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            if (maType == null) return; // MA not installed ⇒ no MA seam
            var getMapping = maType.GetMethod("GetBonesMapping", BindingFlags.Public | BindingFlags.Instance);
            if (getMapping == null) throw new MissingMethodException("ModularAvatarMergeArmature.GetBonesMapping");
            foreach (var comp in mergeGO.GetComponentsInChildren(maType, true))
            {
                var mapping = getMapping.Invoke(comp, null) as System.Collections.IEnumerable;
                if (mapping == null) continue;
                foreach (var item in mapping)
                {
                    var tt = item.GetType();
                    var b = tt.GetField("Item1").GetValue(item) as Transform;
                    var m = tt.GetField("Item2").GetValue(item) as Transform;
                    res.Pairs.Add(new BonePair { Base = b, Merge = m });
                }
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
            var vrcfType = FindType("VF.Model.VRCFury");
            if (vrcfType == null) return; // VRCFury not installed ⇒ no VRCFury seam
            var armLinkType = FindType("VF.Model.Feature.ArmatureLink");
            var svcType = FindType("VF.Service.ArmatureLinkService");
            var vfGoType = FindType("VF.Utils.VFGameObject");
            if (armLinkType == null || svcType == null || vfGoType == null)
                throw new TypeLoadException("VRCFury ArmatureLink/Service/VFGameObject type missing");

            var getLinks = svcType.GetMethod("GetLinks", BindingFlags.Public | BindingFlags.Static);
            if (getLinks == null) throw new MissingMethodException("ArmatureLinkService.GetLinks");
            var contentField = vrcfType.GetField("content", BindingFlags.Public | BindingFlags.Instance);
            if (contentField == null) throw new MissingFieldException("VRCFury.content");

            var avatarVfGo = ToVfGameObject(vfGoType, avatarGO);

            foreach (var comp in mergeGO.GetComponentsInChildren(vrcfType, true))
            {
                var content = contentField.GetValue(comp);
                if (content == null || !armLinkType.IsInstanceOfType(content)) continue; // not an ArmatureLink feature
                var links = getLinks.Invoke(null, new object[] { content, avatarVfGo });
                if (links == null) throw new NullReferenceException("GetLinks returned null (propBone == null)");
                var mergeBones = links.GetType().GetField("mergeBones").GetValue(links) as System.Collections.IEnumerable;
                if (mergeBones == null) continue;
                foreach (var pair in mergeBones)
                {
                    var pt = pair.GetType();
                    var mergeVf = pt.GetField("Item1").GetValue(pair); // prop/merge
                    var baseVf = pt.GetField("Item2").GetValue(pair);  // avatar/base
                    res.Pairs.Add(new BonePair { Base = FromVfGameObject(vfGoType, baseVf), Merge = FromVfGameObject(vfGoType, mergeVf) });
                }
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
