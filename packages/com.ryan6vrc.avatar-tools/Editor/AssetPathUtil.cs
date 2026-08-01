using System;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    // Project-path arithmetic: turning a filesystem path into one the AssetDatabase answers to.
    internal static class AssetPathUtil
    {
        // Resolve a picker-returned absolute path to a project-relative "Assets/..." path, used by the
        // Compile and Decompile menu entries. Returns null when the path is outside this project's Assets/.
        // The `abs == dataPath` case yields "Assets" — reachable only from a folder picker (Compile's
        // output-folder case); a file picker never lands exactly on dataPath, so the branch is
        // dead-but-harmless there.
        internal static string ToProjectRelative(string abs)
        {
            abs = abs.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (abs == data) return "Assets";
            return abs.StartsWith(data + "/", StringComparison.Ordinal) ? "Assets" + abs.Substring(data.Length) : null;
        }

        // The logical asset path for ANY spelling of a path the AssetDatabase can reach — already-logical,
        // absolute, or cwd-relative — or null when it reaches nothing. Wider than ToProjectRelative in the
        // one way that matters: it maps a PACKAGE's physical location back to its logical `Packages/<name>/…`.
        //
        // Both halves are load-bearing and neither is guessable from a string. A caller handed an absolute
        // path (every interactive door is — the menu door calls Path.GetFullPath, and OpenFilePanel returns
        // absolute) would fail a naive `StartsWith("Assets/")` test on an ordinary in-project document. And
        // a package's bytes live wherever it was mounted from, so its physical path is normally OUTSIDE the
        // project directory entirely — the reverse mapping, resolvedPath → assetPath, is the only way back.
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
