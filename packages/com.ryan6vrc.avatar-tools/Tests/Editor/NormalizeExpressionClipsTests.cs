using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class NormalizeExpressionClipsTests
{
    private const string Root = "Assets/Agent/Scratch/NormalizeExpressionClips_NUnit";

    [SetUp]
    public void SetUp() => AnimatorTestHelpers.EnsureFolder(Root);

    [TearDown]
    public void TearDown() => AssetDatabase.DeleteAsset(Root);

    [Test]
    public void Normalize_Persists_The_Clips_Without_Saving_Unrelated_Dirty_Assets()
    {
        string sourcePath = Root + "/Source.anim";
        string targetPath = Root + "/Target.anim";
        var source = AnimatorTestHelpers.MakeClip(sourcePath);
        AnimatorTestHelpers.AddFloatCurve(source, "Body", typeof(SkinnedMeshRenderer), "blendShape.Probe");
        AnimatorTestHelpers.Save(source, sourcePath);
        AnimatorTestHelpers.MakeClip(targetPath);
        var probe = new AnimatorTestHelpers.DirtyMaterialProbe(Root, "Unrelated");

        string result = NormalizeExpressionClips.Run(new[] { sourcePath, targetPath });

        StringAssert.Contains("=> PASS", result);
        Assert.IsTrue(AnimatorTestHelpers.ClipHasBinding(targetPath, "Body"),
            "the missing target curve persisted");
        probe.AssertWasNotSaved();
    }
}
