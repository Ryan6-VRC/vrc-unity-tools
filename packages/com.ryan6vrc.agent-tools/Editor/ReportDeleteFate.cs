using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// What every declared geometry removal on a composed avatar actually costs, measured on a fresh build.
    ///
    /// <para>The rule it exists to enforce, from Modular Avatar source (<c>ReactiveObjectPass.ProcessMeshDeletion</c>,
    /// <c>ReactionRule.IsConstantActive</c>, <c>ReactiveObjectAnalyzer.AnalyzeConstants</c>): a <c>ShapeChanger</c>
    /// Delete row removes triangles at build ONLY when its last action group is constant AND initially active.
    /// A non-constant one is turned into <b>NaNimation</b> instead — MA inserts per-bone objects whose
    /// <c>m_LocalScale</c> is driven to <c>NaN</c>, so the vertices stay in the mesh and stay in the triangle
    /// count. A condition is non-constant when a menu item gates the row, or when any clip anywhere in the
    /// avatar binds an ancestor's <c>m_IsActive</c>. So a garment's anti-clip Delete is authored once and its
    /// fate is decided by whatever ELSE on the avatar happens to toggle its host — which is why this cannot be
    /// answered by reading the authoring, and why <see cref="ReportShapeOverlap"/> (edit-time, one mesh) says
    /// so explicitly rather than guessing.</para>
    ///
    /// <para><b>Presence is not visibility, and this door refuses to count it.</b> NaNimation retains every
    /// vertex and every triangle, so "the shape is gone from the built mesh" is the misread; the number that
    /// answers the question is the built triangle count over the active, enabled renderers, which is what
    /// <c>triangleBudget</c> is measured against.</para>
    ///
    /// <para>It bakes through <see cref="AvatarBake"/> and inherits that contract exactly: a blocked build
    /// names the failed stage and publishes NO fate table — never an authored-only fallback, because authored
    /// rows under a heading promising composed truth is the misread the whole surface exists to prevent. The
    /// bake outruns the MCP transport, so the door is two-phase like <see cref="ReportComposition"/>'s bake
    /// mode: <see cref="Run"/> writes its artifact path first and returns <c>PENDING</c>,
    /// <see cref="Verify"/> re-reads it.</para>
    ///
    /// INSPECTION ONLY — it reads the authoring, builds and destroys its own clone, and writes nothing but its
    /// own Snapshot artifact.
    /// </summary>
    [AgentTool]
    public static class ReportDeleteFate
    {
        // ── The MA names this door joins on. Quoted once, here, because every one of them is a VERBATIM
        //    string MA builds and this door parses; a paraphrase reads plausible and matches nothing.
        //    (NaNimationFilter.NaNimatedBonePrefix / NaNimatedBufferPrefix, ReactiveObjectAnalyzer's
        //    prefixes, ReadablePropertyExtension.GetActiveSelfProxy.)

        /// <summary>MA names a NaNimated bone <c>"NaNimatedBone for " + TargetProp.ToString().Replace('/','_')</c>,
        /// and <c>TargetProp.ToString()</c> is <c>$"{TargetObject}.{PropertyName}"</c> where the target object is a
        /// <c>SkinnedMeshRenderer</c> — so the object's own name literally spells the key this door needs.</summary>
        internal const string NaNimatedBonePrefix = "NaNimatedBone for ";
        internal const string DeletedShapePrefix = "deletedShape.";
        internal const string DeletedMaskPrefix = "deletedMeshByMask.";
        /// <summary>Unity renders a component reference as <c>"&lt;name&gt; (&lt;Type.FullName&gt;)"</c>, which is
        /// what lands in the bone name. Pinned rather than composed from <c>typeof</c> so the emitted key and the
        /// parsed key are provably the same literal.</summary>
        internal const string RendererSuffix = " (UnityEngine.SkinnedMeshRenderer)";
        /// <summary>The controlling parameter MA mints for an ancestor's <c>activeSelf</c>. The trailing
        /// <c>##&lt;n&gt;</c> disambiguator is part of the name — a match written without it finds nothing.</summary>
        internal const string ActiveSelfProxyPrefix = "__MA/ActiveSelfProxy/";

        // ── Fates and mechanisms. Two closed vocabularies, spelled once. ──────────────────────────────────
        internal const string FateBuildTime = "build-time";
        internal const string FateNaNimation = "NaNimation";
        internal const string FateDropped = "dropped";
        internal const string FateCancelled = "cancelled-by-Set";
        internal const string FateUnresolved = "unresolved";

        internal const string MechMenuItem = "menu-item";
        internal const string MechAncestorAnimated = "ancestor-animated";
        internal const string MechHostInactive = "host-inactive";
        internal const string MechSetCancels = "set-cancels";

        // ── Census: what the authoring declares, read BEFORE the bake ─────────────────────────────────────

        /// <summary>One declared removal. <c>Kind</c> is <c>shape</c> (a ShapeChanger row in Delete mode) or
        /// <c>cutter</c> (a MeshCutter), and the two differ in more than shape: MA passes the cutter's renderer
        /// as the rule's affected object, so ancestors shared with the renderer contribute NO condition — a
        /// cutter sitting on the mesh it cuts is unconditional by construction, while a ShapeChanger's every
        /// ancestor counts. <c>AffectedObjectSkip</c> records that asymmetry per row so the reason a cutter is
        /// unconditional is legible from the table rather than folded into a fate.</summary>
        internal sealed class DeclaredRow
        {
            public string Kind;
            public string HostPath;            // the MA component's own GameObject
            public string Shape;               // ShapeChanger rows only
            public string RendererName;        // the join key MA spells into the NaNimated bone's name
            public string RendererPath;
            public bool Inverted;
            public string FilterSummary;       // cutter: its vertex filters, in the order MA reads them
            public string MultiMode;           // cutter
            public string[] FilterTokens;      // cutter: the leading token(s) each filter's ToString() emits
            public string MenuItemAncestor;    // nearest MA MenuItem at or above the host, or null
            public string ParkedAncestor;      // nearest ancestor with activeSelf false, or null
            public string LaterSetHost;        // a Set on this exact (renderer, shape) declared later, or null
            public bool AffectedObjectSkip;    // cutter rows: ancestors shared with the renderer are skipped
            public int AuthoredVerts;          // the target renderer's vertex count, for build-time corroboration

            /// <summary>The exact <c>TargetProp.ToString()</c> MA builds for a ShapeChanger row. Null for a
            /// cutter, whose key embeds the resolved selector's <c>ToString()</c> — a value that exists only
            /// inside the build, which is why cutter attribution is a prefix match plus a token check.</summary>
            public string TargetPropKey => Kind == "shape"
                ? RendererName + RendererSuffix + "." + DeletedShapePrefix + Shape
                : null;

            public string CutterKeyPrefix => RendererName + RendererSuffix + "." + DeletedMaskPrefix;

            public string Label => Kind == "shape"
                ? Shape + " → " + RendererName
                : "cutter(" + (FilterSummary ?? "?") + ") → " + RendererName;
        }

        internal sealed class Census
        {
            public readonly List<DeclaredRow> Rows = new List<DeclaredRow>();
            /// <summary>Set when MA is installed but its API did not resolve. A drifted census is silently
            /// EMPTY, which reads exactly like an avatar that declares no removals — the one failure this door
            /// must never have — so it refuses instead of publishing.</summary>
            public string Drift;
        }

        // ── Doors ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Report the built fate of every MA <c>ShapeChanger</c> Delete row and every
        /// <c>MeshCutter</c> under the in-scene avatar at <paramref name="avatarRoot"/> (hierarchy path, else
        /// instance id, else name), plus the built triangle count against <paramref name="triangleBudget"/>.
        /// <para>It BAKES — it creates and destroys a clone through the real SDK preprocess chain, so it costs
        /// what <c>ReportComposition.Run(…, bake:true)</c> costs and is two-phase for the same reason: this
        /// call returns <c>… =&gt; PENDING | log=&lt;path&gt;</c> with the artifact path already on disk, and
        /// <see cref="Verify"/> re-reads the verdict. A timed-out call has lost nothing and must not be
        /// re-issued.</para>
        /// Bad input (root not found, no descriptor, MA drift) is a bare trailer-less
        /// <c>[ReportDeleteFate] FAIL: …</c>.</summary>
        public static string Run(string avatarRoot, int triangleBudget = 70000)
        {
            var root = Resolve(avatarRoot);
            if (root == null)
                return Refuse("avatar root '" + avatarRoot + "' not found — tried hierarchy path, instance id, then name in the active scene");

            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                return Refuse("'" + avatarRoot + "' has no VRCAvatarDescriptor — Run expects the avatar (descriptor) root");

            if (triangleBudget < 0)
                return Refuse("triangleBudget must be >= 0 (got " + triangleBudget.ToString(CultureInfo.InvariantCulture) + ")");

            var census = TakeCensus(root);
            if (census.Drift != null) return Refuse(census.Drift);

            return DeleteFateBake.Begin(root, census, triangleBudget);
        }

        /// <summary>Re-read the verdict of a <see cref="Run"/> from its artifact. The bake outlives the MCP
        /// transport window, so the result is read back here rather than re-run.</summary>
        public static string Verify(string avatarRoot)
        {
            var root = Resolve(avatarRoot);
            if (root == null)
                return Refuse("avatar root '" + avatarRoot + "' not found — tried hierarchy path, instance id, then name in the active scene");
            return DeleteFateBake.Verify(root);
        }

        // ── Census implementation ─────────────────────────────────────────────────────────────────────────

        /// <summary>Every Delete row and every cutter under <paramref name="root"/>, in MA's own evaluation
        /// order (<c>GetComponentsInChildren</c> — hierarchy order, which is what "later declaration wins"
        /// means). By-name reflection throughout: this assembly references no MA assembly, and a member that
        /// stops resolving must refuse rather than return a confidently-empty list.</summary>
        internal static Census TakeCensus(GameObject root)
        {
            var census = new Census();

            var scType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarShapeChanger");
            var mcType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarMeshCutter");
            if (scType == null || mcType == null)
            {
                if (!VendorReflect.ModularAvatarInstalled()) return census; // MA absent: nothing declares anything
                census.Drift = "Modular Avatar is installed but ModularAvatarShapeChanger/ModularAvatarMeshCutter did "
                             + "not resolve (type renamed or moved). A census taken through drifted reflection is "
                             + "silently empty, which reads exactly like an avatar with no Delete rows, so no fate "
                             + "table is published.";
                return census;
            }

            var shapesProp = scType.GetProperty("Shapes", BindingFlags.Public | BindingFlags.Instance);
            var csType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ChangedShape");
            var objField = csType?.GetField("Object");
            var nameField = csType?.GetField("ShapeName");
            var ctField = csType?.GetField("ChangeType");
            var aorType = VendorReflect.FindType("nadena.dev.modular_avatar.core.AvatarObjectReference");
            var getMethod = aorType == null ? null : VendorReflect.ResolveAorGetOverload(aorType);
            var cutterObjProp = mcType.GetProperty("Object", BindingFlags.Public | BindingFlags.Instance);
            var multiModeProp = mcType.GetProperty("MultiMode", BindingFlags.Public | BindingFlags.Instance);
            if (shapesProp == null || objField == null || nameField == null || ctField == null
                || getMethod == null || cutterObjProp == null || multiModeProp == null)
            {
                census.Drift = "Modular Avatar reflection drift — a member (ShapeChanger.Shapes / "
                             + "ChangedShape.Object|ShapeName|ChangeType / MeshCutter.Object|MultiMode / "
                             + "AvatarObjectReference.Get) did not resolve. No fate table is published: an "
                             + "incomplete census under this heading is a false all-clear.";
                return census;
            }

            var menuItemType = VendorReflect.FindType("nadena.dev.modular_avatar.core.ModularAvatarMenuItem");

            // Two passes over the ShapeChangers. The first records every row keyed by (renderer, shape) in
            // hierarchy order; the second resolves each Delete's LaterSetHost from it. One pass cannot: MA's
            // cancel rule is about what comes AFTER, and a row's canceller may live in a component this sweep
            // has not reached yet.
            var order = new List<(GameObject renderer, string shape, int changeType, Component owner)>();
            foreach (var comp in root.GetComponentsInChildren(scType, true))
            {
                if (comp == null) continue;
                foreach (var row in EnumerateRows(shapesProp, comp))
                {
                    var target = ResolveTarget(getMethod, objField, row, comp);
                    var shapeName = nameField.GetValue(row) as string;
                    if (target == null || string.IsNullOrEmpty(shapeName)) continue;
                    // MA's own three skips, in its order (FindShapes): no SkinnedMeshRenderer, no mesh, and no
                    // such blendshape on that mesh. A row MA skipped becomes NO rule at all, on either key — so
                    // it can neither be a declared removal nor cancel one. Censusing it would put a row in the
                    // table that no bone can ever match and that nothing removed, and the fate table would then
                    // read `build-time` for a row the build never saw.
                    var smr = target.GetComponent<SkinnedMeshRenderer>();
                    if (smr == null || smr.sharedMesh == null) continue;
                    if (smr.sharedMesh.GetBlendShapeIndex(shapeName) < 0) continue;
                    int changeType = Convert.ToInt32(ctField.GetValue(row), CultureInfo.InvariantCulture);
                    order.Add((target, shapeName, changeType, (Component)comp));
                }
            }

            for (int i = 0; i < order.Count; i++)
            {
                var (target, shape, changeType, owner) = order[i];
                if (changeType != 0) continue;  // ShapeChangeType.Delete == 0; a Set declares no removal
                var smr = target.GetComponent<SkinnedMeshRenderer>();  // resolved in the pass above

                string laterSet = null;
                for (int j = i + 1; j < order.Count; j++)
                    if (order[j].renderer == target && order[j].shape == shape && order[j].changeType != 0)
                    { laterSet = PathOf(order[j].owner.gameObject); break; }

                census.Rows.Add(new DeclaredRow
                {
                    Kind = "shape",
                    HostPath = PathOf(owner.gameObject),
                    Shape = shape,
                    RendererName = smr.name,
                    RendererPath = PathOf(smr.gameObject),
                    Inverted = ReportShapeOverlap.ReadInverted(owner),
                    MenuItemAncestor = NearestMenuItem(owner.transform, root.transform, menuItemType),
                    ParkedAncestor = NearestParked(owner.transform, root.transform, null),
                    LaterSetHost = laterSet,
                    AffectedObjectSkip = false,
                    AuthoredVerts = smr.sharedMesh != null ? smr.sharedMesh.vertexCount : -1,
                });
            }

            foreach (var comp in root.GetComponentsInChildren(mcType, true))
            {
                if (comp == null) continue;
                GameObject target;
                try { target = getMethod.Invoke(cutterObjProp.GetValue(comp), new object[] { comp }) as GameObject; }
                catch (Exception e)
                {
                    census.Drift = "a MeshCutter object reference on '" + PathOf(((Component)comp).gameObject)
                                 + "' threw " + e.GetType().Name + " — the census is incomplete, so no fate table is published.";
                    return census;
                }
                var smr = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                if (smr == null) continue;      // MA skips a cutter whose target is not a skinned mesh

                var host = ((Component)comp).gameObject;
                var filters = ReadFilters(host);
                census.Rows.Add(new DeclaredRow
                {
                    Kind = "cutter",
                    HostPath = PathOf(host),
                    Shape = null,
                    RendererName = smr.name,
                    RendererPath = PathOf(smr.gameObject),
                    Inverted = ReportShapeOverlap.ReadInverted(comp),
                    FilterSummary = filters.Count == 0 ? "(no vertex filter — MA skips this cutter entirely)"
                                                       : string.Join(" + ", filters.Select(f => f.summary)),
                    FilterTokens = filters.Select(f => f.token).ToArray(),
                    MultiMode = multiModeProp.GetValue(comp)?.ToString(),
                    MenuItemAncestor = NearestMenuItem(((Component)comp).transform, root.transform, menuItemType),
                    // The renderer's own ancestors are skipped for a cutter, so the parked walk stops where
                    // MA's condition walk stops — at the highest ancestor the renderer does NOT share.
                    ParkedAncestor = NearestParked(((Component)comp).transform, root.transform, smr.transform),
                    AffectedObjectSkip = true,
                    AuthoredVerts = smr.sharedMesh != null ? smr.sharedMesh.vertexCount : -1,
                });
            }

            return census;
        }

        private static IEnumerable<object> EnumerateRows(PropertyInfo shapesProp, UnityEngine.Object comp)
        {
            System.Collections.IEnumerable rows = null;
            try { rows = shapesProp.GetValue(comp) as System.Collections.IEnumerable; }
            catch { rows = null; }
            if (rows == null) yield break;
            foreach (var r in rows) if (r != null) yield return r;
        }

        private static GameObject ResolveTarget(MethodInfo get, FieldInfo objField, object row, UnityEngine.Object comp)
        {
            try
            {
                var objRef = objField.GetValue(row);
                return objRef == null ? null : get.Invoke(objRef, new object[] { comp }) as GameObject;
            }
            catch { return null; }
        }

        /// <summary>Each <c>IMeshSelectorBehavior</c> on the cutter's own GameObject, as MA reads them: the
        /// human summary and the LEADING TOKEN its <c>IMeshSelector.ToString()</c> emits, which is the only
        /// handle available for attributing a NaNimated bone back to one of several cutters on one renderer.
        /// <c>VertexFilterByMask</c> deliberately contributes no token — its <c>ToString()</c> starts with a
        /// material index, so there is nothing to match on, and saying so is what keeps the attribution honest.</summary>
        private static List<(string summary, string token)> ReadFilters(GameObject host)
        {
            var found = new List<(string, string)>();
            foreach (var c in host.GetComponents<Component>())
            {
                if (c == null) continue;
                string t = c.GetType().Name;
                switch (t)
                {
                    case "VertexFilterByShapeComponent":
                        var shapes = ReadStringList(c, "Shapes");
                        found.Add(("shapes[" + string.Join(", ", shapes) + "]", "VertexFilterByShape"));
                        break;
                    case "VertexFilterByBoneComponent": found.Add(("bone", "VertexFilterByBone")); break;
                    case "VertexFilterByAxisComponent": found.Add(("axis", "VertexFilterByAxis")); break;
                    case "VertexFilterByUVTileComponent": found.Add(("uv-tile", "VertexFilterByUVTile")); break;
                    case "VertexFilterByMaskComponent": found.Add(("mask", null)); break;
                }
            }
            return found;
        }

        private static List<string> ReadStringList(Component c, string prop)
        {
            try
            {
                var p = c.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
                if (p?.GetValue(c) is System.Collections.IEnumerable e)
                    return e.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
            }
            catch { }
            return new List<string>();
        }

        /// <summary>The nearest MA <c>MenuItem</c> at or above <paramref name="from"/>, stopping at the avatar
        /// root — MA attaches at most one, the closest, and nothing can make its condition constant, so its
        /// presence alone decides the row is conditional.</summary>
        internal static string NearestMenuItem(Transform from, Transform stopAt, Type menuItemType)
        {
            if (menuItemType == null) return null;
            for (var cur = from; cur != null && cur != stopAt; cur = cur.parent)
                if (cur.GetComponent(menuItemType) != null) return PathOf(cur.gameObject);
            return null;
        }

        /// <summary>The nearest ancestor with <c>activeSelf</c> false, at or above <paramref name="from"/>.
        /// <paramref name="affected"/> non-null reproduces MA's affected-object skip: an ancestor the affected
        /// renderer also sits under contributes no condition, so it cannot park the row either.
        /// Pure over its inputs, so every branch is assertable without a build.</summary>
        internal static string NearestParked(Transform from, Transform stopAt, Transform affected)
        {
            for (var cur = from; cur != null && cur != stopAt; cur = cur.parent)
            {
                if (affected != null && (affected == cur || affected.IsChildOf(cur))) continue;
                if (!cur.gameObject.activeSelf) return PathOf(cur.gameObject);
            }
            return null;
        }

        // ── Pure joins over MA's emitted names ────────────────────────────────────────────────────────────

        /// <summary>MA's own mangling of a <c>TargetProp</c> into a GameObject name: <c>'/'</c> becomes
        /// <c>'_'</c>, because a slash in a hierarchy name breaks the animation binding paths MA then writes.
        /// Applied to BOTH sides of every comparison rather than inverted — the mangle is not injective, so a
        /// shape named <c>A/B</c> and one named <c>A_B</c> produce the same bone name and un-mangling would
        /// invent a distinction the build does not have.</summary>
        internal static string Mangle(string targetPropKey)
            => targetPropKey == null ? null : targetPropKey.Replace('/', '_');

        /// <summary>The key half of a NaNimated bone's name, or null when the name is not one. Returns the
        /// MANGLED key, which is what every comparison here uses.</summary>
        internal static string NaNimatedKeyOf(string gameObjectName)
            => gameObjectName != null && gameObjectName.StartsWith(NaNimatedBonePrefix, StringComparison.Ordinal)
                ? gameObjectName.Substring(NaNimatedBonePrefix.Length)
                : null;

        /// <summary>The ancestor an <c>__MA/ActiveSelfProxy/&lt;name&gt;##&lt;n&gt;</c> parameter names, or null
        /// for any other parameter. The <c>##&lt;n&gt;</c> disambiguator is stripped, never matched away: two
        /// objects with the same name get different indices, so the name alone is a hint and the full parameter
        /// stays in the row.</summary>
        internal static string AncestorNamedBy(string parameter)
        {
            if (parameter == null || !parameter.StartsWith(ActiveSelfProxyPrefix, StringComparison.Ordinal)) return null;
            var rest = parameter.Substring(ActiveSelfProxyPrefix.Length);
            int hash = rest.LastIndexOf("##", StringComparison.Ordinal);
            return hash >= 0 ? rest.Substring(0, hash) : rest;
        }

        /// <summary>Which of the six mechanisms explains a row that did not delete at build, given the
        /// controlling parameters measured off the generating layer. Pure, and ORDERED — a row can satisfy more
        /// than one, and the order is MA's own precedence: a menu item's condition can never be folded away, an
        /// animated ancestor is the next-strongest, and the parked/cancelled arms only apply to rows that
        /// generated no layer at all.
        /// <para>Returns null when nothing explains it. That is a defect in this door, not a finding about the
        /// avatar — MA generated a layer, so SOMETHING made the row conditional — and the caller fails loud on
        /// it rather than printing an unexplained NaNimation.</para></summary>
        internal static string ClassifyConditional(DeclaredRow row, IList<string> controllingParams)
        {
            if (row.MenuItemAncestor != null
                && (controllingParams == null || controllingParams.All(p => AncestorNamedBy(p) == null)))
                return MechMenuItem;
            if (controllingParams != null && controllingParams.Any(p => AncestorNamedBy(p) != null))
                return MechAncestorAnimated;
            if (row.MenuItemAncestor != null) return MechMenuItem;
            return null;
        }

        // ── Emit helpers shared with the bake ─────────────────────────────────────────────────────────────

        /// <summary>The verdict, in <see cref="CheckAvatar"/>'s grammar. <c>PASS</c> when every declared row
        /// deleted at build or was deliberately dropped AND the active triangle count is inside the budget;
        /// <c>CLASSIFY</c> when a row NaNimates, a row was cancelled by a Set, or the budget is exceeded — each
        /// a finding for the operator to rule on per row, never a tool failure, because the venue accepts
        /// NaNimation on some nodes by ruling. <c>FAIL</c> is reserved for a row this door could not explain,
        /// which is a bug in the door.</summary>
        internal static string VerdictOf(IEnumerable<string> fates, long activeTriangles, int budget)
        {
            var list = fates as IList<string> ?? fates.ToList();
            if (list.Any(f => f == FateUnresolved)) return "FAIL";
            if (list.Any(f => f == FateNaNimation || f == FateCancelled)) return "CLASSIFY";
            return activeTriangles > budget ? "CLASSIFY" : "PASS";
        }

        internal static string Refuse(string why)
        {
            string err = "[ReportDeleteFate] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        // ── Scene resolver (path → instance id → name; mirrors CheckAvatar.Resolve) ───────────────────────

        internal static GameObject Resolve(string target)
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

        internal static string PathOf(GameObject go)
        {
            if (go == null) return "—";
            var t = go.transform;
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        /// <summary>Path relative to <paramref name="root"/> — the frame animation bindings are written in, so
        /// a binding lookup uses this and never <see cref="PathOf"/>.</summary>
        internal static string RelativePath(Transform t, Transform root)
        {
            if (t == root) return "";
            var sb = new StringBuilder(t.name);
            for (var cur = t.parent; cur != null && cur != root; cur = cur.parent)
                sb.Insert(0, cur.name + "/");
            return sb.ToString();
        }
    }
}
