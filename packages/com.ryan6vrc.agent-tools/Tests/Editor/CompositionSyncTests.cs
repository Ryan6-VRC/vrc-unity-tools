using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using VRC.SDK3.Avatars.ScriptableObjects;

/// <summary>
/// The sync section publishes two built facts and refuses to manufacture either: the figure is the SDK's
/// own cost of the built asset, and "compressed" is claimed only when VRCFury's two marks agree. The one
/// wording rule it owns is that the ceiling is never written as <c>N/MAX</c> or <c>N of MAX</c>, because a
/// reader shown <c>254/256</c> treats a build the compressor would absorb as a fault. Pure over a hand-built
/// read, so no bake and no live object is needed.
/// </summary>
public class CompositionSyncTests
{
    private static readonly Regex Ratio = new Regex(@"\d+\s*(/|of)\s*\d+");
    private static readonly int Max = VRCExpressionParameters.MAX_PARAMETER_COST;

    private static void AssertNoRatio(System.Collections.Generic.List<string> lines)
    {
        foreach (var l in lines)
            Assert.IsFalse(Ratio.IsMatch(l), "the ceiling must never read as a ratio: " + l);
    }

    [Test]
    public void UnderTheCeilingWithNeitherMark_isNo_andNamesTheConstant()
    {
        var read = new CompositionBake.SyncRead { Bits = 198 };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.AreEqual("syncedBits=198 compressor=no", keys);
        var fig = lines.Single(l => l.StartsWith("synced bits: "));
        StringAssert.Contains("did not run", fig);
        StringAssert.Contains("MAX_PARAMETER_COST of " + Max, fig);
        AssertNoRatio(lines);
    }

    [Test]
    public void BothMarks_isYes_withBothTotals_andTheComponentTextVerbatim()
    {
        const string text = "VRCFury compressed the parameters on this avatar to make them fit VRC's limit."
            + "\n\nOld Total: 311 bits\nNew Total: 250 bits\nCompressed types: Bool, Int\nSync delay: 1.2 - 2.4 seconds"
            + "\nBools per batch: 8\nNumbers per batch: 2\nBatches per sync: 6";
        var read = new CompositionBake.SyncRead { Bits = 250, Layer = true, Component = true, ComponentText = text, OldTotal = 311 };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.AreEqual("syncedBits=250 compressor=yes syncedBitsBefore=311", keys);
        var fig = lines.Single(l => l.StartsWith("synced bits: "));
        StringAssert.Contains("reduced 311", fig);
        StringAssert.Contains("includes the compressor's own channel", fig);
        int open = lines.IndexOf("```");
        Assert.GreaterOrEqual(open, 0, "the component text is fenced");
        int close = lines.IndexOf("```", open + 1);
        var fenced = string.Join("\n", lines.Skip(open + 1).Take(close - open - 1));
        Assert.AreEqual(text, fenced, "VRCFury's text is quoted, never paraphrased");
        AssertNoRatio(lines);
    }

    [Test]
    public void BothMarksButNoParsableOldTotal_keepsYes_andMarksTheBeforeUnread()
    {
        var read = new CompositionBake.SyncRead { Bits = 250, Layer = true, Component = true, ComponentText = "(unexpected text)" };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.AreEqual("syncedBits=250 compressor=yes syncedBitsBefore=unread", keys);
        AssertNoRatio(lines);
    }

    [Test]
    public void OverTheCeilingWithNeitherMark_isDeclined_andSaysAnUploadWouldBeRefused()
    {
        var read = new CompositionBake.SyncRead { Bits = Max + 55 };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.AreEqual("syncedBits=" + (Max + 55) + " compressor=declined", keys);
        StringAssert.Contains("would be refused", lines.Single(l => l.StartsWith("synced bits: ")));
        AssertNoRatio(lines);
    }

    [Test]
    public void LayerWithoutComponent_isUnread_andStillPublishesTheFigure()
    {
        var read = new CompositionBake.SyncRead { Bits = 250, Layer = true };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.AreEqual("syncedBits=250 compressor=unread", keys);
        Assert.IsTrue(lines.Any(l => l.StartsWith("synced bits: 250")));
        StringAssert.Contains("Harmony", lines.Single(l => l.Contains("could not be settled")));
        AssertNoRatio(lines);
    }

