using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    public class DuplicateAndConstrainWindow : EditorWindow
    {
        private Transform sourceRoot;
        private ConstraintDirection direction = ConstraintDirection.DuplicateFollowsOriginal;
        private ConstraintKind constraintType = ConstraintKind.Rotation;

        private bool includeRootBone = true;
        private bool solveInLocalSpace = true;
        private bool stripClonedConstraintsFromDuplicate = true;
        private bool replaceExistingDrivenConstraints = false;
        private string duplicateSuffix = "_Copy";

        [MenuItem("Tools/Ryan6VRC/Duplicate and Constrain Hierarchy")]
        private static void ShowWindow()
        {
            GetWindow<DuplicateAndConstrainWindow>("Duplicate + VRC Constraints");
        }

        private void OnEnable()
        {
            if (sourceRoot == null && Selection.activeTransform != null)
            {
                sourceRoot = Selection.activeTransform;
            }
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(
                "Duplicate a hierarchy and add matching VRChat constraints between original and duplicate bones.",
                EditorStyles.wordWrappedLabel
            );
            EditorGUILayout.Space();

            sourceRoot = (Transform)EditorGUILayout.ObjectField(
                "Source Root",
                sourceRoot != null ? sourceRoot : Selection.activeTransform,
                typeof(Transform),
                true
            );

            constraintType = (ConstraintKind)EditorGUILayout.EnumPopup("Constraint Type", constraintType);
            direction = (ConstraintDirection)EditorGUILayout.EnumPopup("Direction", direction);

            includeRootBone = EditorGUILayout.Toggle("Include Root Bone", includeRootBone);
            solveInLocalSpace = EditorGUILayout.Toggle("Solve In Local Space", solveInLocalSpace);

            stripClonedConstraintsFromDuplicate = EditorGUILayout.Toggle(
                "Strip Cloned Rotation/Parent Constraints On Duplicate",
                stripClonedConstraintsFromDuplicate
            );

            replaceExistingDrivenConstraints = EditorGUILayout.Toggle(
                "Replace Existing Driven Rotation/Parent Constraints",
                replaceExistingDrivenConstraints
            );

            duplicateSuffix = EditorGUILayout.TextField("Duplicate Suffix", duplicateSuffix);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(sourceRoot == null))
            {
                if (GUILayout.Button("Duplicate And Create Constraints", GUILayout.Height(32)))
                {
                    Execute();
                }
            }

            if (sourceRoot == null)
            {
                EditorGUILayout.HelpBox(
                    "Select the armature root or drag a Transform into Source Root.",
                    MessageType.Info
                );
            }
        }

        private void Execute()
        {
            if (sourceRoot == null)
            {
                EditorUtility.DisplayDialog("No Source Root", "Please select a source root transform first.", "OK");
                return;
            }
            Debug.Log(DuplicateAndConstrain.Run(
                sourceRoot.gameObject, constraintType, direction, includeRootBone, solveInLocalSpace,
                stripClonedConstraintsFromDuplicate, replaceExistingDrivenConstraints, duplicateSuffix));
        }
    }
}
