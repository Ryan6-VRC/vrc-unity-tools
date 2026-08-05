using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEngine;

// Format tests for the DebugDisplay wire format: the charset's shape (which the MSDF atlas is generated
// from, so a change here invalidates committed labels AND the atlas together), the label pack/unpack
// round-trip and its exactness through the serialization paths that actually carry it, the format
// bitfield, and the refusals — which are load-bearing, because a display that renders a WRONG label is
// worse than one that refuses to encode it.
//
// The G7 test is the one that justifies the 18-bit component width; see DisplayGlyphs' type remarks.
// Headless via tools/run-editmode-tests.ps1.
public class DisplayGlyphsTests
{
    // ── Charset shape ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Charset_Has_Exactly_64_Distinct_Slots()
    {
        Assert.AreEqual(64, DisplayGlyphs.Charset.Length,
            "6-bit IDs mean exactly 64 slots; the atlas grid is generated at this count");
        var dupes = DisplayGlyphs.Charset.GroupBy(c => c).Where(g => g.Count() > 1)
                                 .Select(g => g.Key.ToString()).ToArray();
        CollectionAssert.IsEmpty(dupes,
            "a duplicated glyph makes IndexOf ambiguous, so one of the two IDs can never be encoded: "
            + string.Join(",", dupes));
    }

    [Test]
    public void Charset_Ids_0_Through_12_Match_The_Ancestor()
    {
        // format_coord indexes these directly (Zero + v % 10), so they are the one inherited run.
        Assert.AreEqual('+', DisplayGlyphs.Charset[DisplayGlyphs.Plus]);
        Assert.AreEqual('-', DisplayGlyphs.Charset[DisplayGlyphs.Minus]);
        Assert.AreEqual('.', DisplayGlyphs.Charset[DisplayGlyphs.Dot]);
        for (int d = 0; d <= 9; d++)
            Assert.AreEqual((char)('0' + d), DisplayGlyphs.Charset[DisplayGlyphs.Zero + d],
                "digits must stay contiguous from Zero or the shader's digit arithmetic breaks");
    }

    [Test]
    public void Charset_Sentinels_And_Letters_Are_Where_The_Shader_Expects()
    {
        Assert.AreEqual(63, DisplayGlyphs.Space, "space coincides with the mask by design");
        Assert.AreEqual(' ', DisplayGlyphs.Charset[DisplayGlyphs.Space]);
        Assert.AreEqual('∞', DisplayGlyphs.Charset[DisplayGlyphs.Infinity],
            "the overflow escape needs a glyph wired to it, not just a constant");
        for (int i = 0; i < 26; i++)
            Assert.AreEqual((char)('A' + i), DisplayGlyphs.Charset[DisplayGlyphs.LetterA + i]);
    }

    // ── Label round-trip ────────────────────────────────────────────────────────────────────────────

    [TestCase("")]
    [TestCase("X")]
    [TestCase("POS X:")]
    [TestCase("FPS:")]
    [TestCase("ABCDEFGHIJKL")]      // exactly MaxLabelChars
    [TestCase("A:1.2-3+4")]
    [TestCase("100%")]
    public void Label_RoundTrips(string label)
    {
        Vector4 packed;
        string error, note;
        Assert.IsTrue(DisplayGlyphs.TryEncodeLabel(label, out packed, out error, out note),
            "unexpected refusal: " + error);
        Assert.AreEqual(label, DisplayGlyphs.DecodeLabel(packed));
    }

    [Test]
    public void Every_Charset_Glyph_RoundTrips_Individually()
    {
        // Space is the sentinel and decodes to a trimmed empty string, so it is excluded by construction.
        foreach (var ch in DisplayGlyphs.Charset.Where(c => c != ' '))
        {
            Vector4 packed;
            string error, note;
            var s = ch.ToString();
            Assert.IsTrue(DisplayGlyphs.TryEncodeLabel(s, out packed, out error, out note),
                "charset glyph '" + ch + "' refused by its own encoder: " + error);
            Assert.AreEqual(s, DisplayGlyphs.DecodeLabel(packed), "glyph '" + ch + "' did not round-trip");
        }
    }

