using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AvatarTools.Editor;

public class UploadAvatarLoopTests
{
    [Test]
    public void Run_RefusesWhenCauAbsent()
    {
        var go = new GameObject("dummy-avatar");
        try
        {
            string result = UploadAvatar.Run(new[] { go }, whatIf: true);
            StringAssert.Contains("REFUSE", result);
            StringAssert.Contains("continuous-avatar-uploader", result);
        }
        finally { Object.DestroyImmediate(go); }
    }
}
