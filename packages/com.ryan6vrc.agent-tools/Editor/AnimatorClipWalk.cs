using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The single motion-slot traversal the clip tools share: every <see cref="AnimationClip"/> an
    /// <see cref="AnimatorController"/> references across states, nested blend trees and synced-layer
    /// override motions. Deduped, insertion-order-stable.
    ///
    /// Deliberately does NOT read <c>controller.animationClips</c>: tools deriving their clip sets from one
    /// traversal stay consistent with each other, and a clip Unity's aggregate might omit cannot be silently
    /// skipped.
    /// </summary>
    public static class AnimatorClipWalk
    {
        public static List<AnimationClip> CollectClips(AnimatorController controller)
        {
            var ordered = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
            if (controller == null) return ordered;

            System.Action<AnimationClip> add = (c) => { if (c != null && seen.Add(c)) ordered.Add(c); };
            System.Action<BlendTree> walkBt = null;
            walkBt = (bt) =>
            {
                if (bt == null) return;
                foreach (var ch in bt.children)
                {
                    var clip = ch.motion as AnimationClip;
                    if (clip != null) add(clip);
                    else walkBt(ch.motion as BlendTree);
                }
            };
            System.Action<Motion> walkMotion = (m) =>
            {
                var clip = m as AnimationClip;
                if (clip != null) add(clip);
                else walkBt(m as BlendTree);
            };
            System.Action<AnimatorStateMachine> walkSm = null;
            walkSm = (sm) =>
            {
                if (sm == null) return;
                foreach (var cs in sm.states) { if (cs.state == null) continue; walkMotion(cs.state.motion); }
                foreach (var css in sm.stateMachines) walkSm(css.stateMachine);
            };

            var layers = controller.layers;
            foreach (var layer in layers)
            {
                if (layer.syncedLayerIndex >= 0)
                {
                    var srcSm = layers[layer.syncedLayerIndex].stateMachine;
                    System.Action<AnimatorStateMachine> walkSync = null;
                    walkSync = (sm) =>
                    {
                        if (sm == null) return;
                        foreach (var cs in sm.states) { if (cs.state == null) continue; walkMotion(layer.GetOverrideMotion(cs.state)); }
                        foreach (var css in sm.stateMachines) walkSync(css.stateMachine);
                    };
                    walkSync(srcSm);
                }
                else
                {
                    walkSm(layer.stateMachine);
                }
            }
            return ordered;
        }
    }
}
