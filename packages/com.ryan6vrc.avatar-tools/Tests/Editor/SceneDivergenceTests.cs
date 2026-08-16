// Pins the verdict SceneDivergence gives on each class of difference measured between a live scene and its
// file on disk (AvatarProject corpus, 2026-08-15). The classes are not hypothetical: every fixture below is
// a shape that actually occurred, and the two verdicts have asymmetric costs — a wrong REFUSE blocks a play
// session (loud, recoverable by saving), a wrong ACCEPT lets RenderThumbnailPlay.End() reopen the scene from
// disk and discard the operator's work with no warning. So the accept cases pin exactly the churn classes
// measured, and nothing wider.
//
// The permutation case earns its own note: an earlier design compared the two texts as a MULTISET of lines,
// which cannot see an edit that only moves lines — flipping which of two avatars is active is bag-identical
// and would have been silently accepted, then reverted. That is why the compare is an LCS, and why
// Classify_permutedValues_refuses exists to stop anyone reintroducing the cheaper structure.
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

public class SceneDivergenceTests
{
    // A prefab-instance modification block, the shape every interesting difference lives in.
    private const string Base =
        "--- !u!1001 &111\n" +
        "PrefabInstance:\n" +
        "  m_ObjectHideFlags: 0\n" +
        "  m_Modification:\n" +
        "    m_Modifications:\n" +
        "    - target: {fileID: 7915821905871551882, guid: c72796b5b10d30e4ea654dcd0ec390c0,\n" +
        "        type: 3}\n" +
        "      propertyPath: m_LocalScale.x\n" +
        "      value: 1\n" +
        "      objectReference: {fileID: 0}\n" +
        "    - target: {fileID: 7833535614601229429, guid: c72796b5b10d30e4ea654dcd0ec390c0,\n" +
        "        type: 3}\n" +
        "      propertyPath: m_IsActive\n" +
        "      value: 1\n" +
        "      objectReference: {fileID: 0}\n" +
        "  m_RemovedComponents: []\n";

    private static bool Lossy(string disk, string mem)
    {
        string summary;
        bool lossy = SceneDivergence.Classify(disk, mem, out summary);
        if (lossy) Assert.IsNotNull(summary, "a refusal must carry a summary — it is the whole remedy");
        else Assert.IsNull(summary, "an accept must not carry a summary");
        return lossy;
    }

    private static string Summary(string disk, string mem)
    {
        string summary;
        Assert.IsTrue(SceneDivergence.Classify(disk, mem, out summary), "expected a refusal");
        return summary;
    }

    [Test]
    public void Classify_identical_accepts()
    {
        Assert.IsFalse(Lossy(Base, Base));
    }

    // Modular Avatar stamps a version tag and cached references into a prefab instance on LOAD. Measured on
    // one venue scene: 93 inserted lines, and the scene did NOT read dirty afterwards. The reopen re-applies
    // it, so nothing is lost by restoring.
    [Test]
    public void Classify_modularAvatarStamp_accepts()
    {
        string mem = Base.Replace("  m_RemovedComponents: []\n",
            "  m_RemovedComponents: []\n" +
            "  _modularAvatarVersionTag:\n" +
            "    UpdatedAtVersion: 1.13.0\n" +
            "    MinimumVersion: 1.9.0\n");
        Assert.IsFalse(Lossy(Base, mem));
    }

    // VRCFury rewrites its version field when it deserializes a component built by an older release.
    [Test]
    public void Classify_vrcfuryVersionBump_accepts()
    {
        string disk = Base + "  vrcfuryVersion: 1.1371.0\n";
        string mem = Base + "  vrcfuryVersion: 1.1414.0\n";
        Assert.IsFalse(Lossy(disk, mem));
    }

    // Unity discards modification entries whose target no longer resolves when it serializes. One measured
    // venue scene carries 10 of them, so this is the difference an unfiltered compare would trip over most.
    // The wrapped mapping is the parser's most failure-prone case: the entry's `{fileID: 0}` and its fields
    // must all be swallowed, and the sequence marker distinguished from `- targetCorrespondingSourceObject:`.
    [Test]
    public void Classify_danglingOverridesPrunedInMemory_accepts()
    {
        string disk = Base.Replace("  m_RemovedComponents: []\n",
            "    - target: {fileID: 0}\n" +
            "      propertyPath: cachedExecutionGroupIndex\n" +
            "      value: 0\n" +
            "      objectReference: {fileID: 0}\n" +
            "    - target: {fileID: 0}\n" +
            "      propertyPath: m_Name\n" +
            "      value: \n" +   // an empty value is ordinary — 69 of them in one measured scene
            "      objectReference: {fileID: 0}\n" +
            "  m_RemovedComponents: []\n");
        Assert.IsFalse(Lossy(disk, Base));
    }

