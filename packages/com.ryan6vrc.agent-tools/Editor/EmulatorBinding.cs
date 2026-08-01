using System;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one place the Av3 Emulator's type and member names are spelled. Every shipped tool that reflects
    /// the emulator reads its names from here, and <c>EmulatorBindingCanary</c> asserts every name below
    /// against the installed package — so a rename reds the suite instead of surfacing as a null mid-task.
    /// <para>Names only, deliberately. Each caller keeps its own resolution policy, because the two shipped
    /// readers need opposite ones: <c>PlayGateCore</c> resolves lazily and public-only (an emulator-free
    /// scene is a legitimate bake-only check, so absence must stay a silent skip), while
    /// <c>RenderThumbnailPlay.Begin</c> resolves up-front including non-public members and refuses the whole
    /// session on a miss (it must fail before it mutates the scene). Funnelling both through one resolver
    /// would impose one policy on two correct answers.</para>
    /// <para>Why the emulator is reflected rather than referenced: an asmdef reference would make
    /// <c>lyuma.av3emulator</c> a hard dependency of these packages, and <c>PlayGateCore</c> is required to
    /// work with no emulator installed. Compiling the binding behind a <c>versionDefines</c> symbol instead
    /// would trade a refusal an agent can read for a branch that silently does not exist in an emulator-free
    /// venue — where the canary could not test it either.</para>
    /// </summary>
    public static class EmulatorBinding
    {
        // The installed package ships no bare `Av3Emulator` type — a wrong literal here silently disables
        // every rule that keys off the emulator's presence, which is why these are constants and not
        // repeated string literals at the read sites.
        public const string RuntimeFullName = "Lyuma.Av3Emulator.Runtime.LyumaAv3Runtime";
        public const string EmulatorFullName = "Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator";

        // ----- Members a shipped tool reads (the reader is named; a re-pin updates one line) -------------

        /// <summary>`LyumaAv3Emulator` flags PlayGateCore's emulator-config rule reads (public).</summary>
        public const string RunPreprocessAvatarHook = "RunPreprocessAvatarHook";
        public const string EnablePlayerContactPermissions = "EnablePlayerContactPermissions";

        /// <summary>`LyumaAv3Runtime` members RenderThumbnailPlay reads. `IsLocal` is public and also the
        /// handle verify.md teaches; the other three are non-public implementation fields.</summary>
        public const string IsLocal = "IsLocal";
        public const string PlayableMixer = "playableMixer";
        public const string Playables = "playables";
        public const string FxIndex = "fxIndex";

        public static readonly string[] PinnedEmulatorFields =
            { RunPreprocessAvatarHook, EnablePlayerContactPermissions };

        public static readonly string[] PinnedRuntimePublicFields = { IsLocal };

        public static readonly string[] PinnedRuntimeNonPublicFields = { PlayableMixer, Playables, FxIndex };

        // ----- The surface docs/verify.md teaches agents to drive by hand ------------------------------
        // No tool calls these. They are here because the doc's factual claim that they exist is worth
        // machine-checking: a rename in this set breaks a recipe an agent is following, and nothing else in
        // the workshop would notice.

        public static readonly string[] DocumentedRuntimePublicFields =
        {
            "IsMirrorClone", "IsShadowClone",   // clone selection (§Remote clone, §Verify mirror-detection)
            "Floats", "Ints", "Bools",          // the parameter mirror (§Drive / observe)
            "GestureLeftIdx", "Viseme", "TrackingType", // built-in inputs
            "CreateNonLocalClone",              // §Remote clone
            "DebugDuplicateAnimator",           // §Observation channels — the AAP read route
            "EnableAvatarOSC",                  // §OSC
            "NonLocalSyncInterval",             // the ~0.1 s sync tick
        };

        public static readonly string[] DocumentedEmulatorPublicFields =
            { "DescriptorColliders" };          // head/hand sender synthesis (§Fake another player's contact)

        /// <summary>Non-public, and the only member in the documented surface that is: the two-avatar
        /// contact venue compares each sender's id against it, and a null read there destroys the
        /// legitimate senders (verify.md's abort-on-null rule).</summary>
        public const string ContactPlayerId = "contactPlayerId";

        // Members on the mirror's per-parameter entries. `expressionValue` is the float drive route; the
        // canary asserts it is ABSENT on the bool entry, because verify.md tells agents a bool has none and
        // routes them to `.value` instead — an emulator that added one would make that instruction stale.
        public static readonly string[] ParamEntryCommonFields = { "name", "value", "synced" };
        public const string ExpressionValue = "expressionValue";

        /// <summary>Resolve a type by full name across the loaded domain; null when genuinely absent.
        /// The public door verify.md's snippets call; the resolver itself is VendorReflect's.</summary>
        public static Type ResolveType(string fullName) => VendorReflect.FindType(fullName);
    }
}
