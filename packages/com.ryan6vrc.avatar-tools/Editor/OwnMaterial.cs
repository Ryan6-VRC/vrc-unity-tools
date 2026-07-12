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
    /// Materializes an owned deep-copy of a vendor material (or branches/augments an already-owned one)
    /// and forks exactly the named texture slots into that material's own namespace — every other slot
    /// keeps its source GUID reference. Routing is a function of target identity: <c>outDir</c> given ⇒
    /// own (vendor source) or branch (owned source, copy-to-new); <c>outDir</c> omitted ⇒ augment the
    /// source in place (fork more slots on an already-owned material). The skill holds the judgment of
    /// which slots to fork; this tool executes the copy deterministically and reports a per-slot
    /// provenance table (<c>slots[]</c>) as the caller's verification gate.
    ///
    /// Full behavioral spec: <c>docs/superpowers/specs/2026-07-11-own-material-lean-design.md</c>. This
    /// file currently implements Flow steps 1–3: arg guards, slot-name validation, and target-identity
    /// routing (own / branch / augment) with a copy-to-new deep copy. Forking, variant flatten, and
    /// locked-poi unlock are not wired yet (Tasks 3/5/6) — every slot on a landed <c>O</c> reports
    /// <c>vendor-ref</c> regardless of what was requested (a deliberate Task-2 stub; slot-name validation
    /// still FAILs a typo'd request, it just doesn't fork a valid one yet).
    ///
    /// Call <see cref="Run"/> from MCP execute_code or a menu item.
    /// </summary>
    [AgentTool]
    public static class OwnMaterial
    {
        /// <summary>
        /// Bring <paramref name="materialPath"/> into ownership and fork <paramref name="forkTextureSlots"/>
        /// (shader texture-property names, e.g. <c>_MainTex</c>) into the resulting owned material's own
        /// namespace. <paramref name="outDir"/> given ⇒ own/branch a NEW owned <c>.mat</c> there (named
        /// <paramref name="newName"/>, default the source's own name); omitted ⇒ augment the (already-owned)
        /// source in place. Returns a one-line PASS/FAIL summary ending with the RunLog path
        /// (<c>… =&gt; RESULT | log=&lt;path&gt;</c>); also Debug.Log/LogError's it.
        /// </summary>
        public static string Run(string materialPath, string outDir = null, string[] forkTextureSlots = null,
                                 string newName = null, bool force = false, bool whatIf = false)
        {
            // ── Guards (Flow step 1) ────────────────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(materialPath))
                return ArgFail("null-material", "materialPath is null or empty");

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
                return ArgFail(TransplantCore.Sanitize(TransplantCore.Leaf(materialPath)),
                    "materialPath '" + materialPath + "' did not load as a Material (missing, wrong asset type, or invalid path)");

            string label = TransplantCore.Sanitize(mat.name);

            var shader = mat.shader;
            if (shader == null || shader.name == "Hidden/InternalErrorShader")
            {
                string shaderName = shader == null ? "(null)" : shader.name;
                return ArgFail(label, "material '" + mat.name + "' (" + materialPath + ") has a broken/missing shader ('" +
                    shaderName + "') — cannot enumerate texture slots");
            }

            var data = new RunData
            {
                whatIf = whatIf,
                instance = mat.name,
                source = materialPath,
            };

            // ── Slot-name validation (Flow step 2) — BEFORE any write/routing (no mode resolved yet, so
            //    this FAIL stays label-only). A copy preserves the shader, so validating against S's
            //    texture properties == validating against O's; failing here means a typo never leaves a
            //    half-made copy. ──
            var requestedSlots = forkTextureSlots ?? Array.Empty<string>();
            var shaderTexProps = mat.GetTexturePropertyNames();
            foreach (var slot in requestedSlots)
            {
                if (Array.IndexOf(shaderTexProps, slot) < 0)
                    data.Offender("no texture property '" + slot + "' on shader '" + shader.name + "'");
            }
            if (data.offenders.Count > 0)
            {
                data.result = "FAIL";
                data.error = "requested slot is not a texture property on the shader";
                return Finish(data, label);
            }
            var requested = new HashSet<string>(requestedSlots, StringComparer.Ordinal);

            // ── Routing (Flow step 3): targetPath is a pure function of target identity. From here on
            //    the mode IS known, so every Fail/Finish call below prefixes it onto the label (the
            //    mode-first one-liner: a wrong-mode run is catchable at a glance). ──
            string sourcePath = materialPath.Replace('\\', '/');
            bool outDirGiven = !string.IsNullOrEmpty(outDir);
            string outClean = outDirGiven ? outDir.Replace('\\', '/').TrimEnd('/') : null;
            string targetPath = outDirGiven
                ? outClean + "/" + TransplantCore.Sanitize(!string.IsNullOrEmpty(newName) ? newName : mat.name) + ".mat"
                : sourcePath;

            bool inPlace = targetPath == sourcePath;
            if (inPlace)
            {
                string modeLabel = "augment " + label;

                // outDir given but resolves to the source: never a silent reroute — one signal
                // (outDir present/absent), one meaning (copy-to-new vs. augment).
                if (outDirGiven)
                    return Fail(data, modeLabel, "outDir resolves to the source — omit outDir to augment in place");
                // outDir omitted but newName given: closes the "meant to branch, forgot outDir, silently
                // mutated the SOURCE" hole (DELTA 4 — attempt 1 did not have this guard).
                if (!string.IsNullOrEmpty(newName))
                    return Fail(data, modeLabel, "newName requires outDir; omit both to augment in place");
                // A vendor source has nowhere writable to land — it must be OWNED first via an outDir.
                if (!TransplantCore.IsWritableAsset(sourcePath))
                    return Fail(data, modeLabel, "vendor source needs an outDir to own it into a writable bucket");
                // In-place O (== S here) must be neither locked nor a variant — this tool never
                // unlocks/flattens in place (that's the copy-to-new normalize step, Tasks 5/6).
                if (IsLocked(mat))
                    return Fail(data, modeLabel, "owned material is locked; unlock first");
                if (mat.parent != null)
                    return Fail(data, modeLabel, "owned material is a variant; OwnMaterial produces standalone materials");

                // No fork/normalize yet (Task 2 scope) — O is already S; report the stub slot table and PASS.
                BuildVendorRefSlots(mat, requested, data);
                data.result = "PASS";
                if (whatIf) data.Note("would augment '" + sourcePath + "' in place");
                return Finish(data, modeLabel);
            }

            // ── Copy-to-new: own (S vendor) or branch (S owned) — same mechanics, RunLog names which. ──
            string mode = TransplantCore.IsWritableAsset(sourcePath) ? "branch" : "own";
            string coLabel = mode + " " + label;

            if (!AssetDatabase.IsValidFolder(outClean) && File.Exists(outClean))
                return Fail(data, coLabel, "outDir '" + outClean + "' resolves to a file, not a folder");

            if (!TransplantCore.IsWritableAsset(targetPath))
            {
                if (force) data.Note("read-only target override (force): " + targetPath);
                else
                    return Fail(data, coLabel, "target '" + targetPath +
                        "' is read-only (under Assets/Vendor or Packages): choose an owned outDir, or pass force=true");
            }

            if (AssetDatabase.LoadMainAssetAtPath(targetPath) != null)
                return Fail(data, coLabel, "an owned material already exists at '" + targetPath +
                    "' — pass it as materialPath to fork more slots, or choose another newName");

            if (whatIf)
            {
                BuildVendorRefSlots(mat, requested, data);
                data.result = "PASS";
                data.Note("would create '" + targetPath + "'");
                return Finish(data, coLabel);
            }

            EnsureFolderExists(outClean);
            if (!AssetDatabase.CopyAsset(sourcePath, targetPath))
                return Fail(data, coLabel, "CopyAsset failed: " + sourcePath + " -> " + targetPath);

            var owned = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
            if (owned == null)
            {
                AssetDatabase.DeleteAsset(targetPath);
                return Fail(data, coLabel, "owned copy load failed immediately after CopyAsset: " + targetPath);
            }

            // No fork/normalize yet (Task 2 scope) — O is a byte-preserving deep copy of S; report the
            // stub slot table (every slot vendor-ref) and PASS.
            BuildVendorRefSlots(owned, requested, data);
            data.result = "PASS";
            return Finish(data, coLabel);
        }

        // ── Task-2 stub slot table (replaced by the full fork disposition matrix in Task 3) ─────────

        /// <summary>
        /// Lists every shader texture slot on <paramref name="src"/> as <c>vendor-ref</c> — Task 2 does
        /// not fork yet, so this is a deliberate placeholder: no slot is ever <c>forked</c>/
        /// <c>already-owned</c>/<c>unforkable</c>/<c>owned-elsewhere</c>/<c>empty</c> here, regardless of
        /// <paramref name="requested"/> (a requested slot still reports <c>vendor-ref</c> — the fork
        /// disposition itself is Task 3's <c>BuildPlan</c>). Read-only: reads each slot's texture once
        /// (never saved afterward in this stub path, so the getter-clears-keywords quirk never reaches
        /// disk — see memory <c>unity-material-getter-clears-keywords</c>).
        /// </summary>
        static void BuildVendorRefSlots(Material src, HashSet<string> requested, RunData data)
        {
            foreach (var slot in src.GetTexturePropertyNames())
            {
                var tex = src.GetTexture(slot);
                string texPath = tex != null ? AssetDatabase.GetAssetPath(tex) : null;
                data.slots.Add(new SlotRow
                {
                    slot = slot,
                    requested = requested.Contains(slot),
                    disposition = "vendor-ref",
                    sourcePath = texPath,
                    ownedPath = null,
                });
            }
            data.Count("slotsForked", 0);
            data.Count("slotsAlreadyOwned", 0);
            data.Count("slotsOwnedElsewhere", 0);
            data.Count("slotsVendorRef", data.slots.Count);
            data.Count("slotsBuiltin", 0);
            data.Count("slotsEmpty", 0);
        }

        /// <summary>A material is locked iff its shader is one of Thry's generated <c>Hidden/Locked/…</c>
        /// shaders. The full unlock mechanics land in Task 6; this predicate alone gates the in-place
        /// augment guard (Task 2) — an owned material must never be locked before this tool touches it.</summary>
        static bool IsLocked(Material m) =>
            m.shader != null && m.shader.name.StartsWith("Hidden/Locked/", StringComparison.Ordinal);

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

        static void EnsureFolderExists(string assetPath)
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

        /// <summary>Route an argument-guard failure through the house RunLog grammar (summary + RunLog +
        /// LogError), like the sibling transplant tools — never a bare trailer-less line.</summary>
        static string ArgFail(string label, string msg)
        {
            var data = new RunData { result = "FAIL", error = msg };
            data.Offender(msg);
            return Finish(data, label);
        }

        /// <summary>Fail an in-flight <see cref="RunData"/> (routing/write-guard stage — after arg/shader
        /// guards, so <c>instance</c>/<c>source</c> are already populated) through the same grammar.</summary>
        static string Fail(RunData data, string label, string msg)
        {
            data.result = "FAIL";
            data.error = msg;
            data.Offender(msg);
            return Finish(data, label);
        }

        /// <summary>Write the RunLog, build the one-line summary with the RunLog path folded onto its
        /// tail, log it at the right severity (PASS → Log, else LogError), and return the summary.</summary>
        static string Finish(RunData data, string label)
        {
            if (data.result == "FAIL" && data.offenders.Count == 0)
                data.Offender("unnamed failure: " + (data.error ?? "no error detail"));

            string path = WriteRunLog(data, label);
            string summary = Summary(data, label) + " | log=" + path;
            if (data.result == "PASS") Debug.Log(summary); else Debug.LogError(summary);
            return summary;
        }

        /// <summary>One-line PASS/FAIL summary: <c>[own-material] label: k1=v1, k2=v2 offenders=[...] => RESULT</c>.</summary>
        static string Summary(RunData data, string label)
        {
            var counts = new StringBuilder();
            for (int i = 0; i < data.counts.Count; i++)
            {
                if (i > 0) counts.Append(", ");
                counts.Append(data.counts[i].Key).Append('=').Append(data.counts[i].Value.ToString(CultureInfo.InvariantCulture));
            }

            string offenders = data.offenders.Count > 0 ? " offenders=[" + string.Join("; ", data.offenders) + "]" : "";
            string notes = data.notes.Count > 0 ? " notes=[" + string.Join("; ", data.notes) + "]" : "";
            string warnings = data.warnings.Count > 0 ? " warnings=[" + string.Join("; ", data.warnings) + "]" : "";
            string error = data.error != null ? " error=" + data.error : "";
            string whatIf = data.whatIf ? " (whatIf)" : "";

            return string.Format(CultureInfo.InvariantCulture,
                "[own-material]{0} {1}: {2}{3}{4}{5}{6} => {7}",
                whatIf, label, counts, offenders, notes, warnings, error, data.result);
        }

        // ── RunLog output ─────────────────────────────────────────────────────────────────────────────

        static string WriteRunLog(RunData data, string label)
        {
            Directory.CreateDirectory(TransplantCore.RunLogDir);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"kind\": \"own-material\",\n");
            sb.Append("  \"unityVersion\": ").Append(TransplantCore.Q(Application.unityVersion)).Append(",\n");
            sb.Append("  \"timestampUtc\": ").Append(TransplantCore.Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"whatIf\": ").Append(data.whatIf ? "true" : "false").Append(",\n");
            sb.Append("  \"instance\": ").Append(TransplantCore.Q(data.instance)).Append(",\n");
            sb.Append("  \"source\": ").Append(TransplantCore.Q(data.source)).Append(",\n");
            sb.Append("  \"result\": ").Append(TransplantCore.Q(data.result)).Append(",\n");
            sb.Append("  \"error\": ").Append(TransplantCore.Q(data.error)).Append(",\n");

            foreach (var kv in data.counts)
                sb.Append("  ").Append(TransplantCore.Q(kv.Key)).Append(": ").Append(kv.Value.ToString(CultureInfo.InvariantCulture)).Append(",\n");

            AppendStringArray(sb, "offenders", data.offenders);
            sb.Append(",\n");
            AppendStringArray(sb, "notes", data.notes);
            sb.Append(",\n");
            AppendStringArray(sb, "warnings", data.warnings);
            sb.Append(",\n");
            AppendSlotArray(sb, data.slots);
            sb.Append("\n}");

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = TransplantCore.RunLogDir + "/own-material_" + TransplantCore.Sanitize(label) + "_" + stamp + ".json";
            File.WriteAllText(path, sb.ToString());
            AssetDatabase.Refresh();
            return path;
        }

        static void AppendStringArray(StringBuilder sb, string key, List<string> items)
        {
            sb.Append("  ").Append(TransplantCore.Q(key)).Append(": [");
            for (int i = 0; i < items.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    ").Append(TransplantCore.Q(items[i]));
            }
            sb.Append(items.Count > 0 ? "\n  ]" : "]");
        }

        static void AppendSlotArray(StringBuilder sb, List<SlotRow> slots)
        {
            sb.Append("  \"slots\": [");
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    { \"slot\": ").Append(TransplantCore.Q(s.slot))
                  .Append(", \"requested\": ").Append(s.requested ? "true" : "false")
                  .Append(", \"disposition\": ").Append(TransplantCore.Q(s.disposition))
                  .Append(", \"sourcePath\": ").Append(TransplantCore.Q(s.sourcePath))
                  .Append(", \"ownedPath\": ").Append(TransplantCore.Q(s.ownedPath))
                  .Append(" }");
            }
            sb.Append(slots.Count > 0 ? "\n  ]" : "]");
        }

        // ── Data types ────────────────────────────────────────────────────────────────────────────────

        /// <summary>One row of the per-slot provenance table (<see cref="RunData.slots"/>). <c>ownedPath</c>
        /// is the owned texture's path for <c>forked</c>/<c>already-owned</c> slots; null otherwise. Every
        /// row reports <c>vendor-ref</c>/<c>ownedPath = null</c> until Task 3 wires the fork planner
        /// (<see cref="OwnMaterial.BuildVendorRefSlots"/>).</summary>
        struct SlotRow
        {
            public string slot;
            public bool requested;
            public string disposition;
            public string sourcePath;
            public string ownedPath;
        }

        /// <summary>Mutable accumulator for one Run, serialized by <see cref="WriteRunLog"/> and
        /// summarized by <see cref="Summary"/>. Mirrors <c>TransplantRunLog</c>'s envelope conventions
        /// (ordered <see cref="counts"/>, offenders/notes/warnings) plus the bespoke <see cref="slots"/>
        /// structured array ConformRenderers-style RunLogs use for a per-row table.</summary>
        class RunData
        {
            public bool whatIf;
            public string instance;
            public string source;
            public string result = "PASS";
            public string error;
            public readonly List<KeyValuePair<string, long>> counts = new List<KeyValuePair<string, long>>();
            public readonly List<string> offenders = new List<string>();
            public readonly List<string> notes = new List<string>();
            public readonly List<string> warnings = new List<string>();
            public readonly List<SlotRow> slots = new List<SlotRow>();

            public void Count(string name, long value) => counts.Add(new KeyValuePair<string, long>(name, value));
            public void Offender(string msg) { if (!string.IsNullOrEmpty(msg)) offenders.Add(msg); }
            public void Note(string msg) { if (!string.IsNullOrEmpty(msg)) notes.Add(msg); }
            public void Warning(string msg) { if (!string.IsNullOrEmpty(msg)) warnings.Add(msg); }
        }
    }
}
