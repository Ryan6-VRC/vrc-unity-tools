using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    public enum ConstraintKind { Rotation, Parent }
    public enum ConstraintDirection { DuplicateFollowsOriginal, OriginalFollowsDuplicate }

    [AgentTool]
    public static class ConstrainedDuplicate
    {
        public static string Run(
            GameObject sourceRoot,
            ConstraintKind kind = ConstraintKind.Rotation,
            ConstraintDirection direction = ConstraintDirection.DuplicateFollowsOriginal,
            bool includeRootBone = true,
            bool solveInLocalSpace = true,
            bool stripClonedConstraints = true,
            bool replaceExisting = false,
            string duplicateSuffix = "_Copy",
            bool whatIf = false)
        {
            if (sourceRoot == null) return "[ConstrainedDuplicate] FAIL: sourceRoot is null";
            Transform src = sourceRoot.transform;
            string dupName = string.IsNullOrWhiteSpace(duplicateSuffix) ? src.name + "(Clone)" : src.name + duplicateSuffix;

            if (whatIf)
            {
                Dictionary<string, Transform> map = BuildPathMap(src, includeRootBone);
                int wc = 0, ws = 0;
                foreach (KeyValuePair<string, Transform> kv in map)
                {
                    bool drivenHasConstraint = (direction == ConstraintDirection.DuplicateFollowsOriginal)
                        ? (!stripClonedConstraints && HasVRCConstraint(kv.Value))
                        : HasVRCConstraint(kv.Value);
                    if (drivenHasConstraint && !replaceExisting) ws++; else wc++;
                }
                return $"[ConstrainedDuplicate] (whatIf) would duplicate '{src.name}' as '{dupName}', " +
                       $"{kind}/{direction}, {wc} constraint(s), {ws} skipped => PASS";
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Duplicate Hierarchy + VRC Constraints");

            GameObject dupObj = DuplicateHierarchyObject(sourceRoot);
            Transform dup = dupObj.transform;
            if (!string.IsNullOrWhiteSpace(duplicateSuffix)) dup.name = dupName;
            if (src.parent != null)
                dup.SetSiblingIndex(Mathf.Min(src.GetSiblingIndex() + 1, src.parent.childCount - 1));
            if (stripClonedConstraints) StripSupportedConstraintsFromHierarchy(dup);

            Dictionary<string, Transform> originalMap = BuildPathMap(src, includeRootBone);
            Dictionary<string, Transform> duplicateMap = BuildPathMap(dup, includeRootBone);
            int created = 0, skipped = 0;
            foreach (KeyValuePair<string, Transform> pair in originalMap)
            {
                if (!duplicateMap.TryGetValue(pair.Key, out Transform dupBone))
                {
                    Debug.LogWarning($"[ConstrainedDuplicate] no matching duplicate bone for '{pair.Key}' — skipping.");
                    skipped++;
                    continue;
                }
                Transform driver = direction == ConstraintDirection.DuplicateFollowsOriginal ? pair.Value : dupBone;
                Transform driven = direction == ConstraintDirection.DuplicateFollowsOriginal ? dupBone : pair.Value;
                if (TryCreateOrReplaceConstraint(driven, driver, kind, solveInLocalSpace, replaceExisting)) created++;
                else skipped++;
            }

            EditorUtility.SetDirty(sourceRoot);
            EditorUtility.SetDirty(dupObj);
            Selection.activeGameObject = dupObj;
            Undo.CollapseUndoOperations(undoGroup);

            return $"[ConstrainedDuplicate] '{dup.name}' created, {created} constraint(s), {skipped} skipped, " +
                   $"root {GetHierarchyPath(dup)} => PASS";
        }

        private static bool HasVRCConstraint(Transform t)
            => t.GetComponent<VRCRotationConstraint>() != null || t.GetComponent<VRCParentConstraint>() != null;

        private static GameObject DuplicateHierarchyObject(GameObject source)
        {
            Transform parent = source.transform.parent;
            GameObject clone = UnityEngine.Object.Instantiate(source, parent);
            Undo.RegisterCreatedObjectUndo(clone, "Duplicate Hierarchy");
            clone.transform.localPosition = source.transform.localPosition;
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localScale = source.transform.localScale;
            clone.SetActive(source.activeSelf);
            return clone;
        }

        private static void StripSupportedConstraintsFromHierarchy(Transform root)
        {
            foreach (VRCRotationConstraint c in root.GetComponentsInChildren<VRCRotationConstraint>(true))
                Undo.DestroyObjectImmediate(c);
            foreach (VRCParentConstraint c in root.GetComponentsInChildren<VRCParentConstraint>(true))
                Undo.DestroyObjectImmediate(c);
        }

        private static bool TryCreateOrReplaceConstraint(
            Transform driven, Transform driver, ConstraintKind kind, bool solveInLocalSpace, bool replaceExisting)
        {
            if (driven == null || driver == null) return false;
            Component existingRotation = driven.GetComponent<VRCRotationConstraint>();
            Component existingParent = driven.GetComponent<VRCParentConstraint>();
            if (existingRotation != null || existingParent != null)
            {
                if (!replaceExisting)
                {
                    Debug.LogWarning($"Skipped '{driven.name}' because it already has a VRChat rotation and/or parent constraint.", driven.gameObject);
                    return false;
                }
                if (existingRotation != null) Undo.DestroyObjectImmediate(existingRotation);
                if (existingParent != null) Undo.DestroyObjectImmediate(existingParent);
            }
            switch (kind)
            {
                case ConstraintKind.Rotation:
                {
                    VRCRotationConstraint c = Undo.AddComponent<VRCRotationConstraint>(driven.gameObject);
                    return ConfigureConstraint(c, driver, solveInLocalSpace);
                }
                case ConstraintKind.Parent:
                {
                    VRCParentConstraint c = Undo.AddComponent<VRCParentConstraint>(driven.gameObject);
                    return ConfigureConstraint(c, driver, solveInLocalSpace);
                }
                default: return false;
            }
        }

        private static bool ConfigureConstraint(VRCConstraintBase constraint, Transform driver, bool solveInLocalSpace)
        {
            try
            {
                constraint.TargetTransform = null;
                constraint.SolveInLocalSpace = solveInLocalSpace;
                constraint.GlobalWeight = 1f;
                constraint.IsActive = true;
                if (!TryReplaceSources(constraint, driver))
                {
                    Debug.LogWarning($"Could not set Sources on '{constraint.gameObject.name}'. The {constraint.GetType().Name} was added but may need manual source setup.", constraint.gameObject);
                    return false;
                }
                constraint.ApplyConfigurationChanges();
                constraint.ZeroConstraint();
                constraint.Locked = true;
                constraint.ApplyConfigurationChanges();
                EditorUtility.SetDirty(constraint);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to configure {constraint.GetType().Name} on '{constraint.gameObject.name}': {ex}", constraint.gameObject);
                return false;
            }
        }

        private static bool TryReplaceSources(VRCConstraintBase constraint, Transform driver)
        {
            if (constraint == null || driver == null) return false;
            try
            {
                var sources = constraint.Sources;
                sources.Clear();
                sources.Add(new VRCConstraintSource(driver, 1f));
                constraint.Sources = sources;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Direct Sources assignment failed: {ex.Message}", constraint);
                return false;
            }
        }

        private static Dictionary<string, Transform> BuildPathMap(Transform root, bool includeRoot)
        {
            Dictionary<string, Transform> map = new Dictionary<string, Transform>();
            if (includeRoot) map[string.Empty] = root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                BuildPathMapRecursive(child, GetSegmentKey(child), map);
            }
            return map;
        }

        private static void BuildPathMapRecursive(Transform current, string currentPath, Dictionary<string, Transform> map)
        {
            map[currentPath] = current;
            for (int i = 0; i < current.childCount; i++)
            {
                Transform child = current.GetChild(i);
                BuildPathMapRecursive(child, currentPath + "/" + GetSegmentKey(child), map);
            }
        }

        private static string GetSegmentKey(Transform t)
        {
            return $"{t.name}[{IndexedPath.SiblingIndexAmongSameName(t)}]";
        }

        private static string GetHierarchyPath(Transform t)
        {
            var stack = new Stack<string>();
            for (Transform c = t; c != null; c = c.parent) stack.Push(c.name);
            return string.Join("/", stack.ToArray());
        }
    }
}
