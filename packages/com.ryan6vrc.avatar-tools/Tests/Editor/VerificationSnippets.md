# RenderThumbnail — live verification snippets

`RenderThumbnail.Render` bakes an avatar (the VRC SDK preprocess chain, `OnPreprocessAvatar`), samples
a humanoid clip, applies an expression, and
renders through an off-screen camera — all of which **mutate live `UnityEngine.Object`s**, which
SIGSEGV-crash this project's headless NUnit suite (`vrc-unity-tools-editmode-batchmode`). So the
render/bake/teardown path is verified **live** via MCP `execute_code`, not in NUnit; the EditMode tests
(`RenderThumbnailTests.cs`) cover only the pure helpers. These are the scripts the coordinator ran
against the live **Plum-Remy-3.0** editor on the real MA/VRCFury-composed `Shinano_kisekae` (18 meshes,
`MergeArmature`). Re-run them after any change to the render pipeline. Pin the MCP session to the target
Editor first (`set_active_instance`), and run with `safety_checks:false` (the pipeline calls
`AssetDatabase.DeleteAsset` during cleanup).

## 1. Pristine-after-run invariant (the non-destructiveness gate)

The rendered PNG proves the *image* is right; it proves nothing about the project being left untouched —
a clobbered bake, orphan clone, dirtied scene, stuck AnimationMode, or clobbered Selection would all pass
an eyeball. This snapshots the editor state before/after a normal render and asserts equality.

```csharp
var t = System.Type.GetType("Ryan6Vrc.AvatarTools.Editor.RenderThumbnail, Ryan6VRC.AvatarTools.Editor");
var m = t.GetMethod("Render", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
bool dirtyBefore = scene.isDirty;
int rootsBefore = scene.GetRootGameObjects().Length;
int selBefore = UnityEditor.Selection.objects.Length;

string verdict = (string)m.Invoke(null, new object[] { "Shinano_kisekae", null, null, "bust", null, false });

return verdict
    + "\nanimMode=" + UnityEditor.AnimationMode.InAnimationMode()   // expect False
    + " roots " + rootsBefore + "->" + scene.GetRootGameObjects().Length   // expect equal (no orphan *__rt_*(Clone))
    + " dirty " + dirtyBefore + "->" + scene.isDirty                // expect unchanged
    + " sel " + selBefore + "->" + UnityEditor.Selection.objects.Length   // expect equal
    ;   // generated assets are the SDK hooks' own business — OnPostprocessAvatar fires their cleanup
```

Observed pass: `... silhouette≈12-14% => OK | png=<temp>/...png` with `animMode=False roots 6->6
dirty True->True sel 0->0`. (`dirty True->True` = the scene was already dirty from
operator work and was left exactly that way — the `ClearSceneDirtiness` restore only fires for a scene
that was *clean* at snapshot.)

## 2. Fail-loud, no-bake on a bad input (preflight)

A non-humanoid clip must fail **named**, before any bake:

```csharp
string bad = (string)m.Invoke(null, new object[] { "Shinano_kisekae", "<path-to-a-non-isHumanMotion-clip>.anim", null, "bust", null, false });
return bad;
```

Observed pass: `... => FAIL: clip '<path>' is not a humanoid muscle clip (isHumanMotion=false)`.
Same shape for an unparseable `bg` or an unknown pose name (error enumerates the bundled vocabulary).

## 3. Teardown on a post-bake exception

The teardown runs inside a `finally`, so C# guarantees it fires when an exception propagates after the
bake. This is confirmed indirectly by §1: a normal posed run exercises every teardown line (AnimationMode
started then `InAnimationMode()==False` after; the unique `ZZZ_GeneratedAssets/<name>__rt_<guid>(Clone)`
subfolder created then deleted; no orphan root). A dedicated forced-throw harness would require a test
hook that does not belong in production code; the `finally` guarantee plus the §1 normal-path teardown
proof cover the exception path. If a hard forced-throw check is wanted later, inject it via a temporary
build of the tool, not a shipped seam.

## 4. Expression composited onto the pose

A gesture slot resolves against the BAKED controller, so its bindings match the baked meshes. The body
must be unchanged from the same pose rendered without an expression — the pose is what a second
`SampleAnimationClip` would have disturbed.

```csharp
string a = (string)m.Invoke(null, new object[] { "<avatar>", "<pose-token>", null,   "bust", null, false });
string b = (string)m.Invoke(null, new object[] { "<avatar>", "<pose-token>", "Open", "bust", null, false });
return a + "
" + b;
```

Expect `shapes=<n>/<n>` (all landed) on the second, and matching `silhouette=` on both. Open the two
PNGs: identical body, different face. A slot measurably beats the pre-bake asset path here — on one
avatar, `42/42` against `42/86`, because the bake rewrites the shape names the source asset binds.
