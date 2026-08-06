# com.ryan6vrc.avatar-tools — Ryan6VRC Avatar Tools

> Part of the [Atelier](https://github.com/Ryan6-VRC/atelier) workspace — a code reference, not a standalone product. The docs that govern this code live in the meta-repo.

Agent-callable Unity Editor build tools for owning (making editable copies of) vendor VRChat avatars and the mergeables that compose them. Provides a composable transplant pipeline: package-graph inspection; tool-by-tool transfer of materials, humanoid rig, and avatar descriptor; a **component-transplant kit** — `CopyComponents` / `MoveComponents` / `GraftHierarchy` over a shared core — that reproduces, relocates, and grafts components selected by type-name (VRC dynamics on a base; Modular Avatar / VRCFury / NDMF on any mergeable that attaches to it); and a clean-FX builder. It also carries standalone clip/material utilities — expression-clip normalizer, material remapper, constrained hierarchy duplicate — each with a **Tools → Ryan6VRC** menu/window door and an agent-callable `Run(...)`.

**Editor-only.** All code lives in an Editor assembly; nothing ships to builds.

**SDK-gated.** The assembly is compiled only when `VRC_SDK_VRCSDK3` is defined (i.e., the VRChat Avatars SDK is present in the project).
