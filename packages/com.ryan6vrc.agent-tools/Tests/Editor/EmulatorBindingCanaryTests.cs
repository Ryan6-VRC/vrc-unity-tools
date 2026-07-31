using System;
using System.Reflection;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// Reflection canary over the whole Av3Emulator surface this workshop depends on — both the members shipped
// tools read and the members docs/verify.md teaches agents to drive by hand.
//
// The second half is the point. The pinned members already have two fail-loud runtime refusals behind them
// (PlayGateCore's emulator-config offender, RenderThumbnailPlay.Begin's drift refusal), so a rename there
// announces itself. Nothing guards the hand-driven surface: if `CreateNonLocalClone` were renamed, no tool
// would notice, and the first symptom would be an agent mid-task reading null while following a doc that has
// silently gone wrong. This fixture converts that into a red suite, at a door no PR bypasses.
//
// Same rule as RenderAvatarFreshnessTests: package present + handle unresolved must FAIL, never skip — a skip
// is exactly when production goes blind. Emulator absent is a legitimate Ignore (a venue may carry no
// emulator, and PlayGateCore is required to work in one).
public class EmulatorBindingCanaryTests
{
    private const string EmulatorPackageId = "lyuma.av3emulator";

    // Package-presence signal independent of the reflection path under test — the package registry by ID,
    // which survives an assembly rename that would blind the type lookups themselves.
    private static bool EmulatorInstalled()
    {
        foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            if (p.name == EmulatorPackageId) return true;
        return false;
    }

    private static Type RequireType(string fullName)
    {
        if (!EmulatorInstalled())
            Assert.Ignore(EmulatorPackageId + " not installed in this venue — canary has nothing to check");
        var t = EmulatorBinding.ResolveType(fullName);
        Assert.IsNotNull(t, fullName + " did not resolve while " + EmulatorPackageId +
                            " IS installed — EmulatorBinding's type name is stale");
        return t;
    }

    private const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void AssertFields(Type t, string[] names, BindingFlags flags, string why)
    {
        foreach (var n in names)
            Assert.IsNotNull(t.GetField(n, flags),
                t.Name + "." + n + " is gone (" + why + ") — the emulator moved under us");
    }

    // ── The two types every reader binds ─────────────────────────────────────────────────────────────

    [Test]
    public void RuntimeAndEmulatorTypes_Resolve()
    {
        Assert.IsNotNull(RequireType(EmulatorBinding.RuntimeFullName));
        Assert.IsNotNull(RequireType(EmulatorBinding.EmulatorFullName));
    }

    // ── Members a shipped tool reads ─────────────────────────────────────────────────────────────────

    [Test]
    public void PinnedEmulatorFields_Resolve_Publicly()
    {
        // Public specifically: PlayGateCore reads these through ReportGimmick.ReadBoolMember, which binds
        // public-only. A field that survived but went non-public would still break it.
        AssertFields(RequireType(EmulatorBinding.EmulatorFullName),
            EmulatorBinding.PinnedEmulatorFields, Public, "PlayGateCore's emulator-config rule reads it");
    }

    [Test]
    public void PinnedRuntimeFields_Resolve()
    {
        var t = RequireType(EmulatorBinding.RuntimeFullName);
        AssertFields(t, EmulatorBinding.PinnedRuntimePublicFields, Public, "RenderThumbnailPlay reads it");
        AssertFields(t, EmulatorBinding.PinnedRuntimeNonPublicFields, NonPublic | Public,
            "RenderThumbnailPlay reads it with NonPublic binding");
    }

    // ── The surface docs/verify.md teaches by hand ───────────────────────────────────────────────────

    [Test]
    public void DocumentedRuntimeFields_Resolve_Publicly()
    {
        AssertFields(RequireType(EmulatorBinding.RuntimeFullName),
            EmulatorBinding.DocumentedRuntimePublicFields, Public, "verify.md teaches it as a public handle");
    }

    [Test]
    public void DocumentedEmulatorFields_Resolve_Publicly()
    {
        AssertFields(RequireType(EmulatorBinding.EmulatorFullName),
            EmulatorBinding.DocumentedEmulatorPublicFields, Public, "verify.md teaches it as a public handle");
    }

    [Test]
    public void ContactPlayerId_IsStillNonPublic()
    {
        // Both halves matter. Gone ⇒ verify.md's two-avatar sender-hygiene recipe is broken. Turned PUBLIC ⇒
        // the recipe's abort-on-null guard and its NonPublic binding flags became unnecessary ceremony the
        // doc still demands, which is its own kind of stale.
        var t = RequireType(EmulatorBinding.RuntimeFullName);
        Assert.IsNotNull(t.GetField(EmulatorBinding.ContactPlayerId, NonPublic),
            EmulatorBinding.ContactPlayerId + " is gone — verify.md's two-avatar sender-hygiene recipe " +
            "reads it to destroy duplicate senders");
        Assert.IsNull(t.GetField(EmulatorBinding.ContactPlayerId, Public),
            EmulatorBinding.ContactPlayerId + " is now PUBLIC — verify.md still prescribes NonPublic binding " +
            "plus an abort-on-null guard for it; simplify the recipe");
    }

    // ── The mirror's per-parameter entries, including one deliberate negative ────────────────────────

    [Test]
    public void ParamEntryTypes_CarryTheDocumentedMembers()
    {
        var t = RequireType(EmulatorBinding.RuntimeFullName);
        foreach (var listName in new[] { "Floats", "Ints", "Bools" })
        {
            var list = t.GetField(listName, Public);
            Assert.IsNotNull(list, listName + " is gone — the parameter mirror verify.md reads");
            var entry = EntryType(list.FieldType);
            Assert.IsNotNull(entry, listName + " is no longer a generic list — cannot reach its entry type");
            AssertFields(entry, EmulatorBinding.ParamEntryCommonFields, Public,
                "verify.md reads it off every " + listName + " entry");
        }
    }

    [Test]
    public void ExpressionValue_OnFloatsButNotBools()
    {
        // verify.md's drive rule rests on exactly this asymmetry: floats drive through `.expressionValue`
        // (a `.value` write reverts on synced params), while "Bools have no expressionValue" and drive via
        // `.value`. If the emulator ever adds one to bools, that instruction becomes wrong silently.
        var t = RequireType(EmulatorBinding.RuntimeFullName);
        var floatEntry = EntryType(t.GetField("Floats", Public).FieldType);
        var boolEntry = EntryType(t.GetField("Bools", Public).FieldType);

        Assert.IsNotNull(floatEntry.GetField(EmulatorBinding.ExpressionValue, Public),
            "the float param entry lost `" + EmulatorBinding.ExpressionValue +
            "` — verify.md's float drive route goes through it");
        Assert.IsNull(boolEntry.GetField(EmulatorBinding.ExpressionValue, Public),
            "the bool param entry GAINED `" + EmulatorBinding.ExpressionValue +
            "` — verify.md says bools have none and routes them to `.value`; re-measure the drive rule");
    }

    // The element type behind a List<T> field, or null when the field is not a generic collection.
    private static Type EntryType(Type listType)
    {
        if (listType.IsGenericType)
        {
            var args = listType.GetGenericArguments();
            if (args.Length == 1) return args[0];
        }
        return listType.IsArray ? listType.GetElementType() : null;
    }
}
