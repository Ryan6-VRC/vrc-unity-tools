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
    /// Entry guard before relying on an existing rig: each humanoid bone's stored bind must still match
    /// the model's local position. Position only, because Unity's thumb-corrected bind rotation differs
    /// from the raw FBX node's (~13deg on ThumbProximal) even on a healthy rig.
    /// Contract: docs/unity-tools.md.
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

        private static string Fail(string label, string path, string why, int bonesChecked = 0)
        {
            string head = "[CheckHumanoidRig] " + label + ": => FAIL (" + why + ")";
            return Finish(head, false, label, path, bonesChecked, new List<string> { why });
        }

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
            bool wroteLog = res.StartsWith(head + " | log=", StringComparison.Ordinal);
            if (pass && wroteLog) Debug.Log(res); else Debug.LogError(res);
            return res;
        }

        private static string Leaf(string assetPath) => RunLogFormat.Leaf(assetPath);

        private static string Q(string s) => RunLogFormat.Q(s);

        // ── Avatar-scoped door: proxy / name-shadow divergence ───────────────────────────────────────

        private const string AvatarLabelPrefix = "[CheckHumanoidRig:avatar]";

        internal const string ScopeNoteLine =
            "scan scope: reads the humanoid mapping as the built Avatar reports it (plus the source " +
            "ModelImporter when the avatar asset is importer-backed) over the SkinnedMeshRenderers present " +
            "in the scene right now, pre-build. A generated/standalone Avatar (no ModelImporter) is not a " +
            "failure — the importer half is simply unavailable and the live mapping stands alone. " +
            "\"Skinned\" is decided by bone INSTANCE or by bone NAME, and the name arm is a heuristic: " +
            "pre-bake a mergeable's renderers skin the mergeable's OWN armature copy, which the build zips " +
            "onto the base's same-named bone, so an instance-only test reads an ordinary composed avatar as " +
            "entirely proxied. The heuristic is the merge's own matching rule — right exactly where the merge " +
            "is, and unable to tell a bone that will zip from an unrelated transform sharing a name. " +
            "The cost runs BOTH ways and `nameMasked=` is the count of the second: a mergeable carrying its own " +
            "skinned copy of a proxy node masks the base's genuine proxy row, so a nonzero count means rows were " +
            "SUPPRESSED, not merely uncertain. Bake the avatar and re-run to settle any row either direction " +
            "reaches (docs/verify.md).";

        private struct DivergenceRow { public string Kind; public string BoneLabel; public string MappedPath; public string OtherPath; }

        /// <summary>
        /// Names proxy and name-shadow divergence on a placed avatar (classes: docs/unity-tools.md).
        /// PASS: none found. CLASSIFY: findings for the agent to route, never a tool failure. FAIL: bad
        /// input, a bare line with no RunLog trailer.
        /// </summary>
        public static string InspectAvatar(string avatarRoot)
        {
            var handle = Ryan6Vrc.AgentTools.Editor.SceneHandle.Resolve(avatarRoot);
            if (!handle.Ok) return FailAvatar(handle.Refusal);
            var avatarGO = handle.Object;

            var descriptor = avatarGO.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                return FailAvatar("'" + avatarRoot + "' has no VRCAvatarDescriptor — InspectAvatar expects the avatar (descriptor) root");

            var animator = avatarGO.GetComponent<Animator>();
            if (animator == null)
                return FailAvatar("'" + avatarRoot + "' has no Animator");
            if (animator.avatar == null)
                return FailAvatar("'" + avatarRoot + "' Animator has no avatar assigned");
            if (!animator.avatar.isHuman)
                return FailAvatar("'" + avatarRoot + "' Animator.avatar is not humanoid");

            string modelPath = AssetDatabase.GetAssetPath(animator.avatar);
            string modelLabel = string.IsNullOrEmpty(modelPath) ? "(none)" : Leaf(modelPath);
            var importerMap = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(modelPath))
            {
                var imp = AssetImporter.GetAtPath(modelPath) as ModelImporter;
                if (imp != null)
                {
                    var human = imp.humanDescription.human ?? new HumanBone[0];
                    foreach (var hb in human) importerMap[hb.humanName] = hb.boneName;
                }
            }

            var liveMap = new Dictionary<HumanBodyBones, Transform>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                var t = animator.GetBoneTransform(bone);
                if (t != null) liveMap[bone] = t;
            }

            var skinned = new HashSet<Transform>();
            var skinnedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var smr in avatarGO.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.rootBone != null) { skinned.Add(smr.rootBone); skinnedNames.Add(smr.rootBone.name); }
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                var bones = smr.bones;
                if (bones == null || bones.Length == 0) continue;
                var weights = mesh.boneWeights;
                if (weights == null || weights.Length == 0) continue;
                var used = new bool[bones.Length];
                foreach (var w in weights)
                {
                    if (w.weight0 > 0f && w.boneIndex0 >= 0 && w.boneIndex0 < used.Length) used[w.boneIndex0] = true;
                    if (w.weight1 > 0f && w.boneIndex1 >= 0 && w.boneIndex1 < used.Length) used[w.boneIndex1] = true;
                    if (w.weight2 > 0f && w.boneIndex2 >= 0 && w.boneIndex2 < used.Length) used[w.boneIndex2] = true;
                    if (w.weight3 > 0f && w.boneIndex3 >= 0 && w.boneIndex3 < used.Length) used[w.boneIndex3] = true;
                }
                for (int i = 0; i < bones.Length; i++)
                    if (used[i] && bones[i] != null) { skinned.Add(bones[i]); skinnedNames.Add(bones[i].name); }
            }

            var byName = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
            foreach (var t in avatarGO.GetComponentsInChildren<Transform>(true))
            {
                if (!byName.TryGetValue(t.name, out var list)) byName[t.name] = list = new List<Transform>();
                list.Add(t);
            }

            var rows = new List<DivergenceRow>();
            var ambiguous = new List<string>();
            foreach (var kv in liveMap)
            {
                var bone = kv.Key;
                var mapped = kv.Value;
                string label = bone.ToString();

                // Why both arms: ScopeNoteLine. Measured on an ordinary composed avatar: 4 bones matched by
                // instance, 49 by name only, 0 by neither.
                bool skinnedByInstance = skinned.Contains(mapped);
                bool skinnedByNameOnly = !skinnedByInstance && skinnedNames.Contains(mapped.name);
                if (!skinnedByInstance && !skinnedByNameOnly)
                {
                    string candidate = FindProxyCandidate(mapped, skinned, skinnedNames, label);
                    rows.Add(new DivergenceRow { Kind = "proxy", BoneLabel = label, MappedPath = PathOf(mapped), OtherPath = candidate });
                }
                else if (skinnedByNameOnly)
                {
                    ambiguous.Add(label + " (" + PathOf(mapped) + "): skinned only by NAME, not by this instance");
                }

                // Where the humanoid bone IS the transform named for it, a second same-named transform is the
                // ordinary base/mergeable pair the build is about to zip — flagging that manufactures an
                // offender on every composed avatar.
                if (!string.Equals(mapped.name, label, StringComparison.Ordinal)
                    && byName.TryGetValue(label, out var named))
                    foreach (var other in named)
                    {
                        if (other == mapped) continue;
                        rows.Add(new DivergenceRow { Kind = "name-shadow", BoneLabel = label, MappedPath = PathOf(mapped), OtherPath = PathOf(other) });
                        break;
                    }
            }

            return FinishAvatar(avatarGO, liveMap.Count, modelLabel, importerMap, rows, ambiguous);
        }

        // `Skinned` must match the classify site's instance-or-name test: instance alone finds no candidate
        // on a composed avatar.
        private static string FindProxyCandidate(Transform mapped, HashSet<Transform> skinned,
                                                 HashSet<string> skinnedNames, string label)
        {
            bool Skinned(Transform t) => skinned.Contains(t) || skinnedNames.Contains(t.name);
            string stripped = StripProxySuffix(mapped.name);
            if (mapped.parent != null)
                foreach (Transform sib in mapped.parent)
                {
                    if (sib == mapped || !Skinned(sib)) continue;
                    if (sib.name == label || sib.name == stripped) return PathOf(sib);
                }
            foreach (var d in mapped.GetComponentsInChildren<Transform>(true))
            {
                if (d == mapped || !Skinned(d)) continue;
                if (d.name == label || d.name == stripped) return PathOf(d);
            }
            return null;
        }

        private static string StripProxySuffix(string name)
        {
            const string suffix = "_Proxy";
            return name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - suffix.Length)
                : name;
        }

        private static string FailAvatar(string why)
        {
            string err = AvatarLabelPrefix + " FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        private static string FinishAvatar(GameObject root, int bonesChecked, string modelLabel,
            Dictionary<string, string> importerMap, List<DivergenceRow> rows, List<string> ambiguous)
        {
            int proxyCount = 0, shadowCount = 0;
            foreach (var r in rows) { if (r.Kind == "proxy") proxyCount++; else shadowCount++; }
            bool pass = rows.Count == 0;

            string head = string.Format(CultureInfo.InvariantCulture,
                "{0} {1}: bones={2} proxy={3} nameShadow={4} nameMasked={5} model={6} => {7}",
                AvatarLabelPrefix, root.name, bonesChecked, proxyCount, shadowCount, ambiguous.Count, modelLabel,
                pass ? "PASS" : "CLASSIFY");

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"check-humanoid-rig-avatar\",\n");
            sb.Append("  \"unityVersion\": ").Append(Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"avatarRoot\": ").Append(Q(PathOf(root.transform))).Append(",\n");
            sb.Append("  \"model\": ").Append(Q(modelLabel)).Append(",\n");
            sb.Append("  \"bonesChecked\": ").Append(bonesChecked).Append(",\n");
            sb.Append("  \"result\": ").Append(Q(pass ? "PASS" : "CLASSIFY")).Append(",\n");
            sb.Append("  \"scope\": ").Append(Q(ScopeNoteLine)).Append(",\n");
            // Masked rows do not change the verdict, so they ride the summary as their own count: a PASS
            // beside a nonzero nameMasked= is not the same claim as a clean one.
            sb.Append("  \"nameMasked\": [");
            for (int i = 0; i < ambiguous.Count; i++) sb.Append(i == 0 ? "" : ", ").Append(Q(ambiguous[i]));
            sb.Append("],\n");

            sb.Append("  \"importerMap\": {");
            bool firstImp = true;
            foreach (var kv in importerMap)
            {
                sb.Append(firstImp ? "" : ", ").Append(Q(kv.Key)).Append(": ").Append(Q(kv.Value));
                firstImp = false;
            }
            sb.Append("},\n");

            sb.Append("  \"offenders\": [");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append(i == 0 ? "" : ", ").Append("{\"class\": ").Append(Q(r.Kind))
                  .Append(", \"bone\": ").Append(Q(r.BoneLabel))
                  .Append(", \"mapped\": ").Append(Q(r.MappedPath))
                  .Append(", \"other\": ").Append(Q(r.OtherPath ?? "(no candidate found)"))
                  .Append("}");
            }
            sb.Append("]\n}");

            string res = RunLogFormat.WriteRunLog(RunLogDir, "check-humanoid-rig-avatar_" + root.name, head, sb.ToString(), ".json");
            bool wroteLog = res.StartsWith(head + " | log=", StringComparison.Ordinal);
            if (pass && wroteLog) Debug.Log(res); else Debug.LogWarning(res);
            return res;
        }

        private static string PathOf(Transform t)
        {
            if (t == null) return "—";
            var sb = new StringBuilder(t.name);
            var cur = t;
            while (cur.parent != null) { cur = cur.parent; sb.Insert(0, cur.name + "/"); }
            return sb.ToString();
        }
    }
}
