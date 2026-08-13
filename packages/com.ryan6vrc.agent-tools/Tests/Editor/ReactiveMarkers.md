# `RenderAvatar.ReactiveMarkers` — why the list is hand-curated, and what the check can prove

Canon for the marker list in `RenderAvatar.cs` and for `ReactiveMarkersTests`. The code comment there routes here and does not restate this. Read it before editing the list — most likely on a Modular Avatar upgrade.

The list names the MA components whose rendered result only resolves once an NDMF preview has settled, so `RenderAvatar` can refuse-or-warn on a grab taken before that.

## There is no registry to reflect

Reflecting NDMF's filter registry instead of maintaining a list is unreachable, in two independent ways. The version anchor is load-bearing here — a null result about a vendor API expires when the vendor changes — so: measured against MA 1.18.1 and the NDMF on disk beside it.

- `nadena.dev.ndmf.preview.IRenderFilter` is **public**, but filters are registered by hand — literal `new SomePreview()` arguments to `PreviewingWith(...)`, at one site in MA's own bootstrap (`Editor/PluginDefinition/PluginDefinition.cs`) — into **`internal`** collections (`SolverPass.RenderFilters`, `PluginResolver.RenderFilters`). No attribute scan, no assembly scan, nothing enumerable from outside the NDMF editor assembly.
- Access would not answer the question anyway. `IRenderFilter.GetTargetGroups(ComputeContext)` returns `RenderGroup`s wrapping `Renderer`s plus opaque context data, and **neither the interface nor `RenderGroup` declares which component type drove a group.** A filter maps context to renderer work items; the component-type walk sits a layer below it, as hardcoded `GetComponentsInChildren<T>` calls in `ReactiveObjectAnalyzer.LocateReactions` plus two filters that walk their own types.

The nearest type-level signal is `nadena.dev.modular_avatar.core.ReactiveComponent` — public, so reachable by an `AppDomain` name scan plus a `BaseType` walk, with no assembly reference. It covers **5 of the 8**: `ShapeChanger`, `MeshCutter`, `ObjectToggle`, `MaterialSetter`, `MaterialSwap`. `BlendshapeSync`, `RemoveVertexColor` and `ScaleAdjuster` derive straight from `AvatarTagComponent`, which is also the base of every static MA component (menu items, merge armature, bone proxy, parameters) and so marks "MA editor-only tag component", not "participates in preview". Worth revisiting only if MA reparents those three.

So the hand-curated list is the only affordance that exists, not a shortcut taken over a better one.

## The eight participating component types

`ModularAvatarShapeChanger`, `ModularAvatarMeshCutter`, `ModularAvatarBlendshapeSync`, `ModularAvatarObjectToggle`, `ModularAvatarMaterialSetter`, `ModularAvatarMaterialSwap`, `ModularAvatarScaleAdjuster`, `ModularAvatarRemoveVertexColor`. None sits behind an `#if`, so all eight are present whenever MA is.

Re-check them against two anchors: the `PreviewingWith(...)` call site in `PluginDefinition.cs` (which filters are registered) and `ReactiveObjectAnalyzer.LocateReactions` (which component types the shared analyzer walks).

## Curate against component type names, never filter or pass class names

The two do not correspond, and the mismatch is silent. MA ships a `MeshDeleterPreview` **filter** with no `ModularAvatarMeshDeleter` **component** behind it — that filter acts on `ModularAvatarMeshCutter`. A marker named for the filter therefore matches nothing forever: the scan runs over `MonoBehaviour` components only, so a non-component type cannot match, and a dead entry costs no verdict and raises no error.

## What `ReactiveMarkersTests` proves, and the direction it cannot

It resolves every entry against the loaded domain and fails naming any that matches no MA component type — one direction only.

**It cannot catch the damaging direction**, a newly shipped MA reactive component missing from the list, because there is no registry to diff against. A green run means "no entry is dead", never "the list is complete".

The predicate takes two gates, and each one closes a different hole. **Assembly identity** (`typeof(AvatarTagComponent).Assembly`) is the anti-fixture guard: `RenderAvatarFreshnessTests` defines `modular_avatar_fixture.FakeShapeChanger`, a `MonoBehaviour` existing precisely to be matched by the production scan, so a namespace test alone is satisfied for `"ShapeChanger"` by that fixture and stays green even if MA's real type were renamed. The **`modular_avatar` namespace** filter mirrors production, which applies it before matching the marker name — without it, a marker hitting a type in MA's assembly outside that namespace passes here while production could never fire on it.

MA being absent is handled by the `#if MA_PRESENT` guard on the file, not by the in-test skip: with MA gone the fixture does not compile, so there is no run to mislead. The `Assert.Ignore` covers the narrower case of MA present with no component types resolvable.
