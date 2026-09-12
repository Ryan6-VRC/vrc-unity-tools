using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;
using UnityEngine;

/// <summary>
/// The texture section makes one claim — this texture costs N megabytes on the built avatar against M
/// authored — and the whole section rests on that claim being the SAME measurement as the SDK total printed
/// beneath it. So the first case below is the reconciliation: rows must sum to the figure the upload gate
/// rates, or a reader who adds the column up finds the table contradicting its own total.
///
/// The rest pin the states a megabyte cell can be in — a number, `unknown`, and absent — because a zero
/// where one of the other two belongs reads as "this texture is free", which is the misread this section
/// must never manufacture. Plus the two pairing keys, which exist because neither survives both of the
/// things a build does: identity dies on a resize, the slot set dies on a renderer merge.
///
/// <c>ReadTextures</c> takes a live root and <c>TextureSection</c> is pure, so both are reachable without a
/// bake.
///
/// Objects are left for the next test's <c>NewScene(Single)</c> to take rather than destroyed —
/// <c>docs/verify.md</c> §Test venue forbids destroying live objects a fixture has mutated.
/// </summary>
public class CompositionTextureTests
{
    [SetUp]
    public void SetUp() => UnityEditor.SceneManagement.EditorSceneManager.NewScene(
        UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
        UnityEditor.SceneManagement.NewSceneMode.Single);

    /// <summary>The load-bearing case. The block arithmetic here is the same arithmetic that reproduced the
    /// SDK's own <c>textureMegabytes</c> to five decimals on a real avatar (18.18228 MB over 17 textures), so
    /// a row total that drifts from it is the section losing its right to print the SDK figure beside it.</summary>
    [Test]
    public void RowsSumToTheSdkTotal_soTheResidualReadsZero()
    {
        var root = new GameObject("Tex");
        Renderer(root, "Body", Tex("body", 256, TextureFormat.DXT1), Tex("face", 128, TextureFormat.BC7));

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);
        long expected = MipChainBytes(256, 0.5f) + MipChainBytes(128, 1f);
        float sdk = expected / 1048576f;

        string keys;
        var lines = CompositionBake.TextureSection(rows, rows, sdk, sdk, 0, 0, null, out keys);

