using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{

    public class NormalizeExpressionClipsWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Ryan6VRC/Expression Clip Normalizer";
        private const float DefaultValueEpsilon = 0.001f;

        [SerializeField] private bool limitToBlendshapeCurves = true;
        [SerializeField] private bool normalizeOnly = true;
        [SerializeField] private bool ensureEveryKeyTimeExistsOnEveryCurve = true;
        [SerializeField] private float nonZeroEpsilon = DefaultValueEpsilon;

        private AnimationClip[] selectedClips = Array.Empty<AnimationClip>();
        private NormalizeExpressionClips.AnalysisResult lastAnalysis;
        private Vector2 scroll;

        [MenuItem(MenuPath)]
        private static void OpenWindow()
        {
            GetWindow<NormalizeExpressionClipsWindow>("Clip Normalizer");
        }

        private void OnEnable()
        {
            RefreshSelection();
        }

        private void OnSelectionChange()
        {
            RefreshSelection();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Selected Animation Clips", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Select editable .anim clips in the Project window. " +
                "This tool finds the union of non-zero animated properties across the selected clips, " +
                "adds missing zero curves using only real key times already present in each clip, " +
                "and can optionally repair clips so every included curve has a key at every included key time.",
                MessageType.Info);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                limitToBlendshapeCurves = EditorGUILayout.ToggleLeft(
                    "Limit to blendshape curves only",
                    limitToBlendshapeCurves);

                normalizeOnly = EditorGUILayout.ToggleLeft(
                    "Normalize only (do not prune globally unused properties)",
                    normalizeOnly);

                ensureEveryKeyTimeExistsOnEveryCurve = EditorGUILayout.ToggleLeft(
                    "Repair clips: ensure every included curve has a key at every included key time",
                    ensureEveryKeyTimeExistsOnEveryCurve);

                nonZeroEpsilon = EditorGUILayout.FloatField("Non-zero epsilon", nonZeroEpsilon);
                if (nonZeroEpsilon < 0f)
                {
                    nonZeroEpsilon = 0f;
                }
            }

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Selection", GUILayout.Height(24f)))
                {
                    RefreshSelection();
                }

                using (new EditorGUI.DisabledScope(selectedClips.Length == 0))
                {
                    if (GUILayout.Button("Analyze", GUILayout.Height(24f)))
                    {
                        lastAnalysis = AnalyzeSelectedClips();
                    }

                    if (GUILayout.Button("Apply", GUILayout.Height(24f)))
                    {
                        ApplyToSelectedClips();
                    }
                }
            }

            EditorGUILayout.Space();

            EditorGUILayout.LabelField($"Selected clips: {selectedClips.Length}");
            if (selectedClips.Length > 0)
            {
                using (var scrollView = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.MinHeight(100f)))
                {
                    scroll = scrollView.scrollPosition;
                    for (int i = 0; i < selectedClips.Length; i++)
                    {
                        EditorGUILayout.ObjectField(selectedClips[i], typeof(AnimationClip), false);
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No editable AnimationClip assets are currently selected.", MessageType.Warning);
            }

            if (lastAnalysis != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Last Analysis", EditorStyles.boldLabel);

                using (new EditorGUILayout.VerticalScope("box"))
                {
                    EditorGUILayout.LabelField("Candidate bindings found", lastAnalysis.candidateBindingCount.ToString());
                    EditorGUILayout.LabelField("Globally used bindings", lastAnalysis.globallyUsedBindingCount.ToString());
                    EditorGUILayout.LabelField("Missing curves to add", lastAnalysis.totalMissingCurves.ToString());
                    EditorGUILayout.LabelField("Missing keys to repair", lastAnalysis.totalMissingKeys.ToString());
                    EditorGUILayout.LabelField("Curves to prune", lastAnalysis.totalCurvesToRemove.ToString());

                    if (!string.IsNullOrEmpty(lastAnalysis.note))
                    {
                        EditorGUILayout.Space();
                        EditorGUILayout.HelpBox(lastAnalysis.note, MessageType.None);
                    }
                }
            }
        }

        private void RefreshSelection()
        {
            UnityEngine.Object[] objects = Selection.GetFiltered(typeof(AnimationClip), SelectionMode.Assets | SelectionMode.Editable);
            List<AnimationClip> clips = new List<AnimationClip>();

            for (int i = 0; i < objects.Length; i++)
            {
                AnimationClip clip = objects[i] as AnimationClip;
                if (clip != null)
                {
                    clips.Add(clip);
                }
            }

            selectedClips = clips.ToArray();
            lastAnalysis = null;
        }

        private NormalizeExpressionClips.AnalysisResult AnalyzeSelectedClips()
        {
            return NormalizeExpressionClips.AnalyzeClips(
                selectedClips, limitToBlendshapeCurves, normalizeOnly, ensureEveryKeyTimeExistsOnEveryCurve, nonZeroEpsilon);
        }

        private void ApplyToSelectedClips()
        {
            if (selectedClips.Length == 0)
            {
                EditorUtility.DisplayDialog("Expression Clip Normalizer", "No editable AnimationClip assets are selected.", "OK");
                return;
            }

            var paths = new System.Collections.Generic.List<string>();
            foreach (var c in selectedClips) paths.Add(AssetDatabase.GetAssetPath(c));
            string result = NormalizeExpressionClips.Run(
                paths.ToArray(), limitToBlendshapeCurves, normalizeOnly,
                ensureEveryKeyTimeExistsOnEveryCurve, nonZeroEpsilon);
            Debug.Log(result);
            lastAnalysis = AnalyzeSelectedClips();
        }
    }
}
