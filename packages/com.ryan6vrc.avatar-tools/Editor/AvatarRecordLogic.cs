using System;
using System.Text;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>VRC-free decision helpers for the avatar-record doors — unit-tested, no editor/SDK deps.
    /// Contract: <c>docs/unity-tools.md</c> §Publish.</summary>
    internal static class AvatarRecordLogic
    {
        // ── Output formatting ───────────────────────────────────────────────────────────────────

        /// <summary>Escape a value before it enters the one-line output grammar. Names, descriptions and
        /// tags are server-controlled, and a newline followed by something shaped like a verdict would
        /// forge a line in the grammar the calling agent parses, so escape at every emit site.</summary>
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

        /// <summary>The code points that changed, by position. A length change makes positional pairing
        /// misleading, so it falls back to a capped dump of the landed value.</summary>
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
        /// <c>.Message</c>: the SDK's error types pass no message to their base constructor, so
        /// <c>.Message</c> is the content-free "Exception of type … was thrown."
        ///
        /// The 404 text names both branches because the API returns it for a missing record and for one
        /// this account may not see.</summary>
        internal static string RefuseForStatus(int? statusCode, string serverMessage)
            => RefuseForStatus(statusCode, serverMessage, forImage: false);

        /// <summary>As above, but able to name the IMAGE as what was rejected. Only 422 differs: its text
        /// tells the caller to change the offending text, and an image-only call has none.</summary>
        internal static string RefuseForStatus(int? statusCode, string serverMessage, bool forImage)
        {
            string tail = string.IsNullOrEmpty(serverMessage) ? "" : " — server said: " + Escape(serverMessage);
            if (forImage && statusCode == 422)
                return "the VRChat moderation filter rejected this image — shoot a different one rather " +
                       "than retrying the same file" + tail;
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
        // These catch what a caller can fix without a round trip. Server rules stay the API's to reject,
        // never mirrored here: a copied tag allow-list would drift and refuse tags the API accepts.

        /// <summary>Refuses null, though null means "leave the name alone" at the door: the caller skips
        /// this call for a null name.</summary>
        internal static string ValidateNewName(string newName)
        {
            if (newName == null) return "newName is null — pass the name to publish";
            if (newName.Trim().Length == 0) return "newName is empty or whitespace";
            if (newName != newName.Trim())
                return "newName has leading/trailing whitespace — pass it already trimmed, so the " +
                       "reported name matches what you asked for";
            return null;
        }

        /// <summary>An empty array passes: it clears every tag (<c>docs/unity-tools.md</c>
        /// §Publish).</summary>
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

        /// <summary>The refusal for a call that sets no field; <c>docs/unity-tools.md</c> §Publish says
        /// why.</summary>
        internal static string CheckSomethingToDo(string newName, string newDescription, string[] newTags,
                                                  string newImagePath = null)
        {
            if (newName == null && newDescription == null && newTags == null && newImagePath == null)
                return "nothing to change — every field is null (null means \"leave it alone\"). Pass at " +
                       "least one of newName / newDescription / newTags / newImagePath.";
            return null;
        }

        // ── The image ───────────────────────────────────────────────────────────────────────────

        /// <summary>Reject an image the call cannot use, BEFORE anything is written. It runs in the
        /// synchronous guard because the image attaches after the metadata write, so a path rejected later
        /// would leave the name landed and the thumbnail refused; existence is checked here for the same
        /// reason.
        ///
        /// The API has no delete-image call, so an empty string is a caller error rather than the "clear
        /// it" idiom an empty tag array is. The extension list is a shape check; the API decides what it
        /// accepts.</summary>
        internal static string ValidateImagePath(string newImagePath, Func<string, bool> fileExists)
        {
            if (newImagePath == null) return null;
            if (newImagePath.Trim().Length == 0)
                return "newImagePath is empty — a thumbnail can be replaced but never cleared (the API has " +
                       "no delete-image call), so pass a path or pass null to leave it alone";
            if (newImagePath != newImagePath.Trim())
                return "newImagePath has leading/trailing whitespace — pass it already trimmed";

            var lower = newImagePath.ToLowerInvariant();
            if (!lower.EndsWith(".png") && !lower.EndsWith(".jpg") && !lower.EndsWith(".jpeg"))
                return "newImagePath is not a .png/.jpg/.jpeg: " + Quote(newImagePath);
            if (fileExists != null && !fileExists(newImagePath))
                return "newImagePath does not exist: " + Quote(newImagePath) +
                       " — pass the png= path a thumbnail door reported";
            return null;
        }

        /// <summary>Whether a failed image upload is the SDK refusing a file it already holds:
        /// <c>UploadFile</c> throws when the candidate's MD5 matches the version already stored. A re-shot
        /// thumbnail can come out byte-identical, so this is a common case rather than an edge one.
        ///
        /// Matched on the message because <c>UploadException</c> carries no status field, so the
        /// shape-based classifier that handles every API error cannot see it. Both SDK throw sites share
        /// the matched phrase.</summary>
        internal static bool IsAlreadyUploaded(string exceptionMessage)
            => exceptionMessage != null &&
               exceptionMessage.IndexOf("was already uploaded", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Report what the image half did, given whether the record's image URL moved. The
        /// decision arrives as a bool so the caller's verdict and this sentence cannot disagree: a verdict
        /// recovered by string-matching the prose would flip to PASS the moment the wording changed. No
        /// read can confirm the move afterwards (<c>docs/unity-tools.md</c> §Publish), so the caller
        /// observes it at the call site.</summary>
        internal static string DescribeImageLanding(bool moved)
            => moved
                ? "image landed (the record's image url moved)"
                : "image DID NOT CHANGE — the API returned the record unmodified, which is how a failed " +
                  "upload surfaces without throwing; the published thumbnail is still the old one";

        /// <summary>The row for an upload the SDK refused because it already holds the file. It does NOT
        /// claim the published thumbnail is the requested one: the SDK derives the file id from the
        /// record's image url but compares the MD5 against the file's LATEST version, and those differ
        /// after a run whose upload completed and whose final url PUT did not. Re-running, this door's
        /// recommended recovery, lands exactly there. The door still closes PASS on this row, so its text
        /// is where the doubt shows.</summary>
        internal static string ImageAlreadyUploadedRow()
            => "image not re-uploaded — the server already holds a byte-identical file. Normally that " +
               "means the requested thumbnail is published; it does NOT prove it, because the match is " +
               "against the file's latest version and the record may reference an older one after an " +
               "interrupted attach. Confirm in the client if it matters.";

        // ── Landing (server-side sanitization) ──────────────────────────────────────────────────

        /// <summary>Describe how a landed field value differs from the submitted one. Text fields report
        /// from what LANDED, never the input, because the server's sanitized form can look identical to it
        /// (<c>docs/unity-tools.md</c> §Publish). A difference shows as code points, and the field is named
        /// because two writable text fields can move in one call.</summary>
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

        /// <summary>Confirm the caller is editing the record it thinks it is: best-effort, not a
        /// compare-and-swap (<c>docs/unity-tools.md</c> §Publish says what it misses). Compared ordinally
        /// against the LANDED current name, so a chained sanitized name matches and a retyped ASCII form is
        /// told about the difference.</summary>
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

        /// <summary>How far an operation had got when the editor lost it. <c>UpdateSent</c> and
        /// <c>ImageSent</c> need OPPOSITE advice: a lost metadata write must not be re-issued blindly,
        /// while a lost image attach is safe to re-run because the SDK refuses a byte-identical re-upload,
        /// and a read cannot see an image. <c>EnqueueSent</c> is the impostor batch past its first request:
        /// no read shows a queued request, so its advice can only be a scoped re-run.</summary>
        internal enum Phase { Reading, UpdateSent, ImageSent, EnqueueSent }

        /// <summary>The verdict for an operation this editor can no longer observe: a domain reload during
        /// the call, or a frame budget that expired with the request still in flight. A write past the send
        /// point is UNKNOWN, never FAIL: it may well have landed.</summary>
        internal static string InterruptedVerdict(string door, string handle, Phase phase, string cause)
        {
            if (phase == Phase.EnqueueSent)
                return "[avatar-record] " + door + " handles=" + Quote(handle) + " => UNKNOWN " + cause +
                       " during the batch; any request already sent may be queued, and no read shows a " +
                       "queued request. Re-run only the avatars you still need.";
            if (phase == Phase.ImageSent)
                return "[avatar-record] " + door + " handle=" + Quote(handle) + " => UNKNOWN " + cause +
                       " while the thumbnail was uploading; any metadata in the same call had already " +
                       "landed. No read can tell you whether the image arrived — ReportAvatarRecord " +
                       "cannot see it. Simply RE-RUN the same call: an image the server already holds is " +
                       "refused as a no-op, so a re-run either completes the attach or reports it absent.";
            if (phase == Phase.UpdateSent)
                return "[avatar-record] " + door + " handle=" + Quote(handle) + " => UNKNOWN " + cause +
                       " AFTER the update was sent — it may well have landed on the server. Do NOT re-run " +
                       "blindly: run ReportAvatarRecord first and compare, then re-issue only if the edit " +
                       "is genuinely absent.";
            return "[avatar-record] " + door + " handle=" + Quote(handle) + " => FAIL " + cause +
                   " while still reading; nothing was written.";
        }

        // ── Impostor batch ──────────────────────────────────────────────────────────────────────

        internal static bool StopsImpostorBatch(int? statusCode) => statusCode == 429;

        /// <summary>PASS means every avatar's request was queued, not that an impostor exists.</summary>
        internal static string ImpostorSummary(string[] rows, int queued, int failed, int notAttempted)
        {
            var sb = new StringBuilder("[avatar-record] impostor all: queued=").Append(queued)
                .Append(" failed=").Append(failed).Append(" not-attempted=").Append(notAttempted);
            foreach (var r in rows) sb.Append(" | ").Append(r);
            return sb.Append(failed == 0 && notAttempted == 0 ? " => PASS" : " => FAIL").ToString();
        }
    }
}
