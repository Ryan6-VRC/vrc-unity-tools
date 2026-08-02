# CheckSeam — recorded acceptance baseline

`CheckSeam` is the mechanical compose-fit gate. It counts weighted humanoid bones and gates on edit-time world-position coincidence: **≤1 weighted humanoid → REFUSE** (offset-tolerant proxy); **≥2 → PASS if all within ε, else NOT-PASS**.

Two doors share that gate: `Check` reflects the seam mapping (MA `GetBonesMapping` / VRCFury `GetLinks`) and derives ε = `max(0.5mm, 0.3%·Hips→Head span)`; `CheckBare` matches pairs by bone name and takes ε from the caller. Everything below is `Check`'s baseline — §The bare door says why that door has no rows here.

## Regression baseline (live corpus, measured 2026-07-11, a personal avatar project)

The *only* validation of the MA/VRCFury reflection defaults — EditMode unit tests inject fake seams. `TestEditor` does carry MA/VRCFury (`test-venue-common.ps1`'s community tier), but a package present is not a seam authored: the venue has no composed avatars for `GetBonesMapping`/`GetLinks` to resolve against, so only real assets exercise those paths. Each row was run by driving the compiled `CheckSeam.Check(base, mergeable)` via `execute_code`, staging the mergeable as an identity child of the base in a throwaway scene. A re-run should reproduce the token + reason below; a divergence is a regression to investigate, not a baseline to silently update.

| mergeable ← base | base GUID / span | mergeable GUID | seam | result |
|---|---|---|---|---|
| Shinano_Stockings ← Shinano_kisekae | `a0f3ced80a65ee64cbc31500a497fe44` / 340mm | `1b101c73ea993b34e83816d8a7cb1aa7` | MA | **PASS** — `weightedHumanoid=50 offenders=0 context=2 dropped=8` |
| CostumeBambino ← Personal_kisekae | `9bbab1857358e684a924c85b2174242a` / 334mm | `21abba6203db9ab4e89422db8bc5183c` | MA | **NOT-PASS** — `weightedHumanoid=6 offenders=6` (`edges`-scaled outfit, wrong base) |
| Hair_Shiori ← Personal_kisekae | `9bbab1857358e684a924c85b2174242a` / 334mm | `a9808f9d78697104b8d6ee94419a900f` | VRCFury ×2 | **REFUSE** — `seams disagree on base bone 'Head' (…/Armature/…/Head vs …/Armature.Shiori/…/Head)` |
| CarriedDoll_Prefab ← Personal_kisekae | `9bbab1857358e684a924c85b2174242a` / 334mm | `2c75de3f38a1da949a3b9bbe22b257cb` | VRCFury ×7 anchors | **REFUSE** (warning) — `seam present but does not resolve onto this base (likely an incompatible or independent rig): …Failed to find object at path 'Armature/…/Head_NoChop'` |

All four verdicts hit their expected token; the reflection defaults (`GetBonesMapping`, `GetLinks`, the scale/severity paths) are proven on real composed assets. The full model is covered: a scored PASS, a scored NOT-PASS, and both REFUSE flavours (conflict + unresolvable-abstain).

## Two corpus-prediction corrections the live run surfaced (design-doc predictions were wrong, not the tool)

- **Shiori** was predicted a single-bone proxy → REFUSE(proxy). It actually ships **two** VRCFury `ArmatureLink` components (`Armature` + `Armature.Shiori`) both mapping base `Head`, so the **conflict** guard fires first. REFUSE is still the correct outcome for a dual-armature hair; the reason is more specific than "proxy".
- **CarriedDoll** was predicted seamless → REFUSE(no-seam). It actually has **seven** `ArmatureLink` anchors (it is the drop-on-player gimmick); one `GetLinks` throws resolving onto the base. This drove the `TargetInvocationException`-unwrap + drift-vs-unresolvable severity split (`fix` commit): a seam that can't resolve onto this base is a **warning-level abstain**, not an error.

## The bare door

`CheckBare` has no corpus rows and needs none. The corpus exists because `Check`'s pair collection is vendor reflection, which only real MA/VRCFury assets can prove; `CheckBare` collects pairs by bone name and touches no vendor package, so every path it owns is reachable from the EditMode suite. That suite is its whole guard — treat a gap there as a gap in the door, not as something a live run would cover.

The one thing a live run still owns for both doors is calibration: what tolerance a given caller should pass. `CheckBare` takes ε from the caller precisely because the tool cannot know that, and the known regimes sit orders of magnitude apart (a warp solver's residue against millimetre-scale pre-seam staging). A caller's tolerance belongs in the skill that measured it — `mochifit`'s is `docs/mochifitter.md`'s.

## Notes for a re-runner

- `-Tag CheckSeam` is only an output label; run `-Filter CheckSeamTests` to isolate the EditMode suite (28 tests, all green).
- The corpus deltas straddle ε by orders of magnitude, so the corpus is an end-to-end plumbing check, **not** the ε calibration guard — the synthetic ε±δ and 0.09/0.11-weight unit brackets are that guard.
- Documented residuals (Rule 2, not fixed): finger-rigged handwear across non-uniform bases → advisory NOT-PASS at the fingers; Head+Neck hair on head-swaps → may NOT-PASS; a PASS certifies the humanoid skeleton coincides, not physics-cage/bust/hair/accessory placement.
