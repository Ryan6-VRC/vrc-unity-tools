using NUnit.Framework;
using Ryan6Vrc.AgentTools.Editor;

// RunLogFormat.Cell — the one hardened markdown-cell encoder the report tools converge on. A value read off
// an asset can carry structural characters (CR/LF, pipe, backslash, backtick, Unicode line separators) that
// would break the table row/column or inject a heading into the LLM-consumed RunLog. These pin that every
// such character is neutralized, most importantly the backslash-before-pipe case a naive `Replace("|","\|")`
// silently mishandles.
public class RunLogFormatTests
{
    [Test]
    public void Cell_nullOrEmpty_returnsEmpty()
    {
        Assert.AreEqual("", RunLogFormat.Cell(null));
        Assert.AreEqual("", RunLogFormat.Cell(""));
    }

    // CR/LF/tab must not survive as physical control characters (they would break the row) and the pipe must
    // be escaped (column break). A crafted name with a newline + "## injected" must not leave a physical
    // heading line.
    [Test]
    public void Cell_escapesRowAndColumnBreakers()
    {
        string encoded = RunLogFormat.Cell("Evil\r\n\n## injected | x\ty");
        StringAssert.DoesNotContain("\n", encoded);
        StringAssert.DoesNotContain("\r", encoded);
        StringAssert.DoesNotContain("\t", encoded);
        StringAssert.Contains("\\n", encoded); // visible escape instead
        StringAssert.Contains("\\|", encoded); // pipe escaped
    }

    // Backticks are deliberately NOT escaped: most call sites wrap the value in a code span, where backslash
    // escapes are inert — "\`" would neither keep the span closed nor render cleanly, just add a stray char.
    // Structural safety comes from the row/column handling, so a passed-through backtick stays cosmetic.
    [Test]
    public void Cell_leavesBacktickUnescaped()
    {
        Assert.AreEqual("a`b", RunLogFormat.Cell("a`b"));
    }

    // The defect this shared encoder exists to fix: a backslash immediately before a pipe. Without escaping the
    // backslash, "a\|b" → "a\\|b", where GFM reads "\\" as an escaped backslash and the pipe is left BARE (a
    // column break). The pipe must stay escaped — an ODD run of backslashes must precede it.
    [Test]
    public void Cell_escapesBackslashBeforePipe()
    {
        string bp = RunLogFormat.Cell("a\\|b");
        int pipe = bp.IndexOf('|');
        int run = 0;
        for (int i = pipe - 1; i >= 0 && bp[i] == '\\'; i--) run++;
        Assert.IsTrue(run % 2 == 1, "pipe must be escaped (odd backslash run precedes it), was '" + bp + "'");
    }

    // U+2028 / U+2029 (Zl/Zp line/paragraph separators) are line breaks that char.IsControl does NOT catch;
    // they must still collapse to a space so nothing survives that reads as a line break.
    [Test]
    public void Cell_neutralizesUnicodeLineSeparators()
    {
        string sep = "a" + (char)0x2028 + "b" + (char)0x2029 + "c";
        string encoded = RunLogFormat.Cell(sep);
        Assert.AreEqual("a b c", encoded);
    }
}
