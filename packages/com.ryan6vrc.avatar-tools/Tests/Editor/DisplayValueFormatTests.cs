using System;
using System.Globalization;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;

// Tests for the value-field arithmetic the DebugDisplay shader draws.
//
// WHY THIS FILE EXISTS. The shader's dd_value_glyph_at is unreachable by any test — its only evidence
// is render output, and render output only ever exercised values under 1000 at <= 2 decimals, which is
// precisely the band where both of the bugs below are invisible. A council review found them by reading.
// So the arithmetic now lives in DisplayGlyphs as pure integer code, the HLSL mirrors it line for line,
// and this file is what keeps the pair honest.
//
// Both regressions are pinned by name, because both printed a PLAUSIBLE WRONG NUMBER rather than
// failing — the exact class this display exists to not do.
public class DisplayValueFormatTests
{
    // ── The two regressions, pinned ─────────────────────────────────────────────────────────────────

    [Test]
    public void An_Over_Wide_Negative_Does_Not_Silently_Drop_Its_Sign()
    {
        // REGRESSION. The sign is emitted last, at m == digits, so a value needing more columns than the
        // field has pushed it off the end: -1234.5 at 5 decimals needs 11 columns and rendered
        // "1234.50000" — correct magnitude, wrong sign, i.e. the mirror position of a coordinate.
        var rendered = DisplayGlyphs.FormatValue(-1234.5f, 5);
        StringAssert.DoesNotContain("1234.50000", rendered,
            "an over-wide negative must not render as a confident positive");
        StringAssert.Contains("∞", rendered, "it must render the overflow glyph instead");
    }

    [TestCase(-1234.5f, 5)]
    [TestCase(-1234567f, 2)]
    [TestCase(123456.789f, 5)]
    public void A_Value_Too_Wide_For_The_Field_Overflows_Rather_Than_Truncating(float value, int decimals)
    {
        var rendered = DisplayGlyphs.FormatValue(value, decimals);
        StringAssert.Contains("∞", rendered,
            "value " + value + " at " + decimals + "dp does not fit " + DisplayGlyphs.ValueGlyphs +
            " columns and must say so: got '" + rendered + "'");
    }

    [Test]
    public void Pow10_Does_Not_Saturate_Below_The_Uint_Ceiling()
    {
        // REGRESSION. The loop bound was 6, so Pow10 returned 10^6 for every k >= 6. That made
        // DigitCount score four spurious increments past a million and made every place value at index
        // >= 6 read the millions digit.
        Assert.AreEqual(1u, DisplayGlyphs.Pow10(0));
        Assert.AreEqual(1000000u, DisplayGlyphs.Pow10(6));
        Assert.AreEqual(10000000u, DisplayGlyphs.Pow10(7));
        Assert.AreEqual(100000000u, DisplayGlyphs.Pow10(8));
        Assert.AreEqual(1000000000u, DisplayGlyphs.Pow10(9),
            "10^9 is the largest power of ten a uint holds; 10^10 exceeds 2^32");
    }

    [TestCase(0u, 1u)]
    [TestCase(9u, 1u)]
    [TestCase(10u, 2u)]
    [TestCase(999999u, 6u)]
    [TestCase(1000000u, 7u)]
    [TestCase(1234567u, 7u)]      // returned 10 before the Pow10 bound was raised
    [TestCase(16777215u, 8u)]
    public void DigitCount_Is_Right_Across_The_Whole_Representable_Range(uint v, uint expected)
    {
        Assert.AreEqual(expected, DisplayGlyphs.DigitCount(v));
    }

    [Test]
    public void Large_Integers_Print_Their_Own_Digits()
    {
        // 1234567 rendered as "1111234567" before the fix: the leading digits were the millions digit
        // repeated, because every place value past index 5 divided by a saturated 10^6.
        Assert.AreEqual("   1234567", DisplayGlyphs.FormatValue(1234567f, 0));
        Assert.AreEqual("  10000000", DisplayGlyphs.FormatValue(10000000f, 0));
    }

    // ── Agreement with the framework formatter, which is the real oracle ────────────────────────────

    [TestCase(0f, 0)]
    [TestCase(0f, 2)]
    [TestCase(1.25f, 2)]
    [TestCase(-2.5f, 2)]
    [TestCase(0.75f, 2)]
    [TestCase(45f, 1)]
    [TestCase(-20f, 1)]
    [TestCase(1000f, 0)]
    [TestCase(-4.5f, 3)]
    [TestCase(72f, 1)]
    [TestCase(999.99f, 2)]
    [TestCase(-999.99f, 2)]
    public void Matches_String_Format_Where_The_Value_Fits(float value, int decimals)
    {
        var expected = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        Assert.AreEqual(expected, DisplayGlyphs.FormatValue(value, decimals).TrimStart(' '),
            "the shader's arithmetic must agree with the framework formatter wherever the field fits");
    }

