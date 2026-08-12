using System.Runtime.CompilerServices;

// The test assembly drives the fail-closed degradation helpers directly (e.g. ReportGimmick.ReadBoolMember
// returning null on an unreflectable field — the emulator-config safety property). Those helpers stay
// internal to the tool assembly; this exposes them to tests only.
[assembly: InternalsVisibleTo("Ryan6VRC.AgentTools.Tests")]

// AvatarBake is the one bake spine, and avatar-tools' RenderThumbnail is its second caller. Reached by IVT
// rather than by making it public: the scope is a two-step protocol (read the clone, then close it), and a
// package's supported surface should not grow a hand-off contract just to cross a seam inside this repo.
[assembly: InternalsVisibleTo("Ryan6VRC.AvatarTools.Editor")]
