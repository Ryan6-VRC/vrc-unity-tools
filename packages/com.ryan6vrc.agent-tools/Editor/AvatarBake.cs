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

        /// <summary>The verbatim cleanup-note tokens, pinned here because they are spliced into a CALLER's
        /// verdict line and the shape is load-bearing: the leading space separates each from whatever precedes
        /// it, and the tool tag stays the caller's (a note tagged <c>[AvatarBake]</c> mid-verdict would
        /// contradict the line's own <c>[RenderThumbnail]</c> head). Matches the other note contributors
        /// exactly. Both are appended, so a teardown that fails twice reports twice.</summary>
        internal static string FormatCleanupNote(Exception e)
        {
            return " note=postprocess-cleanup-threw: " + e.GetType().Name;
        }

        internal static string FormatDestroyNote(Exception e)
        {
            return " note=bake-clone-destroy-threw: " + e.GetType().Name;
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
        private readonly Func<GameObject, bool> _preprocess;
        private readonly Action _postprocess;
        private GameObject _clone;
        private readonly bool _preprocessed;
        private bool _disposed;

        /// <summary>True when the clone baked and is readable until <see cref="Dispose"/>. DERIVED, not a
        /// third field: a settable flag is one more thing that can disagree with the two failure channels,
        /// and this way the type cannot represent "Ok with a failure set".</summary>
        internal bool Ok { get { return FailedStage == null && Failure == null; } }

        /// <summary>Whether the SDK chain was actually ENTERED. False means the failure happened in setup
        /// (the clone, the rename, the activate) and no callback ran — so a caller naming
        /// <c>OnPreprocessAvatar</c> would be describing a call that never happened.</summary>
        internal bool EnteredChain { get { return _preprocessed; } }

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
            : this(source, cloneName, null, null) { }

        /// <summary>Seam constructor: the SDK callbacks are injectable so the lifetime rules above can be
        /// tested without a composed avatar and a live hook chain. Passing null for either takes the real
        /// callback, which is what every production caller gets through <see cref="AvatarBake.Begin"/>.
        /// The seam exists because the properties worth testing here — the pairing fires exactly once,
        /// fires on the FAILURE path too, and survives a throwing callback — are unreachable otherwise.</summary>
        internal AvatarBakeScope(GameObject source, string cloneName,
                                 Func<GameObject, bool> preprocess, Action postprocess)
        {
            _preprocess = preprocess ?? (go =>
                VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPreprocessAvatar(go));
            _postprocess = postprocess ?? (() =>
                VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPostprocessAvatar());

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
                if (_preprocess(_clone)) return;   // mutates in place, so the clone IS the baked avatar
                FailedStage = "OnPreprocessAvatar returned false (a build hook blocked the build — read the console for which)";
            }
            catch (Exception e)
            {
                Failure = e;
            }

            // Failure path only: nothing to read, so the clone goes now. Dispose still owes the post-callback,
            // and this destroy is guarded because a throw here would propagate OUT OF THE CONSTRUCTOR — the
            // caller's variable would never be assigned, so nothing would be left holding the owed pairing.
            DestroyClone();
        }

        /// <summary>Destroy the clone, then fire the SDK's paired <c>OnPostprocessAvatar</c>. Idempotent, and
        /// <b>never throws</b>: it runs inside callers' teardown <c>finally</c> blocks, where a throw would
        /// replace the real pipeline exception with a cleanup one. A failed teardown lands in
        /// <see cref="CleanupNote"/> instead — surfaced, never swallowed, never fatal.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Destroy before the callback, matching the order the render path already runs (its preview scene
            // closes — destroying the clone — before the pairing fires), so both callers share one order.
            // In a try/finally, not in sequence: a failed destroy must not cost the pairing, and _disposed is
            // already set, so a skipped post-callback here could never be recovered by a retry.
            try { DestroyClone(); }
            finally
            {
                if (_preprocessed)
                {
                    // The pair is a contract: OnPreprocessAvatar was entered, so OnPostprocessAvatar must
                    // fire. It runs every hook's OWN cleanup (NDMF's temporary-asset sweep among them) rather
                    // than this tool guessing folder names it does not control. Assets that outlive it are
                    // not chased.
                    try { _postprocess(); }
                    catch (Exception e)
                    {
                        CleanupNote += AvatarBake.FormatCleanupNote(e);
                        // Logged as well as noted: CompositionBake consumes the scope with `using` and writes
                        // its artifact inside it, so the note alone would reach nobody there — and stale SDK
                        // hook state is read by the NEXT build, in whatever tool runs it.
                        Debug.LogWarning("[AvatarBake] OnPostprocessAvatar threw " + e.GetType().Name
                            + " — subsequent builds may read stale hook state.");
                    }
                }
            }
        }

        /// <summary>Destroy the clone at most once, reporting rather than raising. Guarded, not assumed: a
        /// caller may already have destroyed it with the scene it lived in, and <c>DestroyImmediate</c> can
        /// raise on an already-destroyed object.</summary>
        private void DestroyClone()
        {
            if (_clone == null) { _clone = null; return; }
            try { UnityEngine.Object.DestroyImmediate(_clone); }
            catch (Exception e)
            {
                CleanupNote += AvatarBake.FormatDestroyNote(e);
                Debug.LogWarning("[AvatarBake] destroying the bake clone threw " + e.GetType().Name
                    + " — a clone may be left in the scene.");
            }
            _clone = null;
        }

        /// <summary>One accurate sentence for whichever failure channel is set, or null when there was none.
        /// Callers use this instead of composing their own: naming <c>OnPreprocessAvatar</c> for a failure
        /// that happened before the chain was entered describes a call that never ran, and hard-coding a
        /// refusal string discards <see cref="FailedStage"/> — which is the only thing that says WHICH
        /// refusal it was.</summary>
        internal string DescribeFailure()
        {
            if (Failure != null)
                return (_preprocessed ? "OnPreprocessAvatar threw " : "bake clone setup threw ")
                     + Failure.GetType().Name + ": " + Failure.Message;
            return FailedStage;
        }
    }
}
