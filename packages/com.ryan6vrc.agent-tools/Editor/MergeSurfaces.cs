using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using static Ryan6Vrc.AgentTools.Editor.CheckAnimator; // FrameKind / FrameResult / TryMaFrame / TryVrcfFrame

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one enumeration of every animator merged onto a placed avatar: the descriptor's own playable
    /// layers, then every MA MergeAnimator and VRCFury FullController in the subtree, each carrying the
    /// binding-basis root the build will use and the VRCFury <c>rewriteBindings</c> transform that runs
    /// before it. Mounts and rewrites are resolved by invoking the vendors' own code through
    /// <see cref="CheckAnimator"/>'s frame helpers, never by mirroring their rules.
    ///
    /// It lives here rather than inside a door because three doors need the same answer and disagreeing
    /// about it is the failure mode: <see cref="CheckAvatar"/> resolves reference breaks against it,
    /// <see cref="ReportComposition"/> reports provenance from it, and <see cref="CheckAnimator"/>'s
    /// mergeSites count is drawn from it. One walk, so no two of them can describe the same avatar
    /// differently — the same guarantee <c>docs/animator.md</c> already states for the binding walk.
    ///
    /// Notes are pushed OUT through callbacks instead of being formatted here: a fail-loud note names the
    /// door that emitted it, and this type is not a door.
    /// </summary>
    internal static class MergeSurfaces
    {
        /// <summary>One (controller, frame) pair merged onto the avatar.</summary>
        internal struct Surface
        {
            public AnimatorController Controller;
            /// <summary>Binding-basis candidates. One entry for a descriptor layer or an MA frame; for a
            /// VRCFury frame, the mount then each ancestor up to the avatar root, because the build's
            /// nearest-match rewriter accepts a binding that resolves at ANY of them.</summary>
            public List<GameObject> Roots;
            /// <summary>The frame root itself — <c>Roots[0]</c>, named separately so a caller reporting
            /// mount sites does not have to know the ancestor-chain convention.</summary>
            public GameObject Mount;
            /// <summary>The merge component, or null for a descriptor playable layer.</summary>
            public Component Site;
            public FrameKind Kind;
            public string Label;
            public Func<string, string> PathRewrite;
            public bool RootBindingsApplyToAvatar;
            /// <summary>The raw frame as the vendor helpers resolved it. Carried so a door can caption its
            /// own offenders from it — a caveat that cross-references a door's offender list is that door's
            /// prose, and formatting it here would put one door's vocabulary in every other door's mouth.</summary>
            public FrameResult Frame;
        }

        /// <summary>Test seam: forces an unreflected-anchor name onto a real frame, so the fail-loud path
        /// is exercisable without breaking a vendor install.</summary>
        internal static Func<string, string> FrameAnchorOverride = a => a;

        /// <summary>Every (controller, frame) pair merged onto <paramref name="root"/>. Each pair is walked
        /// once — dedup is per (controller, frame root, kind), not global, so a controller shared across
        /// frames is resolved once per frame. <paramref name="descriptor"/> may be null (a bare module prefab
        /// has none, and contributes no descriptor layers). <paramref name="vrcfOnly"/> skips MA frames
        /// outright, for the anchor-seam door: that class is one-directional, so enumerating MA surfaces
        /// there would only manufacture frame notes for a class MA surfaces cannot be in.
        /// <paramref name="onUnreflected"/> receives (component, anchor) whenever a required frame field
        /// fails to reflect — the controller is still returned, never dropped, so drift cannot yield a false
        /// clean read. <paramref name="onMaFrame"/> fires for every MA frame discovered, BEFORE the dedup —
        /// a door captioning its offenders needs one caption per authored component, not per surviving
        /// surface, or two components mounting one controller at one mount silently lose a caveat.</summary>
        internal static List<Surface> Enumerate(
            GameObject root, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            bool vrcfOnly, Action<Component, string> onUnreflected,
            Action<Component, FrameResult> onMaFrame = null)
        {
            var pairs = new List<Surface>();
            var seen = new HashSet<(int ctrl, int root, int kind)>();
            void Add(AnimatorController c, GameObject mount, List<GameObject> roots, Component site,
                     FrameKind kind, string label, Func<string, string> rewrite, bool rootToAvatar,
                     FrameResult frame)
            {
                if (c == null) return;
                int rootId = mount != null ? mount.GetInstanceID() : 0;
                if (!seen.Add((c.GetInstanceID(), rootId, (int)kind))) return;
                pairs.Add(new Surface
                {
                    Controller = c, Roots = roots, Mount = mount, Site = site, Kind = kind,
                    Label = label, PathRewrite = rewrite, RootBindingsApplyToAvatar = rootToAvatar,
                    Frame = frame,
                });
            }

            // (a) Descriptor playable-layer controllers — avatar-root frame, no merge component to read.
            if (descriptor != null && !vrcfOnly)
                CollectDescriptorLayers(descriptor, root, (c, label) => Add(
                    c, root, new List<GameObject> { root }, null, FrameKind.DescriptorLayer, label, null, false,
                    new FrameResult { Root = root, Kind = FrameKind.DescriptorLayer }));

            // (b)/(c) Every MA MergeAnimator + VRCFury FullController in the subtree.
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;

                if (!vrcfOnly && TryMaFrame(c, root, out var maCtrl, out var maFrame))
                {
                    string anchor = FrameAnchorOverride(maFrame.UnreflectedAnchor);
                    if (anchor != null) onUnreflected(c, anchor);
                    onMaFrame?.Invoke(c, maFrame);
                    var mount = maFrame.Root ?? root;
                    // MA declares one frame for the whole controller and carries no path-rewrite rules.
                    Add(maCtrl, mount, new List<GameObject> { mount }, c, FrameKind.MA,
                        "MA MergeAnimator @ " + PathOf(c.gameObject), null, false, maFrame);
                }

                if (TryVrcfFrame(c, out var vrcfCtrls, out var vrcfFrame))
                {
                    string anchor = FrameAnchorOverride(vrcfFrame.UnreflectedAnchor);
                    if (anchor != null) onUnreflected(c, anchor);
                    var mount = vrcfFrame.Root ?? c.gameObject;
                    var roots = AncestorChain(mount, root);
                    // vrcfFrame.PathRewrite is THIS component's rewriteBindings only — applied before the
                    // ancestor walk, mirroring the build's order (it fixes downward relocations the upward
                    // strip cannot reach).
                    foreach (var vc in vrcfCtrls)
                        Add(vc, mount, roots, c, FrameKind.VRCF,
                            "VRCFury FullController @ " + PathOf(c.gameObject),
                            vrcfFrame.PathRewrite, vrcfFrame.RootBindingsApplyToAvatar, vrcfFrame);
                }
            }
            return pairs;
        }

        private static void CollectDescriptorLayers(
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor, GameObject avatarGO,
            Action<AnimatorController, string> add)
        {
            void Walk(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[] layers, string which)
            {
                if (layers == null) return;
                foreach (var layer in layers)
                {
                    // Skip only SDK-default layers (nothing authored). CustomAnimLayer also carries an
                    // `isEnabled` flag, but disabled-layer skipping is deliberately NOT adopted: whether the
                    // SDK build honours isEnabled for base/special layers is unverified, and NOT skipping is
                    // the fail-loud choice — a disabled layer's contents surface for agent discretion rather
                    // than vanishing from every door at once.
                    if (layer.isDefault) continue;
                    var c = layer.animatorController as AnimatorController;
                    if (c == null) continue;
                    add(c, "descriptor " + which + " layer " + layer.type);
                }
            }
            Walk(descriptor.baseAnimationLayers, "base");
            Walk(descriptor.specialAnimationLayers, "special");
        }

        /// <summary>The VRCF upward-strip nearest-match: mount root, then each ancestor up to (and including)
        /// the avatar root. A binding resolving at ANY level is NOT a break — this mirrors VRCFury's build
        /// rewriter rather than predicting where it lands.</summary>
        internal static List<GameObject> AncestorChain(GameObject mount, GameObject avatarGO)
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
            if (roots.Count == 0 || roots[roots.Count - 1] != avatarGO) roots.Add(avatarGO); // avatar root always a candidate
            return roots;
        }

        /// <summary>Full hierarchy path of a scene object, the handle every door in this package speaks.</summary>
        internal static string PathOf(GameObject go)
        {
            if (go == null) return "—";
            var t = go.transform;
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }
    }
}