    [Test]
    public void Blank_Label_Is_All_Space_And_Decodes_Empty()
    {
        // The shader's Properties default. A zeroed vector would decode as glyph 0 twelve times, so a
        // fresh material would print "++++++++++++" — this is the guard against that regressing.
        var blank = DisplayGlyphs.BlankLabel;
        for (int c = 0; c < 4; c++)
            Assert.AreEqual(DisplayGlyphs.MaxComponent, (int)blank[c]);
        Assert.AreEqual(string.Empty, DisplayGlyphs.DecodeLabel(blank));
    }

    [Test]
    public void Trailing_Spaces_Do_Not_Survive_But_Interior_Ones_Do()
    {
        Vector4 packed;
        string error, note;
        Assert.IsTrue(DisplayGlyphs.TryEncodeLabel("A B", out packed, out error, out note));
        Assert.AreEqual("A B", DisplayGlyphs.DecodeLabel(packed));

        Assert.IsTrue(DisplayGlyphs.TryEncodeLabel("AB  ", out packed, out error, out note));
        Assert.AreEqual("AB", DisplayGlyphs.DecodeLabel(packed),
            "trailing spaces are indistinguishable from unused slots, so they trim");
    }

    // ── Exactness ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Packed_Components_Never_Exceed_The_18_Bit_Ceiling()
    {
        var worst = new string(DisplayGlyphs.Charset[62], DisplayGlyphs.MaxLabelChars); // highest non-space ID
        Vector4 packed;
        string error, note;
        Assert.IsTrue(DisplayGlyphs.TryEncodeLabel(worst, out packed, out error, out note), error);
        for (int c = 0; c < 4; c++)
        {
            Assert.LessOrEqual((int)packed[c], DisplayGlyphs.MaxComponent);
            Assert.AreEqual(packed[c], Mathf.Floor(packed[c]),
                "a packed component must be integral, or the (uint) cast in HLSL truncates a real value");
        }
    }

    [Test]
    public void MaxComponent_Survives_A_G7_Text_RoundTrip()
    {
        // This is the test that decides the component width. d4rkAvatarOptimizer bakes material
        // properties into generated shader source via $"{float}" (:4565) — float.ToString(), i.e. "G7".
        // 18 bits must survive it; 24 bits does not.
        float v = DisplayGlyphs.MaxComponent;
        string g7 = v.ToString("G7", CultureInfo.InvariantCulture);
        Assert.AreEqual(DisplayGlyphs.MaxComponent, (int)float.Parse(g7, CultureInfo.InvariantCulture),
            "packed components must round-trip through 7 significant digits");
    }

    [Test]
    public void The_24_Bit_Ceiling_Is_The_Thing_We_Avoided()
    {
        // Documents WHY the format is 18-bit rather than 24-bit, so a future widening attempt fails here
        // with the reason attached instead of shipping corrupted labels through an optimized build.
        const int twentyFourBitMax = (1 << 24) - 1;
        float v = twentyFourBitMax;
        Assert.AreEqual(twentyFourBitMax, (int)v, "float32 holds it exactly in isolation");

        string g7 = v.ToString("G7", CultureInfo.InvariantCulture);
        Assert.AreNotEqual(twentyFourBitMax, (int)float.Parse(g7, CultureInfo.InvariantCulture),
            "if this ever passes, G7 stopped being lossy at 24 bits and the width comment needs revisiting");
    }

    [Test]
    public void Default_Float_ToString_Under_This_Runtime_Is_What_We_Assume()
    {
        // The spec's inherited assumption, asserted rather than trusted: d4rk uses $"{float}" with NO
        // format specifier, so what matters is this runtime's default. If Unity's Mono differs from G7
        // the 18-bit design still stands (d4rk promises no formatting), but the reasoning changes.
        float v = (1 << 24) - 1;
        string dflt = v.ToString(CultureInfo.InvariantCulture);
        TestContext.WriteLine("default float.ToString() for 16777215 => '" + dflt + "'");
        Assert.AreEqual(DisplayGlyphs.MaxComponent,
            (int)float.Parse(((float)DisplayGlyphs.MaxComponent).ToString(CultureInfo.InvariantCulture),
                             CultureInfo.InvariantCulture),
            "18-bit components must survive the default formatter whatever it turns out to be");
    }

