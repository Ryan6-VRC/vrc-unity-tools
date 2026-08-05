using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Material inspector for <c>Ryan6VRC/Overlay/DebugOverlay</c> — the depth-derived surface probes
    /// (wireframe and normals), which are one shader because they differ by eleven lines of fragment code
    /// over an identical depth-reconstruction prologue and an identical shell pass.
    ///
    /// <para>Everything this shader declares beyond its mode is shared shell/rim, so this class is little
    /// more than the mode declaration plus the one diagnostic the family cannot check for itself: both modes
    /// are inert without a scene depth texture, and inert reads as flat, not as an error.</para>
    /// </summary>
    public class DebugOverlayShaderGUI : CrystalShellShaderGUI
    {
        // NOT _Overlay_Mode, which is what upstream's own Overlay/Wireframe calls a different axis
        // entirely (mesh / fullscreen / billboard sphere / trail) — an axis this shader also has, as
        // _Overlay_Fullscreen. Two meanings under one name in one family is the collision worth avoiding;
        // upstream shipping as an installable VPM package makes it a live one.
        protected override string ModeProperty { get { return "_Probe_Mode"; } }
        protected override string ModeLabel { get { return "Probe Mode"; } }
        protected override string[] ModeNames { get { return new[] { "Wireframe", "Normal" }; } }

        // Unity derives a [KeywordEnum]'s keywords as the property name uppercased plus the value, so this
        // array is not a naming choice — it mirrors what the shader compiles.
        protected override string[] ModeKeywords
        {
            get { return new[] { "_PROBE_MODE_WIREFRAME", "_PROBE_MODE_NORMAL" }; }
        }

        static bool _showOverlay = true;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            var mat = materialEditor.target as Material;
            if (mat == null) { base.OnGUI(materialEditor, properties); return; }

            EnsureStyles();

            EditorGUILayout.HelpBox(
                "Both modes read the scene depth texture, which nothing on an avatar populates on its own — " +
                "the entry's sample prefab ships a shadow-casting directional light for exactly that. " +
                "Without one the probe renders a flat fill rather than erroring.", MessageType.Info);

            DrawModeBar(mat);

            if (Section("Screenspace overlay", "Whether the probe takes over the whole frame instead of " +
                        "rendering on its mesh", ref _showOverlay))
                using (Body())
                    DrawOverlaySection(materialEditor, properties, mat);

            DrawShellSection(materialEditor, properties, mat);
            DrawRenderingSection(materialEditor);
            DrawUnclaimed(properties);
        }
    }
}
