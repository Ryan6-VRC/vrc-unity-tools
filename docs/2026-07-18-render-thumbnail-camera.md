# RenderThumbnail — camera rework (head-anchored framing)

Design spec for the second camera pass, shipping with the bundled pose library.

## The bar, and the budget

This tool is a **cheap stand-in for someone who needs an upload thumbnail and won't make custom
art**. It replaces ContinuousAvatarUploader's default, which is a very low bar to clear. It is not a
portrait studio.

That sets the design budget, and the budget is the most load-bearing decision in this document:

- **Heuristics live in the caller, not the tool.** The agent driving this can reason about a specific
  avatar and pose, and the skill can fire two test shots and show them to the operator. Rendering is
  cheap; a thousand lines defending against a case the operator can see and correct in one round-trip
  is not. Prefer a parameter the caller can adjust over machinery that infers.
- **A good default beats a smart algorithm.** Where a review found a case the geometry handles badly,
  the fix of choice is a better constant, not a new subsystem.
- **Only fail loud on what the caller cannot see.** A blank PNG is invisible in a verdict line and
  must fail. A slightly-off crop is visible in the image and must not.

Two review rounds produced correct findings that this budget deliberately declines; §Declined records
them so they aren't re-proposed.

## The problem

The v1 camera is the SDK's `VRC_AvatarDescriptor.PositionPortraitCamera`. Measured black-box — feed a
scratch descriptor known view heights and read back where it puts the camera — it places the camera at
root-local `(0, 0.8·ViewPosition.y, 0.7·ViewPosition.y)` and rotates it by the descriptor's serialized
`portraitCameraRotationOffset`, which defaults to 180° about Y.

So it consumes the viewball's **height only** — `ViewPosition.x` and `.z` are ignored — and it aims
**dead level**, back toward the avatar's vertical axis at a height of 0.8×view height. That is chest
or chin on most rigs: the head is always *above* the aim line, never on it. The portrait works only
because a 60° cone from that distance happens to contain a head, which is why FOV is pinned at Unity's
default and why `framing` is a metre dolly calibrated against it — move either and all three framings
break.

Two further measured facts the callers depend on. The offset is written back into the serialized field
on first call, so merely calling it mutates the descriptor. And it is **root-relative, therefore
pose-blind**: v1 patches that with `poseHeadDelta`, a translation following the head's position but
not its rotation, so a pose that turns the head photographs a cheek. Poses in the bundled library
deliberately turn heads.

## Contract

```csharp
Render(string target, string pose = null, string expression = null,
       string framing = "bust", string bg = null,
       float fov = 30f, float? yaw = null,
       bool whatIf = false)
```

| Parameter | Meaning |
|---|---|
| `fov` | Vertical FOV in degrees, range [10, 90]. Distance is solved from it, so framing is unaffected. |
| `yaw` | `null` ⇒ the automatic oblique below. A number ⇒ **an offset added to the head-tracking term**, degrees, positive orbits the camera to the avatar's left. Unclamped and never re-signed. |

Both validate up front alongside `framing`/`bg`, so `whatIf` rejects a bad value rather than letting
the render path be the first to fail. The `fov` bounds are about the solved distance, not the far
clip: at 90° a bust frame puts the camera 0.23 m from the view point — inside the hair mesh, which
renders as hair interior and passes the empty-frame guard.

**`yaw` is an offset, not an absolute heading.** `yaw: 0` does not mean "frontal" — it means "track
the head with no oblique". This is the honest description of the formula below, and it keeps the
`yaw=null` and explicit paths from being two different geometries.

**Sign convention, measured and stated once.** All angles here are rotations about world **+Y**,
signed as `atan2(v.x, v.z)`. Positive therefore points toward world **+X**, which the shipped light-rig
comment correctly documents as **screen-left** for a camera on the +Z side. Positive `camYaw` orbits
the camera toward +X, and positive `headYaw` turns the face toward +X — the same sense, which is what
makes the camera track rather than counter the head. Most bundled poses turn the head the other way
(negative).

## The automatic oblique

`yaw = null` applies `DefaultOblique = 18°`, signed to the side the face turns toward.

The sign is the load-bearing half. Putting the camera on the side the face turns *toward* brings the
face frontal to the lens while leaving the shoulders oblique — the classic portrait geometry. An
unsigned constant would compound one direction and *cancel* the other.

