# com.ryan6vrc.avatar-tools — Ryan6VRC Avatar Tools

> Part of the [Atelier](https://github.com/Ryan6-VRC/atelier) workspace — a code reference, not a standalone product. The docs that govern this code live in the meta-repo.

Agent-callable Unity Editor build tools for owning (making editable copies of) vendor VRChat avatars and the mergeables that compose them.

The package's callables are indexed in the meta-repo `TOOLS.md`; per-tool contracts live in the meta-repo's `docs/unity-tools.md` §Avatar tools (the controller tools in `docs/animator.md`).

The assembly compiles only when `VRC_SDK_VRCSDK3` is defined (the VRChat Avatars SDK is present in the project).

## Install

Install `com.ryan6vrc.agent-tools` alongside it: this package's assembly references it, and UPM will not pull it in.

`Packages/manifest.json`:

```jsonc
// Local checkout (editable):
"com.ryan6vrc.avatar-tools": "file:../../vrc-unity-tools/packages/com.ryan6vrc.avatar-tools",

// Or by git URL:
"com.ryan6vrc.avatar-tools": "https://github.com/Ryan6-VRC/vrc-unity-tools.git?path=packages/com.ryan6vrc.avatar-tools"
```

Unity 2022.3+. MIT licensed.
