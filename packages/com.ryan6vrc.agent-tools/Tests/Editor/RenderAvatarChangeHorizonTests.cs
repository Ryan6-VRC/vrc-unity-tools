using System;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// The change-horizon sweep closes the settle gate's one blind spot: a scripted edit whose only
// published event is ChangeScene (ignored by NDMF's ChangeStream) stays invisible to the settle
// predicate until PropertyMonitor runs — and that parks while the editor is unfocused. The sweep
// reaches five NDMF internals by reflection; if any renames, the handles silently null and the
// blind spot reopens with only an in-band note. These tests are the drift canary: package present
// + handle unresolved must FAIL the suite, never skip (the versionDefines/reflection-canary rule —
// a skip is exactly when production goes blind).
public class RenderAvatarChangeHorizonTests
{
    private static bool NdmfInstalled()
    {
        // Package-presence signal independent of the type-resolution path under test: the assembly
        // by name. A TYPE rename with the assembly still present must land in the Fail branch below.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { if (asm.GetName().Name == "nadena.dev.ndmf") return true; }
            catch { /* dynamic assembly — skip */ }
        }
        return false;
    }

    [Test]
    public void ChangeHorizonHandles_ResolveAgainstInstalledNdmf()
    {
        if (!NdmfInstalled())
            Assert.Ignore("nadena.dev.ndmf not installed in this venue — canary has nothing to check");

        Assert.IsTrue(RenderAvatar.ChangeHorizonHandlesResolved,
            "NDMF is installed but a change-horizon handle failed to resolve (ObjectWatcher/PropertyMonitor/" +
            "NDMFSyncContext/ComputeContext member renamed?) — the settle gate's scripted-edit blind spot " +
            "is silently open again; re-pin the reflection handles in RenderAvatar.");
    }

    [Test]
    public void Sweep_NeverThrows_AndIsBounded()
    {
        // Headless there is no rendered preview pipeline, so the sweep must degrade to a clean
        // no-op/note — and return within its budget (bounded loop, not a wait-for-settle).
        var go = new GameObject("HorizonSweepProbe");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string note = RenderAvatar.SweepNdmfChangeHorizon(go);
            sw.Stop();
            Assert.IsNotNull(note);
            if (note.Length > 0)
                StringAssert.Contains("change-horizon sweep unavailable", note); // only the drift note is legal
            Assert.Less(sw.ElapsedMilliseconds, 2000,
                "sweep exceeded its bound — the pump loop must be time-capped, not settle-blocking");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
