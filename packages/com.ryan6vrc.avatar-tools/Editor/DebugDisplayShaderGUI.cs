using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Material inspector for <c>Ryan6VRC/Overlay/DebugDisplay</c>. Turns the packed label vectors and
    /// format bitfields back into an editable per-entry table, over a live ASCII preview of the grid.
    ///
    /// <para><b>The preview is the point.</b> Cell width, gutters, label truncation and rpad collisions
    /// are all arithmetic the shader resolves silently — a value that runs into its label just renders
    /// garbled, and a value pushed past its cell's left edge vanishes with no on-screen diagnostic at
    /// all. The preview reproduces the shader's own region rules (value region wins; a space in it falls
    /// through to the label) so those land here, before anyone looks at a mesh.</para>
    ///
    /// <para><b>This is the operator's door, not the agent's.</b> <see cref="SetDisplayEntry"/> and
    /// <see cref="ReportDisplay"/> are the scriptable path and neither depends on this class. If
    /// <c>avatar-tools</c> is absent the material falls back to Unity's default ShaderGUI: every property
    /// stays visible and editable (the label vectors are deliberately not <c>[HideInInspector]</c>), a
    /// label is merely unreadable as a packed integer, and Unity logs one "Could not create a custom UI"
    /// warning per inspect.</para>
    /// </summary>
    public class DebugDisplayShaderGUI : ShaderGUI
    {
        static readonly string[] SourceNames = Enum.GetNames(typeof(DisplayGlyphs.ValueSource));

        // Sample magnitudes for the preview, per source, so a computed entry's alignment and width are
        // visible before it is ever rendered. Chosen to be representative rather than pretty: the far
        // plane really is four digits, and FPS really is two.
        static float PreviewSample(DisplayGlyphs.ValueSource s, float animatorValue)
        {
            switch (s)
            {
                case DisplayGlyphs.ValueSource.Animator: return animatorValue;
                case DisplayGlyphs.ValueSource.WorldX: return -12.3456f;
                case DisplayGlyphs.ValueSource.WorldY: return 1.2345f;
                case DisplayGlyphs.ValueSource.WorldZ: return 98.7654f;
                case DisplayGlyphs.ValueSource.ScaleX:
                case DisplayGlyphs.ValueSource.ScaleY:
                case DisplayGlyphs.ValueSource.ScaleZ: return 1.0f;
                case DisplayGlyphs.ValueSource.Azimuth: return 271.5f;
                case DisplayGlyphs.ValueSource.Elevation: return -12.5f;
                case DisplayGlyphs.ValueSource.CameraDistance: return 3.75f;
                case DisplayGlyphs.ValueSource.CameraFarPlane: return 1000f;
                case DisplayGlyphs.ValueSource.ObserverFps: return 72f;
                case DisplayGlyphs.ValueSource.TimeSeconds: return 1234.5f;
                default: return 0f;
            }
        }

        bool _entriesExpanded = true;
        bool _previewExpanded = true;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            var mat = materialEditor.target as Material;
            if (mat == null) { base.OnGUI(materialEditor, properties); return; }

            // Multi-material editing of packed fields is not meaningful (each display needs its own
            // material anyway), so say so and fall back rather than writing one material's labels onto
            // several.
            if (materialEditor.targets != null && materialEditor.targets.Length > 1)
            {
                EditorGUILayout.HelpBox(
                    "Editing " + materialEditor.targets.Length + " materials at once. The per-entry table " +
                    "is hidden because packed labels cannot be meaningfully multi-edited — and each " +
                    "display instance needs its own material regardless, since animating a material " +
                    "property hits every renderer sharing it.", MessageType.Info);
                base.OnGUI(materialEditor, properties);
                return;
            }

            DrawNamed(materialEditor, properties, "Layout",
                      "_Display_Mode", "_Font_Size", "_Font_Scale_Relative", "_Total_Width",
                      "_Grid_Columns", "_Grid_Rows",
                      "_Text_Depth_Offset", "_MSDF_Glyph_Atlas");

            DrawNamed(materialEditor, properties, "Text palette",
                      "_Palette_0", "_Palette_1", "_Palette_2", "_Palette_3");

            int cols = Mathf.Max(1, Mathf.RoundToInt(GetFloat(mat, "_Grid_Columns", 1)));
            int rows = Mathf.Max(1, Mathf.RoundToInt(GetFloat(mat, "_Grid_Rows", 1)));
            float totalWidthAdv = GetFloat(mat, "_Total_Width", 24f);
            float cellAdv = totalWidthAdv / cols;
            int visible = Mathf.Min(cols * rows, DisplayGlyphs.MaxEntries);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Grid", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                cols + " x " + rows + " = " + (cols * rows) + " cells, " +
                cellAdv.ToString("0.##") + " advances per cell " +
                "(" + DisplayGlyphs.MaxLabelChars + " label + " + DisplayGlyphs.ValueGlyphs + " value = " +
                (DisplayGlyphs.MaxLabelChars + DisplayGlyphs.ValueGlyphs) + " needed to avoid overlap)",
                EditorStyles.miniLabel);

            if (cols * rows > DisplayGlyphs.MaxEntries)
                EditorGUILayout.HelpBox(
                    "Grid asks for " + (cols * rows) + " cells but the shader compiles " +
                    DisplayGlyphs.MaxEntries + " entries. Cells past entry " +
                    (DisplayGlyphs.MaxEntries - 1) + " render nothing.", MessageType.Warning);

            // Judged on ACTUAL label lengths, not the 12-char maximum: a compact display with two-char
            // labels is perfectly correct in a 13-advance cell, and warning on the theoretical worst case
            // would fire on the entry's own shipped preset. A real collision is reported per entry below.
            if (cellAdv < DisplayGlyphs.ValueGlyphs)
                EditorGUILayout.HelpBox(
                    "A cell is " + cellAdv.ToString("0.##") + " advances wide, narrower than the " +
                    DisplayGlyphs.ValueGlyphs + "-glyph value field itself. Values will be clipped at " +
                    "the cell's left edge.", MessageType.Warning);

            // ── Per-entry table ─────────────────────────────────────────────────────────────────────
            EditorGUILayout.Space();
            _entriesExpanded = EditorGUILayout.Foldout(_entriesExpanded, "Entries", true, EditorStyles.foldoutHeader);
            if (_entriesExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    if (GUILayout.Button("Auto-align decimal points (per grid column)"))
                        AutoAlign(mat, cols, rows, cellAdv);

                    for (int i = 0; i < DisplayGlyphs.MaxEntries; i++)
                        DrawEntry(mat, i, visible, cols, cellAdv);
                }
            }

            // ── Preview ─────────────────────────────────────────────────────────────────────────────
            EditorGUILayout.Space();
            _previewExpanded = EditorGUILayout.Foldout(_previewExpanded, "Preview", true, EditorStyles.foldoutHeader);
            if (_previewExpanded)
            {
                var mono = new GUIStyle(EditorStyles.textArea) { font = EditorStyles.miniFont, wordWrap = false };
                EditorGUILayout.LabelField(
                    "Reproduces the shader's region rules. Computed sources show a representative sample, " +
                    "not a live value.", EditorStyles.miniLabel);
                EditorGUILayout.TextArea(BuildPreview(mat, cols, rows, cellAdv, visible), mono);
            }

            // ── Shell ───────────────────────────────────────────────────────────────────────────────
            EditorGUILayout.Space();
            DrawNamed(materialEditor, properties, "Crystal shell",
                      "_Shell_Enabled", "_Shell_Reflection_Color", "_Shell_ReflectionCube",
                      "_Shell_Reflection_Strength", "_Shell_Reflection_Smoothness",
                      "_Shell_Reflection_BlurMaxMip");
            DrawNamed(materialEditor, properties, "Rim light",
                      "_Shell_Rim_Color", "_Shell_Rim_Strength", "_Shell_Rim_Border", "_Shell_Rim_Blur",
                      "_Shell_Rim_FresnelPower", "_Shell_Rim_VRParallaxStrength");

            EditorGUILayout.Space();
            materialEditor.RenderQueueField();
            materialEditor.DoubleSidedGIField();
            materialEditor.EnableInstancingField();
        }

        // ── Entry row ───────────────────────────────────────────────────────────────────────────────

        void DrawEntry(Material mat, int i, int visible, int cols, float cellAdv)
        {
            string labelProp = DisplayGlyphs.LabelProperty(i);
            string formatProp = DisplayGlyphs.FormatProperty(i);
            string valueProp = DisplayGlyphs.ValueProperty(i);
            if (!mat.HasProperty(labelProp)) return;

            var packed = mat.GetVector(labelProp);
            string text = DisplayGlyphs.DecodeLabel(packed);

            int decimals, palette, rpad;
            DisplayGlyphs.ValueSource source;
            DisplayGlyphs.UnpackFormat(mat.GetFloat(formatProp), out decimals, out palette, out rpad, out source);

            bool unreachable = i >= visible;
            bool configured = text.Length > 0 || source != DisplayGlyphs.ValueSource.Animator ||
                              decimals != 0 || rpad != 0 || palette != 0;

            // An unreachable-but-configured entry is worth surfacing; an unreachable-and-empty one is
            // just an unused slot and should stay quiet.
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string header = unreachable
                    ? "E" + i + "  (outside the " + visible + "-cell grid)"
                    : "E" + i + "  row " + (i / cols) + " col " + (i % cols);
                EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);

                if (unreachable && !configured)
                {
                    EditorGUILayout.LabelField("unused", EditorStyles.miniLabel);
                    return;
                }
                if (unreachable && configured)
                    EditorGUILayout.HelpBox("Configured but never rendered at this grid size.", MessageType.Warning);

                EditorGUI.BeginChangeCheck();
                string newText = EditorGUILayout.TextField("Label", text);
                if (EditorGUI.EndChangeCheck() && newText != text)
                {
                    Vector4 newPacked;
                    string error, note;
                    if (DisplayGlyphs.TryEncodeLabel(newText, out newPacked, out error, out note))
                    {
                        Undo.RecordObject(mat, "Edit display label");
                        mat.SetVector(labelProp, newPacked);
                        EditorUtility.SetDirty(mat);
                    }
                    else
                    {
                        // Refused, not silently mangled — the same contract as the write door.
                        Debug.LogWarning("[DebugDisplay] E" + i + " label rejected: " + error);
                    }
                }

                int maxUsable = DisplayGlyphs.MaxUsableRpad(cellAdv);
                EditorGUI.BeginChangeCheck();
                int newSource = EditorGUILayout.Popup("Source", (int)source, SourceNames);
                int newDecimals = EditorGUILayout.IntSlider("Decimals", decimals, 0, DisplayGlyphs.MaxDecimals);
                int newRpad = EditorGUILayout.IntSlider("Right pad", rpad, 0, DisplayGlyphs.MaxRpad);
                int newPalette = EditorGUILayout.IntSlider("Palette", palette, 0, 3);
                if (EditorGUI.EndChangeCheck())
                {
                    float newFmt;
                    string error;
                    if (DisplayGlyphs.TryPackFormat(newDecimals, newPalette, newRpad,
                                                    (DisplayGlyphs.ValueSource)newSource, out newFmt, out error))
                    {
                        Undo.RecordObject(mat, "Edit display format");
                        mat.SetFloat(formatProp, newFmt);
                        EditorUtility.SetDirty(mat);
                    }
                    else Debug.LogWarning("[DebugDisplay] E" + i + " format rejected: " + error);
                }

                if (newRpad > maxUsable)
                    EditorGUILayout.HelpBox(
                        "Right pad " + newRpad + " exceeds " + maxUsable + " for a " +
                        cellAdv.ToString("0.##") + "-advance cell. The value slides off the left of its " +
                        "cell and vanishes with no on-screen diagnostic.", MessageType.Error);

                // The real collision test: this label's actual length against where its value starts.
                // rpad knows nothing about the label, so the shader clips and the preview is what catches
                // it — this names it in words as well.
                int cellW = Mathf.Max(1, (int)Mathf.Floor(cellAdv));
                int valueWidth = DisplayGlyphs
                    .FormatValue(PreviewSample((DisplayGlyphs.ValueSource)newSource, 0f), newDecimals)
                    .TrimStart(' ').Length;
                int valueStart = cellW - newRpad - valueWidth;
                if (newText.Length > valueStart)
                    EditorGUILayout.HelpBox(
                        "Label is " + newText.Length + " chars but the value starts at advance " +
                        valueStart + ". The value wins its region, so the label's tail is overdrawn — " +
                        "see the preview.", MessageType.Warning);

                if ((DisplayGlyphs.ValueSource)newSource == DisplayGlyphs.ValueSource.Animator)
                {
                    EditorGUI.BeginChangeCheck();
                    float v = EditorGUILayout.FloatField("Value", mat.GetFloat(valueProp));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(mat, "Edit display value");
                        mat.SetFloat(valueProp, v);
                        EditorUtility.SetDirty(mat);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Binding", "material." + valueProp, EditorStyles.miniLabel);
                        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(46)))
                            EditorGUIUtility.systemCopyBuffer = "material." + valueProp;
                    }
                }
                else
                {
                    EditorGUILayout.LabelField(
                        "Value", "computed in-shader from " + (DisplayGlyphs.ValueSource)newSource,
                        EditorStyles.miniLabel);
                }
            }
        }

        // ── Auto-align ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets <c>rpad = max(decimals) - decimals</c> computed PER GRID COLUMN, because columns are
        /// independent vertical stacks — aligning against a grid-wide maximum would over-pad every column
        /// whose own deepest entry is shallower.
        /// </summary>
        void AutoAlign(Material mat, int cols, int rows, float cellAdv)
        {
            int visible = Mathf.Min(cols * rows, DisplayGlyphs.MaxEntries);
            Undo.RecordObject(mat, "Auto-align display decimals");

            for (int c = 0; c < cols; c++)
            {
                int maxDecimals = 0;
                for (int r = 0; r < rows; r++)
                {
                    int i = r * cols + c;
                    if (i >= visible) continue;
                    int d, p, rp; DisplayGlyphs.ValueSource s;
                    DisplayGlyphs.UnpackFormat(mat.GetFloat(DisplayGlyphs.FormatProperty(i)), out d, out p, out rp, out s);
                    maxDecimals = Mathf.Max(maxDecimals, d);
                }
                for (int r = 0; r < rows; r++)
                {
                    int i = r * cols + c;
                    if (i >= visible) continue;
                    int d, p, rp; DisplayGlyphs.ValueSource s;
                    DisplayGlyphs.UnpackFormat(mat.GetFloat(DisplayGlyphs.FormatProperty(i)), out d, out p, out rp, out s);
                    int want = Mathf.Min(maxDecimals - d, DisplayGlyphs.MaxUsableRpad(cellAdv));
                    float packed;
                    string error;
                    if (DisplayGlyphs.TryPackFormat(d, p, want, s, out packed, out error))
                        mat.SetFloat(DisplayGlyphs.FormatProperty(i), packed);
                }
            }
            EditorUtility.SetDirty(mat);
        }

        // ── ASCII preview ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the grid as text using the shader's own region arithmetic: the value right-aligns to
        /// <c>cell_w - rpad</c> and wins its region, with a space in it falling through to the label.
        /// Reproducing that ordering rather than approximating it is what makes a collision shown here
        /// the same collision the shader draws.
        /// </summary>
        string BuildPreview(Material mat, int cols, int rows, float cellAdv, int visible)
        {
            int cellW = Mathf.Max(1, (int)Mathf.Floor(cellAdv));
            var sb = new StringBuilder();
            sb.Append('+').Append(new string('-', cellW * cols + (cols - 1))).Append("+\n");

            for (int r = 0; r < rows; r++)
            {
                sb.Append('|');
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    if (c > 0) sb.Append(' ');
                    sb.Append(i < visible ? RenderCell(mat, i, cellW) : new string(' ', cellW));
                }
                sb.Append("|\n");
            }
            sb.Append('+').Append(new string('-', cellW * cols + (cols - 1))).Append("+\n");

            if (cols * rows > visible)
                sb.Append("\n" + (cols * rows - visible) + " cell(s) beyond the shader's " +
                          DisplayGlyphs.MaxEntries + "-entry cap render nothing.\n");
            return sb.ToString();
        }

        string RenderCell(Material mat, int i, int cellW)
        {
            string labelProp = DisplayGlyphs.LabelProperty(i);
            if (!mat.HasProperty(labelProp)) return new string(' ', cellW);

            string label = DisplayGlyphs.DecodeLabel(mat.GetVector(labelProp));
            int decimals, palette, rpad;
            DisplayGlyphs.ValueSource source;
            DisplayGlyphs.UnpackFormat(mat.GetFloat(DisplayGlyphs.FormatProperty(i)),
                                      out decimals, out palette, out rpad, out source);

            float animatorValue = mat.HasProperty(DisplayGlyphs.ValueProperty(i))
                ? mat.GetFloat(DisplayGlyphs.ValueProperty(i)) : 0f;
            // Formatted through the shader's OWN arithmetic, not ToString("F<n>"). A separate
            // implementation here would be a third copy that can disagree with what gets drawn — and it
            // did: it showed an over-wide negative in full while the shader dropped the sign, so the
            // preview reported the very case it exists to catch as correct.
            string value = DisplayGlyphs.FormatValue(PreviewSample(source, animatorValue), decimals)
                                        .TrimStart(' ');

            var cell = new char[cellW];
            for (int k = 0; k < cellW; k++) cell[k] = ' ';

            // Label occupies the leftmost MaxLabelChars advances.
            for (int k = 0; k < label.Length && k < DisplayGlyphs.MaxLabelChars && k < cellW; k++)
                cell[k] = label[k];

            // Value right-aligns to (cellW - rpad), and overwrites whatever the label put there — the
            // shader's value-region-wins ordering.
            int valueRight = cellW - rpad;
            int valueStart = valueRight - value.Length;
            for (int k = 0; k < value.Length; k++)
            {
                int pos = valueStart + k;
                if (pos >= 0 && pos < cellW) cell[pos] = value[k];
            }
            return new string(cell);
        }

        // ── Property helpers ────────────────────────────────────────────────────────────────────────

        static float GetFloat(Material mat, string name, float fallback)
            => mat.HasProperty(name) ? mat.GetFloat(name) : fallback;

        void DrawNamed(MaterialEditor editor, MaterialProperty[] properties, string header, params string[] names)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
            foreach (var name in names)
            {
                var prop = properties.FirstOrDefault(p => p.name == name);
                // A missing property means the shader and this GUI have drifted. Say which, rather than
                // silently omitting a control — that is the failure the gate assert also guards.
                if (prop == null)
                {
                    EditorGUILayout.HelpBox("shader has no property '" + name +
                                            "' — inspector and shader have drifted", MessageType.Error);
                    continue;
                }
                editor.ShaderProperty(prop, prop.displayName);
            }
        }
    }
}
