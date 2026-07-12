using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// On-demand check that MatchHumanoidRig preserves vendor muscle settings. Some vendor avatars customize
    /// the twist/stretch distribution or per-bone muscle limits, and those changes visibly alter humanoid
    /// animation — so this synthesizes a non-default "vendor"
    /// (duplicates a real FBX, injects non-default global muscle fields + a non-default Hips limit), rigs a
    /// SECOND copy against it, and asserts the muscle settings + Chest mapping survived on our model.
    /// PASS/FAIL + RunLog like the tools.
    ///
    /// Scope: this proves the MUSCLE-copy and mapping-survival behavior. It does NOT prove the
    /// skeleton[]-from-our-model reproportion-safety — hips limits and the global muscle fields round-trip
    /// regardless of skeleton[], so this test would pass even on skeleton-less code. The skeleton[] /
    /// reproportion behavior is verified by CheckHumanoidRig (the live re-instance skeleton-freshness verification).
    /// </summary>
    public static class MatchHumanoidRigVerify
    {
        private const string ScratchRoot = "Assets/Agent/Scratch";
        private const string ScratchDir = "Assets/Agent/Scratch/RigVerify";
        private const float Eps = 1e-3f;

        public static string Run(string sourceFbxPath)
        {
            string vendorPath = ScratchDir + "/SynthVendor.fbx";
            string ourPath = ScratchDir + "/SynthOurs.fbx";
            try
            {
                if (string.IsNullOrEmpty(sourceFbxPath) ||
                    AssetImporter.GetAtPath(sourceFbxPath) as ModelImporter == null)
                    return Done("(none)", "FAIL", "source FBX not found: " + sourceFbxPath);

                if (!AssetDatabase.IsValidFolder("Assets/Agent"))
                    AssetDatabase.CreateFolder("Assets", "Agent");
                if (!AssetDatabase.IsValidFolder(ScratchRoot))
                    AssetDatabase.CreateFolder("Assets/Agent", "Scratch");
                if (!AssetDatabase.IsValidFolder(ScratchDir))
                    AssetDatabase.CreateFolder(ScratchRoot, "RigVerify");
                CopyFresh(sourceFbxPath, vendorPath);
                CopyFresh(sourceFbxPath, ourPath);

                // ── Build the synthetic vendor: Human + model skeleton + non-default muscles/limit ──
                var vimp = (ModelImporter)AssetImporter.GetAtPath(vendorPath);
                vimp.animationType = ModelImporterAnimationType.Human;
                vimp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                vimp.SaveAndReimport(); // auto-map first, so human[] is populated to edit
                vimp = (ModelImporter)AssetImporter.GetAtPath(vendorPath);

                var vGo = AssetDatabase.LoadAssetAtPath<GameObject>(vendorPath);
                var vhd = vimp.humanDescription;
                var human = new List<HumanBone>(vhd.human ?? new HumanBone[0]);
                // ensure Chest is present in the synthetic vendor mapping (auto-map drops it on this rig);
                // chestExpected is false when the source rig has no transform we can map as Chest.
                bool chestExpected = EnsureChest(human, vGo);
                // inject a non-default Hips limit
                for (int i = 0; i < human.Count; i++)
                {
                    if (human[i].humanName == "Hips")
                    {
                        var hb = human[i]; var l = hb.limit;
                        l.useDefaultValues = false;
                        l.min = new Vector3(-13, -13, -13);
                        l.max = new Vector3(13, 13, 13);
                        l.center = new Vector3(1, 1, 1);
                        hb.limit = l; human[i] = hb;
                    }
                }
                vhd.human = human.ToArray();
                vhd.skeleton = BuildSkeleton(vGo);
                vhd.armStretch = 0.137f; vhd.legStretch = 0.091f;
                vhd.upperArmTwist = 0.61f; vhd.lowerArmTwist = 0.42f;
                vhd.upperLegTwist = 0.55f; vhd.lowerLegTwist = 0.48f;
                vhd.feetSpacing = 0.23f; vhd.hasTranslationDoF = true;
                vimp.humanDescription = vhd;
                vimp.SaveAndReimport();
                vimp = (ModelImporter)AssetImporter.GetAtPath(vendorPath);

                // Re-resolve vGo: the reimport above can destroy the GameObject loaded before it (the same
                // staleness MatchHumanoidRig guards against). A destroyed reference satisfies == null, which
                // would silently trip Run's null-vendor guard. Re-load and fail loudly if it's gone.
                vGo = AssetDatabase.LoadAssetAtPath<GameObject>(vendorPath);
                if (vGo == null)
                    return Done(Leaf(sourceFbxPath), "FAIL", "synthetic vendor missing after reimport: " + vendorPath);

                // ── Rig our copy against the synthetic vendor ──
                var summary = MatchHumanoidRig.Run(ourPath, vGo);

                // ── Assert the muscle settings + Chest survived on OUR model ──
                var oimp = (ModelImporter)AssetImporter.GetAtPath(ourPath);
                var ohd = oimp.humanDescription;
                var fails = new List<string>();
                if (Math.Abs(ohd.armStretch - 0.137f) > Eps) fails.Add("armStretch=" + ohd.armStretch);
                if (Math.Abs(ohd.legStretch - 0.091f) > Eps) fails.Add("legStretch=" + ohd.legStretch);
                if (Math.Abs(ohd.upperArmTwist - 0.61f) > Eps) fails.Add("upperArmTwist=" + ohd.upperArmTwist);
                if (Math.Abs(ohd.lowerArmTwist - 0.42f) > Eps) fails.Add("lowerArmTwist=" + ohd.lowerArmTwist);
                if (Math.Abs(ohd.upperLegTwist - 0.55f) > Eps) fails.Add("upperLegTwist=" + ohd.upperLegTwist);
                if (Math.Abs(ohd.lowerLegTwist - 0.48f) > Eps) fails.Add("lowerLegTwist=" + ohd.lowerLegTwist);
                if (Math.Abs(ohd.feetSpacing - 0.23f) > Eps) fails.Add("feetSpacing=" + ohd.feetSpacing);
                if (!ohd.hasTranslationDoF) fails.Add("hasTranslationDoF=false");

                bool hipsOk = false, chestOk = false;
                foreach (var hb in ohd.human)
                {
                    if (hb.humanName == "Chest") chestOk = true;
                    if (hb.humanName == "Hips")
                        hipsOk = !hb.limit.useDefaultValues
                                 && (hb.limit.min - new Vector3(-13, -13, -13)).magnitude < Eps
                                 && (hb.limit.max - new Vector3(13, 13, 13)).magnitude < Eps
                                 && (hb.limit.center - new Vector3(1, 1, 1)).magnitude < Eps;
                }
                if (chestExpected && !chestOk) fails.Add("Chest dropped");
                if (!hipsOk) fails.Add("Hips limit not preserved");

                UnityEngine.Avatar av = null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(ourPath))
                    if (o is UnityEngine.Avatar) { av = (UnityEngine.Avatar)o; break; }
                if (av == null || !av.isHuman || !av.isValid) fails.Add("avatar invalid");

                // Gate on the inner rig's own verdict too: muscle fields can round-trip even when the inner
                // rig FAILs on a bone-diff or validity issue, so don't let a buried inner FAIL report PASS.
                if (summary.IndexOf("=> PASS", StringComparison.Ordinal) < 0)
                    fails.Add("inner rig not PASS");

                string result = fails.Count == 0 ? "PASS" : "FAIL";
                return Done(Leaf(sourceFbxPath), result,
                    fails.Count == 0 ? "muscles+limit+Chest preserved | innerRig: " + summary
                                     : string.Join(", ", fails) + " | innerRig: " + summary);
            }
            catch (Exception ex)
            {
                return Done(Leaf(sourceFbxPath), "FAIL", "exception: " + ex.Message);
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(ScratchDir)) AssetDatabase.DeleteAsset(ScratchDir);
            }
        }

        // ── helpers ──
        private static void CopyFresh(string src, string dst)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(dst) != null) AssetDatabase.DeleteAsset(dst);
            AssetDatabase.CopyAsset(src, dst);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
        }

        private static SkeletonBone[] BuildSkeleton(GameObject go)
        {
            var list = new List<SkeletonBone>();
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                list.Add(new SkeletonBone { name = t.name, position = t.localPosition,
                    rotation = t.localRotation, scale = t.localScale });
            return list.ToArray();
        }

        // Ensure the synthetic vendor carries a Chest the inner rig must preserve. Returns true if Chest is
        // already mapped or we could synthesize it; false if the source rig has no transform literally named
        // "Chest" (a differently-named chest bone, e.g. UpperChest/Spine1) — the caller then skips the Chest
        // assertion rather than failing for a reason unrelated to the code under test.
        private static bool EnsureChest(List<HumanBone> human, GameObject go)
        {
            foreach (var hb in human) if (hb.humanName == "Chest") return true;
            Transform chest = null;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                if (t.name == "Chest") { chest = t; break; }
            if (chest == null) return false;
            var nb = new HumanBone { humanName = "Chest", boneName = "Chest" };
            var l = nb.limit; l.useDefaultValues = true; nb.limit = l;
            human.Add(nb);
            return true;
        }

        // Canonical leaf mechanics; only the "(none)" display sentinel for a missing path is local.
        private static string Leaf(string p)
        {
            var leaf = Ryan6Vrc.AgentTools.Editor.RunLogFormat.Leaf(p);
            return leaf.Length == 0 ? "(none)" : leaf;
        }

        private static string Done(string label, string result, string detail)
        {
            string summary = "[MatchHumanoidRigVerify] " + label + ": " + detail + " => " + result;
            if (result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            try
            {
                Directory.CreateDirectory(TransplantCore.RunLogDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var path = TransplantCore.RunLogDir + "/verify_muscle_" + TransplantCore.Sanitize(label) + "_" + stamp + ".json";
                var sb = new StringBuilder();
                sb.Append("{\n  \"kind\": \"match-humanoid-rig-muscle-verify\",\n");
                sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
                sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
                sb.Append("  \"source\": ").Append(TransplantCore.Q(label)).Append(",\n");
                sb.Append("  \"detail\": ").Append(TransplantCore.Q(detail)).Append(",\n");
                sb.Append("  \"result\": ").Append(TransplantCore.Q(result)).Append("\n}");
                File.WriteAllText(path, sb.ToString());
                AssetDatabase.Refresh();
            }
            catch (Exception ex) { Debug.LogWarning("[MatchHumanoidRigVerify] RunLog write failed: " + ex.Message); }
            return summary;
        }
    }
}
