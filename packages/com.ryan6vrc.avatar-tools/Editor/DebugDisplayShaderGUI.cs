using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Material inspector for <c>Ryan6VRC/Overlay/DebugDisplay</c>. Turns the packed label vectors and
    /// format bitfields back into an editable per-entry table, grouped into collapsible sections.
    ///
    /// <para><b>Diagnostics never hide behind a fold.</b> Cell width, gutters, label truncation and rpad
    /// collisions are all arithmetic the shader resolves silently — a value that runs into its label just
    /// renders garbled, and a value pushed past its cell's left edge vanishes with no on-screen diagnostic
    /// at all. So every problem is reported twice: as a box on the offending entry, and in the summary at
    /// the very top of the inspector, which is outside every section and names the entry. Collapsing a
    /// section can hide a control, never a fault.</para>
    ///
    /// <para><b>This is the operator's door, not the agent's.</b> <see cref="SetDisplayEntry"/> and
    /// <see cref="ReportDisplay"/> are the scriptable path and neither depends on this class. If
    /// <c>avatar-tools</c> is absent the material falls back to Unity's default ShaderGUI: every property
    /// stays visible and editable (the label vectors are deliberately not <c>[HideInInspector]</c>), a
    /// label is merely unreadable as a packed integer, and Unity logs one "Could not create a custom UI"
    /// warning per inspect.</para>
    ///
    /// <para><b>Layout borrows lilToon's shape, not its code.</b> The section header bar, the boxed body,
    /// and a collapsed row that still shows its own key control are all patterns from
    /// <c>lilEditorGUI.Foldout</c> / <c>DrawSimpleFoldout</c>. They are reimplemented here in a few lines
    /// against built-in styles: <c>avatar-tools</c> referencing <c>jp.lilxyzw.liltoon</c> to draw a
    /// foldout would put a shader package in the dependency graph of an entry whose only dependency is an
    /// MA <c>BoneProxy</c>.</para>
    /// </summary>
    public class DebugDisplayShaderGUI : ShaderGUI
    {
        static readonly string[] SourceNames = Enum.GetNames(typeof(DisplayGlyphs.ValueSource));

        // Entry properties are drawn by DrawEntry out of the packed representation, not as raw properties,
        // so the coverage check below has to know to skip them.
        static readonly Regex EntryProperty = new Regex(@"^_E\d+_(Label|Format|Value)$");

        // Every section's property list, declared once. DrawUnclaimed checks the shader against the union
        // of these rather than against what actually got drawn: a collapsed section draws nothing, and a
        // coverage check keyed on drawing would call all of its properties orphans.
        static readonly string[] LayoutProps = { "_Grid_Columns", "_Grid_Rows" };
        static readonly string[] TextMetricProps =
            { "_MSDF_Glyph_Atlas", "_Font_Size", "_Font_Scale_Relative", "_Text_Depth_Offset" };
        static readonly string[] PaletteProps =
            { "_Palette_0", "_Palette_1", "_Palette_2", "_Palette_3" };
        // Relabelled here, not in the shader. "Color N" is what the operator wants to read, but the shader
        // display name is also what the FALLBACK GUI shows: with avatar-tools absent there are no section
        // headings, so four bare "Color N" fields would sit in a flat list beside the shell's own
        // "Color / Mask" and "Color / Alpha" with nothing saying which is text. "Palette N" is
        // self-describing without a heading, so the shader keeps it and the override lives here.
        static readonly string[] PaletteLabels = { "Color 0", "Color 1", "Color 2", "Color 3" };
        // Drawn by hand rather than through ShaderProperty — the mode as a button bar, the width per
        // column — but still shader properties this GUI is responsible for covering.
        static readonly string[] HandDrawnProps = { "_Display_Mode", "_Total_Width" };
        static readonly string[] ShellToggleProps = { "_Shell_Enabled" };
        static readonly string[] ShellProps =
            { "_Shell_ReflectionCube", "_Shell_Reflection_Color", "_Shell_Reflection_Strength",
              "_Shell_Reflection_Smoothness", "_Shell_Reflection_BlurMaxMip" };
        static readonly string[] RimProps =
            { "_Shell_Rim_Color", "_Shell_Rim_Strength", "_Shell_Rim_Border", "_Shell_Rim_Blur",
              "_Shell_Rim_FresnelPower", "_Shell_Rim_VRParallaxStrength" };

        static readonly HashSet<string> AllSectionProps = new HashSet<string>(
            LayoutProps.Concat(TextMetricProps).Concat(PaletteProps).Concat(HandDrawnProps)
                       .Concat(ShellToggleProps).Concat(ShellProps).Concat(RimProps));

        // The three display modes, in the order the shader's [KeywordEnum] declares them — the float value
        // IS the index, so this array's order is a wire contract, not a presentation choice.
        static readonly string[] ModeNames = { "Billboard", "Object", "UV" };
        static readonly string[] ModeKeywords =
            { "_DISPLAY_MODE_BILLBOARD", "_DISPLAY_MODE_OBJECT", "_DISPLAY_MODE_UV" };

        // Static, not per-instance: Unity builds a fresh ShaderGUI on every selection change, so instance
        // fields collapse every section each time you click away and back. lilToon keeps the equivalent
        // state in a ScriptableSingleton; static survives everything short of a domain reload, which is
        // enough for a view preference.
        static bool _showLayout = true;
        static bool _showText = true;
        static bool _showShell;
        static bool _showRendering;
        static readonly bool[] _showEntry = new bool[DisplayGlyphs.MaxEntries];

        static GUIStyle _headerStyle;

        // Sample magnitudes per source, so a computed entry's alignment and width are checkable before it
        // is ever rendered. Chosen to be representative rather than pretty: the far plane really is four
        // digits, and FPS really is two.
        static float SampleValue(DisplayGlyphs.ValueSource s, float animatorValue)
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

            EnsureStyles();

            int cols = Mathf.Max(1, Mathf.RoundToInt(GetFloat(mat, "_Grid_Columns", 1)));
            int rows = Mathf.Max(1, Mathf.RoundToInt(GetFloat(mat, "_Grid_Rows", 1)));
            float cellAdv = GetFloat(mat, "_Total_Width", 24f) / cols;
            int visible = Mathf.Min(cols * rows, DisplayGlyphs.MaxEntries);
            var scan = Scan(mat, cols, rows, cellAdv, visible);

            DrawSummary(scan, cols, rows, cellAdv);
            DrawDisplayModeBar(mat);

            if (Section("Layout", "The grid the entries land in, and how wide a column is", ref _showLayout))
                using (Body())
                {
                    DrawNamed(materialEditor, properties, LayoutProps);
                    DrawColumnWidth(mat, cols, cellAdv);
                }

            if (Section("Text", "Glyph size and depth, the atlas they are rasterized from, and the palettes",
                        ref _showText))
                using (Body())
                {
                    DrawNamed(materialEditor, properties, TextMetricProps);
                    Line();
                    DrawNamed(materialEditor, properties, PaletteProps, PaletteLabels);
                }

            if (Section("Crystal shell", "The reflective outer pass, and the rim light inside it",
                        ref _showShell))
                using (Body())
                {
                    DrawNamed(materialEditor, properties, ShellToggleProps);
                    // The rim light lives in the same pass behind the same _SHELL_ON keyword, so with the
                    // shell off neither group does anything. Nesting says that; two sibling sections
                    // implied the rim was independently live.
                    if (GetFloat(mat, "_Shell_Enabled", 1f) != 0f)
                    {
                        DrawNamed(materialEditor, properties, ShellProps);
                        Line();
                        EditorGUILayout.LabelField("Rim light", EditorStyles.boldLabel);
                        DrawNamed(materialEditor, properties, RimProps);
                    }
                }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Entries", EditorStyles.boldLabel);
            if (GUILayout.Button("Auto-align decimal points (per grid column)"))
                AutoAlign(mat, cols, rows, cellAdv);

            // Each entry is its own accordion at the top level. One outer fold over all twelve meant
            // reaching any entry cost two clicks and shutting the group hid every label at once.
            int hidden = 0, missing = 0;
            for (int i = 0; i < DisplayGlyphs.MaxEntries; i++)
            {
                if (!scan[i].Exists) missing++;
                else if (!DrawEntry(mat, scan[i])) hidden++;
            }

            // Counted apart from the hidden slots, and reported in DrawNamed's voice. Folding the two
            // together told an operator to "add grid columns or rows to reach them" about entries the
            // shader does not declare, which growing the grid can never reach — a soothing lie in the one
            // place nothing else objects: the gate's asset check is deliberately entry-agnostic and never
            // looks at MaxEntries.
            if (missing > 0)
                EditorGUILayout.HelpBox(
                    "The shader declares " + (DisplayGlyphs.MaxEntries - missing) + " of the " +
                    DisplayGlyphs.MaxEntries + " entries DisplayGlyphs defines — inspector and shader " +
                    "have drifted.", MessageType.Error);

            // A slot outside the grid with nothing configured is not a control, it is an absence — so it
            // is hidden rather than drawn as a dim row. Counted rather than silent: the slots still exist,
            // and growing the grid is what brings them back. Short enough not to be cut at any inspector
            // width; the tooltip carries the why.
            if (hidden > 0)
                EditorGUILayout.LabelField(new GUIContent(
                    hidden == 1 ? "1 unused slot hidden" : hidden + " unused slots hidden",
                    "Entries outside the " + visible + "-cell grid with nothing configured. Add grid " +
                    "columns or rows to reach them."));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
            if (Section("Rendering", "Unity's own per-material render settings", ref _showRendering))
                using (Body())
                {
                    materialEditor.RenderQueueField();
                    materialEditor.DoubleSidedGIField();
                    materialEditor.EnableInstancingField();
                }

            DrawUnclaimed(properties);
        }

        // ── Hand-drawn controls ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The mode as a button bar at the top of the inspector, where lilToon puts its editor-mode row.
        /// Three mutually exclusive modes read as three buttons; as a popup they read as a list to open.
        /// </summary>
        static void DrawDisplayModeBar(Material mat)
        {
            EditorGUILayout.LabelField("Display Mode", EditorStyles.boldLabel);

            // Guarded like DrawColumnWidth: a hand-drawn control skips DrawNamed, so it gets no
            // shader-lacks-this-property error box unless it raises its own.
            if (!mat.HasProperty("_Display_Mode"))
            {
                EditorGUILayout.HelpBox("shader has no property '_Display_Mode' — inspector and shader " +
                                        "have drifted", MessageType.Error);
                return;
            }

            int cur = Mathf.Clamp(Mathf.RoundToInt(GetFloat(mat, "_Display_Mode", 0f)), 0, ModeNames.Length - 1);
            DrawModeKeywordMismatch(mat, cur);

            EditorGUI.BeginChangeCheck();
            int next = GUILayout.Toolbar(cur, ModeNames, GUILayout.Height(22f));
            // Written even when next == cur: re-picking the shown mode is how an operator repairs a
            // mismatch without reaching for the Fix button, and a no-op guard would make that click do
            // nothing on exactly the material that needs it.
            if (EditorGUI.EndChangeCheck()) WriteMode(mat, next);
            EditorGUILayout.Space();
        }

        /// <summary>
        /// The entry's palette slot as four clickable swatches showing the palette's own colours. An
        /// IntSlider 0..3 named the index without showing what it selects — the whole question an author
        /// has here is "which colour", and four numbers cannot answer it.
        ///
        /// <para>Labelled <b>Color</b> for the operator; the underlying field is the format bitfield's
        /// <c>palette</c>, which is what <see cref="SetDisplayEntry"/>, the README and the shader all call
        /// it. The tooltip carries that name so the two vocabularies stay connected.</para>
        /// </summary>
        static int DrawColorSwatches(Material mat, int palette)
        {
            var rect = EditorGUILayout.GetControlRect();
            var field = EditorGUI.PrefixLabel(rect, new GUIContent(
                "Color",
                "Which of the four text palettes this entry draws in — the format bitfield's palette " +
                "index, 0-3, as SetDisplayEntry and the README name it."));

            const float Gap = 3f;
            float w = Mathf.Min(46f, (field.width - Gap * 3f) / 4f);
            int picked = palette;

            for (int p = 0; p < 4; p++)
            {
                var swatch = new Rect(field.x + p * (w + Gap), field.y + 1f, w, field.height - 2f);

                // The palettes are [HDR], so a component can exceed 1 and a raw DrawRect of it reads as
                // flat white. Clamped for display only; the shader still gets the authored intensity.
                var c = mat.HasProperty("_Palette_" + p) ? mat.GetColor("_Palette_" + p) : Color.grey;
                var shown = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);

                if (p == palette)
                {
                    // Selection reads as a frame around the swatch rather than a tint of it, so the colour
                    // shown stays the colour the entry will draw in.
                    EditorGUI.DrawRect(new Rect(swatch.x - 2f, swatch.y - 2f, swatch.width + 4f, swatch.height + 4f),
                                       EditorGUIUtility.isProSkin ? Color.white : Color.black);
                }
                EditorGUI.DrawRect(swatch, shown);

                var e = Event.current;
                if (e.type == EventType.MouseDown && e.button == 0 && swatch.Contains(e.mousePosition))
                {
                    // Only a real move sets GUI.changed. A hand-drawn control does not set it on its own
                    // and the caller's BeginChangeCheck is what commits the format word, so without it a
                    // click paints a selection that is never written — but setting it unconditionally
                    // would push an undo step and dirty the material for re-clicking the current colour.
                    if (p != palette) { picked = p; GUI.changed = true; }
                    e.Use();
                }
                EditorGUIUtility.AddCursorRect(swatch, MouseCursor.Link);
            }
            return picked;
        }

        /// <summary>
        /// Reports a material whose <c>_Display_Mode</c> float and <c>_DISPLAY_MODE_*</c> keyword disagree,
        /// and offers to repair it. The float is what the bar reads, the keyword is what the shader
        /// branches on, so a mismatch renders one mode while the bar shows another — the exact silent
        /// wrongness this inspector exists to refuse.
        ///
        /// <para>The bar's own write keeps the two in step, so this is about materials that arrive already
        /// split: <c>new Material(shader)</c> enables no keyword at all, so a scripted
        /// <c>SetFloat("_Display_Mode", 2)</c> leaves <c>multi_compile</c>'s first variant (Billboard) in
        /// force while the float says UV. Pasted material properties and applied presets can do the same.
        /// Detected, never repaired behind the operator's back: a write during <c>OnGUI</c> would dirty a
        /// material just for being looked at.</para>
        /// </summary>
        static void DrawModeKeywordMismatch(Material mat, int cur)
        {
            int enabled = -1, enabledCount = 0;
            for (int k = 0; k < ModeKeywords.Length; k++)
                if (mat.IsKeywordEnabled(ModeKeywords[k])) { enabled = k; enabledCount++; }

            // Exactly one keyword, matching the float, is the only correct state.
            if (enabledCount == 1 && enabled == cur) return;

            string keywordSays = enabledCount == 0
                ? "no mode keyword is enabled, so the shader falls back to " + ModeNames[0]
                : enabledCount > 1
                    ? enabledCount + " mode keywords are enabled at once"
                    : "the keyword says " + ModeNames[enabled];

            EditorGUILayout.HelpBox(
                "Display mode is inconsistent: the float says " + ModeNames[cur] + " but " + keywordSays +
                ". The shader branches on the keyword, so what renders is not what this bar shows. " +
                "Re-pick the mode below, or press Fix.", MessageType.Error);

            if (GUILayout.Button("Fix — set the keyword to " + ModeNames[cur]))
                WriteMode(mat, cur);
        }

        /// <summary>
        /// The float and the keywords, written together. <c>[KeywordEnum]</c> is what normally keeps them
        /// in step and only <see cref="MaterialEditor.ShaderProperty"/> honours it, so every hand-drawn
        /// path has to come through here.
        /// </summary>
        static void WriteMode(Material mat, int mode)
        {
            Undo.RecordObject(mat, "Change display mode");
            mat.SetFloat("_Display_Mode", mode);
            for (int k = 0; k < ModeKeywords.Length; k++)
            {
                if (k == mode) mat.EnableKeyword(ModeKeywords[k]);
                else mat.DisableKeyword(ModeKeywords[k]);
            }
            EditorUtility.SetDirty(mat);
        }

        /// <summary>
        /// Column width, which is what an author reasons in, over a material that stores the total across
        /// all columns because that is what the layout math wants. One multiply each way; the alternative
        /// was a summary line restating the division, which said the same thing without being editable.
        /// </summary>
        static void DrawColumnWidth(Material mat, int cols, float cellAdv)
        {
            if (!mat.HasProperty("_Total_Width"))
            {
                EditorGUILayout.HelpBox("shader has no property '_Total_Width' — inspector and shader " +
                                        "have drifted", MessageType.Error);
                return;
            }

            int needed = DisplayGlyphs.MaxLabelChars + DisplayGlyphs.ValueGlyphs;
            var label = new GUIContent(
                "Column width",
                "Glyph advances per grid column. A column needs " + DisplayGlyphs.MaxLabelChars +
                " (label) + " + DisplayGlyphs.ValueGlyphs + " (value) = " + needed + " for a full-width " +
                "label to stay clear of its value. Stored on the material as the total across all " +
                cols + " column(s).");

            EditorGUI.BeginChangeCheck();
            // Ranged so the product stays inside the shader's own Range(10, 200) at any column count.
            float newCell = EditorGUILayout.Slider(label, cellAdv, Mathf.Max(1f, 10f / cols), 200f / cols);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(mat, "Edit column width");
                mat.SetFloat("_Total_Width", newCell * cols);
                EditorUtility.SetDirty(mat);
            }
        }

        // ── Diagnostics ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Everything the rest of the GUI needs to know about one entry, read once per repaint.</summary>
        struct EntryState
        {
            public int Index;
            public bool Exists;
            public int Row, Col;
            public string Label;
            public int Decimals, Palette, Rpad;
            public DisplayGlyphs.ValueSource Source;
            public bool Unreachable, Configured;
            /// <summary>The value disappears entirely. Non-null means an error box.</summary>
            public string Error;
            /// <summary>Something renders wrong but visibly. Non-null means a warning box.</summary>
            public string Warning;
        }

        static EntryState[] Scan(Material mat, int cols, int rows, float cellAdv, int visible)
        {
            int maxUsable = DisplayGlyphs.MaxUsableRpad(cellAdv);
            int cellW = Mathf.Max(1, (int)Mathf.Floor(cellAdv));
            var states = new EntryState[DisplayGlyphs.MaxEntries];

            for (int i = 0; i < states.Length; i++)
            {
                var st = new EntryState { Index = i, Row = i / cols, Col = i % cols };
                string labelProp = DisplayGlyphs.LabelProperty(i);
                st.Exists = mat.HasProperty(labelProp);
                if (!st.Exists) { states[i] = st; continue; }

                st.Label = DisplayGlyphs.DecodeLabel(mat.GetVector(labelProp));
                DisplayGlyphs.UnpackFormat(mat.GetFloat(DisplayGlyphs.FormatProperty(i)),
                                           out st.Decimals, out st.Palette, out st.Rpad, out st.Source);

                st.Unreachable = i >= visible;
                st.Configured = st.Label.Length > 0 || st.Source != DisplayGlyphs.ValueSource.Animator ||
                                st.Decimals != 0 || st.Rpad != 0 || st.Palette != 0;

                if (st.Rpad > maxUsable)
                    st.Error = "Right pad " + st.Rpad + " exceeds " + maxUsable + " for a " +
                               cellAdv.ToString("0.##") + "-advance cell. The value slides off the left " +
                               "of its cell and vanishes with no on-screen diagnostic.";

                // The real collision test: this label's actual length against where its value starts.
                // rpad knows nothing about the label, so the shader draws the value over the label's tail
                // and nothing objects — this is what objects.
                if (!st.Unreachable && st.Error == null)
                {
                    int valueWidth = DisplayGlyphs
                        .FormatValue(SampleValue(st.Source, GetFloat(mat, DisplayGlyphs.ValueProperty(i), 0f)),
                                     st.Decimals)
                        .TrimStart(' ').Length;
                    int valueStart = cellW - st.Rpad - valueWidth;
                    if (st.Label.Length > valueStart)
                        st.Warning = "Label is " + st.Label.Length + " chars but the value starts at " +
                                     "advance " + valueStart + ". The value wins its region, so the " +
                                     "label's tail is overdrawn.";
                }

                // An unreachable-but-configured entry is worth surfacing; an unreachable-and-empty one is
                // just an unused slot and should stay quiet.
                if (st.Unreachable && st.Configured && st.Warning == null)
                    st.Warning = "Configured but never rendered at this grid size (" + visible + " cells).";

                states[i] = st;
            }
            return states;
        }

        /// <summary>
        /// The one block that is never inside a fold. With no ASCII preview to catch an overflow on sight,
        /// this is what catches it: a fault on a collapsed entry inside a collapsed section still shows up
        /// here, named.
        /// </summary>
        static void DrawSummary(EntryState[] scan, int cols, int rows, float cellAdv)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (cols * rows > DisplayGlyphs.MaxEntries)
                warnings.Add("Grid asks for " + (cols * rows) + " cells but the shader compiles " +
                             DisplayGlyphs.MaxEntries + " entries. Cells past entry " +
                             (DisplayGlyphs.MaxEntries - 1) + " render nothing.");

            // Judged on ACTUAL label lengths per entry above, not the 12-char maximum — a compact display
            // with two-char labels is perfectly correct in a 13-advance cell. This one is unconditional
            // because a cell narrower than the value field clips every entry in it.
            if (cellAdv < DisplayGlyphs.ValueGlyphs)
                warnings.Add("A cell is " + cellAdv.ToString("0.##") + " advances wide, narrower than " +
                             "the " + DisplayGlyphs.ValueGlyphs + "-glyph value field itself. Values " +
                             "will be clipped at the cell's left edge.");

            foreach (var st in scan)
            {
                if (st.Error != null) errors.Add("E" + st.Index + ": " + st.Error);
                else if (st.Warning != null) warnings.Add("E" + st.Index + ": " + st.Warning);
            }

            if (errors.Count > 0)
                EditorGUILayout.HelpBox(string.Join("\n\n", errors), MessageType.Error);
            if (warnings.Count > 0)
                EditorGUILayout.HelpBox(string.Join("\n\n", warnings), MessageType.Warning);
        }

        // ── Entry row ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One entry as an ordinary fold — the same header bar every section uses — with every control
        /// inside it. Nothing lives in the header but the entry's name, its label and whether it is at
        /// fault, so the twelve of them read as one list.
        /// </summary>
        /// <returns><c>false</c> if the entry was skipped as an unused slot, so the caller can count it.</returns>
        bool DrawEntry(Material mat, EntryState st)
        {
            // The caller filters !Exists out before this point, so a false return here means one thing:
            // an unused slot.
            int i = st.Index;

            // Outside the grid AND unconfigured: nothing to show and nothing at risk. A configured entry
            // outside the grid still draws — it carries a warning, and hiding it would hide the warning.
            if (st.Unreachable && !st.Configured) return false;

            string valueProp = DisplayGlyphs.ValueProperty(i);
            int decimals = st.Decimals, palette = st.Palette, rpad = st.Rpad;
            var source = st.Source;

            // The label rides in the title because it is the only way to tell twelve otherwise identical
            // folds apart; the fault mark rides there because a shut fold must not hide one.
            string title = "E" + i;
            if (st.Label.Length > 0) title += "   " + st.Label;
            if (st.Error != null) title += "   (!)";
            else if (st.Warning != null) title += "   (?)";

            string tip = st.Error ?? st.Warning ??
                         (st.Unreachable ? "Outside the grid" : "Row " + st.Row + ", column " + st.Col);

            if (!Section(title, tip, ref _showEntry[i])) return true;

            using (Body())
            {
                // Delayed, so an in-progress label is not encoded on every keystroke: TryEncodeLabel
                // refuses an out-of-charset character rather than mangling it, and a live field would log
                // that refusal per character typed on the way to a valid string.
                EditorGUI.BeginChangeCheck();
                string newText = EditorGUILayout.DelayedTextField("Label", st.Label);
                if (EditorGUI.EndChangeCheck() && newText != st.Label)
                {
                    Vector4 newPacked;
                    string error, note;
                    if (DisplayGlyphs.TryEncodeLabel(newText, out newPacked, out error, out note))
                    {
                        Undo.RecordObject(mat, "Edit display label");
                        mat.SetVector(DisplayGlyphs.LabelProperty(i), newPacked);
                        EditorUtility.SetDirty(mat);
                    }
                    else
                    {
                        // Refused, not silently mangled — the same contract as the write door.
                        Debug.LogWarning("[DebugDisplay] E" + i + " label rejected: " + error);
                    }
                }

                EditorGUI.BeginChangeCheck();
                source = (DisplayGlyphs.ValueSource)EditorGUILayout.Popup("Source", (int)source, SourceNames);
                decimals = EditorGUILayout.IntSlider("Decimals", decimals, 0, DisplayGlyphs.MaxDecimals);
                rpad = EditorGUILayout.IntSlider("Right pad", rpad, 0, DisplayGlyphs.MaxRpad);
                palette = DrawColorSwatches(mat, palette);
                if (EditorGUI.EndChangeCheck())
                    WriteFormat(mat, i, decimals, palette, rpad, source);

                if (source == DisplayGlyphs.ValueSource.Animator)
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
                        EditorGUILayout.LabelField("Binding", "material." + valueProp);
                        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(46)))
                            EditorGUIUtility.systemCopyBuffer = "material." + valueProp;
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Value", "computed in-shader from " + source);
                }

                if (st.Error != null) EditorGUILayout.HelpBox(st.Error, MessageType.Error);
                else if (st.Warning != null) EditorGUILayout.HelpBox(st.Warning, MessageType.Warning);
            }
            return true;
        }

        void WriteFormat(Material mat, int i, int decimals, int palette, int rpad,
                         DisplayGlyphs.ValueSource source)
        {
            float packed;
            string error;
            if (DisplayGlyphs.TryPackFormat(decimals, palette, rpad, source, out packed, out error))
            {
                Undo.RecordObject(mat, "Edit display format");
                mat.SetFloat(DisplayGlyphs.FormatProperty(i), packed);
                EditorUtility.SetDirty(mat);
            }
            else Debug.LogWarning("[DebugDisplay] E" + i + " format rejected: " + error);
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

        // ── Section chrome (our own, shaped after lilEditorGUI) ─────────────────────────────────────

        static void EnsureStyles()
        {
            // Built during OnGUI, never in a field initializer: a GUIStyle derived from a named built-in
            // style needs GUI.skin, which does not exist at static-init time.
            if (_headerStyle != null) return;
            _headerStyle = new GUIStyle("ShurikenModuleTitle")
            {
                font = EditorStyles.label.font,
                fontSize = EditorStyles.label.fontSize,
                fontStyle = FontStyle.Bold,
                border = new RectOffset(15, 7, 4, 4),
                contentOffset = new Vector2(20f, -2f),
                fixedHeight = 22
            };
        }

        /// <summary>A full-width header bar whose whole surface toggles the section, as lilToon's is.</summary>
        static bool Section(string title, string tooltip, ref bool display)
        {
            var rect = GUILayoutUtility.GetRect(16f, 22f, _headerStyle);
            rect.width += 8f;
            rect.x -= 8f;
            GUI.Box(rect, new GUIContent(title, tooltip), _headerStyle);

            var e = Event.current;
            if (e.type == EventType.Repaint)
                EditorStyles.foldout.Draw(new Rect(rect.x + 4f, rect.y + 3f, 13f, 13f),
                                          false, false, display, false);
            // Left button only. The bar is deliberately 8px wider than the content rect, so swallowing
            // every button would suppress the context menu over a band beyond the section itself.
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                display = !display;
                e.Use();
            }
            return display;
        }

        static EditorGUILayout.VerticalScope Body()
            => new EditorGUILayout.VerticalScope(EditorStyles.helpBox);

        static void Line()
            => EditorGUI.DrawRect(EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(false, 1)),
                                  new Color(0.5f, 0.5f, 0.5f, 0.4f));

        // ── Property helpers ────────────────────────────────────────────────────────────────────────

        static float GetFloat(Material mat, string name, float fallback)
            => mat.HasProperty(name) ? mat.GetFloat(name) : fallback;

        /// <summary>
        /// Draws the named properties in the order given. A name the shader lacks gets an explicit error
        /// box rather than being silently omitted, so shader/GUI drift is loud — one forgotten control is
        /// an invisible control, and <c>_Font_Scale_Relative</c> governs whether text renders at all.
        /// </summary>
        void DrawNamed(MaterialEditor editor, MaterialProperty[] properties, string[] names,
                       string[] labels = null)
        {
            for (int n = 0; n < names.Length; n++)
            {
                var name = names[n];
                var prop = properties.FirstOrDefault(p => p.name == name);
                if (prop == null)
                {
                    EditorGUILayout.HelpBox("shader has no property '" + name +
                                            "' — inspector and shader have drifted", MessageType.Error);
                    continue;
                }
                editor.ShaderProperty(prop, labels != null ? labels[n] : prop.displayName);
            }
        }

        /// <summary>
        /// The other direction of the same drift check: a property the shader has and no section claims
        /// would otherwise be silently unreachable through this GUI, which is exactly the failure
        /// <see cref="DrawNamed"/>'s error box exists to prevent. Entry properties are excluded because
        /// they are edited through their packed form, and <c>[HideInInspector]</c> ones because being
        /// undrawn is what that flag asks for.
        /// </summary>
        static void DrawUnclaimed(MaterialProperty[] properties)
        {
            var orphans = properties
                .Where(p => (p.flags & MaterialProperty.PropFlags.HideInInspector) == 0)
                .Where(p => !EntryProperty.IsMatch(p.name))
                .Where(p => !AllSectionProps.Contains(p.name))
                .Select(p => p.name)
                .ToArray();

            if (orphans.Length > 0)
                EditorGUILayout.HelpBox(
                    "The shader declares " + orphans.Length + " property(s) no section of this " +
                    "inspector draws, so they are unreachable here: " + string.Join(", ", orphans) +
                    ". Add them to a section in DebugDisplayShaderGUI.", MessageType.Error);
        }
    }
}
