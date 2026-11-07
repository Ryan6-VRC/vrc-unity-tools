using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

// The mapping MatchHumanoidRig writes. A base that maps its head and eyes to unweighted proxies keeps them
// through a re-run, which every geometry change makes; a mapping onto a transform the vendor also carries is
// still conformed to the vendor's.
public class MatchHumanoidRigTests
{
    static HumanBone Bone(string human, string bone, float min = 0f)
    {
        var hb = new HumanBone { humanName = human, boneName = bone };
        var lim = new HumanLimit { useDefaultValues = min == 0f, min = new Vector3(min, min, min), max = new Vector3(-min, -min, -min) };
        hb.limit = lim;
        return hb;
    }

    static Dictionary<string, int> Ours(params string[] names)
    {
        var d = new Dictionary<string, int>();
        foreach (var n in names) d[n] = d.TryGetValue(n, out var c) ? c + 1 : 1;
        return d;
    }

    static string MappedTo(List<HumanBone> map, string human)
        => map.Find(h => h.humanName == human).boneName;

    [Test]
    public void KeepOwnTargets_KeepsAProxyTheVendorLacks_WithItsLimits()
    {
        var vendor = new List<HumanBone> { Bone("Hips", "Hips"), Bone("Head", "Head"), Bone("LeftEye", "LeftEye") };
        var existing = new[] { Bone("Hips", "Hips"), Bone("Head", "Head_Proxy"), Bone("LeftEye", "LeftEye_Proxy", -12f) };
        var map = MatchHumanoidRig.KeepOwnTargets(vendor, existing,
            Ours("Hips", "Head", "Head_Proxy", "LeftEye", "LeftEye_Proxy"), new HashSet<string> { "Hips", "Head", "LeftEye" },
            out var kept);

        Assert.AreEqual("Head_Proxy", MappedTo(map, "Head"));
        Assert.AreEqual("LeftEye_Proxy", MappedTo(map, "LeftEye"));
        Assert.AreEqual("Hips", MappedTo(map, "Hips"));
        Assert.AreEqual(2, kept.Count);
        var eye = map.Find(h => h.humanName == "LeftEye");
        Assert.IsFalse(eye.limit.useDefaultValues, "the eye-look limits ride with the kept entry");
        Assert.AreEqual(-12f, eye.limit.min.x);
        Assert.AreEqual(3, map.Count, "a kept entry replaces the vendor's, it is not added beside it");
    }

    // An auto-guessed mapping onto a bone both models carry is exactly what the door exists to correct.
    [Test]
    public void KeepOwnTargets_ConformsAMappingOntoATransformTheVendorAlsoCarries()
    {
        var vendor = new List<HumanBone> { Bone("Chest", "Chest"), Bone("UpperChest", "Chest2") };
        var existing = new[] { Bone("Chest", "Chest2") };
        var map = MatchHumanoidRig.KeepOwnTargets(vendor, existing,
            Ours("Chest", "Chest2"), new HashSet<string> { "Chest", "Chest2" }, out var kept);

        Assert.AreEqual("Chest", MappedTo(map, "Chest"));
        Assert.IsEmpty(kept);
    }

    [Test]
    public void KeepOwnTargets_DropsATargetOurModelNoLongerHasOrHasTwice()
    {
        var vendor = new List<HumanBone> { Bone("Head", "Head"), Bone("Jaw", "Jaw") };
        var existing = new[] { Bone("Head", "Head_Proxy"), Bone("Jaw", "Jaw_Proxy") };
        var map = MatchHumanoidRig.KeepOwnTargets(vendor, existing,
            Ours("Head", "Jaw", "Jaw", "Jaw_Proxy", "Jaw_Proxy"), new HashSet<string> { "Head", "Jaw" }, out var kept);

        Assert.AreEqual("Head", MappedTo(map, "Head"), "Head_Proxy is gone from our model");
        Assert.AreEqual("Jaw", MappedTo(map, "Jaw"), "an ambiguous target cannot bind");
        Assert.IsEmpty(kept);
    }

    [Test]
    public void KeepOwnTargets_KeepsOurMappingOfABoneTheVendorLeavesUnmapped()
    {
        var vendor = new List<HumanBone> { Bone("Head", "Head") };
        var existing = new[] { Bone("Head", "Head"), Bone("RightEye", "RightEye_Proxy") };
        var map = MatchHumanoidRig.KeepOwnTargets(vendor, existing,
            Ours("Head", "RightEye_Proxy"), new HashSet<string> { "Head" }, out var kept);

        Assert.AreEqual("RightEye_Proxy", MappedTo(map, "RightEye"));
        Assert.AreEqual(1, kept.Count);
    }

    [Test]
    public void KeepOwnTargets_NoExistingMapping_IsTheVendorsVerbatim()
    {
        var vendor = new List<HumanBone> { Bone("Hips", "Hips"), Bone("Head", "Head") };
        var map = MatchHumanoidRig.KeepOwnTargets(vendor, new HumanBone[0], Ours("Hips", "Head"),
            new HashSet<string> { "Hips", "Head" }, out var kept);
        Assert.AreEqual(2, map.Count);
        Assert.IsEmpty(kept);
    }
}
