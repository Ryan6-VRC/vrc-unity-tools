using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

// Shared fixtures + TransplantCore.Finish one-line-summary parser for the mutating-tool test files
// (RepathClips, OwnControllerClips). CleanController uses its own summary parser (different grammar) but
// reuses the grammar-agnostic fixture plumbing here (EnsureFolder, Save, etc.).
public static class AnimatorTestHelpers
{
    // Sweep for suites that call the 2-arg ControllerEmit.Build door directly. That overload targets a
    // throwaway scratch dir and NOTHING persists its EmitResult.Params, so every call mints a
    // VRCExpressionParameters that outlives the test — and a survivor is not inert: leaked SDK
    // ScriptableObjects in this assembly make ControllerEmit.AddStateMachineBehaviour return null and fail
    // UNRELATED suites with NullReferenceExceptions pointing at untouched production code
    // (ControllerFixpointTests's `_made` field owns the measurement: leak → 551/600, destroy → 600/600).
    //
    // DELTA, not a blanket destroy of every unowned instance: the domain legitimately holds unowned
    // VRCExpressionParameters that a test did not create — the SDK's own, an open inspector's — and
    // destroying one of those would break the editor out from under the run. Only what appeared between
    // Begin and End is this test's to clean up.
    //
    // Production code destroying its own side assets is CompileController's job and is covered by
    // SideAssetLifecycleTests; this is strictly the TEST-side half, for the door that has no owner at all.
    public sealed class UnownedParamsSweep
    {
        private HashSet<int> _before;

        private static IEnumerable<VRCExpressionParameters> Unowned() =>
            Resources.FindObjectsOfTypeAll<VRCExpressionParameters>()
                .Where(p => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(p)));

        public void Begin() => _before = new HashSet<int>(Unowned().Select(p => p.GetInstanceID()));

        public void End()
        {
            if (_before == null) return;
            foreach (var p in Unowned().Where(p => !_before.Contains(p.GetInstanceID())).ToList())
                Object.DestroyImmediate(p);
            _before = null;
        }
    }

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
