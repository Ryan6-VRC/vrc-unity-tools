using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>VRC-free decision helpers for UploadAvatar — unit-tested, no editor/SDK deps.</summary>
    internal static class UploadAvatarLogic
    {
        static readonly Regex Url = new Regex(@"https?://\S+", RegexOptions.Compiled);
        static readonly Regex Avtr = new Regex(@"avtr_[A-Za-z0-9\-]+", RegexOptions.Compiled);
        static readonly Regex Usr  = new Regex(@"usr_[A-Za-z0-9\-]+",  RegexOptions.Compiled);

        /// <summary>Scrub avatar/user IDs and URLs from any string before it enters output or a RunLog
        /// (public-repo hygiene — forwarded SDK error strings routinely embed these).</summary>
        internal static string RedactIds(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Url.Replace(s, "<redacted-url>");
            s = Avtr.Replace(s, "<redacted-id>");
            s = Usr.Replace(s, "<redacted-id>");
            return s;
        }

        /// <summary>transient | rate-limit | real. 429 is its own class (never auto-retried);
        /// a ValidationException or any non-429 4xx is real; other 5xx / timeout is transient.</summary>
        internal static string Classify(int? httpStatus, bool isValidationException, bool isTimeout)
        {
            if (isValidationException) return "real";
            if (httpStatus == 429) return "rate-limit";
            if (isTimeout) return "transient";
            if (httpStatus.HasValue && httpStatus >= 500) return "transient";
            if (httpStatus.HasValue && httpStatus >= 400) return "real";
            return "transient";
        }

        internal static string ClassifyBlueprint(string blueprintId)
            => string.IsNullOrEmpty(blueprintId) ? "first-upload" : "update";

        /// <summary>Per-handle hard attempt cap so account-safety never depends on skill prose.</summary>
        internal sealed class AttemptLedger
        {
            internal const int MaxAttempts = 3;
            readonly Dictionary<string, int> _n = new Dictionary<string, int>();
            internal bool MayAttempt(string handle)
                => !_n.TryGetValue(handle, out var c) || c < MaxAttempts;
            internal void Record(string handle)
                => _n[handle] = (_n.TryGetValue(handle, out var c) ? c : 0) + 1;
        }
    }
}
