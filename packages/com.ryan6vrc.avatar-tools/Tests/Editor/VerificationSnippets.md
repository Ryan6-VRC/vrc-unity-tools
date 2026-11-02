# RenderThumbnail — live verification snippets

`RenderThumbnail.Run`'s bake, pose sample, expression and off-screen render mutate live `UnityEngine.Object`s, which NUnit EditMode tests must not do (`docs/verify.md` §Test venue), so that path is verified live over `execute_code` on a real MA/VRCFury-composed avatar; `RenderThumbnailTests.cs` covers only the pure helpers. Re-run these after any change to the render pipeline. The snippets after §1 need its first two lines, which bind `m`. `Invoke` fills no optional arguments, so every call passes each parameter of `Run` (`RenderThumbnail.cs`).

## 1. Pristine-after-run invariant (the non-destructiveness gate)

The PNG proves nothing about the project being left untouched: a clobbered bake, orphan clone, dirtied scene, stuck AnimationMode or clobbered Selection would all pass an eyeball. This compares editor state before and after a normal render.

```csharp
var t = System.Type.GetType("Ryan6Vrc.AvatarTools.Editor.RenderThumbnail, Ryan6VRC.AvatarTools.Editor");
var m = t.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
bool dirtyBefore = scene.isDirty;
int rootsBefore = scene.GetRootGameObjects().Length;
int selBefore = UnityEditor.Selection.objects.Length;

string verdict = (string)m.Invoke(null, new object[] { "<avatar>", null, null, "bust", null, 30f, null, false, 1f, 0f, 0f });

return verdict
    + "\nanimMode=" + UnityEditor.AnimationMode.InAnimationMode()   // expect False
    + " roots " + rootsBefore + "->" + scene.GetRootGameObjects().Length   // expect equal (no orphan *__rt_* root)
    + " dirty " + dirtyBefore + "->" + scene.isDirty                // expect unchanged
    + " sel " + selBefore + "->" + UnityEditor.Selection.objects.Length   // expect equal
    ;   // generated assets are the SDK hooks' own business — OnPostprocessAvatar fires their cleanup
```

The verdict ends `=> OK` and names the PNG. `dirty True->True` is a pass: the `ClearSceneDirtiness` restore fires only for a scene that was clean at snapshot.

## 2. Fail-loud, no-bake on a bad input (preflight)

A non-humanoid clip must fail **named**, before any bake:

```csharp
string bad = (string)m.Invoke(null, new object[] { "<avatar>", "<path-to-a-non-isHumanMotion-clip>.anim", null, "bust", null, 30f, null, false, 1f, 0f, 0f });
return bad;
```

Expect `=> FAIL: clip '<path>' is not a humanoid muscle clip (isHumanMotion=false)`. An unparseable `bg` or an unknown pose name fails the same way, the pose error listing the bundled vocabulary.

## 3. Teardown on a post-bake exception

§1 evidences the observable half of teardown. `OnPostprocessAvatar` produces no signal a snippet can read, which is why it sits in an inner `finally` no earlier teardown step can skip. The reachable abnormal path is an interrupted bake: a render wedged on a VRCFury modal dialog (`docs/unity.md` §Sharp edges) and then dismissed must still end with `animMode=False` and no `*__rt_*` root. Run §1's assertions after any such interruption.

## 4. Expression composited onto the pose

A gesture slot resolves against the BAKED controller, so its bindings match the baked meshes. The body must be unchanged from the same pose rendered without an expression.

```csharp
string a = (string)m.Invoke(null, new object[] { "<avatar>", "<pose-token>", null,   "bust", null, 30f, null, false, 1f, 0f, 0f });
string b = (string)m.Invoke(null, new object[] { "<avatar>", "<pose-token>", "Open", "bust", null, 30f, null, false, 1f, 0f, 0f });
return a + "\n" + b;
```

Expect the second verdict to carry `expression=Open (<clip>)`, the clip that state name resolved to on the baked controller, and matching `camYaw=`/`head=` on both, since an expression moves blendshapes only and must not disturb the camera solve. **The PNGs are the assertion**: identical body, different face. A face that did not change while the verdict still names a clip is exactly the failure this case exists to catch.

A state name beats a pre-bake clip path because the bake rewrites the blendshape names a source asset binds; a path that no longer matches fails loud (`moved no blendshape`) instead of rendering a blank face.
