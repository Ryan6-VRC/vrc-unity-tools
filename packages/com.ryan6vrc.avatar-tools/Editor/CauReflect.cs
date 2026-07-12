using System;

namespace Ryan6Vrc.AvatarTools.Editor
{
    /// <summary>All CAU + SDK-builder reflection lives here so UploadAvatar never hard-references CAU
    /// (CAU's Uploader is internal + its asmdef is autoReferenced:false → no compile-time reference is
    /// possible, which also keeps CAU optional). Resolution is by qualified name across loaded assemblies;
    /// a missing type leaves a null handle → the capability gate REFUSEs, never a class-load throw.</summary>
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
    }
}
