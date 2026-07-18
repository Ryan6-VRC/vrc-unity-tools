# RenderThumbnail — design spec

An `avatar-tools` editor tool that produces a real-shader, cleanly-lit, optionally-posed 1200×900
portrait PNG of a **baked** avatar, to replace VRChat's default rest-pose upload thumbnail. Source
brief: `Atelier/kickoffs.md` block **U2**. Reader is the implementing agent — this records the
decisions, the load-bearing source facts, and the traps; it does not re-derive what the code shows.
Revised after a three-lens review (mechanism / interface / non-destructiveness); the traps below are
source-verified, not anticipated.

## Scope

**In (this PR): the `RenderThumbnail` tool only.** It renders and writes a PNG to a temp path and
returns that path. It does **not** upload.

**Out (deferred follow-up — operator's call):** the upload wiring — a thumbnail-PNG parameter on
`UploadAvatar` that calls `VRCApi.UpdateAvatarImage` after the record exists, and the `upload-avatar`
skill step. The tool is designed so that follow-up is trivial: its verdict carries a `png=<path>` token
(same token RenderAvatar uses), and the seam (`VRCApi.UpdateAvatarImage(id, data, pathToImage)`) is
proven in-repo (`references/ContinuousAvatarUploader/Editor/Uploader.cs:353`; SDK method
`AvatarProject/Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/API/VRCApi.cs:603`). No
blueprint IDs are in scope for this PR — rendering never touches an account.

## Why a bake is required (premise, confirmed)

At edit time MA/VRCFury have **not** unified the skeleton: `Merge Armature` "retargets bones exactly as
they sit in the scene and reconciles nothing" and is a build-time NDMF phase; VRCFury `Armature Link`'s
align options apply at build, not edit mode (`docs/nondestructive.md`). The reactive preview resolves
blendshapes / mesh-hiding / material-setters only — never skeletal merge. So sampling a pose onto the
base at edit time leaves merged clothing/hair in rest pose. The real build-on-a-clone is the only thing
that produces a posed avatar whose outfit follows the body.

**Why not reuse RenderAvatar's capture path** (so a future reader doesn't "simplify" toward it):
RenderAvatar renders through the Scene View (window-grab, resolution capped by pane size, headlight,
orthographic) because it does **not** bake — it must show NDMF *preview proxies*, which only the Scene
View composites. RenderThumbnail **bakes**, so the clone has real resolved meshes any camera renders
faithfully; that is exactly what unlocks a dedicated off-screen camera + fixed-size RenderTexture
(guaranteed 1200×900, headless-safe, real lighting). The two tools diverge because one bakes and one
doesn't — that single fact decides the correct capture surface for each.

## Locked decisions (operator sign-off)

