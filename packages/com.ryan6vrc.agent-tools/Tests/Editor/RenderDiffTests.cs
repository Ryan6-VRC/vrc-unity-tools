using System;
using NUnit.Framework;
using UnityEngine;
using Ryan6Vrc.AgentTools.Editor;

// Pixel-buffer diff/count helpers verified on hand-built small buffers so expected values are obvious.
// The load-bearing guarantee under test is EXACT rgb compare (no silent tolerance): a 1-LSB delta counts,
// and a near-color does NOT match a target — the band-mask trap this design deliberately avoids.
public class RenderDiffTests
{
    // Solid fill of a w*h buffer, alpha 255.
    static Color32[] Fill(int w, int h, Color32 c)
    {
        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(c.r, c.g, c.b, 255);
        return px;
    }

    static readonly Color32 Black = new Color32(0, 0, 0, 255);
    static readonly Color32 Magenta = new Color32(255, 0, 255, 255);

    [Test]
    public void Compare_IdenticalBuffers_IdenticalTrue()
    {
        int w = 4, h = 4;
        var a = Fill(w, h, Black);
        var b = Fill(w, h, Black);

        bool identical = RenderDiff.Compare(a, b, w, h, out int changed, out RectInt bbox);

        Assert.IsTrue(identical);
        Assert.AreEqual(0, changed);
        Assert.AreEqual(new RectInt(0, 0, 0, 0), bbox);
    }

    [Test]
    public void Compare_SingleDifferingPixel_Changed1_BboxIs1x1()
    {
        int w = 4, h = 4;
        int x = 2, y = 1;
        var a = Fill(w, h, Black);
        var b = Fill(w, h, Black);
        b[y * w + x] = new Color32(255, 255, 255, 255);

        bool identical = RenderDiff.Compare(a, b, w, h, out int changed, out RectInt bbox);

        Assert.IsFalse(identical);
        Assert.AreEqual(1, changed);
        Assert.AreEqual(new RectInt(x, y, 1, 1), bbox);
    }

    [Test]
    public void Compare_DifferingBlock_ChangedEqualsAreaBboxEqualsBlock()
    {
        int w = 8, h = 8;
        var a = Fill(w, h, Black);
        var b = Fill(w, h, Black);
        // Block x in [2,4], y in [3,6] => 3 wide, 4 tall = 12 pixels.
        int x0 = 2, y0 = 3, bw = 3, bh = 4;
        for (int y = y0; y < y0 + bh; y++)
            for (int x = x0; x < x0 + bw; x++)
                b[y * w + x] = Magenta;

        RenderDiff.Compare(a, b, w, h, out int changed, out RectInt bbox);

        Assert.AreEqual(bw * bh, changed);
        Assert.AreEqual(new RectInt(x0, y0, bw, bh), bbox);
    }

    [Test]
    public void Compare_OneLsbDelta_Changed1_ExactNoTolerance()
    {
        int w = 4, h = 4;
        var a = Fill(w, h, new Color32(100, 100, 100, 255));
        var b = Fill(w, h, new Color32(100, 100, 100, 255));
        // Exactly 1 LSB different in the green channel of one pixel.
        b[5] = new Color32(100, 101, 100, 255);

        bool identical = RenderDiff.Compare(a, b, w, h, out int changed, out RectInt bbox);

        Assert.IsFalse(identical);
        Assert.AreEqual(1, changed);
        Assert.AreEqual(new RectInt(5 % w, 5 / w, 1, 1), bbox);
    }

    [Test]
    public void Compare_LengthMismatch_Throws()
    {
        int w = 4, h = 4;
        var a = Fill(w, h, Black);
        var b = new Color32[w * h - 1];

        Assert.Throws<ArgumentException>(() =>
            RenderDiff.Compare(a, b, w, h, out int _, out RectInt _));
    }

    [Test]
    public void CountColor_AllMagenta_CountsWholeBuffer()
    {
        int w = 4, h = 4;
        var px = Fill(w, h, Magenta);

        int count = RenderDiff.CountColor(px, w, h, Magenta, out RectInt bbox);

        Assert.AreEqual(w * h, count);
        Assert.AreEqual(new RectInt(0, 0, w, h), bbox);
    }

    [Test]
    public void CountColor_NoMagenta_Zero_BboxZero()
    {
        int w = 4, h = 4;
        var px = Fill(w, h, Black);

        int count = RenderDiff.CountColor(px, w, h, Magenta, out RectInt bbox);

        Assert.AreEqual(0, count);
        Assert.AreEqual(new RectInt(0, 0, 0, 0), bbox);
    }

    [Test]
    public void CountColor_MagentaBlockOnBackground_CountAndBbox()
    {
        int w = 8, h = 8;
        var px = Fill(w, h, Black);
        // Block x in [1,2], y in [4,6] => 2 wide, 3 tall = 6 pixels.
        int x0 = 1, y0 = 4, bw = 2, bh = 3;
        for (int y = y0; y < y0 + bh; y++)
            for (int x = x0; x < x0 + bw; x++)
                px[y * w + x] = Magenta;

        int count = RenderDiff.CountColor(px, w, h, Magenta, out RectInt bbox);

        Assert.AreEqual(bw * bh, count);
        Assert.AreEqual(new RectInt(x0, y0, bw, bh), bbox);
    }

    [Test]
    public void CountColor_SkinPinkVsMagentaTarget_NotCounted()
    {
        int w = 4, h = 4;
        var px = Fill(w, h, Black);
        // A near-color pixel — must NOT match an exact magenta target.
        px[3] = new Color32(235, 180, 170, 255);

        int count = RenderDiff.CountColor(px, w, h, Magenta, out RectInt bbox);

        Assert.AreEqual(0, count);
        Assert.AreEqual(new RectInt(0, 0, 0, 0), bbox);
    }
}
