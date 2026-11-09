using System.Collections.Generic;
using System.Linq;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Document-level validation over a parsed <see cref="AnimDocument"/>, the second gate after the parser: the
    /// parser throws on malformed <em>text</em>; this <em>reports</em> semantic defects a well-formed model can
    /// represent, each as one line <c># &lt;rule&gt;: &lt;detail&gt; (at &lt;location&gt;)</c>.
    ///
    /// System.* only, with no Unity API and no System.IO: whatever needs the AssetDatabase or ControllerRules is
    /// ControllerEmit's.
    /// </summary>
    public static class SchemaValidation
    {
        public static List<string> Validate(AnimDocument doc)
        {
            var errors = new List<string>();
            if (doc == null) return errors;

            if (doc.Schema != 1)
                errors.Add($"# schema-version: unsupported schema {doc.Schema} (supported: 1) (at document)");

            foreach (var p in doc.Parameters)
                if (p != null && p.Name == ReservedNames.CarrierParam)
                    errors.Add($"# reserved-param: '{ReservedNames.CarrierParam}' is reserved for the compiler's seconds-only carrier and cannot be declared (at document)");

            if (doc.Role == ControllerRole.BaseFx && doc.Layers.Count < 3)
                errors.Add($"# base-fx-floor: base-fx controller declares {doc.Layers.Count} layer(s) but needs at least 3 (indices 0-2) (at document)");

            foreach (var p in doc.Parameters)
                if (p != null && p.Vrc != null && p.Vrc.HasEncoding) CheckEncoding(p, errors);

            // Names absent from this map are skipped throughout: the compile's undeclared-param lint owns them.
            var paramTypes = new Dictionary<string, AnimParamType>();
            foreach (var p in doc.Parameters)
                if (p != null && p.Name != null && !paramTypes.ContainsKey(p.Name))
                    paramTypes[p.Name] = p.Type;

            var clipNames = new HashSet<string>();
            foreach (var c in doc.Clips)
                if (c != null && c.Name != null) clipNames.Add(c.Name);

            var treeNames = new HashSet<string>();
            foreach (var t in doc.Trees)
                if (t != null && t.Name != null) treeNames.Add(t.Name);
            // Liveness seeds from the state motions, then propagates through live bodies: a flat tally over every
            // trees: body would let a self-edge or clique sustain itself, and lazy emission, driven only from state
            // motions, would drop it with no diagnostic. The rule is docs/animator-schema.md §shared trees.
            var byName = new Dictionary<string, BlendTreeSpec>();
            foreach (var t in doc.Trees)
                if (t != null && t.Name != null && !byName.ContainsKey(t.Name)) byName[t.Name] = t;

            var unknownShared = new List<string>();
            var live = new HashSet<string>();
            var pending = new List<string>();
            void Reach(MotionRef m)
            {
                foreach (var name in SharedRefsIn(m, unknownShared, treeNames))
                    if (live.Add(name)) pending.Add(name);
            }
            foreach (var layer in doc.Layers)
            {
                if (layer == null) continue;
                var states = new List<State>();
                layer.Root.CollectStates(states);
                foreach (var st in states)
                    if (st != null) Reach(st.Motion);
            }
            while (pending.Count > 0)
            {
                var name = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                if (!byName.TryGetValue(name, out var t) || t == null) continue;
                foreach (var ch in t.Children)
                    if (ch != null) Reach(ch.Motion);
            }
            // The worklist never visits a dead tree, so scan every body: a dangling `shared:` in one still reports.
            foreach (var t in doc.Trees)
                if (t != null) foreach (var ch in t.Children)
                    if (ch != null) SharedRefsIn(ch.Motion, unknownShared, treeNames);
            foreach (var name in unknownShared)
                errors.Add($"# dangling-shared: a motion references shared tree '{name}' which is not declared under trees: (at document)");
            foreach (var t in doc.Trees)
            {
                if (t == null) continue;
                if (string.IsNullOrEmpty(t.Name))
                {
                    errors.Add("# unnamed-tree: a trees: entry has no name (at document)");
                    continue;
                }
                if (!live.Contains(t.Name))
                    errors.Add($"# unreferenced-tree: trees: entry '{t.Name}' is not reachable from any state — a reference only from another unreachable trees: entry does not make it live; reference it or delete it (at tree '{t.Name}')");
                CheckMotionClips(new MotionRef { Tree = t }, $"tree '{t.Name}'", $"tree '{t.Name}'", clipNames, errors);
                CheckTreeAxes(t, $"tree '{t.Name}'", paramTypes, errors);
            }

            // Menu checks key on the WIRE type `vrc.type ?? type` (ControllerEmit.EmitVrcParameters), which is what
            // VRChat reads a control against. Keying on the animator type would let a radial on a float-on-the-
            // animator/bool-on-the-wire param compile clean and yield a knob carrying only 0 and 1.
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

                // Only the root's bare-name `default:` is checked here; ControllerEmit refuses a bad nested one.
                if (!string.IsNullOrEmpty(root.DefaultState) && !MachineHasMember(root, root.DefaultState))
                    errors.Add($"# dangling-default: layer '{ln}' default '{root.DefaultState}' names no state or submachine (at layer '{ln}')");

                WalkMachine(root, ln, paramTypes, clipNames, errors);
            }

            return errors;
        }

        // The SDK's VRCExpressionParameters.Parameter.MinBitWidth/MaxBitWidth/MaxLerpDuration, mirrored because this
        // file is SDK-free; ParamEncodingTests pins the mirror to the installed SDK.
        internal const int MinBits = 2, MaxBits = 8;
        internal const float MaxLerpSeconds = 300f;

        // vrc.bits / vrc.signed / vrc.lerp shape the synced copy of an int or float in the params asset. The SDK-version
        // gate and the VRC built-in exclusion need the SDK and ControllerRules, so they are ControllerEmit's.
        private static void CheckEncoding(ParamSpec p, List<string> errors)
        {
            var v = p.Vrc;
            string n = p.Name ?? "(unnamed)";
            string at = $"(at parameter '{n}')";
            string fields = string.Join("/", new[] { v.Bits.HasValue ? "bits" : null, v.Signed.HasValue ? "signed" : null, v.Lerp.HasValue ? "lerp" : null }.Where(s => s != null));

            if (p.Scratch)
                errors.Add($"# param-encoding: parameter '{n}' is scratch, which the params asset leaves out, so nothing carries its vrc.{fields} {at}");
            else if (!v.Synced)
                errors.Add($"# param-encoding: parameter '{n}' declares vrc.{fields} without vrc.synced: true; these fields shape only the synced copy {at}");
            if ((v.VrcType ?? p.Type) == AnimParamType.Bool)
                errors.Add($"# param-encoding: parameter '{n}' is a bool on the wire (vrc.type, else type) and declares vrc.{fields}; a bool is fixed at 1 bit and never lerps, so these fields take an int or float {at}");

            if (v.Bits.HasValue && (v.Bits.Value < MinBits || v.Bits.Value > MaxBits))
                errors.Add($"# param-bits: parameter '{n}' vrc.bits is {v.Bits.Value}; the SDK takes {MinBits} to {MaxBits} {at}");
            if (v.Lerp.HasValue && !(v.Lerp.Value >= 0f && v.Lerp.Value <= MaxLerpSeconds))
                errors.Add($"# param-lerp: parameter '{n}' vrc.lerp is {v.Lerp.Value}; the SDK takes 0 to {MaxLerpSeconds} seconds {at}");
        }

        private static bool IsRootedIconPath(string p)
            => p[0] == '/' || p[0] == '\\' || (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]));

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

                if (c.Param == null)
                {
                    if (c.Kind != MenuControlKind.SubMenu)
                        errors.Add($"# menu-no-param: {w} is a {c.Kind.ToString().ToLowerInvariant()} with no 'param' — it would render as a control that does nothing (at {w})");
                }
                else
                {
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

                // Icon shape only; ControllerEmit.ResolveIcon owns whether the file exists and imported as a
                // Texture2D. A rooted path is neither a project path nor document-relative, and would bake one
                // machine's layout into a committed document.
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

        private static void WalkMachine(StateMachine sm, string layer,
            Dictionary<string, AnimParamType> paramTypes, HashSet<string> clips, List<string> errors,
            StateMachine layerRoot = null)
        {
            layerRoot = layerRoot ?? sm;

            if (!string.IsNullOrEmpty(sm.DefaultStatePath) && !RootHasStateAtPath(layerRoot, sm.DefaultStatePath))
                errors.Add($"# dangling-default-state: layer '{layer}' defaultState '{sm.DefaultStatePath}' names no state reachable from the layer root (at layer '{layer}')");

            foreach (var t in sm.EntryLadder) CheckConditions(t, layer, "entry", paramTypes, errors);
            foreach (var t in sm.AnyLadder) CheckConditions(t, layer, "any", paramTypes, errors);

            foreach (var st in sm.States)
            {
                if (st == null) continue;
                if (st.Motion != null) CheckMotionClips(st.Motion, $"state '{st.Name}'", $"layer '{layer}' state '{st.Name}'", clips, errors);
                if (st.Motion != null) CheckBlendAxes(st.Motion, $"layer '{layer}' state '{st.Name}'", paramTypes, errors);
                foreach (var t in st.Transitions)
                    CheckConditions(t, layer, $"state '{st.Name}'", paramTypes, errors);
            }

            foreach (var sub in sm.Machines)
            {
                if (sub == null) continue;
                foreach (var t in sub.OnExit) CheckConditions(t, layer, $"onExit of '{sub.Name}'", paramTypes, errors);
                if (sub.Machine != null) WalkMachine(sub.Machine, layer, paramTypes, clips, errors, layerRoot);
            }

            if (sm.Layout != null)
                foreach (var key in sm.Layout.Nodes.Keys)
                {
                    // MachineHasMember unescapes for itself, so it takes the escaped key. UnescapeSegment is not
                    // idempotent: `A\\B` unescapes to `A\B`, and a second pass strips that to `AB`.
                    var raw = AddressPath.UnescapeSegment(key);
                    if (!MachineHasMember(sm, key))
                        errors.Add($"# dangling-layout: layer '{layer}' layout node '{key}' names no state or submachine of its machine (at layer '{layer}')");
                    // A non-canonical key (a '/'-named node written literally) resolves to a real member here but
                    // misses emit's EscapeSegment lookup, which would silently drop the authored position to the grid.
                    else if (AddressPath.EscapeSegment(raw) != key)
                        errors.Add($"# unescaped-layout: layer '{layer}' layout node '{key}' must be canonically escaped as '{AddressPath.EscapeSegment(raw)}' (at layer '{layer}')");
                }
        }

        private static void CheckConditions(Transition t, string layer, string origin,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (t == null) return;
            string target = t.ToExit ? "Exit" : (t.To ?? "Exit");
            string loc = $"layer '{layer}' {origin} → '{target}'";
            foreach (var c in t.When)
            {
                if (c.Param == null || !paramTypes.TryGetValue(c.Param, out var type)) continue;
                if (!OpValidForType(c.Op, type))
                    errors.Add($"# condition-op-type: param '{c.Param}' ({TypeToken(type)}) cannot use operator '{OpToken(c.Op)}' (at {loc})");
            }
        }

        // The `shared:` names directly under one motion. Recurses inline tree children only, never through a
        // `shared:`, so a cycle terminates here.
        private static List<string> SharedRefsIn(MotionRef m, List<string> unknown, HashSet<string> declared)
        {
            var found = new List<string>();
            void Walk(MotionRef mr)
            {
                if (mr == null) return;
                if (mr.Shared != null)
                {
                    if (declared.Contains(mr.Shared)) { if (!found.Contains(mr.Shared)) found.Add(mr.Shared); }
                    else if (!unknown.Contains(mr.Shared)) unknown.Add(mr.Shared);
                    return;
                }
                if (mr.Tree != null)
                    foreach (var ch in mr.Tree.Children)
                        if (ch != null) Walk(ch.Motion);
            }
            Walk(m);
            return found;
        }

        private static void CheckMotionClips(MotionRef m, string subject, string at,
            HashSet<string> clips, List<string> errors)
        {
            if (m == null) return;
            if (m.Clip != null && !clips.Contains(m.Clip))
                errors.Add($"# dangling-clip: {subject} references clip '{m.Clip}' which is not declared (at {at})");
            if (m.Tree != null)
                foreach (var child in m.Tree.Children)
                    if (child != null) CheckMotionClips(child.Motion, subject, at, clips, errors);
        }

        // Unity silently freezes a non-float blend-tree axis at its first child, with no error anywhere.
        private static void CheckBlendAxes(MotionRef m, string at,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (m == null || m.Tree == null) return;
            CheckTreeAxes(m.Tree, at, paramTypes, errors);
        }

        private static void CheckTreeAxes(BlendTreeSpec t, string at,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (t == null) return;
            if (t.Kind == TreeKind.Direct)
            {
                foreach (var ch in t.Children)
                    if (ch != null) RequireFloatAxis(ch.DirectWeight, at, paramTypes, errors);
            }
            else
            {
                RequireFloatAxis(t.Param, at, paramTypes, errors);
                if (t.Kind != TreeKind.OneD) RequireFloatAxis(t.ParamY, at, paramTypes, errors);
            }
            foreach (var ch in t.Children)
                if (ch != null && ch.Motion != null && ch.Motion.Tree != null)
                    CheckTreeAxes(ch.Motion.Tree, at, paramTypes, errors);
        }

        private static void RequireFloatAxis(string param, string at,
            Dictionary<string, AnimParamType> paramTypes, List<string> errors)
        {
            if (string.IsNullOrEmpty(param)) return;
            if (!paramTypes.TryGetValue(param, out var type)) return;
            if (type != AnimParamType.Float)
                errors.Add($"# blend-axis-type: param '{param}' ({TypeToken(type)}) is a blend-tree axis but must be float; declare it 'type: float' and sync int via 'vrc: {{ type: int }}' (at {at})");
        }

        // `name` arrives escaped (a '/' in a name is '\/') while States and Machines carry the raw literal.
        private static bool MachineHasMember(StateMachine sm, string name)
        {
            string raw = AddressPath.UnescapeSegment(name);
            foreach (var s in sm.States) if (s != null && s.Name == raw) return true;
            foreach (var m in sm.Machines) if (m != null && m.Name == raw) return true;
            return false;
        }

        // `defaultState:` is a root-relative path. Segment count comes from AddressPath.Split (unescaped
        // separators only), never Contains('/'), which would read the escaped single name `A\/B` as two segments.
        private static bool RootHasStateAtPath(StateMachine root, string path)
        {
            if (path.Length > 0 && path[0] == '/') path = path.Substring(1);
            var segs = AddressPath.Split(path);
            if (segs.Count == 0) return false;
            var cur = root;
            for (int i = 0; i < segs.Count - 1; i++)
            {
                string seg = AddressPath.UnescapeSegment(segs[i]);
                SubMachine next = null;
                foreach (var m in cur.Machines) if (m != null && m.Name == seg) { next = m; break; }
                if (next == null) return false;
                cur = next.Machine;
            }
            string leaf = AddressPath.UnescapeSegment(segs[segs.Count - 1]);
            foreach (var s in cur.States) if (s != null && s.Name == leaf) return true;
            return false;   // a sub-machine leaf is a real error too — Unity's default is a STATE reference
        }

        // Float equality is invalid in Unity animator conditions, so Float allows only Greater/Less.
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
