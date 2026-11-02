using System;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Pre-arms vendor tools' once-per-session update checks, because a modal raised at editor
    /// startup blocks the main thread before any MCP bridge exists. Each check reads a session flag
    /// as "already checked", and an [InitializeOnLoad] static ctor sets it before the saved layout
    /// re-enables the vendor's window at cold startup.
    /// </summary>
    [InitializeOnLoad]
    public static class VendorDialogGuard
    {
        // A throw from this ctor disables the guard for the whole domain, so every entry stays
        // inside a try.
        static VendorDialogGuard()
        {
            try
            {
                // MochiFitter (OutfitRetargetingSystem.dll): its window's OnEnable schedules a
                // version fetch + modal unless this session flag already reads true.
                SessionState.SetBool("MochiFitter_VersionChecked", true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VendorDialogGuard] could not pre-arm a vendor session flag (harmless): " + e.Message);
            }
        }
    }
}
