using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Core;
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

            if (whatIf)
            {
                // Preflight the whole REFUSE register once (env not ready → no per-avatar work).
                var refuse = CheckPreconditions();
                if (refuse != null)
                {
                    log.result = "REFUSE";
                    log.error  = refuse;
                    return log.Finish("all");
                }

                // Per-avatar read-only classification. Uploads nothing, dirties nothing.
                int n = avatars != null ? avatars.Length : 0;
                var rows = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    var go = avatars[i];
                    var (state, publishName) = ClassifyAvatar(go);
                    bool noPm = go == null || go.GetComponent<PipelineManager>() == null;
                    if (rows.Length > 0) rows.Append("; ");
                    rows.Append("handle=").Append(go != null ? go.name : "(null)")
                        .Append(" state=").Append(state)
                        .Append(" publishName=").Append(publishName);
                    if (noPm) rows.Append(" (no PipelineManager — never uploaded)");
                }
                return "[upload-avatar] (whatIf) " + n + " avatar(s): " + rows + " => PASS";
            }

            log.result = "REFUSE";
            log.error  = "not yet implemented";
            return log.Finish("all");
        }

        /// <summary>Read-only per-avatar preflight read: (blueprint-state, name-that-would-publish). Reads
        /// PipelineManager.blueprintId via SerializedObject and never surfaces the id itself. No PipelineManager
        /// → treated as first-upload (its absence means the avatar was never uploaded). Mutates nothing.</summary>
        public static (string state, string publishName) ClassifyAvatar(GameObject go)
        {
            var pm = go.GetComponent<PipelineManager>();
            if (pm == null) return (UploadAvatarLogic.ClassifyBlueprint(null), go.name);
            var prop = new SerializedObject(pm).FindProperty("blueprintId");
            string id = prop != null ? prop.stringValue : pm.blueprintId;
            return (UploadAvatarLogic.ClassifyBlueprint(id), go.name);
        }

        /// <summary>The upload REFUSE register: returns the first unmet precondition (with its named fix) or
        /// null when the environment is upload-ready. Order is cheap-to-costly.</summary>
        static string CheckPreconditions()
        {
            if (EditorApplication.isPlaying)
                return "in Play mode — exit Play mode before uploading";
            if (!APIUser.IsLoggedIn)
                return "not logged into the VRChat SDK — open the Control Panel and sign in";
            if (!CauReflect.TryGetBuilder(out _, out var why))
                return why;
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                return "build target is " + EditorUserBuildSettings.activeBuildTarget +
                       "; switch to Windows (StandaloneWindows64)";
            return null;
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
