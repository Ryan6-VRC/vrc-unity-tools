using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Conforms our owned FBX's humanoid avatar to the vendor avatar's bone mapping and muscle settings,
    /// correcting auto-mapper mistakes (e.g. Jaw → hair bone). Supplying our own skeleton[] in the assembled
    /// humanDescription is what makes Unity honor the explicit mapping and limits instead of re-running its
    /// auto-mapper.
    /// Contract: docs/unity-tools.md.
    /// </summary>
    [AgentTool]
    public static class MatchHumanoidRig
    {
        public static string Run(string ourFbxPath, GameObject vendorAvatarSource)
        {
            // ── 1. Resolve importers ──────────────────────────────────────────────────────────

            if (string.IsNullOrEmpty(ourFbxPath))
                return Fail(ourFbxPath ?? "(null)", "ourFbxPath is null or empty.");

            var ourImp = AssetImporter.GetAtPath(ourFbxPath) as ModelImporter;
            if (ourImp == null)
                return Fail(ourFbxPath, "No ModelImporter found at: " + ourFbxPath
                    + " — is the path correct and does the file exist in the AssetDatabase?");

            if (vendorAvatarSource == null)
                return Fail(ourFbxPath, "vendorAvatarSource is null.");

            var vendorAssetPath = AssetDatabase.GetAssetPath(vendorAvatarSource);
            if (string.IsNullOrEmpty(vendorAssetPath))
                return Fail(ourFbxPath, "vendorAvatarSource has no asset path — is it a scene object? Pass the FBX model root, not a scene instance.");

            var vendorImp = AssetImporter.GetAtPath(vendorAssetPath) as ModelImporter;
            if (vendorImp == null)
                return Fail(ourFbxPath, "No ModelImporter at vendor path: " + vendorAssetPath
                    + " — vendorAvatarSource must be the root of a model (FBX) asset, not a prefab or scene object.");

            // ── 2. Apply standard import settings on our importer ─────────────────────────────

            ourImp.isReadable = true;

            ourImp.importNormals = ModelImporterNormals.Import;
            ourImp.importBlendShapeNormals = ModelImporterNormals.None;

            // An internal ModelImporter property with no public API, so it is set via SerializedObject.
            {
                var so = new SerializedObject(ourImp);
                var prop = so.FindProperty("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes");
                if (prop != null)
                {
                    prop.boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                else
                {
                    var sb = new StringBuilder("[MatchHumanoidRig] WARN: serialized property "
                        + "'legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes' not found. "
                        + "Available top-level properties: ");
                    var iter = so.GetIterator();
                    bool enter = true;
                    int count = 0;
                    while (iter.Next(enter) && count < 60)
                    {
                        sb.Append(iter.propertyPath).Append(", ");
                        enter = false;
                        count++;
                    }
                    Debug.LogWarning(sb.ToString());
                }
            }

            ourImp.animationType = ModelImporterAnimationType.Human;
            ourImp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            // ── 3. Guard: vendor must be configured Humanoid ──────────────────────────────────
            var vendorHd = vendorImp.humanDescription;
            var vendorHuman = vendorHd.human ?? new HumanBone[0];
            if (vendorHuman.Length == 0)
                return Fail(ourFbxPath, "vendor avatar is not configured Humanoid "
                    + "(humanDescription.human is empty) — nothing to conform.");

            // ── 4. Read our model's transforms (name → transforms, to detect ambiguity) ───────
            var ourModel = AssetDatabase.LoadAssetAtPath<GameObject>(ourFbxPath);
            if (ourModel == null)
                return Fail(ourFbxPath, "Could not load model at " + ourFbxPath + " to read bones.");

            var nameToTransforms = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
            foreach (var t in ourModel.GetComponentsInChildren<Transform>(true))
            {
                if (!nameToTransforms.TryGetValue(t.name, out var lst))
                {
                    lst = new List<Transform>();
                    nameToTransforms[t.name] = lst;
                }
                lst.Add(t);
            }

            // ── 5. Build skeleton[] from OUR model (bind pose; reproportion-safe) ──────────────
            // The ambiguity guard in step 6 covers only humanoid target bones, so duplicate non-humanoid names
            // can still enter skeleton[]; Unity rejects a malformed skeleton and the avatarOk gate catches it.
            var skeleton = new List<SkeletonBone>();
            foreach (var t in ourModel.GetComponentsInChildren<Transform>(true))
            {
                skeleton.Add(new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                });
            }

            // ── 6. Conform human[] from the vendor mapping ────────────────────────────────────
            var conformed = new List<HumanBone>(vendorHuman.Length);
            var skipped = new List<string>();
            foreach (var vb in vendorHuman)
            {
                if (!nameToTransforms.TryGetValue(vb.boneName, out var targets))
                {
                    if (RequiredHumanBones.Contains(vb.humanName))
                        return Fail(ourFbxPath, "required humanoid bone '" + vb.humanName + "' → '"
                            + vb.boneName + "' is absent from our model — re-add that transform and re-run.");
                    skipped.Add(vb.humanName + "→" + vb.boneName);
                    continue;
                }
                if (targets.Count > 1)
                    return Fail(ourFbxPath, "humanoid bone '" + vb.humanName + "' target name '" + vb.boneName
                        + "' is ambiguous — " + targets.Count + " transforms share that name; "
                        + "cannot bind unambiguously.");

                var hb = vb;
                if (!hb.limit.useDefaultValues)
                {
                    var lim = hb.limit;
                    lim.axisLength = DeriveAxisLength(targets[0]);
                    hb.limit = lim;
                }
                conformed.Add(hb);
            }

            // ── 7. Assemble the FULL humanDescription: mapping + our bind + vendor muscles ─────
            var ourHd = ourImp.humanDescription;
            ourHd.human = conformed.ToArray();
            ourHd.skeleton = skeleton.ToArray();
            ourHd.upperArmTwist = vendorHd.upperArmTwist;
            ourHd.lowerArmTwist = vendorHd.lowerArmTwist;
            ourHd.upperLegTwist = vendorHd.upperLegTwist;
            ourHd.lowerLegTwist = vendorHd.lowerLegTwist;
            ourHd.armStretch = vendorHd.armStretch;
            ourHd.legStretch = vendorHd.legStretch;
            ourHd.feetSpacing = vendorHd.feetSpacing;
            ourHd.hasTranslationDoF = vendorHd.hasTranslationDoF;
            ourImp.humanDescription = ourHd;

            // ── 8. Reimport with the complete humanoid configuration ──────────────────────────
            ourImp.SaveAndReimport();

            // ── 9. Diff + report ──────────────────────────────────────────────────────────────

            // Reload the importer after reimport to get the settled humanDescription.
            ourImp = AssetImporter.GetAtPath(ourFbxPath) as ModelImporter;
            if (ourImp == null)
                return Fail(ourFbxPath, "Could not reload ModelImporter after reimport.");

            var ourFinalHuman = ourImp.humanDescription.human;

            var ourMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var hb in ourFinalHuman)
                ourMap[hb.humanName] = hb.boneName;

            int totalVendorBones = vendorHuman.Length;
            var mismatches = new List<BoneDiff>();
            foreach (var vb in vendorHuman)
            {
                if (!ourMap.TryGetValue(vb.humanName, out var ourBoneName))
                {
                    mismatches.Add(new BoneDiff
                    {
                        HumanName = vb.humanName,
                        VendorBone = vb.boneName,
                        OurBone = "(missing)",
                        Kind = "missing"
                    });
                }
                else if (!string.Equals(ourBoneName, vb.boneName, StringComparison.Ordinal))
                {
                    mismatches.Add(new BoneDiff
                    {
                        HumanName = vb.humanName,
                        VendorBone = vb.boneName,
                        OurBone = ourBoneName,
                        Kind = "mismapped"
                    });
                }
            }

            int bonesMatched = totalVendorBones - mismatches.Count;
            int boneDiff = mismatches.Count;

            var humanoidBoneNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hb in conformed) humanoidBoneNames.Add(hb.boneName);

            // Reload our model fresh — the earlier ourModel reference predates SaveAndReimport and is stale.
            var ourModelFinal = AssetDatabase.LoadAssetAtPath<GameObject>(ourFbxPath);
            var ourBoneXf = BuildTransformMap(ourModelFinal);
            var vendorModel = AssetDatabase.LoadAssetAtPath<GameObject>(vendorAssetPath);
            var vendorBoneXf = BuildTransformMap(vendorModel);

            float poseDriftMax = 0f;
            foreach (var name in humanoidBoneNames)
            {
                if (!ourBoneXf.TryGetValue(name, out var ourT) || !vendorBoneXf.TryGetValue(name, out var vT))
                    continue;
                float d = (ourT.position - vT.position).magnitude;
                if (d > poseDriftMax) poseDriftMax = d;
            }
            int poseDriftMm = Mathf.RoundToInt(poseDriftMax * 1000f);

            // Necessary, not sufficient: isValid asserts structural mappability, not a sane bind. The real
            // bind proof is the operator drive.
            UnityEngine.Avatar builtAvatar = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ourFbxPath))
                if (o is UnityEngine.Avatar) { builtAvatar = (UnityEngine.Avatar)o; break; }
            bool avatarOk = builtAvatar != null && builtAvatar.isHuman && builtAvatar.isValid;

            string failReason = null;
            if (boneDiff != 0)
                failReason = "(" + mismatches[0].HumanName + " " + mismatches[0].Kind + ")";
            else if (!avatarOk)
                failReason = builtAvatar == null ? "(no Avatar built)" : "(Avatar !isHuman/!isValid)";

            bool pass = failReason == null;
            string label = TransplantCore.Leaf(ourFbxPath);
            string logPath = WriteRunLog(label, ourFbxPath, vendorAssetPath, totalVendorBones, bonesMatched,
                boneDiff, poseDriftMm, skipped, mismatches, pass ? "PASS" : "FAIL", failReason);

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[MatchHumanoidRig] {0}: bones={1}/{2} diff={3} poseDriftMm={4}(info) => {5}{6}{7}",
                label, bonesMatched, totalVendorBones, boneDiff, poseDriftMm, pass ? "PASS" : "FAIL",
                pass ? "" : " " + failReason,
                logPath != null ? " | log=" + logPath : "");

            if (pass) Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        // ── Preflight ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reports go/no-go for <see cref="Run"/> without reimporting or setting importer fields, checking the
        /// guards Run enforces before SaveAndReimport: our ModelImporter resolves; the vendor resolves and is
        /// Humanoid; every required bone of the vendor mapping is present and no mapped bone is ambiguous in our
        /// model's transforms.
        /// </summary>
        public static string Preflight(string ourFbxPath, GameObject vendorAvatarSource)
        {
            string label = string.IsNullOrEmpty(ourFbxPath) ? "(null)" : TransplantCore.Leaf(ourFbxPath);
            var blockers = new List<string>();
            string vendorPath = null;

            string WriteLog(string result, string failReason)
            {
                try
                {
                    Directory.CreateDirectory(TransplantCore.RunLogDir);
                    var sb = new StringBuilder();
                    sb.Append("{\n");
                    sb.Append("  \"kind\": \"match-humanoid-rig\",\n");
                    sb.Append("  \"preflight\": true,\n");
                    sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
                    sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
                    sb.Append("  \"ourFbx\": ").Append(TransplantCore.Q(ourFbxPath)).Append(",\n");
                    sb.Append("  \"vendorFbx\": ").Append(TransplantCore.Q(vendorPath)).Append(",\n");
                    sb.Append("  \"result\": ").Append(TransplantCore.Q(result)).Append(",\n");
                    sb.Append("  \"failReason\": ").Append(TransplantCore.Q(failReason ?? "")).Append(",\n");
                    sb.Append("  \"blockers\": [");
                    for (int i = 0; i < blockers.Count; i++)
                        sb.Append(i == 0 ? "" : ", ").Append(TransplantCore.Q(blockers[i]));
                    sb.Append("]\n");
                    sb.Append("}");

                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    var path = TransplantCore.RunLogDir + "/verify_" + TransplantCore.Sanitize(label) + "_" + stamp + ".json";
                    File.WriteAllText(path, sb.ToString());
                    AssetDatabase.Refresh();
                    return path;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[MatchHumanoidRig] Could not write preflight RunLog: " + ex.Message);
                    return null;
                }
            }

            // A no-go is this preflight working, so it logs a warning, not an error every later ReportConsole
            // read must discount; misuse produced no answer, so it stays an error. The token and the RunLog
            // `result` stay FAIL on either channel.
            string Report(bool go, string failReason, bool misuse = false)
            {
                string result = go ? "PASS" : "FAIL";
                string logPath = WriteLog(result, failReason);
                string summary = "[MatchHumanoidRig] (preflight) " + label + ": preconditions=" + (go ? "go" : "no-go")
                    + " => " + result
                    + (go ? "" : " " + failReason)
                    + (logPath != null ? " | log=" + logPath : "");
                if (go) Debug.Log(summary);
                else if (misuse) Debug.LogError(summary);
                else Debug.LogWarning(summary);
                return summary;
            }

            // ── Precondition checks ──
            if (string.IsNullOrEmpty(ourFbxPath))
                return Report(false, "ourFbxPath is null or empty.", misuse: true);

            var ourImp = AssetImporter.GetAtPath(ourFbxPath) as ModelImporter;
            if (ourImp == null)
                return Report(false, "No ModelImporter at ourFbxPath: " + ourFbxPath
                    + " — is the path correct and does the file exist in the AssetDatabase?", misuse: true);

            if (vendorAvatarSource == null)
                return Report(false, "vendorAvatarSource is null.", misuse: true);

            vendorPath = AssetDatabase.GetAssetPath(vendorAvatarSource);
            if (string.IsNullOrEmpty(vendorPath))
                return Report(false, "vendor has no asset path (pass the FBX model root, not a scene instance).", misuse: true);

            var vendorImp = AssetImporter.GetAtPath(vendorPath) as ModelImporter;
            if (vendorImp == null)
                return Report(false, "No ModelImporter at vendor path: " + vendorPath
                    + " — vendorAvatarSource must be the root of a model (FBX) asset.", misuse: true);

            // A non-Humanoid vendor is misuse: it cannot serve as the mapping source.
            var vendorHuman = vendorImp.humanDescription.human ?? new HumanBone[0];
            if (vendorHuman.Length == 0)
                return Report(false, "vendor avatar is not configured Humanoid (humanDescription.human is empty).", misuse: true);

            var ourModel = AssetDatabase.LoadAssetAtPath<GameObject>(ourFbxPath);
            if (ourModel == null)
                // Not misuse: a ModelImporter exists at this path, so the argument was right and the import
                // is broken.
                return Report(false, "Could not load model at " + ourFbxPath + " to read bones.");

            var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in ourModel.GetComponentsInChildren<Transform>(true))
                nameCount[t.name] = nameCount.TryGetValue(t.name, out var n) ? n + 1 : 1;

            foreach (var vb in vendorHuman)
            {
                if (!nameCount.TryGetValue(vb.boneName, out var count))
                {
                    if (RequiredHumanBones.Contains(vb.humanName))
                        blockers.Add("missing required bone '" + vb.humanName + "' → '" + vb.boneName + "'");
                }
                else if (count > 1)
                {
                    blockers.Add("ambiguous bone '" + vb.humanName + "' → '" + vb.boneName + "' (" + count + " transforms share that name)");
                }
            }

            bool go = blockers.Count == 0;
            string reason = go ? null : "(" + blockers[0] + "; " + blockers.Count + " blocker(s))";
            return Report(go, reason);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private static string Fail(string ourFbxPath, string reason)
        {
            string label = string.IsNullOrEmpty(ourFbxPath) ? "(null)" : TransplantCore.Leaf(ourFbxPath);

            string logPath = null;
            try
            {
                Directory.CreateDirectory(TransplantCore.RunLogDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var path = TransplantCore.RunLogDir + "/verify_" + TransplantCore.Sanitize(label) + "_" + stamp + ".json";
                var content = "{\n"
                    + "  \"kind\": \"match-humanoid-rig\",\n"
                    + "  \"unityVersion\": " + TransplantCore.Q(Application.unityVersion) + ",\n"
                    + "  \"timestampUtc\": " + TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)) + ",\n"
                    + "  \"ourFbx\": " + TransplantCore.Q(ourFbxPath) + ",\n"
                    + "  \"result\": \"FAIL\",\n"
                    + "  \"failReason\": " + TransplantCore.Q(reason) + "\n"
                    + "}";
                File.WriteAllText(path, content);
                AssetDatabase.Refresh();
                logPath = path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MatchHumanoidRig] Could not write failure RunLog: " + ex.Message);
            }

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[MatchHumanoidRig] {0}: FAIL — {1}{2}", label, reason,
                logPath != null ? " | log=" + logPath : "");
            Debug.LogError(summary);

            return summary;
        }

        private static string WriteRunLog(
            string label, string ourFbxPath, string vendorFbxPath,
            int totalVendorBones, int bonesMatched, int boneDiff, int poseDriftMm,
            List<string> skipped, List<BoneDiff> mismatches, string result, string failReason)
        {
            try
            {
                Directory.CreateDirectory(TransplantCore.RunLogDir);
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"kind\": \"match-humanoid-rig\",\n");
                sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
                sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
                sb.Append("  \"ourFbx\": ").Append(TransplantCore.Q(ourFbxPath)).Append(",\n");
                sb.Append("  \"vendorFbx\": ").Append(TransplantCore.Q(vendorFbxPath)).Append(",\n");
                sb.Append("  \"bonesMatched\": ").Append(bonesMatched).Append(",\n");
                sb.Append("  \"bonesTotal\": ").Append(totalVendorBones).Append(",\n");
                sb.Append("  \"boneDiff\": ").Append(boneDiff).Append(",\n");
                sb.Append("  \"poseDriftMm\": ").Append(poseDriftMm).Append(",\n");
                sb.Append("  \"poseDriftNote\": \"informational — max world drift (mm) of humanoid bones vs vendor; expected nonzero after reproportion; never gates\",\n");
                sb.Append("  \"failReason\": ").Append(TransplantCore.Q(failReason ?? "")).Append(",\n");
                sb.Append("  \"result\": ").Append(TransplantCore.Q(result)).Append(",\n");

                sb.Append("  \"skippedVendorBones\": [");
                for (int i = 0; i < skipped.Count; i++)
                    sb.Append(i == 0 ? "" : ", ").Append(TransplantCore.Q(skipped[i]));
                sb.Append("],\n");

                sb.Append("  \"boneMismatches\": [");
                for (int i = 0; i < mismatches.Count; i++)
                {
                    var m = mismatches[i];
                    sb.Append(i == 0 ? "\n" : ",\n");
                    sb.Append("    { \"humanName\": ").Append(TransplantCore.Q(m.HumanName))
                      .Append(", \"vendorBone\": ").Append(TransplantCore.Q(m.VendorBone))
                      .Append(", \"ourBone\": ").Append(TransplantCore.Q(m.OurBone))
                      .Append(", \"kind\": ").Append(TransplantCore.Q(m.Kind)).Append(" }");
                }
                sb.Append(mismatches.Count > 0 ? "\n  ]\n}" : "]\n}");

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var path = TransplantCore.RunLogDir + "/verify_" + TransplantCore.Sanitize(label) + "_" + stamp + ".json";
                File.WriteAllText(path, sb.ToString());
                AssetDatabase.Refresh();
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[MatchHumanoidRig] Could not write RunLog: " + ex.Message);
                return null;
            }
        }

        // Unity's 15 REQUIRED humanoid bones (absence makes the avatar non-humanoid). Chest/Neck/shoulders/
        // fingers/toes/eyes/jaw are OPTIONAL — Chest-drop is caught by boneDiff, not here.
        private static readonly HashSet<string> RequiredHumanBones = new HashSet<string>(StringComparer.Ordinal)
        {
            "Hips","Spine","Head",
            "LeftUpperLeg","RightUpperLeg","LeftLowerLeg","RightLowerLeg","LeftFoot","RightFoot",
            "LeftUpperArm","RightUpperArm","LeftLowerArm","RightLowerArm","LeftHand","RightHand",
        };

        // axisLength must track OUR geometry, not the vendor's (Unity honors the authored value verbatim).
        // Best-effort: the farthest child transform's local distance, 0 for leaves; the angular min/max are
        // the load-bearing fields.
        private static float DeriveAxisLength(Transform bone)
        {
            float max = 0f;
            for (int i = 0; i < bone.childCount; i++)
            {
                float d = bone.GetChild(i).localPosition.magnitude;
                if (d > max) max = d;
            }
            return max;
        }

        // Last-wins on duplicate names, which the poseDrift diagnostic tolerates.
        private static Dictionary<string, Transform> BuildTransformMap(GameObject model)
        {
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            if (model == null) return map;
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                map[t.name] = t;
            return map;
        }

        // ── Types ───────────────────────────────────────────────────────────────────────────────

        private struct BoneDiff
        {
            public string HumanName;
            public string VendorBone;
            public string OurBone;
            public string Kind; // "missing" | "mismapped"
        }
    }
}
