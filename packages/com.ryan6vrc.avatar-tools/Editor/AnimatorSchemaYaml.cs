using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Parses an animator-schema YAML document into a typed <see cref="AnimDocument"/>. Hand-rolled
    /// bounded-subset parser: no external YAML library (the package must stay portable + dup-assembly-free),
    /// and JSON is not an option because the schema's comments are load-bearing. Depends on System.* only —
    /// no UnityEngine/UnityEditor — so the model + parser can be unit-checked outside the editor.
    ///
    /// Two stages, kept as separate methods so stage 1 is reasoned independently:
    ///   1. <see cref="BuildNeutralTree"/> — tokenize the supported subset into a neutral tree of
    ///      Dictionary&lt;string,object&gt; (mappings), List&lt;object&gt; (sequences), and scalars
    ///      (string / bool / long / double / null). Refuses duplicate map keys and unsupported constructs.
    ///   2. <see cref="Bind"/> — walk the neutral tree into <see cref="AnimDocument"/>, enforcing schema
    ///      semantics. Every violation throws a <see cref="SchemaException"/> naming the offender.
    ///
    /// SUPPORTED YAML SUBSET (this is the fail-loud boundary):
    ///   - Block mappings (2-space indentation) and block sequences ("- ").
    ///   - Flow mappings "{ k: v, ... }" and flow sequences "[ a, b ]", nestable.
    ///   - Scalar type inference: true/false/on/off -> bool; integer -> long; decimal/exponent -> double;
    ///     "~" or empty -> null; otherwise string. Map keys are always strings (never inferred).
    ///   - Single- and double-quoted strings (quotes stripped; in double quotes \" and \\ are unescaped).
    ///   - "#" to end-of-line is a comment, except inside quotes.
    ///   REFUSED, by name + line: anchors "&", aliases "*", explicit tags "!", block scalars "|"/">",
    ///   multi-doc "---"/"...", complex "?" keys, tab indentation.
    /// </summary>
    public static class AnimatorSchemaYaml
    {
        public static AnimDocument Parse(string text, string sourcePath)
        {
            object root = BuildNeutralTree(text ?? string.Empty);
            return Bind(root, sourcePath);
        }

        // ===================================================================================
        // Stage 1 — neutral tree builder
        // ===================================================================================

        private sealed class Line
        {
            public readonly int No;      // 1-based source line number (for messages)
            public int Indent;           // leading-space count of the content
            public string Text;          // content with leading indent + trailing comment removed
            public Line(int no, int indent, string text) { No = no; Indent = indent; Text = text; }
        }

        private static object BuildNeutralTree(string text)
        {
            var lines = Prescan(text);
            if (lines.Count == 0) return new Dictionary<string, object>();
            int i = 0;
            object root = ParseNode(lines, ref i, lines[0].Indent);
            if (i < lines.Count)
                throw new SchemaException($"unexpected indentation at line {lines[i].No}");
            return root;
        }

        // Split into significant logical lines: strip comments, drop blank/comment-only lines, compute
        // indentation, and refuse the line-level unsupported constructs (multi-doc markers, complex keys,
        // tab indentation).
        private static List<Line> Prescan(string text)
        {
            var result = new List<Line>();
            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int n = 0; n < raw.Length; n++)
            {
                string line = StripComment(raw[n]);
                int lineNo = n + 1;

                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;
                if (indent < line.Length && line[indent] == '\t')
                    throw new SchemaException($"unsupported tab indentation at line {lineNo}");

                string content = line.Substring(indent).TrimEnd();
                if (content.Length == 0) continue;   // blank or comment-only

                if (content == "---" || content == "..." || content.StartsWith("--- "))
                    throw new SchemaException($"unsupported multi-document marker '{content}' at line {lineNo}");
                if (content == "?" || content.StartsWith("? "))
                    throw new SchemaException($"unsupported complex '?' key at line {lineNo}");

                result.Add(new Line(lineNo, indent, content));
            }
            return result;
        }

        // Remove a "#...to end of line" comment, but not one inside single/double quotes and only when the
        // "#" begins a token (start of line or preceded by whitespace).
        private static string StripComment(string line)
        {
            bool inS = false, inD = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inS) { if (c == '\'') inS = false; continue; }
                if (inD) { if (c == '\\') { i++; continue; } if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '#' && (i == 0 || line[i - 1] == ' ' || line[i - 1] == '\t'))
                    return line.Substring(0, i);
            }
            return line;
        }

        private static bool IsSeqLine(string text) => text == "-" || text.StartsWith("- ");

        private static object ParseNode(List<Line> lines, ref int i, int indent)
        {
            return IsSeqLine(lines[i].Text)
                ? (object)ParseSequence(lines, ref i, indent)
                : ParseMapping(lines, ref i, indent);
        }

        private static Dictionary<string, object> ParseMapping(List<Line> lines, ref int i, int indent)
        {
            // Parameter/state/clip ORDER is the Dictionary's insertion-order enumeration (keys are only ever
            // added, never removed, so it stays stable). A future edit that removes+re-adds keys would break
            // that ordering contract — use an order-preserving structure if that ever changes.
            var map = new Dictionary<string, object>();
            while (i < lines.Count && lines[i].Indent == indent && !IsSeqLine(lines[i].Text))
            {
                var line = lines[i];
                string key = SplitKey(line.Text, line.No, out string valuePart, out bool hasValue);
                if (map.ContainsKey(key))
                    throw new SchemaException($"duplicate key '{key}' at line {line.No}");

                if (hasValue)
                {
                    map[key] = ParseScalarOrFlow(valuePart, line.No);
                    i++;
                }
                else
                {
                    i++;
                    map[key] = ParseChildBlock(lines, ref i, indent);
                }
            }
            return map;
        }

        // A key with an empty inline value owns the following deeper-indented block, or a sequence at the
        // same indent as the key (YAML allows that), or null if nothing follows.
        private static object ParseChildBlock(List<Line> lines, ref int i, int parentIndent)
        {
            if (i >= lines.Count) return null;
            if (lines[i].Indent > parentIndent)
                return ParseNode(lines, ref i, lines[i].Indent);
            if (lines[i].Indent == parentIndent && IsSeqLine(lines[i].Text))
                return ParseSequence(lines, ref i, parentIndent);
            return null;
        }

        private static List<object> ParseSequence(List<Line> lines, ref int i, int indent)
        {
            var list = new List<object>();
            while (i < lines.Count && lines[i].Indent == indent && IsSeqLine(lines[i].Text))
            {
                var line = lines[i];
                string dash = line.Text;

                // Locate the content start after the dash (support "- " and "-  ").
                int k = 1;
                while (k < dash.Length && dash[k] == ' ') k++;
                string content = k < dash.Length ? dash.Substring(k) : string.Empty;
                int contentCol = indent + k;

                if (content.Length == 0)
                {
                    i++;
                    list.Add(ParseChildBlock(lines, ref i, indent));
                }
                else if (IsInlineMappingStart(content))
                {
                    // "- key: value" — a mapping item that begins on this line; rewrite the line as a
                    // mapping entry at contentCol so subsequent aligned keys join the same mapping.
                    lines[i] = new Line(line.No, contentCol, content);
                    list.Add(ParseMapping(lines, ref i, contentCol));
                }
                else
                {
                    list.Add(ParseScalarOrFlow(content, line.No));
                    i++;
                }
            }
            return list;
        }

        // True when a block-sequence item's content is itself a "key: value" mapping entry (as opposed to a
        // flow collection, a quoted scalar, or a plain scalar).
        private static bool IsInlineMappingStart(string content)
        {
            if (content.Length == 0) return false;
            char c0 = content[0];
            if (c0 == '{' || c0 == '[' || c0 == '"' || c0 == '\'') return false;
            bool inS = false, inD = false; int depth = 0;
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (inS) { if (c == '\'') inS = false; continue; }
                if (inD) { if (c == '\\') { i++; continue; } if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == '{' || c == '[') { depth++; continue; }
                if (c == '}' || c == ']') { if (depth > 0) depth--; continue; }
                if (depth == 0 && c == ':' && (i + 1 == content.Length || content[i + 1] == ' '))
                    return true;
            }
            return false;
        }

        // Split "key: value" / "key:" into a string key and the (possibly empty) value text.
        private static string SplitKey(string text, int lineNo, out string valuePart, out bool hasValue)
        {
            bool inS = false, inD = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inS) { if (c == '\'') inS = false; continue; }
                if (inD) { if (c == '\\') { i++; continue; } if (c == '"') inD = false; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '"') { inD = true; continue; }
                if (c == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
                {
                    string keyRaw = text.Substring(0, i);
                    string rest = text.Substring(i + 1).Trim();
                    valuePart = rest;
                    hasValue = rest.Length > 0;
                    return InterpretKey(keyRaw, lineNo);
                }
            }
            throw new SchemaException($"expected 'key: value' mapping entry at line {lineNo}: '{text}'");
        }

        private static string InterpretKey(string keyRaw, int lineNo)
        {
            string t = keyRaw.Trim();
            if (t.Length == 0) throw new SchemaException($"empty mapping key at line {lineNo}");
            if (t[0] == '"') { int p = 0; return ReadDoubleQuoted(t, ref p, lineNo); }
            if (t[0] == '\'') { int p = 0; return ReadSingleQuoted(t, ref p, lineNo); }
            CheckForbidden(t, lineNo);
            return t;
        }

        // Parse a block value (right side of "key:") — a flow collection, a quoted scalar, or a plain scalar.
        // A plain scalar is taken whole (commas within it are legal), so this is NOT the same as the
        // delimiter-terminated flow-scalar reader.
        private static object ParseScalarOrFlow(string valueText, int lineNo)
        {
            string t = valueText.Trim();
            if (t.Length == 0) return null;
            char c0 = t[0];
            if (c0 == '{' || c0 == '[')
            {
                int pos = 0;
                object v = c0 == '{' ? ParseFlowMap(t, ref pos, lineNo) : (object)ParseFlowSeq(t, ref pos, lineNo);
                SkipWs(t, ref pos);
                if (pos < t.Length)
                    throw new SchemaException($"unexpected content after flow collection at line {lineNo}: '{t}'");
                return v;
            }
            if (c0 == '"' || c0 == '\'')
            {
                int pos = 0;
                string s = c0 == '"' ? ReadDoubleQuoted(t, ref pos, lineNo) : ReadSingleQuoted(t, ref pos, lineNo);
                SkipWs(t, ref pos);
                if (pos < t.Length)
                    throw new SchemaException($"unexpected content after quoted scalar at line {lineNo}: '{t}'");
                return s;
            }
            CheckForbidden(t, lineNo);
            return InferScalar(t);
        }

        // ----- flow collections -----

        private static Dictionary<string, object> ParseFlowMap(string s, ref int pos, int lineNo)
        {
            pos++; // consume '{'
            var map = new Dictionary<string, object>();
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return map; }
            while (true)
            {
                SkipWs(s, ref pos);
                string key = ReadFlowKey(s, ref pos, lineNo);
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                    throw new SchemaException($"expected ':' in flow mapping at line {lineNo}");
                pos++; // consume ':'
                object val = ReadFlowValue(s, ref pos, lineNo);
                if (map.ContainsKey(key))
                    throw new SchemaException($"duplicate key '{key}' at line {lineNo}");
                map[key] = val;
                SkipWs(s, ref pos);
                if (pos >= s.Length)
                    throw new SchemaException($"unterminated flow mapping at line {lineNo}");
                if (s[pos] == ',') { pos++; SkipWs(s, ref pos); if (pos < s.Length && s[pos] == '}') { pos++; break; } continue; }
                if (s[pos] == '}') { pos++; break; }
                throw new SchemaException($"unexpected '{s[pos]}' in flow mapping at line {lineNo}");
            }
            return map;
        }

        private static List<object> ParseFlowSeq(string s, ref int pos, int lineNo)
        {
            pos++; // consume '['
            var list = new List<object>();
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (true)
            {
                object val = ReadFlowValue(s, ref pos, lineNo);
                list.Add(val);
                SkipWs(s, ref pos);
                if (pos >= s.Length)
                    throw new SchemaException($"unterminated flow sequence at line {lineNo}");
                if (s[pos] == ',') { pos++; SkipWs(s, ref pos); if (pos < s.Length && s[pos] == ']') { pos++; break; } continue; }
                if (s[pos] == ']') { pos++; break; }
                throw new SchemaException($"unexpected '{s[pos]}' in flow sequence at line {lineNo}");
            }
            return list;
        }

        private static string ReadFlowKey(string s, ref int pos, int lineNo)
        {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '"') return ReadDoubleQuoted(s, ref pos, lineNo);
            if (pos < s.Length && s[pos] == '\'') return ReadSingleQuoted(s, ref pos, lineNo);
            int start = pos;
            while (pos < s.Length && s[pos] != ':' && s[pos] != ',' && s[pos] != '}' && s[pos] != ']') pos++;
            string raw = s.Substring(start, pos - start).Trim();
            if (raw.Length == 0) throw new SchemaException($"empty key in flow mapping at line {lineNo}");
            CheckForbidden(raw, lineNo);
            return raw;
        }

        private static object ReadFlowValue(string s, ref int pos, int lineNo)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            if (c == '{') return ParseFlowMap(s, ref pos, lineNo);
            if (c == '[') return ParseFlowSeq(s, ref pos, lineNo);
            if (c == '"') return ReadDoubleQuoted(s, ref pos, lineNo);
            if (c == '\'') return ReadSingleQuoted(s, ref pos, lineNo);
            int start = pos;
            while (pos < s.Length && s[pos] != ',' && s[pos] != ']' && s[pos] != '}') pos++;
            string raw = s.Substring(start, pos - start).Trim();
            if (raw.Length == 0) return null;
            CheckForbidden(raw, lineNo);
            return InferScalar(raw);
        }

        private static string ReadDoubleQuoted(string s, ref int pos, int lineNo)
        {
            pos++; // consume opening quote
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '\\')
                {
                    if (pos + 1 >= s.Length) break;
                    char n = s[pos + 1];
                    // Only \" and \\ are specified; pass any other escaped char through literally.
                    sb.Append(n == '"' ? '"' : n == '\\' ? '\\' : n);
                    pos += 2;
                    continue;
                }
                if (c == '"') { pos++; return sb.ToString(); }
                sb.Append(c);
                pos++;
            }
            throw new SchemaException($"unterminated double-quoted string at line {lineNo}");
        }

        private static string ReadSingleQuoted(string s, ref int pos, int lineNo)
        {
            pos++; // consume opening quote
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '\'')
                {
                    if (pos + 1 < s.Length && s[pos + 1] == '\'') { sb.Append('\''); pos += 2; continue; }
                    pos++;
                    return sb.ToString();
                }
                sb.Append(c);
                pos++;
            }
            throw new SchemaException($"unterminated single-quoted string at line {lineNo}");
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
        }

        private static void CheckForbidden(string token, int lineNo)
        {
            if (string.IsNullOrEmpty(token)) return;
            switch (token[0])
            {
                case '&': throw Unsupported("anchor", "&", lineNo);
                case '*': throw Unsupported("alias", "*", lineNo);
                case '!': throw Unsupported("tag", "!", lineNo);
                case '|': throw Unsupported("block scalar", "|", lineNo);
                case '>': throw Unsupported("block scalar", ">", lineNo);
            }
        }

        private static SchemaException Unsupported(string name, string sym, int lineNo)
            => new SchemaException($"unsupported YAML {name} '{sym}' at line {lineNo}");

        // Scalar type inference (unquoted, non-empty, already checked for forbidden leaders). This is the READ
        // half of the round-trip contract: AnimatorSchemaEmit.InfersNonString mirrors these rules to decide
        // which string scalars it must QUOTE (a token that would infer here to bool/number/null), and
        // AnimatorSchemaEmit.NumFromFloat mirrors ToNumber's double↔float handling. A change here must move in
        // lockstep with those — NeedsQuote_Agrees_With_Parser_InferScalar_Across_Token_Battery guards the drift.
        private static object InferScalar(string t)
        {
            if (t == "~") return null;
            string lower = t.ToLowerInvariant();
            if (lower == "true" || lower == "on") return true;
            if (lower == "false" || lower == "off") return false;
            if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                return l;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            return t;
        }

        // ===================================================================================
        // Stage 2 — typed binder
        // ===================================================================================

        private static AnimDocument Bind(object root, string sourcePath)
        {
            var map = ToMap(root, "document");
            var doc = new AnimDocument { SourcePath = sourcePath };
            foreach (var kv in map)
            {
                string key = kv.Key;
                if (key.StartsWith("_")) { doc.ReservedNotes[key] = kv.Value; continue; }
                switch (key)
                {
                    case "schema": doc.Schema = ToInt(kv.Value, "schema"); break;
                    case "controller": doc.ControllerName = ToStr(kv.Value, "controller"); break;
                    case "basis": doc.Basis = ParseBasis(ToStr(kv.Value, "basis")); break;
                    case "role": doc.Role = ParseRole(ToStr(kv.Value, "role")); break;
                    case "defaults": BindDefaults(doc.Defaults, ToMap(kv.Value, "defaults")); break;
                    case "parameters": BindParameters(doc, ToMap(kv.Value, "parameters")); break;
                    case "layers": BindLayers(doc, ToList(kv.Value, "layers")); break;
                    case "clips": BindClips(doc, ToMap(kv.Value, "clips")); break;
                    default: throw new SchemaException($"unknown top-level key '{key}'");
                }
            }
            // Basis has no unbound sentinel: the BindingBasis enum defaults to AvatarRoot, so an omitted key
            // would silently read as avatar-root. Require it here — the earliest point — to close the
            // silently-unbound-animation landmine before it reaches the model.
            if (!map.ContainsKey("basis"))
                throw new SchemaException("missing required key 'basis' (avatar-root | mount-root)");
            return doc;
        }

        private static BindingBasis ParseBasis(string v)
        {
            switch (v)
            {
                case "avatar-root": return BindingBasis.AvatarRoot;
                case "mount-root": return BindingBasis.MountRoot;
                default: throw new SchemaException($"invalid basis '{v}' (expected avatar-root or mount-root)");
            }
        }

        private static ControllerRole ParseRole(string v)
        {
            switch (v)
            {
                case "fx": return ControllerRole.Fx;
                case "base-fx": return ControllerRole.BaseFx;
                case "gesture": return ControllerRole.Gesture;
                case "action": return ControllerRole.Action;
                case "sitting": return ControllerRole.Sitting;
                case "tpose": return ControllerRole.TPose;
                case "ikpose": return ControllerRole.IkPose;
                case "additive": return ControllerRole.Additive;
                case "base": return ControllerRole.Base;
                default: throw new SchemaException(
                    $"invalid role '{v}' (expected one of fx, base-fx, gesture, action, sitting, tpose, ikpose, additive, base)");
            }
        }

        private static TransitionInterruption ParseInterruption(string v)
        {
            switch (v)
            {
                case "none": return TransitionInterruption.None;
                case "source": return TransitionInterruption.Source;
                case "destination": return TransitionInterruption.Destination;
                case "sourceThenDestination": return TransitionInterruption.SourceThenDestination;
                case "destinationThenSource": return TransitionInterruption.DestinationThenSource;
                default: throw new SchemaException(
                    $"invalid interruption '{v}' (expected none, source, destination, sourceThenDestination, destinationThenSource)");
            }
        }

        private static AnimParamType ParseParamType(string v)
        {
            switch (v)
            {
                case "bool": return AnimParamType.Bool;
                case "int": return AnimParamType.Int;
                case "float": return AnimParamType.Float;
                default: throw new SchemaException($"invalid parameter type '{v}' (expected bool, int, or float)");
            }
        }

        private static void BindDefaults(Defaults d, Dictionary<string, object> map)
        {
            foreach (var kv in map)
            {
                switch (kv.Key)
                {
                    case "writeDefaults": d.WriteDefaults = ToBool(kv.Value, "defaults.writeDefaults"); break;
                    case "transition": BindDefaultTransition(d, ToMap(kv.Value, "defaults.transition")); break;
                    default: throw new SchemaException($"unknown defaults field '{kv.Key}'");
                }
            }
        }

        private static void BindDefaultTransition(Defaults d, Dictionary<string, object> map)
        {
            foreach (var kv in map)
            {
                switch (kv.Key)
                {
                    case "duration": d.TransitionDuration = ToNumber(kv.Value, "defaults.transition.duration"); break;
                    case "exitTime":
                        // Defaults only carry the has-exit-time bool; only 'none' is in scope here.
                        if (kv.Value is string s && s == "none") d.TransitionHasExitTime = false;
                        else throw new SchemaException("defaults.transition.exitTime: only 'none' is supported");
                        break;
                    case "interruption": d.Interruption = ParseInterruption(ToStr(kv.Value, "defaults.transition.interruption")); break;
                    default: throw new SchemaException($"unknown defaults.transition field '{kv.Key}'");
                }
            }
        }

        private static void BindParameters(AnimDocument doc, Dictionary<string, object> map)
        {
            // Duplicate names are already refused by stage-1's per-mapping guard (line-numbered).
            foreach (var kv in map)
            {
                string name = kv.Key;
                var spec = new ParamSpec { Name = name };
                object v = kv.Value;
                if (v is Dictionary<string, object> m)
                {
                    BindParameterLong(spec, m, name);
                }
                else if (v is string typeToken)
                {
                    spec.Type = ParseParamType(typeToken);   // shorthand: value IS the type
                }
                else
                {
                    throw new SchemaException($"parameter '{name}': expected a type shorthand (bool/int/float) or a spec map");
                }
                doc.Parameters.Add(spec);
            }
        }

        private static void BindParameterLong(ParamSpec spec, Dictionary<string, object> m, string name)
        {
            if (!m.ContainsKey("type"))
                throw new SchemaException($"parameter '{name}': missing required 'type'");
            foreach (var kv in m)
            {
                switch (kv.Key)
                {
                    case "type": spec.Type = ParseParamType(ToStr(kv.Value, $"parameter '{name}' type")); break;
                    case "default":
                        spec.Default = spec.Type == AnimParamType.Bool
                            ? ToBoolFloat(kv.Value, $"parameter '{name}' default")
                            : ToNumber(kv.Value, $"parameter '{name}' default");
                        break;
                    case "aap": spec.Aap = ToBool(kv.Value, $"parameter '{name}' aap"); break;
                    case "scratch": spec.Scratch = ToBool(kv.Value, $"parameter '{name}' scratch"); break;
                    case "vrc": spec.Vrc = BindVrc(ToMap(kv.Value, $"parameter '{name}' vrc"), name); break;
                    default: throw new SchemaException($"parameter '{name}': unknown field '{kv.Key}'");
                }
            }
        }

        private static VrcParamMeta BindVrc(Dictionary<string, object> m, string name)
        {
            var meta = new VrcParamMeta();
            foreach (var kv in m)
            {
                switch (kv.Key)
                {
                    case "synced": meta.Synced = ToBool(kv.Value, $"parameter '{name}' vrc.synced"); break;
                    case "saved": meta.Saved = ToBool(kv.Value, $"parameter '{name}' vrc.saved"); break;
                    case "osc": meta.Osc = ToBool(kv.Value, $"parameter '{name}' vrc.osc"); break;
                    case "type": meta.VrcType = ParseParamType(ToStr(kv.Value, $"parameter '{name}' vrc.type")); break;
                    default: throw new SchemaException($"parameter '{name}' vrc: unknown field '{kv.Key}'");
                }
            }
            return meta;
        }

        private static void BindLayers(AnimDocument doc, List<object> list)
        {
            foreach (var item in list)
            {
                var m = ToMap(item, "layer");
                var layer = new Layer();
                foreach (var kv in m)
                {
                    switch (kv.Key)
                    {
                        case "name": layer.Name = ToStr(kv.Value, "layer.name"); break;
                        case "weight": layer.Weight = ToNumber(kv.Value, "layer.weight"); break;
                        case "mask": layer.Mask = ToStr(kv.Value, "layer.mask"); break;
                        case "blend": layer.Blend = ParseBlend(ToStr(kv.Value, "layer.blend")); break;
                        case "writeDefaults": layer.WriteDefaults = ToBool(kv.Value, "layer.writeDefaults"); break;
                        // Machine-body keys (states/machines/entry/any/default/behaviours) bind into the
                        // layer's Root machine — the same surface a nested sub-machine carries.
                        default:
                            if (!BindMachineKey(layer.Root, kv.Key, kv.Value, "layer"))
                                throw new SchemaException($"unknown layer field '{kv.Key}'");
                            break;
                    }
                }
                doc.Layers.Add(layer);
            }
        }

        // Binds one machine-body key shared by a layer's Root machine and every nested sub-machine.
        // Returns false (without consuming) if the key isn't a machine-body key, so the layer caller can
        // treat it as an unknown-layer-field error and the sub-machine caller as an unknown-machine-field.
        private static bool BindMachineKey(StateMachine sm, string key, object value, string ctx)
        {
            switch (key)
            {
                case "states": BindStates(sm, ToMap(value, $"{ctx}.states")); return true;
                case "machines": BindMachines(sm, ToMap(value, $"{ctx}.machines")); return true;
                case "entry": BindLadder(sm.EntryLadder, ToList(value, $"{ctx}.entry"), anyLadder: false); return true;
                case "any": BindLadder(sm.AnyLadder, ToList(value, $"{ctx}.any"), anyLadder: true); return true;
                case "default": sm.DefaultState = ToStr(value, $"{ctx}.default"); return true;
                case "behaviours": BindBehaviours(sm.Behaviours, ToList(value, $"{ctx}.behaviours")); return true;
                case "layout": BindLayout(sm, ToMap(value, $"{ctx}.layout"), ctx); return true;
                default: return false;
            }
        }

        private static void BindLayout(StateMachine sm, Dictionary<string, object> map, string ctx)
        {
            var layout = new MachineLayout();
            foreach (var kv in map)
            {
                switch (kv.Key)
                {
                    case "nodes":
                        foreach (var nk in ToMap(kv.Value, $"{ctx}.layout.nodes"))
                            layout.Nodes[nk.Key] = ToCoord(nk.Value, $"{ctx}.layout.nodes.{nk.Key}");
                        break;
                    case "entry":  layout.Entry  = ToCoord(kv.Value, $"{ctx}.layout.entry"); break;
                    case "any":    layout.Any    = ToCoord(kv.Value, $"{ctx}.layout.any"); break;
                    case "exit":   layout.Exit   = ToCoord(kv.Value, $"{ctx}.layout.exit"); break;
                    case "parent": layout.Parent = ToCoord(kv.Value, $"{ctx}.layout.parent"); break;
                    default: throw new SchemaException($"{ctx}.layout: unknown field '{kv.Key}'");
                }
            }
            sm.Layout = layout;
        }

        // A coordinate is a flow list of exactly two numbers -> float[2]. Malformed shape fails by context;
        // a non-numeric element fails through ToNumber's own named throw.
        private static float[] ToCoord(object value, string ctx)
        {
            var list = ToList(value, ctx);
            if (list.Count != 2)
                throw new SchemaException($"{ctx}: coordinate must be [x, y] (two numbers), got {list.Count}");
            return new[] { ToNumber(list[0], ctx), ToNumber(list[1], ctx) };
        }

        // A sub-machine body is a pure machine body (no layer-level keys) that recurses through the same
        // key binder — nested sub-machines fall out for free.
        private static void BindMachines(StateMachine parent, Dictionary<string, object> map)
        {
            // Duplicate sub-machine names are already refused by stage-1's per-mapping guard (line-numbered).
            foreach (var kv in map)
            {
                string name = kv.Key;
                var body = ToMap(kv.Value, $"machine '{name}'");
                var sub = new SubMachine { Name = name };
                foreach (var bk in body)
                    if (!BindMachineKey(sub.Machine, bk.Key, bk.Value, $"machine '{name}'"))
                        throw new SchemaException($"machine '{name}': unknown field '{bk.Key}'");
                parent.Machines.Add(sub);
            }
        }

        // Entry / AnyState ladders are ordered transition lists. Only the AnyState ladder carries the fields of
        // a real state transition (canTransitionToSelf, mute, solo, name) — an entry transition honors none of
        // them, so all are refused on the entry ladder (fail loud, mirroring the canTransitionToSelf precedent).
        private static void BindLadder(List<Transition> into, List<object> list, bool anyLadder)
        {
            BindTransitions(into, list, allowSelf: anyLadder, allowMuteSolo: anyLadder);
        }

        private static LayerBlend ParseBlend(string v)
        {
            switch (v)
            {
                case "override": return LayerBlend.Override;
                case "additive": return LayerBlend.Additive;
                default: throw new SchemaException($"invalid blend '{v}' (expected override or additive)");
            }
        }

        private static void BindStates(StateMachine sm, Dictionary<string, object> map)
        {
            // Duplicate state names are already refused by stage-1's per-mapping guard (line-numbered).
            foreach (var kv in map)
            {
                string name = kv.Key;
                sm.States.Add(BindState(name, ToMap(kv.Value, $"state '{name}'")));
            }
        }

        private static State BindState(string name, Dictionary<string, object> m)
        {
            var st = new State { Name = name };
            foreach (var kv in m)
            {
                switch (kv.Key)
                {
                    case "motion": st.Motion = BindMotion(kv.Value, $"state '{name}'"); break;
                    case "speed": st.Speed = ToNumber(kv.Value, $"state '{name}' speed"); break;
                    case "speedParam": st.SpeedParam = ToStr(kv.Value, $"state '{name}' speedParam"); break;
                    case "motionTimeParam": st.MotionTimeParam = ToStr(kv.Value, $"state '{name}' motionTimeParam"); break;
                    case "mirror": st.Mirror = ToBool(kv.Value, $"state '{name}' mirror"); break;
                    case "writeDefaults": st.WriteDefaults = ToBool(kv.Value, $"state '{name}' writeDefaults"); break;
                    case "behaviours": BindBehaviours(st.Behaviours, ToList(kv.Value, $"state '{name}' behaviours")); break;
                    case "transitions": BindTransitions(st.Transitions, ToList(kv.Value, $"state '{name}' transitions")); break;
                    default: throw new SchemaException($"state '{name}': unknown field '{kv.Key}'");
                }
            }
            return st;
        }

        private static void BindBehaviours(List<Behaviour> into, List<object> list)
        {
            foreach (var item in list)
            {
                var m = ToMap(item, "behaviour");
                if (m.Count != 1)
                    throw new SchemaException("behaviour must be a single-key map like { driver: { ... } }");
                foreach (var kv in m)   // exactly one iteration
                {
                    into.Add(new Behaviour { Kind = kv.Key, Fields = ToMap(kv.Value, $"behaviour '{kv.Key}'") });
                }
            }
        }

        // allowSelf defaults FALSE so a state-transition list (which calls this without the flag) refuses
        // canTransitionToSelf — a field only the AnyState ladder honors. The AnyState caller passes true.
        // allowMuteSolo defaults TRUE: state and AnyState transitions honor mute/solo/name; only the entry
        // ladder (which passes false) refuses them (the entry-emit path never reads them, so they'd silently
        // drop).
        private static void BindTransitions(List<Transition> into, List<object> list, bool allowSelf = false, bool allowMuteSolo = true)
        {
            foreach (var item in list)
            {
                var m = ToMap(item, "transition");
                var t = new Transition();
                foreach (var kv in m)
                {
                    switch (kv.Key)
                    {
                        case "to":
                            string to = ToStr(kv.Value, "transition.to");
                            if (to == "Exit") { t.ToExit = true; t.To = null; }
                            else t.To = to;
                            break;
                        case "when": BindConditions(t.When, ToList(kv.Value, "transition.when")); break;
                        case "exitTime": t.ExitTime = ToNumber(kv.Value, "transition.exitTime"); break;
                        case "duration": t.Duration = ToNumber(kv.Value, "transition.duration"); break;
                        case "fixedDuration": t.FixedDuration = ToBool(kv.Value, "transition.fixedDuration"); break;
                        case "interruption": t.Interruption = ParseInterruption(ToStr(kv.Value, "transition.interruption")); break;
                        case "ordered": t.OrderedInterruption = ToBool(kv.Value, "transition.ordered"); break;
                        case "mute":
                            if (!allowMuteSolo) throw new SchemaException("transition: 'mute' is not valid on an entry ladder");
                            t.Mute = ToBool(kv.Value, "transition.mute");
                            break;
                        case "solo":
                            if (!allowMuteSolo) throw new SchemaException("transition: 'solo' is not valid on an entry ladder");
                            t.Solo = ToBool(kv.Value, "transition.solo");
                            break;
                        case "canTransitionToSelf":
                            if (!allowSelf) throw new SchemaException("transition: 'canTransitionToSelf' is only valid on an AnyState ladder");
                            t.CanTransitionToSelf = ToBool(kv.Value, "transition.canTransitionToSelf");
                            break;
                        case "name":
                            if (!allowMuteSolo) throw new SchemaException("transition: 'name' is not valid on an entry ladder");
                            { var n = ToStr(kv.Value, "transition.name"); t.Name = string.IsNullOrEmpty(n) ? null : n; }
                            break;
                        default: throw new SchemaException($"transition: unknown field '{kv.Key}'");
                    }
                }
                into.Add(t);
            }
        }

        private static void BindConditions(List<Condition> into, List<object> list)
        {
            foreach (var el in list)
                into.Add(ParseCondition(ToStr(el, "condition")));
        }

        // '<param> <op> <value>' parsed RIGHT-ANCHORED: the text after the last space is the value, the
        // single token before it is the op, and everything before is the parameter — VERBATIM (interior
        // spaces, colons, tabs, and op-lookalike suffixes like a param named 'X is true' all survive).
        // Separators are strict single spaces, never normalized: a doubled space is a typo made loud, and
        // a param whose own trailing space would collide with the separator is unrepresentable here (the
        // decompiler refuses such names up front — its self-check renders and re-splits every condition
        // through this same method, so the two can't disagree).
        internal static Condition ParseCondition(string s)
        {
            if (string.IsNullOrEmpty(s))
                throw new SchemaException("condition must be '<param> <op> <value>', got an empty entry");
            int iv = s.LastIndexOf(' ');
            int io = iv > 0 ? s.LastIndexOf(' ', iv - 1) : -1;
            if (io < 0)
                throw new SchemaException($"condition '{s}' must be '<param> <op> <value>' (op and value are the last two space-separated tokens)");
            string value = s.Substring(iv + 1);
            string op = s.Substring(io + 1, iv - io - 1);
            string param = s.Substring(0, io);
            if (op.Length == 0 || value.Length == 0 || (param.Length > 0 && param[param.Length - 1] == ' '))
                throw new SchemaException(
                    $"condition '{s}': param, op, and value are separated by SINGLE spaces (a doubled or trailing space is either a typo or a param name the condition grammar cannot carry)");
            return new Condition { Param = param, Op = ParseCondOp(op), Value = ParseCondValue(value, s) };
        }

        private static CondOp ParseCondOp(string v)
        {
            switch (v)
            {
                case "is": return CondOp.Is;
                case "isNot": return CondOp.IsNot;
                case "greater": return CondOp.Greater;
                case "less": return CondOp.Less;
                case "equals": return CondOp.Equals;
                case "notEqual": return CondOp.NotEqual;
                default: throw new SchemaException(
                    $"invalid condition op '{v}' (expected is, isNot, greater, less, equals, notEqual)");
            }
        }

        private static float ParseCondValue(string v, string cond)
        {
            if (v == "true") return 1f;
            if (v == "false") return 0f;
            if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f;
            throw new SchemaException($"condition '{cond}': value '{v}' is not true/false or a number");
        }

        private static MotionRef BindMotion(object v, string ctx)
        {
            if (v == null) return null;   // motion: ~ -> deliberate empty state
            var m = ToMap(v, $"{ctx} motion");
            bool hasClip = m.ContainsKey("clip"), hasRef = m.ContainsKey("ref"), hasTree = m.ContainsKey("tree");
            int n = (hasClip ? 1 : 0) + (hasRef ? 1 : 0) + (hasTree ? 1 : 0);
            if (n == 0) throw new SchemaException($"{ctx}: motion must set exactly one of clip/ref/tree");
            if (n > 1) throw new SchemaException($"{ctx}: motion sets more than one of clip/ref/tree");

            // Caller-specific strictness stays here: a state's clip form is closed (no sibling keys).
            if (hasClip && m.Count != 1) throw new SchemaException($"{ctx}: motion clip form takes only 'clip'");
            return DecodeMotionRef(m, ctx, $"{ctx} motion.clip", $"{ctx} motion.ref");
        }

        // The clip/ref/tree → MotionRef dispatch shared by a state's `motion:` and a blend-tree child, called
        // once the caller has established exactly one of clip/ref/tree is present. Only the dispatch is shared;
        // each caller keeps its own presence rules (BindMotion: n==0 and a non-'clip' sibling are errors;
        // BindTreeChild: n==0 is a legal empty slot). clipLabel/refLabel carry each caller's error context so
        // diagnostics stay verbatim.
        private static MotionRef DecodeMotionRef(Dictionary<string, object> m, string ctx, string clipLabel, string refLabel)
        {
            var mr = new MotionRef();
            if (m.ContainsKey("clip")) mr.Clip = ToStr(m["clip"], clipLabel);
            else if (m.ContainsKey("ref"))
            {
                object rv = m["ref"];
                if (rv is Dictionary<string, object> gm) mr.RefGuid = BindGuid(gm, ctx);
                else mr.RefPath = ToStr(rv, refLabel);
            }
            else mr.Tree = BindTree(m, ctx);
            return mr;
        }

        private static GuidRef BindGuid(Dictionary<string, object> m, string ctx)
        {
            var g = new GuidRef();
            foreach (var kv in m)
            {
                switch (kv.Key)
                {
                    case "guid": g.Guid = ToStr(kv.Value, $"{ctx} motion.ref.guid"); break;
                    case "fileID": g.FileID = ToLong(kv.Value, $"{ctx} motion.ref.fileID"); break;
                    case "unresolved": g.Unresolved = ToBool(kv.Value, $"{ctx} motion.ref.unresolved"); break;
                    default: throw new SchemaException($"{ctx} motion.ref: unknown field '{kv.Key}'");
                }
            }
            return g;
        }

        private static BlendTreeSpec BindTree(Dictionary<string, object> m, string ctx)
        {
            var spec = new BlendTreeSpec { Kind = ParseTreeKind(ToStr(m["tree"], $"{ctx} tree")) };
            foreach (var kv in m)
            {
                switch (kv.Key)
                {
                    case "tree": break;   // the discriminator, already read
                    case "name": { var n = ToStr(kv.Value, $"{ctx} tree.name"); spec.Name = string.IsNullOrEmpty(n) ? null : n; break; }
                    case "param": spec.Param = ToStr(kv.Value, $"{ctx} tree.param"); break;
                    case "paramY": spec.ParamY = ToStr(kv.Value, $"{ctx} tree.paramY"); break;
                    case "normalized": spec.Normalized = ToBool(kv.Value, $"{ctx} tree.normalized"); break;
                    case "children":
                        foreach (var c in ToList(kv.Value, $"{ctx} tree.children"))
                            spec.Children.Add(BindTreeChild(ToMap(c, $"{ctx} tree child"), ctx));
                        break;
                    // Child-level fields when this map is a tree-that-is-a-child: consumed by BindTreeChild,
                    // ignored here (mirror of BindTreeChild ignoring tree-level fields).
                    case "directWeight": case "threshold": case "timeScale": case "mirror": case "cycleOffset":
                    case "x": case "y": case "posX": case "posY": break;
                    default: throw new SchemaException($"{ctx} tree: unknown field '{kv.Key}'");
                }
            }
            // Reject a field misplaced for this kind — build/decode honor each field only on the kinds below,
            // so accepting it elsewhere would silently erase it through compile→decompile. Refuse instead (the
            // parser already refuses unknown fields and per-context mute/solo/canTransitionToSelf).
            bool twoD = spec.Kind == TreeKind.SimpleDirectional2D
                     || spec.Kind == TreeKind.FreeformDirectional2D
                     || spec.Kind == TreeKind.FreeformCartesian2D;
            if (spec.Normalized.HasValue && spec.Kind != TreeKind.Direct)
                throw new SchemaException($"{ctx} tree: 'normalized' is only valid on a direct tree");
            if (spec.Param != null && spec.Kind == TreeKind.Direct)
                throw new SchemaException($"{ctx} tree: 'param' is not valid on a direct tree (it uses per-child directWeight)");
            if (spec.ParamY != null && !twoD)
                throw new SchemaException($"{ctx} tree: 'paramY' is only valid on a 2D tree");
            return spec;
        }

        private static TreeChild BindTreeChild(Dictionary<string, object> m, string ctx)
        {
            var child = new TreeChild();
            bool hasClip = m.ContainsKey("clip"), hasRef = m.ContainsKey("ref"), hasTree = m.ContainsKey("tree");
            int n = (hasClip ? 1 : 0) + (hasRef ? 1 : 0) + (hasTree ? 1 : 0);
            if (n > 1) throw new SchemaException($"{ctx} tree child: sets more than one motion of clip/ref/tree");
            // n == 0 is a legal EMPTY child (an unassigned blend-tree slot) — Unity permits it, and it is the
            // normalized form of a broken ref after the first compile nulls the motion. Leave Motion null.
            if (n == 1)
                child.Motion = DecodeMotionRef(m, ctx, $"{ctx} tree child clip", $"{ctx} tree child ref");

            foreach (var kv in m)
            {
                switch (kv.Key)
                {
                    case "clip": case "ref": case "tree": break;   // motion, handled above
                    case "param": case "paramY": case "children": case "normalized": case "name":
                        if (!hasTree) throw new SchemaException($"{ctx} tree child: '{kv.Key}' is only valid on a nested-tree child");
                        break; // consumed by nested tree motion
                    case "threshold": child.Threshold = ToNumber(kv.Value, $"{ctx} tree child threshold"); break;
                    case "x": case "posX": child.PosX = ToNumber(kv.Value, $"{ctx} tree child posX"); break;
                    case "y": case "posY": child.PosY = ToNumber(kv.Value, $"{ctx} tree child posY"); break;
                    case "directWeight": child.DirectWeight = ToStr(kv.Value, $"{ctx} tree child directWeight"); break;
                    case "timeScale": child.TimeScale = ToNumber(kv.Value, $"{ctx} tree child timeScale"); break;
                    case "mirror": child.Mirror = ToBool(kv.Value, $"{ctx} tree child mirror"); break;
                    case "cycleOffset": child.CycleOffset = ToNumber(kv.Value, $"{ctx} tree child cycleOffset"); break;
                    default: throw new SchemaException($"{ctx} tree child: unknown field '{kv.Key}'");
                }
            }
            return child;
        }

        // Canonical pinned tree-kind tokens (case-sensitive). No alternates.
        private static TreeKind ParseTreeKind(string v)
        {
            switch (v)
            {
                case "1d": return TreeKind.OneD;
                case "simpleDirectional2d": return TreeKind.SimpleDirectional2D;
                case "freeformDirectional2d": return TreeKind.FreeformDirectional2D;
                case "freeformCartesian2d": return TreeKind.FreeformCartesian2D;
                case "direct": return TreeKind.Direct;
                default: throw new SchemaException(
                    $"invalid tree kind '{v}' (expected 1d, simpleDirectional2d, freeformDirectional2d, freeformCartesian2d, direct)");
            }
        }

        private static void BindClips(AnimDocument doc, Dictionary<string, object> map)
        {
            // Duplicate clip names are already refused by stage-1's per-mapping guard (line-numbered).
            foreach (var kv in map)
            {
                string name = kv.Key;
                var m = ToMap(kv.Value, $"clip '{name}'");
                var clip = new ClipSpec { Name = name };
                foreach (var ck in m)
                {
                    switch (ck.Key)
                    {
                        case "seconds": clip.Seconds = ToNumber(ck.Value, $"clip '{name}' seconds"); break;
                        case "length": clip.Seconds = ToNumber(ck.Value, $"clip '{name}' length"); break;
                        case "set":
                            foreach (var sv in ToMap(ck.Value, $"clip '{name}' set"))
                                clip.Sets[sv.Key] = ToNumber(sv.Value, $"clip '{name}' set.{sv.Key}");
                            break;
                        case "curves":
                            foreach (var cv in ToMap(ck.Value, $"clip '{name}' curves"))
                                clip.Curves.Add(BindCurve(cv.Key, cv.Value, $"clip '{name}' curve '{cv.Key}'"));
                            break;
                        default: throw new SchemaException($"clip '{name}': unknown field '{ck.Key}'");
                    }
                }
                doc.Clips.Add(clip);
            }
        }

        // Accepts EITHER the bare list form `[[t,v],...]` (flat tangents) or a map form
        // `{ tangents: linear, keys: [[t,v],...] }` for an opt-in linear ramp.
        private static CurveSpec BindCurve(string binding, object value, string ctx)
        {
            var curve = new CurveSpec { Binding = binding };
            List<object> keys;
            if (value is Dictionary<string, object> m)
            {
                keys = null;
                foreach (var kv in m)
                {
                    switch (kv.Key)
                    {
                        case "tangents":
                            var t = ToStr(kv.Value, $"{ctx} tangents");
                            curve.Tangents = t == "linear" ? CurveTangent.Linear
                                : t == "stepped" ? CurveTangent.Stepped
                                : t == "flat" ? CurveTangent.Flat
                                : throw new SchemaException($"curve '{binding}': tangents must be 'flat', 'linear', or 'stepped', got '{t}' — 'auto'/'free' are unsupported (Unity recomputes auto/clamped-auto; free carries explicit tangent values the [t,v] schema can't express); author such a curve as a hand-owned .anim");
                            break;
                        case "keys": keys = ToList(kv.Value, $"{ctx} keys"); break;
                        default: throw new SchemaException($"curve '{binding}': unknown field '{kv.Key}'");
                    }
                }
                if (keys == null) throw new SchemaException($"curve '{binding}': map form requires 'keys'");
            }
            else keys = ToList(value, ctx);

            foreach (var k in keys)
            {
                var pair = ToList(k, $"curve '{binding}' keyframe");
                if (pair.Count != 2)
                    throw new SchemaException($"curve '{binding}': each keyframe must be [time, value]");
                curve.Keys.Add(new Keyframe2(
                    ToNumber(pair[0], $"curve '{binding}' time"),
                    ToNumber(pair[1], $"curve '{binding}' value")));
            }
            return curve;
        }

        // ----- neutral-value accessors (named-throw on type mismatch) -----

        private static Dictionary<string, object> ToMap(object v, string ctx)
        {
            if (v is Dictionary<string, object> m) return m;
            throw new SchemaException($"{ctx}: expected a mapping, got {Describe(v)}");
        }

        private static List<object> ToList(object v, string ctx)
        {
            if (v is List<object> l) return l;
            throw new SchemaException($"{ctx}: expected a sequence, got {Describe(v)}");
        }

        private static string ToStr(object v, string ctx)
        {
            if (v == null) return null;
            if (v is string s) return s;
            throw new SchemaException($"{ctx}: expected a string, got {Describe(v)}");
        }

        private static bool ToBool(object v, string ctx)
        {
            if (v is bool b) return b;
            throw new SchemaException($"{ctx}: expected a boolean, got {Describe(v)}");
        }

        // InferScalar only ever yields long/double for numbers, never int — no int arm needed.
        private static float ToNumber(object v, string ctx)
        {
            if (v is long l) return l;
            if (v is double d) return (float)d;
            throw new SchemaException($"{ctx}: expected a number, got {Describe(v)}");
        }

        private static int ToInt(object v, string ctx)
        {
            if (v is long l)
            {
                if (l < int.MinValue || l > int.MaxValue)
                    throw new SchemaException($"{ctx}: value {l} is out of range for an integer");
                return (int)l;
            }
            if (v is double d && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue) return (int)d;
            throw new SchemaException($"{ctx}: expected an integer, got {Describe(v)}");
        }

        private static long ToLong(object v, string ctx)
        {
            if (v is long l) return l;
            if (v is double d && d == Math.Floor(d)) return (long)d;
            throw new SchemaException($"{ctx}: expected an integer, got {Describe(v)}");
        }

        // A boolean-or-number default flattened to a float (true -> 1, false -> 0).
        private static float ToBoolFloat(object v, string ctx)
        {
            if (v is bool b) return b ? 1f : 0f;
            if (v is long l) return l;
            if (v is double d) return (float)d;
            throw new SchemaException($"{ctx}: expected a boolean or number, got {Describe(v)}");
        }

        private static string Describe(object v)
        {
            if (v == null) return "null";
            if (v is Dictionary<string, object>) return "a mapping";
            if (v is List<object>) return "a sequence";
            if (v is string) return "a string";
            if (v is bool) return "a boolean";
            return "a number";
        }
    }
}
