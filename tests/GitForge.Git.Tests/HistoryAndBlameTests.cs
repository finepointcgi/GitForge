using GitForge.Core.Models;

namespace GitForge.Git.Tests;

public class HistoryAndBlameTests
{
    private static async Task CommitAsAsync(TempRepo t, string author, string message)
    {
        await t.GitAsync("add", "-A");
        await t.GitAsync("-c", $"user.name={author}", "-c", $"user.email={author.ToLowerInvariant()}@example.com", "commit", "-q", "-m", message);
    }

    [Fact]
    public async Task FileHistory_FollowsRenames()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("old.txt", "one\n");
        await CommitAsAsync(t, "Alice", "create");
        t.Write("old.txt", "one\ntwo\n");
        await CommitAsAsync(t, "Bob", "extend");
        await t.GitAsync("mv", "old.txt", "new.txt");
        await CommitAsAsync(t, "Alice", "rename");
        t.Write("other.txt", "noise\n");
        await CommitAsAsync(t, "Bob", "unrelated");
        t.Write("new.txt", "one\ntwo\nthree\n");
        await CommitAsAsync(t, "Carol", "extend again");
        var repo = await t.OpenAsync();

        var history = new List<FileHistoryEntry>();
        await foreach (var e in repo.Read.GetFileHistoryAsync("new.txt")) history.Add(e);

        Assert.Equal(["extend again", "rename", "extend", "create"], history.Select(h => h.Commit.Subject));
        Assert.Equal("new.txt", history[0].Path);
        Assert.Equal(("new.txt", "old.txt", FileChangeKind.Renamed), (history[1].Path, history[1].OldPath, history[1].Kind));
        Assert.Equal("old.txt", history[2].Path);
        Assert.Equal(FileChangeKind.Added, history[3].Kind);

        // Each entry resolves to a real diff, even before the rename.
        var diff = await repo.Read.GetCommitDiffAsync(history[2].Commit.Sha, history[2].ToChange(), new GitForge.Core.Diff.DiffOptions());
        Assert.Equal(1, diff!.Additions);
    }

    [Fact]
    public async Task Blame_AttributesEachLine_IncludingUncommittedWork()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("b.txt", "alpha\nbeta\n");
        await CommitAsAsync(t, "Alice", "first");
        t.Write("b.txt", "alpha\nbeta\ngamma\n");
        await CommitAsAsync(t, "Bob", "add gamma");
        t.Write("b.txt", "alpha\nbeta\ngamma\nwork in progress\n");
        var repo = await t.OpenAsync();

        var blame = await repo.Read.GetBlameAsync("b.txt", null);

        Assert.Equal(4, blame.Lines.Count);
        Assert.Equal(["alpha", "beta", "gamma", "work in progress"], blame.Lines.Select(l => l.Text));
        Assert.Equal("Alice", blame.Commits[blame.Lines[0].Sha].Author);
        Assert.Equal("first", blame.Commits[blame.Lines[1].Sha].Summary);
        Assert.Equal("Bob", blame.Commits[blame.Lines[2].Sha].Author);
        Assert.Equal("bob@example.com", blame.Commits[blame.Lines[2].Sha].AuthorEmail);
        Assert.True(blame.Commits[blame.Lines[3].Sha].IsUncommitted);
        Assert.Equal([1, 2, 3, 4], blame.Lines.Select(l => l.LineNumber));

        var atFirst = await repo.Read.GetBlameAsync("b.txt", "HEAD~1");
        Assert.Equal(2, atFirst.Lines.Count);
    }
}
