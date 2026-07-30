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

                // 2. Prefab scan: constraint count + the non-SDK component census
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
                "[ReportPackage] {0}: fbx={1} prefab={2} constraints={3} nonSdkNs={4} toggles={5} headGuess={6} bodyGuess={7} superset={8}{9}{10}{11} => {12} | log={13}",
                label, data.FbxEntries.Count, data.PrefabCount, data.Constraints,
                NonSdkSummary(data.NonSdk), data.ToggleSummary ?? "?",
                data.HeadMesh ?? "?", data.BodyMesh ?? "?",
                data.SupersetFbx ?? "none",
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

        // ── Prefab scan: constraints + the non-SDK component census ───────────────────────────

        /// <summary>
        /// Namespace roots that are Unity or VRChat-SDK infrastructure — a cheap first filter that settles
        /// the overwhelming majority (Transform, the renderers) without touching reflection.
        /// <see cref="IsFromSdkAssembly"/> is the authority for everything this leaves, because a namespace
        /// root is neither necessary (the SDK ships global-namespace components, which no namespace rule can
        /// reach) nor sufficient (a vendor is free to author under any root it likes). VRCSDK2 is the legacy
        /// SDK.
        ///
        /// <para>Read the ordering precisely, because it bounds "authority": a root listed here excludes
        /// regardless of assembly, so for these six names the assembly is never consulted and a vendor
        /// component authored under <c>VRC.*</c> or <c>Unity.*</c> would vanish from the census. That is a
        /// squatting bet, not a derivation. No installed package takes it, and consulting the assembly first
        /// always would pay reflection on every component to defend a case we have never seen.</para>
        /// </summary>
        private static readonly HashSet<string> SdkNamespaceRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityEngine", "UnityEditor", "Unity", "TMPro", "VRC", "VRCSDK2",
        };

        /// <summary>
        /// Assembly-name prefixes for the engine, the .NET base library, and the VRChat SDK — including the
        /// plugin assemblies the SDK vendors (UniTask under <c>Cysharp.*</c>, DOTween under <c>DG.*</c>),
        /// which are SDK infrastructure rather than anything a vendor composed. The assembly is what
        /// actually settles "ships inside the SDK": the SDK's Oculus spatializer component is a
        /// global-namespace type compiled into the SDK's own runtime assembly, so no namespace rule could
        /// exclude it — while excluding the <c>DG</c>/<c>Cysharp</c>/<c>System</c> namespace roots outright
        /// would have hidden a project's own types that merely share those roots.
        ///
        /// <para>NO ENTRY MAY SWALLOW A LONGER NON-SDK ASSEMBLY NAME. Matching is plain prefix, and a
        /// wrongly-excluded framework does not merely go unlisted — it leaves
        /// <see cref="ComposeToggleCaveat"/> asserting that nothing here compiles toggles at build.
        /// <c>VRCFury</c> is the live collision, and the reason <c>VRC</c> is split into <c>VRC.</c> /
        /// <c>VRCSDK</c> / <c>VRCCore</c>: VRCFury's runtime assembly is named exactly that, so a bare
        /// <c>VRC</c> reaches it. Narrowing further is safe to attempt —
        /// <c>IsSdkAssemblyName_still_reaches_every_real_sdk_assembly</c> enumerates the SDK side, so a cut
        /// that hides an SDK assembly fails there rather than in a report. The residual bound: an entry can
        /// still swallow a name extending it with a letter (a hypothetical <c>UnityEngineExtras</c>). No
        /// installed package does, and a uniform letter-or-digit boundary rule is not available — it would
        /// reject <c>VRCSDK3A</c>.</para>
        /// </summary>
        private static readonly string[] SdkAssemblyPrefixes =
        {
            "UnityEngine", "UnityEditor", "Unity.", "mscorlib", "netstandard", "System",
            "VRC.", "VRCSDK", "VRCCore", "SDKBase", "UniTask", "DOTween",
        };

        /// <summary>
        /// True when <paramref name="asmName"/> names engine, base-library, or VRChat-SDK code. Split from
        /// <see cref="IsFromSdkAssembly"/> so the rule above is assertable on strings, which is the only way
        /// to reach it: the collision is a property of assembly NAMES, and VRCFury's components are
        /// <c>internal</c> in a non-auto-referenced assembly, so no <c>typeof()</c> in the test assembly can
        /// name one.
        /// </summary>
        internal static bool IsSdkAssemblyName(string asmName)
        {
            if (string.IsNullOrEmpty(asmName)) return false;
            foreach (var prefix in SdkAssemblyPrefixes)
                if (asmName.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>True when <paramref name="t"/>'s assembly is engine, base-library, or VRChat-SDK code.</summary>
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

        private const string UnresolvedScriptsNote =
            "Components whose script did not resolve, counted rather than skipped: an unresolvable component is by "
          + "definition a framework this census cannot name. Any nonzero count means nonSdkNamespaces is incomplete and "
          + "framework presence is unknown rather than absent — install the missing package (CheckPackage names the "
          + "offenders) and re-run before trusting this census or togglesCaveat.";

        /// <summary>
        /// The census key <paramref name="t"/> contributes, or null when it is Unity/VRChat-SDK
        /// infrastructure. Generic by construction: <see cref="Type.Namespace"/> verbatim, no framework
        /// recognized by name. The predecessor matched a fixed three-framework list and so reported
        /// "ModularAvatar" on an avatar carrying two more; per-framework support has no end.
        /// Namespaces are not collapsed to a shared prefix — no segment count suits all of them (three
        /// folds NDMF's eight component namespaces correctly and splits VRCFury's). Global-namespace
        /// types key on their type name, theirs carrying nothing to report.
        /// </summary>
        internal static string NonSdkKey(Type t)
        {
            if (t == null) return null;
            var ns = t.Namespace;
            int dot = ns == null ? -1 : ns.IndexOf('.');
            // Namespace root first — see SdkNamespaceRoots for what that ordering concedes.
            if (ns != null && SdkNamespaceRoots.Contains(dot < 0 ? ns : ns.Substring(0, dot))) return null;
            if (IsFromSdkAssembly(t)) return null;
            return ns ?? t.Name;
        }

        /// <summary>Census entries, most components first then name — one deterministic order for summary and RunLog.</summary>
        internal static List<KeyValuePair<string, int>> RankNonSdk(IDictionary<string, int> census)
        {
            var ranked = new List<KeyValuePair<string, int>>(census);
            ranked.Sort((a, b) => b.Value != a.Value
                ? b.Value.CompareTo(a.Value)
                : string.Compare(a.Key, b.Key, StringComparison.Ordinal));
            return ranked;
        }

        /// <summary>
        /// The <c>nonSdkNs=</c> field: census size, then the three most-present namespaces so the one-liner
        /// names offenders without unbounding — a composed avatar routinely carries ten. Full list in the RunLog.
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
            // FindAssets' order is unspecified (GUID-derived in practice), and FindFxController takes the FIRST
            // prefab that yields an FX layer — so on a multi-prefab package WHICH controller got reported could
            // differ between machines or across a reimport, with nothing in the output saying so. Sorting does
            // not make the pick right; it makes it reproducible, which is the part a reader can rely on.
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
                    foreach (var comp in root.GetComponentsInChildren<Component>(true))
                    {
                        // A missing script slot is counted, not skipped: it is exactly the case where a
                        // framework is present and unnameable. Skipped, a package with unresolved scripts
                        // reads identically to the same package fully resolved.
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

                        // Constraint detection is a NAME-SUBSTRING rule, not a type test: Unity's built-ins and
                        // VRChat's own both carry "Constraint" in the name, and matching the name is what lets
                        // this count them without referencing either. The cost is that it counts anything else
                        // named that way too — a vendor's ConstraintHelper, a ConstraintSettings data class —
                        // so the figure is an upper bound on real constraint components, not a census. Read it
                        // as "constraint-ish components present", and reach for ReportGimmick when the exact
                        // topology matters. Stated in unity-tools.md's row for the same reason.
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
        /// Sets per-renderer <c>HasToggle</c> and the three toggle fields. The status field names its own
        /// detector, its yield, and its reach in every case: a bare "ok" beside all-false readings is
        /// indistinguishable from "this package has no toggles", and a keep-set decided on that reading is
        /// decided on nothing. The reach limits it states are deliberately not fixed here — unioning every
        /// descriptor's FX, and resolving controllers a framework merge component mounts, is a wider tool
        /// than this digest. Naming what was not read is the honest floor.
        /// </summary>
        private static void BuildToggles(string vendorFolder, List<string> prefabPaths, GraphData data)
        {
            int rendererCount = 0;
            foreach (var entry in data.FbxEntries) rendererCount += entry.Renderers.Count;

            string fxPath;
            AnimatorController fx = FindFxController(vendorFolder, prefabPaths, data, out fxPath);
            data.FxControllerPath = fxPath;

            int matched = 0;
            if (fx != null)
            {
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
                    {
                        ri.HasToggle = MatchesTogglePath(togglePaths, ri.Name);
                        if (ri.HasToggle) matched++;
                    }
            }

            string matchRatio = matched.ToString(CultureInfo.InvariantCulture) + "/"
                              + rendererCount.ToString(CultureInfo.InvariantCulture);

            // Unresolved scripts are framework presence we cannot name, so they caveat the reading exactly as
            // a named framework does. Gating on the census alone would print "no frameworks found" at the one
            // moment that claim is least safe — a project missing a package.
            bool frameworks = data.NonSdk.Count > 0 || data.UnresolvedScripts > 0;

            data.ToggleSummary = (fx != null ? matchRatio : "fx-controller-not-found")
                               + (frameworks ? "(clip-m_IsActive, caveated)" : "(clip-m_IsActive)");

            data.ToggleStatus = "clip-m_IsActive; matched=" + matchRatio + " renderer entries across all FBXes; "
                              + (fx != null
                                  ? "source=" + fxPath + " (first FX controller found among " + data.PrefabCount
                                    + " prefabs in path order; controllers on other prefabs, and controllers a "
                                    + "framework merge component mounts, are not read)"
                                  // The not-found branch has to name the merge-mount blind spot too, and this is
                                  // where naming it matters most: a package whose FX arrives entirely through MA
                                  // MergeAnimator or VRCFury FullController has no descriptor slot and no
                                  // *_FX-named asset, so it lands here reading as "this package has no FX" when it
                                  // has one this probe cannot see. CheckAnimator resolves those mount points.
                                  : "source=none (no FX controller resolved from " + data.PrefabCount
                                    + " prefabs — neither a descriptor FX slot nor a *_FX-named asset. A controller "
                                    + "mounted by MA MergeAnimator or VRCFury FullController is invisible to this "
                                    + "probe and reads the same way; CheckAnimator resolves those mounts)");

            data.ToggleCaveat = ComposeToggleCaveat(data.NonSdk.Count, data.UnresolvedScripts, matched);
        }

        /// <summary>
        /// The <c>togglesCaveat</c> text. Three states, because "no framework named" and "no framework
        /// present" are different claims: unresolved scripts make presence unknown, and reporting it as
        /// absent is the same false-absence the fixed detection list produced.
        /// The named-framework branch triggers on ANY non-SDK framework rather than an unfamiliar one —
        /// Modular Avatar's own reactive object toggles compile to m_IsActive at build too, so gating on
        /// unfamiliarity would stay silent on the framework we are most likely to meet.
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
            public int    UnresolvedScripts;
            public readonly Dictionary<string, int> NonSdk = new Dictionary<string, int>(StringComparer.Ordinal);
            public string FxControllerPath;
            public string ToggleStatus;
            public string ToggleSummary;
            public string ToggleCaveat;
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
