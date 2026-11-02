using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// UI-free evaluation of the play-entry preconditions, which only inspects and never mutates the scene.
    /// <see cref="PlayGate"/> runs it on <c>ExitingEditMode</c>. Contract: docs/verify.md §The play-entry gate.
    ///
    /// Enumeration is scoped to the passed scene, never the global <c>FindObjectsOfType</c>, which spans
    /// additively-loaded scenes and DontDestroyOnLoad and would false-positive "More than 1 avatar enabled".
    /// Non-SDK hazards are detected by <c>GetType().FullName</c> reflection, with no asmdef dependency.
    ///
    /// Degradation: a hazard type that is absent (package not installed) skips its rule silently; a hazard
    /// on a scene object whose needed field or feature cannot be read is present-but-unreadable and blocks
    /// with a named FAIL.
    /// </summary>
    public static class PlayGateCore
    {
        // The VRCFury Fix-Write-Defaults feature type. Re-pin if VRCFury renames it; CheckVrcFury blocks when
        // this no longer resolves in the loaded assembly.
        public const string FwdFeatureFullName = "VF.Model.Feature.FixWriteDefaults";
        // Feature matching compares this leaf to each feature's managedReferenceFullTypename leaf, while the
        // blind check resolves the full name. Both a full-name miss and a leaf mismatch block.
        public static readonly string FwdFeatureLeaf =
            FwdFeatureFullName.Substring(FwdFeatureFullName.LastIndexOf('.') + 1);

        // The emulator's own type and field names live in EmulatorBinding, which its canary asserts against
        // the installed package; don't re-spell them here.
        private const string VrcFuryComponentFullName = "VF.Model.VRCFury";
        private const string GestureManagerFullName   = "BlackStartX.GestureManager.GestureManager";

        /// <summary>The scope a <see cref="PlayGateResult"/> was produced in. <c>Play = 0</c> is deliberate:
        /// it makes <c>Entry</c> unreachable by struct default, so a result that forgot to set the field reads
        /// as the refusing scope instead of as an evaluated entry verdict.</summary>
        public enum PlayGateScope { Play = 0, Entry = 1 }

        public struct PlayGateResult
        {
            public bool Pass;
            public List<Offender> Offenders;
            /// <summary><c>Entry</c>: the four rules ran, or threw while running. <c>Play</c>: nothing was
            /// evaluated and the one offender is the scope refusal. Branch on this, never on
            /// <see cref="Pass"/> alone: a refusal is also <c>Pass=false</c>.</summary>
            public PlayGateScope Scope;
        }
        public struct Offender { public string Tag; public string Message; public string Fix; }

        // ----- The in-play scope refusal --------------------------------------------------------------
        // The two "why" clauses are stated as why the rules CANNOT BE TRUSTED in play, not as observed facts:
        // with no emulator installed neither clause is true of the scene, and rule 2a's offender there would
        // be a genuine multi-avatar finding.

        internal const string ScopeRefusalTag = "Entry gate called in play - not a scene finding";

        internal const string ScopeRefusalMessage =
            "PlayGateCore evaluates the preconditions for ENTERING play, and inside play its rules cannot be " +
            "trusted: rule 2a counts every active descriptor, and a live emulator adds its own MirrorReflection " +
            "and ShadowClone roots to that count, while rule 3b reads RunPreprocessAvatarHook, which VRCFury and " +
            "the emulator each clear themselves on a composed build. No scene was inspected and no rule was " +
            "evaluated here, so Pass=false reports the refusal and says nothing about this scene";

        internal const string ScopeRefusalFix =
            "branch on PlayGateResult.Scope (Entry vs Play), not on Pass alone; to judge whether a build actually ran " +
            "the stack, measure the built avatar rather than the gate (docs/emulator.md §Setup)";

        /// <summary>Evaluate the preconditions for entering play with <paramref name="scene"/>. In edit mode
        /// this is <see cref="EvaluateEntry"/>. In play it evaluates nothing and returns the scope refusal
        /// (<see cref="ScopeRefusalMessage"/> says why).
        ///
        /// <see cref="PlayGate"/> deliberately calls <see cref="EvaluateEntry"/> and NOT this door.
        /// <c>Application.isPlaying</c> may read true during <c>ExitingEditMode</c> — the reason
        /// <c>EditorApplication.isPlayingOrWillChangePlaymode</c> exists as a separate API — and were it
        /// true there, routing the hook through this dispatch would refuse every play entry forever. The hook
        /// must not depend on that fact in either direction.</summary>
        public static PlayGateResult Evaluate(Scene scene)
        {
            if (!Application.isPlaying) return EvaluateEntry(scene);
            return new PlayGateResult
            {
                Pass = false,
                Scope = PlayGateScope.Play,
                Offenders = new List<Offender>
                {
                    new Offender { Tag = ScopeRefusalTag, Message = ScopeRefusalMessage, Fix = ScopeRefusalFix },
                },
            };
        }

        /// <summary>The four entry rules against <paramref name="scene"/>. A scene with no active descriptor
        /// passes silently. It makes no <c>Application.isPlaying</c> read, which is what lets the hook call it
        /// safely.
        ///
        /// Internal on purpose: called in play it evaluates the clone-inflated 2a count and the cleared 3b
        /// flag and stamps the result <c>Scope=Entry</c>, so a caller branching on Scope would read those false
        /// offenders as an entry verdict. <see cref="Evaluate"/> is the door for anyone outside this assembly;
        /// reflection still reaches this method.</summary>
        internal static PlayGateResult EvaluateEntry(Scene scene)
        {
            var offenders = new List<Offender>();
            var roots = scene.IsValid() ? scene.GetRootGameObjects() : Array.Empty<GameObject>();

            // Rule 1. Gather includeInactive:true then filter on activeInHierarchy, NOT the (false) overload: on
            // Unity 2022.3.22f1, GetComponentsInChildren(false) returns the QUERIED ROOT's own component even
            // when that root is inactive (live-reproduced), so a deactivated top-level avatar would be
            // miscounted as active, a false "More than 1 avatar enabled" that defeats the gate's own
            // "deactivate all but one" fix.
            var descriptors = new List<VRCAvatarDescriptor>();
            foreach (var root in roots)
                foreach (var d in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                    if (d.gameObject.activeInHierarchy) descriptors.Add(d);
            if (descriptors.Count == 0)
                return new PlayGateResult { Pass = true, Offenders = offenders, Scope = PlayGateScope.Entry };

            // Rule 2a
            if (descriptors.Count > 1)
            {
                var paths = descriptors.Select(d => Path(d.transform));
                offenders.Add(new Offender
                {
                    Tag = "More than 1 avatar enabled",
                    Message = "active avatars: " + string.Join(", ", paths),
                    Fix = "deactivate all but one",
                });
            }

            // Rule 2b
            CheckVrcFury(descriptors, offenders);

            var emulator = FindEnabledEmulator(roots);
            if (emulator != null)
            {
                CheckGestureManager(roots, offenders);      // 3a
                CheckEmulatorConfig(emulator, offenders);   // 3b
            }

            return new PlayGateResult
            {
                Pass = offenders.Count == 0, Offenders = offenders, Scope = PlayGateScope.Entry,
            };
        }

        public static string OverlaySummaryLine(List<Offender> offenders)
        {
            if (offenders == null || offenders.Count == 0) return "";
            var sb = new StringBuilder();
            int shown = Math.Min(2, offenders.Count);
            for (int i = 0; i < shown; i++) sb.Append('(').Append(offenders[i].Tag).Append(") ");
            if (offenders.Count > 2) sb.Append('+').Append(offenders.Count - 2).Append(" (see log)");
            return sb.ToString().TrimEnd();
        }

        /// <summary>The FAIL's first line: every offender and its fix on one line. Fields are flattened because
        /// a Message can be multi-line: <see cref="PlayGate"/>'s catch embeds a whole stack trace.</summary>
        public static string ConsoleSummaryLine(List<Offender> offenders)
        {
            if (offenders == null || offenders.Count == 0) return "";
            return string.Join(" | ", offenders.Select(o =>
                "[" + OneLine(o.Tag) + "] " + OneLine(o.Message) + " (fix: " + OneLine(o.Fix) + ")"));
        }

        private static string OneLine(string s) =>
            string.IsNullOrEmpty(s) ? s : Regex.Replace(s, @"\s+", " ").Trim();

        // ----- Rule 2b: VRCFury carried but no Fix-Write-Defaults feature ------------------------------

        // A first build without FWD pops a blocking modal that stalls the agent.
        private static void CheckVrcFury(List<VRCAvatarDescriptor> descriptors, List<Offender> offenders)
        {
            var fwdType = VendorReflect.FindType(FwdFeatureFullName);
            bool blindReported = false; // the blind FAIL is domain-global — emit it once, not per avatar

            foreach (var d in descriptors)
            {
                // includeInactive is mandatory here: VRCFury's non-destructive build processes inactive objects
                // too, so a VRCFury/FWD setup under a toggled-off child still pops the modal. The descriptor, GM
                // and emulator scans stay active-only because an inactive GM or emulator drives nothing.
                var fury = new List<MonoBehaviour>();
                foreach (var mb in d.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && mb.GetType().FullName == VrcFuryComponentFullName) fury.Add(mb);
                if (fury.Count == 0) continue;

                // present-but-unreadable: the pinned FWD type no longer resolves, so a no-match proves nothing.
                if (fwdType == null)
                {
                    if (!blindReported)
                    {
                        offenders.Add(new Offender
                        {
                            Tag = "VRCFury",
                            Message = "FWD detection may be blind — feature leaf '" + FwdFeatureLeaf +
                                      "' no longer resolves in the loaded VRCFury; re-pin PlayGateCore's FWD leaf const",
                            Fix = "re-pin PlayGateCore.FwdFeatureFullName to the installed VRCFury Fix-Write-Defaults feature type",
                        });
                        blindReported = true;
                    }
                    continue;
                }

                bool hasFwd = false, anyUndecodable = false;
                foreach (var fc in fury)
                {
                    var content = new SerializedObject(fc).FindProperty("content");
                    string tn = content != null ? content.managedReferenceFullTypename : null;
                    if (string.IsNullOrEmpty(tn)) { anyUndecodable = true; continue; }
                    if (tn.Substring(tn.LastIndexOf('.') + 1) == FwdFeatureLeaf) { hasFwd = true; break; }
                }
                if (hasFwd) continue;

                // present-but-unreadable when a feature was undecodable: it might be the FWD one.
                offenders.Add(anyUndecodable
                    ? new Offender
                    {
                        Tag = "VRCFury",
                        Message = "avatar '" + Path(d.transform) + "' carries a VRCFury component whose feature is " +
                                  "undecodable — cannot confirm Fix Write Defaults is present",
                        Fix = "add a VRCFury Fix Write Defaults component (mode Disabled)",
                    }
                    : new Offender
                    {
                        Tag = "VRCFury",
                        Message = "avatar '" + Path(d.transform) + "' carries VRCFury but no Fix Write Defaults feature",
                        Fix = "add a VRCFury Fix Write Defaults component (mode Disabled)",
                    });
            }
        }

        // ----- Rule 3a: an enabled GestureManager fights a LIVE emulator --------------------------------

        // Caller-gated on an enabled emulator: a standalone enabled GestureManager is a legitimate workflow.
        private static void CheckGestureManager(GameObject[] roots, List<Offender> offenders)
        {
            var found = new List<string>();
            foreach (var root in roots)
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(false))
                    if (mb != null && mb.GetType().FullName == GestureManagerFullName && mb.isActiveAndEnabled)
                        found.Add(Path(mb.transform));
            if (found.Count > 0)
                offenders.Add(new Offender
                {
                    Tag = "Gesture Manager",
                    Message = "active GestureManager (with a live emulator): " + string.Join(", ", found),
                    Fix = "disable it",
                });
        }

        // ----- Rule 3b: emulator config ------------------------------------------------------------------

        private static void CheckEmulatorConfig(MonoBehaviour emu, List<Offender> offenders)
        {
            var t = emu.GetType();
            bool? run  = ReportGimmick.ReadBoolMember(emu, t, EmulatorBinding.RunPreprocessAvatarHook);
            bool? perm = ReportGimmick.ReadBoolMember(emu, t, EmulatorBinding.EnablePlayerContactPermissions);

            // present-but-unreadable
            if (run == null || perm == null)
            {
                var missing = new List<string>();
                if (run == null)  missing.Add(EmulatorBinding.RunPreprocessAvatarHook);
                if (perm == null) missing.Add(EmulatorBinding.EnablePlayerContactPermissions);
                offenders.Add(new Offender
                {
                    Tag = "Emulator config",
                    Message = "emulator '" + Path(emu.transform) + "' field(s) not reflectable: " +
                              string.Join(", ", missing) + " — cannot verify config",
                    // ReadBoolMember binds public-only, so a field that went NON-PUBLIC lands here too —
                    // naming only a rename would send the reader looking for the wrong change.
                    Fix = "re-pin EmulatorBinding's emulator field names to the installed LyumaAv3Emulator; " +
                          "if the field still exists but is no longer public, the read path needs NonPublic binding",
                });
                return;
            }

            // One "Emulator config" offender naming whichever flag(s) are wrong — a second identical-tag
            // offender would render a duplicated "(Emulator config) (Emulator config)" overlay.
            var wrong = new List<string>();
            if (!run.Value)  wrong.Add("RunPreprocessAvatarHook is off");
            if (perm.Value)  wrong.Add("EnablePlayerContactPermissions is on");
            if (wrong.Count > 0)
                offenders.Add(new Offender
                {
                    Tag = "Emulator config",
                    Message = "emulator '" + Path(emu.transform) + "' " + string.Join("; ", wrong),
                    Fix = "set RunPreprocessAvatarHook on and EnablePlayerContactPermissions off",
                });
        }

        private static MonoBehaviour FindEnabledEmulator(GameObject[] roots)
        {
            foreach (var root in roots)
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(false))
                    if (mb != null && mb.GetType().FullName == EmulatorBinding.EmulatorFullName && mb.isActiveAndEnabled)
                        return mb;
            return null;
        }

        private static string Path(Transform t) => ReportGimmick.GetHierarchyPath(t);
    }
}
