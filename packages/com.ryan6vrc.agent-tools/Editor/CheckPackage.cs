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
    /// Deterministic check-package for the AI-assisted VRChat workflow.
    ///
    /// Walks prefabs (under a folder) or selected objects and reports four classes of broken
    /// reference a vendor import can leave behind:
    ///   - material slots: resolved / empty / MISSING
    ///   - renderer meshes: present / MISSING
    ///   - MonoBehaviours: missing script references
    ///   - FBX external-material remap: resolves but the imported model is left empty (stale import)
    ///
    /// The load-bearing distinction is MISSING vs EMPTY. A slot whose serialized reference has a
    /// non-zero instance id that fails to resolve is broken; a clean-zero slot is an intentional
    /// empty (e.g. an unused submesh). Counting raw nulls is a false-alarm trap — a healthy
    /// costume routinely has hundreds of intentionally-empty submesh slots. This counts only the
    /// broken ones, so PASS/FAIL is meaningful.
    ///
    /// A MISSING count alone says how many slots broke, not how many things broke: one vendor-side
    /// mistake can dangle a thousand slots at two targets. So each MISSING offender names the target
    /// its reference still points at, and the summary counts the distinct targets behind the slots.
    ///
    /// The remap-stale check catches what the empty-vs-MISSING rule deliberately ignores: an FBX
    /// using external materials (materialLocation: External) is remapped to .mat assets only at
    /// import time. Import it before those materials exist (e.g. costume package before a separate
    /// MaterialPack) and it caches empty slots that no later import re-applies — the model reads as
    /// "intentionally empty" yet renders untextured. When the remap resolves to real materials but
    /// the imported renderers are still empty, that's an unambiguous stale import: force-reimport.
    ///
    /// Prefab assets are inspected via LoadPrefabContents, which composes variant overrides in an
    /// isolated preview scene without touching the open scene. INSPECTION ONLY — never mutates.
    /// </summary>
    [AgentTool]
    public static class CheckPackage
    {
        private const string RunLogDir = RunLogFormat.RunLogDir;

        // ----- Public API (callable from execute_code / the import skill) ---------------------

        /// <summary>Verify every prefab under an asset folder. Returns a one-line PASS/FAIL summary;
        /// when a verification run was performed it ends with the RunLog path (<c>… => RESULT | log=&lt;path&gt;</c>) —
        /// a bad-input early return is a bare <c>[CheckPackage] FAIL: …</c> with no trailer.</summary>
        public static string VerifyFolder(string assetFolderPath)
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
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { assetFolderPath }))
                ScanModelRemap(AssetDatabase.GUIDToAssetPath(guid), r);
            return Finish(r, Leaf(assetFolderPath));
        }

        /// <summary>Verify selected prefab assets and/or scene GameObjects.</summary>
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

        // ----- Scanning -----------------------------------------------------------------------

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
                    int id = el.objectReferenceInstanceIDValue;
                    r.MatMissing++;
                    r.MissingTargets.Add(id);
                    r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "material-missing", Detail = "material slot " + i + " holds a dangling reference: " + DanglingTarget(id) });
                }
                else r.MatEmpty++; // intentional empty submesh slot
            }
        }

        private static void ScanMeshRef(SerializedObject so, string location, string goPath, string compName, Report r)
        {
            var p = so.FindProperty("m_Mesh");
            if (p == null || p.objectReferenceValue != null) return;     // no slot, or mesh present
            if (p.objectReferenceInstanceIDValue == 0) return;           // intentionally no mesh
            r.MeshesMissing++;
            r.Offenders.Add(new Offender { Location = location, ObjectPath = goPath, Kind = "mesh-missing", Detail = compName + " mesh holds a dangling reference: " + DanglingTarget(p.objectReferenceInstanceIDValue) });
        }

        // Names what a broken reference still points at, which decides the remedy: a guid that resolves
        // to a real file means the reference aims at a sub-object that file does not contain (an FBX set
        // to external materials never creates the material sub-objects its prefab variants override, so
        // the fix is at the referencing end), while an absent guid means the asset was never imported.
        private static string DanglingTarget(int instanceId)
        {
            // Only the long-localId overload is usable; the int one throws unconditionally.
            bool mapped = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(instanceId, out string guid, out long fileId);
            return DescribeTarget(instanceId, mapped, guid, fileId, mapped ? AssetDatabase.GUIDToAssetPath(guid) : null);
        }

        /// <summary>Pure wording for the three shapes the answer takes, so each is assertable without
        /// having to provoke a real dangling reference. The unmapped branch is a genuine unknown, not a
        /// known-impossible one: whether the guid mapping survives for an instance id whose object failed
        /// to load is unverified, so that branch stays a reported outcome rather than an assumption.</summary>
        internal static string DescribeTarget(int instanceId, bool mapped, string guid, long fileId, string assetPath)
        {
            if (!mapped) return "instanceID " + instanceId + " has no guid mapping";
            return string.IsNullOrEmpty(assetPath)
                ? "guid " + guid + " is absent from this project"
                : "guid " + guid + " resolves to " + assetPath + ", which holds no object with fileID " + fileId;
        }

        // An FBX using external materials is remapped to .mat assets only at import time. If the
        // remap resolves to real materials but the imported model's renderers are still empty, the
        // model was imported before those materials existed and never re-applied — a stale import
        // that reads as "intentionally empty" yet renders untextured. Fix: force-reimport the FBX.
        private static void ScanModelRemap(string assetPath, Report r)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null || importer.materialLocation != ModelImporterMaterialLocation.External) return;

            int resolvableRemaps = 0;
            foreach (var kv in importer.GetExternalObjectMap())
                if (kv.Key.type == typeof(Material) && kv.Value != null) resolvableRemaps++;
            if (resolvableRemaps == 0) return;

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null) return;
            int empty = 0;
            foreach (var rend in model.GetComponentsInChildren<Renderer>(true))
            {
                if (rend is ParticleSystemRenderer) continue;
                foreach (var m in rend.sharedMaterials) if (m == null) empty++;
            }
            if (empty == 0) return;

            r.RemapStale += empty;
            r.Offenders.Add(new Offender
            {
                Location = assetPath, ObjectPath = "", Kind = "fbx-remap-stale",
                Detail = empty + " empty slot(s) despite a resolvable external-material remap — force-reimport the FBX"
            });
        }

        // ----- Output -------------------------------------------------------------------------

        private static string Finish(Report r, string label)
        {
            bool pass = r.MatMissing == 0 && r.MeshesMissing == 0 && r.ScriptsMissing == 0 && r.RemapStale == 0 && r.LoadErrors == 0;
            r.Result = pass ? "PASS" : "FAIL";
            string logPath = WriteRunLog(r, label);

            // Distinct targets separate one systematic vendor break from N independent ones, so it rides
            // next to the slot count it qualifies.
            string targets = r.MatMissing == 0 ? "" :
                " (" + r.MissingTargets.Count + " distinct dangling target" + (r.MissingTargets.Count == 1 ? "" : "s") + ")";

            string summary = string.Format(CultureInfo.InvariantCulture,
                "[CheckPackage] {0} ({1}, {2} scanned): materials resolved={3} empty={4} MISSING={5}{6} | meshMISSING={7} | scriptMISSING={8} | remapSTALE={9}{10} => {11} | log={12}",
                label, r.Mode, r.Scanned, r.MatResolved, r.MatEmpty, r.MatMissing, targets, r.MeshesMissing, r.ScriptsMissing, r.RemapStale,
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
              .Append(", \"missing\": ").Append(r.MatMissing)
              .Append(", \"missingDistinctTargets\": ").Append(r.MissingTargets.Count).Append(" },\n");
            sb.Append("  \"meshesMissing\": ").Append(r.MeshesMissing).Append(",\n");
            sb.Append("  \"scriptsMissing\": ").Append(r.ScriptsMissing).Append(",\n");
            sb.Append("  \"remapStale\": ").Append(r.RemapStale).Append(",\n");
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
            // Kind-prefixed filename matching the JSON "kind" (was verify_<label> — the drift #28
            // accepted "until touched"; nothing globs the old prefix).
            var path = $"{RunLogDir}/check-package_{Sanitize(label)}_{stamp}.json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        // ----- Helpers ------------------------------------------------------------------------

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static string Leaf(string assetPath) => RunLogFormat.Leaf(assetPath);

        private static string Sanitize(string s) => RunLogFormat.Sanitize(s);

        private static string Q(string s) => RunLogFormat.Q(s);

        // ----- Types --------------------------------------------------------------------------

        private class Report
        {
            public string Target;
            public string Mode;
            public int Scanned;
            public int MatResolved, MatEmpty, MatMissing;
            public readonly HashSet<int> MissingTargets = new HashSet<int>(); // instance ids behind MatMissing
            public int MeshesMissing;
            public int ScriptsMissing;
            public int RemapStale;
            public int LoadErrors;
            public string Result;
            public readonly List<Offender> Offenders = new List<Offender>();
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
