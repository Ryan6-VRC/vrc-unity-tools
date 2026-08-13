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
    // Every marker must match a MonoBehaviour in MA's OWN assembly. Gating on the assembly rather than on a
    // namespace substring is load-bearing: RenderAvatarFreshnessTests defines
    // modular_avatar_fixture.FakeShapeChanger in THIS assembly, to be matched by the production scan, and a
    // namespace-substring predicate is satisfied for "ShapeChanger" by that fixture alone — green with MA
    // absent or its real type renamed, and a false pass where the skip belongs.
    private static IEnumerable<Type> MaComponentTypes()
    {
        Type[] types;
        try { types = typeof(AvatarTagComponent).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        return types.Where(t => typeof(MonoBehaviour).IsAssignableFrom(t));
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
