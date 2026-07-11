using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// CheckSeam proof obligations (spec 2026-07-11-checkseam-design.md, plan 2026-07-11-checkseam.md).
//
// CheckSeam.Check resolves scene paths against the ACTIVE scene (its local Resolve/FindByHierarchyPath), so —
// like CheckAvatarTests — fixtures live in a throwaway ADDITIVE scene set active in SetUp and torn down in
// place; nothing is ever saved to the real project. The pure-core logic is exercised by injecting fake seams
// (ResolveSeam / ResolveHumanoid) via reflection (Tests is a separate assembly), the same seams that carry the
// real MA/VRCFury reflection live. The seams are captured in SetUp and restored in TearDown.
[Category("CheckSeam")]
public class CheckSeamTests
{
    private GameObject _root;
    private string _logPath;
    private object _origResolveSeam, _origResolveHumanoid;

    [SetUp]
    public void SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // REFUSE paths log at error/warning — expected in negative tests
        // Batchmode boots on an untitled, unsaved scene; NewScene(Additive) throws against that ("Cannot create
        // a new scene additively with an untitled scene unsaved") and a freshly created untitled scene is itself
        // dirty, so additive is never allowed when a test runs in isolation (CheckAvatarTests only survives it
        // because an earlier full-suite fixture leaves a saved scene active). Use a Single throwaway scene: our
        // fixtures live in it (the active scene, which is exactly what CheckSeam.Resolve searches). Never saved.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        _root = new GameObject("CheckSeamTestRoot");
        _origResolveSeam = GetSeam("ResolveSeam");
        _origResolveHumanoid = GetSeam("ResolveHumanoid");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
        _root = null;
        ResetSeams();
        // _tmpScene is the only open scene (Single) and so cannot be closed; the next SetUp's Single NewScene
        // replaces it, discarding fixtures. It is never saved — nothing persists to the project.
        if (!string.IsNullOrEmpty(_logPath)) AssetDatabase.DeleteAsset(_logPath);
        _logPath = null;
        LogAssert.ignoreFailingMessages = false;
    }

    // ── Reflection helpers (resolve real types + flip internal seams) ───────────────────────────────

    private static Type Resolve(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.FullName == fullName);

    private static void SetSeam(string field, object value) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, value);

    private static object GetSeam(string field) =>
        typeof(CheckSeam).GetField(field, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

    private void ResetSeams()
    {
        SetSeam("ResolveSeam", _origResolveSeam);
        SetSeam("ResolveHumanoid", _origResolveHumanoid);
    }

    private string ReadLog(string result)
    {
        const string marker = "| log=";
        int i = result.IndexOf(marker, StringComparison.Ordinal);
        _logPath = i < 0 ? null : result.Substring(i + marker.Length).Trim();
        return _logPath != null && File.Exists(_logPath) ? File.ReadAllText(_logPath) : "";
    }

    // ── Fixture builders ────────────────────────────────────────────────────────────────────────────

    private GameObject NewChild(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    // Hierarchy path of a fixture GO (matches CheckSeam.Resolve's FindByHierarchyPath contract).
    private static string Path(GameObject go)
    {
        var t = go.transform;
        var sb = new System.Text.StringBuilder(t.name);
        while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
        return sb.ToString();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void BadInput_baseNotFound_bareRefuse_noTrailer()
    {
        // A bare REFUSE logs at Error (Refuse → Debug.LogError); consume it so the test doesn't flag it.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] REFUSE:"));
        var r = CheckSeam.Check("no-such-base", "no-such-merge");
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("no-such-base", r);
        Assert.IsFalse(r.Contains("| log="), "refusal carries no RunLog trailer");
    }

    [Test]
    public void NoHumanoidAvatar_refuses()
    {
        // A base with no humanoid Animator → DefaultResolveHumanoid returns an empty map. Inject the empty map
        // directly so this proves the door's empty-bucket REFUSE path independently of the reflection default.
        LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[CheckSeam\] REFUSE:"));
        var baseGO = NewChild(_root, "Base");     // plain GO, no humanoid Animator
        var mergeGO = NewChild(_root, "Merge");
        CheckSeam.ResolveHumanoid = _ => new CheckSeam.HumanoidMap(); // empty ⇒ REFUSE upstream
        var r = CheckSeam.Check(Path(baseGO), Path(mergeGO));
        StringAssert.StartsWith("[CheckSeam] REFUSE:", r);
        StringAssert.Contains("no humanoid", r);
    }
}
