using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Ryan6Vrc.AgentTools.Editor.CheckAnimator;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Scene-scoped inspection gate: names the silent reference and identity breaks a mergeable placement
    /// leaves on an instantiated in-scene avatar root, in five classes: MA scene refs, clip/controller
    /// bindings, anchor-seam, merge-conflict and blendshape-action.
    /// Contract: docs/unity-tools.md.
    ///
    /// Verdict is <c>PASS</c> (clean) or <c>CLASSIFY</c> (any finding), never <c>FAIL</c> for a finding (bad
    /// input alone bare-FAILs). No computed near-miss/absent/N-of-M scoring: every class is a definite
    /// predicate (a ref resolves or it doesn't; a collision group is ≥2-with-a-mergeable or it isn't). The
    /// tool names offenders and their class; the compose agent applies discretion (findings are often
    /// intentional).
    ///
    /// Clip-binding detection calls <see cref="CheckAnimator"/>'s binding walk rather than re-expressing it.
    ///
    /// Inspection only: mutates no scene state and writes no asset but its own RunLog. A reflective hop that
    /// fails degrades to a loud warning rather than a throw.
    /// </summary>
    [AgentTool]
    public static class CheckAvatar
    {
        private const string MaAvatarRootSentinel = CheckAnimator.MaAvatarRootSentinel;

        // Standing Notes lines, on every RunLog.
        private const string ExcludedEdgeLine =
            "Bindings are evaluated against the placed scene. Anticipatory-authoring frames (a binding authored " +
            "expecting a post-merge location) are not distinguished, and an unresolved binding there may be " +
            "intentional. Build-time object *deletions* (as opposed to moves) are also not visible here.";

        private const string DiscretionLimitsLine =
            "Known limitations, left to model discretion (not auto-resolved): (1) an Armature-Link-relocated bone " +
            "— the mergeable's own bone animated at a base-armature path it only occupies post-merge, so it fails " +
            "in-scene yet the build relocates it; (2) a binding typed for a Unity constraint (e.g. RotationConstraint) " +
            "whose scene object carries the VRChat equivalent (VRCRotationConstraint) or vice-versa — it works at " +
            "runtime though it will not resolve here.";

        // ── Conditional Notes lines + the fail-loud label ─────────────────────────────────────────────
        // Tests assert these constants, so the emit sites below stay their only literal.

        internal const string VariantSetNoteLine =
            "Live state is measured relative to this avatar root. A `[not-live]` physbone on a base bone " +
            "is often one of a per-range variant set an FX layer switches between, and de-conflicting against the " +
            "wrong member is silent (docs/outfits.md §The FX controller is the authoritative map). Which member the " +
            "layer selects is a property of the graph, not of the scene's static state.";

        internal const string InactiveRootNoteLine =
            "This avatar root is not active in the scene, so nothing under it is running; the live markers " +
            "above are relative to the root and still discriminate between hosts.";

        internal const string FailLoudNotePrefix = "fail-loud: ";

        /// <summary>Fires iff the inspected root carries any MA relocator, so a clean anchor-seam count
        /// reads as the scoped result it is rather than as whole-avatar confirmation.</summary>
        internal const string AnchorSeamScopeLine =
            "anchor-seam is scoped to VRCFury FullController parameter renames: a VRCContactReceiver, " +
            "VRCRaycast or VRCPhysBone whose parameter its FullController declares, under one of the MA " +
            "components that reparent a node (BoneProxy, MergeArmature, WorldFixedObject, and ReplaceObject " +
            "for the replacement's own subtree only). A FullController mounted on the avatar root is " +
            "exempt, since every MA destination stays under it. The FullController's global-parameter settings are not " +
            "evaluated, and a parameter it keeps global is not renamed, so is not broken. Merged clip " +
            "bindings are not in scope: a relocated one survives, and one whose target the build destroys " +
            "is named by VRCFury's own build warning.";

        internal const string AnchorSeamNoteLine =
            "An anchor-seam offender is a parameter-carrying component that an MA relocator moves out of " +
            "its VRCFury FullController's subtree. The FullController renames its parameters per instance, " +
            "then renames the matching components it still finds under itself — after MA has moved them — so " +
            "a moved one keeps writing the bare name while the controller reads only the renamed one, and no " +
            "build message says so. Whether the relocator acts in THIS build is not evaluated (each acts " +
            "only once its target resolves, and a bare module prefab resolves none). The repair keeps the " +
            "component inside the FullController's subtree: constrain it to the anchor instead of parenting " +
            "it there, or anchor with a VRCFury ArmatureLink, which moves after the rename " +
            "(docs/nondestructive.md §Choosing a framework).";

        internal const string BlendshapeActionNoteLine =
            "VRCFury builds a BlendShape action whose shape no renderer it searches carries to nothing, and says " +
            "nothing. The repair is the shape (author the key on the mesh that should carry it) or the action (name a " +
            "shape the set carries, point it at the renderer that has it, or remove it). Objects the build strips " +
            "first (tagged EditorOnly, or carrying a VRCFury Delete During Upload) count on neither side. Not modelled: " +
            "a mesh or shape a build step adds, and an un-upgraded legacy field (Breathing's version-0 `blendshape` " +
            "string), which VRCFury converts to an action only on upgrade.";

        internal const string VrcfComponentFullName = "VF.Model.VRCFury";
        internal const string VrcfBlendShapeActionFullName = "VF.Model.StateAction.BlendShapeAction";
        internal const string EditorOnlyTag = "EditorOnly";

        // The MA components that reparent a node: BoneProxyProcessor, MergeArmatureHook and
        // WorldFixedObjectProcessor SetParent the GameObject they sit on, and ReplaceObjectPass SetParents the
        // replacement (its own GameObject) into the target's place. VisibleHeadAccessoryProcessor is absent: it
        // reparents only the clones it creates. VRCFury's ArmatureLink is absent: FeatureOrder runs it after
        // FullController, so the rename has already reached the components it moves. Each relocator acts only
        // once its target resolves, and this class does not check that: an entry is scanned as a bare prefab
        // where no target can resolve, so gating on resolution would report every module clean.
        internal static readonly string[] MaRelocatorTypeNames =
        {
            "nadena.dev.modular_avatar.core.ModularAvatarBoneProxy",
            "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature",
            "nadena.dev.modular_avatar.core.ModularAvatarWorldFixedObject",
            "nadena.dev.modular_avatar.core.ModularAvatarReplaceObject",
        };

        // The components FullControllerBuilder renames after it has renamed its own parameters, each with the
        // names that decide whether it does: a receiver on its bare parameter, a raycast or physbone on the
        // parameters its runtime writes. The rename applies iff one of those names is among the parameters
        // the FullController declares.
        internal static readonly (string type, string[] suffixes)[] ParamCarriers =
        {
            ("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver", new[] { "" }),
            ("VRC.SDK3.Avatars.Components.VRCRaycast", new[] { "_Hit", "_Ratio", "_Distance" }),
            ("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone", new[] { "_IsGrabbed", "_Angle", "_Stretch", "_Squish", "_IsPosed" }),
        };

        // ── Injectable seams (internal) ───────────────────────────────────────────────────────────────
        // A test swaps one in SetUp and restores it in TearDown to force a degrade or fail-loud branch the live
        // MA/VRCFury types cannot reach. Defaults are the real behaviour.

        /// <summary>Every node under the root carrying an MA relocator → its relocator label.</summary>
        internal static Func<GameObject, Dictionary<GameObject, string>> CollectAnchors = DefaultCollectAnchors;

        private static Dictionary<GameObject, string> DefaultCollectAnchors(GameObject root)
        {
            var map = new Dictionary<GameObject, string>();
            if (root == null) return map;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue; // a missing MonoBehaviour script serializes as a null component
                string full = c.GetType().FullName;
                foreach (var t in MaRelocatorTypeNames)
                {
                    if (full != t) continue;
                    string label = full.Substring(full.LastIndexOf('.') + 1);
                    // One GameObject can carry two relocators: MA forbids duplicates of one type, not the pair.
                    map[c.gameObject] = map.TryGetValue(c.gameObject, out var prior) ? prior + "+" + label : label;
                    break;
                }
            }
            return map;
        }

        // ── Merge-conflict seams (default = real; tests swap fakes) ──────────────────────────────────
        /// <summary>Whole-avatar merge→base pairs (may contain null sides) + a partial-map note (null if clean).</summary>
        internal static Func<GameObject, (List<(Transform merge, Transform baseT)> pairs, string note)> ResolveMergePairs =
            DefaultResolveMergePairs;
        /// <summary>Every dynamics component's (host, driven target, category, display detail). detail="" except colliders.</summary>
        internal static Func<GameObject, List<(Component host, Transform target, string category, string detail)>> CollectDynamicsTargets =
            DefaultCollectDynamicsTargets;

        private static (List<(Transform, Transform)>, string) DefaultResolveMergePairs(GameObject avatarGO)
        {
            var res = CheckSeam.ResolveMergeMap(avatarGO, avatarGO);
            // ScaleBakeReason stays out of the note: scale-at-bake changes a transform's baked scale, not which
            // transform two components collide on, so those pairs stay valid for collision detection.
            string note = (res.ReflectError ?? res.UnresolvableReason);
            if (note != null) note = "merge map partial — some seams didn't resolve; conflicts may be under-reported (" + note + ")";
            var pairs = new List<(Transform, Transform)>();
            foreach (var p in res.Pairs) pairs.Add((p.Merge, p.Base));
            return (pairs, note);
        }

        // The drift canary iterates this too, so it pins the production strings.
        internal static readonly (string category, string typeName, string getter, bool withShape)[] DynamicsCategories =
        {
            ("physbone", "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone",         "GetRootTransform", false),
            ("collider", "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider", "GetRootTransform", true),
        };

        private static List<(Component, Transform, string, string)> DefaultCollectDynamicsTargets(GameObject avatarGO)
        {
            var result = new List<(Component, Transform, string, string)>();
            foreach (var c in DynamicsCategories)
                AddCategory(avatarGO, result, c.category, c.typeName, c.getter, c.withShape);
            return result;
        }

        private static void AddCategory(GameObject avatarGO, List<(Component, Transform, string, string)> result,
            string category, string typeName, string getterName, bool withShape)
        {
            var type = VendorReflect.FindType(typeName);
            if (type == null) return;
            var getter = type.GetMethod(getterName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getter == null)
            {
                // Skip the whole category: falling back to own-transform for every component would fabricate
                // plausible-but-wrong targets.
                Debug.LogWarning("[CheckAvatar] dynamics getter '" + typeName + "." + getterName
                               + "' did not reflect (API drift) — skipping the " + category + " category.");
                return;
            }
            foreach (var comp in avatarGO.GetComponentsInChildren(type, true))
            {
                if (comp == null) continue;
                try
                {
                    Transform target = getter.Invoke(comp, null) as Transform;
                    if (target == null) target = comp.transform; // getter returned null ⇒ own transform (legit convention)
                    result.Add((comp, target, category, withShape ? ColliderDetail(comp) : ""));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[CheckAvatar] dynamics target read failed on " + PathOf(comp.gameObject)
                                   + " (" + e.GetType().Name + ") — skipping this component.");
                }
            }
        }

        private static string ColliderDetail(Component collider)
        {
            try
            {
                var t = collider.GetType();
                object shape = t.GetField("shapeType")?.GetValue(collider);
                object radius = t.GetField("radius")?.GetValue(collider);
                object height = t.GetField("height")?.GetValue(collider);
                return "shape=" + shape + " radius=" + radius + " height=" + height;
            }
            catch { return "shape=?"; }
        }

        // Two colliders within this distance on every compared measure are one shape stacked twice. Vendor
        // collider values are authored at 5 mm–1 cm grain and a copied component matches to float error, so
        // the tolerance sits under the smallest intended difference.
        internal const float SameShapeToleranceMeters = 0.002f;

        // A collider's shape in world metres, its local fields read through `frame`'s position and rotation
        // (scale always from its own root). Both frameworks keep a collider's referenced bone as its own
        // transform, but where it lands differs: MA reparents it world-position-stays, so the placed root is
        // the shipped frame; a VRCFury ArmatureLink with align on (the default for a recursive link) snaps it
        // onto the avatar bone, so the merge target's pose is. The ends sit at ±height/2 on the axis — a
        // consistent comparator whichever way `height` counts its caps.
        private struct ColliderShape { public string Type; public bool Inside; public float Radius; public Vector3 Center, Axis, EndA, EndB; }

        // Null when any field cannot be read — the caller treats that as matching everything (fail toward
        // flagging), never as distinct. A null frame is the collider's own root.
        private static ColliderShape? ReadColliderShape(Component collider, Transform frame = null)
        {
            try
            {
                var t = collider.GetType();
                object shape = t.GetField("shapeType")?.GetValue(collider);
                object inside = t.GetField("insideBounds")?.GetValue(collider);
                object radius = t.GetField("radius")?.GetValue(collider);
                object height = t.GetField("height")?.GetValue(collider);
                object pos = t.GetField("position")?.GetValue(collider);
                object rot = t.GetField("rotation")?.GetValue(collider);
                if (shape == null || !(inside is bool) || !(radius is float) || !(height is float)
                    || !(pos is Vector3) || !(rot is Quaternion)) return null;
                var root = t.GetField("rootTransform")?.GetValue(collider) as Transform;
                if (root == null) root = collider.transform;
                var ls = root.lossyScale;
                float s = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                if (frame == null) frame = root;
                var center = frame == root ? root.TransformPoint((Vector3)pos)
                    : frame.position + frame.rotation * Vector3.Scale(ls, (Vector3)pos);
                var axis = frame.rotation * (Quaternion)rot * Vector3.up;
                var half = axis * ((float)height * 0.5f * s);
                return new ColliderShape {
                    Type = shape.ToString(), Inside = (bool)inside, Radius = (float)radius * s,
                    Center = center, Axis = axis, EndA = center - half, EndB = center + half,
                };
            }
            catch { return null; }
        }

        // An unrecognized kind matches (fail toward flagging).
        private static bool SameColliderShape(ColliderShape x, ColliderShape y)
        {
            const float tol = SameShapeToleranceMeters;
            if (x.Type != y.Type || x.Inside != y.Inside) return false;
            switch (x.Type)
            {
                case "Sphere":
                    return Mathf.Abs(x.Radius - y.Radius) <= tol && (x.Center - y.Center).magnitude <= tol;
                case "Capsule":
                    if (Mathf.Abs(x.Radius - y.Radius) > tol) return false;
                    bool straight = (x.EndA - y.EndA).magnitude <= tol && (x.EndB - y.EndB).magnitude <= tol;
                    bool flipped = (x.EndA - y.EndB).magnitude <= tol && (x.EndB - y.EndA).magnitude <= tol;
                    return straight || flipped;
                case "Plane":
                    return (x.Axis - y.Axis).magnitude <= tol
                        && Mathf.Abs(Vector3.Dot(y.Center - x.Center, x.Axis)) <= tol
                        && Mathf.Abs(Vector3.Dot(x.Center - y.Center, y.Axis)) <= tol;
                default:
                    return true;
            }
        }

        // Live null means no host to ask, which prints as neither marker. Component is kept only so a collider
        // group can be split by shape.
        private struct ConflictHost { public string Path; public string Type; public string Bone; public bool Mergeable; public string Detail; public bool? Live; public Component Component; }
        private struct MergeConflict { public string Category; public string FinalPath; public List<ConflictHost> Hosts; public int Seq; }

        // Whether a dynamics component is running, relative to the avatar root. activeInHierarchy would mark every
        // host not-live under a deactivated root, which is ordinary workflow, and a shared ancestor's state cannot
        // discriminate between members of one group. Ancestors above the root get InactiveRootNoteLine instead.
        private static bool? IsLive(Component host, Transform root)
        {
            if (host == null) return null;
            var beh = host as Behaviour;
            if (beh != null && !beh.enabled) return false;
            for (var t = host.transform; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        // A collider group's hosts as same-shape clusters, single-linkage, so a chain of near matches joins one
        // cluster (toward flagging). Any host whose shape cannot be read keeps the whole group together. A
        // mergeable host is read at its placed root (MA) and snapped onto `final` (VRCFury align), and a pair
        // matches if any reading of one matches any reading of the other, since which framework merges it is
        // not known here.
        private static List<List<ConflictHost>> SplitByShape(List<ConflictHost> hosts, Transform final)
        {
            var shapes = new List<List<ColliderShape>>();
            foreach (var h in hosts)
            {
                var s = h.Component != null ? ReadColliderShape(h.Component) : null;
                if (s == null) return new List<List<ConflictHost>> { hosts };
                var readings = new List<ColliderShape> { s.Value };
                var snapped = h.Mergeable && final != null ? ReadColliderShape(h.Component, final) : null;
                if (snapped != null) readings.Add(snapped.Value);
                shapes.Add(readings);
            }
            var parent = new int[hosts.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < hosts.Count; i++)
                for (int j = i + 1; j < hosts.Count; j++)
                    if (AnyReadingMatches(shapes[i], shapes[j])) parent[Find(j)] = Find(i);
            var byRoot = new Dictionary<int, List<ConflictHost>>();
            var clusters = new List<List<ConflictHost>>();
            for (int i = 0; i < hosts.Count; i++)
            {
                int r = Find(i);
                if (!byRoot.TryGetValue(r, out var c)) { byRoot[r] = c = new List<ConflictHost>(); clusters.Add(c); }
                c.Add(hosts[i]);
            }
            return clusters;
        }

        private static bool AnyReadingMatches(List<ColliderShape> a, List<ColliderShape> b)
        {
            foreach (var x in a) foreach (var y in b) if (SameColliderShape(x, y)) return true;
            return false;
        }

        private static bool AnyMixedLivePhysboneGroup(List<MergeConflict> conflicts)
        {
            foreach (var mc in conflicts)
            {
                if (mc.Category != "physbone") continue;
                int live = 0, notLive = 0;
                foreach (var h in mc.Hosts)
                {
                    if (h.Live == true) live++;
                    else if (h.Live == false) notLive++;
                }
                if (live >= 1 && notLive >= 1 && live + notLive >= 2) return true;
            }
            return false;
        }

        // ── Public API ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Classify the breaks on the in-scene avatar at <paramref name="avatarRoot"/> (a scene hierarchy
        /// path, else numeric instance id, else name). Returns a one-line summary; a real run ends with the
        /// RunLog path in-band (<c>… =&gt; PASS|CLASSIFY | log=&lt;path&gt;</c>). Bad input (unresolved root / no
        /// VRCAvatarDescriptor) is a bare <c>[CheckAvatar] FAIL: …</c> with no trailer.</summary>
        public static string Run(string avatarRoot)
        {
            var handle = SceneHandle.Resolve(avatarRoot);
            if (!handle.Ok) return Refuse(handle.Refusal);
            var avatarGO = handle.Object;

            var descriptor = avatarGO.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                return Refuse("'" + avatarRoot + "' has no VRCAvatarDescriptor — Run expects the avatar (descriptor) root");

            var rep = new Report { Root = avatarGO };
            var pairs = MergeSurfaces.Enumerate(avatarGO, descriptor,
                (c, anchor) => SurfaceUnreflected(c, anchor, rep),
                (c, frame) => { var n = MaFrameUncertaintyNote(c, avatarGO, frame); if (n != null) rep.FrameUncertain.Add(n); },
                (c, what) => SurfaceUnlintable(c, what, rep));

            // ---- MA scene-ref detection — generic over EVERY component ---------------------------------
            foreach (var c in avatarGO.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                ScanSceneRefs(c, avatarGO, rep);
            }

            // ---- Clip-binding classification (reuse CheckAnimator's walk, demotion off) -----------------
            // Dedup per unique (controller, clip, binding path, binding type): one authored curve expands to
            // several component curves that share a path+type, and a controller shared across frames is walked
            // once per frame. The key omits the frame, so one controller mounted by both a descriptor layer (no
            // rewrite) and a VRCF FullController (rewrite) can add an extra CLASSIFY but never hide a break.
            var clipSeen = new HashSet<(int ctrl, int clip, string path, Type type)>();
            foreach (var p in pairs)
            {
                foreach (var (clip, b) in CollectUnresolvedBindingsCalled(p.Controller, p.Roots, p.PathRewrite,
                             p.Kind == FrameKind.VRCF ? avatarGO : null))
                {
                    string clipAssetPath = AssetDatabase.GetAssetPath(clip);
                    if (IsSdkProxyClip(clipAssetPath)) continue;
                    if (!clipSeen.Add((p.Controller.GetInstanceID(), clip.GetInstanceID(), b.path, b.type))) continue;
                    rep.ClipBindings.Add(new Offender
                    {
                        Kind = "clip-binding",
                        Animator = p.Controller.name,
                        Clip = clip.name,
                        Path = b.path,
                        ClipAssetPath = clipAssetPath,
                        Host = p.Label,
                    });
                }
            }

            // ---- anchor-seam classification ------------------------------------------------------------
            // Enumerate above already surfaced FullController frame drift, so the walk reports only the reflection
            // points it adds.
            rep.AnchorsPresent = CollectAnchors(avatarGO);
            foreach (var hit in CollectAnchorSeams(avatarGO, rep.AnchorsPresent, (c, anchor) => SurfaceUnreflected(c, anchor, rep), reportFrameDrift: false, avatarRoot: avatarGO))
                rep.AnchorSeams.Add(new Offender
                {
                    Kind = "anchor-seam",
                    Path = PathOf(hit.Carrier.gameObject),
                    Carrier = hit.Carrier.GetType().Name,
                    Parameter = hit.Parameter,
                    Anchor = PathOf(hit.Anchor),
                    AnchorLabel = hit.AnchorLabel,
                    Host = "VRCFury FullController @ " + PathOf(hit.FullController.gameObject),
                });

            ScanMergeConflicts(avatarGO, rep);

            var actionsUnchecked = new HashSet<Component>();
            foreach (var hit in CollectBlendshapeActions(avatarGO,
                         (c, field) => { if (actionsUnchecked.Add(c)) rep.UnlintableActions++; BlendshapeUnreflected(c, field, rep); },
                         (type, n) => { rep.UnlintableActions += n; BlendshapeTypeMissing(type, n, rep); }))
                rep.BlendshapeActions.Add(new Offender
                {
                    Kind = "blendshape-action",
                    Path = hit.Shape,
                    Host = "VRCFury " + hit.Feature + " @ " + PathOf(hit.Host.gameObject),
                    Property = hit.Property,
                    Searched = hit.Searched,
                });

            return Emit(rep);
        }

        /// <summary>Prefix marking a line that is NOT a finding but a reason the scan could not be trusted. A
        /// caller folding both into one list must treat a degraded line as at least as serious as a finding,
        /// never as noise to filter.</summary>
        public const string DegradedPrefix = "scan not trustworthy: ";

        /// <summary>Prefix marking a line that is neither a finding nor a failure but a bound on what the scan
        /// could see. A caller must not fold these into a pass/fail tally — surface them beside the verdict,
        /// or a scope note becomes a spurious failure.</summary>
        public const string ScopePrefix = "scan scope: ";

        /// <summary>The anchor-seam class alone, on any GameObject root — no descriptor required, so a bare
        /// module prefab can be scanned outside an avatar. Returns one rendered line per offender, plus any
        /// fail-loud note prefixed with <see cref="DegradedPrefix"/> and any scope bound prefixed with
        /// <see cref="ScopePrefix"/>. A list with no offender line means the scan ran and found nothing; it
        /// never means the scan was skipped, because every reason a scan could not run lands as a degraded
        /// line rather than as silence.</summary>
        public static List<string> ScanAnchorSeams(GameObject root)
        {
            var lines = new List<string>();
            if (root == null)
            {
                lines.Add(DegradedPrefix + "null root — nothing was scanned");
                return lines;
            }

            var rep = new Report { Root = root };
            var anchors = CollectAnchors(root);
            foreach (var hit in CollectAnchorSeams(root, anchors, (c, anchor) => SurfaceUnreflected(c, anchor, rep), reportFrameDrift: true,
                avatarRoot: root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() != null ? root : null))
                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} @ `{1}` parameter `{2}`, moved by {3} @ `{4}` [VRCFury FullController @ {5}]",
                    hit.Carrier.GetType().Name, PathOf(hit.Carrier.gameObject), hit.Parameter, hit.AnchorLabel,
                    PathOf(hit.Anchor), PathOf(hit.FullController.gameObject)));

            foreach (var n in rep.Notes) lines.Add(DegradedPrefix + n);
            if (anchors.Count > 0) lines.Add(ScopePrefix + AnchorSeamScopeLine);
            return lines;
        }

        private static void SurfaceUnreflected(Component c, string anchor, Report rep)
        {
            string msg = "[CheckAvatar] frame field '" + anchor + "' on " + c.GetType().Name + " @ " + PathOf(c.gameObject)
                       + " did not reflect — surfacing the merged animator anyway (not dropped); its frame is best-effort.";
            Debug.LogWarning(msg);
            rep.Notes.Add(FailLoudNotePrefix + msg.Substring("[CheckAvatar] ".Length));
        }

        private static void SurfaceUnlintable(Component c, string what, Report rep)
        {
            string msg = "[CheckAvatar] surface @ " + PathOf(c.gameObject) + " mounts " + what
                       + " — its bindings were NOT checked by this run.";
            Debug.LogWarning(msg);
            rep.Notes.Add(FailLoudNotePrefix + msg.Substring("[CheckAvatar] ".Length));
            rep.UnlintableSurfaces++;
        }

        private static void BlendshapeUnreflected(Component c, string field, Report rep)
        {
            string msg = "[CheckAvatar] VRCFury field '" + field + "' on " + c.GetType().Name + " @ " + PathOf(c.gameObject)
                       + " did not reflect — BlendShape actions it covers were NOT checked by this run.";
            Debug.LogWarning(msg);
            rep.Notes.Add(FailLoudNotePrefix + msg.Substring("[CheckAvatar] ".Length));
        }

        private static void BlendshapeTypeMissing(string type, int components, Report rep)
        {
            string msg = "[CheckAvatar] VRCFury type '" + type + "' does not resolve in the loaded VRCFury — no BlendShape action on any of the "
                       + components + " VRCFury components under the root was checked by this run; re-pin CheckAvatar.VrcfBlendShapeActionFullName.";
            Debug.LogWarning(msg);
            rep.Notes.Add(FailLoudNotePrefix + msg.Substring("[CheckAvatar] ".Length));
        }

        // The frame caveat beside an MA MergeAnimator whose relativePathRoot did not resolve. Two shapes reach
        // the own-GameObject fallback, and both get a note because the scan emits an MA-scene-ref offender for
        // each and an offender with no frame line reads as a dropped ref: a non-empty referencePath that does
        // not resolve (the frame is a guess), and an empty one carrying a live targetObject (the frame is MA's
        // own fallback; the ref is what is broken). Both halves empty is the intentional-empty the scan exempts,
        // so it gets no caption.
        private static string MaFrameUncertaintyNote(Component c, GameObject avatarGO, FrameResult frame)
        {
            if (frame.IsAbsolute) return null;
            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return null; }
            var rel = so.FindProperty("relativePathRoot");
            if (rel == null)
                return "frame-uncertain: the MA relativePathRoot field on '" + PathOf(c.gameObject)
                     + "' did not reflect (API drift) — bindings were resolved against the fallback frame (its own GameObject).";
            var pathChild = rel.FindPropertyRelative("referencePath");
            var targetChild = rel.FindPropertyRelative("targetObject");
            string refPath = pathChild != null ? pathChild.stringValue : "";
            if (string.IsNullOrEmpty(refPath))
            {
                if (targetChild == null || targetChild.objectReferenceValue == null) return null;
                return "frame-certain: bindings for the animator on '" + PathOf(c.gameObject)
                     + "' were resolved against its own GameObject — the MA relativePathRoot carries a targetObject but no"
                     + " referencePath, which is exactly what the build resolves it to. See the matching MA-scene-ref"
                     + " offender: the ref reads resolved in the inspector and is not there at bake, so if that targetObject"
                     + " was meant to be the frame, these bindings are counted against the wrong one.";
            }
            if (TryResolveSceneRef(rel, c, avatarGO, out _)) return null;
            return "frame-uncertain: bindings for the animator on '" + PathOf(c.gameObject)
                 + "' were resolved against the fallback frame (its own GameObject) because the MA relativePathRoot '"
                 + refPath + "' did not resolve — see the matching MA-scene-ref offender. These bindings are counted, not dropped.";
        }

        // ── MA scene-ref detection ────────────────────────────────────────────────────────────────────

        // A property carrying both a referencePath(string) child and a targetObject(objref) child is an
        // AvatarObjectReference. Only BOTH halves empty is exempt: an empty referencePath carrying a live
        // targetObject is the silent no-op nondestructive.md §Reference hardening names.
        // What that costs is the consumer's business and this walk is generic, so the offender says only that no
        // path was written: the reactive family / BlendshapeSync / Mesh Settings resolve nothing, while a
        // MergeAnimator or MergeBlendTree relativePathRoot silently falls back to the component's own GameObject
        // (MergeAnimatorProcessor / MergeBlendTreePass). Only MergeAnimator gets a frame caption
        // (MaFrameUncertaintyNote): TryMaFrame's type gate (CheckAnimator.cs) excludes MergeBlendTree, so read
        // its captionless offender as the same relocated-frame class. If that gate is ever widened,
        // MaFrameUncertaintyNote's hardcoded so.FindProperty("relativePathRoot") (lowercase) won't find it:
        // ModularAvatarMergeBlendTree's field is RelativePathRoot (PascalCase), and FindProperty is
        // case-sensitive, so it would silently take the field-drift branch on a field that is actually present.
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
                var targetGO = targetChild.objectReferenceValue as GameObject;
                if (string.IsNullOrEmpty(refPath) && targetGO == null) continue; // unset by design

                if (TryResolveSceneRef(it.Copy(), c, avatarGO, out _)) continue;
                rep.SceneRefs.Add(new Offender
                {
                    Kind = "MA-scene-ref",
                    Path = string.IsNullOrEmpty(refPath)
                         ? "(unset referencePath; targetObject '" + PathOf(targetGO) + "' only — write the path)"
                         : refPath,
                    Host = c.GetType().Name + " @ " + PathOf(c.gameObject),
                });
            }
        }

        // Resolve an AvatarObjectReference property. Authoritative path: box it (boxedValue THROWS for
        // unsupported shapes) and invoke the pinned Get(Component), which returns null on an empty referencePath
        // BEFORE it looks at targetObject, then lets a live targetObject win only under the avatar root. The
        // static Get(SerializedProperty) overload the inspector uses has the opposite order and no empty-path
        // guard but keeps the IsChildOf gate, so an out-of-avatar targetObject resolves under NEITHER overload.
        // On any reflective failure, warn naming the broken anchor and self-resolve from the SerializedProperty
        // children in that same order.
        private static bool TryResolveSceneRef(SerializedProperty aor, Component host, GameObject avatarGO, out string refPath)
        {
            var pathChild = aor.FindPropertyRelative("referencePath");
            var targetChild = aor.FindPropertyRelative("targetObject");
            refPath = pathChild != null ? pathChild.stringValue : "";
            string reason = null;

            object boxed = null;
            try { boxed = VendorReflect.GetBoxedValue(aor); }
            catch (Exception e) { reason = "boxedValue threw (" + e.GetType().Name + ")"; }

            if (reason == null && boxed != null)
            {
                var mi = VendorReflect.ResolveAorGetOverload(boxed.GetType());
                if (mi == null) reason = "Get(Component) overload unreachable (MA API drift / absent)";
                else
                {
                    try { return (mi.Invoke(boxed, new object[] { host }) as GameObject) != null; }
                    catch (Exception e) { reason = "Get(Component) invoke threw (" + VendorReflect.DescribeInvokeError(e) + ")"; }
                }
            }
            else if (reason == null) reason = "boxedValue was null";

            Debug.LogWarning("[CheckAvatar] scene-ref resolve degraded on " + PathOf(host.gameObject)
                           + " (" + reason + ") — self-resolving from serialized children (empty referencePath first, then an in-avatar targetObject, then the path).");

            var targetGO = targetChild != null ? targetChild.objectReferenceValue as GameObject : null;
            if (string.IsNullOrEmpty(refPath)) return false;
            if (targetGO != null && targetGO.transform.IsChildOf(avatarGO.transform)) return true;
            if (refPath == MaAvatarRootSentinel) return true;
            return avatarGO.transform.Find(refPath) != null;
        }

        // ── Clip-binding walk ─────────────────────────────────────────────────────────────────────────
        // CollectUnresolvedBindings returns raw unresolved pairs and the build-rewrite demotion lives in
        // CheckAnimator's verdict, so every unresolved-in-scene binding here is an offender (CLASSIFY, never FAIL).
        private static IEnumerable<(AnimationClip clip, EditorCurveBinding binding)> CollectUnresolvedBindingsCalled(
            AnimatorController controller, List<GameObject> roots, Func<string, string> pathRewrite,
            GameObject absoluteRoot)
            => CheckAnimator.CollectUnresolvedBindings(controller, roots, pathRewrite, absoluteRoot);

        // VRChat SDK proxy animations (Packages/com.vrchat.*/…/ProxyAnim/proxy_*.anim) are humanoid-muscle
        // placeholders the SDK swaps at runtime; their bone-path bindings never resolve to a scene object and
        // are never a real break. Anchored to the SDK package, not the folder name alone, because this skip
        // could hide a break: a user asset that merely shares a `ProxyAnim` folder name still surfaces.
        private static bool IsSdkProxyClip(string clipAssetPath)
            => !string.IsNullOrEmpty(clipAssetPath)
               && clipAssetPath.StartsWith("Packages/com.vrchat.", StringComparison.OrdinalIgnoreCase)
               && clipAssetPath.IndexOf("/ProxyAnim/", StringComparison.OrdinalIgnoreCase) >= 0;

        // ── Anchor-seam walk ─────────────────────────────────────────────────────────────────────────────
        // A relocator on the mount or above it moves the FullController's whole subtree, component included, so
        // the builder still finds it; only a relocator between the component and the mount counts. A mount on the
        // avatar root is skipped whole: every MA destination lies under it. Nested FullControllers each report
        // their own.
        internal struct AnchorSeamHit
        {
            public Component FullController;
            public Component Carrier;
            public string Parameter;
            public GameObject Anchor;   // the nearest node between the carrier and the mount carrying a relocator
            public string AnchorLabel;  // that relocator's short type name(s)
        }

        internal static List<AnchorSeamHit> CollectAnchorSeams(GameObject root, Dictionary<GameObject, string> anchors,
            Action<Component, string> onUnreflected, bool reportFrameDrift, GameObject avatarRoot)
        {
            var hits = new List<AnchorSeamHit>();
            if (root == null || anchors == null || anchors.Count == 0) return hits;
            foreach (var fc in root.GetComponentsInChildren<Component>(true))
            {
                if (fc == null || !TryVrcfFrame(fc, out var controllers, out var frame)) continue;
                if (reportFrameDrift && frame.UnreflectedAnchor != null) onUnreflected(fc, frame.UnreflectedAnchor);
                if (frame.UnreflectedAnchor == "VRCF.content") continue; // no model to read; already surfaced
                var mount = frame.Root != null ? frame.Root : fc.gameObject;
                if (avatarRoot != null && mount == avatarRoot) continue;
                var declared = DeclaredParameters(fc, controllers, onUnreflected);
                if (declared.Count == 0) continue;
                foreach (var carrier in mount.GetComponentsInChildren<Component>(true))
                {
                    if (carrier == null) continue;
                    string[] suffixes = null;
                    foreach (var (type, sfx) in ParamCarriers)
                        if (carrier.GetType().FullName == type) { suffixes = sfx; break; }
                    if (suffixes == null) continue;
                    var prop = new SerializedObject(carrier).FindProperty("parameter");
                    if (prop == null || prop.propertyType != SerializedPropertyType.String)
                    {
                        onUnreflected(carrier, carrier.GetType().Name + ".parameter");
                        continue;
                    }
                    string p = prop.stringValue;
                    if (string.IsNullOrEmpty(p)) continue;
                    bool renamed = false;
                    foreach (var sfx in suffixes)
                        if (declared.Contains(p + sfx)) { renamed = true; break; }
                    if (!renamed) continue;
                    for (var t = carrier.transform; t != null && t != mount.transform; t = t.parent)
                    {
                        if (!anchors.TryGetValue(t.gameObject, out var label)) continue;
                        hits.Add(new AnchorSeamHit
                        {
                            FullController = fc, Carrier = carrier, Parameter = p, Anchor = t.gameObject, AnchorLabel = label,
                        });
                        break;
                    }
                }
            }
            return hits;
        }

        // Every parameter name the FullController declares — its controllers' parameters and its params
        // assets' entries, the names FullControllerBuilder renames before it walks its components.
        private static HashSet<string> DeclaredParameters(Component fc, List<AnimatorController> controllers,
            Action<Component, string> onUnreflected)
        {
            var names = new HashSet<string>();
            foreach (var ac in controllers)
                foreach (var p in ac.parameters) names.Add(p.name);
            var prms = new SerializedObject(fc).FindProperty("content")?.FindPropertyRelative("prms");
            if (prms == null || !prms.isArray)
            {
                onUnreflected(fc, "VRCF.content.prms");
                return names;
            }
            for (int i = 0; i < prms.arraySize; i++)
            {
                var objRef = prms.GetArrayElementAtIndex(i).FindPropertyRelative("parameters")?.FindPropertyRelative("objRef");
                if (objRef == null)
                {
                    onUnreflected(fc, "VRCF.content.prms.parameters");
                    continue;
                }
                if (objRef.objectReferenceValue is VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters ep && ep.parameters != null)
                    foreach (var p in ep.parameters)
                        if (p != null && !string.IsNullOrEmpty(p.name)) names.Add(p.name);
            }
            return names;
        }

        // ── BlendShape-action walk ───────────────────────────────────────────────────────────────────────
        // Every managed reference anywhere in a VRCFury feature whose type leaf is BlendShapeAction (a Toggle's
        // states, but also Talking, Blinking, a Puppet stop, a SmoothLoop's nested states). The search set is
        // VRCFury's own: of what StrippedAtBuild leaves, BlendshapeActionBuilder takes every SkinnedMeshRenderer,
        // inactive included, when allRenderers is on, else the one renderer named and only if it is one of those.
        // An action on a stripped component never builds, so it is not swept. Read by SerializedObject and
        // type-name leaf, never a typed reference.
        internal struct BlendshapeActionHit
        {
            public Component Host;      // the VRCFury component
            public string Feature;      // its feature's type leaf (Toggle, Talking, ...)
            public string Property;     // the action's serialized property path, the handle a SerializedObject fix takes
            public string Shape;        // the blendShape it names
            public string Searched;     // the renderer set it searched, named
        }

        internal static List<BlendshapeActionHit> CollectBlendshapeActions(GameObject root, Action<Component, string> onUnreflected,
            Action<string, int> onTypeMissing)
        {
            var hits = new List<BlendshapeActionHit>();
            if (root == null) return hits;
            var fury = new List<Component>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().FullName == VrcfComponentFullName && !StrippedAtBuild(c.transform, root.transform)) fury.Add(c);
            if (fury.Count == 0) return hits;
            // The leaf this walk matches must still exist, or a VRCFury rename would read as "no actions".
            if (VendorReflect.FindType(VrcfBlendShapeActionFullName) == null) { onTypeMissing(VrcfBlendShapeActionFullName, fury.Count); return hits; }
            var skins = new List<SkinnedMeshRenderer>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (!StrippedAtBuild(smr.transform, root.transform)) skins.Add(smr);
            foreach (var c in fury)
            {
                SerializedObject so;
                try { so = new SerializedObject(c); } catch { onUnreflected(c, "VRCF (SerializedObject)"); continue; }
                var content = so.FindProperty("content");
                if (content == null) { onUnreflected(c, "VRCF.content"); continue; }
                string ftn = content.managedReferenceFullTypename;
                if (string.IsNullOrEmpty(ftn)) { onUnreflected(c, "VRCF.content (feature type unresolved)"); continue; }
                string feature = Leaf(ftn);
                var it = content.Copy(); var end = content.GetEndProperty(true);
                bool enter = true;
                while (it.Next(enter) && !SerializedProperty.EqualContents(it, end))
                {
                    enter = true;
                    if (it.propertyType != SerializedPropertyType.ManagedReference || Leaf(it.managedReferenceFullTypename) != "BlendShapeAction") continue;
                    enter = false; // an action carries no nested actions
                    var shape = it.FindPropertyRelative("blendShape");
                    var all = it.FindPropertyRelative("allRenderers");
                    var named = it.FindPropertyRelative("renderer");
                    if (shape == null || shape.propertyType != SerializedPropertyType.String) { onUnreflected(c, "BlendShapeAction.blendShape"); continue; }
                    if (all == null || all.propertyType != SerializedPropertyType.Boolean) { onUnreflected(c, "BlendShapeAction.allRenderers"); continue; }
                    if (named == null || named.propertyType != SerializedPropertyType.ObjectReference) { onUnreflected(c, "BlendShapeAction.renderer"); continue; }
                    string name = shape.stringValue ?? "";
                    var target = named.objectReferenceValue as Renderer;
                    string searched;
                    bool found = false;
                    if (all.boolValue)
                    {
                        foreach (var smr in skins) if (Carries(smr, name)) { found = true; break; }
                        searched = "every SkinnedMeshRenderer under the avatar root the build keeps (" + skins.Count + ")";
                    }
                    else if (target == null) searched = "no renderer (allRenderers is off and renderer is unset)";
                    else if (!(target is SkinnedMeshRenderer)) searched = "renderer `" + PathOf(target.gameObject) + "` only, a " + target.GetType().Name + ", which VRCFury never searches";
                    else if (!target.transform.IsChildOf(root.transform)) searched = "renderer `" + PathOf(target.gameObject) + "` only, outside the avatar root, which VRCFury never searches";
                    else if (!skins.Contains((SkinnedMeshRenderer)target)) searched = "renderer `" + PathOf(target.gameObject) + "` only, which the build strips (EditorOnly or Delete During Upload)";
                    else { found = Carries((SkinnedMeshRenderer)target, name); searched = "renderer `" + PathOf(target.gameObject) + "` only"; }
                    if (found) continue;
                    hits.Add(new BlendshapeActionHit { Host = c, Feature = feature, Property = it.propertyPath, Shape = name, Searched = searched });
                }
            }
            return hits;
        }

        // What VRCFuryBuilder.Run removes before any builder runs (EditorOnlyUtils.RemoveEditorOnlyObjects): an object
        // strictly below the avatar root that is tagged EditorOnly or carries a VRCFury DeleteDuringUpload feature, with
        // everything under it. The root itself is never removed.
        private static bool StrippedAtBuild(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
            {
                if (t.CompareTag(EditorOnlyTag)) return true;
                foreach (var c in t.GetComponents<Component>())
                {
                    if (c == null || c.GetType().FullName != VrcfComponentFullName) continue;
                    SerializedProperty content;
                    try { content = new SerializedObject(c).FindProperty("content"); } catch { continue; }
                    if (content != null && Leaf(content.managedReferenceFullTypename) == "DeleteDuringUpload") return true;
                }
            }
            return false;
        }

        private static bool Carries(SkinnedMeshRenderer smr, string shape)
            => smr != null && smr.sharedMesh != null && smr.sharedMesh.GetBlendShapeIndex(shape) >= 0;

        private static string Leaf(string typeName)
            => string.IsNullOrEmpty(typeName) ? "" : typeName.Substring(typeName.LastIndexOf('.') + 1);

        // ── Merge-conflict scan ───────────────────────────────────────────────────────────────────────
        private static void ScanMergeConflicts(GameObject avatarGO, Report rep)
        {
            try
            {
                var (pairs, note) = ResolveMergePairs(avatarGO);
                if (note != null) rep.Notes.Add("fail-loud: " + note);

                var map = new Dictionary<Transform, Transform>();
                foreach (var (merge, baseT) in pairs)
                {
                    if (merge == null || baseT == null) continue;
                    if (!map.ContainsKey(merge)) map[merge] = baseT;
                }

                var targets = CollectDynamicsTargets(avatarGO);
                var groups = new Dictionary<(string cat, Transform final), List<ConflictHost>>();
                foreach (var (host, target, category, detail) in targets)
                {
                    if (target == null) continue;
                    bool mergeable = map.ContainsKey(target);
                    var key = (category, ResolveFinal(target, map));
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<ConflictHost>();
                    list.Add(new ConflictHost {
                        Path = host != null ? PathOf(host.gameObject) : "—",
                        Type = host != null ? host.GetType().Name : "—",
                        Bone = target.name,
                        Mergeable = mergeable, Detail = detail ?? "",
                        Live = IsLive(host, avatarGO.transform),
                        Component = host,
                    });
                }

                foreach (var kv in groups)
                {
                    if (kv.Value.Count < 2) continue;
                    var clusters = kv.Key.cat == "collider" ? SplitByShape(kv.Value, kv.Key.final) : new List<List<ConflictHost>> { kv.Value };
                    foreach (var cluster in clusters)
                    {
                        if (cluster.Count < 2) continue;
                        bool anyMergeable = false; foreach (var h in cluster) if (h.Mergeable) { anyMergeable = true; break; }
                        if (!anyMergeable) continue;
                        rep.MergeConflicts.Add(new MergeConflict {
                            Category = kv.Key.cat,
                            FinalPath = kv.Key.final != null ? PathOf(kv.Key.final.gameObject) : "—",
                            Hosts = cluster,
                            Seq = rep.MergeConflicts.Count,
                        });
                    }
                }
                // Dictionary iteration is unordered and List.Sort unstable, so the key is total for a byte-stable
                // RunLog: FinalPath can repeat (Unity allows duplicate sibling names), the first host's path can too
                // (several colliders on one GameObject), and emission order breaks that last tie.
                rep.MergeConflicts.Sort((x, y) =>
                {
                    int c = string.CompareOrdinal(x.Category, y.Category);
                    if (c != 0) return c;
                    c = string.CompareOrdinal(x.FinalPath, y.FinalPath);
                    if (c != 0) return c;
                    c = string.CompareOrdinal(x.Hosts[0].Path, y.Hosts[0].Path);
                    return c != 0 ? c : x.Seq.CompareTo(y.Seq);
                });
            }
            catch (Exception e)
            {
                string msg = "[CheckAvatar] merge-conflict scan degraded (" + e.GetType().Name + ": " + e.Message + ") — no conflicts reported.";
                Debug.LogWarning(msg);
                rep.Notes.Add("fail-loud: " + msg.Substring("[CheckAvatar] ".Length));
            }
        }

        private static Transform ResolveFinal(Transform target, Dictionary<Transform, Transform> map)
        {
            var seen = new HashSet<Transform>();
            var cur = target;
            while (cur != null && seen.Add(cur) && map.TryGetValue(cur, out var next)) cur = next;
            return cur;
        }

        // ── Output ────────────────────────────────────────────────────────────────────────────────────

        private static string Emit(Report rep)
        {
            int maSceneRef = rep.SceneRefs.Count;
            int clipBinding = rep.ClipBindings.Count;
            int anchorSeam = rep.AnchorSeams.Count;
            int mergeConflict = rep.MergeConflicts.Count;
            int blendshapeAction = rep.BlendshapeActions.Count;
            string result = (maSceneRef > 0 || clipBinding > 0 || anchorSeam > 0 || mergeConflict > 0 || blendshapeAction > 0) ? "CLASSIFY" : "PASS";

            string unlintablePart = (rep.UnlintableSurfaces > 0
                ? string.Format(CultureInfo.InvariantCulture, " unlintableSurfaces={0}", rep.UnlintableSurfaces) : "")
                + (rep.UnlintableActions > 0
                ? string.Format(CultureInfo.InvariantCulture, " unlintableActions={0}", rep.UnlintableActions) : "");
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckAvatar] {0}: maSceneRef={1} clipBinding={2} anchorSeam={3} mergeConflict={4} blendshapeAction={5}{6} => {7}",
                rep.Root.name, maSceneRef, clipBinding, anchorSeam, mergeConflict, blendshapeAction, unlintablePart, result);

            var sb = new StringBuilder();
            sb.Append("# CheckAvatar: ").Append(rep.Root.name).Append('\n');
            sb.Append("root: `").Append(PathOf(rep.Root)).Append("`  \n\n");
            sb.Append(summary.Substring("[CheckAvatar] ".Length)).Append('\n');

            sb.Append("\n## Counts\n\n");
            sb.Append("- maSceneRef: ").Append(maSceneRef).Append('\n');
            sb.Append("- clipBinding: ").Append(clipBinding).Append('\n');
            sb.Append("- anchorSeam: ").Append(anchorSeam).Append('\n');
            sb.Append("- mergeConflict: ").Append(mergeConflict).Append('\n');
            sb.Append("- blendshapeAction: ").Append(blendshapeAction).Append('\n');

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

            sb.Append("\n### anchor-seam\n\n");
            if (rep.AnchorSeams.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.AnchorSeams)
                sb.Append("- **anchor-seam** ").Append(o.Carrier)
                  .Append(" @ `").Append(o.Path)
                  .Append("` parameter=`").Append(o.Parameter)
                  .Append("` moved-by=").Append(o.AnchorLabel)
                  .Append(" @ `").Append(o.Anchor)
                  .Append("` [").Append(o.Host).Append("]\n");

            sb.Append("\n### merge-conflict\n\n");
            if (rep.MergeConflicts.Count == 0) sb.Append("_(none)_\n");
            else foreach (var mc in rep.MergeConflicts)
            {
                sb.Append("- **merge-conflict** category=`").Append(mc.Category)
                  .Append("` final=`").Append(mc.FinalPath).Append("`\n");
                foreach (var h in mc.Hosts)
                    sb.Append("  - ").Append(h.Mergeable ? "[mergeable] " : "[base] ")
                      .Append(h.Live == null ? "" : h.Live == true ? "[live] " : "[not-live] ").Append(h.Path)
                      .Append(" (").Append(h.Type).Append(", bone=`").Append(h.Bone).Append("`")
                      .Append(string.IsNullOrEmpty(h.Detail) ? "" : ", " + h.Detail).Append(")\n");
            }

            sb.Append("\n### blendshape-action\n\n");
            if (rep.BlendshapeActions.Count == 0) sb.Append("_(none)_\n");
            else foreach (var o in rep.BlendshapeActions)
                sb.Append("- **blendshape-action** shape=`").Append(o.Path)
                  .Append("` property=`").Append(o.Property)
                  .Append("` searched=").Append(o.Searched)
                  .Append(" [").Append(o.Host).Append("]\n");

            sb.Append("\n## Notes\n\n");
            sb.Append("- ").Append(ExcludedEdgeLine).Append('\n');
            sb.Append("- ").Append(DiscretionLimitsLine).Append('\n');
            foreach (var n in rep.FrameUncertain) sb.Append("- ").Append(n).Append('\n');
            foreach (var n in rep.Notes) sb.Append("- ").Append(n).Append('\n');
            if (rep.MergeConflicts.Count > 0)
                sb.Append("- MA prunes exact-duplicate physbones at build (PruneDuplicatePhysBones), so a flagged MA " +
                    "physbone pair may already be resolved — verify against a build. VRCFury has no such pass and neither " +
                    "framework prunes colliders; same-shape colliders and non-exact/non-zip-merged MA physbone pairs are the " +
                    "residue this check exists for.\n");
            if (rep.AnchorsPresent != null && rep.AnchorsPresent.Count > 0)
                sb.Append("- ").Append(AnchorSeamScopeLine).Append('\n');
            if (rep.AnchorSeams.Count > 0)
                sb.Append("- ").Append(AnchorSeamNoteLine).Append('\n');
            if (rep.BlendshapeActions.Count > 0)
                sb.Append("- ").Append(BlendshapeActionNoteLine).Append('\n');
            if (AnyMixedLivePhysboneGroup(rep.MergeConflicts))
                sb.Append("- ").Append(VariantSetNoteLine).Append('\n');
            if (!rep.Root.activeInHierarchy)
                sb.Append("- ").Append(InactiveRootNoteLine).Append('\n');

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "check-avatar_" + rep.Root.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogWarning(res);
            return res;
        }

        // ── Bad-input refusal (bare FAIL, no trailer) ─────────────────────────────────────────────────

        private static string Refuse(string why)
        {
            string err = "[CheckAvatar] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        private static string PathOf(GameObject go) => MergeSurfaces.PathOf(go);

        // ── Types ───────────────────────────────────────────────────────────────────────────────────

        private struct Offender
        {
            public string Kind;
            public string Path;          // MA-scene-ref: failing referencePath. clip-binding: binding scene path. anchor-seam: the carrier's scene path. blendshape-action: the shape name.
            public string Host;          // component/site label
            public string Animator;      // clip-binding only
            public string Clip;          // clip-binding only
            public string ClipAssetPath; // clip-binding only — what the compose agent routes on; distinct from Path
            public string Carrier;       // anchor-seam only — the parameter-carrying component's type name
            public string Parameter;     // anchor-seam only — the parameter it writes, which the build leaves bare
            public string Anchor;        // anchor-seam only — scene path of the relocated node (what a repair moves)
            public string AnchorLabel;   // anchor-seam only — the MA relocator's short type name(s)
            public string Property;      // blendshape-action only — the action's serialized property path on its VRCFury component
            public string Searched;      // blendshape-action only — the renderer set VRCFury searches for the shape
        }

        private class Report
        {
            public GameObject Root;
            public Dictionary<GameObject, string> AnchorsPresent; // null ⇒ anchor-seam never ran
            public readonly List<Offender> SceneRefs = new List<Offender>();
            public readonly List<Offender> ClipBindings = new List<Offender>();
            public readonly List<Offender> AnchorSeams = new List<Offender>();
            public readonly List<MergeConflict> MergeConflicts = new List<MergeConflict>();
            public readonly List<Offender> BlendshapeActions = new List<Offender>();
            public readonly List<string> FrameUncertain = new List<string>(); // frame caveats (uncertain AND certain-but-captioned)
            public readonly List<string> Notes = new List<string>();          // fail-loud + degrade notes
            public int UnlintableSurfaces;                                    // surfaces mounting a controller this door cannot walk
            public int UnlintableActions;                                     // VRCFury components whose BlendShape actions went unchecked
        }
    }
}
