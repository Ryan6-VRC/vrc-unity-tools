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
    /// Tool-agnostic primitives the avatar-tools writers share: the ownership predicate, asset copy,
    /// type-name resolution and the RunLog helpers.
    /// </summary>
    public static class TransplantCore
    {
        public const string RunLogDir = RunLogFormat.RunLogDir;

        // ── Ownership predicate ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// False for an asset under <c>Assets/Vendor</c> or <c>Packages</c>, matched at a segment boundary. A
        /// null or empty path is writable: an in-memory asset carries no vendor provenance. Provenance is a
        /// property of each <em>reference</em>, not of a folder (docs/LAYOUT.md), and an owned controller can
        /// reference vendor clips, so call this per asset, not per controller.
        /// </summary>
        public static bool IsWritableAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return true;
            var p = assetPath.Replace('\\', '/');
            return !(UnderSegment(p, "Assets/Vendor") || UnderSegment(p, "Packages"));
        }

        static bool UnderSegment(string path, string prefix)
            => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);

        /// <summary>
        /// Copy one asset without <see cref="AssetDatabase.CopyAsset"/>, which saves every dirty asset in the
        /// project. Saves only the source, preserves its importer settings under a fresh GUID, and refuses a
        /// package destination.
        /// </summary>
        public static bool CopyAssetFile(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationPath)) return false;
            if (UnderSegment(destinationPath.Replace('\\', '/'), "Packages")) return false;
            string sourceFile = AssetFilePath(sourcePath);
            string destinationFile = AssetFilePath(destinationPath);
            string sourceMeta = sourceFile + ".meta";
            string destinationMeta = destinationFile + ".meta";
            if (!File.Exists(sourceFile) || File.Exists(destinationFile) || File.Exists(destinationMeta)) return false;
            var sourceAsset = AssetDatabase.LoadMainAssetAtPath(sourcePath);
            if (sourceAsset != null) AssetDatabase.SaveAssetIfDirty(sourceAsset);

            try
            {
                File.Copy(sourceFile, destinationFile);
                if (File.Exists(sourceMeta))
                {
                    string meta = File.ReadAllText(sourceMeta);
                    const string marker = "guid: ";
                    int guidStart = meta.IndexOf(marker, StringComparison.Ordinal);
                    if (guidStart < 0) throw new InvalidDataException("source meta has no guid");
                    guidStart += marker.Length;
                    int cr = meta.IndexOf('\r', guidStart);
                    int lf = meta.IndexOf('\n', guidStart);
                    int guidEnd = cr < 0 ? lf : lf < 0 ? cr : Math.Min(cr, lf);
                    if (guidEnd < 0) guidEnd = meta.Length;
                    meta = meta.Substring(0, guidStart) + Guid.NewGuid().ToString("N") + meta.Substring(guidEnd);
                    File.WriteAllText(destinationMeta, meta);
                }
                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport);
                if (AssetDatabase.LoadMainAssetAtPath(destinationPath) != null) return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CopyAssetFile] failed to copy '" + sourcePath + "' to '" + destinationPath
                    + "': " + ex.GetType().Name + ": " + ex.Message);
            }

            if (!AssetDatabase.DeleteAsset(destinationPath))
            {
                if (File.Exists(destinationFile)) File.Delete(destinationFile);
                if (File.Exists(destinationMeta)) File.Delete(destinationMeta);
            }
            return false;
        }

        static string AssetFilePath(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            if (!UnderSegment(normalized, "Packages")) return Path.GetFullPath(assetPath);

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalized);
            if (package == null)
            {
                int slash = normalized.IndexOf('/', "Packages/".Length);
                string packageName = slash < 0 ? normalized.Substring("Packages/".Length)
                                               : normalized.Substring("Packages/".Length, slash - "Packages/".Length);
                foreach (var candidate in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                {
                    if (candidate.name == packageName) { package = candidate; break; }
                }
            }
            if (package == null || string.IsNullOrEmpty(package.resolvedPath)) return Path.GetFullPath(assetPath);

            string prefix = "Packages/" + package.name;
            string relative = normalized.Length == prefix.Length
                ? ""
                : normalized.Substring(prefix.Length + 1);
            return Path.Combine(package.resolvedPath, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        // ── Type-name resolution ──────────────────────────────────────────────────────────────────

        /// <summary>Outcome of <see cref="ResolveTypes"/>: the resolved types plus the names that
        /// could not be resolved to exactly one type (unknown or ambiguous).</summary>
        public struct TypeResolution
        {
            public List<Type> resolved;
            public List<string> unresolved;
        }

        /// <summary>
        /// Resolve component type names by <c>type.Name</c> or <c>type.FullName</c>. TypeCache scans every
        /// loaded assembly, so MA, VRCF and NDMF names resolve without the package referencing them. A name
        /// matching more than one type lands in <see cref="TypeResolution.unresolved"/> with its candidates;
        /// disambiguate with the full name.
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

                // Exact full-name backstop for a Component type TypeCache might not surface.
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
            RunLogFormat.PublishArtifact(RunLogDir, path);
            return path;
        }

        /// <summary>One-line PASS/FAIL summary: <c>[kind] label: k1=v1, k2=v2 offenders=[...] => RESULT</c>.</summary>
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
            // A warning names a signal without failing the run. Notes and warnings reach the one-line verdict so
            // they don't hide in the RunLog JSON.
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
        /// The tail of a tool's Run: writes the RunLog, logs the summary with the RunLog path appended (PASS →
        /// Log, else LogError), and returns it.
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
        /// Records as an offender every ObjectReference on a created component still pointing into
        /// <paramref name="vendorRoot"/> or a descendant, a ref the remap failed to rebind. Returns the leak
        /// count and leaves <see cref="RunLog.result"/> alone: the caller folds the count into its verdict.
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
        /// Create every missing segment of an Assets/-relative folder path via
        /// <see cref="AssetDatabase.CreateFolder"/>; a plain <c>Directory.CreateDirectory</c> would leave the
        /// AssetDatabase blind to the new folders until an import.
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

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

        public static string Sanitize(string s) => RunLogFormat.Sanitize(s);

        public static string Q(string s) => RunLogFormat.Q(s);

        public static string Leaf(string assetPath) => RunLogFormat.Leaf(assetPath);
    }

    /// <summary>
    /// Identity maps recorded during a copy: vendor component → our copy, and vendor transform → our
    /// transform, scaffolded GameObjects included. A hit outranks path remap when references rebind.
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
    /// Mints the minimal transforms-only GameObject chain from the nearest existing ancestor down to a
    /// target host, reusing any GameObject already at the target indexed path.
    /// </summary>
    public static class ScaffoldBuilder
    {
        /// <summary>
        /// Ensure the destination host for <paramref name="vendorHost"/> exists under
        /// <paramref name="ourRoot"/> and return it, recording every chain level into
        /// <paramref name="session"/>. Each level reuses the dst child at the same indexed path or mints one
        /// with the vendor segment's verbatim local TRS. Null when out of reach or unrepresentable; the
        /// overload taking <c>out string failReason</c> names the reason.
        /// </summary>
        public static Transform EnsureHost(Transform vendorRoot, Transform ourRoot, Transform vendorHost,
                                           SessionMap session = null, string undoName = "Transplant",
                                           IDictionary<string, string> vendorToOwned = null)
            => EnsureHost(vendorRoot, ourRoot, vendorHost, out _, session, undoName, vendorToOwned);

        /// <summary>
        /// As <see cref="EnsureHost(Transform,Transform,Transform,SessionMap,string,IDictionary{string,string})"/>,
        /// but on a null return sets <paramref name="failReason"/>. A level mints only when the dst parent's
        /// same-name count equals the vendor segment's occurrence index. A lower count fails loud: the GameObject
        /// would land below its vendor occurrence, and a re-run's indexed-path recompute would re-scaffold a
        /// duplicate or bind the wrong sibling.
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

            var chain = new List<Transform>();
            for (var p = vendorHost; p != null && p != vendorRoot; p = p.parent) chain.Add(p);
            chain.Reverse();

            Transform curDst = ourRoot;
            foreach (var seg in chain)
            {
                string mapped = IndexedPath.Substitute(seg.name, vendorToOwned);
                int idx = IndexedPath.SiblingIndexAmongResolvingTo(seg, mapped, vendorToOwned);

                if (!IndexedPath.GuardRename(seg.parent, curDst, mapped, vendorToOwned, out failReason)) return null;

                var existing = IndexedPath.NthChildWithName(curDst, mapped, idx);
                if (existing != null)
                {
                    curDst = existing;
                }
                else
                {
                    int count = IndexedPath.CountChildrenWithName(curDst, mapped);
                    if (count != idx)
                    {
                        failReason = "cannot represent occurrence index " + idx + " of '" + mapped +
                                     "' under '" + curDst.name + "' (only " + count +
                                     " same-named child(ren) exist — minting would land at occurrence " +
                                     count + ", mismatching the vendor's; re-runs would diverge)";
                        return null;
                    }
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
    /// Accumulator for one tool run, serialized by <see cref="TransplantCore.WriteRunLog"/> and summarized by
    /// <see cref="TransplantCore.Summary"/>. A tool subclasses it to carry bespoke fields, and emits structured
    /// rows through <see cref="Section"/>.
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

        public readonly List<KeyValuePair<string, string>> sections = new List<KeyValuePair<string, string>>();

        public void Count(string name, long value) => counts.Add(new KeyValuePair<string, long>(name, value));
        public void Offender(string msg) { if (!string.IsNullOrEmpty(msg)) offenders.Add(msg); }
        public void Note(string msg) { if (!string.IsNullOrEmpty(msg)) notes.Add(msg); }
        public void Warning(string msg) { if (!string.IsNullOrEmpty(msg)) warnings.Add(msg); }

        /// <summary>Attach a section, emitted verbatim after <see cref="warnings"/> as
        /// <c>"name": renderedJson</c>. <paramref name="renderedJson"/> must be a complete JSON value, built with
        /// <see cref="TransplantCore.Q"/>. Sections never reach the one-line summary.</summary>
        public void Section(string name, string renderedJson)
            => sections.Add(new KeyValuePair<string, string>(name, renderedJson));

        /// <summary>
        /// A FAIL with no named offender backfills one from <see cref="error"/>, so no FAIL is emitted
        /// offenderless. <see cref="TransplantCore.Finish"/> calls this; a tool that bypasses it must too.
        /// </summary>
        public void EnsureFailHasOffender()
        {
            if (result == "FAIL" && offenders.Count == 0)
                Offender("unnamed failure: " + (error ?? "no error detail"));
        }
    }
}
