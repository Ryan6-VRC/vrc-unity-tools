# vrc-unity-tools

> Part of the [Atelier](https://github.com/Ryan6-VRC/atelier) workspace — a code reference, not a standalone product. The docs that govern this code live in the meta-repo.

Unity Editor tooling for VRChat avatar work, published as a small monorepo of independent UPM packages so you install only what you need.

## Packages

| Package | What it is |
|---|---|
| [`com.ryan6vrc.agent-tools`](packages/com.ryan6vrc.agent-tools) | The **inspection and verification harness** for AI-assisted (Claude Code) avatar workflows: digests, gates and renders that let an agent observe and prove rather than assert, plus a few doors that write. |
| [`com.ryan6vrc.avatar-tools`](packages/com.ryan6vrc.avatar-tools) | The **vendor→owned avatar kit** — the writing half: transplanting a vendor avatar into a normalized one of your own, the animator-controller compile/decompile substrate that makes controllers reviewable text, and the thumbnail/upload last mile. Depends on `agent-tools` for the shared conventions. |

Each package is its own assembly (`asmdef`). Both carry `[AgentTool]`-tagged, agent-callable static entry points; some tools also keep human menu/window doors.

> The individual callable tools across this workspace are indexed in the meta-repo `TOOLS.md` (vrc-unity-tools section), keyed by `[AgentTool]` type name.

## Install

Each package's README carries its `Packages/manifest.json` lines, as a local checkout and as a git URL: [`com.ryan6vrc.agent-tools`](packages/com.ryan6vrc.agent-tools/README.md), [`com.ryan6vrc.avatar-tools`](packages/com.ryan6vrc.avatar-tools/README.md).

## License

MIT — see [LICENSE](LICENSE).
