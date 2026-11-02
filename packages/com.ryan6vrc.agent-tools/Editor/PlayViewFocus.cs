using System;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// On play entry, brings the Scene view to the front instead of leaving Unity on the Game view.
    ///
    /// Tab focus only: this changes which Editor tab is frontmost, not the Unity process's OS-window
    /// focus, so it leaves <c>EditorApplication.isFocused</c> alone and is no remedy for the NDMF/MA
    /// reactive preview's stale fit.
    ///
    /// Unsupported: Game view "Maximize On Play" hides the Scene view under the maximized Game view;
    /// this hook can't surface a hidden window in that mode.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayViewFocus
    {
        static PlayViewFocus()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Act after entry: Unity focuses the Game view as part of entering play, so an earlier switch
            // gets stomped. FocusWindowIfItsOpen enumerates live windows (dodging the domain-reload reset
            // of SceneView.lastActiveSceneView) and no-ops when no Scene view exists, batch mode included.
            if (state != PlayModeStateChange.EnteredPlayMode) return;

            try
            {
                EditorWindow.FocusWindowIfItsOpen<SceneView>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PlayViewFocus] could not focus the Scene view (harmless): " + e.Message);
            }
        }
    }
}
