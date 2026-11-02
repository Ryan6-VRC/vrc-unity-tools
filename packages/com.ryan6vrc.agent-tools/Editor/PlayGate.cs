using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// The play-entry gate's Editor hook: runs <see cref="PlayGateCore"/> on every interactive play entry and
    /// on failure cancels entry. A pure refusal, it never mutates the scene.
    /// Contract: docs/verify.md §The play-entry gate.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayGate
    {
        // SessionState, not EditorPrefs: an unconsumed override clears on Editor restart, so the gate re-arms
        // for the next session.
        private const string OverrideKey = "Ryan6Vrc.AgentTools.PlayGate.AllowNextEntry";
        private const string OverrideMenu = "Tools/Atelier/Allow Next Play Entry";

        // Trivial ctor, subscribe only: a throw here would silently disable the gate for the whole domain with
        // no cancel path, so all reflection and evaluation happen in the handler under its try/catch.
        static PlayGate()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (Application.isBatchMode) return;
            if (state != PlayModeStateChange.ExitingEditMode) return;

            try
            {
                if (SessionState.GetBool(OverrideKey, false))
                {
                    SessionState.SetBool(OverrideKey, false);
                    return;
                }

                // EvaluateEntry, not Evaluate: PlayGateCore.Evaluate's doc says why.
                var result = PlayGateCore.EvaluateEntry(SceneManager.GetActiveScene());
                if (result.Pass) return;

                // Setting isPlaying false here cancels entry; observed clean, with no flicker into play and a
                // single ExitingEditMode fire.
                EditorApplication.isPlaying = false;
                EmitConsole(result);
                EmitOverlay(result);
            }
            catch (Exception e)
            {
                // Fail closed: a throw must not fall through into a multi-minute build on an unverified scene.
                EditorApplication.isPlaying = false;
                EmitConsole(new PlayGateCore.PlayGateResult
                {
                    Pass = false,
                    Scope = PlayGateCore.PlayGateScope.Entry,
                    Offenders = new List<PlayGateCore.Offender>
                    {
                        new PlayGateCore.Offender
                        {
                            Tag = "PlayGate",
                            Message = "play gate threw while evaluating — blocking to be safe:\n" + e,
                            Fix = "fix the exception above; or override this one entry via the menu on this block's first line",
                        },
                    },
                });
            }
        }

        // Family grammar: prefix [PlayGate], verdict token => FAIL (matching [ReportGimmick] … => OK). The
        // summary line and the per-offender block below render one offender list.
        private static void EmitConsole(PlayGateCore.PlayGateResult result)
        {
            var sb = new StringBuilder();
            sb.Append("[PlayGate] play entry blocked => FAIL — ")
              .Append(PlayGateCore.ConsoleSummaryLine(result.Offenders))
              .Append(" | override: menu ").Append(OverrideMenu);
            foreach (var o in result.Offenders)
                sb.Append("\n  - [").Append(o.Tag).Append("] ").Append(o.Message)
                  .Append("\n      fix: ").Append(o.Fix);
            Debug.LogError(sb.ToString());
        }

        // Own try/catch so an overlay failure stays a warning instead of reaching the handler's catch as a
        // false "gate threw" FAIL. This human surface deliberately skips the [PlayGate] grammar.
        private static void EmitOverlay(PlayGateCore.PlayGateResult result)
        {
            try
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv == null) return;
                var content = new GUIContent(
                    "Atelier Play Gate — entry blocked\n" + PlayGateCore.OverlaySummaryLine(result.Offenders));
                sv.ShowNotification(content, 4.0);
                sv.Repaint();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PlayGate] overlay render failed (console diagnostic already emitted): " + e.Message);
            }
        }

        [MenuItem(OverrideMenu)]
        private static void AllowNextEntry()
        {
            SessionState.SetBool(OverrideKey, true);
            Debug.Log("[PlayGate] one-shot override armed — the next play entry skips the gate (menu " + OverrideMenu + ").");
        }
    }
}
