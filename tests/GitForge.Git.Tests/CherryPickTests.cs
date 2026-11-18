namespace GitForge.Git.Tests;

/// <summary>Cherry-picking several selected commits at once.</summary>
public class CherryPickTests
{
    [Fact]
    public async Task CherryPick_SeveralCommits_AppliesThemInTheGivenOrder()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("f.txt", "base\n");
        await t.CommitAllAsync("base");
        await t.GitAsync("checkout", "-q", "-b", "topic");
        t.Write("a.txt", "a\n");
        await t.CommitAllAsync("add a");
        t.Write("a.txt", "a\nb\n");
        await t.CommitAllAsync("extend a");
        t.Write("c.txt", "c\n");
        await t.CommitAllAsync("add c");
        var oldestFirst = (await t.GitAsync("rev-list", "--reverse", "main..topic")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await t.GitAsync("checkout", "-q", "main");

        var repo = await t.OpenAsync();
        await repo.History.CherryPickAsync(oldestFirst);

        Assert.Equal("add c\nextend a\nadd a\nbase\n", await t.GitAsync("log", "--format=%s", "main"));
        Assert.Equal("a\nb\n", File.ReadAllText(Path.Combine(t.Path, "a.txt")));
    }

    /// <summary>Two commits on "topic" that change shared.txt, and an uncommitted edit to it on main.</summary>
    private static async Task<(TempRepo Repo, string[] Shas)> RefusedSetupAsync()
    {
        var t = await TempRepo.CreateAsync();
        t.Write("shared.txt", "one\n");
        await t.CommitAllAsync("base");
        await t.GitAsync("checkout", "-q", "-b", "topic");
        t.Write("shared.txt", "two\n");
        await t.CommitAllAsync("two");
        t.Write("shared.txt", "three\n");
        await t.CommitAllAsync("three");
        var shas = (await t.GitAsync("rev-list", "--reverse", "main..topic")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await t.GitAsync("checkout", "-q", "main");
        t.Write("shared.txt", "local edit\n");
        return (t, shas);
    }

    [Fact]
    public async Task CherryPick_RefusedByLocalChanges_LeavesNothingInProgress()
    {
        var (t, shas) = await RefusedSetupAsync();
        await using var _ = t;
        var repo = await t.OpenAsync();

        await Assert.ThrowsAsync<GitForge.Core.Abstractions.GitCommandException>(() => repo.History.CherryPickAsync(shas));

        Assert.Equal(Core.Models.RepositoryOperation.None, (await repo.Read.GetSnapshotAsync()).Operation);
        Assert.False(Directory.Exists(Path.Combine(t.Path, ".git", "sequencer")));
        Assert.Equal("local edit\n", File.ReadAllText(Path.Combine(t.Path, "shared.txt")));
    }

    [Fact]
    public async Task Snapshot_ReportsAStalledSequence_AndAbortClearsIt()
    {
        var (t, shas) = await RefusedSetupAsync();
        await using var _ = t;
        await Assert.ThrowsAsync<GitForge.Core.Abstractions.GitCommandException>(() => t.GitAsync(["cherry-pick", .. shas])); // plain git leaves .git/sequencer
        var repo = await t.OpenAsync();

        Assert.Equal(Core.Models.RepositoryOperation.CherryPick, (await repo.Read.GetSnapshotAsync()).Operation);
        Assert.True((await repo.History.AbortAsync()).Succeeded);
        Assert.Equal(Core.Models.RepositoryOperation.None, (await repo.Read.GetSnapshotAsync()).Operation);
    }
}
