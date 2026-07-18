using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Ryan6Vrc.AvatarTools.Editor;

// Pins the DIRECTION and CARDINALITY of ConformRenderers' ownedToSource map.
//
// The map runs opposite to the transplant kit's vendorToOwned (CopyComponents / GraftHierarchy). Both obey
// one invariant — key names the hierarchy the tool WALKS, value the one it RESOLVES INTO — and this tool
// walks OUR renderers, so the key is ours. Nothing pinned that before: a regression flipping the lookup
// would have passed the whole suite, and the asymmetry reads as an accident to anyone who hasn't traced
// both traversals. These tests make the direction a contract rather than a comment.
public class ConformRenderersRenameDirectionTests
{
    static Material MakeMat(string name) => new Material(Shader.Find("Unlit/Color")) { name = name };

    static GameObject BuildOwned(params string[] rendererNames)
    {
        var root = new GameObject("Owned");
        new GameObject("Hips").transform.SetParent(root.transform, false);   // lets the anchor step resolve
        foreach (var n in rendererNames)
        {
            var go = new GameObject(n);
            go.transform.SetParent(root.transform, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = new Mesh();
            smr.sharedMaterials = new Material[] { null };
        }
        return root;
    }

    static GameObject BuildSource(string rendererName, Material mat)
    {
        var root = new GameObject("Src");
        var go = new GameObject(rendererName);
        go.transform.SetParent(root.transform, false);
        var smr = go.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = new Mesh();
        smr.sharedMaterials = new Material[] { mat };
        return root;
    }

    static SkinnedMeshRenderer Find(GameObject root, string name) =>
        root.transform.Find(name).GetComponent<SkinnedMeshRenderer>();

    // Our renderer is "Body"; the source calls the same mesh "Face". The correct map is keyed on OUR name.
    [Test]
    public void Map_is_keyed_on_OUR_name_not_the_sources()
    {
        var owned = BuildOwned("Body");
        var mat = MakeMat("FaceMat");
        var src = BuildSource("Face", mat);

        var summary = ConformRenderers.Run(owned, src,
            new Dictionary<string, string> { { "Body", "Face" } });

        Assert.AreSame(mat, Find(owned, "Body").sharedMaterials[0],
            "ownedToSource {ourName => sourceName} must resolve our 'Body' to the source's 'Face'");
        StringAssert.Contains("overrides=1", summary);

        Object.DestroyImmediate(owned);
        Object.DestroyImmediate(src);
    }

    // The same map written backwards must NOT silently work. Without this, a flipped implementation would
    // still pass every other test in the suite.
    [Test]
    public void Reversed_map_does_not_resolve_and_fails_loud()
    {
        var owned = BuildOwned("Body");
        var mat = MakeMat("FaceMat");
        var src = BuildSource("Face", mat);

        LogAssert.Expect(LogType.Error, new Regex("=> FAIL"));
        var summary = ConformRenderers.Run(owned, src,
            new Dictionary<string, string> { { "Face", "Body" } });   // backwards on purpose

        Assert.IsNull(Find(owned, "Body").sharedMaterials[0],
            "a source-keyed map must not resolve — that direction belongs to the transplant kit");
        StringAssert.Contains("unmatched=1", summary);
        StringAssert.Contains("FAIL", summary);

        Object.DestroyImmediate(owned);
        Object.DestroyImmediate(src);
    }

    // Cardinality: the docs claim many-to-one is legitimate here (and is the reason this map cannot simply
    // be inverted to match the transplant kit's). Assert it, or that claim is only aspirational.
    [Test]
    public void Many_owned_renderers_may_map_onto_one_source_renderer()
    {
        var owned = BuildOwned("Body_A", "Body_B");
        var mat = MakeMat("BodyMat");
        var src = BuildSource("Body", mat);

        var summary = ConformRenderers.Run(owned, src, new Dictionary<string, string>
        {
            { "Body_A", "Body" },
            { "Body_B", "Body" },
        });

        Assert.AreSame(mat, Find(owned, "Body_A").sharedMaterials[0], "first owned mesh takes the source material");
        Assert.AreSame(mat, Find(owned, "Body_B").sharedMaterials[0], "second owned mesh takes the SAME source material");
        StringAssert.Contains("overrides=2", summary);
        // Injectivity is a vendorToOwned requirement (one unique dst sibling); it must NOT be enforced here.
        StringAssert.DoesNotContain("non-injective", summary);

        Object.DestroyImmediate(owned);
        Object.DestroyImmediate(src);
    }

    // Case-insensitivity is this tool's own convention (NormalizeRenameMap lowercases) and diverges from the
    // transplant kit's Ordinal matching — the second way a caller holding "one map" gets surprised.
    [Test]
    public void Map_matching_is_case_insensitive_unlike_the_transplant_kit()
    {
        var owned = BuildOwned("Body");
        var mat = MakeMat("FaceMat");
        var src = BuildSource("Face", mat);

        ConformRenderers.Run(owned, src,
            new Dictionary<string, string> { { "BODY", "fAcE" } });

        Assert.AreSame(mat, Find(owned, "Body").sharedMaterials[0],
            "both sides of the map are lowercased before matching");

        Object.DestroyImmediate(owned);
        Object.DestroyImmediate(src);
    }
}
