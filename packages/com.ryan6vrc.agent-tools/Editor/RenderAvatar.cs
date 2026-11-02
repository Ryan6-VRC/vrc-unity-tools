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
    /// Isolated Scene-View render of ONE avatar subtree into a contact-sheet PNG, the visual backstop for the
    /// spatial-judgment steps compose-mergeable defers to the operator. Contract: docs/unity-tools.md.
    ///
    /// <b>Freshness.</b> Target any subtree; the gate arms at its outermost avatar root, so a leaf-mesh target
    /// still reaches its whole avatar and a non-avatar prop arms nothing. OK carries <c>gate=armed</c> (a
    /// reactive avatar probed settled) or <c>gate=exempt</c> (nothing to certify: no avatar root, no reactive MA
    /// component, play mode, or previews globally disabled). FAIL says what to do next: re-grab an unsettled
    /// preview, focus a backgrounded editor, or re-pin a drifted NDMF handle. Three layers, each with its
    /// mechanism at its code: the settle gate in CaptureCore with SweepNdmfChangeHorizon; the freshness canary,
    /// which nudges a drawn blendshape and demands pixels move, skipped only in play mode and on SMR-less grabs;
    /// and the shading guard, which compiles synchronously and FAILs on any exact <c>#00FFFF</c> in a served or
    /// token-gating frame (CountPlaceholderPx). Freeze states, arming recipes and the live protocol:
    /// Tests/Editor/RenderAvatarFreshnessGate.md.
    ///
    /// <b>The shading guard can stall a first grab.</b> A freshly imported avatar compiles cold variants with
    /// the editor frozen for the whole call and no progress UI possible, and a cold lilToon/Poiyomi avatar could
    /// reach an <c>execute_code</c> timeout that presents as a hang. Read a stalled first grab as this before
    /// diagnosing it as anything else.
    ///
    /// INSPECTION-class, with two declared exceptions. The freshness canary WRITES scene data: a blendshape
    /// weight swing on one drawn renderer, restored to the exact prior value in the same call; if the swing
    /// dirtied a previously-clean scene the flag is cleared best-effort, with an in-band note when it could not
    /// be. The shading guard flips the editor-global <c>EditorSettings.asyncShaderCompilation</c> for the grab's
    /// duration, so any other window compiles synchronously too. Everything else (view transform, display
    /// toggles, selection, SceneVisibilityManager hides, SMR re-bake flags) is transient and restored in a
    /// finally. Visibility restore is COARSE: each subtree this grab hid returns to its own self-state, so a
    /// nested eye-hide the operator set under a subtree the grab hid is not preserved. The target subtree's own
    /// eye-hides are never touched and the tool never reveals what the operator hid: to see a hidden part,
    /// un-hide it before the grab and restore it after.
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

        // <png>.cam.json schema: bump on any field rename, or to make old baselines refuse (diff FAILs on a
        // mismatch, naming "re-grab frame A"). It is the only staleness instrument: a package assembly's Version
        // reads 0.0.0.0 and package.json's version is hand-maintained, so a tool-version field cannot do this job.
        private const int ManifestSchema = 2;

        // Descendants excluded from every grab, merged with the caller's `hide`. The non-destructive
        // build adds a ~10 km "Culling" mesh that renders as a huge pale surface and would otherwise
        // become the framed silhouette.
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
        // name (never typeof). A renamed or removed member leaves its field null → Settle.Drift at call time,
        // never a class-load throw. Settled predicate: ProbeSettle.
        private const string UnsettledNote = " | note=preview not settled (NDMF rebuild in flight) — re-grab in a separate call";
        private const string DriftNote = " | note=settle-state unknown (NDMF internals drifted)";

        private static readonly Type PreviewSessionType = VendorReflect.FindType("nadena.dev.ndmf.preview.PreviewSession");
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
        // Handles for SweepNdmfChangeHorizon, which owns the blind spot they close.
        private const string HorizonDriftNote =
            " | note=change-horizon sweep unavailable (NDMF internals drifted) — a recent scripted edit may not be visible to the settle gate";
        private static readonly Type ObjectWatcherType = VendorReflect.FindType("nadena.dev.ndmf.cs.ObjectWatcher");
        private static readonly PropertyInfo PiOwInstance = SafeGetProperty(ObjectWatcherType, "Instance",
            BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo FiPropertyMonitor = SafeGetField(ObjectWatcherType, "PropertyMonitor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo MiCheckAllObjects = SafeGetMethodNoArgs(FiPropertyMonitor?.FieldType,
            "CheckAllObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Type SyncContextType = VendorReflect.FindType("nadena.dev.ndmf.preview.NDMFSyncContext");
        private static readonly MethodInfo MiSyncScope = SafeGetMethodNoArgs(SyncContextType, "Scope",
            BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo FiInternalContext = SafeGetField(SyncContextType, "InternalContext",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo MiTurn = SafeGetMethodNoArgs(FiInternalContext?.FieldType, "Turn",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly Type ComputeContextType = VendorReflect.FindType("nadena.dev.ndmf.preview.ComputeContext");
        private static readonly MethodInfo MiFlushInvalidates = SafeGetMethodNoArgs(ComputeContextType,
            "FlushInvalidates", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        // ----- NDMF preview-scene proxy attribution (reflection, same drift rules) ---------------
        // Both hooks are public NDMF API, reflected only because this asmdef has references:[].
        private const string ProxyDriftNote =
            " | note=NDMF proxy attribution drifted — preview proxies left visible (possible foreign-geometry leak)";
        // ProxyDriftNote is a sentinel that never reaches a summary: CaptureCore converts it to this FAIL.
        internal const string ProxyDriftFailReason =
            "NDMF proxy attribution drifted — preview proxies are present but attribution is unavailable, so the "
            + "grab would render unattributed preview proxies as unflagged geometry (possible foreign-geometry "
            + "leak); re-pin GetOriginalObjectForProxy before trusting a sheet";
        internal const string ProxyAllNullFailReason =
            "NDMF preview proxies are present and the session is settled, but attribution returned null for "
            + "every one — the grab would hide all reactive-targeted proxies and draw a bodiless sheet; "
            + "re-pin GetOriginalObjectForProxy before trusting a sheet";
        internal const string ArmScopeResolverDriftFailReason =
            "NDMF is installed but its avatar-root resolver (RuntimeUtil.FindAvatarInParents) drifted — the "
            + "handle is missing or its return type changed, so every target would resolve to no-avatar-root "
            + "and render ungated (a silent OK-stale). Re-pin the resolver handle before trusting a sheet";
        private static readonly Type PreviewSceneManagerType =
            VendorReflect.FindType("nadena.dev.ndmf.preview.NDMFPreviewSceneManager");
        private static readonly MethodInfo MiIsPreviewScene = SafeGetMethod(PreviewSceneManagerType, "IsPreviewScene",
            BindingFlags.Static | BindingFlags.Public);
        private static readonly Type NdmfPreviewType = VendorReflect.FindType("nadena.dev.ndmf.preview.NDMFPreview");
        private static readonly MethodInfo MiGetOriginalForProxy = SafeGetMethod(NdmfPreviewType, "GetOriginalObjectForProxy",
            BindingFlags.Static | BindingFlags.Public);

        // ----- NDMF avatar-root resolver (reflection, same drift rules) ---------------------------
        private static readonly Type RuntimeUtilType = VendorReflect.FindType("nadena.dev.ndmf.runtime.RuntimeUtil");
        private static readonly MethodInfo MiFindAvatarInParents = SafeGetMethod(RuntimeUtilType,
            "FindAvatarInParents", BindingFlags.Static | BindingFlags.Public);

        // ----- Pending-script-reload guard (reflection on pinned-Unity internals) -----------------
        // A compiled-but-unloaded script reload parks GPU skin re-baking editor-wide while the editor
        // is unfocused: proxy CONTENT keeps syncing, but every render serves the parked deform and the
        // forcedRebake flag does nothing. isCompiling reads false once the compile finishes, so the
        // reload-request state is the only diagnostic. Only a REQUESTED reload arms the guard;
        // CanReloadAssemblies=false alone is a benign lock with no pending work and only feeds the message.
        // A null handle skips the guard rather than FAILing, because the freshness canary still catches this
        // state end-to-end; the EditMode canary red-fails on the drift.
        private static readonly MethodInfo MiIsScriptReloadRequested = SafeGetMethodNoArgs(
            typeof(UnityEditorInternal.InternalEditorUtility), "IsScriptReloadRequested",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo MiCanReloadAssemblies = SafeGetMethodNoArgs(
            typeof(EditorApplication), "CanReloadAssemblies",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        internal static bool ReloadGuardHandlesResolved =>
            MiIsScriptReloadRequested != null && MiCanReloadAssemblies != null;

        private static bool IsScriptReloadPending(out bool reloadLocked)
        {
            reloadLocked = false;
            bool pending;
            try
            {
                pending = MiIsScriptReloadRequested != null && (bool)MiIsScriptReloadRequested.Invoke(null, null);
            }
            catch { return false; } // primary probe unavailable — the freshness canary still covers the state
            if (!pending) return false;
            // Secondary, message-only probe in its own try: its failure must not defeat the detection above.
            try { reloadLocked = MiCanReloadAssemblies != null && !(bool)MiCanReloadAssemblies.Invoke(null, null); }
            catch { /* lock state unknown — the FAIL just omits the lock hint */ }
            return true;
        }

        // ----- End-to-end freshness canary ---------------------------------------------------------
        // The forcedRebake flag cures only the multi-hour-idle freeze. A pending script reload and a play-mode
        // round-trip exited into an unfocused editor (which also stops proxy content sync) defeat the flag
        // itself, so this layer certifies the OUTCOME: after the tiles are grabbed, nudge one drawn ORIGINAL's largest-amplitude blendshape, which
        // exercises the original→proxy sync leg, re-grab angle 0 pinned, and demand pixels move. One live probe
        // certifies the whole grab because the parking is editor-wide, never per-renderer.
        private const int CanaryFloor = 8;          // px at TILE res; measured no-op floors 0–4, real nudges O(10^4)
        private const int CanaryMinExpectedPx = 16; // px at TILE res a candidate shape must predict, else it is skipped
        private const int CanaryMaxTries = 3;       // occlusion insurance: distinct shapes tried before FAILing
        private const string CanaryLiveToken = " canary=live";
        internal const string CanaryUnavailableNote =
            " | note=freshness canary unavailable (no visible blendshape probe on the drawn meshes) — "
            + "parked-deform states cannot be ruled out for this grab";
        private const string CanaryDirtyNote =
            " | note=canary nudge left the scene dirty (ClearSceneDirtiness drifted) — save or revert manually";

        // ----- Shading: the outcome scan -----------------------------------------------------------
        // The capture forces synchronous shader compilation (see the config block in CaptureCore), so Unity's
        // flat #00FFFF still-compiling placeholder should be undrawable. Every frame this tool SERVES, and every
        // frame that GATES a token, is scanned for it anyway; the floor is 0, measured rather than tuned.
        // Detection bound: Downscale is 4-tap bilinear AT A POINT, not a box filter over the footprint, so at
        // scale>1 a region narrower than the sample spacing (side/tileRes) can fall between taps and be missed
        // ENTIRELY rather than diluted, and an exact survivor generally needs a pure-cyan 2x2 at the sample
        // point. The bound widens as `resolution` drops until tileRes hits its MinTileRes floor. Sizes: the gate doc.
        internal static int CountPlaceholderPx(Color32[] tile)
        {
            if (tile == null) return 0;
            int n = 0;
            for (int i = 0; i < tile.Length; i++)
            {
                var c = tile[i];
                if (c.r == 0 && c.g == 255 && c.b == 255) n++;
            }
            return n;
        }

        // ShaderUtil.anythingCompiling is editor-global and has measured true across a grab that scanned at
        // exactly zero, so it only orders the remedies. Every placeholder hit is a hard FAIL.
        internal static string BuildPlaceholderFailReason(
            string where, int total, string worstAngle, int worstN, bool compiling)
            => "flat #00FFFF placeholder px in " + where + " — " + total + " px, worst '" + worstAngle
               + "' at " + worstN + " (floor 0: a healthy sheet measures exactly zero). Unity draws that "
               + "placeholder for a shader variant still compiling, which this grab forces synchronous "
               + "compilation to prevent, so a path escaped the guard. "
               + (compiling
                   ? "Shader compilation is still in flight — editor-global, so it cannot be attributed to "
                     + "this grab either way, but it is consistent with the known async-compile producer: "
                     + "re-grab first, which cleared it when last measured."
                   : "No shader compilation is in flight, so the known async-compile producer is not "
                     + "indicated; a re-grab may not clear this. Measure the mechanism before assuming the "
                     + "sync-compile guard is the fix.")
               + " If it survives a re-grab: an authored unlit or emissive pure-cyan material counts here too "
               + "(Tests/Editor/RenderAvatarFreshnessGate.md §Shading cell). Re-grab with showGizmos:false to "
               + "rule the gizmo pass in or out, then `hide` the offending renderer; a count that repeats "
               + "identically on an unchanged scene is an authored surface, not a placeholder.";

        internal static string BuildFrameAPlaceholderFailReason(string aName, int total, string worstAngle, int worstN)
            => "flat #00FFFF placeholder px in frame A (" + aName + ") — " + total + " px, worst '" + worstAngle
               + "' at " + worstN + " (floor 0: a healthy sheet measures exactly zero). A placeholder region "
               + "manufactures `changed` px against an honestly-shaded B, or hides a real change under a flat "
               + "fill, so this diff's counts cannot be trusted — re-grab frame A with Run, then diff. If "
               + "the fresh A carries it too, that grab's own FAIL names the remedies "
               + "(Tests/Editor/RenderAvatarFreshnessGate.md §Shading cell).";

        // Top-CanaryMaxTries blendshapes of a mesh by max vertex delta, cached per mesh because the scan is
        // O(shapes × verts) and capture-hot. The Dictionary holds strong Mesh keys: bounded by the meshes seen
        // this domain, never evicted.
        private struct CanaryShape { public int index; public float delta; }
        private static readonly Dictionary<Mesh, CanaryShape[]> CanaryShapeCache = new Dictionary<Mesh, CanaryShape[]>();
        private static CanaryShape[] CanaryShapeCandidates(Mesh mesh)
        {
            if (CanaryShapeCache.TryGetValue(mesh, out var cached)) return cached;
            var found = new List<CanaryShape>();
            try
            {
                if (mesh != null && mesh.isReadable && mesh.blendShapeCount > 0)
                {
                    var dv = new Vector3[mesh.vertexCount];
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        mesh.GetBlendShapeFrameVertices(i, mesh.GetBlendShapeFrameCount(i) - 1, dv, null, null);
                        float mx = 0f;
                        for (int v = 0; v < dv.Length; v++) { float m = dv[v].sqrMagnitude; if (m > mx) mx = m; }
                        found.Add(new CanaryShape { index = i, delta = Mathf.Sqrt(mx) });
                    }
                    found.Sort((a, b) => b.delta.CompareTo(a.delta));
                    if (found.Count > CanaryMaxTries) found.RemoveRange(CanaryMaxTries, found.Count - CanaryMaxTries);
                }
            }
            catch { found.Clear(); } // a throwing mesh is a mesh with no probe, never a capture-killer
            var arr = found.ToArray();
            CanaryShapeCache[mesh] = arr;
            return arr;
        }

        // No focus condition: a kicked-but-not-interacted editor reads FOCUSED while still parked, so a focus
        // skip would certify a stale sheet one call after the canary's own kick.
        internal static bool ShouldRunCanary(bool isPlaying, bool anySmrDrawn)
            => !isPlaying && anySmrDrawn;
        internal static bool CanaryAlive(int changedPixels) => changedPixels > CanaryFloor;
        // Predicted TILE-resolution pixel amplitude of a nudge: worldDelta is the shape's max vertex
        // delta × the renderer's largest lossyScale axis (mesh deltas are mesh-local); the swing
        // (w>50 ? 0 : 100) realizes at least half of it; worldPerSourcePx is 2·ortho / camH of the live
        // canary camera; side→tileRes is the downscale the compared tiles undergo.
        internal static float CanaryExpectedTilePx(float worldDelta, float worldPerSourcePx, int side, int tileRes)
            => worldPerSourcePx <= 0f || side <= 0
                ? 0f
                : (0.5f * worldDelta / worldPerSourcePx) * ((float)tileRes / side);
        internal static string BuildCanaryFailReason(string nudgeDesc, int changed, bool kicked, string kickDetail)
            => "freshness canary dead — nudging " + nudgeDesc + " moved " + changed + " px (floor " + CanaryFloor
               + "), so the editor is serving parked skin deform (the post-play/backgrounded freeze, 2026-07-29) "
               + "and any sheet would certify stale geometry. "
               + (kicked
                   ? "focus kick sent (" + kickDetail + ") — but programmatic focus alone does not wake the parked "
                     + "scheduler (measured): CLICK INTO the Unity Editor, stay ~10 s, then re-grab; if it still "
                     + "FAILs, enter and exit play mode once with the editor focused (measured cure)"
                   : "focus kick failed (" + kickDetail + ") — click into the Unity Editor window, stay ~10 s, then "
                     + "re-grab; if it still FAILs, enter and exit play mode once with the editor focused (measured cure)")
               + ". If every named shape could be occluded at this angle, re-grab with a different angle first "
               + "before treating this as a freeze";
        // Backstop for a canary nudge dirtying a scene the operator had clean (blendshape weight writes have
        // measured not to). Internal API, best-effort: a null handle leaves the flag and never throws.
        private static readonly MethodInfo MiClearSceneDirtiness = SafeGetMethod(
            typeof(EditorSceneManager), "ClearSceneDirtiness", BindingFlags.Static | BindingFlags.NonPublic);

        internal static string BuildReloadPendingFailReason(bool reloadLocked)
            => "a script reload is pending (compiled assemblies waiting to load) — in this state the editor "
               + "serves parked skin deform and would certify stale geometry as fresh (measured 2026-07-29); "
               + "let the reload land (an idle editor tick, or focus the editor), then re-grab."
               + (reloadLocked ? " A tool holds LockReloadAssemblies — unlock it first" : "");

        /// <summary>
        /// Render the GameObject subtree at <paramref name="target"/> in isolation from
        /// <paramref name="angles"/>, silhouette-frame each, and write a single contact-sheet PNG to
        /// Application.temporaryCachePath; return a one-line summary whose in-band <c>png=</c> trailer is
        /// the absolute path.
        /// </summary>
        /// <param name="target">hierarchy path (primary), else numeric instance id, else name.</param>
        /// <param name="angles">subset of {front,back,left,right,top,bottom}; null/empty => [front,back].</param>
        /// <param name="hide">descendant names/paths to exclude from this grab (transient SVM-hide). Each entry
        /// resolves as a descendant of <paramref name="target"/> — a path relative to it, or an exact child name.
        /// An entry that resolves to nothing FAILs the grab rather than rendering the subtree it named.</param>
        /// <param name="margin">fraction of the frame left as border; avatar fills ~(1-margin). Raise to zoom out.</param>
        /// <param name="showGizmos">draw component gizmos (physbone/contact/collider) into the capture.</param>
        /// <param name="resolution">per-tile square edge in px; the sheet is auto-downscaled to a ~2048 edge cap.</param>
        /// <param name="framePrefix">when set, each angle's tile is also written as <c>&lt;framePrefix&gt;_&lt;angle&gt;.png</c>
        /// (e.g. <c>&lt;dir&gt;/&lt;stage&gt;_&lt;pose&gt;</c>), the per-view layout frame tilers read. An absolute prefix is
        /// written as given; a relative one lands under the project's <c>test-output/</c> (created as needed), and one that
        /// already starts with <c>test-output/</c> is not doubled.</param>
        public static string Run(
            string target,
            string[] angles = null,
            string[] hide = null,
            float margin = 0.15f,
            bool showGizmos = false,
            int resolution = 1024,
            string framePrefix = null)
        {
            var r = CaptureCore(target, angles, hide, margin, showGizmos, resolution, default);
            if (!r.ok) return r.fail;
            string png = WriteSheetAndManifest(r);
            if (png == null) return Fail(r.label, "failed to write the grab PNG/manifest to temp (disk full or locked path?)");
            if (framePrefix != null)
            {
                framePrefix = ResolveFramePrefix(framePrefix, Directory.GetParent(Application.dataPath).FullName);
                if (!WriteFrames(r, framePrefix))
                    return Fail(r.label, "failed to write the per-angle frames at '" + framePrefix + "_<angle>.png' (missing directory or locked path?)");
            }
            string proxyInfo = (r.proxiesKept + r.proxiesHidden) > 0
                ? " proxies=kept:" + r.proxiesKept + ",hidden:" + r.proxiesHidden : "";
            // cam=ok signals a diffable camera manifest was written beside the png — CaptureDiff's `against`.
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[RenderAvatar] Capture {0} angles={1} tiles={2} res={3} margin={4} gizmos={5} eyeHidden={6} hideExcluded={7}{8} => OK gate={9}{10} cam=ok{11} | png={12}",
                r.label, string.Join(",", r.manifest.angles), r.manifest.views.Length, r.manifest.tileRes,
                r.manifest.margin.ToString("0.##", CultureInfo.InvariantCulture), showGizmos ? "on" : "off",
                r.hiddenCount, r.excludedCount, proxyInfo, r.gate, r.canaryToken,
                r.proxyNote + r.horizonNote + r.settleNote + r.canaryNote + r.hideNote, png);
            Debug.Log(summary);
            return summary;
        }

        // ----- Hide list: resolution, refusal, empty-entry note ---------------------------------

        /// <summary>Resolve each caller entry as a descendant of <paramref name="root"/>. DefaultHide is added by
        /// the caller afterwards, since a missing `Culling` mesh is the normal case rather than a caller error.</summary>
        internal static void ResolveHideList(GameObject root, string[] hide,
            out List<GameObject> targets, out List<string> unresolved,
            out List<KeyValuePair<string, GameObject>> callerEntries)
        {
            targets = new List<GameObject>();
            unresolved = new List<string>();
            callerEntries = new List<KeyValuePair<string, GameObject>>();
            if (hide == null) return;
            foreach (var h in hide)
            {
                if (string.IsNullOrEmpty(h)) continue;
                var entry = h.Trim();
                var go = ResolveDescendant(root, entry);
                if (go == null) { unresolved.Add(entry); continue; }
                callerEntries.Add(new KeyValuePair<string, GameObject>(entry, go));
                if (!targets.Contains(go)) targets.Add(go);
            }
        }

        /// <summary>The refusal an unresolved entry earns. A pinned diff replays frame A's list rather than one the
        /// caller typed, so it gets its own text.</summary>
        internal static string HideRefusal(bool pinned, string label, List<string> unresolved)
        {
            string named = "'" + string.Join("', '", unresolved.ToArray()) + "'";
            return pinned
                ? "frame A's hide list named " + named + ", which does not resolve under '" + label
                  + "' at B. If the edit under test DELETED it, the pair is still comparable — re-run with that "
                  + "entry dropped from `hide`. If it was renamed or moved, B will draw what A excluded: pass its "
                  + "current path. The manifest stores names, not identities, so this door cannot tell the two apart"
                : "hide entry " + named + " resolved to nothing under '" + label
                  + "' — the subtree would render unexcluded and the sheet would read as if it had been hidden. "
                  + "Entries resolve as descendants of the target: name each as a path relative to it, or as an exact child name";
        }

        /// <summary>Names each resolved entry that excludes no drawable renderer.</summary>
        internal static string HideEmptyNote(List<KeyValuePair<string, GameObject>> callerEntries)
        {
            var empty = new List<string>();
            foreach (var kv in callerEntries)
            {
                bool anyDrawable = false;
                foreach (var rend in kv.Value.GetComponentsInChildren<Renderer>(true))
                    if (rend.enabled && rend.gameObject.activeInHierarchy) { anyDrawable = true; break; }
                if (!anyDrawable) empty.Add(kv.Key);
            }
            return empty.Count == 0 ? "" : " hideEmpty=[" + string.Join(",", empty.ToArray()) + "]";
        }

        // ----- Shared capture types + core ------------------------------------------------------
        // The per-grab camera manifest, written as <png>.cam.json beside every grab: the exact per-angle
        // framing + window geometry to reproduce THIS grab, so CaptureDiff frames B identically to A.
        [Serializable] private class CamFrame { public int w, h, camW, camH; public float ppp; }
        [Serializable] private class CamView { public string angle; public Vector3 pivot; public Quaternion rot; public float orthoSize; public int cropX, cropY, side; }
        [Serializable] private class CamManifest
        {
            public int schema; public string label;
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
            public string proxyNote = "", horizonNote = "", settleNote = "", gate = "", canaryToken = "", canaryNote = "", hideNote = "";
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
            var handle = SceneHandle.Resolve(target);
            if (!handle.Ok) return CoreFail(target, handle.Refusal);
            var root = handle.Object;
            string label = root.name;

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

            // ----- Pending-reload guard (edit mode; see the handles block) ------------------------
            if (!Application.isPlaying && IsScriptReloadPending(out bool reloadLocked))
                return CoreFailT(label, BuildReloadPendingFailReason(reloadLocked));

            // ----- Settle gate (reactive targets only) ----------------------------------------
            // An unsettled NDMF preview means the frame about to be grabbed shows a stale or bodiless avatar,
            // so FAIL, after kicking the editor to the OS foreground so the rebuild's ticks fire between this
            // call and the re-grab. Waiting in-call is impossible: no editor tick runs while this synchronous
            // call blocks the main thread. SweepNdmfChangeHorizon runs first, so an edit NDMF hasn't noticed
            // lands as a real invalidation the probe can see.
            string horizonNote = "";
            string armedBy = null;
            var scopeState = ResolveArmScope(root, out GameObject armScope);
            if (scopeState == ArmScope.Drift && NdmfInstalled)
                return CoreFail(label, ArmScopeResolverDriftFailReason);
            bool reactive = !Application.isPlaying && scopeState == ArmScope.Found && HasReactiveMA(armScope, out armedBy);
            if (reactive)
            {
                horizonNote = SweepNdmfChangeHorizon();
                if (horizonNote == HorizonDriftNote)
                    return CoreFail(label, "change-horizon sweep unavailable (NDMF internals drifted; the "
                        + "scripted-edit blind-spot scan never ran, so freshness can't be certified) — re-pin "
                        + "the sweep handles | armed-by=" + armedBy);
                // Transient: the sweep's task keeps advancing on editor ticks between calls, so the re-grab
                // re-sweeps against a mostly-warm scan.
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
            var settle = ProbeSettle(reactive, out string pipeline);
            if (settle == Settle.Unsettled)
            {
                bool kicked = TryFocusKick(out string kick);
                return CoreFailT(label, "preview not settled (NDMF rebuild in flight; " + pipeline + ") — "
                    + (kicked
                        ? "focus kick sent (" + kick + "), the rebuild can advance now: re-grab in a separate call"
                        : "focus kick failed (" + kick + "): focus the Unity Editor window, then re-grab")
                    + " | armed-by=" + armedBy);
            }
            if (settle == Settle.Drift)
                return CoreFail(label, "settle-state unknown (NDMF preview internals drifted; " + pipeline
                    + ") — freshness can't be certified; re-pin the settle handles | armed-by=" + armedBy);

            // ----- Resolve hide list (descendants of target) + default hides -----------------
            ResolveHideList(root, hide, out var hideTargets, out var hideUnresolved, out var hideCaller);
            var callerHideTargets = new List<GameObject>(hideTargets); // snapshot before DefaultHide joins
            if (hideUnresolved.Count > 0) return CoreFail(label, HideRefusal(pin, label, hideUnresolved));
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
            bool oAsyncShaderCompile = EditorSettings.asyncShaderCompilation;

            var rts = new List<RenderTexture>();
            // SMRs whose forceMatrixRecalculationPerRender this grab flips (see the re-bake block below);
            // restored in finally.
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

                // Preview-scene proxies are exempt from the root hide above and attributed per proxy here.
                var keptProxies = new List<Renderer>();
                string proxyNote = IsolateNdmfProxies(root, hideTargets, cascadeHides, svm, keptProxies,
                    out int proxiesKept, out int proxiesHidden, out int proxiesDiscovered, out int proxiesAttributed);
                if (IsProxyAttributionDrift(proxyNote))
                    return CoreFail(label, ProxyDriftFailReason);
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
                    if (inHideList)
                    {
                        if (IsUnderAny(rend.transform, callerHideTargets)) excludedCount++;
                        continue; // dropped by the hide list (SVM-hidden)
                    }
                    if (svm.IsHidden(rend.gameObject, false)) { hiddenCount++; continue; }
                    drawable.Add(rend);
                }
                if (drawable.Count == 0)
                    return CoreFail(label, "no drawable renderers after exclusion (eyeHidden=" + hiddenCount + " hideExcluded=" + excludedCount + ")");

                string hideNote = HideEmptyNote(hideCaller);

                // ----- Freshness: force a synchronous skin re-bake for the capture --------------------------
                // Two freshness layers, and the settle gate only governs one. The NDMF pipeline keeps proxy
                // CONTENT current (weights, reactive state); proxy SKIN BAKING is a plain-SMR concern the
                // pipeline never touches. A backgrounded editor freezes skin baking to the parked editor tick,
                // so proxies render pre-edit geometry while their weights read current. On a reactive target
                // the drawn renderers ARE the proxies (originals suppressed), so the force flag must land on
                // the kept proxies, not just `drawable`.
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

                // ----- Shading: compile shader variants synchronously for the capture ---------
                // Unity draws a flat #00FFFF placeholder for any shader variant still compiling, and the
                // capture's own RepaintImmediately is what QUEUES that compile — so nothing probed BEFORE a
                // grab can see it, and every freshness layer above certifies the sheet regardless (the canary
                // moves a placeholder's pixels as happily as a real surface). The corruption is partial as
                // often as total. The headlight/no-skybox config just above is why this path hits
                // cold variants at all: it requests a keyword set the operator's own viewport never draws.
                // This MUST also cover the canary's grabs below: with async compilation live across the
                // baseline/nudge pair, a variant finishing between those two frames moves pixels wholesale and
                // would certify canary=live off shader completion rather than off deform.
                // ShaderUtil.allowAsyncCompilation is NOT the lever — this path does not consult it. A bespoke
                // Camera.Render() is the inverse: it ignores this pref, so a scratch-camera path pins that flag
                // instead (RenderThumbnailCore.Capture).
                EditorSettings.asyncShaderCompilation = false;

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

                // ----- Shading: scan the served tiles (see the scan block above) ---------------
                // Before the canary, whose further grabs a poisoned sheet would only waste. Angles come from
                // views[], the authority for what was drawn: the pinned path renders frame A's angles.
                {
                    int placeholderPx = 0, worstN = 0; string worstAngle = null;
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        int ph = CountPlaceholderPx(tiles[i]);   // not `n`: that is the angle count here
                        placeholderPx += ph;
                        if (ph > worstN) { worstN = ph; worstAngle = views[i].angle; }
                    }
                    if (placeholderPx > 0)
                        return CoreFail(label, BuildPlaceholderFailReason(
                            "the served tiles", placeholderPx, worstAngle, worstN, ShaderUtil.anythingCompiling));
                }

                // ----- End-to-end freshness canary (model: the canary block above) ------------------------
                // Post-tiles so the served frames are never polluted; a FAIL discards everything. The
                // compare pair is a dedicated unnudged baseline + the nudged grab, both taken with the
                // IDENTICAL draw state (gizmos off, selection cleared) — never tiles[0], whose gizmos/
                // selection under showGizmos:true would diff against a gizmo-less canary frame and
                // certify canary=live on a stone-dead chain. Only blendshape nudges are sound probes: a
                // transform translate moves an SMR's bones only when they happen to live under the target,
                // and can move pixels by re-drawing a parked skin buffer under a new object matrix — so with
                // no eligible shape the canary declares itself UNAVAILABLE in-band and never FAILs.
                string canaryToken = "", canaryNote = "";
                if (ShouldRunCanary(Application.isPlaying, forcedRebake.Count > 0))
                {
                    var cands = new List<(SkinnedMeshRenderer smr, int idx, float worldDelta)>();
                    foreach (var rend in drawable)
                        if (rend is SkinnedMeshRenderer dsmr && dsmr.sharedMesh != null)
                        {
                            var ls = dsmr.transform.lossyScale;
                            float axis = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                            foreach (var c in CanaryShapeCandidates(dsmr.sharedMesh))
                                cands.Add((dsmr, c.index, c.delta * axis));
                        }
                    cands.Sort((a, b) => b.worldDelta.CompareTo(a.worldDelta));

                    if (cands.Count == 0)
                        canaryNote = CanaryUnavailableNote;
                    else
                    {
                        var v0 = views[0];
                        sv.drawGizmos = false;
                        Selection.objects = Array.Empty<UnityEngine.Object>();
                        sv.LookAt(v0.pivot, v0.rot, v0.orthoSize, true, true);
                        var baseF = cap.Grab(2, rts);
                        // v0.orthoSize is sv.size units, not camera ortho (hence the k-correction in the
                        // framing pass), so read the camera the baseline actually rendered through.
                        float wpp = baseF.camH > 0 ? 2f * baseF.cam.orthographicSize / baseF.camH : 0f;
                        var baseTile = Downscale(baseF.px, baseF.w, v0.cropX, v0.cropY, v0.side, tileRes);
                        // Scanned before any scene write, so this arm can return without owing a restore.
                        int basePh = CountPlaceholderPx(baseTile);
                        if (basePh > 0)
                            return CoreFail(label, BuildPlaceholderFailReason(
                                "the freshness canary's baseline frame", basePh, v0.angle, basePh,
                                ShaderUtil.anythingCompiling));

                        var nudgeScene = root.scene;
                        bool wasDirty = nudgeScene.isDirty;
                        var tried = new List<string>();
                        bool live = false; int lastChanged = 0; int nudgePh = 0;
                        foreach (var cand in cands)
                        {
                            if (tried.Count >= CanaryMaxTries) break;
                            if (CanaryExpectedTilePx(cand.worldDelta, wpp, v0.side, tileRes) < CanaryMinExpectedPx)
                                continue; // too small to clear the floor at this framing even when healthy
                            float priorW = cand.smr.GetBlendShapeWeight(cand.idx);
                            Frame nf;
                            try
                            {
                                cand.smr.SetBlendShapeWeight(cand.idx, priorW > 50f ? 0f : 100f);
                                sv.LookAt(v0.pivot, v0.rot, v0.orthoSize, true, true);
                                nf = cap.Grab(2, rts);
                            }
                            finally { cand.smr.SetBlendShapeWeight(cand.idx, priorW); }
                            tried.Add("blendshape '" + cand.smr.sharedMesh.GetBlendShapeName(cand.idx)
                                + "' on '" + cand.smr.name + "'");
                            var nudgeTile = Downscale(nf.px, nf.w, v0.cropX, v0.cropY, v0.side, tileRes);
                            // Accumulated, not returned on: this arm HAS written scene data, so the
                            // dirtiness restore below must run before any FAIL leaves.
                            nudgePh = Mathf.Max(nudgePh, CountPlaceholderPx(nudgeTile));
                            RenderDiff.Compare(baseTile, nudgeTile, tileRes, tileRes, out lastChanged, out _);
                            if (CanaryAlive(lastChanged)) { live = true; break; }
                        }
                        if (!wasDirty && nudgeScene.isDirty)
                        {
                            bool cleared = false;
                            if (MiClearSceneDirtiness != null)
                                try { MiClearSceneDirtiness.Invoke(null, new object[] { nudgeScene }); cleared = !nudgeScene.isDirty; }
                                catch { }
                            if (!cleared) canaryNote += CanaryDirtyNote;
                        }
                        // After the dirtiness restore, before any verdict: a poisoned nudge frame makes
                        // both the live and the dead reading meaningless, so it outranks either.
                        if (nudgePh > 0)
                            // canaryNote rides along: this is the one placeholder return that fires AFTER a
                            // write to the operator's scene, so a failed dirtiness restore must not die here.
                            return CoreFail(label, BuildPlaceholderFailReason(
                                "the freshness canary's nudge frame", nudgePh, v0.angle, nudgePh,
                                ShaderUtil.anythingCompiling) + canaryNote);
                        if (tried.Count == 0)
                            canaryNote = CanaryUnavailableNote + canaryNote; // shapes exist but none visible at this framing
                        else if (live)
                            canaryToken = CanaryLiveToken;
                        else
                        {
                            bool kicked = TryFocusKick(out string kick);
                            return CoreFailT(label, BuildCanaryFailReason(string.Join("; ", tried), lastChanged, kicked, kick));
                        }
                    }
                }

                // ----- Contact sheet + manifest ----------------------------------------------
                var sheet = Compose(tiles, tileRes, cols, rows, out int sheetW, out int sheetH);
                var manifest = new CamManifest
                {
                    schema = ManifestSchema, label = label,
                    angles = resolvedAngles, hide = hide ?? Array.Empty<string>(), showGizmos = showGizmos,
                    cols = cols, rows = rows, tileRes = tileRes, resolution = resolution, margin = margin,
                    frame = frame, views = views
                };
                return new CoreResult
                {
                    ok = true, sheet = sheet, sheetW = sheetW, sheetH = sheetH, manifest = manifest, label = label,
                    hiddenCount = hiddenCount, excludedCount = excludedCount, hideNote = hideNote,
                    proxiesKept = proxiesKept, proxiesHidden = proxiesHidden,
                    proxyNote = proxyNote, horizonNote = horizonNote, settleNote = SettleNote(reactive),
                    gate = GateToken(reactive, settle), canaryToken = canaryToken, canaryNote = canaryNote,
                };
            }
            catch (Exception e)
            {
                return CoreFail(label, "capture failed: " + e.Message);
            }
            finally
            {
                // The async-compile pref goes FIRST: EditorSettings is a serialized asset, so a leaked `false`
                // persists into ProjectSettings/EditorSettings.asset at the next save and makes the whole
                // project compile synchronously forever, presenting as a hung editor on any new material.
                // Everything else in this finally can throw, and any such throw would skip a later restore.
                EditorSettings.asyncShaderCompilation = oAsyncShaderCompile;

                // Restore the freshness re-bake flags (null-guarded — SMRs can be NDMF-owned), before the
                // synchronous repaint below, so the frame left on the operator's screen bakes with original flags.
                foreach (var kv in forcedRebake)
                    if (kv.Key != null) kv.Key.forceMatrixRecalculationPerRender = kv.Value;

                // The CONTAINING root is skipped: it was never hidden, and cascading over it would re-show the
                // target subtree's own operator eye-hides.
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

        // Encode the composed sheet to a timestamped PNG in temporaryCachePath, write its <png>.cam.json sidecar,
        // and prune old grabs. Returns the PNG path, or NULL on an IO failure, which callers turn into a FAIL.
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
            var handle = SceneHandle.Resolve(target);
            var root = handle.Object;
            string label = root != null ? root.name : target;
            if (string.IsNullOrEmpty(against))
                return Fail(label, "against is empty — pass a prior grab's png path (its png= trailer)");
            // Target before manifest: re-grabbing frame A cannot fix a target that does not resolve.
            if (root == null)
                return Fail(label, handle.Refusal + " CaptureDiff diffs a LIVE scene target, grabbed now as frame B, "
                    + "against a prior grab's PNG as frame A, so the target has to be present in the active scene");
            string camPath = against + ".cam.json";
            if (!File.Exists(camPath))
                return Fail(label, "no camera manifest for " + against + " (expected " + Path.GetFileName(camPath)
                    + " — absent or pruned). Re-grab frame A with Run, then diff.");
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

            // Decode frame A BEFORE grabbing B: the grab costs a shader-compile freeze, the canary's extra
            // grabs and a blendshape write to the operator's scene, all wasted on a defect visible up front.
            var aTex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            Color32[] aSheet; int aW, aH;
            try
            {
                if (!ImageConversion.LoadImage(aTex, File.ReadAllBytes(against)))
                    return Fail(label, "frame A png failed to decode: " + against);
                aW = aTex.width; aH = aTex.height;
                aSheet = aTex.GetPixels32();
            }
            finally { UnityEngine.Object.DestroyImmediate(aTex); }

            // Frame A carries the shading scan too, since B is scanned inside CaptureCore.
            {
                int aPh = 0, aWorstN = 0; string aWorstAngle = null;
                for (int i = 0; i < A.views.Length; i++)
                {
                    int c = i % A.cols, rr = i / A.cols;
                    int x0 = c * A.tileRes, y0 = (A.rows - 1 - rr) * A.tileRes;
                    int ph = CountPlaceholderPx(ExtractTile(aSheet, aW, x0, y0, A.tileRes));
                    aPh += ph;
                    if (ph > aWorstN) { aWorstN = ph; aWorstAngle = A.views[i].angle; }
                }
                if (aPh > 0)
                    return Fail(label, BuildFrameAPlaceholderFailReason(
                        Path.GetFileName(against), aPh, aWorstAngle, aWorstN));
            }

            var r = CaptureCore(target, A.angles, A.hide, A.margin, A.showGizmos, A.resolution, new CoreOpts { pinned = A });
            if (!r.ok) return r.fail;

            if (aW != r.sheetW || aH != r.sheetH)
                return Fail(label, "frame A sheet size " + aW + "x" + aH + " != B "
                    + r.sheetW + "x" + r.sheetH + " — re-grab the pair at the same window/resolution");

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
            // Carry B's settle notes: an unsettled B undercuts "empty diff ⇒ immaterial", so the caveat must
            // ride the diff summary too.
            // The sheet is bottom-origin and RenderDiff reports raw buffer coords, so bbox y grows UPWARD from
            // the bottom edge. Stated once, as its own token rather than inside `diff=[…]`, which is strictly
            // one entry per angle and is parsed by position.
            string originNote = identical == r.manifest.views.Length ? "" : " bboxOrigin=bottom";
            string summary = "[RenderAvatar] CaptureDiff " + label + " against=" + Path.GetFileName(against)
                + " angles=" + string.Join(",", r.manifest.angles) + " => OK gate=" + r.gate + r.canaryToken + " diff=[" + string.Join("; ", parts) + "] identical="
                + identical + "/" + r.manifest.views.Length + originNote + r.hideNote + r.proxyNote + r.horizonNote + r.settleNote + r.canaryNote + " | png=" + pngB;
            Debug.Log(summary);
            return summary;
        }

        internal static string ResolveFramePrefix(string prefix, string projectRoot)
        {
            if (Path.IsPathRooted(prefix)) return prefix;
            string p = prefix.Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            if (!p.StartsWith("test-output/", StringComparison.Ordinal)) p = "test-output/" + p;
            string full = Path.GetFullPath(Path.Combine(projectRoot, p));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            return full;
        }

        // One PNG per angle, cut back out of the composed sheet in Compose's row-major layout.
        private static bool WriteFrames(CoreResult r, string prefix)
        {
            var m = r.manifest;
            var tex = new Texture2D(m.tileRes, m.tileRes, TextureFormat.RGBA32, false, false);
            try
            {
                for (int i = 0; i < m.angles.Length; i++)
                {
                    tex.SetPixels32(ExtractTile(r.sheet, r.sheetW, i % m.cols * m.tileRes, (m.rows - 1 - i / m.cols) * m.tileRes, m.tileRes)); tex.Apply();
                    File.WriteAllBytes(prefix + "_" + m.angles[i] + ".png", tex.EncodeToPNG());
                }
                return true;
            }
            catch (Exception e) { Debug.LogWarning("[RenderAvatar] frame write failed: " + e.Message); return false; }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
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

        // Self hidden-state of every root in every loaded scene EXCEPT the NDMF preview scene (see
        // IsolateNdmfProxies). Restore cascades each root back to this self-state, so a per-object partial
        // hide under a non-target root is not preserved.
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

        private static bool IsNdmfPreviewScene(Scene s)
        {
            if (MiIsPreviewScene != null)
            {
                try { return (bool)MiIsPreviewScene.Invoke(null, new object[] { s }); }
                catch { /* fall through to the name check */ }
            }
            return s.name == "___NDMF Preview___";
        }

        // Proxies live in NDMF's hidden preview scene, where blanket-hiding them would delete every
        // reactive-targeted renderer (typically the body) from the grab, because the preview hook still
        // suppresses their originals. So hide only each proxy whose original is NOT drawable under the target
        // (foreign, stale or unattributable proxies, and proxies of hide-listed, eye-hidden or inactive
        // originals), recording prior state in cascadeHides for the shared restore.
        // Attribution is per proxy-RENDERER GameObject — NDMF's proxy map is keyed on the renderer's GO,
        // and only SkinnedMeshRenderer proxies are preview-scene roots; a MeshRenderer proxy lives UNDER a
        // shadow-bone tree, so attributing scene roots would null-attribute the bone root and hide the
        // target's own non-skinned props with it. Hides are self-only for the same reason (bones carry no
        // renderers; nothing else must cascade). Attribution drift (API handle unresolved, lookup throw, or
        // a non-GameObject return) stops here and returns ProxyDriftNote, which CaptureCore FAILs on.
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

        // Member-handle resolution that NEVER throws, so a static field initializer can never raise
        // TypeInitializationException: a null declaring type, a missing member or a throwing lookup all yield null.
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

        // Same canary contract for the avatar-root resolver handle (ResolveArmScope → gate arming). No name
        // fallback exists, so the EditMode canary must red-fail, never Ignore-when-unresolved. The ReturnType
        // check is load-bearing: GetMethod resolves by name+flags alone, so a return-type-only drift would keep
        // a bare != null green while `raw is Transform` silently fails at runtime.
        internal static bool AvatarRootResolverHandleResolved =>
            MiFindAvatarInParents != null && MiFindAvatarInParents.ReturnType == typeof(Transform);

        internal const string HorizonIncompleteNote = "__horizon-sweep-incomplete__";

        // Closes the settle probe's one blind spot. A scripted edit (reflection write + SetDirty, the normal
        // execute_code mutation path) publishes only a ChangeScene event, which NDMF's ChangeStreamMonitor
        // deliberately ignores, and PropertyMonitor's poll loop, the fallback for unreported changes, parks
        // while the editor is unfocused (the norm in agent sessions). Until one of them runs, the pipeline reads
        // settled while its proxies render pre-edit geometry.
        // So this runs PropertyMonitor.CheckAllObjects inside the NDMF sync context, then pumps that context
        // (Turn + FlushInvalidates) so a pending edit lands as a pipeline invalidation BEFORE ProbeSettle reads
        // the gate. Exits the moment the probe stops reading Settled (the gate FAILs right after), or ~50ms after
        // the sweep completes with nothing found (grace for the invalidation's thread-pool continuation to
        // land). A deadline exit with the sweep still running returns HorizonIncompleteNote, and the caller FAILs
        // transiently. Returns "", the drift note, or that sentinel; never throws.
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

        // Extracted pure so the headless suite can pin them: batchmode has no preview scene or session, so the
        // full CaptureCore paths are live-gate-only (see Tests/Editor/RenderAvatarFreshnessGate.md). The
        // attribution guard is keyed on discovery/attribution totals, NOT on kept-count: kept==0 is legitimate
        // for a plain leaf target under a reactive avatar (its neighbor's proxies are foreign and hidden, not
        // lost), and discovered==0 is an at-rest avatar. Only discovered>0 with every one attributing null while
        // settled is the silent body-drop.
        internal static bool IsProxyAttributionDrift(string proxyNote) => proxyNote == ProxyDriftNote;
        internal static bool IsAttributionAllNull(Settle settle, int discovered, int attributedNonNull)
            => settle == Settle.Settled && discovered > 0 && attributedNonNull == 0;

        internal static string GateToken(bool reactive, Settle settle)
            => reactive && settle == Settle.Settled ? "armed" : "exempt";

        // The one settle probe (read-only). `reactiveEditMode` is the caller's precomputed !isPlaying &&
        // HasReactiveMA hierarchy scan, because the sweep calls this per pump iteration. Unsettled includes a
        // runtime hop that is legitimately null (no pipeline yet, between builds); Drift means reflection itself
        // failed. `pipeline` names the exact hop for the gate's fail-loud message.
        private static Settle ProbeSettle(bool reactiveEditMode, out string pipeline)
        {
            pipeline = "";
            try
            {
                if (!reactiveEditMode) return Settle.Exempt;       // play mode or no reactive fit → nothing to settle
                if (PiCurrent == null || FiProxySession == null || FiActive == null
                    || FiNext == null || PiIsReady == null || PiIsInvalidated == null)
                { pipeline = "NDMF internals drifted"; return Settle.Drift; }
                // Current is null EXACTLY when previews are globally disabled (NDMFPreview.cs); originals then
                // render un-suppressed, so the sheet is trustworthy: Exempt, never a FAIL.
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
        // Foregrounds the editor's main window so frames fire again between MCP calls: a background-throttled
        // editor stops painting frames, which stops the ticks that advance the NDMF rebuild. Windows refuses
        // SetForegroundWindow from a background process unless it recently sent input, so on a refusal a
        // synthetic Alt tap (down+up) releases the foreground lock, the accepted user32 workaround.
        private const int SW_RESTORE = 9;
        private const byte VK_MENU = 0x12;
        private const uint KEYEVENTF_KEYUP = 0x2;
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // Escape hatch: a measurement session that must hold the editor backgrounded sets this pref true; the
        // FAIL message then degrades to the manual instruction.
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
                    // Cost: the tap lands on whatever app IS foreground and can toggle its menu-accelerator
                    // mode, so it is taken only on this already-refused path, key-up guaranteed.
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
        // deliberately do NOT match — they carry no preview-resolution risk.
        //
        // Why hand-curated, and what ReactiveMarkersTests can and cannot catch: Tests/Editor/ReactiveMarkers.md.
        private static readonly string[] ReactiveMarkers =
        {
            "ShapeChanger", "BlendshapeSync", "MaterialSetter", "MaterialSwap", "ObjectToggle",
            "MeshCutter", "RemoveVertexColor", "ScaleAdjuster",
        };

        // The gate arms on ANY match, inactive included: MA analyzes inactive reactives, so an at-rest inactive
        // one can still drive a proxy.
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

        // Package registration, NOT a reflected NDMF type, gates whether a resolver Drift FAILs loud: a reflected
        // sentinel could itself drift in the same NDMF release that renamed the resolver, silently reopening
        // the OK-stale hole.
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

        // NoAvatarRoot is a real null return (a plain prop, where MA proxies can't exist) and earns Settle.Exempt.
        // Drift is an unusable handle (null or wrong return type) or a non-Transform result, and CaptureCore
        // FAILs on it under installed NDMF so every avatar can't silently render ungated.
        internal static ArmScope ClassifyArmScope(bool handleUsable, object rawResult)
            => !handleUsable ? ArmScope.Drift
             : rawResult == null ? ArmScope.NoAvatarRoot
             : rawResult is Transform ? ArmScope.Found
             : ArmScope.Drift;

        // The OUTERMOST avatar root at/above the target, via reflected NDMF RuntimeUtil.FindAvatarInParents, so
        // a nested descriptor can't scope the gate too narrowly and a leaf-mesh target resolves up to its whole
        // avatar. A throw is classified Drift, not swallowed to a false NoAvatarRoot.
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
        // failures (bad input, API drift, degenerate grab) log at Error.
        internal static string Fail(string label, string reason)
        {
            string msg = "[RenderAvatar] " + (string.IsNullOrEmpty(label) ? "?" : label) + " => FAIL: " + reason;
            Debug.LogError(msg);
            return msg;
        }

        // A retryable FAIL with the same "=> FAIL:" contract string as Fail, logged at Warning: an unsettled
        // preview is an expected retry condition, so it doesn't pollute a console-clean gate.
        internal static string FailTransient(string label, string reason)
        {
            string msg = "[RenderAvatar] " + (string.IsNullOrEmpty(label) ? "?" : label) + " => FAIL: " + reason;
            Debug.LogWarning(msg);
            return msg;
        }
    }
}
