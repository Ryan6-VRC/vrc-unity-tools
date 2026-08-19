using System;
using UnityEditor;
using UnityEngine;

namespace Ryan6Vrc.AgentTools.Editor
{
    /// <summary>
    /// Pre-arms vendor tools' once-per-session update checks so their modal "update available"
    /// dialogs never open. A modal raised at editor startup blocks the main thread before any MCP
    /// bridge exists, so from outside the editor reads as hung with nothing to call — the startup
    /// sibling of unity.md's build-modal family, out of reach of a caller-driven
    /// tools/unity-dialog.ps1. Suppression beats dismissal: setting the flag a vendor's check
    /// treats as "already checked this session" skips its network fetch and dialog entirely.
    /// SessionState persists across domain reloads, so the first load of the session settles it;
    /// [InitializeOnLoad] static ctors run during domain initialization, before the saved layout
    /// re-enables the vendor's window at cold startup — which is the path that fires the dialog
    /// unattended. Not [AgentTool]-marked — no callable surface, no TOOLS.md row.
    /// </summary>
    [InitializeOnLoad]
    public static class VendorDialogGuard
    {
        // Trivial ctor discipline (PlayViewFocus/PlayGate): a throw here silently disables the
        // guard for the whole domain, so each entry is individually fenced.
        static VendorDialogGuard()
        {
            try
            {
                // MochiFitter (OutfitRetargetingSystem.dll): its window's OnEnable schedules a
                // version fetch + modal unless this session flag already reads true. Harmless when
                // the tool is absent; a manual re-check remains possible by clearing the flag.
                SessionState.SetBool("MochiFitter_VersionChecked", true);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VendorDialogGuard] could not pre-arm a vendor session flag (harmless): " + e.Message);
            }
        }
    }
}
