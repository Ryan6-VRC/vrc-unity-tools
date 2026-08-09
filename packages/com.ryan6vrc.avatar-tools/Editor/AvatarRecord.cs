using System;
using System.Reflection;
using System.Text;
using System.Threading;
using Ryan6Vrc.AgentTools.Editor;
using UnityEditor;
using UnityEngine;
using VRC.Core;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>Reflection into the SDK's live-content API, isolated here so the doors never hard-reference
    /// it — mirroring <see cref="CauReflect"/>'s contract: every lookup is guarded, a missing member yields
    /// a named failReason and a false return, never a throw.
    ///
    /// The members are PUBLIC statics on a public type (<c>VRC.SDKBase.Editor.Api.VRCApi</c>), so this is
    /// reflection for optionality and version-drift tolerance, not to reach anything private. Every
    /// behavioural claim below is a black-box measurement against the live API, cited at its assertion.</summary>
    internal static class VrcApiReflect
    {
        internal const string ApiTypeName = "VRC.SDKBase.Editor.Api.VRCApi, VRC.SDKBase.Editor";

        internal static Type Api => Type.GetType(ApiTypeName, throwOnError: false);

        internal static bool IsAvailable => Api != null;

        static bool TryMethod(string name, out MethodInfo m, out string failReason)
        {
            m = null; failReason = null;
            var t = Api;
            if (t == null) { failReason = "VRCApi not resolved — is the VRChat SDK installed?"; return false; }
            m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            if (m == null) { failReason = "VRCApi." + name + " not resolved (SDK drift)"; return false; }
            return true;
        }

        /// <summary>Kick <c>VRCApi.GetAvatar(id, forceRefresh: true, ct)</c>. Always forces a refresh: the
        /// whole point of this door is what is live NOW, and a cached record would let it report a name the
        /// server no longer has.</summary>
        internal static bool TryGetAvatar(string id, out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("GetAvatar", out var m, out failReason)) return false;
            try { task = m.Invoke(null, new object[] { id, true, CancellationToken.None }); }
            catch (TargetInvocationException tie) { failReason = (tie.InnerException ?? tie).Message; return false; }
            catch (Exception e) { failReason = e.Message; return false; }
            return true;
        }

        /// <summary>Kick <c>VRCApi.UpdateAvatarInfo(id, record, ct)</c> with a whole <c>VRCAvatar</c>.
        ///
        /// Posting the entire struct back is safe, measured rather than assumed (2026-08-09, against a
        /// throwaway record): a rename that changed only <c>Name</c> left Description, Tags, ImageUrl,
        /// ThumbnailImageUrl, ReleaseStatus, Lock, Featured, PendingUpload, Styles and the whole
        /// UnityPackages entry byte-identical — same package id, same asset URL, same package CreatedAt,
        /// so no bundle is touched and no thumbnail is lost. Only <c>UpdatedAt</c> and <c>Version</c> move
        /// (Version increments once per metadata write). That is why this door can take the read record,
        /// change one property, and post it whole.</summary>
        internal static bool TryUpdateAvatarInfo(string id, object record, out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("UpdateAvatarInfo", out var m, out failReason)) return false;
            try { task = m.Invoke(null, new object[] { id, record, CancellationToken.None }); }
            catch (TargetInvocationException tie) { failReason = (tie.InnerException ?? tie).Message; return false; }
            catch (Exception e) { failReason = e.Message; return false; }
            return true;
        }

        // ── Task inspection (the returned Task<VRCAvatar> is reached reflectively) ───────────────

        internal static bool IsCompleted(object task) => (bool)task.GetType().GetProperty("IsCompleted").GetValue(task);
        internal static bool IsFaulted(object task) => (bool)task.GetType().GetProperty("IsFaulted").GetValue(task);
        internal static object Result(object task) => task.GetType().GetProperty("Result").GetValue(task);

        /// <summary>Innermost exception of a faulted task, unwrapped past AggregateException.</summary>
        internal static Exception Unwrap(object task)
        {
            var ex = (Exception)task.GetType().GetProperty("Exception").GetValue(task);
            while (ex != null && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        /// <summary>Pull <c>StatusCode</c>/<c>ErrorMessage</c> off an SDK <c>ApiErrorException</c> — public
        /// FIELDS, not properties, and the only place the failure's real text lives (its <c>.Message</c> is
        /// the content-free default; see <see cref="AvatarRecordLogic.RefuseForStatus"/>). A non-API
        /// exception yields a null status and its own message, so the caller still hears something true.</summary>
        internal static void ReadApiError(Exception e, out int? statusCode, out string serverMessage)
        {
            statusCode = null;
            serverMessage = e != null ? e.Message : null;
            if (e == null) return;
            var t = e.GetType();
            if (t.Name != "ApiErrorException") return;

            var msgField = t.GetField("ErrorMessage", BindingFlags.Public | BindingFlags.Instance);
            if (msgField != null) serverMessage = msgField.GetValue(e) as string;

            var codeField = t.GetField("StatusCode", BindingFlags.Public | BindingFlags.Instance);
            if (codeField != null)
            {
                var v = codeField.GetValue(e);
                if (v != null) { try { statusCode = (int)Convert.ChangeType(v, typeof(int)); } catch { } }
            }
        }

        internal static string GetName(object record)
        {
            var p = record?.GetType().GetProperty("Name");
            return p?.GetValue(record) as string;
        }

        internal static bool TrySetName(object record, string name, out string failReason)
        {
            failReason = null;
            var p = record?.GetType().GetProperty("Name");
            if (p == null || !p.CanWrite) { failReason = "VRCAvatar.Name is not settable (SDK drift)"; return false; }
            // record is a BOXED VRCAvatar (a struct): SetValue mutates the box, and the box is what gets
            // handed to UpdateAvatarInfo. Unboxing to a local first would silently write to a copy.
            p.SetValue(record, name);
            return true;
        }

        /// <summary>The record's caller-facing digest. Ids and URLs never appear: <c>ID</c>, <c>AuthorId</c>
        /// and the image/asset URLs are omitted outright rather than redacted, and every surviving field is
        /// still run through the scrubber in case a name or description embeds one.</summary>
        internal static string Digest(object record)
        {
            if (record == null) return "<null record>";
            var t = record.GetType();
            var sb = new StringBuilder();
            Action<string> add = key =>
            {
                var p = t.GetProperty(key);
                if (p == null) return;
                object v; try { v = p.GetValue(record); } catch { return; }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(key).Append('=').Append(UploadAvatarLogic.RedactIds(v == null ? "<null>" : v.ToString()));
            };
            add("Name"); add("Description"); add("ReleaseStatus"); add("AuthorName");
            add("Version"); add("Lock"); add("Featured"); add("PendingUpload"); add("UpdatedAt");

            var tagsProp = t.GetProperty("Tags");
            var tags = tagsProp?.GetValue(record) as System.Collections.IEnumerable;
            int tagCount = 0;
            if (tags != null) foreach (var x in tags) tagCount++;
            sb.Append(" Tags=").Append(tagCount);

            var pkgProp = t.GetProperty("UnityPackages");
            var pkgs = pkgProp?.GetValue(record) as System.Collections.IEnumerable;
            int pkgCount = 0;
            if (pkgs != null) foreach (var x in pkgs) pkgCount++;
            sb.Append(" UnityPackages=").Append(pkgCount);

            var img = t.GetProperty("ThumbnailImageUrl")?.GetValue(record) as string;
            sb.Append(" hasThumbnail=").Append(!string.IsNullOrEmpty(img));
            return sb.ToString();
        }
    }

    /// <summary>Shared async drive for the record doors.
    ///
    /// <c>VRCApi</c> returns Tasks that only progress while the editor's update loop runs, so the doors
    /// cannot block: a <c>Task.ContinueWith</c> never fires and a <c>Thread.Sleep</c> starves the very pump
    /// it is waiting on. Same shape as <see cref="UploadAvatar"/>'s Run/Status split — Run kicks and
    /// returns, the update loop advances the state machine, Status() reports. Measured round trips:
    /// ~95-155 frames for one forced GetAvatar, ~550-615 for a full get→update→re-get sequence.
    ///
    /// One operation at a time, editor-lifetime, deliberately not persisted: a domain reload drops the
    /// in-flight Task, and a persisted "running" with no owning Task would be unrecoverable.</summary>
    internal static class AvatarRecordDriver
    {
        internal const int FrameBudget = 4000;

        static bool _running;
        static string _summary;
        static int _frames;
        static Func<int> _step;   // returns: 0 continue, 1 done

        internal static bool Busy => _running;

        internal static void Start(Func<int> step)
        {
            _running = true;
            _summary = null;
            _frames = 0;
            _step = step;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        internal static void Finish(string summary)
        {
            _summary = summary;
            _running = false;
            EditorApplication.update -= Tick;
        }

        internal static int Frames => _frames;

        static void Tick()
        {
            if (!_running) { EditorApplication.update -= Tick; return; }
            _frames++;
            if (_frames > FrameBudget)
            {
                Finish("[avatar-record] all => FAIL error=timed out after " + FrameBudget +
                       " editor frames with no API response; the editor window must stay focused " +
                       "(a backgrounded editor throttles its update loop and stalls the pump)");
                return;
            }
            try { _step(); }
            catch (Exception e)
            {
                Finish("[avatar-record] all => FAIL error=" +
                       UploadAvatarLogic.RedactIds((e.InnerException ?? e).Message));
            }
        }

        /// <summary>Poll. Returns the running marker or the memoized terminal summary.</summary>
        internal static string Status()
        {
            if (_running) return "[avatar-record] running… (" + _frames + " frames)";
            return _summary ?? "[avatar-record] idle — nothing has been run in this editor session";
        }

        /// <summary>Resolve GameObject → live blueprint id, or a named refusal. The id itself is returned
        /// for the API call and MUST NOT reach output (see docs/unity-tools.md's no-blueprint-ids rule).</summary>
        internal static bool TryResolveId(GameObject avatar, out string id, out string failReason)
        {
            id = null; failReason = null;
            if (avatar == null) { failReason = "avatar GameObject is null"; return false; }
            var pm = avatar.GetComponent<PipelineManager>();
            if (pm == null)
            {
                failReason = "'" + avatar.name + "' has no PipelineManager — it has never been uploaded, " +
                             "so there is no live record to read or rename";
                return false;
            }
            var prop = new SerializedObject(pm).FindProperty("blueprintId");
            id = prop != null ? prop.stringValue : pm.blueprintId;
            if (string.IsNullOrEmpty(id))
            {
                failReason = "'" + avatar.name + "' has a PipelineManager but an empty blueprintId — it " +
                             "has never been uploaded, so there is no live record";
                return false;
            }
            return true;
        }

        internal static string Refuse(string reason)
            => "[avatar-record] all => REFUSE error=" + UploadAvatarLogic.RedactIds(reason);

        /// <summary>Turn a faulted task into the door's REFUSE line, reading the API error's fields.</summary>
        internal static string RefuseFromTask(object task)
        {
            VrcApiReflect.ReadApiError(VrcApiReflect.Unwrap(task), out var code, out var msg);
            return Refuse(AvatarRecordLogic.RefuseForStatus(code, msg));
        }

        /// <summary>Guard shared by both doors: the SDK present, and no other record op in flight.</summary>
        internal static string PreflightRefusal()
        {
            if (!VrcApiReflect.IsAvailable)
                return "VRChat SDK not loaded (VRCApi unresolved) — this door needs the SDK in the project";
            if (_running)
                return "an avatar-record operation is already in flight — poll Status() until it completes";
            return null;
        }
    }

    /// <summary>Read one avatar's LIVE VRChat record — the published name, description, release status and
    /// bundle presence as the server currently has them, not as the local scene believes.
    ///
    /// This is the counterpart to the trap that motivates the family: a GameObject rename never reaches an
    /// uploaded avatar's display name, and a re-upload silently republishes under the OLD live name, so the
    /// scene is not evidence about what is published. Only this door is.
    ///
    /// Async: <c>Run</c> kicks and returns; poll <c>Status()</c>. No id or URL enters the output.</summary>
    [AgentTool]
    public static class ReportAvatarRecord
    {
        public static string Run(GameObject avatar)
        {
            var refuse = AvatarRecordDriver.PreflightRefusal();
            if (refuse != null) return AvatarRecordDriver.Refuse(refuse);
            if (!AvatarRecordDriver.TryResolveId(avatar, out var id, out var why))
                return AvatarRecordDriver.Refuse(why);
            if (!VrcApiReflect.TryGetAvatar(id, out var task, out var kickWhy))
                return AvatarRecordDriver.Refuse(kickWhy);

            string handle = avatar.name;
            AvatarRecordDriver.Start(() =>
            {
                if (!VrcApiReflect.IsCompleted(task)) return 0;
                if (VrcApiReflect.IsFaulted(task))
                {
                    AvatarRecordDriver.Finish(AvatarRecordDriver.RefuseFromTask(task));
                    return 1;
                }
                // Report emits a digest, never a verdict token (docs/tool-design.md's read-verb set).
                AvatarRecordDriver.Finish("[avatar-record] report handle=" +
                                          UploadAvatarLogic.RedactIds(handle) + " " +
                                          VrcApiReflect.Digest(VrcApiReflect.Result(task)));
                return 1;
            });
            return "[avatar-record] reading live record for '" + handle + "'; poll ReportAvatarRecord.Status()";
        }

        public static string Status() => AvatarRecordDriver.Status();
    }

    /// <summary>Rename an already-uploaded avatar's published name — metadata only, no bundle, no
    /// re-upload. The gap this closes: the SDK control panel and a full re-upload were the only ways to
    /// change a live name, and a re-upload cannot do it at all (Continuous Avatar Uploader sends a name
    /// exactly once, on the new-avatar path; an update's record carries the LIVE name and is re-fetched
    /// immediately before the bundle write, discarding anything staged).
    ///
    /// <paramref name="expectCurrentName"/> is required and is the guard rail: the write targets whatever
    /// blueprintId the GameObject's PipelineManager holds, so stating the name you believe is live turns a
    /// mis-wired id into a refusal instead of a rename landing on the wrong published avatar. Chain it
    /// straight from <see cref="ReportAvatarRecord"/>'s reported name.
    ///
    /// <c>whatIf</c> reads the record and reports what WOULD change, writing nothing.</summary>
    [AgentTool]
    public static class RenameAvatarRecord
    {
        public static string Run(GameObject avatar, string newName, string expectCurrentName,
                                 bool whatIf = false)
        {
            var refuse = AvatarRecordDriver.PreflightRefusal();
            if (refuse != null) return AvatarRecordDriver.Refuse(refuse);

            var nameWhy = AvatarRecordLogic.ValidateNewName(newName);
            if (nameWhy != null) return AvatarRecordDriver.Refuse(nameWhy);

            if (!AvatarRecordDriver.TryResolveId(avatar, out var id, out var why))
                return AvatarRecordDriver.Refuse(why);
            if (!VrcApiReflect.TryGetAvatar(id, out var getTask, out var kickWhy))
                return AvatarRecordDriver.Refuse(kickWhy);

            string handle = avatar.name;
            string marker = whatIf ? " (whatIf)" : "";
            object updateTask = null;
            int stage = 0;

            AvatarRecordDriver.Start(() =>
            {
                if (stage == 0)
                {
                    if (!VrcApiReflect.IsCompleted(getTask)) return 0;
                    if (VrcApiReflect.IsFaulted(getTask))
                    {
                        AvatarRecordDriver.Finish(AvatarRecordDriver.RefuseFromTask(getTask));
                        return 1;
                    }
                    var record = VrcApiReflect.Result(getTask);
                    string liveName = VrcApiReflect.GetName(record);

                    var casWhy = AvatarRecordLogic.CheckExpectedName(expectCurrentName, liveName);
                    if (casWhy != null) { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(casWhy)); return 1; }

                    if (whatIf)
                    {
                        AvatarRecordDriver.Finish(
                            "[avatar-record]" + marker + " rename handle=" + UploadAvatarLogic.RedactIds(handle) +
                            " from=" + AvatarRecordLogic.Quote(liveName) +
                            " to=" + AvatarRecordLogic.Quote(newName) +
                            " (nothing written; the landed name may be sanitized server-side and is only " +
                            "knowable after a real run) => PASS");
                        return 1;
                    }

                    if (!VrcApiReflect.TrySetName(record, newName, out var setWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(setWhy)); return 1; }

                    if (!VrcApiReflect.TryUpdateAvatarInfo(id, record, out updateTask, out var upWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(upWhy)); return 1; }

                    stage = 1;
                    return 0;
                }

                if (!VrcApiReflect.IsCompleted(updateTask)) return 0;
                if (VrcApiReflect.IsFaulted(updateTask))
                {
                    AvatarRecordDriver.Finish(AvatarRecordDriver.RefuseFromTask(updateTask));
                    return 1;
                }
                // The UPDATE RESPONSE already carries the server-sanitized name (measured), so the landed
                // value needs no extra GetAvatar round trip. Reporting the submitted name here instead
                // would misreport every name the server rewrites.
                var landedRec = VrcApiReflect.Result(updateTask);
                string landed = VrcApiReflect.GetName(landedRec);
                AvatarRecordDriver.Finish(
                    "[avatar-record] rename handle=" + UploadAvatarLogic.RedactIds(handle) +
                    " landedName=" + AvatarRecordLogic.Quote(landed) +
                    " (" + AvatarRecordLogic.DescribeNameLanding(newName, landed) + ") " +
                    VrcApiReflect.Digest(landedRec) + " => PASS");
                return 1;
            });

            return "[avatar-record]" + marker + " renaming '" + handle + "'; poll RenameAvatarRecord.Status()";
        }

        public static string Status() => AvatarRecordDriver.Status();
    }
}