Zero would be worse still: poses whose head faces straight ahead would shoot dead axial, with both
shoulders square.

The magnitude is bounded on both sides, which is why it is small. On a pose whose head is not turned,
the oblique angle *is* the angle the avatar looks past the camera: 18° still reads as eye contact,
35–40° reads as staring into the middle distance, which sells an avatar worse than a frontal shot
does. Below roughly 10° the shoulder line stops reading.

**Not randomized, deliberately.** Re-rendering an avatar must reproduce the frame, or a contact sheet
cannot hold the camera as a control and no lighting or FOV change can be A/B'd. Variety comes from
the poses' own head and torso angles. A caller wanting a specific alternate angle passes `yaw`.

## Camera geometry

**Target: the view point, re-parented onto the head.** The descriptor's `ViewPosition` is
root-local; its world position is `root.TransformPoint(ViewPosition)`. **Compute this on every path,
outside the pose branch** — the floor path (`pose = null`) is the default, never touches the Animator
in the shipped code, and must keep rendering without a head bone or a humanoid assertion. Only the
head-local round-trip is inside the pose branch: convert the world view point into head-bone local
space **after `animator.Rebind()`**, sample, then transform back out. Rest means bind pose, because
that is the base the sampled clip is applied to; anchoring to the pre-Rebind scene pose bakes in a
per-avatar constant that would bias every angle below.

Eye bones are not used: many rigs lack them and NDMF bakes can rename them, whereas Head is mandatory
on the humanoid rig the pose path already asserts.

**Orientation.** Head yaw and pitch are measured as a **delta from the bind pose**, not from absolute
bone orientation — Unity does not normalize humanoid bone axes, so a rig whose head bone points +Y
would otherwise yield a constant follow term and ship a profile shot labelled as a bust. Capture
`restHeadRot` after `Rebind`, `posedHeadRot` after sampling. Both angles must be **wrapped signed**:
`Mathf.DeltaAngle(0f, q.eulerAngles.y)`, not the raw `[0,360)` euler, or a head turned 20° one way
reads as 340° and inverts both the tracking term and the oblique's sign.

```
headDelta  = posedHeadLocal * Inverse(restHeadLocal)     // both already in the root's basis
deltaFwd   = headDelta * forward
headYaw    = YawOf(deltaFwd)                             // atan2(x,z); 0 on the floor path
headPitch  = PitchOf(deltaFwd)                           // asin(y); positive = chin raised
sign       = headYaw < -ObliqueDeadband ? -1 : +1        // ObliqueDeadband = 5°
shotOffset = yaw ?? DefaultOblique * sign
camYaw     = clamp(headYaw, ±60°) + shotOffset
camPitch   = clamp(-headPitch * PitchFollow, ±20°)       // PitchFollow = 0.5; negated, see below
```

**Take the delta rotation first, then extract once.** Extracting an angle from the rest and posed
forward vectors and subtracting is *not* equivalent — it cancels a constant offset but not the axis
dependence, and it fails hardest on the most common rig convention. Measured, for a 30° turn and a 20°
chin raise applied to a head bone whose +Z runs **up the neck** (Blender orients a bone along its
length, so this is most of the VRChat population): the subtract form returns yaw **0.0** — tracking
silently dead, every shot reduced to the bare oblique — and pitch **−20°**, which would photograph a
chin-up pose from below. The delta form returns 30°/+20° for every rest orientation, arbitrary skew
included. Extraction still runs off the forward vector rather than eulers: no wrap or gimbal question.

`camPitch` is **negated** because a positive rotation about +X lowers the camera, and a chin-raised
pose must be shot from slightly above.

**The camera tracks the head one-for-one; there is no follow coefficient.** At tracking coefficient
`k` the face sits `(k−1)·headYaw + DefaultOblique` off the lens — so at any `k < 1` that angle is a
*function of the pose*, sweeping from dead-frontal to fully looking-away across one contact sheet
(the bundled clips span 0–29° of head yaw, which is the whole range). At `k = 1` it collapses to
exactly `DefaultOblique` on every pose. That is the point: the face-to-lens angle is the thing worth
controlling, and `k = 1` is the only value that makes it a constant we choose rather than a
by-product of the clip. Shoulder obliquity is then set by the pose's own head-versus-shoulder
relationship, which is the part that reads as expression. One constant deleted.

