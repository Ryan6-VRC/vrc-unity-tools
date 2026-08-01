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

            // Rule 1b — reserved parameter names. The compiler injects these (the seconds-only carrier), so an
            // authored document must not declare one or emission would collide on a duplicate parameter.
            foreach (var p in doc.Parameters)
                if (p != null && p.Name == ReservedNames.CarrierParam)
                    errors.Add($"# reserved-param: '{ReservedNames.CarrierParam}' is reserved for the compiler's seconds-only carrier and cannot be declared (at document)");

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

            // Rule 7 — the menu tree. Keyed on the WIRE type, not the animator type: emission lists the
            // expression parameter as `vrc.type ?? type` (ControllerEmit.EmitVrcParameters), and a menu
            // control is read by VRChat against that listed type. Validating against the animator type
            // would let every `vrc: { type: … }` override through unchecked — a radial on a float-on-the-
            // animator/bool-on-the-wire param compiles clean and yields a knob carrying only 0 and 1.
            if (doc.Menu != null)
            {
                var wireTypes = new Dictionary<string, AnimParamType>();
                var scratch = new HashSet<string>();
                foreach (var p in doc.Parameters)
                {
                    if (p == null || p.Name == null) continue;
                    if (!wireTypes.ContainsKey(p.Name)) wireTypes[p.Name] = p.Vrc?.VrcType ?? p.Type;
                    if (p.Scratch) scratch.Add(p.Name);
                }
                CheckMenu(doc.Menu, "menu", wireTypes, scratch, errors);
            }

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

        // Rooted in the filesystem sense, spelled without System.IO (Path.IsPathRooted is System.IO, which
        // this file deliberately does not take): a leading '/' or '\', or a Windows drive prefix "X:".
        private static bool IsRootedIconPath(string p)
            => p[0] == '/' || p[0] == '\\' || (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]));

        // Rule 7 carrier: one menu page and, recursively, its sub-menus. `where` is the authored path
        // (menu / menu 'Colors' / …) so an offender in a nested page names the page it sits on.
        private static void CheckMenu(List<MenuControl> controls, string where,
            Dictionary<string, AnimParamType> wireTypes, HashSet<string> scratch, List<string> errors)
        {
            if (controls.Count > MenuLimits.MaxControlsPerMenu)
                errors.Add($"# menu-overflow: {where} holds {controls.Count} controls but a VRChat menu page holds {MenuLimits.MaxControlsPerMenu} — split it into sub-menus (at {where})");

            foreach (var c in controls)
            {
                if (c == null) continue;
                string w = $"{where} '{c.Name}'";

                if (string.IsNullOrEmpty(c.Name))
                    errors.Add($"# menu-unnamed: a {c.Kind.ToString().ToLowerInvariant()} control has no name (at {where})");

                // A bare sub-menu drives nothing; every other kind is meaningless without a parameter.
                if (c.Param == null)
                {
                    if (c.Kind != MenuControlKind.SubMenu)
                        errors.Add($"# menu-no-param: {w} is a {c.Kind.ToString().ToLowerInvariant()} with no 'param' — it would render as a control that does nothing (at {w})");
                }
                else
                {
                    // A scratch param is excluded from the emitted VRCExpressionParameters, so VRChat never
                    // sees the name and the control is inert on the avatar — a defect the built menu cannot show.
                    if (scratch.Contains(c.Param))
                        errors.Add($"# menu-scratch-param: {w} drives '{c.Param}', declared scratch: — scratch params are kept out of the params asset, so the control would be inert (at {w})");

                    if (wireTypes.TryGetValue(c.Param, out var pt))
                    {
                        if (c.Kind == MenuControlKind.Radial && pt != AnimParamType.Float)
                            errors.Add($"# menu-radial-type: {w} is a radial on '{c.Param}', declared {pt.ToString().ToLowerInvariant()} — a radial's position is a float (at {w})");
                        if (pt == AnimParamType.Bool && c.Kind != MenuControlKind.Radial && c.Value != 0f && c.Value != 1f)
                            errors.Add($"# menu-bool-value: {w} writes {c.Value} to bool '{c.Param}' (expected 0 or 1) (at {w})");
                        if (pt == AnimParamType.Int && c.Kind != MenuControlKind.Radial && c.Value != (int)c.Value)
                            errors.Add($"# menu-int-value: {w} writes {c.Value} to int '{c.Param}' (expected a whole number) (at {w})");
                    }
                    else
                    {
                        errors.Add($"# menu-undeclared-param: {w} drives '{c.Param}', which the document does not declare under parameters: (at {w})");
                    }
                }

                // Icon SHAPE only. Whether the file is there, and whether it imported as a Texture2D, is
                // ControllerEmit's to answer — this validator is System.*-only and cannot reach the
                // AssetDatabase. What it can rule out is the two spellings that resolve nowhere by
                // construction: an empty string, and a rooted path (a drive letter or a leading separator),
                // which is neither a project path nor document-relative and would bake one machine's layout
                // into a committed document the way an absolute `compiled-from:` once did.
                if (c.Icon != null)
                {
                    if (c.Icon.Trim().Length == 0)
                        errors.Add($"# menu-icon-empty: {w} declares an empty 'icon' — give it a path or drop the field (at {w})");
                    else if (IsRootedIconPath(c.Icon))
                        errors.Add($"# menu-icon-absolute: {w} has an absolute icon path '{c.Icon}' — write a project path (Assets/… or Packages/…) or one relative to this document (at {w})");
                }

                if (c.Controls != null) CheckMenu(c.Controls, w, wireTypes, scratch, errors);
            }
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
                if (st.Motion != null) CheckBlendAxes(st.Motion, layer, st.Name, paramTypes, errors);
                foreach (var t in st.Transitions)
                    CheckConditions(t, layer, $"state '{st.Name}'", paramTypes, errors);
            }

            foreach (var sub in sm.Machines)
            {
                if (sub == null) continue;
                // onExit conditions are authored on the PARENT (this machine), so they are validated here
                // rather than inside the recursion — same undeclared-param / type rules as any other ladder.
                foreach (var t in sub.OnExit) CheckConditions(t, layer, $"onExit of '{sub.Name}'", paramTypes, errors);
                if (sub.Machine != null) WalkMachine(sub.Machine, layer, paramTypes, clips, errors);
            }

            if (sm.Layout != null)
                foreach (var key in sm.Layout.Nodes.Keys)
                {
                    var raw = AddressPath.UnescapeSegment(key);
                    if (!MachineHasMember(sm, raw))
                        errors.Add($"# dangling-layout: layer '{layer}' layout node '{key}' names no state or submachine of its machine (at layer '{layer}')");
                    // A non-canonical key (e.g. a '/'-named node written literally instead of escaped) resolves to
                    // a real member here but MISSES emit's EscapeSegment lookup, silently grid-dropping the authored
                    // position. Reject it at this fatal gate so the loss is fail-loud, not a silent regrid.
                    else if (AddressPath.EscapeSegment(raw) != key)
                        errors.Add($"# unescaped-layout: layer '{layer}' layout node '{key}' must be canonically escaped as '{AddressPath.EscapeSegment(raw)}' (at layer '{layer}')");
                }
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

        // Rule 7 — a blend-tree axis must be a Float animator param. Unity silently freezes a non-float axis
        // at its first child (the value never reaches the float channel the tree reads — no error anywhere),
        // so this is a fatal gate. 1D/2D read Tree.Param (+ ParamY when 2D); Direct reads each child's
        // DirectWeight. Undeclared axes are skipped — the controller-level undeclared-param check owns those.
        private static void CheckBlendAxes(MotionRef m, string layer, string state,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (m == null || m.Tree == null) return;
            CheckTreeAxes(m.Tree, layer, state, paramTypes, errors);
        }

        private static void CheckTreeAxes(BlendTreeSpec t, string layer, string state,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (t == null) return;
            if (t.Kind == TreeKind.Direct)
            {
                foreach (var ch in t.Children)
                    if (ch != null) RequireFloatAxis(ch.DirectWeight, layer, state, paramTypes, errors);
            }
            else
            {
                RequireFloatAxis(t.Param, layer, state, paramTypes, errors);
                if (t.Kind != TreeKind.OneD) RequireFloatAxis(t.ParamY, layer, state, paramTypes, errors);
            }
            foreach (var ch in t.Children)
                if (ch != null && ch.Motion != null && ch.Motion.Tree != null)
                    CheckTreeAxes(ch.Motion.Tree, layer, state, paramTypes, errors);
        }

        private static void RequireFloatAxis(string param, string layer, string state,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (string.IsNullOrEmpty(param)) return;                    // no axis param here
            if (!paramTypes.TryGetValue(param, out var type)) return;   // undeclared -> other lint's concern
            if (type != AnimParamType.Float)
                errors.Add($"# blend-axis-type: param '{param}' ({TypeToken(type)}) is a blend-tree axis but must be float; declare it 'type: float' and sync int via 'vrc: {{ type: int }}' (at layer '{layer}' state '{state}')");
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
