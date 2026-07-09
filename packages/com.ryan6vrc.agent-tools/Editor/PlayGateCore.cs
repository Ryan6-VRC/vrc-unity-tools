using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Pure, UI-free evaluation of the play-entry preconditions (no window / menu / selection coupling —
    /// the testability seam). <see cref="PlayGate"/> is the Editor-coupled door that runs this on
    /// <c>ExitingEditMode</c> and cancels entry on failure. INSPECTION ONLY — never mutates the scene;
    /// it is a pure refusal that names each offender and its fix, like the tracked <c>run_tests</c> deny hook.
    ///
    /// Enumeration is scoped to the passed <paramref name="scene"/> (the "≥1 active avatar" trigger) —
    /// never the global <c>FindObjectsOfType</c>, which spans additively-loaded scenes + DontDestroyOnLoad
    /// and would false-positive "More than 1 avatar enabled". Non-SDK hazards (VRCFury / GestureManager /
    /// LyumaAv3Emulator) are detected by <c>GetType().FullName</c> reflection (the <see cref="ReportGimmick"/>
    /// precedent — no asmdef dependency).
    ///
    /// Two rules — GestureManager-enabled and the emulator config — fire ONLY when an enabled emulator
    /// control object exists (a GestureManager only fights a *live* emulator; the emulator's own config is
    /// only meaningful when it is present). Exactly-one-avatar and VRCFury-Fix-Write-Defaults are real
    /// hazards regardless, so they are unconditional.
    ///
    /// Degradation is one sentence: <b>absent = safe silent skip; present-but-unreadable = block loud.</b>
    /// A genuinely-absent type (package not installed) skips its rule silently; a hazard that IS on a
    /// scene object but whose needed field/feature can't be reflected emits a loud named FAIL — a read
    /// miss on a live hazard must never read as an honest "all clear".
    /// </summary>
    public static class PlayGateCore
    {
        // FWD leaf — pinned, not tribal. The VRCFury Fix-Write-Defaults feature type; confirmed against
        // VRCFury 1.1334.0. Re-pin if a VRCFury update renames it (the blind self-check below fails loud
        // when this no longer resolves in the loaded VRCFury assembly).
        public const string FwdFeatureFullName = "VF.Model.Feature.FixWriteDefaults";
        // Leaf (last segment) matched against each VRCFury feature's managedReferenceFullTypename leaf,
        // mirroring ReportGimmick's "FullController"/"ApplyDuringUpload" leaf-matching. Derived from the
        // full name — one source of truth, so a re-pin can't leave the two silently divergent.
        // Note the asymmetry: the blind check resolves the FULL name (exact); feature matching compares
        // the LEAF (loose). Both a full-name miss and a leaf mismatch resolve toward a BLOCK — the safe
        // direction — so the looseness never opens a fail-open.
        public static readonly string FwdFeatureLeaf =
            FwdFeatureFullName.Substring(FwdFeatureFullName.LastIndexOf('.') + 1);

        // Full type names of the reflected hazards (mirrors verify.md's assert snippet + ReportGimmick).
        // The emulator control component is `LyumaAv3Emulator` (the installed lyuma.av3emulator package ships
        // no bare `Av3Emulator` type — a wrong literal here silently disables the whole emulator rule).
        private const string VrcFuryComponentFullName = "VF.Model.VRCFury";
        private const string GestureManagerFullName   = "BlackStartX.GestureManager.GestureManager";
        private const string LyumaEmulatorFullName    = "Lyuma.Av3Emulator.Runtime.LyumaAv3Emulator";

        public struct PlayGateResult { public bool Pass; public List<Offender> Offenders; }
        public struct Offender { public string Tag; public string Message; public string Fix; }

        /// <summary>Evaluate the preconditions for entering play with <paramref name="scene"/> (the scene
        /// entering play — the hook passes <c>SceneManager.GetActiveScene()</c>). Pass ⇔ zero offenders.
        /// A non-avatar / codegen / bake scene (no active descriptor) passes silently.</summary>
        public static PlayGateResult Evaluate(Scene scene)
        {
            var offenders = new List<Offender>();
            var roots = scene.IsValid() ? scene.GetRootGameObjects() : Array.Empty<GameObject>();

            // Rule 1: gather active-in-hierarchy descriptors under the scene's roots. None → pass silently.
            // Gather includeInactive:true then filter on activeInHierarchy explicitly — NOT the (false)
            // overload: on Unity 2022.3.22f1, GetComponentsInChildren(false) returns the QUERIED ROOT's own
            // component even when that root is inactive (live-reproduced), so a deactivated top-level avatar
            // would be miscounted as active — a false "More than 1 avatar enabled" that also defeats the
            // gate's own "deactivate all but one" fix. The explicit filter is the spec-sanctioned form.
            var descriptors = new List<VRCAvatarDescriptor>();
            foreach (var root in roots)
                foreach (var d in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                    if (d.gameObject.activeInHierarchy) descriptors.Add(d);
            if (descriptors.Count == 0)
                return new PlayGateResult { Pass = true, Offenders = offenders };

            // Rule 2a (unconditional): more than one active descriptor.
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

            // Rule 2b (unconditional): VRCFury carried but no Fix-Write-Defaults feature.
            CheckVrcFury(descriptors, offenders);

            // Rules 3a/3b are keyed on one lookup — an enabled emulator control object — computed ONCE.
            var emulator = FindEnabledEmulator(roots);
            if (emulator != null)
            {
                CheckGestureManager(roots, offenders);      // 3a — a GM only fights a live emulator
                CheckEmulatorConfig(emulator, offenders);   // 3b — the emulator's own config
            }

            return new PlayGateResult { Pass = offenders.Count == 0, Offenders = offenders };
        }

        /// <summary>Overlay line 2 (deterministic, unit-testable): the first two offender tags in parens,
        /// then <c>+N (see log)</c> when there are more than two. Lives in the pure core so it is testable
        /// without Editor coupling.</summary>
        public static string OverlaySummaryLine(List<Offender> offenders)
        {
            if (offenders == null || offenders.Count == 0) return "";
            var sb = new StringBuilder();
            int shown = Math.Min(2, offenders.Count);
            for (int i = 0; i < shown; i++) sb.Append('(').Append(offenders[i].Tag).Append(") ");
            if (offenders.Count > 2) sb.Append('+').Append(offenders.Count - 2).Append(" (see log)");
            return sb.ToString().TrimEnd();
        }

        // ----- Rule 2b: VRCFury carried but no Fix-Write-Defaults feature ------------------------------

        // Conditional on the avatar actually carrying VRCFury (absent VRCFury → no FWD hazard → silent).
        // Highest-value block: a first build without FWD pops a blocking modal that stalls the agent.
        private static void CheckVrcFury(List<VRCAvatarDescriptor> descriptors, List<Offender> offenders)
        {
            // Blind-detection self-check (finding-#1 policy): resolve the pinned FWD type in the loaded
            // domain. Resolved once; only consulted when an avatar actually carries VRCFury.
            var fwdType = ResolveType(FwdFeatureFullName);
            bool blindReported = false; // the blind FAIL is domain-global — emit it once, not per avatar

            foreach (var d in descriptors)
            {
                // includeInactive: MANDATORY here (the ReportGimmick precedent) — VRCFury's non-destructive
                // build processes inactive objects too, so a VRCFury/FWD setup under a toggled-off child
                // still pops the modal. Missing it would read "no VRCFury" and fail open. (Contrast the
                // active-descriptor / GM / emulator scans, which stay active-only: an inactive GM or
                // emulator drives nothing — only VRCFury's *presence* matters even when inactive.)
                var fury = new List<MonoBehaviour>();
                foreach (var mb in d.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && mb.GetType().FullName == VrcFuryComponentFullName) fury.Add(mb);
                if (fury.Count == 0) continue; // this avatar carries no VRCFury → no FWD hazard

                // present-but-unreadable: VRCFury IS here but the pinned FWD type no longer resolves →
                // a stale leaf silently reading "no FWD needed"/"FWD present" is the exact fail-open this
                // gate exists to prevent. Block loud instead of trusting the no-match (once for the domain).
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

                // No FWD found. If a feature was undecodable we cannot be sure it wasn't the FWD one →
                // present-but-unreadable → block loud (distinct message); otherwise the plain no-FWD block.
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

        // Only reached when an enabled emulator control object exists (caller-gated). A standalone enabled
        // GestureManager, with no emulator, is a legitimate workflow and is allowed.
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

        // ----- Rule 3b: emulator config (the enabled emulator was already found) ------------------------

        // The enabled emulator control object is passed in (found once in Evaluate). Intent-to-emulate is
        // inferred from the scene, never mind-read.
        private static void CheckEmulatorConfig(MonoBehaviour emu, List<Offender> offenders)
        {
            var t = emu.GetType();
            bool? run  = ReportGimmick.ReadBoolMember(emu, t, "RunPreprocessAvatarHook");
            bool? perm = ReportGimmick.ReadBoolMember(emu, t, "EnablePlayerContactPermissions");

            // present-but-unreadable: the emulator IS here but a needed flag can't be reflected (renamed
            // field) → block loud rather than skip a live hazard.
            if (run == null || perm == null)
            {
                var missing = new List<string>();
                if (run == null)  missing.Add("RunPreprocessAvatarHook");
                if (perm == null) missing.Add("EnablePlayerContactPermissions");
                offenders.Add(new Offender
                {
                    Tag = "Emulator config",
                    Message = "emulator '" + Path(emu.transform) + "' field(s) not reflectable: " +
                              string.Join(", ", missing) + " — cannot verify config",
                    Fix = "re-pin PlayGateCore's emulator field names to the installed LyumaAv3Emulator",
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

        // ----- Reflection helpers ----------------------------------------------------------------------

        // Find the first enabled emulator control object under the scene roots (active-only; an inactive
        // emulator drives nothing). Returned to Evaluate so the GM and emulator-config rules share one scan.
        private static MonoBehaviour FindEnabledEmulator(GameObject[] roots)
        {
            foreach (var root in roots)
                foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(false))
                    if (mb != null && mb.GetType().FullName == LyumaEmulatorFullName && mb.isActiveAndEnabled)
                        return mb;
            return null;
        }

        // Resolve a type by full name across the loaded domain (the FWD-present-in-project blind check).
        // A genuinely-absent type returns null → the caller treats absence as a safe silent skip.
        private static Type ResolveType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); }
                catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        // Reuse ReportGimmick's scene-root-absolute path grammar (one grammar across the family), inheriting
        // its first-match duplicate-named-sibling caveat rather than authoring a second path formatter.
        private static string Path(Transform t) => ReportGimmick.GetHierarchyPath(t);
    }
}
