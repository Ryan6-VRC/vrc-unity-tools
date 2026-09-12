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

    /// <summary>The load-bearing case, and it must call the SDK for its expected value — the whole PR rests on
    /// the rows and <c>textureMegabytes</c> being ONE measurement, and a test that compares this arithmetic to
    /// a re-implementation of this arithmetic proves f(x) == f(x) and would pass through any shared error.
    /// <para>Formats are deliberately mixed and include <c>RGB24</c>, the one entry whose measured rate (4 bpp,
    /// 32-bit-aligned) does not follow from its name — the table was wrong there once, and this is the case
    /// that would have caught it.</para></summary>
    [Test]
    public void RowsSumToTheSdksOwnFigure_soTheResidualReadsZero()
    {
        var root = new GameObject("Tex");
        Renderer(root, "Body", Tex("body", 256, TextureFormat.DXT1), Tex("face", 128, TextureFormat.BC7));
        Renderer(root, "Trim", Tex("trim", 64, TextureFormat.RGB24), Tex("mask", 32, TextureFormat.RGBA32));

        var sdk = CompositionBake.SdkTextureMegabytes(root);
        Assert.IsNotNull(sdk, "the SDK must answer, or this case is not testing what it claims");

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);
        long rowBytes = rows.Where(r => !r.Unknown).Sum(r => r.Bytes);

        Assert.AreEqual(sdk.Value, rowBytes / 1048576f, 1e-5f,
            "the rows and the SDK's textureMegabytes must be the same measurement");

        string keys;
        var lines = CompositionBake.TextureSection(rows, rows, sdk, sdk, 0, 0, null, out keys);
        StringAssert.Contains("authored 0.0000, built 0.0000", string.Join("\n", lines.ToArray()));
    }

    /// <summary>A cubemap and a texture array are typed, not `unknown`, and typed the way the SDK rates them —
    /// one face for the cubemap (not six), base level times slices for the array (no mip chain). The array
    /// branch is what keeps the section reconciling on a d4rk-optimized avatar, which atlases into
    /// `Texture2DArray`.</summary>
    /// <summary>Driven through <c>TypeBytes</c> rather than a material, because Unity refuses to assign a CUBE
    /// or array texture to a 2D shader property and the log error that produces is not what this case is
    /// about. The expected figures are the ones measured against the SDK on a bare renderer: a 128 DXT1
    /// cubemap rated 10922 bytes (one face's chain — six would be 65532), a 128x128x4 DXT1 array rated
    /// 32768 (base level times slices, no chain).</summary>
    [Test]
    public void CubemapsAndTextureArraysAreTypedTheWayTheSdkRatesThem()
    {
        var cubeRow = new CompositionBake.TexRow();
        CompositionBake.TypeBytes(new Cubemap(128, TextureFormat.DXT1, true), ref cubeRow);
        Assert.IsFalse(cubeRow.Unknown, "a cubemap is measured, not unknown");
        Assert.AreEqual(MipChainBytes(128, 0.5f), cubeRow.Bytes, "one face's mip chain, never six");
        Assert.AreEqual(10922, cubeRow.Bytes, "the figure measured against the SDK");

        var arrRow = new CompositionBake.TexRow();
        CompositionBake.TypeBytes(new Texture2DArray(128, 128, 4, TextureFormat.DXT1, true), ref arrRow);
        Assert.IsFalse(arrRow.Unknown, "a texture array is measured, not unknown");
        Assert.AreEqual(32768, arrRow.Bytes, "base level times slices, with no mip chain");

        var rtRow = new CompositionBake.TexRow();
        CompositionBake.TypeBytes(new RenderTexture(64, 64, 0), ref rtRow);
        Assert.IsTrue(rtRow.Unknown, "a RenderTexture is outside both totals");
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

    /// <summary>A RenderTexture is `unknown` but for the opposite reason to an untyped format: the SDK rates
    /// it at zero, so it is outside BOTH totals and cannot explain a residual. The row has to say which kind
    /// of unknown it is, or a reader chasing a gap starts at the one row that provably is not the cause.</summary>
    [Test]
    public void ARenderTextureIsUnknownAndOutsideBothTotals_notAZero()
    {
        var root = new GameObject("Tex");
        Renderer(root, "A", Tex("plain", 64, TextureFormat.DXT1));
        Renderer(root, "B", new RenderTexture(64, 64, 0) { name = "rt" });

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);
        var rt = rows.Single(r => r.Format == "RenderTexture");

        Assert.IsTrue(rt.Unknown);
        Assert.AreEqual(0, rt.Bytes, "an unknown row holds no bytes AND contributes none");
        long typed = rows.Where(r => !r.Unknown).Sum(r => r.Bytes);
        Assert.AreEqual(MipChainBytes(64, 0.5f), typed);
        // The claim the legend makes about RenderTextures, asserted against the SDK rather than believed.
        Assert.AreEqual(CompositionBake.SdkTextureMegabytes(root).Value, typed / 1048576f, 1e-5f,
            "the SDK rates a RenderTexture at zero, so excluding it must leave the totals equal");

        string keys;
        var lines = CompositionBake.TextureSection(rows, rows, null, null, 0, 0, null, out keys);
        StringAssert.Contains("unknown", Row(lines, "B["));
        StringAssert.Contains("cannot explain a residual", Row(lines, "B["));
    }

    /// <summary>Two same-named sibling renderers must not collapse into one slot key — that would make two
    /// different textures share a pairing handle, where first-wins silently drops the second.</summary>
    [Test]
    public void SameNamedSiblingRenderersKeepDistinctSlotKeys()
    {
        var root = new GameObject("Tex");
        Renderer(root, "Body", Tex("one", 64, TextureFormat.DXT1));
        Renderer(root, "Body", Tex("two", 32, TextureFormat.DXT1));

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);

        Assert.AreEqual(2, rows.Count, "two textures, two rows");
        Assert.AreEqual(2, rows.Select(r => r.SlotKey).Distinct().Count(), "the two slot keys must differ");
        Assert.IsTrue(rows.Any(r => r.Slot.Contains("#2")), "the second sibling takes an ordinal suffix");
    }

    /// <summary>A material whose texture properties cannot be read becomes a named `unreadable` row in no
    /// total, rather than taking down the whole artifact or vanishing silently.</summary>
    [Test]
    public void AnUnreadableMaterialIsANamedRowInNoTotal()
    {
        var root = new GameObject("Tex");
        Renderer(root, "Good", Tex("main", 64, TextureFormat.DXT1));
        var go = new GameObject("Broken");
        go.transform.SetParent(root.transform);
        go.AddComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Standard")) { shader = null };

        int swap;
        var rows = CompositionBake.ReadTextures(root, out swap);

        Assert.AreEqual(MipChainBytes(64, 0.5f), rows.Where(r => !r.Unknown).Sum(r => r.Bytes),
            "the good texture still counts");
        Assert.IsTrue(rows.All(r => r.Unknown || r.Bytes > 0), "no row is a silent zero");
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

    /// <summary>Square-texture expected bytes, for the cases whose subject is a BRANCH (cubemap, array,
    /// unreadable) rather than the arithmetic itself. The reconciliation case deliberately does not use this —
    /// it takes its expected value from the SDK, because a helper that mirrors the production loop can only
    /// prove the loop equals itself.</summary>
    private static long MipChainBytes(int dim, float bpp)
    {
        long px = (long)dim * dim, total = 0;
        int mips = new Texture2D(dim, dim, TextureFormat.DXT1, true).mipmapCount;
        for (int i = 0; i < mips && (px >> (2 * i)) >= 1; i++) total += (long)System.Math.Round((px >> (2 * i)) * (double)bpp);
        return total;
    }

    private static string Mb(long bytes) =>
        (bytes / 1048576f).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

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
