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

        // ── Avatar-scoped door: proxy / name-shadow divergence ───────────────────────────────────────

        /// <summary>Label prefix for every summary/RunLog <see cref="InspectAvatar"/> emits — kept
        /// distinct from <see cref="Run"/>'s "[CheckHumanoidRig]" so an avatar-scoped log can never be
        /// misread as a bind-drift one (the CheckSeam / "CheckSeam:bare" precedent — docs/unity-tools.md).</summary>
        private const string AvatarLabelPrefix = "[CheckHumanoidRig:avatar]";

        /// <summary>Scope note carried on every avatar-door run, PASS included: a zero count reads the
        /// mapping as the built <c>Avatar</c> reports it (plus the source <c>ModelImporter</c> when the
        /// avatar asset is importer-backed) over the SkinnedMeshRenderers present in the scene right now,
        /// pre-build — not as "no divergence exists anywhere". A generated/standalone Avatar with no
        /// ModelImporter is not a failure — the importer half is simply unavailable and the live mapping
        /// stands alone.</summary>
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
        /// Composed-avatar divergence guard: a humanoid rig's ONLY source of truth is the source model's
        /// <c>ModelImporter.humanDescription</c> — never a prefab, scene, controller, or clip. A rig can
        /// legitimately map humanoid <c>Head</c> to a transform like <c>Head_Proxy</c>, a SIBLING of the
        /// skinned <c>Head</c> bone; every other artifact that looks a bone up BY NAME then silently reads
        /// the wrong transform. <see cref="Run"/> cannot see this — it only asserts bind-vs-geometry drift
        /// on one FBX. This door starts from a PLACED avatar and reports the divergence: a humanoid bone
        /// mapped to a transform no renderer weights (<b>proxy</b>), and a plain name lookup landing on the
        /// wrong transform because some OTHER node carries the humanoid label (<b>name-shadow</b>). A bone
        /// can hit both classes; each is emitted once per class it hits.
        ///
        /// Verdict mirrors <c>Ryan6Vrc.AgentTools.Editor.CheckAvatar.Inspect</c> exactly: PASS (no
        /// divergence found), CLASSIFY (a finding for the agent to route — never a tool failure), FAIL
        /// (bad input: unresolved handle, no descriptor, no Animator, no humanoid avatar — bare, no RunLog
        /// trailer, same family discipline as a CheckAvatar bad-input refusal).
        ///
        /// Read-only: mutates no scene object, no asset, dirties nothing. <paramref name="avatarRoot"/> is
        /// a scene handle resolved the way <c>CheckAvatar.Resolve</c> resolves its own (hierarchy path,
        /// then instance id, then name in the active scene) — mirrored locally rather than shared, since
        /// CheckAvatar keeps that resolver private.
        /// </summary>
        public static string InspectAvatar(string avatarRoot)
        {
            var avatarGO = ResolveScene(avatarRoot);
            if (avatarGO == null)
                return FailAvatar("avatar root '" + avatarRoot + "' not found — tried hierarchy path, instance id, then name in the active scene");

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

            // ---- source model + importer map (context only; the live mapping below is authoritative) ----
            string modelPath = AssetDatabase.GetAssetPath(animator.avatar);
            string modelLabel = string.IsNullOrEmpty(modelPath) ? "(none)" : Leaf(modelPath);
            var importerMap = new Dictionary<string, string>(StringComparer.Ordinal); // humanName -> boneName
            if (!string.IsNullOrEmpty(modelPath))
            {
                var imp = AssetImporter.GetAtPath(modelPath) as ModelImporter;
                if (imp != null) // not importer-backed (generated/standalone Avatar) ⇒ NOT a failure, just no map
                {
                    var human = imp.humanDescription.human ?? new HumanBone[0];
                    foreach (var hb in human) importerMap[hb.humanName] = hb.boneName;
                }
            }

            // ---- live mapping: every HumanBodyBones the built Avatar actually maps ----------------------
            var liveMap = new Dictionary<HumanBodyBones, Transform>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                var t = animator.GetBoneTransform(bone);
                if (t != null) liveMap[bone] = t; // null ⇒ unmapped optional bone, not an error
            }

            // ---- skinned-weight set: renderer.bones actually weighted at NONZERO, plus every rootBone ----
            // Collected by instance AND by name, because pre-bake the instance set is the wrong index: see
            // the classify block below for why an instance-only test reports a whole skeleton as proxied.
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

            // ---- name index over the whole avatar (for name-shadow) --------------------------------------
            var byName = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
            foreach (var t in avatarGO.GetComponentsInChildren<Transform>(true))
            {
                if (!byName.TryGetValue(t.name, out var list)) byName[t.name] = list = new List<Transform>();
                list.Add(t);
            }

            // ---- classify: proxy (unskinned mapping) + name-shadow (a decoy carries the plain label) -----
            var rows = new List<DivergenceRow>();
            var ambiguous = new List<string>();
            foreach (var kv in liveMap)
            {
                var bone = kv.Key;
                var mapped = kv.Value;
                string label = bone.ToString();

                // Skinned by INSTANCE or by NAME. The name arm is not laxity — pre-bake it is the only arm
                // that can be right. On a composed avatar the renderers skin a MERGEABLE's armature copy
                // (`<root>/<Module>/Armature/Hips/Spine`), which MergeArmature zips onto the base's
                // (`<root>/Armature/Hips/Spine`) at build, so every humanoid bone fails an instance test
                // and an instance-only read reports the entire skeleton as proxied. Measured on an ordinary
                // composed avatar: 4 bones matched by instance, 49 by name only, 0 by neither. The same
                // pre-bake fact is recorded for ReportGimmick's `chain subtree` cell in docs/unity-tools.md.
                // Name matching is a HEURISTIC, and the emitted scope note says so: it is the merge's own
                // matching rule, right exactly where the merge is, and unable to tell a bone that will zip
                // from an unrelated transform that happens to share a name.
                bool skinnedByInstance = skinned.Contains(mapped);
                bool skinnedByNameOnly = !skinnedByInstance && skinnedNames.Contains(mapped.name);
                if (!skinnedByInstance && !skinnedByNameOnly)
                {
                    string candidate = FindProxyCandidate(mapped, skinned, skinnedNames, label); // "candidate", never asserted as THE bone
                    rows.Add(new DivergenceRow { Kind = "proxy", BoneLabel = label, MappedPath = PathOf(mapped), OtherPath = candidate });
                }
                else if (skinnedByNameOnly)
                {
                    // The name arm carries a cost in the other direction and it is not allowed to be silent.
                    // `skinnedNames` spans the whole avatar, and MergeArmature duplicates the base armature —
                    // proxy nodes included — into every mergeable, so an outfit whose renderers skin a copy of
                    // `Head_Proxy` masks the base's genuine proxy row. That row would then vanish into a PASS.
                    // Counted and named instead: a masked row is ambiguous, not clean.
                    ambiguous.Add(label + " (" + PathOf(mapped) + "): skinned only by NAME, not by this instance");
                }

                // A shadow exists only where the mapping points somewhere OTHER than the plainly-named bone.
                // Where the humanoid bone IS the transform named for it, a second same-named transform is the
                // ordinary base/mergeable pair the build is about to zip — flagging that manufactures an
                // offender on every composed avatar.
                if (!string.Equals(mapped.name, label, StringComparison.Ordinal)
                    && byName.TryGetValue(label, out var named))
                    foreach (var other in named)
                    {
                        if (other == mapped) continue;
                        rows.Add(new DivergenceRow { Kind = "name-shadow", BoneLabel = label, MappedPath = PathOf(mapped), OtherPath = PathOf(other) });
                        break; // one decoy names the finding; a second same-named decoy is the same finding
                    }
            }

            return FinishAvatar(avatarGO, liveMap.Count, modelLabel, importerMap, rows, ambiguous);
        }

        // Nearest skinned candidate for a proxy row: a same-parent sibling, or a descendant of the mapped
        // transform, named either the humanoid label or the mapped name minus a "_Proxy"-style suffix.
        // Never asserted as the intended bone — the offender says "candidate".
        // "Skinned" here must mean what it means at the classify site one screen up — instance OR name.
        // Using instance alone made this return null on almost every real avatar (measured: 4 bones skinned
        // by instance against 49 by name), so every proxy row said "(no candidate found)" while the candidate
        // was sitting right beside it.
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

        /// <summary>Shared avatar-door tail: JSON body → <see cref="RunLogFormat.WriteRunLog"/>, filename
        /// stem <c>check-humanoid-rig-avatar_&lt;rootName&gt;</c>, JSON <c>kind</c> distinct from
        /// <see cref="Run"/>'s so the two artifact families can never be confused on disk.</summary>
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
            // A masked row is a SUPPRESSED finding, so it rides the summary as its own count rather than
            // living only here: a PASS beside a nonzero nameMasked= is not the same claim as a clean one.
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

        // ── Scene resolver — mirrors CheckAvatar.Resolve (path → instance id → name); kept local since
        // CheckAvatar's is private to its own class. ───────────────────────────────────────────────────

        private static GameObject ResolveScene(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            var byPath = FindByHierarchyPath(target);
            if (byPath != null) return byPath;

            if (int.TryParse(target.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                var obj = EditorUtility.InstanceIDToObject(id);
                if (obj is GameObject go) return go;
                if (obj is Component comp) return comp.gameObject;
            }

            foreach (var rootGo in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                var hit = FindByNameRecursive(rootGo.transform, target);
                if (hit != null) return hit.gameObject;
            }
            return null;
        }

        private static GameObject FindByHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segs = path.Trim('/').Split('/');
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != segs[0]) continue;
                Transform t = root.transform;
                bool ok = true;
                for (int i = 1; i < segs.Length && ok; i++)
                {
                    t = t.Find(segs[i]);
                    if (t == null) ok = false;
                }
                if (ok) return t.gameObject;
            }
            return null;
        }

        private static Transform FindByNameRecursive(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform child in t)
            {
                var hit = FindByNameRecursive(child, name);
                if (hit != null) return hit;
            }
            return null;
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
