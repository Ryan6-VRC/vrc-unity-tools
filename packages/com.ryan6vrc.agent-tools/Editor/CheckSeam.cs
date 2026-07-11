using System;
using System.Collections.Generic;
using System.Globalization;
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
            return Refuse("not yet implemented"); // replaced in later tasks
        }

        private static string Refuse(string why) // valid-abstain vs misuse severity handled in Task 8
        {
            string err = "[CheckSeam] REFUSE: " + why;
            Debug.LogError(err);
            return err;
        }

        // ── Seam defaults (real reflection lands in Tasks 2–3, 7; stubs so the field initializers compile) ─

        private static SeamResolution DefaultResolveSeam(GameObject b, GameObject m) => throw new NotImplementedException();
        private static HumanoidMap DefaultResolveHumanoid(GameObject b) => throw new NotImplementedException();

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
