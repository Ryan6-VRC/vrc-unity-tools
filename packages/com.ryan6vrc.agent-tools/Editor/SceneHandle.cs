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
        /// <summary>The handle names more than one object. Refusing beats picking: a handle that can pick the
        /// wrong object is wrong for a read door too, just more quietly than for a writer.</summary>
        Ambiguous,
        /// <summary>An instance id that resolves to an object outside the searched domain — a prefab asset, or
        /// an object in prefab isolation / a preview scene. Only the id rung can reach these.</summary>
        OutOfDomain,
    }

    internal struct SceneHandleResult
    {
        public SceneHandleOutcome Outcome;
        /// <summary>Set on <see cref="SceneHandleOutcome.Found"/> only.</summary>
        public GameObject Object;
        /// <summary>Set on every non-Found outcome, minted here rather than by the calling door: agent-facing
        /// strings are born at a closed set of carriers (`tool-design.md` §Diagnostics are governed prose), and
        /// twelve doors each formatting a match list is twelve echoes of one diagnostic. A door prefixes its own
        /// envelope and adds no prose of its own.</summary>
        public string Refusal;

        public bool Ok { get { return Outcome == SceneHandleOutcome.Found; } }
    }

    /// <summary>The one string→scene-GameObject resolver behind every string scene handle in `agent-tools` and
    /// `avatar-tools`. Ladder: hierarchy path → instance id → bare name, each rung trying the ACTIVE scene alone
    /// before widening to the other loaded scenes.
    ///
    /// <para>Two measured facts pin the domain, neither obvious from the API:</para>
    /// <list type="bullet">
    /// <item>A preview scene (NDMF's, and a Prefab Stage's) reports <c>IsValid()==true</c> and
    /// <c>isLoaded==true</c>, but is NOT returned by <see cref="SceneManager.GetSceneAt"/> — Unity counts those
    /// separately in <c>EditorSceneManager.previewSceneCount</c>. So enumerating buys the exclusion structurally;
    /// an <c>isLoaded</c> filter is emphatically NOT what buys it, and adding one would read as if it were.</item>
    /// <item><see cref="EditorUtility.InstanceIDToObject"/> is not scene-scoped, so the id rung is the only one
    /// that can reach out of the domain — which is why it alone returns <see cref="SceneHandleOutcome.OutOfDomain"/>.</item>
    /// </list>
    ///
    /// <para>Not here: resolving a path RELATIVE to a known root (`RenderAvatar.ResolveDescendant`). That is a
    /// different question with one caller, and folding it in would widen this surface for nobody.</para></summary>
    internal static class SceneHandle
    {
        internal static SceneHandleResult Resolve(string handle)
        {
            return Resolve(handle, Domain());
        }

        /// <summary>Domain-explicit overload. The test seam: a fixture cannot add a second scene to the venue's
        /// domain (`NewScene(Additive)` throws on the batchmode venue's unsaved untitled scene, and a preview
        /// scene does not enumerate), so multi-scene behaviour is only reachable by passing the domain in.</summary>
        internal static SceneHandleResult Resolve(string handle, IReadOnlyList<Scene> domain)
        {
            if (string.IsNullOrEmpty(handle))
                return Refuse(SceneHandleOutcome.NotFound, "empty scene handle: pass a hierarchy path, an instance id, or an object name.");

            var active = SceneManager.GetActiveScene();

            // Ladder order is path → id → name, preserved from the twelve copies this replaces. The two
            // deviations both exist to keep a refusal from dead-ending: an Ambiguous path falls through to the
            // id rung (otherwise a handle that is BOTH a duplicated name and an instance id refuses while
            // advising the very handle it refused), and an out-of-domain id yields to an Ambiguous path, whose
            // message is the more actionable of the two.
            var byPath = ByPath(handle, domain, active);
            if (byPath.Ok) return byPath;

            var byId = ById(handle, domain);
            if (byId.Ok) return byId;
            if (byPath.Outcome == SceneHandleOutcome.Ambiguous) return byPath;
            if (byId.Outcome == SceneHandleOutcome.OutOfDomain) return byId;

            var byName = ByName(handle, domain, active);
            if (byName.Ok || byName.Outcome == SceneHandleOutcome.Ambiguous) return byName;

            return Refuse(SceneHandleOutcome.NotFound,
                "'" + handle + "' not found in " + DescribeDomain(domain)
                + " — tried hierarchy path, instance id, then object name.");
        }

        // ── Domain ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every loaded scene. <c>IsValid()</c> is not belt-and-braces: <c>Scene.GetRootGameObjects()</c>
        /// throws <c>ArgumentException</c> on an invalid or unloaded scene.</summary>
        internal static List<Scene> Domain()
        {
            var scenes = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded) scenes.Add(s);
            }
            return scenes;
        }

        /// <summary>Roots a handle may name. <c>HideInHierarchy</c> roots are excluded because the operator
        /// cannot see or act on them: in play the emulator parks a full hidden copy of the avatar per runtime
        /// (source clone, mirror reflection, shadow clone), which would otherwise make every bare name under an
        /// avatar ambiguous against objects that are not in the Hierarchy window.</summary>
        private static IEnumerable<Transform> Roots(Scene s)
        {
            foreach (var go in s.GetRootGameObjects())
                if ((go.hideFlags & HideFlags.HideInHierarchy) == 0) yield return go.transform;
        }

        // ── Rung 1: hierarchy path ────────────────────────────────────────────────────────────────────────

        private static SceneHandleResult ByPath(string handle, IReadOnlyList<Scene> domain, Scene active)
        {
            var segs = handle.Trim('/').Split('/');
            return ActiveFirst(domain, active, scenes =>
            {
                var hits = new List<GameObject>();
                foreach (var s in scenes)
                    foreach (var root in Roots(s))
                        if (root.name == segs[0]) Descend(root, segs, 1, hits);
                return hits;
            }, handle, "hierarchy path");
        }

        /// <summary>Branching descent. Never <c>Transform.Find</c>: it returns the FIRST same-named child, so a
        /// path walked with it is a silent coin-flip at every depth — and duplicate-named siblings are the
        /// ordinary composed-avatar shape (an unmerged outfit carries its own Armature/Hips beside the base's).
        /// Collecting the whole frontier is what makes the ambiguity refusal true of paths and not just names.</summary>
        private static void Descend(Transform t, string[] segs, int depth, List<GameObject> into)
        {
            if (depth == segs.Length) { into.Add(t.gameObject); return; }
            foreach (Transform child in t)
                if (child.name == segs[depth]) Descend(child, segs, depth + 1, into);
        }

        // ── Rung 2: instance id ───────────────────────────────────────────────────────────────────────────

        private static SceneHandleResult ById(string handle, IReadOnlyList<Scene> domain)
        {
            int id;
            if (!int.TryParse(handle.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };

            var obj = EditorUtility.InstanceIDToObject(id);
            GameObject go = obj as GameObject;
            if (go == null) { var comp = obj as Component; if (comp != null) go = comp.gameObject; }
            if (go == null) return new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };

            foreach (var s in domain) if (go.scene == s) return new SceneHandleResult { Outcome = SceneHandleOutcome.Found, Object = go };

            // Split by cause: a prefab ASSET is on disk and has no scene at all, while a prefab stage or a
            // preview scene is a live scene the enumeration deliberately omits. One message for both would send
            // the reader looking in a scene for something that is a file, or vice versa.
            return Refuse(SceneHandleOutcome.OutOfDomain, go.scene.IsValid()
                ? "instance id " + id + " ('" + go.name + "') is in prefab isolation or another preview scene — grab from a loaded scene."
                : "instance id " + id + " ('" + go.name + "') is inside a prefab asset, not a loaded scene — place it, or pass an asset path to a door that takes one.");
        }

        // ── Rung 3: bare name ─────────────────────────────────────────────────────────────────────────────

        private static SceneHandleResult ByName(string handle, IReadOnlyList<Scene> domain, Scene active)
        {
            return ActiveFirst(domain, active, scenes =>
            {
                var hits = new List<GameObject>();
                foreach (var s in scenes)
                    foreach (var root in Roots(s)) CollectByName(root, handle, hits);
                return hits;
            }, handle, "object name");
        }

        private static void CollectByName(Transform t, string name, List<GameObject> into)
        {
            if (t.name == name) into.Add(t.gameObject);
            foreach (Transform child in t) CollectByName(child, name, into);
        }

        // ── Active-scene-first ────────────────────────────────────────────────────────────────────────────

        /// <summary>Run a rung over the active scene alone, and only widen to the other loaded scenes when the
        /// active scene names nothing. Two things this buys, both load-bearing: a handle that resolves today
        /// keeps resolving to the same object (the active scene is searched first and wins outright), and the
        /// agent gets a DURABLE way to say which scene it means — make that scene active — where an instance id
        /// is minted per session and cannot be carried in a skill line, a RunLog, or a handoff. Ambiguity inside
        /// the active scene refuses rather than widening: the handle is already wrong where the work is.</summary>
        private static SceneHandleResult ActiveFirst(
            IReadOnlyList<Scene> domain, Scene active, System.Func<IEnumerable<Scene>, List<GameObject>> run,
            string handle, string rung)
        {
            var activeOnly = new List<Scene>();
            var rest = new List<Scene>();
            foreach (var s in domain) { if (s == active) activeOnly.Add(s); else rest.Add(s); }

            var hits = activeOnly.Count > 0 ? run(activeOnly) : new List<GameObject>();
            if (hits.Count == 0 && rest.Count > 0) hits = run(rest);

            if (hits.Count == 1) return new SceneHandleResult { Outcome = SceneHandleOutcome.Found, Object = hits[0] };
            if (hits.Count == 0) return new SceneHandleResult { Outcome = SceneHandleOutcome.NotFound };

            var sb = new StringBuilder();
            sb.Append("'").Append(handle).Append("' matches ").Append(hits.Count).Append(" objects by ").Append(rung).Append(": ");
            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Describe(hits[i]));
            }
            sb.Append(" — pass one hierarchy path, or the instance id of the one you mean.");
            return Refuse(SceneHandleOutcome.Ambiguous, sb.ToString());
        }

        // ── Rendering ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Scene-qualified path plus instance id. The qualifier is a prefix, never inserted between path
        /// segments, so the path a caller passed remains a substring of what the refusal echoes back.</summary>
        private static string Describe(GameObject go)
        {
            return "[" + go.scene.name + "] " + MergeSurfaces.PathOf(go) + " (id " + go.GetInstanceID() + ")";
        }

        private static string DescribeDomain(IReadOnlyList<Scene> domain)
        {
            if (domain.Count == 0) return "any loaded scene (none are loaded)";
            if (domain.Count == 1) return "scene '" + domain[0].name + "'";
            var sb = new StringBuilder("the ").Append(domain.Count).Append(" loaded scenes (");
            for (int i = 0; i < domain.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(domain[i].name); }
            return sb.Append(")").ToString();
        }

        private static SceneHandleResult Refuse(SceneHandleOutcome outcome, string refusal)
        {
            return new SceneHandleResult { Outcome = outcome, Refusal = refusal };
        }
    }
}
