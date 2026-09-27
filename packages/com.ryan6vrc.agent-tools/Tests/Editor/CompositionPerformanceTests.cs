using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

/// <summary>
/// The performance section republishes the SDK's own scan, so what it must never do is disagree with it: every
/// rating is the one the SDK computed, the summary's <c>rank=</c> is the SDK's Overall, and the physbone census
/// sums to the SDK's <c>transformCount</c>. A scan that threw publishes no figure at all, and an unreadable chain
/// is named rather than counted as zero. That the census sums to the SDK's figure on a real chain needs physbones
/// added to live objects, which <c>docs/verify.md</c> §Test venue keeps out of NUnit; it is checked by running
/// the bake on a real avatar.
///
/// Objects are left for the next test's <c>NewScene(Single)</c> to take rather than destroyed —
/// <c>docs/verify.md</c> §Test venue forbids destroying live objects a fixture has mutated.
/// </summary>
public class CompositionPerformanceTests
{
    [SetUp]
    public void SetUp() => UnityEditor.SceneManagement.EditorSceneManager.NewScene(
        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
        UnityEditor.SceneManagement.NewSceneMode.Single);

    [Test]
    public void RatingsAndRankAreTheSdks_andTheSummaryNamesWhatSetTheRank()
    {
        var stats = new AvatarPerformanceStats(false) { polyCount = 500000, materialCount = 1 };
        stats.CalculateAllPerformanceRatings(false);
        var read = new CompositionBake.PerformanceRead { Stats = stats, ConstraintRefresh = "ok" };

        string keys;
        var lines = CompositionBake.PerformanceSection(read, null, out keys);

        var overall = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
        Assert.AreEqual(PerformanceRating.VeryPoor, overall, "fixture: 500k triangles is past every rung");
        StringAssert.StartsWith("rank=VeryPoor rankSetBy=", keys);
        CollectionAssert.Contains(keys.Split('=').Last().Split(','), "PolyCount");
        CollectionAssert.DoesNotContain(keys.Split('=').Last().Split(','), "MaterialCount",
            "a category rated better than Overall did not set the rank");
        CollectionAssert.Contains(lines, "| PolyCount | VeryPoor |");
        CollectionAssert.Contains(lines, "| MaterialCount | "
            + stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.MaterialCount) + " |");
        CollectionAssert.Contains(lines, "| polyCount | 500000 |");
    }

    [Test]
    public void AThrownScan_publishesNoFigure()
    {
        var read = new CompositionBake.PerformanceRead { Error = "NullReferenceException: boom", ConstraintRefresh = "ok" };

        string keys;
        var lines = CompositionBake.PerformanceSection(read, null, out keys);

        Assert.AreEqual("rank=unread", keys);
        Assert.IsFalse(lines.Any(l => l.StartsWith("| ")), "no table may stand in for a scan that did not happen");
        Assert.IsTrue(lines.Any(l => l.Contains("NullReferenceException: boom")));
    }

    [Test]
    public void AFailedConstraintRefresh_isNamedBesideTheDepth()
    {
        var stats = new AvatarPerformanceStats(false);
        stats.CalculateAllPerformanceRatings(false);
        var read = new CompositionBake.PerformanceRead { Stats = stats, ConstraintRefresh = "unavailable" };

        string keys;
        var lines = CompositionBake.PerformanceSection(read, null, out keys);

        Assert.IsTrue(lines.Any(l => l.Contains("refresh before the scan: unavailable") && l.Contains("constraintDepth")));
    }

    [Test]
    public void AnUnreadableChainIsNamed_andLeftOutOfTheSum()
    {
        var stats = new AvatarPerformanceStats(false);
        stats.CalculateAllPerformanceRatings(false);
        var read = new CompositionBake.PerformanceRead { Stats = stats, ConstraintRefresh = "ok" };
        read.PhysBones.Add(new CompositionBake.PhysBoneRow { Path = "Armature/Hair", Bones = 5, Colliders = 2, Active = true });
        read.PhysBones.Add(new CompositionBake.PhysBoneRow { Path = "Armature/Tail", Bones = -1, Caveat = "InitTransforms threw X" });

        string keys;
        var lines = CompositionBake.PerformanceSection(read, null, out keys);

        StringAssert.Contains("| unreadable |", lines.Single(l => l.Contains("Armature/Tail")));
        StringAssert.StartsWith("| total bones | | 5 |", lines.Single(l => l.StartsWith("| total bones")));
    }
}
