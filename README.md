# vrc-unity-tools

> Part of the [Atelier](https://github.com/Ryan6-VRC/atelier) workspace — a code reference, not a standalone product. The docs that govern this code live in the meta-repo.

Unity Editor tooling for VRChat avatar work, published as a small monorepo of independent UPM packages so you install only what you need.

## Packages

| Package | What it is | VRChat SDK? |
|---|---|---|
| [`com.ryan6vrc.agent-tools`](packages/com.ryan6vrc.agent-tools) | The **inspection and verification harness** for AI-assisted (Claude Code) avatar workflows — read-only digests, gates, and renders that report on an avatar without changing it, so an agent can observe and prove rather than assert. Also home of the canonical `[AgentTool]` marker and the shared RunLog text conventions (`RunLogFormat`). | Yes (gated on `VRC_SDK_VRCSDK3`). |
| [`com.ryan6vrc.avatar-tools`](packages/com.ryan6vrc.avatar-tools) | The **vendor→owned avatar kit** — the writing half: transplanting a vendor avatar into a normalized one of your own, the animator-controller compile/decompile substrate that makes controllers reviewable text, and the thumbnail/upload last mile. Depends on `agent-tools` for the shared conventions. | Yes (gated on `VRC_SDK_VRCSDK3`). |

Each package is its own assembly (`asmdef`); `avatar-tools` references `agent-tools` for the shared conventions. Both carry `[AgentTool]`-tagged, agent-callable static entry points; some tools also keep human menu/window doors.

> The individual callable tools across this workspace are indexed in the meta-repo `TOOLS.md`
> (vrc-unity-tools section), keyed by `[AgentTool]` type name.

## Install

Add to a project's `Packages/manifest.json`:

```jsonc
// Local checkout (editable):
"com.ryan6vrc.avatar-tools": "file:../../vrc-unity-tools/packages/com.ryan6vrc.avatar-tools",

// Or by git URL (optionally pin a tag once one is published):
"com.ryan6vrc.avatar-tools": "https://github.com/Ryan6-VRC/vrc-unity-tools.git?path=packages/com.ryan6vrc.avatar-tools"
```

(Swap the package name/path for `com.ryan6vrc.agent-tools` as needed.)

## License

MIT — see [LICENSE](LICENSE).