**The deadband is not optional.** Measured across the bundled library, 9 of 23 poses turn the head
less than 5° and 6 less than 2° — most of these poses simply face forward. A bare `sign()` would let
retarget noise decide a 36° camera swing, and the same pose would photograph from opposite sides on
two avatars in one contact sheet. The deadband resolves that population to a consistent side.

**The poses supply almost no torso twist**: shoulder-line yaw peaks at 7.7° across all 23 clips and
is under 3° for most. Shoulder obliquity therefore has to come from the camera, which is the
strongest argument for the oblique being a non-zero constant rather than something inferred from the
pose. It also means any future pose authored with a real torso twist will read *more* strongly than
anything currently in the library, not less.

**Position and rotation.** Angles apply in the posed clone's root basis, so an avatar sitting rotated
in the scene still photographs frontally — the case that matters most on the floor path, where no
sampling overwrites root rotation:

```
orbit       = AngleAxis(camYaw, Vector3.up) * AngleAxis(camPitch, Vector3.right)
screenRight = rootRot * (orbit * Vector3.left)
aim         = viewpoint - Vector3.up * (span * AimDrop[framing])
                        + screenRight * (span * 0.06) * -sign
cam.position = aim + rootRot * (orbit * (Vector3.forward * distance))
cam.rotation = LookRotation(aim - cam.position, Vector3.up)
```

`Vector3.forward`, not `back` — the camera belongs in front of an avatar that faces +Z. Compute
`orbit` and `screenRight` before `aim`; `aim` depends on the camera's basis but not on its position,
so the apparent circularity resolves in that order. Camera up is world up: roll never follows, or
every head-tilt pose reads as an accidental dutch angle.

**Verify the pitch sign by rendering, not by reading.** The intended outcome is that a chin-**raised**
pose photographs from slightly **above** (a chin-up pose shot from below is a nostril shot).
`HandsOnHips` is measured as the library's strongest chin-up (+29°) and `DanceSweep` its strongest
chin-down (−29°); check the sign on those two. At `PitchFollow = 0.5` the resulting camera pitch
peaks near 15°, so the ±20° clamp never fires on this library — it is a guard for user clips, not a
tuned value.

**Distance and aim.** `framing` names a subject height, scaled to the avatar so "bust" means the same
picture on a 1.2 m and a 1.9 m rig:

```
viewHeight = descriptor.ViewPosition.y * root.lossyScale.y   // pose-independent, captured pre-sample
span       = BaseSpan[framing] * (viewHeight / 1.6f)
distance   = (span / 2) / tan(fov / 2)
```

| framing | `BaseSpan` | `AimDrop` |
|---|---|---|
| bust | 0.45 | 0.12 |
| half | 0.90 | 0.12 |
| full | 1.90 | 0.37 |

`viewHeight` is the **descriptor's** view height, not the posed view point's world height — the
latter changes when the avatar sits, which would shrink the span on exactly the seated poses and
destroy cross-pose comparability. Root scale must be applied: `span` is a world metre quantity
feeding `distance`, and VRChat avatars are routinely scaled at the root.

`AimDrop` is per-framing because the anchor (the eyes) sits near the *top* of the subject, not its
centre. One coefficient cannot serve three spans: at 0.12 a `full` frame cuts the feet off at
mid-shin and leaves half a metre of empty sky above the crown.

The lateral term gives the gaze somewhere to go in a 1200×900 landscape frame whose width is
otherwise wasted on a vertical subject. It keys on the sign of **`shotOffset`** — the same quantity
that drives the orbit — so the subject shifts opposite the way the camera swung and the room opens in
front of the gaze. Keying it off the head's own turn instead would let an explicit `yaw` orbit one way
while the looking-room opened the other: `yaw: -30` against a +10° head turn shifted the subject
*into* its own gaze. `yaw: 0` means no oblique, and therefore no lateral shift.

**Creator portrait-camera overrides are deliberately ignored.** `portraitCameraPositionOffset` is
serialized and a creator may have posed it, but a fixed root-relative offset cannot follow a pose,
which is the entire point of this rework. Dropping the SDK call also drops an incidental descriptor
mutation — it writes its computed default back into the field.

## Lighting

