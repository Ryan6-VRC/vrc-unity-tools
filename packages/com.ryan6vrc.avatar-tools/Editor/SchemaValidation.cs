using System.Collections.Generic;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Document-level validation over a parsed <see cref="AnimDocument"/>. This is the second gate after the
    /// parser: the parser throws on malformed <em>text</em> (unknown fields, bad values); this <em>reports</em>
    /// semantic defects that are representable in a well-formed model. Most "refused constructs" (synced
    /// layers, trigger params, mirror/cycleOffset param binding, ...) are refused by construction — the model
    /// has no field to express them — so this pass does not re-check them.
    ///
    /// <para><see cref="Validate"/> is a PURE function: it NEVER throws. Each offender is a single line
    /// <c># &lt;rule&gt;: &lt;detail&gt; (at &lt;location&gt;)</c>. An empty list means no document-level defect.</para>
    ///
    /// System.* only (no Unity API) so it can be exercised outside the editor.
    /// </summary>
    public static class SchemaValidation
    {
        public static List<string> Validate(AnimDocument doc)
        {
            var errors = new List<string>();
            if (doc == null) return errors;

            // Rule 1 — schema version. Every real document declares schema: 1; 0/unset/other all fail.
            if (doc.Schema != 1)
                errors.Add($"# schema-version: unsupported schema {doc.Schema} (supported: 1) (at document)");

            // Rule 6 — base-fx layer floor. The base-FX index rule addresses layers 0-2, so fewer than three
            // layers cannot satisfy it.
            if (doc.Role == ControllerRole.BaseFx && doc.Layers.Count < 3)
                errors.Add($"# base-fx-floor: base-fx controller declares {doc.Layers.Count} layer(s) but needs at least 3 (indices 0-2) (at document)");

            // name -> declared type. Conditions on names absent from this map are another lint's concern (the
            // controller-level undeclared-param check), so they are skipped here rather than flagged.
            var paramTypes = new Dictionary<string, AnimParamType>();
            foreach (var p in doc.Parameters)
                if (p != null && p.Name != null && !paramTypes.ContainsKey(p.Name))
                    paramTypes[p.Name] = p.Type;

            var clipNames = new HashSet<string>();
            foreach (var c in doc.Clips)
                if (c != null && c.Name != null) clipNames.Add(c.Name);

            foreach (var layer in doc.Layers)
            {
                if (layer == null || layer.Root == null) continue;
                string ln = layer.Name ?? "(unnamed)";
                var root = layer.Root;

                // Rule 4 — default-state existence (checked only against the layer's own root machine, which is
                // where a top-level default: names its target).
                if (!string.IsNullOrEmpty(root.DefaultState) && !MachineHasMember(root, root.DefaultState))
                    errors.Add($"# dangling-default: layer '{ln}' default '{root.DefaultState}' names no state or submachine (at layer '{ln}')");

                // Rules 3 & 5 — condition op/type + inline-clip reference integrity, recursing submachines.
                WalkMachine(root, ln, paramTypes, clipNames, errors);
            }

            return errors;
        }

        // Rule 3 & 5 carrier: walk one state machine's ladders, states, and nested submachines.
        private static void WalkMachine(StateMachine sm, string layer,
            Dictionary<string, AnimParamType> paramTypes, HashSet<string> clips, List<string> errors)
        {
            foreach (var t in sm.EntryLadder) CheckConditions(t, layer, "entry", paramTypes, errors);
            foreach (var t in sm.AnyLadder) CheckConditions(t, layer, "any", paramTypes, errors);

            foreach (var st in sm.States)
            {
                if (st == null) continue;
                if (st.Motion != null) CheckMotionClips(st.Motion, layer, st.Name, clips, errors);
                foreach (var t in st.Transitions)
                    CheckConditions(t, layer, $"state '{st.Name}'", paramTypes, errors);
            }

            foreach (var sub in sm.Machines)
                if (sub != null && sub.Machine != null) WalkMachine(sub.Machine, layer, paramTypes, clips, errors);
        }

        // Rule 3 — operator must be legal for the declared parameter type.
        private static void CheckConditions(Transition t, string layer, string origin,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (t == null) return;
            string target = t.ToExit ? "Exit" : (t.To ?? "Exit");
            string loc = $"layer '{layer}' {origin} → '{target}'";
            foreach (var c in t.When)
            {
                if (c.Param == null || !paramTypes.TryGetValue(c.Param, out var type)) continue; // undeclared -> skip
                if (!OpValidForType(c.Op, type))
                    errors.Add($"# condition-op-type: param '{c.Param}' ({TypeToken(type)}) cannot use operator '{OpToken(c.Op)}' (at {loc})");
            }
        }

        // Rule 5 — every inline-clip reference must name a declared clip; recurse blend-tree children.
        private static void CheckMotionClips(MotionRef m, string layer, string state,
            HashSet<string> clips, List<string> errors)
        {
            if (m == null) return;
            if (m.Clip != null && !clips.Contains(m.Clip))
                errors.Add($"# dangling-clip: state '{state}' references clip '{m.Clip}' which is not declared (at layer '{layer}' state '{state}')");
            if (m.Tree != null)
                foreach (var child in m.Tree.Children)
                    if (child != null) CheckMotionClips(child.Motion, layer, state, clips, errors);
        }

        private static bool MachineHasMember(StateMachine sm, string name)
        {
            foreach (var s in sm.States) if (s != null && s.Name == name) return true;
            foreach (var m in sm.Machines) if (m != null && m.Name == name) return true;
            return false;
        }

        // Float equality is invalid in Unity animator conditions, so Float allows only Greater/Less. Int is a
        // discrete compare (no Is/IsNot). Bool is Is/IsNot only.
        private static bool OpValidForType(CondOp op, AnimParamType type)
        {
            switch (type)
            {
                case AnimParamType.Bool:
                    return op == CondOp.Is || op == CondOp.IsNot;
                case AnimParamType.Int:
                    return op == CondOp.Greater || op == CondOp.Less || op == CondOp.Equals || op == CondOp.NotEqual;
                case AnimParamType.Float:
                    return op == CondOp.Greater || op == CondOp.Less;
                default:
                    return true;
            }
        }

        private static string TypeToken(AnimParamType t)
        {
            switch (t)
            {
                case AnimParamType.Bool: return "bool";
                case AnimParamType.Int: return "int";
                case AnimParamType.Float: return "float";
                default: return t.ToString();
            }
        }

        private static string OpToken(CondOp op)
        {
            switch (op)
            {
                case CondOp.Is: return "is";
                case CondOp.IsNot: return "isNot";
                case CondOp.Greater: return "greater";
                case CondOp.Less: return "less";
                case CondOp.Equals: return "equals";
                case CondOp.NotEqual: return "notEqual";
                default: return op.ToString();
            }
        }
    }
}
