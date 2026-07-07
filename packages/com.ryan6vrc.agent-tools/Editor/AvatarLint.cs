using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Ryan6Vrc.AgentTools.Editor.AnimatorLint; // FrameKind / FrameResult / TryMaFrame / TryVrcfFrame / CollectUnresolvedBindings

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Scene-scoped INSPECTION gate: classify the two silent path-encoded reference breaks a mergeable
    /// placement leaves on an instantiated IN-SCENE avatar root after a base rename
    /// (canonical <c>Body_base</c>→<c>Body_Base</c>):
    ///   - <b>MA scene refs</b> (the reactive family / BlendshapeSync / Mesh Settings — anything carrying
    ///     the <c>referencePath</c>+<c>targetObject</c> <c>AvatarObjectReference</c> field pair) that no
    ///     longer resolve → the skill retargets them in place.
    ///   - <b>clip/controller bindings</b> (descriptor playable layers + every MA MergeAnimator / VRCFury
    ///     FullController merged animator) that resolve to no scene object → the skill owns the vendor
    ///     <c>.anim</c> and repaths (routed by the per-offender <c>clipAssetPath</c>).
    ///
    /// Everything is resolved against the PLACED scene (the load-bearing model, spec D1): a to-be-merged
    /// bone is physically present pre-bake and resolves now; a base-rename break does not. So this predicts
    /// nothing about what the build will move, and never depends on the <c>Armature.&lt;Name&gt;</c> convention.
    ///
    /// Verdict is <c>PASS</c> (all resolve) or <c>CLASSIFY</c> (any unresolved) — never <c>FAIL</c> for a
    /// finding (bad input alone bare-FAILs). No computed near-miss/absent/N-of-M heuristic: the tool names
    /// offenders and their class; the compose agent applies discretion (broken refs are often intentional).
    ///
    /// Clip-binding detection REUSES <see cref="AnimatorLint"/>'s frame-detection + binding-walk +
    /// humanoid-curve-skip (called, not re-expressed) with the build-rewrite demotion flipped off. The
    /// surface enumeration, the VRCF ancestor-walk, and the fail-loud frame guards are net-new here.
    ///
    /// INSPECTION ONLY — mutates no scene state (scene.isDirty is unchanged across a call), writes no asset
    /// but its own RunLog. NEVER throws: every reflective hop is guarded and degrades with a loud warning.
    /// </summary>
    [AgentTool]
    public static class AvatarLint
    {
        private const string MaObjRefTypeName = "nadena.dev.modular_avatar.core.AvatarObjectReference";
        private const string MaAvatarRootSentinel = "$$$AVATAR_ROOT$$$"; // AvatarObjectReference.AVATAR_ROOT

        // Standing Notes line — quoted verbatim from the spec §Excluded edge. Carried by EVERY RunLog so the
        // model's two known holes (anticipatory-authoring frames + build-time deletions) are stated-and-refused
        // on every run, with zero detection code.
        private const string ExcludedEdgeLine =
            "Bindings are evaluated against the placed scene. Anticipatory-authoring frames (a binding authored " +
            "expecting a post-merge location) are not distinguished, and an unresolved binding there may be " +
            "intentional. Build-time object *deletions* (as opposed to moves) are also not visible here.";

        // ── Test seams (internal, default off) ──────────────────────────────────────────────────────
        // Real MA/VRCF types always reflect and MA's Get(Component) is always reachable in this Editor, so
        // the fail-loud/degrade branches would be unexercisable. These force them without un-defining the
        // real types. Each is a plain internal static a test flips, then resets in TearDown.

        /// <summary>Force the pinned <c>Get(Component)</c> overload to be treated as unreachable, so every
        /// scene-ref resolution takes the loud self-resolve fallback (targetObject-first, then referencePath).</summary>
        internal static bool TestForceGetUnreachable = false;

        /// <summary>Force <c>SerializedProperty.boxedValue</c> to be treated as throwing (R-J), so resolution
        /// catches it, warns, and self-resolves — completing with a verdict rather than propagating.</summary>
        internal static bool TestForceBoxedThrow = false;

        /// <summary>When non-null, treat every discovered animator frame as having this unreflected anchor
        /// (R-H), so the fail-loud "frame field didn't reflect" branch is exercisable on real MA/VRCF types.</summary>
        internal static string TestForceUnreflectedAnchor = null;

        // ── Public API ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Classify the MA-scene-ref and clip-binding reference breaks on the in-scene avatar at
        /// <paramref name="avatarRoot"/> (a scene hierarchy path, else numeric instance id, else name —
        /// mirrors AvatarGrab's target resolution). Returns a one-line summary; a real run ends with the
        /// RunLog path in-band (<c>… =&gt; PASS|CLASSIFY | log=&lt;path&gt;</c>). Bad input (root not found /
        /// no VRCAvatarDescriptor) is a bare <c>[AvatarLint] FAIL: …</c> with no trailer.</summary>
        public static string Inspect(string avatarRoot)
        {
            var avatarGO = Resolve(avatarRoot);
            if (avatarGO == null)
                return Refuse("avatar root '" + avatarRoot + "' not found — tried hierarchy path, instance id, then name in the active scene");

            var descriptor = avatarGO.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                return Refuse("'" + avatarRoot + "' has no VRCAvatarDescriptor — Inspect expects the avatar (descriptor) root");

            var rep = new Report { Root = avatarGO };

            // ---- Surface enumeration (net-new) --------------------------------------------------------
            // Each (controller, frame) pair is walked once; dedup per pair (controller + frame root), not
            // globally, so a controller shared across frames is resolved per frame.
            var pairs = new List<Pair>();
            var seen = new HashSet<(int ctrl, int root, int kind)>();
            void AddPair(AnimatorController c, GameObject frameRoot, List<GameObject> roots, FrameKind kind, string label)
            {
                if (c == null) return;
                int rootId = frameRoot != null ? frameRoot.GetInstanceID() : 0;
                if (!seen.Add((c.GetInstanceID(), rootId, (int)kind))) return;
                pairs.Add(new Pair { Controller = c, Roots = roots, Kind = kind, Label = label });
            }

            // (a) Descriptor playable-layer controllers — avatar-root frame.
            CollectDescriptorLayers(descriptor, avatarGO, AddPair);

            // (b)/(c) Every MA MergeAnimator + VRCFury FullController in the subtree.
            foreach (var c in avatarGO.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;

                if (TryMaFrame(c, avatarGO, out var maCtrl, out var maFrame))
                {
                    string anchor = maFrame.UnreflectedAnchor ?? TestForceUnreflectedAnchor;
                    if (anchor != null) SurfaceUnreflected(c, anchor, rep); // R-H — loud, but not dropped
                    // R-K — a Relative MA whose relativePathRoot is set-but-unresolved is a guessed frame.
                    string uncertain = MaFrameUncertaintyNote(c, avatarGO, maFrame);
                    var roots = new List<GameObject> { maFrame.Root ?? avatarGO };
                    AddPair(maCtrl, maFrame.Root ?? avatarGO,
                        roots, FrameKind.MA, "MA MergeAnimator @ " + PathOf(c.gameObject));
                    if (uncertain != null) rep.FrameUncertain.Add(uncertain);
                }

                if (TryVrcfFrame(c, out var vrcfCtrls, out var vrcfFrame))
                {
                    string anchor = vrcfFrame.UnreflectedAnchor ?? TestForceUnreflectedAnchor;
                    if (anchor != null) SurfaceUnreflected(c, anchor, rep);
                    var mount = vrcfFrame.Root ?? c.gameObject;
                    var roots = AncestorChain(mount, avatarGO); // D-A upward strip: resolves at ANY level ⇒ not a break
                    foreach (var vc in vrcfCtrls)
                        AddPair(vc, mount, roots, FrameKind.VRCF, "VRCFury FullController @ " + PathOf(c.gameObject));
                }
            }

            // ---- MA scene-ref detection (D3) — generic over EVERY component ----------------------------
            foreach (var c in avatarGO.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                ScanSceneRefs(c, avatarGO, rep);
            }

            // ---- Clip-binding classification (reuse AnimatorLint's walk, demotion off) -----------------
            foreach (var p in pairs)
            {
                foreach (var (clip, b) in CollectUnresolvedBindingsCalled(p.Controller, p.Roots))
                {
                    rep.ClipBindings.Add(new Offender
                    {
                        Kind = "clip-binding",
                        Animator = p.Controller.name,
                        Clip = clip.name,
                        Path = b.path,
                        ClipAssetPath = AssetDatabase.GetAssetPath(clip),
                        Host = p.Label,
                    });
                }
            }

            return Emit(rep);
        }

        // ── Surface enumeration helpers ───────────────────────────────────────────────────────────────

        private struct Pair { public AnimatorController Controller; public List<GameObject> Roots; public FrameKind Kind; public string Label; }

        private static void CollectDescriptorLayers(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            GameObject avatarGO, Action<AnimatorController, GameObject, List<GameObject>, FrameKind, string> add)
        {
            void Walk(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[] layers, string which)
            {
                if (layers == null) return;
                foreach (var layer in layers)
                {
                    if (layer.isDefault) continue;               // SDK default controller — nothing authored here
                    var c = layer.animatorController as AnimatorController;
                    if (c == null) continue;
                    add(c, avatarGO, new List<GameObject> { avatarGO }, FrameKind.DescriptorLayer,
                        "descriptor " + which + " layer " + layer.type);
                }
            }
            Walk(descriptor.baseAnimationLayers, "base");
            Walk(descriptor.specialAnimationLayers, "special");
        }

        // The VRCF upward-strip nearest-match: mount root, then each ancestor up to (and including) the
        // avatar root. A binding resolving at ANY level is NOT a break (mirrors VRCF's build rewriter).
        private static List<GameObject> AncestorChain(GameObject mount, GameObject avatarGO)
        {
            var roots = new List<GameObject>();
            Transform cur = mount != null ? mount.transform : null;
            var stop = avatarGO.transform;
            while (cur != null)
            {
                roots.Add(cur.gameObject);
                if (cur == stop) break;
                cur = cur.parent;
            }
            if (roots.Count == 0 || roots[roots.Count - 1] != avatarGO) roots.Add(avatarGO); // guarantee avatar root is a candidate
            return roots;
        }

        // R-H: name the anchor loud and record it in Notes; the caller still processes the controller's
        // bindings (never drops it), so an unreflected frame field can't yield a false PASS.
        private static void SurfaceUnreflected(Component c, string anchor, Report rep)
        {
            string msg = "[AvatarLint] frame field '" + anchor + "' on " + c.GetType().Name + " @ " + PathOf(c.gameObject)
                       + " did not reflect — surfacing the merged animator anyway (not dropped); its frame is best-effort.";
            Debug.LogWarning(msg);
            rep.Notes.Add("fail-loud (R-H): " + msg.Substring("[AvatarLint] ".Length));
        }

        // R-K: iff a Relative MA's relativePathRoot is SET (non-empty referencePath) yet does not resolve,
        // TryMaFrame fell back to the component's own GameObject — the frame is a guess. Returns the
        // frame-uncertain caveat cross-referencing the MA-scene-ref offender the generic scan will emit for
        // that same relativePathRoot; null when the frame is confident (Absolute, or an empty/resolving root).
        private static string MaFrameUncertaintyNote(Component c, GameObject avatarGO, FrameResult frame)
        {
            if (frame.IsAbsolute) return null;
            var so = new SerializedObject(c);
            var rel = so.FindProperty("relativePathRoot");
            if (rel == null) return null;
            var pathChild = rel.FindPropertyRelative("referencePath");
            string refPath = pathChild != null ? pathChild.stringValue : "";
            if (string.IsNullOrEmpty(refPath)) return null; // empty ⇒ own-GO by design, not a guess
            if (TryResolveSceneRef(rel, c, avatarGO, out _)) return null; // resolves ⇒ confident frame
            return "frame-uncertain: bindings for the animator on '" + PathOf(c.gameObject)
                 + "' were resolved against the fallback frame (its own GameObject) because the MA relativePathRoot '"
                 + refPath + "' did not resolve — see the matching MA-scene-ref offender. These bindings are counted, not dropped.";
        }

        // ── MA scene-ref detection (D3) — never throws ────────────────────────────────────────────────

        // Walk serialized properties generically (precedent: RemapReferencesByPath). A property carrying both
        // a referencePath(string) child and a targetObject(objref) child is an AvatarObjectReference. Only a
        // SET ref (non-empty referencePath — MA treats an empty path as "unset", exactly like ImportVerify's
        // clean-zero) is validated; unset refs are the intentional-empty case and never counted.
        private static void ScanSceneRefs(Component c, GameObject avatarGO, Report rep)
        {
            SerializedObject so;
            try { so = new SerializedObject(c); }
            catch { return; }
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = true;
                if (it.propertyType != SerializedPropertyType.Generic) continue;
                var pathChild = it.FindPropertyRelative("referencePath");
                var targetChild = it.FindPropertyRelative("targetObject");
                if (pathChild == null || pathChild.propertyType != SerializedPropertyType.String) continue;
                if (targetChild == null || targetChild.propertyType != SerializedPropertyType.ObjectReference) continue;

                enter = false; // it's an AvatarObjectReference — don't descend into its own children
                string refPath = pathChild.stringValue;
                if (string.IsNullOrEmpty(refPath)) continue; // unset (MISSING-vs-EMPTY: intentional empty)

                if (TryResolveSceneRef(it.Copy(), c, avatarGO, out _)) continue; // resolved ⇒ not an offender
                rep.SceneRefs.Add(new Offender
                {
                    Kind = "MA-scene-ref",
                    Path = refPath,
                    Host = c.GetType().Name + " @ " + PathOf(c.gameObject),
                });
            }
        }

        // Cached pinned instance overload: AvatarObjectReference.Get(Component) -> GameObject. Pinned by
        // parameter type == Component AND return type == GameObject, so a future/other Get overload (e.g. the
        // static Get(SerializedProperty)) can never be silently mis-bound. Sentinel _pinAttempted guards a
        // one-time reflect; null MethodInfo ⇒ unreachable (API drift / MA absent).
        private static bool _pinAttempted;
        private static MethodInfo _getOverload;
        private static MethodInfo PinGetOverload(Type aorType)
        {
            if (_pinAttempted) return _getOverload;
            _pinAttempted = true;
            try
            {
                foreach (var m in aorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "Get" || m.ReturnType != typeof(GameObject)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Component)) { _getOverload = m; break; }
                }
            }
            catch { _getOverload = null; }
            return _getOverload;
        }

        // Resolve an AvatarObjectReference property. Authoritative path: box it (guarded — boxedValue THROWS
        // for unsupported shapes, R-J) and invoke the pinned Get(Component) (checks targetObject before
        // referencePath — the targetObject-wins trap). Every reflective hop is guarded: on any failure/drift,
        // warn loud naming the broken anchor and self-resolve from the SerializedProperty CHILDREN
        // (targetObject-first if populated+live, else referencePath against the avatar root). Never throws.
        private static bool TryResolveSceneRef(SerializedProperty aor, Component host, GameObject avatarGO, out string refPath)
        {
            var pathChild = aor.FindPropertyRelative("referencePath");
            var targetChild = aor.FindPropertyRelative("targetObject");
            refPath = pathChild != null ? pathChild.stringValue : "";
            string reason = null;

            if (!TestForceGetUnreachable && !TestForceBoxedThrow)
            {
                object boxed = null;
                try { boxed = aor.boxedValue; }
                catch (Exception e) { reason = "boxedValue threw (" + e.GetType().Name + ")"; }

                if (reason == null && boxed != null)
                {
                    var mi = PinGetOverload(boxed.GetType());
                    if (mi == null) reason = "Get(Component) overload unreachable (MA API drift / absent)";
                    else
                    {
                        try { return (mi.Invoke(boxed, new object[] { host }) as GameObject) != null; }
                        catch (Exception e) { reason = "Get(Component) invoke threw (" + e.GetType().Name + ")"; }
                    }
                }
                else if (reason == null) reason = "boxedValue was null";
            }
            else reason = TestForceBoxedThrow ? "[test seam] forced boxedValue throw" : "[test seam] forced Get(Component) unreachable";

            // ---- Guarded self-resolve from the children already located --------------------------------
            Debug.LogWarning("[AvatarLint] scene-ref resolve degraded on " + PathOf(host.gameObject)
                           + " (" + reason + ") — self-resolving from serialized children (targetObject-first, then referencePath).");

            var targetGO = targetChild != null ? targetChild.objectReferenceValue as GameObject : null;
            if (targetGO != null && targetGO.transform.IsChildOf(avatarGO.transform)) return true; // targetObject wins
            if (string.IsNullOrEmpty(refPath)) return false;                                        // unset
            if (refPath == MaAvatarRootSentinel) return true;                                       // avatar root itself
            return avatarGO.transform.Find(refPath) != null;
        }

        // ── Clip-binding walk (REUSE — AnimatorLint.CollectUnresolvedBindings, demotion off) ────────────
        // The demotion (BrokenBindingIsError = !buildRewrite) lives in AnimatorLint.Emit, NOT in the walk —
        // CollectUnresolvedBindings returns raw unresolved pairs, so calling it directly IS the "demotion
        // off" behaviour: under D1 every unresolved-in-scene binding is a real, non-advisory clip-binding
        // offender (mapped to CLASSIFY, never FAIL).
        private static IEnumerable<(AnimationClip clip, EditorCurveBinding binding)> CollectUnresolvedBindingsCalled(
            AnimatorController controller, List<GameObject> roots)
            => AnimatorLint.CollectUnresolvedBindings(controller, roots);

        // ── Output ────────────────────────────────────────────────────────────────────────────────────

        private static string Emit(Report rep)
        {
            int maSceneRef = rep.SceneRefs.Count;
            int clipBinding = rep.ClipBindings.Count;
            string result = (maSceneRef > 0 || clipBinding > 0) ? "CLASSIFY" : "PASS";

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[AvatarLint] {0}: maSceneRef={1} clipBinding={2} => {3}",
                rep.Root.name, maSceneRef, clipBinding, result);

            var sb = new StringBuilder();
            sb.Append("# AvatarLint: ").Append(rep.Root.name).Append('\n');
            sb.Append("root: `").Append(PathOf(rep.Root)).Append("`  \n\n");
            sb.Append(summary.Substring("[AvatarLint] ".Length)).Append('\n');

            sb.Append("\n## Counts\n\n");
            sb.Append("- maSceneRef: ").Append(maSceneRef).Append('\n');
            sb.Append("- clipBinding: ").Append(clipBinding).Append('\n');

            sb.Append("\n## Offenders\n\n");
            sb.Append("### MA-scene-ref\n\n");
            if (rep.SceneRefs.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.SceneRefs)
                sb.Append("- **MA-scene-ref** path=`").Append(o.Path).Append("` host=").Append(o.Host).Append('\n');

            sb.Append("\n### clip-binding\n\n");
            if (rep.ClipBindings.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.ClipBindings)
                sb.Append("- **clip-binding** animator=`").Append(o.Animator)
                  .Append("` clip=`").Append(o.Clip)
                  .Append("` path=`").Append(o.Path)
                  .Append("` clipAssetPath=`").Append(string.IsNullOrEmpty(o.ClipAssetPath) ? "(unsaved)" : o.ClipAssetPath)
                  .Append("` [").Append(o.Host).Append("]\n");

            sb.Append("\n## Notes\n\n");
            sb.Append("- ").Append(ExcludedEdgeLine).Append('\n');
            foreach (var n in rep.FrameUncertain) sb.Append("- ").Append(n).Append('\n');
            foreach (var n in rep.Notes) sb.Append("- ").Append(n).Append('\n');

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "avatarlint_" + rep.Root.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogWarning(res);
            return res;
        }

        // ── Bad-input refusal (bare FAIL, no trailer — family discipline) ───────────────────────────────

        private static string Refuse(string why)
        {
            string err = "[AvatarLint] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        // ── Scene resolver (path → instance id → name; mirrors AvatarGrab.Resolve, kept local) ──────────

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

        // ── Types ───────────────────────────────────────────────────────────────────────────────────

        private struct Offender
        {
            public string Kind;
            public string Path;          // MA-scene-ref: failing referencePath. clip-binding: binding scene path.
            public string Host;          // component/site label
            public string Animator;      // clip-binding only
            public string Clip;          // clip-binding only
            public string ClipAssetPath; // clip-binding only — AssetDatabase.GetAssetPath(clip); DISTINCT from Path (routing, R-E)
        }

        private class Report
        {
            public GameObject Root;
            public readonly List<Offender> SceneRefs = new List<Offender>();
            public readonly List<Offender> ClipBindings = new List<Offender>();
            public readonly List<string> FrameUncertain = new List<string>(); // R-K caveats
            public readonly List<string> Notes = new List<string>();          // R-H fail-loud + degrade notes
        }
    }
}
