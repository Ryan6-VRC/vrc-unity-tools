using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    [AgentTool]
    public static class NormalizeExpressionClips
    {
        private const float TimeEpsilon = 0.0001f;

        public static string Run(
            string[] clipAssetPaths,
            bool limitToBlendshapeCurves = true,
            bool normalizeOnly = true,
            bool repairKeys = true,
            float nonZeroEpsilon = 0.001f,
            bool whatIf = false)
        {
            if (clipAssetPaths == null || clipAssetPaths.Length == 0)
                return "[NormalizeExpressionClips] FAIL: no clips given";
            var clips = new List<AnimationClip>();
            var seen = new HashSet<AnimationClip>();
            var notEditable = new List<string>();
            foreach (var p in clipAssetPaths)
            {
                var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
                if (c == null) continue;
                // Only standalone editable .anim assets can be persisted; an imported (FBX-embedded)
                // or read-only clip would silently not save — refuse rather than report a false PASS.
                if (!p.EndsWith(".anim", System.StringComparison.OrdinalIgnoreCase)
                    || AssetDatabase.IsSubAsset(c)
                    || (c.hideFlags & HideFlags.NotEditable) != 0)
                {
                    notEditable.Add(p);
                    continue;
                }
                if (seen.Add(c)) clips.Add(c); // dedupe repeated paths (would double whatIf counts)
            }
            if (notEditable.Count > 0)
                return "[NormalizeExpressionClips] FAIL: not editable .anim assets (imported/read-only can't be persisted): " + string.Join(", ", notEditable);
            if (clips.Count == 0) return "[NormalizeExpressionClips] FAIL: no valid AnimationClip among paths";

            AnimationClip[] arr = clips.ToArray();
            AnalysisResult analysis = AnalyzeClips(arr, limitToBlendshapeCurves, normalizeOnly, repairKeys, nonZeroEpsilon);

            if (whatIf)
                return $"[NormalizeExpressionClips] (whatIf) would +{analysis.totalMissingCurves} curve(s) " +
                       $"+{analysis.totalMissingKeys} key(s) -{analysis.totalCurvesToRemove} curve(s) " +
                       $"across {clips.Count} clip(s) => PASS";

            if (analysis.totalMissingCurves == 0 && analysis.totalMissingKeys == 0 && analysis.totalCurvesToRemove == 0)
                return $"[NormalizeExpressionClips] +0 curve(s) +0 key(s) -0 curve(s) across {clips.Count} clip(s) — nothing to do => PASS";

            int addedCurves = 0, addedKeys = 0, removedCurves = 0;
            Undo.RecordObjects(arr, normalizeOnly ? "Normalize Expression Clips" : "Normalize and Prune Expression Clips");
            foreach (AnimationClip clip in arr)
            {
                Dictionary<string, EditorCurveBinding> finalBindings = BuildFinalBindingSetForClip(clip, analysis);
                List<float> targetTimes = BuildTargetKeyTimesForClip(clip, finalBindings);
                foreach (KeyValuePair<string, EditorCurveBinding> pair in finalBindings)
                {
                    EditorCurveBinding binding = pair.Value;
                    AnimationCurve currentCurve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (currentCurve == null)
                    {
                        AnimationCurve zero = CreateZeroCurveAtTimes(targetTimes);
                        addedCurves++; addedKeys += zero != null ? zero.length : 0;
                        AnimationUtility.SetEditorCurve(clip, binding, zero);
                        continue;
                    }
                    if (repairKeys)
                    {
                        int addedForCurve;
                        AnimationCurve rebuilt = RebuildCurveWithAllTimes(currentCurve, targetTimes, out addedForCurve);
                        if (addedForCurve > 0) { addedKeys += addedForCurve; AnimationUtility.SetEditorCurve(clip, binding, rebuilt); }
                    }
                }
                if (!normalizeOnly)
                    foreach (string key in analysis.globallyUnusedBindings)
                        if (analysis.presentBindingsByClip[clip].Contains(key))
                        { AnimationUtility.SetEditorCurve(clip, analysis.allCandidateBindings[key], null); removedCurves++; }
                EditorUtility.SetDirty(clip);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return $"[NormalizeExpressionClips] +{addedCurves} curve(s) +{addedKeys} key(s) " +
                   $"-{removedCurves} curve(s) across {clips.Count} clip(s) => PASS";
        }

        internal static AnalysisResult AnalyzeClips(
            AnimationClip[] clips,
            bool limitToBlendshapes,
            bool normalizeOnlyMode,
            bool repairKeys,
            float epsilon)
        {
            AnalysisResult result = new AnalysisResult
            {
                presentBindingsByClip = new Dictionary<AnimationClip, HashSet<string>>(),
                allCandidateBindings = new Dictionary<string, EditorCurveBinding>(),
                globallyUsedBindings = new Dictionary<string, EditorCurveBinding>(),
                globallyUnusedBindings = new HashSet<string>()
            };

            if (clips == null || clips.Length == 0)
            {
                result.note = "No clips selected.";
                return result;
            }

            Dictionary<string, bool> globallyUsedFlags = new Dictionary<string, bool>();

            for (int clipIndex = 0; clipIndex < clips.Length; clipIndex++)
            {
                AnimationClip clip = clips[clipIndex];
                HashSet<string> presentKeys = new HashSet<string>();
                result.presentBindingsByClip[clip] = presentKeys;

                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                for (int bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
                {
                    EditorCurveBinding binding = bindings[bindingIndex];

                    if (!ShouldIncludeBinding(binding, limitToBlendshapes))
                    {
                        continue;
                    }

                    string key = MakeBindingKey(binding);
                    presentKeys.Add(key);

                    if (!result.allCandidateBindings.ContainsKey(key))
                    {
                        result.allCandidateBindings.Add(key, binding);
                        globallyUsedFlags.Add(key, false);
                    }

                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (CurveHasMeaningfulValue(curve, epsilon))
                    {
                        globallyUsedFlags[key] = true;
                    }
                }
            }

            foreach (KeyValuePair<string, EditorCurveBinding> pair in result.allCandidateBindings)
            {
                bool isUsed = globallyUsedFlags[pair.Key];
                if (isUsed)
                {
                    result.globallyUsedBindings.Add(pair.Key, pair.Value);
                }
                else
                {
                    result.globallyUnusedBindings.Add(pair.Key);
                }
            }

            result.candidateBindingCount = result.allCandidateBindings.Count;
            result.globallyUsedBindingCount = result.globallyUsedBindings.Count;

            int missingCurves = 0;
            int missingKeys = 0;
            int removals = 0;

            for (int clipIndex = 0; clipIndex < clips.Length; clipIndex++)
            {
                AnimationClip clip = clips[clipIndex];
                Dictionary<string, EditorCurveBinding> finalBindings = BuildFinalBindingSetForClip(clip, result);
                List<float> targetTimes = BuildTargetKeyTimesForClip(clip, finalBindings);

                foreach (KeyValuePair<string, EditorCurveBinding> pair in finalBindings)
                {
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, pair.Value);
                    if (curve == null)
                    {
                        // Execute creates a zero-curve at every target time (unconditionally);
                        // count those keys here so whatIf's key figure predicts execute exactly.
                        missingCurves++;
                        missingKeys += targetTimes.Count;
                    }
                    else if (repairKeys)
                    {
                        for (int i = 0; i < targetTimes.Count; i++)
                        {
                            if (!CurveHasKeyAtTime(curve, targetTimes[i]))
                            {
                                missingKeys++;
                            }
                        }
                    }
                }

                if (!normalizeOnlyMode)
                {
                    foreach (string key in result.globallyUnusedBindings)
                    {
                        if (result.presentBindingsByClip[clip].Contains(key))
                        {
                            removals++;
                        }
                    }
                }
            }

            result.totalMissingCurves = missingCurves;
            result.totalMissingKeys = missingKeys;
            result.totalCurvesToRemove = removals;

            if (limitToBlendshapes)
            {
                result.note = normalizeOnlyMode
                    ? "Filtering to SkinnedMeshRenderer blendShape.* float curves only."
                    : "Filtering to SkinnedMeshRenderer blendShape.* float curves only, with pruning for bindings unused across all selected clips.";
            }
            else
            {
                result.note = normalizeOnlyMode
                    ? "Operating on all float curves in the selected clips."
                    : "Operating on all float curves in the selected clips, with pruning for properties unused across all selected clips.";
            }

            if (repairKeys)
            {
                result.note += " Repair mode is on: every included curve in each clip will be given keys at every included key time already present in that clip.";
            }

            return result;
        }

        private static Dictionary<string, EditorCurveBinding> BuildFinalBindingSetForClip(AnimationClip clip, AnalysisResult analysis)
        {
            Dictionary<string, EditorCurveBinding> finalBindings = new Dictionary<string, EditorCurveBinding>();

            if (analysis == null || clip == null)
            {
                return finalBindings;
            }

            if (analysis.presentBindingsByClip.TryGetValue(clip, out HashSet<string> present))
            {
                if (present != null)
                {
                    foreach (string key in present)
                    {
                        if (analysis.allCandidateBindings.TryGetValue(key, out EditorCurveBinding binding))
                        {
                            if (analysis.globallyUsedBindings.ContainsKey(key) || analysis.globallyUnusedBindings.Contains(key))
                            {
                                finalBindings[key] = binding;
                            }
                        }
                    }
                }
            }

            foreach (KeyValuePair<string, EditorCurveBinding> pair in analysis.globallyUsedBindings)
            {
                finalBindings[pair.Key] = pair.Value;
            }

            return finalBindings;
        }

        private static List<float> BuildTargetKeyTimesForClip(AnimationClip clip, Dictionary<string, EditorCurveBinding> finalBindings)
        {
            List<float> targetTimes = new List<float>();

            if (clip == null)
            {
                AddUniqueTime(targetTimes, 0f);
                return targetTimes;
            }

            foreach (KeyValuePair<string, EditorCurveBinding> pair in finalBindings)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, pair.Value);
                if (curve == null)
                {
                    continue;
                }

                Keyframe[] keys = curve.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    AddUniqueTime(targetTimes, keys[i].time);
                }
            }

            if (targetTimes.Count == 0)
            {
                EditorCurveBinding[] allFloatBindings = AnimationUtility.GetCurveBindings(clip);
                for (int i = 0; i < allFloatBindings.Length; i++)
                {
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, allFloatBindings[i]);
                    if (curve == null)
                    {
                        continue;
                    }

                    Keyframe[] keys = curve.keys;
                    for (int k = 0; k < keys.Length; k++)
                    {
                        AddUniqueTime(targetTimes, keys[k].time);
                    }
                }
            }

            if (targetTimes.Count == 0)
            {
                AddUniqueTime(targetTimes, 0f);
            }

            targetTimes.Sort();
            return targetTimes;
        }

        private static AnimationCurve RebuildCurveWithAllTimes(AnimationCurve sourceCurve, List<float> targetTimes, out int addedKeyCount)
        {
            addedKeyCount = 0;

            if (sourceCurve == null)
            {
                AnimationCurve zeroCurve = CreateZeroCurveAtTimes(targetTimes);
                addedKeyCount = zeroCurve != null ? zeroCurve.length : 0;
                return zeroCurve;
            }

            if (targetTimes == null || targetTimes.Count == 0)
            {
                return sourceCurve;
            }

            List<Keyframe> rebuiltKeys = new List<Keyframe>(targetTimes.Count);
            Keyframe[] sourceKeys = sourceCurve.keys;

            for (int i = 0; i < targetTimes.Count; i++)
            {
                float time = targetTimes[i];
                int existingIndex = FindKeyIndexAtTime(sourceKeys, time);

                if (existingIndex >= 0)
                {
                    rebuiltKeys.Add(sourceKeys[existingIndex]);
                }
                else
                {
                    float value = sourceCurve.Evaluate(time);
                    rebuiltKeys.Add(new Keyframe(time, value));
                    addedKeyCount++;
                }
            }

            AnimationCurve rebuiltCurve = new AnimationCurve(rebuiltKeys.ToArray())
            {
                preWrapMode = sourceCurve.preWrapMode,
                postWrapMode = sourceCurve.postWrapMode
            };

            return rebuiltCurve;
        }

        private static AnimationCurve CreateZeroCurveAtTimes(List<float> targetTimes)
        {
            List<Keyframe> keys = new List<Keyframe>();

            if (targetTimes == null || targetTimes.Count == 0)
            {
                keys.Add(new Keyframe(0f, 0f));
            }
            else
            {
                for (int i = 0; i < targetTimes.Count; i++)
                {
                    keys.Add(new Keyframe(targetTimes[i], 0f));
                }
            }

            return new AnimationCurve(keys.ToArray());
        }

        private static bool ShouldIncludeBinding(EditorCurveBinding binding, bool limitToBlendshapeCurvesOnly)
        {
            if (!limitToBlendshapeCurvesOnly)
            {
                return true;
            }

            return binding.type == typeof(SkinnedMeshRenderer)
                && !string.IsNullOrEmpty(binding.propertyName)
                && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal);
        }

        private static bool CurveHasMeaningfulValue(AnimationCurve curve, float epsilon)
        {
            if (curve == null || curve.keys == null || curve.keys.Length == 0)
            {
                return false;
            }

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (Mathf.Abs(keys[i].value) > epsilon)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CurveHasKeyAtTime(AnimationCurve curve, float time)
        {
            if (curve == null)
            {
                return false;
            }

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (Mathf.Abs(keys[i].time - time) <= TimeEpsilon)
                {
                    return true;
                }
            }

            return false;
        }

        private static int FindKeyIndexAtTime(Keyframe[] keys, float time)
        {
            if (keys == null)
            {
                return -1;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                if (Mathf.Abs(keys[i].time - time) <= TimeEpsilon)
                {
                    return i;
                }
            }

            return -1;
        }

        private static void AddUniqueTime(List<float> times, float time)
        {
            for (int i = 0; i < times.Count; i++)
            {
                if (Mathf.Abs(times[i] - time) <= TimeEpsilon)
                {
                    return;
                }
            }

            times.Add(time);
        }

        private static string MakeBindingKey(EditorCurveBinding binding)
        {
            string typeName = binding.type != null ? binding.type.AssemblyQualifiedName : "<null>";
            string path = binding.path ?? string.Empty;
            string property = binding.propertyName ?? string.Empty;
            return typeName + "|" + path + "|" + property;
        }

        [Serializable]
        internal class AnalysisResult
        {
            public int candidateBindingCount;
            public int globallyUsedBindingCount;
            public int totalMissingCurves;
            public int totalMissingKeys;
            public int totalCurvesToRemove;
            public string note;

            public Dictionary<AnimationClip, HashSet<string>> presentBindingsByClip;
            public Dictionary<string, EditorCurveBinding> allCandidateBindings;
            public Dictionary<string, EditorCurveBinding> globallyUsedBindings;
            public HashSet<string> globallyUnusedBindings;
        }
    }
}
