using System;
using System.Collections.Generic;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The one home of "this serialized field is framework or editor churn, not authoring". Two sets, one
    /// file, so a new churn family is added in one place and the two readers cannot drift apart:
    ///
    /// <para><see cref="SceneStampKeys"/> — the CLOSED, narrow set <c>SceneDivergence</c> discounts when it
    /// decides whether a scene may be reopened from disk. Matched on a YAML leaf key. Deliberately small:
    /// there is no safe side of that diff, and a sibling reorder (<c>m_RootOrder</c>) or a bone re-point
    /// (<c>m_Bones</c>) is ordinary work that gate must never swallow.</para>
    ///
    /// <para><see cref="OverrideTokens"/> ⊃ the above — the wider set <c>ReportPrefab</c> counts under
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
        internal static readonly HashSet<string> SceneStampKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "_modularAvatarVersionTag",   // Modular Avatar stamps the resolving version on load
            "UpdatedAtVersion",
            "MinimumVersion",
            "vrcfuryVersion",             // VRCFury's equivalent
            "m_selectionMode",            // editor selection residue
        };

        internal static readonly HashSet<string> OverrideTokens = new HashSet<string>(SceneStampKeys, StringComparer.Ordinal)
        {
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

        /// <summary>SceneDivergence's question: is this YAML leaf key a load-time stamp?</summary>
        internal static bool IsSceneStamp(string yamlKey)
        {
            return yamlKey != null && SceneStampKeys.Contains(yamlKey);
        }

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
