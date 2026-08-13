using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.SceneManagement;
using static Ryan6Vrc.AgentTools.Editor.CheckAnimator; // FrameKind / FrameResult / TryMaFrame / TryVrcfFrame / CollectUnresolvedBindings

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Scene-scoped INSPECTION gate: classify the silent reference/identity breaks a mergeable placement
    /// leaves on an instantiated IN-SCENE avatar root. Two are path-encoded reference breaks a base rename
    /// (canonical <c>Body_base</c>→<c>Body_Base</c>) leaves behind; the third is a post-merge collision.
    ///   - <b>MA scene refs</b> (the reactive family / BlendshapeSync / Mesh Settings — anything carrying
    ///     the <c>referencePath</c>+<c>targetObject</c> <c>AvatarObjectReference</c> field pair) that no
    ///     longer resolve → the skill retargets them in place.
    ///   - <b>clip/controller bindings</b> (descriptor playable layers + every MA MergeAnimator / VRCFury
    ///     FullController merged animator) that resolve to no scene object → the skill owns the vendor
    ///     <c>.anim</c> and repaths (routed by the per-offender <c>clipAssetPath</c>).
    ///   - <b>anchor-seam</b> (the cross-framework break, docs/nondestructive.md §Choosing a framework
    ///     rule 1): a <b>VRCFury FullController-merged</b> binding that resolves here but whose resolved path
    ///     crosses a node carrying an MA relocator — the shape rule 1 forbids, because where that node does
    ///     move, VRCFury's merged clip is not repathed with it. Whether it moves in a given build is NOT
    ///     evaluated (each relocator acts only once its target resolves). Scoped to VRCF frames because the break is one-directional: the
    ///     reverse (an MA-merged clip through a VRCFury-moved node) is repaired by VRCFury's own move
    ///     service, and VRCFury's own <c>ArmatureLink</c> is therefore never an offender. The offender names
    ///     the <b>anchor</b>, which is the one thing no build message ever names → the agent re-anchors it
    ///     in the animating framework, or senses from inside by constraint.
    ///   - <b>merge-conflict</b> (NOT path-encoded — transform-identity, not a name): two+ dynamics
    ///     components in one category (physbone/collider/constraint) that resolve to the SAME post-merge
    ///     transform via the MA/VRCFury merge map, ≥1 of them mergeable-sourced — i.e. a mergeable's bone
    ///     name-merges onto a base bone that already carries the same kind of component, so both bake onto
    ///     one transform and fight → the agent de-conflicts. A base↔base duplicate (none mergeable) is not a
    ///     merge artifact and is dropped. Each offender is marked <c>[live]</c>/<c>[not-live]</c> relative to
    ///     the avatar root (per category: a constraint's <c>IsActive</c> counts alongside <c>enabled</c>), so
    ///     a mixed-live physbone group reads as the per-range variant set it usually is (docs/outfits.md)
    ///     rather than N components to reconcile.
    ///
    /// The first two resolve against the PLACED scene (the load-bearing model, spec D1): a to-be-merged bone
    /// is physically present pre-bake and resolves now; a base-rename break does not. So this predicts nothing
    /// about what the build will MOVE, and never depends on the <c>Armature.&lt;Name&gt;</c> convention.
    /// merge-conflict is the one class that reads the merge map (what the build will MERGE), not a scene path.
    /// <b>anchor-seam keeps the no-prediction model</b>: it asserts that a relocator component is PRESENT on
    /// the path — a scene state — and models nothing about where that component moves its node, whether the
    /// move disperses children, or whether a wholesale move preserves relative structure. An earlier build of
    /// this class did model those and was wrong in three review rounds; the state question is what replaced it.
    ///
    /// Verdict is <c>PASS</c> (clean) or <c>CLASSIFY</c> (any finding) — never <c>FAIL</c> for a finding (bad
    /// input alone bare-FAILs). No computed near-miss/absent/N-of-M SCORING: every class is a definite
    /// predicate (a ref resolves or it doesn't; a collision group is ≥2-with-a-mergeable or it isn't). The
    /// tool names offenders and their class; the compose agent applies discretion (findings are often
    /// intentional).
    ///
    /// Clip-binding detection REUSES <see cref="CheckAnimator"/>'s frame-detection + binding-walk +
    /// humanoid-curve-skip (called, not re-expressed) with the build-rewrite demotion flipped off. The
    /// surface enumeration, the VRCF ancestor-walk, and the fail-loud frame guards are net-new here.
    ///
    /// INSPECTION ONLY — mutates no scene state (scene.isDirty is unchanged across a call), writes no asset
    /// but its own RunLog. NEVER throws: every reflective hop is guarded and degrades with a loud warning.
    /// </summary>
    [AgentTool]
    public static class CheckAvatar
    {
        private const string MaObjRefTypeName = "nadena.dev.modular_avatar.core.AvatarObjectReference";
        // One literal for both resolvers: CheckAnimator's frame walk needs the same sentinel, and CheckAvatar
        // already depends on CheckAnimator (CollectUnresolvedBindings), so it is owned there.
        private const string MaAvatarRootSentinel = CheckAnimator.MaAvatarRootSentinel;

        // Standing Notes line — quoted verbatim from the spec §Excluded edge. Carried by EVERY RunLog so the
        // model's two known holes (anticipatory-authoring frames + build-time deletions) are stated-and-refused
        // on every run, with zero detection code.
        private const string ExcludedEdgeLine =
            "Bindings are evaluated against the placed scene. Anticipatory-authoring frames (a binding authored " +
            "expecting a post-merge location) are not distinguished, and an unresolved binding there may be " +
            "intentional. Build-time object *deletions* (as opposed to moves) are also not visible here.";

        // Two in-scene-unresolved patterns CheckAvatar surfaces honestly but does NOT auto-fix — commonly
        // intentional, left to model discretion (spec §Known limitations). Stated on every run.
        private const string DiscretionLimitsLine =
            "Known limitations, left to model discretion (not auto-resolved): (1) an Armature-Link-relocated bone " +
            "— the mergeable's own bone animated at a base-armature path it only occupies post-merge, so it fails " +
            "in-scene yet the build relocates it; (2) a binding typed for a Unity constraint (e.g. RotationConstraint) " +
            "whose scene object carries the VRChat equivalent (VRCRotationConstraint) or vice-versa — it works at " +
            "runtime though it will not resolve here.";

        // ── Conditional Notes lines + the R-H label: quoted once, here ────────────────────────────────
        // These three are the emitted wording for three GATED behaviours, and the gate — not the phrasing — is
        // what the tests prove. They were asserted as fragments in seven places (three of them negatives), so a
        // rewording reddened seven tests for no behaviour change. Per docs/tool-design.md §Duplication the
        // verbatim string lives at the canon and every other site routes to it: the tests assert these
        // constants, and the emit sites below are their only literal.

        /// <summary>Fires iff a mixed-live physbone group is present (<see cref="AnyMixedLivePhysboneGroup"/>).</summary>
        internal const string VariantSetNoteLine =
            "Live state is measured relative to this avatar root. A `[not-live]` physbone on a base bone " +
            "is often one of a per-range variant set an FX layer switches between, and de-conflicting against the " +
            "wrong member is silent (docs/outfits.md §The FX controller is the authoritative map). Which member the " +
            "layer selects is a property of the graph, not of the scene's static state.";

        /// <summary>Fires iff the inspected root is itself inactive, so `[live]` above reads relative, not absolute.</summary>
        internal const string InactiveRootNoteLine =
            "This avatar root is not active in the scene, so nothing under it is running; the live markers " +
            "above are relative to the root and still discriminate between hosts.";

        /// <summary>Prefix on the Notes entry <see cref="SurfaceUnreflected"/> adds; the remainder is the
        /// dynamic warning text, so only the label is a constant.</summary>
        internal const string FailLoudNotePrefix = "fail-loud (R-H): ";

        /// <summary>Fires iff the inspected avatar carries any MA relocator, so a clean anchor-seam count
        /// reads as the scoped result it is rather than as whole-avatar confirmation.</summary>
        internal const string AnchorSeamScopeLine =
            "anchor-seam is scoped to VRCFury FullController-merged bindings and to the four MA components " +
            "that reparent their own GameObject (BoneProxy, MergeArmature, WorldFixedObject, " +
            "VisibleHeadAccessory). A zero count says nothing about: MA-merged or descriptor-layer bindings " +
            "(the build repaths those); clips merged by VRCFury's AnimationClipAction family (Toggle, Modes, " +
            "Apply During Upload), which take the same rewrite stack and are not enumerated here; or " +
            "ModularAvatarReplaceObject, which relocates its target rather than itself and is not tracked.";

        /// <summary>Fires iff at least one anchor-seam offender exists.</summary>
        internal const string AnchorSeamNoteLine =
            "An anchor-seam offender is a binding path crossing a tracked MA relocator, which is a scene " +
            "state — whether that component relocates in THIS build is not evaluated (each of the four moves " +
            "only once its target resolves, and a bare module prefab scanned outside an avatar resolves " +
            "none). The shape is the finding either way, because repathing the clip cannot fix it and the " +
            "build's own warning names the binding but never the anchor. The repair is to put the move and " +
            "the animation in one framework — re-anchor the named node with a VRCFury ArmatureLink, which " +
            "relocates through VRCFury's own move service and repaths the merged clips with it — or to " +
            "animate the node by constraint from inside the subtree instead of by path " +
            "(docs/nondestructive.md §Choosing a framework; docs/gimmicks.md §Packaging).";

        // The MA components that reparent the GameObject they sit on, each confirmed against MA's own Editor
        // processor (BoneProxyProcessor, MergeArmatureHook, WorldFixedObjectProcessor,
        // VisibleHeadAccessoryProcessor all SetParent). Each moves only once its target RESOLVES, and this
        // class does not check that — deliberately: an entry is scanned as a bare prefab where no proxy
        // target can resolve, so gating on resolution would report every module clean. The finding is the
        // authored shape, not a predicted move. Membership is a type-name test and NOTHING more: no model of
        // where a component moves its node, or of whether the move disperses children.
        // ModularAvatarReplaceObject is deliberately absent — it relocates its *target* rather than itself,
        // so tracking it would mean resolving an AvatarObjectReference, which is exactly the build-modeling
        // this class is defined without. Its absence is stated on every scoped run (AnchorSeamScopeLine).
        // VRCFury's ArmatureLink is absent for a different reason: it is not a break at all. VRCFury repaths
        // its own merged clips when it moves, so a binding through an ArmatureLink is the sanctioned anchor
        // (docs/nondestructive.md §Choosing a framework rule 1), and flagging it would fail the very entry
        // that demonstrates the repair.
        internal static readonly string[] MaRelocatorTypeNames =
        {
            "nadena.dev.modular_avatar.core.ModularAvatarBoneProxy",
            "nadena.dev.modular_avatar.core.ModularAvatarMergeArmature",
            "nadena.dev.modular_avatar.core.ModularAvatarWorldFixedObject",
            "nadena.dev.modular_avatar.core.ModularAvatarVisibleHeadAccessory",
        };

        // Relocators this class knowingly does NOT track. Their presence alone fires the scope note on BOTH
        // doors, because an avatar or module anchored only by one of these is the single case where a zero
        // count is most misleading: the shape is exactly the one the class exists to catch, and it reports
        // clean.
        internal static readonly string[] MaUntrackedRelocatorTypeNames =
        {
            "nadena.dev.modular_avatar.core.ModularAvatarReplaceObject",
        };

        /// <summary>True iff any node under <paramref name="root"/> carries a relocator this class does not
        /// track. Shared by both doors — a scope claim that held on only one of them was a false statement in
        /// exactly the case it exists to cover.</summary>
        private static bool HasUntrackedRelocator(GameObject root)
        {
            if (root == null) return false;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                foreach (var t in MaUntrackedRelocatorTypeNames)
                    if (c.GetType().FullName == t) return true;
            }
            return false;
        }

        // ── Injectable seams (internal) ───────────────────────────────────────────────────────────────
        // Real MA/VRCF types always reflect and MA's Get(Component) is always reachable in this Editor, and an
        // absent serialized FIELD (the drift the fail-loud rail guards) can't be constructed with the live
        // types — so the degrade/fail-loud branches are otherwise unexercisable. These delegates run
        // UNCONDITIONALLY on the resolution hot path (no test-only conditional in production); a test swaps one
        // in SetUp and restores it in TearDown to force a branch. Defaults are the real behaviour.

        // The AOR boxing/pin seams (GetBoxedValue / ResolveAorGetOverload) live in VendorReflect — one home
        // for the vendor-invocation plumbing this scan and CheckAnimator's frame walk both resolve through.

        /// <summary>Every node under the root carrying an MA relocator → its relocator label. A test swaps a
        /// fake to exercise the walk without the live MA types.</summary>
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
                    // One GameObject can carry two relocators (MA forbids duplicates of one type, not the
                    // pair); name both rather than letting the second silently overwrite the first.
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
            // ScaleBakeReason is intentionally omitted from the note: scale-at-bake doesn't change WHICH
            // transform two components collide on (only its baked scale), so those pairs stay valid for
            // collision detection — only genuine drift (ReflectError) or a non-resolving seam (UnresolvableReason)
            // means the map is partial and conflicts may be under-reported.
            string note = (res.ReflectError ?? res.UnresolvableReason);
            if (note != null) note = "merge map partial — some seams didn't resolve; conflicts may be under-reported (" + note + ")";
            var pairs = new List<(Transform, Transform)>();
            foreach (var p in res.Pairs) pairs.Add((p.Merge, p.Base));
            return (pairs, note);
        }

        // (category, typeName, getter, withShape) — single source of truth for the three dynamics categories,
        // iterated by both the collector and the drift canary so the canary pins the real production strings.
        internal static readonly (string category, string typeName, string getter, bool withShape)[] DynamicsCategories =
        {
            ("physbone",   "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone",         "GetRootTransform",            false),
            ("collider",   "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider", "GetRootTransform",            true),
            ("constraint", "VRC.Dynamics.VRCConstraintBase",                            "GetEffectiveTargetTransform", false),
        };

        private static List<(Component, Transform, string, string)> DefaultCollectDynamicsTargets(GameObject avatarGO)
        {
            var result = new List<(Component, Transform, string, string)>();
            foreach (var c in DynamicsCategories)
                AddCategory(avatarGO, result, c.category, c.typeName, c.getter, c.withShape);
            return result;
        }

        // Reflect one dynamics category by typename; absent type ⇒ skip (never throw). Read the driven target via
        // the SDK's own null-resolving getter (null return ⇒ own transform). Every per-component hop guarded — a
        // single bad component warns loud and is skipped, never aborting the sweep.
        private static void AddCategory(GameObject avatarGO, List<(Component, Transform, string, string)> result,
            string category, string typeName, string getterName, bool withShape)
        {
            var type = VendorReflect.FindType(typeName);
            if (type == null) return; // SDK/category absent ⇒ skip
            var getter = type.GetMethod(getterName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getter == null)
            {
                // Type present but its getter renamed ⇒ API drift. Fail loud and skip the whole category — do
                // NOT silently fall back to own-transform for every component (that fabricates plausible-but-wrong
                // targets). The CI canary pins this getter, so this only fires on a live editor whose SDK drifted.
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

        // Best-effort collider shape for agent discretion; every field guarded (never throws).
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

        // Live is nullable: null means "no host to ask", which prints as neither marker — the same
        // absent-vs-false distinction the shape cell spells `—`.
        private struct ConflictHost { public string Path; public string Type; public string Bone; public bool Mergeable; public string Detail; public bool? Live; }
        private struct MergeConflict { public string Category; public string FinalPath; public List<ConflictHost> Hosts; }

        // Whether a dynamics component is running, measured RELATIVE TO THE AVATAR ROOT. Absolute
        // activeInHierarchy would be wrong here: this root can itself be deactivated (parking the avatars
        // you are not editing is ordinary workflow, and Resolve reaches an inactive root), and a shared
        // ancestor's state cannot discriminate between members of one group anyway — it would report every
        // host not-live at once, which is the opposite of the signal. Ancestors ABOVE the root are the
        // subject of the one caveat line instead.
        //
        // Each category has its own enable surface and they are not interchangeable: a VRC constraint is a
        // Behaviour but carries a second flag, `IsActive` — measured by reflecting VRCConstraintBase on the
        // loaded SDK (public bool field, alongside GlobalWeight), and set explicitly by our own
        // avatar-tools ConstrainedDuplicate when it wants a constraint to run. Test `enabled` alone and an
        // inert constraint reports as fighting.
        private static bool? IsLive(Component host, string category, Transform root)
        {
            if (host == null) return null;
            var beh = host as Behaviour;
            if (beh != null && !beh.enabled) return false;
            if (category == "constraint" && !ConstraintIsActive(host)) return false;
            for (var t = host.transform; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        // Reflected, not typed: this file reaches every dynamics type by name (DynamicsCategories) so an
        // absent SDK degrades to skip rather than a compile break. A field we cannot read is treated as
        // active — fail toward reporting the conflict, never toward silently excusing one.
        private static bool ConstraintIsActive(Component host)
        {
            var f = host.GetType().GetField("IsActive", BindingFlags.Public | BindingFlags.Instance);
            if (f == null || f.FieldType != typeof(bool)) return true;
            try { return (bool)f.GetValue(host); } catch { return true; }
        }

        // Scoped to what this change actually established: a PHYSBONE group carrying ≥2 hosts of mixed live
        // state. An unrelated disabled collider must not summon a paragraph about physbone variant sets.
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

        /// <summary>Classify the MA-scene-ref and clip-binding reference breaks on the in-scene avatar at
        /// <paramref name="avatarRoot"/> (a scene hierarchy path, else numeric instance id, else name —
        /// mirrors RenderAvatar's target resolution). Returns a one-line summary; a real run ends with the
        /// RunLog path in-band (<c>… =&gt; PASS|CLASSIFY | log=&lt;path&gt;</c>). Bad input (root not found /
        /// no VRCAvatarDescriptor) is a bare <c>[CheckAvatar] FAIL: …</c> with no trailer.</summary>
        public static string Inspect(string avatarRoot)
        {
            var avatarGO = Resolve(avatarRoot);
            if (avatarGO == null)
                return Refuse("avatar root '" + avatarRoot + "' not found — tried hierarchy path, instance id, then name in the active scene");

            var descriptor = avatarGO.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                return Refuse("'" + avatarRoot + "' has no VRCAvatarDescriptor — Inspect expects the avatar (descriptor) root");

            var rep = new Report { Root = avatarGO };
            var pairs = MergeSurfaces.Enumerate(avatarGO, descriptor, vrcfOnly: false,
                (c, anchor) => SurfaceUnreflected(c, anchor, rep),
                (c, frame) => { var n = MaFrameUncertaintyNote(c, avatarGO, frame); if (n != null) rep.FrameUncertain.Add(n); });

            // ---- MA scene-ref detection (D3) — generic over EVERY component ----------------------------
            foreach (var c in avatarGO.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                ScanSceneRefs(c, avatarGO, rep);
            }

            // ---- Clip-binding classification (reuse CheckAnimator's walk, demotion off) -----------------
            // Dedup per unique (controller, clip, binding path, binding type): one authored curve expands to
            // several component curves (a Transform's position x/y/z, a rotation's quaternion) that share a
            // path+type, and a controller shared across frames is walked once per frame — all the same break.
            // The key omits the frame, so it assumes a binding resolves consistently across the frames a
            // controller is walked under. The one topology that breaks that — the SAME controller mounted both
            // by a descriptor layer (avatar-root, no rewrite) AND a VRCF FullController (rewrite) — is
            // pathological (you would double-mount one controller) and fails safe (an extra CLASSIFY for
            // agent discretion, never a hidden break), so it is left as a known limit rather than paid for
            // with cross-pair aggregation on the hot path.
            var clipSeen = new HashSet<(int ctrl, int clip, string path, Type type)>();
            foreach (var p in pairs)
            {
                foreach (var (clip, b) in CollectUnresolvedBindingsCalled(p.Controller, p.Roots, p.PathRewrite))
                {
                    string clipAssetPath = AssetDatabase.GetAssetPath(clip);
                    if (IsSdkProxyClip(clipAssetPath)) continue; // VRChat SDK humanoid proxy — swapped at runtime, never a scene ref
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
            // VRCF frames ONLY, and the direction is the reason (docs/nondestructive.md §Choosing a
            // framework rule 1): an MA-merged clip through a VRCFury-moved node is repaired by VRCFury's own
            // move service, and a descriptor-layer binding crosses no module seam at all. Enumerating either
            // would manufacture offenders for a break that cannot occur in that direction.
            rep.AnchorsPresent = CollectAnchors(avatarGO);
            rep.UntrackedRelocatorPresent = HasUntrackedRelocator(avatarGO);
            var seamSeen = new HashSet<(int ctrl, int clip, string path, Type type, int anchor)>();
            foreach (var p in pairs)
            {
                if (p.Kind != FrameKind.VRCF) continue;
                foreach (var hit in CollectAnchorSeamBreaks(p.Controller, p.Roots, avatarGO, rep.AnchorsPresent, p.PathRewrite, p.RootBindingsApplyToAvatar))
                {
                    string clipAssetPath = AssetDatabase.GetAssetPath(hit.Clip);
                    if (IsSdkProxyClip(clipAssetPath)) continue;
                    // clip-binding's dedup key plus the anchor: one authored curve expands into several
                    // component curves sharing a path and type, but two anchors on one binding are two repairs.
                    if (!seamSeen.Add((p.Controller.GetInstanceID(), hit.Clip.GetInstanceID(), hit.Binding.path, hit.Binding.type, hit.Anchor.GetInstanceID()))) continue;
                    rep.AnchorSeams.Add(new Offender
                    {
                        Kind = "anchor-seam",
                        Animator = p.Controller.name,
                        Clip = hit.Clip.name,
                        Path = hit.Binding.path,
                        ClipAssetPath = clipAssetPath,
                        Anchor = PathOf(hit.Anchor),
                        AnchorLabel = hit.AnchorLabel,
                        Host = p.Label,
                    });
                }
            }

            ScanMergeConflicts(avatarGO, rep);

            return Emit(rep);
        }

        /// <summary>Prefix marking a line that is NOT a finding but a reason the scan could not be trusted —
        /// a fail-loud reflection drift or an uncertain frame. A caller folding both into one list must treat
        /// a degraded line as at least as serious as a finding, never as noise to filter.</summary>
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
            var pairs = MergeSurfaces.Enumerate(root, null, vrcfOnly: true,
                (c, anchor) => SurfaceUnreflected(c, anchor, rep));

            // Keyed on the anchor too: one controller mounted on two FullControllers with different anchors is
            // two distinct repairs, and a key without it reports only whichever was walked first.
            var seen = new HashSet<(int ctrl, int clip, string path, Type type, int anchor)>();
            foreach (var p in pairs)
            {
                foreach (var hit in CollectAnchorSeamBreaks(p.Controller, p.Roots, root, anchors, p.PathRewrite, p.RootBindingsApplyToAvatar))
                {
                    if (IsSdkProxyClip(AssetDatabase.GetAssetPath(hit.Clip))) continue;
                    if (!seen.Add((p.Controller.GetInstanceID(), hit.Clip.GetInstanceID(), hit.Binding.path, hit.Binding.type, hit.Anchor.GetInstanceID()))) continue;
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}: clip `{1}` binds `{2}`, moved by {3} @ `{4}` [{5}]",
                        p.Controller.name, hit.Clip.name, hit.Binding.path, hit.AnchorLabel, PathOf(hit.Anchor), p.Label));
                }
            }

            foreach (var n in rep.Notes) lines.Add(DegradedPrefix + n);
            // R-K frame-uncertainty notes are raised on the MA branch only, which vrcfOnly skips, so this is
            // empty today. Kept so widening the scope cannot silently drop a caveat it starts producing.
            foreach (var n in rep.FrameUncertain) lines.Add(DegradedPrefix + n);
            if (anchors.Count > 0 || HasUntrackedRelocator(root)) lines.Add(ScopePrefix + AnchorSeamScopeLine);
            return lines;
        }

        // R-H: name the anchor loud and record it in Notes; the caller still processes the controller's
        // bindings (never drops it), so an unreflected frame field can't yield a false PASS.
        private static void SurfaceUnreflected(Component c, string anchor, Report rep)
        {
            string msg = "[CheckAvatar] frame field '" + anchor + "' on " + c.GetType().Name + " @ " + PathOf(c.gameObject)
                       + " did not reflect — surfacing the merged animator anyway (not dropped); its frame is best-effort.";
            Debug.LogWarning(msg);
            rep.Notes.Add(FailLoudNotePrefix + msg.Substring("[CheckAvatar] ".Length));
        }

        // R-K: the frame caveat that rides beside an MA MergeAnimator whose relativePathRoot did not resolve.
        // Two shapes reach the own-GameObject fallback and BOTH get a note, because the generic scan emits an
        // MA-scene-ref offender for both and an offender with no frame line beside it reads as a dropped ref:
        //   - non-empty referencePath that does not resolve ⇒ the frame is a GUESS (frame-uncertain);
        //   - empty referencePath carrying a live targetObject ⇒ the frame is CERTAIN (MA's own fallback), and
        //     what is broken is the ref, which the inspector still shows resolved (nondestructive.md).
        // Returns null only where there is no offender to caption: Absolute, a resolving root, or a wholly
        // empty relativePathRoot (both halves empty — the intentional-empty the scan exempts too).
        private static string MaFrameUncertaintyNote(Component c, GameObject avatarGO, FrameResult frame)
        {
            if (frame.IsAbsolute) return null;
            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return null; } // B6
            var rel = so.FindProperty("relativePathRoot");
            if (rel == null) // B2: field absent (drift) — surface it, don't silently treat as a confident frame
                return "frame-uncertain: the MA relativePathRoot field on '" + PathOf(c.gameObject)
                     + "' did not reflect (API drift) — bindings were resolved against the fallback frame (its own GameObject).";
            var pathChild = rel.FindPropertyRelative("referencePath");
            var targetChild = rel.FindPropertyRelative("targetObject");
            string refPath = pathChild != null ? pathChild.stringValue : "";
            if (string.IsNullOrEmpty(refPath))
            {
                // Both halves empty ⇒ the author wrote no root at all and meant the own-GO fallback: no
                // offender, so no caption. A live targetObject ⇒ the scan DOES emit one, and this is its
                // frame half — the frame is right, the ref is the silent no-op.
                if (targetChild == null || targetChild.objectReferenceValue == null) return null;
                return "frame-certain: bindings for the animator on '" + PathOf(c.gameObject)
                     + "' were resolved against its own GameObject — the MA relativePathRoot carries a targetObject but no"
                     + " referencePath, which is exactly what the build resolves it to. See the matching MA-scene-ref"
                     + " offender: the ref reads resolved in the inspector and is not there at bake, so if that targetObject"
                     + " was meant to be the frame, these bindings are counted against the wrong one.";
            }
            if (TryResolveSceneRef(rel, c, avatarGO, out _)) return null; // resolves ⇒ confident frame
            return "frame-uncertain: bindings for the animator on '" + PathOf(c.gameObject)
                 + "' were resolved against the fallback frame (its own GameObject) because the MA relativePathRoot '"
                 + refPath + "' did not resolve — see the matching MA-scene-ref offender. These bindings are counted, not dropped.";
        }

        // ── MA scene-ref detection (D3) — never throws ────────────────────────────────────────────────

        // Walk serialized properties generically (precedent: RemapReferencesByPath). A property carrying both
        // a referencePath(string) child and a targetObject(objref) child is an AvatarObjectReference. The
        // intentional-empty exemption (MISSING-vs-EMPTY, as CheckPackage's clean-zero) is BOTH halves empty:
        // an empty referencePath carrying a live targetObject is the silent no-op nondestructive.md names —
        // Get(Component) returns null on the empty path whatever targetObject holds, while the inspector's
        // editor-side resolver reads targetObject first, so the ref reads correct in the UI and is not there
        // at bake. Exempting on the path alone hid exactly that class.
        // What "not there" costs is the CONSUMER's business and this walk is generic, so the offender says
        // only that no path was written: the reactive family / BlendshapeSync / Mesh Settings resolve nothing,
        // while a MergeAnimator or MergeBlendTree relativePathRoot silently falls back to the component's own
        // GameObject (MergeAnimatorProcessor / MergeBlendTreePass) — a relocated binding frame rather than a
        // dropped ref, and harmless only where the author meant that fallback. Only MergeAnimator gets the
        // R-K frame-caption (MaFrameUncertaintyNote) alongside that offender — TryMaFrame's type gate
        // (CheckAnimator.cs) excludes MergeBlendTree, so its offender here carries no frame line today; read
        // it as the same relocated-frame class anyway, not as uncaptioned-because-broken. If that gate is
        // ever widened, MaFrameUncertaintyNote's hardcoded so.FindProperty("relativePathRoot") (lowercase)
        // won't find it — ModularAvatarMergeBlendTree's field is RelativePathRoot (PascalCase), and
        // FindProperty is case-sensitive, so it would silently take the field-drift branch on a field that is
        // actually present.
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

                if (TryResolveSceneRef(it.Copy(), c, avatarGO, out _)) continue; // resolved ⇒ not an offender
                rep.SceneRefs.Add(new Offender
                {
                    Kind = "MA-scene-ref",
                    // Parenthesized so the targetObject-only offender can never read as a path the agent
                    // should go looking for: the defect IS that no path was written.
                    Path = string.IsNullOrEmpty(refPath)
                         ? "(unset referencePath; targetObject '" + PathOf(targetGO) + "' only — write the path)"
                         : refPath,
                    Host = c.GetType().Name + " @ " + PathOf(c.gameObject),
                });
            }
        }

        // Resolve an AvatarObjectReference property. Authoritative path: box it (guarded — boxedValue THROWS
        // for unsupported shapes, R-J) and invoke the pinned Get(Component), which returns null on an empty
        // referencePath BEFORE it looks at targetObject and only then lets a live targetObject win, and only
        // one sitting under the avatar root. (The static Get(SerializedProperty) overload the inspector uses
        // has the opposite order and no empty-path guard — it keeps the IsChildOf gate, which is why an
        // out-of-avatar targetObject resolves under NEITHER overload. That split is the silent no-op
        // nondestructive.md names, and mirroring the wrong one here is what made this scan blind to it.)
        // Every reflective hop is guarded: on any failure/drift, warn loud
        // naming the broken anchor and self-resolve from the SerializedProperty CHILDREN in that same order.
        // Never throws.
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

            // ---- Guarded self-resolve from the children already located --------------------------------
            Debug.LogWarning("[CheckAvatar] scene-ref resolve degraded on " + PathOf(host.gameObject)
                           + " (" + reason + ") — self-resolving from serialized children (empty referencePath first, then an in-avatar targetObject, then the path).");

            var targetGO = targetChild != null ? targetChild.objectReferenceValue as GameObject : null;
            if (string.IsNullOrEmpty(refPath)) return false;    // empty path ⇒ null, whatever targetObject holds
            // B3: a live targetObject wins — but only under the avatar root. BOTH MA overloads gate on
            // IsChildOf (Get(Component) against FindAvatarTransformInParents, Get(SerializedProperty) against
            // the same root), so a targetObject pointing outside the avatar resolves to NOTHING at bake.
            // Accepting it here was a false negative on the one path that exists to survive MA API drift.
            if (targetGO != null && targetGO.transform.IsChildOf(avatarGO.transform)) return true;
            if (refPath == MaAvatarRootSentinel) return true;                                       // avatar root itself
            return avatarGO.transform.Find(refPath) != null;
        }

        // ── Clip-binding walk (REUSE — CheckAnimator.CollectUnresolvedBindings, demotion off) ────────────
        // The demotion (BrokenBindingIsError = !buildRewrite) lives in CheckAnimator.Emit, NOT in the walk —
        // CollectUnresolvedBindings returns raw unresolved pairs, so calling it directly IS the "demotion
        // off" behaviour: under D1 every unresolved-in-scene binding is a real, non-advisory clip-binding
        // offender (mapped to CLASSIFY, never FAIL).
        private static IEnumerable<(AnimationClip clip, EditorCurveBinding binding)> CollectUnresolvedBindingsCalled(
            AnimatorController controller, List<GameObject> roots, Func<string, string> pathRewrite)
            => CheckAnimator.CollectUnresolvedBindings(controller, roots, pathRewrite);

        // VRChat SDK proxy animations (Packages/com.vrchat.*/…/ProxyAnim/proxy_*.anim) are humanoid-muscle
        // placeholders the SDK swaps at runtime; their bone-path bindings never resolve to a scene object and
        // are never a real break. Anchored to the SDK PACKAGE, not the folder name alone: a proxy's identity
        // is its SDK location, and this is the one skip that could HIDE a break — so it must not fire on a
        // user asset that merely shares a `ProxyAnim` folder name (fail-loud: when unsure, surface).
        private static bool IsSdkProxyClip(string clipAssetPath)
            => !string.IsNullOrEmpty(clipAssetPath)
               && clipAssetPath.StartsWith("Packages/com.vrchat.", StringComparison.OrdinalIgnoreCase)
               && clipAssetPath.IndexOf("/ProxyAnim/", StringComparison.OrdinalIgnoreCase) >= 0;

        // ── Merge-conflict scan (pure grouping core) ─────────────────────────────────────────────────
        // Merge-conflict scan: two dynamics components (grouped within a category) that resolve to the same
        // POST-MERGE transform, where ≥1 is mergeable-sourced (its raw target name-merges onto a shared bone).
        // CLASSIFY-style; never throws (degrades to a loud Note).
        private static void ScanMergeConflicts(GameObject avatarGO, Report rep)
        {
            try
            {
                var (pairs, note) = ResolveMergePairs(avatarGO);
                if (note != null) rep.Notes.Add("fail-loud: " + note);

                var map = new Dictionary<Transform, Transform>();
                foreach (var (merge, baseT) in pairs)
                {
                    if (merge == null || baseT == null) continue;   // Check()-parity null guard (extracted body lacks it)
                    if (!map.ContainsKey(merge)) map[merge] = baseT; // first-wins (safe for detection)
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
                        Live = IsLive(host, category, avatarGO.transform),
                    });
                }

                foreach (var kv in groups)
                {
                    if (kv.Value.Count < 2) continue;
                    bool anyMergeable = false; foreach (var h in kv.Value) if (h.Mergeable) { anyMergeable = true; break; }
                    if (!anyMergeable) continue;
                    rep.MergeConflicts.Add(new MergeConflict {
                        Category = kv.Key.cat,
                        FinalPath = kv.Key.final != null ? PathOf(kv.Key.final.gameObject) : "—",
                        Hosts = kv.Value,
                    });
                }
                // groups is a Dictionary (non-deterministic iteration) — sort for a byte-stable RunLog, unlike
                // the List-ordered maSceneRef/clipBinding blocks. Host order within a group is already stable.
                // FinalPath is not unique — two distinct final bones can share a hierarchy path (Unity allows
                // duplicate sibling names) — so tiebreak on the first host's path (List.Sort is unstable) to keep
                // the order total and the RunLog byte-stable; every group has ≥2 hosts, so Hosts[0] is safe.
                rep.MergeConflicts.Sort((x, y) =>
                {
                    int c = string.CompareOrdinal(x.Category, y.Category);
                    if (c != 0) return c;
                    c = string.CompareOrdinal(x.FinalPath, y.FinalPath);
                    return c != 0 ? c : string.CompareOrdinal(x.Hosts[0].Path, y.Hosts[0].Path);
                });
            }
            catch (Exception e)
            {
                string msg = "[CheckAvatar] merge-conflict scan degraded (" + e.GetType().Name + ": " + e.Message + ") — no conflicts reported.";
                Debug.LogWarning(msg);
                rep.Notes.Add("fail-loud: " + msg.Substring("[CheckAvatar] ".Length));
            }
        }

        // Follow merge→base transitively (accessory→outfit→base); cycle-guarded. Not in map ⇒ own final.
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
            string result = (maSceneRef > 0 || clipBinding > 0 || anchorSeam > 0 || mergeConflict > 0) ? "CLASSIFY" : "PASS";

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckAvatar] {0}: maSceneRef={1} clipBinding={2} anchorSeam={3} mergeConflict={4} => {5}",
                rep.Root.name, maSceneRef, clipBinding, anchorSeam, mergeConflict, result);

            var sb = new StringBuilder();
            sb.Append("# CheckAvatar: ").Append(rep.Root.name).Append('\n');
            sb.Append("root: `").Append(PathOf(rep.Root)).Append("`  \n\n");
            sb.Append(summary.Substring("[CheckAvatar] ".Length)).Append('\n');

            sb.Append("\n## Counts\n\n");
            sb.Append("- maSceneRef: ").Append(maSceneRef).Append('\n');
            sb.Append("- clipBinding: ").Append(clipBinding).Append('\n');
            sb.Append("- anchorSeam: ").Append(anchorSeam).Append('\n');
            sb.Append("- mergeConflict: ").Append(mergeConflict).Append('\n');

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
                sb.Append("- **anchor-seam** animator=`").Append(o.Animator)
                  .Append("` clip=`").Append(o.Clip)
                  .Append("` path=`").Append(o.Path)
                  .Append("` moved-by=").Append(o.AnchorLabel)
                  .Append(" @ `").Append(o.Anchor)
                  .Append("` clipAssetPath=`").Append(string.IsNullOrEmpty(o.ClipAssetPath) ? "(unsaved)" : o.ClipAssetPath)
                  .Append("` [").Append(o.Host).Append("]\n");

            sb.Append("\n### merge-conflict\n\n");
            if (rep.MergeConflicts.Count == 0) sb.Append("_(none)_\n");
            else foreach (var mc in rep.MergeConflicts)
            {
                sb.Append("- **merge-conflict** category=`").Append(mc.Category)
                  .Append("` final=`").Append(mc.FinalPath).Append("`\n");
                // Both states are spelled, like [mergeable]/[base]: emitting only the negative would make
                // an all-live report byte-identical to one from a build that never evaluated liveness.
                foreach (var h in mc.Hosts)
                    sb.Append("  - ").Append(h.Mergeable ? "[mergeable] " : "[base] ")
                      .Append(h.Live == null ? "" : h.Live == true ? "[live] " : "[not-live] ").Append(h.Path)
                      .Append(" (").Append(h.Type).Append(", bone=`").Append(h.Bone).Append("`")
                      .Append(string.IsNullOrEmpty(h.Detail) ? "" : ", " + h.Detail).Append(")\n");
            }

            sb.Append("\n## Notes\n\n");
            sb.Append("- ").Append(ExcludedEdgeLine).Append('\n');
            sb.Append("- ").Append(DiscretionLimitsLine).Append('\n');
            foreach (var n in rep.FrameUncertain) sb.Append("- ").Append(n).Append('\n');
            foreach (var n in rep.Notes) sb.Append("- ").Append(n).Append('\n');
            if (rep.MergeConflicts.Count > 0)
                sb.Append("- MA prunes exact-duplicate physbones at build (PruneDuplicatePhysBones), so a flagged MA " +
                    "physbone pair may already be resolved — verify against a build. VRCFury has no such pass; colliders, " +
                    "constraints, and non-exact/non-zip-merged MA physbone pairs are the residue this check exists for.\n");
            // Scope before repair: an avatar carrying relocators gets the scope line whether or not anything
            // fired, so a zero count is never read as whole-avatar confirmation (the corpus-silence rule).
            if ((rep.AnchorsPresent != null && rep.AnchorsPresent.Count > 0) || rep.UntrackedRelocatorPresent)
                sb.Append("- ").Append(AnchorSeamScopeLine).Append('\n');
            if (rep.AnchorSeams.Count > 0)
                sb.Append("- ").Append(AnchorSeamNoteLine).Append('\n');
            if (AnyMixedLivePhysboneGroup(rep.MergeConflicts))
                sb.Append("- ").Append(VariantSetNoteLine).Append('\n');
            if (!rep.Root.activeInHierarchy)
                sb.Append("- ").Append(InactiveRootNoteLine).Append('\n');

            var res = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, "avatarlint_" + rep.Root.name, summary, sb.ToString(), ".md");
            if (result == "PASS") Debug.Log(res); else Debug.LogWarning(res);
            return res;
        }

        // ── Bad-input refusal (bare FAIL, no trailer — family discipline) ───────────────────────────────

        private static string Refuse(string why)
        {
            string err = "[CheckAvatar] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        // ── Scene resolver (path → instance id → name; mirrors RenderAvatar.Resolve, kept local) ──────────

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

        private static string PathOf(GameObject go) => MergeSurfaces.PathOf(go);

        // ── Types ───────────────────────────────────────────────────────────────────────────────────

        private struct Offender
        {
            public string Kind;
            public string Path;          // MA-scene-ref: failing referencePath. clip-binding: binding scene path.
            public string Host;          // component/site label
            public string Animator;      // clip-binding + anchor-seam
            public string Clip;          // clip-binding + anchor-seam
            public string ClipAssetPath; // clip-binding + anchor-seam — AssetDatabase.GetAssetPath(clip); DISTINCT from Path (routing, R-E)
            public string Anchor;        // anchor-seam only — scene path of the relocated node (what a repair moves)
            public string AnchorLabel;   // anchor-seam only — the MA relocator's short type name(s)
        }

        private class Report
        {
            public GameObject Root;
            public Dictionary<GameObject, string> AnchorsPresent; // null ⇒ anchor-seam never ran
            public bool UntrackedRelocatorPresent;               // an MA relocator this class does not track
            public readonly List<Offender> SceneRefs = new List<Offender>();
            public readonly List<Offender> ClipBindings = new List<Offender>();
            public readonly List<Offender> AnchorSeams = new List<Offender>();
            public readonly List<MergeConflict> MergeConflicts = new List<MergeConflict>();
            public readonly List<string> FrameUncertain = new List<string>(); // R-K frame caveats (uncertain AND certain-but-captioned)
            public readonly List<string> Notes = new List<string>();          // R-H fail-loud + degrade notes
        }
    }
}
