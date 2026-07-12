using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Shared fixtures + TransplantCore.Finish one-line-summary parser for the mutating-tool test files
// (RepathClips, OwnControllerClips). CleanController uses its own summary parser (different grammar) but
// reuses the grammar-agnostic fixture plumbing here (EnsureFolder, Save, etc.).
public static class AnimatorTestHelpers
{
    public static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        string parent = path.Substring(0, slash), leaf = path.Substring(slash + 1);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    public static void Save(AnimatorController ctrl, string path)
    {
        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path);
    }

    public static void Save(AnimationClip clip, string path)
    {
        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path);
    }

    public static AnimationClip MakeClip(string path)
    {
        var clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(path) };
        AssetDatabase.CreateAsset(clip, path);
        AssetDatabase.SaveAssets();
        return clip;
    }

    public static void AddFloatCurve(AnimationClip clip, string bindingPath, System.Type type, string prop, float v = 1f)
    {
        var b = EditorCurveBinding.FloatCurve(bindingPath, type, prop);
        AnimationUtility.SetEditorCurve(clip, b, AnimationCurve.Linear(0, v, 1, v));
    }

    public static void AddObjRefCurve(AnimationClip clip, string bindingPath, string prop, Object value)
    {
        var b = EditorCurveBinding.PPtrCurve(bindingPath, typeof(MeshRenderer), prop);
        AnimationUtility.SetObjectReferenceCurve(clip, b,
            new[] { new ObjectReferenceKeyframe { time = 0, value = value } });
    }

    public static bool ClipHasBinding(string clipPath, string bindingPath)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        foreach (var b in AnimationUtility.GetCurveBindings(clip))
            if (b.path == bindingPath) return true;
        return false;
    }

    public static bool HasSubObjectNamed(string path, string name)
    {
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            if (o != null && o.name == name) return true;
        return false;
    }

    public static void AddSyncedLayer(AnimatorController ctrl, int sourceIndex = 0)
    {
        var synced = new AnimatorControllerLayer { name = "SyncedLayer", syncedLayerIndex = sourceIndex, defaultWeight = 1f };
        var list = new List<AnimatorControllerLayer>(ctrl.layers) { synced };
        ctrl.layers = list.ToArray();
    }

    // Compile/Decompile door refusals write a RunLog/Snapshot artifact (R4) outside any TestRoot
    // teardown — refusal-path tests call this on the returned summary so artifacts don't accumulate
    // across runs.
    public static void DeleteRefusalArtifact(string summary)
    {
        int i = summary.IndexOf("log=", System.StringComparison.Ordinal);
        if (i >= 0) AssetDatabase.DeleteAsset(summary.Substring(i + 4));
    }

    // Grammar: "[kind] (whatIf) label: k1=v1, k2=v2 offenders=[…] notes=[…] warnings=[…] error=… => RESULT | log=…"
    public static int Count(string summary, string key)
    {
        int i = summary.IndexOf(key + "=");
        Assert.GreaterOrEqual(i, 0, "count '" + key + "' missing in: " + summary);
        i += key.Length + 1;
        int j = i;
        while (j < summary.Length && char.IsDigit(summary[j])) j++;
        return int.Parse(summary.Substring(i, j - i));
    }

    public static List<string> Notes(string summary)
    {
        var list = new List<string>();
        int ni = summary.IndexOf("notes=[");
        if (ni < 0) return list;
        int e = summary.IndexOf("]", ni);
        string body = summary.Substring(ni + 7, e - (ni + 7));
        foreach (var p in body.Split(';'))
        {
            var s = p.Trim();
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }
}
