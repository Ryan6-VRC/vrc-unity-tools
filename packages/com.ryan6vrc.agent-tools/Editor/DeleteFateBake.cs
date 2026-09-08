using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// <see cref="ReportDeleteFate"/>'s bake half: build the clone, read the fates off it, write the artifact.
    ///
    /// <b>Two-phase, by measurement</b> — identical to <see cref="CompositionBake"/> and for the same reason: a
    /// full preprocess chain outruns the MCP transport's patience, and a lost reply on a synchronous call
    /// discards a result the editor actually produced. <see cref="Begin"/> writes the artifact path FIRST,
    /// schedules the work off <c>EditorApplication.update</c> (never <c>delayCall</c>, which queues indefinitely
    /// in an unfocused editor), and returns <c>PENDING</c>; <see cref="Verify"/> re-reads it. The transport
    /// re-sends a timed-out payload, so <see cref="Begin"/> is idempotent while a bake is in flight.
    ///
    /// <para><b>What is measured versus what is joined.</b> <c>NaNimation</c> is measured outright: MA names the
    /// bone it inserts after the exact key the row generates, so a matching bone on the clone IS the fate, and
    /// the controlling parameters come off the generating layer's own transition conditions. The other three
    /// fates share one clone-side observation — no such bone — and are separated by predicates that are
    /// definite rather than inferred: whether a later <c>Set</c> declares the same key (authoring, and it is the
    /// whole of MA's cancel rule), and whether any clip in the BUILT animator set binds the parked ancestor's
    /// <c>m_IsActive</c>, which is MA's constancy test read off the same index MA read. The build-time arm is
    /// the residue, corroborated where the clone's renderer still name-resolves by the vertex count having
    /// dropped — corroboration, never the discriminator, because an optimizer may have merged the renderer
    /// away and a missing corroboration would then read as a false finding. Every row says which of these it
    /// rests on.</para>
    /// </summary>
    internal static class DeleteFateBake
    {
        private const string Pending = "pending";
        /// <summary>Matches <see cref="CompositionBake"/>'s: well past the worst case measured on a complex
        /// avatar with an optimizer installed, short enough that a discarded callback is not a long wedge.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
        private static readonly Dictionary<int, bool> InFlight = new Dictionary<int, bool>();

        // Keyed on name AND instance id, for CompositionBake's reason: the path must be STABLE across a
        // transport retry, and two roots named alike in one scene must not share a file.
        private static string ArtifactPath(GameObject root) =>
            RunLogFormat.SnapshotDir + "/delete-fate_" + RunLogFormat.Sanitize(root.name)
            + "_" + root.GetInstanceID().ToString("X", CultureInfo.InvariantCulture) + ".md";

        internal static string Begin(GameObject root, ReportDeleteFate.Census census, int triangleBudget)
        {
            string path = ArtifactPath(root);
            int key = root.GetInstanceID();
            if (InFlight.TryGetValue(key, out bool running) && running)
                return "[ReportDeleteFate] " + root.name + ": => PENDING (already running) | log=" + path;

            WriteArtifact(path, "# ReportDeleteFate: " + root.name + "\n\nstatus: " + Pending
                + "\nstarted: " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                + "\n\nThe bake is running. Re-read this file, or call `ReportDeleteFate.Verify(<avatarRoot>)`.\n"
                + "Do NOT re-issue the run: a second call while this one is in flight returns this same path.\n");
            InFlight[key] = true;

            var rootRef = root;
            EditorApplication.CallbackFunction step = null;
            step = () =>
            {
                EditorApplication.update -= step;
                try { Run(rootRef, census, triangleBudget, path); }
                finally { InFlight[key] = false; }
            };
            EditorApplication.update += step;

            return "[ReportDeleteFate] " + root.name + ": declared=" + census.Rows.Count
                 + " budget=" + triangleBudget.ToString(CultureInfo.InvariantCulture) + " => PENDING | log=" + path;
        }

        internal static string Verify(GameObject root)
        {
            string path = ArtifactPath(root);
            string full = FullPath(path);
            if (!File.Exists(full))
                return "[ReportDeleteFate] FAIL: no artifact at " + path + " — run ReportDeleteFate.Run(<avatarRoot>) first";
            string text = File.ReadAllText(full);
            if (text.Contains("status: " + Pending))
            {
                // A domain reload discards the scheduled callback WITHOUT running it and clears the in-memory
                // in-flight flag, leaving the artifact reading `pending` forever. Age is the only signal the
                // editor can offer, and without it the artifact's own "do not re-issue" wedges the door.
                var m = Regex.Match(text, @"^started: (.+)$", RegexOptions.Multiline);
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var started)
                    && DateTime.UtcNow - started > StaleAfter)
                    return "[ReportDeleteFate] " + root.name + ": => STALE (started "
                         + started.ToString("o", CultureInfo.InvariantCulture) + ", over "
                         + StaleAfter.TotalMinutes.ToString(CultureInfo.InvariantCulture)
                         + " min ago and still pending — the scheduled bake was almost certainly discarded by a "
                         + "domain reload; re-issue ReportDeleteFate.Run(<avatarRoot>)) | log=" + path;
                return "[ReportDeleteFate] " + root.name + ": => PENDING | log=" + path;
            }
            string head = text.Split('\n').FirstOrDefault(l => l.StartsWith("summary: ", StringComparison.Ordinal));
            return head != null ? head.Substring("summary: ".Length).Trim()
                                : "[ReportDeleteFate] " + root.name + ": => OK | log=" + path;
        }

        // ── The measured picture, read inside the bake scope ──────────────────────────────────────────────

        /// <summary>One NaNimated bone MA inserted, with everything needed to attribute it. <c>Key</c> is the
        /// MANGLED <c>TargetProp</c> string MA spelled into the object's name.</summary>
        private sealed class NanBone
        {
            public string Key;
            public string RelativePath;
            public GameObject Go;
            public bool Claimed;
        }

        private sealed class BuiltAnimation
        {
            /// <summary>Per layer: the clip binding paths it touches for <c>m_LocalScale.x</c>, and every
            /// parameter its transitions read. Joined by binding rather than by layer NAME on purpose — MA
            /// names every reactive layer <c>"MA Responsive: &lt;renderer&gt;"</c>, so a renderer with several
            /// reactive keys has several identically-named layers and the name attributes nothing.</summary>
            public readonly List<(HashSet<string> scaledPaths, List<string> parameters)> Layers
                = new List<(HashSet<string>, List<string>)>();
            /// <summary>Every <c>(path, clip)</c> the built animator set binds <c>m_IsActive</c> on. This is
            /// MA's own constancy test — <c>AnalyzeConstants</c> asks exactly "does any clip bind this object's
            /// m_IsActive" — read off the built index rather than modelled.</summary>
            public readonly Dictionary<string, string> ActiveBindings = new Dictionary<string, string>(StringComparer.Ordinal);
            public int Controllers;
        }

        private static void Run(GameObject root, ReportDeleteFate.Census census, int triangleBudget, string path)
        {
            if (root == null)
            {
                WriteArtifact(path, Refusal(path, "the avatar root was destroyed before the bake ran"));
                return;
            }

            // `using`, so the SDK's paired post-callback fires on every exit including the refusal return. The
            // read happens INSIDE the scope by necessity: the post-callback destroys the clone's generated
            // controllers and meshes, and every fact below is read off exactly those.
            using (var bake = AvatarBake.Begin(root, root.name + " (delete-fate bake)"))
            {
                if (!bake.Ok)
                {
                    string stage = bake.DescribeFailure();
                    WriteArtifact(path, Refusal(path, stage));
                    Debug.LogError("[ReportDeleteFate] " + root.name + ": => FAIL (" + stage + ") | log=" + path);
                    return;
                }

                var clone = bake.Clone;
                var bones = CollectNanBones(clone);
                var anim = CollectBuiltAnimation(clone);
                var tris = CountTriangles(clone);
                var results = ResolveFates(census, clone, bones, anim);
                string summary;
                WriteArtifact(path, Render(root, census, results, bones, tris, triangleBudget, path, out summary));
                // Logged at the severity of the verdict, matching CheckAvatar: a CLASSIFY is a finding the
                // operator rules on, and a FAIL here is a defect in this door rather than in the avatar.
                if (summary.Contains("=> PASS")) Debug.Log(summary); else Debug.LogWarning(summary);
            }   // scope closes: the clone is destroyed and OnPostprocessAvatar fires, in that order
        }

        private static List<NanBone> CollectNanBones(GameObject clone)
        {
            var list = new List<NanBone>();
            foreach (var t in clone.GetComponentsInChildren<Transform>(true))
            {
                var key = ReportDeleteFate.NaNimatedKeyOf(t.name);
                if (key == null) continue;
                list.Add(new NanBone
                {
                    Key = key,
                    RelativePath = ReportDeleteFate.RelativePath(t, clone.transform),
                    Go = t.gameObject,
                });
            }
            return list;
        }

        private static BuiltAnimation CollectBuiltAnimation(GameObject clone)
        {
            var built = new BuiltAnimation();
            foreach (var ac in BuiltControllers(clone))
            {
                built.Controllers++;
                foreach (var layer in ac.layers)
                {
                    if (layer?.stateMachine == null) continue;
                    var scaled = new HashSet<string>(StringComparer.Ordinal);
                    var parameters = new List<string>();
                    WalkStateMachine(layer.stateMachine, scaled, parameters, built.ActiveBindings, new HashSet<int>());
                    built.Layers.Add((scaled, parameters));
                }
            }
            return built;
        }

        /// <summary>Every <c>AnimatorController</c> the built clone plays: the descriptor's playable-layer slots
        /// plus every child <c>Animator</c>. An override controller is unwrapped, matching
        /// <c>CompositionBake.AddParams</c> — the subject is the built avatar's animation index, not a named asset.</summary>
        private static IEnumerable<AnimatorController> BuiltControllers(GameObject clone)
        {
            var seen = new HashSet<int>();
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (d != null)
                foreach (var set in new[] { d.baseAnimationLayers, d.specialAnimationLayers })
                {
                    if (set == null) continue;
                    foreach (var l in set)
                    {
                        // The isDefault guard matches CompositionBake's, and for its reason: a programmatic
                        // write can leave a default-flagged slot holding a controller the built avatar does
                        // not play, and reading it would attribute parameters to a layer nothing runs.
                        if (l.isDefault) continue;
                        var ac = Unwrap(l.animatorController);
                        if (ac != null && seen.Add(ac.GetInstanceID())) yield return ac;
                    }
                }
            foreach (var a in clone.GetComponentsInChildren<Animator>(true))
            {
                var ac = Unwrap(a == null ? null : a.runtimeAnimatorController);
                if (ac != null && seen.Add(ac.GetInstanceID())) yield return ac;
            }
        }

        private static AnimatorController Unwrap(RuntimeAnimatorController rac)
        {
            var ac = rac as AnimatorController;
            for (int hop = 0; ac == null && rac is AnimatorOverrideController ovr && hop < 8; hop++)
            {
                ac = ovr.runtimeAnimatorController as AnimatorController;
                rac = ovr.runtimeAnimatorController;
            }
            return ac;
        }

        private static void WalkStateMachine(AnimatorStateMachine sm, HashSet<string> scaled, List<string> parameters,
                                             Dictionary<string, string> activeBindings, HashSet<int> visited)
        {
            if (sm == null || !visited.Add(sm.GetInstanceID())) return;
            AddConditions(sm.anyStateTransitions, parameters);
            AddConditions(sm.entryTransitions, parameters);
            foreach (var child in sm.states)
            {
                if (child.state == null) continue;
                AddConditions(child.state.transitions, parameters);
                foreach (var clip in ClipsOf(child.state.motion, new HashSet<int>()))
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (b.type == typeof(Transform) && b.propertyName == "m_LocalScale.x") scaled.Add(b.path);
                        if (b.type == typeof(GameObject) && b.propertyName == "m_IsActive" && !activeBindings.ContainsKey(b.path))
                            activeBindings[b.path] = clip.name;
                    }
            }
            foreach (var child in sm.stateMachines)
            {
                AddConditions(sm.GetStateMachineTransitions(child.stateMachine), parameters);
                WalkStateMachine(child.stateMachine, scaled, parameters, activeBindings, visited);
            }
        }

        private static void AddConditions(IEnumerable<AnimatorTransitionBase> transitions, List<string> parameters)
        {
            if (transitions == null) return;
            foreach (var t in transitions)
            {
                if (t?.conditions == null) continue;
                foreach (var c in t.conditions)
                    if (!string.IsNullOrEmpty(c.parameter) && !parameters.Contains(c.parameter)) parameters.Add(c.parameter);
            }
        }

        private static IEnumerable<AnimationClip> ClipsOf(Motion m, HashSet<int> visited)
        {
            if (m == null || !visited.Add(m.GetInstanceID())) yield break;
            if (m is AnimationClip clip) { yield return clip; yield break; }
            if (m is BlendTree tree)
                foreach (var child in tree.children)
                    foreach (var c in ClipsOf(child.motion, visited)) yield return c;
        }

        // ── Triangles ─────────────────────────────────────────────────────────────────────────────────────

        private struct TriangleCount { public long All; public long ActiveEnabled; public int Renderers; }

        /// <summary>Triangles over every skinned and mesh renderer on the built clone, and over the
        /// active-and-enabled subset. Index count over triangle-topology submeshes, never
        /// <c>Mesh.triangles</c> — that allocates a full index copy per mesh and a composed avatar has dozens.
        /// Vertices are deliberately not reported as a coverage measure anywhere: NaNimation retains every one
        /// of them, so a presence count reads a hidden garment as worn.</summary>
        private static TriangleCount CountTriangles(GameObject clone)
        {
            var c = new TriangleCount();
            foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = null;
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
                else if (r is MeshRenderer) { var mf = r.GetComponent<MeshFilter>(); mesh = mf != null ? mf.sharedMesh : null; }
                if (mesh == null) continue;
                long tris = 0;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    if (mesh.GetTopology(sm) == MeshTopology.Triangles) tris += mesh.GetIndexCount(sm) / 3;
                c.Renderers++;
                c.All += tris;
                if (r.enabled && r.gameObject.activeInHierarchy) c.ActiveEnabled += tris;
            }
            return c;
        }

        // ── Fate resolution ───────────────────────────────────────────────────────────────────────────────

        private sealed class RowResult
        {
            public ReportDeleteFate.DeclaredRow Row;
            public string Fate;
            public string Mechanism;
            public string Detail;
            public List<string> Parameters = new List<string>();
            public string Basis;
        }

        private static List<RowResult> ResolveFates(ReportDeleteFate.Census census, GameObject clone,
                                                    List<NanBone> bones, BuiltAnimation anim)
        {
            var results = new List<RowResult>();
            foreach (var row in census.Rows)
            {
                var r = new RowResult { Row = row };
                var bone = MatchBone(row, bones);
                if (bone != null)
                {
                    bone.Claimed = true;
                    r.Fate = ReportDeleteFate.FateNaNimation;
                    bool byLeaf;
                    r.Parameters = ParametersDriving(bone, anim, out byLeaf);
                    r.Basis = "a NaNimated bone on the built clone carries this row's key (`" + bone.Key + "`)"
                            + (r.Parameters.Count == 0 ? "; no layer drives it"
                               : byLeaf ? "; its driving layer was found by the bone's NAME, not its path — an "
                                          + "optimizer moved the bone and repathed the curve with it"
                                        : "; its driving layer binds the bone at `" + bone.RelativePath + "`");
                    r.Mechanism = ReportDeleteFate.ClassifyConditional(row, r.Parameters);
                    if (r.Mechanism == ReportDeleteFate.MechAncestorAnimated)
                    {
                        var ancestor = r.Parameters.Select(ReportDeleteFate.AncestorNamedBy).First(a => a != null);
                        var clipName = anim.ActiveBindings.FirstOrDefault(kv => Leaf(kv.Key) == ancestor).Value;
                        r.Detail = "ancestor `" + ancestor + "`"
                                 + (clipName != null ? ", bound by clip `" + clipName + "`"
                                                     : ", whose binding clip this read could not name — the build "
                                                       + "renamed the object out of every binding path it wrote");
                    }
                    else if (r.Mechanism == ReportDeleteFate.MechMenuItem)
                        r.Detail = "menu item at `" + row.MenuItemAncestor + "`";
                    else
                    {
                        // MA generated a layer, so SOMETHING made this row conditional. Nothing named it, which
                        // is a defect in this door rather than a finding about the avatar.
                        r.Fate = ReportDeleteFate.FateUnresolved;
                        r.Mechanism = null;
                        r.Detail = "NaNimated with no reason this door can name — controlling parameters: "
                                 + (r.Parameters.Count == 0 ? "(none found on any layer driving the bone)"
                                                            : string.Join(", ", r.Parameters));
                    }
                    results.Add(r);
                    continue;
                }

                if (row.LaterSetHost != null)
                {
                    r.Fate = ReportDeleteFate.FateCancelled;
                    r.Mechanism = ReportDeleteFate.MechSetCancels;
                    r.Detail = "a later `Set` on the same shape at `" + row.LaterSetHost + "` registers a null value on "
                             + "this row's own key, and a null is not an `IMeshSelector` — so the row neither deletes "
                             + "nor NaNimates and the vertices stay";
                    r.Basis = "declaration order (MA's cancel rule is exactly this), and no NaNimated bone on the clone";
                    results.Add(r);
                    continue;
                }

                if (row.ParkedAncestor != null)
                {
                    string cloneLeaf = Leaf(row.ParkedAncestor);
                    var animatedBy = anim.ActiveBindings.FirstOrDefault(kv => Leaf(kv.Key) == cloneLeaf);
                    if (animatedBy.Key == null)
                    {
                        r.Fate = ReportDeleteFate.FateDropped;
                        r.Mechanism = ReportDeleteFate.MechHostInactive;
                        r.Detail = "ancestor `" + row.ParkedAncestor + "` is inactive and no clip in the BUILT animator "
                                 + "set binds its `m_IsActive`, so the condition is constant-and-unsatisfied and MA drops "
                                 + "the action group entirely — no layer, no bone, no delete";
                        r.Basis = "MA's own constancy test, run over the built animation index (" + anim.Controllers
                                + " controllers)";
                        results.Add(r);
                        continue;
                    }
                }

                r.Fate = ReportDeleteFate.FateBuildTime;
                r.Basis = Corroboration(row, clone);
                results.Add(r);
            }
            return results;
        }

        /// <summary>The NaNimated bone this row's key claims, or null. A ShapeChanger row's key is exact — MA
        /// spells the shape into the name. A cutter's is not: the key embeds the resolved selector's
        /// <c>ToString()</c>, a value that exists only inside the build, so the match is the renderer plus the
        /// leading token the row's own vertex filter emits. Where a renderer carries several cutters and the
        /// tokens do not separate them, the first unclaimed bone is taken and the row's basis says the
        /// attribution is by renderer rather than by filter — the honest reading, since the FATE is the same
        /// for every cutter on that renderer even when which-bone-is-whose is not recoverable.</summary>
        private static NanBone MatchBone(ReportDeleteFate.DeclaredRow row, List<NanBone> bones)
        {
            if (row.Kind == "shape")
            {
                string want = ReportDeleteFate.Mangle(row.TargetPropKey);
                return bones.FirstOrDefault(b => !b.Claimed && b.Key == want);
            }
            string prefix = ReportDeleteFate.Mangle(row.CutterKeyPrefix);
            var candidates = bones.Where(b => !b.Claimed && b.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0) return null;
            var tokens = row.FilterTokens?.Where(t => t != null).ToArray() ?? Array.Empty<string>();
            if (tokens.Length > 0)
            {
                var scored = candidates.FirstOrDefault(b => tokens.All(t =>
                    b.Key.IndexOf(t, prefix.Length, StringComparison.Ordinal) >= 0));
                if (scored != null) return scored;
            }
            return candidates[0];
        }

        /// <summary>Every parameter on a layer whose clips scale this bone. The join is by BINDING, not by layer
        /// name — MA names all of a renderer's reactive layers alike (<c>"MA Responsive: &lt;renderer&gt;"</c>), so
        /// a renderer with several reactive keys has several identically-named layers and the name attributes
        /// nothing.
        /// <para>Exact path first, then the bone's own NAME anywhere a binding ends. The fallback exists because
        /// an optimizer running after MA can move the bone and repath the curve together, leaving both internally
        /// consistent and neither equal to the path this read computes; without it a correctly-NaNimated row on
        /// an optimized avatar reports no controlling parameter and the door FAILs on its own blind spot. The
        /// bone name embeds the renderer and the shape, so it is not a loose match. <paramref name="byLeaf"/>
        /// says which arm answered, and the row carries it.</para></summary>
        private static List<string> ParametersDriving(NanBone bone, BuiltAnimation anim, out bool byLeaf)
        {
            byLeaf = false;
            var found = new List<string>();
            foreach (var (scaled, parameters) in anim.Layers)
            {
                if (!scaled.Contains(bone.RelativePath)) continue;
                foreach (var p in parameters) if (!found.Contains(p)) found.Add(p);
            }
            if (found.Count > 0) return found;

            string leaf = Leaf(bone.RelativePath);
            foreach (var (scaled, parameters) in anim.Layers)
            {
                if (!scaled.Any(pth => Leaf(pth) == leaf)) continue;
                byLeaf = true;
                foreach (var p in parameters) if (!found.Contains(p)) found.Add(p);
            }
            return found;
        }

        /// <summary>What the clone can add to a build-time verdict. Corroboration only: an optimizer may have
        /// merged the row's renderer away, and treating a missing name-match as evidence would manufacture a
        /// finding out of a successful optimization.</summary>
        private static string Corroboration(ReportDeleteFate.DeclaredRow row, GameObject clone)
        {
            var matches = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                               .Where(s => s.name == row.RendererName && s.sharedMesh != null).ToList();
            if (matches.Count != 1)
                return "no NaNimated bone, no later Set, no parked ancestor — and the clone carries "
                     + (matches.Count == 0 ? "no renderer named `" + row.RendererName + "` (an optimizer merged it), "
                                           : matches.Count + " renderers named `" + row.RendererName + "`, ")
                     + "so there is no vertex-count corroboration";
            int now = matches[0].sharedMesh.vertexCount;
            string cmp = row.AuthoredVerts < 0 ? "the authored vertex count was unreadable"
                : now < row.AuthoredVerts ? "the clone's `" + row.RendererName + "` is down to " + now + " vertices from "
                                            + row.AuthoredVerts + ", which is the removal"
                : "the clone's `" + row.RendererName + "` still reads " + now + " vertices against " + row.AuthoredVerts
                  + " authored — a merge or another pass may account for it, so read this row before trusting it";
            return "no NaNimated bone, no later Set, no parked ancestor; " + cmp;
        }

        private static string Leaf(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }

        // ── Emit ──────────────────────────────────────────────────────────────────────────────────────────

        private static string Render(GameObject root, ReportDeleteFate.Census census, List<RowResult> results,
                                     List<NanBone> bones, TriangleCount tris, int budget, string path,
                                     out string summaryOut)
        {
            int buildTime = results.Count(r => r.Fate == ReportDeleteFate.FateBuildTime);
            int nan = results.Count(r => r.Fate == ReportDeleteFate.FateNaNimation);
            int dropped = results.Count(r => r.Fate == ReportDeleteFate.FateDropped);
            int cancelled = results.Count(r => r.Fate == ReportDeleteFate.FateCancelled);
            int unresolved = results.Count(r => r.Fate == ReportDeleteFate.FateUnresolved);
            long over = tris.ActiveEnabled - budget;
            string verdict = ReportDeleteFate.VerdictOf(results.Select(r => r.Fate), tris.ActiveEnabled, budget);

            string summary = summaryOut = string.Format(CultureInfo.InvariantCulture,
                "[ReportDeleteFate] {0}: declared={1} buildTime={2} naNimation={3} dropped={4} cancelledBySet={5}{6} "
                + "nanBones={7} tris={8} active={9} budget={10} delta={11}{12} => {13} | log={14}",
                root.name, census.Rows.Count, buildTime, nan, dropped, cancelled,
                unresolved > 0 ? " unresolved=" + unresolved : "",
                bones.Count, tris.All, tris.ActiveEnabled, budget,
                (over >= 0 ? "+" : "") + over.ToString(CultureInfo.InvariantCulture),
                bones.Count(b => !b.Claimed) > 0 ? " unattributedBones=" + bones.Count(b => !b.Claimed) : "",
                verdict, path);

            var sb = new StringBuilder();
            sb.Append("# ReportDeleteFate: ").Append(root.name).Append('\n');
            sb.Append("root: `").Append(ReportDeleteFate.PathOf(root)).Append("`  \n\n");
            sb.Append("summary: ").Append(summary).Append("\n\n");

            sb.Append("## Fates — one row per declared removal\n\n");
            sb.Append("_`build-time` removed triangles; `NaNimation` removed none and never will — the vertices stay in "
                    + "the mesh and in the count, hidden by a NaN scale; `dropped` never reached the build at all; "
                    + "`cancelled-by-Set` was voided by a later Set on the same key. Only the first is geometry you "
                    + "stopped paying for._\n\n");
            sb.Append("| declared | kind | host | fate | mechanism | controlling parameters | detail |\n");
            sb.Append("| --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (var r in results)
                sb.Append("| `").Append(RunLogFormat.Cell(r.Row.Label)).Append("` | ").Append(r.Row.Kind)
                  .Append(" | `").Append(RunLogFormat.Cell(r.Row.HostPath)).Append("` | ")
                  .Append(r.Fate == ReportDeleteFate.FateBuildTime ? "**build-time**" : r.Fate).Append(" | ")
                  .Append(r.Mechanism ?? "—").Append(" | ")
                  .Append(r.Parameters.Count == 0 ? "—" : "`" + RunLogFormat.Cell(string.Join("`, `", r.Parameters)) + "`")
                  .Append(" | ").Append(RunLogFormat.Cell(r.Detail ?? "—")).Append(" |\n");

            sb.Append("\n## What each fate rests on\n\n");
            sb.Append("| declared | basis |\n| --- | --- |\n");
            foreach (var r in results)
                sb.Append("| `").Append(RunLogFormat.Cell(r.Row.Label)).Append("` | ")
                  .Append(RunLogFormat.Cell(r.Basis ?? "—")).Append(" |\n");

            sb.Append("\n## Declared, as authored\n\n");
            sb.Append("| declared | renderer | inverted | menu-item ancestor | parked ancestor | later Set | filters |\n");
            sb.Append("| --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (var row in census.Rows)
                sb.Append("| `").Append(RunLogFormat.Cell(row.Label)).Append("` | `")
                  .Append(RunLogFormat.Cell(row.RendererPath)).Append("` | ").Append(row.Inverted ? "yes" : "no")
                  .Append(" | ").Append(row.MenuItemAncestor == null ? "—" : "`" + RunLogFormat.Cell(row.MenuItemAncestor) + "`")
                  .Append(" | ").Append(row.ParkedAncestor == null ? "—" : "`" + RunLogFormat.Cell(row.ParkedAncestor) + "`")
                  .Append(" | ").Append(row.LaterSetHost == null ? "—" : "`" + RunLogFormat.Cell(row.LaterSetHost) + "`")
                  .Append(" | ").Append(row.Kind == "cutter"
                        ? RunLogFormat.Cell((row.FilterSummary ?? "—") + " / " + (row.MultiMode ?? "—")) : "—")
                  .Append(" |\n");
            sb.Append("\nA cutter's ancestors that the cut renderer also sits under contribute NO condition "
                    + "(`LocateReactions` passes the renderer as the rule's affected object), which is why a cutter on "
                    + "the mesh it cuts is unconditional by construction and a ShapeChanger in the same place is not.\n");

            sb.Append("\n## NaNimated bones on the built clone\n\n");
            if (bones.Count == 0) sb.Append("_(none — nothing on this build was hidden by NaNimation)_\n");
            else
            {
                sb.Append("| key | path | attributed |\n| --- | --- | --- |\n");
                foreach (var b in bones)
                    sb.Append("| `").Append(RunLogFormat.Cell(b.Key)).Append("` | `")
                      .Append(RunLogFormat.Cell(b.RelativePath)).Append("` | ").Append(b.Claimed ? "yes" : "**no**").Append(" |\n");
                sb.Append("\nAn unattributed bone is a removal this census did not declare — a vendor module below the "
                        + "root, or a row whose target this read resolved differently from the build. It is a place to "
                        + "look, not a verdict.\n");
            }

            sb.Append("\n## Triangles\n\n");
            sb.Append("- all skinned + mesh renderers: ").Append(tris.All.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("- active and enabled: ").Append(tris.ActiveEnabled.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("- budget: ").Append(budget.ToString(CultureInfo.InvariantCulture))
              .Append("  delta: ").Append((over >= 0 ? "+" : "") + over.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("- renderers counted: ").Append(tris.Renderers.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("\n**Never read vertex presence as coverage.** NaNimation retains every vertex and every triangle "
                    + "of the shape it hides, so a mesh that still carries the geometry says nothing about whether the "
                    + "garment under it is visible. Triangles, or the bone table above, are the answer.\n");

            sb.Append("\n## Verdict\n\n");
            sb.Append("`PASS` when every declared row is `build-time` or deliberately `dropped` and the active triangle "
                    + "count is within budget. `CLASSIFY` when any row NaNimates or is cancelled, or the budget is "
                    + "exceeded — a finding to route per row, not a tool failure: NaNimation is accepted by ruling on "
                    + "some nodes and the operator rules row by row. `FAIL` is reserved for a row this door could not "
                    + "explain, which is a defect here rather than a fact about the avatar.\n");

            return "summary: " + summary + "\n\n" + sb;
        }

        private static string Refusal(string path, string stage) =>
            "# ReportDeleteFate\n\nstatus: FAILED\n\nsummary: [ReportDeleteFate] => FAIL (" + stage + ") | log=" + path
            + "\n\nThe bake did not complete, so this artifact carries NO fate table. The authored census is "
            + "deliberately not published here: every fate in it is a claim about the BUILT avatar, and authored rows "
            + "under that heading are the exact misread this door exists to prevent.\n";

        private static string FullPath(string assetPath) =>
            Path.Combine(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length), assetPath);

        private static void WriteArtifact(string assetPath, string text)
        {
            try
            {
                string full = FullPath(assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, text);
                RunLogFormat.PublishArtifact(RunLogFormat.SnapshotDir, assetPath);
                var head = text.Split('\n').FirstOrDefault(l => l.StartsWith("summary: ", StringComparison.Ordinal));
                if (head != null)
                {
                    string line = head.Substring("summary: ".Length).Trim();
                    if (line.EndsWith("=> PASS", StringComparison.Ordinal) || line.Contains("=> PASS |")) Debug.Log(line);
                    else Debug.LogWarning(line);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[ReportDeleteFate] could not write the artifact at " + assetPath + " ("
                             + e.GetType().Name + ") — the bake's result is unrecoverable, so treat this run as not taken.");
            }
        }
    }
}
