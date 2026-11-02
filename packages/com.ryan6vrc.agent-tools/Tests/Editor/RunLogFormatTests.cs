using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// RunLogFormat.Cell: a value read off an asset can carry characters that would break a table row or column or
// inject a heading into the RunLog.
public class RunLogFormatTests
{
    [Test]
    public void Cell_nullOrEmpty_returnsEmpty()
    {
        Assert.AreEqual("", RunLogFormat.Cell(null));
        Assert.AreEqual("", RunLogFormat.Cell(""));
    }

    // The backtick deliberately passes through: most call sites wrap the value in a code span, where backslash
    // escapes are inert, so "\`" would only add a stray char. Structural safety comes from the row/column
    // handling, so a passed-through backtick stays cosmetic.
    [Test]
    public void Cell_escapesRowAndColumnBreakers()
    {
        string encoded = RunLogFormat.Cell("Evil\r\n\n## injected | x\ty");
        StringAssert.DoesNotContain("\n", encoded);
        StringAssert.DoesNotContain("\r", encoded);
        StringAssert.DoesNotContain("\t", encoded);
        StringAssert.Contains("\\n", encoded); // visible escape instead
        StringAssert.Contains("\\|", encoded); // pipe escaped
        Assert.AreEqual("a`b", RunLogFormat.Cell("a`b")); // backtick passes through untouched
    }

    // Without escaping the backslash, "a\|b" becomes "a\\|b", where GFM reads "\\" as an escaped backslash and
    // leaves the pipe BARE, a column break.
    [Test]
    public void Cell_escapesBackslashBeforePipe()
    {
        string bp = RunLogFormat.Cell("a\\|b");
        int pipe = bp.IndexOf('|');
        int run = 0;
        for (int i = pipe - 1; i >= 0 && bp[i] == '\\'; i--) run++;
        Assert.IsTrue(run % 2 == 1, "pipe must be escaped (odd backslash run precedes it), was '" + bp + "'");
    }

    // U+2028 / U+2029 are line breaks that char.IsControl does NOT catch.
    [Test]
    public void Cell_neutralizesUnicodeLineSeparators()
    {
        string sep = "a" + (char)0x2028 + "b" + (char)0x2029 + "c";
        string encoded = RunLogFormat.Cell(sep);
        Assert.AreEqual("a b c", encoded);
    }
}
