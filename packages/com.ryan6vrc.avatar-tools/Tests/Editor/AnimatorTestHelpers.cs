using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

// Shared test fixtures, plus the parser (Count, Notes) for TransplantCore.Finish's one-line summary.
public static class AnimatorTestHelpers
{
    // faceTrackingSets (the Blendshapes Set state behaviour) first ships in SDK 3.10.6, so every test that emits
    // or decompiles one is compiled under VRC_FT_SETS_PRESENT (the avatar-tools asmdefs' versionDefine) and, below
    // that SDK, replaced by a twin of the same name that skips with this reason, so the run names the skip.
    public const string FtSetsSkip =
        "needs com.vrchat.avatars >= 3.10.6 (VRCAnimatorFaceTrackingBlendshapesSet); TestEditor is on an older SDK";

    // One state carrying all four entry forms, in a deliberately non-alphabetical order so a reordering hop
    // shows.
    public const string FtSetsDoc =
        "schema: 1\ncontroller: FtSets_Fx\nbasis: avatar-root\nrole: fx\n" +
        "parameters:\n  Gate: float\n  Lip: float\n" +
        "layers:\n  - name: L\n    states:\n      S:\n        motion: ~\n" +
        "        behaviours:\n" +
        "          - faceTrackingSets:\n" +
        "              sets:\n" +
        "                Lids: 0.25\n" +
        "                \"FT Eyes\": Gate\n" +
        "                Mouth: { weight: 1, lerpSpeed: 10 }\n" +
        "                Cheek: { source: Lip, sourceMin: 0, sourceMax: 2, destMin: 1, destMax: 0, lerpSpeed: 30 }\n" +
        "    default: S\n";

    // Sweep for suites that call the 2-arg ControllerEmit.Build door directly. That overload persists the
    // CONTROLLER only, so every call mints side assets that outlive the test, and a survivor fails unrelated
    // suites (see EmitResult.DestroyUnpersisted).
    //
    // It sweeps both types EmitResult carries, Params and the Menu tree, and only the delta between Begin and
    // End: the domain legitimately holds unowned instances of both that a test did not create (the SDK's own, an
    // open inspector's), and destroying one of those would break the editor out from under the run.
    // EmitResult.DestroyUnpersisted is the caller's door; this is the net for a fixture that forgets.
    public sealed class UnownedSideAssetSweep
    {
        private HashSet<int> _before;

        private static IEnumerable<Object> Unowned() =>
            Resources.FindObjectsOfTypeAll<VRCExpressionParameters>().Cast<Object>()
                .Concat(Resources.FindObjectsOfTypeAll<VRCExpressionsMenu>())
                .Where(o => string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o)));

        public void Begin() => _before = new HashSet<int>(Unowned().Select(o => o.GetInstanceID()));

        public void End()
        {
            if (_before == null) return;
            foreach (var o in Unowned().Where(o => !_before.Contains(o.GetInstanceID())).ToList())
                Object.DestroyImmediate(o);
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

    // Compile/Decompile door refusals write a RunLog/Snapshot artifact outside any TestRoot teardown, so
    // refusal-path tests call this on the returned summary.
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
    public sealed class DirtyMaterialProbe
    {
        private readonly string _diskBefore;

        public DirtyMaterialProbe(string root, string name)
        {
            EnsureFolder(root);
            Path = root.TrimEnd('/') + "/" + name + ".mat";
            Material = new Material(Shader.Find("Standard"));
            Material.SetFloat("_Glossiness", 0.1f);
            AssetDatabase.CreateAsset(Material, Path);
            AssetDatabase.SaveAssetIfDirty(Material);
            _diskBefore = File.ReadAllText(Path);

            Material.SetFloat("_Glossiness", 0.9f);
            EditorUtility.SetDirty(Material);
            Assert.AreEqual(_diskBefore, File.ReadAllText(Path), "probe mutation starts only in memory");
        }

        public string Path { get; }
        public Material Material { get; }

        public void AssertWasNotSaved()
        {
            Assert.AreEqual(_diskBefore, File.ReadAllText(Path),
                "the door must not persist an unrelated dirty material");
            Assert.IsTrue(EditorUtility.IsDirty(Material),
                "the unrelated material remains dirty for its owner to restore or save");
        }
    }

}
