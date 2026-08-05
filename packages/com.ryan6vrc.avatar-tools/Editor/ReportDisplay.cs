using System.Text;
using UnityEditor;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Decodes a <c>Ryan6VRC/Overlay/DebugDisplay</c> material back into readable text: the grid shape,
    /// then per populated entry the label, decimals, rpad, palette, value source, and the current value.
    ///
    /// <para><b>Why this is owed rather than optional.</b> A packed label is the bare number
    /// <c>262143</c> in any raw inspector or <c>AgentInspector</c> dump, so without a decode door an
    /// agent can neither observe a display before changing it (rule 1) nor verify it after
    /// (rule 7). The packed representation is a private handle; this is the only door that renders it
    /// back into the substrate's own names.</para>
    ///
    /// <para>Read-only and verdict-free — it reports facts and never judges whether a configuration is
    /// sensible. Two things it does flag as FACTS, because both are invisible in a screenshot and both
    /// print a plausible-looking wrong display: an entry whose grid cell does not exist (its
    /// <c>_Grid_Columns × _Grid_Rows</c> product places it past the visible cells, or past the 12-entry
    /// shader cap), and an rpad past what the cell width can carry.</para>
    ///
    /// Always <c>=&gt; OK</c> on a readable material; <c>=&gt; ERROR</c> on bad input (a missing asset or the
    /// wrong shader) — never a content verdict, per the <c>Report</c> grammar in <c>unity-tools.md</c>.
    /// RunLog kind <c>report-display</c>.
    /// </summary>
    [AgentTool]
    public static class ReportDisplay
    {
        /// <param name="materialPath">Asset path of the display material. Readable anywhere, including
        /// under <c>Packages/</c> — reading a shipped template is legitimate.</param>
        public static string Run(string materialPath)
        {
            string logLabel = "report-display_" +
                              TransplantCore.Sanitize(RunLogFormat.Leaf(materialPath ?? "null-path"));
            var log = new RunLog("report-display") { instance = materialPath };

            var mat = string.IsNullOrEmpty(materialPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
            {
                log.result = "ERROR";
                log.error  = "no Material at '" + (materialPath ?? "<null>") + "'";
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }
            if (mat.shader == null || mat.shader.name != DisplayGlyphs.ShaderName)
            {
                log.result = "ERROR";
                log.error  = "material shader is '" +
                             (mat.shader == null ? "<null>" : mat.shader.name) +
                             "', expected '" + DisplayGlyphs.ShaderName + "'";
                log.Offender(log.error);
                return TransplantCore.Finish(log, logLabel);
            }

            int cols = mat.HasProperty("_Grid_Columns") ? Mathf.RoundToInt(mat.GetFloat("_Grid_Columns")) : 1;
            int rows = mat.HasProperty("_Grid_Rows")    ? Mathf.RoundToInt(mat.GetFloat("_Grid_Rows"))    : 1;
            float totalWidthAdv = mat.HasProperty("_Total_Width") ? mat.GetFloat("_Total_Width") : 0f;
            float cellAdv = cols > 0 ? totalWidthAdv / cols : 0f;
            int visibleCells = Mathf.Min(cols * rows, DisplayGlyphs.MaxEntries);

            log.Note("grid=" + cols + "x" + rows + " cells=" + visibleCells +
                     " cell_width=" + cellAdv.ToString("0.##") + "adv");
            if (cols * rows > DisplayGlyphs.MaxEntries)
                log.Note("grid product " + (cols * rows) + " exceeds the " + DisplayGlyphs.MaxEntries +
                         "-entry shader cap; entries past " + (DisplayGlyphs.MaxEntries - 1) + " never render");

            var body = new StringBuilder();
            body.AppendLine("# report-display " + materialPath);
            body.AppendLine();
            body.AppendLine("grid: " + cols + " x " + rows +
                            "  total_width: " + totalWidthAdv.ToString("0.##") + " advances" +
                            "  cell_width: " + cellAdv.ToString("0.##") + " advances");
            body.AppendLine();
            body.AppendLine("| entry | cell | label | dec | rpad | pal | source | value |");
            body.AppendLine("|---|---|---|---|---|---|---|---|");

            int populated = 0, unreachable = 0, overPadded = 0;

            for (int i = 0; i < DisplayGlyphs.MaxEntries; i++)
            {
                string labelProp  = DisplayGlyphs.LabelProperty(i);
                string formatProp = DisplayGlyphs.FormatProperty(i);
                string valueProp  = DisplayGlyphs.ValueProperty(i);
                if (!mat.HasProperty(labelProp)) continue;

                var packedLabel = mat.GetVector(labelProp);
                string text = DisplayGlyphs.DecodeLabel(packedLabel);

                int decimals, palette, rpad;
                DisplayGlyphs.ValueSource source;
                DisplayGlyphs.UnpackFormat(mat.HasProperty(formatProp) ? mat.GetFloat(formatProp) : 0f,
                                           out decimals, out palette, out rpad, out source);
                float value = mat.HasProperty(valueProp) ? mat.GetFloat(valueProp) : 0f;

                // A nonzero value alone makes an entry live: with a blank label and default format it
                // still DRAWS the number (the value region fills, the label falls through to space), so
                // omitting the row would hide a rendering entry from the door whose whole justification
                // is observe-before-change.
                bool blank = text.Length == 0 && source == DisplayGlyphs.ValueSource.Animator
                             && decimals == 0 && rpad == 0 && palette == 0 && value == 0f;
                if (blank) continue;
                populated++;

                string cell;
                if (i < visibleCells)
                {
                    cell = "r" + (i / Mathf.Max(1, cols)) + "c" + (i % Mathf.Max(1, cols));
                }
                else
                {
                    cell = "UNREACHABLE";
                    unreachable++;
                }

                int maxUsable = DisplayGlyphs.MaxUsableRpad(cellAdv);
                string rpadCell = rpad.ToString();
                if (cellAdv > 0f && rpad > maxUsable) { rpadCell = rpad + " (>" + maxUsable + ")"; overPadded++; }

                // '|' is a legal charset glyph, so an unescaped label breaks the markdown row the
                // reading agent parses.
                string labelCell = TransplantCore.Q(text).Replace("|", "\\|");
                // A computed source's _E{i}_Value is dormant; printing it under a "value" header would
                // read as the current number. Say where the number comes from instead.
                string valueCell = source == DisplayGlyphs.ValueSource.Animator
                    ? value.ToString("0.#####")
                    : "(computed at render)";
                body.AppendLine("| " + i + " | " + cell + " | " + labelCell + " | " +
                                decimals + " | " + rpadCell + " | " + palette + " | " +
                                source + " | " + valueCell + " |");
            }

            log.Count("populated", populated);
            log.Count("unreachable", unreachable);
            log.Count("over_padded", overPadded);

            if (unreachable > 0)
                log.Note(unreachable + " configured entr" + (unreachable == 1 ? "y" : "ies") +
                         " sit past the visible cells and never render");
            if (overPadded > 0)
                log.Note(overPadded + " entr" + (overPadded == 1 ? "y" : "ies") +
                         " carry an rpad wider than the cell, clipping the value off its left edge");

            log.result = "OK";
            // Report tools own their own tail (as ReportPackage does): the markdown table IS the artifact,
            // so it goes through the body-agnostic writer, which appends the "| log=<path>" trailer. The
            // FAIL paths above use Finish instead — a refusal has no table to render.
            string summary = RunLogFormat.WriteRunLog(RunLogFormat.RunLogDir, logLabel,
                                                      TransplantCore.Summary(log, logLabel),
                                                      body.ToString(), ".md");
            Debug.Log(summary);
            return summary;
        }
    }
}
