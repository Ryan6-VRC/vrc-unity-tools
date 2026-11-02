using System;
using System.Reflection;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// Reflection canary over the Av3Emulator surface: the members shipped tools read and the members
// docs/emulator.md teaches agents to drive by hand, where no tool would notice a rename. Package present with
// a handle unresolved fails rather than skips; emulator absent is an Ignore.
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

    // Mirrors ReportGimmick.ReadBoolMember, which binds public-only and tries property then field: a field
    // converted to a public auto-property keeps production working, so a field-only assert would red on a
    // change that broke nothing.
    private static void AssertBoolPropertyOrField(Type t, string[] names, string why)
    {
        foreach (var n in names)
        {
            var p = t.GetProperty(n, Public);
            var f = t.GetField(n, Public);
            bool readable = (p != null && p.PropertyType == typeof(bool))
                         || (f != null && f.FieldType == typeof(bool));
            Assert.IsTrue(readable,
                t.Name + "." + n + " is no longer readable as a public bool property or field (" + why +
                ") — either it was renamed or its type changed");
        }
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
    public void PinnedEmulatorFields_StayReadableAsPublicBools()
    {
        AssertBoolPropertyOrField(RequireType(EmulatorBinding.EmulatorFullName),
            EmulatorBinding.PinnedEmulatorFields, "PlayGateCore's emulator-config rule reads it");
    }

    [Test]
    public void PinnedRuntimeFields_Resolve()
    {
        var t = RequireType(EmulatorBinding.RuntimeFullName);
        AssertFields(t, EmulatorBinding.PinnedRuntimePublicFields, Public, "a shipped tool reads it");
        AssertFields(t, EmulatorBinding.PinnedRuntimeNonPublicFields, NonPublic | Public,
            "RenderThumbnailPlay reads it with NonPublic binding");
    }

    [Test]
    public void PinnedRuntimeIndexMaps_AreStringToIntDictionaries()
    {
        var t = RequireType(EmulatorBinding.RuntimeFullName);
        foreach (var n in EmulatorBinding.PinnedRuntimeIndexMaps)
        {
            var f = t.GetField(n, Public);
            Assert.IsNotNull(f, t.Name + "." + n + " is gone (RecordPlay reads it) — the emulator moved under us");
            Assert.AreEqual(typeof(System.Collections.Generic.Dictionary<string, int>), f.FieldType,
                t.Name + "." + n + " changed type — RecordPlay's parameter mirror read casts it");
        }
    }

    // ── The surface docs/emulator.md teaches by hand ───────────────────────────────────────────────────

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
        // The recipe this pins is docs/emulator.md §Two-avatar contact sessions.
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
            AssertFields(RequireEntryType(t, listName), EmulatorBinding.ParamEntryCommonFields, Public,
                "verify.md reads it off every " + listName + " entry");
    }

    [Test]
    public void ExpressionValue_OnFloatsButNotBools()
    {
        // Pins the drive rule in docs/emulator.md §Drive / observe: floats through `.expressionValue`, bools
        // through `.value`.
        var t = RequireType(EmulatorBinding.RuntimeFullName);
        var floatEntry = RequireEntryType(t, "Floats");
        var boolEntry = RequireEntryType(t, "Bools");

        Assert.IsNotNull(floatEntry.GetField(EmulatorBinding.ExpressionValue, Public),
            "the float param entry lost `" + EmulatorBinding.ExpressionValue +
            "` — verify.md's float drive route goes through it");
        Assert.IsNull(boolEntry.GetField(EmulatorBinding.ExpressionValue, Public),
            "the bool param entry GAINED `" + EmulatorBinding.ExpressionValue +
            "` — verify.md says bools have none and routes them to `.value`; re-measure the drive rule");
    }

    [Test]
    public void ExportedValue_IsAFloatPropertyOnTheFloatEntry()
    {
        var floatEntry = RequireEntryType(RequireType(EmulatorBinding.RuntimeFullName), "Floats");
        var p = floatEntry.GetProperty(EmulatorBinding.ExportedValue, Public);
        Assert.IsTrue(p != null && p.PropertyType == typeof(float),
            "the float param entry lost its float `" + EmulatorBinding.ExportedValue +
            "` property — RecordPlay's mirror channel reads a float through it");
    }

    private static Type RequireEntryType(Type runtime, string listName)
    {
        var list = runtime.GetField(listName, Public);
        Assert.IsNotNull(list, runtime.Name + "." + listName +
            " is gone — the parameter mirror verify.md drives and reads");
        var entry = EntryType(list.FieldType);
        Assert.IsNotNull(entry, runtime.Name + "." + listName + " is no longer a single-arg generic list (" +
            list.FieldType.Name + ") — cannot reach its entry type to check the drive route");
        return entry;
    }

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
