using System;
using UnityEngine;

namespace Ryan6Vrc.AvatarTools.Editor
{
    // Resolve a picker-returned absolute path to a project-relative "Assets/..." path, shared by the
    // Compile and Decompile menu entries (the only callers). Returns null when the path is outside this
    // project's Assets/. The `abs == dataPath` case yields "Assets" — reachable only from a folder picker
    // (Compile's output-folder case); a file picker never lands exactly on dataPath, so the branch is
    // dead-but-harmless there.
    internal static class AssetPathUtil
    {
        internal static string ToProjectRelative(string abs)
        {
            abs = abs.Replace('\\', '/');
            string data = Application.dataPath.Replace('\\', '/');
            if (abs == data) return "Assets";
            return abs.StartsWith(data + "/", StringComparison.Ordinal) ? "Assets" + abs.Substring(data.Length) : null;
        }
    }
}
