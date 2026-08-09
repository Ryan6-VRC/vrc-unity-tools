using System;
using System.Text;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>VRC-free decision helpers for the avatar-record doors — unit-tested, no editor/SDK deps.
    /// Redaction is shared with the upload door (<see cref="UploadAvatarLogic.RedactIds"/>) rather than
    /// re-implemented: one scrubber, so a new id shape is fixed in one place for both doors.</summary>
    internal static class AvatarRecordLogic
    {
        /// <summary>Map an SDK <c>ApiErrorException</c> to the door's REFUSE text.
        ///
        /// Takes the exception's <c>StatusCode</c>/<c>ErrorMessage</c> FIELDS, never its
        /// <c>.Message</c>: measured 2026-08-09, a 404 from <c>GetAvatar</c> carries the useless default
        /// "Exception of type '…ApiErrorException' was thrown." in <c>.Message</c> while the real text
        /// ("This avatar is unavailable.") sits in the <c>ErrorMessage</c> field. A door forwarding
        /// <c>.Message</c> would report every failure as the same content-free string.
        ///
        /// 404 is deliberately NOT reported as "not found": the API returns it both for a record that does
        /// not exist and for one you may not see, and the door cannot tell those apart — so the text names
        /// both branches rather than asserting the one it cannot prove.</summary>
        internal static string RefuseForStatus(int? statusCode, string serverMessage)
        {
            string tail = string.IsNullOrEmpty(serverMessage)
                ? ""
                : " — server said: " + UploadAvatarLogic.RedactIds(serverMessage);
            switch (statusCode)
            {
                case 401:
                    return "not authenticated to the VRChat API — sign in through the SDK Build Control Panel" + tail;
                case 403:
                    return "this record is not yours to change (or your account lacks permission)" + tail;
                case 404:
                    return "no such avatar record, or it is not visible to this account — check the " +
                           "blueprintId on the avatar's PipelineManager" + tail;
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

        /// <summary>Reject a name the API would refuse or that would make the door's own report ambiguous.
        /// Deliberately thin: server-side rules are the API's to enforce and we do not mirror them (a
        /// mirrored rule drifts silently), so this catches only what a caller can fix without a round trip.</summary>
        internal static string ValidateNewName(string newName)
        {
            if (newName == null) return "newName is null — pass the name to publish";
            if (newName.Trim().Length == 0) return "newName is empty or whitespace";
            if (newName != newName.Trim())
                return "newName has leading/trailing whitespace — pass it already trimmed, so the " +
                       "reported name matches what you asked for";
            return null;
        }

        /// <summary>Describe how the landed name differs from the submitted one.
        ///
        /// VRChat sanitizes names server-side: a submitted ASCII period (U+002E) comes back as U+2024 ONE
        /// DOT LEADER (measured 2026-08-09 — "Probe 0.2s" landed as "Probe 0․2s"). Nothing breaks, but
        /// exact-string matching against a published name fails, so a door reporting "renamed to X" from
        /// its own INPUT would be lying. Every caller-visible name in this door's output is the landed
        /// string, and this is what flags the difference rather than hiding it.</summary>
        internal static string DescribeNameLanding(string submitted, string landed)
        {
            if (landed == null) return "landed=<null> (the API returned no name)";
            if (string.Equals(submitted, landed, StringComparison.Ordinal))
                return "landed exactly as submitted";
            return "SANITIZED server-side — submitted " + Quote(submitted) + ", landed " + Quote(landed) +
                   " " + CodePoints(landed) + "; match published names against the landed form, not yours";
        }

        /// <summary>Code points for a string, so a homoglyph substitution is legible in text output
        /// instead of invisible. Only emitted where a name did not land verbatim.</summary>
        internal static string CodePoints(string s)
        {
            if (s == null) return "<null>";
            var sb = new StringBuilder("[");
            for (int i = 0; i < s.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append("U+").Append(((int)s[i]).ToString("X4"));
            }
            return sb.Append(']').ToString();
        }

        internal static string Quote(string s) => s == null ? "<null>" : "\"" + s + "\"";

        /// <summary>The compare-and-swap guard. A rename names the avatar by GameObject, but the id it
        /// actually writes comes from that object's PipelineManager — so a stale or mis-wired blueprintId
        /// silently retargets the write onto a DIFFERENT live record. Requiring the caller to state the
        /// name it believes is live turns that into a refusal instead of a wrong rename, and it is what
        /// makes ReportAvatarRecord's output chain into this door's input.
        ///
        /// Compared against the LANDED current name, ordinally — so a caller that pasted a sanitized name
        /// back from a report matches, and one that guessed the ASCII form is told about the difference
        /// rather than being silently accepted.</summary>
        internal static string CheckExpectedName(string expectCurrentName, string liveName)
        {
            if (expectCurrentName == null)
                return "expectCurrentName is null — pass the record's current live name (run " +
                       "ReportAvatarRecord first and copy the name it reports) so the rename cannot " +
                       "land on the wrong record";
            if (string.Equals(expectCurrentName, liveName, StringComparison.Ordinal)) return null;
            return "expectCurrentName does not match the live record: you expected " +
                   Quote(expectCurrentName) + ", the record is named " + Quote(liveName) + " " +
                   CodePoints(liveName) + ". Refusing — either the blueprintId points at a different " +
                   "avatar than you think, or the name was changed elsewhere. Re-run ReportAvatarRecord.";
        }
    }
}
