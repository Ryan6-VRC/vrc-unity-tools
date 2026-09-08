using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

/// <summary>
/// The geometry section makes exactly one kind of claim — this renderer carries N triangles on the built
/// avatar, against M authored — and every reading a builder takes from it (a Delete removed geometry; a hide
/// only NaNimated it) is arithmetic on those two numbers. So the cases below pin the arithmetic and the three
/// states a count can be in: a number, `unreadable`, and absent. A zero where one of the other two belongs is
/// the misread, because a zero reads as "the build emptied this".
///
/// <c>ReadGeometry</c> takes a live root and <c>GeometrySection</c> is pure, so both are reachable without a
/// bake; case 5 is the exception and drives <c>AvatarBakeScope</c>'s injectable callback seam.
///
/// Objects are left for the next test's <c>NewScene(Single)</c> to take rather than destroyed —
/// <c>docs/verify.md</c> §Test venue forbids destroying live objects a fixture has mutated.
/// </summary>
public class CompositionGeometryTests
{
    [SetUp]
    public void SetUp() => UnityEditor.SceneManagement.EditorSceneManager.NewScene(
        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
        UnityEditor.SceneManagement.NewSceneMode.Single);

    [Test]
    public void TrianglesSumPerSubmesh_andANonTriangleTopologyIsNamedRatherThanCounted()
    {
        var root = new GameObject("Geo");
        Skinned(root, "Body", Tris(10));
        Static(root, "Prop", Tris(4));
        Skinned(root, "Mixed", TrisAndLines(6, 3));

        var rows = CompositionBake.ReadGeometry(root);
        string keys;
        var lines = CompositionBake.GeometrySection(rows, rows, null, out keys);

        StringAssert.Contains("| 10 | 10 |", Row(lines, "Body"));
        StringAssert.Contains("| mesh | yes | 4 | 4 |", Row(lines, "Prop"));
        // The line submesh's indices must not reach the count, and the row must SAY they were left out.
        StringAssert.Contains("| 6 | 6 |", Row(lines, "Mixed"));
        StringAssert.Contains("submesh 1 is Lines", Row(lines, "Mixed"));
        Assert.AreEqual("| total (all) | | | 20 | 20 | |", Total(lines, "all"));
        Assert.AreEqual("| total (skinned) | | | 16 | 16 | |", Total(lines, "skinned"));
        Assert.AreEqual("tris=20 trisSkinned=16 trisActive=16", keys);
    }

    [Test]
    public void AnInactiveOrDisabledRenderer_isCountedInAllAndSkinned_butNotInActive()
    {
        // Not a filter question: the build merges toggled-off renderers into an always-active mesh under
        // NaNimation, so dropping them would report a merge as a deletion on every avatar with a toggle.
        var root = new GameObject("Toggled");
        Skinned(root, "On", Tris(10));
        Skinned(root, "OffObject", Tris(5)).gameObject.SetActive(false);
        Skinned(root, "OffRenderer", Tris(7)).enabled = false;

        var rows = CompositionBake.ReadGeometry(root);
        string keys;
        var lines = CompositionBake.GeometrySection(rows, rows, null, out keys);

        StringAssert.Contains("| skinned | no |", Row(lines, "OffObject"));
        StringAssert.Contains("| skinned | no |", Row(lines, "OffRenderer"));
        Assert.AreEqual("| total (all) | | | 22 | 22 | |", Total(lines, "all"));
        Assert.AreEqual("| total (skinned) | | | 22 | 22 | |", Total(lines, "skinned"));
        Assert.AreEqual("| total (active) | | | 10 | 10 | |", Total(lines, "active"));
        StringAssert.Contains("trisActive=10", keys);
    }

    [Test]
    public void AMissingMesh_isANamedUnreadableRow_neverASilentDropAndNeverAZero()
    {
        var root = new GameObject("Broken");
        Skinned(root, "Good", Tris(9));
        Skinned(root, "NoMesh", null);
        var locked = Tris(3);
        locked.UploadMeshData(true);   // markNoLongerReadable: the CPU copy is gone, isReadable reads false
        Skinned(root, "Locked", locked);

        var rows = CompositionBake.ReadGeometry(root);
        string keys;
        var lines = CompositionBake.GeometrySection(rows, rows, null, out keys);

        string row = Row(lines, "NoMesh");
        StringAssert.Contains("| unreadable | unreadable |", row);
        StringAssert.Contains("no mesh", row);
        row = Row(lines, "Locked");
        StringAssert.Contains("| unreadable | unreadable |", row);
        StringAssert.Contains("not readable", row);
        // Unknown is not zero: neither unreadable row contributes, and the total stays the readable sum.
        Assert.AreEqual("| total (all) | | | 9 | 9 | |", Total(lines, "all"));
        Assert.AreEqual("tris=9 trisSkinned=9 trisActive=9", keys);
    }

