using GitForge.Core.Abstractions;
using GitForge.Core.Diff;
using GitForge.Core.Models;

namespace GitForge.Git.Tests;

public class BranchCompareTests
{
    [Fact]
    public async Task BranchChanges_SinceMergeBase_WithAndWithoutWorkingTree()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("shared.txt", "base\n");
        await t.CommitAllAsync("base");
        await t.GitAsync("checkout", "-q", "-b", "feature");
        t.Write("feature.txt", "new\n");
        t.Write("shared.txt", "base\nfeature line\n");
        await t.CommitAllAsync("feature work");
        await t.GitAsync("checkout", "-q", "main");
        t.Write("main-only.txt", "main moved on\n");
        await t.CommitAllAsync("main work");
        await t.GitAsync("checkout", "-q", "feature");
        t.Write("feature.txt", "new\nuncommitted\n");
        var repo = await t.OpenAsync();

        var mergeBase = await repo.Read.GetMergeBaseAsync("main", "HEAD");
        Assert.Equal((await t.GitAsync("rev-parse", "main~1")).Trim(), mergeBase);

        // Committed changes only: main's later commit must not appear.
        var committed = await repo.Read.GetChangesBetweenAsync(mergeBase!, "HEAD");
        Assert.Equal(["feature.txt", "shared.txt"], committed.Select(c => c.Path));
        Assert.Equal(FileChangeKind.Added, committed[0].Kind);

        var commits = new List<string>();
        await foreach (var c in repo.Read.GetHistoryAsync(new HistoryQuery { StartRef = $"{mergeBase}..HEAD", AllRefs = false })) commits.Add(c.Subject);
        Assert.Equal(["feature work"], commits);

        // Including the working tree picks up the uncommitted line.
        var withWorking = await repo.Read.GetDiffBetweenAsync(mergeBase!, null, committed[0], new DiffOptions());
        Assert.Equal(2, withWorking!.Additions);
        var committedOnly = await repo.Read.GetDiffBetweenAsync(mergeBase!, "HEAD", committed[0], new DiffOptions());
        Assert.Equal(1, committedOnly!.Additions);
    }

    [Fact]
    public async Task UnrelatedHistories_HaveNoMergeBase()
    {
        await using var t = await TempRepo.CreateAsync();
        t.Write("a.txt", "a\n");
        await t.CommitAllAsync("a");
        await t.GitAsync("checkout", "-q", "--orphan", "other");
        t.Write("b.txt", "b\n");
        await t.CommitAllAsync("b");
        var repo = await t.OpenAsync();

        Assert.Null(await repo.Read.GetMergeBaseAsync("main", "other"));
        await Assert.ThrowsAsync<ArgumentException>(() => repo.Read.GetMergeBaseAsync("--all", "main"));
    }

    /// <summary>"Remote deleted" branches: detected after a pruning fetch, and the safe delete keeps unmerged work.</summary>
    [Fact]
    public async Task GoneUpstream_IsDetected_AndSafeDeleteKeepsUnmergedWork()
    {
        await using var server = await TempRepo.CreateAsync();
        server.Write("a.txt", "a\n");
        await server.CommitAllAsync("base");
        await server.GitAsync("branch", "-q", "merged");
        await server.GitAsync("checkout", "-q", "-b", "unmerged");
        server.Write("b.txt", "b\n");
        await server.CommitAllAsync("extra");
        await server.GitAsync("checkout", "-q", "main");

        await using var local = await TempRepo.CreateAsync();
        await local.GitAsync("remote", "add", "origin", server.Path);
        await local.GitAsync("fetch", "-q", "origin");
        await local.GitAsync("checkout", "-q", "-B", "main", "origin/main");
        await local.GitAsync("branch", "-q", "--track", "merged", "origin/merged");
        await local.GitAsync("branch", "-q", "--track", "unmerged", "origin/unmerged");

        await server.GitAsync("branch", "-q", "-D", "merged", "unmerged"); // e.g., deleted after the pull requests merged
        await local.GitAsync("fetch", "-q", "--prune", "origin");
        var repo = await local.OpenAsync();

        var branches = (await repo.Read.GetSnapshotAsync()).LocalBranches.ToDictionary(b => b.Name);
        Assert.True(branches["merged"].IsUpstreamGone);
        Assert.True(branches["unmerged"].IsUpstreamGone);
        Assert.False(branches["main"].IsUpstreamGone);

        await repo.Branches.DeleteAsync("merged", force: false);
        var refused = await Assert.ThrowsAsync<GitCommandException>(() => repo.Branches.DeleteAsync("unmerged", force: false));
        Assert.Contains("not fully merged", refused.GitMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["main", "unmerged"], (await repo.Read.GetSnapshotAsync()).LocalBranches.Select(b => b.Name).Order());
    }
}
