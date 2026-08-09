using System;
using System.Text;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>VRC-free decision helpers for the avatar-record doors — unit-tested, no editor/SDK deps.
    /// Contract for the family lives in <c>docs/unity-tools.md</c> §Publish; this file holds the decisions,
    /// not their rationale.</summary>
    internal static class AvatarRecordLogic
    {
        // ── Output formatting ───────────────────────────────────────────────────────────────────

        /// <summary>Escape a value before it enters the one-line output grammar.
        ///
        /// Names, descriptions and tags are SERVER-controlled text. A value carrying a newline followed by
        /// something shaped like a verdict would forge a line inside the door's own grammar — the grammar
        /// the calling agent parses — so control characters are neutralised at every emit site, not just
        /// the ones that look risky today.</summary>
        internal static string Escape(string s)
        {
            if (s == null) return null;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:   sb.Append(c);      break;
                }
            }
            return sb.ToString();
        }

        internal static string Quote(string s) => s == null ? "<null>" : "\"" + Escape(s) + "\"";

        /// <summary>Render a tag list. An EMPTY list prints as <c>[]</c> rather than nothing, because
        /// "cleared every tag" and "did not touch tags" are different outcomes and a blank would read as
        /// the second.</summary>
        internal static string FormatTags(string[] tags)
        {
            if (tags == null) return "<null>";
            if (tags.Length == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < tags.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(tags[i]));
            }
            return sb.Append(']').ToString();
        }

        /// <summary>Decompose a string into code points — not UTF-16 code units — so a non-BMP character
        /// (an emoji in an avatar name is ordinary on VRChat) is one element rather than a surrogate pair
        /// the reader has to recombine.</summary>
        internal static int[] ToCodePoints(string s)
        {
            var list = new System.Collections.Generic.List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                { list.Add(char.ConvertToUtf32(s[i], s[i + 1])); i++; }
                else list.Add(s[i]);
            }
            return list.ToArray();
        }

        /// <summary>Code points, capped. The helper exists to make an invisible substitution legible, and a
        /// full dump of a description-length value defeats that as thoroughly as printing nothing — the two
        /// characters that matter are lost in eighty that did not change.</summary>
        internal static string CodePoints(string s, int max = 16)
        {
            if (s == null) return "<null>";
            var cps = ToCodePoints(s);
            var sb = new StringBuilder("[");
            int n = Math.Min(cps.Length, max);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append("U+").Append(cps[i].ToString("X4"));
            }
            if (cps.Length > max) sb.Append(" … +").Append(cps.Length - max).Append(" more");
            return sb.Append(']').ToString();
        }

        /// <summary>Name the code points that actually CHANGED, positionally.
        ///
        /// This is what a caller needs from a homoglyph substitution: "position 7 went U+002E → U+2024",
        /// not a transcription of the whole value. Falls back to a capped dump of the landed value when the
        /// lengths differ, since positional pairing would then be misleading rather than merely long.</summary>
        internal static string DescribeCodePointDiff(string submitted, string landed, int max = 6)
        {
            var a = ToCodePoints(submitted);
            var b = ToCodePoints(landed);
            if (a.Length != b.Length)
                return "length changed " + a.Length + "->" + b.Length + " code points; landed " +
                       CodePoints(landed);

            var sb = new StringBuilder();
            int shown = 0, total = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                total++;
                if (shown >= max) continue;
                if (shown > 0) sb.Append(", ");
                sb.Append("at ").Append(i).Append(": U+").Append(a[i].ToString("X4"))
                  .Append("->U+").Append(b[i].ToString("X4"));
                shown++;
            }
            if (total == 0) return "no code point differs";
            if (total > shown) sb.Append(" (+").Append(total - shown).Append(" more)");
            return sb.ToString();
        }

        // ── REFUSE register ─────────────────────────────────────────────────────────────────────

        /// <summary>Map an SDK API error to the door's REFUSE text.
        ///
        /// Fed from the exception's <c>StatusCode</c>/<c>ErrorMessage</c> FIELDS, never its
        /// <c>.Message</c>: the SDK's error types do not pass a message to their base constructor, so
        /// <c>.Message</c> is the content-free "Exception of type … was thrown." while the server's real
        /// text sits in the field.
        ///
        /// 404 is deliberately NOT reported as "not found": the API returns it both for a record that does
        /// not exist and for one this account may not see, and the door cannot tell those apart — so the
        /// text names both branches rather than asserting the one it cannot prove.</summary>
        internal static string RefuseForStatus(int? statusCode, string serverMessage)
        {
            string tail = string.IsNullOrEmpty(serverMessage) ? "" : " — server said: " + Escape(serverMessage);
            switch (statusCode)
            {
                case 401:
                    return "not authenticated to the VRChat API — sign in through the SDK Build Control Panel" + tail;
                case 403:
                    return "this record is not yours to change (or your account lacks permission)" + tail;
                case 404:
                    return "no such avatar record, or it is not visible to this account — check the " +
                           "blueprintId on the avatar's PipelineManager" + tail;
                case 422:
                    return "the VRChat moderation filter rejected this content — a name, description or " +
                           "tag was refused; change the text rather than retrying it" + tail;
                case 429:
                    return "rate-limited by the VRChat API — wait and re-run; do not retry in a loop" + tail;
                default:
                    if (statusCode.HasValue && statusCode >= 500)
                        return "VRChat API error " + statusCode + " (server-side, transient) — re-run" + tail;
                    if (statusCode.HasValue)
                        return "VRChat API error " + statusCode + tail;
                    return "VRChat API call failed with no HTTP status" + tail;
            }
        }

        // ── Input validation ────────────────────────────────────────────────────────────────────

        /// <summary>Reject a name the API would refuse or that would make the door's own report ambiguous.
        /// Deliberately thin: server-side rules are the API's to enforce and we do not mirror them (a
        /// mirrored rule drifts silently), so this catches only what a caller can fix without a round trip.
        /// A null name means "leave the name alone" and is the caller's to interpret, not this method's.</summary>
        internal static string ValidateNewName(string newName)
        {
            if (newName == null) return "newName is null — pass the name to publish";
            if (newName.Trim().Length == 0) return "newName is empty or whitespace";
            if (newName != newName.Trim())
                return "newName has leading/trailing whitespace — pass it already trimmed, so the " +
                       "reported name matches what you asked for";
            return null;
        }

        /// <summary>Reject a tag list the caller can fix without a round trip. An EMPTY array is legal and
        /// means "clear every tag" — the only way to express clearing, since null already means "leave them
        /// alone", so it must not be mistaken for an omission.
        ///
        /// Content tags are a server-side vocabulary this door deliberately does not mirror: a copied
        /// allow-list would drift silently and start refusing tags the API accepts. Malformed shape is
        /// caught here; unknown vocabulary is the API's to reject, and its refusal reaches the caller
        /// intact through the status register above.</summary>
        internal static string ValidateTags(string[] newTags)
        {
            if (newTags == null) return null;
            for (int i = 0; i < newTags.Length; i++)
            {
                if (newTags[i] == null) return "newTags[" + i + "] is null";
                if (newTags[i].Trim().Length == 0) return "newTags[" + i + "] is empty or whitespace";
                if (newTags[i] != newTags[i].Trim())
                    return "newTags[" + i + "] (" + Quote(newTags[i]) + ") has leading/trailing whitespace";
                for (int j = 0; j < i; j++)
                    if (string.Equals(newTags[i], newTags[j], StringComparison.Ordinal))
                        return "newTags has a duplicate: " + Quote(newTags[i]);
            }
            return null;
        }

        /// <summary>Which fields a call intends to write, and the refusal when it intends none.
        ///
        /// Every field is null-means-unchanged, so "change nothing" is expressible and is almost always a
        /// caller bug — a door that silently posted the record back unchanged would still bump the server's
        /// Version and report PASS, which reads as a successful edit that never happened.</summary>
        internal static string CheckSomethingToDo(string newName, string newDescription, string[] newTags)
        {
            if (newName == null && newDescription == null && newTags == null)
                return "nothing to change — every field is null (null means \"leave it alone\"). Pass at " +
                       "least one of newName / newDescription / newTags.";
            return null;
        }

        // ── Landing (server-side sanitization) ──────────────────────────────────────────────────

        /// <summary>Describe how a landed field value differs from the submitted one.
        ///
        /// VRChat sanitizes text server-side — an ASCII period comes back as U+2024 ONE DOT LEADER, and it
        /// is not name-specific — so every text field this door writes is reported from what LANDED, never
        /// from the input. A door echoing its own argument would silently misreport every value the server
        /// rewrites, and the two forms are visually identical, which is why a difference is shown as code
        /// points. The field is named because two writable text fields can move in one call.</summary>
        internal static string DescribeLanding(string field, string submitted, string landed)
        {
            if (landed == null) return field + " landed=<null> (the API returned no value)";
            if (string.Equals(submitted, landed, StringComparison.Ordinal))
                return field + " landed exactly as submitted";
            return field + " SANITIZED server-side — submitted " + Quote(submitted) + ", landed " +
                   Quote(landed) + " (" + DescribeCodePointDiff(submitted, landed) +
                   "); match published values against the landed form, not yours";
        }

        // ── The expected-name check ─────────────────────────────────────────────────────────────

        /// <summary>Confirm the caller is editing the record it thinks it is.
        ///
        /// A call names the avatar by GameObject, but the id actually written comes from that object's
        /// PipelineManager — an id the caller never sees. A stale or mis-wired one silently retargets the
        /// write onto a different live record, so requiring the caller to state the name it believes is
        /// live turns that into a refusal instead of a wrong edit. It is what makes ReportAvatarRecord's
        /// output chain into this door's input.
        ///
        /// It is a BEST-EFFORT confirmation, not a compare-and-swap, and must not be described as one:
        /// the read and the write are separate requests with no server-side conditional (the API offers no
        /// ETag or version predicate), so a name that changes in between is not caught; and a display name
        /// is not an identity, so a stale id pointing at an identically-named record passes. It narrows the
        /// blast radius of a mis-wired id; it does not close it.
        ///
        /// Compared ordinally against the LANDED current name, so a caller chaining a sanitized name from
        /// a report matches, and one that retyped the ASCII form is told about the difference rather than
        /// being silently accepted.</summary>
        internal static string CheckExpectedName(string expectCurrentName, string liveName)
        {
            if (expectCurrentName == null)
                return "expectCurrentName is null — pass the record's current live name (run " +
                       "ReportAvatarRecord first and copy the name it reports) so the edit cannot land " +
                       "on the wrong record";
            if (string.Equals(expectCurrentName, liveName, StringComparison.Ordinal)) return null;
            return "expectCurrentName does not match the live record: you expected " +
                   Quote(expectCurrentName) + ", the record is named " + Quote(liveName) + " " +
                   CodePoints(liveName) + ". Refusing — either the blueprintId points at a different " +
                   "avatar than you think, or the name was changed elsewhere. Re-run ReportAvatarRecord.";
        }

        // ── Interrupted-operation reconciliation ────────────────────────────────────────────────

        /// <summary>How far an operation had got when the editor lost it, and therefore what the caller
        /// must be told. The distinction is the whole point: a lost READ costs nothing, while a lost WRITE
        /// may have landed on the server, and reporting either as a plain failure invites a re-run that
        /// silently double-writes or a false belief that nothing changed.</summary>
        internal enum Phase { Reading, UpdateSent }

        /// <summary>The verdict for an operation this editor can no longer observe — a domain reload during
        /// the call, or a frame budget that expired with the request still in flight.
        ///
        /// A write past the send point is reported UNKNOWN, never FAIL: the request reached the server and
        /// most likely landed. Naming ReportAvatarRecord as the reconciler matters more than the verdict —
        /// it is the only way the caller can find out what is actually true.</summary>
        internal static string InterruptedVerdict(string door, string handle, Phase phase, string cause)
        {
            if (phase == Phase.UpdateSent)
                return "[avatar-record] " + door + " handle=" + Quote(handle) + " => UNKNOWN " + cause +
                       " AFTER the update was sent — it may well have landed on the server. Do NOT re-run " +
                       "blindly: run ReportAvatarRecord first and compare, then re-issue only if the edit " +
                       "is genuinely absent.";
            return "[avatar-record] " + door + " handle=" + Quote(handle) + " => FAIL " + cause +
                   " while still reading; nothing was written.";
        }
    }
}
