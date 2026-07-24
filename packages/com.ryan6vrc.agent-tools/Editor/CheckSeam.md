# CheckSeam — recorded acceptance baseline

`CheckSeam` is the mechanical compose-fit gate. It reflects the seam mapping (MA `GetBonesMapping` / VRCFury `GetLinks`), counts weighted humanoid bones, and gates on edit-time world-position coincidence: **≤1 weighted humanoid → REFUSE** (offset-tolerant proxy); **≥2 → PASS if all within ε, else NOT-PASS**. ε = `max(0.5mm, 0.2%·Hips→Head span)`.

## Regression baseline (live corpus, measured 2026-07-11, a personal avatar project)

The *only* validation of the MA/VRCFury reflection defaults — EditMode unit tests inject fake seams because the SDK-only `TestEditor` has no MA/VRCFury. Each row was run by driving the compiled `CheckSeam.Check(base, mergeable)` via `execute_code`, staging the mergeable as an identity child of the base in a throwaway scene. A re-run should reproduce the token + reason below; a divergence is a regression to investigate, not a baseline to silently update.

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

## Notes for a re-runner

- `-Tag CheckSeam` is only an output label; run `-Filter CheckSeamTests` to isolate the EditMode suite (15 tests, all green as of this baseline).
- The corpus deltas straddle ε by orders of magnitude, so the corpus is an end-to-end plumbing check, **not** the ε calibration guard — the synthetic ε±δ and 0.09/0.11-weight unit brackets are that guard.
- Documented residuals (Rule 2, not fixed): finger-rigged handwear across non-uniform bases → advisory NOT-PASS at the fingers; Head+Neck hair on head-swaps → may NOT-PASS; a PASS certifies the humanoid skeleton coincides, not physics-cage/bust/hair/accessory placement.
