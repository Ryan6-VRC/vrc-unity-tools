using System;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one bake spine: clone a placed avatar and run it through the real VRC SDK preprocess chain
    /// (<c>VRCBuildPipelineCallbacks.OnPreprocessAvatar</c>, never NDMF's <c>ManualProcessAvatar</c>), so a
    /// caller can read what actually ships rather than what is authored. <c>docs/nondestructive.md</c>
    /// §The bake door owns why.
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
    ///
    /// <para><b>The post-callback destroys the clone it just made, so read before the scope closes.</b>
    /// Measured on a real composed avatar: after <c>OnPreprocessAvatar</c> all five playable-layer slots hold
    /// controllers (under <c>Packages/nadena.dev.ndmf/__Generated/&lt;clone&gt;/</c>) and every renderer has its
    /// mesh; after <c>OnPostprocessAvatar</c> all five read null and 10 of 34 renderers have a destroyed
    /// <c>sharedMesh</c> — the hooks sweep their own generated assets, and surviving renderers are left
    /// pointing at nothing. That is why the pair is split across <see cref="AvatarBakeScope"/>'s construction
    /// and its <see cref="AvatarBakeScope.Dispose"/> rather than fired back-to-back: a caller that renders or
    /// reads a clone after the post-callback measures a gutted avatar and cannot tell. The scope's
    /// <see cref="AvatarBakeScope.Clone"/> refuses after disposal rather than leaving that to be noticed.</para>
    /// </summary>
    internal static class AvatarBake
    {
        /// <summary>Bake a throwaway clone of <paramref name="source"/> and hand back the scope that owns it.
        /// NEVER returns null and never throws: inspect <see cref="AvatarBakeScope.Ok"/>, and on false read
        /// <see cref="AvatarBakeScope.FailedStage"/> (a hook REFUSED the build) or
        /// <see cref="AvatarBakeScope.Failure"/> (a hook THREW — exactly one of the two is set, so a caller
        /// can report a crash as a crash rather than as a refusal). Dispose the scope on every path,
        /// including the failure path: the preprocess may already have run, and the SDK's hooks keep state
        /// across the pair that the next build would otherwise read.</summary>
        /// <param name="cloneName">the clone's GameObject name; null =&gt; "&lt;source&gt; (composition bake)".</param>
        internal static AvatarBakeScope Begin(GameObject source, string cloneName = null)
        {
            return new AvatarBakeScope(source, cloneName);
        }

        /// <summary>The verbatim cleanup-note token, pinned here because it is spliced into a CALLER's verdict
        /// line and the shape is load-bearing: the leading space separates it from whatever precedes it, and
        /// the tool tag stays the caller's (a note tagged <c>[AvatarBake]</c> mid-verdict would contradict the
        /// line's own <c>[RenderThumbnail]</c> head). Matches the other note contributors exactly.</summary>
        internal static string FormatCleanupNote(Exception e)
        {
            return " note=postprocess-cleanup-threw: " + e.GetType().Name;
        }
    }

    /// <summary>
    /// One bake's lifetime: the baked clone, alive from <see cref="AvatarBake.Begin"/> until
    /// <see cref="Dispose"/> fires the SDK's paired <c>OnPostprocessAvatar</c> and destroys it. Read
    /// <see cref="Clone"/> only inside that window — <see cref="AvatarBake"/>'s doc comment has the
    /// measurement of what the post-callback takes away.
    /// <para>State is per-scope on purpose: a static "a bake is pending" flag would survive a caller's early
    /// return and wedge every later bake in the editor session, in every tool, until a domain reload — the
    /// same trap <c>CompositionBake</c>'s <c>InFlight</c> dictionary needed a staleness escape for.</para>
    /// </summary>
    internal sealed class AvatarBakeScope : IDisposable
    {
        private GameObject _clone;
        private readonly bool _preprocessed;
        private bool _disposed;

        /// <summary>True when the clone baked and is readable until <see cref="Dispose"/>.</summary>
        internal bool Ok { get; private set; }

        /// <summary>Set only when a build hook REFUSED the build (the chain returned false). Null otherwise —
        /// a hook that threw sets <see cref="Failure"/> instead, because "a hook blocked the build" is a false
        /// account of a crash and points the reader at the console for a reason that was never logged.</summary>
        internal string FailedStage { get; private set; }

        /// <summary>Set only when a hook THREW, and kept as the exception rather than flattened to a string so
        /// a caller can surface the real type and stack of a deep NDMF/MA failure.
        /// <para>Not the universal crash channel, and don't read an empty <c>Failure</c> as "nothing crashed":
        /// VRCFury catches its own hook exceptions and reports them as a MODAL dialog, then returns false — so
        /// its crashes arrive down <see cref="FailedStage"/> after wedging an MCP-driven editor until someone
        /// clicks Ok (measured; <c>docs/unity.md</c> §Sharp edges owns the recovery).</para></summary>
        internal Exception Failure { get; private set; }

        /// <summary>Null on a clean teardown; otherwise the pinned note token (see
        /// <see cref="AvatarBake.FormatCleanupNote"/>). Readable AFTER <see cref="Dispose"/> — that is the
        /// point of it living on the scope rather than being returned.</summary>
        internal string CleanupNote { get; private set; }

        /// <summary>The baked avatar. Valid only before <see cref="Dispose"/>.</summary>
        /// <exception cref="InvalidOperationException">after disposal — see the class remarks.</exception>
        internal GameObject Clone
        {
            get
            {
                if (_disposed)
                    throw new InvalidOperationException(
                        "AvatarBakeScope.Clone was read after the scope closed. OnPostprocessAvatar has run, "
                        + "so this clone's generated assets (playable-layer controllers, optimizer-built meshes) "
                        + "are destroyed and what is left reads as nulls. Read the clone inside the scope.");
                return _clone;
            }
        }

        internal AvatarBakeScope(GameObject source, string cloneName)
        {
            if (source == null) { FailedStage = "clone (source was null)"; return; }

            try
            {
                _clone = UnityEngine.Object.Instantiate(source);
                // Instantiate places the clone in the SOURCE's scene, and it stays there until a caller moves
                // it. RenderThumbnail's orphan sweep relies on exactly that to catch a clone stranded by a
                // throw before it reaches the preview scene, so this is contract, not incidental.
                _clone.name = cloneName ?? source.name + " (composition bake)";
                _clone.SetActive(true); // an inactive avatar is not a valid preprocess target

                // Owed the instant the chain is ENTERED, not once it returns: a hook that throws midway has
                // already moved SDK state that only the post-callback puts back.
                _preprocessed = true;
                if (VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPreprocessAvatar(_clone))
                {
                    Ok = true;   // OnPreprocessAvatar mutates in place, so the clone IS the baked avatar
                    return;
                }
                FailedStage = "OnPreprocessAvatar returned false (a build hook blocked the build — read the console for which)";
            }
            catch (Exception e)
            {
                Failure = e;
            }

            // Failure path only: nothing to read, so the clone goes now. Dispose still owes the post-callback.
            if (_clone != null) UnityEngine.Object.DestroyImmediate(_clone);
            _clone = null;
        }

        /// <summary>Destroy the clone, then fire the SDK's paired <c>OnPostprocessAvatar</c>. Idempotent, and
        /// <b>never throws</b>: it runs inside callers' teardown <c>finally</c> blocks, where a throw would
        /// replace the real pipeline exception with a cleanup one. A failed post-callback lands in
        /// <see cref="CleanupNote"/> instead — surfaced, never swallowed, never fatal.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Destroy before the callback, matching the order the render path already runs (its preview scene
            // closes — destroying the clone — before the pairing fires), so both callers share one order.
            // Guarded rather than assumed: a caller may already have destroyed it with the scene it lived in.
            if (_clone != null) UnityEngine.Object.DestroyImmediate(_clone);
            _clone = null;

            if (!_preprocessed) return;
            // The pair is a contract: OnPreprocessAvatar was entered, so OnPostprocessAvatar must fire. It
            // runs every hook's OWN cleanup (NDMF's temporary-asset sweep among them) rather than this tool
            // guessing folder names it does not control. Assets that outlive it are not chased.
            try { VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPostprocessAvatar(); }
            catch (Exception e) { CleanupNote = AvatarBake.FormatCleanupNote(e); }
        }
    }
}
