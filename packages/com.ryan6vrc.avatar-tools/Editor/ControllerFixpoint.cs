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
    // Gate infrastructure for a directory of vrc-patterns entries — NOT an [AgentTool] and NOT a
    // [MenuItem] (no callable door, so no TOOLS.md row). It reuses the shipped compile/decompile
    // primitives: decode(c) = AnimatorSchemaEmit.Serialize(ControllerDecompile.Walk(c).Doc), the same
    // canonical string the fixpoint tests trust.
    //
    // THE ONE QUESTION THIS GATE ASKS: was built/ regenerated after its source yaml changed? In
    // vrc-patterns the yaml is the source of truth and built/ is a generated artifact, committed only
    // so prefabs can resolve it by GUID and so a study entry opens in the animator window. Nobody
    // hand-maintains it. So a field the schema does not model does not matter here — and one that DOES
    // matter is a reason to grow the schema, never a reason to tighten this comparison. Do not add a
    // check that guards hand-authored content in a generated directory; that category is empty by
    // construction, and CONVENTIONS.md routes the real exception (a menu the schema cannot express)
    // to assets/ instead.
    //
    // Drift is decompile-equality, never a byte diff — and that is a fact about Unity, not a taste.
    // Unity assigns .controller sub-asset fileIDs non-deterministically: two compiles of the SAME yaml
    // in one Editor seconds apart produce byte-different files (measured across all 17 library
    // documents — every one byte-different, every one content-identical). A byte gate would fail every
    // entry on every run. Flat single-document assets (*_Parameters.asset, *_Menu.asset) have no
    // sub-asset ids and DO compare byte-stable. *_Menu.asset is compared (MenuPresence/MenuDiff).
    // *_Parameters.asset is NOT, and that is a real hole rather than a considered omission: the
    // emitter filters on `!p.Scratch && !ControllerRules.IsVrcReserved(p.Name)`, so changing the
    // reserved-name set silently adds or drops entries in every emitted params asset while both
    // sides of decompile-equality stay identical (the .controller carries no notion of either flag)
    // and this gate reports PASS. Any change to that set therefore has to be settled by diffing the
    // committed *_Parameters.asset by hand, because nothing here will.
    //
    // A committed built .controller lives at an arbitrary --root filesystem path, not under the
    // project, so it is copied into Assets/ (with its committed GUID) to be imported and loaded.
    // A second RunGate pass loads each entry's prefab(s) the same way — copied into Assets/ to
    // import — and fails any with a missing MonoBehaviour script, an anchor seam, or a committed
    // consuming-project path (ForeignProjectPathLines); the coverage a Structural Module (a prefab, no
    // controller.yaml) otherwise never gets. That last one is the one check here that DOES guard
    // hand-authored content, and it is not the exception the paragraph above forbids: it reads the
    // committed prefab, which is hand-authored source, never built/.
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

        // Per-call scratch cleanup: pure filesystem delete of the folder + its .meta, no AssetDatabase
        // call. Deliberately NO DeleteAsset and NO Refresh — Check holds loaded AnimatorControllers from
        // this scratch as live locals through the finally, and any AssetDatabase reconcile fired while
        // those refs pin an asset whose folder just vanished makes Unity re-materialize an empty husk
        // renamed "<name> 1", which then accumulates. Removing the bytes and leaving the stale DB entry
        // to be reconciled later (by then the refs are out of scope) avoids the resurrection; SweepScratch
        // at end-of-run is the authoritative backstop for anything that slips through.
        static void CleanupScratch(string scratchAssetsPath)
        {
            var full = Path.GetFullPath(scratchAssetsPath);
            if (Directory.Exists(full)) Directory.Delete(full, true);
            if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
        }

        // Filesystem-only sweep of every scratch dir + .meta this tool creates, matching the "<name> 1"
        // husk-rename variants via the glob. No AssetDatabase call — run it when no scratch asset is
        // referenced (RunGate start, before any Check; and end, after every Check has returned) so no
        // reconcile can resurrect a husk. Start-of-run self-heals scratch a crashed batchmode run (no
        // finally) stranded; end-of-run clears the run's own residue. Serial venue — nothing live owns
        // these prefixes.
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

        // Compile a yaml at a filesystem path into a temp Assets/ folder and load the emitted controller.
        static AnimatorController CompileToTemp(string yamlPath, string tempAssetsDir)
        {
            Directory.CreateDirectory(Path.GetFullPath(tempAssetsDir));
            var msg = CompileController.Compile(yamlPath, ToAssetsRelative(tempAssetsDir));
            if (msg == null || msg.IndexOf("=> OK", StringComparison.Ordinal) < 0)
                throw new Exception("compile failed: " + msg);
            AssetDatabase.Refresh();
            var ctrl = Directory.GetFiles(Path.GetFullPath(tempAssetsDir), "*.controller").FirstOrDefault();
            if (ctrl == null) throw new Exception("no .controller emitted into " + tempAssetsDir);
            var loaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(ToAssetsRelative(ctrl));
            if (loaded == null) throw new Exception("emitted .controller failed to load: " + ctrl);
            return loaded;
        }

        // Copy ONE committed built controller (+ its .meta) into Assets/ so the AssetDatabase imports
        // it with the committed GUID, then load it. Only the named controller is copied — a multi-
        // controller entry (an FX + Gesture pair) would otherwise import every sibling's committed
        // GUID once per checked document, colliding across scratch dirs. Assumes the package under
        // test is NOT also loaded in this host — else the committed GUID exists twice (a collision).
        static AnimatorController ImportCommitted(string builtControllerPath, string destAssetsDir)
        {
            var full = Path.GetFullPath(destAssetsDir);
            Directory.CreateDirectory(full);
            var src = Path.GetFullPath(builtControllerPath);
            File.Copy(src, Path.Combine(full, Path.GetFileName(src)), true);
            if (File.Exists(src + ".meta"))
                File.Copy(src + ".meta", Path.Combine(full, Path.GetFileName(src) + ".meta"), true);
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(
                ToAssetsRelative(Path.Combine(full, Path.GetFileName(builtControllerPath))));
        }

        // (ok, message). yamlPath: filesystem path to controller.yaml. builtControllerPath: filesystem
        // path to a committed built .controller (asset-bound/module tiers), or null for a Pattern entry.
        public static (bool ok, string msg) Check(string yamlPath, string builtControllerPath)
        {
            var scratch = "Assets/_fixpoint_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                // NO round-trip pass here (decode(compile(decode(compile(yaml)))) == decode(compile(yaml))).
                // That theorem is about the COMPILER AND DECOMPILER being mutually inverse — a property of
                // avatar-tools, not of any entry — and this package already owns it at the right door:
                // FixpointOracle + FixpointAcceptanceTests (real-controller fixtures picked by construct
                // census) + RoundtripStressTests (synthetic fixtures spanning the whole schema vocabulary).
                // The acceptance suite states the remediation this gate cannot deliver: "A FIXPOINT BREAK IS
                // A REAL BUG in decode / serialize / compile, fixed at the true site." Run here it failed an
                // ENTRY's admission for a TOOL bug, blocking a vrc-patterns PR nobody in that repo could fix.
                // The corpus argument for keeping it does not hold either: censused, the library exercises no
                // construct the fixtures lack — no sub-machines, no onExit, no offset, no mute/solo, no
                // fixedDuration, a narrower SMB set — it is larger in lines and a strict subset in vocabulary.
                var cFresh = CompileToTemp(yamlPath, scratch + "/a");
                var yFresh = Decode(cFresh, out var r1);
                if (yFresh == null) return (false, "fresh decompile refused: " + r1);

                if (builtControllerPath != null)
                {
                    var cCommitted = ImportCommitted(builtControllerPath, scratch + "/committed");
                    if (cCommitted == null) return (false, "committed controller failed to import");
                    var yCommitted = Decode(cCommitted, out var r3);
                    if (yCommitted == null) return (false, "committed decompile refused: " + r3);
                    if (yCommitted != yFresh) return (false, "committed built/ differs from compile(yaml) — regenerate built/");
                }

                // The MENU pass. Decompile-equality above cannot reach a menu: both sides of that comparison
                // are decoded from a .controller, which stores no menu at all, so an emitted menu asset is
                // dropped identically on both sides and drift between built/ and the yaml passes unseen.
                // Compare the emitted menu against the committed one directly, or built/ silently rots.
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
                        var imported = ImportCommittedMenu(committedMenuPath, scratch + "/menu");
                        if (imported == null) return (false, "committed menu asset failed to import");
                        var diff = MenuDiff(imported, freshMenu, "menu");
                        if (diff != null) return (false, "committed built/ menu differs from compile(yaml): " + diff + " — regenerate built/");
                    }
                }
                return (true, "OK");
            }
            catch (Exception e) { return (false, e.Message); }
            finally { CleanupScratch(scratch); }
        }

        internal enum MenuPass { Skip, Compare, Fail }

        // Which of the three things Check does about a menu, decided from presence alone: neither side has
        // one (Skip), both do (Compare the trees), or exactly one does (Fail). Extracted from Check so the
        // decision is reachable without an AssetDatabase — MenuDiff being correct is worth nothing if the
        // caller can stop invoking it, and a comparator at full branch coverage cannot see that happen.
        //
        // The two refusal strings live HERE rather than at the old call site because the message is part of
        // the decision: which of the two asymmetric cases fired is exactly what a test must be able to
        // distinguish, and a helper returning a bare bool could not tell them apart at all.
        //
        // TAKES THE MENU, NOT A BOOL, and that is the whole reason for the signature. Two same-typed bool
        // parameters would let a caller swap them: it compiles, it inverts which refusal fires — telling an
        // author to "delete or restore" when the fix is "regenerate" — and no test here would catch it,
        // because nothing in this file's suite invokes Check. Inline code had no argument order to get wrong,
        // so extracting created that hazard; differing the types is what removes it, at compile time rather
        // than by a test that does not exist.
        internal static (MenuPass pass, string msg) MenuPresence(VRCExpressionsMenu freshMenu, bool committedExists)
        {
            bool freshEmits = freshMenu != null;
            if (!freshEmits && committedExists)
                return (MenuPass.Fail, "built/ ships a menu asset the yaml no longer emits — delete it or restore the menu: block");
            if (freshEmits && !committedExists)
                return (MenuPass.Fail, "yaml emits a menu but built/ has none — regenerate built/");
            return (freshEmits ? MenuPass.Compare : MenuPass.Skip, null);
        }

        // The menu asset CompileController writes beside a controller, by the same formula it uses:
        // "<dir>/<name>_Menu.asset". Path arithmetic only — the file need not exist.
        //
        // This RE-DERIVES CompileController's formula (CompileController.cs: emitDir + "/" + name +
        // "_Menu.asset") rather than sharing it — two copies of one convention, coupled by nothing, and this
        // copy feeds both the File.Exists presence check and the LoadAssetAtPath above.
        //
        // What keeps the copies in step is the PAIR of suites, not this side alone: ControllerFixpointTests
        // pins this formula against literals, and MenuEmitTests pins the compiler's against its own
        // (MenuPath => OutDir + "/M_Fx_Menu.asset", asserted on the emitted asset's name). Drift in either
        // goes red. Sharing one helper would make the second suite unnecessary; until then both are load-bearing.
        internal static string MenuBeside(string controllerPath)
        {
            var dir = Path.GetDirectoryName(controllerPath) ?? "";
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(controllerPath) + "_Menu.asset")
                       .Replace('\\', '/');
        }

        // Copy the committed menu (+ its .meta, for the committed GUID) into Assets/ and load it. Same
        // constraint as ImportCommitted: entry files live outside the project and cannot be loaded in place.
        static VRCExpressionsMenu ImportCommittedMenu(string menuPath, string destAssetsDir)
        {
            var full = Path.GetFullPath(destAssetsDir);
            Directory.CreateDirectory(full);
            var src = Path.GetFullPath(menuPath);
            File.Copy(src, Path.Combine(full, Path.GetFileName(src)), true);
            if (File.Exists(src + ".meta"))
                File.Copy(src + ".meta", Path.Combine(full, Path.GetFileName(src) + ".meta"), true);
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(
                ToAssetsRelative(Path.Combine(full, Path.GetFileName(menuPath))));
        }

        // Structural comparison of two menu trees, recursing sub-menus. NOT a byte diff: the two assets
        // carry different file GUIDs and sub-asset fileIDs by construction, which a byte compare would
        // report as drift on every run. Returns null when equal, else the first difference, addressed by
        // its page path so an offender in a nested page names the page it sits on.
        //
        // It compares EVERY serialized field of a control — including `icon`, `style`, `labels`, and the
        // page's own name. Not because unmodeled content in built/ is worth guarding (it is not; see the
        // class header), but because there is no decoded intermediate to compare against for a menu, so
        // the fields ARE the comparison. Where the controller pass compares two decoded documents, this
        // one compares two loaded assets directly, and the cheapest complete way to do that is field by
        // field. A committed field the schema cannot author is a file in the wrong directory rather than
        // drift worth catching — CONVENTIONS.md routes such a menu to assets/ — but failing loud on it
        // costs nothing here and tells its author their edit was about to be regenerated away.
        //
        // ONE FIELD IS COMPARED BUT NOT COVERED: `icon`. It became authorable in the schema, and an entry's
        // icon lives in that entry's assets/ — which the gate host does not load, so both sides resolve to
        // null and any real difference between them is invisible here. Closing it means importing the entry
        // dir before the compile pass, the way the prefab pass already does. Deliberately not done: it is a
        // gate change, and no library entry authors an icon yet. ControllerFixpointTests does exercise the
        // branch against real imported assets, which keeps the comparison itself honest — it does not close
        // the gate hole above, and the two must not be confused.
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
                // Both sides read null whenever the entry's own assets/ is absent from THIS host's
                // AssetDatabase, which is the normal gate condition (the host never loads the package under
                // test). So this comparison is largely vacuous at the gate and is not what keeps an authored
                // `icon:` honest — see the class header.
                if (AssetDatabase.GetAssetPath(x.icon) != AssetDatabase.GetAssetPath(y.icon))
                    return $"{w}: icon '{AssetDatabase.GetAssetPath(x.icon)}' vs '{AssetDatabase.GetAssetPath(y.icon)}' — regenerate built/";

                var xl = x.labels ?? new VRCExpressionsMenu.Control.Label[0];
                var yl = y.labels ?? new VRCExpressionsMenu.Control.Label[0];
                if (xl.Length != yl.Length) return $"{w}: {xl.Length} label(s) vs {yl.Length}";
                // Label is a STRUCT (unlike Control.Parameter, a class) — no null-conditional here.
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

        // Reads the `controller:` name off a schema document without compiling it. Null when the file
        // carries no such key (e.g. a CompileClips document) — the caller decides what that means.
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

        // Copy an entry's WHOLE directory into a scratch Assets/ dir as a UNIT — every file, subpath
        // preserved — then import it and assert none of its prefabs has a missing MonoBehaviour script
        // or fails to load. Copying the entry entire (not just *.prefab) is what lets a prefab's hard
        // load dependencies travel with it: a Prefab Variant's base is a hard dep, and a base that
        // lives in assets/ as a non-prefab (an .fbx the *.prefab glob would miss) makes the variant
        // load as null otherwise — the head-proxy false-negative this dissolves. Entry files live at an
        // arbitrary --root path outside the project (the patterns package is not loaded here), so they
        // must be brought into Assets/ to load — the same constraint ImportCommitted solves for
        // controllers. Per-entry scratch isolation holds: each entry gets its own fresh scratch, so one
        // entry's committed GUIDs never co-import with another's. Only prefabs are asserted on; built/
        // controllers, yaml, and README ride along for GUID resolution but are never load-checked (a
        // dangling controller ref does not fail a prefab load, so widening the copy set masks nothing).
        // The anchor-seam class (CheckAvatar) over one entry prefab, instantiated so the scan walks a real
        // scene hierarchy rather than the asset. Gate tier is FAIL where CheckAvatar.Inspect is CLASSIFY for
        // the same predicate, and the asymmetry is deliberate: an entry in THIS library is ours and
        // CONVENTIONS.md forbids the shape outright, while a composed avatar carries mergeables that are not
        // ours to rule on. An instantiation that yields nothing FAILS rather than reporting a clean prefab —
        // an empty list is otherwise indistinguishable from "scanned, found nothing", which is exactly the
        // silent no-op this pass exists to prevent.
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

        static (bool ok, string msg) CheckPrefabIntegrity(string entryDir)
        {
            var scratch = "Assets/_prefab_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var full = Path.GetFullPath(scratch);
            var entryFull = Path.GetFullPath(entryDir);
            try
            {
                foreach (var src in Directory.GetFiles(entryDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetFullPath(src).Substring(entryFull.Length).TrimStart('/', '\\');
                    var dest = Path.Combine(full, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(src, dest, true);
                }
                AssetDatabase.Refresh();

                int offenderCount = 0;
                var offenders = new System.Collections.Generic.List<string>();
                foreach (var src in Directory.GetFiles(entryDir, "*.prefab", SearchOption.AllDirectories))
                {
                    var rel = Path.GetFullPath(src).Substring(entryFull.Length).TrimStart('/', '\\');
                    var label = rel.Replace('\\', '/');

                    // Provenance, checked on the COMMITTED text and before the load, so a prefab that fails
                    // to load still gets this verdict — the two defects are independent.
                    var foreign = ForeignProjectPathLines(File.ReadAllText(src));
                    if (foreign.Count > 0)
                    {
                        // The per-line truncation bounds one line, not the join — six leaked lines was
                        // the motivating case, and N of them still swamp a single-line FAIL. Cap the list.
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
                        // One leaky prefab is one offender. Adding foreign.Count weighted it by line
                        // count against every other defect here, which counts one per defect.
                        offenderCount += 1;
                    }

                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(ToAssetsRelative(Path.Combine(full, rel)));
                    if (go == null) { offenders.Add(label + " (failed to load)"); offenderCount++; continue; }
                    int missing = 0;
                    foreach (var t in go.GetComponentsInChildren<Transform>(true))
                        missing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                    if (missing > 0) { offenders.Add($"{label} ({missing} missing script(s))"); offenderCount += missing; }

                    foreach (var seam in ScanAnchorSeams(go))
                    {
                        // A scope line bounds what the scan could see; it is not a finding and must not fail
                        // the entry, or stating a limit would be indistinguishable from breaking a rule.
                        // It still has to reach the log, so a PASS is never read as wider than it is.
                        if (seam.StartsWith(Ryan6Vrc.AgentTools.Editor.CheckAvatar.ScopePrefix, StringComparison.Ordinal))
                        {
                            Debug.LogWarning($"[gate] {label} {seam}");
                            continue;
                        }
                        offenders.Add($"{label} anchor-seam: {seam}");
                        offenderCount++;
                    }
                }
                return offenderCount == 0 ? (true, "OK") : (false, string.Join(", ", offenders));
            }
            catch (Exception e) { return (false, e.Message); }
            finally { CleanupScratch(scratch); }
        }

        // Every line of a committed prefab's text carrying the substring `Assets/` — a path that can only have
        // come from the project that last inspected the prefab, since a VPM package resolves its own content
        // under `Packages/<name>/` and never under `Assets/`. The leak path is a cached asset reference:
        // VRCFury stores one as `<guid>|<path>` and back-fills `path` on first inspection, so an Editor that
        // mounts this library writable (a `file:` mount, not Library/PackageCache) writes its own project
        // layout into tracked source, where a commit publishes it permanently.
        //
        // Judgment-free by construction: presence of the substring is the whole predicate — no allowlist, no
        // knob, no attempt to tell a leaked path from an intentional one, because at gate tier there is no
        // such thing as an intentional one. Measured over this library at the time of the change: 0 of 31
        // committed prefabs contain `Assets/`, while 17 of 31 carry a `<guid>|<path>` pair that can acquire it
        // — a zero false-positive surface against a live leak surface of 17.
        //
        // `Assets/` has to START a path segment. A bare substring test also fires on a project's own
        // `MyAssets/` folder or a mesh named `ExtraAssets/…` — legal content, failed by a diagnostic
        // that names the wrong defect entirely and sends the reader hunting a VRCFury back-fill that
        // was never there. The lookbehind is the whole difference; the breadth is otherwise kept,
        // because at gate tier there is no such thing as an intentional `Assets/` path.
        private static readonly System.Text.RegularExpressions.Regex ForeignProjectPath =
            new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])Assets/",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Returns `line <n>: <text>` handles (1-based, matching an editor's gutter), each truncated so one
        // pathological line cannot swamp the gate's single-line FAIL message.
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
        // built/ dir) MUST ship a built .controller per document. Without this, a Module/Asset-bound entry
        // whose built controller went missing would silently pass as a Pattern.
        //
        // Throws DirectoryNotFoundException on an entryDir that does not exist — RunGate only ever passes a
        // directory it just enumerated, so the guard stays at that caller rather than being swallowed here.
        internal static bool IsGuidConsumer(string entryDir)
        {
            var builtDir = Path.Combine(entryDir, "built");
            var assetsDir = Path.Combine(entryDir, "assets");
            return Directory.GetFiles(entryDir, "*.prefab").Length > 0
                || Directory.Exists(builtDir)
                || (Directory.Exists(assetsDir) && Directory.GetFiles(assetsDir)
                        .Any(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)));
        }

        // Committed built controllers no yaml document claims. `claimed` arrives as a plain sequence and the
        // ORDINAL comparer is applied here, not at the caller: a test that supplied its own pre-built
        // HashSet would be asserting against its own comparer choice and would stay green if this one
        // changed. Case matters — `Fx` does not claim `FX.controller`.
        //
        // Both orphan helpers throw DirectoryNotFoundException on a missing builtDir; RunGate keeps its
        // Directory.Exists guard, since "no built/ at all" is a legal Pattern shape and not drift.
        internal static IEnumerable<string> OrphanControllers(string builtDir, IEnumerable<string> claimed)
        {
            var set = new HashSet<string>(claimed, StringComparer.Ordinal);
            return Directory.GetFiles(builtDir, "*.controller")
                .Select(Path.GetFileNameWithoutExtension).Where(n => !set.Contains(n));
        }

        // Same rule for a committed menu: it is named off its controller ("<name>_Menu.asset"), so one whose
        // controller no document claims is the same drift.
        internal static IEnumerable<string> OrphanMenus(string builtDir, IEnumerable<string> claimed)
        {
            var set = new HashSet<string>(claimed, StringComparer.Ordinal);
            return Directory.GetFiles(builtDir, "*_Menu.asset")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !set.Contains(n.Substring(0, n.Length - "_Menu".Length)));
        }

        // The gate's whole contract with gate.ps1: 0 iff nothing failed in either pass. Every FAIL this tool
        // logs is worthless if this expression says 0 anyway, and it is the one line where a mistake makes a
        // broken gate look like a passing one, so it is lifted out of RunGate for the same reason as the rest.
        internal static int GateExit(int failedEntries, int prefabFailed) =>
            (failedEntries == 0 && prefabFailed == 0) ? 0 : 1;

        // -executeMethod entrypoint. Args after `--`: --root <dir>. An entry is a non-dot <dir>/* folder
        // containing controller.yaml; EVERY top-level *.yaml in it with a `controller:` key is gated
        // (a multi-controller entry ships an FX + Gesture pair), each against built/<name>.controller.
        // A built controller no document claims is drift and fails the entry. Exits 0 iff all pass.
        // A second pass enumerates every non-dot dir shipping a prefab (controller.yaml or not) and
        // asserts each imports with zero missing MonoBehaviour scripts, carries no anchor seam
        // (CheckAvatar.ScanAnchorSeams), and names no consuming project's Assets/ path
        // (ForeignProjectPathLines).
        public static void RunGate()
        {
            string root = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--root") root = args[i + 1];
            if (root == null) { Debug.LogError("[gate] --root <dir> required"); EditorApplication.Exit(2); return; }
            if (!Directory.Exists(root)) { Debug.LogError("[gate] root not found: " + root); EditorApplication.Exit(2); return; }

            SweepScratch(); // self-heal any scratch a crashed prior run stranded, before we start

            var entries = Directory.GetDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .Where(d => File.Exists(Path.Combine(d, "controller.yaml")))
                .OrderBy(d => d, StringComparer.Ordinal).ToList();

            int failedEntries = 0, checkedDocs = 0;
            foreach (var dir in entries)
            {
                var entry = Path.GetFileName(dir);
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
                        // Not a controller document (a clips file has its own compile path) — named, not silent.
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

                // A committed controller no document claims is drift (a renamed/deleted yaml left its
                // built form behind) — the silent-skip this multi-yaml gate exists to prevent.
                if (Directory.Exists(builtDir))
                    foreach (var orphan in OrphanControllers(builtDir, claimed))
                    {
                        Debug.Log($"[gate] FAIL {entry}: built/{orphan}.controller matches no yaml document (drift)");
                        entryFailed = true;
                    }

                // Same rule for a committed menu: it is named off its controller, so one whose controller
                // no document claims is the same drift. Check() catches a menu the yaml stopped emitting;
                // this catches the case where the whole document went away and took the check with it.
                if (Directory.Exists(builtDir))
                    foreach (var orphan in OrphanMenus(builtDir, claimed))
                    {
                        Debug.Log($"[gate] FAIL {entry}: built/{orphan}.asset matches no yaml document (drift)");
                        entryFailed = true;
                    }

                if (entryFailed) failedEntries++;
            }
            Debug.Log($"[gate] {entries.Count - failedEntries}/{entries.Count} entries passed ({checkedDocs} documents)");

            // Second pass: every non-dot dir shipping a prefab must import with zero missing scripts.
            // Structural Modules (a prefab, no controller.yaml) are invisible to the loop above; this
            // pass covers them and every other entry's prefab alike — a vanished VRCFury/MA script ref
            // is the regression it catches. It also asserts provenance on the committed text
            // (ForeignProjectPathLines). Integrity and provenance only; behaviour still rests on the README.
            var prefabEntries = Directory.GetDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith("."))
                .Where(d => Directory.GetFiles(d, "*.prefab", SearchOption.AllDirectories).Length > 0)
                .OrderBy(d => d, StringComparer.Ordinal).ToList();

            int prefabFailed = 0;
            foreach (var dir in prefabEntries)
            {
                var (ok, msg) = CheckPrefabIntegrity(dir);
                if (!ok) { Debug.Log($"[gate] prefab-integrity FAIL {Path.GetFileName(dir)}: {msg}"); prefabFailed++; }
            }
            Debug.Log($"[gate] prefab-integrity {prefabEntries.Count - prefabFailed}/{prefabEntries.Count} entries clean");

            SweepScratch(); // authoritative cleanup: all Check refs are out of scope now, no Refresh follows

            EditorApplication.Exit(GateExit(failedEntries, prefabFailed));
        }
    }
}
