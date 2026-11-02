using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>All CAU and SDK-builder reflection, so UploadAvatar never hard-references CAU: its Uploader
    /// is internal behind an autoReferenced:false asmdef, and staying off it keeps CAU optional. A missing
    /// type leaves a null handle and the capability gate REFUSEs, never a class-load throw.
    ///
    /// A missing type, field, method or ctor yields a named failReason, never a throw; only
    /// <see cref="UploadOne"/> throws, so the tool layer can classify the failure.</summary>
    internal static class CauReflect
    {
        internal const string UploaderTypeName =
            "Anatawa12.ContinuousAvatarUploader.Editor.Uploader, com.anatawa12.continuous-avatar-uploader.editor";
        internal const string SettingTypeName =
            "Anatawa12.ContinuousAvatarUploader.Editor.AvatarUploadSetting, com.anatawa12.continuous-avatar-uploader.editor";

        internal static Type Uploader => Type.GetType(UploaderTypeName, throwOnError: false);
        internal static Type Setting  => Type.GetType(SettingTypeName,  throwOnError: false);

        internal static bool IsAvailable => Uploader != null && Setting != null;

        private static Type FindLoadedType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                if (types == null) continue;
                foreach (var t in types)
                    if (t != null && t.Name == simpleName) return t;
            }
            return null;
        }

        /// <summary>Kick CAU's <c>Uploader.TryLogin()</c>, which restores saved credentials, and hand back
        /// the running Task unawaited: its fetch callback requires the editor update loop, so blocking here
        /// would hang the editor (<see cref="UploadAvatarLogic.LoginLatch"/> carries the measurement). A
        /// missing member returns a null task plus a named reason, which leaves the caller's latch
        /// untouched.</summary>
        internal static (Task<bool> task, string failReason) TryLoginKick()
        {
            var m = Uploader?.GetMethod("TryLogin", BindingFlags.Public | BindingFlags.Static);
            if (m == null) return (null, "CAU TryLogin not resolved (CAU absent, or CAU drift)");
            object raw;
            try { raw = m.Invoke(null, null); }
            catch (TargetInvocationException tie) { return (null, (tie.InnerException ?? tie).Message); }
            catch (Exception e) { return (null, e.Message); }
            if (raw is Task<bool> t) return (t, null);
            return (null, "CAU TryLogin returned " + (raw?.GetType().Name ?? "null") +
                          ", not Task<bool> (CAU drift) — the restore may have run unobserved");
        }

        /// <summary>The door's login self-heal. Editor-lifetime static and NOT persisted: a domain reload
        /// drops it and resets <c>APIUser.IsLoggedIn</c> together, and a persisted in-flight state would
        /// have no owning Task in the new domain.</summary>
        internal static readonly UploadAvatarLogic.LoginLatch LoginLatch =
            new UploadAvatarLogic.LoginLatch(TryLoginKick,
                                             () => APIUser.IsLoggedIn,
                                             () => EditorApplication.timeSinceStartup);

        /// <summary>Construct a platform-enabled <c>AvatarUploadSetting</c> pointing at one avatar.
        /// <c>windows.enabled</c> MUST be forced true: CAU's UploadSingle returns without uploading when
        /// the current platform is disabled, and <c>enabled</c> is only set by AvatarUploadSetting.Reset(),
        /// which does not run under CreateInstance.</summary>
        internal static bool TryBuildSetting(VRCAvatarDescriptor desc, out object setting, out string failReason)
        {
            setting = null;
            failReason = null;

            if (desc == null) { failReason = "null descriptor"; return false; }
            var settingType = Setting;
            if (settingType == null) { failReason = "CAU not installed"; return false; }

            var instance = ScriptableObject.CreateInstance(settingType);
            if (instance == null) { failReason = "AvatarUploadSetting instance not created"; return false; }

            var nameField = settingType.GetField("avatarName");
            if (nameField == null) { failReason = "avatarName field not found"; return false; }
            nameField.SetValue(instance, desc.gameObject.name);

            var descField = settingType.GetField("avatarDescriptor");
            if (descField == null) { failReason = "avatarDescriptor field not found"; return false; }
            var mayType = descField.FieldType;
            var mayCtor = mayType.GetConstructor(new[] { typeof(UnityEngine.Object) });
            if (mayCtor == null) { failReason = "MaySceneReference(Object) ctor not found"; return false; }
            object mayRef;
            try { mayRef = mayCtor.Invoke(new object[] { desc }); }
            catch (Exception e)
            {
                // In-scene descriptor without a saved scene → ctor throws; surface the reason, do not upload.
                failReason = "cannot reference avatar (save the scene, or use a prefab asset): " +
                             (e.InnerException ?? e).Message;
                return false;
            }
            descField.SetValue(instance, mayRef);

            var windowsField = settingType.GetField("windows");
            if (windowsField == null) { failReason = "windows field not found"; return false; }
            var windowsVal = windowsField.GetValue(instance);
            if (windowsVal == null) { failReason = "windows PlatformSpecificInfo not initialized"; return false; }
            var enabledField = windowsVal.GetType().GetField("enabled");
            if (enabledField == null) { failReason = "PlatformSpecificInfo.enabled field not found"; return false; }
            enabledField.SetValue(windowsVal, true);

            setting = instance;
            return true;
        }

        /// <summary>Acquire the SDK avatar builder. False plus a named reason when the SDK types are
        /// unresolved or the panel has no builder (panel closed or not logged in).</summary>
        internal static bool TryGetBuilder(out object builder, out string failReason)
        {
            builder = null;
            failReason = null;

            var panelType = FindLoadedType("VRCSdkControlPanel");
            var builderIface = FindLoadedType("IVRCSdkAvatarBuilderApi");
            if (panelType == null || builderIface == null)
            {
                failReason = "SDK builder API not found — open the Build Control Panel";
                return false;
            }

            var tryGet = panelType.GetMethod("TryGetBuilder", BindingFlags.Public | BindingFlags.Static);
            if (tryGet == null || !tryGet.IsGenericMethodDefinition)
            {
                failReason = "VRCSdkControlPanel.TryGetBuilder<T> not found";
                return false;
            }

            try
            {
                var generic = tryGet.MakeGenericMethod(builderIface);
                var args = new object[] { null };
                var got = (bool)generic.Invoke(null, args);
                if (!got)
                {
                    failReason = "no avatar builder — is the SDK panel open?";
                    return false;
                }
                builder = args[0];
                return true;
            }
            catch (Exception e)
            {
                failReason = "TryGetBuilder invocation failed: " + (e.InnerException ?? e).Message;
                return false;
            }
        }

        /// <summary>Invoke CAU's <c>Uploader.UploadSingle(setting, builder, uploadRetryCount:0, ct)</c> and
        /// await it. A faulted upload throws the REAL SDK exception so the tool layer can classify and
        /// redact it; a bool would erase the failure taxonomy.</summary>
        internal static async Task UploadOne(object setting, object builder, CancellationToken ct)
        {
            var m = Uploader?.GetMethod("UploadSingle", BindingFlags.Public | BindingFlags.Static);
            if (m == null) throw new InvalidOperationException("CAU UploadSingle not resolved (CAU drift)");

            object taskObj;
            try { taskObj = m.Invoke(null, new object[] { setting, builder, 0, ct }); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }

            await (Task)taskObj;
        }
    }
}
