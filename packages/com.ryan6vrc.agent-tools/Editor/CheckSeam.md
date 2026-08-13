# CheckSeam — recorded acceptance baseline

What `CheckSeam` gates and how, both doors, is `unity-tools.md`'s contract. This file records one thing that lives nowhere else: what the tool actually returned on real composed avatars.

Scope: `CheckSeam.Check` only. `CheckBare` touches no vendor package, so it is owed no corpus rows — but a green suite is not the same as full coverage, and the shape that escaped it was a *scene shape*, not a branch: a fixture builder only builds what someone thought to build.

## Regression baseline (live corpus, measured 2026-07-11, a personal avatar project)

The only validation of the reflection defaults **against authored assets**. `CheckSeamLiveTests` drives the same `CollectMaPairs`/`CollectVrcfPairs` against synthesized MA/VRCFury components and catches collector drift in CI; what it cannot prove is what a shipped outfit actually authors — the seven-anchor gimmick, the dual armature, the scaled bake. That is what these rows hold. Each row was run by driving the compiled `CheckSeam.Check(base, mergeable)` via `execute_code`, staging the mergeable as an identity child of the base in a throwaway scene. A re-run should reproduce the token + reason below; a divergence is a regression to investigate, not a baseline to silently update.

| mergeable ← base | base GUID / span | mergeable GUID | seam | result |
|---|---|---|---|---|
| Shinano_Stockings ← Shinano_kisekae | `a0f3ced80a65ee64cbc31500a497fe44` / 340mm | `1b101c73ea993b34e83816d8a7cb1aa7` | MA | **PASS** — `weightedHumanoid=50 offenders=0 context=2 dropped=8` |
| CostumeBambino ← Personal_kisekae | `9bbab1857358e684a924c85b2174242a` / 334mm | `21abba6203db9ab4e89422db8bc5183c` | MA | **NOT-PASS** — `weightedHumanoid=6 offenders=6` (`edges`-scaled outfit, wrong base) |
| Hair_Shiori ← Personal_kisekae | `9bbab1857358e684a924c85b2174242a` / 334mm | `a9808f9d78697104b8d6ee94419a900f` | VRCFury ×2 | **REFUSE** — `seams disagree on base bone 'Head' (…/Armature/…/Head vs …/Armature.Shiori/…/Head)` |
| CarriedDoll_Prefab ← Personal_kisekae | `9bbab1857358e684a924c85b2174242a` / 334mm | `2c75de3f38a1da949a3b9bbe22b257cb` | VRCFury ×7 anchors | **REFUSE** (warning) — `seam present but does not resolve onto this base: …Failed to find object at path 'Armature/…/Head_NoChop'` |

All four verdicts hit their expected token; the reflection defaults (`GetBonesMapping`, `GetLinks`, the scale/severity paths) are proven on real composed assets. The full model is covered: a scored PASS, a scored NOT-PASS, and both REFUSE flavours (conflict + unresolvable-abstain).

## Two corpus-prediction corrections the live run surfaced (design-doc predictions were wrong, not the tool)

- **Shiori** was predicted a single-bone proxy → REFUSE(proxy). It actually ships **two** VRCFury `ArmatureLink` components (`Armature` + `Armature.Shiori`) both mapping base `Head`, so the **conflict** guard fires first. REFUSE is still the correct outcome for a dual-armature hair; the reason is more specific than "proxy".
- **CarriedDoll** was predicted seamless → REFUSE(no-seam). It actually has **seven** `ArmatureLink` anchors (it is the drop-on-player gimmick); one `GetLinks` throws resolving onto the base. This drove the `TargetInvocationException`-unwrap + drift-vs-unresolvable severity split (`fix` commit): a seam that can't resolve onto this base is a **warning-level abstain**, not an error.

## Notes for a re-runner

- `-Tag CheckSeam` is only an output label; run `-Filter CheckSeamTests` to isolate the EditMode suite.
- The corpus deltas straddle ε by orders of magnitude, so the corpus is an end-to-end plumbing check, **not** the ε calibration guard — the synthetic ε±δ and 0.09/0.11-weight unit brackets are that guard.
- Documented residuals (Rule 2, not fixed): finger-rigged handwear across non-uniform bases → advisory NOT-PASS at the fingers; Head+Neck hair on head-swaps → may NOT-PASS; a PASS certifies the humanoid skeleton coincides, not physics-cage/bust/hair/accessory placement.
