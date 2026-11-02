#if MA_PRESENT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using Ryan6Vrc.AgentTools.Editor;

// Why the list is hand-curated, what a green run cannot prove, and why both gates: Tests/Editor/ReactiveMarkers.md.
[Category("ReactiveMarkers")]
public class ReactiveMarkersTests
{
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
