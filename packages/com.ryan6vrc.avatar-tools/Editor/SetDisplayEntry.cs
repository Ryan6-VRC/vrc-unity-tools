using System;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Configures one entry of a <c>Ryan6VRC/Overlay/DebugDisplay</c> material: the label string, the
    /// number of decimals, the right-pad, the palette slot, and the value source. Speaks strings and
    /// names — the caller never sees the packed floats, which are a private handle
    /// (<see cref="DisplayGlyphs"/> owns the arithmetic).
    ///
    /// <para><b>Why a door and not just the material inspector.</b> The ShaderGUI is the operator's
    /// front; this is the agent's, and the string→6-bit→float pack is exactly the deterministic,
    /// error-prone slice a script does better than tokens. It is also the only path that can be driven
    /// unattended, so a display's configuration is reproducible from a script rather than from a
    /// remembered sequence of clicks.</para>
    ///
    /// <para><b>The material must be an owned copy.</b> The entry ships <c>WorldCoords.mat</c> as a
    /// TEMPLATE under <c>Packages/com.ryan6vrc.patterns/…</c>, and <c>LAYOUT.md</c> makes
    /// <c>Packages/</c> read-only to our tooling — so a <c>Packages/</c> target is REFUSED, naming
    /// copy-into-<c>Assets/</c> as the fix. This is a distinct reason from the per-instance rule below
    /// and both bite: a consumer who satisfies one can still violate the other.</para>
    ///
    /// <para><b>Each display instance needs its own material.</b> Animating a material property hits
    /// every renderer sharing that material, so two displays on one material show the same number. This
    /// tool cannot detect that (it sees one asset, not the scene), so it is the entry README's install
    /// check — noted here only so the omission is deliberate rather than forgotten.</para>
    ///
    /// PASS = the entry was written (or already matched, in which case nothing is dirtied).
    /// <c>whatIf</c> previews the exact same plan without writing — the pack is fully computable ahead of
    /// the write, so preview and execute cannot disagree. FAILs named on: a missing/non-material asset,
    /// a material on another shader, an out-of-range entry index, a <c>Packages/</c> target, a label the
    /// charset cannot carry, or an out-of-range format field. RunLog kind <c>set-display-entry</c>.
    /// </summary>
    [AgentTool]
    public static class SetDisplayEntry
    {
        /// <param name="materialPath">Asset path of the display material. Must be under
        /// <c>Assets/</c> — a <c>Packages/</c> path is refused rather than silently copied.</param>
        /// <param name="entry">Entry index, 0..<see cref="DisplayGlyphs.MaxEntries"/>-1. This is the
        /// grid cell in row-major order from the top-left.</param>
        /// <param name="label">Up to 12 chars from the charset, uppercased if needed (reported when it
        /// happens). The <c>:</c> separator is part of the label, not injected by the shader — author
        /// <c>"POS X:"</c>, not <c>"POS X"</c>.</param>
        /// <param name="decimals">Digits after the point, 0..5.</param>
        /// <param name="rpad">Right-pad in glyph advances. <c>max_decimals - decimals</c> aligns decimal
        /// points across a grid column; 0 parks a value at the cell's right edge.</param>
        /// <param name="palette">Text colour slot, 0..3, indexing the material's <c>_Palette_N</c>.</param>
        /// <param name="source">Value source name, e.g. <c>Animator</c>, <c>WorldX</c>,
        /// <c>ObserverFps</c>, <c>CameraDistance</c>. Case-insensitive.
        /// <c>Animator</c> (the default) means the entry's own <c>_E{i}_Value</c> float, which is what a
        /// clip curve drives.</param>
        /// <param name="whatIf">Preview only. Reports the identical plan; writes nothing.</param>
        public static string Run(string materialPath, int entry, string label,
                                 int decimals = 0, int rpad = 0, int palette = 0,
                                 string source = "Animator", bool whatIf = false)
        {
            string logLabel = "set-display-entry_" +
                              TransplantCore.Sanitize(RunLogFormat.Leaf(materialPath ?? "null-path"))
                              + "_E" + entry;
            var log = new RunLog("set-display-entry")
            {
                whatIf   = whatIf,
                instance = materialPath,
            };

            // ── Target resolution ───────────────────────────────────────────────────────────────────
            if (string.IsNullOrEmpty(materialPath))
            {
                log.result = "FAIL";
                log.error  = "materialPath is null or empty";
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }

            // Pure argument checks precede any policy or I/O, so a caller's mistake is named for what it
            // is rather than masked by whatever the target turns out to be.
            if (entry < 0 || entry >= DisplayGlyphs.MaxEntries)
            {
                log.result = "FAIL";
                log.error  = "entry " + entry + " out of range 0.." + (DisplayGlyphs.MaxEntries - 1);
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }

            if (!TransplantCore.IsWritableAsset(materialPath))
            {
                log.result = "FAIL";
                log.error  = "target is read-only: '" + materialPath +
                             "' is under Packages/ or Assets/Vendor/. The shipped preset is a TEMPLATE — " +
                             "copy it into Assets/ and configure the copy (LAYOUT.md)";
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
            {
                log.result = "FAIL";
                log.error  = "no Material at '" + materialPath + "'";
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }

            if (mat.shader == null || mat.shader.name != DisplayGlyphs.ShaderName)
            {
                log.result = "FAIL";
                log.error  = "material shader is '" +
                             (mat.shader == null ? "<null>" : mat.shader.name) +
                             "', expected '" + DisplayGlyphs.ShaderName + "'";
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }

            // ── Encode (all refusals land before any write) ─────────────────────────────────────────
            DisplayGlyphs.ValueSource parsedSource;
            if (!TryParseSource(source, out parsedSource))
            {
                log.result = "FAIL";
                log.error  = "unknown source '" + source + "'; valid: " +
                             string.Join(", ", Enum.GetNames(typeof(DisplayGlyphs.ValueSource)));
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }

            Vector4 packedLabel;
            string encodeError, encodeNote;
            if (!DisplayGlyphs.TryEncodeLabel(label, out packedLabel, out encodeError, out encodeNote))
            {
                log.result = "FAIL";
                log.error  = encodeError;
                log.Offender(encodeError);
                return TransplantCore.Finish(log, logLabel);
            }
            log.Note(encodeNote);

            float packedFormat;
            string formatError;
            if (!DisplayGlyphs.TryPackFormat(decimals, palette, rpad, parsedSource,
                                            out packedFormat, out formatError))
            {
                log.result = "FAIL";
                log.error  = formatError;
                log.Offender(formatError);
                return TransplantCore.Finish(log, logLabel);
            }

            // ── Plan, then write ────────────────────────────────────────────────────────────────────
            string labelProp  = DisplayGlyphs.LabelProperty(entry);
            string formatProp = DisplayGlyphs.FormatProperty(entry);

            var currentLabel  = mat.HasProperty(labelProp)  ? mat.GetVector(labelProp) : Vector4.zero;
            var currentFormat = mat.HasProperty(formatProp) ? mat.GetFloat(formatProp) : 0f;
            bool changed = currentLabel != packedLabel || !Mathf.Approximately(currentFormat, packedFormat);

            log.Note("label=" + TransplantCore.Q(DisplayGlyphs.DecodeLabel(packedLabel)) +
                     " decimals=" + decimals + " rpad=" + rpad + " palette=" + palette +
                     " source=" + parsedSource);
            log.Note("binds at material." + DisplayGlyphs.ValueProperty(entry));

            // rpad's 4-bit range is wider than a narrow cell can use, and an over-padded value slides off
            // the left of its own cell and vanishes with NO on-screen diagnostic. Cell width is a property
            // of the material's grid, so the bound is checkable here rather than left to the inspector.
            if (mat.HasProperty("_Total_Width") && mat.HasProperty("_Grid_Columns"))
            {
                float cols = Mathf.Max(1f, mat.GetFloat("_Grid_Columns"));
                float cellAdv = mat.GetFloat("_Total_Width") / cols;
                int maxUsable = DisplayGlyphs.MaxUsableRpad(cellAdv);
                if (rpad > maxUsable)
                    log.Warning("rpad " + rpad + " exceeds the usable max " + maxUsable +
                                " for a " + cellAdv.ToString("0.##") + "-advance cell — the value will be " +
                                "clipped off the left of its cell with no on-screen diagnostic");
            }

            log.Count("changed", changed ? 1 : 0);

            if (!whatIf && changed)
            {
                Undo.RecordObject(mat, "Set display entry");
                mat.SetVector(labelProp, packedLabel);
                mat.SetFloat(formatProp, packedFormat);
                EditorUtility.SetDirty(mat);
                AssetDatabase.SaveAssetIfDirty(mat);
            }

            log.result = "PASS";
            return TransplantCore.Finish(log, logLabel);
        }

        /// <summary>Case-insensitive source-name parse. Names, never wire IDs: the caller should not have
        /// to know that <c>ObserverFps</c> is 11, and an ID typo silently selects a different source.</summary>
        static bool TryParseSource(string name, out DisplayGlyphs.ValueSource source)
        {
            source = DisplayGlyphs.ValueSource.Animator;
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var candidate in Enum.GetNames(typeof(DisplayGlyphs.ValueSource)))
            {
                if (!string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) continue;
                source = (DisplayGlyphs.ValueSource)Enum.Parse(typeof(DisplayGlyphs.ValueSource), candidate);
                return true;
            }
            return false;
        }
    }
}
