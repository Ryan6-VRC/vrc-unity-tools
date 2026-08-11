using System;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one bake spine: clone a placed avatar and run it through the real VRC SDK preprocess chain, so a
    /// caller can read what actually ships rather than what is authored.
    ///
    /// The door is <c>VRCBuildPipelineCallbacks.OnPreprocessAvatar</c> and never NDMF's
    /// <c>AvatarProcessor.ManualProcessAvatar</c>: manual processing walks NDMF's plugin chain only, so
    /// Modular Avatar survives while VRCFury — which registers as an SDK preprocess callback and ships no
    /// NDMF plugin — never runs at all, producing a plausible baked avatar that is not the one that
    /// uploads, with no error to say so. <c>docs/nondestructive.md</c> §The bake door owns this.
    ///
    /// It lives in this package because the dependency arrow only runs one way — <c>avatar-tools</c>
    /// references <c>agent-tools</c>, never the reverse — so a bake spine parked in <c>avatar-tools</c> is
    /// unreachable from any door here. Two spines would be unmanaged duplication of the most consequential
    /// call in the repo, so there is one, and it is this.
    ///
    /// The preprocess chain mutates its argument IN PLACE and returns false when a hook blocks the build.
    /// A blocked build is a loud refusal naming the stage, never a caller's silent fallback to authored
    /// state: reporting authored data under a heading that promises composed truth is the exact failure
    /// this whole surface exists to prevent. Hooks may also raise modal dialogs (VRCFury prompts on a broken
    /// Write Defaults mix), which wedges an MCP-driven editor — <c>docs/unity.md</c> §Sharp edges owns the
    /// recovery, and the prompt is never suppressed because it reports a real defect.
    /// </summary>
    internal static class AvatarBake
    {
        /// <summary>Bake a throwaway clone of <paramref name="source"/>. On true, <paramref name="clone"/> is
        /// the baked avatar and the CALLER owns destroying it. On false, <paramref name="failedStage"/> names
        /// what failed and any partial clone has already been destroyed — there is nothing to read.</summary>
        internal static bool Try(GameObject source, out GameObject clone, out string failedStage)
        {
            clone = null;
            failedStage = null;
            if (source == null) { failedStage = "clone (source was null)"; return false; }

            GameObject mine = null;
            bool preprocessed = false;
            try
            {
                mine = UnityEngine.Object.Instantiate(source);
                mine.name = source.name + " (composition bake)";
                mine.SetActive(true);

                // OnPreprocessAvatar mutates in place, so `mine` IS the baked avatar on success.
                preprocessed = true;
                if (!VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPreprocessAvatar(mine))
                {
                    failedStage = "OnPreprocessAvatar returned false (a build hook blocked the build — read the console for which)";
                    return false;
                }
                clone = mine;
                mine = null; // ownership transferred to the caller
                return true;
            }
            catch (Exception e)
            {
                failedStage = "OnPreprocessAvatar threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                // Pair the post-callback with the pre-callback unconditionally: the SDK's hooks keep state
                // across the pair, and skipping it on the failure path leaves the next build reading it.
                if (preprocessed)
                {
                    try { VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPostprocessAvatar(); }
                    catch (Exception e) { Debug.LogWarning("[AvatarBake] OnPostprocessAvatar threw " + e.GetType().Name + " — subsequent builds may read stale hook state."); }
                }
                if (mine != null) UnityEngine.Object.DestroyImmediate(mine); // only on the failure path
            }
        }
    }
}