| Decision | Choice | Note |
|---|---|---|
| Bake policy | **Always bake** | One code path; pose is an optional clip sampled onto the baked clone. Cost ≈ a play-mode build (seconds→minutes on a real composed avatar), once per upload. |
| Upload seam | **Post-upload `UpdateAvatarImage`** | Deferred to follow-up; decoupled, identical first/re-upload. |
| Framing | **Own head-anchored camera solve** | The SDK's `PositionPortraitCamera` is root-relative and pose-blind; `framing` names a subject height and distance is solved from `fov`. Superseded in full by `2026-07-18-render-thumbnail-camera.md`. |
| Pose source | **Author our own** `.anim` poses | No permissively-licensed curated portrait poses exist (two independent searches): the popular VRChat pose/locomotion assets ship no-redistribution EULAs, the free retargeting services forbid redistributing their clips standalone, and the academic mocap libraries hold motion rather than poses — their freeze-frames are mid-stride. Single-keyframe humanoid clips we author are committable and retarget to any rig. |
| Lighting/bg | **In-scene 3-point rig + camera-clear background** (`SolidColor`), overridable | Lights live **in the preview scene** (never `RenderSettings` — that's global, see Non-destructiveness). `bg` takes solid or gradient hex; the rig yaws with the camera. Levels and ramp mechanics: `2026-07-18-render-thumbnail-camera.md`. |

## Verified mechanism (callables + source-verified traps)

- **Bake:** `VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks.OnPreprocessAvatar(GameObject)`
  — the SDK preprocess chain (see `nondestructive.md` §The bake door for why NDMF's
  `ManualProcessAvatar` is the wrong door). It mutates the object **in place** and returns `false` when
  a hook blocks the build. **Traps:** (1) we must clone first — the argument is consumed, so a
  uniquely-named private clone is made and the original never touched; (2) hooks can open **modal
  dialogs** (VRCFury prompts per build on a broken Write Defaults mix), which wedges an MCP-driven
  render — a timed-out bake means read the dialog, not retry; (3) the paired
  `OnPostprocessAvatar()` **must** run, since that is what fires each hook's own cleanup — teardown
  calls it from an inner `finally` so no earlier teardown step can skip it by throwing.
- **Framing:** `VRC.SDKBase.VRC_AvatarDescriptor.PositionPortraitCamera(Transform)` (compiled in
  `VRCSDKBase.dll`; call sites SDK `RuntimeBlueprintCreation.cs:47`, CAU `Uploader.cs:615`). Sets the
  camera **transform only**, calibrated to the **default 60° FOV** — so **do not override FOV** (neither
  the SDK nor CAU does); set only clip planes (`near=0.01`, `far=100`) and `cullingMask=0xFFFFFFDF`, as
  CAU does. `framing` (dolly) moves the camera back along local-forward — **never** via FOV.
- **Pose:** `AnimationMode.StartAnimationMode()` → `BeginSampling()` →
  `SampleAnimationClip(bakedClone, clip, t)` → `EndSampling()` — then the pose is **held**;
  `StopAnimationMode()` runs later in teardown *after* the render (stopping it here reverts the pose).
  Humanoid
  retargeting **works on the baked clone** — MA re-runs `RebindHumanoidAvatar` as a build pass
  (`RebindHumanoidAvatar.cs`, MA `PluginDefinition.cs:150`/`197`), so `animator.avatar`/`isHuman`
  survive. Two guards: the clip must be a real muscle clip — **assert `clip.isHumanMotion == true`** (a
  silently-generic clip won't retarget, defeating "one clip everywhere"); and after
  `MoveGameObjectToScene` call **`animator.Rebind()`** before sampling (stale bind → no-op). All of
  `Start`/`Stop` and `Begin`/`End` are **global editor state** — nest their guards (below).
- **Sampling relocates the whole skeleton** — the clone's root snaps to the animator origin (≈0.8 m
  down, undoing NDMF's `+2` Z). This is why the camera anchors on the posed Head bone rather than on
  any root-relative offset; the solve is in `2026-07-18-render-thumbnail-camera.md`.
- **Capture:** dedicated disabled `Camera` (`HideFlags.DontSave`), `camera.scene = previewScene`,
  render into `new RenderTexture(1200, 900, 24, GraphicsFormat.R8G8B8A8_SRGB){ antiAliasing =
  Max(1, QualitySettings.antiAliasing) }`, `allowHDR = false` → `Texture2D(RGBA32).ReadPixels` →
  `EncodeToPNG`. **The sRGB RT format is mandatory** — the project is Linear (`ProjectSettings.asset`
  `m_ActiveColorSpace: 1`); a default linear RT ships a dark, wrong-gamma PNG. This is copied verbatim
  from CAU `TakePicture` (`Uploader.cs:611`,`619-621`) for exactly that reason. GrabPass (Poiyomi
  refraction/rim) renders correctly under a single `camera.Render()` — non-issue. `camera.clearFlags =
  SolidColor` with the chosen background (a preview scene has no skybox; default Skybox clear is
  nondeterministic at the backdrop edges).

## Tool contract

`[AgentTool] public static class RenderThumbnail`, one door:

```csharp
public static string Render(
    string target,            // avatar root: scene path or name (resolve like RenderAvatar's target)
    string pose = null,       // null => floor (unposed); a bundled pose token; or a clip asset path/GUID
    string expression = null, // null => none; else a gesture slot, clip name, or clip asset path/GUID
    string framing = "bust",  // bust | half | full — subject height; distance is solved from fov
    string bg = null,         // null => default backdrop; "#RRGGBB[AA]" solid or "#TOP:#BOTTOM" gradient
    float  fov = 30f,         // vertical degrees, [10,90]
    float? yaw = null,        // null => automatic oblique; else an offset on the head-tracking term
    bool   whatIf = false)    // preflight: resolve target/descriptor/pose/expression, report, bake NOTHING
```

### The pose vocabulary is a folder glob

There is no hard-wired name array. `Editor/Poses/RTPose_<Name>.anim` IS the vocabulary, matched the same
way `expression` matches FX state names — `NormalizeToken` on both sides, first match wins — so
`hand-on-hip`, `hand_on_hip` and `HandOnHip` are one name. **Adding a pose is dropping a file**, and the
unknown-pose error enumerates the folder, so what the tool advertises cannot drift from what ships.

One guard stays that the expression side does not need: a clip must be `isHumanMotion`. A generic clip
would not retarget across rigs, and `SampleAnimationClip` is a *silent* no-op for it on a humanoid — the
verdict would claim `pose=<name>` over an unposed avatar, and nothing downstream would catch it. There
is no runtime check for two files normalizing to the same name; a unit test guards that at build time,
which is where it belongs when poses land by dropping files.

### The bake door

`VRCBuildPipelineCallbacks.OnPreprocessAvatar`, per `nondestructive.md` §The bake door — the rule and
its evidence live there, not here. Three consequences this tool absorbs. The call mutates its argument
**in place**, so the clone *is* the baked avatar with no second object to destroy. It returns `false`
when a hook blocks the build, surfaced as a FAIL because such an avatar would not upload either. And
its paired `OnPostprocessAvatar` **must** be called — it fires each hook's own cleanup, so teardown
runs it from an inner `finally` that no earlier teardown step can skip by throwing.

### `expression` — a second clip, applied not sampled

The tool holds **no opinion about what an expression is**. It resolves the name the caller chose and
applies it; choosing is the caller's job, and a heuristic here would be the tool doing the LLM's.

`expression` is a state name on the baked FX controller, or a clip asset path/GUID. State-name
resolution is the mechanism that matters: the bake renames and merges blendshapes, so a clip taken
from a pre-bake asset can bind names the baked avatar no longer has, while a **state name survives**.
Resolution therefore runs on the baked clone — which is also why `whatIf` cannot preflight it and
simply echoes the token.

Matching is `NormalizeToken` over every FX layer's state names, first match wins. No layer window (the
bake reorders layers — Modular Avatar's `MergeBlendTreePass` uses `LayerPriority(int.MinValue)`, a sort
key, so it prepends) and no filtering of what "counts" (a caller asking for `Open` will not
accidentally match a `Shirt` toggle; the name they chose is the filter).

Applying writes `blendShape.*` curves straight onto the renderers rather than sampling a second clip,
because a second `SampleAnimationClip` re-runs the Animator's humanoid solver and partially undoes the
pose — measured, left upper arm `(301.6,303.5,76.7)` → `(321.6,344.4,33.2)`. Pose and expression bind
disjoint properties, not disjoint systems. A clip that moves nothing on the baked avatar is a named
FAIL: it binds shapes this avatar lacks, or only meshes that are not drawn.

## Render pipeline (every step teardown-guarded)

Setup snapshots (for restore): each open scene's `isDirty`, the set of live-scene root GameObjects,
`Selection.objects`/`activeGameObject`.

1. Resolve `target`; assert a `VRC_AvatarDescriptor`. If `whatIf`: resolve `pose`, echo `expression`,
   report, **return** (no bake).
2. **Unique private clone** (the keystone non-destructiveness fix): `var mine = Object.Instantiate(target);
   mine.name = target.name + "__rt_" + stamp;` then `OnPreprocessAvatar(mine)`, which mutates it in
   place — `mine` *is* the baked avatar, with no second object to destroy. The unique name keeps
   interleaved runs from colliding and any name-derived generated folder exclusive to this run.
3. `preview = EditorSceneManager.NewPreviewScene()`; `SceneManager.MoveGameObjectToScene(baked, preview)`.
4. Build the lit stage **in `preview`**: 3-point directional rig (key/fill/rim), all `HideFlags.DontSave`,
   one parent for one-destroy teardown. **Background is the camera's clear** (`SolidColor` plus, for the
   gradient `bg` form, a `CommandBuffer` ramp — see the camera doc), never a backdrop object.
   **No `RenderSettings` writes** (ambient/skybox
   are global/active-scene — they leak into and dirty the live scene; CAU stays lights-only for this reason).
