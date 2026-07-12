using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>
    /// Self-driving VRChat upload for a batch of avatars, driving CAU (optional) by reflection.
    /// Operator-gated (the skill's explicit "upload now" is the trigger), fail-loud, no auto-work-around.
    /// PASS/REFUSE/FAIL — REFUSE = environment not ready (CAU absent, not logged in, panel closed,
    /// wrong build target); FAIL = a genuine upload rejection. Spec: 2026-07-11-upload-readiness-design.md.
    /// </summary>
    [AgentTool]
    public static class UploadAvatar
    {
        public static string Run(GameObject[] avatars, bool whatIf = false)
        {
            var log = new UploadAvatarLog();
            if (!CauReflect.IsAvailable)
            {
                log.result = "REFUSE";
                log.error  = "CAU not installed; self-driving upload unavailable — install " +
                             "com.anatawa12.continuous-avatar-uploader (ALCOM), or upload via the SDK panel";
                return log.Finish("all");
            }
            log.result = "REFUSE";
            log.error  = "not yet implemented";
            return log.Finish("all");
        }
    }

    /// <summary>Tool-local RunLog accumulator. This is a STUB for Task 0; Task 4 replaces Finish with the
    /// structured avatars[] writer (kind="upload-avatar", envelope via TransplantCore.Q/Sanitize).</summary>
    internal sealed class UploadAvatarLog
    {
        public string result = "PASS";
        public string error;
        public string Finish(string label)
            => "[upload-avatar] " + label + " => " + result + (error != null ? " error=" + error : "");
    }
}
