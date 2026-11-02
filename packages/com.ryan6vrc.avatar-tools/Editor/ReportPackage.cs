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
    /// <summary>Read-only inventory of a vendor avatar folder: FBX meshes, the face and body picks, constraint count,
    /// non-SDK component census and FX toggle reach. Prefabs load into an isolated preview scene, so no scene opens.
    /// Contract: docs/unity-tools.md.</summary>
    [AgentTool]
    public static class ReportPackage
    {
        // ── Public API ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Inspects <paramref name="vendorFolder"/>, writes a graph RunLog to Assets/Agent/RunLogs/, and returns and logs a
        /// one-line summary (<c>… => OK | log=&lt;path&gt;</c>). An empty package is OK. A mid-scan exception reports
        /// <c>=> ERROR | log=&lt;path&gt;</c>; an invalid folder reports <c>=> ERROR</c> and writes no log.
        /// </summary>
        public static string Run(string vendorFolder)
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
                CollectFbxData(vendorFolder, data);

                var prefabPaths = FindPrefabs(vendorFolder);
                data.PrefabCount = prefabPaths.Count;
                ScanPrefabs(prefabPaths, data);

                BuildToggles(vendorFolder, prefabPaths, data);

                ComputeSuperset(data);

                ResolveVisemeAndBody(data);
            }
            catch (Exception ex)
            {
                data.Error = ex.Message;
            }

            string logPath = WriteRunLog(data, label);

            bool errored = data.Error != null;
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[ReportPackage] {0}: fbx={1} prefab={2} constraints={3} nonSdkNs={4} toggles={5} visemeMesh={6} bodyGuess={7} superset={8}{9}{10}{11}{12}{13} => {14} | log={15}",
                label, data.FbxEntries.Count, data.PrefabCount, data.Constraints,
                NonSdkSummary(data.NonSdk), data.ToggleSummary ?? "?",
                VisemeField(data), data.BodyMesh ?? "?",
                data.SupersetFbx ?? "none",
                data.AvatarFbxPath != null ? " avatarFbx=" + TransplantCore.Leaf(data.AvatarFbxPath) : "",
                data.VisemeJoinedFbxCount > 1 ? " avatarFbxCandidates=" + data.VisemeJoinedFbxCount : "",
                data.UnresolvedScripts > 0 ? " unresolvedScripts=" + data.UnresolvedScripts : "",
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

                foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var ri = BuildRendererInfo(smr.name, smr.sharedMesh);
                    entry.Renderers.Add(ri);
                    if (!entry.MeshNames.Contains(smr.name))
                        entry.MeshNames.Add(smr.name);
                }

                foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.GetComponent<MeshRenderer>() == null) continue;
                    if (entry.MeshNames.Contains(mf.name)) continue;
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
                // Kept for the join in ResolveVisemeAndBody: the descriptor names its face renderer on a PREFAB, whose
                // GameObject name can differ from the FBX transform's, and both sides load the same Mesh asset instance.
                Mesh            = mesh,
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

        // ── Viseme (face) mesh + body pick ────────────────────────────────────────────────────

        /// <summary>The <c>visemeMesh=</c> field: the mesh name with its basis in parens.</summary>
        internal static string VisemeSummary(string meshName, string basis)
        {
            if (meshName == null) return "?";
            return meshName + "(" + (basis ?? "unknown") + ")";
        }

        private static string VisemeField(GraphData data)
        {
            return VisemeSummary(data.VisemeMesh, data.VisemeBasis);
        }

        /// <summary>
        /// The lipSync modes that actually declare a face mesh. The inspector's mode popup only switches
        /// which fields it DRAWS — it never clears <c>VisemeSkinnedMesh</c> — so a descriptor left on
        /// <c>JawFlapBone</c>, <c>Default</c> or <c>VisemeParameterOnly</c> can still carry a live pointer to
        /// whatever face mesh was selected before the switch.
        /// </summary>
        internal static bool DeclaresFaceMesh(VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle style)
        {
            return style == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape
                || style == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape;
        }

        /// <summary>Records a descriptor's declared face mesh for <see cref="ResolveVisemeAndBody"/> to join.</summary>
        private static void NoteVisemeMesh(GraphData data, VRCAvatarDescriptor desc)
        {
            if (desc == null || !DeclaresFaceMesh(desc.lipSync)) return;
            var smr = desc.VisemeSkinnedMesh;
            if (smr == null) return;
            var mesh = smr.sharedMesh;
            if (mesh == null) return;

            data.VisemeDescriptors++;
            if (!data.VisemeDeclared.Contains(mesh)) data.VisemeDeclared.Add(mesh);
            data.VisemeDistinctAssets = data.VisemeDeclared.Count;
        }

        /// <summary>
        /// Whether the body pick excludes the face mesh by identity rather than by name: only where a descriptor's
        /// mesh joined the inventory, since a declared mesh this package lacks would match no renderer and exclude nothing.
        /// </summary>
        internal static bool ExcludesFaceByIdentity(string visemeBasis)
        {
            return visemeBasis == "descriptor";
        }

        private static bool CarriesADeclaredMesh(FbxEntry e, List<Mesh> declared)
        {
            foreach (var ri in e.Renderers)
                if (ri.Mesh != null && declared.Contains(ri.Mesh)) return true;
            return false;
        }

        /// <summary>
        /// Resolves the face mesh and the body pick. The descriptor route joins a declared mesh to the FBX inventory by
        /// Mesh asset identity, so the mesh and its FBX are read rather than inferred. The guess route takes the
        /// most-blendshapes renderer and labels it, where no descriptor declared a mesh or the declared mesh is outside
        /// this folder's <c>t:Model</c> scan, ordinary for an outfit or hair package whose descriptor points at a base
        /// body in another folder. The body pick is a heuristic on both routes: <see cref="BodyGuessNote"/>.
        /// </summary>
        private static void ResolveVisemeAndBody(GraphData data)
        {
            FbxEntry source = null;

            // ---- Descriptor route: identity-join the declared meshes into the FBX inventory ----
            // Every declared mesh is tried, in path order, and the first that joins wins: an outfit prefab sorting
            // ahead of the avatar's names a base body in another package, which does not join.
            foreach (var declared in data.VisemeDeclared)
            {
                foreach (var e in data.FbxEntries)
                {
                    foreach (var ri in e.Renderers)
                        if (ri.Mesh == declared) { source = e; break; }
                    if (source != null) break;
                }
                if (source != null) { data.VisemeSharedMesh = declared; break; }
            }

            foreach (var e in data.FbxEntries)
                if (CarriesADeclaredMesh(e, data.VisemeDeclared)) data.VisemeJoinedFbxCount++;

            if (source != null)
            {
                data.AvatarFbxPath = source.Path;
                data.VisemeBasis   = "descriptor";
                foreach (var e in data.FbxEntries)
                    foreach (var ri in e.Renderers)
                        if (ri.Mesh == data.VisemeSharedMesh)
                        {
                            ri.IsVisemeMesh = true;
                            if (data.VisemeMesh == null) data.VisemeMesh = ri.Name;
                        }
            }
            else
            {
                // ---- Guess route: the superset FBX, else the first scanned ----
                foreach (var e in data.FbxEntries) if (e.IsSuperset) { source = e; break; }
                if (source == null && data.FbxEntries.Count > 0) source = data.FbxEntries[0];
                if (source == null) return;

                RendererInfo top = null;
                foreach (var ri in source.Renderers)
                    if (top == null || ri.BlendShapeCount > top.BlendShapeCount) top = ri;

                // Every renderer had a null sharedMesh (BlendShapeCount -1): name nothing.
                if (top == null || top.BlendShapeCount < 0) return;

                data.VisemeMesh  = top.Name;
                data.VisemeBasis = "guess:most-blendshapes";
            }

            // ---- Body pick: most blend shapes on the reference FBX that is not the face mesh ----
            bool byIdentity = ExcludesFaceByIdentity(data.VisemeBasis);
            RendererInfo body = null;
            foreach (var ri in source.Renderers)
            {
                bool isFace = byIdentity
                    ? ri.Mesh == data.VisemeSharedMesh
                    : ri.Name == data.VisemeMesh;
                if (isFace) continue;
                if (body == null || ri.BlendShapeCount > body.BlendShapeCount) body = ri;
            }
            if (body == null || body.BlendShapeCount < 0) return;

            data.BodyMesh = body.Name;
            foreach (var e in data.FbxEntries)
                foreach (var ri in e.Renderers)
                    ri.LikelyBody = ri.Name == data.BodyMesh;
        }

        // ── Prefab scan: constraints + the non-SDK component census ───────────────────────────

        /// <summary>
        /// Namespace roots that are Unity or VRChat-SDK infrastructure: a first filter that spares most components the
        /// reflection in <see cref="IsFromSdkAssembly"/>, which decides the rest. A listed root excludes without consulting
        /// the assembly, so a vendor component authored under <c>VRC.*</c> or <c>Unity.*</c> vanishes from the census.
        /// </summary>
        private static readonly HashSet<string> SdkNamespaceRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityEngine", "UnityEditor", "Unity", "TMPro", "VRC", "VRCSDK2",
        };

        /// <summary>
        /// Assembly-name prefixes for the engine, the .NET base library and the VRChat SDK, including the plugin assemblies
        /// the SDK vendors (UniTask, DOTween). Matching is plain prefix, so no entry may swallow a longer non-SDK name:
        /// VRCFury's runtime assembly is named <c>VRCFury</c>, which is why <c>VRC</c> is split into <c>VRC.</c> /
        /// <c>VRCSDK</c> / <c>VRCCore</c>. A framework wrongly excluded here leaves <see cref="ComposeToggleCaveat"/>
        /// asserting that nothing compiles toggles at build.
        /// </summary>
        private static readonly string[] SdkAssemblyPrefixes =
        {
            "UnityEngine", "UnityEditor", "Unity.", "mscorlib", "netstandard", "System",
            "VRC.", "VRCSDK", "VRCCore", "SDKBase", "UniTask", "DOTween",
        };

        /// <summary>True when <paramref name="asmName"/> names engine, base-library, or VRChat-SDK code.</summary>
        internal static bool IsSdkAssemblyName(string asmName)
        {
            if (string.IsNullOrEmpty(asmName)) return false;
            foreach (var prefix in SdkAssemblyPrefixes)
                if (asmName.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static bool IsFromSdkAssembly(Type t)
        {
            if (t == null) return false;
            return IsSdkAssemblyName(t.Assembly.GetName().Name);
        }

        private const string NonSdkNote =
            "Verbatim Type.Namespace of every component whose assembly is not the engine, the .NET base library, or the "
          + "VRChat SDK, most-present first. The assembly is what settles \"ships inside the SDK\", because the SDK ships "
          + "global-namespace components that no namespace rule could exclude. These are namespaces, not framework names, "
          + "and this tool recognizes no framework by name — an entry means \"a non-SDK framework is on this prefab, go "
          + "read it\", never \"supported\". Global-namespace components key on their type name instead. Read "
          + "unresolvedScripts before treating this list as complete.";

        private const string VisemeNote =
            "The face mesh, with visemeMeshBasis naming how it was arrived at. \"descriptor\" is a FACT: a "
          + "VRCAvatarDescriptor whose lipSync mode declares a face mesh named it, and it was joined to this "
          + "inventory by Mesh ASSET IDENTITY, not by name — avatarFbx is the FBX that mesh lives in, and is the "
          + "reference for bodyGuess. \"guess:most-blendshapes\" is the old heuristic, reported only where no "
          + "descriptor answered or the mesh it named is not in this package's FBX inventory (ordinary for an "
          + "outfit or hair package pointing at a base body elsewhere) — verify it. Every declared mesh is "
          + "tried in prefab path order and the first that joins wins, so a package whose outfit prefab sorts "
          + "ahead of its avatar still reports the avatar's own answer. isVisemeMesh rides a renderer only on "
          + "the descriptor route; on the guess route it is absent rather than false, because nothing was "
          + "established. Read the two counts differently: visemeDistinctAssets > 1 is ORDINARY (a package "
          + "shipping several body FBX variants gives each its own face-mesh asset, so identity differs while "
          + "the answer does not), while visemeJoinedFbxCount > 1 says several FBXes here each carry a declared "
          + "face mesh — this package holds more than one candidate avatar, and avatarFbx names whichever "
          + "joined first in path order. Ordinary for a PC/Android pair or a kisekae variant; the same reading "
          + "is also what catches two sibling avatars in a family package, where the one named may not be the "
          + "avatar being worked on. Check it against the avatar you are owning before trusting avatarFbx.";

        private const string BodyGuessNote =
            "A HEURISTIC on both routes — nothing in the substrate declares \"the body mesh\". It is the "
          + "most-blendshapes renderer on the reference FBX excluding the face mesh, so on a base whose single "
          + "body mesh also carries the visemes it necessarily names something else, sometimes a prop. A tie is "
          + "broken by source order. Verify before anything hangs on it.";

        private const string UnresolvedScriptsNote =
            "Components whose script did not resolve, counted rather than skipped: an unresolvable component is by "
          + "definition a framework this census cannot name. Any nonzero count means nonSdkNamespaces is incomplete and "
          + "framework presence is unknown rather than absent — install the missing package (CheckPackage names the "
          + "offenders) and re-run before trusting this census or togglesCaveat.";

        /// <summary>
        /// The census key <paramref name="t"/> contributes, or null when it is Unity/VRChat-SDK infrastructure:
        /// <see cref="Type.Namespace"/> verbatim, or the type name for a global-namespace type.
        /// </summary>
        internal static string NonSdkKey(Type t)
        {
            if (t == null) return null;
            var ns = t.Namespace;
            int dot = ns == null ? -1 : ns.IndexOf('.');
            if (ns != null && SdkNamespaceRoots.Contains(dot < 0 ? ns : ns.Substring(0, dot))) return null;
            if (IsFromSdkAssembly(t)) return null;
            return ns ?? t.Name;
        }

        internal static List<KeyValuePair<string, int>> RankNonSdk(IDictionary<string, int> census)
        {
            var ranked = new List<KeyValuePair<string, int>>(census);
            ranked.Sort((a, b) => b.Value != a.Value
                ? b.Value.CompareTo(a.Value)
                : string.Compare(a.Key, b.Key, StringComparison.Ordinal));
            return ranked;
        }

        /// <summary>
        /// The <c>nonSdkNs=</c> field: census size, then the three most-present namespaces. Full list in the RunLog.
        /// </summary>
        internal static string NonSdkSummary(IDictionary<string, int> census)
        {
            var ranked = RankNonSdk(census);
            if (ranked.Count == 0) return "0";
            var sb = new StringBuilder(ranked.Count.ToString(CultureInfo.InvariantCulture)).Append('(');
            for (int i = 0; i < ranked.Count && i < 3; i++)
                sb.Append(i > 0 ? ", " : "").Append(ranked[i].Key);
            if (ranked.Count > 3) sb.Append(", +").Append(ranked.Count - 3).Append(" more");
            return sb.Append(')').ToString();
        }

        private static List<string> FindPrefabs(string vendorFolder)
        {
            var list = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { vendorFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    list.Add(path);
            }
            // FindAssets' order is unspecified; sorting makes FindFxController's first-hit pick reproducible, not right.
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static void ScanPrefabs(List<string> prefabPaths, GraphData data)
        {
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
                    foreach (var desc in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                        NoteVisemeMesh(data, desc);

                    foreach (var comp in root.GetComponentsInChildren<Component>(true))
                    {
                        // A null entry is a missing script: a framework present and unnameable, so it is counted.
                        if (comp == null) { data.UnresolvedScripts++; continue; }

                        var type      = comp.GetType();
                        var fullName  = type.FullName ?? "";
                        var shortName = type.Name;

                        var key = NonSdkKey(type);
                        if (key != null)
                        {
                            int seen;
                            data.NonSdk.TryGetValue(key, out seen);
                            data.NonSdk[key] = seen + 1;
                        }

                        // An upper bound: the name match also counts a vendor's ConstraintHelper. Contract:
                        // docs/unity-tools.md.
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
        }

        // ── FX controller + toggle detection ─────────────────────────────────────────────────

        /// <summary>
        /// Sets per-renderer <c>HasToggle</c> and the three toggle fields. The status field names its detector, yield and
        /// reach in every case, so all-false readings cannot pass for "this package has no toggles".
        /// </summary>
        private static void BuildToggles(string vendorFolder, List<string> prefabPaths, GraphData data)
        {
            int rendererCount = 0;
            foreach (var entry in data.FbxEntries) rendererCount += entry.Renderers.Count;

            string fxPath;
            AnimatorController fx = FindFxController(vendorFolder, prefabPaths, data, out fxPath,
                                                     out bool byPathMatch, out string fxPrefabPath);
            data.FxControllerPath = fxPath;

            int matched = 0;
            if (fx != null)
            {
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

                foreach (var entry in data.FbxEntries)
                    foreach (var ri in entry.Renderers)
                    {
                        ri.HasToggle = MatchesTogglePath(togglePaths, ri.Name);
                        if (ri.HasToggle) matched++;
                    }
            }

            string matchRatio = matched.ToString(CultureInfo.InvariantCulture) + "/"
                              + rendererCount.ToString(CultureInfo.InvariantCulture);

            bool frameworks = data.NonSdk.Count > 0 || data.UnresolvedScripts > 0;

            data.ToggleSummary = (fx != null ? matchRatio : "fx-controller-not-found")
                               + (frameworks ? "(clip-m_IsActive, caveated)" : "(clip-m_IsActive)");

            data.ToggleStatus = "clip-m_IsActive; matched=" + matchRatio + " renderer entries across all FBXes; "
                              + (fx != null
                                  ? "source=" + fxPath + (byPathMatch
                                      ? " (asset path carries _FX under " + vendorFolder
                                        + ", first in path order; no prefab of " + data.PrefabCount
                                        + " resolved a descriptor FX slot)"
                                      : " (descriptor FX slot on " + fxPrefabPath + " — the first of "
                                        + data.PrefabCount + " prefabs in path order that resolved one; "
                                        + "controllers on other prefabs are not read)")
                                    + " — a controller a framework merge component mounts is not read, unless its "
                                    + "own asset path happens to carry _FX and this scan reached it"
                                  : "source=none (no FX controller resolved from " + data.PrefabCount
                                    + " prefabs — neither a descriptor FX slot nor an asset whose path carries "
                                    + "_FX. A controller "
                                    + "mounted by MA MergeAnimator or VRCFury FullController is invisible to this "
                                    + "probe and reads the same way; CheckAnimator resolves those mounts)");

            data.ToggleCaveat = ComposeToggleCaveat(data.NonSdk.Count, data.UnresolvedScripts, matched);
        }

        /// <summary>
        /// The <c>togglesCaveat</c> text. Any non-SDK framework triggers the named branch, not just an unfamiliar one,
        /// because Modular Avatar's own reactive object toggles compile to m_IsActive at build.
        /// </summary>
        internal static string ComposeToggleCaveat(int namedNamespaces, int unresolvedScripts, int matched)
        {
            const string ReadsOnly =
                "hasToggle is set only from clip m_IsActive curves in the FX controller named by `toggles` — false means "
              + "that detector found nothing, not that the mesh is not removable. ";

            string incomplete = unresolvedScripts > 0
                ? " On top of that, " + unresolvedScripts.ToString(CultureInfo.InvariantCulture)
                  + " components have unresolved scripts, so the namespace list is incomplete and there may be further "
                  + "frameworks it does not name."
                : "";

            if (namedNamespaces > 0)
                return ReadsOnly
                     + "This tool parses no framework, and frameworks routinely compile toggles from their own components "
                     + "to m_IsActive at build (Modular Avatar's reactive object toggles included), so with "
                     + namedNamespaces.ToString(CultureInfo.InvariantCulture) + " non-SDK namespaces present matched="
                     + matched.ToString(CultureInfo.InvariantCulture) + " carries no removability information: read the "
                     + "vendor's own toggle/menu components on the prefab before fixing a keep-set." + incomplete;

            if (unresolvedScripts > 0)
                return ReadsOnly
                     + "Whether anything here compiles toggles at build is UNKNOWN, not absent: no non-SDK namespace was "
                     + "named, but " + unresolvedScripts.ToString(CultureInfo.InvariantCulture)
                     + " components have unresolved scripts and an unresolved component cannot be classified. Install the "
                     + "missing package (CheckPackage names the offenders) and re-run before reading matched="
                     + matched.ToString(CultureInfo.InvariantCulture) + " as evidence of anything.";

            return ReadsOnly
                 + "No non-SDK framework components were found on these prefabs and every component's script resolved, so "
                 + "nothing here compiles toggles at build; the reach limit stated in `toggles` still applies.";
        }

        /// <summary>
        /// Resolves the FX controller: the first prefab in path order whose descriptor FX slot holds one
        /// (<paramref name="fxPrefabPath"/> names it), else the first controller whose asset path contains "_FX"
        /// (<paramref name="byPathMatch"/> true). Null when neither answers.
        /// </summary>
        private static AnimatorController FindFxController(string vendorFolder, List<string> prefabPaths, GraphData data,
                                                           out string fxPath, out bool byPathMatch,
                                                           out string fxPrefabPath)
        {
            fxPath = null;
            byPathMatch = false;
            fxPrefabPath = null;

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
                    // The AnimatorController reference is a persistent asset and survives the unload.
                    PrefabUtility.UnloadPrefabContents(root);
                }

                if (found != null) { fxPath = foundPath; fxPrefabPath = path; return found; }
            }

            // Strategy 2: "_FX" anywhere in the ASSET PATH, not the filename, so a folder named *_FX qualifies every
            // controller under it.
            var byName = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:AnimatorController", new[] { vendorFolder }))
            {
                var ap = AssetDatabase.GUIDToAssetPath(guid);
                if (ap.IndexOf("_FX", StringComparison.OrdinalIgnoreCase) >= 0) byName.Add(ap);
            }
            byName.Sort(StringComparer.Ordinal);
            foreach (var ap in byName)
            {
                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ap);
                if (ctrl != null) { fxPath = ap; byPathMatch = true; return ctrl; }
            }

            return null;
        }

        /// <summary>
        /// An AnimationClip binding path is the transform path from the avatar root: a bare name ("Body") or a full path
        /// ("Armature/Body").
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
            bool descriptorRoute = data.VisemeBasis == "descriptor";
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
            sb.Append("  \"visemeMesh\": ").Append(TransplantCore.Q(data.VisemeMesh)).Append(",\n");
            sb.Append("  \"visemeMeshBasis\": ").Append(TransplantCore.Q(data.VisemeBasis)).Append(",\n");
            sb.Append("  \"avatarFbx\": ").Append(TransplantCore.Q(data.AvatarFbxPath)).Append(",\n");
            sb.Append("  \"visemeDescriptors\": ").Append(data.VisemeDescriptors).Append(",\n");
            sb.Append("  \"visemeDistinctAssets\": ").Append(data.VisemeDistinctAssets).Append(",\n");
            sb.Append("  \"visemeJoinedFbxCount\": ").Append(data.VisemeJoinedFbxCount).Append(",\n");
            sb.Append("  \"visemeMeshNote\": ").Append(TransplantCore.Q(VisemeNote)).Append(",\n");
            sb.Append("  \"bodyGuess\": ").Append(TransplantCore.Q(data.BodyMesh)).Append(",\n");
            sb.Append("  \"bodyGuessHeuristic\": ")
              .Append(data.BodyMesh != null ? TransplantCore.Q(BodyGuessNote) : "null")
              .Append(",\n");
            sb.Append("  \"fxController\": ").Append(TransplantCore.Q(data.FxControllerPath)).Append(",\n");
            sb.Append("  \"toggles\": ").Append(TransplantCore.Q(data.ToggleStatus)).Append(",\n");
            sb.Append("  \"togglesCaveat\": ").Append(TransplantCore.Q(data.ToggleCaveat)).Append(",\n");
            sb.Append("  \"constraints\": ").Append(data.Constraints).Append(",\n");
            sb.Append("  \"nonSdkNamespaces\": [");
            var census = RankNonSdk(data.NonSdk);
            for (int i = 0; i < census.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"namespace\": ").Append(TransplantCore.Q(census[i].Key))
                  .Append(", \"components\": ").Append(census[i].Value).Append(" }");
            }
            sb.Append(census.Count > 0 ? "\n  ],\n" : "],\n");
            sb.Append("  \"nonSdkNamespacesNote\": ").Append(TransplantCore.Q(NonSdkNote)).Append(",\n");
            sb.Append("  \"unresolvedScripts\": ").Append(data.UnresolvedScripts).Append(",\n");
            sb.Append("  \"unresolvedScriptsNote\": ").Append(TransplantCore.Q(UnresolvedScriptsNote)).Append(",\n");
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
                      .Append(descriptorRoute ? ", \"isVisemeMesh\": " + (r.IsVisemeMesh ? "true" : "false") : "")
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
            public int    UnresolvedScripts;
            public readonly Dictionary<string, int> NonSdk = new Dictionary<string, int>(StringComparer.Ordinal);
            public string FxControllerPath;
            public string ToggleStatus;
            public string ToggleSummary;
            public string ToggleCaveat;
            public string SupersetFbx;

            // ── Viseme (face) mesh ──
            /// <summary>Every face mesh a descriptor declared, in prefab path order, deduped. Persistent asset instances,
            /// so they outlive the unloaded prefabs and serve as the join key.</summary>
            public readonly List<Mesh> VisemeDeclared = new List<Mesh>();
            /// <summary>The declared mesh that actually joined this inventory; null when none did.</summary>
            public Mesh   VisemeSharedMesh;
            /// <summary>Reported viseme/face mesh name, whatever the basis.</summary>
            public string VisemeMesh;
            /// <summary>How <see cref="VisemeMesh"/> was arrived at: <c>descriptor</c> or
            /// <c>guess:most-blendshapes</c>.</summary>
            public string VisemeBasis;
            /// <summary>Asset path of the FBX carrying the declared viseme mesh; null when the descriptor route did not
            /// answer.</summary>
            public string AvatarFbxPath;
            /// <summary>Descriptors whose lipSync mode declares a face mesh.</summary>
            public int    VisemeDescriptors;
            /// <summary>Distinct face-mesh assets declared; <see cref="VisemeNote"/> gives the reading. RunLog only.</summary>
            public int    VisemeDistinctAssets;
            /// <summary>Distinct FBXes in this inventory that carry a declared mesh; the count that means ambiguity, because
            /// names miss a renamed prefab renderer and assets over-fire on ordinary variants. Surfaced when &gt;1.</summary>
            public int    VisemeJoinedFbxCount;

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
            public Mesh   Mesh;
            public int    VertexCount;
            public int    SubMeshCount;
            public int    BlendShapeCount;
            /// <summary>True only where the descriptor's viseme mesh is this renderer's mesh, by identity. The RunLog omits
            /// the flag on the guess route.</summary>
            public bool   IsVisemeMesh;
            public bool   LikelyBody;
            public bool   HasToggle;
        }
    }
}
