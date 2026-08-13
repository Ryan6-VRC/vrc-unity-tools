using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Where behaviour comes from on a composed avatar: every animator merged onto it, every parameter's
    /// declaration and its writers and readers per merged surface, and the menu controls installed from
    /// every source. One subject in, one digest out.
    ///
    /// It exists because the artifacts disagree about scope and no other door crosses them. Deciding
    /// whether one toggle exists can take five artifacts — a prefab, two controllers merged from different
    /// components, a menu asset carrying a second YAML document, and a parameters asset — and each of them
    /// reads perfectly consistent on its own with the wrong answer. <c>ReportGimmick</c> deliberately stops
    /// at the parameter FIELD and predicts no bake output; <c>CheckAvatar</c> is a <c>Check</c> on reference
    /// integrity; <c>ReportController</c> sees one controller. This crosses those seams and nothing else.
    ///
    /// <b>A Report, never a verdict.</b> The writer set for a parameter is open — menu controls, contacts,
    /// physbone and raycast suffixes, OSC from outside the avatar entirely — so "nothing writes this" is
    /// not a claim any static read can make, and every empty cell says so in the cell rather than looking
    /// like a finding. <c>docs/verify.md</c> owns what a reader may then assert.
    ///
    /// <b>Two modes, one door.</b> Plain (default) is an authored census: a pure read, cheap, and honest
    /// that build-time renames are outside what it can see. <c>bake:true</c> measures the composed truth
    /// instead, by building a throwaway clone through the real VRC SDK preprocess chain and diffing it
    /// against that census. The flag only upgrades exactness — same subject, same question — which is why
    /// this is a <c>Report</c> with a completeness flag and not a <c>Compare</c>: a <c>Compare</c> takes two
    /// subjects from the caller, and here the caller supplies one and the tool derives the second view.
    ///
    /// Boundary against <see cref="AgentInspector"/>: that door dumps a menu asset's raw serialized fields
    /// generically and leaves decoding to you; this one reports the authored control SET across every
    /// installer, typed and merged, and dumps no raw fields. Drop to <c>AgentInspector.Snapshot</c> for a
    /// control's nested structs. Humanoid mapping is not here either — that divergence is
    /// <c>CheckHumanoidRig.InspectAvatar</c>'s, named in output rather than called, because it lives in
    /// <c>avatar-tools</c> and the dependency arrow runs the other way.
    /// </summary>
    [AgentTool]
    public static class ReportComposition
    {
        /// <summary>Rows past this are in the artifact only — a real avatar's tables run to hundreds of rows
        /// and the in-band summary is a read budget, not a dump.</summary>
        private const int WindowRows = 12;

        internal const string ScopeWriters =
            "(none among scanned surfaces; menu/contact/physbone/OSC not excluded)";
        internal const string ScopeAuthoredNames =
            "authored names; the build may rewrite them — bake:true resolves";

        /// <summary>Digest the composition at <paramref name="avatarRoot"/> (a scene hierarchy path).
        /// <paramref name="bake"/> measures composed truth off a fresh build instead of reporting the
        /// authored census — it creates and destroys a clone, so it mutates editor state, and on a complex
        /// avatar with an optimizer installed it has been measured at roughly half a minute; it is
        /// two-phase for that reason (see <see cref="Verify"/>). Default off: cheap and safe is the default,
        /// exactness is opt-in. <paramref name="paramFilter"/> narrows every parameter table to names
        /// containing it, for chasing one parameter without paying for the whole avatar.</summary>
        public static string Report(string avatarRoot, bool bake = false, string paramFilter = null)
        {
            var root = FindByHierarchyPath(avatarRoot);
            if (root == null) return Refuse("avatarRoot '" + (avatarRoot ?? "(null)") + "' did not resolve to a GameObject");
            var descriptor = root.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (descriptor == null)
                return Refuse("'" + avatarRoot + "' has no VRCAvatarDescriptor — Report expects the avatar (descriptor) root");

            var census = Census(root, descriptor, paramFilter);
            if (!bake) return EmitPlain(root, census, paramFilter);
            return CompositionBake.Begin(root, census, paramFilter);
        }

        /// <summary>Re-read the verdict of a <c>bake:true</c> run from its artifact. The bake outlives the
        /// MCP transport window, so <see cref="Report"/> returns a stable path before doing the work and the
        /// result is read back here — a timed-out call loses nothing and must not be re-run.</summary>
        public static string Verify(string avatarRoot)
        {
            var root = FindByHierarchyPath(avatarRoot);
            if (root == null) return Refuse("avatarRoot '" + (avatarRoot ?? "(null)") + "' did not resolve to a GameObject");
            return CompositionBake.Verify(root);
        }

        // ── The authored census ──────────────────────────────────────────────────────────────────────

        internal class SurfaceRow
        {
            public string Label;
            public string Mount;
            public AnimatorController Controller;
            public string Kind;
            public bool HasRewrite;
        }

        internal class ParamRow
        {
            public string Name;
            /// <summary>Every asset that declares this name. A list, not a slot: two FullControllers each
            /// shipping a parameters asset that declares one name is a composed-scope fact, and overwriting
            /// a single field reports whichever surface happened to be walked last.</summary>
            public readonly List<string> DeclaredAt = new List<string>();
            public string Declared => DeclaredAt.Count == 0
                ? "(undeclared on any expression parameters asset reached here)"
                : string.Join("; ", DeclaredAt);
            /// <summary>False for a name that no expression-parameters asset and no controller declares —
            /// a physbone suffix or a menu sub-parameter the runtime writes. Bake mode must NOT diff these:
            /// the built side is a declaration set, so an undiffable name would land in `dropped`, whose
            /// plain reading is "the build removed it".</summary>
            public bool Diffable;
            public string Type = "—";
            public string Synced = "—";
            public string Saved = "—";
            public string Default = "—";
            public readonly List<string> Writers = new List<string>();
            public readonly List<string> Readers = new List<string>();
        }

        internal class MenuRow
        {
            public string Source;     // where the control was installed from
            public string Path;       // menu path as authored
            public string ControlName;
            public string Type;
            public string Parameter;
        }

        internal class CensusResult
        {
            public readonly List<SurfaceRow> Surfaces = new List<SurfaceRow>();
            public readonly List<ParamRow> Params = new List<ParamRow>();
            public readonly List<MenuRow> Menu = new List<MenuRow>();
            public readonly List<string> Optimizers = new List<string>();
            public readonly List<string> Notes = new List<string>();
            public readonly List<string> Other = new List<string>(); // tier-2 census: components no table read
            public int MenuAssetsWalked;
        }

        internal static CensusResult Census(GameObject root,
            VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor, string paramFilter)
        {
            var res = new CensusResult();
            var surfaces = MergeSurfaces.Enumerate(root, descriptor, vrcfOnly: false,
                (c, anchor) =>
                {
                    string m = "[ReportComposition] frame field '" + anchor + "' on " + c.GetType().Name + " @ "
                             + MergeSurfaces.PathOf(c.gameObject)
                             + " did not reflect — the surface is reported anyway (not dropped); its frame is best-effort.";
                    Debug.LogWarning(m);
                    res.Notes.Add(m.Substring("[ReportComposition] ".Length));
                });

            var rows = new Dictionary<string, ParamRow>(StringComparer.Ordinal);
            ParamRow Row(string name)
            {
                if (!rows.TryGetValue(name, out var r)) rows[name] = r = new ParamRow { Name = name };
                return r;
            }

            foreach (var s in surfaces)
            {
                res.Surfaces.Add(new SurfaceRow
                {
                    Label = s.Label, Mount = MergeSurfaces.PathOf(s.Mount), Controller = s.Controller,
                    Kind = s.Kind.ToString(), HasRewrite = s.PathRewrite != null,
                });

                ControllerRules.ParamUsage usage;
                try { usage = ControllerRules.CollectParamUsage(s.Controller); }
                catch (Exception e)
                {
                    res.Notes.Add("parameter walk failed on `" + s.Controller.name + "` (" + e.GetType().Name
                                + ") — its reads/writes are absent from the table below, which is therefore incomplete.");
                    continue;
                }
                string where = s.Label;
                foreach (var kv in usage.Writes) Row(kv.Key).Writers.Add(where + ": " + string.Join(", ", kv.Value));
                foreach (var kv in usage.Reads) Row(kv.Key).Readers.Add(where + ": " + string.Join(", ", kv.Value));
            }

            // Declared columns, mirroring the expression-parameters field grammar rather than inventing one.
            CollectDeclared(descriptor, surfaces, Row, res);
            // Menu controls are writers too, and the commonest one an animator-only read misses entirely.
            CollectMenu(descriptor, surfaces, res, Row);
            CollectDynamicsWriters(root, Row);
            CollectOptimizers(root, res);
            CollectOther(root, res);

            foreach (var r in rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
                if (Matches(r.Name, paramFilter)) res.Params.Add(r);
            return res;
        }

        private static bool Matches(string name, string filter) =>
            string.IsNullOrEmpty(filter) || (name != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);

        private static void CollectDeclared(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            List<MergeSurfaces.Surface> surfaces, Func<string, ParamRow> row, CensusResult res)
        {
            void Ingest(VRCExpressionParameters ep, string where)
            {
                if (ep == null || ep.parameters == null) return;
                foreach (var p in ep.parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.name)) continue;
                    var r = row(p.name);
                    if (!r.DeclaredAt.Contains(where)) r.DeclaredAt.Add(where);
                    r.Diffable = true;
                    r.Type = p.valueType.ToString();
                    r.Synced = p.networkSynced ? "yes" : "no";
                    r.Saved = p.saved ? "yes" : "no";
                    r.Default = p.defaultValue.ToString(CultureInfo.InvariantCulture);
                }
            }
            Ingest(descriptor.expressionParameters, "descriptor expressionParameters");
            foreach (var s in surfaces)
            {
                if (s.Site == null) continue;
                foreach (var ep in VrcfParamAssets(s.Site)) Ingest(ep, s.Label);
            }
            // A controller parameter with no expression-parameters row is not an error — it may be a
            // driver-only scratch value — but it IS the shape a reader mistakes for "undeclared/broken", so
            // the row exists and says which asset did not carry it.
            foreach (var s in surfaces)
            {
                if (s.Controller == null || s.Controller.parameters == null) continue;
                foreach (var p in s.Controller.parameters)
                {
                    var r = row(p.name);
                    r.Diffable = true; // a controller declares it, so the built controllers can carry it
                    if (r.Type == "—") r.Type = p.type.ToString() + " (controller only)";
                }
            }
            res.Notes.Add("declared columns are read from the expression-parameters assets reachable here; "
                        + ScopeAuthoredNames + ".");
        }

        /// <summary>Every VRCExpressionParameters a VRCFury FullController installs, read untyped so this
        /// package keeps referencing no vendor assembly.</summary>
        private static IEnumerable<VRCExpressionParameters> VrcfParamAssets(Component site)
        {
            SerializedObject so;
            try { so = new SerializedObject(site); } catch { yield break; }
            var content = so.FindProperty("content");
            if (content == null) yield break;
            var list = content.FindPropertyRelative("prms");
            if (list == null || !list.isArray) yield break;
            for (int i = 0; i < list.arraySize; i++)
            {
                var el = list.GetArrayElementAtIndex(i);
                var p = el != null ? el.FindPropertyRelative("parameters") : null;
                var objRef = p != null ? p.FindPropertyRelative("objRef") : null;
                if (objRef != null && objRef.objectReferenceValue is VRCExpressionParameters ep) yield return ep;
            }
        }

        // ── Menu coverage (plain mode: the authored union) ────────────────────────────────────────────

        private static void CollectMenu(VRC.SDK3.Avatars.Components.VRCAvatarDescriptor descriptor,
            List<MergeSurfaces.Surface> surfaces, CensusResult res, Func<string, ParamRow> row)
        {
            // Keyed on (asset, source): deduping the WALK is right, but dropping the second installer's
            // provenance is not — a submenu asset two outfits both install is exactly the composed-scope
            // fact this table exists to carry, and provenance is the product.
            var seenAssets = new HashSet<(int asset, string source)>();
            void Walk(VRCExpressionsMenu menu, string source, string path, int depth)
            {
                if (menu == null || menu.controls == null) return;
                if (depth > 12) { res.Notes.Add("menu walk stopped at depth 12 under '" + path + "' — deeper controls are not in the table."); return; }
                if (!seenAssets.Add((menu.GetInstanceID(), source))) return;
                res.MenuAssetsWalked++;
                foreach (var c in menu.controls)
                {
                    if (c == null) continue;
                    string p = string.IsNullOrEmpty(path) ? c.name : path + "/" + c.name;
                    res.Menu.Add(new MenuRow
                    {
                        Source = source, Path = p, ControlName = c.name, Type = c.type.ToString(),
                        Parameter = c.parameter != null && !string.IsNullOrEmpty(c.parameter.name) ? c.parameter.name : "—",
                    });
                    if (c.parameter != null && !string.IsNullOrEmpty(c.parameter.name))
                        row(c.parameter.name).Writers.Add("menu control `" + p + "` [" + source + "]");
                    if (c.subParameters != null)
                        foreach (var sp in c.subParameters)
                            if (sp != null && !string.IsNullOrEmpty(sp.name))
                                row(sp.name).Writers.Add("menu control `" + p + "` sub-parameter [" + source + "]");
                    if (c.subMenu != null) Walk(c.subMenu, source, p, depth + 1);
                }
            }

            Walk(descriptor.expressionsMenu, "descriptor expressionsMenu", "", 0);
            foreach (var s in surfaces)
            {
                if (s.Site == null) continue;
                foreach (var (menu, prefix) in VrcfMenus(s.Site)) Walk(menu, s.Label, prefix, 0);
            }
            // MA installs menus through its own components, which reference nothing this package can type.
            foreach (var c in descriptor.gameObject.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string fn = c.GetType().FullName ?? "";
                if (fn == "nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller")
                {
                    var m = ReadObjectField<VRCExpressionsMenu>(c, "menuToAppend");
                    if (m != null) Walk(m, "MA MenuInstaller @ " + MergeSurfaces.PathOf(c.gameObject), "", 0);
                }
                else if (fn == "nadena.dev.modular_avatar.core.ModularAvatarMenuItem")
                {
                    ReadMenuItem(c, res, row);
                }
            }
        }

        /// <summary>An MA MenuItem authors one control inline rather than pointing at an asset, so it is read
        /// field by field. This is the read <c>ReportGimmick</c> deliberately does not do — its census peeks
        /// one struct level and stops above the driven parameter, which is exactly the field that decides
        /// whether a toggle exists.</summary>
        private static void ReadMenuItem(Component c, CensusResult res, Func<string, ParamRow> row)
        {
            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return; }
            var ctrl = so.FindProperty("Control") ?? so.FindProperty("control");
            if (ctrl == null)
            {
                res.Notes.Add("MA MenuItem @ " + MergeSurfaces.PathOf(c.gameObject)
                            + " has no readable Control field (API drift) — its control is absent from the table.");
                return;
            }
            string name = ctrl.FindPropertyRelative("name")?.stringValue ?? c.gameObject.name;
            var typeProp = ctrl.FindPropertyRelative("type");
            var param = ctrl.FindPropertyRelative("parameter");
            string pname = param?.FindPropertyRelative("name")?.stringValue;
            string source = "MA MenuItem @ " + MergeSurfaces.PathOf(c.gameObject);
            res.Menu.Add(new MenuRow
            {
                Source = source, Path = name, ControlName = name,
                Type = typeProp != null ? typeProp.enumDisplayNames[Mathf.Clamp(typeProp.enumValueIndex, 0, typeProp.enumDisplayNames.Length - 1)] : "—",
                Parameter = string.IsNullOrEmpty(pname) ? "—" : pname,
            });
            if (!string.IsNullOrEmpty(pname)) row(pname).Writers.Add("MA menu item `" + name + "` [" + source + "]");
        }

        private static IEnumerable<(VRCExpressionsMenu menu, string prefix)> VrcfMenus(Component site)
        {
            SerializedObject so;
            try { so = new SerializedObject(site); } catch { yield break; }
            var content = so.FindProperty("content");
            var list = content != null ? content.FindPropertyRelative("menus") : null;
            if (list == null || !list.isArray) yield break;
            for (int i = 0; i < list.arraySize; i++)
            {
                var el = list.GetArrayElementAtIndex(i);
                var m = el != null ? el.FindPropertyRelative("menu") : null;
                var objRef = m != null ? m.FindPropertyRelative("objRef") : null;
                string prefix = el?.FindPropertyRelative("prefix")?.stringValue ?? "";
                if (objRef != null && objRef.objectReferenceValue is VRCExpressionsMenu menu) yield return (menu, prefix);
            }
        }

        // ── Non-animator writers ─────────────────────────────────────────────────────────────────────

        /// <summary>Contacts, physbones and raycasts write parameters no controller mentions. The physbone
        /// suffix family is the one an animator-only read never sees: the component declares a PREFIX and
        /// the runtime writes prefix+suffix, so the parameter a controller reads appears nowhere in the
        /// scene as a literal string.</summary>
        private static void CollectDynamicsWriters(GameObject root, Func<string, ParamRow> row)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string where = c.GetType().Name + " @ " + MergeSurfaces.PathOf(c.gameObject);
                if (c is VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone pb)
                {
                    if (string.IsNullOrEmpty(pb.parameter)) continue;
                    foreach (var suffix in new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" })
                        row(pb.parameter + suffix).Writers.Add(where + " (physbone suffix)");
                }
                else if (c is VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver cr)
                {
                    if (!string.IsNullOrEmpty(cr.parameter)) row(cr.parameter).Writers.Add(where + " (contact receiver)");
                }
            }
        }

        private static void CollectOptimizers(GameObject root, CensusResult res)
        {
            // Reported, never controlled: the full chain is what ships, so an agent wanting a pre-optimizer
            // view disables these itself before invoking. Naming them is the one honesty measure a bake owes
            // its reader — a table taken through an optimizer is not the authored shape.
            foreach (var c in root.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n == "d4rkAvatarOptimizer" || n == "TextureCompressor" || n.IndexOf("Optimizer", StringComparison.Ordinal) >= 0)
                    res.Optimizers.Add(c.GetType().FullName);
            }
        }

        /// <summary>Tier-2 census: every non-transform component under the root that no table above read, by
        /// type, so <c>other=0</c> genuinely means empty rather than meaning nobody looked.</summary>
        private static void CollectOther(GameObject root, CensusResult res)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t == typeof(Transform) || t == typeof(RectTransform)) continue;
                string fn = t.FullName ?? t.Name;
                if (Interpreted(fn)) continue;
                counts.TryGetValue(fn, out int n);
                counts[fn] = n + 1;
            }
            foreach (var kv in counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
                res.Other.Add(kv.Key + "×" + kv.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static bool Interpreted(string fullName) =>
            fullName == "VF.Model.VRCFury"
            || fullName == "nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator"
            || fullName == "nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller"
            || fullName == "nadena.dev.modular_avatar.core.ModularAvatarMenuItem"
            || fullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"
            || fullName == "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone"
            || fullName == "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver"
            || fullName == "UnityEngine.Animator";

        // ── Emit ─────────────────────────────────────────────────────────────────────────────────────

        internal static string EmitPlain(GameObject root, CensusResult c, string paramFilter)
        {
            var body = RenderBody(root, c, paramFilter, "plain (authored census)", null);
            string summary = string.Format(CultureInfo.InvariantCulture,
                "[ReportComposition] {0}: surfaces={1} params={2} menuControls={3} optimizers={4} other={5} mode=plain => OK",
                root.name, c.Surfaces.Count, c.Params.Count, c.Menu.Count, c.Optimizers.Count, c.Other.Count);
            string res = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "composition_" + RunLogFormat.Sanitize(root.name),
                summary, body, ".md");
            Debug.Log(res + "\n" + Window(c, paramFilter));
            return res;
        }

        internal static string RenderBody(GameObject root, CensusResult c, string paramFilter, string mode,
            List<string> bakeSection)
        {
            var sb = new StringBuilder();
            sb.Append("# ReportComposition: ").Append(root.name).Append('\n');
            sb.Append("root: `").Append(MergeSurfaces.PathOf(root)).Append("`  \n");
            sb.Append("mode: ").Append(mode).Append("  \n");
            if (!string.IsNullOrEmpty(paramFilter))
                sb.Append("paramFilter: `").Append(paramFilter).Append("` — every parameter table below is NARROWED to matching names; counts are of the filtered set.  \n");
            sb.Append("optimizers: ").Append(c.Optimizers.Count == 0 ? "(none on the root)" : string.Join(", ", c.Optimizers)).Append("  \n");
            foreach (var n in c.Notes) sb.Append("> note: ").Append(n).Append("  \n");

            sb.Append("\n## Merge surfaces\n\n| surface | mount | controller | kind | rewriteBindings |\n| --- | --- | --- | --- | --- |\n");
            if (c.Surfaces.Count == 0) sb.Append("| _(none)_ | | | | |\n");
            foreach (var s in c.Surfaces)
                sb.Append("| ").Append(RunLogFormat.Cell(s.Label)).Append(" | `").Append(RunLogFormat.Cell(s.Mount)).Append("` | `")
                  .Append(RunLogFormat.Cell(s.Controller != null ? s.Controller.name : "—")).Append("` | ").Append(s.Kind)
                  .Append(" | ").Append(s.HasRewrite ? "yes" : "no").Append(" |\n");

            sb.Append("\n## Parameters\n\n| parameter | declared | type | synced | saved | default | writers | readers |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n");
            if (c.Params.Count == 0) sb.Append("| _(none)_ | | | | | | | |\n");
            foreach (var p in c.Params)
                sb.Append("| `").Append(RunLogFormat.Cell(p.Name)).Append("` | ").Append(RunLogFormat.Cell(p.Declared)).Append(" | ")
                  .Append(p.Type).Append(" | ").Append(p.Synced).Append(" | ").Append(p.Saved).Append(" | ")
                  .Append(p.Default).Append(" | ").Append(RunLogFormat.Cell(Cell(p.Writers, ScopeWriters)))
                  .Append(" | ").Append(RunLogFormat.Cell(Cell(p.Readers, "(none among scanned surfaces)"))).Append(" |\n");

            sb.Append("\n## Menu controls\n\n| path | type | parameter | source |\n| --- | --- | --- | --- |\n");
            if (c.Menu.Count == 0) sb.Append("| _(none)_ | | | |\n");
            foreach (var m in c.Menu)
                sb.Append("| `").Append(m.Path).Append("` | ").Append(m.Type).Append(" | `").Append(m.Parameter)
                  .Append("` | ").Append(RunLogFormat.Cell(m.Source)).Append(" |\n");
            sb.Append("\nmenu assets walked: ").Append(c.MenuAssetsWalked)
              .Append(" — a menu asset can hold more than one YAML document, and every document reachable through a `subMenu` reference is walked here.\n");

            sb.Append("\n## Other components (tier-2 census)\n\n");
            sb.Append(c.Other.Count == 0 ? "_(none — every component under the root was read by a table above)_\n"
                                         : string.Join(", ", c.Other) + "\n");

            if (bakeSection != null)
            {
                sb.Append("\n## Bake diff\n\n");
                foreach (var l in bakeSection) sb.Append(l).Append('\n');
            }
            // Scope is emitted in BOTH modes. It used to be the `else` arm of the bake section, so a bake
            // artifact — the one whose heading promises composed truth — lost every scope rule while still
            // rendering the whole Parameters table above, including its authored-only `synced` column.
            sb.Append("\n## Scope\n\n");
            if (bakeSection == null)
                sb.Append("Plain mode reports what is AUTHORED. It makes no namespace-resolution claim: ").Append(ScopeAuthoredNames).Append(".\n");
            else
                sb.Append("The **Bake diff** section is measured against a fresh build. Everything ABOVE it — the ")
                  .Append("merge-surface, parameter and menu tables — is still the authored census, and the bake ")
                  .Append("resolves only the names: read a row's build-time identity from the diff, not from the tables.\n");
            sb.Append("An empty writers cell reads `").Append(ScopeWriters).Append("` because the writer set for a parameter is open — an empty cell is not a finding.\n");
            if (bakeSection != null)
                sb.Append("**The `synced` / `saved` / `default` columns are read from the authored parameters assets, and bake ")
                  .Append("mode does not revisit them.** On an avatar whose synced bits overflow, the build re-plans sync ")
                  .Append("entirely and a parameter can read un-synced while still replicating — `docs/runtime.md` ")
                  .Append("§VRCFury build-time reshaping owns that trap and names where the build records what it did. ")
                  .Append("Nothing in this artifact is evidence about sync state.\n");
            sb.Append("Humanoid mapping is not read here; `CheckHumanoidRig.InspectAvatar` is the door that reports a humanoid-vs-skinned divergence.\n");
            return sb.ToString();
        }

        private static string Cell(List<string> entries, string emptyNote) =>
            entries.Count == 0 ? emptyNote : string.Join("; ", entries);

        private static string Window(CensusResult c, string paramFilter)
        {
            var sb = new StringBuilder();
            sb.Append("surfaces: ").Append(c.Surfaces.Count == 0 ? "(none)"
                : string.Join(", ", c.Surfaces.Take(WindowRows).Select(s => s.Label))).Append('\n');
            int shown = Math.Min(WindowRows, c.Params.Count);
            for (int i = 0; i < shown; i++)
            {
                var p = c.Params[i];
                sb.Append("  ").Append(p.Name).Append(" | writers=").Append(Cell(p.Writers, ScopeWriters))
                  .Append(" | readers=").Append(Cell(p.Readers, "(none among scanned surfaces)")).Append('\n');
            }
            if (c.Params.Count > shown)
                sb.Append("  … ").Append(c.Params.Count - shown).Append(" more parameter row(s) in the artifact")
                  .Append(string.IsNullOrEmpty(paramFilter) ? " — pass paramFilter to narrow this to one" : "").Append('\n');
            return sb.ToString();
        }

        // ── Shared helpers ───────────────────────────────────────────────────────────────────────────

        private static string Refuse(string why)
        {
            string s = "[ReportComposition] FAIL: " + why;
            Debug.LogError(s);
            return s;
        }

        private static T ReadObjectField<T>(Component c, string field) where T : UnityEngine.Object
        {
            SerializedObject so;
            try { so = new SerializedObject(c); } catch { return null; }
            var p = so.FindProperty(field);
            return p != null ? p.objectReferenceValue as T : null;
        }

        internal static GameObject FindByHierarchyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var direct = GameObject.Find(path);
            if (direct != null) return direct;
            // GameObject.Find never returns an inactive object, and an authoring avatar is routinely parked
            // inactive — walk the loaded scenes' roots instead rather than reporting a live avatar as absent.
            var segs = path.Split('/');
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var r in scene.GetRootGameObjects())
                {
                    if (r.name != segs[0]) continue;
                    var t = r.transform;
                    for (int s = 1; s < segs.Length && t != null; s++) t = t.Find(segs[s]);
                    if (t != null) return t.gameObject;
                }
            }
            return null;
        }
    }
}
