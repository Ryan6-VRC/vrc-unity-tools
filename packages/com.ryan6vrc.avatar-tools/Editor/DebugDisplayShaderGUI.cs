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
        static readonly string[] LayoutProps =
            { "_Display_Mode", "_Grid_Columns", "_Grid_Rows", "_Total_Width", "_Text_Depth_Offset" };
        static readonly string[] TextProps =
            { "_MSDF_Glyph_Atlas", "_Font_Size", "_Font_Scale_Relative",
              "_Palette_0", "_Palette_1", "_Palette_2", "_Palette_3" };
        static readonly string[] ShellToggleProps = { "_Shell_Enabled" };
        static readonly string[] ShellProps =
            { "_Shell_ReflectionCube", "_Shell_Reflection_Color", "_Shell_Reflection_Strength",
              "_Shell_Reflection_Smoothness", "_Shell_Reflection_BlurMaxMip" };
        static readonly string[] RimProps =
            { "_Shell_Rim_Color", "_Shell_Rim_Strength", "_Shell_Rim_Border", "_Shell_Rim_Blur",
              "_Shell_Rim_FresnelPower", "_Shell_Rim_VRParallaxStrength" };

        static readonly HashSet<string> AllSectionProps = new HashSet<string>(
            LayoutProps.Concat(TextProps).Concat(ShellToggleProps).Concat(ShellProps).Concat(RimProps));

        // Static, not per-instance: Unity builds a fresh ShaderGUI on every selection change, so instance
        // fields collapse every section each time you click away and back. lilToon keeps the equivalent
        // state in a ScriptableSingleton; static survives everything short of a domain reload, which is
        // enough for a view preference.
        static bool _showLayout = true;
        static bool _showText = true;
        static bool _showEntries = true;
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

            if (Section("Layout", "Display mode and the grid the entries land in", ref _showLayout))
                using (Body())
                {
                    DrawNamed(materialEditor, properties, LayoutProps);
                    EditorGUILayout.LabelField(
                        cols + " x " + rows + " = " + (cols * rows) + " cells, " +
                        cellAdv.ToString("0.##") + " advances per cell (" +
                        DisplayGlyphs.MaxLabelChars + " label + " + DisplayGlyphs.ValueGlyphs +
                        " value = " + (DisplayGlyphs.MaxLabelChars + DisplayGlyphs.ValueGlyphs) +
                        " needed to avoid overlap)", EditorStyles.miniLabel);
                }

            if (Section("Text", "Glyph size, the atlas it is rasterized from, and the four palettes",
                        ref _showText))
                using (Body())
                    DrawNamed(materialEditor, properties, TextProps);

            if (Section("Entries", "One row per shader entry; the collapsed row carries its label and source",
                        ref _showEntries))
                using (Body())
                {
                    if (GUILayout.Button("Auto-align decimal points (per grid column)"))
                        AutoAlign(mat, cols, rows, cellAdv);
                    for (int i = 0; i < DisplayGlyphs.MaxEntries; i++)
                        DrawEntry(mat, scan[i]);
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
                        EditorGUILayout.LabelField("Rim light", EditorStyles.miniBoldLabel);
                        DrawNamed(materialEditor, properties, RimProps);
                    }
                }

            if (Section("Rendering", "Unity's own per-material render settings", ref _showRendering))
                using (Body())
                {
                    materialEditor.RenderQueueField();
                    materialEditor.DoubleSidedGIField();
                    materialEditor.EnableInstancingField();
                }

            DrawUnclaimed(properties);
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
        /// One entry as a foldout whose collapsed header still carries the two fields you scan for —
        /// the label and the source — plus a fault icon, so a collapsed row can hide detail but not a
        /// problem. lilToon's texture-slot-in-the-header foldout is the pattern.
        /// </summary>
        void DrawEntry(Material mat, EntryState st)
        {
            if (!st.Exists) return;
            int i = st.Index;
            string valueProp = DisplayGlyphs.ValueProperty(i);

            if (st.Unreachable && !st.Configured)
            {
                // An unused slot outside the grid: one dim line, no controls, no fold.
                EditorGUILayout.LabelField("E" + i, "unused, outside the grid", EditorStyles.miniLabel);
                return;
            }

            int decimals = st.Decimals, palette = st.Palette, rpad = st.Rpad;
            var source = st.Source;

            // One row: fold arrow, grid position, label field, source popup, fault icon. Laid out in
            // explicit rects rather than a HorizontalScope so the label field takes the slack and the
            // popup keeps a fixed width no matter how narrow the inspector gets.
            var row = EditorGUILayout.GetControlRect();
            float iconW = (st.Error ?? st.Warning) != null ? 20f : 0f;
            const float SourceW = 104f;
            var foldRect = new Rect(row.x, row.y, 13f, row.height);
            var posRect = new Rect(row.x + 13f, row.y, 52f, row.height);
            float labelX = posRect.xMax + 2f;
            var labelRect = new Rect(labelX, row.y,
                                     Mathf.Max(40f, row.xMax - labelX - SourceW - iconW - 6f), row.height);
            var sourceRect = new Rect(labelRect.xMax + 3f, row.y, SourceW, row.height);
            var iconRect = new Rect(sourceRect.xMax + 3f, row.y, iconW, row.height);

            _showEntry[i] = EditorGUI.Foldout(foldRect, _showEntry[i], GUIContent.none);
            EditorGUI.LabelField(posRect,
                                 st.Unreachable ? "E" + i + " ·—" : "E" + i + " ·" + st.Row + "," + st.Col,
                                 EditorStyles.miniLabel);

            // Delayed, so an in-progress label is not encoded on every keystroke: TryEncodeLabel refuses
            // an out-of-charset character rather than mangling it, and a live field would log that refusal
            // per character typed on the way to a valid string.
            EditorGUI.BeginChangeCheck();
            string newText = EditorGUI.DelayedTextField(labelRect, st.Label);
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
            int newSource = EditorGUI.Popup(sourceRect, (int)source, SourceNames);
            if (EditorGUI.EndChangeCheck())
            {
                source = (DisplayGlyphs.ValueSource)newSource;
                WriteFormat(mat, i, decimals, palette, rpad, source);
            }

            if (iconW > 0f)
            {
                var icon = EditorGUIUtility.IconContent(
                    st.Error != null ? "console.erroricon.sml" : "console.warnicon.sml");
                EditorGUI.LabelField(iconRect, new GUIContent(icon.image, st.Error ?? st.Warning));
            }

            if (!_showEntry[i]) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                decimals = EditorGUILayout.IntSlider("Decimals", decimals, 0, DisplayGlyphs.MaxDecimals);
                rpad = EditorGUILayout.IntSlider("Right pad", rpad, 0, DisplayGlyphs.MaxRpad);
                palette = EditorGUILayout.IntSlider("Palette", palette, 0, 3);
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
                        EditorGUILayout.LabelField("Binding", "material." + valueProp,
                                                   EditorStyles.miniLabel);
                        if (GUILayout.Button("Copy", EditorStyles.miniButton, GUILayout.Width(46)))
                            EditorGUIUtility.systemCopyBuffer = "material." + valueProp;
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Value", "computed in-shader from " + source,
                                               EditorStyles.miniLabel);
                }

                if (st.Error != null) EditorGUILayout.HelpBox(st.Error, MessageType.Error);
                else if (st.Warning != null) EditorGUILayout.HelpBox(st.Warning, MessageType.Warning);
            }
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
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
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
        void DrawNamed(MaterialEditor editor, MaterialProperty[] properties, params string[] names)
        {
            foreach (var name in names)
            {
                var prop = properties.FirstOrDefault(p => p.name == name);
                if (prop == null)
                {
                    EditorGUILayout.HelpBox("shader has no property '" + name +
                                            "' — inspector and shader have drifted", MessageType.Error);
                    continue;
                }
                editor.ShaderProperty(prop, prop.displayName);
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
