#if MA_PRESENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using Ryan6Vrc.AgentTools.Editor;

// RenderAvatar.ReactiveMarkers is hand-curated because NDMF exposes no reflectable registry of
// preview-participating component types. Tests/Editor/ReactiveMarkers.md is the canon for why, for the eight
// types, and for the bound below; this file does not restate it.
//
// The bound, because a green run is easy to over-read: this proves no entry is DEAD. It cannot prove the list
// is COMPLETE — a newly shipped MA reactive component missing from the list is invisible here, with no
// registry to diff against.
[Category("ReactiveMarkers")]
public class ReactiveMarkersTests
{
    // Every marker must match a MonoBehaviour that production could actually fire on, which takes BOTH gates.
    //
    // The ASSEMBLY gate is the anti-fixture guard, and it is load-bearing: RenderAvatarFreshnessTests defines
    // modular_avatar_fixture.FakeShapeChanger in THIS assembly, to be matched by the production scan, so a
    // namespace test alone is satisfied for "ShapeChanger" by that fixture and stays green even if MA's real
    // type were renamed.
    //
    // The NAMESPACE gate mirrors production. HasReactiveMA filters on a `modular_avatar` namespace before it
    // matches the marker name, so a marker hitting a type in MA's assembly OUTSIDE that namespace would pass
    // here while production could never fire on it — an entry that reads live and is dead in place. Latent
    // today (MA's components all sit under nadena.dev.modular_avatar.core), which is why it is asserted rather
    // than left to stay true by accident.
    private static IEnumerable<Type> MaComponentTypes()
    {
        Type[] types;
        try { types = typeof(AvatarTagComponent).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        return types.Where(t => typeof(MonoBehaviour).IsAssignableFrom(t)
            && (t.Namespace ?? "").IndexOf("modular_avatar", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string[] Markers()
    {
        var f = typeof(RenderAvatar).GetField("ReactiveMarkers",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(f, "RenderAvatar.ReactiveMarkers not found — renamed or removed; this check is now blind");
        return (string[])f.GetValue(null);
    }

    [Test]
    public void EveryMarkerMatchesALiveMaComponentType()
    {
        var maTypes = MaComponentTypes().ToList();
        // A named skip, never a silent pass: with no MA component types loaded the predicate is vacuous and
        // every marker would "fail" for a reason that says nothing about the list.
        if (maTypes.Count == 0)
            Assert.Ignore("no MonoBehaviour types in MA's assembly — cannot adjudicate the marker list");

        var dead = Markers()
            .Where(mk => !maTypes.Any(t => t.Name.IndexOf(mk, StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        Assert.IsEmpty(dead,
            "ReactiveMarkers entr(y/ies) match no MA component type and can never fire: "
            + string.Join(", ", dead)
            + ". Curate against component type names, not filter/pass class names (Tests/Editor/ReactiveMarkers.md).");
    }
}
#endif
