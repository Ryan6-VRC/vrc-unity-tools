using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>All CAU + SDK-builder reflection lives here so UploadAvatar never hard-references CAU
    /// (CAU's Uploader is internal + its asmdef is autoReferenced:false → no compile-time reference is
    /// possible, which also keeps CAU optional). Resolution is by qualified name across loaded assemblies;
    /// a missing type leaves a null handle → the capability gate REFUSEs, never a class-load throw.
    ///
    /// Every lookup here is guarded: a missing type/field/method/ctor yields a named failReason and a false
    /// return — never a throw. The live UploadSingle/TryGetBuilder invocations cannot be verified headless
    /// (CAU absent, SDK panel not driveable); only the absent-graceful path is testable now.</summary>
    internal static class CauReflect
    {
        internal const string UploaderTypeName =
            "Anatawa12.ContinuousAvatarUploader.Editor.Uploader, com.anatawa12.continuous-avatar-uploader.editor";
        internal const string SettingTypeName =
            "Anatawa12.ContinuousAvatarUploader.Editor.AvatarUploadSetting, com.anatawa12.continuous-avatar-uploader.editor";

        internal static Type Uploader => Type.GetType(UploaderTypeName, throwOnError: false);
        internal static Type Setting  => Type.GetType(SettingTypeName,  throwOnError: false);

        /// <summary>CAU present iff both load-bearing types resolve.</summary>
        internal static bool IsAvailable => Uploader != null && Setting != null;

        /// <summary>Find a loaded type by its simple <c>Type.Name</c> across every loaded assembly
        /// (namespace-robust — mirrors TransplantCore.ResolveTypes' assembly scan). Returns the first
        /// match or null. A partially-loaded assembly (ReflectionTypeLoadException) is skipped, not fatal.</summary>
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

        /// <summary>Construct a platform-enabled <c>AvatarUploadSetting</c> pointing at one avatar.
        /// The <c>windows.enabled</c> flag MUST be forced true — CAU's UploadSingle silently no-ops when the
        /// current platform is disabled, and <c>enabled</c> is only set by AvatarUploadSetting.Reset(),
        /// which does not run under CreateInstance. Returns false + a named failReason on any missing member
        /// or a MaySceneReference ctor throw (an in-scene descriptor with no saved scene). Never throws.</summary>
        internal static bool TryBuildSetting(VRCAvatarDescriptor desc, out object setting, out string failReason)
        {
            setting = null;
            failReason = null;

            if (desc == null) { failReason = "null descriptor"; return false; }
            var settingType = Setting;
            if (settingType == null) { failReason = "CAU not installed"; return false; }

            var instance = ScriptableObject.CreateInstance(settingType);
            if (instance == null) { failReason = "AvatarUploadSetting instance not created"; return false; }

            // avatarName
            var nameField = settingType.GetField("avatarName");
            if (nameField == null) { failReason = "avatarName field not found"; return false; }
            nameField.SetValue(instance, desc.gameObject.name);

            // avatarDescriptor: MaySceneReference(UnityEngine.Object). Type comes off the field itself.
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

            // windows.enabled = true (field initializer already built the PlatformSpecificInfo instance).
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

        /// <summary>Acquire the SDK avatar builder via <c>VRCSdkControlPanel.TryGetBuilder&lt;IVRCSdkAvatarBuilderApi&gt;</c>.
        /// Both the control-panel type and the builder interface are resolved by simple name across loaded
        /// assemblies (namespace not hardcoded). Returns false + a named reason when the SDK types are
        /// unresolved or the panel has no builder (panel closed / not logged in). Never throws.</summary>
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
        /// await the returned Task. Does NOT swallow: a faulted upload throws the REAL SDK exception (so the
        /// tool layer can classify 429/validation/etc. and redact) rather than collapsing to a bool — a
        /// swallowed exception would erase the whole failure taxonomy. A missing/unresolved member throws
        /// InvalidOperationException (CAU drift). LIVE binding: not verifiable headless, deferred to Task 8.</summary>
        internal static async Task UploadOne(object setting, object builder, CancellationToken ct)
        {
            var m = Uploader?.GetMethod("UploadSingle", BindingFlags.Public | BindingFlags.Static);
            if (m == null) throw new InvalidOperationException("CAU UploadSingle not resolved (CAU drift)");

            object taskObj;
            // Unwrap the reflection wrapper so the real SDK exception (not TargetInvocationException) surfaces.
            try { taskObj = m.Invoke(null, new object[] { setting, builder, 0, ct }); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }

            await (Task)taskObj; // a faulted upload throws the real exception here
        }
    }
}
