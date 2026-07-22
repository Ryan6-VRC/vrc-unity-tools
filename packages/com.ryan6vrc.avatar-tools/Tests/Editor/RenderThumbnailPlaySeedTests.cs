// Behavioral tests for RenderThumbnailPlay.SeedParameters — the param-continuity copy that keeps a
// freshly-inserted FX/pose playable from reporting controller defaults back into the emulator's two-way
// param sync (which would reset the operator's toggles runtime-wide). Pure in-memory graph build + read
// + destroy: no live-object mutation, safe headless via tools/run-editmode-tests.ps1.
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Ryan6Vrc.AvatarTools.Editor;

public class RenderThumbnailPlaySeedTests
{
    PlayableGraph _graph;
    AnimatorController _src, _dst;

    [SetUp]
    public void SetUp()
    {
        _graph = PlayableGraph.Create("__seed_test");
    }

    [TearDown]
    public void TearDown()
    {
        if (_graph.IsValid()) _graph.Destroy();
        if (_src != null) Object.DestroyImmediate(_src);
        if (_dst != null) Object.DestroyImmediate(_dst);
    }

    static AnimatorController MakeController(string name, params AnimatorControllerParameter[] pars)
    {
        var ctrl = new AnimatorController { name = name, hideFlags = HideFlags.HideAndDontSave };
        ctrl.AddLayer("base");
        foreach (var p in pars) ctrl.AddParameter(p);
        return ctrl;
    }

    static AnimatorControllerParameter P(string name, AnimatorControllerParameterType t, float f = 0f, int i = 0, bool b = false)
        => new AnimatorControllerParameter { name = name, type = t, defaultFloat = f, defaultInt = i, defaultBool = b };

    [Test]
    public void SeedParameters_CopiesMatchingValues_SkipsTriggersAndUnmatched()
    {
        _src = MakeController("src",
            P("F", AnimatorControllerParameterType.Float),
            P("I", AnimatorControllerParameterType.Int),
            P("B", AnimatorControllerParameterType.Bool),
            P("T", AnimatorControllerParameterType.Trigger),
            P("OnlySrc", AnimatorControllerParameterType.Float));
        _dst = MakeController("dst",
            P("F", AnimatorControllerParameterType.Float),
            P("I", AnimatorControllerParameterType.Int),
            P("B", AnimatorControllerParameterType.Bool),
            P("T", AnimatorControllerParameterType.Trigger),
            P("OnlyDst", AnimatorControllerParameterType.Float, f: 7f));

        var src = AnimatorControllerPlayable.Create(_graph, _src);
        var dst = AnimatorControllerPlayable.Create(_graph, _dst);
        src.SetFloat("F", 0.6f);
        src.SetInteger("I", 3);
        src.SetBool("B", true);
        src.SetTrigger("T");

        RenderThumbnailPlay.SeedParameters(src, dst);

        Assert.That(dst.GetFloat("F"), Is.EqualTo(0.6f).Within(1e-6f), "float value copied");
        Assert.That(dst.GetInteger("I"), Is.EqualTo(3), "int value copied");
        Assert.That(dst.GetBool("B"), Is.True, "bool value copied");
        Assert.That(dst.GetBool("T"), Is.False, "trigger NOT copied (transient)");
        Assert.That(dst.GetFloat("OnlyDst"), Is.EqualTo(7f).Within(1e-6f), "dest-only param keeps its default");
    }

    [Test]
    public void SeedParameters_TypeMismatch_IsSkippedNotThrown()
    {
        _src = MakeController("src", P("X", AnimatorControllerParameterType.Float, f: 0.9f));
        _dst = MakeController("dst", P("X", AnimatorControllerParameterType.Bool));

        var src = AnimatorControllerPlayable.Create(_graph, _src);
        var dst = AnimatorControllerPlayable.Create(_graph, _dst);

        Assert.DoesNotThrow(() => RenderThumbnailPlay.SeedParameters(src, dst));
        Assert.That(dst.GetBool("X"), Is.False, "mismatched-type param left at its default");
    }
}
