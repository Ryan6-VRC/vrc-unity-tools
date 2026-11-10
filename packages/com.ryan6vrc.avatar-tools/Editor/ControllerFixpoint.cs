using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Ryan6Vrc.AvatarTools.Editor
{
    // The vrc-patterns library gate: does committed built/ match what the compiler emits from its yaml
    // today? Contract and passes: vrc-patterns/CONVENTIONS.md §The gate; tools/gate.ps1 there drives
    // RunGate. decode(c) = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(c).Doc), the same
    // canonical string the fixpoint tests trust.
    //
    // built/ is generated and nobody hand-maintains it, so a field that matters grows the schema and never
    // tightens this comparison. Do not add a check guarding hand-authored content in built/; CONVENTIONS.md
    // routes a menu the schema cannot express to assets/. ForeignProjectPathLines is not that exception: it
    // reads the committed prefab, which is source.
    //
    // Drift is decompile-equality, never a byte diff: Unity assigns .controller sub-asset fileIDs
    // non-deterministically, so two compiles of one yaml differ in bytes.
    //
    // Decompile-equality cannot reach the two flat assets, because a .controller stores no menu and neither
    // flag the params filter reads, so each has a structural pass: *_Menu.asset (MenuPresence/MenuDiff) and
    // *_Parameters.asset (ParamsPresence/ParamsDiff). Changing ControllerRules.VrcReservedParams here
    // changes built/*_Parameters.asset in vrc-patterns, a different repo, and nothing routes the author to
    // that second commit.
    public static class ControllerFixpoint
    {
        static string Decode(AnimatorController c, out string refusal)
        {
            var w = ControllerDecompile.Walk(c);
            if (w.Refusals != null && w.Refusals.Count != 0) { refusal = string.Join("; ", w.Refusals); return null; }
            refusal = null;
            return AnimatorSchemaEmit.Serialize(w.Doc);
        }

        static string ToAssetsRelative(string abs)
        {
            var proj = Directory.GetCurrentDirectory().Replace('\\', '/');
            abs = Path.GetFullPath(abs).Replace('\\', '/');
            return abs.StartsWith(proj + "/", StringComparison.Ordinal) ? abs.Substring(proj.Length + 1) : abs;
        }

        // Filesystem delete only, no DeleteAsset and no Refresh: Check holds loaded AnimatorControllers
        // from this scratch as live locals through the finally, and an AssetDatabase reconcile while those
        // refs pin an asset whose folder just vanished makes Unity re-materialize an empty husk renamed
        // "<name> 1".
        static void CleanupScratch(string scratchAssetsPath)
        {
            var full = Path.GetFullPath(scratchAssetsPath);
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }

        // Deletes every scratch dir + .meta this tool creates, "<name> 1" husks included. Filesystem only,
        // and called only while no scratch asset is referenced, so no reconcile can resurrect a husk.
        // Serial venue: nothing live owns these prefixes.
        static void SweepScratch()
        {
            var assetsRoot = Path.GetFullPath("Assets");
            if (!Directory.Exists(assetsRoot)) return;
            foreach (var stale in Directory.GetDirectories(assetsRoot, "_fixpoint_*")
                     .Concat(Directory.GetDirectories(assetsRoot, "_prefab_*")))
                try { Directory.Delete(stale, true); } catch { /* best-effort */ }
            foreach (var staleMeta in Directory.GetFiles(assetsRoot, "_fixpoint_*.meta")
                     .Concat(Directory.GetFiles(assetsRoot, "_prefab_*.meta")))
                try { File.Delete(staleMeta); } catch { /* best-effort */ }
        }

        static AnimatorController CompileToTemp(string yamlPath, string tempAssetsDir)
        {
            Directory.CreateDirectory(Path.GetFullPath(tempAssetsDir));
            var msg = CompileController.Run(yamlPath, ToAssetsRelative(tempAssetsDir));
            if (msg == null || msg.IndexOf("=> OK", StringComparison.Ordinal) < 0)
                throw new Exception("compile failed: " + msg);
            AssetDatabase.Refresh();
            var ctrl = Directory.GetFiles(Path.GetFullPath(tempAssetsDir), "*.controller").FirstOrDefault();
            if (ctrl == null) throw new Exception("no .controller emitted into " + tempAssetsDir);
            var loaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(ToAssetsRelative(ctrl));
            if (loaded == null) throw new Exception("emitted .controller failed to load: " + ctrl);
            return loaded;
        }

        // Copies the named file + .meta into Assets/ so it imports with its committed GUID, then loads it:
        // LoadAssetAtPath resolves nothing at an arbitrary --root path outside the project.
        //
        // Only the named file: a multi-artifact entry's siblings would otherwise import their committed
        // GUIDs once per checked document, colliding across scratch dirs. Assumes the package under test is
        // not also loaded in this host, which is the same collision by another route.
        static T ImportCommittedAsset<T>(string committedPath, string destAssetsDir) where T : UnityEngine.Object
        {
            var full = Path.GetFullPath(destAssetsDir);
            Directory.CreateDirectory(full);
            var src = Path.GetFullPath(committedPath);
            var metaComplaint = MissingCommittedMeta(src);
            if (metaComplaint != null) throw new Exception(metaComplaint);
            File.Copy(src, Path.Combine(full, Path.GetFileName(src)), true);
            File.Copy(src + ".meta", Path.Combine(full, Path.GetFileName(src) + ".meta"), true);
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<T>(ToAssetsRelative(Path.Combine(full, Path.GetFileName(src))));
        }

        // The complaint when a committed artifact has no .meta beside it, or null when it does. Asserted
        // before the copy, because afterward a fresh-GUID import is indistinguishable from a correct one.
        internal static string MissingCommittedMeta(string committedPath)
        {
            if (File.Exists(committedPath + ".meta")) return null;
            return "committed artifact has no .meta beside it: " + committedPath
                + " — importing it would mint a fresh GUID, so this gate would PASS while every reference to "
                + "the artifact dangles. Restore the .meta (git checkout / recover the deletion). Do NOT "
                + "regenerate built/ to fix this: regeneration mints a new GUID too, which turns this gate "
                + "green on the very break it just caught.";
        }

        // yamlPath: filesystem path to a controller document. builtControllerPath: filesystem path to its
        // committed built .controller, or null when the entry ships none.
        public static (bool ok, string msg) Check(string yamlPath, string builtControllerPath)
        {
            var scratch = "Assets/_fixpoint_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                // No round-trip pass here: a compile/decompile inverse break is a tool bug no entry can
                // fix, owned by FixpointOracle + FixpointAcceptanceTests + RoundtripStressTests.
                var cFresh = CompileToTemp(yamlPath, scratch + "/a");
                var yFresh = Decode(cFresh, out var r1);
                if (yFresh == null) return (false, "fresh decompile refused: " + r1);

                if (builtControllerPath != null)
                {
                    var cCommitted = ImportCommittedAsset<AnimatorController>(builtControllerPath, scratch + "/committed");
                    if (cCommitted == null) return (false, "committed controller failed to import");
                    var yCommitted = Decode(cCommitted, out var r3);
                    if (yCommitted == null) return (false, "committed decompile refused: " + r3);
                    if (yCommitted != yFresh) return (false, "committed built/ differs from compile(yaml) — regenerate built/");
                }

                // A .controller stores no menu, so the decompile-equality above cannot see one.
                if (builtControllerPath != null)
                {
                    var freshMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(
                        MenuBeside(AssetDatabase.GetAssetPath(cFresh)));
                    var committedMenuPath = MenuBeside(builtControllerPath);
                    bool committedExists = File.Exists(committedMenuPath);

                    var (pass, presenceMsg) = MenuPresence(freshMenu, committedExists);
                    if (pass == MenuPass.Fail) return (false, presenceMsg);
                    if (pass == MenuPass.Compare)
                    {
                        var imported = ImportCommittedAsset<VRCExpressionsMenu>(committedMenuPath, scratch + "/menu");
                        if (imported == null) return (false, "committed menu asset failed to import");
                        var diff = MenuDiff(imported, freshMenu, "menu");
                        if (diff != null) return (false, "committed built/ menu differs from compile(yaml): " + diff + " — regenerate built/");
                    }
                }

                // The params asset is emitted from flags (`scratch:`, VRC-reserved) a .controller does not
                // carry.
                if (builtControllerPath != null)
                {
                    var freshParams = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(
                        ParamsBeside(AssetDatabase.GetAssetPath(cFresh)));
                    var committedParamsPath = ParamsBeside(builtControllerPath);

                    var (pPass, pMsg) = ParamsPresence(freshParams, File.Exists(committedParamsPath));
                    if (pPass == MenuPass.Fail) return (false, pMsg);
                    if (pPass == MenuPass.Compare)
                    {
                        var importedParams = ImportCommittedAsset<VRCExpressionParameters>(committedParamsPath, scratch + "/params");
                        if (importedParams == null) return (false, "committed params asset failed to import");
                        var pDiff = ParamsDiff(importedParams, freshParams, "params");
                        if (pDiff != null)
                            return (false, "committed built/ params differ from compile(yaml): " + pDiff +
                                           " — regenerating built/ is the fix ONLY if the yaml's own parameter list " +
                                           "changed on purpose. If a `scratch:` flag or ControllerRules.VrcReservedParams " +
                                           "changed, settle that first: regenerating blind commits it as though reviewed");
                    }
                }
                return (true, "OK");
            }
            catch (Exception e) { return (false, e.Message); }
            finally { CleanupScratch(scratch); }
        }

        internal enum MenuPass { Skip, Compare, Fail }

        // Which of the three things Check does about a menu, decided from presence alone: neither side has
        // one (Skip), both do (Compare the trees), or exactly one does (Fail).
        internal static (MenuPass pass, string msg) MenuPresence(VRCExpressionsMenu freshMenu, bool committedExists)
        {
            bool freshEmits = freshMenu != null;
            if (!freshEmits && committedExists)
                return (MenuPass.Fail, "built/ ships a menu asset the yaml no longer emits — delete it or restore the menu: block");
            if (freshEmits && !committedExists)
                return (MenuPass.Fail, "yaml emits a menu but built/ has none — regenerate built/");
            return (freshEmits ? MenuPass.Compare : MenuPass.Skip, null);
        }

        // CompileController's "<dir>/<name>_Menu.asset" formula, re-derived rather than shared; path
        // arithmetic only, so the file need not exist. Two suites keep the copies in step:
        // ControllerFixpointTests pins this one and MenuEmitTests the compiler's, so drift in either goes
        // red.
        internal static string MenuBeside(string controllerPath)
        {
            var dir = Path.GetDirectoryName(controllerPath) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(controllerPath) + "_Menu.asset")
                       .Replace('\\', '/');
        }

        // CompileController's "<dir>/<name>_Parameters.asset" formula, re-derived as MenuBeside's is. Not
        // one AssetBeside(path, suffix) shared with MenuBeside: each formula keeps its own pin against
        // literals.
        internal static string ParamsBeside(string controllerPath)
        {
            var dir = Path.GetDirectoryName(controllerPath) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(controllerPath) + "_Parameters.asset")
                       .Replace('\\', '/');
        }

        // Structural comparison of two menu trees, recursing sub-menus: the two assets carry different file
        // GUIDs and sub-asset fileIDs by construction, so a byte compare would report drift on every run.
        // Returns null when equal, else the first difference, addressed by its page path.
        //
        // Every serialized field is compared because a menu has no decoded intermediate: the fields are the
        // comparison.
        //
        // Three legs are compared but not covered, and no test pins them. `icon`: an entry's icon lives in
        // that entry's assets/, which the gate host does not load, so both sides resolve to no asset;
        // closing it means importing the entry dir before the compile pass, the way the prefab pass does.
        // `style` and `labels`: the schema cannot author either (animator-schema.md §menu).
        //
        // Known defect, shared with ParamsDiff's defaultValue: `value` is compared with `!=`, so two NaNs
        // report a difference no "regenerate built/" can clear, and nothing on AnimatorSchemaYaml's
        // ToNumber path rejects `value: NaN`. The fix is !x.value.Equals(y.value) (RepathClips' FloatEq has
        // the idiom), and it changes what the gate admits.
        internal static string MenuDiff(VRCExpressionsMenu a, VRCExpressionsMenu b, string where)
        {
            if (a.name != b.name)
                return $"{where}: page name '{a.name}' vs '{b.name}'";
            var ac = a.controls ?? new System.Collections.Generic.List<VRCExpressionsMenu.Control>();
            var bc = b.controls ?? new System.Collections.Generic.List<VRCExpressionsMenu.Control>();
            if (ac.Count != bc.Count)
                return $"{where}: committed has {ac.Count} control(s), compiled has {bc.Count}";

            for (int i = 0; i < ac.Count; i++)
            {
                var x = ac[i]; var y = bc[i];
                string w = $"{where}[{i}]";
                if (x.name != y.name) return $"{w}: name '{x.name}' vs '{y.name}'";
                w = $"{where} '{x.name}'";
                if (x.type != y.type) return $"{w}: type {x.type} vs {y.type}";
                if ((x.parameter?.name ?? "") != (y.parameter?.name ?? ""))
                    return $"{w}: parameter '{x.parameter?.name}' vs '{y.parameter?.name}'";
                if (x.value != y.value) return $"{w}: value {x.value} vs {y.value}";
                if (x.style != y.style) return $"{w}: style {x.style} vs {y.style}";
                if (AssetDatabase.GetAssetPath(x.icon) != AssetDatabase.GetAssetPath(y.icon))
                    return $"{w}: icon '{AssetDatabase.GetAssetPath(x.icon)}' vs '{AssetDatabase.GetAssetPath(y.icon)}' — regenerate built/";

                var xl = x.labels ?? new VRCExpressionsMenu.Control.Label[0];
                var yl = y.labels ?? new VRCExpressionsMenu.Control.Label[0];
                if (xl.Length != yl.Length) return $"{w}: {xl.Length} label(s) vs {yl.Length}";
                for (int k = 0; k < xl.Length; k++)
                    if ((xl[k].name ?? "") != (yl[k].name ?? ""))
                        return $"{w}: label[{k}] '{xl[k].name}' vs '{yl[k].name}'";

                var xs = x.subParameters ?? new VRCExpressionsMenu.Control.Parameter[0];
                var ys = y.subParameters ?? new VRCExpressionsMenu.Control.Parameter[0];
                if (xs.Length != ys.Length) return $"{w}: {xs.Length} subParameter(s) vs {ys.Length}";
                for (int k = 0; k < xs.Length; k++)
                    if ((xs[k]?.name ?? "") != (ys[k]?.name ?? ""))
                        return $"{w}: subParameter[{k}] '{xs[k]?.name}' vs '{ys[k]?.name}'";

                if ((x.subMenu == null) != (y.subMenu == null))
                    return $"{w}: one side has a sub-menu and the other does not";
                if (x.subMenu != null)
                {
                    var deeper = MenuDiff(x.subMenu, y.subMenu, w);
                    if (deeper != null) return deeper;
                }
            }
            return null;
        }

        // The params equivalent of MenuPresence. The Skip leg is live: EmitVrcParameters early-returns when
        // every declared param is scratch: or VRC-reserved (anchor-prop/gesture.yaml).
        internal static (MenuPass pass, string msg) ParamsPresence(VRCExpressionParameters fresh, bool committedExists)
        {
            bool freshEmits = fresh != null;
            if (!freshEmits && committedExists)
                return (MenuPass.Fail, "built/ ships a params asset the yaml no longer emits — delete it if " +
                                       "those parameters were removed on purpose, or restore them if a `scratch:` " +
                                       "flag or the reserved-name set wrongly emptied the list");
            if (freshEmits && !committedExists)
                return (MenuPass.Fail, "yaml emits parameters but built/ has none — regenerating built/ is the " +
                                       "fix ONLY if the yaml's own parameter list changed on purpose. If a " +
                                       "`scratch:` flag or ControllerRules.VrcReservedParams changed, settle " +
                                       "that first: regenerating blind commits it as though reviewed");
            return (freshEmits ? MenuPass.Compare : MenuPass.Skip, null);
        }

        // Structural comparison of two emitted params assets, order-sensitive by index: EmitVrcParameters
        // preserves the document's declaration order, so a reordering is real drift.
        //
        // Does not compare m_Name, unlike MenuDiff's page name: CompileController's menu reuse path syncs
        // the name and its params branch assigns .parameters only, so a stale name from a rename would fail
        // here for a difference the compiler never reconciles.
        internal static string ParamsDiff(VRCExpressionParameters a, VRCExpressionParameters b, string where)
        {
            var ap = a.parameters ?? new VRCExpressionParameters.Parameter[0];
            var bp = b.parameters ?? new VRCExpressionParameters.Parameter[0];
            if (ap.Length != bp.Length)
            {
                var an = new HashSet<string>(ap.Select(p => p?.name ?? ""), StringComparer.Ordinal);
                var bn = new HashSet<string>(bp.Select(p => p?.name ?? ""), StringComparer.Ordinal);
                var dropped = ap.Select(p => p?.name ?? "").Where(n => !bn.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
                var added = bp.Select(p => p?.name ?? "").Where(n => !an.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
                if (dropped.Count == 0 && added.Count == 0)
                    return $"{where}: committed has {ap.Length} parameter(s), compiled has {bp.Length}" +
                           " — same names either side, so one of them declares a name twice";
                return $"{where}: committed has {ap.Length} parameter(s), compiled has {bp.Length}" +
                       $" — compiled dropped {NameList(dropped)}, added {NameList(added)}";
            }

            for (int i = 0; i < ap.Length; i++)
            {
                var x = ap[i]; var y = bp[i];
                if ((x?.name ?? "") != (y?.name ?? ""))
                    return $"{where}[{i}]: name '{x?.name}' vs '{y?.name}'";
                if (x == null || y == null)
                    return $"{where}[{i}]: null parameter entry";
                string w = $"{where} '{x.name}'";
                if (x.valueType != y.valueType) return $"{w}: valueType {x.valueType} vs {y.valueType}";
                if (x.saved != y.saved) return $"{w}: saved {x.saved} vs {y.saved}";
                if (x.defaultValue != y.defaultValue) return $"{w}: defaultValue {x.defaultValue} vs {y.defaultValue}";
                if (x.networkSynced != y.networkSynced) return $"{w}: networkSynced {x.networkSynced} vs {y.networkSynced}";
#if VRC_PARAM_ENCODING_PRESENT
                if (x.bitWidth != y.bitWidth) return $"{w}: bitWidth {x.bitWidth} vs {y.bitWidth}";
                if (x.flippedRepresentation != y.flippedRepresentation) return $"{w}: flippedRepresentation {x.flippedRepresentation} vs {y.flippedRepresentation}";
                if (x.useCustomLerpDuration != y.useCustomLerpDuration) return $"{w}: useCustomLerpDuration {x.useCustomLerpDuration} vs {y.useCustomLerpDuration}";
                if (x.customLerpDuration != y.customLerpDuration) return $"{w}: customLerpDuration {x.customLerpDuration} vs {y.customLerpDuration}";
#endif
            }
            return null;
        }

        static string NameList(List<string> names)
        {
            if (names.Count == 0) return "(none)";
            if (names.Count <= 5) return string.Join(", ", names.Select(n => $"'{n}'"));
            return string.Join(", ", names.GetRange(0, 5).Select(n => $"'{n}'")) + $" and {names.Count - 5} more";
        }

        // Reads the `controller:` name off a schema document without compiling it. Null when the file
        // carries no such key (e.g. a CompileClips document).
        //
        // Diverges from AnimatorSchemaYaml, which binds all three lines below, so this can resolve a name
        // the compiler never writes and then hunt a built/<name>.controller that cannot exist. Other
        // spellings diverge too (a '#' without leading space, a duplicate key, a bool-like scalar); they
        // matter only to a GUID-consumer entry, since the name's only use is addressing built/.
        //   controller: "FX"   keeps the quotes, and '"' is illegal in a Windows path. RunGate feeds this to
        //                      Path.Combine OUTSIDE any try, so the run dies with no [gate] line at all.
        //   controller : FX    return null, so the document is SKIPped before the missing-built requirement
        //   "controller": FX   is ever applied — guidConsumer is computed earlier, but never consulted. The
        //                      orphan pass is the only remaining net, and it sees this only while the built
        //                      artifact still exists; if built/ is what went missing, nothing does.
        internal static string ParseControllerName(string yamlPath)
        {
            foreach (var line in File.ReadLines(yamlPath))
            {
                if (!line.StartsWith("controller:", StringComparison.Ordinal)) continue;
                var v = line.Substring("controller:".Length);
                int hash = v.IndexOf('#');
                if (hash >= 0) v = v.Substring(0, hash);
                v = v.Trim();
                return v.Length == 0 ? null : v;
            }
            return null;
        }

        // The anchor-seam class (CheckAvatar) over one entry prefab, instantiated so the scan walks a real
        // scene hierarchy rather than the asset. Gate tier is FAIL where CheckAvatar.Run is CLASSIFY for
        // the same predicate: an entry in this library is ours and CONVENTIONS.md forbids the shape, while
        // a composed avatar carries mergeables that are not ours to rule on. An instantiation that yields
        // nothing FAILS, because an empty list reads as "scanned, found nothing".
        static System.Collections.Generic.List<string> ScanAnchorSeams(GameObject prefab)
        {
            var inst = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (inst == null)
                return new System.Collections.Generic.List<string> {
                    Ryan6Vrc.AgentTools.Editor.CheckAvatar.DegradedPrefix +
                    "PrefabUtility.InstantiatePrefab returned null, so no seam scan ran on this prefab"
                };
            try { return Ryan6Vrc.AgentTools.Editor.CheckAvatar.ScanAnchorSeams(inst); }
            finally { UnityEngine.Object.DestroyImmediate(inst); }
        }

        // Every file under the non-dot top-level dirs. A dot directory is skipped at every depth so that
        // every file imported here is one DuplicateCommittedGuid reads: that pass is what makes one shared
        // scratch safe.
        static System.Collections.Generic.IEnumerable<string> LibraryFiles(string root, string pattern)
        {
            var rootFull = Path.GetFullPath(root);
            return Directory.GetDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith(".", StringComparison.Ordinal))
                .OrderBy(d => d, StringComparer.Ordinal)
                .SelectMany(d => Directory.GetFiles(d, pattern, SearchOption.AllDirectories))
                .Where(f => !Path.GetFullPath(f).Substring(rootFull.Length)
                                .TrimStart('/', '\\')
                                .Replace('\\', '/')
                                .Split('/')
                                .Any(seg => seg.StartsWith(".", StringComparison.Ordinal)));
        }

        // Copies the whole library into one scratch Assets/ dir as a unit, imports it, and checks every
        // committed prefab. Copying every file rather than *.prefab lets hard load dependencies travel: a
        // Prefab Variant whose base is an .fbx in assets/, or sits in a sibling tree, otherwise loads as
        // null. One shared copy requires that no two committed .meta share a GUID, which
        // DuplicateCommittedGuid asserts. Only prefabs are asserted on: the rest rides along for GUID
        // resolution, and a dangling controller ref does not fail a prefab load, so the wider copy masks
        // nothing.
        static (int failed, int total, string msg) CheckPrefabIntegrity(string root)
        {
            var scratch = "Assets/_prefab_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var full = Path.GetFullPath(scratch);
            var rootFull = Path.GetFullPath(root);
            try
            {
                foreach (var src in LibraryFiles(root, "*"))
                {
                    var rel = Path.GetFullPath(src).Substring(rootFull.Length).TrimStart('/', '\\');
                    var dest = Path.Combine(full, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(src, dest, true);
                }
                AssetDatabase.Refresh();

                int offenderCount = 0;
                // failedPrefabs is what the caller reports and exits on; offenderCount counts defects.
                var failedPrefabs = new System.Collections.Generic.HashSet<string>();
                int total = 0;
                var offenders = new System.Collections.Generic.List<string>();
                foreach (var src in LibraryFiles(root, "*.prefab"))
                {
                    total++;
                    var rel = Path.GetFullPath(src).Substring(rootFull.Length).TrimStart('/', '\\');
                    var label = rel.Replace('\\', '/');

                    var foreign = ForeignProjectPathLines(File.ReadAllText(src));
                    if (foreign.Count > 0)
                    {
                        var shown = foreign.Count > 5
                            ? string.Join("; ", foreign.GetRange(0, 5)) + $"; and {foreign.Count - 5} more"
                            : string.Join("; ", foreign);
                        offenders.Add($"{label} names a consuming project's Assets/ path on {foreign.Count} line(s) — " +
                                      shown +
                                      ". A VPM package resolves nothing under Assets/: this is a cached asset " +
                                      "reference back-filled with the path of whatever project last inspected the " +
                                      "prefab (VRCFury stores each reference as `<guid>|<path>` and fills `path` on " +
                                      "first inspection), and committing it publishes that project's layout. Fix: " +
                                      "re-resolve the reference in an Editor that mounts this library as a package " +
                                      "so the cached path names Packages/<this package>/…, then commit that line. " +
                                      "Blanking the path by hand is not a fix: the next inspection refills it from " +
                                      "whichever project does the inspecting");
                        offenderCount += 1;
                        failedPrefabs.Add(label);
                    }

                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(ToAssetsRelative(Path.Combine(full, rel)));
                    if (go == null) { offenders.Add(label + " (failed to load)"); offenderCount++; failedPrefabs.Add(label); continue; }
                    int missing = 0;
                    foreach (var t in go.GetComponentsInChildren<Transform>(true))
                        missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                    if (missing > 0) { offenders.Add($"{label} ({missing} missing script(s))"); offenderCount += missing; failedPrefabs.Add(label); }

                    foreach (var seam in ScanAnchorSeams(go))
                    {
                        // A scope line bounds what the scan could see: it logs as a warning and never fails
                        // the entry.
                        if (seam.StartsWith(Ryan6Vrc.AgentTools.Editor.CheckAvatar.ScopePrefix, StringComparison.Ordinal))
                        {
                            Debug.LogWarning($"[gate] {label} {seam}");
                            continue;
                        }
                        offenders.Add($"{label} anchor-seam: {seam}");
                        offenderCount++;
                        failedPrefabs.Add(label);
                    }

                    // A source slot past the constraint list length solves in the editor and is ignored by
                    // the client. FAIL here where ReportGimmick only observes, for the reason
                    // ScanAnchorSeams gives.
                    foreach (var slot in Ryan6Vrc.AgentTools.Editor.ReportGimmick.ScanConstraintLengths(go))
                    {
                        if (slot.StartsWith(Ryan6Vrc.AgentTools.Editor.CheckAvatar.ScopePrefix, StringComparison.Ordinal))
                        {
                            Debug.LogWarning($"[gate] {label} {slot}");
                            continue;
                        }
                        offenders.Add($"{label} constraint-source-length: {slot}");
                        offenderCount++;
                        failedPrefabs.Add(label);
                    }
                }
                return (failedPrefabs.Count, total, offenderCount == 0 ? "OK" : string.Join(", ", offenders));
            }
            catch (Exception e) { return (1, -1, e.Message); }
            finally { CleanupScratch(scratch); }
        }

        // A committed prefab line naming `Assets/` at the start of a path segment: a VPM package resolves
        // its own content under Packages/<name>/, so the path can only be a consuming project's. No
        // allowlist, because at gate tier no such path is intentional. CheckPrefabIntegrity's offender
        // message states the VRCFury back-fill behind it; the writer is an Editor mounting this library
        // writable (a `file:` mount, not Library/PackageCache).
        private static readonly System.Text.RegularExpressions.Regex ForeignProjectPath =
            new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])Assets/",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        internal static System.Collections.Generic.List<string> ForeignProjectPathLines(string prefabText)
        {
            var hits = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(prefabText)) return hits;
            var lines = prefabText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ForeignProjectPath.IsMatch(lines[i])) continue;
                var text = lines[i].TrimEnd('\r').Trim();
                if (text.Length > 160) text = text.Substring(0, 160) + "…";
                hits.Add($"line {i + 1}: {text}");
            }
            return hits;
        }

        // Tier is derived from files present; a GUID-consumer shape (a prefab, a non-empty assets/, or a
        // built/ dir) MUST ship a built .controller per document, or a Module whose built controller went
        // missing would pass as a Pattern.
        //
        // Two known gaps. assets/ is scanned top-level only while CheckPrefabIntegrity walks the same tree
        // with AllDirectories, so an entry shipping only assets/<subdir>/ reads as a Pattern and loses the
        // built-controller requirement. Closing it must not widen the *.prefab glob beside it, which
        // IsGuidConsumer_PrefabOneLevelDown_IsNotSeen pins: a parent must not read as a GUID-consumer off a
        // nested entry's prefab. And a `built` that is a file switches the whole built/ regime off with no
        // diagnostic.
        internal static bool IsGuidConsumer(string entryDir)
        {
            var builtDir = Path.Combine(entryDir, "built");
            var assetsDir = Path.Combine(entryDir, "assets");
            return Directory.GetFiles(entryDir, "*.prefab").Length > 0
                || Directory.Exists(builtDir)
                || (Directory.Exists(assetsDir) && Directory.GetFiles(assetsDir)
                        .Any(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)));
        }

        // Committed built controllers no yaml document claims. Case matters: `Fx` does not claim
        // `FX.controller`.
        internal static IEnumerable<string> OrphanControllers(string builtDir, IEnumerable<string> claimed)
        {
            var set = new HashSet<string>(claimed, StringComparer.Ordinal);
            return Directory.GetFiles(builtDir, "*.controller")
                .Select(Path.GetFileNameWithoutExtension).Where(n => !set.Contains(n));
        }

        internal static IEnumerable<string> OrphanMenus(string builtDir, IEnumerable<string> claimed)
        {
            var set = new HashSet<string>(claimed, StringComparer.Ordinal);
            return Directory.GetFiles(builtDir, "*_Menu.asset")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !set.Contains(n.Substring(0, n.Length - "_Menu".Length)));
        }

        // One-way: an entry legitimately ships fewer params assets than controllers (ParamsPresence's Skip
        // leg), so this never asks that every claimed controller has one.
        internal static IEnumerable<string> OrphanParams(string builtDir, IEnumerable<string> claimed)
        {
            var set = new HashSet<string>(claimed, StringComparer.Ordinal);
            return Directory.GetFiles(builtDir, "*_Parameters.asset")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !set.Contains(n.Substring(0, n.Length - "_Parameters".Length)));
        }

        // Every entry under root, at any depth, ordinally sorted: a non-dot directory holding a
        // controller.yaml.
        internal static List<string> EnumerateEntries(string root)
        {
            var found = new List<string>();
            Descend(root, found);
            found.Sort(StringComparer.Ordinal);
            return found;
        }

        // Case-insensitive because IsGuidConsumer's Directory.Exists is on Windows, where the gate runs: an
        // ordinal compare would let a mis-cased Assets/ read as reserved for tier derivation while this
        // walk descended into it.
        static bool IsReserved(string name) =>
            string.Equals(name, "built", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "assets", StringComparison.OrdinalIgnoreCase);

        // Prunes skip and do not descend. A dot-named directory: a leaf-name filter over
        // SearchOption.AllDirectories looks equivalent and walks the whole of .git, since .git/refs has the
        // leaf name refs. built/ and assets/: both are reserved inside an entry, so a controller.yaml under
        // either is content and gating it would invent a phantom Pattern.
        //
        // No reparse-point guard: Windows refuses to resolve a path past 63 reparse traversals, so a
        // junction aimed at its own ancestor ends in an IOException that RunGate's walk guard turns into
        // exit 2, and a skip would only hide an entry mounted behind a junction.
        static void Descend(string dir, List<string> found)
        {
            foreach (var child in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                if (IsReserved(name)) continue;
                if (File.Exists(Path.Combine(child, "controller.yaml"))) found.Add(child);
                Descend(child, found);
            }
        }

        // An entry's log address: its path relative to root, forward-slashed. Not the leaf name, because
        // entries share leaf names (several are named object-sync) and RunGate composes its per-doc line as
        // $"{entry}/{filename}", so leaf labels would emit byte-identical FAIL lines for different entries.
        internal static string EntryLabel(string root, string entryDir)
        {
            var r = Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/');
            var e = Path.GetFullPath(entryDir).Replace('\\', '/');
            return e.StartsWith(r + "/", StringComparison.Ordinal) ? e.Substring(r.Length + 1) : Path.GetFileName(e);
        }

        // Column-0 match, so a `guid:` nested inside an importer block (a reference to some other asset) is
        // not mistaken for this file's own.
        static string CommittedGuid(string metaPath)
        {
            foreach (var line in File.ReadLines(metaPath))
                if (line.StartsWith("guid:", StringComparison.Ordinal))
                    return line.Substring("guid:".Length).Trim();
            return null;
        }

        // Two committed .meta declaring one GUID, anywhere in the library. CheckPrefabIntegrity imports
        // them all into one scratch, where Unity resolves a duplicate by rewriting one .meta and leaving
        // refs to it stale: a null asset load, with the rewrite on the scratch copy so nothing on disk
        // tells the author. A pass of its own because a collision between prefab-free entries fails no
        // prefab load here yet reaches a consuming project intact.
        //
        // Dot-directories are skipped because Unity never imports a hidden folder, so a stashed
        // .built-backup/ cannot collide. OrdinalIgnoreCase because Unity parses the field as 128-bit hex.
        internal static string DuplicateCommittedGuid(string root)
        {
            var rootFull = Path.GetFullPath(root);
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var meta in Directory.GetFiles(root, "*.meta", SearchOption.AllDirectories))
            {
                var rel = Path.GetFullPath(meta).Substring(rootFull.Length).TrimStart('/', '\\').Replace('\\', '/');
                if (rel.Split('/').Any(s => s.StartsWith(".", StringComparison.Ordinal))) continue;
                var guid = CommittedGuid(meta);
                if (guid == null) continue;
                if (seen.TryGetValue(guid, out var first))
                    return $"guid {guid} is declared by two committed .meta files — {first} and {rel}. " +
                           "One is a copy that kept the original's GUID: re-GUID the second and repoint " +
                           "whatever references it in the same edit (for a copied built/, that is the " +
                           "prefab pointing at it)";
                seen[guid] = rel;
            }
            return null;
        }

        // A committed model .meta whose importer the VRChat SDK panel refuses an upload over. The panel
        // walks every mesh on the avatar, inactive included, and errors on two ModelImporter settings:
        // Read/Write off, and blendshape normals set to Calculate without the legacy flag. No consuming
        // venue can fix that, because a VPM resolve rewrites Packages/ and `ConformImportSettings` names
        // such an offender without writing it, so the fix belongs in this .meta.
        //
        // Serialized `blendShapeNormalImportMode`: 0 = Import, 1 = Calculate, 2 = None.
        internal static string ModelImporterOffense(IEnumerable<string> metaLines)
        {
            bool model = false;
            string readable = null, bsNormals = null, legacy = null;
            foreach (var raw in metaLines)
            {
                var line = raw.Trim();
                if (line == "ModelImporter:") model = true;
                else if (readable == null && line.StartsWith("isReadable:", StringComparison.Ordinal))
                    readable = line.Substring("isReadable:".Length).Trim();
                else if (bsNormals == null && line.StartsWith("blendShapeNormalImportMode:", StringComparison.Ordinal))
                    bsNormals = line.Substring("blendShapeNormalImportMode:".Length).Trim();
                else if (legacy == null && line.StartsWith("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes:", StringComparison.Ordinal))
                    legacy = line.Substring("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes:".Length).Trim();
            }
            if (!model) return null;
            var why = new List<string>();
            if (readable != "1") why.Add(readable == null ? "isReadable absent" : "Read/Write disabled (isReadable: " + readable + ")");
            if (bsNormals == null) why.Add("blendShapeNormalImportMode absent");
            else if (bsNormals == "1" && legacy != "1")
                why.Add(legacy == null ? "blendshape normals = Calculate, legacy flag absent" : "blendshape normals = Calculate without legacy");
            return why.Count == 0 ? null : string.Join("; ", why);
        }

        // One offender line per model. Dot-directories are skipped for DuplicateCommittedGuid's reason.
        internal static string ModelImporterOffenders(string root)
        {
            var rootFull = Path.GetFullPath(root);
            var bad = new List<string>();
            foreach (var meta in Directory.GetFiles(root, "*.meta", SearchOption.AllDirectories))
            {
                var rel = Path.GetFullPath(meta).Substring(rootFull.Length).TrimStart('/', '\\').Replace('\\', '/');
                if (rel.Split('/').Any(s => s.StartsWith(".", StringComparison.Ordinal))) continue;
                var why = ModelImporterOffense(File.ReadLines(meta));
                if (why != null) bad.Add(rel.Substring(0, rel.Length - ".meta".Length) + ": " + why);
            }
            if (bad.Count == 0) return null;
            bad.Sort(StringComparer.Ordinal);
            return string.Join(" | ", bad) + " — the SDK panel refuses an upload over these on any avatar " +
                   "carrying the mesh; set Read/Write on and legacy blendshape normals on in the committed " +
                   ".meta (reimport through an Editor that mounts this checkout, then commit what it wrote)";
        }

        // The gate's contract with gate.ps1: 0 iff nothing failed in any pass (the third and fourth fold
        // into prefabFailed).
        internal static int GateExit(int failedEntries, int prefabFailed) =>
            (failedEntries == 0 && prefabFailed == 0) ? 0 : 1;

        // -executeMethod entrypoint. Args: --root <dir>. In each entry (EnumerateEntries), every top-level
        // *.yaml with a `controller:` key is gated against built/<name>.controller; a multi-controller
        // entry ships an FX + Gesture pair. Exits 0 iff all pass, 1 on a FAIL, 2 on a refusal.
        public static void RunGate()
        {
            string root = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--root") root = args[i + 1];
            if (root == null) { Debug.LogError("[gate] --root <dir> required"); EditorApplication.Exit(2); return; }
            if (!Directory.Exists(root)) { Debug.LogError("[gate] root not found: " + root); EditorApplication.Exit(2); return; }

            SweepScratch(); // self-heal any scratch a crashed prior run stranded, before we start

            List<string> entries;
            try { entries = EnumerateEntries(root); }
            catch (Exception e)
            {
                Debug.LogError("[gate] entry walk failed under " + root + ": " + e.Message);
                EditorApplication.Exit(2); return;
            }
            // A root holding no entry is a mis-pointed --root, not a clean library.
            if (entries.Count == 0)
            {
                Debug.LogError("[gate] no entry (a non-dot dir holding controller.yaml, at any depth) found " +
                               "under " + root + " — refusing rather than reporting 0/0 as a pass");
                EditorApplication.Exit(2); return;
            }

            int failedEntries = 0, checkedDocs = 0;
            foreach (var dir in entries)
            {
                var entry = EntryLabel(root, dir);
                bool entryFailed = false;
                var builtDir = Path.Combine(dir, "built");

                bool guidConsumer = IsGuidConsumer(dir);

                var claimed = new List<string>();
                foreach (var yaml in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var doc = $"{entry}/{Path.GetFileName(yaml)}";
                    var name = ParseControllerName(yaml);
                    if (name == null)
                    {
                        Debug.Log($"[gate] SKIP {doc}: no controller: key (not a controller document)");
                        continue;
                    }
                    claimed.Add(name);
                    var built = Path.Combine(builtDir, name + ".controller");
                    var builtExists = File.Exists(built);
                    if (!builtExists && guidConsumer)
                    {
                        Debug.Log($"[gate] FAIL {doc}: GUID-consumer entry (prefab/assets/built) has no built/{name}.controller");
                        entryFailed = true; continue;
                    }

                    checkedDocs++;
                    var (ok, msg) = Check(yaml, builtExists ? built : null);
                    Debug.Log($"[gate] {(ok ? "PASS" : "FAIL")} {doc}: {msg}");
                    if (!ok) entryFailed = true;
                }

                // Committed built artifacts no document claims are drift: a renamed or deleted yaml left
                // them behind, and Check never ran for them.
                if (Directory.Exists(builtDir))
                    foreach (var orphan in OrphanControllers(builtDir, claimed))
                    {
                        Debug.Log($"[gate] FAIL {entry}: built/{orphan}.controller matches no yaml document (drift)");
                        entryFailed = true;
                    }

                if (Directory.Exists(builtDir))
                    foreach (var orphan in OrphanMenus(builtDir, claimed))
                    {
                        Debug.Log($"[gate] FAIL {entry}: built/{orphan}.asset matches no yaml document (drift)");
                        entryFailed = true;
                    }

                if (Directory.Exists(builtDir))
                    foreach (var orphan in OrphanParams(builtDir, claimed))
                    {
                        Debug.Log($"[gate] FAIL {entry}: built/{orphan}.asset matches no yaml document (drift)");
                        entryFailed = true;
                    }

                if (entryFailed) failedEntries++;
            }
            Debug.Log($"[gate] {entries.Count - failedEntries}/{entries.Count} entries passed ({checkedDocs} documents)");

            // Second pass: every committed prefab in the library, including the Structural Modules (a
            // prefab, no controller.yaml) the loop above never sees.
            var (prefabFailed, prefabTotal, prefabMsg) = CheckPrefabIntegrity(root);
            if (prefabFailed > 0) Debug.Log($"[gate] prefab-integrity FAIL: {prefabMsg}");
            if (prefabTotal < 0)
            {
                Debug.Log("[gate] prefab-integrity did not enumerate: the pass threw before counting " +
                          "prefabs, so the FAIL above is the whole verdict");
            }
            else
            {
                Debug.Log($"[gate] prefab-integrity {prefabTotal - prefabFailed}/{prefabTotal} committed prefabs clean");
            }

            // Third pass. Counted into prefabFailed so the exit expression needs no new term.
            var dup = DuplicateCommittedGuid(root);
            if (dup != null) { Debug.Log($"[gate] duplicate-guid FAIL: {dup}"); prefabFailed++; }

            // Fourth pass, folded into prefabFailed the same way.
            var models = ModelImporterOffenders(root);
            if (models != null) { Debug.Log($"[gate] model-importer FAIL: {models}"); prefabFailed++; }

            SweepScratch(); // authoritative cleanup: all Check refs are out of scope now, no Refresh follows

            EditorApplication.Exit(GateExit(failedEntries, prefabFailed));
        }
    }
}
