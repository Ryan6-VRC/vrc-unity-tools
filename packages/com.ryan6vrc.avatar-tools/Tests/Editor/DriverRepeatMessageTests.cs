using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Driver = VRC.SDKBase.VRC_AvatarParameterDriver;

// A driver that repeats an operation on one parameter is refused either way — the schema holds one entry per
// (type, name), so a recompile emits a driver the vendor did not author. What these pin is that the refusal
// says only what is KNOWN. The message used to assert "commonly a mistyped parameter name", which is a guess
// about intent, and it sent a reader hunting a typo through a driver whose two entries were byte-identical.
//
// Assertions match short stable tokens and the offending VALUES, never prose — the convention the surrounding
// suites already use, so a reworded message does not rot the test while a wrong BRANCH still fails it.
public class DriverRepeatMessageTests
{
    private const string TestRoot = "Assets/Agent/Scratch/driverrepeat";

    [SetUp]
    public void SetUp() => AnimatorTestHelpers.EnsureFolder(TestRoot);

    [TearDown]
    public void TearDown()
    {
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
    }

    private static string RefusalFor(params Driver.Parameter[] ops)
    {
        var c = AnimatorController.CreateAnimatorControllerAtPath(TestRoot + "/D.controller");
        var st = c.layers[0].stateMachine.AddState("S");
        var drv = st.AddStateMachineBehaviour<VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver>();
        foreach (var p in ops) drv.parameters.Add(p);
        EditorUtility.SetDirty(c);
        AssetDatabase.SaveAssets();

        var w = ControllerDecompile.Walk(c);
        var r = w.Refusals.FirstOrDefault(x => x.Contains("repeats operation"));
        Assert.IsNotNull(r, "a repeated (type,name) must refuse; got: " + string.Join(" | ", w.Refusals));
        return r;
    }

    // The regression the finding is about: a byte-identical repeat must not be described as a probable typo.
    [Test]
    public void Same_Value_Set_Repeat_Does_Not_Blame_A_Typo()
    {
        string r = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Set, name = "X", value = 1f },
            new Driver.Parameter { type = Driver.ChangeType.Set, name = "X", value = 1f });

        StringAssert.DoesNotContain("mistyped", r, "the repeat is byte-identical — nothing here suggests a typo");
        StringAssert.Contains("same operand", r);
    }

    // The replacement must not swap one guess for another. `Add` ACCUMULATES — two identical `Add X 2` entries
    // add 4 — so "dropping one would not change runtime behaviour" is false, and a differing pair is not
    // last-write-wins either. The message states the comparison and stops.
    [Test]
    public void The_Message_Does_Not_Assert_Runtime_Consequences_It_Cannot_Know()
    {
        string same = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Add, name = "N", value = 2f },
            new Driver.Parameter { type = Driver.ChangeType.Add, name = "N", value = 2f });
        StringAssert.DoesNotContain("would not change runtime behaviour", same,
            "false for Add, which accumulates");

        string diff = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Add, name = "N", value = 2f },
            new Driver.Parameter { type = Driver.ChangeType.Add, name = "N", value = 5f });
        StringAssert.DoesNotContain("wins at runtime", diff, "not last-write-wins for Add");
        StringAssert.DoesNotContain("is dead", diff);
    }

    // "The same operand" must mean the same, not near enough: an epsilon would assert an equality the asset
    // does not contain.
    [Test]
    public void Near_But_Unequal_Values_Are_Not_Reported_As_The_Same()
    {
        string r = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Set, name = "X", value = 1f },
            new Driver.Parameter { type = Driver.ChangeType.Set, name = "X", value = 1f + 1e-7f });
        StringAssert.Contains("DIFFERENT operands", r);
    }

    [Test]
    public void Differing_Value_Set_Repeat_Names_Both_Values()
    {
        string r = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Set, name = "X", value = 1f },
            new Driver.Parameter { type = Driver.ChangeType.Set, name = "X", value = 7f });

        StringAssert.Contains("DIFFERENT operands", r);
        StringAssert.Contains("1", r);
        StringAssert.Contains("7", r);
    }

    // A driver Parameter is a union discriminated by `type`. Comparing `value` alone would call these two Copy
    // entries identical — reporting a CONTRADICTORY repeat as a harmless one, which is the same species of
    // misleading advice this split exists to end. `value` is untouched (0) on both.
    [Test]
    public void Copy_Repeat_Compares_The_Source_Not_The_Value_Field()
    {
        string r = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Copy, name = "A", source = "B" },
            new Driver.Parameter { type = Driver.ChangeType.Copy, name = "A", source = "D" });

        StringAssert.Contains("DIFFERENT operands", r, "Copy's operand is its SOURCE, not the unused value field");
        StringAssert.Contains("'B'", r);
        StringAssert.Contains("'D'", r);
    }

    [Test]
    public void Copy_Repeat_With_The_Same_Source_Reads_As_Redundant()
    {
        string r = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Copy, name = "A", source = "B" },
            new Driver.Parameter { type = Driver.ChangeType.Copy, name = "A", source = "B" });

        StringAssert.Contains("same operand", r);
        StringAssert.DoesNotContain("mistyped", r);
    }

    // Random's operand is its range, again not `value`.
    [Test]
    public void Random_Repeat_Compares_Its_Range()
    {
        string r = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Random, name = "R", valueMin = 0f, valueMax = 7f },
            new Driver.Parameter { type = Driver.ChangeType.Random, name = "R", valueMin = 0f, valueMax = 3f });

        StringAssert.Contains("DIFFERENT operands", r);
    }

    [Test]
    public void Add_Repeat_With_The_Same_Amount_Reads_As_Redundant()
    {
        string r = RefusalFor(
            new Driver.Parameter { type = Driver.ChangeType.Add, name = "N", value = 2f },
            new Driver.Parameter { type = Driver.ChangeType.Add, name = "N", value = 2f });

        StringAssert.Contains("same operand", r);
    }
}
