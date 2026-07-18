# RenderThumbnail — design spec

An `avatar-tools` editor tool that produces a real-shader, cleanly-lit, optionally-posed 1200×900
portrait PNG of a **baked** avatar, to replace VRChat's default rest-pose upload thumbnail. Source
brief: `Atelier/kickoffs.md` block **U2**. Reader is the implementing agent — this records the
decisions, the load-bearing source facts, and the traps; it does not re-derive what the code shows.

## Scope

**In (this PR): the `RenderThumbnail` tool only.** It renders and writes a PNG to a temp path and
returns that path. It does **not** upload.

**Out (deferred follow-up — operator's call):** the upload wiring — a thumbnail-PNG parameter on
`UploadAvatar` that calls `VRCApi.UpdateAvatarImage` after the record exists, and the `upload-avatar`
skill step that renders-then-uploads. The tool is designed so that follow-up is trivial: it returns the
PNG path, and the seam (`VRCApi.UpdateAvatarImage(id, data, pathToImage)`) is already proven in-repo
(`references/ContinuousAvatarUploader/Editor/Uploader.cs:353-370`; the SDK method is
`AvatarProject/Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/API/VRCApi.cs:603`). No
blueprint IDs are in scope for this PR — rendering never touches an account.

## Why a bake is required (premise, confirmed)

At edit time MA/VRCFury have **not** unified the skeleton: `Merge Armature` "retargets bones exactly as
they sit in the scene and reconciles nothing" and is a build-time NDMF phase; VRCFury `Armature Link`'s
align options apply at build, not edit mode (`docs/nondestructive.md`). The reactive preview resolves
blendshapes / mesh-hiding / material-setters only — never skeletal merge. So sampling a pose onto the
base at edit time leaves merged clothing/hair in rest pose. The real build-on-a-clone is the only thing
that produces a posed avatar whose outfit follows the body. `RenderAvatar` is not the source: it renders
the live hierarchy + preview proxies through the Scene View with `sceneLighting=false` (headlight),
orthographic — "truthful for geometry/silhouette/clipping/fit, not matcap/rim/fresnel"
(`docs/unity-tools.md`). We always bake (locked decision below) so the portrait is uniformly the
"what-ships" avatar — VRCFury toggle side-effects resolved even when unposed.

## Locked decisions (operator sign-off)

| Decision | Choice | Note |
|---|---|---|
| Bake policy | **Always bake** | One code path; pose is just an optional clip sampled onto the baked clone. Bake cost ≈ a play-mode build, run once per upload. |
| Upload seam | **Post-upload `UpdateAvatarImage`** | Deferred to follow-up; decoupled, identical for first/re-upload, minimal additive change. |
| Framing | **SDK `PositionPortraitCamera`** | Bust (head+shoulders) from the descriptor's `ViewPosition`. Plus an optional `zoom` pull-back (below) so full-body posed shots are possible without a second path. |
| Pose source | **Author our own** `.anim` poses | No permissively-licensed curated portrait poses exist — corroborated by two independent searches. GoGo Loco / BUDDYWORKS are no-redistribution EULAs; Mixamo forbids standalone redistribution; CMU/ACCAD/Quaternius are motion libraries whose freeze-frames are mid-stride. Single-keyframe humanoid clips we author are 100% ours, committable, and retarget to any rig. |
| Lighting/bg | **Neutral 3-point rig + soft mid-gray gradient backdrop**, overridable | Deterministic (rendered in an isolated preview scene, no live-scene lights leak in). Constants are cosmetic taste defaults, not physics — freely tunable. |

## Verified mechanism (callables, from source)

- **Bake:** `nadena.dev.ndmf` → `AvatarProcessor.ManualProcessAvatar(GameObject)` (`AvatarProcessor.cs:105`,
  `[PublicAPI]`). **Clones internally, returns the fully-baked clone**; original untouched; runs all
  phases (MA + VRCFury + skeleton merge). Two traps: (1) it shifts the clone `+2` on Z; (2) it writes
  generated assets to a **persistent** `Assets/ZZZ_GeneratedAssets` folder in the host project — cleanup
  is on us (see Non-destructiveness). Do **not** use the in-place `ProcessAvatar(GameObject)` overloads —
  they mutate the passed root and don't clone.
- **Framing:** `VRC.SDKBase.VRC_AvatarDescriptor.PositionPortraitCamera(Transform)` (compiled in
  `VRCSDKBase.dll`; no source — call sites: SDK `RuntimeBlueprintCreation.cs:47`, CAU `Uploader.cs:615`).
  Sets the camera **transform only** (position/rotation for bust framing from `ViewPosition`); we set FOV
  / clip planes / culling mask ourselves, as CAU does.
- **Pose:** `AnimationMode.BeginSampling()` / `AnimationMode.SampleAnimationClip(bakedClone, clip, time)` /
  `AnimationMode.EndSampling()`, bracketed by `StartAnimationMode()` / `StopAnimationMode()`. Works on the
  baked clone (clip binding paths resolve against the merged hierarchy). Nothing in-repo uses it yet —
  fresh introduction. Sample a single frame (time = clip.length or a chosen still frame; our authored
  clips are one keyframe).
- **Capture + lifecycle model:** mirror CAU `TakePicture` (`Uploader.cs:592-645`) — a disabled `Camera`
  with `HideFlags.DontSave`, render into a `RenderTexture` → `Texture2D.ReadPixels` → `EncodeToPNG`, all
  wrapped in `using`/RAII (`DestroyLater<T>`, `PreviewSceneScope` in CAU `Utils.cs`). We render in a fresh
  `EditorSceneManager.NewPreviewScene()` (not the live scene) for deterministic lighting.

## Tool contract

`[AgentTool] public static class RenderThumbnail`, one door:

```csharp
public static string Render(
    string target,            // avatar root: scene path or name (resolve like RenderAvatar's target)
    string pose = null,       // null/"none"/"unposed" => floor; a bundled pose name; or a clip asset path/GUID
    float  zoom = 1.0f,       // pull-back factor over PositionPortraitCamera bust framing; >1 shows more body
    string bg   = null)       // null => default gradient backdrop; a hex color => solid background
```

- **Returns** a one-line legible verdict string ending with the PNG path, e.g.
  `RenderThumbnail: baked (MA+VRCFury resolved), pose 'contrapposto', 1200x900 -> <temp>/renderthumbnail_<label>_<stamp>.png`.
  Fail loud: an empty silhouette, a missing descriptor, an unresolvable pose, or a bake exception is a
  thrown/verbose error, never a silent blank PNG.
- **Output:** fixed **1200×900** (VRChat 4:3 thumbnail aspect), PNG, written to
  `Application.temporaryCachePath` (outside `Assets/`, like RenderAvatar). Resolution is not a param
  (YAGNI); the SDK re-crops on upload anyway.
- **`pose` resolution order:** exact `none`/`unposed`/null → floor (no sampling); else try a bundled pose
  name under the package's `Poses/` folder; else treat as an asset path or GUID and load the clip. The
  path/GUID branch is the extension point for referencing an installed paid clip later **without
  committing it** — the deferred P2 upside.

## Render pipeline

1. Resolve `target` to the live avatar GameObject; assert it has a `VRC_AvatarDescriptor`.
2. Snapshot the pre-existing `Assets/ZZZ_GeneratedAssets` contents (for cleanup bookkeeping).
3. `bakedClone = AvatarProcessor.ManualProcessAvatar(target)`.
4. `preview = EditorSceneManager.NewPreviewScene()`; `SceneManager.MoveGameObjectToScene(bakedClone, preview)`.
5. Build the lit stage in `preview`: 3-point rig (key/fill/rim directional lights + modest neutral
   ambient) and a backdrop — a runtime-generated vertical-gradient unlit quad, or a solid quad when `bg`
   is a hex color. All objects `HideFlags.DontSave`, parented so teardown is one destroy.
6. If posing: `StartAnimationMode()` → `BeginSampling()` → `SampleAnimationClip(bakedClone, clip, t)` →
   `EndSampling()` (leave animation mode on until after capture; stop in `finally`).
7. Camera: disabled `Camera`, `camera.scene = preview`, set FOV/clip/cullingMask, then
   `descriptor.PositionPortraitCamera(camera.transform)`; apply `zoom` by moving the camera back along its
   local forward (bust → more body as zoom rises). Render into a `RenderTexture` → `ReadPixels` →
   `EncodeToPNG` → write PNG.
8. `finally`: stop animation mode; `ClosePreviewScene(preview)` (destroys clone + rig + backdrop);
   `DestroyImmediate` any stragglers; **clean `ZZZ_GeneratedAssets`** — delete only assets our bake added
   (or the whole folder iff it didn't exist at step 2). Leave the project as found.

Every step is `using`/`finally`-guarded so an exception mid-pipeline still tears the preview scene and the
clone down and restores the asset folder — non-destructiveness must survive failure.

## Owned pose assets

- Bundle in the package: `com.ryan6vrc.avatar-tools/Editor/Poses/` (or a package-root `Poses/`), as
  single-keyframe **humanoid** `.anim` clips. v1 set: `unposed` is the implicit floor (no asset); plus
  **contrapposto** and **hand-on-hip** — authored **upper-body-focused**, since bust framing crops the
  legs. Author by hand-posing the humanoid rig in Unity and saving the muscle-space clip; a Quaternius
  CC0 idle frame may seed a starting stance (the one asset we could also keep as reference), but the
  committed poses are ours.
- Humanoid muscle-space clips retarget to any rig with no per-avatar bake — one clip works across every
  avatar. The bake is per-upload (skeleton merge); the clip is universal.

## Non-destructiveness & the Plum-Remy test venue

- The **live authoring scene is never mutated**: `ManualProcessAvatar` clones, and we move the *clone*
  (not the original) into the preview scene. The original avatar, its scene, and vendor assets are
  untouched.
- The one real residue is `Assets/ZZZ_GeneratedAssets` in the host project — clean it per step 8. This is
  the only way the tool writes into a host project; the cleanup is mandatory, not best-effort.
- **Venue** (`worktree-unity-editor` convention): host this worktree's `com.ryan6vrc.avatar-tools` in the
  live **Plum-Remy-3.0** editor (`@6401`) — a real MA/VRCFury-composed avatar, the merged-skeleton case
  the tool must handle. Back up `Packages/manifest.json`, repoint the one package at the worktree
  (absolute `file:` path), `Resolve` + `Refresh`; after verifying, **restore the manifest** and
  re-resolve, and clean any `ZZZ_GeneratedAssets` the test bakes left in Plum-Remy's `Assets/`. Leave the
  editor as found. Its blueprint IDs are real and never enter a commit (and this PR doesn't upload, so
  none are even read).

## Verification

Two tiers (rendering inherently mutates, so most behavior is proven live, not in NUnit —
`vrc-unity-tools-editmode-batchmode`, `subagent-editmode-verify-serial`):

- **Headless EditMode** (`tools/run-editmode-tests.ps1` against the generated `TestEditor`, run serially):
  pure helpers only — `pose` resolution order, the `zoom` camera-offset math, `ZZZ_GeneratedAssets`
  cleanup bookkeeping (delete-only-our-additions), param/verdict formatting, and fail-loud branches
  (`LogAssert.Expect` on the error paths). Do not put the bake/render/sample in NUnit — it mutates live
  objects and would crash the suite.
- **Live `execute_code`** against the Plum-Remy composed avatar: call `RenderThumbnail.Render(...)` for
  unposed and for each authored pose, then surface the returned PNGs to the operator (SendUserFile) — the
  eyeball is the real gate that the posed portrait renders correctly on a merged-skeleton avatar with real
  shaders and clean lighting. Compare against the status-quo default thumbnail to confirm the win.

## Open risks / notes

- **Pose vs. bust framing:** `PositionPortraitCamera` reads static `ViewPosition`, not the posed head, so
  a large pose could misframe slightly; upper-body-focused poses + the `zoom` param keep this minor.
  Acceptable for v1; revisit if a pose visibly breaks framing.
- **Gradient backdrop:** if the runtime-generated gradient proves fiddly, ship the solid neutral fallback
  first and add the gradient as polish — the render correctness doesn't depend on it.
- **Lighting constants** are taste defaults; expect the operator to tune key/fill/rim after seeing real
  output. Keep them as named, obvious constants at the top of the file.
