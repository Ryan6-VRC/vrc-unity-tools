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
| Framing | **SDK `PositionPortraitCamera`** | Bust (head+shoulders) from `ViewPosition`, at the SDK-calibrated default 60° FOV. A `framing` enum dollies back for more body. |
| Pose source | **Author our own** `.anim` poses | No permissively-licensed curated portrait poses exist (two independent searches; GoGo Loco / BUDDYWORKS are no-redistribution EULAs, Mixamo forbids standalone redistribution, CMU/ACCAD/Quaternius are motion libraries whose freeze-frames are mid-stride). Single-keyframe humanoid clips we author are committable and retarget to any rig. |
| Lighting/bg | **In-scene 3-point rig + solid background** (camera `SolidColor` clear), overridable | Lights live **in the preview scene** (never `RenderSettings` — that's global, see Non-destructiveness). Solid (uniform) bg keeps the silhouette reference trivial; gradient is deferred polish. Constants are cosmetic taste defaults. |

## Verified mechanism (callables + source-verified traps)

- **Bake:** `nadena.dev.ndmf` → `AvatarProcessor.ManualProcessAvatar(GameObject)` (`AvatarProcessor.cs:105`,
  `[PublicAPI]`). Clones the passed object internally, returns the fully-baked clone (all phases → MA +
  VRCFury + skeleton merge), original untouched. **Traps:** (1) it instantiates that clone **into the
  live scene**, unhidden, shifted `+2` Z, and returns it only on success — an exception mid-bake strands
  a reference-less orphan and dirties the live scene; (2) it writes generated assets to a **persistent**
  folder named after the clone: `Assets/ZZZ_GeneratedAssets/<name>(Clone)/`, and **if that folder
  already exists it is deleted wholesale** inside the bake (`AssetSaver.cs:39`; folder name from
  `BuildContext.cs:158`). Both traps are handled by the **unique private clone** below. Do **not** use the
  in-place `ProcessAvatar(GameObject)` overloads (mutate the root), and do **not** call
  `AvatarProcessor.CleanTemporaryAssets()` post-bake (its temp-dir override is already disposed, so it
  targets the wrong folder).
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
- **Posed-head framing correction (measured, load-bearing).** Sampling resets the avatar's body
  position — the whole skeleton relocates (the clone's root shifts to the animator origin, ≈0.8m down
  and undoing NDMF's `+2` Z). `PositionPortraitCamera` runs *after* sampling so it already absorbs the
  **root** move, but it aims at the static `ViewPosition`, not the dropped head. The residual is only the
  head's **root-local** displacement: capture Head-bone + root world positions before `Rebind` and after
  `EndSampling`, and translate the camera by `(posedHead−posedRoot)−(restHead−restRoot)` *after*
  `PositionPortraitCamera` + the `framing` dolly. Floor path: the delta is `Vector3.zero` (no change). The
  delta is a world-space `Transform.position` difference, so it is correct under any root orientation
  provided the clip carries no root-rotation curve (the bundled clips carry none).
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
    string expression = null, // null => no expression; else a facial clip asset path/GUID (no vocabulary)
    string framing = "bust",  // bust | half | full — dolly distance over PositionPortraitCamera
    string bg = null,         // null => default backdrop; "#RRGGBB" => solid color (fail named on unparseable)
    bool   whatIf = false)    // preflight: resolve target/descriptor/pose/expression, report, bake NOTHING
```

### The pose vocabulary is a folder glob

There is no hard-wired name array. `Editor/Poses/RTPose_<Name>.anim` IS the vocabulary: a token matches
by normalizing both sides (lowercase, strip non-alphanumerics), so `hand-on-hip`, `hand_on_hip` and
`HandOnHip` are one token. **Adding a pose is dropping a file** — no code edit — and the unknown-pose
error enumerates the glob, so what the tool advertises cannot drift from what ships.

### `expression` — a second clip, applied not sampled

Deliberately **no bundled vocabulary**: expressions are avatar-specific, so a bare name fails with a
pointer at the discovery route (`ReportController` on the FX controller — candidates sit on layers 1–2 —
then `ReportClip` to confirm blendShape curves on the face mesh). Those two tools already return every
datum needed; a third discovery tool would only re-derive them.

Validation is the **mirror image of pose** — pose requires `isHumanMotion=true`, expression requires
`isHumanMotion=false` plus ≥1 `blendShape.*` binding — so passing the two swapped fails named instead of
silently no-opping.

**The expression is written directly onto the renderers, never sampled.** A second
`SampleAnimationClip` in the pose's sampling block re-runs the Animator's humanoid solver, which
re-solves muscles toward default and *partially undoes the pose*: measured on Chocolat, the left upper
arm moved `(301.6,303.5,76.7)` → `(321.6,344.4,33.2)` when the face clip was sampled after the pose.
Pose and expression bind disjoint **properties** but not disjoint **systems**. Evaluating each
`blendShape.*` curve and calling `SetBlendShapeWeight` touches neither the Animator nor AnimationMode
and leaves the pose byte-identical. Only blendShape curves are applied — by contract an expression *is*
its blendshape curves.

`shapes=<applied>/<total>` rides the verdict whenever an expression was requested. A clip authored for a
different body, or a bake that renamed the face mesh, lands fewer curves than it tried; zero landing is
a named FAIL rather than a portrait with a blank face and a verdict still claiming `expression=<name>`.

- **Verdict grammar** (matches the family — `[Tool] Verb <label> … => OK | key=val`; RunLog is
  intentionally **not** used — a render tool's artifact is the PNG, as with RenderAvatar):
  - render: `[RenderThumbnail] Render <label> baked pose=<name|floor> expression=<path|none> [shapes=53/67] framing=bust silhouette=41% => OK | png=<temp>/renderthumbnail_<label>_<stamp>.png`
  - preflight: `[RenderThumbnail] Render <label> whatIf pose=clasped expression=none descriptor=OK => WOULD-RENDER (no bake)` (pose/expression tokens = the arguments as passed, or `floor`/`none`)
  - the `png=` token is load-bearing (the deferred upload step consumes it) — keep it verbatim.
- **Fail loud, named** (CLAUDE.md rule 7): missing `VRC_AvatarDescriptor`, unresolvable `pose`
  (error **enumerates the glob**: `unknown pose 'x' — bundled: Clasped, HandOnHip; or pass a clip asset
  path/GUID`), an `expression` that is a muscle clip / carries no blendShape curves / lands zero shapes
  on the baked avatar, unparseable `bg`, a non-`isHumanMotion` pose clip, a bake exception, or a
  near-empty silhouette (`silhouette≈0%` = "nothing drew"). `silhouette=NN%` is **reported**, not
  gated on a tuned middle threshold (per `dont-tune-tools-against-one-clean-asset`) — only ~0 fails; a
  low-but-nonzero value is surfaced for the operator to judge, replacing an unbacked "MA+VRCFury
  resolved" assertion.
- **Cleanup failure is surfaced, not swallowed** (it's the only host-project write): residue appends
  `note=cleanup-residual: Assets/ZZZ_GeneratedAssets/<sub> not removed` to the verdict — a failed
  cleanup can't hide behind a still-`OK` render.
- **Output:** fixed **1200×900** PNG to `Application.temporaryCachePath` (outside `Assets/`, like
  RenderAvatar). Resolution is not a param (YAGNI; the SDK re-crops on upload).
- **`pose` resolution order:** `null` → floor (no sampling); else a bundled name under the package's
  `Poses/` → the clip asset; else treat as an asset path or GUID and load it. The path/GUID branch is the
  extension point for referencing an installed paid clip later **without committing it** (deferred P2).
- **`framing`:** `bust` (SDK default distance) · `half` · `full` map to fixed dolly-back distances.
  Named/outcome-shaped rather than a numeric knob (a raw "zoom" reads inverted vs. camera convention).

**NDMF dependency — deliberate:** unlike RenderAvatar (which reflects into NDMF *internal* preview
state precisely to avoid an asmdef reference), RenderThumbnail takes a **hard asmdef + package.json
reference on `nadena.dev.ndmf`** — `ManualProcessAvatar` is a supported `[PublicAPI]` build dependency,
not internal state. This is an intentional divergence from the family's reflect-to-decouple habit; the
package manifest change is expected.

## Render pipeline (every step teardown-guarded)

Setup snapshots (for restore): each open scene's `isDirty`, the set of live-scene root GameObjects,
`Selection.objects`/`activeGameObject`.

1. Resolve `target`; assert a `VRC_AvatarDescriptor`. If `whatIf`: resolve `pose`, report, **return** (no
   bake).
2. **Unique private clone** (the keystone non-destructiveness fix): `var mine = Object.Instantiate(target);
   mine.name = target.name + "__rt_" + stamp;` then `var baked = AvatarProcessor.ManualProcessAvatar(mine);`
   `Object.DestroyImmediate(mine);`. The generated folder is now
   `Assets/ZZZ_GeneratedAssets/<name>__rt_<stamp>(Clone)/` — provably exclusive to this run, so NDMF's
   pre-existing-folder wholesale-delete can never touch a user's kept bake, and interleaved runs never
   collide.
3. `preview = EditorSceneManager.NewPreviewScene()`; `SceneManager.MoveGameObjectToScene(baked, preview)`.
4. Build the lit stage **in `preview`**: 3-point directional rig (key/fill/rim), all `HideFlags.DontSave`,
   one parent for one-destroy teardown. **Background is the camera's `clearFlags = SolidColor`** (the `bg`
   color, else the neutral default) — a uniform solid, no backdrop object (which also keeps the silhouette
   reference trivial). A gradient backdrop is deferred polish. **No `RenderSettings` writes** (ambient/skybox
   are global/active-scene — they leak into and dirty the live scene; CAU stays lights-only for this reason).
5. If posing (and `animator.isHuman`, else fail loud): capture Head-bone + root world positions;
   `animator.Rebind()`; `StartAnimationMode()` → `BeginSampling()` → `try{ SampleAnimationClip(baked, clip, t)
   } finally{ EndSampling() }`. **Do NOT stop animation mode here** — the sampled pose is *held*, and
   `StopAnimationMode()` runs only in the teardown `finally` (step 7) *after* the render, or the avatar would
   revert to unposed before capture. Capture Head + root again post-sample for the framing delta
   (§Verified mechanism → Pose). (`t` = a chosen still frame; our clips are one keyframe.)
6. Camera (§Capture): `PositionPortraitCamera` + `framing` dolly + posed-head delta; render → RT → PNG.
   Measure silhouette coverage for the verdict.
7. **`finally`** (runs on every exit, success or throw): stop animation mode **only if this run started it**
   (snapshot-guarded — never end an operator's pre-existing recording session);
   `ClosePreviewScene(preview)`; `DestroyImmediate` the baked clone and any **live-scene root new since the
   step-0 snapshot** (catches a reference-less orphan from a bake that threw); restore `Selection`;
   `ClearSceneDirtiness` on each scene that was clean at snapshot; delete the run's unique
   `ZZZ_GeneratedAssets/<...>(Clone)` subfolder via `AssetDatabase.DeleteAsset` (handles its `.meta`),
   and then **delete the parent `ZZZ_GeneratedAssets` folder itself whenever it is now empty** (whether or
   not this run created it — an empty folder means nothing else is relying on it, so leave the project as
   if we were never here). A pre-existing user bake in a sibling subfolder keeps the folder non-empty, so
   this only fires when it's genuinely empty.

**Concurrency:** the bake's `AssetDatabase.StartAssetEditing`, `AnimationMode`, and `Selection` are
global editor state — **serialize `RenderThumbnail` calls** (do not run two at once), consistent with the
serial-venue guidance elsewhere in the workshop.

## Owned pose assets

- Bundle under `com.ryan6vrc.avatar-tools/Editor/Poses/` as single-keyframe **humanoid muscle** `.anim`
  clips. v1 set: `floor` (implicit, no asset) + **clasped** (hands clasped in front) + **hand-on-hip**,
  authored via muscle curves. These are **starter poses** — recognizable and natural, but blind muscle-value
  authoring converges slowly, so they are explicitly tunable later (the Animation window with live visual
  feedback, or a licensed clip via the `pose` path/GUID arg). The framing correction (below) is what makes
  any pose sit correctly; the specific stances are secondary.
- Each bundled clip is asserted `isHumanMotion == true` in tests — the guard against a silently-generic
  save that would only pose identical-bone-path rigs.

## Non-destructiveness & the Plum-Remy test venue

The real invariant is **"the project and editor are left as found after any run, success or failure"** —
with one deliberate exception: an **empty** `ZZZ_GeneratedAssets` folder is deleted even if it pre-existed
(operator's request — an empty folder relies on nothing). It is enforced structurally, not by hope:

- **No user asset is ever destroyed:** the unique-named clone (step 2) removes NDMF's same-name
  wholesale-delete hazard — the single highest-consequence risk on a real/messy project.
- **The live scene is restored:** `ManualProcessAvatar` *does* dirty the active scene (it instantiates
  the clone there) — so `isDirty` is snapshotted and `ClearSceneDirtiness` restores a scene that was
  clean; the orphan-sweep destroys any stranded clone.
- **Global editor state is restored:** `AnimationMode` (nested idempotent guards), `Selection`
  (snapshot/restore like `RenderAvatar.cs:426`,`683` — **not** CAU, which clobbers it), and **no**
  `RenderSettings` writes at all.
- **The only host-project write** is the run's unique `ZZZ_GeneratedAssets` subfolder, deleted in
  `finally`; a failure to delete is surfaced in the verdict.
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
  merged case: unique-clone → `ManualProcessAvatar` fully processed it (**MA components 6→0**, 18 meshes
  survived, baked clone `isHuman=True` `avatar=Shinano_kisekaeAvatar`) → `MoveGameObjectToScene(preview)`
  → `animator.Rebind()` → `SampleAnimationClip(Chiffon_Fist)` rotated the finger proximals **48–83°**.
  **Clothing-follows-body confirmed:** all 18 merged meshes are weighted to the unified right-hand bone
  chain, so posing it drives body + every outfit piece off one skeleton — the exact premise the bake
  exists to satisfy. Teardown verified: no orphan, `InAnimationMode=False` after, unique
  `ZZZ_GeneratedAssets` subfolder deleted and the now-empty parent removed, console 0 errors. Confirmed
  incidentally: `nadena.dev.ndmf.AvatarProcessor.ManualProcessAvatar` binds directly (no reflection),
  validating the hard-NDMF-ref decision. Not exercised (scene was already dirty from operator work): the
  `ClearSceneDirtiness` restore branch — covered by the invariant test below.
- **Headless EditMode** (`tools/run-editmode-tests.ps1` against `TestEditor`, run serially): pure helpers
  only — `pose` resolution order, `framing`→distance mapping, cleanup subfolder-name derivation, verdict
  formatting, `bg` parse, and each bundled clip's `isHumanMotion == true`. Fail-loud branches use
  `LogAssert.Expect`. The bake/render/sample path is **not** in NUnit (mutates live objects → crashes the
  suite).
- **Live `execute_code`** against the Plum-Remy composed avatar:
  1. **Pristine-after-run invariant** (the real non-destructiveness gate the eyeball can't provide):
     snapshot before/after a normal run and assert equal — `ZZZ_GeneratedAssets` listing, each open
     scene's `isDirty`, `AnimationMode` restored to its **pre-run** state (the tool stops it only if it
     started it — never ends an operator's recording), `Selection` unchanged, and **no residual
     `*__rt_*(Clone)` root** in any open scene.
  2. **Fault-injection run:** force a throw *after* a successful bake (e.g. a clip that fails to sample)
     and assert the same invariants still hold — the only proof teardown fires on the exception path.
  3. **Eyeball:** render floor + each pose, `SendUserFile` the PNGs to the operator; compare against the
     status-quo default thumbnail. The eyeball gates *appearance*; tiers 1–2 gate *safety*.

## Open risks / notes

- **Pose framing — resolved.** The body-position reset that dropped the posed head below the frame is
  fixed by the posed-head framing correction (§Verified mechanism → Pose). Residual, still open: a
  user-supplied clip that carries a real **root-rotation** curve would break the world-space delta's
  orientation assumption — out of scope for the bundled upper-body clips, revisit if such a clip is used.
- **Gradient backdrop — deferred.** v1 ships a solid `SolidColor` clear (uniform bg also keeps the
  silhouette reference trivial); a runtime gradient is optional polish, render correctness doesn't depend
  on it.
- **Lighting constants** are taste defaults (named, obvious, top-of-file); expect the operator to tune
  key/fill/rim after seeing real output.
