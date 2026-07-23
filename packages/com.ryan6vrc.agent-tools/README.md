# Ryan6VRC Agent Tools

> Part of the [Atelier](https://github.com/Ryan6-VRC/atelier) workspace — a code reference, not a standalone product. The docs that govern this code live in the meta-repo.

Editor tooling for AI-assisted (Claude Code) VRChat avatar workflows — the "observe" and "verify" side of letting an agent work on a Unity project safely.

The package is gated on the VRChat Avatars SDK (`VRC_SDK_VRCSDK3`) — it only compiles where the SDK is present, which is the only place these tools run. The inspector still reads any component generically via `SerializedObject` (no per-type code); the gate is what lets the verify/modify side reach typed avatar components (Avatar Descriptor, PhysBones, VRC constraints) directly.

This package also owns the cross-package agent-tool conventions: the canonical `[AgentTool]` marker and the `RunLogFormat` text helpers (JSON escape, filename sanitize, path leaf) that sibling packages delegate to.

The package's callables — inspection snapshots, import verification, and the read-only animator/gimmick/render digests — are indexed in the meta-repo `TOOLS.md` (an `[AgentTool]`-tagged class is what puts a row there); behavioral detail lives in the meta-repo's `docs/unity.md`. Beyond the tagged tools, `AgentSelfTest` (headless via `RunHeadless()` — see its docstring for the invocation) smoke-tests the observe→modify→verify loop.

## Install

`Packages/manifest.json`:

```jsonc
"com.ryan6vrc.agent-tools": "https://github.com/Ryan6-VRC/vrc-unity-tools.git?path=packages/com.ryan6vrc.agent-tools"
```

Unity 2022.3+. Requires the VRChat Avatars SDK (`VRC_SDK_VRCSDK3`). MIT licensed.