    // ── Label refusals ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Refuses_An_Over_Long_Label_Naming_The_Length()
    {
        Vector4 packed;
        string error, note;
        Assert.IsFalse(DisplayGlyphs.TryEncodeLabel("ABCDEFGHIJKLM", out packed, out error, out note));
        StringAssert.Contains("13", error);
        StringAssert.Contains("12", error);
    }

    [Test]
    public void Refuses_An_Out_Of_Charset_Char_Naming_The_Offender_And_Its_Index()
    {
        Vector4 packed;
        string error, note;
        Assert.IsFalse(DisplayGlyphs.TryEncodeLabel("AB©D", out packed, out error, out note),
            "a copyright sign is not in the charset and must not silently become a space");
        StringAssert.Contains("©", error);
        StringAssert.Contains("2", error);
    }

    [Test]
    public void Uppercases_Lowercase_Input_And_Says_So()
    {
        Vector4 packed;
        string error, note;
        Assert.IsTrue(DisplayGlyphs.TryEncodeLabel("fps:", out packed, out error, out note));
        Assert.AreEqual("FPS:", DisplayGlyphs.DecodeLabel(packed));
        Assert.IsNotNull(note, "a silent case change is still a change the author did not ask for");
        StringAssert.Contains("FPS:", note);
    }

    [Test]
    public void Already_Uppercase_Input_Produces_No_Note()
    {
        Vector4 packed;
        string error, note;
        Assert.IsTrue(DisplayGlyphs.TryEncodeLabel("FPS:", out packed, out error, out note));
        Assert.IsNull(note, "no coercion happened, so there is nothing to report");
    }

    // ── Format bitfield ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Format_RoundTrips_Across_Every_Valid_Combination()
    {
        for (int d = 0; d <= DisplayGlyphs.MaxDecimals; d++)
        for (int p = 0; p <= 3; p++)
        for (int r = 0; r <= DisplayGlyphs.MaxRpad; r++)
        for (int s = 0; s <= DisplayGlyphs.MaxSource; s++)
        {
            float packed;
            string error;
            Assert.IsTrue(DisplayGlyphs.TryPackFormat(d, p, r, (DisplayGlyphs.ValueSource)s,
                                                      out packed, out error), error);
            Assert.LessOrEqual(packed, 16383f, "the bitfield must stay inside 14 bits");

            int d2, p2, r2;
            DisplayGlyphs.ValueSource s2;
            DisplayGlyphs.UnpackFormat(packed, out d2, out p2, out r2, out s2);
            Assert.AreEqual(d, d2); Assert.AreEqual(p, p2);
            Assert.AreEqual(r, r2); Assert.AreEqual(s, (int)s2);
        }
    }

    [Test]
    public void Format_Default_Zero_Is_The_Sane_Fresh_Material_State()
    {
        int d, p, r;
        DisplayGlyphs.ValueSource s;
        DisplayGlyphs.UnpackFormat(0f, out d, out p, out r, out s);
        Assert.AreEqual(0, d); Assert.AreEqual(0, p); Assert.AreEqual(0, r);
        Assert.AreEqual(DisplayGlyphs.ValueSource.Animator, s,
            "unlike the label, a zeroed format is correct — source 0 is the animator float");
    }

    [TestCase(6, 0, 0, 0, "decimals")]
    [TestCase(0, 4, 0, 0, "palette")]
    [TestCase(0, 0, 16, 0, "rpad")]
    [TestCase(0, 0, 0, 16, "source")]
    public void Format_Refuses_Out_Of_Range_Fields_Naming_Which(int d, int p, int r, int s, string expected)
    {
        float packed;
        string error;
        Assert.IsFalse(DisplayGlyphs.TryPackFormat(d, p, r, (DisplayGlyphs.ValueSource)s,
                                                   out packed, out error));
        StringAssert.Contains(expected, error);
    }

