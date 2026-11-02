using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    internal enum SceneHandleOutcome
    {
        Found,
        NotFound,
        /// <summary>The handle names more than one object.</summary>
        Ambiguous,
        /// <summary>An instance id that resolves outside the active scene: a prefab asset, or an object in prefab
        /// isolation, a preview scene or another loaded scene. Only the id rung can reach these, because
        /// <see cref="EditorUtility.InstanceIDToObject"/> is not scene-scoped.</summary>
        OutOfDomain,
    }

    internal struct SceneHandleResult
    {
        public SceneHandleOutcome Outcome;
        /// <summary>Set on <see cref="SceneHandleOutcome.Found"/> only.</summary>
        public GameObject Object;
        /// <summary>Set on every non-Found outcome. A door prefixes its own envelope and adds no prose of its own.</summary>
        public string Refusal;

        public bool Ok { get { return Outcome == SceneHandleOutcome.Found; } }
    }

    /// <summary>Resolves a door's string scene handle, in both packages, over the active scene: hierarchy path,
    /// then instance id, then bare name.</summary>
    internal static class SceneHandle
    {
        internal static SceneHandleResult Resolve(string handle)
        {
            handle = handle == null ? null : handle.Trim();
            if (string.IsNullOrEmpty(handle))
                return Refuse(SceneHandleOutcome.NotFound, "empty scene handle: pass a hierarchy path, an instance id, or an object name.");

            var scene = SceneManager.GetActiveScene();

            // An Ambiguous path falls through to the id rung: otherwise a handle that is both a duplicated name
            // and an instance id refuses while advising the very handle it refused. An out-of-domain id then
            // yields to that Ambiguous path, whose message is the more actionable of the two.
            var byPath = ByPath(handle, scene);
            if (byPath.Ok) return byPath;

            var byId = ById(handle, scene);
            if (byId.Ok) return byId;
            if (byPath.Outcome == SceneHandleOutcome.Ambiguous) return byPath;
            if (byId.Outcome == SceneHandleOutcome.OutOfDomain) return byId;

            var byName = ByName(handle, scene);
            if (byName.Ok || byName.Outcome == SceneHandleOutcome.Ambiguous) return byName;

            return Refuse(SceneHandleOutcome.NotFound,
                "'" + handle + "' not found in scene '" + scene.name + "'"
                + " — tried hierarchy path, instance id, then object name.");
        }

        private static SceneHandleResult ByPath(string handle, Scene scene)
        {
            var segs = handle.Trim('/').Split('/');
            var hits = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == segs[0]) Descend(root.transform, segs, 1, hits);
            return Decide(hits, handle, "hierarchy path");
        }

        /// <summary>Never <c>Transform.Find</c>: it returns the first same-named child, and duplicate-named siblings
        /// are the ordinary composed-avatar shape (an unmerged outfit carries its own Armature/Hips beside the
        /// base's). Collecting the whole frontier is what lets a path refuse as ambiguous.</summary>
        private static void Descend(Transform t, string[] segs, int depth, List<GameObject> into)
        {
            if (depth == segs.Length) { into.Add(t.gameObject); return; }
            foreach (Transform child in t)
                if (child.name == segs[depth]) Descend(child, segs, depth + 1, into);
        }

        private static SceneHandleResult ById(string handle, Scene scene)
        {
            int id;
            if (!int.TryParse(handle, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };

            var obj = EditorUtility.InstanceIDToObject(id);
            GameObject go = obj as GameObject;
            if (go == null) { var comp = obj as Component; if (comp != null) go = comp.gameObject; }
            if (go == null) return new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };

            if (go.scene == scene) return new SceneHandleResult { Outcome = SceneHandleOutcome.Found, Object = go };

            // NDMF's preview scene appears in SceneManager's enumeration (a real additively-opened scene under
            // Packages/, unlike EditorSceneManager.NewPreviewScene()), so a search wider than the active scene
            // would take it in.
            return Refuse(SceneHandleOutcome.OutOfDomain, go.scene.IsValid()
                ? "instance id " + id + " ('" + go.name + "') is not in the active scene (prefab isolation, a preview scene, or another loaded scene) — open or activate the scene holding it."
                : "instance id " + id + " ('" + go.name + "') is inside a prefab asset, not a loaded scene — place it, or pass an asset path to a door that takes one.");
        }

        private static SceneHandleResult ByName(string handle, Scene scene)
        {
            var hits = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects()) CollectByName(root.transform, handle, hits);
            return Decide(hits, handle, "object name");
        }

        private static void CollectByName(Transform t, string name, List<GameObject> into)
        {
            if (t.name == name) into.Add(t.gameObject);
            foreach (Transform child in t) CollectByName(child, name, into);
        }

        private static SceneHandleResult Decide(List<GameObject> hits, string handle, string rung)
        {
            if (hits.Count == 1) return new SceneHandleResult { Outcome = SceneHandleOutcome.Found, Object = hits[0] };
            if (hits.Count == 0) return new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };

            var sb = new StringBuilder();
            sb.Append("'").Append(handle).Append("' matches ").Append(hits.Count).Append(" objects by ").Append(rung).Append(": ");
            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Describe(hits[i]));
            }
            bool pathsDiffer = false;
            for (int i = 1; i < hits.Count && !pathsDiffer; i++)
                if (MergeSurfaces.PathOf(hits[i]) != MergeSurfaces.PathOf(hits[0])) pathsDiffer = true;
            sb.Append(pathsDiffer
                ? " — pass one hierarchy path, or the instance id of the one you mean."
                : " — these share a path, so pass the instance id of the one you mean.");
            return Refuse(SceneHandleOutcome.Ambiguous, sb.ToString());
        }

        private static string Describe(GameObject go)
        {
            return MergeSurfaces.PathOf(go) + " (id " + go.GetInstanceID() + ")";
        }

        private static SceneHandleResult Refuse(SceneHandleOutcome outcome, string refusal)
        {
            return new SceneHandleResult { Outcome = outcome, Refusal = refusal };
        }
    }
}
