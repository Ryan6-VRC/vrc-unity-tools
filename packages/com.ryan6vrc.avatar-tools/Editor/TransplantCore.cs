using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Shared engine for the component-transplant tools (CopyComponents / MoveComponents /
    /// GraftHierarchy). Tool-agnostic primitives only: type-name resolution, the session identity
    /// maps (<see cref="SessionMap"/>), the depth-N transforms-only scaffold builder
    /// (<see cref="ScaffoldBuilder"/>), and the shared RunLog/whatIf helpers. The deep-tier VRC
    /// descriptors live in <see cref="VrcComponentTable"/>; the generic indexed-path remap lives in
    /// <see cref="RemapReferencesByPath"/> (both called, never duplicated here).
    /// </summary>
    public static class TransplantCore
    {
        public const string RunLogDir = RunLogFormat.RunLogDir;

        // ── Ownership predicate ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Per-asset, path-based ownership predicate shared by the clip-repath tools (RepathClips /
        /// OwnControllerClips). An asset is READ-ONLY (returns false) iff its normalized forward-slash path
        /// is, at a segment boundary, under <c>Assets/Vendor</c> OR under <c>Packages</c> (the latter covers
        /// the VRChat SDK proxy gesture clips CleanController keeps) — i.e. the path equals that prefix exactly or
        /// starts with it + "/". Everything else is WRITABLE (returns true), including a null/empty path (an
        /// in-memory asset carries no vendor provenance to protect; the write-landed read-back is the real
        /// safety net). Provenance is a property of each <em>reference</em>, not of a folder (LAYOUT.md) — an
        /// owned controller can legitimately reference vendor clips, so this is called per asset, not per
        /// controller. Matches on the raw asset path; <c>Assets/VendorFoo</c> is writable (segment-safe).
        /// </summary>
        public static bool IsWritableAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return true;
            var p = assetPath.Replace('\\', '/');
            return !(UnderSegment(p, "Assets/Vendor") || UnderSegment(p, "Packages"));
        }

        static bool UnderSegment(string path, string prefix)
            => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);

        // ── Type-name resolution ──────────────────────────────────────────────────────────────────

        /// <summary>Outcome of <see cref="ResolveTypes"/>: the resolved types plus the names that
        /// could not be resolved to exactly one type (unknown OR ambiguous — both fail loud).</summary>
        public struct TypeResolution
        {
            public List<Type> resolved;
            public List<string> unresolved;
        }

        /// <summary>
        /// Resolve component type-name strings to <see cref="Type"/>s. A name matches a type by
        /// <c>type.Name</c> OR <c>type.FullName</c> over <see cref="TypeCache.GetTypesDerivedFrom{T}"/>
        /// (Component), plus an exact full-name lookup across loaded assemblies as a backstop. A name
        /// resolving to MORE THAN ONE distinct type across loaded assemblies FAILS LOUD — it lands in
        /// <see cref="TypeResolution.unresolved"/> annotated with the candidates (disambiguate with the
        /// full name). Names resolve without the package referencing MA/VRCF/NDMF assemblies because
        /// TypeCache scans every loaded assembly.
        /// </summary>
        public static TypeResolution ResolveTypes(string[] names)
        {
            var result = new TypeResolution
            {
                resolved = new List<Type>(),
                unresolved = new List<string>(),
            };
            if (names == null) return result;

            var components = TypeCache.GetTypesDerivedFrom<Component>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var raw in names)
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name)) { result.unresolved.Add(raw ?? "(null)"); continue; }

                var matches = new HashSet<Type>();
                foreach (var t in components)
                    if (t.Name == name || t.FullName == name) matches.Add(t);

                // Exact full-name backstop (a full-name-only Component type TypeCache might not surface;
                // Reflection's GetType requires a namespace-qualified name, so simple names are always
                // carried by TypeCache above, never this branch).
                foreach (var asm in assemblies)
                {
                    Type t = null;
                    try { t = asm.GetType(name, false); } catch { /* malformed name → skip */ }
                    if (t != null && typeof(Component).IsAssignableFrom(t)) matches.Add(t);
                }

                if (matches.Count == 1)
                {
                    foreach (var m in matches) result.resolved.Add(m);
                }
                else if (matches.Count == 0)
                {
                    result.unresolved.Add(name + " (no matching Component type)");
                }
                else
                {
                    var fullNames = new List<string>();
                    foreach (var m in matches) fullNames.Add(m.FullName);
                    fullNames.Sort(StringComparer.Ordinal);
                    result.unresolved.Add(name + " (ambiguous: " + string.Join(", ", fullNames) + ")");
                }
            }
            return result;
        }

        // ── Shared RunLog / whatIf helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Write <paramref name="log"/> as JSON to <see cref="RunLogDir"/> (the package's RunLog
        /// envelope shape, with <c>kind</c> a parameter), refresh the asset DB, and return the path.
        /// <paramref name="label"/> is sanitized into the file name.
        /// </summary>
        public static string WriteRunLog(RunLog log, string label)
        {
            Directory.CreateDirectory(RunLogDir);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": ").Append(Q(log.kind)).Append(",\n");
            sb.Append("  \"unityVersion\": ").Append(Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(log.whatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"instance\": ").Append(Q(log.instance)).Append(",\n");
            sb.Append("  \"source\": ").Append(Q(log.source)).Append(",\n");
            sb.Append("  \"result\": ").Append(Q(log.result)).Append(",\n");
            sb.Append("  \"error\": ").Append(Q(log.error)).Append(",\n");

            foreach (var kv in log.counts)
                sb.Append("  ").Append(Q(kv.Key)).Append(": ").Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(",\n");

            AppendStringArray(sb, "offenders", log.offenders);
            sb.Append(",\n");
            AppendStringArray(sb, "notes", log.notes);
            sb.Append(",\n");
            AppendStringArray(sb, "warnings", log.warnings);

            foreach (var kv in log.sections)
                sb.Append(",\n  ").Append(Q(kv.Key)).Append(": ").Append(kv.Value);

            sb.Append("\n}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = RunLogDir + "/" + Sanitize(log.kind) + "_" + Sanitize(label) + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        /// <summary>
        /// One-line PASS/FAIL summary: <c>[kind] label: k1=v1, k2=v2 offenders=[...] => RESULT</c>.
        /// Reusable by every tool's Run.
        /// </summary>
        public static string Summary(RunLog log, string label)
        {
            var counts = new StringBuilder();
            for (int i = 0; i < log.counts.Count; i++)
            {
                if (i > 0) counts.Append(", ");
                counts.Append(log.counts[i].Key).Append('=').Append(log.counts[i].Value.ToString(CultureInfo.InvariantCulture));
            }

            string offenders = log.offenders.Count > 0
                ? " offenders=[" + string.Join("; ", log.offenders) + "]"
                : "";
            // notes = loud force-override records; warnings = named-but-still-PASS signals (e.g. "stale
            // move?"). Surfaced on the one-line verdict so they don't hide in the RunLog JSON.
            string notes = log.notes.Count > 0
                ? " notes=[" + string.Join("; ", log.notes) + "]"
                : "";
            string warnings = log.warnings.Count > 0
                ? " warnings=[" + string.Join("; ", log.warnings) + "]"
                : "";
            string error = log.error != null ? " error=" + log.error : "";
            string whatIf = log.whatIf ? " (whatIf)" : "";

            return string.Format(CultureInfo.InvariantCulture,
                "[{0}]{1} {2}: {3}{4}{5}{6}{7} => {8}",
                log.kind, whatIf, label, counts, offenders, notes, warnings, error, log.result);
        }

        /// <summary>
        /// Write the RunLog, build the one-line summary with the RunLog path folded onto its tail,
        /// log it at the right severity (PASS → Log, else LogError), and return the summary. The
        /// single tail every transplant tool's Run funnels through — hoisted out of the three
        /// byte-identical local copies (CopyComponents / MoveComponents / GraftHierarchy).
        /// </summary>
        public static string Finish(RunLog log, string label)
        {
            log.EnsureFailHasOffender();
            string path = WriteRunLog(log, label);
            string summary = Summary(log, label) + " | log=" + path;
            if (log.result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        /// <summary>
        /// Vendor-leak sweep, shared by every tool that creates copied components: any ObjectReference on
        /// a created component still pointing INTO the vendor source (<paramref name="vendorRoot"/> or a
        /// descendant) is a ref the remap failed to rebind. Each is recorded as a named offender on
        /// <paramref name="log"/>; the leak count is returned so the caller can fold it into its PASS/FAIL
        /// verdict (a non-zero count is a real tool fault). Null entries in <paramref name="created"/> are
        /// skipped.
        /// </summary>
        public static int SweepVendorLeaks(IEnumerable<Component> created, Transform vendorRoot, RunLog log)
        {
            int leaks = 0;
            foreach (var ours in created)
            {
                if (ours == null) continue;
                var lso = new SerializedObject(ours);
                var it  = lso.GetIterator();
                while (it.Next(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var o = it.objectReferenceValue;
                    if (o == null) continue;
                    Transform t = o is Component oc ? oc.transform : (o is GameObject og ? og.transform : null);
                    if (t != null && (t == vendorRoot || t.IsChildOf(vendorRoot)))
                    {
                        leaks++;
                        log.Offender(ours.GetType().Name + " '" + ours.gameObject.name +
                            "': ref '" + it.propertyPath + "' still points into the vendor source (leak)");
                    }
                }
            }
            return leaks;
        }

        /// <summary>
        /// Recursively ensure every segment of an Assets/-relative folder path exists, via
        /// <see cref="AssetDatabase.CreateFolder"/> per segment — a plain
        /// <c>Directory.CreateDirectory</c> would leave the AssetDatabase blind to the new folders
        /// until an import. The single copy the folder-creating tools share.
        /// </summary>
        public static void EnsureFolderExists(string assetPath)
        {
            assetPath = assetPath.TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            int slash = assetPath.LastIndexOf('/');
            if (slash < 0) return;
            string parent = assetPath.Substring(0, slash);
            string leaf = assetPath.Substring(slash + 1);
            EnsureFolderExists(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static void AppendStringArray(StringBuilder sb, string key, List<string> items)
        {
            sb.Append("  ").Append(Q(key)).Append(": [");
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    ").Append(Q(items[i]));
            }
            sb.Append(items.Count > 0 ? "\n  ]" : "]");
        }

        // ── Helpers — package RunLog convention; canonical impl: agent-tools RunLogFormat ───────────

        public static string Sanitize(string s) => RunLogFormat.Sanitize(s);

        public static string Q(string s) => RunLogFormat.Q(s);

        /// <summary>Path basename. Null or empty → "". Otherwise <c>TrimEnd('/')</c> then the substring after
        /// the last '/' (the whole trimmed string if none). The RunLog-label leaf helper the tools converge on.</summary>
        public static string Leaf(string assetPath) => RunLogFormat.Leaf(assetPath);
    }

    /// <summary>
    /// Identity maps recorded as the engine copies in dependency order: vendor component → our copy,
    /// and vendor transform → our transform (including recreated/scaffolded GOs). Every reference on
    /// every copy is later rebound by: map-hit → mapped target; else path-remap; else flag.
    /// </summary>
    public sealed class SessionMap
    {
        readonly Dictionary<Component, Component> components = new Dictionary<Component, Component>();
        readonly Dictionary<Transform, Transform> transforms = new Dictionary<Transform, Transform>();

        public int ComponentCount => components.Count;
        public int TransformCount => transforms.Count;

        public void AddComponent(Component vendor, Component ours)
        {
            if (vendor != null && ours != null) components[vendor] = ours;
        }

        public void AddTransform(Transform vendor, Transform ours)
        {
            if (vendor != null && ours != null) transforms[vendor] = ours;
        }

        public bool TryGetComponent(Component vendor, out Component ours)
        {
            ours = null;
            return vendor != null && components.TryGetValue(vendor, out ours);
        }

        public bool TryGetTransform(Transform vendor, out Transform ours)
        {
            ours = null;
            return vendor != null && transforms.TryGetValue(vendor, out ours);
        }
    }

    /// <summary>
    /// Mints a minimal transforms-only GameObject chain from the nearest existing mapped ancestor down
    /// to a target host, reusing any GO already present at the target indexed path (never duplicating),
    /// bounded by the destination reach root. The depth-N generalization of the depth-1
    /// leaf-anchor recreate (used by leaf-recreate, <c>force</c>, and graft). Each created GO gets the
    /// vendor's verbatim LOCAL position/rotation/scale and is registered with
    /// <see cref="Undo.RegisterCreatedObjectUndo"/>.
    /// </summary>
    public static class ScaffoldBuilder
    {
        /// <summary>
        /// Ensure the destination host for <paramref name="vendorHost"/> exists under
        /// <paramref name="ourRoot"/> (the destination reach root) and return it. Walks the vendor's
        /// root→host segment chain; at each level reuses the dst child at the same indexed path or mints
        /// a transforms-only GO with the vendor segment's verbatim local TRS. Records every level into
        /// <paramref name="session"/> (vendor transform → dst transform). Returns <paramref name="ourRoot"/>
        /// when the host IS the vendor root; null when out of reach or unrepresentable — see the overload
        /// taking <c>out string failReason</c> for the named reason (callers surface it).
        /// </summary>
        public static Transform EnsureHost(Transform vendorRoot, Transform ourRoot, Transform vendorHost,
                                           SessionMap session = null, string undoName = "Transplant",
                                           IDictionary<string, string> vendorToOwned = null)
            => EnsureHost(vendorRoot, ourRoot, vendorHost, out _, session, undoName, vendorToOwned);

        /// <summary>
        /// As <see cref="EnsureHost(Transform,Transform,Transform,SessionMap,string)"/>, but on a null
        /// return sets <paramref name="failReason"/> to a named reason the caller folds into its offender
        /// message. Null is returned (and a reason set) when:
        ///   - an argument is null, or <paramref name="vendorHost"/> is not under <paramref name="vendorRoot"/>
        ///     (out of reach); OR
        ///   - a chain level's occurrence index is UNREPRESENTABLE on the dst: the dst parent currently has
        ///     FEWER same-named children than the vendor segment's occurrence index, so minting here would
        ///     land the GO at a LOWER occurrence than vendor[idx]. Recording that into the SessionMap would
        ///     make a re-run's indexed-path recompute mismatch (re-scaffold a duplicate / bind the wrong
        ///     sibling). Minting is correct ONLY when the existing same-name count == idx (append at exactly
        ///     occurrence idx); we FAIL LOUD rather than mint at the wrong index.
        /// </summary>
        public static Transform EnsureHost(Transform vendorRoot, Transform ourRoot, Transform vendorHost,
                                           out string failReason,
                                           SessionMap session = null, string undoName = "Transplant",
                                           IDictionary<string, string> vendorToOwned = null)
        {
            failReason = null;
            if (vendorRoot == null || ourRoot == null || vendorHost == null)
            {
                failReason = "null vendorRoot/ourRoot/vendorHost argument";
                return null;
            }
            if (vendorHost == vendorRoot) return ourRoot;
            if (!vendorHost.IsChildOf(vendorRoot))
            {
                failReason = "vendor host is not under the vendor reach root (out of reach)";
                return null;
            }

            // Root-exclusive vendor chain, top → host.
            var chain = new List<Transform>();
            for (var p = vendorHost; p != null && p != vendorRoot; p = p.parent) chain.Add(p);
            chain.Reverse();

            Transform curDst = ourRoot;
            foreach (var seg in chain)
            {
                // Destination lookup/mint uses the MAPPED name (vendorToOwned: vendorName ⇒ ownedName); the
                // occurrence index is counted on the SOURCE side in the SAME resolving-to-mapped space as the
                // dest lookup (symmetric with the A1 guard), so a mapped-key child and a literal-mapped sibling
                // never collapse onto one dest occurrence. Null/non-funneling map ⇒ mapped == seg.name and this
                // reduces to same-name indexing — byte-identical to today.
                string mapped = IndexedPath.Substitute(seg.name, vendorToOwned);
                int idx = IndexedPath.SiblingIndexAmongResolvingTo(seg, mapped, vendorToOwned);

                // A1 count-equality guard (shared with FindByIndexedPath): a mapped value already present on
                // the dst (or a non-injective slip) could silently bind the WRONG sibling → fail loud instead.
                if (!IndexedPath.GuardRename(seg.parent, curDst, mapped, vendorToOwned, out failReason)) return null;

                var existing = IndexedPath.NthChildWithName(curDst, mapped, idx);
                if (existing != null)
                {
                    curDst = existing;
                }
                else
                {
                    // existing == null guarantees curDst has <= idx same-name (mapped) children. Minting is
                    // correct ONLY when the count == idx (append at exactly occurrence idx). count < idx is an
                    // unrepresentable occurrence (the new GO would land below vendor[idx]) → FAIL LOUD.
                    int count = IndexedPath.CountChildrenWithName(curDst, mapped);
                    if (count != idx)
                    {
                        failReason = "cannot represent occurrence index " + idx + " of '" + mapped +
                                     "' under '" + curDst.name + "' (only " + count +
                                     " same-named child(ren) exist — minting would land at occurrence " +
                                     count + ", mismatching the vendor's; re-runs would diverge)";
                        return null;
                    }
                    // Mint with the MAPPED name so a minted-then-reused dst hierarchy is self-consistent
                    // (a re-run reuses 'Armature.1', never mints a parallel 'Armature').
                    var go = new GameObject(mapped);
                    Undo.RegisterCreatedObjectUndo(go, undoName);
                    go.transform.SetParent(curDst, false);
                    go.transform.localPosition = seg.localPosition;
                    go.transform.localRotation = seg.localRotation;
                    go.transform.localScale = seg.localScale;
                    curDst = go.transform;
                }
                session?.AddTransform(seg, curDst);
            }
            return curDst;
        }
    }

    /// <summary>
    /// Mutable accumulator for one tool run, serialized by <see cref="TransplantCore.WriteRunLog"/> and
    /// summarized by <see cref="TransplantCore.Summary"/>. <see cref="counts"/> preserves insertion order
    /// so the JSON and one-line summary read in a stable, tool-defined order. Unsealed so a tool can
    /// subclass to carry its bespoke structured rows alongside the envelope (see <see cref="Section"/>).
    /// </summary>
    public class RunLog
    {
        public string kind;
        public bool whatIf;
        public string instance;
        public string source;
        public string result = "PASS";
        public string error;
        public readonly List<KeyValuePair<string, long>> counts = new List<KeyValuePair<string, long>>();
        public readonly List<string> offenders = new List<string>();
        public readonly List<string> notes = new List<string>();
        public readonly List<string> warnings = new List<string>();

        public RunLog(string kind) { this.kind = kind; }

        /// <summary>Bespoke structured JSON sections (e.g. OwnMaterial's <c>slots[]</c> table), emitted
        /// verbatim after <see cref="warnings"/> in insertion order. The value is PRE-RENDERED JSON the
        /// tool builds with <see cref="TransplantCore.Q"/> — what you pass is exactly what lands, so the
        /// envelope writer stays logic-free. Sections never appear in the one-line summary.</summary>
        public readonly List<KeyValuePair<string, string>> sections = new List<KeyValuePair<string, string>>();

        public void Count(string name, long value) => counts.Add(new KeyValuePair<string, long>(name, value));
        public void Offender(string msg) { if (!string.IsNullOrEmpty(msg)) offenders.Add(msg); }
        public void Note(string msg) { if (!string.IsNullOrEmpty(msg)) notes.Add(msg); }
        public void Warning(string msg) { if (!string.IsNullOrEmpty(msg)) warnings.Add(msg); }

        /// <summary>Attach a bespoke structured section: <paramref name="renderedJson"/> must be a complete
        /// JSON value (typically an array). Emitted as <c>"name": renderedJson</c> by
        /// <see cref="TransplantCore.WriteRunLog"/>.</summary>
        public void Section(string name, string renderedJson)
            => sections.Add(new KeyValuePair<string, string>(name, renderedJson));

        /// <summary>
        /// Enforce the reverse leg of the offenders⇔FAIL invariant: a FAIL with no named offender
        /// backfills one from <see cref="error"/>, so a FAIL can never be emitted offenderless.
        /// Called by <see cref="TransplantCore.Finish"/> — the single tail every tool's Run funnels
        /// through — so the exception catch, the arg-guards, and any future FAIL are all covered at
        /// one site.
        /// </summary>
        public void EnsureFailHasOffender()
        {
            if (result == "FAIL" && offenders.Count == 0)
                Offender("unnamed failure: " + (error ?? "no error detail"));
        }
    }
}