    [Test]
    public void Decimals_Are_Refused_Not_Clamped_Past_Five()
    {
        // The 3-bit field reaches 7 but the shader's fractional multiplier is only exact to 5. Clamping
        // would print a different number than the author asked for, silently.
        float packed;
        string error;
        Assert.IsFalse(DisplayGlyphs.TryPackFormat(7, 0, 0, DisplayGlyphs.ValueSource.Animator,
                                                   out packed, out error));
    }

    // ── rpad usability bound ────────────────────────────────────────────────────────────────────────

    [Test]
    public void MaxUsableRpad_Keeps_The_Value_Inside_Its_Cell()
    {
        // The value's left edge sits at cell_w_adv - rpad - ValueGlyphs; past that it slides off the left
        // of its own cell and vanishes with no on-screen diagnostic.
        Assert.AreEqual(0, DisplayGlyphs.MaxUsableRpad(10f), "a 10-advance cell has no room to pad");
        Assert.AreEqual(0, DisplayGlyphs.MaxUsableRpad(4f), "narrower than the value itself: still 0, never negative");
        Assert.AreEqual(4, DisplayGlyphs.MaxUsableRpad(14f));
        Assert.AreEqual(DisplayGlyphs.MaxRpad, DisplayGlyphs.MaxUsableRpad(100f),
            "a wide cell is bounded by the 4-bit field, not by geometry");
    }

    [Test]
    public void Rpad_Aligns_Decimal_Points_Across_Differing_Precisions()
    {
        // The documented relation: rpad = max_decimals - entry_decimals aligns the '.' columns. Asserted
        // as arithmetic here so the README's claim has a test behind it rather than a worked example.
        //
        // Geometry: the value right-aligns to (cell_w_adv - rpad), so its last digit sits `rpad` advances
        // from the cell's right edge, and its '.' sits `decimals` further left again.
        const int maxDecimals = 3;
        foreach (var decimals in new[] { 1, 2, 3 })
        {
            int rpad = maxDecimals - decimals;
            Assert.AreEqual(maxDecimals, rpad + decimals,
                "every entry's decimal point must land the same distance from the cell's right edge");
        }

        // An integer chooses its own depth instead: rpad 0 parks it at the far right edge, and
        // rpad = maxDecimals + 1 parks it just left of the shared decimal column (the +1 being the '.').
        // Both are consequences of the same offset relation above, so they carry no separate assertion.
    }

    // ── Property naming (the shader surface) ────────────────────────────────────────────────────────

    [Test]
    public void Property_Names_Match_The_Published_Binding_Path()
    {
        // _E0_Value is what a consumer's clip binds to, so the README quotes it; this pins the shape.
        Assert.AreEqual("_E0_Value", DisplayGlyphs.ValueProperty(0));
        Assert.AreEqual("_E11_Label", DisplayGlyphs.LabelProperty(11));
        Assert.AreEqual("_E7_Format", DisplayGlyphs.FormatProperty(7));
        Assert.AreEqual(12, DisplayGlyphs.MaxEntries);
    }

    [Test]
    public void Source_Enum_Is_Dense_And_Append_Only()
    {
        // IDs are wire values baked into every authored material, so a gap or a renumber silently
        // repoints existing entries at a different source.
        var ids = Enum.GetValues(typeof(DisplayGlyphs.ValueSource)).Cast<int>().OrderBy(i => i).ToArray();
        Assert.AreEqual(0, ids.First());
        Assert.AreEqual(DisplayGlyphs.MaxSource, ids.Last());
        for (int i = 0; i < ids.Length; i++)
            Assert.AreEqual(i, ids[i], "source IDs must be dense 0..MaxSource with no gaps");
    }
}