    [Test]
    public void ComponentWithoutLayer_isUnread()
    {
        var read = new CompositionBake.SyncRead { Bits = 250, Component = true, ComponentText = "Old Total: 311 bits", OldTotal = 311 };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.AreEqual("syncedBits=250 compressor=unread", keys);
        Assert.IsFalse(keys.Contains("syncedBitsBefore"), "no before-figure is claimed when compression is not settled");
        AssertNoRatio(lines);
    }

    [Test]
    public void AnUnreadTotal_publishesNoFigure()
    {
        var read = new CompositionBake.SyncRead { BitsError = "the clone has no VRCAvatarDescriptor" };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.AreEqual("syncedBits=unread compressor=unread", keys);
        Assert.IsFalse(lines.Any(l => l.StartsWith("synced bits: ")), "no number may stand in for a read that did not happen");
        Assert.IsTrue(lines.Any(l => l.Contains("no VRCAvatarDescriptor")));
    }

    [Test]
    public void AParamFilter_narrowsThePerParameterTable_andNeverTheFigure()
    {
        var read = new CompositionBake.SyncRead { Bits = 10, Params = BuiltParams() };
        string keys;
        var lines = CompositionBake.SyncSection(read, "Hair/", out keys);

        StringAssert.Contains("narrows the per-parameter table", lines[0]);
        StringAssert.Contains("Hair/", lines[0]);
        Assert.AreEqual("syncedBits=10 compressor=no", keys, "the figure is the whole avatar's under any filter");
        Assert.IsTrue(lines.Contains("| `Hair/Toggle` | Bool | no |"));
        Assert.IsFalse(lines.Any(l => l.StartsWith("| `Body/Slider` |")), "a name the filter misses is not a row");
    }

    [Test]
    public void EveryBuiltParameter_isARowUnderItsBuiltName_onEveryBranchOfTheFigure()
    {
        // The compressor branch returns early with VRCFury's text fenced; the table must still follow it,
        // because that is the build where a reader most needs to see which names stopped syncing.
        var compressed = new CompositionBake.SyncRead
        {
            Bits = 250, Layer = true, Component = true, ComponentText = "Old Total: 311 bits", OldTotal = 311,
            Params = BuiltParams(),
        };
        string keys;
        var lines = CompositionBake.SyncSection(compressed, null, out keys);

        Assert.AreEqual("syncedBits=250 compressor=yes syncedBitsBefore=311", keys);
        int header = lines.IndexOf("| built parameter | type | synced |");
        Assert.Greater(header, lines.LastIndexOf("```"), "the table sits below the quoted component text");
        Assert.IsTrue(lines.Contains("| `Hair/Toggle` | Bool | no |"));
        Assert.IsTrue(lines.Contains("| `Body/Slider` | Float | yes |"));
        AssertNoRatio(lines);
    }

    [Test]
    public void AnUnreachableParametersAsset_isNotRead_ratherThanAnEmptyTable()
    {
        var read = new CompositionBake.SyncRead { BitsError = "the clone descriptor's expressionParameters is null" };
        string keys;
        var lines = CompositionBake.SyncSection(read, null, out keys);

        Assert.IsFalse(lines.Contains("| built parameter | type | synced |"));
        Assert.IsTrue(lines.Any(l => l.Contains("built parameters were not read")));
    }

    private static System.Collections.Generic.List<CompositionBake.BuiltParam> BuiltParams() =>
        new System.Collections.Generic.List<CompositionBake.BuiltParam>
        {
            new CompositionBake.BuiltParam { Name = "Hair/Toggle", Type = "Bool", Synced = false },
            new CompositionBake.BuiltParam { Name = "Body/Slider", Type = "Float", Synced = true },
        };

    [Test]
    public void TheLayerMatch_acceptsVRCFurysFeaturePrefix_andNothingLooser()
    {
        Assert.IsTrue(CompositionBake.IsCompressorLayerName("[VF12] Parameter Compressor"), "VRCFury prefixes every layer it creates");
        Assert.IsTrue(CompositionBake.IsCompressorLayerName("[VF3] Legacy Parameter Compressor"));
        Assert.IsTrue(CompositionBake.IsCompressorLayerName("[VF3] Legacy Parameter Compressor (Math)"));
        Assert.IsTrue(CompositionBake.IsCompressorLayerName("Parameter Compressor"));
        Assert.IsFalse(CompositionBake.IsCompressorLayerName("[VF12] My Parameter Compressor Toggle"));
        Assert.IsFalse(CompositionBake.IsCompressorLayerName(null));
    }
}
