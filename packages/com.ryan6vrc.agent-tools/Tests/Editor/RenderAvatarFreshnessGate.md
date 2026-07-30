# RenderAvatar freshness gate — live-editor detector (V4 protocol, certifying the V5 freshness layer)

Counter naming: the *protocol* in this doc is V4 (successor to the V3 detector); the *code's freshness layer* (canary + reload guard) is V5 in commit titles. They advance independently.

Re-run this whenever RenderAvatar's freshness code changes (`forcedRebake`, the settle gate, the change-horizon sweep, proxy isolation, the reload guard, the freshness canary). It is the live half of the headless canaries in `RenderAvatarProxyRebakeTests.cs` / `RenderAvatarCanaryTests.cs`; batchmode has no SceneView, preview scene, or play mode, so this gate runs against a live, MA-composed avatar over `execute_code`.

## The two-depth model (all measured 2026-07-29, AvatarProject)

The "backgrounded-editor freeze" is not one state. Every row below certified `gate=armed` while stale until its owning layer shipped:

| State | Arming | Content sync | GPU re-bake | `forcedRebake` flag | Caught by |
|---|---|---|---|---|---|
| idle freeze | multi-hour un-poked background idle (2026-07-16, witnessed; not summonable on demand) | alive | parked | **works** | flag (V3) |
| pending reload | compile lands while a reload can't (deferred/locked); `isCompiling` reads False once compiled | alive | parked | dead | reload guard, canary |
| post-play | play round-trip exited into an unfocused editor | **dead** | parked | dead | canary |

The post-play state defeats everything mechanism-shaped that was tried against it: `updateWhenOffscreen`, proxy weight writes, pumping `ProxySession.OnFrame`, per-controller `OnPreFrame` (syncs weights, pixels stay parked), `ForceResetPreview` + full rebuild (rebuilt proxies serve parked deform), `sharedMesh` reassign, and a 3 s programmatic focus. **The only measured cure is ~10 s of sustained editor focus.** Play mode itself renders fresh (live player loop) — the freeze forms on exit.

This is why the canary certifies the *outcome* (nudge one drawn original → pixels must move) rather than any mechanism: an unknown depth-3 freezes the nudge exactly as these do.

## Arming on demand

- **Pending reload**: `EditorApplication.LockReloadAssemblies()`, write + `ImportAsset` a scratch `.cs` under `Assets/Agent/Scratch/`, confirm `InternalEditorUtility.IsScriptReloadRequested()` is true. **Always unlock + delete the script after** — a leaked lock leaves the editor parked for everyone. **Flaky**: the compile can park at `isCompiling=True` without ever requesting the reload (seen 2026-07-29, second attempt, same recipe) — that sub-state measured *fresh* (canary corroborated by a real trip), so the cell is **inconclusive** when the request flag never flips; re-arm later rather than reporting either verdict.
- **Post-play**: enter and exit play mode with the editor unfocused (the whole session unfocused, including the exit), with **≥10 s in play and at least one in-play render** — an instant enter/exit round trip measured un-armed (2026-07-29, fresh + canary=live).
- **Unfocused domain reload**: a script recompile whose reload lands while the editor is unfocused re-armed a just-cured editor (2026-07-29) — the likely reason agent sessions (refresh + script edits mid-wave) hit this family so often. Not deliberately summoned yet; treat any canary-FAIL right after your own recompile as this, not as a code regression.
- Editor must be **unfocused for every cell** — abort any call that reads `InternalEditorUtility.isApplicationActive == true`, and set the `Ryan6VRC.AgentTools.RenderAvatar.DisableFocusKick` EditorPref for the run (unset after) so the gate's own kick doesn't fight the venue.

## Protocol (all `execute_code`, separate calls)

Parameters: `<AVATAR>` MA-composed scene root; `<BODY>` an SMR under it with live NDMF proxies; `<SHAPE>` a large-silhouette blendshape (BakeMesh 0→100 max delta > 10mm — verify first).

**Targets per cell**: every cell below runs `<BODY>`-scoped AND once `<AVATAR>`-scoped — leaf and root arm the same gate (arm-scope resolves to the outermost avatar root) but frame differently, and framing decides which blendshapes the canary's visibility filter accepts; a cell that passes at one scope and not the other is a finding, not noise.

1. **Healthy baseline** (un-armed; any focus state — the canary runs focused too, since a kicked-but-not-interacted editor reads focused while parked): `Capture` → summary must carry `canary=live`; trip `<SHAPE>` in its own call; `CaptureDiff` → changed ≥ 10× the no-op floor (grab a no-edit diff first for the floor; measured 0–4 px at tile res). Restore. Run one `showGizmos:true` variant — the canary's compare pair is its own gizmo-less baseline, so gizmo pixels must not fake a live verdict (council finding, PR #73).
2. **Pending-reload cell**: arm per above, then `Capture` → must FAIL with the pending-reload text (naming the lock iff `LockReloadAssemblies` is held). Disarm, confirm a re-grab goes back to `canary=live`.
3. **Post-play cell**: arm per above, trip `<SHAPE>`, `CaptureDiff` → must FAIL with the canary text (never an `OK` — an OK on this cell is the exact stale-certification this layer exists to kill). The named nudge should be a real shape on a drawn mesh.
4. **Cure**: click INTO the editor and stay ~10 s, re-grab; if still FAILing, **enter and exit play mode once with the editor focused** → `canary=live` and a fresh trip diff. (Measured 2026-07-29: programmatic kick-focus never cures; the interactive click cured one specimen and not a later, more-cycled one; the focused play round-trip cured the specimen the click could not — play runs the real skinning loop and a focused exit does not re-arm. The kick's value is limited to un-wedging NDMF rebuilds and prompting the human.)
5. **Same-call note**: edit+grab in ONE call is measured fresh on current NDMF (the in-call repaint pumps the content sync) — kept as a protocol *recommendation*, no longer load-bearing. Reactive-component edits in the same call still FAIL loudly via the settle gate; that is correct behavior, re-grab.
6. **BakeMesh ground truth rides every cell**: pixel evidence without a `BakeMesh` vertex delta lets a failed edit masquerade as a freeze (and vice versa).
7. **Self-noise**: three consecutive `Capture` calls on a reactive target, separate calls, healthy editor — none may settle-FAIL (the canary's nudge+restore is a pair of scene writes NDMF could observe; measured clean 2026-07-29, re-verify when the canary's write pattern changes).
8. **No-probe surface**: `Capture` a blendshape-less SMR prop → summary must carry the `canary unavailable` note, never `canary=live` and never a FAIL — the tool declares what it cannot certify.

## Traps

- The parked state **survives domain reloads** (two recompile/reload cycles measured, 2026-07-29) — recompiling is not a cure, and a canary-FAIL after your own script edit is the same state, not a new one.

- An **armed cell that reads fresh** usually means the editor got focused mid-cell (a human at the machine) — inconclusive, re-arm and re-run; never report it as "fixed".
- Any GameObject hide/show between baseline and measure forces an NDMF rebuild (fresh-looking) — keep cells clean.
- The canary can false-FAIL if view-0's framing makes the chosen nudge invisible (occlusion at that angle) — the FAIL names the mesh/shape so this is judgeable; re-grab from another angle order before suspecting the tool.
- A `Capture` **force-bakes the proxies it draws** (flag set during the grab): a flag-less control render taken *after* a Capture reads fresh even on an idle-freeze editor — order controls before captures, as V3 always required.
