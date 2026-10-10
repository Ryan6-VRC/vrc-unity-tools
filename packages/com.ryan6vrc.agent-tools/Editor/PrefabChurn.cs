using System;
using System.Collections.Generic;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one home of "this serialized field is framework or editor churn, not authoring".
    ///
    /// <para><see cref="OverrideTokens"/> — the set <c>ReportPrefab</c> counts under
    /// <c>churn</c> when it classifies a prefab instance's property overrides. Matched per dot-segment of a
    /// <c>propertyPath</c> with array indices stripped, so <c>_modularAvatarVersionTag.MinimumVersion</c>
    /// and <c>animationHashSet.Array.data[3]</c> both hit. Report-side only: counting a family here hides its
    /// rows behind a count, it never discards anything.</para>
    ///
    /// <para>Not here, on purpose: Modular Avatar's <c>targetObject</c> load-time stamp (the same field is a
    /// real authored override on a ShapeChanger row; the report tells the two apart by whether the source
    /// value is null) and SkinnedMeshRenderer <c>m_Bones</c> (a variant re-points every bone, but the
    /// re-point is a no-op the report proves by corresponding-source identity, and a genuinely re-bound
    /// skin is the one thing an allowlist must not hide).</para>
    /// </summary>
    internal static class PrefabChurn
    {
        internal static readonly HashSet<string> OverrideTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            // Load-time stamps: Modular Avatar's resolving version, VRCFury's equivalent, editor selection residue
            "_modularAvatarVersionTag",
            "UpdatedAtVersion",
            "MinimumVersion",
            "vrcfuryVersion",
            "m_selectionMode",
            // VRC SDK: build stamps on the descriptor / pipeline, and the constraint solver's bookkeeping
            "animationHashSet",
            "blueprintId",
            "fallbackStatus",
            "cachedExecutionGroupIndex",
            "latestValidExecutionGroupIndex",
            // Unity-generated: the inspector's euler cache, the auto-computed skinned bounds, sibling order
            "m_LocalEulerAnglesHint",
            "m_AABB",
            "m_RootOrder",
        };

        /// <summary>ReportPrefab's question: does any segment of this propertyPath name a churn family?</summary>
        internal static bool IsChurn(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return false;
            foreach (var seg in propertyPath.Split('.'))
            {
                int b = seg.IndexOf('[');
                if (OverrideTokens.Contains(b < 0 ? seg : seg.Substring(0, b))) return true;
            }
            return false;
        }

        /// <summary>The first churn token on the path, for the per-family histogram; null when none.</summary>
        internal static string ChurnToken(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return null;
            foreach (var seg in propertyPath.Split('.'))
            {
                int b = seg.IndexOf('[');
                string s = b < 0 ? seg : seg.Substring(0, b);
                if (OverrideTokens.Contains(s)) return s;
            }
            return null;
        }
    }
}
