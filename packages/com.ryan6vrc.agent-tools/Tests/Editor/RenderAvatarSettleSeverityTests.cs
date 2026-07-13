using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AgentTools.Editor;

// G17: the transient settle-gate FAIL must log at Warning (an expected "re-grab" retry condition), while
// genuine failures stay at Error — so a console-clean gate isn't polluted by the re-grab prompt.
// RenderAvatar.Capture can't reach the settle gate headless (it returns "no SceneView" first), so the
// severity split is verified on the two Fail helpers directly; the call-site wiring (settle gate →
// FailTransient) is a one-line read above.
public class RenderAvatarSettleSeverityTests
{
    [Test]
    public void SettleFail_LogsWarning_NotError()
    {
        // Expect(Warning) both asserts the severity IS Warning and marks it handled. Were it an Error, the
        // Warning-expect would go unmatched AND the Error would be an unhandled-error test failure.
        LogAssert.Expect(LogType.Warning, new Regex(@"\[RenderAvatar\] Hair => FAIL: preview not settled"));
        string msg = RenderAvatar.FailTransient("Hair", "preview not settled (rebuild in flight)");
        StringAssert.StartsWith("[RenderAvatar] Hair => FAIL:", msg); // same FAIL contract string as Fail
    }

    [Test]
    public void GenuineFail_LogsError()
    {
        LogAssert.Expect(LogType.Error, new Regex(@"\[RenderAvatar\] Hair => FAIL: target not found"));
        string msg = RenderAvatar.Fail("Hair", "target not found");
        StringAssert.StartsWith("[RenderAvatar] Hair => FAIL:", msg);
    }
}
