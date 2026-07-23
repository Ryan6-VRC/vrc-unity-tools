using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Editor-coupled door for the play-entry gate: on every interactive play entry it runs the pure
    /// <see cref="PlayGateCore"/> against the active scene and — on failure — cancels entry and prints a
    /// prescriptive FAIL naming each offender and its fix, plus a 2-line Scene-view overlay and a one-shot
    /// human-clickable override. A pure refusal: it never mutates the scene.
    ///
    /// Not <c>[AgentTool]</c>-marked — the hook and menu are not agent-invoked, so the callable surface is
    /// unchanged (no TOOLS.md row). The reactive cancel already hands the agent the verdict cheaply
    /// (instant, before the multi-minute build), so no <c>Check()</c> pre-check tool exists.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayGate
    {
        // SessionState (not EditorPrefs): session-local, auto-clears on Editor restart so the gate re-arms
        // for the next agent session — dodging the machine-global-EditorPref cross-project leak. Namespaced
        // to avoid cross-tool collision.
        private const string OverrideKey = "Ryan6Vrc.AgentTools.PlayGate.AllowNextEntry";
        private const string OverrideMenu = "Tools/Atelier/Allow Next Play Entry";

        // Trivial ctor — does exactly one thing: subscribe. A throw here would silently disable the gate for
        // the whole domain with no cancel path; all reflection/evaluation happens lazily in the handler
        // (under its try/catch).
        static PlayGate()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Batch/CI has no human, Scene view, or interactive session to protect, and no one to click the
            // override — gating there would cancel a legitimate headless play-entry. No-op in batch mode.
            if (Application.isBatchMode) return;
            if (state != PlayModeStateChange.ExitingEditMode) return;

            try
            {
                // One-shot override: consume (clear) and let play proceed with no evaluation.
                if (SessionState.GetBool(OverrideKey, false))
                {
                    SessionState.SetBool(OverrideKey, false);
                    return;
                }

                var result = PlayGateCore.Evaluate(SceneManager.GetActiveScene());
                if (result.Pass) return; // play proceeds silently

                // Cancel entry (spike-confirmed clean: no flicker into play, single ExitingEditMode fire),
                // then emit console FIRST, overlay LAST.
                EditorApplication.isPlaying = false;
                EmitConsole(result);
                EmitOverlay(result);
            }
            catch (Exception e)
            {
                // Fail closed: never let a throw fall through into a multi-minute build on an unverified
                // scene. Route through EmitConsole so the exception FAIL names its fix + the override path
                // (the error is the interface), same as an ordinary offender block.
                EditorApplication.isPlaying = false;
                EmitConsole(new PlayGateCore.PlayGateResult
                {
                    Pass = false,
                    Offenders = new List<PlayGateCore.Offender>
                    {
                        new PlayGateCore.Offender
                        {
                            Tag = "PlayGate",
                            Message = "play gate threw while evaluating — blocking to be safe:\n" + e,
                            Fix = "fix the exception above; or override this one entry via the menu below",
                        },
                    },
                });
            }
        }

        // The addressable-specifics surface the agent reads via read_console. Family grammar: prefix
        // [PlayGate], verdict token => FAIL (matching [ReportGimmick] … => OK). Tone model: the run_tests
        // deny reason — a refusal that names its own fix.
        //
        // read_console returns ONLY the first line of a log entry's message (a multi-line body is silently
        // dropped — see PlayGateCore.ConsoleSummaryLine), so LINE 1 is the agent's whole channel and must be
        // self-sufficient: every offender, its fix, and the override path, all on it. The pretty per-offender
        // block below is the human's expanded Console view (Unity collapses the entry to line 1 anyway) — a
        // second rendering of the same offender list, not a second source of truth.
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

        // Glanceable, branded, auto-fading. In its OWN try/catch so a Scene-view failure can never suppress
        // the already-emitted console diagnostic; null-guarded (no Scene view open → console already fired).
        // The 'Atelier' brand lives ONLY here (the human surface) — the deliberate exception to the machine
        // [PlayGate] grammar. Two lines, no emoji.
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

        // One-shot, universal (no human-vs-agent distinction, no persistent disable) — consumed by the next
        // ExitingEditMode. The gate is a shared safety net, not an agent leash.
        [MenuItem(OverrideMenu)]
        private static void AllowNextEntry()
        {
            SessionState.SetBool(OverrideKey, true);
            Debug.Log("[PlayGate] one-shot override armed — the next play entry skips the gate (menu " + OverrideMenu + ").");
        }
    }
}
