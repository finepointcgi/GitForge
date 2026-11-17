using GitForge.Core.Diff;
using GitForge.UI.Services;

namespace GitForge.UI.Tests;

public class SyntaxHighlighterTests
{
    private static readonly SyntaxHighlighter Highlighter = new();

    [Fact]
    public void CSharp_KeywordsAndStrings_GetDistinctColors()
    {
        var tok = Highlighter.CreateTokenizer("Program.cs", dark: true);
        Assert.NotNull(tok);

        const string line = "public static string Name = \"value\";";
        var spans = tok!.Next(line);
        string? ColorOf(string text) => spans.FirstOrDefault(s => line.Substring(s.Start, s.Length) == text).Color;

        // TextMate splits quotes from string contents; Dark+ colors keywords #569CD6 and strings #CE9178.
        Assert.Equal("#569CD6", ColorOf("public"), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("#CE9178", ColorOf("value"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultiLineComment_StateCarriesAcrossLines()
    {
        var tok = Highlighter.CreateTokenizer("a.cs", dark: true)!;
        var opening = tok.Next("/* start of a comment");
        var inside = tok.Next("int x = 1; still comment */");
        var commentColor = opening[0].Color;
        Assert.Equal(commentColor, inside.First(s => s.Start == 0).Color); // "int" is colored as a comment here
    }

    [Fact]
    public void UnknownExtension_IsPlain() => Assert.Null(Highlighter.CreateTokenizer("notes.unknownext", dark: true));

    [Fact]
    public void ToHtml_EncodesEverything()
    {
        const string line = "<script>alert('x')</script> & \"q\"";
        var html = SyntaxHighlighter.ToHtml(line, [new HighlightSpan(0, 8, "#ff0000", false, false), new HighlightSpan(8, 5, "red;background:url(x)", true, false)]);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", html, StringComparison.Ordinal); // invalid color rejected, style not injected
        Assert.Contains("font-weight:600", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DiffHighlighter_UsesOldSideForDeletions()
    {
        var diff = UnifiedDiffParser.ParseSingle(
            "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1,2 +1,2 @@\n /* comment\n-old line */\n+new line */\n")!;
        var hl = new DiffHighlighter(Highlighter, diff, dark: true);

        var deleted = hl.Html(0, 1);
        var added = hl.Html(0, 2);
        Assert.Contains("old line", deleted, StringComparison.Ordinal);
        Assert.Contains("new line", added, StringComparison.Ordinal);
        Assert.Contains("<span", added, StringComparison.Ordinal); // tokenized within the comment begun by the context line
    }
}

public class SmallParsersTests
{
    [Fact]
    public void CommandLineSplitter_HandlesQuotes() =>
        Assert.Equal(["log", "--format=%h %s", "a b", "c"], CommandLineSplitter.Split("log \"--format=%h %s\" 'a b' c"));

    [Fact]
    public void HistoryFilter_ParsesTokens()
    {
        var f = HistoryFilter.Parse("fix author:alice path:src/app.cs since:2025-01-01");
        Assert.Equal(("fix", "alice", "src/app.cs"), (f.Text, f.Author, f.Path));
        Assert.NotNull(f.Since);
    }
}
