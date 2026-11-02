using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Import health check: reports the dangling material, mesh and script references and the FBX remap
    /// faults a vendor import leaves under a folder or in the selection. Inspection only.
    /// Contract: docs/unity-tools.md.
    ///
    /// Add no verdict on empty slots without a measured instance of a slot that should have drawn.
    /// </summary>
    [AgentTool]
    public static class CheckPackage
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;

        /// <summary>Verify every prefab and model under an asset folder. Returns the one-line summary ending
        /// <c>… => RESULT | log=&lt;path&gt;</c>, or on bad input a bare <c>[CheckPackage] FAIL: …</c>.</summary>
        public static string Run(string assetFolderPath)
        {
            if (string.IsNullOrEmpty(assetFolderPath) || !AssetDatabase.IsValidFolder(assetFolderPath))
                return "[CheckPackage] FAIL: not a valid asset folder: " + assetFolderPath;

            var r = new Report { Target = assetFolderPath, Mode = "folder" };
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { assetFolderPath }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) continue;
                ScanPrefabAsset(path, r);
            }
            // A model asset gets both scans, matching VerifySelection's FBX branch.
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { assetFolderPath }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model != null) { r.Scanned++; ScanHierarchy(model, path, r); }
                ScanModelRemap(path, r);
            }
            return Finish(r, Leaf(assetFolderPath));
        }

        /// <summary>Verify the selected prefab or model assets and scene GameObjects.</summary>
        public static string VerifySelection()
        {
            var objs = Selection.gameObjects;
            if (objs == null || objs.Length == 0)
            {
                Debug.LogWarning("[CheckPackage] Nothing selected.");
                return "[CheckPackage] FAIL: nothing selected.";
            }

            var r = new Report { Target = "selection", Mode = "selection" };
            foreach (var go in objs)
            {
                if (EditorUtility.IsPersistent(go))
                {
                    var path = AssetDatabase.GetAssetPath(go);
                    if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        ScanPrefabAsset(path, r);
                    else { r.Scanned++; ScanHierarchy(go, path, r); ScanModelRemap(path, r); } // model prefab / FBX asset
                }
                else { r.Scanned++; ScanHierarchy(go, "scene:" + go.scene.name, r); }
            }
            return Finish(r, "selection");
        }

        private static void ScanPrefabAsset(string assetPath, Report r)
        {
            r.Scanned++;
            GameObject root;
            try { root = PrefabUtility.LoadPrefabContents(assetPath); }
            catch (Exception e)
            {
                r.LoadErrors++;
                r.Offenders.Add(new Offender { Location = assetPath, ObjectPath = "", Kind = "load-error", Detail = e.Message });
                return;
            }
            try { ScanHierarchy(root, assetPath, r); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void ScanHierarchy(GameObject root, string location, Report r)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                string goPath = HierarchyPath(t);

                int missingScripts = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missingScripts > 0)
                {
                    r.ScriptsMissing += missingScripts;
                    r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "script-missing", Detail = missingScripts + " missing MonoBehaviour(s)" });
                }

                foreach (var rend in go.GetComponents<Renderer>())
                {
                    ScanMaterials(rend, location, goPath, r);
                    var smr = rend as SkinnedMeshRenderer;
                    if (smr != null) ScanMeshRef(new SerializedObject(smr), location, goPath, "SkinnedMeshRenderer", r);
                }
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null) ScanMeshRef(new SerializedObject(mf), location, goPath, "MeshFilter", r);
            }
        }

        private static void ScanMaterials(Renderer rend, string location, string goPath, Report r)
        {
            var arr = new SerializedObject(rend).FindProperty("m_Materials");
            if (arr == null || !arr.isArray) return;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var el = arr.GetArrayElementAtIndex(i);
                if (el.objectReferenceValue != null) r.MatResolved++;
                else if (el.objectReferenceInstanceIDValue != 0)
                {
                    r.MatMissing++;
                    r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "material-missing", Detail = "material slot " + i + " holds a dangling reference: " + DanglingTarget(el.objectReferenceInstanceIDValue, r) });
                }
                else r.MatEmpty++; // empty, not broken: counted, never gated
            }
        }

        private static void ScanMeshRef(SerializedObject so, string location, string goPath, string compName, Report r)
        {
            var p = so.FindProperty("m_Mesh");
            if (p == null || p.objectReferenceValue != null) return;
            if (p.objectReferenceInstanceIDValue == 0) return; // intentionally no mesh
            r.MeshesMissing++;
            r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "mesh-missing", Detail = compName + " mesh holds a dangling reference: " + DanglingTarget(p.objectReferenceInstanceIDValue, r) });
        }

        // Names what a broken reference still points at, which decides the remedy: a guid that resolves
        // to a real file means the reference aims at a sub-object that file does not contain (an FBX set
        // to external materials never creates the material sub-objects its prefab variants override, so
        // the fix is at the referencing end), while an absent guid means the asset was never imported.
        private static string DanglingTarget(int instanceId, Report r)
        {
            if (!r.TargetCache.TryGetValue(instanceId, out var t))
            {
                // Only the long-localId overload is usable; the int one throws unconditionally.
                bool mapped = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(instanceId, out string guid, out long fileId);
                t = new TargetInfo
                {
                    Mapped = mapped,
                    // Serialized identity, not the in-memory handle: counting distinct targets is only
                    // meaningful against what the file records.
                    Key = mapped ? guid + "/" + fileId : "instanceID:" + instanceId,
                    Detail = DescribeTarget(instanceId, mapped, guid, fileId,
                        mapped ? AssetDatabase.GUIDToAssetPath(guid) : null,
                        mapped && RunLogFormat.AssetGuidResolves(guid))
                };
                r.TargetCache[instanceId] = t;
            }
            r.DanglingTargets.Add(t.Key);
            if (!t.Mapped) r.UnidentifiedTargets.Add(t.Key);
            return t.Detail;
        }

        /// <summary>Pure wording for the four shapes the answer takes, so each is assertable without
        /// having to provoke a real dangling reference.
        ///
        /// <paramref name="assetExists"/> is why a path alone cannot word this: a deleted asset's GUID
        /// keeps resolving to its old path (<see cref="RunLogFormat.AssetGuidResolves"/>), so wording every
        /// non-empty path as "holds no object with fileID N" hunts a sub-asset in a file that is gone.</summary>
        internal static string DescribeTarget(int instanceId, bool mapped, string guid, long fileId, string assetPath,
                                              bool assetExists)
        {
            if (!mapped) return "instanceID " + instanceId + " has no guid mapping";
            if (string.IsNullOrEmpty(assetPath)) return "guid " + guid + " is absent from this project";
            return assetExists
                ? "guid " + guid + " resolves to " + assetPath + ", which holds no object with fileID " + fileId
                : "guid " + guid + " was " + assetPath + ", which no longer resolves (deleted, moved, or failed to import)";
        }

        // An FBX's external-material remap is applied only at import time, so a model imported before its
        // materials existed caches empty slots that no later import re-applies — it reads "intentionally
        // empty" yet renders untextured. The gate is the external-object map, not `materialLocation`: a
        // modern remap stays `InPrefab`.
        //
        // An all-bound model — every resolvable entry landed somewhere, yet slots are still empty — carries no
        // verdict: binding is by name at import, so those slots are other submeshes a reimport cannot clear.
        // A verdict here would need a per-slot check: `notBound` is a model-wide material-identity test, so on
        // a map aiming several keys at one material one bound occurrence marks them all bound.
        //
        // `materialImportMode == None` is excluded: materials are deliberately not imported there, so every
        // slot is empty by design.
        private static void ScanModelRemap(string assetPath, Report r)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null || importer.materialImportMode == ModelImporterMaterialImportMode.None) return;

            var mapped = new List<string>();
            var unresolved = new List<string>();
            // Indexer, not Add: a duplicate identifier name would throw, and a throw here would take down a
            // whole-folder scan over one malformed importer entry.
            var target = new Dictionary<string, Material>();
            foreach (var kv in importer.GetExternalObjectMap())
            {
                if (kv.Key.type != typeof(Material)) continue;
                mapped.Add(kv.Key.name);
                if (kv.Value == null) unresolved.Add(kv.Key.name);
                else target[kv.Key.name] = kv.Value as Material;
            }
            if (mapped.Count == 0) return;

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null) return;
            int empty = 0;
            // Attribution stops at the entry, never the slot: the importer's source-material list does not
            // index-align with renderer slots.
            var boundHere = new HashSet<Material>();
            foreach (var rend in model.GetComponentsInChildren<Renderer>(true))
            {
                // The ParticleSystemRenderer exclusion applies to the EMPTY count only. Its null slot
                // is routine and carries no signal, but a material bound THERE is still bound on this
                // model — excluding it from the set would report a remap entry as "landed nowhere" and
                // prescribe a reimport that cannot change anything.
                bool slotsAreEvidence = !(rend is ParticleSystemRenderer);
                foreach (var m in rend.sharedMaterials)
                {
                    if (m == null) { if (slotsAreEvidence) empty++; }
                    else boundHere.Add(m);
                }
            }
            if (empty == 0) return;

            var notBound = new List<string>();
            foreach (var name in mapped)
            {
                Material t;
                if (target.TryGetValue(name, out t) && t != null && !boundHere.Contains(t)) notBound.Add(name);
            }

            var verdict = ClassifyRemap(mapped.Count, unresolved.Count, empty, notBound.Count);
            if (verdict == RemapVerdict.None) return;

            if (verdict == RemapVerdict.Unresolved) r.RemapUnresolved += empty; else r.RemapStale += empty;
            r.Offenders.Add(new Offender
            {
                Location = assetPath,
                ObjectPath = "",
                Kind = verdict == RemapVerdict.Unresolved ? "fbx-remap-unresolved" : "fbx-remap-stale",
                Detail = DescribeRemap(verdict, mapped.Count, unresolved, empty, mapped, notBound)
            });
        }

        internal enum RemapVerdict { None, Stale, Unresolved }

        /// <summary>The remap decision, pure so every branch is asserted directly rather than by hunting a
        /// binary FBX whose importer happens to produce it.</summary>
        internal static RemapVerdict ClassifyRemap(int mappedCount, int unresolvedCount, int emptySlots,
                                                   int notBoundCount = 0)
        {
            if (mappedCount == 0 || emptySlots == 0) return RemapVerdict.None;
            if (unresolvedCount > 0) return RemapVerdict.Unresolved;
            return notBoundCount > 0 ? RemapVerdict.Stale : RemapVerdict.None;
        }

        /// <summary>Pure wording for the two remap classes, whose remedies run in opposite order.</summary>
        internal static string DescribeRemap(RemapVerdict verdict, int mappedCount, List<string> unresolved, int emptySlots,
                                            List<string> mapped = null, List<string> notBound = null)
        {
            if (verdict == RemapVerdict.Unresolved)
                return emptySlots + " empty renderer slot(s); " + unresolved.Count + " of " + mappedCount
                     + " external-material remap entries resolve to nothing (" + NameList(unresolved)
                     + ") — restore or import those material assets, THEN force-reimport the FBX. A reimport alone "
                     + "cannot fix this: the targets do not exist yet";

            // No partition supplied, which ScanModelRemap never does: say only what this caller established.
            if (notBound == null)
                return emptySlots + " empty renderer slot(s) despite " + mappedCount
                     + " resolvable external-material remap entries (" + NameList(mapped ?? new List<string>())
                     + ") — force-reimport the FBX. If a reimport does not clear it, the empty slots are "
                     + "not the mapped ones and this model is fine";

            return emptySlots + " empty renderer slot(s); " + notBound.Count + " of " + mappedCount
                 + " resolvable external-material remap entries are on no renderer of this model ("
                 + NameList(notBound) + ") — force-reimport the FBX. The other "
                 + (mappedCount - notBound.Count) + " bound fine and are not the problem. If a reimport "
                 + "does not clear it, those entries name no submesh this mesh carries and this model is fine";
        }

        /// <summary>Comma-joined names, capped so a many-material FBX names a readable few and counts the rest
        /// rather than emitting a wall the reader skips.</summary>
        internal static string NameList(List<string> names)
        {
            const int Cap = 6;
            if (names.Count <= Cap) return string.Join(", ", names.ToArray());
            return string.Join(", ", names.GetRange(0, Cap).ToArray()) + ", +" + (names.Count - Cap) + " more";
        }

        private static string Finish(Report r, string label)
        {
            bool pass = r.MatMissing == 0 && r.MeshesMissing == 0 && r.ScriptsMissing == 0
                     && r.RemapStale == 0 && r.RemapUnresolved == 0 && r.LoadErrors == 0;
            r.Result = pass ? "PASS" : "FAIL";
            string logPath = WriteRunLog(r, label);

            // A thousand slots at two targets is one vendor-side mistake, not a thousand, so both numbers
            // ride together.
            int danglingRefs = r.MatMissing + r.MeshesMissing;
            string dangling = danglingRefs == 0 ? "" :
                " | dangling: " + danglingRefs + " ref(s) at " + r.DanglingTargets.Count + " distinct target(s)"
                + (r.UnidentifiedTargets.Count > 0 ? ", " + r.UnidentifiedTargets.Count + " identified by instance id only (may over-count)" : "");

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckPackage] {0} ({1}, {2} scanned): materials resolved={3} empty={4} MISSING={5} | meshMISSING={6} | scriptMISSING={7} | remapSTALE={8} | remapUNRESOLVED={9}{10}{11} => {12} | log={13}",
                label, r.Mode, r.Scanned, r.MatResolved, r.MatEmpty, r.MatMissing, r.MeshesMissing, r.ScriptsMissing,
                r.RemapStale, r.RemapUnresolved, dangling,
                r.LoadErrors > 0 ? " | loadErrors=" + r.LoadErrors : "", r.Result, logPath);

            if (pass) Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        private static string WriteRunLog(Report r, string label)
        {
            Directory.CreateDirectory(RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"check-package\",\n");
            sb.Append("  \"unityVersion\": ").Append(Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"target\": ").Append(Q(r.Target)).Append(",\n");
            sb.Append("  \"mode\": ").Append(Q(r.Mode)).Append(",\n");
            sb.Append("  \"scanned\": ").Append(r.Scanned).Append(",\n");
            sb.Append("  \"materials\": { \"resolved\": ").Append(r.MatResolved)
              .Append(", \"empty\": ").Append(r.MatEmpty)
              .Append(", \"missing\": ").Append(r.MatMissing).Append(" },\n");
            sb.Append("  \"meshesMissing\": ").Append(r.MeshesMissing).Append(",\n");
            sb.Append("  \"danglingDistinctTargets\": ").Append(r.DanglingTargets.Count).Append(",\n");
            sb.Append("  \"danglingUnidentifiedTargets\": ").Append(r.UnidentifiedTargets.Count).Append(",\n");
            sb.Append("  \"scriptsMissing\": ").Append(r.ScriptsMissing).Append(",\n");
            sb.Append("  \"remapStale\": ").Append(r.RemapStale).Append(",\n");
            sb.Append("  \"remapUnresolved\": ").Append(r.RemapUnresolved).Append(",\n");
            sb.Append("  \"loadErrors\": ").Append(r.LoadErrors).Append(",\n");
            sb.Append("  \"result\": ").Append(Q(r.Result)).Append(",\n");
            sb.Append("  \"offenders\": [");
            for (int i = 0; i < r.Offenders.Count; i++)
            {
                var o = r.Offenders[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"location\": ").Append(Q(o.Location))
                  .Append(", \"objectPath\": ").Append(Q(o.ObjectPath))
                  .Append(", \"kind\": ").Append(Q(o.Kind))
                  .Append(", \"detail\": ").Append(Q(o.Detail)).Append(" }");
            }
            sb.Append(r.Offenders.Count > 0 ? "\n  ]\n}" : "]\n}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = $"{RunLogDir}/check-package_{Sanitize(label)}_{stamp}.json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static string Leaf(string assetPath) => RunLogFormat.Leaf(assetPath);

        private static string Sanitize(string s) => RunLogFormat.Sanitize(s);

        private static string Q(string s) => RunLogFormat.Q(s);

        private class Report
        {
            public string Target;
            public string Mode;
            public int Scanned;
            public int MatResolved, MatEmpty, MatMissing;
            public readonly HashSet<string> DanglingTargets = new HashSet<string>();
            public readonly HashSet<string> UnidentifiedTargets = new HashSet<string>();
            public readonly Dictionary<int, TargetInfo> TargetCache = new Dictionary<int, TargetInfo>();
            public int MeshesMissing;
            public int ScriptsMissing;
            public int RemapStale;
            public int RemapUnresolved;
            public int LoadErrors;
            public string Result;
            public readonly List<Offender> Offenders = new List<Offender>();
        }

        private struct TargetInfo
        {
            public string Key;
            public string Detail;
            public bool Mapped;
        }

        private struct Offender
        {
            public string Location;
            public string ObjectPath;
            public string Kind;
            public string Detail;
        }
    }
}
