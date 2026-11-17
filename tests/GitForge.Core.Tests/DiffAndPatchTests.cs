using GitForge.Core.Diff;

namespace GitForge.Core.Tests;

public class DiffAndPatchTests
{
    private const string TwoHunkDiff =
        "diff --git a/src/app.cs b/src/app.cs\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/src/app.cs\n" +
        "+++ b/src/app.cs\n" +
        "@@ -1,4 +1,4 @@ class App\n" +
        " line1\n" +
        "-line2\n" +
        "+line2 changed\n" +
        " line3\n" +
        " line4\n" +
        "@@ -10,3 +10,5 @@\n" +
        " line10\n" +
        "+added A\n" +
        "+added B\n" +
        " line11\n" +
        " line12\n";

    [Fact]
    public void Parser_ReadsHeaderHunksAndLineNumbers()
    {
        var diff = UnifiedDiffParser.ParseSingle(TwoHunkDiff)!;

        Assert.Equal("src/app.cs", diff.OldPath);
        Assert.Equal("src/app.cs", diff.NewPath);
        Assert.Equal(4, diff.HeaderLines.Count);
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Equal("class App", diff.Hunks[0].Section);
        Assert.Equal(3, diff.Additions);
        Assert.Equal(1, diff.Deletions);
        Assert.True(diff.SupportsPartialStaging);

        var added = diff.Hunks[1].Lines[1];
        Assert.Equal(DiffLineKind.Added, added.Kind);
        Assert.Equal(11, added.NewLineNumber);
        Assert.Null(added.OldLineNumber);
    }

    [Fact]
    public void Parser_KeepsCarriageReturns_AndNoNewlineMarker()
    {
        const string text =
            "diff --git a/w.txt b/w.txt\n--- a/w.txt\n+++ b/w.txt\n" +
            "@@ -1,2 +1,2 @@\n a\r\n-b\r\n\\ No newline at end of file\n+c\r\n\\ No newline at end of file\n";
        var diff = UnifiedDiffParser.ParseSingle(text)!;
        var lines = diff.Hunks[0].Lines;

        Assert.Equal("a\r", lines[0].Text);
        Assert.Equal("a", lines[0].DisplayText);
        Assert.True(lines[1].NoNewlineAtEnd);
        Assert.True(lines[2].NoNewlineAtEnd);
    }

    [Fact]
    public void Parser_HandlesNewFilesBinaryAndQuotedPaths()
    {
        const string text =
            "diff --git a/new.txt b/new.txt\nnew file mode 100644\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+hi\n" +
            "diff --git a/img.png b/img.png\nBinary files a/img.png and b/img.png differ\n" +
            "diff --git \"a/caf\\303\\251.txt\" \"b/caf\\303\\251.txt\"\n--- \"a/caf\\303\\251.txt\"\n+++ \"b/caf\\303\\251.txt\"\n@@ -1 +1 @@\n-x\n+y\n";
        var files = UnifiedDiffParser.Parse(text);

        Assert.Equal(3, files.Count);
        Assert.True(files[0].IsNewFile);
        Assert.False(files[0].SupportsPartialStaging);
        Assert.True(files[1].IsBinary);
        Assert.Equal("café.txt", files[2].Path);
    }

    [Fact]
    public void Stage_SingleHunk_EmitsOnlyThatHunk()
    {
        var diff = UnifiedDiffParser.ParseSingle(TwoHunkDiff)!;
        var patch = PatchBuilder.Build(diff, PatchBuilder.SelectHunk(diff.Hunks[1]), PatchAction.Stage)!;

        Assert.Contains("@@ -10,3 +10,5 @@\n", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("line2 changed", patch, StringComparison.Ordinal);
        Assert.StartsWith("diff --git a/src/app.cs b/src/app.cs\n", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_OneAddedLine_DropsUnselectedAdditions()
    {
        var diff = UnifiedDiffParser.ParseSingle(TwoHunkDiff)!;
        var patch = PatchBuilder.Build(diff, new HashSet<LineRef> { new(1, 1) }, PatchAction.Stage)!;

        Assert.Contains("@@ -10,3 +10,4 @@\n line10\n+added A\n line11\n line12\n", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("added B", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage_SecondHunkAfterPartialFirst_ShiftsNewStart()
    {
        var diff = UnifiedDiffParser.ParseSingle(TwoHunkDiff)!;
        // First hunk: stage only the deletion (net -1 line); second hunk: both additions (+2).
        var selection = new HashSet<LineRef> { new(0, 1), new(1, 1), new(1, 2) };
        var patch = PatchBuilder.Build(diff, selection, PatchAction.Stage)!;

        Assert.Contains("@@ -1,4 +1,3 @@\n line1\n-line2\n line3\n line4\n", patch, StringComparison.Ordinal);
        Assert.Contains("@@ -10,3 +9,5 @@", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void Unstage_OneDeletion_KeepsUnselectedAdditionsAsContext()
    {
        var diff = UnifiedDiffParser.ParseSingle(TwoHunkDiff)!;
        var patch = PatchBuilder.Build(diff, new HashSet<LineRef> { new(0, 1) }, PatchAction.Unstage)!;

        // Reverse patches match the post-image: unselected '+' becomes context, unselected '-' disappears.
        // Old side gains line2 back; new side (current index) is unchanged: 5 old lines, 4 new lines.
        Assert.Contains("@@ -1,5 +1,4 @@\n line1\n-line2\n line2 changed\n line3\n line4\n", patch, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySelection_ReturnsNull()
    {
        var diff = UnifiedDiffParser.ParseSingle(TwoHunkDiff)!;
        Assert.Null(PatchBuilder.Build(diff, new HashSet<LineRef>(), PatchAction.Stage));
    }

    [Fact]
    public void SideBySide_PairsDeletionsWithAdditions()
    {
        var diff = UnifiedDiffParser.ParseSingle(TwoHunkDiff)!;
        var rows = DiffLayout.SideBySide(diff);

        var changed = rows.Single(r => r.Left?.Kind == DiffLineKind.Deleted);
        Assert.Equal("line2 changed", changed.Right!.Text);
        Assert.Equal(2, rows.Count(r => r.IsHunkHeader));
        Assert.Equal(2, rows.Count(r => r.Left is null && r.Right?.Kind == DiffLineKind.Added));
        Assert.Equal(diff.Hunks.Sum(h => h.Lines.Count) + 2, DiffLayout.Inline(diff).Count);
    }
}
