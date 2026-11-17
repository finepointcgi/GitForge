using GitForge.Core.Models;
using GitForge.Git.Undo;

namespace GitForge.Git.Tests;

public class UndoTests
{
    [Fact]
    public async Task UndoCommit_KeepsChangesStaged_AndRedoRestoresCommit()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "1\n");
        await t.CommitAllAsync("init");
        var repo = await t.OpenAsync();
        var undo = new UndoManager();

        t.Write("a.txt", "2\n");
        await repo.Index.StageAsync(["a.txt"]);
        undo.Record(await UndoableOperations.CommitAsync(repo, new CommitRequest("second")));
        var committed = (await t.GitAsync("rev-parse", "HEAD")).Trim();
        Assert.Equal("commit \"second\"", undo.UndoDescription);

        await undo.UndoAsync(repo);
        Assert.Equal("init", (await t.GitAsync("log", "-1", "--format=%s")).Trim());
        Assert.Contains("a.txt", (await repo.Read.GetStatusAsync()).Staged.Select(f => f.Path));
        Assert.True(undo.CanRedo);

        await undo.RedoAsync(repo);
        Assert.Equal(committed, (await t.GitAsync("rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task UndoDeleteBranch_RecreatesItAtTheSameCommit()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "1\n");
        await t.CommitAllAsync("init");
        await t.GitAsync("branch", "topic");
        var tip = (await t.GitAsync("rev-parse", "topic")).Trim();
        var repo = await t.OpenAsync();
        var undo = new UndoManager();

        undo.Record(await UndoableOperations.DeleteBranchAsync(repo, "topic", force: true));
        Assert.DoesNotContain((await repo.Read.GetSnapshotAsync()).LocalBranches, b => b.Name == "topic");

        await undo.UndoAsync(repo);
        Assert.Equal(tip, (await t.GitAsync("rev-parse", "topic")).Trim());
    }

    [Fact]
    public async Task UndoDiscard_RestoresTrackedAndUntrackedFiles()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "committed\n");
        await t.CommitAllAsync("init");
        t.Write("a.txt", "precious edit\n");
        t.Write("new.txt", "untracked work\n");
        var repo = await t.OpenAsync();
        var undo = new UndoManager();

        var files = (await repo.Read.GetStatusAsync()).Unstaged;
        undo.Record(await UndoableOperations.DiscardAsync(repo, () => repo.Index.DiscardAsync(files), files.Select(f => f.Path).ToList()));
        Assert.True((await repo.Read.GetStatusAsync()).IsClean);

        await undo.UndoAsync(repo);
        Assert.Equal("precious edit\n", File.ReadAllText(Path.Combine(t.Path, "a.txt")));
        Assert.Equal("untracked work\n", File.ReadAllText(Path.Combine(t.Path, "new.txt")));
    }

    [Fact]
    public async Task Undo_RefusesWhenHeadMoved()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "1\n");
        await t.CommitAllAsync("init");
        var repo = await t.OpenAsync();
        var undo = new UndoManager();

        t.Write("a.txt", "2\n");
        await repo.Index.StageAsync(["a.txt"]);
        undo.Record(await UndoableOperations.CommitAsync(repo, new CommitRequest("second")));
        t.Write("a.txt", "3\n");
        await t.CommitAllAsync("third, made outside the app");

        await Assert.ThrowsAsync<InvalidOperationException>(() => undo.UndoAsync(repo));
        Assert.Equal("third, made outside the app", (await t.GitAsync("log", "-1", "--format=%s")).Trim());
    }

    [Fact]
    public async Task History_CanBeLimitedToSelectedRefs()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "1\n");
        await t.CommitAllAsync("init");
        await t.GitAsync("checkout", "-q", "-b", "side");
        t.Write("b.txt", "1\n");
        await t.CommitAllAsync("side work");
        await t.GitAsync("checkout", "-q", "main");
        var repo = await t.OpenAsync();

        var all = new List<string>();
        await foreach (var c in repo.Read.GetHistoryAsync(new HistoryQuery())) all.Add(c.Subject);
        var mainOnly = new List<string>();
        await foreach (var c in repo.Read.GetHistoryAsync(new HistoryQuery { Refs = ["main"] })) mainOnly.Add(c.Subject);

        Assert.Contains("side work", all);
        Assert.Equal(["init"], mainOnly);
    }
}
