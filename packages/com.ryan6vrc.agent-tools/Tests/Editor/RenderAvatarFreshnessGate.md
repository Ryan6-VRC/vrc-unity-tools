# RenderAvatar freshness gate — live-editor detector

Re-run this whenever RenderAvatar's freshness code changes (`forcedRebake`, the settle gate, the change-horizon sweep, proxy isolation, the reload guard, the freshness canary, the sync-compile shading guard). It is the live half of the headless canaries in `RenderAvatarFreshnessTests.cs`; batchmode has no SceneView, preview scene, or play mode, so this gate runs against a live, MA-composed avatar over `execute_code`.

## The two-depth model

The "backgrounded-editor freeze" is not one state. Each row reads `gate=armed` while stale unless the layer in its last column catches it:

| State | Arming | Content sync | GPU re-bake | `forcedRebake` flag | Caught by |
|---|---|---|---|---|---|
| idle freeze | multi-hour un-poked background idle (not summonable on demand) | alive | parked | **works** | flag |
| pending reload | compile lands while a reload can't (deferred/locked); `isCompiling` reads False once compiled | alive | parked | dead | reload guard, canary |
| post-play | play round-trip exited into an unfocused editor | **dead** | parked | dead | canary |

The post-play state defeats everything mechanism-shaped that was tried against it: `updateWhenOffscreen`, proxy weight writes, pumping `ProxySession.OnFrame`, per-controller `OnPreFrame` (syncs weights, pixels stay parked), `ForceResetPreview` + full rebuild (rebuilt proxies serve parked deform), `sharedMesh` reassign, and a 3 s programmatic focus. **The measured cures are ~10 s of sustained interactive focus and, failing that, one focused play round trip** (§Protocol step 4). Play mode itself renders fresh (live player loop); the freeze forms on exit.

The canary therefore certifies the *outcome* (nudge one drawn original, pixels must move) rather than any mechanism: an unknown third depth would freeze the nudge exactly as these do.

## Arming on demand

- **Pending reload**: `EditorApplication.LockReloadAssemblies()`, write + `ImportAsset` a scratch `.cs` under `Assets/Agent/Scratch/`, confirm `InternalEditorUtility.IsScriptReloadRequested()` is true. **Always unlock + delete the script after** — a leaked lock leaves the editor parked for everyone. **Flaky**: the compile can park at `isCompiling=True` without ever requesting the reload. That sub-state measured *fresh* (canary corroborated by a real trip), so the cell is **inconclusive** when the request flag never flips; re-arm later rather than reporting either verdict.
- **Post-play**: enter and exit play mode with the editor unfocused (the whole session unfocused, including the exit), with **≥10 s in play and at least one in-play render**. An instant enter/exit round trip measured un-armed (fresh + canary=live).
- **Unfocused domain reload**: a script recompile whose reload lands while the editor is unfocused re-armed a just-cured editor. There is no on-demand recipe; treat any canary-FAIL right after your own recompile as this, not as a code regression.
- **Unresolved shading**: `AssetDatabase.ImportAsset("Packages/jp.lilxyzw.liltoon/Shader/lts.shader", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport)`, then grab **in the same call**. The placeholder is drawn only for variants still compiling and the compile is queued by the grab's own repaint, so a separate-call grab arrives after it cleared — this is the one cell whose arming and measurement must share a call. Unlike every recipe above it is **deterministic, ~335 ms, and self-clears in one grab**, making it the cheapest cell here. Focus state is irrelevant.
- Editor must be **unfocused for every cell that does not say otherwise** — abort any call that reads `InternalEditorUtility.isApplicationActive == true`, and set the `Ryan6VRC.AgentTools.RenderAvatar.DisableFocusKick` EditorPref for the run (unset after) so the gate's own kick doesn't fight the venue.

## Protocol (all `execute_code`, separate calls)

Parameters: `<AVATAR>` MA-composed scene root; `<BODY>` an SMR under it with live NDMF proxies; `<SHAPE>` a large-silhouette blendshape (BakeMesh 0→100 max delta > 10mm — verify first).

**Targets per cell**: every cell below runs `<BODY>`-scoped AND once `<AVATAR>`-scoped — leaf and root arm the same gate (arm-scope resolves to the outermost avatar root) but frame differently, and framing decides which blendshapes the canary's visibility filter accepts; a cell that passes at one scope and not the other is a finding, not noise.

