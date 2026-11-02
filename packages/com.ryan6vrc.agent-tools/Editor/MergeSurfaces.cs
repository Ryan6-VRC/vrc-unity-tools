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
    /// <see cref="CheckAvatar"/>, <see cref="ReportComposition"/> and <see cref="CheckAnimator"/>'s mergeSites
    /// count all read this one walk, so a change here moves all three.
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
            /// <summary>The frame root itself: <c>Roots[0]</c>.</summary>
            public GameObject Mount;
            /// <summary>The merge component, or null for a descriptor playable layer.</summary>
            public Component Site;
            public FrameKind Kind;
            public string Label;
            public Func<string, string> PathRewrite;
            public bool RootBindingsApplyToAvatar;
            /// <summary>The raw frame as the vendor helpers resolved it.</summary>
            public FrameResult Frame;
        }

        /// <summary>Test seam: forces an unreflected-anchor name onto a real frame, so the fail-loud path
        /// is exercisable without breaking a vendor install.</summary>
        internal static Func<string, string> FrameAnchorOverride = a => a;

        /// <summary>Every (controller, frame) pair merged onto <paramref name="root"/>. Dedup is per
        /// (controller, frame root, kind), so a controller shared across frames appears once per frame.
        /// <paramref name="descriptor"/> may be null: a bare module prefab has none.
        /// <paramref name="onUnreflected"/> receives (component, anchor) whenever a required frame field
        /// fails to reflect; the controller is still returned, so drift cannot yield a false clean read.
        /// <paramref name="onMaFrame"/> fires for every MA frame BEFORE the dedup, so a door captioning its
        /// offenders gets one caption per authored component even when two mount one controller at one mount.
        /// <paramref name="onUnlintable"/> receives (site, description) for a surface that really mounts a
        /// controller these doors cannot walk, such as an <c>AnimatorOverrideController</c>. That surface is
        /// not in the returned list, so a caller reporting coverage must count it as a gap.</summary>
        internal static List<Surface> Enumerate(
            GameObject root, VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            Action<Component, string> onUnreflected,
            Action<Component, FrameResult> onMaFrame = null,
            Action<Component, string> onUnlintable = null)
        {
            // Not returned as a Surface: Surface.Controller is typed AnimatorController and every consumer
            // runs rules on it unconditionally, so admitting one would be a silent false inclusion.
            void Unlintable(Component site, List<string> descriptions)
            {
                if (descriptions == null || onUnlintable == null) return;
                foreach (var d in descriptions) onUnlintable(site, d);
            }
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

            if (descriptor != null)
                CollectDescriptorLayers(descriptor, root, (c, label) => Add(
                    c, root, new List<GameObject> { root }, null, FrameKind.DescriptorLayer, label, null, false,
                    new FrameResult { Root = root, Kind = FrameKind.DescriptorLayer }),
                    (label, what) => onUnlintable?.Invoke(descriptor, label + ": " + what));

            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;

                if (TryMaFrame(c, root, out var maCtrl, out var maFrame))
                {
                    string anchor = FrameAnchorOverride(maFrame.UnreflectedAnchor);
                    if (anchor != null) onUnreflected(c, anchor);
                    Unlintable(c, maFrame.Unlintable);
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
                    Unlintable(c, vrcfFrame.Unlintable);
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
            Action<AnimatorController, string> add, Action<string, string> unlintable)
        {
            void Walk(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer[] layers, string which)
            {
                if (layers == null) return;
                foreach (var layer in layers)
                {
                    // Only isDefault (nothing authored) skips. CustomAnimLayer.isEnabled is deliberately not
                    // consulted: whether the SDK build honours it for base/special layers is unverified, and a
                    // disabled layer surfacing beats it vanishing from every door at once.
                    if (layer.isDefault) continue;
                    var c = layer.animatorController as AnimatorController;
                    if (c == null)
                    {
                        var what = CheckAnimator.DescribeUnlintableController(layer.animatorController);
                        if (what != null) unlintable?.Invoke("descriptor " + which + " layer " + layer.type, what);
                        continue;
                    }
                    add(c, "descriptor " + which + " layer " + layer.type);
                }
            }
            Walk(descriptor.baseAnimationLayers, "base");
            Walk(descriptor.specialAnimationLayers, "special");
        }

        /// <summary>The levels of VRCFury's upward-strip nearest-match, as <see cref="Surface.Roots"/> carries
        /// them: mount root, then each ancestor up to and including the avatar root.</summary>
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
            if (roots.Count == 0 || roots[roots.Count - 1] != avatarGO) roots.Add(avatarGO);
            return roots;
        }

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
