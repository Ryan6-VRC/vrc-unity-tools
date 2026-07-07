using System;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// On play entry, brings the Scene view to the front instead of leaving Unity on the Game view —
    /// nicer resolution and a movable camera for avatar work. A pure UI nudge: deliberately separate
    /// from <see cref="PlayGate"/> (which gates/cancels entry and must never mutate the scene), so each
    /// hook does one thing. Not <c>[AgentTool]</c>-marked — no callable surface, no TOOLS.md row.
    ///
    /// Tab focus only: this changes which Editor tab is frontmost, NOT the Unity process's OS-window
    /// focus. It is orthogonal to <c>EditorApplication.isFocused</c> and the NDMF/MA reactive-preview
    /// stale-fit behavior — it neither helps nor harms it. Not a stale-preview remedy.
    ///
    /// Unsupported: Game view "Maximize On Play" hides the Scene view under the maximized Game view;
    /// this hook can't surface a hidden window in that mode.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayViewFocus
    {
        // Trivial ctor — subscribe only. Matches PlayGate: a throw here would silently disable the hook
        // for the whole domain, so all work happens lazily in the handler under its own try/catch.
        static PlayViewFocus()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Act AFTER entry: Unity focuses the Game view as part of entering play, so an earlier switch
            // gets stomped. FocusWindowIfItsOpen enumerates live windows (dodging the domain-reload reset
            // of SceneView.lastActiveSceneView) and no-ops when no Scene view exists — including batch mode,
            // so no explicit Application.isBatchMode check is needed.
            if (state != PlayModeStateChange.EnteredPlayMode) return;

            try
            {
                EditorWindow.FocusWindowIfItsOpen<SceneView>();
            }
            catch (Exception e)
            {
                // Fail harmless: a focus failure must never disrupt play. LogWarning tier + [PlayViewFocus]
                // prefix match the family console grammar.
                Debug.LogWarning("[PlayViewFocus] could not focus the Scene view (harmless): " + e.Message);
            }
        }
    }
}
