using System.Collections.Generic;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Covers the resolver itself; each door's own tests cover that its envelope carries the refusal through.
//
// The out-of-scene case uses a preview scene, valid and loaded but never active, because NewScene(Additive)
// throws on the batchmode venue's unsaved untitled scene.
//
// Objects are built and read, never mutated (docs/verify.md §Test venue).
public class SceneHandleTests
{
    private readonly List<GameObject> _roots = new List<GameObject>();
    private Scene _other;
    private bool _otherOpen;

    private GameObject Go(string name, GameObject parent = null)
    {
        var go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent.transform, false);
        else _roots.Add(go);
        return go;
    }

    private GameObject GoInOther(string name)
    {
        if (!_otherOpen) { _other = EditorSceneManager.NewPreviewScene(); _otherOpen = true; }
        var go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, _other);
        return go;
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var go in _roots) if (go != null) Object.DestroyImmediate(go);
        _roots.Clear();
        if (_otherOpen) { EditorSceneManager.ClosePreviewScene(_other); _otherOpen = false; }
    }

    // ── The ladder ────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void UniqueRoot_ResolvesByPath()
    {
        var root = Go("SH_Root");
        var r = SceneHandle.Resolve("SH_Root");
        Assert.That(r.Ok, Is.True, r.Refusal);
        Assert.That(r.Object, Is.SameAs(root));
    }

    [Test]
    public void NestedPath_Resolves()
    {
        var root = Go("SH_Root");
        var child = Go("SH_Child", root);
        var r = SceneHandle.Resolve("SH_Root/SH_Child");
        Assert.That(r.Ok, Is.True, r.Refusal);
        Assert.That(r.Object, Is.SameAs(child));
    }

    [Test]
    public void UniqueBareName_ResolvesByNameTail()
    {
        var root = Go("SH_Root");
        var child = Go("SH_OnlyOne", root);
        var r = SceneHandle.Resolve("SH_OnlyOne");
        Assert.That(r.Ok, Is.True, r.Refusal);
        Assert.That(r.Object, Is.SameAs(child));
    }

    [Test]
    public void InstanceId_InDomain_Resolves()
    {
        var root = Go("SH_Root");
        var r = SceneHandle.Resolve(root.GetInstanceID().ToString());
        Assert.That(r.Ok, Is.True, r.Refusal);
        Assert.That(r.Object, Is.SameAs(root));
    }

    [Test]
    public void EmptyHandle_IsNotFound_AndDoesNotThrow()
    {
        var r = SceneHandle.Resolve("");
        Assert.That(r.Outcome, Is.EqualTo(SceneHandleOutcome.NotFound));
        Assert.That(r.Refusal, Is.Not.Null.And.Not.Empty);
    }

    // ── Ambiguity ─────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void DuplicateBareName_RefusesNamingEveryMatchWithPathAndId()
    {
        var a = Go("SH_RootA");
        var b = Go("SH_RootB");
        var ca = Go("SH_Dup", a);
        var cb = Go("SH_Dup", b);

        var r = SceneHandle.Resolve("SH_Dup");
        Assert.That(r.Outcome, Is.EqualTo(SceneHandleOutcome.Ambiguous));
        Assert.That(r.Refusal, Does.Contain("SH_RootA/SH_Dup").And.Contain("SH_RootB/SH_Dup"),
            "the refusal hands back the hierarchy paths that disambiguate");
        Assert.That(r.Refusal, Does.Contain(ca.GetInstanceID().ToString())
                                 .And.Contain(cb.GetInstanceID().ToString()),
            "and the instance ids, which are the handle of last resort when two paths are identical");
    }

    [Test]
    public void DuplicateRootNames_RefuseOnThePathBranchToo()
    {
        Go("SH_SameRoot");
        Go("SH_SameRoot");
        var r = SceneHandle.Resolve("SH_SameRoot");
        Assert.That(r.Outcome, Is.EqualTo(SceneHandleOutcome.Ambiguous),
            "duplicate root names must refuse as Ambiguous on the path branch, never resolve first-wins");
    }

    /// <summary>Transform.Find returns the FIRST same-named child, so an outfit's own Armature beside the base's
    /// would silently pick one at any depth. The descent has to branch for the refusal to hold on paths.</summary>
    [Test]
    public void DuplicateNamedSiblings_RefuseMidPath()
    {
        var root = Go("SH_Avatar");
        var armA = Go("Armature", root);
        var armB = Go("Armature", root);
        var hipsA = Go("Hips", armA);
        var hipsB = Go("Hips", armB);

        var r = SceneHandle.Resolve("SH_Avatar/Armature/Hips");
        Assert.That(r.Outcome, Is.EqualTo(SceneHandleOutcome.Ambiguous));
        Assert.That(r.Refusal, Does.Contain(hipsA.GetInstanceID().ToString())
                                 .And.Contain(hipsB.GetInstanceID().ToString()));
    }

    // ── Outside the active scene ──────────────────────────────────────────────────────────────────────

    [Test]
    public void InstanceId_OutsideTheDomain_IsOutOfDomain_NotNotFound()
    {
        var elsewhere = GoInOther("SH_Elsewhere");
        var r = SceneHandle.Resolve(elsewhere.GetInstanceID().ToString());
        Assert.That(r.Outcome, Is.EqualTo(SceneHandleOutcome.OutOfDomain),
            "the id rung is the only one that can reach out of the domain, so it is the only one that must say so");
        Assert.That(r.Refusal, Does.Contain("SH_Elsewhere"));
    }

}