        Assert.AreEqual(expected, rows.Sum(r => r.Bytes), "row bytes must be the block-compression arithmetic");
        StringAssert.Contains("authored 0.0000, built 0.0000", string.Join("\n", lines.ToArray()));
        StringAssert.Contains("textureMB=" + sdk.ToString("F4"), keys);
    }

    /// <summary>A texture twelve slots reach is ONE row, because the SDK counts it once. If it were rowed per
    /// slot the column would sum to more than the total it sits under.</summary>
    [Test]
    public void ASharedTextureIsOneRow_countedOnce_andSaysHowManySlotsReachIt()
    {
        var root = new GameObject("Tex");
        var shared = Tex("shared", 64, TextureFormat.DXT1);
        Renderer(root, "A", shared);
        Renderer(root, "B", shared);

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);

        Assert.AreEqual(1, rows.Count, "one texture, one row, however many slots reach it");
        Assert.AreEqual(MipChainBytes(64, 0.5f), rows[0].Bytes);

        string keys;
        var lines = CompositionBake.TextureSection(rows, rows, null, null, 0, 0, null, out keys);
        StringAssert.Contains("reached by 2 slots; counted once", string.Join("\n", lines.ToArray()));
    }

    /// <summary>The third state. A RenderTexture's bytes are not typed here, so its cell says so and it
    /// leaves the row total — never a 0, which reads as a texture that costs nothing.</summary>
    [Test]
    public void AnUntypedTextureIsUnknownRatherThanZero_andLeavesTheRowTotal()
    {
        var root = new GameObject("Tex");
        var rt = new RenderTexture(64, 64, 0) { name = "rt" };
        Renderer(root, "A", Tex("plain", 64, TextureFormat.DXT1));
        Renderer(root, "B", rt);

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);
        var unknown = rows.Single(r => r.Format == "RenderTexture");

        Assert.IsTrue(unknown.Unknown);
        Assert.AreEqual(0, unknown.Bytes, "an unknown row holds no bytes AND contributes none");
        Assert.AreEqual(MipChainBytes(64, 0.5f), rows.Where(r => !r.Unknown).Sum(r => r.Bytes));

        string keys;
        var lines = CompositionBake.TextureSection(rows, rows, null, null, 0, 0, null, out keys);
        StringAssert.Contains("unknown", Row(lines, "B["));
        StringAssert.DoesNotContain("| 0.0000 | 0.0000 |", Row(lines, "B["));
    }

    /// <summary>A shader property the material leaves null is NOTHING — not a zero-byte row that a reader
    /// would scan past as a texture costing nothing.</summary>
    [Test]
    public void ANullTextureSlotIsNoRowAtAll()
    {
        var root = new GameObject("Tex");
        var mat = new Material(Shader.Find("Standard")) { name = "m" };
        mat.SetTexture("_MainTex", Tex("main", 64, TextureFormat.DXT1));
        mat.SetTexture("_BumpMap", null);
        var go = new GameObject("A");
        go.transform.SetParent(root.transform);
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);

        Assert.AreEqual(1, rows.Count, "only the populated property is a row");
        StringAssert.Contains("_MainTex", rows[0].Slot);
    }

    /// <summary>Identity pairs a texture the build left alone even though a renderer merge moved every slot
    /// handle — the case the slot key alone cannot reach, and the ordinary one under d4rk.</summary>
    [Test]
    public void IdentityPairsAcrossARendererMerge_whereTheSlotHandleMoved()
    {
        var authoredRoot = new GameObject("A");
        var shared = Tex("kept", 64, TextureFormat.DXT1);
        Renderer(authoredRoot, "Hair", shared);

        var builtRoot = new GameObject("B");
        Renderer(builtRoot, "MergedMesh", shared);   // same texture, different slot: a merge

        int s1, s2;
        var authored = CompositionBake.ReadTextures(authoredRoot, out s1);
        var built = CompositionBake.ReadTextures(builtRoot, out s2);

        string keys;
        var lines = CompositionBake.TextureSection(authored, built, null, null, 0, 0, null, out keys);
        var body = string.Join("\n", lines.ToArray());

        StringAssert.DoesNotContain("(built-only)", body);
        StringAssert.DoesNotContain("(authored-only)", body);
    }

    /// <summary>The mirror: a resize mints a new object, so identity is gone and the slot set is what pairs
    /// the two sides. Without this key every resized texture would read as a deletion plus an addition.</summary>
    [Test]
    public void TheSlotKeyPairsAResize_whereIdentityIsGone()
    {
        var authoredRoot = new GameObject("A");
        Renderer(authoredRoot, "Body", Tex("big", 256, TextureFormat.DXT1));

        var builtRoot = new GameObject("B");
        Renderer(builtRoot, "Body", Tex("big (MaxTextureSize)", 64, TextureFormat.DXT1));

        int s1, s2;
        var authored = CompositionBake.ReadTextures(authoredRoot, out s1);
        var built = CompositionBake.ReadTextures(builtRoot, out s2);

        string keys;
        var lines = CompositionBake.TextureSection(authored, built, null, null, 0, 0, null, out keys);
        var row = Row(lines, "Body[0]._MainTex");

        StringAssert.DoesNotContain("built-only", row);
        StringAssert.Contains(Mb(MipChainBytes(256, 0.5f)) + " | " + Mb(MipChainBytes(64, 0.5f)), row);
    }

    /// <summary>The disclosed omission. A material only a swap curve can reach is counted and named; the
    /// count says zero by measurement, not by assumption, when there is nothing to disclose.</summary>
    [Test]
    public void SwapOnlyMaterialsAreCountedAndDisclosed_andZeroSaysSoExplicitly()
    {
        var root = new GameObject("Tex");
        Renderer(root, "Body", Tex("main", 64, TextureFormat.DXT1));

        int swap;
        CompositionBake.ReadTextures(root, out swap);
        Assert.AreEqual(0, swap, "no animator, no swap-reachable material");

        var rows = CompositionBake.ReadTextures(root, out swap);
        string keys;
        var none = string.Join("\n", CompositionBake.TextureSection(rows, rows, null, null, 0, 0, null, out keys).ToArray());
        StringAssert.Contains("none on either side", none);

        var some = string.Join("\n", CompositionBake.TextureSection(rows, rows, null, null, 2, 3, null, out keys).ToArray());
        StringAssert.Contains("2 authored, 3 built", some);
        StringAssert.Contains("in NO row above", some);
    }

    /// <summary>A null SDK stat is an absent figure. `textureMB=0` would be a confident claim that the avatar
    /// carries no textures at all.</summary>
    [Test]
    public void ANullSdkStatReportsUnknown_neverZero()
    {
        var root = new GameObject("Tex");
        Renderer(root, "Body", Tex("main", 64, TextureFormat.DXT1));

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);
        string keys;
        CompositionBake.TextureSection(rows, rows, null, null, 0, 0, null, out keys);

        Assert.AreEqual("textureMB=unknown", keys);
    }

    /// <summary>paramFilter narrows the parameter tables only; a geometry- or texture-table narrowed by it
    /// would report a partial avatar under totals that read whole, so it is disclosed rather than applied.</summary>
    [Test]
    public void ParamFilterNarrowsNothingHere_andSaysSo()
    {
        var root = new GameObject("Tex");
        Renderer(root, "Body", Tex("main", 64, TextureFormat.DXT1));

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);
        string keys;
        var lines = CompositionBake.TextureSection(rows, rows, null, null, 0, 0, "NoSuchParam", out keys);

        Assert.AreEqual(1, rows.Count, "the filter does not remove rows");
        StringAssert.Contains("narrows the parameter tables ONLY", lines[0]);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same chain the section computes: each mip a quarter of the last, rounded per level.</summary>
    private static long MipChainBytes(int dim, float bpp)
    {
        long px = (long)dim * dim, total = 0;
        var t = new Texture2D(dim, dim, TextureFormat.DXT1, true);
        int mips = t.mipmapCount;
        for (int i = 0; i < mips && (px >> (2 * i)) >= 1; i++) total += Mathf.RoundToInt((px >> (2 * i)) * bpp);
        return total;
    }

    private static string Mb(long bytes) => (bytes / 1048576f).ToString("F4");

    private static Texture2D Tex(string name, int dim, TextureFormat fmt) =>
        new Texture2D(dim, dim, fmt, true) { name = name };

    private static void Renderer(GameObject root, string name, params Texture[] textures)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform);
        var mats = new List<Material>();
        foreach (var t in textures)
        {
            var m = new Material(Shader.Find("Standard")) { name = name + "_mat" };
            m.SetTexture("_MainTex", t);
            mats.Add(m);
        }
        go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
    }

    private static string Row(List<string> lines, string contains) =>
        lines.First(l => l.Contains(contains));
}