**The rig yaws with the camera.** It is world-fixed and calibrated to a camera on the avatar's +Z
side; orbiting without it lights yawed shots from behind. Rotate `lightHolder` about world Y by
`rootYaw + camYaw`, **after** all three lights are parented (`MakeLight` sets world rotation and then
parents with `worldPositionStays`). Yaw only. Every pose is then lit identically, which is the right
trade for a never-think-about-it default.

**Cut the level.** These avatars are lilToon/Poiyomi, calibrated so a single directional near 1.0 is
correct exposure; the shipped rig totals 2.7 with `allowHDR = false`, which clips hard and plausibly
renders every face flat white. Drop to roughly 1.2 and re-verify against one lilToon and one Poiyomi
base during the evaluation sweep. Two constants, and it gates whether any other tuning is judgeable.

## Ordering

Step 5 splits. The world view point and `viewHeight` are computed on every path before sampling; the
head-local round-trip and `restHeadRot` are captured after `Rebind` and inside the pose branch.
Step 5b's expression cannot disturb the geometry — expressions are written straight onto renderer
blendshape weights, never sampled, precisely so they don't re-run the humanoid solver. `poseHeadDelta`
and the comment block justifying it are deleted, not left beside the new mechanism.

No render this produces is byte-identical to a v1 render — FOV, distance solve, aim offsets, light
level and rig yaw all change. Any test or comment asserting the old floor-path invariant goes with
it. What replaces it is run-to-run determinism: same avatar, same pose, same parameters, same PNG.

## Background

`bg` stays a single string. Hex only, no named presets:

- `#RRGGBB` / `#RRGGBBAA` — solid, unchanged.
- `#TOP:#BOTTOM` — vertical two-stop gradient. The `:` is unambiguous against `#RRGGBBAA`.

Draw the ramp with a `CommandBuffer` at `CameraEvent.BeforeForwardOpaque`, keeping
`clearFlags = SolidColor`. Blitting into the render texture before `cam.Render()` and clearing depth
only would rely on colour contents surviving the render-target switch, which is not contractual on an
MSAA target and can differ between an editor and a batchmode run. Drawing inside the camera's own
pass, after its clear, has no such dependency.

**Set `cam.renderingPath = RenderingPath.Forward`.** The default follows project tier settings, and
under Deferred that camera event never fires — the gradient silently doesn't draw, the background is
the solid clear colour, and the empty-frame guard passes it happily. One line, on a camera the tool
owns exclusively.

The ramp source is a 1×N `Texture2D` at the **project default (sRGB), not linear** — measured: an sRGB
ramp blitted to the sRGB target round-trips byte-exact (`#3A4A6A` in, `(58,74,106)` out) and
interpolates in the intuitive gamma space, while a linear-constructed one would ship double-encoded.
Both it and the `CommandBuffer` need teardown entries beside `rt` and `tex`; a contact-sheet run of
23 poses × 4 bases otherwise leaks 92 of each in one editor session.

A backdrop quad was rejected (scene geometry catches light, clips, and couples to framing distance),
as was `RenderSettings.skybox` (preview-scene sharp edge, drags in pipeline setup, and the tool's
no-global-writes discipline forbids it).

## Verdict and the empty-frame guard

The reported `silhouette=NN%` is dropped and replaced by the camera solve's own numbers, which are
what a caller actually needs to judge a frame it cannot see:

```
[RenderThumbnail] Render <label> baked pose=<name> expression=<name|none> framing=bust
  fov=30 headYaw=-8.4 camYaw=-26.4 head=(0.45,0.62) => OK | png=<path>
```

`headYaw` is what the pose itself did; `camYaw` is the **resolved** angle. Both are reported because
one without the other is not decomposable: `camYaw − headYaw` is the offset a caller would pass as
`yaw` to reproduce the shot, and their disagreement is how a saturated tracking clamp becomes visible
rather than silent. `head` is
`WorldToViewportPoint(viewpoint)` — the **view point**, not `aim`: the camera is aimed exactly at
`aim` by construction, so projecting that would print `(0.5, 0.5)` forever and the token would be
decoration. Report it; do not fail on it. A head near or past a frame edge is visible in the image,
and the budget says the caller's eye owns that call — failing would also withhold the PNG that would
have shown them.

