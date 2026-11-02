# Ryan6VRC Agent Tools

> Part of the [Atelier](https://github.com/Ryan6-VRC/atelier) workspace — a code reference, not a standalone product. The docs that govern this code live in the meta-repo.

Editor tooling for AI-assisted (Claude Code) VRChat avatar workflows — the "observe" and "verify" side of letting an agent work on a Unity project safely.

The package is gated on the VRChat Avatars SDK (`VRC_SDK_VRCSDK3`) — it only compiles where the SDK is present, which is the only place these tools run.

This package also owns the cross-package agent-tool conventions: the canonical `[AgentTool]` marker and the `RunLogFormat` helpers that sibling packages delegate to.

The package's callables are indexed in the meta-repo `TOOLS.md` (an `[AgentTool]`-tagged class is what puts a row there); per-tool contracts live in the meta-repo's `docs/unity-tools.md` (the animator/clip digests in `docs/animator.md`). Beyond the tagged tools, `AgentSelfTest` (headless via `RunHeadless()` — see its docstring for the invocation) smoke-tests the observe→modify→verify loop.

## Install

`Packages/manifest.json`:

```jsonc
// Local checkout (editable):
"com.ryan6vrc.agent-tools": "file:../../vrc-unity-tools/packages/com.ryan6vrc.agent-tools",

// Or by git URL:
"com.ryan6vrc.agent-tools": "https://github.com/Ryan6-VRC/vrc-unity-tools.git?path=packages/com.ryan6vrc.agent-tools"
```

Unity 2022.3+. MIT licensed.
