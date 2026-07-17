using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Isolated Scene-View render of ONE avatar subtree — the visual backstop for the spatial-judgment
    /// steps compose-mergeable defers to the operator (mesh de-conflict, clipping/fit).
    ///
    /// The gap this closes is <b>isolation</b>, not screenshots: MCP <c>manage_camera screenshot</c>
    /// already auto-frames + orbits, but a framed grab still renders every avatar in the scene.
    /// RenderAvatar drives the operator's Scene View to show the target subtree ALONE, headlight-lit on a
    /// uniform gray background, from named world-axis angles, into one contact-sheet PNG the agent reads.
    ///
    /// <b>Framing is measured, not estimated.</b> It frames off the <i>drawn silhouette</i> — the actual
    /// rendered pixels on the uniform background — via one exact orthographic correction. So a wrong
    /// posed-mesh estimate and shader-shrunk props (a mesh whose verts span metres but renders at ~30cm)
    /// both stop mattering: what is measured is what is drawn.
    ///
    /// <b>Rung — honest scope.</b> The Scene View renders NDMF preview proxies, so the grab shows
    /// reactive-resolved fit (MA Blendshape Sync / Shape Changer applied) — a faithful fit look, still
    /// the editor preview, NOT the baked upload clone. The NDMF test build + the operator's eye remain
    /// the bar. Headlight lighting is truthful for geometry/silhouette/clipping/fit; matcap / rim /
    /// fresnel / GrabPass effects are not the point — judge shading in the operator's scene view.
    ///
    /// <b>Isolation is proxy-aware.</b> NDMF parks its preview proxies in the hidden
    /// <c>___NDMF Preview___</c> scene; blanket-hiding "every other root" would hide them while the
    /// preview hook still suppresses their originals — deleting every reactive-targeted renderer (the
    /// body, typically) from the grab. So the preview scene is exempt from root isolation; instead each
    /// proxy-renderer GameObject (the key NDMF's proxy map uses — SMR proxies are scene roots, MeshRenderer
    /// proxies sit under shadow-bone trees) is attributed to its original via the public
    /// <c>NDMFPreview.GetOriginalObjectForProxy</c> and self-only hidden unless that original is drawable
    /// under the target (foreign avatars' proxies, stale or unattributable proxies, and proxies of
    /// hide-listed / eye-hidden / inactive originals all hide).
    /// The summary reports <c>proxies=kept:N,hidden:M</c> whenever proxies exist; if the attribution
    /// API drifts, proxies are left visible and the note says so — a visible foreign-geometry leak the
    /// reader can see, never a silent body-drop it can't.
    ///
    /// <b>Freshness — settle-gated, horizon-swept.</b> One-sentence contract: <b>target any subtree; the
    /// freshness gate arms at its avatar root</b> (NDMF's own outermost-root resolver — a leaf-mesh target
    /// still reaches its whole avatar, a non-avatar prop arms nothing); <b>OK means rendered-current</b>
    /// (<c>gate=armed</c>) <b>or nothing-to-certify</b> (<c>gate=exempt</c> — no avatar root, where MA
    /// proxies can't exist, or previews globally disabled so originals render un-suppressed); <b>FAIL says
    /// what to do next</b> — re-grab an unsettled preview, focus a backgrounded editor, or re-pin a drifted
    /// NDMF handle. Mechanism: the preview rebuilds asynchronously, advanced only by editor ticks that fire
    /// after a synchronous call returns — so a same-call edit+grab would capture the pre-edit proxy, and a
    /// background-throttled editor stops ticking and wedges the rebuild indefinitely. Rather than return an
    /// untrustworthy sheet, an unsettled pipeline on an armed target FAILS the grab — after kicking the
    /// editor's main window to the OS foreground (a synthetic Alt tap first releases the Windows foreground
    /// lock) so real frames fire between this call and the re-grab. The settle predicate alone has one blind
    /// spot: a scripted edit (reflection write + SetDirty) publishes only a ChangeScene event, which NDMF's
    /// ChangeStream deliberately ignores, and the PropertyMonitor fallback that would catch it parks while
    /// the editor is unfocused — the pipeline then reads settled while its MA proxies render pre-edit
    /// geometry, for seconds to indefinitely. The gate closes it by running one PropertyMonitor sweep itself
    /// before probing (see SweepNdmfChangeHorizon), so a pending edit becomes a real invalidation and FAILs
    /// the grab loudly. Protocol stays <b>edit and grab in separate calls</b>; on the settle FAIL, just
    /// re-grab. In-call waiting for the REBUILD remains impossible by construction — no editor tick runs
    /// while the synchronous call blocks the main thread. An editor whose foregrounding Windows keeps
    /// refusing stays FAILed by design — the message names the operator action (focus the editor), and the
    /// tool never trades that cliff for a sheet it can't vouch for. Mechanism →
    /// docs/superpowers/surveys/2026-07-07-ndmf-preview-refresh.md.
    ///
    /// <b>Angles are world axes, not the avatar's.</b> No root-finding: assumes the VRChat convention
    /// (target upright, facing world +Z, unrotated). A target rotated in the scene shows the scene's
    /// front — a documented limitation; the upside is it also works on a child or a non-avatar object.
    ///
    /// INSPECTION-class: mutates only transient Scene-View state (view transform, display toggles,
    /// selection, and SceneVisibilityManager hides) and restores it in a finally, leaving the scene
    /// un-dirtied. Visibility restore is COARSE: each subtree this grab hid returns to its own self-state,
    /// so a nested eye-hide the operator set *under* a subtree the grab hid is not preserved (the same
    /// bound whole-scene isolation has always carried). The target subtree's own eye-hides are never
    /// touched — they render exactly as the operator left them. To see a hidden part, un-hide it before
    /// the grab and restore it after; the read-only tool never reveals what the operator hid. Writes the
    /// PNG to Application.temporaryCachePath (outside Assets/).
    /// </summary>
    [AgentTool]
    public static class RenderAvatar
    {
        private static readonly string[] Vocabulary = { "front", "back", "left", "right", "top", "bottom" };

        private const int SheetEdgeCap = 2048; // context-budget ceiling on the composed sheet edge
        private const int MinResolution = 64;
        private const int MaxResolution = 2048;
        private const int MinTileRes = 128;    // floor when the sheet cap forces a downscale
        private const int Inset = 10;          // px border inset — rejects the pane's window frame
        private const int OverlayPad = 12;     // px cleared below the detected floating-overlay band
        private const int FgThreshold = 35;    // per-pixel sum |channel-bg| above which a pixel is foreground

        private const int ManifestSchema = 1;  // <png>.cam.json schema; bumped on any field rename (diff FAILs on mismatch)
        private static readonly string ToolVersion = typeof(RenderAvatar).Assembly.GetName().Version.ToString();

        // Descendants excluded from every grab, merged with the caller's `hide`. The non-destructive
        // build adds a ~10 km "Culling" mesh (visible in play mode) that renders as a huge pale surface
        // and would otherwise become the framed silhouette. Present only in built/play state; a no-op
        // in edit mode, where no such object exists.
        private static readonly string[] DefaultHide = { "Culling" };

        // Internal selection-outline/wire annotations — a lingering selection draws a wireframe into the
        // grab that a synchronous RepaintImmediately does NOT clear (the outline caches across the tick).
        // Suppressed during capture (read at render time, so the suppression is synchronous), restored after.
        private static readonly Type AnnType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AnnotationUtility");
        private static readonly PropertyInfo PiOutline = AnnType?.GetProperty("showSelectionOutline",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo PiWire = AnnType?.GetProperty("showSelectionWire",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        // ----- NDMF preview settle-state (reflection) -------------------------------------------
        // The asmdef has references:[], so NDMF internal types are found by assembly-scan on the qualified
        // name (never typeof). Every hop is null-conditional: a renamed/removed member leaves the field null
        // → the drift note at call time, never a class-load throw. Settled predicate + mechanism:
        // docs/superpowers/surveys/2026-07-07-ndmf-preview-refresh.md.
        private const string UnsettledNote = " | note=preview not settled (NDMF rebuild in flight) — re-grab in a separate call";
        private const string DriftNote = " | note=settle-state unknown (NDMF internals drifted)";

        // Resolution routes through SafeGetProperty/SafeGetField (below) so a member lookup that THROWS
        // (e.g. AmbiguousMatchException) at class load yields a null handle → the drift note at call time,
        // never a TypeInitializationException from a field initializer. `?.` on the Types short-circuits a
        // null declaring type before the call is even made.
        private static readonly Type PreviewSessionType = ResolveNdmfType("nadena.dev.ndmf.preview.PreviewSession");
        private static readonly PropertyInfo PiCurrent = SafeGetProperty(PreviewSessionType, "Current",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo FiProxySession = SafeGetField(PreviewSessionType, "_proxySession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type ProxySessionType = FiProxySession?.FieldType;
        private static readonly FieldInfo FiActive = SafeGetField(ProxySessionType, "_active",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo FiNext = SafeGetField(ProxySessionType, "_next",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type ProxyPipelineType = FiActive?.FieldType;
        private static readonly PropertyInfo PiIsReady = SafeGetProperty(ProxyPipelineType, "IsReady",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo PiIsInvalidated = SafeGetProperty(ProxyPipelineType, "IsInvalidated",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // ----- NDMF change-horizon sweep (reflection, same drift rules) ---------------------------
        // The settle predicate reads PIPELINE state, but a scripted edit (reflection write + SetDirty —
        // the normal execute_code mutation path) publishes only a ChangeScene event, which NDMF's
        // ChangeStreamMonitor deliberately ignores ("too many spurious sources"); the fallback that is
        // supposed to catch such unreported changes — PropertyMonitor's poll loop — PARKS while the
        // editor is unfocused (the norm in agent sessions). Until one of them runs, the pipeline reads
        // settled while its proxies render pre-edit geometry: the gate's one blind spot, seen live as
        // => OK on a stale sheet. Before probing, Capture therefore runs one PropertyMonitor sweep
        // itself (NDMF's own unreported-change detector) and pumps the NDMF sync context so a pending
        // edit lands as a real invalidation the probe can see. Drift on any handle skips the sweep and
        // appends a note — the gate still runs exactly as before, its blind spot documented in-band.
        private const string HorizonDriftNote =
            " | note=change-horizon sweep unavailable (NDMF internals drifted) — a recent scripted edit may not be visible to the settle gate";
        private static readonly Type ObjectWatcherType = ResolveNdmfType("nadena.dev.ndmf.cs.ObjectWatcher");
        private static readonly PropertyInfo PiOwInstance = SafeGetProperty(ObjectWatcherType, "Instance",
            BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo FiPropertyMonitor = SafeGetField(ObjectWatcherType, "PropertyMonitor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo MiCheckAllObjects = SafeGetMethodNoArgs(FiPropertyMonitor?.FieldType,
            "CheckAllObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Type SyncContextType = ResolveNdmfType("nadena.dev.ndmf.preview.NDMFSyncContext");
        private static readonly MethodInfo MiSyncScope = SafeGetMethodNoArgs(SyncContextType, "Scope",
            BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo FiInternalContext = SafeGetField(SyncContextType, "InternalContext",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo MiTurn = SafeGetMethodNoArgs(FiInternalContext?.FieldType, "Turn",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Type ComputeContextType = ResolveNdmfType("nadena.dev.ndmf.preview.ComputeContext");
        // FlushInvalidates is overloaded — the Type.EmptyTypes lookup picks the parameterless one
        // (a bare GetMethod would throw AmbiguousMatchException and null the handle).
        private static readonly MethodInfo MiFlushInvalidates = SafeGetMethodNoArgs(ComputeContextType,
            "FlushInvalidates", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        // ----- NDMF preview-scene proxy attribution (reflection, same drift rules) ---------------
        // Both hooks are PUBLIC NDMF API (NDMFPreviewSceneManager.IsPreviewScene, changelog-tracked
        // NDMFPreview.GetOriginalObjectForProxy) — reflected only because this asmdef has references:[].
        // A drift leaves the handles null → proxies stay visible + an in-band note (see class doc).
        private const string ProxyDriftNote =
            " | note=NDMF proxy attribution drifted — preview proxies left visible (possible foreign-geometry leak)";
        // ProxyDriftNote never reaches a summary anymore: CaptureCore converts it to this error-level FAIL
        // (a sheet with unattributed proxies can't be vouched for). internal for the headless drift test.
        internal const string ProxyDriftFailReason =
            "NDMF proxy attribution drifted — preview proxies are present but attribution is unavailable, so the "
            + "grab would render unattributed preview proxies as unflagged geometry (possible foreign-geometry "
            + "leak); re-pin GetOriginalObjectForProxy before trusting a sheet";
        // Proxies discovered + session settled + none attribute = the attribution API silently returned null
        // for every proxy; the grab would hide all reactive-targeted geometry and draw a bodiless sheet.
        // internal for the headless truth-table test.
        internal const string ProxyAllNullFailReason =
            "NDMF preview proxies are present and the session is settled, but attribution returned null for "
            + "every one — the grab would hide all reactive-targeted proxies and draw a bodiless sheet; "
            + "re-pin GetOriginalObjectForProxy before trusting a sheet";
        // NDMF installed but its avatar-root resolver drifted (handle missing OR return type changed): every
        // target would classify as no-avatar-root → render ungated → silent OK-stale, and the return-type
        // mode also slips past a bare-null canary. FAIL loud, symmetric with the proxy-attribution drift FAIL.
        internal const string ArmScopeResolverDriftFailReason =
            "NDMF is installed but its avatar-root resolver (RuntimeUtil.FindAvatarInParents) drifted — the "
            + "handle is missing or its return type changed, so every target would resolve to no-avatar-root "
            + "and render ungated (a silent OK-stale). Re-pin the resolver handle before trusting a sheet";
        private static readonly Type PreviewSceneManagerType =
            ResolveNdmfType("nadena.dev.ndmf.preview.NDMFPreviewSceneManager");
        private static readonly MethodInfo MiIsPreviewScene = SafeGetMethod(PreviewSceneManagerType, "IsPreviewScene",
            BindingFlags.Static | BindingFlags.Public);
        private static readonly Type NdmfPreviewType = ResolveNdmfType("nadena.dev.ndmf.preview.NDMFPreview");
        private static readonly MethodInfo MiGetOriginalForProxy = SafeGetMethod(NdmfPreviewType, "GetOriginalObjectForProxy",
            BindingFlags.Static | BindingFlags.Public);

        // ----- NDMF avatar-root resolver (reflection, same drift rules) ---------------------------
        // RuntimeUtil.FindAvatarInParents(Transform) → the OUTERMOST avatar root at/above the target,
        // using NDMF's own AllRootTypes (VRCAvatarDescriptor is registered by VRChatPlatform via
        // PlatformRegistry's InitializeOnLoad). Reflected only because this asmdef has references:[].
        private static readonly Type RuntimeUtilType = ResolveNdmfType("nadena.dev.ndmf.runtime.RuntimeUtil");
        private static readonly MethodInfo MiFindAvatarInParents = SafeGetMethod(RuntimeUtilType,
            "FindAvatarInParents", BindingFlags.Static | BindingFlags.Public);

        /// <summary>
        /// Render the GameObject subtree at <paramref name="target"/> in isolation from
        /// <paramref name="angles"/>, silhouette-frame each, and write a single contact-sheet PNG to
        /// Application.temporaryCachePath; return a one-line summary whose in-band <c>png=</c> trailer is
        /// the absolute path.
        /// </summary>
        /// <param name="target">hierarchy path (primary), else numeric instance id, else name.</param>
        /// <param name="angles">subset of {front,back,left,right,top,bottom}; null/empty => [front,back].</param>
        /// <param name="hide">descendant names/paths to exclude from this grab (transient SVM-hide).</param>
        /// <param name="margin">fraction of the frame left as border; avatar fills ~(1-margin). Raise to zoom out.</param>
        /// <param name="showGizmos">draw component gizmos (physbone/contact/collider) into the capture.</param>
        /// <param name="resolution">per-tile square edge in px; the sheet is auto-downscaled to a ~2048 edge cap.</param>
        public static string Capture(
            string target,
            string[] angles = null,
            string[] hide = null,
            float margin = 0.15f,
            bool showGizmos = false,
            int resolution = 1024)
        {
            var r = CaptureCore(target, angles, hide, margin, showGizmos, resolution, default);
            if (!r.ok) return r.fail;
            string png = WriteSheetAndManifest(r);
            if (png == null) return Fail(r.label, "failed to write the grab PNG/manifest to temp (disk full or locked path?)");
            string proxyInfo = (r.proxiesKept + r.proxiesHidden) > 0
                ? " proxies=kept:" + r.proxiesKept + ",hidden:" + r.proxiesHidden : "";
            // cam=ok signals a diffable camera manifest was written beside the png — CaptureDiff's `against`.
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[RenderAvatar] Capture {0} angles={1} tiles={2} res={3} margin={4} gizmos={5} hidden={6} excluded={7}{8} => OK gate={9} cam=ok{10}{11}{12} | png={13}",
                r.label, string.Join(",", r.manifest.angles), r.manifest.views.Length, r.manifest.tileRes,
                r.manifest.margin.ToString("0.##", CultureInfo.InvariantCulture), showGizmos ? "on" : "off",
                r.hiddenCount, r.excludedCount, proxyInfo, r.gate, r.proxyNote, r.horizonNote, r.settleNote, png);
            Debug.Log(summary);
            return summary;
        }

        // ----- Shared capture types + core ------------------------------------------------------
        // The per-grab camera manifest, written as <png>.cam.json beside every grab: the exact per-angle
        // framing + window geometry to reproduce THIS grab, so CaptureDiff frames B identically to A.
        [Serializable] private class CamFrame { public int w, h, camW, camH; public float ppp; }
        [Serializable] private class CamView { public string angle; public Vector3 pivot; public Quaternion rot; public float orthoSize; public int cropX, cropY, side; }
        [Serializable] private class CamManifest
        {
            public int schema; public string toolVersion; public string label;
            public string[] angles; public string[] hide; public bool showGizmos;
            public int cols, rows, tileRes, resolution; public float margin;
            public CamFrame frame; public CamView[] views;
        }

        // opts.pinned reuses a prior grab's per-angle framing (diff — skips silhouette auto-frame + resize-guards
        // against A's window).
        private struct CoreOpts { public CamManifest pinned; }
        private sealed class CoreResult
        {
            public bool ok; public string fail;
            public Color32[] sheet; public int sheetW, sheetH;
            public CamManifest manifest; public string label;
            public int hiddenCount, excludedCount, proxiesKept, proxiesHidden;
            public string proxyNote = "", horizonNote = "", settleNote = "", gate = "";
        }
        private static CoreResult Failed(string msg) => new CoreResult { ok = false, fail = msg };
        private static CoreResult CoreFail(string label, string reason) => Failed(Fail(label, reason));
        private static CoreResult CoreFailT(string label, string reason) => Failed(FailTransient(label, reason));

        // The isolate -> freshness -> configure -> per-angle frame/grab -> compose -> restore scaffold that both
        // public doors run. Returns the composed sheet + a fully-populated manifest, or a Fail string.
        private static CoreResult CaptureCore(
            string target, string[] angles, string[] hide, float margin, bool showGizmos, int resolution, CoreOpts opts)
        {
            resolution = Mathf.Clamp(resolution, MinResolution, MaxResolution);
            margin = Mathf.Clamp(margin, 0f, 0.8f);
            bool pin = opts.pinned != null;

            // ----- Resolve target ------------------------------------------------------------
            var root = Resolve(target);
            if (root == null)
                return CoreFail(target, "target not found — tried hierarchy path, instance id, then name in the active scene");
            string label = root.name;

            if (PrefabStageUtility.GetPrefabStage(root) != null)
                return CoreFail(label, "target is in prefab isolation — grab from the scene");

            var sv = SceneView.lastActiveSceneView;
            if (sv == null)
                return CoreFail(label, "no SceneView window open — open a Scene View first (can't grab a viewport that doesn't exist)");

            // ----- Resolve + validate angles -------------------------------------------------
            if (angles == null || angles.Length == 0) angles = new[] { "front", "back" };
            var resolvedAngles = new string[angles.Length];
            for (int i = 0; i < angles.Length; i++)
            {
                string a = (angles[i] ?? "").Trim().ToLowerInvariant();
                if (Array.IndexOf(Vocabulary, a) < 0)
                    return CoreFail(label, "unknown angle '" + angles[i] + "' — valid: " + string.Join(",", Vocabulary));
                resolvedAngles[i] = a;
            }

            // ----- Settle gate (reactive targets only) ----------------------------------------
            // An unsettled NDMF preview means the frame about to be grabbed shows a stale or
            // bodiless avatar — never return that sheet. FAIL instead, after kicking the editor
            // to the OS foreground so the rebuild's ticks fire between this call and the re-grab
            // (a background-throttled editor wedges the rebuild indefinitely; waiting in-call is
            // impossible — no editor tick runs while this synchronous call blocks the main thread).
            // The sweep first: a scripted edit NDMF hasn't noticed yet (ChangeScene-only publish +
            // parked PropertyMonitor — see the change-horizon handles above) leaves the pipeline
            // reading settled while its proxies are stale; the sweep surfaces it as a real
            // invalidation so the probe below FAILs honestly instead of grabbing the stale frame.
            string horizonNote = "";
            // Arm scope = the OUTERMOST avatar root at/above the target (NDMF's own resolver), tri-state:
            //  Found       → arm the gate on that avatar root;
            //  NoAvatarRoot→ a plain prop / scratch clone → not reactive → Settle.Exempt (MA proxies only
            //               exist under an avatar root, so there is nothing to certify);
            //  Drift       → the resolver handle is missing or its return type changed. Under installed NDMF
            //               (NdmfInstalled — the package-registration check, NOT a reflected sentinel that
            //               could drift in the same release) this must FAIL LOUD, not fall through to a
            //               silent exempt — a drifted resolver would render every avatar ungated (OK-stale),
            //               and the return-type mode also defeats a bare-null canary. Symmetric with the
            //               attribution-drift FAIL. Absent NDMF → no proxies to certify → exempt.
            string armedBy = null;
            var scopeState = ResolveArmScope(root, out GameObject armScope);
            if (scopeState == ArmScope.Drift && NdmfInstalled)
                return CoreFail(label, ArmScopeResolverDriftFailReason);
            bool reactive = !Application.isPlaying && scopeState == ArmScope.Found && HasReactiveMA(armScope, out armedBy);
            if (reactive)
            {
                horizonNote = SweepNdmfChangeHorizon();
                // Sweep handles drifted → the scripted-edit blind-spot scan never ran, so a settled probe
                // below would stamp gate=armed on an uncertified frame. Drift guards FAIL-not-skip: fail loud
                // (persistent — re-pinning the handles is the fix, not a re-grab).
                if (horizonNote == HorizonDriftNote)
                    return CoreFail(label, "change-horizon sweep unavailable (NDMF internals drifted; the "
                        + "scripted-edit blind-spot scan never ran, so freshness can't be certified) — re-pin "
                        + "the sweep handles | armed-by=" + armedBy);
                // A sweep that ran out of budget with the probe still reading settled certifies NOTHING —
                // an unswept pending edit would be the exact stale-OK this gate exists to prevent. Same
                // transient-FAIL contract as unsettled: the sweep's task keeps advancing on editor ticks
                // between calls, so the re-grab re-sweeps against a mostly-warm scan.
                if (horizonNote == HorizonIncompleteNote)
                {
                    bool kickedSweep = TryFocusKick(out string kickSweep);
                    return CoreFailT(label, "change-horizon sweep incomplete (unreported-change scan exceeded its in-call budget; freshness can't be certified) — "
                        + (kickedSweep
                            ? "focus kick sent (" + kickSweep + "): re-grab in a separate call"
                            : "focus kick failed (" + kickSweep + "): focus the Unity Editor window, then re-grab")
                        + " | armed-by=" + armedBy);
                }
            }
            // The result (not just the Unsettled compare) stays in scope: the narrowed attribution guard
            // below reuses it — no editor tick runs inside this synchronous call, so it can't go stale.
            var settle = ProbeSettle(reactive, out string pipeline);
            if (settle == Settle.Unsettled)
            {
                bool kicked = TryFocusKick(out string kick);
                // Transient: the verdict is still FAIL (no sheet — re-grab), but an unsettled preview is an
                // expected retry condition, not an error, so it logs at Warning to keep console-clean gates
                // clean (G17). Genuine failures below still log at Error.
                return CoreFailT(label, "preview not settled (NDMF rebuild in flight; " + pipeline + ") — "
                    + (kicked
                        ? "focus kick sent (" + kick + "), the rebuild can advance now: re-grab in a separate call"
                        : "focus kick failed (" + kick + "): focus the Unity Editor window, then re-grab")
                    + " | armed-by=" + armedBy);
            }
            // Settle probe itself drifted (NDMF internals reflection failed / threw): settled-state is
            // unknowable, so an OK here would stamp gate=armed on a frame we can't certify. FAIL loud
            // (persistent — re-pin the handles). This leaves only Settled / Exempt reaching the OK path, so
            // the gate token is exactly armed|exempt (never "drift").
            if (settle == Settle.Drift)
                return CoreFail(label, "settle-state unknown (NDMF preview internals drifted; " + pipeline
                    + ") — freshness can't be certified; re-pin the settle handles | armed-by=" + armedBy);

            // ----- Resolve hide list (descendants of target) + default hides -----------------
            var hideTargets = new List<GameObject>();
            if (hide != null)
                foreach (var h in hide)
                {
                    if (string.IsNullOrEmpty(h)) continue;
                    var go = ResolveDescendant(root, h.Trim());
                    if (go != null && !hideTargets.Contains(go)) hideTargets.Add(go);
                }
            // Always drop the build-added Culling mesh (see DefaultHide); a no-op when absent.
            foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                if (Array.IndexOf(DefaultHide, tr.name) >= 0 && !hideTargets.Contains(tr.gameObject))
                    hideTargets.Add(tr.gameObject);

            // ----- Reflection handles for the GUIView capture path ---------------------------
            var hostField = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);
            object host = hostField?.GetValue(sv);
            if (host == null) return CoreFail(label, "SceneView host GUIView (m_Parent) not resolvable by reflection — Unity API drift");
            var hostType = host.GetType();
            var miRepaint = hostType.GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var miGrab = hostType.GetMethod("GrabPixels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var piPos = hostType.GetProperty("position");
            if (miRepaint == null || miGrab == null || piPos == null)
                return CoreFail(label, "GUIView RepaintImmediately/GrabPixels/position not resolvable by reflection — Unity API drift");
            var cap = new Capturer(sv, host, miRepaint, miGrab, piPos);

            // ----- Record all prior state (restored in finally) ------------------------------
            var rootVis = RecordRootVisibility();                     // every loaded-scene root's self hidden-state
            var containingRoot = root.transform.root.gameObject;      // target's top-level scene-root ancestor (== root when target is itself a root)
            var cascadeHides = new Dictionary<GameObject, bool>();    // hide-list / DefaultHide / ancestor-chain siblings, hidden includeDescendants; restore cascade to recorded self-state
            var svm = SceneVisibilityManager.instance;
            var oRt = RenderTexture.active;                           // restore the caller's active RT, not null it
            var oPivot = sv.pivot; var oRot = sv.rotation; var oSize = sv.size; var oOrtho = sv.orthographic;
            var oLight = sv.sceneLighting; var oGiz = sv.drawGizmos; var oGrid = sv.showGrid;
            var svState = sv.sceneViewState;
            bool oSky = svState.showSkybox, oFog = svState.showFog, oFx = svState.showImageEffects;
            bool oToolsHidden = Tools.hidden;
            var oTool = Tools.current;
            var oSel = Selection.objects; var oActive = Selection.activeGameObject;
            bool oOutline = PiOutline != null && (bool)PiOutline.GetValue(null, null);
            bool oWire = PiWire != null && (bool)PiWire.GetValue(null, null);

            var rts = new List<RenderTexture>();
            // Freshness: SMRs whose forceMatrixRecalculationPerRender we flip true for the capture's duration
            // so a backgrounded editor re-bakes their skinned deform each render (recorded as we set them,
            // once `drawable` is known; restored in finally). See the freshness note below.
            var forcedRebake = new Dictionary<SkinnedMeshRenderer, bool>();

            try
            {
                // ----- Isolate: show only the target subtree ---------------------------------
                // Hide every other scene root fully. Skip the root that CONTAINS the target — hiding it
                // (includeDescendants) would hide the target itself; for a descendant target we instead
                // hide the ancestor-chain siblings below, leaving the target subtree and its internal
                // eye-hides untouched (so they render as the operator left them and the restore stays correct).
                foreach (var kv in rootVis)
                {
                    var r = kv.Key;
                    if (r == containingRoot || r.name == "nadena.dev.ndmf__Activator") continue;
                    svm.Hide(r, true);
                }
                if (root != containingRoot)
                    for (Transform cur = root.transform; cur.parent != null; cur = cur.parent)
                        foreach (Transform sib in cur.parent)
                            if (sib.gameObject != cur.gameObject && !cascadeHides.ContainsKey(sib.gameObject))
                            {
                                cascadeHides[sib.gameObject] = svm.IsHidden(sib.gameObject, false);
                                svm.Hide(sib.gameObject, true);
                            }
                foreach (var t in hideTargets)
                    if (!cascadeHides.ContainsKey(t))
                    {
                        cascadeHides[t] = svm.IsHidden(t, false);
                        svm.Hide(t, true);
                    }

                // NDMF preview-scene proxies: exempt from the blanket root-hide above (RecordRootVisibility
                // skips the preview scene), attributed per-proxy here — the target's own proxies must stay
                // visible or the preview hook deletes every reactive-targeted renderer from the grab.
                var keptProxies = new List<Renderer>();
                string proxyNote = IsolateNdmfProxies(root, hideTargets, cascadeHides, svm, keptProxies,
                    out int proxiesKept, out int proxiesHidden, out int proxiesDiscovered, out int proxiesAttributed);
                // Attribution drift with proxies PRESENT is a FAIL, not a note-and-continue: the grab
                // would render unattributed preview proxies as unflagged geometry. Error-level (not
                // transient) — a re-grab can't fix drifted reflection handles.
                if (IsProxyAttributionDrift(proxyNote))
                    return CoreFail(label, ProxyDriftFailReason);
                // Attribution-integrity guard: proxies WERE discovered and the session is SETTLED, yet EVERY
                // one attributes to null — the attribution API silently broke for all of them, so the grab
                // would hide every reactive-targeted proxy (IsolateNdmfProxies hides a null-attributed proxy)
                // and draw a bodiless sheet. Not a heuristic and false-FAIL-free: a healthy proxy always
                // attributes to its original, so all-null-while-settled is a genuine drift. Distinct from the
                // handle-drift FAIL above (that fires on a null handle / throw / non-GameObject return, never
                // on a clean null-for-all). NOT keyed on discovered==0 (an at-rest avatar legitimately has
                // zero proxies — the deleted presence assert's false-FAIL) nor on kept-count.
                if (IsAttributionAllNull(settle, proxiesDiscovered, proxiesAttributed))
                    return CoreFail(label, ProxyAllNullFailReason);

                // ----- Collect drawable renderers + count hidden -----------------------------
                var all = root.GetComponentsInChildren<Renderer>(true);
                int hiddenCount = 0, excludedCount = 0; // excludedCount = drawable renderers actually dropped by hide
                var drawable = new List<Renderer>();
                foreach (var rend in all)
                {
                    if (!rend.enabled || !rend.gameObject.activeInHierarchy) continue;
                    bool inHideList = IsUnderAny(rend.transform, hideTargets);
                    if (inHideList) { excludedCount++; continue; } // dropped by the hide list (SVM-hidden)
                    // Honor the operator's eye-hides: a hidden child is counted and skipped, never
                    // force-shown. To include one, the caller un-hides it before the grab (and restores it
                    // after) — the read-only tool never mutates the target subtree's own visibility.
                    if (svm.IsHidden(rend.gameObject, false)) { hiddenCount++; continue; }
                    drawable.Add(rend);
                }
                if (drawable.Count == 0)
                    return CoreFail(label, "no drawable renderers after exclusion (hidden=" + hiddenCount + " excluded=" + excludedCount + ")");

                // ----- Freshness: force a synchronous skin re-bake for the capture --------------------------
                // Two freshness layers, and the settle gate only governs one. The NDMF pipeline keeps proxy
                // CONTENT current (weights, reactive state — the #42 sweep); proxy SKIN BAKING is a plain-SMR
                // concern the pipeline never touches. A backgrounded editor freezes skin baking to the (parked)
                // editor tick, so proxies render pre-edit geometry while their weights read current — measured
                // 2026-07-16 (proxy weight synced=100, deform frozen, three settled `=> OK` captures
                // byte-identical around a BakeMesh-verified move). On a reactive target the drawn renderers ARE
                // the proxies (originals suppressed), so the force flag must land on the kept proxies, not just
                // `drawable`. Restored in finally via the same dict.
                foreach (var rend in drawable)
                    if (rend is SkinnedMeshRenderer smr && !forcedRebake.ContainsKey(smr))
                    {
                        forcedRebake[smr] = smr.forceMatrixRecalculationPerRender;
                        smr.forceMatrixRecalculationPerRender = true;
                    }
                foreach (var rend in keptProxies)
                    if (rend is SkinnedMeshRenderer smr && !forcedRebake.ContainsKey(smr))
                    {
                        forcedRebake[smr] = smr.forceMatrixRecalculationPerRender;
                        smr.forceMatrixRecalculationPerRender = true;
                    }

                // Generous first-frame bounds: inflated Renderer.bounds union guarantees the whole
                // avatar is in-view for the initial over-framed shot. NOT the framing basis (silhouette is).
                Bounds gb = drawable[0].bounds;
                for (int i = 1; i < drawable.Count; i++) gb.Encapsulate(drawable[i].bounds);
                float sphereRadius = Mathf.Max(1e-4f, gb.extents.magnitude);
                Vector3 gcenter = gb.center;

                // ----- Configure view for a clean framing pass -------------------------------
                Selection.objects = Array.Empty<UnityEngine.Object>();
                Tools.hidden = true;
                Tools.current = Tool.None; // no active transform tool -> no handle drawn for a lingering selection
                PiOutline?.SetValue(null, false, null);
                PiWire?.SetValue(null, false, null);
                sv.orthographic = true;
                sv.sceneLighting = false;   // headlight
                sv.showGrid = false;
                sv.drawGizmos = false;
                svState.showSkybox = false; svState.showFog = false; svState.showImageEffects = false;
                sv.sceneViewState = svState;

                // ----- Per-angle capture -----------------------------------------------------
                int n = pin ? opts.pinned.views.Length : resolvedAngles.Length;
                int cols = pin ? opts.pinned.cols : Mathf.CeilToInt(Mathf.Sqrt(n));
                int rows = pin ? opts.pinned.rows : Mathf.CeilToInt((float)n / cols);
                int tileRes = pin ? opts.pinned.tileRes
                    : Mathf.Max(MinTileRes, Mathf.Min(resolution, SheetEdgeCap / Mathf.Max(cols, rows)));

                var tiles = new List<Color32[]>(n);
                var views = new CamView[n];
                CamFrame frame = null;
                for (int ai = 0; ai < n; ai++)
                {
                    string angle = pin ? opts.pinned.views[ai].angle : resolvedAngles[ai];
                    Basis(angle, out Vector3 fwd, out Vector3 upv);
                    var rot = Quaternion.LookRotation(fwd, upv);
                    int cropX, cropY, side; Vector3 pivotF; float sizeF; Frame f;

                    if (pin)
                    {
                        // Reuse frame A's exact per-angle view + crop; NO silhouette auto-frame — a diff pair must
                        // share one camera, else a silhouette-changing edit moves the camera and corrupts the diff.
                        var pv = opts.pinned.views[ai];
                        pivotF = pv.pivot; rot = pv.rot; sizeF = pv.orthoSize;
                        cropX = pv.cropX; cropY = pv.cropY; side = pv.side;
                        if (showGizmos) { sv.drawGizmos = true; Selection.activeGameObject = root; Selection.objects = new UnityEngine.Object[] { root }; }
                        sv.LookAt(pivotF, rot, sizeF, true, true);
                        f = cap.Grab(showGizmos ? 3 : 2, rts);
                        if (ai == 0)
                        {
                            frame = new CamFrame { w = f.w, h = f.h, camW = f.camW, camH = f.camH, ppp = EditorGUIUtility.pixelsPerPoint };
                            var pf = opts.pinned.frame;
                            if (pf == null || pf.w != frame.w || pf.h != frame.h || pf.camW != frame.camW
                                || pf.camH != frame.camH || Mathf.Abs(pf.ppp - frame.ppp) > 1e-3f)
                                return CoreFail(label, "SceneView resized since frame A (was "
                                    + (pf == null ? "?" : pf.w + "x" + pf.h) + ", now " + frame.w + "x" + frame.h + ") — re-grab the pair");
                        }
                    }
                    else
                    {
                        // (a) generous shot -> measure silhouette
                        sv.drawGizmos = false;
                        Selection.objects = Array.Empty<UnityEngine.Object>();
                        sv.LookAt(gcenter, rot, sphereRadius, true, true);
                        var g = cap.Grab(2, rts);
                        if (!Measure(g, out RectInt bbox, out int usableTop, out Color32 bg))
                            return CoreFail(label, "degenerate silhouette on '" + angle + "' — nothing drew (isolation dropped the body?)");

                        // (c) one exact orthographic correction
                        var cam = g.cam;
                        float ortho1 = cam.orthographicSize;
                        float k = ortho1 / Mathf.Max(1e-6f, sv.size);
                        float ccx = g.camW / 2f, ccy = g.camH / 2f;
                        float wpp1 = (2f * ortho1) / g.camH;
                        Vector3 right = cam.transform.right, up = cam.transform.up;

                        int usableW = g.w - 2 * Inset;
                        int usableH = usableTop + 1;
                        side = Mathf.Min(usableW, usableH);
                        float fill = Mathf.Max(0.05f, 1f - margin);
                        float bcx = bbox.x + bbox.width * 0.5f, bcy = bbox.y + bbox.height * 0.5f;

                        float ortho2 = ortho1 * (Mathf.Max(bbox.width, bbox.height) / (fill * side));
                        sizeF = ortho2 / k;
                        float wpp2 = (2f * ortho2) / g.camH;
                        Vector3 avC = gcenter + right * ((bcx - ccx) * wpp1) + up * ((bcy - ccy) * wpp1);
                        float uCy = usableTop / 2f;
                        pivotF = avC - up * ((uCy - ccy) * wpp2);

                        // (d) final shot at the corrected frame (gizmos + root selection only here)
                        if (showGizmos)
                        {
                            sv.drawGizmos = true;
                            Selection.activeGameObject = root;
                            Selection.objects = new UnityEngine.Object[] { root };
                        }
                        sv.LookAt(pivotF, rot, sizeF, true, true);
                        f = cap.Grab(showGizmos ? 3 : 2, rts);

                        // (e) center-square crop rect (bilinear downscale below)
                        cropX = Mathf.RoundToInt(ccx - side / 2f);
                        cropY = Mathf.RoundToInt(uCy - side / 2f);
                        cropX = Mathf.Clamp(cropX, 0, Mathf.Max(0, f.w - side));
                        cropY = Mathf.Clamp(cropY, 0, Mathf.Max(0, usableTop + 1 - side));
                        if (ai == 0)
                            frame = new CamFrame { w = f.w, h = f.h, camW = f.camW, camH = f.camH, ppp = EditorGUIUtility.pixelsPerPoint };
                    }

                    tiles.Add(Downscale(f.px, f.w, cropX, cropY, side, tileRes));
                    views[ai] = new CamView { angle = angle, pivot = pivotF, rot = rot, orthoSize = sizeF, cropX = cropX, cropY = cropY, side = side };
                }

                // ----- Contact sheet + manifest ----------------------------------------------
                var sheet = Compose(tiles, tileRes, cols, rows, out int sheetW, out int sheetH);
                var manifest = new CamManifest
                {
                    schema = ManifestSchema, toolVersion = ToolVersion, label = label,
                    angles = resolvedAngles, hide = hide ?? Array.Empty<string>(), showGizmos = showGizmos,
                    cols = cols, rows = rows, tileRes = tileRes, resolution = resolution, margin = margin,
                    frame = frame, views = views
                };
                return new CoreResult
                {
                    ok = true, sheet = sheet, sheetW = sheetW, sheetH = sheetH, manifest = manifest, label = label,
                    hiddenCount = hiddenCount, excludedCount = excludedCount,
                    proxiesKept = proxiesKept, proxiesHidden = proxiesHidden,
                    proxyNote = proxyNote, horizonNote = horizonNote, settleNote = SettleNote(reactive),
                    gate = GateToken(reactive, settle),
                };
            }
            catch (Exception e)
            {
                return CoreFail(label, "capture failed: " + e.Message);
            }
            finally
            {
                // Restore the freshness re-bake flags first (null-guarded — SMRs can be NDMF-owned), before the
                // synchronous repaint below, so the frame left on the operator's screen bakes with original flags.
                foreach (var kv in forcedRebake)
                    if (kv.Key != null) kv.Key.forceMatrixRecalculationPerRender = kv.Value;

                // Restore visibility COARSELY: every subtree this grab hid (hide-list / DefaultHide /
                // ancestor siblings, and the other scene roots) returns to its own recorded self-state.
                // The CONTAINING root is skipped — it was never hidden, and cascading over it would re-show
                // the target subtree's own operator eye-hides, which the tool never touched.
                foreach (var kv in cascadeHides)
                    if (kv.Key != null) { if (kv.Value) svm.Hide(kv.Key, true); else svm.Show(kv.Key, true); }
                foreach (var kv in rootVis)
                    if (kv.Key != null && kv.Key != containingRoot && kv.Key.name != "nadena.dev.ndmf__Activator")
                    { if (kv.Value) svm.Hide(kv.Key, true); else svm.Show(kv.Key, true); }

                sv.pivot = oPivot; sv.rotation = oRot; sv.size = oSize; sv.orthographic = oOrtho;
                sv.sceneLighting = oLight; sv.drawGizmos = oGiz; sv.showGrid = oGrid;
                var st = sv.sceneViewState; st.showSkybox = oSky; st.showFog = oFog; st.showImageEffects = oFx; sv.sceneViewState = st;
                Tools.hidden = oToolsHidden;
                Tools.current = oTool;
                PiOutline?.SetValue(null, oOutline, null);
                PiWire?.SetValue(null, oWire, null);
                Selection.objects = oSel; Selection.activeGameObject = oActive;

                foreach (var rt in rts) if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                // Force a synchronous repaint of the RESTORED view so the operator's Scene View shows its
                // normal state immediately — an async Repaint() alone leaves the last captured frame on
                // screen (looks "stale") until the next editor tick / a click in the viewport.
                try { miRepaint.Invoke(host, null); } catch { /* best-effort un-stale */ }
                sv.Repaint();
                RenderTexture.active = oRt; // restore LAST — the synchronous repaint above binds its own RT
            }
        }

        // Encode the composed sheet to a timestamped PNG in temporaryCachePath, write its <png>.cam.json sidecar
        // (the framing to reproduce this grab for a diff), and prune our grabs + their sidecars as PAIRS past 30
        // days (the sidecar glob doesn't match "*.png", so it must be deleted explicitly or it orphans).
        // Returns the PNG path, or NULL on an IO failure (disk full, locked path) — callers turn null into a
        // loud FAIL, never a raw throw, so this sits outside a door's FAIL boundary safely.
        private static string WriteSheetAndManifest(CoreResult r)
        {
            byte[] png;
            var tex = new Texture2D(r.sheetW, r.sheetH, TextureFormat.RGBA32, false, false);
            try { tex.SetPixels32(r.sheet); tex.Apply(); png = tex.EncodeToPNG(); }
            finally { UnityEngine.Object.DestroyImmediate(tex); } // release the native texture even if encode throws

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = Application.temporaryCachePath + "/renderavatar_" + RunLogFormat.Sanitize(r.label) + "_" + stamp + ".png";
            try
            {
                File.WriteAllBytes(path, png);
                File.WriteAllText(path + ".cam.json", JsonUtility.ToJson(r.manifest));
            }
            catch (Exception e) { Debug.LogWarning("[RenderAvatar] grab write failed: " + e.Message); return null; }

            // Prune our grabs + their sidecars as PAIRS past 30 days, and sweep any orphaned sidecar whose png was
            // removed out-of-band (the "*.png" glob never visits it). Best-effort; a prune failure never fails a grab.
            var cutoff = DateTime.Now.AddDays(-30);
            try
            {
                foreach (var old in Directory.GetFiles(Application.temporaryCachePath, "renderavatar_*.png"))
                    try
                    {
                        if (File.GetLastWriteTime(old) < cutoff)
                        {
                            File.Delete(old);
                            if (File.Exists(old + ".cam.json")) File.Delete(old + ".cam.json");
                        }
                    }
                    catch { /* locked or already gone */ }
                const string ext = ".cam.json";
                foreach (var sc in Directory.GetFiles(Application.temporaryCachePath, "renderavatar_*.png" + ext))
                    try { if (!File.Exists(sc.Substring(0, sc.Length - ext.Length))) File.Delete(sc); }
                    catch { }
            }
            catch { /* enumeration failure — the write already succeeded; skip the prune */ }
            return path;
        }

        /// <summary>
        /// Diff a fresh grab of <paramref name="target"/> against a prior grab, reusing that grab's exact per-angle
        /// camera + framing (from its <c>.cam.json</c> sidecar) so a silhouette-changing edit can't move the camera.
        /// Reports, per angle: exact byte-equality, changed-pixel count, and the changed bounding box.
        /// </summary>
        /// <param name="target">the subtree to grab now (frame B; usually the same target as frame A).</param>
        /// <param name="against">the prior grab's png path (its <c>png=</c> trailer) — its sidecar supplies the framing.</param>
        public static string CaptureDiff(string target, string against)
        {
            var root = Resolve(target);
            string label = root != null ? root.name : target;
            if (string.IsNullOrEmpty(against))
                return Fail(label, "against is empty — pass a prior grab's png path (its png= trailer)");
            string camPath = against + ".cam.json";
            if (!File.Exists(camPath))
                return Fail(label, "no camera manifest for " + against + " (expected " + Path.GetFileName(camPath)
                    + " — absent or pruned). Re-grab frame A with Capture, then diff.");
            CamManifest A;
            try { A = JsonUtility.FromJson<CamManifest>(File.ReadAllText(camPath)); }
            catch (Exception e) { return Fail(label, "camera manifest unreadable (" + e.Message + ") — re-grab frame A"); }
            // JsonUtility zero-fills missing/renamed fields silently, so validate rather than trust the parse.
            if (A == null || A.schema != ManifestSchema || A.views == null || A.angles == null
                || A.frame == null || A.views.Length == 0 || A.views.Length != A.angles.Length
                || A.cols <= 0 || A.rows <= 0 || A.tileRes <= 0
                || A.frame.w <= 0 || A.frame.h <= 0 || A.frame.camH <= 0)
                return Fail(label, "camera manifest for " + against + " is drifted/incomplete (schema="
                    + (A == null ? -1 : A.schema) + ", expected " + ManifestSchema + ") — re-grab frame A");
            foreach (var v in A.views)
                if (v == null || !(v.orthoSize > 0f) || v.side <= 0)
                    return Fail(label, "camera manifest has a degenerate view (orthoSize/side <= 0) — re-grab frame A");
            if (!File.Exists(against))
                return Fail(label, "frame A png missing: " + against + " — re-grab frame A");

            var r = CaptureCore(target, A.angles, A.hide, A.margin, A.showGizmos, A.resolution, new CoreOpts { pinned = A });
            if (!r.ok) return r.fail;

            // Decode frame A BEFORE writing B: WriteSheetAndManifest's 30-day prune can delete a stale `against`,
            // and this read has no catch — reading first turns a pruned A into the loud FAIL above, not a raw throw.
            var aTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            Color32[] aSheet;
            try
            {
                if (!ImageConversion.LoadImage(aTex, File.ReadAllBytes(against)))
                    return Fail(label, "frame A png failed to decode: " + against);
                if (aTex.width != r.sheetW || aTex.height != r.sheetH)
                    return Fail(label, "frame A sheet size " + aTex.width + "x" + aTex.height + " != B "
                        + r.sheetW + "x" + r.sheetH + " — re-grab the pair at the same window/resolution");
                aSheet = aTex.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(aTex); }

            string pngB = WriteSheetAndManifest(r);
            if (pngB == null) return Fail(label, "failed to write the diff grab PNG/manifest to temp (disk full or locked path?)");

            int cols = r.manifest.cols, rows = r.manifest.rows, tileRes = r.manifest.tileRes;
            int identical = 0;
            var parts = new List<string>();
            for (int i = 0; i < r.manifest.views.Length; i++)
            {
                int c = i % cols, rr = i / cols;
                int x0 = c * tileRes, y0 = (rows - 1 - rr) * tileRes; // Compose layout (row 0 at top, bottom-origin sheet)
                var aTile = ExtractTile(aSheet, r.sheetW, x0, y0, tileRes);
                var bTile = ExtractTile(r.sheet, r.sheetW, x0, y0, tileRes);
                bool id = RenderDiff.Compare(aTile, bTile, tileRes, tileRes, out int changed, out RectInt bb);
                if (id) identical++;
                parts.Add(r.manifest.views[i].angle + ":changed=" + changed + ",bbox="
                    + (changed == 0 ? "-" : "(" + bb.x + "," + bb.y + "," + bb.width + "," + bb.height + ")"));
            }
            string versionNote = A.toolVersion != ToolVersion
                ? " | note=frame A grabbed by tool v" + A.toolVersion + " (now v" + ToolVersion + "); a pre-fix grab may be stale-baked" : "";
            // Carry B's freshness/settle notes — an unsettled or horizon-incomplete B undercuts the whole
            // "empty diff ⇒ immaterial GIVEN freshness" premise, so the caveat must ride the diff summary too.
            string summary = "[RenderAvatar] CaptureDiff " + label + " against=" + Path.GetFileName(against)
                + " angles=" + string.Join(",", r.manifest.angles) + " => OK gate=" + r.gate + " diff=[" + string.Join("; ", parts) + "] identical="
                + identical + "/" + r.manifest.views.Length + r.proxyNote + r.horizonNote + r.settleNote + versionNote + " | png=" + pngB;
            Debug.Log(summary);
            return summary;
        }

        // Extract a tileRes×tileRes tile at (x0,y0) from a bottom-origin composed sheet into its own buffer.
        private static Color32[] ExtractTile(Color32[] sheet, int sheetW, int x0, int y0, int tileRes)
        {
            var tile = new Color32[tileRes * tileRes];
            for (int ty = 0; ty < tileRes; ty++)
                Array.Copy(sheet, (y0 + ty) * sheetW + x0, tile, ty * tileRes, tileRes);
            return tile;
        }

        // ===== Capture ===========================================================================

        // Bundles the reflection handles + one grabbed, vertically-flipped frame (bottom-origin px[]).
        private sealed class Capturer
        {
            private readonly SceneView _sv; private readonly object _host;
            private readonly MethodInfo _repaint, _grab; private readonly PropertyInfo _pos;
            public Capturer(SceneView sv, object host, MethodInfo repaint, MethodInfo grab, PropertyInfo pos)
            { _sv = sv; _host = host; _repaint = repaint; _grab = grab; _pos = pos; }

            public Frame Grab(int repaints, List<RenderTexture> rtSink)
            {
                _sv.Focus();
                for (int i = 0; i < repaints; i++) _repaint.Invoke(_host, null);
                var pos = (Rect)_pos.GetValue(_host, null);
                float ppp = EditorGUIUtility.pixelsPerPoint;
                int w = Mathf.Max(1, (int)(pos.width * ppp)), h = Mathf.Max(1, (int)(pos.height * ppp));
                // Linear RT: GrabPixels returns display-encoded bytes; an sRGB RT would double-gamma (wash out).
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                rtSink.Add(rt);
                _grab.Invoke(_host, new object[] { rt, new Rect(0, 0, w, h) });
                var prev = RenderTexture.active;
                Texture2D tmp = null;
                Color32[] raw;
                try
                {
                    RenderTexture.active = rt;
                    tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                    tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0); tmp.Apply();
                    raw = tmp.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = prev; // restore even if ReadPixels/GetPixels32 throws
                    if (tmp != null) UnityEngine.Object.DestroyImmediate(tmp);
                }
                var px = new Color32[raw.Length];
                for (int y = 0; y < h; y++) Array.Copy(raw, y * w, px, (h - 1 - y) * w, w); // flip: row 0 = bottom
                var cam = _sv.camera;
                return new Frame { px = px, w = w, h = h, camW = cam.pixelWidth, camH = cam.pixelHeight, cam = cam };
            }
        }

        private struct Frame { public Color32[] px; public int w, h, camW, camH; public Camera cam; }

        // ===== Silhouette measurement ============================================================

        // Finds the drawn-pixel bounding box on the uniform background. Returns false if nothing drew.
        // Excludes the top tab/toolbar (grab rows above camH), the floating-overlay band (tool palette /
        // orientation gizmo, detected in the viewport's top ~32%), and a border inset.
        private static bool Measure(Frame g, out RectInt bbox, out int usableTop, out Color32 bg)
        {
            int w = g.w, camH = g.camH;
            bbox = default; usableTop = 0; bg = default;
            // A Scene View too small to inset-sample degrades to a clean Fail, not an OOB pixel read.
            if (w <= 2 * Inset || g.h <= 2 * Inset) return false;
            // background sample: the bottom-left inset corner. Sphere-fit framing keeps the silhouette
            // centred and inside the inscribed circle, so corners are guaranteed background — whereas a
            // low-CENTRE sample sits on the mesh for a full-body target (legs) and would invert the bbox.
            bg = g.px[Inset * w + Inset];

            // The floating overlays (tool palette, orientation gizmo) live only in the top-LEFT and
            // top-RIGHT corners. Scan just those edge columns for their lowest extent — never the centre,
            // where a tall/large avatar's head would otherwise be mistaken for chrome and crop the top.
            int bandBot = camH - (int)(camH * 0.32f);
            int sideW = (int)(w * 0.15f);
            int overlayLow = camH - 1;
            for (int yy = bandBot; yy <= camH - 1; yy++)
            {
                bool nonBg = false;
                for (int xx = Inset; xx < w - Inset; xx += 2)
                {
                    if (xx > sideW && xx < w - sideW) continue; // skip the centre column (avatar lives here)
                    if (Diff(g.px[yy * w + xx], bg) > FgThreshold) { nonBg = true; break; }
                }
                if (nonBg) { overlayLow = yy; break; }
            }
            usableTop = Mathf.Max(1, overlayLow - OverlayPad);

            int minX = w, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (int yy = Inset; yy <= usableTop; yy++)
                for (int xx = Inset; xx < w - Inset; xx++)
                    if (Diff(g.px[yy * w + xx], bg) > FgThreshold)
                    {
                        if (xx < minX) minX = xx; if (xx > maxX) maxX = xx;
                        if (yy < minY) minY = yy; if (yy > maxY) maxY = yy;
                    }
            if (maxX < minX || maxY < minY) return false;
            bbox = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return true;
        }

        private static int Diff(Color32 p, Color32 bg)
            => Mathf.Abs(p.r - bg.r) + Mathf.Abs(p.g - bg.g) + Mathf.Abs(p.b - bg.b);

        // ===== Post-normalize + compose ==========================================================

        // Bilinear-downscale a [srcX,srcX+side) x [srcY,srcY+side) square of the bottom-origin px[] to res x res.
        private static Color32[] Downscale(Color32[] px, int w, int srcX, int srcY, int side, int res)
        {
            var outPx = new Color32[res * res];
            float scale = (float)side / res;
            for (int oy = 0; oy < res; oy++)
            {
                float fy = srcY + (oy + 0.5f) * scale - 0.5f;
                for (int ox = 0; ox < res; ox++)
                {
                    float fx = srcX + (ox + 0.5f) * scale - 0.5f;
                    outPx[oy * res + ox] = Sample(px, w, fx, fy, srcX, srcY, side);
                }
            }
            return outPx;
        }

        private static Color32 Sample(Color32[] px, int w, float fx, float fy, int clampMinX, int clampMinY, int side)
        {
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float dx = fx - x0, dy = fy - y0;
            int x1 = x0 + 1, y1 = y0 + 1;
            int lo_x = clampMinX, hi_x = clampMinX + side - 1, lo_y = clampMinY, hi_y = clampMinY + side - 1;
            x0 = Mathf.Clamp(x0, lo_x, hi_x); x1 = Mathf.Clamp(x1, lo_x, hi_x);
            y0 = Mathf.Clamp(y0, lo_y, hi_y); y1 = Mathf.Clamp(y1, lo_y, hi_y);
            var a = px[y0 * w + x0]; var b = px[y0 * w + x1];
            var c = px[y1 * w + x0]; var d = px[y1 * w + x1];
            return new Color32(
                (byte)(a.r + (b.r - a.r) * dx + ((c.r + (d.r - c.r) * dx) - (a.r + (b.r - a.r) * dx)) * dy),
                (byte)(a.g + (b.g - a.g) * dx + ((c.g + (d.g - c.g) * dx) - (a.g + (b.g - a.g) * dx)) * dy),
                (byte)(a.b + (b.b - a.b) * dx + ((c.b + (d.b - c.b) * dx) - (a.b + (b.b - a.b) * dx)) * dy),
                255);
        }

        // Grid, row-major in requested order; empty cells filled with the background gray. Bottom-origin.
        private static Color32[] Compose(List<Color32[]> tiles, int res, int cols, int rows, out int sheetW, out int sheetH)
        {
            sheetW = cols * res; sheetH = rows * res;
            if (tiles.Count == 1) return tiles[0];
            var sheet = new Color32[sheetW * sheetH];
            var fill = new Color32(71, 71, 71, 255);
            for (int i = 0; i < sheet.Length; i++) sheet[i] = fill;
            for (int i = 0; i < tiles.Count; i++)
            {
                int r = i / cols, c = i % cols;
                int x0 = c * res, y0 = (rows - 1 - r) * res; // row 0 at the top (bottom-origin sheet)
                var t = tiles[i];
                for (int ty = 0; ty < res; ty++)
                    Array.Copy(t, ty * res, sheet, (y0 + ty) * sheetW + x0, res);
            }
            return sheet;
        }

        // ===== Angle basis (world axes; VRChat convention: avatar faces +Z) ======================

        private static void Basis(string angle, out Vector3 fwd, out Vector3 up)
        {
            switch (angle)
            {
                case "front":  fwd = new Vector3(0, 0, -1); up = Vector3.up;             break; // camera on +Z looking -Z
                case "back":   fwd = new Vector3(0, 0, 1);  up = Vector3.up;             break;
                case "left":   fwd = new Vector3(1, 0, 0);  up = Vector3.up;             break; // camera on -X looking +X
                case "right":  fwd = new Vector3(-1, 0, 0); up = Vector3.up;             break;
                case "top":    fwd = new Vector3(0, -1, 0); up = new Vector3(0, 0, 1);   break; // world-Z up avoids a degenerate rot
                default:       fwd = new Vector3(0, 1, 0);  up = new Vector3(0, 0, -1);  break; // bottom
            }
        }

        // ===== Visibility recording ==============================================================

        // Self hidden-state of every root in every loaded scene EXCEPT the NDMF preview scene, whose
        // roots are the preview proxies — never blanket-hidden (the preview hook suppresses their
        // originals, so hiding a proxy deletes its renderer from the grab outright). Proxies are handled
        // per-object by IsolateNdmfProxies. Isolation hides non-target roots with includeDescendants;
        // restore cascades each root back to this self-state (a per-object partial hide under a
        // non-target root is not preserved — rare, and documented).
        private static Dictionary<GameObject, bool> RecordRootVisibility()
        {
            var svm = SceneVisibilityManager.instance;
            var map = new Dictionary<GameObject, bool>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (!s.isLoaded || IsNdmfPreviewScene(s)) continue;
                foreach (var go in s.GetRootGameObjects()) map[go] = svm.IsHidden(go, false);
            }
            return map;
        }

        // Public NDMF API when resolvable; name fallback otherwise (the constant predates the API and
        // NDMF itself falls back to it on load). Never throws.
        private static bool IsNdmfPreviewScene(Scene s)
        {
            if (MiIsPreviewScene != null)
            {
                try { return (bool)MiIsPreviewScene.Invoke(null, new object[] { s }); }
                catch { /* fall through to the name check */ }
            }
            return s.name == "___NDMF Preview___";
        }

        // Hide every preview-scene proxy renderer whose original is NOT drawable under the target; leave
        // the target's own proxies visible (recording prior state in cascadeHides for the shared restore).
        // Attribution is per proxy-RENDERER GameObject — NDMF's proxy map is keyed on the renderer's GO,
        // and only SkinnedMeshRenderer proxies are preview-scene roots; a MeshRenderer proxy lives UNDER a
        // shadow-bone tree, so attributing scene roots would null-attribute the bone root and hide the
        // target's own non-skinned props with it. Hides are self-only for the same reason (bones carry no
        // renderers; nothing else must cascade). Attribution drift (API handle unresolved, lookup throw, or
        // a non-GameObject return) hides NOTHING and returns the drift note: a visible foreign-proxy leak
        // beats a silent body-drop.
        private static string IsolateNdmfProxies(
            GameObject root, List<GameObject> hideTargets, Dictionary<GameObject, bool> cascadeHides,
            SceneVisibilityManager svm, List<Renderer> keptProxies,
            out int kept, out int hidden, out int discovered, out int attributedNonNull)
        {
            kept = 0; hidden = 0; discovered = 0; attributedNonNull = 0;
            var proxies = new List<GameObject>();
            var seen = new HashSet<GameObject>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (!s.isLoaded || !IsNdmfPreviewScene(s)) continue;
                foreach (var sceneRoot in s.GetRootGameObjects())
                    foreach (var r in sceneRoot.GetComponentsInChildren<Renderer>(true))
                        if (seen.Add(r.gameObject)) proxies.Add(r.gameObject);
            }
            discovered = proxies.Count;
            if (proxies.Count == 0) return "";
            if (MiGetOriginalForProxy == null) return ProxyDriftNote;

            foreach (var proxy in proxies)
            {
                GameObject original;
                try
                {
                    object result = MiGetOriginalForProxy.Invoke(null, new object[] { proxy });
                    if (result != null && !(result is GameObject)) return ProxyDriftNote; // return-type drift
                    original = result as GameObject;
                }
                catch { return ProxyDriftNote; } // partial hides already applied stay recorded in cascadeHides → restored
                if (original != null) attributedNonNull++;
                bool drawable = original != null
                    && (original.transform == root.transform || original.transform.IsChildOf(root.transform))
                    && original.activeInHierarchy
                    && !IsUnderAny(original.transform, hideTargets)
                    && !svm.IsHidden(original, false);
                if (drawable)
                {
                    kept++;
                    keptProxies.AddRange(proxy.GetComponents<Renderer>());
                    continue;
                }
                hidden++;
                if (!cascadeHides.ContainsKey(proxy))
                {
                    cascadeHides[proxy] = svm.IsHidden(proxy, false);
                    svm.Hide(proxy, false); // self-only: never cascade over a shadow-bone tree
                }
            }
            return "";
        }

        private static bool IsUnderAny(Transform t, List<GameObject> ancestors)
        {
            foreach (var a in ancestors)
                if (a != null && (t == a.transform || t.IsChildOf(a.transform))) return true;
            return false;
        }

        // ===== Target resolution: hierarchy path -> instance id -> name (first match) ============

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

            var scene = SceneManager.GetActiveScene();
            foreach (var rootGo in scene.GetRootGameObjects())
            {
                var hit = FindByNameRecursive(rootGo.transform, target);
                if (hit != null) return hit.gameObject;
            }
            return null;
        }

        private static GameObject FindByHierarchyPath(string path)
        {
            var segs = path.Trim('/').Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != segs[0]) continue;
                Transform t = root.transform; bool ok = true;
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

        // A hide entry: relative path under target first, else recursive name (first match).
        private static GameObject ResolveDescendant(GameObject root, string spec)
        {
            var byPath = root.transform.Find(spec.Trim('/'));
            if (byPath != null) return byPath.gameObject;
            var byName = FindByNameRecursive(root.transform, spec);
            return byName != null ? byName.gameObject : null;
        }

        // Assembly-scan for an NDMF internal type by qualified name (references:[] rules out typeof).
        // Never throws — a scan miss or a load fault on any assembly just yields null → the drift note.
        private static Type ResolveNdmfType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(fullName); if (t != null) return t; }
                catch { /* dynamic / unloadable assembly — skip */ }
            }
            return null;
        }

        // Member-handle resolution that NEVER throws — a null declaring type, a missing member, or a
        // throwing lookup (e.g. AmbiguousMatchException) all yield null → the drift note at call time. The
        // `?.` on the Types above already short-circuits a null type; this additionally swallows a throw
        // from the reflection call itself, so class load can never raise TypeInitializationException.
        private static PropertyInfo SafeGetProperty(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            try { return type.GetProperty(name, flags); }
            catch { return null; }
        }

        private static FieldInfo SafeGetField(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            try { return type.GetField(name, flags); }
            catch { return null; }
        }

        private static MethodInfo SafeGetMethod(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            try { return type.GetMethod(name, flags); }
            catch { return null; }
        }

        // For overloaded members: binds the parameterless overload explicitly, where the name-only
        // lookup above would throw AmbiguousMatchException and null the handle.
        private static MethodInfo SafeGetMethodNoArgs(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            try { return type.GetMethod(name, flags, null, Type.EmptyTypes, null); }
            catch { return null; }
        }

        // True when every change-horizon handle resolved — the drift canary the EditMode suite gates on
        // (NDMF installed + this false = the sweep silently reopened the gate's blind spot).
        internal static bool ChangeHorizonHandlesResolved =>
            PiOwInstance != null && FiPropertyMonitor != null && MiCheckAllObjects != null
            && MiSyncScope != null && FiInternalContext != null && MiTurn != null && MiFlushInvalidates != null;

        // Same canary contract for the proxy-attribution handle (kept-proxy identification → the proxy
        // skin-rebake force flag): NDMF installed + this false = the flag silently stops landing.
        // Deliberately ONLY GetOriginalObjectForProxy — IsPreviewScene has a working name fallback
        // (IsNdmfPreviewScene), so its drift alone must not red-fail a gate that still lands the flag.
        internal static bool ProxyHandlesResolved => MiGetOriginalForProxy != null;

        // Same canary contract for the avatar-root resolver handle (ResolveArmScope → gate arming): NDMF
        // installed + this false = the resolver classifies EVERY target as no-avatar-root → every reactive
        // avatar wrongly Settle.Exempt → silent OK-stale. No name fallback exists, so the EditMode canary
        // must red-fail (Assert.IsTrue when NDMF is installed), never Ignore-when-unresolved. The ReturnType
        // check is load-bearing: GetMethod resolves by name+flags alone, so a return-type-only drift
        // (Transform → GameObject) would keep a bare != null GREEN while `raw is Transform` silently fails at
        // runtime — the "canary green while production blind" trap. handleUsable folds both checks.
        internal static bool AvatarRootResolverHandleResolved =>
            MiFindAvatarInParents != null && MiFindAvatarInParents.ReturnType == typeof(Transform);

        // Sentinel: the sweep ran out of in-call budget before CheckAllObjects finished, with the probe
        // still reading settled — freshness is UNCERTIFIED and Capture must FAIL transiently, never OK.
        internal const string HorizonIncompleteNote = "__horizon-sweep-incomplete__";

        // Runs NDMF's own unreported-change sweep (PropertyMonitor.CheckAllObjects) inside the NDMF sync
        // context, then pumps that context (Turn + FlushInvalidates) so a pending scripted edit lands as a
        // pipeline invalidation BEFORE ProbeSettle reads the gate. Bounded and self-terminating: exits the
        // moment the probe stops reading Settled (poisoned — the gate FAILs right after), or ~50ms after
        // the sweep completes with nothing found (grace for the invalidation's thread-pool continuation to
        // land; measured ~20ms live). A deadline exit with the sweep still running returns
        // HorizonIncompleteNote — the caller FAILs transiently rather than serve a sheet whose freshness
        // the sweep never certified. Only ever called on a reactive edit-mode target. Returns "", the
        // drift note, or the incomplete sentinel; never throws (a throw would fail a grab the old gate
        // would have served).
        internal static string SweepNdmfChangeHorizon()
        {
            if (!ChangeHorizonHandlesResolved) return HorizonDriftNote;
            try
            {
                // Previews globally disabled → originals render un-suppressed; nothing to sweep.
                if (PiCurrent == null || PiCurrent.GetValue(null, null) == null) return "";
                var watcher = PiOwInstance.GetValue(null, null);
                var monitor = watcher != null ? FiPropertyMonitor.GetValue(watcher) : null;
                var syncCtx = FiInternalContext.GetValue(null);
                if (monitor == null || syncCtx == null) return HorizonDriftNote;

                System.Threading.Tasks.Task sweep;
                var scope = MiSyncScope.Invoke(null, null) as IDisposable;
                try { sweep = MiCheckAllObjects.Invoke(monitor, null) as System.Threading.Tasks.Task; }
                finally { scope?.Dispose(); }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 250)
                {
                    MiTurn.Invoke(syncCtx, null);           // drain queued posts (incl. the sweep's yields)
                    // NOT redundant with Turn: Turn only flushes when its queue had work — a listener that
                    // fired synchronously inside CheckAllObjects leaves the queue empty and its invalidate
                    // pending, so an explicit flush is what lands it.
                    MiFlushInvalidates.Invoke(null, null);
                    if (ProbeSettle(true, out _) != Settle.Settled) return ""; // invalidation landed — gate FAILs next
                    if ((sweep == null || sweep.IsCompleted) && sw.ElapsedMilliseconds >= 50) return ""; // clean
                    System.Threading.Thread.Sleep(2);
                }
                // Deadline with the sweep unfinished and the probe still settled: nothing was certified.
                return (sweep != null && !sweep.IsCompleted) ? HorizonIncompleteNote : "";
            }
            catch { return HorizonDriftNote; }
        }

        internal enum Settle { Exempt, Settled, Unsettled, Drift }

        // The review-gate decisions, extracted pure so the headless suite can pin them — batchmode has no
        // preview scene or session, so the full CaptureCore paths are live-gate-only (see
        // Tests/Editor/RenderAvatarFreshnessGate.md). The attribution guard is keyed on discovery/attribution
        // totals, NOT on kept-count: kept==0 is legitimate for a plain leaf target under a reactive avatar
        // (its neighbor's proxies are foreign and hidden, not lost). discovered==0 is likewise NOT a fault —
        // an at-rest avatar has zero proxies; only discovered>0 with every one attributing null while settled
        // is the silent body-drop.
        internal static bool IsProxyAttributionDrift(string proxyNote) => proxyNote == ProxyDriftNote;
        internal static bool IsAttributionAllNull(Settle settle, int discovered, int attributedNonNull)
            => settle == Settle.Settled && discovered > 0 && attributedNonNull == 0;

        // The OK-path gate token — armed ONLY when a reactive avatar probed settled; every other OK path
        // (non-reactive target, previews globally disabled) is exempt. Unsettled / Drift / horizon-drift all
        // FAIL before the OK return, so this never yields "drift": the summary token is exactly armed|exempt,
        // matching the docs. Extracted pure so the headless test can pin that invariant.
        internal static string GateToken(bool reactive, Settle settle)
            => reactive && settle == Settle.Settled ? "armed" : "exempt";

        // The one settle probe (read-only), shared by the pre-grab gate, the sweep's pump loop, and the
        // residual note. `reactiveEditMode` is the caller's ONE precomputed !isPlaying && HasReactiveMA
        // hierarchy scan (the probe is called per pump iteration — rescanning here would repeat it ~100×
        // per sweep). Exempt when there is no settle risk (play mode / non-reactive target); Unsettled
        // when a rebuild is in flight (predicate false, or any runtime hop legitimately null — no pipeline
        // yet / between builds / absent fit); Drift when reflection itself failed (a handle didn't
        // resolve, or a read threw). `pipeline` names the exact hop for the gate's fail-loud message. The
        // whole body is one try/catch and it ALWAYS returns, so an exception can never escape to
        // Capture's outer catch.
        private static Settle ProbeSettle(bool reactiveEditMode, out string pipeline)
        {
            pipeline = "";
            try
            {
                if (!reactiveEditMode) return Settle.Exempt;       // play mode or no reactive fit → nothing to settle
                // Drift: a reflection handle failed to resolve at class load.
                if (PiCurrent == null || FiProxySession == null || FiActive == null
                    || FiNext == null || PiIsReady == null || PiIsInvalidated == null)
                { pipeline = "NDMF internals drifted"; return Settle.Drift; }
                // Global poll (NDMF has one preview session; single-LLM MCP → only this agent's edit unsettles it).
                // Current is null EXACTLY when previews are globally disabled (NDMFPreview.cs: Current =
                // !EnablePreviewsUI || _disablePreviewDepth != 0 ? null : _globalPreviewSession) — in that
                // state originals render un-suppressed, so the sheet is trustworthy: Exempt, never a FAIL.
                var current = PiCurrent.GetValue(null, null);
                if (current == null) { pipeline = "previews disabled (sheet renders originals)"; return Settle.Exempt; }
                var prox = FiProxySession.GetValue(current);
                if (prox == null) { pipeline = "_proxySession=null"; return Settle.Unsettled; }
                var active = FiActive.GetValue(prox);
                if (active == null) { pipeline = "_active=null (no pipeline built)"; return Settle.Unsettled; }
                var next = FiNext.GetValue(prox);            // non-null == a rebuild is queued to swap in
                bool isReady = (bool)PiIsReady.GetValue(active, null);
                bool isInvalidated = (bool)PiIsInvalidated.GetValue(active, null);
                pipeline = "_active.IsReady=" + isReady + " _active.IsInvalidated=" + isInvalidated
                    + " _next=" + (next == null ? "null" : "queued");
                return (isReady && !isInvalidated && next == null) ? Settle.Settled : Settle.Unsettled;
            }
            catch (Exception e)
            {
                pipeline = "probe threw: " + e.Message;      // unexpected read exception / cast mismatch
                return Settle.Drift;
            }
        }

        // Residual settle-state note for the summary (see the call site for when it can still fire).
        // Called after cap.Grab: no update tick fires inside the synchronous call, so the polled state
        // is exactly the state that produced the frames.
        private static string SettleNote(bool reactiveEditMode)
        {
            switch (ProbeSettle(reactiveEditMode, out _))
            {
                case Settle.Unsettled: return UnsettledNote;
                case Settle.Drift: return DriftNote;
                default: return "";
            }
        }

        // ----- Editor focus kick (Windows) --------------------------------------------------------
        // A background-throttled editor stops painting frames, which stops the ticks that advance the
        // NDMF rebuild — the preview wedges until the editor is foregrounded. The kick restores and
        // foregrounds the editor's main window so frames fire again between MCP calls. Windows refuses
        // SetForegroundWindow from a background process unless it recently sent input, so a synthetic
        // Alt tap (down+up) goes first to release the foreground lock — the accepted user32 workaround.
        // Only ever called on the gate's fail path, so the stray Alt lands rarely and on purpose.
        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x2;
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // Escape hatch: a measurement session that must hold the editor backgrounded (freshness
        // probing, focus-state matrices) sets this pref true; the FAIL message then degrades to the
        // manual instruction. Default off — shipped kick behavior unchanged.
        internal const string DisableFocusKickPref = "Ryan6VRC.AgentTools.RenderAvatar.DisableFocusKick";

        private static bool TryFocusKick(out string detail)
        {
            if (EditorPrefs.GetBool(DisableFocusKickPref, false))
            { detail = "kick disabled by pref (" + DisableFocusKickPref + ")"; return false; }
            if (Application.platform != RuntimePlatform.WindowsEditor)
            { detail = "non-Windows editor, no kick path"; return false; }
            try
            {
                // isFocused, not a GetForegroundWindow pid check: the Unity process owns hidden helper
                // windows that can hold OS foreground while the editor itself is unfocused and throttled
                // (observed live) — the pid check reads "foreground" exactly when the kick is needed.
                if (EditorApplication.isFocused) { detail = "editor already focused"; return true; }
                using (var proc = System.Diagnostics.Process.GetCurrentProcess()) // qualified: `using System.Diagnostics` would make Debug ambiguous
                {
                    var hWnd = proc.MainWindowHandle;
                    if (hWnd == IntPtr.Zero) { detail = "editor main window handle unresolved"; return false; }
                    if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
                    if (SetForegroundWindow(hWnd)) { detail = "editor foregrounded"; return true; }
                    // Bare call refused (background process without recent input). The synthetic Alt tap
                    // marks this process as input-producing, which unlocks SetForegroundWindow. Cost owned:
                    // the tap lands on whatever app IS foreground and can toggle its menu-accelerator mode —
                    // taken only on this already-refused path, key-up guaranteed even if the down throws.
                    try { keybd_event(VK_MENU, 0, 0, UIntPtr.Zero); }
                    finally { keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); }
                    bool ok = SetForegroundWindow(hWnd);
                    detail = ok ? "editor foregrounded (Alt-tap fallback)" : "SetForegroundWindow refused";
                    return ok;
                }
            }
            catch (Exception e) { detail = "kick threw: " + e.Message; return false; }
        }

        // The MA reactive-object family (+ Blendshape Sync) — components whose rendered result only
        // resolves in a settled NDMF preview, so a grab of them can be stale before that preview settles.
        // Matched by type name (this asmdef has no MA reference — references: []), scoped to the
        // modular_avatar namespace so a same-named foreign type can't false-positive. Static components
        // (Merge Animator/Armature, Bone Proxy, Menu*, Parameters, Mesh Settings, VRChat Settings)
        // deliberately do NOT match — they carry no preview-resolution risk. Drives SettleNote's
        // cheap-first non-reactive short-circuit (the settle-state pre-filter).
        private static readonly string[] ReactiveMarkers =
        {
            "ShapeChanger", "BlendshapeSync", "MaterialSetter", "MaterialSwap", "ObjectToggle",
            "MeshDeleter", "MeshCutter", "RemoveVertexColor", "ScaleAdjuster",
        };

        // `armedBy` = hierarchy path of the FIRST matched reactive component's GameObject (null when none),
        // threaded into the settle-FAIL messages so a re-grab loop can name what armed the gate. The gate
        // arms on ANY match (inactive included — MA analyzes inactive reactives, so an at-rest inactive one
        // can still drive a proxy; conservative, costs a probe).
        internal static bool HasReactiveMA(GameObject root, out string armedBy)
        {
            armedBy = null;
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var ty = mb.GetType();
                if ((ty.Namespace ?? "").IndexOf("modular_avatar", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var mk in ReactiveMarkers)
                    if (ty.Name.IndexOf(mk, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        armedBy = HierarchyPath(mb.transform);
                        return true;
                    }
            }
            return false;
        }

        // "Is NDMF installed?" — the authoritative package-registration check (the same signal the EditMode
        // canary uses), NOT a reflected NDMF type. Gates whether a resolver Drift FAILs loud: a reflected
        // sentinel could itself drift in the same NDMF release that renamed the resolver, silently reopening
        // the OK-stale hole (council review round 2). Computed once — install state is fixed within a domain.
        private static readonly bool NdmfInstalled = ComputeNdmfInstalled();
        private static bool ComputeNdmfInstalled()
        {
            try
            {
                foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                    if (p.name == "nadena.dev.ndmf") return true;
            }
            catch { /* package manager not ready at this moment — treat as absent; next domain recomputes */ }
            return false;
        }

        internal enum ArmScope { NoAvatarRoot, Found, Drift }

        // Pure tri-state classifier, shared by the live resolver and the headless truth-table test.
        // handleUsable folds BOTH the null-handle and return-type checks (a return-type drift makes the
        // handle unusable even though GetMethod still found it). A non-null, non-Transform invoke result is
        // a second-line catch for the same drift. Distinguishing NoAvatarRoot (a real null return — a plain
        // prop) from Drift is the whole point: the former is a legitimate Settle.Exempt, the latter must
        // FAIL loud so a silent OK-stale can't slip through (see the call site).
        internal static ArmScope ClassifyArmScope(bool handleUsable, object rawResult)
            => !handleUsable ? ArmScope.Drift
             : rawResult == null ? ArmScope.NoAvatarRoot
             : rawResult is Transform ? ArmScope.Found
             : ArmScope.Drift;

        // Gate-arm scope resolution: the OUTERMOST avatar root at/above the target, via reflected NDMF
        // RuntimeUtil.FindAvatarInParents — the semantics NDMF's preview uses to decide what to proxy. A
        // nested descriptor can't scope the gate too narrowly (the old nearest-match bug) and a leaf-mesh
        // target still resolves up to its whole avatar. Never throws (references:[] rules out typeof); a
        // throw is classified Drift, not swallowed to a false NoAvatarRoot.
        internal static ArmScope ResolveArmScope(GameObject root, out GameObject scope)
        {
            scope = null;
            bool handleUsable = AvatarRootResolverHandleResolved; // null-handle OR return-type drift → unusable
            object raw = null;
            if (handleUsable)
            {
                try { raw = MiFindAvatarInParents.Invoke(null, new object[] { root.transform }); }
                catch { return ArmScope.Drift; }
            }
            var state = ClassifyArmScope(handleUsable, raw);
            if (state == ArmScope.Found) scope = ((Transform)raw).gameObject;
            return state;
        }

        private static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (var cur = t.parent; cur != null; cur = cur.parent) path = cur.name + "/" + path;
            return path;
        }

        // Family arrow; NO `| png=` trailer — the schema never points at a PNG that isn't on disk. Genuine
        // failures (bad input, API drift, degenerate grab) log at Error. internal for the severity-split test.
        internal static string Fail(string label, string reason)
        {
            string msg = "[RenderAvatar] " + (string.IsNullOrEmpty(label) ? "?" : label) + " => FAIL: " + reason;
            Debug.LogError(msg);
            return msg;
        }

        // A transient, retryable FAIL — the preview settle-gate (G17). Same "=> FAIL:" contract string as
        // Fail (no sheet was produced; re-grab), but logs at Warning: an unsettled preview is an expected
        // retry condition, not an error, so it doesn't pollute a console-clean gate. internal for the test.
        internal static string FailTransient(string label, string reason)
        {
            string msg = "[RenderAvatar] " + (string.IsNullOrEmpty(label) ? "?" : label) + " => FAIL: " + reason;
            Debug.LogWarning(msg);
            return msg;
        }
    }
}