The blank-frame guard stays, because that failure *is* invisible in a verdict line — it would
otherwise ship as `=> OK | png=…`. Keep it a boolean, and **keep sampling the rendered image** for
the reference: each row's own column-0 pixel, not a computed ramp value and not v1's single corner.
A vertical ramp is constant per row, so this is exact for both solid and gradient backgrounds and
colour-space-agnostic.

Both alternatives are measurably broken once gradients ship. Replaying all three rules over a **blank
gradient frame**: per-row reads 0% and fails correctly; v1's single-corner reference reads **77%
drawn** and would pass it as `=> OK`; a computed reference re-arms the gamma bug this code already
hit and documented. The per-row change is what keeps the guard alive, not a tidy-up.

## What this obligates elsewhere

Landing this leaves stale text in five places; all are edited in the same PR.

- `2026-07-17-render-thumbnail-design.md`: §Locked decisions rows *Framing* and *Lighting/bg*;
  §Verified mechanism → **Framing** (which states "**never** via FOV" as a source-verified trap — the
  direct contradiction) and → **Posed-head framing correction**; §Tool contract's signature block;
  §Render pipeline steps 4, 5 and 6; §Open risks → *Pose framing — resolved* and *Gradient backdrop —
  deferred*.
- `vrc-skills/skills/shoot-thumbnail/SKILL.md`: §"Pick the backdrop" is built on `silhouette=NN%` as
  its backdrop-contrast tell, plus references in §"Read the verdict" and a stale call signature. The
  gradient `bg` form and the new `camYaw`/`head` tokens replace that heuristic.
- `Tests/Editor/VerificationSnippets.md`: its observed-pass lines match on `silhouette=`, a token no
  path emits any more, so an operator following that runbook verifies against something that cannot
  appear.
- `Tests/Editor/RenderThumbnailTests.cs`: `FramingDistance` becomes `FramingGeometry`, yielding span
  *and* aim drop as a pair — a dolly distance and a subject span are different quantities, and an
  ordering-only assertion would stay green across the semantic swap, so the spans and drops are pinned
  by value. `TryParseBg` returns two colours. The angle extraction's **rest-orientation invariance** is
  the property worth a test: canonical-direction checks on `YawOf`/`PitchOf` cannot catch an
  axis-dependent caller by construction.
- `TOOLS.md` and `README.md` in the meta-repo carry the tool's parameter surface.

## Declined

Review findings that are correct but bought less than they cost:

- **Shoulder-relative framing** — keying the orbit to the camera-to-shoulder angle. One-for-one head
  tracking plus the signed oblique reaches the same geometry without a second bone-measurement path.
- **Grounding the floating sit poses** — no bundled clip carries root translation, so seated poses
  hover at standing hip height. Head-anchored framing hides it at `bust`; the caller sees it at once
  otherwise. The skill should prefer `half` over `full` for seated poses, which is a documentation
  line, not code.
- **A continuous ramp on the lateral offset** — a step keyed to a stable, non-zero sign is enough.
- **`whatIf` running the unposed camera solve** — real preflight value, but a test shot is cheap and
  answers more.
- **Viewball sanity-check with head-bone fallback** — a badly-placed viewball produces a visibly bad
  shot, which is exactly the case the operator loop catches.
- **Off-centre projection matrix for composition** — the aim-point offsets buy the same framing
  without a custom frustum.
- **Per-shot lighting variation and shadows** — shadows are off; hand-near-face poses (8 of 23) would
  read better with a soft key shadow. One flag, worth trying during the sweep, not worth a system.

## Unsettled

Resolved by eye during the pose library's final evaluation, which re-renders 23 poses × 4 clothed
bases regardless: `fov`'s default within roughly 20–40 (sweep it and report solved distance alongside
degrees — distance is what governs the look), `DefaultOblique` near 18 (judge it on a pose whose head
faces forward, where the oblique *is* the angle the avatar looks past the lens — that is the case
that bounds it), `ObliqueDeadband` near 5°, `PitchFollow` near 0.5, the `AimDrop` values, total light
intensity near 1.2, and whether key shadows are on.

The sign convention and the head/shoulder angles above are **measured**, not inferred — sampled off
each clip on a real humanoid rig (`_study/pose-angles.md` on the NAS carries the full table). An
earlier reading of the clips as "torso twist plus an opposing counter-turn" is wrong for this rig:
the terms add. Re-measure with that probe if the library changes.
