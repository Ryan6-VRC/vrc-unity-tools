using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

public class CleanControllerSelectLayersTests
{
    static readonly string[] Layers = { "Base Layer", "Default", "LeftHand", "RightHand", "Visible_X" };

    [Test]
    public void Keeps_named_layers_plus_base_zero()
    {
        string err;
        var idx = CleanController.SelectLayersToKeep(Layers, new[] { "LeftHand", "RightHand" }, out err);
        Assert.IsNull(err);
        Assert.AreEqual(new[] { 0, 2, 3 }, idx); // 0 always kept even though unnamed
    }

    [Test]
    public void Errors_on_absent_name()
    {
        string err;
        var idx = CleanController.SelectLayersToKeep(Layers, new[] { "Nope" }, out err);
        Assert.IsNotNull(err);
        StringAssert.Contains("Nope", err);
        Assert.IsNull(idx);
    }

    [Test]
    public void Errors_on_ambiguous_name()
    {
        var dup = new[] { "Base Layer", "Hand", "Hand" };
        string err;
        var idx = CleanController.SelectLayersToKeep(dup, new[] { "Hand" }, out err);
        Assert.IsNotNull(err);
        StringAssert.Contains("Hand", err);
        Assert.IsNull(idx);
    }
}
