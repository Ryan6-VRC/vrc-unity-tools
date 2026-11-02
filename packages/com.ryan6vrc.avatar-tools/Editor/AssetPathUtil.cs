using System;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    // Project-path arithmetic: turning a filesystem path into one the AssetDatabase answers to.
    internal static class AssetPathUtil
    {
        // An absolute path as project-relative "Assets/...", or null when it is outside this project's Assets/.
        internal static string ToProjectRelative(string abs)
        {
            abs = abs.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (abs == data) return "Assets";
            return abs.StartsWith(data + "/", StringComparison.Ordinal) ? "Assets" + abs.Substring(data.Length) : null;
        }

        // The logical asset path for any spelling the AssetDatabase can reach (logical, absolute or cwd-relative), or
        // null. An absolute path, which OpenFilePanel returns, fails a naive `StartsWith("Assets/")` test even for an
        // in-project document. A package's physical path is normally outside the project directory, so
        // resolvedPath → assetPath is the only way back to its logical `Packages/<name>/…`.
        internal static string ToLogicalAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var p = path.Replace('\\', '/');
            if (p == "Assets" || p.StartsWith("Assets/", StringComparison.Ordinal)
                || p.StartsWith("Packages/", StringComparison.Ordinal)) return p;

            string full;
            try { full = System.IO.Path.GetFullPath(p).Replace('\\', '/'); }
            catch { return null; }

            var underAssets = ToProjectRelative(full);
            if (underAssets != null) return underAssets;

            foreach (var pkg in PackageInfo.GetAllRegisteredPackages())
            {
                var resolved = pkg.resolvedPath?.Replace('\\', '/');
                if (string.IsNullOrEmpty(resolved)) continue;
                if (full == resolved) return pkg.assetPath;
                if (full.StartsWith(resolved + "/", StringComparison.Ordinal))
                    return pkg.assetPath + full.Substring(resolved.Length);
            }
            return null;
        }
    }
}