    [Test]
    public void Near_The_Exact_Integer_Ceiling_Ours_Is_Right_And_ToString_Is_Not()
    {
        // The framework formatter stops being a valid oracle here, so this case is asserted against the
        // true value instead. `(16777215f).ToString("F0")` returns "16777220" on this runtime — the same
        // 7-significant-digit lossiness that drove the 18-bit label components, showing up in the
        // formatter itself. Our integer arithmetic reads the float exactly.
        Assert.AreEqual("16777215", DisplayGlyphs.FormatValue(16777215f, 0).TrimStart(' '));
        Assert.AreNotEqual(16777215f.ToString("F0", CultureInfo.InvariantCulture),
                           DisplayGlyphs.FormatValue(16777215f, 0).TrimStart(' '),
                           "if these ever agree, the runtime's formatter changed and the note above is stale");
    }

    [Test]
    public void Right_Aligns_Within_The_Field()
    {
        var rendered = DisplayGlyphs.FormatValue(1.25f, 2);
        Assert.AreEqual(DisplayGlyphs.ValueGlyphs, rendered.Length);
        Assert.AreEqual("      1.25", rendered, "values right-align so a grid column lines up");
    }

    // ── Rounding, degenerate values, and the decimals ceiling ───────────────────────────────────────

    [Test]
    public void Fraction_Rounding_Carries_Into_The_Integer_Part()
    {
        // 1.999 at 2dp is 2.00, never 1.100.
        Assert.AreEqual("2.00", DisplayGlyphs.FormatValue(1.999f, 2).TrimStart(' '));
        Assert.AreEqual("-2.00", DisplayGlyphs.FormatValue(-1.999f, 2).TrimStart(' '));
        Assert.AreEqual("10.0", DisplayGlyphs.FormatValue(9.99f, 1).TrimStart(' '));
    }

    [Test]
    public void NaN_And_Infinity_Render_The_Overflow_Glyph_Not_A_Confident_Zero()
    {
        // (uint)NaN is 0, and every comparison against NaN is false, so a naive guard lets a NaN print
        // "0.00" — a lie exactly where a diagnostic belongs.
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var rendered = DisplayGlyphs.FormatValue(bad, 2);
            StringAssert.Contains("∞", rendered, bad + " must render the overflow glyph, got '" + rendered + "'");
            Assert.AreNotEqual("0.00", rendered.TrimStart(' '), bad + " must not print as zero");
        }
    }

    [Test]
    public void A_Magnitude_Past_The_Exact_Integer_Ceiling_Overflows()
    {
        StringAssert.Contains("∞", DisplayGlyphs.FormatValue(16777216f, 0),
            "past 2^24 the integer part is no longer exact, so it must say so rather than guess");
    }

    [Test]
    public void Zero_Prints_A_Leading_Zero_Not_A_Bare_Point()
    {
        Assert.AreEqual("0.00", DisplayGlyphs.FormatValue(0f, 2).TrimStart(' '));
        Assert.AreEqual("0", DisplayGlyphs.FormatValue(0f, 0).TrimStart(' '));
    }

    [Test]
    public void Columns_Past_The_Rendered_Value_Are_Spaces()
    {
        // The shader discards on a space rather than sampling the atlas, so a wrong glyph here would
        // draw ink where the cell should be empty — and fall through to the label region.
        for (int n = 4; n < DisplayGlyphs.ValueGlyphs; n++)
            Assert.AreEqual((uint)DisplayGlyphs.Space, DisplayGlyphs.ValueGlyphAt(1.25f, 2, n),
                "column " + n + " is past '1.25' and must be blank");
    }

    [Test]
    public void Decimals_Above_The_Exact_Ceiling_Are_Clamped_Not_Garbled()
    {
        // TryPackFormat refuses 6 and 7, but the field is 3 bits so a hand-edited material can still
        // carry them. Both sides clamp to 5 rather than computing digits they cannot represent.
        var atSix = DisplayGlyphs.FormatValue(1.5f, 6);
        var atFive = DisplayGlyphs.FormatValue(1.5f, 5);
        Assert.AreEqual(atFive, atSix, "decimals past 5 must clamp to the exact ceiling");
    }
}
