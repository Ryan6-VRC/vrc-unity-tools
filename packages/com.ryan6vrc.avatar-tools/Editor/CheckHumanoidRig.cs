using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Reproportion-safety guard: the bind (humanDescription.skeleton) is frozen into the
    /// .meta at rig time and does NOT self-update. If the model geometry changed (a
    /// reproportion re-export) without re-running MatchHumanoidRig, the stored bind disagrees
    /// with the current bones -> folded hips. This asserts each humanoid bone's stored bind
    /// LOCAL POSITION still matches the current model node, FAILing (named) on any drift. Run it
    /// as an entry guard before relying on an existing rig.
    ///
    /// Position only — deliberately NOT rotation. Reproportioning moves bones (limb lengths /
    /// translations change), so position is the load-bearing signal and the one that folds the
    /// hips. Local rotation is preserved across a reproportion, and Unity stores a thumb-corrected
    /// bind rotation in skeleton[] that legitimately differs from the raw FBX node localRotation
    /// (~13deg on ThumbProximal) even on a pristine rig — so a rotation check produces guaranteed
    /// false positives on healthy rigs while catching nothing that position misses.
    /// </summary>
    [AgentTool]
    public static class CheckHumanoidRig
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;
        private const float Eps = 1e-3f;       // metres of local-position drift

        public static string Run(string ourFbxPath)
        {
            if (string.IsNullOrEmpty(ourFbxPath))
                return Fail("(null)", ourFbxPath ?? "(null)", "ourFbxPath is null or empty");

            var label = Leaf(ourFbxPath);

            var imp = AssetImporter.GetAtPath(ourFbxPath) as ModelImporter;
            if (imp == null) return Fail(label, ourFbxPath, "no ModelImporter at path");

            var hd = imp.humanDescription;
            var human = hd.human ?? new HumanBone[0];
            if (human.Length == 0)
                return Fail(label, ourFbxPath, "humanDescription.human is empty (not Humanoid)");
            var skel = hd.skeleton ?? new SkeletonBone[0];
            if (skel.Length == 0)
                return Fail(label, ourFbxPath, "humanDescription.skeleton is empty (rig never run)");

            // The built Avatar is the proof a humanoid rig actually exists; a stale/missing one means
            // there is no trustworthy rig to compare against — fail loudly before diffing.
            UnityEngine.Avatar builtAvatar = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ourFbxPath))
                if (o is UnityEngine.Avatar) { builtAvatar = (UnityEngine.Avatar)o; break; }
            if (builtAvatar == null || !builtAvatar.isHuman || !builtAvatar.isValid)
                return Fail(label, ourFbxPath,
                    builtAvatar == null ? "no built humanoid Avatar at path" : "built Avatar !isHuman/!isValid");

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ourFbxPath);
            if (model == null) return Fail(label, ourFbxPath, "could not load model to read bones");

            var nodePos = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                nodePos[t.name] = t.localPosition;

            var bindPos = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            foreach (var sb in skel) bindPos[sb.name] = sb.position;

            var drifted = new List<string>();
            int bonesChecked = 0;
            foreach (var hb in human)
            {
                var bone = hb.boneName;
                bool inBind = bindPos.ContainsKey(bone), inNode = nodePos.ContainsKey(bone);
                if (!inBind || !inNode)
                    return Fail(label, ourFbxPath, "humanoid bone '" + bone + "' missing from " +
                        (!inBind ? "stored bind" : "current model nodes") +
                        " — rig/model out of sync; re-run MatchHumanoidRig", bonesChecked);
                bonesChecked++;
                float dp = (bindPos[bone] - nodePos[bone]).magnitude;
                if (dp > Eps)
                    drifted.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}(dPos={1:F4})", bone, dp));
            }

            bool pass = drifted.Count == 0;
            string failReason = pass ? "" : " (stale bind: " + drifted[0] + "; re-run MatchHumanoidRig)";

            string head = string.Format(CultureInfo.InvariantCulture,
                "[CheckHumanoidRig] {0}: bonesChecked={1} drifted={2} => {3}{4}",
                label, bonesChecked, drifted.Count, pass ? "PASS" : "FAIL", failReason);
            return Finish(head, pass, label, ourFbxPath, bonesChecked, drifted);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private static string Fail(string label, string path, string why, int bonesChecked = 0)
        {
            string head = "[CheckHumanoidRig] " + label + ": => FAIL (" + why + ")";
            return Finish(head, false, label, path, bonesChecked, new List<string> { why });
        }

        /// <summary>Shared tail: JSON body → <see cref="RunLogFormat.WriteRunLog"/> (kind-prefixed
        /// filename, honest write-failure degradation to a trailer-less bare FAIL — the old bespoke
        /// writer swallowed a write failure into a warning and still asserted its verdict), then
        /// severity-log. Filename was <c>reproportion_freshness_…</c>, a name inherited from the
        /// tool's reproportion-flow origin that disagreed with its JSON <c>kind</c>.</summary>
        private static string Finish(string head, bool pass, string label, string path, int bonesChecked, List<string> drifted)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"check-humanoid-rig\",\n");
            sb.Append("  \"unityVersion\": ").Append(Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"ourFbx\": ").Append(Q(path)).Append(",\n");
            sb.Append("  \"bonesChecked\": ").Append(bonesChecked).Append(",\n");
            sb.Append("  \"result\": ").Append(Q(pass ? "PASS" : "FAIL")).Append(",\n");
            sb.Append("  \"drifted\": [");
            for (int i = 0; i < drifted.Count; i++)
                sb.Append(i == 0 ? "" : ", ").Append(Q(drifted[i]));
            sb.Append("]\n}");

            string res = RunLogFormat.WriteRunLog(RunLogDir, "check-humanoid-rig_" + label, head, sb.ToString(), ".json");
            // Anchored to WriteRunLog's exact success contract (summary + " | log=" + path), not a
            // floating substring.
            bool wroteLog = res.StartsWith(head + " | log=", StringComparison.Ordinal);
            if (pass && wroteLog) Debug.Log(res); else Debug.LogError(res);
            return res;
        }

        private static string Leaf(string assetPath) => RunLogFormat.Leaf(assetPath);

        private static string Q(string s) => RunLogFormat.Q(s);
    }
}
