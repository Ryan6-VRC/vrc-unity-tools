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
    /// <b>Isolation is proxy-aware.</b> NDMF parks its preview proxies as roots of the hidden
    /// <c>___NDMF Preview___</c> scene; blanket-hiding "every other root" would hide them while the
    /// preview hook still suppresses their originals — deleting every reactive-targeted renderer (the
    /// body, typically) from the grab. So the preview scene is exempt from root isolation; instead each
    /// proxy is attributed to its original via the public <c>NDMFPreview.GetOriginalObjectForProxy</c>
    /// and hidden unless that original is drawable under the target (foreign avatars' proxies, stale or
    /// unattributable proxies, and proxies of hide-listed / eye-hidden / inactive originals all hide).
    /// The summary reports <c>proxies=kept:N,hidden:M</c> whenever proxies exist; if the attribution
    /// API drifts, proxies are left visible and the note says so — a visible foreign-geometry leak the
    /// reader can see, never a silent body-drop it can't.
    ///
    /// <b>Freshness — settle-gated.</b> The preview rebuilds asynchronously, advanced only by editor
    /// ticks that fire after a synchronous call returns — so a same-call edit+grab would capture the
    /// pre-edit proxy, and a background-throttled editor stops ticking and wedges the rebuild
    /// indefinitely. Rather than return an untrustworthy sheet, an unsettled pipeline on a reactive
    /// target FAILS the grab — after kicking the editor's main window to the OS foreground (a synthetic
    /// Alt tap first releases the Windows foreground lock) so real frames fire between this call and the
    /// re-grab. Protocol stays <b>edit and grab in separate calls</b>; on the settle FAIL, just re-grab.
    /// In-call waiting is impossible by construction — no editor tick runs while the synchronous call
    /// blocks the main thread. Mechanism →
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

        // ----- NDMF preview-scene proxy attribution (reflection, same drift rules) ---------------
        // Both hooks are PUBLIC NDMF API (NDMFPreviewSceneManager.IsPreviewScene, changelog-tracked
        // NDMFPreview.GetOriginalObjectForProxy) — reflected only because this asmdef has references:[].
        // A drift leaves the handles null → proxies stay visible + an in-band note (see class doc).
        private const string ProxyDriftNote =
            " | note=NDMF proxy attribution drifted — preview proxies left visible (possible foreign-geometry leak)";
        private static readonly Type PreviewSceneManagerType =
            ResolveNdmfType("nadena.dev.ndmf.preview.NDMFPreviewSceneManager");
        private static readonly MethodInfo MiIsPreviewScene = SafeGetMethod(PreviewSceneManagerType, "IsPreviewScene",
            BindingFlags.Static | BindingFlags.Public);
        private static readonly Type NdmfPreviewType = ResolveNdmfType("nadena.dev.ndmf.preview.NDMFPreview");
        private static readonly MethodInfo MiGetOriginalForProxy = SafeGetMethod(NdmfPreviewType, "GetOriginalObjectForProxy",
            BindingFlags.Static | BindingFlags.Public);

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
            resolution = Mathf.Clamp(resolution, MinResolution, MaxResolution);
            margin = Mathf.Clamp(margin, 0f, 0.8f);

            // ----- Resolve target ------------------------------------------------------------
            var root = Resolve(target);
            if (root == null)
                return Fail(target, "target not found — tried hierarchy path, instance id, then name in the active scene");
            string label = root.name;

            if (PrefabStageUtility.GetPrefabStage(root) != null)
                return Fail(label, "target is in prefab isolation — grab from the scene");

            var sv = SceneView.lastActiveSceneView;
            if (sv == null)
                return Fail(label, "no SceneView window open — open a Scene View first (can't grab a viewport that doesn't exist)");

            // ----- Resolve + validate angles -------------------------------------------------
            if (angles == null || angles.Length == 0) angles = new[] { "front", "back" };
            var resolvedAngles = new string[angles.Length];
            for (int i = 0; i < angles.Length; i++)
            {
                string a = (angles[i] ?? "").Trim().ToLowerInvariant();
                if (Array.IndexOf(Vocabulary, a) < 0)
                    return Fail(label, "unknown angle '" + angles[i] + "' — valid: " + string.Join(",", Vocabulary));
                resolvedAngles[i] = a;
            }

            // ----- Settle gate (reactive targets only) ----------------------------------------
            // An unsettled NDMF preview means the frame about to be grabbed shows a stale or
            // bodiless avatar — never return that sheet. FAIL instead, after kicking the editor
            // to the OS foreground so the rebuild's ticks fire between this call and the re-grab
            // (a background-throttled editor wedges the rebuild indefinitely; waiting in-call is
            // impossible — no editor tick runs while this synchronous call blocks the main thread).
            if (ProbeSettle(root, out string pipeline) == Settle.Unsettled)
            {
                bool kicked = TryFocusKick(out string kick);
                return Fail(label, "preview not settled (NDMF rebuild in flight; " + pipeline + ") — "
                    + (kicked
                        ? "focus kick sent (" + kick + "), the rebuild can advance now: re-grab in a separate call"
                        : "focus kick failed (" + kick + "): focus the Unity Editor window, then re-grab"));
            }

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
            if (host == null) return Fail(label, "SceneView host GUIView (m_Parent) not resolvable by reflection — Unity API drift");
            var hostType = host.GetType();
            var miRepaint = hostType.GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var miGrab = hostType.GetMethod("GrabPixels", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var piPos = hostType.GetProperty("position");
            if (miRepaint == null || miGrab == null || piPos == null)
                return Fail(label, "GUIView RepaintImmediately/GrabPixels/position not resolvable by reflection — Unity API drift");
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
            var scratch = new List<Texture2D>();

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
                string proxyNote = IsolateNdmfProxies(root, hideTargets, cascadeHides, svm,
                    out int proxiesKept, out int proxiesHidden);

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
                    return Fail(label, "no drawable renderers after exclusion (hidden=" + hiddenCount + " excluded=" + excludedCount + ")");

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
                int n = resolvedAngles.Length;
                int cols = Mathf.CeilToInt(Mathf.Sqrt(n));
                int rows = Mathf.CeilToInt((float)n / cols);
                int tileRes = Mathf.Max(MinTileRes, Mathf.Min(resolution, SheetEdgeCap / Mathf.Max(cols, rows)));

                var tiles = new List<Color32[]>(n);
                foreach (var angle in resolvedAngles)
                {
                    Basis(angle, out Vector3 fwd, out Vector3 upv);
                    var rot = Quaternion.LookRotation(fwd, upv);

                    // (a) generous shot -> measure silhouette
                    sv.drawGizmos = false;
                    Selection.objects = Array.Empty<UnityEngine.Object>();
                    sv.LookAt(gcenter, rot, sphereRadius, true, true);
                    var g = cap.Grab(2, rts);
                    if (!Measure(g, out RectInt bbox, out int usableTop, out Color32 bg))
                        return Fail(label, "degenerate silhouette on '" + angle + "' — nothing drew (isolation dropped the body?)");

                    // (c) one exact orthographic correction
                    var cam = g.cam;
                    float ortho1 = cam.orthographicSize;
                    float k = ortho1 / Mathf.Max(1e-6f, sv.size);
                    float ccx = g.camW / 2f, ccy = g.camH / 2f;
                    float wpp1 = (2f * ortho1) / g.camH;
                    Vector3 right = cam.transform.right, up = cam.transform.up;

                    int usableW = g.w - 2 * Inset;
                    int usableH = usableTop + 1;
                    int side = Mathf.Min(usableW, usableH);
                    float fill = Mathf.Max(0.05f, 1f - margin);
                    float bcx = bbox.x + bbox.width * 0.5f, bcy = bbox.y + bbox.height * 0.5f;

                    float ortho2 = ortho1 * (Mathf.Max(bbox.width, bbox.height) / (fill * side));
                    float size2 = ortho2 / k;
                    float wpp2 = (2f * ortho2) / g.camH;
                    Vector3 avC = gcenter + right * ((bcx - ccx) * wpp1) + up * ((bcy - ccy) * wpp1);
                    float uCy = usableTop / 2f;
                    Vector3 pivot2 = avC - up * ((uCy - ccy) * wpp2);

                    // (d) final shot at the corrected frame (gizmos + root selection only here)
                    if (showGizmos)
                    {
                        sv.drawGizmos = true;
                        Selection.activeGameObject = root;
                        Selection.objects = new UnityEngine.Object[] { root };
                    }
                    sv.LookAt(pivot2, rot, size2, true, true);
                    var f = cap.Grab(showGizmos ? 3 : 2, rts);

                    // (e) post-normalize: center-crop the square, bilinear downscale to tileRes
                    int cropX = Mathf.RoundToInt(ccx - side / 2f);
                    int cropY = Mathf.RoundToInt(uCy - side / 2f);
                    cropX = Mathf.Clamp(cropX, 0, Mathf.Max(0, f.w - side));
                    cropY = Mathf.Clamp(cropY, 0, Mathf.Max(0, usableTop + 1 - side));
                    tiles.Add(Downscale(f.px, f.w, cropX, cropY, side, tileRes));
                }

                // ----- Contact sheet ---------------------------------------------------------
                var sheet = Compose(tiles, tileRes, cols, rows, out int sheetW, out int sheetH);
                var tex = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false, false);
                scratch.Add(tex);
                tex.SetPixels32(sheet);
                tex.Apply();
                var png = tex.EncodeToPNG();

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var path = Application.temporaryCachePath + "/renderavatar_" + RunLogFormat.Sanitize(label) + "_" + stamp + ".png";
                File.WriteAllBytes(path, png);

                // The temp grab dir is never swept by Unity or Windows, so it accumulates across every
                // session. Prune our own grabs older than 30 days here — the common write path — so it stays
                // bounded; the just-written grab is always newer than the cutoff. Best-effort per file (a
                // locked grab being Read elsewhere just survives to the next run).
                var cutoff = DateTime.Now.AddDays(-30);
                foreach (var old in Directory.GetFiles(Application.temporaryCachePath, "renderavatar_*.png"))
                {
                    try { if (File.GetLastWriteTime(old) < cutoff) File.Delete(old); }
                    catch { /* locked or already gone — leave it for a later run */ }
                }

                // Settle note (residual): the pre-grab gate FAILED any grab that STARTED unsettled, so
                // this firing means the pipeline invalidated DURING the call (this grab's own SVM churn
                // can). The grabbed frames still rendered the pre-call settled proxies — no tick ran
                // in-call to swap them — so the sheet stands; the note flags it for a cautious re-grab.
                string note = SettleNote(root);

                // The note sits BEFORE png= so the png= trailer is always terminal — a consumer reading
                // png= to end-of-line gets a clean path, never one with the note appended.
                string proxyInfo = (proxiesKept + proxiesHidden) > 0
                    ? " proxies=kept:" + proxiesKept + ",hidden:" + proxiesHidden : "";
                string summary = string.Format(CultureInfo.InvariantCulture,
                    "[RenderAvatar] {0} angles={1} tiles={2} res={3} margin={4} gizmos={5} hidden={6} excluded={7}{8} => OK{9}{10} | png={11}",
                    label, string.Join(",", resolvedAngles), n, tileRes, margin.ToString("0.##", CultureInfo.InvariantCulture),
                    showGizmos ? "on" : "off", hiddenCount, excludedCount, proxyInfo,
                    proxyNote, note, path);
                Debug.Log(summary);
                return summary;
            }
            catch (Exception e)
            {
                return Fail(label, "capture failed: " + e.Message);
            }
            finally
            {
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

                foreach (var t in scratch) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                foreach (var rt in rts) if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                // Force a synchronous repaint of the RESTORED view so the operator's Scene View shows its
                // normal state immediately — an async Repaint() alone leaves the last captured frame on
                // screen (looks "stale") until the next editor tick / a click in the viewport.
                try { miRepaint.Invoke(host, null); } catch { /* best-effort un-stale */ }
                sv.Repaint();
                RenderTexture.active = oRt; // restore LAST — the synchronous repaint above binds its own RT
            }
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

        // Hide every preview-scene proxy whose original is NOT drawable under the target; leave the
        // target's own proxies visible (recording prior state in cascadeHides for the shared restore).
        // Attribution drift (API handle unresolved or original lookup unavailable) hides NOTHING and
        // returns the drift note: a visible foreign-proxy leak beats a silent body-drop.
        private static string IsolateNdmfProxies(
            GameObject root, List<GameObject> hideTargets, Dictionary<GameObject, bool> cascadeHides,
            SceneVisibilityManager svm, out int kept, out int hidden)
        {
            kept = 0; hidden = 0;
            var proxies = new List<GameObject>();
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var s = EditorSceneManager.GetSceneAt(i);
                if (!s.isLoaded || !IsNdmfPreviewScene(s)) continue;
                foreach (var go in s.GetRootGameObjects())
                    if (go.GetComponentInChildren<Renderer>(true) != null) // skips the ndmf Activator root
                        proxies.Add(go);
            }
            if (proxies.Count == 0) return "";
            if (MiGetOriginalForProxy == null) return ProxyDriftNote;

            foreach (var proxy in proxies)
            {
                GameObject original;
                try { original = MiGetOriginalForProxy.Invoke(null, new object[] { proxy }) as GameObject; }
                catch { return ProxyDriftNote; } // partial hides already applied stay recorded in cascadeHides → restored
                bool drawable = original != null
                    && (original.transform == root.transform || original.transform.IsChildOf(root.transform))
                    && original.activeInHierarchy
                    && !IsUnderAny(original.transform, hideTargets)
                    && !svm.IsHidden(original, false);
                if (drawable) { kept++; continue; }
                hidden++;
                if (!cascadeHides.ContainsKey(proxy))
                {
                    cascadeHides[proxy] = svm.IsHidden(proxy, false);
                    svm.Hide(proxy, true);
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

        private enum Settle { Exempt, Settled, Unsettled, Drift }

        // The one settle probe (read-only), shared by the pre-grab gate and the residual note.
        // Exempt when there is no settle risk (play mode / non-reactive target); Unsettled when a rebuild
        // is in flight (predicate false, or any runtime hop legitimately null — no pipeline yet / between
        // builds / absent fit); Drift when reflection itself failed (a handle didn't resolve, or a read
        // threw). `pipeline` names the exact hop for the gate's fail-loud message. The whole body is one
        // try/catch and it ALWAYS returns, so an exception can never escape to Capture's outer catch.
        private static Settle ProbeSettle(GameObject root, out string pipeline)
        {
            pipeline = "";
            try
            {
                if (Application.isPlaying) return Settle.Exempt;   // play renders the driven runtime — always fresh
                if (!HasReactiveMA(root)) return Settle.Exempt;    // no reactive fit → nothing to settle, no reflection
                // Drift: a reflection handle failed to resolve at class load.
                if (PiCurrent == null || FiProxySession == null || FiActive == null
                    || FiNext == null || PiIsReady == null || PiIsInvalidated == null)
                { pipeline = "NDMF internals drifted"; return Settle.Drift; }
                // Global poll (NDMF has one preview session; single-LLM MCP → only this agent's edit unsettles it).
                var current = PiCurrent.GetValue(null, null);
                if (current == null) { pipeline = "PreviewSession.Current=null"; return Settle.Unsettled; }
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
        private static string SettleNote(GameObject root)
        {
            switch (ProbeSettle(root, out _))
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

        private static bool TryFocusKick(out string detail)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            { detail = "non-Windows editor, no kick path"; return false; }
            try
            {
                // isFocused, not a GetForegroundWindow pid check: the Unity process owns hidden helper
                // windows that can hold OS foreground while the editor itself is unfocused and throttled
                // (observed live) — the pid check reads "foreground" exactly when the kick is needed.
                if (EditorApplication.isFocused) { detail = "editor already focused"; return true; }
                var proc = System.Diagnostics.Process.GetCurrentProcess(); // qualified: `using System.Diagnostics` would make Debug ambiguous
                var hWnd = proc.MainWindowHandle;
                if (hWnd == IntPtr.Zero) { detail = "editor main window handle unresolved"; return false; }
                if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                bool ok = SetForegroundWindow(hWnd);
                detail = ok ? "editor foregrounded" : "SetForegroundWindow refused";
                return ok;
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

        private static bool HasReactiveMA(GameObject root)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var ty = mb.GetType();
                if ((ty.Namespace ?? "").IndexOf("modular_avatar", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var mk in ReactiveMarkers)
                    if (ty.Name.IndexOf(mk, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // Family arrow; NO `| png=` trailer — the schema never points at a PNG that isn't on disk.
        private static string Fail(string label, string reason)
        {
            string msg = "[RenderAvatar] " + (string.IsNullOrEmpty(label) ? "?" : label) + " => FAIL: " + reason;
            Debug.LogError(msg);
            return msg;
        }
    }
}
