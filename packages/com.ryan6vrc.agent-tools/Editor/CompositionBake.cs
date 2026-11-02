using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary><see cref="ReportComposition"/>'s bake mode: builds a fresh clone through <see cref="AvatarBake"/>
    /// and diffs the built parameter set against the authored census. It reads the rewrite a build performs; pin
    /// no vendor name-mangling grammar here, since a wrong pin returns plausible strings with nothing to signal it.
    /// Contract: docs/unity-tools.md.</summary>
    internal static class CompositionBake
    {
        private const string Pending = "pending";
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
        private static readonly Dictionary<int, bool> InFlight = new Dictionary<int, bool>();
        private static readonly HashSet<int> InFlightProbed = new HashSet<int>();

        // Stable, because the caller re-reads it after a transport timeout; the instance id keeps two same-named
        // roots in one scene from sharing a file.
        private static string ArtifactPath(GameObject root) =>
            RunLogFormat.SnapshotDir + "/composition-bake_" + RunLogFormat.Sanitize(root.name)
            + "_" + root.GetInstanceID().ToString("X", CultureInfo.InvariantCulture) + ".md";

        internal static string Begin(GameObject root, ReportComposition.CensusResult census, string paramFilter,
                                     Func<GameObject, string> probe = null)
        {
            string path = ArtifactPath(root);
            int key = root.GetInstanceID();
            if (InFlight.TryGetValue(key, out bool running) && running)
            {
                // A second probe on a probed bake is the transport re-sending a timed-out call, so it gets the
                // ordinary PENDING.
                if (probe != null && !InFlightProbed.Contains(key))
                {
                    string refusal = "[ReportComposition] FAIL: a bake of '" + root.name + "' is already running and was started "
                         + "without a probe, so this probe would never run — wait for Verify(<avatarRoot>) to leave "
                         + "PENDING, then re-issue the call | log=" + path;
                    Debug.LogError(refusal);
                    return refusal;
                }
                return "[ReportComposition] " + root.name + ": mode=bake => PENDING (already running) | log=" + path;
            }
            if (probe != null) InFlightProbed.Add(key); else InFlightProbed.Remove(key);

            if (KeepPrevious(FullPath(path))) RunLogFormat.PublishArtifact(RunLogFormat.SnapshotDir, path + PreviousSuffix);

            WriteArtifact(path, "# ReportComposition (bake): " + root.name + "\n\nstatus: " + Pending
                + "\nstarted: " + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                + "\n\nThe bake is running. Re-read this file, or call `ReportComposition.Verify(<avatarRoot>)`.\n"
                + "Do NOT re-issue the bake: a second call while this one is in flight returns this same path"
                + (probe != null ? "" : ", and one carrying a probe is refused because this bake was started without it") + ".\n"
                + "The last completed bake of this root, if there was one, is beside this file as `"
                + Path.GetFileName(path) + PreviousSuffix + "`.\n");
            InFlight[key] = true;

            var rootRef = root;
            EditorApplication.CallbackFunction step = null;
            step = () =>
            {
                EditorApplication.update -= step;
                try { Run(rootRef, census, paramFilter, path, probe); }
                finally { InFlight[key] = false; }
            };
            // update, never delayCall, which queues while the Editor is unfocused: docs/unity.md §Sharp edges.
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
            if (!IsFinished(text) && text.Contains("status: " + Pending))
            {
                // A domain reload discards the scheduled callback without running it and clears the in-flight flag,
                // leaving the artifact `pending` forever. Nothing in the editor can report whether the callback
                // still exists, so age is the only signal.
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

        internal const string PreviousSuffix = ".prev.md";

        /// <summary>A finished bake's artifact opens with its summary line; the pending stub and a refusal open
        /// with a heading.</summary>
        internal static bool IsFinished(string artifactText) =>
            artifactText != null && artifactText.StartsWith("summary: ", StringComparison.Ordinal);

        /// <summary>Copy a finished bake to its <see cref="PreviousSuffix"/> sibling, so a failed re-bake never
        /// costs the last good one. Never throws: a failed copy must not stop the bake it precedes.</summary>
        internal static bool KeepPrevious(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath) || !IsFinished(File.ReadAllText(fullPath))) return false;
                File.Copy(fullPath, fullPath + PreviousSuffix, true);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ReportComposition] could not keep the previous bake artifact beside " + fullPath
                               + " (" + e.GetType().Name + ") — the bake proceeds and overwrites it.");
                return false;
            }
        }

        private static void Run(GameObject root, ReportComposition.CensusResult census, string paramFilter, string path,
                                Func<GameObject, string> probe)
        {
            if (root == null)
            {
                WriteArtifact(path, Refusal(path, "the avatar root was destroyed before the bake ran", "(destroyed)", probe != null));
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                WriteArtifact(path, Refusal(path, "play mode was entered before the bake ran; exit play and re-issue the bake", root.name, probe != null));
                return;
            }

            // `using`, so the SDK's paired post-callback fires on every exit, including the refusal below. Every read
            // sits inside the scope: the post-callback destroys the clone's generated assets (built controllers,
            // parameters, menus, the optimizers' textures), as AvatarBake's doc comment measures.
            using (var bake = AvatarBake.Begin(root))
            {
                if (!bake.Ok)
                {
                    string failedStage = bake.DescribeFailure();
                    WriteArtifact(path, Refusal(path, failedStage, root.name, probe != null));
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
                var perfRead = ReadPerformance(clone);
                string perfKeys;
                var performance = PerformanceSection(perfRead, paramFilter, out perfKeys);
                var syncRead = ReadSync(clone);
                string syncKeys;
                var sync = SyncSection(syncRead, paramFilter, out syncKeys);
                string menuKey;
                var builtMenu = BuiltMenuSection(ReadBuiltMenu(clone), paramFilter, out menuKey);
                int swapAuthored, swapBuilt;
                var texAuthored = ReadTextures(root, out swapAuthored);
                var texBuilt = ReadTextures(clone, out swapBuilt);
                string textureKeys;
                var textures = TextureSection(texAuthored, texBuilt,
                    SdkTextureMegabytes(root), perfRead.Stats != null ? perfRead.Stats.textureMegabytes : null,
                    swapAuthored, swapBuilt, paramFilter, out textureKeys);
                // Last: the probe is the caller's code holding the clone, so whatever it does can spoil only its
                // own section.
                string probeKey = null;
                var probed = probe != null ? ProbeSection(probe, clone, out probeKey) : null;
                string summary = string.Format(CultureInfo.InvariantCulture,
                    "[ReportComposition] {0}: surfaces={1}{13} params={2} kept={3} renamed={4} dropped={5} merged={6} ambiguous={7} builtOnly={8} vrcReserved={9} notInScope={10} builtSideUnread={11} {14} {15} {16} {17} {19}{18} mode=bake => OK | log={12}",
                    root.name, census.Surfaces.Count, census.Params.Count,
                    diff.Count(d => d.Category == "kept"), diff.Count(d => d.Category == "renamed"),
                    diff.Count(d => d.Category == "dropped"), diff.Count(d => d.Category == "merged"),
                    diff.Count(d => d.Category == "ambiguous"), diff.Count(d => d.Category == "built-only"),
                    diff.Count(d => d.Category == "vrc-reserved"),
                    diff.Count(d => d.Category == "not-in-scope"),
                    diff.Count(d => d.Category == "built-side-unread"), path,
                    census.UnlintableSurfaces > 0 ? " unlintableSurfaces=" + census.UnlintableSurfaces : "",
                    geometryKeys, textureKeys, perfKeys, syncKeys,
                    probeKey != null ? " " + probeKey : "", menuKey);

                var section = new List<string>
                {
                    "| authored | category | built | owning surface |",
                    "| --- | --- | --- | --- |",
                };
                foreach (var d in diff)
                    section.Add("| `" + RunLogFormat.Cell(d.Authored) + "` | " + d.Category + " | `" + RunLogFormat.Cell(d.Built) + "` | "
                              + RunLogFormat.Cell(d.Surface + d.Caveat) + " |");
                section.Add("");
                // Each note ends with a hard break and the block with a blank line: without both, CommonMark lazy
                // continuation folds every following paragraph into this blockquote.
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
                            + ReportComposition.RenderBody(root, census, paramFilter, "bake (measured against a fresh build)", section, geometry, textures, performance, sync, builtMenu, probed);
                WriteArtifact(path, body);
                Debug.Log(summary);
            }
        }

        // ── Geometry: triangles, authored against built ───────────────────────────────────────────────

        /// <summary>One renderer's triangles on one side of the diff. <c>Unreadable</c> is never a zero: a 0 in a
        /// triangle column reads as "the build emptied this".</summary>
        internal struct GeoRow
        {
            public string Path;
            public bool Skinned;
            public bool Active;
            public long Tris;
            public bool Unreadable;
            public string Caveat;
            /// <summary><c>channel:components</c> per UV channel (<c>0:2 1:4</c>), read off the attribute descriptors,
            /// so it is there for a mesh whose index buffer is not readable; null where there is no mesh.</summary>
            public string Uv;
        }

        /// <summary>Every <c>SkinnedMeshRenderer</c> and <c>MeshRenderer</c> under <paramref name="root"/>, active or
        /// not: the build merges toggled-off renderers into an always-active mesh under NaNimation, so filtering on
        /// "active" would report a merge as a deletion.
        /// <para>The built side is readable only inside the bake scope: AAO's output meshes live under NDMF's
        /// generated-asset root, which the post-callback's <c>CleanupTemporaryAssets</c> deletes, and d4rk's merged mesh
        /// is a bare <c>new Mesh()</c> never persisted.</para></summary>
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
                else continue;

                var row = new GeoRow
                {
                    Path = RelPath(root, r.transform), Skinned = skinned,
                    Active = r.gameObject.activeInHierarchy && r.enabled,
                    Uv = mesh != null ? UvLayout(mesh) : null,
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

        private static string UvLayout(Mesh mesh)
        {
            var parts = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                var attr = (UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + i);
                if (mesh.HasVertexAttribute(attr))
                    parts.Add(i.ToString(CultureInfo.InvariantCulture) + ":"
                            + mesh.GetVertexAttributeDimension(attr).ToString(CultureInfo.InvariantCulture));
            }
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }

        private static string RelPath(GameObject root, Transform t)
        {
            if (t == root.transform) return "(root)";
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != root.transform; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        internal static List<string> GeometrySection(List<GeoRow> authored, List<GeoRow> built,
                                                     string paramFilter, out string summaryKeys)
        {
            var a = KeyRows(authored);
            var b = KeyRows(built);

            var lines = new List<string>
            {
                "built triangles and UV channels, authored against the clone; paramFilter does not narrow this section"
                    + (string.IsNullOrEmpty(paramFilter) ? ""
                       : " (`" + RunLogFormat.Cell(paramFilter) + "` is active and narrows the parameter tables ONLY, so these rows and totals are of the WHOLE avatar)"),
                "",
                "| renderer (path) | kind | active | authored tris | built tris | built uv | caveat |",
                "| --- | --- | --- | --- | --- | --- | --- |",
            };
            foreach (var k in a.Keys.Union(b.Keys).OrderBy(s => s, StringComparer.Ordinal))
            {
                bool inA = a.ContainsKey(k), inB = b.ContainsKey(k);
                var row = inB ? b[k] : a[k];
                string presence = inA && inB ? "" : inB ? " (built-only)" : " (authored-only)";
                var caveats = new List<string>();
                if (inA && !string.IsNullOrEmpty(a[k].Caveat)) caveats.Add((inB ? "authored: " : "") + a[k].Caveat);
                if (inB && !string.IsNullOrEmpty(b[k].Caveat)) caveats.Add((inA ? "built: " : "") + b[k].Caveat);
                if (inA && inB && a[k].Active != b[k].Active)
                    caveats.Add("active differs: authored=" + (a[k].Active ? "yes" : "no"));
                if (inA && inB && a[k].Uv != b[k].Uv)
                    caveats.Add("authored uv: " + (a[k].Uv ?? "unreadable"));
                lines.Add("| `" + RunLogFormat.Cell(row.Path) + "` | " + (row.Skinned ? "skinned" : "mesh") + presence
                        + " | " + (row.Active ? "yes" : "no")
                        + " | " + TriCell(a, k) + " | " + TriCell(b, k)
                        + " | " + (inB ? b[k].Uv ?? "unreadable" : "—")
                        + " | " + RunLogFormat.Cell(string.Join("; ", caveats)) + " |");
            }
            foreach (var t in new[] { "all", "skinned", "active" })
                lines.Add("| total (" + t + ") | | | " + Subtotal(authored, t) + " | " + Subtotal(built, t) + " | | |");
            lines.Add("");
            lines.Add("**The three subtotals.** `all` is every renderer counted here; `skinned` only the "
                    + "`SkinnedMeshRenderer`s; `active` only those of them active in the hierarchy with the "
                    + "component enabled. A **built-only** row is where a merge landed — compare it yourself "
                    + "against the sum of the authored rows that fed it, which no static read can identify. "
                    + "An **authored-only** row carries geometry the build removed. This section attributes "
                    + "nothing to a blendshape: a drop says the triangles went, never which shape's footprint "
                    + "they were (`ReportShapeOverlap` prints an authored footprint per shape at edit time).");
            lines.Add("**`built uv` is the built mesh's vertex layout**, `channel:components` for each UV channel "
                    + "the mesh carries and `none` for a mesh with no UV channel: what a shader sampling that "
                    + "channel is handed, not whether the data in it survived. An optimizer's merged mesh widening "
                    + "a channel or dropping an all-zero one is the ordinary result of the merge, and a merged row "
                    + "is built-only, so it has no authored layout to compare against here.");
            summaryKeys = "tris=" + Subtotal(built, "all") + " trisSkinned=" + Subtotal(built, "skinned")
                        + " trisActive=" + Subtotal(built, "active");
            return lines;
        }

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

        // ── Probe: the caller's own read of the clone ─────────────────────────────────────────────────

        /// <summary>The <c>## Probe</c> section. The call and its catch live here so a throwing probe costs only this
        /// section. Fenced, so probe text carrying its own code fence or a line this tool's readers key on stays
        /// inert.</summary>
        internal static List<string> ProbeSection(Func<GameObject, string> probe, GameObject clone, out string summaryKey)
        {
            var lines = new List<string>
            {
                "the caller's probe, run against the clone after every section above was read and before the clone "
                    + "was destroyed. The text is the caller's, quoted whole; any object it names is gone.",
                "",
            };
            string text;
            try { text = probe(clone); }
            catch (Exception e)
            {
                lines.Add("**The probe threw, so it reported nothing:** " + e.GetType().Name + ": " + RunLogFormat.Cell(e.Message));
                summaryKey = "probe=threw";
                return lines;
            }
            summaryKey = "probe=ok";
            if (text == null) { lines.Add("(returned null)"); return lines; }
            if (text.Length == 0) { lines.Add("(returned empty)"); return lines; }
            int longest = 0, run = 0;
            foreach (char ch in text) { run = ch == '`' ? run + 1 : 0; if (run > longest) longest = run; }
            string fence = new string('`', Math.Max(3, longest) + 1);
            lines.Add(fence);
            foreach (var l in text.Replace("\r", "").Split('\n')) lines.Add(l);
            lines.Add(fence);
            return lines;
        }

        // ── Textures: memory, authored against built ──────────────────────────────────────────────────

        /// <summary>One distinct texture on one side of the diff, with the slots that reach it. <c>Unknown</c> is
        /// never a zero, as with <see cref="GeoRow.Unreadable"/>: the row is named and left out of the total. AAO's own
        /// reader guesses 16 bpp on an unrecognised format; this one leaves the gap named.</summary>
        internal struct TexRow
        {
            public string Slot;
            public int OtherSlots;
            public string SlotKey;
            public string Identity;
            public int Width, Height, Mips;
            public string Format;
            public long Bytes;
            public bool Unknown;
            public string Caveat;
        }

        /// <summary>Bytes per pixel, each entry measured against the SDK's own <c>textureMegabytes</c>: one texture
        /// of each format on a bare renderer, the reported figure divided by the mip chain's pixel count. Not
        /// inferred from the format: <c>RGB24</c> rates 4, not 3.
        /// <para>A format absent here is Unknown, never a guess. The ASTC/ETC families are unmeasured because this
        /// is PC-facing.</para></summary>
        private static readonly Dictionary<TextureFormat, float> BytesPerPixel = new Dictionary<TextureFormat, float>
        {
            { TextureFormat.DXT1, 0.5f },   { TextureFormat.DXT1Crunched, 0.5f },
            { TextureFormat.DXT5, 1f },     { TextureFormat.DXT5Crunched, 1f },
            { TextureFormat.BC7, 1f },      { TextureFormat.BC6H, 1f },
            { TextureFormat.BC4, 0.5f },    { TextureFormat.BC5, 1f },
            { TextureFormat.Alpha8, 1f },   { TextureFormat.R8, 1f },
            { TextureFormat.R16, 2f },      { TextureFormat.RG16, 2f },
            { TextureFormat.RGB24, 4f },    { TextureFormat.RGBA32, 4f },
            { TextureFormat.ARGB32, 4f },   { TextureFormat.BGRA32, 4f },
            { TextureFormat.RGB565, 2f },   { TextureFormat.RGBA4444, 2f },
            { TextureFormat.ARGB4444, 2f }, { TextureFormat.RHalf, 2f },
            { TextureFormat.RGHalf, 4f },   { TextureFormat.RGBAHalf, 8f },
            { TextureFormat.RFloat, 4f },   { TextureFormat.RGFloat, 8f },
            { TextureFormat.RGBAFloat, 16f },
        };

        /// <summary>The mip chain's pixel count, <c>area &gt;&gt; 2i</c> per level. The SDK uses this area shift, so do
        /// not walk per dimension: on a 2048x8 DXT1 the SDK reports 10922 bytes, the shift exactly, and the walk gives
        /// 11007.</summary>
        private static long MipChainPixels(int width, int height, int mips)
        {
            long px = (long)width * height, total = 0;
            for (int i = 0; i < mips && (px >> (2 * i)) >= 1; i++) total += px >> (2 * i);
            return total;
        }

        /// <summary>Every texture reachable from every <c>Renderer</c>'s shared materials under
        /// <paramref name="root"/>, active or not: particle, trail and line renderers carry materials too, and
        /// deactivating every renderer object on a real avatar moved the SDK's <c>textureMegabytes</c> by zero.
        /// <para><paramref name="swapOnlyMaterials"/> counts materials no renderer's <c>sharedMaterials</c> holds,
        /// reachable only through an <c>m_Materials</c> object-reference curve. They are counted, not rowed:
        /// <see cref="SwapSentence"/> discloses them.</para></summary>
        internal static List<TexRow> ReadTextures(GameObject root, out int swapOnlyMaterials)
        {
            var rows = new List<TexRow>();
            swapOnlyMaterials = 0;
            if (root == null) return rows;

            var order = new List<Texture>();
            var slots = new Dictionary<Texture, List<string>>();
            var sharedMats = new HashSet<Material>();

            // An ordinal suffix: same-named siblings would otherwise give two different textures one SlotKey, and
            // PairTextures' first-wins map would leave the second unpaired.
            var pathUse = new Dictionary<string, int>(StringComparer.Ordinal);
            var unreadableMaterials = new List<string>();

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                string basePath = RelPath(root, r.transform), path = basePath;
                int used;
                if (pathUse.TryGetValue(basePath, out used))
                {
                    path = basePath + " #" + (used + 1).ToString(CultureInfo.InvariantCulture);
                    pathUse[basePath] = used + 1;
                }
                else pathUse[basePath] = 1;

                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    sharedMats.Add(m);
                    string[] props;
                    // A material with a missing or broken shader throws here.
                    try { props = m.GetTexturePropertyNames(); }
                    catch (Exception e)
                    {
                        unreadableMaterials.Add(path + "[" + i.ToString(CultureInfo.InvariantCulture) + "] `"
                                              + m.name + "` (" + e.GetType().Name + ")");
                        continue;
                    }
                    foreach (var prop in props)
                    {
                        Texture t;
                        try { t = m.GetTexture(prop); }
                        catch (Exception) { continue; }
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
                // The asset path alone is not an identity: every sub-asset of one file shares it, and d4rk mints every
                // non-material object it creates into a single shared TrashBin asset, so a path-keyed pair would marry
                // unrelated textures. A runtime-only texture has no asset at all.
                string guid, assetPath = AssetDatabase.GetAssetPath(t);
                long localId;
                row.Identity = !string.IsNullOrEmpty(assetPath)
                             && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(t, out guid, out localId)
                    ? guid + "::" + localId.ToString(CultureInfo.InvariantCulture)
                    : "iid:" + t.GetInstanceID().ToString(CultureInfo.InvariantCulture);

                TypeBytes(t, ref row);
                rows.Add(row);
            }
            foreach (var bad in unreadableMaterials)
                rows.Add(new TexRow
                {
                    Slot = bad, SlotKey = bad, Identity = "unreadable:" + bad,
                    Format = "unreadable", Unknown = true,
                    Caveat = "this material's texture properties could not be read, so any texture only it "
                           + "reaches is in neither the rows nor the row total",
                });
            return rows;
        }

        /// <summary>Fill a row's <c>Format</c> and <c>Bytes</c>, or mark it Unknown. Each rate is measured against the
        /// SDK on a bare renderer:
        /// <list type="bullet">
        /// <item><c>Cubemap</c>: one face's mip chain, not six (128 DXT1 reported 10922 bytes; six would be 65532).</item>
        /// <item><c>Texture2DArray</c>: <c>w * h * depth * bpp</c> with no mip chain (128x128x4 DXT1 reported 32768).</item>
        /// <item><c>RenderTexture</c>: zero (a 256 RenderTexture on a renderer moved <c>textureMegabytes</c> not at
        /// all), so it stays out of the row total.</item>
        /// </list>
        /// <c>Texture3D</c> and anything else is unmeasured, so Unknown.</summary>
        internal static void TypeBytes(Texture t, ref TexRow row)
        {
            var t2 = t as Texture2D;
            if (t2 != null || t is Cubemap)
            {
                var fmt = t2 != null ? t2.format : ((Cubemap)t).format;
                row.Format = fmt.ToString() + (t2 == null ? " (cubemap)" : "");
                float bpp;
                if (!BytesPerPixel.TryGetValue(fmt, out bpp))
                {
                    row.Unknown = true;
                    row.Caveat = "format " + fmt + " is not typed by this arithmetic, so its bytes are unknown rather than guessed";
                    return;
                }
                if (t2 == null) row.Caveat = "cubemap: the SDK rates one face's mip chain, not six";
                row.Bytes = (long)Math.Round(MipChainPixels(t.width, t.height, t.mipmapCount) * (double)bpp);
                return;
            }

            var arr = t as Texture2DArray;
            if (arr != null)
            {
                row.Format = arr.format + " (array x" + arr.depth.ToString(CultureInfo.InvariantCulture) + ")";
                float bpp;
                if (!BytesPerPixel.TryGetValue(arr.format, out bpp))
                {
                    row.Unknown = true;
                    row.Caveat = "array format " + arr.format + " is not typed by this arithmetic";
                    return;
                }
                row.Caveat = "texture array: the SDK rates base level times slice count, with no mip chain";
                row.Bytes = (long)Math.Round((double)arr.width * arr.height * arr.depth * bpp);
                return;
            }

            if (t is RenderTexture)
            {
                row.Unknown = true;
                row.Format = "RenderTexture";
                row.Caveat = "the SDK rates a RenderTexture at zero, so this row is outside BOTH totals and cannot explain a residual";
                return;
            }

            row.Unknown = true;
            row.Format = t.GetType().Name;
            row.Caveat = "this texture type is not typed by this arithmetic, so its bytes are unknown rather than guessed";
        }

        /// <summary>Materials any <c>m_Materials</c> object-reference curve can put into a slot, across every clip on
        /// <see cref="SwapSearchControllers"/>. A material a component sets (MA Material Setter) is not reached.</summary>
        private static IEnumerable<Material> SwapReachableMaterials(GameObject root)
        {
            var seen = new HashSet<Material>();
            foreach (var controller in SwapSearchControllers(root))
                foreach (var clip in controller.animationClips)
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

        /// <summary>Every controller a swap curve could live on. The descriptor's playable layers are the load-bearing
        /// half: NDMF writes built controllers back to the descriptor slots, VRCFury's holder destroys the root
        /// Animator mid-build, and Modular Avatar never assigns one, so child Animators alone find nothing on
        /// essentially every avatar.</summary>
        private static IEnumerable<RuntimeAnimatorController> SwapSearchControllers(GameObject root)
        {
            var seen = new HashSet<RuntimeAnimatorController>();
            var descriptor = root.GetComponentInChildren<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>(true);
            if (descriptor != null)
            {
                var layers = new List<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.CustomAnimLayer>();
                if (descriptor.baseAnimationLayers != null) layers.AddRange(descriptor.baseAnimationLayers);
                if (descriptor.specialAnimationLayers != null) layers.AddRange(descriptor.specialAnimationLayers);
                foreach (var layer in layers)
                {
                    if (layer.isDefault || layer.animatorController == null) continue;
                    if (seen.Add(layer.animatorController)) yield return layer.animatorController;
                }
            }
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.runtimeAnimatorController == null) continue;
                if (seen.Add(animator.runtimeAnimatorController)) yield return animator.runtimeAnimatorController;
            }
        }

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
                if (inA && inB)
                {
                    var a = p.Authored.Value;
                    var b = p.Built.Value;
                    if (a.Width != b.Width || a.Height != b.Height || a.Format != b.Format || a.Mips != b.Mips)
                        caveats.Add("authored shape: " + a.Width + "x" + a.Height + " " + a.Format + ", " + a.Mips + " mips");
                }
                lines.Add("| `" + RunLogFormat.Cell(row.Slot) + "`" + presence
                        + " | " + row.Width + "x" + row.Height
                        + " | " + RunLogFormat.Cell(row.Format)
                        + " | " + row.Mips
                        + " | " + MbCell(p.Authored) + " | " + MbCell(p.Built)
                        + " | " + RunLogFormat.Cell(string.Join("; ", caveats.ToArray())) + " |");
            }
            lines.Add("| total (rows) | | | | " + Mb(SumBytes(authored)) + " | " + Mb(SumBytes(built)) + " | |");
            lines.Add("| total (SDK) | | | | " + SdkCell(sdkAuthored) + " (pre-bake) | " + SdkCell(sdkBuilt) + " | |");
            lines.Add("");

            lines.Add("**The two totals are one measurement, and their residual is the check.** The row total sums "
                    + "this section's own arithmetic — the mip chain's pixels times the format's measured bytes per "
                    + "pixel; the SDK total is `AvatarPerformanceStats.textureMegabytes`. They are computed the same "
                    + "way, so the expected residual is ZERO and a test asserts it. "
                    + ResidualSentence(authored, built, sdkAuthored, sdkBuilt));
            lines.Add("**Only the BUILT SDK total is a rank figure.** The authored one is a pre-bake read, which "
                    + "`optimization.md` §Measuring rules out for any rank conversation — MA, VRCFury and the "
                    + "optimizers all reshape what is counted. It is here to make the authored rows checkable, not "
                    + "to be quoted; `textureMB=` on the summary line is the built one.");
            lines.Add("A texture reached by several slots is ONE row, labelled by its first reaching slot in "
                    + "ordinal slot order, with the rest disclosed in its caveat — it is counted once here because "
                    + "the SDK counts it once, so the rows do sum to the total.");
            lines.Add("**An `unknown` MB cell is not a zero.** A `TextureFormat` this arithmetic does not type, and a "
                    + "texture type it does not type (`Texture3D`), are excluded from the row total and from the "
                    + "ordering rather than guessed at — those rows are where the two totals can legitimately "
                    + "disagree. Cubemaps and texture arrays are **not** among them: both are measured and typed "
                    + "(a cubemap at one face's mip chain, an array at base level times slice count). A "
                    + "**RenderTexture** is `unknown` for the opposite reason — the SDK rates it at zero, so it sits "
                    + "outside BOTH totals and provably cannot explain a residual.");
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

        // ── Sync: the clone's built synced-bit total and whether VRCFury's Parameter Compressor compressed ──

        /// <summary>What the clone says about sync after every build pass. The compressor leaves two marks, read
        /// independently because they fail differently: a layer on a playable-layer controller, and a
        /// <c>VF.Model.VRCFuryDebugInfo</c> on the root that is <c>IEditorOnly</c> and outlives the SDK's final strip
        /// only through VRCFury's Harmony prefix. <see cref="SyncSection"/> treats their disagreement as
        /// unread.</summary>
        internal sealed class SyncRead
        {
            public int? Bits;
            public string BitsError;
            public bool Layer;
            public bool Component;
            public string ComponentText;
            public int? OldTotal;
            public int Max = VRCExpressionParameters.MAX_PARAMETER_COST;
            public List<BuiltParam> Params;
        }

        internal struct BuiltParam { public string Name, Type; public bool Synced; }

        private const string CompressorTitle = "Parameter Compressor";
        private const string CompressorDebugType = "VF.Model.VRCFuryDebugInfo";

        /// <summary>VRCFury's <c>ControllerManager.NewLayerName</c> prefixes every layer it creates with
        /// <c>[VF&lt;n&gt;] </c>, so the built layer is <c>[VF12] Parameter Compressor</c>, never the bare title.</summary>
        private static readonly Regex CompressorLayer =
            new Regex(@"^(\[VF\d+\] )?(Parameter Compressor|Legacy Parameter Compressor( \(Math\))?)$");

        internal static bool IsCompressorLayerName(string name) =>
            name != null && CompressorLayer.IsMatch(name);

        internal static SyncRead ReadSync(GameObject clone)
        {
            var read = new SyncRead();
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var ep = d != null ? d.expressionParameters : null;
            if (d == null) read.BitsError = "the clone has no VRCAvatarDescriptor";
            else if (ep == null) read.BitsError = "the clone descriptor's expressionParameters is null";
            else
            {
                try { read.Bits = ep.CalcTotalCost(); }
                catch (Exception e) { read.BitsError = "CalcTotalCost threw " + e.GetType().Name + ": " + e.Message; }
                read.Params = new List<BuiltParam>();
                if (ep.parameters != null)
                    foreach (var p in ep.parameters)
                        if (p != null && !string.IsNullOrEmpty(p.name))
                            read.Params.Add(new BuiltParam { Name = p.name, Type = p.valueType.ToString(), Synced = p.networkSynced });
            }

            if (d != null)
                foreach (var set in new[] { d.baseAnimationLayers, d.specialAnimationLayers })
                {
                    if (set == null) continue;
                    foreach (var l in set)
                    {
                        var ac = l.animatorController as AnimatorController;
                        if (ac == null || ac.layers == null) continue;
                        if (ac.layers.Any(x => x != null && IsCompressorLayerName(x.name)))
                            read.Layer = true;
                    }
                }

            // Matched by name: the class is internal, its fields public, and a workspace without VRCFury simply has
            // none.
            foreach (var c in clone.GetComponents<Component>())
            {
                if (c == null) continue;
                var ct = c.GetType();
                if (ct.FullName != CompressorDebugType) continue;
                var titleField = ct.GetField("title", BindingFlags.Instance | BindingFlags.Public);
                var title = titleField != null ? titleField.GetValue(c) as string : null;
                if (title != CompressorTitle) continue;
                read.Component = true;
                var textField = ct.GetField("debugInfo", BindingFlags.Instance | BindingFlags.Public);
                read.ComponentText = (textField != null ? textField.GetValue(c) as string : null) ?? "";
                var m = Regex.Match(read.ComponentText, @"Old Total:\s*(\d+)\s*bit");
                int old;
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out old))
                    read.OldTotal = old;
                break;
            }
            return read;
        }

        internal static List<string> SyncSection(SyncRead read, string paramFilter, out string summaryKeys)
        {
            var lines = SyncFigure(read, paramFilter, out summaryKeys);
            lines.Add("");
            if (read.Params == null)
            {
                lines.Add("**The built parameters were not read, so no per-parameter row is published:** "
                        + RunLogFormat.Cell(read.BitsError ?? "(no error recorded)"));
                return lines;
            }
            lines.Add("| built parameter | type | synced |");
            lines.Add("| --- | --- | --- |");
            var shown = string.IsNullOrEmpty(paramFilter) ? read.Params
                : read.Params.Where(p => p.Name.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (shown.Count == 0) lines.Add("| _(none)_ | | |");
            foreach (var p in shown)
                lines.Add("| `" + RunLogFormat.Cell(p.Name) + "` | " + p.Type + " | " + (p.Synced ? "yes" : "no") + " |");
            lines.Add("");
            lines.Add("**`synced` is the built asset's `networkSynced`, under the BUILT name** (the Bake diff maps an "
                    + "authored name to it). A `no` here beside an authored `yes` in the Parameters table means the "
                    + "build stopped syncing that parameter under its own name, and the Parameter Compressor is one "
                    + "cause among several: VRCFury also unsyncs a parameter it drives as an animated animator "
                    + "parameter, and VRChat's own global names. A compressed parameter still replicates, through "
                    + "the compressor's channel; the others do not. So read `compressor=` before calling a row "
                    + "compressed, and a `no` with `compressor=no` is never one.");
            return lines;
        }

        private static List<string> SyncFigure(SyncRead read, string paramFilter, out string summaryKeys)
        {
            string max = read.Max.ToString(CultureInfo.InvariantCulture);
            var lines = new List<string>
            {
                "the clone's built sync state; paramFilter narrows the per-parameter table at the end of this section and nothing above it"
                    + (string.IsNullOrEmpty(paramFilter) ? ""
                       : " (`" + RunLogFormat.Cell(paramFilter) + "` is active, so that table is narrowed; the figures are of the WHOLE avatar)"),
                "",
            };
            if (read.Bits == null)
            {
                lines.Add("**The sync read did not complete, so no figure is published:** " + RunLogFormat.Cell(read.BitsError ?? "(no error recorded)"));
                summaryKeys = "syncedBits=unread compressor=unread";
                return lines;
            }
            string bits = read.Bits.Value.ToString(CultureInfo.InvariantCulture);
            string ceiling = "the SDK's MAX_PARAMETER_COST is " + max;

            if (read.Layer && read.Component)
            {
                string before = read.OldTotal.HasValue ? read.OldTotal.Value.ToString(CultureInfo.InvariantCulture) : null;
                lines.Add("synced bits: " + bits + " (" + ceiling + "), after the Parameter Compressor reduced "
                        + (before ?? "a total its text below did not state in the expected form")
                        + ". The figure includes the compressor's own channel; compressed parameters are no longer "
                        + "networkSynced in the built asset and replicate through that channel at the sync delay below.");
                lines.Add("");
                lines.Add("```");
                foreach (var l in (read.ComponentText ?? "").Replace("\r", "").Split('\n')) lines.Add(l);
                lines.Add("```");
                summaryKeys = "syncedBits=" + bits + " compressor=yes syncedBitsBefore=" + (before ?? "unread");
                return lines;
            }
            if (read.Layer != read.Component)
            {
                lines.Add("synced bits: " + bits + " (" + ceiling + ")");
                lines.Add("");
                lines.Add("**Whether the Parameter Compressor ran could not be settled:** "
                        + (read.Layer
                            ? "a playable-layer controller carries its layer but the clone root has no \"" + CompressorTitle
                              + "\" debug component (that component survives the SDK's EditorOnly strip only through VRCFury's Harmony patch)."
                            : "the clone root carries its \"" + CompressorTitle + "\" debug component but no playable-layer controller carries its layer.")
                        + " Read the two marks on a fresh clone rather than trusting either.");
                summaryKeys = "syncedBits=" + bits + " compressor=unread";
                return lines;
            }
            if (read.Bits.Value > read.Max)
            {
                lines.Add("synced bits: " + bits + ", above the SDK's MAX_PARAMETER_COST of " + max
                        + ", and the Parameter Compressor did not compress: it declined (no fit, or VRCFury's setting is Fail). "
                        + "An upload of this build would be refused by the SDK.");
                summaryKeys = "syncedBits=" + bits + " compressor=declined";
                return lines;
            }
            lines.Add("synced bits: " + bits + "; the Parameter Compressor did not run (it engages above the SDK's MAX_PARAMETER_COST of " + max + ").");
            summaryKeys = "syncedBits=" + bits + " compressor=no";
            return lines;
        }

        // ── Built menu: the clone's expressions menu, control by control ──────────────────────────────

        internal struct BuiltMenuRow { public string Path, Type, Parameter, SubParameters; public float Value; }

        /// <summary>The clone's built expressions menu. <see cref="Rows"/> null with <see cref="Error"/> means the root
        /// menu was not reached, a different answer from a menu holding no controls.</summary>
        internal sealed class BuiltMenuRead
        {
            public List<BuiltMenuRow> Rows;
            public string Error;
            public readonly List<string> Notes = new List<string>();
        }

        private const int BuiltMenuMaxDepth = 12;

        internal static BuiltMenuRead ReadBuiltMenu(GameObject clone)
        {
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            if (d == null) return new BuiltMenuRead { Error = "the clone has no VRCAvatarDescriptor" };
            if (d.expressionsMenu == null) return new BuiltMenuRead { Error = "the clone descriptor's expressionsMenu is null" };
            return ReadMenu(d.expressionsMenu);
        }

        /// <summary>A submenu reached twice from different parents is listed twice, because the built menu shows it
        /// twice. Only a submenu reopening one of its own ancestors is cut.</summary>
        internal static BuiltMenuRead ReadMenu(VRCExpressionsMenu root)
        {
            var read = new BuiltMenuRead { Rows = new List<BuiltMenuRow>() };
            var open = new List<VRCExpressionsMenu>();
            void Walk(VRCExpressionsMenu menu, string path, int depth)
            {
                if (menu.controls == null) return;
                if (depth > BuiltMenuMaxDepth)
                {
                    read.Notes.Add("the walk stopped at depth " + BuiltMenuMaxDepth + " under `" + path + "`; deeper controls are not in the table.");
                    return;
                }
                open.Add(menu);
                foreach (var c in menu.controls)
                {
                    if (c == null) continue;
                    string p = string.IsNullOrEmpty(path) ? c.name : path + "/" + c.name;
                    read.Rows.Add(new BuiltMenuRow
                    {
                        Path = p, Type = c.type.ToString(), Value = c.value,
                        Parameter = c.parameter != null && !string.IsNullOrEmpty(c.parameter.name) ? c.parameter.name : null,
                        SubParameters = c.subParameters == null ? null
                            : string.Join(", ", c.subParameters.Where(s => s != null && !string.IsNullOrEmpty(s.name)).Select(s => s.name)),
                    });
                    // A control retyped away from SubMenu can keep a stale subMenu reference that nothing opens.
                    if (c.type != VRCExpressionsMenu.Control.ControlType.SubMenu || c.subMenu == null) continue;
                    if (open.Contains(c.subMenu))
                        read.Notes.Add("`" + p + "` opens a menu already open above it (a cycle); its controls are listed once, at the first opening.");
                    else Walk(c.subMenu, p, depth + 1);
                }
                open.RemoveAt(open.Count - 1);
            }
            Walk(root, "", 0);
            return read;
        }

        /// <summary>The <c>## Built menu</c> section. It joins nothing to the authored Menu controls table: the build
        /// rewrites paths and names, so any pairing would be inference.</summary>
        internal static List<string> BuiltMenuSection(BuiltMenuRead read, string paramFilter, out string summaryKey)
        {
            var lines = new List<string>
            {
                "the clone's built expressions menu, every control in menu order"
                    + (string.IsNullOrEmpty(paramFilter) ? ""
                       : "; `" + RunLogFormat.Cell(paramFilter) + "` narrows the table to controls whose parameter or a sub-parameter contains it, and the summary count is of the WHOLE menu"),
                "",
            };
            if (read.Rows == null)
            {
                lines.Add("**The built menu was not read, so no row is published:** " + RunLogFormat.Cell(read.Error ?? "(no error recorded)"));
                summaryKey = "builtMenuControls=unread";
                return lines;
            }
            summaryKey = "builtMenuControls=" + read.Rows.Count.ToString(CultureInfo.InvariantCulture);
            bool Hit(string s) => s != null && s.IndexOf(paramFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            var shown = string.IsNullOrEmpty(paramFilter) ? read.Rows
                : read.Rows.Where(r => Hit(r.Parameter) || Hit(r.SubParameters)).ToList();
            lines.Add("| path | type | built parameter | value | sub-parameters |");
            lines.Add("| --- | --- | --- | --- | --- |");
            if (shown.Count == 0) lines.Add("| _(none)_ | | | | |");
            foreach (var r in shown)
                lines.Add("| `" + RunLogFormat.Cell(r.Path) + "` | " + r.Type
                        + " | " + (r.Parameter == null ? "—" : "`" + RunLogFormat.Cell(r.Parameter) + "`")
                        + " | " + (r.Parameter == null ? "—" : r.Value.ToString("G", CultureInfo.InvariantCulture))
                        + " | " + (string.IsNullOrEmpty(r.SubParameters) ? "—" : "`" + RunLogFormat.Cell(r.SubParameters) + "`") + " |");
            foreach (var n in read.Notes) lines.Add("> note: " + n + "  ");
            lines.Add("");
            lines.Add("**Paths are the built menu's own**, including the pages a framework adds when one overflows "
                    + "(Modular Avatar's `More`; VRCFury's `Next`, or its menu-settings override), so a path here need not match an "
                    + "authored one. A parameter is the name the built animator reads. An auto-named Modular Avatar toggle has no "
                    + "authored name, so the Bake diff rows its parameter `built-only`; this table is where its control is named.");
            return lines;
        }

        // ── Performance: the SDK's own scan of the clone ─────────────────────────────────────────────

        internal struct PhysBoneRow
        {
            public string Path;
            public int Bones;
            public int Colliders;
            public bool Active;
            public bool Ignored;
            public string Caveat;
        }

        internal sealed class PerformanceRead
        {
            public VRC.SDKBase.Validation.Performance.Stats.AvatarPerformanceStats Stats;
            public string Error;
            public string ConstraintRefresh;
            public readonly List<PhysBoneRow> PhysBones = new List<PhysBoneRow>();
        }

        private static readonly MethodInfo MiRefreshConstraintGroups =
            VendorReflect.FindType("VRC.Dynamics.VRCConstraintManager")?.GetMethod("Sdk_ManuallyRefreshGroups",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>Scan the baked clone the way the upload panel does, PC thresholds. The clone's VRC constraints are
        /// regrouped first: on a fresh clone the manager has not grouped them, and <c>constraintDepth</c> then reads a
        /// different wrong figure bake to bake. The census runs after the scan because <c>InitTransforms</c> rebuilds
        /// each chain's bone list.</summary>
        internal static PerformanceRead ReadPerformance(GameObject clone)
        {
            var read = new PerformanceRead();
            var constraints = clone.GetComponentsInChildren<VRC.Dynamics.VRCConstraintBase>(true);
            if (MiRefreshConstraintGroups == null) read.ConstraintRefresh = "unavailable";
            else
            {
                try { MiRefreshConstraintGroups.Invoke(null, new object[] { constraints }); read.ConstraintRefresh = "ok"; }
                catch (Exception e) { read.ConstraintRefresh = "failed: " + VendorReflect.DescribeInvokeError(e); }
            }

            try
            {
                var stats = new VRC.SDKBase.Validation.Performance.Stats.AvatarPerformanceStats(false);
                VRC.SDKBase.Validation.Performance.AvatarPerformance.CalculatePerformanceStats(clone.name, clone, stats, false);
                stats.CalculateAllPerformanceRatings(false);
                read.Stats = stats;
            }
            catch (Exception e) { read.Error = e.GetType().Name + ": " + e.Message; }

            foreach (var pb in clone.GetComponentsInChildren<VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone>(true))
            {
                var row = new PhysBoneRow
                {
                    Path = RelPath(clone, pb.transform),
                    Active = pb.isActiveAndEnabled,
                    Colliders = pb.colliders == null ? 0 : pb.colliders.Count(c => c != null),
                    Ignored = ScanIgnores(pb),
                };
                try { pb.InitTransforms(true); row.Bones = pb.bones == null ? -1 : pb.bones.Count; }
                catch (Exception e) { row.Bones = -1; row.Caveat = "InitTransforms threw " + e.GetType().Name; }
                read.PhysBones.Add(row);
            }
            return read;
        }

        private static bool ScanIgnores(Component c)
        {
            for (var t = c.transform; t != null; t = t.parent)
                if (t.CompareTag("EditorOnly")) return true;
            var hook = VRC.SDKBase.Validation.Performance.AvatarPerformance.ShouldIgnoreComponent;
            try { return hook != null && hook(c); }
            catch (Exception) { return false; }
        }

        private static readonly VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory[] NotACategory =
        {
            VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory.None,
            VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory.Overall,
            VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory.AvatarPerformanceCategoryCount,
        };

        internal static List<string> PerformanceSection(PerformanceRead read, string paramFilter, out string summaryKeys)
        {
            var lines = new List<string>
            {
                "the SDK's performance scan of the clone (PC); paramFilter does not narrow this section"
                    + (string.IsNullOrEmpty(paramFilter) ? ""
                       : " (`" + RunLogFormat.Cell(paramFilter) + "` is active and narrows the parameter tables ONLY)"),
                "",
            };
            if (read.Stats == null)
            {
                lines.Add("**The scan threw, so no figure is published:** " + RunLogFormat.Cell(read.Error ?? "(no error recorded)"));
                summaryKeys = "rank=unread";
                return lines;
            }

            var stats = read.Stats;
            var overall = stats.GetPerformanceRatingForCategory(VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory.Overall);
            var setBy = new List<string>();
            lines.Add("overall: **" + overall + "**");
            lines.Add("");
            lines.Add("| category | rating |");
            lines.Add("| --- | --- |");
            foreach (VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory cat in
                     Enum.GetValues(typeof(VRC.SDKBase.Validation.Performance.AvatarPerformanceCategory)))
            {
                if (Array.IndexOf(NotACategory, cat) >= 0) continue;
                string cell;
                try
                {
                    var r = stats.GetPerformanceRatingForCategory(cat);
                    cell = r.ToString();
                    if (r == overall && overall != VRC.SDKBase.Validation.Performance.PerformanceRating.None) setBy.Add(cat.ToString());
                }
                catch (Exception e) { cell = "unread (" + e.GetType().Name + ")"; }
                lines.Add("| " + cat + " | " + cell + " |");
            }

            lines.Add("");
            lines.Add("| stat | value |");
            lines.Add("| --- | --- |");
            foreach (var f in stats.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.Name == "avatarName") continue;
                var v = f.GetValue(stats);
                if (v == null) { lines.Add("| " + f.Name + " | — |"); continue; }
                var vt = v.GetType();
                if (!vt.IsPrimitive && !vt.IsEnum && (vt.Namespace ?? "").StartsWith("VRC", StringComparison.Ordinal))
                    foreach (var g in vt.GetFields(BindingFlags.Instance | BindingFlags.Public))
                        lines.Add("| " + f.Name + "." + g.Name + " | " + Convert.ToString(g.GetValue(v), CultureInfo.InvariantCulture) + " |");
                else
                    lines.Add("| " + f.Name + " | " + RunLogFormat.Cell(Convert.ToString(v, CultureInfo.InvariantCulture)) + " |");
            }
            lines.Add("");
            lines.Add("Constraint-group refresh before the scan: " + read.ConstraintRefresh
                    + (read.ConstraintRefresh == "ok" ? "." : " — `constraintDepth` above may not be the figure an upload rates."));

            lines.Add("");
            lines.Add("### PhysBone chains");
            lines.Add("");
            lines.Add("| physbone (path) | active | bones | colliders | caveat |");
            lines.Add("| --- | --- | --- | --- | --- |");
            int sum = 0;
            foreach (var r in read.PhysBones.OrderBy(r => r.Path, StringComparer.Ordinal))
            {
                if (r.Bones >= 0 && !r.Ignored) sum += r.Bones;
                string caveat = r.Ignored ? "skipped by the SDK scan; not in the sum" + (r.Caveat != null ? "; " + r.Caveat : "") : r.Caveat ?? "";
                lines.Add("| `" + RunLogFormat.Cell(r.Path) + "` | " + (r.Active ? "yes" : "no") + " | "
                        + (r.Bones >= 0 ? r.Bones.ToString(CultureInfo.InvariantCulture) : "unreadable") + " | "
                        + r.Colliders + " | " + RunLogFormat.Cell(caveat) + " |");
            }
            string sdkTransforms = stats.physBone.HasValue
                ? stats.physBone.Value.transformCount.ToString(CultureInfo.InvariantCulture) : "—";
            lines.Add("| total bones | | " + sum + " | | SDK `physBone.transformCount` = " + sdkTransforms + " |");
            lines.Add("");
            lines.Add("A chain's `bones` is the list the SDK counts, so the column locates where `physBone.transformCount` "
                    + "goes and should sum to it; a residual means an unreadable row, or a chain the scan skips by a rule "
                    + "this census does not see. `colliders` is the chain's "
                    + "reference count, not the SDK's `collisionCheckCount` rule. Inactive chains count at full weight.");

            summaryKeys = "rank=" + overall + (setBy.Count > 0 ? " rankSetBy=" + string.Join(",", setBy) : "");
            return lines;
        }

        /// <summary>The SDK's own <c>textureMegabytes</c> for <paramref name="go"/>, the figure the upload gate rates.
        /// Null on any failure, never a 0, which would claim the avatar carries no textures.</summary>
        internal static float? SdkTextureMegabytes(GameObject go)
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

        /// <summary>Identity first, then the slot set, then unpaired. An untouched texture keeps its identity through a
        /// renderer merge, where its slot set changes; a resized one is a new object whose only surviving handle is
        /// its slot set. Each built row is claimed at most once.</summary>
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

            return pairs
                .OrderByDescending(p => p.Built.HasValue && !p.Built.Value.Unknown ? p.Built.Value.Bytes
                                      : p.Authored.HasValue && !p.Authored.Value.Unknown ? p.Authored.Value.Bytes : -1L)
                .ThenBy(p => (p.Built ?? p.Authored.Value).Slot, StringComparer.Ordinal)
                .ToList();
        }

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

        /// <summary>One row of the diff. <c>Caveat</c> is kept out of <c>Surface</c> so the <c>merged</c> pass can carry
        /// it onto the row that replaces this one.</summary>
        internal struct DiffRow { public string Authored, Built, Category, Surface, Caveat; }

        /// <summary>Every parameter name the built clone declares: its descriptor's expression parameters union every
        /// parameter on every controller the clone plays. The expression asset alone is a synced-parameter budget, and
        /// a controller-only value (a driver scratch, a gesture built-in) never appears there.
        /// <para><paramref name="incompleteReason"/> is set only from the playable-layer slots, because a non-null
        /// value downgrades every unmatched row on the avatar; <paramref name="notes"/> takes any other controller
        /// this read cannot resolve, downgrading nothing.</para></summary>
        internal static List<string> BuiltDeclarations(GameObject clone, List<string> notes, out string incompleteReason)
        {
            incompleteReason = null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            var d = clone.GetComponent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
            var ep = d != null ? d.expressionParameters : null;
            if (ep != null && ep.parameters != null)
                foreach (var p in ep.parameters)
                    if (p != null && !string.IsNullOrEmpty(p.name)) names.Add(p.name);
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
                        // Never fired post-bake when measured, but the SDK keeps isDefault and a non-null controller
                        // mutually exclusive only along its inspector path, so a programmatic write can leave a
                        // default-flagged slot holding a controller the built avatar does not play: a false `kept`.
                        if (l.isDefault) continue;
                        if (l.animatorController == null) continue;
                        slotsWithController++;
                        if (!AddParams(l.animatorController, names))
                            unreadableSlots.Add(l.type + " → `" + l.animatorController.name + "` ("
                                              + l.animatorController.GetType().Name + ")");
                    }
                }
            }
            // An empty slot is authoring state and declares nothing; only a controller that cannot be read makes this
            // read partial.
            if (unreadableSlots.Count > 0)
                incompleteReason = unreadableSlots.Count + " playable-layer slot(s) hold a controller this read "
                    + "could not resolve to an AnimatorController (" + string.Join("; ", unreadableSlots)
                    + "), so their parameters are absent and the built side is a PARTIAL read";
            // No controller in any slot and nothing declared is a clone read after its generated assets were swept,
            // where every authored name would fall to `dropped`. A healthy bake always has a controller in a slot.
            else if (d != null && slotsWithController == 0 && names.Count == 0)
                incompleteReason = "the built clone's descriptor holds no playable-layer controller at all and its "
                    + "expression parameters declare nothing, so NOTHING of the built side was readable — the shape "
                    + "of a clone read after its generated assets were swept, not of an avatar with nothing on it";
            return names.ToList();
        }

        /// <summary>Union <paramref name="rac"/>'s parameters into <paramref name="into"/>. Returns false only when a
        /// controller was present and could not be read as an <c>AnimatorController</c>.</summary>
        private static bool AddParams(RuntimeAnimatorController rac, HashSet<string> into)
        {
            if (rac == null) return true;
            var ac = rac as AnimatorController;
            // Unlike the asset doors, which refuse an override controller by name, this unwraps it: the subject is the
            // built avatar's parameter set, and an override declares no parameters of its own. Chains unwrap too,
            // depth-capped because a cycle in the data must not hang the bake.
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

        /// <summary>Is <paramref name="built"/> a prefixed form of <paramref name="authored"/>? The build prepends a
        /// namespace, so the authored name survives as a suffix behind a separator; anything else stays
        /// unattributed.</summary>
        internal static bool IsPrefixedForm(string built, string authored)
        {
            if (string.IsNullOrEmpty(built) || string.IsNullOrEmpty(authored)) return false;
            if (built.Length <= authored.Length || !built.EndsWith(authored, StringComparison.Ordinal)) return false;
            char boundary = built[built.Length - authored.Length - 1];
            return boundary == '/' || boundary == '_';
        }

        /// <summary>Diff the authored census against the built declaration set. A row the census did not mark diffable
        /// is reported <c>not-in-scope</c>: the runtime writes it and nothing declares it, so a declaration set cannot
        /// carry it.
        /// <para><paramref name="paramFilter"/> is applied last, to both sides of each row, so it narrows the view
        /// without changing a category over the census it is handed. The live census is already narrowed by the same
        /// filter (<c>ReportComposition.Census</c>), so an authored name the filter misses has no row here, and a
        /// built form of it that matches reads <c>built-only</c>.</para></summary>
        internal static List<DiffRow> Diff(ReportComposition.CensusResult census, List<string> built, string paramFilter,
                                           bool builtSideComplete = true)
        {
            var builtSet = new HashSet<string>(built, StringComparer.Ordinal);
            var unclaimed = new HashSet<string>(built, StringComparer.Ordinal);
            var claimedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var rowsByBuilt = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var rows = new List<DiffRow>();

            // Pass 1: every built name an authored row claims by exact match, under the `kept` branch's own predicate.
            var exactClaim = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in census.Params)
                if (p.Diffable && builtSet.Contains(p.Name)) exactClaim.Add(p.Name);

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
                    bool alsoBuilt = builtSet.Contains(p.Name);
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = alsoBuilt ? p.Name + " (see its built-only row)" : "—",
                        Category = "not-in-scope",
                        Surface = "runtime-written (physbone or raycast suffix / menu sub-parameter) — nothing declares it, so a declaration set cannot carry it"
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
                // Pass 2: a built name already claimed by exact match is not this row's rename. That exclusion is a
                // tie-breaker, so it applies only while it leaves a candidate standing: authored `Toggle` +
                // `Hair/Toggle` onto built `Hair/Toggle` (MA `remapTo`) is a merge, and emptying the set would report
                // it `dropped`.
                var narrowed = raw.Where(b => !exactClaim.Contains(b)).ToList();
                var candidates = narrowed.Count > 0 ? narrowed : raw;
                if (candidates.Count == 1)
                {
                    Claim(candidates[0], p.Name, rows.Count);
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = candidates[0], Category = "renamed", Surface = p.Declared,
                        Caveat = candidates.Count == raw.Count ? null : ExclusionNote(raw, narrowed),
                    });
                }
                else if (candidates.Count > 1)
                {
                    // The candidates stay unclaimed, so a genuinely built-only parameter matching an ambiguous authored
                    // name still gets its own built-only row.
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
                    rows.Add(new DiffRow
                    {
                        Authored = p.Name, Built = "?", Category = "built-side-unread",
                        Surface = p.Declared + " — no built name matched, and the built read was incomplete, so removal is NOT the claim",
                    });
                }
            }

            var drop = new HashSet<int>();
            var merged = new List<DiffRow>();
            foreach (var kv in claimedBy)
            {
                if (kv.Value.Count <= 1) continue;
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

        private static string ExclusionNote(List<string> raw, List<string> narrowed)
        {
            var excluded = raw.Where(b => !narrowed.Contains(b)).Select(b => "`" + b + "`");
            return " — the single candidate only after excluding " + string.Join(", ", excluded)
                 + ", each already claimed by an authored name of its own (an exact match beats a prefix "
                 + "inference). If the build instead MERGED this name onto one of those, the truth is `merged` "
                 + "and this row does not say so.";
        }

        private static string Refusal(string path, string stage, string name, bool probeSupplied) =>
            "# ReportComposition (bake)\n\nstatus: FAILED\n\nsummary: [ReportComposition] mode=bake => FAIL ("
            + stage + ") | log=" + path
            + "\n\nThe bake did not complete, so this artifact carries NO parameter table. Authored-census rows are "
            + "deliberately not published here: presenting them under a heading that promises composed truth is the "
            + "misread this door exists to prevent. Run `Report(<avatarRoot>)` without the flag for the authored census, "
            + "knowing it is authored.\n"
            + (probeSupplied ? "\nThe probe supplied with this call did not run: there was no built clone to hand it.\n" : "");

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
