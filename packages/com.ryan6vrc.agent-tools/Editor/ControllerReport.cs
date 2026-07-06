using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Read-only markdown DIGEST of an <see cref="AnimatorController"/> for the AI-assisted VRChat
    /// workflow — the semantic decode a coding agent needs to reason about an animator without opening
    /// the graph window. Walks parameters, layers (with the Write-Defaults ON/OFF/MIXED signal
    /// AnimatorLint flags), every state (recursing sub-state-machines at any depth), the first-match
    /// transition ladder with real parameter names, blend trees, and — the highest-value surface —
    /// VRC state-machine behaviours decoded typed (parameter drivers, tracking control, locomotion).
    ///
    /// Speaks the substrate's own handles: GameObject-free but asset paths + GUIDs for every clip
    /// (a clip is only recoverable by GUID once its path rots), real parameter/state/motion names.
    /// The load-bearing motion distinction is EMPTY (a clean, intentional idiom) vs BROKEN (a dangling
    /// motion reference whose asset is gone) — the latter surfaces the raw dangling GUID, the only
    /// surviving handle, recovered by parsing the controller YAML once (no C# API exposes it).
    ///
    /// INSPECTION ONLY — never mutates. Emits one line carrying the artifact path in-band.
    /// </summary>
    [AgentTool]
    public static class ControllerReport
    {
        // ----- Agent entry point (path-addressed load is one line at the call site:
        //   ControllerReport.Report(AssetDatabase.LoadAssetAtPath<AnimatorController>(path)) ) -------

        /// <summary>Digest <paramref name="controller"/> to markdown under Snapshots/. Returns a one-line
        /// summary ending with the artifact path in-band (<c>… =&gt; OK | log=&lt;path&gt;</c>); a null
        /// controller is a bare-FAIL with no trailer (nothing was written).</summary>
        public static string Report(AnimatorController controller)
        {
            if (controller == null)
            {
                const string err = "[ControllerReport] FAIL: controller not found";
                Debug.LogError(err);
                return err;
            }

            var sb = new StringBuilder();
            int layerCount = controller.layers.Length;
            int paramCount = controller.parameters.Length;
            int stateCount = 0;

            // Recover the dangling-motion GUID set once from the controller YAML — the only handle a
            // broken motion leaves behind, and no C# API exposes it.
            var danglingGuids = RecoverDanglingMotionGuids(controller);

            // ---- Header ----
            string assetPath = AssetDatabase.GetAssetPath(controller);
            sb.Append("# ControllerReport: ").Append(controller.name).Append('\n');
            sb.Append("asset: `").Append(string.IsNullOrEmpty(assetPath) ? "(unsaved)" : assetPath).Append("`  \n");
            sb.Append("layers=").Append(layerCount).Append(" states=(see below) params=").Append(paramCount).Append('\n');

            if (danglingGuids.Count > 0)
            {
                sb.Append("\n**Broken motion GUIDs** (dangling — asset missing, GUID is the only surviving handle):\n");
                foreach (var g in danglingGuids) sb.Append("- `").Append(g).Append("`\n");
            }

            // ---- Parameters ----
            sb.Append("\n## Parameters\n\n");
            if (paramCount == 0) sb.Append("_(none)_\n");
            else
            {
                sb.Append("| name | type | default |\n|---|---|---|\n");
                foreach (var p in controller.parameters)
                {
                    string def;
                    switch (p.type)
                    {
                        case AnimatorControllerParameterType.Bool:    def = p.defaultBool ? "true" : "false"; break;
                        case AnimatorControllerParameterType.Int:     def = p.defaultInt.ToString(CultureInfo.InvariantCulture); break;
                        case AnimatorControllerParameterType.Float:   def = F(p.defaultFloat); break;
                        default:                                      def = "—"; break; // Trigger: no default
                    }
                    sb.Append("| `").Append(Cell(p.name)).Append("` | ").Append(p.type).Append(" | ").Append(Cell(def)).Append(" |\n");
                }
            }

            // ---- Layers + nested states/transitions/blend-trees ----
            // Reading an animator means reading each state alongside its own transitions, so states,
            // their first-match ladders, and blend-tree expansions render together under their layer.
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];

                // A synced layer owns no state machine: it re-skins the SOURCE layer's states with
                // per-state OVERRIDE motions/behaviours (the whole point — common in VRC gesture
                // layers). Walk the source topology but resolve motion/behaviours through the layer's
                // override APIs so those overrides are decoded, not silently dropped.
                bool synced = layer.syncedLayerIndex >= 0;
                var walkSm = synced ? layers[layer.syncedLayerIndex].stateMachine : layer.stateMachine;

                sb.Append("\n## Layer ").Append(li).Append(": ").Append(layer.name).Append('\n');
                sb.Append("weight=").Append(F(layer.defaultWeight))
                  .Append(" blend=").Append(layer.blendingMode)
                  .Append(" mask=").Append(layer.avatarMask != null ? layer.avatarMask.name : "—")
                  .Append(" ").Append(WriteDefaultsSummary(walkSm))
                  .Append('\n');

                if (synced)
                    sb.Append("_synced layer → source layer ").Append(layer.syncedLayerIndex)
                      .Append(" (`").Append(layers[layer.syncedLayerIndex].name).Append("`); motions/behaviours below are this layer's overrides_\n");

                if (walkSm == null) { sb.Append("_(no state machine)_\n"); continue; }

                WalkStateMachine(walkSm, "", sb, controller, layer, synced, danglingGuids, ref stateCount);
            }

            // ---- Summary + artifact ----
            var body = sb.ToString();
            var summary = "[ControllerReport] " + controller.name + ": layers=" + layerCount
                        + " states=" + stateCount + " params=" + paramCount + " => OK";
            var result = RunLogFormat.WriteRunLog(RunLogFormat.SnapshotDir, "controller_" + controller.name, summary, body, ".md");
            Debug.Log(result);
            return result;
        }

        // ----- State-machine walk (recurses sub-state-machines at any depth) ----------------------

        private static void WalkStateMachine(AnimatorStateMachine sm, string prefix, StringBuilder sb,
            AnimatorController controller, AnimatorControllerLayer layer, bool synced,
            List<string> danglingGuids, ref int stateCount)
        {
            var def = sm.defaultState;

            // AnyState / Entry ladders are per-state-machine — render them at this level's head,
            // scoped by the sub-SM path so the reader can tell which level a ladder governs.
            foreach (var t in sm.anyStateTransitions)
                sb.Append("- ").Append(prefix).Append("AnyState ").Append(RenderTransition(t)).Append('\n');
            foreach (var t in sm.entryTransitions)
                sb.Append("- ").Append(prefix).Append("Entry ").Append(RenderEntryTransition(t)).Append('\n');
            if (def != null)
                sb.Append("- ").Append(prefix).Append("Entry → `").Append(prefix).Append(def.name).Append("` (default)\n");

            foreach (var cs in sm.states)
            {
                var st = cs.state;
                if (st == null) continue;
                // A synced layer re-skins the SOURCE layer's states — they are the same AnimatorState
                // objects already counted for the source layer, so counting them again would inflate the
                // hint. Count each distinct state once (on its owning, non-synced layer).
                if (!synced) stateCount++;

                // In a synced layer the state belongs to the source SM; its EFFECTIVE motion/behaviours
                // are this layer's overrides (falling back to the source when no override is set).
                Motion motion;
                string motionCell;
                if (synced)
                {
                    var ov = layer.GetOverrideMotion(st);
                    motion = ov != null ? ov : st.motion;
                    motionCell = motion != null
                        ? MotionLabel(motion) + (ov != null ? " (synced override)" : " (inherited source)")
                        : "(empty)";
                }
                else
                {
                    motion = st.motion;
                    motionCell = StateMotion(st, controller, danglingGuids);
                }

                sb.Append("\n### `").Append(prefix).Append(st.name).Append("`\n");
                sb.Append("motion: ").Append(motionCell);
                sb.Append("  speed=").Append(F(st.speed));
                if (st.speedParameterActive && !string.IsNullOrEmpty(st.speedParameter))
                    sb.Append(" speedParam=`").Append(st.speedParameter).Append('`');
                if (st.timeParameterActive && !string.IsNullOrEmpty(st.timeParameter))
                    sb.Append(" motionTime=`").Append(st.timeParameter).Append('`');
                sb.Append(" WD=").Append(st.writeDefaultValues ? "on" : "off").Append('\n');

                // Expand a blend-tree motion inline (recursing nested trees).
                if (motion is BlendTree bt)
                    AppendBlendTree(bt, 0, sb);

                // Typed VRC behaviour decode (override behaviours for a synced layer).
                AppendBehaviours(synced ? layer.GetOverrideBehaviours(st) : st.behaviours, sb);

                // This state's own transition ladder.
                foreach (var t in st.transitions)
                    sb.Append("- → ").Append(RenderTransition(t)).Append('\n');
            }

            foreach (var child in sm.stateMachines)
            {
                if (child.stateMachine == null) continue;
                WalkStateMachine(child.stateMachine, prefix + child.stateMachine.name + "/", sb, controller, layer, synced, danglingGuids, ref stateCount);
            }
        }

        // ----- Motion rendering -------------------------------------------------------------------

        private static string StateMotion(AnimatorState st, AnimatorController controller, List<string> danglingGuids)
        {
            if (st.motion != null) return MotionLabel(st.motion);

            // motion == null: distinguish a clean-empty idiom from a dangling (broken) reference.
            var mp = new SerializedObject(st).FindProperty("m_Motion");
            bool dangling = mp != null && mp.objectReferenceInstanceIDValue != 0;
            if (!dangling) return "(empty)";
            if (danglingGuids.Count == 1) return "(broken: guid=" + danglingGuids[0] + ")";
            if (danglingGuids.Count > 1) return "(broken: dangling motion — see Broken motion GUIDs)";
            return "(broken: guid unrecoverable)"; // no guid recoverable from YAML (e.g. binary-serialized)
        }

        private static string MotionLabel(Motion m)
        {
            if (m is AnimationClip clip)
            {
                // A clip embedded as a sub-asset of the controller reports the controller's own
                // .controller path — a GUID/path there would misattribute it. The name is its handle.
                if (AssetDatabase.IsSubAsset(clip)) return "`" + clip.name + "` (embedded in controller)";
                string path = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path)) return "`" + clip.name + "` (embedded)"; // scene/unsaved
                return "`" + clip.name + "` (`" + path + "`, guid=" + AssetDatabase.AssetPathToGUID(path) + ")";
            }
            if (m is BlendTree bt)
            {
                bool is2D = Is2D(bt.blendType);
                string prm = bt.blendType == BlendTreeType.Direct ? "direct"
                           : is2D ? "`" + bt.blendParameter + "`,`" + bt.blendParameterY + "`"
                           : "`" + bt.blendParameter + "`";
                return "BlendTree(" + bt.blendType + ", param=" + prm + ", " + bt.children.Length + " children)";
            }
            return m != null ? m.GetType().Name : "(empty)";
        }

        private static void AppendBlendTree(BlendTree bt, int indent, StringBuilder sb)
        {
            string pad = new string(' ', (indent + 1) * 2);
            foreach (var ch in bt.children)
            {
                sb.Append(pad).Append("- ");
                if (bt.blendType == BlendTreeType.Direct)
                    sb.Append("param=`").Append(ch.directBlendParameter).Append("` ");
                else if (Is2D(bt.blendType))
                    sb.Append("@(").Append(F(ch.position.x)).Append(", ").Append(F(ch.position.y)).Append(") ");
                else
                    sb.Append("@").Append(F(ch.threshold)).Append(' ');

                sb.Append(ch.motion != null ? MotionLabel(ch.motion) : "(empty)");
                if (Math.Abs(ch.timeScale - 1f) > 1e-6f) sb.Append(" timeScale=").Append(F(ch.timeScale));
                sb.Append('\n');

                if (ch.motion is BlendTree childBt) AppendBlendTree(childBt, indent + 1, sb);
            }
        }

        private static bool Is2D(BlendTreeType t) =>
            t == BlendTreeType.SimpleDirectional2D || t == BlendTreeType.FreeformDirectional2D || t == BlendTreeType.FreeformCartesian2D;

        // ----- Transitions ------------------------------------------------------------------------

        private static string RenderTransition(AnimatorStateTransition t)
        {
            string dest = t.isExit ? "Exit"
                        : t.destinationState != null ? "`" + t.destinationState.name + "`"
                        : t.destinationStateMachine != null ? "`" + t.destinationStateMachine.name + "` (state machine)"
                        : "(none)";
            return dest + " " + Conditions(t.conditions, t.hasExitTime);
        }

        private static string RenderEntryTransition(AnimatorTransition t)
        {
            string dest = t.destinationState != null ? "`" + t.destinationState.name + "`"
                        : t.destinationStateMachine != null ? "`" + t.destinationStateMachine.name + "` (state machine)"
                        : "(none)";
            return "→ " + dest + " " + Conditions(t.conditions, false);
        }

        private static string Conditions(AnimatorCondition[] conditions, bool hasExitTime)
        {
            if (conditions == null || conditions.Length == 0)
                return hasExitTime ? "[exitTime]" : "[unconditional]";
            var parts = new List<string>();
            foreach (var c in conditions)
            {
                string op;
                switch (c.mode)
                {
                    case AnimatorConditionMode.If:       op = "= true"; break;
                    case AnimatorConditionMode.IfNot:    op = "= false"; break;
                    case AnimatorConditionMode.Greater:  op = "> " + F(c.threshold); break;
                    case AnimatorConditionMode.Less:     op = "< " + F(c.threshold); break;
                    case AnimatorConditionMode.Equals:   op = "== " + F(c.threshold); break;
                    case AnimatorConditionMode.NotEqual: op = "!= " + F(c.threshold); break;
                    default:                             op = c.mode + " " + F(c.threshold); break;
                }
                parts.Add("`" + c.parameter + "` " + op);
            }
            string joined = "[" + string.Join(" && ", parts) + "]";
            return hasExitTime ? joined + " +exitTime" : joined;
        }

        // ----- Write Defaults signal (majority / mixed over a layer's states, recursing) ----------

        private static string WriteDefaultsSummary(AnimatorStateMachine sm)
        {
            int on = 0, off = 0;
            CountWriteDefaults(sm, ref on, ref off);
            if (on > 0 && off > 0) return "WD=MIXED";
            if (on > 0) return "WD=ON";
            if (off > 0) return "WD=OFF";
            return "WD=n/a";
        }

        private static void CountWriteDefaults(AnimatorStateMachine sm, ref int on, ref int off)
        {
            if (sm == null) return;
            foreach (var cs in sm.states)
            {
                if (cs.state == null) continue;
                if (cs.state.writeDefaultValues) on++; else off++;
            }
            foreach (var child in sm.stateMachines)
                CountWriteDefaults(child.stateMachine, ref on, ref off);
        }

        // ----- Typed VRC behaviour decode ---------------------------------------------------------

        private static void AppendBehaviours(StateMachineBehaviour[] behaviours, StringBuilder sb)
        {
            if (behaviours == null) return;
            foreach (var b in behaviours)
            {
                if (b == null) { sb.Append("- behaviour: (missing script)\n"); continue; }
                switch (b)
                {
                    case VRC.SDKBase.VRC_AvatarParameterDriver drv:      AppendDriver(drv, sb); break;
                    case VRC.SDKBase.VRC_AnimatorTrackingControl tc:     AppendTracking(tc, sb); break;
                    case VRC.SDKBase.VRC_AnimatorLocomotionControl lc:
                        sb.Append("- LocomotionControl: disableLocomotion=").Append(lc.disableLocomotion ? "true" : "false").Append('\n');
                        break;
                    default:
                        sb.Append("- behaviour: ").Append(b.GetType().Name).Append('\n');
                        break;
                }
            }
        }

        private static void AppendDriver(VRC.SDKBase.VRC_AvatarParameterDriver drv, StringBuilder sb)
        {
            sb.Append("- ParameterDriver (localOnly=").Append(drv.localOnly ? "true" : "false").Append("):\n");
            if (drv.parameters == null) return;
            foreach (var p in drv.parameters)
            {
                sb.Append("  - op=").Append(p.type).Append(" name=`").Append(p.name).Append('`');
                switch (p.type)
                {
                    case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set:
                    case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Add:
                        sb.Append(" value=").Append(F(p.value));
                        break;
                    case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Random:
                        // Numeric dest reads min..max; bool/trigger dest reads chance — render both honestly.
                        sb.Append(" min=").Append(F(p.valueMin)).Append(" max=").Append(F(p.valueMax)).Append(" chance=").Append(F(p.chance));
                        break;
                    case VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy:
                        sb.Append(" source=`").Append(p.source).Append('`');
                        if (p.convertRange)
                            sb.Append(" range=[").Append(F(p.sourceMin)).Append("..").Append(F(p.sourceMax))
                              .Append("]=>[").Append(F(p.destMin)).Append("..").Append(F(p.destMax)).Append(']');
                        break;
                }
                sb.Append('\n');
            }
        }

        private static void AppendTracking(VRC.SDKBase.VRC_AnimatorTrackingControl tc, StringBuilder sb)
        {
            var parts = new List<string>();
            void T(string label, VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType v)
            {
                if (v != VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType.NoChange) parts.Add(label + "=" + v);
            }
            T("head", tc.trackingHead);
            T("leftHand", tc.trackingLeftHand);
            T("rightHand", tc.trackingRightHand);
            T("hip", tc.trackingHip);
            T("leftFoot", tc.trackingLeftFoot);
            T("rightFoot", tc.trackingRightFoot);
            T("leftFingers", tc.trackingLeftFingers);
            T("rightFingers", tc.trackingRightFingers);
            T("eyes", tc.trackingEyes);
            T("mouth", tc.trackingMouth);
            sb.Append("- TrackingControl: ").Append(parts.Count == 0 ? "(all NoChange)" : string.Join(" ", parts)).Append('\n');
        }

        // ----- Dangling-motion GUID recovery (parse controller YAML once) -------------------------

        private static List<string> RecoverDanglingMotionGuids(AnimatorController controller)
        {
            var result = new List<string>();
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return result;
            try
            {
                var text = File.ReadAllText(path);
                var seen = new HashSet<string>();
                foreach (Match m in Regex.Matches(text, @"m_Motion:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*\d+\}"))
                {
                    var g = m.Groups[1].Value;
                    if (seen.Add(g) && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(g)))
                        result.Add(g);
                }
            }
            catch { /* binary-serialized or unreadable — no guids recoverable */ }
            return result;
        }

        // ----- Helpers ----------------------------------------------------------------------------

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // Keep cell text on one table row: escape the column delimiter and collapse newlines.
        private static string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
