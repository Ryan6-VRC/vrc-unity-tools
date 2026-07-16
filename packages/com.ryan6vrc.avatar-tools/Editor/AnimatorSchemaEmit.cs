using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// The WRITE-BACK direction, inverse of <see cref="AnimatorSchemaYaml"/>: renders a typed
    /// <see cref="AnimDocument"/> as YAML text in exactly the bounded block/flow subset that
    /// <see cref="AnimatorSchemaYaml.Parse"/> accepts. The parser is the SPEC — <c>Parse(Serialize(doc))</c>
    /// reproduces <paramref name="doc"/> structurally, so this only emits constructs the reader binds and
    /// quotes exactly the scalars the reader would otherwise mis-infer (a value that reads as bool/number/null,
    /// or a token that would break tokenization). System.* only (no Unity API), like the parser it inverts, so
    /// the codec pair is unit-checkable outside the editor.
    ///
    /// DETERMINISTIC + CANONICAL — the Decompile→Compile→Decompile fixpoint compares two serialized
    /// strings for textual identity, so the SAME document must always render byte-identically:
    ///   - Fixed key order per construct; map/list order is the model's insertion order (already stable).
    ///   - '\n' line endings only (never Environment.NewLine), 2-space indent, no tabs.
    ///   - One number formatting for every numeric type: integral values render without a decimal point,
    ///     fractional values via the shortest round-trip decimal — and float values are normalized THROUGH
    ///     that decimal so a value re-read as double re-renders identically (serialize is idempotent through a
    ///     parse: <c>Serialize(Parse(Serialize(doc))) == Serialize(doc)</c>).
    ///
    /// Never emits an unsupported construct (anchors/aliases/tags/block scalars/multi-doc/tabs); GUIDs are
    /// always quoted (an all-hex handle can read as a number); the reserved <c>_notes</c> block is rendered
    /// last and is compile-ignored on re-parse (the parser skips <c>_</c>-prefixed top-level keys).
    /// </summary>
    public static class AnimatorSchemaEmit
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Serialize(AnimDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            var sb = new StringBuilder();

            L(sb, "schema: " + doc.Schema.ToString(Inv));
            L(sb, "controller: " + ScalarStr(doc.ControllerName));
            L(sb, "basis: " + BasisToken(doc.Basis));
            L(sb, "role: " + RoleToken(doc.Role));
            EmitDefaults(sb, doc.Defaults);
            if (doc.Parameters.Count > 0) EmitParameters(sb, doc.Parameters);
            if (doc.Layers.Count > 0) EmitLayers(sb, doc.Layers);
            if (doc.Clips.Count > 0) EmitClips(sb, doc.Clips);
            EmitNotes(sb, doc.ReservedNotes);

            return sb.ToString();
        }

        // ===================================================================================
        // Document sections
        // ===================================================================================

        private static void EmitDefaults(StringBuilder sb, Defaults d)
        {
            L(sb, "defaults:");
            L(sb, "  writeDefaults: " + Bool(d.WriteDefaults));
            // exitTime in defaults only carries the has-exit-time bool, and the parser accepts only 'none'
            // there (the sole value in scope) — so it is always rendered 'none'.
            L(sb, "  transition: { duration: " + Num(d.TransitionDuration)
                  + ", exitTime: none, interruption: " + InterruptionToken(d.Interruption) + " }");
        }

        private static void EmitParameters(StringBuilder sb, List<ParamSpec> parameters)
        {
            L(sb, "parameters:");
            foreach (var p in parameters)
            {
                if (IsShorthand(p)) L(sb, "  " + Key(p.Name) + ": " + ParamTypeToken(p.Type));
                else L(sb, "  " + Key(p.Name) + ": " + ParamLongFlow(p));
            }
        }

        private static bool IsShorthand(ParamSpec p)
            => p.Default == 0f && !p.Aap && !p.Scratch && p.Vrc == null;

        private static string ParamLongFlow(ParamSpec p)
        {
            var parts = new List<string> { "type: " + ParamTypeToken(p.Type) };
            if (p.Default != 0f)
                parts.Add("default: " + (p.Type == AnimParamType.Bool ? Bool(p.Default != 0f) : Num(p.Default)));
            if (p.Aap) parts.Add("aap: true");
            if (p.Scratch) parts.Add("scratch: true");
            if (p.Vrc != null) parts.Add("vrc: " + VrcFlow(p.Vrc));
            return "{ " + string.Join(", ", parts) + " }";
        }

        private static string VrcFlow(VrcParamMeta v)
        {
            var parts = new List<string>();
            if (v.Synced) parts.Add("synced: true");
            if (v.Saved) parts.Add("saved: true");
            if (v.Osc) parts.Add("osc: true");
            if (v.VrcType.HasValue) parts.Add("type: " + ParamTypeToken(v.VrcType.Value));
            return parts.Count == 0 ? "{}" : "{ " + string.Join(", ", parts) + " }";
        }

        // ----- clips -----

        private static void EmitClips(StringBuilder sb, List<ClipSpec> clips)
        {
            L(sb, "clips:");
            foreach (var c in clips)
            {
                if (c.Curves.Count > 0)
                {
                    // Keyframed clips render in block form: one binding per line.
                    L(sb, "  " + Key(c.Name) + ":");
                    if (c.Seconds.HasValue) L(sb, "    seconds: " + Num(c.Seconds.Value));
                    if (c.Sets.Count > 0) L(sb, "    set: " + FlowSets(c.Sets));
                    L(sb, "    curves:");
                    foreach (var cs in c.Curves) L(sb, "      " + Key(cs.Binding) + ": " + FlowCurve(cs));
                }
                else
                {
                    L(sb, "  " + Key(c.Name) + ": " + ClipFlow(c));
                }
            }
        }

        private static string ClipFlow(ClipSpec c)
        {
            var parts = new List<string>();
            if (c.Seconds.HasValue) parts.Add("seconds: " + Num(c.Seconds.Value));
            if (c.Sets.Count > 0) parts.Add("set: " + FlowSets(c.Sets));
            return parts.Count == 0 ? "{}" : "{ " + string.Join(", ", parts) + " }";
        }

        private static string FlowSets(Dictionary<string, float> sets)
        {
            if (sets.Count == 0) return "{}";
            var parts = new List<string>();
            foreach (var kv in sets) parts.Add(Key(kv.Key) + ": " + Num(kv.Value));
            return "{ " + string.Join(", ", parts) + " }";
        }

        private static string FlowCurveKeys(List<Keyframe2> keys)
        {
            if (keys.Count == 0) return "[]";
            var parts = new List<string>();
            foreach (var k in keys) parts.Add("[" + Num(k.Time) + ", " + Num(k.Value) + "]");
            return "[ " + string.Join(", ", parts) + " ]";
        }

        // Flat (default) curves keep the bare-list form byte-identical to before linear tangents existed;
        // only a Linear/Stepped-tangent curve pays for the map form, matching AnimatorSchemaYaml.BindCurve's reader.
        private static string FlowCurve(CurveSpec cs)
        {
            var keysFlow = FlowCurveKeys(cs.Keys);
            return cs.Tangents == CurveTangent.Flat
                ? keysFlow
                : "{ tangents: " + TangentToken(cs.Tangents) + ", keys: " + keysFlow + " }";
        }

        private static string TangentToken(CurveTangent t) => t switch
        {
            CurveTangent.Linear  => "linear",
            CurveTangent.Stepped => "stepped",
            _ => throw new System.InvalidOperationException($"flat curve uses the bare-list form, not the map form (got {t})"),
        };

        // ----- layers / machines / states -----

        private static void EmitLayers(StringBuilder sb, List<Layer> layers)
        {
            L(sb, "layers:");
            foreach (var layer in layers)
            {
                // First key of a block-sequence item carries the "- "; the dash sits at indent 2, its keys at 4.
                L(sb, "  - name: " + ScalarStr(layer.Name));
                const int f = 4;
                if (layer.Weight != 1f) L(sb, Sp(f) + "weight: " + Num(layer.Weight));
                if (!string.IsNullOrEmpty(layer.Mask)) L(sb, Sp(f) + "mask: " + ScalarStr(layer.Mask));
                if (layer.Blend == LayerBlend.Additive) L(sb, Sp(f) + "blend: additive");
                if (layer.WriteDefaults.HasValue) L(sb, Sp(f) + "writeDefaults: " + Bool(layer.WriteDefaults.Value));
                EmitMachineBody(sb, layer.Root, f);
            }
        }

        // The machine-body keys shared by a layer's Root machine and every nested sub-machine, in fixed order.
        private static void EmitMachineBody(StringBuilder sb, StateMachine sm, int f)
        {
            if (sm.States.Count > 0)
            {
                L(sb, Sp(f) + "states:");
                foreach (var st in sm.States) EmitState(sb, st, f + 2);
            }
            if (sm.Machines.Count > 0)
            {
                L(sb, Sp(f) + "machines:");
                foreach (var sub in sm.Machines)
                {
                    if (IsMachineEmpty(sub.Machine)) { L(sb, Sp(f + 2) + Key(sub.Name) + ": {}"); continue; }
                    L(sb, Sp(f + 2) + Key(sub.Name) + ":");
                    EmitMachineBody(sb, sub.Machine, f + 4);
                }
            }
            if (sm.EntryLadder.Count > 0)
            {
                L(sb, Sp(f) + "entry:");
                foreach (var t in sm.EntryLadder) L(sb, Sp(f + 2) + "- " + FlowTransition(t, anyLadder: false));
            }
            if (sm.AnyLadder.Count > 0)
            {
                L(sb, Sp(f) + "any:");
                foreach (var t in sm.AnyLadder) L(sb, Sp(f + 2) + "- " + FlowTransition(t, anyLadder: true));
            }
            if (sm.Behaviours.Count > 0)
            {
                L(sb, Sp(f) + "behaviours:");
                foreach (var b in sm.Behaviours) L(sb, Sp(f + 2) + "- " + FlowBehaviour(b));
            }
            if (sm.DefaultState != null) L(sb, Sp(f) + "default: " + ScalarStr(sm.DefaultState));
            if (sm.Layout != null) EmitLayout(sb, sm, f);
        }

        // Emit nodes in States-then-Machines order (deterministic — Dictionary order is not contractual).
        private static void EmitLayout(StringBuilder sb, StateMachine sm, int f)
        {
            var l = sm.Layout;
            L(sb, Sp(f) + "layout:");
            var parts = new List<string>();
            foreach (var st in sm.States)
                if (st != null && l.Nodes.TryGetValue(AddressPath.EscapeSegment(st.Name), out var xy))
                    parts.Add(Key(AddressPath.EscapeSegment(st.Name)) + ": " + Coord(xy));
            foreach (var sub in sm.Machines)
                if (sub != null && l.Nodes.TryGetValue(AddressPath.EscapeSegment(sub.Name), out var xy))
                    parts.Add(Key(AddressPath.EscapeSegment(sub.Name)) + ": " + Coord(xy));
            if (parts.Count > 0) L(sb, Sp(f + 2) + "nodes: { " + string.Join(", ", parts) + " }");
            if (l.Entry  != null) L(sb, Sp(f + 2) + "entry: "  + Coord(l.Entry));
            if (l.Any    != null) L(sb, Sp(f + 2) + "any: "    + Coord(l.Any));
            if (l.Exit   != null) L(sb, Sp(f + 2) + "exit: "   + Coord(l.Exit));
            if (l.Parent != null) L(sb, Sp(f + 2) + "parent: " + Coord(l.Parent));
        }

        private static string Coord(float[] xy)
            => "[" + Num(xy[0]) + ", " + Num(xy[1]) + "]";

        private static bool IsMachineEmpty(StateMachine sm)
            => sm.States.Count == 0 && sm.Machines.Count == 0 && sm.EntryLadder.Count == 0
               && sm.AnyLadder.Count == 0 && sm.Behaviours.Count == 0 && sm.DefaultState == null
               && sm.Layout == null;

        private static void EmitState(StringBuilder sb, State st, int f)
        {
            L(sb, Sp(f) + Key(st.Name) + ":");
            int g = f + 2;
            if (st.Motion == null) L(sb, Sp(g) + "motion: ~");
            else EmitMotion(sb, st.Motion, g);
            if (st.Speed != 1f) L(sb, Sp(g) + "speed: " + Num(st.Speed));
            if (!string.IsNullOrEmpty(st.SpeedParam)) L(sb, Sp(g) + "speedParam: " + ScalarStr(st.SpeedParam));
            if (!string.IsNullOrEmpty(st.MotionTimeParam)) L(sb, Sp(g) + "motionTimeParam: " + ScalarStr(st.MotionTimeParam));
            if (st.Mirror) L(sb, Sp(g) + "mirror: true");
            if (st.WriteDefaults.HasValue) L(sb, Sp(g) + "writeDefaults: " + Bool(st.WriteDefaults.Value));
            if (st.Behaviours.Count > 0)
            {
                L(sb, Sp(g) + "behaviours:");
                foreach (var b in st.Behaviours) L(sb, Sp(g + 2) + "- " + FlowBehaviour(b));
            }
            if (st.Transitions.Count > 0)
            {
                L(sb, Sp(g) + "transitions:");
                foreach (var t in st.Transitions) L(sb, Sp(g + 2) + "- " + FlowTransition(t, anyLadder: false));
            }
        }

        // ----- motions / blend trees -----

        private static void EmitMotion(StringBuilder sb, MotionRef mr, int g)
        {
            if (mr.Tree != null)
            {
                L(sb, Sp(g) + "motion:");
                EmitTreeBlock(sb, mr.Tree, g + 2);
            }
            else
            {
                L(sb, Sp(g) + "motion: " + FlowMotion(mr));
            }
        }

        private static string FlowMotion(MotionRef mr)
        {
            if (mr.Clip != null) return "{ clip: " + ScalarStr(mr.Clip) + " }";
            if (mr.RefPath != null) return "{ ref: " + ScalarStr(mr.RefPath) + " }";
            if (mr.RefGuid != null) return "{ ref: " + FlowGuid(mr.RefGuid) + " }";
            return "{}"; // degenerate: a motion that sets nothing (the parser refuses it, upstream's concern)
        }

        private static string FlowGuid(GuidRef g)
        {
            var parts = new List<string> { "guid: " + (g.Guid == null ? "~" : Quote(g.Guid)) };
            if (g.FileID != 0) parts.Add("fileID: " + g.FileID.ToString(Inv));
            if (g.Unresolved) parts.Add("unresolved: true");
            return "{ " + string.Join(", ", parts) + " }";
        }

        // The state's own blend tree renders in block form (the human-facing surface); nested child
        // trees render inline (flow) so recursion stays a single-line value.
        private static void EmitTreeBlock(StringBuilder sb, BlendTreeSpec spec, int indent)
        {
            L(sb, Sp(indent) + "tree: " + TreeKindToken(spec.Kind));
            if (!string.IsNullOrEmpty(spec.Name)) L(sb, Sp(indent) + "name: " + ScalarStr(spec.Name));
            if (!string.IsNullOrEmpty(spec.Param)) L(sb, Sp(indent) + "param: " + ScalarStr(spec.Param));
            if (!string.IsNullOrEmpty(spec.ParamY)) L(sb, Sp(indent) + "paramY: " + ScalarStr(spec.ParamY));
            if (spec.Normalized.HasValue) L(sb, Sp(indent) + "normalized: " + Bool(spec.Normalized.Value));
            if (spec.Children.Count == 0)
            {
                L(sb, Sp(indent) + "children: []");
                return;
            }
            L(sb, Sp(indent) + "children:");
            foreach (var c in spec.Children) L(sb, Sp(indent + 2) + "- " + FlowChild(c, spec.Kind));
        }

        // A child map carries its motion (clip/ref/nested-tree) plus its parent-kind-specific placement and
        // per-child modifiers. A nested tree splices its tree-level keys in beside the placement — the parser's
        // BindTreeChild reads the placement/modifiers and BindTree reads the tree keys off the same map.
        private static string FlowChild(TreeChild ch, TreeKind parentKind)
        {
            var parts = new List<string>();
            var mr = ch.Motion;
            if (mr != null)
            {
                if (mr.Clip != null) parts.Add("clip: " + ScalarStr(mr.Clip));
                else if (mr.RefPath != null) parts.Add("ref: " + ScalarStr(mr.RefPath));
                else if (mr.RefGuid != null) parts.Add("ref: " + FlowGuid(mr.RefGuid));
                else if (mr.Tree != null) parts.AddRange(TreeInner(mr.Tree));
            }
            switch (parentKind)
            {
                case TreeKind.OneD: parts.Add("threshold: " + Num(ch.Threshold)); break;
                case TreeKind.Direct:
                    if (!string.IsNullOrEmpty(ch.DirectWeight)) parts.Add("directWeight: " + ScalarStr(ch.DirectWeight));
                    break;
                default: parts.Add("x: " + Num(ch.PosX)); parts.Add("y: " + Num(ch.PosY)); break;
            }
            if (ch.TimeScale != 1f) parts.Add("timeScale: " + Num(ch.TimeScale));
            if (ch.Mirror) parts.Add("mirror: true");
            if (ch.CycleOffset != 0f) parts.Add("cycleOffset: " + Num(ch.CycleOffset));
            return "{ " + string.Join(", ", parts) + " }";
        }

        // The tree-level fields of a nested (flow) tree, without the surrounding braces.
        private static List<string> TreeInner(BlendTreeSpec spec)
        {
            var parts = new List<string> { "tree: " + TreeKindToken(spec.Kind) };
            if (!string.IsNullOrEmpty(spec.Name)) parts.Add("name: " + ScalarStr(spec.Name));
            if (!string.IsNullOrEmpty(spec.Param)) parts.Add("param: " + ScalarStr(spec.Param));
            if (!string.IsNullOrEmpty(spec.ParamY)) parts.Add("paramY: " + ScalarStr(spec.ParamY));
            if (spec.Normalized.HasValue) parts.Add("normalized: " + Bool(spec.Normalized.Value));
            if (spec.Children.Count == 0)
            {
                parts.Add("children: []");
            }
            else
            {
                var kids = new List<string>();
                foreach (var c in spec.Children) kids.Add(FlowChild(c, spec.Kind));
                parts.Add("children: [ " + string.Join(", ", kids) + " ]");
            }
            return parts;
        }

        // ----- transitions -----

        private static string FlowTransition(Transition t, bool anyLadder)
        {
            var parts = new List<string> { "to: " + (t.ToExit ? "Exit" : ScalarStr(t.To)) };
            if (!string.IsNullOrEmpty(t.Name)) parts.Add("name: " + ScalarStr(t.Name));
            parts.Add("when: " + WhenList(t.When));
            if (t.ExitTime.HasValue) parts.Add("exitTime: " + Num(t.ExitTime.Value));
            if (t.Duration.HasValue) parts.Add("duration: " + Num(t.Duration.Value));
            if (t.FixedDuration.HasValue) parts.Add("fixedDuration: " + Bool(t.FixedDuration.Value));
            if (t.Interruption.HasValue) parts.Add("interruption: " + InterruptionToken(t.Interruption.Value));
            if (t.OrderedInterruption.HasValue) parts.Add("ordered: " + Bool(t.OrderedInterruption.Value));
            if (t.Mute) parts.Add("mute: true");
            if (t.Solo) parts.Add("solo: true");
            if (anyLadder) parts.Add("canTransitionToSelf: " + Bool(t.CanTransitionToSelf));
            return "{ " + string.Join(", ", parts) + " }";
        }

        private static string WhenList(List<Condition> conds)
        {
            if (conds.Count == 0) return "[]";
            var strs = new List<string>();
            foreach (var c in conds) strs.Add(CondString(c));
            return "[ " + string.Join(", ", strs) + " ]";
        }

        // '<param> <op> <value>' — op and value are the LAST two space-separated tokens; everything before
        // is the param, verbatim (the parser right-anchors, so interior spaces stay unquoted and legible).
        // The whole string routes through ScalarStr, so a param carrying a flow-delimiter/comment/leader
        // hazard emits as ONE quoted scalar under the same predicate as every other scalar in the schema.
        // Exposed raw (unquoted) for the decompiler's self-check, which re-splits it with the parser's own
        // grammar to refuse a condition that can't survive its serialized form.
        internal static string RawCondString(Condition c)
        {
            string val = (c.Op == CondOp.Is || c.Op == CondOp.IsNot)
                ? (c.Value != 0f ? "true" : "false")
                : Num(c.Value);
            return c.Param + " " + CondOpToken(c.Op) + " " + val;
        }

        private static string CondString(Condition c) => ScalarStr(RawCondString(c));

        // ----- behaviours -----

        private static string FlowBehaviour(Behaviour b)
            => b.Kind + ": " + FlowMap(b.Fields);

        // ===================================================================================
        // Generic neutral-value emitter (behaviour Fields + the _notes block)
        // ===================================================================================

        private static string FlowValue(object v)
        {
            switch (v)
            {
                case null: return "~";
                case bool b: return Bool(b);
                case string s: return ScalarStr(s);
                case Dictionary<string, object> m: return FlowMap(m);
                case List<object> l: return FlowList(l);
                default: return Num(v); // long/int/float/double
            }
        }

        private static string FlowMap(Dictionary<string, object> m)
        {
            if (m.Count == 0) return "{}";
            var parts = new List<string>();
            foreach (var kv in m) parts.Add(Key(kv.Key) + ": " + FlowValue(kv.Value));
            return "{ " + string.Join(", ", parts) + " }";
        }

        private static string FlowList(List<object> l)
        {
            if (l.Count == 0) return "[]";
            var parts = new List<string>();
            foreach (var v in l) parts.Add(FlowValue(v));
            return "[ " + string.Join(", ", parts) + " ]";
        }

        // ----- _notes -----

        // Renders every ReservedNotes entry inside a single top-level `_notes:` block (the decompiler's output
        // channel). The entry keyed exactly "_notes" (what a prior parse stores the whole block under) is
        // unwrapped so a round-tripped document does not gain a nesting level each pass — the fixpoint the
        // reserved-key contract exists for. Re-parsing skips `_`-prefixed top-level keys, so this is inert.
        private static void EmitNotes(StringBuilder sb, Dictionary<string, object> notes)
        {
            if (notes.Count == 0) return;
            var contents = new Dictionary<string, object>();
            foreach (var kv in notes)
            {
                if (kv.Key == "_notes" && kv.Value is Dictionary<string, object> inner)
                    foreach (var ik in inner) contents[ik.Key] = ik.Value;
                else
                    contents[kv.Key] = kv.Value;
            }
            L(sb, "_notes: " + FlowMap(contents));
        }

        // ===================================================================================
        // Scalars, quoting, numbers
        // ===================================================================================

        private static string Key(string s) => ScalarStr(s);

        private static string ScalarStr(string s)
        {
            if (s == null) return "~";
            CheckNoLineBreak(s);
            return NeedsQuote(s) ? Quote(s) : s;
        }

        // The line-based reader cannot carry a literal newline in any scalar and Quote does not escape
        // one, so emitting it would write torn YAML. Every string field funnels through ScalarStr/Quote,
        // so the guard lives here — one choke point instead of per-decode-site checks that rot as fields
        // are added. DecompileController catches this into a named FAIL.
        private static void CheckNoLineBreak(string s)
        {
            if (s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return;
            throw new SchemaException("'" + s.Replace("\r", "\\r").Replace("\n", "\\n")
                + "' contains a line break, which cannot round-trip the line-based YAML");
        }

        // Quote when the raw token would NOT read back as the same string: it infers to bool/number/null, is
        // empty, has edge whitespace, opens with a reserved/ambiguous leader, or contains a char that breaks
        // block or flow tokenization. Quoting is always safe (the reader strips quotes to the literal) and a
        // pure function of the string, so it stays idempotent.
        private static bool NeedsQuote(string s)
        {
            if (s.Length == 0) return true;
            if (InfersNonString(s)) return true;
            if (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[s.Length - 1])) return true;
            if ("&*!|>?@`%#-".IndexOf(s[0]) >= 0) return true;
            foreach (char c in s)
                if (":#,[]{}\"'".IndexOf(c) >= 0) return true;
            return false;
        }

        // Mirror of AnimatorSchemaYaml.InferScalar: does this token read as something other than a string?
        private static bool InfersNonString(string t)
        {
            if (t == "~") return true;
            string lower = t.ToLowerInvariant();
            if (lower == "true" || lower == "false" || lower == "on" || lower == "off") return true;
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, Inv, out _)) return true;
            if (double.TryParse(t, NumberStyles.Float, Inv, out _)) return true;
            return false;
        }

        // Double-quoted form: the reader unescapes \" and \\, so those are the only escapes emitted.
        // (Direct callers — GUID rendering — get the line-break guard here; ScalarStr guards its own.)
        private static string Quote(string s)
        {
            CheckNoLineBreak(s);
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Bool(bool b) => b ? "true" : "false";

        private static string Num(object v)
        {
            switch (v)
            {
                case long l: return l.ToString(Inv);
                case int i: return i.ToString(Inv);
                case float f: return NumFromFloat(f);
                case double d: return NumFromDouble(d);
                default: return "0";
            }
        }

        private static string NumFromDouble(double d)
        {
            // Intentional lossy clamp: NaN/Infinity have no token in the accepted subset and would emit
            // unparseable text. A decompiled graph can't author them (Unity stores finite floats), so 0 keeps
            // output parseable rather than silently corrupting it — not an accidental data change.
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(Inv);
            return d.ToString("R", Inv);
        }

        // A float is normalized THROUGH its shortest decimal into the double formatter, so a value serialized
        // from a float and the same value re-read as a double (the parser yields double) render identically —
        // the guarantee idempotence-through-a-parse rests on.
        private static string NumFromFloat(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "0"; // intentional lossy clamp (see NumFromDouble)
            if (f == Math.Floor(f) && Math.Abs(f) < 1e15f) return ((long)f).ToString(Inv);
            return NumFromDouble(double.Parse(f.ToString("R", Inv), Inv));
        }

        // ===================================================================================
        // Enum tokens (the exact surface AnimatorSchemaYaml binds)
        // ===================================================================================

        private static string BasisToken(BindingBasis b)
            => b == BindingBasis.MountRoot ? "mount-root" : "avatar-root";

        private static string RoleToken(ControllerRole r)
        {
            switch (r)
            {
                case ControllerRole.Fx: return "fx";
                case ControllerRole.BaseFx: return "base-fx";
                case ControllerRole.Gesture: return "gesture";
                case ControllerRole.Action: return "action";
                case ControllerRole.Sitting: return "sitting";
                case ControllerRole.TPose: return "tpose";
                case ControllerRole.IkPose: return "ikpose";
                case ControllerRole.Additive: return "additive";
                case ControllerRole.Base: return "base";
                default: return "fx";
            }
        }

        private static string InterruptionToken(TransitionInterruption i)
        {
            switch (i)
            {
                case TransitionInterruption.None: return "none";
                case TransitionInterruption.Source: return "source";
                case TransitionInterruption.Destination: return "destination";
                case TransitionInterruption.SourceThenDestination: return "sourceThenDestination";
                case TransitionInterruption.DestinationThenSource: return "destinationThenSource";
                default: return "none";
            }
        }

        private static string ParamTypeToken(AnimParamType t)
        {
            switch (t)
            {
                case AnimParamType.Bool: return "bool";
                case AnimParamType.Int: return "int";
                default: return "float";
            }
        }

        private static string TreeKindToken(TreeKind k)
        {
            switch (k)
            {
                case TreeKind.OneD: return "1d";
                case TreeKind.SimpleDirectional2D: return "simpleDirectional2d";
                case TreeKind.FreeformDirectional2D: return "freeformDirectional2d";
                case TreeKind.FreeformCartesian2D: return "freeformCartesian2d";
                default: return "direct";
            }
        }

        private static string CondOpToken(CondOp op)
        {
            switch (op)
            {
                case CondOp.Is: return "is";
                case CondOp.IsNot: return "isNot";
                case CondOp.Greater: return "greater";
                case CondOp.Less: return "less";
                case CondOp.Equals: return "equals";
                default: return "notEqual";
            }
        }

        // ===================================================================================
        // Low-level output
        // ===================================================================================

        private static void L(StringBuilder sb, string line) => sb.Append(line).Append('\n');

        private static string Sp(int n) => new string(' ', n);
    }
}