1. **Healthy baseline** (un-armed; any focus state — the canary runs focused too, since a kicked-but-not-interacted editor reads focused while parked): `Run` → summary must carry `canary=live`; trip `<SHAPE>` in its own call; `CaptureDiff` → changed ≥ 10× the no-op floor (grab a no-edit diff first for the floor; measured 0–4 px at tile res). Restore. Run one `showGizmos:true` variant — the canary's compare pair is its own gizmo-less baseline, so gizmo pixels must not fake a live verdict.
2. **Pending-reload cell**: arm per above, then `Run` → must FAIL with the pending-reload text (naming the lock iff `LockReloadAssemblies` is held). Disarm, confirm a re-grab goes back to `canary=live`.
3. **Post-play cell**: arm per above, trip `<SHAPE>`, `CaptureDiff` → must FAIL with the canary text; an `OK` on this cell is a stale certification. The named nudge should be a real shape on a drawn mesh.
4. **Cure**: click INTO the editor and stay ~10 s, re-grab; if still FAILing, **enter and exit play mode once with the editor focused** → `canary=live` and a fresh trip diff. Programmatic kick-focus never cures, and the click can fail on a more-cycled editor where the focused play round trip still cures: play runs the real skinning loop and a focused exit does not re-arm. The kick's value is limited to un-wedging NDMF rebuilds and prompting the human.
5. **Same-call note**: edit+grab in ONE call measures fresh on current NDMF (the in-call repaint pumps the content sync), but reactive-component edits in the same call still FAIL via the settle gate; re-grab.
6. **BakeMesh ground truth rides every cell**: pixel evidence without a `BakeMesh` vertex delta lets a failed edit masquerade as a freeze (and vice versa).
7. **Self-noise**: three consecutive `Run` calls on a reactive target, separate calls, healthy editor — none may settle-FAIL (the canary's nudge+restore is a pair of scene writes NDMF could observe; re-verify when the canary's write pattern changes).
8. **No-probe surface**: `Run` a blendshape-less SMR prop → summary must carry the `canary unavailable` note, never `canary=live` and never a FAIL.
9. **Shading cell** — the only proof the sync-compile guard holds, since batchmode cannot see a rendered pixel. **This doc is canon for every measured count below**: the code comments, the class doc, and the FAIL strings route here and must not restate the measured counts; the 0 floor is the one figure a FAIL prints. The guard is unconditional, so the unguarded control has to come from the commit before it, not from a flag.
   - *Control*, on the commit before the guard: arm per §Arming on demand, `Run` in that same call, count exact `#00FFFF` — must be **> 0**, else the arming didn't take and the cell proves nothing (measured: 32,618 px at 1 angle/512, 453,750 at 4 angles/1024).
   - *Measure*, on the guarded code, same avatar and same angles: **the grab must return OK, and hand-count the returned sheet anyway — it must be 0**. The tool's own scan (`CountPlaceholderPx`) FAILs on any hit, which makes `OK` alone **circular** as proof: it says the counter returned 0, the thing under test. The hand count is the independent half and catches a scan wired to the wrong buffer.

   Treat a hit as a finding to resolve by re-grab rather than proof by itself: an unlit or emissive material could legitimately render exact cyan, which no fixture here does. Do not filter to flat blob interiors. The corruption measured both total (a settled `canary=live` grab drew a whole silhouette flat cyan on an avatar whose hair texture means (233,226,224)) and partial (64 px on a second avatar's sheet that read healthy, cleared by the guard), and the partial case has **no** interior pixels.

   **Scan bound, from `Downscale`:** it is 4-tap bilinear *at a point*, not a box filter over the footprint, so at `scale > 1` a cyan region narrower than the sample spacing (`side/tileRes`) can fall between taps and be missed **entirely** rather than diluted. The bound grows as `resolution` drops but stops widening at `tileRes` 128, because `tileRes = Max(MinTileRes, …)` clamps a `resolution:64` request back up. Not in play at the measured failure sizes; in play at `resolution:128` against a ~900 px viewport, the true worst case. A survivor generally needs a pure-cyan 2×2 at the sample point, though a single px suffices where the taps land exactly on a texel (odd integer `scale`, which zeroes both interpolants).

   **Guard-holds measurements, on guarded code with the editor unfocused.** Every variant measured **0 exact `#00FFFF` and 0 near-cyan**: no arming at front/512; §Arming's `lts.shader` recipe in the same call (325 ms); the same without `ForceSynchronousImport` (241 ms); a bulk `ForceUpdate` of all 65 lilToon shaders in the same call (1881 ms); `showGizmos:true` at 512; 4 angles at 1024 (2048² sheet); and `showGizmos:true` with 4 angles at 1024.

   **`anythingCompiling` rides the FAIL as a mechanism hint and never fires as a standalone note**: the bulk variant read it True across a grab whose tiles scanned at exactly zero, so the signal cannot see the outcome.

## Traps

- **A flat-cyan FAIL already carries the `anythingCompiling` read** and splits its remedy on it, so act on its text rather than re-probing. It cannot tell you two things. A shader *asset* mid-import has no compiled variant to block on and is an open edge the guard does not cover; the variant without `ForceSynchronousImport` does not narrow it, because `ImportAsset` had finished before the grab ran. And the trigger coverage is inference: the reproduction is a shader reimport, while a mid-NDMF-settle sighting has not been reproduced.
- The parked state **survives domain reloads**, so recompiling is not a cure.
- An **armed cell that reads fresh** usually means the editor got focused mid-cell (a human at the machine) — inconclusive, re-arm and re-run; never report it as "fixed".
- Any GameObject hide/show between baseline and measure forces an NDMF rebuild (fresh-looking) — keep cells clean.
- The canary can false-FAIL if view-0's framing makes the chosen nudge invisible (occlusion at that angle) — the FAIL names the mesh/shape so this is judgeable; re-grab from another angle order before suspecting the tool.
- A `Run` **force-bakes the proxies it draws** (flag set during the grab): a flag-less control render taken *after* a Capture reads fresh even on an idle-freeze editor, so order controls before captures.
