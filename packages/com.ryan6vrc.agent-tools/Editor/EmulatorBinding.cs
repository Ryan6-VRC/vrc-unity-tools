using System;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one place the Av3 Emulator's type and member names are spelled; <c>EmulatorBindingCanaryTests</c>
    /// asserts them against the installed package. Reflected rather than referenced because
    /// <c>PlayGateCore</c> must work with no emulator installed.
    /// <para>Names only: each caller keeps its own resolution policy. <c>PlayGateCore</c> binds public-only
    /// and treats an absent emulator as a silent skip; <c>RenderThumbnailPlay.Run</c> and
    /// <c>RecordPlay.Run</c> resolve up-front including non-public members and refuse the session on a
    /// miss.</para>
    /// </summary>
    public static class EmulatorBinding
    {
        // The installed package ships no bare `Av3Emulator` type: a wrong literal here silently disables
        // every rule that keys off the emulator's presence.
        public const string RuntimeFullName = "Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime";
        public const string EmulatorFullName = "Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator";

        // ----- Members a shipped tool reads --------------------------------------------------------------

        /// <summary>`LyumaAv3Emulator` flags PlayGateCore's emulator-config rule reads (public).</summary>
        public const string RunPreprocessAvatarHook = "RunPreprocessAvatarHook";
        public const string EnablePlayerContactPermissions = "EnablePlayerContactPermissions";

        /// <summary>`LyumaAv3Runtime` members RenderThumbnailPlay reads. `IsLocal` is public; the other three
        /// are non-public implementation fields.</summary>
        public const string IsLocal = "IsLocal";
        public const string PlayableMixer = "playableMixer";
        public const string Playables = "playables";
        public const string FxIndex = "fxIndex";

        /// <summary>`LyumaAv3Runtime` members RecordPlay reads, all public.</summary>
        public const string IsMirrorClone = "IsMirrorClone";
        public const string IsShadowClone = "IsShadowClone";
        public const string NonLocalClones = "NonLocalClones";
        public const string Floats = "Floats", Ints = "Ints", Bools = "Bools";
        public const string FloatToIndex = "FloatToIndex", IntToIndex = "IntToIndex", BoolToIndex = "BoolToIndex";

        public static readonly string[] PinnedEmulatorFields =
            { RunPreprocessAvatarHook, EnablePlayerContactPermissions };

        public static readonly string[] PinnedRuntimePublicFields =
            { IsLocal, IsMirrorClone, IsShadowClone, NonLocalClones, Floats, Ints, Bools };

        /// <summary>Each a <c>Dictionary&lt;string, int&gt;</c>: RecordPlay reads them through that cast, so a
        /// changed type would read as an empty mirror.</summary>
        public static readonly string[] PinnedRuntimeIndexMaps = { FloatToIndex, IntToIndex, BoolToIndex };

        public static readonly string[] PinnedRuntimeNonPublicFields = { PlayableMixer, Playables, FxIndex };

        // ----- The surface docs/emulator.md teaches agents to drive by hand ------------------------------
        // No tool calls these; the canary is what catches a rename breaking one of the doc's recipes.

        public static readonly string[] DocumentedRuntimePublicFields =
        {
            "GestureLeftIdx", "Viseme", "TrackingType", // built-in inputs
            "CreateNonLocalClone",              // §Remote clone
            "DebugDuplicateAnimator",           // §Observation channels — the AAP read route
            "EnableAvatarOSC",                  // §OSC
            "NonLocalSyncInterval",             // the ~0.1 s sync tick
        };

        public static readonly string[] DocumentedEmulatorPublicFields =
            { "DescriptorColliders" };          // head/hand sender synthesis (§Fake another player's contact)

        /// <summary>Non-public, the only such member of the documented surface. A null read destroys the
        /// legitimate senders: docs/emulator.md §Two-avatar contact sessions.</summary>
        public const string ContactPlayerId = "contactPlayerId";

        // Members on the mirror's per-parameter entries. `expressionValue` is the float drive route; the
        // canary asserts it is ABSENT on the bool entry, because docs/emulator.md §Drive / observe tells
        // agents a bool has none and routes them to `.value`.
        // `exportedValue` is a property, on the float entry only: the float RecordPlay's mirror channel reads.
        public const string ParamEntryValue = "value";
        public static readonly string[] ParamEntryCommonFields = { "name", ParamEntryValue, "synced" };
        public const string ExpressionValue = "expressionValue";
        public const string ExportedValue = "exportedValue";

        /// <summary>Resolve a type by full name across the loaded domain; null when absent.</summary>
        public static Type ResolveType(string fullName) => VendorReflect.FindType(fullName);
    }
}
