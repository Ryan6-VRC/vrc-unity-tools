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
    /// reflection for optionality and version-drift tolerance, not to reach anything private. Behaviour is
    /// asserted from black-box measurement against the live API; the contract lives in
    /// <c>docs/unity-tools.md</c> §Publish.</summary>
    internal static class VrcApiReflect
    {
        internal const string ApiTypeName = "VRC.SDKBase.Editor.Api.VRCApi, VRC.SDKBase.Editor";

        internal static Type Api => Type.GetType(ApiTypeName, throwOnError: false);

        internal static bool IsAvailable => Api != null;

        /// <summary>Resolve a static method by name. <c>GetMethod(name, flags)</c> THROWS
        /// AmbiguousMatchException the moment the SDK adds an overload, so the lookup itself is guarded —
        /// this file's whole contract is that drift produces a named refusal, and a lookup that can throw
        /// would break it precisely when the SDK changes.</summary>
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

        /// <summary>Invoke a resolved static and confirm the result is actually a Task. Without the type
        /// check a drifted return value would sail through and NRE later inside the update loop, surfacing
        /// as an unnamed failure with no route back to the real cause.</summary>
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

        /// <summary>Kick <c>VRCApi.GetAvatar(id, forceRefresh: true, ct)</c>. Always forces a refresh: the
        /// whole point of this door is what is live NOW, and a cached record would let it report a name the
        /// server no longer has.</summary>
        internal static bool TryGetAvatar(string id, out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("GetAvatar", out var m, out failReason)) return false;
            return Invoke(m, new object[] { id, true, CancellationToken.None }, out task, out failReason);
        }

        /// <summary>Kick <c>VRCApi.UpdateAvatarInfo(id, record, ct)</c> with a whole <c>VRCAvatar</c>.
        ///
        /// Posting the entire struct back is what the SDK's own control panel and CAU both do, so this door
        /// inherits their safety envelope rather than inventing one: the SDK narrows the record to a
        /// changes payload internally, which is why no bundle or thumbnail is ever at risk here — they are
        /// not submitted at all. Fields that ARE submitted (description, tags, release status, styles) are
        /// round-tripped from the read, so a concurrent writer between the read and this call is the one
        /// case where an unrelated field can be reverted.</summary>
        internal static bool TryUpdateAvatarInfo(string id, object record, out object task, out string failReason)
        {
            task = null;
            if (!TryMethod("UpdateAvatarInfo", out var m, out failReason)) return false;
            return Invoke(m, new object[] { id, record, CancellationToken.None }, out task, out failReason);
        }

        // ── Task inspection (the returned Task<VRCAvatar> is reached reflectively) ───────────────

        internal static bool IsCompleted(object task) => (bool)task.GetType().GetProperty("IsCompleted").GetValue(task);
        internal static bool IsFaulted(object task) => (bool)task.GetType().GetProperty("IsFaulted").GetValue(task);
        internal static object Result(object task) => task.GetType().GetProperty("Result").GetValue(task);

        // ── API error classification ────────────────────────────────────────────────────────────

        /// <summary>Find the exception in a faulted task's chain that actually carries API signal.
        ///
        /// Deliberately NOT "unwrap to the innermost": the SDK wraps a deserialization failure in its own
        /// exception, so the innermost link is a Newtonsoft error and the layer holding StatusCode is gone.
        /// Same reasoning as <see cref="UploadAvatar.FailedFromException"/> — walk the chain and stop at the
        /// first link that can be classified, rather than blindly taking one end of it. AggregateException's
        /// siblings are walked too, so a multi-inner fault does not lose its signal to the first branch.</summary>
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
        /// name-equality test would drop both — including the 422 that a refused name produces, which is
        /// the single most likely failure this door will ever see. Anything exposing a readable StatusCode
        /// field is classifiable, whatever it is called.</summary>
        static bool TryReadApiFields(Exception e, out int? statusCode, out string serverMessage)
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
        internal static string DescribeFault(object task)
        {
            var ex = (Exception)task.GetType().GetProperty("Exception").GetValue(task);
            var hit = FindClassifiable(ex, out var code, out var msg);
            if (hit != null) return AvatarRecordLogic.RefuseForStatus(code, msg);

            // Nothing in the chain carried API signal — fall back to the innermost message, which at least
            // names a transport or cancellation failure. Scrubbed: an arbitrary exception string is the one
            // place an id or URL can still reach output.
            var deepest = ex;
            while (deepest != null && deepest.InnerException != null) deepest = deepest.InnerException;
            return "VRChat API call failed: " +
                   AvatarRecordLogic.Escape(UploadAvatarLogic.RedactIds(deepest?.Message ?? "no exception detail"));
        }

        // ── Record reads / writes ───────────────────────────────────────────────────────────────

        internal static string GetString(object record, string property)
            => record?.GetType().GetProperty(property)?.GetValue(record) as string;

        internal static string GetName(object record) => GetString(record, "Name");

        /// <summary>Write one property on the BOXED <c>VRCAvatar</c>. The box matters: the record is a
        /// struct, <c>SetValue</c> mutates the box, and that same box is what goes to
        /// <c>UpdateAvatarInfo</c> — unboxing to a local first would silently write to a copy and post the
        /// record back unchanged while reporting success.
        ///
        /// A property that is missing or read-only is an SDK-drift REFUSE, never a silent skip: the caller
        /// asked for a change, and a door that quietly dropped it would report PASS on an edit that never
        /// happened.</summary>
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

        /// <summary>Tags as a plain array, order preserved, for reporting and comparison.</summary>
        internal static string[] GetTags(object record)
        {
            var raw = record?.GetType().GetProperty("Tags")?.GetValue(record) as System.Collections.IEnumerable;
            if (raw == null) return null;
            var list = new System.Collections.Generic.List<string>();
            foreach (var t in raw) list.Add(t as string);
            return list.ToArray();
        }

        /// <summary>Set Tags, building the exact collection type the property declares rather than assuming
        /// it, so an SDK that changes that type produces a named refusal instead of an InvalidCastException
        /// thrown from inside the setter.</summary>
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

        /// <summary>The record's caller-facing digest.
        ///
        /// Two different rules apply, and conflating them is what made an earlier version unusable. Ids and
        /// URLs (<c>ID</c>, <c>AuthorId</c>, <c>AuthorName</c>, the image and asset URLs) are OMITTED
        /// outright — <c>AuthorName</c> included, since an account identifier is barred from output just as
        /// firmly as a blueprint id. The published TEXT fields are emitted verbatim (escaped, not scrubbed):
        /// they are already public, and they are what a caller chains into
        /// <see cref="UpdateAvatarRecord"/>'s <c>expectCurrentName</c> — scrubbing them would hand back a
        /// string the expected-name check can never match, locking such an avatar out of editing entirely.</summary>
        internal static string Digest(object record)
        {
            if (record == null) return "<null record>";
            var t = record.GetType();
            var sb = new StringBuilder();

            sb.Append("name=").Append(AvatarRecordLogic.Quote(GetName(record)));
            sb.Append(" description=").Append(AvatarRecordLogic.Quote(GetString(record, "Description")));
            // Tag VALUES, not a count: tags are writable, so the digest has to show what a caller would be
            // overwriting — a count cannot be chained back into an edit.
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

            var img = GetString(record, "ThumbnailImageUrl");
            sb.Append(" hasThumbnail=").Append(!string.IsNullOrEmpty(img));
            return sb.ToString();
        }
    }

    /// <summary>Shared async drive for the record doors.
    ///
    /// <c>VRCApi</c> returns Tasks that only progress while the editor's update loop runs, so the doors
    /// cannot block: a <c>Task.ContinueWith</c> never fires and a <c>Thread.Sleep</c> starves the very pump
    /// it is waiting on. Same Run/Status split as <see cref="UploadAvatar"/> — Run kicks and returns, the
    /// update loop advances the state machine, Status() reports.
    ///
    /// One operation at a time. The in-flight Task cannot survive a domain reload, so it is deliberately not
    /// persisted — but the FACT that an operation was interrupted is, in SessionState, because losing that
    /// is what lets a landed write be reported as "nothing has been run".</summary>
    internal static class AvatarRecordDriver
    {
        internal const int FrameBudget = 4000;
        const string BreadcrumbKey = "Ryan6Vrc.AvatarRecord.pending";
        // A GameObject name can contain anything printable, so the breadcrumb is split on a control
        // character that cannot appear in one — a comma or pipe would corrupt the handle field.
        const char Sep = '\u001f';

        static bool _running;
        static string _summary;
        static string _summaryOwner;      // which door produced _summary
        static int _frames;
        static Action _step;
        static string _door, _handle;
        static AvatarRecordLogic.Phase _phase;

        internal static bool Busy => _running;
        internal static int Frames => _frames;

        /// <summary>Convert a breadcrumb that outlived its editor session into an explicit verdict.
        ///
        /// A domain reload (a recompile, entering play mode) mid-operation destroys the Task and every
        /// static holding it. Without this, <c>Status()</c> would answer "idle — nothing has been run",
        /// which is a positive false claim about a write that may already have landed.</summary>
        [InitializeOnLoadMethod]
        static void ReconcileInterrupted()
        {
            var crumb = SessionState.GetString(BreadcrumbKey, "");
            if (string.IsNullOrEmpty(crumb)) return;
            SessionState.EraseString(BreadcrumbKey);

            var parts = crumb.Split(new[] { Sep }, 3);
            if (parts.Length < 3) return;
            var phase = parts[1] == "w" ? AvatarRecordLogic.Phase.UpdateSent : AvatarRecordLogic.Phase.Reading;
            _summaryOwner = parts[0];
            _summary = AvatarRecordLogic.InterruptedVerdict(
                parts[0], parts[2], phase, "the editor reloaded (recompile or play-mode entry)");
        }

        static void WriteBreadcrumb()
            => SessionState.SetString(BreadcrumbKey,
                   _door + Sep + (_phase == AvatarRecordLogic.Phase.UpdateSent ? "w" : "r") + Sep + _handle);

        /// <summary>Called by a door the instant its update request is handed to the API. Everything after
        /// this point must be reported UNKNOWN rather than FAIL if the operation is lost.</summary>
        internal static void MarkUpdateSent()
        {
            _phase = AvatarRecordLogic.Phase.UpdateSent;
            WriteBreadcrumb();
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
                // Stage-aware: past the send point the request reached the server, so "FAIL" would be a
                // false negative that invites a blind re-run.
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

        /// <summary>Poll, scoped to the asking door.
        ///
        /// A single shared summary crossing doors is how a caller ends up reading someone else's result:
        /// the convention is "Run, then poll Status()", so a door that refused synchronously would
        /// otherwise hand back the PREVIOUS door's terminal line with nothing marking it as foreign — and
        /// on the "already in flight" refusal, that line is precisely the other operation's outcome.</summary>
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

        /// <summary>Resolve GameObject → live blueprint id, or a named refusal. The id is returned for the
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

        /// <summary>The refusal funnel. Named per door rather than the batch grammar's "all": these doors
        /// act on ONE avatar, and a scope token copied from a batch tool would be a false claim.</summary>
        internal static string Refuse(string door, string reason)
            => "[avatar-record] " + door + " => REFUSE error=" + UploadAvatarLogic.RedactIds(reason);

        /// <summary>Guard shared by both doors: the SDK present, and no other record op in flight.</summary>
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

    /// <summary>Read one avatar's LIVE VRChat record — the published name, description, tags, release
    /// status and bundle presence as the server currently has them, not as the local scene believes.
    ///
    /// The trap that motivates the family (an upload cannot rename, and a re-upload republishes under the
    /// old name) is <c>docs/unity-tools.md</c> §Publish's to explain; what matters here is the consequence:
    /// the scene is not evidence about what is published, and this door is.
    ///
    /// Async: <c>Run</c> kicks and returns; poll <c>Status()</c>. No id, account name or URL enters output.</summary>
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

    /// <summary>Edit an already-uploaded avatar's published metadata — name, description and tags. Metadata
    /// only: no bundle, no re-upload. Contract and the reasons behind each guard: <c>docs/unity-tools.md</c>
    /// §Publish.
    ///
    /// Every field is NULL-MEANS-UNCHANGED. For tags that makes an empty array the only way to say "clear
    /// them", so an empty array is accepted and is not treated as an omission.
    ///
    /// <paramref name="expectCurrentName"/> is required: the write targets whatever blueprintId the
    /// GameObject's PipelineManager holds, so stating the name you believe is live turns a mis-wired id
    /// into a refusal instead of an edit landing on the wrong published avatar. Chain it from
    /// <see cref="ReportAvatarRecord"/>'s reported name. It is a best-effort confirmation, not an atomic
    /// compare-and-swap — see <see cref="AvatarRecordLogic.CheckExpectedName"/> for what it does not catch.
    ///
    /// <c>ReleaseStatus</c> is deliberately NOT settable here. It is the one field that can make an avatar
    /// public, and folding an irreversible visibility change into a general metadata setter is how it gets
    /// flipped by a caller who was only fixing a typo. Same reasoning leaves <c>DeleteAvatar</c> unbuilt: a
    /// door earns a destructive verb explicitly or not at all.
    ///
    /// <c>whatIf</c> reads the record and reports what WOULD change, writing nothing.</summary>
    [AgentTool]
    public static class UpdateAvatarRecord
    {
        internal const string Door = "UpdateAvatarRecord";

        public static string Run(GameObject avatar, string expectCurrentName, string newName = null,
                                 string newDescription = null, string[] newTags = null, bool whatIf = false)
        {
            AvatarRecordDriver.ClearIfOwnedBy(Door);
            var refuse = AvatarRecordDriver.PreflightRefusal();
            if (refuse != null) return AvatarRecordDriver.Refuse(Door, refuse);

            var nothing = AvatarRecordLogic.CheckSomethingToDo(newName, newDescription, newTags);
            if (nothing != null) return AvatarRecordDriver.Refuse(Door, nothing);
            if (newName != null)
            {
                var nameWhy = AvatarRecordLogic.ValidateNewName(newName);
                if (nameWhy != null) return AvatarRecordDriver.Refuse(Door, nameWhy);
            }
            var tagsWhy = AvatarRecordLogic.ValidateTags(newTags);
            if (tagsWhy != null) return AvatarRecordDriver.Refuse(Door, tagsWhy);

            if (!AvatarRecordDriver.TryResolveId(avatar, out var id, out var why))
                return AvatarRecordDriver.Refuse(Door, why);
            if (!VrcApiReflect.TryGetAvatar(id, out var getTask, out var kickWhy))
                return AvatarRecordDriver.Refuse(Door, kickWhy);

            string handle = avatar.name;
            string marker = whatIf ? " (whatIf)" : "";
            object updateTask = null;
            int stage = 0;

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
                        AvatarRecordDriver.Finish(
                            "[avatar-record]" + marker + " update handle=" + AvatarRecordLogic.Quote(handle) +
                            plan + " (nothing written; landed values may be sanitized server-side and are " +
                            "only knowable after a real run) => PASS");
                        return;
                    }

                    string setWhy = null;
                    if (newName != null && !VrcApiReflect.TrySet(record, "Name", newName, out setWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, setWhy)); return; }
                    if (newDescription != null &&
                        !VrcApiReflect.TrySet(record, "Description", newDescription, out setWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, setWhy)); return; }
                    if (newTags != null && !VrcApiReflect.TrySetTags(record, newTags, out setWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, setWhy)); return; }

                    // Breadcrumb BEFORE the call: the window this protects is the call itself, so marking
                    // it afterwards would leave the one moment that matters unmarked.
                    AvatarRecordDriver.MarkUpdateSent();
                    if (!VrcApiReflect.TryUpdateAvatarInfo(id, record, out updateTask, out var upWhy))
                    { AvatarRecordDriver.Finish(AvatarRecordDriver.Refuse(Door, upWhy)); return; }

                    stage = 1;
                    return;
                }

                if (!VrcApiReflect.IsCompleted(updateTask)) return;
                if (VrcApiReflect.IsFaulted(updateTask))
                {
                    AvatarRecordDriver.Finish(
                        AvatarRecordDriver.Refuse(Door, VrcApiReflect.DescribeFault(updateTask)));
                    return;
                }
                // The UPDATE RESPONSE already carries the server-sanitized values, so no extra GetAvatar
                // round trip is needed. Reporting the SUBMITTED values here instead would misreport every
                // field the server rewrites.
                var landedRec = VrcApiReflect.Result(updateTask);
                var landings = new StringBuilder();
                if (newName != null)
                    landings.Append(" | ").Append(AvatarRecordLogic.DescribeLanding(
                        "name", newName, VrcApiReflect.GetName(landedRec)));
                if (newDescription != null)
                    landings.Append(" | ").Append(AvatarRecordLogic.DescribeLanding(
                        "description", newDescription, VrcApiReflect.GetString(landedRec, "Description")));
                if (newTags != null)
                    landings.Append(" | tags landed=")
                            .Append(AvatarRecordLogic.FormatTags(VrcApiReflect.GetTags(landedRec)));
                AvatarRecordDriver.Finish(
                    "[avatar-record] update handle=" + AvatarRecordLogic.Quote(handle) + " " +
                    VrcApiReflect.Digest(landedRec) + landings + " => PASS");
            });

            return "[avatar-record]" + marker + " updating " + AvatarRecordLogic.Quote(handle) +
                   "; poll UpdateAvatarRecord.Status()";
        }

        public static string Status() => AvatarRecordDriver.Status(Door);
    }
}