5. Compute the world view point and `viewHeight` on **every** path (the floor path must not touch the
   Animator). If posing (and `animator.isHuman`, else fail loud):
   `animator.Rebind()`; capture `restHeadRot`; `StartAnimationMode()` → `BeginSampling()` → `try{ SampleAnimationClip(baked, clip, t)
   } finally{ EndSampling() }`; capture `posedHeadRot`. **Do NOT stop animation mode here** — the sampled pose is *held*, and
   `StopAnimationMode()` runs only in the teardown `finally` (step 7) *after* the render, or the avatar would
   revert to unposed before capture. (`t` = a chosen still frame; our clips are one keyframe.)
6. Camera (§Capture): the head-anchored solve (`2026-07-18-render-thumbnail-camera.md`); render → RT → PNG.
   Report the resolved `camYaw` and the view point's viewport coords in the verdict; fail only on a
   blank frame.
7. **`finally`** (runs on every exit, success or throw): stop animation mode **only if this run started it**
   (snapshot-guarded — never end an operator's pre-existing recording session);
   `ClosePreviewScene(preview)`; `DestroyImmediate` the baked clone and any **live-scene root new since the
   step-0 snapshot** (catches a reference-less orphan from a bake that threw); restore `Selection`;
   `ClearSceneDirtiness` on each scene that was clean at snapshot; then, from an **inner `finally` that
   no earlier teardown step can skip by throwing**, call `OnPostprocessAvatar()` — the SDK's paired half,
   which fires each hook's own cleanup rather than this tool guessing at folders it does not own.
   Generated assets that outlive it are not chased; every project accumulates some.

**Concurrency:** the bake's `AssetDatabase.StartAssetEditing`, `AnimationMode`, and `Selection` are
global editor state — **serialize `RenderThumbnail` calls** (do not run two at once), consistent with the
serial-venue guidance elsewhere in the workshop.

## Owned pose assets

- Bundle under `com.ryan6vrc.avatar-tools/Editor/Poses/` as single-keyframe **humanoid muscle** `.anim`
  clips. v1 set: `floor` (implicit, no asset) + **clasped** (hands clasped in front) + **hand-on-hip**,
  authored via muscle curves. These are **starter poses** — recognizable and natural, but blind muscle-value
  authoring converges slowly, so they are explicitly tunable later (the Animation window with live visual
  feedback, or a licensed clip via the `pose` path/GUID arg). Head-anchored framing is what makes
  any pose sit correctly; the specific stances are secondary.
- Each bundled clip is asserted `isHumanMotion == true` in tests — the guard against a silently-generic
  save that would only pose identical-bone-path rigs.

## Non-destructiveness & the Plum-Remy test venue

The real invariant is **"the scene and editor are left as found after any run, success or failure"**.
Generated assets are the exception, deliberately: the bake's hooks own those, `OnPostprocessAvatar`
fires their cleanup, and anything surviving it is left alone rather than guessed at. It is enforced
structurally, not by hope:

- **No user asset is ever destroyed:** the unique-named clone (step 2) keeps any name-derived generated
  folder exclusive to this run — the single highest-consequence risk on a real/messy project.
- **The live scene is restored:** the bake dirties the active scene (the clone lives there until it is
  moved) — so `isDirty` is snapshotted and `ClearSceneDirtiness` restores a scene that was clean; the
  orphan-sweep destroys any stranded clone.
- **Global editor state is restored:** `AnimationMode` (nested idempotent guards), `Selection`
  (snapshot/restore like `RenderAvatar.cs:426`,`683` — **not** CAU, which clobbers it), and **no**
  `RenderSettings` writes at all.
- **Generated assets are the hooks' own:** teardown calls the SDK's paired `OnPostprocessAvatar` from
  an inner `finally`, which fires each hook's cleanup; a throw there is surfaced in the verdict.
- **Venue** (`worktree-unity-editor`): host this worktree's `com.ryan6vrc.avatar-tools` in the live
  **Plum-Remy-3.0** editor (`@6401`) — a real MA/VRCFury avatar, the merged-skeleton case. Back up
  `Packages/manifest.json`, repoint the one package (absolute `file:`), `Resolve` + `Refresh`; after
  verifying, **restore the manifest** and re-resolve. Its blueprint IDs are real and never enter a commit
  (and this PR doesn't upload).

## Verification

Rendering mutates, so most behavior is proven live, not in NUnit
(`vrc-unity-tools-editmode-batchmode`, `subagent-editmode-verify-serial`). Three tiers:

- **Pre-implementation spike — DONE, PASSED** (2026-07-17, live Plum-Remy `@6401`, **`Shinano_kisekae`** —
  a genuinely MA/VRCFury-composed avatar: 6 ModularAvatar components incl. `MergeArmature`, 18 skinned
  meshes; a first pass on the non-composed `Chocolat` was discarded for not exercising the merge). On the
  merged case: unique-clone → the bake fully processed it (**MA components 6→0**, 18 meshes
  survived, baked clone `isHuman=True` `avatar=Shinano_kisekaeAvatar`) → `MoveGameObjectToScene(preview)`
  → `animator.Rebind()` → `SampleAnimationClip(Chiffon_Fist)` rotated the finger proximals **48–83°**.
  **Clothing-follows-body confirmed:** all 18 merged meshes are weighted to the unified right-hand bone
  chain, so posing it drives body + every outfit piece off one skeleton — the exact premise the bake
  exists to satisfy. Teardown verified: no orphan, `InAnimationMode=False` after, console 0 errors.
  (Run against NDMF's `ManualProcessAvatar`, before the bake door moved to the SDK chain — the
  clothing-follows-body premise it established is unaffected.) Not exercised (scene was already dirty
  from operator work): the `ClearSceneDirtiness` restore branch — covered by the invariant test below.
- **Headless EditMode** (`tools/run-editmode-tests.ps1` against `TestEditor`, run serially): pure helpers
  only — `pose` resolution order and token normalization, pose-token uniqueness (the sole guard against
  a glob collision), `framing`→span mapping, `bg` parse (solid and gradient), the camera solve's pure
  float math (signed euler wrap, `screenRight` sign, `AimDrop`), and each bundled clip's
  `isHumanMotion == true`. Fail-loud branches use
  `LogAssert.Expect`. The bake/render/sample path is **not** in NUnit (mutates live objects → crashes the
  suite).
- **Live `execute_code`** against the Plum-Remy composed avatar:
  1. **Pristine-after-run invariant** (the real non-destructiveness gate the eyeball can't provide):
     snapshot before/after a normal run and assert equal — each open
     scene's `isDirty`, `AnimationMode` restored to its **pre-run** state (the tool stops it only if it
     started it — never ends an operator's recording), `Selection` unchanged, and **no residual
     `*__rt_*(Clone)` root** in any open scene.
  2. **Fault-injection run:** force a throw *after* a successful bake (e.g. a clip that fails to sample)
     and assert the same invariants still hold — the only proof teardown fires on the exception path.
  3. **Eyeball:** render floor + each pose, `SendUserFile` the PNGs to the operator; compare against the
     status-quo default thumbnail. The eyeball gates *appearance*; tiers 1–2 gate *safety*.

## Open risks / notes

- **Pose framing — resolved** by the head-anchored camera (`2026-07-18-render-thumbnail-camera.md`),
  which anchors on the posed head's own position *and* orientation, so a clip carrying root motion or
  root rotation no longer breaks framing.
- **Lighting constants** are taste defaults (named, obvious, top-of-file); expect the operator to tune
  key/fill/rim after seeing real output.
