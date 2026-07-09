using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Read-only static report of a vendor avatar package (Phase-1 graph).
    /// Inspection-only — never mutates any asset or opens a scene.
    ///
    /// Call <see cref="Report"/> from MCP execute_code or a menu item, passing the vendor
    /// avatar folder (e.g. "Assets/Vendor/Avatars/Chocolat"). It writes a JSON RunLog to
    /// Assets/Agent/RunLogs/ and returns a one-line descriptive summary ending with that RunLog
    /// path (<c>… => OK | log=&lt;path&gt;</c>).
    ///
    /// This is a pure descriptive digest — it emits no PASS/FAIL verdict. An empty package
    /// (fbx=0 prefab=0) is a fact the digest states, not a failure. Only bad input (an invalid
    /// folder) or an exception mid-scan is an ERROR — the digest refuses and names the problem.
    ///
    /// FBX mesh data is read via AssetDatabase.LoadAssetAtPath (no scene involvement).
    /// Prefab inspection uses PrefabUtility.LoadPrefabContents / UnloadPrefabContents
    /// in an isolated preview scene.
    /// </summary>
    [AgentTool]
    public static class ReportPackage
    {
        // ── Public API ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Inspect <paramref name="vendorFolder"/> and emit a graph RunLog.
        /// Returns a one-line descriptive summary ending with the RunLog path (<c>… => OK | log=&lt;path&gt;</c>);
        /// bad input or a mid-scan exception ends <c>=> ERROR</c>. Also Debug.Log/LogError it.
        /// </summary>
        public static string Report(string vendorFolder)
        {
            string label = TransplantCore.Leaf(vendorFolder);

            if (string.IsNullOrEmpty(vendorFolder) || !AssetDatabase.IsValidFolder(vendorFolder))
            {
                string err = "[ReportPackage] " + label + ": not a valid asset folder: " + vendorFolder
                           + " — pass an existing folder under Assets/ => ERROR";
                Debug.LogError(err);
                return err;
            }

            var data = new GraphData { Target = vendorFolder };

            try
            {
                // 1. FBX mesh inventory (SkinnedMeshRenderer + MeshFilter/MeshRenderer)
                CollectFbxData(vendorFolder, data);

                // 2. Prefab scan: constraint count + MA/VRCF/NDMF detection
                var prefabPaths = FindPrefabs(vendorFolder);
                data.PrefabCount = prefabPaths.Count;
                ScanPrefabs(prefabPaths, data);

                // 3. FX controller resolution + per-mesh toggle membership
                BuildToggles(vendorFolder, prefabPaths, data);

                // 4. Superset detection across FBXes
                ComputeSuperset(data);

                // 5. Head vs body flag (blendShapeCount heuristic)
                ComputeHeadBody(data);

                // No content verdict: fbx/prefab counts are facts the digest states, not a gate.
                // An empty package (fbx=0 prefab=0) is reported as-is, not a failure.
            }
            catch (Exception ex)
            {
                // Never propagate — record the exception (the lone ERROR path) and still leave a RunLog trace.
                data.Error = ex.Message;
            }

            string logPath = WriteRunLog(data, label);

            // An exception mid-scan is the only ERROR the digest can hit here (bad input already
            // returned above). Otherwise the summary is a verdict-free descriptive digest.
            bool errored = data.Error != null;
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[ReportPackage] {0}: fbx={1} prefab={2} constraints={3} thirdParty={4} headGuess={5} bodyGuess={6} superset={7}{8}{9} => {10} | log={11}",
                label, data.FbxEntries.Count, data.PrefabCount, data.Constraints,
                data.ThirdPartyComponents, data.HeadMesh ?? "?", data.BodyMesh ?? "?",
                data.SupersetFbx ?? "none",
                data.LoadErrors > 0 ? " loadErrors=" + data.LoadErrors : "",
                errored ? " error=" + data.Error : "",
                errored ? "ERROR" : "OK", logPath);

            if (errored) Debug.LogError(summary); else Debug.Log(summary);
            return summary;
        }

        // ── FBX Inventory ─────────────────────────────────────────────────────────────────────

        private static void CollectFbxData(string vendorFolder, GraphData data)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { vendorFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = new FbxEntry { Path = path, Name = TransplantCore.Leaf(path) };

                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null) { data.FbxEntries.Add(entry); continue; }

                // SkinnedMeshRenderers (main avatar meshes)
                foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var ri = BuildRendererInfo(smr.name, smr.sharedMesh);
                    entry.Renderers.Add(ri);
                    if (!entry.MeshNames.Contains(smr.name))
                        entry.MeshNames.Add(smr.name);
                }

                // MeshFilter + MeshRenderer pairs (props, accessories)
                foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.GetComponent<MeshRenderer>() == null) continue; // skip non-visual
                    if (entry.MeshNames.Contains(mf.name)) continue;       // already from SMR
                    var ri = BuildRendererInfo(mf.name, mf.sharedMesh);
                    entry.Renderers.Add(ri);
                    entry.MeshNames.Add(mf.name);
                }

                data.FbxEntries.Add(entry);
            }
        }

        private static RendererInfo BuildRendererInfo(string name, Mesh mesh)
        {
            return new RendererInfo
            {
                Name = name,
                VertexCount     = mesh != null ? mesh.vertexCount     : -1,
                SubMeshCount    = mesh != null ? mesh.subMeshCount    : -1,
                BlendShapeCount = mesh != null ? mesh.blendShapeCount : -1,
            };
        }

        // ── Superset detection ─────────────────────────────────────────────────────────────────

        private static void ComputeSuperset(GraphData data)
        {
            if (data.FbxEntries.Count == 0) { data.SupersetFbx = "none"; return; }
            if (data.FbxEntries.Count == 1)
            {
                data.FbxEntries[0].IsSuperset = true;
                data.SupersetFbx = data.FbxEntries[0].Name;
                return;
            }

            FbxEntry winner = null;
            foreach (var candidate in data.FbxEntries)
            {
                bool isSuper = true;
                foreach (var other in data.FbxEntries)
                {
                    if (ReferenceEquals(other, candidate)) continue;
                    foreach (var name in other.MeshNames)
                    {
                        if (!candidate.MeshNames.Contains(name)) { isSuper = false; break; }
                    }
                    if (!isSuper) break;
                }
                if (isSuper) { winner = candidate; break; }
            }

            data.SupersetFbx = winner != null ? winner.Name : "none";
            if (winner != null) winner.IsSuperset = true;
        }

        // ── Head / body detection ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Heuristic: renderer with the most blend shapes is the face/head mesh; second-most is
        /// body. Uses the superset FBX as the reference so all mesh names are present.
        /// </summary>
        private static void ComputeHeadBody(GraphData data)
        {
            // Prefer the superset FBX as the reference for the heuristic
            FbxEntry source = null;
            foreach (var e in data.FbxEntries) if (e.IsSuperset) { source = e; break; }
            if (source == null && data.FbxEntries.Count > 0) source = data.FbxEntries[0];
            if (source == null) return;

            RendererInfo head = null;
            RendererInfo body = null;

            foreach (var ri in source.Renderers)
            {
                if (head == null || ri.BlendShapeCount > head.BlendShapeCount)
                {
                    body = head;
                    head = ri;
                }
                else if (body == null || ri.BlendShapeCount > body.BlendShapeCount)
                {
                    body = ri;
                }
            }

            // Honesty guard: if even the top mesh has no readable mesh (blendShapeCount < 0,
            // i.e. all renderers had a null sharedMesh), don't name a sentinel mesh as head/body.
            if (head == null || head.BlendShapeCount < 0) return;

            data.HeadMesh = head.Name;
            data.BodyMesh = body != null ? body.Name : null;

            // Two hedge vocabularies, by design: top-level headGuess/bodyGuess name the single mesh this
            // heuristic picked; the per-renderer likelyHead/likelyBody booleans mark that same pick across
            // every FBX renderer. They are the same guess viewed two ways, not drift.
            foreach (var e in data.FbxEntries)
                foreach (var ri in e.Renderers)
                {
                    ri.LikelyHead = data.HeadMesh != null && ri.Name == data.HeadMesh;
                    ri.LikelyBody = data.BodyMesh != null && ri.Name == data.BodyMesh;
                }
        }

        // ── Prefab scan: constraints + third-party components ─────────────────────────────────

        private static List<string> FindPrefabs(string vendorFolder)
        {
            var list = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { vendorFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    list.Add(path);
            }
            return list;
        }

        private static void ScanPrefabs(List<string> prefabPaths, GraphData data)
        {
            var thirdParty = new HashSet<string>();

            foreach (var path in prefabPaths)
            {
                GameObject root;
                try { root = PrefabUtility.LoadPrefabContents(path); }
                catch (Exception e)
                {
                    data.LoadErrors++;
                    Debug.LogWarning("[ReportPackage] load failed: " + path + " — " + e.Message);
                    continue;
                }
                try
                {
                    foreach (var comp in root.GetComponentsInChildren<Component>(true))
                    {
                        if (comp == null) continue; // missing script slot
                        var fullName  = comp.GetType().FullName ?? "";
                        var shortName = comp.GetType().Name;

                        // Third-party detection: match by namespace / type name
                        if (fullName.Contains("nadena.dev.modular_avatar") ||
                            shortName.IndexOf("ModularAvatar", StringComparison.OrdinalIgnoreCase) >= 0)
                            thirdParty.Add("ModularAvatar");

                        if (fullName.StartsWith("VF.", StringComparison.Ordinal) ||
                            fullName.Contains(".VRCFury") ||
                            shortName.IndexOf("VRCFury", StringComparison.OrdinalIgnoreCase) >= 0)
                            thirdParty.Add("VRCFury");

                        if (fullName.Contains("nadena.dev.ndmf"))
                            thirdParty.Add("NDMF");

                        // Constraint detection: Unity built-in + VRChat constraints share "Constraint" in the name
                        if (shortName.Contains("Constraint") || fullName.Contains("Constraint"))
                            data.Constraints++;
                    }
                }
                catch (Exception e)
                {
                    data.LoadErrors++;
                    Debug.LogWarning("[ReportPackage] inspect failed: " + path + " — " + e.Message);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }

            data.ThirdPartyComponents = thirdParty.Count == 0
                ? "none"
                : string.Join(", ", thirdParty);
        }

        // ── FX controller + toggle detection ─────────────────────────────────────────────────

        private static void BuildToggles(string vendorFolder, List<string> prefabPaths, GraphData data)
        {
            string fxPath;
            AnimatorController fx = FindFxController(vendorFolder, prefabPaths, data, out fxPath);
            data.FxControllerPath = fxPath;

            if (fx == null)
            {
                data.ToggleStatus = "fx-controller-not-found";
                return;
            }

            // Collect all transform paths that have an m_IsActive (GameObject active) binding
            var togglePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var clip in fx.animationClips)
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.propertyName == "m_IsActive")
                        togglePaths.Add(binding.path);
                }
            }

            // Match paths to renderer names (path is hierarchy from avatar root, e.g. "Body" or "Armature/Body")
            foreach (var entry in data.FbxEntries)
                foreach (var ri in entry.Renderers)
                    ri.HasToggle = MatchesTogglePath(togglePaths, ri.Name);

            data.ToggleStatus = "ok";
        }

        /// <summary>
        /// Tries two strategies in order:
        /// 1. Load each prefab via PrefabUtility.LoadPrefabContents, find VRCAvatarDescriptor,
        ///    walk baseAnimationLayers for the FX layer's animatorController.
        /// 2. Scan t:AnimatorController under vendorFolder for one whose name contains "_FX".
        /// Returns null (and sets fxPath to null) if neither succeeds — caller degrades gracefully.
        /// </summary>
        private static AnimatorController FindFxController(string vendorFolder, List<string> prefabPaths, GraphData data, out string fxPath)
        {
            fxPath = null;

            // Strategy 1: VRCAvatarDescriptor playable layers
            foreach (var path in prefabPaths)
            {
                GameObject root;
                try { root = PrefabUtility.LoadPrefabContents(path); }
                catch (Exception e)
                {
                    data.LoadErrors++;
                    Debug.LogWarning("[ReportPackage] load failed: " + path + " — " + e.Message);
                    continue;
                }

                AnimatorController found = null;
                string foundPath = null;
                try
                {
                    var desc = root.GetComponent<VRCAvatarDescriptor>();
                    if (desc == null) desc = root.GetComponentInChildren<VRCAvatarDescriptor>(true);
                    if (desc != null)
                    {
                        foreach (var layer in desc.baseAnimationLayers)
                        {
                            if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX &&
                                !layer.isDefault &&
                                layer.animatorController != null)
                            {
                                var ctrl = layer.animatorController as AnimatorController;
                                if (ctrl != null)
                                {
                                    foundPath = AssetDatabase.GetAssetPath(ctrl);
                                    found = ctrl;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    data.LoadErrors++;
                    Debug.LogWarning("[ReportPackage] inspect failed: " + path + " — " + e.Message);
                }
                finally
                {
                    // Always unload; the AnimatorController reference is a persistent asset and survives.
                    PrefabUtility.UnloadPrefabContents(root);
                }

                if (found != null) { fxPath = foundPath; return found; }
            }

            // Strategy 2: scan by name convention (_FX in the filename)
            foreach (var guid in AssetDatabase.FindAssets("t:AnimatorController", new[] { vendorFolder }))
            {
                var ap = AssetDatabase.GUIDToAssetPath(guid);
                if (ap.IndexOf("_FX", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ap);
                    if (ctrl != null) { fxPath = ap; return ctrl; }
                }
            }

            return null;
        }

        /// <summary>
        /// Returns true if any toggle path equals the renderer name or ends with /rendererName.
        /// The binding path in an AnimationClip is the transform hierarchy path from the avatar
        /// root, which may be just the name (e.g. "Body") or a full path (e.g. "Armature/Body").
        /// </summary>
        private static bool MatchesTogglePath(HashSet<string> togglePaths, string rendererName)
        {
            foreach (var tp in togglePaths)
            {
                if (string.Equals(tp, rendererName, StringComparison.OrdinalIgnoreCase)) return true;
                if (tp.EndsWith("/" + rendererName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ── RunLog output ─────────────────────────────────────────────────────────────────────

        private static string WriteRunLog(GraphData data, string label)
        {
            Directory.CreateDirectory(TransplantCore.RunLogDir);
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"report-package\",\n");
            sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"target\": ").Append(TransplantCore.Q(data.Target)).Append(",\n");
            sb.Append("  \"loadErrors\": ").Append(data.LoadErrors).Append(",\n");
            sb.Append("  \"error\": ").Append(TransplantCore.Q(data.Error)).Append(",\n");
            sb.Append("  \"fbxCount\": ").Append(data.FbxEntries.Count).Append(",\n");
            sb.Append("  \"prefabCount\": ").Append(data.PrefabCount).Append(",\n");
            sb.Append("  \"supersetFbx\": ").Append(TransplantCore.Q(data.SupersetFbx ?? "none")).Append(",\n");
            sb.Append("  \"headGuess\": ").Append(TransplantCore.Q(data.HeadMesh)).Append(",\n");
            sb.Append("  \"bodyGuess\": ").Append(TransplantCore.Q(data.BodyMesh)).Append(",\n");
            sb.Append("  \"headBodyHeuristic\": ")
              .Append(data.HeadMesh != null ? TransplantCore.Q("most-blendshapes renderer = face; verify") : "null")
              .Append(",\n");
            sb.Append("  \"fxController\": ").Append(TransplantCore.Q(data.FxControllerPath)).Append(",\n");
            sb.Append("  \"toggles\": ").Append(TransplantCore.Q(data.ToggleStatus)).Append(",\n");
            sb.Append("  \"constraints\": ").Append(data.Constraints).Append(",\n");
            sb.Append("  \"thirdPartyComponents\": ").Append(TransplantCore.Q(data.ThirdPartyComponents)).Append(",\n");
            sb.Append("  \"fbxes\": [");

            for (int fi = 0; fi < data.FbxEntries.Count; fi++)
            {
                var e = data.FbxEntries[fi];
                sb.Append(fi == 0 ? "\n" : ",\n");
                sb.Append("    {\n");
                sb.Append("      \"path\": ").Append(TransplantCore.Q(e.Path)).Append(",\n");
                sb.Append("      \"name\": ").Append(TransplantCore.Q(e.Name)).Append(",\n");
                sb.Append("      \"isSuperset\": ").Append(e.IsSuperset ? "true" : "false").Append(",\n");
                sb.Append("      \"meshNames\": [");
                for (int mi = 0; mi < e.MeshNames.Count; mi++)
                {
                    if (mi > 0) sb.Append(", ");
                    sb.Append(TransplantCore.Q(e.MeshNames[mi]));
                }
                sb.Append("],\n");
                sb.Append("      \"renderers\": [");
                for (int ri = 0; ri < e.Renderers.Count; ri++)
                {
                    var r = e.Renderers[ri];
                    sb.Append(ri == 0 ? "\n" : ",\n");
                    sb.Append("        {")
                      .Append(" \"name\": ").Append(TransplantCore.Q(r.Name))
                      .Append(", \"vertexCount\": ").Append(r.VertexCount)
                      .Append(", \"subMeshCount\": ").Append(r.SubMeshCount)
                      .Append(", \"blendShapeCount\": ").Append(r.BlendShapeCount)
                      .Append(", \"likelyHead\": ").Append(r.LikelyHead ? "true" : "false")
                      .Append(", \"likelyBody\": ").Append(r.LikelyBody ? "true" : "false")
                      .Append(", \"hasToggle\": ").Append(r.HasToggle ? "true" : "false")
                      .Append(" }");
                }
                sb.Append(e.Renderers.Count > 0 ? "\n      " : "");
                sb.Append("]\n    }");
            }

            sb.Append(data.FbxEntries.Count > 0 ? "\n  ]\n}" : "]\n}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = TransplantCore.RunLogDir + "/graph_" + TransplantCore.Sanitize(label) + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        // ── Data types ────────────────────────────────────────────────────────────────────────

        private class GraphData
        {
            public string Target;
            public string Error;
            public int    LoadErrors;
            public int    PrefabCount;
            public int    Constraints;
            public string ThirdPartyComponents = "none";
            public string FxControllerPath;
            public string ToggleStatus;
            public string SupersetFbx;
            public string HeadMesh;
            public string BodyMesh;
            public readonly List<FbxEntry> FbxEntries = new List<FbxEntry>();
        }

        private class FbxEntry
        {
            public string Path;
            public string Name;
            public bool   IsSuperset;
            public readonly List<string>       MeshNames = new List<string>();
            public readonly List<RendererInfo> Renderers = new List<RendererInfo>();
        }

        private class RendererInfo
        {
            public string Name;
            public int    VertexCount;
            public int    SubMeshCount;
            public int    BlendShapeCount;
            public bool   LikelyHead;
            public bool   LikelyBody;
            public bool   HasToggle;
        }
    }
}
