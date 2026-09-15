using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Reads a prefab instance or variant asset as the CHAIN it is — level by level up to the Model or
    /// Regular prefab it bottoms out on — and says what each level does to its parent: objects and components
    /// added and removed, and the property overrides sorted into tiers so framework and editor churn is
    /// counted rather than read. Every other door sees only the flattened result.
    ///
    /// <para><b>The five override lists are per file.</b> <c>PrefabUtility</c> answers
    /// <c>GetPropertyModifications</c> / <c>GetAddedGameObjects</c> / <c>GetRemovedGameObjects</c> /
    /// <c>GetAddedComponents</c> / <c>GetRemovedComponents</c> for the PrefabInstance object that lives in the
    /// file being read: from a scene, every nested root answers with the outermost instance's lists; inside a
    /// variant asset, a nested root whose PrefabInstance the file owns answers with its own. So per-level
    /// attribution walks <c>GetCorrespondingObjectFromSource</c> from asset to asset, and each level lists only
    /// the nested instances IT owns. All five lists read straight off <c>AssetDatabase.LoadAssetAtPath</c>: no
    /// instantiation, no <c>LoadPrefabContents</c>.</para>
    ///
    /// <para>Read-only: a markdown digest under Snapshots/, a one-line summary in-band.</para>
    /// </summary>
    [AgentTool]
    public static class ReportPrefab
    {
        internal const int MaxChainDepth = 8;       // asset levels walked above level 0
        internal const int MaxDependentsDepth = 8;  // containment tree depth in Dependents
        internal const int MaxRows = 40;            // rows per section by default
        internal const int MaxRowsAll = 400;        // rows per section under all:true — capped, never unbounded
        internal const double Epsilon = 1e-4;       // numeric override equals its source below this
        internal const double RotationDotEpsilon = 1e-6; // |dot(q, src)| >= 1 - this ⇒ same rotation (≈0.16°; a float quaternion's own norm noise is ~1e-7)

        private enum Tier { Default, Churn, Dangling, Inert, Authored }

        private sealed class Level
        {
            public GameObject Root;
            public string Path;      // asset path, or the scene hierarchy path for level 0 in a scene
            public string Kind;      // "scene instance" | Variant | Regular | Model
            public bool IsScene;
            public bool HasParent;   // false on the base line (Regular / Model)
        }

        // ----- Run ------------------------------------------------------------------------------------

        /// <summary>Report the chain above <paramref name="handle"/> — a <c>.prefab</c> asset path, or a scene
        /// handle (hierarchy path / instance id / name) resolving to an OUTERMOST prefab instance root. Rows are
        /// the <c>authored</c> tier only unless <paramref name="all"/>; every tier is always counted.</summary>
        public static string Run(string handle, bool all = false)
        {
            if (string.IsNullOrEmpty(handle)) return Refuse("handle is null/empty: pass a .prefab asset path or a scene handle");

            var levels = new List<Level>();
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(handle);
            if (asset != null)
            {
                var kind = PrefabUtility.GetPrefabAssetType(asset);
                if (kind == PrefabAssetType.NotAPrefab) return Refuse("'" + handle + "' is not a prefab asset");
                if (kind == PrefabAssetType.Model) return Refuse("'" + handle + "' is a Model: nothing sits above an FBX to report. AgentInspector.Run reads its hierarchy.");
                if (kind == PrefabAssetType.MissingAsset) return Refuse("'" + handle + "' has a missing source asset — restore or re-import it before reading its overrides");
                levels.Add(new Level { Root = asset, Path = handle, Kind = kind.ToString(), HasParent = kind == PrefabAssetType.Variant });
            }
            else
            {
                var sh = SceneHandle.Resolve(handle);
                if (!sh.Ok) return Refuse("handle: " + sh.Refusal + " (a .prefab path is accepted here too)");
                var go = sh.Object;
                if (!PrefabUtility.IsPartOfPrefabInstance(go)) return Refuse("'" + handle + "' is not a prefab instance — nothing to report; AgentInspector.Run reads a plain hierarchy");
                if (PrefabUtility.IsPrefabAssetMissing(go)) return Refuse("'" + handle + "' is an instance of a MISSING asset — every override target is dangling; restore the asset first");
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                {
                    var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                    var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                    return Refuse("'" + handle + "' is inside the instance rooted at '" + HierarchyPath(outer.transform)
                        + "' — from a scene the five override lists answer for that OUTERMOST root only. Run(\"" + HierarchyPath(outer.transform) + "\") for the scene level, or Run(\""
                        + PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(nearest) + "\") for the nested asset's own overrides.");
                }
                levels.Add(new Level { Root = go, Path = HierarchyPath(go.transform), Kind = "scene instance", IsScene = true, HasParent = true });
            }

            // Walk up. GetCorrespondingObjectFromSource is one asset level per hop; a Regular or Model root
            // returns null and is the base line.
            var cur = levels[0].Root;
            while (levels.Count <= MaxChainDepth)
            {
                var src = PrefabUtility.GetCorrespondingObjectFromSource(cur);
                if (src == null) break;
                var kind = PrefabUtility.GetPrefabAssetType(src);
                if (kind == PrefabAssetType.MissingAsset) return Refuse("level " + levels.Count + " of '" + handle + "' is a MISSING asset — restore it before reading the chain");
                levels.Add(new Level { Root = src, Path = AssetDatabase.GetAssetPath(src), Kind = kind.ToString(), HasParent = kind == PrefabAssetType.Variant });
                cur = src;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
                foreach (var l in levels)
                    if (!l.IsScene && string.Equals(l.Path, stage.assetPath, StringComparison.Ordinal))
                        return Refuse("a prefab stage is open on '" + stage.assetPath + "', which is level " + levels.IndexOf(l) + " of this chain: an asset read would report the on-disk content, not the stage's edits. Save and close the stage first.");

            var doc = new StringBuilder();
            var summary = new StringBuilder("[ReportPrefab] " + levels[0].Root.name + ": levels=" + levels.Count);
            doc.Append("# ReportPrefab: ").Append(levels[0].Root.name).Append('\n');
            doc.Append("_One block per level, outermost first. Each level's lists are the PrefabInstance that level's FILE owns; an inherited nested instance is listed at the level that owns it. Object paths are relative to the level's root. Tiers: `default` = Unity's root transform/name defaults; `churn` = a framework or editor stamp (`PrefabChurn`); `dangling` = the target no longer exists; `inert` = equals its source (a float within ")
               .Append(Epsilon.ToString(CultureInfo.InvariantCulture)).Append(", a rotation within ~0.2°, an object ref whose corresponding source is the source) — present in the file and still a pin against the parent changing, but not a difference today; `authored` = the rest. `constraint=` marks a Transform whose instance object carries a VRC constraint, so its value may be the edit-mode solve rather than a hand; physbone-driven bones are NOT marked._\n\n");

            for (int k = 0; k < levels.Count; k++)
            {
                var l = levels[k];
                doc.Append("## L").Append(k).Append(' ').Append(Cell(l.Path)).Append("  (").Append(l.Kind).Append(')');
                if (l.HasParent && k + 1 < levels.Count) doc.Append("  parent: ").Append(Cell(levels[k + 1].Path));
                doc.Append('\n');
                if (!l.HasParent)
                {
                    doc.Append("_base of the chain — no overrides above it._\n");
                    AppendOwnedInstances(doc, summary, l, new HashSet<GameObject>(), all);
                    doc.Append('\n');
                    continue;
                }
                var added = new HashSet<GameObject>();
                AppendStructure(doc, summary, l, k, added, all);
                AppendOverrides(doc, summary, l, k, all);
                AppendOwnedInstances(doc, summary, l, added, all);
                doc.Append('\n');
            }

            summary.Append(" => OK");
            var result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "prefab_" + levels[0].Root.name, summary.ToString(), doc.ToString(), ".md");
            Debug.Log(result);
            return result;
        }

        // ----- Structure --------------------------------------------------------------------------------

        private static void AppendStructure(StringBuilder doc, StringBuilder summary, Level l, int k, HashSet<GameObject> added, bool all)
        {
            var root = l.Root.transform;
            var addedGo = PrefabUtility.GetAddedGameObjects(l.Root);
            var removedGo = PrefabUtility.GetRemovedGameObjects(l.Root);
            var addedComp = PrefabUtility.GetAddedComponents(l.Root);
            var removedComp = PrefabUtility.GetRemovedComponents(l.Root);
            summary.Append(" L").Append(k).Append("(+go=").Append(addedGo.Count).Append(" -go=").Append(removedGo.Count)
                   .Append(" +comp=").Append(addedComp.Count).Append(" -comp=").Append(removedComp.Count);

            var rows = new List<string>();
            foreach (var a in addedGo)
            {
                added.Add(a.instanceGameObject);
                rows.Add(Cell(a.instanceGameObject.name) + " under `" + Cell(RelPath(a.instanceGameObject.transform.parent, root)) + "`"
                       + (PrefabUtility.IsAnyPrefabInstanceRoot(a.instanceGameObject) ? " (an instance — see instances owned below)" : ""));
            }
            AppendList(doc, "added objects", rows, all);

            rows.Clear();
            foreach (var r in removedGo)
                rows.Add(Cell(r.assetGameObject != null ? r.assetGameObject.name : "?") + " under `"
                       + Cell(r.parentOfRemovedGameObjectInInstance != null ? RelPath(r.parentOfRemovedGameObjectInInstance.transform, root) : "?") + "`");
            AppendList(doc, "removed objects", rows, all);

            rows.Clear();
            foreach (var c in addedComp)
                rows.Add(Cell(c.instanceComponent != null ? c.instanceComponent.GetType().Name : "?") + " on `"
                       + Cell(c.instanceComponent != null ? RelPath(c.instanceComponent.transform, root) : "?") + "`");
            AppendList(doc, "added components", rows, all);

            rows.Clear();
            foreach (var c in removedComp)
                rows.Add(Cell(c.assetComponent != null ? c.assetComponent.GetType().Name : "?") + " on `"
                       + Cell(c.containingInstanceGameObject != null ? RelPath(c.containingInstanceGameObject.transform, root) : "?") + "`");
            AppendList(doc, "removed components", rows, all);
        }

        private static void AppendList(StringBuilder doc, string title, List<string> rows, bool all)
        {
            doc.Append("**").Append(title).Append("** (").Append(rows.Count).Append(")\n");
            int cap = all ? MaxRowsAll : MaxRows;
            for (int i = 0; i < rows.Count && i < cap; i++) doc.Append("- ").Append(rows[i]).Append('\n');
            if (rows.Count > cap) doc.Append("- … +").Append(rows.Count - cap).Append(" more (all:true lifts the cap to ").Append(MaxRowsAll).Append(")\n");
        }

        // ----- Owned nested instances ---------------------------------------------------------------------

        /// <summary>Instance roots under this level whose PrefabInstance this level's file owns: their own five
        /// lists are readable here and nowhere else. Inherited nested roots share the level root's handle and
        /// are skipped — they belong to the level that owns them.</summary>
        private static void AppendOwnedInstances(StringBuilder doc, StringBuilder summary, Level l, HashSet<GameObject> added, bool all)
        {
            var rootHandle = PrefabUtility.GetPrefabInstanceHandle(l.Root);
            var rows = new List<string>();
            foreach (var t in l.Root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                if (go == l.Root || !PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;
                var h = PrefabUtility.GetPrefabInstanceHandle(go);
                if (h == null || h == rootHandle) continue;
                if (!string.Equals(AssetDatabase.GetAssetPath(h), l.IsScene ? "" : l.Path, StringComparison.Ordinal)) continue;
                var mods = PrefabUtility.GetPropertyModifications(go);
                rows.Add("`" + Cell(RelPath(t, l.Root.transform)) + "` <- `" + Cell(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go)) + "`"
                       + " mods=" + (mods == null ? 0 : mods.Length)
                       + " +go=" + PrefabUtility.GetAddedGameObjects(go).Count + " -go=" + PrefabUtility.GetRemovedGameObjects(go).Count
                       + " +comp=" + PrefabUtility.GetAddedComponents(go).Count + " -comp=" + PrefabUtility.GetRemovedComponents(go).Count
                       + (added.Contains(go) ? " [added here]" : "")
                       + " → ReportPrefab.Run(\"" + Cell(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go)) + "\")");
            }
            if (rows.Count > 0 || l.HasParent) AppendList(doc, "instances owned by this file", rows, all);
            if (l.HasParent) summary.Append(" owned=").Append(rows.Count).Append(')');
        }

        // ----- Overrides ------------------------------------------------------------------------------

        private sealed class Row
        {
            public Tier Tier;
            public string Target, Prop, Source, Value, Note;
        }

        private sealed class RotationGroup
        {
            public Object Target;
            public float? X, Y, Z, W;
            public bool AnyDefault;
        }

        private static void AppendOverrides(StringBuilder doc, StringBuilder summary, Level l, int k, bool all)
        {
            var mods = PrefabUtility.GetPropertyModifications(l.Root) ?? new PropertyModification[0];
            var map = BuildInstanceMap(l.Root);
            var sos = new Dictionary<Object, SerializedObject>();
            var counts = new int[5];
            var churnHist = new Dictionary<string, int>();
            var rows = new List<Row>();
            var rotations = new Dictionary<Object, RotationGroup>();
            int constraintDriven = 0;
            var rootPosDelta = new float[3]; bool rootMoved = false;
            try
            {
                foreach (var m in mods)
                {
                    // m_LocalRotation is judged per target as one quaternion, after the loop.
                    if (m.target != null && m.propertyPath.StartsWith("m_LocalRotation.", StringComparison.Ordinal) && m.propertyPath.Length == 17)
                    {
                        RotationGroup g;
                        if (!rotations.TryGetValue(m.target, out g)) rotations[m.target] = g = new RotationGroup { Target = m.target };
                        float f = ParseFloat(m.value);
                        switch (m.propertyPath[16]) { case 'x': g.X = f; break; case 'y': g.Y = f; break; case 'z': g.Z = f; break; case 'w': g.W = f; break; }
                        g.AnyDefault |= PrefabUtility.IsDefaultOverride(m);
                        continue;
                    }
                    var row = Classify(m, sos, map, ref constraintDriven);
                    counts[(int)row.Tier]++;
                    if (row.Tier == Tier.Churn) { var tok = PrefabChurn.ChurnToken(m.propertyPath) ?? "ma-stamp"; int c; churnHist.TryGetValue(tok, out c); churnHist[tok] = c + 1; }
                    if (row.Tier == Tier.Default && m.propertyPath.StartsWith("m_LocalPosition.", StringComparison.Ordinal) && m.target != null)
                    {
                        var sp = Source(sos, m.target, m.propertyPath);
                        if (sp != null && sp.propertyType == SerializedPropertyType.Float)
                        {
                            int axis = m.propertyPath.Length == 17 ? m.propertyPath[16] - 'x' : -1;
                            if (axis >= 0 && axis < 3)
                            {
                                float d = ParseFloat(m.value) - sp.floatValue;
                                rootPosDelta[axis] = d;
                                if (Math.Abs(d) >= Epsilon) rootMoved = true;
                            }
                        }
                    }
                    if (all || row.Tier == Tier.Authored) rows.Add(row);
                }
                float rootRotDeg = 0f;
                foreach (var g in rotations.Values)
                {
                    var row = ClassifyRotation(g, sos, map, ref constraintDriven, out float deg);
                    counts[(int)row.Tier]++;
                    if (g.AnyDefault && deg >= 0.01f) { rootMoved = true; rootRotDeg = deg; }
                    if (all || row.Tier == Tier.Authored) rows.Add(row);
                }

                doc.Append("**overrides** authored=").Append(counts[(int)Tier.Authored])
                   .Append(" churn=").Append(counts[(int)Tier.Churn])
                   .Append(" inert=").Append(counts[(int)Tier.Inert])
                   .Append(" dangling=").Append(counts[(int)Tier.Dangling])
                   .Append(" default=").Append(counts[(int)Tier.Default]);
                if (constraintDriven > 0) doc.Append(" (constraint-driven among authored: ").Append(constraintDriven).Append(')');
                if (churnHist.Count > 0)
                {
                    doc.Append("  churn by family:");
                    foreach (var kv in churnHist) doc.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                }
                doc.Append('\n');
                if (rootMoved)
                    doc.Append("_root moved (default tier): pos Δ=(").Append(F(rootPosDelta[0])).Append(", ").Append(F(rootPosDelta[1])).Append(", ").Append(F(rootPosDelta[2]))
                       .Append(") rot Δ=").Append(F(rootRotDeg)).Append("°_\n");
                summary.Append(" authored=").Append(counts[(int)Tier.Authored]).Append(" dangling=").Append(counts[(int)Tier.Dangling]);

                if (rows.Count > 0)
                {
                    doc.Append("\n| tier | target | property | source | value | note |\n|---|---|---|---|---|---|\n");
                    int cap = all ? MaxRowsAll : MaxRows;
                    for (int i = 0; i < rows.Count && i < cap; i++)
                    {
                        var r = rows[i];
                        doc.Append("| ").Append(r.Tier.ToString().ToLowerInvariant()).Append(" | `").Append(r.Target).Append("` | `").Append(r.Prop)
                           .Append("` | ").Append(r.Source).Append(" | ").Append(r.Value).Append(" | ").Append(r.Note ?? "").Append(" |\n");
                    }
                    if (rows.Count > cap) doc.Append("| … | +").Append(rows.Count - cap).Append(" more | | | | |\n");
                }
            }
            finally
            {
                foreach (var so in sos.Values) so.Dispose();
            }
        }

        private static Row Classify(PropertyModification m, Dictionary<Object, SerializedObject> sos, Dictionary<Object, Mapped> map, ref int constraintDriven)
        {
            var row = new Row { Prop = Cell(m.propertyPath), Target = TargetLabel(m.target, map), Value = Cell(m.objectReference != null ? m.objectReference.name : m.value) };
            if (PrefabUtility.IsDefaultOverride(m)) { row.Tier = Tier.Default; return row; }
            if (PrefabChurn.IsChurn(m.propertyPath)) { row.Tier = Tier.Churn; return row; }
            if (m.target == null) { row.Tier = Tier.Dangling; row.Target = "(target missing)"; return row; }

            var sp = Source(sos, m.target, m.propertyPath);
            if (sp == null)
            {
                int idx = ArrayIndex(m.propertyPath);
                if (idx >= 0)
                {
                    var size = Source(sos, m.target, m.propertyPath.Substring(0, m.propertyPath.IndexOf(".Array.data[", StringComparison.Ordinal)) + ".Array.size");
                    if (size != null && idx >= size.intValue) { row.Tier = Tier.Authored; row.Source = "(no element " + idx + " in source)"; row.Note = "new element"; return row; }
                }
                row.Tier = Tier.Dangling; row.Source = "(no such field on " + m.target.GetType().Name + ")"; return row;
            }

            row.Source = Cell(Describe(sp, m.target, m.propertyPath));
            bool equal;
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Float:
                    equal = Math.Abs(ParseFloat(m.value) - sp.doubleValue) < Epsilon; break;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    { long v; equal = long.TryParse(m.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v == sp.longValue; break; }
                case SerializedPropertyType.Boolean:
                    equal = (m.value == "1" || string.Equals(m.value, "true", StringComparison.OrdinalIgnoreCase)) == sp.boolValue; break;
                case SerializedPropertyType.String:
                    equal = string.Equals(m.value ?? "", sp.stringValue ?? "", StringComparison.Ordinal); break;
                case SerializedPropertyType.ObjectReference:
                    {
                        var srcRef = sp.objectReferenceValue;
                        equal = SameOrCorresponds(m.objectReference, srcRef);
                        // Modular Avatar stamps its resolved object refs on load: a null source filled with a live
                        // object on an MA component is the stamp, not authoring.
                        if (!equal && srcRef == null && m.objectReference != null && m.target.GetType().FullName.StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal))
                        { row.Tier = Tier.Churn; row.Note = "MA load-time stamp"; return row; }
                        break;
                    }
                default:
                    equal = false; break;
            }
            row.Tier = equal ? Tier.Inert : Tier.Authored;
            if (row.Tier == Tier.Authored && m.target is Transform) row.Note = ConstraintNote(m.target, map, ref constraintDriven);
            return row;
        }

        private static Row ClassifyRotation(RotationGroup g, Dictionary<Object, SerializedObject> sos, Dictionary<Object, Mapped> map, ref int constraintDriven, out float degrees)
        {
            degrees = 0f;
            var row = new Row { Prop = "m_LocalRotation", Target = TargetLabel(g.Target, map) };
            var sp = Source(sos, g.Target, "m_LocalRotation");
            if (sp == null) { row.Tier = Tier.Dangling; row.Source = "(no such field)"; return row; }
            var src = sp.quaternionValue;
            var q = Reconstruct(src, g.X, g.Y, g.Z, g.W);
            row.Source = "(" + F(src.x) + ", " + F(src.y) + ", " + F(src.z) + ", " + F(src.w) + ")";
            row.Value = "(" + F(q.x) + ", " + F(q.y) + ", " + F(q.z) + ", " + F(q.w) + ")";
            bool same = SameRotation(src, q);
            degrees = same ? 0f : Quaternion.Angle(src, q);
            if (g.AnyDefault) { row.Tier = Tier.Default; return row; }
            row.Tier = same ? Tier.Inert : Tier.Authored;
            if (!same) row.Note = "Δ=" + F(degrees) + "° " + (ConstraintNote(g.Target, map, ref constraintDriven) ?? "");
            return row;
        }

        /// <summary>The instance's rotation as Unity stores it: the components it listed, the rest from the
        /// source (the partial-quaternion trap — filling with zero invents a large divergence).</summary>
        internal static Quaternion Reconstruct(Quaternion source, float? x, float? y, float? z, float? w)
        {
            return new Quaternion(x ?? source.x, y ?? source.y, z ?? source.z, w ?? source.w);
        }

        /// <summary>q and −q are one rotation; a per-component compare reads a sign flip as a large change.</summary>
        internal static bool SameRotation(Quaternion a, Quaternion b)
        {
            double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
            return Math.Abs(dot) >= 1.0 - RotationDotEpsilon;
        }

        /// <summary>Object-ref equality across a prefab boundary: the override's object IS the source, or the
        /// source is on its corresponding-source chain (a variant re-pointing a bone at its own copy of it).</summary>
        internal static bool SameOrCorresponds(Object overrideRef, Object sourceRef)
        {
            if (overrideRef == sourceRef) return true;
            if (overrideRef == null || sourceRef == null) return false;
            var o = overrideRef;
            for (int i = 0; i < MaxChainDepth && o != null; i++)
            {
                o = PrefabUtility.GetCorrespondingObjectFromSource(o);
                if (o == sourceRef) return true;
            }
            return false;
        }

        private static string ConstraintNote(Object target, Dictionary<Object, Mapped> map, ref int constraintDriven)
        {
            Mapped mp;
            if (!map.TryGetValue(target, out mp) || mp.Instance == null) return null;
            foreach (var c in mp.Instance.GetComponents<Component>())
            {
                if (c == null) continue;
                for (var t = c.GetType(); t != null; t = t.BaseType)
                    if (t.Name == "VRCConstraintBase") { constraintDriven++; return "constraint=" + c.GetType().Name; }
            }
            return null;
        }

        // ----- Source lookups ---------------------------------------------------------------------

        private static SerializedProperty Source(Dictionary<Object, SerializedObject> sos, Object target, string path)
        {
            SerializedObject so;
            if (!sos.TryGetValue(target, out so)) sos[target] = so = new SerializedObject(target);
            return so.FindProperty(path);
        }

        private static string Describe(SerializedProperty sp, Object target, string path)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Float: return F(sp.floatValue) + BlendShapeName(target, path);
                case SerializedPropertyType.Boolean: return sp.boolValue ? "1" : "0";
                case SerializedPropertyType.String: return sp.stringValue;
                case SerializedPropertyType.ObjectReference: return sp.objectReferenceValue != null ? sp.objectReferenceValue.name : "null";
                case SerializedPropertyType.Enum: return sp.enumValueIndex >= 0 && sp.enumValueIndex < sp.enumDisplayNames.Length ? sp.enumDisplayNames[sp.enumValueIndex] : sp.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                    return sp.longValue.ToString(CultureInfo.InvariantCulture);
                default: return sp.propertyType.ToString();
            }
        }

        /// <summary>A blendshape weight row is unreadable as `data[47]`; the mesh knows the name.</summary>
        private static string BlendShapeName(Object target, string path)
        {
            var smr = target as SkinnedMeshRenderer;
            if (smr == null || smr.sharedMesh == null || !path.StartsWith("m_BlendShapeWeights.", StringComparison.Ordinal)) return "";
            int idx = ArrayIndex(path);
            return idx >= 0 && idx < smr.sharedMesh.blendShapeCount ? " (" + smr.sharedMesh.GetBlendShapeName(idx) + ")" : "";
        }

        private static int ArrayIndex(string path)
        {
            int a = path.LastIndexOf(".Array.data[", StringComparison.Ordinal);
            if (a < 0) return -1;
            int b = path.IndexOf(']', a);
            int idx;
            return b > a && int.TryParse(path.Substring(a + 12, b - a - 12), out idx) ? idx : -1;
        }

        // ----- Instance map: source object → path under the level root ----------------------------------

        private struct Mapped { public string Path; public GameObject Instance; public int Hits; }

        /// <summary>A modification's <c>target</c> lives in the parent asset, so its own path is source-rooted
        /// and no handle for this level. One hop of <c>GetCorrespondingObjectFromSource</c> from every object
        /// under the level root maps the parent's objects back to instance paths. Two instances of one nested
        /// prefab under one root reach the same source: the first path wins and the row says <c>(×n)</c>.</summary>
        private static Dictionary<Object, Mapped> BuildInstanceMap(GameObject root)
        {
            var map = new Dictionary<Object, Mapped>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string path = RelPath(t, root.transform);
                Add(map, t.gameObject, path, t.gameObject);
                Add(map, t, path, t.gameObject);
                foreach (var c in t.GetComponents<Component>()) if (c != null) Add(map, c, path, t.gameObject);
            }
            return map;
        }

        private static void Add(Dictionary<Object, Mapped> map, Object o, string path, GameObject instance)
        {
            var s = PrefabUtility.GetCorrespondingObjectFromSource(o);
            if (s == null) return;
            Mapped mp;
            if (map.TryGetValue(s, out mp)) { if (mp.Path != path) { mp.Hits++; map[s] = mp; } return; }
            map[s] = new Mapped { Path = path, Instance = instance, Hits = 1 };
        }

        private static string TargetLabel(Object target, Dictionary<Object, Mapped> map)
        {
            if (target == null) return "(target missing)";
            Mapped mp;
            string where = map.TryGetValue(target, out mp) ? mp.Path + (mp.Hits > 1 ? " (×" + mp.Hits + ")" : "") : "?/" + (target is Component ? ((Component)target).gameObject.name : target.name);
            return Cell(target.GetType().Name + "(" + where + ")");
        }

        // ----- Dependents -----------------------------------------------------------------------------

        /// <summary>Everything under <c>Assets/</c> that contains <paramref name="assetPath"/> — a variant of it,
        /// a prefab nesting an instance of it, a scene placing it — walked transitively through variants and
        /// nesters (a variant of a nester still contains it). A prefab that merely references it (a constraint
        /// source, a mesh) is listed as <c>references</c> and not walked.</summary>
        public static string Dependents(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return Refuse("Dependents: assetPath is null/empty");
            var target = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (target == null || PrefabUtility.GetPrefabAssetType(target) == PrefabAssetType.NotAPrefab)
                return Refuse("Dependents: '" + assetPath + "' is not a prefab or model asset");

            // Reverse map over Assets/: one non-recursive GetDependencies per prefab and scene. The batch
            // overload returns a union with no per-path attribution, so it cannot build this.
            var dependentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int scanned = 0;
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (!p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                scanned++;
                foreach (var d in AssetDatabase.GetDependencies(p, false))
                {
                    if (string.Equals(d, p, StringComparison.Ordinal)) continue;
                    List<string> list;
                    if (!dependentsOf.TryGetValue(d, out list)) dependentsOf[d] = list = new List<string>();
                    list.Add(p);
                }
            }

            var doc = new StringBuilder();
            doc.Append("# ReportPrefab.Dependents: ").Append(Cell(assetPath)).Append('\n');
            doc.Append("_`variant` derives from the line above it; `nests` holds an instance of it; `scene` places it; `references` points at it without containing it (not walked). Indent is containment depth; a diamond is listed once._\n\n");
            var counts = new Dictionary<string, int> { { "variant", 0 }, { "nests", 0 }, { "scene", 0 }, { "references", 0 } };
            var visited = new HashSet<string>(StringComparer.Ordinal) { assetPath };
            Walk(doc, dependentsOf, assetPath, 0, visited, counts);

            var summary = "[ReportPrefab.Dependents] " + RunLogFormat.Leaf(assetPath) + ": variants=" + counts["variant"] + " nests=" + counts["nests"]
                        + " scenes=" + counts["scene"] + " references=" + counts["references"] + " scanned=" + scanned + " => OK";
            var result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "prefab-dependents_" + RunLogFormat.Leaf(assetPath), summary, doc.ToString(), ".md");
            Debug.Log(result);
            return result;
        }

        private static void Walk(StringBuilder doc, Dictionary<string, List<string>> dependentsOf, string target, int depth, HashSet<string> visited, Dictionary<string, int> counts)
        {
            List<string> list;
            if (!dependentsOf.TryGetValue(target, out list)) return;
            list.Sort(StringComparer.Ordinal);
            foreach (var p in list)
            {
                if (!visited.Add(p)) continue;
                string kind = ClassifyDependent(p, target);
                counts[kind]++;
                doc.Append(new string(' ', depth * 2)).Append("- ").Append(kind).Append(' ').Append(Cell(p)).Append('\n');
                if (kind == "references" || kind == "scene") continue;
                if (depth + 1 >= MaxDependentsDepth) { doc.Append(new string(' ', (depth + 1) * 2)).Append("- … depth cap ").Append(MaxDependentsDepth).Append(" reached\n"); continue; }
                Walk(doc, dependentsOf, p, depth + 1, visited, counts);
            }
        }

        private static string ClassifyDependent(string path, string target)
        {
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return "scene";
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null) return "references";
            var src = PrefabUtility.GetCorrespondingObjectFromSource(root);
            if (src != null && string.Equals(AssetDatabase.GetAssetPath(src), target, StringComparison.Ordinal)) return "variant";
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.gameObject != root && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)
                    && string.Equals(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject), target, StringComparison.Ordinal))
                    return "nests";
            return "references";
        }

        // ----- Helpers ----------------------------------------------------------------------------------

        private static string Refuse(string why)
        {
            string err = "[ReportPrefab] FAIL: " + why;
            Debug.LogError(err);
            return err;
        }

        private static string RelPath(Transform t, Transform root)
        {
            if (t == null) return "?";
            if (t == root) return ".";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != root; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        private static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        private static float ParseFloat(string s)
        {
            float f;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : float.NaN;
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        private static string Cell(string s) => RunLogFormat.Cell(s);
    }
}
