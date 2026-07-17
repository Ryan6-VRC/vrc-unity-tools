# RenderAvatar freshness gate — live-editor detector (V3)

Re-run this whenever RenderAvatar's freshness code changes (`forcedRebake`, the settle gate, the
change-horizon sweep, proxy isolation). It is the live half of the proxy-rebake drift guards in
`RenderAvatarProxyRebakeTests.cs`; batchmode cannot reproduce the windowed-editor throttle states,
so this gate runs against a live, MA-composed avatar over `execute_code`.

## What it certifies — and what it can't

Certifies, on a healthy editor: a scripted blendshape edit on a reactive (NDMF-proxied) mesh is
visible in the next separate-call capture while the editor is backgrounded (pixels ≥ 10× the no-op
floor), with `BakeMesh` ground truth proving the edit landed. On an ARMED editor (see below) it
certifies the full transition: stale with the proxy flag reverted, fresh with it applied.

It does NOT certify fix efficacy when run un-armed — an un-armed editor reads fresh regardless of
the code (the false-"fixed" trap that laundered four prior sessions; V3 exit decision, 2026-07-16).
State which mode you ran in when you report results.

**Arming (measured 2026-07-16):** the freeze forms only after multi-hour genuinely-un-poked
background idle (witnessed twice; not summonable by scripted toggles, 40-min idle, Undo-path
writes, selection residue, Interaction-Mode settings, or ForceResetPreview). To attempt an armed
run: leave the editor backgrounded and untouched (no MCP calls, no focus) for hours/overnight,
then run the sequence below without focusing it first.

## Protocol (all `execute_code`, separate calls; abort any call that reads
`InternalEditorUtility.isApplicationActive == true`)

Parameters: `<AVATAR>` MA-composed scene root; `<BODY>` an SMR under it with live NDMF proxies;
`<SHAPE>` a large-silhouette blendshape (BakeMesh 0→100 max delta > 10mm — verify first).

1. **Floor**: `Capture(<AVATAR>, ["front"])` → png A; `CaptureDiff(<AVATAR>, A)` with no edit
   between → FLOOR px (expect O(200); 2026-07-16 measured 193–196).
2. **Baseline**: `Capture` → png B.
3. **Trip** (own call, no capture, no hides): `SetBlendShapeWeight(<SHAPE>, 100)`.
4. **Flag-less control render** (the "fix-reverted ⇒ stale" leg — without it an un-armed editor
   yields the same FRESH verdict and the run certifies nothing about the fix): render the live
   scene with an ad-hoc scratch `Camera.Render()` into a RenderTexture — that path sets NO
   `forceMatrixRecalculationPerRender` flags, so on an ARMED editor it draws the frozen proxy
   deform (measured 2026-07-16: an ad-hoc camera "draws the same frozen proxy") while step 5's
   `Capture` must read fresh. The control MUST precede step-5's `Capture` — `Capture` sets
   `forceMatrixRecalculationPerRender` and force-bakes the proxy; its finally restores the flag but
   not the bake, so a control run after it reads fresh even on an armed editor. Armed certification
   = control-stale + Capture-fresh on the identical state. Control-fresh means the editor is NOT
   armed — say so; the run then certifies detector behavior only, not fix efficacy.
5. **Measure** (own call): `CaptureDiff(<AVATAR>, B)` plus evidence bundle:
   - source `BakeMesh` max vertex delta vs weight-0 bake (> 10mm proves the edit landed);
   - live-proxy attribution: preview-scene (`___NDMF Preview___`) renderers with
     `NDMFPreview.GetOriginalObjectForProxy == <BODY>` and `enabled` — NOT sceneless name-matches
     (dead-session fossils read stale values);
   - content-sync: proxy **BakeMesh geometry** in the driven region (ShapeChanger applies baked
     geometry — proxy blendshape weights are the wrong instrument for reactive drives; scripted
     source edits do sync weights).
6. **Verdict**: changed ≥ 10×FLOOR with BakeMesh > 10mm → FRESH. changed ≤ 2×FLOOR with
   BakeMesh > 10mm → STALE (the bug; on a fixed build this must be impossible — investigate
   before touching the verdict). Between → report, don't force.
7. **Restore**: weight 0; destroy the scratch camera/RT; never save the scene.

Traps (each silently fakes a verdict): any GameObject hide/show between baseline and measure
forces an NDMF rebuild (fresh-looking); edit+capture in one call is a different contract row;
pixel evidence without the BakeMesh pair lets a failed edit masquerade as a repro; the tool's
settle-FAIL focus kick disturbs focus-state cells — set the
`Ryan6VRC.AgentTools.RenderAvatar.DisableFocusKick` EditorPref for the run (and unset after).
