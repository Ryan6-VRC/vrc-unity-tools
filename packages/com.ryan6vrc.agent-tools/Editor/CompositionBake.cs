using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// <see cref="ReportComposition"/>'s bake mode: measure the composed truth instead of inferring it.
    /// It builds a fresh clone through <see cref="AvatarBake"/> and diffs the built parameter set against
    /// the authored census, so the rewrite a build performs is READ rather than modelled — no vendor
    /// name-mangling grammar is pinned anywhere here, because a mis-pinned one returns plausible strings
    /// with nothing to signal they are wrong.
    ///
    /// <b>Two-phase, by measurement.</b> A full preprocess chain on a complex avatar with an optimizer
    /// installed runs past the MCP transport's patience, and a lost reply on a synchronous call discards a
    /// result the editor actually produced. So <see cref="Begin"/> writes the artifact path FIRST, schedules
    /// the work, and returns <c>PENDING</c> with that path; <see cref="Verify"/> re-reads it. The transport
    /// re-sends a timed-out payload, so <see cref="Begin"/> is idempotent while a bake is in flight — a
    /// duplicate returns the same pending path rather than starting a second build.
    ///
    /// Deferred off <c>EditorApplication.update</c>, never <c>delayCall</c>: an MCP-driven editor is
    /// unfocused, where <c>delayCall</c> queues indefinitely and fires on the click that focuses the window.
    ///
    /// <b>Freshness is by construction.</b> The clone is built here and destroyed after the read; no
    /// previously-baked artifact under <c>com.vrcfury.temp</c> is ever consulted, because those reflect the
    /// VRCFury version that produced them and a stale one can differ structurally from what the installed
    /// version now emits.
    /// </summary>
    internal static class CompositionBake
    {
        private const string Pending = "pending";
        /// <summary>Well past the ~30 s worst case measured on a complex avatar with an optimizer installed,
        /// so a slow bake is never called dead; short enough that a discarded callback is not a long wedge.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
        private static readonly Dictionary<int, bool> InFlight = new Dictionary<int, bool>();

        // Keyed on name AND instance id: the path must be STABLE (the caller re-reads it after a transport
        // timeout, so a timestamp is out) but two roots named "Avatar" in one scene would otherwise share a
        // file — B's pending stub overwriting A's result, and Verify(B) returning A's summary as B's.
        private static string ArtifactPath(GameObject root) =>
            RunLogFormat.SnapshotDir + "/composition-bake_" + RunLogFormat.Sanitize(root.name)
            + "_" + root.GetInstanceID().ToString("X", CultureInfo.InvariantCulture) + ".md";

        internal static string Begin(GameObject root, ReportComposition.CensusResult census, string paramFilter)
        {
            string path = ArtifactPath(root);
            int key = root.GetInstanceID();
            if (InFlight.TryGetValue(key, out bool running) && running)
                return "[ReportComposition] " + root.name + ": mode=bake => PENDING (already running) | log=" + path;

            // The path is on disk BEFORE the work starts, so a transport timeout loses nothing.
            WriteArtifact(path, "# ReportComposition (bake): " + root.name + "\n\nstatus: " + Pending
                + "\n\nThe bake is running. Re-read this file, or call `ReportComposition.Verify(<avatarRoot>)`.\n"
                + "Do NOT re-issue the bake: a second call while this one is in flight returns this same path.\n");
            InFlight[key] = true;

            var rootRef = root;
            EditorApplication.CallbackFunction step = null;
            step = () =>
            {
                EditorApplication.update -= step;
                try { Run(rootRef, census, paramFilter, path); }
                finally { InFlight[key] = false; }
            };
            EditorApplication.update += step;

            return "[ReportComposition] " + root.name + ": mode=bake => PENDING | log=" + path;
        }

        internal static string Verify(GameObject root)
        {
            string path = ArtifactPath(root);
            string full = FullPath(path);
            if (!File.Exists(full))
                return "[ReportComposition] FAIL: no bake artifact at " + path + " — run Report(<avatarRoot>, bake:true) first";
            string text = File.ReadAllText(full);
            if (text.Contains("status: " + Pending))
            {
                // A domain reload discards the scheduled callback WITHOUT running it and clears the in-memory
                // in-flight flag, leaving the artifact reading `pending` forever. Nothing in the editor can be
                // asked whether the callback still exists, so age is the only available signal — and without
                // it the artifact's own "do not re-issue" instruction wedges the door permanently.
                var m = Regex.Match(text, @"^started: (.+)$", RegexOptions.Multiline);
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var started)
                    && DateTime.UtcNow - started > StaleAfter)
                    return "[ReportComposition] " + root.name + ": mode=bake => STALE (started "
                         + started.ToString("o", CultureInfo.InvariantCulture) + ", over "
                         + StaleAfter.TotalMinutes.ToString(CultureInfo.InvariantCulture)
                         + " min ago and still pending — the scheduled bake was almost certainly discarded by a "
                         + "domain reload; re-issue Report(<avatarRoot>, bake:true)) | log=" + path;
                return "[ReportComposition] " + root.name + ": mode=bake => PENDING | log=" + path;
            }
            string head = text.Split('\n').FirstOrDefault(l => l.StartsWith("summary: ", StringComparison.Ordinal));
            return head != null ? head.Substring("summary: ".Length).Trim()
                                : "[ReportComposition] " + root.name + ": mode=bake => OK | log=" + path;
        }

        private static void Run(GameObject root, ReportComposition.CensusResult census, string paramFilter, string path)
        {
            if (root == null)
            {
                WriteArtifact(path, Refusal(path, "the avatar root was destroyed before the bake ran", "(destroyed)"));
                return;
            }

            // `using`, so the SDK's paired post-callback fires on EVERY exit — including the refusal return
            // below, which sits inside the scope precisely because that early return used to skip the pairing.
            // The read happens INSIDE the scope by necessity, not by style: the post-callback destroys the
            // clone's generated assets, so every playable-layer controller BuiltDeclarations exists to read is
            // null once the scope closes (AvatarBake's doc comment has the measurement).
            using (var bake = AvatarBake.Begin(root))
            {
                if (!bake.Ok)
                {
                    // A loud refusal naming the stage — NEVER a silent fallback to the authored census. Emitting
                    // authored rows under a heading that promises composed truth is the failure this door exists
                    // to prevent, so bake mode publishes no table at all when the bake did not happen. A hook
                    // that THREW is reported as the crash it was, not as a refusal that never happened.
                    string failedStage = bake.DescribeFailure();
                    WriteArtifact(path, Refusal(path, failedStage, root.name));
                    Debug.LogError("[ReportComposition] " + root.name + ": mode=bake => FAIL (" + failedStage + ") | log=" + path);
                    return;
                }

                var clone = bake.Clone;
                string incomplete;
                var readNotes = new List<string>();
                var built = BuiltDeclarations(clone, readNotes, out incomplete);
                var diff = Diff(census, built, paramFilter, incomplete == null);
                string geometryKeys;
                var geometry = GeometrySection(ReadGeometry(root), ReadGeometry(clone), paramFilter, out geometryKeys);
                // Both texture reads sit INSIDE the scope for ReadGeometry's reason: the optimizers' output
                // textures are NDMF `__Generated` assets the post-callback deletes, so after the scope closes
                // the built side has nothing left to measure.
                int swapAuthored, swapBuilt;
                var texAuthored = ReadTextures(root, out swapAuthored);
                var texBuilt = ReadTextures(clone, out swapBuilt);
                string textureKeys;
                var textures = TextureSection(texAuthored, texBuilt,
                    SdkTextureMegabytes(root), SdkTextureMegabytes(clone),
                    swapAuthored, swapBuilt, paramFilter, out textureKeys);
                string summary = string.Format(CultureInfo.InvariantCulture,
                    // `unattributed=` is deliberately GONE rather than kept with a narrower meaning: it used to
                    // count ambiguity and built-only rows together, so preserving the key while the number moves
                    // (145 → 8 on one measured avatar) would leave a reader comparing two artifacts with no
                    // signal that the denominator changed. Renaming both halves makes the change visible.
                    // unlintableSurfaces rides here too, not only EmitPlain: bake is the EXACTNESS mode, so a
                    // surface this run could not walk is where the omission costs most.
                    "[ReportComposition] {0}: surfaces={1}{13} params={2} kept={3} renamed={4} dropped={5} merged={6} ambiguous={7} builtOnly={8} vrcReserved={9} notInScope={10} builtSideUnread={11} {14} {15} mode=bake => OK | log={12}",
                    root.name, census.Surfaces.Count, census.Params.Count,
                    diff.Count(d => d.Category == "kept"), diff.Count(d => d.Category == "renamed"),
                    diff.Count(d => d.Category == "dropped"), diff.Count(d => d.Category == "merged"),
                    diff.Count(d => d.Category == "ambiguous"), diff.Count(d => d.Category == "built-only"),
                    diff.Count(d => d.Category == "vrc-reserved"),
                    diff.Count(d => d.Category == "not-in-scope"),
                    diff.Count(d => d.Category == "built-side-unread"), path,
                    census.UnlintableSurfaces > 0 ? " unlintableSurfaces=" + census.UnlintableSurfaces : "",
                    geometryKeys, textureKeys);

                var section = new List<string>
                {
                    "| authored | category | built | owning surface |",
                    "| --- | --- | --- | --- |",
                };
                foreach (var d in diff)
                    section.Add("| `" + RunLogFormat.Cell(d.Authored) + "` | " + d.Category + " | `" + RunLogFormat.Cell(d.Built) + "` | "
                              + RunLogFormat.Cell(d.Surface + d.Caveat) + " |");
                section.Add("");
                // Each note ends with a hard break and the block ends with a blank line, matching RenderBody's own
                // note convention: without both, CommonMark lazy continuation folds every following paragraph —
                // the incompleteness warning, the category legend, the arithmetic — into this blockquote.
                foreach (var n in readNotes) section.Add("> built-side read note: " + n + "  ");
                if (readNotes.Count > 0) section.Add("");
                if (incomplete != null)
                    section.Add("**The built read was incomplete: " + incomplete + ".** Exact matches and renames below "
                              + "are still measured facts; every unmatched authored name is reported "
                              + "`built-side-unread` rather than `dropped`, because this read cannot tell removal from "
                              + "not-looking. Treat the absence of a row as unknown, not as evidence.");
                section.Add("The built side is a DECLARATION set: the built descriptor's expression parameters union every "
                          + "parameter on every controller the clone plays. Every category below is relative to that.");
                section.Add("Categories: **kept** the built avatar declares the authored name unchanged; **renamed** exactly "
                          + "one built name is a prefixed form of it (the authored name behind a `/` or `_` separator); "
                          + "**dropped** no built name is a prefixed form of it at all; **merged** two or more authored names "
                          + "resolved onto one built name, replacing their individual rows so the counts still sum; "
                          + "**built-side-unread** no match AND the built read was partial, so removal is not claimed; "
                          + "**ambiguous** an authored name more than one built name could be, none attributed; "
                          + "**built-only** a BUILT name no authored one claims — a measured fact, and the normal home of "
                          + "build-minted internals and SDK-supplied names; **vrc-reserved** the same, for a name on VRChat's "
                          + "reserved list (`ControllerRules.IsVrcReserved`, the one predicate the lint rules and the "
                          + "controller compiler also use) — split out because those rows are the SDK's, not this avatar's, "
                          + "and reading a dozen of them as unexplained build output is the misread; **not-in-scope** a runtime-written name (physbone "
                          + "suffix, menu sub-parameter) that nothing declares, so a declaration set cannot carry it and its "
                          + "absence means nothing.");
                section.Add("**One parameter can occupy two rows, and they say so.** A runtime-written name the build carries "
                          + "verbatim is `not-in-scope` on the authored side (it declares nothing to diff) and `built-only` on "
                          + "the built side (no row can claim it). Both are true; neither is a finding. Each names the other, so "
                          + "the identical string is read as one parameter rather than as a drop beside an unexplained addition.");
                section.Add("Attribution is inference, not a pinned grammar — an `ambiguous` row is the honest answer and "
                          + "beats a mapping the tool cannot support. The separator boundary is the whole of the rule; a "
                          + "bare suffix match would call authored `Toggle` a rename of built `Hair/HairToggle`.");
                section.Add("**A built name already claimed by exact match is not another row's rename** — an exact match is "
                          + "the stronger claim. That precedence is a tie-breaker only: where it would empty a candidate set "
                          + "it is not applied, so `dropped` never rests on it. Where it decided a row — `renamed` or "
                          + "`ambiguous` — that row says so in its own owning-surface cell and names what was excluded, "
                          + "because the reading it forecloses is `merged`; and where such a row was itself replaced by a "
                          + "`merged` row, the merged row carries the same disclosure.");
                section.Add("**The arithmetic.** Authored rows (`kept` + `renamed` + `dropped` + `ambiguous` + "
                          + "`built-side-unread` + `not-in-scope`, plus each `merged` row standing in for the two or more it "
                          + "replaced) sum to `params=`; `built-only` and `vrc-reserved` rows are additional and belong to no "
                          + "authored name. One "
                          + "built name can appear BOTH inside an `A or B` cell and as its own `built-only` row: an ambiguous "
                          + "match deliberately claims nothing, or a genuinely built-only parameter would be hidden inside a "
                          + "cell belonging to an unrelated authored name. That is not double-counting.");
                section.Add("Optimizers found on the root: "
                          + (census.Optimizers.Count == 0 ? "(none)" : string.Join(", ", census.Optimizers))
                          + ". The full chain is what ships and is what was measured; disable them yourself for a "
                          + "pre-optimizer view.");

                string body = "summary: " + summary + "\n\n"
                            + ReportComposition.RenderBody(root, census, paramFilter, "bake (measured against a fresh build)", section, geometry, textures);
                WriteArtifact(path, body);
                Debug.Log(summary);
            }   // scope closes: the clone is destroyed and OnPostprocessAvatar fires, in that order
        }

        // ── Geometry: triangles, authored against built ───────────────────────────────────────────────

        /// <summary>One renderer's triangles on one side of the diff. <c>Unreadable</c> is a THIRD state, not
        /// a zero: a renderer whose mesh is null, destroyed or unreadable is a named row with an empty count,
        /// because a 0 in a triangle column reads as "the build emptied this" — the one claim this section
        /// exists to let a reader make, and the one it must not manufacture.</summary>
        internal struct GeoRow
        {
            public string Path;      // hierarchy path relative to the side's own root
            public bool Skinned;     // SkinnedMeshRenderer; false is a MeshRenderer read through its MeshFilter
            public bool Active;      // active in hierarchy AND the renderer component enabled
            public long Tris;
            public bool Unreadable;
            public string Caveat;
        }

        /// <summary>Every <c>SkinnedMeshRenderer</c> and <c>MeshRenderer</c> under <paramref name="root"/>,
        /// active or not: the build merges toggled-off renderers into an always-active mesh under NaNimation,
        /// so "active" is runtime state and filtering on it would report a merge as a deletion.
        /// <para><b>The built side is readable only inside the bake scope</b>, which is why the call sits where
        /// it does in <see cref="Run"/>: both optimizers' output meshes live only in memory — AAO's under
        /// NDMF's generated-asset root that the post-callback's <c>CleanupTemporaryAssets</c> deletes, d4rk's a
        /// bare <c>new Mesh()</c> never persisted — so after the scope closes there is nothing left to count.
        /// The other half of the same question is what the chain SKIPS when no upload is happening: VRCFury's
        /// <c>DisablePluginsWhenNotUploadingHook</c> inhibits six named callback types (Poiyomi's and liltoon's
        /// material lockdown, UdonSharp's recompile, and the SDK's product/network id assignment), none of
        /// which touches a mesh — read from VRCFury 1.1427.0's source; re-read that list if a triangle count
        /// ever disagrees with an uploaded avatar's.</para></summary>
        internal static List<GeoRow> ReadGeometry(GameObject root)
        {
            var rows = new List<GeoRow>();
            if (root == null) return rows;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                Mesh mesh;
                bool skinned = r is SkinnedMeshRenderer;
                if (skinned) mesh = ((SkinnedMeshRenderer)r).sharedMesh;
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    mesh = mf != null ? mf.sharedMesh : null;
                }
                else continue;   // particle, trail and line renderers carry no mesh this read counts

                var row = new GeoRow
                {
                    Path = RelPath(root, r.transform), Skinned = skinned,
                    Active = r.gameObject.activeInHierarchy && r.enabled,
                };
                if (mesh == null || !mesh.isReadable)
                {
                    row.Unreadable = true;
                    row.Caveat = mesh == null
                        ? "no mesh (null, destroyed, or a MeshRenderer with no MeshFilter)"
                        : "mesh is not readable, so its index buffer cannot be counted here";
                }
                else
                {
                    var skippedTopologies = new List<string>();
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var topology = mesh.GetTopology(s);
                        if (topology == MeshTopology.Triangles) row.Tris += mesh.GetIndexCount(s) / 3;
                        else skippedTopologies.Add("submesh " + s + " is " + topology);
                    }
                    if (skippedTopologies.Count > 0)
                        row.Caveat = "not counted: " + string.Join(", ", skippedTopologies);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static string RelPath(GameObject root, Transform t)
        {
            if (t == root.transform) return "(root)";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != root.transform; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        /// <summary>The <c>## Geometry</c> table, and (out) the built side's three summary keys. Pure — rows in,
        /// lines out — so the arithmetic every reading of this section rests on is unit-testable without a bake.
        /// <para>Rows are keyed on path AND kind, because one transform may carry both a skinned and a mesh
        /// renderer and a path-only key would silently fold them into one row. <paramref name="paramFilter"/>
        /// narrows nothing here — it is a parameter-name filter, and a geometry table narrowed by it would
        /// report a partial avatar under totals that read whole — so it is only disclosed.</para></summary>
        internal static List<string> GeometrySection(List<GeoRow> authored, List<GeoRow> built,
                                                     string paramFilter, out string summaryKeys)
        {
            var a = KeyRows(authored);
            var b = KeyRows(built);

            var lines = new List<string>
            {
                "built triangles, authored against the clone; paramFilter does not narrow this section"
                    + (string.IsNullOrEmpty(paramFilter) ? ""
                       : " (`" + RunLogFormat.Cell(paramFilter) + "` is active and narrows the parameter tables ONLY, so these rows and totals are of the WHOLE avatar)"),
                "",
                "| renderer (path) | kind | active | authored tris | built tris | caveat |",
                "| --- | --- | --- | --- | --- | --- |",
            };
            foreach (var k in a.Keys.Union(b.Keys).OrderBy(s => s, StringComparer.Ordinal))
            {
                bool inA = a.ContainsKey(k), inB = b.ContainsKey(k);
                var row = inB ? b[k] : a[k];
                string presence = inA && inB ? "" : inB ? " (built-only)" : " (authored-only)";
                var caveats = new List<string>();
                if (inA && !string.IsNullOrEmpty(a[k].Caveat)) caveats.Add((inB ? "authored: " : "") + a[k].Caveat);
                if (inB && !string.IsNullOrEmpty(b[k].Caveat)) caveats.Add((inA ? "built: " : "") + b[k].Caveat);
                // The `active` cell reports the BUILT side where there is one; a side that disagrees is named
                // rather than dropped, since a renderer the build activates is exactly the NaNimation merge the
                // population rule above exists for.
                if (inA && inB && a[k].Active != b[k].Active)
                    caveats.Add("active differs: authored=" + (a[k].Active ? "yes" : "no"));
                lines.Add("| `" + RunLogFormat.Cell(row.Path) + "` | " + (row.Skinned ? "skinned" : "mesh") + presence
                        + " | " + (row.Active ? "yes" : "no")
                        + " | " + TriCell(a, k) + " | " + TriCell(b, k)
                        + " | " + RunLogFormat.Cell(string.Join("; ", caveats)) + " |");
            }
            foreach (var t in new[] { "all", "skinned", "active" })
                lines.Add("| total (" + t + ") | | | " + Subtotal(authored, t) + " | " + Subtotal(built, t) + " | |");
            lines.Add("");
            lines.Add("**The three subtotals.** `all` is every renderer counted here; `skinned` only the "
                    + "`SkinnedMeshRenderer`s; `active` only those of them active in the hierarchy with the "
                    + "component enabled. A **built-only** row is where a merge landed — compare it yourself "
                    + "against the sum of the authored rows that fed it, which no static read can identify. "
                    + "An **authored-only** row carries geometry the build removed. This section attributes "
                    + "nothing to a blendshape: a drop says the triangles went, never which shape's footprint "
                    + "they were (`ReportShapeOverlap` prints an authored footprint per shape at edit time).");
            summaryKeys = "tris=" + Subtotal(built, "all") + " trisSkinned=" + Subtotal(built, "skinned")
                        + " trisActive=" + Subtotal(built, "active");
            return lines;
        }

        /// <summary>Unity permits same-named siblings, so path plus kind is not unique: a second renderer on the
        /// same key takes an ordinal suffix on its displayed path (` #2`, ` #3`, …) in hierarchy order, so every
        /// renderer keeps its own row and the two sides pair by that order rather than one silently overwriting
        /// the other.</summary>
        private static Dictionary<string, GeoRow> KeyRows(List<GeoRow> rows)
        {
            var d = new Dictionary<string, GeoRow>(StringComparer.Ordinal);
            foreach (var r0 in rows)
            {
                var r = r0;
                string kind = r.Skinned ? "skinned" : "mesh";
                string basePath = r.Path;
                for (int n = 2; d.ContainsKey(r.Path + " " + kind); n++) r.Path = basePath + " #" + n;
                d[r.Path + " " + kind] = r;
            }
            return d;
        }

        private static string TriCell(Dictionary<string, GeoRow> side, string k)
        {
            if (!side.ContainsKey(k)) return "—";
            var r = side[k];
            return r.Unreadable ? "unreadable" : r.Tris.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>An unreadable row contributes nothing to any subtotal — its count is unknown, not zero —
        /// which is why its row says `unreadable` where a total could not.</summary>
        private static string Subtotal(List<GeoRow> side, string which)
        {
            long sum = 0;
            foreach (var r in side)
            {
                if (r.Unreadable) continue;
                if (which == "skinned" && !r.Skinned) continue;
                if (which == "active" && !(r.Skinned && r.Active)) continue;
                sum += r.Tris;
            }
            return sum.ToString(CultureInfo.InvariantCulture);
        }

        // ── Textures: memory, authored against built ──────────────────────────────────────────────────

        /// <summary>One distinct texture on one side of the diff, with the slots that reach it.
        /// <para><c>Unknown</c> is the THIRD state, and it is the analogue of <see cref="GeoRow.Unreadable"/>
        /// for the same reason: a 0 in a megabyte column reads as "this costs nothing", which is a claim this
        /// section must never manufacture. A texture whose <c>TextureFormat</c> this arithmetic cannot type,
        /// and every non-<c>Texture2D</c> (RenderTexture, Cubemap, Texture2DArray), is Unknown — named in its
        /// row, excluded from the total and from the ordering. AAO's own reader guesses 16 bpp on an
        /// unrecognised format and logs an error doing it; a guess laundered into a table a reader sorts by
        /// size is worse than an honest gap.</para></summary>
        internal struct TexRow
        {
            public string Slot;        // "<renderer path>[slot].<shader property>" — first slot reaching this texture
            public int OtherSlots;     // how many further slots reach the same texture; it is counted ONCE
            public string SlotKey;     // every reaching slot, ordinal-sorted — the pairing key when identity fails
            public string Identity;    // asset path, else instance id: survives a renderer merge, dies on a resize
            public int Width, Height, Mips;
            public string Format;
            public long Bytes;
            public bool Unknown;
            public string Caveat;
        }

        /// <summary>Bytes per pixel for the formats this arithmetic can type. Verified against the SDK's own
        /// <c>textureMegabytes</c>: summing <c>width*height*bpp</c> over the mip chain for 17 textures on a
        /// real avatar reproduced the SDK's figure to five decimals (18.18228 MB), so these rows and the
        /// reported total are the SAME measurement rather than two estimators printed side by side.
        /// A format absent here is Unknown, never a guess — this is PC-facing (<c>optimization.md</c> works
        /// PC only), so the ASTC/ETC families are deliberately absent rather than wrong.</summary>
        private static readonly Dictionary<TextureFormat, float> BytesPerPixel = new Dictionary<TextureFormat, float>
        {
            { TextureFormat.DXT1, 0.5f },   { TextureFormat.DXT1Crunched, 0.5f },
            { TextureFormat.DXT5, 1f },     { TextureFormat.DXT5Crunched, 1f },
            { TextureFormat.BC7, 1f },      { TextureFormat.BC6H, 1f },
            { TextureFormat.BC4, 0.5f },    { TextureFormat.BC5, 1f },
            { TextureFormat.Alpha8, 1f },   { TextureFormat.R8, 1f },
            { TextureFormat.R16, 2f },      { TextureFormat.RG16, 2f },
            { TextureFormat.RGB24, 3f },    { TextureFormat.RGBA32, 4f },
            { TextureFormat.ARGB32, 4f },   { TextureFormat.BGRA32, 4f },
            { TextureFormat.RGB565, 2f },   { TextureFormat.RGBA4444, 2f },
            { TextureFormat.ARGB4444, 2f }, { TextureFormat.RHalf, 2f },
            { TextureFormat.RGHalf, 4f },   { TextureFormat.RGBAHalf, 8f },
            { TextureFormat.RFloat, 4f },   { TextureFormat.RGFloat, 8f },
            { TextureFormat.RGBAFloat, 16f },
        };

        /// <summary>Every texture reachable from every renderer's shared materials under <paramref name="root"/>,
        /// active or not — the same population rule <see cref="ReadGeometry"/> states, and measured the same way:
        /// deactivating every renderer object on a real avatar moved the SDK's <c>textureMegabytes</c> by zero,
        /// so an inactive renderer's textures count at full weight.
        /// <para><b>This walks every <c>Renderer</c>, not <see cref="ReadGeometry"/>'s loop</b>, which
        /// <c>continue</c>s past particle, trail and line renderers because they carry no mesh. They carry
        /// materials, and those textures are in the megabyte figure.</para>
        /// <para><paramref name="swapOnlyMaterials"/> is the disclosed omission: materials no renderer's
        /// <c>sharedMaterials</c> holds, reachable only through an <c>m_Materials</c> object-reference curve.
        /// They are COUNTED, not rowed — the optimizers that own this stat do reach them (AAO unions the
        /// animated materials per renderer; Limitex walks the animator and the components besides), so their
        /// textures sit inside the SDK total while being in no row here, and an undisclosed gap in a table a
        /// reader uses to pick a target is this section's worst failure.</para></summary>
        internal static List<TexRow> ReadTextures(GameObject root, out int swapOnlyMaterials)
        {
            var rows = new List<TexRow>();
            swapOnlyMaterials = 0;
            if (root == null) return rows;

            var order = new List<Texture>();
            var slots = new Dictionary<Texture, List<string>>();
            var sharedMats = new HashSet<Material>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string path = RelPath(root, r.transform);
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    sharedMats.Add(m);
                    foreach (var prop in m.GetTexturePropertyNames())
                    {
                        var t = m.GetTexture(prop);
                        // A property the shader declares and the material leaves null is NOTHING — not a
                        // zero-byte row, which would read as a texture that costs nothing.
                        if (t == null) continue;
                        List<string> reaching;
                        if (!slots.TryGetValue(t, out reaching))
                        {
                            reaching = new List<string>();
                            slots[t] = reaching;
                            order.Add(t);
                        }
                        reaching.Add(path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]." + prop);
                    }
                }
            }

            foreach (var m in SwapReachableMaterials(root))
                if (!sharedMats.Contains(m)) swapOnlyMaterials++;

            foreach (var t in order)
            {
                var reaching = slots[t];
                reaching.Sort(StringComparer.Ordinal);
                var row = new TexRow
                {
                    Slot = reaching[0],
                    OtherSlots = reaching.Count - 1,
                    SlotKey = string.Join("|", reaching.ToArray()),
                    Width = t.width,
                    Height = t.height,
                    Mips = t.mipmapCount,
                };
                string assetPath = AssetDatabase.GetAssetPath(t);
                row.Identity = string.IsNullOrEmpty(assetPath)
                    ? "iid:" + t.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                    : assetPath;

                var t2 = t as Texture2D;
                if (t2 == null)
                {
                    row.Unknown = true;
                    row.Format = t.GetType().Name;
                    // A Cubemap legitimately ships uncapped — AAO's MaxTextureSize maps `is Texture2D` only —
                    // so an unchanged row here is correct behaviour, not a lever someone forgot to pull.
                    row.Caveat = "not a Texture2D; its bytes are not typed here and the optimizers' size caps do not apply to it";
                }
                else
                {
                    row.Format = t2.format.ToString();
                    float bpp;
                    if (!BytesPerPixel.TryGetValue(t2.format, out bpp))
                    {
                        row.Unknown = true;
                        row.Caveat = "format " + t2.format + " is not typed by this arithmetic, so its bytes are unknown rather than guessed";
                    }
                    else
                    {
                        long px = (long)t2.width * t2.height;
                        for (int i = 0; i < t2.mipmapCount && (px >> (2 * i)) >= 1; i++)
                            row.Bytes += (long)Mathf.RoundToInt((px >> (2 * i)) * bpp);
                    }
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Materials any <c>m_Materials</c> object-reference curve can put into a slot, across every
        /// clip reachable from an <c>Animator</c> under the root. Deliberately the cheap half of the question:
        /// it exists to COUNT a disclosed omission, not to resolve one, so it reads clips and stops there. A
        /// material a COMPONENT sets (MA Material Setter) is reached by neither this count nor the rows, and
        /// the section's prose says so rather than letting the count imply a completeness it lacks.</summary>
        private static IEnumerable<Material> SwapReachableMaterials(GameObject root)
        {
            var seen = new HashSet<Material>();
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.runtimeAnimatorController == null) continue;
                foreach (var clip in animator.runtimeAnimatorController.animationClips)
                {
                    if (clip == null) continue;
                    foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        if (b.propertyName == null ||
                            !b.propertyName.StartsWith("m_Materials.Array.data[", StringComparison.Ordinal)) continue;
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, b);
                        if (keys == null) continue;
                        foreach (var k in keys)
                        {
                            var m = k.value as Material;
                            if (m != null && seen.Add(m)) yield return m;
                        }
                    }
                }
            }
        }

        /// <summary>The <c>## Textures</c> table, and (out) the built side's summary key. Pure — rows in,
        /// lines out — so the reconciliation every reading of this section rests on is unit-testable without
        /// a bake.
        /// <para><b>Pairing is identity first, slot set second, and it needs both.</b> An untouched texture
        /// keeps its asset path and pairs straight through a renderer merge; a resized one is a NEW object
        /// whose only surviving handle is the set of slots reaching it. Neither key alone survives both cases
        /// — identity dies on a resize, the slot set dies on a merge — and d4rk's <c>MergeSkinnedMeshes</c> is
        /// on in the house profile, so the merge case is the ordinary one, not the exotic one.</para></summary>
        internal static List<string> TextureSection(List<TexRow> authored, List<TexRow> built,
                                                    float? sdkAuthored, float? sdkBuilt,
                                                    int swapOnlyAuthored, int swapOnlyBuilt,
                                                    string paramFilter, out string summaryKeys)
        {
            var pairs = PairTextures(authored, built);

            var lines = new List<string>
            {
                "texture memory, authored against the clone; paramFilter does not narrow this section"
                    + (string.IsNullOrEmpty(paramFilter) ? ""
                       : " (`" + RunLogFormat.Cell(paramFilter) + "` is active and narrows the parameter tables ONLY, so these rows and totals are of the WHOLE avatar)"),
                "",
                "| texture (slot) | size | format | mips | authored MB | built MB | caveat |",
                "| --- | --- | --- | --- | --- | --- | --- |",
            };
            foreach (var p in pairs)
            {
                bool inA = p.Authored.HasValue, inB = p.Built.HasValue;
                var row = inB ? p.Built.Value : p.Authored.Value;
                string presence = inA && inB ? "" : inB ? " (built-only)" : " (authored-only)";
                var caveats = new List<string>();
                if (inA && !string.IsNullOrEmpty(p.Authored.Value.Caveat)) caveats.Add((inB ? "authored: " : "") + p.Authored.Value.Caveat);
                if (inB && !string.IsNullOrEmpty(p.Built.Value.Caveat)) caveats.Add((inA ? "built: " : "") + p.Built.Value.Caveat);
                if (row.OtherSlots > 0)
                    caveats.Add("reached by " + (row.OtherSlots + 1) + " slots; counted once, as the SDK counts it once");
                lines.Add("| `" + RunLogFormat.Cell(row.Slot) + "`" + presence
                        + " | " + row.Width + "x" + row.Height
                        + " | " + RunLogFormat.Cell(row.Format)
                        + " | " + row.Mips
                        + " | " + MbCell(p.Authored) + " | " + MbCell(p.Built)
                        + " | " + RunLogFormat.Cell(string.Join("; ", caveats.ToArray())) + " |");
            }
            lines.Add("| total (rows) | | | | " + Mb(SumBytes(authored)) + " | " + Mb(SumBytes(built)) + " | |");
            lines.Add("| total (SDK) | | | | " + SdkCell(sdkAuthored) + " | " + SdkCell(sdkBuilt) + " | |");
            lines.Add("");

            lines.Add("**The two totals are one measurement, and their residual is the check.** The row total sums "
                    + "this section's own arithmetic — width x height x the format's bytes per pixel, over the mip "
                    + "chain; the SDK total is `AvatarPerformanceStats.textureMegabytes`, the figure the upload gate "
                    + "actually rates. They are computed the same way, so the expected residual is ZERO and a test "
                    + "asserts it. " + ResidualSentence(authored, built, sdkAuthored, sdkBuilt));
            lines.Add("A texture reached by several slots is ONE row, labelled by its first reaching slot in "
                    + "ordinal slot order, with the rest disclosed in its caveat — it is counted once here because "
                    + "the SDK counts it once, so the rows do sum to the total.");
            lines.Add("**An `unknown` MB cell is not a zero.** A non-`Texture2D` (RenderTexture, Cubemap, "
                    + "Texture2DArray) and a `TextureFormat` this arithmetic does not type are excluded from the row "
                    + "total and from the ordering rather than guessed at. They remain inside the SDK total, so an "
                    + "unknown row is exactly where the two totals may legitimately disagree.");
            lines.Add("**A built-only row is where a resize or a mint landed**; an **authored-only** row is a texture "
                    + "the build dropped. A renderer merge moves every slot handle at once, so on a d4rk-merged "
                    + "avatar expect paired rows to fall to those the build left untouched and the rest to split "
                    + "into authored-only/built-only pairs: that is the pairing losing its handle, NOT mass being "
                    + "deleted and re-added. Rows are ordered by built megabytes descending, so the levers sort to "
                    + "the top; the row set is complete regardless.");
            lines.Add(SwapSentence(swapOnlyAuthored, swapOnlyBuilt));
            lines.Add("**This built column overstates what an upload ships on an unlocked Poiyomi avatar.** "
                    + "VRCFury's `DisablePluginsWhenNotUploadingHook` inhibits Poiyomi's material lockdown during a "
                    + "bake, and that lockdown DELETES the texture-property entries bound to every shader feature "
                    + "the material leaves disabled. Those textures leave on upload and stay here, so the gap runs "
                    + "in the direction a reader would not guess: the real avatar is lighter than this table, by an "
                    + "amount that grows with how many features are off. liltoon's inhibited module strips shader "
                    + "settings rather than textures and does not move this figure.");

            summaryKeys = "textureMB=" + SdkCell(sdkBuilt);
            return lines;
        }

        /// <summary>The SDK's own <c>textureMegabytes</c> for <paramref name="go"/> — the figure the upload gate
        /// rates, and the only one <c>optimization.md</c> §Measuring lets a rank conversation use.
        /// <para>Returns null rather than 0 on any failure, and the stat field is itself nullable: a 0 here would
        /// be a confident claim that the avatar carries no textures. The call is ~2 ms for the whole stat vector,
        /// so it is free beside the bake that had to happen anyway.</para></summary>
        private static float? SdkTextureMegabytes(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var stats = new VRC.SDKBase.Validation.Performance.Stats.AvatarPerformanceStats(false);
                VRC.SDKBase.Validation.Performance.AvatarPerformance
                    .CalculatePerformanceStats(go.name, go, stats, false);
                return stats.textureMegabytes;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private struct TexPair { public TexRow? Authored, Built; }

        /// <summary>Identity first, then the slot set, then unpaired. Each built row is claimed at most once,
        /// and a claimed row cannot be claimed again by the weaker key — the same "an exact match is not
        /// another row's rename" discipline the parameter diff holds, for the same reason.</summary>
        private static List<TexPair> PairTextures(List<TexRow> authored, List<TexRow> built)
        {
            var pairs = new List<TexPair>();
            var takenBuilt = new bool[built.Count];

            var byIdentity = new Dictionary<string, int>(StringComparer.Ordinal);
            var bySlotKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < built.Count; i++)
            {
                if (!byIdentity.ContainsKey(built[i].Identity)) byIdentity[built[i].Identity] = i;
                if (!bySlotKey.ContainsKey(built[i].SlotKey)) bySlotKey[built[i].SlotKey] = i;
            }

            foreach (var a in authored)
            {
                int j;
                if (byIdentity.TryGetValue(a.Identity, out j) && !takenBuilt[j])
                {
                    takenBuilt[j] = true;
                    pairs.Add(new TexPair { Authored = a, Built = built[j] });
                }
                else if (bySlotKey.TryGetValue(a.SlotKey, out j) && !takenBuilt[j])
                {
                    takenBuilt[j] = true;
                    pairs.Add(new TexPair { Authored = a, Built = built[j] });
                }
                else pairs.Add(new TexPair { Authored = a, Built = null });
            }
            for (int i = 0; i < built.Count; i++)
                if (!takenBuilt[i]) pairs.Add(new TexPair { Authored = null, Built = built[i] });

            // Ordered by BUILT megabytes descending so the levers sort to the top; an authored-only row sorts
            // on what it used to cost, and an unknown row sorts last rather than as a zero.
            return pairs
                .OrderByDescending(p => p.Built.HasValue && !p.Built.Value.Unknown ? p.Built.Value.Bytes
                                      : p.Authored.HasValue && !p.Authored.Value.Unknown ? p.Authored.Value.Bytes : -1L)
                .ThenBy(p => (p.Built ?? p.Authored.Value).Slot, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>An Unknown row contributes nothing to the row total — its bytes are unknown, not zero —
        /// which is exactly why its cell says `unknown` where a total could not.</summary>
        private static long SumBytes(List<TexRow> side)
        {
            long sum = 0;
            foreach (var r in side) if (!r.Unknown) sum += r.Bytes;
            return sum;
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576f).ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string MbCell(TexRow? row)
        {
            if (!row.HasValue) return "—";
            return row.Value.Unknown ? "unknown" : Mb(row.Value.Bytes);
        }

        /// <summary>A null stat is an ABSENT figure, never a 0 — <c>textureMB=0</c> is a confident claim that
        /// the avatar carries no textures, and the SDK's stat fields are nullable.</summary>
        private static string SdkCell(float? mb)
        {
            return mb.HasValue ? mb.Value.ToString("F4", CultureInfo.InvariantCulture) : "unknown";
        }

        private static string ResidualSentence(List<TexRow> authored, List<TexRow> built, float? sdkA, float? sdkB)
        {
            var parts = new List<string>();
            if (sdkA.HasValue) parts.Add("authored " + (sdkA.Value - SumBytes(authored) / 1048576f).ToString("F4", CultureInfo.InvariantCulture));
            if (sdkB.HasValue) parts.Add("built " + (sdkB.Value - SumBytes(built) / 1048576f).ToString("F4", CultureInfo.InvariantCulture));
            if (parts.Count == 0) return "The SDK reported no figure on either side, so no residual is computable.";
            return "Residual (SDK minus rows), in MB: " + string.Join(", ", parts.ToArray())
                 + ". A non-zero residual names mass inside the rated figure that no row above accounts for — "
                 + "read the `unknown` rows and the swap disclosure before reading it as a defect.";
        }

        private static string SwapSentence(int authored, int built)
        {
            if (authored == 0 && built == 0)
                return "**Materials reachable only through an animated swap: none on either side.** Where they exist "
                     + "their textures are inside the SDK total and in no row here; on this avatar that gap is closed "
                     + "by measurement rather than by assumption.";
            return "**Materials reachable only through an animated swap: " + authored + " authored, " + built + " built.** "
                 + "Their textures are inside the SDK total and in NO row above — a disclosed omission, not a silent "
                 + "one, and a likely source of a non-zero residual. This count reads `m_Materials` object-reference "
                 + "curves on clips reachable from an `Animator` under the root; a material a COMPONENT sets (MA "
                 + "Material Setter) is reached by neither the rows nor this count.";
        }

        /// <summary>One row of the diff. <c>Caveat</c> is kept OUT of <c>Surface</c> rather than concatenated into
        /// it so it can be CARRIED when a row is replaced: the <c>merged</c> pass destroys the rows it summarises,
        /// and a caveat living inside their prose died with them. Rendered immediately after <c>Surface</c>, so the
        /// cell reads the same either way.</summary>
        internal struct DiffRow { public string Authored, Built, Category, Surface, Caveat; }

        /// <summary>Every parameter name the BUILT clone declares: its descriptor's expression parameters
        /// UNION every parameter on every controller the clone plays. Both halves are needed. The expression
        /// asset alone is a synced-parameter budget, not the avatar's parameter set — a controller-only value
        /// (a driver scratch, a gesture built-in) never appears there, so diffing against it alone put every
        /// such name in <c>dropped</c>, whose plain reading is "the build removed it".
        /// <para>Two channels for an unreadable controller, and the split is deliberate.
        /// <paramref name="incompleteReason"/> is set only for a <b>playable-layer slot</b> — the avatar's own
        /// declaration set — because a non-null value downgrades every unmatched row on the avatar.
        /// <paramref name="notes"/> takes anything else (a child <c>Animator</c> playing a controller this read
        /// cannot resolve), naming it without downgrading a single category.</para></summary>
        internal static List<string> BuiltDeclarations(GameObject clone, List<string> notes, out string incompleteReason)
        {
            incompleteReason = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var ep = d != null ? d.expressionParameters : null;
            if (ep != null && ep.parameters != null)
                foreach (var p in ep.parameters)
                    if (p != null && !string.IsNullOrEmpty(p.name)) names.Add(p.name);
            // A controller here that cannot be read is NOTED, never a global downgrade: a nested
            // AnimatorOverrideController on a child prop is legal and defeats the one-hop cast below, and
            // downgrading a whole avatar's diff over one prop would be a far bigger lie than the prop's
            // absent parameters. Only the descriptor's own playable layers carry the avatar's declaration set.
            foreach (var anim in clone.GetComponentsInChildren<Animator>(true))
            {
                var rac = anim != null ? anim.runtimeAnimatorController : null;
                if (!AddParams(rac, names))
                    notes.Add("the `Animator` at `" + MergeSurfaces.PathOf(anim.gameObject) + "` plays `" + rac.name
                            + "` (" + rac.GetType().Name + "), which this read cannot resolve to an AnimatorController — "
                            + "its parameters are absent from the built side below. Not a partial read of the AVATAR's "
                            + "declaration set (the playable layers carry that), so no category is downgraded.");
            }
            var unreadableSlots = new List<string>();
            int slotsWithController = 0;
            if (d != null)
            {
                foreach (var set in new[] { d.baseAnimationLayers, d.specialAnimationLayers })
                {
                    if (set == null) continue;
                    foreach (var l in set)
                    {
                        // The `isDefault` guard stays. Measured post-bake, every slot reads isDefault=false with
                        // a real controller, so it never fires — but the SDK keeps the flag and a non-null
                        // controller mutually exclusive only along its INSPECTOR path, so a programmatic write
                        // can leave a default-flagged slot holding a controller the built avatar does not play.
                        // Reading that would union parameters from a controller nothing plays: a false `kept`
                        // masking a real `dropped`, which is the failure direction that matters here.
                        if (l.isDefault) continue;
                        // An EMPTY slot declares nothing, and that is all it means. It used to be counted as
                        // evidence the built side went unread — see the history note below.
                        if (l.animatorController == null) continue;
                        slotsWithController++;
                        if (!AddParams(l.animatorController, names))
                            unreadableSlots.Add(l.type + " → `" + l.animatorController.name + "` ("
                                              + l.animatorController.GetType().Name + ")");
                    }
                }
            }
            // The one condition that makes this a genuinely PARTIAL read: a playable layer holds a controller
            // whose parameters could not be read. Every authored name would then fail to match and be reported
            // `dropped` — "the build removed it" — when the truth is that this read never saw them.
            //
            // It used to key on an empty non-default slot instead, explained as "the preprocess chain does not
            // leave the built controllers in the descriptor's slots". That was true only of a clone read AFTER
            // the paired OnPostprocessAvatar had swept its generated assets (atelier vrc-unity-tools#118 fixed
            // the lifetime; AvatarBake's doc comment has the measurement). Measured after the fix: 8/8 slots
            // hold real controllers under `Packages/nadena.dev.ndmf/__Generated/`, on a VRCFury avatar and an
            // MA-only one alike. An empty slot is authoring state, and emptying one on a baked clone left the
            // built name set IDENTICAL — nothing was unread — while the old arm downgraded every unmatched row
            // on the avatar and suppressed the one claim bake mode exists to make.
            if (unreadableSlots.Count > 0)
                incompleteReason = unreadableSlots.Count + " playable-layer slot(s) hold a controller this read "
                    + "could not resolve to an AnimatorController (" + string.Join("; ", unreadableSlots)
                    + "), so their parameters are absent and the built side is a PARTIAL read";
            // The retarget above dropped a FALSE arm, and with it the only automated detection of the arm's one
            // TRUE case: a clone holding no controller in any slot and declaring nothing. That is the state #118
            // measured on a clone read after its generated assets were swept — not an authoring state, and not
            // survivable, because every authored name would fall to `dropped`: a confident "the build removed it"
            // across a whole avatar. Kept as a second arm rather than trusted to the caller, because the only
            // thing standing between this door and that state is AvatarBakeScope's lifetime, which this codebase
            // has already gotten wrong once and silently. Judgment-free, and unable to fire on a healthy bake:
            // a real built avatar always has a controller in a slot, and an avatar that truly declares nothing
            // has no diffable census rows for the flag to downgrade.
            else if (d != null && slotsWithController == 0 && names.Count == 0)
                incompleteReason = "the built clone's descriptor holds no playable-layer controller at all and its "
                    + "expression parameters declare nothing, so NOTHING of the built side was readable — the shape "
                    + "of a clone read after its generated assets were swept, not of an avatar with nothing on it";
            return names.ToList();
        }

        /// <summary>Union <paramref name="rac"/>'s parameters into <paramref name="into"/>. Returns <b>false</b>
        /// only when a controller was PRESENT and could not be read as an <c>AnimatorController</c> — the one
        /// condition that makes a built read partial, and a silent return before this.
        /// <para>A null controller returns true, deliberately: an absent controller declares nothing, and a
        /// child <c>Animator</c> with no controller is the common case on any avatar — counting it would make
        /// the incompleteness flag non-null essentially always and delete <c>dropped</c> as a category. An empty
        /// parameter list returns true too: that read succeeded and found nothing.</para></summary>
        private static bool AddParams(RuntimeAnimatorController rac, HashSet<string> into)
        {
            if (rac == null) return true;
            var ac = rac as AnimatorController;
            // Deliberate divergence from the asset doors, which REFUSE an override controller by name rather
            // than unwrapping it (they report a named asset, so silently reporting a different one would
            // mislead). Here the subject is the built avatar's parameter SET, not a named asset, and an
            // override controller declares no parameters of its own — every one it can carry comes from the
            // base — so unwrapping reads the right set rather than a substitute for it.
            //
            // Chains unwrap too: an override OF an override is legal, and stopping at one hop would read
            // nothing and mark the built side incomplete. Depth-capped because the chain is data, and a
            // cycle in it must not hang the bake.
            for (int hop = 0; ac == null && rac is AnimatorOverrideController ovr && hop < 8; hop++)
            {
                ac = ovr.runtimeAnimatorController as AnimatorController;
                rac = ovr.runtimeAnimatorController;
            }
            if (ac == null) return false;
            if (ac.parameters == null) return true;
            foreach (var p in ac.parameters) if (!string.IsNullOrEmpty(p.name)) into.Add(p.name);
            return true;
        }

        /// <summary>Is <paramref name="built"/> a prefixed form of <paramref name="authored"/>? The build
        /// prepends a namespace, so the authored name survives as a suffix behind a SEPARATOR. A bare
        /// EndsWith would make authored <c>Toggle</c> a candidate for built <c>Hair/HairToggle</c> and emit a
        /// confident, wrong provenance claim from the one door whose whole thesis is that it never makes one.
        /// The boundary is declared here rather than left implicit, and anything outside it stays
        /// unattributed rather than guessed.</summary>
        internal static bool IsPrefixedForm(string built, string authored)
        {
            if (string.IsNullOrEmpty(built) || string.IsNullOrEmpty(authored)) return false;
            if (built.Length <= authored.Length || !built.EndsWith(authored, StringComparison.Ordinal)) return false;
            char boundary = built[built.Length - authored.Length - 1];
            return boundary == '/' || boundary == '_';
        }

        /// <summary>Diff the authored census against the built declaration set. Pure — it touches no Unity
        /// object — which is what makes it unit-testable, and it is the part of this door most able to lie.
        ///
        /// Only rows the census marked diffable participate. A physbone suffix or a menu sub-parameter is
        /// written by the runtime and declared by nothing, so a declaration set cannot carry it: those are
        /// reported <c>not-in-scope</c> rather than counted as removed.
        ///
        /// <paramref name="paramFilter"/> is applied LAST, to both sides of each row. Filtering the authored
        /// census first — the obvious order — makes the answer depend on the filter: a built name whose
        /// authored counterpart was filtered out has nothing left to claim it and is reported built-only, so
        /// filtering on a surface prefix (the most natural use) would report that whole surface as
        /// unattributed.
        /// <para>Precedence is filter-INDEPENDENT for a reason worth recording, since the live census reaching
        /// this method is already narrowed: an excludable built name <c>b</c> ends with a separator plus the
        /// authored name it would claim, so any filter matching that short name necessarily matches <c>b</c>
        /// too (matching is substring). A filter can therefore drop the short row but never strip the
        /// exact-claimer of a row it kept — so no verdict can flip on the filter.</para></summary>
        internal static List<DiffRow> Diff(ReportComposition.CensusResult census, List<string> built, string paramFilter,
                                           bool builtSideComplete = true)
        {
            var builtSet = new HashSet<string>(built, StringComparer.Ordinal);
            var unclaimed = new HashSet<string>(built, StringComparer.Ordinal);
            var claimedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var rowsByBuilt = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var rows = new List<DiffRow>();

            // Pass 1 — every built name an authored row claims by EXACT match. The predicate is exactly the
            // `kept` branch's own, and must stay that way: the natural shortcut ("census names ∩ built") lets a
            // NON-diffable row in, and a non-diffable row claims nothing in pass 2 — so its name would be
            // excluded from a candidate set while no row ever claims it, and one built name would read
            // `dropped` ("the build removed it") AND `built-only` ("present, unclaimed") in the same table.
            var exactClaim = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in census.Params)
                if (p.Diffable && builtSet.Contains(p.Name)) exactClaim.Add(p.Name);

            // The non-diffable half of that same intersection: authored names the build carries verbatim
            // that no row can claim. Collected so the two rows describing one parameter can point at each
            // other — see the not-in-scope branch below.
            var notInScopeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in census.Params)
                if (!p.Diffable && builtSet.Contains(p.Name)) notInScopeNames.Add(p.Name);

            void Claim(string b, string authored, int rowIndex)
            {
                if (!claimedBy.TryGetValue(b, out var l)) claimedBy[b] = l = new List<string>();
                l.Add(authored);
                if (!rowsByBuilt.TryGetValue(b, out var idx)) rowsByBuilt[b] = idx = new List<int>();
                idx.Add(rowIndex);
                unclaimed.Remove(b);
            }

            foreach (var p in census.Params)
            {
                if (!p.Diffable)
                {
                    // A non-diffable name that the built avatar ALSO carries, byte-identical, is the one
                    // shape this table used to leave the reader to join by eye: two rows, one string, two
                    // dispositions (`not-in-scope` here and `built-only` below), nothing saying they are
                    // the same parameter. Neither row is wrong — nothing authored declares it, and nothing
                    // authored claims it — so the fix is the cross-reference, not a category change, and
                    // the exact-claim set above stays diffable-only for the reason its own comment gives.
                    bool alsoBuilt = builtSet.Contains(p.Name);
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = alsoBuilt ? p.Name + " (see its built-only row)" : "—",
                        Category = "not-in-scope",
                        Surface = "runtime-written (physbone suffix / menu sub-parameter) — nothing declares it, so a declaration set cannot carry it"
                                + (alsoBuilt ? "; the build DOES carry this exact name — the two rows are one parameter, not two findings" : ""),
                    });
                    continue;
                }
                if (builtSet.Contains(p.Name))
                {
                    Claim(p.Name, p.Name, rows.Count);
                    rows.Add(new DiffRow { Authored = p.Name, Built = p.Name, Category = "kept", Surface = p.Declared });
                    continue;
                }
                var raw = built.Where(b => IsPrefixedForm(b, p.Name)).ToList();
                // Pass 2 — a built name already claimed by exact match is not this row's rename: an exact match
                // is a stronger claim than a prefix inference. Without this, a gimmick that declares its own
                // internal `ObjectSync/D/Prop/PX/C` alongside the avatar's `Prop/PX/C` made every internal name
                // a spurious candidate for the shorter one: 63 of 271 rows on the demo avatar reported ambiguous
                // while the 63 real renames were orphaned as built-only. One defect, counted twice.
                //
                // But exclusion is a TIE-BREAKER, never evidence of removal — so it applies only while it leaves
                // a candidate standing. Emptying the set and falling through to `dropped` would assert "the build
                // removed it" on the ordinary idiom of an inner parameter exposed under a name the outer avatar
                // also declares (MA `remapTo`): authored `Toggle` + `Hair/Toggle` onto built `Hair/Toggle` is a
                // MERGE, and the fallback keeps it reported as one. `dropped` therefore still fires iff no built
                // name is a prefixed form at all — precisely the pre-precedence condition, so this rule cannot
                // manufacture a false removal.
                var narrowed = raw.Where(b => !exactClaim.Contains(b)).ToList();
                var candidates = narrowed.Count > 0 ? narrowed : raw;
                if (candidates.Count == 1)
                {
                    Claim(candidates[0], p.Name, rows.Count);
                    // Disclosed PER ROW, not once in the legend. Exclusion deletes the evidence of a possible
                    // merge, so a row that reads as a confident rename because its rivals were excluded has to
                    // carry that fact where a reader checking THIS row will see it — a legend sentence cannot be
                    // checked against a specific row, and these are exactly the rows the rule is adopted for.
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = candidates[0], Category = "renamed", Surface = p.Declared,
                        Caveat = candidates.Count == raw.Count ? null : ExclusionNote(raw, narrowed),
                    });
                }
                else if (candidates.Count > 1)
                {
                    // Ambiguous, so nothing is attributed — and the candidates stay UNCLAIMED on purpose, or a
                    // genuinely built-only parameter that happens to match an ambiguous authored name would be
                    // swallowed: never given its own built-only row, and visible only inside a cell attributed
                    // to an unrelated parameter.
                    //
                    // Exclusion is disclosed here too, and it matters MORE than on a renamed row: this is the one
                    // category whose whole purpose is honesty about not knowing, so a candidate list silently
                    // trimmed by precedence would make the trimming unknowable from the artifact.
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = string.Join(" or ", candidates), Category = "ambiguous",
                        Surface = p.Declared + " — more than one built name is a prefixed form of this one",
                        Caveat = candidates.Count == raw.Count ? null : ExclusionNote(raw, narrowed),
                    });
                }
                else if (builtSideComplete)
                {
                    rows.Add(new DiffRow { Authored = p.Name, Built = "—", Category = "dropped", Surface = p.Declared });
                }
                else
                {
                    // No match AND the built side is known partial: `dropped` would assert a removal this
                    // read cannot see. Say what is true instead.
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = "?", Category = "built-side-unread",
                        Surface = p.Declared + " — no built name matched, and the built read was incomplete, so removal is NOT the claim",
                    });
                }
            }

            // `merged` REPLACES the rows it summarises rather than riding beside them. Emitting one merged row
            // on top of the two renamed rows that produced it double-counts a single built parameter and makes
            // the category totals exceed the parameter count — a table lying about its own arithmetic.
            var drop = new HashSet<int>();
            var merged = new List<DiffRow>();
            foreach (var kv in claimedBy)
            {
                if (kv.Value.Count <= 1) continue;
                // Every caveat on a replaced row is CARRIED onto the row that replaces it. A merged row is the most
                // confident statement this table makes — "these names became that one" — and it is assembled from
                // rows that may each have reached their claim only because precedence excluded a rival. Dropping
                // their caveats here would launder exactly the disclosure the renamed branch exists to make, and
                // silently: the foreclosed reading vanishes with the row that named it.
                var carried = new List<string>();
                foreach (var i in rowsByBuilt[kv.Key])
                {
                    drop.Add(i);
                    if (!string.IsNullOrEmpty(rows[i].Caveat) && !carried.Contains(rows[i].Caveat))
                        carried.Add(rows[i].Caveat);
                }
                merged.Add(new DiffRow
                {
                    Authored = string.Join(" + ", kv.Value), Built = kv.Key, Category = "merged",
                    Surface = "two or more authored names resolved onto one built name",
                    Caveat = carried.Count == 0 ? null : string.Concat(carried),
                });
            }
            var final = new List<DiffRow>();
            for (int i = 0; i < rows.Count; i++) if (!drop.Contains(i)) final.Add(rows[i]);
            final.AddRange(merged);

            // A built name no authored one claims. This is a MEASURED fact, not an inference failure — which is
            // why it no longer shares a category with ambiguity: build-minted internals and SDK-supplied names
            // are the normal population here, and the note says so IN THE CELL, following the empty-writers-cell
            // precedent, because a bare `(built only)` on a hundred rows reads as a hundred findings.
            foreach (var b in unclaimed)
                final.Add(ControllerRules.IsVrcReserved(b)
                    ? new DiffRow
                    {
                        Authored = "—", Built = b, Category = "vrc-reserved",
                        Surface = "(built only — a VRChat reserved parameter: declared nowhere, referenced everywhere, "
                                + "so no authored row can claim it)",
                    }
                    : new DiffRow
                    {
                        Authored = notInScopeNames.Contains(b) ? b + " (see its not-in-scope row)" : "—",
                        Built = b, Category = "built-only",
                        Surface = notInScopeNames.Contains(b)
                            ? "(built only — no authored row CLAIMS it, because the authored row carrying this exact "
                            + "name is not-in-scope: runtime-written, so it declares nothing to diff. One parameter, two rows)"
                            : "(built only — build-minted or SDK-supplied; nothing authored claims it, which is not a finding)",
                    });

            if (string.IsNullOrEmpty(paramFilter)) return final;
            return final.Where(r =>
                (r.Authored != null && r.Authored.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (r.Built != null && r.Built.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        /// <summary>What a `renamed` row owes its reader when exclusion is what made it unambiguous: which
        /// candidates were removed, why they were removable, and the alternative reading this row is NOT.</summary>
        private static string ExclusionNote(List<string> raw, List<string> narrowed)
        {
            var excluded = raw.Where(b => !narrowed.Contains(b)).Select(b => "`" + b + "`");
            return " — the single candidate only after excluding " + string.Join(", ", excluded)
                 + ", each already claimed by an authored name of its own (an exact match beats a prefix "
                 + "inference). If the build instead MERGED this name onto one of those, the truth is `merged` "
                 + "and this row does not say so.";
        }

        private static string Refusal(string path, string stage, string name) =>
            "# ReportComposition (bake)\n\nstatus: FAILED\n\nsummary: [ReportComposition] mode=bake => FAIL ("
            + stage + ") | log=" + path
            + "\n\nThe bake did not complete, so this artifact carries NO parameter table. Authored-census rows are "
            + "deliberately not published here: presenting them under a heading that promises composed truth is the "
            + "misread this door exists to prevent. Run `Report(<avatarRoot>)` without the flag for the authored census, "
            + "knowing it is authored.\n";

        private static string FullPath(string assetPath) =>
            Path.Combine(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length), assetPath);

        private static void WriteArtifact(string assetPath, string text)
        {
            try
            {
                string full = FullPath(assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, text);
                RunLogFormat.PublishArtifact(RunLogFormat.SnapshotDir, assetPath);
            }
            catch (Exception e)
            {
                Debug.LogError("[ReportComposition] could not write the bake artifact at " + assetPath + " ("
                             + e.GetType().Name + ") — the bake's result is unrecoverable, so treat this run as not taken.");
            }
        }
    }
}
