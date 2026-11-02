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
    /// <summary>Reflection into the SDK's live-content API, so the doors never hard-reference it: the
    /// <c>Try</c> methods turn a missing member into a named failReason and a false return, never a throw.
    /// The members are public statics on a public type, so the reflection buys optionality and
    /// version-drift tolerance, not access. Contract: <c>docs/unity-tools.md</c> §Publish.</summary>
    internal static class VrcApiReflect
    {
        internal const string ApiTypeName = "VRC.SDKBase.Editor.Api.VRCApi, VRC.SDKBase.Editor";

        internal static Type Api => Type.GetType(ApiTypeName, throwOnError: false);

        internal static bool IsAvailable => Api != null;

        /// <summary>Resolve a static method by name. <c>GetMethod(name, flags)</c> throws
        /// AmbiguousMatchException once the SDK adds an overload, so the lookup itself is guarded.</summary>
        static bool TryMethod(string name, out MethodInfo m, out string failReason)
        {
            m = null; failReason = null;
            var t = Api;
            if (t == null) { failReason = "VRCApi not resolved — is the VRChat SDK installed?"; return false; }
            try { m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static); }
            catch (AmbiguousMatchException)
            {
                failReason = "VRCApi." + name + " is overloaded in this SDK — this door binds a single " +
                             "signature and cannot choose (SDK drift)";
                return false;
            }
            if (m == null) { failReason = "VRCApi." + name + " not resolved (SDK drift)"; return false; }
            return true;
        }

        static bool Invoke(MethodInfo m, object[] args, out object task, out string failReason)
        {
            task = null; failReason = null;
            object raw;
            try { raw = m.Invoke(null, args); }
            catch (TargetInvocationException tie) { failReason = (tie.InnerException ?? tie).Message; return false; }
            catch (Exception e) { failReason = e.Message; return false; }
            if (!(raw is System.Threading.Tasks.Task))
            {
                failReason = "VRCApi." + m.Name + " returned " + (raw?.GetType().Name ?? "null") +
                             ", not a Task (SDK drift)";
                return false;
            }
            task = raw;
            return true;
        }

        /// <summary>Kick <c>VRCApi.GetAvatar(id, forceRefresh: true, ct)</c>.</summary>
        internal static bool TryGetAvatar(string id, out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("GetAvatar", out var m, out failReason)) return false;
            return Invoke(m, new object[] { id, true, CancellationToken.None }, out task, out failReason);
        }

        /// <summary>Kick <c>VRCApi.UpdateAvatarInfo(id, record, ct)</c> with a whole <c>VRCAvatar</c>. Why a
        /// whole-record post is safe: <c>docs/unity-tools.md</c> §Publish.</summary>
        internal static bool TryUpdateAvatarInfo(string id, object record, out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("UpdateAvatarInfo", out var m, out failReason)) return false;
            return Invoke(m, new object[] { id, record, CancellationToken.None }, out task, out failReason);
        }

        /// <summary>Kick <c>VRCApi.UpdateAvatarImage(id, record, pathToImage, onProgress, ct)</c>.
        ///
        /// Unlike <see cref="TryUpdateAvatarInfo"/> this does NOT post the record: it uploads the file and
        /// then PUTs <c>{"imageUrl": …}</c> alone. Two fields are read off <paramref name="record"/> — the
        /// name, only to label the uploaded file, and the image url, from which the SDK derives the file id
        /// the new image is stored as a VERSION of. That is why the record must be one the API just handed
        /// back: a hand-built one has no image url, so the derivation silently takes the new-file branch.</summary>
        internal static bool TryUpdateAvatarImage(string id, object record, string pathToImage,
                                                  Action<string, float> onProgress,
                                                  out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("UpdateAvatarImage", out var m, out failReason)) return false;
            return Invoke(m, new object[] { id, record, pathToImage, onProgress, CancellationToken.None },
                          out task, out failReason);
        }

        /// <summary>Kick <c>POST avatars/{id}/impostor/enqueue</c> through the SDK's generic <c>Post</c> with a
        /// null body. The response is the queued job, as a dictionary.</summary>
        internal static bool TryEnqueueImpostor(string id, out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("Post", out var open, out failReason)) return false;
            MethodInfo m;
            try { m = open.MakeGenericMethod(typeof(object), typeof(System.Collections.Generic.Dictionary<string, object>)); }
            catch (Exception e) { failReason = "VRCApi.Post is not the generic <T, TResponse> it was (SDK drift): " + e.Message; return false; }
            var ps = m.GetParameters();
            if (ps.Length < 2 || ps[0].ParameterType != typeof(string))
            { failReason = "VRCApi.Post's signature changed (SDK drift)"; return false; }
            // Trailing parameters take their declared defaults, so a new optional one does not break the
            // call. Invoke reads a null for a struct parameter as its default value.
            var args = new object[ps.Length];
            args[0] = "avatars/" + id + "/impostor/enqueue";
            for (int i = 2; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            return Invoke(m, args, out task, out failReason);
        }

        /// <summary>The queued job's <c>state</c>, the one field of it that is safe to emit: the job also
        /// carries the avatar and requester ids.</summary>
        internal static string JobState(object job)
        {
            var d = job as System.Collections.Generic.IDictionary<string, object>;
            return d != null && d.TryGetValue("state", out var s) ? s as string : null;
        }

        /// <summary>The HTTP status of a faulted task, when anything in its chain carries one.</summary>
        internal static int? FaultStatus(object task)
        {
            var ex = (Exception)task.GetType().GetProperty("Exception").GetValue(task);
            FindClassifiable(ex, out var code, out _);
            return code;
        }

        internal static string GetImageUrl(object record) => GetString(record, "ImageUrl");

        /// <summary>The innermost message of a faulted task, for the one case the status classifier cannot
        /// reach: <c>UploadException</c> exposes no status field, so its text is the only signal there is.</summary>
        internal static string DeepestMessage(object task)
        {
            var ex = (Exception)task.GetType().GetProperty("Exception").GetValue(task);
            while (ex != null && ex.InnerException != null) ex = ex.InnerException;
            return ex?.Message;
        }

        // ── Task inspection (the returned Task<T> is reached reflectively) ──────────────────────

        internal static bool IsCompleted(object task) => (bool)task.GetType().GetProperty("IsCompleted").GetValue(task);
        internal static bool IsFaulted(object task) => (bool)task.GetType().GetProperty("IsFaulted").GetValue(task);
        internal static object Result(object task) => task.GetType().GetProperty("Result").GetValue(task);

        // ── API error classification ────────────────────────────────────────────────────────────

        /// <summary>The first exception in a faulted task's chain that carries API signal, not simply the
        /// innermost: the SDK wraps a JSON failure in its own exception, so the innermost link can be a
        /// Newtonsoft error with no status.</summary>
        static Exception FindClassifiable(Exception e, out int? statusCode, out string serverMessage)
        {
            statusCode = null; serverMessage = null;
            if (e == null) return null;

            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    var hit = FindClassifiable(inner, out statusCode, out serverMessage);
                    if (hit != null) return hit;
                }
                return null;
            }

            if (TryReadApiFields(e, out statusCode, out serverMessage)) return e;
            return FindClassifiable(e.InnerException, out statusCode, out serverMessage);
        }

        /// <summary>Read <c>StatusCode</c>/<c>ErrorMessage</c> off an SDK API exception.
        ///
        /// Matched by SHAPE, not by exact type name: the SDK's moderation rejection and its empty-body
        /// failure are distinct types (one derives from the base error type, one does not), and a
        /// name-equality test would drop both, including the 422 that a refused name produces.</summary>
        internal static bool TryReadApiFields(Exception e, out int? statusCode, out string serverMessage)
        {
            statusCode = null; serverMessage = null;
            var t = e.GetType();

            var codeField = t.GetField("StatusCode", BindingFlags.Public | BindingFlags.Instance);
            if (codeField == null) return false;
            object raw;
            try { raw = codeField.GetValue(e); } catch { return false; }
            if (raw == null) return false;
            try { statusCode = (int)Convert.ChangeType(raw, typeof(int)); } catch { return false; }

            var msgField = t.GetField("ErrorMessage", BindingFlags.Public | BindingFlags.Instance);
            if (msgField != null) { try { serverMessage = msgField.GetValue(e) as string; } catch { } }
            return true;
        }

        /// <summary>Classify a faulted task into the door's REFUSE text.</summary>
        internal static string DescribeFault(object task, bool forImage = false)
        {
            var ex = (Exception)task.GetType().GetProperty("Exception").GetValue(task);
            var hit = FindClassifiable(ex, out var code, out var msg);
            if (hit != null) return AvatarRecordLogic.RefuseForStatus(code, msg, forImage);

            // No link carried API signal, so the innermost message is all there is. Scrubbed: an arbitrary
            // exception string can carry an id or URL.
            var deepest = ex;
            while (deepest != null && deepest.InnerException != null) deepest = deepest.InnerException;
            return "VRChat API call failed: " +
                   AvatarRecordLogic.Escape(UploadAvatarLogic.RedactIds(deepest?.Message ?? "no exception detail"));
        }

        // ── Record reads / writes ───────────────────────────────────────────────────────────────

        internal static string GetString(object record, string property)
            => record?.GetType().GetProperty(property)?.GetValue(record) as string;

        internal static string GetName(object record) => GetString(record, "Name");

        /// <summary>Write one property on the BOXED <c>VRCAvatar</c>. The record is a struct, <c>SetValue</c>
        /// mutates the box, and that same box is what goes to <c>UpdateAvatarInfo</c>: unboxing to a local
        /// first would silently write to a copy and post the record back unchanged while reporting
        /// success.</summary>
        internal static bool TrySet(object record, string property, object value, out string failReason)
        {
            failReason = null;
            var p = record?.GetType().GetProperty(property);
            if (p == null || !p.CanWrite)
            {
                failReason = "VRCAvatar." + property + " is not settable (SDK drift) — this door cannot " +
                             "change that field against the installed SDK";
                return false;
            }
            try { p.SetValue(record, value); }
            catch (Exception e)
            {
                failReason = "VRCAvatar." + property + " rejected the value: " + (e.InnerException ?? e).Message;
                return false;
            }
            return true;
        }

        internal static string[] GetTags(object record)
        {
            var raw = record?.GetType().GetProperty("Tags")?.GetValue(record) as System.Collections.IEnumerable;
            if (raw == null) return null;
            var list = new System.Collections.Generic.List<string>();
            foreach (var t in raw) list.Add(t as string);
            return list.ToArray();
        }

        internal static bool TrySetTags(object record, string[] tags, out string failReason)
        {
            failReason = null;
            var p = record?.GetType().GetProperty("Tags");
            if (p == null || !p.CanWrite)
            { failReason = "VRCAvatar.Tags is not settable (SDK drift)"; return false; }

            object collection;
            try
            {
                collection = Activator.CreateInstance(p.PropertyType);
                var add = p.PropertyType.GetMethod("Add", new[] { typeof(string) });
                if (add == null)
                {
                    failReason = "VRCAvatar.Tags is " + p.PropertyType.Name +
                                 ", which has no Add(string) (SDK drift)";
                    return false;
                }
                foreach (var t in tags) add.Invoke(collection, new object[] { t });
            }
            catch (Exception e)
            {
                failReason = "could not build a Tags collection of type " + p.PropertyType.Name + ": " +
                             (e.InnerException ?? e).Message;
                return false;
            }
            return TrySet(record, "Tags", collection, out failReason);
        }

        /// <summary>The record's caller-facing digest. Ids and URLs (<c>ID</c>, <c>AuthorId</c>,
        /// <c>AuthorName</c>, the image and asset URLs) are OMITTED outright; an account identifier is barred
        /// from output as firmly as a blueprint id. The published TEXT fields are emitted verbatim (escaped,
        /// not scrubbed): a caller chains them into <see cref="UpdateAvatarRecord"/>'s
        /// <c>expectCurrentName</c>, and a scrubbed string could never match that check.</summary>
        internal static string Digest(object record)
        {
            if (record == null) return "<null record>";
            var t = record.GetType();
            var sb = new StringBuilder();

            sb.Append("name=").Append(AvatarRecordLogic.Quote(GetName(record)));
            sb.Append(" description=").Append(AvatarRecordLogic.Quote(GetString(record, "Description")));
            sb.Append(" tags=").Append(AvatarRecordLogic.FormatTags(GetTags(record)));

            Action<string> plain = key =>
            {
                var p = t.GetProperty(key);
                if (p == null) return;
                object v; try { v = p.GetValue(record); } catch { return; }
                sb.Append(' ').Append(key).Append('=').Append(v == null ? "<null>" : v.ToString());
            };
            plain("ReleaseStatus"); plain("Version"); plain("Lock");
            plain("Featured"); plain("PendingUpload"); plain("UpdatedAt");

            var pkgs = t.GetProperty("UnityPackages")?.GetValue(record) as System.Collections.IEnumerable;
            int pkgCount = 0;
            if (pkgs != null) foreach (var x in pkgs) pkgCount++;
            sb.Append(" unityPackages=").Append(pkgCount);
            // No impostor field: this record lists no impostor package even for an avatar that has one, so
            // a count here would read 0 either way.

            var img = GetString(record, "ThumbnailImageUrl");
            sb.Append(" hasThumbnail=").Append(!string.IsNullOrEmpty(img));
            return sb.ToString();
        }
    }

    /// <summary>Shared async drive for the record doors, one operation at a time. <c>VRCApi</c> returns Tasks
    /// that only progress while the editor's update loop runs, so a door cannot block on one: a
    /// <c>Thread.Sleep</c> starves the pump it is waiting on. Run kicks and returns, the update loop advances
    /// the state machine, Status() reports. A domain reload destroys the in-flight Task, so only the fact of
    /// an interruption persists, in SessionState.</summary>
    internal static class AvatarRecordDriver
    {
        internal const int FrameBudget = 4000;
        const string BreadcrumbKey = "Ryan6Vrc.AvatarRecord.pending";
        const char Sep = '\u001f';

        static bool _running;
        static string _summary;
        static string _summaryOwner;
        static int _frames;
        static Action _step;
        static string _door, _handle;
        static AvatarRecordLogic.Phase _phase;

        internal static bool Busy => _running;
        internal static int Frames => _frames;

        /// <summary>Convert a breadcrumb that outlived its editor session into an explicit verdict, so
        /// <c>Status()</c> does not answer "idle" about a write that may already have landed.</summary>
        [InitializeOnLoadMethod]
        static void ReconcileInterrupted()
        {
            var crumb = SessionState.GetString(BreadcrumbKey, "");
            if (string.IsNullOrEmpty(crumb)) return;
            SessionState.EraseString(BreadcrumbKey);

            var parts = crumb.Split(new[] { Sep }, 3);
            if (parts.Length < 3) return;
            // An unrecognised char resolves to UpdateSent, like "w", not to Reading: Reading's verdict asserts
            // "nothing was written", which a crumb this build cannot parse does not support.
            AvatarRecordLogic.Phase phase;
            switch (parts[1])
            {
                case "r": phase = AvatarRecordLogic.Phase.Reading; break;
                case "i": phase = AvatarRecordLogic.Phase.ImageSent; break;
                case "q": phase = AvatarRecordLogic.Phase.EnqueueSent; break;
                default:  phase = AvatarRecordLogic.Phase.UpdateSent; break;
            }
            _summaryOwner = parts[0];
            _summary = AvatarRecordLogic.InterruptedVerdict(
                parts[0], parts[2], phase, "the editor reloaded (recompile or play-mode entry)");
        }

        static string PhaseChar(AvatarRecordLogic.Phase p)
        {
            if (p == AvatarRecordLogic.Phase.UpdateSent) return "w";
            if (p == AvatarRecordLogic.Phase.ImageSent) return "i";
            if (p == AvatarRecordLogic.Phase.EnqueueSent) return "q";
            return "r";
        }

        static void WriteBreadcrumb()
            => SessionState.SetString(BreadcrumbKey, _door + Sep + PhaseChar(_phase) + Sep + _handle);

        /// <summary>Called by a door immediately BEFORE it hands its update request to the API, because the
        /// call itself is the window to protect. From here a lost operation reports UNKNOWN rather than
        /// FAIL.</summary>
        internal static void MarkUpdateSent()
        {
            _phase = AvatarRecordLogic.Phase.UpdateSent;
            WriteBreadcrumb();
        }

        /// <summary>As above, for the image attach.</summary>
        internal static void MarkImageSent()
        {
            _phase = AvatarRecordLogic.Phase.ImageSent;
            WriteBreadcrumb();
        }

        /// <summary>Called by the impostor batch before its first request leaves.</summary>
        internal static void MarkEnqueueSent()
        {
            _phase = AvatarRecordLogic.Phase.EnqueueSent;
            WriteBreadcrumb();
        }

        /// <summary>Count observable progress as liveness, resetting the frame budget: a file upload holds
        /// seconds of unconditional delay and a multi-part transfer, so a budget it cannot reset would time
        /// out a healthy attach. The SDK can invoke this off the main thread, which is safe here only because
        /// the write is a single int the tick loop re-reads.
        ///
        /// Gated on the calling door still owning the driver: the progress closure is handed to the SDK with
        /// no cancellation token, so it outlives a Finish(), and an orphaned upload would otherwise keep
        /// zeroing the counter and disarm the stall detector of whatever runs next.</summary>
        internal static void NoteProgress(string door)
        {
            if (_running && _door == door) _frames = 0;
        }

        internal static void Start(string door, string handle, Action step)
        {
            _running = true;
            _summary = null;
            _summaryOwner = door;
            _frames = 0;
            _step = step;
            _door = door;
            _handle = handle;
            _phase = AvatarRecordLogic.Phase.Reading;
            WriteBreadcrumb();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        internal static void Finish(string summary)
        {
            _summary = summary;
            _running = false;
            SessionState.EraseString(BreadcrumbKey);
            EditorApplication.update -= Tick;
        }

        static void Tick()
        {
            if (!_running) { EditorApplication.update -= Tick; return; }
            _frames++;
            if (_frames > FrameBudget)
            {
                Finish(AvatarRecordLogic.InterruptedVerdict(
                    _door, _handle, _phase,
                    "timed out after " + FrameBudget + " editor frames (keep the editor window focused — " +
                    "a backgrounded editor throttles its update loop and stalls the pump)"));
                return;
            }
            try { _step(); }
            catch (Exception e)
            {
                Finish(Refuse(_door, "unhandled error: " +
                              UploadAvatarLogic.RedactIds((e.InnerException ?? e).Message)));
            }
        }

        /// <summary>Poll, scoped to the asking door, so a caller never reads another door's terminal line as
        /// its own.</summary>
        internal static string Status(string door)
        {
            if (_running)
                return _door == door
                    ? "[avatar-record] " + door + " running… (" + _frames + " frames)"
                    : "[avatar-record] " + door + " => REFUSE error=no operation of your own; " + _door +
                      " is running (" + _frames + " frames). Poll " + _door + ".Status().";
            if (_summary == null)
                return "[avatar-record] " + door + " idle — nothing has been run in this editor session";
            if (_summaryOwner != door)
                return "[avatar-record] " + door + " idle — nothing of your own has been run; the last " +
                       "result belongs to " + _summaryOwner + ", poll that door for it";
            return _summary;
        }

        /// <summary>Clear a stale result this door owns, so a synchronous refusal is not followed by a poll
        /// that returns the door's own PREVIOUS success.</summary>
        internal static void ClearIfOwnedBy(string door)
        {
            if (!_running && _summaryOwner == door) { _summary = null; _summaryOwner = null; }
        }

        /// <summary>Resolve GameObject → local blueprint id, or a named refusal. The id is returned for the
        /// API call and MUST NOT reach output (docs/unity-tools.md's no-blueprint-ids rule).</summary>
        internal static bool TryResolveId(GameObject avatar, out string id, out string failReason)
        {
            id = null; failReason = null;
            if (avatar == null) { failReason = "avatar GameObject is null"; return false; }
            var pm = avatar.GetComponent<PipelineManager>();
            if (pm == null)
            {
                failReason = "'" + AvatarRecordLogic.Escape(avatar.name) + "' has no PipelineManager — it " +
                             "has never been uploaded, so there is no live record to read or change";
                return false;
            }
            var prop = new SerializedObject(pm).FindProperty("blueprintId");
            id = prop != null ? prop.stringValue : pm.blueprintId;
            if (string.IsNullOrEmpty(id))
            {
                failReason = "'" + AvatarRecordLogic.Escape(avatar.name) + "' has a PipelineManager but an " +
                             "empty blueprintId — it has never been uploaded, so there is no live record";
                return false;
            }
            return true;
        }

        /// <summary>The refusal funnel.</summary>
        internal static string Refuse(string door, string reason)
            => "[avatar-record] " + door + " => REFUSE error=" + UploadAvatarLogic.RedactIds(reason);

        internal static string PreflightRefusal()
        {
            if (!VrcApiReflect.IsAvailable)
                return "VRChat SDK not loaded (VRCApi unresolved) — this door needs the SDK in the project";
            if (_running)
                return "an avatar-record operation is already in flight (" + _door + ") — poll " + _door +
                       ".Status() until it completes";
            return null;
        }
    }

    /// <summary>Read one avatar's LIVE VRChat record as the server holds it. Async: <c>Run</c> kicks and
    /// returns; poll <c>Status()</c>. No id, account name or URL enters output.
    /// Contract: <c>docs/unity-tools.md</c> §Publish.</summary>
    [AgentTool]
    public static class ReportAvatarRecord
    {
        internal const string Door = "ReportAvatarRecord";

        public static string Run(GameObject avatar)
        {
            AvatarRecordDriver.ClearIfOwnedBy(Door);
            var refuse = AvatarRecordDriver.PreflightRefusal();
            if (refuse != null) return AvatarRecordDriver.Refuse(Door, refuse);
            if (!AvatarRecordDriver.TryResolveId(avatar, out var id, out var why))
                return AvatarRecordDriver.Refuse(Door, why);
            if (!VrcApiReflect.TryGetAvatar(id, out var task, out var kickWhy))
                return AvatarRecordDriver.Refuse(Door, kickWhy);

            string handle = avatar.name;
            AvatarRecordDriver.Start(Door, handle, () =>
            {
                if (!VrcApiReflect.IsCompleted(task)) return;
                if (VrcApiReflect.IsFaulted(task))
                {
                    AvatarRecordDriver.Finish(
                        AvatarRecordDriver.Refuse(Door, VrcApiReflect.DescribeFault(task)));
                    return;
                }
                // A digest, not a verdict (docs/tool-design.md's read-verb set).
                AvatarRecordDriver.Finish("[avatar-record] report handle=" +
                                          AvatarRecordLogic.Quote(handle) + " " +
                                          VrcApiReflect.Digest(VrcApiReflect.Result(task)));
            });
            return "[avatar-record] reading the live record for " + AvatarRecordLogic.Quote(handle) +
                   "; poll ReportAvatarRecord.Status()";
        }

        public static string Status() => AvatarRecordDriver.Status(Door);
    }

    /// <summary>Edit an already-uploaded avatar's published record — name, description, tags and thumbnail.
    /// No bundle and no re-upload. Contract and the reasons behind each guard: <c>docs/unity-tools.md</c>
    /// §Publish.
    ///
    /// A call setting both metadata and an image can half-succeed, which closes <c>=&gt; FAIL</c> with the
    /// metadata's landing rows still shown. There is no rollback: the remedy is re-running, which the SDK's
    /// own cleanup branches make safe. <c>whatIf</c> reads the record and reports what WOULD change, writing
    /// nothing.</summary>
    [AgentTool]
    public static class UpdateAvatarRecord
    {
        internal const string Door = "UpdateAvatarRecord";

        public static string Run(GameObject avatar, string expectCurrentName, string newName = null,
                                 string newDescription = null, string[] newTags = null,
                                 string newImagePath = null, bool whatIf = false)
        {
            AvatarRecordDriver.ClearIfOwnedBy(Door);
            var refuse = AvatarRecordDriver.PreflightRefusal();
            if (refuse != null) return AvatarRecordDriver.Refuse(Door, refuse);

            var nothing = AvatarRecordLogic.CheckSomethingToDo(newName, newDescription, newTags, newImagePath);
            if (nothing != null) return AvatarRecordDriver.Refuse(Door, nothing);
            if (newName != null)
            {
                var nameWhy = AvatarRecordLogic.ValidateNewName(newName);
                if (nameWhy != null) return AvatarRecordDriver.Refuse(Door, nameWhy);
            }
            var tagsWhy = AvatarRecordLogic.ValidateTags(newTags);
            if (tagsWhy != null) return AvatarRecordDriver.Refuse(Door, tagsWhy);
            var imgWhy = AvatarRecordLogic.ValidateImagePath(newImagePath, System.IO.File.Exists);
            if (imgWhy != null) return AvatarRecordDriver.Refuse(Door, imgWhy);

            if (!AvatarRecordDriver.TryResolveId(avatar, out var id, out var why))
                return AvatarRecordDriver.Refuse(Door, why);
            if (!VrcApiReflect.TryGetAvatar(id, out var getTask, out var kickWhy))
                return AvatarRecordDriver.Refuse(Door, kickWhy);

            string handle = avatar.name;
            string marker = whatIf ? " (whatIf)" : "";
            bool writesMetadata = newName != null || newDescription != null || newTags != null;
            object updateTask = null, imageTask = null;
            object carriedRec = null;          // freshest record the door holds
            string metaLandings = "";
            string beforeImageUrl = null;
            int stage = 0;

            Action kickImage = () =>
            {
                beforeImageUrl = VrcApiReflect.GetImageUrl(carriedRec);
                AvatarRecordDriver.MarkImageSent();
                Action<string, float> progress = (s, f) => AvatarRecordDriver.NoteProgress(Door);
                if (!VrcApiReflect.TryUpdateAvatarImage(id, carriedRec, newImagePath, progress,
                                                        out imageTask, out var imgKickWhy))
                {
                    // A bare Refuse would read as "nothing happened", false once the metadata PUT has landed,
                    // and would send the caller back with an expectCurrentName a rename has invalidated.
                    AvatarRecordDriver.Finish(writesMetadata
                        ? "[avatar-record] update handle=" + AvatarRecordLogic.Quote(handle) + " " +
                          VrcApiReflect.Digest(carriedRec) + metaLandings +
                          " | image FAILED to start: " + imgKickWhy + " => FAIL"
                        : AvatarRecordDriver.Refuse(Door, imgKickWhy));
                    return;
                }
                stage = 2;
            };

            AvatarRecordDriver.Start(Door, handle, () =>
            {
                if (stage == 0)
                {
                    if (!VrcApiReflect.IsCompleted(getTask)) return;
                    if (VrcApiReflect.IsFaulted(getTask))
                    {
                        AvatarRecordDriver.Finish(
                            AvatarRecordDriver.Refuse(Door, VrcApiReflect.DescribeFault(getTask)));
                        return;
                    }
                    var record = VrcApiReflect.Result(getTask);
                    string liveName = VrcApiReflect.GetName(record);

                    var casWhy = AvatarRecordLogic.CheckExpectedName(expectCurrentName, liveName);
                    if (casWhy != null)
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, casWhy)); return; }

                    if (whatIf)
                    {
                        var plan = new StringBuilder();
                        if (newName != null)
                            plan.Append(" name: ").Append(AvatarRecordLogic.Quote(liveName))
                                .Append(" -> ").Append(AvatarRecordLogic.Quote(newName));
                        if (newDescription != null)
                            plan.Append(" description: ")
                                .Append(AvatarRecordLogic.Quote(VrcApiReflect.GetString(record, "Description")))
                                .Append(" -> ").Append(AvatarRecordLogic.Quote(newDescription));
                        if (newTags != null)
                            plan.Append(" tags: ")
                                .Append(AvatarRecordLogic.FormatTags(VrcApiReflect.GetTags(record)))
                                .Append(" -> ").Append(AvatarRecordLogic.FormatTags(newTags));
                        if (newImagePath != null)
                            plan.Append(" image: would upload ").Append(AvatarRecordLogic.Quote(newImagePath));
                        AvatarRecordDriver.Finish(
                            "[avatar-record]" + marker + " update handle=" + AvatarRecordLogic.Quote(handle) +
                            plan + " (nothing written; landed values may be sanitized server-side and are " +
                            "only knowable after a real run) => PASS");
                        return;
                    }

                    carriedRec = record;
                    if (!writesMetadata) { kickImage(); return; }

                    string setWhy = null;
                    if (newName != null && !VrcApiReflect.TrySet(record, "Name", newName, out setWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, setWhy)); return; }
                    if (newDescription != null &&
                        !VrcApiReflect.TrySet(record, "Description", newDescription, out setWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, setWhy)); return; }
                    if (newTags != null && !VrcApiReflect.TrySetTags(record, newTags, out setWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, setWhy)); return; }

                    AvatarRecordDriver.MarkUpdateSent();
                    if (!VrcApiReflect.TryUpdateAvatarInfo(id, record, out updateTask, out var upWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, upWhy)); return; }

                    stage = 1;
                    return;
                }

                if (stage == 1)
                {
                    if (!VrcApiReflect.IsCompleted(updateTask)) return;
                    if (VrcApiReflect.IsFaulted(updateTask))
                    {
                        AvatarRecordDriver.Finish(
                            AvatarRecordDriver.Refuse(Door, VrcApiReflect.DescribeFault(updateTask)));
                        return;
                    }
                    // The UPDATE RESPONSE already carries the server-sanitized values; reporting the SUBMITTED
                    // values here instead would misreport every field the server rewrites.
                    carriedRec = VrcApiReflect.Result(updateTask);
                    var landings = new StringBuilder();
                    if (newName != null)
                        landings.Append(" | ").Append(AvatarRecordLogic.DescribeLanding(
                            "name", newName, VrcApiReflect.GetName(carriedRec)));
                    if (newDescription != null)
                        landings.Append(" | ").Append(AvatarRecordLogic.DescribeLanding(
                            "description", newDescription, VrcApiReflect.GetString(carriedRec, "Description")));
                    if (newTags != null)
                        landings.Append(" | tags landed=")
                                .Append(AvatarRecordLogic.FormatTags(VrcApiReflect.GetTags(carriedRec)));
                    metaLandings = landings.ToString();

                    // Metadata first, image second: VRCAvatarChanges carries no imageUrl field, so a metadata
                    // write cannot clobber an image, and the image call gets the PUT's own response as the
                    // fresh record its file-id derivation needs.
                    if (newImagePath == null)
                    {
                        AvatarRecordDriver.Finish(
                            "[avatar-record] update handle=" + AvatarRecordLogic.Quote(handle) + " " +
                            VrcApiReflect.Digest(carriedRec) + metaLandings + " => PASS");
                        return;
                    }
                    kickImage();
                    return;
                }

                if (!VrcApiReflect.IsCompleted(imageTask)) return;

                string imageRow;
                string verdict;
                var reportRec = carriedRec;
                if (VrcApiReflect.IsFaulted(imageTask))
                {
                    if (AvatarRecordLogic.IsAlreadyUploaded(VrcApiReflect.DeepestMessage(imageTask)))
                    {
                        imageRow = AvatarRecordLogic.ImageAlreadyUploadedRow();
                        verdict = "PASS";
                    }
                    else
                    {
                        imageRow = "image FAILED: " + VrcApiReflect.DescribeFault(imageTask, forImage: true);
                        verdict = "FAIL";
                    }
                }
                else
                {
                    reportRec = VrcApiReflect.Result(imageTask);
                    bool moved = !string.Equals(beforeImageUrl, VrcApiReflect.GetImageUrl(reportRec),
                                                StringComparison.Ordinal);
                    imageRow = AvatarRecordLogic.DescribeImageLanding(moved);
                    verdict = moved ? "PASS" : "FAIL";
                }

                AvatarRecordDriver.Finish(
                    "[avatar-record] update handle=" + AvatarRecordLogic.Quote(handle) + " " +
                    VrcApiReflect.Digest(reportRec) + metaLandings + " | " + imageRow + " => " + verdict);
            });

            return "[avatar-record]" + marker + " updating " + AvatarRecordLogic.Quote(handle) +
                   "; poll UpdateAvatarRecord.Status()";
        }

        public static string Status() => AvatarRecordDriver.Status(Door);
    }

    /// <summary>Ask VRChat to generate an impostor for each of a batch of uploaded avatars. Contract:
    /// <c>docs/unity-tools.md</c> §Publish.
    ///
    /// A rate limit stops the batch and names every avatar left unsent. <c>whatIf</c> resolves every handle
    /// to its local blueprint id, sends nothing and answers synchronously; otherwise poll
    /// <c>Status()</c>.</summary>
    [AgentTool]
    public static class RequestImpostor
    {
        internal const string Door = "RequestImpostor";

        public static string Run(GameObject[] avatars, bool whatIf = false)
        {
            AvatarRecordDriver.ClearIfOwnedBy(Door);
            var refuse = AvatarRecordDriver.PreflightRefusal();
            if (refuse != null) return AvatarRecordDriver.Refuse(Door, refuse);
            if (avatars == null || avatars.Length == 0)
                return AvatarRecordDriver.Refuse(Door, "no avatars given — pass the GameObjects that were uploaded");

            int n = avatars.Length;
            var ids = new string[n];
            var handles = new string[n];
            var unresolved = new System.Collections.Generic.List<string>();
            for (int k = 0; k < n; k++)
            {
                handles[k] = avatars[k] != null ? avatars[k].name : "<null>";
                if (!AvatarRecordDriver.TryResolveId(avatars[k], out ids[k], out var why)) unresolved.Add(why);
            }
            if (unresolved.Count > 0)
                return AvatarRecordDriver.Refuse(Door, string.Join("; ", unresolved));

            if (whatIf)
            {
                var plan = new StringBuilder("[avatar-record] (whatIf) impostor all: would request=").Append(n);
                foreach (var h in handles) plan.Append(" | handle=").Append(AvatarRecordLogic.Quote(h));
                return plan.Append(" (nothing sent; whether the server accepts a request is only knowable " +
                                   "from a real run) => PASS").ToString();
            }

            var rows = new System.Collections.Generic.List<string>();
            int queued = 0, failed = 0, i = 0;
            object task = null;

            Action finish = () =>
            {
                for (int k = i; k < n; k++)
                    rows.Add("handle=" + AvatarRecordLogic.Quote(handles[k]) + " not-attempted");
                AvatarRecordDriver.Finish(UploadAvatarLogic.RedactIds(
                    AvatarRecordLogic.ImpostorSummary(rows.ToArray(), queued, failed, n - queued - failed)));
            };

            AvatarRecordDriver.Start(Door, string.Join(", ", handles), () =>
            {
                if (task == null)
                {
                    if (i == 0) AvatarRecordDriver.MarkEnqueueSent();
                    if (!VrcApiReflect.TryEnqueueImpostor(ids[i], out task, out var kickWhy))
                    {
                        // A kick failure is SDK drift, identical for every avatar — so it ends the batch.
                        rows.Add("handle=" + AvatarRecordLogic.Quote(handles[i]) + " failed: " + kickWhy);
                        failed++; i++;
                        finish();
                    }
                    return;
                }
                if (!VrcApiReflect.IsCompleted(task)) return;
                AvatarRecordDriver.NoteProgress(Door);

                bool stop = false;
                if (VrcApiReflect.IsFaulted(task))
                {
                    failed++;
                    stop = AvatarRecordLogic.StopsImpostorBatch(VrcApiReflect.FaultStatus(task));
                    rows.Add("handle=" + AvatarRecordLogic.Quote(handles[i]) + " failed: " +
                             VrcApiReflect.DescribeFault(task) + (stop ? " (batch stopped)" : ""));
                }
                else
                {
                    queued++;
                    rows.Add("handle=" + AvatarRecordLogic.Quote(handles[i]) + " state=" +
                             AvatarRecordLogic.Quote(VrcApiReflect.JobState(VrcApiReflect.Result(task))));
                }
                task = null; i++;
                if (stop || i == n) finish();
            });
            return "[avatar-record] requesting impostors for " + n + " avatar(s); poll RequestImpostor.Status()";
        }

        public static string Status() => AvatarRecordDriver.Status(Door);
    }
}
