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

                // 5. Viseme (face) mesh — the descriptor's declaration where there is one, a labelled
                //    most-blendshapes guess where there is not — plus the body pick, which stays a heuristic.
                ResolveVisemeAndBody(data);

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
                "[ReportPackage] {0}: fbx={1} prefab={2} constraints={3} nonSdkNs={4} toggles={5} visemeMesh={6} bodyGuess={7} superset={8}{9}{10}{11}{12}{13} => {14} | log={15}",
                label, data.FbxEntries.Count, data.PrefabCount, data.Constraints,
                NonSdkSummary(data.NonSdk), data.ToggleSummary ?? "?",
                VisemeField(data), data.BodyMesh ?? "?",
                data.SupersetFbx ?? "none",
                // The avatar FBX rides the SUMMARY, not just the RunLog: it is the handle the next step takes,
                // and a result carries the handles the next step needs (docs/tool-design.md §Tools).
                data.AvatarFbxPath != null ? " avatarFbx=" + TransplantCore.Leaf(data.AvatarFbxPath) : "",
                // A fact, not an alarm: several FBXes here carry a declared face mesh, so this package holds
                // more than one candidate avatar and avatarFbx names the first in path order. Ordinary for a
                // PC/Android pair or a kisekae variant (4 of 14 local vendor packages) — and the same reading
                // is what catches two sibling avatars in a family package, which is not ordinary at all.
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
                // The Mesh instance is kept for the viseme join in ResolveVisemeMesh: the descriptor names its
                // face renderer on a PREFAB, whose GameObject name is free to differ from the FBX transform's,
                // so joining the two by name would reintroduce exactly the guess this field exists to replace.
                // Both sides load the same Mesh asset instance, which is what makes the join an identity test.
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

        /// <summary>
        /// The <c>visemeMesh=</c> field: the mesh name with its basis in parens, one key for one concept on
        /// both routes. Emitting a different key per route (a <c>visemeMesh=</c> here, a <c>headGuess=</c>
        /// there) would make every reader parse two names for one thing. Shape matches <c>toggles=</c>, which
        /// already carries value-then-mechanism the same way.
        /// </summary>
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
        /// whatever face mesh was selected before the switch. Reporting that as a fact is precisely the
        /// failure this door exists to remove, so the mode gates the read and everything else degrades.
        /// </summary>
        internal static bool DeclaresFaceMesh(VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle style)
        {
            return style == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape
                || style == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape;
        }

        /// <summary>
        /// Collects a descriptor's declared face mesh. Every one is kept, in path order, deduped — the pick
        /// happens later in <see cref="ResolveVisemeAndBody"/>, against the FBX inventory, because that is
        /// where it can be made against something. Keeping only the first would settle it here on prefab
        /// sort order alone, and a family package's outfit prefab sorts before the avatar's.
        /// </summary>
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
        /// Resolves the face mesh and the body pick, and names which of the two routes answered.
        ///
        /// <para>DESCRIPTOR route (a fact): the avatar's own <c>VRCAvatarDescriptor</c> declares its viseme
        /// mesh, so the mesh — and with it WHICH FBX is the avatar's — is read rather than inferred. The join
        /// back to the FBX inventory is by Mesh asset identity, never by name.</para>
        ///
        /// <para>GUESS route (labelled): where no descriptor answered, or the mesh it named is not in this
        /// package's FBX inventory, fall back to the most-blendshapes reading and SAY SO in the field. The
        /// out-of-inventory case is ordinary rather than exotic — an outfit or hair package's descriptor
        /// routinely points at a base-body FBX in another package folder, while the FBX scan only reaches
        /// <c>t:Model</c> under this one.</para>
        ///
        /// <para>The body pick is a heuristic on BOTH routes and keeps its hedge: nothing in the substrate
        /// declares "the body mesh". What the descriptor buys it is the right reference FBX and a known mesh
        /// to exclude. Its standing limit is structural, not the rare tie — it names the next-most-blendshapes
        /// renderer, so on a base whose single body mesh also carries the visemes it necessarily names
        /// something else, sometimes a prop.</para>
        /// </summary>
        /// <summary>
        /// Which key the body pick excludes the face mesh on, given the route that answered. Identity only
        /// where a descriptor's mesh actually joined the inventory; by name otherwise — including when a
        /// descriptor DID declare a mesh that this package does not contain, where an identity test would
        /// match no renderer and so exclude nothing.
        /// </summary>
        internal static bool ExcludesFaceByIdentity(string visemeBasis)
        {
            return visemeBasis == "descriptor";
        }

        /// <summary>True when any renderer in <paramref name="e"/> carries one of the declared face meshes.</summary>
        private static bool CarriesADeclaredMesh(FbxEntry e, List<Mesh> declared)
        {
            foreach (var ri in e.Renderers)
                if (ri.Mesh != null && declared.Contains(ri.Mesh)) return true;
            return false;
        }

        private static void ResolveVisemeAndBody(GraphData data)
        {
            FbxEntry source = null;

            // ---- Descriptor route: identity-join the declared meshes into the FBX inventory ----
            // Every declared mesh is tried, in path order, and the FIRST THAT JOINS wins. Trying only the
            // first declared would hand the answer to prefab sort order: an outfit prefab sorting ahead of
            // the avatar's names a base body in another package, fails to join, and the run degrades to a
            // guess while the avatar's own descriptor sat unread two prefabs later.
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

            // How many DISTINCT FBXes any declared mesh joins — the ambiguity that actually decides work.
            // Two sibling avatars in one family package each declare their own `Body`: same name, different
            // asset, different FBX. Keyed on names that reads as agreement, keyed on assets it reads as an
            // ordinary variant, and either way the run would name one sibling's FBX while the other is being
            // owned. `own-base` graphs such families whole, so this must not pass silently.
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
                // Reached both when no descriptor declared anything AND when one did but named a mesh this
                // package does not contain (ordinary for an outfit or hair package pointing at a base body).
                foreach (var e in data.FbxEntries) if (e.IsSuperset) { source = e; break; }
                if (source == null && data.FbxEntries.Count > 0) source = data.FbxEntries[0];
                if (source == null) return;

                RendererInfo top = null;
                foreach (var ri in source.Renderers)
                    if (top == null || ri.BlendShapeCount > top.BlendShapeCount) top = ri;

                // Honesty guard: every renderer had a null sharedMesh (blendShapeCount < 0) ⇒ name nothing.
                if (top == null || top.BlendShapeCount < 0) return;

                data.VisemeMesh  = top.Name;
                data.VisemeBasis = "guess:most-blendshapes";
                // IsVisemeMesh deliberately stays false on this route — a guess does not get to wear a
                // fact's name on a per-renderer flag.
            }

            // ---- Body pick: most blend shapes on the reference FBX that is not the face mesh ----
            // Keyed on the route that ANSWERED, not on whether a descriptor exists. Keying on
            // VisemeSharedMesh's nullity excluded nothing in the case where a descriptor declared a mesh that
            // did not join: the identity test matched no renderer (that is why the join failed) while the
            // name fallback stayed switched off, so the top renderer became bodyGuess as well as visemeMesh
            // and the report named one mesh twice.
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
                    // Viseme resolution rides THIS walk, not FindFxController's: that one returns at the first
                    // prefab yielding an FX layer, so it is not a full walk, and reusing it would mean deleting
                    // that early return and paying LoadPrefabContents N times over. This walk already visits
                    // every prefab in path order, and runs before the viseme/body resolve. Every descriptor is
                    // read (not the first), so a package that disagrees with itself is counted rather than
                    // silently resolved first-wins.
                    foreach (var desc in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                        NoteVisemeMesh(data, desc);

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
            AnimatorController fx = FindFxController(vendorFolder, prefabPaths, data, out fxPath,
                                                     out bool byPathMatch, out string fxPrefabPath);
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
                                  // Name the prefab, never an ordinal, and say "path" rather than "name
                                  // convention" — see FindFxController for why both were false.
                                  ? "source=" + fxPath + (byPathMatch
                                      ? " (asset path carries _FX under " + vendorFolder
                                        + ", first in path order; no prefab of " + data.PrefabCount
                                        + " resolved a descriptor FX slot)"
                                      : " (descriptor FX slot on " + fxPrefabPath + " — the first of "
                                        + data.PrefabCount + " prefabs in path order that resolved one; "
                                        + "controllers on other prefabs are not read)")
                                    + " — a controller a framework merge component mounts is not read, unless its "
                                    + "own asset path happens to carry _FX and this scan reached it"
                                  // The not-found branch has to name the merge-mount blind spot too, and this is
                                  // where naming it matters most: a package whose FX arrives entirely through MA
                                  // MergeAnimator or VRCFury FullController has no descriptor slot and no
                                  // *_FX-named asset, so it lands here reading as "this package has no FX" when it
                                  // has one this probe cannot see. CheckAnimator resolves those mount points.
                                  : "source=none (no FX controller resolved from " + data.PrefabCount
                                    + " prefabs — neither a descriptor FX slot nor an asset whose path carries "
                                    + "_FX. A controller "
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
        /// 2. Scan t:AnimatorController under vendorFolder for one whose ASSET PATH contains "_FX".
        /// Returns null (and sets fxPath to null) if neither succeeds — caller degrades gracefully.
        /// <para><paramref name="fxPrefabPath"/> is the prefab Strategy 1 answered from — the first in path
        /// order that RESOLVED a controller, which is not necessarily the first prefab. Null when Strategy 2
        /// answered. The status line names it rather than an ordinal, because "the first of N prefabs" is
        /// false the moment an earlier prefab carries no descriptor, an isDefault layer, or fails to load,
        /// and it is the one field a reader uses to decide which prefab to open.</para>
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
                    // Always unload; the AnimatorController reference is a persistent asset and survives.
                    PrefabUtility.UnloadPrefabContents(root);
                }

                if (found != null) { fxPath = foundPath; fxPrefabPath = path; return found; }
            }

            // Strategy 2: scan for "_FX" anywhere in the ASSET PATH — not the filename, which is what a
            // "*_FX name convention" would mean. A folder named *_FX therefore qualifies every controller
            // under it, `Base.controller` included. Kept as-is (narrowing it to the filename would stop
            // resolving packages this currently finds); the status line and unity-tools.md say "path" so the
            // reader knows which of the two they got.
            //
            // Sorted for the same reason the prefab list is: FindAssets' order is unspecified, and this also
            // takes the first hit, so a package with two matching controllers would otherwise report a
            // machine-dependent pick.
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
                      // Emitted only where a descriptor settled it. On the guess route the flag is OMITTED
                      // rather than written false: a definite false is a claim, and the tool has established
                      // nothing about which renderer is the face — including on the one it just named.
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

            // ── Viseme (face) mesh: a FACT when a descriptor declares it, a labelled guess otherwise ──
            /// <summary>Every face mesh a descriptor declared, in prefab path order, deduped. A LIST, not a
            /// single winner: the first declared mesh need not be one this package's FBX inventory contains —
            /// a family package's outfit prefab sorts before the avatar's and names a base body elsewhere —
            /// and keeping only the first would discard a fact the package supplied and degrade to a guess.
            /// Asset instances, because identity is the join key; they outlive the prefabs they were read
            /// from, being persistent assets, exactly as the FX controller does.</summary>
            public readonly List<Mesh> VisemeDeclared = new List<Mesh>();
            /// <summary>The declared mesh that actually joined this inventory; null when none did.</summary>
            public Mesh   VisemeSharedMesh;
            /// <summary>Reported viseme/face mesh name, whatever the basis.</summary>
            public string VisemeMesh;
            /// <summary>How <see cref="VisemeMesh"/> was arrived at — <c>descriptor</c> or
            /// <c>guess:most-blendshapes</c>. Rides the field in parens so one key never means two things,
            /// and is what the body pick keys its face-exclusion on.</summary>
            public string VisemeBasis;
            /// <summary>Asset path of the FBX carrying the viseme mesh — the reference FBX for the body pick,
            /// and the handle the next step needs. Null when the descriptor route did not answer.</summary>
            public string AvatarFbxPath;
            /// <summary>Descriptors whose lipSync mode declares a face mesh.</summary>
            public int    VisemeDescriptors;
            /// <summary>Distinct face-mesh ASSETS declared. Routinely &gt;1 without anything being wrong: a
            /// package shipping several body FBX variants gives each its own <c>Body</c> mesh asset, so
            /// identity differs while the answer does not. RunLog only.</summary>
            public int    VisemeDistinctAssets;
            /// <summary>How many DISTINCT FBXes in this inventory the declared meshes join. This is the count
            /// that means ambiguity, and it is deliberately neither of the other two: names are the wrong key
            /// (a renamed prefab renderer differs from its FBX transform, which is why the join is by identity
            /// at all), and assets over-fire on ordinary variants. What decides work is whether the package
            /// settles WHICH FBX is the avatar's — two sibling avatars in one family package each declaring
            /// their own <c>Body</c> is the case that must not pass silently. Surfaced when &gt;1.</summary>
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
            /// <summary>True only where the descriptor's own viseme mesh IS this renderer's mesh (identity).
            /// Emitted only on the descriptor route — on the degraded route the flag is omitted entirely
            /// rather than shipped as a guess wearing a fact's name.</summary>
            public bool   IsVisemeMesh;
            public bool   LikelyBody;
            public bool   HasToggle;
        }
    }
}