    [Test]
    public void Classify_danglingAddedObjectEntryPruned_accepts()
    {
        string disk = Base.Replace("  m_RemovedComponents: []\n",
            "  m_AddedGameObjects:\n" +
            "  - targetCorrespondingSourceObject: {fileID: 0}\n" +
            "    insertionIndex: -1\n" +
            "    addedObject: {fileID: 0}\n" +
            "  m_RemovedComponents: []\n");
        string mem = Base.Replace("  m_RemovedComponents: []\n",
            "  m_AddedGameObjects:\n" +
            "  m_RemovedComponents: []\n");
        Assert.IsFalse(Lossy(disk, mem));
    }

    // A LIVE case, values and all: the scene read isDirty=False while a prefab instance sat moved and scaled
    // in memory. This is the silent-data-loss path the whole class exists for.
    [Test]
    public void Classify_realOverrideValueChanged_refuses()
    {
        string mem = Base.Replace("      propertyPath: m_LocalScale.x\n      value: 1\n",
                                  "      propertyPath: m_LocalScale.x\n      value: 0.58823526\n");
        string summary = Summary(Base, mem);
        // A bare "value: 0.58823526" identifies nothing to the person who has to decide whether it is theirs —
        // the property name carried down from the entry above it is what makes the refusal actionable.
        StringAssert.Contains("m_LocalScale.x=0.58823526", summary,
            "the refusal must name the property AND the value that would be lost");
    }

    // A deletion is work too, and the restore undoes it. Disk-only lines are NOT a free side of the diff.
    [Test]
    public void Classify_contentDeletedInMemory_refuses()
    {
        string mem = Base.Replace(
            "    - target: {fileID: 7833535614601229429, guid: c72796b5b10d30e4ea654dcd0ec390c0,\n" +
            "        type: 3}\n" +
            "      propertyPath: m_IsActive\n" +
            "      value: 1\n" +
            "      objectReference: {fileID: 0}\n", "");
        Assert.IsTrue(Lossy(Base, mem));
    }

    // The multiset design's blind spot, kept as a standing regression guard: two values trade places, so
    // every line still appears exactly as often as before. Ordinary work (which avatar is shown).
    [Test]
    public void Classify_permutedValues_refuses()
    {
        string disk = Base
            + "  a: {m_IsActive: 1}\n"
            + "  b: {m_IsActive: 0}\n";
        string mem = Base
            + "  a: {m_IsActive: 0}\n"
            + "  b: {m_IsActive: 1}\n";
        Assert.IsTrue(Lossy(disk, mem), "a permutation is real work the restore would revert");
    }

    // The churn list is matched on the KEY. Here an allowlisted name appears as a VALUE, and the line that
    // actually changed is an ordinary `value:` — a substring match would drop the wrong line and could
    // cancel a real edit.
    [Test]
    public void Classify_allowlistedNameAsValue_refuses()
    {
        string disk = Base
            + "      propertyPath: MinimumVersion\n"
            + "      value: 1.9.0\n";
        string mem = Base
            + "      propertyPath: MinimumVersion\n"
            + "      value: 9.9.9\n";
        string summary = Summary(disk, mem);
        StringAssert.Contains("9.9.9", summary);
    }

    // Churn does not launder an edit riding alongside it.
    [Test]
    public void Classify_churnPlusRealEdit_refuses()
    {
        string disk = Base + "  vrcfuryVersion: 1.1371.0\n";
        string mem = (Base + "  vrcfuryVersion: 1.1414.0\n")
            .Replace("      propertyPath: m_IsActive\n      value: 1\n",
                     "      propertyPath: m_IsActive\n      value: 0\n");
        Assert.IsTrue(Lossy(disk, mem));
    }

    // Past the cap the tool stops characterizing and refuses. Fail-closed is the rule everywhere in this
    // class: a difference it cannot describe is still a difference.
    [Test]
    public void Classify_overCap_refuses()
    {
        var disk = new StringBuilder();
        var mem = new StringBuilder();
        for (int i = 0; i < SceneDivergence.MaxEditScript + 50; i++)
        {
            disk.Append("      value: ").Append(i).Append('\n');
            mem.Append("      value: ").Append(i + 1000000).Append('\n');
        }
        string summary = Summary(disk.ToString(), mem.ToString());
        StringAssert.Contains("too large", summary);
    }

    [TestCase("      value: 0.5", "value")]
    [TestCase("    - target: {fileID: 0}", "target")]
    [TestCase("  _modularAvatarVersionTag:", "_modularAvatarVersionTag")]
    [TestCase("nokeyhere", "")]
    public void KeyOf_readsTheKeyNotTheValue(string line, string expected)
    {
        Assert.AreEqual(expected, SceneDivergence.KeyOf(line));
    }

    // The strip is structural: a LIVE entry keeps every one of its lines, or a real override would vanish
    // along with the dangling ones.
    [Test]
    public void StripDanglingOverrides_keepsLiveEntries()
    {
        var lines = new List<string>(Base.Split('\n'));
        int before = lines.Count;
        List<string> after = SceneDivergence.StripDanglingOverrides(lines);
        Assert.AreEqual(before, after.Count, "nothing in the base fixture is dangling");
    }
}