    [Test]
    public void ParamFilter_leavesEveryRowAndSubtotalUntouched_andSaysSo()
    {
        var root = new GameObject("Filtered");
        Skinned(root, "Body", Tris(12));
        Skinned(root, "Cloth", Tris(5));
        var rows = CompositionBake.ReadGeometry(root);

        // A filter matching no renderer name: were geometry wrongly filtered by it, every row would vanish.
        string plainKeys, filteredKeys;
        var plain = CompositionBake.GeometrySection(rows, rows, null, out plainKeys);
        var filtered = CompositionBake.GeometrySection(rows, rows, "NoSuchParameter", out filteredKeys);

        Assert.AreEqual(plainKeys, filteredKeys, "a parameter filter must not move a triangle count");
        CollectionAssert.AreEqual(plain.Skip(1), filtered.Skip(1),
            "every line but the header must be identical under a filter");
        StringAssert.Contains("narrows the parameter tables ONLY", filtered[0],
            "a reader who sees a filter on the artifact must be told this table is still whole");
        Assert.AreEqual("tris=17 trisSkinned=17 trisActive=17", filteredKeys);
    }

    [Test]
    public void SameNamedSiblings_eachKeepARow_pairedByHierarchyOrder()
    {
        var root = new GameObject("Twins");
        Skinned(root, "Sleeve", Tris(3));
        Skinned(root, "Sleeve", Tris(5));

        var rows = CompositionBake.ReadGeometry(root);
        string keys;
        var lines = CompositionBake.GeometrySection(rows, rows, null, out keys);

        StringAssert.Contains("| 3 | 3 |", Row(lines, "Sleeve"));
        StringAssert.Contains("| 5 | 5 |", Row(lines, "Sleeve #2"));
        Assert.AreEqual("| total (all) | | | 8 | 8 | |", Total(lines, "all"));
    }

    [Test]
    public void ARendererThePreprocessRemoves_readsAuthoredOnly_andOneItAddsReadsBuiltOnly()
    {
        var source = new GameObject("Avatar");
        Skinned(source, "Kept", Tris(10));
        Skinned(source, "Deleted", Tris(4));

        // The injectable seam stands in for the SDK chain: the real one is what deletes and merges renderers,
        // and the scope is NOT disposed, so nothing this fixture mutated is ever destroyed.
        var scope = new AvatarBakeScope(source, "Avatar (bake)",
            clone =>
            {
                Object.DestroyImmediate(clone.transform.Find("Deleted").gameObject);
                Skinned(clone, "Merged", Tris(14));
                return true;
            },
            () => { });
        Assert.IsTrue(scope.Ok, scope.DescribeFailure());

        string keys;
        var lines = CompositionBake.GeometrySection(
            CompositionBake.ReadGeometry(source), CompositionBake.ReadGeometry(scope.Clone), null, out keys);

        StringAssert.Contains("| skinned (authored-only) | yes | 4 | — |", Row(lines, "Deleted"));
        StringAssert.Contains("| skinned (built-only) | yes | — | 14 |", Row(lines, "Merged"));
        StringAssert.Contains("| 10 | 10 |", Row(lines, "Kept"));
        Assert.AreEqual("| total (all) | | | 14 | 24 | |", Total(lines, "all"));
    }

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────────

    private static SkinnedMeshRenderer Skinned(GameObject root, string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform);
        var r = go.AddComponent<SkinnedMeshRenderer>();
        r.sharedMesh = mesh;
        return r;
    }

    private static void Static(GameObject root, string name, Mesh mesh)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>();
    }

    private static Mesh Tris(int triangles) => TrisAndLines(triangles, 0);

    private static Mesh TrisAndLines(int triangles, int lines)
    {
        var m = new Mesh();
        int count = triangles * 3 + lines * 2;
        var v = new Vector3[count];
        for (int i = 0; i < count; i++) v[i] = new Vector3(i, 0, 0);
        m.vertices = v;
        m.subMeshCount = lines > 0 ? 2 : 1;
        m.SetIndices(Enumerable.Range(0, triangles * 3).ToArray(), MeshTopology.Triangles, 0);
        if (lines > 0)
            m.SetIndices(Enumerable.Range(triangles * 3, lines * 2).ToArray(), MeshTopology.Lines, 1);
        return m;
    }

    private static string Row(List<string> lines, string path) =>
        Find(lines, "| `" + path + "` |");

    private static string Total(List<string> lines, string which) =>
        Find(lines, "| total (" + which + ") |");

    private static string Find(List<string> lines, string prefix)
    {
        var hit = lines.FirstOrDefault(l => l.StartsWith(prefix, System.StringComparison.Ordinal));
        if (hit == null) Assert.Fail("no line starting `" + prefix + "` in:\n" + string.Join("\n", lines));
        return hit;
    }
}
