using GitForge.Core.Merge;

namespace GitForge.Core.Tests;

public class MergeDocumentTests
{
    private const string Diff3 =
        "a\r\n" +
        "<<<<<<< mine\r\n" +
        "mine 1\r\n" +
        "mine 2\r\n" +
        "||||||| base\r\n" +
        "base\r\n" +
        "=======\r\n" +
        "theirs\r\n" +
        ">>>>>>> theirs\r\n" +
        "b\r\n" +
        "<<<<<<< mine\r\n" +
        "x\r\n" +
        "||||||| base\r\n" +
        "=======\r\n" +
        "y\r\n" +
        ">>>>>>> theirs\r\n";

    [Fact]
    public void Parses_Diff3_Conflicts_And_CommonText()
    {
        var doc = MergeDocument.Parse(Diff3);

        Assert.Equal(2, doc.Conflicts.Count);
        Assert.Equal(["mine 1", "mine 2"], doc.Conflicts[0].Ours);
        Assert.Equal(["base"], doc.Conflicts[0].Base);
        Assert.Equal(["theirs"], doc.Conflicts[0].Theirs);
        Assert.Empty(doc.Conflicts[1].Base);
        Assert.Equal("\r\n", doc.NewLine);
        Assert.False(doc.IsFullyResolved);
        Assert.Throws<InvalidOperationException>(() => doc.Render());
    }

    [Fact]
    public void Renders_Choices_With_Original_LineEndings()
    {
        var doc = MergeDocument.Parse(Diff3);
        doc.Conflicts[0].Resolution = ConflictResolution.TheirsThenOurs;
        doc.Conflicts[1].Resolution = ConflictResolution.Custom;
        doc.Conflicts[1].CustomText = "hand\nedited\n";

        Assert.Equal("a\r\ntheirs\r\nmine 1\r\nmine 2\r\nb\r\nhand\r\nedited\r\n", doc.Render());
        Assert.Equal(2, doc.ResolvedCount);
    }

    [Fact]
    public void ResolveAll_And_EmptySides()
    {
        var doc = MergeDocument.Parse("<<<<<<< mine\n=======\nonly theirs\n>>>>>>> theirs\n");
        doc.ResolveAll(ConflictResolution.Ours);
        Assert.Equal(string.Empty, doc.Render());
        doc.ResolveAll(ConflictResolution.Theirs);
        Assert.Equal("only theirs\n", doc.Render());
    }

    [Fact]
    public void LookalikeLines_AreNotMarkers()
    {
        var doc = MergeDocument.Parse("<<<<<<<<< not a marker\n======== nor this\n");
        Assert.Empty(doc.Conflicts);
        Assert.Throws<FormatException>(() => MergeDocument.Parse("<<<<<<< mine\nx\n"));
    }
}
