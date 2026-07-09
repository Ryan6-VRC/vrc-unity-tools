using System.Runtime.CompilerServices;

// The test assembly drives the fail-closed degradation helpers directly (e.g. ReportGimmick.ReadBoolMember
// returning null on an unreflectable field — the emulator-config safety property). Those helpers stay
// internal to the tool assembly; this exposes them to tests only.
[assembly: InternalsVisibleTo("Ryan6VRC.AgentTools.Tests")]
